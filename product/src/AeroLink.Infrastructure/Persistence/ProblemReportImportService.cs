using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>How the importer maps a source export onto Problem Reports (#1114). Column values are header names.</summary>
public sealed record ProblemReportImportMapping
{
    public string SourceSystem { get; init; } = "";
    public Dictionary<string, string> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Source status → Draft, ReadyForSccb, Open, Implementing, Verifying, ClosedInSource or Skip.</summary>
    public Dictionary<string, string> Statuses { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Severities { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Priorities { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Source person → AeroLink user name.</summary>
    public Dictionary<string, string> People { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Source version → AeroLink build id, or "Unassigned".</summary>
    public Dictionary<string, string> Builds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON binding replaces the dictionaries with case-sensitive ones; every read goes through this.</summary>
    public ProblemReportImportMapping Normalized()
    {
        static Dictionary<string, string> Fold(Dictionary<string, string>? values) =>
            new((values ?? []).Where(x => x.Key is not null && x.Value is not null)
                .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last().Value.Trim()),
                StringComparer.OrdinalIgnoreCase);
        return new() { SourceSystem = (SourceSystem ?? "").Trim(), Columns = Fold(Columns), Statuses = Fold(Statuses), Severities = Fold(Severities),
            Priorities = Fold(Priorities), Categories = Fold(Categories), People = Fold(People), Builds = Fold(Builds) };
    }
}

public sealed record ProblemReportImportRow(int Row, string SourceKey, string Title, string Action, string? Reason,
    string? LandingState, string? Severity, string? RaisedBy, string? ResponsibleEngineer, string? ExistingReport);

public sealed record ProblemReportImportPreview(string SourceHash, string PreviewHash, IReadOnlyList<string> Headers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DistinctValues, IReadOnlyList<ProblemReportImportRow> Rows,
    int Create, int Skip);

/// <summary>
/// Brings Problem Reports in from another tool's CSV/XLSX export (#1114). Preview and commit run the same
/// reconciliation, so every source row ends as exactly one Create or Skip-with-reason and nothing is dropped
/// silently; commit refuses unless the file and mapping still hash to the previewed result. Source identity,
/// reporter, date and status stay source facts; a report the source had closed arrives Closed-in-source with
/// no AeroLink SQA closure. A source key already imported into the project is skipped.
/// </summary>
public sealed class ProblemReportImportService(AeroLinkDbContext db)
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    private static readonly string[] MappedFields = ["sourceKey", "title", "problem", "status", "severity", "priority",
        "category", "reportedBy", "responsibleEngineer", "createdAt", "targetBuild", "analysis", "rootCause", "correctiveAction"];
    private static readonly string[] ValueMappedFields = ["status", "severity", "priority", "category", "reportedBy", "responsibleEngineer", "targetBuild"];

    public static IReadOnlyList<string[]> ReadTable(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0) throw new DomainException("The file is empty.");
        if (bytes.Length > MaximumBytes) throw new DomainException("Problem Report imports are limited to 20 MB.");
        var tables = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? InceptionTableReader.Xlsx(new MemoryStream(bytes, false))
            : fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? InceptionTableReader.Csv(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'))
                : throw new DomainException("Import a .csv or .xlsx file.");
        var rows = tables.FirstOrDefault()?.Rows ?? [];
        if (rows.Count < 2) throw new DomainException("The file needs a header row and at least one report.");
        return rows;
    }

    public async Task<ProblemReportImportPreview> PreviewAsync(Guid projectId, byte[] bytes, string fileName,
        ProblemReportImportMapping mapping, string importer, CancellationToken ct)
    {
        mapping = mapping.Normalized();
        var table = ReadTable(bytes, fileName);
        var headers = table[0].Select(x => x.Trim()).ToArray();
        string Cell(string[] row, string field)
        {
            if (!mapping.Columns.TryGetValue(field, out var header) || string.IsNullOrWhiteSpace(header)) return "";
            var index = Array.FindIndex(headers, x => string.Equals(x, header.Trim(), StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index < row.Length ? row[index].Trim() : "";
        }
        var distinct = ValueMappedFields.Where(field => mapping.Columns.ContainsKey(field)).ToDictionary(field => field,
            field => (IReadOnlyList<string>)table.Skip(1).Select(row => Cell(row, field)).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(200).ToList());

        var system = mapping.SourceSystem.Trim();
        var existing = system.Length == 0 ? new Dictionary<string, string>() : (await db.ProblemReports.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.SourceSystem == system && x.SourceKey != null)
                .Select(x => new { x.SourceKey, x.ReportNumber, x.Revision }).ToListAsync(ct))
            .GroupBy(x => x.SourceKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => $"{x.First().ReportNumber}.{x.First().Revision:D2}", StringComparer.OrdinalIgnoreCase);
        var accounts = (await db.UserAccounts.AsNoTracking().Select(x => x.UserName).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builds = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProblemReportImportRow>();
        for (var index = 1; index < table.Count; index++)
        {
            var row = table[index];
            var key = Cell(row, "sourceKey"); var title = Cell(row, "title");
            ProblemReportImportRow Skip(string reason, string? existingReport = null) =>
                new(index + 1, key, title, "Skip", reason, null, null, null, null, existingReport);
            if (row.All(string.IsNullOrWhiteSpace)) { rows.Add(Skip("Empty row.")); continue; }
            var decision = Decide(row, key, title);
            rows.Add(decision);

            ProblemReportImportRow Decide(string[] source, string sourceKey, string sourceTitle)
            {
                if (system.Length == 0) return Skip("Name the source system.");
                if (sourceKey.Length == 0) return Skip("No source key.");
                if (!seen.Add(sourceKey)) return Skip("The same source key appears earlier in this file.");
                if (existing.TryGetValue(sourceKey, out var already)) return Skip($"Already imported as {already}.", already);
                if (sourceTitle.Length == 0) return Skip("No title.");
                if (Cell(source, "problem").Length == 0) return Skip("No problem statement.");
                if (sourceTitle.Length > 300) return Skip("The title is longer than 300 characters.");
                if (Cell(source, "problem").Length > 8000) return Skip("The problem statement is longer than 8000 characters.");
                if (sourceKey.Length > 200) return Skip("The source key is longer than 200 characters.");
                foreach (var field in new[] { "analysis", "rootCause", "correctiveAction" })
                    if (Cell(source, field).Length > 8000) return Skip($"The {field} text is longer than 8000 characters.");
                var status = Cell(source, "status");
                var landing = status.Length == 0 ? "Draft" : mapping.Statuses.GetValueOrDefault(status) ?? "";
                if (landing.Length == 0) return Skip($"Map the source status \"{status}\".");
                if (landing.Equals("Skip", StringComparison.OrdinalIgnoreCase)) return Skip($"Status \"{status}\" is mapped to skip.");
                var closed = landing.Equals("ClosedInSource", StringComparison.OrdinalIgnoreCase);
                if (!closed && !Enum.TryParse<ProblemReportState>(landing, true, out _)) return Skip($"\"{landing}\" is not a landing state.");
                if (!TryEnum<ProblemReportSeverity>(Cell(source, "severity"), mapping.Severities, ProblemReportSeverity.Major, out var severity))
                    return Skip($"Map the severity \"{Cell(source, "severity")}\".");
                if (!TryEnum<ProblemReportPriority>(Cell(source, "priority"), mapping.Priorities, ProblemReportPriority.Normal, out _))
                    return Skip($"Map the priority \"{Cell(source, "priority")}\".");
                var categoryText = Cell(source, "category");
                var category = categoryText.Length == 0 ? null : Category(categoryText);
                if (categoryText.Length > 0 && category is null) return Skip($"Map the category \"{categoryText}\".");
                if (category is null && !landing.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                    return Skip("A category is required for a report beyond Draft.");
                var build = Cell(source, "targetBuild");
                if (build.Length > 0)
                {
                    var target = mapping.Builds.GetValueOrDefault(build) ?? "";
                    if (target.Length == 0) return Skip($"Map the source version \"{build}\" to a build or Unassigned.");
                    if (!target.Equals("Unassigned", StringComparison.OrdinalIgnoreCase)
                        && !(Guid.TryParse(target, out var buildId) && builds.Contains(buildId)))
                        return Skip($"The build mapped for \"{build}\" is not in this project.");
                }
                var raisedBy = Person(Cell(source, "reportedBy")) ?? importer;
                var responsible = Person(Cell(source, "responsibleEngineer"));
                if (responsible is null && !closed)
                    return Skip("Map the responsible engineer to an AeroLink account: an open report needs one.");
                return new(index + 1, sourceKey, sourceTitle, "Create", null, closed ? "ClosedInSource" : Canonical(landing),
                    severity.ToString(), raisedBy, responsible ?? importer, null);
            }
        }

        string? Person(string sourceName)
        {
            if (sourceName.Length == 0) return null;
            var mapped = mapping.People.GetValueOrDefault(sourceName);
            return mapped is not null && accounts.Contains(mapped.Trim()) ? mapped.Trim().ToLowerInvariant() : null;
        }
        ProblemReportCategory? Category(string value)
        {
            var mapped = mapping.Categories.GetValueOrDefault(value) ?? value;
            return Enum.TryParse<ProblemReportCategory>(mapped, true, out var category) && Enum.IsDefined(category) ? category : null;
        }

        var sourceHash = Sha256(bytes);
        var previewHash = Sha256(Encoding.UTF8.GetBytes(sourceHash + "\n" + JsonSerializer.Serialize(Canonical(mapping))));
        return new(sourceHash, previewHash, headers, distinct, rows, rows.Count(x => x.Action == "Create"), rows.Count(x => x.Action == "Skip"));
    }

    /// <summary>Creates the previewed reports in one transaction and records the import ledger.</summary>
    public async Task<(ProblemReportImportBatch Batch, IReadOnlyList<ProblemReport> Reports)> CommitAsync(Guid projectId,
        byte[] bytes, string fileName, ProblemReportImportMapping mapping, string expectedPreviewHash, string importer,
        string importerDisplayName, DateTimeOffset now, CancellationToken ct)
    {
        mapping = mapping.Normalized();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var preview = await PreviewAsync(projectId, bytes, fileName, mapping, importer, ct);
        if (!string.Equals(preview.PreviewHash, expectedPreviewHash, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The file or mapping changed since the preview. Preview again before importing.");
        if (preview.Create == 0) throw new DomainException("Nothing in this preview would be created.");
        var table = ReadTable(bytes, fileName);
        var headers = preview.Headers.ToArray();
        string Cell(string[] row, string field)
        {
            if (!mapping.Columns.TryGetValue(field, out var header) || string.IsNullOrWhiteSpace(header)) return "";
            var index = Array.FindIndex(headers, x => string.Equals(x, header.Trim(), StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index < row.Length ? row[index].Trim() : "";
        }
        var batch = new ProblemReportImportBatch(projectId, mapping.SourceSystem, Path.GetFileName(fileName), preview.SourceHash,
            JsonSerializer.Serialize(Canonical(mapping)), preview.PreviewHash, preview.Create, preview.Skip, importer, now);
        db.ProblemReportImportBatches.Add(batch);
        var reports = new List<ProblemReport>();
        foreach (var plan in preview.Rows.Where(x => x.Action == "Create"))
        {
            var row = table[plan.Row - 1];
            var closed = plan.LandingState == "ClosedInSource";
            TryEnum<ProblemReportSeverity>(Cell(row, "severity"), mapping.Severities, ProblemReportSeverity.Major, out var severity);
            TryEnum<ProblemReportPriority>(Cell(row, "priority"), mapping.Priorities, ProblemReportPriority.Normal, out var priority);
            var categoryText = Cell(row, "category");
            ProblemReportCategory? category = categoryText.Length > 0
                && Enum.TryParse<ProblemReportCategory>(mapping.Categories.GetValueOrDefault(categoryText) ?? categoryText, true, out var parsed) ? parsed : null;
            var buildText = Cell(row, "targetBuild");
            Guid? target = buildText.Length > 0 && Guid.TryParse(mapping.Builds.GetValueOrDefault(buildText), out var buildId) ? buildId : null;
            var created = DateTimeOffset.TryParse(Cell(row, "createdAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var sourceDate) ? sourceDate : (DateTimeOffset?)null;
            var report = ProblemReport.Import(projectId, await IdentifierAllocator.NextProblemReportAsync(db, ct), plan.Title,
                Cell(row, "problem"), Cell(row, "analysis"), plan.RaisedBy!, plan.ResponsibleEngineer!, now, severity, priority,
                category, target, mapping.SourceSystem, plan.SourceKey, Cell(row, "reportedBy"), created, Cell(row, "status"),
                closed ? ProblemReportState.Closed : Enum.Parse<ProblemReportState>(plan.LandingState!), closed,
                Cell(row, "rootCause"), Cell(row, "correctiveAction"));
            db.ProblemReports.Add(report);
            if (target is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", target.Value,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, importer, now));
            var evidence = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision, "ImportedFromSource", importer,
                evidence.Hash, evidence.Json, now, detail: $"Imported from {mapping.SourceSystem.Trim()} {plan.SourceKey} (batch {batch.Id:D})",
                toState: report.State.ToString(), actorDisplayName: importerDisplayName));
            reports.Add(report);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (batch, reports);
    }

    private static bool TryEnum<T>(string value, Dictionary<string, string> map, T fallback, out T result) where T : struct, Enum
    {
        result = fallback;
        if (value.Length == 0) return true;
        var mapped = map.GetValueOrDefault(value) ?? value;
        return Enum.TryParse(mapped, true, out result) && Enum.IsDefined(result);
    }

    private static string Canonical(string landing) =>
        Enum.TryParse<ProblemReportState>(landing, true, out var state) ? state.ToString() : landing;

    private static object Canonical(ProblemReportImportMapping mapping)
    {
        static SortedDictionary<string, string> Sorted(Dictionary<string, string> values) =>
            new(values.Where(x => x.Value is not null).ToDictionary(x => x.Key.Trim().ToLowerInvariant(), x => x.Value.Trim()), StringComparer.Ordinal);
        return new
        {
            sourceSystem = mapping.SourceSystem.Trim(),
            columns = Sorted(mapping.Columns.Where(x => MappedFields.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value)),
            statuses = Sorted(mapping.Statuses), severities = Sorted(mapping.Severities), priorities = Sorted(mapping.Priorities),
            categories = Sorted(mapping.Categories), people = Sorted(mapping.People), builds = Sorted(mapping.Builds),
        };
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
