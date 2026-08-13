using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Api;
using AeroLink.ConnectorProtocol;
using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
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
        group.MapPost("/connector-enrollment", ConnectorEnrollmentAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/attachments/{attachmentId:guid}", DownloadAttachmentAsync);
        group.MapPost("/attachments/{attachmentId:guid}/restore", RestoreAttachmentAsync);
        group.MapPost("/projects/{projectId:guid}/integrity/scan", ScanIntegrityAsync);
        group.MapPost("/projects/{projectId:guid}/storage/reconcile", ReconcileStorageAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapPost("/{id:guid}/revisions", StartRevisionAsync);
        group.MapPost("/{id:guid}/links", AddLinkAsync);
        group.MapPatch("/{id:guid}/links/{linkId:guid}", CorrectLinkAsync);
        group.MapPost("/{id:guid}/links/{linkId:guid}/supersede", SupersedeLinkAsync);
        group.MapPost("/revisions/{revisionId:guid}/checkout", CheckoutAsync);
        group.MapPost("/revisions/{revisionId:guid}/recovery", RecoverCheckoutAsync);
        group.MapPost("/revisions/{revisionId:guid}/recovery/discard", DiscardRecoveryAsync);
        group.MapPost("/revisions/{revisionId:guid}/submit", SubmitAsync);
        group.MapPatch("/revisions/{revisionId:guid}/formal-summary", ReviseFormalSummaryAsync);
        group.MapPatch("/{id:guid}/steward", ReassignStewardAsync);
        group.MapPatch("/revisions/{revisionId:guid}/responsible-owner", ReassignResponsibleOwnerAsync);
        group.MapPost("/revisions/{revisionId:guid}/review/approve", ApproveAsync);
        group.MapPost("/revisions/{revisionId:guid}/review/return", ReturnAsync);
        group.MapPost("/revisions/{revisionId:guid}/release-preparation", PrepareReleaseAsync);
        group.MapPost("/revisions/{revisionId:guid}/force-unlock", ForceUnlockAsync);
        group.MapPost("/revisions/{revisionId:guid}/withdraw", WithdrawAsync);

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

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct)) return Results.Forbid();
        var revisions = (await db.ManagedDocumentRevisions.AsNoTracking().Where(x => x.DocumentId == id).Include(x => x.ReviewSteps).ToListAsync(ct)).OrderByDescending(x => x.Revision).ToList();
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var attachments = await db.ControlledAttachments.AsNoTracking().Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == id).ToListAsync(ct);
        var integritySignals = await db.OperationalAlerts.AsNoTracking().Where(x => x.ProjectId == document.ProjectId
            && x.State != OperationalAlertState.Resolved && x.Signal.StartsWith("managed-document-integrity:"))
            .Select(x => new { x.Signal, x.Detail, x.OpenedAt }).ToListAsync(ct);
        var integrityFailures = integritySignals.Select(x => new
        {
            AttachmentId = Guid.TryParseExact(x.Signal["managed-document-integrity:".Length..], "N", out var parsed) ? parsed : Guid.Empty,
            x.Detail,
            x.OpenedAt
        }).Where(x => x.AttachmentId != Guid.Empty).ToList();
        var links = (await db.ManagedDocumentLinks.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct)).OrderBy(x => x.CreatedAt).ToList();
        var buildProvenance = await (from provenance in db.ManagedDocumentBuildProvenance.AsNoTracking() join release in db.Releases.AsNoTracking() on provenance.ReleaseId equals release.Id where provenance.DocumentId == id select new { provenance.ReleaseId, release.Version, provenance.RevisionId, provenance.Source, provenance.RecordedBy, provenance.RecordedAt, meaning = "Legacy build association retained as provenance only" }).ToListAsync(ct);
        var active = (await ActiveSessionsAsync(db, [id], ct)).SingleOrDefault();
        var audits = (await db.ManagedDocumentEvents.AsNoTracking().Where(x => x.DocumentId == id).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList();
        var signatures = (await db.ElectronicSignatures.AsNoTracking().Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == id).ToListAsync(ct)).OrderBy(x => x.SignedAt).ToList();
        var checkIns = (await db.ManagedDocumentCheckIns.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList();
        var contributors = await db.ManagedDocumentReviewContributors.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        var assignments = (await db.ManagedDocumentAssignments.AsNoTracking().Where(x => x.DocumentId == id).ToListAsync(ct)).OrderBy(x => x.EffectiveAt).ToList();
        var actor = http.UserAccount();
        var canCorrectFormalScopeByRole = await http.HasProjectRoleAsync(db, identity, document.ProjectId, ct,
            ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead);
        var canManageRelationshipByRole = await ManagedDocumentAssignmentPolicy.HasExplicitAuthorityAsync(db, document.ProjectId, actor, DateTimeOffset.UtcNow, ct,
            ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead);
        var activeResponsibleOwner = await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, document.ProjectId, actor.UserName, DateTimeOffset.UtcNow, ct);
        return Results.Ok(new
        {
            document.Id, document.ProjectId, document.DocumentNumber, document.Acronym, document.DocumentType, document.Title, document.OwnerId, document.StewardId, document.CreatedBy, canReassignSteward = canCorrectFormalScopeByRole, document.CreatedAt, document.UpdatedAt, document.Version,
            lockInfo = active is null ? null : new { active.Id, active.UserName, active.OpenedAt, active.UpdatedAt, active.ExpiresAt }, buildProvenance,
            revisions = revisions.Select(revision => new
            {
                revision.Id, revision.Revision, revision.ParentRevisionId, revision.ParentReleasedDocxAttachmentId, revision.ParentReleasedDocxSha256, revision.TransformationProfile, displayNumber = $"{document.DocumentNumber}.{revision.Revision:D2}", revision.OwnerId, revision.ResponsibleOwnerId, revision.InitiatedBy, revision.FormalChangeSummary, revision.FormalSummaryHash, revision.FormalSummaryVersion, revision.FormalSummaryProvenance, canReviseFormalSummary = revision.State is ManagedDocumentState.Draft or ManagedDocumentState.Returned && (string.Equals(revision.ResponsibleOwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase) || canCorrectFormalScopeByRole), canReassignResponsibleOwner = revision.State is ManagedDocumentState.Draft or ManagedDocumentState.Returned && canCorrectFormalScopeByRole, canManageRelationships = revision.State is ManagedDocumentState.Draft or ManagedDocumentState.Returned && ((activeResponsibleOwner && string.Equals(revision.ResponsibleOwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase)) || canManageRelationshipByRole), state = revision.State.ToString(), revision.CurrentWorkingAttachmentId, revision.ReleaseCandidateDocxAttachmentId, revision.ReleaseCandidatePdfAttachmentId, revision.ReleasedDocxAttachmentId, revision.ReleasedPdfAttachmentId, revision.SnapshotHash, revision.CurrentReviewCycle, revision.SubmittedFormalSummaryHash, revision.SubmittedFormalSummaryVersion, revision.SubmittedRelationshipManifest, revision.SubmittedRelationshipManifestHash, revision.RelationshipManifestVersion, revision.ReleaseManifestHash, revision.ReturnReason, revision.SubmittedBy, revision.SubmittedAt, revision.ReleasedBy, revision.ReleasedAt, revision.CreatedAt, revision.UpdatedAt, revision.Version,
                integrityBlocked = integrityFailures.Any(failure => attachments.Any(attachment => attachment.RevisionId == revision.Id && attachment.Id == failure.AttachmentId)),
                integrityFailures = integrityFailures.Where(failure => attachments.Any(attachment => attachment.RevisionId == revision.Id && attachment.Id == failure.AttachmentId)),
                currentRelationshipManifestHash = ManagedDocumentRelationshipPolicy.Manifest(links.Where(x => x.RevisionId == revision.Id).ToList()).Hash,
                reviewSteps = revision.ReviewSteps.OrderBy(x => x.Cycle).ThenBy(x => x.Position).Select(x => new { x.Id, x.Cycle, x.Position, x.StageName, x.ApproverId, x.ApproverName, x.RequiredAuthority, x.GrantedAuthority, x.AuthoritySource, x.AuthoritySourceId, x.WorkflowId, x.WorkflowName, x.WorkflowVersion, x.AuthorityPolicy, x.AssignedAt, x.Version, state = x.State.ToString(), x.Rationale, x.DecidedAt }),
                attachments = attachments.Where(x => x.RevisionId == revision.Id).OrderByDescending(x => x.UploadedAt).Select(Attachment),
                links = links.Where(x => x.RevisionId == revision.Id).Select(x => new { x.Id, x.ArtifactType, x.ArtifactId, x.DisplayNumber, x.CanonicalTitle, x.TargetState, x.TargetProjectId, x.TargetReleaseId, x.TargetReleaseVersion, x.DeepLink, x.Relationship, x.PolicyVersion, x.Provenance, x.IsCurrent, x.SupersededByLinkId, x.SupersedeReason, x.SupersededBy, x.SupersededAt, x.CreatedBy, x.CreatedAt })
                , checkIns = checkIns.Where(x => x.RevisionId == revision.Id).Select(x => new { x.Id, x.WorkingAttachmentId, x.WorkingVersion, x.ActorId, x.Comment, x.BaseAttachmentId, x.BaseSha256, x.ResultSha256, x.SupersededAttachmentId, x.ConnectorSessionId, x.OperationId, x.OccurredAt, x.ReturnResolutionNote })
                , reviewContributors = contributors.Where(x => x.RevisionId == revision.Id).Select(x => new { x.Id, x.ReviewCycle, x.ContributorId, x.EvidenceHash, x.CapturedAt, x.Provenance })
            }),
            assignments = assignments.Select(x => new { x.Id, x.RevisionId, x.AssignmentType, x.PriorAssigneeId, x.NewAssigneeId, x.AssignedBy, x.Reason, x.EffectiveAt }),
            signatures = signatures.Select(x => new { x.Id, x.UserName, x.DisplayName, x.ArtifactRevision, x.Action, x.Authority, x.AuthoritySource, x.AuthoritySourceId, x.WorkflowId, x.WorkflowVersion, x.ReviewStepId, x.ReviewCycle, x.ReviewStepPosition, x.Meaning, x.Rationale, x.ContentHash, x.SignedAt, isLegacyAuthority = string.IsNullOrWhiteSpace(x.Authority) }),
            audit = audits.Select(x => new { x.Id, x.EventType, x.ActorId, x.Detail, x.OccurredAt })
        });
    }

    private static async Task<IResult> CreateAsync(CreateManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentFileService files, ManagedDocumentStorageCoordinator storage, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        var operationError = ValidateOperationKey(request.OperationKey); if (operationError is not null) return operationError;
        var actor = http.UserAccount(); var ownerId = request.OwnerId ?? actor.UserName;
        if (!await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, request.ProjectId, ownerId, DateTimeOffset.UtcNow, ct)) return Results.BadRequest(new { error = actor.IsAdministrator && request.OwnerId is null ? "Select an active authorized Program author as document steward and responsible owner; administrator status is not document-authoring authority." : "The document steward and responsible owner must be an active authorized member or delegate in this Program." });
        ManagedDocumentStorageOperation? operation = null;
        try
        {
            var acronym = request.Acronym.Trim().ToUpperInvariant(); if (acronym.Length is < 2 or > 12 || acronym.Any(c => c is < 'A' or > 'Z')) return Results.BadRequest(new { error = "Use a 2-12 letter document acronym." });
            var numbers = await db.ManagedDocuments.Where(x => x.ProjectId == request.ProjectId && x.Acronym == acronym).Select(x => x.DocumentNumber).ToListAsync(ct); var next = numbers.Select(NumberSequence).DefaultIfEmpty(0).Max() + 1;
            var now = DateTimeOffset.UtcNow;
            var document = new ManagedDocument(request.ProjectId, $"{acronym}-{next:D6}", acronym, request.DocumentType, request.Title, ownerId, now, actor.UserName);
            var revision = new ManagedDocumentRevision(document.Id, 0, ownerId, request.FormalChangeSummary ?? request.ChangeSummary ?? "Initial controlled draft.", now, initiatedBy: actor.UserName);
            var context = await ProjectContextAsync(db, request.ProjectId, ct);
            var output = ProfessionalPublicationRenderer.Render(NewDraftPublication(document, revision, context.Project, context.Program), "docx", $"{document.DocumentNumber}.00");
            var payloadHash = OperationPayloadHash("DocumentCreate", new { request.ProjectId, acronym, request.DocumentType, request.Title, ownerId, formalSummary = revision.FormalChangeSummary });
            var started = await storage.BeginAsync(request.ProjectId, document.Id, revision.Id, "DocumentCreate",
                request.OperationKey!, payloadHash, actor.UserName, now, ct);
            operation = started.Operation; if (started.ExistingResult is not null) return Results.Content(started.ExistingResult, "application/json", statusCode: StatusCodes.Status201Created);
            var staged = await files.StageAsync(operation.Id, "working-docx", document.ProjectId, document.Id, revision.Id, revision.Id, 1,
                "Working Word document", "Initial AeroLink draft template.", output.FileName, output.ContentType, output.Content, null, actor.UserName, now, ct);
            await storage.CheckpointAsync(operation, "object-staged-1", ct);
            var resultJson = JsonSerializer.Serialize(new { id = document.Id, documentNumber = document.DocumentNumber, revisionId = revision.Id });
            await storage.RecordPlanAsync(operation, [StorageObject("working-docx", staged.Attachment, staged.Staged)], resultJson, now, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            db.ManagedDocuments.Add(document); db.ManagedDocumentRevisions.Add(revision);
            var attachment = staged.Attachment;
            db.ControlledAttachments.Add(attachment); revision.RecordCheckIn(attachment.Id, now);
            db.ManagedDocumentCheckIns.Add(new(revision.Id, attachment.Id, 1, actor.UserName, "Created the initial controlled Word template.", null, null, attachment.Sha256, null, null, $"document-create:{document.Id}", now));
            db.ManagedDocumentEvents.Add(new(document.Id, "DocumentCreated", actor.UserName, $"Created {document.DocumentNumber}.00 as a Project-wide Draft.", now));
            await storage.PromoteAsync(operation, [staged.Staged], ct); await db.SaveChangesAsync(ct); await storage.CheckpointAsync(operation, "metadata-saved", ct); await transaction.CommitAsync(ct);
            await storage.CompleteAsync(operation, now, ct); return Results.Content(resultJson, "application/json", statusCode: StatusCodes.Status201Created);
        }
        catch (ManagedDocumentStorageConflictException ex) { return Results.Conflict(new { error = ex.Message, code = ex.Code }); }
        catch (DomainException ex)
        { if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, ex.Message, actor.UserName); return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException)
        { if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, "The document metadata transaction failed.", actor.UserName); return Results.Conflict(new { error = "A document number was allocated concurrently. Retry the create request with a new operation key." }); }
        catch
        { if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, "The document create request failed before atomic completion.", actor.UserName); throw; }
    }

    private static async Task<IResult> StartRevisionAsync(Guid id, StartManagedDocumentRevisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentFileService files, ManagedDocumentIntegrityService integrity, ManagedDocumentStorageCoordinator storage, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, document.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        var actor = http.UserAccount(); var ownerId = request.OwnerId ?? actor.UserName;
        if (!await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, document.ProjectId, ownerId, DateTimeOffset.UtcNow, ct)) return Results.BadRequest(new { error = actor.IsAdministrator && request.OwnerId is null ? "Select an active authorized Program author as responsible revision owner; administrator status is not document-authoring authority." : "The responsible revision owner must be an active authorized member or delegate in this Program." });
        if (await db.ManagedDocumentRevisions.AnyAsync(x => x.DocumentId == id && (x.State == ManagedDocumentState.Draft || x.State == ManagedDocumentState.InReview || x.State == ManagedDocumentState.Returned), ct)) return Results.Conflict(new { error = "Complete or withdraw the existing in-work revision before starting another." });
        var released = await db.ManagedDocumentRevisions.Where(x => x.DocumentId == id && x.State == ManagedDocumentState.Released).ToListAsync(ct);
        if (released.Count != 1) return Results.Conflict(new { error = released.Count == 0 ? "Release the initial revision before starting a successor." : "This document has multiple released heads and requires controlled lineage reconciliation.", code = "document_lineage_reconciliation_required" });
        var prior = released[0];
        if (prior.ReleasedDocxAttachmentId is null) return Results.Conflict(new { error = "The released head has no immutable released DOCX source.", code = "document_lineage_reconciliation_required" });
        var priorAttachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == prior.ReleasedDocxAttachmentId && x.RevisionId == prior.Id, ct);
        if (priorAttachment is null) return Results.Conflict(new { error = "The immutable released parent DOCX metadata is missing.", code = "document_lineage_reconciliation_required" });
        FileStream verifiedParent;
        try { verifiedParent = await integrity.OpenVerifiedAsync(priorAttachment, actor.UserName, ct); }
        catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
        await using var input = verifiedParent;
        var now = DateTimeOffset.UtcNow; var revision = new ManagedDocumentRevision(id, prior.Revision + 1, ownerId, request.FormalChangeSummary ?? request.ChangeSummary ?? "", now, prior.Id, priorAttachment.Id, priorAttachment.Sha256, ManagedDocumentFileService.SuccessorTransformationProfile, actor.UserName);
        using var copy = new MemoryStream(); await input.CopyToAsync(copy, ct);
        byte[] nextDraft;
        try { nextDraft = ManagedDocumentFileService.PrepareNextRevisionDraft(copy.ToArray(), document.DocumentNumber, prior.Revision, revision.Revision); }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "document_parent_transform_failure" }); }
        ManagedDocumentStorageOperation? operation = null;
        try
        {
            var payloadHash = OperationPayloadHash("RevisionStart", new { documentId = id, priorRevisionId = prior.Id,
                priorAttachmentId = priorAttachment.Id, priorAttachment.Sha256, ownerId, revision.FormalChangeSummary });
            var started = await storage.BeginAsync(document.ProjectId, id, revision.Id, "RevisionStart",
                request.OperationKey ?? $"revision-start:{prior.Id:N}", payloadHash, actor.UserName, now, ct);
            operation = started.Operation; if (started.ExistingResult is not null) return Results.Content(started.ExistingResult, "application/json", statusCode: StatusCodes.Status201Created);
            var staged = await files.StageAsync(operation.Id, "working-docx", document.ProjectId, id, revision.Id, revision.Id, 1,
                "Working Word document", "Draft source copied from the last released revision.", $"{document.DocumentNumber}.{revision.Revision:D2}.docx",
                ManagedDocumentFileService.DocxContentType, nextDraft, null, actor.UserName, now, ct);
            await storage.CheckpointAsync(operation, "object-staged-1", ct);
            var resultJson = JsonSerializer.Serialize(new { id = revision.Id, revision = revision.Revision });
            await storage.RecordPlanAsync(operation, [StorageObject("working-docx", staged.Attachment, staged.Staged)], resultJson, now, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            if (await db.ManagedDocumentRevisions.AnyAsync(x => x.DocumentId == id && (x.State == ManagedDocumentState.Draft || x.State == ManagedDocumentState.InReview || x.State == ManagedDocumentState.Returned), ct))
                throw new ManagedDocumentStorageConflictException("document_successor_conflict", "Complete or withdraw the existing in-work revision before starting another.");
            if (!await db.ManagedDocumentRevisions.AnyAsync(x => x.Id == prior.Id && x.State == ManagedDocumentState.Released && x.ReleasedDocxAttachmentId == priorAttachment.Id, ct))
                throw new ManagedDocumentStorageConflictException("document_parent_changed", "The released parent changed while it was being verified. Refresh and retry.");
            db.ManagedDocumentRevisions.Add(revision); var attachment = staged.Attachment;
            db.ControlledAttachments.Add(attachment); revision.RecordCheckIn(attachment.Id, now);
            db.ManagedDocumentCheckIns.Add(new(revision.Id, attachment.Id, 1, actor.UserName, "Created the successor Draft from the verified released parent DOCX.", priorAttachment.Id, priorAttachment.Sha256, attachment.Sha256, null, null, $"revision-start:{revision.Id}", now));
            db.ManagedDocumentEvents.Add(new(id, "DocumentRevisionStarted", actor.UserName, $"Started {document.DocumentNumber}.{revision.Revision:D2} from verified released parent {document.DocumentNumber}.{prior.Revision:D2} DOCX {priorAttachment.Sha256} using {ManagedDocumentFileService.SuccessorTransformationProfile}.", now));
            await storage.PromoteAsync(operation, [staged.Staged], ct); await db.SaveChangesAsync(ct); await storage.CheckpointAsync(operation, "metadata-saved", ct); await transaction.CommitAsync(ct);
            await storage.CompleteAsync(operation, now, ct); return Results.Content(resultJson, "application/json", statusCode: StatusCodes.Status201Created);
        }
        catch (ManagedDocumentStorageConflictException ex)
        {
            if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, ex.Message, actor.UserName);
            return Results.Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (DbUpdateException)
        {
            if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, "A concurrent successor won the metadata transaction.", actor.UserName);
            return Results.Conflict(new { error = "Another request started the active successor revision first. Refresh the document.", code = "document_successor_conflict" });
        }
        catch { if (operation is not null) await RollBackStorageAsync(db, storage, operation.Id, "The successor operation failed before atomic completion.", actor.UserName); throw; }
    }

    private static async Task<IResult> CheckoutAsync(Guid revisionId, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity, ConnectorSigningService signing, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        if (data.Revision.State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) return Results.Conflict(new { error = "Only a Draft or returned revision can be checked out." });
        var actor = http.UserAccount(); if (actor.UserName != data.Revision.ResponsibleOwnerId && !actor.IsAdministrator) return Results.Forbid();
        return await CreateGrantAsync(data, "edit", actor.UserName, http, db, integrity, signing, ct);
    }

    private static async Task<IResult> PrepareReleaseAsync(Guid revisionId, HttpContext http, AeroLinkDbContext db, ManagedDocumentIntegrityService integrity, ConnectorSigningService signing, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var active = data.Revision.ReviewSteps.SingleOrDefault(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active);
        var finalPosition = data.Revision.ReviewSteps.Where(x => x.Cycle == data.Revision.CurrentReviewCycle).Select(x => x.Position).DefaultIfEmpty(-1).Max(); var actor = http.UserAccount();
        if (data.Revision.State != ManagedDocumentState.InReview || active is null || active.Position != finalPosition || active.ApproverId != actor.UserName) return Results.Conflict(new { error = "Release preparation is available only to the active final approver after technical review." });
        return await CreateGrantAsync(data, "release", actor.UserName, http, db, integrity, signing, ct);
    }

    private static async Task<IResult> CreateGrantAsync(RevisionData data, string mode, string actor, HttpContext http, AeroLinkDbContext db, ManagedDocumentIntegrityService integrity, ConnectorSigningService signing, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var sessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == data.Document.Id && x.IsExclusive && x.State == EditSessionState.Active).ToListAsync(ct);
        foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now); var active = sessions.FirstOrDefault(x => x.State == EditSessionState.Active);
        if (active is not null) return Results.Conflict(new { error = active.UserName == actor ? "You already have this document open in the desktop connector." : $"{active.UserName} has this document checked out.", code = "exclusive_lock", holder = active.UserName, active.ExpiresAt });
        var attachment = data.Revision.CurrentWorkingAttachmentId is null ? null : await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct); if (attachment is null) return Results.Conflict(new { error = "Check in a controlled Word working copy before opening the connector." });
        try { await integrity.VerifyAsync(attachment, actor, ct); }
        catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
        var session = new ArtifactEditSession(data.Document.ProjectId, "ManagedDocument", data.Document.Id, data.Revision.Id, attachment.Sha256, "{}", actor, now, true, 15);
        var origin = signing.ResolveOrigin(http);
        var launchToken = Token(); var revisionNumber = $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}";
        var grant = new DocumentConnectorGrant(data.Document.ProjectId, data.Document.Id, data.Revision.Id, session.Id, actor, mode,
            Hash(launchToken), signing.DeploymentId, origin, signing.KeyId, attachment.Id, attachment.Size, attachment.Sha256,
            data.Document.DocumentNumber, revisionNumber, now);
        db.ArtifactEditSessions.Add(session); db.DocumentConnectorGrants.Add(grant); db.ManagedDocumentEvents.Add(new(data.Document.Id, mode == "edit" ? "DocumentCheckedOut" : "ReleasePreparationOpened", actor, mode == "edit" ? $"Checked out {data.Document.DocumentNumber}.{data.Revision.Revision:D2} for exclusive editing." : $"Opened exact release-candidate preparation for {data.Document.DocumentNumber}.{data.Revision.Revision:D2}.", now)); await db.SaveChangesAsync(ct);
        var envelope = new ConnectorLaunchEnvelope(ConnectorLaunchProtocol.Version, ConnectorLaunchProtocol.ProfileVersion,
            signing.DeploymentId, origin, signing.KeyId, launchToken, grant.ExpiresAt, data.Document.ProjectId,
            data.Document.Id, data.Document.DocumentNumber, data.Revision.Id, revisionNumber, mode, attachment.Id,
            attachment.Size, attachment.Sha256, session.Id);
        var launchUri = $"aerolink://document/{mode}?envelope={Uri.EscapeDataString(signing.Sign(envelope))}";
        return Results.Ok(new { grantId = grant.Id, sessionId = session.Id, session.ExpiresAt, launchUri, mode, holder = actor });
    }

    private static async Task<IResult> RecoverCheckoutAsync(Guid revisionId, RecoverDocumentConnectorRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity,
        ConnectorSigningService signing, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount();
        var original = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.Id == request.WorkspaceId
            && x.ProjectId == data.Document.ProjectId && x.DocumentId == data.Document.Id && x.RevisionId == revisionId, ct);
        if (original is null || original.SourceAttachmentId is null || original.SourceSize is null || original.SourceSha256 is null
            || original.DeploymentId is null || original.KeyId is null || original.DocumentNumber is null || original.RevisionNumber is null)
            return Results.NotFound();
        if (original.Mode == "edit" && !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct,
            ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        if (original.Mode == "edit" && !actor.IsAdministrator
            && (!string.Equals(actor.UserName, original.UserName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actor.UserName, data.Revision.ResponsibleOwnerId, StringComparison.OrdinalIgnoreCase))) return Results.Forbid();
        if (original.Mode == "release" && !string.Equals(actor.UserName, original.UserName, StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();

        var recoveryGrants = await db.DocumentConnectorGrants.Where(x => x.RecoveryWorkspaceId == original.Id).ToListAsync(ct);
        var latestRecovery = recoveryGrants.OrderByDescending(x => x.CreatedAt).FirstOrDefault(); var basis = latestRecovery ?? original;
        var now = DateTimeOffset.UtcNow;
        if (latestRecovery is { RedeemedAt: null, RevokedAt: null } && latestRecovery.ExpiresAt > now)
            return Results.Conflict(new { error = "A recovery launch was already issued for this workspace. Open it or retry after it expires.", code = "document_recovery_already_issued" });
        var priorSession = await db.ArtifactEditSessions.SingleAsync(x => x.Id == basis.EditSessionId, ct);
        if (priorSession.State == EditSessionState.Active && priorSession.ExpiresAt <= now) priorSession.Expire(now);
        if (priorSession.State == EditSessionState.Committed)
        {
            var operationKey = basis.Mode == "release" ? $"connector-release-candidate:{basis.Id}" : $"connector-check-in:{basis.Id}";
            var operation = await db.ManagedDocumentOperations.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revisionId && x.OperationKey == operationKey, ct);
            if (operation is null) return Results.Conflict(new { error = "The server completed this connector session without recoverable completion evidence. Preserve and export the local copy.", code = "document_recovery_completion_unknown" });
            return Results.Ok(new { status = "completed", launchUri = RecoveryCommand(signing, http, original, priorSession.Id, "cleanup", operation.ResultJson, now) });
        }
        if (priorSession.State == EditSessionState.Abandoned)
            return Results.Ok(new { status = "discarded", launchUri = RecoveryCommand(signing, http, original, priorSession.Id, "discard", null, now) });
        if (priorSession.State == EditSessionState.Conflict)
            return Results.Conflict(new { error = "The prior checkout is in conflict. Preserve or export the local copy; it cannot be uploaded into this revision.", code = "document_recovery_conflict" });

        if (original.Mode == "edit")
        {
            if (data.Revision.State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned))
                return Results.Conflict(new { error = "The formal revision is no longer Draft or Returned. Preserve or export the local copy.", code = "document_recovery_revision_advanced" });
        }
        else
        {
            var active = data.Revision.ReviewSteps.SingleOrDefault(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active);
            var final = data.Revision.ReviewSteps.Where(x => x.Cycle == data.Revision.CurrentReviewCycle).Select(x => x.Position).DefaultIfEmpty(-1).Max();
            if (data.Revision.State != ManagedDocumentState.InReview || active is null || active.Position != final || active.ApproverId != actor.UserName)
                return Results.Conflict(new { error = "Release preparation is no longer at the same authorized final-review stage. Preserve the candidate files.", code = "document_recovery_revision_advanced" });
        }
        if (data.Revision.CurrentWorkingAttachmentId != original.SourceAttachmentId)
            return Results.Conflict(new { error = "The controlled source changed after this workspace was created. Preserve or export the local copy.", code = "document_recovery_source_changed" });
        var source = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == original.SourceAttachmentId
            && x.ProjectId == original.ProjectId && x.ArtifactId == original.DocumentId && x.RevisionId == original.RevisionId, ct);
        if (source is null || source.Size != original.SourceSize || !string.Equals(source.Sha256, original.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "The exact base attachment is no longer available. Preserve or export the local copy.", code = "document_recovery_source_changed" });
        try { await integrity.VerifyAsync(source, actor.UserName, ct); }
        catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }

        var activeSessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == data.Document.Id
            && x.State == EditSessionState.Active).ToListAsync(ct);
        foreach (var expired in activeSessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now);
        var activeOther = activeSessions.FirstOrDefault(x => x.State == EditSessionState.Active && x.Id != priorSession.Id);
        if (activeOther is not null) return Results.Conflict(new { error = $"{activeOther.UserName} now holds the document checkout. Preserve or export this local copy.", code = "document_recovery_lock_conflict" });
        ArtifactEditSession session;
        if (priorSession.State == EditSessionState.Active)
        {
            priorSession.Heartbeat(priorSession.Version, now, 15); session = priorSession;
        }
        else
        {
            session = new ArtifactEditSession(data.Document.ProjectId, "ManagedDocument", data.Document.Id, data.Revision.Id,
                source.Sha256, "{}", actor.UserName, now, true, 15); db.ArtifactEditSessions.Add(session);
        }
        var priorGrants = await db.DocumentConnectorGrants.Where(x => x.EditSessionId == priorSession.Id && x.RevokedAt == null).ToListAsync(ct);
        foreach (var prior in priorGrants) prior.Revoke(now);
        var origin = signing.ResolveOrigin(http); var nonce = Token();
        var grant = new DocumentConnectorGrant(data.Document.ProjectId, data.Document.Id, data.Revision.Id, session.Id,
            actor.UserName, original.Mode, Hash(nonce), signing.DeploymentId, origin, signing.KeyId, source.Id, source.Size,
            source.Sha256, data.Document.DocumentNumber, original.RevisionNumber, now, original.Id);
        db.DocumentConnectorGrants.Add(grant);
        db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentCheckoutRecovered", actor.UserName,
            $"Reauthenticated and recovered local workspace {original.Id} for {original.RevisionNumber} from exact base {source.Sha256}.", now));
        db.SecurityAuditEvents.Add(new("DocumentCheckoutRecovered", actor.UserName, original.Id.ToString(), "Success",
            $"Recovered {original.RevisionNumber} using a new scoped connector grant.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "Another checkout or recovery won the exclusive lock.", code = "document_recovery_lock_conflict" }); }
        var envelope = new ConnectorLaunchEnvelope(ConnectorLaunchProtocol.Version, ConnectorLaunchProtocol.ProfileVersion,
            signing.DeploymentId, origin, signing.KeyId, nonce, grant.ExpiresAt, data.Document.ProjectId, data.Document.Id,
            data.Document.DocumentNumber, data.Revision.Id, original.RevisionNumber, original.Mode, source.Id, source.Size,
            source.Sha256, session.Id, original.Id);
        return Results.Ok(new { status = "recoverable", grantId = grant.Id, sessionId = session.Id, session.ExpiresAt,
            launchUri = $"aerolink://document/{original.Mode}?envelope={Uri.EscapeDataString(signing.Sign(envelope))}" });
    }

    private static async Task<IResult> DiscardRecoveryAsync(Guid revisionId, RecoverDocumentConnectorRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, ConnectorSigningService signing, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount(); var original = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.Id == request.WorkspaceId
            && x.ProjectId == data.Document.ProjectId && x.DocumentId == data.Document.Id && x.RevisionId == revisionId, ct);
        if (original is null || original.SourceAttachmentId is null || original.SourceSize is null || original.SourceSha256 is null
            || original.DeploymentId is null || original.KeyId is null || original.DocumentNumber is null || original.RevisionNumber is null) return Results.NotFound();
        if (!actor.IsAdministrator && !string.Equals(actor.UserName, original.UserName, StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var recoveryGrants = await db.DocumentConnectorGrants.Where(x => x.RecoveryWorkspaceId == original.Id).ToListAsync(ct);
        var latestRecovery = recoveryGrants.OrderByDescending(x => x.CreatedAt).FirstOrDefault(); var basis = latestRecovery ?? original;
        var now = DateTimeOffset.UtcNow; var session = await db.ArtifactEditSessions.SingleAsync(x => x.Id == basis.EditSessionId, ct);
        if (session.State == EditSessionState.Committed)
        {
            var operationKey = basis.Mode == "release" ? $"connector-release-candidate:{basis.Id}" : $"connector-check-in:{basis.Id}";
            var operation = await db.ManagedDocumentOperations.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revisionId && x.OperationKey == operationKey, ct);
            if (operation is null) return Results.Conflict(new { error = "Completion evidence is unavailable. Preserve and export the local copy.", code = "document_recovery_completion_unknown" });
            return Results.Ok(new { status = "completed", launchUri = RecoveryCommand(signing, http, original, session.Id, "cleanup", operation.ResultJson, now) });
        }
        if (session.State == EditSessionState.Active)
        {
            if (session.ExpiresAt <= now) session.Expire(now); else session.Close(EditSessionState.Abandoned, session.Version, now, actor.UserName, "User discarded the retained local connector workspace after browser reauthentication.");
        }
        var grants = await db.DocumentConnectorGrants.Where(x => x.EditSessionId == session.Id && x.RevokedAt == null).ToListAsync(ct); foreach (var grant in grants) grant.Revoke(now);
        db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentRecoveryDiscarded", actor.UserName, $"Authorized local discard of workspace {original.Id} for {original.RevisionNumber}.", now));
        db.SecurityAuditEvents.Add(new("DocumentRecoveryDiscarded", actor.UserName, original.Id.ToString(), "Success",
            $"Authorized connector cleanup for {original.RevisionNumber}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = "discarded", launchUri = RecoveryCommand(signing, http, original, session.Id, "discard", null, now) });
    }

    private static string RecoveryCommand(ConnectorSigningService signing, HttpContext http, DocumentConnectorGrant original,
        Guid editSessionId, string mode, string? completionEvidenceJson, DateTimeOffset now)
    {
        var origin = signing.ResolveOrigin(http); var envelope = new ConnectorLaunchEnvelope(ConnectorLaunchProtocol.Version,
            ConnectorLaunchProtocol.ProfileVersion, signing.DeploymentId, origin, signing.KeyId, Token(), now.AddMinutes(5),
            original.ProjectId, original.DocumentId, original.DocumentNumber!, original.RevisionId, original.RevisionNumber!, mode,
            original.SourceAttachmentId!.Value, original.SourceSize!.Value, original.SourceSha256!, editSessionId, original.Id,
            completionEvidenceJson);
        return $"aerolink://document/{mode}?envelope={Uri.EscapeDataString(signing.Sign(envelope))}";
    }

    private static async Task<IResult> SubmitAsync(Guid revisionId, SubmitManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        var operationError = ValidateOperationKey(request.OperationKey); if (operationError is not null) return operationError;
        var payloadHash = OperationPayloadHash($"Submit:{http.UserAccount().UserName}", request);
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount(); if (actor.UserName != data.Revision.ResponsibleOwnerId && !actor.IsAdministrator) return Results.Forbid();
        var priorOperation = await OperationResultAsync(db, revisionId, "Submit", request.OperationKey, payloadHash, ct); if (priorOperation is not null) return priorOperation;
        if (data.Revision.Version != request.ExpectedVersion || data.Revision.CurrentWorkingAttachmentId != request.ExpectedWorkingAttachmentId
            || data.Revision.FormalSummaryVersion != request.ExpectedFormalSummaryVersion
            || !string.Equals(data.Revision.FormalSummaryHash, request.ExpectedFormalSummaryHash, StringComparison.OrdinalIgnoreCase))
            return ReviewConflict("submission_evidence_changed", "The document changed after this page loaded. Refresh and review the exact working evidence before submitting.", data.Revision);
        if (await db.ArtifactEditSessions.AnyAsync(x => x.ArtifactId == data.Document.Id && x.ArtifactType == "ManagedDocument" && x.State == EditSessionState.Active, ct)) return Results.Conflict(new { error = "Check in or discard the active desktop checkout before submitting." });
        var accounts = await db.UserAccounts.AsNoTracking().Where(x => (x.UserName == request.TechnicalReviewerId || x.UserName == request.FinalApproverId) && x.State == AccountState.Active).ToListAsync(ct); if (accounts.Count != 2) return Results.BadRequest(new { error = "Select two active AeroLink users for document review." });
        var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
        var now = DateTimeOffset.UtcNow; var technicalAccount = accounts.Single(x => x.UserName == request.TechnicalReviewerId); var finalAccount = accounts.Single(x => x.UserName == request.FinalApproverId);
        var technicalAuthority = await ManagedDocumentReviewAuthority.ResolveTechnicalAsync(db, programId, technicalAccount, now, ct);
        var finalAuthority = await ManagedDocumentReviewAuthority.ResolveFinalAsync(db, programId, finalAccount, now, ct);
        if (technicalAuthority is null) return Results.BadRequest(new { error = "The technical reviewer needs current review or engineering-lead authority in this Program." });
        if (finalAuthority is null) return Results.BadRequest(new { error = "The final approver needs current SQA, configuration, approval, Program, or authorized administrator-substitution authority." });
        var acceptedCheckIns = await db.ManagedDocumentCheckIns.AsNoTracking().Where(x => x.RevisionId == revisionId).ToListAsync(ct);
        var contributionEvidence = acceptedCheckIns.GroupBy(x => x.ActorId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { ContributorId = group.Key, EvidenceHash = group.OrderByDescending(x => x.WorkingVersion).First().ResultSha256 }).ToList();
        var contributorIds = contributionEvidence.Select(x => x.ContributorId).Append(data.Revision.InitiatedBy).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (contributorIds.Contains(request.TechnicalReviewerId) || contributorIds.Contains(request.FinalApproverId))
            return Results.BadRequest(new { error = "A contributor to this exact submitted snapshot cannot serve as an independent reviewer." });
        try
        {
            var attachment = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct);
            try { await integrity.VerifyAsync(attachment, actor.UserName, ct); }
            catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
            var relationshipEvidence = ManagedDocumentRelationshipPolicy.Manifest(await db.ManagedDocumentLinks.AsNoTracking().Where(x => x.RevisionId == revisionId).ToListAsync(ct));
            if (!string.Equals(attachment.Sha256, request.ExpectedWorkingSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(relationshipEvidence.Hash, request.ExpectedRelationshipManifestHash, StringComparison.OrdinalIgnoreCase))
                return ReviewConflict("submission_evidence_changed", "The working file or relationship manifest changed after this page loaded. Refresh before submitting.", data.Revision);
            var snapshotHash = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"managed-document-review-v2:{attachment.Sha256}:{data.Revision.FormalSummaryHash}:{data.Revision.FormalSummaryVersion}:{relationshipEvidence.Hash}"));
            ManagedDocumentReviewer Reviewer(UserAccount account, string stage, ManagedDocumentAuthorityEvidence evidence) => new(account.UserName, account.DisplayName, stage,
                evidence.RequiredAuthority, evidence.GrantedAuthority.ToString(), evidence.Source, evidence.SourceId, ManagedDocumentReviewAuthority.PolicyId,
                ManagedDocumentReviewAuthority.PolicyName, ManagedDocumentReviewAuthority.PolicyVersion, ManagedDocumentReviewAuthority.FrozenPolicy);
            var cycle = data.Revision.SubmitForReview(actor.UserName, snapshotHash, [Reviewer(technicalAccount, "Technical review", technicalAuthority), Reviewer(finalAccount, "SQA / configuration release authorization", finalAuthority)], now, relationshipEvidence.Json, relationshipEvidence.Hash);
            db.ManagedDocumentReviewSteps.AddRange(data.Revision.ReviewSteps.Where(x => x.Cycle == cycle));
            foreach (var contribution in contributionEvidence)
                db.ManagedDocumentReviewContributors.Add(new(revisionId, cycle, contribution.ContributorId, contribution.EvidenceHash, now));
            if (!contributionEvidence.Any(x => string.Equals(x.ContributorId, data.Revision.InitiatedBy, StringComparison.OrdinalIgnoreCase)))
                db.ManagedDocumentReviewContributors.Add(new(revisionId, cycle, data.Revision.InitiatedBy, snapshotHash, now));
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentSubmitted", actor.UserName, $"Submitted {data.Document.DocumentNumber}.{data.Revision.Revision:D2} for independent review.", now)); db.UserNotifications.Add(new(data.Document.ProjectId, request.TechnicalReviewerId, "DocumentReviewActivated", $"Review {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Technical document review is ready.", $"managed-document:{data.Document.Id}", data.Document.Id, now));
            var resultJson = JsonSerializer.Serialize(new { state = data.Revision.State.ToString(), data.Revision.Version, cycle, snapshotHash });
            db.ManagedDocumentOperations.Add(new(revisionId, "Submit", request.OperationKey, payloadHash, resultJson, now));
            await db.SaveChangesAsync(ct); return Results.Content(resultJson, "application/json");
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Submit", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The document changed while it was being submitted. Refresh before trying again.", code = "stale_revision" }); }
        catch (DbUpdateException ex) when (IsManagedDocumentOperationKeyConflict(ex))
        { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Submit", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The submission operation key was used concurrently.", code = "operation_key_conflict" }); }
    }

    private static async Task<IResult> ReviseFormalSummaryAsync(Guid revisionId, ReviseManagedDocumentFormalSummaryRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount();
        if (!string.Equals(data.Revision.ResponsibleOwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase)
            && !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        try
        {
            var oldHash = data.Revision.FormalSummaryHash; var now = DateTimeOffset.UtcNow; var reason = request.Reason.Trim();
            data.Revision.ReviseFormalSummary(request.FormalChangeSummary, reason, request.ExpectedVersion, now);
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentFormalSummaryRevised", actor.UserName, $"Revised the formal scope for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} from {oldHash} to {data.Revision.FormalSummaryHash}. Reason: {reason}", now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { formalChangeSummary = data.Revision.FormalChangeSummary, data.Revision.FormalSummaryHash, data.Revision.FormalSummaryVersion, data.Revision.Version });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
    }

    private static async Task<IResult> ReassignStewardAsync(Guid id, ReassignManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.SingleOrDefaultAsync(x => x.Id == id, ct); if (document is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct) || !await http.HasProjectRoleAsync(db, identity, document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) return Results.BadRequest(new { error = "Provide a reassignment reason of 1000 characters or fewer." });
        if (!await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, document.ProjectId, request.AssigneeId, DateTimeOffset.UtcNow, ct)) return Results.BadRequest(new { error = "The new document steward must be an active authorized member or delegate in this Program." });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); var reason = request.Reason?.Trim() ?? "";
            var prior = document.ReassignSteward(request.AssigneeId, request.ExpectedVersion, now);
            db.ManagedDocumentAssignments.Add(new(document.Id, null, "DocumentSteward", prior, document.StewardId, actor.UserName, reason, now));
            db.ManagedDocumentEvents.Add(new(document.Id, "DocumentStewardReassigned", actor.UserName, $"Reassigned document stewardship from {prior} to {document.StewardId}. Reason: {reason}", now));
            db.SecurityAuditEvents.Add(new("ManagedDocumentStewardReassigned", actor.UserName, document.DocumentNumber, "Success", $"{prior} -> {document.StewardId}; {reason}", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            db.UserNotifications.Add(new(document.ProjectId, document.StewardId, "ManagedDocumentStewardAssigned", $"Steward {document.DocumentNumber}", reason, $"managed-document:{document.Id}", document.Id, now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { document.StewardId, document.Version });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The managed document changed after this page loaded. Refresh and try again." }); }
    }

    private static async Task<IResult> ReassignResponsibleOwnerAsync(Guid revisionId, ReassignManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct) || !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) return Results.BadRequest(new { error = "Provide a reassignment reason of 1000 characters or fewer." });
        if (!await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, data.Document.ProjectId, request.AssigneeId, DateTimeOffset.UtcNow, ct)) return Results.BadRequest(new { error = "The responsible revision owner must be an active authorized member or delegate in this Program." });
        try
        {
            var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); var reason = request.Reason?.Trim() ?? "";
            var prior = data.Revision.ReassignResponsibleOwner(request.AssigneeId, request.ExpectedVersion, now);
            db.ManagedDocumentAssignments.Add(new(data.Document.Id, data.Revision.Id, "RevisionResponsibleOwner", prior, data.Revision.ResponsibleOwnerId, actor.UserName, reason, now));
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentRevisionOwnerReassigned", actor.UserName, $"Reassigned responsibility for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} from {prior} to {data.Revision.ResponsibleOwnerId}. Reason: {reason}", now));
            db.SecurityAuditEvents.Add(new("ManagedDocumentRevisionOwnerReassigned", actor.UserName, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Success", $"{prior} -> {data.Revision.ResponsibleOwnerId}; {reason}", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            db.UserNotifications.Add(new(data.Document.ProjectId, data.Revision.ResponsibleOwnerId, "ManagedDocumentRevisionAssigned", $"Own {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", reason, $"managed-document:{data.Document.Id}", data.Document.Id, now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { data.Revision.ResponsibleOwnerId, data.Revision.Version });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
    }

    private static async Task<IResult> ApproveAsync(Guid revisionId, DocumentReviewDecisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        var inputError = ValidateReviewDecision(request); if (inputError is not null) return inputError;
        var payloadHash = OperationPayloadHash($"Approve:{http.UserAccount().UserName}", ReviewDecisionPayload(request));
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid(); var actor = http.UserAccount();
        if (!await db.UserAccounts.AsNoTracking().AnyAsync(x => x.Id == actor.Id && x.State == AccountState.Active, ct)) return Results.Forbid();
        if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
        var priorOperation = await OperationResultAsync(db, revisionId, "Approve", request.OperationKey, payloadHash, ct); if (priorOperation is not null) return priorOperation;
        try
        {
            var step = data.Revision.ReviewSteps.SingleOrDefault(x => x.Id == request.ExpectedStepId);
            if (step is null) return ReviewConflict("stale_review_intent", "The review step changed after this page loaded. Refresh before acting.", data.Revision);
            var attachmentsToVerify = new List<Guid>();
            if (data.Revision.CurrentWorkingAttachmentId is Guid workingId) attachmentsToVerify.Add(workingId);
            var isFinalStep = step.Position == data.Revision.ReviewSteps.Where(x => x.Cycle == step.Cycle).Max(x => x.Position);
            if (isFinalStep)
            {
                if (data.Revision.ReleaseCandidateDocxAttachmentId is Guid docxId) attachmentsToVerify.Add(docxId);
                if (data.Revision.ReleaseCandidatePdfAttachmentId is Guid pdfId) attachmentsToVerify.Add(pdfId);
            }
            var controlledFiles = await db.ControlledAttachments.AsNoTracking().Where(x => attachmentsToVerify.Contains(x.Id)).ToListAsync(ct);
            if (controlledFiles.Count != attachmentsToVerify.Distinct().Count())
                return Results.Conflict(new { error = "The controlled review evidence metadata is incomplete.", code = "document_integrity_blocked" });
            foreach (var attachment in controlledFiles)
            {
                try { await integrity.VerifyAsync(attachment, actor.UserName, ct); }
                catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
            }
            var now = DateTimeOffset.UtcNow;
            var final = data.Revision.Approve(actor.UserName, request.ExpectedStepId, request.ExpectedCycle, request.ExpectedVersion,
                request.ExpectedStepVersion, request.ExpectedSnapshotHash, request.ExpectedCandidateDocxAttachmentId,
                request.ExpectedCandidatePdfAttachmentId, request.ExpectedCandidateManifestHash, request.Rationale, now);
            var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct); var contentHash = final ? data.Revision.ReleaseManifestHash : data.Revision.SnapshotHash;
            db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ManagedDocument", data.Document.Id, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}", final ? "Release" : "Approve", request.Meaning.Trim(), contentHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now, step.GrantedAuthority, step.Id, step.Cycle, step.Position, request.Rationale, step.AuthoritySource, step.WorkflowId, step.WorkflowVersion, step.AuthoritySourceId)); db.ManagedDocumentEvents.Add(new(data.Document.Id, final ? "DocumentReleased" : "DocumentReviewApproved", actor.UserName, final ? $"Released {data.Document.DocumentNumber}.{data.Revision.Revision:D2} as the exact approved DOCX/PDF pair under {step.GrantedAuthority} authority ({step.AuthoritySource})." : $"Approved {step.StageName} for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} under {step.GrantedAuthority} authority ({step.AuthoritySource}).", now));
            if (final)
            {
                var older = await db.ManagedDocumentRevisions.Where(x => x.DocumentId == data.Document.Id && x.Id != data.Revision.Id && x.State == ManagedDocumentState.Released).ToListAsync(ct); foreach (var prior in older.Where(x => x.Revision < data.Revision.Revision)) prior.Supersede(now);
            }
            else { var next = data.Revision.ReviewSteps.Single(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active); db.UserNotifications.Add(new(data.Document.ProjectId, next.ApproverId, "DocumentReviewActivated", $"Review {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Final document release review is ready.", $"managed-document:{data.Document.Id}", data.Document.Id, now)); }
            var resultJson = JsonSerializer.Serialize(new { final, state = data.Revision.State.ToString(), data.Revision.Version, reviewStepId = step.Id, cycle = step.Cycle, authority = step.GrantedAuthority, authoritySource = step.AuthoritySource, contentHash });
            db.ManagedDocumentOperations.Add(new(revisionId, "Approve", request.OperationKey, payloadHash, resultJson, now));
            await db.SaveChangesAsync(ct); return Results.Content(resultJson, "application/json");
        }
        catch (DomainException ex) when (IsStaleReview(ex)) { return ReviewConflict("stale_review_intent", ex.Message, data.Revision); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Approve", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The review advanced while this decision was being recorded. Refresh before acting.", code = "stale_review_intent" }); }
        catch (DbUpdateException ex) when (IsManagedDocumentOperationKeyConflict(ex))
        { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Approve", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The decision operation key was used concurrently.", code = "operation_key_conflict" }); }
    }

    private static async Task<IResult> ReturnAsync(Guid revisionId, DocumentReviewDecisionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        var inputError = ValidateReviewDecision(request); if (inputError is not null) return inputError;
        var payloadHash = OperationPayloadHash($"Return:{http.UserAccount().UserName}", ReviewDecisionPayload(request));
        var data = await RevisionDataAsync(db, revisionId, ct, true); if (data is null) return Results.NotFound(); if (!await http.HasProjectAccessAsync(db, data.Document.ProjectId, ct)) return Results.Forbid(); var actor = http.UserAccount();
        if (!await db.UserAccounts.AsNoTracking().AnyAsync(x => x.Id == actor.Id && x.State == AccountState.Active, ct)) return Results.Forbid();
        if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
        var priorOperation = await OperationResultAsync(db, revisionId, "Return", request.OperationKey, payloadHash, ct); if (priorOperation is not null) return priorOperation;
        try
        {
            var step = data.Revision.ReviewSteps.SingleOrDefault(x => x.Id == request.ExpectedStepId);
            if (step is null) return ReviewConflict("stale_review_intent", "The review step changed after this page loaded. Refresh before acting.", data.Revision);
            if (data.Revision.CurrentWorkingAttachmentId is not Guid workingId) return Results.Conflict(new { error = "The submitted working evidence metadata is missing.", code = "document_integrity_blocked" });
            var working = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workingId, ct);
            if (working is null) return Results.Conflict(new { error = "The submitted working evidence metadata is missing.", code = "document_integrity_blocked" });
            try { await integrity.VerifyAsync(working, actor.UserName, ct); }
            catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
            var contentHash = data.Revision.SnapshotHash; var now = DateTimeOffset.UtcNow;
            if (data.Revision.ReleaseCandidateDocxAttachmentId is Guid candidateDocxId)
            {
                var candidateDocx = await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == candidateDocxId, ct);
                candidateDocx?.Supersede();
            }
            if (data.Revision.ReleaseCandidatePdfAttachmentId is Guid candidatePdfId)
            {
                var candidatePdf = await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == candidatePdfId, ct);
                candidatePdf?.Supersede();
            }
            data.Revision.Return(actor.UserName, request.ExpectedStepId, request.ExpectedCycle, request.ExpectedVersion, request.ExpectedStepVersion, request.ExpectedSnapshotHash, request.Rationale, now);
            var programId = await db.Projects.Where(x => x.Id == data.Document.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
            db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ManagedDocument", data.Document.Id, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}", "Return", request.Meaning.Trim(), contentHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now, step.GrantedAuthority, step.Id, step.Cycle, step.Position, request.Rationale, step.AuthoritySource, step.WorkflowId, step.WorkflowVersion, step.AuthoritySourceId));
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentReturned", actor.UserName, $"Returned {data.Document.DocumentNumber}.{data.Revision.Revision:D2} from {step.StageName} under {step.GrantedAuthority} authority ({step.AuthoritySource}): {request.Rationale.Trim()}", now)); db.UserNotifications.Add(new(data.Document.ProjectId, data.Revision.ResponsibleOwnerId, "DocumentReturned", $"Returned {data.Document.DocumentNumber}.{data.Revision.Revision:D2}", request.Rationale.Trim(), $"managed-document:{data.Document.Id}", data.Document.Id, now));
            var resultJson = JsonSerializer.Serialize(new { state = data.Revision.State.ToString(), data.Revision.Version, reviewStepId = step.Id, cycle = step.Cycle, authority = step.GrantedAuthority, authoritySource = step.AuthoritySource, contentHash });
            db.ManagedDocumentOperations.Add(new(revisionId, "Return", request.OperationKey, payloadHash, resultJson, now));
            await db.SaveChangesAsync(ct); return Results.Content(resultJson, "application/json");
        }
        catch (DomainException ex) when (IsStaleReview(ex)) { return ReviewConflict("stale_review_intent", ex.Message, data.Revision); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Return", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The review advanced while this decision was being recorded. Refresh before acting.", code = "stale_review_intent" }); }
        catch (DbUpdateException ex) when (IsManagedDocumentOperationKeyConflict(ex))
        { db.ChangeTracker.Clear(); return await OperationResultAsync(db, revisionId, "Return", request.OperationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The decision operation key was used concurrently.", code = "operation_key_conflict" }); }
    }

    private static async Task<IResult> ForceUnlockAsync(Guid revisionId, ForceUnlockManagedDocumentRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound(); var actor = http.UserAccount(); if (!actor.IsAdministrator && !await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid(); var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.ArtifactType == "ManagedDocument" && x.ArtifactId == data.Document.Id && x.State == EditSessionState.Active, ct); if (session is null) return Results.NotFound();
        try { var now = DateTimeOffset.UtcNow; session.ForceUnlock(actor.UserName, request.Reason, now); var grants = await db.DocumentConnectorGrants.Where(x => x.EditSessionId == session.Id && x.RevokedAt == null).ToListAsync(ct); foreach (var grant in grants) grant.Revoke(now); db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentForceUnlocked", actor.UserName, $"Force-unlocked the checkout held by {session.UserName}. Reason: {request.Reason}", now)); db.SecurityAuditEvents.Add(new("DocumentForceUnlock", actor.UserName, data.Document.DocumentNumber, "Success", request.Reason, http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct); return Results.NoContent(); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> WithdrawAsync(Guid revisionId, WithdrawManagedDocumentRevisionRequest request,
        HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RevisionDataAsync(db, revisionId, ct); if (data is null) return Results.NotFound();
        var actor = http.UserAccount();
        var authority = string.Equals(data.Revision.ResponsibleOwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase)
            ? await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)
            : await http.HasProjectRoleAsync(db, identity, data.Document.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead);
        if (!authority) return Results.Forbid();
        try
        {
            var now = DateTimeOffset.UtcNow; data.Revision.Withdraw(request.Reason, request.ExpectedVersion, now);
            var sessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument"
                && x.RevisionId == revisionId && x.State == EditSessionState.Active).ToListAsync(ct);
            foreach (var session in sessions) session.ForceUnlock(actor.UserName, $"Revision withdrawn: {request.Reason.Trim()}", now);
            var grants = await db.DocumentConnectorGrants.Where(x => x.RevisionId == revisionId && x.RevokedAt == null).ToListAsync(ct);
            foreach (var grant in grants) grant.Revoke(now);
            var attachments = await db.ControlledAttachments.Where(x => x.ArtifactType == "ManagedDocument" && x.RevisionId == revisionId).ToListAsync(ct);
            foreach (var attachment in attachments) attachment.Withdraw();
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentRevisionWithdrawn", actor.UserName,
                $"Withdrew {data.Document.DocumentNumber}.{data.Revision.Revision:D2}; closed {sessions.Count} checkout(s), revoked {grants.Count} grant(s), and retained {attachments.Count} controlled attachment record(s). Reason: {request.Reason.Trim()}", now));
            db.SecurityAuditEvents.Add(new("ManagedDocumentRevisionWithdraw", actor.UserName, $"{data.Document.DocumentNumber}.{data.Revision.Revision:D2}",
                "Success", request.Reason.Trim(), http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct); return Results.Ok(new { data.Revision.Id, state = data.Revision.State.ToString(), data.Revision.Version, closedSessions = sessions.Count, revokedGrants = grants.Count });
        }
        catch (DomainException ex) when (ex.Message.Contains("after this page loaded", StringComparison.OrdinalIgnoreCase))
        { return Results.Conflict(new { error = ex.Message, code = "stale_document_revision" }); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed while it was being withdrawn.", code = "stale_document_revision" }); }
    }

    private static async Task<IResult> AddLinkAsync(Guid id, ManagedDocumentLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var data = await RelationshipRevisionAsync(id, request.RevisionId, http, db, identity, ct); if (data.Error is not null) return data.Error;
        try
        {
            var target = await ResolveLinkTargetAsync(request.ArtifactType, request.ArtifactId, data.Document!.ProjectId, db, ct);
            var relationship = ManagedDocumentRelationshipPolicy.Validate(target.ArtifactType, request.Relationship); var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            if (await db.ManagedDocumentLinks.AnyAsync(x => x.RevisionId == request.RevisionId && x.IsCurrent && x.ArtifactType == target.ArtifactType && x.ArtifactId == target.ArtifactId && x.Relationship == relationship, ct))
                return Results.Conflict(new { error = "That canonical relationship is already active on this document revision." });
            data.Revision!.RecordRelationshipChange(request.ExpectedVersion, now);
            var link = NewCanonicalLink(request.RevisionId, target, relationship, actor.UserName, now); db.ManagedDocumentLinks.Add(link);
            db.ManagedDocumentEvents.Add(new(id, "DocumentArtifactLinked", actor.UserName, $"Linked canonical {target.DisplayNumber} as {relationship} ({target.ArtifactType}, policy v{ManagedDocumentRelationshipPolicy.CurrentVersion}).", now));
            await db.SaveChangesAsync(ct); return Results.Created($"/api/managed-documents/{id}", LinkResult(link));
        }
        catch (DomainException ex) when (ex.Message.StartsWith("The document revision changed", StringComparison.Ordinal)) { return Results.Conflict(new { error = ex.Message }); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "That canonical relationship is already active on this document revision." }); }
    }

    private static async Task<IResult> CorrectLinkAsync(Guid id, Guid linkId, CorrectManagedDocumentLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var prior = await db.ManagedDocumentLinks.SingleOrDefaultAsync(x => x.Id == linkId, ct); if (prior is null) return Results.NotFound();
        var data = await RelationshipRevisionAsync(id, prior.RevisionId, http, db, identity, ct); if (data.Error is not null) return data.Error;
        try
        {
            var target = await ResolveLinkTargetAsync(request.ArtifactType, request.ArtifactId, data.Document!.ProjectId, db, ct);
            var relationship = ManagedDocumentRelationshipPolicy.Validate(target.ArtifactType, request.Relationship); var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
            if (await db.ManagedDocumentLinks.AnyAsync(x => x.RevisionId == prior.RevisionId && x.Id != prior.Id && x.IsCurrent && x.ArtifactType == target.ArtifactType && x.ArtifactId == target.ArtifactId && x.Relationship == relationship, ct))
                return Results.Conflict(new { error = "That canonical relationship is already active on this document revision." });
            data.Revision!.RecordRelationshipChange(request.ExpectedVersion, now);
            var replacement = NewCanonicalLink(prior.RevisionId, target, relationship, actor.UserName, now); prior.Supersede(actor.UserName, request.Reason, now, replacement.Id); db.ManagedDocumentLinks.Add(replacement);
            db.ManagedDocumentEvents.Add(new(id, "DocumentArtifactRelationshipCorrected", actor.UserName, $"Corrected {prior.DisplayNumber} / {prior.Relationship} to canonical {target.DisplayNumber} / {relationship}. Reason: {request.Reason.Trim()}", now));
            await db.SaveChangesAsync(ct); return Results.Ok(LinkResult(replacement));
        }
        catch (DomainException ex) when (ex.Message.StartsWith("The document revision changed", StringComparison.Ordinal) || ex.Message.Contains("already been superseded", StringComparison.Ordinal)) { return Results.Conflict(new { error = ex.Message }); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "That canonical relationship is already active on this document revision." }); }
    }

    private static async Task<IResult> SupersedeLinkAsync(Guid id, Guid linkId, SupersedeManagedDocumentLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var link = await db.ManagedDocumentLinks.SingleOrDefaultAsync(x => x.Id == linkId, ct); if (link is null) return Results.NotFound();
        var data = await RelationshipRevisionAsync(id, link.RevisionId, http, db, identity, ct); if (data.Error is not null) return data.Error;
        try { var now = DateTimeOffset.UtcNow; var actor = http.UserAccount(); data.Revision!.RecordRelationshipChange(request.ExpectedVersion, now); link.Supersede(actor.UserName, request.Reason, now); db.ManagedDocumentEvents.Add(new(id, "DocumentArtifactRelationshipSuperseded", actor.UserName, $"Superseded {link.DisplayNumber} / {link.Relationship}. Reason: {request.Reason.Trim()}", now)); await db.SaveChangesAsync(ct); return Results.Ok(new { link.Id, link.IsCurrent, data.Revision.Version }); }
        catch (DomainException ex) when (ex.Message.StartsWith("The document revision changed", StringComparison.Ordinal) || ex.Message.Contains("already been superseded", StringComparison.Ordinal)) { return Results.Conflict(new { error = ex.Message }); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The document revision changed after this page loaded. Refresh and try again." }); }
    }

    private static async Task<IResult> LinkOptionsAsync(Guid projectId, string artifactType, string? search, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var term = search?.Trim().ToLowerInvariant() ?? ""; string type; try { type = ManagedDocumentRelationshipPolicy.CanonicalType(artifactType); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        if (type == "ChangeRequest")
        {
            var rows = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, x.Title, secondary = x.State.ToString(), relationships = ManagedDocumentRelationshipPolicy.Relationships(type) }));
        }
        if (type == "ProblemReport")
        {
            var rows = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, x.Title, secondary = x.State.ToString(), relationships = ManagedDocumentRelationshipPolicy.Relationships(type) }));
        }
        if (type == "TestChangeRequest")
        {
            var rows = await db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Where(x => term.Length == 0 || x.DisplayNumber.Contains(term, StringComparison.OrdinalIgnoreCase) || x.SourceChangeRequestNumber.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).Take(100).Select(x => new { x.Id, x.DisplayNumber, title = x.SourceChangeRequestNumber, secondary = x.State.ToString(), relationships = ManagedDocumentRelationshipPolicy.Relationships(type) }));
        }
        if (type == "Release")
        {
            var rows = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, displayNumber = $"BUILD-{x.Version}", title = $"Build {x.Version}", secondary = x.IsReleased ? "Released" : "In work", relationships = ManagedDocumentRelationshipPolicy.Relationships(type) }));
        }
        return Results.BadRequest(new { error = "Choose a supported lifecycle artifact type." });
    }

    private static async Task<IResult> DownloadAttachmentAsync(Guid attachmentId, HttpContext http, AeroLinkDbContext db, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        var attachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachmentId && x.ArtifactType == "ManagedDocument", ct);
        if (attachment is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, attachment.ProjectId, ct)) return Results.Forbid();
        try { return Results.File(await integrity.OpenVerifiedAsync(attachment, http.UserAccount().UserName, ct), attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true); }
        catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
    }

    private static async Task<IResult> ScanIntegrityAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.ConfigurationManager, ProgramRole.SoftwareQualityAnalyst, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        var result = await integrity.ScanProjectAsync(projectId, http.UserAccount().UserName, ct);
        return Results.Ok(new { result.Checked, result.Healthy, result.Failed, result.FailedAttachmentIds, scannedAt = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> ReconcileStorageAsync(Guid projectId, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, ManagedDocumentStorageCoordinator storage, CancellationToken ct)
    {
        if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.ConfigurationManager,
            ProgramRole.SoftwareQualityAnalyst, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead)) return Results.Forbid();
        var result = await storage.ReconcileProjectAsync(projectId, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RestoreAttachmentAsync(Guid attachmentId, HttpContext http, AeroLinkDbContext db, IdentityService identity, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    {
        var attachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachmentId && x.ArtifactType == "ManagedDocument", ct);
        if (attachment is null) return Results.NotFound();
        if (!await http.HasProjectRoleAsync(db, identity, attachment.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.SoftwareQualityAnalyst, ProgramRole.ProgramManager)) return Results.Forbid();
        var form = await http.Request.ReadFormAsync(ct); var file = form.Files.GetFile("file"); var reason = form["reason"].ToString();
        if (file is null) return Results.BadRequest(new { error = "Select the verified recovery object." });
        try
        {
            await using var source = file.OpenReadStream();
            var quarantineKey = await integrity.RestoreAsync(attachment, source, http.UserAccount().UserName, reason, ct);
            return Results.Ok(new { attachment.Id, attachment.Sha256, attachment.Size, quarantineKey, recoveredAt = DateTimeOffset.UtcNow });
        }
        catch (ManagedDocumentIntegrityFailure ex) { return Results.BadRequest(new { error = ex.Message, code = ex.Code, ex.AttachmentId }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message, code = "document_recovery_conflict" }); }
    }

    private static async Task<IResult> ConnectorEnrollmentAsync(Guid projectId, HttpContext http, AeroLinkDbContext db, ConnectorSigningService signing, CancellationToken ct)
    {
        if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
        var now = DateTimeOffset.UtcNow; var actor = http.UserAccount();
        var origin = signing.ResolveOrigin(http);
        var manifest = signing.Enrollment(origin, now);
        db.SecurityAuditEvents.Add(new("ConnectorTrustManifestIssued", actor.UserName, signing.DeploymentId, "Success",
            $"Issued connector trust manifest for {origin}, key {signing.KeyId}, and Project {projectId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
        await db.SaveChangesAsync(ct);
        return Results.Json(manifest);
    }

    private static async Task<IResult> RedeemAsync(string launchToken, AeroLinkDbContext db, ILogger<ConnectorSigningService> logger, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var tokenHash = Hash(launchToken);
        var grant = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.LaunchTokenHash == tokenHash, ct);
        if (grant is null)
        {
            logger.LogWarning("Rejected unknown connector launch nonce {LaunchNonceHashPrefix}.", tokenHash[..16]);
            return Results.Unauthorized();
        }
        try
        {
            var accessToken = Token(); grant.Redeem(Hash(accessToken), now);
            var session = await db.ArtifactEditSessions.SingleAsync(x => x.Id == grant.EditSessionId, ct);
            var document = await db.ManagedDocuments.AsNoTracking().SingleAsync(x => x.Id == grant.DocumentId, ct);
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == grant.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
            var attachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grant.SourceAttachmentId
                && x.ProjectId == grant.ProjectId && x.ArtifactId == grant.DocumentId && x.RevisionId == grant.RevisionId, ct);
            if (attachment is null || attachment.Size != grant.SourceSize || !string.Equals(attachment.Sha256, grant.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "The connector source attachment no longer matches the signed launch envelope.", code = "document_integrity_blocked" });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { grant.Id, accessToken, grant.Mode, grant.DeploymentId, grant.Origin, programId, grant.ProjectId,
                grant.DocumentId, grant.DocumentNumber, document.Title, grant.RevisionId, grant.RevisionNumber,
                editSessionId = session.Id, grant.RecoveryWorkspaceId, expiresAt = session.ExpiresAt, sessionVersion = session.Version, grant.SourceAttachmentId,
                grant.SourceSize, grant.SourceSha256 });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This connector launch ticket was redeemed concurrently.", code = "connector_envelope_replayed" }); }
    }

    private static async Task<IResult> ConnectorDownloadAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, ManagedDocumentIntegrityService integrity, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; var grant = auth.Grant!; var attachment = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grant.SourceAttachmentId && x.ProjectId == grant.ProjectId && x.ArtifactId == grant.DocumentId && x.RevisionId == grant.RevisionId, ct); if (attachment is null || attachment.Size != grant.SourceSize || !string.Equals(attachment.Sha256, grant.SourceSha256, StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new { error = "The connector source attachment metadata does not match the signed launch.", code = "document_integrity_blocked" }); try { return Results.File(await integrity.OpenVerifiedAsync(attachment, grant.UserName, ct), attachment.ContentType, attachment.OriginalFileName, enableRangeProcessing: true); } catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); } }

    private static async Task<IResult> ConnectorHeartbeatAsync(Guid grantId, ConnectorVersionRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; try { var now = DateTimeOffset.UtcNow; auth.Session!.Heartbeat(request.ExpectedVersion, now, 15); auth.Grant!.Extend(now); await db.SaveChangesAsync(ct); return Results.Ok(new { auth.Session.Version, auth.Session.ExpiresAt }); } catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "stale_connector_session" }); } catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The connector session was finalized while its lease was being renewed.", code = "stale_connector_session" }); } }

    private static async Task<IResult> ConnectorCheckInAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, ManagedDocumentFileService files, ManagedDocumentIntegrityService integrity, ManagedDocumentStorageCoordinator storage, CancellationToken ct)
    {
        var grant = await ConnectorGrantByTokenAsync(grantId, http, db, ct); if (grant is null) return Results.Unauthorized();
        if (grant.Mode != "edit") return Results.BadRequest(new { error = "This connector session is for release preparation, not draft check-in." });
        var form = await http.Request.ReadFormAsync(ct); var upload = form.Files.GetFile("file"); var comment = form["comment"].ToString().Trim();
        if (upload is null) return Results.BadRequest(new { error = "Choose the edited Word document to check in." });
        if (!long.TryParse(form["expectedVersion"], out var expectedVersion)) return Results.BadRequest(new { error = "The connector session version is required." });
        if (comment.Length == 0) return Results.BadRequest(new { error = "A check-in comment is required." });
        if (comment.Length > 4000) return Results.BadRequest(new { error = "A check-in comment cannot exceed 4000 characters." });
        var payloadHash = ""; var operationKey = $"connector-check-in:{grant.Id}"; ManagedDocumentStorageOperation? storageOperation = null;
        try
        {
            await using var source = upload.OpenReadStream(); var content = await files.ReadDocxAsync(source, upload.FileName, true, ct);
            payloadHash = OperationPayloadHash("ConnectorCheckIn", new { expectedVersion, comment, sha256 = ManagedDocumentFileService.Sha256(content) });
            var priorOperation = await OperationResultAsync(db, grant.RevisionId, "ConnectorCheckIn", operationKey, payloadHash, ct); if (priorOperation is not null) return priorOperation;
            if (grant.RevokedAt is not null) return Results.Conflict(new { error = "That connector grant completed with different check-in intent.", code = "operation_key_reused" });
            var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error;
            var data = await RevisionDataAsync(db, grant.RevisionId, ct); if (data is null) return Results.NotFound();
            if (auth.Session!.Version != expectedVersion) return Results.Conflict(new { error = "The connector session changed after this upload began.", code = "stale_connector_session" });
            if (data.Revision.State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) return Results.Conflict(new { error = "Only a Draft or returned revision can accept a check-in.", code = "document_revision_not_editable" });
            var current = data.Revision.CurrentWorkingAttachmentId is null ? null : await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == data.Revision.CurrentWorkingAttachmentId, ct);
            if (current is null || !string.Equals(current.Sha256, auth.Session!.BaseSnapshotHash, StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new { error = "The checked-in source changed after this checkout. No file was overwritten.", code = "document_snapshot_conflict" });
            try { await integrity.VerifyAsync(current, grant.UserName, ct); }
            catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }
            var version = await db.ControlledAttachments.CountAsync(x => x.LogicalId == data.Revision.Id, ct) + 1; var now = DateTimeOffset.UtcNow;
            var returnResolution = data.Revision.State == ManagedDocumentState.Returned ? comment : null;
            var started = await storage.BeginAsync(data.Document.ProjectId, data.Document.Id, data.Revision.Id, "ConnectorCheckIn", operationKey, payloadHash, grant.UserName, now, ct);
            storageOperation = started.Operation; if (started.ExistingResult is not null) return Results.Content(started.ExistingResult, "application/json");
            var staged = await files.StageAsync(storageOperation.Id, "working-docx", data.Document.ProjectId, data.Document.Id,
                data.Revision.Id, data.Revision.Id, version, "Working Word document", comment, upload.FileName,
                ManagedDocumentFileService.DocxContentType, content, current.Id, grant.UserName, now, ct);
            await storage.CheckpointAsync(storageOperation, "object-staged-1", ct);
            var next = staged.Attachment;
            var resultJson = JsonSerializer.Serialize(new { attachmentId = next.Id, sha256 = next.Sha256, workingVersion = version, documentVersion = data.Revision.Version + 1 });
            await storage.RecordPlanAsync(storageOperation, [StorageObject("working-docx", next, staged.Staged)], resultJson, now, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            current.Supersede(); db.ControlledAttachments.Add(next); data.Revision.RecordCheckIn(next.Id, now);
            db.ManagedDocumentCheckIns.Add(new(data.Revision.Id, next.Id, version, grant.UserName, comment, current.Id, current.Sha256, next.Sha256, current.Id, auth.Session.Id, operationKey, now, returnResolution));
            auth.Session.Close(EditSessionState.Committed, expectedVersion, now, grant.UserName, comment); grant.Revoke(now);
            db.ManagedDocumentEvents.Add(new(data.Document.Id, returnResolution is null ? "DocumentCheckedIn" : "DocumentReturnResolved", grant.UserName, returnResolution is null ? $"Checked in {data.Document.DocumentNumber}.{data.Revision.Revision:D2} working version {version}: {comment}" : $"Resolved the returned review for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} in working version {version}: {comment}", now));
            db.ManagedDocumentOperations.Add(new(data.Revision.Id, "ConnectorCheckIn", operationKey, payloadHash, resultJson, now));
            await storage.PromoteAsync(storageOperation, [staged.Staged], ct); await db.SaveChangesAsync(ct); await storage.CheckpointAsync(storageOperation, "metadata-saved", ct); await transaction.CommitAsync(ct);
            await storage.CompleteAsync(storageOperation, now, ct); return Results.Content(resultJson, "application/json");
        }
        catch (ManagedDocumentStorageConflictException ex) { return Results.Conflict(new { error = ex.Message, code = ex.Code }); }
        catch (DomainException ex) { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, ex.Message, grant.UserName); return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "A concurrent session or revision update won the check-in transaction.", grant.UserName); db.ChangeTracker.Clear(); return await OperationResultAsync(db, grant.RevisionId, "ConnectorCheckIn", operationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The connector session was finalized concurrently.", code = "stale_connector_session" }); }
        catch (DbUpdateException ex) when (IsManagedDocumentOperationKeyConflict(ex))
        { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "The connector operation key completed concurrently.", grant.UserName); db.ChangeTracker.Clear(); return await OperationResultAsync(db, grant.RevisionId, "ConnectorCheckIn", operationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The connector operation completed concurrently.", code = "operation_key_conflict" }); }
        catch { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "The connector check-in failed before atomic completion.", grant.UserName); throw; }
    }

    private static async Task<IResult> ConnectorReleaseCandidateAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, ManagedDocumentFileService files, ManagedDocumentIntegrityService integrity, ManagedDocumentStorageCoordinator storage, CancellationToken ct)
    {
        var grant = await ConnectorGrantByTokenAsync(grantId, http, db, ct); if (grant is null) return Results.Unauthorized();
        if (grant.Mode != "release") return Results.BadRequest(new { error = "This connector session is for draft editing, not release preparation." });
        var form = await http.Request.ReadFormAsync(ct); var docxUpload = form.Files.GetFile("docx"); var pdfUpload = form.Files.GetFile("pdf");
        if (docxUpload is null || pdfUpload is null) return Results.BadRequest(new { error = "The exact clean DOCX and PDF release renditions are both required." });
        if (!long.TryParse(form["expectedVersion"], out var expectedVersion)) return Results.BadRequest(new { error = "The connector session version is required." });
        var payloadHash = ""; var operationKey = $"connector-release-candidate:{grant.Id}"; ManagedDocumentStorageOperation? storageOperation = null; string? pdfStagingPath = null;
        try
        {
            byte[] docx;
            await using (var docxStream = docxUpload.OpenReadStream())
                docx = await files.ReadDocxAsync(docxStream, docxUpload.FileName, false, ct);

            string pdfFileName;
            try { pdfFileName = ManagedDocumentFileService.NormalizePdfFileName(pdfUpload.FileName); }
            catch (DomainException ex) { return Results.UnprocessableEntity(new { error = ex.Message, code = "invalid_pdf_filename" }); }

            string pdfSha256;
            try
            {
                await using var pdfStream = pdfUpload.OpenReadStream();
                (pdfStagingPath, pdfSha256, _) = await ManagedDocumentFileService.ReadPdfToStagedFileAsync(pdfStream, ct);
            }
            catch (PdfRenditionTooLargeException)
            {
                return Results.Json(new { error = "The PDF rendition exceeds the 100 MB release limit.", code = "rendition_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var pdfValidation = PdfReleaseProfile.ValidateFile(pdfStagingPath);
            if (!pdfValidation.IsValid) return Results.UnprocessableEntity(new { error = pdfValidation.Message, code = pdfValidation.Code });

            payloadHash = OperationPayloadHash("ConnectorReleaseCandidate", new { expectedVersion, docxSha256 = ManagedDocumentFileService.Sha256(docx), pdfSha256 });
            var priorOperation = await OperationResultAsync(db, grant.RevisionId, "ConnectorReleaseCandidate", operationKey, payloadHash, ct); if (priorOperation is not null) return priorOperation;
            if (grant.RevokedAt is not null) return Results.Conflict(new { error = "That connector grant completed with a different release-candidate set.", code = "operation_key_reused" });
            var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error;
            var data = await RevisionDataAsync(db, grant.RevisionId, ct, true); if (data is null) return Results.NotFound(); var now = DateTimeOffset.UtcNow;
            if (auth.Session!.Version != expectedVersion) return Results.Conflict(new { error = "The connector session changed after this candidate upload began.", code = "stale_connector_session" });
            var active = data.Revision.ReviewSteps.SingleOrDefault(x => x.Cycle == data.Revision.CurrentReviewCycle && x.State == ManagedDocumentReviewStepState.Active);
            var finalPosition = data.Revision.ReviewSteps.Where(x => x.Cycle == data.Revision.CurrentReviewCycle).Select(x => x.Position).DefaultIfEmpty(-1).Max();
            if (data.Revision.State != ManagedDocumentState.InReview || active is null || active.Position != finalPosition || !string.Equals(active.ApproverId, grant.UserName, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "Release preparation is no longer at the authorized final review stage.", code = "release_stage_changed" });
            if (data.Revision.CurrentWorkingAttachmentId is not Guid workingId) return Results.Conflict(new { error = "The reviewed working evidence metadata is missing.", code = "document_integrity_blocked" });
            var working = await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workingId, ct);
            if (working is null) return Results.Conflict(new { error = "The reviewed working evidence metadata is missing.", code = "document_integrity_blocked" });
            try { await integrity.VerifyAsync(working, grant.UserName, ct); }
            catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }

            var snapshotBinding = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"managed-document-review-v2:{working.Sha256}:{data.Revision.FormalSummaryHash}:{data.Revision.FormalSummaryVersion}:{data.Revision.SubmittedRelationshipManifestHash}"));
            if (!string.Equals(snapshotBinding, data.Revision.SnapshotHash, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "The candidate was not produced from the exact reviewed working snapshot. Complete technical review again for the current content.", code = "stale_reviewed_source" });

            byte[] reviewed;
            try
            {
                await using var verified = await integrity.OpenVerifiedAsync(working, grant.UserName, ct);
                using var buffer = new MemoryStream();
                await verified.CopyToAsync(buffer, ct);
                reviewed = buffer.ToArray();
            }
            catch (ManagedDocumentIntegrityFailure ex) { return IntegrityFailure(ex); }

            var transformation = ManagedDocumentFileService.ValidateReleaseTransformation(reviewed, docx, data.Document.DocumentNumber, data.Revision.Revision);
            if (!transformation.IsValid)
                return transformation.Code is "candidate_source_mismatch" or "stale_reviewed_source"
                    ? Results.Conflict(new { error = transformation.Message, code = transformation.Code })
                    : Results.UnprocessableEntity(new { error = transformation.Message, code = transformation.Code });

            var summaryMetadata = $"Formal revision scope v{data.Revision.FormalSummaryVersion} ({data.Revision.FormalSummaryHash}): {data.Revision.FormalChangeSummary}. Reviewed source {working.Sha256}; {ManagedDocumentFileService.ReleaseTransformationProfile} v{ManagedDocumentFileService.ReleaseTransformationVersion}.";
            var started = await storage.BeginAsync(data.Document.ProjectId, data.Document.Id, data.Revision.Id,
                "ConnectorReleaseCandidate", operationKey, payloadHash, grant.UserName, now, ct);
            storageOperation = started.Operation; if (started.ExistingResult is not null) return Results.Content(started.ExistingResult, "application/json");
            var priorDocx = data.Revision.ReleaseCandidateDocxAttachmentId is Guid priorDocxId
                ? await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == priorDocxId, ct) : null;
            var priorPdf = data.Revision.ReleaseCandidatePdfAttachmentId is Guid priorPdfId
                ? await db.ControlledAttachments.SingleOrDefaultAsync(x => x.Id == priorPdfId, ct) : null;
            var stagedDocx = await files.StageAsync(storageOperation.Id, "candidate-docx", data.Document.ProjectId, data.Document.Id,
                data.Revision.Id, Guid.NewGuid(), 1, "Released DOCX", summaryMetadata, docxUpload.FileName,
                ManagedDocumentFileService.DocxContentType, docx, priorDocx?.Id, grant.UserName, now, ct);
            await storage.CheckpointAsync(storageOperation, "object-staged-1", ct);
            await using var pdfSource = System.IO.File.OpenRead(pdfStagingPath);
            var stagedPdf = await files.StageAsync(storageOperation.Id, "candidate-pdf", data.Document.ProjectId, data.Document.Id,
                data.Revision.Id, Guid.NewGuid(), 1, "Released PDF", summaryMetadata, pdfFileName,
                ManagedDocumentFileService.PdfContentType, pdfSource, priorPdf?.Id, grant.UserName, now, ct);
            await storage.CheckpointAsync(storageOperation, "object-staged-2", ct);
            var docxAttachment = stagedDocx.Attachment; var pdfAttachment = stagedPdf.Attachment;
            var manifest = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"managed-document-release-v3:{working.Sha256}:{ManagedDocumentFileService.ReleaseTransformationProfile}:{ManagedDocumentFileService.ReleaseTransformationVersion}:{docxAttachment.Sha256}:{pdfAttachment.Sha256}:{data.Revision.FormalSummaryHash}:{data.Revision.FormalSummaryVersion}:{data.Revision.SubmittedRelationshipManifestHash}"));
            var resultJson = JsonSerializer.Serialize(new { manifestHash = manifest, reviewedSourceSha256 = working.Sha256, transformationProfile = ManagedDocumentFileService.ReleaseTransformationProfile, transformationVersion = ManagedDocumentFileService.ReleaseTransformationVersion, docxSha256 = docxAttachment.Sha256, pdfSha256 = pdfAttachment.Sha256, formalSummaryHash = data.Revision.FormalSummaryHash, formalSummaryVersion = data.Revision.FormalSummaryVersion, relationshipManifestHash = data.Revision.SubmittedRelationshipManifestHash });
            await storage.RecordPlanAsync(storageOperation,
                [StorageObject("candidate-docx", docxAttachment, stagedDocx.Staged), StorageObject("candidate-pdf", pdfAttachment, stagedPdf.Staged)], resultJson, now, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            priorDocx?.Supersede(); priorPdf?.Supersede();
            db.ControlledAttachments.AddRange(docxAttachment, pdfAttachment); data.Revision.RecordReleaseCandidate(docxAttachment.Id, pdfAttachment.Id, manifest, grant.UserName, now);
            auth.Session!.Close(EditSessionState.Committed, expectedVersion, now, grant.UserName, "Prepared exact DOCX and PDF release candidate."); grant.Revoke(now);
            db.ManagedDocumentEvents.Add(new(data.Document.Id, "DocumentReleaseCandidatePrepared", grant.UserName, $"Prepared the exact Released DOCX/PDF candidate for {data.Document.DocumentNumber}.{data.Revision.Revision:D2} from reviewed source {working.Sha256} using {ManagedDocumentFileService.ReleaseTransformationProfile} v{ManagedDocumentFileService.ReleaseTransformationVersion}.", now));
            db.ManagedDocumentOperations.Add(new(data.Revision.Id, "ConnectorReleaseCandidate", operationKey, payloadHash, resultJson, now));
            await storage.PromoteAsync(storageOperation, [stagedDocx.Staged, stagedPdf.Staged], ct); await db.SaveChangesAsync(ct); await storage.CheckpointAsync(storageOperation, "metadata-saved", ct); await transaction.CommitAsync(ct);
            await storage.CompleteAsync(storageOperation, now, ct); return Results.Content(resultJson, "application/json");
        }
        catch (ManagedDocumentStorageConflictException ex) { return Results.Conflict(new { error = ex.Message, code = ex.Code }); }
        catch (DomainException ex) { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, ex.Message, grant.UserName); return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "A concurrent review/session update won the release-candidate transaction.", grant.UserName); db.ChangeTracker.Clear(); return await OperationResultAsync(db, grant.RevisionId, "ConnectorReleaseCandidate", operationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The connector session was finalized concurrently.", code = "stale_connector_session" }); }
        catch (DbUpdateException ex) when (IsManagedDocumentOperationKeyConflict(ex))
        { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "The connector operation key completed concurrently.", grant.UserName); db.ChangeTracker.Clear(); return await OperationResultAsync(db, grant.RevisionId, "ConnectorReleaseCandidate", operationKey, payloadHash, ct) ?? Results.Conflict(new { error = "The connector operation completed concurrently.", code = "operation_key_conflict" }); }
        catch { if (storageOperation is not null) await RollBackStorageAsync(db, storage, storageOperation.Id, "The release-candidate operation failed before atomic completion.", grant.UserName); throw; }
        finally
        {
            if (pdfStagingPath is not null && System.IO.File.Exists(pdfStagingPath)) System.IO.File.Delete(pdfStagingPath);
        }
    }

    private static async Task<IResult> ConnectorDiscardAsync(Guid grantId, ConnectorDiscardRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var auth = await ConnectorAuthAsync(grantId, http, db, ct); if (auth.Error is not null) return auth.Error; try { var now = DateTimeOffset.UtcNow; auth.Session!.Close(EditSessionState.Abandoned, request.ExpectedVersion, now, auth.Grant!.UserName, request.Reason ?? "Desktop checkout discarded."); auth.Grant.Revoke(now); db.ManagedDocumentEvents.Add(new(auth.Grant.DocumentId, "DocumentCheckoutDiscarded", auth.Grant.UserName, request.Reason ?? "Desktop checkout discarded without check-in.", now)); await db.SaveChangesAsync(ct); return Results.NoContent(); } catch (DomainException ex) { return Results.Conflict(new { error = ex.Message }); } }

    private static async Task<ConnectorAuth> ConnectorAuthAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    { var bearer = http.Request.Headers.Authorization.ToString(); if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return new(null, null, Results.Unauthorized()); var grant = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct); if (grant?.AccessTokenHash is null || !FixedEquals(grant.AccessTokenHash, Hash(bearer[7..].Trim()))) return new(null, null, Results.Unauthorized()); var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == grant.EditSessionId, ct); var now=DateTimeOffset.UtcNow;if(session is null)return new(grant,null,Results.Json(new{error="The desktop checkout session is unavailable.",code="connector_session_expired"},statusCode:409));if(grant.RevokedAt is not null||session.State!=EditSessionState.Active||session.ExpiresAt<=now||grant.ExpiresAt<=now){var code=session.State switch{EditSessionState.ForceUnlocked=>"connector_force_unlocked",EditSessionState.Conflict=>"document_snapshot_conflict",_=>"connector_session_expired"};return new(grant,session,Results.Json(new{error=$"The desktop checkout is {session.State}.",code},statusCode:409));} return new(grant, session, null); }

    private static async Task<DocumentConnectorGrant?> ConnectorGrantByTokenAsync(Guid grantId, HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var bearer = http.Request.Headers.Authorization.ToString(); if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var grant = await db.DocumentConnectorGrants.SingleOrDefaultAsync(x => x.Id == grantId, ct);
        return grant?.AccessTokenHash is not null && FixedEquals(grant.AccessTokenHash, Hash(bearer[7..].Trim())) ? grant : null;
    }

    private static async Task<List<ArtifactEditSession>> ActiveSessionsAsync(AeroLinkDbContext db, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    { var now = DateTimeOffset.UtcNow; var sessions = await db.ArtifactEditSessions.Where(x => x.ArtifactType == "ManagedDocument" && documentIds.Contains(x.ArtifactId) && x.State == EditSessionState.Active).ToListAsync(ct); foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now); if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct); return sessions.Where(x => x.State == EditSessionState.Active).ToList(); }

    private static async Task<RevisionData?> RevisionDataAsync(AeroLinkDbContext db, Guid revisionId, CancellationToken ct, bool includeReviews = false)
    { var query = db.ManagedDocumentRevisions.AsQueryable(); if (includeReviews) query = query.Include(x => x.ReviewSteps); var revision = await query.SingleOrDefaultAsync(x => x.Id == revisionId, ct); if (revision is null) return null; var document = await db.ManagedDocuments.SingleAsync(x => x.Id == revision.DocumentId, ct); return new(document, revision); }

    private static object Attachment(ControlledAttachment x) => new { x.Id, x.LogicalId, x.Version, x.Label, x.Description, x.OriginalFileName, x.ContentType, x.Size, x.Sha256, x.ValidationProfile, x.ValidationResult, state = x.State.ToString(), x.UploadedBy, x.UploadedAt, x.IntegrityVerifiedAt, downloadUrl = $"/api/managed-documents/attachments/{x.Id}" };
    private static ManagedDocumentSummary Summary(ManagedDocument document, IReadOnlyList<ManagedDocumentRevision> revisions, ArtifactEditSession? session)
    { var releasedHeads = revisions.Where(x => x.State == ManagedDocumentState.Released).ToList(); var released = releasedHeads.Count == 1 ? releasedHeads[0] : null; var inWorkHeads = revisions.Where(x => x.State is ManagedDocumentState.Draft or ManagedDocumentState.InReview or ManagedDocumentState.Returned).ToList(); var inWork = inWorkHeads.Count == 1 ? inWorkHeads[0] : null; var reconciliationRequired = releasedHeads.Count > 1 || inWorkHeads.Count > 1; return new(document.Id, document.DocumentNumber, document.Acronym, document.DocumentType, document.Title, document.StewardId, inWork?.ResponsibleOwnerId, released is null ? "None" : $"{document.DocumentNumber}.{released.Revision:D2}", reconciliationRequired ? "ReconciliationRequired" : released?.State.ToString() ?? "NotReleased", inWork is null ? null : $"{document.DocumentNumber}.{inWork.Revision:D2}", reconciliationRequired ? "ReconciliationRequired" : inWork?.State.ToString() ?? "None", inWork is null ? null : session?.UserName, inWork is null ? null : session?.ExpiresAt, reconciliationRequired, document.UpdatedAt); }

    private static ProfessionalPublication NewDraftPublication(ManagedDocument document, ManagedDocumentRevision revision, string project, string program)
    { var hash = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{document.DocumentNumber}|{revision.Revision}|{revision.FormalChangeSummary}")); return new ProfessionalPublication("AeroLink", program, project, document.DocumentType, document.Title, "Controlled Project document", document.DocumentNumber, revision.Revision.ToString("D2"), "Draft", "Project-wide", "All software builds", revision.ResponsibleOwnerId, revision.CreatedAt, hash, [("Document steward", document.StewardId), ("Revision responsible owner", revision.ResponsibleOwnerId), ("Revision initiated by", revision.InitiatedBy), ("Applicability", "Project-wide; build links are contextual traceability only"), ("Formal change summary", revision.FormalChangeSummary)], [], [(revision.Revision.ToString("D2"), "Draft", revision.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"), revision.ResponsibleOwnerId)], [new("1. Purpose and scope", "Complete this controlled Word template using the applicable project standard.", [new("1.1", "Author guidance", "Purpose", "State why this document exists, what it governs, and where its applicability begins and ends.", [("Status", "Draft")])]), new("2. Controlled content", "Replace the guidance below with the approved lifecycle content.", [new("2.1", "Author guidance", "Lifecycle content", "Identify responsibilities, inputs, activities, outputs, transition criteria, records and linked AeroLink artifacts.", [("Working format", "Macro-free Microsoft Word DOCX")])]), new("3. Review and release", "AeroLink records review evidence outside the editable document.", [new("3.1", "Release criteria", "Independent approval", "A technical reviewer and a separate final SQA or configuration approver must approve the exact release candidate.", [("Released formats", "DOCX and PDF")])])]) { Watermark = "DRAFT", ControlledStatusControls = true }; }

    private static async Task<(string Program, string Project)> ProjectContextAsync(AeroLinkDbContext db, Guid projectId, CancellationToken ct) => await (from project in db.Projects.AsNoTracking() join program in db.Programs.AsNoTracking() on project.ProgramId equals program.Id where project.Id == projectId select new ValueTuple<string, string>(program.Name, project.Name)).SingleAsync(ct);
    private static int NumberSequence(string value) => int.TryParse(value[(value.LastIndexOf('-') + 1)..], out var number) ? number : 0;
    private static async Task<RelationshipRevision> RelationshipRevisionAsync(Guid documentId, Guid revisionId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var document = await db.ManagedDocuments.SingleOrDefaultAsync(x => x.Id == documentId, ct); if (document is null) return new(null, null, Results.NotFound());
        if (!await http.HasProjectAccessAsync(db, document.ProjectId, ct)) return new(null, null, Results.Forbid());
        var revision = await db.ManagedDocumentRevisions.SingleOrDefaultAsync(x => x.Id == revisionId && x.DocumentId == documentId, ct); if (revision is null) return new(null, null, Results.BadRequest(new { error = "The selected revision does not belong to this document." }));
        var actor = http.UserAccount(); var owner = string.Equals(revision.ResponsibleOwnerId, actor.UserName, StringComparison.OrdinalIgnoreCase)
            && await ManagedDocumentAssignmentPolicy.IsEligibleAsync(db, identity, document.ProjectId, actor.UserName, DateTimeOffset.UtcNow, ct);
        var configurationAuthority = await ManagedDocumentAssignmentPolicy.HasExplicitAuthorityAsync(db, document.ProjectId, actor, DateTimeOffset.UtcNow, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead);
        if (!owner && !configurationAuthority) return new(null, null, Results.Forbid());
        if (revision.State is not (ManagedDocumentState.Draft or ManagedDocumentState.Returned)) return new(null, null, Results.Conflict(new { error = "Document relationships can change only while the formal revision is Draft or Returned.", code = "document_relationships_immutable" }));
        return new(document, revision, null);
    }

    private static async Task<CanonicalLinkTarget> ResolveLinkTargetAsync(string requestedType, Guid id, Guid projectId, AeroLinkDbContext db, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new DomainException("Choose an existing lifecycle artifact to link.");
        var type = ManagedDocumentRelationshipPolicy.CanonicalType(requestedType);
        var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => x.ProgramId).SingleAsync(ct);
        if (type == "ChangeRequest")
        {
            var row = await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || row.ProjectId != projectId) throw new DomainException("The linked artifact is not in this Project.");
            var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == row.TargetReleaseId, ct); var scope = row.Type == ChangeRequestType.Software ? "software" : "systems";
            return new(type, row.Id, row.DisplayNumber, row.Title, row.State.ToString(), row.ProjectId, row.TargetReleaseId, release.Version, $"/programs/{programId}/projects/{projectId}/releases/{release.Id}/{scope}/change-requests/{row.Id}");
        }
        if (type == "TestChangeRequest")
        {
            var row = await db.TestChangeReviews.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || row.ProjectId != projectId) throw new DomainException("The linked artifact is not in this Project.");
            var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == row.ReleaseId, ct); var branch = row.Discipline switch { TestChangeReviewDiscipline.System => "system-verification", TestChangeReviewDiscipline.HighLevelSoftware => "software-verification/hlr", _ => "software-verification/llr" };
            return new(type, row.Id, row.DisplayNumber, $"Test change for {row.SourceChangeRequestNumber}", row.State.ToString(), row.ProjectId, row.ReleaseId, release.Version, $"/programs/{programId}/projects/{projectId}/releases/{release.Id}/{branch}/coverage/{row.Id}");
        }
        if (type == "ProblemReport")
        {
            var row = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || row.ProjectId != projectId) throw new DomainException("The linked artifact is not in this Project.");
            var release = row.TargetReleaseId is null ? await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.IsReleased).ThenByDescending(x => x.Version).FirstOrDefaultAsync(ct) : await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.TargetReleaseId, ct);
            var releaseId = release?.Id; var deepLink = releaseId is null ? $"/projects/{projectId}/problem-reports/{row.Id}" : $"/programs/{programId}/projects/{projectId}/releases/{releaseId}/problem-reports/{row.Id}";
            return new(type, row.Id, row.DisplayNumber, row.Title, row.State.ToString(), row.ProjectId, row.TargetReleaseId, row.TargetReleaseId is null ? "" : release?.Version ?? "", deepLink);
        }
        var build = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (build is null || build.ProjectId != projectId) throw new DomainException("The linked artifact is not in this Project.");
        return new(type, build.Id, $"BUILD-{build.Version}", $"Build {build.Version}", build.IsReleased ? "Released" : "InWork", build.ProjectId, build.Id, build.Version, $"/programs/{programId}/projects/{projectId}/releases/{build.Id}/command-center");
    }

    private static ManagedDocumentLink NewCanonicalLink(Guid revisionId, CanonicalLinkTarget target, string relationship, string actor, DateTimeOffset now) =>
        new(revisionId, target.ArtifactType, target.ArtifactId, target.DisplayNumber, target.Title, target.State, target.ProjectId,
            target.ReleaseId, target.ReleaseVersion, target.DeepLink, relationship, actor, now);
    private static object LinkResult(ManagedDocumentLink link) => new { link.Id, link.ArtifactType, link.ArtifactId, link.DisplayNumber, link.CanonicalTitle, link.TargetState, link.TargetProjectId, link.TargetReleaseId, link.TargetReleaseVersion, link.DeepLink, link.Relationship, link.PolicyVersion, link.Provenance, link.IsCurrent, link.CreatedBy, link.CreatedAt };
    private static IResult? ValidateOperationKey(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100
        ? Results.BadRequest(new { error = "A one-use operation key of 100 characters or fewer is required.", code = "operation_key_required" }) : null;
    private static IResult? ValidateReviewDecision(DocumentReviewDecisionRequest request)
    {
        var operation = ValidateOperationKey(request.OperationKey); if (operation is not null) return operation;
        if (string.IsNullOrWhiteSpace(request.Meaning) || request.Meaning.Trim().Length > 1000)
            return Results.BadRequest(new { error = "Provide an explicit signature meaning of 1000 characters or fewer.", code = "signature_meaning_required" });
        if (string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Trim().Length > 4000)
            return Results.BadRequest(new { error = "Provide an engineering rationale of 4000 characters or fewer.", code = "signature_rationale_required" });
        if (request.ExpectedStepId == Guid.Empty || request.ExpectedCycle < 1 || request.ExpectedStepVersion < 1 || request.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(request.ExpectedSnapshotHash))
            return Results.BadRequest(new { error = "The exact review version, cycle, step, and snapshot evidence are required.", code = "review_intent_required" });
        return null;
    }
    private static bool IsStaleReview(DomainException ex) => ex.Message.Contains("after this page loaded", StringComparison.OrdinalIgnoreCase)
        || ex.Message.StartsWith("The release candidate changed", StringComparison.OrdinalIgnoreCase);
    private static string OperationPayloadHash(string operationType, object request) =>
        ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"managed-document-operation-v1:{operationType}:{JsonSerializer.Serialize(request)}"));
    private static object ReviewDecisionPayload(DocumentReviewDecisionRequest request) => new
    {
        request.Meaning, request.Rationale, request.ExpectedVersion, request.ExpectedCycle, request.ExpectedStepId,
        request.ExpectedStepVersion, request.ExpectedSnapshotHash, request.ExpectedCandidateDocxAttachmentId,
        request.ExpectedCandidatePdfAttachmentId, request.ExpectedCandidateManifestHash, request.OperationKey
    };
    private static async Task<IResult?> OperationResultAsync(AeroLinkDbContext db, Guid revisionId, string operationType,
        string operationKey, string payloadHash, CancellationToken ct)
    {
        var prior = await db.ManagedDocumentOperations.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revisionId && x.OperationType == operationType && x.OperationKey == operationKey, ct);
        if (prior is null) return null;
        return string.Equals(prior.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase)
            ? Results.Content(prior.ResultJson, "application/json")
            : Results.Conflict(new { error = "That operation key was already used for different review intent.", code = "operation_key_reused" });
    }
    private static bool IsManagedDocumentOperationKeyConflict(DbUpdateException ex) =>
        ex.ToString().Contains("managed_document_operations", StringComparison.OrdinalIgnoreCase)
        && (ex.ToString().Contains("unique", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("23505", StringComparison.OrdinalIgnoreCase));
    private static ManagedDocumentStagedObject StorageObject(string slot, ControlledAttachment attachment, StagedEvidence staged) =>
        new(slot, attachment.Id, staged.StagingKey, staged.StorageKey, staged.Size, staged.Sha256);
    private static async Task RollBackStorageAsync(AeroLinkDbContext db, ManagedDocumentStorageCoordinator storage,
        Guid operationId, string detail, string actor)
    {
        try
        {
            db.ChangeTracker.Clear();
            var operation = await db.ManagedDocumentStorageOperations.SingleOrDefaultAsync(x => x.Id == operationId, CancellationToken.None);
            if (operation is not null && operation.State != ManagedDocumentStorageOperationState.Available)
            {
                operation.RecordFailure(detail, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                // Reconcile from durable metadata instead of blindly deleting files. The metadata
                // transaction may have committed immediately before the request observed a fault.
                await storage.ReconcileAbandonedOperationAsync(operation, actor, DateTimeOffset.UtcNow, CancellationToken.None);
            }
        }
        catch { /* The durable Pending record and staged names remain available to the reconciler. */ }
    }
    private static IResult IntegrityFailure(ManagedDocumentIntegrityFailure failure) => Results.Json(new
    {
        error = failure.Message,
        code = "document_integrity_blocked",
        reason = failure.Code,
        failure.AttachmentId
    }, statusCode: StatusCodes.Status409Conflict);
    private static IResult ReviewConflict(string code, string error, ManagedDocumentRevision revision) => Results.Conflict(new
    {
        error, code, current = new { revision.Version, cycle = revision.CurrentReviewCycle, revision.SnapshotHash, revision.SubmittedFormalSummaryHash, revision.SubmittedFormalSummaryVersion, revision.SubmittedRelationshipManifestHash, revision.ReleaseCandidateDocxAttachmentId, revision.ReleaseCandidatePdfAttachmentId, revision.ReleaseManifestHash }
    });
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private sealed record RevisionData(ManagedDocument Document, ManagedDocumentRevision Revision);
    private sealed record ConnectorAuth(DocumentConnectorGrant? Grant, ArtifactEditSession? Session, IResult? Error);
    private sealed record RelationshipRevision(ManagedDocument? Document, ManagedDocumentRevision? Revision, IResult? Error);
    private sealed record CanonicalLinkTarget(string ArtifactType, Guid ArtifactId, string DisplayNumber, string Title, string State, Guid ProjectId, Guid? ReleaseId, string ReleaseVersion, string DeepLink);
    private sealed record ManagedDocumentSummary(Guid Id, string DocumentNumber, string Acronym, string DocumentType, string Title, string StewardId, string? ResponsibleOwnerId, string ReleasedRevision, string ReleasedState, string? InWorkRevision, string InWorkState, string? CheckedOutBy, DateTimeOffset? CheckoutExpiresAt, bool ReconciliationRequired, DateTimeOffset UpdatedAt);
}

public sealed record CreateManagedDocumentRequest(Guid ProjectId, string Acronym, string DocumentType, string Title, string? OwnerId, string? FormalChangeSummary, string? ChangeSummary = null, string? OperationKey = null);
public sealed record StartManagedDocumentRevisionRequest(string? FormalChangeSummary, string? ChangeSummary = null, string? OwnerId = null, string? OperationKey = null);
public sealed record SubmitManagedDocumentRequest(string TechnicalReviewerId, string FinalApproverId, long ExpectedVersion,
    Guid ExpectedWorkingAttachmentId, string ExpectedWorkingSha256, long ExpectedFormalSummaryVersion,
    string ExpectedFormalSummaryHash, string ExpectedRelationshipManifestHash, string OperationKey);
public sealed record ReviseManagedDocumentFormalSummaryRequest(string FormalChangeSummary, string Reason, long ExpectedVersion);
public sealed record ReassignManagedDocumentRequest(string AssigneeId, string Reason, long ExpectedVersion);
public sealed record DocumentReviewDecisionRequest(string Password, string Meaning, string Rationale, long ExpectedVersion,
    int ExpectedCycle, Guid ExpectedStepId, long ExpectedStepVersion, string ExpectedSnapshotHash,
    Guid? ExpectedCandidateDocxAttachmentId, Guid? ExpectedCandidatePdfAttachmentId,
    string? ExpectedCandidateManifestHash, string OperationKey);
public sealed record ForceUnlockManagedDocumentRequest(string Reason);
public sealed record WithdrawManagedDocumentRevisionRequest(string Reason, long ExpectedVersion);
public sealed record ManagedDocumentLinkRequest(Guid RevisionId, string ArtifactType, Guid ArtifactId, string? DisplayNumber, string Relationship, long ExpectedVersion);
public sealed record CorrectManagedDocumentLinkRequest(string ArtifactType, Guid ArtifactId, string Relationship, string Reason, long ExpectedVersion);
public sealed record SupersedeManagedDocumentLinkRequest(string Reason, long ExpectedVersion);
public sealed record ConnectorVersionRequest(long ExpectedVersion);
public sealed record ConnectorDiscardRequest(long ExpectedVersion, string? Reason);
public sealed record RecoverDocumentConnectorRequest(Guid WorkspaceId);
