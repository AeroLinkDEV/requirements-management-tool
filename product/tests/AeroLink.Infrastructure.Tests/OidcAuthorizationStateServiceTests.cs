using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class OidcAuthorizationStateServiceTests
{
    [Fact]
    public async Task State_is_signed_expiring_single_use_and_carries_pkce_verifier()
    {
        await using var fixture=await TestDatabase.CreateAsync();
        var now=DateTimeOffset.UtcNow;
        var providerId=Guid.NewGuid();
        await fixture.Db.Database.ExecuteSqlRawAsync("INSERT INTO external_identity_providers (id,key,display_name,protocol,issuer,subject_claim,group_claim,enabled,created_by,created_at,disabled_at) VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},NULL)",providerId,"entra","Entra","OpenIdConnect","https://login.example.test/tenant","sub","groups",true,"admin",now);
        var service=new OidcAuthorizationStateService(fixture.Db,new EphemeralDataProtectionProvider());

        var created=await service.CreateAsync(providerId,"/requirements?project=1",now,default);
        Assert.Equal(OidcAuthorizationStateService.PkceChallenge(created.CodeVerifier),created.CodeChallenge);
        Assert.NotEqual(created.Nonce,created.CodeVerifier);

        var consumed=await service.ConsumeAsync(created.State,created.Nonce,now.AddMinutes(1),default);
        Assert.NotNull(consumed);
        Assert.Equal(providerId,consumed!.ProviderId);
        Assert.Equal(created.CodeVerifier,consumed.CodeVerifier);
        Assert.Equal("/requirements?project=1",consumed.ReturnPath);
        Assert.Null(await service.ConsumeAsync(created.State,created.Nonce,now.AddMinutes(2),default));
    }

    [Fact]
    public async Task Tampered_expired_wrong_nonce_and_external_return_urls_fail_closed()
    {
        await using var fixture=await TestDatabase.CreateAsync();
        var now=DateTimeOffset.UtcNow;
        var providerId=Guid.NewGuid();
        await fixture.Db.Database.ExecuteSqlRawAsync("INSERT INTO external_identity_providers (id,key,display_name,protocol,issuer,subject_claim,group_claim,enabled,created_by,created_at,disabled_at) VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},NULL)",providerId,"entra","Entra","OpenIdConnect","https://login.example.test/tenant","sub","groups",true,"admin",now);
        var service=new OidcAuthorizationStateService(fixture.Db,new EphemeralDataProtectionProvider());

        var wrongNonce=await service.CreateAsync(providerId,"https://evil.example.test",now,default);
        Assert.Equal("/",wrongNonce.ReturnPath);
        Assert.Null(await service.ConsumeAsync(wrongNonce.State,"wrong",now.AddMinutes(1),default));

        var expired=await service.CreateAsync(providerId,"//evil.example.test",now,default);
        Assert.Null(await service.ConsumeAsync(expired.State,expired.Nonce,now.AddMinutes(11),default));

        var tampered=await service.CreateAsync(providerId,"/",now,default);
        Assert.Null(await service.ConsumeAsync(tampered.State+"x",tampered.Nonce,now.AddMinutes(1),default));
    }

    private sealed class TestDatabase(SqliteConnection connection,AeroLinkDbContext db):IAsyncDisposable
    {
        public AeroLinkDbContext Db{get;}=db;
        public static async Task<TestDatabase>CreateAsync()
        {
            var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
            var db=new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE external_identity_providers (
                    id TEXT NOT NULL PRIMARY KEY,key TEXT NOT NULL,display_name TEXT NOT NULL,protocol TEXT NOT NULL,
                    issuer TEXT NOT NULL,subject_claim TEXT NOT NULL,group_claim TEXT NOT NULL,enabled INTEGER NOT NULL,
                    created_by TEXT NOT NULL,created_at TEXT NOT NULL,disabled_at TEXT NULL);
                CREATE TABLE oidc_authentication_attempts (
                    id TEXT NOT NULL PRIMARY KEY,provider_id TEXT NOT NULL,state_hash TEXT NOT NULL,nonce_hash TEXT NOT NULL,
                    code_verifier_protected TEXT NOT NULL,return_path TEXT NOT NULL,created_at TEXT NOT NULL,expires_at TEXT NOT NULL,consumed_at TEXT NULL,
                    FOREIGN KEY(provider_id) REFERENCES external_identity_providers(id));
                CREATE UNIQUE INDEX ux_oidc_authentication_attempts_state ON oidc_authentication_attempts(state_hash);
                """);
            return new(connection,db);
        }
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();await connection.DisposeAsync();}
    }
}
