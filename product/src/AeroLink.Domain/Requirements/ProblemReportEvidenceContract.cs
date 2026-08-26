using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// The single deterministic evidence shape for a Problem Report. Every controlled public value on the aggregate
/// is deliberately committed: authored content, identity, assignment, lifecycle, authority and event timing.
/// Derived persistence indexes are explicitly classified by the completeness test instead. A future aggregate
/// field therefore has to be classified instead of silently falling out of history.
/// </summary>
public sealed record ProblemReportEvidenceSnapshot
{
    [JsonPropertyOrder(-2)] public string Contract { get; init; } = ProblemReportEvidenceContract.Contract;
    [JsonPropertyOrder(-1)] public int SchemaVersion { get; init; } = ProblemReportEvidenceContract.SchemaVersion;
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string ReportNumber { get; init; }
    public required int Revision { get; init; }
    public required string DisplayNumber { get; init; }
    public required string Title { get; init; }
    public required string Problem { get; init; }
    public required string Analysis { get; init; }
    public required string AnalysisRich { get; init; }
    public required string ReportedBy { get; init; }
    public required string ResponsibleEngineerId { get; init; }
    public required Guid? TargetReleaseId { get; init; }
    public required string ProblemRich { get; init; }
    public required string AdditionalInformation { get; init; }
    public required string AdditionalInformationRich { get; init; }
    public required string SystemAircraftImpact { get; init; }
    public required string SystemAircraftImpactRich { get; init; }
    public required string? Category { get; init; }
    public required string? CategoryProvenance { get; init; }
    public required string Workaround { get; init; }
    public required string WorkaroundRich { get; init; }
    public required string ImpactAssessmentJson { get; init; }
    public required string Classification { get; init; }
    public required string Severity { get; init; }
    public required string Priority { get; init; }
    public required string Origin { get; init; }
    public required string AffectedConfiguration { get; init; }
    public required string RootCause { get; init; }
    public required string RootCauseRich { get; init; }
    public required string Effects { get; init; }
    public required string EffectsRich { get; init; }
    public required string Containment { get; init; }
    public required string ContainmentRich { get; init; }
    public required string CorrectiveAction { get; init; }
    public required string CorrectiveActionRich { get; init; }
    public required string? Disposition { get; init; }
    public required string DispositionRationale { get; init; }
    public required Guid? ResolutionVerificationExecutionId { get; init; }
    public required Guid? ClosureApprovedBy { get; init; }
    public required string ClosureApprovedByName { get; init; }
    public required DateTimeOffset? ClosureApprovedAt { get; init; }
    public required bool IsReleaseBlocker { get; init; }
    public required long ReleaseBlockerVersion { get; init; }
    public required string WaiverRationale { get; init; }
    public required string WaivedBy { get; init; }
    public required DateTimeOffset? WaivedAt { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required long Version { get; init; }
}

public static class ProblemReportEvidenceContract
{
    public const string Contract = "aerolink.problem-report-evidence";

    // Version 1 was the independently maintained closure-review projection. Unversioned lifecycle rows are
    // schema 0. Version 2 is the first shared, complete Problem Report evidence contract. Version 3 retires
    // the four-kind Type in favour of the nine-category vocabulary and records how each value was arrived at,
    // so a schema-2 snapshot and a schema-3 snapshot of the same report are not comparable field for field.
    // Version 4 adds the authored companion to every narrative field. A schema-3 snapshot committed only
    // the plain projection of an analysis or a root cause, so the two are not comparable field for field.
    public const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static ProblemReportEvidenceSnapshot Create(ProblemReport report, long? versionOverride = null) => new()
    {
        Id = report.Id,
        ProjectId = report.ProjectId,
        ReportNumber = report.ReportNumber,
        Revision = report.Revision,
        DisplayNumber = report.DisplayNumber,
        Title = report.Title,
        Problem = report.Problem,
        Analysis = report.Analysis,
        AnalysisRich = report.AnalysisRich,
        ReportedBy = report.ReportedBy,
        ResponsibleEngineerId = report.ResponsibleEngineerId,
        TargetReleaseId = report.TargetReleaseId,
        ProblemRich = report.ProblemRich,
        AdditionalInformation = report.AdditionalInformation,
        AdditionalInformationRich = report.AdditionalInformationRich,
        SystemAircraftImpact = report.SystemAircraftImpact,
        SystemAircraftImpactRich = report.SystemAircraftImpactRich,
        Category = report.Category?.ToString(),
        CategoryProvenance = report.CategoryProvenance?.ToString(),
        Workaround = report.Workaround,
        WorkaroundRich = report.WorkaroundRich,
        ImpactAssessmentJson = report.ImpactAssessmentJson,
        Classification = report.Classification,
        Severity = report.Severity.ToString(),
        Priority = report.Priority.ToString(),
        Origin = report.Origin,
        AffectedConfiguration = report.AffectedConfiguration,
        RootCause = report.RootCause,
        RootCauseRich = report.RootCauseRich,
        Effects = report.Effects,
        EffectsRich = report.EffectsRich,
        Containment = report.Containment,
        ContainmentRich = report.ContainmentRich,
        CorrectiveAction = report.CorrectiveAction,
        CorrectiveActionRich = report.CorrectiveActionRich,
        Disposition = report.Disposition?.ToString(),
        DispositionRationale = report.DispositionRationale,
        ResolutionVerificationExecutionId = report.ResolutionVerificationExecutionId,
        ClosureApprovedBy = report.ClosureApprovedBy,
        ClosureApprovedByName = report.ClosureApprovedByName,
        ClosureApprovedAt = report.ClosureApprovedAt,
        IsReleaseBlocker = report.IsReleaseBlocker,
        ReleaseBlockerVersion = report.ReleaseBlockerVersion,
        WaiverRationale = report.WaiverRationale,
        WaivedBy = report.WaivedBy,
        WaivedAt = report.WaivedAt,
        State = ProblemReportTransitionPolicy.Canonical(report.State).ToString(),
        CreatedAt = report.CreatedAt,
        UpdatedAt = report.UpdatedAt,
        Version = versionOverride ?? report.Version,
    };

    public static string Serialize(ProblemReport report, long? versionOverride = null) =>
        Serialize(Create(report, versionOverride));

    public static string Serialize(ProblemReportEvidenceSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);

    public static string Hash(ProblemReport report, long? versionOverride = null) =>
        Hash(Serialize(report, versionOverride));

    public static string Hash(ProblemReportEvidenceSnapshot snapshot) => Hash(Serialize(snapshot));

    public static string Hash(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
