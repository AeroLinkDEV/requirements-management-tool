using System.Data.Common;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

// Support shared by more than one endpoint module: reading the actor off the request, allocating the next
// controlled identifier, and mapping an aggregate to the shape the browser reads.
//
// ApiMap is why these are not simply private to a module. A change request rendered by the change-request
// endpoints, by the baseline endpoints, and by search has to be the same object in all three, or the client
// ends up holding three subtly different notions of one record.

static class IdentityHttpExtensions
{
    public static AuthenticatedUser UserAccount(this HttpContext context) => context.Items.TryGetValue("AeroLink.User", out var value) && value is AuthenticatedUser user
        ? user : throw new InvalidOperationException("Authenticated user context is unavailable.");
    public static async Task<bool> HasProjectRoleAsync(this HttpContext context, AeroLinkDbContext db, IdentityService identity, Guid projectId, CancellationToken ct, params ProgramRole[] roles)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct); if (programId is null) return false;
        foreach (var role in roles) if (await identity.HasRoleAsync(context.UserAccount(), programId.Value, role, DateTimeOffset.UtcNow, ct)) return true;
        return false;
    }
    public static async Task<bool> HasProjectAccessAsync(this HttpContext context, AeroLinkDbContext db, Guid projectId, CancellationToken ct)
    {
        var actor=context.UserAccount();if(actor.IsAdministrator)return true;
        var programId=await db.Projects.Where(x=>x.Id==projectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        return programId is not null&&actor.Programs.Any(x=>x.ProgramId==programId.Value);
    }
}

/// <summary>
/// Projects the append-only identity-migration evidence alongside the original signature. The signature row
/// remains the source of truth for who signed which old hash; this projection only reports the governed hand-off
/// recorded by the migration and never changes or re-signs that row.
/// </summary>
internal sealed record SignatureMigrationProvenance(
    string? Migration,
    string? Reason,
    string? OldArtifactIdentity,
    string? OldSignatureHash,
    string? NewArtifactIdentity,
    string? NewContentHash,
    Guid? PendingEvidenceId,
    Guid? CompletedEvidenceId,
    DateTimeOffset? SupersededAt,
    DateTimeOffset? CompletedAt);

internal sealed record SignatureMigrationProjection(
    string Status,
    bool IsSuperseded,
    SignatureMigrationProvenance? Supersession)
{
    public static SignatureMigrationProjection Current { get; } = new("Current", false, null);
}

internal static class SignatureMigrationProjector
{
    private const string PendingType = "VerificationIdentityMigration.SignatureSuperseded";
    private const string CompletedType = "VerificationIdentityMigration.SignatureSupersessionCompleted";

    public static async Task<IReadOnlyDictionary<Guid, SignatureMigrationProjection>> ForAsync(
        AeroLinkDbContext db, IEnumerable<Guid> signatureIds, CancellationToken ct)
    {
        var ids = signatureIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, SignatureMigrationProjection>();
        var targets = ids.Select(id => $"ElectronicSignature:{id}").ToArray();
        var eventQuery = db.SecurityAuditEvents.AsNoTracking()
            .Where(x => targets.Contains(x.Target) && (x.EventType == PendingType || x.EventType == CompletedType));
        var events = db.Database.IsSqlite()
            ? (await eventQuery.ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList()
            : await eventQuery.OrderBy(x => x.OccurredAt).ToListAsync(ct);
        var result = new Dictionary<Guid, SignatureMigrationProjection>();
        foreach (var id in ids)
        {
            var target = $"ElectronicSignature:{id}";
            var pending = events.LastOrDefault(x => x.Target == target && x.EventType == PendingType);
            var completed = events.LastOrDefault(x => x.Target == target && x.EventType == CompletedType);
            if (pending is null && completed is null) continue;
            var source = completed ?? pending!;
            result[id] = new SignatureMigrationProjection(
                "Superseded", true,
                new SignatureMigrationProvenance(
                    DetailString(source.Detail, "migration"),
                    DetailString(source.Detail, "reason"),
                    DetailString(source.Detail, "oldArtifactIdentity"),
                    DetailString(source.Detail, "oldSignatureHash"),
                    DetailString(source.Detail, "newArtifactIdentity"),
                    DetailString(source.Detail, "newContentHash"),
                    pending?.Id,
                    completed?.Id,
                    pending?.OccurredAt,
                    completed?.OccurredAt));
        }
        return result;
    }

    private static string? DetailString(string detail, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            // The migration writes structured evidence. A malformed historical event is not converted into
            // invented provenance; the signature remains visible with the immutable row fields only.
            return null;
        }
    }
}

