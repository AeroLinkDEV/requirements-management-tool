using AeroLink.Domain.Identity;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore;
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
        Console.WriteLine("Applying semantic upgrades...");
        await services.GetRequiredService<SoftwareVerificationCaseMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<ProjectLeadershipMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<ProjectLeadershipReconciliationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<TestChangeRequestPrefixMigrationAuthority>().EnsureCompletedAsync();
        await services.GetRequiredService<SoftwareProcedureExecutionCutoverAuthority>().EnsureCompletedAsync();

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
}
