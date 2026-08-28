using AeroLink.Domain.Assurance;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AeroLink.Infrastructure.Persistence;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

[CollectionDefinition("Issue711Postgres", DisableParallelization = true)]
public sealed class Issue711PostgresCollection : ICollectionFixture<object>;

/// <summary>
/// The #711 schema on PostgreSQL, which the SQLite API suite cannot speak for.
///
/// Two things need a real server. The partial unique index that keeps one effective policy version per
/// project is a database guarantee rather than a service convention, and the upgrade path has to leave an
/// existing release campaign carrying no snapshot — because a campaign that predates the feature was run
/// under the AeroLink recommendations, and inventing a snapshot for it would be a claim about history.
/// </summary>
[Collection("Issue711Postgres")]
public sealed class AssurancePolicyPostgresQualificationTests
{
    private const string DatabaseName = "aerolink_711_qualify";
    private const string PreFeatureMigration = "20260825114510_AddExecutionCutoverSchema";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Issue711PostgresFact]
    public async Task Clean_install_carries_the_assurance_schema_and_its_database_level_guarantees()
    {
        await using var db = await ResetAtLatestAsync(QualificationConnectionOrSkip());
        var project = await SeedProjectAsync(db);

        var first = ProjectAssurancePolicy.Record(project, 1, AssuranceLevel.LevelB,
            new Dictionary<AssurancePolicyLever, AssuranceLeverValue>
            {
                [AssurancePolicyLever.RequirementCoverageBeforeRelease] = AssuranceLeverValue.NotRequired,
            }, "Declare the pilot posture.", "cm", Now);
        db.ProjectAssurancePolicies.Add(first);
        await db.SaveChangesAsync();

        var definition = AssurancePolicyCatalogue.Definition(AssurancePolicyLever.RequirementCoverageBeforeRelease);
        var proposer = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var decision = AssuranceDeviationAuthority.Decide(AssuranceDeviationClass.Verification, Guid.NewGuid(),
            proposer, new(approver, "sqa", [ProgramRole.SoftwareQualityAnalyst], [], false, []), Now);
        db.AssurancePolicyDeviations.Add(AssurancePolicyDeviation.Approve(project, first.Id, 1, definition,
            "Project", AssuranceLeverValue.NotRequired, "The customer runs this campaign.",
            AssuranceDeviationClass.Verification, false, proposer, "cm", approver, "sqa", decision, Now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.AssurancePolicyDeviations.AsNoTracking().SingleAsync(x => x.ProjectId == project);
        Assert.True(stored.VerifyRecord());
        Assert.Equal(AssuranceLeverValue.NotRequired, stored.SelectedValue);

        // One effective version per project, enforced by the partial unique index rather than by the service.
        db.ProjectAssurancePolicies.Add(ProjectAssurancePolicy.Record(project, 2, AssuranceLevel.LevelB,
            AssurancePolicyCatalogue.Recommended, "A second effective version must be impossible.", "cm", Now));
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_project_assurance_policies_effective", conflict.InnerException?.Message ?? conflict.Message);
        db.ChangeTracker.Clear();

        // Superseding the first version frees the slot, and the superseded row keeps exactly what it said.
        var effective = await db.ProjectAssurancePolicies.SingleAsync(x => x.Id == first.Id);
        var snapshot = effective.SelectionsSnapshot;
        effective.Supersede("cm", Now.AddMinutes(5));
        db.ProjectAssurancePolicies.Add(ProjectAssurancePolicy.Record(project, 2, AssuranceLevel.LevelB,
            AssurancePolicyCatalogue.Recommended, "Return to the AeroLink recommendations.", "cm", Now.AddMinutes(5)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(snapshot, (await db.ProjectAssurancePolicies.AsNoTracking().SingleAsync(x => x.Id == first.Id)).SelectionsSnapshot);
        Assert.Equal(2, await db.ProjectAssurancePolicies.CountAsync(x => x.ProjectId == project));

        // Self-approval is refused by the database as well as by the resolver, so no direct write can produce
        // a deviation somebody approved for themselves.
        var selfApproved = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO assurance_policy_deviations (\"Id\",\"ProjectId\",\"PolicyVersionId\",\"PolicyVersion\",\"Lever\",\"Scope\","
            + "\"RecommendedValue\",\"RecommendationBasis\",\"BasisKind\",\"SelectedValue\",\"Rationale\",\"DeviationClass\","
            + "\"AirworthinessDesignated\",\"ReleaseEffect\",\"ProposedByAccountId\",\"ProposedBy\",\"ProposedAt\","
            + "\"ApprovedByAccountId\",\"ApprovedBy\",\"ApprovalAuthority\",\"ApprovalAuthoritySource\","
            + "\"AuthorityPolicyVersion\",\"EffectiveFrom\",\"SupersededBy\",\"SupersededReason\",\"RecordHash\") "
            + $"VALUES ('{Guid.NewGuid()}','{project}','{first.Id}',1,'RequirementCoverageBeforeRelease','Project',"
            + "'Required','basis','AeroLinkRule','NotRequired','rationale','Verification',false,'effect',"
            + $"'{proposer}','cm','{Now:O}','{proposer}','cm','SoftwareQualityAnalyst','Membership',1,'{Now:O}','','','hash');"));
        Assert.Equal("CK_assurance_deviation_distinct_parties", selfApproved.ConstraintName);
    }

    [Issue711PostgresFact]
    public async Task Upgrade_leaves_an_existing_campaign_on_the_AeroLink_recommendations_it_was_run_under()
    {
        var connection = QualificationConnectionOrSkip();
        Guid campaignId;
        await using (var pre = new AeroLinkDbContext(Options(connection)))
        {
            await pre.Database.EnsureDeletedAsync();
            await pre.Database.GetService<IMigrator>().MigrateAsync(PreFeatureMigration);
            Assert.False(await TableExistsAsync(pre, "project_assurance_policies"));
            Assert.False(await ColumnExistsAsync(pre, "release_campaigns", "AssurancePolicyVersionId"));

            var project = await SeedProjectAsync(pre);
            var release = new SoftwareRelease(project, "1.0", false);
            var baseline = new CandidateBaseline("BL-00000001", 0, project, release.Id, null, "Pre-upgrade", "cm", Now);
            pre.AddRange(release, baseline);
            await pre.SaveChangesAsync();

            // Written as SQL rather than through the aggregate: the model already knows about the snapshot
            // column, and the whole point of this arrangement is a campaign created before that column existed.
            campaignId = Guid.NewGuid();
            await pre.Database.ExecuteSqlRawAsync(
                "INSERT INTO release_campaigns (\"Id\",\"ProjectId\",\"ReleaseId\",\"BaselineId\",\"Name\",\"OwnerId\","
                + "\"State\",\"Version\",\"CreatedAt\",\"UpdatedAt\") "
                + $"VALUES ('{campaignId}','{project}','{release.Id}','{baseline.Id}','1.0','program.manager',"
                + $"'Planning',1,'{Now:O}','{Now:O}');");
        }

        await using var db = new AeroLinkDbContext(Options(connection));
        await db.Database.MigrateAsync();
        Assert.True(await TableExistsAsync(db, "project_assurance_policies"));
        Assert.True(await TableExistsAsync(db, "assurance_policy_deviations"));

        var upgraded = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId);
        // No snapshot is invented for it. Resolving that absence yields the AeroLink recommendations, which
        // are the rules this campaign was actually run under.
        Assert.Null(upgraded.AssurancePolicyVersionId);
        var resolved = await new EffectiveProjectAssurancePolicyResolver(db).ResolveAsync(upgraded.ProjectId, default);
        Assert.Equal(ResolvedAssurancePolicy.Recommended.Selections, resolved.Selections);
        Assert.Equal(AssuranceLevel.NotDeclared, resolved.DeclaredLevel);
        Assert.Null(resolved.PolicyVersionId);
    }

    private static async Task<Guid> SeedProjectAsync(AeroLinkDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var program = new ProgramRecord($"Assurance qualification {tag}", $"AQ{tag}".ToUpperInvariant());
        var project = new ProjectRecord(program.Id, $"Assurance qualification {tag}", "Assurance qualification software");
        db.AddRange(program, project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<bool> TableExistsAsync(AeroLinkDbContext db, string table) =>
        await db.Database.SqlQueryRaw<bool>(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '{table}') AS \"Value\"")
            .SingleAsync();

    private static async Task<bool> ColumnExistsAsync(AeroLinkDbContext db, string table, string column) =>
        await db.Database.SqlQueryRaw<bool>(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}') AS \"Value\"")
            .SingleAsync();

    private static async Task<AeroLinkDbContext> ResetAtLatestAsync(string connectionString)
    {
        var db = new AeroLinkDbContext(Options(connectionString));
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrSkip() => ValidateQualificationConnection(ResolveQualificationConnection());

    /// <summary>
    /// The connection this qualification runs against, or null when no PostgreSQL server was offered.
    ///
    /// The shared variable is accepted so an ordinary maintainer run does not silently skip these two tests,
    /// and it is passed through exactly as supplied. <see cref="ValidateQualificationConnection"/> then
    /// refuses anything that does not already name the dedicated disposable database — these tests call
    /// EnsureDeletedAsync, and silently retargeting somebody's connection would drop a database they never
    /// nominated for #711.
    /// </summary>
    internal static string? ResolveQualificationConnection()
    {
        var dedicated = Environment.GetEnvironmentVariable("AEROLINK_711_CONNECTION");
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;
        var shared = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        return string.IsNullOrWhiteSpace(shared) ? null : shared;
    }

    private static string ValidateQualificationConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "Issue #711 PostgreSQL qualification requires AEROLINK_711_CONNECTION or AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var host = (builder.Host ?? string.Empty).Trim().Trim('[', ']');
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #711 PostgreSQL qualification requires a loopback host.");
        if (builder.Port == 54329)
            throw new InvalidOperationException("Issue #711 qualification refuses the protected PostgreSQL port 54329.");
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Issue #711 PostgreSQL qualification requires the dedicated database {DatabaseName}.");
        return connection;
    }

    private sealed class Issue711PostgresFactAttribute : FactAttribute
    {
        public Issue711PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(ResolveQualificationConnection()))
                Skip = "Issue #711 PostgreSQL qualification skipped: set AEROLINK_711_CONNECTION or AEROLINK_MIGRATIONS_CONNECTION.";
        }
    }
}
