using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Buffers;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Durable source staging and server-side reconciliation for new-project inception. The service owns only the
/// source package while a setup is unfinished; it never creates a Project as a side effect of an upload.
/// </summary>
public sealed class ProjectSetupInceptionService(
    AeroLinkDbContext db,
    IProjectLadderPolicyResolver policyResolver,
    IdentityService identity)
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }, PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> NativeCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Requirements", "Traces", "Cases", "Procedures", "Evidence" };
    private static readonly HashSet<string> ReqIfCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Requirements", "Traces" };
    private static readonly HashSet<string> TabularCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Requirements" };

    public async Task<ProjectSetupSourceMutationResult> CaptureNativeAsync(Guid draftId, AuthenticatedUser actor,
        long expectedVersion, Guid baselineId, CancellationToken ct)
    {
        var draft = (await LoadDraftAsync(draftId, actor, ct))!;
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == baselineId, ct)
            ?? throw new ProjectSetupInvalidException("The selected AeroLink baseline was not found.");
        if (baseline.State is not (CandidateBaselineState.Frozen or CandidateBaselineState.Released)
            || baseline.RequirementsMaterializedAt is null)
            throw new ProjectSetupInvalidException("Only a Frozen or Released baseline with a materialized requirement manifest can start a new project.");
        var sourceProject = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.ProjectId, ct)
            ?? throw new ProjectSetupInvalidException("The selected baseline has no source project.");
        await RequireCurrentSourceProgramAccessAsync(sourceProject.ProgramId, actor, ct);
        // A client can lose the response after the source package and draft answer commit. Replaying the same
        // immutable native selection is safe and returns the existing package, even though its draft token has
        // advanced. A different selection still goes through the optimistic version check below.
        var existingPackage = await db.ProjectSetupSourcePackages.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DraftId == draft.Id && x.SourceBaselineId == baselineId, ct);
        if (existingPackage is not null && draft.SourceBaselineId == baselineId)
            return new(existingPackage, draft.Version);
        EnsureVersion(draft, expectedVersion);

        var rows = await (from membership in db.BaselineRequirements.AsNoTracking()
                           join revision in db.RequirementRevisions.AsNoTracking() on membership.RevisionId equals revision.Id
                           join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                           where membership.BaselineId == baseline.Id
                           select new { membership, revision, artifact }).ToListAsync(ct);
        var sourceKeys = rows.ToDictionary(x => x.revision.Id, x => x.revision.Id.ToString("N"));
        var requirementObjects = rows.OrderBy(x => x.artifact.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.revision.Revision)
            .Select(x => new ProjectCreationSourceObject(
                sourceKeys[x.revision.Id], x.artifact.Level.ToString(), x.artifact.BaseNumber, "Requirement",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Statement"] = x.revision.Statement,
                    ["Rationale"] = x.revision.Rationale,
                    ["VerificationMethod"] = x.revision.VerificationMethod,
                    ["Level"] = x.artifact.Level.ToString(),
                    ["Revision"] = x.revision.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["RevisionId"] = x.revision.Id.ToString("D"),
                    ["OriginKind"] = x.revision.OriginKind.ToString(),
                    ["State"] = x.revision.State.ToString(),
                })).ToArray();
        var procedureRows = await (from membership in db.BaselineTestProcedures.AsNoTracking()
                                   join revision in db.TestProcedureRevisions.AsNoTracking() on membership.RevisionId equals revision.Id
                                   join procedure in db.TestProcedures.AsNoTracking() on membership.ProcedureId equals procedure.Id
                                   where membership.BaselineId == baseline.Id
                                   select new { membership, revision, procedure }).ToListAsync(ct);
        var verificationObjects = procedureRows.OrderBy(x => x.procedure.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.revision.Revision)
            .Select(x => new ProjectCreationSourceObject(
                x.revision.Id.ToString("N"), x.procedure.Level.ToString(), x.procedure.BaseNumber,
                x.procedure.ArtifactKind == VerificationArtifactKind.Case ? "Case" : "Procedure",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Title"] = x.procedure.Title,
                    ["Objective"] = x.revision.Objective,
                    ["Preconditions"] = x.revision.Preconditions,
                    ["Steps"] = x.revision.Steps,
                    ["ExpectedResult"] = x.revision.ExpectedResult,
                    ["Level"] = x.procedure.Level.ToString(),
                    ["Revision"] = x.revision.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["RevisionId"] = x.revision.Id.ToString("D"),
                    ["State"] = x.revision.State.ToString(),
                    ["ArtifactKind"] = x.procedure.ArtifactKind.ToString(),
                    ["SourceOwnerId"] = x.procedure.OwnerId,
                    ["SourceAuthorId"] = x.revision.AuthorId,
                })).ToArray();
        var objects = requirementObjects.Concat(verificationObjects).ToArray();
        var revisionIds = rows.Select(x => x.revision.Id).ToHashSet();
        var traceRows = await db.RequirementTraces.AsNoTracking()
            .Where(x => x.ProjectId == sourceProject.Id
                && revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var traces = traceRows.Select(x => new ProjectCreationSourceRelation(x.Id.ToString("N"), sourceKeys[x.SourceRevisionId],
            sourceKeys[x.TargetRevisionId], x.Type.ToString(), new Dictionary<string, string>())).ToList();
        var procedureKeys = procedureRows.ToDictionary(x => x.revision.Id, x => x.revision.Id.ToString("N"));
        var caseProcedureRows = await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                                       join @case in db.TestProcedureRevisions.AsNoTracking() on link.CaseRevisionId equals @case.Id
                                       join procedure in db.TestProcedureRevisions.AsNoTracking() on link.ProcedureRevisionId equals procedure.Id
                                       where procedureRows.Select(x => x.revision.Id).Contains(@case.Id)
                                           && procedureRows.Select(x => x.revision.Id).Contains(procedure.Id)
                                       select new { link, @case, procedure }).ToListAsync(ct);
        foreach (var row in caseProcedureRows)
            traces.Add(new ProjectCreationSourceRelation(row.link.Id.ToString("N"), procedureKeys[row.@case.Id],
                procedureKeys[row.procedure.Id], "CaseProcedure", new Dictionary<string, string>()));
        var coverageRows = await (from coverage in db.TestCoverage.AsNoTracking()
                                  where procedureRows.Select(x => x.revision.Id).Contains(coverage.ProcedureRevisionId)
                                  select coverage).ToListAsync(ct);
        foreach (var row in coverageRows)
            traces.Add(new ProjectCreationSourceRelation(row.Id.ToString("N"), procedureKeys[row.ProcedureRevisionId],
                sourceKeys.GetValueOrDefault(row.RequirementRevisionId, row.RequirementRevisionId.ToString("N")),
                "VerificationCoverage", new Dictionary<string, string>()));
        // Evidence is source fact data attached to the selected source build. There is deliberately no target
        // execution/evidence write in inception: imported source facts stay attributable and do not become new
        // project approvals or test results.
        var sourceBuildIds = await db.SoftwareBuilds.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id)
            .Select(x => x.Id).ToListAsync(ct);
        var executionRows = sourceBuildIds.Count == 0
            ? []
            : await db.TestExecutions.AsNoTracking()
                .Where(x => x.SoftwareBuildId != null && sourceBuildIds.Contains(x.SoftwareBuildId.Value))
                .ToListAsync(ct);
        var executionIds = executionRows.Select(x => x.Id).ToHashSet();
        var evidenceLinks = executionIds.Count == 0
            ? []
            : await (from link in db.TestExecutionEvidence.AsNoTracking()
                     join evidence in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals evidence.Id
                     where executionIds.Contains(link.TestExecutionId)
                     select new { link, evidence }).ToListAsync(ct);
        var sourceFactObjects = executionRows.Select(execution =>
            new ProjectCreationSourceObject($"execution:{execution.Id:N}", "TestExecution",
                execution.Id.ToString("D"), "Evidence",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EvidenceKind"] = "TestExecution",
                    ["Outcome"] = execution.Outcome.ToString(),
                    ["ExecutedBy"] = execution.ExecutedBy,
                    ["Configuration"] = execution.Configuration,
                    ["Determination"] = execution.Determination,
                    ["EvidenceReference"] = execution.EvidenceReference,
                    ["ExecutedAt"] = execution.ExecutedAt.ToString("O"),
                    ["RecordedAt"] = execution.RecordedAt.ToString("O"),
                    ["SoftwareBuildId"] = execution.SoftwareBuildId?.ToString("D") ?? ""
                })).ToList();
        var sourceEvidenceRows = evidenceLinks.Select(x => x.evidence).DistinctBy(x => x.Id).ToList();
        sourceFactObjects.AddRange(sourceEvidenceRows.Select(evidence =>
            new ProjectCreationSourceObject($"evidence:{evidence.Id:N}", "Evidence",
                evidence.Sha256, "Evidence",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EvidenceKind"] = "EvidenceRecord",
                    ["OriginalFileName"] = evidence.OriginalFileName,
                    ["ContentType"] = evidence.ContentType,
                    ["Size"] = evidence.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Sha256"] = evidence.Sha256,
                    ["StorageKey"] = evidence.StorageKey,
                    ["UploadedBy"] = evidence.UploadedBy,
                    ["UploadedAt"] = evidence.UploadedAt.ToString("O")
                })));
        foreach (var link in evidenceLinks)
            traces.Add(new ProjectCreationSourceRelation($"evidence-link:{link.link.Id:N}",
                $"evidence:{link.evidence.Id:N}", $"execution:{link.link.TestExecutionId:N}",
                "EvidenceExecution", new Dictionary<string, string>()));
        var traceArray = traces.ToArray();
        objects = objects.Concat(sourceFactObjects).ToArray();
        // SQLite stores DateTimeOffset as text and cannot translate ordering it. This is a small, exact
        // baseline event projection, so apply the deterministic historical ordering after authorization-filtered
        // materialization; PostgreSQL follows the same result order.
        var baselineEvents = (await db.BaselineEvents.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id)
            .Select(x => new { x.Id, x.EventType, x.ActorId, x.Detail, x.OccurredAt }).ToListAsync(ct))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToList();
        var selectedChangeRequests = await (from selection in db.BaselineSelections.AsNoTracking()
                                             join request in db.SystemChangeRequests.AsNoTracking()
                                                 on selection.ChangeRequestId equals request.Id
                                             where selection.BaselineId == baseline.Id
                                             select new
                                             {
                                                 selection.Id, selection.ChangeRequestId, selection.ChangeRequestDisplayNumber,
                                                 request.BaseNumber, request.Revision, request.State, request.AuthorId,
                                                 request.CreatedAt, request.UpdatedAt
                                             }).ToListAsync(ct);
        var selectedChangeRequestIds = selectedChangeRequests.Select(x => x.ChangeRequestId).ToList();
        var sourceReviewCycles = await db.ReviewCycles.AsNoTracking()
            .Where(x => x.ChangeRequestId.HasValue && selectedChangeRequestIds.Contains(x.ChangeRequestId.Value))
            .Select(x => new { x.Id, x.ChangeRequestId, x.Sequence, x.SnapshotHash, x.State, x.StartedAt, x.CompletedAt })
            .ToListAsync(ct);
        var sourceReviewCycleIds = sourceReviewCycles.Select(x => x.Id).ToList();
        var sourceApprovalSteps = await db.ApprovalSteps.AsNoTracking()
            .Where(x => sourceReviewCycleIds.Contains(x.ReviewCycleId))
            .Select(x => new
            {
                x.Id, x.ReviewCycleId, x.Position, x.ApproverId, x.ApproverName, x.StageName,
                x.StageKind, x.Authority, x.AuthoritySource, x.AuthoritySourceId, x.Rationale, x.State, x.DecidedAt
            }).ToListAsync(ct);
        var selectedTestReviews = await (from selection in db.BaselineTestChangeSelections.AsNoTracking()
                                         join review in db.TestChangeReviews.AsNoTracking()
                                             on selection.TestChangeRequestId equals review.Id
                                         where selection.BaselineId == baseline.Id
                                         select new
                                         {
                                             selection.Id, TestChangeReviewId = selection.TestChangeRequestId,
                                             selection.TestChangeRequestDisplayNumber,
                                             review.BaseNumber, review.Revision, review.State, review.AuthorId,
                                             review.CreatedAt, review.UpdatedAt
                                         }).ToListAsync(ct);
        var selectedTestReviewIds = selectedTestReviews.Select(x => x.TestChangeReviewId).ToList();
        var sourceTestReviewCycles = await db.ReviewCycles.AsNoTracking()
            .Where(x => x.TestChangeReviewId.HasValue && selectedTestReviewIds.Contains(x.TestChangeReviewId.Value))
            .Select(x => new { x.Id, x.TestChangeReviewId, x.Sequence, x.SnapshotHash, x.State, x.StartedAt, x.CompletedAt })
            .ToListAsync(ct);
        var sourceTestReviewCycleIds = sourceTestReviewCycles.Select(x => x.Id).ToList();
        var sourceTestApprovalSteps = await db.ApprovalSteps.AsNoTracking()
            .Where(x => sourceTestReviewCycleIds.Contains(x.ReviewCycleId))
            .Select(x => new
            {
                x.Id, x.ReviewCycleId, x.Position, x.ApproverId, x.ApproverName, x.StageName,
                x.StageKind, x.Authority, x.AuthoritySource, x.AuthoritySourceId, x.Rationale, x.State, x.DecidedAt
            }).ToListAsync(ct);
        var analysis = new ProjectCreationSourceAnalysis("AeroLinkBaseline", "", 0,
            "AeroLink", objects, traceArray, []);
        var snapshot = JsonSerializer.Serialize(new
        {
            baselineId = baseline.Id,
            baseline.DisplayNumber,
            baseline.Name,
            state = baseline.State.ToString(),
            baseline.ContentHash,
            baseline.RequirementsHash,
            baseline.RequirementsMaterializedAt,
            sourceProjectId = sourceProject.Id,
            sourceProjectName = sourceProject.Name,
            baselineEvents,
            selectedChangeRequests,
            sourceReviewCycles,
            sourceApprovalSteps,
            selectedTestReviews,
            sourceTestReviewCycles,
            sourceTestApprovalSteps,
            objects = objects.Select(x => new { x.Key, x.Module, x.SourceIdentifier, x.Kind, x.Attributes }),
            relations = traceArray,
        }, JsonOptions);
        var hash = Sha256(Encoding.UTF8.GetBytes(snapshot));
        var package = await db.ProjectSetupSourcePackages
            .SingleOrDefaultAsync(x => x.DraftId == draft.Id && x.Sha256 == hash, ct);
        if (package is null)
        {
            package = new ProjectSetupSourcePackage(draft.Id, ProjectSetupSourceKind.AeroLinkBaseline,
                $"baseline-{baseline.DisplayNumber}.aerolink", "AeroLinkBaseline", hash, 0, [], actor.UserName, DateTimeOffset.UtcNow,
                baseline.Id);
            package.RecordNativeSnapshot(sourceProject.Id, baseline.State.ToString(), snapshot,
                JsonSerializer.Serialize(analysis with { Sha256 = hash, Size = snapshot.Length }, JsonOptions), DateTimeOffset.UtcNow);
            db.ProjectSetupSourcePackages.Add(package);
        }
        draft.UpdateAnswers(expectedVersion, ProjectSetupStep.StartingPoint, null, null,
            ProjectSetupStartKind.AeroLinkBaseline, baseline.Id, null, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new(package, draft.Version);
    }

    /// <summary>Checks draft access without reading an upload, before the host grants its larger body allowance.</summary>
    public async Task AuthorizeUploadAsync(Guid draftId, AuthenticatedUser actor, CancellationToken ct) =>
        _ = await LoadDraftAsync(draftId, actor, ct);

    public async Task<ProjectSetupSourceMutationResult> UploadAsync(Guid draftId, AuthenticatedUser actor,
        long expectedVersion, string fileName, Stream content, CancellationToken ct)
    {
        var draft = (await LoadDraftAsync(draftId, actor, ct))!;
        if (string.IsNullOrWhiteSpace(fileName)) throw new ProjectSetupInvalidException("A source file name is required.");
        var bytes = await ReadUploadBoundedAsync(content, ct);
        ProjectCreationSourceAnalysis analysis;
        try { analysis = ProjectCreationSourceParser.Analyse(new MemoryStream(bytes, writable: false), fileName); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { throw new ProjectSetupInvalidException(ex.Message, ex); }
        var format = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        var hash = Sha256(bytes);
        var package = await db.ProjectSetupSourcePackages.SingleOrDefaultAsync(x => x.DraftId == draft.Id && x.Sha256 == hash, ct);
        // Upload retry after a lost HTTP response must replay the same staged package. The body is already
        // hash-checked, so this bypass is limited to the exact immutable source bytes and authorized draft.
        if (package is not null && draft.SourceImportId == package.Id)
            return new(package, draft.Version);
        EnsureVersion(draft, expectedVersion);
        if (package is null)
        {
            package = new ProjectSetupSourcePackage(draft.Id, ProjectSetupSourceKind.ExternalBaseline,
                fileName, format, hash, bytes.LongLength, bytes, actor.UserName, DateTimeOffset.UtcNow);
            package.RecordAnalysis(analysis.SourceTool, "{}", JsonSerializer.Serialize(analysis, JsonOptions), DateTimeOffset.UtcNow);
            db.ProjectSetupSourcePackages.Add(package);
        }
        draft.UpdateAnswers(expectedVersion, ProjectSetupStep.StartingPoint, null, null,
            ProjectSetupStartKind.ExternalBaseline, null, package.Id, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new(package, draft.Version);
    }

    public async Task<ProjectSetupSourceMutationResult> SaveConfigurationAsync(Guid draftId, AuthenticatedUser actor,
        InceptionConfigurationCommand command, CancellationToken ct)
    {
        var draft = (await LoadDraftAsync(draftId, actor, ct))!;
        EnsureVersion(draft, command.ExpectedVersion);
        var package = await LoadPackageAsync(draft, ct);
        await ValidateNativePackageAsync(package, actor, ct);
        if (!string.IsNullOrWhiteSpace(command.MetadataJson)
            && command.MetadataJson.Trim() is not "{}" and not "null")
            throw new ProjectSetupInvalidException("Source metadata is parser-derived and cannot be edited during reconciliation.");
        var analysis = ReadAnalysis(package);
        var categories = ParseCategories(command.SelectedCategoriesJson);
        ValidateCategories(package, categories);
        var mapping = ParseMapping(command.MappingJson, analysis);
        ValidateCategorySelections(analysis, mapping, categories);
        var ladder = await ResolveDraftPolicyAsync(draft, ct);
        var reconciliation = Reconcile(analysis, mapping, ladder, draft);
        // Metadata remains the server/parser observation. Browser supplied labels or guessed version/name/date
        // values cannot become source facts; unknown values stay absent.
        package.RecordConfiguration(JsonSerializer.Serialize(categories, JsonOptions),
            JsonSerializer.Serialize(mapping, JsonOptions), DateTimeOffset.UtcNow);
        if (reconciliation.Ready)
            package.RecordReconciliation(JsonSerializer.Serialize(reconciliation, JsonOptions), reconciliation.ManifestHash!, DateTimeOffset.UtcNow);
        draft.UpdateAnswers(command.ExpectedVersion, ProjectSetupStep.Review, null, null, null, null, null,
            null, JsonSerializer.Serialize(categories, JsonOptions), null, null, null, null,
            JsonSerializer.Serialize(mapping, JsonOptions), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new(package, draft.Version);
    }

    public async Task<ProjectSetupSourceReconciliationResult> ReconcileAsync(Guid draftId, AuthenticatedUser actor,
        long expectedVersion, CancellationToken ct)
    {
        var draft = (await LoadDraftAsync(draftId, actor, ct))!;
        EnsureVersion(draft, expectedVersion);
        var package = await LoadPackageAsync(draft, ct);
        await ValidateNativePackageAsync(package, actor, ct);
        var analysis = ReadAnalysis(package);
        var mapping = ReadMapping(package.MappingJson, analysis);
        ValidateCategorySelections(analysis, mapping, ParseCategories(package.SelectedCategoriesJson));
        var ladder = await ResolveDraftPolicyAsync(draft, ct);
        var result = Reconcile(analysis, mapping, ladder, draft);
        if (result.Ready)
            package.RecordReconciliation(JsonSerializer.Serialize(result, JsonOptions), result.ManifestHash!, DateTimeOffset.UtcNow);
        // Reconciliation is a durable draft answer. Advance the draft token so a concurrent save, source
        // replacement, or finalization cannot silently accept an older result.
        draft.UpdateAnswers(expectedVersion, ProjectSetupStep.Review, null, null, null, null, null, null, null,
            null, null, null, null, null, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new(result, draft.Version);
    }

    public async Task<ProjectSetupSourceView?> ReadSourceAsync(Guid draftId, AuthenticatedUser actor, CancellationToken ct)
    {
        var draft = await LoadDraftAsync(draftId, actor, ct, allowMissing: true);
        if (draft is null) return null;
        var package = await SelectedPackageAsync(draft, ct, asNoTracking: true);
        if (package is null) return null;
        await ValidateNativePackageAsync(package, actor, ct);
        return ToView(package, ReadAnalysis(package), draft);
    }

    /// <summary>
    /// Returns the durable source facts recorded beside a materialized project. The exact source snapshot stays
    /// server-owned; this projection removes storage keys while retaining source identities, revisions, states,
    /// target links, and the captured fact payload needed to inspect provenance.
    /// </summary>
    public async Task<ProjectInceptionSourceProjection?> ReadMaterializedSourceAsync(Guid projectId,
        AuthenticatedUser actor, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return null;
        if (!actor.IsAdministrator && !await db.ProgramMemberships.AsNoTracking()
                .AnyAsync(x => x.UserId == actor.Id && x.ProgramId == project.ProgramId && x.EndedAt == null, ct))
            throw new ProjectSetupAccessException();
        var records = (await db.ProjectInceptionSourceRecords.AsNoTracking()
                .Where(x => x.ProjectId == projectId).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new ProjectInceptionSourceRecordView(x.Id, x.PackageId, x.BaselineId, x.TargetKind,
                x.TargetId, x.TargetRevisionId, x.SourceKey, x.SourceModule, x.SourceIdentifier,
                x.SourceRevision, x.SourceState, RedactStorageKeys(x.SourceSnapshotJson), x.CreatedAt))
            .ToArray();
        if (records.Length == 0) return null;
        var packageIds = records.Select(x => x.PackageId).Distinct().ToArray();
        var package = (await db.ProjectSetupSourcePackages.AsNoTracking()
                .Where(x => packageIds.Contains(x.Id)).ToListAsync(ct))
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).FirstOrDefault();
        var assertionBaselineId = records[0].BaselineId;
        // Bind the projection to the package's exact assertion hash and action. A later signature for this
        // artifact must never replace the person who accepted this immutable source assertion. SQLite cannot
        // translate DateTimeOffset ordering, so materialize the bounded matching rows and order in memory.
        var acceptance = package?.AssertionHash is null ? null : (await db.ElectronicSignatures.AsNoTracking()
            .Where(x => x.ProgramId == project.ProgramId
                && x.ArtifactType == "ProjectInceptionSourceAssertion"
                && x.ArtifactId == assertionBaselineId
                && x.Action == "AcceptSource"
                && x.ContentHash == package.AssertionHash)
            .Select(x => new ProjectInceptionSourceAcceptance(x.UserId, x.UserName, x.DisplayName,
                x.SignedAt, x.Action, x.Meaning, x.ContentHash, x.Authority, x.Rationale))
            .ToListAsync(ct)).OrderByDescending(x => x.SignedAt).FirstOrDefault();
        var packageView = package is null ? null : new ProjectInceptionSourcePackageView(package.Id,
            package.Kind.ToString(), package.FileName, package.Format, package.Sha256, package.SizeBytes,
            package.SourceTool, package.SourceBaselineId, package.SourceProjectId, package.SourceState,
            ParseAndRedact(package.SelectedCategoriesJson), ParseAndRedact(package.MappingJson),
            package.ReconciliationJson.Length == 0 ? null : ParseAndRedact(package.ReconciliationJson),
            package.ManifestHash, package.AssertionHash, package.CapturedBy, package.CapturedAt,
            package.MaterializedBaselineId, package.UpdatedAt);
        return new ProjectInceptionSourceProjection(projectId, packageView, acceptance, records);
    }

    public async Task<IReadOnlyList<ProjectSetupSourceOption>> ListNativeOptionsAsync(AuthenticatedUser actor,
        int offset, int limit, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        if (offset < 0 || limit is < 1 or > 200) throw new ProjectSetupInvalidException("Source option paging is invalid.");
        var accessiblePrograms = actor.IsAdministrator ? null : (await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == actor.Id && x.EndedAt == null)
            .Select(x => x.ProgramId).ToListAsync(ct)).ToHashSet();
        var query = from baseline in db.CandidateBaselines.AsNoTracking()
                    join project in db.Projects.AsNoTracking() on baseline.ProjectId equals project.Id
                    where baseline.State != CandidateBaselineState.Draft && baseline.RequirementsMaterializedAt != null
                    select new { baseline, project };
        var rows = await query.ToListAsync(ct);
        if (accessiblePrograms is not null) rows = rows.Where(x => accessiblePrograms.Contains(x.project.ProgramId)).ToList();
        var result = new List<ProjectSetupSourceOption>();
        foreach (var row in rows.OrderByDescending(x => x.baseline.CreatedAt).ThenBy(x => x.baseline.Id).Skip(offset).Take(limit))
        {
            var ids = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == row.baseline.Id)
                .Join(db.Requirements.AsNoTracking(), x => x.ArtifactId, x => x.Id, (_, a) => a.Level).ToListAsync(ct);
            var verificationCounts = await (from selection in db.BaselineTestProcedures.AsNoTracking()
                                            join procedure in db.TestProcedures.AsNoTracking()
                                                on selection.ProcedureId equals procedure.Id
                                            where selection.BaselineId == row.baseline.Id
                                            group procedure by procedure.ArtifactKind into groupRows
                                            select new { Kind = groupRows.Key, Count = groupRows.Count() }).ToListAsync(ct);
            var caseCount = verificationCounts.SingleOrDefault(x => x.Kind == VerificationArtifactKind.Case)?.Count ?? 0;
            var procedureCount = verificationCounts.SingleOrDefault(x => x.Kind == VerificationArtifactKind.Procedure)?.Count ?? 0;
            // Evidence is a source fact projection, not a baseline membership table. Count the exact
            // executions of this baseline's software builds and the distinct evidence records attached to
            // those executions; CaptureNativeAsync takes the same joins and preserves both as source-only data.
            var sourceBuildIds = await db.SoftwareBuilds.AsNoTracking()
                .Where(x => x.BaselineId == row.baseline.Id)
                .Select(x => x.Id).ToListAsync(ct);
            var sourceExecutionIds = sourceBuildIds.Count == 0
                ? []
                : await db.TestExecutions.AsNoTracking()
                    .Where(x => x.SoftwareBuildId != null && sourceBuildIds.Contains(x.SoftwareBuildId.Value))
                    .Select(x => x.Id).ToListAsync(ct);
            var attachedEvidenceIds = sourceExecutionIds.Count == 0
                ? []
                : await db.TestExecutionEvidence.AsNoTracking()
                    .Where(x => sourceExecutionIds.Contains(x.TestExecutionId))
                    .Select(x => x.EvidenceId).ToListAsync(ct);
            var evidenceFactCount = sourceExecutionIds.Count + attachedEvidenceIds.Distinct().Count();
            result.Add(new(row.baseline.Id, row.project.Id, row.project.Name, row.baseline.Name,
                row.baseline.DisplayNumber, row.baseline.State.ToString(), ids.Count, caseCount, procedureCount,
                evidenceFactCount));
        }
        return result;
    }

    internal async Task MaterializeAsync(ProjectSetupDraft draft, ProjectRecord project, CandidateBaseline targetBaseline,
        AuthenticatedUser actor, string? password, string? suppliedAssertionHash, bool sourceAssertionAccepted,
        ILadderPolicy ladder, CancellationToken ct)
    {
        if (draft.StartKind == ProjectSetupStartKind.Fresh) return;
        var package = await LoadPackageAsync(draft, ct);
        await ValidateNativePackageAsync(package, actor, ct);
        if (package.Stage != ProjectSetupSourceStage.Reconciled || package.ManifestHash is null)
            throw new ProjectSetupInvalidException("The selected source must be fully reconciled before finalization.");
        var analysis = ReadAnalysis(package);
        var mapping = ReadMapping(package.MappingJson, analysis);
        ValidateCategorySelections(analysis, mapping, ParseCategories(package.SelectedCategoriesJson));
        var reconciliation = Reconcile(analysis, mapping, ladder, draft);
        if (!reconciliation.Ready || !string.Equals(reconciliation.ManifestHash, package.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new ProjectSetupInvalidException("The source changed or no longer reconciles against the accepted ladder; review it again.");
        var assertion = BuildAssertion(package, draft, project, targetBaseline, ladder, reconciliation);
        if (!sourceAssertionAccepted || !string.Equals(suppliedAssertionHash, assertion.Hash, StringComparison.OrdinalIgnoreCase))
            throw new ProjectSetupInvalidException("Explicit acceptance of the exact source assertion is required.");
        if (string.IsNullOrWhiteSpace(password) || !await identity.ConfirmPasswordAsync(actor.Id, password, ct))
            throw new ProjectSetupInvalidException("The accepting administrator password was not confirmed.");
        if (!actor.IsAdministrator && actor.Id != draft.CreatorUserId)
            throw new ProjectSetupAccessException();
        var now = DateTimeOffset.UtcNow;
        BaselineImport? import = null;
        if (package.Kind == ProjectSetupSourceKind.AeroLinkBaseline)
        {
            // The package metadata is the immutable native snapshot, including the source baseline's state,
            // manifest facts, and any source approvals/execution facts present at capture time.
            var sourceBaselineId = package.SourceBaselineId!.Value;
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id,
                targetBaseline.Id, "AeroLinkBaselineSnapshot", targetBaseline.Id, null,
                $"baseline:{sourceBaselineId:N}", "AeroLinkBaseline", sourceBaselineId.ToString("D"),
                "", package.SourceState ?? "", package.MetadataJson, now));
        }
        if (package.Kind == ProjectSetupSourceKind.ExternalBaseline)
        {
            var metadata = ParseMetadata(package.MetadataJson);
            import = new BaselineImport(project.Id, package.SourceTool.Length == 0 ? package.Format : package.SourceTool,
                metadata.Version, metadata.Name, metadata.Date, package.FileName, package.Sha256, package.SizeBytes,
                ImportedArtifactKinds.Requirements, null, null, actor.UserName, now);
            import.RecordAnalysis(now);
            import.RecordMapping(package.MappingJson, now);
            import.NoteSourceRecordsAccountedFor(reconciliation.IncludedObjects, now);
            import.RecordReconciliation(JsonSerializer.Serialize(reconciliation, JsonOptions), now);
            db.BaselineImports.Add(import);
        }
        var revisionBySource = new Dictionary<string, RequirementRevision>(StringComparer.Ordinal);
        foreach (var item in reconciliation.Requirements.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            var prefix = ladder.RequirementPrefix(item.Level);
            var number = await IdentifierAllocator.NextRequirementAsync(db, prefix, ct);
            var artifact = new RequirementArtifact(project.Id, number, item.Level, now);
            db.Requirements.Add(artifact);
            RequirementRevision revision;
            if (package.Kind == ProjectSetupSourceKind.AeroLinkBaseline)
            {
                var source = analysis.Objects.Single(x => x.Key == item.SourceKey);
                var sourceRevision = source.Attributes.GetValueOrDefault("RevisionId", source.Attributes.GetValueOrDefault("Revision", "0"));
                revision = RequirementRevision.FromAeroLinkBaseline(artifact.Id, 0, item.Statement, item.Rationale,
                    RequirementRevisionState.Active, package.SourceBaselineId!.Value, targetBaseline.Id, now,
                    sourceRevision, item.VerificationMethod);
            }
            else
            {
                revision = RequirementRevision.FromExternalSourcePackage(artifact.Id, 0, item.Statement, item.Rationale,
                    RequirementRevisionState.Active, import!.Id, targetBaseline.Id, now);
            }
            db.RequirementRevisions.Add(revision);
            db.BaselineRequirements.Add(new BaselineRequirementSelection(targetBaseline.Id, artifact.Id, revision.Id));
            revisionBySource[item.SourceKey] = revision;
            var sourceObject = analysis.Objects.Single(x => x.Key == item.SourceKey);
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id, targetBaseline.Id,
                "Requirement", artifact.Id, revision.Id, sourceObject.Key, sourceObject.Module, sourceObject.SourceIdentifier,
                sourceObject.Attributes.GetValueOrDefault("Revision", ""), sourceObject.Attributes.GetValueOrDefault("State", ""),
                JsonSerializer.Serialize(sourceObject, JsonOptions), now));
            if (import is not null)
            {
                var sourceIdentity = new SourceIdentity(project.Id, import.Id, import.SourceSystem,
                    sourceObject.Module.Length == 0 ? "Imported" : sourceObject.Module, sourceObject.Key,
                    sourceObject.SourceIdentifier.Length == 0 ? sourceObject.Key : sourceObject.SourceIdentifier, now);
                db.SourceIdentities.Add(sourceIdentity);
                db.SourceIdentityLinks.Add(sourceIdentity.LinkToFromImport(revision.Id, import.Id, now));
            }
        }
        var verificationBySource = new Dictionary<string, (TestProcedure Artifact, TestProcedureRevision Revision, string Kind)>(StringComparer.Ordinal);
        foreach (var item in reconciliation.Verifications ?? [])
        {
            var artifactKind = item.Kind.Equals("Procedure", StringComparison.OrdinalIgnoreCase)
                ? VerificationArtifactKind.Procedure : VerificationArtifactKind.Case;
            var procedureLevel = item.Level switch
            {
                RequirementLevel.System => TestProcedureLevel.System,
                RequirementLevel.HighLevel => TestProcedureLevel.HighLevel,
                RequirementLevel.LowLevel => TestProcedureLevel.LowLevel,
                _ => throw new ProjectSetupInvalidException($"Source verification level {item.Level} is not supported by the target ladder."),
            };
            var number = await IdentifierAllocator.NextTestProcedureAsync(db, procedureLevel, artifactKind, ct, ladder);
            var ownerId = package.Kind == ProjectSetupSourceKind.AeroLinkBaseline
                ? item.SourceOwnerId
                : null;
            if (package.Kind == ProjectSetupSourceKind.AeroLinkBaseline
                && (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(item.SourceAuthorId)))
                throw new ProjectSetupInvalidException("The native verification source is missing its exact owner or author identity.");
            // The source identities are provenance facts, retained in the source snapshot and reconciliation.
            // They are not target-project staffing assignments: the new project starts with no inherited roster,
            // so its copied verification artifact is deliberately unassigned until project personnel configure it.
            var targetOwnerId = "";
            var targetAuthorId = "";
            var procedure = new TestProcedure(project.Id, number, item.Title, targetOwnerId, now, procedureLevel,
                ladder, artifactKind, artifactKind == VerificationArtifactKind.Procedure && procedureLevel != TestProcedureLevel.System
                    ? VerificationProcedureParentKind.Allocated : VerificationProcedureParentKind.Unspecified);
            db.TestProcedures.Add(procedure);
            var revision = new TestProcedureRevision(procedure.Id, 0, item.Objective, item.Preconditions,
                item.Steps, item.ExpectedResult, TestProcedureState.Draft, targetAuthorId, now,
                effectiveBaselineId: targetBaseline.Id,
                parentKind: artifactKind == VerificationArtifactKind.Procedure && procedureLevel != TestProcedureLevel.System
                    ? VerificationProcedureParentKind.Allocated : VerificationProcedureParentKind.Unspecified);
            db.TestProcedureRevisions.Add(revision);
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(targetBaseline.Id, procedure.Id, revision.Id));
            verificationBySource[item.SourceKey] = (procedure, revision, item.Kind);
            var sourceObject = analysis.Objects.Single(x => x.Key == item.SourceKey);
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id, targetBaseline.Id,
                artifactKind == VerificationArtifactKind.Case ? "TestCase" : "TestProcedure", procedure.Id, revision.Id,
                sourceObject.Key, sourceObject.Module, sourceObject.SourceIdentifier, item.SourceRevision, item.SourceState,
                JsonSerializer.Serialize(sourceObject, JsonOptions), now));
        }
        foreach (var relationship in reconciliation.Relationships ?? [])
        {
            if (package.Kind != ProjectSetupSourceKind.AeroLinkBaseline)
                throw new ProjectSetupInvalidException("External sources cannot carry native verification relationships.");
            if (relationship.RelationshipKind.Equals("CaseProcedure", StringComparison.OrdinalIgnoreCase))
            {
                if (!verificationBySource.TryGetValue(relationship.SourceEndpointKey, out var @case)
                    || !verificationBySource.TryGetValue(relationship.TargetEndpointKey, out var procedure)
                    || !@case.Kind.Equals("Case", StringComparison.OrdinalIgnoreCase)
                    || !procedure.Kind.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                    throw new ProjectSetupInvalidException($"Native CaseProcedure relation '{relationship.SourceKey}' references a verification object that was not materialized.");
                var link = new TestCaseProcedureLink(@case.Revision.Id, procedure.Revision.Id);
                db.TestCaseProcedureLinks.Add(link);
                db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id, targetBaseline.Id,
                    "TestCaseProcedure", link.Id, null, relationship.SourceKey, "", relationship.SourceKey, "", "",
                    JsonSerializer.Serialize(analysis.Relations.Single(x => x.Key == relationship.SourceKey), JsonOptions), now));
            }
            else if (relationship.RelationshipKind.Equals("VerificationCoverage", StringComparison.OrdinalIgnoreCase))
            {
                if (!verificationBySource.TryGetValue(relationship.SourceEndpointKey, out var procedure)
                    || !revisionBySource.TryGetValue(relationship.TargetEndpointKey, out var requirement)
                    || !procedure.Kind.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
                    throw new ProjectSetupInvalidException($"Native VerificationCoverage relation '{relationship.SourceKey}' references a verification or requirement object that was not materialized.");
                var coverage = new TestRequirementCoverage(procedure.Revision.Id, requirement.Id);
                db.TestCoverage.Add(coverage);
                db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id, targetBaseline.Id,
                    "TestRequirementCoverage", coverage.Id, null, relationship.SourceKey, "", relationship.SourceKey, "", "",
                    JsonSerializer.Serialize(analysis.Relations.Single(x => x.Key == relationship.SourceKey), JsonOptions), now));
            }
            else if (relationship.RelationshipKind.Equals("EvidenceExecution", StringComparison.OrdinalIgnoreCase))
            {
                // Evidence/execution relationships remain source-only facts. The source objects are recorded
                // below; no target execution or evidence result is created during inception.
                continue;
            }
            else
            {
                throw new ProjectSetupInvalidException($"Unsupported native verification relationship '{relationship.RelationshipKind}'.");
            }
        }
        foreach (var sourceFact in reconciliation.SourceFacts ?? [])
        {
            var sourceObject = analysis.Objects.Single(x => x.Key == sourceFact.SourceKey);
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id,
                targetBaseline.Id, "SourceEvidenceFact", targetBaseline.Id, null, sourceFact.SourceKey,
                sourceFact.SourceModule, sourceFact.SourceIdentifier, "", "",
                JsonSerializer.Serialize(sourceObject, JsonOptions), now));
        }
        foreach (var relationship in (reconciliation.Relationships ?? [])
                     .Where(x => x.RelationshipKind.Equals("EvidenceExecution", StringComparison.OrdinalIgnoreCase)))
        {
            var sourceRelation = analysis.Relations.Single(x => x.Key == relationship.SourceKey);
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id,
                targetBaseline.Id, "SourceEvidenceRelation", targetBaseline.Id, null, relationship.SourceKey,
                "", relationship.SourceKey, "", "", JsonSerializer.Serialize(sourceRelation, JsonOptions), now));
        }
        foreach (var trace in reconciliation.Traces)
        {
            if (!revisionBySource.TryGetValue(trace.ChildSourceKey, out var child)
                || !revisionBySource.TryGetValue(trace.ParentSourceKey, out var parent))
                throw new ProjectSetupInvalidException("The reconciled trace references a source object that was not materialized.");
            var childLevel = reconciliation.Requirements.Single(x => x.SourceKey == trace.ChildSourceKey).Level;
            var parentLevel = reconciliation.Requirements.Single(x => x.SourceKey == trace.ParentSourceKey).Level;
            RequirementTracePolicy.Validate(ladder, childLevel, parentLevel, trace.Type);
            var targetTrace = new RequirementTraceLink(project.Id, child.Id, parent.Id, trace.Type,
                "Imported exact source relationship; no new engineering approval is asserted.", now);
            db.RequirementTraces.Add(targetTrace);
            var relation = analysis.Relations.Single(x => x.Key == trace.SourceKey);
            db.ProjectInceptionSourceRecords.Add(new ProjectInceptionSourceRecord(package.Id, project.Id, targetBaseline.Id,
                "RequirementTrace", targetTrace.Id, null, relation.Key, "", relation.Key, "", "", JsonSerializer.Serialize(relation, JsonOptions), now));
        }
        targetBaseline.FreezeForInception(actor.UserName, now);
        var manifest = string.Join(";", revisionBySource.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}:{x.Value.Id}:{x.Value.ArtifactId}"));
        targetBaseline.MarkRequirementsMaterialized(actor.UserName, Sha256(Encoding.UTF8.GetBytes(manifest)), revisionBySource.Count, now);
        if (verificationBySource.Count > 0)
        {
            var verificationManifest = string.Join(";", verificationBySource.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}:{x.Value.Revision.Id}:{x.Value.Artifact.Id}"));
            targetBaseline.MarkTestProceduresMaterialized(actor.UserName,
                Sha256(Encoding.UTF8.GetBytes(verificationManifest)), verificationBySource.Count, now);
        }
        if (import is not null)
            import.AcceptForExternalPackage(actor.UserName, targetBaseline.Id, targetBaseline.ReleaseId,
                package.ManifestHash!, now);
        package.MarkMaterialized(project.Id, targetBaseline.Id, assertion.Hash, now);
        db.ElectronicSignatures.Add(new ElectronicSignature(actor.Id, actor.UserName, actor.DisplayName, project.ProgramId,
            "ProjectInceptionSourceAssertion", targetBaseline.Id, "1", "AcceptSource", assertion.Meaning,
            assertion.Hash, "local", now, "ProjectAdministrator", rationale: "Accepted exact source provenance and reconciliation."));
    }

    private async Task<ProjectSetupDraft?> LoadDraftAsync(Guid id, AuthenticatedUser actor, CancellationToken ct,
        bool allowMissing = false)
    {
        RequireAuthenticated(actor);
        var draft = await db.ProjectSetupDrafts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (draft is null) return allowMissing ? null : throw new ProjectSetupNotFoundException();
        if (!actor.IsAdministrator && draft.CreatorUserId != actor.Id) throw new ProjectSetupAccessException();
        return draft;
    }

    private async Task<ProjectSetupSourcePackage> LoadPackageAsync(ProjectSetupDraft draft, CancellationToken ct) =>
        await SelectedPackageAsync(draft, ct)
        ?? throw new ProjectSetupInvalidException("Choose and stage a source before configuring inception.");

    /// <summary>
    /// Revalidates the source authority at every source boundary. A source can be selected while its baseline is
    /// available, then reopened or its source-project access can be revoked before a resumed draft is read or
    /// finalized. The staged snapshot remains durable, but it must never turn stale authority into usable content.
    /// </summary>
    private async Task ValidateNativePackageAsync(ProjectSetupSourcePackage package, AuthenticatedUser actor,
        CancellationToken ct)
    {
        if (package.Kind != ProjectSetupSourceKind.AeroLinkBaseline) return;
        if (package.SourceBaselineId is not Guid sourceBaselineId || package.SourceProjectId is not Guid sourceProjectId)
            throw new ProjectSetupInvalidException("The native source snapshot is missing its exact source identities.");
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceBaselineId, ct)
            ?? throw new ProjectSetupInvalidException("The selected AeroLink baseline is no longer available.");
        if (baseline.ProjectId != sourceProjectId
            || baseline.State is not (CandidateBaselineState.Frozen or CandidateBaselineState.Released)
            || baseline.RequirementsMaterializedAt is null)
            throw new ProjectSetupInvalidException("The selected AeroLink baseline is no longer a materialized Frozen or Released source.");
        var sourceProject = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceProjectId, ct)
            ?? throw new ProjectSetupInvalidException("The selected native source project is no longer available.");
        await RequireCurrentSourceProgramAccessAsync(sourceProject.ProgramId, actor, ct);
    }

    /// <summary>
    /// The authenticated request contains a deliberately small directory snapshot. Native-source authority is
    /// time-sensitive, so it must be resolved against the current persisted membership at every source boundary.
    /// An ended membership never grants access, even when an older token still carries the former program list.
    /// </summary>
    private async Task RequireCurrentSourceProgramAccessAsync(Guid programId, AuthenticatedUser actor,
        CancellationToken ct)
    {
        if (actor.IsAdministrator) return;
        var hasCurrentMembership = await db.ProgramMemberships.AsNoTracking()
            .AnyAsync(x => x.UserId == actor.Id && x.ProgramId == programId && x.EndedAt == null, ct);
        if (!hasCurrentMembership) throw new ProjectSetupAccessException();
    }

    private static async Task<byte[]> ReadUploadBoundedAsync(Stream content, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await content.ReadAsync(rented.AsMemory(0, rented.Length), ct);
                if (read == 0) break;
                total += read;
                if (total > MaxUploadBytes)
                    throw new ProjectSetupInvalidException("Source files must be between 1 byte and 50 MB.");
                await buffer.WriteAsync(rented.AsMemory(0, read), ct);
            }
            if (total == 0) throw new ProjectSetupInvalidException("Source files must be between 1 byte and 50 MB.");
            return buffer.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private Task<ProjectSetupSourcePackage?> SelectedPackageAsync(ProjectSetupDraft draft, CancellationToken ct,
        bool asNoTracking = false)
    {
        var query = db.ProjectSetupSourcePackages.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();
        if (draft.SourceImportId is Guid importId)
            return query.SingleOrDefaultAsync(x => x.Id == importId && x.DraftId == draft.Id, ct);
        if (draft.SourceBaselineId is Guid baselineId)
            return query.SingleOrDefaultAsync(x => x.SourceBaselineId == baselineId && x.DraftId == draft.Id, ct);
        return Task.FromResult<ProjectSetupSourcePackage?>(null);
    }

    private async Task<ILadderPolicy> ResolveDraftPolicyAsync(ProjectSetupDraft draft, CancellationToken ct)
    {
        _ = policyResolver;
        _ = ct;
        // A setup draft has no Project row yet. Compile its typed ladder against the same product catalogue used
        // by the activation authority so reconciliation never trusts a browser supplied level list.
        var configuration = ProjectSetupLadderFactory.FromJson(draft.ProjectId, draft.LadderJson, DateTimeOffset.UtcNow);
        var resolved = ProjectLadderResolver.Resolve(configuration, LegacyLadderPolicy.Instance);
        return new ResolvedProjectLadderPolicy(resolved, LegacyLadderPolicy.Instance);
    }

    private static InceptionReconciliation Reconcile(ProjectCreationSourceAnalysis source, InceptionMapping mapping,
        ILadderPolicy ladder, ProjectSetupDraft draft)
    {
        var ladderHash = ProjectLadderSnapshot.HashV2(ProjectSetupLadderFactory.Steps(draft.LadderJson),
            ProjectSetupLadderFactory.Relationships(draft.LadderJson), LegacyLadderPolicy.Instance);
        if (source.Format is not ("AeroLinkBaseline" or "AeroLink"))
            return ProjectCreationSourceReconciler.Reconcile(source, mapping, ladder,
                new VerificationMethodPolicy(FoundingVerificationMethods.Ordered), ladderHash);

        // The reviewed generic reconciler intentionally has a requirement/trace output contract. Native
        // baselines additionally carry exact Case/Procedure source facts, which are reconciled here with the
        // same explicit attribute-accounting rules before they are materialized as new target artifacts.
        var requirements = source.Objects.Where(x => x.Kind is "Requirement" or "Unmapped" or "").ToArray();
        var verification = source.Objects.Where(x => x.Kind is "Case" or "Procedure").ToArray();
        var unsupported = source.Objects.Where(x => x.Kind is not ("Requirement" or "Unmapped" or "" or "Case" or "Procedure")).ToArray();
        var nativeRelationshipKinds = new[] { "CaseProcedure", "VerificationCoverage", "EvidenceExecution" };
        var baseRelations = source.Relations
            .Where(x => !nativeRelationshipKinds.Contains(x.Type, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var baseMapping = mapping with
        {
            Objects = mapping.Objects.Where(x => requirements.Any(o => o.Key == x.SourceKey)).ToArray(),
            Relations = mapping.Relations.Where(x => baseRelations.Any(r => r.Key == x.SourceKey)).ToArray()
        };
        var baseAnalysis = source with { Objects = requirements, Relations = baseRelations };
        var result = ProjectCreationSourceReconciler.Reconcile(baseAnalysis, baseMapping, ladder,
            new VerificationMethodPolicy(FoundingVerificationMethods.Ordered), ladderHash);
        var errors = result.Errors.ToList();
        var verificationRows = new List<ReconciledInceptionVerification>();
        var sourceFactRows = new List<ReconciledInceptionSourceFact>();
        var nativeRelationships = new List<ReconciledInceptionRelationship>();
        var verificationExcluded = 0;
        var sourceFactExcluded = 0;
        var nativeRelationshipExcluded = 0;
        foreach (var item in verification)
        {
            var selection = mapping.Objects.SingleOrDefault(x => x.SourceKey == item.Key);
            if (selection is null) { errors.Add($"Source verification object '{item.Key}' has no mapping or exclusion."); continue; }
            if (!selection.Include)
            {
                if (string.IsNullOrWhiteSpace(selection.ExclusionReason)) errors.Add($"Excluded verification object '{item.Key}' needs a reason.");
                verificationExcluded++; continue;
            }
            if (!string.Equals(source.Format, "AeroLinkBaseline", StringComparison.OrdinalIgnoreCase))
            { errors.Add($"External source verification object '{item.Key}' is unsupported by this inception matrix; explicitly exclude it."); continue; }
            if (selection.Level is not { } level || !ladder.OrderedLevels.Contains(level))
            { errors.Add($"Verification object '{item.Key}' needs a supported level in the accepted ladder."); continue; }
            var fields = new Dictionary<InceptionAttributeDestination, string>();
            var attributes = selection.Attributes.ToDictionary(x => x.SourceAttribute, StringComparer.Ordinal);
            foreach (var attribute in item.Attributes)
            {
                if (!attributes.TryGetValue(attribute.Key, out var rule)) { errors.Add($"Verification object '{item.Key}' attribute '{attribute.Key}' is unmapped."); continue; }
                if (attribute.Key is "SourceOwnerId" or "SourceAuthorId"
                    && rule.Destination is not (InceptionAttributeDestination.SourceOnly or InceptionAttributeDestination.Exclude))
                {
                    errors.Add($"Verification object '{item.Key}' source identity '{attribute.Key}' must remain source-only.");
                    continue;
                }
                if (rule.Destination is InceptionAttributeDestination.SourceOnly or InceptionAttributeDestination.Exclude)
                {
                    if (string.IsNullOrWhiteSpace(rule.Reason)) errors.Add($"Verification object '{item.Key}' source-only/excluded attribute '{attribute.Key}' needs a reason.");
                    continue;
                }
                if (!fields.TryAdd(rule.Destination, attribute.Value)) errors.Add($"Verification object '{item.Key}' maps multiple attributes to {rule.Destination}.");
            }
            var sourceIdentifier = fields.GetValueOrDefault(InceptionAttributeDestination.SourceIdentifier, item.SourceIdentifier);
            var title = fields.GetValueOrDefault(InceptionAttributeDestination.Title, item.SourceIdentifier);
            var objective = fields.GetValueOrDefault(InceptionAttributeDestination.Objective, "");
            var steps = fields.GetValueOrDefault(InceptionAttributeDestination.Steps, "");
            var expected = fields.GetValueOrDefault(InceptionAttributeDestination.ExpectedResult, "");
            if (string.IsNullOrWhiteSpace(sourceIdentifier)) errors.Add($"Verification object '{item.Key}' needs an exact source identifier.");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(objective)
                || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expected))
                errors.Add($"Verification object '{item.Key}' needs mapped title, objective, steps, and expected result.");
            var sourceOwnerId = item.Attributes.GetValueOrDefault("SourceOwnerId", "");
            var sourceAuthorId = item.Attributes.GetValueOrDefault("SourceAuthorId", "");
            if (string.IsNullOrWhiteSpace(sourceOwnerId) || string.IsNullOrWhiteSpace(sourceAuthorId))
                errors.Add($"Verification object '{item.Key}' needs exact source owner and author identities.");
            verificationRows.Add(new(item.Key, item.Module, sourceIdentifier, level, item.Kind, title, objective,
                fields.GetValueOrDefault(InceptionAttributeDestination.Preconditions, ""), steps, expected,
                item.Attributes.GetValueOrDefault("Revision", ""), item.Attributes.GetValueOrDefault("State", ""),
                sourceOwnerId, sourceAuthorId));
        }
        foreach (var item in source.Objects.Where(x => x.Kind == "Evidence"))
        {
            var selection = mapping.Objects.SingleOrDefault(x => x.SourceKey == item.Key);
            if (selection is null)
            {
                errors.Add($"Source evidence fact '{item.Key}' has no mapping or exclusion.");
                continue;
            }
            if (!selection.Include)
            {
                if (string.IsNullOrWhiteSpace(selection.ExclusionReason))
                    errors.Add($"Excluded source evidence fact '{item.Key}' needs a reason.");
                sourceFactExcluded++;
                continue;
            }
            var attributes = selection.Attributes.ToDictionary(x => x.SourceAttribute, StringComparer.Ordinal);
            foreach (var attribute in item.Attributes)
            {
                if (!attributes.TryGetValue(attribute.Key, out var rule))
                {
                    errors.Add($"Source evidence fact '{item.Key}' attribute '{attribute.Key}' is unmapped.");
                    continue;
                }
                if (rule.Destination is not (InceptionAttributeDestination.SourceOnly or InceptionAttributeDestination.Exclude))
                {
                    errors.Add($"Source evidence fact '{item.Key}' can only be retained as source-only data.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(rule.Reason))
                    errors.Add($"Source evidence fact '{item.Key}' source-only attribute '{attribute.Key}' needs a reason.");
            }
            if (string.IsNullOrWhiteSpace(item.SourceIdentifier))
                errors.Add($"Source evidence fact '{item.Key}' needs an exact source identifier.");
            sourceFactRows.Add(new(item.Key, item.Module, item.SourceIdentifier, item.Attributes.GetValueOrDefault("EvidenceKind", "Evidence")));
        }
        foreach (var item in unsupported)
        {
            var selection = mapping.Objects.SingleOrDefault(x => x.SourceKey == item.Key);
            if (selection is null) errors.Add($"Unsupported source object '{item.Key}' has no mapping or exclusion.");
            else if (selection.Include) errors.Add($"Source object '{item.Key}' has unsupported kind '{item.Kind}'; explicitly exclude it.");
            else if (string.IsNullOrWhiteSpace(selection.ExclusionReason)) errors.Add($"Excluded source object '{item.Key}' needs a reason.");
        }
        foreach (var relation in source.Relations.Where(x => nativeRelationshipKinds.Contains(x.Type, StringComparer.OrdinalIgnoreCase)))
        {
            var selection = mapping.Relations.SingleOrDefault(x => x.SourceKey == relation.Key);
            if (selection is null)
            {
                errors.Add($"Native verification relation '{relation.Key}' has no mapping or exclusion.");
                continue;
            }
            if (!selection.Include)
            {
                if (string.IsNullOrWhiteSpace(selection.ExclusionReason))
                    errors.Add($"Excluded native verification relation '{relation.Key}' needs a reason.");
                nativeRelationshipExcluded++;
                continue;
            }
            if (!string.Equals(selection.RelationshipKind, relation.Type, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Native verification relation '{relation.Key}' must explicitly retain relationship kind '{relation.Type}'.");
                continue;
            }
            if (relation.Attributes.Count > 0)
            {
                errors.Add($"Native verification relation '{relation.Key}' carries unsupported attributes; explicitly exclude it with a reason.");
                continue;
            }
            var sourceObject = source.Objects.SingleOrDefault(x => x.Key == relation.SourceKey);
            var targetObject = source.Objects.SingleOrDefault(x => x.Key == relation.TargetKey);
            if (sourceObject is null || targetObject is null
                || mapping.Objects.SingleOrDefault(x => x.SourceKey == relation.SourceKey)?.Include != true
                || mapping.Objects.SingleOrDefault(x => x.SourceKey == relation.TargetKey)?.Include != true)
            {
                errors.Add($"Native verification relation '{relation.Key}' requires both exact included endpoint objects.");
                continue;
            }
            if (relation.Type.Equals("CaseProcedure", StringComparison.OrdinalIgnoreCase)
                && (sourceObject.Kind != "Case" || targetObject.Kind != "Procedure"))
            {
                errors.Add($"Native CaseProcedure relation '{relation.Key}' must point from an exact Case to an exact Procedure.");
                continue;
            }
            if (relation.Type.Equals("VerificationCoverage", StringComparison.OrdinalIgnoreCase)
                && (sourceObject.Kind != "Procedure" || targetObject.Kind != "Requirement"))
            {
                errors.Add($"Native VerificationCoverage relation '{relation.Key}' must point from an exact Procedure to an exact Requirement.");
                continue;
            }
            if (relation.Type.Equals("EvidenceExecution", StringComparison.OrdinalIgnoreCase)
                && (sourceObject.Kind != "Evidence" || targetObject.Kind != "Evidence"
                    || !string.Equals(sourceObject.Attributes.GetValueOrDefault("EvidenceKind", ""), "EvidenceRecord",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(targetObject.Attributes.GetValueOrDefault("EvidenceKind", ""), "TestExecution",
                        StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Native EvidenceExecution relation '{relation.Key}' must point from an exact evidence record to its source execution fact.");
                continue;
            }
            nativeRelationships.Add(new(relation.Key, relation.SourceKey, relation.TargetKey, relation.Type));
        }
        // A non-System software Procedure carries an explicit Case parent decision. Requiring a source relation
        // here prevents materialization from inventing an allocated parent when the baseline had no such fact.
        foreach (var procedure in verification.Where(x => x.Kind == "Procedure"))
        {
            var mappingEntry = mapping.Objects.SingleOrDefault(x => x.SourceKey == procedure.Key);
            if (mappingEntry?.Include != true || mappingEntry.Level is RequirementLevel.System) continue;
            if (!nativeRelationships.Any(x => x.RelationshipKind == "CaseProcedure" && x.TargetEndpointKey == procedure.Key))
                errors.Add($"Procedure '{procedure.Key}' requires an exact included CaseProcedure parent relation.");
        }
        if (errors.Count == 0)
        {
            var combined = JsonSerializer.Serialize(new { baseManifest = result.ManifestHash,
                verification = verificationRows.OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                relationships = nativeRelationships.OrderBy(x => x.SourceKey, StringComparer.Ordinal),
                sourceFacts = sourceFactRows.OrderBy(x => x.SourceKey, StringComparer.Ordinal) }, JsonOptions);
            var manifest = Sha256(Encoding.UTF8.GetBytes(combined));
            return new(true, source.Objects.Count, result.IncludedObjects + verificationRows.Count,
                result.ExcludedObjects + verificationExcluded + sourceFactExcluded + unsupported.Length, source.Relations.Count,
                result.IncludedRelations + nativeRelationships.Count,
                result.ExcludedRelations + nativeRelationshipExcluded, [], result.Requirements, result.Traces, manifest,
                verificationRows, nativeRelationships, sourceFactRows);
        }
        return new(false, source.Objects.Count, result.IncludedObjects + verificationRows.Count,
            result.ExcludedObjects + verificationExcluded + sourceFactExcluded + unsupported.Length, source.Relations.Count,
            result.IncludedRelations + nativeRelationships.Count, result.ExcludedRelations + nativeRelationshipExcluded,
            errors, result.Requirements, result.Traces, null, verificationRows, nativeRelationships, sourceFactRows);
    }

    private static ProjectCreationSourceAnalysis ReadAnalysis(ProjectSetupSourcePackage package) =>
        Deserialize<ProjectCreationSourceAnalysis>(package.AnalysisJson, "source analysis");

    private static InceptionMapping ReadMapping(string json, ProjectCreationSourceAnalysis source) =>
        ParseMapping(json, source);

    private static InceptionMapping ParseMapping(string json, ProjectCreationSourceAnalysis source)
    {
        try
        {
            var wire = JsonSerializer.Deserialize<InceptionMappingWire>(json, JsonOptions)
                ?? throw new ProjectSetupInvalidException("Source mapping is required.");
            var objects = wire.Objects ?? wire.Modules?.SelectMany(module => module.Objects ?? []).ToList() ?? [];
            var relations = wire.Relations ?? [];
            var mapping = new InceptionMapping(wire.SourceSha256 ?? source.Sha256, objects.Select(ToObject).ToArray(),
                relations.Select(ToRelation).ToArray(), wire.FindingResolutions ?? new Dictionary<string, string>());
            return mapping;
        }
        catch (JsonException ex) { throw new ProjectSetupInvalidException("Source mapping is invalid typed JSON.", ex); }
    }

    private static InceptionObjectMapping ToObject(InceptionObjectWire item)
    {
        RequirementLevel? level = null;
        if (!string.IsNullOrWhiteSpace(item.Level)
            && Enum.TryParse<RequirementLevel>(item.Level, false, out var parsed)) level = parsed;
        return new(item.SourceKey ?? "", item.Include, item.ExclusionReason, level,
            (item.Attributes ?? []).Select(x => new InceptionAttributeMapping(x.SourceAttribute ?? "",
                x.Destination, x.Reason, x.ValueMappings)).ToArray());
    }
    private static InceptionRelationMapping ToRelation(InceptionRelationWire item) =>
        new(item.SourceKey ?? "", item.Include, item.ExclusionReason, item.Type,
            item.SourceIsParent, item.RelationshipKind);

    private static string[] ParseCategories(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
            if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
                throw new ProjectSetupInvalidException("Selected source categories cannot be duplicated.");
            return result;
        }
        catch (JsonException ex) { throw new ProjectSetupInvalidException("Selected source categories are invalid typed JSON.", ex); }
    }

    private static void ValidateCategories(ProjectSetupSourcePackage package, IReadOnlyList<string> categories)
    {
        var allowed = package.Kind == ProjectSetupSourceKind.AeroLinkBaseline ? NativeCategories
            : package.Format.Equals("REQIF", StringComparison.OrdinalIgnoreCase) || package.Format.Equals("REQIFZ", StringComparison.OrdinalIgnoreCase)
                ? ReqIfCategories : TabularCategories;
        if (categories.Any(x => !allowed.Contains(x)))
            throw new ProjectSetupInvalidException($"The selected source format supports only: {string.Join(", ", allowed.OrderBy(x => x))}.");
    }

    private static void ValidateCategorySelections(ProjectCreationSourceAnalysis source, InceptionMapping mapping,
        IReadOnlyList<string> categories)
    {
        var selected = categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Contains("Requirements"))
            throw new ProjectSetupInvalidException("Requirements must remain selected as the destination project foundation.");
        foreach (var item in mapping.Objects.Where(x => x.Include))
        {
            var observed = source.Objects.SingleOrDefault(x => x.Key == item.SourceKey);
            if (observed is null) continue;
            var category = observed.Kind switch
            {
                "Requirement" or "Unmapped" or "" => "Requirements",
                "Case" => "Cases",
                "Procedure" => "Procedures",
                "Evidence" => "Evidence",
                _ => ""
            };
            if (category.Length > 0 && !selected.Contains(category))
                throw new ProjectSetupInvalidException($"Source object '{item.SourceKey}' is included although category '{category}' is not selected.");
        }
        foreach (var relation in source.Relations)
        {
            var mappingEntry = mapping.Relations.SingleOrDefault(x => x.SourceKey == relation.Key);
            if (mappingEntry?.Include != true) continue;
            var required = relation.Type switch
            {
                "CaseProcedure" => new[] { "Cases", "Procedures" },
                "VerificationCoverage" => new[] { "Requirements", "Procedures" },
                "EvidenceExecution" => new[] { "Evidence" },
                _ => new[] { "Requirements", "Traces" }
            };
            if (required.Any(x => !selected.Contains(x)))
                throw new ProjectSetupInvalidException($"Source relation '{relation.Key}' is included although its required categories are not selected.");
        }
    }

    private static ProjectSetupSourceView ToView(ProjectSetupSourcePackage package, ProjectCreationSourceAnalysis analysis,
        ProjectSetupDraft draft)
    {
        var selected = ParseCategories(package.SelectedCategoriesJson);
        var assertion = package.ManifestHash is null ? null : new ProjectSetupSourceAssertion(
            BuildAssertionDescription(package, draft, draft.ProjectId, draft.InceptionBaselineId, draft.InitialReleaseId,
                package.ManifestHash),
            BuildAssertionHash(package, draft, draft.ProjectId, draft.InceptionBaselineId, draft.InitialReleaseId, package.ManifestHash));
        var ladderSuggestion = SuggestLadder(analysis);
        return new(package.Id, package.Kind.ToString(), package.FileName, package.Format, package.Sha256,
            package.SourceBaselineId, package.SourceProjectId, package.SourceState, selected,
            analysis.Objects.GroupBy(x => x.Module, StringComparer.Ordinal).Select(group =>
                new ProjectSetupSourceModule(group.Key, group.Key, group.Count(), group.Select(x => x.Key).ToArray(),
                    group.Select(x => new ProjectSetupSourceObjectView(x.Key, x.Module, x.SourceIdentifier, x.Kind,
                        ClientSourceAttributes(x.Attributes))).ToArray())).ToArray(),
            analysis.Relations.Select(x => new ProjectSetupSourceRelationView(x.Key, x.SourceKey, x.TargetKey, x.Type,
                x.Attributes)).ToArray(),
            analysis.Findings, package.Stage.ToString(), package.ManifestHash,
            package.ReconciliationJson.Length == 0 ? null : Parse(package.ReconciliationJson), assertion,
            Parse(package.MappingJson), Parse(package.MetadataJson), ladderSuggestion);
    }

    private static ProjectSetupSourceLadderSuggestion SuggestLadder(ProjectCreationSourceAnalysis analysis)
    {
        var levels = analysis.Objects.Select(x => x.Attributes.GetValueOrDefault("Level", ""))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var supported = LegacyLadderPolicy.Instance.OrderedLevels
            .Where(level => levels.Any(value => string.Equals(value, level.ToString(), StringComparison.OrdinalIgnoreCase)))
            .Select(level => level.ToString()).ToArray();
        var findings = levels.Where(value => !supported.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Select(value => $"Source level '{value}' needs an explicit compatible mapping.").ToArray();
        var byKey = analysis.Objects.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var relationships = analysis.Relations
            .Where(x => byKey.ContainsKey(x.SourceKey) && byKey.ContainsKey(x.TargetKey))
            .Select(x => new ProjectSetupSourceSuggestedRelationship(x.Key, x.Type,
                byKey[x.SourceKey].Attributes.GetValueOrDefault("Level", ""),
                byKey[x.TargetKey].Attributes.GetValueOrDefault("Level", "")))
            .Where(x => x.SourceLevel.Length > 0 && x.TargetLevel.Length > 0
                && !string.Equals(x.SourceLevel, x.TargetLevel, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new(supported, relationships, findings);
    }

    private static IReadOnlyDictionary<string, string> ClientSourceAttributes(
        IReadOnlyDictionary<string, string> attributes) => attributes
        .Where(x => !string.Equals(x.Key, "StorageKey", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static JsonElement RedactStorageKeys(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            RedactStorageKeys(node);
            using var document = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static JsonElement ParseAndRedact(string json) => RedactStorageKeys(json);

    private static void RedactStorageKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach (var property in objectNode.ToArray())
                {
                    if (string.Equals(property.Key, "StorageKey", StringComparison.OrdinalIgnoreCase))
                        objectNode.Remove(property.Key);
                    else
                        RedactStorageKeys(property.Value);
                }
                break;
            case JsonArray arrayNode:
                foreach (var item in arrayNode) RedactStorageKeys(item);
                break;
        }
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone();
    }

    private static (string? Version, string? Name, DateTimeOffset? Date) ParseMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            DateTimeOffset? date = root.TryGetProperty("date", out var dateElement)
                && dateElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(dateElement.GetString(), out var parsed) ? parsed : null;
            return (Get(root, "version"), Get(root, "name"), date);
        }
        catch (JsonException) { return (null, null, null); }
    }
    private static string? Get(JsonElement root, string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static void EnsureVersion(ProjectSetupDraft draft, long expected) { if (draft.Version != expected) throw new ProjectSetupConcurrencyException("This setup changed; refresh and retry."); }
    private static void RequireAuthenticated(AuthenticatedUser actor) { if (actor.Id == Guid.Empty) throw new ProjectSetupAccessException(); }
    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static T Deserialize<T>(string json, string field)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new ProjectSetupInvalidException($"The {field} payload is empty."); }
        catch (JsonException ex) { throw new ProjectSetupInvalidException($"The {field} payload is invalid JSON.", ex); }
    }
    private static SourceAssertion BuildAssertion(ProjectSetupSourcePackage package, ProjectSetupDraft draft, ProjectRecord project,
        CandidateBaseline baseline, ILadderPolicy ladder, InceptionReconciliation reconciliation)
    {
        var hash = BuildAssertionHash(package, draft, project.Id, baseline.Id, baseline.ReleaseId, reconciliation.ManifestHash!);
        return new SourceAssertion(hash, BuildAssertionDescription(package, draft, project.Id, baseline.Id,
            baseline.ReleaseId, reconciliation.ManifestHash!));
    }

    private static string BuildAssertionDescription(ProjectSetupSourcePackage package, ProjectSetupDraft draft,
        Guid projectId, Guid baselineId, Guid releaseId, string manifestHash) =>
        $"Accepted source {package.Sha256} with reconciliation {manifestHash} for target project {projectId:D}, " +
        $"baseline {baselineId:D}, official build {draft.InitialReleaseCanonicalIdentity}, start kind {draft.StartKind?.ToString() ?? "Unknown"}, " +
        $"release {releaseId:D}.";

    private static string BuildAssertionHash(ProjectSetupSourcePackage package, ProjectSetupDraft draft,
        Guid projectId, Guid baselineId, Guid releaseId, string manifestHash)
    {
        var payload = JsonSerializer.Serialize(new { packageId = package.Id, package.Kind, package.Sha256,
            package.SourceBaselineId, packageManifestHash = package.ManifestHash, selectedCategories = package.SelectedCategoriesJson, mapping = package.MappingJson,
            ladderHash = ProjectLadderSnapshot.HashV2(ProjectSetupLadderFactory.Steps(draft.LadderJson),
                ProjectSetupLadderFactory.Relationships(draft.LadderJson), LegacyLadderPolicy.Instance),
            reconciliationManifestHash = manifestHash, projectId, baselineId, releaseId,
            officialBuildIdentity = draft.InitialReleaseCanonicalIdentity,
            startKind = draft.StartKind?.ToString() }, JsonOptions);
        return Sha256(Encoding.UTF8.GetBytes(payload));
    }

    private sealed record InceptionMappingWire(string? SourceSha256, List<InceptionObjectWire>? Objects,
        List<InceptionModuleWire>? Modules, List<InceptionRelationWire>? Relations,
        Dictionary<string, string>? FindingResolutions);
    private sealed record InceptionModuleWire(string Key, List<InceptionObjectWire>? Objects);
    private sealed record InceptionObjectWire(string? SourceKey, bool Include, string? ExclusionReason,
        string? Level, List<InceptionAttributeWire>? Attributes);
    private sealed record InceptionAttributeWire(string? SourceAttribute, InceptionAttributeDestination Destination,
        string? Reason, Dictionary<string, string>? ValueMappings);
    private sealed record InceptionRelationWire(string? SourceKey, bool Include, string? ExclusionReason,
        RequirementTraceType? Type, bool? SourceIsParent, string? RelationshipKind);
    private sealed record SourceAssertion(string Hash, string Meaning);
}

public sealed record InceptionConfigurationCommand(long ExpectedVersion, string SelectedCategoriesJson,
    string MappingJson, string MetadataJson = "{}");
public sealed record ProjectSetupSourceMutationResult(ProjectSetupSourcePackage Package, long DraftVersion);
public sealed record ProjectSetupSourceReconciliationResult(InceptionReconciliation Reconciliation, long DraftVersion);
public sealed record ProjectSetupSourceOption(Guid BaselineId, Guid ProjectId, string ProjectName, string Name,
    string DisplayNumber, string State, int RequirementsCount, int CasesCount, int ProceduresCount, int EvidenceCount);
public sealed record ProjectSetupSourceObjectView(string Key, string Module, string SourceIdentifier, string Kind,
    IReadOnlyDictionary<string, string> Attributes);
public sealed record ProjectSetupSourceModule(string Key, string Name, int ObjectCount, IReadOnlyList<string> ObjectKeys,
    IReadOnlyList<ProjectSetupSourceObjectView>? Objects = null);
public sealed record ProjectSetupSourceRelationView(string Key, string SourceKey, string TargetKey, string Type,
    IReadOnlyDictionary<string, string>? Attributes = null);
public sealed record ProjectSetupSourceView(Guid Id, string Kind, string FileName, string Format, string Sha256,
    Guid? SourceBaselineId, Guid? SourceProjectId, string? SourceState, IReadOnlyList<string> SelectedCategories,
    IReadOnlyList<ProjectSetupSourceModule> Modules, IReadOnlyList<ProjectSetupSourceRelationView> Relations,
    IReadOnlyList<string> Findings, string Stage, string? ManifestHash, JsonElement? Reconciliation,
    ProjectSetupSourceAssertion? Assertion, JsonElement? Mapping = null, JsonElement? Metadata = null,
    ProjectSetupSourceLadderSuggestion? LadderSuggestion = null);
public sealed record ProjectSetupSourceAssertion(string Text, string Hash);
public sealed record ProjectInceptionSourceProjection(Guid ProjectId, ProjectInceptionSourcePackageView? Package,
    ProjectInceptionSourceAcceptance? Acceptance, IReadOnlyList<ProjectInceptionSourceRecordView> Records);
public sealed record ProjectInceptionSourcePackageView(Guid Id, string Kind, string FileName, string Format,
    string Sha256, long SizeBytes, string SourceTool, Guid? SourceBaselineId, Guid? SourceProjectId, string? SourceState,
    JsonElement SelectedCategories, JsonElement Mapping, JsonElement? Reconciliation, string? ManifestHash,
    string? AssertionHash, string CapturedBy, DateTimeOffset CapturedAt, Guid? MaterializedBaselineId,
    DateTimeOffset UpdatedAt);
public sealed record ProjectInceptionSourceAcceptance(Guid UserId, string UserName, string DisplayName,
    DateTimeOffset SignedAt, string Action, string Meaning, string ContentHash, string Authority, string Rationale);
public sealed record ProjectInceptionSourceRecordView(Guid Id, Guid PackageId, Guid BaselineId, string TargetKind,
    Guid TargetId, Guid? TargetRevisionId, string SourceKey, string SourceModule, string SourceIdentifier,
    string SourceRevision, string SourceState, JsonElement SourceSnapshot, DateTimeOffset CreatedAt);
public sealed record ProjectSetupSourceLadderSuggestion(IReadOnlyList<string> Levels,
    IReadOnlyList<ProjectSetupSourceSuggestedRelationship> Relationships, IReadOnlyList<string> Findings);
public sealed record ProjectSetupSourceSuggestedRelationship(string Key, string Type, string SourceLevel,
    string TargetLevel);

internal static class ProjectSetupLadderFactory
{
    public static ProjectLadderConfiguration FromJson(Guid projectId, string json, DateTimeOffset now)
    {
        var steps = Steps(json); var relationships = Relationships(json);
        var (validatedSteps, validatedRelationships) = ProjectLadderDraftValidator.Validate(steps, relationships, LegacyLadderPolicy.Instance);
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var byEntry = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in validatedSteps)
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var entity = new ProjectLadderStep(configuration.Id, projectId, level, step.Position, step.Capabilities, now, step.EnabledArtifactKinds);
            configuration.Steps.Add(entity); byEntry.Add(step.CatalogueEntry, entity);
        }
        foreach (var edge in validatedRelationships)
            configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
                byEntry[edge.Parent].Id, byEntry[edge.Child].Id, now));
        return configuration;
    }
    public static IReadOnlyList<LadderStepDraft> Steps(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return LegacyLadderPolicy.Instance.OrderedLevels.Select((level, i) => new LadderStepDraft(level.ToString(), i + 1,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities,
                LegacyLadderPolicy.Instance.Definition(level).VerificationProfile?.EnabledKinds)).ToArray();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ProjectSetupInvalidException("A reviewed ladder must provide typed steps.");
        return JsonSerializer.Deserialize<List<LadderStepDraft>>(list.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } }) ?? throw new ProjectSetupInvalidException("A reviewed ladder must provide typed steps.");
    }
    public static IReadOnlyList<LadderRelationshipDraft> Relationships(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return [new("System", "HighLevel"), new("HighLevel", "LowLevel")];
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("relationships", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return JsonSerializer.Deserialize<List<LadderRelationshipDraft>>(list.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }
}
