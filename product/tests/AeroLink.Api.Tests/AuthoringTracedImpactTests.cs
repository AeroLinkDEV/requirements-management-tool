using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// What the traceability graph says a proposed change touches, offered to the author deciding its impact.
///
/// A change request asks its author to close five impact decisions, two of which — trace relationships and
/// verification coverage — are answerable from links the product already holds. Those links were reachable from
/// the requirements explorer and from nowhere near the person actually deciding, so the decision was made from
/// memory beside a database that knew the answer.
///
/// The line these tests hold is that informing is not deciding. The endpoint reports; it must never write a
/// disposition, and a requirement with nothing downstream must still leave its author something to confirm.
/// </summary>
public sealed class AuthoringTracedImpactTests
{
    private sealed record Traced(string BaseNumber, bool Known, string? DisplayNumber, Guid? RequirementRevisionId,
        TracedRequirement[] DerivedRequirements, TracedProcedure[] CoveringProcedures);
    private sealed record TracedRequirement(Guid Id, string DisplayNumber, string Level, string Statement, string LinkType);
    private sealed record TracedProcedure(Guid Id, Guid RevisionId, string DisplayNumber, string Title, string Level,
        string State, bool IsSuspect, string CoverageState);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A parent requirement, a child that derives from it, and a procedure that verifies the parent.</summary>
    private static async Task<(Guid ProjectId, string ParentNumber, string ChildNumber, string ProcedureNumber)> SeedAsync(
        AeroLinkApiFactory factory, bool suspectCoverage = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Traced Program", "TRP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Traced Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var account = new UserAccount("traced.author", "traced.author", "traced.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var approver = new UserAccount("traced.approver", "traced.approver", "traced.approver@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(account, approver);
        db.AddRange(
            new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(account.Id, program.Id, ProgramRole.Approver, "test.setup", now),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", now));

        // A revision records the change request and baseline it came from, so those exist rather than being
        // faked with empty identifiers the foreign keys would reject.
        var origin = new SystemChangeRequest("SRCR-00500", 0, project.Id, release.Id, "Origin", "P", "A", "S", "traced.author", now);
        var baseline = new CandidateBaseline("SW-50.00", 0, project.Id, release.Id, null, "Origin baseline", "cm", now);
        db.AddRange(origin, baseline);

        var parent = new RequirementArtifact(project.Id, "SYSR-000501", RequirementLevel.System, now);
        var child = new RequirementArtifact(project.Id, "HLR-000502", RequirementLevel.HighLevel, now);
        var low = new RequirementArtifact(project.Id, "LLR-000503", RequirementLevel.LowLevel, now);
        db.AddRange(parent, child, low);
        var parentRevision = new RequirementRevision(parent.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "Rationale", "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now,
            RequirementParentKind.Derived, "The system-level source is standalone in this trace projection fixture.");
        var childRevision = new RequirementRevision(child.Id, 0, "The software shall compute the sequence.",
            "Rationale", "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now,
            RequirementParentKind.Allocated, parentRevisionIds: [parentRevision.Id]);
        var lowRevision = new RequirementRevision(low.Id, 0, "The implementation shall compute the sequence.",
            "Rationale", "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now,
            RequirementParentKind.Derived,
            "This trace projection fixture exercises authored trace relations separately from upstream allocation.");
        db.AddRange(parentRevision, childRevision, lowRevision,
            new BaselineRequirementSelection(baseline.Id, parent.Id, parentRevision.Id),
            new BaselineRequirementSelection(baseline.Id, child.Id, childRevision.Id),
            new BaselineRequirementSelection(baseline.Id, low.Id, lowRevision.Id));

        // The child traces up to the parent: source derives from target. A change to the parent therefore
        // propagates down to the child, which is the direction the endpoint has to read.
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, childRevision.Id, parentRevision.Id,
            RequirementTraceType.DerivedFrom, "Derived from the system requirement.", now));
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, childRevision.Id, parentRevision.Id,
            RequirementTraceType.AllocatedFrom, "Allocated to the exact system parent.", now));

        var procedure = new TestProcedure(project.Id, "SYSTP-000503", "Verify oceanic sequencing",
            "test.author", now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Purpose", "Configuration", "Steps",
            "Expected", TestProcedureState.Approved, "test.author", now);
        db.AddRange(procedure, procedureRevision);
        db.TestCoverage.Add(suspectCoverage
            ? TestRequirementCoverage.CarriedForward(procedureRevision.Id, parentRevision.Id,
                "The parent requirement wording changed.", now)
            : new TestRequirementCoverage(procedureRevision.Id, parentRevision.Id));

        await db.SaveChangesAsync();
        return (project.Id, parent.BaseNumber, child.BaseNumber, procedure.BaseNumber);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "traced.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_requirement_reports_what_derives_from_it_and_what_verifies_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, procedureNumber) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        var traced = JsonSerializer.Deserialize<Traced>(body, Json)!;

        Assert.True(traced.Known);
        Assert.Equal($"{parentNumber}.00", traced.DisplayNumber);
        Assert.Equal(childNumber,
            Assert.Single(traced.DerivedRequirements, x => x.LinkType == "DerivedFrom")
                .DisplayNumber[..childNumber.Length]);
        Assert.Equal("HighLevel", traced.DerivedRequirements[0].Level);
        Assert.Equal(procedureNumber, Assert.Single(traced.CoveringProcedures).DisplayNumber[..procedureNumber.Length]);
        Assert.Equal("Approved", traced.CoveringProcedures[0].State);
        Assert.Equal("Confirmed", traced.CoveringProcedures[0].CoverageState);
        Assert.False(traced.CoveringProcedures[0].IsSuspect);
        Assert.NotEqual(Guid.Empty, traced.CoveringProcedures[0].RevisionId);
        Assert.NotNull(traced.RequirementRevisionId);
    }

    [Fact]
    public async Task Requirement_impact_and_global_traceability_carry_exact_related_revision_identity()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);

        Guid parentArtifactId, parentRevisionId, childArtifactId, childRevisionId, baselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var rows = await (from artifact in db.Requirements
                              join revision in db.RequirementRevisions on artifact.Id equals revision.ArtifactId
                              where artifact.ProjectId == projectId
                                  && (artifact.BaseNumber == parentNumber || artifact.BaseNumber == childNumber)
                              select new { artifact.BaseNumber, ArtifactId = artifact.Id, RevisionId = revision.Id })
                .ToListAsync();
            parentArtifactId = rows.Single(x => x.BaseNumber == parentNumber).ArtifactId;
            parentRevisionId = rows.Single(x => x.BaseNumber == parentNumber).RevisionId;
            childArtifactId = rows.Single(x => x.BaseNumber == childNumber).ArtifactId;
            childRevisionId = rows.Single(x => x.BaseNumber == childNumber).RevisionId;
            baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
        }

        using var impactResponse = await client.GetAsync($"/api/enterprise-requirements/{parentArtifactId}/impact");
        Assert.Equal(HttpStatusCode.OK, impactResponse.StatusCode);
        var impact = await impactResponse.Content.ReadFromJsonAsync<JsonElement>();
        var child = impact.GetProperty("children").EnumerateArray()
            .First(row => row.GetProperty("id").GetGuid() == childArtifactId);
        Assert.Equal(childArtifactId, child.GetProperty("id").GetGuid());
        Assert.Equal(childRevisionId, child.GetProperty("revisionId").GetGuid());
        Assert.Equal($"{childNumber}.00", child.GetProperty("displayNumber").GetString());

        using var traceResponse = await client.GetAsync(
            $"/api/traceability?projectId={projectId}&baselineId={baselineId}&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
        var trace = await traceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var childRow = trace.GetProperty("items").EnumerateArray()
            .First(row => row.GetProperty("id").GetGuid() == childArtifactId);
        var parents = childRow.GetProperty("parents").EnumerateArray().ToList();
        Assert.NotEmpty(parents);
        Assert.All(parents, parent =>
        {
            Assert.Equal(parentArtifactId, parent.GetProperty("artifactId").GetGuid());
            Assert.Equal(parentRevisionId, parent.GetProperty("revisionId").GetGuid());
            Assert.Equal($"{parentNumber}.00", parent.GetProperty("displayNumber").GetString());
        });
    }

    [Fact]
    public async Task Trace_mutation_accepts_both_relation_types_and_refuses_invalid_duplicate_self_project_and_frozen_history_operations()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        Guid parentRevisionId, childRevisionId, lowRevisionId, baselineId, releaseId, originId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            parentRevisionId = await (from revision in db.RequirementRevisions
                                      join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                      where artifact.ProjectId == projectId && artifact.BaseNumber == parentNumber
                                      select revision.Id).SingleAsync();
            lowRevisionId = await (from revision in db.RequirementRevisions
                                   join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                   where artifact.ProjectId == projectId && artifact.BaseNumber == "LLR-000503"
                                   select revision.Id).SingleAsync();
            childRevisionId = await (from revision in db.RequirementRevisions
                                     join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                     where artifact.ProjectId == projectId && artifact.BaseNumber == "HLR-000502"
                                     select revision.Id).SingleAsync();
            baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            releaseId = await db.Releases.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            originId = await db.SystemChangeRequests.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
        }

        using var created = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = parentRevisionId,
            targetRevisionId = lowRevisionId,
            type = "DerivedFrom",
            rationale = "The low-level implementation derives from the system requirement.",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var allocatedLinkId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var derived = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = lowRevisionId,
            targetRevisionId = parentRevisionId,
            type = "DerivedFrom",
            rationale = "The low-level implementation derives from the system requirement.",
        });
        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var derivedLinkId = (await derived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var duplicate = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = parentRevisionId,
            targetRevisionId = lowRevisionId,
            type = "DerivedFrom",
            rationale = "A duplicate controlled link must not be created.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using var invalidType = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = childRevisionId,
            targetRevisionId = lowRevisionId,
            type = "NotARequirementTraceType",
            rationale = "An unknown relation type is not controlled.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidType.StatusCode);

        using var self = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = parentRevisionId,
            targetRevisionId = parentRevisionId,
            type = "DerivedFrom",
            rationale = "A requirement cannot trace to itself.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        using var foreignProject = factory.Services.CreateScope();
        var foreignDb = foreignProject.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var foreignProgram = new ProgramRecord("Foreign Trace Program", "FTP");
        var foreign = new ProjectRecord(foreignProgram.Id, "Foreign Trace Software", "Foreign Trace Software");
        var foreignArtifact = new RequirementArtifact(foreign.Id, "SYSR-009901", RequirementLevel.System, DateTimeOffset.UtcNow);
        var foreignRevision = new RequirementRevision(foreignArtifact.Id, 0, "Foreign revision", "R", "Test",
            RequirementRevisionState.Active, originId, baselineId, DateTimeOffset.UtcNow);
        foreignDb.AddRange(foreignProgram, foreign, foreignArtifact, foreignRevision);
        await foreignDb.SaveChangesAsync();
        using var wrongProject = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = foreignRevision.Id,
            targetRevisionId = parentRevisionId,
            type = "AllocatedFrom",
            rationale = "A foreign-project revision cannot enter this project graph.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongProject.StatusCode);

        using var deleted = await client.DeleteAsync($"/api/trace-links/{allocatedLinkId}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);

        using (var freezeScope = factory.Services.CreateScope())
        {
            var db = freezeScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var campaign = new ReleaseCampaign(projectId, releaseId, baselineId, "Trace freeze", "traced.author", DateTimeOffset.UtcNow);
            campaign.StartVerification("traced.author", DateTimeOffset.UtcNow);
            campaign.BeginReleaseReview("traced.author", [("traced.author", "Trace approver")], new string('a', 64), DateTimeOffset.UtcNow);
            db.ReleaseCampaigns.Add(campaign);
            await db.SaveChangesAsync();
        }

        using var frozenCreate = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = childRevisionId,
            targetRevisionId = lowRevisionId,
            type = "AllocatedFrom",
            rationale = "The release package is now frozen.",
        });
        Assert.Equal(HttpStatusCode.Conflict, frozenCreate.StatusCode);

        using var frozenDelete = await client.DeleteAsync($"/api/trace-links/{derivedLinkId}");
        Assert.Equal(HttpStatusCode.Conflict, frozenDelete.StatusCode);
    }

    [Fact]
    public async Task Exact_trace_lifecycle_read_action_and_frozen_mutation_refusal_are_project_authorized()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, _) = await SeedAsync(factory);
        Guid linkId, parentRevisionId, childRevisionId, lowRevisionId, baselineId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            parentRevisionId = await (from revision in db.RequirementRevisions
                                      join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                      where artifact.ProjectId == projectId && artifact.BaseNumber == parentNumber
                                      select revision.Id).SingleAsync();
            childRevisionId = await (from revision in db.RequirementRevisions
                                     join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                     where artifact.ProjectId == projectId && artifact.BaseNumber == childNumber
                                     select revision.Id).SingleAsync();
            lowRevisionId = await (from revision in db.RequirementRevisions
                                   join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                   where artifact.ProjectId == projectId && artifact.BaseNumber == "LLR-000503"
                                   select revision.Id).SingleAsync();
            baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            releaseId = await db.Releases.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            var link = await db.RequirementTraces.SingleAsync(x => x.SourceRevisionId == childRevisionId
                && x.TargetRevisionId == parentRevisionId
                && x.Type == RequirementTraceType.DerivedFrom);
            var lifecycle = ExactLinkSuspectLifecycle.Raise(projectId, ExactLinkKind.RequirementTrace, link.Id,
                ExactLinkLifecycleCauseKind.InternalRequirementRevision, parentRevisionId, null,
                "traced.author", "The exact upstream revision changed.", DateTimeOffset.UtcNow);
            db.Entry(link).Property<Guid?>("ExactLinkSuspectLifecycleId").CurrentValue = lifecycle.Id;
            db.ExactLinkSuspectLifecycles.Add(lifecycle); db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
            await db.SaveChangesAsync();
            linkId = link.Id;
        }

        using (var unauthenticated = factory.CreateClient())
        {
            using var refused = await unauthenticated.GetAsync($"/api/trace-links/{linkId}/lifecycle");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        using (var projectMember = factory.CreateClient())
        {
            using var login = await projectMember.PostAsJsonAsync("/api/auth/login",
                new { userName = "traced.approver", password = AeroLinkApiFactory.MemberPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await SecurityBoundaryTests.AuthorizeMutationsAsync(projectMember);
            using var refused = await projectMember.PostAsJsonAsync($"/api/trace-links/{linkId}/lifecycle/acknowledge",
                new { rationale = "An approver without engineering authority cannot assess this link." });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        await SignInAsync(client);

        using var read = await client.GetAsync($"/api/trace-links/{linkId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var initial = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Suspect", initial.GetProperty("state").GetString());
        Assert.Equal("InternalRequirementRevision", initial.GetProperty("causeKind").GetString());
        Assert.Single(initial.GetProperty("events").EnumerateArray());

        using var acknowledged = await client.PostAsJsonAsync($"/api/trace-links/{linkId}/lifecycle/acknowledge",
            new { rationale = "The downstream assessment is underway." });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal("Acknowledged", (await acknowledged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        using var resolved = await client.PostAsJsonAsync($"/api/trace-links/{linkId}/lifecycle/resolve",
            new { outcome = "ExistingDownstreamRevisionRemainsValid", rationale = "The existing downstream revision remains valid." });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var final = await (await client.GetAsync($"/api/trace-links/{linkId}/lifecycle")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Closed", final.GetProperty("state").GetString());
        Assert.Equal(3, final.GetProperty("events").GetArrayLength());
        Assert.Equal("traced.author", final.GetProperty("acknowledgedBy").GetString());

        using var ordinaryCreate = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId, sourceRevisionId = parentRevisionId, targetRevisionId = lowRevisionId,
            type = "DerivedFrom", rationale = "An ordinary exact trace has no lifecycle yet."
        });
        Assert.Equal(HttpStatusCode.Created, ordinaryCreate.StatusCode);
        var ordinaryId = (await ordinaryCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var ordinaryRead = await client.GetAsync($"/api/trace-links/{ordinaryId}/lifecycle");
        var ordinary = await ordinaryRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, ordinaryRead.StatusCode);
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("causeKind").ValueKind);
        Assert.Empty(ordinary.GetProperty("events").EnumerateArray());

        using (var freeze = factory.Services.CreateScope())
        {
            var db = freeze.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var lifecycle = ExactLinkSuspectLifecycle.Raise(projectId, ExactLinkKind.RequirementTrace, ordinaryId,
                ExactLinkLifecycleCauseKind.InternalRequirementRevision, lowRevisionId, null,
                "traced.author", "The exact upstream revision changed.", DateTimeOffset.UtcNow);
            var ordinaryLink = await db.RequirementTraces.SingleAsync(x => x.Id == ordinaryId);
            db.Entry(ordinaryLink).Property<Guid?>("ExactLinkSuspectLifecycleId").CurrentValue = lifecycle.Id;
            db.ExactLinkSuspectLifecycles.Add(lifecycle); db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
            var campaign = new ReleaseCampaign(projectId, releaseId, baselineId, "Frozen trace lifecycle", "traced.author", DateTimeOffset.UtcNow);
            campaign.StartVerification("traced.author", DateTimeOffset.UtcNow);
            campaign.BeginReleaseReview("traced.author", [("traced.author", "Trace approver")], new string('a', 64), DateTimeOffset.UtcNow);
            db.ReleaseCampaigns.Add(campaign);
            await db.SaveChangesAsync();
        }
        using var frozen = await client.PostAsJsonAsync($"/api/trace-links/{ordinaryId}/lifecycle/acknowledge",
            new { rationale = "A frozen package must refuse lifecycle mutation." });
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
    }

    [Fact]
    public async Task Configured_trace_mutation_refuses_unrelated_revisions_with_direct_orientation_message()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ConfiguredSystemLowPolicy());
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        Guid parentRevisionId;
        Guid unrelatedRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            parentRevisionId = await (from revision in db.RequirementRevisions
                                      join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                      where artifact.ProjectId == projectId && artifact.BaseNumber == parentNumber
                                      select revision.Id).SingleAsync();
            var originId = await db.SystemChangeRequests.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            var baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
            var unrelated = new RequirementArtifact(projectId, "SYSR-009999", RequirementLevel.System, DateTimeOffset.UtcNow);
            var extraRevision = new RequirementRevision(unrelated.Id, 0, "An unrelated system revision.", "Rationale", "Test",
                RequirementRevisionState.Active, originId, baselineId, DateTimeOffset.UtcNow);
            db.AddRange(unrelated, extraRevision);
            await db.SaveChangesAsync();
            unrelatedRevisionId = extraRevision.Id;
        }

        using var response = await client.PostAsJsonAsync("/api/trace-links", new
        {
            projectId,
            sourceRevisionId = parentRevisionId,
            targetRevisionId = unrelatedRevisionId,
            type = "DerivedFrom",
            rationale = "Configured unrelated revisions must be refused."
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("configured child", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct parent", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Modification_picker_searches_requirement_wording_as_well_as_identifier()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync(
            $"/api/authoring/requirements?projectId={projectId}&scope=System&search=oceanic%20waypoints");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Contains(rows.EnumerateArray(), x => x.GetProperty("baseNumber").GetString() == parentNumber);
    }

    [Fact]
    public async Task Modification_picker_hydrates_the_current_exact_upward_allocation()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync(
            $"/api/authoring/requirements?projectId={projectId}&scope=Software&search={childNumber}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var child = Assert.Single(rows.EnumerateArray(), x => x.GetProperty("baseNumber").GetString() == childNumber);
        var parentRevisionId = Assert.Single(child.GetProperty("currentUpstreamRevisionIds").EnumerateArray()).GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var parentId = db.Requirements.Single(x => x.ProjectId == projectId && x.BaseNumber == parentNumber).Id;
        Assert.Equal(db.RequirementRevisions.Single(x => x.ArtifactId == parentId).Id, parentRevisionId);
    }

    [Fact]
    public async Task Upstream_picker_hydrates_an_exact_selected_parent_from_an_older_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);

        Guid releaseId, parentRevisionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var baseline = db.CandidateBaselines.Single(x => x.ProjectId == projectId);
            releaseId = db.Releases.Single(x => x.ProjectId == projectId).Id;
            var parent = db.Requirements.Single(x => x.ProjectId == projectId && x.BaseNumber == parentNumber);
            var parentRevision = db.RequirementRevisions.Single(x => x.ArtifactId == parent.Id);
            parentRevisionId = parentRevision.Id;
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RequirementsMaterializedAt, DateTimeOffset.UtcNow));
            await db.RequirementRevisions.Where(x => x.Id == parentRevision.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, RequirementRevisionState.Superseded));
        }

        var requirements = await client.GetFromJsonAsync<JsonElement>(
            $"/api/authoring/requirements?projectId={projectId}&scope=Software&search={childNumber}");
        var child = Assert.Single(requirements.EnumerateArray(), x => x.GetProperty("baseNumber").GetString() == childNumber);
        Assert.Equal(parentRevisionId, Assert.Single(child.GetProperty("currentUpstreamRevisionIds").EnumerateArray()).GetGuid());

        using var response = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={projectId}&releaseId={releaseId}&childLevel=HighLevel&selected={parentRevisionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var selected = Assert.Single(rows.EnumerateArray());
        Assert.Equal(parentRevisionId, selected.GetProperty("revisionId").GetGuid());
        Assert.Equal($"{parentNumber}.00", selected.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task Upstream_picker_and_submission_validation_enforce_level_project_build_and_review_rules()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);
        var ladder = await MaterializeLadderAsync(factory, projectId, parentNumber, childNumber);

        using var systemPicker = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={projectId}&releaseId={ladder.ReleaseId}&childLevel=System");
        Assert.Equal(HttpStatusCode.BadRequest, systemPicker.StatusCode);
        Assert.Contains("Only HLR and LLR proposals", await systemPicker.Content.ReadAsStringAsync());

        using var unknownLevelPicker = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={projectId}&releaseId={ladder.ReleaseId}&childLevel=99");
        Assert.Equal(HttpStatusCode.BadRequest, unknownLevelPicker.StatusCode);
        Assert.Contains("Only HLR and LLR proposals", await unknownLevelPicker.Content.ReadAsStringAsync());

        using (var systemDraftWithParent = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId,
            targetReleaseId = ladder.ReleaseId,
            type = "System",
            title = "Reject System upstream allocation",
            problem = "P", analysis = "A", solution = "S",
            requirementChanges = new[]
            {
                new
                {
                    baseNumber = parentNumber, revision = 1, level = "System", kind = "Modify",
                    statement = "The System requirement remains controlled.",
                    rationale = "Characterization", verificationMethod = "Test",
                    upstreamRevisionIds = new[] { ladder.SystemRevisionId }
                }
            }
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, systemDraftWithParent.StatusCode);
            Assert.Contains("System requirements cannot carry", await systemDraftWithParent.Content.ReadAsStringAsync());
        }

        using var hlrPicker = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={projectId}&releaseId={ladder.ReleaseId}&childLevel=HighLevel");
        Assert.Equal(HttpStatusCode.OK, hlrPicker.StatusCode);
        var hlrRows = JsonSerializer.Deserialize<JsonElement>(await hlrPicker.Content.ReadAsStringAsync());
        var hlrParent = Assert.Single(hlrRows.EnumerateArray());
        Assert.Equal(ladder.SystemRevisionId, hlrParent.GetProperty("revisionId").GetGuid());
        Assert.Equal($"{parentNumber}.00", hlrParent.GetProperty("displayNumber").GetString());
        Assert.Equal("System", hlrParent.GetProperty("level").GetString());

        using var llrPicker = await client.GetAsync(
            $"/api/authoring/upstream-requirements?projectId={projectId}&releaseId={ladder.ReleaseId}&childLevel=LowLevel");
        Assert.Equal(HttpStatusCode.OK, llrPicker.StatusCode);
        var llrRows = JsonSerializer.Deserialize<JsonElement>(await llrPicker.Content.ReadAsStringAsync());
        var llrParent = Assert.Single(llrRows.EnumerateArray());
        Assert.Equal(ladder.HighRevisionId, llrParent.GetProperty("revisionId").GetGuid());
        Assert.Equal($"{childNumber}.00", llrParent.GetProperty("displayNumber").GetString());
        Assert.Equal("HighLevel", llrParent.GetProperty("level").GetString());

        async Task<JsonElement> CreateDraftAsync(string level, string baseNumber, IReadOnlyList<Guid> upstream,
            bool derived = false, string impact = "{}")
        {
            using var response = await client.PostAsJsonAsync("/api/change-request-drafts", new
            {
                projectId,
                targetReleaseId = ladder.ReleaseId,
                type = "Software",
                softwareLevel = level,
                title = $"Characterize {level} upstream {Guid.NewGuid():N}",
                problem = "P", analysis = "A", solution = "S",
                requirementChanges = new[]
                {
                    new
                    {
                        baseNumber, revision = 1, level, kind = "Modify",
                        statement = $"The {level} requirement shall remain controlled.",
                        rationale = "Characterization", verificationMethod = "Test",
                        attributesJson = derived ? "{\"owner\":\"traced.author\",\"criticality\":\"Safety Significant\"}" : "{}",
                        impactDispositionJson = impact, isDerived = derived,
                        upstreamRevisionIds = upstream
                    }
                }
            });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"{(int)response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<JsonElement>(body);
        }

        async Task<HttpResponseMessage> TryCreateDraftAsync(string level, string baseNumber,
            IReadOnlyList<Guid> upstream, bool derived = false, string rationale = "Characterization")
        {
            return await client.PostAsJsonAsync("/api/change-request-drafts", new
            {
                projectId,
                targetReleaseId = ladder.ReleaseId,
                type = "Software",
                softwareLevel = level,
                title = $"Reject {level} upstream {Guid.NewGuid():N}",
                problem = "P", analysis = "A", solution = "S",
                requirementChanges = new[]
                {
                    new
                    {
                        baseNumber, revision = 1, level, kind = "Modify",
                        statement = "The requirement shall remain controlled.",
                        rationale, verificationMethod = "Test",
                        attributesJson = derived ? "{\"owner\":\"traced.author\",\"criticality\":\"Safety Significant\"}" : "{}",
                        impactDispositionJson = "{}", isDerived = derived,
                        upstreamRevisionIds = upstream
                    }
                }
            });
        }

        async Task<long> AuthorNoUpstreamAsync(Guid changeRequestId)
        {
            using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
                new { artifactType = "SCR", artifactId = changeRequestId, leaseMinutes = 15 });
            Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
            var session = JsonSerializer.Deserialize<JsonElement>(await checkout.Content.ReadAsStringAsync());
            var sessionId = session.GetProperty("id").GetGuid();
            var expectedVersion = session.GetProperty("version").GetInt64();
            var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
            draft["upstreamLinks"] = new JsonArray();
            draft["noUpstreamRationale"] = "The parent change is not applicable to this controlled HLR test.";
            using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
                new { expectedVersion, draftJson = draft.ToJsonString() });
            Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
            expectedVersion = JsonSerializer.Deserialize<JsonElement>(await autosave.Content.ReadAsStringAsync())
                .GetProperty("version").GetInt64();
            using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
                new { expectedVersion });
            Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(await checkIn.Content.ReadAsStringAsync())
                .GetProperty("resultingArtifactVersion").GetInt64();
        }

        // A normal HLR and LLR draft may be saved while its engineering decisions are incomplete.
        using (var incompleteHlr = await TryCreateDraftAsync("HighLevel", childNumber, []))
            Assert.Equal(HttpStatusCode.Created, incompleteHlr.StatusCode);
        using (var incompleteLlr = await TryCreateDraftAsync("LowLevel", "LLR-000503", []))
            Assert.Equal(HttpStatusCode.Created, incompleteLlr.StatusCode);

        // Derived software uses its explicit rationale and cannot also carry an authored parent allocation.
        using (var derivedWithParent = await TryCreateDraftAsync("HighLevel", childNumber,
                   [ladder.SystemRevisionId], derived: true))
        {
            Assert.Equal(HttpStatusCode.BadRequest, derivedWithParent.StatusCode);
            Assert.Contains("derived requirement", await derivedWithParent.Content.ReadAsStringAsync());
        }

        // An unfinished ordinary Draft is allowed, but an explicit Derived decision is not meaningful
        // without its engineering rationale even before the package is submitted for review.
        using (var derivedWithoutRationale = await TryCreateDraftAsync("HighLevel", childNumber, [],
                   derived: true, rationale: "  "))
        {
            Assert.Equal(HttpStatusCode.BadRequest, derivedWithoutRationale.StatusCode);
            Assert.Contains("explicit engineering rationale", await derivedWithoutRationale.Content.ReadAsStringAsync());
        }

        Guid foreignRevisionId, sameProjectOutOfBaselineRevisionId;
        using (var foreignScope = factory.Services.CreateScope())
        {
            var foreignDb = foreignScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var foreignProgram = new ProgramRecord("Foreign Upstream Program", "FUP");
            var foreignProject = new ProjectRecord(foreignProgram.Id, "Foreign Upstream Software", "Foreign Upstream Software");
            var foreignRelease = new SoftwareRelease(foreignProject.Id, "1.6", false);
            var foreignBaseline = new CandidateBaseline("BL-09901", 0, foreignProject.Id, foreignRelease.Id, null,
                "Foreign baseline", "traced.author", now);
            var foreignRequest = new SystemChangeRequest("SRCR-09901", 0, foreignProject.Id, foreignRelease.Id,
                "Foreign upstream", "P", "A", "S", "traced.author", now);
            var foreignArtifact = new RequirementArtifact(foreignProject.Id, "SYSR-09901", RequirementLevel.System, now);
            var foreignRevision = new RequirementRevision(foreignArtifact.Id, 0, "The foreign parent is not in this build.",
                "R", "Test", RequirementRevisionState.Active, foreignRequest.Id, foreignBaseline.Id, now);
            foreignDb.AddRange(foreignProgram, foreignProject, foreignRelease, foreignBaseline, foreignRequest,
                foreignArtifact, foreignRevision);
            await foreignDb.SaveChangesAsync();
            foreignRevisionId = foreignRevision.Id;
        }

        using (var outOfBaselineScope = factory.Services.CreateScope())
        {
            var outOfBaselineDb = outOfBaselineScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var baselineId = await outOfBaselineDb.CandidateBaselines.Where(x => x.ProjectId == projectId)
                .Select(x => x.Id).SingleAsync();
            var originId = await outOfBaselineDb.SystemChangeRequests.Where(x => x.ProjectId == projectId && x.BaseNumber == "SRCR-00500")
                .Select(x => x.Id).SingleAsync();
            var artifact = new RequirementArtifact(projectId, "SYSR-000504", RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, "The same-project parent is outside the effective build.",
                "R", "Test", RequirementRevisionState.Active, originId, baselineId, now);
            outOfBaselineDb.AddRange(artifact, revision);
            await outOfBaselineDb.SaveChangesAsync();
            sameProjectOutOfBaselineRevisionId = revision.Id;
        }

        // A non-derived proposal accepts only distinct, exact same-project expected-level revisions.
        using (var wrongLevel = await TryCreateDraftAsync("HighLevel", childNumber, [ladder.LowRevisionId]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongLevel.StatusCode);
            Assert.Contains("current System", await wrongLevel.Content.ReadAsStringAsync());
        }
        using (var wrongProject = await TryCreateDraftAsync("HighLevel", childNumber, [foreignRevisionId]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongProject.StatusCode);
            Assert.Contains("current System", await wrongProject.Content.ReadAsStringAsync());
        }
        using (var outOfBaseline = await TryCreateDraftAsync("HighLevel", childNumber,
                   [sameProjectOutOfBaselineRevisionId]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, outOfBaseline.StatusCode);
            Assert.Contains("current System", await outOfBaseline.Content.ReadAsStringAsync());
        }
        using (var duplicate = await TryCreateDraftAsync("HighLevel", childNumber,
                   [ladder.SystemRevisionId, ladder.SystemRevisionId]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            Assert.Contains("distinct controlled revision", await duplicate.Content.ReadAsStringAsync());
        }

        // Review-ready validation requires an exact current parent from the same project/build.
        var incompleteForReview = await CreateDraftAsync("HighLevel", childNumber, [], impact: RequirementAuthoringJson.CompleteImpactDispositions);
        using (var noParent = await client.PostAsJsonAsync(
                   $"/api/change-requests/{incompleteForReview.GetProperty("id").GetGuid()}/submit",
                   new { expectedVersion = incompleteForReview.GetProperty("version").GetInt64(), mode = "Sequential",
                       approvers = new[] { new { userId = "traced.approver", name = "Traced Approver" } } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noParent.StatusCode);
            Assert.Contains("Allocate the proposed HLR", await noParent.Content.ReadAsStringAsync());
        }

        var validHlr = await CreateDraftAsync("HighLevel", childNumber, [ladder.SystemRevisionId], impact: RequirementAuthoringJson.CompleteImpactDispositions);
        var validHlrVersion = await AuthorNoUpstreamAsync(validHlr.GetProperty("id").GetGuid());
        using (var submitHlr = await client.PostAsJsonAsync(
                   $"/api/change-requests/{validHlr.GetProperty("id").GetGuid()}/submit",
                   new { expectedVersion = validHlrVersion, mode = "Sequential",
                       approvers = new[] { new { userId = "traced.approver", name = "Traced Approver" } } }))
        {
            var body = await submitHlr.Content.ReadAsStringAsync();
            Assert.True(submitHlr.StatusCode == HttpStatusCode.OK, $"{(int)submitHlr.StatusCode}: {body}");
        }

        var validLlr = await CreateDraftAsync("LowLevel", "LLR-000503", [ladder.HighRevisionId], impact: RequirementAuthoringJson.CompleteImpactDispositions);
        var validLlrVersion = await AuthorNoUpstreamAsync(validLlr.GetProperty("id").GetGuid());
        using (var submitLlr = await client.PostAsJsonAsync(
                   $"/api/change-requests/{validLlr.GetProperty("id").GetGuid()}/submit",
                   new { expectedVersion = validLlrVersion, mode = "Sequential",
                       approvers = new[] { new { userId = "traced.approver", name = "Traced Approver" } } }))
        {
            var body = await submitLlr.Content.ReadAsStringAsync();
            Assert.True(submitLlr.StatusCode == HttpStatusCode.OK, $"{(int)submitLlr.StatusCode}: {body}");
        }

        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var hlrChange = await assertDb.RequirementChanges.SingleAsync(x => x.ChangeRequestId == validHlr.GetProperty("id").GetGuid());
        var llrChange = await assertDb.RequirementChanges.SingleAsync(x => x.ChangeRequestId == validLlr.GetProperty("id").GetGuid());
        Assert.Equal([ladder.SystemRevisionId], JsonSerializer.Deserialize<Guid[]>(hlrChange.ProposedUpstreamRevisionIdsJson)!);
        Assert.Equal([ladder.HighRevisionId], JsonSerializer.Deserialize<Guid[]>(llrChange.ProposedUpstreamRevisionIdsJson)!);
    }

    [Fact]
    public async Task A_suspect_link_is_reported_separately_from_the_approved_procedure_lifecycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory, suspectCoverage: true);
        await SignInAsync(client);

        var traced = await client.GetFromJsonAsync<Traced>(
            $"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}", Json);

        var procedure = Assert.Single(traced!.CoveringProcedures);
        Assert.Equal("Approved", procedure.State);
        Assert.True(procedure.IsSuspect);
        Assert.Equal("Suspect", procedure.CoverageState);
    }

    /// <summary>
    /// The direction matters. A change to the parent propagates to what derives from it; opening the child must
    /// not report its parent as something this change affects, or an author closes a trace disposition against
    /// the wrong set.
    /// </summary>
    [Fact]
    public async Task Only_what_derives_from_the_requirement_is_reported_not_what_it_derives_from()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={childNumber}");
        var traced = JsonSerializer.Deserialize<Traced>(await response.Content.ReadAsStringAsync(), Json)!;

        Assert.True(traced.Known);
        Assert.Empty(traced.DerivedRequirements);
        Assert.Empty(traced.CoveringProcedures);
    }

    /// <summary>
    /// A requirement being introduced does not exist yet. That is the ordinary case for the commonest kind of
    /// proposal, so it answers rather than 404s — an authoring surface that errors on a new requirement is worse
    /// than one that says there is nothing recorded.
    /// </summary>
    [Fact]
    public async Task A_requirement_that_does_not_exist_yet_answers_rather_than_failing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber=SYSR-999999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var traced = JsonSerializer.Deserialize<Traced>(await response.Content.ReadAsStringAsync(), Json)!;
        Assert.False(traced.Known);
        Assert.Empty(traced.DerivedRequirements);
        Assert.Empty(traced.CoveringProcedures);
    }

    /// <summary>
    /// The one that matters: reading the traces must not decide anything.
    ///
    /// The five dispositions are the author's, and a proposal is not review-ready until a person has closed each
    /// one. If reading this endpoint quietly marked trace or verification as decided, a change request could
    /// reach review carrying a machine's opinion dressed as an engineer's.
    /// </summary>
    [Fact]
    public async Task Reading_the_traces_changes_no_disposition()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        var releaseId = await ReleaseIdAsync(factory, projectId);
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", new
        {
            projectId,
            targetReleaseId = releaseId,
            title = "Modify oceanic sequencing",
            problem = "P", analysis = "A", solution = "S",
            type = "System",
            requirementChanges = new[]
            {
                new { baseNumber = parentNumber, revision = 1, level = "System", kind = "Modify",
                      statement = "The FMS shall sequence oceanic waypoints deterministically.",
                      rationale = "Clarified", verificationMethod = "Test" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var changeRequestId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();

        using (var read = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var detail = await client.GetAsync($"/api/change-requests/{changeRequestId}");
        var change = JsonSerializer.Deserialize<JsonElement>(await detail.Content.ReadAsStringAsync())
            .GetProperty("requirementChanges").EnumerateArray().Single();
        var dispositions = JsonSerializer.Deserialize<Dictionary<string, string>>(
            change.GetProperty("impactDispositionJson").GetString() ?? "{}") ?? [];

        // Every area still Pending, or absent entirely. Either way, nothing was decided on the author's behalf.
        foreach (var area in new[] { "trace", "verification", "documents", "baseline", "collaboration" })
            Assert.True(!dispositions.TryGetValue(area, out var value) || value == "Pending",
                $"{area} was set to '{dispositions.GetValueOrDefault(area)}' by reading the traces.");
    }

    private static async Task<Guid> ReleaseIdAsync(AeroLinkApiFactory factory, Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await Task.FromResult(db.Releases.Single(x => x.ProjectId == projectId).Id);
    }

    private static ILadderPolicy ConfiguredSystemLowPolicy()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
        configuration.Steps.Add(system);
        configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, configuration.ProjectId,
            system.Id, low.Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static async Task<(Guid ReleaseId, Guid SystemRevisionId, Guid HighRevisionId, Guid LowRevisionId)>
        MaterializeLadderAsync(AeroLinkApiFactory factory, Guid projectId, string parentNumber, string childNumber)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.ProjectId == projectId);
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync();
        var artifacts = await db.Requirements.Where(x => x.ProjectId == projectId &&
                (x.BaseNumber == parentNumber || x.BaseNumber == childNumber || x.BaseNumber == "LLR-000503"))
            .ToDictionaryAsync(x => x.BaseNumber);
        var revisions = await db.RequirementRevisions
            .Where(x => artifacts.Values.Select(a => a.Id).Contains(x.ArtifactId))
            .ToDictionaryAsync(x => x.ArtifactId);
        var selectedRevisionIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id)
            .Select(x => x.RevisionId)
            .ToHashSetAsync();
        var selections = new[] { parentNumber, childNumber, "LLR-000503" }
            .Select(number => (Artifact: artifacts[number], Revision: revisions[artifacts[number].Id]))
            .Where(x => selectedRevisionIds.Add(x.Revision.Id))
            .Select(x => new BaselineRequirementSelection(baseline.Id, x.Artifact.Id, x.Revision.Id));
        db.BaselineRequirements.AddRange(selections);
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, DateTimeOffset.UtcNow));
        return (releaseId, revisions[artifacts[parentNumber].Id].Id, revisions[artifacts[childNumber].Id].Id,
            revisions[artifacts["LLR-000503"].Id].Id);
    }
}
