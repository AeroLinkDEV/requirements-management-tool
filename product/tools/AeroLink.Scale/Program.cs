using System.Diagnostics;
using System.Net.Http.Json;
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
else if (command == "session-load") await ConcurrentSessions();
else Console.WriteLine("Usage: dotnet run -- generate|workspace|benchmark|load|session-load [--profile smoke|small|medium] [--reset] [--users 150] [--iterations 8] [--api http://127.0.0.1:5175]");

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
        var scr = new SystemChangeRequest($"SRCR-{i:D5}", 0, project.Id, release.Id, $"Synthetic controlled change {i}",
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
    for (var i = 1; i <= Math.Max(1, counts.Scrs / 50); i++) db.Add(new CandidateBaseline($"SW-{i % 100:D2}.00", 0, project.Id, release.Id, null, $"Synthetic software build {i}", "cm.synthetic", start.AddDays(i)));
    await db.SaveChangesAsync();
    stopwatch.Stop();
    Console.WriteLine($"Generated {counts.Scrs:N0} SCRs and {counts.Requirements:N0} requirement changes in {stopwatch.Elapsed.TotalSeconds:N1}s using deterministic seed 4754.");
}

async Task Benchmark()
{
    await db.Database.MigrateAsync();
    var projectId = await db.Projects.Select(x => x.Id).FirstAsync();
    var results = new List<object>();
    await Measure("dashboard_aggregates", 2_000, async () => { var q=db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId); _=await q.GroupBy(_=>1).Select(g=>new { Total=g.Count(), Draft=g.Count(x=>x.State==ChangeRequestState.Draft), Review=g.Count(x=>x.State==ChangeRequestState.InReview)}).SingleAsync(); });
    await Measure("scr_page_50", 500, async () => { _=await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderByDescending(x=>x.UpdatedAt).Take(50).Select(x=>new{x.Id,x.Title,x.State}).ToListAsync(); });
    await Measure("exact_requirement", 300, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber=="HLR-002375") join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId orderby revision.Revision descending select new{artifact.Id,artifact.BaseNumber,revision.Statement}).FirstOrDefaultAsync(); });
    await Measure("enterprise_page_100", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision) orderby artifact.BaseNumber select new{artifact.Id,artifact.BaseNumber,revision.Statement}).Take(100).ToListAsync(); });
    await Measure("structured_system_test_filter", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Level==RequirementLevel.System) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.VerificationMethod=="Test" select artifact.Id).CountAsync(); });
    // The exact tag and owner filters, at the scale that decides whether they are usable. These replaced
    // substring scans over serialized JSON, which no index could serve — so what is being measured is not a
    // micro-optimisation but whether the query shape is indexable at all.
    var sampleTag = await db.RequirementRevisionTags.AsNoTracking().Select(x => x.Tag).FirstOrDefaultAsync() ?? "safe";
    var sampleOwner = await db.RequirementRevisionProfiles.AsNoTracking().Where(x => x.Owner != "").Select(x => x.Owner).FirstOrDefaultAsync() ?? "owner";
    await Measure("exact_tag_filter_page", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where db.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("exact_owner_filter_page", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where db.RequirementRevisionProfiles.Any(p=>p.RevisionId==revision.Id&&p.Owner==sampleOwner) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("combined_owner_tag_level_filter", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Level==RequirementLevel.System) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where db.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag)&&db.RequirementRevisionProfiles.Any(p=>p.RevisionId==revision.Id&&p.Owner==sampleOwner) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("filtered_total_count", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where db.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag) select artifact.Id).CountAsync(); });
    // The worst case is a filter that matches nothing: the database cannot stop early, so it is the honest
    // upper bound on what a mistyped tag costs.
    await Measure("worst_case_no_match", 500, async () => { _=await (from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where db.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag=="no-requirement-carries-this-tag") select artifact.Id).CountAsync(); });
    await Measure("specification_tree", 500, async () => { _=await db.SpecificationNodes.AsNoTracking().Where(x=>db.RequirementSpecifications.Any(s=>s.Id==x.SpecificationId&&s.ProjectId==projectId)).GroupBy(x=>x.SpecificationId).Select(x=>new{x.Key,Count=x.Count()}).ToListAsync(); });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    // The first run is reported separately: a warm p95 alone hides what the first reader of the day waits
    // for, and cold behaviour is one of the gaps SCALE_FOUNDATION already lists as unproven.
    async Task Measure(string name, int targetMs, Func<Task> operation) { var cold=Stopwatch.StartNew(); await operation(); cold.Stop(); var samples=new List<long>(); for(var i=0;i<5;i++){var sw=Stopwatch.StartNew();await operation();sw.Stop();samples.Add(sw.ElapsedMilliseconds);}var p95=samples.Order().ElementAt(4);results.Add(new{name,coldMs=cold.ElapsedMilliseconds,targetMs,p95Ms=p95,passed=p95<=targetMs,samples});if(p95>targetMs)Environment.ExitCode=1; }
}

