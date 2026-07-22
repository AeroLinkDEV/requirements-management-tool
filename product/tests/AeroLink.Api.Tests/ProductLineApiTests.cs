using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProductLineApiTests
{
    [Fact]
    public async Task Parallel_streams_merge_deterministically_and_produce_immutable_variant_baseline()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();await BootstrapAsync(client);Guid projectId;
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var program=new ProgramRecord("Product Line Test","PLT");var project=new ProjectRecord(program.Id,"Reusable FMS","Flight management software");db.AddRange(program,project);await db.SaveChangesAsync();projectId=project.Id;}
        var component=await Post(client,"/api/product-line/components",new{projectId,componentNumber="COMP-00001",name="Guidance core",description="Reusable guidance behavior."});var componentId=component.GetProperty("id").GetGuid();
        var main=await Post(client,$"/api/product-line/components/{componentId}/streams",new{streamKey="MAIN",name="Main"});var mainId=main.GetProperty("id").GetGuid();
        var parallel=await Post(client,$"/api/product-line/components/{componentId}/streams",new{streamKey="NEXT",name="Next"});var parallelId=parallel.GetProperty("id").GetGuid();
        var baseline=await Post(client,$"/api/product-line/streams/{mainId}/revisions",new{contentJson="{\"guidance\":1,\"display\":1}"});var baseId=baseline.GetProperty("id").GetGuid();
        var source=await Post(client,$"/api/product-line/streams/{parallelId}/revisions",new{contentJson="{\"guidance\":2,\"display\":1}"});var sourceId=source.GetProperty("id").GetGuid();
        var target=await Post(client,$"/api/product-line/streams/{mainId}/revisions",new{contentJson="{\"guidance\":1,\"display\":2}"});var targetId=target.GetProperty("id").GetGuid();
        var change=await Post(client,"/api/product-line/change-sets",new{baseRevisionId=baseId,sourceRevisionId=sourceId,targetRevisionId=targetId,title="Merge next guidance",description="Combine independent changes."});
        Assert.Equal("InWork",change.GetProperty("state").GetString());Assert.Equal("{\"display\":2,\"guidance\":2}",change.GetProperty("mergeResultJson").GetString());var changeId=change.GetProperty("id").GetGuid();
        var applied=await Post(client,$"/api/product-line/change-sets/{changeId}/apply",new{});var appliedRevision=applied.GetProperty("revision").GetProperty("id").GetGuid();
        await Post(client,$"/api/product-line/components/{componentId}/approve",new{});
        var variant=await Post(client,"/api/product-line/variants",new{projectId,variantKey="FMS-NG",name="FMS Next Generation",applicabilityJson="{\"aircraft\":\"NG\"}"});var variantId=variant.GetProperty("id").GetGuid();
        await Post(client,$"/api/product-line/variants/{variantId}/components",new{componentRevisionId=appliedRevision,applicabilityJson="{\"enabled\":true}"});
        await Post(client,$"/api/product-line/variants/{variantId}/propagation-decisions",new{componentRevisionId=appliedRevision,decision="Accept",rationale="Approved guidance is applicable to the NG configuration."});
        await Post(client,$"/api/product-line/variants/{variantId}/approve",new{});var frozen=await Post(client,$"/api/product-line/variants/{variantId}/baselines",new{});
        Assert.Equal(1,frozen.GetProperty("revision").GetInt32());Assert.Equal(64,frozen.GetProperty("manifestHash").GetString()!.Length);
        var manifest=await client.GetFromJsonAsync<JsonElement>($"/api/product-line/variants/{variantId}/manifest");Assert.Equal(frozen.GetProperty("manifestHash").GetString(),manifest.GetProperty("manifestHash").GetString());Assert.Equal(1,manifest.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Conflicting_stream_changes_require_attributable_resolution_before_apply()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();await BootstrapAsync(client);Guid projectId;
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var program=new ProgramRecord("Conflict Test","CFT");var project=new ProjectRecord(program.Id,"Conflict Project","FMS");db.AddRange(program,project);await db.SaveChangesAsync();projectId=project.Id;}
        var componentId=(await Post(client,"/api/product-line/components",new{projectId,componentNumber="COMP-00002",name="Conflict core",description=""})).GetProperty("id").GetGuid();
        var leftId=(await Post(client,$"/api/product-line/components/{componentId}/streams",new{streamKey="LEFT",name="Left"})).GetProperty("id").GetGuid();var rightId=(await Post(client,$"/api/product-line/components/{componentId}/streams",new{streamKey="RIGHT",name="Right"})).GetProperty("id").GetGuid();
        var baseId=(await Post(client,$"/api/product-line/streams/{leftId}/revisions",new{contentJson="{\"mode\":\"base\"}"})).GetProperty("id").GetGuid();var sourceId=(await Post(client,$"/api/product-line/streams/{rightId}/revisions",new{contentJson="{\"mode\":\"source\"}"})).GetProperty("id").GetGuid();var targetId=(await Post(client,$"/api/product-line/streams/{leftId}/revisions",new{contentJson="{\"mode\":\"target\"}"})).GetProperty("id").GetGuid();
        var change=await Post(client,"/api/product-line/change-sets",new{baseRevisionId=baseId,sourceRevisionId=sourceId,targetRevisionId=targetId,title="Resolve mode",description=""});Assert.Equal("Conflict",change.GetProperty("state").GetString());var id=change.GetProperty("id").GetGuid();
        using var blocked=await client.PostAsJsonAsync($"/api/product-line/change-sets/{id}/apply",new{});Assert.Equal(HttpStatusCode.Conflict,blocked.StatusCode);
        var resolved=await Post(client,$"/api/product-line/change-sets/{id}/resolve",new{resolutionJson="{\"mode\":\"resolved\"}",rationale="Systems and software owners selected the compatible behavior."});Assert.Equal("InWork",resolved.GetProperty("state").GetString());
        var applied=await Post(client,$"/api/product-line/change-sets/{id}/apply",new{});Assert.Equal("Closed",applied.GetProperty("changeSet").GetProperty("state").GetString());
    }

    private static async Task BootstrapAsync(HttpClient client)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"/api/setup/bootstrap"){Content=JsonContent.Create(new{displayName="Administrator",email="admin@example.test",password=AeroLinkApiFactory.AdministratorPassword})};request.Headers.Add("X-AeroLink-Bootstrap-Secret",AeroLinkApiFactory.BootstrapSecret);using var created=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,created.StatusCode);using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password=AeroLinkApiFactory.AdministratorPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);await SecurityBoundaryTests.AuthorizeMutationsAsync(client);}
    private static async Task<JsonElement> Post(HttpClient client,string url,object body){using var response=await client.PostAsJsonAsync(url,body);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{url} returned {(int)response.StatusCode}: {text}");return JsonDocument.Parse(text).RootElement.Clone();}
}
