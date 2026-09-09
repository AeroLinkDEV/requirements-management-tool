using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

public sealed class ChangeRequestNumberOrderingTests
{
    [Fact]
    public Task Sqlite_orders_both_registers_numerically_before_paging() => AssertOrderingAsync(null);

    [OrderingPostgresFact]
    public Task Postgres_orders_both_registers_numerically_before_paging()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEROLINK_1006_CONNECTION"));
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Port == 54329
            || connection.Database != "aerolink_1006_ordering_test")
            throw new InvalidOperationException("Ordering qualification requires its named disposable loopback database away from port 54329.");
        return AssertOrderingAsync(connection.ConnectionString);
    }

    private static async Task AssertOrderingAsync(string? connection)
    {
        using var factory = new AeroLinkApiFactory(postgresConnection: connection);
        using var client = factory.CreateClient();
        Guid projectId, releaseId;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var author = "ordering." + suffix;
        int[] numbers = [100001, 99998, 100000, 99999, 100002];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Ordering " + suffix, "ORD" + suffix);
            var project = new ProjectRecord(program.Id, "Ordering", "Ordering fixture");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var account = new UserAccount(author, author, author + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, account, new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            projectId = project.Id; releaseId = release.Id;
            for (var index = 0; index < numbers.Length; index++)
            {
                // Time order deliberately differs from numeric order, including digit-width growth.
                var at = now.AddMinutes(index);
                var cr = new SystemChangeRequest($"SRCR-{numbers[index]}", 0, project.Id, release.Id,
                    "Ordering fixture", "P", "A", "S", author, at);
                var tcr = new TestChangeReview(project.Id, release.Id, cr.Id, TestChangeReviewDiscipline.System,
                    cr.DisplayNumber, at, baseNumber: $"SYSTPCR-{numbers[index]}", authorId: author);
                tcr.RecordTestChangeRequired(author, at);
                db.AddRange(cr, tcr);
            }
            await db.SaveChangesAsync();
        }
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = author, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        foreach (var (path, prefix) in new[] { ("change-requests", "SRCR"), ("test-change-requests", "SYSTPCR") })
        {
            var expected = numbers.Order().Select(number => $"{prefix}-{number}.00").ToArray();
            var actual = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                using var response = await client.GetAsync($"/api/history/{path}?projectId={projectId}&releaseId={releaseId}&page={page}&pageSize=2");
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(numbers.Length, result.GetProperty("totalCount").GetInt32());
                Assert.Equal(3, result.GetProperty("totalPages").GetInt32());
                actual.AddRange(result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayNumber").GetString()!));
            }
            Assert.Equal(expected, actual);
        }
    }

    private sealed class OrderingPostgresFactAttribute : FactAttribute
    {
        public OrderingPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_1006_CONNECTION")))
                Skip = "Set AEROLINK_1006_CONNECTION to the dedicated disposable ordering database.";
        }
    }
}
