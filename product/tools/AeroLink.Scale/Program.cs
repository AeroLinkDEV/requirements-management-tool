using System.Diagnostics;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var profile = Option("--profile") ?? "small";
var connection = Environment.GetEnvironmentVariable("AEROLINK_SCALE_CONNECTION")
    ?? "Host=127.0.0.1;Port=54329;Database=aerolink_scale;Username=postgres";
var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;
await using var db = new AeroLinkDbContext(options);

if (command == "generate") await Generate();
else if (command == "workspace") await GenerateWorkspace();
else if (command == "benchmark") await Benchmark();
else if (command == "load") await ConcurrentLoad();
else Console.WriteLine("Usage: dotnet run -- generate|workspace|benchmark|load [--profile smoke|small|medium] [--reset] [--users 150] [--iterations 8]");

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
    await Measure("exact_requirement", 300, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber=="HLR-00002375") join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId orderby revision.Revision descending select new{artifact.Id,artifact.BaseNumber,revision.Statement}).FirstOrDefaultAsync(); });
    await Measure("enterprise_page_100", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision) orderby artifact.BaseNumber select new{artifact.Id,artifact.BaseNumber,revision.Statement}).Take(100).ToListAsync(); });
    await Measure("structured_system_test_filter", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Level==RequirementLevel.System) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.VerificationMethod=="Test" select artifact.Id).CountAsync(); });
    await Measure("specification_tree", 500, async () => { _=await db.SpecificationNodes.AsNoTracking().Where(x=>db.RequirementSpecifications.Any(s=>s.Id==x.SpecificationId&&s.ProjectId==projectId)).GroupBy(x=>x.SpecificationId).Select(x=>new{x.Key,Count=x.Count()}).ToListAsync(); });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    async Task Measure(string name, int targetMs, Func<Task> operation) { await operation(); var samples=new List<long>(); for(var i=0;i<5;i++){var sw=Stopwatch.StartNew();await operation();sw.Stop();samples.Add(sw.ElapsedMilliseconds);}var p95=samples.Order().ElementAt(4);results.Add(new{name,targetMs,p95Ms=p95,passed=p95<=targetMs,samples});if(p95>targetMs)Environment.ExitCode=1; }
}

