using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

public sealed class ChangeRequestTraceProjectionTests
{
    [Fact]
    public async Task Composes_exact_tcr_origin_and_additional_sources_without_unrelated_nodes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = new SystemChangeRequest("SRCR-07860", 0, fixture.Project.Id, fixture.Release.Id,
            "Root", "P", "A", "S", "author", fixture.Now);
        var additional = new SystemChangeRequest("SRCR-07861", 0, fixture.Project.Id, fixture.Release.Id,
            "Additional", "P", "A", "S", "author", fixture.Now);
        var unrelated = new SystemChangeRequest("SRCR-07862", 0, fixture.Project.Id, fixture.Release.Id,
            "Unrelated", "P", "A", "S", "author", fixture.Now);
        var tcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, root.Id,
            TestChangeReviewDiscipline.System, root.DisplayNumber, fixture.Now,
            baseNumber: "SYSTPCR-07860", revision: 0);
        tcr.IncludeChangeRequest("test.engineer", additional.Id, additional.DisplayNumber, fixture.Now);
        var report = new ProblemReport(fixture.Project.Id, "PR-07860", "Unrelated problem report",
            "The field report is not a CR source.", "Analysis", "author", fixture.Now);
        var problemTcr = TestChangeReview.FromProblemReport(fixture.Project.Id, fixture.Release.Id, report.Id,
            TestChangeReviewDiscipline.System, report.DisplayNumber, fixture.Now,
            baseNumber: "SYSTPCR-07861", revision: 0);
        fixture.Db.AddRange(root, additional, unrelated, tcr, report, problemTcr);
        await fixture.Db.SaveChangesAsync();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(result);
        var tcrNode = Assert.Single(result!.Nodes, x => x.Kind == "TestChangeRequest" && x.Id == tcr.Id);
        Assert.Equal("SYSTPCR-07860.00", tcrNode.DisplayNumber);
        Assert.Contains(result.Edges, x => x.FromId == root.Id && x.ToId == tcr.Id
            && x.Provenance.Any(p => p.Kind == "TcrOrigin"));
        Assert.Contains(result.Edges, x => x.FromId == additional.Id && x.ToId == tcr.Id
            && x.Provenance.Any(p => p.Kind == "TcrAdditionalSource"));
        Assert.DoesNotContain(result.Nodes, x => x.Id == unrelated.Id && x.Kind == "ChangeRequest");
        Assert.DoesNotContain(result.Nodes, x => x.Id == problemTcr.Id && x.Kind == "TestChangeRequest");
        Assert.All(result.Nodes.Where(x => x.Kind == "ChangeRequest"), x => Assert.NotEqual(unrelated.Id, x.Id));

        var selectedTcr = await ChangeRequestTraceProjection.ForTestChangeReviewAsync(
            fixture.Db, fixture.Project.Id, tcr.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(selectedTcr);
        Assert.Equal(tcr.Id, selectedTcr!.RootArtifactId);
        Assert.Equal("TestChangeRequest", selectedTcr.RootArtifactKind);
        Assert.Equal(Guid.Empty, selectedTcr.RootChangeRequestId);
        Assert.Contains(selectedTcr.Nodes, x => x.Kind == "TestChangeRequest" && x.Id == tcr.Id);
        Assert.Contains(selectedTcr.Nodes, x => x.Kind == "ChangeRequest" && x.Id == root.Id);
        Assert.Contains(selectedTcr.Nodes, x => x.Kind == "ChangeRequest" && x.Id == additional.Id);
        Assert.Contains(selectedTcr.Edges, x => x.FromId == root.Id && x.ToId == tcr.Id);
        Assert.Contains(selectedTcr.Edges, x => x.FromId == additional.Id && x.ToId == tcr.Id);

        var standalone = await ChangeRequestTraceProjection.ForTestChangeReviewAsync(
            fixture.Db, fixture.Project.Id, problemTcr.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(standalone);
        Assert.Equal(problemTcr.Id, standalone!.RootArtifactId);
        Assert.Equal("TestChangeRequest", standalone.RootArtifactKind);
        Assert.Null(standalone.State);
        Assert.Single(standalone.Nodes, x => x.Kind == "TestChangeRequest" && x.Id == problemTcr.Id);
        Assert.Empty(standalone.Edges);
    }

    [Fact]
    public async Task Superseded_assessment_links_are_not_live_trace_edges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = new SystemChangeRequest("SRCR-07865", 0, fixture.Project.Id, fixture.Release.Id,
            "Upstream", "P", "A", "S", "author", fixture.Now);
        var child = new SystemChangeRequest("HLRCR-07866", 0, fixture.Project.Id, fixture.Release.Id,
            "Downstream", "P", "A", "S", "author", fixture.Now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var assessment = new DownstreamChangeAssessment(fixture.Project.Id, fixture.Release.Id, source.Id,
            source.DisplayNumber, RequirementLevel.HighLevel, fixture.Now);
        assessment.Assign("author", "author", fixture.Now);
        assessment.RecordChangeRequired("author", fixture.Now);
        assessment.LinkChangeRequest("author", child.Id, child.DisplayNumber, fixture.Now);
        fixture.Db.AddRange(source, child, assessment);
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE downstream_change_assessments SET State = {DownstreamAssessmentState.Superseded.ToString()} WHERE Id = {assessment.Id}");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET State = {ChangeRequestState.Approved.ToString()} WHERE Id = {child.Id}");
        fixture.Db.ChangeTracker.Clear();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, child.Id, LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Edges, x => x.Provenance.Any(p => p.Kind == "AssessmentDerived"));
        Assert.DoesNotContain(result.Nodes, x => x.Kind == "ChangeRequest" && x.Id == source.Id);
        var state = (await ChangeRequestTraceProjection.StatesAsync(fixture.Db, fixture.Project.Id,
            [child.Id], LegacyLadderPolicy.Instance, CancellationToken.None))[child.Id];
        Assert.Equal("UpstreamGap", state.Upstream);
    }

    [Fact]
    public async Task Assessment_edges_require_current_effective_direct_parent_and_build()
    {
        await using var fixture = await Fixture.CreateAsync();
        var earlierRelease = new SoftwareRelease(fixture.Project.Id, "0.9", false);
        var child = new SystemChangeRequest("HLRCR-07870", 0, fixture.Project.Id, fixture.Release.Id,
            "Current HLR", "P", "A", "S", "author", fixture.Now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var wrongTargetLevel = new SystemChangeRequest("SRCR-07871", 0, fixture.Project.Id,
            fixture.Release.Id, "Wrong target level source", "P", "A", "S", "author", fixture.Now);
        var wrongDirectParent = new SystemChangeRequest("HLRCR-07872", 0, fixture.Project.Id,
            fixture.Release.Id, "Wrong direct parent source", "P", "A", "S", "author", fixture.Now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var earlierSource = new SystemChangeRequest("SRCR-07873", 0, fixture.Project.Id,
            earlierRelease.Id, "Earlier source", "P", "A", "S", "author", fixture.Now);
        DownstreamChangeAssessment Assessment(SystemChangeRequest source, Guid releaseId,
            RequirementLevel targetLevel)
        {
            var assessment = new DownstreamChangeAssessment(fixture.Project.Id, releaseId, source.Id,
                source.DisplayNumber, targetLevel, fixture.Now);
            assessment.Assign("author", "author", fixture.Now);
            assessment.RecordChangeRequired("author", fixture.Now);
            assessment.LinkChangeRequest("author", child.Id, child.DisplayNumber, fixture.Now);
            return assessment;
        }
        var mismatchedLevel = Assessment(wrongTargetLevel, fixture.Release.Id, RequirementLevel.LowLevel);
        var wrongParent = Assessment(wrongDirectParent, fixture.Release.Id, RequirementLevel.HighLevel);
        var wrongBuild = Assessment(earlierSource, earlierRelease.Id, RequirementLevel.HighLevel);
        fixture.Db.AddRange(earlierRelease, child, wrongTargetLevel, wrongDirectParent, earlierSource,
            mismatchedLevel, wrongParent, wrongBuild);
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET State = {ChangeRequestState.Approved.ToString()} WHERE Id = {child.Id}");
        fixture.Db.ChangeTracker.Clear();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, child.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        var state = (await ChangeRequestTraceProjection.StatesAsync(fixture.Db, fixture.Project.Id,
            [child.Id], LegacyLadderPolicy.Instance, CancellationToken.None))[child.Id];

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Edges, x => x.Provenance.Any(p => p.Kind == "AssessmentDerived"));
        Assert.DoesNotContain(result.Nodes, x => x.Id == wrongTargetLevel.Id || x.Id == wrongDirectParent.Id
            || x.Id == earlierSource.Id);
        Assert.Equal("UpstreamGap", state.Upstream);
    }

    [Fact]
    public async Task Requirement_trace_walk_ignores_open_suspect_lifecycle_but_includes_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = new SystemChangeRequest("SRCR-07874", 0, fixture.Project.Id, fixture.Release.Id,
            "Requirement trace root", "P", "A", "S", "author", fixture.Now);
        var baseline = new CandidateBaseline("SW-07874", 0, fixture.Project.Id, fixture.Release.Id, null,
            "Trace lifecycle baseline", "author", fixture.Now);
        var baselineImport = new BaselineImport(fixture.Project.Id, "Legacy", "1", "Imported trace", fixture.Now,
            "trace.json", new string('a', 64), 1, ImportedArtifactKinds.Requirements, "author", fixture.Now,
            "author", fixture.Now);
        var sourceArtifact = new RequirementArtifact(fixture.Project.Id, "SYSR-07874", RequirementLevel.System,
            fixture.Now);
        var sourceRevision = new RequirementRevision(sourceArtifact.Id, 0, "Source requirement", "Rationale",
            "Test", RequirementRevisionState.Active, root.Id, baseline.Id, fixture.Now);
        var suspectArtifact = new RequirementArtifact(fixture.Project.Id, "SYSR-07875", RequirementLevel.System,
            fixture.Now);
        var suspectRevision = RequirementRevision.FromExternalSourcePackage(suspectArtifact.Id, 0,
            "Suspect target", "Imported", RequirementRevisionState.Active, baselineImport.Id,
            baseline.Id, fixture.Now);
        var closedArtifact = new RequirementArtifact(fixture.Project.Id, "SYSR-07876", RequirementLevel.System,
            fixture.Now);
        var closedRevision = RequirementRevision.FromExternalSourcePackage(closedArtifact.Id, 0,
            "Closed target", "Imported", RequirementRevisionState.Active, baselineImport.Id,
            baseline.Id, fixture.Now);
        var acknowledgedArtifact = new RequirementArtifact(fixture.Project.Id, "SYSR-07877", RequirementLevel.System,
            fixture.Now);
        var acknowledgedRevision = RequirementRevision.FromExternalSourcePackage(acknowledgedArtifact.Id, 0,
            "Acknowledged target", "Imported", RequirementRevisionState.Active, baselineImport.Id,
            baseline.Id, fixture.Now);
        var changeRequiredArtifact = new RequirementArtifact(fixture.Project.Id, "SYSR-07878", RequirementLevel.System,
            fixture.Now);
        var changeRequiredRevision = RequirementRevision.FromExternalSourcePackage(changeRequiredArtifact.Id, 0,
            "Change-required target", "Imported", RequirementRevisionState.Active, baselineImport.Id,
            baseline.Id, fixture.Now);
        var suspectLink = new RequirementTraceLink(fixture.Project.Id, sourceRevision.Id, suspectRevision.Id,
            RequirementTraceType.DerivedFrom, "Open suspect", fixture.Now);
        var closedLink = new RequirementTraceLink(fixture.Project.Id, sourceRevision.Id, closedRevision.Id,
            RequirementTraceType.DerivedFrom, "Closed suspect", fixture.Now);
        var suspectLifecycle = ExactLinkSuspectLifecycle.Raise(fixture.Project.Id, ExactLinkKind.RequirementTrace,
            suspectLink.Id, ExactLinkLifecycleCauseKind.ExternalBaselineImport, null, baselineImport.Id,
            "author", "The imported source changed.", fixture.Now);
        suspectLink.AttachExactLinkLifecycle(suspectLifecycle.Id);
        var closedLifecycle = ExactLinkSuspectLifecycle.Raise(fixture.Project.Id, ExactLinkKind.RequirementTrace,
            closedLink.Id, ExactLinkLifecycleCauseKind.ExternalBaselineImport, null, baselineImport.Id,
            "author", "The imported source changed.", fixture.Now);
        closedLifecycle.RecordResolution(ExactLinkResolutionOutcome.NoDownstreamChangeRequired,
            "author", "Reviewed exact imported source.", fixture.Now);
        closedLink.AttachExactLinkLifecycle(closedLifecycle.Id);
        var acknowledgedLink = new RequirementTraceLink(fixture.Project.Id, sourceRevision.Id,
            acknowledgedRevision.Id, RequirementTraceType.DerivedFrom, "Acknowledged suspect", fixture.Now);
        var acknowledgedLifecycle = ExactLinkSuspectLifecycle.Raise(fixture.Project.Id,
            ExactLinkKind.RequirementTrace, acknowledgedLink.Id, ExactLinkLifecycleCauseKind.ExternalBaselineImport,
            null, baselineImport.Id, "author", "The imported source changed.", fixture.Now);
        acknowledgedLifecycle.Acknowledge("author", "Acknowledged for investigation.", fixture.Now);
        acknowledgedLink.AttachExactLinkLifecycle(acknowledgedLifecycle.Id);
        var changeRequiredLink = new RequirementTraceLink(fixture.Project.Id, sourceRevision.Id,
            changeRequiredRevision.Id, RequirementTraceType.DerivedFrom, "Change required suspect", fixture.Now);
        var changeRequiredLifecycle = ExactLinkSuspectLifecycle.Raise(fixture.Project.Id,
            ExactLinkKind.RequirementTrace, changeRequiredLink.Id, ExactLinkLifecycleCauseKind.ExternalBaselineImport,
            null, baselineImport.Id, "author", "The imported source changed.", fixture.Now);
        changeRequiredLifecycle.RecordResolution(
            ExactLinkResolutionOutcome.DownstreamChangeRequiredNotYetApproved, "author",
            "A downstream change is required.", fixture.Now);
        changeRequiredLink.AttachExactLinkLifecycle(changeRequiredLifecycle.Id);
        var excludedCode = new CodeTraceabilityRecord(fixture.Project.Id, fixture.Release.Id, suspectArtifact.Id,
            suspectRevision.Id, CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
            "No code change is required for the excluded suspect revision.", false, "author", fixture.Now);
        fixture.Db.AddRange(root, baseline, baselineImport, sourceArtifact, sourceRevision, suspectArtifact,
            suspectRevision, closedArtifact, closedRevision, acknowledgedArtifact, acknowledgedRevision,
            changeRequiredArtifact, changeRequiredRevision, suspectLink, suspectLifecycle, closedLink,
            closedLifecycle, acknowledgedLink, acknowledgedLifecycle, changeRequiredLink, changeRequiredLifecycle,
            excludedCode);
        await fixture.Db.SaveChangesAsync();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Nodes, x => x.Id == closedRevision.Id);
        Assert.DoesNotContain(result.Nodes, x => x.Id == suspectRevision.Id);
        Assert.Contains(result.Edges, x => x.Relation == "RequirementTrace"
            && x.ToId == closedRevision.Id);
        Assert.DoesNotContain(result.Edges, x => x.Relation == "RequirementTrace"
            && x.ToId == suspectRevision.Id);
        Assert.DoesNotContain(result.Nodes, x => x.Id == acknowledgedRevision.Id || x.Id == changeRequiredRevision.Id
            || x.Id == excludedCode.Id);
    }

    [Fact]
    public async Task Follows_exact_requirement_and_code_sources_without_fabricating_external_change_request_edges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = new SystemChangeRequest("SRCR-07863", 0, fixture.Project.Id, fixture.Release.Id,
            "Root with requirement evidence", "P", "A", "S", "author", fixture.Now);
        var baseline = new CandidateBaseline("SW-07863", 0, fixture.Project.Id, fixture.Release.Id, null,
            "Trace evidence baseline", "author", fixture.Now);
        var import = new BaselineImport(fixture.Project.Id, "Legacy", "1", "Imported trace", fixture.Now,
            "trace.json", new string('a', 64), 1, ImportedArtifactKinds.Requirements, "author", fixture.Now, "author", fixture.Now);
        var requirement = new RequirementArtifact(fixture.Project.Id, "SYSR-07863", RequirementLevel.System, fixture.Now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0, "The system shall remain traceable.",
            "Evidence", "Test", RequirementRevisionState.Active, root.Id, baseline.Id, fixture.Now);
        var external = new RequirementArtifact(fixture.Project.Id, "SYSR-07864", RequirementLevel.System, fixture.Now);
        var externalRevision = RequirementRevision.FromExternalSourcePackage(external.Id, 0,
            "An externally supplied exact revision.", "Imported", RequirementRevisionState.Active,
            import.Id, baseline.Id, fixture.Now);
        var requirementTrace = new RequirementTraceLink(fixture.Project.Id, requirementRevision.Id,
            externalRevision.Id, RequirementTraceType.DerivedFrom, "The imported revision is exact evidence.", fixture.Now);
        var code = new CodeTraceabilityRecord(fixture.Project.Id, fixture.Release.Id, external.Id,
            externalRevision.Id, CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
            "No code change is required for this imported revision.", false, "author", fixture.Now);
        fixture.Db.AddRange(root, baseline, import, requirement, requirementRevision, external, externalRevision, requirementTrace, code);
        await fixture.Db.SaveChangesAsync();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(result);
        var exactRequirementNode = Assert.Single(result!.Nodes,
            x => x.Kind == "RequirementRevision" && x.Id == requirementRevision.Id);
        Assert.Equal(requirement.Id, exactRequirementNode.ArtifactId);
        Assert.Equal(baseline.Id, exactRequirementNode.EffectiveBaselineId);
        Assert.Contains(result.Nodes, x => x.Kind == "RequirementRevision" && x.Id == externalRevision.Id);
        Assert.Contains(result.Nodes, x => x.Kind == "CodeTraceability" && x.Id == code.Id);
        Assert.Contains(result.Edges, x => x.FromId == externalRevision.Id && x.ToId == code.Id
            && x.Relation == "RequirementCodeEvidence");
        Assert.DoesNotContain(result.Edges, x => x.ToId == externalRevision.Id
            && x.Relation == "OwnsRequirementRevision");
    }

    [Fact]
    public async Task Register_state_uses_a_fixed_set_based_query_shape_for_fifty_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requests = Enumerable.Range(0, 50).Select(index => new SystemChangeRequest(
            $"SRCR-079{index:D2}", 0, fixture.Project.Id, fixture.Release.Id,
            $"State row {index}", "P", "A", "S", "author", fixture.Now)).ToList();
        fixture.Db.AddRange(requests);
        await fixture.Db.SaveChangesAsync();
        fixture.QueryCounter.Reset();

        var states = await ChangeRequestTraceProjection.StatesAsync(fixture.Db, fixture.Project.Id,
            requests.Select(x => x.Id).ToArray(), LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.Equal(50, states.Count);
        Assert.All(states.Values, state => Assert.Equal("Root", state.Overall));
        Assert.Equal(7, fixture.QueryCounter.Count);
        fixture.QueryCounter.Reset();
        await ChangeRequestTraceProjection.StatesAsync(fixture.Db, fixture.Project.Id,
            [requests[0].Id], LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.Equal(7, fixture.QueryCounter.Count);
    }

    [Fact]
    public async Task Register_state_matrix_preserves_unlinked_decisions_and_target_viability()
    {
        await using var fixture = await Fixture.CreateAsync();
        async Task<SystemChangeRequest> Source(string number, ChangeRequestType type = ChangeRequestType.System,
            RequirementLevel? level = null, int revision = 0)
        {
            var source = new SystemChangeRequest(number, revision, fixture.Project.Id, fixture.Release.Id,
                number, "P", "A", "S", "author", fixture.Now, type, softwareLevel: level);
            fixture.Db.Add(source);
            await fixture.Db.SaveChangesAsync();
            return source;
        }
        async Task SetState(Guid id, ChangeRequestState state)
        {
            await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE system_change_requests SET State = {state.ToString()} WHERE Id = {id}");
            fixture.Db.ChangeTracker.Clear();
        }
        async Task<DownstreamChangeAssessment> Assessment(SystemChangeRequest source,
            DownstreamAssessmentOutcome outcome, DownstreamAssessmentState? state = null)
        {
            var assessment = new DownstreamChangeAssessment(fixture.Project.Id, fixture.Release.Id, source.Id,
                source.DisplayNumber, RequirementLevel.HighLevel, fixture.Now);
            fixture.Db.Add(assessment);
            await fixture.Db.SaveChangesAsync();
            if (outcome == DownstreamAssessmentOutcome.ChangeRequired)
            {
                assessment.Assign("author", "author", fixture.Now);
                assessment.RecordChangeRequired("author", fixture.Now);
            }
            else if (outcome == DownstreamAssessmentOutcome.NoChangeRequired)
            {
                assessment.Assign("author", "author", fixture.Now);
                assessment.RecordNoChange("author", "No downstream change is required.", fixture.Now);
                if (state == DownstreamAssessmentState.InReview)
                    assessment.Submit("author", "approver", fixture.Now);
                else if (state == DownstreamAssessmentState.Approved)
                {
                    assessment.Submit("author", "approver", fixture.Now);
                    assessment.Approve("approver", fixture.Now);
                }
            }
            await fixture.Db.SaveChangesAsync();
            if (state == DownstreamAssessmentState.Superseded)
            {
                await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE downstream_change_assessments SET State = {state.ToString()} WHERE Id = {assessment.Id}");
                fixture.Db.ChangeTracker.Clear();
            }
            return assessment;
        }

        var root = await Source("SRCR-08000");
        var draft = await Source("HLRCR-08001", ChangeRequestType.Software, RequirementLevel.HighLevel);
        var historical = await Source("HLRCR-08002", ChangeRequestType.Software, RequirementLevel.HighLevel);
        await SetState(historical.Id, ChangeRequestState.Approved);
        var pendingSource = await Source("SRCR-08003");
        await SetState(pendingSource.Id, ChangeRequestState.Approved);
        var pending = await Assessment(pendingSource, DownstreamAssessmentOutcome.Pending);
        var requiredSource = await Source("SRCR-08004");
        await SetState(requiredSource.Id, ChangeRequestState.Approved);
        var required = await Assessment(requiredSource, DownstreamAssessmentOutcome.ChangeRequired);
        var openSource = await Source("SRCR-08005");
        await SetState(openSource.Id, ChangeRequestState.Approved);
        var open = await Assessment(openSource, DownstreamAssessmentOutcome.NoChangeRequired, DownstreamAssessmentState.Open);
        var reviewSource = await Source("SRCR-08006");
        await SetState(reviewSource.Id, ChangeRequestState.Approved);
        var review = await Assessment(reviewSource, DownstreamAssessmentOutcome.NoChangeRequired, DownstreamAssessmentState.InReview);
        var approvedSource = await Source("SRCR-08007");
        await SetState(approvedSource.Id, ChangeRequestState.Approved);
        var approved = await Assessment(approvedSource, DownstreamAssessmentOutcome.NoChangeRequired, DownstreamAssessmentState.Approved);
        var linkedSource = await Source("SRCR-08008");
        await SetState(linkedSource.Id, ChangeRequestState.Approved);
        var linked = await Assessment(linkedSource, DownstreamAssessmentOutcome.ChangeRequired);
        var viableTarget = await Source("HLRCR-08009", ChangeRequestType.Software, RequirementLevel.HighLevel);
        linked.LinkChangeRequest("author", viableTarget.Id, viableTarget.DisplayNumber, fixture.Now);
        await fixture.Db.SaveChangesAsync();
        var withdrawnSource = await Source("SRCR-08010");
        await SetState(withdrawnSource.Id, ChangeRequestState.Approved);
        var withdrawn = await Assessment(withdrawnSource, DownstreamAssessmentOutcome.ChangeRequired);
        var withdrawnTarget = await Source("HLRCR-08011", ChangeRequestType.Software, RequirementLevel.HighLevel);
        withdrawn.LinkChangeRequest("author", withdrawnTarget.Id, withdrawnTarget.DisplayNumber, fixture.Now);
        await fixture.Db.SaveChangesAsync();
        await SetState(withdrawnTarget.Id, ChangeRequestState.Withdrawn);
        var supersededSource = await Source("SRCR-08012");
        await SetState(supersededSource.Id, ChangeRequestState.Approved);
        var superseded = await Assessment(supersededSource, DownstreamAssessmentOutcome.ChangeRequired);
        var supersededTarget = await Source("HLRCR-08013", ChangeRequestType.Software, RequirementLevel.HighLevel);
        var successorTarget = await Source("HLRCR-08013", ChangeRequestType.Software, RequirementLevel.HighLevel, 1);
        superseded.LinkChangeRequest("author", supersededTarget.Id, supersededTarget.DisplayNumber, fixture.Now);
        await fixture.Db.SaveChangesAsync();
        await SetState(supersededTarget.Id, ChangeRequestState.Approved);
        await SetState(successorTarget.Id, ChangeRequestState.Approved);
        var deferredSource = await Source("SRCR-08014");
        await SetState(deferredSource.Id, ChangeRequestState.Approved);
        var deferred = await Assessment(deferredSource, DownstreamAssessmentOutcome.ChangeRequired);
        var deferredTarget = await Source("HLRCR-08015", ChangeRequestType.Software, RequirementLevel.HighLevel);
        deferred.LinkChangeRequest("author", deferredTarget.Id, deferredTarget.DisplayNumber, fixture.Now);
        await fixture.Db.SaveChangesAsync();
        await SetState(deferredTarget.Id, ChangeRequestState.Deferred);
        var supersededAssessmentSource = await Source("SRCR-08016");
        await SetState(supersededAssessmentSource.Id, ChangeRequestState.Approved);
        var supersededAssessment = await Assessment(supersededAssessmentSource,
            DownstreamAssessmentOutcome.Pending, DownstreamAssessmentState.Superseded);

        var ids = new[] { root.Id, draft.Id, historical.Id, pendingSource.Id, requiredSource.Id, openSource.Id,
            reviewSource.Id, approvedSource.Id, linkedSource.Id, withdrawnSource.Id, supersededSource.Id,
            deferredSource.Id, supersededAssessmentSource.Id };
        var states = await ChangeRequestTraceProjection.StatesAsync(fixture.Db, fixture.Project.Id, ids,
            LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.Equal("Root", states[root.Id].Overall);
        Assert.Equal("IncompleteAuthoring", states[draft.Id].Upstream);
        Assert.Contains("complete it before review", states[draft.Id].Warnings.Single(), StringComparison.Ordinal);
        Assert.Equal("UpstreamGap", states[historical.Id].Upstream);
        Assert.Equal("Pending", states[pendingSource.Id].Downstream);
        Assert.Equal("ActionGap", states[requiredSource.Id].Downstream);
        Assert.Equal("ApprovalPending", states[openSource.Id].Downstream);
        Assert.Equal("ApprovalPending", states[reviewSource.Id].Downstream);
        Assert.Equal("Satisfied", states[approvedSource.Id].Downstream);
        Assert.Equal("Linked", states[linkedSource.Id].Downstream);
        Assert.Equal("ActionGap", states[withdrawnSource.Id].Downstream);
        Assert.Equal("ActionGap", states[supersededSource.Id].Downstream);
        Assert.Equal("Deferred", states[deferredSource.Id].Downstream);
        Assert.Equal("NoDownstreamWork", states[supersededAssessmentSource.Id].Downstream);
    }

    [Fact]
    public async Task Cyclic_change_request_component_is_complete_deterministic_and_folds_dual_provenance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = new SystemChangeRequest("SRCR-08100", 0, fixture.Project.Id, fixture.Release.Id,
            "First", "P", "A", "S", "author", fixture.Now);
        var second = new SystemChangeRequest("HLRCR-08101", 0, fixture.Project.Id, fixture.Release.Id,
            "Second", "P", "A", "S", "author", fixture.Now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        var third = new SystemChangeRequest("SRCR-08102", 0, fixture.Project.Id, fixture.Release.Id,
            "Third", "P", "A", "S", "author", fixture.Now);
        fixture.Db.AddRange(first, second, third);
        await fixture.Db.SaveChangesAsync();
        var approvedState = ChangeRequestState.Approved.ToString();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET State = {approvedState} WHERE Id = {first.Id}");
        fixture.Db.ChangeTracker.Clear();
        var assessment = new DownstreamChangeAssessment(fixture.Project.Id, fixture.Release.Id, first.Id,
            first.DisplayNumber, RequirementLevel.HighLevel, fixture.Now);
        assessment.Assign("author", "author", fixture.Now);
        assessment.RecordChangeRequired("author", fixture.Now);
        assessment.LinkChangeRequest("author", second.Id, second.DisplayNumber, fixture.Now);
        fixture.Db.Add(assessment);
        await fixture.Db.SaveChangesAsync();

        async Task AddRawLink(Guid child, Guid parent)
        {
            await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO change_request_upstream_links
                    (Id, ChangeRequestId, UpstreamChangeRequestId, UpstreamDisplayNumber,
                     UpstreamBuildId, UpstreamBuildVersion, Rationale, ActorId, StatedAt)
                VALUES ({Guid.NewGuid()}, {child}, {parent}, {"exact parent"}, {fixture.Release.Id},
                        {"1.0"}, {"Cycle fixture"}, {"author"}, {fixture.Now})
                """);
        }
        await AddRawLink(second.Id, first.Id);
        await AddRawLink(first.Id, third.Id);
        await AddRawLink(third.Id, second.Id);
        fixture.Db.ChangeTracker.Clear();

        var firstProjection = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, second.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        var secondProjection = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, second.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(firstProjection);
        Assert.Equal(3, firstProjection!.Nodes.Count(x => x.Kind == "ChangeRequest"));
        Assert.Equal(JsonSerializer.Serialize(firstProjection), JsonSerializer.Serialize(secondProjection));
        var dual = Assert.Single(firstProjection.Edges, x => x.FromId == second.Id && x.ToId == first.Id);
        var kinds = dual.Provenance.Select(x => x.Kind).ToHashSet();
        Assert.Contains("AuthorStated", kinds);
        Assert.Contains("AssessmentDerived", kinds);
    }

    [Fact]
    public async Task Typed_fixpoint_reaches_case_procedure_additional_change_and_requirement_chain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = new SystemChangeRequest("SRCR-08200", 0, fixture.Project.Id, fixture.Release.Id,
            "Root", "P", "A", "S", "author", fixture.Now);
        var additional = new SystemChangeRequest("SRCR-08201", 0, fixture.Project.Id, fixture.Release.Id,
            "Additional source", "P", "A", "S", "author", fixture.Now);
        var unrelated = new SystemChangeRequest("SRCR-08202", 0, fixture.Project.Id, fixture.Release.Id,
            "Unrelated", "P", "A", "S", "author", fixture.Now);
        var baseline = new CandidateBaseline("SW-08200", 0, fixture.Project.Id, fixture.Release.Id, null,
            "Fixpoint baseline", "author", fixture.Now);
        fixture.Db.AddRange(root, additional, unrelated, baseline);
        await fixture.Db.SaveChangesAsync();
        var approved = ChangeRequestState.Approved.ToString();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE system_change_requests SET State = {approved} WHERE Id = {root.Id}");
        fixture.Db.ChangeTracker.Clear();

        var caseTcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, root.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            root.DisplayNumber, fixture.Now, baseNumber: "HLRTCCR-08200", revision: 0);
        fixture.Db.Add(caseTcr);
        await fixture.Db.SaveChangesAsync();
        var tcrApproved = TestChangeReviewState.Approved.ToString();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE test_change_reviews SET State = {tcrApproved} WHERE Id = {caseTcr.Id}");
        fixture.Db.ChangeTracker.Clear();
        var procedureTcr = TestChangeReview.FromCaseReview(fixture.Project.Id, fixture.Release.Id, caseTcr.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure),
            caseTcr.DisplayNumber, fixture.Now, baseNumber: "HLRTPCR-08200", revision: 0);
        procedureTcr.IncludeChangeRequest("author", additional.Id, additional.DisplayNumber, fixture.Now);
        var requirement = new RequirementArtifact(fixture.Project.Id, "SYSR-08201", RequirementLevel.System, fixture.Now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The additional source shall be implemented.", "Fixpoint evidence", "Test",
            RequirementRevisionState.Active, additional.Id, baseline.Id, fixture.Now);
        fixture.Db.AddRange(procedureTcr, requirement, requirementRevision);
        await fixture.Db.SaveChangesAsync();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains(result!.Nodes, x => x.Kind == "TestChangeRequest" && x.Id == caseTcr.Id);
        Assert.Contains(result.Nodes, x => x.Kind == "TestChangeRequest" && x.Id == procedureTcr.Id);
        Assert.Contains(result.Nodes, x => x.Kind == "ChangeRequest" && x.Id == additional.Id);
        Assert.Contains(result.Nodes, x => x.Kind == "RequirementRevision" && x.Id == requirementRevision.Id);
        Assert.DoesNotContain(result.Nodes, x => x.Id == unrelated.Id);
    }

    [Fact]
    public async Task Case_change_assessment_and_problem_report_origins_keep_their_exact_discriminators()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = new SystemChangeRequest("SRCR-08210", 0, fixture.Project.Id, fixture.Release.Id,
            "Root", "P", "A", "S", "author", fixture.Now);
        var report = new ProblemReport(fixture.Project.Id, "PR-08210", "Field report",
            "A Problem Report is not a CR origin.", "Analysis", "author", fixture.Now);
        fixture.Db.AddRange(root, report);
        await fixture.Db.SaveChangesAsync();
        var caseKey = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
            VerificationArtifactKind.Case);
        var procedureKey = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
            VerificationArtifactKind.Procedure);
        var caseTcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, root.Id, caseKey,
            root.DisplayNumber, fixture.Now, baseNumber: "HLRTCCR-08210", revision: 0);
        caseTcr.RecordTestChangeRequired("author", fixture.Now);
        var caseChange = caseTcr.AddProcedureChange("author", new TestProcedureChangeDraft(
            "HLRTC-08210", 0, TestProcedureLevel.HighLevel, TestProcedureChangeKind.Retire,
            "", "", "", "", "", "Retire the obsolete Case."), fixture.Now);
        fixture.Db.Add(caseTcr);
        await fixture.Db.SaveChangesAsync();
        var approved = TestChangeReviewState.Approved.ToString();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE test_change_reviews SET State = {approved} WHERE Id = {caseTcr.Id}");
        fixture.Db.ChangeTracker.Clear();

        var assessment = VerificationImpactItem.ForIntroducedRequirement(fixture.Project.Id,
            fixture.Release.Id, root.Id, caseTcr.Id, Guid.NewGuid(), "HLR-08210.00", "Test", fixture.Now);
        fixture.Db.Add(assessment);
        await fixture.Db.SaveChangesAsync();
        var fromChange = TestChangeReview.FromCaseReview(fixture.Project.Id, fixture.Release.Id,
            caseTcr.Id, procedureKey, caseTcr.DisplayNumber, fixture.Now,
            baseNumber: "HLRTPCR-08210", revision: 0);
        var fromAssessment = TestChangeReview.FromCaseReview(fixture.Project.Id, fixture.Release.Id,
            caseTcr.Id, procedureKey, caseTcr.DisplayNumber, fixture.Now,
            baseNumber: "HLRTPCR-08211", revision: 1);
        var fromReport = TestChangeReview.FromProblemReport(fixture.Project.Id, fixture.Release.Id,
            report.Id, TestChangeReviewDiscipline.System, report.DisplayNumber, fixture.Now,
            baseNumber: "SYSTPCR-08210", revision: 0);
        fromReport.IncludeChangeRequest("author", root.Id, root.DisplayNumber, fixture.Now);
        fixture.Db.AddRange(fromChange, fromAssessment, fromReport);
        await fixture.Db.SaveChangesAsync();
        // Exercise the projection's migration-safe discriminator reads without recreating the much larger
        // approval/baseline fixtures that validate these origin transitions elsewhere.
        var caseChangeKind = TestChangeReviewOriginKind.CaseChange.ToString();
        var caseAssessmentKind = TestChangeReviewOriginKind.CaseAssessment.ToString();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE test_change_reviews SET OriginKind = {caseChangeKind}, OriginReferenceId = {caseChange.Id} WHERE Id = {fromChange.Id}");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE test_change_reviews SET OriginKind = {caseAssessmentKind}, OriginReferenceId = {assessment.Id} WHERE Id = {fromAssessment.Id}");
        fixture.Db.ChangeTracker.Clear();

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Edges, x => x.FromId == caseTcr.Id && x.ToId == fromChange.Id
            && x.Provenance.Any(p => p.Kind == "CaseChangeOrigin" && p.SourceId == caseChange.Id));
        Assert.Contains(result.Edges, x => x.FromId == caseTcr.Id && x.ToId == fromAssessment.Id
            && x.Provenance.Any(p => p.Kind == "CaseAssessmentOrigin" && p.SourceId == assessment.Id));
        var reportEdge = Assert.Single(result.Edges, x => x.FromId == root.Id && x.ToId == fromReport.Id);
        Assert.Contains(reportEdge.Provenance, x => x.Kind == "TcrAdditionalSource");
        Assert.DoesNotContain(reportEdge.Provenance, x => x.Kind == "TcrOrigin");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, AeroLinkDbContext db, ProjectRecord project,
            SoftwareRelease release, DateTimeOffset now, QueryCounter queryCounter)
        { _connection = connection; Db = db; Project = project; Release = release; Now = now; QueryCounter = queryCounter; }
        public AeroLinkDbContext Db { get; }
        public ProjectRecord Project { get; }
        public SoftwareRelease Release { get; }
        public DateTimeOffset Now { get; }
        public QueryCounter QueryCounter { get; }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var queryCounter = new QueryCounter();
            var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite(connection).AddInterceptors(queryCounter).Options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Trace", "TRC");
            var project = new ProjectRecord(program.Id, "Trace project", "Trace product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            return new(connection, db, project, release, now, queryCounter);
        }
        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
