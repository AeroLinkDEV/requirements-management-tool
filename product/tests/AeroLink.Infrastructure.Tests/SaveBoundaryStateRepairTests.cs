using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class SaveBoundaryStateRepairTests
{
    [Fact]
    public async Task Added_version_families_keep_their_long_property_type_and_start_at_one()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var db = new AeroLinkDbContext(options);

        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var systemChangeRequest = new SystemChangeRequest(
            "SRCR-90001", 0, projectId, releaseId, "Title", "Purpose", "Analysis", "Solution", "author", now);
        var specification = new RequirementSpecification(
            projectId, "SPEC-90001", "Specification", "System", "Description", "author", now);
        var procedure = new TestProcedure(
            projectId, "SYSTP-90001", "Procedure", "author", now, TestProcedureLevel.System);
        var trace = new RequirementTraceLink(
            projectId, Guid.NewGuid(), Guid.NewGuid(), RequirementTraceType.AllocatedFrom, "Rationale", now);
        var baseline = new CandidateBaseline(
            "SW-90.01", 0, projectId, releaseId, null, "Baseline", "author", now);
        db.AddRange(systemChangeRequest, specification, procedure, trace, baseline);

        await new SaveBoundaryStateRepair(db).ApplyAsync(CancellationToken.None);

        AssertVersion(db.Entry(systemChangeRequest));
        AssertVersion(db.Entry(specification));
        AssertVersion(db.Entry(procedure));
        AssertVersion(db.Entry(trace));
        AssertVersion(db.Entry(baseline));
    }

    private static void AssertVersion(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var property = entry.Property("Version");
        Assert.Equal(1L, property.CurrentValue);
        Assert.Equal(typeof(long), property.Metadata.ClrType);
    }
}
