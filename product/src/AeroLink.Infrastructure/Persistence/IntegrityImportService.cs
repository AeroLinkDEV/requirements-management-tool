using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record IntegrityOutcome(string Path, string Kind, string Outcome, string? SourceName = null);
public sealed record IntegrityImportRow(string SourceKey, string ItemPath, string Action, string Reason,
    Guid? ExistingReportId, string? ExistingReport, string Title, string Problem, string Analysis,
    string RootCause, string CorrectiveAction, string LandingState, string Severity, string Priority,
    string? Category, Guid? TargetBuildId, string RaisedBy, string ResponsibleEngineer,
    string SourceReportedBy, string SourceState, IntegritySourceDate SourceDate, string[] Findings,
    IntegrityOutcome[] Outcomes);
public sealed record IntegrityImportPreview(string ManifestHash, string PreviewHash, string SourceName, string EvidenceMode,
    string[] Fields, IReadOnlyDictionary<string, string[]> DistinctValues, IntegrityImportRow[] Rows,
    int Create, int Skip, int Blocked, IntegrityOutcome[] PackageOutcomes,
    IReadOnlyDictionary<Guid, string>? Images = null);
public sealed record IntegrityCreatedReport(Guid Id, string DisplayNumber, string SourceKey);
public sealed record IntegrityImportReceipt(Guid BatchId, string ManifestHash, string PreviewHash, int Created, int Skipped,
    IntegrityCreatedReport[] Reports);
public sealed class IntegrityImportConflict(string message) : InvalidOperationException(message);

/// <summary>#1186: package validation, exact reconciliation, atomic acceptance and recoverable storage.</summary>
public sealed class IntegrityImportService(AeroLinkDbContext db, EvidenceFileStore files, ManagedDocumentStorageCoordinator storage)
{
    public const string PackageArtifact = "IntegrityImportPackage";
    public const string ImageArtifact = "IntegritySourceImage";
    private static readonly string[] NativeFields = ["title", "problem", "status", "severity", "priority", "category",
        "reportedBy", "responsibleEngineer", "createdAt", "targetBuild", "analysis", "rootCause", "correctiveAction"];
    private static string SystemName(IntegritySourcePackage package) => $"Integrity:{package.Manifest.SourceInstanceId:D}";

