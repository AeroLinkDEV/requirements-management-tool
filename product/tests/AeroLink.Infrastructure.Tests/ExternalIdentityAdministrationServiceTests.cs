using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ExternalIdentityAdministrationServiceTests
{
    [Fact]
    public async Task Provider_and_mapping_persist_resolve_and_fail_closed_when_disabled()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management", "FMS");
        fixture.Db.Programs.Add(program);
        await fixture.Db.SaveChangesAsync();

        var service = new ExternalIdentityAdministrationService(fixture.Db);
        var provider = await service.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "127.0.0.1", now, default);
        var mapping = await service.CreateMappingAsync(provider.Id, "FMS-APPROVERS", program.Id, ProgramRole.Approver,
            "admin", "127.0.0.1", now, default);

        fixture.Db.ChangeTracker.Clear();
        Assert.Single(await service.ListProvidersAsync(default));
        Assert.Single(await service.ListMappingsAsync(provider.Id, program.Id, default));
        Assert.Equal([ProgramRole.Approver], await service.ResolveRolesAsync(provider.Id, provider.Issuer, [" fms-approvers "], program.Id, default));
        Assert.Empty(await service.ResolveRolesAsync(provider.Id, "https://wrong.example.test", ["fms-approvers"], program.Id, default));
        Assert.Empty(await service.ResolveRolesAsync(provider.Id, provider.Issuer, ["fms-approvers"], Guid.NewGuid(), default));

        Assert.True(await service.SetMappingEnabledAsync(mapping.Id, false, "admin", "127.0.0.1", now.AddMinutes(1), default));
        Assert.Empty(await service.ResolveRolesAsync(provider.Id, provider.Issuer, ["fms-approvers"], program.Id, default));
        Assert.True(await service.SetProviderEnabledAsync(provider.Id, false, "admin", "127.0.0.1", now.AddMinutes(2), default));
        Assert.Empty(await service.ResolveRolesAsync(provider.Id, provider.Issuer, ["fms-approvers"], program.Id, default));

        var auditTypes = await fixture.Db.SecurityAuditEvents.Select(x => x.EventType).ToListAsync();
        Assert.Contains("ExternalIdentityProviderCreated", auditTypes);
        Assert.Contains("ExternalGroupRoleMappingCreated", auditTypes);
        Assert.Contains("ExternalGroupRoleMappingDisabled", auditTypes);
        Assert.Contains("ExternalIdentityProviderDisabled", auditTypes);
    }

    [Fact]
    public async Task Database_constraints_reject_duplicate_provider_and_mapping_authority()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management", "FMS");
        fixture.Db.Programs.Add(program);
        await fixture.Db.SaveChangesAsync();
        var service = new ExternalIdentityAdministrationService(fixture.Db);

        var provider = await service.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "local", now, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProviderAsync("entra", "Duplicate", ExternalIdentityProtocol.OpenIdConnect,
            "https://other.example.test", "sub", "groups", "admin", "local", now, default));

        await service.CreateMappingAsync(provider.Id, "fms-reviewers", program.Id, ProgramRole.Reviewer, "admin", "local", now, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateMappingAsync(provider.Id, " FMS-REVIEWERS ", program.Id, ProgramRole.Reviewer, "admin", "local", now, default));
    }

    private sealed class TestDatabase(SqliteConnection connection, AeroLinkDbContext db) : IAsyncDisposable
    {
        public AeroLinkDbContext Db { get; } = db;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            var db = new AeroLinkDbContext(options);
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
                """);
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
