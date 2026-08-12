using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public static class ManagedDocumentEndpoints
{
    public static IEndpointRouteBuilder MapManagedDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/managed-documents");
        group.MapGet("", ListAsync);
        group.MapGet("/dashboard", DashboardAsync);
        group.MapGet("/link-options", LinkOptionsAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/attachments/{attachmentId:guid}", DownloadAttachmentAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapPost("/{id:guid}/revisions", StartRevisionAsync);
        group.MapPost("/{id:guid}/links", AddLinkAsync);
        group.MapPost("/revisions/{revisionId:guid}/checkout", CheckoutAsync);
        group.MapPost("/revisions/{revisionId:guid}/submit", SubmitAsync);
        group.MapPatch("/revisions/{revisionId:guid}/formal-summary", ReviseFormalSummaryAsync);
        group.MapPost("/revisions/{revisionId:guid}/review/approve", ApproveAsync);
        group.MapPost("/revisions/{revisionId:guid}/review/return", ReturnAsync);
        group.MapPost("/revisions/{revisionId:guid}/release-preparation", PrepareReleaseAsync);
        group.MapPost("/revisions/{revisionId:guid}/force-unlock", ForceUnlockAsync);

        var connector = app.MapGroup("/api/document-connector");
        connector.MapPost("/redeem/{launchToken}", RedeemAsync);
        connector.MapGet("/{grantId:guid}/download", ConnectorDownloadAsync);
        connector.MapPost("/{grantId:guid}/heartbeat", ConnectorHeartbeatAsync);
        connector.MapPost("/{grantId:guid}/check-in", ConnectorCheckInAsync).DisableAntiforgery();
        connector.MapPost("/{grantId:guid}/release-candidate", ConnectorReleaseCandidateAsync).DisableAntiforgery();
        connector.MapPost("/{grantId:guid}/discard", ConnectorDiscardAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(Guid projectId, string? search, string? state, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var documents = await db.ManagedDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); documents = documents.Where(x => x.DocumentNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Acronym.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || x.DocumentType.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList(); }
        var ids = documents.Select(x => x.Id).ToList();
        var revisions = await db.ManagedDocumentRevisions.AsNoTracking().Where(x => ids.Contains(x.DocumentId)).ToListAsync(ct);
        var sessions = await ActiveSessionsAsync(db, ids, ct);
        var items = documents.Select(document => Summary(document, revisions.Where(x => x.DocumentId == document.Id).ToList(), sessions.SingleOrDefault(x => x.ArtifactId == document.Id))).ToList();
        if (!string.IsNullOrWhiteSpace(state)) items = items.Where(x => x.InWorkState.Equals(state, StringComparison.OrdinalIgnoreCase) || x.ReleasedState.Equals(state, StringComparison.OrdinalIgnoreCase)).ToList();
        return Results.Ok(new { totalCount = items.Count, items = items.OrderBy(x => x.DocumentNumber) });
    }

    private static async Task<IResult> DashboardAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var ids = await db.ManagedDocuments.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct);
        var revisions = await db.ManagedDocumentRevisions.AsNoTracking().Where(x => ids.Contains(x.DocumentId)).ToListAsync(ct);
        var active = await ActiveSessionsAsync(db, ids, ct);
        return Results.Ok(new { total = ids.Count, released = revisions.Select(x => x.DocumentId).Distinct().Count(id => revisions.Any(x => x.DocumentId == id && x.State == ManagedDocumentState.Released)), inWork = revisions.Count(x => x.State is ManagedDocumentState.Draft or ManagedDocumentState.InReview or ManagedDocumentState.Returned), inReview = revisions.Count(x => x.State == ManagedDocumentState.InReview), returned = revisions.Count(x => x.State == ManagedDocumentState.Returned), checkedOut = active.Count });
    }

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct)) return Results.Forbid();
        var revisions = (await db.ManagedDocumentRevisions.AsNoTracking().Where(x => x.DocumentId == id).Include(x => x.ReviewSteps).ToListAsync(ct)).OrderByDescending(x => x.Revision).ToList();
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var attachments = await db.ControlledAttachments.AsNoTracking().Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == id).ToListAsync(ct);
        var links = (await db.ManagedDocumentLinks.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct)).OrderBy(x => x.CreatedAt).ToList();
        var buildProvenance = await (from provenance in db.ManagedDocumentBuildProvenance.AsNoTracking() join release in db.Releases.AsNoTracking() on provenance.ReleaseId equals release.Id where provenance.DocumentId == id select new { provenance.ReleaseId, release.Version, provenance.RevisionId, provenance.Source, provenance.RecordedBy, provenance.RecordedAt, meaning = "Legacy build association retained as provenance only" }).ToListAsync(ct);
        var active = (await ActiveSessionsAsync(db, [id], ct)).SingleOrDefault();
        var audits = (await db.ManagedDocumentEvents.AsNoTracking().Where(x => x.DocumentId == id).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList();
        var signatures = (await db.ElectronicSignatures.AsNoTracking().Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == id).ToListAsync(ct)).OrderBy(x => x.SignedAt).ToList();
        var checkIns = (await db.ManagedDocumentCheckIns.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList();
        return Results.Ok(new
        {
            document.Id, document.ProjectId, document.DocumentNumber, document.Acronym, document.DocumentType, document.Title, document.OwnerId, document.CreatedAt, document.UpdatedAt, document.Version,
            lockInfo = active is null ? null : new { active.Id, active.UserName, active.OpenedAt, active.UpdatedAt, active.ExpiresAt }, buildProvenance,
            revisions = revisions.Select(revision => new
            {
                revision.Id, revision.Revision, revision.ParentRevisionId, revision.ParentReleasedDocxAttachmentId, revision.ParentReleasedDocxSha256, revision.TransformationProfile, displayNumber = $"{document.DocumentNumber}.{revision.Revision:D2}", revision.OwnerId, revision.FormalChangeSummary, revision.FormalSummaryHash, revision.FormalSummaryVersion, revision.FormalSummaryProvenance, state = revision.State.ToString(), revision.CurrentWorkingAttachmentId, revision.ReleasedDocxAttachmentId, revision.ReleasedPdfAttachmentId, revision.SnapshotHash, revision.SubmittedFormalSummaryHash, revision.SubmittedFormalSummaryVersion, revision.ReleaseManifestHash, revision.ReturnReason, revision.SubmittedBy, revision.SubmittedAt, revision.ReleasedBy, revision.ReleasedAt, revision.CreatedAt, revision.UpdatedAt, revision.Version,
                reviewSteps = revision.ReviewSteps.OrderBy(x => x.Cycle).ThenBy(x => x.Position).Select(x => new { x.Id, x.Cycle, x.Position, x.StageName, x.ApproverId, x.ApproverName, state = x.State.ToString(), x.Rationale, x.DecidedAt }),
                attachments = attachments.Where(x => x.RevisionId == revision.Id).OrderByDescending(x => x.UploadedAt).Select(Attachment),
                links = links.Where(x => x.RevisionId == revision.Id).Select(x => new { x.Id, x.ArtifactType, x.ArtifactId, x.DisplayNumber, x.Relationship, x.CreatedBy, x.CreatedAt })
                , checkIns = checkIns.Where(x => x.RevisionId == revision.Id).Select(x => new { x.Id, x.WorkingAttachmentId, x.WorkingVersion, x.ActorId, x.Comment, x.BaseAttachmentId, x.BaseSha256, x.ResultSha256, x.SupersededAttachmentId, x.ConnectorSessionId, x.OperationId, x.OccurredAt, x.ReturnResolutionNote })
            }),
            signatures = signatures.Select(x => new { x.Id, x.UserName, x.DisplayName, x.ArtifactRevision, x.Action, x.Meaning, x.ContentHash, x.SignedAt }),
            audit = audits.Select(x => new { x.Id, x.EventType, x.ActorId, x.Detail, x.OccurredAt })
        });
    }

    private static async Task<IResult> CreateAsync(CreateManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentFileService files, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var acronym = request.Acronym.Trim().ToUpperInvariant(); if (acronym.Length is < 2 or > 12 || acronym.Any(c => c is < 'A' or > 'Z')) return Results.BadRequest(new { error = "Use a 2-12 letter document acronym." });
            var numbers = await db.ManagedDocuments.Where(x => x.ProjectId == request.ProjectId && x.Acronym == acronym).Select(x => x.DocumentNumber).ToListAsync(ct); var next = numbers.Select(NumberSequence).DefaultIfEmpty(0).Max() + 1;
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            var document = new ManagedDocument(request.ProjectId, $"{acronym}-{next:D6}", acronym, request.DocumentType, request.Title, request.OwnerId ?? actor.UserName, now);
            var revision = new ManagedDocumentRevision(document.Id, 0, request.OwnerId ?? actor.UserName, request.FormalChangeSummary ?? request.ChangeSummary ?? "Initial controlled draft.", now);
            db.ManagedDocuments.Add(document); db.ManagedDocumentRevisions.Add(revision);
            var context = await ProjectContextAsync(db, request.ProjectId, ct);
            var output = ProfessionalPublicationRenderer.Render(NewDraftPublication(document, revision, context.Project, context.Program), "docx", $"{document.DocumentNumber}.00");
            var attachment = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, revision.Id, 1, "Working Word document", "Initial AeroLink draft template.", output.FileName, output.ContentType, output.Content, null, actor.UserName, now, ct);
            db.ControlledAttachments.Add(attachment); revision.RecordCheckIn(attachment.Id, now);
            db.ManagedDocumentCheckIns.Add(new(revision.Id, attachment.Id, 1, actor.UserName, "Created the initial controlled Word template.", null, null, attachment.Sha256, null, null, $"document-create:{document.Id}", now));
            db.ManagedDocumentEvents.Add(new(document.Id, "DocumentCreated", actor.UserName, $"Created {document.DocumentNumber}.00 as a Project-wide Draft.", now));
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Created($"/api/managed-documents/{document.Id}", new { document.Id, document.DocumentNumber, revisionId = revision.Id });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A document number was allocated concurrently. Retry the create request." }); }
    }

    private static async Task<IResult> StartRevisionAsync(Guid id, StartManagedDocumentRevisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentFileService files, EvidenceFileStore store, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, document.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await db.ManagedDocumentRevisions.AnyAsync(x => x.DocumentId == id && (x.State == ManagedDocumentState.Draft || x.State == ManagedDocumentState.InReview || x.State == ManagedDocumentState.Returned), ct)) return Results.Conflict(new { error = "Complete or withdraw the existing in-work revision before starting another." });
        var released = await db.ManagedDocumentRevisions.Where(x => x.DocumentId == id && x.State == ManagedDocumentState.Released).ToListAsync(ct);
        if (released.Count != 1) return Results.Conflict(new { error = released.Count == 0 ? "Release the initial revision before starting a successor." : "This document has multiple released heads and requires controlled lineage reconciliation.", code = "document_lineage_reconciliation_required" });
        var prior = released[0];
        if (prior.ReleasedDocxAttachmentId is null) return Results.Conflict(new { error = "The released head has no immutable released DOCX source.", code = "document_lineage_reconciliation_required" });
        var priorAttachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == prior.ReleasedDocxAttachmentId && x.RevisionId == prior.Id, ct);
        if (priorAttachment is null || !store.Exists(priorAttachment.StorageKey) || priorAttachment.Size != store.GetSize(priorAttachment.StorageKey) || !string.Equals(await store.ComputeSha256Async(priorAttachment.StorageKey, ct), priorAttachment.Sha256, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "The immutable released parent DOCX is missing or failed size/hash verification.", code = "document_parent_integrity_failure" });
        var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); var revision = new ManagedDocumentRevision(id, prior.Revision + 1, request.OwnerId ?? actor.UserName, request.FormalChangeSummary ?? request.ChangeSummary ?? "", now, prior.Id, priorAttachment.Id, priorAttachment.Sha256, ManagedDocumentFileService.SuccessorTransformationProfile);
        db.ManagedDocumentRevisions.Add(revision);
        await using var input = store.OpenRead(priorAttachment.StorageKey); using var copy = new MemoryStream(); await input.CopyToAsync(copy, ct);
        byte[] nextDraft;
        try { nextDraft = ManagedDocumentFileService.PrepareNextRevisionDraft(copy.ToArray(), document.DocumentNumber, prior.Revision, revision.Revision); }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "document_parent_transform_failure" }); }
        var attachment = await files.StoreAsync(document.ProjectId, id, revision.Id, revision.Id, 1, "Working Word document", "Draft source copied from the last released revision.", $"{document.DocumentNumber}.{revision.Revision:D2}.docx", ManagedDocumentFileService.DocxContentType, nextDraft, null, actor.UserName, now, ct);
        db.ControlledAttachments.Add(attachment); revision.RecordCheckIn(attachment.Id, now);
        db.ManagedDocumentCheckIns.Add(new(revision.Id, attachment.Id, 1, actor.UserName, "Created the successor Draft from the verified released parent DOCX.", priorAttachment.Id, priorAttachment.Sha256, attachment.Sha256, null, null, $"revision-start:{revision.Id}", now));
        db.ManagedDocumentEvents.Add(new(id, "DocumentRevisionStarted", actor.UserName, $"Started {document.DocumentNumber}.{revision.Revision:D2} from verified released parent {document.DocumentNumber}.{prior.Revision:D2} DOCX {priorAttachment.Sha256} using {ManagedDocumentFileService.SuccessorTransformationProfile}.", now));
        try
        {
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Created($"/api/managed-documents/{id}", new { revision.Id, revision.Revision });
        }
        catch (DbUpdateException)
        {
            store.Delete(attachment.StorageKey);
            return Results.Conflict(new { error = "Another request started the active successor revision first. Refresh the document.", code = "document_successor_conflict" });
        }
        catch
        {
            store.Delete(attachment.StorageKey);
            throw;
        }
    }

    private static async Task<IResult> CheckoutAsync(Guid revisionId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        if (data.Revision.State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) return Results.Conflict(new { error = "Only a Draft or returned revision can be checked out." });
        var actor = http.UserAccount(); if (actor.UserName != data.Revision.OwnerId && !actor.IsAdministrator) return Results.Forbid();
        return await CreateGrantAsync(data, "edit", actor.UserName, http, db, ct);
    }

    private static async Task<IResult> PrepareReleaseAsync(Guid revisionId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var active = data.Revision.ReviewSteps.SingleOrDefault(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active);
        var finalPosition = data.Revision.ReviewSteps.Where(x => x.Cycle == data.Revision.CurrentReviewCycle).Select(x => x.Position).DefaultIfEmpty(-1).Max(); var actor = http.UserAccount();
        if (data.Revision.State != ManagedDocumentState.InReview || active is null || active.Position != finalPosition || active.ApproverId != actor.UserName) return Results.Conflict(new { error = "Release preparation is available only to the active final approver after technical review." });
        return await CreateGrantAsync(data, "release", actor.UserName, http, db, ct);
    }

    private static async Task<IResult> CreateGrantAsync(RevisionData data, string mode, string actor, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var sessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == data.Document.Id && x.IsExclusive && x.State == EditSessionState.Active).ToListAsync(ct);
        foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now); var active = sessions.FirstOrDefault(x => x.State == EditSessionState.Active);
        if (active is not null) return Results.Conflict(new { error = active.UserName == actor ? "You already have this document open in the desktop connector." : $"{active.UserName} has this document checked out.", code = "exclusive_lock", holder = active.UserName, active.ExpiresAt });
        var attachment = data.Revision.CurrentWorkingAttachmentId is null ? null : await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct); if (attachment is null) return Results.Conflict(new { error = "Check in a controlled Word working copy before opening the connector." });
        var session = new ArtifactEditSession(data.Document.ProjectId, "ManagedDocument", data.Document.Id, data.Revision.Id, attachment.Sha256, "{}", actor, now, true, 120);
        var launchToken = Token(); var grant = new DocumentConnectorGrant(data.Document.ProjectId, data.Document.Id, data.Revision.Id, session.Id, actor, mode, Hash(launchToken), now);
        db.ArtifactEditSessions.Add(session); db.DocumentConnectorGrants.Add(grant); db.ManagedDocumentEvents.Add(new(data.Document.Id, mode == "edit" ? "DocumentCheckedOut" : "ReleasePreparationOpened", actor, mode == "edit" ? $"Checked out {data.Document.DocumentNumber}.{data.Revision.Revision:D2} for exclusive editing." : $"Opened exact release-candidate preparation for {data.Document.DocumentNumber}.{data.Revision.Revision:D2}.", now)); await db.SaveChangesAsync(ct);
        var server = $"{http.Request.Scheme}://{http.Request.Host}"; var launchUri = $"aerolink://document/{mode}?server={Uri.EscapeDataString(server)}&ticket={Uri.EscapeDataString(launchToken)}";
        return Results.Ok(new { grantId = grant.Id, sessionId = session.Id, session.ExpiresAt, launchUri, mode, holder = actor });
    }

    private static async Task<IResult> SubmitAsync(Guid revisionId, SubmitManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount(); if (actor.UserName != data.Revision.OwnerId && !actor.IsAdministrator) return Results.Forbid();
        if (await db.ArtifactEditSessions.AnyAsync(x => x.ArtifactId == data.Document.Id && x.ArtifactType == "ManagedDocument" && x.State == EditSessionState.Active, ct)) return Results.Conflict(new { error = "Check in or discard the active desktop checkout before submitting." });
        var accounts = await db.UserAccounts.AsNoTracking().Where(x => (x.UserName == request.TechnicalReviewerId || x.UserName == request.FinalApproverId) && x.State == AccountState.Active).ToListAsync(ct); if (accounts.Count != 2) return Results.BadRequest(new { error = "Select two active AeroLink users for document review." });
        var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
        var technicalId = accounts.Single(x => x.UserName == request.TechnicalReviewerId).Id; var finalId = accounts.Single(x => x.UserName == request.FinalApproverId).Id;
        var memberships = await db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == programId && x.EndedAt == null && (x.UserId == technicalId || x.UserId == finalId)).ToListAsync(ct);
        var technicalRoles = new[] { ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.SystemEngineeringLead, ProgramRole.SoftwareEngineeringLead, ProgramRole.ProjectEngineeringLead, ProgramRole.EngineeringManager };
        var finalRoles = new[] { ProgramRole.SoftwareQualityAnalyst, ProgramRole.ConfigurationManager, ProgramRole.Approver, ProgramRole.ProgramManager };
        if (!memberships.Any(x => x.UserId == technicalId && technicalRoles.Contains(x.Role))) return Results.BadRequest(new { error = "The technical reviewer needs review or engineering-lead authority in this Program." });
        if (!memberships.Any(x => x.UserId == finalId && finalRoles.Contains(x.Role))) return Results.BadRequest(new { error = "The final approver needs SQA, configuration, approval, or Program authority." });
        try
        {
            var attachment = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct); var now = DateTimeOffset.UtcNow;
            var snapshotHash = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{attachment.Sha256}:{data.Revision.FormalSummaryHash}:{data.Revision.FormalSummaryVersion}"));
            var cycle = data.Revision.SubmitForReview(actor.UserName, snapshotHash, [new(request.TechnicalReviewerId, accounts.Single(x => x.UserName == request.TechnicalReviewerId).DisplayName, "Technical review"), new(request.FinalApproverId, accounts.Single(x => x.UserName == request.FinalApproverId).DisplayName, "SQA / configuration release authorization")], now);
            db.ManagedDocumentReviewSteps.AddRange(data.Revision.ReviewSteps.Where(x => x.Cycle == cycle));
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentSubmitted", actor.UserName, $"Submitted {data.Document.DocumentNumber}.{data.Revision.Revision:D2} for independent review.", now)); db.UserNotifications.Add(new(data.Document.ProjectId, request.TechnicalReviewerId, "DocumentReviewActivated", $"Review {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Technical document review is ready.", $"managed-document:{data.Document.Id}", data.Document.Id, now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { state = data.Revision.State.ToString(), data.Revision.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> ReviseFormalSummaryAsync(Guid revisionId, ReviseManagedDocumentFormalSummaryRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        var actor = http.UserAccount();
        if (!string.Equals(data.Revision.OwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase)
            && !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        try
        {
            var oldHash = data.Revision.FormalSummaryHash; var now = DateTimeOffset.UtcNow;
            data.Revision.ReviseFormalSummary(request.FormalChangeSummary, request.Reason, request.ExpectedVersion, now);
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentFormalSummaryRevised", actor.UserName, $"Revised the formal scope for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} from {oldHash} to {data.Revision.FormalSummaryHash}. Reason: {request.Reason}", now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { formalChangeSummary = data.Revision.FormalChangeSummary, data.Revision.FormalSummaryHash, data.Revision.FormalSummaryVersion, data.Revision.Version });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
    }

    private static async Task<IResult> ApproveAsync(Guid revisionId, DocumentReviewDecisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid(); var actor = http.UserAccount();
        if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
        try
        {
            var now = DateTimeOffset.UtcNow; var final = data.Revision.Approve(actor.UserName, request.Rationale, now); var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct); var contentHash = final ? data.Revision.ReleaseManifestHash : data.Revision.SnapshotHash;
            db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ManagedDocument", data.Document.Id, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}", final ? "Release" : "Approve", request.Meaning, contentHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); db.ManagedDocumentEvents.Add(new(data.Document.Id, final ? "DocumentReleased" : "DocumentReviewApproved", actor.UserName, final ? $"Released {data.Document.DocumentNumber}.{data.Revision.Revision:D2} as the exact approved DOCX/PDF pair." : $"Approved the active review stage for {data.Document.DocumentNumber}.{data.Revision.Revision:D2}.", now));
            if (final)
            {
                var older = await db.ManagedDocumentRevisions.Where(x => x.DocumentId == data.Document.Id && x.Id != data.Revision.Id && x.State == ManagedDocumentState.Released).ToListAsync(ct); foreach (var prior in older.Where(x => x.Revision < data.Revision.Revision)) prior.Supersede(now);
            }
            else { var next = data.Revision.ReviewSteps.Single(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active); db.UserNotifications.Add(new(data.Document.ProjectId, next.ApproverId, "DocumentReviewActivated", $"Review {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Final document release review is ready.", $"managed-document:{data.Document.Id}", data.Document.Id, now)); }
            await db.SaveChangesAsync(ct); return Results.Ok(new { final, state = data.Revision.State.ToString(), data.Revision.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> ReturnAsync(Guid revisionId, DocumentReviewDecisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid(); var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
        try { var now = DateTimeOffset.UtcNow; data.Revision.Return(actor.UserName, request.Rationale, now); var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct); db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ManagedDocument", data.Document.Id, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Return", request.Meaning, data.Revision.SnapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentReturned", actor.UserName, $"Returned {data.Document.DocumentNumber}.{data.Revision.Revision:D2}: {request.Rationale}", now)); db.UserNotifications.Add(new(data.Document.ProjectId, data.Revision.OwnerId, "DocumentReturned", $"Returned {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", request.Rationale, $"managed-document:{data.Document.Id}", data.Document.Id, now)); await db.SaveChangesAsync(ct); return Results.Ok(new { state = data.Revision.State.ToString(), data.Revision.Version }); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> ForceUnlockAsync(Guid revisionId, ForceUnlockManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound(); var actor = http.UserAccount(); if (!actor.IsAdministrator && !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid(); var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == data.Document.Id && x.State == EditSessionState.Active, ct); if (session is null) return Results.NotFound();
        try { var now = DateTimeOffset.UtcNow; session.ForceUnlock(actor.UserName, request.Reason, now); var grants = await db.DocumentConnectorGrants.Where(x => x.EditSessionId == session.Id && x.RevokedAt == null).ToListAsync(ct); foreach (var grant in grants) grant.Revoke(now); db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentForceUnlocked", actor.UserName, $"Force-unlocked the checkout held by {session.UserName}. Reason: {request.Reason}", now)); db.SecurityAuditEvents.Add(new("DocumentForceUnlock", actor.UserName, data.Document.DocumentNumber, "Success", request.Reason, http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct); return Results.NoContent(); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> AddLinkAsync(Guid id, ManagedDocumentLinkRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct)) return Results.Forbid(); var revision = await db.ManagedDocumentRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RevisionId && x.DocumentId == id, ct); if (revision is null) return Results.BadRequest(new { error = "The selected revision does not belong to this document." }); if (!await LinkExistsAsync(request.ArtifactType, request.ArtifactId, document.ProjectId, db, ct)) return Results.BadRequest(new { error = "The linked artifact is not in this project or is not a supported link type." });
        try { var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); db.ManagedDocumentLinks.Add(new(request.RevisionId, CanonicalLinkType(request.ArtifactType), request.ArtifactId, request.DisplayNumber, request.Relationship, actor.UserName, now)); db.ManagedDocumentEvents.Add(new(id, "DocumentArtifactLinked", actor.UserName, $"Linked {request.DisplayNumber.ToUpperInvariant()} as {request.Relationship}.", now)); await db.SaveChangesAsync(ct); return Results.Created($"/api/managed-documents/{id}", new { request.ArtifactId }); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } catch (DbUpdateException) { return Results.Conflict(new { error = "That artifact is already linked to this document revision." }); }
    }

    private static async Task<IResult> LinkOptionsAsync(Guid projectId, string artifactType, string? search, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var term = search?.Trim().ToLowerInvariant() ?? ""; var type = CanonicalLinkType(artifactType);
        if (type == "ChangeRequest")
        {
            var rows = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, x.Title, secondary = x.State.ToString() }));
        }
        if (type == "ProblemReport")
        {
            var rows = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, x.Title, secondary = x.State.ToString() }));
        }
        if (type == "TestChangeRequest")
        {
            var rows = await db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.SourceChangeRequestNumber.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, title = x.SourceChangeRequestNumber, secondary = x.State.ToString() }));
        }
        if (type == "Release")
        {
            var rows = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, displayNumber = $"BUILD-{x.Version}", title = $"Build {x.Version}", secondary = x.IsReleased ? "Released" : "In work" }));
        }
        return Results.BadRequest(new { error = "Choose a supported lifecycle artifact type." });
    }

    private static async Task<IResult> DownloadAttachmentAsync(Guid attachmentId, HttpContext http, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct)
    { var attachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachmentId && x.ArtifactType == "ManagedDocument", ct); if (attachment is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, attachment.ProjectId, ct)) return Results.Forbid(); if (!store.Exists(attachment.StorageKey)) return Results.NotFound(); return Results.File(store.OpenRead(attachment.StorageKey), attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true); }

    private static async Task<IResult> RedeemAsync(string launchToken, AeroLinkDbContext db, CancellationToken ct)
    { var now = DateTimeOffset.UtcNow; var grant = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.LaunchTokenHash == Hash(launchToken), ct); if (grant is null) return Results.Unauthorized(); try { var accessToken = Token(); grant.Redeem(Hash(accessToken), now); var session = await db.ArtifactEditSessions.SingleAsync(x => x.Id == grant.EditSessionId, ct); await db.SaveChangesAsync(ct); var document = await db.ManagedDocuments.AsNoTracking().SingleAsync(x => x.Id == grant.DocumentId, ct); var revision = await db.ManagedDocumentRevisions.AsNoTracking().SingleAsync(x => x.Id == grant.RevisionId, ct); return Results.Ok(new { grant.Id, accessToken, grant.Mode, documentNumber = $"{document.DocumentNumber}.{revision.Revision:D2}", document.Title, expiresAt = grant.ExpiresAt, sessionVersion = session.Version }); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } }

    private static async Task<IResult> ConnectorDownloadAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; var revision = await db.ManagedDocumentRevisions.AsNoTracking().SingleAsync(x => x.Id == auth.Grant!.RevisionId, ct); if (revision.CurrentWorkingAttachmentId is null) return Results.NotFound(); var attachment = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == revision.CurrentWorkingAttachmentId, ct); return Results.File(store.OpenRead(attachment.StorageKey), attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true); }

    private static async Task<IResult> ConnectorHeartbeatAsync(Guid grantId, ConnectorVersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; try { var now = DateTimeOffset.UtcNow; auth.Session!.Heartbeat(request.ExpectedVersion, now, 120); auth.Grant!.Extend(now); await db.SaveChangesAsync(ct); return Results.Ok(new { auth.Session.Version, auth.Session.ExpiresAt }); } catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); } }

    private static async Task<IResult> ConnectorCheckInAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, ManagedDocumentFileService files, CancellationToken ct)
    {
        var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error;
        if (auth.Grant!.Mode != "edit") return Results.BadRequest(new { error = "This connector session is for release preparation, not draft check-in." });
        var form = await http.Request.ReadFormAsync(ct); var upload = form.Files.GetFile("file"); var comment = form["comment"].ToString().Trim();
        if (upload is null) return Results.BadRequest(new { error = "Choose the edited Word document to check in." });
        if (!long.TryParse(form["expectedVersion"], out var expectedVersion)) return Results.BadRequest(new { error = "The connector session version is required." });
        if (comment.Length == 0) return Results.BadRequest(new { error = "A check-in comment is required." });
        if (comment.Length > 4000) return Results.BadRequest(new { error = "A check-in comment cannot exceed 4000 characters." });
        try
        {
            var data = await RevisionDataAsync(db, auth.Grant.RevisionId, ct); if (data is null) return Results.NotFound();
            await using var source = upload.OpenReadStream(); var content = await files.ReadDocxAsync(source, upload.FileName, true, ct);
            var current = data.Revision.CurrentWorkingAttachmentId is null ? null : await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct);
            if (current is null || !string.Equals(current.Sha256, auth.Session!.BaseSnapshotHash, StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new { error = "The checked-in source changed after this checkout. No file was overwritten.", code = "document_snapshot_conflict" });
            var version = await db.ControlledAttachments.CountAsync(x => x.LogicalId == data.Revision.Id, ct) + 1; var now = DateTimeOffset.UtcNow;
            var returnResolution = data.Revision.State == ManagedDocumentState.Returned ? comment : null;
            var next = await files.StoreAsync(data.Document.ProjectId, data.Document.Id, data.Revision.Id, data.Revision.Id, version, "Working Word document", comment, upload.FileName, ManagedDocumentFileService.DocxContentType, content, current.Id, auth.Grant.UserName, now, ct);
            current.Supersede(); db.ControlledAttachments.Add(next); data.Revision.RecordCheckIn(next.Id, now);
            db.ManagedDocumentCheckIns.Add(new(data.Revision.Id, next.Id, version, auth.Grant.UserName, comment, current.Id, current.Sha256, next.Sha256, current.Id, auth.Session.Id, $"connector-check-in:{auth.Grant.Id}", now, returnResolution));
            auth.Session.Close(EditSessionState.Committed, expectedVersion, now, auth.Grant.UserName, comment); auth.Grant.Revoke(now);
            db.ManagedDocumentEvents.Add(new(data.Document.Id, returnResolution is null ? "DocumentCheckedIn" : "DocumentReturnResolved", auth.Grant.UserName, returnResolution is null ? $"Checked in {data.Document.DocumentNumber}.{data.Revision.Revision:D2} working version {version}: {comment}" : $"Resolved the returned review for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} in working version {version}: {comment}", now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { attachmentId = next.Id, next.Sha256, workingVersion = version, documentVersion = data.Revision.Version });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> ConnectorReleaseCandidateAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, ManagedDocumentFileService files, CancellationToken ct)
    {
        var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; if (auth.Grant!.Mode != "release") return Results.BadRequest(new { error = "This connector session is for draft editing, not release preparation." }); var form = await http.Request.ReadFormAsync(ct); var docxUpload = form.Files.GetFile("docx"); var pdfUpload = form.Files.GetFile("pdf"); if (docxUpload is null || pdfUpload is null) return Results.BadRequest(new { error = "The exact clean DOCX and PDF release renditions are both required." }); if (!long.TryParse(form["expectedVersion"], out var expectedVersion)) return Results.BadRequest(new { error = "The connector session version is required." });
        try { await using var docxStream = docxUpload.OpenReadStream(); var docx = await files.ReadDocxAsync(docxStream, docxUpload.FileName, false, ct); ManagedDocumentFileService.ValidateReleaseDocx(docx); await using var pdfStream = pdfUpload.OpenReadStream(); using var pdfBuffer = new MemoryStream(); await pdfStream.CopyToAsync(pdfBuffer, ct); var pdf = pdfBuffer.ToArray(); ManagedDocumentFileService.ValidatePdf(pdf); var data = await RevisionDataAsync(db, auth.Grant.RevisionId, ct, true); if (data is null) return Results.NotFound(); var now = DateTimeOffset.UtcNow; var summaryMetadata = $"Formal revision scope v{data.Revision.FormalSummaryVersion} ({data.Revision.FormalSummaryHash}): {data.Revision.FormalChangeSummary}"; var docxAttachment = await files.StoreAsync(data.Document.ProjectId, data.Document.Id, data.Revision.Id, Guid.NewGuid(), 1, "Release candidate DOCX", summaryMetadata, docxUpload.FileName, ManagedDocumentFileService.DocxContentType, docx, null, auth.Grant.UserName, now, ct); var pdfAttachment = await files.StoreAsync(data.Document.ProjectId, data.Document.Id, data.Revision.Id, Guid.NewGuid(), 1, "Release candidate PDF", summaryMetadata, pdfUpload.FileName, ManagedDocumentFileService.PdfContentType, pdf, null, auth.Grant.UserName, now, ct); var manifest = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{docxAttachment.Sha256}:{pdfAttachment.Sha256}:{data.Revision.FormalSummaryHash}:{data.Revision.FormalSummaryVersion}")); db.ControlledAttachments.AddRange(docxAttachment, pdfAttachment); data.Revision.RecordReleaseCandidate(docxAttachment.Id, pdfAttachment.Id, manifest, auth.Grant.UserName, now); auth.Session!.Close(EditSessionState.Committed, expectedVersion, now, auth.Grant.UserName, "Prepared exact DOCX and PDF release candidate."); auth.Grant.Revoke(now); db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentReleaseCandidatePrepared", auth.Grant.UserName, $"Prepared the exact DOCX/PDF release candidate for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} with formal summary {data.Revision.FormalSummaryHash} v{data.Revision.FormalSummaryVersion}.", now)); await db.SaveChangesAsync(ct); return Results.Ok(new { manifestHash = manifest, docxSha256 = docxAttachment.Sha256, pdfSha256 = pdfAttachment.Sha256, formalSummaryHash = data.Revision.FormalSummaryHash, formalSummaryVersion = data.Revision.FormalSummaryVersion }); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> ConnectorDiscardAsync(Guid grantId, ConnectorDiscardRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; try { var now = DateTimeOffset.UtcNow; auth.Session!.Close(EditSessionState.Abandoned, request.ExpectedVersion, now, auth.Grant!.UserName, request.Reason ?? "Desktop checkout discarded."); auth.Grant.Revoke(now); db.ManagedDocumentEvents.Add(new(auth.Grant.DocumentId, "DocumentCheckoutDiscarded", auth.Grant.UserName, request.Reason ?? "Desktop checkout discarded without check-in.", now)); await db.SaveChangesAsync(ct); return Results.NoContent(); } catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); } }

    private static async Task<ConnectorAuth> ConnectorAuthAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var bearer = http.Request.Headers.Authorization.ToString(); if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return new(null, null, Results.Unauthorized()); var grant = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct); if (grant is null || !grant.IsAccessValid(DateTimeOffset.UtcNow) || !FixedEquals(grant.AccessTokenHash!, Hash(bearer[7..].Trim()))) return new(null, null, Results.Unauthorized()); var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == grant.EditSessionId, ct); if (session is null || session.State != EditSessionState.Active || session.ExpiresAt <= DateTimeOffset.UtcNow) return new(grant, session, Results.Json(new { error = "The desktop checkout has expired." }, statusCode: 409)); return new(grant, session, null); }

    private static async Task<List<ArtifactEditSession>> ActiveSessionsAsync(AeroLinkDbContext db, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    { var now = DateTimeOffset.UtcNow; var sessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument" && documentIds.Contains(x.ArtifactId) && x.State == EditSessionState.Active).ToListAsync(ct); foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now); if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct); return sessions.Where(x => x.State == EditSessionState.Active).ToList(); }

    private static async Task<RevisionData?> RevisionDataAsync(AeroLinkDbContext db, Guid revisionId, CancellationToken ct, bool includeReviews = false)
    { var query = db.ManagedDocumentRevisions.AsQueryable(); if (includeReviews) query = query.Include(x => x.ReviewSteps); var revision = await query.SingleOrDefaultAsync(x => x.Id == revisionId, ct); if (revision is null) return null; var document = await db.ManagedDocuments.SingleAsync(x => x.Id == revision.DocumentId, ct); return new(document, revision); }

    private static object Attachment(ControlledAttachment x) => new { x.Id, x.LogicalId, x.Version, x.Label, x.Description, x.OriginalFileName, x.ContentType, x.Size, x.Sha256, state = x.State.ToString(), x.UploadedBy, x.UploadedAt, downloadUrl = $"/api/managed-documents/attachments/{x.Id}" };
    private static ManagedDocumentSummary Summary(ManagedDocument document, IReadOnlyList<ManagedDocumentRevision> revisions, ArtifactEditSession? session)
    { var releasedHeads = revisions.Where(x => x.State == ManagedDocumentState.Released).ToList(); var released = releasedHeads.Count == 1 ? releasedHeads[0] : null; var inWorkHeads = revisions.Where(x => x.State is ManagedDocumentState.Draft or ManagedDocumentState.InReview or ManagedDocumentState.Returned).ToList(); var inWork = inWorkHeads.Count == 1 ? inWorkHeads[0] : null; var reconciliationRequired = releasedHeads.Count > 1 || inWorkHeads.Count > 1; return new(document.Id, document.DocumentNumber, document.Acronym, document.DocumentType, document.Title, document.OwnerId, released is null ? "None" : $"{document.DocumentNumber}.{released.Revision:D2}", reconciliationRequired ? "ReconciliationRequired" : released?.State.ToString() ?? "NotReleased", inWork is null ? null : $"{document.DocumentNumber}.{inWork.Revision:D2}", reconciliationRequired ? "ReconciliationRequired" : inWork?.State.ToString() ?? "None", inWork is null ? null : session?.UserName, inWork is null ? null : session?.ExpiresAt, reconciliationRequired, document.UpdatedAt); }

    private static ProfessionalPublication NewDraftPublication(ManagedDocument document, ManagedDocumentRevision revision, string project, string program)
    { var hash = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{document.DocumentNumber}|{revision.Revision}|{revision.FormalChangeSummary}")); return new ProfessionalPublication("AeroLink", program, project, document.DocumentType, document.Title, "Controlled Project document", document.DocumentNumber, revision.Revision.ToString("D2"), "Draft", "Project-wide", "All software builds", revision.OwnerId, revision.CreatedAt, hash, [("Document owner", revision.OwnerId), ("Applicability", "Project-wide; build links are contextual traceability only"), ("Formal change summary", revision.FormalChangeSummary)], [], [(revision.Revision.ToString("D2"), "Draft", revision.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"), revision.OwnerId)], [new("1. Purpose and scope", "Complete this controlled Word template using the applicable project standard.", [new("1.1", "Author guidance", "Purpose", "State why this document exists, what it governs, and where its applicability begins and ends.", [("Status", "Draft")])]), new("2. Controlled content", "Replace the guidance below with the approved lifecycle content.", [new("2.1", "Author guidance", "Lifecycle content", "Identify responsibilities, inputs, activities, outputs, transition criteria, records and linked AeroLink artifacts.", [("Working format", "Macro-free Microsoft Word DOCX")])]), new("3. Review and release", "AeroLink records review evidence outside the editable document.", [new("3.1", "Release criteria", "Independent approval", "A technical reviewer and a separate final SQA or configuration approver must approve the exact release candidate.", [("Released formats", "DOCX and PDF")])])]) { Watermark = "DRAFT" }; }

    private static async Task<(string Program, string Project)> ProjectContextAsync(AeroLinkDbContext db, Guid projectId, CancellationToken ct) => await (from project in db.Projects.AsNoTracking() join program in db.Programs.AsNoTracking() on project.ProgramId equals program.Id where project.Id == projectId select new ValueTuple<string, string>(program.Name, project.Name)).SingleAsync(ct);
    private static int NumberSequence(string value) => int.TryParse(value[(value.LastIndexOf('-') + 1)..], out var number) ? number : 0;
    private static string CanonicalLinkType(string type) => type.Trim().ToLowerInvariant() switch { "changerequest" or "change-request" or "srcr" or "hlrcr" or "llrcr" => "ChangeRequest", "testchangerequest" or "test-change-request" or "tcr" => "TestChangeRequest", "problemreport" or "problem-report" or "pr" => "ProblemReport", "release" or "build" => "Release", _ => type.Trim() };
    private static async Task<bool> LinkExistsAsync(string type, Guid id, Guid projectId, AeroLinkDbContext db, CancellationToken ct) => CanonicalLinkType(type) switch { "ChangeRequest" => await db.SystemChangeRequests.AnyAsync(x => x.Id == id && x.ProjectId == projectId, ct), "TestChangeRequest" => await db.TestChangeReviews.AnyAsync(x => x.Id == id && x.ProjectId == projectId, ct), "ProblemReport" => await db.ProblemReports.AnyAsync(x => x.Id == id && x.ProjectId == projectId, ct), "Release" => await db.Releases.AnyAsync(x => x.Id == id && x.ProjectId == projectId, ct), _ => false };
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private sealed record RevisionData(ManagedDocument Document, ManagedDocumentRevision Revision);
    private sealed record ConnectorAuth(DocumentConnectorGrant? Grant, ArtifactEditSession? Session, IResult? Error);
    private sealed record ManagedDocumentSummary(Guid Id, string DocumentNumber, string Acronym, string DocumentType, string Title, string OwnerId, string ReleasedRevision, string ReleasedState, string? InWorkRevision, string InWorkState, string? CheckedOutBy, DateTimeOffset? CheckoutExpiresAt, bool ReconciliationRequired, DateTimeOffset UpdatedAt);
}

public sealed record CreateManagedDocumentRequest(Guid ProjectId, string Acronym, string DocumentType, string Title, string? OwnerId, string? FormalChangeSummary, string? ChangeSummary = null);
public sealed record StartManagedDocumentRevisionRequest(string? FormalChangeSummary, string? ChangeSummary = null, string? OwnerId = null);
public sealed record SubmitManagedDocumentRequest(string TechnicalReviewerId, string FinalApproverId);
public sealed record ReviseManagedDocumentFormalSummaryRequest(string FormalChangeSummary, string Reason, long ExpectedVersion);
public sealed record DocumentReviewDecisionRequest(string Password, string Meaning, string Rationale);
public sealed record ForceUnlockManagedDocumentRequest(string Reason);
public sealed record ManagedDocumentLinkRequest(Guid RevisionId, string ArtifactType, Guid ArtifactId, string DisplayNumber, string Relationship);
public sealed record ConnectorVersionRequest(long ExpectedVersion);
public sealed record ConnectorDiscardRequest(long ExpectedVersion, string? Reason);