static class DirectoryTitles
{
    public static string For(string userName,IReadOnlyCollection<string> roles)
    {
        if(userName.StartsWith("system.engineer"))return "System Engineer";
        if(userName.StartsWith("software.engineer"))return "Software Engineer";
        if(userName.StartsWith("verification.engineer"))return "Verification Engineer";
        if(userName.StartsWith("systems.lead"))return "Systems Engineering Lead";
        if(userName.StartsWith("software.lead"))return "Software Engineering Lead";
        if(userName.StartsWith("engineering.manager"))return "Engineering Manager";
        if(userName.StartsWith("configuration"))return "Configuration Management Specialist";
        if(userName.StartsWith("airworthiness"))return "Airworthiness";
        if(userName.StartsWith("quality"))return "Software Quality Analyst";
        if(userName.StartsWith("project.lead"))return "Project Engineering Lead";
        if(roles.Contains("SystemEngineeringLead"))return "System Engineering Lead";
        if(roles.Contains("SoftwareEngineeringLead"))return "Software Engineering Lead";
        if(roles.Contains("ProjectEngineeringLead"))return "Project Engineering Lead";
        if(roles.Contains("EngineeringManager"))return "Engineering Manager";
        if(roles.Contains("Airworthiness"))return "Airworthiness";
        if(roles.Contains("SoftwareQualityAnalyst"))return "Software Quality Analyst";
        if(roles.Contains("TestLead"))return "Test Engineering Lead";
        if(roles.Contains("SystemEngineer"))return "System Engineer";
        if(roles.Contains("SoftwareEngineer"))return "Software Engineer";
        if(roles.Contains("ProgramManager"))return "Program Manager";
        if(roles.Contains("TestEngineer"))return "Test Engineer";
        if(roles.Contains("Approver"))return "Designated Approver";
        if(roles.Contains("Engineer"))return "Engineer";
        return "AeroLink User";
    }
}

static class ProblemReportIntegrationMap
{
    public static string ArtifactKind(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "requirement" => "requirement",
        "changerequest" or "scr" or "swcr" => "change-request",
        "testexecution" => "test-execution",
        "softwarebuild" or "build" => "build",
        "baseline" => "baseline",
        "document" => "document",
        "evidence" => "evidence",
        "release" => "release",
        "problemreport" or "pr" => "problem-report",
        _ => "artifact"
    };

    public static string ArtifactLabel(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "changerequest" or "scr" or "swcr" => "Controlled change",
        "testexecution" => "Verification execution",
        "softwarebuild" or "build" => "Software build",
        "problemreport" or "pr" => "Related problem report",
        _ => artifactType
    };
}

static class ApiMap
{
    /// <summary>
    /// The requirement level a procedure at this level verifies. One fact, stated once.
    ///
    /// The same correspondence <c>AeroLinkDbContext</c> enforces on a coverage link — a procedure covers
    /// requirements at its own level — expressed here so the authoring path can refuse a wrong-level
    /// requirement before it is stored rather than at materialization.
    /// </summary>
    public static RequirementLevel RequirementLevelFor(TestProcedureLevel level) =>
        LegacyLadderPolicy.Instance.RequirementLevelFor(level);

    public static RequirementLevel RequirementLevelFor(TestProcedureLevel level, ILadderPolicy ladderPolicy) =>
        ladderPolicy.RequirementLevelFor(level);

    public static string ControlledDocumentTypeLabel(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "System Requirements Document (SYSRD)",
        ControlledDocumentType.SwrdHighLevel => "High-Level Software Requirements Document (HLRD)",
        ControlledDocumentType.SwrdLowLevel => "Low-Level Software Requirements Document (LLRD)",
        ControlledDocumentType.SystemTestProcedures => "System Test Procedure Document (SYSTD)",
        ControlledDocumentType.HighLevelTestProcedures => "HLR Test Procedure Document (HLRTPD)",
        ControlledDocumentType.LowLevelTestProcedures => "LLR Test Procedure Document (LLRTPD)",
        ControlledDocumentType.HighLevelTestCases => "HLR Test Case Document (HLRTD)",
        ControlledDocumentType.LowLevelTestCases => "LLR Test Case Document (LLRTD)",
        _ => throw new DomainException($"Unknown controlled document type: {type}.")
    };

