using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ShowcaseInventoryExample(Guid Id, string Identifier, string State, string? Owner, bool? IsDemonstration = null);
public sealed record ShowcaseInventoryFamily(string Family, string Surface, string Scope, int Count,
    IReadOnlyDictionary<string, int> States, IReadOnlyDictionary<string, int> Owners,
    IReadOnlyList<ShowcaseInventoryExample> Examples);
public sealed record ShowcaseBuildInventory(Guid BuildId, string Version, bool Released,
    bool RequirementsMaterialized, IReadOnlyList<ShowcaseInventoryFamily> Families);

public sealed partial class FmsShowcaseSeeder
{
    /// <summary>
    /// Operator inventory of exact build membership and authored work. Project registers are reported
    /// separately: neither a global artifact header nor an inherited manifest is a new-build revision.
    /// Native state names and attributable authors/owners are retained; current holders come from Team Work.
    /// </summary>
    public async Task<object> InventoryAsync(Guid programId, CancellationToken ct = default)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var policy = await resolver.ResolveAsync(projectId, ct);
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var requirements = await (from member in db.BaselineRequirements.AsNoTracking()
            join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
            join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
            where artifact.ProjectId == projectId
            select new { member.BaselineId, revision.Id, artifact.BaseNumber, revision.Revision, artifact.Level })
            .ToListAsync(ct);
        var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var reviews = await db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var artifacts = await db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => artifactIds.Contains(x.ProcedureId)).ToListAsync(ct);
        var assessments = await db.DownstreamChangeAssessments.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var impacts = await db.VerificationImpactItems.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var executions = await db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var problems = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var documents = await db.ControlledDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var code = await db.CodeTraceabilityRecords.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var families = policy.Definitions.Where(x => x.VerificationProfile is not null)
            .SelectMany(x => x.VerificationProfile!.Definitions.Select(d => new { x.Level, d.Key })).ToList();
        var artifactById = artifacts.ToDictionary(x => x.Id);
        var revisionById = revisions.ToDictionary(x => x.Id);
        var managedDocuments = await db.ManagedDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var managedIds = managedDocuments.Select(x => x.Id).ToList();
        var managedRevisions = await db.ManagedDocumentRevisions.AsNoTracking().Where(x => managedIds.Contains(x.DocumentId)).ToListAsync(ct);
        var managedById = managedDocuments.ToDictionary(x => x.Id);
        var documentProvenance = await db.ManagedDocumentBuildProvenance.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var registers = await db.TestProcedureDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var projectFamilies = new List<ShowcaseInventoryFamily>
        {
            Summarize("Managed document revisions", "Managed documents", "Project library; build counts require recorded build provenance",
                managedRevisions.Select(x => new ShowcaseInventoryExample(x.Id,
                    $"{managedById[x.DocumentId].DocumentNumber}.{x.Revision:D2}", x.State.ToString(), x.ResponsibleOwnerId))),
        };
        foreach (var family in families)
            projectFamilies.Add(Summarize($"Document register/{family.Level}/{family.Key.Kind}", "Verification document register",
                "One project register per configured artifact key",
                registers.Where(x => x.Level == policy.ProcedureLevel(family.Level) && x.ArtifactKind == family.Key.Kind)
                    .Select(x => new ShowcaseInventoryExample(x.Id, x.Id.ToString(), "Register", null))));
        var result = new List<ShowcaseBuildInventory>();
        var traces = new List<object>();
        foreach (var release in releases.OrderBy(x => x.Version))
        {
            var buildBaselines = baselines.Where(x => x.ReleaseId == release.Id).ToList();
            var materializedIds = buildBaselines.Where(x => x.RequirementsMaterializedAt is not null).Select(x => x.Id).ToHashSet();
            var rows = new List<ShowcaseInventoryFamily>();
            foreach (var level in policy.OrderedLevels)
            {
                rows.Add(Summarize($"Requirements/{level}", "Requirements workspace", "Exact materialized baseline membership",
                    requirements.Where(x => materializedIds.Contains(x.BaselineId) && x.Level == level)
                        .DistinctBy(x => x.Id).Select(x => new ShowcaseInventoryExample(x.Id, $"{x.BaseNumber}.{x.Revision:D2}", "Materialized", null))));
                rows.Add(Summarize($"Change requests/{level}", "Change requests; Digital Thread", "Target build",
                    requests.Where(x => x.TargetReleaseId == release.Id && (level == RequirementLevel.System
                            ? x.Type == ChangeRequestType.System
                            : x.Type == ChangeRequestType.Software && x.SoftwareLevel == level))
                        .Select(x => new ShowcaseInventoryExample(x.Id, x.DisplayNumber, x.State.ToString(), x.AuthorId))));
                if (policy.ParentLevels(level).Count > 0)
                    rows.Add(Summarize($"Assessments/{level}", "Downstream assessments", "Assessment release and actual target level",
                        assessments.Where(x => x.ReleaseId == release.Id && x.TargetLevel == level)
                            .Select(x => new ShowcaseInventoryExample(x.Id, x.SourceChangeRequestNumber,
                                $"{x.State}/{x.Outcome}", x.AssignedEngineerId))));
            }
            // Use only this build's own materialized manifests. Inherited availability is a different fact.
            var ownRevisionIds = new HashSet<Guid>();
            var ownCoverageRevisionIds = new HashSet<Guid>();
            foreach (var baselineId in materializedIds)
            {
                var manifest = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
                if (manifest is null) continue;
                ownRevisionIds.UnionWith(manifest.RevisionIds);
                ownCoverageRevisionIds.UnionWith(await CoveragePopulationAsync(manifest, policy, ct));
            }
            var buildRequirementIds = requirements.Where(x => materializedIds.Contains(x.BaselineId))
                .Select(x => x.Id).Distinct().ToList();
            var settled = await VerificationCoverageProjection.SettledCoveredAsync(db, buildRequirementIds, ct,
                ownCoverageRevisionIds, buildScoped: false);
            var linked = await VerificationCoverageProjection.LinkedRequirementRevisionIds(db, ownCoverageRevisionIds)
                .Where(id => buildRequirementIds.Contains(id)).Distinct().ToListAsync(ct);
            var buildRequests = requests.Where(x => x.TargetReleaseId == release.Id).ToList();
            static RequirementLevel? RequestLevel(AeroLink.Domain.ChangeControl.SystemChangeRequest request) => request.Type switch
            {
                ChangeRequestType.System => RequirementLevel.System,
                ChangeRequestType.Software => request.SoftwareLevel,
                ChangeRequestType.Interface => RequirementLevel.Interface,
                _ => null,
            };
            var onLadderRequests = buildRequests.Where(x => RequestLevel(x) is { } level && policy.OrderedLevels.Contains(level)).ToList();
            var onLadderIds = onLadderRequests.Select(x => x.Id).ToHashSet();
            var traceStates = await ChangeRequestTraceProjection.StatesAsync(db, projectId,
                onLadderIds, policy, ct);
            traces.Add(new
            {
                BuildId = release.Id, release.Version,
                RequirementCoverage = new
                {
                    Scope = "This build's materialized requirements and own procedure manifest; current rework remains suspect",
                    WaitingForPrerequisite = materializedIds.Count == 0,
                    Total = buildRequirementIds.Count, Settled = settled.Count,
                    Suspect = linked.Count(id => !settled.Contains(id)),
                    Uncovered = buildRequirementIds.Count(id => !settled.Contains(id) && !linked.Contains(id)),
                },
                ChangeControl = new
                {
                    Scope = "All change requests targeting this exact build; native Digital Thread states for configured levels; off-ladder history explicitly retained below",
                    Total = buildRequests.Count,
                    OnLadder = onLadderRequests.Count,
                    OffLadder = buildRequests.Where(x => !onLadderIds.Contains(x.Id)).Select(x => new
                    { x.Id, x.DisplayNumber, NativeState = x.State.ToString(), Reason = "Level is not configured by this project's current ladder" }).ToList(),
                    Upstream = traceStates.Values.GroupBy(x => x.Upstream).ToDictionary(x => x.Key, x => x.Count()),
                    Downstream = traceStates.Values.GroupBy(x => x.Downstream).ToDictionary(x => x.Key, x => x.Count()),
                    Records = buildRequests.OrderBy(x => x.DisplayNumber).Select(x => new
                    { x.Id, x.DisplayNumber, State = traceStates.GetValueOrDefault(x.Id) }).ToList(),
                },
            });
            foreach (var family in families)
            {
                var key = $"{family.Level}/{family.Key.Kind}";
                var familyArtifactIds = artifacts.Where(x => x.Level == policy.ProcedureLevel(family.Level) && x.ArtifactKind == family.Key.Kind)
                    .Select(x => x.Id).ToHashSet();
                var familyReviewIds = reviews.Where(x => (int)x.Discipline == (int)family.Key.Discipline && x.ArtifactKind == family.Key.Kind)
                    .Select(x => x.Id).ToHashSet();
                rows.Add(Summarize($"Verification/{key}", "Verification artifact workspace", "Own baseline executable membership and its exact coverage Case population",
                    revisions.Where(x => (ownRevisionIds.Contains(x.Id) || ownCoverageRevisionIds.Contains(x.Id)) && familyArtifactIds.Contains(x.ProcedureId))
                        .Select(x => new ShowcaseInventoryExample(x.Id, $"{artifactById[x.ProcedureId].BaseNumber}.{x.Revision:D2}", x.State.ToString(), x.AuthorId))));
                rows.Add(Summarize($"Test change reviews/{key}", "Test change reviews", "Review release",
                    reviews.Where(x => x.ReleaseId == release.Id && familyReviewIds.Contains(x.Id))
                        .Select(x => new ShowcaseInventoryExample(x.Id, x.DisplayNumber, x.State.ToString(), x.AuthorId))));
                rows.Add(Summarize($"Verification impact/{key}", "Verification impact queue", "Impact release; originating review family",
                    impacts.Where(x => x.ReleaseId == release.Id && familyReviewIds.Contains(x.TestChangeReviewId))
                        .Select(x => new ShowcaseInventoryExample(x.Id, x.SubjectDisplayNumber, x.State.ToString(), x.AssignedEngineerId))));
                if (policy.ExecutableArtifactKey(family.Level) == family.Key)
                    rows.Add(Summarize($"Executions/{key}", "Test Results", "Execution's recorded release",
                        executions.Where(x => x.ReleaseId == release.Id && revisionById.TryGetValue(x.ProcedureRevisionId, out var revision)
                                && familyArtifactIds.Contains(revision.ProcedureId))
                            .Select(x => new ShowcaseInventoryExample(x.Id,
                                $"{artifactById[revisionById[x.ProcedureRevisionId].ProcedureId].BaseNumber}.{revisionById[x.ProcedureRevisionId].Revision:D2}",
                                x.Outcome + (x.RetestOfExecutionId is null ? "" : "/Retest"), x.ExecutedBy))));
            }
            rows.Add(Summarize("Problem Reports", "Problem Reports", "Target build",
                problems.Where(x => x.TargetReleaseId == release.Id).Select(x => new ShowcaseInventoryExample(x.Id, x.DisplayNumber, x.State.ToString(), x.ResponsibleEngineerId))));
            rows.Add(Summarize("Controlled documents", "Controlled publications", "Publication release",
                documents.Where(x => x.ReleaseId == release.Id).Select(x => new ShowcaseInventoryExample(x.Id, $"{x.DocumentNumber}.{x.Revision:D2}", x.Type.ToString(), null))));
            rows.Add(Summarize("Code traceability", "Code traceability", "Recorded release; exact requirement revision",
                code.Where(x => x.ReleaseId == release.Id).Select(x => new ShowcaseInventoryExample(x.Id, x.RequirementRevisionId.ToString(), x.Disposition.ToString(), x.RecordedBy, x.IsDemonstration))));
            var buildDocumentIds = documentProvenance.Where(x => x.ReleaseId == release.Id).Select(x => x.RevisionId).ToHashSet();
            rows.Add(Summarize("Managed document revisions", "Managed documents", "Explicit recorded build provenance only; project-library availability is separate",
                managedRevisions.Where(x => buildDocumentIds.Contains(x.Id)).Select(x => new ShowcaseInventoryExample(x.Id,
                    $"{managedById[x.DocumentId].DocumentNumber}.{x.Revision:D2}", x.State.ToString(), x.ResponsibleOwnerId))));
            rows.Add(Summarize("Baselines", "Build baseline", "Owning release",
                buildBaselines.Select(x => new ShowcaseInventoryExample(x.Id, $"{x.BaseNumber}.{x.Revision:D2}", x.State.ToString(), null))));
            result.Add(new(release.Id, release.Version, release.IsReleased, materializedIds.Count > 0, rows));
        }
        return new { ProjectId = projectId, Builds = result, ProjectFamilies = projectFamilies, Traces = traces,
            Scope = "Build counts retain exact membership or authored release provenance. Zero materialized requirements on an in-work build means waiting for prerequisite; no predecessor population is substituted.",
            OwnerMeaning = "Attributable author/assigned engineer, not a current-holder claim. Current holder counts and bases are reported by the Team Work projection.",
            Exceptions = "Minimums apply across the project inventory. Baselines, builds and per-family document registers are singular. Software Procedure impacts belong to the originating Case review. Interface is retired from the FMS ladder." };
    }

    private async Task<IReadOnlyCollection<Guid>> CoveragePopulationAsync(TestProcedureEffectivityResult manifest,
        ILadderPolicy policy, CancellationToken ct)
    {
        if (!manifest.IsExactManifest) return manifest.RevisionIds;
        // Same typed membership as release readiness: software Procedure selections execute, while
        // requirement coverage remains on their exact Case revisions. Never search globally for coverage.
        var procedureLevels = policy.Definitions.Where(x =>
                x.VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true
                && x.VerificationProfile?.Enables(VerificationArtifactKind.Case) == true)
            .Select(x => policy.ProcedureLevel(x.Level)).ToHashSet();
        return (await BaselineExecutableMembership.ForPopulationAsync(db, manifest.BaselineId, procedureLevels, ct)).CoverageRevisionIds;
    }

    private static ShowcaseInventoryFamily Summarize(string family, string surface, string scope, IEnumerable<ShowcaseInventoryExample> source)
    {
        var rows = source.OrderBy(x => x.Identifier, StringComparer.Ordinal).ThenBy(x => x.Id).ToList();
        return new(family, surface, scope, rows.Count,
            rows.GroupBy(x => x.State).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count()),
            rows.Where(x => !string.IsNullOrWhiteSpace(x.Owner)).GroupBy(x => x.Owner!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count()), rows.Take(8).ToList());
    }
}