async Task GenerateWorkspace()
{
    var requirementCount=profile switch{"smoke"=>1_000,"medium"=>50_000,_=>10_000};
    if(args.Contains("--reset")){if(!connection.Contains("aerolink_scale",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("--reset is allowed only for the dedicated aerolink_scale database.");await db.Database.EnsureDeletedAsync();}
    await db.Database.MigrateAsync();if(await db.Programs.AnyAsync())throw new InvalidOperationException("Scale database already contains data. Use --reset for the dedicated aerolink_scale database.");
    var now=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);var program=new ProgramRecord("AeroLink Enterprise Qualification Program","QUAL");var project=new ProjectRecord(program.Id,"Enterprise FMS Qualification","Qualification Flight Management System");var release=new SoftwareRelease(project.Id,"10.0",false);db.AddRange(program,project,release);await db.SaveChangesAsync();
    var scr=new SystemChangeRequest("SRCR-00001",0,project.Id,release.Id,"Establish enterprise-scale qualification baseline","A repeatable large repository is required.","Generate mixed-level immutable requirements and exact baseline membership.","Establish the controlled qualification dataset.","scale.author",now);scr.AddRequirementChange("scale.author","SYSR-000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,"The qualification FMS shall support enterprise-scale repository validation.","Scale authority.","Test",now);var approvers=new[]{new ApproverSelection("scale.reviewer","Scale Reviewer")};scr.SubmitForReview("scale.author",approvers,now.AddMinutes(1));scr.ApproveActiveStage("scale.reviewer",now.AddMinutes(2));var baseline=new CandidateBaseline("SYSBL-00000001",0,project.Id,release.Id,null,"10,000-requirement qualification baseline","scale.cm",now);baseline.Select(scr,"scale.cm",now.AddMinutes(3));baseline.Freeze("scale.cm",now.AddMinutes(4));var manifest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"enterprise-workspace:{requirementCount}:4754"))).ToLowerInvariant();baseline.MarkRequirementsMaterialized("scale.cm",manifest,requirementCount,now.AddMinutes(5));db.AddRange(scr,baseline);await db.SaveChangesAsync();
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


/// <summary>
/// What 150 people using the product actually costs.
///
/// The `load` command above measures 150 concurrent database clients: EF queries issued straight at
/// PostgreSQL. That is a real measurement and it is why the product's scale claim has always been worded as
/// "database clients" — but it is not what a person does. A person signs in, holds a session, and makes HTTP
/// requests that carry authentication, authorization, project scoping, JSON serialization, and the whole
/// middleware pipeline before any query runs. Every one of those costs something, and none of them appear in
/// a direct-to-database measurement.
///
/// So this drives the product the way a browser does: each simulated user authenticates once, keeps its own
/// cookie container and connection pool, and then works — reading the dashboard, paging change requests,
/// searching requirements, opening one. If the HTTP path cannot carry 150 of those, the claim cannot be
/// stated in terms of users, however good the database numbers look.
/// </summary>
async Task ConcurrentSessions()
{
    var api = (Option("--api") ?? "http://127.0.0.1:5175").TrimEnd('/');
    var users = int.TryParse(Option("--users"), out var u) ? Math.Clamp(u, 1, 500) : 150;
    var iterations = int.TryParse(Option("--iterations"), out var i) ? Math.Clamp(i, 1, 200) : 8;
    var password = Environment.GetEnvironmentVariable("AEROLINK_SCALE_PASSWORD") ?? "AeroLink!2026";
    var userName = Environment.GetEnvironmentVariable("AEROLINK_SCALE_USER") ?? "admin";
    // One account per simulated person. Signing 150 sessions in as the same account measures a scenario
    // nobody has, and it collides with the sign-in rate limiter — which is keyed per account precisely
    // because that is what stops one account being guessed at, not what stops a team arriving at work.
    var accounts = await EnsureLoadAccountsAsync(users, password);
    var targetP95 = int.TryParse(Option("--target-p95"), out var t) ? t : 2000;

    using var discovery = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(60) };
    var workspace = await SignInAsync(discovery);

    if (workspace is null) { Console.Error.WriteLine("Could not sign in to the API. Is it running, and is AEROLINK_SCALE_USER correct?"); Environment.ExitCode = 1; return; }
    var (projectId, releaseId) = workspace.Value;

    var samples = new System.Collections.Concurrent.ConcurrentBag<(string Route, long Ms)>();
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var signInSamples = new System.Collections.Concurrent.ConcurrentBag<long>();

    // Every user is started before any of them works, so the measurement is of 150 sessions in flight
    // rather than of a queue that drains as it fills.
    var ready = new SemaphoreSlim(0, users);
    var go = new TaskCompletionSource();

    var total = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, users).Select(async worker =>
    {
        // A cookie container per user, because a shared one would make this one session used 150 times.
        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            var signIn = Stopwatch.StartNew();
            var session = await SignInAsync(client, accounts[worker]);
            signIn.Stop();
            signInSamples.Add(signIn.ElapsedMilliseconds);
            if (session is null) { failures.Add("sign-in refused"); ready.Release(); return; }
            ready.Release();
            await go.Task;

            for (var turn = 0; turn < iterations; turn++)
            {
                var route = ((worker + turn) % 5) switch
                {
                    0 => $"/api/dashboard?projectId={projectId}&releaseId={releaseId}",
                    1 => $"/api/scrs?projectId={projectId}&page=1&pageSize=25",
                    2 => $"/api/enterprise-requirements/workspace?projectId={projectId}&page={(worker % 8) + 1}&pageSize=50",
                    3 => $"/api/enterprise-requirements/workspace?projectId={projectId}&page=1&pageSize=25&search={(worker % 100) + 1:D4}",
                    _ => $"/api/my-work",
                };
                var sw = Stopwatch.StartNew();
                using var response = await client.GetAsync(route);
                await response.Content.ReadAsByteArrayAsync();
                sw.Stop();
                if (!response.IsSuccessStatusCode) failures.Add($"{(int)response.StatusCode} {route}");
                else samples.Add((route.Split('?')[0], sw.ElapsedMilliseconds));
            }
        }
        catch (Exception ex) { failures.Add(ex.GetType().Name + ": " + ex.Message); }
    }).Prepend(Task.Run(async () =>
    {
        for (var n = 0; n < users; n++) await ready.WaitAsync();
        go.SetResult();
    })));
    total.Stop();

    var ordered = samples.Select(x => x.Ms).Order().ToArray();
    long Percentile(double p) => ordered.Length == 0 ? 0 : ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * p) - 1)];
    var byRoute = samples.GroupBy(x => x.Route).Select(g =>
    {
        var values = g.Select(x => x.Ms).Order().ToArray();
        return new { route = g.Key, count = values.Length, p50Ms = values[values.Length / 2], p95Ms = values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * .95) - 1)] };
    }).OrderByDescending(x => x.p95Ms).ToList();

    var signIns = signInSamples.Order().ToArray();
    var result = new
    {
        surface = "HTTP session",
        users,
        iterations,
        requests = ordered.Length,
        failures = failures.Count,
        // Distinct failures only: 150 copies of one message is one fact, and printing it 150 times buries
        // the one that is different.
        failureKinds = failures.Distinct().Take(8).ToArray(),
        totalSeconds = Math.Round(total.Elapsed.TotalSeconds, 2),
        requestsPerSecond = total.Elapsed.TotalSeconds == 0 ? 0 : Math.Round(ordered.Length / total.Elapsed.TotalSeconds, 1),
        signInP95Ms = signIns.Length == 0 ? 0 : signIns[Math.Min(signIns.Length - 1, (int)Math.Ceiling(signIns.Length * .95) - 1)],
        p50Ms = Percentile(.50),
        p95Ms = Percentile(.95),
        p99Ms = Percentile(.99),
        maxMs = ordered.Length == 0 ? 0 : ordered[^1],
        targetP95Ms = targetP95,
        byRoute,
        passed = failures.IsEmpty && Percentile(.95) <= targetP95,
    };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    if (!result.passed) Environment.ExitCode = 1;

    async Task<(Guid ProjectId, Guid ReleaseId)?> SignInAsync(HttpClient client, string? asUser = null)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = asUser ?? userName, password });
        if (!login.IsSuccessStatusCode) return null;
        using var workspaces = await client.GetAsync("/api/workspaces");
        if (!workspaces.IsSuccessStatusCode) return null;
        var payload = await workspaces.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // The largest project wins, because measuring against a small one would flatter the result: what is
        // being asked is what 150 people cost on the repository they actually work in.
        foreach (var program in payload.EnumerateArray())
        foreach (var entry in program.GetProperty("projects").EnumerateArray())
        foreach (var release in entry.GetProperty("releases").EnumerateArray())
            if (entry.GetProperty("project").GetProperty("name").GetString()?.Contains("Qualification", StringComparison.OrdinalIgnoreCase) == true)
                return (entry.GetProperty("project").GetProperty("id").GetGuid(), release.GetProperty("id").GetGuid());
        foreach (var program in payload.EnumerateArray())
        foreach (var entry in program.GetProperty("projects").EnumerateArray())
        foreach (var release in entry.GetProperty("releases").EnumerateArray())
            return (entry.GetProperty("project").GetProperty("id").GetGuid(), release.GetProperty("id").GetGuid());
        return null;
    }
}


