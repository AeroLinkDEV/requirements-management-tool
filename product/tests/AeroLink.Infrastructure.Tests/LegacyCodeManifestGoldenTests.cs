using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class LegacyCodeManifestGoldenTests
{
    [Fact]
    public async Task Legacy_code_manifest_matches_pre_source_evidence_golden_hash()
    {
        // Literal captured by executing this fixture on protected main 6723839267cac5cc0a1714acabc1b44f6e93cb9f.
        // Fixed identities and times cover ordering, empty/null values and JSON escaping in both legacy dispositions.
        const string expected = "45958db6fcdeee36221463d27f7ce4f47519ba77b029adf9ee3504142440f25f";
        var now = DateTimeOffset.Parse("2026-01-02T03:04:05+00:00");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var program = Identify(new ProgramRecord("Golden fixture", "GOLD"), 1);
        var project = Identify(new ProjectRecord(program.Id, "Golden fixture", "Serializer compatibility"), 2);
        var release = Identify(new SoftwareRelease(project.Id, "1.0", false), 3);
        var baseline = Identify(new CandidateBaseline("BL-000001", 0, project.Id, release.Id, null, "Golden", "tester", now), 4);
        foreach (var item in baseline.Events) Set(item, nameof(item.BaselineId), baseline.Id);
        var prior = Identify(new CandidateBaseline("BL-000002", 0, project.Id, release.Id, null, "Source fixture", "tester", now), 5);
        foreach (var item in prior.Events) Set(item, nameof(item.BaselineId), prior.Id);
        var build = Identify(new SoftwareBuild(project.Id, release.Id, baseline.Id, "SW-01.00", "Build \"one\"\n& source", "tester", now), 6);
        var campaign = Identify(new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Golden", "tester", now), 7);
        foreach (var item in campaign.Events) Set(item, nameof(item.CampaignId), campaign.Id);
        campaign.SelectVerificationBuild(build.Id, "tester", now);
        db.AddRange(program, project, release, baseline, prior, build, campaign);
        // Reverse insertion order must not affect the legacy canonical order by exact revision identity.
        foreach (var n in new[] { 2, 1 })
        {
            var artifact = Identify(new RequirementArtifact(project.Id, $"LLR-{n:D6}", RequirementLevel.LowLevel, now), 10 + n);
            var revision = Identify(RequirementRevision.FromAeroLinkBaseline(artifact.Id, 0, $"Synthetic {n}", "Test",
                RequirementRevisionState.Active, prior.Id, baseline.Id, now, $"LLR-{n:D6}.00"), 20 + n);
            Set(revision, nameof(revision.ParentKind), RequirementParentKind.Derived);
            Set(revision, nameof(revision.DerivedRationale), "Isolated serializer fixture.");
            var code = Identify(new CodeTraceabilityRecord(project.Id, release.Id, artifact.Id, revision.Id,
                n == 1 ? CodeTraceDisposition.GitLabMerge : CodeTraceDisposition.NoCodeChangeRequired,
                "demo/source", "!3", "Retain \"valid\" <route> & state", "https://gitlab.example/demo/source/-/merge_requests/3",
                new string('a', 40), n == 1 ? now : null, n == 2 ? "No code\nchange: \"unchanged\" & retained." : "", true, "tester", now), 30 + n);
            db.AddRange(artifact, revision, code, new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
        }
        await db.SaveChangesAsync();
        var service = new ReleaseExecutionService(db, new EvidenceFileStore(Path.Combine(Path.GetTempPath(), "aerolink-golden-unused")));
        var actual = await service.ComputeReviewManifestHashAsync(campaign.Id, default);
        Assert.True(expected == actual, actual);
        var priorEvents = campaign.Events.ToHashSet();
        campaign.StartVerification("tester", now);
        campaign.BeginReleaseReview("tester", [("reviewer", "Reviewer")], expected, now);
        db.AddRange(campaign.Approvals);
        db.AddRange(campaign.Events.Where(item => !priorEvents.Contains(item)));
        await db.SaveChangesAsync();
        // Model a retained historical v1 cycle without a format row after new source records become available.
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/demo/source", "tester", now);
        var snapshot = new GitLabSourceSnapshot(project.Id, repository.Id, "https://gitlab.example", 17,
            "demo/source", new string('b', 40), "main", "tester", now, repository.Version);
        var selection = new GitLabSourceSelectionEvent(project.Id, release.Id, snapshot.Id, 0, "tester", now);
        db.AddRange(repository, snapshot, selection,
            new GitLabCurrentSourceSelection(project.Id, release.Id, snapshot.Id, selection.Id, "tester", now));
        await db.SaveChangesAsync();
        Assert.Equal(expected, await service.ComputeRecordedReviewManifestHashAsync(campaign.Id, default));
        Assert.Equal(expected, campaign.ReleaseHash);
        Assert.Empty(await db.CodeReviewCycleManifestIdentities.ToListAsync());
    }

    private static T Identify<T>(T item, int number) where T : class
    { Set(item, "Id", Guid.Parse($"00000000-0000-0000-0000-{number:D12}")); return item; }
    private static void Set(object item, string property, object value) => item.GetType().GetProperty(property)!.SetValue(item, value);
}
