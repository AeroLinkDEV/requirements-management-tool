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
        // A schema migrated only up to a point before feature sets existed (upgrade qualification writes legacy
        // rows that way) has no feature sets, so every project in it has every feature. Probed without raising an
        // error, because a failed statement would abort the caller's PostgreSQL transaction.
        if (!await FeatureTableExistsAsync(db, ct)) return;
        var sets = await db.ProjectFeatureSets.AsNoTracking().Where(x => projectIds.Contains(x.ProjectId))
            .ToDictionaryAsync(x => x.ProjectId, x => x.Enabled, ct);
        foreach (var (projectId, feature) in added)
            if (sets.TryGetValue(projectId, out var enabled) && !enabled.HasFlag(feature))
                throw new DomainException($"{ProjectFeatures.Label(feature)} is not enabled for this project.");
    }

    /// <summary>
    /// Save-boundary gate for DEC-144: Standalone verification exists only where there is no requirement to
    /// trace to. A new Standalone proposal or revision is refused in a project that uses Requirements, unless
    /// it continues an artifact whose latest revision is already Standalone. Switching Requirements on later
    /// never invalidates existing standalone work; such an artifact is traced by an ordinary Modify when its
    /// owners choose to, and nothing rewrites it.
    /// </summary>
    internal static async Task RefuseStandaloneVerificationWithRequirementsAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var changes = db.ChangeTracker.Entries<TestProcedureChange>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified
                && x.Entity.ParentKind == VerificationProcedureParentKind.Standalone
                && x.Entity.Kind != TestProcedureChangeKind.Retire)
            .Select(x => x.Entity).ToList();
        var revisions = db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State == EntityState.Added
                && x.Entity.ParentKind == VerificationProcedureParentKind.Standalone
                && x.Entity.State != TestProcedureState.Retired)
            .Select(x => x.Entity).ToList();
        if (changes.Count == 0 && revisions.Count == 0) return;

        var trackedReviews = db.ChangeTracker.Entries<TestChangeReview>().ToDictionary(x => x.Entity.Id, x => x.Entity.ProjectId);
        var reviewIds = changes.Select(x => x.TestChangeReviewId).Where(x => !trackedReviews.ContainsKey(x)).Distinct().ToList();
        foreach (var review in await db.TestChangeReviews.AsNoTracking().Where(x => reviewIds.Contains(x.Id))
                     .Select(x => new { x.Id, x.ProjectId }).ToListAsync(ct))
            trackedReviews[review.Id] = review.ProjectId;
        var procedures = db.ChangeTracker.Entries<TestProcedure>().ToDictionary(x => x.Entity.Id, x => x.Entity);
        var procedureIds = revisions.Select(x => x.ProcedureId).Where(x => !procedures.ContainsKey(x)).Distinct().ToList();
        foreach (var procedure in await db.TestProcedures.AsNoTracking().Where(x => procedureIds.Contains(x.Id)).ToListAsync(ct))
            procedures[procedure.Id] = procedure;

        var subjects = changes
            .Select(x => new StandaloneSubject(trackedReviews.GetValueOrDefault(x.TestChangeReviewId), x.BaseNumber,
                int.MaxValue, x.DisplayNumber))
            .Concat(revisions.Select(x => procedures.TryGetValue(x.ProcedureId, out var procedure)
                ? new StandaloneSubject(procedure.ProjectId, procedure.BaseNumber, x.Revision,
                    ArtifactNumber.Display(procedure.BaseNumber, x.Revision))
                : new StandaloneSubject(Guid.Empty, "", x.Revision, "")))
            .ToList();
        if (subjects.Any(x => x.ProjectId == Guid.Empty))
            throw new DomainException("A Standalone verification artifact must belong to a known project.");
        var projectIds = subjects.Select(x => x.ProjectId).Distinct().ToList();
        var sets = await FeatureTableExistsAsync(db, ct)
            ? await db.ProjectFeatureSets.AsNoTracking().Where(x => projectIds.Contains(x.ProjectId))
                .ToDictionaryAsync(x => x.ProjectId, x => x.Enabled, ct)
            : new Dictionary<Guid, ProjectFeature>();
        foreach (var tracked in db.ChangeTracker.Entries<ProjectFeatureSet>()
                     .Where(x => x.State != EntityState.Deleted && projectIds.Contains(x.Entity.ProjectId)))
            sets[tracked.Entity.ProjectId] = tracked.Entity.Enabled;

        foreach (var subject in subjects)
        {
            // A project without a stored set has every feature, Requirements included.
            if (sets.TryGetValue(subject.ProjectId, out var enabled) && !enabled.HasFlag(ProjectFeature.Requirements))
                continue;
            var latest = string.IsNullOrWhiteSpace(subject.BaseNumber)
                ? null
                : await (from revision in db.TestProcedureRevisions.AsNoTracking()
                         join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                         where procedure.ProjectId == subject.ProjectId && procedure.BaseNumber == subject.BaseNumber
                             && revision.Revision < subject.Before
                         orderby revision.Revision descending
                         select (VerificationProcedureParentKind?)revision.ParentKind).FirstOrDefaultAsync(ct);
            if (latest != VerificationProcedureParentKind.Standalone)
                throw new DomainException(
                    $"{(subject.Label.Length == 0 ? "A verification artifact" : subject.Label)} cannot be Standalone: this project uses Requirements, so it is Allocated to requirement revisions or explicitly Derived.");
        }
    }

    /// <summary>A Standalone proposal or revision: its project, artifact, the revision it follows, and its display name.</summary>
    private sealed record StandaloneSubject(Guid ProjectId, string BaseNumber, int Before, string Label);

    /// <summary>Databases known to have the feature-set table. A table, once present, stays, so only "yes" is cached.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FeatureTablePresent = new();

    private static async Task<bool> FeatureTableExistsAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var key = db.Database.GetConnectionString() ?? "";
        if (FeatureTablePresent.ContainsKey(key)) return true;
        var present = db.Database.IsNpgsql()
            ? await db.Database.SqlQueryRaw<bool>("SELECT (to_regclass('project_feature_sets') IS NOT NULL) AS \"Value\"").SingleAsync(ct)
            : db.Database.IsSqlite()
                ? await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'project_feature_sets'").SingleAsync(ct) > 0
                : true;
        if (present) FeatureTablePresent[key] = true;
        return present;
    }
}
