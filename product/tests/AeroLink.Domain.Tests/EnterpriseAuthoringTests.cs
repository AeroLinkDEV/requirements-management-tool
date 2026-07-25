using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Content;

namespace AeroLink.Domain.Tests;

public sealed class EnterpriseAuthoringTests
{
    [Fact]
    public void Configurable_schema_versions_when_fields_are_added()
    {
        var schema=new ArtifactSchemaDefinition(Guid.NewGuid(),"system-req","System Requirement","System","Program-defined requirement fields.","admin",DateTimeOffset.UtcNow);
        schema.AddField("criticality","Criticality",SchemaFieldType.Enumeration,true,10,"[\"Normal\",\"Safety\"]","admin",DateTimeOffset.UtcNow);
        Assert.Equal(2,schema.Version);Assert.Single(schema.Fields);Assert.True(schema.Fields.Single().IsRequired);
        Assert.Throws<DomainException>(()=>schema.AddField("criticality","Duplicate",SchemaFieldType.ShortText,false,20,"[]","admin",DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Comments_require_explicit_resolution_and_retain_disposition()
    {
        var comment=new ArtifactComment(Guid.NewGuid(),"Requirement",Guid.NewGuid(),Guid.NewGuid(),null,"Verification coverage needs clarification.","[]","reviewer",DateTimeOffset.UtcNow);
        comment.Resolve("author","Additional test procedure will be created.",DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(CollaborationState.Dispositioned,comment.State);Assert.Equal("author",comment.ResolvedBy);Assert.Contains("test procedure",comment.Disposition);
        Assert.Throws<DomainException>(()=>comment.Resolve("author","again",DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Controlled_requirement_proposals_freeze_rich_content_and_require_impact_dispositions()
    {
        var now=DateTimeOffset.UtcNow;var scr=new SystemChangeRequest("SWCR-00000001",0,Guid.NewGuid(),Guid.NewGuid(),"Controlled proposal","Problem","Analysis","Solution","author",now,ChangeRequestType.Software);var pending="{\"trace\":\"Pending\",\"verification\":\"Affected\",\"documents\":\"Not Affected\",\"baseline\":\"Affected\",\"collaboration\":\"Not Affected\"}";
        scr.AddRequirementChange("author","HLR-00000001",1,RequirementLevel.HighLevel,RequirementChangeKind.Modify,"The FMS software shall navigate.","Controlled rationale.","Test",now,"**The FMS software** shall navigate.","{\"criticality\":\"Safety Significant\"}",pending);
        // Supporting content that arrives as plain text is adopted as a single paragraph rather than
        // rejected, so nothing an author already wrote is lost to the storage format changing under them.
        Assert.Equal("**The FMS software** shall navigate.",RichContent.ToPlainText(scr.RequirementChanges.Single().RichText));Assert.Throws<DomainException>(()=>scr.SubmitForReview("author",[new("reviewer","Reviewer")],now));
        var complete=pending.Replace("\"Pending\"","\"Affected\"");scr.UpdateDraft("author",scr.Title,scr.Problem,scr.Analysis,scr.Solution,[new("HLR-00000001",1,RequirementLevel.HighLevel,RequirementChangeKind.Modify,"The FMS software shall navigate.","Controlled rationale.","Test","**The FMS software** shall navigate.","{}",complete)],now.AddMinutes(1));
        var cycle=scr.SubmitForReview("author",[new("reviewer","Reviewer")],now.AddMinutes(2));Assert.Equal(64,cycle.SnapshotHash.Length);
    }

    [Fact]
    public void Assignments_and_notifications_preserve_work_state_and_concurrency()
    {
        var now=DateTimeOffset.UtcNow;var assignment=new ArtifactAssignment(Guid.NewGuid(),"Requirement",Guid.NewGuid(),null,"test.engineer","Add coverage","Create an additional test.",now.AddDays(2),"systems.author",now);assignment.Complete("test.engineer",1,now.AddHours(1));Assert.Equal(AssignmentState.Completed,assignment.State);Assert.Equal("test.engineer",assignment.CompletedBy);Assert.Throws<DomainException>(()=>assignment.Complete("test.engineer",1,now));var notification=new UserNotification(Guid.NewGuid(),"test.engineer","Assignment","Coverage","Work assigned.","requirement:1",null,now);notification.MarkRead(now.AddMinutes(1));Assert.Equal(NotificationState.Read,notification.State);
    }
}
