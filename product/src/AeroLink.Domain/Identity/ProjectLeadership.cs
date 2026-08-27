using System.Text;

namespace AeroLink.Domain.Identity;

/// <summary>
/// The eight accountable Project Leadership positions, exactly as the product owner decided for #816.
///
/// These are not job titles somebody holds alongside a discipline — they are singular positions a person is
/// elevated into, each carrying additional authority while the assignment is active. The elevation is a
/// separate persisted fact from the base roles a person performs: holding the System Engineer role does not
/// make anybody the System Engineering Lead, and occupying the System Engineering Lead position is what
/// carries the lead's authority. `ProjectEngineeringLead` is deliberately absent — it is retired as an
/// active concept, and its legitimate authority moved to the Project Engineer position.
/// </summary>
public enum ProjectLeadershipPosition
{
    ProjectEngineer,
    ProgramManager,
    EngineeringManager,
    ConfigurationManager,
    SystemEngineeringLead,
    SoftwareEngineeringLead,
    SystemTestLead,
    SoftwareTestLead,
}

/// <summary>
/// The policy that makes the eight positions mean one thing everywhere: which base role makes a person
/// eligible for each position, and which role demands an active holder (or their standing backup) answers.
///
/// Eligibility is checked at assignment time and re-checked whenever authority is exercised — losing the
/// base role, the project membership, or an active account retires the effective authority immediately,
/// while the assignment rows stay as attributable history.
///
/// The demand footprints preserve the authority each position's predecessor carried before this model
/// existed, so existing authorization gates keep behaving for the people who legitimately hold them:
/// the four discipline-lead positions and the Project Engineer position answer the review/approval and
/// engineering demands their lead roles answered, and the Project Engineer position additionally carries
/// the retired `ProjectEngineeringLead` authority (review, approval, and recovery of stranded problem
/// reports). A base role alone never answers those — elevation is what does.
/// </summary>
public static class ProjectLeadership
{
    public static readonly IReadOnlyList<ProjectLeadershipPosition> All =
    [
        ProjectLeadershipPosition.ProjectEngineer,
        ProjectLeadershipPosition.ProgramManager,
        ProjectLeadershipPosition.EngineeringManager,
        ProjectLeadershipPosition.ConfigurationManager,
        ProjectLeadershipPosition.SystemEngineeringLead,
        ProjectLeadershipPosition.SoftwareEngineeringLead,
        ProjectLeadershipPosition.SystemTestLead,
        ProjectLeadershipPosition.SoftwareTestLead,
    ];

    /// <summary>The base project role a person must already hold to be elevated into the position.</summary>
    public static ProgramRole RequiredBaseRole(ProjectLeadershipPosition position) => position switch
    {
        ProjectLeadershipPosition.ProjectEngineer => ProgramRole.ProjectEngineer,
        ProjectLeadershipPosition.ProgramManager => ProgramRole.ProgramManager,
        ProjectLeadershipPosition.EngineeringManager => ProgramRole.EngineeringManager,
        ProjectLeadershipPosition.ConfigurationManager => ProgramRole.ConfigurationManager,
        ProjectLeadershipPosition.SystemEngineeringLead => ProgramRole.SystemEngineer,
        ProjectLeadershipPosition.SoftwareEngineeringLead => ProgramRole.SoftwareEngineer,
        ProjectLeadershipPosition.SystemTestLead => ProgramRole.SystemTestEngineer,
        ProjectLeadershipPosition.SoftwareTestLead => ProgramRole.SoftwareTestEngineer,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown Project Leadership position."),
    };

    /// <summary>
    /// The role demands an active holder of the position answers, on top of what their own base-role
    /// membership already gives them. This is the compatibility bridge: the gates that today name the
    /// predecessor roles (discipline leads, the retiring ProjectEngineeringLead) keep accepting the people
    /// who legitimately hold the new positions, and legacy workflow definitions requiring a retired role
    /// keep resolving — without ever handing that authority to somebody who merely holds the base role.
    /// </summary>
    public static IReadOnlyList<ProgramRole> SatisfyingDemands(ProjectLeadershipPosition position) => position switch
    {
        // The Project Engineer position absorbed the retired ProjectEngineeringLead authority: review,
        // approval, and the recovery of stranded Problem Reports travel with the position, not with the job.
        ProjectLeadershipPosition.ProjectEngineer =>
            [ProgramRole.ProjectEngineer, ProgramRole.ProjectEngineeringLead, ProgramRole.Engineer, ProgramRole.Reviewer, ProgramRole.Approver],
        ProjectLeadershipPosition.ProgramManager => [ProgramRole.ProgramManager],
        ProjectLeadershipPosition.EngineeringManager => [ProgramRole.EngineeringManager, ProgramRole.Engineer],
        ProjectLeadershipPosition.ConfigurationManager => [ProgramRole.ConfigurationManager],
        ProjectLeadershipPosition.SystemEngineeringLead =>
            [ProgramRole.SystemEngineeringLead, ProgramRole.SystemEngineer, ProgramRole.Engineer, ProgramRole.Reviewer, ProgramRole.Approver],
        ProjectLeadershipPosition.SoftwareEngineeringLead =>
            [ProgramRole.SoftwareEngineeringLead, ProgramRole.SoftwareEngineer, ProgramRole.Engineer, ProgramRole.Reviewer, ProgramRole.Approver],
        // Leading verification is not doing it: the lead answers review/approval and test-lead demands,
        // but a request for a test engineer must still not accept the lead.
        ProjectLeadershipPosition.SystemTestLead =>
            [ProgramRole.SystemTestLead, ProgramRole.SystemTestEngineer, ProgramRole.TestLead, ProgramRole.Reviewer, ProgramRole.Approver],
        ProjectLeadershipPosition.SoftwareTestLead =>
            [ProgramRole.SoftwareTestLead, ProgramRole.SoftwareTestEngineer, ProgramRole.TestLead, ProgramRole.Reviewer, ProgramRole.Approver],
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown Project Leadership position."),
    };

