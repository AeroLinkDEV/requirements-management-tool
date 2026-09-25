namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A PostgreSQL qualification test that runs only against an owned, disposable server named by
/// <c>AEROLINK_MIGRATIONS_CONNECTION</c>, never the persistent AeroLink database on port 54329.
///
/// Without a server the test reports Skipped, never Passed (#1121). A CI lane that must run the qualification
/// sets <c>AEROLINK_REQUIRE_POSTGRES_QUALIFICATION</c>, which removes the skip so a missing connection fails
/// instead. Each test still validates the connection it is given before creating its own database.
/// </summary>
public sealed class DisposablePostgresFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "AEROLINK_MIGRATIONS_CONNECTION";
    public const string RequireVariable = "AEROLINK_REQUIRE_POSTGRES_QUALIFICATION";

    public DisposablePostgresFactAttribute()
    {
        if (!Required && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = $"PostgreSQL qualification skipped: set {ConnectionVariable} to an owned disposable server.";
    }

    private static bool Required
    {
        get
        {
            var required = Environment.GetEnvironmentVariable(RequireVariable);
            return !string.IsNullOrWhiteSpace(required) && !required.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
