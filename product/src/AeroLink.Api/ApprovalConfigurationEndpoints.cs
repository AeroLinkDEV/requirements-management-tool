using AeroLink.Domain.ChangeControl;
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
            var accountIds = memberships.Select(x => x.UserId).Concat(backups.Select(x => x.BackupUserId)).Distinct().ToList();
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
                // Blocking is named separately from "no holder" because a standing backup is enough to
                // complete the stage even when the position itself is empty.
                return new ResolvedAuthority(required.ToString(), SingularProgramRoles.IsSingular(required),
                    holders, standing, holders.Count == 0 && standing.Count == 0);
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
                        blockingStages = 0,
                    };

                var stages = workflow.Stages.OrderBy(x => x.Position).Select(stage => new
                {
                    stage.Position,
                    stage.Name,
                    kind = stage.Kind.ToString(),
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
                    blockingStages = stages.Count(x => x.required.Blocking),
                };
            }).ToList();

            return Results.Ok(new { projectId, canManage, artifacts = configured });
        });
    }
}

/// <summary>Who could sign a stage today, and whether anybody could.</summary>
public sealed record ResolvedAuthority(
    string Role,
    bool Singular,
    IReadOnlyList<string> Holders,
    IReadOnlyList<string> Backups,
    bool Blocking);