    public static IReadOnlyList<ProgramRole> SatisfyingDemands(params ProjectLeadershipPosition[] positions)
    {
        var demands = new HashSet<ProgramRole>();
        foreach (var position in positions)
            foreach (var demand in SatisfyingDemands(position))
                demands.Add(demand);
        return [.. demands];
    }
}

/// <summary>
/// The one accountable holder of a Project Leadership position, and the history of who held it.
///
/// Ending an assignment records when and by whom rather than deleting the row: "who was the Project
/// Engineer in March" has to stay answerable after a replacement. Only an unended assignment grants
/// authority, and a replacement is one transaction — the old primary ends and the new one begins with no
/// committed vacancy between them.
/// </summary>
public sealed class ProjectLeadershipAssignment
{
    private ProjectLeadershipAssignment() { }

    public ProjectLeadershipAssignment(
        Guid programId, ProjectLeadershipPosition position, Guid holderUserId, string assignedBy, DateTimeOffset now)
    {
        if (programId == Guid.Empty) throw new ArgumentException("A leadership assignment requires a program.", nameof(programId));
        if (holderUserId == Guid.Empty) throw new ArgumentException("A leadership assignment requires a holder.", nameof(holderUserId));
        if (!Enum.IsDefined(position)) throw new ArgumentOutOfRangeException(nameof(position));
        if (string.IsNullOrWhiteSpace(assignedBy)) throw new ArgumentException("Assigning a leadership position requires an attributable actor.", nameof(assignedBy));
        Id = Guid.NewGuid(); ProgramId = programId; Position = position; HolderUserId = holderUserId;
        AssignedBy = assignedBy.Trim(); AssignedAt = now;
    }

    public const int ActorMaxLength = 100;

    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public ProjectLeadershipPosition Position { get; private set; }
    public Guid HolderUserId { get; private set; }
    public string AssignedBy { get; private set; } = "";
    public DateTimeOffset AssignedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string EndedBy { get; private set; } = "";

    public bool IsActive => EndedAt is null;

    public void End(string actor, DateTimeOffset now)
    {
        if (EndedAt is not null) return;
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Ending a leadership assignment requires an attributable actor.", nameof(actor));
        if (now < AssignedAt) throw new ArgumentOutOfRangeException(nameof(now), "A leadership assignment cannot end before it began.");
        EndedAt = now;
        EndedBy = actor.Trim();
    }
}

/// <summary>
/// The standing backup of a Project Leadership position, named on the project and standing until removed.
///
/// While the designation is active the backup answers the same authority as the primary — it is not a
/// contact field. The backup must be an active current project member satisfying the position's base-role
/// eligibility, may not be the position's current primary, and loses the authority the moment the
/// designation is removed or they stop being eligible. A person may back up several positions if they are
/// genuinely eligible for each.
/// </summary>
public sealed class ProjectLeadershipBackup
{
    private ProjectLeadershipBackup() { }

    public ProjectLeadershipBackup(
        Guid programId, ProjectLeadershipPosition position, Guid backupUserId, string namedBy, DateTimeOffset now)
    {
        if (programId == Guid.Empty) throw new ArgumentException("A leadership backup requires a program.", nameof(programId));
        if (backupUserId == Guid.Empty) throw new ArgumentException("A leadership backup requires a backup holder.", nameof(backupUserId));
        if (!Enum.IsDefined(position)) throw new ArgumentOutOfRangeException(nameof(position));
        if (string.IsNullOrWhiteSpace(namedBy)) throw new ArgumentException("Naming a leadership backup requires an attributable actor.", nameof(namedBy));
        Id = Guid.NewGuid(); ProgramId = programId; Position = position; BackupUserId = backupUserId;
        NamedBy = namedBy.Trim(); NamedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public ProjectLeadershipPosition Position { get; private set; }
    public Guid BackupUserId { get; private set; }
    public string NamedBy { get; private set; } = "";
    public DateTimeOffset NamedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public string RemovedBy { get; private set; } = "";

    public bool IsActive => RemovedAt is null;

    public void Remove(string actor, DateTimeOffset now)
    {
        if (RemovedAt is not null) return;
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Removing a leadership backup requires an attributable actor.", nameof(actor));
        if (now < NamedAt) throw new ArgumentOutOfRangeException(nameof(now), "A leadership backup cannot be removed before it was named.");
        RemovedAt = now;
        RemovedBy = actor.Trim();
    }
}
