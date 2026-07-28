using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class VerificationImpactTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Release = Guid.NewGuid();
    private static readonly Guid ChangeRequest = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static VerificationImpactItem Introduced(string method = "Test") =>
        VerificationImpactItem.ForIntroducedRequirement(Project, Release, ChangeRequest, Guid.NewGuid(), "SYSR-000001.00", method, Now);

    private static VerificationImpactItem Modified() =>
        VerificationImpactItem.ForModifiedRequirement(Project, Release, ChangeRequest, Guid.NewGuid(), "SYSR-000002.01", "Test", Now);

    private static VerificationImpactItem Orphaned() =>
        VerificationImpactItem.ForOrphanedProcedure(Project, Release, ChangeRequest, Guid.NewGuid(), "SYSTP-000009.00", Now);

    [Fact]
    public void Item_starts_open_carries_its_release_and_blocks_baseline_approval()
    {
        var item = Introduced();

        Assert.Equal(VerificationImpactState.Open, item.State);
        Assert.Equal(Release, item.ReleaseId);
        Assert.Equal(ChangeRequest, item.ChangeRequestId);
        Assert.Equal(VerificationImpactTrigger.RequirementIntroduced, item.Trigger);
        Assert.True(item.BlocksBaselineApproval);
        Assert.Null(item.Outcome);
    }

    [Fact]
    public void Declared_verification_method_is_context_and_never_resolves_the_item()
    {
        // A requirement author declaring "Analysis" does not close the question. A verification engineer
        // still has to confirm that no test is required.
        var item = VerificationImpactItem.ForIntroducedRequirement(Project, Release, ChangeRequest,
            Guid.NewGuid(), "SYSR-000003.00", "Analysis", Now);

        Assert.Equal("Analysis", item.DeclaredVerificationMethod);
        Assert.Equal(VerificationImpactState.Open, item.State);
        Assert.True(item.BlocksBaselineApproval);

        item.Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired,
            "Verified by analysis report AR-114; the behaviour is not observable through test.", Now.AddHours(1));

        Assert.False(item.BlocksBaselineApproval);
        Assert.Equal("test.engineer", item.ResolvedBy);
    }

    [Fact]
    public void Lead_assigns_to_an_engineer_and_the_item_still_blocks_until_resolved()
    {
        var item = Modified();
        item.AssignToEngineer("test.lead", "test.engineer", Now.AddMinutes(5));

        Assert.Equal(VerificationImpactState.Assigned, item.State);
        Assert.Equal("test.lead", item.AssignedByLeadId);
        Assert.Equal("test.engineer", item.AssignedEngineerId);
        Assert.Equal(Now.AddMinutes(5), item.AssignedAt);
        Assert.True(item.BlocksBaselineApproval);
    }

    [Fact]
    public void Resolution_always_records_who_decided_and_why()
    {
        var item = Introduced();
        var procedure = Guid.NewGuid();
        var procedureRevision = Guid.NewGuid();
        Assert.Throws<DomainException>(() =>
            item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed, "   ", Now, procedure, procedureRevision));
        Assert.Throws<DomainException>(() =>
            item.Resolve("", VerificationImpactOutcome.ProcedureCoverageConfirmed, "Covered by SYSTP-000001.00.", Now, procedure, procedureRevision));

        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Covered by SYSTP-000001.00 revision 02.", Now.AddHours(2), procedure, procedureRevision);

        Assert.Equal(VerificationImpactState.Resolved, item.State);
        Assert.Equal(VerificationImpactOutcome.ProcedureCoverageConfirmed, item.Outcome);
        Assert.Equal("Covered by SYSTP-000001.00 revision 02.", item.ResolutionRationale);
        Assert.Equal(Now.AddHours(2), item.ResolvedAt);
        Assert.Equal(procedure, item.ResolvedProcedureId);
        Assert.Equal(procedureRevision, item.ResolvedProcedureRevisionId);
        Assert.False(item.BlocksBaselineApproval);
    }

    [Fact]
    public void Resolved_items_are_immutable()
    {
        var item = Introduced();
        item.Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired, "Covered by inspection.", Now);

        Assert.Throws<DomainException>(() =>
            item.Resolve("someone.else", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                "Changed my mind.", Now, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<DomainException>(() => item.AssignToEngineer("test.lead", "other.engineer", Now));
        Assert.Equal("test.engineer", item.ResolvedBy);
    }

    [Fact]
    public void Reopening_requires_a_reason_and_returns_the_item_to_its_assigned_gate_state()
    {
        var item = Introduced();
        item.AssignToEngineer("test.lead", "test.engineer", Now);
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Exact procedure coverage.", Now.AddHours(1), Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<DomainException>(() => item.Reopen("test.engineer", " ", Now.AddHours(2)));
        item.Reopen("test.engineer", "The requirement interpretation changed.", Now.AddHours(2));

        Assert.Equal(VerificationImpactState.Assigned, item.State);
        Assert.True(item.BlocksBaselineApproval);
        Assert.Null(item.Outcome);
        Assert.Null(item.ResolvedProcedureId);
        Assert.Null(item.ResolvedProcedureRevisionId);
        Assert.Null(item.ResolvedAt);
        Assert.Equal("test.engineer", item.AssignedEngineerId);
    }

    [Theory]
    [InlineData(VerificationImpactOutcome.ProcedureRetired)]
    [InlineData(VerificationImpactOutcome.ProcedureRetained)]
    public void Requirement_items_reject_procedure_only_outcomes(VerificationImpactOutcome outcome)
    {
        var item = Introduced();
        Assert.Throws<DomainException>(() => item.Resolve("test.engineer", outcome, "Not applicable here.", Now));
    }

    [Theory]
    [InlineData(VerificationImpactOutcome.ProcedureCoverageConfirmed)]
    [InlineData(VerificationImpactOutcome.NoTestRequired)]
    public void Orphaned_procedure_items_reject_requirement_only_outcomes(VerificationImpactOutcome outcome)
    {
        var item = Orphaned();
        var procedure = outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed ? Guid.NewGuid() : (Guid?)null;
        Assert.Throws<DomainException>(() => item.Resolve("test.engineer", outcome, "Not applicable here.", Now, procedure));
    }

    [Fact]
    public void Orphaned_procedure_items_can_be_retired_or_deliberately_retained()
    {
        var retired = Orphaned();
        retired.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetired,
            "No remaining requirement links after SYSR-000004 retirement.", Now);
        Assert.Equal(VerificationImpactOutcome.ProcedureRetired, retired.Outcome);

        var retained = Orphaned();
        retained.Resolve("test.engineer", VerificationImpactOutcome.ProcedureRetained,
            "Kept as a regression procedure for the oceanic route campaign.", Now);
        Assert.Equal(VerificationImpactOutcome.ProcedureRetained, retained.Outcome);
        Assert.False(retained.BlocksBaselineApproval);
    }

    [Fact]
    public void Item_follows_its_change_request_when_the_release_is_retargeted()
    {
        var item = Introduced();
        var deferredTo = Guid.NewGuid();

        item.Retarget(deferredTo, Now.AddDays(1));

        Assert.Equal(deferredTo, item.ReleaseId);
        Assert.Throws<DomainException>(() => item.Retarget(Guid.Empty, Now));
    }

    [Fact]
    public void Item_requires_its_project_release_change_request_and_subject()
    {
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForIntroducedRequirement(
            Guid.Empty, Release, ChangeRequest, Guid.NewGuid(), "SYSR-000001.00", "Test", Now));
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForIntroducedRequirement(
            Project, Guid.Empty, ChangeRequest, Guid.NewGuid(), "SYSR-000001.00", "Test", Now));
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForIntroducedRequirement(
            Project, Release, Guid.Empty, Guid.NewGuid(), "SYSR-000001.00", "Test", Now));
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForIntroducedRequirement(
            Project, Release, ChangeRequest, Guid.Empty, "SYSR-000001.00", "Test", Now));  // no requirement change
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForIntroducedRequirement(
            Project, Release, ChangeRequest, Guid.NewGuid(), "  ", "Test", Now));
        Assert.Throws<DomainException>(() => VerificationImpactItem.ForOrphanedProcedure(
            Project, Release, ChangeRequest, Guid.Empty, "SYSTP-000009.00", Now));
    }
}

