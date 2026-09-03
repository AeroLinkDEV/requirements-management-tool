using Microsoft.EntityFrameworkCore;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

/// <summary>
/// The non-secret answer to "which AeroLink is this, and what is it running?".
///
/// A 200 from <c>/health/ready</c> proves this process can reach a database. It does not prove the process was
/// built from the source the launcher is about to run, nor that it belongs to the launcher mode being started
/// — and #816 showed what that costs: a healthy API from an older revision survived a repository update while
/// the client moved forward, and every launcher declared success.
///
/// This surface exists so a launcher can decide reuse honestly, and so an operator can tell HOME CANONICAL
/// from WORK-LAPTOP LOCAL at a glance rather than by remembering which window is which.
///
/// Everything here is non-secret by construction. The database is reported by NAME only: never the host, the
/// port, the user, the password, or the connection string. No token, key, or credential is read.
/// </summary>
public sealed record AeroLinkRuntimeIdentity(
    string SourceSha,
    string SourceShortSha,
    string SourceIdentity,
    string Mode,
    string InstanceLabel,
    string InstanceClassification,
    string? DatabaseName,
    string? SnapshotSourceLabel,
    string? SnapshotSourceSha,
    string? SnapshotCreatedAtUtc,
    string? SnapshotActivatedAtUtc,
    DateTimeOffset StartedAtUtc);

public static class RuntimeIdentityEndpoints
{
    /// <summary>The instant this process started, fixed once so every caller sees the same value.</summary>
    public static readonly DateTimeOffset ProcessStartedAtUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Reads the identity a launcher declared through configuration.
    ///
    /// A launcher that declares nothing gets "unknown" rather than a guess. That is the safe answer: an
    /// unknown source identity never equals the identity a launcher expects, so a process started outside the
    /// supported launchers is treated as stale and restarted rather than silently reused.
    /// </summary>
    public static AeroLinkRuntimeIdentity Resolve(IConfiguration configuration)
    {
        var sha = Trimmed(configuration["Runtime:SourceSha"]) ?? "unknown";
        // The launcher's identity string is the authority when present: for a dirty development checkout it
        // carries a worktree fingerprint that a bare SHA cannot, and pretending the SHA is sufficient there
        // is the exact lie this contract exists to stop.
        var identity = Trimmed(configuration["Runtime:SourceIdentity"]) ?? sha;
        return new AeroLinkRuntimeIdentity(
            SourceSha: sha,
            SourceShortSha: sha.Length >= 8 ? sha[..8] : sha,
            SourceIdentity: identity,
            Mode: Trimmed(configuration["Runtime:Mode"]) ?? "UNKNOWN",
            InstanceLabel: Trimmed(configuration["Instance:Label"]) ?? "AEROLINK",
            InstanceClassification: Trimmed(configuration["Instance:Classification"]) ?? "Undeclared",
            DatabaseName: DatabaseName(configuration),
            SnapshotSourceLabel: Trimmed(configuration["Instance:SnapshotSourceLabel"]),
            SnapshotSourceSha: Trimmed(configuration["Instance:SnapshotSourceSha"]),
            SnapshotCreatedAtUtc: Trimmed(configuration["Instance:SnapshotCreatedAtUtc"]),
            SnapshotActivatedAtUtc: Trimmed(configuration["Instance:SnapshotActivatedAtUtc"]),
            StartedAtUtc: ProcessStartedAtUtc);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The database NAME out of the connection string, and nothing else from it.
    ///
    /// Naming the database is what stops "I added it at work, so it must be at home": it distinguishes one
    /// installation from another. Host, port, user and password are not identity, they are credentials and
    /// topology, and they are deliberately dropped here rather than filtered downstream.
    /// </summary>
    private static string? DatabaseName(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("AeroLink");
        if (string.IsNullOrWhiteSpace(connection)) return null;
        foreach (var part in connection.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = pair[0].Trim();
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim();
        }
        return null;
    }

    /// <summary>
    /// Maps <c>/health/identity</c>. It sits under <c>/health</c> deliberately: that prefix is already the
    /// anonymous set in Program.cs, and a launcher must be able to ask who is listening before it holds a
    /// session — which is the whole point of asking.
    /// </summary>
    public static void MapRuntimeIdentityEndpoint(this WebApplication app)
    {
        app.MapGet("/health/identity", async (IConfiguration configuration, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var identity = Resolve(configuration);
            // Best-effort, never fatal: this endpoint answers about the PROCESS, and must keep answering when
            // the database is unreachable so a launcher can still tell a stale process from a foreign one.
            string? latestMigration = null;
            try { latestMigration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault(); }
            catch { latestMigration = null; }

            return Results.Ok(new
            {
                service = "AeroLink API",
                sourceSha = identity.SourceSha,
                sourceShortSha = identity.SourceShortSha,
                sourceIdentity = identity.SourceIdentity,
                mode = identity.Mode,
                instance = new
                {
                    label = identity.InstanceLabel,
                    classification = identity.InstanceClassification,
                    snapshot = identity.SnapshotCreatedAtUtc is null && identity.SnapshotSourceSha is null ? null : new
                    {
                        sourceLabel = identity.SnapshotSourceLabel,
                        sourceSha = identity.SnapshotSourceSha,
                        createdAtUtc = identity.SnapshotCreatedAtUtc,
                        activatedAtUtc = identity.SnapshotActivatedAtUtc,
                    },
                },
                database = new { name = identity.DatabaseName },
                schema = new { latestAppliedMigration = latestMigration },
                startedAtUtc = identity.StartedAtUtc,
            });
        });
    }
}
