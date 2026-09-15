using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1045 server-side coverage for a project setup whose verification capability is disabled, plus the
/// default semantics that make "absent", "explicitly empty", "valid" and "unrecognized" different facts.
///
/// The recorded owner failure is a saved draft that disables verification at System while the level still
/// enables Procedure. Draft saves stay recoverable; the authoritative readiness verdict explains what is
/// wrong by level and field; finalization refuses it and commits nothing.
/// </summary>
public sealed class ProjectSetupVerificationProfileApiTests
{
    private const int ChangeControl = 1;
    private const int Verification = 2;
    private const int RequirementsDocument = 4;
    private const int CodeTraceability = 8;
    private const int SoftwareCapabilities = ChangeControl | Verification | RequirementsDocument;
    private const int LowLevelCapabilities = SoftwareCapabilities | CodeTraceability;
    private const int SystemWithoutVerification = ChangeControl | RequirementsDocument;

    [Fact]
    public async Task Recorded_contradictory_draft_is_refused_with_no_partial_state_and_a_truthful_verdict()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Recorded shape {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);

        // The recorded owner shape: System keeps Procedure while the capability mask disables verification.
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "GPS 2.0" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(systemKinds: ["Procedure"], systemCapabilities: SystemWithoutVerification,
                highLevelKinds: ["Case", "Procedure"], lowLevelKinds: ["Case", "Procedure"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal(2, saved.GetProperty("version").GetInt64());
        Assert.Equal("SW-00.01", saved.GetProperty("build").GetProperty("officialName").GetString());

        // A save must succeed, and the authoritative verdict must say plainly that this is not viable.
        var validation = saved.GetProperty("validation");
        Assert.False(validation.GetProperty("configurationReady").GetBoolean());
        Assert.False(validation.GetProperty("ladderValid").GetBoolean());
        var finding = Findings(validation).Single(x => x.GetProperty("code").GetString()
            == "verification_disabled_with_artifacts");
        Assert.Equal("System", finding.GetProperty("level").GetString());
        Assert.Equal("enabledArtifactKinds", finding.GetProperty("field").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var before = await ProjectStateCountsAsync(db);
        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"recorded-shape-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        // The compatible top-level fields stay exactly as they were for existing callers.
        Assert.Equal("cannot_finalize", failure.RootElement.GetProperty("code").GetString());
        Assert.Equal("A level without verification capability cannot enable verification artifacts.",
            failure.RootElement.GetProperty("error").GetString());
        // The refusal is also diagnosable by machine, not only by reading English.
        var refused = Findings(failure.RootElement).Single(x => x.GetProperty("code").GetString()
            == "verification_disabled_with_artifacts");
        Assert.Equal("System", refused.GetProperty("level").GetString());

        // Every saved answer the creator is entitled to keep survives the refusal unchanged.
        var resumed = await ReadDraftAsync(client, draftId);
        Assert.Equal("Draft", resumed.GetProperty("state").GetString());
        Assert.Equal(2, resumed.GetProperty("version").GetInt64());
        Assert.Equal(projectName, resumed.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("GPS 2.0", resumed.GetProperty("project").GetProperty("softwareProduct").GetString());
        Assert.Equal("0.01", resumed.GetProperty("build").GetProperty("version").GetString());
        Assert.Equal("System:5:Procedure|HighLevel:7:Case,Procedure|LowLevel:15:Case,Procedure",
            StoredLadderOf(resumed));
        Assert.False(resumed.GetProperty("validation").GetProperty("configurationReady").GetBoolean());

        // A refusal must leave no partial committed state anywhere the transaction could write.
        Assert.Equal(before, await ProjectStateCountsAsync(db));
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
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Disabled verification product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(systemKinds: [], systemCapabilities: SystemWithoutVerification,
                highLevelKinds: ["Case", "Procedure"], lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // The accepted definition must describe this ladder exactly: no System test subjects, because the
        // System level no longer enables verification, and no LowLevel procedure subject either.
        var subjects = SubjectsOf(saved.GetProperty("reviewRules").GetProperty("definition"));
        Assert.DoesNotContain("SystemTest", subjects);
        Assert.Contains("System", subjects);
        Assert.Contains("Software", subjects);
        Assert.Contains("HighLevelSoftwareCase", subjects);
        Assert.Contains("HighLevelSoftwareProcedure", subjects);
        Assert.Contains("LowLevelSoftwareCase", subjects);
        Assert.DoesNotContain("LowLevelSoftwareProcedure", subjects);
        Assert.True(saved.GetProperty("validation").GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"disabled-system-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("Completed", result.RootElement.GetProperty("state").GetString());
        Assert.False(result.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal("SW-00.01", result.RootElement.GetProperty("officialBuildName").GetString());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();
        var releaseId = result.RootElement.GetProperty("releaseId").GetGuid();

        // A same-key retry recovers the recorded result instead of creating a second project.
        using var sameKey = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"disabled-system-{draftId:N}" });
        Assert.True(sameKey.IsSuccessStatusCode, await sameKey.Content.ReadAsStringAsync());
        using var sameKeyBody = JsonDocument.Parse(await sameKey.Content.ReadAsStringAsync());
        Assert.True(sameKeyBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal(projectId, sameKeyBody.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(releaseId, sameKeyBody.RootElement.GetProperty("releaseId").GetGuid());

        // A different key on a completed draft recovers the same identities rather than a second project.
        using var otherKey = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"disabled-system-{draftId:N}-other" });
        Assert.True(otherKey.IsSuccessStatusCode, await otherKey.Content.ReadAsStringAsync());
        using var otherKeyBody = JsonDocument.Parse(await otherKey.Content.ReadAsStringAsync());
        Assert.True(otherKeyBody.RootElement.GetProperty("alreadyCompleted").GetBoolean());
        Assert.Equal(projectId, otherKeyBody.RootElement.GetProperty("projectId").GetGuid());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var steps = await db.ProjectLadderSteps.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Position).ToListAsync();
        Assert.Equal(3, steps.Count);
        Assert.Equal((LevelCapabilities)SystemWithoutVerification,
            steps.Single(x => x.CatalogueEntry == "System").Capabilities);
        Assert.Empty(steps.Single(x => x.CatalogueEntry == "System").EnabledArtifactKinds);
        Assert.Equal(new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure },
            steps.Single(x => x.CatalogueEntry == "HighLevel").EnabledArtifactKinds);
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            steps.Single(x => x.CatalogueEntry == "LowLevel").EnabledArtifactKinds);

        // Effective/resolved configuration, not only the stored columns.
        var stored = await db.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId);
        var activation = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(stored.Version, activation.Revision);
        Assert.Equal(ProjectLadderSnapshot.CurrentSchemaVersion, activation.SnapshotSchemaVersion);
        Assert.True(ProjectLadderSnapshot.Verify(activation.CanonicalSnapshot, activation.SnapshotHash,
            activation.SnapshotSchemaVersion), "the activated ladder snapshot must verify against its hash");

        // The maintained resolver — the authority a runtime consumer reads the ladder through — must derive
        // exactly the accepted profile from the persisted configuration, including the disabled System level.
        var storedForResolution = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == projectId);
        var resolved = ProjectLadderResolver.Resolve(storedForResolution);
        Assert.Empty(resolved.Steps.Single(x => x.Level == RequirementLevel.System).EnabledArtifactKinds!);
        Assert.Equal(new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure },
            resolved.Steps.Single(x => x.Level == RequirementLevel.HighLevel).EnabledArtifactKinds);
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            resolved.Steps.Single(x => x.Level == RequirementLevel.LowLevel).EnabledArtifactKinds);
        Assert.Equal(2, resolved.AllowedUpstream.Count);
        Assert.Contains(resolved.AllowedUpstream,
            x => x.Parent == RequirementLevel.System && x.Child == RequirementLevel.HighLevel);
        Assert.Contains(resolved.AllowedUpstream,
            x => x.Parent == RequirementLevel.HighLevel && x.Child == RequirementLevel.LowLevel);

        // No inappropriate verification scaffolding, and no fabricated engineering content or history.
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
        Assert.Empty(await db.SoftwareBuilds.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());

        var workflows = await db.ReviewWorkflows.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync();
        Assert.Equal(5, workflows.Count);
        Assert.DoesNotContain(workflows, x => x.AppliesTo == ReviewSubject.SystemTest);
        Assert.All(workflows, x => Assert.Equal(ReviewWorkflowState.Active, x.State));

        var release = Assert.Single(await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Equal(releaseId, release.Id);
        Assert.Equal("SW-00.01", release.CanonicalIdentity);
        Assert.False(release.IsReleased);
        var programId = (await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId)).ProgramId;
        Assert.Equal(1, await db.Programs.CountAsync(x => x.Id == programId));
        Assert.Equal(1, await db.ProgramMemberships.CountAsync(x => x.ProgramId == programId));
    }

    [Fact]
    public async Task A_missing_software_profile_keeps_the_legacy_case_only_interpretation_everywhere()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Missing profile {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        // HighLevel carries no profile property at all; System and LowLevel are explicit.
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Missing profile product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        var validation = saved.GetProperty("validation");
        var highLevel = validation.GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("level").GetString() == "HighLevel");
        Assert.Equal(JsonValueKind.Null, highLevel.GetProperty("stored").ValueKind);
        Assert.Equal(["Case"], Strings(highLevel.GetProperty("effective")));
        Assert.Equal("catalogue-fallback", highLevel.GetProperty("profileSource").GetString());
        Assert.True(validation.GetProperty("ladderValid").GetBoolean(), saved.GetRawText());

        // The offered standard must describe the same interpretation the finalizer applies. The legacy
        // software fallback is Case-only; it is not the walkthrough's new-project Case + Procedure default.
        var subjects = SubjectsOf(saved.GetProperty("reviewRules").GetProperty("definition"));
        Assert.Contains("HighLevelSoftwareCase", subjects);
        Assert.DoesNotContain("HighLevelSoftwareProcedure", subjects);
        Assert.True(validation.GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"missing-profile-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(new[] { VerificationArtifactKind.Case },
            (await db.ProjectLadderSteps.AsNoTracking().SingleAsync(
                x => x.ProjectId == projectId && x.CatalogueEntry == "HighLevel")).EnabledArtifactKinds);
        var workflows = await db.ReviewWorkflows.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync();
        Assert.Contains(workflows, x => x.AppliesTo == ReviewSubject.HighLevelSoftwareCase);
        Assert.DoesNotContain(workflows, x => x.AppliesTo == ReviewSubject.HighLevelSoftwareProcedure);

        // An explicit null is the other way the wire can carry "no profile was recorded". It must reach the
        // same maintained interpretation as an absent property — and stay null on read, not become [].
        var nullDraft = await CreateDraftAsync(client, $"{projectName} null");
        var savedNull = await SaveAsync(client, nullDraft, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = $"{projectName} null", softwareProduct = "Null profile product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"], highLevelProfileIsNull: true),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var nullValidation = savedNull.GetProperty("validation");
        var nullHighLevel = nullValidation.GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("level").GetString() == "HighLevel");
        Assert.Equal(JsonValueKind.Null, nullHighLevel.GetProperty("stored").ValueKind);
        Assert.Equal(["Case"], Strings(nullHighLevel.GetProperty("effective")));
        Assert.Equal("catalogue-fallback", nullHighLevel.GetProperty("profileSource").GetString());
        Assert.True(nullValidation.GetProperty("ladderValid").GetBoolean(), savedNull.GetRawText());
        var nullRead = await ReadDraftAsync(client, nullDraft);
        var storedNullStep = nullRead.GetProperty("ladder").GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("catalogueEntry").GetString() == "HighLevel");
        Assert.Equal(JsonValueKind.Null, storedNullStep.GetProperty("enabledArtifactKinds").ValueKind);
        var nullSubjects = SubjectsOf(savedNull.GetProperty("reviewRules").GetProperty("definition"));
        Assert.Contains("HighLevelSoftwareCase", nullSubjects);
        Assert.DoesNotContain("HighLevelSoftwareProcedure", nullSubjects);
    }

    [Fact]
    public async Task Explicit_empty_profile_with_verification_enabled_stays_empty_and_is_diagnosed_by_level()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Explicit empty {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Explicit empty product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(systemKinds: ["Procedure"], highLevelKinds: [], lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // The stored intent is untouched: an explicit empty list is not "fill in the default".
        Assert.Equal("System:7:Procedure|HighLevel:7:|LowLevel:15:Case", StoredLadderOf(saved));
        var validation = saved.GetProperty("validation");
        var highLevel = validation.GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("level").GetString() == "HighLevel");
        Assert.Empty(Strings(highLevel.GetProperty("stored")));
        Assert.Empty(Strings(highLevel.GetProperty("effective")));
        Assert.Equal("explicit", highLevel.GetProperty("profileSource").GetString());
        Assert.False(validation.GetProperty("ladderValid").GetBoolean());
        var finding = Findings(validation).Single(x => x.GetProperty("code").GetString()
            == "verification_profile_invalid");
        Assert.Equal("HighLevel", finding.GetProperty("level").GetString());
        Assert.Contains("Case", finding.GetProperty("message").GetString());
        Assert.False(validation.GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"explicit-empty-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Contains("Case, Procedure", failure.RootElement.GetProperty("error").GetString());
        Assert.Equal("HighLevel", Findings(failure.RootElement)
            .Single(x => x.GetProperty("code").GetString() == "verification_profile_invalid")
            .GetProperty("level").GetString());

        var resumed = await ReadDraftAsync(client, draftId);
        Assert.Empty(Strings(resumed.GetProperty("ladder").GetProperty("steps").EnumerateArray()
            .Single(x => x.GetProperty("catalogueEntry").GetString() == "HighLevel")
            .GetProperty("enabledArtifactKinds")));
        Assert.Equal(ProjectSetupState.Draft,
            (await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AeroLinkDbContext>()
                .ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).State);
    }

    [Fact]
    public async Task Unrecognized_artifact_token_is_diagnosed_by_level_and_token_not_as_a_payload_error()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Unknown token {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Unknown token product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(highLevelKinds: ["Case", "Rubbish"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // The stored token is preserved, never filtered into a shorter apparently valid profile.
        Assert.Equal(["Case", "Rubbish"], Strings(saved.GetProperty("ladder").GetProperty("steps")
            .EnumerateArray().Single(x => x.GetProperty("catalogueEntry").GetString() == "HighLevel")
            .GetProperty("enabledArtifactKinds")));
        var validation = saved.GetProperty("validation");
        Assert.False(validation.GetProperty("ladderValid").GetBoolean());
        var finding = Findings(validation).Single(x => x.GetProperty("code").GetString()
            == "artifact_kind_unrecognized");
        Assert.Equal("HighLevel", finding.GetProperty("level").GetString());
        Assert.Equal("enabledArtifactKinds", finding.GetProperty("field").GetString());
        Assert.Equal("Rubbish", finding.GetProperty("token").GetString());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"unknown-token-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.NotEqual("The ladder payload is invalid JSON.", failure.RootElement.GetProperty("error").GetString());
        Assert.Equal("Rubbish", Findings(failure.RootElement)
            .Single(x => x.GetProperty("code").GetString() == "artifact_kind_unrecognized")
            .GetProperty("token").GetString());
    }

    [Fact]
    public async Task A_stale_review_definition_that_no_longer_covers_the_ladder_is_refused_even_when_re_accepted()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Stale coverage {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var enabled = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Stale coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var staleDefinition = enabled.GetProperty("reviewRules").GetProperty("definition").Clone();
        Assert.Contains("SystemTest", SubjectsOf(staleDefinition));

        // The ladder no longer enables System verification, but the creator re-accepts the structurally
        // complete definition written for the previous ladder. Clearing and re-ticking a checkbox is not
        // coverage, and the authoritative verdict has to say so before anything is created.
        var acceptedStale = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Stale coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(systemKinds: [], systemCapabilities: SystemWithoutVerification),
            reviewRules = staleDefinition,
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.Equal("Draft", acceptedStale.GetProperty("state").GetString());
        var validation = acceptedStale.GetProperty("validation");
        Assert.True(validation.GetProperty("ladderValid").GetBoolean(), acceptedStale.GetRawText());
        var review = validation.GetProperty("review");
        Assert.False(review.GetProperty("covers").GetBoolean());
        Assert.Equal(["SystemTest"], Strings(review.GetProperty("unexpectedSubjects")));
        Assert.Empty(Strings(review.GetProperty("missingSubjects")));
        Assert.False(validation.GetProperty("configurationReady").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var before = await ProjectStateCountsAsync(db);
        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"stale-coverage-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Contains("cover each applicable ladder subject exactly once",
            failure.RootElement.GetProperty("error").GetString());
        // This refusal happens after the claim and after the program, project, release, ladder, membership
        // and repository rows were staged, so it is real evidence that the whole unit rolled back.
        Assert.Equal(before, await ProjectStateCountsAsync(db));
        var draft = await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
        Assert.Equal(ProjectSetupState.Draft, draft.State);
        Assert.Equal(3, draft.Version);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task Exact_rule_coverage_is_required_beyond_set_membership(string mutation)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Coverage {mutation} {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = seeded.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!).ToList();
        Assert.Equal(5, rules.Count);
        var mutated = new JsonArray(rules.Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray());
        switch (mutation)
        {
            case "duplicate":
                mutated.Add(JsonNode.Parse(rules[0]!.ToJsonString()));
                break;
            case "missing":
                mutated.RemoveAt(mutated.Count - 1);
                break;
            default:
                var extra = JsonNode.Parse(rules[0]!.ToJsonString())!;
                extra["subject"] = "Interface";
                mutated.Add(extra);
                break;
        }

        var accepted = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Coverage product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules = mutated },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var review = accepted.GetProperty("validation").GetProperty("review");
        Assert.False(review.GetProperty("covers").GetBoolean());
        switch (mutation)
        {
            case "duplicate":
                Assert.Equal(["System"], Strings(review.GetProperty("duplicateSubjects")));
                break;
            case "missing":
                Assert.Equal(["LowLevelSoftwareCase"], Strings(review.GetProperty("missingSubjects")));
                break;
            default:
                Assert.Equal(["Interface"], Strings(review.GetProperty("unexpectedSubjects")));
                break;
        }

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"coverage-{mutation}-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Contains("cover each applicable ladder subject exactly once",
            failure.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// C2-01 counterexample: subjects that cover the ladder exactly are still not a valid definition. A rule
    /// with named, otherwise valid Review stages but no Approval stage is refused by the final gate, so the
    /// readiness verdict must not call the saved configuration ready — and the refusal must name the rule.
    /// </summary>
    [Fact]
    public async Task A_rule_without_an_approval_stage_is_not_ready_and_is_refused_by_rule()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Incomplete definition {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Incomplete definition product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = seeded.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!).ToList();
        // Drop only the Approval signature. Coverage, names, roles and the Review stages all stay valid.
        var first = rules[0]!;
        var stagesWithoutApproval = new JsonArray(first["stages"]!.AsArray()
            .Where(x => x!["kind"]!.GetValue<string>() != "Approval")
            .Select(x => JsonNode.Parse(x!.ToJsonString())!)
            .ToArray());
        first["stages"] = stagesWithoutApproval;
        var mutated = new JsonArray(rules.Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray());

        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Incomplete definition product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules = mutated },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // The incomplete definition is saved — draft recovery is not narrowed — but it is not ready.
        Assert.Equal("Draft", saved.GetProperty("state").GetString());
        var validation = saved.GetProperty("validation");
        Assert.True(validation.GetProperty("ladderValid").GetBoolean());
        var review = validation.GetProperty("review");
        Assert.True(review.GetProperty("covers").GetBoolean(), "the subjects still cover this ladder exactly");
        Assert.False(review.GetProperty("definitionValid").GetBoolean());
        Assert.False(validation.GetProperty("configurationReady").GetBoolean());
        var definitionFinding = Assert.Single(Findings(review, "definitionFindings"));
        Assert.Equal("review_rule_missing_signature_kind", definitionFinding.GetProperty("code").GetString());
        Assert.Equal("System", definitionFinding.GetProperty("subject").GetString());
        Assert.Contains("requires explicit Review and Approval stages",
            definitionFinding.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var before = await ProjectStateCountsAsync(db);
        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"incomplete-definition-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("cannot_finalize", failure.RootElement.GetProperty("code").GetString());
        Assert.Contains("requires explicit Review and Approval stages",
            failure.RootElement.GetProperty("error").GetString());
        var refusalFinding = Assert.Single(Findings(failure.RootElement));
        Assert.Equal("review_rule_missing_signature_kind", refusalFinding.GetProperty("code").GetString());
        Assert.Equal("System", refusalFinding.GetProperty("subject").GetString());
        Assert.Equal(before, await ProjectStateCountsAsync(db));
        Assert.Equal(ProjectSetupState.Draft,
            (await db.ProjectSetupDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId)).State);
    }

    /// <summary>
    /// C2-01: a stage whose stored authority the workflow authority refuses — here a signature meaning
    /// demanded as a base role — is also a definition problem, diagnosed by rule and stage rather than as an
    /// undifferentiated payload error.
    /// </summary>
    [Fact]
    public async Task A_stage_demanding_an_unconfigurable_authority_is_not_ready_and_names_its_stage()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Unconfigurable authority {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Unconfigurable authority product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = seeded.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!).ToList();
        rules[0]!["stages"]!.AsArray()[0]!["requiredRole"] = "Reviewer";
        rules[0]!["stages"]!.AsArray()[0]!["authorityKind"] = "BaseRole";
        var mutated = new JsonArray(rules.Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray());

        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Unconfigurable authority product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules = mutated },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        var review = saved.GetProperty("validation").GetProperty("review");
        Assert.True(review.GetProperty("covers").GetBoolean());
        Assert.False(review.GetProperty("definitionValid").GetBoolean());
        Assert.False(saved.GetProperty("validation").GetProperty("configurationReady").GetBoolean());
        var finding = Assert.Single(Findings(review, "definitionFindings"));
        Assert.Equal("review_stage_authority_invalid", finding.GetProperty("code").GetString());
        Assert.Equal("System", finding.GetProperty("subject").GetString());
        Assert.Equal(1, finding.GetProperty("stageIndex").GetInt32());
        Assert.Contains("signature meaning", finding.GetProperty("message").GetString());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"authority-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Contains("signature meaning", failure.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, Assert.Single(Findings(failure.RootElement)).GetProperty("stageIndex").GetInt32());
    }

    /// <summary>
    /// C2R-03: a position that is missing, null, fractional or textual is authoring input the maintained
    /// contract never replaced with a valid position — the prior typed payload read a missing position as 0
    /// (refused by the validator) and a null or fractional one as a payload refusal. The draft stays saveable
    /// and readable with a named finding, and finalization stays fail-closed with the same finding.
    /// </summary>
    [Theory]
    [InlineData("missing", "ladder_position_missing")]
    [InlineData("null", "ladder_position_missing")]
    [InlineData("fractional", "ladder_position_unreadable")]
    [InlineData("text", "ladder_position_unreadable")]
    public async Task A_step_without_a_readable_position_is_diagnosed_and_refused(string mutation,
        string expectedCode)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Position {mutation} {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var ladder = JsonNode.Parse(JsonSerializer.Serialize(Ladder(lowLevelKinds: ["Case"])))!;
        var systemStep = ladder["steps"]!.AsArray()[0]!.AsObject();
        switch (mutation)
        {
            case "missing":
                systemStep.Remove("position");
                break;
            case "null":
                systemStep["position"] = null;
                break;
            case "fractional":
                systemStep["position"] = 1.5;
                break;
            default:
                systemStep["position"] = "first";
                break;
        }

        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Position product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { steps = ladder["steps"], relationships = ladder["relationships"] },
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        // Saving and resuming stay usable; the verdict names the position and refuses to call it valid.
        Assert.Equal("Draft", saved.GetProperty("state").GetString());
        var validation = saved.GetProperty("validation");
        Assert.False(validation.GetProperty("ladderValid").GetBoolean());
        Assert.Contains(Findings(validation), x => x.GetProperty("code").GetString() == expectedCode
            && x.GetProperty("field").GetString() == "position" && x.GetProperty("level").GetString() == "System");

        var read = await ReadDraftAsync(client, draftId);
        var stored = read.GetProperty("ladder").GetProperty("steps").EnumerateArray().First();
        switch (mutation)
        {
            case "missing":
                Assert.False(stored.TryGetProperty("position", out _),
                    "the missing position must not be invented on read");
                break;
            case "null":
                Assert.Equal(JsonValueKind.Null, stored.GetProperty("position").ValueKind);
                break;
            case "fractional":
                Assert.Equal(1.5, stored.GetProperty("position").GetDouble());
                break;
            default:
                Assert.Equal("first", stored.GetProperty("position").GetString());
                break;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var before = await ProjectStateCountsAsync(db);
        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"position-{mutation}-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("cannot_finalize", failure.RootElement.GetProperty("code").GetString());
        Assert.Contains(Findings(failure.RootElement),
            x => x.GetProperty("code").GetString() == expectedCode);
        Assert.Equal(before, await ProjectStateCountsAsync(db));
    }

    /// <summary>
    /// C2R-04: the wire contract resolves a subject name case-insensitively and the finalizer compares it the
    /// same way, so the verdict must agree. The same otherwise-valid definition, spelled in lower case, must
    /// read as covered and ready — and must be accepted by the gate it claims readiness for.
    /// </summary>
    [Fact]
    public async Task A_supported_subject_spelling_reads_the_same_in_the_verdict_and_at_the_gate()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Subject casing {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Subject casing product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = seeded.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!).ToList();
        foreach (var rule in rules)
            rule["subject"] = rule["subject"]!.GetValue<string>().ToLowerInvariant();
        var lowerCased = new JsonArray(rules.Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray());

        var accepted = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Subject casing product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules = lowerCased },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        var review = accepted.GetProperty("validation").GetProperty("review");
        Assert.True(review.GetProperty("covers").GetBoolean(), accepted.GetRawText());
        Assert.Empty(Strings(review.GetProperty("missingSubjects")));
        Assert.Empty(Strings(review.GetProperty("unexpectedSubjects")));
        Assert.Empty(Strings(review.GetProperty("duplicateSubjects")));
        Assert.True(review.GetProperty("definitionValid").GetBoolean());
        Assert.True(accepted.GetProperty("validation").GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"subject-casing-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Equal("Completed", result.RootElement.GetProperty("state").GetString());
    }

    /// <summary>
    /// C2R-04: duplicates are identified the way the finalizer identifies subjects — case-insensitively — so a
    /// second spelling of the same subject is a duplicate, not a covered second rule.
    /// </summary>
    [Fact]
    public async Task Differently_cased_duplicate_subjects_are_not_covered()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Subject duplicate {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Subject duplicate product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = seeded.GetProperty("reviewRules").GetProperty("definition").GetProperty("rules")
            .EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!).ToList();
        var duplicate = JsonNode.Parse(rules[0]!.ToJsonString())!;
        duplicate["subject"] = duplicate["subject"]!.GetValue<string>().ToLowerInvariant();
        rules.Add(duplicate);
        var withDuplicate = new JsonArray(rules.Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray());

        var accepted = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Subject duplicate product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules = withDuplicate },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });

        var review = accepted.GetProperty("validation").GetProperty("review");
        Assert.False(review.GetProperty("covers").GetBoolean());
        Assert.Single(Strings(review.GetProperty("duplicateSubjects")));
        Assert.False(accepted.GetProperty("validation").GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"subject-duplicate-{draftId:N}" });
        Assert.Equal(HttpStatusCode.BadRequest, finalized.StatusCode);
        using var failure = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        Assert.Contains("cover each applicable ladder subject exactly once",
            failure.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Compatible_customised_rules_remain_supported_end_to_end()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Custom rules {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var seeded = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "WorkingRules",
            project = new { name = projectName, softwareProduct = "Custom rules product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var rules = new JsonArray(seeded.GetProperty("reviewRules").GetProperty("definition")
            .GetProperty("rules").EnumerateArray()
            .Select(x => JsonNode.Parse(x.GetRawText())!).ToArray());
        // A compatible adjustment: same subjects, the project's own stage name and a supported authority.
        var first = rules[0]!;
        first["name"] = "System change control board";
        var stages = (JsonArray)first["stages"]!;
        stages[0]!["name"] = "Independent system review";
        stages[0]!["requiredRole"] = "SystemEngineer";

        var accepted = await SaveAsync(client, draftId, new
        {
            expectedVersion = 2,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Custom rules product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = Ladder(lowLevelKinds: ["Case"]),
            reviewRules = new { rules },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        Assert.True(accepted.GetProperty("validation").GetProperty("configurationReady").GetBoolean(),
            accepted.GetRawText());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 3, idempotencyKey = $"custom-rules-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var workflow = await db.ReviewWorkflows.AsNoTracking()
            .Include(x => x.Stages)
            .SingleAsync(x => x.ProjectId == projectId && x.AppliesTo == ReviewSubject.System);
        Assert.Equal("System change control board", workflow.Name);
        Assert.Contains(workflow.Stages, x => x.Name == "Independent system review");
    }

    [Fact]
    public async Task A_ladder_with_no_applicable_subjects_is_truthfully_ready_with_an_empty_standard()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"No subjects {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "No subjects product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = new
            {
                steps = new[]
                {
                    new { catalogueEntry = "Customer", position = 1, capabilities = 0,
                        enabledArtifactKinds = Array.Empty<string>() },
                },
                relationships = Array.Empty<object>(),
            },
            reviewRules = new { rules = Array.Empty<object>() },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var validation = saved.GetProperty("validation");
        Assert.True(validation.GetProperty("ladderValid").GetBoolean());
        Assert.Empty(Strings(validation.GetProperty("review").GetProperty("applicableSubjects")));
        Assert.True(validation.GetProperty("review").GetProperty("covers").GetBoolean());
        Assert.True(validation.GetProperty("review").GetProperty("definitionValid").GetBoolean(),
            "the empty standard is a valid concrete definition, not an absent one");
        Assert.True(validation.GetProperty("configurationReady").GetBoolean(), saved.GetRawText());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"no-subjects-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();
        var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.ReviewWorkflows.Where(x => x.ProjectId == projectId).ToListAsync());
    }

    [Fact]
    public async Task New_project_default_finalizes_with_case_and_procedure()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);

        var projectName = $"Default ladder {Guid.NewGuid():N}";
        var draftId = await CreateDraftAsync(client, projectName);
        var saved = await SaveAsync(client, draftId, new
        {
            expectedVersion = 1,
            currentStep = "Review",
            project = new { name = projectName, softwareProduct = "Default ladder product" },
            start = new { kind = "Fresh" },
            build = new { version = "0.01" },
            selectedCategories = Array.Empty<string>(),
            ladder = new { },
            reviewRules = new { },
            reviewRulesAccepted = true,
            repository = new { mode = "ConfigureLater" },
            mapping = new { },
        });
        var steps = saved.GetProperty("validation").GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(["Procedure"],
            Strings(steps.Single(x => x.GetProperty("level").GetString() == "System").GetProperty("effective")));
        Assert.Equal(["Case", "Procedure"],
            Strings(steps.Single(x => x.GetProperty("level").GetString() == "HighLevel").GetProperty("effective")));
        Assert.True(saved.GetProperty("validation").GetProperty("configurationReady").GetBoolean());

        using var finalized = await client.PostAsJsonAsync($"/api/project-setups/{draftId}/finalize",
            new { expectedVersion = 2, idempotencyKey = $"default-ladder-{draftId:N}" });
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await finalized.Content.ReadAsStringAsync());
        var projectId = result.RootElement.GetProperty("projectId").GetGuid();
        var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure },
            (await db.ProjectLadderSteps.AsNoTracking()
                .SingleAsync(x => x.ProjectId == projectId && x.CatalogueEntry == "HighLevel")).EnabledArtifactKinds);
    }

    private static object Ladder(string[]? systemKinds = null, int systemCapabilities = SoftwareCapabilities,
        string[]? highLevelKinds = null, string[]? lowLevelKinds = null, bool highLevelProfileIsNull = false)
    {
        var system = new Dictionary<string, object?>
        {
            ["catalogueEntry"] = "System",
            ["position"] = 1,
            ["capabilities"] = systemCapabilities,
        };
        var highLevel = new Dictionary<string, object?>
        {
            ["catalogueEntry"] = "HighLevel",
            ["position"] = 2,
            ["capabilities"] = SoftwareCapabilities,
        };
        var lowLevel = new Dictionary<string, object?>
        {
            ["catalogueEntry"] = "LowLevel",
            ["position"] = 3,
            ["capabilities"] = LowLevelCapabilities,
        };
        // A null array means "the draft carries no profile property at all", which is a different saved
        // fact from an explicitly empty list and must stay that way through save and resume.
        if (systemKinds is not null) system["enabledArtifactKinds"] = systemKinds;
        if (highLevelKinds is not null) highLevel["enabledArtifactKinds"] = highLevelKinds;
        // An explicit null is a third saved fact: the property is present and carries no value.
        if (highLevelProfileIsNull) highLevel["enabledArtifactKinds"] = null;
        if (lowLevelKinds is not null) lowLevel["enabledArtifactKinds"] = lowLevelKinds;
        return new
        {
            steps = new[] { system, highLevel, lowLevel },
            relationships = new[]
            {
                new { parent = "System", child = "HighLevel" },
                new { parent = "HighLevel", child = "LowLevel" },
            },
        };
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client, string projectName)
    {
        using var created = await client.PostAsJsonAsync("/api/project-setups", new { projectName });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("draftId").GetGuid();
    }

    /// <summary>
    /// Counts every record a completed creation could leave behind. Comparing the whole set before and
    /// after a refusal is stronger than checking that one named project is absent: a partially committed
    /// transaction would move at least one of these counters.
    /// </summary>
    private static async Task<Dictionary<string, int>> ProjectStateCountsAsync(AeroLinkDbContext db) => new()
    {
        ["programs"] = await db.Programs.CountAsync(),
        ["projects"] = await db.Projects.CountAsync(),
        ["releases"] = await db.Releases.CountAsync(),
        ["softwareBuilds"] = await db.SoftwareBuilds.CountAsync(),
        ["candidateBaselines"] = await db.CandidateBaselines.CountAsync(),
        ["ladderConfigurations"] = await db.ProjectLadderConfigurations.CountAsync(),
        ["ladderSteps"] = await db.ProjectLadderSteps.CountAsync(),
        ["ladderAllowedUpstreams"] = await db.ProjectLadderAllowedUpstreams.CountAsync(),
        ["ladderHistory"] = await db.ProjectLadderConfigurationHistories.CountAsync(),
        ["reviewWorkflows"] = await db.ReviewWorkflows.CountAsync(),
        ["reviewWorkflowStages"] = await db.Set<ReviewWorkflowStage>().CountAsync(),
        ["reviewCycles"] = await db.ReviewCycles.CountAsync(),
        ["procedureDocuments"] = await db.TestProcedureDocuments.CountAsync(),
        ["procedureDocumentNodes"] = await db.TestProcedureDocumentNodes.CountAsync(),
        ["repositoryConfigurations"] = await db.ProjectRepositoryConfigurations.CountAsync(),
        ["verificationVocabularies"] = await db.ProjectVerificationVocabularies.CountAsync(),
        ["verificationMethods"] = await db.ProjectVerificationMethods.CountAsync(),
        ["memberships"] = await db.ProgramMemberships.CountAsync(),
        ["requirements"] = await db.Requirements.CountAsync(),
        ["testProcedures"] = await db.TestProcedures.CountAsync(),
        ["auditEvents"] = await db.SecurityAuditEvents.CountAsync(),
    };

    private static async Task<JsonElement> SaveAsync(HttpClient client, Guid draftId, object payload)
    {
        using var saved = await client.PutAsJsonAsync($"/api/project-setups/{draftId}", payload);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadDraftAsync(HttpClient client, Guid draftId)
    {
        using var resumed = await client.GetAsync($"/api/project-setups/{draftId}");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        using var document = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static JsonElement[] Findings(JsonElement owner) => owner.GetProperty("findings")
        .EnumerateArray().Select(x => x.Clone()).ToArray();

    private static JsonElement[] Findings(JsonElement owner, string property) => owner.GetProperty(property)
        .EnumerateArray().Select(x => x.Clone()).ToArray();

    private static string[] SubjectsOf(JsonElement definition) => definition.GetProperty("rules")
        .EnumerateArray().Select(x => x.GetProperty("subject").GetString() ?? string.Empty).ToArray();

    private static string[] Strings(JsonElement array) => array.ValueKind == JsonValueKind.Null
        ? []
        : array.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

    /// <summary>
    /// Reads the resume payload back into the exact stored shape save/resume must preserve. An omitted
    /// profile is reported as <c>&lt;absent&gt;</c> so "never supplied" cannot be confused with "supplied
    /// empty" — the distinction the whole correction turns on.
    /// </summary>
    private static string StoredLadderOf(JsonElement draft) => string.Join("|", draft
        .GetProperty("ladder").GetProperty("steps").EnumerateArray()
        .OrderBy(x => x.GetProperty("position").GetInt32())
        .Select(step => $"{step.GetProperty("catalogueEntry").GetString()}:" +
            $"{step.GetProperty("capabilities").GetInt32()}:" +
            (step.TryGetProperty("enabledArtifactKinds", out var kinds)
                && kinds.ValueKind == JsonValueKind.Array
                    ? string.Join(",", kinds.EnumerateArray().Select(kind => kind.GetString()))
                    : "<absent>")));
}
