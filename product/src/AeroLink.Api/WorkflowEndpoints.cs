using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Administration of a team's review procedure.
///
/// Teams do not review the same way, and until now the only expression of that was the author picking names
/// by hand at submission — the procedure lived in people's heads, and nothing could tell whether a given
/// review had followed it. A recorded workflow makes the procedure a thing the product can check and a thing
/// an auditor can read.
///
/// Nothing here is required. A project with no workflow submits reviews exactly as before, with free
/// approver choice, because a rule nobody has written down must not become a rule that blocks work.
/// </summary>
public static class WorkflowEndpoints
{
    /// <summary>One offered signer for one stage, carrying why they qualify so the picker can say so.</summary>
    private sealed record StageCandidate(string UserId, string Name, string Role, string Via);

    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/review-workflows", async (Guid projectId, HttpContext http, AeroLinkDbContext db,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            var rows = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                .Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows
                .Where(x => SupportsSubject(ladderPolicy, x.AppliesTo))
                .OrderBy(x => x.AppliesTo).ThenBy(x => x.Name).ThenByDescending(x => x.Version)
                .Select(Map));
        });

        app.MapPost("/api/review-workflows", async (CreateReviewWorkflowRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver,
            ProjectAuthorityResolver authority, CancellationToken ct) =>
        {
            // Deciding how a team reviews is a configuration-management act, not an authoring one.
            if (!await http.HasApprovalConfigurationAuthorityAsync(db, authority, request.ProjectId, ct))
                return Results.Forbid();
            try
            {
                var ladderPolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
                ValidateSubject(ladderPolicy, request.AppliesTo);
                var stages = request.Stages.Select(ToStageDraft).ToList();
                var workflow = new ReviewWorkflow(request.ProjectId, request.Name, request.AppliesTo, request.Mode,
                    stages, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.ReviewWorkflows.Add(workflow);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/review-workflows/{workflow.Id}", Map(workflow));
            }
            catch (DbUpdateException ex) when (IsWorkflowUniquenessConflict(ex))
            {
                return Results.Conflict(new { error = "Another review workflow version or active configuration was saved concurrently. Refresh and try again." });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/activate", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver,
            ProjectAuthorityResolver authority, CancellationToken ct) =>
        {
            var workflow = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            if (!await http.HasApprovalConfigurationAuthorityAsync(db, authority, workflow.ProjectId, ct))
                return Results.Forbid();
            try
            {
                var ladderPolicy = await policyResolver.ResolveAsync(workflow.ProjectId, ct);
                ValidateSubject(ladderPolicy, workflow.AppliesTo);
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                // Two active procedures for the same kind of change request would mean the product silently
                // choosing which rules a review was judged by. Activating one retires the other.
                var superseded = await db.ReviewWorkflows
                    .Where(x => x.ProjectId == workflow.ProjectId && x.AppliesTo == workflow.AppliesTo
                                && x.State == ReviewWorkflowState.Active && x.Id != workflow.Id)
                    .ToListAsync(ct);
                foreach (var previous in superseded) previous.Retire(actor, now);
                workflow.Activate(actor, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(workflow));
            }
            catch (DbUpdateException ex) when (IsWorkflowUniquenessConflict(ex))
            {
                return Results.Conflict(new { error = "Another review workflow version or active configuration was saved concurrently. Refresh and try again." });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/revise", async (Guid id, ReviseReviewWorkflowRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver,
            ProjectAuthorityResolver authority, CancellationToken ct) =>
        {
            var current = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (current is null) return Results.NotFound();
            if (!await http.HasApprovalConfigurationAuthorityAsync(db, authority, current.ProjectId, ct))
                return Results.Forbid();
            try
            {
                var ladderPolicy = await policyResolver.ResolveAsync(current.ProjectId, ct);
                ValidateSubject(ladderPolicy, current.AppliesTo);
                // The prior version stays exactly as it was. A completed review has to remain explainable by
                // the procedure it was actually judged against.
                var stages = request.Stages.Select(ToStageDraft).ToList();
                var next = current.Revise(request.Name, request.Mode, stages, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.ReviewWorkflows.Add(next);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/review-workflows/{next.Id}", Map(next));
            }
            catch (DbUpdateException ex) when (IsWorkflowUniquenessConflict(ex))
            {
                return Results.Conflict(new { error = "Another review workflow version or active configuration was saved concurrently. Refresh and try again." });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/retire", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectAuthorityResolver authority, CancellationToken ct) =>
        {
            var workflow = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            if (!await http.HasApprovalConfigurationAuthorityAsync(db, authority, workflow.ProjectId, ct))
                return Results.Forbid();
            try
            {
                workflow.Retire(http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(workflow));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // What the author needs before choosing approvers: the stages they must fill, and who can fill each.
        //
        // The parameter keeps its name and widens its type. A caller asking for "System" or "Software" binds
        // exactly as before, because those values kept their names when the subject widened to cover test
        // change requests, so no existing client has to change to keep working.
        app.MapGet("/api/review-workflows/applicable", async (Guid projectId, ReviewSubject type,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            try { ValidateSubject(ladderPolicy, type); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var workflow = await ActiveAsync(db, projectId, type, ct);
            if (workflow is null) return Results.Ok(new { required = false });

            var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProgramId).SingleAsync(ct);
            var now = DateTimeOffset.UtcNow;

            // Who each stage may actually name, taken from the same resolver the signing gate consults. The
            // picker used to build candidates from memberships, legacy role backups and delegations, which
            // stopped meaning authority at #816: it offered a base-role-only member for a leadership stage
            // and omitted a newly assigned lead, so it and the signature endpoint disagreed in both
            // directions. Resolving per required role makes them the same answer by construction.
            var resolver = new ProjectAuthorityResolver(db);
            // Load the roster once for role labels, then add only the few non-member accounts the resolver
            // actually returns (an exact-role delegate or the installation administrator). That keeps the
            // query scoped without hiding compatibility authority that is valid independently of membership.
            var programMembers = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null)
                .Join(db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active),
                    m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.UserName, u.DisplayName, m.Role })
                .ToListAsync(ct);
            var accountById = programMembers.DistinctBy(x => x.Id)
                .ToDictionary(x => x.Id, x => (x.UserName, x.DisplayName));
            var rolesByUser = programMembers.GroupBy(x => x.Id)
                .ToDictionary(x => x.Key, x => x.Select(member => member.Role).ToList());
            // The installation administrator is intentionally not required to hold a Program membership.
            // ResolveHoldersAsync includes that substitution because the signing gate does; load the account
            // alongside the scoped roster so the projection can actually name the holder it reports.
            var systemAdministrator = await db.UserAccounts.AsNoTracking()
                .Where(x => x.State == AccountState.Active
                            && x.UserName == IdentityService.SystemAdministratorUserName)
                .Select(x => new { x.Id, x.UserName, x.DisplayName })
                .SingleOrDefaultAsync(ct);
            if (systemAdministrator is not null)
                accountById[systemAdministrator.Id] =
                    (systemAdministrator.UserName, systemAdministrator.DisplayName);

            // What to show beside a candidate's name: the role they actually hold that answers this stage,
            // not the stage's own required role. Labelling every option with the requirement makes the
            // dropdown read as the same text repeated, which tells an author nothing about who they are
            // choosing between.
            string HeldRole(Guid userId, ProgramRole required)
            {
                var accepted = ProgramRoleAuthority.Satisfying(required);
                var held = rolesByUser.GetValueOrDefault(userId) ?? [];
                foreach (var role in held)
                    if (accepted.Contains(role)) return role.ToString();
                return required.ToString();
            }

            // A stage's label names the authority the stage demands: the job for a base-role demand, the
            // accountable position for a leadership demand, and the stored role verbatim for a legacy stage —
            // never a modern-sounding rewrite of what the row actually says.
            string AuthorityLabel(ProjectAuthorityRequirement requirement) => requirement.Kind switch
            {
                ProjectAuthorityKind.LeadershipPosition => $"Project Leadership · {ReadablePosition(requirement.Position!.Value)}",
                ProjectAuthorityKind.LegacyRoleDemand => $"Legacy authority · {ReadableRole(requirement.Role!.Value)}",
                _ => ReadableRole(requirement.Role!.Value),
            };

            // Modern stages resolve per recorded requirement through the typed resolver overload the signing
            // gate answers from; legacy stages keep the compatibility demand they were recorded under. Keying
            // by the requirement (not the raw role) is what keeps a BaseRole:ProjectEngineer stage and a
            // LeadershipPosition:ProjectEngineer stage from sharing one candidate set.
            var candidatesByRequirement = new Dictionary<string, IReadOnlyList<StageCandidate>>();
            foreach (var requirement in workflow.Stages.Select(x => x.RequiredAuthority).DistinctBy(x => x.ToString()))
            {
                var holders = await resolver.ResolveHoldersAsync(programId, requirement, now,
                    includeProgramAdministratorSubstitution: true, ct);
                var missingAccountIds = holders.Select(x => x.UserId)
                    .Where(x => !accountById.ContainsKey(x)).Distinct().ToList();
                if (missingAccountIds.Count > 0)
                {
                    var additionalAccounts = await db.UserAccounts.AsNoTracking()
                        .Where(x => missingAccountIds.Contains(x.Id) && x.State == AccountState.Active)
                        .Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                    foreach (var account in additionalAccounts)
                        accountById[account.Id] = (account.UserName, account.DisplayName);
                }
                var listed = holders.Where(x => accountById.ContainsKey(x.UserId))
                    .Select(x => new StageCandidate(
                        accountById[x.UserId].UserName, accountById[x.UserId].DisplayName,
                        x.Source == ProjectAuthoritySource.AdministratorSubstitution
                            ? ProgramRole.Administrator.ToString()
                            : requirement.Kind == ProjectAuthorityKind.LeadershipPosition
                                ? requirement.Position!.Value.ToString()
                                : HeldRole(x.UserId, requirement.Role!.Value),
                        x.Source.ToString()))
                    .ToList();
                candidatesByRequirement[requirement.ToString()] =
                    listed.DistinctBy(x => x.UserId).OrderBy(x => x.Name).ToList();
            }

            return Results.Ok(new
            {
                required = true,
                minimum = workflow.Stages.Count,
                allowsAdditional = true,
                workflow.Id,
                workflow.Name,
                workflow.Version,
                mode = workflow.Mode.ToString(),
                stages = workflow.Stages.OrderBy(x => x.Position).Select(stage => new
                {
                    stage.Position,
                    stage.Name,
                    kind = stage.Kind.ToString(),
                    requiredRole = stage.RequiredRole.ToString(),
                    authorityKind = stage.RequiredAuthorityKind?.ToString(),
                    isLegacy = stage.RequiredAuthorityKind is null,
                    requiredAuthority = RequiredAuthorityShape(stage),
                    authorityLabel = AuthorityLabel(stage.RequiredAuthority),
                    candidates = candidatesByRequirement[stage.RequiredAuthority.ToString()]
                        .Select(x => new { userId = x.UserId, name = x.Name, role = x.Role, via = x.Via }),
                }),
            });
        });
    }

    /// <summary>The active procedure for this kind of package, or null when the project records none.</summary>
    public static async Task<ReviewWorkflow?> ActiveAsync(AeroLinkDbContext db, Guid projectId,
        ReviewSubject subject, CancellationToken ct) =>
        await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.AppliesTo == subject
                                       && x.State == ReviewWorkflowState.Active, ct);

    /// <summary>A change request names its subject by its type; a test change request by its discipline.</summary>
    public static ReviewSubject SubjectOf(ChangeRequestType type, ILadderPolicy? ladderPolicy = null)
        => (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(type);

    public static ReviewSubject SubjectOf(TestChangeReviewDiscipline discipline, ILadderPolicy? ladderPolicy = null)
        => (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(discipline);

    public static ReviewSubject SubjectOf(VerificationArtifactKey key, ILadderPolicy? ladderPolicy = null)
        => (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(key);

    public static async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, ChangeRequestType type, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(db, projectId, SubjectOf(type, ladderPolicy), ct))?.Specification();

    public static async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, TestChangeReviewDiscipline discipline, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(db, projectId, SubjectOf(discipline, ladderPolicy), ct))?.Specification();

    public static async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, VerificationArtifactKey key, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(db, projectId, SubjectOf(key, ladderPolicy), ct))?.Specification();

    /// <summary>
    /// Loads the exact workflow recorded on an in-flight cycle. Revisions govern future Draft submissions;
    /// correction/restart operations inside an existing cycle must not silently switch to today's active
    /// policy.
    /// </summary>
    public static async Task<ReviewWorkflowSpecification?> HistoricalSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is null) return null;
        return (await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.Id == workflowId && x.ProjectId == projectId, ct))?.Specification();
    }

    /// <summary>
    /// The strongest effective authority each user holds on the program owning this project.
    ///
    /// This must resolve the same leadership, standing-backup, delegation, account-state and administrator
    /// rules as the signing gate. Reading raw membership rows here allowed a retired position role to become
    /// an additional signer and could freeze an unrelated base role as the signature provenance.
    /// </summary>
    public static async Task<Dictionary<Guid, ProgramRole?>> AuthoritiesAsync(AeroLinkDbContext db,
        Guid projectId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null || userIds.Count == 0) return [];
        var resolver = new ProjectAuthorityResolver(db);
        var now = DateTimeOffset.UtcNow;
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null && userIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.Role }).ToListAsync(ct);
        var result = new Dictionary<Guid, ProgramRole?>();
        foreach (var userId in userIds)
        {
            ProgramRole? resolvedRole = null;
            foreach (var candidate in ParticipationAuthorities)
            {
                var requirement = ProjectAuthorityRequirement.LegacyRoleDemand(candidate,
                    allowProgramAdministratorSubstitution: true);
                var decision = await resolver.ResolveAsync(userId, programId.Value, requirement, now, ct);
                if (!decision.Granted) continue;

                if (decision.Source == ProjectAuthoritySource.AdministratorSubstitution)
                    resolvedRole = ProgramRole.Administrator;
                else if (decision.Source == ProjectAuthoritySource.DirectBaseRole)
                {
                    var accepted = ProgramRoleAuthority.Satisfying(candidate)
                        .Where(role => !SingularProgramRoles.IsSingular(role)).ToList();
                    var held = memberships.Where(x => x.UserId == userId && accepted.Contains(x.Role))
                        .Select(x => x.Role).ToHashSet();
                    resolvedRole = accepted.Where(held.Contains).Select(role => (ProgramRole?)role).FirstOrDefault();
                }
                else
                    resolvedRole = candidate;
                break;
            }
            result[userId] = resolvedRole;
        }
        return result;
    }

    /// <summary>
    /// The authority one user actually uses to sign one configured stage.
    ///
    /// A person can hold several Program roles, and the strongest one is not necessarily the one a stage
    /// asks for: a TestLead who is also an Approver must still be able to sign the TestLead stage as a
    /// TestLead, and a Configuration Manager who is also a Program Manager signs the Configuration Manager
    /// stage as a Configuration Manager. Administrator remains a substitution authority for any stage.
    /// The resolved authority is frozen on the approval step, so the signature stays explainable after
    /// memberships change.
    /// </summary>
    public static async Task<ProgramRole?> StageAuthorityAsync(AeroLinkDbContext db, Guid projectId,
        Guid userId, ProgramRole requiredRole, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return null;
        var decision = await new ProjectAuthorityResolver(db).ResolveAsync(userId, programId.Value,
            ProjectAuthorityRequirement.LegacyRoleDemand(requiredRole,
                allowProgramAdministratorSubstitution: true), DateTimeOffset.UtcNow, ct);
        if (!decision.Granted) return null;
        if (decision.Source == ProjectAuthoritySource.AdministratorSubstitution) return ProgramRole.Administrator;

        // Preserve the actual base role on the frozen signature where a membership answered the demand.
        // Leadership, standing-backup and delegation decisions instead record the exact configured demand:
        // no raw retired position membership is consulted, and the picker and submission gate therefore
        // cannot disagree about whether the person may occupy this stage.
        if (decision.Source == ProjectAuthoritySource.DirectBaseRole)
        {
            var accepted = SingularProgramRoles.IsPositionGoverned(requiredRole)
                ? []
                : ProgramRoleAuthority.Satisfying(requiredRole)
                    .Where(role => !SingularProgramRoles.IsSingular(role)).ToList();
            var roles = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.UserId == userId && x.EndedAt == null
                            && accepted.Contains(x.Role))
                .Select(x => x.Role).ToListAsync(ct);
            if (roles.Contains(requiredRole)) return requiredRole;
            foreach (var role in accepted)
                if (roles.Contains(role)) return role;
        }
        return requiredRole;
    }

    /// <summary>
    /// The stage-aware form: an explicit-authority stage resolves through its own recorded
    /// <see cref="ReviewStageRequirement.RequiredAuthority"/> — the exact #816 requirement the candidate
    /// picker offered — while a legacy stage keeps answering under the compatibility demand it was recorded
    /// under. Freezing the required role itself (not the holder's strongest role) is what lets the domain's
    /// exact-match stage validation and this resolution agree by construction.
    /// </summary>
    public static async Task<ProgramRole?> StageAuthorityAsync(AeroLinkDbContext db, Guid projectId,
        Guid userId, ReviewStageRequirement stage, CancellationToken ct)
    {
        if (stage.AuthorityKind is null)
            return await StageAuthorityAsync(db, projectId, userId, stage.RequiredRole, ct);
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return null;
        var decision = await new ProjectAuthorityResolver(db).ResolveAsync(userId, programId.Value,
            stage.RequiredAuthority, DateTimeOffset.UtcNow, ct);
        if (!decision.Granted) return null;
        if (decision.Source == ProjectAuthoritySource.AdministratorSubstitution) return ProgramRole.Administrator;
        return stage.RequiredRole;
    }

    private static readonly ProgramRole[] ParticipationAuthorities =
    [
        ProgramRole.Administrator,
        ProgramRole.ProgramManager,
        ProgramRole.ConfigurationManager,
        ProgramRole.ProjectEngineeringLead,
        ProgramRole.EngineeringManager,
        ProgramRole.SystemEngineeringLead,
        ProgramRole.SoftwareEngineeringLead,
        ProgramRole.SystemTestLead,
        ProgramRole.SoftwareTestLead,
        ProgramRole.Approver,
        ProgramRole.TestLead,
        ProgramRole.Reviewer,
        ProgramRole.TestEngineer,
        ProgramRole.Engineer,
        ProgramRole.SoftwareQualityAnalyst,
        ProgramRole.Airworthiness,
    ];

    private static void ValidateSubject(ILadderPolicy policy, ReviewSubject subject)
    {
        _ = subject switch
        {
            ReviewSubject.System => policy.WorkflowSubject(ChangeRequestType.System),
            ReviewSubject.Software => policy.WorkflowSubject(ChangeRequestType.Software),
            ReviewSubject.Interface => policy.WorkflowSubject(ChangeRequestType.Interface),
            ReviewSubject.SystemTest => policy.WorkflowSubject(TestChangeReviewDiscipline.System),
            ReviewSubject.HighLevelSoftwareCase => policy.WorkflowSubject(TestChangeReviewDiscipline.HighLevelSoftware),
            ReviewSubject.LowLevelSoftwareCase => policy.WorkflowSubject(TestChangeReviewDiscipline.LowLevelSoftware),
            ReviewSubject.HighLevelSoftwareProcedure => policy.WorkflowSubject(new VerificationArtifactKey(
                VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure)),
            ReviewSubject.LowLevelSoftwareProcedure => policy.WorkflowSubject(new VerificationArtifactKey(
                VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure)),
            _ => throw new DomainException("The review workflow subject is not supported by the project ladder."),
        };
    }

    private static bool SupportsSubject(ILadderPolicy policy, ReviewSubject subject)
    {
        try { ValidateSubject(policy, subject); return true; }
        catch (DomainException) { return false; }
    }

    private static object Map(ReviewWorkflow x) => new
    {
        x.Id,
        x.LogicalId,
        x.ProjectId,
        x.Name,
        appliesTo = x.AppliesTo.ToString(),
        mode = x.Mode.ToString(),
        x.Version,
        state = x.State.ToString(),
        x.CreatedBy,
        x.CreatedAt,
        x.ActivatedAt,
        x.RetiredAt,
        stages = x.Stages.OrderBy(s => s.Position).Select(s => new
        {
            s.Position,
            s.Name,
            requiredRole = s.RequiredRole.ToString(),
            kind = s.Kind.ToString(),
            authorityKind = s.RequiredAuthorityKind?.ToString(),
            isLegacy = s.RequiredAuthorityKind is null,
            requiredAuthority = RequiredAuthorityShape(s),
        }),
    };

    /// <summary>
    /// The discriminated authority a stage demands, as stored. Legacy rows say so explicitly — presenting a
    /// persisted Reviewer demand as a modern base role would claim Reviewer is a current job.
    /// </summary>
    internal static object RequiredAuthorityShape(ReviewWorkflowStage stage) => stage.RequiredAuthorityKind switch
    {
        ReviewStageAuthorityKind.BaseRole =>
            new { kind = nameof(ReviewStageAuthorityKind.BaseRole), role = (string?)stage.RequiredRole.ToString(), position = (string?)null },
        ReviewStageAuthorityKind.LeadershipPosition =>
            new { kind = nameof(ReviewStageAuthorityKind.LeadershipPosition), role = (string?)null, position = stage.RequiredPosition?.ToString() },
        _ => new { kind = "LegacyRoleDemand", role = (string?)stage.RequiredRole.ToString(), position = (string?)null },
    };

    /// <summary>
    /// Converts a modern stage request into its domain draft. The server owns the cutover rules: a write
    /// without an explicit authority, with contradictory payloads, with an unrecognized kind, or demanding a
    /// legacy role outright is refused here, not hidden by the browser. The remaining vocabulary rules
    /// (Reviewer/Approver as meanings, singular positions, the configurable base-role list) stay in the
    /// domain's own <see cref="ReviewWorkflowStage.ValidateAuthority"/>, which the draft's construction runs.
    /// </summary>
    internal static ReviewWorkflowStageDraft ToStageDraft(ReviewWorkflowStageRequest request)
    {
        var authority = request.RequiredAuthority
            ?? throw new DomainException(
                $"Stage '{request.Name}' must record a required project authority. Choose a base project role or a Project Leadership position; a legacy role demand cannot be written after the cutover.");
        var kind = (authority.Kind ?? "").Trim();
        if (kind.Equals("LegacyRoleDemand", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(
                "A legacy role demand cannot be recorded as new workflow authority. Choose a base project role or a Project Leadership position.");
        if (kind.Equals(nameof(ReviewStageAuthorityKind.BaseRole), StringComparison.OrdinalIgnoreCase))
        {
            if (authority.Role is null || authority.Position is not null)
                throw new DomainException(
                    $"Stage '{request.Name}' demands a base project role: set the role and no leadership position.");
            return new ReviewWorkflowStageDraft(request.Name, authority.Role.Value, request.Kind,
                ReviewStageAuthorityKind.BaseRole);
        }
        if (kind.Equals(nameof(ReviewStageAuthorityKind.LeadershipPosition), StringComparison.OrdinalIgnoreCase))
        {
            if (authority.Position is null || authority.Role is not null)
                throw new DomainException(
                    $"Stage '{request.Name}' demands a Project Leadership position: set the position and no base role.");
            if (!Enum.TryParse<ProgramRole>(authority.Position.Value.ToString(), out var roleShape)
                || !Enum.IsDefined(roleShape))
                throw new DomainException(
                    $"'{authority.Position.Value}' does not name a recognized project authority.");
            return new ReviewWorkflowStageDraft(request.Name, roleShape, request.Kind,
                ReviewStageAuthorityKind.LeadershipPosition);
        }
        throw new DomainException(
            $"'{authority.Kind}' is not a recognized required-authority kind. Use '{nameof(ReviewStageAuthorityKind.BaseRole)}' or '{nameof(ReviewStageAuthorityKind.LeadershipPosition)}'.");
    }

    private static string ReadablePosition(ProjectLeadershipPosition position) => position switch
    {
        ProjectLeadershipPosition.ProjectEngineer => "Project Engineer",
        ProjectLeadershipPosition.ProgramManager => "Program Manager",
        ProjectLeadershipPosition.EngineeringManager => "Engineering Manager",
        ProjectLeadershipPosition.ConfigurationManager => "Configuration Manager",
        ProjectLeadershipPosition.SystemEngineeringLead => "System Engineering Lead",
        ProjectLeadershipPosition.SoftwareEngineeringLead => "Software Engineering Lead",
        ProjectLeadershipPosition.SystemTestLead => "System Test Lead",
        ProjectLeadershipPosition.SoftwareTestLead => "Software Test Lead",
        _ => position.ToString(),
    };

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
        _ => role.ToString(),
    };

    private static bool IsWorkflowUniquenessConflict(DbUpdateException exception)
    {
        var details = exception.ToString();
        return details.Contains("IX_review_workflows_ProjectId_AppliesTo_State", StringComparison.OrdinalIgnoreCase)
            || details.Contains("IX_review_workflows_LogicalId_Version", StringComparison.OrdinalIgnoreCase)
            || (details.Contains("review_workflows", StringComparison.OrdinalIgnoreCase)
                && ((details.Contains("ProjectId", StringComparison.OrdinalIgnoreCase)
                     && details.Contains("AppliesTo", StringComparison.OrdinalIgnoreCase)
                     && details.Contains("State", StringComparison.OrdinalIgnoreCase))
                    || (details.Contains("LogicalId", StringComparison.OrdinalIgnoreCase)
                        && details.Contains("Version", StringComparison.OrdinalIgnoreCase))));
    }
}

/// <summary>
/// The required project authority one stage demands, recorded explicitly since the Slice 4 cutover.
///
/// A base-role demand names the job many people may hold; a leadership demand names the accountable
/// position exactly one person occupies (with its standing backup). Reviewer and Approver are signature
/// meanings carried by the stage kind, not authorities, and a persisted legacy demand can never be written
/// as new configuration — it only ever comes back in a read of a row recorded before the cutover.
/// </summary>
public sealed record StageAuthorityRequest(string Kind, ProgramRole? Role = null,
    ProjectLeadershipPosition? Position = null);

public sealed record ReviewWorkflowStageRequest(string Name, ReviewStageKind Kind,
    StageAuthorityRequest? RequiredAuthority = null);
public sealed record CreateReviewWorkflowRequest(Guid ProjectId, string Name, ReviewSubject AppliesTo,
    ReviewMode Mode, List<ReviewWorkflowStageRequest> Stages);
public sealed record ReviseReviewWorkflowRequest(string Name, ReviewMode Mode, List<ReviewWorkflowStageRequest> Stages);
