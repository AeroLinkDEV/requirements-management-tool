using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

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
        Assert.Equal("SRCR-01049.01", scr.DisplayNumber);
        Assert.Equal("SYSR-00002375.04", ArtifactNumber.Display("SYSR-00002375", 4));
    }

    [Fact]
    public void Change_requests_use_five_digits_and_software_builds_use_the_official_name()
    {
        Assert.Equal("SRCR-00039.00", ArtifactNumber.Display("SRCR-00039", 0));
        Assert.Equal("HLRCR-00039.02", ArtifactNumber.Display("HLRCR-00039", 2));
        Assert.Equal("SW-01.60", ArtifactNumber.Display("SW-01.60", 0));
        Assert.Equal("SW-01.60", SoftwareBuildIdentifier.FromVersion("1.6"));
        Assert.Throws<DomainException>(() => ArtifactNumber.ValidateBase("SRCR-00000039"));
    }

    [Fact]
    public void Submit_requires_pas_and_at_least_one_requirement_change()
    {
        var empty = new SystemChangeRequest("SRCR-01049", 1, ProjectId, ReleaseId,
            "Round Robin", "", "Analysis", "Solution", "author", Now);
        Assert.Throws<DomainException>(() => empty.SubmitForReview("author", Approvers(), Now));

        var noChange = new SystemChangeRequest("SRCR-01049", 1, ProjectId, ReleaseId,
            "Round Robin", "Problem", "Analysis", "Solution", "author", Now);
        Assert.Throws<DomainException>(() => noChange.SubmitForReview("author", Approvers(), Now));
    }

    [Fact]
    public void Author_can_replace_draft_content_without_changing_revision()
    {
        var scr = new SystemChangeRequest("HLRCR-01049", 1, ProjectId, ReleaseId,
            "Round Robin", "Problem", "Analysis", "Solution", "author", Now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        scr.AddRequirementChange("author", "HLR-00002375", 1, RequirementLevel.HighLevel,
            RequirementChangeKind.Modify, "The software shall sequence waypoints.", "Clarification.", "Test", Now);
        scr.UpdateDraft("author", "Updated Round Robin", "Updated problem", "Updated analysis", "Updated solution",
        [
            new RequirementChangeDraft("HLR-00002375", 2, RequirementLevel.HighLevel, RequirementChangeKind.Modify,
                "The FMS software shall sequence Round Robin waypoints deterministically.", "Clarified behavior.", "Test"),
            // Both changes are HLR: this draft is an HLRCR, and a change request that carries a level cannot
            // hold the other one. The test is about replacing draft content without moving the revision.
            new RequirementChangeDraft("HLR-00002376", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
                "The software shall expose the selected sequence.", "New HLR.", "Test")
        ], Now.AddMinutes(1));

        Assert.Equal(1, scr.Revision);
        Assert.Equal("Updated Round Robin", scr.Title);
        Assert.Equal(2, scr.RequirementChanges.Count);
        Assert.Contains(scr.AuditEvents, x => x.EventType == "ScrDraftUpdated");
    }

    [Theory]
    [InlineData(RequirementLevel.HighLevel)]
    [InlineData(RequirementLevel.LowLevel)]
    public void System_change_request_rejects_software_requirement_levels(RequirementLevel level)
    {
        var scr = new SystemChangeRequest("SRCR-01050", 0, ProjectId, ReleaseId,
            "System change", "Problem", "Analysis", "Solution", "author", Now, ChangeRequestType.System);

        var error = Assert.Throws<DomainException>(() => scr.AddRequirementChange("author", "HLR-00000001", 0,
            level, RequirementChangeKind.Introduce, "Software behavior.", "Rationale.", "Test", Now));

        Assert.Contains("System requirements only", error.Message);
        Assert.Empty(scr.RequirementChanges);
    }

    [Fact]
    public void Software_change_request_rejects_system_requirement_level()
    {
        var swcr = new SystemChangeRequest("HLRCR-01050", 0, ProjectId, ReleaseId,
            "Software change", "Problem", "Analysis", "Solution", "author", Now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);

        var error = Assert.Throws<DomainException>(() => swcr.AddRequirementChange("author", "SYSR-00000001", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce, "System behavior.", "Rationale.", "Test", Now));

        Assert.Contains("HLR or LLR requirements only", error.Message);
        Assert.Empty(swcr.RequirementChanges);
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
        Assert.Equal(ChangeRequestState.InReview, scr.State);
        Assert.Equal(1, scr.ActiveReviewCycle!.ActivePosition);
        scr.ApproveActiveStage("software", Now);
        scr.ApproveActiveStage("verification", Now);

        Assert.Equal(ChangeRequestState.Approved, scr.State);
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
        Assert.Equal(ChangeRequestState.InReview, scr.State);

        scr.ApproveActiveStage("software", Now.AddMinutes(3));
        Assert.Equal(ChangeRequestState.Approved, scr.State);
        Assert.All(cycle.Steps, step => Assert.Equal(ApprovalStepState.Approved, step.State));
    }

    [Fact]
    public void Review_step_freezes_configured_principal_name_and_authority()
    {
        var scr = CreateDraftWithRequirement();
        var cycle = scr.SubmitForReview("author",
            [new ApproverSelection("systems.reviewer", "Systems Engineer", ProgramRole.Approver)], Now);

        var step = Assert.Single(cycle.Steps);
        Assert.Equal("systems.reviewer", step.ApproverId);
        Assert.Equal("Systems Engineer", step.ApproverName);
        Assert.Equal("Approver", step.Authority);
    }

    [Fact]
    public void Requested_change_returns_same_revision_to_draft_and_preserves_cycle()
    {
        var scr = CreateDraftWithRequirement();
        scr.SubmitForReview("author", Approvers(), Now);
        scr.RequestChanges("systems", "Clarify trigger behavior.", Now.AddMinutes(5));

        Assert.Equal(ChangeRequestState.Draft, scr.State);
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
        Assert.Equal(ChangeRequestState.InReview, scr.State);
    }

    [Fact]
    public void Approved_scr_change_creates_next_revision()
    {
        var scr = FullyApprove();
        var next = scr.StartNextRevision("author", Now.AddHours(1), targetReleaseIsReleased: false);

        Assert.Equal(ChangeRequestState.Approved, scr.State);
        Assert.Equal(ChangeRequestState.Draft, next.State);
        Assert.Equal(2, next.Revision);
        Assert.Equal("SRCR-01049.02", next.DisplayNumber);
        Assert.Single(next.RequirementChanges);
    }

    /// <summary>
    /// The state every approved change request in a working programme actually sits in.
    ///
    /// Requiring exactly `Approved` was defensible reading the enum and wrong in practice: allocating an
    /// approved change request to a candidate baseline moves it to SelectedForBaseline, which is where it stays.
    /// Across a 113-record programme not one change request was in `Approved`, so revising was unreachable
    /// everywhere — a gate that admitted a state the product does not rest in.
    /// </summary>
    [Fact]
    public void Scr_allocated_to_an_unreleased_build_can_still_be_revised()
    {
        var scr = FullyApprove();
        scr.MarkSelectedForBaseline("cm", Now.AddMinutes(30));
        Assert.Equal(ChangeRequestState.SelectedForBaseline, scr.State);

        var next = scr.StartNextRevision("author", Now.AddHours(1), targetReleaseIsReleased: false);

        Assert.Equal(ChangeRequestState.Draft, next.State);
        Assert.Equal(2, next.Revision);
        Assert.Equal(ChangeRequestState.SelectedForBaseline, scr.State);
    }

    /// <summary>
    /// Once the build has shipped, the change request that went into it is frozen history. A `.02` of it would
    /// claim the release said something it never said, so the answer is a new change request against the
    /// in-work build. Both signed-for states are refused, not just one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scr_incorporated_in_a_released_build_cannot_be_revised(bool allocated)
    {
        var scr = FullyApprove();
        if (allocated) scr.MarkSelectedForBaseline("cm", Now.AddMinutes(30));

        var refused = Assert.Throws<DomainException>(() =>
            scr.StartNextRevision("author", Now.AddHours(1), targetReleaseIsReleased: true));

        Assert.Contains("released build", refused.Message);
        Assert.Contains("new SCR", refused.Message);
    }

    [Fact]
    public void Draft_scr_cannot_skip_review_by_starting_a_revision()
    {
        var draft = CreateDraftWithRequirement();
        Assert.Throws<DomainException>(() =>
            draft.StartNextRevision("author", Now.AddHours(1), targetReleaseIsReleased: false));
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
        Assert.Equal(ChangeRequestState.SelectedForBaseline, approved.State);
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
        Assert.Equal(ChangeRequestState.Approved, approved.State);
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

    [Fact]
    public void A_change_request_can_be_deferred_from_draft_review_or_approved()
    {
        var draft = CreateDraftWithRequirement();
        draft.Defer("author", "Descoped from 1.6.", Now);
        Assert.Equal(ChangeRequestState.Deferred, draft.State);

        var approved = FullyApprove();
        approved.Defer("author", "Correct, but not shipping in 1.6.", Now);
        Assert.Equal(ChangeRequestState.Deferred, approved.State);

        // The case that had nowhere to go. A change request under review that the programme drops had to be
        // rejected — discarding a review that raised no objection — or left in review holding a release gate
        // that would never clear.
        var inReview = CreateDraftWithRequirement();
        inReview.SubmitForReview("author", Approvers(), Now);
        inReview.ApproveActiveStage("systems", Now);
        inReview.Defer("author", "Programme cut the scope mid-review.", Now);
        Assert.Equal(ChangeRequestState.Deferred, inReview.State);

        // The cycle in flight is closed with the deferral as its reason, not abandoned mid-flight.
        var cycle = Assert.Single(inReview.ReviewCycles);
        Assert.Equal(ReviewCycleState.Cancelled, cycle.State);
        Assert.Equal("Programme cut the scope mid-review.", cycle.ClosureReason);
        // The approval already given keeps its decision and its attribution; what it loses is force.
        Assert.Equal(ApprovalStepState.Approved, cycle.Steps.Single(x => x.ApproverId == "systems").State);
        Assert.Contains(inReview.AuditEvents, x => x.EventType == "ChangeRequestDeferred");
    }

    /// <summary>
    /// Deferring changes where the work sits, not how far it got.
    ///
    /// Storing only "Deferred" lost the difference between a signed-off change put away and an unwritten one, and
    /// a shelf that cannot tell those apart is a shelf nobody can plan from. Allocation and state are two facts
    /// and `ChangeRequestState` was carrying both.
    /// </summary>
    [Fact]
    public void Deferring_remembers_how_far_the_work_had_got()
    {
        var draft = CreateDraftWithRequirement();
        draft.Defer("author", "Descoped from 1.6.", Now);
        Assert.Equal(ChangeRequestState.Draft, draft.DeferredFromState);

        var approved = FullyApprove();
        approved.Defer("author", "Correct, but not shipping in 1.6.", Now);
        Assert.Equal(ChangeRequestState.Approved, approved.DeferredFromState);
    }

    [Fact]
    public void Reinstating_puts_a_change_request_back_where_it_was()
    {
        var approved = FullyApprove();
        approved.Defer("author", "Not shipping in 1.6.", Now);

        approved.Reinstate("author", Now.AddDays(30));

        Assert.Equal(ChangeRequestState.Approved, approved.State);
        Assert.Null(approved.DeferredFromState);
        Assert.Contains(approved.AuditEvents, x => x.EventType == "ChangeRequestReinstated");
    }

    /// <summary>
    /// Except from review, which cannot be resumed. Deferring cancels the cycle in flight — the approvers were
    /// asked about work that has since been put away — so it comes back as a Draft and is submitted again.
    /// Restoring InReview would mean signatures standing against a snapshot nobody has looked at since.
    /// </summary>
    [Fact]
    public void A_change_request_deferred_mid_review_comes_back_as_a_draft()
    {
        var inReview = CreateDraftWithRequirement();
        inReview.SubmitForReview("author", Approvers(), Now);
        inReview.Defer("author", "Programme cut the scope mid-review.", Now);
        Assert.Equal(ChangeRequestState.InReview, inReview.DeferredFromState);

        inReview.Reinstate("author", Now.AddDays(30));

        Assert.Equal(ChangeRequestState.Draft, inReview.State);
        Assert.Equal(ReviewCycleState.Cancelled, Assert.Single(inReview.ReviewCycles).State);
    }

    [Fact]
    public void Only_a_deferred_change_request_can_be_reinstated()
    {
        var approved = FullyApprove();
        Assert.Throws<DomainException>(() => approved.Reinstate("author", Now));

        approved.Defer("author", "Shelved.", Now);
        Assert.Throws<DomainException>(() => approved.Reinstate("someone.else", Now));
    }

    [Fact]
    public void Deferral_requires_a_reason_and_will_not_silently_leave_a_candidate_baseline()
    {
        var approved = FullyApprove();
        Assert.Throws<DomainException>(() => approved.Defer("author", "   ", Now));

        approved.MarkSelectedForBaseline("cm", Now);
        // Taking a change request out of a candidate baseline is its own attributable act, not a side effect.
        var refused = Assert.Throws<DomainException>(() => approved.Defer("author", "Changed our minds.", Now));
        Assert.Contains("candidate baseline", refused.Message);

        approved.UnmarkSelectedForBaseline("cm", Now);
        approved.Defer("author", "Now it can be put away.", Now);
        Assert.Equal(ChangeRequestState.Deferred, approved.State);

        Assert.Throws<DomainException>(() => approved.Defer("author", "Twice.", Now));
    }

    [Fact]
    public void Server_derived_administrator_authority_preserves_state_rules_authorship_and_actual_actor()
    {
        var draft = CreateDraftWithRequirement();
        Assert.Throws<DomainException>(() => draft.Defer("unrelated", "Unauthorized.", Now));

        draft.UpdateDraft("admin", "Administrator governed Draft", draft.Problem, draft.Analysis, draft.Solution,
            draft.RequirementChanges.Select(x => new RequirementChangeDraft(x.BaseNumber, x.Revision, x.Level,
                x.Kind, x.Statement, x.Rationale, x.VerificationMethod, x.RichText, x.AttributesJson,
                x.ImpactDispositionJson, x.TargetSectionId)).ToList(), Now.AddMinutes(1),
            administratorAuthority: true);
        draft.SubmitForReview("admin", Approvers(), Now.AddMinutes(2),
            administratorAuthority: true);
        draft.Defer("admin", "Programme authority paused the package.", Now.AddMinutes(3),
            administratorAuthority: true);
        draft.Reinstate("admin", Now.AddMinutes(4), administratorAuthority: true);

        Assert.Equal("author", draft.AuthorId);
        Assert.Equal(ChangeRequestState.Draft, draft.State);
        Assert.Contains(draft.AuditEvents, x => x.ActorId == "admin" && x.EventType == "ScrDraftUpdated");
        Assert.Contains(draft.AuditEvents, x => x.ActorId == "admin" && x.EventType == "ChangeRequestDeferred");

        var approved = FullyApprove();
        var next = approved.StartNextRevision("admin", Now.AddMinutes(5),
            targetReleaseIsReleased: false, administratorAuthority: true);
        Assert.Equal("author", next.AuthorId);
        Assert.Contains(next.AuditEvents, x => x.ActorId == "admin" && x.EventType == "RequirementChangeAdded");

        var invalidState = CreateDraftWithRequirement();
        var refusal = Assert.Throws<DomainException>(() => invalidState.StartNextRevision("admin",
            Now.AddMinutes(6), targetReleaseIsReleased: false, administratorAuthority: true));
        Assert.Contains("approved", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SystemChangeRequest CreateDraft() =>
        new("SRCR-01049", 1, ProjectId, ReleaseId, "Introduce Round Robin",
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
