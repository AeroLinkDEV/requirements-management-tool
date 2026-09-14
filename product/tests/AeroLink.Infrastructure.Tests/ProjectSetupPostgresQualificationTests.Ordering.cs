using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Canonical_release_keyset_pages_preserve_historical_versions_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options);
            await SoftwareReleaseOrderingQueryTests.AssertCanonicalPagesAsync(db);
        });
    }
}
