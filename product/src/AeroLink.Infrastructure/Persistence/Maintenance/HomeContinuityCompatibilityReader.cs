using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AeroLink.Infrastructure.Persistence.Maintenance;

/// <summary>
/// A read-only source-compatible reader for an attended HOME continuity operation.
///
/// This is a new process and a candidate-provider proof. It never claims to capture the old process's hidden
/// Data Protection composition. A complete result is accepted only when every durable protected value in the
/// source-owned catalog is unprotected in memory and all binding/ring invariants remain stable.
/// </summary>
public static class HomeContinuityCompatibilityReader
{
    public const string CompatibilityProof = "compatibility-proof";
    public const string MetadataCaptured = "metadata-captured";
    public const string Unproven = "unproven";
    public const string Refused = "refused";
    public const string NotApplicable = "NOT_APPLICABLE";
    public const string CandidateDiscriminatorCompatibility = "candidate-discriminator-compatibility";
    public const string ExplicitRingRead = "explicit-ring-read";
    public const string UnknownProtectionScope = "unknown";
    public const string NotificationPurpose = "AeroLink.Notifications.ProtectedEvidence.v1";

    private static readonly IReadOnlyList<HomeContinuityProtectedFieldDefinition> Catalog =
    [
        new("mfa-secret", "user_mfa_enrollments", "Secret", "AeroLink.Identity.MfaSecret.v1", "dp:v1:"),
        new("webhook-secret", "webhook_subscriptions", "ProtectedSecret", "AeroLink.Webhooks.SigningSecret.v1", null),
        new("jira-token", "jira_connections", "ProtectedApiToken", "AeroLink.Jira.ApiToken.v1", null),
        new("notification-credential", "notification_settings_revisions", "ProtectedCredential", NotificationPurpose, null),
        new("notification-address", "notification_delivery_generations", "ProtectedAddress", NotificationPurpose, null),
        new("notification-mime", "notification_delivery_generations", "ProtectedMime", NotificationPurpose, null),
        new("notification-blocked-settings", "notification_delivery_generations", "BlockedEffectiveSettingsHash", NotificationPurpose, null),
        new("notification-settings-snapshot", "notification_physical_attempts", "ProtectedSettingsSnapshot", NotificationPurpose, null),
        new("notification-effective-settings", "notification_physical_attempts", "EffectiveSettingsHash", NotificationPurpose, null),
        new("notification-operation-payload", "notification_operations", "PayloadHash", NotificationPurpose, null),
    ];

    private static readonly IReadOnlyList<string> ReissueCategories =
    [
        "user_sessions.TokenHash (one-way session hash)",
        "document_connector_grants.LaunchTokenHash/AccessTokenHash (one-way, time-bound)",
        "notification_preference_confirmations.ChallengeHash (one-way, time-bound)",
        "mfa_recovery_codes.CodeHash (one-way recovery hash)",
        "integration_service_identities.ApiKeyHash (one-way API-key hash)",
        "BrowserMutation, session, antiforgery, Windows-link, and notification-confirmation tokens (ephemeral; reissue)",
    ];

    private static readonly IReadOnlyList<string> ExternalCategories =
    [
        "gitlab-protected-config",
        "esri-imagery-protected-config",
        "connector-signing-identity",
        "notification-authority",
    ];

    public static IReadOnlyList<HomeContinuityProtectedFieldDefinition> ProtectedFieldCatalog => Catalog;
    public static IReadOnlyList<string> ReissueOnlyCategories => ReissueCategories;
    public static IReadOnlyList<string> ExternalProtectedCategories => ExternalCategories;

    /// <summary>
    /// Enumerates the complete compiled catalog against an already restored, owned database. The transaction is
    /// rolled back and the source service provider must have automatic key generation disabled.
    /// </summary>
    public static async Task<HomeContinuityCompatibilityReceipt> ProbeAsync(
        AeroLinkDbContext db,
        IServiceProvider sourceServices,
        HomeContinuityCompatibilityBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sourceServices);
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        AssertDisposableRestoredDatabase(db, binding.RestoredDatabaseName, binding.RestoredPostgresPort);

