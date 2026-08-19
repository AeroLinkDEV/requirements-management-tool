using System.Data.Common;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
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
    public static RequirementLevel RequirementLevelFor(TestProcedureLevel level) => level switch
    {
        TestProcedureLevel.System => RequirementLevel.System,
        TestProcedureLevel.HighLevel => RequirementLevel.HighLevel,
        _ => RequirementLevel.LowLevel,
    };

    public static string ControlledDocumentTypeLabel(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "System Requirements Document (SYSRD)",
        ControlledDocumentType.SwrdHighLevel => "High-Level Software Requirements Document (HLRD)",
        ControlledDocumentType.SwrdLowLevel => "Low-Level Software Requirements Document (LLRD)",
        ControlledDocumentType.SystemTestProcedures => "System Test Procedure Document (SYSTD)",
        ControlledDocumentType.HighLevelTestProcedures => "HLR Test Procedure Document (HLRTD)",
        _ => "LLR Test Procedure Document (LLRTD)"
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
    public static object ChangeRequestSummary(ScrListItem x) => new { x.Id, x.BaseNumber, x.Revision, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Title, state = x.State.ToString(), type = x.Type.ToString(), x.AuthorId, x.TargetReleaseId, x.RequirementCount, x.UpdatedAt, deferredFromState = x.DeferredFromState?.ToString(), x.RevisionCount };
    public static object ChangeRequestDetail(SystemChangeRequest x, IReadOnlyList<object>? contention = null) => new
    {
        // Null everywhere except the authoring responses that compute it: the reader of a record does not
        // need a contention query run for them, and the author adding a change does.
        contention,
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, x.OriginReleaseId, type = x.Type.ToString(), softwareLevel = x.SoftwareLevel?.ToString(), x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId, x.Version,
        x.ProblemRich, x.AnalysisRich, x.SolutionRich,
        state = x.State.ToString(), deferredFromState = x.DeferredFromState?.ToString(), x.CreatedAt, x.UpdatedAt,
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
    public static object Baseline(CandidateBaseline x) => new { x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.TestProceduresHash, x.TestProceduresMaterializedAt, selectionCount = x.Selections.Count };
    public static object BaselineDetail(CandidateBaseline x, IReadOnlyList<SystemChangeRequest> selected) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.TestProceduresHash, x.TestProceduresMaterializedAt,
        selections = selected.OrderBy(scr => scr.DisplayNumber).Select(scr => new
        {
            scr.Id, scr.DisplayNumber, scr.Title,
            requirementChanges = scr.RequirementChanges.OrderBy(r => r.DisplayNumber).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.VerificationMethod })
        }),
        events = x.Events.OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.ActorId, e.Detail, e.OccurredAt })
    };
}
