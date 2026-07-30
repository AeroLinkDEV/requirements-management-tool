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
    [InlineData(ProgramRole.Approver)]
    [InlineData(ProgramRole.TestEngineer)]
    [InlineData(ProgramRole.TestLead)]
    public void Every_other_authority_is_satisfied_only_by_itself(ProgramRole role)
        => Assert.Equal([role], ProgramRoleAuthority.Satisfying(role));
}
