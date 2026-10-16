using AeroLink.ConnectorProtocol;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;

namespace AeroLink.Api;

/// <summary>
/// The maintenance mode of the AeroLink application host: <c>AeroLink.Api maintenance &lt;command&gt;</c>.
///
/// #881 allows either a separate console host or a deliberate mode of the existing one. This is the second,
/// on purpose. The analysis and the resolution have to use the same domain, the same persistence
/// configuration and the same migration authorities that startup uses, and the surest way to guarantee that
/// is for them to be the same executable with the same composition — not a sibling project that shares an
/// assembly reference and drifts in configuration.
///
/// No web server is started, no port is bound and no hosted worker runs. That is the point: an operator with
/// an old database gets an answer in seconds instead of after a readiness timeout.
///
/// Commands:
///   maintenance analyze [--json]
///       Read-only. Reports pending schema migrations, pending semantic upgrades, and every modelled
///       conflict, with the supported decisions for each. Writes nothing.
///
///   maintenance resolve --conflict &lt;code&gt; --choice &lt;key&gt; --program &lt;guid&gt; --position &lt;name&gt;
///                       --person &lt;guid&gt; --legacy-backup &lt;guid&gt; --operator &lt;reference&gt;
///                       [--expect-primary &lt;guid|none&gt;] [--apply]
///       Dry run unless --apply is given. Preconditions are re-read immediately before any write.
///
///   maintenance home-continuity export --package &lt;new-directory&gt; --source-installation &lt;id&gt;
///       --source-discriminator &lt;exact-value&gt; --source-protection &lt;provider&gt; --verified-backup &lt;id&gt;
///       --connector-deployment &lt;id&gt; --connector-key &lt;id&gt; --target-application AeroLink
///       --verified-backup-archive &lt;verified-backup.zip&gt;
///       --certificate &lt;public-cert.pem|cer&gt; --source-ring &lt;captured-directory&gt; --compatibility-receipt &lt;receipt.json&gt;
///       --restore-postgres-bin &lt;postgres-bin-directory&gt;
///       Exports an identity-bound, certificate-wrapped ring package without printing key material.
///
///   maintenance home-continuity apply --package &lt;directory&gt; --destination-ring &lt;new-directory&gt;
///       --source-installation &lt;id&gt; --source-discriminator &lt;exact-value&gt; --source-protection &lt;provider&gt;
///       --verified-backup &lt;sha256&gt; --connector-deployment &lt;id&gt; --connector-key &lt;id&gt;
///       --connector-public-fingerprint &lt;sha256&gt; --verified-backup-archive-sha256 &lt;sha256&gt; --manifest-sha256 &lt;hash&gt;
///       --certificate &lt;cert.pem|pfx&gt; [--certificate-key &lt;private-key.pem&gt;]
///       Rewraps the package with LocalMachine DPAPI and refuses a non-empty or incorrectly ACLed destination ring.
///
///   maintenance home-continuity recover --action complete|abort --package &lt;directory&gt;
///       --destination-ring &lt;directory&gt; --package-id &lt;guid&gt; --manifest-sha256 &lt;hash&gt;
///       --source-installation &lt;id&gt; --source-discriminator &lt;exact-value&gt; --source-protection &lt;provider&gt;
///       --verified-backup &lt;sha256&gt; --connector-deployment &lt;id&gt; --connector-key &lt;id&gt;
///       --connector-public-fingerprint &lt;sha256&gt; --verified-backup-archive-sha256 &lt;sha256&gt;
///       Completes or compensates only the exact receipt-bound partial transfer; mismatches refuse all cleanup.
///
///   maintenance home-continuity probe --binding &lt;binding.json&gt; --receipt &lt;receipt.json&gt;
///       Reads only an already restored disposable database and a frozen source ring. The compiled
///       protected-field catalog cannot be supplied or narrowed by the caller.
/// </summary>
public static class AeroLinkMaintenanceHost
{
    /// <summary>True when the process was invoked as the maintenance host rather than as the web API.</summary>
    public static bool IsMaintenanceInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "maintenance", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("AeroLink maintenance host");
            Console.WriteLine("  maintenance analyze [--json]");
            Console.WriteLine("  maintenance upgrade [--apply]");
            Console.WriteLine("  maintenance resolve --conflict <code> --choice <key> --program <guid> --position <name> --person <guid> --legacy-backup <guid> --operator <reference> [--expect-primary <guid|none>] [--apply]");
            Console.WriteLine("  maintenance home-continuity export --package <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive <verified-backup.zip> --connector-deployment <id> --connector-key <id> --target-application AeroLink --certificate <public-cert.pem|cer> --source-ring <captured-directory> --compatibility-receipt <receipt.json> --restore-postgres-bin <postgres-bin-directory>");
            Console.WriteLine("  maintenance home-continuity apply --package <directory> --destination-ring <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive-sha256 <sha256> --connector-deployment <id> --connector-key <id> --connector-public-fingerprint <sha256> --manifest-sha256 <hash> --certificate <cert.pem|pfx> [--certificate-key <private-key.pem>]");
            Console.WriteLine("  maintenance home-continuity recover --action complete|abort --package <directory> --destination-ring <directory> --package-id <guid> --manifest-sha256 <hash> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive-sha256 <sha256> --connector-deployment <id> --connector-key <id> --connector-public-fingerprint <sha256>");
            Console.WriteLine("  maintenance home-continuity probe --binding <binding.json> --receipt <receipt.json>");
            return 0;
        }

        // The web host and the generic host disagree about two things, and both matter here.
        //
        // WebApplication.CreateBuilder reads ASPNETCORE_ENVIRONMENT and takes the current directory as its
        // content root; Host.CreateApplicationBuilder reads DOTNET_ENVIRONMENT and takes the application
        // directory. Left alone, maintenance would run as "Production" from a directory with no
        // appsettings.Development.json, find no connection string, and report a perfectly healthy database
        // as unreachable — a wrong answer that reads exactly like a real one. The environment the launchers
        // set is honoured explicitly, and configuration is read from beside the assembly, where the build
        // puts the settings files.
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Maintenance output is read by an operator and parsed by a launcher. Entity Framework's Information
        // level narrates every command it runs, which buries a three-line answer in a page of SQL and puts
        // arbitrary text in front of the JSON a caller has to parse. Warnings and errors still surface.
        //
        // Applied as configuration rather than through builder.Logging.SetMinimumLevel, because the
        // configuration-driven filters from appsettings.Development.json win over a programmatic minimum —
        // which is exactly what happened on the first attempt, and looked like the call had no effect.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
        });
        builder.Services.AddAeroLinkInfrastructure(builder.Configuration);
        // Registered in Program.cs beside the web composition, and required by infrastructure services the
        // container validates whether maintenance resolves them or not. Development turns on validate-on-
        // build, so an unregistered dependency is a startup failure rather than a lazy one — which is how
        // this was found, and is the behaviour worth keeping.
        builder.Services.AddSingleton<AeroLink.Domain.Hierarchy.ILadderPolicy, AeroLink.Domain.Hierarchy.LegacyLadderPolicy>();
        // Every hosted worker is removed: maintenance must not dispatch notifications, reconcile documents,
        // or run integrity sweeps against a database whose posture is the very thing in question.
        foreach (var worker in builder.Services.Where(x => x.ServiceType == typeof(IHostedService)).ToList())
            builder.Services.Remove(worker);
        builder.Services.AddScoped<AeroLinkUpgradeAnalyzer>();
        builder.Services.AddScoped<ProjectLeadershipMaintenanceResolver>();
        builder.Services.AddScoped<ProjectLeadershipReconciliationAuthority>();

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();

        return command switch
        {
            "analyze" => await AnalyzeAsync(scope.ServiceProvider, args),
            "upgrade" => await UpgradeAsync(scope.ServiceProvider, args),
            "resolve" => await ResolveAsync(scope.ServiceProvider, args),
            "home-continuity" => await HomeContinuityAsync(scope.ServiceProvider, args),
            _ => Fail($"Unknown maintenance command '{command}'. Run 'maintenance help'."),
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>
    /// Exit codes are the launcher's contract, and they are the reason a known refusal no longer costs
    /// fifteen minutes: the caller branches on the code rather than polling a port that will never open.
    ///   0 current, 10 deterministic upgrade required, 20 conflict, 30 database unreachable.
    /// </summary>
    private static async Task<int> AnalyzeAsync(IServiceProvider services, string[] args)
    {
        var analysis = await services.GetRequiredService<AeroLinkUpgradeAnalyzer>().AnalyzeAsync();
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = analysis.Status,
                databaseReachable = analysis.DatabaseReachable,
                unreachableReason = analysis.UnreachableReason,
                databaseName = analysis.DatabaseName,
                pendingEfMigrations = analysis.PendingEfMigrations,
                semanticUpgrades = analysis.SemanticUpgrades,
                pendingSemanticUpgrades = analysis.PendingSemanticUpgrades,
                conflicts = analysis.Conflicts,
                upgradeRequired = analysis.UpgradeRequired,
                deterministicUpgrade = analysis.DeterministicUpgrade,
                databaseModified = analysis.DatabaseModified,
                // The third category. Machine-readable as well as rendered, because #881 asks for the
                // pending versioned showcase posture in both, and a category only a human can read is not
                // something a launcher or a script can act on.
                showcase = analysis.Showcase,
                showcaseUpgradeAvailable = analysis.ShowcaseUpgradeAvailable,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var line in AeroLinkUpgradeAnalyzer.Render(analysis)) Console.WriteLine(line);
        }
        return analysis.Status switch
        {
            "current" => 0,
            "upgrade-required" => 10,
            "conflict" => 20,
            _ => 30,
        };
    }

    /// <summary>
    /// Applies the schema and semantic upgrade this build implies, using the same authorities startup uses.
    ///
    /// It exists so the clone-validation path can apply the real upgrade to an isolated restored copy and
    /// then, only if that passed, to the real database — both times through the identical code. A second
    /// upgrade implementation "for maintenance" would defeat the whole point of validating on a clone.
    ///
    /// Requires --apply. Without it this reports what would happen and writes nothing.
    /// </summary>
    private static async Task<int> UpgradeAsync(IServiceProvider services, string[] args)
    {
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var analyzer = services.GetRequiredService<AeroLinkUpgradeAnalyzer>();
        var before = await analyzer.AnalyzeAsync();
        if (!before.DatabaseReachable) return Fail(before.UnreachableReason ?? "The database could not be reached.");
        if (before.Conflicts.Count > 0)
        {
            foreach (var line in AeroLinkUpgradeAnalyzer.Render(before)) Console.WriteLine(line);
            return 20;
        }
        if (!before.UpgradeRequired)
        {
            Console.WriteLine($"DATABASE CURRENT: {before.DatabaseName} needs no schema or semantic upgrade.");
            return 0;
        }
        if (!apply)
        {
            foreach (var line in AeroLinkUpgradeAnalyzer.Render(before)) Console.WriteLine(line);
            Console.WriteLine("Nothing was written. Re-run with --apply to perform this upgrade.");
            return 10;
        }

        var db = services.GetRequiredService<AeroLinkDbContext>();
        Console.WriteLine($"Applying {before.PendingEfMigrations.Count} schema migration(s) to {before.DatabaseName}...");
        await db.Database.MigrateAsync();

        // The phase boundary. Before the schema moved, the semantic markers lived in tables that may not
        // have existed, so the analysis above reported the semantic posture as unknown rather than clean.
        // Now it is knowable — and a controlled-data ambiguity that only becomes readable at this point must
        // be reported as a structured conflict with its supported decisions, exactly as it would have been
        // on a database that was already migrated. Running the authorities first would surface the same
        // ambiguity as an exception and a stack tail, which is the operator experience #881 exists to end.
        var afterSchema = await analyzer.AnalyzeAsync();
        if (afterSchema.Conflicts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("The schema upgrade completed. The semantic posture is now readable, and it needs a decision:");
            Console.WriteLine();
            foreach (var line in AeroLinkUpgradeAnalyzer.Render(afterSchema)) Console.WriteLine(line);
            return 20;
        }

        Console.WriteLine("Applying semantic upgrades...");
        await services.GetRequiredService<SoftwareVerificationCaseMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<ProjectLeadershipMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<ProjectLeadershipReconciliationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<TestChangeRequestPrefixMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<SoftwareProcedureExecutionCutoverAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<FrozenReviewTraceAdjacencyMigrationAuthority>().EnsureCompletedAsync();

        var after = await analyzer.AnalyzeAsync();
        foreach (var line in AeroLinkUpgradeAnalyzer.Render(after)) Console.WriteLine(line);
        return after.Status switch { "current" => 0, "conflict" => 20, "upgrade-required" => 10, _ => 30 };
    }

    private static async Task<int> ResolveAsync(IServiceProvider services, string[] args)
    {
        string? Value(string name)
        {
            var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var conflict = Value("--conflict");
        var choice = Value("--choice");
        var operatorReference = Value("--operator");
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);

        if (conflict is null || choice is null || operatorReference is null)
            return Fail("--conflict, --choice and --operator are required.");
        if (!Guid.TryParse(Value("--program"), out var programId)) return Fail("--program must be a GUID.");
        if (!Guid.TryParse(Value("--person"), out var personId)) return Fail("--person must be a GUID.");
        if (!Guid.TryParse(Value("--legacy-backup"), out var legacyBackupId)) return Fail("--legacy-backup must be a GUID.");
        if (!Enum.TryParse<ProjectLeadershipPosition>(Value("--position"), ignoreCase: true, out var position))
            return Fail("--position must be one of: " + string.Join(", ", ProjectLeadership.All));

        Guid? expectedPrimary = null;
        var rawExpectedPrimary = Value("--expect-primary");
        if (!string.IsNullOrWhiteSpace(rawExpectedPrimary) && !rawExpectedPrimary.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(rawExpectedPrimary, out var parsedPrimary)) return Fail("--expect-primary must be a GUID or 'none'.");
            expectedPrimary = parsedPrimary;
        }

        var supported = new[]
        {
            AeroLinkUpgradeConflict.LegacyBackupIneligibleCode,
            AeroLinkUpgradeConflict.LegacyBackupIsPrimaryCode,
            AeroLinkUpgradeConflict.LegacyBackupSupersededCode,
        };
        if (!supported.Contains(conflict))
            return Fail($"Conflict '{conflict}' has no supported automated resolution. Resolve it in AeroLink, then analyze again.");

        var result = await services.GetRequiredService<ProjectLeadershipMaintenanceResolver>()
            .ResolveLegacyBackupAsync(programId, legacyBackupId, position, personId, choice, expectedPrimary,
                operatorReference, apply, conflict);

        Console.WriteLine(result.Applied ? "MAINTENANCE DECISION APPLIED" : $"MAINTENANCE DECISION NOT APPLIED ({result.Outcome})");
        Console.WriteLine(result.Detail);
        foreach (var change in result.Changes) Console.WriteLine("  " + change);
        if (!result.Applied && result.Outcome != AeroLinkResolutionResult.DryRunOutcome)
            Console.WriteLine("No persistent data was changed.");

        return result.Outcome switch
        {
            AeroLinkResolutionResult.AppliedOutcome => 0,
            AeroLinkResolutionResult.DryRunOutcome => 0,
            AeroLinkResolutionResult.PreconditionFailedOutcome => 21,
            _ => 22,
        };
    }

    private static async Task<int> HomeContinuityAsync(IServiceProvider services, string[] args)
    {
        var operation = args.Length > 2 ? args[2].ToLowerInvariant() : "help";
        return operation switch
        {
            "export" => await ExportHomeContinuityAsync(services, args),
            "apply" => await ApplyHomeContinuityAsync(services, args),
            "recover" => await RecoverHomeContinuityAsync(services, args),
            "probe" => await ProbeHomeContinuityAsync(services, args),
            _ => Fail("Use 'maintenance home-continuity export', 'maintenance home-continuity apply', 'maintenance home-continuity recover' or 'maintenance home-continuity probe'."),
        };
    }

    private static async Task<int> ProbeHomeContinuityAsync(IServiceProvider services, string[] args)
    {
        try
        {
            var bindingPath = RequiredValue(args, "--binding");
            var receiptPath = RequiredValue(args, "--receipt");
            if (!File.Exists(bindingPath)) return Fail("The compatibility binding file does not exist.");
            var binding = JsonSerializer.Deserialize<HomeContinuityCompatibilityBinding>(
                await File.ReadAllTextAsync(bindingPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (binding is null) return Fail("The compatibility binding file is invalid.");
            HomeContinuityCompatibilityReader.ValidateBinding(binding);
            using var sourceServices = HomeContinuityKeyTransfer.BuildSourceServices(
                binding.SourceRingDirectory, binding.SourceDiscriminator, binding.SourceProviderSelection);
            await using var db = BuildContinuityRestoreDb(services, binding);
            var receipt = await HomeContinuityCompatibilityReader.ProbeAsync(db, sourceServices, binding);
            var directory = Path.GetDirectoryName(Path.GetFullPath(receiptPath));
            if (string.IsNullOrWhiteSpace(directory)) return Fail("The compatibility receipt path has no directory.");
            Directory.CreateDirectory(directory);
            WriteReceiptOnce(receiptPath, receipt);
            Console.WriteLine($"HOME CONTINUITY COMPATIBILITY STATUS: {receipt.Status}");
            Console.WriteLine($"Protected categories: {receipt.ProtectedCategories.Count}; external categories: {receipt.ExternalCategories.Count}");
            return receipt.Status == HomeContinuityCompatibilityReader.CompatibilityProof ? 0 : 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbException)
        {
            return Fail("HOME continuity compatibility probe refused: the bound archive, restore, source ring or read-only database boundary was not proven.");
        }
    }

    private static async Task<int> ExportHomeContinuityAsync(IServiceProvider services, string[] args)
    {
        var package = RequiredValue(args, "--package");
        var sourceInstallation = RequiredValue(args, "--source-installation");
        var sourceDiscriminator = RequiredValue(args, "--source-discriminator");
        var sourceProtection = RequiredValue(args, "--source-protection");
        var verifiedBackup = RequiredValue(args, "--verified-backup");
        var verifiedBackupArchive = RequiredValue(args, "--verified-backup-archive");
        var connectorDeployment = RequiredValue(args, "--connector-deployment");
        var connectorKey = RequiredValue(args, "--connector-key");
        var targetApplication = RequiredValue(args, "--target-application");
        var certificatePath = RequiredValue(args, "--certificate");
        var sourceRingPath = RequiredValue(args, "--source-ring");
        var compatibilityReceiptPath = RequiredValue(args, "--compatibility-receipt");
        var restorePostgresBin = RequiredValue(args, "--restore-postgres-bin");
        if (!string.Equals(targetApplication, HomeContinuityKeyTransfer.CanonicalTargetApplicationName, StringComparison.Ordinal))
            return Fail("The continuity destination application must be exactly 'AeroLink'.");
        var backupHash = VerifySourceBackup(verifiedBackupArchive, verifiedBackup, sourceInstallation);
        var compatibilityReceipt = ReadCompatibilityReceipt(compatibilityReceiptPath);
        HomeContinuityCompatibilityReader.ValidateReceipt(compatibilityReceipt);
        var configuration = services.GetRequiredService<IConfiguration>();
        var installationRoot = Path.GetFullPath(RequiredConfiguration(configuration, "SiteServer:InstallationRoot"));
        var protectedConfigRoot = configuration["SiteServer:ProtectedConfigRoot"]?.Trim();
        ValidateExportSourceContext(compatibilityReceipt.Binding, sourceInstallation, sourceDiscriminator,
            sourceProtection, sourceRingPath, verifiedBackupArchive, backupHash);
        var restorePort = GetConfiguredRestorePort(configuration);
        var restoreScript = ResolveCanonicalRestoreScript(installationRoot);
        ValidatePostgresBin(restorePostgresBin);
        var restoreDatabase = "aerolink_continuity_restore_" + Guid.NewGuid().ToString("N")[..12];
        var restoreValidationRoot = Path.Combine(installationRoot, "restore-validation");
        Directory.CreateDirectory(restoreValidationRoot);
        RefusePendingContinuityOperations(restoreValidationRoot);
        using var operationLease = AcquireContinuityOperationLease(restoreValidationRoot);
        var restoreProof = Path.Combine(restoreValidationRoot, restoreDatabase + ".continuity.json");
        var operationId = Guid.NewGuid().ToString("N");
        var operationJournal = Path.Combine(restoreValidationRoot, restoreDatabase + ".operation.json");
        WriteContinuityOperationJournal(operationJournal, new
        {
            JournalVersion = 1,
            OperationId = operationId,
            Status = "admitted",
            Phase = "admission",
            TargetDatabase = restoreDatabase,
            RestoreValidationRoot = Path.GetFullPath(restoreValidationRoot),
            RestoreWorkRoot = Path.Combine(installationRoot, "restore-work"),
            ProofPath = restoreProof,
            ArchivePath = Path.GetFullPath(verifiedBackupArchive),
            ArchiveSha256 = backupHash,
            PostgresPort = restorePort,
            CreatedByOperation = false,
            CreatedDatabaseOid = (string?)null,
            EvidenceOwned = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, createOnly: true);
        var readerImage = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(readerImage) || !File.Exists(readerImage))
            throw new InvalidOperationException("The continuity reader process image identity is unavailable.");
        var apiAssemblyPath = typeof(AeroLinkMaintenanceHost).Assembly.Location;
        if (string.IsNullOrWhiteSpace(apiAssemblyPath) || !File.Exists(apiAssemblyPath))
            throw new InvalidOperationException("The continuity API entry assembly identity is unavailable.");
        apiAssemblyPath = Path.GetFullPath(apiAssemblyPath);
        var apiAssemblySha256 = Sha256File(apiAssemblyPath);
        var readerSid = CurrentReaderSid(compatibilityReceipt.Binding.ReaderUserSid);
        var freshBinding = compatibilityReceipt.Binding with
        {
            SourceProviderSelection = sourceProtection,
            VerifiedBackupArchivePath = Path.GetFullPath(verifiedBackupArchive),
            VerifiedBackupArchiveSha256 = backupHash,
            RestoredDatabaseName = restoreDatabase,
            RestoredPostgresPort = restorePort,
            RestoreProofPath = restoreProof,
            RestoreScriptPath = restoreScript,
            ReaderUserSid = readerSid,
            ReaderSourceSha256 = Sha256File(readerImage),
            ReaderAssemblySha256 = Sha256File(typeof(HomeContinuityCompatibilityReader).Assembly.Location),
            SourceProcessAttestation = "operator-attested-frozen",
            RestoreApiAssemblyPath = apiAssemblyPath,
            RestoreApiAssemblySha256 = apiAssemblySha256,
        };
        UpdateContinuityOperationJournal(operationJournal, journal =>
        {
            journal["MaintenanceEntryImagePath"] = Path.GetFullPath(readerImage);
            journal["MaintenanceEntryImageSha256"] = freshBinding.ReaderSourceSha256;
            journal["InfrastructureReaderAssemblyPath"] = typeof(HomeContinuityCompatibilityReader).Assembly.Location;
            journal["InfrastructureReaderAssemblySha256"] = freshBinding.ReaderAssemblySha256;
            journal["ApiEntryAssemblyPath"] = apiAssemblyPath;
            journal["ApiEntryAssemblySha256"] = apiAssemblySha256;
            journal["ApiHttpValidation"] = "not-executed: SkipCurrentCodeValidation was explicitly supplied";
            journal["ApiExecutablePath"] = null;
            journal["ApiExecutableSha256"] = null;
        });
        var operationCleaned = false;
        try
        {
            var operation = await RunFreshContinuityRestoreAsync(restoreScript, restorePostgresBin, verifiedBackupArchive,
                restoreDatabase, restorePort, restoreProof, operationJournal, operationId, installationRoot, protectedConfigRoot,
                apiAssemblyPath, apiAssemblySha256);
            var restoredOid = RequiredJournalValue(operation, "CreatedDatabaseOid");
            freshBinding = freshBinding with
            {
                RestoreOperationJournalPath = operationJournal,
                RestoreOperationId = operationId,
                RestoredDatabaseOid = restoredOid,
            };
            HomeContinuityCompatibilityReader.ValidateBinding(freshBinding);
            var connector = ReadConnectorIdentity(configuration, connectorDeployment, connectorKey, sourceInstallation);
            using var sourceServices = HomeContinuityKeyTransfer.BuildSourceServices(sourceRingPath, sourceDiscriminator, sourceProtection);
            await using var restoredDb = BuildContinuityRestoreDb(services, freshBinding);
            UpdateContinuityOperationJournal(operationJournal, journal =>
            {
                journal["Phase"] = "probe-running";
                journal["ProbeStartedAtUtc"] = DateTimeOffset.UtcNow;
            });
            var freshCompatibility = await HomeContinuityCompatibilityReader.ProbeAsync(
                restoredDb, sourceServices, freshBinding);
            UpdateContinuityOperationJournal(operationJournal, journal =>
            {
                journal["Phase"] = "probe-complete";
                journal["ProbeCompletedAtUtc"] = DateTimeOffset.UtcNow;
                journal["ProbeStatus"] = freshCompatibility.Status;
            });
            if (freshCompatibility.Status is not (HomeContinuityCompatibilityReader.CompatibilityProof
                or HomeContinuityCompatibilityReader.NoApplicableValues
                or HomeContinuityCompatibilityReader.LegacyPlaintextPresent))
                throw new InvalidOperationException("The continuity export requires a fresh complete compatibility result from its owned restore; a retained receipt status is not sufficient.");

            await CleanupContinuityOperationAsync(restoreScript, restorePostgresBin, restoreDatabase, restorePort,
                operationJournal, operationId, installationRoot, protectedConfigRoot);
            operationCleaned = true;
            var sourceManager = sourceServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
            var sourceRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                new DirectoryInfo(sourceRingPath), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            using var certificate = LoadCertificate(certificatePath, keyPath: null, requirePrivateKey: false);
            var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator, targetApplication,
                sourceProtection, backupHash, connectorDeployment, connectorKey, connector.PublicKeyFingerprint, backupHash);
            var manifest = HomeContinuityKeyTransfer.ExportPackage(
                sourceManager, sourceRepository, certificate, binding, connector, package,
                freshCompatibility.Status, string.Equals(freshCompatibility.Status, HomeContinuityCompatibilityReader.CompatibilityProof, StringComparison.Ordinal));
            Console.WriteLine($"HOME CONTINUITY PACKAGE EXPORTED: {manifest.PackageId:D}");
            Console.WriteLine($"Keys: {manifest.Keys.Count}; revocations: {manifest.Revocations.Count}");
            Console.WriteLine($"Compatibility status: {manifest.CompatibilityStatus}; cryptographic proof: {manifest.CryptographicCompatibilityProof}");
            Console.WriteLine($"Manifest SHA-256: {Sha256File(Path.Combine(package, "manifest.json"))}");
            Console.WriteLine("Key descriptors and protected values were not printed or written in plaintext.");
            await Task.CompletedTask;
            return 0;
        }
        catch
        {
            if (!operationCleaned) TryCleanupContinuityOperation(restoreScript, restorePostgresBin, restoreDatabase, restorePort,
                operationJournal, operationId, installationRoot, protectedConfigRoot);
            throw;
        }
    }

    private static void ValidateExportSourceContext(
        HomeContinuityCompatibilityBinding captured,
        string sourceInstallation,
        string sourceDiscriminator,
        string sourceProtection,
        string sourceRingPath,
        string verifiedBackupArchive,
        string backupHash)
    {
        if (!string.Equals(captured.SourceInstallationId, sourceInstallation, StringComparison.Ordinal)
            || !string.Equals(captured.SourceDiscriminator, sourceDiscriminator, StringComparison.Ordinal)
            || !string.Equals(captured.SourceProviderSelection, sourceProtection, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(captured.SourceRingDirectory), Path.GetFullPath(sourceRingPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(captured.VerifiedBackupArchivePath), Path.GetFullPath(verifiedBackupArchive), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The continuity export receipt is not bound to this source ring, installation, discriminator, provider or verified backup.");
        if (!Guid.TryParse(sourceInstallation, out var instanceId) || instanceId == Guid.Empty || captured.SourceInstanceId != instanceId)
            throw new InvalidOperationException("The continuity export source installation and instance identities do not agree.");
        if (captured.SourceProcessId <= 0 || captured.SourceProcessStartedUtc == default
            || !string.Equals(captured.SourceProcessAttestation, "operator-attested-frozen", StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity export requires a separately attested frozen source process identity.");
        if (!File.Exists(captured.SourceExecutablePath) || !string.Equals(Sha256File(captured.SourceExecutablePath), captured.SourceExecutableSha256, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(captured.SourceConfigurationPath) || !string.Equals(Sha256File(captured.SourceConfigurationPath), captured.SourceConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The captured source executable or configuration no longer matches its recorded identity.");
        if (!string.Equals(HomeContinuityCompatibilityReader.ComputeRingManifestSha256(sourceRingPath), captured.SourceRingManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The captured source Data Protection ring no longer matches its recorded manifest digest.");
        if (!string.Equals(captured.VerifiedBackupArchiveSha256, backupHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The retained continuity context does not match the verified backup archive hash.");
        if (captured.ExternalStoreStates is null
            || HomeContinuityCompatibilityReader.ExternalProtectedCategories.Any(category => !captured.ExternalStoreStates.ContainsKey(category)))
            throw new InvalidOperationException("The retained continuity context does not contain the complete external-store inventory.");
    }

    private static int GetConfiguredRestorePort(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("AeroLink");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("The fresh continuity restore connection is not configured.");
        var connection = new NpgsqlConnectionStringBuilder(configured);
        if (connection.Port is <= 0 or 54329 || connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("The fresh continuity restore must use an owned loopback PostgreSQL port other than 54329.");
        return connection.Port;
    }

    private static string ResolveCanonicalRestoreScript(string installationRoot)
    {
        var script = Path.GetFullPath(Path.Combine(installationRoot, "program", "scripts", "Restore-AeroLink.ps1"));
        if (!File.Exists(script) || (File.GetAttributes(script) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The installed continuity restore script is missing from the declared program root.", script);
        return script;
    }

    private static void ValidatePostgresBin(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot) || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The supplied PostgreSQL binary root does not exist as an owned directory.");
        foreach (var executable in new[] { "createdb.exe", "pg_restore.exe", "psql.exe", "dropdb.exe" })
        {
            var path = Path.Combine(fullRoot, executable);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new FileNotFoundException($"The supplied PostgreSQL binary root is missing {executable}.", path);
        }
    }

    private static string CurrentReaderSid(string capturedSid)
    {
        if (!OperatingSystem.IsWindows()) return capturedSid;
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The continuity reader Windows identity is unavailable.");
    }

    private static async Task<JsonObject> RunFreshContinuityRestoreAsync(
        string restoreScript,
        string postgresBin,
        string archive,
        string database,
        int port,
        string proof,
        string operationJournal,
        string operationId,
        string installationRoot,
        string? protectedConfigRoot,
        string apiAssemblyPath,
        string apiAssemblySha256)
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(restoreScript)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            // A .NET host launched from pwsh inherits pwsh's PSModulePath, whose PowerShell 7
            // Microsoft.PowerShell.Security module can shadow the Windows PowerShell module. The
            // installed restore authority uses Get-Acl, so give Windows PowerShell its own module
            // roots exactly as a direct powershell.exe invocation does.
            var windowsPowerShellModules = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
            }.Where(Directory.Exists);
            start.Environment["PSModulePath"] = string.Join(Path.PathSeparator, windowsPowerShellModules);
        }
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", restoreScript,
            "-BackupArchive", archive, "-TargetDatabase", database, "-PostgresPort", port.ToString(CultureInfo.InvariantCulture),
            "-PostgresBin", postgresBin, "-DisposableQualification", "-SkipCurrentCodeValidation",
            "-RequireFreshTarget", "-ContinuityReceiptPath", proof, "-InstallationRoot", installationRoot,
            "-ContinuityOperationJournalPath", operationJournal, "-ContinuityOperationId", operationId,
            "-ContinuityApiAssemblyPath", apiAssemblyPath, "-ContinuityApiAssemblySha256", apiAssemblySha256,
            "-RetainContinuityRestore",
        }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(protectedConfigRoot))
        {
            start.ArgumentList.Add("-ProtectedConfigRoot");
            start.ArgumentList.Add(protectedConfigRoot);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The continuity restore process could not be started.");
        var childIdentity = CaptureProcessIdentity(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                if (!process.HasExited && SameProcessIdentity(process, childIdentity)) process.Kill(entireProcessTree: true);
            }
            catch { }
            try
            {
                using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(killWait.Token);
            }
            catch { }
        }
        var captured = await DrainContinuityChildOutputAsync(process, stdout, stderr, TimeSpan.FromSeconds(5));
        var operation = UpdateContinuityOperationJournal(operationJournal, journal =>
        {
            journal["ParentProcessId"] = Environment.ProcessId;
            journal["ParentProcessStartedUtc"] = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            journal["RestoreChildProcess"] = childIdentity;
            journal["RestoreChildExitCode"] = timedOut || !process.HasExited ? null : process.ExitCode;
            journal["RestoreChildTimedOut"] = timedOut;
            journal["RestoreChildOutputDrainComplete"] = captured.Drained;
            journal["RestoreChildStdout"] = RedactContinuityOutput(captured.Stdout);
            journal["RestoreChildStderr"] = RedactContinuityOutput(captured.Stderr);
        });
        if (timedOut)
        {
            MarkContinuityRecoveryRequired(operationJournal, "restore-timeout", "The restore child exceeded its bounded lifetime; attended recovery is required.");
            throw new InvalidOperationException("The fresh continuity restore exceeded its bounded operation time and was refused.");
        }
        if (!captured.Drained)
        {
            throw new InvalidOperationException("The fresh continuity restore child output could not be drained within its bounded evidence window.");
        }
        if (process.ExitCode != 0 || !File.Exists(proof))
            throw new InvalidOperationException("The existing continuity restore authority refused the fresh owned restore; no export package was written.");
        var status = operation["Status"]?.GetValue<string>();
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
            throw new InvalidOperationException("The continuity restore completed without a product-owned completed operation journal.");
        return operation;
    }

    private static async Task CleanupContinuityOperationAsync(
        string restoreScript,
        string postgresBin,
        string database,
        int port,
        string operationJournal,
        string operationId,
        string installationRoot,
        string? protectedConfigRoot)
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(restoreScript)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            var windowsPowerShellModules = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
            }.Where(Directory.Exists);
            start.Environment["PSModulePath"] = string.Join(Path.PathSeparator, windowsPowerShellModules);
        }
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", restoreScript,
            "-CleanupContinuityOperation", "-ContinuityOperationJournalPath", operationJournal,
            "-ContinuityOperationId", operationId, "-TargetDatabase", database,
            "-PostgresPort", port.ToString(CultureInfo.InvariantCulture), "-PostgresBin", postgresBin,
            "-InstallationRoot", installationRoot,
        }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(protectedConfigRoot))
        {
            start.ArgumentList.Add("-ProtectedConfigRoot");
            start.ArgumentList.Add(protectedConfigRoot);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The continuity cleanup process could not be started.");
        var childIdentity = CaptureProcessIdentity(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { if (!process.HasExited && SameProcessIdentity(process, childIdentity)) process.Kill(entireProcessTree: true); } catch { }
            try
            {
                using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(killWait.Token);
            }
            catch { }
        }
        var captured = await DrainContinuityChildOutputAsync(process, stdout, stderr, TimeSpan.FromSeconds(5));
        UpdateContinuityOperationJournal(operationJournal, journal =>
        {
            journal["CleanupChildProcess"] = childIdentity;
            journal["CleanupChildExitCode"] = timedOut || !process.HasExited ? null : process.ExitCode;
            journal["CleanupChildTimedOut"] = timedOut;
            journal["CleanupChildOutputDrainComplete"] = captured.Drained;
            journal["CleanupChildStdout"] = RedactContinuityOutput(captured.Stdout);
            journal["CleanupChildStderr"] = RedactContinuityOutput(captured.Stderr);
        });
        if (timedOut || !captured.Drained)
        {
            MarkContinuityRecoveryRequired(operationJournal, "cleanup-timeout", "The cleanup child did not complete its bounded evidence window; attended recovery is required.");
            throw new InvalidOperationException($"The continuity cleanup timed out or its output could not be drained; recovery is required at {operationJournal}.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The product-owned continuity cleanup refused; recovery is required at {operationJournal}.");
        var final = ReadContinuityOperationJournal(operationJournal);
        if (!string.Equals(final["Status"]?.GetValue<string>(), "cleaned", StringComparison.Ordinal))
            throw new InvalidOperationException($"The product-owned continuity cleanup did not reach a terminal cleaned state; recovery is required at {operationJournal}.");
    }

    private static void TryCleanupContinuityOperation(
        string restoreScript,
        string postgresBin,
        string database,
        int port,
        string operationJournal,
        string operationId,
        string installationRoot,
        string? protectedConfigRoot)
    {
        try
        {
            var journal = ReadContinuityOperationJournal(operationJournal);
            if (journal["Status"]?.GetValue<string>() is "cleaned" or "recovery-required") return;
            CleanupContinuityOperationAsync(restoreScript, postgresBin, database, port, operationJournal, operationId,
                installationRoot, protectedConfigRoot).GetAwaiter().GetResult();
        }
        catch { /* The journal remains the attended recovery record; never hide the original refusal. */ }
    }

    private static JsonObject ReadContinuityOperationJournal(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("The continuity operation journal is missing.");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("The continuity operation journal is invalid.");
    }

    private static async Task<(string Stdout, string Stderr, bool Drained)> DrainContinuityChildOutputAsync(
        Process process,
        Task<string> stdout,
        Task<string> stderr,
        TimeSpan timeout)
    {
        var both = Task.WhenAll(stdout, stderr);
        var completed = await Task.WhenAny(both, Task.Delay(timeout));
        if (completed != both)
        {
            try { process.StandardOutput.Close(); } catch { }
            try { process.StandardError.Close(); } catch { }
            await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        if (both.IsCompleted)
        {
            try { await both; } catch { }
        }
        return (CompletedChildOutput(stdout), CompletedChildOutput(stderr), both.IsCompletedSuccessfully);
    }

    private static string CompletedChildOutput(Task<string> output) =>
        output.Status == TaskStatus.RanToCompletion ? output.Result : "[child-output-unavailable]";

    private static void MarkContinuityRecoveryRequired(string operationJournal, string phase, string disposition)
    {
        try
        {
            var journal = ReadContinuityOperationJournal(operationJournal);
            if (journal["Status"]?.GetValue<string>() != "cleaned")
            {
                journal["Status"] = "recovery-required";
                journal["Phase"] = phase;
                journal["CleanupDisposition"] = disposition;
                journal["UpdatedAtUtc"] = DateTimeOffset.UtcNow;
                WriteJsonOnceReplacing(operationJournal, journal);
            }
        }
        catch { }
    }

    private static void RefusePendingContinuityOperations(string restoreValidationRoot)
    {
        var leasePath = Path.Combine(restoreValidationRoot, ".home-continuity.operation.lease");
        if (File.Exists(leasePath))
            throw new InvalidOperationException($"A continuity operation lease is present; attended recovery is required at {leasePath}.");
        foreach (var path in Directory.EnumerateFiles(restoreValidationRoot, "*.operation.json", SearchOption.TopDirectoryOnly))
        {
            JsonObject journal;
            try { journal = ReadContinuityOperationJournal(path); }
            catch { throw new InvalidOperationException($"A continuity operation journal is malformed; attended recovery is required at {path}."); }
            var status = journal["Status"]?.GetValue<string>();
            if (!string.Equals(status, "cleaned", StringComparison.Ordinal))
                throw new InvalidOperationException($"A continuity operation is incomplete; attended recovery is required at {path}.");
        }
    }

    private static ContinuityOperationLease AcquireContinuityOperationLease(string restoreValidationRoot)
    {
        var path = Path.Combine(restoreValidationRoot, ".home-continuity.operation.lease");
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId,
                ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                ImagePath = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath,
                AcquiredAtUtc = DateTimeOffset.UtcNow,
            }));
            writer.Flush();
            stream.Flush(true);
            return new ContinuityOperationLease(path, stream);
        }
        catch (IOException) { throw new InvalidOperationException($"A continuity export operation is already in progress or requires attended recovery at {path}."); }
    }

    private sealed class ContinuityOperationLease : IDisposable
    {
        private readonly string path;
        private readonly FileStream stream;
        public ContinuityOperationLease(string path, FileStream stream) { this.path = path; this.stream = stream; }
        public void Dispose()
        {
            stream.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static JsonObject UpdateContinuityOperationJournal(string path, Action<JsonObject> update)
    {
        var journal = ReadContinuityOperationJournal(path);
        update(journal);
        WriteJsonOnceReplacing(path, journal);
        return journal;
    }

    private static void WriteContinuityOperationJournal(string path, object value, bool createOnly)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (createOnly && File.Exists(fullPath)) throw new InvalidOperationException("The continuity operation journal already exists; refusing to reuse an operation.");
        WriteJsonOnceReplacing(fullPath, JsonSerializer.SerializeToNode(value)?.AsObject()
            ?? throw new InvalidOperationException("The continuity operation journal could not be serialized."));
    }

    private static void WriteJsonOnceReplacing(string path, JsonObject value)
    {
        var temporary = $"{path}.writing-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        try
        {
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonObject CaptureProcessIdentity(Process process)
    {
        var image = process.MainModule?.FileName ?? throw new InvalidOperationException("The continuity child image identity is unavailable.");
        return new JsonObject
        {
            ["ProcessId"] = process.Id,
            ["ProcessStartedUtc"] = process.StartTime.ToUniversalTime(),
            ["ImagePath"] = Path.GetFullPath(image),
            ["ImageSha256"] = Sha256File(image),
        };
    }

    private static bool SameProcessIdentity(Process process, JsonObject identity) =>
        process.Id == identity["ProcessId"]?.GetValue<int>()
        && process.StartTime.ToUniversalTime() == identity["ProcessStartedUtc"]?.GetValue<DateTimeOffset>();

    private static string RedactContinuityOutput(string value)
    {
        var redacted = Regex.Replace(value ?? string.Empty,
            @"(?im)(?<name>password|pwd|secret|connectionstring|pgpassword)\s*[:=]\s*[^\s;]+",
            "${name}=<redacted>");
        return redacted.Length <= 16000 ? redacted : redacted[^16000..];
    }

    private static string RequiredJournalValue(JsonObject journal, string name) =>
        journal[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The continuity operation journal has no {name}.");

    private static HomeContinuityCompatibilityReceipt ReadCompatibilityReceipt(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("The compatibility receipt file does not exist.");
        var receipt = JsonSerializer.Deserialize<HomeContinuityCompatibilityReceipt>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return receipt ?? throw new InvalidOperationException("The compatibility receipt file is invalid.");
    }

    private static AeroLinkDbContext BuildContinuityRestoreDb(IServiceProvider services, HomeContinuityCompatibilityBinding binding)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!string.Equals(configuration["Database:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HOME continuity requires a local disposable PostgreSQL restore; the ambient database provider is not an owned restore boundary.");
        var configured = configuration.GetConnectionString("AeroLink");
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("The disposable restore connection is not configured.");
        var connection = new NpgsqlConnectionStringBuilder(configured);
        connection.Database = binding.RestoredDatabaseName;
        connection.Port = binding.RestoredPostgresPort;
        connection.ApplicationName = $"AeroLinkHomeContinuity/{Guid.NewGuid():N}";
        if (connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("HOME continuity refuses a remote PostgreSQL restore host.");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection.ConnectionString).Options;
        return new AeroLinkDbContext(options);
    }

    private static void WriteReceiptOnce(string path, HomeContinuityCompatibilityReceipt receipt)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + $".writing-{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(fullPath)) throw new InvalidOperationException("The compatibility receipt already exists; receipts are write-once.");
            File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<int> ApplyHomeContinuityAsync(IServiceProvider services, string[] args)
    {
        var package = RequiredValue(args, "--package");
        var destinationRing = RequiredValue(args, "--destination-ring");
        var sourceInstallation = RequiredValue(args, "--source-installation");
        var sourceDiscriminator = RequiredValue(args, "--source-discriminator");
        var sourceProtection = RequiredValue(args, "--source-protection");
        var verifiedBackup = RequiredValue(args, "--verified-backup");
        var verifiedBackupArchiveHash = RequiredValue(args, "--verified-backup-archive-sha256");
        var connectorDeployment = RequiredValue(args, "--connector-deployment");
        var connectorKey = RequiredValue(args, "--connector-key");
        var connectorFingerprint = RequiredValue(args, "--connector-public-fingerprint");
        var manifestSha256 = RequiredValue(args, "--manifest-sha256");
        var certificatePath = RequiredValue(args, "--certificate");
        var certificateKeyPath = OptionalValue(args, "--certificate-key");

        var configuration = services.GetRequiredService<IConfiguration>();
        try { HomeContinuityKeyTransfer.ValidateDestinationConfiguration(configuration, destinationRing); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        { return Fail(ex.Message); }
        if (!string.Equals(verifiedBackup, verifiedBackupArchiveHash, StringComparison.OrdinalIgnoreCase))
            return Fail("--verified-backup and --verified-backup-archive-sha256 must identify the same verified archive.");
        if (!IsSha256(connectorFingerprint))
            return Fail("--connector-public-fingerprint must be a SHA-256 fingerprint.");

        var destination = ReadDestinationIdentity(configuration, connectorDeployment);

        using var certificate = LoadCertificate(certificatePath, certificateKeyPath, requirePrivateKey: true);
        var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator,
            HomeContinuityKeyTransfer.CanonicalTargetApplicationName, sourceProtection, verifiedBackup,
            connectorDeployment, connectorKey, connectorFingerprint, verifiedBackupArchiveHash);
        var applied = HomeContinuityKeyTransfer.ApplyPackage(package, destinationRing, certificate,
            HomeContinuityKeyTransfer.CanonicalTargetApplicationName, binding, manifestSha256,
            destination.InstallationRoot, destination.ConnectorPath, destination.ServiceSid);
        Console.WriteLine($"HOME CONTINUITY RING INSTALLED: {applied.PackageId:D}");
        Console.WriteLine($"Keys: {applied.Keys.Count}; revocations: {applied.Revocations.Count}");
        Console.WriteLine("Destination key files were rewrapped with LocalMachine DPAPI; no source file was modified.");
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RecoverHomeContinuityAsync(IServiceProvider services, string[] args)
    {
        var action = RequiredValue(args, "--action").ToLowerInvariant();
        if (action is not ("complete" or "abort")) return Fail("--action must be complete or abort.");
        var package = RequiredValue(args, "--package");
        var destinationRing = RequiredValue(args, "--destination-ring");
        var packageIdText = RequiredValue(args, "--package-id");
        if (!Guid.TryParse(packageIdText, out var packageId) || packageId == Guid.Empty)
            return Fail("--package-id must be a non-empty GUID.");
        var sourceInstallation = RequiredValue(args, "--source-installation");
        var sourceDiscriminator = RequiredValue(args, "--source-discriminator");
        var sourceProtection = RequiredValue(args, "--source-protection");
        var verifiedBackup = RequiredValue(args, "--verified-backup");
        var verifiedBackupArchiveHash = RequiredValue(args, "--verified-backup-archive-sha256");
        var connectorDeployment = RequiredValue(args, "--connector-deployment");
        var connectorKey = RequiredValue(args, "--connector-key");
        var connectorFingerprint = RequiredValue(args, "--connector-public-fingerprint");
        var manifestSha256 = RequiredValue(args, "--manifest-sha256");
        if (!IsSha256(manifestSha256) || !IsSha256(verifiedBackup) || !IsSha256(verifiedBackupArchiveHash)
            || !IsSha256(connectorFingerprint))
            return Fail("Recovery hashes must be SHA-256 values.");
        if (!string.Equals(verifiedBackup, verifiedBackupArchiveHash, StringComparison.OrdinalIgnoreCase))
            return Fail("--verified-backup and --verified-backup-archive-sha256 must identify the same verified archive.");

        var configuration = services.GetRequiredService<IConfiguration>();
        try { HomeContinuityKeyTransfer.ValidateDestinationConfiguration(configuration, destinationRing); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        { return Fail(ex.Message); }
        var destination = ReadDestinationIdentity(configuration, connectorDeployment);
        var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator,
            HomeContinuityKeyTransfer.CanonicalTargetApplicationName, sourceProtection, verifiedBackup,
            connectorDeployment, connectorKey, connectorFingerprint, verifiedBackupArchiveHash);
        var recovered = HomeContinuityKeyTransfer.RecoverPending(package, destinationRing, binding, manifestSha256,
            packageId, destination.InstallationRoot, destination.ConnectorPath, destination.ServiceSid,
            abort: action == "abort");
        Console.WriteLine(action == "abort"
            ? $"HOME CONTINUITY PARTIAL TRANSFER ABORTED: {recovered.PackageId:D}"
            : $"HOME CONTINUITY PARTIAL TRANSFER COMPLETED: {recovered.PackageId:D}");
        Console.WriteLine("The receipt, package identity, destination binding and artifact hashes were verified before the recovery action.");
        await Task.CompletedTask;
        return 0;
    }

    private static X509Certificate2 LoadCertificate(string path, string? keyPath, bool requirePrivateKey)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The continuity transport certificate is missing.", path);
        var certificate = string.IsNullOrWhiteSpace(keyPath)
            ? IsPkcs12(path)
                ? X509CertificateLoader.LoadPkcs12FromFile(path, ReadOnlySpan<char>.Empty,
                    X509KeyStorageFlags.EphemeralKeySet, Pkcs12LoaderLimits.Defaults)
                : X509CertificateLoader.LoadCertificateFromFile(path)
            : X509Certificate2.CreateFromPemFile(path, keyPath);
        if (requirePrivateKey && !certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The destination continuity certificate must include its private key.");
        }
        if (!requirePrivateKey && certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The export continuity certificate must be public-only; keep its private key on the destination.");
        }
        return certificate;
    }

    private static bool IsPkcs12(string path) =>
        Path.GetExtension(path).Equals(".pfx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".p12", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".pkcs12", StringComparison.OrdinalIgnoreCase);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string VerifySourceBackup(string archivePath, string expectedHash, string expectedSourceInstallation)
    {
        if (!File.Exists(archivePath) || !Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The verified HOME backup archive is missing or is not a ZIP archive.", archivePath);
        var sidecar = archivePath + ".sha256";
        if (!File.Exists(sidecar)) throw new InvalidOperationException("The verified HOME backup archive has no SHA-256 sidecar.");
        var actual = Sha256File(archivePath).ToLowerInvariant();
        var sidecarHash = (File.ReadAllText(sidecar).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToLowerInvariant();
        if (!IsSha256(actual) || !string.Equals(actual, sidecarHash, StringComparison.Ordinal)
            || !string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified HOME backup archive, sidecar, and supplied identity do not agree.");
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("The verified HOME backup has no manifest.");
        using var reader = new StreamReader(entry.Open());
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        if (!document.RootElement.TryGetProperty("FormatVersion", out var version) || version.GetInt32() != 2
            || !document.RootElement.TryGetProperty("Instance", out var instance)
            || !instance.TryGetProperty("Classification", out var classification)
            || !string.Equals(classification.GetString(), "HomeCanonical", StringComparison.Ordinal)
            || !instance.TryGetProperty("InstanceId", out var instanceId)
            || string.IsNullOrWhiteSpace(instanceId.GetString()))
            throw new InvalidOperationException("The verified HOME backup manifest is not a HomeCanonical format-2 archive.");
        if (!string.Equals(instanceId.GetString(), expectedSourceInstallation, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified HOME backup instance identity does not match --source-installation.");
        return actual;
    }

    private static HomeContinuityConnectorMaterial ReadConnectorIdentity(
        IConfiguration configuration, string expectedDeploymentId, string expectedKeyId, string sourceInstallation)
    {
        var installationRoot = RequiredConfiguration(configuration, "SiteServer:InstallationRoot");
        var configuredInstance = RequiredConfiguration(configuration, "Instance:InstanceId");
        if (!string.Equals(configuredInstance, sourceInstallation, StringComparison.Ordinal))
            throw new InvalidOperationException("The source runtime Instance:InstanceId does not match --source-installation.");
        var serviceSid = RequiredConfiguration(configuration, "SiteServer:ServiceSid", "SiteServer:RunAsSid");
        var keyPath = RequiredConfiguration(configuration, "Connector:SigningKeyPath");
        var effectiveDeployment = ResolveEffectiveDeploymentId(configuration);
        if (!string.Equals(effectiveDeployment, expectedDeploymentId, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied connector deployment does not match the effective configured runtime deployment.");
        keyPath = Path.GetFullPath(keyPath);
        var root = Path.GetFullPath(installationRoot);
        RequireUnderRoot(root, keyPath, "The source connector signing key");
        if (!File.Exists(keyPath)) throw new FileNotFoundException("The source connector signing key is missing.", keyPath);
        using var key = ECDsa.Create();
        var pem = File.ReadAllText(keyPath);
        key.ImportFromPem(pem);
        var fingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(key.ExportSubjectPublicKeyInfoPem());
        if (!string.Equals(expectedKeyId, fingerprint[..24], StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied connector key ID does not match the source signing key.");
        return new HomeContinuityConnectorMaterial(effectiveDeployment, expectedKeyId, fingerprint, pem,
            root, keyPath, serviceSid);
    }

    private static (string InstallationRoot, string ConnectorPath, string ServiceSid) ReadDestinationIdentity(
        IConfiguration configuration, string expectedDeploymentId)
    {
        var root = Path.GetFullPath(RequiredConfiguration(configuration, "SiteServer:InstallationRoot"));
        var connectorPath = Path.GetFullPath(RequiredConfiguration(configuration, "Connector:SigningKeyPath"));
        var serviceSid = RequiredConfiguration(configuration, "SiteServer:ServiceSid", "SiteServer:RunAsSid");
        var effectiveDeployment = ResolveEffectiveDeploymentId(configuration);
        if (!string.Equals(effectiveDeployment, expectedDeploymentId, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied connector deployment does not match the effective configured destination deployment.");
        RequireUnderRoot(root, connectorPath, "The destination connector signing key");
        return (root, connectorPath, serviceSid);
    }

    private static string ResolveEffectiveDeploymentId(IConfiguration configuration)
    {
        var configured = configuration["Connector:DeploymentId"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var machine = Encoding.UTF8.GetBytes($"{Environment.MachineName}|{Environment.UserDomainName}");
        return "aerolink-local-" + Convert.ToHexString(SHA256.HashData(machine))[..16].ToLowerInvariant();
    }

    private static string RequiredConfiguration(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key]?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        throw new InvalidOperationException($"The maintenance runtime configuration is missing {string.Join(" or ", keys)}.");
    }

    private static void RequireUnderRoot(string root, string path, string description)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The configured installation root is not provisioned.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{description} must be under SiteServer:InstallationRoot.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string? OptionalValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RequiredValue(string[] args, string name) =>
        OptionalValue(args, name) ?? throw new ArgumentException($"{name} is required.");
}
