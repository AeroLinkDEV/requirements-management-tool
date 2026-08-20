using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// What each artifact requires before it can be released, resolved to the people who could actually sign it.
///
/// A review procedure names authorities rather than people on purpose, so it survives somebody changing jobs.
/// The cost of that is a procedure can quietly reference a position nobody holds, and nothing says so until
/// an author submits and the review stops at a stage with no one to sign it.
///
/// This reads the procedure and the roster together, which neither page can do alone: a stage naming a
/// position one person holds resolves to that person, a stage naming a discipline resolves to a count, and a
/// stage naming a position nobody holds resolves to nothing and is reported as blocking.
/// </summary>
public static class ApprovalConfigurationEndpoints
{
    /// <summary>The artifact types a project configures a procedure for, in the order the page lists them.</summary>
    private static readonly ReviewSubject[] Subjects =
    [
        ReviewSubject.System, ReviewSubject.Software,
        ReviewSubject.SystemTest, ReviewSubject.HighLevelSoftwareTest, ReviewSubject.LowLevelSoftwareTest,
    ];

    public static void MapApprovalConfigurationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/approval-configuration", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
                .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
            if (programId is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;

            // Deciding how a team reviews is a configuration-management act, matching the authority the
            // workflow routes themselves already require.
            var canManage = await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator);