        var ringBefore = ComputeRingManifestSha256(binding.SourceRingDirectory);
        if (!string.Equals(ringBefore, binding.SourceRingManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The captured source Data Protection ring does not match its bound manifest digest.");

        var dataProtectionOptions = sourceServices.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        if (!string.Equals(dataProtectionOptions.ApplicationDiscriminator, binding.SourceDiscriminator, StringComparison.Ordinal))
            throw new InvalidOperationException("The source provider discriminator does not match the attended binding.");
        var keyManagement = sourceServices.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        if (keyManagement.AutoGenerateKeys)
            throw new InvalidOperationException("The compatibility reader requires automatic key generation to be disabled.");
        var keyManager = sourceServices.GetRequiredService<IKeyManager>();
        var keys = keyManager.GetAllKeys().ToArray();
        if (keys.Length == 0)
            throw new InvalidOperationException("The captured source Data Protection ring contains no keys.");
        var provider = sourceServices.GetRequiredService<IDataProtectionProvider>();

        var categories = new List<HomeContinuityProtectedCategoryReceipt>(Catalog.Count);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            categories.Add(await InspectAsync(Catalog[0],
                await db.UserMfaEnrollments.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.Secret)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[0].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[1],
                await db.WebhookSubscriptions.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedSecret)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[1].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[2],
                await db.JiraConnections.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedApiToken)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[2].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[3],
                await db.NotificationSettingsRevisions.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedCredential)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[3].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[4],
                await db.NotificationDeliveryGenerations.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedAddress)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[4].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[5],
                await db.NotificationDeliveryGenerations.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedMime)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[5].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[6],
                await db.NotificationDeliveryGenerations.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.BlockedEffectiveSettingsHash)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[6].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[7],
                await db.NotificationPhysicalAttempts.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.ProtectedSettingsSnapshot)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[7].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[8],
                await db.NotificationPhysicalAttempts.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.EffectiveSettingsHash)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[8].Purpose), cancellationToken));
            categories.Add(await InspectAsync(Catalog[9],
                await db.NotificationOperations.AsNoTracking().Select(x => new ProtectedFieldRow(x.Id.ToString("D"), x.PayloadHash)).ToListAsync(cancellationToken),
                provider.CreateProtector(Catalog[9].Purpose), cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }

        var ringAfter = ComputeRingManifestSha256(binding.SourceRingDirectory);
        if (!string.Equals(ringBefore, ringAfter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source Data Protection ring changed during the compatibility probe.");

        var external = ExternalCategories
            .Select(name => new HomeContinuityExternalCategoryReceipt(name,
                binding.ExternalStoreStates.TryGetValue(name, out var state) ? state : "separate-reentry-required"))
            .ToArray();
        var reader = CaptureReaderIdentity(binding.ReaderSourceSha256);
        var status = DetermineStatus(categories, external, binding.DiscoveryMode, ringBefore == ringAfter);
        return new HomeContinuityCompatibilityReceipt(
            status,
            DateTimeOffset.UtcNow,
            CandidateDiscriminatorCompatibility,
            binding.DiscoveryMode,
            binding.SourceProtectionScopeEvidence,
            binding,
            reader,
            ringBefore,
            ringAfter,
            categories,
            external,
            ReissueCategories);
    }

    public static void ValidateBinding(HomeContinuityCompatibilityBinding binding)
    {
        Require(binding.SourceInstallationId, nameof(binding.SourceInstallationId));
        if (binding.SourceInstanceId == Guid.Empty) throw new ArgumentException("The source instance ID is required.", nameof(binding));
        if (binding.SourceProcessId <= 0 || binding.SourceProcessStartedUtc == default) throw new ArgumentException("The old process identity is incomplete.", nameof(binding));
        RequireExistingFile(binding.SourceExecutablePath, nameof(binding.SourceExecutablePath));
        RequireExistingDirectory(binding.SourceContentRoot, nameof(binding.SourceContentRoot));
        RequireExistingDirectory(binding.SourceRingDirectory, nameof(binding.SourceRingDirectory));
        Require(binding.SourceUserSid, nameof(binding.SourceUserSid));
        if (!binding.SourceUserSid.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The source Windows user SID is invalid.", nameof(binding));
        RequireSha256(binding.SourceExecutableSha256, nameof(binding.SourceExecutableSha256));
        RequireSha256(binding.SourceConfigurationSha256, nameof(binding.SourceConfigurationSha256));
        RequireSha256(binding.SourceRingManifestSha256, nameof(binding.SourceRingManifestSha256));
        RequireSha256(binding.VerifiedBackupArchiveSha256, nameof(binding.VerifiedBackupArchiveSha256));
        RequireSha256(binding.ReaderSourceSha256, nameof(binding.ReaderSourceSha256));
        Require(binding.SourceDiscriminator, nameof(binding.SourceDiscriminator));
        if (!string.Equals(binding.SourceProtectionScopeEvidence, UnknownProtectionScope, StringComparison.Ordinal)
            && binding.SourceProtectionScopeEvidence is not ("DpapiCurrentUser" or "DpapiLocalMachine" or "Unprotected"))
            throw new ArgumentException("Protection scope evidence must be unknown or a supported explicit value.", nameof(binding));
        if (binding.SourceProviderSelection is not ("DpapiCurrentUser" or "DpapiLocalMachine" or "Unprotected"))
            throw new ArgumentException("The source provider selection is unsupported.", nameof(binding));
        if (!string.Equals(binding.DiscoveryMode, ExplicitRingRead, StringComparison.Ordinal))
            throw new ArgumentException("Only the explicit ring-read discovery mode is implemented; fallback observations require their pinned public event schema.", nameof(binding));
        Require(binding.VerifiedBackupId, nameof(binding.VerifiedBackupId));
        Require(binding.RestoredDatabaseName, nameof(binding.RestoredDatabaseName));
        if (binding.RestoredPostgresPort is <= 0 or 54329) throw new ArgumentException("The restored database must use an owned non-54329 PostgreSQL port.", nameof(binding));
        if (!binding.RestoredDatabaseName.Contains("restore", StringComparison.OrdinalIgnoreCase)
            && !binding.RestoredDatabaseName.Contains("validation", StringComparison.OrdinalIgnoreCase)
            && !binding.RestoredDatabaseName.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The restored database name must identify disposable restore/validation/test state.", nameof(binding));
        if (binding.ExternalStoreStates is null) throw new ArgumentException("External protected-store states are required; an omitted category cannot pass.", nameof(binding));
        foreach (var category in ExternalCategories)
            if (binding.ExternalStoreStates.TryGetValue(category, out var state)
                && state is not ("absent" or "configured" or "separate-reentry-required"))
                throw new ArgumentException($"External protected-store state '{state}' is unsupported.", nameof(binding));
        RequireNoReparsePoints(binding.SourceRingDirectory);
        RequireNoReparsePoints(binding.SourceContentRoot);
    }

    public static void AssertDisposableRestoredDatabase(AeroLinkDbContext db, string expectedDatabaseName, int expectedPort)
    {
        ArgumentNullException.ThrowIfNull(db);
        Require(expectedDatabaseName, nameof(expectedDatabaseName));
        if (expectedPort is <= 0 or 54329) throw new InvalidOperationException("A compatibility probe cannot use persistent PostgreSQL port 54329.");
        if (!expectedDatabaseName.Contains("restore", StringComparison.OrdinalIgnoreCase)
            && !expectedDatabaseName.Contains("validation", StringComparison.OrdinalIgnoreCase)
            && !expectedDatabaseName.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compatibility probe requires a disposable restore/validation/test database name.");

        var provider = db.Database.ProviderName ?? "";
        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("The restored PostgreSQL connection is not configured.");
            var connection = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.Equals(connection.Database, expectedDatabaseName, StringComparison.Ordinal)
                || connection.Port != expectedPort)
                throw new InvalidOperationException("The configured database does not match the bound disposable restore identity.");
        }
        else if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compatibility probe supports only a disposable PostgreSQL or SQLite test database.");
    }

    public static string ComputeRingManifestSha256(string root)
    {
        RequireExistingDirectory(root, nameof(root));
        RequireNoReparsePoints(root);
        var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return $"{relative}\t{info.Length}\t{Sha256File(path)}";
            })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static async Task<HomeContinuityProtectedCategoryReceipt> InspectAsync(
        HomeContinuityProtectedFieldDefinition definition,
        IReadOnlyList<ProtectedFieldRow> rows,
        IDataProtector protector,
        CancellationToken cancellationToken)
    {
        var results = new List<HomeContinuityProtectedFieldResult>();
        var empty = 0;
        var passed = 0;
        var failed = 0;
        var legacy = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = row.Value ?? "";
            if (value.Length == 0)
            {
                empty++;
                continue;
            }
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            if (definition.RequiredPrefix is not null && !value.StartsWith(definition.RequiredPrefix, StringComparison.Ordinal))
            {
                legacy++;
                results.Add(new(definition.Category, definition.Table, definition.Column, row.RecordId, digest, "legacy-plaintext"));
                continue;
            }
            try
            {
                _ = protector.Unprotect(definition.RequiredPrefix is null ? value : value[definition.RequiredPrefix.Length..]);
                passed++;
                results.Add(new(definition.Category, definition.Table, definition.Column, row.RecordId, digest, "passed"));
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                failed++;
                results.Add(new(definition.Category, definition.Table, definition.Column, row.RecordId, digest, "failed"));
            }
        }
        var status = failed > 0 ? "failed" : legacy > 0 ? Unproven : passed == 0 ? NotApplicable : "passed";
        return new HomeContinuityProtectedCategoryReceipt(definition, rows.Count, empty, passed, failed, legacy, status, results);
    }

    private static string DetermineStatus(
        IReadOnlyList<HomeContinuityProtectedCategoryReceipt> categories,
        IReadOnlyList<HomeContinuityExternalCategoryReceipt> external,
        string discoveryMode,
        bool ringStable)
    {
        if (!ringStable || !string.Equals(discoveryMode, ExplicitRingRead, StringComparison.Ordinal)) return Refused;
        if (categories.Any(x => x.Status is "failed" or "legacy-plaintext" or Unproven)) return Unproven;
        if (external.Any(x => !string.Equals(x.Status, "absent", StringComparison.Ordinal))) return Unproven;
        return CompatibilityProof;
    }

    private static HomeContinuityReaderIdentity CaptureReaderIdentity(string sourceSha)
    {
        using var process = Process.GetCurrentProcess();
        var image = process.MainModule?.FileName ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image)) throw new InvalidOperationException("The compatibility reader image identity is unavailable.");
        return new HomeContinuityReaderIdentity(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(image), sourceSha,
            typeof(HomeContinuityCompatibilityReader).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static void RequireSha256(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException($"{name} must be a SHA-256 digest.", name);
    }

    private static void RequireExistingFile(string path, string name)
    {
        Require(path, name);
        if (!File.Exists(path)) throw new ArgumentException($"{name} does not identify an existing file.", name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"{name} must not be a reparse point.", name);
    }

    private static void RequireExistingDirectory(string path, string name)
    {
        Require(path, name);
        if (!Directory.Exists(path)) throw new ArgumentException($"{name} does not identify an existing directory.", name);
    }

    private static void RequireNoReparsePoints(string root)
    {
        RequireExistingDirectory(root, nameof(root));
        var fullRoot = Path.GetFullPath(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The compatibility reader refuses a reparse point under '{fullRoot}'.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record ProtectedFieldRow(string RecordId, string Value);
}

public sealed record HomeContinuityProtectedFieldDefinition(
    string Category, string Table, string Column, string Purpose, string? RequiredPrefix);

public sealed record HomeContinuityProtectedFieldResult(
    string Category, string Table, string Column, string RecordId, string CiphertextSha256, string Status);

public sealed record HomeContinuityProtectedCategoryReceipt(
    HomeContinuityProtectedFieldDefinition Definition,
    int RowCount,
    int EmptyCount,
    int PassedCount,
    int FailedCount,
    int LegacyPlaintextCount,
    string Status,
    IReadOnlyList<HomeContinuityProtectedFieldResult> Fields);

public sealed record HomeContinuityExternalCategoryReceipt(string Category, string Status);

public sealed record HomeContinuityReaderIdentity(
    int ProcessId, DateTimeOffset ProcessStartedUtc, string ImagePath, string SourceSha256, string RuntimeVersion);

public sealed record HomeContinuityCompatibilityBinding(
    string SourceInstallationId,
    Guid SourceInstanceId,
    int SourceProcessId,
    DateTimeOffset SourceProcessStartedUtc,
    string SourceExecutablePath,
    string SourceExecutableSha256,
    string SourceContentRoot,
    string SourceUserSid,
    string SourceConfigurationSha256,
    string SourceRingDirectory,
    string SourceRingManifestSha256,
    string SourceDiscriminator,
    string SourceProtectionScopeEvidence,
    string SourceProviderSelection,
    string VerifiedBackupId,
    string VerifiedBackupArchiveSha256,
    string RestoredDatabaseName,
    int RestoredPostgresPort,
    string ReaderSourceSha256,
    string DiscoveryMode,
    Dictionary<string, string> ExternalStoreStates);

public sealed record HomeContinuityCompatibilityReceipt(
    string Status,
    DateTimeOffset CreatedAtUtc,
    string DiscriminatorEvidence,
    string DiscoveryMode,
    string ProtectionScopeEvidence,
    HomeContinuityCompatibilityBinding Binding,
    HomeContinuityReaderIdentity Reader,
    string RingManifestSha256Before,
    string RingManifestSha256After,
    IReadOnlyList<HomeContinuityProtectedCategoryReceipt> ProtectedCategories,
    IReadOnlyList<HomeContinuityExternalCategoryReceipt> ExternalCategories,
    IReadOnlyList<string> ReissueCategories);
