using System.Net;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReleasedSyntheticSourceSupplementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private const long RemoteProjectId = 86663796;
    private const string Origin = "https://gitlab.example";
    private const string RepositoryPath = "synthetic/fms-demo";
    private const string CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Apply_persists_only_the_standalone_supplement_and_replay_is_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();
        var preflight = fixture.Preflight(manifest);
        var campaignId = manifest.ReleaseCampaignId;
        var v1 = new CodeReviewCycleManifestIdentity(fixture.ProjectId, fixture.ReleaseId, campaignId, 1,
            "CodeReview", 1, new string('e', 64), null, null, [], "tester", Now);
        var v2 = new CodeReviewCycleManifestIdentity(fixture.ProjectId, fixture.ReleaseId, campaignId, 2,
            "CodeReview", 2, new string('f', 64), null, null, [], "tester", Now);
        fixture.Db.AddRange(v1, v2);
        await fixture.Db.SaveChangesAsync();
        var manifestBefore = await fixture.Db.CodeReviewCycleManifestIdentities.AsNoTracking()
            .OrderBy(x => x.ApprovalCycle).Select(x => new { x.FormatVersion, x.ManifestHash, x.EvidenceReferenceIdsJson })
            .ToListAsync();

        var first = await fixture.ApplyAsync(manifest, preflight);

        Assert.False(first.IsReplay);
        Assert.Equal(manifest.ProjectId, first.ProjectId);
        Assert.Equal(manifest.ReleaseId, first.ReleaseId);
        Assert.Equal(1, await fixture.Db.ReleasedSyntheticSourceSupplements.CountAsync());
        Assert.Equal(1, await fixture.Db.GitLabSourceSnapshots.CountAsync());
        Assert.Empty(await fixture.Db.GitLabSourceSelectionEvents.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabCurrentSourceSelections.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabMergeRequestRelationships.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabFileRelationships.ToListAsync());

        Assert.Empty(await fixture.Db.CodeEvidenceDispositionSets.ToListAsync());
        var manifestAfter = await fixture.Db.CodeReviewCycleManifestIdentities.AsNoTracking()
            .OrderBy(x => x.ApprovalCycle).Select(x => new { x.FormatVersion, x.ManifestHash, x.EvidenceReferenceIdsJson })
            .ToListAsync();
        Assert.Equal(manifestBefore, manifestAfter);

        fixture.Settings.ReleasedSyntheticSourceSupplementScope.CampaignId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(manifest));
        fixture.Settings.ReleasedSyntheticSourceSupplementScope.CampaignId = campaignId.ToString("D");

        fixture.Db.ChangeTracker.Clear();
        var replay = await fixture.ApplyAsync(manifest, new(
            manifest.Digest(), true, await fixture.Db.ReleasedSyntheticSourceSupplements.AsNoTracking().SingleAsync(),
            null, fixture.ConfigurationId, fixture.ConfigurationVersion));

        Assert.True(replay.IsReplay);
        Assert.Equal(first.SupplementId, replay.SupplementId);
        Assert.Equal(first.SourceSnapshotId, replay.SourceSnapshotId);
        Assert.Equal(1, await fixture.Db.ReleasedSyntheticSourceSupplements.CountAsync());
        Assert.Equal(1, await fixture.Db.GitLabSourceSnapshots.CountAsync());
    }

    [Fact]
    public async Task Manifest_digest_is_canonical_and_wrong_project_or_reference_is_refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();

        Assert.Equal(manifest.Digest(), (manifest with { Reason = "  " + manifest.Reason + "  " }).Digest());
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(
            manifest with { ProjectId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(
            manifest with { CommitSha = new string('b', 40) }));
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(
            manifest with { ExpectedSourceSelectionVersion = 1 }));
    }

    [Fact]
    public async Task Supplement_requires_the_exact_operator_authority_tuple()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();
        fixture.Settings.ReleasedSyntheticSourceSupplementScope = new();
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(manifest));

        fixture.Settings.ReleasedSyntheticSourceSupplementScope = new()
        {
            ProgramId = Guid.NewGuid().ToString("D"),
            ProjectId = manifest.ProjectId.ToString("D"),
            ReleaseId = manifest.ReleaseId.ToString("D"),
            BaselineId = manifest.BaselineId.ToString("D"),
            CampaignId = manifest.ReleaseCampaignId.ToString("D"),
        };
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(manifest));
    }

    [Fact]
    public async Task Existing_ordinary_source_history_blocks_the_first_init_supplement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = new GitLabSourceSnapshot(fixture.ProjectId, fixture.ConfigurationId, Origin,
            RemoteProjectId, RepositoryPath, CommitSha, "main", "tester", Now, fixture.ConfigurationVersion);
        var selection = new GitLabSourceSelectionEvent(fixture.ProjectId, fixture.ReleaseId, snapshot.Id,
            0, "tester", Now);
        var current = new GitLabCurrentSourceSelection(fixture.ProjectId, fixture.ReleaseId, snapshot.Id,
            selection.Id, "tester", Now);
        fixture.Db.AddRange(snapshot, selection, current);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.PreviewAsync(fixture.Manifest()));
        Assert.Empty(await fixture.Db.ReleasedSyntheticSourceSupplements.ToListAsync());
    }

    [Fact]
    public async Task Configuration_drift_after_preflight_is_refused_without_persisting_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();
        var preflight = fixture.Preflight(manifest);
        var configuration = await fixture.Db.ProjectRepositoryConfigurations.SingleAsync();
        configuration.RecordVerification("changed", Now.AddMinutes(1), RemoteProjectId, RepositoryPath);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<DomainException>(() => fixture.ApplyAsync(manifest, preflight));
        Assert.Empty(await fixture.Db.ReleasedSyntheticSourceSupplements.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabSourceSnapshots.ToListAsync());
    }

    [Fact]
    public async Task Transaction_rollback_removes_the_snapshot_and_supplement_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();
        await using var scope = await ProjectControlledWriteScope.AcquireAsync(fixture.Db, fixture.ProjectId);
        await fixture.Service.ApplyAsync(scope, manifest, fixture.Preflight(manifest), "tester", Now);
        await fixture.Db.SaveChangesAsync();
        await scope.RollbackAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.Empty(await fixture.Db.ReleasedSyntheticSourceSupplements.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabSourceSnapshots.ToListAsync());
    }

    [Fact]
    public async Task Supplement_and_owned_snapshot_are_immutable_at_the_save_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ApplyAsync(fixture.Manifest(), fixture.Preflight(fixture.Manifest()));

        fixture.Db.ChangeTracker.Clear();
        var supplement = await fixture.Db.ReleasedSyntheticSourceSupplements.SingleAsync();
        fixture.Db.Entry(supplement).Property(x => x.Reason).CurrentValue = "tampered";
        await Assert.ThrowsAsync<DomainException>(() => fixture.Db.SaveChangesAsync());

        fixture.Db.ChangeTracker.Clear();
        var snapshot = await fixture.Db.GitLabSourceSnapshots.SingleAsync(x => x.Id == first.SourceSnapshotId);
        fixture.Db.Entry(snapshot).Property(x => x.FriendlyRef).CurrentValue = "tampered";
        await Assert.ThrowsAsync<DomainException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Ordinary_save_does_not_require_the_supplement_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"released_synthetic_source_supplements\"");

        db.Add(new ProgramRecord("Predecessor schema program", "PREDECESSOR"));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Supplement_snapshot_is_browsing_only_for_later_code_relationships()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest();
        var first = await fixture.ApplyAsync(manifest, fixture.Preflight(manifest));
        var laterRelease = new SoftwareRelease(fixture.ProjectId, "1.6", false);
        fixture.Db.Add(laterRelease);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var configuration = await fixture.Db.ProjectRepositoryConfigurations.SingleAsync();
        var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "LLRCR-00001.00");
        var observed = new GitLabMergeRequestDetails(1201, RemoteProjectId, 8, "Retain source state", "opened", false,
            $"{Origin}/{RepositoryPath}/-/merge_requests/8", "retain-source", "main", null, null, null, null,
            new(false, [], "approvals_unavailable", "Approvals were not observed."));

        await using (var mergeScope = await ProjectControlledWriteScope.AcquireAsync(fixture.Db, fixture.ProjectId))
        {
            await Assert.ThrowsAsync<DomainException>(() => new CodeRelationshipService(fixture.Db).AddMergeRequestAsync(
                mergeScope, configuration, Origin, observed, laterRelease.Id, first.SourceSnapshotId, null,
                target, CodeRelationshipMeaning.Implements, "tester", Now, default));
            await mergeScope.RollbackAsync();
        }

        var snapshot = await fixture.Db.GitLabSourceSnapshots.SingleAsync(x => x.Id == first.SourceSnapshotId);
        await using (var fileScope = await ProjectControlledWriteScope.AcquireAsync(fixture.Db, fixture.ProjectId))
        {
            await Assert.ThrowsAsync<DomainException>(() => new CodeRelationshipService(fixture.Db).AddFileAsync(
                fileScope, configuration, Origin, RemoteProjectId, laterRelease.Id, snapshot.Id, null,
                snapshot.CommitSha, "src/demo.c", null, null, 8, target,
                CodeRelationshipMeaning.Implements, "tester", Now, default));
            await fileScope.RollbackAsync();
        }

        Assert.Empty(await fixture.Db.GitLabMergeRequestRelationships.ToListAsync());
        Assert.Empty(await fixture.Db.GitLabFileRelationships.ToListAsync());

        var treeReader = new GitLabMetadataReader(new HttpClient(new Fixture.TreeHandler()), Options.Create(fixture.Settings));
        var tree = await treeReader.ReadTreePageAsync(configuration, snapshot.CommitSha, null, null, 20, default);
        Assert.True(tree.Succeeded, tree.Detail);
        Assert.Equal(snapshot.CommitSha, tree.Value!.CommitSha);
        Assert.Equal("README.md", Assert.Single(tree.Value.Entries).Path);
    }

    private sealed class Fixture(SqliteConnection connection, AeroLinkDbContext db,
        Guid projectId, Guid releaseId, Guid configurationId, long configurationVersion,
        ProjectGitLabOptions settings) : IAsyncDisposable
    {
        public AeroLinkDbContext Db => db;
        public Guid ProjectId => projectId;
        public Guid ReleaseId => releaseId;
        public Guid ConfigurationId => configurationId;
        public long ConfigurationVersion => configurationVersion;
        public ProjectGitLabOptions Settings => settings;
        public ReleasedSyntheticSourceSupplementService Service { get; } = new(
            db, new GitLabMetadataReader(new HttpClient(new RejectingHandler()), Options.Create(settings)),
            Options.Create(settings));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var program = new ProgramRecord("Flight Management System Live", "FMSLIVE");
            var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.5", false);
            var configuration = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
                "GitLab", $"{Origin}/{RepositoryPath}", "tester", Now);
            configuration.RecordVerification("tester", Now, RemoteProjectId, RepositoryPath);
            var baseline = new CandidateBaseline("SW-01.50", 0, project.Id, release.Id, null,
                "FMS 1.5 released baseline", "tester", Now);
            baseline.FreezeForInception("tester", Now);
            baseline.MarkRequirementsMaterialized("tester", new string('c', 64), 0, Now);
            var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-1.5.0",
                "Synthetic released build", "tester", Now);
            build.MarkReleased(Now);
            var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id,
                "FMS 1.5 release", "tester", Now);
            campaign.SelectVerificationBuild(build.Id, "tester", Now);
            campaign.StartVerification("tester", Now);
            campaign.BeginReleaseReview("tester", [("approver", "Approver")], new string('d', 64), Now);
            campaign.Approve("approver", Now);
            campaign.Release(build.Id, new string('d', 64), "tester", Now);
            release.MarkReleased(Now);
            baseline.MarkReleased("tester", Now);
            db.AddRange(program, project, release, configuration, baseline, build, campaign,
                new ShowcaseUpgradeStep(program.Id, "released-campaign", "Fixture ownership marker.", Now));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var settings = new ProjectGitLabOptions
            {
                BaseUrl = Origin,
                ReadAccessToken = "fixture-token",
                ReleasedSyntheticSourceSupplementScope = new()
                {
                    ProgramId = program.Id.ToString("D"),
                    ProjectId = project.Id.ToString("D"),
                    ReleaseId = release.Id.ToString("D"),
                    BaselineId = baseline.Id.ToString("D"),
                    CampaignId = campaign.Id.ToString("D"),
                },
            };
            return new(connection, db, project.Id, release.Id, configuration.Id, configuration.Version, settings);
        }

        public ReleasedSyntheticSourceSupplementManifest Manifest() => new(
            1, ProjectId, ReleaseId, db.ReleaseCampaigns.AsNoTracking().Single().Id,
            db.CandidateBaselines.AsNoTracking().Single().Id, ConfigurationId, ConfigurationVersion, 0,
            Origin, RemoteProjectId, RepositoryPath, CommitSha, CommitSha, GitLabReferenceKind.Commit,
            Guid.Parse("b5d6d377-28b1-4f64-a5d4-f699259ed7d8"), ReleasedSyntheticSourceSupplementService.PolicyId,
            ReleasedSyntheticSourceSupplementService.AuthorizationReference,
            "Dated source-only supplement for the synthetic released FMS 1.5 demonstration.");

        public ReleasedSyntheticSourceSupplementPreflight Preflight(ReleasedSyntheticSourceSupplementManifest manifest) =>
            new(manifest.Digest(), false, null,
                new GitLabCommitReference(RemoteProjectId, manifest.RequestedReference, manifest.ReferenceKind, CommitSha),
                ConfigurationId, ConfigurationVersion);

        public async Task<ReleasedSyntheticSourceSupplementResult> ApplyAsync(
            ReleasedSyntheticSourceSupplementManifest manifest,
            ReleasedSyntheticSourceSupplementPreflight preflight)
        {
            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, ProjectId);
            var result = await Service.ApplyAsync(scope, manifest, preflight, "tester", Now);
            await db.SaveChangesAsync();
            await scope.CommitAsync();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private sealed class RejectingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The service test must not perform remote I/O for this path.");
        }

        internal sealed class TreeHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"README.md\",\"type\":\"blob\",\"path\":\"README.md\",\"mode\":\"100644\"}]")
                };
                return Task.FromResult(response);
            }
        }
    }
}
