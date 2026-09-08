using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>The compatibility role plus the exact authority decision frozen on a review step.</summary>
public readonly record struct AuthorityResolution(ProgramRole? Role, ProjectAuthorityDecision Decision);

/// <summary>
/// Application-facing workflow authority resolution.
///
/// Workflow administration remains an HTTP concern, but review submission and restart need the same
/// effective-authority answer as the signing gate. Keeping that answer here prevents one endpoint module from
/// reaching into another endpoint module and makes the frozen authority decision independently testable.
/// </summary>
public sealed class WorkflowAuthorityService(AeroLinkDbContext db)
{
    public async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(Guid projectId,
        ChangeRequestType type, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(projectId, SubjectOf(type, ladderPolicy), ct))?.Specification();

    public async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(Guid projectId,
        TestChangeReviewDiscipline discipline, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(projectId, SubjectOf(discipline, ladderPolicy), ct))?.Specification();

    public async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(Guid projectId,
        VerificationArtifactKey key, CancellationToken ct, ILadderPolicy? ladderPolicy = null) =>
        (await ActiveAsync(projectId, SubjectOf(key, ladderPolicy), ct))?.Specification();

    public async Task<ReviewWorkflowSpecification?> HistoricalSpecificationAsync(Guid projectId,
        Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is null) return null;
        return (await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.Id == workflowId && x.ProjectId == projectId, ct))?.Specification();
    }

    public async Task<Dictionary<Guid, ProgramRole?>> AuthoritiesAsync(Guid projectId,
        IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var resolved = await AuthoritiesWithDecisionsAsync(projectId, userIds, ct);
        return resolved.ToDictionary(x => x.Key, x => x.Value.Role);
    }

    /// <summary>Resolves effective authority and retains the source needed to freeze provenance.</summary>
    public async Task<Dictionary<Guid, AuthorityResolution>> AuthoritiesWithDecisionsAsync(Guid projectId,
        IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null || userIds.Count == 0) return [];
        var resolver = new ProjectAuthorityResolver(db);
        var now = DateTimeOffset.UtcNow;
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null && userIds.Contains(x.UserId))
            .Select(x => new { x.Id, x.UserId, x.Role }).ToListAsync(ct);
        var result = new Dictionary<Guid, AuthorityResolution>();
        foreach (var userId in userIds.Distinct())
        {
            foreach (var candidate in ParticipationAuthorities)
            {
                var decision = await resolver.ResolveAsync(userId, programId.Value,
                    ProjectAuthorityRequirement.LegacyRoleDemand(candidate,
                        allowProgramAdministratorSubstitution: true), now, ct);
                if (!decision.Granted) continue;
                ProgramRole? role = decision.Source == ProjectAuthoritySource.AdministratorSubstitution
                    ? ProgramRole.Administrator
                    : decision.Source == ProjectAuthoritySource.DirectBaseRole
                        ? memberships.Where(m => m.UserId == userId && m.Id == decision.SourceId)
                            .Select(m => (ProgramRole?)m.Role).FirstOrDefault()
                        : candidate;
                result[userId] = new AuthorityResolution(role, decision);
                break;
            }
            if (!result.ContainsKey(userId))
                result[userId] = new AuthorityResolution(null, ProjectAuthorityDecision.Denied);
        }
        return result;
    }

    public async Task<ProgramRole?> StageAuthorityAsync(Guid projectId, Guid userId,
        ProgramRole requiredRole, CancellationToken ct) =>
        (await StageAuthorityWithDecisionAsync(projectId, userId, requiredRole, ct)).Role;

    /// <summary>Legacy-role resolution with exact authority provenance.</summary>
    public async Task<AuthorityResolution> StageAuthorityWithDecisionAsync(Guid projectId, Guid userId,
        ProgramRole requiredRole, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return new(null, ProjectAuthorityDecision.Denied);
        var decision = await new ProjectAuthorityResolver(db).ResolveAsync(userId, programId.Value,
            ProjectAuthorityRequirement.LegacyRoleDemand(requiredRole,
                allowProgramAdministratorSubstitution: true), DateTimeOffset.UtcNow, ct);
        if (!decision.Granted) return new(null, decision);
        if (decision.Source == ProjectAuthoritySource.AdministratorSubstitution)
            return new(ProgramRole.Administrator, decision);
        if (decision.Source == ProjectAuthoritySource.DirectBaseRole && decision.SourceId is { } sourceId)
        {
            var sourceRole = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.Id == sourceId && x.ProgramId == programId && x.UserId == userId
                    && x.EndedAt == null)
                .Select(x => (ProgramRole?)x.Role).SingleOrDefaultAsync(ct);
            if (sourceRole is not null) return new(sourceRole, decision);
        }
        return new(requiredRole, decision);
    }

    public async Task<ProgramRole?> StageAuthorityAsync(Guid projectId, Guid userId,
        ReviewStageRequirement stage, CancellationToken ct) =>
        (await StageAuthorityWithDecisionAsync(projectId, userId, stage, ct)).Role;

    /// <summary>Stage-aware resolution uses the exact authority requirement recorded in the workflow.</summary>
    public async Task<AuthorityResolution> StageAuthorityWithDecisionAsync(Guid projectId, Guid userId,
        ReviewStageRequirement stage, CancellationToken ct)
    {
        if (stage.AuthorityKind is null)
            return await StageAuthorityWithDecisionAsync(projectId, userId, stage.RequiredRole, ct);
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return new(null, ProjectAuthorityDecision.Denied);
        var decision = await new ProjectAuthorityResolver(db).ResolveAsync(userId, programId.Value,
            stage.RequiredAuthority, DateTimeOffset.UtcNow, ct);
        if (!decision.Granted) return new(null, decision);
        return new(decision.Source == ProjectAuthoritySource.AdministratorSubstitution
            ? ProgramRole.Administrator : stage.RequiredRole, decision);
    }

    public static ReviewSubject SubjectOf(ChangeRequestType type, ILadderPolicy? ladderPolicy = null) =>
        (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(type);

    public static ReviewSubject SubjectOf(TestChangeReviewDiscipline discipline, ILadderPolicy? ladderPolicy = null) =>
        (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(discipline);

    public static ReviewSubject SubjectOf(VerificationArtifactKey key, ILadderPolicy? ladderPolicy = null) =>
        (ladderPolicy ?? LegacyLadderPolicy.Instance).WorkflowSubject(key);

    private async Task<ReviewWorkflow?> ActiveAsync(Guid projectId, ReviewSubject subject, CancellationToken ct) =>
        await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.AppliesTo == subject
                                       && x.State == ReviewWorkflowState.Active, ct);

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
}