    private static readonly Regex LegacyRequirementNumber = new(@"\b(SYSR|HLR|LLR)-0*([0-9]{1,6})(\.[0-9]{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static string CanonicalAuditDetail(string detail) => LegacyRequirementNumber.Replace(detail, match =>
        $"{match.Groups[1].Value.ToUpperInvariant()}-{int.Parse(match.Groups[2].Value):D6}{match.Groups[3].Value}");
    public static object Workspace(ProgramRecord program, ProjectRecord project, SoftwareRelease release) => new
    {
        program = new { program.Id, program.Name, program.Code },
        project = new { project.Id, project.Name, project.SoftwareProduct },
        release = new { release.Id, release.Version, release.IsReleased }
    };
    // baseNumber and revisionCount travel with each row so a collapsed listing can offer the history behind it,
    // and deferredFromState so a shelved change request can say how far it got rather than only that it is away.
    public static object ChangeRequestSummary(ScrListItem x) => new { x.Id, x.BaseNumber, x.Revision, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Title, state = x.State.ToString(), type = x.Type.ToString(), x.AuthorId, x.TargetReleaseId, x.RequirementCount, x.UpdatedAt, deferredFromState = x.DeferredFromState?.ToString(), x.RevisionCount, x.RebaseRequiredReason };
    public static object ChangeRequestDetail(SystemChangeRequest x, IReadOnlyList<object>? contention = null) => new
    {
        // Null everywhere except the authoring responses that compute it: the reader of a record does not
        // need a contention query run for them, and the author adding a change does.
        contention,
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, x.OriginReleaseId, type = x.Type.ToString(), softwareLevel = x.SoftwareLevel?.ToString(), x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId, x.Version,
        x.ProblemRich, x.AnalysisRich, x.SolutionRich,
        state = x.State.ToString(), deferredFromState = x.DeferredFromState?.ToString(),
        withdrawnFromState = x.WithdrawnFromState?.ToString(),
        // Served rather than derived in the browser: the rebase prompt hangs off it, and a client working out
        // whether a revision still exists is a client that will disagree with the server about it.
        x.RebaseRequiredReason, x.CreatedAt, x.UpdatedAt,
        requirementChanges = x.RequirementChanges.Select(r => new { r.Id, r.BaseNumber, r.Revision, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.Rationale, r.VerificationMethod,r.RichText,r.AttributesJson,r.ImpactDispositionJson,r.TargetSectionId, upstreamRevisionIds = JsonSerializer.Deserialize<List<Guid>>(r.ProposedUpstreamRevisionIdsJson) ?? [] }),
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, mode=c.Mode.ToString(), state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, s.Authority, s.StageName, s.Rationale, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, Detail = CanonicalAuditDetail(a.Detail), a.OccurredAt, a.EvidenceJson, a.SchemaVersion })
    };
    /// <summary>
    /// A reviewer comment as one viewer sees it. <c>isMine</c> is served rather than left to the client to
    /// work out, because the edit and remove controls hang off it and comparing display names in the browser
    /// is how you end up letting the wrong person try.
    /// </summary>
    public static object ReviewComment(ReviewComment x, string viewer) => new
    {
        x.Id, x.AuthorId, anchor = x.Anchor.ToString(), x.RequirementChangeId, x.Body,
        // Empty for a change request comment, and the reviewer's own words about where they were reading
        // for a document one — a DOCX has no structure this system can address.
        x.SectionLabel,
        state = x.State.ToString(), x.DecisionRecorded, x.CreatedAt, x.UpdatedAt, x.PublishedAt,
        isMine = string.Equals(x.AuthorId, viewer, StringComparison.OrdinalIgnoreCase),
    };
    public static object Baseline(CandidateBaseline x) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId,
        state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt,
        x.CreatedAt, x.FrozenAt, x.TestProceduresHash, x.TestProceduresMaterializedAt,
        scrSelectionCount = x.Selections.Count,
        externalPackageSelectionCount = x.ExternalPackageSelections.Count,
        selectionCount = x.Selections.Count + x.ExternalPackageSelections.Count
    };
    public static object BaselineDetail(CandidateBaseline x, IReadOnlyList<SystemChangeRequest> selected) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.TestProceduresHash, x.TestProceduresMaterializedAt,
        selections = selected.OrderBy(scr => scr.DisplayNumber).Select(scr => new
        {
            scr.Id, scr.DisplayNumber, scr.Title,
            requirementChanges = scr.RequirementChanges.OrderBy(r => r.DisplayNumber).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.VerificationMethod })
        }),
        externalPackageSelections = x.ExternalPackageSelections.OrderBy(selection => selection.BaselineImportId).Select(selection => new
        {
            selection.Id, selection.BaselineImportId, selection.PackageContentHash,
            selection.SelectedAt, selection.SelectedBy
        }),
        events = x.Events.OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.ActorId, e.Detail, e.OccurredAt })
    };

    /// <summary>
    /// What reopening a build disturbs, in the shape the confirmation renders.
    ///
    /// The same projection serves the preview and the result of the act, so what a reader was shown and what
    /// they are told happened are the same list rather than two lists that agree today.
    /// </summary>
    public static object ReopenConsequences(ReopenConsequences x) => new
    {
        revisionsTakenBack = x.RevisionsTakenBack,
        requirementsRemoved = x.RequirementsRemoved,
        codeRecordsTakenBack = x.CodeRecordsTakenBack,
        strandedChangeRequests = x.StrandedChangeRequests.Select(s => new
        {
            s.ChangeRequestId, s.DisplayNumber, state = s.State, s.ReviewWillBeCancelled, s.Requirements,
        }),
        disturbedCoverage = x.DisturbedCoverage.Select(c => new { c.Procedure, c.Requirement, c.Consequence }),
    };
}
