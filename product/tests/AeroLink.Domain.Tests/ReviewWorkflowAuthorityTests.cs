using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The Slice 4 cutover in the domain: a stage records WHICH kind of authority it demands — a base role many
/// people hold, or the one accountable Project Leadership position — and independently WHAT its signature
/// means (Review or Approval). New configuration refuses the retired vocabulary; rows recorded before the
/// cutover stay legacy and are answered by the rules they were written under.
/// </summary>
public sealed class ReviewWorkflowAuthorityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ReviewWorkflow Workflow(params ReviewWorkflowStageDraft[] stages)
    {
        var workflow = new ReviewWorkflow(Guid.NewGuid(), "Authority board", ReviewSubject.System,
            ReviewMode.Sequential, stages, "config.manager", Now);
        workflow.Activate("config.manager", Now);
        return workflow;
    }

    [Fact]
    public void A_base_role_authority_is_valid_and_projects_to_the_848_requirement()
    {
        var stage = Workflow(new ReviewWorkflowStageDraft("Technical review", ProgramRole.SystemEngineer,
            ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole)).Stages.Single();

        Assert.Equal(ReviewStageAuthorityKind.BaseRole, stage.RequiredAuthorityKind);
        var requirement = stage.RequiredAuthority;
        Assert.Equal(ProjectAuthorityKind.BaseRole, requirement.Kind);
        Assert.Equal(ProgramRole.SystemEngineer, requirement.Role);
        Assert.Null(requirement.Position);
        Assert.True(requirement.AllowProgramAdministratorSubstitution);
    }

    [Fact]
    public void A_leadership_authority_is_valid_and_projects_to_the_848_requirement()
    {
        var stage = Workflow(new ReviewWorkflowStageDraft("Configuration approval", ProgramRole.ConfigurationManager,
            ReviewStageKind.Approval, ReviewStageAuthorityKind.LeadershipPosition)).Stages.Single();

        Assert.Equal(ReviewStageAuthorityKind.LeadershipPosition, stage.RequiredAuthorityKind);
        Assert.Equal(ProjectLeadershipPosition.ConfigurationManager, stage.RequiredPosition);
        var requirement = stage.RequiredAuthority;
        Assert.Equal(ProjectAuthorityKind.LeadershipPosition, requirement.Kind);
        Assert.Equal(ProjectLeadershipPosition.ConfigurationManager, requirement.Position);
        Assert.Null(requirement.Role);
    }

    [Fact]
    public void A_base_role_stage_cannot_demand_a_signature_meaning_or_a_retired_position()
    {
        // Validation lives on the aggregate: building the workflow is what refuses the demand.
        ReviewWorkflowStageDraft Draft(string name, ProgramRole role) =>
            new(name, role, ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole);
        Assert.Throws<DomainException>(() => Workflow(Draft("Reviewer stage", ProgramRole.Reviewer)));
        Assert.Throws<DomainException>(() => Workflow(Draft("Approver stage", ProgramRole.Approver)));
        Assert.Throws<DomainException>(() => Workflow(Draft("Lead stage", ProgramRole.ProjectEngineeringLead)));
        Assert.Throws<DomainException>(() => Workflow(Draft("Legacy lead", ProgramRole.SystemEngineeringLead)));
        // The compatibility vocabulary is not part of the modern base-role list either.
        Assert.Throws<DomainException>(() => Workflow(Draft("Generic", ProgramRole.Engineer)));
        Assert.Throws<DomainException>(() => Workflow(Draft("Legacy lead", ProgramRole.TestLead)));
    }

    [Fact]
    public void A_leadership_authority_must_name_one_of_the_eight_positions()
    {
        ReviewWorkflowStageDraft Draft(ProgramRole role) =>
            new("Not a position", role, ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition);
        Assert.Throws<DomainException>(() => Workflow(Draft(ProgramRole.SystemEngineer)));
        Assert.Throws<DomainException>(() => Workflow(Draft(ProgramRole.Airworthiness)));
        Assert.Throws<DomainException>(() => Workflow(Draft(ProgramRole.Reviewer)));
        // Every one of the eight accountable positions is valid, including the discipline leads that used to
        // be singular ProgramRoles.
        foreach (var position in ProjectLeadership.All)
            Assert.Equal(position, Workflow(new ReviewWorkflowStageDraft($"{position} stage",
                Enum.Parse<ProgramRole>(position.ToString()), ReviewStageKind.Review,
                ReviewStageAuthorityKind.LeadershipPosition)).Stages.Single().RequiredPosition);
    }

    [Fact]
    public void A_pre_cutover_stage_stays_legacy_and_is_never_reinterpreted()
    {
        var reviewerStage = Workflow(new ReviewWorkflowStageDraft("Historic generic review", ProgramRole.Reviewer)).Stages.Single();
        Assert.Null(reviewerStage.RequiredAuthorityKind);
        Assert.Equal(ProjectAuthorityKind.LegacyRoleDemand, reviewerStage.RequiredAuthority.Kind);
        Assert.Equal(ProgramRole.Reviewer, reviewerStage.RequiredAuthority.Role);
        // A stored lead-role demand keeps its exact transitional semantics too.
        var leadStage = Workflow(new ReviewWorkflowStageDraft("Historic lead review", ProgramRole.SystemEngineeringLead)).Stages.Single();
        Assert.Null(leadStage.RequiredAuthorityKind);
        Assert.Equal(ProjectAuthorityKind.LegacyRoleDemand, leadStage.RequiredAuthority.Kind);
        Assert.Equal(ProgramRole.SystemEngineeringLead, leadStage.RequiredAuthority.Role);
    }

    [Fact]
    public void The_signature_meaning_is_independent_of_the_required_authority()
    {
        var workflow = Workflow(
            new ReviewWorkflowStageDraft("Engineer review", ProgramRole.SystemEngineer,
                ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole),
            new ReviewWorkflowStageDraft("Lead review", ProgramRole.ConfigurationManager,
                ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition),
            new ReviewWorkflowStageDraft("Engineer approval", ProgramRole.SoftwareEngineer,
                ReviewStageKind.Approval, ReviewStageAuthorityKind.BaseRole),
            new ReviewWorkflowStageDraft("Lead approval", ProgramRole.SystemEngineeringLead,
                ReviewStageKind.Approval, ReviewStageAuthorityKind.LeadershipPosition));

        Assert.Equal(
            [ReviewStageKind.Review, ReviewStageKind.Review, ReviewStageKind.Approval, ReviewStageKind.Approval],
            workflow.Stages.OrderBy(x => x.Position).Select(x => x.Kind).ToArray());
        Assert.All(workflow.Stages, stage => Assert.NotNull(stage.RequiredAuthorityKind));
    }

    [Fact]
    public void The_specification_freezes_the_exact_recorded_requirement()
    {
        var workflow = Workflow(
            new ReviewWorkflowStageDraft("Base stage", ProgramRole.ConfigurationManager,
                ReviewStageKind.Approval, ReviewStageAuthorityKind.BaseRole),
            new ReviewWorkflowStageDraft("Position stage", ProgramRole.ConfigurationManager,
                ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition));

        var specification = workflow.Specification();
        Assert.Equal(ProjectAuthorityKind.BaseRole, specification.Stages[0].RequiredAuthority.Kind);
        Assert.Equal(ProjectAuthorityKind.LeadershipPosition, specification.Stages[1].RequiredAuthority.Kind);
        // Same role name, two different demands: this is the distinction the cutover exists to make.
        Assert.Equal(ProjectLeadershipPosition.ConfigurationManager, specification.Stages[1].RequiredAuthority.Position);
    }

    [Fact]
    public void An_explicit_base_role_stage_is_judged_by_exact_membership_not_leadership_elevation()
    {
        var specification = Workflow(
            new ReviewWorkflowStageDraft("Configuration review", ProgramRole.ConfigurationManager,
                ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole)).Specification();

        // The holder of the job signs it.
        specification.ValidateStage(specification.Stages[0],
            new ApproverSelection("cm", "Configuration Manager", ProgramRole.ConfigurationManager));
        // A leadership elevation is not the job: the position holder cannot ride it into a base-role stage.
        var error = Assert.Throws<DomainException>(() => specification.ValidateStage(specification.Stages[0],
            new ApproverSelection("cm", "Configuration Manager", ProgramRole.SystemEngineeringLead)));
        Assert.Contains("must be signed through", error.Message);
    }

    [Fact]
    public void An_explicit_leadership_stage_is_never_answered_by_the_base_role_alone()
    {
        var specification = Workflow(
            new ReviewWorkflowStageDraft("Lead review", ProgramRole.SystemEngineeringLead,
                ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition)).Specification();

        // The position itself answers.
        specification.ValidateStage(specification.Stages[0],
            new ApproverSelection("lead", "System Engineering Lead", ProgramRole.SystemEngineeringLead));
        // A base SystemEngineer who is merely ELIGIBLE for the position cannot answer it.
        Assert.Throws<DomainException>(() => specification.ValidateStage(specification.Stages[0],
            new ApproverSelection("engineer", "System Engineer", ProgramRole.SystemEngineer)));
    }

    [Fact]
    public void An_administrator_may_still_stand_in_for_an_explicit_stage()
    {
        var specification = Workflow(
            new ReviewWorkflowStageDraft("Lead review", ProgramRole.SystemEngineeringLead,
                ReviewStageKind.Review, ReviewStageAuthorityKind.LeadershipPosition)).Specification();
        specification.ValidateStage(specification.Stages[0],
            new ApproverSelection("admin", "Administrator", ProgramRole.Administrator));
    }

    [Fact]
    public void Revising_a_legacy_procedure_produces_an_explicit_next_version_and_retains_the_old_one()
    {
        var legacy = Workflow(new ReviewWorkflowStageDraft("Historic generic review", ProgramRole.Reviewer));
        var revised = legacy.Revise("Authority board", ReviewMode.Sequential,
            [new("Technical review", ProgramRole.SystemEngineer, ReviewStageKind.Review, ReviewStageAuthorityKind.BaseRole)],
            "config.manager", Now.AddDays(1));

        Assert.Equal(legacy.LogicalId, revised.LogicalId);
        Assert.Equal(2, revised.Version);
        // The old version keeps its legacy nature; the new version is explicit.
        Assert.Null(legacy.Stages.Single().RequiredAuthorityKind);
        Assert.Equal(ReviewStageAuthorityKind.BaseRole, revised.Stages.Single().RequiredAuthorityKind);
    }
}
