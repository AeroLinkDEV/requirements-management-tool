using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class FederatedIdentityRuntimeServiceTests
{
    [Fact]
    public async Task Bound_subject_creates_revocable_session_with_transient_mapped_roles()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management", "FMS");
        var user = new UserAccount("federated.user", "Federated User", "federated@example.test", IdentityService.HashPassword("Temporary!123"), now);
        fixture.Db.Programs.Add(program);
        fixture.Db.UserAccounts.Add(user);
        await fixture.Db.SaveChangesAsync();

        var administration = new ExternalIdentityAdministrationService(fixture.Db);
        var provider = await administration.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "local", now, default);
        await administration.CreateMappingAsync(provider.Id, "fms-approvers", program.Id, ProgramRole.Approver, "admin", "local", now, default);

        var runtime = new FederatedIdentityRuntimeService(fixture.Db, administration);
        await runtime.BindAsync(provider.Id, "subject-123", user.Id, "admin", "local", now, default);

        var result = await runtime.CreateSessionAsync(new(provider.Id, provider.Issuer, "subject-123", ["FMS-APPROVERS"]),
            "127.0.0.1", "test-agent", now.AddMinutes(1), default);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result!.User.Id);
        var access = Assert.Single(result.User.Programs);
        Assert.Equal(program.Id, access.ProgramId);
        Assert.Contains(nameof(ProgramRole.Approver), access.Roles);
        Assert.Single(await fixture.Db.UserSessions.AsNoTracking().ToListAsync());
        Assert.Contains(await fixture.Db.SecurityAuditEvents.Select(x => x.EventType).ToListAsync(), x => x == "FederatedLogin");

        var resolved = await new IdentityService(fixture.Db).ResolveAsync(result.Token, now.AddMinutes(2), default);
        Assert.NotNull(resolved);
        Assert.Empty(resolved!.Programs); // mapped roles remain transient to the validated federated login result.
    }

    [Fact]
    public async Task Missing_binding_disabled_binding_and_wrong_issuer_fail_closed_and_are_audited()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management", "FMS");
        var user = new UserAccount("federated.user", "Federated User", "federated@example.test", IdentityService.HashPassword("Temporary!123"), now);
        fixture.Db.Programs.Add(program);
        fixture.Db.UserAccounts.Add(user);
        await fixture.Db.SaveChangesAsync();
        var administration = new ExternalIdentityAdministrationService(fixture.Db);
        var provider = await administration.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "local", now, default);
        await administration.CreateMappingAsync(provider.Id, "fms-reviewers", program.Id, ProgramRole.Reviewer, "admin", "local", now, default);
        var runtime = new FederatedIdentityRuntimeService(fixture.Db, administration);

        Assert.Null(await runtime.CreateSessionAsync(new(provider.Id, provider.Issuer, "missing", ["fms-reviewers"]), "local", "test", now, default));
        var binding = await runtime.BindAsync(provider.Id, "subject-123", user.Id, "admin", "local", now, default);
        Assert.Null(await runtime.CreateSessionAsync(new(provider.Id, "https://wrong.example.test", "subject-123", ["fms-reviewers"]), "local", "test", now, default));
        Assert.True(await runtime.SetBindingEnabledAsync(binding.Id, false, "admin", "local", now.AddMinutes(1), default));
        Assert.Null(await runtime.CreateSessionAsync(new(provider.Id, provider.Issuer, "subject-123", ["fms-reviewers"]), "local", "test", now.AddMinutes(2), default));

        var denied = await fixture.Db.SecurityAuditEvents.AsNoTracking().CountAsync(x => x.EventType == "FederatedLogin" && x.Outcome == "Denied");
        Assert.Equal(3, denied);
    }

    private sealed class TestDatabase(SqliteConnection connection, AeroLinkDbContext db) : IAsyncDisposable
    {
        public AeroLinkDbContext Db { get; } = db;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE external_identity_providers (
                    id TEXT NOT NULL PRIMARY KEY, key TEXT NOT NULL, display_name TEXT NOT NULL, protocol TEXT NOT NULL,
                    issuer TEXT NOT NULL, subject_claim TEXT NOT NULL, group_claim TEXT NOT NULL, enabled INTEGER NOT NULL,
                    created_by TEXT NOT NULL, created_at TEXT NOT NULL, disabled_at TEXT NULL);
                CREATE UNIQUE INDEX ux_external_identity_providers_key ON external_identity_providers(key);
                CREATE UNIQUE INDEX ux_external_identity_providers_issuer ON external_identity_providers(issuer);
                CREATE TABLE external_group_role_mappings (
                    id TEXT NOT NULL PRIMARY KEY, provider_id TEXT NOT NULL, external_group TEXT NOT NULL, program_id TEXT NOT NULL,
                    role TEXT NOT NULL, enabled INTEGER NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL, disabled_at TEXT NULL,
                    FOREIGN KEY(provider_id) REFERENCES external_identity_providers(id), FOREIGN KEY(program_id) REFERENCES programs(Id));
                CREATE UNIQUE INDEX ux_external_group_role_mappings_authority ON external_group_role_mappings(provider_id,external_group,program_id,role);
                CREATE TABLE external_identity_account_bindings (
                    id TEXT NOT NULL PRIMARY KEY, provider_id TEXT NOT NULL, external_subject TEXT NOT NULL, user_id TEXT NOT NULL,
                    created_by TEXT NOT NULL, created_at TEXT NOT NULL, last_authenticated_at TEXT NULL, disabled_at TEXT NULL,
                    FOREIGN KEY(provider_id) REFERENCES external_identity_providers(id), FOREIGN KEY(user_id) REFERENCES user_accounts(Id));
                CREATE UNIQUE INDEX ux_external_identity_account_bindings_subject ON external_identity_account_bindings(provider_id,external_subject);
                CREATE UNIQUE INDEX ux_external_identity_account_bindings_user ON external_identity_account_bindings(provider_id,user_id);
                """);
            return new(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
