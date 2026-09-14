using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>Exercise the ordinary services from an actually finalized Fresh project, with no showcase roster.</summary>
public sealed class ProjectSetupServiceQualificationTests
{
    [Fact]
    public async Task Fresh_project_supports_deliberate_staffing_ordinary_review_documents_and_durable_roster()
    {
        using var factory = new AeroLinkApiFactory();
        await QualifyAsync(factory);
    }

    internal static async Task<(Guid ProjectId, Guid ProgramId, Guid ReleaseId)> QualifyAsync(AeroLinkApiFactory factory)
    {
        using var admin = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        var created = await JsonAsync(await admin.PostAsJsonAsync("/api/project-setups", new { projectName = "Independent services" }));
        var draftId = created.GetProperty("draftId").GetGuid();
        var draft = await admin.GetFromJsonAsync<JsonElement>($"/api/project-setups/{draftId}");
        await JsonAsync(await admin.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "Review",
            project = new { name = "Independent services", softwareProduct = "Independent product" },
            start = new { kind = "Fresh" }, build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = draft.GetProperty("reviewRules").GetProperty("suggestedDefinition"),
            reviewRulesAccepted = true, repository = new { mode = "ConfigureLater" }, mapping = new { },
        }));
        var result = await JsonAsync(await admin.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = "services-qualification" }));
        var projectId = result.GetProperty("projectId").GetGuid();
        var programId = result.GetProperty("programId").GetGuid();
        var releaseId = result.GetProperty("releaseId").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.Requirements.Where(x => x.ProjectId == projectId).ToListAsync());
            Assert.Empty(await db.CandidateBaselines.Where(x => x.ProjectId == projectId).ToListAsync());
            Assert.Empty(await db.TestProcedures.Where(x => x.ProjectId == projectId).ToListAsync());
            Assert.Single(await db.ProgramMemberships.Where(x => x.ProgramId == programId).ToListAsync());
        }

        // A missing signer does not prevent unrelated setup or a truthful in-work document.
        foreach (var format in new[] { "pdf", "docx" })
        {
            using var document = await admin.GetAsync($"/api/releases/{releaseId}/draft-document?type=Sysrd&format={format}");
            var bytes = await document.Content.ReadAsByteArrayAsync();
            Assert.True(document.IsSuccessStatusCode, Encoding.UTF8.GetString(bytes));
            if (format == "pdf") Assert.StartsWith("%PDF", Encoding.Latin1.GetString(bytes));
            else
            {
                using var zip = new ZipArchive(new MemoryStream(bytes));
                using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                var xml = await reader.ReadToEndAsync();
                Assert.Contains("DRAFT", xml);
                Assert.DoesNotContain("FMS shall", xml);
            }
        }

        UserAccount Account(string name) => new(name, name, $"{name}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        var author = Account("fresh.author");
        var reviewer = Account("fresh.reviewer");
        var approver = Account("fresh.approver");
        var backup = Account("fresh.backup");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.UserAccounts.AddRange(author, reviewer, approver, backup);
            await db.SaveChangesAsync();
        }
        foreach (var person in new[] { author, reviewer, approver, backup })
        {
            using var assigned = await admin.PostAsJsonAsync($"/api/projects/{projectId}/personnel", new
            {
                userId = person.Id,
                roles = new[] { person == author || person == reviewer ? "SystemEngineer" : "ProjectEngineer" },
            });
            await SuccessAsync(assigned);
        }
        using var authorClient = factory.CreateClient();
        await LoginAsync(authorClient, author.UserName);
        var sections = await authorClient.GetFromJsonAsync<JsonElement>($"/api/authoring/sections?projectId={projectId}&level=System");
        var sectionId = sections.EnumerateArray().First().GetProperty("id").GetGuid();
        var change = await JsonAsync(await authorClient.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId, targetReleaseId = releaseId, type = "System", title = "First deliberate requirement",
            problem = "An isolated acceptance requirement is needed.", analysis = "Independent review is required.",
            solution = "Introduce the requirement through the ordinary workflow.",
            requirementChanges = new[] { new
            {
                level = "System", kind = "Introduce", statement = "The isolated test system shall record its operating mode.",
                rationale = "Disposable qualification fixture", verificationMethod = "Test", impactDispositionJson = "{}",
                targetSectionId = sectionId,
            } },
        }));
        var changeId = change.GetProperty("id").GetGuid();
        var submit = new
        {
            expectedVersion = change.GetProperty("version").GetInt64(), mode = "Sequential",
            approvers = new[] { new { userId = reviewer.UserName, name = reviewer.DisplayName },
                new { userId = approver.UserName, name = approver.DisplayName } },
        };
        using (var missingLeader = await authorClient.PostAsJsonAsync($"/api/change-requests/{changeId}/submit", submit))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingLeader.StatusCode);
            Assert.Contains($"{approver.UserName} does not hold authority", await missingLeader.Content.ReadAsStringAsync());
        }
        using (var primary = await admin.PostAsJsonAsync($"/api/projects/{projectId}/leadership/ProjectEngineer/primary",
            new { holderUserId = approver.Id })) await SuccessAsync(primary);
        using (var standingBackup = await admin.PostAsJsonAsync($"/api/projects/{projectId}/leadership/ProjectEngineer/backup",
            new { backupUserId = backup.Id })) await SuccessAsync(standingBackup);
        using (var submitted = await authorClient.PostAsJsonAsync($"/api/change-requests/{changeId}/submit", submit))
            await SuccessAsync(submitted);

        foreach (var signer in new[] { reviewer, approver })
        {
            // Exercise the real outbox while this stage is active. Capture transport locally; never send
            // mail to a service or confuse a recording sender with live relay qualification.
            using (var deliveryScope = factory.Services.CreateScope())
            {
                var db = deliveryScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Notifications:BaseUrl"] = "https://aerolink.example.test",
                    ["Notifications:UnsubscribeSecret"] = "isolated-1037-notification-test-secret-0123456789",
                }).Build();
                var sender = new SetupRecordingSender();
                await new NotificationOutbox(db).DispatchPendingAsync(sender, new NotificationLinkBuilder(configuration),
                    new UnsubscribeTokenService(configuration), 50, 5, DateTimeOffset.UtcNow, default);
                Assert.Contains(sender.Sent, x => x.To == signer.Email
                    && x.PlainTextBody.Contains($"https://aerolink.example.test/open/scr/{changeId}", StringComparison.Ordinal));
            }
            using var signerClient = factory.CreateClient();
            await LoginAsync(signerClient, signer.UserName);
            using (var linkedArtifact = await signerClient.GetAsync($"/api/change-requests/{changeId}"))
                await SuccessAsync(linkedArtifact);
            using var signed = await signerClient.PostAsJsonAsync($"/api/change-requests/{changeId}/approve", new
            {
                password = AeroLinkApiFactory.MemberPassword, meaning = "I accept my assigned review stage.",
                rationale = "Verified the isolated requirement and its stated impact.",
            });
            await SuccessAsync(signed);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var signatures = await db.ElectronicSignatures.Where(x => x.ArtifactId == changeId).ToListAsync();
            Assert.Equal(2, signatures.Count);
            Assert.Contains(signatures, x => x.UserId == reviewer.Id && x.Action == "Review");
            Assert.Contains(signatures, x => x.UserId == approver.Id && x.Action == "Approval");
            Assert.All(signatures, x => { Assert.NotNull(x.WorkflowId); Assert.NotEmpty(x.AuthoritySource); });
            var notifications = await db.UserNotifications.Where(x => x.ArtifactId == changeId).ToListAsync();
            Assert.Contains(notifications, x => x.Recipient == reviewer.UserName);
            Assert.Contains(notifications, x => x.Recipient == approver.UserName);
            Assert.All(notifications, x => { Assert.Equal(projectId, x.ProjectId); Assert.Equal($"scr:{changeId}", x.Route); });
            Assert.DoesNotContain(await db.ProgramMemberships.Where(x => x.ProgramId == programId).ToListAsync(),
                x => x.Role is ProgramRole.Reviewer or ProgramRole.Approver);
        }

        // Compare the deliberately configured records, including history, across fresh contexts and repeated
        // directory seeding. A fixed original probe member count would miss changed decisions.
        var before = await RosterAsync(factory, programId);
        for (var restart = 0; restart < 2; restart++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await new IdentitySeeder(db).EnsureSeededAsync();
            Assert.Equal(before, await RosterAsync(factory, programId));
        }
        return (projectId, programId, releaseId);
    }

    internal static async Task<string> RosterAsync(AeroLinkApiFactory factory, Guid programId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return JsonSerializer.Serialize(new
        {
            memberships = await db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == programId).OrderBy(x => x.Id).ToListAsync(),
            leadership = await db.ProjectLeadershipAssignments.AsNoTracking().Where(x => x.ProgramId == programId).OrderBy(x => x.Id).ToListAsync(),
            leadershipBackups = await db.ProjectLeadershipBackups.AsNoTracking().Where(x => x.ProgramId == programId).OrderBy(x => x.Id).ToListAsync(),
            roleBackups = await db.ProjectRoleBackups.AsNoTracking().Where(x => x.ProgramId == programId).OrderBy(x => x.Id).ToListAsync(),
        });
    }

    private static async Task LoginAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = AeroLinkApiFactory.MemberPassword });
        await SuccessAsync(response);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
    private static async Task SuccessAsync(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response) { await SuccessAsync(response); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    }

    private sealed class SetupRecordingSender : IEmailSender
    {
        public bool IsConfigured => true;
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
