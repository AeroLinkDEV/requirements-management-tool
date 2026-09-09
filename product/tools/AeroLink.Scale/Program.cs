using System.Diagnostics;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Scale;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var profile = Option("--profile") ?? "small";
var connection = command == "help"
    ? null
    : ScaleQualificationSafety.RequireSafeConnection(Environment.GetEnvironmentVariable("AEROLINK_SCALE_CONNECTION"));
AeroLinkDbContext? db = null;
AeroLinkDbContext Db() => db ??= new AeroLinkDbContext(
    new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection!).Options);

try
{
    if (command == "generate") await Generate();
    else if (command == "workspace") await GenerateWorkspace();
    else if (command == "benchmark") await Benchmark();
    else if (command == "load") await ConcurrentLoad();
    else if (command == "session-load") await ConcurrentSessions();
    else if (command == "preflight") await PreflightOnly();
    else Console.WriteLine("Usage: dotnet run -- generate|workspace|benchmark|load|session-load|preflight [--profile smoke|small|medium] [--qualification-enabled --allow-dataset-write|--allow-write-load] [--reset] [--users 150] [--iterations 8] [--api http://127.0.0.1:5175]");
}
finally
{
    if (db is not null) await db.DisposeAsync();
}

string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

string RequiredOption(string name) => Option(name) is { Length: > 0 } value
    ? value.Trim()
    : throw new InvalidOperationException($"Scale qualification requires the explicit {name} option.");

string Commit()
{
    var expected = Option("--commit");
    using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to determine the scale tool source commit.");
    var actual = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    if (process.ExitCode != 0 || actual.Length != 40)
        throw new InvalidOperationException("Unable to determine the scale tool source commit.");
    if (!string.IsNullOrWhiteSpace(expected) && !actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The requested commit does not match the scale tool source commit.");
    return actual;
}

bool SourceDirty()
{
    using var process = Process.Start(new ProcessStartInfo("git", "status --porcelain")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to determine the scale tool source state.");
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException("Unable to determine the scale tool source state.");
    return !string.IsNullOrWhiteSpace(output);
}

string EvidenceRoot() => ScaleQualificationSafety.RequireEvidenceRoot(RequiredOption("--evidence-root"));

string ManifestPath(string root) => ScaleQualificationSafety.RequireManifestPath(Option("--manifest"), root);

ScaleQualificationScope Scope() => ScaleQualificationSafety.RequireScope(
    Option("--program-id"), Option("--project-id"), Option("--release-id"), Option("--baseline-id"),
    Option("--dataset-seed"), Option("--dataset-hash"));

async Task<ScaleDatasetIdentity> ReadDatasetIdentityAsync(ScaleQualificationScope scope)
{
    var context = Db();
    var program = await context.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.ProgramId)
        ?? throw new InvalidOperationException("The requested qualification Program does not exist.");
    var project = await context.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.ProjectId)
        ?? throw new InvalidOperationException("The requested qualification Project does not exist.");
    if (project.ProgramId != program.Id)
        throw new InvalidOperationException("The requested Project is outside the requested qualification Program.");
    var release = await context.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.ReleaseId)
        ?? throw new InvalidOperationException("The requested qualification release does not exist.");
    if (release.ProjectId != project.Id)
        throw new InvalidOperationException("The requested release is outside the requested qualification Project.");
    var baseline = await context.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.BaselineId)
        ?? throw new InvalidOperationException("The requested qualification baseline does not exist.");
    if (baseline.ProjectId != project.Id || baseline.ReleaseId != release.Id)
        throw new InvalidOperationException("The requested baseline is outside the requested qualification release.");
    if (baseline.State is not CandidateBaselineState.Frozen and not CandidateBaselineState.Released
        || baseline.RequirementsMaterializedAt is null
        || string.IsNullOrWhiteSpace(baseline.RequirementsHash))
        throw new InvalidOperationException("The requested qualification baseline is not a materialized frozen dataset.");
    var requirementCount = await context.BaselineRequirements.AsNoTracking().CountAsync(x => x.BaselineId == baseline.Id);
    var contentHash = await RequirementContentHashAsync(baseline.Id, project.Id, requirementCount);
    var identity = new ScaleDatasetIdentity(program.Id, program.Name, program.Code, project.Id, project.Name,
        project.SoftwareProduct, release.Id, release.Version, baseline.Id, baseline.DisplayNumber, requirementCount,
        scope.DatasetSeed, contentHash, baseline.RequirementsHash);
    ScaleQualificationSafety.ValidateExactDataset(scope, identity);
    return identity;
}

