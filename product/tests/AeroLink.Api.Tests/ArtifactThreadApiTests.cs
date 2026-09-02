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

        var thread = await ThreadAsync(client, world, focalKind, focalId);

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

        using var response = await client.GetAsync(Url(world, "Baseline", world.BuildId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- 2. all branches survive; no preferred single path -------------------------------------------

    [Fact]
    public async Task Both_covering_procedures_are_returned_not_a_preferred_one()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // The case runs two procedures. The compact path read picks one by tie-breaker; a lane canvas that did
        // the same would silently hide a procedure a reviewer is accountable for.
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.Contains(world.SecondProcedureRevisionId.ToString(), procedures);
    }

    // ---- 3. exact revisions do not collapse ----------------------------------------------------------

    [Fact]
    public async Task Two_revisions_of_one_requirement_are_two_nodes()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.SupersededSystemRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        var thread = await ThreadAsync(client, world, "Procedure", world.SystemProcedureRevisionId);

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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);

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

        var thread = await ThreadAsync(client, world, "Execution", world.PassExecutionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.InterfaceRevisionId);
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

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
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

        using var response = await client.GetAsync(Url(world, "Requirement", world.HighLevelRevisionId));
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
        using var response = await client.GetAsync(Url(world, "Requirement", world.ForeignRevisionId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task No_detail_of_another_project_appears_in_a_thread()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var raw = thread.GetRawText();

        Assert.DoesNotContain("SR-97900", raw);
        Assert.DoesNotContain("Foreign statement", raw);
        Assert.DoesNotContain(world.ForeignRevisionId.ToString(), raw);
    }

    // ---- build scoping ---------------------------------------------------------------------------------

    [Fact]
    public async Task One_configuration_cannot_reach_a_run_recorded_in_another()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        // The same exact procedure revision was run in two builds under two baselines. Each request now carries
        // a fact able to choose between them, and neither may return the other's history. Without a
        // configuration in the contract both runs came back merged, which reports a failure against a build
        // that never saw it.
        var first = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var firstRuns = Nodes(first, "Execution").Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Contains(world.RunInFirstBuildId.ToString(), firstRuns);
        Assert.DoesNotContain(world.RunInSecondBuildId.ToString(), firstRuns);

        var second = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId,
            buildId: world.SecondBuildId, baselineId: world.SecondBaselineId);
        var secondRuns = Nodes(second, "Execution").Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Contains(world.RunInSecondBuildId.ToString(), secondRuns);
        Assert.DoesNotContain(world.RunInFirstBuildId.ToString(), secondRuns);
    }

    [Fact]
    public async Task A_named_build_narrows_the_thread_to_that_build_alone()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId,
            buildId: world.BuildId);
        Assert.Equal([world.BuildId.ToString()],
            Nodes(thread, "Build").Select(x => x.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task A_baseline_from_another_project_is_not_found()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        using var response = await client.GetAsync(
            $"/api/artifact-thread?projectId={world.ProjectId}&baselineId={world.ForeignBaselineId}"
            + $"&focalKind=Requirement&focalId={world.HighLevelRevisionId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- direction purity ------------------------------------------------------------------------------

    [Fact]
    public async Task A_sibling_requirement_is_not_pulled_in_through_the_shared_parent()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var requirements = Nodes(thread, "Requirement").Select(x => x.GetProperty("id").GetString()).ToList();

        // The focal HLR reaches its System ancestor. Turning round at that ancestor and walking back down into
        // its other children would report requirements that are neither upstream nor downstream of what the
        // reader opened — the connected component, not the thread.
        Assert.Contains(world.SystemRevisionId.ToString(), requirements);
        Assert.Contains(world.HighLevelRevisionId.ToString(), requirements);
        Assert.DoesNotContain(world.SiblingHighLevelRevisionId.ToString(), requirements);
    }

    [Fact]
    public async Task A_system_focal_still_reaches_every_child_below_it()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.SystemRevisionId);
        var requirements = Nodes(thread, "Requirement").Select(x => x.GetProperty("id").GetString()).ToList();

        // Direction purity is not narrowing: from the parent, both children are genuinely downstream.
        Assert.Contains(world.HighLevelRevisionId.ToString(), requirements);
        Assert.Contains(world.SiblingHighLevelRevisionId.ToString(), requirements);
    }

    // ---- focal-first, and exact kind --------------------------------------------------------------------

    [Fact]
    public async Task A_procedure_revision_asked_for_as_a_case_fails_closed()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        // Existence alone is not identity. Serving a Procedure under the word Case would place it in the wrong
        // lane and describe it as something the controlled record does not say it is.
        using var wrongKind = await client.GetAsync(Url(world, "Case", world.FirstProcedureRevisionId));
        Assert.Equal(HttpStatusCode.NotFound, wrongKind.StatusCode);

        using var alsoWrong = await client.GetAsync(Url(world, "Procedure", world.CaseRevisionId));
        Assert.Equal(HttpStatusCode.NotFound, alsoWrong.StatusCode);
    }

    // ---- typed relationships ----------------------------------------------------------------------------

    [Fact]
    public async Task An_allocated_link_and_a_derived_link_do_not_arrive_as_the_same_relation()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var edges = thread.GetProperty("edges").EnumerateArray().ToList();

        // RequirementTraceType distinguishes these and they are different controlled claims. Collapsing both to
        // one generic word at a new server boundary loses trace meaning the domain already records.
        var allocated = edges.Single(x =>
            x.GetProperty("fromId").GetString() == world.HighLevelRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.SystemRevisionId.ToString());
        Assert.Equal("allocated from", allocated.GetProperty("relation").GetString());

        var derived = edges.Single(x =>
            x.GetProperty("fromId").GetString() == world.SystemRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.SupersededSystemRevisionId.ToString());
        Assert.Equal("derived from", derived.GetProperty("relation").GetString());
    }

    [Fact]
    public async Task Edges_name_the_kind_of_both_endpoints()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var byId = thread.GetProperty("nodes").EnumerateArray()
            .ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("kind").GetString());

        // Mirrors ChangeRequestTraceEdge: an edge can be understood without resolving both endpoints first.
        Assert.All(thread.GetProperty("edges").EnumerateArray(), edge =>
        {
            Assert.Equal(byId[edge.GetProperty("fromId").GetString()!], edge.GetProperty("fromKind").GetString());
            Assert.Equal(byId[edge.GetProperty("toId").GetString()!], edge.GetProperty("toKind").GetString());
        });
    }

    [Fact]
    public async Task Executions_carry_their_timing_and_builds_carry_their_state()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);

        // The prototype shows execution timing and a released build state. Carrying them here stops 5B needing
        // a second read, or inventing them.
        var execution = Nodes(thread, "Execution")
            .Single(x => x.GetProperty("id").GetString() == world.PassExecutionId.ToString());
        Assert.NotEqual(JsonValueKind.Null, execution.GetProperty("executedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, execution.GetProperty("recordedAt").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(execution.GetProperty("executedBy").GetString()));
        Assert.Equal(JsonValueKind.Null, execution.GetProperty("displayNumber").ValueKind);

        var build = Nodes(thread, "Build").First();
        Assert.False(string.IsNullOrWhiteSpace(build.GetProperty("state").GetString()));
    }

    // ---- partial chains are not unconnected records -----------------------------------------------------

    [Fact]
    public async Task An_execution_keeps_its_procedure_and_build_when_the_procedure_covers_nothing()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Execution", world.LonelyExecutionId,
            baselineId: world.LonelyBaselineId);

        // The execution records its procedure and its build directly. Missing requirement coverage above them
        // stops the chain going farther upstream; it does not delete the facts the record itself carries.
        // Growing the thread only through coverage turned a partial chain into a claim of no relationships.
        Assert.Contains(Nodes(thread, "Procedure"),
            x => x.GetProperty("id").GetString() == world.LonelyProcedureRevisionId.ToString());
        Assert.Contains(Nodes(thread, "Build"),
            x => x.GetProperty("id").GetString() == world.LonelyBuildId.ToString());

        var edges = thread.GetProperty("edges").EnumerateArray().ToList();
        Assert.Contains(edges, x => x.GetProperty("fromId").GetString() == world.LonelyProcedureRevisionId.ToString()
            && x.GetProperty("toId").GetString() == world.LonelyExecutionId.ToString()
            && x.GetProperty("relation").GetString() == "produced");
        Assert.Contains(edges, x => x.GetProperty("fromId").GetString() == world.LonelyExecutionId.ToString()
            && x.GetProperty("toId").GetString() == world.LonelyBuildId.ToString());

        // No requirement was reachable, and none is invented to fill the gap.
        Assert.Empty(Nodes(thread, "Requirement"));
    }

    [Fact]
    public async Task A_build_keeps_the_execution_recorded_against_it_when_coverage_stops_short()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Build", world.LonelyBuildId,
            baselineId: world.LonelyBaselineId);

        Assert.Contains(Nodes(thread, "Execution"),
            x => x.GetProperty("id").GetString() == world.LonelyExecutionId.ToString());
        Assert.Contains(Nodes(thread, "Procedure"),
            x => x.GetProperty("id").GetString() == world.LonelyProcedureRevisionId.ToString());
    }

    [Fact]
    public async Task A_procedure_keeps_its_case_link_when_the_case_covers_nothing()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Procedure", world.UncoveredProcedureRevisionId,
            baselineId: world.LonelyBaselineId);

        // The Case-to-Procedure link is a recorded exact fact. It survives the absence of requirement coverage.
        Assert.Contains(Nodes(thread, "Case"),
            x => x.GetProperty("id").GetString() == world.UncoveredCaseRevisionId.ToString());
        Assert.Contains(thread.GetProperty("edges").EnumerateArray(),
            x => x.GetProperty("fromId").GetString() == world.UncoveredCaseRevisionId.ToString()
                && x.GetProperty("toId").GetString() == world.UncoveredProcedureRevisionId.ToString()
                && x.GetProperty("relation").GetString() == "run by");
    }

    [Fact]
    public async Task A_case_with_no_recorded_relationship_at_all_returns_only_itself()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Case", world.LonelyCaseRevisionId,
            baselineId: world.LonelyBaselineId);

        // The genuine §6.8 unconnected record, and the reason the three tests above matter: this is what a
        // record with no relationships actually looks like, and the others must not be reported the same way.
        Assert.Single(thread.GetProperty("nodes").EnumerateArray());
        Assert.Empty(thread.GetProperty("edges").EnumerateArray());
    }

    // ---- an execution or build anchors its own configuration ---------------------------------------------

    [Theory]
    [InlineData("Execution")]
    [InlineData("Build")]
    public async Task A_result_focal_stays_in_its_own_build_when_none_is_requested(string focalKind)
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var focalId = focalKind == "Execution" ? world.PeerRunInFirstBuildId : world.BuildId;
        var thread = await ThreadAsync(client, world, focalKind, focalId);

        // The same procedure revision was run in two builds of this one baseline. An Execution or a Build names
        // its own configuration, so the peer run must not join the response merely because its build shares the
        // baseline — the caller should not have to pass buildId redundantly to prevent that.
        var runs = Nodes(thread, "Execution").Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Contains(world.PeerRunInFirstBuildId.ToString(), runs);
        Assert.DoesNotContain(world.PeerRunInSecondBuildId.ToString(), runs);
        Assert.Equal([world.BuildId.ToString()],
            Nodes(thread, "Build").Select(x => x.GetProperty("id").GetString()).ToArray());
    }

    // ---- the recorded retest relationship ----------------------------------------------------------------

    [Fact]
    public async Task A_retest_names_the_run_it_repeats()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);

        // TestExecution records RetestOfExecutionId. Returning both runs without it leaves two results with no
        // stated relationship, and a reader inferring one from timestamps would be guessing.
        var retest = thread.GetProperty("edges").EnumerateArray().Single(x =>
            x.GetProperty("relation").GetString() == "retest of");
        Assert.Equal(world.PassExecutionId.ToString(), retest.GetProperty("fromId").GetString());
        Assert.Equal(world.FailExecutionId.ToString(), retest.GetProperty("toId").GetString());
        Assert.Equal("Execution", retest.GetProperty("fromKind").GetString());
        Assert.Equal("Execution", retest.GetProperty("toKind").GetString());
    }

    [Fact]
    public async Task A_run_that_repeats_nothing_states_no_retest()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Execution", world.LonelyExecutionId,
            baselineId: world.LonelyBaselineId);

        // RetestOfExecutionId is null here, and nothing is fabricated to fill it.
        Assert.DoesNotContain(thread.GetProperty("edges").EnumerateArray(),
            x => x.GetProperty("relation").GetString() == "retest of");
    }

    // ---- direction purity on the verification side -------------------------------------------------------

    [Fact]
    public async Task A_procedure_focal_does_not_reach_its_case_s_other_procedures()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Procedure", world.FirstProcedureRevisionId);
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // The parent case is upstream of this procedure and belongs. Turning round at that case and collecting
        // the other procedures it runs is sideways: they are peers, neither upstream nor downstream of the
        // record the reader opened. §6.5 unions two direction-pure walks and does not walk sideways.
        Assert.Contains(Nodes(thread, "Case"),
            x => x.GetProperty("id").GetString() == world.CaseRevisionId.ToString());
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.DoesNotContain(world.SecondProcedureRevisionId.ToString(), procedures);
        Assert.DoesNotContain(world.ClosedProcedureRevisionId.ToString(), procedures);
    }

    [Fact]
    public async Task A_case_focal_does_not_reach_a_peer_case_through_a_shared_procedure()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Case", world.CaseRevisionId);
        var cases = Nodes(thread, "Case").Select(x => x.GetProperty("id").GetString()).ToList();
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // Every procedure this case runs is genuinely downstream and must stay — direction purity is not
        // narrowing. The revised case shares two of those procedures, and reaching it means reversing at a
        // shared procedure into a peer case.
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.Contains(world.SecondProcedureRevisionId.ToString(), procedures);
        Assert.Contains(world.ClosedProcedureRevisionId.ToString(), procedures);
        Assert.DoesNotContain(world.RevisedCaseRevisionId.ToString(), cases);
    }

    [Fact]
    public async Task An_execution_focal_does_not_branch_back_down_into_sibling_procedures()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Execution", world.PassExecutionId);
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // Upstream through its own procedure and that procedure's parent case is valid; the case's other
        // procedures are not part of what this run evidences.
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.DoesNotContain(world.SecondProcedureRevisionId.ToString(), procedures);
    }

    [Fact]
    public async Task A_requirement_focal_still_keeps_every_verification_branch_below_it()
    {
        var world = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, world.Member);

        var thread = await ThreadAsync(client, world, "Requirement", world.HighLevelRevisionId);
        var procedures = Nodes(thread, "Procedure").Select(x => x.GetProperty("id").GetString()).ToList();

        // The requirement is above all of it, so every covering case and every procedure those cases run is
        // genuinely downstream. This is the fan-out direction purity must not cost.
        Assert.Contains(world.FirstProcedureRevisionId.ToString(), procedures);
        Assert.Contains(world.SecondProcedureRevisionId.ToString(), procedures);
        Assert.Contains(Nodes(thread, "Case"),
            x => x.GetProperty("id").GetString() == world.CaseRevisionId.ToString());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static List<JsonElement> Nodes(JsonElement thread, string kind) =>
        thread.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("kind").GetString() == kind).ToList();

    private static string Url(World world, string kind, Guid focalId, Guid? buildId = null,
        Guid? baselineId = null) =>
        $"/api/artifact-thread?projectId={world.ProjectId}&baselineId={baselineId ?? world.BaselineId}"
        + (buildId is Guid build ? $"&buildId={build}" : "")
        + $"&focalKind={kind}&focalId={focalId}";

    private static async Task<JsonElement> ThreadAsync(HttpClient client, World world, string kind, Guid focalId,
        Guid? buildId = null, Guid? baselineId = null)
    {
        using var response = await client.GetAsync(Url(world, kind, focalId, buildId, baselineId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Every successful thread names exactly one focal node, and it is the artifact that was asked for.
        // §4.4 lands the reader on that record selected and expanded, which is impossible if it is missing.
        var focal = body.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("isFocal").GetBoolean()).ToList();
        Assert.Single(focal);
        Assert.Equal(focalId.ToString(), focal[0].GetProperty("id").GetString());
        return body;
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private sealed record World(
        Guid ProjectId, Guid BaselineId, Guid SecondBaselineId, Guid SecondBuildId,
        Guid SharedProcedureRevisionId, Guid RunInFirstBuildId, Guid RunInSecondBuildId,
        Guid LonelyProcedureRevisionId, Guid LonelyCaseRevisionId, Guid LonelyExecutionId, Guid LonelyBuildId,
        Guid LonelyBaselineId, Guid ForeignBaselineId,
        Guid PeerRunInFirstBuildId, Guid PeerRunInSecondBuildId,
        Guid UncoveredCaseRevisionId, Guid UncoveredProcedureRevisionId, Guid SystemArtifactId, Guid SystemRevisionId, Guid SupersededSystemRevisionId,
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

        // The same exact procedure revision run in a second build under a second baseline. This is a
        // legitimate history, and it is the case that proves the thread is pinned rather than merged: nothing
        // in a request for one configuration may reach the other.
        var secondBaseline = new CandidateBaseline("SW-97.01", 0, project.Id, release.Id, baseline.Id,
            "Build 7.1", "cm", now);
        db.Add(secondBaseline);
        var secondBuild = new SoftwareBuild(project.Id, release.Id, secondBaseline.Id, "FMS-7.1.0",
            "Second baseline build", member, now.AddDays(2));
        db.Add(secondBuild);
        var (sharedProcedure, sharedRevision) = Verification(db, project.Id, "HLRTP-97004",
            "Shared across builds", TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure, member, now);
        var sharedRun = new TestCaseProcedureLink(caseRevision.Id, sharedRevision.Id);
        db.Add(sharedRun);
        // No direct coverage row: a software Procedure is refused a direct requirement link and reaches the
        // requirement through its Case, which caseRevision already covers.
        var runInFirstBuild = new TestExecution(project.Id, sharedRevision.Id, build.Id, null, TestOutcome.Pass,
            member, "FMS rig", "Passed on the first build.", "evidence/first.json", now, now, release.Id);
        var runInSecondBuild = new TestExecution(project.Id, sharedRevision.Id, secondBuild.Id, null,
            TestOutcome.Fail, member, "FMS rig", "Failed on the second build.", "evidence/second.json",
            now.AddDays(2), now.AddDays(2), release.Id);
        db.AddRange(runInFirstBuild, runInSecondBuild);

        // A second build of the SAME baseline, running the same exact procedure revision. This is what proves
        // an Execution or Build focal anchors its own configuration without the caller passing buildId.
        var peerBuild = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-7.0.2",
            "Peer build of the same baseline", member, now.AddDays(3));
        db.Add(peerBuild);
        var peerRunInFirstBuild = new TestExecution(project.Id, sharedRevision.Id, build.Id, null,
            TestOutcome.Pass, member, "FMS rig", "Peer run in the first build.", "evidence/peer-first.json",
            now, now, release.Id);
        var peerRunInSecondBuild = new TestExecution(project.Id, sharedRevision.Id, peerBuild.Id, null,
            TestOutcome.Pass, member, "FMS rig", "Peer run in the peer build.", "evidence/peer-second.json",
            now.AddDays(3), now.AddDays(3), release.Id);
        db.AddRange(peerRunInFirstBuild, peerRunInSecondBuild);

        // A Case and Procedure joined by a recorded link, covering no requirement. A partial chain, not an
        // unconnected record — the distinction the thread has to keep.
        var (uncoveredCase, uncoveredCaseRevision) = Verification(db, project.Id, "HLRTC-97008",
            "Uncovered case", TestProcedureLevel.HighLevel, VerificationArtifactKind.Case, member, now);
        var (uncoveredProcedure, uncoveredProcedureRevision) = Verification(db, project.Id, "HLRTP-97008",
            "Uncovered procedure", TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure, member, now);
        db.Add(new TestCaseProcedureLink(uncoveredCaseRevision.Id, uncoveredProcedureRevision.Id));

        // Artifacts with no relationships at all. §6.8 says an unconnected record still renders as a normal
        // card, so each of these must come back as its own thread rather than vanishing from it.
        var (lonelyCase, lonelyCaseRevision) = Verification(db, project.Id, "HLRTC-97009", "Unconnected case",
            TestProcedureLevel.HighLevel, VerificationArtifactKind.Case, member, now);
        var (lonelyProcedure, lonelyProcedureRevision) = Verification(db, project.Id, "SYSTP-97009",
            "Unconnected procedure", TestProcedureLevel.System, VerificationArtifactKind.Procedure, member, now);
        var lonelyBaseline = new CandidateBaseline("SW-97.09", 0, project.Id, release.Id, null,
            "Unconnected build baseline", "cm", now);
        db.Add(lonelyBaseline);
        var lonelyBuild = new SoftwareBuild(project.Id, release.Id, lonelyBaseline.Id, "FMS-7.9.0",
            "Build with nothing recorded against it", member, now);
        db.Add(lonelyBuild);
        var lonelyExecution = new TestExecution(project.Id, lonelyProcedureRevision.Id, lonelyBuild.Id, null,
            TestOutcome.Pass, member, "FMS rig", "Ran, but the procedure covers nothing.",
            "evidence/lonely.json", now, now, release.Id);
        db.Add(lonelyExecution);

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

        return new World(project.Id, baseline.Id, secondBaseline.Id, secondBuild.Id,
            sharedRevision.Id, runInFirstBuild.Id, runInSecondBuild.Id,
            lonelyProcedureRevision.Id, lonelyCaseRevision.Id, lonelyExecution.Id, lonelyBuild.Id,
            lonelyBaseline.Id, foreignBaseline.Id,
            peerRunInFirstBuild.Id, peerRunInSecondBuild.Id,
            uncoveredCaseRevision.Id, uncoveredProcedureRevision.Id, systemArtifact.Id, currentSystem.Id, supersededSystem.Id,
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
