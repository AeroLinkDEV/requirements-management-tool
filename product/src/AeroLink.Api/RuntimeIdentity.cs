using Microsoft.EntityFrameworkCore;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Infrastructure.Persistence.Maintenance;
using AeroLink.Infrastructure.Notifications;

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
        app.MapGet("/health/identity/data-protection", (
            HttpContext context,
            IConfiguration configuration,
            IOptions<DataProtectionOptions> dataProtectionOptions,
            IOptions<KeyManagementOptions> keyManagementOptions,
            IKeyManager keyManager,
            DataProtectionRepositoryCapture repositoryCapture,
            IHostEnvironment environment,
            NotificationInstallationAuthority notificationAuthority) =>
        {
            // This answer contains the effective discriminator, ring location and key metadata fingerprints.
            // A null TestServer address is not loopback evidence, and a forwarded header is deliberately ignored.
            if (context.Connection.RemoteIpAddress is not { } remoteAddress
                || !IPAddress.IsLoopback(remoteAddress))
                return Results.NotFound();

            return Results.Ok(DataProtectionRuntimeCapture.Capture(
                configuration, dataProtectionOptions.Value, keyManagementOptions.Value, keyManager,
                repositoryCapture, environment.ContentRootPath) with
            {
                Connector = ConnectorSigningService.CaptureExistingIdentity(configuration),
                NotificationAuthority = NotificationAuthorityRuntimeCapture.Capture(notificationAuthority),
            });
        });

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

/// <summary>
/// Captures the effective Data Protection configuration of the running process without opening or serializing
/// protected material.  This is intentionally a separate read-only diagnostic so the HOME continuity export can
/// be fed an action-time source receipt instead of a guessed framework default.
/// </summary>
public static class DataProtectionRuntimeCapture
{
    public static DataProtectionRuntimeCaptureResult Capture(
        IConfiguration configuration,
        DataProtectionOptions dataProtectionOptions,
        KeyManagementOptions keyManagementOptions,
        IKeyManager keyManager,
        DataProtectionRepositoryCapture? repositoryCapture = null,
        string? contentRoot = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dataProtectionOptions);
        ArgumentNullException.ThrowIfNull(keyManagementOptions);
        ArgumentNullException.ThrowIfNull(keyManager);

        var identity = RuntimeIdentityEndpoints.Resolve(configuration);
        var process = CaptureProcessIdentity();
        var repository = keyManagementOptions.XmlRepository;
        var repositoryType = repository?.GetType().FullName ?? "unconfigured";
        var repositoryPath = repository is FileSystemXmlRepository fileSystemRepository
            ? fileSystemRepository.Directory.FullName
            : null;
        var fallback = repository is null && keyManager is XmlKeyManager ? repositoryCapture?.Read() : null;
        if (fallback is not null && fallback.HasExistingOrdinaryDirectory())
        {
            repositoryPath = Path.GetFullPath(fallback.Directory);
            repositoryType = typeof(FileSystemXmlRepository).FullName!;
        }
        var repositoryExists = repositoryPath is not null
            && Directory.Exists(repositoryPath);
        var applicationDiscriminator = dataProtectionOptions.ApplicationDiscriminator;
        var declaredProtection = ClassifyProtection(configuration["DataProtection:ProtectKeysWith"]);
        var metadata = new DataProtectionRuntimeMetadata(
            ApplicationDiscriminator: applicationDiscriminator,
            ApplicationDiscriminatorSource: "IOptions<DataProtectionOptions>",
            RepositoryType: repositoryType,
            RepositoryPath: repositoryExists ? repositoryPath : null,
            RepositoryPathSource: repositoryExists ? fallback?.Provenance ?? "actual XmlRepository.Directory" : "unproven",
            DeclaredProtection: declaredProtection,
            XmlEncryptorType: keyManagementOptions.XmlEncryptor?.GetType().FullName,
            AutoGenerateKeys: keyManagementOptions.AutoGenerateKeys,
            DeclaredKeyRingPath: configuration["DataProtection:KeyRingPath"],
            KeyCount: null,
            RevokedKeyCount: null,
            KeyMetadataSha256: null,
            RevokedMetadataSha256: null,
            ObservedProviderSelection: repositoryExists ? fallback?.ProviderSelection : null,
            ProtectionScopeEvidence: fallback?.ProtectionScopeEvidence ?? "unknown",
            DeclaredProtectionSource: "IConfiguration:DataProtection:ProtectKeysWith");

        // FileSystemXmlRepository creates its directory when enumerated. Do not ask IKeyManager for metadata
        // unless the resolved directory already exists; a default candidate must remain explicitly unproven.
        if (!repositoryExists)
            return new DataProtectionRuntimeCaptureResult(
                Status: "unproven",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Process: process,
                Instance: CaptureInstance(identity),
                DataProtection: metadata,
                ContentRoot: contentRoot);

