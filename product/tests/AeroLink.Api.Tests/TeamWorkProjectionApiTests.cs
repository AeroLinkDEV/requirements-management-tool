using System.Net;
using System.Net.Http.Json;
using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Phase-1 evidence for the Team Work read projection. The fixtures use disposable SQLite and write no
/// product/demo state. Lifecycle values are deliberately varied through the domain objects, while a tiny
/// reflection seam is used only to place the matrix at each persisted state without exercising unrelated API
/// authoring workflows in every row.
/// </summary>
public sealed class TeamWorkProjectionApiTests
{
    [Fact]
    public async Task Project_projection_is_authorized_and_does_not_leak_a_foreign_project()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedBaseAsync(factory);
        await SignInAsync(client, fixture.Viewer);

        using var authorized = await client.GetAsync($"/api/team-work?projectId={fixture.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var body = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(items, item => item.GetProperty("title").GetString() == "Foreign project record");
        Assert.DoesNotContain(body.RootElement.GetProperty("people").EnumerateArray(),
            person => person.GetProperty("userName").GetString() == fixture.Outsider);

        using var outsider = factory.CreateClient();
        await SignInAsync(outsider, fixture.Outsider);
        using var forbidden = await outsider.GetAsync($"/api/team-work?projectId={fixture.ProjectId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var crossProject = await client.GetAsync($"/api/team-work?projectId={fixture.ForeignProjectId}");
        Assert.Equal(HttpStatusCode.Forbidden, crossProject.StatusCode);
    }

    [Fact]
    public async Task Project_projection_covers_lanes_holders_revision_and_cross_family_truth()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedMatrixAsync(factory);
        await SignInAsync(client, fixture.Viewer);

        using var response = await client.GetAsync($"/api/team-work?projectId={fixture.ProjectId}");
        var json = await ReadSuccessAsync(response);
        var root = json.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        var byId = items.ToDictionary(x => x.GetProperty("id").GetGuid());

        Assert.Equal(items.Count, root.GetProperty("totals").GetProperty("items").GetInt32());
        Assert.Equal(items.Count, root.GetProperty("totals").GetProperty("returned").GetInt32());
        Assert.Equal(items.Count(item => item.GetProperty("currentHolderIds").GetArrayLength() == 0),
            root.GetProperty("totals").GetProperty("unheld").GetInt32());
        Assert.All(items, item => Assert.True(item.TryGetProperty("nativeState", out _)));
        Assert.All(items, item => Assert.True(item.TryGetProperty("openUrl", out _)));
        Assert.DoesNotContain("ownerId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // #868 exposes server-owned contextual facets. The returned item classification is the same
        // authoritative identity used to build those facets; a client must not guess family combinations.
        var layers = root.GetProperty("layers").EnumerateArray().ToList();
        Assert.Contains(layers, layer => layer.GetProperty("id").GetString() == "System");
        Assert.Contains(layers, layer => layer.GetProperty("id").GetString() == "HighLevel");
        Assert.Contains(layers, layer => layer.GetProperty("id").GetString() == "LowLevel");
        Assert.Contains(root.GetProperty("artifactTypes").EnumerateArray(),
            facet => facet.GetProperty("id").GetString() == "SRCR");
        Assert.Contains(root.GetProperty("artifactTypes").EnumerateArray(),
            facet => facet.GetProperty("id").GetString() == "HLRTCCR");
        Assert.All(items, item =>
        {
            Assert.True(item.TryGetProperty("artifactType", out var artifactType));
            Assert.False(string.IsNullOrWhiteSpace(artifactType.GetString()));
        });

        AssertItem(byId, fixture.CrDraft, "work", [fixture.Author], "author", "Draft");
        AssertItem(byId, fixture.CrReview, "review", [fixture.Reviewer], "activeReviewStage", "InReview");
        AssertStageKinds(byId[fixture.CrReview], [(fixture.Reviewer, "review")]);
        AssertItem(byId, fixture.CrApproval, "sign", [fixture.FrozenHolder], "activeApprovalStage", "InReview");
        AssertStageKinds(byId[fixture.CrApproval], [(fixture.FrozenHolder, "approval")]);
        AssertItem(byId, fixture.CrMixed, "sign", [fixture.Reviewer, fixture.FrozenHolder],
            "activeReviewAndApprovalStages", "InReview");
        AssertStageKinds(byId[fixture.CrMixed], [(fixture.Reviewer, "review"), (fixture.FrozenHolder, "approval")]);
        AssertItem(byId, fixture.CrZeroSteps, "review", [], "none", "InReview");
        AssertItem(byId, fixture.CrApproved, "approved", [], "none", "Approved");
        AssertItem(byId, fixture.CrSelectedUnreleased, "approved", [], "none", "SelectedForBaseline");
        var selectedUnreleased = byId[fixture.CrSelectedUnreleased];
        Assert.Equal(fixture.ReleaseA, selectedUnreleased.GetProperty("release").GetProperty("id").GetGuid());
        Assert.False(selectedUnreleased.GetProperty("release").GetProperty("isReleased").GetBoolean());
        var selectedAllocation = selectedUnreleased.GetProperty("allocation");
        Assert.NotEqual(JsonValueKind.Null, selectedAllocation.ValueKind);
        Assert.Equal(fixture.ReleaseA, selectedAllocation.GetProperty("releaseId").GetGuid());
        Assert.False(selectedAllocation.GetProperty("isReleased").GetBoolean());
        AssertItem(byId, fixture.CrApprovedReleaseB, "approved", [], "none", "Approved");
        AssertItem(byId, fixture.CrDeferredDraft, "work", [], "none", "Deferred", deferred: true);
        AssertItem(byId, fixture.CrDeferredReview, "sign", [], "none", "Deferred", deferred: true);
        AssertItem(byId, fixture.CrDeferredApproved, "approved", [], "none", "Deferred", deferred: true);
        Assert.DoesNotContain(fixture.CrDeferredUnknown, byId.Keys);
        AssertItem(byId, fixture.CrReturnedDraft, "work", [fixture.Author], "author", "Draft");
        Assert.DoesNotContain(fixture.CrSelectedReleased, byId.Keys);
        Assert.DoesNotContain(fixture.CrWithdrawn, byId.Keys);
        Assert.DoesNotContain(fixture.CrSupersededOld, byId.Keys);
        Assert.Contains(fixture.CrSupersededCurrent, byId.Keys);

        var representedReleases = items
            .Where(item => item.GetProperty("release").ValueKind != JsonValueKind.Null)
            .Select(item => item.GetProperty("release").GetProperty("id").GetGuid())
            .ToHashSet();
        Assert.Contains(fixture.ReleaseA, representedReleases);
        Assert.Contains(fixture.ReleaseB, representedReleases);

        AssertItem(byId, fixture.TcrDraft, "work", [fixture.Assigned], "assignedEngineer", "Draft");
        Assert.Equal("Pending", NullableString(byId[fixture.TcrDraft], "nativeOutcome"));
        AssertItem(byId, fixture.TcrNullAssigned, "work", [], "assignedEngineer", "Draft");
        AssertItem(byId, fixture.TcrReview, "review", [fixture.Reviewer], "activeReviewStage", "InReview");
        AssertStageKinds(byId[fixture.TcrReview], [(fixture.Reviewer, "review")]);
        AssertItem(byId, fixture.TcrApproval, "sign", [fixture.FrozenHolder], "activeApprovalStage", "InReview");
        AssertStageKinds(byId[fixture.TcrApproval], [(fixture.FrozenHolder, "approval")]);
        Assert.Equal("Pending", NullableString(byId[fixture.TcrApproval], "nativeOutcome"));
        AssertItem(byId, fixture.TcrApproved, "approved", [], "none", "Approved");
        AssertItem(byId, fixture.TcrDeferredReview, "sign", [], "none", "Deferred", deferred: true);
        Assert.DoesNotContain(fixture.TcrDeferredUnknown, byId.Keys);
        Assert.DoesNotContain(fixture.TcrSuperseded, byId.Keys);
        Assert.DoesNotContain(fixture.TcrIncorporated, byId.Keys);
        Assert.DoesNotContain(fixture.TcrUnnumbered, byId.Keys);
        Assert.Contains(fixture.TcrProcedureSystem, byId.Keys);
        Assert.Contains(fixture.TcrProcedureHigh, byId.Keys);
        Assert.Contains(fixture.TcrProcedureLow, byId.Keys);
        Assert.Equal("SYSTPCR", byId[fixture.TcrProcedureSystem].GetProperty("prefix").GetString());
        Assert.Equal("HLRTPCR", byId[fixture.TcrProcedureHigh].GetProperty("prefix").GetString());
        Assert.Equal("LLRTPCR", byId[fixture.TcrProcedureLow].GetProperty("prefix").GetString());
        Assert.DoesNotContain(fixture.TcrLatestOld, byId.Keys);
        Assert.Contains(fixture.TcrLatestNew, byId.Keys);

        var automatic = byId[fixture.TcrAutomatic];
        Assert.Null(NullableString(automatic, "raisedById"));
        Assert.Equal("problemReport", NullableString(automatic, "raisedByKind"));
        Assert.Equal("HLRTCCR", NullableString(automatic, "prefix"));

        AssertItem(byId, fixture.PrDraft, "work", [fixture.Responsible], "responsibleEngineer", "Draft");
        AssertItem(byId, fixture.PrSccb, "review", [], "none", "ReadyForSccb");
        AssertItem(byId, fixture.PrOpen, "work", [fixture.Responsible], "responsibleEngineer", "Open");
        AssertItem(byId, fixture.PrDeferred, "work", [fixture.Responsible], "responsibleEngineer", "Open", deferred: true);
        Assert.Equal("Deferred", NullableString(byId[fixture.PrDeferred], "nativeOutcome"));
        AssertItem(byId, fixture.PrImplementing, "work", [fixture.Responsible], "responsibleEngineer", "Implementing");
        AssertItem(byId, fixture.PrVerifying, "work", [fixture.Responsible], "responsibleEngineer", "Verifying");
        AssertItem(byId, fixture.PrSqa, "sign", [], "none", "WaitingForSqaToClose");
        Assert.DoesNotContain(fixture.PrClosed, byId.Keys);
        Assert.DoesNotContain(fixture.PrRejected, byId.Keys);

        AssertItem(byId, fixture.AssessmentOpenPending, "work", [fixture.Assigned], "assignedEngineer", "Open");
        Assert.Equal(fixture.AssessmentOpenPendingSource.ToString(), NullableString(byId[fixture.AssessmentOpenPending], "raisedById"));
        Assert.Equal("changeRequest", NullableString(byId[fixture.AssessmentOpenPending], "raisedByKind"));
        AssertItem(byId, fixture.AssessmentOpenChange, "work", [fixture.Assigned], "assignedEngineer", "Open");
        AssertItem(byId, fixture.AssessmentOpenNoChange, "work", [fixture.Assigned], "assignedEngineer", "Open");
        Assert.DoesNotContain(fixture.AssessmentOpenLinked, byId.Keys);
        AssertItem(byId, fixture.AssessmentReview, "review", [fixture.AssessmentApprover],
            "selectedAssessmentApprover", "InReview");
        Assert.Empty(byId[fixture.AssessmentReview].GetProperty("activeStageObligations").EnumerateArray());
        AssertItem(byId, fixture.AssessmentApproved, "approved", [], "none", "Approved");
        Assert.DoesNotContain(fixture.AssessmentApprovedLinked, byId.Keys);
        Assert.DoesNotContain(fixture.AssessmentSuperseded, byId.Keys);

        var release = byId[fixture.CrDraft].GetProperty("release");
        Assert.Equal(fixture.ReleaseA, release.GetProperty("id").GetGuid());
        Assert.Equal("1.6", release.GetProperty("version").GetString());
        Assert.Equal("SRCR", byId[fixture.CrDraft].GetProperty("prefix").GetString());
        Assert.Equal("HLR assessment", byId[fixture.AssessmentOpenPending].GetProperty("category").GetString());
        Assert.Null(NullableString(byId[fixture.AssessmentOpenPending], "number"));
        Assert.Null(NullableString(byId[fixture.AssessmentOpenPending], "prefix"));
        Assert.Equal("Pending", NullableString(byId[fixture.AssessmentOpenPending], "nativeOutcome"));
        Assert.Equal("Assessment", byId[fixture.AssessmentSystem].GetProperty("category").GetString());
        Assert.Equal("LLR assessment", byId[fixture.AssessmentLow].GetProperty("category").GetString());
        Assert.Equal($"/open/change-request/{fixture.CrDraft}", byId[fixture.CrDraft].GetProperty("openUrl").GetString());
        Assert.Equal($"/open/test-change-request/{fixture.TcrDraft}", byId[fixture.TcrDraft].GetProperty("openUrl").GetString());
        Assert.Equal($"/open/problem-report/{fixture.PrDraft}", byId[fixture.PrDraft].GetProperty("openUrl").GetString());
        Assert.Equal($"/open/downstream-assessment/{fixture.AssessmentOpenPending}", byId[fixture.AssessmentOpenPending].GetProperty("openUrl").GetString());
        var assessmentPairs = items.Where(item => item.GetProperty("family").GetString() == "assessment")
            .Select(item => (State: item.GetProperty("nativeState").GetString()!, Outcome: item.GetProperty("nativeOutcome").GetString()!))
            .ToHashSet();
        Assert.Contains(("Open", "Pending"), assessmentPairs);
        Assert.Contains(("Open", "ChangeRequired"), assessmentPairs);
        Assert.Contains(("Open", "NoChangeRequired"), assessmentPairs);
        Assert.DoesNotContain(("Open", "ChangeRequestsLinked"), assessmentPairs);
        Assert.Contains(("InReview", "Pending"), assessmentPairs);
        Assert.Contains(("InReview", "ChangeRequired"), assessmentPairs);
        Assert.Contains(("InReview", "NoChangeRequired"), assessmentPairs);
        Assert.Contains(("InReview", "ChangeRequestsLinked"), assessmentPairs);
        Assert.Contains(("Approved", "Pending"), assessmentPairs);
        Assert.Contains(("Approved", "ChangeRequired"), assessmentPairs);
        Assert.Contains(("Approved", "NoChangeRequired"), assessmentPairs);
        Assert.DoesNotContain(("Approved", "ChangeRequestsLinked"), assessmentPairs);
        Assert.DoesNotContain(("Superseded", "Pending"), assessmentPairs);
        Assert.DoesNotContain(("Superseded", "ChangeRequired"), assessmentPairs);
        Assert.DoesNotContain(("Superseded", "NoChangeRequired"), assessmentPairs);
        Assert.DoesNotContain(("Superseded", "ChangeRequestsLinked"), assessmentPairs);

        var people = root.GetProperty("people").EnumerateArray().ToList();
        var peopleJson = root.GetProperty("people").GetRawText();
        Assert.DoesNotContain("\"email\"", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endedRoles", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backsUp", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.All(people, person =>
        {
            Assert.True(person.TryGetProperty("userId", out _));
            Assert.True(person.TryGetProperty("isCurrentProjectMember", out _));
            Assert.True(person.TryGetProperty("accountState", out _));
            Assert.True(person.TryGetProperty("baseRoles", out _));
            Assert.True(person.TryGetProperty("disciplineAffinities", out _));
        });
        var idle = people.Single(person => person.GetProperty("userName").GetString() == fixture.IdleMember);
        Assert.Equal(0, idle.GetProperty("holds").GetInt32());
        Assert.True(idle.GetProperty("isCurrentProjectMember").GetBoolean());
        Assert.Equal("disabled", idle.GetProperty("accountState").GetString());
        var locked = people.Single(person => person.GetProperty("userName").GetString() == fixture.LockedMember);
        Assert.Equal(0, locked.GetProperty("holds").GetInt32());
        Assert.True(locked.GetProperty("isCurrentProjectMember").GetBoolean());
        Assert.Equal("locked", locked.GetProperty("accountState").GetString());
        var inactiveHolder = people.Single(person => person.GetProperty("userName").GetString() == fixture.FrozenHolder);
        Assert.True(inactiveHolder.GetProperty("holds").GetInt32() > 0);
        Assert.False(inactiveHolder.GetProperty("isCurrentProjectMember").GetBoolean());
        Assert.Equal("disabled", inactiveHolder.GetProperty("accountState").GetString());
        Assert.Empty(inactiveHolder.GetProperty("baseRoles").EnumerateArray());
        Assert.Empty(inactiveHolder.GetProperty("disciplineAffinities").EnumerateArray());
        var mixedHolder = people.Single(person => person.GetProperty("userName").GetString() == fixture.Reviewer);
        Assert.True(mixedHolder.GetProperty("byLane").GetProperty("sign").GetInt32() > 0);
        Assert.Equal(["SystemEngineer"], mixedHolder.GetProperty("baseRoles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(["system"], mixedHolder.GetProperty("disciplineAffinities").EnumerateArray().Select(affinity => affinity.GetString()));
        Assert.DoesNotContain(people, person => person.GetProperty("userName").GetString() == fixture.InactiveNonHolder);
        var totalHolds = people.Sum(person => person.GetProperty("holds").GetInt32());
        var heldItems = items.Count(item => item.GetProperty("currentHolderIds").GetArrayLength() > 0);
        Assert.True(totalHolds > heldItems, $"Expected multi-holder count to exceed held-item count; holds={totalHolds}, heldItems={heldItems}");
    }

    [Fact]
    public void Team_work_classifies_every_current_program_role_explicitly()
    {
        var expected = new Dictionary<ProgramRole, string?>
        {
            [ProgramRole.SystemEngineer] = "SystemEngineer",
            [ProgramRole.SoftwareEngineer] = "SoftwareEngineer",
            [ProgramRole.SystemTestEngineer] = "SystemTestEngineer",
            [ProgramRole.SoftwareTestEngineer] = "SoftwareTestEngineer",
            [ProgramRole.ProjectEngineer] = "ProjectEngineer",
            [ProgramRole.EngineeringManager] = "EngineeringManager",
            [ProgramRole.ProgramManager] = "ProgramManager",
            [ProgramRole.ConfigurationManager] = "ConfigurationManager",
            [ProgramRole.SoftwareQualityAnalyst] = "SoftwareQualityAnalyst",
            [ProgramRole.Airworthiness] = "Airworthiness",
            [ProgramRole.Engineer] = null,
            [ProgramRole.Reviewer] = null,
            [ProgramRole.Approver] = null,
            [ProgramRole.TestEngineer] = null,
            [ProgramRole.TestLead] = null,
            [ProgramRole.Administrator] = null,
            [ProgramRole.ProjectEngineeringLead] = null,
            [ProgramRole.SystemEngineeringLead] = null,
            [ProgramRole.SoftwareEngineeringLead] = null,
            [ProgramRole.SystemTestLead] = null,
            [ProgramRole.SoftwareTestLead] = null,
        };
        Assert.Equal(Enum.GetValues<ProgramRole>().Order(), expected.Keys.Order());

        var method = typeof(TeamWorkProjectionService).GetMethod(
            "ModernBaseRoles", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        foreach (var role in Enum.GetValues<ProgramRole>())
        {
            var actual = Assert.IsAssignableFrom<IReadOnlyList<string>>(
                method.Invoke(null, [new[] { role }]));
            if (expected[role] is string modern) Assert.Equal([modern], actual);
            else Assert.Empty(actual);
        }

        var unknown = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [new[] { (ProgramRole)999 }]));
        Assert.IsType<AeroLink.Domain.Common.DomainException>(unknown.InnerException);
    }

    [Fact]
    public async Task Project_endpoint_ignores_a_shell_release_query_and_open_resolver_uses_each_item_release()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fixture = await SeedRoutingAsync(factory);
        await SignInAsync(client, fixture.Viewer);

        using var projection = await client.GetAsync(
            $"/api/team-work?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseB}");
        var document = await ReadSuccessAsync(projection);
        Assert.Contains(document.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == fixture.SystemCr);

        foreach (var expected in fixture.Routes)
        {
            using var response = await client.GetAsync($"/open/{expected.Kind}/{expected.Id}?releaseId={fixture.ReleaseB}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location?.ToString();
            Assert.NotNull(location);
            Assert.Contains($"/releases/{fixture.ReleaseA}/", location, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expected.Tail, location, StringComparison.OrdinalIgnoreCase);
        }

        await SetAssessmentTargetAsync(factory, fixture.UnsupportedAssessment, RequirementLevel.Customer);
        using var unsupported = await client.GetAsync($"/open/downstream-assessment/{fixture.UnsupportedAssessment}");
        Assert.Equal(HttpStatusCode.Redirect, unsupported.StatusCode);
        Assert.Equal("/", unsupported.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Project_projection_query_count_is_constant_as_item_count_grows()
    {
        var counter = new TeamWorkQueryCounter();
        using var factory = new AeroLinkApiFactory(commandInterceptor: counter);
        using var client = factory.CreateClient();
        var fixture = await SeedBaseAsync(factory);
        await SignInAsync(client, fixture.Viewer);

        counter.Clear();
        using var first = await client.GetAsync($"/api/team-work?projectId={fixture.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstQueryCount = counter.Count;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.AddRange(Enumerable.Range(1, 25).Select(index =>
                Change($"SRCR-{index + 30000:D5}", 0, fixture.ProjectId, fixture.ReleaseA,
                    $"Additional item {index}", fixture.Viewer, ChangeRequestState.Draft, now)));
            await db.SaveChangesAsync();
        }

        counter.Clear();
        using var second = await client.GetAsync($"/api/team-work?projectId={fixture.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstQueryCount, counter.Count);
        Assert.True(firstQueryCount > 0, "The interceptor did not observe projection SELECT commands.");
    }

    private static void AssertItem(
        IReadOnlyDictionary<Guid, JsonElement> items,
        Guid id,
        string lane,
        IReadOnlyCollection<string> holders,
        string basis,
        string nativeState,
        bool deferred = false)
    {
        Assert.True(items.TryGetValue(id, out var item), $"Expected Team Work item {id} was not returned.");
        Assert.Equal(lane, item.GetProperty("lane").GetString());
        Assert.Equal(basis, item.GetProperty("holderBasis").GetString());
        Assert.Equal(nativeState, item.GetProperty("nativeState").GetString());
        Assert.Equal(deferred, item.GetProperty("deferred").GetBoolean());
        Assert.Equal(holders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            item.GetProperty("currentHolderIds").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static void AssertStageKinds(JsonElement item, IReadOnlyCollection<(string Holder, string Kind)> expected)
    {
        var actual = item.GetProperty("activeStageObligations").EnumerateArray()
            .Select(obligation => (obligation.GetProperty("holderId").GetString()!, obligation.GetProperty("stageKind").GetString()!))
            .OrderBy(value => value.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Item2, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.OrderBy(value => value.Holder, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Kind), actual);
    }

    private static string? NullableString(JsonElement item, string property) =>
        item.GetProperty(property).ValueKind == JsonValueKind.Null ? null : item.GetProperty(property).GetString();

    private static async Task<JsonDocument> ReadSuccessAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Seed> SeedBaseAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"Team Work {suffix}", $"TW{suffix}");
        var project = new ProjectRecord(program.Id, "Team Work project", "TW");
        var releaseA = new SoftwareRelease(project.Id, "1.6", false);
        var releaseB = new SoftwareRelease(project.Id, "1.7", true);
        var foreignProgram = new ProgramRecord($"Foreign {suffix}", $"FX{suffix}");
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Foreign project", "FX");
        var foreignRelease = new SoftwareRelease(foreignProject.Id, "9.9", false);
        var viewer = Account("team.viewer", "Team Viewer", now);
        var outsider = Account("team.outsider", "Team Outsider", now);
        var localRecord = Change("SRCR-90001", 0, project.Id, releaseA.Id, "Local project record", "team.viewer", ChangeRequestState.Draft, now);
        var foreignRecord = Change("SRCR-90000", 0, foreignProject.Id, foreignRelease.Id, "Foreign project record", "team.viewer", ChangeRequestState.Draft, now);
        db.AddRange(program, project, releaseA, releaseB, foreignProgram, foreignProject, foreignRelease, viewer, outsider, localRecord, foreignRecord);
        db.Add(new ProgramMembership(viewer.Id, program.Id, ProgramRole.Engineer, "test", now));
        db.Add(new ProgramMembership(outsider.Id, foreignProgram.Id, ProgramRole.SoftwareEngineer, "test", now));
        await db.SaveChangesAsync();
        return new Seed(project.Id, foreignProject.Id, releaseA.Id, releaseB.Id, viewer.UserName, outsider.UserName);
    }

    private static async Task<MatrixSeed> SeedMatrixAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"Matrix {suffix}", $"MX{suffix}");
        var project = new ProjectRecord(program.Id, "Matrix project", "MX");
        var releaseA = new SoftwareRelease(project.Id, "1.6", false);
        var releaseB = new SoftwareRelease(project.Id, "1.7", true);
        var accounts = new[]
        {
            Account("matrix.viewer", "Matrix Viewer", now), Account("matrix.author", "Matrix Author", now),
            Account("matrix.reviewer", "Matrix Reviewer", now), Account("matrix.frozen", "Frozen Holder", now),
            Account("matrix.assigned", "Assigned Engineer", now), Account("matrix.responsible", "Responsible Engineer", now),
            Account("matrix.assessment", "Assessment Approver", now), Account("matrix.idle", "Idle Member", now),
            Account("matrix.locked", "Locked Member", now), Account("matrix.inactive", "Inactive Non Holder", now),
        };
        db.AddRange(program, project, releaseA, releaseB);
        db.AddRange(accounts);
        foreach (var account in accounts.Where(account => account.UserName is not ("matrix.inactive" or "matrix.frozen")))
            db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test", now));
        db.Add(new ProgramMembership(accounts.Single(x => x.UserName == "matrix.reviewer").Id,
            program.Id, ProgramRole.SystemEngineer, "test", now));
        db.Add(new ProgramMembership(accounts.Single(x => x.UserName == "matrix.reviewer").Id,
            program.Id, ProgramRole.Reviewer, "test", now));
        db.Add(new ProgramMembership(accounts.Single(x => x.UserName == "matrix.reviewer").Id,
            program.Id, ProgramRole.SystemEngineeringLead, "test", now));
        accounts.Single(x => x.UserName == "matrix.idle").Disable(now.AddMinutes(1));
        var locked = accounts.Single(x => x.UserName == "matrix.locked");
        for (var failedLogin = 0; failedLogin < 8; failedLogin++) locked.LoginFailed();
        var endedMembership = new ProgramMembership(accounts.Single(x => x.UserName == "matrix.inactive").Id,
            program.Id, ProgramRole.Engineer, "test", now);
        endedMembership.End("test", now.AddMinutes(1));
        db.Add(endedMembership);
        var endedHolderMembership = new ProgramMembership(accounts.Single(x => x.UserName == "matrix.frozen").Id,
            program.Id, ProgramRole.Engineer, "test", now);
        endedHolderMembership.End("test", now.AddMinutes(1));
        db.Add(endedHolderMembership);
        accounts.Single(x => x.UserName == "matrix.frozen").Disable(now.AddMinutes(1));

        var source = Change("SRCR-10000", 0, project.Id, releaseA.Id, "Source", "matrix.author", ChangeRequestState.Withdrawn, now);
        db.Add(source);

        var crDraft = Change("SRCR-10001", 0, project.Id, releaseA.Id, "CR Draft", "matrix.author", ChangeRequestState.Draft, now);
        var crReview = ChangeWithCycle("SRCR-10002", project.Id, releaseA.Id, "CR Review", "matrix.author", ["matrix.reviewer"], [ReviewStageKind.Review], now);
        var crApproval = ChangeWithCycle("SRCR-10003", project.Id, releaseA.Id, "CR Approval", "matrix.author", ["matrix.frozen"], [ReviewStageKind.Approval], now);
        var crMixed = ChangeWithCycle("SRCR-10004", project.Id, releaseA.Id, "CR Mixed", "matrix.author", ["matrix.reviewer", "matrix.frozen"], [ReviewStageKind.Review, ReviewStageKind.Approval], now);
        var crZero = ChangeWithCycle("SRCR-10005", project.Id, releaseA.Id, "CR Zero", "matrix.author", ["matrix.reviewer"], [ReviewStageKind.Review], now, makeStepsInactive: true);
        var crApproved = Change("SRCR-10006", 0, project.Id, releaseA.Id, "CR Approved", "matrix.author", ChangeRequestState.Approved, now);
        var crSelected = Change("SRCR-10007", 0, project.Id, releaseB.Id, "CR Selected Released", "matrix.author", ChangeRequestState.SelectedForBaseline, now);
        var crSelectedUnreleased = Change("SRCR-10014", 0, project.Id, releaseA.Id, "CR Selected Unreleased", "matrix.author", ChangeRequestState.SelectedForBaseline, now);
        var crApprovedReleaseB = Change("SRCR-10015", 0, project.Id, releaseB.Id, "CR Approved Release B", "matrix.author", ChangeRequestState.Approved, now);
        var crDeferredDraft = Change("SRCR-10008", 0, project.Id, releaseA.Id, "CR Deferred Draft", "matrix.author", ChangeRequestState.Deferred, now, ChangeRequestState.Draft);
        var crDeferredReview = ChangeWithCycle("SRCR-10009", project.Id, releaseA.Id, "CR Deferred Review", "matrix.author", ["matrix.frozen"], [ReviewStageKind.Approval], now, cycleState: ReviewCycleState.Cancelled);
        Set(crDeferredReview, nameof(SystemChangeRequest.State), ChangeRequestState.Deferred);
        Set(crDeferredReview, nameof(SystemChangeRequest.DeferredFromState), ChangeRequestState.InReview);
        var crDeferredApproved = Change("SRCR-10010", 0, project.Id, releaseA.Id, "CR Deferred Approved", "matrix.author", ChangeRequestState.Deferred, now, ChangeRequestState.Approved);
        var crDeferredUnknown = Change("SRCR-10016", 0, project.Id, releaseA.Id, "CR Deferred Unknown", "matrix.author", ChangeRequestState.Deferred, now);
        var crWithdrawn = Change("SRCR-10011", 0, project.Id, releaseA.Id, "CR Withdrawn", "matrix.author", ChangeRequestState.Withdrawn, now);
        var crOld = Change("SRCR-10012", 0, project.Id, releaseA.Id, "CR old", "matrix.author", ChangeRequestState.Withdrawn, now);
        var crCurrent = Change("SRCR-10012", 1, project.Id, releaseA.Id, "CR current", "matrix.author", ChangeRequestState.Draft, now.AddMinutes(2));
        var crReturned = ChangeWithCycle("SRCR-10013", project.Id, releaseA.Id, "CR Returned", "matrix.author", ["matrix.reviewer"], [ReviewStageKind.Review], now, cycleState: ReviewCycleState.ChangesRequested);
        Set(crReturned, nameof(SystemChangeRequest.State), ChangeRequestState.Draft);

        var prDraft = Problem(project.Id, releaseA.Id, "PR-10001", ProblemReportState.Draft, now);
        var tcrDraft = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10001", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 0);
        Set(tcrDraft, nameof(TestChangeReview.AssignedEngineerId), "matrix.assigned");
        var tcrNullAssigned = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10002", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 1);
        var tcrReview = TcrWithCycle(project.Id, releaseA.Id, source.Id, "HLRTCCR-10003", ["matrix.reviewer"], [ReviewStageKind.Review], now, revision: 2);
        var tcrApproval = TcrWithCycle(project.Id, releaseA.Id, source.Id, "LLRTCCR-10004", ["matrix.frozen"], [ReviewStageKind.Approval], now, revision: 3);
        var tcrApproved = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10005", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 4);
        Set(tcrApproved, nameof(TestChangeReview.State), TestChangeReviewState.Approved);
        var tcrIncorporated = Tcr(project.Id, releaseB.Id, source.Id, "SYSTPCR-10011", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 11);
        Set(tcrIncorporated, nameof(TestChangeReview.State), TestChangeReviewState.Approved);
        var tcrDeferredReview = TcrWithCycle(project.Id, releaseA.Id, source.Id, "HLRTCCR-10006", ["matrix.frozen"], [ReviewStageKind.Approval], now, ReviewCycleState.Cancelled, revision: 5);
        Set(tcrDeferredReview, nameof(TestChangeReview.State), TestChangeReviewState.Deferred);
        Set(tcrDeferredReview, nameof(TestChangeReview.DeferredFromState), TestChangeReviewState.InReview);
        var tcrDeferredUnknown = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10014", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 14);
        Set(tcrDeferredUnknown, nameof(TestChangeReview.State), TestChangeReviewState.Deferred);
        var tcrSuperseded = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10007", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 6);
        Set(tcrSuperseded, nameof(TestChangeReview.State), TestChangeReviewState.Superseded);
        var tcrUnnumbered = Tcr(project.Id, releaseA.Id, source.Id, "", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 7);
        var tcrAutomatic = TestChangeReview.FromProblemReport(project.Id, releaseA.Id, prDraft.Id, TestChangeReviewDiscipline.HighLevelSoftware, "PR-100", now, "HLRTCCR-10012", authorId: "");
        Set(tcrAutomatic, nameof(TestChangeReview.State), TestChangeReviewState.Draft);
        var tcrProcedureSystem = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10008", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 8);
        var tcrProcedureHigh = ProcedureTcr(project.Id, releaseA.Id, source.Id, "HLRTPCR-10009", TestChangeReviewDiscipline.HighLevelSoftware, "matrix.author", now, revision: 9);
        var tcrProcedureLow = ProcedureTcr(project.Id, releaseA.Id, source.Id, "LLRTPCR-10010", TestChangeReviewDiscipline.LowLevelSoftware, "matrix.author", now, revision: 10);
        var tcrLatestOld = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10013", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 12);
        var tcrLatestNew = Tcr(project.Id, releaseA.Id, source.Id, "SYSTPCR-10013", TestChangeReviewDiscipline.System, "matrix.author", now, revision: 13);
        Set(tcrLatestNew, nameof(TestChangeReview.State), TestChangeReviewState.Approved);

        var prSccb = Problem(project.Id, releaseA.Id, "PR-10002", ProblemReportState.ReadyForSccb, now);
        var prOpen = Problem(project.Id, releaseA.Id, "PR-10003", ProblemReportState.Open, now);
        var prDeferred = Problem(project.Id, releaseA.Id, "PR-10009", ProblemReportState.Open, now);
        Set(prDeferred, nameof(ProblemReport.Disposition), ProblemReportDisposition.Deferred);
        Set(prDeferred, nameof(ProblemReport.DispositionRationale), "Deferred for a later release.");
        var prImplementing = Problem(project.Id, releaseA.Id, "PR-10004", ProblemReportState.Implementing, now);
        var prVerifying = Problem(project.Id, releaseA.Id, "PR-10005", ProblemReportState.Verifying, now);
        var prSqa = Problem(project.Id, releaseA.Id, "PR-10006", ProblemReportState.WaitingForSqaToClose, now);
        var prClosed = Problem(project.Id, releaseA.Id, "PR-10007", ProblemReportState.Closed, now);
        var prRejected = Problem(project.Id, releaseA.Id, "PR-10008", ProblemReportState.Rejected, now);

        var assessmentIds = new List<DownstreamChangeAssessment>();
        Guid? firstAssessmentSourceId = null;
        DownstreamChangeAssessment Assessment(string key, DownstreamAssessmentState state, DownstreamAssessmentOutcome outcome, RequirementLevel level = RequirementLevel.HighLevel)
        {
            var sourceForAssessment = Change($"SRCR-{key.PadLeft(5, '0')}", 0, project.Id, releaseA.Id, $"Assessment source {key}", "matrix.author", ChangeRequestState.Withdrawn, now);
            db.Add(sourceForAssessment);
            if (key == "101") firstAssessmentSourceId = sourceForAssessment.Id;
            // The legacy constructor accepts only the historical HLR/LLR targets. Persisted rows can still
            // contain a newer level, so place the matrix value after construction through the fixture seam.
            var value = new DownstreamChangeAssessment(project.Id, releaseA.Id, sourceForAssessment.Id, sourceForAssessment.DisplayNumber, RequirementLevel.HighLevel, now);
            Set(value, nameof(DownstreamChangeAssessment.TargetLevel), level);
            Set(value, nameof(DownstreamChangeAssessment.State), state);
            Set(value, nameof(DownstreamChangeAssessment.Outcome), outcome);
            Set(value, nameof(DownstreamChangeAssessment.AssignedEngineerId), "matrix.assigned");
            Set(value, nameof(DownstreamChangeAssessment.SelectedApproverId), "matrix.assessment");
            assessmentIds.Add(value);
            return value;
        }
        var assessmentOpenPending = Assessment("101", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.Pending);
        var assessmentOpenChange = Assessment("102", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.ChangeRequired);
        var assessmentOpenNoChange = Assessment("103", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.NoChangeRequired);
        var assessmentOpenLinked = Assessment("104", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.ChangeRequestsLinked);
        var assessmentReview = Assessment("105", DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.NoChangeRequired);
        var assessmentApproved = Assessment("106", DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.NoChangeRequired);
        var assessmentApprovedLinked = Assessment("107", DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.ChangeRequestsLinked);
        var assessmentSuperseded = Assessment("108", DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.Pending);
        Assessment("111", DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.Pending);
        Assessment("112", DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.ChangeRequired);
        Assessment("113", DownstreamAssessmentState.InReview, DownstreamAssessmentOutcome.ChangeRequestsLinked);
        Assessment("114", DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.Pending);
        Assessment("115", DownstreamAssessmentState.Approved, DownstreamAssessmentOutcome.ChangeRequired);
        Assessment("116", DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.ChangeRequired);
        Assessment("117", DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.NoChangeRequired);
        Assessment("118", DownstreamAssessmentState.Superseded, DownstreamAssessmentOutcome.ChangeRequestsLinked);
        var assessmentSystem = Assessment("109", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.Pending, RequirementLevel.System);
        var assessmentLow = Assessment("110", DownstreamAssessmentState.Open, DownstreamAssessmentOutcome.Pending, RequirementLevel.LowLevel);

        var baseline = new CandidateBaseline("BL-10001", 0, project.Id, releaseB.Id, null, "Released baseline", "matrix.author", now);
        Set(baseline, nameof(CandidateBaseline.State), CandidateBaselineState.Released);
        var crSelection = NewSelection<BaselineChangeRequestSelection>(baseline.Id, crSelected.Id, crSelected.DisplayNumber);
        var tcrSelection = NewSelection<BaselineTestChangeRequestSelection>(baseline.Id, tcrIncorporated.Id, tcrIncorporated.DisplayNumber);
        var unreleasedBaseline = new CandidateBaseline("BL-10002", 0, project.Id, releaseA.Id, null, "Unreleased baseline", "matrix.author", now);
        var unreleasedCrSelection = NewSelection<BaselineChangeRequestSelection>(unreleasedBaseline.Id, crSelectedUnreleased.Id, crSelectedUnreleased.DisplayNumber);

        db.AddRange(crDraft, crReview, crApproval, crMixed, crZero, crApproved, crSelected, crSelectedUnreleased, crApprovedReleaseB, crDeferredDraft, crDeferredReview,
            crDeferredApproved, crDeferredUnknown, crWithdrawn, crOld, crCurrent, crReturned,
            tcrDraft, tcrNullAssigned, tcrReview, tcrApproval, tcrApproved, tcrIncorporated, tcrDeferredReview, tcrDeferredUnknown, tcrSuperseded, tcrUnnumbered,
            tcrAutomatic, tcrProcedureSystem, tcrProcedureHigh, tcrProcedureLow, tcrLatestOld, tcrLatestNew,
            prDraft, prSccb, prOpen, prDeferred, prImplementing, prVerifying, prSqa, prClosed, prRejected,
            baseline, crSelection, tcrSelection, unreleasedBaseline, unreleasedCrSelection);
        db.AddRange(assessmentIds);
        await db.SaveChangesAsync();

        return new MatrixSeed(project.Id, releaseA.Id, releaseB.Id, "matrix.viewer", "matrix.author", "matrix.reviewer", "matrix.frozen", "matrix.assigned", "matrix.responsible", "matrix.assessment", "matrix.idle", "matrix.locked", "matrix.inactive",
            crDraft.Id, crReview.Id, crApproval.Id, crMixed.Id, crZero.Id, crApproved.Id, crSelected.Id, crSelectedUnreleased.Id, crApprovedReleaseB.Id, crDeferredDraft.Id, crDeferredReview.Id, crDeferredApproved.Id, crDeferredUnknown.Id, crWithdrawn.Id, crOld.Id, crCurrent.Id, crReturned.Id,
            tcrDraft.Id, tcrNullAssigned.Id, tcrReview.Id, tcrApproval.Id, tcrApproved.Id, tcrDeferredReview.Id, tcrDeferredUnknown.Id, tcrSuperseded.Id, tcrUnnumbered.Id, tcrAutomatic.Id, tcrProcedureSystem.Id, tcrProcedureHigh.Id, tcrProcedureLow.Id, tcrLatestOld.Id, tcrLatestNew.Id, tcrIncorporated.Id,
            prDraft.Id, prSccb.Id, prOpen.Id, prDeferred.Id, prImplementing.Id, prVerifying.Id, prSqa.Id, prClosed.Id, prRejected.Id,
            assessmentOpenPending.Id, firstAssessmentSourceId!.Value, assessmentOpenChange.Id, assessmentOpenNoChange.Id, assessmentOpenLinked.Id, assessmentReview.Id, assessmentApproved.Id, assessmentApprovedLinked.Id, assessmentSuperseded.Id, assessmentSystem.Id, assessmentLow.Id);
    }

    private static async Task<RoutingSeed> SeedRoutingAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord($"Routing {suffix}", $"RT{suffix}");
        var project = new ProjectRecord(program.Id, "Routing project", "RT");
        var releaseA = new SoftwareRelease(project.Id, "1.6", false);
        var releaseB = new SoftwareRelease(project.Id, "1.7", true);
        var viewer = Account("routing.viewer", "Routing Viewer", now);
        db.AddRange(program, project, releaseA, releaseB, viewer);
        db.Add(new ProgramMembership(viewer.Id, program.Id, ProgramRole.Engineer, "test", now));
        var system = Change("SRCR-20001", 0, project.Id, releaseA.Id, "System", viewer.UserName, ChangeRequestState.Draft, now, type: ChangeRequestType.System);
        var high = Change("HLRCR-20002", 0, project.Id, releaseA.Id, "High", viewer.UserName, ChangeRequestState.Draft, now, type: ChangeRequestType.Software, level: RequirementLevel.HighLevel);
        var low = Change("LLRCR-20003", 0, project.Id, releaseA.Id, "Low", viewer.UserName, ChangeRequestState.Draft, now, type: ChangeRequestType.Software, level: RequirementLevel.LowLevel);
        var interfaceCr = Change("ICDCR-20004", 0, project.Id, releaseA.Id, "Interface", viewer.UserName, ChangeRequestState.Draft, now, type: ChangeRequestType.Interface);
        var tcrSystem = Tcr(project.Id, releaseA.Id, system.Id, "SYSTPCR-20005", TestChangeReviewDiscipline.System, viewer.UserName, now, revision: 0);
        var tcrHigh = Tcr(project.Id, releaseA.Id, system.Id, "HLRTCCR-20006", TestChangeReviewDiscipline.HighLevelSoftware, viewer.UserName, now, revision: 0);
        var tcrLow = Tcr(project.Id, releaseA.Id, system.Id, "LLRTCCR-20007", TestChangeReviewDiscipline.LowLevelSoftware, viewer.UserName, now, revision: 0);
        var procedureHigh = ProcedureTcr(project.Id, releaseA.Id, system.Id, "HLRTPCR-20008", TestChangeReviewDiscipline.HighLevelSoftware, viewer.UserName, now, revision: 1);
        var procedureLow = ProcedureTcr(project.Id, releaseA.Id, system.Id, "LLRTPCR-20009", TestChangeReviewDiscipline.LowLevelSoftware, viewer.UserName, now, revision: 2);
        var pr = Problem(project.Id, releaseA.Id, "PR-20009", ProblemReportState.Draft, now);
        var assessmentSource = Change("SRCR-20010", 0, project.Id, releaseA.Id, "Assessment source", viewer.UserName, ChangeRequestState.Withdrawn, now);
        var assessmentSystem = new DownstreamChangeAssessment(project.Id, releaseA.Id, assessmentSource.Id, assessmentSource.DisplayNumber, RequirementLevel.HighLevel, now);
        Set(assessmentSystem, nameof(DownstreamChangeAssessment.TargetLevel), RequirementLevel.System);
        var assessmentHigh = new DownstreamChangeAssessment(project.Id, releaseA.Id, assessmentSource.Id, assessmentSource.DisplayNumber, RequirementLevel.HighLevel, now);
        var assessmentLowSource = Change("SRCR-20011", 0, project.Id, releaseA.Id, "Assessment low source", viewer.UserName, ChangeRequestState.Withdrawn, now);
        var assessmentLow = new DownstreamChangeAssessment(project.Id, releaseA.Id, assessmentLowSource.Id, assessmentLowSource.DisplayNumber, RequirementLevel.LowLevel, now);
        db.AddRange(system, high, low, interfaceCr, tcrSystem, tcrHigh, tcrLow, procedureHigh, procedureLow, pr, assessmentSource, assessmentSystem, assessmentHigh, assessmentLowSource, assessmentLow);
        await db.SaveChangesAsync();
        var routes = new[]
        {
            new Route("change-request", system.Id, "/systems/change-requests/"),
            new Route("change-request", high.Id, "/software/change-requests/"),
            new Route("change-request", low.Id, "/software/change-requests/"),
            new Route("change-request", interfaceCr.Id, "/interfaces/change-requests/"),
            new Route("test-change-request", tcrSystem.Id, "/system-verification/change-requests/"),
            new Route("test-change-request", tcrHigh.Id, "/software-verification/hlr/change-requests/"),
            new Route("test-change-request", tcrLow.Id, "/software-verification/llr/change-requests/"),
            new Route("test-change-request", procedureHigh.Id, "/software-verification/hlr/change-requests/"),
            new Route("test-change-request", procedureLow.Id, "/software-verification/llr/change-requests/"),
            new Route("problem-report", pr.Id, $"/problem-reports/{pr.Id}"),
            new Route("downstream-assessment", assessmentSystem.Id, "/system-verification/coverage/"),
            new Route("downstream-assessment", assessmentHigh.Id, "/software-verification/hlr/coverage/"),
            new Route("downstream-assessment", assessmentLow.Id, "/software-verification/llr/coverage/"),
        };
        return new RoutingSeed(project.Id, releaseA.Id, releaseB.Id, viewer.UserName, system.Id, assessmentLow.Id, routes);
    }

    private static async Task SetAssessmentTargetAsync(AeroLinkApiFactory factory, Guid assessmentId, RequirementLevel level)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var assessment = await db.DownstreamChangeAssessments.SingleAsync(item => item.Id == assessmentId);
        Set(assessment, nameof(DownstreamChangeAssessment.TargetLevel), level);
        await db.SaveChangesAsync();
    }

