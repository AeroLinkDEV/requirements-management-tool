using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AeroLink.Api.Tests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public async Task File_backed_test_database_uses_wal_and_provider_lock_retry_budget()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await AssertSqliteConfigurationAsync(factory.Services);
    }

    [Fact]
    public void File_backed_test_database_uses_wal_and_provider_lock_retry_budget_when_opened_synchronously()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        AssertSqliteConfiguration(factory.Services);
    }

    [Fact]
    public void Api_test_factory_removes_all_background_workers_by_default()
    {
        using var factory = new AeroLinkApiFactory();

        Assert.Empty(AeroLinkHostedServices(factory));
    }

    [Fact]
    public void Api_test_factory_opt_in_registers_only_the_enterprise_job_worker()
    {
        using var factory = new AeroLinkApiFactory(enableEnterpriseJobWorker: true);

        Assert.Collection(AeroLinkHostedServices(factory),
            worker => Assert.IsType<EnterpriseJobWorker>(worker));
    }

    // WebApplicationFactory adds its own GenericWebHostService after ConfigureServices. That service hosts the
    // application; this assertion concerns only the AeroLink.Infrastructure workers this factory replaces.
    private static IHostedService[] AeroLinkHostedServices(AeroLinkApiFactory factory) =>
        factory.Services.GetServices<IHostedService>()
            .Where(worker => worker.GetType().Assembly == typeof(EnterpriseJobWorker).Assembly)
            .ToArray();

    /// <summary>
    /// #593, after #601: WAL was switched on, but nothing kept it switched on.
    ///
    /// <c>PRAGMA journal_mode</c> is persistent in the file header and reads <c>wal</c> whether or not the WAL
    /// index currently exists, so the #601 configuration assertions above pass in both states and cannot see
    /// this. That is the gap. This pins the runtime state instead: the <c>-shm</c> file is actually present,
    /// across the connection churn <c>Pooling=False</c> produces.
    ///
    /// This is a fingerprint, not a contention test — it shows the exclusive build/teardown windows are gone,
    /// not that any reader was unblocked. It fails on the first assertion without the keep-alive connection,
    /// and it also catches the subtler regression of keeping that connection but dropping its warm-up read,
    /// without which SQLite never attaches it to the index and the index is never held at all.
    /// </summary>
    [Fact]
    public async Task Wal_index_is_never_torn_down_while_the_test_database_is_in_use()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var walIndex = new SqliteConnectionStringBuilder(factory.ConnectionString).DataSource + "-shm";

        Assert.True(File.Exists(walIndex), "The WAL index was already gone once the host had started.");
        for (var round = 0; round < 20; round++)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                await db.Projects.AsNoTracking().CountAsync();
            }
            Assert.True(
                File.Exists(walIndex),
                $"The WAL index was torn down after {round + 1} scoped context(s). Readers are exposed to the "
                + "exclusive lock SQLite takes to rebuild it, which is how #593 recurred.");
        }

        // Checking only after an operation is not enough on its own: a connection the host happens to still
        // have open would satisfy it for the wrong reason, and the state that matters is the idle gap between
        // statements, which is where the count reaches zero. Sample across one, and require every sample.
        for (var sample = 0; sample < 50; sample++)
        {
            Assert.True(
                File.Exists(walIndex),
                $"The WAL index was torn down while the host sat idle (sample {sample + 1} of 50). Nothing is "
                + "holding it, so the next reader pays to rebuild it under an exclusive lock.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-sqlite-contention-{Guid.NewGuid():N}.db");
        try
        {
            var holderConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = AeroLinkApiFactory.CommandTimeoutSeconds,
            }.ToString();
            using var holder = new SqliteConnection(holderConnectionString);
            holder.Open();
            using (var journalMode = holder.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode=WAL;";
                Assert.Equal("wal", journalMode.ExecuteScalar()?.ToString()?.ToLowerInvariant());
            }
            using (var createTable = holder.CreateCommand())
            {
                createTable.CommandText = "CREATE TABLE lock_probe (id INTEGER PRIMARY KEY);";
                createTable.ExecuteNonQuery();
            }

            using var holderTransaction = holder.BeginTransaction();
            using (var holderWrite = holder.CreateCommand())
            {
                holderWrite.Transaction = holderTransaction;
                holderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";
                holderWrite.ExecuteNonQuery();
            }

            var contenderConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
            using var contender = new SqliteConnection(contenderConnectionString);
            contender.Open();
            Assert.Equal(1, contender.DefaultTimeout);

            using var busyTimeout = contender.CreateCommand();
            busyTimeout.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(0L, Convert.ToInt64(busyTimeout.ExecuteScalar()));

            using var contenderWrite = contender.CreateCommand();
            Assert.Equal(1, contenderWrite.CommandTimeout);
            contenderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<SqliteException>(() => contenderWrite.ExecuteNonQuery());
            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(750),
                $"The provider returned SQLITE_BUSY too early after {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            Assert.Equal(5, error.SqliteErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AeroLinkApiFactory.DeleteDatabaseArtifacts(path);
        }
    }

    internal static void AssertSqliteConfiguration(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        db.Database.OpenConnection();
        var connection = db.Database.GetDbConnection();
        Assert.Equal(AeroLinkApiFactory.CommandTimeoutSeconds, ((SqliteConnection)connection).DefaultTimeout);
        using var commandTimeout = connection.CreateCommand();
        Assert.Equal(AeroLinkApiFactory.CommandTimeoutSeconds, commandTimeout.CommandTimeout);

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", journalMode.ExecuteScalar()?.ToString()?.ToLowerInvariant());

        using var busyTimeout = connection.CreateCommand();
        busyTimeout.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(0L, Convert.ToInt64(busyTimeout.ExecuteScalar()));
    }

    internal static async Task AssertSqliteConfigurationAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        Assert.Equal(AeroLinkApiFactory.CommandTimeoutSeconds, ((SqliteConnection)connection).DefaultTimeout);
        using var commandTimeout = connection.CreateCommand();
        Assert.Equal(AeroLinkApiFactory.CommandTimeoutSeconds, commandTimeout.CommandTimeout);

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (await journalMode.ExecuteScalarAsync())?.ToString()?.ToLowerInvariant());

        using var busyTimeout = connection.CreateCommand();
        busyTimeout.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(0L, Convert.ToInt64(await busyTimeout.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Mfa_enrollment_returns_interoperable_uri_protects_secret_and_cannot_downgrade_confirmed_factor()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(client);
        using var enrolled = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode); var payload = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        var secret = payload.GetProperty("secret").GetString()!; var uri = payload.GetProperty("otpauthUri").GetString()!;
        Assert.Equal(32, secret.Length); Assert.StartsWith("otpauth://totp/AeroLink%3Aadmin?secret=", uri); Assert.Contains("issuer=AeroLink", uri);
        using (var scope=factory.Services.CreateScope())
        {
            var stored=await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserMfaEnrollments.AsNoTracking().SingleAsync();
            Assert.StartsWith("dp:v1:",stored.Secret); Assert.DoesNotContain(secret,stored.Secret);
        }
        var code=Totp(secret,DateTimeOffset.UtcNow);using var confirmed=await client.PostAsJsonAsync("/api/auth/mfa/confirm",new{code});Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        var recovery=(await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes");Assert.Equal(10,recovery.GetArrayLength());
        var status=await client.GetFromJsonAsync<JsonElement>("/api/auth/security");Assert.True(status.GetProperty("mfaEnabled").GetBoolean());Assert.Equal(10,status.GetProperty("recoveryCodesRemaining").GetInt32());
        using var repeated=await client.PostAsJsonAsync("/api/auth/mfa/enroll",new{});Assert.Equal(HttpStatusCode.Conflict,repeated.StatusCode);
        using var badDisable=await client.PostAsJsonAsync("/api/auth/mfa/disable",new{password=AeroLinkApiFactory.AdministratorPassword,code="000000"});Assert.Equal(HttpStatusCode.Unauthorized,badDisable.StatusCode);
        using var disabled=await client.PostAsJsonAsync("/api/auth/mfa/disable",new{password=AeroLinkApiFactory.AdministratorPassword,code=Totp(secret,DateTimeOffset.UtcNow)});Assert.Equal(HttpStatusCode.NoContent,disabled.StatusCode);
        status=await client.GetFromJsonAsync<JsonElement>("/api/auth/security");Assert.False(status.GetProperty("mfaEnabled").GetBoolean());Assert.Equal(0,status.GetProperty("recoveryCodesRemaining").GetInt32());
    }

    [Fact]
    public async Task Empty_database_bootstrap_requires_secret_runs_once_and_creates_login()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(status.GetProperty("bootstrapRequired").GetBoolean());
        Assert.True(status.GetProperty("bootstrapEnabled").GetBoolean());

        using var denied = await BootstrapAsync(client, "incorrect-bootstrap-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var weakPassword = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret, "too-weak");
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);

        using var created = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var administrator = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdentityService.SystemAdministratorUserName, administrator.GetProperty("userName").GetString());

        using var repeated = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var completed = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.False(completed.GetProperty("bootstrapRequired").GetBoolean());
        Assert.False(completed.GetProperty("bootstrapEnabled").GetBoolean());
    }

    [Fact]
    public void Production_rejects_demo_identity_seeding_without_explicit_override()
    {
        using var factory = new AeroLinkApiFactory(seedDemoAccounts: true);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Demo identity seeding is disabled outside Development", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_member_without_control_roles_cannot_mutate_baseline_or_release_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        Guid baselineId;
        Guid campaignId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Security Boundary Program", "SBP");
            var project = new ProjectRecord(program.Id, "Security Boundary Project", "Boundary Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-90.10", 0, project.Id, release.Id, null, "Security candidate", "configuration.manager", now);
            var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Security campaign", "program.manager", now);
            var member = new UserAccount("program.engineer", "Program Engineer", "program.engineer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, campaign, member, new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            await db.SaveChangesAsync();
            baselineId = baseline.Id;
            campaignId = campaign.Id;
        }

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "program.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AuthorizeMutationsAsync(client);

        var responses = new List<HttpResponseMessage>
        {
            await client.DeleteAsync($"/api/baselines/{baselineId}/selections/{Guid.NewGuid()}"),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/start-verification", new { }),
            await client.PutAsJsonAsync($"/api/release-campaigns/{campaignId}/impact-dispositions", new { changeRequestId = (Guid?)null, state = "Addressed", rationale = "Unauthorized.", actorId = "ignored" }),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/reconcile-lifecycle-links", new { actorId = "ignored" }),
            await client.PostAsync($"/api/release-campaigns/{campaignId}/verification-package", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/verification-build", new { softwareBuildId = Guid.NewGuid(), actorId = "ignored" })
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Disabling_account_revokes_session_so_reenable_does_not_resurrect_cookie()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);

        using var created = await administrator.PostAsJsonAsync("/api/admin/users", new
        {
            userName = "session.user",
            displayName = "Session User",
            email = "session.user@example.test",
            temporaryPassword = AeroLinkApiFactory.MemberPassword
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var userId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var user = factory.CreateClient();
        using var login = await user.PostAsJsonAsync("/api/auth/login", new { userName = "session.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync($"/api/admin/users/{userId}/state", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync($"/api/admin/users/{userId}/state", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Administrator_can_revoke_membership_and_delegation_with_audit_state_retained()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);
        var now = DateTimeOffset.UtcNow;
        Guid programId;
        Guid delegatorId;
        Guid delegateId;
        Guid delegationId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Authority Revocation Program", "ARP");
            var delegator = new UserAccount("delegator.user", "Delegator User", "delegator@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegateUser = new UserAccount("delegate.user", "Delegate User", "delegate@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegatorMembership = new ProgramMembership(delegator.Id, program.Id, ProgramRole.Engineer, "test.setup", now);
            var delegateMembership = new ProgramMembership(delegateUser.Id, program.Id, ProgramRole.Engineer, "test.setup", now);
            var delegation = new RoleDelegation(program.Id, delegator.Id, delegateUser.Id, ProgramRole.Engineer, now.AddMinutes(-1), now.AddHours(1), "Temporary engineering coverage.", "test.setup", now);
            db.AddRange(program, delegator, delegateUser, delegatorMembership, delegateMembership, delegation);
            await db.SaveChangesAsync();
            programId = program.Id;
            delegatorId = delegator.Id;
            delegateId = delegateUser.Id;
            delegationId = delegation.Id;
        }

        using var membership = await administrator.DeleteAsync($"/api/admin/users/{delegateId}/memberships/{programId}/Engineer");
        Assert.Equal(HttpStatusCode.NoContent, membership.StatusCode);
        using var delegationResponse = await administrator.DeleteAsync($"/api/delegations/{delegationId}");
        Assert.Equal(HttpStatusCode.NoContent, delegationResponse.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Ended rather than removed, which is what the delegation half of this test has always asserted about
        // its own revocation. The membership row is retained so the roster can still say who held this and
        // when; what must not survive is the authority (DEC-110).
        var revoked = await verificationDb.ProgramMemberships.AsNoTracking()
            .SingleAsync(x => x.UserId == delegateId && x.ProgramId == programId && x.Role == ProgramRole.Engineer);
        Assert.NotNull(revoked.EndedAt);
        Assert.False(await verificationDb.ProgramMemberships.AnyAsync(x => x.UserId == delegateId && x.ProgramId == programId && x.Role == ProgramRole.Engineer && x.EndedAt == null));
        Assert.NotNull((await verificationDb.RoleDelegations.AsNoTracking().SingleAsync(x => x.Id == delegationId)).RevokedAt);
        Assert.True(await verificationDb.SecurityAuditEvents.CountAsync(x => x.EventType == "RoleRevoked" || x.EventType == "DelegationRevoked") >= 2);
        Assert.NotEqual(Guid.Empty, delegatorId);
    }

    [Fact]
    public async Task Only_a_position_holder_or_backup_can_delegate_a_position_governed_role()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Delegation Leadership Program", "DLP");
        var baseOnly = Account("base.only.manager", "Base Only Manager");
        var primary = Account("primary.manager", "Primary Manager");
        var backup = Account("backup.manager", "Backup Manager");
        var baseDelegate = Account("base.delegate", "Base Delegate");
        var primaryDelegate = Account("primary.delegate", "Primary Delegate");
        var backupDelegate = Account("backup.delegate", "Backup Delegate");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(program, baseOnly, primary, backup, baseDelegate, primaryDelegate, backupDelegate);
            db.ProgramMemberships.AddRange(
                new(baseOnly.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
                new(primary.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
                new(backup.Id, program.Id, ProgramRole.ProgramManager, "test.setup", now),
                new(baseDelegate.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
                new(primaryDelegate.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
                new(backupDelegate.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            db.ProjectLeadershipAssignments.Add(new(program.Id, ProjectLeadershipPosition.ProgramManager,
                primary.Id, "test.setup", now));
            db.ProjectLeadershipBackups.Add(new(program.Id, ProjectLeadershipPosition.ProgramManager,
                backup.Id, "test.setup", now));
            await db.SaveChangesAsync();
        }

        using var baseOnlyClient = await LoginAsync(baseOnly.UserName);
        using var refused = await CreateDelegationAsync(baseOnlyClient, baseOnly.Id, baseDelegate.Id);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var primaryClient = await LoginAsync(primary.UserName);
        using var primaryCreated = await CreateDelegationAsync(primaryClient, primary.Id, primaryDelegate.Id);
        Assert.Equal(HttpStatusCode.Created, primaryCreated.StatusCode);

        using var backupClient = await LoginAsync(backup.UserName);
        using var backupCreated = await CreateDelegationAsync(backupClient, backup.Id, backupDelegate.Id);
        Assert.Equal(HttpStatusCode.Created, backupCreated.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await verificationDb.RoleDelegations.AsNoTracking().CountAsync(
            x => x.ProgramId == program.Id && x.Role == ProgramRole.ProgramManager));

        UserAccount Account(string userName, string displayName) =>
            new(userName, displayName, $"{userName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

        async Task<HttpClient> LoginAsync(string userName)
        {
            var client = factory.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new { userName, password = AeroLinkApiFactory.MemberPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await AuthorizeMutationsAsync(client);
            return client;
        }

        Task<HttpResponseMessage> CreateDelegationAsync(HttpClient client, Guid delegatorId, Guid delegateId) =>
            client.PostAsJsonAsync("/api/delegations", new
            {
                programId = program.Id,
                delegatorUserId = delegatorId,
                delegateUserId = delegateId,
                role = ProgramRole.ProgramManager,
                startsAt = now.AddMinutes(-1),
                endsAt = now.AddHours(1),
                reason = "Temporary accountable management coverage.",
            });
    }

    [Fact]
    public async Task Role_session_and_delegation_lifecycle_exposes_current_state_without_erasing_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);
        var now = DateTimeOffset.UtcNow;
        Guid programId;
        Guid delegatorId;
        Guid delegateId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Identity Lifecycle Program", "ILP");
            var delegator = new UserAccount("identity.delegator", "Identity Delegator", "identity.delegator@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegateUser = new UserAccount("identity.delegate", "Identity Delegate", "identity.delegate@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(
                program,
                delegator,
                delegateUser,
                new ProgramMembership(delegator.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
                new RoleDelegation(program.Id, delegator.Id, delegateUser.Id, ProgramRole.Engineer, now.AddHours(-2), now.AddHours(-1), "Expired coverage retained for history.", "test.setup", now.AddHours(-3)),
                new RoleDelegation(program.Id, delegator.Id, delegateUser.Id, ProgramRole.Engineer, now.AddMinutes(-1), now.AddHours(1), "Active coverage available for revocation.", "test.setup", now));
            await db.SaveChangesAsync();
            programId = program.Id;
            delegatorId = delegator.Id;
            delegateId = delegateUser.Id;
        }

        using var granted = await administrator.PostAsJsonAsync($"/api/admin/users/{delegateId}/memberships", new { programId, role = "Engineer" });
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        using var duplicate = await administrator.PostAsJsonAsync($"/api/admin/users/{delegateId}/memberships", new { programId, role = "Engineer" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var lastRoleRemoved = await administrator.DeleteAsync($"/api/admin/users/{delegateId}/memberships/{programId}/Engineer");
        Assert.Equal(HttpStatusCode.NoContent, lastRoleRemoved.StatusCode);

        using var secondAdministratorSession = factory.CreateClient();
        using var login = await secondAdministratorSession.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AuthorizeMutationsAsync(secondAdministratorSession);
        var sessions = await secondAdministratorSession.GetFromJsonAsync<JsonElement>("/api/auth/sessions");
        Assert.Equal(2, sessions.EnumerateArray().Count(x => x.GetProperty("revokedAt").ValueKind == JsonValueKind.Null));
        Assert.Single(sessions.EnumerateArray(), x => x.GetProperty("current").GetBoolean());
        using var sessionsRevoked = await secondAdministratorSession.PostAsJsonAsync("/api/auth/sessions/revoke-others", new { });
        Assert.Equal(HttpStatusCode.OK, sessionsRevoked.StatusCode);
        Assert.Equal(1, (await sessionsRevoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());

        using var delegatorClient = factory.CreateClient();
        using var delegatorLogin = await delegatorClient.PostAsJsonAsync("/api/auth/login", new { userName = "identity.delegator", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, delegatorLogin.StatusCode);
        await AuthorizeMutationsAsync(delegatorClient);
        var delegations = await delegatorClient.GetFromJsonAsync<JsonElement>("/api/delegations");
        var delegationRows = delegations.EnumerateArray().ToList();
        Assert.Contains(delegationRows, x => x.GetProperty("status").GetString() == "Expired" && !x.GetProperty("canRevoke").GetBoolean());
        var active = Assert.Single(delegationRows, x => x.GetProperty("status").GetString() == "Active");
        Assert.Equal("Identity Lifecycle Program", active.GetProperty("program").GetString());
        Assert.Equal("Identity Delegator", active.GetProperty("delegator").GetString());
        Assert.Equal("Identity Delegate", active.GetProperty("delegateName").GetString());
        Assert.Equal("test.setup", active.GetProperty("actor").GetString());
        Assert.True(active.GetProperty("canRevoke").GetBoolean());
        using var revoked = await delegatorClient.DeleteAsync($"/api/delegations/{active.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        delegations = await delegatorClient.GetFromJsonAsync<JsonElement>("/api/delegations");
        Assert.Contains(delegations.EnumerateArray(), x => x.GetProperty("status").GetString() == "Revoked");

        using var verificationScope = factory.Services.CreateScope();
        var audit = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().SecurityAuditEvents.AsNoTracking();
        Assert.Equal(1, await audit.CountAsync(x => x.EventType == "RoleGranted" && x.Target == delegateId.ToString()));
        Assert.Equal(1, await audit.CountAsync(x => x.EventType == "RoleRevoked" && x.Target == delegateId.ToString()));
        Assert.NotEqual(Guid.Empty, delegatorId);
    }

    private static async Task<HttpResponseMessage> BootstrapAsync(HttpClient client, string secret, string password = AeroLinkApiFactory.AdministratorPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "AeroLink Administrator",
                email = "admin@example.test",
                password
            })
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", secret);
        return await client.SendAsync(request);
    }

    internal static async Task BootstrapAndLoginAdministratorAsync(HttpClient client)
    {
        using var bootstrap = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AuthorizeMutationsAsync(client);
    }

    internal static async Task AuthorizeMutationsAsync(HttpClient client)
    {
        var response=await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-AeroLink-CSRF");
        client.DefaultRequestHeaders.Add("X-AeroLink-CSRF",response.GetProperty("token").GetString());
    }

    private static string Totp(string secret,DateTimeOffset now)
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var bytes=new List<byte>();var buffer=0;var bits=0;
        foreach(var ch in secret){buffer=(buffer<<5)|alphabet.IndexOf(ch);bits+=5;if(bits>=8){bits-=8;bytes.Add((byte)((buffer>>bits)&255));}}
        var counter=BitConverter.GetBytes(now.ToUnixTimeSeconds()/30);if(BitConverter.IsLittleEndian)Array.Reverse(counter);using var hmac=new System.Security.Cryptography.HMACSHA1(bytes.ToArray());var hash=hmac.ComputeHash(counter);var offset=hash[^1]&15;var value=((hash[offset]&127)<<24)|(hash[offset+1]<<16)|(hash[offset+2]<<8)|hash[offset+3];return(value%1_000_000).ToString("D6");
    }
}

internal sealed class AeroLinkApiFactory(bool seedDemoAccounts = false, bool allowDemoAccounts = false,
    string? showcaseTemplate = null, string? staticFilesRoot = null,
    DbCommandInterceptor? commandInterceptor = null,
    IManagedDocumentStorageFaultInjector? storageFaultInjector = null,
    ILadderPolicy? testLadderPolicy = null,
    bool attachProjectLadders = true,
    bool enableEnterpriseJobWorker = false,
    Action<object>? telemetryObserver = null,
    string? postgresConnection = null,
    [CallerFilePath] string? callerFile = null,
    [CallerMemberName] string? callerMember = null) : WebApplicationFactory<Program>
{
    public const string BootstrapSecret = "test-bootstrap-secret-0123456789-abcdef";
    public const string AdministratorPassword = "Bootstrap-Admin!2026";
    public const string MemberPassword = "Program-Member!2026";
    // Keep Microsoft.Data.Sqlite's provider retry budget at the previous 30-second value. This is the
    // SQLITE_BUSY/SQLITE_LOCKED retry budget, not a whole-command wall-clock budget.
    internal const int CommandTimeoutSeconds = 30;
    private readonly DisposableDatabase _database = NewDatabase(showcaseTemplate);
    private string DatabasePath => _database.Path;
    private readonly string? _postgresConnection = postgresConnection;
    public string ConnectionString => _postgresConnection ?? DatabaseConnectionString(DatabasePath);

    /// <summary>
    /// The database file, together with the one connection that must stay open while it is in use.
    ///
    /// WAL mode only helps while the WAL index exists. SQLite builds that index — the <c>-shm</c> file — when
    /// the first connection to a database opens, and tears it down, checkpointing and unlinking <c>-wal</c> and
    /// <c>-shm</c>, when the last one closes. Both ends take an exclusive lock on the whole database, and an
    /// exclusive lock blocks readers, which is the one thing WAL exists to prevent.
    ///
    /// <c>Pooling=False</c> means EF opens a real connection per operation and closes it again, so the count
    /// returns to zero between almost every statement. Sampled on an idle host before this connection was held,
    /// the index was absent for 195 of 200 samples: #601 turned WAL on, but nothing kept it on, so operations
    /// were paying for an index build and teardown they need not have paid for. Holding one warmed connection
    /// removes both windows, and WAL behaves as #601 intended.
    ///
    /// That is a necessary part of #593's recurrence, not the whole of it. A single one of those windows is
    /// sub-millisecond; exhausting a 30-second budget also required the runner starvation visible in the same
    /// shard, where this test's host build took 27.8 s against a median of 1.0 s. This removes the windows. It
    /// does not remove the starvation, so treat a quiet suite as encouraging rather than as proof.
    ///
    /// The connection must stay idle. A read transaction left open on it pins the WAL and stops autocheckpoint
    /// reclaiming it, so the file grows without bound; it exists to hold the index, and nothing else.
    /// </summary>
    private sealed record DisposableDatabase(string Path, SqliteConnection WalIndexKeepAlive);

    /// <summary>
    /// A private database file, optionally starting as a copy of an already-seeded showcase.
    ///
    /// Three tests in this assembly seeded the FMS showcase inside their own factory, which is 40 to 60 seconds
    /// each and was 177 of the assembly's 552 CPU-seconds for a dataset identical all three times. The copy
    /// happens before the host starts, so the API opens a database that is already populated and its startup
    /// EnsureCreated finds nothing to do.
    /// </summary>
    private static DisposableDatabase NewDatabase(string? template)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-api-tests-{Guid.NewGuid():N}.db");
        SqliteConnection? keepAlive = null;
        try
        {
            if (template is not null)
            {
                // Only the .db is copied. That is safe while the template is written in DELETE mode, but a
                // template in WAL mode can hold committed rows in its -wal, and copying the .db alone would
                // silently drop them — a half-seeded showcase surfacing as an assertion failure somewhere else
                // entirely. Fail closed instead of copying something that is missing its tail.
                if (File.Exists(template + "-wal"))
                    throw new InvalidOperationException(
                        $"The showcase template '{template}' has an unmerged -wal; copying the database alone "
                        + "would lose committed rows. Checkpoint the template before using it as a template.");
                File.Copy(template, path);
            }
            // The API host and test-scoped contexts intentionally use separate connections to this file. WAL lets
            // readers run while a writer is active; the provider retry budget handles remaining serialized-writer
            // contention without changing the product's PostgreSQL or SQLite configuration.
            keepAlive = new SqliteConnection(DatabaseConnectionString(path));
            keepAlive.Open();
            using var command = keepAlive.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite API test databases must support WAL mode.");
            // Setting the pragma is not the same as joining the WAL index: SQLite attaches a connection to the
            // index when that connection first reads or writes, and only an attached connection keeps the index
            // alive once the others close. Read something that exists before the schema does.
            using var warm = keepAlive.CreateCommand();
            warm.CommandText = "SELECT count(*) FROM sqlite_master;";
            warm.ExecuteScalar();
            return new DisposableDatabase(path, keepAlive);
        }
        catch
        {
            keepAlive?.Dispose();
            DeleteDatabaseArtifacts(path);
            throw;
        }
    }

    private static string DatabaseConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = CommandTimeoutSeconds,
    }.ToString();

    internal static void ConfigureSqliteOptions(DbContextOptionsBuilder options, string connectionString, params IInterceptor[] interceptors)
    {
        options.UseSqlite(connectionString)
            .AddInterceptors(interceptors);
    }

    private readonly string _evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-api-evidence-{Guid.NewGuid():N}");
    private readonly string _connectorKeyPath = Path.Combine(Path.GetTempPath(), $"aerolink-connector-key-{Guid.NewGuid():N}.pem");
    private static long _nextFactoryId;
    private readonly long _factoryId = Interlocked.Increment(ref _nextFactoryId);
    private readonly string _callerFile = callerFile ?? "unknown";
    private readonly string _callerMember = callerMember ?? "unknown";
    private readonly Stopwatch _construction = Stopwatch.StartNew();
    private double _constructionBeforeHostMs;
    private readonly Action<object>? _telemetryObserver = telemetryObserver;
    // WebApplicationFactory.Dispose() enters this virtual method, then synchronously invokes its virtual
    // DisposeAsync(), which returns here once more after stopping the host. Both public disposal paths must
    // start one shared timer before that shutdown, while only one callback owns AeroLink's cleanup and
    // telemetry. Interlocked keeps that ownership singular if a caller races disposal paths.
    private int _aeroLinkDisposeStarted;
    private readonly object _disposalStopwatchGate = new();
    private Stopwatch? _disposalStopwatch;

    internal long TelemetryFactoryId => _factoryId;

    public override async ValueTask DisposeAsync()
    {
        StartDisposalStopwatch();
        await base.DisposeAsync();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Capture construction latency BEFORE base.CreateHost starts: constructionMs and hostMs are
        // non-overlapping intervals. Reading _construction.Elapsed in the finally would include the host
        // build and double-count it in the aggregator (constructionMs + hostMs + disposeMs).
        _constructionBeforeHostMs = _construction.Elapsed.TotalMilliseconds;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            ApiTestTelemetry.RecordFactoryPhase("host", _constructionBeforeHostMs, stopwatch.Elapsed.TotalMilliseconds, _callerFile, _callerMember, _factoryId, _telemetryObserver);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseContentRoot(FindApiContentRoot());
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = _postgresConnection is null ? "Sqlite" : "PostgreSql",
            ["ConnectionStrings:AeroLink"] = ConnectionString,
            ["Evidence:Root"] = _evidenceRoot,
            ["Connector:DeploymentId"] = "aerolink-api-tests",
            ["Connector:SigningKeyPath"] = _connectorKeyPath,
            ["DemoData:Enabled"] = "false",
            ["Identity:SeedDemoAccounts"] = seedDemoAccounts.ToString(),
            ["Identity:AllowDemoAccounts"] = allowDemoAccounts.ToString(),
            ["Identity:BootstrapSecret"] = BootstrapSecret,
            ["Identity:CookieSecure"] = "false",
            ["Identity:LoginRateLimitPerMinute"] = "500",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
        };
        if (staticFilesRoot is not null) settings["Client:StaticFiles"] = staticFilesRoot;
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AeroLinkDbContext>();
            services.RemoveAll<DbContextOptions<AeroLinkDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AeroLinkDbContext>>();
            services.AddDbContext<AeroLinkDbContext>(options =>
            {
                if (_postgresConnection is null)
                    ConfigureSqliteOptions(options, ConnectionString, new SaveRaceInterceptor(),
                        new TimingConnectionInterceptor(_factoryId, _callerFile, _callerMember, _telemetryObserver));
                else
                    options.UseNpgsql(ConnectionString).AddInterceptors(
                        new TimingConnectionInterceptor(_factoryId, _callerFile, _callerMember, _telemetryObserver));
                if (attachProjectLadders) options.AddInterceptors(new TestProjectLadderInterceptor());
                if (commandInterceptor is not null) options.AddInterceptors(commandInterceptor);
            });
            // Test hosts must not run the five production polling workers: each opens independent SQLite
            // connections, and their idle commands make request-count and lock-contention evidence noisy.
            // Removing them is independent of any command interceptor; the two publication suites explicitly
            // opt back into the one worker they exercise. Keep all other production workers out of API tests.
            services.RemoveAll<IHostedService>();
            if (enableEnterpriseJobWorker) services.AddHostedService<EnterpriseJobWorker>();
            if (storageFaultInjector is not null)
            {
                services.RemoveAll<IManagedDocumentStorageFaultInjector>();
                services.AddSingleton(storageFaultInjector);
            }
            if (testLadderPolicy is not null)
            {
                services.RemoveAll<IProjectLadderPolicyResolver>();
                services.AddSingleton<IProjectLadderPolicyResolver>(new FixedProjectLadderPolicyResolver(testLadderPolicy));
            }
        });
    }

    /// <summary>
    /// Tidying up, which must never be the reason a test is reported as failed.
    ///
    /// On Windows CI a handle to the throwaway database occasionally outlives the host that opened it, and
    /// <c>File.Delete</c> then throws from inside <c>Dispose</c> — turning a test whose every assertion passed
    /// into a red one, with a stack trace that says nothing about the product. The file lives in the system
    /// temp directory; leaving one behind costs nothing, and losing the signal costs a great deal.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        var stopwatch = StartDisposalStopwatch();
        if (Interlocked.Exchange(ref _aeroLinkDisposeStarted, 1) != 0) return;

        ExceptionDispatchInfo? baseDisposeException = null;
        ExceptionDispatchInfo? cleanupException = null;
        try
        {
            try
            {
                base.Dispose(disposing);
            }
            catch (Exception problem)
            {
                baseDisposeException = ExceptionDispatchInfo.Capture(problem);
            }
        }
        finally
        {
            // Released after the host, so the WAL index outlives every connection the host owns and is torn down
            // once, here, rather than between statements. Deliberately outside the cleanup block below and
            // deliberately swallowing: while this connection is open Windows refuses to delete the database and
            // its sidecars, so letting it throw here would skip every remaining step, guarantee a three-file
            // leak, and fail a test whose assertions all passed — the exact outcome this method exists to avoid.
            try { _database.WalIndexKeepAlive.Dispose(); }
            catch (Exception) { }

            try
            {
                SqliteConnection.ClearAllPools();
                DeleteDatabaseArtifacts(DatabasePath);
                try { if (Directory.Exists(_evidenceRoot)) Directory.Delete(_evidenceRoot, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                DeleteIfPresent(_connectorKeyPath);
            }
            catch (Exception problem)
            {
                cleanupException = ExceptionDispatchInfo.Capture(problem);
            }

            try
            {
                ApiTestTelemetry.RecordFactoryPhase("dispose", _constructionBeforeHostMs, stopwatch.Elapsed.TotalMilliseconds, _callerFile, _callerMember, _factoryId, _telemetryObserver);
            }
            catch when (baseDisposeException is not null || cleanupException is not null)
            {
                // Preserve the first failure; telemetry must not mask a disposal or cleanup exception.
            }
        }

        if (baseDisposeException is not null) baseDisposeException.Throw();
        if (cleanupException is not null) cleanupException.Throw();
    }

    private Stopwatch StartDisposalStopwatch()
    {
        lock (_disposalStopwatchGate)
        {
            return _disposalStopwatch ??= Stopwatch.StartNew();
        }
    }

    internal static void DeleteDatabaseArtifacts(string path)
    {
        DeleteIfPresent(path);
        DeleteIfPresent(path + "-shm");
        DeleteIfPresent(path + "-wal");
    }

    // Retried briefly before being given up on, because the usual cause is a handle closing a moment late
    // rather than one held for good.
    private static void DeleteIfPresent(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return;
                Thread.Sleep(100);
            }
        }
    }

    private static string FindApiContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx"))) current = current.Parent;
        if (current is null) throw new InvalidOperationException("Could not locate the product solution root for API tests.");
        return Path.Combine(current.FullName, "src", "AeroLink.Api");
    }
}
