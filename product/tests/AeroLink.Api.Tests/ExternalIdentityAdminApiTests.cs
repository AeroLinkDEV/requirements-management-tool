using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ExternalIdentityAdminApiTests
{
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
    }
}