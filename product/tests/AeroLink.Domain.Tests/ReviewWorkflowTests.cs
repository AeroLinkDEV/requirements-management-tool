using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A recorded review procedure is only worth having if it is enforced, if it cannot be changed underneath a
/// review that already ran, and if introducing it does not stop a team that has not written one down.
/// </summary>
public sealed class ReviewWorkflowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly IReadOnlyList<ReviewWorkflowStageDraft> Board =
    [
        new("Peer engineering", ProgramRole.Reviewer),
        new("Configuration management", ProgramRole.ConfigurationManager),
    ];

    private static ReviewWorkflow Workflow(ReviewMode mode = ReviewMode.Sequential)
    {
        var workflow = new ReviewWorkflow(Guid.NewGuid(), "System change board", ReviewSubject.System,
            mode, Board, "config.manager", Now);
        workflow.Activate("config.manager", Now);
        return workflow;
    }

    private static SystemChangeRequest ReadyScr()
    {
        var scr = new SystemChangeRequest("SRCR-00001", 0, Guid.NewGuid(), Guid.NewGuid(), "Oceanic routing",
            "A defect exists.", "It was analyzed.", "It will be fixed.", "author", Now);
        scr.AddRequirementChange("author", "REQ-00000001", 1, RequirementLevel.System,
            RequirementChangeKind.Modify, "The FMS shall sequence waypoints.", "Because.", "Test", Now,
            impactDispositionJson: """{"trace":"Affected","verification":"Affected","documents":"Affected","baseline":"Affected","collaboration":"Affected"}""");
        return scr;
    }

    [Fact]
    public void A_project_with_no_recorded_procedure_reviews_exactly_as_before()
    {
        // Introducing workflows must not turn "we have not written our procedure down yet" into "you cannot
        // submit a change request".
        var cycle = ReadyScr().SubmitForReview("author", [new("reviewer.one", "Reviewer One")], Now);
        Assert.Single(cycle.Steps);
        Assert.Null(cycle.WorkflowId);
        Assert.Equal("", cycle.WorkflowName);
    }

    [Fact]
    public void A_new_legacy_fallback_cycle_keeps_resolved_authority_provenance()
    {
        var sourceId = Guid.NewGuid();
        var cycle = ReadyScr().SubmitForReview("author", [new ApproverSelection("approver", "Approver",
            ProgramRole.Approver, ProjectAuthoritySource.Delegation, sourceId)], Now);

        var step = Assert.Single(cycle.Steps);
        Assert.Equal(ProjectAuthoritySource.Delegation, step.AuthoritySource);
        Assert.Equal(sourceId, step.AuthoritySourceId);
    }

    [Fact]
    public void A_review_that_follows_the_procedure_records_which_one_and_which_version()
    {
        var workflow = Workflow();
        var cycle = ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager)],
            Now, workflow: workflow.Specification());

        // Recorded on the cycle so the review stays explainable after the procedure is revised.
        Assert.Equal(workflow.Id, cycle.WorkflowId);
        Assert.Equal("System change board", cycle.WorkflowName);
        Assert.Equal(1, cycle.WorkflowVersion);
        Assert.Equal("Peer engineering", cycle.Steps.Single(x => x.Position == 0).StageName);
        Assert.Equal("Configuration management", cycle.Steps.Single(x => x.Position == 1).StageName);
    }

    [Fact]
    public void Additional_signers_are_allowed_and_frozen_as_named_cycle_steps()
    {
        var workflow = Workflow().Specification();
        var cycle = ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager),
             new("extra", "Additional Engineer", ProgramRole.Engineer)],
            Now, workflow: workflow);

        Assert.Equal(3, cycle.Steps.Count);
        var extra = cycle.Steps.Single(x => x.Position == 2);
        Assert.Equal("Additional reviewer 1", extra.StageName);
        Assert.Equal(ReviewStageKind.Review, extra.StageKind);
        Assert.Equal(ProgramRole.Engineer.ToString(), extra.Authority);
    }

    [Fact]
    public void An_additional_signer_without_program_authority_is_refused()
    {
        var error = Assert.Throws<DomainException>(() => ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager),
             new("outsider", "Unrelated Account")],
            Now, workflow: Workflow().Specification()));

        Assert.Contains("no active Program authority", error.Message);
    }

    [Fact]
    public void A_cycle_keeps_its_original_workflow_specification_when_policy_is_revised()
    {
        var first = Workflow();
        var scr = ReadyScr();
        var cycle = scr.SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager)],
            Now, workflow: first.Specification());

        var revised = first.Revise("Changed board", ReviewMode.Parallel,
            [new("Different authority", ProgramRole.TestLead)], "config.manager", Now.AddDays(1));

        Assert.Equal(first.Id, cycle.WorkflowId);
        Assert.Equal(first.Version, cycle.WorkflowVersion);
        Assert.Equal("System change board", cycle.WorkflowName);
        Assert.Equal(ReviewWorkflowState.Draft, revised.State);
    }

    [Fact]
    public void An_approver_without_the_stage_authority_cannot_be_chosen()
    {
        var error = Assert.Throws<DomainException>(() => ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("someone", "Another Engineer", ProgramRole.Engineer)],
            Now, workflow: Workflow().Specification()));

        Assert.Contains("Configuration management stage must be signed by a Configuration Manager", error.Message);
        Assert.Contains("Another Engineer holds Engineer authority", error.Message);
    }

    [Fact]
    public void An_approver_with_no_authority_at_all_is_named_in_the_refusal()
    {
        var error = Assert.Throws<DomainException>(() => ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer"), new("cm", "Configuration Manager", ProgramRole.ConfigurationManager)],
            Now, workflow: Workflow().Specification()));

        Assert.Contains("Peer Engineer has no recorded authority", error.Message);
    }

    [Fact]
    public void An_administrator_can_stand_in_for_any_stage()
    {
        // Somebody has to be able to unblock a review when the named authority is on leave. A control that
        // cannot proceed at all is not a control.
        var cycle = ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("admin", "Administrator", ProgramRole.Administrator)],
            Now, workflow: Workflow().Specification());
        Assert.Equal(2, cycle.Steps.Count);
    }

    [Fact]
    public void The_wrong_number_of_approvers_is_refused_with_the_stages_named()
    {
        var error = Assert.Throws<DomainException>(() => ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer)], Now, workflow: Workflow().Specification()));

        Assert.Contains("requires 2 approvers", error.Message);
        Assert.Contains("Peer engineering, Configuration management", error.Message);
    }

    [Fact]
    public void The_procedures_own_mode_wins_over_whatever_the_author_chose()
    {
        // A team that recorded a parallel board does not want an author quietly making it sequential.
        var cycle = ReadyScr().SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager)],
            Now, ReviewMode.Sequential, Workflow(ReviewMode.Parallel).Specification());

        Assert.Equal(ReviewMode.Parallel, cycle.Mode);
        Assert.All(cycle.Steps, step => Assert.Equal(ApprovalStepState.Active, step.State));
    }

    [Fact]
    public void Swapping_in_an_approver_who_lacks_the_authority_is_refused()
    {
        var workflow = Workflow().Specification();
        var scr = ReadyScr();
        scr.SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager)],
            Now, workflow: workflow);

        // Otherwise the procedure would be satisfied at submission and quietly broken before anybody signed.
        Assert.Throws<DomainException>(() => scr.ReplaceFutureApprover("author", 1,
            new("someone", "Another Engineer", ProgramRole.Engineer), Now, workflow));

        scr.ReplaceFutureApprover("author", 1, new("cm.two", "Second CM", ProgramRole.ConfigurationManager), Now, workflow);
        Assert.Equal("Second CM", scr.ActiveReviewCycle!.Steps.Single(x => x.Position == 1).ApproverName);
    }

    [Fact]
    public void Replacing_a_future_approver_freezes_the_replacement_source()
    {
        var workflow = Workflow().Specification();
        var scr = ReadyScr();
        scr.SubmitForReview("author",
            [new("peer", "Peer Engineer", ProgramRole.Reviewer, ProjectAuthoritySource.DirectBaseRole, Guid.NewGuid()),
             new("cm", "Configuration Manager", ProgramRole.ConfigurationManager, ProjectAuthoritySource.LeadershipPrimary, Guid.NewGuid())],
            Now, workflow: workflow);
        var sourceId = Guid.NewGuid();

        scr.ReplaceFutureApprover("author", 1,
            new("cm.two", "Second CM", ProgramRole.ConfigurationManager,
                ProjectAuthoritySource.LeadershipBackup, sourceId), Now, workflow);

        var replacement = scr.ActiveReviewCycle!.Steps.Single(x => x.Position == 1);
        Assert.Equal(ProjectAuthoritySource.LeadershipBackup, replacement.AuthoritySource);
        Assert.Equal(sourceId, replacement.AuthoritySourceId);
    }

    [Fact]
    public void Revising_a_procedure_leaves_the_one_a_completed_review_was_judged_by_intact()
    {
        var first = Workflow();
        var second = first.Revise("System change board", ReviewMode.Parallel,
            [new("Verification", ProgramRole.TestLead)], "config.manager", Now.AddDays(1));

        // Same procedure, next version. Rewriting the first in place would make a recorded approval say
        // something that never happened.
        Assert.Equal(first.LogicalId, second.LogicalId);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, first.Stages.Count);
        Assert.Equal(ReviewWorkflowState.Draft, second.State);
        Assert.Equal(ReviewWorkflowState.Active, first.State);
    }

    [Fact]
    public void A_retired_procedure_stays_readable()
    {
        var workflow = Workflow();
        workflow.Retire("config.manager", Now.AddDays(1));
        Assert.Equal(ReviewWorkflowState.Retired, workflow.State);
        Assert.Equal(2, workflow.Stages.Count);
        Assert.Throws<DomainException>(() => workflow.Retire("config.manager", Now.AddDays(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_procedure_needs_a_name(string name) =>
        Assert.Throws<DomainException>(() => new ReviewWorkflow(Guid.NewGuid(), name, ReviewSubject.System,
            ReviewMode.Sequential, Board, "config.manager", Now));

    [Fact]
    public void A_procedure_with_no_stages_says_nothing() =>
        Assert.Throws<DomainException>(() => new ReviewWorkflow(Guid.NewGuid(), "Empty", ReviewSubject.System,
            ReviewMode.Sequential, [], "config.manager", Now));
}
