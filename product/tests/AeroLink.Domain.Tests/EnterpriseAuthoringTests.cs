using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

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
}
