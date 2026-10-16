using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public const string NoApplicableValues = "no-applicable-values";
    public const string LegacyPlaintextPresent = "legacy-plaintext-present";
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
        var backup = VerifyBackupArchive(binding.VerifiedBackupArchivePath, binding.VerifiedBackupArchiveSha256, binding.SourceInstallationId);
        VerifyRestoreProof(binding, backup);
        var syntheticDatabase = AssertDisposableRestoredDatabase(db, binding.RestoredDatabaseName, binding.RestoredPostgresPort, binding.RestoredDatabaseOid);

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
            await SetReadOnlyAsync(db, syntheticDatabase, cancellationToken);
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
        var status = DetermineStatus(categories, binding.DiscoveryMode, ringBefore == ringAfter, syntheticDatabase);
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
        RequireExistingFile(binding.SourceConfigurationPath, nameof(binding.SourceConfigurationPath));
        RequireExistingDirectory(binding.SourceRingDirectory, nameof(binding.SourceRingDirectory));
        RequireExistingFile(binding.VerifiedBackupArchivePath, nameof(binding.VerifiedBackupArchivePath));
        RequireExistingFile(binding.RestoreProofPath, nameof(binding.RestoreProofPath));
        RequireExistingFile(binding.RestoreScriptPath, nameof(binding.RestoreScriptPath));
        if (!string.IsNullOrWhiteSpace(binding.RestoreOperationJournalPath))
        {
            RequireExistingFile(binding.RestoreOperationJournalPath, nameof(binding.RestoreOperationJournalPath));
            Require(binding.RestoreOperationId, nameof(binding.RestoreOperationId));
            Require(binding.RestoredDatabaseOid, nameof(binding.RestoredDatabaseOid));
            if (!binding.RestoredDatabaseOid.All(char.IsDigit))
                throw new ArgumentException("The restored database OID is invalid.", nameof(binding));
            using var journal = JsonDocument.Parse(File.ReadAllText(binding.RestoreOperationJournalPath));
            if (!TryGetString(journal.RootElement, "OperationId", out var operationId)
                || !string.Equals(operationId, binding.RestoreOperationId, StringComparison.Ordinal)
                || !TryGetString(journal.RootElement, "Status", out var operationStatus)
                || !string.Equals(operationStatus, "completed", StringComparison.Ordinal)
                || !TryGetString(journal.RootElement, "CreatedDatabaseOid", out var journalOid)
                || !string.Equals(journalOid, binding.RestoredDatabaseOid, StringComparison.Ordinal))
                throw new ArgumentException("The restored database operation journal is not a completed identity match.", nameof(binding));
        }
        Require(binding.SourceUserSid, nameof(binding.SourceUserSid));
        if (!binding.SourceUserSid.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The source Windows user SID is invalid.", nameof(binding));
        Require(binding.ReaderUserSid, nameof(binding.ReaderUserSid));
        if (!binding.ReaderUserSid.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The reader Windows user SID is invalid.", nameof(binding));
        RequireSha256(binding.SourceExecutableSha256, nameof(binding.SourceExecutableSha256));
        RequireSha256(binding.SourceConfigurationSha256, nameof(binding.SourceConfigurationSha256));
        RequireSha256(binding.SourceRingManifestSha256, nameof(binding.SourceRingManifestSha256));
        RequireSha256(binding.VerifiedBackupArchiveSha256, nameof(binding.VerifiedBackupArchiveSha256));
        RequireSha256(binding.ReaderSourceSha256, nameof(binding.ReaderSourceSha256));
        RequireSha256(binding.ReaderAssemblySha256, nameof(binding.ReaderAssemblySha256));
        Require(binding.SourceDiscriminator, nameof(binding.SourceDiscriminator));
        if (!Guid.TryParse(binding.SourceInstallationId, out var installationId)
            || installationId != binding.SourceInstanceId)
            throw new ArgumentException("The source installation and instance identities do not match.", nameof(binding));
        if (binding.SourceProcessAttestation is not ("operator-attested-frozen" or "reader-process"))
            throw new ArgumentException("Source process attestation must identify an operator-frozen source or the reader process.", nameof(binding));
        if (!string.Equals(binding.SourceProtectionScopeEvidence, UnknownProtectionScope, StringComparison.Ordinal)
            && binding.SourceProtectionScopeEvidence is not ("DpapiCurrentUser" or "DpapiLocalMachine" or "Unprotected"))
            throw new ArgumentException("Protection scope evidence must be unknown or a supported explicit value.", nameof(binding));
        if (binding.SourceProviderSelection is not ("WindowsDpapi" or "DpapiCurrentUser" or "DpapiLocalMachine" or "Unprotected"))
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
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!string.Equals(identity.User?.Value, binding.ReaderUserSid, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The reader Windows SID does not match the current process.", nameof(binding));
        }
        if (!string.Equals(Sha256File(binding.SourceExecutablePath), binding.SourceExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source executable hash does not match the captured file.", nameof(binding));
        if (!string.Equals(Sha256File(binding.SourceConfigurationPath), binding.SourceConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source configuration hash does not match the captured file.", nameof(binding));
        var assemblyPath = GetReaderAssemblyPath();
        if (!string.Equals(Sha256File(assemblyPath), binding.ReaderAssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The compatibility reader assembly hash does not match the captured source identity.", nameof(binding));
        using (var process = Process.GetCurrentProcess())
        {
            if (binding.SourceProcessAttestation == "reader-process"
                && (binding.SourceProcessId != process.Id || process.StartTime.ToUniversalTime() != binding.SourceProcessStartedUtc))
                throw new ArgumentException("The reader-process source attestation does not match the current process.", nameof(binding));
        }
        RequireNoReparsePoints(binding.SourceRingDirectory);
        RequireNoReparsePoints(binding.SourceContentRoot);
        RequireNoReparsePoints(Path.GetDirectoryName(Path.GetFullPath(binding.RestoreProofPath))!);
    }

    /// <summary>
    /// Validates the shape and source bindings of a retained receipt. Export still runs a fresh probe; this method
    /// only prevents malformed, incomplete or obviously substituted JSON from becoming its input context.
    /// </summary>
    public static void ValidateReceipt(HomeContinuityCompatibilityReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateBinding(receipt.Binding);
        if (receipt.Status is not (CompatibilityProof or MetadataCaptured or Unproven or Refused or NoApplicableValues or LegacyPlaintextPresent))
            throw new ArgumentException("The compatibility receipt status is unsupported.", nameof(receipt));
        if (!string.Equals(receipt.DiscriminatorEvidence, CandidateDiscriminatorCompatibility, StringComparison.Ordinal)
            || !string.Equals(receipt.DiscoveryMode, receipt.Binding.DiscoveryMode, StringComparison.Ordinal)
            || !string.Equals(receipt.RingManifestSha256Before, receipt.RingManifestSha256After, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.RingManifestSha256Before, ComputeRingManifestSha256(receipt.Binding.SourceRingDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The compatibility receipt ring or discovery binding is not current.", nameof(receipt));
        var backup = VerifyBackupArchive(receipt.Binding.VerifiedBackupArchivePath,
            receipt.Binding.VerifiedBackupArchiveSha256, receipt.Binding.SourceInstallationId);
        VerifyRestoreProof(receipt.Binding, backup);

        if (receipt.ProtectedCategories.Count != Catalog.Count
            || receipt.ProtectedCategories.Select(x => x.Definition.Category).ToHashSet(StringComparer.Ordinal)
                .SetEquals(Catalog.Select(x => x.Category)) == false)
            throw new ArgumentException("The compatibility receipt does not contain the complete compiled catalog.", nameof(receipt));
        foreach (var category in receipt.ProtectedCategories)
        {
            var expected = Catalog.Single(x => x.Category == category.Definition.Category);
            if (category.Definition != expected
                || category.RowCount < 0 || category.EmptyCount < 0 || category.PassedCount < 0
                || category.FailedCount < 0 || category.LegacyPlaintextCount < 0
                || category.EmptyCount + category.PassedCount + category.FailedCount + category.LegacyPlaintextCount > category.RowCount
                || category.Fields.Count != category.PassedCount + category.FailedCount + category.LegacyPlaintextCount)
                throw new ArgumentException("The compatibility receipt contains an incomplete catalog category.", nameof(receipt));
            if (category.Status is not ("passed" or "failed" or "legacy-plaintext" or NotApplicable or Unproven))
                throw new ArgumentException("The compatibility receipt contains an unsupported category status.", nameof(receipt));
            foreach (var field in category.Fields)
                if (field.Category != expected.Category || field.Table != expected.Table || field.Column != expected.Column
                    || field.Status is not ("passed" or "failed" or "legacy-plaintext") || !IsSha256(field.CiphertextSha256))
                    throw new ArgumentException("The compatibility receipt contains an invalid field result.", nameof(receipt));
        }
        var external = receipt.ExternalCategories.ToDictionary(x => x.Category, StringComparer.Ordinal);
        if (external.Count != ExternalCategories.Count || ExternalCategories.Any(x => !external.ContainsKey(x)))
            throw new ArgumentException("The compatibility receipt does not contain all external-store categories.", nameof(receipt));
        if (external.Values.Any(x => x.Status is not ("absent" or "configured" or "separate-reentry-required")))
            throw new ArgumentException("The compatibility receipt contains an unsupported external-store status.", nameof(receipt));
        if (receipt.Status == CompatibilityProof
            && (receipt.ProtectedCategories.Any(x => x.Status is "failed" or "legacy-plaintext" or Unproven)
                || receipt.ProtectedCategories.All(x => x.Status == NotApplicable)))
            throw new ArgumentException("The compatibility receipt claims cryptographic proof without complete passing durable values.", nameof(receipt));
        var reader = receipt.Reader;
        if (!File.Exists(reader.ImagePath) || !string.Equals(Sha256File(reader.ImagePath), reader.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Sha256File(GetReaderAssemblyPath()), reader.AssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The compatibility receipt reader image hash is not current.", nameof(receipt));
    }

    public static bool AssertDisposableRestoredDatabase(AeroLinkDbContext db, string expectedDatabaseName, int expectedPort, string expectedDatabaseOid = "")
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
            if (connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("The compatibility probe refuses a remote or unowned PostgreSQL host.");
            if (!string.IsNullOrWhiteSpace(expectedDatabaseOid))
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT oid::text FROM pg_database WHERE datname = current_database();";
                db.Database.OpenConnection();
                try
                {
                    var actualOid = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(actualOid, expectedDatabaseOid, StringComparison.Ordinal))
                        throw new InvalidOperationException("The restored database identity no longer matches the product-owned restore operation.");
                }
                finally { db.Database.CloseConnection(); }
            }
            return false;
        }
        else if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compatibility probe supports only a disposable PostgreSQL or SQLite test database.");
        return true;
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
        var status = failed > 0 ? "failed" : legacy > 0 ? "legacy-plaintext" : passed == 0 ? NotApplicable : "passed";
        return new HomeContinuityProtectedCategoryReceipt(definition, rows.Count, empty, passed, failed, legacy, status, results);
    }

    private static string DetermineStatus(
        IReadOnlyList<HomeContinuityProtectedCategoryReceipt> categories,
        string discoveryMode,
        bool ringStable,
        bool syntheticDatabase)
    {
        if (!ringStable || !string.Equals(discoveryMode, ExplicitRingRead, StringComparison.Ordinal)) return Refused;
        if (categories.Any(x => x.Status == "failed")) return Unproven;
        if (categories.Any(x => x.Status == "legacy-plaintext")) return LegacyPlaintextPresent;
        if (categories.All(x => x.Status == NotApplicable)) return NoApplicableValues;
        if (syntheticDatabase) return MetadataCaptured;
        return CompatibilityProof;
    }

    private static HomeContinuityReaderIdentity CaptureReaderIdentity(string expectedSourceSha)
    {
        using var process = Process.GetCurrentProcess();
        var image = process.MainModule?.FileName ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image)) throw new InvalidOperationException("The compatibility reader image identity is unavailable.");
        var actualSourceSha = Sha256File(image);
        if (!string.Equals(actualSourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compatibility reader image hash does not match its bound source identity.");
        var assemblyPath = GetReaderAssemblyPath();
        return new HomeContinuityReaderIdentity(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(image), actualSourceSha,
            Sha256File(assemblyPath), typeof(HomeContinuityCompatibilityReader).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    private static string GetReaderAssemblyPath()
    {
        var path = typeof(HomeContinuityCompatibilityReader).Assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The compatibility reader assembly identity is unavailable.");
        return Path.GetFullPath(path);
    }

    private static async Task SetReadOnlyAsync(AeroLinkDbContext db, bool syntheticDatabase, CancellationToken cancellationToken)
    {
        if (syntheticDatabase)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA query_only = ON", cancellationToken);
            return;
        }
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
    }

    public static HomeContinuityVerifiedBackup VerifyBackupArchive(
        string archivePath, string expectedArchiveSha256, string expectedSourceInstallationId)
    {
        RequireExistingFile(archivePath, nameof(archivePath));
        if (!string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity backup must be a ZIP archive.");
        var actual = Sha256File(archivePath);
        if (!string.Equals(actual, expectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity backup archive hash does not match its bound identity.");
        var sidecar = archivePath + ".sha256";
        RequireExistingFile(sidecar, "archive sidecar");
        var sidecarHash = File.ReadAllText(sidecar).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(sidecarHash, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity backup archive sidecar does not match the archive.");

        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("The continuity backup has no manifest.");
        using var manifestReader = new StreamReader(manifestEntry.Open());
        using var manifest = JsonDocument.Parse(manifestReader.ReadToEnd());
        var root = manifest.RootElement;
        if (!TryGetInt(root, "FormatVersion", out var format) || format != 2)
            throw new InvalidOperationException("The continuity backup manifest format is unsupported.");
        if (!TryGetString(root, "Instance", "Classification", out var classification)
            || !string.Equals(classification, "HomeCanonical", StringComparison.Ordinal)
            || !TryGetString(root, "Instance", "InstanceId", out var instanceId)
            || !string.Equals(instanceId, expectedSourceInstallationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity backup manifest does not identify the bound HOME source instance.");
        if (archive.GetEntry("aerolink-postgresql.dump") is null)
            throw new InvalidOperationException("The continuity backup has no PostgreSQL dump.");
        return new HomeContinuityVerifiedBackup(actual, instanceId,
            TryGetString(root, "Application", "SourceSha", out var sourceSha) ? sourceSha : null);
    }

    private static void VerifyRestoreProof(HomeContinuityCompatibilityBinding binding, HomeContinuityVerifiedBackup backup)
    {
        var proofPath = Path.GetFullPath(binding.RestoreProofPath);
        using var document = JsonDocument.Parse(File.ReadAllText(proofPath));
        var root = document.RootElement;
        if (!TryGetInt(root, "FormatVersion", out var format) || format != 1
            || !TryGetString(root, "ArchiveSha256", out var proofHash)
            || !string.Equals(proofHash, backup.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
            || !TryGetString(root, "DatabaseName", out var database)
            || !string.Equals(database, binding.RestoredDatabaseName, StringComparison.Ordinal)
            || !TryGetInt(root, "PostgresPort", out var port) || port != binding.RestoredPostgresPort
            || !root.TryGetProperty("AttachmentInventoryVerified", out var inventory) || inventory.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("The continuity restore proof is missing or does not bind the verified archive and disposable database.");
        if (!TryGetString(root, "RestoreScriptSha256", out var scriptHash)
            || !string.Equals(Sha256File(binding.RestoreScriptPath), scriptHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity restore proof was not produced by the bound restore script.");
        var validationRoot = TryGetString(root, "RestoreValidationRoot", out var rootValue)
            ? Path.GetFullPath(rootValue!) + Path.DirectorySeparatorChar
            : throw new InvalidOperationException("The continuity restore proof has no validation root.");
        if (!proofPath.StartsWith(validationRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity restore proof is outside the owned validation root.");
        if (backup.InstanceId is null || !string.Equals(backup.InstanceId, binding.SourceInstallationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified archive instance is not bound to the continuity source.");
        if (!string.IsNullOrWhiteSpace(binding.RestoreOperationJournalPath))
        {
            if (!TryGetString(root, "OperationId", out var operationId)
                || !string.Equals(operationId, binding.RestoreOperationId, StringComparison.Ordinal)
                || !TryGetString(root, "OperationJournalPath", out var operationJournalPath)
                || !string.Equals(Path.GetFullPath(operationJournalPath), Path.GetFullPath(binding.RestoreOperationJournalPath), StringComparison.OrdinalIgnoreCase)
                || !TryGetString(root, "DatabaseOid", out var databaseOid)
                || !string.Equals(databaseOid, binding.RestoredDatabaseOid, StringComparison.Ordinal)
                || !root.TryGetProperty("PostgresRuntimeAfter", out var runtimeAfter)
                || runtimeAfter.ValueKind != JsonValueKind.Object
                || !TryGetString(runtimeAfter, "DataDirectory", out _)
                || !runtimeAfter.TryGetProperty("Postmaster", out var postmaster)
                || !TryGetString(postmaster, "ImageSha256", out _))
                throw new InvalidOperationException("The continuity restore proof does not contain the product-owned PostgreSQL operation identity.");
        }
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        if (root.TryGetProperty(name, out var element) && element.TryGetInt32(out value)) return true;
        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        value = null;
        return false;
    }

    private static bool TryGetString(JsonElement root, string parent, string name, out string? value)
    {
        if (root.TryGetProperty(parent, out var objectElement)) return TryGetString(objectElement, name, out value);
        value = null;
        return false;
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

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

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
    int ProcessId, DateTimeOffset ProcessStartedUtc, string ImagePath, string SourceSha256, string AssemblySha256, string RuntimeVersion);

public sealed record HomeContinuityCompatibilityBinding(
    string SourceInstallationId,
    Guid SourceInstanceId,
    int SourceProcessId,
    DateTimeOffset SourceProcessStartedUtc,
    string SourceExecutablePath,
    string SourceExecutableSha256,
    string SourceContentRoot,
    string SourceConfigurationPath,
    string SourceUserSid,
    string ReaderUserSid,
    string SourceConfigurationSha256,
    string SourceRingDirectory,
    string SourceRingManifestSha256,
    string SourceDiscriminator,
    string SourceProtectionScopeEvidence,
    string SourceProviderSelection,
    string VerifiedBackupId,
    string VerifiedBackupArchivePath,
    string VerifiedBackupArchiveSha256,
    string RestoredDatabaseName,
    int RestoredPostgresPort,
    string RestoreProofPath,
    string RestoreScriptPath,
    string ReaderSourceSha256,
    string ReaderAssemblySha256,
    string DiscoveryMode,
    string SourceProcessAttestation,
    Dictionary<string, string> ExternalStoreStates,
    string RestoreOperationJournalPath = "",
    string RestoreOperationId = "",
    string RestoredDatabaseOid = "");

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

public sealed record HomeContinuityVerifiedBackup(
    string ArchiveSha256, string? InstanceId, string? ApplicationSourceSha);
