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

}
