using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportApiTests
{
    [Fact]
    public async Task Generic_links_fail_closed_for_controlled_or_unknown_semantics_and_keep_context_links_versioned_and_scoped()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        Guid projectId, requirementId, otherRequirementId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("PR relationship policy", $"RL{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var otherProject = new ProjectRecord(program.Id, "Other Product", "Other System");
            var requirement = new RequirementArtifact(project.Id, "SYSR-008448", RequirementLevel.System, DateTimeOffset.UtcNow);
            var otherRequirement = new RequirementArtifact(otherProject.Id, "SYSR-008449", RequirementLevel.System, DateTimeOffset.UtcNow);
            db.AddRange(program, project, otherProject, requirement, otherRequirement); await db.SaveChangesAsync();
            projectId = project.Id; requirementId = requirement.Id; otherRequirementId = otherRequirement.Id;
        }
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, title = "Controlled relationship forgery", problem = "A generic caller can forge evidence."
        });
        var report = await created.Content.ReadFromJsonAsync<JsonElement>();
        var reportId = report.GetProperty("id").GetGuid();
        var version = report.GetProperty("version").GetInt64();

        foreach (var definition in ProblemReportRelationshipPolicy.Definitions.Where(item => item.IsControlled))
        {
            using var rejected = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
            {
                expectedVersion = version, artifactType = definition.ArtifactType, artifactId = requirementId,
                relationship = definition.Relationship
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("problem_report_relationship_not_generic", error.GetProperty("code").GetString());
        }
        using (var rejected = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            expectedVersion = version, artifactType = "Requirement", artifactId = requirementId, relationship = "CallerInventedEvidence"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("problem_report_relationship_not_generic", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var missingVersion = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            artifactType = "Requirement", artifactId = requirementId, relationship = ProblemReportRelationshipPolicy.AffectedRequirement
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
            Assert.Equal("expected_version_required", (await missingVersion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        using var linked = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            expectedVersion = version, artifactType = "Requirement", artifactId = requirementId,
            relationship = ProblemReportRelationshipPolicy.AffectedRequirement
        });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var linkedBody = await linked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(version + 1, linkedBody.GetProperty("version").GetInt64());

        using var stale = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            expectedVersion = version, artifactType = "Requirement", artifactId = requirementId,
            relationship = ProblemReportRelationshipPolicy.AffectedRequirement
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_version", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var crossProject = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            expectedVersion = version + 1, artifactType = "Requirement", artifactId = otherRequirementId,
            relationship = ProblemReportRelationshipPolicy.AffectedRequirement
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var stored = await db.ProblemReports.SingleAsync(item => item.Id == reportId);
            stored.ApplyDisposition("admin", ProblemReportDisposition.CannotReproduce,
                "Closed for the terminal-state relationship check.", null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using var terminal = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/links", new
        {
            expectedVersion = version + 2, artifactType = "Requirement", artifactId = requirementId,
            relationship = ProblemReportRelationshipPolicy.AffectedRequirement
        });
        Assert.Equal(HttpStatusCode.BadRequest, terminal.StatusCode);
        Assert.Contains("closed or dispositioned", (await terminal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var detail = await client.GetAsync($"/api/problem-reports/{reportId}");
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(detailBody.GetProperty("links").EnumerateArray());
        Assert.Contains(detailBody.GetProperty("revisions").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "ContextArtifactLinked");
    }

    [Fact]
    public async Task Detail_keeps_legacy_links_readable_without_promoting_untrusted_controlled_evidence()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        Guid reportId, forgedChangeId, forgedExecutionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("PR legacy relationship", $"LR{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var report = new ProblemReport(project.Id, "PR-04480", "Legacy forged links", "Links predate relationship policy.", "", "admin", now);
            forgedChangeId = Guid.NewGuid(); forgedExecutionId = Guid.NewGuid(); reportId = report.Id;
            db.AddRange(program, project, report,
                new ProblemReportLink(report.Id, "ChangeRequest", forgedChangeId, ProblemReportRelationshipPolicy.ApprovedCorrectiveAction, "legacy", now),
                new ProblemReportLink(report.Id, "TestExecution", forgedExecutionId, ProblemReportRelationshipPolicy.ResolutionVerification, "legacy", now));
            await db.SaveChangesAsync();
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}");
        Assert.Equal(2, detail.GetProperty("links").GetArrayLength());
        Assert.Empty(detail.GetProperty("approvedCorrectiveActions").EnumerateArray());
        Assert.Empty(detail.GetProperty("testEvidence").EnumerateArray());
        Assert.Contains(detail.GetProperty("links").EnumerateArray(), item => item.GetProperty("artifactId").GetGuid() == forgedChangeId);
        Assert.Contains(detail.GetProperty("links").EnumerateArray(), item => item.GetProperty("artifactId").GetGuid() == forgedExecutionId);
    }

    [Fact]
    public async Task Build_queue_is_numeric_oldest_first_and_reports_when_more_than_ten_match()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        Guid projectId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("PR queue Program", $"PQ{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync(); projectId = project.Id; releaseId = release.Id;
        }
        for (var index = 1; index <= 12; index++)
        {
            using var created = await client.PostAsJsonAsync("/api/problem-reports", new
            {
                projectId, releaseId, title = $"Queue report {index}", problem = $"Observed anomaly {index}."
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var queue = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports?projectId={projectId}&releaseId={releaseId}");
        Assert.Equal(12, queue.GetProperty("totalCount").GetInt32());
        var numbers = queue.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("reportNumber").GetString()).ToArray();
        Assert.Equal(10, numbers.Length);
        Assert.Equal(Enumerable.Range(1, 10).Select(x => $"PR-{x:D5}"), numbers);
    }

    [Fact]
    public async Task Draft_fields_SCCB_lifecycle_filters_and_linked_change_implementation_are_audited()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        Guid projectId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("PR MVP Program", $"PM{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync(); projectId = project.Id; releaseId = release.Id;
        }
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, title = "Intermittent position disagreement", problem = "The alert clears before the disagreement ends.",
            problemRich = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"The alert clears before the disagreement ends.\"}]}",
            additionalInformation = "Observed during three approaches.", additionalInformationRich = "{\"blocks\":[]}",
            systemAircraftImpact = "Flight crew may miss a persistent disagreement.",
            impactAssessmentJson = "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"Yes\",\"Llr\":\"Unknown\",\"Code\":\"Yes\",\"Tests\":\"Yes\",\"Documents\":\"No\",\"SystemAircraft\":\"Yes\",\"Safety\":\"Unknown\"}"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var report = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = report.GetProperty("id").GetGuid(); var version = report.GetProperty("version").GetInt64();
        Assert.Equal("Draft", report.GetProperty("state").GetString());

        // Details are edited under the universal controlled-editing lease, the same as every other
        // controlled record, rather than through a second write path of the report's own.
        version = await ProblemReportCheckoutApiTests.EditUnderCheckoutAsync(client, id, draft =>
        {
            draft["title"] = "Intermittent position-source disagreement";
            draft["additionalInformation"] = "Observed twice after data reload.";
            draft["priority"] = "Normal";
        });

        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb", new { expectedVersion = version });
        version = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var opened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/sccb/open", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        using var change = await client.PostAsJsonAsync("/api/change-requests", new { projectId, targetReleaseId = releaseId, type = "Software", softwareLevel = "HighLevel", title = "Keep disagreement alert active", problem = "P", analysis = "A", solution = "S", problemReportIds = new[] { id } });
        Assert.Equal(HttpStatusCode.Created, change.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Implementing", detail.GetProperty("state").GetString());
        Assert.Equal("admin", detail.GetProperty("reportedBy").GetString());
        Assert.Equal("admin", detail.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal(releaseId, detail.GetProperty("targetReleaseId").GetGuid());
        Assert.Contains("SystemRequirements", detail.GetProperty("impactAssessmentJson").GetString());
        Assert.Contains(detail.GetProperty("revisions").EnumerateArray(), revision => revision.GetProperty("eventType").GetString() == "ImplementationStartedByLinkedChangeRequest");

        var filtered = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports?projectId={projectId}&releaseId={releaseId}&state=Implementing&severity=Major&priority=Normal&owner=admin&search=disagreement");
        Assert.Equal(id, Assert.Single(filtered.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(ChangeRequestType.System)]
    [InlineData(ChangeRequestType.Software)]
    public async Task A_problem_report_can_drive_each_engineering_change_request_type(ChangeRequestType type)
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        Guid projectId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("PR change Program", $"PC{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            db.AddRange(program, project, release); await db.SaveChangesAsync();
            projectId = project.Id; releaseId = release.Id;
        }
        using var createdReport = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId, releaseId, title = "Position source disagreement", problem = "Sources disagree during approach."
        });
        Assert.Equal(HttpStatusCode.Created, createdReport.StatusCode);
        var reportBody = await createdReport.Content.ReadFromJsonAsync<JsonElement>();
        var reportId = reportBody.GetProperty("id").GetGuid();

        using var createdChange = await client.PostAsJsonAsync("/api/change-requests", new
        {
            projectId, targetReleaseId = releaseId, type = type.ToString(), title = "Correct source selection",
            problem = "P", analysis = "A", solution = "S", softwareLevel = type == ChangeRequestType.Software ? "HighLevel" : null, problemReportIds = new[] { reportId }
        });
        var text = await createdChange.Content.ReadAsStringAsync();
        Assert.True(createdChange.StatusCode == HttpStatusCode.Created, text);
        var changeId = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        var linked = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/linked/ChangeRequest/{changeId}");
        Assert.Equal(reportId, Assert.Single(linked.EnumerateArray()).GetProperty("id").GetGuid());

        var changeNumber = JsonDocument.Parse(text).RootElement.GetProperty("displayNumber").GetString();
        var reportNumber = reportBody.GetProperty("displayNumber").GetString();
        var changeSearch = await client.GetFromJsonAsync<JsonElement>(
            $"/api/search?projectId={projectId}&releaseId={releaseId}&query={changeNumber}");
        var reportSearch = await client.GetFromJsonAsync<JsonElement>(
            $"/api/search?projectId={projectId}&releaseId={releaseId}&query={reportNumber}");
        Assert.Equal(changeNumber, Assert.Single(changeSearch.GetProperty("items").EnumerateArray(), x =>
            x.GetProperty("id").GetGuid() == changeId).GetProperty("identifier").GetString());
        Assert.Equal(reportNumber, Assert.Single(reportSearch.GetProperty("items").EnumerateArray(), x =>
            x.GetProperty("id").GetGuid() == reportId).GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task Problem_report_is_server_numbered_and_cannot_be_closed_by_its_owner()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);

        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Unexpected reset", problem = "The unit resets during a route update.", analysis = "", classification = "Verification failure", severity = "High", priority = "Urgent", origin = "Test execution", affectedConfiguration = "Build 1.6.0" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var report = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = report.GetProperty("id").GetGuid();

        using var opened = await client.GetAsync($"/api/problem-reports/{id}"); var openedText = await opened.Content.ReadAsStringAsync(); Assert.True(opened.IsSuccessStatusCode, openedText); var openedBody = JsonDocument.Parse(openedText).RootElement;
        Assert.Equal("PR-00001", openedBody.GetProperty("reportNumber").GetString());
        Assert.Equal("Draft", openedBody.GetProperty("state").GetString());
        var version = openedBody.GetProperty("version").GetInt64();
        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode); version = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var sccbOpen = await client.PostAsJsonAsync($"/api/problem-reports/{id}/sccb/open", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, sccbOpen.StatusCode); version = (await sccbOpen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var investigation = await client.PostAsJsonAsync($"/api/problem-reports/{id}/investigation", new { expectedVersion = version, analysis = "Reproduced during integration test.", rootCause = "Timeout race", effects = "Navigation reset", containment = "Disable retry" });
        Assert.Equal(HttpStatusCode.OK, investigation.StatusCode); version = (await investigation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var proposal = await client.PostAsJsonAsync($"/api/problem-reports/{id}/resolution", new { expectedVersion = version, correctiveAction = "Serialize reset commands." });
        Assert.Equal(HttpStatusCode.OK, proposal.StatusCode);

        using var close = await client.PostAsJsonAsync($"/api/problem-reports/{id}/closure/approve", new { expectedVersion = version + 1 });
        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Verifying", detail.GetProperty("state").GetString());
        Assert.True(detail.GetProperty("revisions").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Legacy_closed_report_is_identified_truthfully_instead_of_fabricating_a_package()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);
        Guid reportId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var report = new ProblemReport(projectId, "PR-LEGACY-CLOSED", "Historical closure",
                "This report closed before frozen packages existed.", "", "admin", now,
                responsibleEngineerId: "admin");
            report.ReadyForSccb("admin", now.AddMinutes(1));
            report.OpenBySccb("sccb", now.AddMinutes(2));
            report.BeginInvestigation("admin", "Analysis", "Cause", "Effect", "", now.AddMinutes(3));
            report.ProposeResolution("admin", "Historical correction", now.AddMinutes(4));
            report.RecordResolutionVerification("admin", Guid.NewGuid(), now.AddMinutes(5));
            report.ApproveClosure("legacy.quality", Guid.NewGuid(), now.AddMinutes(6));
            db.ProblemReports.Add(report); await db.SaveChangesAsync(); reportId = report.Id;
        }

        using var response = await client.GetAsync($"/api/problem-reports/{reportId}/closure-package");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pr_closure_package_legacy_unfrozen", body.GetProperty("code").GetString());
        Assert.Equal("LegacyClosureNotFrozen", body.GetProperty("provenance").GetString());
    }

    [Fact]
    public async Task Dashboard_exposes_unwaived_release_blockers_as_exact_records()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Critical data loss", problem = "Unexpected data loss observed.", classification = "Software anomaly", severity = "Critical", priority = "Urgent", origin = "Manual report" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var detailResponse = await client.GetAsync($"/api/problem-reports/{id}"); var detailText = await detailResponse.Content.ReadAsStringAsync(); Assert.True(detailResponse.IsSuccessStatusCode, detailText); var detail = JsonDocument.Parse(detailText).RootElement; var version = detail.GetProperty("version").GetInt64();
        using var blocked = await client.PostAsJsonAsync($"/api/problem-reports/{id}/blocker", new { expectedVersion = version, isReleaseBlocker = true, waiverRationale = "" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        var dashboard = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/dashboard?projectId={projectId}");
        Assert.Equal(1, dashboard.GetProperty("summary").GetProperty("releaseBlockers").GetInt32());
        Assert.Contains(dashboard.GetProperty("attention").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Universal_search_and_artifact_inspector_expose_problem_reports()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient(); await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Navigation data corruption", problem = "Route data becomes inconsistent after reset.", classification = "Software anomaly", severity = "Critical", priority = "Urgent", origin = "Manual report" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var results = await client.GetFromJsonAsync<JsonElement>($"/api/search?projectId={projectId}&query=corruption");
        Assert.Contains(results.GetProperty("items").EnumerateArray(), x => x.GetProperty("kind").GetString() == "problem-report" && x.GetProperty("id").GetGuid() == id);
        var artifact = await client.GetFromJsonAsync<JsonElement>($"/api/artifacts/problem-report/{id}");
        Assert.Equal("PR-00001.00", artifact.GetProperty("identifier").GetString());
        Assert.Equal("Critical", artifact.GetProperty("details").GetProperty("severity").GetString());
    }

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Problem Report Program", $"PR{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    internal static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap") { Content = JsonContent.Create(new { displayName = "AeroLink Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }) };
        bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret); Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(bootstrap)).StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
