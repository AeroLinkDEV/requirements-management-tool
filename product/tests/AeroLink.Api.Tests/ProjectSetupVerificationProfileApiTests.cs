using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1045 server-side reproduction and invariant coverage for a level whose verification capability is
/// disabled. The recorded owner failure is a saved draft that disables verification at System while the
/// level still enables the Procedure artifact. Draft saves must stay recoverable; finalization is the gate
/// that refuses the contradiction, and a refused finalization must commit nothing at all.
///
/// These tests intentionally describe the shape the walkthrough must be able to produce so the correction
/// is measured against the real service rather than a browser approximation.
/// </summary>
public sealed class ProjectSetupVerificationProfileApiTests
{
    private const int ChangeControl = 1;
    private const int Verification = 2;
    private const int RequirementsDocument = 4;
    private const int CodeTraceability = 8;

    [Fact]
    public async Task Recorded_contradictory_draft_is_refused_and_leaves_no_partial_or_mutated_state()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Recorded shape {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);

        // The recorded owner shape: System keeps Procedure while the capability mask disables verification.
        // A save must still succeed — an intermediate draft is deliberately recoverable.
        var saved = await SaveAsync(client, draftId, expectedVersion: 1, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "GPS 2.0" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = ContradictoryLadder(),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal(2, saved.GetProperty("version").GetInt64());
        Assert.Equal("SW-00.01", saved.GetProperty("build").GetProperty("officialName").GetString());
        Assert.True(saved.GetProperty("reviewRules").GetProperty("accepted").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"recorded-shape-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("cannot_finalize", failure.RootElement.GetProperty("code").GetString());
        Assert.Equal("A level without verification capability cannot enable verification artifacts.",
            failure.RootElement.GetProperty("error").GetString());

        // A refused finalization proves nothing about rollback by itself. Read the authoritative state.
        using var resumed = await client.GetAsync($"/api/project-setups/{draftId}");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        using var resumedBody = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.Equal("Draft", resumedBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, resumedBody.RootElement.GetProperty("version").GetInt64());
        // Every saved answer the creator is entitled to keep survives the refusal unchanged.
        Assert.Equal(projectName, resumedBody.RootElement.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("GPS 2.0", resumedBody.RootElement.GetProperty("project").GetProperty("softwareProduct").GetString());
        Assert.Equal("0.01", resumedBody.RootElement.GetProperty("build").GetProperty("version").GetString());
        // The contradictory-but-saveable shape is preserved verbatim so the creator can repair it in place
        // rather than losing answers to a silent normalization.
        Assert.Equal("System:5:Procedure|HighLevel:7:Case,Procedure|LowLevel:15:Case,Procedure",
            StoredLadderOf(resumedBody.RootElement));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.Projects.Where(x => x.Name == projectName).ToListAsync());
        Assert.Null((await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).CompletedProjectId);
    }

    [Fact]
    public async Task Disabled_system_verification_finalizes_into_a_real_project_without_system_verification_scaffolding()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Disabled system verification {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var saved = await SaveAsync(client, draftId, expectedVersion: 1, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Disabled verification product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = CoherentDisabledSystemLadder(),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // The accepted definition must describe this ladder exactly: no System test subjects, because the
        // System level no longer enables verification.
        var subjects = saved.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => x.GetProperty("subject").GetString()).ToArray();
        Assert.DoesNotContain("SystemTest", subjects);
        Assert.Contains("System", subjects);
        Assert.Contains("Software", subjects);
        Assert.Contains("HighLevelSoftwareCase", subjects);
        Assert.Contains("HighLevelSoftwareProcedure", subjects);
        Assert.Contains("LowLevelSoftwareCase", subjects);
        // The Case-only LowLevel profile must not acquire a procedure subject for its author's convenience.
        Assert.DoesNotContain("LowLevelSoftwareProcedure", subjects);
        Assert.Equal(5, subjects.Length);

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"disabled-system-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("Completed", result.RootElement.GetProperty("state").GetString());
        Assert.False(result.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal("0.01", result.RootElement.GetProperty("version").GetString());
        Assert.Equal("SW-00.01", result.RootElement.GetProperty("officialBuildName").GetString());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();
        var releaseId = result.RootElement.GetProperty("releaseId").GetGuid();

        using var replay = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"disabled-system-{draftId:N}-retry" });
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal(projectId, replayBody.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(releaseId, replayBody.RootElement.GetProperty("releaseId").GetGuid());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var steps = await db.ProjectLadderSteps.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Position).ToListAsync();
        Assert.Equal(3, steps.Count);
        var system = steps.Single(x => x.CatalogueEntry == "System");
        Assert.Equal((LevelCapabilities)(ChangeControl | RequirementsDocument), system.Capabilities);
        Assert.Empty(system.EnabledArtifactKinds);
        // The other levels keep exactly the profile they were accepted with.
        var highLevel = steps.Single(x => x.CatalogueEntry == "HighLevel");
        Assert.Equal(new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure },
            highLevel.EnabledArtifactKinds);
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            steps.Single(x => x.CatalogueEntry == "LowLevel").EnabledArtifactKinds);

        // No inappropriate verification scaffolding for the disabled level, and no fabricated content.
        var containers = await db.TestProcedureDocuments.AsNoTracking()
            .Where(x => x.ProjectId == projectId).ToListAsync();
        Assert.DoesNotContain(containers, x => x.Level == TestProcedureLevel.System);
        Assert.Equal(
            new[]
            {
                (TestProcedureLevel.HighLevel, VerificationArtifactKind.Case),
                (TestProcedureLevel.HighLevel, VerificationArtifactKind.Procedure),
                (TestProcedureLevel.LowLevel, VerificationArtifactKind.Case),
            },
            containers.Select(x => (x.Level, x.ArtifactKind)).OrderBy(x => x.Level).ThenBy(x => x.ArtifactKind).ToArray());
        Assert.Empty(await db.Requirements.Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.TestProcedures.Where(x => x.ProjectId == projectId).ToListAsync());

        // Applicable review workflows match the accepted profile exactly once each.
        var workflows = await db.ReviewWorkflows.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync();
        Assert.Equal(5, workflows.Count);
        Assert.DoesNotContain(workflows, x => x.AppliesTo == ReviewSubject.SystemTest);
        Assert.All(workflows, x => Assert.Equal(ReviewWorkflowState.Active, x.State));

        // The first build is a real, In Work build at the exact requested version identity. A Fresh start
        // records no inherited or fabricated build row: exactly one unpaid-as-released release exists.
        Assert.Empty(await db.SoftwareBuilds.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync();
        var release = Assert.Single(releases);
        Assert.Equal(releaseId, release.Id);
        Assert.Equal("SW-00.01", release.CanonicalIdentity);
        Assert.False(release.IsReleased);
        var programId = (await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId)).ProgramId;
        Assert.Equal(1, await db.Programs.CountAsync(x => x.Id == programId));
    }

    [Fact]
    public async Task A_stale_review_definition_that_no_longer_covers_the_ladder_is_refused_even_when_re_accepted()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Stale coverage {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);

        // First save: System verification is enabled, so the accepted standard covers SystemTest.
        var enabled = await SaveAsync(client, draftId, expectedVersion: 1, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Stale coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = CoherentEnabledSystemLadder(),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var staleDefinition = enabled.GetProperty("reviewRules").GetProperty("definition").Clone();
        var staleSubjects = staleDefinition.GetProperty("rules").EnumerateArray()
            .Select(x => x.GetProperty("subject").GetString()).ToArray();
        Assert.Contains("SystemTest", staleSubjects);

        // Second save: the ladder no longer enables System verification, but the creator re-accepts the
        // structurally complete definition that was written for the previous ladder. Clearing and re-ticking
        // the acceptance control is not coverage.
        var acceptedStaleSubjectSet = await SaveAsync(client, draftId, expectedVersion: 2, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Stale coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = CoherentDisabledSystemLadder(),
            reviewRules = staleDefinition,
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal("Draft", acceptedStaleSubjectSet.GetProperty("state").GetString());
        Assert.True(acceptedStaleSubjectSet.GetProperty("reviewRules").GetProperty("accepted").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"stale-coverage-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("cannot_finalize", failure.RootElement.GetProperty("code").GetString());
        Assert.Contains("cover each applicable ladder subject exactly once",
            failure.RootElement.GetProperty("error").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.Projects.Where(x => x.Name == projectName).ToListAsync());
        Assert.Equal(ProjectSetupState.Draft,
            (await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).State);
        Assert.Equal(3, (await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).Version);
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client, string projectName)
    {
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("draftId").GetGuid();
    }

    private static async Task<JsonElement> SaveAsync(HttpClient client, Guid draftId, long expectedVersion,
        object payload)
    {
        using var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", payload);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode} (expectedVersion {expectedVersion}): {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static object ContradictoryLadder() => new
    {
        steps = new[]
        {
            new { catalogueEntry = "System", position = 1,
                capabilities = ChangeControl | RequirementsDocument,
                enabledArtifactKinds = new[] { "Procedure" } },
            new { catalogueEntry = "HighLevel", position = 2,
                capabilities = ChangeControl | Verification | RequirementsDocument,
                enabledArtifactKinds = new[] { "Case", "Procedure" } },
            new { catalogueEntry = "LowLevel", position = 3,
                capabilities = ChangeControl | Verification | RequirementsDocument | CodeTraceability,
                enabledArtifactKinds = new[] { "Case", "Procedure" } },
        },
        relationships = new[]
        {
            new { parent = "System", child = "HighLevel" },
            new { parent = "HighLevel", child = "LowLevel" },
        },
    };

    private static object CoherentDisabledSystemLadder() => new
    {
        steps = new[]
        {
            new { catalogueEntry = "System", position = 1,
                capabilities = ChangeControl | RequirementsDocument,
                enabledArtifactKinds = Array.Empty<string>() },
            new { catalogueEntry = "HighLevel", position = 2,
                capabilities = ChangeControl | Verification | RequirementsDocument,
                enabledArtifactKinds = new[] { "Case", "Procedure" } },
            new { catalogueEntry = "LowLevel", position = 3,
                capabilities = ChangeControl | Verification | RequirementsDocument | CodeTraceability,
                enabledArtifactKinds = new[] { "Case" } },
        },
        relationships = new[]
        {
            new { parent = "System", child = "HighLevel" },
            new { parent = "HighLevel", child = "LowLevel" },
        },
    };

    private static object CoherentEnabledSystemLadder() => new
    {
        steps = new[]
        {
            new { catalogueEntry = "System", position = 1,
                capabilities = ChangeControl | Verification | RequirementsDocument,
                enabledArtifactKinds = new[] { "Procedure" } },
            new { catalogueEntry = "HighLevel", position = 2,
                capabilities = ChangeControl | Verification | RequirementsDocument,
                enabledArtifactKinds = new[] { "Case", "Procedure" } },
            new { catalogueEntry = "LowLevel", position = 3,
                capabilities = ChangeControl | Verification | RequirementsDocument | CodeTraceability,
                enabledArtifactKinds = new[] { "Case", "Procedure" } },
        },
        relationships = new[]
        {
            new { parent = "System", child = "HighLevel" },
            new { parent = "HighLevel", child = "LowLevel" },
        },
    };

    /// <summary>Reads the resume payload back into the same three-tuple shape save/resume must preserve.</summary>
    private static string StoredLadderOf(JsonElement draft) => string.Join("|", draft
        .GetProperty("ladder").GetProperty("steps").EnumerateArray()
        .Select(step => $"{step.GetProperty("catalogueEntry").GetString()}:" +
            $"{(int)step.GetProperty("capabilities").GetInt32()}:" +
            string.Join(",", step.GetProperty("enabledArtifactKinds").EnumerateArray()
                .Select(kind => kind.GetString()))));
}
