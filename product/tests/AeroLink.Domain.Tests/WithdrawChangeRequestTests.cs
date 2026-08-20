using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Taking a change request back, and unsealing a build so that becomes possible.
///
/// Work is abandoned for ordinary reasons. Until now the only options were deferring it, which says "later"
/// rather than "never", or leaving it in the register misrepresenting the plan.
///
/// Nothing is unwound by withdrawing, and that is not an omission: approving a change request does not move
/// the requirement. The revision appears when a baseline is frozen and materialized, so a change request
/// withdrawn before that has produced nothing to take back — and one whose baseline has been frozen cannot be
/// withdrawn until somebody reopens it, deliberately and by name.
/// </summary>
public sealed class WithdrawChangeRequestTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Build = Guid.NewGuid();
    private const string Author = "author";

    [Fact]
    public void A_change_request_can_be_withdrawn_from_every_state_it_can_reach()
    {
        foreach (var reach in new[] { ChangeRequestState.Draft, ChangeRequestState.InReview, ChangeRequestState.Approved })
        {
            var scr = Reached(reach);
            scr.Withdraw(Author, "Superseded by a better approach.", Now);

            Assert.Equal(ChangeRequestState.Withdrawn, scr.State);
            Assert.Equal(reach, scr.WithdrawnFromState);
        }
    }

    [Fact]
    public void The_record_says_what_was_abandoned_and_why()
    {
        var scr = Reached(ChangeRequestState.Approved);
        scr.Withdraw(Author, "The problem turned out not to exist.", Now);

        var entry = Assert.Single(scr.AuditEvents.Where(x => x.EventType == "ChangeRequestWithdrawn"));
        Assert.Contains("Approved", entry.Detail);
        Assert.Contains("The problem turned out not to exist.", entry.Detail);
    }

    /// <summary>
    /// The approvers were asked about work that is being taken away. Leaving the cycle open would leave
    /// signatures outstanding against a package nobody intends to ship.
    /// </summary>
    [Fact]
    public void Withdrawing_mid_review_cancels_the_cycle()
    {
        var scr = Reached(ChangeRequestState.InReview);
        scr.Withdraw(Author, "Scope cut.", Now);

        Assert.Equal(ChangeRequestState.Withdrawn, scr.State);
        Assert.Null(scr.ActiveReviewCycle);
    }

    [Fact]
    public void A_reason_is_required_and_it_cannot_be_withdrawn_twice()
    {
        var scr = Reached(ChangeRequestState.Draft);
        Assert.Throws<DomainException>(() => scr.Withdraw(Author, "  ", Now));

        scr.Withdraw(Author, "Abandoned.", Now);
        Assert.Throws<DomainException>(() => scr.Withdraw(Author, "Again.", Now));
    }

    /// <summary>
    /// Selected work belongs to a baseline that is planning around it. Taking it out of the baseline is an
    /// explicit act with its own event, exactly as deferring already requires.
    /// </summary>
    [Fact]
    public void Work_selected_into_a_baseline_must_leave_it_first()
    {
        var scr = Reached(ChangeRequestState.Approved);
        scr.MarkSelectedForBaseline(Author, Now);

        var refused = Assert.Throws<DomainException>(() => scr.Withdraw(Author, "Abandoned.", Now));
        Assert.Contains("candidate baseline", refused.Message);
    }

    [Fact]
    public void A_frozen_baseline_can_be_reopened_and_says_so()
    {
        var baseline = Frozen();
        baseline.Reopen("cm", "SRCR-00110 was wrong and 1.6 has not shipped.", Now);

        Assert.Equal(CandidateBaselineState.Draft, baseline.State);
        Assert.Null(baseline.FrozenAt);
        Assert.Equal(string.Empty, baseline.ContentHash);
        var entry = Assert.Single(baseline.Events.Where(x => x.EventType == "CandidateBaselineReopened"));
        Assert.Contains("SRCR-00110 was wrong", entry.Detail);
    }

    [Fact]
    public void Reopening_says_when_it_took_materialized_revisions_back_with_it()
    {
        var baseline = Frozen();
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 12, Now);

        baseline.Reopen("cm", "Wrong content.", Now);

        Assert.Null(baseline.RequirementsMaterializedAt);
        Assert.Equal(string.Empty, baseline.RequirementsHash);
        Assert.Contains("materialized were taken back",
            baseline.Events.Single(x => x.EventType == "CandidateBaselineReopened").Detail);
    }

    [Fact]
    public void A_reason_is_required_to_reopen_and_a_draft_baseline_is_already_open()
    {
        var frozen = Frozen();
        Assert.Throws<DomainException>(() => frozen.Reopen("cm", "   ", Now));

        frozen.Reopen("cm", "Wrong content.", Now);
        Assert.Throws<DomainException>(() => frozen.Reopen("cm", "Again.", Now));
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static SystemChangeRequest Reached(ChangeRequestState reach)
    {
        var scr = new SystemChangeRequest("SRCR-00971", 0, Project, Build,
            "Withdrawable", "P", "A", "S", Author, Now);
        scr.AddRequirementChange(Author, "SYSR-00211", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.5 seconds.", "Latency", "Test", Now);
        if (reach is ChangeRequestState.InReview or ChangeRequestState.Approved)
            scr.SubmitForReview(Author, [new("reviewer", "Reviewer")], Now);
        if (reach == ChangeRequestState.Approved)
            scr.ApproveActiveStage("reviewer", Now);
        return scr;
    }

    private static CandidateBaseline Frozen()
    {
        var baseline = new CandidateBaseline("BL-00001", 0, Project, Build, null, "Build 1.6", "cm", Now);
        var scr = Reached(ChangeRequestState.Approved);
        baseline.Select(scr, "cm", Now);
        baseline.Freeze("cm", Now);
        return baseline;
    }
}
