using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ChangeAuthoringInvariantApiTests
{
    private sealed record Scenario(Guid ProjectId, Guid ReleaseId, Guid SystemSectionId, Guid HlrSectionId);

    [Fact]
    public async Task Authored_attributes_and_sections_survive_creation_and_server_owned_derived_cannot_be_spoofed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);
        var complete = RequirementAuthoringJson.CompleteImpactDispositions;

        using var systemResponse = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Persist system metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall retain metadata.",
                    rationale = "Controlled ownership", verificationMethod = "Test",
                    attributesJson = """{"owner":"systems.author","criticality":"Mission Critical","derived":true}""",
                    impactDispositionJson = complete, isDerived = true, targetSectionId = scenario.SystemSectionId }
            }
        });
        var systemBody = await systemResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, systemResponse.StatusCode);
        var system = JsonSerializer.Deserialize<JsonElement>(systemBody);
        var systemChange = system.GetProperty("requirementChanges")[0];
        Assert.Equal(scenario.SystemSectionId, systemChange.GetProperty("targetSectionId").GetGuid());
        using var systemAttributes = JsonDocument.Parse(systemChange.GetProperty("attributesJson").GetString()!);
        Assert.Equal("systems.author", systemAttributes.RootElement.GetProperty("owner").GetString());
        Assert.Equal("Mission Critical", systemAttributes.RootElement.GetProperty("criticality").GetString());
        Assert.False(systemAttributes.RootElement.TryGetProperty("derived", out _));
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "SCR", artifactId = system.GetProperty("id").GetGuid(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var checkedOut = JsonSerializer.Deserialize<JsonElement>(await checkout.Content.ReadAsStringAsync());
        using var recovery = JsonDocument.Parse(checkedOut.GetProperty("draftJson").GetString()!);
        Assert.Equal(scenario.SystemSectionId, recovery.RootElement.GetProperty("requirementChanges")[0]
            .GetProperty("targetSectionId").GetGuid());

        using var softwareResponse = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            title = "Persist software metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "HighLevel", kind = "Introduce", statement = "The software shall retain metadata.",
                    rationale = "Controlled ownership", verificationMethod = "Test",
                    attributesJson = """{"owner":"software.author","criticality":"Safety Significant","derived":false}""",
                    impactDispositionJson = complete, isDerived = true, targetSectionId = scenario.HlrSectionId }
            }
        });
        var softwareBody = await softwareResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, softwareResponse.StatusCode);
        var software = JsonSerializer.Deserialize<JsonElement>(softwareBody);
        var softwareChange = software.GetProperty("requirementChanges")[0];
        using var softwareAttributes = JsonDocument.Parse(softwareChange.GetProperty("attributesJson").GetString()!);
        Assert.Equal("software.author", softwareAttributes.RootElement.GetProperty("owner").GetString());
        Assert.True(softwareAttributes.RootElement.GetProperty("derived").GetBoolean());

        using var invalid = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Reject unknown metadata", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall reject unknown metadata.",
                    rationale = "Schema authority", verificationMethod = "Test",
                    attributesJson = """{"owner":"systems.author","invented":"not allowed"}""" }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("not allowed by the System Requirement schema", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_authored_attributes_are_reported_without_rewriting_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        Guid changeRequestId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var scr = new SystemChangeRequest("SRCR-00999", 0, scenario.ProjectId, scenario.ReleaseId,
                "Legacy gap", "P", "A", "S", "invariant.author", DateTimeOffset.UtcNow);
            scr.AddRequirementChange("invariant.author", "SYSR-00000999", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall expose a legacy gap.", "R", "Test",
                DateTimeOffset.UtcNow, attributesJson: "{}", impactDispositionJson: "{}");
            seedDb.Add(scr);
            await seedDb.SaveChangesAsync();
            changeRequestId = scr.Id;
        }
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/attribute-gaps?projectId={scenario.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(rows.EnumerateArray(), x => x.GetProperty("id").GetGuid() == changeRequestId);
        // `owner` was dropped from this expectation by #1016 S01: the per-requirement Author input is gone, so
        // an owner gap is one nobody can close. The row still exists and still reports criticality — dropping
        // the expectation must not make a genuinely incomplete proposal disappear from the report.
        Assert.Equal(new[] { "criticality" },
            row.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal($"scr:{changeRequestId}", row.GetProperty("reconciliation").GetString());

        using var checkpointResponse = await client.PostAsJsonAsync(
            "/api/enterprise-hardening/integrity-checkpoints", new { projectId = scenario.ProjectId });
        Assert.Equal(HttpStatusCode.Created, checkpointResponse.StatusCode);
        var checkpoint = JsonSerializer.Deserialize<JsonElement>(await checkpointResponse.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", checkpoint.GetProperty("state").GetString());
        Assert.DoesNotContain("impact-disposition", checkpoint.GetProperty("detail").GetString());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("{}", (await verificationDb.RequirementChanges.SingleAsync(x => x.ChangeRequestId == changeRequestId)).AttributesJson);
    }

    /// <summary>
    /// #1016 S01. A requirement proposal has no author of its own — the change request records who wrote it —
    /// so the per-requirement Author input was removed and `owner` is no longer an expected attribute.
    ///
    /// Two things have to hold at once, and they pull in opposite directions. An absent owner must stop being
    /// reported, because there is no supported way left to supply one and a gap nobody can close is not a
    /// gap. But dropping that expectation must not quietly take other gaps with it, and it must not touch a
    /// single owner value already recorded: those were authored by somebody, under attribution, and this
    /// change removes a question rather than anybody's answer to it.
    /// </summary>
    [Fact]
    public async Task Owner_is_no_longer_expected_and_recorded_owner_values_are_left_exactly_as_stored()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        Guid completeId, criticalityOnlyGapId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();

            // Criticality recorded, no owner. Under the old expectation this was a gap row; it is not one now.
            var complete = new SystemChangeRequest("SRCR-00997", 0, scenario.ProjectId, scenario.ReleaseId,
                "No owner recorded", "P", "A", "S", "invariant.author", DateTimeOffset.UtcNow);
            complete.AddRequirementChange("invariant.author", "SYSR-00000997", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall record no owner.", "R", "Test",
                DateTimeOffset.UtcNow, attributesJson: """{"criticality":"Normal"}""",
                impactDispositionJson: "{}");

            // A legacy owner, and a genuinely missing criticality. The row must survive with criticality alone.
            var legacy = new SystemChangeRequest("SRCR-00998", 0, scenario.ProjectId, scenario.ReleaseId,
                "Legacy owner recorded", "P", "A", "S", "invariant.author", DateTimeOffset.UtcNow);
            legacy.AddRequirementChange("invariant.author", "SYSR-00000998", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall retain a legacy owner.", "R", "Test",
                DateTimeOffset.UtcNow, attributesJson: """{"owner":"legacy.author"}""",
                impactDispositionJson: "{}");

            seedDb.AddRange(complete, legacy);
            await seedDb.SaveChangesAsync();
            completeId = complete.Id;
            criticalityOnlyGapId = legacy.Id;
        }
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/attribute-gaps?projectId={scenario.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .EnumerateArray().ToList();

        // No owner, and no gap: the expectation is gone, not merely reordered.
        Assert.DoesNotContain(rows, x => x.GetProperty("id").GetGuid() == completeId);
        Assert.DoesNotContain(rows, x => x.GetProperty("missing").EnumerateArray()
            .Any(key => key.GetString() == "owner"));

        // The other gap is still reported on its own.
        var gap = Assert.Single(rows, x => x.GetProperty("id").GetGuid() == criticalityOnlyGapId);
        Assert.Equal(new[] { "criticality" },
            gap.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToArray());

        // Nothing was backfilled, blanked or rewritten. The stored JSON is byte-for-byte what was recorded.
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("""{"criticality":"Normal"}""",
            (await verificationDb.RequirementChanges.SingleAsync(x => x.ChangeRequestId == completeId)).AttributesJson);
        Assert.Equal("""{"owner":"legacy.author"}""",
            (await verificationDb.RequirementChanges.SingleAsync(x => x.ChangeRequestId == criticalityOnlyGapId)).AttributesJson);

        // And the integrity checkpoint is unmoved by the change of expectation.
        using var checkpointResponse = await client.PostAsJsonAsync(
            "/api/enterprise-hardening/integrity-checkpoints", new { projectId = scenario.ProjectId });
        Assert.Equal(HttpStatusCode.Created, checkpointResponse.StatusCode);
        var checkpoint = JsonSerializer.Deserialize<JsonElement>(await checkpointResponse.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", checkpoint.GetProperty("state").GetString());
    }

    /// <summary>
    /// #1016 S01. `owner` stops being asked for; it does not stop being accepted. A caller that still sends
    /// one — an import, an integration, an older client — must be able to save and read it back unchanged,
    /// because the key remains part of the System Requirement schema and the Explorer's owner filter and the
    /// saved views built on it still read it.
    /// </summary>
    [Fact]
    public async Task A_supplied_owner_attribute_is_still_accepted_and_survives_a_save_and_reopen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);

        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Owner still accepted", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce",
                    statement = "The FMS shall keep a supplied owner attribute.",
                    rationale = "Saved-view compatibility", verificationMethod = "Test",
                    attributesJson = """{"criticality":"Normal","owner":"systems.author"}""",
                    impactDispositionJson = RequirementAuthoringJson.CompleteImpactDispositions,
                    targetSectionId = scenario.SystemSectionId }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());
        var changeRequestId = draft.GetProperty("id").GetGuid();

        // Reopened through the ordinary read, which is what the workspace does when the author comes back.
        using var reopened = await client.GetAsync($"/api/change-requests/{changeRequestId}");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await reopened.Content.ReadAsStringAsync());
        var attributes = JsonSerializer.Deserialize<JsonElement>(
            body.GetProperty("requirementChanges")[0].GetProperty("attributesJson").GetString()!);
        Assert.Equal("systems.author", attributes.GetProperty("owner").GetString());
        Assert.Equal("Normal", attributes.GetProperty("criticality").GetString());

        // Present in the record, and therefore absent from the gap report — for criticality's sake, not owner's.
        using var gaps = await client.GetAsync($"/api/authoring/attribute-gaps?projectId={scenario.ProjectId}");
        var gapRows = JsonSerializer.Deserialize<JsonElement>(await gaps.Content.ReadAsStringAsync());
        Assert.DoesNotContain(gapRows.EnumerateArray(), x => x.GetProperty("id").GetGuid() == changeRequestId);
    }

    [Fact]
    public async Task Direct_api_submission_does_not_require_author_owned_impact_dispositions()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "System",
            title = "Incomplete impacts", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "System", kind = "Introduce", statement = "The FMS shall require dispositions.",
                    rationale = "Lifecycle integrity", verificationMethod = "Test",
                    impactDispositionJson = "{}", targetSectionId = scenario.SystemSectionId }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());

        using var submitted = await client.PostAsJsonAsync($"/api/change-requests/{draft.GetProperty("id").GetGuid()}/submit",
            new { actorId = "invariant.author", expectedVersion = draft.GetProperty("version").GetInt64(), mode = "Sequential",
                approvers = new[] { new { userId = "invariant.reviewer", name = "Caller supplied name" } } });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
    }

    [Theory]
    [InlineData("System", "HighLevel", "only System requirement changes")]
    [InlineData("Software", "System", "must declare HLR or LLR scope")]
    public async Task Change_request_type_rejects_incompatible_requirement_level(
        string type, string level, string expectedGuidance)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type,
            title = "Reject incompatible level", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level, kind = "Introduce", statement = "The product shall reject incompatible work.",
                    rationale = "Controlled causality", verificationMethod = "Test", isDerived = true }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedGuidance, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await db.SystemChangeRequests.AnyAsync(x => x.Title == "Reject incompatible level"));
    }

    [Fact]
    public async Task A_draft_scoped_to_the_LLR_workspace_refuses_an_HLR_proposal_identity()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);

        // The F1 reproduction: a proposal opened in the LLR workspace whose identity was silently
        // re-levelled to an HLR. Whether it arrives through the picker or a crafted call, the draft is
        // scoped to LLR change control and the aggregate must refuse the cross-level identity rather
        // than save a package whose workspace and content disagree.
        using var crafted = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            softwareLevel = "LowLevel",
            title = "Reject cross-level proposal", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "HighLevel", kind = "Introduce", statement = "The software shall refuse cross-level proposals.",
                    rationale = "Controlled identity boundary", verificationMethod = "Test", isDerived = true }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, crafted.StatusCode);
        Assert.Contains("belongs to the LLR workspace", await crafted.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.False(await db.SystemChangeRequests.AnyAsync(x => x.Title == "Reject cross-level proposal"));
        }

        // The same shape as the observed defect, as a modification naming an existing HLR identity: the
        // artifact-level checks agree (the proposal names an HLR and declares HLR), so it is exactly the
        // proposal-versus-workspace scope disagreement that the aggregate boundary must catch.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var origin = new SystemChangeRequest("SRCR-00888", 0, scenario.ProjectId, scenario.ReleaseId,
                "Level boundary origin", "P", "A", "S", "invariant.author", now);
            var baseline = new CandidateBaseline("SW-88.00", 0, scenario.ProjectId, scenario.ReleaseId, null,
                "Level boundary baseline", "cm", now);
            var hlr = new RequirementArtifact(scenario.ProjectId, "HLR-000733", RequirementLevel.HighLevel, now);
            db.AddRange(origin, baseline, hlr, new RequirementRevision(hlr.Id, 0,
                "The software shall keep its declared workspace.", "Rationale", "Test",
                RequirementRevisionState.Active, origin.Id, baseline.Id, now,
                RequirementParentKind.Derived, "Boundary fixture derives without a recorded parent."));
            await db.SaveChangesAsync();
        }
        using var craftedModify = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            softwareLevel = "LowLevel",
            title = "Reject cross-level modification", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { baseNumber = "HLR-000733", revision = 1, level = "HighLevel", kind = "Modify",
                    statement = "The software shall keep its declared workspace, modified.",
                    rationale = "Controlled identity boundary", verificationMethod = "Test" }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, craftedModify.StatusCode);
        Assert.Contains("belongs to the LLR workspace", await craftedModify.Content.ReadAsStringAsync());

        // The honest counterpart: the same LLR-scoped draft with an LLR identity saves normally.
        using var honest = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            softwareLevel = "LowLevel",
            title = "Accept matching level proposal", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "LowLevel", kind = "Introduce", statement = "The software shall accept its own level.",
                    rationale = "Controlled identity boundary", verificationMethod = "Test", isDerived = true }
            }
        });
        Assert.Equal(HttpStatusCode.Created, honest.StatusCode);
    }

    [Fact]
    public async Task Adding_a_proposal_to_an_existing_LLR_draft_refuses_an_HLR_identity()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client);
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId = scenario.ProjectId, targetReleaseId = scenario.ReleaseId, type = "Software",
            softwareLevel = "LowLevel",
            title = "LLR draft for extension", problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new { level = "LowLevel", kind = "Introduce", statement = "The software shall hold one proposal.",
                    rationale = "Controlled identity boundary", verificationMethod = "Test", isDerived = true }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());
        var draftId = draft.GetProperty("id").GetGuid();

        using var crafted = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/requirements", new
        {
            baseNumber = "", revision = 0, level = "HighLevel", kind = "Introduce",
            statement = "The software shall refuse a foreign-level extension.",
            rationale = "Controlled identity boundary", verificationMethod = "Test"
        });
        Assert.Equal(HttpStatusCode.BadRequest, crafted.StatusCode);
        Assert.Contains("belongs to the LLR workspace", await crafted.Content.ReadAsStringAsync());

        using var honest = await client.PostAsJsonAsync($"/api/change-requests/{draftId}/requirements", new
        {
            baseNumber = "", revision = 0, level = "LowLevel", kind = "Introduce",
            statement = "The software shall accept a matching-level extension.",
            rationale = "Controlled identity boundary", verificationMethod = "Test"
        });
        Assert.Equal(HttpStatusCode.OK, honest.StatusCode);
    }

    [Fact]
    public async Task Legacy_impact_disposition_metadata_does_not_block_selection_or_freeze()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        Guid selectedBaselineId;
        Guid emptyBaselineId;
        Guid selectionScrId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var scr = new SystemChangeRequest("SRCR-00888", 0, scenario.ProjectId, scenario.ReleaseId,
                "Legacy invalid impacts", "P", "A", "S", "invariant.author", now);
            scr.AddRequirementChange("invariant.author", "SYSR-00000888", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall reject legacy invalid impacts.", "R", "Test", now);
            scr.SubmitForReview("invariant.author", [new("invariant.reviewer", "Invariant Reviewer")], now);
            scr.ApproveActiveStage("invariant.reviewer", now);
            var selected = new CandidateBaseline("SW-88.80", 0, scenario.ProjectId, scenario.ReleaseId,
                null, "Selected legacy record", "invariant.author", now);
            selected.Select(scr, "invariant.author", now);
            var selectionScr = new SystemChangeRequest("SRCR-00889", 0, scenario.ProjectId, scenario.ReleaseId,
                "Second legacy invalid impact record", "P", "A", "S", "invariant.author", now);
            selectionScr.AddRequirementChange("invariant.author", "SYSR-00000889", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall allow downstream impact assessment.", "R", "Test", now);
            selectionScr.SubmitForReview("invariant.author", [new("invariant.reviewer", "Invariant Reviewer")], now);
            selectionScr.ApproveActiveStage("invariant.reviewer", now);
            var empty = new CandidateBaseline("SW-88.90", 0, scenario.ProjectId, scenario.ReleaseId,
                null, "Selection guard", "invariant.author", now);
            db.AddRange(scr, selectionScr, selected, empty);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE requirement_changes SET ImpactDispositionJson = '{{}}' WHERE ChangeRequestId = {scr.Id} OR ChangeRequestId = {selectionScr.Id}");
            selectedBaselineId = selected.Id;
            emptyBaselineId = empty.Id;
            selectionScrId = selectionScr.Id;
        }
        await SignInAsync(client);

        using var selection = await client.PostAsJsonAsync($"/api/baselines/{emptyBaselineId}/selections",
            new { changeRequestId = selectionScrId, actorId = "invariant.author" });
        Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        using var freeze = await client.PostAsJsonAsync($"/api/baselines/{selectedBaselineId}/freeze",
            new { actorId = "invariant.author" });
        Assert.Equal(HttpStatusCode.OK, freeze.StatusCode);

        // The lifecycle gate that matters: DEC-071 removed the former five impact dispositions from
        // review submission, baseline selection/freeze/materialization and integrity checkpoints, and
        // existing stored disposition data remains historical. A legacy "{}" value (what the introducing
        // migration's default wrote into pre-existing rows) must therefore not block materialization.
        using var materialized = await client.PostAsJsonAsync(
            $"/api/baselines/{selectedBaselineId}/materialize-requirements", new { });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);

        var baselineAfter = await client.GetFromJsonAsync<JsonElement>(
            $"/api/baselines/{selectedBaselineId}");
        Assert.NotEqual(JsonValueKind.Null, baselineAfter.GetProperty("requirementsHash").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, baselineAfter.GetProperty("requirementsMaterializedAt").ValueKind);

        // The materialized requirement is the exact governed revision the legacy change introduced.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var materializedSelection = await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == selectedBaselineId)
                .Select(x => new { x.ArtifactId, x.RevisionId }).SingleAsync();
            var artifact = await db.Requirements.AsNoTracking()
                .SingleAsync(x => x.Id == materializedSelection.ArtifactId);
            var revision = await db.RequirementRevisions.AsNoTracking()
                .SingleAsync(x => x.Id == materializedSelection.RevisionId);
            Assert.Equal("SYSR-00000888", artifact.BaseNumber);
            Assert.Equal("The FMS shall reject legacy invalid impacts.", revision.Statement);
            Assert.Equal(RequirementRevisionState.Active, revision.State);
        }
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Invariant Program", "IVP");
        var project = new ProjectRecord(program.Id, "Invariant Project", "Invariant Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var account = new UserAccount("invariant.author", "Invariant Author", "invariant@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var reviewer = new UserAccount("invariant.reviewer", "Invariant Reviewer", "reviewer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, account, reviewer,
            new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(account.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                account.Id, "test.setup", now),
            new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Reviewer, "test.setup", now),
            new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Approver, "test.setup", now));
        await db.SaveChangesAsync();
        await new EnterpriseRequirementsService(db).SynchronizeProjectAsync(project.Id, account.UserName);
        var sections = await (from node in db.SpecificationNodes
                              join specification in db.RequirementSpecifications on node.SpecificationId equals specification.Id
                              where specification.ProjectId == project.Id && node.Type == SpecificationNodeType.Section
                              select new { node.Id, specification.Level }).ToListAsync();
        return new(project.Id, release.Id,
            sections.First(x => x.Level == RequirementLevel.System.ToString()).Id,
            sections.First(x => x.Level == RequirementLevel.HighLevel.ToString()).Id);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "invariant.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
