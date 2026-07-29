using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReqIfExchangeTests
{
    [Fact]
    public void Parser_rejects_documents_with_a_dtd()
    {
        const string xml="<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><REQ-IF xmlns='http://www.omg.org/spec/ReqIF/20110401/reqif.xsd'>&xxe;</REQ-IF>";
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.ThrowsAny<Exception>(()=>ReqIfExchangeService.ParsePackage(stream,"unsafe.reqif"));
    }

    [Fact]
    public async Task Exported_package_round_trips_identifiers_hierarchy_relations_and_content()
    {
        var database=Path.Combine(Path.GetTempPath(),$"aerolink-reqif-{Guid.NewGuid():N}.db");var evidence=Path.Combine(Path.GetTempPath(),$"aerolink-reqif-evidence-{Guid.NewGuid():N}");
        try
        {
            var options=new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
            await using var db=new AeroLinkDbContext(options);await db.Database.EnsureCreatedAsync();var now=DateTimeOffset.UtcNow;
            var program=new ProgramRecord("Round Trip Program","RTP");var project=new ProjectRecord(program.Id,"Round Trip Project","Control Computer");var release=new SoftwareRelease(project.Id,"1.0",false);
            var scr=new SystemChangeRequest("SCR-00000001",0,project.Id,release.Id,"Seed requirements","Need","Analysis","Solution","author",now);scr.AddRequirementChange("author","SYSR-00000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,"The controller shall retain state.","Safety continuity","Test",now);scr.SubmitForReview("author",[new("reviewer","Reviewer")],now);scr.ApproveActiveStage("reviewer",now);
            var baseline=new CandidateBaseline("BL-00000001",0,project.Id,release.Id,null,"Round-trip baseline","cm",now);baseline.Select(scr,"cm",now);baseline.Freeze("cm",now);
            var first=new RequirementArtifact(project.Id,"SYSR-00000001",RequirementLevel.System,now);var second=new RequirementArtifact(project.Id,"SYSR-00000002",RequirementLevel.System,now);
            var firstRevision=new RequirementRevision(first.Id,0,"The controller shall retain state.","Safety continuity","Test",RequirementRevisionState.Active,scr.Id,baseline.Id,now);var secondRevision=new RequirementRevision(second.Id,0,"The controller shall restore state.","Recovery","Test",RequirementRevisionState.Active,scr.Id,baseline.Id,now);
            var specification=new RequirementSpecification(project.Id,"SYSRD-00000001","Controller requirements","System","Round-trip fixture","author",now);var section=new SpecificationNode(specification.Id,null,1,SpecificationNodeType.Section,"State management",null,"author",now);var firstNode=new SpecificationNode(specification.Id,section.Id,1,SpecificationNodeType.Requirement,"",first.Id,"author",now);var secondNode=new SpecificationNode(specification.Id,section.Id,2,SpecificationNodeType.Requirement,"",second.Id,"author",now);var trace=new RequirementTraceLink(project.Id,secondRevision.Id,firstRevision.Id,RequirementTraceType.DerivedFrom,"Recovery derives from retention.",now);
            db.AddRange(program,project,release,scr,baseline,first,second,firstRevision,secondRevision,specification,section,firstNode,secondNode,trace);await db.SaveChangesAsync();
            var service=new ReqIfExchangeService(db,new EvidenceFileStore(evidence));var exported=await service.ExportAsync(project.Id,null,"author",now,CancellationToken.None);
            await using var package=service.OpenPackage(exported.Job);var parsed=ReqIfExchangeService.ParsePackage(package,exported.Package.OriginalFileName);
            Assert.Equal("1.0",parsed.ReqIfVersion);Assert.Equal(2,parsed.Items.Count);Assert.Equal(3,parsed.HierarchyCount);Assert.Equal(1,parsed.RelationCount);Assert.Contains(parsed.Items,x=>x.Identifier=="SYSR-00000001"&&x.Statement=="The controller shall retain state.");Assert.All(parsed.Items,x=>Assert.True(x.Valid));
        }
        finally{if(File.Exists(database))File.Delete(database);if(Directory.Exists(evidence))Directory.Delete(evidence,true);}
    }

    [Fact]
    public async Task Lifecycle_changes_create_transactional_events_in_the_same_save()
    {
        var options=new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;await using var db=new AeroLinkDbContext(options);await db.Database.OpenConnectionAsync();await db.Database.EnsureCreatedAsync();var now=DateTimeOffset.UtcNow;
        var program=new ProgramRecord("Event Program","EVT");var project=new ProjectRecord(program.Id,"Event Project","Product");var release=new SoftwareRelease(project.Id,"1.0",false);var scr=new SystemChangeRequest("SCR-00000001",0,project.Id,release.Id,"Evented change","P","A","S","author",now);
        db.AddRange(program,project,release,scr);await db.SaveChangesAsync();
        var integrationEvent=await db.IntegrationEvents.SingleAsync(x=>x.ProjectId==project.Id&&x.AggregateId==scr.Id);Assert.Equal("aerolink.change-request.changed",integrationEvent.EventType);Assert.Contains("Evented change",scr.Title);Assert.Contains("Draft",integrationEvent.PayloadJson);
    }
}
