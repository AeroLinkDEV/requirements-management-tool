using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class RequirementRedlineApiTests
{
    [Fact]
    public async Task Redline_returns_complete_exact_revision_text_and_explicit_comparison_modes_without_writes()
    {
        await using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var prefix = string.Join(" ", Enumerable.Repeat("requirement", 400));
        var beforeText = prefix + " reject";
        var afterText = prefix + " accept";
        Guid artifactId, beforeId, afterId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Redline programme", "RDL");
            var project = new ProjectRecord(program.Id, "Software", "Redline project");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var scr = new SystemChangeRequest("SRCR-00954", 0, project.Id, release.Id, "Redline", "P", "A", "S", "author", now);
            var baseline = new CandidateBaseline("BL-00000954", 0, project.Id, release.Id, null, "Redline baseline", "cm", now);
            var artifact = new RequirementArtifact(project.Id, "SYSR-00000954", RequirementLevel.System, now);
            var before = new RequirementRevision(artifact.Id, 0, beforeText, beforeText, "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            var after = new RequirementRevision(artifact.Id, 1, afterText, afterText, "Analysis", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            var account = new UserAccount("redline.reader", "Redline reader", "redline@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, scr, baseline, artifact, before, after, account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "fixture", now));
            await db.SaveChangesAsync();
            artifactId = artifact.Id;
            beforeId = before.Id;
            afterId = after.Id;
        }

        var route = $"/api/enterprise-requirements/{artifactId}/redline?fromRevisionId={beforeId}&toRevisionId={afterId}";
        using var anonymous = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "redline.reader", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(beforeId, body.GetProperty("fromRevisionId").GetGuid());
        Assert.Equal(afterId, body.GetProperty("toRevisionId").GetGuid());
        Assert.True(body.GetProperty("comparison").GetProperty("isComplete").GetBoolean());
        foreach (var field in new[] { "statement", "rationale", "richText" })
        {
            Assert.Equal("WholeField", body.GetProperty("comparison").GetProperty(field).GetString());
            var spans = body.GetProperty(field).EnumerateArray().ToList();
            Assert.Equal(beforeText, Assert.Single(spans, x => x.GetProperty("kind").GetString() == "removed").GetProperty("text").GetString());
            Assert.Equal(afterText, Assert.Single(spans, x => x.GetProperty("kind").GetString() == "added").GetProperty("text").GetString());
        }
        using var invalidPair = await client.GetAsync(
            $"/api/enterprise-requirements/{artifactId}/redline?fromRevisionId={beforeId}&toRevisionId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPair.StatusCode);
        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revisions = await assertDb.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == artifactId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(beforeText, revisions[0].Statement);
        Assert.Equal(afterText, revisions[1].Statement);
        Assert.All(revisions, x => Assert.Equal(RequirementRevisionState.Active, x.State));
    }
}
