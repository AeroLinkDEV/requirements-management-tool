using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;

namespace AeroLink.Domain.ChangeControl;

public enum ReviewWorkflowState { Draft, Active, Retired }

/// <summary>
/// The kind of controlled package a review procedure governs.
///
/// Separate from <see cref="ChangeRequestType"/>, which decides an identifier prefix and what a change request
/// may contain. This answers a different question — whose review board signs this — and test change requests
/// need their own answer: a program may want three signatures on a system requirement change and one on the
/// test work that follows it.
///
/// <c>System</c> and <c>Software</c> keep their names deliberately. The value is stored by name, so every
/// workflow recorded before test disciplines existed still reads back as what it always was, and this widening
/// needs no data migration.
/// </summary>
public enum ReviewSubject
{
    System,
    Software,
    Interface,
    SystemTest,
    HighLevelSoftwareTest,
    LowLevelSoftwareTest,
    /// <summary>Current high-level software Case reviews. The older Test value remains readable history.</summary>
    HighLevelSoftwareCase,
    /// <summary>Current low-level software Case reviews. The older Test value remains readable history.</summary>
    LowLevelSoftwareCase,
    /// <summary>Current high-level software Procedure reviews. The older Test value remains readable history.</summary>
    HighLevelSoftwareProcedure,
    /// <summary>Current low-level software Procedure reviews. The older Test value remains readable history.</summary>
    LowLevelSoftwareProcedure,
}

/// <summary>
/// What a signature on a stage means.
///
/// A review examines the content: is this correct, complete, and fit for what it claims. An approval sits
/// above that and acknowledges the artifact is done and being released. The same person may do either on
/// different artifacts, so this is a property of the stage rather than of the people.
///
/// Recorded because the two were previously indistinguishable — every step's authority was stamped with the
/// literal string "Reviewer" — and an electronic signature is required to carry a meaning of its own. Two
/// signatures that read identically cannot later say which of them authorised the release.
/// </summary>
public enum ReviewStageKind { Review, Approval }

/// <summary>
/// What kind of project authority a stage demands, recorded explicitly since the #816 Slice 4 cutover.
///
/// Before the cutover a stage carried only a <c>ProgramRole</c>, which could not say whether it meant the
/// job somebody performs or the accountable position somebody occupies — a stage naming
/// <c>ProjectEngineer</c> was ambiguous between "any of the project's engineers" and "the one accountable
/// Project Engineer". New configuration records the intent; rows recorded before the cutover carry no kind
/// and keep answering through the legacy compatibility policy.
/// </summary>
public enum ReviewStageAuthorityKind { BaseRole, LeadershipPosition }

/// <summary>
/// One stage of a team's review procedure: who has to sign, in what authority, and what their signature means.
///
/// A stage names an authority rather than a person. "Verification lead" survives somebody changing jobs;
/// a named individual does not, and a workflow that has to be rewritten every time somebody moves teams is
/// a workflow nobody maintains.
///
/// Since the Slice 4 cutover a stage records <em>which kind</em> of authority it demands — a base project
/// role many people may hold, or the accountable Project Leadership position exactly one person occupies —
/// through <see cref="RequiredAuthorityKind"/>. Stages recorded before the cutover carry no kind: they keep
/// their stored <see cref="RequiredRole"/> and answer through the legacy compatibility policy, so historical
/// evidence is never reinterpreted under today's vocabulary.
/// </summary>
public sealed class ReviewWorkflowStage
{
    private ReviewWorkflowStage() { }

    internal ReviewWorkflowStage(Guid workflowId, int position, string name, ProgramRole requiredRole,
        ReviewStageKind kind = ReviewStageKind.Review, ReviewStageAuthorityKind? authorityKind = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A review stage needs a name.");
        ValidateAuthority(requiredRole, authorityKind);
        Id = Guid.NewGuid();
        WorkflowId = workflowId;
        Position = position;
        Name = name.Trim();
        RequiredRole = requiredRole;
        RequiredAuthorityKind = authorityKind;
        Kind = kind;
    }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int Position { get; private set; }
    public string Name { get; private set; } = "";
    public ProgramRole RequiredRole { get; private set; }
    /// <summary>
    /// Defaults to <see cref="ReviewStageKind.Review"/>, which is what every stage recorded before this
    /// existed actually was: the step stamped its authority as "Reviewer" and nothing distinguished a release
    /// acknowledgement from a content examination. Teams mark their approval stages when they next revise.
    /// </summary>
    public ReviewStageKind Kind { get; private set; }
    /// <summary>
    /// The kind of authority this stage demands. Null on stages recorded before the Slice 4 cutover — those
    /// keep the legacy role-shaped semantics and are never produced by new configuration.
    /// </summary>
    public ReviewStageAuthorityKind? RequiredAuthorityKind { get; private set; }

