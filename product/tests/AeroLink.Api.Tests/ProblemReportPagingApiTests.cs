using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportPagingApiTests
{
    private const string Member = "paging.member";
    private const string Outsider = "paging.outsider";

    [Fact]
    public async Task Project_queue_is_bounded_complete_filtered_and_stable_across_pages()
    {
        var commands = new ProblemReportPagingCommandInterceptor();
        using var factory = new AeroLinkApiFactory(commandInterceptor: commands);
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory);
        await SignInAsync(client, Member);

        commands.Clear();
        var first = await PageAsync(client, scenario.ProjectId, "&page=1&pageSize=10");
        Assert.Equal(205, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(21, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(10, Numbers(first).Length);

        var problemReportSql = commands.Commands
            .Where(command => command.Contains("problem_reports", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Contains(problemReportSql, command => command.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        var boundedPage = Assert.Single(problemReportSql, command =>
            command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ORDER BY", boundedPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(problemReportSql, command => !command.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        var walked = new List<string>();
        for (var page = 1; page <= 13; page++)
            walked.AddRange(Numbers(await PageAsync(client, scenario.ProjectId, $"&page={page}&pageSize=17")));
        Assert.Equal(205, walked.Count);
        Assert.Equal(205, walked.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 204).Select(index => $"PR-{index:D5}").Append("PR-100000"), walked);

        Assert.Equal(Numbers(first), Numbers(await PageAsync(client, scenario.ProjectId, "&page=1&pageSize=10")));
        var beyond = await PageAsync(client, scenario.ProjectId, "&page=999&pageSize=10");
        Assert.Equal(21, beyond.GetProperty("page").GetInt32());
        Assert.Equal(5, Numbers(beyond).Length);

        var search = await PageAsync(client, scenario.ProjectId, "&search=scale&page=2&pageSize=50");
        Assert.Equal(205, search.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, search.GetProperty("totalPages").GetInt32());
        Assert.Equal(50, Numbers(search).Length);

        var composedQuery = $"&targetReleaseId={scenario.FirstReleaseId}&state=Open&severity=Critical" +
                            "&priority=High&owner=paging.owner&search=scale&page=1&pageSize=200";
        var composed = await PageAsync(client, scenario.ProjectId, composedQuery);
        var expected = scenario.Expected.Where(item => item.ReleaseId == scenario.FirstReleaseId
            && item.State == ProblemReportState.Open && item.Severity == ProblemReportSeverity.Critical
            && item.Priority == ProblemReportPriority.High && item.Owner.Contains("paging.owner")
            && item.Title.Contains("scale", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(expected.Length, composed.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Select(item => item.Number), Numbers(composed));

        using var outsider = factory.CreateClient();
        await SignInAsync(outsider, Outsider);
        using var forbidden = await outsider.GetAsync(
            $"/api/problem-reports?projectId={scenario.ProjectId}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Problem Report paging", "PRPG");
        var project = new ProjectRecord(program.Id, "Scaled queue", "FMS");
        var firstRelease = new SoftwareRelease(project.Id, "1.0", false);
        var secondRelease = new SoftwareRelease(project.Id, "2.0", false);
        var otherProgram = new ProgramRecord("Other paging Program", "OPPG");
        var otherProject = new ProjectRecord(otherProgram.Id, "Other queue", "Other");
        var member = new UserAccount(Member, "Paging Member", "paging.member@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount(Outsider, "Paging Outsider", "paging.outsider@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, firstRelease, secondRelease, otherProgram, otherProject, member, outsider,
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var stateProperty = typeof(ProblemReport).GetProperty(nameof(ProblemReport.State),
            BindingFlags.Instance | BindingFlags.Public)!;
        var expected = new List<ExpectedReport>();
        for (var index = 1; index <= 205; index++)
        {
            var sequence = index == 205 ? 100000 : index;
            var release = index % 2 == 0 ? firstRelease : secondRelease;
            var state = index % 3 == 0 ? ProblemReportState.Open : ProblemReportState.Draft;
            var severity = index % 2 == 0 ? ProblemReportSeverity.Critical : ProblemReportSeverity.Major;
            var priority = index % 5 == 0 ? ProblemReportPriority.High : ProblemReportPriority.Normal;
            var owner = index % 4 == 0 ? "paging.owner" : Member;
            var report = new ProblemReport(project.Id, $"PR-{sequence:D5}", $"Scale queue report {index:D3}",
                "Scale paging regression population.", "", Member, now.AddMinutes(index),
                severity: severity, priority: priority,
                targetReleaseId: release.Id, responsibleEngineerId: owner);
            stateProperty.SetValue(report, state);
            db.Add(report);
            db.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", release.Id,
                ProblemReportRelationshipPolicy.BuildScope,
                ProblemReportRelationshipProducer.TargetBuildWorkflow, Member, now));
            expected.Add(new ExpectedReport(report.ReportNumber, release.Id, state, severity, priority, owner,
                report.Title));
        }
        for (var index = 1; index <= 3; index++)
            db.Add(new ProblemReport(otherProject.Id, $"PR-{index:D5}", $"Other queue {index}",
                "Must never enter the authorized Project total.", "", Outsider, now));
        await db.SaveChangesAsync();
        return new Scenario(project.Id, firstRelease.Id, expected);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, Guid projectId, string query)
    {
        using var response = await client.GetAsync($"/api/problem-reports?projectId={projectId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Numbers(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("reportNumber").GetString()!)];

    private sealed record ExpectedReport(string Number, Guid ReleaseId, ProblemReportState State,
        ProblemReportSeverity Severity, ProblemReportPriority Priority, string Owner, string Title);
    private sealed record Scenario(Guid ProjectId, Guid FirstReleaseId, IReadOnlyList<ExpectedReport> Expected);
}

internal sealed class ProblemReportPagingCommandInterceptor : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();
    public void Clear() { while (Commands.TryDequeue(out _)) { } }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
