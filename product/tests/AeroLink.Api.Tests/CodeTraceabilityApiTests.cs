using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

[Collection(ShowcaseApiCollection.Name)]
public sealed class CodeTraceabilityApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task Code_gate_is_build_scoped_and_accepts_a_justified_no_code_decision_for_active_work()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var summary = showcase.Summary;

        var active = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={summary.ActiveReleaseId}");
        Assert.False(active.GetProperty("build").GetProperty("readOnly").GetBoolean());
        Assert.True(active.GetProperty("demonstrationScope").GetBoolean());
        Assert.Equal(5, active.GetProperty("summary").GetProperty("required").GetInt32());
        Assert.Equal(4, active.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.False(active.GetProperty("summary").GetProperty("gateComplete").GetBoolean());
        Assert.Contains("GitLab is the source of truth", active.GetProperty("sourceOfTruth").GetString());

        var missing = active.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("mapping").ValueKind == JsonValueKind.Null);
        using var created = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = summary.ProjectId,
            releaseId = summary.ActiveReleaseId,
            requirementArtifactId = missing.GetProperty("artifactId").GetGuid(),
            requirementRevisionId = missing.GetProperty("revisionId").GetGuid(),
            disposition = "NoCodeChangeRequired",
            noCodeChangeRationale = "The approved LLR clarifies existing behavior and requires no executable change.",
        });
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"Expected Created, got {(int)created.StatusCode}: {createdBody}");

        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={summary.ActiveReleaseId}");
        Assert.Equal(5, completed.GetProperty("summary").GetProperty("mapped").GetInt32());
        Assert.True(completed.GetProperty("summary").GetProperty("gateComplete").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var releasedId = db.Releases.Single(x => x.ProjectId == summary.ProjectId && x.IsReleased).Id;
        var released = await client.GetFromJsonAsync<JsonElement>($"/api/code-traceability?projectId={summary.ProjectId}&releaseId={releasedId}");
        Assert.True(released.GetProperty("build").GetProperty("readOnly").GetBoolean());
        Assert.True(released.GetProperty("summary").GetProperty("gateComplete").GetBoolean());

        // Remove one historical mapping inside this private database copy so the request cannot be rejected merely
        // by the unique index. The endpoint itself must enforce released-build immutability.
        var historicalRequirement = released.GetProperty("requirements").EnumerateArray().First();
        var historicalMappingId = historicalRequirement.GetProperty("mapping").GetProperty("id").GetGuid();
        db.CodeTraceabilityRecords.Remove(db.CodeTraceabilityRecords.Single(x => x.Id == historicalMappingId));
        await db.SaveChangesAsync();

        using var refused = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = summary.ProjectId,
            releaseId = releasedId,
            requirementArtifactId = historicalRequirement.GetProperty("artifactId").GetGuid(),
            requirementRevisionId = historicalRequirement.GetProperty("revisionId").GetGuid(),
            disposition = "NoCodeChangeRequired",
            noCodeChangeRationale = "Must remain historical.",
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("released and read-only", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Digital_thread_returns_one_exact_SYSR_to_build_path()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var summary = showcase.Summary;
        var path = await client.GetFromJsonAsync<JsonElement>($"/api/traceability/path?projectId={summary.ProjectId}&baselineId={summary.ReleasedBaselineId}");

        Assert.Equal(["System", "HighLevel", "LowLevel"], path.GetProperty("nodes").EnumerateArray().Select(x => x.GetProperty("level").GetString()!).ToArray());
        Assert.StartsWith("LLRTP-", path.GetProperty("procedure").GetProperty("displayNumber").GetString());
        Assert.Equal("Pass", path.GetProperty("execution").GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(path.GetProperty("execution").GetProperty("evidenceReference").GetString()));
        Assert.Empty(path.GetProperty("execution").GetProperty("evidence").EnumerateArray());
        Assert.Contains("1.5", path.GetProperty("build").GetProperty("buildNumber").GetString());
    }

    [Fact]
    public async Task Digital_thread_prefers_the_exact_procedure_with_linked_checksummed_evidence()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var summary = showcase.Summary;
        var original = await client.GetFromJsonAsync<JsonElement>($"/api/traceability/path?projectId={summary.ProjectId}&baselineId={summary.ReleasedBaselineId}");
        var llrRevisionId = original.GetProperty("nodes").EnumerateArray().Last().GetProperty("revisionId").GetGuid();

        Guid evidencedExecutionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var releasedId = db.Releases.Single(x => x.ProjectId == summary.ProjectId && x.IsReleased).Id;
            var buildId = db.SoftwareBuilds.Single(x => x.ReleaseId == releasedId).Id;
            var now = DateTimeOffset.UtcNow;
            var decoyProcedure = new TestProcedure(summary.ProjectId, "LLRTP-000000", "Unscoped evidenced procedure", "test.engineer", now, TestProcedureLevel.LowLevel);
            var decoyRevision = new TestProcedureRevision(decoyProcedure.Id, 0, "Verify an unspecified build.", "Load an unspecified build.", "Exercise the behavior.", "The behavior is observed.", TestProcedureState.Approved, "test.engineer", now);
            var decoyExecution = new TestExecution(summary.ProjectId, decoyRevision.Id, null, null, TestOutcome.Pass, "test.engineer", "Legacy release scope", "This result has no immutable software-build identity.", "external://run/unscoped", now.AddMinutes(1), now.AddMinutes(1), releasedId);
            var decoyEvidence = new EvidenceRecord(summary.ProjectId, "unscoped-run.json", "application/json", 128, new string('c', 64), "test/unscoped-run.json", "test.engineer", now);
            var procedure = new TestProcedure(summary.ProjectId, "LLRTP-999999", "Verify the evidenced exact LLR path", "test.engineer", now, TestProcedureLevel.LowLevel);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify the exact approved behavior.", "Load the released build.", "Exercise the approved LLR behavior.", "The behavior matches the exact LLR revision.", TestProcedureState.Approved, "test.engineer", now);
            var execution = new TestExecution(summary.ProjectId, revision.Id, buildId, null, TestOutcome.Pass, "test.engineer", "FMS 1.5", "The observed behavior satisfies the approved expected result.", "external://run/evidenced", now, now, releasedId);
            var evidence = new EvidenceRecord(summary.ProjectId, "evidenced-run.json", "application/json", 128, new string('a', 64), "test/evidenced-run.json", "test.engineer", now);
            db.AddRange(
                decoyProcedure, decoyRevision, new TestRequirementCoverage(decoyRevision.Id, llrRevisionId), decoyExecution, decoyEvidence, new TestExecutionEvidence(decoyExecution.Id, decoyEvidence.Id),
                procedure, revision, new TestRequirementCoverage(revision.Id, llrRevisionId), execution, evidence, new TestExecutionEvidence(execution.Id, evidence.Id));
            await db.SaveChangesAsync();
            evidencedExecutionId = execution.Id;
        }

        var selected = await client.GetFromJsonAsync<JsonElement>($"/api/traceability/path?projectId={summary.ProjectId}&baselineId={summary.ReleasedBaselineId}&focusRevisionId={llrRevisionId}");
        Assert.Equal("LLRTP-999999.00", selected.GetProperty("procedure").GetProperty("displayNumber").GetString());
        Assert.Equal(evidencedExecutionId, selected.GetProperty("execution").GetProperty("id").GetGuid());
        var attached = Assert.Single(selected.GetProperty("execution").GetProperty("evidence").EnumerateArray());
        Assert.Equal("evidenced-run.json", attached.GetProperty("originalFileName").GetString());
        Assert.Equal(new string('a', 64), attached.GetProperty("sha256").GetString());
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new { displayName = "Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
