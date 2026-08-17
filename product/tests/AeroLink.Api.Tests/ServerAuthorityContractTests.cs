using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ServerAuthorityContractTests
{
    [Fact]
    public async Task Legacy_identity_fields_are_ignored_and_cannot_spoof_change_author()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        Guid projectId;
        Guid releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Authority Contract Program", $"AUTH{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            projectId = project.Id;
            releaseId = release.Id;
        }

        using var response = await client.PostAsJsonAsync("/api/change-requests", new
        {
            projectId,
            targetReleaseId = releaseId,
            title = "Server authoritative authorship",
            problem = "A caller may try to provide a different author.",
            analysis = "Authenticated context is the only authority source.",
            solution = "Ignore legacy attribution properties.",
            type = "System",
            authorId = "spoofed.author",
            actorId = "spoofed.actor",
            recordedBy = "spoofed.recorder"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", created.GetProperty("authorId").GetString());

        using var scope2 = factory.Services.CreateScope();
        var verificationDb = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var stored = await verificationDb.SystemChangeRequests.FindAsync(created.GetProperty("id").GetGuid());
        Assert.NotNull(stored);
        Assert.Equal("admin", stored.AuthorId);
    }

    [Fact]
    public void Standard_diagnostics_contains_no_human_login_or_committed_password()
    {
        var productRoot = FindProductRoot();
        var script = File.ReadAllText(Path.Combine(productRoot, "scripts", "Get-AeroLinkDiagnostics.ps1"));

        Assert.DoesNotContain("/api/auth/login", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AeroLink!2026", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Password", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/health/live", script, StringComparison.Ordinal);
        Assert.Contains("/health/ready", script, StringComparison.Ordinal);
        Assert.Contains("CreatesBrowserSession = $false", script, StringComparison.Ordinal);
    }

    private static string FindProductRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate the product root.");
    }
}
