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

    public async Task<GeneratedOutput?> GenerateAsync(Guid problemReportId, int? revision, Guid? snapshotId, string format,
        CancellationToken ct)
    {
        if (!format.Equals("docx", StringComparison.OrdinalIgnoreCase)
            && !format.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return null;

        var report = await db.ProblemReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == problemReportId, ct);
        if (report is null) return null;

        var selected = await SelectSnapshotAsync(report, revision, snapshotId, ct);
        if (selected is null) return null;
        var (snapshot, snapshotJson, snapshotHash, snapshotSchema, frozen, legacyType,
            selectedSnapshotId, selectedOccurredAt) = selected.Value;
        if (snapshot.Id != report.Id || snapshot.ProjectId != report.ProjectId
            || snapshot.Revision != (frozen ? revision ?? snapshot.Revision : report.Revision)) return null;

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
        var images = await richContent.ResolveImagesAsync(richValues, snapshot.ProjectId, ct, includeWithdrawn: frozen);
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
            .Where(x => x.ProblemReportId == report.Id && (!frozen || x.Revision <= snapshot.Revision))
            .ToListAsync(ct);
        if (selectedSnapshotId is Guid exactId && selectedOccurredAt is DateTimeOffset exactTime)
            revisions = revisions.Where(x => x.Revision < snapshot.Revision
                    || (x.Revision == snapshot.Revision
                        && (x.OccurredAt < exactTime || (x.OccurredAt == exactTime && x.Id.CompareTo(exactId) <= 0))))
                .ToList();
        var history = revisions.OrderBy(x => x.Revision).ThenBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => (
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
        if (legacyType is not null)
            metadata.Insert(1, ("Legacy type", legacyType));
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
            [
                new PublicationSection("Problem Report Record", "Narrative fields retain their typed authored structure while controlled fields remain in Document Control.", records),
                new PublicationSection("Supporting Attachments", "Supporting files remain separate controlled objects. This manifest records the exact file versions and SHA-256 digests that belonged to this Problem Report snapshot.",
                    (snapshot.SupportingAttachments ?? []).Select((item, index) => new PublicationRecord(
                        $"ATT-{index + 1:D2}", "Supporting file", item.FileName,
                        $"{item.ContentType} · {item.Size} bytes · version {item.Version} · SHA-256 {item.Sha256}", [], "")).ToList()),
            ]);

        return ProfessionalPublicationRenderer.Render(publication, format,
            SafeFileName(snapshot.DisplayNumber + "_" + snapshot.Title));
    }

    private async Task<(ProblemReportEvidenceSnapshot Snapshot, string Json, string Hash, int Schema, bool Frozen,
        string? LegacyType, Guid? SnapshotId, DateTimeOffset? OccurredAt)?>
        SelectSnapshotAsync(ProblemReport report, int? revision, Guid? snapshotId, CancellationToken ct)
    {
        if (revision is null && snapshotId is null)
        {
            var attachments = await ProblemReportAttachmentEvidence.ActiveAsync(db, report.ProjectId, report.Id, ct);
            var json = ProblemReportEvidenceContract.Serialize(report, supportingAttachments: attachments);
            return (ProblemReportEvidenceContract.Create(report, supportingAttachments: attachments), json,
                ProblemReportEvidenceContract.Hash(json), ProblemReportEvidenceContract.SchemaVersion, false, null,
                null, null);
        }

        // SQLite (used by the hosted API contract tests) cannot order DateTimeOffset in SQL. Read only the
        // immutable candidate rows, then order their captured event times in memory; no current record is
        // consulted to choose the historical snapshot.
        var rows = await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.ProblemReportId == report.Id
                && (snapshotId.HasValue ? x.Id == snapshotId.Value : x.Revision == revision!.Value))
            .Select(x => new { x.Id, x.Revision, x.SnapshotJson, x.SnapshotHash, x.SnapshotSchemaVersion, x.OccurredAt })
            .ToListAsync(ct);
        var row = rows.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).FirstOrDefault();
        if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson)
            || (revision.HasValue && row.Revision != revision.Value)
            || !string.Equals(ProblemReportEvidenceContract.Hash(row.SnapshotJson), row.SnapshotHash, StringComparison.OrdinalIgnoreCase)) return null;
        var parsed = ReadStoredSnapshot(row.SnapshotJson, row.SnapshotSchemaVersion);
        return parsed is null || parsed.Value.Snapshot.Revision != row.Revision ? null
            : (parsed.Value.Snapshot, row.SnapshotJson, row.SnapshotHash, row.SnapshotSchemaVersion, true,
                parsed.Value.LegacyType, row.Id, row.OccurredAt);
    }

    /// <summary>
    /// Reads the immutable snapshot envelope in the schema in which it was written. This is deliberately a
    /// reader-only compatibility boundary: the original JSON and hash returned by SelectSnapshotAsync are
    /// never reserialized or replaced with today's aggregate. v1 was the closure-review envelope, v2 the
    /// original shared evidence envelope (with Type), v3 added Category, v4 added authored narrative fields,
    /// and v5 added authored image layout. Missing fields therefore render as "not recorded", never as a
    /// value borrowed from the current Problem Report.
    /// </summary>
    internal static (ProblemReportEvidenceSnapshot Snapshot, string? LegacyType)? ReadStoredSnapshot(string json, int expectedSchema)
    {
        StoredSnapshot? stored;
        try { stored = JsonSerializer.Deserialize<StoredSnapshot>(json, SnapshotOptions); }
        catch (JsonException) { return null; }
        if (stored is null || stored.SchemaVersion != expectedSchema) return null;

        var expectedContract = expectedSchema == 1
            ? "aerolink.problem-report-closure-review"
            : ProblemReportEvidenceContract.Contract;
        if (!string.Equals(stored.Contract, expectedContract, StringComparison.Ordinal)
            || expectedSchema is < 1 or > ProblemReportEvidenceContract.SchemaVersion)
            return null;

        var type = stored.Type;
        var snapshot = new ProblemReportEvidenceSnapshot
        {
            Contract = stored.Contract!,
            SchemaVersion = expectedSchema,
            Id = stored.Id,
            ProjectId = stored.ProjectId,
            ReportNumber = Text(stored.ReportNumber),
            Revision = stored.Revision,
            DisplayNumber = Text(stored.DisplayNumber, stored.ReportNumber),
            Title = Text(stored.Title),
            Problem = Text(stored.Problem),
            Analysis = Text(stored.Analysis),
            AnalysisRich = Text(stored.AnalysisRich, stored.Analysis),
            ReportedBy = Text(stored.ReportedBy),
            ResponsibleEngineerId = Text(stored.ResponsibleEngineerId),
            TargetReleaseId = stored.TargetReleaseId,
            ProblemRich = Text(stored.ProblemRich),
            AdditionalInformation = Text(stored.AdditionalInformation),
            AdditionalInformationRich = Text(stored.AdditionalInformationRich, stored.AdditionalInformation),
            SystemAircraftImpact = Text(stored.SystemAircraftImpact),
            SystemAircraftImpactRich = Text(stored.SystemAircraftImpactRich, stored.SystemAircraftImpact),
            Category = stored.Category,
            CategoryProvenance = stored.CategoryProvenance,
            Workaround = Text(stored.Workaround),
            WorkaroundRich = Text(stored.WorkaroundRich, stored.Workaround),
            ImpactAssessmentJson = Text(stored.ImpactAssessmentJson, "{}"),
            Classification = Text(stored.Classification),
            Severity = Text(stored.Severity),
            Priority = Text(stored.Priority),
            Origin = Text(stored.Origin),
            AffectedConfiguration = Text(stored.AffectedConfiguration),
            RootCause = Text(stored.RootCause),
            RootCauseRich = Text(stored.RootCauseRich, stored.RootCause),
            Effects = Text(stored.Effects),
            EffectsRich = Text(stored.EffectsRich, stored.Effects),
            Containment = Text(stored.Containment),
            ContainmentRich = Text(stored.ContainmentRich, stored.Containment),
            CorrectiveAction = Text(stored.CorrectiveAction),
            CorrectiveActionRich = Text(stored.CorrectiveActionRich, stored.CorrectiveAction),
            Disposition = stored.Disposition,
            DispositionRationale = Text(stored.DispositionRationale),
            ResolutionVerificationExecutionId = stored.ResolutionVerificationExecutionId,
            ClosureApprovedBy = stored.ClosureApprovedBy,
            ClosureApprovedByName = Text(stored.ClosureApprovedByName),
            ClosureApprovedAt = stored.ClosureApprovedAt,
            IsReleaseBlocker = stored.IsReleaseBlocker,
            ReleaseBlockerVersion = stored.ReleaseBlockerVersion,
            WaiverRationale = Text(stored.WaiverRationale),
            WaivedBy = Text(stored.WaivedBy),
            WaivedAt = stored.WaivedAt,
            State = Text(stored.State),
            CreatedAt = stored.CreatedAt ?? DateTimeOffset.MinValue,
            UpdatedAt = stored.UpdatedAt ?? stored.CreatedAt ?? DateTimeOffset.MinValue,
            Version = stored.Version,
            SupportingAttachments = expectedSchema >= 6 ? stored.SupportingAttachments ?? [] : null,
        };
        return (snapshot, type);
    }

    private static string Text(string? value, string? fallback = null) => value ?? fallback ?? "";

    // All members are optional because this is a reader for deployed historical envelopes. Do not add
    // defaults that source current aggregate state: an omitted historical field means it was not recorded.
    private sealed class StoredSnapshot
    {
        public string? Contract { get; set; }
        public int SchemaVersion { get; set; }
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string? ReportNumber { get; set; }
        public int Revision { get; set; }
        public string? DisplayNumber { get; set; }
        public string? Title { get; set; }
        public string? Problem { get; set; }
        public string? Analysis { get; set; }
        public string? AnalysisRich { get; set; }
        public string? ReportedBy { get; set; }
        public string? ResponsibleEngineerId { get; set; }
        public Guid? TargetReleaseId { get; set; }
        public string? ProblemRich { get; set; }
        public string? AdditionalInformation { get; set; }
        public string? AdditionalInformationRich { get; set; }
        public string? SystemAircraftImpact { get; set; }
        public string? SystemAircraftImpactRich { get; set; }
        public string? Type { get; set; }
        public string? Category { get; set; }
        public string? CategoryProvenance { get; set; }
        public string? Workaround { get; set; }
        public string? WorkaroundRich { get; set; }
        public string? ImpactAssessmentJson { get; set; }
        public string? Classification { get; set; }
        public string? Severity { get; set; }
        public string? Priority { get; set; }
        public string? Origin { get; set; }
        public string? AffectedConfiguration { get; set; }
        public string? RootCause { get; set; }
        public string? RootCauseRich { get; set; }
        public string? Effects { get; set; }
        public string? EffectsRich { get; set; }
        public string? Containment { get; set; }
        public string? ContainmentRich { get; set; }
        public string? CorrectiveAction { get; set; }
        public string? CorrectiveActionRich { get; set; }
        public string? Disposition { get; set; }
        public string? DispositionRationale { get; set; }
        public Guid? ResolutionVerificationExecutionId { get; set; }
        public Guid? ClosureApprovedBy { get; set; }
        public string? ClosureApprovedByName { get; set; }
        public DateTimeOffset? ClosureApprovedAt { get; set; }
        public bool IsReleaseBlocker { get; set; }
        public long ReleaseBlockerVersion { get; set; }
        public string? WaiverRationale { get; set; }
        public string? WaivedBy { get; set; }
        public DateTimeOffset? WaivedAt { get; set; }
        public string? State { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public long Version { get; set; }
        public List<ProblemReportSupportingAttachmentSnapshot>? SupportingAttachments { get; set; }
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
