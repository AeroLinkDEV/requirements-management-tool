using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Release campaigns, and the controlled documents a release produces.
///
/// A campaign is the assembly of a release — gates, readiness, approvals — and the generated publications
/// that become the record of what was released.
/// </summary>
public static class ReleaseCampaignEndpoints
{
    public static void MapReleaseCampaignEndpoints(this WebApplication app)
    {
        app.MapGet("/api/documents", async (Guid projectId, Guid? releaseId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var rows = await (from document in db.ControlledDocuments.AsNoTracking().Where(x => x.ProjectId == projectId && (releaseId == null || x.ReleaseId == releaseId))
                              join release in db.Releases.AsNoTracking() on document.ReleaseId equals release.Id
                              join baseline in db.CandidateBaselines.AsNoTracking() on document.BaselineId equals baseline.Id
                              orderby document.Type select new { document.Id, type = document.Type.ToString(), document.DocumentNumber, document.Revision, document.Title,
                                  document.ContentHash, document.ArtifactCount, document.GeneratedAt, release = release.Version,
                                  baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, x.type, displayNumber = x.DocumentNumber + "." + x.Revision.ToString("D2"), x.Title, x.ContentHash, x.ArtifactCount, x.GeneratedAt, x.release, x.baselineId, x.baseline }));
        });