    private static UserAccount Account(string userName, string displayName, DateTimeOffset now) =>
        new(userName, displayName, $"{userName}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

    private static SystemChangeRequest Change(string number, int revision, Guid projectId, Guid releaseId, string title,
        string author, ChangeRequestState state, DateTimeOffset now, ChangeRequestState? deferredFromState = null,
        ChangeRequestType type = ChangeRequestType.System, RequirementLevel? level = null)
    {
        var change = new SystemChangeRequest(number, revision, projectId, releaseId, title, "P", "A", "S", author, now, type, softwareLevel: level);
        Set(change, nameof(SystemChangeRequest.State), state);
        Set(change, nameof(SystemChangeRequest.DeferredFromState), deferredFromState);
        return change;
    }

    private static SystemChangeRequest ChangeWithCycle(string number, Guid projectId, Guid releaseId, string title,
        string author, IReadOnlyList<string> holders, IReadOnlyList<ReviewStageKind> kinds, DateTimeOffset now,
        ReviewCycleState cycleState = ReviewCycleState.Active, bool makeStepsInactive = false)
    {
        var change = Change(number, 0, projectId, releaseId, title, author, ChangeRequestState.Draft, now);
        change.AddRequirementChange(author, $"SYSR-{number[^5..]}", 0, RequirementLevel.System,
            RequirementChangeKind.Modify, "Statement", "Rationale", "Test", now);
        var selections = holders.Select(holder => new ApproverSelection(holder, holder)).ToArray();
        var cycle = change.SubmitForReview(author, selections, now,
            holders.Count > 1 ? ReviewMode.Parallel : ReviewMode.Sequential);
        Set(cycle, nameof(ReviewCycle.State), cycleState);
        foreach (var (step, index) in cycle.Steps.Select((step, index) => (step, index)))
        {
            Set(step, nameof(ApprovalStep.StageKind), kinds[Math.Min(index, kinds.Count - 1)]);
            if (makeStepsInactive) Set(step, nameof(ApprovalStep.State), ApprovalStepState.Approved);
        }
        return change;
    }

    private static TestChangeReview Tcr(Guid projectId, Guid releaseId, Guid sourceId, string number,
        TestChangeReviewDiscipline discipline, string author, DateTimeOffset now, int revision = 0) =>
        new(projectId, releaseId, sourceId, discipline, "SRCR-10000.00", now, number, revision, authorId: author);

    private static TestChangeReview ProcedureTcr(Guid projectId, Guid releaseId, Guid sourceId, string number,
        TestChangeReviewDiscipline discipline, string author, DateTimeOffset now, int revision = 0) =>
        new TestChangeReview(projectId, releaseId, sourceId,
            new VerificationArtifactKey(
                discipline == TestChangeReviewDiscipline.HighLevelSoftware
                    ? VerificationDiscipline.HighLevelSoftware
                    : VerificationDiscipline.LowLevelSoftware,
                VerificationArtifactKind.Procedure),
            "SRCR-10000.00", now, number, revision, authorId: author);

    private static TestChangeReview TcrWithCycle(Guid projectId, Guid releaseId, Guid sourceId, string number,
        IReadOnlyList<string> holders, IReadOnlyList<ReviewStageKind> kinds, DateTimeOffset now,
        ReviewCycleState cycleState = ReviewCycleState.Active, int revision = 0)
    {
        var review = Tcr(projectId, releaseId, sourceId, number, TestChangeReviewDiscipline.HighLevelSoftware, "matrix.author", now, revision);
        Set(review, nameof(TestChangeReview.State), TestChangeReviewState.InReview);
        var approvers = holders.Select(holder => new ApproverSelection(holder, holder)).ToArray();
        var method = typeof(ReviewCycle).GetMethod("ForTestChangeRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cycle = (ReviewCycle)method.Invoke(null, [review.Id, 1, new string('a', 64), approvers, now, ReviewMode.Parallel, null, 0, ""] )!;
        Set(cycle, nameof(ReviewCycle.State), cycleState);
        foreach (var (step, index) in cycle.Steps.Select((step, index) => (step, index)))
            Set(step, nameof(ApprovalStep.StageKind), kinds[Math.Min(index, kinds.Count - 1)]);
        var cyclesField = typeof(TestChangeReview).GetField("_reviewCycles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((IList)cyclesField.GetValue(review)!).Add(cycle);
        return review;
    }

    private static ProblemReport Problem(Guid projectId, Guid releaseId, string number, ProblemReportState state, DateTimeOffset now)
    {
        var report = new ProblemReport(projectId, number, number, "problem", "analysis", "matrix.responsible", now,
            targetReleaseId: releaseId, responsibleEngineerId: "matrix.responsible");
        Set(report, nameof(ProblemReport.State), state);
        return report;
    }

    private static T NewSelection<T>(Guid baselineId, Guid recordId, string displayNumber) where T : class
    {
        var constructor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(Guid), typeof(Guid), typeof(string)], null)!;
        return (T)constructor.Invoke([baselineId, recordId, displayNumber]);
    }

    private static void Set(object target, string property, object? value)
    {
        var propertyInfo = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property {target.GetType().Name}.{property} not found.");
        propertyInfo.SetValue(target, value);
    }

    private sealed record Seed(Guid ProjectId, Guid ForeignProjectId, Guid ReleaseA, Guid ReleaseB, string Viewer, string Outsider);
    private sealed record Route(string Kind, Guid Id, string Tail);
    private sealed record RoutingSeed(Guid ProjectId, Guid ReleaseA, Guid ReleaseB, string Viewer, Guid SystemCr,
        Guid UnsupportedAssessment, IReadOnlyList<Route> Routes);
    private sealed record MatrixSeed(Guid ProjectId, Guid ReleaseA, Guid ReleaseB, string Viewer, string Author, string Reviewer,
        string FrozenHolder, string Assigned, string Responsible, string AssessmentApprover, string IdleMember, string LockedMember, string InactiveNonHolder,
        Guid CrDraft, Guid CrReview, Guid CrApproval, Guid CrMixed, Guid CrZeroSteps, Guid CrApproved, Guid CrSelectedReleased, Guid CrSelectedUnreleased, Guid CrApprovedReleaseB,
        Guid CrDeferredDraft, Guid CrDeferredReview, Guid CrDeferredApproved, Guid CrDeferredUnknown, Guid CrWithdrawn, Guid CrSupersededOld, Guid CrSupersededCurrent, Guid CrReturnedDraft,
        Guid TcrDraft, Guid TcrNullAssigned, Guid TcrReview, Guid TcrApproval, Guid TcrApproved, Guid TcrDeferredReview, Guid TcrDeferredUnknown, Guid TcrSuperseded,
        Guid TcrUnnumbered, Guid TcrAutomatic, Guid TcrProcedureSystem, Guid TcrProcedureHigh, Guid TcrProcedureLow, Guid TcrLatestOld, Guid TcrLatestNew, Guid TcrIncorporated,
        Guid PrDraft, Guid PrSccb, Guid PrOpen, Guid PrDeferred, Guid PrImplementing, Guid PrVerifying, Guid PrSqa, Guid PrClosed, Guid PrRejected,
        Guid AssessmentOpenPending, Guid AssessmentOpenPendingSource, Guid AssessmentOpenChange, Guid AssessmentOpenNoChange, Guid AssessmentOpenLinked, Guid AssessmentReview,
        Guid AssessmentApproved, Guid AssessmentApprovedLinked, Guid AssessmentSuperseded, Guid AssessmentSystem, Guid AssessmentLow);
}

internal sealed class TeamWorkQueryCounter : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> commands = new();

    public int Count => commands.Count(command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        while (commands.TryDequeue(out _)) { }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