async Task<string> RequirementContentHashAsync(Guid baselineId, Guid projectId, int expectedCount)
{
    var context = Db();
    var rows = await (from selection in context.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                      join artifact in context.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId) on selection.ArtifactId equals artifact.Id
                      join revision in context.RequirementRevisions.AsNoTracking() on selection.RevisionId equals revision.Id
                      where revision.ArtifactId == artifact.Id
                      orderby artifact.BaseNumber, revision.Revision, revision.Id
                      select new { artifact.Id, artifact.BaseNumber, artifact.Level, RevisionId = revision.Id, revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, revision.State })
        .ToListAsync();
    if (rows.Count != expectedCount)
        throw new InvalidOperationException("The qualification baseline contains selections outside the requested Project or exact requirement revisions.");
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var row in rows)
    {
        var canonical = string.Join('|', row.Id.ToString("D"), row.BaseNumber, row.Level, row.RevisionId.ToString("D"),
            row.Revision, row.Statement, row.Rationale, row.VerificationMethod, row.State);
        hash.AppendData(Encoding.UTF8.GetBytes(canonical));
        hash.AppendData("\n"u8.ToArray());
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

async Task Generate()
{
    ScaleQualificationSafety.RequireDatasetWriteOptIn(Flag("--qualification-enabled"), Flag("--allow-dataset-write"));
    var datasetSeed = RequiredOption("--dataset-seed");
    if (!datasetSeed.Equals("4754", StringComparison.Ordinal))
        throw new InvalidOperationException("The current scale generator supports only the declared dataset seed 4754.");
    (int Scrs, int Requirements) counts = profile switch { "smoke" => (200, 1_000), "medium" => (10_000, 50_000), _ => (1_000, 5_000) };
    if (args.Contains("--reset"))
    {
        if (!ScaleQualificationSafety.SafeConnectionIdentity(connection!).Database.Equals("aerolink_scale", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("--reset is allowed only for the dedicated aerolink_scale database.");
        await Db().Database.EnsureDeletedAsync();
    }
    await Db().Database.MigrateAsync();
    if (await Db().Programs.AnyAsync()) throw new InvalidOperationException("Scale database already contains data. Use --reset for the dedicated qualification database.");
    var program = new ProgramRecord("Synthetic Flight Controls Program", "SYNFC");
    var project = new ProjectRecord(program.Id, "Flight Controls Software", "Synthetic Flight Controls Software");
    var release = new SoftwareRelease(project.Id, "6.4", false);
    Db().AddRange(program, project, release); await Db().SaveChangesAsync();
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
        Db().Add(scr);
        if (i % 250 == 0) { await Db().SaveChangesAsync(); Db().ChangeTracker.Clear(); Console.WriteLine($"Generated {i:N0}/{counts.Scrs:N0} SCRs..."); }
    }
    await Db().SaveChangesAsync();
    for (var i = 1; i <= Math.Max(1, counts.Scrs / 50); i++) Db().Add(new CandidateBaseline($"SW-{i % 100:D2}.00", 0, project.Id, release.Id, null, $"Synthetic software build {i}", "cm.synthetic", start.AddDays(i)));
    await Db().SaveChangesAsync();
    stopwatch.Stop();
    Console.WriteLine($"Generated {counts.Scrs:N0} SCRs and {counts.Requirements:N0} requirement changes in {stopwatch.Elapsed.TotalSeconds:N1}s using deterministic seed {datasetSeed}.");
}

async Task Benchmark()
{
    var scope = Scope();
    _ = await ReadDatasetIdentityAsync(scope);
    var projectId = scope.ProjectId;
    var context = Db();
    var results = new List<object>();
    await Measure("dashboard_aggregates", 2_000, async () => { var q=context.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId); _=await q.GroupBy(_=>1).Select(g=>new { Total=g.Count(), Draft=g.Count(x=>x.State==ChangeRequestState.Draft), Review=g.Count(x=>x.State==ChangeRequestState.InReview)}).SingleAsync(); });
    await Measure("scr_page_50", 500, async () => { _=await context.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderByDescending(x=>x.UpdatedAt).Take(50).Select(x=>new{x.Id,x.Title,x.State}).ToListAsync(); });
    await Measure("exact_requirement", 300, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber=="HLR-002375") join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId orderby revision.Revision descending select new{artifact.Id,artifact.BaseNumber,revision.Statement}).FirstOrDefaultAsync(); });
    await Measure("enterprise_page_100", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==context.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision) orderby artifact.BaseNumber select new{artifact.Id,artifact.BaseNumber,revision.Statement}).Take(100).ToListAsync(); });
    await Measure("structured_system_test_filter", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Level==RequirementLevel.System) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.VerificationMethod=="Test" select artifact.Id).CountAsync(); });
    // The exact tag and owner filters, at the scale that decides whether they are usable. These replaced
    // substring scans over serialized JSON, which no index could serve — so what is being measured is not a
    // micro-optimisation but whether the query shape is indexable at all.
    var sampleTag = await context.RequirementRevisionTags.AsNoTracking().Select(x => x.Tag).FirstOrDefaultAsync() ?? "safe";
    var sampleOwner = await context.RequirementRevisionProfiles.AsNoTracking().Where(x => x.Owner != "").Select(x => x.Owner).FirstOrDefaultAsync() ?? "owner";
    await Measure("exact_tag_filter_page", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where context.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("exact_owner_filter_page", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where context.RequirementRevisionProfiles.Any(p=>p.RevisionId==revision.Id&&p.Owner==sampleOwner) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("combined_owner_tag_level_filter", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Level==RequirementLevel.System) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where context.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag)&&context.RequirementRevisionProfiles.Any(p=>p.RevisionId==revision.Id&&p.Owner==sampleOwner) orderby artifact.BaseNumber select artifact.Id).Take(100).ToListAsync(); });
    await Measure("filtered_total_count", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where context.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag==sampleTag) select artifact.Id).CountAsync(); });
    // The worst case is a filter that matches nothing: the database cannot stop early, so it is the honest
    // upper bound on what a mistyped tag costs.
    await Measure("worst_case_no_match", 500, async () => { _=await (from artifact in context.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in context.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where context.RequirementRevisionTags.Any(t=>t.RevisionId==revision.Id&&t.Tag=="no-requirement-carries-this-tag") select artifact.Id).CountAsync(); });
    await Measure("specification_tree", 500, async () => { _=await context.SpecificationNodes.AsNoTracking().Where(x=>context.RequirementSpecifications.Any(s=>s.Id==x.SpecificationId&&s.ProjectId==projectId)).GroupBy(x=>x.SpecificationId).Select(x=>new{x.Key,Count=x.Count()}).ToListAsync(); });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    // The first run is reported separately: a warm p95 alone hides what the first reader of the day waits
    // for, and cold behaviour is one of the gaps SCALE_FOUNDATION already lists as unproven.
    async Task Measure(string name, int targetMs, Func<Task> operation) { var cold=Stopwatch.StartNew(); await operation(); cold.Stop(); var samples=new List<long>(); for(var i=0;i<5;i++){var sw=Stopwatch.StartNew();await operation();sw.Stop();samples.Add(sw.ElapsedMilliseconds);}var p95=samples.Order().ElementAt(4);results.Add(new{name,coldMs=cold.ElapsedMilliseconds,targetMs,p95Ms=p95,passed=p95<=targetMs,samples});if(p95>targetMs)Environment.ExitCode=1; }
}

