using System.Text.Json;
using AeroLink.Domain.Common;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ControlledEditingCheckInEngineTests
{
    [Fact]
    public async Task Successful_check_in_applies_latest_autosave_and_atomically_closes_lease_with_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var latest = Scenario.Draft("Latest autosaved title", "Latest problem");
        await scenario.AutosaveAsync(latest);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.ResultingArtifactVersion);
        Assert.NotNull(result.ResultingHash);
        scenario.Db.ChangeTracker.Clear();
        var saved = await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id);
        var session = await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id);
        var evidence = await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId);
        Assert.Equal("Latest autosaved title", saved.Title);
        Assert.Equal("Latest problem", saved.Problem);
        Assert.Equal(EditSessionState.Committed, session.State);
        Assert.Null(session.LockKey);
        Assert.Equal(ControlledCheckInOutcome.Succeeded, evidence.Outcome);
        Assert.Equal("SystemChangeRequestControlledEditingAdapter", evidence.Adapter);
        Assert.Equal(1, evidence.AggregateVersionBefore);
        Assert.Equal(2, evidence.AggregateVersionAfter);
        Assert.Equal(result.ResultingHash, evidence.ResultingSnapshotHash);
        Assert.NotNull(evidence.DraftSnapshotId);
        // The narrative and the evidence are separate records, because the narrative used to be the payload:
        // the timeline rendered serialized session and evidence identifiers as the audit story.
        var checkedIn = Assert.Single(await scenario.Db.AuditEvents.ToListAsync(), x =>
            x.AggregateId == scenario.Scr.Id && x.EventType == "ArtifactCheckedIn");
        Assert.DoesNotContain("{", checkedIn.Detail);
        Assert.DoesNotContain(evidence.Id.ToString(), checkedIn.Detail);
        Assert.Contains("Checked in", checkedIn.Detail);
        Assert.Equal(AuditEvent.CurrentSchemaVersion, checkedIn.SchemaVersion);

        // The identifiers are retained, in the structured field rather than the sentence.
        Assert.NotNull(checkedIn.EvidenceJson);
        using var structured = JsonDocument.Parse(checkedIn.EvidenceJson!);
        Assert.Equal(evidence.Id, structured.RootElement.GetProperty("evidenceId").GetGuid());
        Assert.Equal("SystemChangeRequestControlledEditingAdapter", structured.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(2, structured.RootElement.GetProperty("aggregateVersionAfter").GetInt32());
        Assert.Equal(result.ResultingHash, structured.RootElement.GetProperty("resultingSnapshotHash").GetString());
    }

    [Fact]
    public async Task Draft_check_in_atomically_adds_a_build_scoped_driving_problem_report()
    {
        await using var scenario = await Scenario.CreateAsync();
        var releaseId = await scenario.Db.Releases.Select(x => x.Id).SingleAsync();
        var report = new ProblemReport(scenario.Project.Id, "PR-00001", "Position disagreement",
            "Sources disagree during approach.", "", scenario.Actor.UserName, scenario.Now);
        scenario.Db.ProblemReports.Add(report);
        scenario.Db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", releaseId,
            "BuildScope", scenario.Actor.UserName, scenario.Now));
        await scenario.Db.SaveChangesAsync();
        await scenario.AutosaveAsync(Scenario.Draft("PR-driven correction", "Latest problem", [report.Id]));

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.True(result.Success, result.Error);
        scenario.Db.ChangeTracker.Clear();
        var link = await scenario.Db.ProblemReportLinks.SingleAsync(x => x.ArtifactType == "ChangeRequest"
            && x.ArtifactId == scenario.Scr.Id && x.Relationship == "ProposedCorrectiveAction");
        Assert.Equal(report.Id, link.ProblemReportId);
        Assert.Contains(await scenario.Db.AuditEvents.ToListAsync(), x =>
            x.AggregateId == scenario.Scr.Id && x.EventType == "ProblemReportLinksUpdated");
    }

    [Fact]
    public async Task Draft_check_in_rejects_a_problem_report_from_another_build_without_partial_changes()
    {
        await using var scenario = await Scenario.CreateAsync();
        var otherRelease = new SoftwareRelease(scenario.Project.Id, "2.0", false);
        var report = new ProblemReport(scenario.Project.Id, "PR-00001", "Future-build problem",
            "This report belongs to another build.", "", scenario.Actor.UserName, scenario.Now);
        scenario.Db.AddRange(otherRelease, report);
        scenario.Db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", otherRelease.Id,
            "BuildScope", scenario.Actor.UserName, scenario.Now));
        await scenario.Db.SaveChangesAsync();
        await scenario.AutosaveAsync(Scenario.Draft("Should roll back", "Changed problem", [report.Id]));

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.InvalidDraft, result.Status);
        Assert.Contains("target build", result.Error);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Original title", (await scenario.Db.SystemChangeRequests.SingleAsync()).Title);
        Assert.Empty(await scenario.Db.ProblemReportLinks.Where(x => x.ArtifactType == "ChangeRequest").ToListAsync());
    }

    [Fact]
    public async Task Expected_session_version_mismatch_retains_active_session_and_does_not_mutate_artifact()
    {
        await using var scenario = await Scenario.CreateAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version + 1,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("edit_session_version_mismatch", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task Authoritative_change_after_checkout_returns_deterministic_stale_artifact_conflict()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Scr.UpdateDraft(scenario.Actor.UserName, "Changed elsewhere", "Problem", "Analysis",
            "Solution", [], scenario.Now.AddSeconds(5));
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("stale_artifact_version", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Changed elsewhere", (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).Title);
        Assert.Equal(EditSessionState.Active, (await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id)).State);
    }

    [Fact]
    public async Task Expired_lease_is_rejected_without_applying_draft()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await using var scenario = await Scenario.CreateAsync(openedAt, 2);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, DateTimeOffset.UtcNow, default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("edit_session_expired", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Original title", (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).Title);
        var expired = await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id);
        Assert.Equal(EditSessionState.Expired, expired.State);
        Assert.Null(expired.LockKey);
    }

    [Fact]
    public async Task Wrong_user_is_forbidden_and_session_owner_keeps_the_lease()
    {
        await using var scenario = await Scenario.CreateAsync();
        var other = new AuthenticatedUser(Guid.NewGuid(), "other.engineer", "Other Engineer", "other@example.test",
            false, [new UserProgramAccess(scenario.Program.Id, [ProgramRole.Engineer.ToString()])]);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            other, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Forbidden, result.Status);
        Assert.Equal("edit_session_owner_mismatch", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task User_without_project_authorization_is_forbidden()
    {
        await using var scenario = await Scenario.CreateAsync();
        var unauthorized = scenario.Actor with { Programs = [] };

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            unauthorized, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Forbidden, result.Status);
        Assert.Equal("project_authorization_required", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task Lifecycle_rejection_precedes_stale_snapshot_detection()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Scr.AddRequirementChange(scenario.Actor.UserName, "SYSR-000001", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce, "Required behavior", "Rationale",
            "Test", scenario.Now.AddSeconds(1));
        scenario.Scr.SubmitForReview(scenario.Actor.UserName,
            [new ApproverSelection("approver", "Approver")], scenario.Now.AddSeconds(2));
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal("artifact_not_editable", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal(ChangeRequestState.InReview, (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).State);
        Assert.Equal(EditSessionState.Active, (await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id)).State);
    }

    [Fact]
    public async Task Malformed_latest_autosave_is_rejected_and_preserved_as_failure_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.AutosaveAsync("{ malformed");

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.InvalidDraft, result.Status);
        Assert.Equal("malformed_draft_json", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
        Assert.Equal(ControlledCheckInOutcome.Failed,
            (await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId)).Outcome);
    }

    [Fact]
    public async Task Aggregate_validation_failure_does_not_partially_persist_or_release_lease()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.AutosaveAsync(Scenario.Draft("", "Attempted mutation"));

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.InvalidDraft, result.Status);
        Assert.Equal("aggregate_validation_failed", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
        Assert.Contains("SCR title", (await scenario.Db.ControlledArtifactCheckInEvidence
            .SingleAsync(x => x.Id == result.EvidenceId)).Reason);
    }

    [Fact]
    public async Task Requirement_proposal_adapter_updates_content_through_parent_aggregate_without_changing_identity()
    {
        await using var scenario = await Scenario.CreateAsync();
        var proposal = scenario.Scr.AddRequirementChange(scenario.Actor.UserName, "SYSR-000777", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce, "Original proposal", "Rationale",
            "Test", scenario.Now.AddSeconds(1));
        await scenario.Db.SaveChangesAsync();
        var adapter = new RequirementProposalControlledEditingAdapter(scenario.Db);
        var artifact = await adapter.ResolveAsync(proposal.Id, default) ?? throw new InvalidOperationException();
        var canonical = adapter.CanonicalSnapshot(artifact);
        var baseHash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(canonical));
        var session = new ArtifactEditSession(scenario.Project.Id, "RequirementProposal", proposal.Id, null,
            baseHash, canonical, scenario.Actor.UserName, scenario.Now.AddSeconds(2), true, 15);
        scenario.Db.AddRange(session, new ArtifactDraftSnapshot(scenario.Project.Id, session.Id,
            "RequirementProposal", proposal.Id, 1, canonical, baseHash, scenario.Actor.UserName,
            scenario.Now.AddSeconds(2)));
        await scenario.Db.SaveChangesAsync();
        var latest = JsonSerializer.Serialize(new { proposal.BaseNumber, proposal.Revision,
            level = proposal.Level.ToString(), kind = proposal.Kind.ToString(),
            statement = "Updated proposal through the universal adapter", proposal.Rationale,
            proposal.VerificationMethod, proposal.RichText, proposal.AttributesJson,
            proposal.ImpactDispositionJson });
        session.Save(latest, session.Version, scenario.Now.AddSeconds(3), 15);
        var latestHash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(latest));
        scenario.Db.ArtifactDraftSnapshots.Add(new(scenario.Project.Id, session.Id,
            "RequirementProposal", proposal.Id, session.Version, latest, latestHash,
            scenario.Actor.UserName, scenario.Now.AddSeconds(3)));
        await scenario.Db.SaveChangesAsync();
        var engine = new ControlledEditingCheckInEngine(scenario.Db, new IdentityService(scenario.Db),
            [new SystemChangeRequestControlledEditingAdapter(scenario.Db), adapter]);

        var result = await engine.CheckInAsync(session.Id, session.Version, scenario.Actor,
            scenario.Now.AddMinutes(1), default);

        Assert.True(result.Success);
        scenario.Db.ChangeTracker.Clear();
        var parent = await scenario.Db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == scenario.Scr.Id);
        var updated = Assert.Single(parent.RequirementChanges);
        Assert.Equal("SYSR-000777", updated.BaseNumber);
        Assert.Equal(0, updated.Revision);
        Assert.Equal("Updated proposal through the universal adapter", updated.Statement);
        Assert.Equal(EditSessionState.Committed,
            (await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == session.Id)).State);
    }

    [Fact]
    public async Task Specification_structure_adapter_updates_controlled_metadata_and_preserves_node_identity()
    {
        await using var scenario = await Scenario.CreateAsync();
        var specification = new RequirementSpecification(scenario.Project.Id, "SYSRD-000001", "Original structure",
            "System", "Original description", scenario.Actor.UserName, scenario.Now);
        var section = new SpecificationNode(specification.Id, null, 10, SpecificationNodeType.Section,
            "Navigation", null, scenario.Actor.UserName, scenario.Now);
        scenario.Db.AddRange(specification, section);
        await scenario.Db.SaveChangesAsync();
        var adapter = new SpecificationStructureControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new
        {
            id = specification.Id, specification.DocumentNumber, title = "Updated structure", specification.Level,
            description = "Updated description", version = specification.Version,
            nodes = new[] { new { section.Id, section.ParentId, position = 20, type = section.Type.ToString(), heading = "Updated navigation", section.RequirementArtifactId } }
        });

        var result = await CheckInAsync(scenario, adapter, "SpecificationStructure", specification.Id, draft);

        Assert.True(result.Success);
        scenario.Db.ChangeTracker.Clear();
        var stored = await scenario.Db.RequirementSpecifications.SingleAsync(x => x.Id == specification.Id);
        var storedNode = await scenario.Db.SpecificationNodes.SingleAsync(x => x.Id == section.Id);
        Assert.Equal("Updated structure", stored.Title);
        Assert.Equal(2, stored.Version);
        Assert.Equal("Updated navigation", storedNode.Heading);
        Assert.Equal(20, storedNode.Position);
    }

    [Fact]
    public async Task Test_procedure_adapter_updates_only_a_draft_revision_through_the_procedure_root()
    {
        await using var scenario = await Scenario.CreateAsync();
        var procedure = new TestProcedure(scenario.Project.Id, "SYSTP-000001", "Original procedure",
            scenario.Actor.UserName, scenario.Now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Original objective", "Original preconditions",
            "Original steps", "Original expected", TestProcedureState.Draft, scenario.Actor.UserName, scenario.Now);
        scenario.Db.AddRange(procedure, revision);
        await scenario.Db.SaveChangesAsync();
        var adapter = new TestProcedureControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new
        {
            procedureId = procedure.Id, procedure.BaseNumber, title = "Updated procedure", procedure.OwnerId,
            level = procedure.Level.ToString(), version = procedure.Version, revisionId = revision.Id,
            revision.Revision, objective = "Updated objective", revision.Preconditions, steps = "Updated steps",
            expectedResult = "Updated expected", state = revision.State.ToString(), revision.AuthorId
        });

        var result = await CheckInAsync(scenario, adapter, "TestProcedure", revision.Id, draft);

        Assert.True(result.Success);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Updated procedure", (await scenario.Db.TestProcedures.SingleAsync(x => x.Id == procedure.Id)).Title);
        var stored = await scenario.Db.TestProcedureRevisions.SingleAsync(x => x.Id == revision.Id);
        Assert.Equal("Updated objective", stored.Objective);
        Assert.Equal("Updated steps", stored.Steps);
    }

    [Fact]
    public async Task Trace_link_adapter_rejects_identity_changes_and_applies_an_authorized_proposal_update()
    {
        await using var scenario = await Scenario.CreateAsync();
        var baseline = new CandidateBaseline("SW-00.10", 0, scenario.Project.Id,
            (await scenario.Db.Releases.SingleAsync()).Id, null, "Trace support", scenario.Actor.UserName, scenario.Now);
        var source = new RequirementArtifact(scenario.Project.Id, "SYSR-000001", RequirementLevel.System, scenario.Now);
        var target = new RequirementArtifact(scenario.Project.Id, "SYSR-000002", RequirementLevel.System, scenario.Now);
        var sourceRevision = new RequirementRevision(source.Id, 0, "Source", "", "Test", RequirementRevisionState.Active, scenario.Scr.Id, baseline.Id, scenario.Now);
        var targetRevision = new RequirementRevision(target.Id, 0, "Target", "", "Test", RequirementRevisionState.Active, scenario.Scr.Id, baseline.Id, scenario.Now);
        var link = new RequirementTraceLink(scenario.Project.Id, sourceRevision.Id, targetRevision.Id,
            RequirementTraceType.DerivedFrom, "Original rationale", scenario.Now);
        scenario.Db.AddRange(baseline, source, target, sourceRevision, targetRevision, link);
        await scenario.Db.SaveChangesAsync();
        var adapter = new TraceLinkProposalControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new { link.Id, link.ProjectId, link.SourceRevisionId, link.TargetRevisionId,
            type = RequirementTraceType.AllocatedFrom.ToString(), rationale = "Updated rationale", version = link.Version });

        var result = await CheckInAsync(scenario, adapter, "TraceLinkProposal", link.Id, draft);

        Assert.True(result.Success);
        scenario.Db.ChangeTracker.Clear();
        var stored = await scenario.Db.RequirementTraces.SingleAsync(x => x.Id == link.Id);
        Assert.Equal(RequirementTraceType.AllocatedFrom, stored.Type);
        Assert.Equal("Updated rationale", stored.Rationale);
    }

    [Fact]
    public async Task Release_planning_adapter_updates_draft_name_and_retains_atomic_controlled_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var release = await scenario.Db.Releases.SingleAsync();
        var baseline = new CandidateBaseline("SW-00.20", 0, scenario.Project.Id, release.Id, null,
            "Original release plan", scenario.Actor.UserName, scenario.Now);
        scenario.Db.CandidateBaselines.Add(baseline);
        await scenario.Db.SaveChangesAsync();
        var adapter = new ReleasePlanningControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new { baseline.Id, baseline.BaseNumber, baseline.Revision,
            name = "Updated release plan", baseline.ReleaseId, baseline.PredecessorBaselineId,
            state = baseline.State.ToString(), baseline.ContentHash, baseline.RequirementsHash,
            version = baseline.Version, selectedScrIds = Array.Empty<Guid>() });

        var result = await CheckInAsync(scenario, adapter, "ReleasePlanning", baseline.Id, draft);

        Assert.True(result.Success);
        scenario.Db.ChangeTracker.Clear();
        var stored = await scenario.Db.CandidateBaselines.SingleAsync(x => x.Id == baseline.Id);
        Assert.Equal("Updated release plan", stored.Name);
        Assert.Equal(2, stored.Version);
        Assert.Equal("ReleasePlanningControlledEditingAdapter", (await scenario.Db.ControlledArtifactCheckInEvidence
            .SingleAsync(x => x.Id == result.EvidenceId)).Adapter);
    }

    private static async Task<ControlledCheckInResult> CheckInAsync(Scenario scenario, IControlledEditingAdapter adapter,
        string artifactType, Guid artifactId, string latestDraft)
    {
        var artifact = await adapter.ResolveAsync(artifactId, default) ?? throw new InvalidOperationException();
        var snapshot = adapter.CanonicalSnapshot(artifact);
        var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));
        var session = new ArtifactEditSession(scenario.Project.Id, artifactType, artifactId, null, hash, snapshot,
            scenario.Actor.UserName, scenario.Now.AddSeconds(1), true, 15);
        scenario.Db.AddRange(session, new ArtifactDraftSnapshot(scenario.Project.Id, session.Id, artifactType, artifactId,
            1, snapshot, hash, scenario.Actor.UserName, scenario.Now.AddSeconds(1)));
        await scenario.Db.SaveChangesAsync();
        session.Save(latestDraft, session.Version, scenario.Now.AddSeconds(2), 15);
        var latestHash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(latestDraft));
        scenario.Db.ArtifactDraftSnapshots.Add(new ArtifactDraftSnapshot(scenario.Project.Id, session.Id, artifactType,
            artifactId, session.Version, latestDraft, latestHash, scenario.Actor.UserName, scenario.Now.AddSeconds(2)));
        await scenario.Db.SaveChangesAsync();
        var engine = new ControlledEditingCheckInEngine(scenario.Db, new IdentityService(scenario.Db), [adapter]);
        return await engine.CheckInAsync(session.Id, session.Version, scenario.Actor, scenario.Now.AddMinutes(1), default);
    }

    [Fact]
    public async Task Document_template_check_in_creates_immutable_universal_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var template = new DocumentTemplate(scenario.Project.Id, "TPL-00001", "Verification template", "Initial controlled body", scenario.Actor.UserName, scenario.Now);
        scenario.Db.DocumentTemplates.Add(template); await scenario.Db.SaveChangesAsync();
        var adapter = new DocumentTemplateControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new { template.Id, template.ProjectId, template.TemplateNumber, title = "Approved verification template", body = "Latest autosaved controlled body", template.OwnerId, state = template.State.ToString(), version = template.Version });

        var result = await CheckInAsync(scenario, adapter, "DocumentTemplate", template.Id, draft);

        Assert.True(result.Success); scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Approved verification template", (await scenario.Db.DocumentTemplates.SingleAsync(x => x.Id == template.Id)).Title);
        Assert.Equal("DocumentTemplateControlledEditingAdapter", (await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId)).Adapter);
    }

    [Fact]
    public async Task Problem_report_check_in_creates_immutable_universal_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var report = new ProblemReport(scenario.Project.Id, "PR-00001", "Unexpected reset", "The unit reset unexpectedly.", "Initial analysis", scenario.Actor.UserName, scenario.Now);
        scenario.Db.ProblemReports.Add(report); await scenario.Db.SaveChangesAsync();
        var adapter = new ProblemReportControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new { report.Id, report.ProjectId, report.ReportNumber, title = "Reset under recovery load", problem = "The unit reset during recovery load.", analysis = "Latest root-cause analysis", report.ReportedBy, state = report.State.ToString(), version = report.Version });

        var result = await CheckInAsync(scenario, adapter, "ProblemReport", report.Id, draft);

        Assert.True(result.Success); scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Latest root-cause analysis", (await scenario.Db.ProblemReports.SingleAsync(x => x.Id == report.Id)).Analysis);
        Assert.Equal("ProblemReportControlledEditingAdapter", (await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId)).Adapter);
    }

    [Fact]
    public async Task Configuration_change_set_check_in_creates_immutable_universal_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var changeSet = new ConfigurationChangeSet(scenario.Project.Id, "CCS-00001", "Recovery configuration", "Initial controlled scope", scenario.Actor.UserName, scenario.Now);
        scenario.Db.ConfigurationChangeSets.Add(changeSet); await scenario.Db.SaveChangesAsync();
        var adapter = new ConfigurationChangeSetControlledEditingAdapter(scenario.Db);
        var draft = JsonSerializer.Serialize(new { changeSet.Id, changeSet.ProjectId, changeSet.ChangeSetNumber, title = "Recovery configuration release", description = "Latest controlled configuration scope", changeSet.OwnerId, state = changeSet.State.ToString(), version = changeSet.Version });

        var result = await CheckInAsync(scenario, adapter, "ConfigurationChangeSet", changeSet.Id, draft);

        Assert.True(result.Success); scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Recovery configuration release", (await scenario.Db.ConfigurationChangeSets.SingleAsync(x => x.Id == changeSet.Id)).Title);
        Assert.Equal("ConfigurationChangeSetControlledEditingAdapter", (await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId)).Adapter);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(AeroLinkDbContext db, ProgramRecord program, ProjectRecord project,
            SystemChangeRequest scr, ArtifactEditSession session, AuthenticatedUser actor,
            ControlledEditingCheckInEngine engine, DateTimeOffset now)
        { Db = db; Program = program; Project = project; Scr = scr; Session = session; Actor = actor; Engine = engine; Now = now; }

        public AeroLinkDbContext Db { get; }
        public ProgramRecord Program { get; }
        public ProjectRecord Project { get; }
        public SystemChangeRequest Scr { get; }
        public ArtifactEditSession Session { get; }
        public AuthenticatedUser Actor { get; }
        public ControlledEditingCheckInEngine Engine { get; }
        public DateTimeOffset Now { get; }

        public static async Task<Scenario> CreateAsync(DateTimeOffset? openedAt = null, int leaseMinutes = 15)
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
            var db = new AeroLinkDbContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            var now = openedAt ?? DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Controlled Editing Program", $"CE{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Controlled Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var scr = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id, "Original title",
                "Original problem", "Original analysis", "Original solution", "engineer", now);
            db.AddRange(program, project, release, scr);
            await db.SaveChangesAsync();
            var adapter = new SystemChangeRequestControlledEditingAdapter(db);
            var artifact = await adapter.ResolveAsync(scr.Id, default) ?? throw new InvalidOperationException();
            var snapshot = adapter.CanonicalSnapshot(artifact);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));
            var session = new ArtifactEditSession(project.Id, "ChangeRequest", scr.Id, null, hash,
                snapshot, "engineer", now, true, leaseMinutes);
            var draft = new ArtifactDraftSnapshot(project.Id, session.Id, "ChangeRequest", scr.Id, 1,
                snapshot, hash, "engineer", now);
            db.AddRange(session, draft);
            await db.SaveChangesAsync();
            var actor = new AuthenticatedUser(Guid.NewGuid(), "engineer", "Engineer", "engineer@example.test",
                false, [new UserProgramAccess(program.Id, [ProgramRole.Engineer.ToString()])]);
            var engine = new ControlledEditingCheckInEngine(db, new IdentityService(db), [adapter]);
            return new(db, program, project, scr, session, actor, engine, now);
        }

        public async Task AutosaveAsync(string draftJson)
        {
            Session.Save(draftJson, Session.Version, Now.AddSeconds(Session.Version), 15);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(draftJson));
            Db.ArtifactDraftSnapshots.Add(new(Project.Id, Session.Id, "ChangeRequest", Scr.Id,
                Session.Version, draftJson, hash, Actor.UserName, Now.AddSeconds(Session.Version)));
            await Db.SaveChangesAsync();
        }

        public async Task AssertUnchangedAndActiveAsync()
        {
            Db.ChangeTracker.Clear();
            var scr = await Db.SystemChangeRequests.SingleAsync(x => x.Id == Scr.Id);
            var session = await Db.ArtifactEditSessions.SingleAsync(x => x.Id == Session.Id);
            Assert.Equal("Original title", scr.Title);
            Assert.Equal("Original problem", scr.Problem);
            Assert.Equal(EditSessionState.Active, session.State);
            Assert.NotNull(session.LockKey);
        }

        public static string Draft(string title, string problem, Guid[]? problemReportIds = null) => JsonSerializer.Serialize(new
        {
            title, problem, analysis = "Latest analysis", solution = "Latest solution",
            problemReportIds,
            requirementChanges = Array.Empty<object>()
        });

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
