using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A more precise job title must never take capability away.
///
/// The product asks for `Engineer` in about thirty places before allowing authoring or controlled editing.
/// Recording somebody as the System Engineer they actually are, instead of the generic Engineer they were
/// given on day one, must not stop them doing the work they did yesterday — that is the worst kind of
/// change, because it looks like a tidy-up and lands as a lockout.
/// </summary>
public sealed class ProgramRoleAuthorityTests
{
    [Theory]
    [InlineData(ProgramRole.SystemEngineer)]
    [InlineData(ProgramRole.SoftwareEngineer)]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    [InlineData(ProgramRole.EngineeringManager)]
    public void Every_engineering_job_title_satisfies_a_request_for_an_engineer(ProgramRole role)
        => Assert.Contains(role, ProgramRoleAuthority.Satisfying(ProgramRole.Engineer));

    [Fact]
    public void An_engineer_still_satisfies_a_request_for_an_engineer()
        => Assert.Contains(ProgramRole.Engineer, ProgramRoleAuthority.Satisfying(ProgramRole.Engineer));

    /// <summary>
    /// Both read everything in the Program, which membership alone grants. Neither is an engineering
    /// authority over its content, and quietly making them one would be a governance change nobody asked for.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.Airworthiness)]
    [InlineData(ProgramRole.SoftwareQualityAnalyst)]
    public void An_oversight_role_does_not_confer_engineering_authority(ProgramRole role)
        => Assert.DoesNotContain(role, ProgramRoleAuthority.Satisfying(ProgramRole.Engineer));

    /// <summary>
    /// The implication runs one way. A System Engineer is an engineer; an engineer is not a configuration
    /// manager, a Program manager, or an administrator, and no job title should quietly become one.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.ConfigurationManager)]
    [InlineData(ProgramRole.ProgramManager)]
    [InlineData(ProgramRole.Administrator)]
    [InlineData(ProgramRole.Airworthiness)]
    [InlineData(ProgramRole.SoftwareQualityAnalyst)]
    public void Every_other_authority_is_satisfied_only_by_itself(ProgramRole role)
        => Assert.Equal([role], ProgramRoleAuthority.Satisfying(role));

    /// <summary>
    /// Leading a discipline is authority, not a label.
    ///
    /// A lead reviews and approves work that a member of the same discipline cannot. Before this, naming
    /// somebody the lead also meant remembering to grant them Reviewer and Approver separately — and
    /// forgetting produced a lead who could not sign the review stage that names their own position.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    public void A_discipline_lead_can_review_and_approve(ProgramRole role)
    {
        Assert.Contains(role, ProgramRoleAuthority.Satisfying(ProgramRole.Reviewer));
        Assert.Contains(role, ProgramRoleAuthority.Satisfying(ProgramRole.Approver));
    }

    /// <summary>
    /// Leading is not the same as belonging to the discipline. An ordinary engineer gains nothing from the
    /// rule above, which is the whole point of distinguishing the lead.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.SystemEngineer)]
    [InlineData(ProgramRole.SoftwareEngineer)]
    [InlineData(ProgramRole.SystemTestEngineer)]
    [InlineData(ProgramRole.SoftwareTestEngineer)]
    [InlineData(ProgramRole.Engineer)]
    public void Belonging_to_a_discipline_does_not_confer_approval(ProgramRole role)
    {
        Assert.DoesNotContain(role, ProgramRoleAuthority.Satisfying(ProgramRole.Reviewer));
        Assert.DoesNotContain(role, ProgramRoleAuthority.Satisfying(ProgramRole.Approver));
    }

