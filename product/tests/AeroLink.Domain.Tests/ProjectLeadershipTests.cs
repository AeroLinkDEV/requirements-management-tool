using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The Project Leadership domain rules from #816: exactly eight positions, each requiring a base role,
/// each carrying the authority footprint its predecessor role had — with the retiring
/// ProjectEngineeringLead authority deliberately absorbed by the Project Engineer position alone.
/// </summary>
public sealed class ProjectLeadershipTests
{
    [Fact]
    public void There_are_exactly_eight_positions()
        => Assert.Equal(8, ProjectLeadership.All.Count);

    /// <summary>
    /// The positions are exactly the eight owner-decided ones in their stable UI order. The retired
    /// ProjectEngineeringLead cannot even appear here: the position enum has no such value.
    /// </summary>
    [Fact]
    public void The_positions_are_exactly_the_eight_owner_decided_ones_in_stable_order()
        => Assert.Equal(
            [
                ProjectLeadershipPosition.ProjectEngineer,
                ProjectLeadershipPosition.ProgramManager,
                ProjectLeadershipPosition.EngineeringManager,
                ProjectLeadershipPosition.ConfigurationManager,
                ProjectLeadershipPosition.SystemEngineeringLead,
                ProjectLeadershipPosition.SoftwareEngineeringLead,
                ProjectLeadershipPosition.SystemTestLead,
                ProjectLeadershipPosition.SoftwareTestLead,
            ],
            ProjectLeadership.All);

    [Theory]
    [InlineData(ProjectLeadershipPosition.ProjectEngineer, ProgramRole.ProjectEngineer)]
    [InlineData(ProjectLeadershipPosition.ProgramManager, ProgramRole.ProgramManager)]
    [InlineData(ProjectLeadershipPosition.EngineeringManager, ProgramRole.EngineeringManager)]
    [InlineData(ProjectLeadershipPosition.ConfigurationManager, ProgramRole.ConfigurationManager)]
    [InlineData(ProjectLeadershipPosition.SystemEngineeringLead, ProgramRole.SystemEngineer)]
    [InlineData(ProjectLeadershipPosition.SoftwareEngineeringLead, ProgramRole.SoftwareEngineer)]
    [InlineData(ProjectLeadershipPosition.SystemTestLead, ProgramRole.SystemTestEngineer)]
    [InlineData(ProjectLeadershipPosition.SoftwareTestLead, ProgramRole.SoftwareTestEngineer)]
    public void Every_position_requires_its_approved_base_role(ProjectLeadershipPosition position, ProgramRole required)
        => Assert.Equal(required, ProjectLeadership.RequiredBaseRole(position));

    /// <summary>
    /// The authority footprints: what an active holder answers beyond their own base-role membership.
    /// The Project Engineer position absorbed the retired ProjectEngineeringLead authority (review,
    /// approval, recovery); the discipline-lead positions carry their review/approval authority; the
    /// manager positions answer exactly their own demands.
    /// </summary>
    [Fact]
    public void The_project_engineer_position_carries_the_retired_leads_authority()
    {
        var demands = ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.ProjectEngineer);
        Assert.Contains(ProgramRole.ProjectEngineeringLead, demands);
        Assert.Contains(ProgramRole.Engineer, demands);
        Assert.Contains(ProgramRole.Reviewer, demands);
        Assert.Contains(ProgramRole.Approver, demands);
    }

    [Fact]
    public void A_discipline_lead_position_carries_review_and_approval_authority()
    {
        foreach (var position in new[]
                 {
                     ProjectLeadershipPosition.SystemEngineeringLead,
                     ProjectLeadershipPosition.SoftwareEngineeringLead,
                     ProjectLeadershipPosition.SystemTestLead,
                     ProjectLeadershipPosition.SoftwareTestLead,
                 })
        {
            var demands = ProjectLeadership.SatisfyingDemands(position);
            Assert.Contains(ProgramRole.Reviewer, demands);
            Assert.Contains(ProgramRole.Approver, demands);
        }
    }

    /// <summary>
    /// Leading verification is not doing it — the same rule the role model pinned, carried into the
    /// positions: a test-lead position answers test-lead and review demands, never a test-engineer demand.
    /// </summary>
    [Fact]
    public void A_test_lead_position_does_not_answer_a_test_engineer_demand()
    {
        Assert.DoesNotContain(ProgramRole.TestEngineer,
            ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.SystemTestLead));
        Assert.DoesNotContain(ProgramRole.TestEngineer,
            ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.SoftwareTestLead));
    }

    [Fact]
    public void Manager_positions_answer_exactly_their_own_demand()
    {
        Assert.Equal([ProgramRole.ProgramManager],
            ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.ProgramManager));
        Assert.Equal([ProgramRole.ConfigurationManager],
            ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.ConfigurationManager));
    }

    [Fact]
    public void An_engineering_manager_position_answers_engineering_demands()
        => Assert.Contains(ProgramRole.Engineer, ProjectLeadership.SatisfyingDemands(ProjectLeadershipPosition.EngineeringManager));

    [Fact]
    public void An_assignment_records_who_assigned_it_and_when()
    {
        var now = DateTimeOffset.UtcNow;
        var assignment = new ProjectLeadershipAssignment(Guid.NewGuid(), ProjectLeadershipPosition.SystemEngineeringLead,
            Guid.NewGuid(), "admin", now);
        Assert.True(assignment.IsActive);
        Assert.Null(assignment.EndedAt);
        assignment.End("admin", now.AddHours(1));
        Assert.False(assignment.IsActive);
        Assert.Equal("admin", assignment.EndedBy);
    }

    [Fact]
    public void Ending_an_assignment_requires_an_attributable_actor()
        => Assert.Throws<ArgumentException>(() =>
            new ProjectLeadershipAssignment(Guid.NewGuid(), ProjectLeadershipPosition.ProjectEngineer,
                Guid.NewGuid(), "admin", DateTimeOffset.UtcNow).End(" ", DateTimeOffset.UtcNow));

    [Fact]
    public void A_backup_designation_records_its_removal()
    {
        var now = DateTimeOffset.UtcNow;
        var backup = new ProjectLeadershipBackup(Guid.NewGuid(), ProjectLeadershipPosition.ConfigurationManager,
            Guid.NewGuid(), "admin", now);
        Assert.True(backup.IsActive);
        backup.Remove("admin", now.AddDays(2));
        Assert.False(backup.IsActive);
        Assert.Equal("admin", backup.RemovedBy);
    }
}
