using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

static class IdentifierAllocator
{
    public static async Task<string> NextChangeRequestAsync(AeroLinkDbContext db, ChangeRequestType type, CancellationToken ct)
    {
        var prefix = type == ChangeRequestType.System ? "SCR" : "SWCR";
        var numbers = await db.SystemChangeRequests.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return FormatChangeRequest(prefix, Max(numbers, prefix) + 1);
    }

    public static async Task<string> NextRequirementAsync(AeroLinkDbContext db, string prefix, CancellationToken ct)
    {
        var authoritative = await db.Requirements.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        var proposed = await db.RequirementChanges.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return Format(prefix, Math.Max(Max(authoritative, prefix), Max(proposed, prefix)) + 1);
    }

    public static async Task<string> NextTestProcedureAsync(AeroLinkDbContext db, TestProcedureLevel level, CancellationToken ct)
    {
        var prefix = level switch { TestProcedureLevel.System => "SYSTP", TestProcedureLevel.HighLevel => "HLRTP", _ => "LLRTP" };
        var numbers = await db.TestProcedures.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return Format(prefix, Max(numbers, prefix) + 1);
    }

    public static async Task<string> NextProblemReportAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var numbers = await db.ProblemReports.AsNoTracking().Where(x => x.ReportNumber.StartsWith("PR-")).Select(x => x.ReportNumber).ToListAsync(ct);
        return $"PR-{Max(numbers, "PR") + 1:D5}";
    }

    public static int Sequence(string number) => int.TryParse(number[(number.LastIndexOf('-') + 1)..], out var value) ? value : 1;
    public static string Format(string prefix, int sequence) => $"{prefix}-{sequence:D6}";
    private static string FormatChangeRequest(string prefix, int sequence) => $"{prefix}-{sequence:D8}";
    private static int Max(IEnumerable<string> numbers, string prefix) => numbers.Select(x => x.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[(prefix.Length + 1)..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
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
    public static object Workspace(ProgramRecord program, ProjectRecord project, SoftwareRelease release) => new
    {
        program = new { program.Id, program.Name, program.Code },
        project = new { project.Id, project.Name, project.SoftwareProduct },
        release = new { release.Id, release.Version, release.IsReleased }
    };
    // baseNumber and revisionCount travel with each row so a collapsed listing can offer the history behind it,
    // and deferredFromState so a shelved change request can say how far it got rather than only that it is away.
    public static object ScrSummary(ScrListItem x) => new { x.Id, x.BaseNumber, x.Revision, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Title, state = x.State.ToString(), type = x.Type.ToString(), x.AuthorId, x.TargetReleaseId, x.RequirementCount, x.UpdatedAt, deferredFromState = x.DeferredFromState?.ToString(), x.RevisionCount };
    public static object ScrDetail(SystemChangeRequest x) => new
    {
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, type = x.Type.ToString(), x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId, x.Version,
        x.ProblemRich, x.AnalysisRich, x.SolutionRich,
        state = x.State.ToString(), deferredFromState = x.DeferredFromState?.ToString(), x.CreatedAt, x.UpdatedAt,
        requirementChanges = x.RequirementChanges.Select(r => new { r.Id, r.BaseNumber, r.Revision, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.Rationale, r.VerificationMethod,r.RichText,r.AttributesJson,r.ImpactDispositionJson,r.TargetSectionId }),
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, mode=c.Mode.ToString(), state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, a.Detail, a.OccurredAt })
    };
    public static object Baseline(CandidateBaseline x) => new { x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count };
    public static object BaselineDetail(CandidateBaseline x, IReadOnlyList<SystemChangeRequest> selected) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt,
        selections = selected.OrderBy(scr => scr.DisplayNumber).Select(scr => new
        {
            scr.Id, scr.DisplayNumber, scr.Title,
            requirementChanges = scr.RequirementChanges.OrderBy(r => r.DisplayNumber).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.VerificationMethod })
        }),
        events = x.Events.OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.ActorId, e.Detail, e.OccurredAt })
    };
}
