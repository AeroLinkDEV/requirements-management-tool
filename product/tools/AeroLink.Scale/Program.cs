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
if (command is "generate" or "benchmark" or "load" or "session-load")
    throw new InvalidOperationException($"The {command} command is deferred in CQ-14 A0; use workspace preparation followed by preflight.");
var connection = command is "workspace" or "preflight"
    ? ScaleQualificationSafety.RequireSafeConnection(Environment.GetEnvironmentVariable("AEROLINK_SCALE_CONNECTION"))
    : null;
AeroLinkDbContext? db = null;
AeroLinkDbContext Db() => db ??= new AeroLinkDbContext(
    new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection!).Options);

try
{
    if (command == "workspace") await GenerateWorkspace();
    else if (command == "preflight") await PreflightOnly();
    else Console.WriteLine("Usage: dotnet run -- workspace|preflight [--profile smoke|small|medium] [--qualification-enabled --allow-dataset-write] [--dataset-seed 4754] [--prepared-manifest path] [--evidence-root path] [--manifest path]");
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
    => ScaleQualificationSafety.RequireSourceCommit(Option("--commit"));

bool SourceDirty() => ScaleQualificationSafety.IsSourceDirty();

string EvidenceRoot() => ScaleQualificationSafety.RequireEvidenceRoot(RequiredOption("--evidence-root"));

string ManifestPath(string root) => ScaleQualificationSafety.RequireManifestPath(Option("--manifest"), root);

ScaleQualificationManifest PreparedManifest()
{
    ScaleQualificationSafety.RejectApiOption(Flag("--api"), Option("--api"));
    foreach (var option in new[] { "--program-id", "--project-id", "--release-id", "--baseline-id", "--dataset-seed", "--dataset-hash" })
        if (Flag(option))
            throw new InvalidOperationException("preflight reads its exact scope from --prepared-manifest; caller scope options are not accepted.");
    var prepared = ScaleQualificationSafety.ReadPreparedManifest(RequiredOption("--prepared-manifest"));
    if (!prepared.Command.Equals("workspace", StringComparison.OrdinalIgnoreCase)
        || !prepared.QualificationOptIn || !prepared.WriteOptIn)
        throw new InvalidOperationException("The prepared qualification manifest is not an authorized workspace dataset.");
    if (!prepared.Commit.Equals(Commit(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The prepared qualification manifest was created by a different scale tool build.");
    var actualTarget = ScaleQualificationSafety.SafeConnectionIdentity(connection!);
    if (!prepared.DatabaseHost.Equals(actualTarget.Host, StringComparison.OrdinalIgnoreCase)
        || prepared.DatabasePort != actualTarget.Port
        || !prepared.DatabaseName.Equals(actualTarget.Database, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The prepared qualification manifest does not identify the connected database target.");
    return prepared;
}

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
                      select new { artifact.Id, artifact.BaseNumber, artifact.Level, RevisionId = revision.Id, revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, revision.State, revision.ParentKind, revision.DerivedRationale, revision.ParentRevisionIdsJson })
        .ToListAsync();
    if (rows.Count != expectedCount)
        throw new InvalidOperationException("The qualification baseline contains selections outside the requested Project or exact requirement revisions.");
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var row in rows)
    {
        var canonical = System.Text.Json.JsonSerializer.Serialize(new
        {
            artifactId = row.Id,
            baseNumber = row.BaseNumber,
            level = row.Level,
            revisionId = row.RevisionId,
            revision = row.Revision,
            statement = row.Statement,
            rationale = row.Rationale,
            verificationMethod = row.VerificationMethod,
            state = row.State,
            parentKind = row.ParentKind,
            derivedRationale = row.DerivedRationale,
            parentRevisionIds = row.ParentRevisionIdsJson,
        });
        hash.AppendData(Encoding.UTF8.GetBytes(canonical));
        hash.AppendData("\n"u8.ToArray());
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

async Task GenerateWorkspace()
{
    ScaleQualificationSafety.RejectApiOption(Flag("--api"), Option("--api"));
    if (Flag("--reset"))
        throw new InvalidOperationException("--reset is deferred in CQ-14 A0 until disposable database ownership can be proven.");
    ScaleQualificationSafety.RequireDatasetWriteOptIn(Flag("--qualification-enabled"), Flag("--allow-dataset-write"));
    var datasetSeed = RequiredOption("--dataset-seed");
    if (!datasetSeed.Equals("4754", StringComparison.Ordinal))
        throw new InvalidOperationException("The current workspace generator supports only the declared dataset seed 4754.");
    var evidenceRoot = EvidenceRoot();
    var manifestPath = ManifestPath(evidenceRoot);
    var sourceCommit = Commit();
    var sourceDirty = SourceDirty();
    var requirementCount=profile switch{"smoke"=>1_000,"medium"=>50_000,_=>10_000};
    await Db().Database.MigrateAsync();if(await Db().Programs.AnyAsync())throw new InvalidOperationException("Scale database already contains data. Create a new owned disposable database for qualification; reset is deferred.");
    var now=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);var program=new ProgramRecord("AeroLink Enterprise Qualification Program","QUAL");var project=new ProjectRecord(program.Id,"Enterprise FMS Qualification","Qualification Flight Management System");var release=new SoftwareRelease(project.Id,"10.0",false);Db().AddRange(program,project,release);await Db().SaveChangesAsync();
    var scr=new SystemChangeRequest("SRCR-00001",0,project.Id,release.Id,"Establish enterprise-scale qualification baseline","A repeatable large repository is required.","Generate mixed-level immutable requirements and exact baseline membership.","Establish the controlled qualification dataset.","scale.author",now);scr.AddRequirementChange("scale.author","SYSR-000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,"The qualification FMS shall support enterprise-scale repository validation.","Scale authority.","Test",now);var approvers=new[]{new ApproverSelection("scale.reviewer","Scale Reviewer")};scr.SubmitForReview("scale.author",approvers,now.AddMinutes(1));scr.ApproveActiveStage("scale.reviewer",now.AddMinutes(2));var baseline=new CandidateBaseline("SYSBL-00000001",0,project.Id,release.Id,null,"10,000-requirement qualification baseline","scale.cm",now);baseline.Select(scr,"scale.cm",now.AddMinutes(3));baseline.Freeze("scale.cm",now.AddMinutes(4));var baselineManifest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"enterprise-workspace:{requirementCount}:{datasetSeed}"))).ToLowerInvariant();baseline.MarkRequirementsMaterialized("scale.cm",baselineManifest,requirementCount,now.AddMinutes(5));Db().AddRange(scr,baseline);await Db().SaveChangesAsync();
    var sw=Stopwatch.StartNew();var counters=new Dictionary<RequirementLevel,int>{{RequirementLevel.System,0},{RequirementLevel.HighLevel,0},{RequirementLevel.LowLevel,0}};for(var i=1;i<=requirementCount;i++){var level=i<=requirementCount*15/100?RequirementLevel.System:i<=requirementCount*50/100?RequirementLevel.HighLevel:RequirementLevel.LowLevel;var number=++counters[level];var prefix=level switch{RequirementLevel.System=>"SYSR",RequirementLevel.HighLevel=>"HLR",_=>"LLR"};var artifact=new RequirementArtifact(project.Id,$"{prefix}-{number:D8}",level,now.AddSeconds(i));var parentKind=level==RequirementLevel.System?RequirementParentKind.Unspecified:RequirementParentKind.Derived;var derivedRationale=level==RequirementLevel.System?null:"Synthetic scale content is explicitly derived at this configured requirement level.";var revision=new RequirementRevision(artifact.Id,0,$"The qualification FMS shall provide deterministic {level} capability {number:D8} at enterprise scale.",$"Generated using deterministic qualification seed {datasetSeed}.",(i%4) switch{0=>"Analysis",1=>"Test",2=>"Inspection",_=>"Demonstration"},RequirementRevisionState.Active,scr.Id,baseline.Id,now.AddSeconds(i),parentKind,derivedRationale);Db().AddRange(artifact,revision,new BaselineRequirementSelection(baseline.Id,artifact.Id,revision.Id));if(i%500==0){await Db().SaveChangesAsync();Db().ChangeTracker.Clear();Console.WriteLine($"Materialized {i:N0}/{requirementCount:N0} requirements...");}}
    await Db().SaveChangesAsync();await new EnterpriseRequirementsService(Db()).SynchronizeProjectAsync(project.Id,"scale.workspace");sw.Stop();
    var contentHash = await RequirementContentHashAsync(baseline.Id, project.Id, requirementCount);
    var identity = new ScaleDatasetIdentity(program.Id, program.Name, program.Code, project.Id, project.Name, project.SoftwareProduct, release.Id, release.Version, baseline.Id, baseline.DisplayNumber, requirementCount, datasetSeed, contentHash, baseline.RequirementsHash!);
    var manifest = ScaleQualificationSafety.CreateManifest("dataset-prepared", "workspace", sourceCommit, identity, "disposable-postgresql", connection!, evidenceRoot, true, true, sourceDirty);
    ScaleQualificationSafety.WriteImmutableManifest(manifestPath, manifest);
    Console.WriteLine($"Generated a mixed-level {requirementCount:N0}-requirement Enterprise Requirements Workspace in {sw.Elapsed.TotalSeconds:N1}s; dataset manifest written to {manifestPath}.");
}

async Task PreflightOnly()
{
    ScaleQualificationSafety.RequireQualificationOptIn(Flag("--qualification-enabled"), "preflight");
    var evidenceRoot = EvidenceRoot();
    var manifestPath = ManifestPath(evidenceRoot);
    var prepared = PreparedManifest();
    var sourceCommit = Commit();
    var sourceDirty = SourceDirty();
    var scope = new ScaleQualificationScope(prepared.ProgramId, prepared.ProjectId, prepared.ReleaseId,
        prepared.BaselineId, prepared.DatasetSeed, prepared.DatasetHash);
    var identity = await ReadDatasetIdentityAsync(scope);
    var manifest = ScaleQualificationSafety.CreateManifest("preflight-only", "preflight", sourceCommit, identity,
        "direct database preflight; HTTP API binding deferred", connection!, evidenceRoot, true, false, sourceDirty);
    ScaleQualificationSafety.WriteImmutableManifest(manifestPath, manifest);
    Console.WriteLine($"Qualification preflight passed; manifest status is preflight-only at {manifestPath}. No benchmark, HTTP, or target write workload was executed.");
}
