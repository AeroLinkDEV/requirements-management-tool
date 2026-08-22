using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record CrossLevelRequirementChange(
    Guid ChangeRequestId,
    string ChangeRequestNumber,
    string ChangeRequestType,
    string RequirementNumber,
    string RequirementLevel);

/// <summary>
/// Reports change requests holding a requirement level their type forbids.
///
/// The rule is enforced in the domain, but enforcement only guards records being written. A row that predates
/// the rule, or one that arrives through a future import or integration, sits there silently — which is how
/// `SCR-00032.00` kept `HLR-000075.02` through a fix, a closed issue and a handoff claiming it was corrected.
/// It was eventually noticed in a screenshot.
///
/// This is the check that makes the next one visible instead. It answers the same question the repair
/// migration answers, so a survivor cannot hide behind the migration having run.
/// </summary>
public static class ChangeRequestScopeAudit
{
    public static async Task<IReadOnlyList<CrossLevelRequirementChange>> ViolationsAsync(
        AeroLinkDbContext db, ILadderPolicy? policy = null, CancellationToken ct = default)
    {
        var ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
        var rows = await (from change in db.RequirementChanges.AsNoTracking()
                          join request in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals request.Id
                          orderby request.BaseNumber, change.BaseNumber
                          select new { Change = change, Request = request })
            .ToListAsync(ct);
        return rows
            .Where(x => !ladderPolicy.AcceptsChangeRequest(x.Request.Type, x.Request.SoftwareLevel, x.Change.Level))
            .Select(x => new CrossLevelRequirementChange(
                x.Request.Id,
                x.Request.BaseNumber,
                x.Request.Type.ToString(),
                x.Change.BaseNumber,
                x.Change.Level.ToString()))
            .ToArray();
    }
}