    /// <summary>
    /// The authority this stage demands as one #816 requirement, so the signing gate, the candidate picker
    /// and this specification all answer from the same vocabulary.
    /// </summary>
    public ProjectAuthorityRequirement RequiredAuthority => RequiredAuthorityKind switch
    {
        ReviewStageAuthorityKind.BaseRole =>
            ProjectAuthorityRequirement.BaseRole(RequiredRole, allowProgramAdministratorSubstitution: true),
        ReviewStageAuthorityKind.LeadershipPosition =>
            ProjectAuthorityRequirement.Leadership(RequiredPosition!.Value, allowProgramAdministratorSubstitution: true),
        // Legacy rows keep the exact transitional semantics they were recorded under, administrator
        // substitution included. Reinterpreting them under today's vocabulary would rewrite history.
        _ => ProjectAuthorityRequirement.LegacyRoleDemand(RequiredRole, allowProgramAdministratorSubstitution: true),
    };

    /// <summary>The leadership position this stage demands, when it demands one.</summary>
    public ProjectLeadershipPosition? RequiredPosition => RequiredAuthorityKind == ReviewStageAuthorityKind.LeadershipPosition
        ? Enum.TryParse<ProjectLeadershipPosition>(RequiredRole.ToString(), out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null
        : null;

    /// <summary>
    /// New configuration may only demand the modern explicit authorities. Generic Reviewer/Approver are
    /// signature meanings that lived as standing roles before the cutover — a stage's meaning now comes from
    /// <see cref="ReviewStageKind"/>, so granting them as required authority would re-conflate the two
    /// questions the split exists to separate. The retired ProjectEngineeringLead and the other singular
    /// position roles are demanded through <see cref="ReviewStageAuthorityKind.LeadershipPosition"/>, never
    /// as base membership.
    /// </summary>
    internal static void ValidateAuthority(ProgramRole requiredRole, ReviewStageAuthorityKind? authorityKind)
    {
        if (authorityKind is null) return; // legacy row: recorded before the cutover, answered compatibly
        if (authorityKind == ReviewStageAuthorityKind.BaseRole)
        {
            if (requiredRole is ProgramRole.Reviewer or ProgramRole.Approver)
                throw new DomainException(
                    $"{requiredRole} is a signature meaning, not a project authority. Configure the stage's required authority explicitly and record the Review/Approval meaning in the stage kind.");
            if (SingularProgramRoles.IsSingular(requiredRole))
                throw new DomainException(
                    $"{requiredRole} is a retired leadership position, not a base role. Demand it through the LeadershipPosition authority kind.");
            if (!ConfigurableBaseRoles.Contains(requiredRole))
                throw new DomainException(
                    $"'{requiredRole}' is not a configurable base project role. Choose one of: {string.Join(", ", ConfigurableBaseRoles.Select(ReadableRole))}.");
            return;
        }
        if (!Enum.TryParse<ProjectLeadershipPosition>(requiredRole.ToString(), out var parsed)
            || !Enum.IsDefined(parsed))
            throw new DomainException(
                $"'{requiredRole}' does not name a Project Leadership position. Leadership authority must name one of the eight accountable positions.");
    }

    /// <summary>The base project roles new configuration may demand, per the #816 owner decisions.</summary>
    public static readonly IReadOnlyList<ProgramRole> ConfigurableBaseRoles =
    [
        ProgramRole.SystemEngineer, ProgramRole.SoftwareEngineer,
        ProgramRole.SystemTestEngineer, ProgramRole.SoftwareTestEngineer,
        ProgramRole.ProjectEngineer, ProgramRole.ProgramManager,
        ProgramRole.EngineeringManager, ProgramRole.ConfigurationManager,
        ProgramRole.SoftwareQualityAnalyst, ProgramRole.Airworthiness,
    ];

    private static string ReadableRole(ProgramRole role) => role switch
    {
        ProgramRole.SystemEngineer => "System Engineer",
        ProgramRole.SoftwareEngineer => "Software Engineer",
        ProgramRole.SystemTestEngineer => "System Test Engineer",
        ProgramRole.SoftwareTestEngineer => "Software Test Engineer",
        ProgramRole.ProjectEngineer => "Project Engineer",
        ProgramRole.ProgramManager => "Program Manager",
        ProgramRole.EngineeringManager => "Engineering Manager",
        ProgramRole.ConfigurationManager => "Configuration Manager",
        ProgramRole.SoftwareQualityAnalyst => "Software Quality Assurance",
        ProgramRole.Airworthiness => "Airworthiness",
        ProgramRole.Reviewer => "Reviewer",
        ProgramRole.Approver => "Approver",
        _ => role.ToString(),
    };
}

/// <summary>
/// A team's review procedure, recorded so a review can be judged by the rules that were in force when it ran.
///
/// Teams do not review the same way. One wants a peer engineer then a configuration manager; another puts
/// verification before the change board; a third runs everything in parallel with a single deadline. Until
/// now the only expression of any of that was the author picking names by hand at submission, which meant
/// the procedure lived in people's heads and nothing could tell whether a given review had followed it.
///
/// Workflows are additive. A project with none behaves exactly as before — the author selects approvers
/// freely — because a rule nobody has written down yet must not become a rule that blocks work.
///
/// A workflow that has been used is never edited in place. Changing it produces the next version, and the
/// prior one is retired but retained, because a recorded approval has to remain explainable by the procedure
/// it was actually judged against. Rewriting the procedure under a completed review would make its record
/// say something that never happened.
/// </summary>
public sealed class ReviewWorkflow
{
    private readonly List<ReviewWorkflowStage> _stages = [];
    private ReviewWorkflow() { }

