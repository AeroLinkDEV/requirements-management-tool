using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// Controlled editing, and the enterprise surfaces around it: checkout leases, autosave, merge
/// conflicts, integrity checkpoints, and asynchronous jobs.
///
/// An edit session is what makes concurrent authoring safe. A draft lives on the server against a known base
/// snapshot, so two people editing one artifact becomes a conflict the product can describe rather than a
/// silent overwrite.
/// </summary>
public static class EditSessionEndpoints
{
    public static void MapEditSessionEndpoints(this WebApplication app)
    {
        // Enterprise hardening: controlled content, durable operations, merge protection,
        // integrity qualification, and operator-facing health evidence.
        app.MapGet("/api/enterprise-hardening/overview",async(Guid projectId,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            var artifacts=await db.Requirements.AsNoTracking().CountAsync(x=>x.ProjectId==projectId,ct);
            var revisions=await(from revision in db.RequirementRevisions.AsNoTracking() join artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) on revision.ArtifactId equals artifact.Id select revision.Id).CountAsync(ct);
            var attachments=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);
            var jobs=(await db.EnterpriseOperationJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(30).ToList();
            var sessions=(await db.ArtifactEditSessions.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.State==EditSessionState.Active).ToListAsync(ct)).OrderByDescending(x=>x.UpdatedAt).Take(30).ToList();
            var conflicts=(await db.ArtifactMergeConflicts.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ResolvedAt==null).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).Take(30).ToList();
            var views=await db.SavedRequirementViews.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OwnerId==http.UserAccount().Id||x.IsShared)).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Name,x.QueryJson,x.ColumnsJson,x.IsShared,owned=x.OwnerId==http.UserAccount().Id}).ToListAsync(ct);
            var checkpoint=(await db.EnterpriseIntegrityCheckpoints.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).FirstOrDefault();
            var missingFiles=attachments.Count(x=>!store.Exists(x.StorageKey));
            var activeAttachments=attachments.Where(x=>x.State==ControlledAttachmentState.Active).ToList();
            return Results.Ok(new{generatedAt=DateTimeOffset.UtcNow,repository=new{artifacts,revisions,attachments=activeAttachments.Count,attachmentVersions=attachments.Count,attachmentBytes=attachments.Sum(x=>x.Size),missingFiles,views=views.Count},jobs=jobs.Select(x=>new{x.Id,x.JobType,state=x.State.ToString(),x.ItemCount,x.SucceededCount,x.FailedCount,x.ProgressPercent,x.Attempt,x.IdempotencyKey,x.LastError,x.CreatedBy,x.CreatedAt,x.UpdatedAt,x.CompletedAt,x.ResultJson,x.ClaimedBy,x.ClaimedAt,x.LeaseExpiresAt,errorHistory=x.ErrorHistory(),maximumAttempts=EnterpriseJobWorker.MaximumAttempts}),sessions=sessions.Select(x=>new{x.Id,x.ArtifactId,x.ArtifactType,x.UserName,x.State,x.OpenedAt,x.UpdatedAt,x.Version}),conflicts=conflicts.Select(x=>new{x.Id,x.ArtifactId,x.LocalSessionId,x.CompetingSessionId,x.CreatedBy,x.CreatedAt}),views,checkpoint=checkpoint is null?null:new{checkpoint.Id,state=checkpoint.State.ToString(),checkpoint.ManifestHash,checkpoint.Detail,checkpoint.CreatedAt,checkpoint.CreatedBy}});
        });

        app.MapGet("/api/enterprise-hardening/attachments",async(Guid projectId,string artifactType,Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            // Browser-recovery images are private, transient authoring state, not controlled attachment-vault
            // records. Never enumerate them through this project-scoped generic surface; the dedicated image
            // endpoint applies the uploader/session boundary when an editor needs to preview one.
            if(artifactType.Equals("InlineImageDraft",StringComparison.OrdinalIgnoreCase))return Results.Ok(Array.Empty<object>());
            var rows=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ArtifactType==artifactType&&x.ArtifactId==artifactId).OrderBy(x=>x.LogicalId).ThenByDescending(x=>x.Version).ToListAsync(ct);
            return Results.Ok(rows.Select(x=>new{x.Id,x.LogicalId,x.Version,x.RevisionId,x.Label,x.Description,x.OriginalFileName,x.ContentType,x.Size,x.Sha256,state=x.State.ToString(),x.UploadedBy,x.UploadedAt,x.IntegrityVerifiedAt,x.SupersedesId}));
        });

        app.MapPost("/api/enterprise-hardening/attachments",(HttpRequest request,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,ManagedDocumentStorageCoordinator storage,CancellationToken ct)=>
            UploadAttachmentAsync(request,http,db,store,storage,ct)).DisableAntiforgery();

        app.MapPost("/api/enterprise-hardening/attachments/{id:guid}/withdraw",(Guid id,WithdrawSupportingAttachmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
            WithdrawProblemReportAttachmentAsync(id,request,http,db,ct));

        // Inline images are their own surface rather than a use of the attachment vault.
        //
        // An image inside a requirement statement is not a document somebody attached; it is part of what the
        // statement says, and it has to be storable before the record that references it exists, because an author
        // writes the figure into the paragraph as they are drafting it. Uploading here stores and hashes the file
        // against the project, and the authored content then references it by identifier. The file is never
        // duplicated into the record, so one diagram used in five requirements is stored once and stays one thing.

        app.MapGet("/api/enterprise-hardening/attachments/{id:guid}/download",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            var item=await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(item is null)return Results.NotFound();
            if(item.ArtifactType=="InlineImageDraft")return Results.NotFound();
            if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();
            try{return Results.File(await store.OpenVerifiedReadAsync(item.StorageKey,item.Size,item.Sha256,ct),item.ContentType,item.OriginalFileName,enableRangeProcessing:true);}
            catch(EvidenceIntegrityException){return Results.NotFound();}
        });

        app.MapPost("/api/enterprise-hardening/attachments/{id:guid}/verify",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {var item=await db.ControlledAttachments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var actual=await store.ComputeSha256Async(item.StorageKey,ct);var valid=CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual),Convert.FromHexString(item.Sha256));if(valid){item.RecordIntegrityVerification(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);}return Results.Ok(new{valid,expected=item.Sha256,actual,verifiedAt=item.IntegrityVerifiedAt});});

        app.MapPost("/api/enterprise-hardening/jobs",async(CreateEnterpriseJobRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(request.JobType is not "RepositoryExport")return Results.BadRequest(new{error="Only controlled repository exports are available through this job endpoint."});
            if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();var key=string.IsNullOrWhiteSpace(request.IdempotencyKey)?Guid.NewGuid().ToString("N"):request.IdempotencyKey.Trim();var existing=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==request.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{existing.Id,state=existing.State.ToString(),reused=true});
            const string type="BackgroundRepositoryExport";var count=await db.Requirements.CountAsync(x=>x.ProjectId==request.ProjectId,ct);var job=new EnterpriseOperationJob(request.ProjectId,type,request.RequestJson,count,http.UserAccount().UserName,DateTimeOffset.UtcNow,key);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Accepted($"/api/enterprise-hardening/jobs/{job.Id}",new{job.Id,state=job.State.ToString(),reused=false});
        });

        app.MapPost("/api/enterprise-hardening/jobs/{id:guid}/retry",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();try{job.Retry(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Accepted();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

        app.MapPost("/api/enterprise-hardening/jobs/{id:guid}/cancel",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();try{job.Cancel(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

        app.MapGet("/api/enterprise-hardening/jobs/{id:guid}/download",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();if(job.State!=EnterpriseJobState.Completed)return Results.BadRequest(new{error="The output is not complete."});using var result=JsonDocument.Parse(job.ResultJson);if(!result.RootElement.TryGetProperty("StorageKey",out var storage)&&!result.RootElement.TryGetProperty("storageKey",out storage))return Results.BadRequest(new{error="This job has no downloadable output."});var fileName=result.RootElement.TryGetProperty("OriginalFileName",out var name)||result.RootElement.TryGetProperty("originalFileName",out name)?name.GetString():$"aerolink-export-{id:N}.bin";var contentType=result.RootElement.TryGetProperty("ContentType",out var type)||result.RootElement.TryGetProperty("contentType",out type)?type.GetString():"application/octet-stream";return Results.File(store.OpenRead(storage.GetString()!),contentType??"application/octet-stream",fileName,enableRangeProcessing:true);});

        // Bounded, Program-scoped universal search. Results are identifiers plus stable IDs;
        // the client owns the durable URL so every result can be opened in a new tab.

        // Exclusive controlled editing for SCR/SWCR Drafts. The pre-existing enterprise
        // merge endpoints remain available for artifacts configured for optimistic editing.
        app.MapGet("/api/edit-sessions/status",async(string artifactType,Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!artifactType.Equals("SCR",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="This controlled editor currently supports change-request Drafts."});var scr=await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(scr is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,scr.ProjectId,ct))return Results.Forbid();var now=DateTimeOffset.UtcNow;var sessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==artifactId&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in sessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);if(db.ChangeTracker.HasChanges())await db.SaveChangesAsync(ct);var active=sessions.FirstOrDefault(x=>x.State==EditSessionState.Active);return Results.Ok(active is null?new{editable=scr.State==ChangeRequestState.Draft,locked=false}:new{editable=scr.State==ChangeRequestState.Draft,locked=true,sessionId=active.Id,holder=active.UserName,openedAt=active.OpenedAt,lastActivityAt=active.UpdatedAt,expiresAt=active.ExpiresAt,mine=active.UserName==http.UserAccount().UserName});
        });

        app.MapPost("/api/edit-sessions/checkout",async(CheckoutEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!request.ArtifactType.Equals("SCR",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="This controlled editor currently supports change-request Drafts."});var scr=await db.SystemChangeRequests.Include(x=>x.RequirementChanges).SingleOrDefaultAsync(x=>x.Id==request.ArtifactId,ct);if(scr is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,scr.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount();if(scr.State!=ChangeRequestState.Draft)return Results.Conflict(new{error="Approved, frozen, or in-review change requests cannot be checked out for editing."});if(scr.AuthorId!=actor.UserName&&!actor.IsAdministrator)return Results.Forbid();var now=DateTimeOffset.UtcNow;var sessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==scr.Id&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in sessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);await db.SaveChangesAsync(ct);var active=sessions.FirstOrDefault(x=>x.State==EditSessionState.Active);if(active is not null){if(active.UserName==actor.UserName){var latest=await db.ArtifactDraftSnapshots.AsNoTracking().Where(x=>x.SessionId==active.Id).OrderByDescending(x=>x.Sequence).FirstOrDefaultAsync(ct);return Results.Ok(EditSessionMap(active,latest?.DraftJson??active.DraftJson,true));}return Results.Conflict(new{error=$"{active.UserName} has this artifact checked out.",code="exclusive_lock",holder=active.UserName,active.OpenedAt,lastActivityAt=active.UpdatedAt,active.ExpiresAt,readOnly=true});}
            var draft=ControlledScrDraft(scr);var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(draft));var session=new ArtifactEditSession(scr.ProjectId,"SCR",scr.Id,null,hash,draft,actor.UserName,now,true,request.LeaseMinutes??15);db.ArtifactEditSessions.Add(session);db.ArtifactDraftSnapshots.Add(new(scr.ProjectId,session.Id,"SCR",scr.Id,1,draft,hash,actor.UserName,now));db.AuditEvents.Add(new(scr.Id,"ArtifactCheckedOut",actor.UserName,$"Checked out {scr.DisplayNumber} until {session.ExpiresAt:O}.",now));try{await db.SaveChangesAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{error="Another user obtained the edit lock first. Refresh to see the current holder.",code="exclusive_lock"});}return Results.Created($"/api/edit-sessions/{session.Id}",EditSessionMap(session,draft,false));
        });

        app.MapPut("/api/edit-sessions/{id:guid}/autosave",async(Guid id,AutosaveEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(Encoding.UTF8.GetByteCount(request.DraftJson)>2_000_000)return Results.BadRequest(new{error="The recoverable draft exceeds the 2 MB controlled autosave limit."});try{using var parsed=JsonDocument.Parse(request.DraftJson);if(parsed.RootElement.ValueKind!=JsonValueKind.Object)return Results.BadRequest(new{error="The autosave payload must be a JSON object."});}catch(JsonException){return Results.BadRequest(new{error="The autosave payload is not valid JSON."});}var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();try{var now=DateTimeOffset.UtcNow;session.Save(request.DraftJson,request.ExpectedVersion,now,request.LeaseMinutes??15);var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(request.DraftJson));db.ArtifactDraftSnapshots.Add(new(session.ProjectId,session.Id,session.ArtifactType,session.ArtifactId,session.Version,request.DraftJson,hash,http.UserAccount().UserName,now));await db.SaveChangesAsync(ct);return Results.Ok(new{session.Id,session.Version,session.UpdatedAt,session.ExpiresAt,status="Saved",hash});}catch(DomainException ex){return Results.Conflict(new{error=ex.Message,code="edit_session_conflict"});}
        });

        app.MapPost("/api/edit-sessions/{id:guid}/heartbeat",async(Guid id,HeartbeatEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();try{session.Heartbeat(request.ExpectedVersion,DateTimeOffset.UtcNow,request.LeaseMinutes??15);await db.SaveChangesAsync(ct);return Results.Ok(new{session.Id,session.Version,session.UpdatedAt,session.ExpiresAt});}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}});

        app.MapPost("/api/edit-sessions/{id:guid}/discard",async(Guid id,CloseEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
            try
            {
                var session=await ArtifactEditSessionLock.AcquireAsync(db,id,ct);
                if(session is null||!session.IsExclusive)return Results.NotFound();
                if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();
                var now=DateTimeOffset.UtcNow;
                session.Close(EditSessionState.Abandoned,request.ExpectedVersion,now,http.UserAccount().UserName,
                    string.IsNullOrWhiteSpace(request.Reason)?"Draft checkout discarded.":request.Reason);
                db.AuditEvents.Add(new(session.ArtifactId,"EditSessionDiscarded",http.UserAccount().UserName,
                    session.ClosedReason??"Draft checkout discarded.",now));
                await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.NoContent();
            }
            catch(DomainException ex)
            {
                await transaction.RollbackAsync(ct);return Results.Conflict(new{error=ex.Message});
            }
            catch(DbUpdateException)
            {
                await transaction.RollbackAsync(ct);return Results.Conflict(new{error="The editing session changed; refresh before discarding.",code="edit_session_conflict"});
            }
        });

        app.MapPost("/api/edit-sessions/{id:guid}/force-unlock",async(Guid id,ForceUnlockEditSessionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
            try
            {
                var session=await ArtifactEditSessionLock.AcquireAsync(db,id,ct);
                if(session is null||!session.IsExclusive)return Results.NotFound();
                var actor=http.UserAccount();
                if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct))return Results.Forbid();
                if(!actor.IsAdministrator&&!await http.HasProjectRoleAsync(db,identity,session.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();
                var now=DateTimeOffset.UtcNow;session.ForceUnlock(actor.UserName,request.Reason,now);
                db.AuditEvents.Add(new(session.ArtifactId,"EditSessionForceUnlocked",actor.UserName,$"Force-unlocked {session.ArtifactType} held by {session.UserName}. Reason: {request.Reason}",now));
                db.SecurityAuditEvents.Add(new("ForcedUnlock",actor.UserName,$"{session.ArtifactType}:{session.ArtifactId}","Success",request.Reason,http.Connection.RemoteIpAddress?.ToString()??"local",now));
                await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.NoContent();
            }
            catch(DomainException ex){await transaction.RollbackAsync(ct);return Results.BadRequest(new{error=ex.Message});}
            catch(DbUpdateException){await transaction.RollbackAsync(ct);return Results.Conflict(new{error="The editing session changed; refresh before unlocking.",code="edit_session_conflict"});}
        });

        app.MapPost("/api/enterprise-hardening/edit-sessions",async(OpenEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.ArtifactId&&x.ProjectId==request.ProjectId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();var latest=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifact.Id).OrderByDescending(x=>x.Revision).FirstAsync(ct);var snapshot=JsonSerializer.Serialize(new{latest.Statement,latest.Rationale,latest.VerificationMethod});var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));var session=new ArtifactEditSession(request.ProjectId,"Requirement",artifact.Id,latest.Id,hash,snapshot,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ArtifactEditSessions.Add(session);await db.SaveChangesAsync(ct);var competing=await db.ArtifactEditSessions.AsNoTracking().Where(x=>x.ArtifactId==artifact.Id&&x.Id!=session.Id&&x.State==EditSessionState.Active).Select(x=>new{x.Id,x.UserName,x.UpdatedAt,x.Version}).ToListAsync(ct);return Results.Created($"/api/enterprise-hardening/edit-sessions/{session.Id}",new{session.Id,session.Version,session.BaseSnapshotHash,session.DraftJson,competing});
        });

        app.MapPut("/api/enterprise-hardening/edit-sessions/{id:guid}",async(Guid id,SaveEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();var competitor=(await db.ArtifactEditSessions.AsNoTracking().Where(x=>x.ArtifactId==session.ArtifactId&&x.Id!=id&&x.State==EditSessionState.Active).ToListAsync(ct)).Where(x=>x.UpdatedAt>=session.OpenedAt).OrderByDescending(x=>x.UpdatedAt).FirstOrDefault();
            if(competitor is not null){var conflict=new ArtifactMergeConflict(session.ProjectId,session.ArtifactId,session.Id,competitor.Id,session.DraftJson,request.DraftJson,competitor.DraftJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ArtifactMergeConflicts.Add(conflict);session.Close(EditSessionState.Conflict,request.ExpectedVersion,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Conflict(new{error="A concurrent editing session changed this requirement. Review the three-way merge.",conflict.Id,conflict.BaseJson,conflict.LocalJson,conflict.RemoteJson});}
            try{session.Save(request.DraftJson,request.ExpectedVersion,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{session.Id,session.Version,session.UpdatedAt});}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-hardening/conflicts/{id:guid}/resolve",async(Guid id,ResolveMergeConflictRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var conflict=await db.ArtifactMergeConflicts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(conflict is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,conflict.ProjectId,ct))return Results.Forbid();try{conflict.Resolve(request.ResolutionJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

        app.MapPost("/api/enterprise-hardening/integrity-checkpoints",async(CreateIntegrityCheckpointRequest request,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();
            var artifactCount=await db.Requirements.CountAsync(x=>x.ProjectId==request.ProjectId,ct);
            var revisionCount=await(from revision in db.RequirementRevisions join artifact in db.Requirements.Where(x=>x.ProjectId==request.ProjectId) on revision.ArtifactId equals artifact.Id select revision.Id).CountAsync(ct);
            var attachments=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId).ToListAsync(ct);
            var failedJobs=await db.EnterpriseOperationJobs.CountAsync(x=>x.ProjectId==request.ProjectId&&x.State==EnterpriseJobState.Failed,ct);
            var conflicts=await db.ArtifactMergeConflicts.CountAsync(x=>x.ProjectId==request.ProjectId&&x.ResolvedAt==null,ct);
            // Every stored digest is recomputed from the file it names. This counted the files that existed
            // and hashed the counts, so an altered attachment left a Healthy checkpoint behind for as long
            // as the file was still there and the totals still matched — "integrity verified" over a
            // measurement that had never read a byte of controlled content.
            var missing=0;var mismatched=0;var unreadable=0;var digests=new List<string>();
            foreach(var item in attachments.OrderBy(x=>x.StorageKey,StringComparer.Ordinal))
            {
                if(!store.Exists(item.StorageKey)){missing++;digests.Add($"{item.Id:N}:missing");continue;}
                try
                {
                    var actual=await store.ComputeSha256Async(item.StorageKey,ct);
                    if(!string.Equals(actual,item.Sha256,StringComparison.OrdinalIgnoreCase))mismatched++;
                    digests.Add($"{item.Id:N}:{actual}");
                }
                catch(IOException){unreadable++;digests.Add($"{item.Id:N}:unreadable");}
            }
            // The manifest is over the content identities rather than the totals, so it is stable for an
            // unchanged repository and moves the moment a byte does.
            var material=$"{request.ProjectId:N}|{artifactCount}|{revisionCount}|{attachments.Count}|{attachments.Sum(x=>x.Size)}|{failedJobs}|{conflicts}|{string.Join('|',digests)}";
            var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(material));
            var state=missing+mismatched+unreadable>0?IntegrityCheckpointState.Failed:failedJobs+conflicts>0?IntegrityCheckpointState.Attention:IntegrityCheckpointState.Healthy;
            var checkpoint=new EnterpriseIntegrityCheckpoint(request.ProjectId,artifactCount,revisionCount,attachments.Count,attachments.Sum(x=>x.Size),failedJobs,conflicts,hash,state,$"{attachments.Count-missing-unreadable} attachment digest(s) recomputed; {mismatched} altered; {missing} missing file(s); {unreadable} unreadable file(s); {failedJobs} failed job(s); {conflicts} open merge conflict(s).",http.UserAccount().UserName,DateTimeOffset.UtcNow);
            db.EnterpriseIntegrityCheckpoints.Add(checkpoint);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-hardening/integrity-checkpoints/{checkpoint.Id}",new{checkpoint.Id,state=checkpoint.State.ToString(),checkpoint.ManifestHash,checkpoint.Detail});
        });
    }

    private const long MaximumProblemReportAttachmentBytes = 25L * 1024 * 1024;
    private const long MaximumProblemReportAttachmentBytesPerReport = 256L * 1024 * 1024;
    private const long MaximumProblemReportAttachmentBytesPerProject = 2L * 1024 * 1024 * 1024;

    private static async Task<IResult> UploadAttachmentAsync(HttpRequest request,HttpContext http,
        AeroLinkDbContext db,EvidenceFileStore store,ManagedDocumentStorageCoordinator storage,CancellationToken ct)
    {
        if(!request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data."});
        var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
        if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty file."});
        if(!Guid.TryParse(form["projectId"],out var projectId)||!Guid.TryParse(form["artifactId"],out var artifactId))
            return Results.BadRequest(new{error="Project and artifact identifiers are required."});
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
        var artifactType=string.IsNullOrWhiteSpace(form["artifactType"])?"Requirement":form["artifactType"].ToString();
        var label=form["label"].ToString().Trim();var description=form["description"].ToString().Trim();
        if(string.IsNullOrWhiteSpace(label)||label.Length>300||description.Length>4000)
            return Results.BadRequest(new{error="A label is required and attachment text must stay within its controlled limits."});

        var contentType=file.ContentType;
        if(artifactType=="ProblemReport")
        {
            if(file.Length>MaximumProblemReportAttachmentBytes)
                return Results.BadRequest(new{error="Problem Report supporting files are limited to 25 MB.",code="attachment_too_large"});
            var validation=await ValidateProblemReportFileAsync(file,ct);
            if(validation.Error is not null)return Results.BadRequest(new{error=validation.Error,code="attachment_type_denied"});
            contentType=validation.ContentType!;
        }

        StoredEvidence? stored=null;StagedEvidence? staged=null;ManagedDocumentStorageOperation? storageOperation=null;
        var committed=false;var commitAttempted=false;
        try
        {
            await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
            ProblemReport? report=null;
            if(artifactType=="ProblemReport")
            {
                report=await db.ProblemReports.SingleOrDefaultAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct);
                if(report is null)return Results.BadRequest(new{error="The controlled artifact does not belong to this Project."});
                if(report.State is ProblemReportState.Closed or ProblemReportState.Rejected)
                    return Results.Conflict(new{error="Finished Problem Reports cannot accept supporting-file changes.",code="artifact_not_editable"});
                if(!Guid.TryParse(form["editSessionId"],out var editSessionId))return Results.Forbid();
                var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==editSessionId,ct);
                if(!ValidProblemReportSession(session,projectId,artifactId,http.UserAccount().UserName))return Results.Forbid();
                var reportBytes=await db.ControlledAttachments.AsNoTracking()
                    .Where(x=>x.ProjectId==projectId&&x.ArtifactType=="ProblemReport"&&x.ArtifactId==artifactId)
                    .SumAsync(x=>(long?)x.Size,ct)??0;
                if(reportBytes>MaximumProblemReportAttachmentBytesPerReport-file.Length)
                    return Results.Conflict(new{error="This Problem Report has reached its retained supporting-file limit.",code="attachment_report_quota"});
                var projectBytes=await db.ControlledAttachments.AsNoTracking()
                    .Where(x=>x.ProjectId==projectId&&x.ArtifactType=="ProblemReport")
                    .SumAsync(x=>(long?)x.Size,ct)??0;
                if(projectBytes>MaximumProblemReportAttachmentBytesPerProject-file.Length)
                    return Results.Conflict(new{error="This Project has reached its retained Problem Report supporting-file limit.",code="attachment_project_quota"});
            }
            else
            {
                var artifactExists=artifactType switch
                {
                    "Requirement"=>await db.Requirements.AnyAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct),
                    "ChangeRequest"=>await db.SystemChangeRequests.AnyAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct),
                    "TestChangeRequest"=>await db.TestChangeReviews.AnyAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct),
                    _=>false,
                };
                if(!artifactExists)return Results.BadRequest(new{error="The controlled artifact does not belong to this Project."});
                if(artifactType=="ChangeRequest")
                {
                    var changeRequest=await db.SystemChangeRequests.AsNoTracking().SingleAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct);
                    var actor=http.UserAccount();
                    if(!actor.IsAdministrator&&!string.Equals(changeRequest.AuthorId,actor.UserName,StringComparison.OrdinalIgnoreCase))return Results.Forbid();
                    if(changeRequest.State!=ChangeRequestState.Draft)return Results.Conflict(new{error="Supporting files can be added only while the change request is a Draft.",code="artifact_not_editable"});
                }
            }

            Guid? revisionId=Guid.TryParse(form["revisionId"],out var parsedRevision)?parsedRevision:null;
            if(revisionId is not null&&artifactType=="Requirement"&&!await db.RequirementRevisions.AnyAsync(x=>x.Id==revisionId&&x.ArtifactId==artifactId,ct))
                return Results.BadRequest(new{error="The selected revision does not belong to this requirement."});
            var logicalId=Guid.TryParse(form["logicalId"],out var parsedLogical)?parsedLogical:Guid.NewGuid();
            var logicalRows=await db.ControlledAttachments.Where(x=>x.LogicalId==logicalId).ToListAsync(ct);
            if(logicalRows.Any(x=>x.ProjectId!=projectId||x.ArtifactId!=artifactId||x.ArtifactType!=artifactType))
                return Results.BadRequest(new{error="The selected attachment identity belongs to another controlled artifact."});
            var previous=logicalRows.Where(x=>x.State==ControlledAttachmentState.Active).OrderByDescending(x=>x.Version).FirstOrDefault();
            var version=await IdentifierAllocator.ClaimAsync(db,"ATTACHMENT-"+logicalId.ToString("N"),
                ()=>Task.FromResult(logicalRows.Select(x=>x.Version).DefaultIfEmpty(0).Max()+1),ct);
            var now=DateTimeOffset.UtcNow;var actorNow=http.UserAccount();
            if (report is not null)
            {
                // Supporting files use the existing managed-document storage-operation journal. The PR id is
                // carried in DocumentId only because this operation table is deliberately artifact-neutral at
                // the persistence boundary; the operation type and object manifest make the ownership explicit.
                var operationKey=$"problem-report-attachment:{report.Id:N}:{Guid.NewGuid():N}";
                var payloadHash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(
                    $"{projectId:N}|{report.Id:N}|{file.FileName}|{file.Length}|{contentType}|{operationKey}"));
                var started=await storage.BeginAsync(projectId,report.Id,revisionId??Guid.Empty,
                    "ProblemReportAttachment",operationKey,payloadHash,actorNow.UserName,now,ct);
                storageOperation=started.Operation;
                if (started.ExistingResult is not null)
                    throw new ManagedDocumentStorageConflictException("operation_reused", "The supporting-file storage operation was already completed.");
                staged=await store.StageAsync(file.OpenReadStream(),storageOperation.Id,"supporting-attachment",file.FileName,contentType,ct);
                stored=new(staged.OriginalFileName,staged.ContentType,staged.Size,staged.Sha256,staged.StorageKey);
            }
            else
                stored=await store.StoreAsync(file.OpenReadStream(),file.FileName,contentType,ct);
            previous?.Supersede();
            var attachment=new ControlledAttachment(projectId,artifactType,artifactId,revisionId,logicalId,version,label,description,
                stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256,stored.StorageKey,previous?.Id,actorNow.UserName,now);
            if (storageOperation is not null && staged is not null)
            {
                var planned=JsonSerializer.Serialize(new { attachmentId=attachment.Id, artifactType, artifactId,
                    storageKey=staged.StorageKey, size=staged.Size, sha256=staged.Sha256 });
                await storage.RecordPlanAsync(storageOperation,
                    [new ManagedDocumentStagedObject("supporting-attachment",attachment.Id,staged.StagingKey,
                        staged.StorageKey,staged.Size,staged.Sha256)],planned,now,ct);
                await storage.PromoteAsync(storageOperation,[staged],ct);
            }
            db.ControlledAttachments.Add(attachment);await db.SaveChangesAsync(ct);
            await SupersedeAllButNewestAsync(db,projectId,artifactId,logicalId,ct);
            if(report is not null)
            {
                var evidence=await ProblemReportAttachmentEvidence.SnapshotAsync(db,report,ct);
                var detail=previous is null
                    ?$"Attached {attachment.OriginalFileName} v{attachment.Version}; SHA-256 {attachment.Sha256}."
                    :$"Replaced {previous.OriginalFileName} v{previous.Version} with {attachment.OriginalFileName} v{attachment.Version}; SHA-256 {attachment.Sha256}.";
                db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id,report.Revision,
                    previous is null?"SupportingAttachmentAdded":"SupportingAttachmentReplaced",actorNow.UserName,
                    evidence.Hash,evidence.Json,now,detail:detail,actorDisplayName:actorNow.DisplayName));
                await db.SaveChangesAsync(ct);
            }
            // Match the managed-document coordinator's commit order: metadata and the promoted bytes are
            // committed first, then the durable operation is marked Available. If the process stops between
            // those two writes, reconciliation sees the attachment row through the pending manifest and can
            // verify it rather than treating a successful controlled write as an orphan.
            commitAttempted=true;await transaction.CommitAsync(ct);committed=true;
            if (storageOperation is not null)
                await storage.CompleteAsync(storageOperation,now,ct);
            return Results.Created($"/api/enterprise-hardening/attachments/{attachment.Id}",new{attachment.Id,attachment.LogicalId,attachment.Version,attachment.Sha256});
        }
        catch(DbUpdateException)
        {
            CleanupFailedUpload(store,stored,staged,committed,commitAttempted);
            return Results.Conflict(new{error="The supporting file changed concurrently. Refresh and retry.",code="attachment_concurrency"});
        }
        catch{CleanupFailedUpload(store,stored,staged,committed,commitAttempted);throw;}
    }

    private static void CleanupFailedUpload(EvidenceFileStore store,StoredEvidence? stored,StagedEvidence? staged,
        bool committed,bool commitAttempted)
    {
        // Before CommitAsync is attempted, the database transaction will remove the operation journal and no
        // request can legitimately reference these bytes. Once commit is attempted, retain them: the process
        // may have lost the response after PostgreSQL committed, and deleting them could orphan a controlled row.
        if(committed||commitAttempted)return;
        if(staged is not null)store.Delete(staged.StagingKey);
        if(stored is not null)store.Delete(stored.StorageKey);
    }

    private static async Task<IResult> WithdrawProblemReportAttachmentAsync(Guid id,
        WithdrawSupportingAttachmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
        var item=await db.ControlledAttachments.SingleOrDefaultAsync(x=>x.Id==id&&x.ArtifactType=="ProblemReport",ct);
        if(item is null)return Results.NotFound();
        if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();
        var report=await db.ProblemReports.SingleOrDefaultAsync(x=>x.Id==item.ArtifactId&&x.ProjectId==item.ProjectId,ct);
        if(report is null)return Results.NotFound();
        var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==request.EditSessionId,ct);
        if(!ValidProblemReportSession(session,item.ProjectId,item.ArtifactId,http.UserAccount().UserName))return Results.Forbid();
        if(report.State is ProblemReportState.Closed or ProblemReportState.Rejected)
            return Results.Conflict(new{error="Finished Problem Reports cannot remove supporting files.",code="artifact_not_editable"});
        if(item.State!=ControlledAttachmentState.Active)
            return Results.Conflict(new{error="Only the current supporting-file version can be removed.",code="attachment_not_current"});
        var reason=request.Reason?.Trim()??"";
        if(reason.Length is 0 or > 1000)return Results.BadRequest(new{error="A concise removal reason is required."});
        item.Withdraw();await db.SaveChangesAsync(ct);
        var evidence=await ProblemReportAttachmentEvidence.SnapshotAsync(db,report,ct);var actor=http.UserAccount();var now=DateTimeOffset.UtcNow;
        db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id,report.Revision,"SupportingAttachmentRemoved",actor.UserName,
            evidence.Hash,evidence.Json,now,detail:$"Removed {item.OriginalFileName} v{item.Version}; SHA-256 {item.Sha256}. Reason: {reason}",
            rationale:reason,actorDisplayName:actor.DisplayName));
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static bool ValidProblemReportSession(ArtifactEditSession? session,Guid projectId,Guid artifactId,string actor) =>
        session is not null&&session.ProjectId==projectId&&session.ArtifactId==artifactId&&session.IsExclusive
        &&session.ArtifactType.Equals("ProblemReport",StringComparison.OrdinalIgnoreCase)
        &&session.State==EditSessionState.Active&&session.ExpiresAt>DateTimeOffset.UtcNow
        &&string.Equals(session.UserName,actor,StringComparison.OrdinalIgnoreCase);

    private static async Task<(string? ContentType,string? Error)> ValidateProblemReportFileAsync(IFormFile file,CancellationToken ct)
    {
        var name=file.FileName;var safeName=Path.GetFileName(name);
        if(string.IsNullOrWhiteSpace(safeName)||safeName.Length>180||safeName!=name
            ||safeName.Any(character=>char.IsControl(character)||Path.GetInvalidFileNameChars().Contains(character)))
            return (null,"The supporting-file name is not safe.");
        var extension=Path.GetExtension(safeName).ToLowerInvariant();
        var expected=extension switch
        {
            ".png"=>"image/png",
            ".jpg" or ".jpeg"=>"image/jpeg",
            ".pdf"=>"application/pdf",
            ".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt" or ".log"=>"text/plain",
            ".csv"=>"text/csv",
            _=>null,
        };
        if(expected is null)return (null,"That supporting-file type is not allowed.");
        if(!string.Equals(file.ContentType,expected,StringComparison.OrdinalIgnoreCase))
            return (null,"The filename extension and declared media type do not match.");
        await using var stream=file.OpenReadStream();var header=new byte[12];var read=await stream.ReadAsync(header,ct);stream.Position=0;
        var signatureOk=extension switch
        {
            ".png"=>read>=8&&header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            ".jpg" or ".jpeg"=>await ValidJpegAsync(stream,ct),
            ".pdf"=>read>=5&&Encoding.ASCII.GetString(header,0,5)=="%PDF-",
            ".docx"=>ValidOfficePackage(stream,"word/document.xml"),
            ".xlsx"=>ValidOfficePackage(stream,"xl/workbook.xml"),
            _=>!header.AsSpan(0,read).Contains((byte)0),
        };
        return signatureOk?(expected,null):(null,"The file bytes do not match the allowed supporting-file type.");
    }

    private static async Task<bool> ValidJpegAsync(Stream stream,CancellationToken ct)
    {
        // A three-byte SOI prefix is not enough: malformed or truncated JPEGs otherwise reach the renderer
        // and can make a generated publication fail after the attachment was already accepted. JPEGs are
        // bounded by the endpoint before this check, so parsing a bounded copy is deterministic and safe.
        using var buffer=new MemoryStream();await stream.CopyToAsync(buffer,ct);var bytes=buffer.ToArray();
        if(bytes.Length<4||bytes[0]!=0xff||bytes[1]!=0xd8)return false;
        var position=2;var sawFrame=false;
        while(position<bytes.Length)
        {
            if(bytes[position++]!=0xff)return false;
            while(position<bytes.Length&&bytes[position]==0xff)position++;
            if(position>=bytes.Length)return false;
            var marker=bytes[position++];
            if(marker==0xd9)return sawFrame;
            if(marker==0x00)return false;
            if(marker is 0xd8 or 0xd9 or 0x01 or >=0xd0 and <=0xd7)continue;
            if(position+2>bytes.Length)return false;
            var length=(bytes[position]<<8)|bytes[position+1];
            if(length<2||position+length>bytes.Length)return false;
            var frame=marker is >=0xc0 and <=0xc3 or >=0xc5 and <=0xc7 or >=0xc9 and <=0xcb or >=0xcd and <=0xcf;
            if(frame)
            {
                if(length<7)return false;
                var height=(bytes[position+3]<<8)|bytes[position+4];var width=(bytes[position+5]<<8)|bytes[position+6];
                if(width==0||height==0)return false;sawFrame=true;
            }
            position+=length;
            if(marker==0xda)
            {
                // Entropy-coded scan data uses FF 00 byte stuffing and restart markers. Continue until a
                // real marker appears; EOI is accepted only after a valid frame has been seen.
                while(position<bytes.Length)
                {
                    if(bytes[position++]!=0xff)continue;
                    while(position<bytes.Length&&bytes[position]==0xff)position++;
                    if(position>=bytes.Length)return false;
                    var scanMarker=bytes[position++];
                    if(scanMarker==0x00||scanMarker is >=0xd0 and <=0xd7)continue;
                    if(scanMarker==0xd9)return sawFrame;
                    position-=2;break;
                }
            }
        }
        return false;
    }

    private static bool ValidOfficePackage(Stream stream,string requiredPart)
    {
        try
        {
            using var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Read,leaveOpen:true);
            return archive.Entries.Count<=10_000&&archive.GetEntry("[Content_Types].xml") is not null&&archive.GetEntry(requiredPart) is not null;
        }
        catch(InvalidDataException){return false;}
    }

    static string ControlledScrDraft(SystemChangeRequest scr)=>JsonSerializer.Serialize(new{scrVersion=scr.Version,title=scr.Title,problem=scr.Problem,analysis=scr.Analysis,solution=scr.Solution,requirementChanges=scr.RequirementChanges.Select(x=>new{baseNumber=x.BaseNumber,revision=x.Revision,level=x.Level.ToString(),kind=x.Kind.ToString(),statement=x.Statement,rationale=x.Rationale,verificationMethod=x.VerificationMethod,richText=x.RichText,attributesJson=x.AttributesJson,impactDispositionJson=x.ImpactDispositionJson,targetSectionId=x.TargetSectionId})});

    static object EditSessionMap(ArtifactEditSession session,string draftJson,bool resumed)=>new{session.Id,session.ArtifactType,session.ArtifactId,session.Version,session.UserName,session.OpenedAt,lastActivityAt=session.UpdatedAt,session.ExpiresAt,session.BaseSnapshotHash,draftJson,resumed,readOnly=false,status="Saved"};

    /// <summary>
    /// Leaves exactly one Active version of a logical file: the highest. Run after an upload commits, so the
    /// decision is made against the rows that exist rather than against the row one request read.
    /// </summary>
    static async Task SupersedeAllButNewestAsync(AeroLinkDbContext db,Guid projectId,Guid artifactId,Guid logicalId,CancellationToken ct)
    {
        var active=await db.ControlledAttachments.Where(x=>x.ProjectId==projectId&&x.ArtifactId==artifactId&&x.LogicalId==logicalId&&x.State==ControlledAttachmentState.Active).ToListAsync(ct);
        if(active.Count<2)return;
        var newest=active.Max(x=>x.Version);
        foreach(var stale in active.Where(x=>x.Version!=newest))stale.Supersede();
        await db.SaveChangesAsync(ct);
    }
}

public sealed record WithdrawSupportingAttachmentRequest(Guid EditSessionId,string Reason);