    public async Task<IntegrityImportPreview> PreviewAsync(Guid projectId, IntegritySourcePackage package,
        ProblemReportImportMapping mapping, string actor, CancellationToken ct)
    {
        ValidateMapping(mapping);
        var accounts = await db.UserAccounts.AsNoTracking().Select(x => x.UserName).ToListAsync(ct);
        var builds = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => new { x.Id, x.Version }).ToListAsync(ct);
        var system = SystemName(package);
        var existing = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId && x.SourceSystem == system)
            .Select(x => new { x.Id, x.SourceKey, x.ReportNumber, x.Revision }).ToListAsync(ct);
        var rows = new List<IntegrityImportRow>();
        var images = new Dictionary<Guid, string>();
        long imageBytes = 0;
        var distinct = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "status", "severity", "priority", "category", "reportedBy", "responsibleEngineer", "targetBuild" })
            distinct[name] = package.Items.Select(x => Cell(x.Item, mapping, name)).Where(x => x.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var (path, item) in package.Items)
        {
            var findings = new List<string>(); var errors = new List<string>();
            string Content(string field)
            {
                var sourceField = Field(item, mapping, field);
                if (sourceField is null) return "";
                var rendered = IntegrityRichContent.Convert(Value(sourceField), sourceField.Representation, item,
                    image =>
                    {
                        var id = ImageId(projectId, package.ManifestHash, item.Id, image.Path);
                        var bytes = package.Members[image.Path];
                        IntegrityRichContent.ValidateImage(bytes, image.ContentType);
                        if (!images.ContainsKey(id))
                        {
                            imageBytes += bytes.Length;
                            if (imageBytes > 8 * 1024 * 1024) throw new DomainException("Preview images exceed 8 MB. Reduce the accepted package scope.");
                            images[id] = $"data:{image.ContentType};base64,{Convert.ToBase64String(bytes)}";
                        }
                        return id;
                    });
                findings.AddRange(rendered.Findings.Select(x => field + ": " + x));
                if (RichContent.ToPlainText(rendered.Content).Length > 8000) errors.Add($"The {field} content exceeds 8000 characters.");
                return rendered.Content;
            }
            var title = Cell(item, mapping, "title");
            var problem = Content("problem"); var analysis = Content("analysis");
            var rootCause = Content("rootCause"); var corrective = Content("correctiveAction");
            if (title.Length is 0 or > 300) errors.Add("Choose a title of 1–300 characters.");
            if (RichContent.ToPlainText(problem).Length == 0) errors.Add("Map a non-empty problem statement.");
            var state = Cell(item, mapping, "status");
            var landing = state.Length == 0 ? "Draft" : mapping.Statuses.GetValueOrDefault(state) ?? "";
            if (landing is not ("Draft" or "ReadyForSccb" or "Open" or "Implementing" or "Verifying" or "ClosedInSource"))
                errors.Add("Map this source status to a supported landing state; exclusions are not enabled.");
            string EnumValue<T>(string field, Dictionary<string, string> choices, string fallback) where T : struct, Enum
            {
                var source = Cell(item, mapping, field);
                var value = source.Length == 0 ? fallback : choices.GetValueOrDefault(source) ?? source;
                if (value.Length == 0) return "";
                if (!Enum.TryParse<T>(value, false, out var parsed) || !Enum.IsDefined(parsed)) { errors.Add($"Map the {field} value."); return fallback; }
                return parsed.ToString();
            }
            var severity = EnumValue<ProblemReportSeverity>("severity", mapping.Severities, "Major");
            var priority = EnumValue<ProblemReportPriority>("priority", mapping.Priorities, "Normal");
            var category = EnumValue<ProblemReportCategory>("category", mapping.Categories, "");
            if (category.Length == 0 && landing != "Draft") errors.Add("A category is required beyond Draft.");
            string? Person(string field)
            {
                var name = Cell(item, mapping, field);
                return mapping.People.TryGetValue(name, out var user)
                    ? accounts.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x, user)) : null;
            }
            var raisedBy = Person("reportedBy") ?? actor;
            var responsible = Person("responsibleEngineer") ?? (landing == "ClosedInSource" ? actor : "");
            if (responsible.Length == 0) errors.Add("Map the responsible engineer to an AeroLink account.");
            var sourceBuild = Cell(item, mapping, "targetBuild"); Guid? target = null;
            if (sourceBuild.Length > 0)
            {
                var selected = mapping.Builds.GetValueOrDefault(sourceBuild);
                if (selected != "Unassigned")
                {
                    if (Guid.TryParse(selected, out var id) && builds.Any(x => x.Id == id)) target = id;
                    else errors.Add("Map the source build to a destination build or Unassigned.");
                }
            }
            var date = IntegritySourcePackage.ReadDate(Cell(item, mapping, "createdAt", trim: false));
            if (Field(item, mapping, "createdAt") is { } dateField)
                date = date with { Representation = dateField.Type + "/" + dateField.Representation };
            if (date.Finding is not null) findings.Add(date.Finding);
            var matches = existing.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.SourceKey, item.Id)).ToArray();
            if (matches.Length > 1) throw new DomainException("Existing source identity is ambiguous; controlled correction is required.");
            var prior = matches.SingleOrDefault();
            var action = prior is not null ? "Skip" : errors.Count > 0 ? "Blocked" : "Create";
            var reason = prior is not null ? "already-imported" : string.Join(" ", errors);
            var outcomes = item.Fields.Select((f, i) => new IntegrityOutcome(path + "#/fields/" + i, "field",
                    action == "Create" && mapping.Columns.Where(x => NativeFields.Contains(x.Key) && (x.Key != "createdAt" || date.Instant is not null))
                        .Any(x => x.Value == f.Name) ? "native-and-preserved" : "preserved", f.Name))
                .Concat(item.History.EnumerateArray().Select((_, i) => new IntegrityOutcome(path + "#/history/" + i, "history", "preserved")))
                .Concat(item.Annotations.EnumerateArray().Select((_, i) => new IntegrityOutcome(path + "#/annotations/" + i, "annotation", "preserved")))
                .Concat(item.Relationships.EnumerateArray().Select((_, i) => new IntegrityOutcome(path + "#/relationships/" + i, "relationship", "source-reference")))
                .Concat(item.Attachments.Select(x => new IntegrityOutcome(x.Path, "attachment", "preserved"))).ToArray();
            rows.Add(new(item.Id, path, action, reason, prior?.Id, prior is null ? null : $"{prior.ReportNumber}.{prior.Revision:D2}",
                title, problem, analysis, rootCause, corrective, landing, severity, priority, category.Length == 0 ? null : category,
                target, raisedBy, responsible, Cell(item, mapping, "reportedBy"), state, date,
                findings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), outcomes));
        }
        var packageOutcomes = package.Manifest.Members.Select(x => new IntegrityOutcome(x.Path, x.Kind, "preserved")).ToArray();
        // Bind resolved accounts/build identities, interpretations and both Create/Skip directions, not display text alone.
        var hash = IntegritySourcePackage.Hash(Canonical(new { contract = 1, projectId, actor, package.ManifestHash,
            mapping, rows, packageOutcomes, builds = builds.OrderBy(x => x.Id) }));
        return new(package.ManifestHash, hash, package.Manifest.SourceName, package.Manifest.EvidenceMode,
            package.Items.SelectMany(x => x.Item.Fields.Select(f => f.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            distinct, rows.ToArray(), rows.Count(x => x.Action == "Create"), rows.Count(x => x.Action == "Skip"),
            rows.Count(x => x.Action == "Blocked"), packageOutcomes, images);
    }

    public async Task<IntegrityImportReceipt> CommitAsync(Guid projectId, Guid operationId, byte[] bytes,
        IntegritySourcePackage package, ProblemReportImportMapping mapping, string expectedPreviewHash,
        AuthenticatedUser actor, string remoteAddress, Func<ProjectControlledWriteScope, Task<bool>> authorized, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new DomainException("An import operation identity is required.");
        ValidateMapping(mapping);
        var requestHash = IntegritySourcePackage.Hash(Canonical(new { projectId, actor = actor.Id, package.ManifestHash, mapping, expectedPreviewHash }));
        var prior = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId
            && x.ActorId == actor.Id && x.OperationId == operationId, ct);
        if (prior is not null) return Receipt(prior, requestHash);
        var preview = await PreviewAsync(projectId, package, mapping, actor.UserName, ct);
        CheckPreview(preview, expectedPreviewHash);
        var now = DateTimeOffset.UtcNow;
        // The shared storage journal records staging before controlled publication and reconciles crash windows.
        var storageStart = await storage.BeginAsync(projectId, operationId, operationId, "IntegrityImport",
            actor.Id.ToString("N") + operationId.ToString("N"), requestHash, actor.UserName, now, ct);
        var operation = storageStart.Operation;
        if (storageStart.ExistingResult is not null)
        {
            prior = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId
                && x.ActorId == actor.Id && x.OperationId == operationId, ct);
            return prior is null ? throw new IntegrityImportConflict("The stored import receipt requires reconciliation.") : Receipt(prior, requestHash);
        }
        var objects = new List<(Guid Id, string Kind, string ItemId, StagedEvidence File)>();
        try
        {
            var archive = await files.StageAsync(new MemoryStream(bytes, false), operation.Id, "package", "integrity-source.zip", "application/zip", ct);
            objects.Add((Guid.NewGuid(), PackageArtifact, "", archive));
            foreach (var row in preview.Rows.Where(x => x.Action == "Create"))
            {
                var item = package.Items.Single(x => x.Item.Id == row.SourceKey).Item;
                var imageIds = new[] { row.Problem, row.Analysis, row.RootCause, row.CorrectiveAction }
                    .SelectMany(RichContent.Read).Where(x => x.Kind == RichBlockKind.Image).Select(x => x.AttachmentId!.Value).ToHashSet();
                foreach (var attachment in item.Attachments.Where(x => imageIds.Contains(ImageId(projectId, package.ManifestHash, item.Id, x.Path))
                    && PngImage.IsDeclaredImage(package.Members[x.Path], x.ContentType)).DistinctBy(x => x.Path))
                {
                    var imageId = ImageId(projectId, package.ManifestHash, item.Id, attachment.Path);
                    if (!imageIds.Contains(imageId)) continue;
                    var staged = await files.StageAsync(new MemoryStream(package.Members[attachment.Path], false), operation.Id,
                        imageId.ToString("N"), attachment.Name, attachment.ContentType, ct);
                    objects.Add((imageId, ImageArtifact, item.Id, staged));
                }
            }
            await storage.RecordPlanAsync(operation, objects.Select(x => new ManagedDocumentStagedObject(x.Id.ToString("N"), x.Id,
                x.File.StagingKey, x.File.StorageKey, x.File.Size, x.File.Sha256)).ToArray(), "{}", now, ct);
            await storage.PromoteAsync(operation, objects.Select(x => x.File), ct);
            await using var write = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
            if (!await authorized(write)) throw new DomainException("Import authority, session or project features changed. Review again.");
            prior = await db.IntegrityImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId
                && x.ActorId == actor.Id && x.OperationId == operationId, ct);
            if (prior is not null) throw new IntegrityImportConflict("This operation completed concurrently. Retry to retrieve its original receipt.");
            preview = await PreviewAsync(projectId, package, mapping, actor.UserName, ct);
            CheckPreview(preview, expectedPreviewHash);
            var batch = new IntegrityImportBatch(projectId, operationId, actor.Id, package.Manifest.SourceInstanceId,
                package.ManifestHash, requestHash, preview.PreviewHash, Canonical(mapping), Canonical(preview with { Images = null }), objects[0].Id, actor.UserName, now);
            var reports = new List<IntegrityCreatedReport>();
            foreach (var row in preview.Rows.Where(x => x.Action == "Create"))
            {
                var closed = row.LandingState == "ClosedInSource";
                var report = ProblemReport.Import(projectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), row.Title,
                    RichContent.ToPlainText(row.Problem), RichContent.ToPlainText(row.Analysis), row.RaisedBy, row.ResponsibleEngineer, now,
                    Enum.Parse<ProblemReportSeverity>(row.Severity), Enum.Parse<ProblemReportPriority>(row.Priority),
                    row.Category is null ? null : Enum.Parse<ProblemReportCategory>(row.Category), row.TargetBuildId,
                    SystemName(package), row.SourceKey, row.SourceReportedBy, row.SourceDate.Instant, row.SourceState,
                    closed ? ProblemReportState.Closed : Enum.Parse<ProblemReportState>(row.LandingState), closed,
                    RichContent.ToPlainText(row.RootCause), RichContent.ToPlainText(row.CorrectiveAction), row.Problem,
                    new(AnalysisRich: row.Analysis, RootCauseRich: row.RootCause, CorrectiveActionRich: row.CorrectiveAction));
                db.ProblemReports.Add(report);
                db.IntegrityReportSources.Add(new(projectId, batch.Id, report.Id, package.Manifest.SourceInstanceId, row.SourceKey, row.ItemPath, Canonical(row.SourceDate)));
                if (row.TargetBuildId is { } target)
                    db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", target,
                        ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, actor.UserName, now));
                foreach (var image in objects.Where(x => x.ItemId == row.SourceKey))
                    db.ControlledAttachments.Add(Attachment(image.Id, ImageArtifact, report.Id, image.File));
                var evidence = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
                db.ProblemReportRevisions.Add(new(report.Id, report.Revision, "ImportedFromSource", actor.UserName, evidence.Hash,
                    evidence.Json, now, detail: $"Integrity {row.SourceKey}; source package {package.ManifestHash}; batch {batch.Id:D}",
                    toState: report.State.ToString(), actorDisplayName: actor.DisplayName));
                reports.Add(new(report.Id, report.DisplayNumber, row.SourceKey));
            }
            db.ControlledAttachments.Add(Attachment(objects[0].Id, PackageArtifact, batch.Id, objects[0].File));
            var receipt = new IntegrityImportReceipt(batch.Id, batch.ManifestHash, batch.PreviewHash, preview.Create, preview.Skip, reports.ToArray());
            batch.RecordReceipt(JsonSerializer.Serialize(receipt, IntegritySourcePackage.Json));
            db.IntegrityImportBatches.Add(batch);
            var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProgramId).SingleAsync(ct);
            db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "IntegrityImportBatch", batch.Id,
                package.Manifest.SourceName, "ImportIntegrityProblemReports", "Accepted the exact reviewed import result and preserved source assertion",
                batch.PreviewHash, remoteAddress, now));
            await storage.CompleteAsync(operation, now, ct);
            await write.CommitAsync(ct);
            return receipt;

            ControlledAttachment Attachment(Guid id, string type, Guid artifactId, StagedEvidence file) => new(projectId, type, artifactId,
                null, id, 1, file.OriginalFileName, "Preserved Integrity content; source attribution remains in the signed package.",
                file.OriginalFileName, file.ContentType, file.Size, file.Sha256, file.StorageKey, null, actor.UserName, now, reservedId: id);
        }
        catch
        {
            // Do not delete promoted evidence after an uncertain COMMIT. The journal checks durable references.
            db.ChangeTracker.Clear();
            var tracked = await db.ManagedDocumentStorageOperations.SingleAsync(x => x.Id == operation.Id, CancellationToken.None);
            await storage.ReconcileAbandonedOperationAsync(tracked, actor.UserName, DateTimeOffset.UtcNow, CancellationToken.None);
            throw;
        }
    }

    public static Guid ImageId(Guid project, string manifest, string item, string path) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"integrity-image-v1\n{project:D}\n{manifest}\n{item}\n{path}"))[..16]);
    private static IntegrityImportReceipt Receipt(IntegrityImportBatch batch, string requestHash) => batch.RequestHash != requestHash
        ? throw new IntegrityImportConflict("This operation identity was already used with different content.")
        : JsonSerializer.Deserialize<IntegrityImportReceipt>(batch.ReceiptJson, IntegritySourcePackage.Json)!;
    private static void CheckPreview(IntegrityImportPreview preview, string expected)
    {
        if (preview.PreviewHash != expected) throw new IntegrityImportConflict("The import result changed. Review the new preview before confirming.");
        if (preview.Blocked != 0) throw new DomainException("Resolve every blocked report before importing; no item may be silently excluded.");
    }
    public static string Canonical(object value)
    {
        using var source = JsonDocument.Parse(JsonSerializer.Serialize(value, IntegritySourcePackage.Json));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            void Write(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value); }
                    writer.WriteEndObject();
                }
                else if (element.ValueKind == JsonValueKind.Array)
                { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item); writer.WriteEndArray(); }
                else element.WriteTo(writer);
            }
            Write(source.RootElement);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static IntegrityField? Field(IntegritySourceItem item, ProblemReportImportMapping mapping, string field) =>
        mapping.Columns.TryGetValue(field, out var name) ? item.Fields.SingleOrDefault(x => x.Name == name) : null;
    private static string Cell(IntegritySourceItem item, ProblemReportImportMapping mapping, string field, bool trim = true)
    {
        var value = Field(item, mapping, field) is { } found ? Value(found) : "";
        return trim ? value.Trim() : value;
    }
    private static string Value(IntegrityField field) => field.Value.ValueKind switch
    { JsonValueKind.String => field.Value.GetString()!, JsonValueKind.Null => "", _ => field.Value.GetRawText() };
    private static void ValidateMapping(ProblemReportImportMapping mapping)
    {
        if (new[] { mapping.Columns, mapping.Statuses, mapping.Severities, mapping.Priorities, mapping.Categories, mapping.People, mapping.Builds }
            .Any(x => x is null || x.Count > 2000 || x.Any(y => y.Key.Length > 400 || y.Value is null || y.Value.Length > 400)))
            throw new DomainException("The import mapping is invalid or exceeds its limit.");
        if (mapping.Columns.Keys.Any(x => !NativeFields.Contains(x))) throw new DomainException("The mapping names an unsupported destination field.");
    }
}
