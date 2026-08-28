using AeroLink.Domain.Identity;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A pre-migration characterization of the ProgramRole model exactly as #816 Slice 2 found it.
///
/// These tests pin outcomes that Slice 2 deliberately changes — most importantly that
/// `ProjectEngineeringLead` is currently a singular position and that `ProjectEngineer` membership carries
/// no review/approval authority. When the Project Leadership model lands, the deltas in this file are the
/// record of what moved; a change here without the corresponding domain change is a bug.
/// </summary>
public sealed class ProgramRoleModelCharacterizationTests
{
    private static readonly ProgramRole[] SingularAsOfSnapshot =
    [
        ProgramRole.ProjectEngineer, ProgramRole.ProgramManager, ProgramRole.EngineeringManager,
        ProgramRole.ConfigurationManager, ProgramRole.ProjectEngineeringLead,
        ProgramRole.SystemEngineeringLead, ProgramRole.SoftwareEngineeringLead,
        ProgramRole.SystemTestLead, ProgramRole.SoftwareTestLead,
    ];

    private static readonly ProgramRole[] NonSingularAsOfSnapshot =
    [
        ProgramRole.Engineer, ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.TestEngineer,
        ProgramRole.TestLead, ProgramRole.Administrator, ProgramRole.SystemEngineer,
        ProgramRole.SoftwareEngineer, ProgramRole.SoftwareQualityAnalyst, ProgramRole.Airworthiness,
        ProgramRole.SystemTestEngineer, ProgramRole.SoftwareTestEngineer,
    ];

    public static TheoryData<ProgramRole> EveryProgramRoleValue()
    {
        var data = new TheoryData<ProgramRole>();
        foreach (var value in Enum.GetValues<ProgramRole>()) data.Add(value);
        return data;
    }

    /// <summary>
    /// The four values Slice 2 moved out of membership singularity.
    ///
    /// The snapshot above records what Slice 1 found: nine singular roles, because the enum conflated "the
    /// job" with "the post". #816 split them, so these four became base eligibility — several people may
    /// perform the job, and the singular post is a <c>ProjectLeadershipAssignment</c>. The snapshot stays
    /// intact as the historical record; this is the delta against it.
    /// </summary>
    private static readonly ProgramRole[] MovedToBaseEligibilityBySlice2 =
    [
        ProgramRole.ProjectEngineer, ProgramRole.ProgramManager,
        ProgramRole.EngineeringManager, ProgramRole.ConfigurationManager,
    ];

    /// <summary>
    /// The exhaustive classification of the enum. Adding a ProgramRole value without deciding whether it is
    /// singular fails here rather than silently inheriting a default.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProgramRoleValue))]
    public void Every_role_value_is_exactly_classified_as_singular_or_not(ProgramRole role)
        => Assert.Equal(
            SingularAsOfSnapshot.Contains(role) && !MovedToBaseEligibilityBySlice2.Contains(role),
            SingularProgramRoles.IsSingular(role));

    [Fact]
    public void The_slice_1_snapshot_records_exactly_nine_singular_values()
        => Assert.Equal(9, SingularAsOfSnapshot.Length);

    /// <summary>
    /// The delta itself, pinned so a later change that quietly re-imposes membership singularity on an
    /// eligibility role — which would break atomic Replace-Leader again — fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProgramRoleValue))]
    public void Exactly_the_four_conflated_positions_became_base_eligibility(ProgramRole role)
        => Assert.Equal(MovedToBaseEligibilityBySlice2.Contains(role), SingularProgramRoles.IsBaseEligibility(role));

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
