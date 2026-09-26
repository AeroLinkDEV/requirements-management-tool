using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #1163: the browser hosts' disposable SQLite database skips durability flushes, because one flush on a busy
/// runner disk held a login for 10 to 40 s. The setting must reach every connection EF opens, leave SQLite's
/// own default alone when unset, and refuse a value or provider it cannot honour.
/// </summary>
public sealed class SqliteSynchronousTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aerolink-synchronous-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Off_reaches_the_connections_ef_opens()
    {
        Assert.Equal(0, await EfSynchronousAsync("Off"));
        // Every open, not only the first: pooled connections are reopened for each operation.
        Assert.Equal(0, await EfSynchronousAsync("off"));
    }

    [Fact]
    public async Task Unset_leaves_sqlite_default_untouched()
    {
        await using var raw = new SqliteConnection($"Data Source={_path};Pooling=False");
        await raw.OpenAsync();
        await using var command = raw.CreateCommand();
        command.CommandText = "PRAGMA synchronous";
        var sqliteDefault = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.NotEqual(0, sqliteDefault);
        Assert.Equal(sqliteDefault, await EfSynchronousAsync(null));
    }

    [Theory]
    [InlineData("Sqlite", "Fast", "SQLite defines Off, Normal, Full and Extra")]
    [InlineData("PostgreSql", "Off", "applies only to Database:Provider Sqlite")]
    public void A_value_or_provider_it_cannot_honour_is_refused_at_startup(string provider, string value, string reason)
    {
        var problem = Assert.Throws<InvalidOperationException>(() => Services(provider, value));
        Assert.Contains(reason, problem.Message);
    }

    private async Task<int> EfSynchronousAsync(string? level)
    {
        await using var provider = Services("Sqlite", level).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA synchronous";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private IServiceCollection Services(string provider, string? level) =>
        new ServiceCollection().AddLogging().AddAeroLinkInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["ConnectionStrings:AeroLink"] = provider == "Sqlite"
                    ? $"Data Source={_path}"
                    : "Host=127.0.0.1;Port=5432;Database=unused;Username=unused;Password=unused",
                [SqliteSynchronousInterceptor.Key] = level,
            }).Build());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(file); } catch (IOException) { /* a leftover temp file costs nothing */ }
    }
}
