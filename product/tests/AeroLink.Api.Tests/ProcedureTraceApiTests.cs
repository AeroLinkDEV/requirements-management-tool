using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #399 — the Test Procedure Explorer's Trace &amp; impact tab must list the exact requirement revisions the
/// selected effective procedure revision verifies, with their controlled display number, immutable revision
/// identity, level, statement and Confirmed/Suspect coverage state, plus the TCR/change provenance that
/// produced the procedure revision.
///
/// The projection is authoritative server truth backed by exact stored coverage rows and the selected
/// build's exact procedure manifest (#214): a later procedure revision or a relationship belonging to
/// another build must never leak into an earlier build's trace.
/// </summary>
public sealed class ProcedureTraceApiTests
{
    private sealed record Fixture(
        Guid ProjectId,
        Guid Release15Id,
        Guid Release16Id,
        Guid Release17Id,
        Guid Baseline15Id,
        Guid Baseline16Id,
        Guid Baseline17Id,
        Guid ProcedureId,
        Guid Revision00Id,
        Guid Revision01Id,
        Guid Revision02Id,
        Guid ZeroCoverageProcedureId,
        Guid ZeroCoverageRevisionId,
        Guid Requirement1Id,
        Guid Requirement1RevisionId,
        Guid Requirement2Id,
        Guid Requirement2RevisionId,
        Guid Requirement3Id,
        Guid Requirement3RevisionId,
        Guid Requirement4Id,
        Guid Requirement4RevisionId,
        Guid TcrId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Procedure Trace Program", "PTP");
        var project = new ProjectRecord(program.Id, "FMS", "Trace FMS");
        var release15 = new SoftwareRelease(project.Id, "1.5", true);
        var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        var release17 = new SoftwareRelease(project.Id, "1.7", false, release16.Id);
        db.AddRange(program, project, release15, release16, release17);

        SystemChangeRequest Approved(string number, string requirementNumber, string statement, Guid releaseId)
        {
            var request = new SystemChangeRequest(number, 0, project.Id, releaseId,
                "Trace fixture", "Problem", "Analysis", "Solution", "author", now);
            request.AddRequirementChange("author", requirementNumber, 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, statement, "Trace fixture rationale.", "Test", now);
            request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            request.ApproveActiveStage("reviewer", now);
            return request;
        }

        CandidateBaseline Baseline(string number, SoftwareRelease release, SystemChangeRequest request,
            Guid? predecessor)
        {
            var baseline = new CandidateBaseline(number, 0, project.Id, release.Id, predecessor,
                $"Build {release.Version}", "cm", now);
            baseline.Select(request, "cm", now);
            baseline.Freeze("cm", now);
            baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now);
            return baseline;
        }

        var scr15 = Approved("SRCR-03150", "SYSR-000001",
            "The FMS shall retain exact build-scoped verification traceability.", release15.Id);
        var scr16 = Approved("SRCR-03151", "SYSR-000002",
            "The FMS shall sequence oceanic waypoints in the configured round-robin order.", release16.Id);
        var scr17 = Approved("SRCR-03152", "SYSR-000003",
            "The FMS shall report the active round-robin sequence to the flight crew.", release17.Id);
        var baseline15 = Baseline("SW-01.50", release15, scr15, null);
        var baseline16 = Baseline("SW-01.60", release16, scr16, baseline15.Id);
        var baseline17 = Baseline("SW-01.70", release17, scr17, baseline16.Id);

