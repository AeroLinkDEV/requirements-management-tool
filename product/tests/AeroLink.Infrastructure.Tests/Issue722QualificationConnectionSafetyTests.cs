using AeroLink.Infrastructure.Persistence;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The #722 qualification connection contract, extracted from the fixture so the refusal behaviour is itself
/// directly tested rather than only exercised incidentally by a skipped-or-running qualification.
///
/// The contract is fail-closed: an unsafe caller-supplied target is refused, never rewritten to a different
/// database and then deleted. The persistent AeroLink database (port 54329) is refused explicitly; only the
/// dedicated disposable qualification database on a loopback host is accepted.
/// </summary>
public sealed class Issue722QualificationConnectionSafetyTests
{
    [Fact]
    public void Missing_or_blank_connection_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate(null));
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate(""));
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate("   "));
    }

    [Theory]
    [InlineData("Host=10.1.2.3;Port=5555;Database=aerolink_722_qualify;Username=postgres")]
    [InlineData("Host=pg.internal.example;Port=5555;Database=aerolink_722_qualify;Username=postgres")]
    public void Non_loopback_hosts_are_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate(connection));

    [Theory]
    [InlineData("Host=127.0.0.1;Port=54329;Database=aerolink_722_qualify;Username=postgres")]
    [InlineData("Host=localhost;Port=54329;Database=aerolink_722_qualify;Username=postgres")]
    public void The_protected_persistent_port_is_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate(connection));

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5555;Database=aerolink;Username=postgres")]
    [InlineData("Host=127.0.0.1;Port=5555;Database=postgres;Username=postgres")]
    [InlineData("Host=127.0.0.1;Port=5555;Database=aerolink_722_qualify_typo;Username=postgres")]
    public void A_database_other_than_the_dedicated_qualification_database_is_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => Issue722QualificationConnection.Validate(connection));

    [Fact]
    public void A_compliant_dedicated_loopback_connection_is_accepted_unchanged()
    {
        const string connection = "Host=127.0.0.1;Port=55555;Database=aerolink_722_qualify;Username=postgres";
        Assert.Same(connection, Issue722QualificationConnection.Validate(connection));
    }
}

/// <summary>The connection contract shared by both #722 qualification cases.</summary>
internal static class Issue722QualificationConnection
{
    private const string DatabaseName = "aerolink_722_qualify";

    /// <summary>
    /// Validates the caller-supplied connection and returns it unchanged. Every violation throws: the
    /// fixture must never transform an unsafe target into a "safe" one and then run destructive operations
    /// against it.
    /// </summary>
    public static string Validate(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #722 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #722 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #722 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Issue #722 PostgreSQL qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }
}
