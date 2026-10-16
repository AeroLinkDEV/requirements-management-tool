using AeroLink.ConnectorProtocol;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

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
///       --certificate &lt;public-cert.pem|cer&gt; [--source-ring &lt;captured-directory&gt;]
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
            Console.WriteLine("  maintenance home-continuity export --package <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive <verified-backup.zip> --connector-deployment <id> --connector-key <id> --target-application AeroLink --certificate <public-cert.pem|cer> --source-ring <captured-directory>");
            Console.WriteLine("  maintenance home-continuity apply --package <directory> --destination-ring <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive-sha256 <sha256> --connector-deployment <id> --connector-key <id> --connector-public-fingerprint <sha256> --manifest-sha256 <hash> --certificate <cert.pem|pfx> [--certificate-key <private-key.pem>]");
            Console.WriteLine("  maintenance home-continuity recover --action complete|abort --package <directory> --destination-ring <directory> --package-id <guid> --manifest-sha256 <hash> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <sha256> --verified-backup-archive-sha256 <sha256> --connector-deployment <id> --connector-key <id> --connector-public-fingerprint <sha256>");
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
            _ => Fail("Use 'maintenance home-continuity export', 'maintenance home-continuity apply' or 'maintenance home-continuity recover'."),
        };
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
        if (!string.Equals(targetApplication, HomeContinuityKeyTransfer.CanonicalTargetApplicationName, StringComparison.Ordinal))
            return Fail("The continuity destination application must be exactly 'AeroLink'.");
        var backupHash = VerifySourceBackup(verifiedBackupArchive, verifiedBackup, sourceInstallation);
        var configuration = services.GetRequiredService<IConfiguration>();
        var connector = ReadConnectorIdentity(configuration, connectorDeployment, connectorKey, sourceInstallation);
        using var sourceServices = HomeContinuityKeyTransfer.BuildSourceServices(sourceRingPath, sourceDiscriminator, sourceProtection);
        var sourceManager = sourceServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
        var sourceRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
            new DirectoryInfo(sourceRingPath), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        using var certificate = LoadCertificate(certificatePath, keyPath: null, requirePrivateKey: false);
        var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator, targetApplication,
            sourceProtection, backupHash, connectorDeployment, connectorKey, connector.PublicKeyFingerprint, backupHash);
        var manifest = HomeContinuityKeyTransfer.ExportPackage(
            sourceManager, sourceRepository, certificate, binding, connector, package);
        Console.WriteLine($"HOME CONTINUITY PACKAGE EXPORTED: {manifest.PackageId:D}");
        Console.WriteLine($"Keys: {manifest.Keys.Count}; revocations: {manifest.Revocations.Count}");
        Console.WriteLine($"Manifest SHA-256: {Sha256File(Path.Combine(package, "manifest.json"))}");
        Console.WriteLine("Key descriptors and protected values were not printed or written in plaintext.");
        await Task.CompletedTask;
        return 0;
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
