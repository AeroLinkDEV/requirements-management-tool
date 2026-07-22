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

    [Theory]
    [InlineData(PropagationDecisionKind.Accept,ReuseSynchronizationState.Current)]
    [InlineData(PropagationDecisionKind.Defer,ReuseSynchronizationState.Deferred)]
    [InlineData(PropagationDecisionKind.Reject,ReuseSynchronizationState.Rejected)]
    [InlineData(PropagationDecisionKind.Diverge,ReuseSynchronizationState.Diverged)]
    public void Controlled_library_reuse_retains_each_explicit_upstream_disposition(PropagationDecisionKind decision,ReuseSynchronizationState expected)
    {
        var now=DateTimeOffset.UtcNow;var selected=Guid.NewGuid();var latest=Guid.NewGuid();var reuse=new VariantLibraryReuse(Guid.NewGuid(),Guid.NewGuid(),selected,VariantReuseMode.SynchronizedCopy,"{}","cm",now);reuse.NotifyUpstream(latest,now.AddMinutes(1));reuse.Decide(decision,latest,"Reviewed against the exact product configuration.","cm",now.AddMinutes(2));Assert.Equal(expected,reuse.SynchronizationState);Assert.Equal(decision==PropagationDecisionKind.Accept?latest:selected,reuse.SelectedRevisionId);
    }

    [Fact]
    public void Approved_template_requires_a_successor_revision_before_editing()
    {
        var now=DateTimeOffset.UtcNow;var template=new DocumentTemplate(Guid.NewGuid(),"TPL-00001","SYSRD","{\"title\":\"Controlled\"}","cm",now);Assert.Equal(1,template.Approve("cm",now));Assert.ThrowsAny<Exception>(()=>template.UpdateDraft("changed","{}","cm",now));template.BeginSuccessorRevision("cm",now.AddMinutes(1));template.UpdateDraft("changed","{\"title\":\"Successor\"}","cm",now.AddMinutes(2));Assert.Equal(2,template.Approve("cm",now.AddMinutes(3)));
    }
}
