using Microsoft.EntityFrameworkCore;
using AeroLink.Infrastructure.Persistence;
using System.Net;
using System.Diagnostics;
using System.Reflection;

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
    string InstanceId,
    string InstanceLabel,
    string InstanceClassification,
    string GitLabConfigurationFingerprint,
    string? DatabaseName,
    string? SnapshotSourceLabel,
    string? SnapshotSourceSha,
    string? SnapshotCreatedAtUtc,
    string? SnapshotActivatedAtUtc,
    DateTimeOffset StartedAtUtc);

public static class RuntimeIdentityEndpoints
{
    /// <summary>
    /// The operating system's creation instant for this process, fixed once so every caller sees the same
    /// value. The updater joins this public observation to the exact service PID and image it owns; a timestamp
    /// captured when the endpoint was first loaded would describe startup timing rather than the process identity.
    /// </summary>
    public static readonly DateTimeOffset ProcessStartedAtUtc = ReadProcessStartedAtUtc();

    private static DateTimeOffset ReadProcessStartedAtUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>The file a release build writes beside the executable to stamp <c>Runtime:SourceSha</c>.</summary>
    public const string ReleaseIdentityFileName = "release-identity.json";

    /// <summary>
    /// The product version compiled into this assembly: the release semver from <c>product/release/VERSION</c>
    /// for a packaged build, <c>0.0.0-dev</c> for every other build. Build metadata after '+' (the source
    /// revision) is dropped, because the revision is reported separately and only to loopback callers.
    /// </summary>
    public static readonly string ProductVersion = ReadProductVersion();

    private static string ReadProductVersion()
    {
        var informational = typeof(RuntimeIdentityEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "unknown";
        var metadata = informational.IndexOf('+');
        return metadata >= 0 ? informational[..metadata] : informational;
    }

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
            InstanceId: Trimmed(configuration["Instance:InstanceId"]) ?? "unknown",
            InstanceLabel: Trimmed(configuration["Instance:Label"]) ?? "AEROLINK",
            InstanceClassification: Trimmed(configuration["Instance:Classification"]) ?? "Undeclared",
            GitLabConfigurationFingerprint: Trimmed(configuration["Runtime:GitLabConfigFingerprint"]) ?? "unconfigured",
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
        app.MapGet("/health/identity", async (HttpContext context, IConfiguration configuration, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var identity = Resolve(configuration);
            var currency = MainCurrency.Read(configuration, identity, DateTimeOffset.UtcNow);
            context.Response.Headers.CacheControl = "no-store";

            // Two audiences, two answers.
            //
            // The launchers and remote-demo recovery always reach this over loopback, and they need the
            // source identity to tell a stale process from a current one. A browser is the other audience,
            // and it needs only enough to label which installation it is looking at.
            //
            // Under START_AEROLINK_SHARED.bat the API binds 0.0.0.0 with AllowedHosts '*', and this endpoint
            // is anonymous by the /health prefix — so anyone on the office network could otherwise read the
            // canonical database name, the exact source revision and the applied schema version without
            // authenticating. None of that is a credential, and none of it is any of their business either.
            // The instance label and snapshot provenance stay, so the badge still works for a LAN browser.
            var loopback = context.Connection.RemoteIpAddress is not null
                && IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

            // Best-effort, never fatal: this endpoint answers about the PROCESS, and must keep answering when
            // the database is unreachable so a launcher can still tell a stale process from a foreign one.
            string? latestMigration = null;
            if (loopback)
            {
                try { latestMigration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault(); }
                catch { latestMigration = null; }
            }

            return Results.Ok(new
            {
                service = "AeroLink API",
                // Not loopback-only, unlike the source revision: the release updater (#1614 1.7) confirms the
                // promoted version at the public HTTPS name, where it is never a loopback caller.
                version = ProductVersion,
                sourceSha = loopback ? identity.SourceSha : null,
                sourceShortSha = loopback ? identity.SourceShortSha : null,
                sourceIdentity = loopback ? identity.SourceIdentity : null,
                gitLabConfigurationFingerprint = loopback ? identity.GitLabConfigurationFingerprint : null,
                mode = identity.Mode,
                mainCurrency = currency is null ? null : new
                {
                    state = currency.State,
                    checkedAtUtc = currency.CheckedAtUtc,
                    remoteSha = loopback ? currency.RemoteSha : null,
                },
                instance = new
                {
                    id = identity.InstanceId,
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
                database = new { name = loopback ? identity.DatabaseName : null },
                schema = new { latestAppliedMigration = latestMigration },
                startedAtUtc = identity.StartedAtUtc,
            });
        });
    }
}
