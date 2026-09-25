using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The safety contract every PostgreSQL qualification relies on (#1122). An unsafe server is refused, never
/// rewritten to another target, and the only database a test may migrate or drop is one it created itself.
/// </summary>
[Trait("Category", "PostgresQualification")]
public sealed class DisposablePostgresDatabaseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_server_is_refused(string? connection) =>
        Assert.Throws<InvalidOperationException>(() => DisposablePostgresDatabase.ValidateServer(connection));

    [Theory]
    [InlineData("Host=10.1.2.3;Port=5555;Database=postgres;Username=postgres")]
    [InlineData("Host=pg.internal.example;Port=5555;Database=postgres;Username=postgres")]
    public void A_non_loopback_server_is_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => DisposablePostgresDatabase.ValidateServer(connection));

    [Theory]
    [InlineData("Host=127.0.0.1;Port=54329;Database=postgres;Username=postgres")]
    [InlineData("Host=localhost;Port=54329;Database=aerolink;Username=postgres")]
    public void The_persistent_AeroLink_port_is_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => DisposablePostgresDatabase.ValidateServer(connection));

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=postgres;Username=aerolink")]
    [InlineData("Host=localhost;Port=55447;Database=anything;Username=postgres")]
    public void A_loopback_disposable_server_is_accepted_unchanged(string connection) =>
        Assert.Same(connection, DisposablePostgresDatabase.ValidateServer(connection));

    [Fact]
    public void Each_database_name_is_unique_and_fits_a_postgres_identifier()
    {
        const string prefix = "aerolink_726_qualify_prefeature";
        var first = DisposablePostgresDatabase.UniqueName(prefix);
        var second = DisposablePostgresDatabase.UniqueName(prefix);

        Assert.NotEqual(first, second);
        Assert.StartsWith(prefix + "_", first);
        Assert.InRange(first.Length, prefix.Length + 13, 63);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper_case")]
    [InlineData("quoted\"name")]
    [InlineData("a_prefix_that_is_far_too_long_to_leave_room_for_a_suffix")]
    public void An_unsafe_or_overlong_prefix_is_refused(string prefix) =>
        Assert.Throws<ArgumentException>(() => DisposablePostgresDatabase.UniqueName(prefix));

    [DisposablePostgresFact]
    public async Task The_database_exists_while_held_and_is_dropped_on_dispose()
    {
        string name;
        await using (var database = await DisposablePostgresDatabase.CreateAsync("aerolink_1122_helper"))
        {
            name = database.Name;
            Assert.True(await ExistsAsync(name));
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            Assert.Equal(name, connection.Database);
        }

        Assert.False(await ExistsAsync(name));
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        var server = DisposablePostgresDatabase.ValidateServer(
            Environment.GetEnvironmentVariable(DisposablePostgresFactAttribute.ConnectionVariable));
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }
}
