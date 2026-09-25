using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AeroLink.Api.Tests;

internal sealed class AeroLinkApiFactory(bool seedDemoAccounts = false, bool allowDemoAccounts = false,
    string? showcaseTemplate = null, string? staticFilesRoot = null,
    DbCommandInterceptor? commandInterceptor = null,
    IManagedDocumentStorageFaultInjector? storageFaultInjector = null,
    ILadderPolicy? testLadderPolicy = null,
    bool attachProjectLadders = true,
    bool enableEnterpriseJobWorker = false,
    Action<object>? telemetryObserver = null,
    string? postgresConnection = null,
    [CallerFilePath] string? callerFile = null,
    [CallerMemberName] string? callerMember = null) : WebApplicationFactory<Program>
{
    public const string BootstrapSecret = "test-bootstrap-secret-0123456789-abcdef";
    public const string AdministratorPassword = "Bootstrap-Admin!2026";
    public const string MemberPassword = "Program-Member!2026";
    // Keep Microsoft.Data.Sqlite's provider retry budget at the previous 30-second value. This is the
    // SQLITE_BUSY/SQLITE_LOCKED retry budget, not a whole-command wall-clock budget.
    internal const int CommandTimeoutSeconds = 30;
    private readonly DisposableDatabase _database = NewDatabase(showcaseTemplate);
    private string DatabasePath => _database.Path;
    private readonly string? _postgresConnection = postgresConnection;
    public string ConnectionString => _postgresConnection ?? DatabaseConnectionString(DatabasePath);

    /// <summary>
    /// The database file, together with the one connection that must stay open while it is in use.
    ///
    /// WAL mode only helps while the WAL index exists. SQLite builds that index — the <c>-shm</c> file — when
    /// the first connection to a database opens, and tears it down, checkpointing and unlinking <c>-wal</c> and
    /// <c>-shm</c>, when the last one closes. Both ends take an exclusive lock on the whole database, and an
    /// exclusive lock blocks readers, which is the one thing WAL exists to prevent.
    ///
    /// <c>Pooling=False</c> means EF opens a real connection per operation and closes it again, so the count
    /// returns to zero between almost every statement. Sampled on an idle host before this connection was held,
    /// the index was absent for 195 of 200 samples: #601 turned WAL on, but nothing kept it on, so operations
    /// were paying for an index build and teardown they need not have paid for. Holding one warmed connection
    /// removes both windows, and WAL behaves as #601 intended.
    ///
    /// That is a necessary part of #593's recurrence, not the whole of it. A single one of those windows is
    /// sub-millisecond; exhausting a 30-second budget also required the runner starvation visible in the same
    /// shard, where this test's host build took 27.8 s against a median of 1.0 s. This removes the windows. It
    /// does not remove the starvation, so treat a quiet suite as encouraging rather than as proof.
    ///
    /// The connection must stay idle. A read transaction left open on it pins the WAL and stops autocheckpoint
    /// reclaiming it, so the file grows without bound; it exists to hold the index, and nothing else.
    /// </summary>
    private sealed record DisposableDatabase(string Path, SqliteConnection WalIndexKeepAlive);

    /// <summary>
    /// A private database file, optionally starting as a copy of an already-seeded showcase.
    ///
    /// Three tests in this assembly seeded the FMS showcase inside their own factory, which is 40 to 60 seconds
    /// each and was 177 of the assembly's 552 CPU-seconds for a dataset identical all three times. The copy
    /// happens before the host starts, so the API opens a database that is already populated and its startup
    /// EnsureCreated finds nothing to do.
    /// </summary>
    private static DisposableDatabase NewDatabase(string? template)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-api-tests-{Guid.NewGuid():N}.db");
        SqliteConnection? keepAlive = null;
        try
        {
            if (template is not null)
            {
                // Only the .db is copied. That is safe while the template is written in DELETE mode, but a
                // template in WAL mode can hold committed rows in its -wal, and copying the .db alone would
                // silently drop them — a half-seeded showcase surfacing as an assertion failure somewhere else
                // entirely. Fail closed instead of copying something that is missing its tail.
                if (File.Exists(template + "-wal"))
                    throw new InvalidOperationException(
                        $"The showcase template '{template}' has an unmerged -wal; copying the database alone "
                        + "would lose committed rows. Checkpoint the template before using it as a template.");
                File.Copy(template, path);
            }
            // The API host and test-scoped contexts intentionally use separate connections to this file. WAL lets
            // readers run while a writer is active; the provider retry budget handles remaining serialized-writer
            // contention without changing the product's PostgreSQL or SQLite configuration.
            keepAlive = new SqliteConnection(DatabaseConnectionString(path));
            keepAlive.Open();
            using var command = keepAlive.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite API test databases must support WAL mode.");
            // Setting the pragma is not the same as joining the WAL index: SQLite attaches a connection to the
            // index when that connection first reads or writes, and only an attached connection keeps the index
            // alive once the others close. Read something that exists before the schema does.
            using var warm = keepAlive.CreateCommand();
            warm.CommandText = "SELECT count(*) FROM sqlite_master;";
            warm.ExecuteScalar();
            return new DisposableDatabase(path, keepAlive);
        }
        catch
        {
            keepAlive?.Dispose();
            DeleteDatabaseArtifacts(path);
            throw;
        }
    }

    private static string DatabaseConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = CommandTimeoutSeconds,
    }.ToString();

    internal static void ConfigureSqliteOptions(DbContextOptionsBuilder options, string connectionString, params IInterceptor[] interceptors)
    {
        options.UseSqlite(connectionString)
            .AddInterceptors(interceptors);
    }

    private readonly string _evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-api-evidence-{Guid.NewGuid():N}");
    private readonly string _connectorKeyPath = Path.Combine(Path.GetTempPath(), $"aerolink-connector-key-{Guid.NewGuid():N}.pem");
    private static long _nextFactoryId;
    private readonly long _factoryId = Interlocked.Increment(ref _nextFactoryId);
    private readonly string _callerFile = callerFile ?? "unknown";
    private readonly string _callerMember = callerMember ?? "unknown";
    private readonly Stopwatch _construction = Stopwatch.StartNew();
    private double _constructionBeforeHostMs;
    private readonly Action<object>? _telemetryObserver = telemetryObserver;
    // WebApplicationFactory.Dispose() enters this virtual method, then synchronously invokes its virtual
    // DisposeAsync(), which returns here once more after stopping the host. Both public disposal paths must
    // start one shared timer before that shutdown, while only one callback owns AeroLink's cleanup and
    // telemetry. Interlocked keeps that ownership singular if a caller races disposal paths.
    private int _aeroLinkDisposeStarted;
    private readonly object _disposalStopwatchGate = new();
    private Stopwatch? _disposalStopwatch;

    internal long TelemetryFactoryId => _factoryId;

    public override async ValueTask DisposeAsync()
    {
        StartDisposalStopwatch();
        await base.DisposeAsync();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Capture construction latency BEFORE base.CreateHost starts: constructionMs and hostMs are
        // non-overlapping intervals. Reading _construction.Elapsed in the finally would include the host
        // build and double-count it in the aggregator (constructionMs + hostMs + disposeMs).
        _constructionBeforeHostMs = _construction.Elapsed.TotalMilliseconds;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            ApiTestTelemetry.RecordFactoryPhase("host", _constructionBeforeHostMs, stopwatch.Elapsed.TotalMilliseconds, _callerFile, _callerMember, _factoryId, _telemetryObserver);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseContentRoot(FindApiContentRoot());
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = _postgresConnection is null ? "Sqlite" : "PostgreSql",
            ["ConnectionStrings:AeroLink"] = ConnectionString,
            ["Evidence:Root"] = _evidenceRoot,
            ["Connector:DeploymentId"] = "aerolink-api-tests",
            ["Connector:SigningKeyPath"] = _connectorKeyPath,
            ["DemoData:Enabled"] = "false",
            ["Identity:SeedDemoAccounts"] = seedDemoAccounts.ToString(),
            ["Identity:AllowDemoAccounts"] = allowDemoAccounts.ToString(),
            ["Identity:BootstrapSecret"] = BootstrapSecret,
            ["Identity:CookieSecure"] = "false",
            ["Identity:LoginRateLimitPerMinute"] = "500",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
        };
        if (staticFilesRoot is not null) settings["Client:StaticFiles"] = staticFilesRoot;
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AeroLinkDbContext>();
            services.RemoveAll<DbContextOptions<AeroLinkDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AeroLinkDbContext>>();
            services.AddDbContext<AeroLinkDbContext>(options =>
            {
                if (_postgresConnection is null)
                    ConfigureSqliteOptions(options, ConnectionString, new SaveRaceInterceptor(),
                        new TimingConnectionInterceptor(_factoryId, _callerFile, _callerMember, _telemetryObserver));
                else
                    options.UseNpgsql(ConnectionString).AddInterceptors(
                        new TimingConnectionInterceptor(_factoryId, _callerFile, _callerMember, _telemetryObserver));
                if (attachProjectLadders) options.AddInterceptors(new TestProjectLadderInterceptor());
                if (commandInterceptor is not null) options.AddInterceptors(commandInterceptor);
            });
            // Test hosts must not run the five production polling workers: each opens independent SQLite
            // connections, and their idle commands make request-count and lock-contention evidence noisy.
            // Removing them is independent of any command interceptor; the two publication suites explicitly
            // opt back into the one worker they exercise. Keep all other production workers out of API tests.
            services.RemoveAll<IHostedService>();
            if (enableEnterpriseJobWorker) services.AddHostedService<EnterpriseJobWorker>();
            // Each host keeps its key ring in memory. By default every host in this process shares the runner user's
            // DataProtection-Keys folder, and parallel classes on a fresh runner raced to create its first key: one
            // host read the file while another was still writing it, and the CSRF read failed with a 500 (#1130).
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            if (storageFaultInjector is not null)
            {
                services.RemoveAll<IManagedDocumentStorageFaultInjector>();
                services.AddSingleton(storageFaultInjector);
            }
            if (testLadderPolicy is not null)
            {
                services.RemoveAll<IProjectLadderPolicyResolver>();
                services.AddSingleton<IProjectLadderPolicyResolver>(new FixedProjectLadderPolicyResolver(testLadderPolicy));
            }
        });
    }

    /// <summary>
    /// Tidying up, which must never be the reason a test is reported as failed.
    ///
    /// On Windows CI a handle to the throwaway database occasionally outlives the host that opened it, and
    /// <c>File.Delete</c> then throws from inside <c>Dispose</c> — turning a test whose every assertion passed
    /// into a red one, with a stack trace that says nothing about the product. The file lives in the system
    /// temp directory; leaving one behind costs nothing, and losing the signal costs a great deal.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        var stopwatch = StartDisposalStopwatch();
        if (Interlocked.Exchange(ref _aeroLinkDisposeStarted, 1) != 0) return;

        ExceptionDispatchInfo? baseDisposeException = null;
        ExceptionDispatchInfo? cleanupException = null;
        try
        {
            try
            {
                base.Dispose(disposing);
            }
            catch (Exception problem)
            {
                baseDisposeException = ExceptionDispatchInfo.Capture(problem);
            }
        }
        finally
        {
            // Released after the host, so the WAL index outlives every connection the host owns and is torn down
            // once, here, rather than between statements. Deliberately outside the cleanup block below and
            // deliberately swallowing: while this connection is open Windows refuses to delete the database and
            // its sidecars, so letting it throw here would skip every remaining step, guarantee a three-file
            // leak, and fail a test whose assertions all passed — the exact outcome this method exists to avoid.
            try { _database.WalIndexKeepAlive.Dispose(); }
            catch (Exception) { }

            try
            {
                SqliteConnection.ClearAllPools();
                DeleteDatabaseArtifacts(DatabasePath);
                try { if (Directory.Exists(_evidenceRoot)) Directory.Delete(_evidenceRoot, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                DeleteIfPresent(_connectorKeyPath);
            }
            catch (Exception problem)
            {
                cleanupException = ExceptionDispatchInfo.Capture(problem);
            }

            try
            {
                ApiTestTelemetry.RecordFactoryPhase("dispose", _constructionBeforeHostMs, stopwatch.Elapsed.TotalMilliseconds, _callerFile, _callerMember, _factoryId, _telemetryObserver);
            }
            catch when (baseDisposeException is not null || cleanupException is not null)
            {
                // Preserve the first failure; telemetry must not mask a disposal or cleanup exception.
            }
        }

        if (baseDisposeException is not null) baseDisposeException.Throw();
        if (cleanupException is not null) cleanupException.Throw();
    }

    private Stopwatch StartDisposalStopwatch()
    {
        lock (_disposalStopwatchGate)
        {
            return _disposalStopwatch ??= Stopwatch.StartNew();
        }
    }

    internal static void DeleteDatabaseArtifacts(string path)
    {
        DeleteIfPresent(path);
        DeleteIfPresent(path + "-shm");
        DeleteIfPresent(path + "-wal");
    }

    // Retried briefly before being given up on, because the usual cause is a handle closing a moment late
    // rather than one held for good.
    private static void DeleteIfPresent(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return;
                Thread.Sleep(100);
            }
        }
    }

    private static string FindApiContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx"))) current = current.Parent;
        if (current is null) throw new InvalidOperationException("Could not locate the product solution root for API tests.");
        return Path.Combine(current.FullName, "src", "AeroLink.Api");
    }
}
