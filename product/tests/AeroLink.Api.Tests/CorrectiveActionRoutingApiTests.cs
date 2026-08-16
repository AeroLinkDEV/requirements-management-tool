using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// "Record a passing successor execution" routed to the generic System Verification workspace carrying
/// nothing, so a software author landed in the wrong discipline, on a tab about change impact, with no
/// report, procedure or result selected. The primary remediation call to action could not guide anybody to
/// the evidence it was asking for.
///
/// The destination is resolved from the report's own links rather than guessed at by the browser: a report
/// raised from a failure names the execution that raised it, and that execution names the exact procedure
/// revision, whose level decides the discipline.
/// </summary>
public sealed class CorrectiveActionRoutingApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public CorrectiveActionRoutingApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Fixture(Guid ProjectId, Guid SystemReportId, Guid SoftwareReportId, Guid UnlinkedReportId,
        string SystemProcedureNumber, string SoftwareProcedureNumber, string MemberName, string OutsiderName);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var memberName = $"corrective.engineer.{tag}";
        var outsiderName = $"corrective.outsider.{tag}";

        var program = new ProgramRecord($"Corrective Program {tag}", $"CRP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Corrective Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var baseline = new CandidateBaseline("SW-09.01", 0, project.Id, release.Id, null,
            "Corrective baseline", "cm", now);
        db.AddRange(program, project, release, baseline);

        var reports = new List<ProblemReport>();
        Guid Raise(TestProcedureLevel level, string number, string procedureNumber)
        {
            var procedure = new TestProcedure(project.Id, procedureNumber, $"{level} behaviour", "test.author", now, level);
            // Approved as materialisation writes it, on the authority of the package that carried the change.
            var revision = new TestProcedureRevision(procedure.Id, 1, "Objective", "Preconditions", "Steps", "Expected",
                TestProcedureState.Approved, "test.author", now, effectiveBaselineId: baseline.Id);
            var execution = new TestExecution(project.Id, revision.Id, null, null, TestOutcome.Fail, "test.engineer",
                "Rig", "Observed output did not satisfy the expected result.", "evidence/fail.json", now, now);
            var report = new ProblemReport(project.Id, number, $"{level} failure", "Problem", "Analysis", "reporter", now,
                "Verification failure", ProblemReportSeverity.Major, ProblemReportPriority.High, "Test execution", "Config",
                targetReleaseId: release.Id);
            db.AddRange(procedure, revision, execution, report);
            db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "TestExecution", execution.Id, "OriginatingFailure", "reporter", now));
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, procedure.Id, revision.Id));
            reports.Add(report);
            return report.Id;
        }

        var systemReport = Raise(TestProcedureLevel.System, "PR-00001", "SYSTP-00000901");
        var softwareReport = Raise(TestProcedureLevel.LowLevel, "PR-00002", "LLRTP-00000901");

        // Raised by hand, linked to nothing: the scope genuinely cannot be determined and must say so.
        var unlinked = new ProblemReport(project.Id, "PR-00003", "Manual report", "Problem", "Analysis", "reporter", now,
            "Engineering anomaly", ProblemReportSeverity.Minor, ProblemReportPriority.Normal, "Manual report", "");
        db.Add(unlinked);

        var account = new UserAccount(memberName, memberName, $"{memberName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.RequirementsMaterializedAt, now)
            .SetProperty(x => x.TestProceduresMaterializedAt, now)
            .SetProperty(x => x.TestProceduresHash, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

        return new Fixture(project.Id, systemReport, softwareReport, unlinked.Id, "SYSTP-00000901", "LLRTP-00000901",
            memberName, outsiderName);
    }

    private static async Task<JsonElement> TargetAsync(HttpClient client, Guid reportId)
    {
        using var response = await client.GetAsync($"/api/problem-reports/{reportId}/corrective-action");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task The_corrective_action_resolves_its_discipline_procedure_and_report_from_the_record()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);
        using (var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = fixture.MemberName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var system = await TargetAsync(client, fixture.SystemReportId);
        Assert.True(system.GetProperty("available").GetBoolean());
        Assert.Equal("system", system.GetProperty("discipline").GetString());
        Assert.Equal(fixture.SystemProcedureNumber, system.GetProperty("procedureNumber").GetString());
        Assert.Equal("PR-00001.00", system.GetProperty("problemReportNumber").GetString());
        Assert.Equal("TestEngineer", system.GetProperty("requiredRole").GetString());

        using var detailResponse = await client.GetAsync($"/api/problem-reports/{fixture.SystemReportId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(fixture.SystemProcedureNumber + ".01",
            detail.GetProperty("links")[0].GetProperty("identifier").GetString());

        // The one the old behaviour got wrong: a software failure sent to System Verification.
        var software = await TargetAsync(client, fixture.SoftwareReportId);
        Assert.True(software.GetProperty("available").GetBoolean());
        Assert.Equal("software", software.GetProperty("discipline").GetString());
        Assert.Equal(fixture.SoftwareProcedureNumber, software.GetProperty("procedureNumber").GetString());

        // Neither report may be answered with the other's procedure.
        Assert.NotEqual(system.GetProperty("procedureId").GetGuid(), software.GetProperty("procedureId").GetGuid());
    }

    [Fact]
    public async Task A_report_with_nothing_linked_says_the_scope_cannot_be_determined_rather_than_guessing()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);
        using (var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = fixture.MemberName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var unresolved = await TargetAsync(client, fixture.UnlinkedReportId);
        Assert.False(unresolved.GetProperty("available").GetBoolean());
        Assert.Null(unresolved.GetProperty("discipline").GetString());
        Assert.Contains("cannot be determined", unresolved.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_target_is_not_readable_without_access_to_the_project()
    {
        using var client = _host.CreateClient();
        var fixture = await SeedAsync(_host.Factory);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var outsider = new UserAccount(fixture.OutsiderName, fixture.OutsiderName, $"{fixture.OutsiderName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.Add(outsider);
        await db.SaveChangesAsync();

        using (var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = fixture.OutsiderName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync($"/api/problem-reports/{fixture.SystemReportId}/corrective-action");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
