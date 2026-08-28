using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportWaiverApiTests
{
    [Fact]
    public async Task Release_blocker_waiver_is_independent_server_attributed_revocable_and_bound_to_one_blocker_context()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(administrator);
        var fixture = await SeedAsync(factory);

        using var owner = factory.CreateClient(); await LoginAsync(owner, "waiver.owner");
        using var reporter = factory.CreateClient(); await LoginAsync(reporter, "waiver.reporter");
        using var outsider = factory.CreateClient(); await LoginAsync(outsider, "waiver.engineer");
        using var testEngineer = factory.CreateClient(); await LoginAsync(testEngineer, "waiver.test");
        using var approver = factory.CreateClient(); await LoginAsync(approver, "waiver.approver");
        using var quality = factory.CreateClient(); await LoginAsync(quality, "waiver.quality");

        var legacyDashboard = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/dashboard?projectId={fixture.ProjectId}");
        Assert.Equal(1, legacyDashboard.GetProperty("summary").GetProperty("releaseBlockers").GetInt32());
        Assert.Equal(0, legacyDashboard.GetProperty("summary").GetProperty("waivedBlockers").GetInt32());
        var blockersOnly = await quality.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports?projectId={fixture.ProjectId}&blockersOnly=true");
        Assert.Equal(1, blockersOnly.GetProperty("totalCount").GetInt32());

        using (var forgedInline = await owner.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/blocker",
            new { expectedVersion = fixture.Version, isReleaseBlocker = true, waiverRationale = "Self approve" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forgedInline.StatusCode);
            Assert.Equal("pr_waiver_separate_approval_required",
                (await forgedInline.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        await RejectIndependenceAsync(owner, fixture.ReportId, fixture.Version);
        using (var baseRoleReporter = await reporter.PostAsJsonAsync(
                   $"/api/problem-reports/{fixture.ReportId}/release-waiver", WaiverBody(fixture.Version)))
            Assert.Equal(HttpStatusCode.Forbidden, baseRoleReporter.StatusCode);
        using (var ordinary = await outsider.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            WaiverBody(fixture.Version))) Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
        using (var testOnly = await testEngineer.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            WaiverBody(fixture.Version))) Assert.Equal(HttpStatusCode.Forbidden, testOnly.StatusCode);
        using (var genericApprover = await approver.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            WaiverBody(fixture.Version))) Assert.Equal(HttpStatusCode.Forbidden, genericApprover.StatusCode);
        using (var admin = await administrator.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            WaiverBody(fixture.Version))) Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        using (var stale = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            WaiverBody(fixture.Version - 1))) Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var approved = await quality.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/release-waiver",
            new { expectedVersion = fixture.Version, rationale = "Bounded operational limitation accepted for this release.",
                approvedBy = "forged.release.authority", expiresAt = DateTimeOffset.UtcNow.AddDays(7) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedBody = await approved.Content.ReadFromJsonAsync<JsonElement>();
        var approvedVersion = approvedBody.GetProperty("version").GetInt64();

        var detail = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.True(detail.GetProperty("waived").GetBoolean());
        var active = detail.GetProperty("activeReleaseWaiver");
        Assert.Equal("waiver.quality", active.GetProperty("approvedBy").GetString());
        Assert.NotEqual("forged.release.authority", active.GetProperty("approvedBy").GetString());
        Assert.Equal("SoftwareQualityAnalyst", active.GetProperty("approvalAuthority").GetString());
        Assert.Equal("IndependentProblemReportReleaseWaiver", active.GetProperty("signatureMeaning").GetString());
        Assert.Equal(detail.GetProperty("releaseBlockerVersion").GetInt64(), active.GetProperty("blockerVersion").GetInt64());
        var waiverId = active.GetProperty("id").GetGuid();
        var dashboard = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/dashboard?projectId={fixture.ProjectId}");
        Assert.Equal(0, dashboard.GetProperty("summary").GetProperty("releaseBlockers").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("summary").GetProperty("waivedBlockers").GetInt32());

        using var revoked = await quality.PostAsJsonAsync(
            $"/api/problem-reports/{fixture.ReportId}/release-waiver/{waiverId}/revoke",
            new { expectedVersion = approvedVersion, reason = "The approved operating interval ended early." });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var revokedVersion = (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        detail = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.False(detail.GetProperty("waived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("activeReleaseWaiver").ValueKind);
        var historical = Assert.Single(detail.GetProperty("releaseWaivers").EnumerateArray());
        Assert.False(historical.GetProperty("active").GetBoolean());
        Assert.Equal("waiver.quality", historical.GetProperty("revokedBy").GetString());

        using var cleared = await owner.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/blocker",
            new { expectedVersion = revokedVersion, isReleaseBlocker = false, waiverRationale = "" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedVersion = (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var reraised = await owner.PostAsJsonAsync($"/api/problem-reports/{fixture.ReportId}/blocker",
            new { expectedVersion = clearedVersion, isReleaseBlocker = true, waiverRationale = "" });
        Assert.Equal(HttpStatusCode.OK, reraised.StatusCode);
        detail = await quality.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.False(detail.GetProperty("waived").GetBoolean());
        Assert.NotEqual(active.GetProperty("blockerVersion").GetInt64(), detail.GetProperty("releaseBlockerVersion").GetInt64());
        Assert.Single(detail.GetProperty("releaseWaivers").EnumerateArray());
    }

    [Fact]
    public async Task Release_waiver_configuration_authority_requires_the_position_and_accepts_its_standing_backup()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedPositionAuthorityAsync(factory);
        using var baseClient = factory.CreateClient(); await LoginAsync(baseClient, "waiver.cm.base");
        using var primaryClient = factory.CreateClient(); await LoginAsync(primaryClient, "waiver.cm.primary");
        using var backupClient = factory.CreateClient(); await LoginAsync(backupClient, "waiver.cm.backup");

        using (var refused = await baseClient.PostAsJsonAsync(
                   $"/api/problem-reports/{fixture.ReportId}/release-waiver", WaiverBody(fixture.Version)))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var approved = await primaryClient.PostAsJsonAsync(
            $"/api/problem-reports/{fixture.ReportId}/release-waiver", WaiverBody(fixture.Version));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedVersion = (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        var detail = await primaryClient.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        var waiverId = detail.GetProperty("activeReleaseWaiver").GetProperty("id").GetGuid();
        Assert.Equal("ConfigurationManager",
            detail.GetProperty("activeReleaseWaiver").GetProperty("approvalAuthority").GetString());

        using var revoked = await primaryClient.PostAsJsonAsync(
            $"/api/problem-reports/{fixture.ReportId}/release-waiver/{waiverId}/revoke",
            new { expectedVersion = approvedVersion, reason = "Transfer controlled cover to the standing backup." });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var revokedVersion = (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var backupApproved = await backupClient.PostAsJsonAsync(
            $"/api/problem-reports/{fixture.ReportId}/release-waiver", WaiverBody(revokedVersion));
        Assert.Equal(HttpStatusCode.OK, backupApproved.StatusCode);
        detail = await backupClient.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{fixture.ReportId}");
        Assert.Equal("waiver.cm.backup",
            detail.GetProperty("activeReleaseWaiver").GetProperty("approvedBy").GetString());
        Assert.Equal("ConfigurationManager",
            detail.GetProperty("activeReleaseWaiver").GetProperty("approvalAuthority").GetString());
    }

    private static object WaiverBody(long version) => new
    {
        expectedVersion = version,
        rationale = "Temporary release authority decision.",
        expiresAt = DateTimeOffset.UtcNow.AddDays(7),
    };

    private static async Task RejectIndependenceAsync(HttpClient client, Guid reportId, long version)
    {
        using var response = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/release-waiver", WaiverBody(version));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("pr_waiver_independence_required",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<(Guid ReportId, Guid ProjectId, long Version)> SeedAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var program = new ProgramRecord("PR waiver authority", $"PWA{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "FMS", "Waiver-controlled FMS");
        var reporter = Account("waiver.reporter", now); var owner = Account("waiver.owner", now);
        var engineer = Account("waiver.engineer", now); var testEngineer = Account("waiver.test", now);
        var approver = Account("waiver.approver", now); var quality = Account("waiver.quality", now);
        var report = new ProblemReport(project.Id, "PR-09000", "Release-impacting anomaly",
            "An unresolved anomaly blocks the release.", "", reporter.UserName, now,
            responsibleEngineerId: owner.UserName);
        report.SetReleaseBlocker(owner.UserName, true, now.AddMinutes(1));
        db.AddRange(program, project, reporter, owner, engineer, testEngineer, approver, quality,
            new ProgramMembership(reporter.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(owner.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", now),
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(testEngineer.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "test.setup", now),
            new ProgramMembership(quality.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", now),
            report);
        await db.SaveChangesAsync();
        await db.ProblemReports.Where(item => item.Id == report.Id).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.WaiverRationale, "Legacy owner-entered waiver text")
            .SetProperty(item => item.WaivedBy, owner.UserName)
            .SetProperty(item => item.WaivedAt, now.AddMinutes(2)));
        return (report.Id, project.Id, report.Version);
    }

    private static async Task<(Guid ReportId, long Version)> SeedPositionAuthorityAsync(AeroLinkApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var program = new ProgramRecord("PR waiver position authority", $"PWP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "FMS", "Position-controlled waiver FMS");
        var reporter = Account("waiver.position.reporter", now);
        var owner = Account("waiver.position.owner", now);
        var baseOnly = Account("waiver.cm.base", now);
        var primary = Account("waiver.cm.primary", now);
        var backup = Account("waiver.cm.backup", now);
        var report = new ProblemReport(project.Id, "PR-09001", "Position-controlled release anomaly",
            "An unresolved anomaly blocks the release.", "", reporter.UserName, now,
            responsibleEngineerId: owner.UserName);
        report.SetReleaseBlocker(owner.UserName, true, now.AddMinutes(1));
        db.AddRange(program, project, reporter, owner, baseOnly, primary, backup,
            new ProgramMembership(reporter.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(owner.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(baseOnly.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(primary.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProgramMembership(backup.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
            new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                primary.Id, "test.setup", now),
            new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                backup.Id, "test.setup", now),
            report);
        await db.SaveChangesAsync();
        return (report.Id, report.Version);
    }

    private static UserAccount Account(string name, DateTimeOffset now) => new(name, name, $"{name}@example.test",
        IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