        app.MapGet("/api/documents/{id:guid}/download", async (Guid id, string? format, HttpContext http, AeroLinkDbContext db, ControlledOutputGenerator generator, CancellationToken ct) =>
        {
            var projectId = await db.ControlledDocuments.Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct); if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var output = await generator.GenerateAsync(id, format ?? "docx", ct); return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
        });

        // The document a release is heading towards, before it is frozen. Generated on demand from the released
        // baseline plus every approved change, watermarked DRAFT, and never persisted — see DraftDocumentGenerator
        // for why a controlled record of content that is still moving would be a record of nothing.
        app.MapGet("/api/releases/{releaseId:guid}/draft-document", async (Guid releaseId, string type, string? format, HttpContext http, AeroLinkDbContext db, DraftDocumentGenerator generator, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            if (!Enum.TryParse<ControlledDocumentType>(type, true, out var documentType))
                return Results.BadRequest(new { error = "Unknown document type.", code = "unknown_document_type" });
            var output = await generator.GenerateAsync(releaseId, documentType, format ?? "docx", http.UserAccount().DisplayName, ct);
            return output is null
                ? Results.BadRequest(new { error = "A draft is only produced for a requirements document.", code = "draft_not_available" })
                : Results.File(output.Content, output.ContentType, output.FileName);
        });

        app.MapGet("/api/documents/{id:guid}/manifest",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var document=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(document is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,document.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==document.BaselineId,ct);var release=await db.Releases.AsNoTracking().SingleAsync(x=>x.Id==document.ReleaseId,ct);var approvals=await(from approval in db.ReleaseApprovals.AsNoTracking() join campaign in db.ReleaseCampaigns.AsNoTracking() on approval.CampaignId equals campaign.Id where campaign.ProjectId==document.ProjectId&&campaign.ReleaseId==document.ReleaseId&&campaign.BaselineId==document.BaselineId&&approval.ApprovedAt!=null&&approval.ApprovedAt<=document.GeneratedAt select new{campaignId=campaign.Id,approval.ApproverId,approval.ApproverName,state=approval.State.ToString(),approval.ApprovedAt}).OrderBy(x=>x.ApprovedAt).ToListAsync(ct);return Results.Ok(new{format="AeroLink controlled-publication-manifest/v1",document=new{document.Id,document.DocumentNumber,document.Revision,document.Title,type=document.Type.ToString(),document.ContentHash,document.ArtifactCount,document.GeneratedAt},source=new{baseline=baseline.DisplayNumber,baseline.ContentHash,baseline.RequirementsHash,release=release.Version},approvalEvidence=approvals,reproducibility=new{renderer="AeroLink professional publication renderer",contentHash=document.ContentHash,deterministic=true}});
        });

        app.MapGet("/api/release-campaigns", async (Guid projectId, Guid? releaseId, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
        {
            var campaigns = (await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId && (releaseId == null || x.ReleaseId == releaseId)).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList(); var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
            var output = new List<object>(); foreach (var campaign in campaigns) { var status = await readiness.CalculateAsync(campaign.Id, ct); output.Add(new { campaign.Id, campaign.Name, state = campaign.State.ToString(), campaign.ReleaseId, release = releases[campaign.ReleaseId].Version, campaign.BaselineId, campaign.SoftwareBuildId, campaign.OwnerId, campaign.CreatedAt, campaign.ReleasedAt, campaign.ReleaseHash, readiness = status }); }
            return Results.Ok(output);
        });

        app.MapPost("/api/release-campaigns", async (CreateReleaseCampaignRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            if (await db.ReleaseCampaigns.AnyAsync(x => x.ProjectId == request.ProjectId && x.ReleaseId == request.ReleaseId, ct)) return Results.Conflict(new { error = "This release already has a campaign." });
            var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId && !x.IsReleased, ct);
            var baseline = await db.CandidateBaselines.SingleOrDefaultAsync(x => x.Id == request.BaselineId && x.ProjectId == request.ProjectId && x.ReleaseId == request.ReleaseId, ct);
            if (release is null || baseline is null) return Results.BadRequest(new { error = "Choose an unreleased version and one of its candidate baselines." });
            try
            {
                var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
                var campaign = new ReleaseCampaign(request.ProjectId, request.ReleaseId, request.BaselineId, request.Name, actor.UserName, now); db.ReleaseCampaigns.Add(campaign);
                var changes = await db.SystemChangeRequests.Where(x => x.TargetReleaseId == request.ReleaseId).ToListAsync(ct);
                foreach (var change in changes) foreach (var kind in Enum.GetValues<ImpactKind>())
                    db.ImpactDispositions.Add(new ChangeImpactDisposition(campaign.Id, change.Id, kind, change.DisplayNumber, $"Disposition {kind.ToString().ToLowerInvariant()} impact for {change.DisplayNumber}."));
                await db.SaveChangesAsync(ct); return Results.Created($"/api/release-campaigns/{campaign.Id}", new { campaign.Id, campaign.ReleaseId, campaign.BaselineId, campaign.Name, state = campaign.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/start-verification", async (Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            try { campaign.StartVerification(http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/release-campaigns/{id:guid}", async (Guid id, AeroLinkDbContext db, ReleaseReadinessService readiness, BuildTestSetService testSets, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            // The build's test sets are created the first time somebody looks at its readiness, and carry
            // forward every procedure the old "evidence required before release" checkbox had pointed at.
            // Done here rather than when a build is created, because a build that predates the test set has
            // to acquire one anyway and both cases then collapse into the same path.
            await testSets.EnsureForReleaseAsync(campaign.ProjectId, campaign.ReleaseId, ct);
            var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == campaign.ReleaseId, ct); var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
            var impacts = await (from impact in db.ImpactDispositions.AsNoTracking().Where(x => x.CampaignId == id) join scr in db.SystemChangeRequests.AsNoTracking() on impact.ChangeRequestId equals scr.Id orderby scr.BaseNumber, impact.Kind select new { impact.Id, impact.ChangeRequestId, scr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, scr.Title, kind = impact.Kind.ToString(), impact.ArtifactReference, impact.Description, state = impact.State.ToString(), impact.Rationale, impact.DispositionedBy, impact.DispositionedAt }).ToListAsync(ct);
            var changes = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == campaign.ReleaseId).OrderBy(x => x.BaseNumber).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title, type = x.Type.ToString(), state = x.State.ToString(), x.AuthorId, requirementCount = x.RequirementChanges.Count, included = db.BaselineSelections.Any(s => s.BaselineId == baseline.Id && s.ChangeRequestId == x.Id) }).ToListAsync(ct);
            return Results.Ok(new { campaign.Id, campaign.Name, state = campaign.State.ToString(), campaign.ProjectId, campaign.ReleaseId, release = release.Version, campaign.BaselineId, baseline = baseline.DisplayNumber, baselineState = baseline.State.ToString(), baseline.RequirementsHash, campaign.SoftwareBuildId, campaign.OwnerId, campaign.CreatedAt, campaign.ReleasedAt, campaign.ReleaseHash,
                readiness = await readiness.CalculateAsync(id, ct), changes, impacts, approvals = campaign.Approvals.OrderBy(x => x.Position).Select(x => new { x.Position, x.ApproverId, x.ApproverName, state = x.State.ToString(), x.ApprovedAt }), events = campaign.Events.OrderByDescending(x => x.OccurredAt).Select(x => new { x.EventType, x.ActorId, x.Detail, x.OccurredAt }) });
        });

        app.MapPut("/api/release-campaigns/{id:guid}/impact-dispositions", async (Guid id, BulkDispositionImpactRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            if (campaign.State == ReleaseCampaignState.InReview) return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (campaign.State == ReleaseCampaignState.Released) return Results.BadRequest(new { error = "A released campaign is immutable." });
            var impacts = await db.ImpactDispositions.Where(x => x.CampaignId == id && x.State == ImpactDispositionState.Pending && (request.ChangeRequestId == null || x.ChangeRequestId == request.ChangeRequestId)).ToListAsync(ct);
            if (impacts.Count == 0) return Results.BadRequest(new { error = "No pending impacts match this disposition." });
            try { foreach (var impact in impacts) impact.Disposition(request.State, request.Rationale, http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.Ok(new { dispositioned = impacts.Count }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/reconcile-lifecycle-links", async (Guid id, EmptyMutationRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            var projectId = await db.ReleaseCampaigns.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct); if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, projectId.Value, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            try { return Results.Ok(await execution.ReconcileAsync(id, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct)); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/release-campaigns/{id:guid}/verification-template", async (Guid id, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            try { return Results.File(await execution.CreateVerificationTemplateAsync(id, ct), "application/json", "verification-manifest-template.json"); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/verification-package", async (Guid id, HttpRequest http, AeroLinkDbContext db, IdentityService identity, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            var projectId = await db.ReleaseCampaigns.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct); if (projectId is null) return Results.NotFound();
            if (!await http.HttpContext.HasProjectRoleAsync(db, identity, projectId.Value, ct, ProgramRole.TestEngineer)) return Results.Forbid();
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Use multipart form data with manifest and evidence files." });
            var form = await http.ReadFormAsync(ct); var manifest = form.Files.GetFile("manifest"); var evidence = form.Files.GetFile("evidence"); var actorId = http.HttpContext.UserAccount().UserName;
            if (manifest is null || evidence is null || manifest.Length == 0 || evidence.Length == 0) return Results.BadRequest(new { error = "Both a completed JSON manifest and an evidence package are required." });
            if (manifest.Length > 10 * 1024 * 1024) return Results.BadRequest(new { error = "Verification manifests are limited to 10 MB." });
            try { await using var manifestStream = manifest.OpenReadStream(); await using var evidenceStream = evidence.OpenReadStream(); return Results.Ok(await execution.ImportVerificationAsync(id, manifestStream, evidenceStream, evidence.FileName, evidence.ContentType, actorId, DateTimeOffset.UtcNow, ct)); }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();

        app.MapPost("/api/release-campaigns/{id:guid}/verification-build", async (Guid id, SelectBuildRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
            if (!await db.SoftwareBuilds.AnyAsync(x => x.Id == request.SoftwareBuildId && x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId, ct)) return Results.BadRequest(new { error = "Select a software build from this campaign release." });
            try { campaign.SelectVerificationBuild(request.SoftwareBuildId, http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/review", async (Guid id, StartReleaseReviewRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseReadinessService readiness, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound(); var status = await readiness.CalculateAsync(id, ct);
            if(!await http.HasProjectRoleAsync(db,identity,campaign.ProjectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager))return Results.Forbid();
            var blockers = status.Gates.Where(x => x.Code != "release_approval" && !x.Complete).Select(x => x.Name).ToList(); if (blockers.Count > 0) return Results.BadRequest(new { error = "Resolve readiness gates before release review.", blockers });
            try
            {
                var requested=request.Approvers.Select(a=>a.UserId.Trim().ToLowerInvariant()).ToList();
                var known = await db.UserAccounts.AsNoTracking().Where(x => requested.Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every release approver must be a distinct active AeroLink user." });
                var programId=await db.Projects.AsNoTracking().Where(x=>x.Id==campaign.ProjectId).Select(x=>x.ProgramId).SingleAsync(ct);
                foreach(var approver in known)if(!await identity.HasRoleAsync(approver.Id,programId,ProgramRole.Approver,DateTimeOffset.UtcNow,ct))return Results.BadRequest(new{error=$"{approver.DisplayName} does not hold Approver authority for this Program."});
                var manifestHash=await execution.ComputeReviewManifestHashAsync(id,ct);
                campaign.BeginReleaseReview(http.UserAccount().UserName, requested.Select(userName=>{var person=known.Single(x=>x.UserName==userName);return(person.UserName,person.DisplayName);}).ToList(),manifestHash,DateTimeOffset.UtcNow);
                // Existing approvals belong to the cancelled cycle and must stay Unchanged. The fresh rows
                // have never been persisted; once DetectChanges discovers them through the campaign
                // collection EF treats application-assigned keys as existing (Modified) and would UPDATE
                // rows that do not exist. Capture the persisted approval ids before review starts and
                // explicitly Add every newly created approval (Add also corrects a premature Modified
                // attachment back to Added).
                var existingApprovalIds = db.ChangeTracker.Entries<ReleaseApproval>().Select(e => e.Entity.Id).ToHashSet();
                foreach (var approval in campaign.Approvals.Where(x => !existingApprovalIds.Contains(x.Id)))
                    db.ReleaseApprovals.Add(approval);
                await db.SaveChangesAsync(ct); return Results.Ok(new{manifestHash});
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Results.Conflict(new { error = "The release campaign changed while review was starting. Reload and retry.", code = "release_campaign_conflict" }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/review/cancel", async (Guid id, CancelReleaseReviewRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                campaign.CancelReleaseReview(http.UserAccount().UserName, request.Reason, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { state = campaign.State.ToString(), manifestHash = campaign.ReleaseHash });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Results.Conflict(new { error = "The release campaign changed concurrently. Reload before cancelling review.", code = "release_campaign_conflict" }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/approve", async (Guid id, ReleaseSignatureRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if(string.IsNullOrWhiteSpace(request.Meaning))return Results.BadRequest(new{error="An explicit electronic signature meaning is required."});
            if(string.IsNullOrWhiteSpace(campaign.ReleaseHash)||campaign.ReleaseHash.Length!=64)return Results.Conflict(new{error="Release review is not bound to a valid package manifest.",code="release_manifest_missing"});
            if(string.IsNullOrWhiteSpace(request.ExpectedManifestHash)||request.ExpectedManifestHash.Length!=64
                ||!string.Equals(request.ExpectedManifestHash,campaign.ReleaseHash,StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new{error="The release package you reviewed has changed. Reload the release package before approving.",code="stale_release_package",currentManifestHash=campaign.ReleaseHash});
            var currentHash=await execution.ComputeReviewManifestHashAsync(id,ct);
            if(!string.Equals(currentHash,campaign.ReleaseHash,StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new{error="The release package changed after review began. Cancel and restart release review against the current manifest.",code="release_manifest_changed",reviewedManifestHash=campaign.ReleaseHash,currentManifestHash=currentHash});
            var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            var programId = await db.Projects.Where(x => x.Id == campaign.ProjectId).Select(x => x.ProgramId).SingleAsync(ct); if (!await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct)) return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var active = campaign.Approvals.SingleOrDefault(x => x.State == ReleaseApprovalState.Active);
                if (active is null)
                {
                    // A lost response is retried with the exact same intent. The recorded signature for this
                    // actor and package is the idempotency evidence; nothing new is written.
                    var priorSignatures = await db.ElectronicSignatures.AsNoTracking()
                        .Where(x => x.ArtifactId == campaign.Id && x.UserName == actor.UserName
                            && x.Action == "ApproveRelease" && x.ContentHash == request.ExpectedManifestHash)
                        .ToListAsync(ct);
                    var prior = priorSignatures.OrderByDescending(x => x.SignedAt).FirstOrDefault();
                    if (prior is not null)
                    {
                        if (!string.Equals(prior.Meaning, request.Meaning, StringComparison.OrdinalIgnoreCase))
                            return Results.Conflict(new { error = "The release approval for this package was already recorded with a different meaning.", code = "decision_already_recorded" });
                        return Results.Ok(new { complete = !campaign.Approvals.Any(x => x.State == ReleaseApprovalState.Pending), manifestHash = campaign.ReleaseHash });
                    }
                    return Results.Conflict(new { error = "No release approval is active for this request.", code = "release_approval_not_active" });
                }
                var position = active.Position;
                var complete = campaign.Approve(actor.UserName, now);
                db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ReleaseCampaign", campaign.Id, campaign.Name, "ApproveRelease", request.Meaning, campaign.ReleaseHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now,
                    reviewStepPosition: position, reviewCycle: active.Cycle, rationale: request.Rationale ?? ""));
                await db.SaveChangesAsync(ct); return Results.Ok(new { complete, manifestHash = campaign.ReleaseHash });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Results.Conflict(new { error = "The release approval changed concurrently. Reload the release package before deciding.", code = "approval_step_conflict" }); }
        });

        app.MapPost("/api/release-campaigns/{id:guid}/release", async (Guid id, EmptyMutationRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseReadinessService readiness, ReleaseExecutionService execution, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct); var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            var status = await readiness.CalculateAsync(id, ct); if (!status.ReadyForRelease) return Results.BadRequest(new { error = "Every release-readiness gate must be complete.", blockers = status.Gates.Where(x => !x.Complete).Select(x => x.Name) });
            if (campaign.SoftwareBuildId is null) return Results.BadRequest(new { error = "Select the verified release build." });
            var baseline = await db.CandidateBaselines.Include(x => x.Events).SingleAsync(x => x.Id == campaign.BaselineId, ct); var release = await db.Releases.SingleAsync(x => x.Id == campaign.ReleaseId, ct); var build = await db.SoftwareBuilds.SingleAsync(x => x.Id == campaign.SoftwareBuildId, ct);
            var hash=await execution.ComputeReviewManifestHashAsync(id,ct);if(!string.Equals(campaign.ReleaseHash,hash,StringComparison.OrdinalIgnoreCase))return Results.Conflict(new{error="The release package changed after review began. Cancel and restart release review against the current manifest.",code="release_manifest_changed",reviewedManifestHash=campaign.ReleaseHash,currentManifestHash=hash});
            try { var actor = http.UserAccount().UserName; campaign.Release(build.Id, hash, actor, DateTimeOffset.UtcNow); baseline.MarkReleased(actor, DateTimeOffset.UtcNow); release.MarkReleased(DateTimeOffset.UtcNow); build.MarkReleased(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Ok(new { release = release.Version, build.BuildNumber, releaseHash = hash }); }
            catch (DbUpdateConcurrencyException) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return Results.Conflict(new { error = "The release package changed concurrently. Reload before releasing.", code = "release_campaign_conflict" }); }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
}
