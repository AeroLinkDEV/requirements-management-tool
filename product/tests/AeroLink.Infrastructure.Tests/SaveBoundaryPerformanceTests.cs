using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Opt-in, repeatable SaveChanges measurements. Setup, aggregate authoring and result verification are
/// outside the measured region. Run this class alone; elapsed time is diagnostic, not a CI speed claim.
/// Allocation uses the process-wide counter because asynchronous saves can resume on another thread.
/// </summary>
public sealed class SaveBoundaryPerformanceTests(ITestOutputHelper output)
{
    [SaveBenchmarkFact]
    public async Task Measure_new_and_existing_children_with_unrelated_tracked_entities()
    {
        foreach (var append in new[] { true, false })
        {
            await MeasureAsync(1, append); // Warm the model and the real save path.
            foreach (var count in new[] { 1, 100, 1000 })
            {
                var samples = new List<Measurement>();
                for (var repeat = 0; repeat < 3; repeat++)
                    samples.Add(await MeasureAsync(count, append));
                output.WriteLine("SAVE_BOUNDARY_MEASUREMENT " + JsonSerializer.Serialize(new
                {
                    scenario = append ? "append-children" : "edit-existing-children",
                    affectedEntities = count,
                    unrelatedTrackedEntities = 1000,
                    samples,
                    medianCommands = samples.OrderBy(x => x.Commands).ElementAt(1).Commands,
                    medianReadCommands = samples.OrderBy(x => x.ReadCommands).ElementAt(1).ReadCommands,
                    medianAllocatedBytes = samples.OrderBy(x => x.AllocatedBytes).ElementAt(1).AllocatedBytes,
                    medianElapsedMilliseconds = samples.OrderBy(x => x.ElapsedMilliseconds).ElementAt(1).ElapsedMilliseconds,
                }));
            }
        }
    }

    private static async Task<Measurement> MeasureAsync(int count, bool append)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var probe = new CommandProbe();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).AddInterceptors(probe).Options;
        var now = DateTimeOffset.UtcNow;
        Guid requestId;
        await using (var setup = new AeroLinkDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Save boundary measurements", "SBM");
            var project = new ProjectRecord(program.Id, "Save boundary project", "Measured product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            setup.AddRange(program, project, release);
            await setup.SaveChangesAsync();
            setup.Add(LegacyDefaultProjectLadderFactory.Create(project.Id, now));
            await setup.SaveChangesAsync();
            var request = new SystemChangeRequest("SRCR-00967", 0, project.Id, release.Id,
                "Save boundary sample", "Problem", "Analysis", "Solution", "author", now);
            if (!append)
                for (var i = 1; i <= count; i++) AddChild(request, i, now);
            setup.Add(request);
            setup.AddRange(Enumerable.Range(1, 1000).Select(i => new RequirementArtifact(
                project.Id, $"SYSR-{90000000 + i:D8}", RequirementLevel.System, now)));
            await setup.SaveChangesAsync();
            requestId = request.Id;
            Assert.True((await setup.ProjectLadderConfigurations.SingleAsync()).IsSealed);
        }

        await using var db = new AeroLinkDbContext(options);
        var changed = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .Include(x => x.AuditEvents).SingleAsync(x => x.Id == requestId);
        var unrelated = await db.Requirements.ToListAsync();
        Assert.Equal(1000, unrelated.Count);
        var originalVersion = changed.Version;
        var originalIds = changed.RequirementChanges.Select(x => x.Id).ToHashSet();
        if (append)
            for (var i = 1; i <= count; i++) AddChild(changed, i, now.AddMinutes(1));
        else
            foreach (var child in changed.RequirementChanges.ToArray())
                changed.RebaseRequirementChange("author", child.Id, 1, "Reapplied requirement text.",
                    "SRCR-00968.00", now.AddMinutes(1));

        probe.Enabled = true;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        await db.SaveChangesAsync();
        watch.Stop();
        var measurement = new Measurement(probe.Commands, probe.ReadCommands,
            GC.GetTotalAllocatedBytes(precise: true) - allocated, watch.Elapsed.TotalMilliseconds);
        probe.Enabled = false;

        Assert.Equal(originalVersion + 1, changed.Version);
        Assert.All(db.ChangeTracker.Entries<RequirementArtifact>(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        await using var verification = new AeroLinkDbContext(options);
        var saved = await verification.RequirementChanges.AsNoTracking()
            .Where(x => x.ChangeRequestId == requestId).ToListAsync();
        Assert.Equal(count, saved.Count);
        if (!append)
        {
            Assert.True(originalIds.SetEquals(saved.Select(x => x.Id)));
            Assert.All(saved, row => Assert.Equal(1, row.Revision));
        }
        Assert.Equal(1000, await verification.Requirements.CountAsync());
        return measurement;
    }

    private static void AddChild(SystemChangeRequest request, int index, DateTimeOffset now) =>
        request.AddRequirementChange("author", $"SYSR-{index:D8}", 0, RequirementLevel.System,
            RequirementChangeKind.Modify, "Proposed requirement text.", "Measurement rationale", "Test", now);

    private sealed record Measurement(int Commands, int ReadCommands, long AllocatedBytes, double ElapsedMilliseconds);

    private sealed class CommandProbe : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Commands { get; private set; }
        public int ReadCommands { get; private set; }
        private void Record(DbCommand command)
        {
            if (!Enabled) return;
            Commands++;
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                ReadCommands++;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
    }

    private sealed class SaveBenchmarkFactAttribute : FactAttribute
    {
        public SaveBenchmarkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AEROLINK_SAVE_BENCHMARK") != "1")
                Skip = "Set AEROLINK_SAVE_BENCHMARK=1 and run this class alone for repeatable save measurements.";
        }
    }
}
