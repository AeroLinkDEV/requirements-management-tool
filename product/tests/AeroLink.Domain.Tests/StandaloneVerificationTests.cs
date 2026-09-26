using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>DEC-144: the Standalone parent kind for verification in a project without Requirements.</summary>
public sealed class StandaloneVerificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    [Fact]
    public void Standalone_names_no_parents_and_no_rationale_and_only_where_the_parent_would_be_a_requirement()
    {
        var parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Standalone,
            VerificationParentArtifactKind.Requirement, [], null, "System Procedure");
        VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Standalone,
            VerificationParentArtifactKind.Requirement, [], "   ", "software Case");

        Assert.Contains("names no exact parents", Assert.Throws<DomainException>(() =>
            VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Standalone,
                VerificationParentArtifactKind.Requirement, [parent], null, "software Case")).Message);
        Assert.Contains("carries no Derived rationale", Assert.Throws<DomainException>(() =>
            VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Standalone,
                VerificationParentArtifactKind.Requirement, [], "Because.", "software Case")).Message);
        // A software Procedure's parent is a Case, which always exists, so it is never Standalone.
        Assert.Contains("cannot be Standalone", Assert.Throws<DomainException>(() =>
            VerificationProcedureParentPolicy.Validate(VerificationProcedureParentKind.Standalone, [], null)).Message);
        // The requirement side has no such kind: the shared policy refuses it as unclassified.
        Assert.Throws<DomainException>(() => ExactParentSelectionPolicy.Validate(
            VerificationProcedureParentPolicy.Classification(VerificationProcedureParentKind.Standalone), [], null));

        Assert.True(VerificationProcedureParentPolicy.NamesNoParents(VerificationProcedureParentKind.Standalone));
        Assert.True(VerificationProcedureParentPolicy.NamesNoParents(VerificationProcedureParentKind.Derived));
        Assert.False(VerificationProcedureParentPolicy.NamesNoParents(VerificationProcedureParentKind.Allocated));
        Assert.False(VerificationProcedureParentPolicy.NamesNoParents(VerificationProcedureParentKind.Unspecified));
    }

    [Fact]
    public void A_software_procedure_header_cannot_be_standalone()
    {
        var error = Assert.Throws<DomainException>(() => new TestProcedure(Guid.NewGuid(), "HLRTP-000001",
            "Procedure", "owner", Now, TestProcedureLevel.HighLevel,
            artifactKind: VerificationArtifactKind.Procedure, parentKind: VerificationProcedureParentKind.Standalone));
        Assert.Contains("explicit Allocated or Derived", error.Message);
        // A Case or System Procedure may be.
        _ = new TestProcedure(Guid.NewGuid(), "SYSTP-000001", "System procedure", "owner", Now,
            TestProcedureLevel.System, parentKind: VerificationProcedureParentKind.Standalone);
    }

    private static TestChangeReview SystemPackage(string driving = "[]")
    {
        var review = TestChangeReview.FromProblemReport(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "PR-00001.00", Now, "SYSTPCR-000001");
        review.RecordTestChangeRequired("engineer", Now);
        review.WriteCase("engineer", "Bench frame loss", "Problem", "Analysis", "Solution", Now);
        review.AddProcedureChange("engineer", new TestProcedureChangeDraft("SYSTP-000001", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Frame capture", "Keep every frame.",
            "Rig powered.", "1. Load. 2. Count.", "No frame lost.", "The rig loses frames.", driving,
            ParentKind: VerificationProcedureParentKind.Standalone), Now);
        return review;
    }

    [Fact]
    public void A_standalone_system_procedure_is_submitted_and_approved_like_any_other()
    {
        var review = SystemPackage();
        var change = Assert.Single(review.ProcedureChanges);
        Assert.Equal(VerificationProcedureParentKind.Standalone, change.ParentKind);
        Assert.Equal("[]", change.ParentRevisionIdsJson);
        review.SubmitForReview("engineer", [new("approver", "Approver")], true, Now);
        review.Approve("approver", "Approved.", Now);
        Assert.Equal(TestChangeReviewState.Approved, review.State);
    }

    [Fact]
    public void A_standalone_proposal_that_names_driving_requirements_is_refused_at_submission()
    {
        // Driving requirements are not quietly promoted to parents, as they would be for an Allocated proposal.
        var review = SystemPackage(JsonSerializer.Serialize(new[] { Guid.NewGuid() }));
        Assert.Equal("[]", Assert.Single(review.ProcedureChanges).ParentRevisionIdsJson);
        var error = Assert.Throws<DomainException>(() =>
            review.SubmitForReview("engineer", [new("approver", "Approver")], true, Now));
        Assert.Contains("is Standalone but still names driving requirement revisions", error.Message);
    }

    [Fact]
    public void A_software_procedure_package_refuses_a_standalone_proposal_at_submission()
    {
        var package = TestChangeReview.FromCaseChange(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure),
            "HLRTCCR-000001.00", Now, baseNumber: "HLRTPCR-000001");
        package.RecordTestChangeRequired("engineer", Now);
        package.WriteCase("engineer", "Procedure", "Problem", "Analysis", "Solution", Now);
        package.AddProcedureChange("engineer", new TestProcedureChangeDraft("HLRTP-000001", 0,
            TestProcedureLevel.HighLevel, TestProcedureChangeKind.Introduce, "Procedure", "Objective", "Pre",
            "Steps", "Expected", "Rationale", ParentKind: VerificationProcedureParentKind.Standalone,
            EnvironmentSetup: "Setup", TestData: "Data", OrderedSteps: "1. Run.", ExpectedObservations: "Pass.",
            Cleanup: "Reset.", ToolingAutomation: "Manual."), Now);
        var error = Assert.Throws<DomainException>(() =>
            package.SubmitForReview("engineer", [new("approver", "Approver")], true, Now));
        Assert.Contains("cannot be Standalone", error.Message);
    }

    [Fact]
    public void A_baseline_freezes_without_requirements_only_while_nothing_is_selected()
    {
        var baseline = new CandidateBaseline("SW-00.01", 0, Guid.NewGuid(), Guid.NewGuid(), null, "Bench", "cm", Now);
        Assert.Contains("At least one approved change request",
            Assert.Throws<DomainException>(() => baseline.Freeze("cm", Now)).Message);
        baseline.FreezeWithoutRequirements("cm", Now);
        Assert.Equal(CandidateBaselineState.Frozen, baseline.State);
        Assert.Equal(64, baseline.ContentHash.Length);
        Assert.Contains("does not use Requirements", baseline.Events.Last().Detail);
        Assert.Throws<DomainException>(() => baseline.FreezeWithoutRequirements("cm", Now));
    }
}
