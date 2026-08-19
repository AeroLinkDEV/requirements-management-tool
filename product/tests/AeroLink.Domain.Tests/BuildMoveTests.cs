using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Moving a change request between builds, and what does not move with it.
///
/// Reviewers approve a change into a particular build, against that build's baseline and the requirement
/// revisions current at the time. None of that is true of the build it moves into, so the approval does not
/// travel — by either route, because two ways to the same place must not carry different evidence.
/// </summary>
public sealed class BuildMoveTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid BuildSixteen = Guid.NewGuid();
    private static readonly Guid BuildSeventeen = Guid.NewGuid();
    private const string Author = "author";

    [Fact]
    public void Reinstating_returns_a_draft_whatever_it_was_when_it_was_shelved()
    {
        foreach (var reach in new[] { ChangeRequestState.Draft, ChangeRequestState.InReview, ChangeRequestState.Approved })
        {
            var scr = Approved(reach);
            var now = DateTimeOffset.UtcNow;
            scr.Defer(Author, "Moved to the next build.", now);
            Assert.Equal(reach, scr.DeferredFromState);

            scr.Reinstate(Author, now);

            Assert.Equal(ChangeRequestState.Draft, scr.State);
            Assert.Null(scr.DeferredFromState);
        }
    }

    [Fact]
    public void Reinstating_from_approved_says_the_approvals_did_not_come_back_with_it()
    {
        var scr = Approved(ChangeRequestState.Approved);
        var now = DateTimeOffset.UtcNow;
        scr.Defer(Author, "Shelved.", now);
        scr.Reinstate(Author, now);

        var entry = scr.AuditEvents.Last(x => x.EventType == "ChangeRequestReinstated");
        Assert.Contains("Approved", entry.Detail);
        Assert.Contains("do not carry into a new build", entry.Detail);
    }

    [Fact]
    public void Retargeting_an_approved_change_request_returns_it_to_draft()
    {
        var scr = Approved(ChangeRequestState.Approved);
        var now = DateTimeOffset.UtcNow;

        scr.Retarget(Author, BuildSeventeen, "Slipped to 1.7.", now);

        Assert.Equal(ChangeRequestState.Draft, scr.State);
        Assert.Equal(BuildSeventeen, scr.TargetReleaseId);
        Assert.Contains("approvals do not carry into another build",
            scr.AuditEvents.Last(x => x.EventType == "TargetReleaseChanged").Detail);
    }

    [Fact]
    public void Retargeting_a_draft_leaves_it_a_draft_and_says_nothing_about_approvals()
    {
        var scr = Approved(ChangeRequestState.Draft);
        var now = DateTimeOffset.UtcNow;

        scr.Retarget(Author, BuildSeventeen, "Slipped to 1.7.", now);

        Assert.Equal(ChangeRequestState.Draft, scr.State);
        Assert.DoesNotContain("approvals do not carry",
            scr.AuditEvents.Last(x => x.EventType == "TargetReleaseChanged").Detail);
    }

    /// <summary>
    /// The fact the originating build needs. Without it, a change request raised in 1.6 and reinstated into
    /// 1.7 is no longer deferred and no longer targets 1.6, so it disappears from 1.6 entirely.
    /// </summary>
    [Fact]
    public void The_build_it_was_raised_in_survives_every_move()
    {
        var scr = Approved(ChangeRequestState.Approved);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(BuildSixteen, scr.OriginReleaseId);

        scr.Defer(Author, "Shelved.", now);
        scr.Reinstate(Author, now);
        scr.Retarget(Author, BuildSeventeen, "Into 1.7.", now);

        Assert.Equal(BuildSeventeen, scr.TargetReleaseId);
        Assert.Equal(BuildSixteen, scr.OriginReleaseId);
    }

    [Fact]
    public void Only_a_deferred_change_request_can_be_reinstated()
    {
        var scr = Approved(ChangeRequestState.Draft);
        Assert.Throws<DomainException>(() => scr.Reinstate(Author, DateTimeOffset.UtcNow));
    }

    private static SystemChangeRequest Approved(ChangeRequestState reach)
    {
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest("SRCR-00931", 0, Project, BuildSixteen,
            "Build move", "P", "A", "S", Author, now);
        scr.AddRequirementChange(Author, "SYSR-00171", 2, RequirementLevel.System,
            RequirementChangeKind.Modify, "The system shall respond within 1.5 seconds.", "Latency", "Test", now);
        if (reach is ChangeRequestState.InReview or ChangeRequestState.Approved)
            scr.SubmitForReview(Author, [new("reviewer", "Reviewer")], now);
        if (reach == ChangeRequestState.Approved)
            scr.ApproveActiveStage("reviewer", now);
        return scr;
    }
}
