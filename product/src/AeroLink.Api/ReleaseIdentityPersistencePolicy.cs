using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Api;

/// <summary>Maps provider races in the shared release identity authority to a safe retry response.</summary>
internal static class ReleaseIdentityPersistencePolicy
{
    public static bool IsIdentityRace(DbUpdateException exception)
    {
        var root = exception.GetBaseException();
        if (root is PostgresException { SqlState: "23505" or "40001" or "40P01" }) return true;
        var message = root.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("serialization failure", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deadlock detected", StringComparison.OrdinalIgnoreCase)
            || message.Contains("40001", StringComparison.OrdinalIgnoreCase)
            || message.Contains("40P01", StringComparison.OrdinalIgnoreCase);
    }
}