        var requirement1 = new RequirementArtifact(project.Id, "SYSR-000001", RequirementLevel.System, now);
        var requirement2 = new RequirementArtifact(project.Id, "SYSR-000002", RequirementLevel.System, now);
        var requirement3 = new RequirementArtifact(project.Id, "SYSR-000003", RequirementLevel.System, now);
        var requirement4 = new RequirementArtifact(project.Id, "SYSR-000004", RequirementLevel.System, now);
        var requirement1Revision = new RequirementRevision(requirement1.Id, 0,
            "The FMS shall retain exact build-scoped verification traceability.",
            "Configuration identity must be deterministic.", "Test",
            RequirementRevisionState.Active, scr15.Id, baseline15.Id, now);
        var requirement2Revision = new RequirementRevision(requirement2.Id, 0,
            "The FMS shall sequence oceanic waypoints in the configured round-robin order.",
            "New FMS 1.6 capability.", "Test",
            RequirementRevisionState.Active, scr16.Id, baseline16.Id, now);
        var requirement3Revision = new RequirementRevision(requirement3.Id, 0,
            "The FMS shall report the active round-robin sequence to the flight crew.",
            "New FMS 1.7 capability.", "Test",
            RequirementRevisionState.Active, scr17.Id, baseline17.Id, now);
        var requirement4Revision = new RequirementRevision(requirement4.Id, 0,
            "The FMS shall persist the round-robin sequence across power cycles.",
            "Future-only capability.", "Test",
            RequirementRevisionState.Active, scr17.Id, baseline17.Id, now);
        db.AddRange(scr15, scr16, scr17, baseline15, baseline16, baseline17,
            requirement1, requirement2, requirement3, requirement4,
            requirement1Revision, requirement2Revision, requirement3Revision, requirement4Revision);
        db.BaselineRequirements.AddRange(
            new BaselineRequirementSelection(baseline15.Id, requirement1.Id, requirement1Revision.Id),
            new BaselineRequirementSelection(baseline16.Id, requirement1.Id, requirement1Revision.Id),
            new BaselineRequirementSelection(baseline16.Id, requirement2.Id, requirement2Revision.Id),
            new BaselineRequirementSelection(baseline17.Id, requirement1.Id, requirement1Revision.Id),
            new BaselineRequirementSelection(baseline17.Id, requirement2.Id, requirement2Revision.Id),
            new BaselineRequirementSelection(baseline17.Id, requirement3.Id, requirement3Revision.Id),
            new BaselineRequirementSelection(baseline17.Id, requirement4.Id, requirement4Revision.Id));

        var review = new TestChangeReview(project.Id, release16.Id, scr16.Id,
            TestChangeReviewDiscipline.System, scr16.DisplayNumber, now);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AssignControlledNumber("SYSTCR-000001", now);
        db.Add(review);

        var procedure = new TestProcedure(project.Id, "SYSTP-000001", "Exact trace procedure",
            "test.author", now, TestProcedureLevel.System);
        var revision00 = Revision(procedure.Id, 0, "Released 1.5 procedure", baseline15.Id);
        var revision01 = Revision(procedure.Id, 1, "Build 1.6 procedure", baseline16.Id, review.Id);
        var revision02 = Revision(procedure.Id, 2, "Future 1.7 procedure", baseline17.Id);
        var zeroCoverage = new TestProcedure(project.Id, "SYSTP-000002", "Retained zero-coverage procedure",
            "test.author", now, TestProcedureLevel.System);
        var zeroRevision = Revision(zeroCoverage.Id, 0, "Carried without current coverage", baseline15.Id);
        db.AddRange(procedure, revision00, revision01, revision02, zeroCoverage, zeroRevision);

        db.TestCoverage.AddRange(
            new TestRequirementCoverage(revision00.Id, requirement1Revision.Id),
            new TestRequirementCoverage(revision01.Id, requirement1Revision.Id),
            new TestRequirementCoverage(revision01.Id, requirement2Revision.Id),
            // Suspect on purpose: carried forward onto a revision whose requirement wording changed and
            // never reconfirmed, so it must render distinctly from Confirmed coverage.
            TestRequirementCoverage.CarriedForward(revision01.Id, requirement3Revision.Id,
                "Requirement wording changed; reconfirmation pending.", now),
            new TestRequirementCoverage(revision02.Id, requirement1Revision.Id),
            new TestRequirementCoverage(revision02.Id, requirement4Revision.Id));
        db.BaselineTestProcedures.AddRange(
            new BaselineTestProcedureSelection(baseline15.Id, procedure.Id, revision00.Id),
            new BaselineTestProcedureSelection(baseline15.Id, zeroCoverage.Id, zeroRevision.Id),
            new BaselineTestProcedureSelection(baseline16.Id, procedure.Id, revision01.Id),
            new BaselineTestProcedureSelection(baseline16.Id, zeroCoverage.Id, zeroRevision.Id),
            new BaselineTestProcedureSelection(baseline17.Id, procedure.Id, revision02.Id));
        baseline15.MarkTestProceduresMaterialized("cm", new string('b', 64), 2, now);
        baseline16.MarkTestProceduresMaterialized("cm", new string('c', 64), 2, now);
        baseline17.MarkTestProceduresMaterialized("cm", new string('d', 64), 1, now);

