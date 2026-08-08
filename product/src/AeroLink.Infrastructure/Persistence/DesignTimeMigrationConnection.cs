namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The explicit connection contract for design-time EF commands.
///
/// Design-time commands must never silently reach the persistent AeroLink demonstration database. The
/// runtime path is unaffected: <c>Program.cs</c> resolves its own connection through the application
/// configuration and calls <c>Database.MigrateAsync()</c>. This contract exists only for
/// <c>dotnet ef</c>, which has no application configuration and must be told where to work.
/// </summary>
public static class DesignTimeMigrationConnection
{
    public const string EnvironmentVariable = "AEROLINK_MIGRATIONS_CONNECTION";

    /// <summary>
    /// Returns the explicitly supplied migration connection, or fails closed.
    ///
    /// The error deliberately carries no connection-string contents or passwords, only the name of the
    /// environment variable and where to find the operating guidance.
    /// </summary>
    public static string Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                "Design-time EF commands require an explicit disposable migration connection. " +
                $"Set the {EnvironmentVariable} environment variable before running 'dotnet ef', and never " +
                "point design-time commands at the persistent AeroLink database. " +
                "See product/docs/OPERATIONS.md for the supported workflow.");
        return configured.Trim();
    }
}
