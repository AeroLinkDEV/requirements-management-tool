using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
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
    public async Task A_remark_on_a_case_is_stored_against_the_artifact_and_read_back()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        Guid caseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            caseId = await db.TestProcedures.AsNoTracking()
                .Where(x => x.ProjectId == showcase.Summary.ProjectId && x.Level != TestProcedureLevel.System)
                .Select(x => x.Id).FirstAsync();
        }

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/test-cases/{caseId}/comments");
        var alreadySaid = before.GetArrayLength();

        using var posted = await client.PostAsJsonAsync($"/api/test-cases/{caseId}/comments",
            new { body = "Confirmed against the oceanic rig." });
        Assert.True(posted.StatusCode == HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/test-cases/{caseId}/comments");
        Assert.Equal(alreadySaid + 1, after.GetArrayLength());
        var comment = after.EnumerateArray().Last();
        Assert.Equal("Confirmed against the oceanic rig.", comment.GetProperty("body").GetString());
        Assert.Equal("admin", comment.GetProperty("createdBy").GetString());

        // Against the table rather than only the response: the point of the route is that a procedure's
        // discussion is the same record a requirement's is, distinguished only by artifact type.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var stored = await db.ArtifactComments.AsNoTracking().SingleAsync(x => x.ArtifactId == caseId);
            Assert.Equal("TestCase", stored.ArtifactType);
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

    [Fact]
    public async Task Enabled_software_procedure_discussion_mutates_and_disabled_historical_procedure_is_refused()
    {
        // An activated Procedure-enabled resolver is the post-#726 authority for an active software Procedure.
        using (var enabledFactory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create()))
        {
            using var enabledClient = enabledFactory.CreateClient();
            await BootstrapAsync(enabledClient);
            using var workspaceResponse = await enabledClient.PostAsJsonAsync("/api/workspaces", new
            {
                programName = "Procedure discussion enabled Program",
                programCode = "PDE001",
                projectName = "Procedure discussion enabled Project",
                softwareProduct = "Procedure discussion enabled Product",
                initialRelease = "1.0",
                initialReleaseIsReleased = false,
            });
            Assert.Equal(HttpStatusCode.Created, workspaceResponse.StatusCode);
            Assert.True(workspaceResponse.IsSuccessStatusCode, await workspaceResponse.Content.ReadAsStringAsync());
            var workspace = await workspaceResponse.Content.ReadFromJsonAsync<JsonElement>();
            var projectId = workspace.GetProperty("project").GetProperty("id").GetGuid();
            await using (var scope = enabledFactory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                db.UserAccounts.Add(new UserAccount("discussion.reader", "Discussion Reader",
                    "discussion.reader@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword),
                    DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            using var enabledCreated = await enabledClient.PostAsJsonAsync("/api/test-procedures/drafts", new
            {
                projectId,
                level = "HighLevel",
                title = "Enabled Procedure discussion",
                environmentSetup = "Bench",
                testData = "Known vector",
                orderedSteps = "1. Execute",
                expectedObservations = "Observed",
                cleanup = "Restore",
                toolingAutomation = "Runner",
                parentKind = "Derived",
                derivedRationale = "Procedure-enabled profile discussion proof.",
            });
            Assert.Equal(HttpStatusCode.Created, enabledCreated.StatusCode);
            Assert.True(enabledCreated.IsSuccessStatusCode, await enabledCreated.Content.ReadAsStringAsync());
            var enabledCreatedBody = await enabledCreated.Content.ReadFromJsonAsync<JsonElement>();
            var enabledProcedureId = enabledCreatedBody.GetProperty("id").GetGuid();
            var enabledRevisionId = enabledCreatedBody.GetProperty("revisionId").GetGuid();
            var enabledNotificationsBefore = await CountNotificationsAsync(enabledFactory, projectId);

            using var enabledPost = await enabledClient.PostAsJsonAsync($"/api/test-procedures/{enabledProcedureId}/comments", new
            {
                revisionId = enabledRevisionId,
                body = "The enabled Procedure discussion is controlled.",
                mentions = new[] { "discussion.reader" },
            });
            Assert.Equal(HttpStatusCode.Created, enabledPost.StatusCode);
            Assert.True(enabledPost.IsSuccessStatusCode, await enabledPost.Content.ReadAsStringAsync());
            var enabledCommentId = (await enabledPost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.True(await CountNotificationsAsync(enabledFactory, projectId) > enabledNotificationsBefore);

            using var enabledResolved = await enabledClient.PostAsJsonAsync(
                $"/api/enterprise-requirements/comments/{enabledCommentId}/resolve",
                new { disposition = "The enabled Procedure discussion was reviewed." });
            Assert.Equal(HttpStatusCode.NoContent, enabledResolved.StatusCode);
            Assert.True(enabledResolved.IsSuccessStatusCode, await enabledResolved.Content.ReadAsStringAsync());
            await using (var scope = enabledFactory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var comment = await db.ArtifactComments.AsNoTracking().SingleAsync(x => x.Id == enabledCommentId);
                Assert.Equal(CollaborationState.Dispositioned, comment.State);
            }
        }

        // The default Case-only profile may still contain historical Procedure rows, but its effective key
        // disables Procedure discussion mutations and must not emit notifications.
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        using var created = await client.PostAsJsonAsync("/api/test-procedures/drafts", new
        {
            projectId = showcase.Summary.ProjectId,
            level = "HighLevel",
            title = "Read-only dormant discussion Procedure",
            environmentSetup = "Bench",
            testData = "Known vector",
            orderedSteps = "1. Execute",
            expectedObservations = "Observed",
            cleanup = "Restore",
            toolingAutomation = "Runner",
            parentKind = "Derived",
            derivedRationale = "Standalone while dormant."
        });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var procedureId = createdBody.GetProperty("id").GetGuid();
        var revisionId = createdBody.GetProperty("revisionId").GetGuid();

        int commentsBefore;
        int notificationsBefore;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            commentsBefore = await db.ArtifactComments.CountAsync(x => x.ArtifactId == procedureId);
            notificationsBefore = await db.UserNotifications.CountAsync(x => x.ProjectId == showcase.Summary.ProjectId);
        }

        using var post = await client.PostAsJsonAsync($"/api/test-procedures/{procedureId}/comments", new
        {
            revisionId,
            body = "This must not be stored.",
            mentions = new[] { "admin" }
        });
        var postBody = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal("verification_discussion_disabled", postBody.GetProperty("code").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(commentsBefore, await db.ArtifactComments.CountAsync(x => x.ArtifactId == procedureId));
            Assert.Equal(notificationsBefore, await db.UserNotifications.CountAsync(x => x.ProjectId == showcase.Summary.ProjectId));
        }

        Guid commentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var comment = new ArtifactComment(showcase.Summary.ProjectId, "TestProcedure", procedureId,
                revisionId, null, "Existing read-only comment", "[]", "admin", DateTimeOffset.UtcNow);
            db.ArtifactComments.Add(comment);
            await db.SaveChangesAsync();
            commentId = comment.Id;
        }

        using var resolve = await client.PostAsJsonAsync($"/api/enterprise-requirements/comments/{commentId}/resolve",
            new { disposition = "Must remain open." });
        var resolveBody = await resolve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, resolve.StatusCode);
        Assert.Equal("verification_discussion_disabled", resolveBody.GetProperty("code").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var stored = await db.ArtifactComments.AsNoTracking().SingleAsync(x => x.Id == commentId);
            Assert.Equal(CollaborationState.Open, stored.State);
            Assert.Null(stored.ResolvedBy);
            Assert.Equal(commentsBefore + 1, await db.ArtifactComments.CountAsync(x => x.ArtifactId == procedureId));
            Assert.Equal(notificationsBefore, await db.UserNotifications.CountAsync(x => x.ProjectId == showcase.Summary.ProjectId));
        }
    }

    private static async Task<int> CountNotificationsAsync(AeroLinkApiFactory factory, Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.UserNotifications.CountAsync(x => x.ProjectId == projectId);
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
