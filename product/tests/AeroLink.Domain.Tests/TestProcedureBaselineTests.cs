using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A build says exactly which requirements it holds. These are about it saying exactly which test procedures
/// verify them, on the same terms.
/// </summary>
public sealed class TestProcedureBaselineTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Test_work_can_join_a_baseline_after_the_requirements_are_frozen()
    {
        var baseline = Frozen();
        var tcr = ApprovedTestChangeRequest();

        // The difference from a change request, and the reason for it: a procedure is written against a
        // requirement, so it is finished after the requirement is fixed. Requiring it before the freeze would
        // hold the requirement baseline open waiting for work that cannot start yet.
        baseline.SelectTestChangeRequest(tcr, "verification.lead", Now.AddDays(1));

        Assert.Single(baseline.TestChangeSelections);
        Assert.Equal("SYSTPCR-000042.00", baseline.TestChangeSelections.Single().TestChangeRequestDisplayNumber);
        Assert.Contains(baseline.Events, x => x.EventType == "TestChangeRequestSelected");
    }

    [Fact]
    public void Only_approved_test_work_that_found_something_to_do_can_be_carried()
    {
        var baseline = Frozen();

        var open = RaisedTestChangeRequest();
        open.RecordTestChangeRequired("verification.engineer", Now);
        Assert.Throws<DomainException>(() => baseline.SelectTestChangeRequest(open, "verification.lead", Now));

        // An assessment that concluded nothing was needed has no procedures and no controlled number. Carrying
        // it would put an empty package in the manifest and imply test work that does not exist.
        var nothingToDo = RaisedTestChangeRequest();
        nothingToDo.RecordNoTestChangeRequired("verification.engineer",
            "The approved change alters wording the existing procedures already exercise.", Now);
        nothingToDo.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(1));
        nothingToDo.Approve("test.lead", "Agreed, nothing to do.", Now.AddMinutes(2));
        Assert.Throws<DomainException>(() => baseline.SelectTestChangeRequest(nothingToDo, "verification.lead", Now));
    }

    [Fact]
    public void A_test_change_request_from_another_build_cannot_be_carried()
    {
        var baseline = Frozen();
        var elsewhere = new TestChangeReview(ProjectId, Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00039.00", Now);
        elsewhere.RecordTestChangeRequired("verification.engineer", Now);
        elsewhere.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
        elsewhere.AssignControlledNumber("SYSTPCR-000043", Now);
        elsewhere.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft("SYSTP-000900", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Elsewhere", "Objective",
            "Preconditions", "Steps", "Expected", "Raised against another build.",
            $"[\"{Guid.NewGuid()}\"]"), Now);
        elsewhere.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(1));
        elsewhere.Approve("test.lead", "Reviewed.", Now.AddMinutes(2));

        Assert.Throws<DomainException>(() => baseline.SelectTestChangeRequest(elsewhere, "verification.lead", Now));
    }

    [Fact]
    public void Procedures_cannot_be_fixed_before_the_requirements_they_verify()
    {
        var draft = Draft();
        Assert.Throws<DomainException>(() => draft.MarkTestProceduresMaterialized("cm", Hash, 1, Now));

        var frozen = Frozen(materializeRequirements: false);
        // Frozen is not enough. A procedure verifies a requirement, and until the requirement revisions exist
        // there is nothing for a procedure revision to be bound to.
        Assert.Throws<DomainException>(() => frozen.MarkTestProceduresMaterialized("cm", Hash, 1, Now));
    }

    [Fact]
    public void An_in_work_materialized_procedure_manifest_stays_open_until_release()
    {
        var baseline = Frozen();
        var tcr = ApprovedTestChangeRequest();
        baseline.SelectTestChangeRequest(tcr, "verification.lead", Now.AddDays(1));
        baseline.MarkTestProceduresMaterialized("cm", Hash, 12, Now.AddDays(2));

        Assert.Equal(Now.AddDays(2), baseline.TestProceduresMaterializedAt);
        Assert.Equal(Hash, baseline.TestProceduresHash);
        // #726: the verification artifact manifest is assembled incrementally while a build is in work — a
        // Case materializes first, then its allocated Procedure package is selected and materialized next.
        // Only a Released baseline closes the manifest.
        var second = ApprovedTestChangeRequest();
        baseline.SelectTestChangeRequest(second, "verification.lead", Now.AddDays(3));
        baseline.MarkTestProceduresMaterialized("cm", new string('b', 64), 13, Now.AddDays(4));
        baseline.MarkReleased("cm", Now.AddDays(5));
        Assert.Throws<DomainException>(() => baseline.RemoveTestChangeRequest(second, "verification.lead", Now.AddDays(6)));
        Assert.Throws<DomainException>(() => baseline.MarkTestProceduresMaterialized("cm", Hash, 12, Now.AddDays(6)));
        Assert.Contains(baseline.Events, x => x.EventType == "TestProceduresMaterialized");
    }

    [Fact]
    public void An_unmaterialized_procedure_baseline_does_not_hold_up_the_release()
    {
        var baseline = Frozen();

        // Every build released so far has no procedure manifest. Gating release on one would make those builds
        // retrospectively invalid rather than simply unmaterialized, so it is a decision to take openly.
        baseline.MarkReleased("cm", Now.AddDays(1));

        Assert.Equal(CandidateBaselineState.Released, baseline.State);
        Assert.Null(baseline.TestProceduresMaterializedAt);
    }

    [Fact]
    public void A_released_baseline_cannot_accept_or_materialize_test_procedure_work()
    {
        var baseline = Frozen();
        baseline.MarkReleased("cm", Now.AddDays(1));
        var tcr = ApprovedTestChangeRequest();

        // D-029: a released baseline is immutable. Ordinary TCR selection and manifest materialization are
        // configuration mutations and must be refused even though the manifest was never fixed.
        Assert.Throws<DomainException>(() => baseline.SelectTestChangeRequest(tcr, "verification.lead", Now.AddDays(2)));
        Assert.Throws<DomainException>(() => baseline.MarkTestProceduresMaterialized("cm", Hash, 12, Now.AddDays(2)));
    }

    [Fact]
    public void A_released_baseline_cannot_remove_a_test_change_request()
    {
        var baseline = Frozen();
        var tcr = ApprovedTestChangeRequest();
        baseline.SelectTestChangeRequest(tcr, "verification.lead", Now.AddDays(1));
        baseline.MarkReleased("cm", Now.AddDays(2));

        Assert.Throws<DomainException>(() => baseline.RemoveTestChangeRequest(tcr, "verification.lead", Now.AddDays(3)));
        Assert.Single(baseline.TestChangeSelections);
    }

    [Fact]
    public void A_procedure_revision_names_the_test_change_request_that_produced_it()
    {
        var procedureId = Guid.NewGuid();
        var tcrId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();

        var revision = new TestProcedureRevision(procedureId, 0, "Objective", "Preconditions", "Steps",
            "Expected", TestProcedureState.Approved, "verification.engineer", Now,
            sourceTestChangeRequestId: tcrId, effectiveBaselineId: baselineId);

        Assert.Equal(tcrId, revision.SourceTestChangeRequestId);
        Assert.Equal(baselineId, revision.EffectiveBaselineId);

        // A revision written before test-procedure change was controlled genuinely has no package behind it.
        // Recording "unknown" is honest; inventing an identifier would not be.
        var legacy = new TestProcedureRevision(procedureId, 1, "Objective", "", "Steps", "Expected",
            TestProcedureState.Approved, "verification.engineer", Now);
        Assert.Null(legacy.SourceTestChangeRequestId);
        Assert.Null(legacy.EffectiveBaselineId);
    }

    [Fact]
    public void Procedure_title_and_draft_owner_validation_and_source_json_constructor_are_characterized()
    {
        Assert.Throws<DomainException>(() => new TestProcedure(ProjectId, "SYSTP-000010", "  ", "owner", Now,
            TestProcedureLevel.System));

        var procedure = new TestProcedure(ProjectId, "SYSTP-000010", "Initial title", "owner", Now,
            TestProcedureLevel.System);
        Assert.Throws<DomainException>(() => procedure.UpdateDraft("Revised title", "  ", Now.AddMinutes(1)));
        Assert.Throws<DomainException>(() => procedure.UpdateDraft("  ", "owner", Now.AddMinutes(1)));
        procedure.UpdateDraft("  Revised title  ", "  revised.owner  ", Now.AddMinutes(1));
        Assert.Equal("Revised title", procedure.Title);
        Assert.Equal("revised.owner", procedure.OwnerId);

        Assert.Throws<DomainException>(() => new TestProcedureRevision(Guid.NewGuid(), 0, "Objective",
            "Preconditions", "Steps", "", TestProcedureState.Approved, "verification.engineer", Now));
        Assert.Throws<DomainException>(() => new TestProcedureRevision(Guid.NewGuid(), 0, "Objective", "", "Steps",
            "Expected", TestProcedureState.Approved, "verification.engineer", Now,
            sourceChangeRequestsJson: "{not-json"));
        var blankSource = new TestProcedureRevision(Guid.NewGuid(), 0, "Objective", "", "Steps", "Expected",
            TestProcedureState.Approved, "verification.engineer", Now, sourceChangeRequestsJson: "  ");
        Assert.Equal("[]", blankSource.SourceChangeRequestsJson);
    }

    [Fact]
    public void A_retired_procedure_revision_needs_no_body()
    {
        var procedureId = Guid.NewGuid();

        // The retirement withdraws the procedure rather than restating it — the exemption a retired requirement
        // revision already gets, so the two behave the same way at the same point in their lifecycle.
        var retired = new TestProcedureRevision(procedureId, 2, "", "", "", "", TestProcedureState.Retired,
            "verification.engineer", Now);
        Assert.Equal(TestProcedureState.Retired, retired.State);

        Assert.Throws<DomainException>(() => new TestProcedureRevision(procedureId, 3, "", "", "", "",
            TestProcedureState.Approved, "verification.engineer", Now));
    }

    [Fact]
    public void A_package_that_states_no_procedure_work_cannot_be_carried_into_a_build()
    {
        var baseline = Frozen();
        var empty = new TestChangeReview(ProjectId, ReleaseId, Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00039.00", Now, caseContractVersion: 0);
        empty.RecordTestChangeRequired("verification.engineer", Now);
        empty.AssignControlledNumber("SYSTPCR-000044", Now);
        empty.MarkAsLegacyHistoricalPackage("verification.engineer", Now.AddSeconds(1));
        empty.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(1));
        empty.Approve("test.lead", "Approved before procedure decisions were captured.", Now.AddMinutes(2));

        // Packages approved before procedure decisions existed are real history and stay readable. A build
        // still cannot carry work that was never stated, so the route to fixing one is to revise it.
        var error = Assert.Throws<DomainException>(() =>
            baseline.SelectTestChangeRequest(empty, "verification.lead", Now.AddDays(1)));
        Assert.Contains("carries no verification artifact decisions", error.Message);
    }

    private static CandidateBaseline Draft() =>
        new("BL-00000217", 1, ProjectId, ReleaseId, null, "FMS 3.3 Candidate", "cm", Now);

    private static CandidateBaseline Frozen(bool materializeRequirements = true)
    {
        var baseline = Draft();
        baseline.Select(ApprovedChangeRequest(), "cm", Now);
        baseline.Freeze("cm", Now.AddMinutes(1));
        if (materializeRequirements) baseline.MarkRequirementsMaterialized("cm", Hash, 1, Now.AddMinutes(2));
        return baseline;
    }

    private static SystemChangeRequest ApprovedChangeRequest()
    {
        var scr = new SystemChangeRequest("SRCR-01049", 1, ProjectId, ReleaseId,
            "Round Robin", "Problem", "Analysis", "Solution", "author", Now);
        scr.AddRequirementChange("author", "SYSR-00002375", 1, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall provide selectable Round Robin sequencing.",
            "Required for the new function.", "Test", Now);
        scr.SubmitForReview("author",
            [new ApproverSelection("systems", "Maya Chen"), new ApproverSelection("software", "David Lee"),
             new ApproverSelection("verification", "Sarah Rodriguez")], Now);
        scr.ApproveActiveStage("systems", Now);
        scr.ApproveActiveStage("software", Now);
        scr.ApproveActiveStage("verification", Now);
        return scr;
    }

    private static TestChangeReview RaisedTestChangeRequest() =>
        new(ProjectId, ReleaseId, Guid.NewGuid(), TestChangeReviewDiscipline.System, "SRCR-00039.00", Now);

    private static TestChangeReview ApprovedTestChangeRequest()
    {
        var tcr = RaisedTestChangeRequest();
        tcr.RecordTestChangeRequired("verification.engineer", Now);
        tcr.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", Now);
        tcr.AssignControlledNumber("SYSTPCR-000042", Now);
        tcr.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft("SYSTP-000123", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Oceanic waypoint sequencing",
            "Verify oceanic waypoints are sequenced in the order the active flight plan holds.",
            "The aircraft is in cruise with an active oceanic flight plan.",
            "1. Load the plan. 2. Advance past the first waypoint. 3. Read the sequencer.",
            "The next eligible oceanic waypoint is sequenced.",
            "No procedure exercises oceanic sequencing after the approved change.",
            $"[\"{Guid.NewGuid()}\"]"), Now);
        tcr.Submit("verification.engineer", "test.lead", true, Now.AddMinutes(1));
        tcr.Approve("test.lead", "Procedure decisions are complete and technically sound.", Now.AddMinutes(2));
        return tcr;
    }
}
