using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReleaseExecutionTraceCarryForwardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reconciliation_carries_exact_predecessor_links_once_and_skips_non_member_endpoints()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-release-traces-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            Guid campaignId, currentHighRevisionId, currentSystemRevisionId;
            await using (var setup = new AeroLinkDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("Trace Carry Program", "TCP");
                var project = new ProjectRecord(program.Id, "Trace Carry Software", "Trace Carry Software");
                var release = new SoftwareRelease(project.Id, "1.6", false);
                var predecessor = new CandidateBaseline("BL-00000001", 0, project.Id, release.Id, null,
                    "Predecessor", "trace.setup", Now);
                var current = new CandidateBaseline("BL-00000002", 0, project.Id, release.Id, predecessor.Id,
                    "Current", "trace.setup", Now);
                var campaign = new ReleaseCampaign(project.Id, release.Id, current.Id, "Trace carry", "trace.setup", Now);
                var sourceRequest = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
                    "Trace source", "P", "A", "S", "trace.setup", Now);
                var system = new RequirementArtifact(project.Id, "SYSR-000001", RequirementLevel.System, Now);
                var high = new RequirementArtifact(project.Id, "HLR-000002", RequirementLevel.HighLevel, Now);
                var skipped = new RequirementArtifact(project.Id, "LLR-000003", RequirementLevel.LowLevel, Now);
                var nonMember = new RequirementArtifact(project.Id, "SYSR-000004", RequirementLevel.System, Now);
                var predecessorSystem = new RequirementRevision(system.Id, 0, "The system behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, predecessor.Id, Now);
                var predecessorHigh = new RequirementRevision(high.Id, 0, "The high-level behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, predecessor.Id, Now);
                var predecessorSkipped = new RequirementRevision(skipped.Id, 0, "The skipped behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, predecessor.Id, Now);
                var predecessorNonMember = new RequirementRevision(nonMember.Id, 0, "The non-member behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, predecessor.Id, Now);
                var currentSystem = new RequirementRevision(system.Id, 1, "The current system behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, current.Id, Now);
                var currentHigh = new RequirementRevision(high.Id, 1, "The current high-level behavior is controlled.", "R", "Test",
                    RequirementRevisionState.Active, sourceRequest.Id, current.Id, Now);

                setup.AddRange(program, project, release, predecessor, current, campaign, sourceRequest,
                    system, high, skipped, nonMember,
                    predecessorSystem, predecessorHigh, predecessorSkipped, predecessorNonMember,
                    currentSystem, currentHigh);
                setup.BaselineRequirements.AddRange(
                    new BaselineRequirementSelection(predecessor.Id, system.Id, predecessorSystem.Id),
                    new BaselineRequirementSelection(predecessor.Id, high.Id, predecessorHigh.Id),
                    new BaselineRequirementSelection(predecessor.Id, skipped.Id, predecessorSkipped.Id),
                    new BaselineRequirementSelection(current.Id, system.Id, currentSystem.Id),
                    new BaselineRequirementSelection(current.Id, high.Id, currentHigh.Id));
                setup.RequirementTraces.AddRange(
                    new RequirementTraceLink(project.Id, predecessorHigh.Id, predecessorSystem.Id,
                        RequirementTraceType.DerivedFrom, "The HLR derives from the System requirement.", Now),
                    new RequirementTraceLink(project.Id, predecessorHigh.Id, predecessorSkipped.Id,
                        RequirementTraceType.AllocatedFrom, "The omitted successor member is not carried.", Now),
                    new RequirementTraceLink(project.Id, predecessorHigh.Id, predecessorNonMember.Id,
                        RequirementTraceType.DerivedFrom, "An endpoint outside predecessor membership is ignored.", Now));
                await setup.SaveChangesAsync();
                await setup.CandidateBaselines.Where(x => x.Id == predecessor.Id || x.Id == current.Id)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                        .SetProperty(x => x.RequirementsMaterializedAt, Now));
                campaignId = campaign.Id;
                currentHighRevisionId = currentHigh.Id;
                currentSystemRevisionId = currentSystem.Id;
            }

            var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-release-traces-evidence-{Guid.NewGuid():N}");
            try
            {
                await using (var reconcile = new AeroLinkDbContext(options))
                {
                    var result = await new ReleaseExecutionService(reconcile, new EvidenceFileStore(evidenceRoot))
                        .ReconcileAsync(campaignId, "trace.assurance", Now, default);
                    Assert.Equal(1, result.TraceLinksCreated);
                }

                await using (var assertOnce = new AeroLinkDbContext(options))
                {
                    var carried = Assert.Single(await assertOnce.RequirementTraces.AsNoTracking()
                        .Where(x => x.SourceRevisionId == currentHighRevisionId && x.TargetRevisionId == currentSystemRevisionId)
                        .ToListAsync());
                    Assert.Equal(RequirementTraceType.DerivedFrom, carried.Type);
                    Assert.Contains("predecessor baseline", carried.Rationale);
                    Assert.Equal(4, await assertOnce.RequirementTraces.CountAsync());
                }

                await using (var repeat = new AeroLinkDbContext(options))
                {
                    var result = await new ReleaseExecutionService(repeat, new EvidenceFileStore(evidenceRoot))
                        .ReconcileAsync(campaignId, "trace.assurance", Now.AddMinutes(1), default);
                    Assert.Equal(0, result.TraceLinksCreated);
                    Assert.Equal(4, await repeat.RequirementTraces.CountAsync());
                }
            }
            finally
            {
                if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