    public ReviewWorkflow(Guid projectId, string name, ReviewSubject appliesTo, ReviewMode mode,
        IReadOnlyList<ReviewWorkflowStageDraft> stages, string actorId, DateTimeOffset now, int version = 1,
        Guid? logicalId = null)
    {
        if (projectId == Guid.Empty) throw new DomainException("A review workflow belongs to a project.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A review workflow needs a name.");
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("A review workflow needs an attributable author.");
        if (stages.Count == 0) throw new DomainException("A review workflow needs at least one stage.");
        if (version < 1) throw new DomainException("Workflow versions begin at one.");

        Id = Guid.NewGuid();
        LogicalId = logicalId ?? Id;
        ProjectId = projectId;
        Name = name.Trim();
        AppliesTo = appliesTo;
        Mode = mode;
        Version = version;
        State = ReviewWorkflowState.Draft;
        CreatedBy = actorId.Trim();
        CreatedAt = now;
        for (var index = 0; index < stages.Count; index++)
            _stages.Add(new ReviewWorkflowStage(Id, index, stages[index].Name, stages[index].RequiredRole,
                stages[index].Kind, stages[index].AuthorityKind));
    }

    public Guid Id { get; private set; }
    /// <summary>Stable across versions, so "this procedure" can be followed through its history.</summary>
    public Guid LogicalId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public ReviewSubject AppliesTo { get; private set; }
    public ReviewMode Mode { get; private set; }
    public int Version { get; private set; }
    public ReviewWorkflowState State { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public IReadOnlyCollection<ReviewWorkflowStage> Stages => _stages.AsReadOnly();

    public void Activate(string actorId, DateTimeOffset now)
    {
        if (State != ReviewWorkflowState.Draft) throw new DomainException("Only a draft workflow can be activated.");
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("Activating a workflow requires an attributable actor.");
        State = ReviewWorkflowState.Active;
        ActivatedAt = now;
    }

    /// <summary>
    /// Withdraws a workflow from future use. Reviews already recorded against it keep referring to it, which
    /// is why it is retired rather than deleted.
    /// </summary>
    public void Retire(string actorId, DateTimeOffset now)
    {
        if (State == ReviewWorkflowState.Retired) throw new DomainException("This workflow is already retired.");
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("Retiring a workflow requires an attributable actor.");
        State = ReviewWorkflowState.Retired;
        RetiredAt = now;
    }

    /// <summary>Produces the next version of this procedure, leaving the current one intact.</summary>
    public ReviewWorkflow Revise(string name, ReviewMode mode, IReadOnlyList<ReviewWorkflowStageDraft> stages,
        string actorId, DateTimeOffset now) =>
        new(ProjectId, name, AppliesTo, mode, stages, actorId, now, Version + 1, LogicalId);

    /// <summary>The stage requirements, in order, as the review cycle needs to see them.</summary>
    public ReviewWorkflowSpecification Specification() =>
        new(Id, LogicalId, Name, Version, Mode,
            _stages.OrderBy(x => x.Position).Select(x => new ReviewStageRequirement(x.Position, x.Name,
                x.RequiredRole, x.Kind, x.RequiredAuthorityKind)).ToList());
}

public sealed record ReviewWorkflowStageDraft(string Name, ProgramRole RequiredRole,
    ReviewStageKind Kind = ReviewStageKind.Review, ReviewStageAuthorityKind? AuthorityKind = null);
public sealed record ReviewStageRequirement(int Position, string Name, ProgramRole RequiredRole,
    ReviewStageKind Kind = ReviewStageKind.Review, ReviewStageAuthorityKind? AuthorityKind = null)
{
    /// <summary>The authority this stage demands as one #816 requirement.</summary>
    public ProjectAuthorityRequirement RequiredAuthority => AuthorityKind switch
    {
        ReviewStageAuthorityKind.BaseRole =>
            ProjectAuthorityRequirement.BaseRole(RequiredRole, allowProgramAdministratorSubstitution: true),
        ReviewStageAuthorityKind.LeadershipPosition =>
            ProjectAuthorityRequirement.Leadership(
                Enum.TryParse<ProjectLeadershipPosition>(RequiredRole.ToString(), out var parsed) && Enum.IsDefined(parsed)
                    ? parsed : throw new DomainException($"'{RequiredRole}' does not name a Project Leadership position."),
                allowProgramAdministratorSubstitution: true),
        // Legacy rows keep the exact transitional semantics they were recorded under.
        _ => ProjectAuthorityRequirement.LegacyRoleDemand(RequiredRole, allowProgramAdministratorSubstitution: true),
    };
}

/// <summary>
/// What a review must satisfy, passed to the change request at submission.
///
/// The specification is a value, not the aggregate, so the change request never reaches into workflow
/// administration to decide whether a review is well formed — and so a review can be validated against the
/// exact procedure text that was in force, rather than against whatever the workflow says today.
/// </summary>
public sealed record ReviewWorkflowSpecification(
    Guid WorkflowId, Guid LogicalId, string Name, int Version, ReviewMode Mode,
    IReadOnlyList<ReviewStageRequirement> Stages)
{
    /// <summary>
    /// Checks the chosen approvers against the procedure.
    ///
    /// The authority each person holds is resolved outside the domain and arrives on the selection, because
    /// membership lives in a different aggregate. What is enforced here is what the procedure says: the right
    /// number of approvers, in order, each holding the authority their stage demands.
    /// </summary>
    public void Validate(IReadOnlyList<ApproverSelection> approvers)
    {
        if (approvers.Count < Stages.Count)
            throw new DomainException(
                $"{Name} v{Version} requires {Stages.Count} approver{(Stages.Count == 1 ? "" : "s")} minimum (at least {Stages.Count}), one for each stage: " +
                string.Join(", ", Stages.Select(x => x.Name)) + ".");

        foreach (var stage in Stages) ValidateStage(stage, approvers[stage.Position]);

        // Configured rows are the minimum accountable positions. Additional signers are permitted, but they
        // still have to be active, attributable Program participants; the API resolves that authority and
        // passes it here rather than allowing a browser-supplied role claim to create a free-floating step.
        for (var index = Stages.Count; index < approvers.Count; index++)
        {
            if (approvers[index].Role is null)
                throw new DomainException(
                    $"{approvers[index].Name} has no active Program authority, so they cannot be added as an additional reviewer.");
        }
    }

    /// <summary>Checks one chosen approver against one stage.</summary>
    public void ValidateStage(ReviewStageRequirement stage, ApproverSelection chosen)
    {
        if (chosen.Role is null)
            throw new DomainException(
                $"{chosen.Name} has no recorded authority on this program, so they cannot sign the {stage.Name} stage.");
        // An administrator can stand in for any stage. Somebody has to be able to unblock a review when the
        // named authority is unavailable, and the substitution is recorded on the step either way.
        if (chosen.Role == ProgramRole.Administrator) return;
        // Legacy stages keep the historical implication semantics exactly as they were recorded — a stage
        // naming Reviewer or a discipline lead is judged by the rules it was written under, never
        // reinterpreted into today's vocabulary.
        if (stage.AuthorityKind is null)
        {
            if (!ProgramRoleAuthority.Satisfying(stage.RequiredRole).Contains(chosen.Role.Value))
                throw new DomainException(
                    $"The {stage.Name} stage must be signed by a {Readable(stage.RequiredRole)}. " +
                    $"{chosen.Name} holds {Readable(chosen.Role.Value)} authority.");
            return;
        }
        // Explicit authority stages are validated against the exact demand. The API froze each selection by
        // resolving the stage's own ProjectAuthorityRequirement, so the frozen authority is the authority
        // the stage asked for: a base-role holder cannot ride a leadership elevation into a base-role stage,
        // and a mere base-role member cannot answer a leadership stage.
        if (chosen.Role != stage.RequiredRole)
            throw new DomainException(
                $"The {stage.Name} stage must be signed through the {Readable(stage.RequiredRole)} authority. " +
                $"{chosen.Name} holds {Readable(chosen.Role.Value)} authority.");
    }

    private static string Readable(ProgramRole role) => role switch
    {
        ProgramRole.ConfigurationManager => "Configuration Manager",
        ProgramRole.TestEngineer => "Test Engineer",
        ProgramRole.TestLead => "Test Lead",
        ProgramRole.ProgramManager => "Program Manager",
        _ => role.ToString(),
    };
}
