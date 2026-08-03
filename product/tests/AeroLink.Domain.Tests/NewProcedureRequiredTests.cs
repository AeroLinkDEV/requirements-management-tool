using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// "A test is required and no procedure exists yet" — the ordinary answer for a newly introduced requirement,
/// and until now one that could not be given.
///
/// The outcomes were "an approved procedure covers this" and "no test required", so an engineer whose honest
/// answer was "a procedure has to be written" had to leave the item unanswered, go and author the procedure,
/// get it approved, and come back. Nothing could tell that state apart from an item nobody had looked at, and
/// the release gates could not either.
/// </summary>
public sealed class NewProcedureRequiredTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Release = Guid.NewGuid();
    private static readonly Guid ChangeRequest = Guid.NewGuid();
    private static readonly Guid Review = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static VerificationImpactItem Introduced() =>
        VerificationImpactItem.ForIntroducedRequirement(Project, Release, ChangeRequest, Review, Guid.NewGuid(), "SYSR-000900.00", "Test", Now);

    private static VerificationImpactItem Modified() =>
        VerificationImpactItem.ForModifiedRequirement(Project, Release, ChangeRequest, Review, Guid.NewGuid(), "SYSR-000901.01", "Test", Now);

    private static VerificationImpactItem Orphaned() =>
        VerificationImpactItem.ForOrphanedProcedure(Project, Release, ChangeRequest, Review, Guid.NewGuid(), "SYSTP-000900.00", Now);

    private static VerificationImpactItem Claimed(VerificationImpactItem item)
    {
        item.AssignToEngineer("test.lead", "test.engineer", Now);
        return item;
    }

    [Fact]
    public void A_new_requirement_can_be_decided_as_needing_a_procedure_that_does_not_exist_yet()
    {
        var item = Claimed(Introduced());

        item.Resolve("test.engineer", VerificationImpactOutcome.NewProcedureRequired,
            "Oceanic sequencing has no procedure; one must be written.", Now);

        Assert.Equal(VerificationImpactState.Resolved, item.State);
        Assert.Equal(VerificationImpactOutcome.NewProcedureRequired, item.Outcome);
        Assert.Equal(TestProcedureChangeAction.CreateNew, item.ProcedureChangeAction);
        // Decided, but not coverage: nothing verifies the requirement yet, so it names no procedure.
        Assert.Null(item.ResolvedProcedureId);
        Assert.Null(item.ResolvedProcedureRevisionId);
        Assert.True(item.AwaitsNewProcedure);
    }

    [Fact]
    public void An_orphaned_procedure_item_cannot_ask_for_a_new_procedure()
    {
        var item = Claimed(Orphaned());

        // That item exists *because* a procedure exists and lost what it covered. Asking for a new one would
        // be answering a different question from the one raised.
        var refused = Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.NewProcedureRequired, "Not applicable here.", Now));
        Assert.Contains("does not apply", refused.Message);
    }

    [Fact]
    public void Approving_the_requested_procedure_settles_the_decision_without_a_second_answer()
    {
        var item = Claimed(Modified());
        item.Resolve("test.engineer", VerificationImpactOutcome.NewProcedureRequired, "A procedure must be written.", Now);

        var procedure = Guid.NewGuid();
        var revision = Guid.NewGuid();
        Assert.True(item.SettleWithApprovedProcedure(procedure, revision, Now.AddDays(1)));

        Assert.Equal(VerificationImpactOutcome.ProcedureCoverageConfirmed, item.Outcome);
        Assert.Equal(procedure, item.ResolvedProcedureId);
        Assert.Equal(revision, item.ResolvedProcedureRevisionId);
        Assert.False(item.AwaitsNewProcedure);
        // The original reasoning survives rather than being replaced by the settlement note.
        Assert.Contains("A procedure must be written.", item.ResolutionRationale);
    }

    [Fact]
    public void Settling_applies_only_to_an_item_that_asked_for_a_procedure()
    {
        var item = Claimed(Introduced());
        item.Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired, "Satisfied by analysis.", Now);

        // Nothing else may be quietly turned into coverage because a procedure was approved elsewhere.
        Assert.False(item.SettleWithApprovedProcedure(Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Equal(VerificationImpactOutcome.NoTestRequired, item.Outcome);
        Assert.Null(item.ResolvedProcedureId);
    }

    [Fact]
    public void Deciding_that_a_procedure_is_needed_names_no_procedure()
    {
        var item = Claimed(Introduced());

        // Naming one would be claiming coverage the decision explicitly says does not exist.
        var refused = Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.NewProcedureRequired, "One must be written.", Now,
            procedureId: Guid.NewGuid(), procedureRevisionId: Guid.NewGuid()));
        Assert.Contains("Only confirmed coverage names a procedure", refused.Message);
    }
}
