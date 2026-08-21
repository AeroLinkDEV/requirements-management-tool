using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Direct IdentifierAllocator rules belong at the persistence boundary, not behind WebApplicationFactory.
/// HTTP route/status/auth coverage remains in AeroLink.Api.Tests; these cases deliberately exercise only the
/// allocator and its disposable SQLite persistence contract.
/// </summary>
public sealed class IdentifierAllocationPersistenceTests
{
    [Fact]
    public Task Two_allocations_before_either_record_is_saved_do_not_collide() => WithDatabaseAsync(async db =>
    {
        var first = await IdentifierAllocator.NextProblemReportAsync(db, default);
        var second = await IdentifierAllocator.NextProblemReportAsync(db, default);
        var third = await IdentifierAllocator.NextProblemReportAsync(db, default);

        Assert.Equal("PR-00001", first);
        Assert.Equal("PR-00002", second);
        Assert.Equal("PR-00003", third);
    });

    [Fact]
    public Task Each_prefix_numbers_independently_and_continuously_across_projects() => WithDatabaseAsync(async db =>
    {
        Assert.Equal("SYSR-000001", await IdentifierAllocator.NextRequirementAsync(db, "SYSR", default));
        Assert.Equal("HLR-000001", await IdentifierAllocator.NextRequirementAsync(db, "HLR", default));
        Assert.Equal("LLR-000001", await IdentifierAllocator.NextRequirementAsync(db, "LLR", default));
        Assert.Equal("SYSR-000002", await IdentifierAllocator.NextRequirementAsync(db, "SYSR", default));
        Assert.Equal("SRCR-00001", await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.System, null, default));
        Assert.Equal("HLRCR-00001", await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.Software, RequirementLevel.HighLevel, default));
        Assert.Equal("LLRCR-00001", await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.Software, RequirementLevel.LowLevel, default));
        Assert.Equal("SRCR-00002", await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.System, null, default));
        Assert.Equal("SYSTP-000001", await IdentifierAllocator.NextTestProcedureAsync(db, TestProcedureLevel.System, default));
        Assert.Equal("HLRTP-000001", await IdentifierAllocator.NextTestProcedureAsync(db, TestProcedureLevel.HighLevel, default));
        Assert.Equal("LLRTP-000001", await IdentifierAllocator.NextTestProcedureAsync(db, TestProcedureLevel.LowLevel, default));
        Assert.Equal("SYSTCR-000001", await IdentifierAllocator.NextTestChangeRequestAsync(db, TestChangeReviewDiscipline.System, default));
        Assert.Equal("HLRTCR-000001", await IdentifierAllocator.NextTestChangeRequestAsync(db, TestChangeReviewDiscipline.HighLevelSoftware, default));
        Assert.Equal("LLRTCR-000001", await IdentifierAllocator.NextTestChangeRequestAsync(db, TestChangeReviewDiscipline.LowLevelSoftware, default));

        var sequences = await db.IdentifierSequences.AsNoTracking().OrderBy(x => x.Scope).ToListAsync();
        Assert.Equal(new[] { "HLR", "HLRCR", "HLRTCR", "HLRTP", "LLR", "LLRCR", "LLRTCR", "LLRTP", "SRCR", "SYSR", "SYSTCR", "SYSTP" }, sequences.Select(x => x.Scope));
    });

    [Fact]
    public Task A_number_handed_out_is_not_returned_to_the_pool_when_its_record_is_never_written() => WithDatabaseAsync(async db =>
    {
        Assert.Equal("PR-00001", await IdentifierAllocator.NextProblemReportAsync(db, default));
        Assert.Equal("PR-00002", await IdentifierAllocator.NextProblemReportAsync(db, default));
        Assert.Equal(3, await db.IdentifierSequences.AsNoTracking().Where(x => x.Scope == "PR").Select(x => x.NextValue).SingleAsync());
    });

    [Fact]
    public Task Attachment_versions_are_claimed_per_logical_file_and_never_repeat() => WithDatabaseAsync(async db =>
    {
        var logicalId = Guid.NewGuid();
        var other = Guid.NewGuid();
        Task<int> Claim(Guid id) => IdentifierAllocator.ClaimAsync(db, "ATTACHMENT-" + id.ToString("N"),
            async () => (await db.ControlledAttachments.AsNoTracking().Where(x => x.LogicalId == id).Select(x => x.Version).ToListAsync()).DefaultIfEmpty(0).Max() + 1,
            default);

        Assert.Equal(1, await Claim(logicalId));
        Assert.Equal(2, await Claim(logicalId));
        Assert.Equal(1, await Claim(other));
        Assert.Equal(3, await Claim(logicalId));
    });

    private static async Task WithDatabaseAsync(Func<AeroLinkDbContext, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-identifier-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await test(db);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
