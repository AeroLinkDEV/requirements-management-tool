using AeroLink.Domain.Identity;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
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
///       --connector-deployment &lt;id&gt; --connector-key &lt;id&gt; --target-application &lt;name&gt;
///       --certificate &lt;public-cert.pem|cer&gt; [--source-ring &lt;captured-directory&gt;]
///       Exports an identity-bound, certificate-wrapped ring package without printing key material.
///
///   maintenance home-continuity apply --package &lt;directory&gt; --destination-ring &lt;new-directory&gt;
///       --source-installation &lt;id&gt; --source-discriminator &lt;exact-value&gt; --source-protection &lt;provider&gt;
///       --verified-backup &lt;id&gt; --connector-deployment &lt;id&gt; --connector-key &lt;id&gt; --manifest-sha256 &lt;hash&gt;
///       --certificate &lt;cert.pem|pfx&gt; [--certificate-key &lt;private-key.pem&gt;]
///       Rewraps the package with LocalMachine DPAPI and refuses a pre-existing destination ring.
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
            Console.WriteLine("  maintenance home-continuity export --package <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <id> --connector-deployment <id> --connector-key <id> --target-application <name> --certificate <public-cert.pem|cer> [--source-ring <captured-directory>]");
            Console.WriteLine("  maintenance home-continuity apply --package <directory> --destination-ring <new-directory> --source-installation <id> --source-discriminator <exact-value> --source-protection <provider> --verified-backup <id> --connector-deployment <id> --connector-key <id> --manifest-sha256 <hash> --certificate <cert.pem|pfx> [--certificate-key <private-key.pem>]");
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
            _ => Fail("Use 'maintenance home-continuity export' or 'maintenance home-continuity apply'."),
        };
    }

    private static async Task<int> ExportHomeContinuityAsync(IServiceProvider services, string[] args)
    {
        var package = RequiredValue(args, "--package");
        var sourceInstallation = RequiredValue(args, "--source-installation");
        var sourceDiscriminator = RequiredValue(args, "--source-discriminator");
        var sourceProtection = RequiredValue(args, "--source-protection");
        var verifiedBackup = RequiredValue(args, "--verified-backup");
        var connectorDeployment = RequiredValue(args, "--connector-deployment");
        var connectorKey = RequiredValue(args, "--connector-key");
        var targetApplication = RequiredValue(args, "--target-application");
        var certificatePath = RequiredValue(args, "--certificate");
        var sourceRingPath = OptionalValue(args, "--source-ring");

        var actualDiscriminator = services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator;
        if (!string.Equals(actualDiscriminator, sourceDiscriminator, StringComparison.Ordinal))
            return Fail("The supplied source discriminator does not match the current host; continuity refuses to guess or export the wrong source ring.");
        var repository = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
            ?? (!string.IsNullOrWhiteSpace(sourceRingPath) && Directory.Exists(sourceRingPath)
                ? new FileSystemXmlRepository(new DirectoryInfo(sourceRingPath), NullLoggerFactory.Instance)
                : null);
        if (repository is null)
            return Fail("The current Data Protection provider has no explicit XML repository; supply the exact captured source ring with --source-ring.");
        using var certificate = LoadCertificate(certificatePath, keyPath: null, requirePrivateKey: false);
        var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator, targetApplication,
            sourceProtection, verifiedBackup, connectorDeployment, connectorKey);
        var manifest = HomeContinuityKeyTransfer.ExportPackage(
            services.GetRequiredService<IKeyManager>(), repository, certificate, binding, package);
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
        var connectorDeployment = RequiredValue(args, "--connector-deployment");
        var connectorKey = RequiredValue(args, "--connector-key");
        var manifestSha256 = RequiredValue(args, "--manifest-sha256");
        var certificatePath = RequiredValue(args, "--certificate");
        var certificateKeyPath = OptionalValue(args, "--certificate-key");

        var configuration = services.GetRequiredService<IConfiguration>();
        var targetApplication = configuration["DataProtection:ApplicationName"]?.Trim();
        if (string.IsNullOrWhiteSpace(targetApplication))
            return Fail("DataProtection:ApplicationName must be the explicit destination application name before continuity apply.");
        var configuredRing = configuration["DataProtection:KeyRingPath"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredRing)
            && !string.Equals(Path.GetFullPath(configuredRing), Path.GetFullPath(destinationRing), StringComparison.OrdinalIgnoreCase))
            return Fail("The destination ring does not match DataProtection:KeyRingPath; continuity refuses a mixed installation.");

        using var certificate = LoadCertificate(certificatePath, certificateKeyPath, requirePrivateKey: true);
        var binding = new HomeContinuityIdentityBinding(sourceInstallation, sourceDiscriminator,
            targetApplication, sourceProtection, verifiedBackup, connectorDeployment, connectorKey);
        var applied = HomeContinuityKeyTransfer.ApplyPackage(package, destinationRing, certificate, targetApplication, binding, manifestSha256);
        Console.WriteLine($"HOME CONTINUITY RING INSTALLED: {applied.PackageId:D}");
        Console.WriteLine($"Keys: {applied.Keys.Count}; revocations: {applied.Revocations.Count}");
        Console.WriteLine("Destination key files were rewrapped with LocalMachine DPAPI; no source file was modified.");
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

    private static string? OptionalValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RequiredValue(string[] args, string name) =>
        OptionalValue(args, name) ?? throw new ArgumentException($"{name} is required.");
}
