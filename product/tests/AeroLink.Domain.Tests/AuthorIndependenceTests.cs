using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// #1091 SOD-1: the author of a change request cannot review it. An administrator is exempt, and the
/// exemption is recorded rather than passed off as independent review.
/// </summary>
public sealed class AuthorIndependenceTests
{
    private static SystemChangeRequest Draft(string author)
    {
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest("SRCR-00001", 0, Guid.NewGuid(), Guid.NewGuid(), "Governed change",
            "Problem", "Analysis", "Solution", author, now);
        scr.AddRequirementChange(author, "SYSR-00000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall hold its course.", "Rationale.", "Test", now,
            targetSectionId: Guid.NewGuid());
        return scr;
    }

    [Fact]
    public void An_author_cannot_be_named_as_a_reviewer()
    {
        var scr = Draft("change.author");
        var error = Assert.Throws<DomainException>(() => scr.SubmitForReview("change.author",
            [new ApproverSelection("change.author", "Change Author", ProgramRole.SystemEngineer, ProjectAuthoritySource.DirectBaseRole)],
            DateTimeOffset.UtcNow));
        Assert.Contains("cannot review", error.Message);
        Assert.Equal(ChangeRequestState.Draft, scr.State);
    }

    [Fact]
    public void An_administrator_author_may_review_and_the_exemption_is_recorded()
    {
        var scr = Draft("admin");
        scr.SubmitForReview("admin",
            [new ApproverSelection("admin", "Administrator", ProgramRole.Administrator, ProjectAuthoritySource.AdministratorSubstitution)],
            DateTimeOffset.UtcNow, administratorAuthority: true);

        scr.ApproveActiveStage("admin", DateTimeOffset.UtcNow);

        Assert.Equal(ChangeRequestState.Approved, scr.State);
        Assert.Equal(2, scr.AuditEvents.Count(x => x.EventType == "AdministratorSelfReview"));
    }
}