    /// <summary>
    /// The same "a precise title never removes capability" rule, applied to verification. Every place that
    /// asks for the undivided `TestEngineer` or `TestLead` has to accept the titles that name their discipline.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.SystemTestEngineer)]
    [InlineData(ProgramRole.SoftwareTestEngineer)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    public void Every_verification_job_title_satisfies_a_request_for_a_test_engineer(ProgramRole role)
        => Assert.Contains(role, ProgramRoleAuthority.Satisfying(ProgramRole.TestEngineer));

    [Theory]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    public void A_discipline_test_lead_satisfies_a_request_for_a_test_lead(ProgramRole role)
        => Assert.Contains(role, ProgramRoleAuthority.Satisfying(ProgramRole.TestLead));

    /// <summary>
    /// A test lead leads verification; it does not make them an engineer over the requirements under test.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.SystemTestEngineer)]
    [InlineData(ProgramRole.SoftwareTestEngineer)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    public void A_verification_title_does_not_confer_engineering_authority(ProgramRole role)
        => Assert.DoesNotContain(role, ProgramRoleAuthority.Satisfying(ProgramRole.Engineer));

    /// <summary>
    /// The Project Engineer is accountable for a project's engineering, so the thirty-odd places that ask for
    /// `Engineer` have to accept them exactly as they accept a System Engineer.
    /// </summary>
    [Fact]
    public void The_project_engineer_satisfies_a_request_for_an_engineer()
        => Assert.Contains(ProgramRole.ProjectEngineer, ProgramRoleAuthority.Satisfying(ProgramRole.Engineer));

    /// <summary>
    /// Exactly one person holds each of these on a project. The disciplines beneath them have many members and
    /// one lead; these have a holder and nothing beneath them.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.ProjectEngineer)]
    [InlineData(ProgramRole.ProgramManager)]
    [InlineData(ProgramRole.EngineeringManager)]
    [InlineData(ProgramRole.ConfigurationManager)]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    public void A_position_one_person_holds_is_recorded_as_singular(ProgramRole role)
        => Assert.True(SingularProgramRoles.IsSingular(role));

    /// <summary>
    /// A discipline is not singular. A project has many system engineers and one System Engineering Lead, and
    /// treating the membership itself as singular would stop the second engineer joining.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.SystemEngineer)]
    [InlineData(ProgramRole.SoftwareEngineer)]
    [InlineData(ProgramRole.SystemTestEngineer)]
    [InlineData(ProgramRole.SoftwareTestEngineer)]
    [InlineData(ProgramRole.Engineer)]
    [InlineData(ProgramRole.Reviewer)]
    [InlineData(ProgramRole.Approver)]
    [InlineData(ProgramRole.SoftwareQualityAnalyst)]
    [InlineData(ProgramRole.Airworthiness)]
    public void A_discipline_or_general_authority_is_not_singular(ProgramRole role)
        => Assert.False(SingularProgramRoles.IsSingular(role));

    [Theory]
    [InlineData(ProgramRole.Engineer)]
    [InlineData(ProgramRole.SystemEngineer)]
    [InlineData(ProgramRole.SoftwareEngineer)]
    [InlineData(ProgramRole.SystemEngineeringLead)]
    [InlineData(ProgramRole.SoftwareEngineeringLead)]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    [InlineData(ProgramRole.EngineeringManager)]
    [InlineData(ProgramRole.TestEngineer)]
    [InlineData(ProgramRole.TestLead)]
    [InlineData(ProgramRole.ProjectEngineer)]
    [InlineData(ProgramRole.SystemTestEngineer)]
    [InlineData(ProgramRole.SoftwareTestEngineer)]
    [InlineData(ProgramRole.SystemTestLead)]
    [InlineData(ProgramRole.SoftwareTestLead)]
    public void Engineering_and_verification_engineering_roles_can_own_problem_reports(ProgramRole role)
        => Assert.True(ProblemReportOwnerAuthority.IsEligible([role]));

    [Theory]
    [InlineData(ProgramRole.Reviewer)]
    [InlineData(ProgramRole.Approver)]
    [InlineData(ProgramRole.ConfigurationManager)]
    [InlineData(ProgramRole.ProgramManager)]
    [InlineData(ProgramRole.SoftwareQualityAnalyst)]
    [InlineData(ProgramRole.Airworthiness)]
    public void Oversight_and_approval_only_roles_cannot_hold_problem_report_ownership(ProgramRole role)
        => Assert.False(ProblemReportOwnerAuthority.IsEligible([role]));

    [Theory]
    [InlineData(ProgramRole.ProjectEngineeringLead)]
    [InlineData(ProgramRole.EngineeringManager)]
    [InlineData(ProgramRole.ProgramManager)]
    public void Explicit_supervision_can_recover_an_ineligible_problem_report_owner(ProgramRole role)
        => Assert.True(ProblemReportOwnerAuthority.CanRecover([role]));
}
