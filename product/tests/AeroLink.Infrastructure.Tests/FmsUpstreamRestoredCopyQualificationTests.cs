using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A manual qualification (#1122): it runs only against a disposable restored copy of the FMS showcase, which no
/// CI lane has. Run it before applying the #1006 correction, as OPERATIONS.md describes for
/// <c>Repair-FmsDemoHistory.ps1</c>.
/// </summary>
public sealed class FmsUpstreamRestoredCopyQualificationTests
{
    [RestoredCopyFact]
    public async Task Explicit_upgrade_corrects_the_owned_copy_and_preserves_every_existing_review_snapshot()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEROLINK_1006_RESTORED_CONNECTION"));
        var evidenceRoot = Environment.GetEnvironmentVariable("AEROLINK_1006_RESTORED_EVIDENCE");
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Port == 54329
            || connection.Database != "aerolink_1006_validation" || string.IsNullOrWhiteSpace(evidenceRoot)
            || !Path.GetFullPath(evidenceRoot).Contains(Path.DirectorySeparatorChar + "restore-validation" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This opt-in correction qualification requires the named restored disposable database and its isolated evidence tree.");
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection.ConnectionString).Options);
        var program = await db.Programs.AsNoTracking().SingleAsync(x => x.Code == "FMSLIVE");
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.ProgramId == program.Id);
        var before = await db.ReviewCycles.AsNoTracking().Select(x => new { x.Id, x.SnapshotJson, x.SnapshotHash }).ToListAsync();
        var seeder = new FmsShowcaseSeeder(db, evidenceStore: new EvidenceFileStore(evidenceRoot));
        await seeder.UpgradeAsync(program.Id);
        db.ChangeTracker.Clear();
        var after = await db.ReviewCycles.AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.All(before, cycle => {
            Assert.Equal(cycle.SnapshotJson, after[cycle.Id].SnapshotJson);
            Assert.Equal(cycle.SnapshotHash, after[cycle.Id].SnapshotHash);
        });
        var invalid = await (from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
            join child in db.SystemChangeRequests.AsNoTracking() on link.ChangeRequestId equals child.Id
            join source in db.SystemChangeRequests.AsNoTracking() on link.UpstreamChangeRequestId equals source.Id
            where child.ProjectId == project.Id && source.State != ChangeRequestState.Approved
                && source.State != ChangeRequestState.SelectedForBaseline
            select link.Id).CountAsync();
        Assert.Equal(0, invalid);
        var policy = await new EffectiveProjectLadderPolicyResolver(db).ResolveAsync(project.Id);
        foreach (var number in new[] { "HLRCR-00136", "LLRCR-00134" })
        {
            var root = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.ProjectId == project.Id && x.BaseNumber == number);
            var direct = await ChangeRequestTraceProjection.ForChangeRequestAsync(db, project.Id, root.Id,
                policy, CancellationToken.None, directOnly: true);
            Assert.NotNull(direct);
            Assert.True(direct.DirectOnly);
            Assert.Contains(direct.Edges, edge => edge.ToId == root.Id && edge.Provenance.Any(fact => fact.Kind == "AuthorStated" && fact.IsLive));
            Assert.All(direct.Edges, edge => Assert.True(edge.FromId == root.Id || edge.ToId == root.Id));
        }
        var second = await seeder.UpgradeAsync(program.Id);
        Assert.DoesNotContain(second, step => step.StartsWith("approved-upstream-correction-1006:", StringComparison.Ordinal));
    }

    private sealed class RestoredCopyFactAttribute : FactAttribute
    {
        public RestoredCopyFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_1006_RESTORED_CONNECTION")))
                Skip = "Manual qualification: set AEROLINK_1006_RESTORED_CONNECTION (database aerolink_1006_validation) and "
                    + "AEROLINK_1006_RESTORED_EVIDENCE for a disposable restored FMS copy; see OPERATIONS.md.";
        }
    }
}
