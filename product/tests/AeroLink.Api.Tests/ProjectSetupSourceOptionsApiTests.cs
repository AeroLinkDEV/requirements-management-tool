using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProjectSetupSourceOptionsApiTests
{
    [Fact]
    public async Task Native_options_page_reports_authorized_total_and_keeps_later_pages_reachable()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(administrator);
        Guid membershipId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var employee = new UserAccount("source.pager", "Source Pager", "source.pager@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var permitted = new ProgramRecord("Permitted source scope", "PERMIT");
            var hidden = new ProgramRecord("Hidden source scope", "HIDDEN");
            var membership = new ProgramMembership(employee.Id, permitted.Id, ProgramRole.Engineer, "admin", now);
            membershipId = membership.Id;
            db.AddRange(employee, permitted, hidden, membership);
            foreach (var (program, count) in new[] { (permitted, 51), (hidden, 2) })
            {
                var project = new ProjectRecord(program.Id, program.Name, "Paging fixture");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                db.AddRange(project, release);
                for (var index = 0; index < count; index++)
                {
                    var baseline = new CandidateBaseline($"SW-10.{index:D2}", 0, project.Id, release.Id, null,
                        $"Source {index}", "source.owner", now.AddMinutes(index));
                    baseline.FreezeForInception("source.owner", now);
                    baseline.MarkRequirementsMaterialized("source.owner", new string('a', 64), 0, now);
                    db.Add(baseline);
                }
            }
            await db.SaveChangesAsync();
        }
        using var employeeClient = factory.CreateClient();
        using var login = await employeeClient.PostAsJsonAsync("/api/auth/login", new
        { userName = "source.pager", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var allIds = new HashSet<Guid>();
        foreach (var offset in new[] { 0, 50 })
        {
            using var response = await employeeClient.GetAsync($"/api/project-setups/source-options?offset={offset}&limit=50");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(51, page.GetProperty("total").GetInt32());
            Assert.Equal(offset, page.GetProperty("offset").GetInt32());
            Assert.Equal(50, page.GetProperty("limit").GetInt32());
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(offset == 0 ? 50 : 1, items.Length);
            foreach (var item in items)
            {
                Assert.Equal("Permitted source scope", item.GetProperty("projectName").GetString());
                Assert.True(allIds.Add(item.GetProperty("baselineId").GetGuid()));
            }
        }
        Assert.Equal(51, allIds.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await db.ProgramMemberships.SingleAsync(x => x.Id == membershipId)).End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using var revoked = await employeeClient.GetAsync("/api/project-setups/source-options?offset=0&limit=50");
        var empty = await revoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        using var invalid = await employeeClient.GetAsync("/api/project-setups/source-options?offset=-1&limit=50");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
