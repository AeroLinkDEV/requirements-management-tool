using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class IntegrationCompletionApiTests
{
    [Fact]
    public async Task Connector_checkpoints_replay_mapping_history_and_oslc_consumption_are_durable()
    {
        using var factory=new AeroLinkApiFactory();using var administrator=factory.CreateClient();await BootstrapAndLoginAsync(administrator);await SecurityBoundaryTests.AuthorizeMutationsAsync(administrator);var seeded=await CreateProjectAsync(factory);

        using var mappingResponse=await administrator.PostAsJsonAsync("/api/integrations/mappings",new{projectId=seeded.ProjectId,name="OSLC default",mapping=new{identifier="dcterms:identifier",statement="dcterms:description"}});Assert.Equal(HttpStatusCode.Created,mappingResponse.StatusCode);var mapping=await mappingResponse.Content.ReadFromJsonAsync<JsonElement>();var mappingId=mapping.GetProperty("id").GetGuid();
        using var history=await administrator.GetAsync($"/api/integrations/mappings/{mappingId}/history");Assert.Equal(HttpStatusCode.OK,history.StatusCode);var historyBody=await history.Content.ReadFromJsonAsync<JsonElement>();Assert.Single(historyBody.GetProperty("revisions").EnumerateArray());

        using var connectorResponse=await administrator.PostAsJsonAsync("/api/integrations/connectors",new{projectId=seeded.ProjectId,name="Supplier RM",kind="OSLC RM",baseUrl="https://supplier.example.test/rm",configurationContext="stream:flight-control",mappingName="OSLC default",staleAfterMinutes=60});Assert.Equal(HttpStatusCode.Created,connectorResponse.StatusCode);var connector=await connectorResponse.Content.ReadFromJsonAsync<JsonElement>();var connectorId=connector.GetProperty("connectorId").GetGuid();
        using var checkpointResponse=await administrator.PostAsJsonAsync($"/api/integrations/connectors/{connectorId}/checkpoints",new{projectId=seeded.ProjectId,cursor="page-17",operation="query",itemCount=17,sourceHash="abc123",mappingName="OSLC default",mappingVersion=1,configurationContext="stream:flight-control",succeeded=false,error="supplier unavailable"});Assert.Equal(HttpStatusCode.Accepted,checkpointResponse.StatusCode);var checkpoint=await checkpointResponse.Content.ReadFromJsonAsync<JsonElement>();var checkpointId=checkpoint.GetProperty("id").GetGuid();
        using var replay=await administrator.PostAsJsonAsync($"/api/integrations/checkpoints/{checkpointId}/replay",new{});Assert.Equal(HttpStatusCode.Accepted,replay.StatusCode);

        using var credentialResponse=await administrator.PostAsJsonAsync("/api/integrations/service-identities",new{projectId=seeded.ProjectId,name="Supplier OSLC gateway",scopes=new[]{"oslc:write","integrations:read"}});Assert.Equal(HttpStatusCode.Created,credentialResponse.StatusCode);var credential=await credentialResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var service=factory.CreateClient();service.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",credential.GetProperty("apiKey").GetString());service.DefaultRequestHeaders.Add("Idempotency-Key","oslc-consume-00000001");var consumeRequest=new{targetReleaseId=seeded.ReleaseId,mappingId,sourceUri="https://supplier.example.test/rm/requirements/123",sourceEtag="\"remote-v4\"",externalIdentifier="SUP-123",localIdentifier="SYSR-00990001",level="System",statement="The system shall retain supplier state.",rationale="Supplier allocation",verificationMethod="Test",configurationContext="stream:flight-control",title="Consume supplier requirement",analysis="Mapped through the approved OSLC profile.",solution="Create a governed requirement proposal."};
        using var first=await service.PostAsJsonAsync("/api/v1/oslc/rm/consume",consumeRequest);using var second=await service.PostAsJsonAsync("/api/v1/oslc/rm/consume",consumeRequest);Assert.Equal(HttpStatusCode.Accepted,first.StatusCode);Assert.Equal(HttpStatusCode.OK,second.StatusCode);var firstBody=await first.Content.ReadFromJsonAsync<JsonElement>();var secondBody=await second.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(firstBody.GetProperty("scrId").GetGuid(),secondBody.GetProperty("scrId").GetGuid());Assert.True(secondBody.GetProperty("idempotentReplay").GetBoolean());

        using var health=await service.GetAsync("/api/v1/integrations/health");Assert.Equal(HttpStatusCode.OK,health.StatusCode);var healthBody=await health.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("attention",healthBody.GetProperty("status").GetString());Assert.Single(healthBody.GetProperty("connectors").EnumerateArray());
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap=new HttpRequestMessage(HttpMethod.Post,"/api/setup/bootstrap"){Content=JsonContent.Create(new{displayName="AeroLink Administrator",email="admin@example.test",password=AeroLinkApiFactory.AdministratorPassword})};bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret",AeroLinkApiFactory.BootstrapSecret);using var created=await client.SendAsync(bootstrap);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password=AeroLinkApiFactory.AdministratorPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);
    }

    private static async Task<(Guid ProjectId,Guid ReleaseId)> CreateProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var program=new ProgramRecord("Integration Completion Program","ICP");var project=new ProjectRecord(program.Id,"Integrated Product","Integrated Product");var release=new SoftwareRelease(project.Id,"1.0",false);db.AddRange(program,project,release);await db.SaveChangesAsync();return(project.Id,release.Id);
    }
}
