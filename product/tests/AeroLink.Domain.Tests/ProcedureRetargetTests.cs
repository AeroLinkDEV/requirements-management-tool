using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A procedure stranded by a retirement can be moved to the requirement it now covers.
///
/// Retiring a requirement takes away what a procedure was written against, and the only two answers were to
/// retire the procedure with it or keep it covering nothing. Neither fits the common case: the behaviour was
/// not withdrawn, it moved, and the procedure that exercises it is still the right procedure.
/// </summary>
public sealed class ProcedureRetargetTests
{
    private static VerificationImpactItem Orphaned() => VerificationImpactItem.ForOrphanedProcedure(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SYSTP-000042", DateTimeOffset.UtcNow);

    private static VerificationImpactItem Introduced() => VerificationImpactItem.ForIntroducedRequirement(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SYSR-000042", "Test", DateTimeOffset.UtcNow);

    [Fact]
    public void A_stranded_procedure_can_be_moved_onto_the_requirement_it_now_covers()
    {
        var item = Orphaned();
        var target = Guid.NewGuid();

        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "The behaviour moved to SYSR-000151; the procedure still exercises it.", DateTimeOffset.UtcNow,
            retargetedRequirementRevisionId: target);

        Assert.Equal(VerificationImpactState.Resolved, item.State);
        Assert.Equal(VerificationImpactOutcome.ProcedureRetargeted, item.Outcome);
        Assert.Equal(TestProcedureChangeAction.ModifyExisting, item.ProcedureChangeAction);
        Assert.Equal(target, item.RetargetedRequirementRevisionId);
    }

    [Fact]
    public void An_existing_retarget_target_may_be_confirmed_as_link_existing()
    {
        var item = Orphaned();
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetargeted,
            "The existing exact target remains valid.", DateTimeOffset.UtcNow,
            procedureChangeAction: TestProcedureChangeAction.LinkExisting,
            retargetedRequirementRevisionId: Guid.NewGuid());

        Assert.Equal(TestProcedureChangeAction.LinkExisting, item.ProcedureChangeAction);
    }

    [Fact]
    public void Retargeting_cannot_be_recorded_as_create_new_or_no_test_required()
    {
        foreach (var action in new[] { TestProcedureChangeAction.CreateNew, TestProcedureChangeAction.NoTestRequired })
        {
            var item = Orphaned();
            Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
                VerificationImpactOutcome.ProcedureRetargeted, "The target changed.", DateTimeOffset.UtcNow,
                procedureChangeAction: action, retargetedRequirementRevisionId: Guid.NewGuid()));
        }
    }

    [Fact]
    public void Moving_a_procedure_requires_saying_where_it_moves_to()
    {
        var item = Orphaned();
        // Without a target this is indistinguishable from retaining it, which is a different decision with a
        // different consequence for coverage.
        var error = Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.ProcedureRetargeted, "It moved.", DateTimeOffset.UtcNow));
        Assert.Contains("requirement revision it now covers", error.Message);
        Assert.Equal(VerificationImpactState.Open, item.State);
    }

    [Fact]
    public void Only_a_move_names_a_requirement_to_move_to()
    {
        var item = Orphaned();
        Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.ProcedureRetired, "No longer needed.", DateTimeOffset.UtcNow,
            retargetedRequirementRevisionId: Guid.NewGuid()));
    }

    /// <summary>
    /// The decision only exists because a retirement stranded something. Offering it against a newly
    /// introduced requirement would be offering to move a procedure that nothing has displaced.
    /// </summary>
    [Fact]
    public void A_requirement_item_cannot_be_resolved_by_moving_a_procedure()
    {
        var item = Introduced();
        var error = Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.ProcedureRetargeted, "It moved.", DateTimeOffset.UtcNow,
            retargetedRequirementRevisionId: Guid.NewGuid()));
        Assert.Contains("does not apply", error.Message);
    }

    [Fact]
    public void Retiring_and_retaining_a_stranded_procedure_still_work()
    {
        var retired = Orphaned();
        retired.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetired, "Withdrawn.", DateTimeOffset.UtcNow);
        Assert.Equal(VerificationImpactOutcome.ProcedureRetired, retired.Outcome);

        var retained = Orphaned();
        retained.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetained, "Kept deliberately.", DateTimeOffset.UtcNow);
        Assert.Equal(VerificationImpactOutcome.ProcedureRetained, retained.Outcome);
    }
}
