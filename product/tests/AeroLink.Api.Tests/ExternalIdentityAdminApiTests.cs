using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ExternalIdentityAdminApiTests
{
    [Fact]
    public async Task Administrator_can_administer_external_identity_against_the_shipped_schema()
    {
        using var factory=new AeroLinkApiFactory();
        using var client=factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        Guid programId;
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program=new ProgramRecord("Flight Management","FMS");
            db.Programs.Add(program);
            await db.SaveChangesAsync();
            programId=program.Id;
        }

        using var created=await client.PostAsJsonAsync("/api/admin/external-identity/providers",new
        {
            key="Corporate-Entra",
            displayName="Corporate Entra ID",
            protocol="OpenIdConnect",
            issuer="https://Login.Example.Test/tenant/",
            subjectClaim="sub",
            groupClaim="groups"
        });
        Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var provider=await created.Content.ReadFromJsonAsync<JsonElement>();
        var providerId=provider.GetProperty("id").GetGuid();
        Assert.Equal("corporate-entra",provider.GetProperty("key").GetString());
        Assert.Equal("https://login.example.test/tenant",provider.GetProperty("issuer").GetString());
        Assert.True(provider.GetProperty("enabled").GetBoolean());
        Assert.Equal("admin",provider.GetProperty("createdBy").GetString());

        using var duplicate=await client.PostAsJsonAsync("/api/admin/external-identity/providers",new
        {
            key="corporate-entra",displayName="Duplicate",protocol="OpenIdConnect",
            issuer="https://second.example.test",subjectClaim="sub",groupClaim="groups"
        });
        Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);

        using var invalid=await client.PostAsJsonAsync("/api/admin/external-identity/providers",new
        {
            key=new string('k',101),displayName="Too Long",protocol="OpenIdConnect",
            issuer="https://third.example.test",subjectClaim="sub",groupClaim="groups"
        });
        Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);

        using var mapped=await client.PostAsJsonAsync("/api/admin/external-identity/mappings",new
        {
            providerId,externalGroup="FMS-Approvers",programId,role="Approver"
        });
        Assert.Equal(HttpStatusCode.Created,mapped.StatusCode);
        var mapping=await mapped.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fms-approvers",mapping.GetProperty("externalGroup").GetString());

        using var unknownProgram=await client.PostAsJsonAsync("/api/admin/external-identity/mappings",new
        {
            providerId,externalGroup="fms-engineers",programId=Guid.NewGuid(),role="Engineer"
        });
        Assert.Equal(HttpStatusCode.NotFound,unknownProgram.StatusCode);

        var providers=await client.GetFromJsonAsync<JsonElement>("/api/admin/external-identity/providers");
        Assert.Equal(1,providers.GetArrayLength());
        var mappings=await client.GetFromJsonAsync<JsonElement>($"/api/admin/external-identity/mappings?providerId={providerId}&programId={programId}");
        Assert.Equal(1,mappings.GetArrayLength());

        Assert.Equal(["Approver"],await ResolveAsync(client,providerId,"https://login.example.test/tenant",["FMS-Approvers"],programId));
        Assert.Empty(await ResolveAsync(client,providerId,"https://attacker.example.test",["FMS-Approvers"],programId));

        using var disabled=await client.PostAsJsonAsync($"/api/admin/external-identity/providers/{providerId}/enabled",new{enabled=false});
        Assert.Equal(HttpStatusCode.NoContent,disabled.StatusCode);
        Assert.Empty(await ResolveAsync(client,providerId,"https://login.example.test/tenant",["FMS-Approvers"],programId));

        using var missing=await client.PostAsJsonAsync($"/api/admin/external-identity/providers/{Guid.NewGuid()}/enabled",new{enabled=false});
        Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);

        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(1,await db.ExternalIdentityProviders.CountAsync());
            Assert.Equal(1,await db.ExternalGroupRoleMappings.CountAsync());
            var events=await db.SecurityAuditEvents.AsNoTracking().Where(x=>x.EventType.StartsWith("External")).ToListAsync();
            Assert.Contains(events,x=>x.EventType=="ExternalIdentityProviderCreated"&&x.Outcome=="Success"&&x.ActorId=="admin");
            Assert.Contains(events,x=>x.EventType=="ExternalGroupRoleMappingCreated"&&x.Outcome=="Success");
            Assert.Contains(events,x=>x.EventType=="ExternalIdentityProviderDisabled"&&x.Outcome=="Success");
            Assert.Contains(events,x=>x.EventType=="ExternalIdentityProviderCreateRejected"&&x.Outcome=="Denied");
            Assert.Contains(events,x=>x.EventType=="ExternalIdentityRolesResolved"&&x.Outcome=="Success");
            Assert.Contains(events,x=>x.EventType=="ExternalIdentityRoleResolutionDenied"&&x.Outcome=="Denied");
            Assert.DoesNotContain(events,x=>x.Detail.Contains("password",StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Non_administrator_is_forbidden_from_external_identity_administration()
    {
        using var factory=new AeroLinkApiFactory();
        using var client=factory.CreateClient();
        var now=DateTimeOffset.UtcNow;

        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.UserAccounts.Add(new UserAccount("identity.viewer","Identity Viewer","identity.viewer@example.test",IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword),now));
            await db.SaveChangesAsync();
        }

        using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="identity.viewer",password=AeroLinkApiFactory.MemberPassword});
        Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using var providers=await client.GetAsync("/api/admin/external-identity/providers");
        using var mappings=await client.GetAsync("/api/admin/external-identity/mappings");
        using var createProvider=await client.PostAsJsonAsync("/api/admin/external-identity/providers",new
        {
            key="blocked-provider",
            displayName="Blocked Provider",
            protocol="OpenIdConnect",
            issuer="https://blocked.example.test",
            subjectClaim="sub",
            groupClaim="groups"
        });
        using var resolve=await client.PostAsJsonAsync("/api/admin/external-identity/resolve",new
        {
            providerId=Guid.NewGuid(),
            issuer="https://blocked.example.test",
            externalGroups=new[]{"blocked-group"},
            programId=Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.Forbidden,providers.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,mappings.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,createProvider.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,resolve.StatusCode);

        using var scope2=factory.Services.CreateScope();
        var db2=scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db2.ExternalIdentityProviders.ToListAsync());
    }

    private static async Task<string[]> ResolveAsync(HttpClient client,Guid providerId,string issuer,string[] groups,Guid programId)
    {
        using var response=await client.PostAsJsonAsync("/api/admin/external-identity/resolve",new{providerId,issuer,externalGroups=groups,programId});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var payload=await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("roles").EnumerateArray().Select(x=>x.GetString()!).ToArray();
    }
}
