using AeroLink.Domain.Assurance;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The rules #711 says the product must hold: a recommendation states its basis honestly, a relaxation is a
/// governed record rather than a setting, and exactly one resolver decides who may approve one.
/// </summary>
public sealed class AssurancePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Program = Guid.NewGuid();
    private static readonly Guid Proposer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static AssuranceApproverFacts Person(params ProgramRole[] roles) =>
        new(Approver, "approver", roles, [], roles.Contains(ProgramRole.Administrator), []);

    private static AssuranceApproverFacts Delegate(ProgramRole role, DateTimeOffset startsAt, DateTimeOffset endsAt,
        Guid? programId = null, bool revoked = false) =>
        new(Approver, "delegate", [], [new(role, programId ?? Program, startsAt, endsAt, revoked)], false, []);

    // ---- The catalogue's honesty obligations -------------------------------------------------------------

    [Fact]
    public void Every_shipped_recommendation_is_an_AeroLink_rule_with_a_named_enforcement_point()
    {
        Assert.NotEmpty(AssurancePolicyCatalogue.All);
        foreach (var definition in AssurancePolicyCatalogue.All)
        {
            // #711 ships no certification-derived mapping. A recommendation labelled as published guidance
            // would be a claim nobody has approved for this installation.
            Assert.Equal(AssuranceBasisKind.AeroLinkRule, definition.BasisKind);
            Assert.False(string.IsNullOrWhiteSpace(definition.EnforcementPoint));
            Assert.False(string.IsNullOrWhiteSpace(definition.RecommendationBasis));
            Assert.False(string.IsNullOrWhiteSpace(definition.ReleaseEffect));
            Assert.True(definition.Accepts(definition.RecommendedValue));
            Assert.True(definition.Options.Count >= 2);
            Assert.Equal(definition.Options.Count, definition.Options.Select(x => x.Value).Distinct().Count());
            Assert.False(definition.IsRelaxation(definition.RecommendedValue));
        }
    }

    [Fact]
    public void Same_level_coverage_is_stated_as_an_AeroLink_rule_and_not_attributed_to_published_guidance()
    {
        var coverage = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.RequirementCoverageBeforeRelease);
        Assert.Equal(AssuranceBasisKind.AeroLinkRule, coverage.BasisKind);
        Assert.Contains("AeroLink rule", coverage.RecommendationBasis);
        foreach (var definition in AssurancePolicyCatalogue.All)
        {
            Assert.DoesNotContain("DO-178", definition.RecommendationBasis, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DO-178", definition.ReleaseEffect, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("compliant", definition.RecommendationBasis, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_project_with_no_recorded_policy_runs_on_the_recommendations()
    {
        var resolved = ResolvedAssurancePolicy.Recommended;
        Assert.Equal(AssuranceLevel.NotDeclared, resolved.DeclaredLevel);
        Assert.Null(resolved.PolicyVersionId);
        foreach (var definition in AssurancePolicyCatalogue.All)
        {
            Assert.Equal(definition.RecommendedValue, resolved.Value(definition.Lever));
            Assert.False(resolved.IsRelaxed(definition.Lever));
        }
    }

    [Fact]
    public void A_stricter_selection_is_not_a_relaxation_and_a_looser_one_is()
    {
        var waivers = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.ProblemReportWaiverAcceptance);
        Assert.False(waivers.IsRelaxation(AssuranceLeverValue.WaiversRefused));
        var coverage = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.RequirementCoverageBeforeRelease);
        Assert.True(coverage.IsRelaxation(AssuranceLeverValue.NotRequired));
    }

    // ---- The one shared approval-authority resolver ------------------------------------------------------

    /// <summary>
    /// SQA is a base assurance role and membership is the right question for it — unchanged by #816.
    /// </summary>
    [Fact]
    public void An_ordinary_project_policy_deviation_accepts_an_sqa_by_membership()
    {
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.SoftwareQualityAnalyst), Now);
        Assert.True(decision.Permitted);
        Assert.Equal(ProgramRole.SoftwareQualityAnalyst, decision.SatisfiedBy);
        Assert.Equal(AssuranceAuthoritySource.Membership, decision.Source);
        Assert.Equal(AssuranceAuthorityPolicy.CurrentVersion, decision.PolicyVersion);
    }

    /// <summary>
    /// Program Manager is an accountable position since #816, so the deviation takes whoever holds it —
    /// not everybody granted the role that merely makes them eligible for it.
    /// </summary>
    [Fact]
    public void An_ordinary_project_policy_deviation_accepts_the_program_manager_leadership()
    {
        var holder = new AssuranceApproverFacts(Approver, "approver",
            [ProgramRole.ProgramManager], [], false, [ProgramRole.ProgramManager]);
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, holder, Now);
        Assert.True(decision.Permitted);
        Assert.Equal(ProgramRole.ProgramManager, decision.SatisfiedBy);
        Assert.Equal(AssuranceAuthoritySource.ProjectLeadership, decision.Source);
        Assert.Equal(AssuranceAuthorityPolicy.CurrentVersion, decision.PolicyVersion);
    }

    [Fact]
    public void The_program_manager_base_role_alone_cannot_approve_a_project_policy_deviation()
    {
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.ProgramManager), Now);
        Assert.False(decision.Permitted);
        Assert.Equal(AssuranceAuthoritySource.None, decision.Source);
    }

    [Fact]
    public void Version_one_preserves_its_original_program_manager_membership_meaning()
    {
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.ProgramManager), Now, policyVersion: 1);

        Assert.True(decision.Permitted);
        Assert.Equal(AssuranceAuthoritySource.Membership, decision.Source);
        Assert.Equal(1, decision.PolicyVersion);
    }

    [Theory]
    [InlineData(AssuranceDeviationClass.Verification)]
    [InlineData(AssuranceDeviationClass.Independence)]
    [InlineData(AssuranceDeviationClass.Evidence)]
    [InlineData(AssuranceDeviationClass.ReleaseGate)]
    public void Verification_independence_evidence_and_release_gate_deviations_require_sqa_specifically(
        AssuranceDeviationClass deviationClass)
    {
        Assert.True(AssuranceDeviationAuthority
            .Decide(deviationClass, Program, Proposer, Person(ProgramRole.SoftwareQualityAnalyst), Now).Permitted);
        var manager = AssuranceDeviationAuthority
            .Decide(deviationClass, Program, Proposer, Person(ProgramRole.ProgramManager), Now);
        Assert.False(manager.Permitted);
        Assert.Contains("Software Quality Analyst", manager.Reason);
    }

    [Fact]
    public void An_airworthiness_designated_deviation_requires_airworthiness_and_outranks_the_levers_own_class()
    {
        var definition = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.ChangeImpactDispositionBeforeRelease);
        Assert.Equal(AssuranceDeviationClass.ProjectPolicy, AssurancePolicyDeviation.ClassOf(definition, false));
        Assert.Equal(AssuranceDeviationClass.Airworthiness, AssurancePolicyDeviation.ClassOf(definition, true));

        Assert.True(AssuranceDeviationAuthority
            .Decide(AssuranceDeviationClass.Airworthiness, Program, Proposer, Person(ProgramRole.Airworthiness), Now).Permitted);
        Assert.False(AssuranceDeviationAuthority
            .Decide(AssuranceDeviationClass.Airworthiness, Program, Proposer, Person(ProgramRole.SoftwareQualityAnalyst), Now).Permitted);
    }

    [Fact]
    public void Administrator_access_alone_carries_no_assurance_authority()
    {
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.Administrator), Now);
        Assert.False(decision.Permitted);
        Assert.Contains("carries no assurance authority", decision.Reason);

        // The same person, separately holding the qualifying project role, may approve.
        var alsoSqa = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.Administrator, ProgramRole.SoftwareQualityAnalyst), Now);
        Assert.True(alsoSqa.Permitted);
        Assert.Equal(ProgramRole.SoftwareQualityAnalyst, alsoSqa.SatisfiedBy);
    }

    [Fact]
    public void Configuration_manager_status_alone_does_not_authorize_a_relaxation()
    {
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ProjectPolicy,
            Program, Proposer, Person(ProgramRole.ConfigurationManager), Now);
        Assert.False(decision.Permitted);
        Assert.Contains("does not hold", decision.Reason);
    }

    [Fact]
    public void A_recorded_delegation_in_force_approves_and_an_expired_revoked_or_foreign_one_does_not()
    {
        var live = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.SoftwareQualityAnalyst, Now.AddDays(-1), Now.AddDays(1)), Now);
        Assert.True(live.Permitted);
        Assert.Equal(AssuranceAuthoritySource.Delegation, live.Source);

        Assert.False(AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.SoftwareQualityAnalyst, Now.AddDays(-10), Now.AddDays(-1)), Now).Permitted);
        Assert.False(AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.SoftwareQualityAnalyst, Now.AddDays(1), Now.AddDays(5)), Now).Permitted);
        Assert.False(AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.SoftwareQualityAnalyst, Now.AddDays(-1), Now.AddDays(1), revoked: true), Now).Permitted);
        // Scoped to a Program: a delegation raised elsewhere is not authority here.
        Assert.False(AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.SoftwareQualityAnalyst, Now.AddDays(-1), Now.AddDays(1), programId: Guid.NewGuid()), Now).Permitted);
        // And to the authority type: an Approver delegation does not answer for SQA.
        Assert.False(AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Delegate(ProgramRole.Approver, Now.AddDays(-1), Now.AddDays(1)), Now).Permitted);
    }

    [Fact]
    public void Self_approval_is_refused_however_much_authority_the_proposer_holds()
    {
        var facts = new AssuranceApproverFacts(Proposer, "proposer",
            [ProgramRole.SoftwareQualityAnalyst, ProgramRole.ProgramManager, ProgramRole.Airworthiness], [], false,
            [ProgramRole.ProgramManager]);
        foreach (var deviationClass in Enum.GetValues<AssuranceDeviationClass>())
        {
            var decision = AssuranceDeviationAuthority.Decide(deviationClass, Program, Proposer, facts, Now);
            Assert.False(decision.Permitted);
            Assert.Contains("Self-approval is prohibited", decision.Reason);
        }
    }

    [Fact]
    public void Authority_rules_are_versioned_data_and_an_unknown_version_is_refused()
    {
        Assert.Equal(Enum.GetValues<AssuranceDeviationClass>().Length,
            AssuranceAuthorityPolicy.Version(AssuranceAuthorityPolicy.CurrentVersion).Count);
        foreach (var version in new[] { 1, AssuranceAuthorityPolicy.CurrentVersion })
        Assert.All(AssuranceAuthorityPolicy.Version(version), rule =>
        {
            Assert.Equal(1, rule.MinimumApprovals);
            Assert.True(rule.DelegationAllowed);
            Assert.False(rule.SelfApprovalAllowed);
            Assert.NotEmpty(rule.ApprovingRoles);
        });
        Assert.Throws<DomainException>(() => AssuranceAuthorityPolicy.Version(3));
    }

    // ---- The deviation record --------------------------------------------------------------------------

    private static AssurancePolicyDeviation ApprovedDeviation(bool airworthiness = false)
    {
        var definition = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.RequirementCoverageBeforeRelease);
        var deviationClass = AssurancePolicyDeviation.ClassOf(definition, airworthiness);
        var role = airworthiness ? ProgramRole.Airworthiness : ProgramRole.SoftwareQualityAnalyst;
        var decision = AssuranceDeviationAuthority.Decide(deviationClass, Program, Proposer, Person(role), Now);
        return AssurancePolicyDeviation.Approve(Guid.NewGuid(), Guid.NewGuid(), 2, definition, "Project",
            AssuranceLeverValue.NotRequired, "Coverage is being carried by the customer's own campaign for this build.",
            deviationClass, airworthiness, Proposer, "proposer", Approver, "approver", decision, Now);
    }

    [Fact]
    public void An_approved_deviation_records_the_recommendation_its_basis_and_the_authority_that_approved_it()
    {
        var deviation = ApprovedDeviation();
        Assert.Equal(AssuranceLeverValue.Required, deviation.RecommendedValue);
        Assert.Equal(AssuranceLeverValue.NotRequired, deviation.SelectedValue);
        Assert.Equal(AssuranceBasisKind.AeroLinkRule, deviation.BasisKind);
        Assert.Equal(AssuranceDeviationClass.Verification, deviation.DeviationClass);
        Assert.Equal(ProgramRole.SoftwareQualityAnalyst, deviation.ApprovalAuthority);
        Assert.Equal(AssuranceAuthoritySource.Membership, deviation.ApprovalAuthoritySource);
        Assert.Equal(AssuranceAuthorityPolicy.CurrentVersion, deviation.AuthorityPolicyVersion);
        Assert.Equal("Project", deviation.Scope);
        Assert.Equal(Now, deviation.EffectiveFrom);
        Assert.Null(deviation.SupersededAt);
        Assert.NotEmpty(deviation.ReleaseEffect);
        Assert.True(deviation.VerifyRecord());
    }

    [Fact]
    public void A_deviation_cannot_be_recorded_without_a_rationale_or_against_a_refused_approval()
    {
        var definition = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.RequirementCoverageBeforeRelease);
        var permitted = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Person(ProgramRole.SoftwareQualityAnalyst), Now);
        Assert.Throws<DomainException>(() => AssurancePolicyDeviation.Approve(Guid.NewGuid(), Guid.NewGuid(), 2,
            definition, "Project", AssuranceLeverValue.NotRequired, "   ", AssuranceDeviationClass.Verification,
            false, Proposer, "proposer", Approver, "approver", permitted, Now));

        var refused = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Program, Proposer,
            Person(ProgramRole.ProgramManager), Now);
        var error = Assert.Throws<DomainException>(() => AssurancePolicyDeviation.Approve(Guid.NewGuid(), Guid.NewGuid(),
            2, definition, "Project", AssuranceLeverValue.NotRequired, "Because.", AssuranceDeviationClass.Verification,
            false, Proposer, "proposer", Approver, "approver", refused, Now));
        Assert.Contains("Software Quality Analyst", error.Message);
    }

    [Fact]
    public void A_selection_that_is_not_a_relaxation_cannot_be_dressed_up_as_a_deviation()
    {
        var waivers = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.ProblemReportWaiverAcceptance);
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.ReleaseGate, Program, Proposer,
            Person(ProgramRole.SoftwareQualityAnalyst), Now);
        var error = Assert.Throws<DomainException>(() => AssurancePolicyDeviation.Approve(Guid.NewGuid(), Guid.NewGuid(),
            2, waivers, "Project", AssuranceLeverValue.WaiversRefused, "Stricter than recommended.",
            AssuranceDeviationClass.ReleaseGate, false, Proposer, "proposer", Approver, "approver", decision, Now));
        Assert.Contains("not a deviation", error.Message);
    }

    [Fact]
    public void A_deviation_is_superseded_rather_than_rewritten_and_only_once()
    {
        var deviation = ApprovedDeviation();
        var hash = deviation.RecordHash;
        deviation.Supersede("cm", "Returned to the AeroLink recommendation.", Now.AddDays(30));
        Assert.Equal(Now.AddDays(30), deviation.SupersededAt);
        Assert.Equal("cm", deviation.SupersededBy);
        Assert.False(deviation.IsEffective);
        // Closing the interval does not change what the record says was approved.
        Assert.Equal(hash, deviation.RecordHash);
        Assert.True(deviation.VerifyRecord());
        Assert.Throws<DomainException>(() => deviation.Supersede("cm", "again", Now.AddDays(31)));
    }

    // ---- Policy versions -------------------------------------------------------------------------------

    [Fact]
    public void A_policy_version_canonicalizes_every_lever_and_hashes_what_it_recorded()
    {
        var version = ProjectAssurancePolicy.Record(Guid.NewGuid(), 1, AssuranceLevel.LevelB,
            new Dictionary<AssurancePolicyLever, AssuranceLeverValue>
            {
                [AssurancePolicyLever.RequirementCoverageBeforeRelease] = AssuranceLeverValue.NotRequired,
            }, "Declare the pilot posture.", "cm", Now);

        Assert.Equal(AssuranceLevel.LevelB, version.DeclaredLevel);
        Assert.Contains("level[LevelB]", version.SelectionsSnapshot);
        Assert.Equal(AssurancePolicySnapshot.Hash(version.SelectionsSnapshot), version.SnapshotHash);
        // Levers the request omitted resolve to their recommendation rather than to nothing.
        var selections = version.Selections();
        Assert.Equal(AssuranceLeverValue.NotRequired, selections[AssurancePolicyLever.RequirementCoverageBeforeRelease]);
        Assert.Equal(AssuranceLeverValue.Required, selections[AssurancePolicyLever.TestEvidenceBeforeRelease]);
        Assert.Equal(AssuranceLeverValue.WaiversAccepted, selections[AssurancePolicyLever.ProblemReportWaiverAcceptance]);
    }

    [Fact]
    public void A_policy_version_requires_a_reason_and_is_superseded_rather_than_edited()
    {
        Assert.Throws<DomainException>(() => ProjectAssurancePolicy.Record(Guid.NewGuid(), 1,
            AssuranceLevel.LevelC, AssurancePolicyCatalogue.Recommended, "  ", "cm", Now));

        var version = ProjectAssurancePolicy.Record(Guid.NewGuid(), 1, AssuranceLevel.LevelC,
            AssurancePolicyCatalogue.Recommended, "Baseline the posture.", "cm", Now);
        var snapshot = version.SelectionsSnapshot;
        version.Supersede("cm", Now.AddDays(10));
        Assert.Equal(Now.AddDays(10), version.SupersededAt);
        Assert.Equal(snapshot, version.SelectionsSnapshot);
        Assert.False(version.IsEffective);
        Assert.Throws<DomainException>(() => version.Supersede("cm", Now.AddDays(11)));
    }
}
