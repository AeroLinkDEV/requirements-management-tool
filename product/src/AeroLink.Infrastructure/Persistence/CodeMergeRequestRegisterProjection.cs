using AeroLink.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record CodeMergeRequestRegisterRow(string InstanceBaseUrl, long RemoteProjectId,
    int MergeRequestIid, int RelationshipCount);
public sealed record CodeMergeRequestRegisterPage(int Page, int PageSize, int Total,
    IReadOnlyList<CodeMergeRequestRegisterRow> Items);

/// <summary>
/// Pages recorded MR identities before any remote decoration. Explicit file-to-MR annotations participate;
/// repository discovery and metadata availability never create or remove local register membership.
/// </summary>
public static class CodeMergeRequestRegisterProjection
{
    public static async Task<CodeMergeRequestRegisterPage> ReadPageAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, int page, int pageSize, bool includeWithdrawn, CancellationToken ct)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            throw new DomainException("Choose a register page between 1 and 100000 and a page size between 1 and 100.");
        var direct = db.GitLabMergeRequestRelationships.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && (includeWithdrawn || x.IsActive))
            .Select(x => new { x.InstanceBaseUrl, x.RemoteProjectId, x.MergeRequestIid });
        var files = db.GitLabFileRelationships.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && x.MergeRequestIid != null
                && (includeWithdrawn || x.IsActive))
            .Select(x => new { x.InstanceBaseUrl, x.RemoteProjectId, MergeRequestIid = x.MergeRequestIid!.Value });
        var grouped = direct.Concat(files)
            .GroupBy(x => new { x.InstanceBaseUrl, x.RemoteProjectId, x.MergeRequestIid })
            .Select(g => new { g.Key.InstanceBaseUrl, g.Key.RemoteProjectId, g.Key.MergeRequestIid, Count = g.Count() });
        var total = await grouped.CountAsync(ct);
        var items = await grouped.OrderBy(x => x.InstanceBaseUrl).ThenBy(x => x.RemoteProjectId)
            .ThenBy(x => x.MergeRequestIid).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new CodeMergeRequestRegisterRow(x.InstanceBaseUrl, x.RemoteProjectId, x.MergeRequestIid, x.Count))
            .ToListAsync(ct);
        return new(page, pageSize, total, items);
    }
}
