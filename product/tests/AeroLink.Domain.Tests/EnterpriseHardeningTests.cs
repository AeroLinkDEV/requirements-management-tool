using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class EnterpriseHardeningTests
{
    [Fact]
    public void Controlled_attachment_retains_version_and_integrity_authority()
    {
        var now=DateTimeOffset.UtcNow;var logicalId=Guid.NewGuid();
        var first=new ControlledAttachment(Guid.NewGuid(),"Requirement",Guid.NewGuid(),Guid.NewGuid(),logicalId,1,"Interface diagram","Controlled source","fms.png","image/png",42,"a".PadLeft(64,'a'),"aa/file.png",null,"engineer",now);
        first.RecordIntegrityVerification(now.AddMinutes(1));first.Supersede();
        Assert.Equal(ControlledAttachmentState.Superseded,first.State);Assert.NotNull(first.IntegrityVerifiedAt);Assert.Equal(logicalId,first.LogicalId);
        Assert.Throws<DomainException>(()=>new ControlledAttachment(Guid.NewGuid(),"Requirement",Guid.NewGuid(),null,Guid.NewGuid(),1,"Empty","","empty.txt","text/plain",0,"b".PadLeft(64,'b'),"bb/empty.txt",null,"engineer",now));
    }

    [Fact]
    public void Editing_session_rejects_stale_writes_and_job_supports_retry()
    {
        var now=DateTimeOffset.UtcNow;var session=new ArtifactEditSession(Guid.NewGuid(),"Requirement",Guid.NewGuid(),null,"a".PadLeft(64,'a'),"{}","engineer",now);
        session.Save("{\"statement\":\"draft\"}",1,now.AddMinutes(1));Assert.Equal(2,session.Version);Assert.Throws<DomainException>(()=>session.Save("{}",1,now));
        var job=new EnterpriseOperationJob(Guid.NewGuid(),"BackgroundRepositoryExport","{}",100,"engineer",now,"stable-key");job.Start(now);job.ReportProgress(50,now);job.Fail("temporary",now);job.Retry(now);Assert.Equal(EnterpriseJobState.Preview,job.State);job.Start(now);job.Complete(100,0,"{}",now);Assert.Equal(EnterpriseJobState.Completed,job.State);Assert.Equal(2,job.Attempt);
    }

    [Fact]
    public void Merge_resolution_and_integrity_checkpoint_are_attributable()
    {
        var now=DateTimeOffset.UtcNow;var conflict=new ArtifactMergeConflict(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"{}","{\"x\":1}","{\"x\":2}","engineer",now);conflict.Resolve("{\"x\":1}","engineer",now.AddMinutes(1));Assert.Equal("engineer",conflict.ResolvedBy);Assert.Throws<DomainException>(()=>conflict.Resolve("{}","other",now));
        var checkpoint=new EnterpriseIntegrityCheckpoint(Guid.NewGuid(),100,150,4,4096,0,0,"c".PadLeft(64,'c'),IntegrityCheckpointState.Healthy,"All checks passed.","admin",now);Assert.Equal(IntegrityCheckpointState.Healthy,checkpoint.State);
    }
}
