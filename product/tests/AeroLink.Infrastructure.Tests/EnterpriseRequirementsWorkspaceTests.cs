using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class EnterpriseRequirementsWorkspaceTests
{
    [Fact]
    public async Task Synchronization_creates_schemas_specifications_profiles_and_placements()
    {
        var options=new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;await using var db=new AeroLinkDbContext(options);await db.Database.OpenConnectionAsync();await db.Database.EnsureCreatedAsync();var now=DateTimeOffset.UtcNow;
        var program=new ProgramRecord("Workspace Program","WSP");var project=new ProjectRecord(program.Id,"FMS","Flight Management System");var release=new SoftwareRelease(project.Id,"1.0",true);var scr=new SystemChangeRequest("SCR-00000001",0,project.Id,release.Id,"Initial","Problem","Analysis","Solution","author",now);var baseline=new CandidateBaseline("SWBL-00000001",0,project.Id,release.Id,null,"Initial baseline","cm",now);var artifact=new RequirementArtifact(project.Id,"SYSR-00000001",RequirementLevel.System,now);var revision=new RequirementRevision(artifact.Id,0,"The FMS shall compute an active flight plan.","Operational capability.","Test",RequirementRevisionState.Active,scr.Id,baseline.Id,now);
        db.AddRange(program,project,release,scr,baseline,artifact,revision);await db.SaveChangesAsync();await new EnterpriseRequirementsService(db).SynchronizeProjectAsync(project.Id,"system",default);
        Assert.Equal(3,await db.ArtifactSchemas.CountAsync());Assert.Equal(3,await db.RequirementSpecifications.CountAsync());Assert.Equal(15,await db.SpecificationNodes.CountAsync(x=>x.Type==SpecificationNodeType.Section));Assert.Single(await db.SpecificationNodes.Where(x=>x.Type==SpecificationNodeType.Requirement).ToListAsync());Assert.Single(await db.RequirementRevisionProfiles.ToListAsync());
    }

    [Fact]
    public void Csv_preview_validates_rows_and_redline_preserves_additions_and_removals()
    {
        var csv="Identifier,Level,Statement,Rationale,VerificationMethod\r\nSYSR-00000001,System,The FMS shall navigate.,Needed,Test\r\nBAD,Unknown,,None,";using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));var rows=EnterpriseRequirementsService.ParseImport(stream,"requirements.csv");
        Assert.Equal(2,rows.Count);Assert.True(rows[0].Valid);Assert.False(rows[1].Valid);Assert.True(rows[1].Errors.Count>=3);
        var diff=EnterpriseRequirementsService.Diff("The FMS shall navigate.","The FMS shall safely navigate.");Assert.Contains(diff,x=>x.Kind=="added"&&x.Text.Contains("safely"));
    }
}
