using System.Data.Common;
using AeroLink.Domain.Common;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
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

// Controlled numbers are claimed from a per-prefix sequence row, not computed from the identifiers already
// in the table. Each Next* below is one atomic increment: the database decides who gets which number, so two
// simultaneous creates get two numbers instead of colliding on a unique index and making a person resubmit.
//
// Numbering scope is the prefix, repository-wide — see IdentifierSequence for why, and for why a rolled-back
// create leaves a permanent gap rather than returning its number to the pool.
public static class IdentifierAllocator
{
    public static async Task<string> NextChangeRequestAsync(AeroLinkDbContext db, ChangeRequestType type, CancellationToken ct)
    {
        var prefix = type == ChangeRequestType.System ? "SCR" : "SWCR";
        return FormatChangeRequest(prefix, await ClaimAsync(db, prefix, ct));
    }

    public static async Task<string> NextRequirementAsync(AeroLinkDbContext db, string prefix, CancellationToken ct) =>
        Format(prefix, await ClaimAsync(db, prefix, ct));

    public static async Task<string> NextTestProcedureAsync(AeroLinkDbContext db, TestProcedureLevel level, CancellationToken ct)
    {
        var prefix = level switch { TestProcedureLevel.System => "SYSTP", TestProcedureLevel.HighLevel => "HLRTP", _ => "LLRTP" };
        return Format(prefix, await ClaimAsync(db, prefix, ct));
    }

    public static async Task<string> NextProblemReportAsync(AeroLinkDbContext db, CancellationToken ct) =>
        $"PR-{await ClaimAsync(db, "PR", ct):D5}";

    /// <summary>
    /// Takes the next number for a prefix as a single statement, so concurrent callers serialize on the row
    /// rather than racing to read the same maximum.
    ///
    /// The sequence row is created on first use from the highest identifier already recorded, which is what
    /// lets an existing database adopt this without a data migration that has to know every prefix in use.
    /// </summary>
    public static Task<int> ClaimAsync(AeroLinkDbContext db, string prefix, CancellationToken ct) =>
        ClaimAsync(db, prefix, () => SeedAsync(db, prefix.Trim().ToUpperInvariant(), ct), ct);

    /// <summary>
    /// Claims from a sequence whose first value cannot be read off the identifier tables — a controlled
    /// attachment numbers its versions per logical file, so only the caller knows where that count stands.
    /// </summary>
    public static async Task<int> ClaimAsync(AeroLinkDbContext db, string prefix, Func<Task<int>> seed, CancellationToken ct)
    {
        var scope = prefix.Trim().ToUpperInvariant();

        // Only the very first claim for a prefix can need more than one pass, and only because the row has to
        // exist before it can be incremented. The insert is attempted more than once because it can lose for
        // two different reasons — another writer seeded the same prefix first, or the database was briefly
        // locked — and only the first of those means the row is now there to read.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claimed = await TryClaimAsync(db, scope, ct);
            if (claimed is not null) return claimed.Value;

            // Inserted through ADO rather than the change tracker on purpose — SaveChangesAsync here would
            // also commit whatever the caller had staged but not yet decided to write.
            try { await SeedRowAsync(db, scope, await seed(), ct); }
            catch (DbException) { /* Seeded by someone else, or transiently refused; the next pass settles it. */ }
        }

        return await TryClaimAsync(db, scope, ct)
            ?? throw new IdentifierAllocationException(scope);
    }

    private static Task SeedRowAsync(AeroLinkDbContext db, string scope, int firstValue, CancellationToken ct) =>
        ExecuteAsync(db, ct, command =>
        {
            command.CommandText = """INSERT INTO identifier_sequences ("Id", "Scope", "NextValue", "ConcurrencyStamp") VALUES (@id, @scope, @next, 0)""";
            Bind(command, "@id", Guid.NewGuid());
            Bind(command, "@scope", scope);
            Bind(command, "@next", (long)firstValue);
            return command.ExecuteNonQueryAsync(ct);
        });

    private static async Task<int?> TryClaimAsync(AeroLinkDbContext db, string scope, CancellationToken ct)
    {
        // Raw ADO rather than a tracked entity on purpose. A tracked increment would only take effect when
        // the caller saves, which puts the read and the write back on opposite sides of a race; this commits
        // the claim on its own so the number is spent the moment it is handed out.
        var result = await ExecuteAsync(db, ct, command =>
        {
            command.CommandText =
                """
                UPDATE identifier_sequences
                   SET "NextValue" = "NextValue" + 1, "ConcurrencyStamp" = "ConcurrencyStamp" + 1
                 WHERE "Scope" = @scope
                RETURNING "NextValue" - 1
                """;
            Bind(command, "@scope", scope);
            return command.ExecuteScalarAsync(ct);
        });
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    /// <summary>
    /// Runs one statement on the context's own connection, enlisted in whatever transaction it already has.
    ///
    /// Opened and closed through <see cref="DatabaseFacade"/> rather than on the raw connection: EF counts
    /// who opened the connection and closes it when that count returns to zero. Opening the underlying
    /// connection directly is invisible to that count, so the connection stays open for the rest of the
    /// context's life — which on a file-backed database leaves the file locked long after the request is done.
    /// </summary>
    private static async Task<T> ExecuteAsync<T>(AeroLinkDbContext db, CancellationToken ct, Func<DbCommand, Task<T>> run)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return await run(command);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static void Bind(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The first number a prefix should hand out, given whatever it has already numbered.</summary>
    private static async Task<int> SeedAsync(AeroLinkDbContext db, string scope, CancellationToken ct)
    {
        var highest = 0;
        void Consider(IEnumerable<string> numbers) => highest = Math.Max(highest, Max(numbers, scope));

        Consider(await db.SystemChangeRequests.AsNoTracking().Where(x => x.BaseNumber.StartsWith(scope + "-")).Select(x => x.BaseNumber).ToListAsync(ct));
        Consider(await db.Requirements.AsNoTracking().Where(x => x.BaseNumber.StartsWith(scope + "-")).Select(x => x.BaseNumber).ToListAsync(ct));
        Consider(await db.RequirementChanges.AsNoTracking().Where(x => x.BaseNumber.StartsWith(scope + "-")).Select(x => x.BaseNumber).ToListAsync(ct));
        Consider(await db.TestProcedures.AsNoTracking().Where(x => x.BaseNumber.StartsWith(scope + "-")).Select(x => x.BaseNumber).ToListAsync(ct));
        Consider(await db.ProblemReports.AsNoTracking().Where(x => x.ReportNumber.StartsWith(scope + "-")).Select(x => x.ReportNumber).ToListAsync(ct));
        return highest + 1;
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
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, mode=c.Mode.ToString(), state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, s.Authority, s.StageName, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, a.Detail, a.OccurredAt, a.EvidenceJson, a.SchemaVersion })
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

/// <summary>
/// The sequence row for a prefix could neither be read nor created. The request itself was valid, so this is
/// answered as a conflict a caller can resubmit rather than as a fault.
/// </summary>
public sealed class IdentifierAllocationException(string scope)
    : Exception($"Could not allocate a controlled number for prefix '{scope}'.")
{
    public string Scope { get; } = scope;
}
