using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class AdministratorChangeRequestApiTests
{
    [Theory]
    [InlineData(ChangeRequestType.System)]
    [InlineData(ChangeRequestType.Software)]
    public async Task Administrator_can_govern_another_authors_change_without_losing_authorship(
        ChangeRequestType type)
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        var scenario = await SeedAsync(factory, type);

        using (var unrelated = factory.CreateClient())
        {
            await LoginAsync(unrelated, "unrelated.engineer");
            using var rejectedDefer = await unrelated.PostAsJsonAsync(
                $"/api/scrs/{scenario.ReadyId}/defer",
                new { reason = "Spoofed author action.", actorId = "change.author" });
            Assert.Equal(HttpStatusCode.Forbidden, rejectedDefer.StatusCode);
            using var rejectedCheckout = await unrelated.PostAsJsonAsync(
                "/api/controlled-editing/checkout",
                new { artifactType = "ChangeRequest", artifactId = scenario.ReadyId });
            Assert.Equal(HttpStatusCode.Forbidden, rejectedCheckout.StatusCode);
        }

        using (var wrongProject = factory.CreateClient())
        {
            await LoginAsync(wrongProject, "other.program.engineer");
            using var rejected = await wrongProject.PostAsJsonAsync(
                $"/api/scrs/{scenario.ReadyId}/defer",
                new { reason = "No access to the governed project." });
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        using (var author = factory.CreateClient())
        {
            await LoginAsync(author, "change.author");
            using var authorDefer = await author.PostAsJsonAsync(
                $"/api/scrs/{scenario.AddRequirementId}/defer",
                new { reason = "Author-owned lifecycle action." });
            Assert.Equal(HttpStatusCode.OK, authorDefer.StatusCode);
            using var authorReinstate = await author.PostAsync(
                $"/api/scrs/{scenario.AddRequirementId}/reinstate", null);
            Assert.Equal(HttpStatusCode.OK, authorReinstate.StatusCode);
        }

        using var checkout = await administrator.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ChangeRequest", artifactId = scenario.ReadyId });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var draftJson = session.GetProperty("draftJson").GetString()!
            .Replace("Governed ready change", "Administrator-governed ready change", StringComparison.Ordinal);
        using var autosave = await administrator.PutAsJsonAsync(
            $"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = session.GetProperty("version").GetInt64(), draftJson });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        var sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt64();
        using var checkIn = await administrator.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = sessionVersion });
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);

        using var file = new MultipartFormDataContent();
        file.Add(new StringContent(scenario.ProjectId.ToString()), "projectId");
        file.Add(new StringContent("ChangeRequest"), "artifactType");
        file.Add(new StringContent(scenario.ReadyId.ToString()), "artifactId");
        file.Add(new StringContent("Supplier rationale"), "label");
        file.Add(new StringContent("Controlled supporting evidence."), "description");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("controlled evidence"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        file.Add(fileContent, "file", "supplier-note.txt");
        using var attachment = await administrator.PostAsync("/api/enterprise-hardening/attachments", file);
        var attachmentBody = await attachment.Content.ReadAsStringAsync();
        Assert.True(attachment.StatusCode == HttpStatusCode.Created,
            $"{(int)attachment.StatusCode}: {attachmentBody}");

        using var readiness = await administrator.PostAsJsonAsync(
            $"/api/scrs/{scenario.AddRequirementId}/requirements", new
            {
                baseNumber = type == ChangeRequestType.System ? "SYSR-00000902" : "HLR-00000902",
                revision = 0,
                level = type == ChangeRequestType.System ? "System" : "HighLevel",
                kind = "Introduce",
                statement = "The product shall retain governed administrator action evidence.",
                rationale = "Administrator recovery remains attributable.",
                verificationMethod = "Inspection",
                actorId = "change.author"
            });
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);

        using var submit = await administrator.PostAsJsonAsync($"/api/scrs/{scenario.ReadyId}/submit",
            new
            {
                expectedVersion = (long?)null,
                approvers = new[] { new { userId = "change.reviewer", name = "Caller supplied name ignored" } },
                mode = "Sequential",
                actorId = "change.author"
            });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        using var immutableAttachment = new MultipartFormDataContent();
        immutableAttachment.Add(new StringContent(scenario.ProjectId.ToString()), "projectId");
        immutableAttachment.Add(new StringContent("ChangeRequest"), "artifactType");
        immutableAttachment.Add(new StringContent(scenario.ReadyId.ToString()), "artifactId");
        immutableAttachment.Add(new ByteArrayContent([1, 2, 3]), "file", "late.bin");
        using var rejectedAttachment = await administrator.PostAsync(
            "/api/enterprise-hardening/attachments", immutableAttachment);
        Assert.Equal(HttpStatusCode.Conflict, rejectedAttachment.StatusCode);

        using var defer = await administrator.PostAsJsonAsync($"/api/scrs/{scenario.ReadyId}/defer",
            new { reason = "Program authority paused the governed package." });
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        using var reinstate = await administrator.PostAsync(
            $"/api/scrs/{scenario.ReadyId}/reinstate", null);
        Assert.Equal(HttpStatusCode.OK, reinstate.StatusCode);

        using var revise = await administrator.PostAsJsonAsync(
            $"/api/scrs/{scenario.ApprovedId}/next-revision", new { actorId = "change.author" });
        Assert.Equal(HttpStatusCode.Created, revise.StatusCode);
        var next = await revise.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("change.author", next.GetProperty("authorId").GetString());
        Assert.Contains(next.GetProperty("audit").EnumerateArray(),
            item => item.GetProperty("actorId").GetString() == "admin");

        using var invalidLifecycle = await administrator.PostAsJsonAsync(
            $"/api/scrs/{scenario.AddRequirementId}/next-revision", new { });
        Assert.Equal(HttpStatusCode.BadRequest, invalidLifecycle.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var records = await db.SystemChangeRequests.Include(x => x.AuditEvents)
            .Where(x => x.Id == scenario.ReadyId || x.Id == scenario.AddRequirementId).ToListAsync();
        Assert.All(records, record => Assert.Equal("change.author", record.AuthorId));
        Assert.Contains(records.SelectMany(x => x.AuditEvents), x => x.ActorId == "admin");
        Assert.Contains(await db.ControlledAttachments.ToListAsync(),
            x => x.ArtifactId == scenario.ReadyId && x.UploadedBy == "admin");
        Assert.Contains(await db.ControlledArtifactCheckInEvidence.ToListAsync(),
            x => x.ArtifactId == scenario.ReadyId && x.Actor == "admin" &&
                 x.Outcome == ControlledCheckInOutcome.Succeeded);
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory, ChangeRequestType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var code = $"AC{Guid.NewGuid():N}"[..12];
        var program = new ProgramRecord("Administrator Change Program", code);
        var project = new ProjectRecord(program.Id, "Governed Flight Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, release);

        foreach (var (userName, role) in new[]
                 {
                     ("change.author", ProgramRole.Engineer),
                     ("unrelated.engineer", ProgramRole.Engineer),
                     ("change.reviewer", ProgramRole.Approver)
                 })
        {
            var account = new UserAccount(userName, userName, $"{userName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(account, new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }

        var otherProgram = new ProgramRecord("Unrelated Program", $"ZZ{Guid.NewGuid():N}"[..12]);
        var otherAccount = new UserAccount("other.program.engineer", "Other Program Engineer",
            "other.program.engineer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(otherProgram, otherAccount,
            new ProgramMembership(otherAccount.Id, otherProgram.Id, ProgramRole.Engineer, "test.setup", now));

        var prefix = type == ChangeRequestType.System ? "SCR" : "SWCR";
        var requirement = type == ChangeRequestType.System ? "SYSR" : "HLR";
        var level = type == ChangeRequestType.System ? RequirementLevel.System : RequirementLevel.HighLevel;
        var ready = Ready($"{prefix}-00900", $"{requirement}-00000900", "Governed ready change");
        var approved = Ready($"{prefix}-00901", $"{requirement}-00000901", "Approved governed change");
        approved.SubmitForReview("change.author",
            [new ApproverSelection("change.reviewer", "change.reviewer")], now);
        approved.ApproveActiveStage("change.reviewer", now);
        var addRequirement = new SystemChangeRequest($"{prefix}-00902", 0, project.Id, release.Id,
            "Draft readiness completion", "Problem", "Analysis", "Solution", "change.author", now, type);
        db.AddRange(ready, approved, addRequirement);
        await db.SaveChangesAsync();
        return new(project.Id, ready.Id, addRequirement.Id, approved.Id);

        SystemChangeRequest Ready(string number, string requirementNumber, string title)
        {
            var item = new SystemChangeRequest(number, 0, project.Id, release.Id, title,
                "Problem", "Analysis", "Solution", "change.author", now, type);
            item.AddRequirementChange("change.author", requirementNumber, 0, level,
                RequirementChangeKind.Introduce, "The product shall preserve governed state.",
                "Controlled rationale.", "Test", now);
            return item;
        }
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed record Scenario(Guid ProjectId, Guid ReadyId, Guid AddRequirementId, Guid ApprovedId);
}
