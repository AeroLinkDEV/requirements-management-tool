using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Discussion on a test procedure, the same conversation a requirement carries.
///
/// ArtifactComment was already generic over artifact type; only the routes were requirement-shaped. What is
/// worth proving is that the procedure routes write to and read from that same table rather than a parallel
/// record, and that a remark survives being read back on a later request.
/// </summary>
[Collection(ShowcaseApiCollection.Name)]
public sealed class ProcedureDiscussionApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task A_remark_on_a_procedure_is_stored_against_the_procedure_and_read_back()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        Guid procedureId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            procedureId = await db.TestProcedures.AsNoTracking()
                .Where(x => x.ProjectId == showcase.Summary.ProjectId)
                .Select(x => x.Id).FirstAsync();
        }

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/test-procedures/{procedureId}/comments");
        var alreadySaid = before.GetArrayLength();

        using var posted = await client.PostAsJsonAsync($"/api/test-procedures/{procedureId}/comments",
            new { body = "Confirmed against the oceanic rig." });
        Assert.True(posted.StatusCode == HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/test-procedures/{procedureId}/comments");
        Assert.Equal(alreadySaid + 1, after.GetArrayLength());
        var comment = after.EnumerateArray().Last();
        Assert.Equal("Confirmed against the oceanic rig.", comment.GetProperty("body").GetString());
        Assert.Equal("admin", comment.GetProperty("createdBy").GetString());

        // Against the table rather than only the response: the point of the route is that a procedure's
        // discussion is the same record a requirement's is, distinguished only by artifact type.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var stored = await db.ArtifactComments.AsNoTracking().SingleAsync(x => x.ArtifactId == procedureId);
            Assert.Equal("TestProcedure", stored.ArtifactType);
        }
    }

    [Fact]
    public async Task A_remark_on_a_procedure_that_does_not_exist_is_refused()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        using var posted = await client.PostAsJsonAsync($"/api/test-procedures/{Guid.NewGuid()}/comments",
            new { body = "Into the void." });
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Administrator", email = "admin@example.test",
                password = AeroLinkApiFactory.AdministratorPassword,
            }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