        try
        {
            var ringBefore = HomeContinuityCompatibilityReader.ComputeRingManifestSha256(repositoryPath!);
            var keys = keyManager.GetAllKeys()
                .OrderBy(key => key.KeyId)
                .ToArray();
            var revoked = keys.Where(key => key.IsRevoked).ToArray();
            metadata = metadata with
            {
                KeyCount = keys.Length,
                RevokedKeyCount = revoked.Length,
                KeyMetadataSha256 = MetadataDigest(keys),
                RevokedMetadataSha256 = MetadataDigest(revoked),
                RingManifestSha256 = ringBefore,
                Keys = keys.Select(key => new DataProtectionRuntimeKeyMetadata(key.KeyId, key.CreationDate,
                    key.ActivationDate, key.ExpirationDate, key.IsRevoked)).ToArray(),
            };
            if (ringBefore != HomeContinuityCompatibilityReader.ComputeRingManifestSha256(repositoryPath!))
                throw new InvalidOperationException("The ring changed during metadata capture.");
            var status = string.IsNullOrWhiteSpace(applicationDiscriminator) ? "unproven" : "captured";
            return new DataProtectionRuntimeCaptureResult(
                Status: status,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Process: process,
                Instance: CaptureInstance(identity),
                DataProtection: metadata,
                ContentRoot: contentRoot);
        }
        catch
        {
            // A diagnostic must never turn an unreadable ring into a false source receipt or expose framework
            // paths/errors. The effective repository and discriminator are still returned as unproven facts.
            return new DataProtectionRuntimeCaptureResult(
                Status: "unavailable",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Process: process,
                Instance: CaptureInstance(identity),
                DataProtection: metadata,
                ContentRoot: contentRoot);
        }
    }

    private static DataProtectionRuntimeInstance CaptureInstance(AeroLinkRuntimeIdentity identity) =>
        new(identity.InstanceId, identity.Mode, identity.InstanceLabel, identity.InstanceClassification,
            identity.SourceSha, identity.SourceIdentity,
            identity.SourceIdentity == "unknown" ? "unproven" : "declared");

    private static DataProtectionProcessIdentity CaptureProcessIdentity()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            string? imagePath = null;
            try { imagePath = process.MainModule?.FileName; } catch { }
            return new DataProtectionProcessIdentity(
                process.Id, process.StartTime.ToUniversalTime(), imagePath, CurrentWindowsSid());
        }
        catch
        {
            return new DataProtectionProcessIdentity(Environment.ProcessId, null, null);
        }
    }

    private static string? CurrentWindowsSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch { return null; }
    }

    private static string ClassifyProtection(string? value) => string.IsNullOrWhiteSpace(value)
        ? "absent"
        : value.Trim().Equals("DpapiLocalMachine", StringComparison.OrdinalIgnoreCase)
            ? "DpapiLocalMachine"
            : "other";

    private static string MetadataDigest(IEnumerable<IKey> keys)
    {
        var canonical = string.Join('\n', keys.Select(key => string.Join('|',
            key.KeyId.ToString("D"),
            key.CreationDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            key.ActivationDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            key.ExpirationDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            key.IsRevoked ? "true" : "false")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record DataProtectionRuntimeCaptureResult(
    string Status,
    DateTimeOffset CapturedAtUtc,
    DataProtectionProcessIdentity Process,
    DataProtectionRuntimeInstance Instance,
    DataProtectionRuntimeMetadata DataProtection,
    string? ContentRoot = null,
    ConnectorRuntimeIdentityCapture? Connector = null,
    NotificationAuthorityRuntimeCapture? NotificationAuthority = null);

public sealed record DataProtectionProcessIdentity(
    int Pid,
    DateTimeOffset? StartedAtUtc,
    string? ImagePath,
    string? WindowsUserSid = null);

public sealed record DataProtectionRuntimeInstance(
    string InstanceId,
    string Mode,
    string InstanceLabel,
    string InstanceClassification,
    string SourceSha,
    string SourceIdentity,
    string SourceIdentityStatus);

public sealed record DataProtectionRuntimeMetadata(
    string? ApplicationDiscriminator,
    string ApplicationDiscriminatorSource,
    string RepositoryType,
    string? RepositoryPath,
    string RepositoryPathSource,
    string DeclaredProtection,
    string? XmlEncryptorType,
    bool AutoGenerateKeys,
    string? DeclaredKeyRingPath,
    int? KeyCount,
    int? RevokedKeyCount,
    string? KeyMetadataSha256,
    string? RevokedMetadataSha256,
    string? ObservedProviderSelection = null,
    string ProtectionScopeEvidence = "unknown",
    string? RingManifestSha256 = null,
    IReadOnlyList<DataProtectionRuntimeKeyMetadata>? Keys = null,
    string? DeclaredProtectionSource = null);

public sealed record DataProtectionRuntimeKeyMetadata(Guid KeyId, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ActivationAtUtc, DateTimeOffset ExpiresAtUtc, bool Revoked);

public sealed record NotificationAuthorityRuntimeCapture(string Status, string InstallationId, string HostIdentity,
    string? AuthorityPath, Guid? SendGeneration, Guid? PolicyRevision, string? MaximumMode, string? PolicySha256)
{
    public static NotificationAuthorityRuntimeCapture Capture(NotificationInstallationAuthority authority)
    {
        try
        {
            var (policy, hash) = authority.Read();
            return new(policy is not null ? "captured" : File.Exists(authority.AuthorityPath) ? "unproven" : "absent",
                authority.InstallationId, authority.HostIdentity, authority.AuthorityPath,
                policy?.SendGeneration, policy?.PolicyRevision, policy?.MaximumMode.ToString(), hash.Length == 0 ? null : hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("unavailable", authority.InstallationId, authority.HostIdentity, null, null, null, null, null);
        }
    }
}
