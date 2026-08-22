using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ExternalRequirementMaterializationTests
{
    [Fact]
    public async Task External_package_materializes_customer_origin_and_committing_package_lineage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-external-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("External Program", "EXT");
            var project = new ProjectRecord(program.Id, "Customer Product", "Customer Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var import = new BaselineImport(project.Id, "DOORS", "1", "Customer v1", now,
                "customer.reqif", "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            import.RecordAnalysis(now); import.RecordMapping("{}", now); import.NoteSourceRecordsAccountedFor(1, now);
            import.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(project.Id, import.Id, "DOORS", "Requirements", "42", "\"REQ-42\"", now);
            var item = new BaselineImportPackageItem(project.Id, import.Id, identity.Id, "CUSR-000001", 0,
                "The customer system shall provide navigation.", "Customer rationale", "\"REQ-42\"", now);
            var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Customer baseline", "cm", now);
            baseline.SelectExternalPackage(import, new[] { item }, "cm", now);
            baseline.Freeze("cm", now);
            db.AddRange(program, project, release, import, identity, item, baseline);
            await db.SaveChangesAsync();

            var policy = CustomerPolicy(project.Id, now);
            await new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy)
                .MaterializeAsync(baseline.Id, "cm", now, default);

            var artifact = await db.Requirements.SingleAsync(x => x.BaseNumber == "CUSR-000001");
            var revision = await db.RequirementRevisions.SingleAsync(x => x.ArtifactId == artifact.Id);
            Assert.Equal(RequirementLevel.Customer, artifact.Level);
            Assert.Equal(RequirementRevisionOriginKind.ExternalSourcePackage, revision.OriginKind);
            Assert.Null(revision.SourceChangeRequestId);
            Assert.Equal(import.Id, revision.SourceBaselineImportId);
            Assert.Single(await db.BaselineRequirements.Where(x => x.BaselineId == baseline.Id).ToListAsync());
            var link = await db.SourceIdentityLinks.SingleAsync();
            Assert.Equal(import.Id, link.BaselineImportId);
            Assert.Equal(revision.Id, link.RequirementRevisionId);
            Assert.Equal(baseline.Id, revision.EffectiveBaselineId);
        }
        finally { File.Delete(path); }
    }

    private static ILadderPolicy CustomerPolicy(Guid projectId, DateTimeOffset now)
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var levels = new[] { RequirementLevel.Customer, RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel };
        var steps = levels.Select((level, index) => new ProjectLadderStep(configuration.Id, projectId, level, index + 1,
            LegacyLadderPolicy.Instance.Definition(level).Capabilities, now)).ToArray();
        foreach (var step in steps) configuration.Steps.Add(step);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId, steps[2].Id, steps[3].Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