public sealed class SuspectCoverageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void New_coverage_is_trusted_and_carried_forward_coverage_is_not()
    {
        var authored = new TestRequirementCoverage(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(authored.IsSuspect);

        var carried = TestRequirementCoverage.CarriedForward(Guid.NewGuid(), Guid.NewGuid(),
            "Carried forward from the predecessor baseline; the requirement statement changed.", Now);

        Assert.True(carried.IsSuspect);
        Assert.Equal(Now, carried.SuspectSince);
        Assert.Contains("statement changed", carried.SuspectReason);
        Assert.Null(carried.ConfirmedBy);
    }

    [Fact]
    public void Carried_forward_coverage_requires_a_reason()
    {
        Assert.Throws<DomainException>(() =>
            TestRequirementCoverage.CarriedForward(Guid.NewGuid(), Guid.NewGuid(), "  ", Now));
    }

    [Fact]
    public void A_verification_engineer_clears_suspicion_and_the_confirmation_is_attributable()
    {
        var carried = TestRequirementCoverage.CarriedForward(Guid.NewGuid(), Guid.NewGuid(), "Requirement modified.", Now);

        carried.ConfirmStillValid("test.engineer", Now.AddHours(3));

        Assert.False(carried.IsSuspect);
        Assert.Equal("test.engineer", carried.ConfirmedBy);
        Assert.Equal(Now.AddHours(3), carried.ConfirmedAt);
        Assert.Equal("", carried.SuspectReason);
        Assert.Throws<DomainException>(() => carried.ConfirmStillValid("", Now));
    }
}

public sealed class VerificationImpactRevisionBindingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Requirement_items_anchor_to_the_approved_change_and_bind_the_revision_at_materialisation()
    {
        // Revisions do not exist when a change request is approved; they are created when a baseline is
        // materialised. The item must therefore be usable before any revision exists.
        var change = Guid.NewGuid();
        var item = VerificationImpactItem.ForIntroducedRequirement(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            change, "SYSR-000007.00", "Test", Now);

        Assert.Equal(change, item.RequirementChangeId);
        Assert.Null(item.RequirementRevisionId);

        var revision = Guid.NewGuid();
        item.LinkRequirementRevision(revision, Now.AddDays(1));
        Assert.Equal(revision, item.RequirementRevisionId);

        Assert.Throws<DomainException>(() => item.LinkRequirementRevision(Guid.Empty, Now));
    }

    [Fact]
    public void Orphaned_procedure_items_have_no_requirement_revision_to_bind()
    {
        var item = VerificationImpactItem.ForOrphanedProcedure(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "SYSTP-000009.00", Now);

        Assert.Throws<DomainException>(() => item.LinkRequirementRevision(Guid.NewGuid(), Now));
    }
}

public sealed class VerificationImpactProcedureNamingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static VerificationImpactItem Item() => VerificationImpactItem.ForIntroducedRequirement(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SYSR-000010.00", "Test", Now);

    [Fact]
    public void Confirming_coverage_must_name_a_procedure()
    {
        // The exact coverage link cannot exist yet — it binds a revision that only materialisation creates —
        // so naming the procedure is what keeps the claim checkable rather than prose.
        var item = Item();
        Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.ProcedureCoverageConfirmed, "Covered somewhere.", Now));
        Assert.Throws<DomainException>(() => item.Resolve("test.engineer",
            VerificationImpactOutcome.ProcedureCoverageConfirmed, "Covered somewhere.", Now, Guid.Empty));

        var procedure = Guid.NewGuid();
        var procedureRevision = Guid.NewGuid();
        item.Resolve("test.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Covered by SYSTP-000012.", Now, procedure, procedureRevision);
        Assert.Equal(procedure, item.ResolvedProcedureId);
        Assert.Equal(procedureRevision, item.ResolvedProcedureRevisionId);
    }

    [Fact]
    public void Outcomes_other_than_confirmed_coverage_must_not_name_a_procedure()
    {
        Assert.Throws<DomainException>(() => Item().Resolve("test.engineer",
            VerificationImpactOutcome.NoTestRequired, "By analysis.", Now, Guid.NewGuid()));

        var ok = Item();
        ok.Resolve("test.engineer", VerificationImpactOutcome.NoTestRequired, "By analysis.", Now);
        Assert.Null(ok.ResolvedProcedureId);
    }
}
