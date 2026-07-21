using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProductLineConfigurationTests
{
    [Fact]
    public void Stream_revision_is_immutable_and_component_can_be_reused_by_two_variants()
    {
        var now=DateTimeOffset.UtcNow;
        var component=new ProductLineComponent(Guid.NewGuid(),"COMP-00001","Navigation core","Reusable guidance computation.","cm",now);
        var stream=new ComponentStream(component.Id,"MAIN","Mainline","cm",now);
        var revision=new ComponentStreamRevision(stream.Id,1,"{\"mode\":\"LNAV\"}",new string('a',64),"cm",now);
        component.Approve("cm",now);
        var first=new ProductVariant(component.ProjectId,"CIVIL","Civil transport","{\"market\":\"civil\"}","cm",now);
        var second=new ProductVariant(component.ProjectId,"MIL","Mission transport","{\"market\":\"military\"}","cm",now);
        var firstPick=new VariantComponentSelection(first.Id,revision.Id,"{\"enabled\":true}","cm",now);
        var secondPick=new VariantComponentSelection(second.Id,revision.Id,"{\"enabled\":true}","cm",now);

        Assert.Equal(revision.Id,firstPick.ComponentRevisionId);
        Assert.Equal(revision.Id,secondPick.ComponentRevisionId);
        Assert.Equal(ProductLineComponentState.Approved,component.State);
        Assert.Equal(1,revision.Revision);
    }

    [Fact]
    public void Component_stream_revisions_require_a_positive_revision()
    {
        Assert.ThrowsAny<Exception>(()=>new ComponentStreamRevision(Guid.NewGuid(),0,"{}",new string('b',64),"cm",DateTimeOffset.UtcNow));
    }
}
