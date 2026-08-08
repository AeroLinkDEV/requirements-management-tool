using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AeroLink.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AeroLinkDbContext>
{
    public AeroLinkDbContext CreateDbContext(string[] args)
    {
        var connection = DesignTimeMigrationConnection.Resolve(
            Environment.GetEnvironmentVariable(DesignTimeMigrationConnection.EnvironmentVariable));
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
        return new AeroLinkDbContext(options);
    }
}
