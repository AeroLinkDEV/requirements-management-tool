using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class SecondShowcaseSeederTests
{
    [Fact]
    public async Task Seeds_an_idempotent_active_system_low_level_workspace_with_policy_aware_outputs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-second-showcase-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
                .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
            var resolver = new EffectiveProjectLadderPolicyResolver(db);
            var authoring = new ProjectLadderAuthoringService(db, LegacyLadderPolicy.Instance, consumers);
            var seeder = new SecondShowcaseSeeder(db, authoring, resolver);

            var first = await seeder.EnsureSeededAsync();
            var second = await seeder.EnsureSeededAsync();

            Assert.Equal(first, second);
            var ladder = await db.ProjectLadderConfigurations.Include(x => x.Steps)
                .Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == first.ProjectId);
            Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Active, ladder.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.LowLevel],
                ladder.Steps.OrderBy(x => x.Position).Select(x => Enum.Parse<RequirementLevel>(x.CatalogueEntry)));
            Assert.Single(ladder.AllowedUpstream);
            Assert.Equal(first.ProjectId, ladder.ProjectId);
            Assert.NotNull(ladder.ActivatedAt);
            Assert.Equal("showcase.second", ladder.ActivatedBy);
            Assert.NotNull(ladder.ActivationManifestVersion);
            Assert.NotNull(ladder.ActivationManifestHash);
            var activationHistory = (await db.ProjectLadderConfigurationHistories.AsNoTracking()
                .Where(x => x.ProjectId == first.ProjectId).ToListAsync())
                .Single(x => x.Reason.StartsWith("Activated ladder:", StringComparison.Ordinal));
            Assert.Equal("showcase.second", activationHistory.Actor);
            var systemRequest = await db.SystemChangeRequests
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.ProjectId == first.ProjectId && x.BaseNumber == "SRCR-71201");
            var lowLevelRequest = await db.SystemChangeRequests
                .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleAsync(x => x.ProjectId == first.ProjectId && x.BaseNumber == "LLRCR-71202");
            Assert.Single(systemRequest.ReviewCycles.SelectMany(x => x.Steps),
                x => x.ApproverId == "systems.reviewer" && x.State == ApprovalStepState.Approved);
            Assert.Single(lowLevelRequest.ReviewCycles.SelectMany(x => x.Steps),
                x => x.ApproverId == "software.lead" && x.State == ApprovalStepState.Approved);
            Assert.Equal(1, await db.Requirements.CountAsync(x => x.ProjectId == first.ProjectId && x.Level == RequirementLevel.System));
            Assert.Equal(1, await db.Requirements.CountAsync(x => x.ProjectId == first.ProjectId && x.Level == RequirementLevel.LowLevel));
            Assert.Equal(0, await db.Requirements.CountAsync(x => x.ProjectId == first.ProjectId && x.Level == RequirementLevel.HighLevel));
            Assert.Equal(1, await db.DownstreamChangeAssessments.CountAsync(x => x.ProjectId == first.ProjectId && x.TargetLevel == RequirementLevel.LowLevel));
            Assert.Equal(0, await db.DownstreamChangeAssessments.CountAsync(x => x.ProjectId == first.ProjectId && x.TargetLevel == RequirementLevel.HighLevel));

            var policy = await resolver.ResolveAsync(first.ProjectId);
            Assert.Equal([RequirementLevel.System, RequirementLevel.LowLevel], policy.OrderedLevels);
            Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.System));
            Assert.Equal([RequirementLevel.System], policy.ParentLevels(RequirementLevel.LowLevel));
            Assert.DoesNotContain(policy.Definitions, x => x.Level == RequirementLevel.HighLevel);
            Assert.DoesNotContain(await db.RequirementSpecifications.Where(x => x.ProjectId == first.ProjectId).Select(x => x.Level).ToListAsync(), x => x == nameof(RequirementLevel.HighLevel));
            Assert.DoesNotContain(await db.ArtifactSchemas.Where(x => x.ProjectId == first.ProjectId).Select(x => x.AppliesTo).ToListAsync(), x => x == nameof(RequirementLevel.HighLevel));
            Assert.DoesNotContain(await db.TestProcedures.Where(x => x.ProjectId == first.ProjectId).Select(x => x.Level).ToListAsync(), x => x == TestProcedureLevel.HighLevel);
            Assert.DoesNotContain(await db.SystemChangeRequests.Where(x => x.ProjectId == first.ProjectId).Select(x => x.BaseNumber).ToListAsync(), x => x.StartsWith("HLR", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(await db.DownstreamChangeAssessments.Where(x => x.ProjectId == first.ProjectId).Select(x => x.SourceChangeRequestNumber).ToListAsync(), x => x.StartsWith("HLR", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(await db.ControlledDocuments.Where(x => x.ProjectId == first.ProjectId).Select(x => x.DocumentNumber).ToListAsync(), x => x.StartsWith("HLR", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(await db.TestProcedureDocuments.Where(x => x.ProjectId == first.ProjectId).Select(x => x.DocumentNumber).ToListAsync(), x => x.StartsWith("HLR", StringComparison.OrdinalIgnoreCase));

            var trace = await (from link in db.RequirementTraces
                               join sourceRevision in db.RequirementRevisions on link.SourceRevisionId equals sourceRevision.Id
                               join source in db.Requirements on sourceRevision.ArtifactId equals source.Id
                               join targetRevision in db.RequirementRevisions on link.TargetRevisionId equals targetRevision.Id
                               join target in db.Requirements on targetRevision.ArtifactId equals target.Id
                               where link.ProjectId == first.ProjectId
                               select new { link, source.Level, TargetLevel = target.Level }).SingleAsync();
            Assert.Equal(RequirementLevel.LowLevel, trace.Level);
            Assert.Equal(RequirementLevel.System, trace.TargetLevel);
            Assert.Equal(RequirementTraceType.DerivedFrom, trace.link.Type);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Resumes_after_the_initial_workspace_checkpoint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-second-showcase-recovery-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord(SecondShowcaseSeeder.ProjectName, SecondShowcaseSeeder.ProgramCode);
            var project = new ProjectRecord(program.Id, SecondShowcaseSeeder.ProjectName, "Configured Ladder Software");
            var release = new SoftwareRelease(project.Id, "2.0", false);
            db.AddRange(program, project, release,
                LegacyDefaultProjectLadderFactory.Create(project.Id, new DateTimeOffset(2026, 1, 12, 13, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();

            var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
                .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
            var resolver = new EffectiveProjectLadderPolicyResolver(db);
            var seeder = new SecondShowcaseSeeder(db,
                new ProjectLadderAuthoringService(db, LegacyLadderPolicy.Instance, consumers), resolver);

            var first = await seeder.EnsureSeededAsync();
            var second = await seeder.EnsureSeededAsync();

            Assert.Equal(first, second);
            Assert.Equal(SecondShowcaseSeeder.ProgramCode,
                await db.Programs.Where(x => x.Id == first.ProgramId).Select(x => x.Code).SingleAsync());
            Assert.Equal(1, await db.Projects.CountAsync(x => x.ProgramId == first.ProgramId));
            Assert.Equal(1, await db.Releases.CountAsync(x => x.ProjectId == first.ProjectId));
            Assert.Equal(1, await db.ProjectLadderConfigurations.CountAsync(x => x.ProjectId == first.ProjectId));
            Assert.Equal(ProjectLadderConfigurationState.Active,
                await db.ProjectLadderConfigurations.Where(x => x.ProjectId == first.ProjectId)
                    .Select(x => x.State).SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rejects_a_mismatched_dedicated_workspace_without_resetting_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-second-showcase-mismatch-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Unexpected Showcase", SecondShowcaseSeeder.ProgramCode);
            db.Programs.Add(program);
            await db.SaveChangesAsync();
            var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
                .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
            var seeder = new SecondShowcaseSeeder(db,
                new ProjectLadderAuthoringService(db, LegacyLadderPolicy.Instance, consumers));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.EnsureSeededAsync());

            Assert.Contains("unexpected name", exception.Message, StringComparison.Ordinal);
            Assert.Equal("Unexpected Showcase",
                await db.Programs.Where(x => x.Code == SecondShowcaseSeeder.ProgramCode)
                    .Select(x => x.Name).SingleAsync());
            Assert.Empty(await db.Projects.Where(x => x.ProgramId == program.Id).ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Seeding_the_second_workspace_does_not_change_an_existing_fms_workspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-second-showcase-fms-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var fms = await new FmsShowcaseSeeder(db).EnsureSeededAsync();
            var before = await SnapshotFmsAsync(db, fms.ProjectId);
            var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
                .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
            var resolver = new EffectiveProjectLadderPolicyResolver(db);
            await new SecondShowcaseSeeder(db,
                new ProjectLadderAuthoringService(db, LegacyLadderPolicy.Instance, consumers), resolver)
                .EnsureSeededAsync();
            var after = await SnapshotFmsAsync(db, fms.ProjectId);
            Assert.Equal(before.Releases, after.Releases);
            Assert.Equal(before.Requirements, after.Requirements);
            Assert.Equal(before.RevisionContent, after.RevisionContent);
            Assert.Equal(before.Requests, after.Requests);
            Assert.Equal(before.RequestContent, after.RequestContent);
            Assert.Equal(before.Traces, after.Traces);
            Assert.Equal(before.Documents, after.Documents);
            Assert.Equal(before.Classification, after.Classification);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.Steps, after.Steps);
            Assert.Equal(before.Relationships, after.Relationships);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    private static async Task<FmsSnapshot> SnapshotFmsAsync(AeroLinkDbContext db, Guid projectId)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Version).Select(x => $"{x.Version}:{x.IsReleased}:{x.PredecessorReleaseId}").ToArrayAsync();
        var requirements = await db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.BaseNumber).Select(x => $"{x.BaseNumber}:{x.Level}").ToArrayAsync();
        var revisionContent = await (from revision in db.RequirementRevisions.AsNoTracking()
                                     join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                     where artifact.ProjectId == projectId
                                     orderby artifact.BaseNumber, revision.Revision
                                     select $"{artifact.BaseNumber}:{revision.Revision}:{revision.State}:{revision.Statement}:{revision.Rationale}:{revision.VerificationMethod}").ToArrayAsync();
        var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision)
            .Select(x => $"{x.DisplayNumber}:{x.State}:{x.Type}:{x.SoftwareLevel}").ToArrayAsync();
        var requestContent = await (from request in db.SystemChangeRequests.AsNoTracking()
                                    join change in db.RequirementChanges.AsNoTracking() on request.Id equals change.ChangeRequestId
                                    where request.ProjectId == projectId
                                    orderby request.BaseNumber, change.BaseNumber
                                    select $"{request.DisplayNumber}:{change.DisplayNumber}:{change.Level}:{change.Kind}:{change.Statement}:{change.Rationale}").ToArrayAsync();
        var traces = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.SourceRevisionId).ThenBy(x => x.TargetRevisionId)
            .Select(x => $"{x.SourceRevisionId}:{x.TargetRevisionId}:{x.Type}").ToArrayAsync();
        var documents = await db.ControlledDocuments.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.DocumentNumber)
            .Select(x => $"{x.DocumentNumber}:{x.Type}:{x.ContentHash}:{x.ArtifactCount}").ToArrayAsync();
        var ladder = await db.ProjectLadderConfigurations.AsNoTracking().Include(x => x.Steps)
            .Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == projectId);
        return new(releases, requirements, revisionContent, requests, requestContent, traces, documents,
            ladder.Classification, ladder.State,
            ladder.Steps.OrderBy(x => x.Position).Select(x => $"{x.CatalogueEntry}:{x.Position}:{x.Capabilities}").ToArray(),
            ladder.AllowedUpstream.OrderBy(x => x.ParentStepId).Select(x => $"{x.ParentStepId}:{x.ChildStepId}").ToArray());
    }

    private sealed record FmsSnapshot(
        string[] Releases,
        string[] Requirements,
        string[] RevisionContent,
        string[] Requests,
        string[] RequestContent,
        string[] Traces,
        string[] Documents,
        ProjectLadderConfigurationClassification Classification,
        ProjectLadderConfigurationState State,
        string[] Steps,
        string[] Relationships);
}
