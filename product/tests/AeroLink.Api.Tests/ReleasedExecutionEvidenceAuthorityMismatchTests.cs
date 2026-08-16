using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroLink.Api.Tests;

/// <summary>
/// Defense-in-depth regression for #423: inconsistent integration/seeder metadata cannot use an in-work
/// execution ReleaseId to hide a released SoftwareBuildId authority.
/// </summary>
public sealed class ReleasedExecutionEvidenceAuthorityMismatchTests
{
    [Fact]
    public async Task Guarded_factory_preserves_the_file_backed_sqlite_contract()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.AssertSqliteConfigurationAsync(factory.Services);
    }

    [Fact]
    public async Task A_released_build_authority_wins_even_when_execution_release_id_is_in_work()
    {
        using var root = new AeroLinkApiFactory();
        using var factory = GuardedFactory(root);
        Guid executionId;
        Guid evidenceId;
        Guid linkId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Evidence authority mismatch", "EAM");
            var project = new ProjectRecord(program.Id, "Mismatch project", "Mismatch product");
            var releasedRelease = new SoftwareRelease(project.Id, "3.0", false);
            var inWorkRelease = new SoftwareRelease(project.Id, "3.1", false, releasedRelease.Id);
            var baseline = new CandidateBaseline("SW-30.00", 0, project.Id, releasedRelease.Id, null,
                "Released authority baseline", "cm", now);
            var releasedBuild = new SoftwareBuild(project.Id, releasedRelease.Id, baseline.Id,
                "SW-30.00", "Released authority build", "cm", now);
            var procedure = new TestProcedure(project.Id, "SYSTP-423099", "Authority mismatch procedure",
                "verification.engineer", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify evidence authority.",
                "Configured rig.", "Exercise the procedure.", "Expected behavior is observed.",
                TestProcedureState.Approved, "verification.engineer", now);
            // Deliberately inconsistent: direct release says in work; build authority says released.
            var execution = new TestExecution(project.Id, revision.Id, releasedBuild.Id, null,
                TestOutcome.Pass, "verification.engineer", "Rig", "Pass", "evidence://mismatch",
                now, now, inWorkRelease.Id);
            var evidence = new EvidenceRecord(project.Id, "mismatch.txt", "text/plain", 1,
                new string('e', 64), "seed/mismatch.txt", "verification.engineer", now);
            db.AddRange(program, project, releasedRelease, inWorkRelease, baseline, releasedBuild,
                procedure, revision, execution, evidence);
            await db.SaveChangesAsync();
            releasedRelease.MarkReleased(now.AddMinutes(1));
            await db.SaveChangesAsync();
            executionId = execution.Id;
            evidenceId = evidence.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var link = new TestExecutionEvidence(executionId, evidenceId);
            linkId = link.Id;
            db.Add(link);
            var exception = await Assert.ThrowsAsync<ReleasedBuildReadOnlyException>(() => db.SaveChangesAsync());
            Assert.Contains("released build", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await verify.TestExecutionEvidence.AnyAsync(x => x.Id == linkId));
        Assert.False(await verify.TestExecutionEvidence.AnyAsync(x => x.TestExecutionId == executionId
            && x.EvidenceId == evidenceId));
    }

    private static WebApplicationFactory<Program> GuardedFactory(AeroLinkApiFactory root) =>
        root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<AeroLinkDbContext>();
            services.RemoveAll<DbContextOptions<AeroLinkDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AeroLinkDbContext>>();
            services.AddDbContext<AeroLinkDbContext>(options =>
                AeroLinkApiFactory.ConfigureSqliteOptions(options, root.ConnectionString, new ReleasedExecutionEvidenceInterceptor()));
        }));
}