            var workflows = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                .Where(x => x.ProjectId == projectId && x.State == ReviewWorkflowState.Active)
                .ToListAsync(ct);

            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.EndedAt == null)
                .Select(x => new { x.UserId, x.Role }).ToListAsync(ct);
            var backups = await db.ProjectRoleBackups.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RemovedAt == null)
                .Select(x => new { x.BackupUserId, x.Role }).ToListAsync(ct);
            var delegations = await db.RoleDelegations.AsNoTracking()
                .Where(x => x.ProgramId == programId && x.RevokedAt == null)
                .Select(x => new { x.DelegateUserId, x.Role, x.StartsAt, x.EndsAt }).ToListAsync(ct);
            // SQLite deliberately does not translate DateTimeOffset comparisons. Materialize the small,
            // program-scoped live-delegation set and apply the same interval rule in memory for both providers.
            delegations = delegations.Where(x => x.StartsAt <= now && x.EndsAt > now).ToList();
            var accountIds = memberships.Select(x => x.UserId)
                .Concat(backups.Select(x => x.BackupUserId))
                .Concat(delegations.Select(x => x.DelegateUserId))
                .Distinct().ToList();
            var names = await db.UserAccounts.AsNoTracking()
                .Where(x => accountIds.Contains(x.Id) && x.State == AccountState.Active)
                .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

            // Who satisfies a stage: anybody holding a role that answers for it, plus anybody standing as its
            // backup. The same implication the server enforces at signing time, so the page cannot promise a
            // signature the review would refuse — or withhold one it would accept.
            ResolvedAuthority Resolve(ProgramRole required)
            {
                var accepted = ProgramRoleAuthority.Satisfying(required);
                var holders = memberships.Where(x => accepted.Contains(x.Role))
                    .Select(x => x.UserId).Distinct()
                    .Where(names.ContainsKey).Select(x => names[x]).Order().ToList();
                var standing = backups.Where(x => accepted.Contains(x.Role))
                    .Select(x => x.BackupUserId).Distinct()
                    .Where(id => names.ContainsKey(id) && memberships.Any(m => m.UserId == id))
                    .Select(x => names[x]).Order().ToList();
                // IdentityService.HasRoleAsync treats a delegation as a live exact-role grant. Keep that
                // same rule here so the configuration center never promises a delegate the signing gate rejects.
                var delegated = delegations.Where(x => x.Role == required)
                    .Select(x => x.DelegateUserId).Distinct()
                    .Where(id => names.ContainsKey(id) && memberships.Any(m => m.UserId == id))
                    .Select(x => names[x]).Order().ToList();
                // Blocking is named separately from "no holder" because a standing backup is enough to
                // complete the stage even when the position itself is empty.
                return new ResolvedAuthority(required.ToString(), SingularProgramRoles.IsSingular(required),
                    holders, standing, delegated, holders.Count == 0 && standing.Count == 0 && delegated.Count == 0);
            }

            var configured = Subjects.Select(subject =>
            {
                var workflow = workflows
                    .Where(x => x.AppliesTo == subject)
                    .OrderByDescending(x => x.Version).FirstOrDefault();
                if (workflow is null)
                    return new
                    {
                        subject = subject.ToString(),
                        configured = false,
                        name = (string?)null,
                        version = (int?)null,
                        mode = (string?)null,
                        stages = (object?)null,
                        minimum = 0,
                        allowsAdditional = false,
                        blockingStages = 0,
                    };

                var stages = workflow.Stages.OrderBy(x => x.Position).Select(stage => new
                {
                    stage.Position,
                    stage.Name,
                    kind = stage.Kind.ToString(),
                    requiredRole = stage.RequiredRole.ToString(),
                    required = Resolve(stage.RequiredRole),
                }).ToList();

                return new
                {
                    subject = subject.ToString(),
                    configured = true,
                    name = (string?)workflow.Name,
                    version = (int?)workflow.Version,
                    mode = (string?)workflow.Mode.ToString(),
                    stages = (object?)stages,
                    minimum = stages.Count,
                    allowsAdditional = true,
                    blockingStages = stages.Count(x => x.required.Blocking),
                };
            }).ToList();

            return Results.Ok(new { projectId, canManage, artifacts = configured });
        });

        // The configuration center is deliberately project-scoped: it creates and activates the next
        // version in one unit of work, retiring the prior active version while retaining it for in-flight
        // review history. This is the same ReviewWorkflow aggregate used by the existing workflow routes;
        // there is no parallel policy store for the page to drift away from.
        app.MapPut("/api/projects/{projectId:guid}/approval-configuration/{subject}", SaveConfigurationAsync);
        // The subject-specific PUT is the one write contract. Keeping the subject in the route makes a
        // cross-subject mutation impossible to hide in a body and gives clients an idempotent versioned save.
    }

    private static async Task<IResult> SaveConfigurationAsync(Guid projectId, ReviewSubject subject,
        ConfigureApprovalRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
        CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
            return Results.Forbid();
        if (request.Stages is null || request.Stages.Count == 0)
            return Results.BadRequest(new { error = "At least one required sign-off stage must be configured." });
        if (request.Stages.Count > 50)
            return Results.BadRequest(new { error = "A configuration may contain at most 50 required sign-off stages." });

        var now = DateTimeOffset.UtcNow;
        var actor = http.UserAccount().UserName;
        var current = await db.ReviewWorkflows.Include(x => x.Stages)
            .Where(x => x.ProjectId == projectId && x.AppliesTo == subject && x.State == ReviewWorkflowState.Active)
            .SingleOrDefaultAsync(ct);
        var latest = await db.ReviewWorkflows.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AppliesTo == subject)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);

        var mode = request.Mode ?? current?.Mode ?? latest?.Mode ?? ReviewMode.Sequential;
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? current?.Name ?? latest?.Name ?? $"{subject} approval configuration"
            : request.Name.Trim();
        var stages = request.Stages.Select((stage, index) =>
            new ReviewWorkflowStageDraft(
                string.IsNullOrWhiteSpace(stage.Name)
                    ? $"{stage.Kind} {ReadableRole(stage.RequiredRole)} {index + 1}"
                    : stage.Name.Trim(),
                stage.RequiredRole,
                stage.Kind)).ToList();

        try
        {
            var next = new ReviewWorkflow(projectId, name, subject, mode, stages, actor,
                now, (latest?.Version ?? 0) + 1, latest?.LogicalId);
            if (current is not null) current.Retire(actor, now);
            next.Activate(actor, now);
            db.ReviewWorkflows.Add(next);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                projectId,
                subject = subject.ToString(),
                configured = true,
                name = next.Name,
                version = next.Version,
                mode = next.Mode.ToString(),
                stages = next.Stages.OrderBy(x => x.Position).Select(x => new
                {
                    x.Position,
                    x.Name,
                    kind = x.Kind.ToString(),
                    requiredRole = x.RequiredRole.ToString(),
                }),
            });
        }
        catch (DbUpdateException ex) when (IsWorkflowUniquenessConflict(ex))
        {
            return Results.Conflict(new { error = "Another approval configuration version was saved concurrently. Refresh and try again." });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

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

    private static string ReadableRole(ProgramRole role) => role switch
    {
        ProgramRole.ConfigurationManager => "Configuration management",
        ProgramRole.ProgramManager => "Program management",
        ProgramRole.SystemEngineer => "System engineering",
        ProgramRole.SystemEngineeringLead => "System engineering lead",
        ProgramRole.SoftwareEngineer => "Software engineering",
        ProgramRole.SoftwareEngineeringLead => "Software engineering lead",
        ProgramRole.SystemTestEngineer => "System test engineering",
        ProgramRole.SystemTestLead => "System test lead",
        ProgramRole.SoftwareTestEngineer => "Software test engineering",
        ProgramRole.SoftwareTestLead => "Software test lead",
        _ => role.ToString(),
    };
}

/// <summary>Who could sign a stage today, and whether anybody could.</summary>
public sealed record ResolvedAuthority(
    string Role,
    bool Singular,
    IReadOnlyList<string> Holders,
    IReadOnlyList<string> Backups,
    IReadOnlyList<string> Delegates,
    bool Blocking);

public sealed record ConfigureApprovalRequest(
    string? Name,
    ReviewMode? Mode,
    List<ReviewWorkflowStageRequest>? Stages);
