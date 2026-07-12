using System.Diagnostics;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var profile = Option("--profile") ?? "small";
var connection = Environment.GetEnvironmentVariable("AEROLINK_SCALE_CONNECTION")
    ?? "Host=127.0.0.1;Port=55432;Database=aerolink_scale;Username=postgres";
var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
await using var db = new AeroLinkDbContext(options);

if (command == "generate") await Generate();
else if (command == "benchmark") await Benchmark();
else Console.WriteLine("Usage: dotnet run -- generate|benchmark [--profile smoke|small|medium] [--reset]");

string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

async Task Generate()
{
    (int Scrs, int Requirements) counts = profile switch { "smoke" => (200, 1_000), "medium" => (10_000, 50_000), _ => (1_000, 5_000) };
    if (args.Contains("--reset"))
    {
        if (!connection.Contains("aerolink_scale", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("--reset is allowed only for an aerolink_scale database.");
        await db.Database.EnsureDeletedAsync();
    }
    await db.Database.MigrateAsync();
    if (await db.Programs.AnyAsync()) throw new InvalidOperationException("Scale database already contains data. Use --reset for the dedicated aerolink_scale database.");
    var program = new ProgramRecord("Synthetic Flight Controls Program", "SYNFC");
    var project = new ProjectRecord(program.Id, "Flight Controls Software", "Synthetic Flight Controls Software");
    var release = new SoftwareRelease(project.Id, "6.4", false);
    db.AddRange(program, project, release); await db.SaveChangesAsync();
    var start = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var perScr = counts.Requirements / counts.Scrs;
    var extra = counts.Requirements % counts.Scrs;
    var stopwatch = Stopwatch.StartNew();
    for (var i = 1; i <= counts.Scrs; i++)
    {
        var now = start.AddMinutes(i);
        var scr = new SystemChangeRequest($"SCR-{i:D8}", 0, project.Id, release.Id, $"Synthetic controlled change {i}",
            $"Synthetic problem statement {i}.", $"Synthetic impact analysis {i}.", $"Synthetic proposed solution {i}.", "author.synthetic", now);
        var requirementCount = perScr + (i <= extra ? 1 : 0);
        for (var r = 0; r < requirementCount; r++)
        {
            var number = (i - 1) * perScr + Math.Min(i - 1, extra) + r + 1;
            scr.AddRequirementChange("author.synthetic", $"SWR-{number:D8}", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, $"The synthetic software shall provide deterministic capability {number}.",
                "Generated for repeatable scale validation.", "Test", now.AddSeconds(r + 1));
        }
        if (i % 5 != 0)
        {
            var approvers = new[] { new ApproverSelection("reviewer.systems", "Systems Reviewer"), new ApproverSelection("reviewer.safety", "Safety Reviewer"), new ApproverSelection("manager.approval", "Engineering Manager") };
            scr.SubmitForReview("author.synthetic", approvers, now.AddMinutes(1));
            if (i % 5 >= 2) { scr.ApproveActiveStage("reviewer.systems", now.AddMinutes(2)); scr.ApproveActiveStage("reviewer.safety", now.AddMinutes(3)); scr.ApproveActiveStage("manager.approval", now.AddMinutes(4)); }
        }
        db.Add(scr);
        if (i % 250 == 0) { await db.SaveChangesAsync(); db.ChangeTracker.Clear(); Console.WriteLine($"Generated {i:N0}/{counts.Scrs:N0} SCRs..."); }
    }
    await db.SaveChangesAsync();
    for (var i = 1; i <= Math.Max(1, counts.Scrs / 50); i++) db.Add(new CandidateBaseline($"SWBL-{i:D8}", 0, project.Id, release.Id, null, $"Synthetic candidate baseline {i}", "cm.synthetic", start.AddDays(i)));
    await db.SaveChangesAsync();
    stopwatch.Stop();
    Console.WriteLine($"Generated {counts.Scrs:N0} SCRs and {counts.Requirements:N0} requirement changes in {stopwatch.Elapsed.TotalSeconds:N1}s using deterministic seed 4754.");
}

async Task Benchmark()
{
    await db.Database.MigrateAsync();
    var projectId = await db.Projects.Select(x => x.Id).FirstAsync();
    var results = new List<object>();
    await Measure("dashboard_aggregates", 2_000, async () => { var q=db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId); _=await q.GroupBy(_=>1).Select(g=>new { Total=g.Count(), Draft=g.Count(x=>x.State==ScrState.Draft), Review=g.Count(x=>x.State==ScrState.InReview)}).SingleAsync(); });
    await Measure("scr_page_50", 500, async () => { _=await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderByDescending(x=>x.UpdatedAt).Take(50).Select(x=>new{x.Id,x.Title,x.State}).ToListAsync(); });
    await Measure("exact_requirement", 300, async () => { _=await db.RequirementChanges.AsNoTracking().Where(x=>x.BaseNumber=="SWR-00002375").Select(x=>new{x.Id,x.Statement}).FirstOrDefaultAsync(); });
    await Measure("requirement_page_50", 500, async () => { _=await db.RequirementChanges.AsNoTracking().OrderBy(x=>x.BaseNumber).Take(50).Select(x=>new{x.Id,x.BaseNumber,x.Statement}).ToListAsync(); });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    async Task Measure(string name, int targetMs, Func<Task> operation) { await operation(); var samples=new List<long>(); for(var i=0;i<5;i++){var sw=Stopwatch.StartNew();await operation();sw.Stop();samples.Add(sw.ElapsedMilliseconds);}var p95=samples.Order().ElementAt(4);results.Add(new{name,targetMs,p95Ms=p95,passed=p95<=targetMs,samples});if(p95>targetMs)Environment.ExitCode=1; }
}