        var change = scr16.RequirementChanges.Single(x => x.BaseNumber == "SYSR-000002");
        var item = VerificationImpactItem.ForIntroducedRequirement(project.Id, release16.Id, scr16.Id,
            review.Id, change.Id, change.DisplayNumber, "Test", now);
        item.LinkRequirementRevision(requirement2Revision.Id, now);
        item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Procedure alignment completed for Build 1.6.", now,
            procedure.Id, revision01.Id, TestProcedureChangeAction.CreateNew,
            preReleaseEvidenceRequired: false);
        db.Add(item);

        var engineer = new UserAccount("trace.engineer", "Trace Engineer", "trace@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount("trace.outsider", "Trace Outsider", "outsider@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(engineer, outsider,
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        var otherProgram = new ProgramRecord("Other Program", "OTP");
        var otherProject = new ProjectRecord(otherProgram.Id, "Other", "Other software");
        db.AddRange(otherProgram, otherProject,
            new ProgramMembership(outsider.Id, otherProgram.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        return new Fixture(project.Id, release15.Id, release16.Id, release17.Id,
            baseline15.Id, baseline16.Id, baseline17.Id,
            procedure.Id, revision00.Id, revision01.Id, revision02.Id,
            zeroCoverage.Id, zeroRevision.Id,
            requirement1.Id, requirement1Revision.Id,
            requirement2.Id, requirement2Revision.Id,
            requirement3.Id, requirement3Revision.Id,
            requirement4.Id, requirement4Revision.Id,
            review.Id);

        TestProcedureRevision Revision(Guid procedureId, int revision, string objective, Guid baselineId,
            Guid? sourceTestChangeRequestId = null) =>
            new(procedureId, revision, objective, "Configured test environment", "Execute the controlled steps.",
                "The expected behavior is observed.", TestProcedureState.Approved, "test.author", now,
                sourceTestChangeRequestId: sourceTestChangeRequestId, effectiveBaselineId: baselineId);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Trace_lists_every_exact_requirement_revision_with_state_and_provenance()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "trace.engineer");

        using var response = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Equal(fixture.ProcedureId, body.GetProperty("procedureId").GetGuid());
        Assert.Equal(fixture.Revision01Id, body.GetProperty("revisionId").GetGuid());
        Assert.Equal("SYSTP-000001.01", body.GetProperty("displayNumber").GetString());
        Assert.Equal("Approved", body.GetProperty("state").GetString());

        var requirements = body.GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal(3, requirements.Count);
        var byDisplay = requirements.ToDictionary(x => x.GetProperty("displayNumber").GetString()!);
        Assert.Equal(fixture.Requirement1RevisionId,
            byDisplay["SYSR-000001.00"].GetProperty("revisionId").GetGuid());
        Assert.Equal(fixture.Requirement1Id, byDisplay["SYSR-000001.00"].GetProperty("id").GetGuid());
        Assert.Equal("System", byDisplay["SYSR-000001.00"].GetProperty("level").GetString());
        Assert.Contains("exact build-scoped verification traceability",
            byDisplay["SYSR-000001.00"].GetProperty("statement").GetString());
        Assert.Equal("Confirmed", byDisplay["SYSR-000001.00"].GetProperty("coverageState").GetString());
        Assert.Equal(fixture.Requirement2RevisionId,
            byDisplay["SYSR-000002.00"].GetProperty("revisionId").GetGuid());
        Assert.Equal("Confirmed", byDisplay["SYSR-000002.00"].GetProperty("coverageState").GetString());
        Assert.Equal(fixture.Requirement3RevisionId,
            byDisplay["SYSR-000003.00"].GetProperty("revisionId").GetGuid());
        Assert.Equal("Suspect", byDisplay["SYSR-000003.00"].GetProperty("coverageState").GetString());
        Assert.True(byDisplay["SYSR-000003.00"].GetProperty("isSuspect").GetBoolean());

        var provenance = body.GetProperty("provenance").EnumerateArray().ToList();
        Assert.Contains(provenance, x => x.GetProperty("package").GetString() == "SYSTCR-000001"
            && x.GetProperty("changeRequest").GetString() == "SRCR-03151.00");
        Assert.Equal(fixture.TcrId, body.GetProperty("sourceTestChangeRequestId").GetGuid());
        Assert.True(body.GetProperty("build").GetProperty("isExactManifest").GetBoolean());
        Assert.Equal(fixture.Baseline16Id, body.GetProperty("build").GetProperty("effectiveBaselineId").GetGuid());
    }

    [Fact]
    public async Task Each_builds_trace_is_its_own_manifest_revision_and_later_links_do_not_leak()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "trace.engineer");

        var build15 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release15Id}");
        Assert.Equal(fixture.Revision00Id, build15.GetProperty("revisionId").GetGuid());
        Assert.Equal("SYSTP-000001.00", build15.GetProperty("displayNumber").GetString());
        var build15Requirements = build15.GetProperty("requirements").EnumerateArray()
            .Select(x => x.GetProperty("displayNumber").GetString()).ToList();
        Assert.Equal(["SYSR-000001.00"], build15Requirements);
        Assert.Equal("Confirmed", build15.GetProperty("requirements")[0].GetProperty("coverageState").GetString());

        var build16 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}");
        Assert.Equal(fixture.Revision01Id, build16.GetProperty("revisionId").GetGuid());
        var build16Requirements = build16.GetProperty("requirements").EnumerateArray()
            .Select(x => x.GetProperty("displayNumber").GetString()).ToList();
        Assert.Equal(["SYSR-000001.00", "SYSR-000002.00", "SYSR-000003.00"], build16Requirements);

        var build17 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release17Id}");
        Assert.Equal(fixture.Revision02Id, build17.GetProperty("revisionId").GetGuid());
        var build17Requirements = build17.GetProperty("requirements").EnumerateArray()
            .Select(x => x.GetProperty("displayNumber").GetString()).ToList();
        Assert.Equal(["SYSR-000001.00", "SYSR-000004.00"], build17Requirements);
    }

    [Fact]
    public async Task Cross_build_revision_access_follows_the_exact_manifest()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "trace.engineer");

        using var earlierRevision = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}&revisionId={fixture.Revision00Id}");
        Assert.Equal(HttpStatusCode.NotFound, earlierRevision.StatusCode);

        using var laterRevision = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}&revisionId={fixture.Revision02Id}");
        Assert.Equal(HttpStatusCode.NotFound, laterRevision.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await laterRevision.Content.ReadAsStringAsync());
        Assert.Equal("cross_build_procedure_revision", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Zero_coverage_carried_procedure_remains_visible_and_truthful()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "trace.engineer");

        using var response = await client.GetAsync(
            $"/api/test-procedures/{fixture.ZeroCoverageProcedureId}/trace?releaseId={fixture.Release15Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(fixture.ZeroCoverageRevisionId, body.GetProperty("revisionId").GetGuid());
        Assert.Equal("SYSTP-000002.00", body.GetProperty("displayNumber").GetString());
        Assert.Equal(0, body.GetProperty("requirements").GetArrayLength());
    }

    [Fact]
    public async Task Trace_is_refused_for_another_program()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "trace.outsider");

        using var response = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/trace?releaseId={fixture.Release16Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
