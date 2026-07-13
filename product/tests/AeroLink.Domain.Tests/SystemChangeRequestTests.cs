using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Tests;

public sealed class SystemChangeRequestTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Display_number_appends_two_digit_revision()
    {
        var scr = CreateDraft();
        Assert.Equal("SCR-00001049.01", scr.DisplayNumber);
        Assert.Equal("SYSR-00002375.04", ArtifactNumber.Display("SYSR-00002375", 4));
    }

    [Fact]
    public void Submit_requires_pas_and_at_least_one_requirement_change()
    {
        var empty = new SystemChangeRequest("SCR-00001049", 1, ProjectId, ReleaseId,
            "Round Robin", "", "Analysis", "Solution", "author", Now);
        Assert.Throws<DomainException>(() => empty.SubmitForReview("author", Approvers(), Now));

        var noChange = new SystemChangeRequest("SCR-00001049", 1, ProjectId, ReleaseId,
            "Round Robin", "Problem", "Analysis", "Solution", "author", Now);
        Assert.Throws<DomainException>(() => noChange.SubmitForReview("author", Approvers(), Now));
    }

    [Fact]
    public void Author_can_replace_draft_content_without_changing_revision()
    {
        var scr = CreateDraftWithRequirement();
        scr.UpdateDraft("author", "Updated Round Robin", "Updated problem", "Updated analysis", "Updated solution",
        [
            new RequirementChangeDraft("SYSR-00002375", 2, RequirementLevel.System, RequirementChangeKind.Modify,
                "The FMS shall sequence Round Robin waypoints deterministically.", "Clarified behavior.", "Test"),
            new RequirementChangeDraft("SWR-00002376", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
                "The software shall expose the selected sequence.", "New HLR.", "Test")
        ], Now.AddMinutes(1));

        Assert.Equal(1, scr.Revision);
        Assert.Equal("Updated Round Robin", scr.Title);
        Assert.Equal(2, scr.RequirementChanges.Count);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "ScrDraftUpdated");
    }

    [Fact]
    public void Draft_content_cannot_change_during_review()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);
        Assert.Throws<DomainException>(() => scr.UpdateDraft("author", "Changed", "P", "A", "S", [], Now));
    }

    [Fact]
    public void Approval_proceeds_in_author_selected_order()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);

        Assert.Throws<DomainException>(() => scr.ApproveActiveStage("verification", Now));
        scr.ApproveActiveStage("systems", Now);
        Assert.Equal(ScrState.InReview, scr.State);
        Assert.Equal(1, scr.ActiveReviewCycle!.ActivePosition);
        scr.ApproveActiveStage("software", Now);
        scr.ApproveActiveStage("verification", Now);

        Assert.Equal(ScrState.Approved, scr.State);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "ScrApproved");
    }

    [Fact]
    public void Parallel_review_activates_every_reviewer_and_completes_after_all_approve()
    {
        var scr = CreateDraftWithRequirement();
        var cycle = scr.SubmitForReview("author", Approvers(), Now, ReviewMode.Parallel);

        Assert.Equal(ReviewMode.Parallel, cycle.Mode);
        Assert.All(cycle.Steps, step => Assert.Equal(ApprovalStepState.Active, step.State));

        scr.ApproveActiveStage("verification", Now.AddMinutes(1));
        scr.ApproveActiveStage("systems", Now.AddMinutes(2));
        Assert.Equal(ScrState.InReview, scr.State);

        scr.ApproveActiveStage("software", Now.AddMinutes(3));
        Assert.Equal(ScrState.Approved, scr.State);
        Assert.All(cycle.Steps, step => Assert.Equal(ApprovalStepState.Approved, step.State));
    }

    [Fact]
    public void Requested_change_returns_same_revision_to_draft_and_preserves_cycle()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);
        scr.RequestChanges("systems", "Clarify trigger behavior.", Now.AddMinutes(5));

        Assert.Equal(ScrState.Draft, scr.State);
        Assert.Equal(1, scr.Revision);
        Assert.Equal(ReviewCycleState.ChangesRequested, scr.ReviewCycles.Single().State);

        var second = scr.SubmitForReview("author", Approvers(), Now.AddMinutes(10));
        Assert.Equal(2, second.Sequence);
        Assert.Equal(2, scr.ReviewCycles.Count);
    }

    [Fact]
    public void Author_can_replace_only_not_yet_reached_approver()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);

        Assert.Throws<DomainException>(() =>
            scr.ReplaceFutureApprover("author", 0, new("replacement", "Wrong"), Now));

        scr.ReplaceFutureApprover("author", 2, new("quality", "Priya Nair"), Now);
        Assert.Equal("Priya Nair", scr.ActiveReviewCycle!.Steps.Single(x => x.Position == 2).ApproverName);
    }

    [Fact]
    public void Wrong_completed_approver_cancels_cycle_and_restarts_against_same_snapshot()
    {
        var scr = CreateDraftWithRequirement();
        var first = scr.SubmitForReview("author", Approvers(), Now);
        scr.ApproveActiveStage("systems", Now.AddMinutes(1));

        var corrected = new[]
        {
            new ApproverSelection("correct-systems", "Correct Systems Lead"),
            new ApproverSelection("software", "David Lee"),
            new ApproverSelection("verification", "Sarah Rodriguez")
        };
        var second = scr.CancelAndRestartForWrongApprover("author", "Wrong first approver.", corrected, Now.AddMinutes(2));

        Assert.Equal(ReviewCycleState.Cancelled, first.State);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(0, second.ActivePosition);
        Assert.Equal(ScrState.InReview, scr.State);
    }

    [Fact]
    public void Approved_scr_change_creates_next_revision()
    {
        var scr = FullyApprove();
        var next = scr.StartNextRevision("author", Now.AddHours(1));

        Assert.Equal(ScrState.Approved, scr.State);
        Assert.Equal(ScrState.Draft, next.State);
        Assert.Equal(2, next.Revision);
        Assert.Equal("SCR-00001049.02", next.DisplayNumber);
        Assert.Single(next.RequirementChanges);
    }

    [Fact]
    public void Candidate_baseline_accepts_only_approved_target_release_scr()
    {
        var draft = CreateDraftWithRequirement();
        var baseline = new CandidateBaseline("BL-00000217", 1, ProjectId, ReleaseId, null,
            "FMS 3.3 Candidate", "cm", Now);
        Assert.Throws<DomainException>(() => baseline.Select(draft, "cm", Now));

        var approved = FullyApprove();
        baseline.Select(approved, "cm", Now);
        Assert.Single(baseline.Selections);
        Assert.Equal(ScrState.SelectedForBaseline, approved.State);
    }

    [Fact]
    public void Candidate_baseline_can_remove_selection_before_freeze()
    {
        var approved = FullyApprove();
        var baseline = new CandidateBaseline("BL-00000217", 1, ProjectId, ReleaseId, null,
            "FMS 3.3 Candidate", "cm", Now);
        baseline.Select(approved, "cm", Now);
        baseline.Remove(approved, "cm", Now.AddMinutes(1));
        Assert.Empty(baseline.Selections);
        Assert.Equal(ScrState.Approved, approved.State);
        Assert.Contains(baseline.Events, x => x.EventType == "ScrRemoved");
    }

    [Fact]
    public void Frozen_baseline_has_deterministic_hash_and_is_immutable()
    {
        var approved = FullyApprove();
        var baseline = new CandidateBaseline("BL-00000217", 1, ProjectId, ReleaseId, null,
            "FMS 3.3 Candidate", "cm", Now);
        baseline.Select(approved, "cm", Now);
        baseline.Freeze("cm", Now.AddMinutes(1));
        Assert.Equal(CandidateBaselineState.Frozen, baseline.State);
        Assert.Equal(64, baseline.ContentHash!.Length);
        Assert.Throws<DomainException>(() => baseline.Remove(approved, "cm", Now.AddMinutes(2)));
        Assert.Contains(baseline.Events, x => x.EventType == "CandidateBaselineFrozen");
    }

    [Fact]
    public void Author_can_move_uncommitted_change_to_a_future_release_with_audit_history()
    {
        var scr = CreateDraftWithRequirement();
        var nextRelease = Guid.NewGuid();

        scr.Retarget("author", nextRelease, "Deferred from 1.6 to the 1.7 planning window.", Now.AddMinutes(1));

        Assert.Equal(nextRelease, scr.TargetReleaseId);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "TargetReleaseChanged" && x.Detail.Contains("1.7"));
        Assert.Throws<DomainException>(() => scr.Retarget("someone.else", Guid.NewGuid(), "Unauthorized move.", Now.AddMinutes(2)));
    }

    private static SystemChangeRequest CreateDraft() =>
        new("SCR-00001049", 1, ProjectId, ReleaseId, "Introduce Round Robin",
            "Round Robin is not available.", "The existing sequence is linear.",
            "Add selectable deterministic Round Robin sequencing.", "author", Now);

    private static SystemChangeRequest CreateDraftWithRequirement()
    {
        var scr = CreateDraft();
        scr.AddRequirementChange("author", "SYSR-00002375", 1, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall provide selectable Round Robin sequencing.",
            "Required for the new function.", "Test", Now);
        return scr;
    }

    private static SystemChangeRequest FullyApprove()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);
        scr.ApproveActiveStage("systems", Now);
        scr.ApproveActiveStage("software", Now);
        scr.ApproveActiveStage("verification", Now);
        return scr;
    }

    private static ApproverSelection[] Approvers() =>
    [
        new("systems", "Maya Chen"),
        new("software", "David Lee"),
        new("verification", "Sarah Rodriguez")
    ];
}
