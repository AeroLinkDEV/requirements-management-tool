using System.Net;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A database of the test's own on the disposable server named by <c>AEROLINK_MIGRATIONS_CONNECTION</c> (#1122).
///
/// The server must be loopback and never the persistent AeroLink port 54329. The database gets a unique name and is
/// dropped on dispose, so a test may migrate, seed or drop it freely, and every PostgreSQL qualification can share
/// one server, including the CI service container. Use it with <see cref="DisposablePostgresFactAttribute"/>.
/// </summary>
internal sealed class DisposablePostgresDatabase : IAsyncDisposable
{
    private const int ProtectedPort = 54329;
    private const int MaximumIdentifierLength = 63;

    private readonly string server;

    private DisposablePostgresDatabase(string server, string name)
    {
        this.server = server;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    /// <summary>Creates <c>{prefix}_{unique suffix}</c> on the validated server.</summary>
    public static async Task<DisposablePostgresDatabase> CreateAsync(string prefix)
    {
        var server = ValidateServer(Environment.GetEnvironmentVariable(DisposablePostgresFactAttribute.ConnectionVariable));
        var name = UniqueName(prefix);
        await ExecuteOnServerAsync(server, $"CREATE DATABASE \"{name}\"");
        return new DisposablePostgresDatabase(server, name);
    }

    /// <summary>
    /// Returns the server connection unchanged, or throws. It never rewrites an unsafe target into a safe one.
    /// </summary>
    internal static string ValidateServer(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                $"PostgreSQL qualification requires {DisposablePostgresFactAttribute.ConnectionVariable} to name a disposable server.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
            throw new InvalidOperationException("PostgreSQL qualification requires a loopback disposable server.");
        if (builder.Port == ProtectedPort)
            throw new InvalidOperationException("PostgreSQL qualification refuses the persistent AeroLink port 54329.");
        return connection;
    }

    internal static string UniqueName(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.All(x => char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x == '_'))
            throw new ArgumentException("A qualification database prefix uses lower-case ASCII letters, digits and underscores.", nameof(prefix));
        // PostgreSQL silently truncates a longer identifier, and the connection would then name another database.
        var suffixLength = Math.Min(32, MaximumIdentifierLength - prefix.Length - 1);
        if (suffixLength < 12)
            throw new ArgumentException("A qualification database prefix must leave room for a 12-character unique suffix.", nameof(prefix));
        return $"{prefix}_{Guid.NewGuid().ToString("N")[..suffixLength]}";
    }

    public async ValueTask DisposeAsync() =>
        await ExecuteOnServerAsync(server, $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");

    private static async Task ExecuteOnServerAsync(string server, string sql)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server)
        { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
