using System.Data.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

// Moved here from the API assembly, which is where it was written and not where it belongs: every caller
// works through AeroLinkDbContext, and the verification services that now need a controlled number live in
// this assembly and cannot reference the API. The class stays in the global namespace so no call site moves.

// Controlled numbers are claimed from a per-prefix sequence row, not computed from the identifiers already
// in the table. Each Next* below is one atomic increment: the database decides who gets which number, so two
// simultaneous creates get two numbers instead of colliding on a unique index and making a person resubmit.
//
// Numbering scope is the prefix, repository-wide — see IdentifierSequence for why, and for why a rolled-back
// create leaves a permanent gap rather than returning its number to the pool.
public static class IdentifierAllocator
{
    private static ILadderPolicy LadderPolicy => LegacyLadderPolicy.Instance;
    /// <summary>
    /// Software change requests are numbered per level, so HLRCR and LLRCR each count on their own — the
    /// same choice already made for SYSTCR/HLRTCR/LLRTCR and the procedures they govern. The prefix
    /// disambiguates, and a reader of an HLRCR never has to wonder whether an LLR change is hiding in it.
    /// </summary>
    public static async Task<string> PreviewChangeRequestAsync(AeroLinkDbContext db, ChangeRequestType type,
        RequirementLevel? softwareLevel, CancellationToken ct, ILadderPolicy? policy = null)
    {
        var prefix = (policy ?? LadderPolicy).ChangeRequestPrefix(type, softwareLevel);
        return FormatChangeRequest(prefix, await PreviewAsync(db, prefix, ct));
    }

    public static async Task<string> PreviewRequirementAsync(AeroLinkDbContext db, string prefix, CancellationToken ct) =>
        Format(prefix, await PreviewAsync(db, prefix, ct));

    public static async Task<string> NextChangeRequestAsync(AeroLinkDbContext db, ChangeRequestType type,
        RequirementLevel? softwareLevel, CancellationToken ct, ILadderPolicy? policy = null)
    {
        var prefix = (policy ?? LadderPolicy).ChangeRequestPrefix(type, softwareLevel);
        return FormatChangeRequest(prefix, await ClaimAsync(db, prefix, ct));
    }

    public static async Task<string> NextRequirementAsync(AeroLinkDbContext db, string prefix, CancellationToken ct) =>
        Format(prefix, await ClaimAsync(db, prefix, ct));

    /// <summary>
    /// The next test change request number, numbered per discipline like the procedures it governs.
    ///
    /// SYSTCR, HLRTCR and LLRTCR follow SYSTP, HLRTP and LLRTP deliberately: a reader who knows what a
    /// HLRTP is should not have to be told what an HLRTCR is. Software's two levels are numbered apart for
    /// the same reason the packages themselves are separate — they are finished by different people.
    /// </summary>
    public static async Task<string> NextTestChangeRequestAsync(AeroLinkDbContext db, TestChangeReviewDiscipline discipline,
        CancellationToken ct, ILadderPolicy? policy = null)
    {
        var prefix = (policy ?? LadderPolicy).TestChangeReviewPrefix(discipline);
        return Format(prefix, await ClaimAsync(db, prefix, ct));
    }

    public static async Task<string> NextTestProcedureAsync(AeroLinkDbContext db, TestProcedureLevel level,
        CancellationToken ct, ILadderPolicy? policy = null)
    {
        var prefix = (policy ?? LadderPolicy).TestProcedurePrefix(level);
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
    /// Reads the number a create would claim without reserving it. Authoring uses this only as an advisory
    /// preview; the create path still claims atomically and may receive a later number after concurrent work.
    /// </summary>
    private static async Task<int> PreviewAsync(AeroLinkDbContext db, string prefix, CancellationToken ct)
    {
        var scope = prefix.Trim().ToUpperInvariant();
        var next = await db.IdentifierSequences.AsNoTracking()
            .Where(x => x.Scope == scope)
            .Select(x => (int?)x.NextValue)
            .SingleOrDefaultAsync(ct);
        return next ?? await SeedAsync(db, scope, ct);
    }

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
        Consider(await db.TestChangeReviews.AsNoTracking().Where(x => x.BaseNumber.StartsWith(scope + "-")).Select(x => x.BaseNumber).ToListAsync(ct));
        Consider(await db.ProblemReports.AsNoTracking().Where(x => x.ReportNumber.StartsWith(scope + "-")).Select(x => x.ReportNumber).ToListAsync(ct));
        return highest + 1;
    }

    public static int Sequence(string number) => ProblemReportNumber.Sequence(number);
    public static string Format(string prefix, int sequence) => $"{prefix}-{sequence:D6}";
    private static string FormatChangeRequest(string prefix, int sequence) => $"{prefix}-{sequence:D5}";
    private static int Max(IEnumerable<string> numbers, string prefix) => numbers.Select(x => x.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[(prefix.Length + 1)..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
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
