using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The checkpoint counted requirements, revisions and attachments, checked that each attachment file
/// existed, and hashed those totals. It never recomputed a stored digest, so an altered attachment left a
/// Healthy checkpoint behind for as long as the file was still there and the counts still matched — the word
/// "integrity" over a measurement that had never read a byte of controlled content.
/// </summary>
public sealed class IntegrityCheckpointApiTests
{
    private const string Member = "integrity.engineer";

    private static async Task<(Guid ProjectId, string StorageKey)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Integrity Program", "ITG");
        var project = new ProjectRecord(program.Id, "Software", "Integrity Software");
        var artifact = new RequirementArtifact(project.Id, "SYSR-00000600", RequirementLevel.System, now);
        db.AddRange(program, project, artifact);

        var payload = Encoding.UTF8.GetBytes("Controlled evidence written once and never expected to change.");
        var stored = await store.StoreAsync(new MemoryStream(payload), "evidence.txt", "text/plain", default);
        db.ControlledAttachments.Add(new ControlledAttachment(project.Id, "Requirement", artifact.Id, null,
            Guid.NewGuid(), 1, "Evidence", "Controlled evidence", stored.OriginalFileName, stored.ContentType,
            stored.Size, stored.Sha256, stored.StorageKey, null, "test.setup", now));

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, stored.StorageKey);
    }

    private static async Task<JsonElement> CheckpointAsync(HttpClient client, Guid projectId)
    {
        using var response = await client.PostAsJsonAsync("/api/enterprise-hardening/integrity-checkpoints", new { projectId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task An_altered_attachment_fails_the_checkpoint_and_changes_its_manifest()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);

        using (var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Member, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        var healthy = await CheckpointAsync(client, seed.ProjectId);
        Assert.Equal("Healthy", healthy.GetProperty("state").GetString());
        Assert.Contains("1 attachment digest(s) recomputed", healthy.GetProperty("detail").GetString());

        // Repeating it on an unchanged repository must reproduce the same manifest, or the hash says nothing.
        var repeated = await CheckpointAsync(client, seed.ProjectId);
        Assert.Equal(healthy.GetProperty("manifestHash").GetString(), repeated.GetProperty("manifestHash").GetString());

        // Alter the byte content in place, leaving the file present and its recorded size untouched. This is
        // exactly the change the old checkpoint could not see.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var length = new FileInfo(StorePath(store, seed.StorageKey)).Length;
            await File.WriteAllBytesAsync(StorePath(store, seed.StorageKey), Encoding.UTF8.GetBytes(new string('x', (int)length)));
        }

        var altered = await CheckpointAsync(client, seed.ProjectId);
        Assert.Equal("Failed", altered.GetProperty("state").GetString());
        Assert.Contains("1 altered", altered.GetProperty("detail").GetString());
        Assert.NotEqual(healthy.GetProperty("manifestHash").GetString(), altered.GetProperty("manifestHash").GetString());
    }

    /// <summary>The store resolves keys privately, so the test asks it to open the file and reads the path.</summary>
    private static string StorePath(EvidenceFileStore store, string storageKey)
    {
        using var stream = (FileStream)store.OpenRead(storageKey);
        return stream.Name;
    }
}