/// <summary>
/// Provisions the load accounts, and grants each of them access to every program.
///
/// Written straight to the database rather than through the administration API, because creating a hundred
/// and fifty accounts through the sign-in-protected surface is itself a load test of a different thing. The
/// accounts are idempotent, so a repeat run reuses them.
/// </summary>
async Task<string[]> EnsureLoadAccountsAsync(int count, string password)
{
    var names = Enumerable.Range(0, count).Select(i => $"load.user.{i:D3}").ToArray();
    var existing = (await db.UserAccounts.Where(x => names.Contains(x.UserName)).ToListAsync())
        .ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
    var programs = await db.Programs.Select(x => x.Id).ToListAsync();
    var now = DateTimeOffset.UtcNow;
    var hash = IdentityService.HashPassword(password);
    foreach (var name in names)
    {
        if (existing.ContainsKey(name)) continue;
        var account = new AeroLink.Domain.Identity.UserAccount(name, $"Load User {name[^3..]}", $"{name}@example.test", hash, now);
        db.UserAccounts.Add(account);
        foreach (var program in programs)
            db.ProgramMemberships.Add(new AeroLink.Domain.Identity.ProgramMembership(account.Id, program, AeroLink.Domain.Identity.ProgramRole.Engineer, "scale.harness", now));
    }
    await db.SaveChangesAsync();
    return names;
}