async Task GenerateWorkspace()
{
    var requirementCount=profile switch{"smoke"=>1_000,"medium"=>50_000,_=>10_000};
    if(args.Contains("--reset")){if(!connection.Contains("aerolink_scale",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("--reset is allowed only for the dedicated aerolink_scale database.");await db.Database.EnsureDeletedAsync();}
    await db.Database.MigrateAsync();if(await db.Programs.AnyAsync())throw new InvalidOperationException("Scale database already contains data. Use --reset for the dedicated aerolink_scale database.");
    var now=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);var program=new ProgramRecord("AeroLink Enterprise Qualification Program","QUAL");var project=new ProjectRecord(program.Id,"Enterprise FMS Qualification","Qualification Flight Management System");var release=new SoftwareRelease(project.Id,"10.0",false);db.AddRange(program,project,release);await db.SaveChangesAsync();
    var scr=new SystemChangeRequest("SCR-00000001",0,project.Id,release.Id,"Establish enterprise-scale qualification baseline","A repeatable large repository is required.","Generate mixed-level immutable requirements and exact baseline membership.","Establish the controlled qualification dataset.","scale.author",now);scr.AddRequirementChange("scale.author","SYSR-00000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,"The qualification FMS shall support enterprise-scale repository validation.","Scale authority.","Test",now);var approvers=new[]{new ApproverSelection("scale.reviewer","Scale Reviewer")};scr.SubmitForReview("scale.author",approvers,now.AddMinutes(1));scr.ApproveActiveStage("scale.reviewer",now.AddMinutes(2));var baseline=new CandidateBaseline("SYSBL-00000001",0,project.Id,release.Id,null,"10,000-requirement qualification baseline","scale.cm",now);baseline.Select(scr,"scale.cm",now.AddMinutes(3));baseline.Freeze("scale.cm",now.AddMinutes(4));var manifest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"enterprise-workspace:{requirementCount}:4754"))).ToLowerInvariant();baseline.MarkRequirementsMaterialized("scale.cm",manifest,requirementCount,now.AddMinutes(5));db.AddRange(scr,baseline);await db.SaveChangesAsync();
    var sw=Stopwatch.StartNew();var counters=new Dictionary<RequirementLevel,int>{{RequirementLevel.System,0},{RequirementLevel.HighLevel,0},{RequirementLevel.LowLevel,0}};for(var i=1;i<=requirementCount;i++){var level=i<=requirementCount*15/100?RequirementLevel.System:i<=requirementCount*50/100?RequirementLevel.HighLevel:RequirementLevel.LowLevel;var number=++counters[level];var prefix=level switch{RequirementLevel.System=>"SYSR",RequirementLevel.HighLevel=>"HLR",_=>"LLR"};var artifact=new RequirementArtifact(project.Id,$"{prefix}-{number:D8}",level,now.AddSeconds(i));var revision=new RequirementRevision(artifact.Id,0,$"The qualification FMS shall provide deterministic {level} capability {number:D8} at enterprise scale.","Generated using deterministic qualification seed 4754.",(i%4) switch{0=>"Analysis",1=>"Test",2=>"Inspection",_=>"Demonstration"},RequirementRevisionState.Active,scr.Id,baseline.Id,now.AddSeconds(i));db.AddRange(artifact,revision,new BaselineRequirementSelection(baseline.Id,artifact.Id,revision.Id));if(i%500==0){await db.SaveChangesAsync();db.ChangeTracker.Clear();Console.WriteLine($"Materialized {i:N0}/{requirementCount:N0} requirements...");}}
    await db.SaveChangesAsync();await new EnterpriseRequirementsService(db).SynchronizeProjectAsync(project.Id,"scale.workspace");sw.Stop();Console.WriteLine($"Generated a mixed-level {requirementCount:N0}-requirement Enterprise Requirements Workspace in {sw.Elapsed.TotalSeconds:N1}s using deterministic seed 4754.");
}

async Task ConcurrentLoad()
{
    await db.Database.MigrateAsync();
    var projectId=await db.Projects.Select(x=>x.Id).FirstAsync();
    var users=int.TryParse(Option("--users"),out var u)?Math.Clamp(u,1,500):150;
    var iterations=int.TryParse(Option("--iterations"),out var i)?Math.Clamp(i,1,100):8;
    var samples=new System.Collections.Concurrent.ConcurrentBag<long>();var failures=new System.Collections.Concurrent.ConcurrentBag<string>();
    var total=Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0,users).Select(async worker=>
    {
        try
        {
            await using var workerDb=new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options);
            for(var turn=0;turn<iterations;turn++)
            {
                var sw=Stopwatch.StartNew();var mode=(worker+turn)%4;
                if(mode==0)_=await workerDb.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.BaseNumber).Skip((worker*37)%1000).Take(100).Select(x=>new{x.Id,x.BaseNumber,x.Level}).ToListAsync();
                else if(mode==1)_=await workerDb.RequirementRevisions.AsNoTracking().Where(x=>workerDb.Requirements.Any(a=>a.Id==x.ArtifactId&&a.ProjectId==projectId)&&x.VerificationMethod=="Test").CountAsync();
                else if(mode==2)_=await workerDb.RequirementSpecifications.AsNoTracking().Where(x=>x.ProjectId==projectId).Select(x=>new{x.Id,count=workerDb.SpecificationNodes.Count(n=>n.SpecificationId==x.Id)}).ToListAsync();
                else _=await workerDb.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber.Contains($"{(worker%100)+1:D4}")).Take(25).ToListAsync();
                sw.Stop();samples.Add(sw.ElapsedMilliseconds);
            }
        }
        catch(Exception ex){failures.Add(ex.GetType().Name+": "+ex.Message);}
    }));
    total.Stop();var ordered=samples.Order().ToArray();long Percentile(double p)=>ordered.Length==0?0:ordered[Math.Min(ordered.Length-1,(int)Math.Ceiling(ordered.Length*p)-1)];
    var result=new{users,iterations,operations=ordered.Length,failures=failures.Count,totalSeconds=Math.Round(total.Elapsed.TotalSeconds,2),throughputPerSecond=total.Elapsed.TotalSeconds==0?0:Math.Round(ordered.Length/total.Elapsed.TotalSeconds,1),p50Ms=Percentile(.50),p95Ms=Percentile(.95),p99Ms=Percentile(.99),targetP95Ms=2000,passed=failures.IsEmpty&&Percentile(.95)<=2000};
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));if(!result.passed)Environment.ExitCode=1;
}
