using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The artifact thread of #880 §5.3 — one focal artifact's exact-revision chain across the six prototype lanes.
///
/// What these prove is mostly what the read refuses to do. The compact path read it sits beside walks one
/// branch, roots only on a requirement and takes the latest build; each of those is a regression this thread
/// would silently suffer, so each has a test that fails if it returns.
/// </summary>
public sealed class ArtifactThreadApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;
    public ArtifactThreadApiTests(SharedApiHost host) => _host = host;

    private const string PassHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string FailHash = "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752";

    // ---- 1. every focal kind roots the same thread ---------------------------------------------------

    [Theory]
    [InlineData("Requirement")]
    [InlineData("Case")]
    [InlineData("Procedure")]
    [InlineData("Execution")]
    [InlineData("Build")]
    public async Task Every_focal_kind_of_section_4_4_can_root_a_thread(string focalKind)
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var focalId = focalKind switch
        {
            "Requirement" => world.HighLevelRevisionId,
            "Case" => world.CaseRevisionId,
            "Procedure" => world.FirstProcedureRevisionId,
            "Execution" => world.PassExecutionId,
            _ => world.BuildId,
        };

        var thread = await ThreadAsync(client, world.ProjectId, focalKind, focalId);

        // Whichever end it is entered from, the same recorded chain is reached. §4.4 lists five entry kinds and
        // the compact path read expresses exactly one of them.
        var kinds = thread.GetProperty("nodes").EnumerateArray()
            .Select(x => x.GetProperty("kind").GetString()).Distinct().ToList();
        Assert.Contains("Requirement", kinds);
        Assert.Contains("Procedure", kinds);
        Assert.Contains("Execution", kinds);
    }

    [Fact]
    public async Task An_unknown_focal_kind_is_refused_rather_than_guessed()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        using var response = await client.GetAsync(
            $"/api/artifact-thread?projectId={world.ProjectId}&focalKind=Baseline&focalId={world.BuildId}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- 2. all branches survive; no preferred single path -------------------------------------------

    [Fact]
    public async Task Both_covering_procedures_are_returned_not_a_preferred_one()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // The case runs two procedures. The compact path read picks one by tie-breaker; a lane canvas that did
        // the same would silently hide a procedure a reviewer is accountable for.
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.Contains(world.SecondProcedureRevisionId.ToString(), procedures);
    }

    [Fact]
    public async Task Sibling_requirements_under_one_parent_are_both_returned()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var requirements = Nodes(thread, "Requirement").Select(x => x.GetProperty("id").GetString()).ToList();

        Assert.Contains(world.SystemRevisionId.ToString(), requirements);
        Assert.Contains(world.HighLevelRevisionId.ToString(), requirements);
        // A second child of the same System parent. Walking one descendant would drop it.
        Assert.Contains(world.SiblingHighLevelRevisionId.ToString(), requirements);
    }

    // ---- 3. exact revisions do not collapse ----------------------------------------------------------

    [Fact]
    public async Task Two_revisions_of_one_requirement_are_two_nodes()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.SupersededSystemRevisionId);
        var system = Nodes(thread, "Requirement")
            .Where(x => x.GetProperty("artifactId").GetString() == world.SystemArtifactId.ToString()).ToList();

        // Same controlled artifact, two exact revisions. Keying anything by artifact id would collapse them and
        // take one revision's coverage with it.
        Assert.Equal(2, system.Count);
        Assert.Equal([0, 1], system.Select(x => x.GetProperty("revision").GetInt32()).OrderBy(x => x).ToArray());
    }

    // ---- 4. the build is the recorded one, not the latest --------------------------------------------

    [Fact]
    public async Task The_build_is_the_one_the_execution_recorded_not_the_newest_for_the_baseline()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var builds = Nodes(thread, "Build").Select(x => x.GetProperty("id").GetString()).ToList();

        // A later build exists for the same baseline and no execution in this thread ran against it. The compact
        // path read takes SoftwareBuilds.OrderByDescending(RecordedAt).First(), which would name it here.
        Assert.Contains(world.BuildId.ToString(), builds);
        Assert.DoesNotContain(world.LaterBuildId.ToString(), builds);
    }

    [Fact]
    public async Task An_execution_and_its_build_share_the_final_lane()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var execution = Nodes(thread, "Execution").First();
        var build = Nodes(thread, "Build").First();

        // The prototype places EXE-004821 and FMS-1.5.0 both in lane 5 and draws an edge between them, so the
        // contract has to admit an edge whose endpoints share a lane.
        Assert.Equal(5, execution.GetProperty("lane").GetInt32());
        Assert.Equal(5, build.GetProperty("lane").GetInt32());
        Assert.Contains(thread.GetProperty("edges").EnumerateArray(), edge =>
            edge.GetProperty("fromId").GetString() == execution.GetProperty("id").GetString()
            && edge.GetProperty("toId").GetString() == build.GetProperty("id").GetString());
    }

    // ---- 5. System bypasses Case; HLR goes through it -------------------------------------------------

    [Fact]
    public async Task A_system_requirement_reaches_its_procedure_without_a_case()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Procedure", world.SystemProcedureRevisionId);

        // Read from the recorded coverage row, not assumed from the level: a System procedure covers the
        // requirement directly, so no Case node stands between them.
        Assert.Contains(thread.GetProperty("edges").EnumerateArray(), edge =>
            edge.GetProperty("fromId").GetString() == world.SystemRevisionId.ToString()
            && edge.GetProperty("toId").GetString() == world.SystemProcedureRevisionId.ToString()
            && edge.GetProperty("relation").GetString() == "verified by");
    }

    [Fact]
    public async Task A_high_level_requirement_reaches_its_procedures_through_its_case()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var edges = thread.GetProperty("edges").EnumerateArray().ToList();

        Assert.Contains(edges, edge =>
            edge.GetProperty("fromId").GetString() == world.HighLevelRevisionId.ToString()
            && edge.GetProperty("toId").GetString() == world.CaseRevisionId.ToString());
        Assert.Contains(edges, edge =>
            edge.GetProperty("fromId").GetString() == world.CaseRevisionId.ToString()
            && edge.GetProperty("toId").GetString() == world.FirstProcedureRevisionId.ToString()
            && edge.GetProperty("relation").GetString() == "run by");

        // Both cases covering this requirement are present, and every one of them sits in the Test Case lane.
        var cases = Nodes(thread, "Case");
        Assert.Equal(2, cases.Count);
        Assert.All(cases, node => Assert.Equal(3, node.GetProperty("lane").GetInt32()));
    }

    // ---- 6. suspect is server-stated ------------------------------------------------------------------

    [Fact]
    public async Task A_suspect_coverage_row_and_a_suspect_exact_link_both_arrive_marked()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var edges = thread.GetProperty("edges").EnumerateArray().ToList();

        // Two different mechanisms: TestRequirementCoverage carries a stored IsSuspect flag, while a
        // Case-to-Procedure link is suspect through a shared ExactLinkSuspectLifecycle that is not yet Closed.
        // The artifact thread is the first #880 view able to return true at all (§8.3).
        var coverage = edges.Single(x => x.GetProperty("fromId").GetString() == world.HighLevelRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.CaseRevisionId.ToString());
        Assert.True(coverage.GetProperty("isSuspect").GetBoolean());

        var run = edges.Single(x => x.GetProperty("fromId").GetString() == world.RevisedCaseRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.SecondProcedureRevisionId.ToString());
        Assert.True(run.GetProperty("isSuspect").GetBoolean());

        var settled = edges.Single(x => x.GetProperty("fromId").GetString() == world.CaseRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.FirstProcedureRevisionId.ToString());
        Assert.False(settled.GetProperty("isSuspect").GetBoolean());
    }

    [Fact]
    public async Task A_closed_lifecycle_is_not_suspect()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);

        // Closed is the one state the repository treats as settled everywhere else — the requirements workspace,
        // the controlled output generator and the release readiness gate all filter on exactly this. A lifecycle
        // that exists but has been resolved must not leave its link permanently amber.
        var settled = thread.GetProperty("edges").EnumerateArray().Single(x =>
            x.GetProperty("fromId").GetString() == world.RevisedCaseRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.ClosedProcedureRevisionId.ToString());
        Assert.False(settled.GetProperty("isSuspect").GetBoolean());
    }

    // ---- 7. evidence identity and hash survive --------------------------------------------------------

    [Fact]
    public async Task Evidence_files_keep_their_identity_and_hash_on_the_execution()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Execution", world.PassExecutionId);
        var execution = Nodes(thread, "Execution")
            .Single(x => x.GetProperty("id").GetString() == world.PassExecutionId.ToString());
        var evidence = execution.GetProperty("evidence").EnumerateArray().ToList();

        // The hash is the reason EvidenceRecord exists. Folding these into the execution's free-text
        // EvidenceReference would drop the file identity a certification reviewer follows the thread to reach.
        Assert.Equal(2, evidence.Count);
        Assert.Equal(["oceanic-run.json", "oceanic-trace.log"],
            evidence.Select(x => x.GetProperty("fileName").GetString()).ToArray());
        Assert.Contains(evidence, x => x.GetProperty("sha256").GetString() == PassHash);
        Assert.All(evidence, x => Assert.False(string.IsNullOrWhiteSpace(x.GetProperty("uploadedBy").GetString())));
    }

    [Fact]
    public async Task Every_recorded_run_is_returned_not_only_the_latest()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var executions = Nodes(thread, "Execution").Select(x => x.GetProperty("id").GetString()).ToList();

        // A failed run and the retest that followed it are both part of the certification record. Showing only
        // the newest would report a clean history that did not happen.
        Assert.Contains(world.FailExecutionId.ToString(), executions);
        Assert.Contains(world.PassExecutionId.ToString(), executions);
    }

    // ---- 8. levels with no verification discipline ----------------------------------------------------

    [Fact]
    public async Task An_interface_requirement_states_why_it_has_no_verification_rather_than_fabricating_one()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.InterfaceRevisionId);
        var verification = thread.GetProperty("verification");

        // RequirementLevel has five members; VerificationDiscipline has three. ProjectLadderConfiguration throws
        // for Interface, so the chain truthfully stops at Requirement. The view is not refused, no case,
        // procedure or execution is invented, and the reason is stated rather than left to be inferred.
        Assert.False(verification.GetProperty("isApplicable").GetBoolean());
        Assert.Contains("no verification discipline", verification.GetProperty("reason").GetString());
        Assert.Empty(Nodes(thread, "Case"));
        Assert.Empty(Nodes(thread, "Procedure"));
        Assert.Empty(Nodes(thread, "Execution"));
        Assert.NotEmpty(Nodes(thread, "Requirement"));
    }

    [Fact]
    public async Task A_verifiable_level_reports_verification_as_applicable()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        Assert.True(thread.GetProperty("verification").GetProperty("isApplicable").GetBoolean());
        Assert.Null(thread.GetProperty("verification").GetProperty("reason").GetString());
    }

    // ---- 9. authorization ------------------------------------------------------------------------------

    [Fact]
    public async Task The_thread_is_refused_to_a_caller_outside_the_project()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Outsider);

        using var response = await client.GetAsync(
            $"/api/artifact-thread?projectId={world.ProjectId}&focalKind=Requirement&focalId={world.HighLevelRevisionId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_focal_artifact_in_another_project_is_not_found_rather_than_served()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        // Authorized for this Project, asking for another Project's requirement through it. Answering would let
        // an authorized caller read across the boundary by supplying a foreign identity.
        using var response = await client.GetAsync(
            $"/api/artifact-thread?projectId={world.ProjectId}&focalKind=Requirement&focalId={world.ForeignRevisionId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task No_detail_of_another_project_appears_in_a_thread()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world.ProjectId, "Requirement", world.HighLevelRevisionId);
        var raw = thread.GetRawText();

        Assert.DoesNotContain("SR-97900", raw);
        Assert.DoesNotContain("Foreign statement", raw);
        Assert.DoesNotContain(world.ForeignRevisionId.ToString(), raw);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static List<JsonElement> Nodes(JsonElement thread, string kind) =>
        thread.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("kind").GetString() == kind).ToList();

    private static async Task<JsonElement> ThreadAsync(HttpClient client, Guid projectId, string kind, Guid focalId)
    {
        using var response = await client.GetAsync(
            $"/api/artifact-thread?projectId={projectId}&focalKind={kind}&focalId={focalId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private sealed record World(
        Guid ProjectId, Guid SystemArtifactId, Guid SystemRevisionId, Guid SupersededSystemRevisionId,
        Guid ClosedProcedureRevisionId, Guid RevisedCaseRevisionId, Guid HighLevelRevisionId, Guid SiblingHighLevelRevisionId,
        Guid InterfaceRevisionId, Guid CaseRevisionId, Guid FirstProcedureRevisionId,
        Guid SecondProcedureRevisionId, Guid SystemProcedureRevisionId, Guid PassExecutionId,
        Guid FailExecutionId, Guid BuildId, Guid LaterBuildId, Guid ForeignRevisionId,
        string Member, string Outsider);

    /// <summary>
    /// One project holding every shape the thread has to survive: a branching requirement ladder, two revisions
    /// of one artifact, a Case running two procedures, a System procedure covering directly, a failed run and
    /// its retest, two builds, both suspect mechanisms and a closed lifecycle — plus a second Project whose
    /// records must never surface.
    /// </summary>
    private static async Task<World> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];

        var member = $"thread.member.{tag}";
        var outsider = $"thread.outsider.{tag}";
        var program = new ProgramRecord($"Thread {tag}", $"TH{tag}");
        var project = new ProjectRecord(program.Id, "Flight management", "Artifact thread qualification");
        var release = new SoftwareRelease(project.Id, "7.0", false);
        var memberAccount = new UserAccount(member, member, $"{member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsiderAccount = new UserAccount(outsider, outsider, $"{outsider}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, memberAccount, outsiderAccount,
            new ProgramMembership(memberAccount.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var baseline = new CandidateBaseline("SW-97.00", 0, project.Id, release.Id, null, "Build 7.0", "cm", now);
        db.Add(baseline);

        var scr = new SystemChangeRequest("SRCR-97001", 0, project.Id, release.Id, "Oceanic sequencing",
            "Problem", "Analysis", "Solution", member, now);
        db.Add(scr);

        var report = new ProblemReport(project.Id, "PR-97001", "Waypoints sequenced out of order",
            "Observed during flight test.", "Root cause in the sequencer.", member, now);
        db.Add(report);
        db.Add(new ProblemReportLink(report.Id, "ChangeRequest", scr.Id, "resolved by", member, now));

        // A System requirement with two exact revisions, so nothing may collapse them by artifact id.
        var systemArtifact = new RequirementArtifact(project.Id, "SR-97001", RequirementLevel.System, now);
        var supersededSystem = new RequirementRevision(systemArtifact.Id, 0,
            "The FMS shall sequence oceanic waypoints.", "Rationale", "Test",
            RequirementRevisionState.Superseded, scr.Id, baseline.Id, now);
        var currentSystem = new RequirementRevision(systemArtifact.Id, 1,
            "The FMS shall sequence oceanic waypoints in filed order.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        db.AddRange(systemArtifact, supersededSystem, currentSystem);
        // The parent has to be a current member of the governed baseline before a child may allocate to it, and
        // the build focal kind resolves its thread through exactly these rows.
        db.Add(new BaselineRequirementSelection(baseline.Id, systemArtifact.Id, currentSystem.Id));
        await db.SaveChangesAsync();

        // Two HLR children of the same System parent: a walk that takes one descendant loses the other.
        var (highLevel, highLevelRevision) = Requirement(db, project.Id, $"HLR-97001", RequirementLevel.HighLevel,
            "The sequencer shall order waypoints by filed sequence.", scr.Id, baseline.Id, now, currentSystem.Id);
        var (sibling, siblingRevision) = Requirement(db, project.Id, $"HLR-97002", RequirementLevel.HighLevel,
            "The sequencer shall reject duplicate waypoints.", scr.Id, baseline.Id, now, currentSystem.Id);
        var (closedChild, closedChildRevision) = Requirement(db, project.Id, $"HLR-97003", RequirementLevel.HighLevel,
            "The sequencer shall log each ordering decision.", scr.Id, baseline.Id, now, currentSystem.Id);

        // An Interface requirement: a level with no verification discipline at all.
        var (interfaceArtifact, interfaceRevision) = Requirement(db, project.Id, $"IRS-97001",
            RequirementLevel.Interface, "The FMS shall expose waypoints on ARINC 429 label 310.",
            scr.Id, baseline.Id, now);

        var traceToSystem = new RequirementTraceLink(project.Id, highLevelRevision.Id, currentSystem.Id,
            RequirementTraceType.AllocatedFrom, "Allocated.", now);
        var siblingTrace = new RequirementTraceLink(project.Id, siblingRevision.Id, currentSystem.Id,
            RequirementTraceType.AllocatedFrom, "Allocated.", now);
        var closedTrace = new RequirementTraceLink(project.Id, closedChildRevision.Id, currentSystem.Id,
            RequirementTraceType.AllocatedFrom, "Allocated.", now);
        var supersededTrace = new RequirementTraceLink(project.Id, currentSystem.Id, supersededSystem.Id,
            RequirementTraceType.DerivedFrom, "Supersedes.", now);
        db.AddRange(traceToSystem, siblingTrace, closedTrace, supersededTrace);

        // Verification: a Case running two procedures for the HLR, and a System procedure covering directly.
        var (caseArtifact, caseRevision) = Verification(db, project.Id, "HLRTC-97001", "Oceanic sequencing case",
            TestProcedureLevel.HighLevel, VerificationArtifactKind.Case, member, now);
        var (firstProcedure, firstRevision) = Verification(db, project.Id, "HLRTP-97001", "Filed order procedure",
            TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure, member, now);
        var (secondProcedure, secondRevision) = Verification(db, project.Id, "HLRTP-97002", "Duplicate rejection procedure",
            TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure, member, now);
        var (systemProcedure, systemProcedureRevision) = Verification(db, project.Id, "SYSTP-97001",
            "System sequencing procedure", TestProcedureLevel.System, VerificationArtifactKind.Procedure, member, now);
        var (closedProcedure, closedProcedureRevision) = Verification(db, project.Id, "HLRTP-97003",
            "Settled procedure", TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure, member, now);
        // A second Case that also runs two of those procedures. It exists because a lifecycle-attached link is
        // revalidation evidence rather than authored parentage, so a procedure needs an authored parent link of
        // its own before a suspect one can be hung beside it.
        var (revisedCase, revisedCaseRevision) = Verification(db, project.Id, "HLRTC-97002",
            "Revised sequencing case", TestProcedureLevel.HighLevel, VerificationArtifactKind.Case, member, now);

        // Suspect coverage: the stored flag on the row itself.
        var suspectCoverage = TestRequirementCoverage.CarriedForward(caseRevision.Id, highLevelRevision.Id,
            "Carried forward from the superseded revision.", now);
        db.Add(suspectCoverage);
        db.Add(new TestRequirementCoverage(systemProcedureRevision.Id, currentSystem.Id));
        db.Add(new TestRequirementCoverage(revisedCaseRevision.Id, highLevelRevision.Id));

        // Authored parentage: every software procedure hangs off the first case by a lifecycle-free link.
        var settledRun = new TestCaseProcedureLink(caseRevision.Id, firstRevision.Id);
        var authoredSecond = new TestCaseProcedureLink(caseRevision.Id, secondRevision.Id);
        var authoredClosed = new TestCaseProcedureLink(caseRevision.Id, closedProcedureRevision.Id);
        // Carried evidence: the revised case re-runs two of them, and those links carry the lifecycles.
        var suspectRun = new TestCaseProcedureLink(revisedCaseRevision.Id, secondRevision.Id);
        var closedRun = new TestCaseProcedureLink(revisedCaseRevision.Id, closedProcedureRevision.Id);
        db.AddRange(settledRun, authoredSecond, authoredClosed, suspectRun, closedRun);

        // Suspect exact link: a live ExactLinkSuspectLifecycle on the Case-to-Procedure link. A link's lifecycle
        // association is immutable once persisted, so it is attached before the link is first saved rather than
        // added afterwards.
        var liveLifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure,
            suspectRun.Id, ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null,
            member, "Case revised.", now, revisedCaseRevision.Id);
        // Attached before saving: the persistence boundary requires a Case-to-Procedure lifecycle to identify its
        // carried link in the same unit of work, so an add-then-attach in two saves is refused.
        db.Add(liveLifecycle);
        suspectRun.AttachExactLinkLifecycle(liveLifecycle.Id);

        // A lifecycle taken all the way to Closed. Closed is the one state the rest of the repository treats as
        // settled, so it must not read as suspect here either.
        var closedLifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure,
            closedRun.Id, ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null,
            member, "Case revised.", now, revisedCaseRevision.Id);
        closedLifecycle.Acknowledge(member, "Reviewed.", now.AddMinutes(1));
        closedLifecycle.RecordResolution(ExactLinkResolutionOutcome.NoDownstreamChangeRequired, member,
            "No change required.", now.AddMinutes(2));
        db.Add(closedLifecycle);
        closedRun.AttachExactLinkLifecycle(closedLifecycle.Id);
        await db.SaveChangesAsync();

        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-7.0.0",
            "Released baseline", member, now);
        // A later build for the same baseline that nothing in this thread ran against.
        var laterBuild = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-7.0.1",
            "Later build", member, now.AddDays(1));
        db.AddRange(build, laterBuild);

        var fail = new TestExecution(project.Id, firstRevision.Id, build.Id, null, TestOutcome.Fail, member,
            "FMS rig", "Ordering did not match the filed sequence.", "evidence/fail.json", now, now, release.Id);
        var pass = new TestExecution(project.Id, firstRevision.Id, build.Id, fail.Id, TestOutcome.Pass, member,
            "FMS rig", "Ordering matched the filed sequence.", "evidence/pass.json",
            now.AddHours(2), now.AddHours(2), release.Id);
        db.AddRange(fail, pass);

        var runFile = new EvidenceRecord(project.Id, "oceanic-run.json", "application/json", 2048, PassHash,
            "evidence/oceanic-run.json", member, now);
        var traceFile = new EvidenceRecord(project.Id, "oceanic-trace.log", "text/plain", 8192, FailHash,
            "evidence/oceanic-trace.log", member, now);
        db.AddRange(runFile, traceFile);
        await db.SaveChangesAsync();
        db.AddRange(new TestExecutionEvidence(pass.Id, runFile.Id), new TestExecutionEvidence(pass.Id, traceFile.Id));

        // A second Project whose records must never appear in this Project's thread.
        var foreignProgram = new ProgramRecord($"Foreign {tag}", $"FN{tag}");
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Other", "Foreign");
        var foreignRelease = new SoftwareRelease(foreignProject.Id, "1.0", false);
        var foreignBaseline = new CandidateBaseline("SW-98.00", 0, foreignProject.Id, foreignRelease.Id, null,
            "Foreign", "cm", now);
        var foreignScr = new SystemChangeRequest("SRCR-98001", 0, foreignProject.Id, foreignRelease.Id,
            "Foreign change", "Problem", "Analysis", "Solution", member, now);
        db.AddRange(foreignProgram, foreignProject, foreignRelease, foreignBaseline, foreignScr);
        var foreignArtifact = new RequirementArtifact(foreignProject.Id, "SR-97900", RequirementLevel.System, now);
        var foreignRevision = new RequirementRevision(foreignArtifact.Id, 0, "Foreign statement.",
            "Rationale", "Test", RequirementRevisionState.Active, foreignScr.Id, foreignBaseline.Id, now);
        db.AddRange(foreignArtifact, foreignRevision);

        await db.SaveChangesAsync();

        return new World(project.Id, systemArtifact.Id, currentSystem.Id, supersededSystem.Id,
            closedProcedureRevision.Id, revisedCaseRevision.Id, highLevelRevision.Id, siblingRevision.Id, interfaceRevision.Id,
            caseRevision.Id, firstRevision.Id, secondRevision.Id, systemProcedureRevision.Id,
            pass.Id, fail.Id, build.Id, laterBuild.Id, foreignRevision.Id, member, outsider);
    }

    private static (RequirementArtifact Artifact, RequirementRevision Revision) Requirement(
        AeroLinkDbContext db, Guid projectId, string baseNumber, RequirementLevel level, string statement,
        Guid changeRequestId, Guid baselineId, DateTimeOffset now, Guid? parentRevisionId = null)
    {
        // A requirement below the top of the ladder must resolve Allocated or Derived exact parents before it can
        // be persisted at all. The fixture allocates honestly rather than leaving the classification unset.
        var artifact = new RequirementArtifact(projectId, baseNumber, level, now);
        var revision = parentRevisionId is Guid parent
            ? new RequirementRevision(artifact.Id, 0, statement, "Rationale", "Test",
                RequirementRevisionState.Active, changeRequestId, baselineId, now,
                RequirementParentKind.Allocated, null, [parent])
            : new RequirementRevision(artifact.Id, 0, statement, "Rationale", "Test",
                RequirementRevisionState.Active, changeRequestId, baselineId, now);
        db.AddRange(artifact, revision);
        return (artifact, revision);
    }

    private static (TestProcedure Artifact, TestProcedureRevision Revision) Verification(
        AeroLinkDbContext db, Guid projectId, string baseNumber, string title, TestProcedureLevel level,
        VerificationArtifactKind kind, string owner, DateTimeOffset now)
    {
        // A software Procedure must declare Allocated or Derived parentage; the domain refuses an unclassified
        // one. Cases and System procedures carry no such requirement, so only the software procedures state it.
        var parentKind = kind == VerificationArtifactKind.Procedure && level != TestProcedureLevel.System
            ? VerificationProcedureParentKind.Allocated
            : VerificationProcedureParentKind.Unspecified;
        var artifact = new TestProcedure(projectId, baseNumber, title, owner, now, level, null, kind, parentKind);
        // A software Procedure header is persisted with a Draft revision 0; approval is a later act, and the
            // persistence boundary refuses a header whose first revision claims to be already approved.
        var revision = new TestProcedureRevision(artifact.Id, 0, "Objective", "Preconditions", "Steps",
            "Expected", TestProcedureState.Draft, owner, now,
            environmentSetup: "FMS integration rig.", testData: "Filed oceanic route.",
            orderedSteps: "Enter the route and observe sequencing.",
            expectedObservations: "Waypoints appear in filed order.", cleanup: "Clear the flight plan.",
            toolingAutomation: "Rig capture harness.", parentKind: parentKind);
        db.AddRange(artifact, revision);
        return (artifact, revision);
    }
}
