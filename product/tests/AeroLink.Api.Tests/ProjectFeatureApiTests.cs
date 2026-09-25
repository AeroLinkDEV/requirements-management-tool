using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>#1113: a project's switched-on features, and what a project without Verification or Release does instead.</summary>
public sealed class ProjectFeatureApiTests
{
    [Fact]
    public async Task Features_default_on_follow_dependencies_switch_off_only_while_empty_and_gate_new_records()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        Guid projectId, otherProjectId;
        var member = $"pf.member.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Feature program", "PFP");
            var project = new ProjectRecord(program.Id, "Feature Project", "FMS");
            var other = new ProjectRecord(program.Id, "Other Project", "FMS");
            var account = new UserAccount(member, member, $"{member}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, other, account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.SoftwareEngineer, "test.setup", now));
            db.ProblemReports.Add(new ProblemReport(other.Id, "PR-00001", "Existing", "Existing report.", "", "admin", now));
            await db.SaveChangesAsync();
            projectId = project.Id; otherProjectId = other.Id;
        }
        string Url(Guid id) => $"/api/projects/{id}/features";
        Task<HttpResponseMessage> PutAsync(Guid id, long version, params string[] enabled) =>
            client.PutAsJsonAsync($"/api/projects/{id}/features", new { expectedVersion = version, reason = "Problem Reports trial project", enabled });

        // No row: every feature, nothing persisted.
        var initial = await client.GetFromJsonAsync<JsonElement>(Url(projectId));
        Assert.False(initial.GetProperty("persisted").GetBoolean());
        Assert.Equal(7, initial.GetProperty("enabled").GetArrayLength());

        // Dependencies are refused before anything is stored.
        using (var codeAlone = await PutAsync(projectId, 0, "Code", "ProblemReports"))
            Assert.Equal(HttpStatusCode.BadRequest, codeAlone.StatusCode);
        using (var verificationAlone = await PutAsync(projectId, 0, "Verification", "ProblemReports"))
            Assert.Equal(HttpStatusCode.BadRequest, verificationAlone.StatusCode);
        using (var unknown = await PutAsync(projectId, 0, "Bogus"))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var noReason = await client.PutAsJsonAsync(Url(projectId), new { expectedVersion = 0, enabled = new[] { "ProblemReports" } }))
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // A feature holding records cannot be switched off.
        using (var withRecords = await PutAsync(otherProjectId, 0, "TeamWork", "Requirements", "Verification", "Code", "DocumentationCenter", "Release"))
        {
            Assert.Equal(HttpStatusCode.Conflict, withRecords.StatusCode);
            Assert.Contains("Problem Reports already holds records", await withRecords.Content.ReadAsStringAsync());
        }

        // The owner's target shape on an empty project: Team Work and Problem Reports only.
        using (var changed = await PutAsync(projectId, 0, "TeamWork", "ProblemReports"))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("version").GetInt64());
            Assert.Equal(["TeamWork", "ProblemReports"], body.GetProperty("enabled").EnumerateArray().Select(x => x.GetString()));
            var entry = Assert.Single(body.GetProperty("history").EnumerateArray());
            Assert.Equal("admin", entry.GetProperty("actor").GetString());
            Assert.Equal(64, entry.GetProperty("snapshotHash").GetString()!.Length);
        }
        using (var stale = await PutAsync(projectId, 0, "ProblemReports"))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The save boundary refuses new records for a feature that is off, whatever the path.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ManagedDocuments.Add(ManagedDocumentFixture(projectId));
            var refused = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("Documentation Center is not enabled for this project.", refused.Message);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ProblemReports.Add(new ProblemReport(projectId, "PR-00002", "Allowed", "Problem Reports stay on.", "", "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // Switching on is always allowed; Team Work refuses its projection once off.
        using (var teamWorkOff = await PutAsync(projectId, 1, "ProblemReports"))
            Assert.Equal(HttpStatusCode.OK, teamWorkOff.StatusCode);
        using (var projection = await client.GetAsync($"/api/team-work?projectId={projectId}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, projection.StatusCode);
            Assert.Contains("feature_disabled", await projection.Content.ReadAsStringAsync());
        }
        using (var allOn = await PutAsync(projectId, 2, "TeamWork", "Requirements", "Verification", "Code", "DocumentationCenter", "ProblemReports", "Release"))
            Assert.Equal(HttpStatusCode.OK, allOn.StatusCode);

        // Members read the features; only Configuration Manager, Program Manager or Administrator change them.
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        var asMember = await client.GetFromJsonAsync<JsonElement>(Url(projectId));
        Assert.False(asMember.GetProperty("canManage").GetBoolean());
        using (var forbidden = await PutAsync(projectId, 3, "ProblemReports"))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Without_Verification_a_fixed_report_reaches_independent_SQA_closure_on_an_attested_statement()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var engineer = $"pf.eng.{Guid.NewGuid():N}";
        var quality = $"pf.sqa.{Guid.NewGuid():N}";
        Guid attestedId, verifiedProjectReportId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Attestation program", "PFA");
            var reportsOnly = new ProjectRecord(program.Id, "Problem Reports only", "FMS");
            var everything = new ProjectRecord(program.Id, "Every feature", "FMS");
            db.AddRange(program, reportsOnly, everything,
                new ProjectFeatureSet(reportsOnly.Id, ProjectFeature.TeamWork | ProjectFeature.ProblemReports, "test.setup", now));
            foreach (var (name, role) in new[] { (engineer, ProgramRole.SoftwareEngineer), (quality, ProgramRole.SoftwareQualityAnalyst) })
            {
                var account = new UserAccount(name, name, $"{name}@example.test",
                    IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
                db.AddRange(account, new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            }
            var state = typeof(ProblemReport).GetProperty(nameof(ProblemReport.State))!;
            ProblemReport Verifying(Guid projectId, string number)
            {
                var report = new ProblemReport(projectId, number, "Display freezes", "The display freezes.", "", engineer, now);
                state.SetValue(report, ProblemReportState.Verifying);
                db.ProblemReports.Add(report);
                return report;
            }
            attestedId = Verifying(reportsOnly.Id, "PR-00001").Id;
            verifiedProjectReportId = Verifying(everything.Id, "PR-00002").Id;
            await db.SaveChangesAsync();
        }
        const string statement = "Re-ran the waypoint insert sequence 50 times on the bench build; no freeze observed.";
        Task<HttpResponseMessage> AttestAsync(HttpClient http, Guid id, string text) =>
            http.PostAsJsonAsync($"/api/problem-reports/{id}/attest-resolution", new { statement = text });

        using var engineerClient = factory.CreateClient();
        using (var login = await engineerClient.PostAsJsonAsync("/api/auth/login", new { userName = engineer, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(engineerClient);

        // Where Verification exists, a test result remains the only basis.
        using (var refused = await AttestAsync(engineerClient, verifiedProjectReportId, statement))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("pr_attestation_verification_enabled", await refused.Content.ReadAsStringAsync());
        }
        using (var tooShort = await AttestAsync(engineerClient, attestedId, "Looks fine."))
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        using (var sent = await AttestAsync(engineerClient, attestedId, statement))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal("WaitingForSqaToClose", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }
        var detail = await engineerClient.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{attestedId}");
        Assert.Equal(statement, detail.GetProperty("resolutionAttestation").GetString());

        // The responsible engineer still cannot close it; independent SQA can.
        using (var selfClose = await engineerClient.PostAsJsonAsync($"/api/problem-reports/{attestedId}/closure/approve", new { }))
            Assert.Equal(HttpStatusCode.Forbidden, selfClose.StatusCode);
        using var qualityClient = factory.CreateClient();
        using (var login = await qualityClient.PostAsJsonAsync("/api/auth/login", new { userName = quality, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(qualityClient);
        using (var closed = await qualityClient.PostAsJsonAsync($"/api/problem-reports/{attestedId}/closure/approve", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            Assert.Equal("Closed", (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var candidate = db.ProblemReportClosureCandidates.Single(x => x.ProblemReportId == attestedId);
            Assert.Equal(ProblemReportClosureCandidateState.Approved, candidate.State);
            Assert.Null(candidate.VerificationExecutionId);
            Assert.Contains(statement, candidate.VerificationEvidenceJson);
            Assert.Contains("aerolink.problem-report-resolution-attestation", candidate.ClosurePackageJson);
        }
    }

    [Fact]
    public async Task Without_Release_a_build_is_released_by_a_signed_decision_and_its_successor_can_start()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var manager = $"pf.cm.{Guid.NewGuid():N}";
        var engineer = $"pf.eng.{Guid.NewGuid():N}";
        Guid reportsOnlyId, buildId, everythingBuildId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Release program", "PFR");
            var reportsOnly = new ProjectRecord(program.Id, "Problem Reports only", "Widget");
            var everything = new ProjectRecord(program.Id, "Every feature", "Widget");
            var build = new SoftwareRelease(reportsOnly.Id, "1.0", false);
            var everythingBuild = new SoftwareRelease(everything.Id, "1.0", false);
            db.AddRange(program, reportsOnly, everything, build, everythingBuild,
                new ProjectFeatureSet(reportsOnly.Id, ProjectFeature.TeamWork | ProjectFeature.ProblemReports, "test.setup", now));
            foreach (var (name, role) in new[] { (manager, ProgramRole.ConfigurationManager), (engineer, ProgramRole.SoftwareEngineer) })
            {
                var account = new UserAccount(name, name, $"{name}@example.test",
                    IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
                db.AddRange(account, new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
                // Configuration Manager authority is the Project Leadership position, not the membership alone.
                if (role == ProgramRole.ConfigurationManager)
                    db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager, account.Id, "test.setup", now));
            }
            await db.SaveChangesAsync();
            reportsOnlyId = reportsOnly.Id; buildId = build.Id; everythingBuildId = everythingBuild.Id;
        }
        async Task<HttpClient> SignInAsync(string userName)
        {
            var http = factory.CreateClient();
            using var login = await http.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await SecurityBoundaryTests.AuthorizeMutationsAsync(http);
            return http;
        }
        const string reason = "Shipped to the customer as the first field release.";
        Task<HttpResponseMessage> ReleaseAsync(HttpClient http, Guid id, string text, string password) =>
            http.PostAsJsonAsync($"/api/releases/{id}/release-without-readiness", new { reason = text, password });

        using var engineerClient = await SignInAsync(engineer);
        using (var forbidden = await ReleaseAsync(engineerClient, buildId, reason, AeroLinkApiFactory.MemberPassword))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var managerClient = await SignInAsync(manager);
        using (var withRelease = await ReleaseAsync(managerClient, everythingBuildId, reason, AeroLinkApiFactory.MemberPassword))
        {
            Assert.Equal(HttpStatusCode.Conflict, withRelease.StatusCode);
            Assert.Contains("release_feature_enabled", await withRelease.Content.ReadAsStringAsync());
        }
        using (var wrongPassword = await ReleaseAsync(managerClient, buildId, reason, "not-the-password"))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        using (var noReason = await ReleaseAsync(managerClient, buildId, "ok", AeroLinkApiFactory.MemberPassword))
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        using (var released = await ReleaseAsync(managerClient, buildId, reason, AeroLinkApiFactory.MemberPassword))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
            Assert.True((await released.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releasedWithoutReadiness").GetBoolean());
        }
        using (var again = await ReleaseAsync(managerClient, buildId, reason, AeroLinkApiFactory.MemberPassword))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The released build can now have its successor.
        using (var successor = await managerClient.PostAsJsonAsync("/api/releases", new { projectId = reportsOnlyId, version = "1.1", predecessorReleaseId = buildId }))
            Assert.Equal(HttpStatusCode.Created, successor.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var signature = db.ElectronicSignatures.Single(x => x.ArtifactId == buildId);
            Assert.Equal("ReleaseWithoutReadiness", signature.Action);
            Assert.Equal(manager, signature.UserName);
            Assert.Equal(reason, signature.Rationale);
            Assert.True(db.Releases.Single(x => x.Id == buildId).ReleasedWithoutReadiness);
        }
    }

    private static AeroLink.Domain.Documents.ManagedDocument ManagedDocumentFixture(Guid projectId) =>
        new(projectId, "SYSRD-00001", "SYSRD", "System Requirements", "Gated document", "admin", DateTimeOffset.UtcNow);

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Administrator",
                email = "admin@example.test",
                password = AeroLinkApiFactory.AdministratorPassword,
            }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = "admin",
            password = AeroLinkApiFactory.AdministratorPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
