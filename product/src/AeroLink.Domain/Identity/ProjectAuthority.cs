namespace AeroLink.Domain.Identity;

/// <summary>
/// What a call site is actually asking about when it asks whether somebody may act.
///
/// Before #816 there was one question — "does this person answer <c>ProgramRole.X</c>?" — and it meant two
/// different things depending on the role. For <c>SoftwareQualityAnalyst</c> it asked what job somebody does.
/// For <c>ProgramManager</c> it asked whether they hold the singular accountable position. The same call
/// answered both, so holding the base role was indistinguishable from occupying the position, and the
/// elevation the owner asked for in #816 did not exist in the code that enforces it.
///
/// Naming the question removes the ambiguity. A site that wants a discipline asks for a base role; a site
/// that wants the accountable holder asks for a leadership position; a site that must keep honouring a
/// persisted legacy demand says so explicitly and is visible as such in a search.
/// </summary>
public enum ProjectAuthorityKind
{
    /// <summary>What work this person performs. Many people may satisfy it.</summary>
    BaseRole,

    /// <summary>Whether this person currently holds the accountable position, as primary or standing backup.</summary>
    LeadershipPosition,

    /// <summary>
    /// A role-shaped demand from before the leadership model — a stored workflow stage naming
    /// <c>SystemEngineeringLead</c>, for example, or an untyped endpoint gate. Answered by non-position
    /// membership or leadership; a raw retired position membership never answers it. Deliberately explicit
    /// so these compatibility sites stay countable.
    /// </summary>
    LegacyRoleDemand,
}

/// <summary>
/// One authority question, with the kind of answer it wants. Construct through the factory methods so the
/// kind and the payload cannot disagree.
/// </summary>
public readonly record struct ProjectAuthorityRequirement
{
    private ProjectAuthorityRequirement(ProjectAuthorityKind kind, ProgramRole? role,
        ProjectLeadershipPosition? position, bool allowProgramAdministratorSubstitution = false)
    {
        Kind = kind;
        Role = role;
        Position = position;
        AllowProgramAdministratorSubstitution = allowProgramAdministratorSubstitution;
    }

    public ProjectAuthorityKind Kind { get; }

    /// <summary>Set for <see cref="ProjectAuthorityKind.BaseRole"/> and <see cref="ProjectAuthorityKind.LegacyRoleDemand"/>.</summary>
    public ProgramRole? Role { get; }

    /// <summary>Set for <see cref="ProjectAuthorityKind.LeadershipPosition"/>.</summary>
    public ProjectLeadershipPosition? Position { get; }

    /// <summary>
    /// Some persisted workflow demands deliberately allow a Program-scoped administrator to stand in.
    /// This is explicit rather than a property of every legacy demand: assurance and other controlled
    /// decisions do not inherit workflow's emergency substitution policy by accident.
    /// </summary>
    public bool AllowProgramAdministratorSubstitution { get; }

    /// <summary>"Does this person perform this job?" Base membership answers it; elevation is irrelevant.</summary>
    public static ProjectAuthorityRequirement BaseRole(ProgramRole role,
        bool allowProgramAdministratorSubstitution = false) =>
        new(ProjectAuthorityKind.BaseRole, role, null, allowProgramAdministratorSubstitution);

    /// <summary>"Is this person the accountable holder of this position?" Base membership never answers it.</summary>
    public static ProjectAuthorityRequirement Leadership(ProjectLeadershipPosition position,
        bool allowProgramAdministratorSubstitution = false) =>
        new(ProjectAuthorityKind.LeadershipPosition, null, position, allowProgramAdministratorSubstitution);

    /// <summary>A role-shaped demand that predates the split and must keep resolving both ways.</summary>
    public static ProjectAuthorityRequirement LegacyRoleDemand(
        ProgramRole role, bool allowProgramAdministratorSubstitution = false) =>
        new(ProjectAuthorityKind.LegacyRoleDemand, role, null, allowProgramAdministratorSubstitution);

    public override string ToString() => Kind switch
    {
        ProjectAuthorityKind.BaseRole => $"BaseRole:{Role}",
        ProjectAuthorityKind.LeadershipPosition => $"Leadership:{Position}",
        _ => AllowProgramAdministratorSubstitution
            ? $"LegacyRoleDemand:{Role}:ProgramAdministratorSubstitution"
            : $"LegacyRoleDemand:{Role}",
    };
}

/// <summary>
/// Where an authority actually came from.
///
/// Recorded rather than inferred because the record is evidence: a signature attributed to "direct
/// membership" when it was really a standing backup misdescribes who was accountable, and that is the kind
/// of thing an audit exists to catch.
/// </summary>
public enum ProjectAuthoritySource
{
    None,
    DirectBaseRole,
    LeadershipPrimary,
    LeadershipBackup,
    Delegation,
    AdministratorSubstitution,
    LegacyCompatibility,
}

/// <summary>
/// The answer, with its provenance, the position that carried it where one did, and the row that recorded
/// it. <paramref name="SourceId"/> is the assignment, backup, membership or delegation the authority came
/// from: a controlled signature cites it, so "which designation was this signed under" stays answerable
/// after the designation itself has moved on.
/// </summary>
public readonly record struct ProjectAuthorityDecision(
    bool Granted,
    ProjectAuthoritySource Source,
    ProjectLeadershipPosition? Position = null,
    Guid? SourceId = null)
{
    public static readonly ProjectAuthorityDecision Denied = new(false, ProjectAuthoritySource.None);

    public static ProjectAuthorityDecision From(
        ProjectAuthoritySource source, ProjectLeadershipPosition? position = null, Guid? sourceId = null) =>
        new(true, source, position, sourceId);
}
