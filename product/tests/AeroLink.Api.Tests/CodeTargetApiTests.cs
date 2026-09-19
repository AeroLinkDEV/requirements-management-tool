using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

[Collection(ShowcaseApiCollection.Name)]
public sealed class CodeTargetApiTests(ShowcaseApiFixture showcase)
{
    [Theory]
    [InlineData("RequirementRevision")]
    [InlineData("ChangeRequestRevision")]
    [InlineData("RequirementProposal")]
    [InlineData("ProblemReportRevision")]
    public async Task Paged_targets_preserve_exact_project_owned_identity(string kind)
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await ShowcaseApiFixture.LoginAdministratorAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var projectId = showcase.Summary.ProjectId;
        var baselineId = showcase.Summary.ReleasedBaselineId;
        var releaseId = await db.CandidateBaselines.Where(x => x.Id == baselineId).Select(x => x.ReleaseId).SingleAsync();
        var url = $"/api/projects/{projectId}/code/targets?releaseId={releaseId}&targetKind={kind}&pageSize=2";
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var first = await response.Content.ReadFromJsonAsync<JsonElement>();
        var repeated = await client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(first.GetRawText(), repeated.GetRawText());
        var identities = new HashSet<Guid>();
        foreach (var row in first.GetProperty("items").EnumerateArray())
        {
            var id = row.GetProperty("exactIdentityId").GetGuid();
            Assert.True(identities.Add(id));
            var exact = await CodeRelationshipTargetResolver.ResolveAsync(db, projectId,
                Enum.Parse<CodeRelationshipTargetKind>(kind), id, default);
            Assert.Equal(exact is not null, row.GetProperty("available").GetBoolean());
            if (exact is not null) Assert.Equal(exact.DisplaySnapshot, row.GetProperty("display").GetString());
        }
        if (first.GetProperty("total").GetInt32() > 2)
        {
            var second = await client.GetFromJsonAsync<JsonElement>(url + "&page=2");
            Assert.Equal(first.GetProperty("total").GetInt32(), second.GetProperty("total").GetInt32());
            Assert.All(second.GetProperty("items").EnumerateArray(), row =>
                Assert.DoesNotContain(row.GetProperty("exactIdentityId").GetGuid(), identities));
        }
    }

    [Fact]
    public async Task Target_discovery_rejects_foreign_context_and_unbounded_filters()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await ShowcaseApiFixture.LoginAdministratorAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var releaseId = await db.CandidateBaselines.Where(x => x.Id == showcase.Summary.ReleasedBaselineId)
            .Select(x => x.ReleaseId).SingleAsync();
        var url = $"/api/projects/{showcase.Summary.ProjectId}/code/targets?releaseId={releaseId}&targetKind=RequirementRevision";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + $"&baselineId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url + "&pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url + "&page=100001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url.Replace("RequirementRevision", "Unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(releaseId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
    }
}
