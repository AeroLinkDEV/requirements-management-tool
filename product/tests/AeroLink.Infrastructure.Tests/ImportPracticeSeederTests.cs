using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ImportPracticeSeederTests
{
    [Fact]
    public async Task Import_practice_creation_bootstraps_the_new_case_plus_procedure_default_ladder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-import-practice-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var projectId = await new ImportPracticeSeeder(db).EnsureSeededAsync();

            var ladder = await db.ProjectLadderConfigurations
                .Include(x => x.Steps).Include(x => x.AllowedUpstream)
                .SingleAsync(x => x.ProjectId == projectId);
            var resolved = ProjectLadderResolver.Resolve(ladder);

            // #726: every creation seam starts from the new-project default — an authored NonDefault Draft
            // with System [Procedure] and software [Case, Procedure] — so a newly imported practice project
            // executes its Procedures after activation.
            Assert.False(resolved.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Draft, ladder.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel],
                resolved.Steps.Select(x => x.Level));
            Assert.Equal([7, 7, 15], resolved.Steps.Select(x => (int)x.Capabilities));
            Assert.Equal(2, ladder.AllowedUpstream.Count);
            Assert.Equal([VerificationArtifactKind.Procedure],
                resolved.Steps.Single(x => x.Level == RequirementLevel.System).EnabledArtifactKinds);
            Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
                resolved.Steps.Single(x => x.Level == RequirementLevel.HighLevel).EnabledArtifactKinds);
            Assert.Equal([VerificationArtifactKind.Case, VerificationArtifactKind.Procedure],
                resolved.Steps.Single(x => x.Level == RequirementLevel.LowLevel).EnabledArtifactKinds);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}
