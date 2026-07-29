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
    public void Controlled_requirement_proposals_freeze_rich_content_without_author_owned_impact_gates()
    {
        var now=DateTimeOffset.UtcNow;var scr=new SystemChangeRequest("SWCR-00001",0,Guid.NewGuid(),Guid.NewGuid(),"Controlled proposal","Problem","Analysis","Solution","author",now,ChangeRequestType.Software);var pending="{\"trace\":\"Pending\",\"verification\":\"Affected\",\"documents\":\"Not Affected\",\"baseline\":\"Affected\",\"collaboration\":\"Not Affected\"}";
        scr.AddRequirementChange("author","HLR-00000001",1,RequirementLevel.HighLevel,RequirementChangeKind.Modify,"The FMS software shall navigate.","Controlled rationale.","Test",now,"**The FMS software** shall navigate.","{\"criticality\":\"Safety Significant\"}",pending);
        // Supporting content that arrives as plain text is adopted as a single paragraph rather than
        // rejected, so nothing an author already wrote is lost to the storage format changing under them.
        Assert.Equal("**The FMS software** shall navigate.",RichContent.ToPlainText(scr.RequirementChanges.Single().RichText));
        var cycle=scr.SubmitForReview("author",[new("reviewer","Reviewer")],now.AddMinutes(2));Assert.Equal(64,cycle.SnapshotHash.Length);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"trace\":\"Affected\"}")]
    [InlineData("{\"trace\":\"Affected\",\"verification\":\"Affected\",\"documents\":\"Affected\",\"baseline\":\"Affected\",\"collaboration\":\"Pending\"}")]
    [InlineData("{\"trace\":\"Affected\",\"verification\":\"Affected\",\"documents\":\"Affected\",\"baseline\":\"Affected\",\"collaboration\":\"Affected\",\"invented\":\"Affected\"}")]
    [InlineData("not-json")]
    public void Review_ignores_legacy_author_impact_disposition_metadata(string dispositions)
    {
        var now=DateTimeOffset.UtcNow;
        var scr=new SystemChangeRequest("SCR-00001",0,Guid.NewGuid(),Guid.NewGuid(),"Proposal","Problem","Analysis","Solution","author",now);
        scr.AddRequirementChange("author","SYSR-00000001",0,RequirementLevel.System,RequirementChangeKind.Introduce,
            "The FMS shall navigate.","Rationale","Test",now,impactDispositionJson:dispositions);
        var cycle=scr.SubmitForReview("author",[new("reviewer","Reviewer")],now);
        Assert.Equal(ReviewCycleState.Active,cycle.State);
    }

    [Fact]
    public void Schema_validation_preserves_authored_values_and_overrides_server_owned_derived()
    {
        var now=DateTimeOffset.UtcNow;
        var schema=new ArtifactSchemaDefinition(Guid.NewGuid(),"hlr","HLR","HighLevel","", "admin",now);
        schema.AddField("criticality","Criticality",SchemaFieldType.Enumeration,false,10,
            "[\"Normal\",\"Safety Significant\"]","admin",now);
        schema.AddField("owner","Owner",SchemaFieldType.User,false,20,"[]","admin",now);
        schema.AddField("derived","Derived",SchemaFieldType.Boolean,false,30,"[]","admin",now);

        var merged=RequirementAuthoringJson.ValidateAndMergeAttributes(
            "{\"owner\":\"software.author\",\"criticality\":\"Safety Significant\",\"derived\":false}",schema,true);

        Assert.Equal("{\"criticality\":\"Safety Significant\",\"owner\":\"software.author\",\"derived\":true}",merged);
        Assert.Throws<DomainException>(()=>RequirementAuthoringJson.ValidateAndMergeAttributes(
            "{\"unknown\":\"value\"}",schema,false));
        Assert.Throws<DomainException>(()=>RequirementAuthoringJson.ValidateAndMergeAttributes(
            "{\"criticality\":\"Impossible\"}",schema,false));
        Assert.Throws<DomainException>(()=>RequirementAuthoringJson.ValidateAndMergeAttributes("[]",schema,false));
    }

    [Fact]
    public void Assignments_and_notifications_preserve_work_state_and_concurrency()
    {
        var now=DateTimeOffset.UtcNow;var assignment=new ArtifactAssignment(Guid.NewGuid(),"Requirement",Guid.NewGuid(),null,"test.engineer","Add coverage","Create an additional test.",now.AddDays(2),"systems.author",now);assignment.Complete("test.engineer",1,now.AddHours(1));Assert.Equal(AssignmentState.Completed,assignment.State);Assert.Equal("test.engineer",assignment.CompletedBy);Assert.Throws<DomainException>(()=>assignment.Complete("test.engineer",1,now));var notification=new UserNotification(Guid.NewGuid(),"test.engineer","Assignment","Coverage","Work assigned.","requirement:1",null,now);notification.MarkRead(now.AddMinutes(1));Assert.Equal(NotificationState.Read,notification.State);
    }
}