async Task GenerateWorkspace()
{
    ScaleQualificationSafety.RequireDatasetWriteOptIn(Flag("--qualification-enabled"), Flag("--allow-dataset-write"));
    var datasetSeed = RequiredOption("--dataset-seed");
    if (!datasetSeed.Equals("4754", StringComparison.Ordinal))
        throw new InvalidOperationException("The current workspace generator supports only the declared dataset seed 4754.");
    var evidenceRoot = EvidenceRoot();
    var manifestPath = ManifestPath(evidenceRoot);
    var requirementCount=profile switch{"smoke"=>1_000,"medium"=>50_000,_=>10_000};
    if(args.Contains("--reset")){if(!ScaleQualificationSafety.SafeConnectionIdentity(connection!).Database.Equals("aerolink_scale",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("--reset is allowed only for the dedicated aerolink_scale database.");await Db().Database.EnsureDeletedAsync();}
    await Db().Database.MigrateAsync();if(await Db().Programs.AnyAsync())throw new InvalidOperationException("Scale database already contains data. Use --reset for the dedicated qualification database.");
    var now=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);var program=new ProgramRecord("AeroLink Enterprise Qualification Program","QUAL");var project=new ProjectRecord(program.Id,"Enterprise FMS Qualification","Qualification Flight Management System");var release=new SoftwareRelease(project.Id,"10.0",false);Db().AddRange(program,project,release);await Db().SaveChangesAsync();
    var scr=new SystemChangeRequest("SRCR-00001",0,project.Id,release.Id,"Establish enterprise-scale qualification baseline","A repeatable large repository is required.","Generate mixed-level immutable requirements and exact baseline membership.","Establish the controlled qualification dataset.","scale.author",now);scr.AddRequirementChange("scale.author","SYSR-000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,"The qualification FMS shall support enterprise-scale repository validation.","Scale authority.","Test",now);var approvers=new[]{new ApproverSelection("scale.reviewer","Scale Reviewer")};scr.SubmitForReview("scale.author",approvers,now.AddMinutes(1));scr.ApproveActiveStage("scale.reviewer",now.AddMinutes(2));var baseline=new CandidateBaseline("SYSBL-00000001",0,project.Id,release.Id,null,"10,000-requirement qualification baseline","scale.cm",now);baseline.Select(scr,"scale.cm",now.AddMinutes(3));baseline.Freeze("scale.cm",now.AddMinutes(4));var baselineManifest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"enterprise-workspace:{requirementCount}:{datasetSeed}"))).ToLowerInvariant();baseline.MarkRequirementsMaterialized("scale.cm",baselineManifest,requirementCount,now.AddMinutes(5));Db().AddRange(scr,baseline);await Db().SaveChangesAsync();
    var sw=Stopwatch.StartNew();var counters=new Dictionary<RequirementLevel,int>{{RequirementLevel.System,0},{RequirementLevel.HighLevel,0},{RequirementLevel.LowLevel,0}};for(var i=1;i<=requirementCount;i++){var level=i<=requirementCount*15/100?RequirementLevel.System:i<=requirementCount*50/100?RequirementLevel.HighLevel:RequirementLevel.LowLevel;var number=++counters[level];var prefix=level switch{RequirementLevel.System=>"SYSR",RequirementLevel.HighLevel=>"HLR",_=>"LLR"};var artifact=new RequirementArtifact(project.Id,$"{prefix}-{number:D8}",level,now.AddSeconds(i));var parentKind=level==RequirementLevel.System?RequirementParentKind.Unspecified:RequirementParentKind.Derived;var derivedRationale=level==RequirementLevel.System?null:"Synthetic scale content is explicitly derived at this configured requirement level.";var revision=new RequirementRevision(artifact.Id,0,$"The qualification FMS shall provide deterministic {level} capability {number:D8} at enterprise scale.",$"Generated using deterministic qualification seed {datasetSeed}.",(i%4) switch{0=>"Analysis",1=>"Test",2=>"Inspection",_=>"Demonstration"},RequirementRevisionState.Active,scr.Id,baseline.Id,now.AddSeconds(i),parentKind,derivedRationale);Db().AddRange(artifact,revision,new BaselineRequirementSelection(baseline.Id,artifact.Id,revision.Id));if(i%500==0){await Db().SaveChangesAsync();Db().ChangeTracker.Clear();Console.WriteLine($"Materialized {i:N0}/{requirementCount:N0} requirements...");}}
    await Db().SaveChangesAsync();await new EnterpriseRequirementsService(Db()).SynchronizeProjectAsync(project.Id,"scale.workspace");sw.Stop();
    var contentHash = await RequirementContentHashAsync(baseline.Id, project.Id, requirementCount);
    var identity = new ScaleDatasetIdentity(program.Id, program.Name, program.Code, project.Id, project.Name, project.SoftwareProduct, release.Id, release.Version, baseline.Id, baseline.DisplayNumber, requirementCount, datasetSeed, contentHash, baseline.RequirementsHash!);
    var manifest = ScaleQualificationSafety.CreateManifest("dataset-prepared", "workspace", Commit(), identity, "disposable-postgresql", connection!, evidenceRoot, true, true, SourceDirty());
    ScaleQualificationSafety.WriteImmutableManifest(manifestPath, manifest);
    Console.WriteLine($"Generated a mixed-level {requirementCount:N0}-requirement Enterprise Requirements Workspace in {sw.Elapsed.TotalSeconds:N1}s; dataset manifest written to {manifestPath}.");
}

async Task PreflightOnly()
{
    ScaleQualificationSafety.RequireOptIns(Flag("--qualification-enabled"), Flag("--allow-write-load"), "preflight");
    var evidenceRoot = EvidenceRoot();
    var manifestPath = ManifestPath(evidenceRoot);
    var scope = Scope();
    var identity = await ReadDatasetIdentityAsync(scope);
    _ = ScaleQualificationSafety.RequireExplicitSecret("AEROLINK_SCALE_PASSWORD");
    _ = ScaleQualificationSafety.RequireExplicitSetting("AEROLINK_SCALE_USER");
    var api = RequiredOption("--api").TrimEnd('/');
    if (!Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || apiUri.UserInfo.Length > 0
        || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException("preflight requires an explicit HTTP(S) API origin without embedded credentials.");
    var manifest = ScaleQualificationSafety.CreateManifest("preflight-only", "preflight", Commit(), identity,
        $"HTTP session; API={api}", connection!, evidenceRoot, true, true, SourceDirty());
    ScaleQualificationSafety.WriteImmutableManifest(manifestPath, manifest);
    Console.WriteLine($"Qualification preflight passed; manifest status is preflight-only at {manifestPath}. No benchmark or write workload was executed.");
}

async Task ConcurrentLoad()
{
    var scope = Scope();
    _ = await ReadDatasetIdentityAsync(scope);
    var projectId=scope.ProjectId;
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
        catch(Exception ex){failures.Add(ex.GetType().Name);}
    }));
    total.Stop();var ordered=samples.Order().ToArray();long Percentile(double p)=>ordered.Length==0?0:ordered[Math.Min(ordered.Length-1,(int)Math.Ceiling(ordered.Length*p)-1)];
    var result=new{users,iterations,operations=ordered.Length,failures=failures.Count,totalSeconds=Math.Round(total.Elapsed.TotalSeconds,2),throughputPerSecond=total.Elapsed.TotalSeconds==0?0:Math.Round(ordered.Length/total.Elapsed.TotalSeconds,1),p50Ms=Percentile(.50),p95Ms=Percentile(.95),p99Ms=Percentile(.99),targetP95Ms=2000,passed=failures.IsEmpty&&Percentile(.95)<=2000};
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));if(!result.passed)Environment.ExitCode=1;
}


async Task ConcurrentSessions() => throw new InvalidOperationException(
    "session-load is deferred: CQ-14 A0 has no supported proof that the HTTP API is attached to the exact qualified database. No HTTP workload or account write was executed.");
