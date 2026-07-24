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
        Assert.Equal([ProgramRole.Approver], await ResolveAsync(service, provider.Id, provider.Issuer, [" fms-approvers "], program.Id, now));
        Assert.Empty(await ResolveAsync(service, provider.Id, "https://wrong.example.test", ["fms-approvers"], program.Id, now));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, ["fms-approvers"], Guid.NewGuid(), now));

        Assert.True(await service.SetMappingEnabledAsync(mapping.Id, false, "admin", "127.0.0.1", now.AddMinutes(1), default));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, ["fms-approvers"], program.Id, now));
        Assert.True(await service.SetProviderEnabledAsync(provider.Id, false, "admin", "127.0.0.1", now.AddMinutes(2), default));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, ["fms-approvers"], program.Id, now));

        var auditTypes = await fixture.Db.SecurityAuditEvents.Select(x => x.EventType).ToListAsync();
        Assert.Contains("ExternalIdentityProviderCreated", auditTypes);
        Assert.Contains("ExternalGroupRoleMappingCreated", auditTypes);
        Assert.Contains("ExternalGroupRoleMappingDisabled", auditTypes);
        Assert.Contains("ExternalIdentityProviderDisabled", auditTypes);
    }

    [Fact]
    public async Task Database_constraints_reject_duplicates_and_record_denied_audit_events()
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
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateMappingAsync(Guid.NewGuid(), "missing-provider", program.Id, ProgramRole.Engineer, "admin", "local", now, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateMappingAsync(provider.Id, "missing-program", Guid.NewGuid(), ProgramRole.Engineer, "admin", "local", now, default));
        Assert.False(await service.SetProviderEnabledAsync(Guid.NewGuid(), false, "admin", "local", now, default));
        Assert.False(await service.SetMappingEnabledAsync(Guid.NewGuid(), false, "admin", "local", now, default));

        var denied = await fixture.Db.SecurityAuditEvents.AsNoTracking().Where(x => x.Outcome == "Denied").Select(x => x.EventType).ToListAsync();
        Assert.Contains("ExternalIdentityProviderCreateRejected", denied);
        Assert.Contains("ExternalGroupRoleMappingCreateRejected", denied);
        Assert.Contains("ExternalIdentityProviderStateChangeRejected", denied);
        Assert.Contains("ExternalGroupRoleMappingStateChangeRejected", denied);
    }

    [Fact]
    public async Task Issuer_uniqueness_survives_case_and_default_port_variation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var service = new ExternalIdentityAdministrationService(fixture.Db);

        var provider = await service.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://Login.Example.Test/tenant/", "sub", "groups", "admin", "local", now, default);
        Assert.Equal("https://login.example.test/tenant", provider.Issuer);

        // The same anchor written in a different but equivalent form must not become a second trusted provider.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProviderAsync("entra-two", "Duplicate Anchor",
            ExternalIdentityProtocol.OpenIdConnect, "HTTPS://LOGIN.EXAMPLE.TEST:443/tenant", "sub", "groups", "admin", "local", now, default));

        // A path that differs only in case is a different issuer under RFC 3986 and stays distinct.
        var distinct = await service.CreateProviderAsync("entra-three", "Distinct Path", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/TENANT", "sub", "groups", "admin", "local", now, default);
        Assert.Equal("https://login.example.test/TENANT", distinct.Issuer);
    }

    [Fact]
    public async Task Over_long_and_malformed_input_is_rejected_as_validation_not_conflict()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var service = new ExternalIdentityAdministrationService(fixture.Db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProviderAsync(new string('k', 101), "Too Long Key",
            ExternalIdentityProtocol.OpenIdConnect, "https://login.example.test", "sub", "groups", "admin", "local", now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProviderAsync("entra", new string('d', 201),
            ExternalIdentityProtocol.OpenIdConnect, "https://login.example.test", "sub", "groups", "admin", "local", now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProviderAsync("entra", "Query Issuer",
            ExternalIdentityProtocol.OpenIdConnect, "https://login.example.test/tenant?probe=1", "sub", "groups", "admin", "local", now, default));

        var denied = await fixture.Db.SecurityAuditEvents.AsNoTracking().Where(x => x.Outcome == "Denied").Select(x => x.Detail).ToListAsync();
        Assert.Equal(3, denied.Count);
        Assert.All(denied, detail => Assert.StartsWith("Validation:", detail));
        Assert.Empty(await service.ListProvidersAsync(default));
    }

    [Fact]
    public async Task Role_resolution_records_evidence_for_grants_and_refusals()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management", "FMS");
        fixture.Db.Programs.Add(program);
        await fixture.Db.SaveChangesAsync();
        var service = new ExternalIdentityAdministrationService(fixture.Db);
        var provider = await service.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "local", now, default);
        await service.CreateMappingAsync(provider.Id, "fms-approvers", program.Id, ProgramRole.Approver, "admin", "local", now, default);

        Assert.Equal([ProgramRole.Approver], await ResolveAsync(service, provider.Id, provider.Issuer, ["fms-approvers"], program.Id, now));
        Assert.Empty(await ResolveAsync(service, Guid.NewGuid(), provider.Issuer, ["fms-approvers"], program.Id, now));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, ["   ", ""], program.Id, now));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, null, program.Id, now));
        Assert.Empty(await ResolveAsync(service, provider.Id, provider.Issuer, ["unmapped-group"], program.Id, now));

        var events = await fixture.Db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType.StartsWith("ExternalIdentityRole")).Select(x => new { x.EventType, x.Outcome, x.Detail }).ToListAsync();
        Assert.Equal(2, events.Count(x => x.EventType == "ExternalIdentityRolesResolved"));
        Assert.Single(events, x => x.Outcome == "Success" && x.Detail.Contains("Approver"));
        Assert.Equal(3, events.Count(x => x.EventType == "ExternalIdentityRoleResolutionDenied"));
        Assert.Contains(events, x => x.Detail.Contains("identity provider was not found"));
        Assert.Contains(events, x => x.Detail.Contains("No usable directory group"));
    }

    [Fact]
    public async Task Redundant_state_change_is_idempotent_and_adds_no_evidence()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var service = new ExternalIdentityAdministrationService(fixture.Db);
        var provider = await service.CreateProviderAsync("entra", "Corporate Entra", ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant", "sub", "groups", "admin", "local", now, default);

        Assert.True(await service.SetProviderEnabledAsync(provider.Id, true, "admin", "local", now.AddMinutes(1), default));
        Assert.Empty(await fixture.Db.SecurityAuditEvents.AsNoTracking().Where(x => x.EventType == "ExternalIdentityProviderEnabled").ToListAsync());

        Assert.True(await service.SetProviderEnabledAsync(provider.Id, false, "admin", "local", now.AddMinutes(2), default));
        Assert.True(await service.SetProviderEnabledAsync(provider.Id, true, "admin", "local", now.AddMinutes(3), default));
        var reloaded = Assert.Single(await service.ListProvidersAsync(default));
        Assert.True(reloaded.Enabled);
        Assert.Null(reloaded.DisabledAt);
    }

    private static Task<IReadOnlyList<ProgramRole>> ResolveAsync(ExternalIdentityAdministrationService service,
        Guid providerId, string? issuer, IEnumerable<string>? groups, Guid programId, DateTimeOffset now)
        => service.ResolveRolesAsync(providerId, issuer, groups, programId, "admin", "local", now, default);

    private sealed class TestDatabase(SqliteConnection connection, AeroLinkDbContext db) : IAsyncDisposable
    {
        public AeroLinkDbContext Db { get; } = db;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            var db = new AeroLinkDbContext(options);
            // The external identity tables are part of the EF model, so the schema under test is the one the
            // product ships rather than a fixture-local approximation of it.
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
