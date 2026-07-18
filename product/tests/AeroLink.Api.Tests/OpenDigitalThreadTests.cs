using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class OpenDigitalThreadTests
{
    [Fact]
    public async Task Browser_mutations_require_a_session_bound_token()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();await BootstrapAndLoginAsync(client);var projectId=await CreateProjectAsync(factory);
        client.DefaultRequestHeaders.Add("Origin","https://portal.example.test");
        using var denied=await client.PostAsJsonAsync("/api/integrations/events/test",new{projectId,message="unprotected"});
        Assert.Equal(HttpStatusCode.BadRequest,denied.StatusCode);var problem=await denied.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("antiforgery_validation_failed",problem.GetProperty("code").GetString());
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var accepted=await client.PostAsJsonAsync("/api/integrations/events/test",new{projectId,message="protected"});Assert.Equal(HttpStatusCode.Accepted,accepted.StatusCode);
    }

    [Fact]
    public async Task Scoped_service_identity_reads_v1_requirements_and_cannot_exceed_scope()
    {
        using var factory=new AeroLinkApiFactory();using var administrator=factory.CreateClient();await BootstrapAndLoginAsync(administrator);await SecurityBoundaryTests.AuthorizeMutationsAsync(administrator);var projectId=await CreateProjectAsync(factory);
        using var created=await administrator.PostAsJsonAsync("/api/integrations/service-identities",new{projectId,name="Verification pipeline",scopes=new[]{"requirements:read"}});Assert.Equal(HttpStatusCode.Created,created.StatusCode);var credential=await created.Content.ReadFromJsonAsync<JsonElement>();
        using var service=factory.CreateClient();service.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",credential.GetProperty("apiKey").GetString());
        using var requirements=await service.GetAsync($"/api/v1/requirements?projectId={projectId}&pageSize=25");Assert.Equal(HttpStatusCode.OK,requirements.StatusCode);Assert.True(requirements.Headers.ETag is not null);
        using var cursorPage=await service.GetAsync($"/api/v1/requirements?projectId={projectId}&pageSize=25&cursor=SYSR-00000000");Assert.Equal(HttpStatusCode.OK,cursorPage.StatusCode);
        using var forbidden=await service.PostAsJsonAsync("/api/v1/events",new{eventType="external.build.completed",aggregateType="Build",aggregateId=Guid.NewGuid(),data=new{build="1.0.0"}});Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
    }

    [Fact]
    public async Task Service_event_ingestion_is_idempotent_and_enters_the_transactional_outbox()
    {
        using var factory=new AeroLinkApiFactory();using var administrator=factory.CreateClient();await BootstrapAndLoginAsync(administrator);await SecurityBoundaryTests.AuthorizeMutationsAsync(administrator);var projectId=await CreateProjectAsync(factory);
        using var created=await administrator.PostAsJsonAsync("/api/integrations/service-identities",new{projectId,name="Build gateway",scopes=new[]{"events:write"}});var credential=await created.Content.ReadFromJsonAsync<JsonElement>();
        using var service=factory.CreateClient();service.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",credential.GetProperty("apiKey").GetString());service.DefaultRequestHeaders.Add("Idempotency-Key","build-event-00000001");var aggregateId=Guid.NewGuid();
        using var first=await service.PostAsJsonAsync("/api/v1/events",new{eventType="external.build.completed",aggregateType="Build",aggregateId,data=new{buildNumber="FMS-2.0.17"}});using var second=await service.PostAsJsonAsync("/api/v1/events",new{eventType="external.build.completed",aggregateType="Build",aggregateId,data=new{buildNumber="FMS-2.0.17"}});
        Assert.Equal(HttpStatusCode.Accepted,first.StatusCode);Assert.Equal(HttpStatusCode.OK,second.StatusCode);var firstBody=await first.Content.ReadFromJsonAsync<JsonElement>();var secondBody=await second.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(firstBody.GetProperty("id").GetGuid(),secondBody.GetProperty("id").GetGuid());Assert.True(secondBody.GetProperty("idempotentReplay").GetBoolean());
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();Assert.Single(db.IntegrationEvents.Where(x=>x.ProjectId==projectId));
    }

    [Fact]
    public async Task Readiness_reports_database_connectivity()
    {using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();using var response=await client.GetAsync("/health/ready");Assert.Equal(HttpStatusCode.OK,response.StatusCode);var body=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("connected",body.GetProperty("database").GetString());}

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap=new HttpRequestMessage(HttpMethod.Post,"/api/setup/bootstrap"){Content=JsonContent.Create(new{displayName="AeroLink Administrator",email="admin@example.test",password=AeroLinkApiFactory.AdministratorPassword})};bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret",AeroLinkApiFactory.BootstrapSecret);using var created=await client.SendAsync(bootstrap);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password=AeroLinkApiFactory.AdministratorPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);
    }

    private static async Task<Guid> CreateProjectAsync(AeroLinkApiFactory factory)
    {using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var program=new ProgramRecord("Connected Program","CONN");var project=new ProjectRecord(program.Id,"Connected Product","Connected Product");db.AddRange(program,project);await db.SaveChangesAsync();return project.Id;}
}
