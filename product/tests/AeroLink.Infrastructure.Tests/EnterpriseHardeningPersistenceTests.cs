using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class EnterpriseHardeningPersistenceTests
{
    [Fact]
    public async Task Hardening_records_persist_with_unique_idempotency_and_concurrency_tokens()
    {
        var options=new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;await using var db=new AeroLinkDbContext(options);await db.Database.OpenConnectionAsync();await db.Database.EnsureCreatedAsync();var now=DateTimeOffset.UtcNow;var projectId=Guid.NewGuid();var artifactId=Guid.NewGuid();
        var attachment=new ControlledAttachment(projectId,"Requirement",artifactId,null,Guid.NewGuid(),1,"Design","","design.pdf","application/pdf",100,"d".PadLeft(64,'d'),"dd/design.pdf",null,"engineer",now);var session=new ArtifactEditSession(projectId,"Requirement",artifactId,null,"e".PadLeft(64,'e'),"{}","engineer",now);var job=new EnterpriseOperationJob(projectId,"BackgroundIntegrityScan","{}",1,"admin",now,"qualification-1");var checkpoint=new EnterpriseIntegrityCheckpoint(projectId,1,1,1,100,0,0,"f".PadLeft(64,'f'),IntegrityCheckpointState.Healthy,"Healthy","admin",now);
        db.AddRange(attachment,session,job,checkpoint);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        Assert.Single(await db.ControlledAttachments.ToListAsync());Assert.Equal(1,(await db.ArtifactEditSessions.SingleAsync()).Version);Assert.Equal("qualification-1",(await db.EnterpriseOperationJobs.SingleAsync()).IdempotencyKey);Assert.Single(await db.EnterpriseIntegrityCheckpoints.ToListAsync());
    }
}
