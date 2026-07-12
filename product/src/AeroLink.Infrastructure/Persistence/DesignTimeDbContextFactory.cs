using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AeroLink.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AeroLinkDbContext>
{
    public AeroLinkDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")
            ?? "Host=127.0.0.1;Port=55432;Database=aerolink;Username=postgres";
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
        return new AeroLinkDbContext(options);
    }
}
