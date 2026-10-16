using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroLink.Api.Tests;

/// <summary>
/// Primary hosted owner for the B2 credential boundary: the endpoint session is deliberately ordinary, while the
/// signature proof follows the account's current Windows link and is persisted in the read projection.
/// </summary>
public sealed class WindowsSignatureApiTests
{
    [Fact]
    public async Task Linked_windows_account_signs_with_windows_password_and_read_projection_preserves_method()
    {
        var validator = new FakeWindowsCredentialValidator("CONTOSO\\linked.user", "S-1-5-21-10-20-30-1001", "Windows-Sign!2026");
        using var baseFactory = new AeroLinkApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWindowsCredentialValidator>();
            services.AddSingleton<IWindowsCredentialValidator>(validator);
        }));
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        Guid releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Windows signature program", $"WS{Guid.NewGuid():N}"[..8]);
            var project = new ProjectRecord(program.Id, "Windows signature project", "Widget");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var account = new UserAccount("linked.user", "Linked User", "linked@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            account.LinkWindows(validator.Sid, validator.Principal);
            db.AddRange(program, project, release, new ProjectFeatureSet(project.Id,
                ProjectFeature.TeamWork | ProjectFeature.ProblemReports, "test.setup", now), account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    account.Id, "test.setup", now));
            await db.SaveChangesAsync();
            releaseId = release.Id;
        }

        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "linked.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        const string reason = "The controlled build is ready for field delivery.";
        using var response = await client.PostAsJsonAsync($"/api/releases/{releaseId}/release-without-readiness",
            new { reason, password = validator.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var signature = db.ElectronicSignatures.Single(x => x.ArtifactId == releaseId);
            Assert.Equal("windows-password", signature.AuthenticationMethod);
        }
        using var projection = await client.GetAsync($"/api/signatures?artifactId={releaseId}");
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        var rows = await projection.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal("windows-password", Assert.Single(rows!).GetProperty("authenticationMethod").GetString());
    }

    [Fact]
    public async Task Unlinked_local_account_keeps_password_signature_and_wrong_proof_writes_no_signature()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        Guid releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Local signature program", $"LS{Guid.NewGuid():N}"[..8]);
            var project = new ProjectRecord(program.Id, "Local signature project", "Widget");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var account = new UserAccount("local.user", "Local User", "local@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, new ProjectFeatureSet(project.Id,
                ProjectFeature.TeamWork | ProjectFeature.ProblemReports, "test.setup", now), account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    account.Id, "test.setup", now));
            await db.SaveChangesAsync();
            releaseId = release.Id;
        }
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "local.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        const string reason = "The local breakglass path is explicitly authorized.";
        using (var wrong = await client.PostAsJsonAsync($"/api/releases/{releaseId}/release-without-readiness",
            new { reason, password = "wrong-signature-password" }))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(db.ElectronicSignatures.Where(x => x.ArtifactId == releaseId));
        }
        using var response = await client.PostAsJsonAsync($"/api/releases/{releaseId}/release-without-readiness",
            new { reason, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal("password", db.ElectronicSignatures.Single(x => x.ArtifactId == releaseId).AuthenticationMethod);
        }
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Administrator",
                email = "admin@example.test",
                password = AeroLinkApiFactory.AdministratorPassword,
            }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = "admin",
            password = AeroLinkApiFactory.AdministratorPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed class FakeWindowsCredentialValidator(string principal, string sid, string password)
        : IWindowsCredentialValidator
    {
        public string Principal { get; } = principal;
        public string Sid { get; } = sid;
        public string Password { get; } = password;

        public Task<bool> ValidateAsync(string suppliedPrincipal, string expectedSid, string suppliedPassword,
            CancellationToken ct) => Task.FromResult(
                string.Equals(suppliedPrincipal, Principal, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expectedSid, Sid, StringComparison.OrdinalIgnoreCase)
                && suppliedPassword == Password);
    }
}
