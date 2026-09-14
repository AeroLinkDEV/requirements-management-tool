using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>AR09 coverage for the split between draft management and project creation authority.</summary>
public sealed class ProjectSetupAuthorizationApiTests
{
    [Fact]
    public async Task Former_creator_can_resume_and_save_but_current_administrator_must_finalize()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);

        const string creatorName = "setup.former.creator";
        var creatorPassword = AeroLinkApiFactory.MemberPassword;
        Guid draftId;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var creator = new UserAccount(creatorName, "Setup Former Creator", "setup.former.creator@example.test",
                IdentityService.HashPassword(creatorPassword), DateTimeOffset.UtcNow);
            var draft = new ProjectSetupDraft(creator.Id, creator.UserName, "Administrator handoff");
            db.AddRange(creator, draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }

        using var creatorClient = factory.CreateClient();
        using var login = await creatorClient.PostAsJsonAsync("/api/auth/login",
            new { userName = creatorName, password = creatorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(creatorClient);

        using var resumed = await creatorClient.GetAsync($"/api/project-setups/{draftId}");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        using var saved = await creatorClient.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = "Administrator handoff", softwareProduct = "Handoff product" },
            start = new { kind = "Fresh" },
            build = new { version = "1.3" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { },
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedBody = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal(2, savedBody.RootElement.GetProperty("version").GetInt64());

        using var creatorFinalize = await creatorClient.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "former-creator-must-not-create",
        });
        Assert.Equal(HttpStatusCode.Forbidden, creatorFinalize.StatusCode);

        using var adminFinalize = await administrator.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = 2,
            idempotencyKey = "current-admin-creates",
        });
        Assert.Equal(HttpStatusCode.OK, adminFinalize.StatusCode);
        using var result = JsonDocument.Parse(await adminFinalize.Content.ReadAsStringAsync());
        var programId = result.RootElement.GetProperty("programId").GetGuid();
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();

        using var verify = factory.Services.CreateScope();
        var verificationDb = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var management = await verificationDb.ProgramMemberships.AsNoTracking().SingleAsync(x => x.ProgramId == programId);
        Assert.Equal(creatorName, (await verificationDb.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == management.UserId)).UserName);
        Assert.Equal("admin", management.GrantedBy);
        var completion = await verificationDb.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
            x.EventType == "ProjectSetupCompleted" && x.Target == draftId.ToString("D"));
        Assert.Equal("admin", completion.ActorId);
        Assert.Contains(projectId.ToString("D"), completion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demoted_native_creator_can_resume_but_current_administrator_must_accept_source()
    {
        using var factory = new AeroLinkApiFactory();
        using var originalAdministrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(originalAdministrator);

        const string demotedName = "demoted.native.creator";
        const string administratorPassword = AeroLinkApiFactory.AdministratorPassword;
        Guid originalAdministratorId;
        Guid sourceBaselineId;
        Guid sourceProgramId;
        var now = DateTimeOffset.UtcNow;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var administrator = await db.UserAccounts.SingleAsync(x => x.UserName == "admin");
            originalAdministratorId = administrator.Id;
            var program = new ProgramRecord("Native demotion source program", "NDSP");
            var project = new ProjectRecord(program.Id, "Native demotion source", "Native demotion product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-93.01", 0, project.Id, release.Id, null,
                "Native demotion source baseline", "source.manager", now);
            var change = new SystemChangeRequest("SRCR-930001", 0, project.Id, release.Id,
                "Native demotion requirement", "Problem", "Analysis", "Solution", "source.author", now);
            var requirement = new RequirementArtifact(project.Id, "SYSR-930001", RequirementLevel.System, now);
            var revision = new RequirementRevision(requirement.Id, 0,
                "The native demotion source requirement shall remain exact.", "Source rationale", "Inspection",
                RequirementRevisionState.Active, change.Id, baseline.Id, now);
            baseline.FreezeForInception("source.manager", now);
            baseline.MarkRequirementsMaterialized("source.manager", new string('e', 64), 1, now);
            db.AddRange(program, project, release, baseline, change, requirement, revision,
                new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id),
                new ProgramMembership(administrator.Id, program.Id, ProgramRole.Engineer, "admin", now));
            await db.SaveChangesAsync();
            sourceBaselineId = baseline.Id;
            sourceProgramId = program.Id;
        }

        using var create = await originalAdministrator.PostAsJsonAsync("/api/project-setups", new
        { projectName = "Demoted native destination" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = created.GetProperty("draftId").GetGuid();
        using var details = await originalAdministrator.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        {
            expectedVersion = 1, currentStep = "StartingPoint",
            project = new { name = "Demoted native destination", softwareProduct = "Demoted native product" },
            build = new { version = "1.3" }, selectedCategories = Array.Empty<string>(), ladder = new { },
            reviewRules = new { }, reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" }, mapping = new { },
        });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var capture = await originalAdministrator.PostAsJsonAsync($"/api/project-setups/{draftId}/source/native", new
        { expectedVersion = 2, baselineId = sourceBaselineId });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        var captured = await capture.Content.ReadFromJsonAsync<JsonElement>();
        var capturedVersion = captured.GetProperty("draftVersion").GetInt64();
        using var observedResponse = await originalAdministrator.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, observedResponse.StatusCode);
        var observed = await observedResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var configured = await originalAdministrator.PutAsJsonAsync($"/api/project-setups/{draftId}/source/configuration", new
        {
            expectedVersion = capturedVersion, selectedCategories = new[] { "Requirements" },
            mapping = BuildNativeMapping(observed), metadata = new { },
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        var configuredBody = await configured.Content.ReadFromJsonAsync<JsonElement>();
        var configuredVersion = configuredBody.GetProperty("draftVersion").GetInt64();
        using var configuredSourceResponse = await originalAdministrator.GetAsync($"/api/project-setups/{draftId}/source");
        var configuredSource = await configuredSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sourceAssertionHash = configuredSource.GetProperty("assertion").GetProperty("hash").GetString();

        // This is the current identity model's administrator boundary: administrator authority is derived from
        // the persisted reserved username. Rename the actual draft creator, preserve its source membership, and
        // install a different current administrator so both sessions resolve authority from the database.
        using (var demotion = factory.Services.CreateScope())
        {
            var db = demotion.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var updated = await db.UserAccounts.Where(x => x.Id == originalAdministratorId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.UserName, demotedName));
            Assert.Equal(1, updated);
            db.UserAccounts.Add(new UserAccount("admin", "Replacement Administrator", "replacement.admin@example.test",
                IdentityService.HashPassword(administratorPassword), now));
            await db.SaveChangesAsync();
        }

        using var creator = factory.CreateClient();
        using var creatorLogin = await creator.PostAsJsonAsync("/api/auth/login", new
        { userName = demotedName, password = administratorPassword });
        Assert.Equal(HttpStatusCode.OK, creatorLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(creator);
        var me = await creator.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.False(me.GetProperty("isAdministrator").GetBoolean());
        using var creatorSource = await creator.GetAsync($"/api/project-setups/{draftId}/source");
        Assert.Equal(HttpStatusCode.OK, creatorSource.StatusCode);
        using (var membershipCheck = factory.Services.CreateScope())
        {
            var db = membershipCheck.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.True(await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == originalAdministratorId
                && x.ProgramId == sourceProgramId && x.EndedAt == null));
        }
        using var creatorSave = await creator.PutAsJsonAsync($"/api/project-setups/{draftId}", new
        { expectedVersion = configuredVersion, currentStep = "Review" });
        Assert.Equal(HttpStatusCode.OK, creatorSave.StatusCode);
        var creatorSaved = await creatorSave.Content.ReadFromJsonAsync<JsonElement>();
        var resumedVersion = creatorSaved.GetProperty("version").GetInt64();
        using var creatorFinalize = await creator.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = resumedVersion, idempotencyKey = "demoted-native-creator-must-not-accept",
            password = administratorPassword, sourceAssertionHash, sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.Forbidden, creatorFinalize.StatusCode);

        using var currentAdministrator = factory.CreateClient();
        using var currentLogin = await currentAdministrator.PostAsJsonAsync("/api/auth/login", new
        { userName = "admin", password = administratorPassword });
        Assert.Equal(HttpStatusCode.OK, currentLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(currentAdministrator);
        using var currentSourceResponse = await currentAdministrator.GetAsync($"/api/project-setups/{draftId}/source");
        var currentSource = await currentSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var assertionHash = currentSource.GetProperty("assertion").GetProperty("hash").GetString();
        using var finalized = await currentAdministrator.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize", new
        {
            expectedVersion = resumedVersion, idempotencyKey = "replacement-admin-accepts-native-source",
            password = administratorPassword, sourceAssertionHash = assertionHash, sourceAssertionAccepted = true,
        });
        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        var result = await finalized.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = result.GetProperty("projectId").GetGuid();
        using var provenance = await currentAdministrator.GetAsync($"/api/projects/{projectId}/inception-source");
        Assert.Equal(HttpStatusCode.OK, provenance.StatusCode);
        var provenanceBody = await provenance.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", provenanceBody.GetProperty("acceptance").GetProperty("userName").GetString());

        using var verify = factory.Services.CreateScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var destinationProgramId = result.GetProperty("programId").GetGuid();
        var management = await dbVerify.ProgramMemberships.AsNoTracking().SingleAsync(x => x.ProgramId == destinationProgramId);
        Assert.Equal(originalAdministratorId, management.UserId);
        Assert.Equal("admin", management.GrantedBy);
        var completion = await dbVerify.SecurityAuditEvents.AsNoTracking().SingleAsync(x =>
            x.EventType == "ProjectSetupCompleted" && x.Target == draftId.ToString("D"));
        Assert.Equal("admin", completion.ActorId);
        Assert.Contains(sourceBaselineId.ToString("D"), completion.Detail, StringComparison.Ordinal);
    }

    private static object BuildNativeMapping(JsonElement source)
    {
        var objects = source.GetProperty("modules").EnumerateArray()
            .SelectMany(module => module.GetProperty("objects").EnumerateArray())
            .Select(item =>
            {
                var kind = item.GetProperty("kind").GetString() ?? string.Empty;
                var attributes = item.GetProperty("attributes").EnumerateObject()
                    .Select(attribute =>
                    {
                        var destination = kind.Equals("Requirement", StringComparison.OrdinalIgnoreCase)
                            ? attribute.Name.ToLowerInvariant() switch
                            {
                                "statement" => "Statement",
                                "rationale" => "Rationale",
                                "verificationmethod" => "VerificationMethod",
                                _ => "SourceOnly",
                            }
                            : "SourceOnly";
                        return new
                        {
                            sourceAttribute = attribute.Name,
                            destination,
                            reason = destination == "SourceOnly" ? "Retain exact source fact." : null,
                        };
                    }).ToArray();
                var levelEntry = item.GetProperty("attributes").EnumerateObject()
                    .FirstOrDefault(attribute => attribute.Name.Equals("Level", StringComparison.OrdinalIgnoreCase));
                var level = levelEntry.Value.ValueKind == JsonValueKind.String ? levelEntry.Value.GetString() : "System";
                return new { sourceKey = item.GetProperty("key").GetString(), include = true, level, attributes };
            }).ToArray();
        return new
        {
            sourceSha256 = source.GetProperty("sha256").GetString(), objects,
            relations = Array.Empty<object>(), findingResolutions = new Dictionary<string, string>(),
        };
    }
}
