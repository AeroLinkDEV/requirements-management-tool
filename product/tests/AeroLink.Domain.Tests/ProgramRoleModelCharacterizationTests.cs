using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The ProgramRole model as the #816 slices reshaped it. The original pre-migration pins lived here and
/// were updated deliberately as each slice landed: Slice 3 moved `ProjectEngineer` out of the singular
/// set — it is now the multi-member eligibility role for the Project Engineer leadership position, whose
/// singularity is enforced by the Project Leadership assignment tables instead.
/// </summary>
public sealed class ProgramRoleModelCharacterizationTests
{
    private static readonly ProgramRole[] SingularAsOfSnapshot =
    [
        ProgramRole.ProjectEngineeringLead,
        ProgramRole.SystemEngineeringLead, ProgramRole.SoftwareEngineeringLead,
        ProgramRole.SystemTestLead, ProgramRole.SoftwareTestLead,
    ];

    private static readonly ProgramRole[] NonSingularAsOfSnapshot =
    [
        ProgramRole.Engineer, ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.TestEngineer,
        ProgramRole.TestLead, ProgramRole.Administrator, ProgramRole.SystemEngineer,
        ProgramRole.SoftwareEngineer, ProgramRole.SoftwareQualityAnalyst, ProgramRole.Airworthiness,
        ProgramRole.SystemTestEngineer, ProgramRole.SoftwareTestEngineer, ProgramRole.ProjectEngineer,
        ProgramRole.ProgramManager, ProgramRole.EngineeringManager, ProgramRole.ConfigurationManager,
    ];

    public static TheoryData<ProgramRole> EveryProgramRoleValue()
    {
        var data = new TheoryData<ProgramRole>();
        foreach (var value in Enum.GetValues<ProgramRole>()) data.Add(value);
        return data;
    }

    /// <summary>
    /// The exhaustive classification of the enum as the migration found it. Adding a ProgramRole value
    /// without deciding whether it is singular fails here rather than silently inheriting a default.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProgramRoleValue))]
    public void Every_role_value_is_exactly_classified_as_singular_or_not(ProgramRole role)
        => Assert.Equal(
            SingularAsOfSnapshot.Contains(role),
            SingularProgramRoles.IsSingular(role));

    [Fact]
    public void The_snapshot_records_exactly_five_singular_values()
        => Assert.Equal(5, SingularAsOfSnapshot.Length);

    /// <summary>
    /// #816 retires ProjectEngineeringLead. Until Slice 2 performs that migration, it is still a singular
    /// position with live authority — pinning that fact here is what makes its removal a visible delta.
    /// </summary>
    [Fact]
    public void ProjectEngineeringLead_is_still_a_singular_position()
        => Assert.True(SingularProgramRoles.IsSingular(ProgramRole.ProjectEngineeringLead));

    /// <summary>
    /// The retiring role currently carries engineering authority and review/approval authority, but it is
    /// not verification authority: a ProjectEngineeringLead neither verifies nor distributes verification
    /// work, so a request for a test lead or test engineer must not accept them.
    /// </summary>
    [Fact]
    public void ProjectEngineeringLead_does_not_satisfy_verification_demands()
    {
        Assert.DoesNotContain(ProgramRole.ProjectEngineeringLead, ProgramRoleAuthority.Satisfying(ProgramRole.TestLead));
        Assert.DoesNotContain(ProgramRole.ProjectEngineeringLead, ProgramRoleAuthority.Satisfying(ProgramRole.TestEngineer));
    }

    /// <summary>
    /// Holding the base Project Engineer job is not the same as carrying review or approval authority.
    /// Under #816 the leadership elevation is what will grant signing authority — today even the singular
    /// ProjectEngineer membership has none beyond engineering, which the migration must preserve as the
    /// base-role state of a future leader.
    /// </summary>
    [Fact]
    public void ProjectEngineer_membership_does_not_satisfy_review_or_approval()
    {
        Assert.DoesNotContain(ProgramRole.ProjectEngineer, ProgramRoleAuthority.Satisfying(ProgramRole.Reviewer));
        Assert.DoesNotContain(ProgramRole.ProjectEngineer, ProgramRoleAuthority.Satisfying(ProgramRole.Approver));
    }

    /// <summary>
    /// The Program-scoped Administrator role is its own authority: no engineering or approval demand
    /// accepts it through the satisfying set. (The global administrator bypass is a different mechanism
    /// entirely — see the IdentityService characterization tests.)
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.Engineer)]
    [InlineData(ProgramRole.Reviewer)]
    [InlineData(ProgramRole.Approver)]
    [InlineData(ProgramRole.TestEngineer)]
    [InlineData(ProgramRole.TestLead)]
    public void The_program_administrator_role_is_never_an_implicit_engineering_or_signing_authority(ProgramRole demanded)
        => Assert.DoesNotContain(ProgramRole.Administrator, ProgramRoleAuthority.Satisfying(demanded));
}
