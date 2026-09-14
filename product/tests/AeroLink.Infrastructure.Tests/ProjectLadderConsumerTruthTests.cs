using System.IO.Compression;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectLadderConsumerTruthTests
{
    [Theory]
    [InlineData(RequirementLevel.Customer, false, false)]
    [InlineData(RequirementLevel.Interface, false, false)]
    [InlineData(RequirementLevel.HighLevel, false, false)]
    [InlineData(RequirementLevel.System, true, false)]
    [InlineData(RequirementLevel.HighLevel, true, false)]
    [InlineData(RequirementLevel.HighLevel, true, true)]
    public async Task Thread_and_traceability_publication_use_the_effective_verification_profile(
        RequirementLevel level, bool verification, bool procedures)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Isolated profile fixture", "PROFILE");
        var project = new ProjectRecord(program.Id, "Profile truth", "Profile truth product");
        var release = new SoftwareRelease(project.Id, "1.3", false);
        var ladder = ProjectLadderConfiguration.CreateDraft(project.Id, now);
        VerificationArtifactKind[] kinds = !verification ? [] : level == RequirementLevel.System
            ? [VerificationArtifactKind.Procedure] : procedures
                ? [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure] : [VerificationArtifactKind.Case];
        ladder.Steps.Add(new ProjectLadderStep(ladder.Id, project.Id, level, 1,
            verification ? LevelCapabilities.HasVerification : LevelCapabilities.None, now, kinds));
        ladder.Activate("fixture.configuration", now, LadderConsumerManifestCatalog.VersionV2, new string('a', 64));
        db.AddRange(program, project, release, ladder);
        await db.SaveChangesAsync();

        var baseline = new CandidateBaseline("SW-01.30", 0, project.Id, release.Id, null,
            "Isolated source fixture", "fixture.source", now);
        baseline.FreezeForInception("fixture.source", now);
        baseline.MarkRequirementsMaterialized("fixture.source", new string('b', 64), 1, now);
        db.Add(baseline);
        var artifact = new RequirementArtifact(project.Id,
            LegacyLadderPolicy.Instance.RequirementPrefix(level) + "-103701", level, now);
        var revision = RequirementRevision.FromAeroLinkBaseline(artifact.Id, 0,
            "An isolated source requirement.", "Profile qualification", RequirementRevisionState.Active,
            baseline.Id, baseline.Id, now, "fixture:exact-revision-1");
        db.AddRange(artifact, revision, new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
        await db.SaveChangesAsync();

        var policies = new EffectiveProjectLadderPolicyResolver(db);
        var thread = await ArtifactThreadProjection.BuildAsync(db, project.Id, baseline.Id, null,
            ArtifactThreadFocalKind.Requirement, revision.Id, default, policies);
        Assert.NotNull(thread);
        Assert.Equal(verification, thread.Verification.IsApplicable);
        if (!verification) Assert.Contains("no verification discipline", thread.Verification.Reason);

        var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db,
            new EvidenceFileStore(Path.Combine(Path.GetTempPath(), $"aerolink-profile-{Guid.NewGuid():N}"))),
            policyResolver: policies);
        var output = await generator.GenerateTraceabilityAsync(baseline.Id, "docx", default);
        Assert.NotNull(output);
        using var archive = new ZipArchive(new MemoryStream(output.Content));
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        Assert.Contains("An isolated source requirement.", xml);
        if (verification)
        {
            Assert.Contains(level == RequirementLevel.System ? "Verification procedure revisions" : "Verification case revisions", xml);
            Assert.Contains("Coverage gap - none recorded", xml);
        }
        else
        {
            Assert.Contains("Not applicable - this level has no configured verification discipline.", xml);
            Assert.DoesNotContain("Coverage gap", xml);
        }

        if (verification)
        {
            // Requirement coverage enters at the first configured tier: System Procedure or software
            // Case, including when a software Procedure tier is also enabled. Keep exact revision membership.
            var firstTier = (await policies.ResolveAsync(project.Id)).Definition(level).VerificationProfile!.Definitions[0];
            var testArtifact = new TestProcedure(project.Id, firstTier.ArtifactPrefix + "-103701",
                "Exact configured coverage", "fixture.author", now, firstTier.ProcedureLevel,
                artifactKind: firstTier.Kind);
            var testRevision = new TestProcedureRevision(testArtifact.Id, 0, "Objective", "Preconditions",
                "Steps", "Expected", TestProcedureState.Draft, "fixture.author", now);
            db.AddRange(testArtifact, testRevision, new TestRequirementCoverage(testRevision.Id, revision.Id),
                new BaselineTestProcedureSelection(baseline.Id, testArtifact.Id, testRevision.Id));
            baseline.MarkTestProceduresMaterialized("fixture.source", new string('c', 64), 1, now);
            await db.SaveChangesAsync();
            var covered = await generator.GenerateTraceabilityAsync(baseline.Id, "docx", default);
            using var coveredArchive = new ZipArchive(new MemoryStream(covered!.Content));
            using var coveredReader = new StreamReader(coveredArchive.GetEntry("word/document.xml")!.Open());
            var coveredXml = await coveredReader.ReadToEndAsync();
            Assert.Contains(testArtifact.BaseNumber + ".00", coveredXml);
            Assert.DoesNotContain("Coverage gap", coveredXml);
        }
    }
}
