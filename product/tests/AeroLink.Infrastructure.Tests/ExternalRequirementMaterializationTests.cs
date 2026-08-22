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
            var membership = new BaselineImportSourceIdentityMembership(import.Id, identity.Id, true, now);
            var item = new BaselineImportPackageItem(project.Id, import.Id, identity.Id, "CUSR-000001", 0,
                "The customer system shall provide navigation.", "Customer rationale", "\"REQ-42\"", now);
            var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Customer baseline", "cm", now);
            baseline.SelectExternalPackage(import, new[] { item }, "cm", now);
            baseline.Freeze("cm", now);
            db.AddRange(program, project, release, import, identity, membership, item, baseline);
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

    [Fact]
    public async Task Later_import_membership_can_materialize_a_new_revision_and_committing_package_lineage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-external-delta-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("External Delta Program", "EXD");
            var project = new ProjectRecord(program.Id, "Customer Delta Product", "Customer Delta Product");
            var release = new SoftwareRelease(project.Id, "2.0", false);
            var firstImport = new BaselineImport(project.Id, "DOORS", "1", "Customer v1", now,
                "customer-v1.reqif", "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            firstImport.RecordAnalysis(now); firstImport.RecordMapping("{}", now);
            firstImport.NoteSourceRecordsAccountedFor(1, now); firstImport.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(project.Id, firstImport.Id, "DOORS", "Requirements", "77", "REQ-77", now);
            var firstMembership = new BaselineImportSourceIdentityMembership(firstImport.Id, identity.Id, true, now);
            var firstItem = new BaselineImportPackageItem(project.Id, firstImport.Id, identity.Id, "CUSR-000010", 0,
                "The customer system shall provide the original behavior.", "v1", "REQ-77", now);
            var firstBaseline = new CandidateBaseline("SW-02.00", 0, project.Id, release.Id, null,
                "Customer v1 baseline", "cm", now);
            firstBaseline.SelectExternalPackage(firstImport, new[] { firstItem }, "cm", now);
            firstBaseline.Freeze("cm", now);
            db.AddRange(program, project, release, firstImport, identity, firstMembership, firstItem, firstBaseline);
            await db.SaveChangesAsync();

            var policy = CustomerPolicy(project.Id, now);
            var materializer = new RequirementBaselineMaterializer(db, new VerificationImpactService(db), policy: policy);
            await materializer.MaterializeAsync(firstBaseline.Id, "cm", now, default);

            var secondImport = new BaselineImport(project.Id, "DOORS", "1", "Customer v2", now.AddMinutes(1),
                "customer-v2.reqif", "8f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                ImportedArtifactKinds.Requirements, "source", now.AddMinutes(1), "cm", now.AddMinutes(1));
            secondImport.RecordAnalysis(now.AddMinutes(1)); secondImport.RecordMapping("{}", now.AddMinutes(1));
            secondImport.NoteSourceRecordsAccountedFor(1, now.AddMinutes(1));
            secondImport.RecordReconciliation("{}", now.AddMinutes(1));
            var secondMembership = new BaselineImportSourceIdentityMembership(secondImport.Id, identity.Id, true, now.AddMinutes(1));
            var secondItem = new BaselineImportPackageItem(project.Id, secondImport.Id, identity.Id, "CUSR-000010", 1,
                "The customer system shall provide the revised behavior.", "v2", "REQ-77", now.AddMinutes(1));
            var secondBaseline = new CandidateBaseline("SW-02.01", 0, project.Id, release.Id, firstBaseline.Id,
                "Customer v2 baseline", "cm", now.AddMinutes(1));
            secondBaseline.SelectExternalPackage(secondImport, new[] { secondItem }, "cm", now.AddMinutes(1));
            secondBaseline.Freeze("cm", now.AddMinutes(1));
            db.AddRange(secondImport, secondMembership, secondItem, secondBaseline);
            await db.SaveChangesAsync();

            await materializer.MaterializeAsync(secondBaseline.Id, "cm", now.AddMinutes(1), default);

            var customerArtifactId = await db.Requirements.Where(x => x.BaseNumber == "CUSR-000010")
                .Select(x => x.Id).SingleAsync();
            var revisions = await db.RequirementRevisions.AsNoTracking()
                .Where(x => x.ArtifactId == customerArtifactId).OrderBy(x => x.Revision).ToListAsync();
            Assert.Equal(new[] { 0, 1 }, revisions.Select(x => x.Revision));
            Assert.Equal(firstImport.Id, revisions[0].SourceBaselineImportId);
            Assert.Equal(secondImport.Id, revisions[1].SourceBaselineImportId);
            var links = (await db.SourceIdentityLinks.AsNoTracking().ToListAsync())
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
            Assert.Equal(new[] { firstImport.Id, secondImport.Id }, links.Select(x => x.BaselineImportId));
            Assert.Equal(revisions[1].Id, links[1].RequirementRevisionId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stale_customer_package_stage_cannot_commit_after_external_package_binding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-external-stage-race-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            var now = DateTimeOffset.UtcNow;
            Guid projectId, importId, baselineId, lateIdentityId;
            await using (var seed = new AeroLinkDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var program = new ProgramRecord("External Race Program", "EXR");
                var project = new ProjectRecord(program.Id, "Customer Race Product", "Customer Race Product");
                var release = new SoftwareRelease(project.Id, "3.0", false);
                var import = new BaselineImport(project.Id, "DOORS", "1", "Customer race", now,
                    "customer-race.reqif", "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                    ImportedArtifactKinds.Requirements, "source", now, "cm", now);
                import.RecordAnalysis(now); import.RecordMapping("{}", now);
                import.NoteSourceRecordsAccountedFor(2, now); import.RecordReconciliation("{}", now);
                var firstIdentity = new SourceIdentity(project.Id, import.Id, "DOORS", "Requirements", "101", "REQ-101", now);
                var lateIdentity = new SourceIdentity(project.Id, import.Id, "DOORS", "Requirements", "102", "REQ-102", now);
                var firstMembership = new BaselineImportSourceIdentityMembership(import.Id, firstIdentity.Id, true, now);
                var lateMembership = new BaselineImportSourceIdentityMembership(import.Id, lateIdentity.Id, true, now);
                var firstItem = new BaselineImportPackageItem(project.Id, import.Id, firstIdentity.Id, "CUSR-000101", 0,
                    "The first customer requirement.", "", "REQ-101", now);
                var baseline = new CandidateBaseline("SW-03.00", 0, project.Id, release.Id, null,
                    "Customer race baseline", "cm", now);
                seed.AddRange(program, project, release, import, firstIdentity, lateIdentity,
                    firstMembership, lateMembership, firstItem, baseline);
                await seed.SaveChangesAsync();
                projectId = project.Id; importId = import.Id; baselineId = baseline.Id; lateIdentityId = lateIdentity.Id;
            }

            await using var staging = new AeroLinkDbContext(options);
            var staleImport = await staging.BaselineImports.SingleAsync(x => x.Id == importId);
            await using (var binding = new AeroLinkDbContext(options))
            {
                var import = await binding.BaselineImports.SingleAsync(x => x.Id == importId);
                var baseline = await binding.CandidateBaselines.Include(x => x.ExternalPackageSelections)
                    .SingleAsync(x => x.Id == baselineId);
                var items = await binding.BaselineImportPackageItems
                    .Where(x => x.BaselineImportId == importId).ToListAsync();
                baseline.SelectExternalPackage(import, items, "cm", now.AddMinutes(1));
                await binding.SaveChangesAsync();
            }

            staging.BaselineImportPackageItems.Add(new BaselineImportPackageItem(projectId, importId, lateIdentityId,
                "CUSR-000102", 0, "A late customer requirement.", "", "REQ-102", now.AddMinutes(2)));
            staleImport.RecordCustomerPackageStaging(now.AddMinutes(2));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staging.SaveChangesAsync());

            await using var verify = new AeroLinkDbContext(options);
            var storedImport = await verify.BaselineImports.AsNoTracking().SingleAsync(x => x.Id == importId);
            Assert.Equal(BaselineImportState.Accepted, storedImport.State);
            Assert.Equal(baselineId, storedImport.BoundCandidateBaselineId);
            Assert.False(await verify.BaselineImportPackageItems.AnyAsync(x => x.SourceIdentityId == lateIdentityId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Database_rejects_the_same_external_package_in_a_second_candidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-external-package-unique-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("External Unique Program", "EXU");
            var project = new ProjectRecord(program.Id, "Customer Unique Product", "Customer Unique Product");
            var release = new SoftwareRelease(project.Id, "4.0", false);
            var import = new BaselineImport(project.Id, "DOORS", "1", "Customer unique", now,
                "customer-unique.reqif", "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
                ImportedArtifactKinds.Requirements, "source", now, "cm", now);
            import.RecordAnalysis(now); import.RecordMapping("{}", now);
            import.NoteSourceRecordsAccountedFor(1, now); import.RecordReconciliation("{}", now);
            var identity = new SourceIdentity(project.Id, import.Id, "DOORS", "Requirements", "201", "REQ-201", now);
            var membership = new BaselineImportSourceIdentityMembership(import.Id, identity.Id, true, now);
            var item = new BaselineImportPackageItem(project.Id, import.Id, identity.Id, "CUSR-000201", 0,
                "The unique customer requirement.", "", "REQ-201", now);
            var first = new CandidateBaseline("SW-04.00", 0, project.Id, release.Id, null, "First", "cm", now);
            first.SelectExternalPackage(import, new[] { item }, "cm", now);
            db.AddRange(program, project, release, import, identity, membership, item, first);
            await db.SaveChangesAsync();

            var second = new CandidateBaseline("SW-04.01", 0, project.Id, release.Id, first.Id, "Second", "cm", now);
            db.CandidateBaselines.Add(second);
            await db.SaveChangesAsync();
            var secondSelectionId = Guid.NewGuid();
            var hash = BaselineImportPackageManifest.Hash(new[] { item });
            const string actor = "cm";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO baseline_external_package_selections (\"Id\", \"BaselineId\", \"BaselineImportId\", \"PackageContentHash\", \"SelectedAt\", \"SelectedBy\") VALUES ({secondSelectionId}, {second.Id}, {import.Id}, {hash}, {now}, {actor})"));
            Assert.NotNull(error);
            Assert.Equal(1, await db.BaselineExternalPackageSelections.CountAsync(x => x.BaselineImportId == import.Id));
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
