using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ProjectFeatureRead(bool Persisted, long Version, ProjectFeature Enabled,
    IReadOnlyDictionary<ProjectFeature, bool> HasRecords, IReadOnlyList<ProjectFeatureSetHistory> History);

public enum ProjectFeatureChangeKind { Changed, NotFound, Invalid, Conflict }

public sealed record ProjectFeatureChangeResult(ProjectFeatureChangeKind Kind, string? Error = null, ProjectFeatureRead? Read = null);

/// <summary>
/// Reads and changes a project's features (#1113). A feature may be switched off only while it holds no
/// records, so switching off never hides or strands controlled history; switching on is always allowed.
/// </summary>
public sealed class ProjectFeatureService(AeroLinkDbContext db)
{
    public static async Task<ProjectFeature> EffectiveAsync(AeroLinkDbContext db, Guid projectId, CancellationToken ct) =>
        await db.ProjectFeatureSets.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => (ProjectFeature?)x.Enabled).SingleOrDefaultAsync(ct) ?? ProjectFeatures.All;

    public async Task<ProjectFeatureRead?> ReadAsync(Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return null;
        var set = await db.ProjectFeatureSets.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        var records = new Dictionary<ProjectFeature, bool>();
        foreach (var feature in ProjectFeatures.Each) records[feature] = await HasRecordsAsync(projectId, feature, ct);
        var history = (await db.ProjectFeatureSetHistories.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct))
            .OrderByDescending(x => x.Version).ToList();
        return new ProjectFeatureRead(set is not null, set?.Version ?? 0, set?.Enabled ?? ProjectFeatures.All, records, history);
    }

    public async Task<ProjectFeatureChangeResult> ChangeAsync(Guid projectId, ProjectFeature enabled, long expectedVersion,
        string reason, string actor, string ipAddress, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct))
            return new(ProjectFeatureChangeKind.NotFound, "That project does not exist.");
        if (ProjectFeatures.Refusal(enabled) is { } refusal) return new(ProjectFeatureChangeKind.Invalid, refusal);
        if (string.IsNullOrWhiteSpace(reason)) return new(ProjectFeatureChangeKind.Invalid, "Say why the project's features are changing.");
        var set = await db.ProjectFeatureSets.SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if ((set?.Version ?? 0) != expectedVersion)
            return new(ProjectFeatureChangeKind.Conflict, "The project's features changed after you opened them. Refresh before changing them.");
        var previous = set?.Enabled ?? ProjectFeatures.All;
        if (previous == enabled) return new(ProjectFeatureChangeKind.Invalid, "Nothing changed.");
        foreach (var feature in ProjectFeatures.Each.Where(x => previous.HasFlag(x) && !enabled.HasFlag(x)))
            if (await HasRecordsAsync(projectId, feature, ct))
                return new(ProjectFeatureChangeKind.Conflict,
                    $"{ProjectFeatures.Label(feature)} already holds records in this project, so it cannot be switched off.");
        try
        {
            if (set is null) { set = new ProjectFeatureSet(projectId, enabled, actor, now); db.ProjectFeatureSets.Add(set); }
            else set.Change(enabled, actor, now);
            db.ProjectFeatureSetHistories.Add(new ProjectFeatureSetHistory(set, previous, reason, now));
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("ProjectFeaturesChanged", actor, $"Project:{projectId}", "Success",
                $"{Describe(previous)} -> {Describe(enabled)}: {reason.Trim()}", ipAddress, now));
            await db.SaveChangesAsync(ct);
        }
        catch (DomainException ex) { return new(ProjectFeatureChangeKind.Invalid, ex.Message); }
        catch (DbUpdateException)
        {
            return new(ProjectFeatureChangeKind.Conflict, "The project's features changed concurrently. Refresh before changing them.");
        }
        return new(ProjectFeatureChangeKind.Changed, Read: await ReadAsync(projectId, ct));
    }

    private static string Describe(ProjectFeature enabled) =>
        string.Join(", ", ProjectFeatures.Each.Where(x => enabled.HasFlag(x)).Select(ProjectFeatures.Label)) is { Length: > 0 } text ? text : "none";

    private async Task<bool> HasRecordsAsync(Guid projectId, ProjectFeature feature, CancellationToken ct)
    {
        foreach (var source in RecordSources(feature))
            if (await source.AnyAsync(id => id == projectId, ct)) return true;
        return false;
    }

    /// <summary>The project ids of the records that make each feature non-empty.</summary>
    private IEnumerable<IQueryable<Guid>> RecordSources(ProjectFeature feature)
    {
        switch (feature)
        {
            case ProjectFeature.Requirements:
                yield return db.SystemChangeRequests.Select(x => x.ProjectId);
                yield return db.Requirements.Select(x => x.ProjectId);
                break;
            case ProjectFeature.Verification:
                yield return db.TestChangeReviews.Select(x => x.ProjectId);
                yield return db.TestProcedures.Select(x => x.ProjectId);
                yield return db.TestExecutions.Select(x => x.ProjectId);
                break;
            case ProjectFeature.Code:
                yield return db.CodeTraceabilityRecords.Select(x => x.ProjectId);
                yield return db.GitLabSourceSelectionEvents.Select(x => x.ProjectId);
                yield return db.GitLabMergeRequestRelationships.Select(x => x.ProjectId);
                yield return db.GitLabFileRelationships.Select(x => x.ProjectId);
                break;
            case ProjectFeature.DocumentationCenter:
                yield return db.ManagedDocuments.Select(x => x.ProjectId);
                break;
            case ProjectFeature.ProblemReports:
                yield return db.ProblemReports.Select(x => x.ProjectId);
                break;
            case ProjectFeature.Release:
                yield return db.ReleaseCampaigns.Select(x => x.ProjectId);
                yield return db.CandidateBaselines.Select(x => x.ProjectId);
                break;
        }
    }

    /// <summary>
    /// Save-boundary gate: a feature that is off cannot acquire records by any path — API, import, seeder.
    /// Together with "off only while empty" this keeps a disabled feature empty without guarding each
    /// endpoint. Projects without a feature-set row have every feature and cost one indexed read at most.
    /// </summary>
    internal static async Task RefuseRecordsForDisabledFeaturesAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var added = new List<(Guid ProjectId, ProjectFeature Feature)>();
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
        {
            var feature = entry.Entity switch
            {
                SystemChangeRequest or RequirementArtifact => ProjectFeature.Requirements,
                TestChangeReview or TestProcedure or TestExecution => ProjectFeature.Verification,
                CodeTraceabilityRecord or GitLabSourceSelectionEvent or GitLabMergeRequestRelationship or GitLabFileRelationship => ProjectFeature.Code,
                ManagedDocument => ProjectFeature.DocumentationCenter,
                ProblemReport => ProjectFeature.ProblemReports,
                ReleaseCampaign or CandidateBaseline => ProjectFeature.Release,
                _ => ProjectFeature.None,
            };
            if (feature == ProjectFeature.None) continue;
            added.Add(((Guid)entry.Property("ProjectId").CurrentValue!, feature));
        }
        if (added.Count == 0) return;
        var projectIds = added.Select(x => x.ProjectId).Distinct().ToList();
        var sets = await db.ProjectFeatureSets.AsNoTracking().Where(x => projectIds.Contains(x.ProjectId))
            .ToDictionaryAsync(x => x.ProjectId, x => x.Enabled, ct);
        foreach (var (projectId, feature) in added)
            if (sets.TryGetValue(projectId, out var enabled) && !enabled.HasFlag(feature))
                throw new DomainException($"{ProjectFeatures.Label(feature)} is not enabled for this project.");
    }
}
