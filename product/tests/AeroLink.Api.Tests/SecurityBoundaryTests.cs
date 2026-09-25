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
