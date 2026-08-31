using System.Text.Json;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Generates a Problem Report publication from either the live record or one immutable lifecycle snapshot.
/// The snapshot is selected explicitly for historical output; its stored hash is checked before any rich
/// content is resolved, so a download can never quietly become today's report under yesterday's filename.
/// </summary>
public sealed class ProblemReportOutputGenerator(AeroLinkDbContext db, RichContentPublisher richContent)
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<GeneratedOutput?> GenerateAsync(Guid problemReportId, int? revision, string format,
        CancellationToken ct)
    {
        if (!format.Equals("docx", StringComparison.OrdinalIgnoreCase)
            && !format.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return null;

        var report = await db.ProblemReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == problemReportId, ct);
        if (report is null) return null;

        var selected = await SelectSnapshotAsync(report, revision, ct);
        if (selected is null) return null;
        var (snapshot, snapshotJson, snapshotHash, snapshotSchema, frozen) = selected.Value;
        if (snapshot.Id != report.Id || snapshot.ProjectId != report.ProjectId
            || snapshot.Revision != (revision ?? report.Revision)) return null;

        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == snapshot.ProjectId, ct);
        if (project is null) return null;
        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == project.ProgramId, ct);
        if (program is null) return null;

        var release = snapshot.TargetReleaseId is { } releaseId
            ? await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == snapshot.ProjectId, ct)
            : null;
        if (snapshot.TargetReleaseId is not null && release is null) return null;

        var richValues = RichValues(snapshot);
        // A frozen snapshot is allowed to read the immutable bytes of an attachment that was subsequently
        // withdrawn. Current output retains the normal withdrawn guard; a future attachment-removal workflow
        // therefore cannot make already-checked-in PR output drift.
        var images = await richContent.ResolveImagesAsync(richValues, ct, includeWithdrawn: frozen);
        string Published(string value) => RichContentPublisher.ForPublication(value, images);
        var records = new List<PublicationRecord>
        {
            Record("Problem", "Problem statement", snapshot.Problem, snapshot.ProblemRich, Published),
            Record("Additional information", "Additional information", snapshot.AdditionalInformation, snapshot.AdditionalInformationRich, Published),
            Record("Analysis", "Analysis", snapshot.Analysis, snapshot.AnalysisRich, Published),
            Record("Root cause", "Root cause", snapshot.RootCause, snapshot.RootCauseRich, Published),
            Record("Effects", "Effects", snapshot.Effects, snapshot.EffectsRich, Published),
            Record("Containment", "Containment", snapshot.Containment, snapshot.ContainmentRich, Published),
            Record("Workaround", "Workaround", snapshot.Workaround, snapshot.WorkaroundRich, Published),
            Record("Corrective action", "Corrective action", snapshot.CorrectiveAction, snapshot.CorrectiveActionRich, Published),
            Record("System / aircraft impact", "System / aircraft impact", snapshot.SystemAircraftImpact, snapshot.SystemAircraftImpactRich, Published),
        };

        var revisions = await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.ProblemReportId == report.Id && (!revision.HasValue || x.Revision <= revision.Value))
            .ToListAsync(ct);
        var history = revisions.OrderBy(x => x.Revision).ThenBy(x => x.OccurredAt).Select(x => (
            Revision: x.Revision.ToString("D2"),
            Status: string.IsNullOrWhiteSpace(x.ToState) ? x.EventType : x.ToState,
            Date: x.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd"),
            Author: string.IsNullOrWhiteSpace(x.ActorDisplayName) ? x.Actor : x.ActorDisplayName!)).ToList();

        var metadata = new List<(string Label, string Value)>
        {
            ("Category", snapshot.Category ?? "Not classified"),
            ("Classification", snapshot.Classification),
            ("Severity", snapshot.Severity),
            ("Priority", snapshot.Priority),
            ("Origin", snapshot.Origin),
            ("Affected configuration", snapshot.AffectedConfiguration),
            ("Target build", release?.Version ?? "Unassigned"),
            ("Snapshot schema", snapshotSchema.ToString()),
            ("Snapshot SHA-256", snapshotHash),
        };
        var publication = new ProfessionalPublication(
            project.SoftwareProduct,
            program.Name + " (" + program.Code + ")",
            project.Name,
            "Problem Report",
            snapshot.Title,
            "Controlled problem statement, analysis, corrective action, and lifecycle history",
            snapshot.DisplayNumber,
            snapshot.Revision.ToString("D2"),
            snapshot.State,
            release?.Version ?? "Unassigned",
            "Project-scoped controlled record",
            snapshot.ReportedBy,
            snapshot.UpdatedAt,
            snapshotHash,
            metadata,
            [],
            history,
            [new PublicationSection("Problem Report Record", "Narrative fields retain their typed authored structure while controlled fields remain in Document Control.", records)]);

        return ProfessionalPublicationRenderer.Render(publication, format,
            SafeFileName(snapshot.DisplayNumber + "_" + snapshot.Title));
    }

    private async Task<(ProblemReportEvidenceSnapshot Snapshot, string Json, string Hash, int Schema, bool Frozen)?>
        SelectSnapshotAsync(ProblemReport report, int? revision, CancellationToken ct)
    {
        if (revision is null)
        {
            var json = ProblemReportEvidenceContract.Serialize(report);
            return (ProblemReportEvidenceContract.Create(report), json,
                ProblemReportEvidenceContract.Hash(json), ProblemReportEvidenceContract.SchemaVersion, false);
        }

        // SQLite (used by the hosted API contract tests) cannot order DateTimeOffset in SQL. Read only the
        // immutable candidate rows, then order their captured event times in memory; no current record is
        // consulted to choose the historical snapshot.
        var rows = await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.ProblemReportId == report.Id && x.Revision == revision.Value)
            .Select(x => new { x.SnapshotJson, x.SnapshotHash, x.SnapshotSchemaVersion, x.OccurredAt })
            .ToListAsync(ct);
        var row = rows.OrderByDescending(x => x.OccurredAt).FirstOrDefault();
        if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson)
            || !string.Equals(ProblemReportEvidenceContract.Hash(row.SnapshotJson), row.SnapshotHash, StringComparison.OrdinalIgnoreCase)) return null;
        ProblemReportEvidenceSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<ProblemReportEvidenceSnapshot>(row.SnapshotJson, SnapshotOptions); }
        catch (JsonException) { return null; }
        if (snapshot is null || !string.Equals(snapshot.Contract, ProblemReportEvidenceContract.Contract, StringComparison.Ordinal)
            || snapshot.SchemaVersion != row.SnapshotSchemaVersion
            || snapshot.SchemaVersion is not (4 or ProblemReportEvidenceContract.SchemaVersion)) return null;
        return (snapshot, row.SnapshotJson, row.SnapshotHash, row.SnapshotSchemaVersion, true);
    }

    private static PublicationRecord Record(string number, string title, string plain, string rich,
        Func<string, string> publish) => new(number, "Narrative", title, plain ?? "", [], publish(rich));

    private static string[] RichValues(ProblemReportEvidenceSnapshot snapshot) =>
    [snapshot.ProblemRich, snapshot.AdditionalInformationRich, snapshot.AnalysisRich, snapshot.RootCauseRich,
        snapshot.EffectsRich, snapshot.ContainmentRich, snapshot.WorkaroundRich, snapshot.CorrectiveActionRich,
        snapshot.SystemAircraftImpactRich];

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim();
        return safe.Length > 60 ? safe[..60].Trim() : safe;
    }
}
