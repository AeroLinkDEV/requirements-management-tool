using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// Verification: procedures, executions, evidence, and the trace links that connect a
/// requirement to whatever demonstrates it.
///
/// AeroLink records a determination somebody made. It never runs a test and never decides an outcome.
/// </summary>
public static class VerificationEndpoints
{
    public static void MapVerificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/traceability/{baselineId:guid}/download", async (Guid baselineId,string? format,HttpContext http,AeroLinkDbContext db,ControlledOutputGenerator generator,CancellationToken ct) =>
        {
            var projectId=await db.CandidateBaselines.Where(x=>x.Id==baselineId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var output=await generator.GenerateTraceabilityAsync(baselineId,format??"pdf",ct);return output is null?Results.NotFound():Results.File(output.Content,output.ContentType,output.FileName);
        });

        // Dormant software Procedure authoring deliberately lives beside the shared verification aggregate,
        // but outside the Test Change Request workflow.  This establishes the reusable content/parent seam;
        // #725 can add its governed package boundary without inventing a second Procedure identity store.
        app.MapPost("/api/test-procedures/drafts", async (CreateDormantProcedureRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, VerificationProcedureAuthoringService authoring,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead)) return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var created = await authoring.CreateAsync(request.ProjectId, request.Level, request.Title,
                    http.UserAccount().UserName,
                    new VerificationProcedureContent(request.EnvironmentSetup, request.TestData,
                        request.OrderedSteps, request.ExpectedObservations, request.Cleanup,
                        request.ToolingAutomation, request.Objective, request.Preconditions),
                    request.ParentKind, request.CaseRevisionIds, request.DerivedRationale, now, ct);
                return Results.Created($"/api/test-procedures/{created.Artifact.Id}", new
                {
                    id = created.Artifact.Id, revisionId = created.Revision.Id,
                    displayNumber = $"{created.Artifact.BaseNumber}.{created.Revision.Revision:D2}",
                    baseNumber = created.Artifact.BaseNumber, level = created.Artifact.Level.ToString(),
                    artifactKind = created.Artifact.ArtifactKind.ToString(), state = created.Revision.State.ToString(),
                    version = created.Artifact.Version
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-procedures/{id:guid}/revisions", async (Guid id,
            ReviseDormantProcedureRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, VerificationProcedureAuthoringService authoring, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.ProjectId, x.Version }).SingleOrDefaultAsync(ct);
            if (procedure is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, procedure.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead)) return Results.Forbid();
            if (request.ExpectedVersion is null)
                return Results.BadRequest(new { error = "The Procedure version is required when revising.", code = "verification_procedure_expected_version_required" });
            if (procedure.Version != request.ExpectedVersion.Value)
                return Results.Conflict(new { error = "The Procedure changed after it was opened. Refresh before revising.", code = "verification_procedure_concurrency_conflict" });
            try
            {
                var revision = await authoring.ReviseAsync(id, http.UserAccount().UserName,
                    new VerificationProcedureContent(request.EnvironmentSetup, request.TestData,
                        request.OrderedSteps, request.ExpectedObservations, request.Cleanup,
                        request.ToolingAutomation, request.Objective, request.Preconditions),
                    request.ParentKind, request.CaseRevisionIds, request.DerivedRationale,
                    DateTimeOffset.UtcNow, ct, request.ExpectedVersion.Value);
                return Results.Created($"/api/test-procedures/{id}/history?revisionId={revision.Id}", new
                {
                    id, revisionId = revision.Id, revision = revision.Revision,
                    state = revision.State.ToString(), parentKind = revision.ParentKind.ToString(),
                    version = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id).Select(x => x.Version).SingleAsync(ct)
                });
            }
            catch (VerificationProcedureConcurrencyException ex) { return Results.Conflict(new { error = ex.Message, code = "verification_procedure_concurrency_conflict" }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The Procedure changed while it was being revised; reload its current revision and retry.", code = "verification_procedure_concurrency_conflict" }); }
        });

        app.MapPost("/api/test-procedures/{id:guid}/retire", async (Guid id,
            RetireDormantProcedureRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, VerificationProcedureAuthoringService authoring, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.ProjectId, x.Version }).SingleOrDefaultAsync(ct);
            if (procedure is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, procedure.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead)) return Results.Forbid();
            if (request.ExpectedVersion is null)
                return Results.BadRequest(new { error = "The Procedure version is required when retiring.", code = "verification_procedure_expected_version_required" });
            if (procedure.Version != request.ExpectedVersion.Value)
                return Results.Conflict(new { error = "The Procedure changed after it was opened. Refresh before retiring.", code = "verification_procedure_concurrency_conflict" });
            try
            {
                var revision = await authoring.RetireAsync(id, http.UserAccount().UserName,
                    request.Rationale, DateTimeOffset.UtcNow, ct, request.ExpectedVersion.Value);
                return Results.Ok(new { id, revisionId = revision.Id, revision = revision.Revision,
                    state = revision.State.ToString(), retirementRationale = revision.RetirementRationale,
                    version = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id).Select(x => x.Version).SingleAsync(ct) });
            }
            catch (VerificationProcedureConcurrencyException ex) { return Results.Conflict(new { error = ex.Message, code = "verification_procedure_concurrency_conflict" }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "The Procedure changed while it was being retired; reload its current revision and retry.", code = "verification_procedure_concurrency_conflict" }); }
        });

        app.MapPost("/api/evidence", async (HttpRequest http, AeroLinkDbContext db, IdentityService identity, EvidenceFileStore store, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Use multipart form data." }); var form = await http.ReadFormAsync(ct); var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a non-empty evidence file." }); if (!Guid.TryParse(form["projectId"], out var projectId) || !await db.Projects.AnyAsync(x => x.Id == projectId, ct)) return Results.BadRequest(new { error = "A valid project is required." }); var uploadedBy = http.HttpContext.UserAccount().UserName;
            if (!await http.HttpContext.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.TestEngineer)) return Results.Forbid();
            try { await using var stream = file.OpenReadStream(); var stored = await store.StoreAsync(stream, file.FileName, file.ContentType, ct); var evidence = new EvidenceRecord(projectId, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, uploadedBy, DateTimeOffset.UtcNow); db.EvidenceRecords.Add(evidence); await db.SaveChangesAsync(ct); return Results.Created($"/api/evidence/{evidence.Id}", new { evidence.Id, evidence.OriginalFileName, evidence.ContentType, evidence.Size, evidence.Sha256, evidence.UploadedBy, evidence.UploadedAt }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();

        app.MapPost("/api/test-executions/{executionId:guid}/evidence/{evidenceId:guid}", async (Guid executionId, Guid evidenceId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var execution = await db.TestExecutions.AsNoTracking().Where(x => x.Id == executionId).Select(x => new { x.ProjectId, x.SoftwareBuildId }).SingleOrDefaultAsync(ct);
            var evidenceProject = await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == evidenceId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (execution is null || evidenceProject is null) return Results.NotFound();
            if (execution.ProjectId != evidenceProject) return Results.BadRequest(new { error = "Evidence and execution must belong to the same project." });
            if (!await http.HasProjectRoleAsync(db, identity, execution.ProjectId, ct, ProgramRole.TestEngineer)) return Results.Forbid();
            if (execution.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == execution.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (await db.TestExecutionEvidence.AnyAsync(x => x.TestExecutionId == executionId && x.EvidenceId == evidenceId, ct)) return Results.Conflict(new { error = "Evidence is already linked." }); db.TestExecutionEvidence.Add(new TestExecutionEvidence(executionId, evidenceId)); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapGet("/api/evidence/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct) =>
        {
            var projectId = await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == id)
                .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var evidence = await db.EvidenceRecords.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.File(store.OpenRead(evidence.StorageKey), evidence.ContentType, evidence.OriginalFileName,
                enableRangeProcessing: true);
        });

        app.MapPost("/api/trace-links", async (CreateTraceLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.Id == request.SourceRevisionId || x.Id == request.TargetRevisionId)
                                   join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                   select new { revision.Id, artifact.ProjectId, artifact.Level }).ToListAsync(ct);
            if (revisions.Count != 2) return Results.BadRequest(new { error = "Both exact requirement revisions must exist." });
            if (revisions.Any(x => x.ProjectId != request.ProjectId)) return Results.BadRequest(new { error = "Both revisions must belong to the selected project." });
            var effectivePolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
            try
            {
                var sourceLevel = revisions.Single(x => x.Id == request.SourceRevisionId).Level;
                var targetLevel = revisions.Single(x => x.Id == request.TargetRevisionId).Level;
                RequirementTracePolicy.Validate(effectivePolicy, sourceLevel, targetLevel, request.Type);
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (await (from member in db.BaselineRequirements.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId))
                       join campaign in db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == request.ProjectId && x.State == ReleaseCampaignState.InReview) on member.BaselineId equals campaign.BaselineId
                       select member.Id).AnyAsync(ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            try { var link = new RequirementTraceLink(request.ProjectId, request.SourceRevisionId, request.TargetRevisionId, request.Type, request.Rationale, DateTimeOffset.UtcNow); db.RequirementTraces.Add(link); await db.SaveChangesAsync(ct); return Results.Created($"/api/trace-links/{link.Id}", new { link.Id }); }
            catch (Exception ex) when (ex is DomainException or DbUpdateException) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/trace-links/{id:guid}/lifecycle", (Guid id, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
            ReadExactLinkLifecycleAsync(ExactLinkKind.RequirementTrace, id, http, db, ct));
        app.MapGet("/api/case-procedure-links/{id:guid}/lifecycle", (Guid id, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
            ReadExactLinkLifecycleAsync(ExactLinkKind.CaseProcedure, id, http, db, ct));

        app.MapPost("/api/trace-links/{id:guid}/lifecycle/acknowledge", (Guid id,
            AcknowledgeExactLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ExactLinkLifecycleService service, CancellationToken ct) =>
            MutateExactLinkLifecycleAsync(ExactLinkKind.RequirementTrace, id, request.Rationale, null,
                http, db, identity, service, ct));
        app.MapPost("/api/case-procedure-links/{id:guid}/lifecycle/acknowledge", (Guid id,
            AcknowledgeExactLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ExactLinkLifecycleService service, CancellationToken ct) =>
            MutateExactLinkLifecycleAsync(ExactLinkKind.CaseProcedure, id, request.Rationale, null,
                http, db, identity, service, ct));
        app.MapPost("/api/trace-links/{id:guid}/lifecycle/resolve", (Guid id,
            ResolveExactLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ExactLinkLifecycleService service, CancellationToken ct) =>
            MutateExactLinkLifecycleAsync(ExactLinkKind.RequirementTrace, id, request.Rationale, request.Outcome,
                http, db, identity, service, ct));
        app.MapPost("/api/case-procedure-links/{id:guid}/lifecycle/resolve", (Guid id,
            ResolveExactLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ExactLinkLifecycleService service, CancellationToken ct) =>
            MutateExactLinkLifecycleAsync(ExactLinkKind.CaseProcedure, id, request.Rationale, request.Outcome,
                http, db, identity, service, ct));

        app.MapDelete("/api/trace-links/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var link = await db.RequirementTraces.SingleOrDefaultAsync(x => x.Id == id, ct); if (link is null) return Results.NotFound();
            if(!await http.HasProjectRoleAsync(db,identity,link.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
            var revisionIds = new[] { link.SourceRevisionId, link.TargetRevisionId };
            if(await db.BaselineRequirements.AsNoTracking().AnyAsync(x=>revisionIds.Contains(x.RevisionId),ct))
                return Results.Conflict(new{error="Trace links involving a baselined requirement revision are controlled history and cannot be deleted. Create the corrected revision and superseding link instead.",code="controlled_trace_history"});
            db.RequirementTraces.Remove(link); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapGet("/api/traceability", async (Guid projectId, Guid? baselineId, string? search, int page, int pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            if (baselineId is null) baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null).OrderByDescending(x => x.FrozenAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (baselineId is null) return Results.Ok(new { page, pageSize, totalCount = 0, items = Array.Empty<object>() });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId.Value, ct);
            var effectiveProcedureRevisionIds = procedureEffectivity?.RevisionIds ?? [];
            var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                         join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                         join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                         select new { artifact, revision };
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct); var selected = await source.OrderBy(x => x.artifact.BaseNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var selectedIds = selected.Select(x => x.revision.Id).ToList();
            var baselineRevisionIds = await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == baselineId).Select(x => x.RevisionId).ToHashSetAsync(ct);
            // Both endpoints must belong to the selected baseline. A closed #709 carried link is valid in
            // the successor baseline that selected its new target, but must not leak into an older release
            // merely because its unchanged source revision is also present there.
            var links = await db.RequirementTraces.AsNoTracking()
                .Where(x => selectedIds.Contains(x.SourceRevisionId) && baselineRevisionIds.Contains(x.TargetRevisionId)
                    || selectedIds.Contains(x.TargetRevisionId) && baselineRevisionIds.Contains(x.SourceRevisionId))
                .ToListAsync(ct);
            var relatedIds = links.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
            var related = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => relatedIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id select new { revision.Id, artifactId = artifact.Id, artifact.BaseNumber, revision.Revision, level = artifact.Level.ToString() }).ToDictionaryAsync(x => x.Id, ct);
            var linkIds = links.Select(x => x.Id).ToList();
            var lifecycles = await db.ExactLinkSuspectLifecycles.AsNoTracking()
                .Where(x => x.LinkKind == ExactLinkKind.RequirementTrace && linkIds.Contains(x.LinkId)).ToDictionaryAsync(x => x.LinkId, ct);
            var lifecycleIds = lifecycles.Values.Select(x => x.Id).ToList();
            var lifecycleEvents = (await db.ExactLinkSuspectEvents.AsNoTracking()
                .Where(x => lifecycleIds.Contains(x.LifecycleId)).ToListAsync(ct))
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToList();
            var coverage = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, selectedIds, ct,
                buildScoped: true, effectiveProcedureRevisionIds: effectiveProcedureRevisionIds);
            var procedureRevisionIds=coverage.Select(x=>x.ProcedureRevisionId).Distinct().ToList();
            var executionQuery=db.TestExecutions.AsNoTracking().Where(x=>procedureRevisionIds.Contains(x.ProcedureRevisionId));
            var executions=await(db.Database.IsSqlite()?executionQuery.OrderByDescending(x=>x.Id):executionQuery.OrderByDescending(x=>x.ExecutedAt)).ToListAsync(ct);
            var executionIds=executions.Select(x=>x.Id).ToList();
            var evidence=await(from link in db.TestExecutionEvidence.AsNoTracking().Where(x=>executionIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new{link.TestExecutionId,item.Id,item.OriginalFileName,item.Sha256,item.Size,item.UploadedAt}).ToListAsync(ct);
            var items = selected.Select(x => new { x.artifact.Id, revisionId = x.revision.Id, displayNumber = x.artifact.BaseNumber + "." + x.revision.Revision.ToString("D2"), level = x.artifact.Level.ToString(), x.revision.Statement,
                parents = links.Where(l => l.SourceRevisionId == x.revision.Id).Select(l => new { id = l.TargetRevisionId, revisionId = l.TargetRevisionId, artifactId = related[l.TargetRevisionId].artifactId, linkId = l.Id, displayNumber = related[l.TargetRevisionId].BaseNumber + "." + related[l.TargetRevisionId].Revision.ToString("D2"), related[l.TargetRevisionId].level, type = l.Type.ToString(), lifecycle = lifecycles.TryGetValue(l.Id, out var life) ? new { state = life.State.ToString(), causeKind = life.CauseKind.ToString(), life.CauseRequirementRevisionId, life.CauseBaselineImportId, outcome = life.Outcome?.ToString(), events = lifecycleEvents.Where(e => e.LifecycleId == life.Id).Select(e => new { type = e.EventType.ToString(), e.ActorId, e.OccurredAt, e.Rationale, outcome = e.Outcome?.ToString() }) } : null }),
                children = links.Where(l => l.TargetRevisionId == x.revision.Id).Select(l => new { id = l.SourceRevisionId, revisionId = l.SourceRevisionId, artifactId = related[l.SourceRevisionId].artifactId, linkId = l.Id, displayNumber = related[l.SourceRevisionId].BaseNumber + "." + related[l.SourceRevisionId].Revision.ToString("D2"), related[l.SourceRevisionId].level, type = l.Type.ToString(), lifecycle = lifecycles.TryGetValue(l.Id, out var life) ? new { state = life.State.ToString(), causeKind = life.CauseKind.ToString(), life.CauseBaselineImportId, life.CauseRequirementRevisionId, outcome = life.Outcome?.ToString(), events = lifecycleEvents.Where(e => e.LifecycleId == life.Id).Select(e => new { type = e.EventType.ToString(), e.ActorId, e.OccurredAt, e.Rationale, outcome = e.Outcome?.ToString() }) } : null }),
                testCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && !c.IsSuspect),
                suspectTestCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && c.IsSuspect),
                tests=coverage.Where(c=>c.RequirementRevisionId==x.revision.Id).Select(c=>new
                {
                    artifactId=c.ProcedureId,
                    procedureId=c.ProcedureId, // compatibility alias for the pre-Case trace contract
                    artifactKind=c.ArtifactKind,
                    artifactRevisionId=c.ProcedureRevisionId,
                    revisionId=c.ProcedureRevisionId, // compatibility alias for the pre-neutral trace contract
                    c.DisplayNumber,c.Title,c.Level,
                    artifactState=c.ProcedureState,
                    state=c.ProcedureState, // compatibility alias for the pre-neutral trace contract
                    c.IsSuspect,c.CoverageState,
                    executions=executions.Where(e=>e.ProcedureRevisionId==c.ProcedureRevisionId).Select(e=>new{e.Id,outcome=e.Outcome.ToString(),e.ExecutedBy,e.ExecutedAt,e.RecordedAt,e.SoftwareBuildId,e.RetestOfExecutionId,e.Determination,e.EvidenceReference,evidence=evidence.Where(a=>a.TestExecutionId==e.Id).Select(a=>new{a.Id,a.OriginalFileName,a.Sha256,a.Size,a.UploadedAt})})
                }) });
            return Results.Ok(new { baselineId, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        // A compact, exact end-to-end thread for the selected baseline. The general traceability endpoint above
        // remains the exploration surface; this projection answers the separate assurance question "show me one
        // complete controlled path" without implying that a nearby requirement, procedure, or build is related.
        app.MapGet("/api/traceability/path", async (Guid projectId, Guid baselineId, Guid? requirementRevisionId,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.AsNoTracking()
                .Where(x => x.Id == baselineId && x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.DisplayNumber, x.Name })
                .SingleOrDefaultAsync(ct);
            if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();

            var nodes = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                               join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                               join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                               select new
                               {
                                   id = artifact.Id,
                                   revisionId = revision.Id,
                                   displayNumber = artifact.BaseNumber + "." + revision.Revision.ToString("D2"),
                                   level = artifact.Level.ToString(),
                                   revision.Statement,
                               }).ToListAsync(ct);
            if (nodes.Count == 0) return Results.Ok(new { baselineId, nodes = Array.Empty<object>() });

            var byRevision = nodes.ToDictionary(x => x.revisionId);
            var revisionIds = byRevision.Keys.ToList();
            var links = await db.RequirementTraces.AsNoTracking()
                .Where(x => revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId))
                .ToListAsync(ct);
            var coveredIds = await db.TestCoverage.AsNoTracking()
                .Where(x => revisionIds.Contains(x.RequirementRevisionId) && !x.IsSuspect)
                .Select(x => x.RequirementRevisionId).Distinct().ToListAsync(ct);
            var covered = coveredIds.ToHashSet();

            var focus = requirementRevisionId is Guid requested && byRevision.TryGetValue(requested, out var selected)
                ? selected
                : nodes.OrderBy(x => x.level == "System" ? 0 : x.level == "HighLevel" ? 1 : 2)
                    .ThenBy(x => x.displayNumber).First();

            // Source is the child and Target is its parent. Walk both directions from the reader's focus so the
            // path always includes it. Prefer a covered descendant, then use the stable controlled number.
            var ancestors = new List<Guid>();
            var cursor = focus.revisionId;
            var seen = new HashSet<Guid> { cursor };
            while (links.Where(x => x.SourceRevisionId == cursor)
                       .OrderBy(x => byRevision[x.TargetRevisionId].displayNumber)
                       .Select(x => (Guid?)x.TargetRevisionId).FirstOrDefault() is Guid parent
                   && seen.Add(parent))
            {
                ancestors.Add(parent);
                cursor = parent;
            }
            ancestors.Reverse();

            var descendants = new List<Guid>();
            cursor = focus.revisionId;
            while (links.Where(x => x.TargetRevisionId == cursor)
                       .Select(x => x.SourceRevisionId)
                       .OrderByDescending(x => covered.Contains(x))
                       .ThenBy(x => byRevision[x].displayNumber)
                       .Select(x => (Guid?)x).FirstOrDefault() is Guid child
                   && seen.Add(child))
            {
                descendants.Add(child);
                cursor = child;
            }
            var pathIds = ancestors.Append(focus.revisionId).Concat(descendants).ToList();
            var verificationRequirementId = pathIds.AsEnumerable().Reverse().FirstOrDefault(covered.Contains);

            var buildQuery = db.SoftwareBuilds.AsNoTracking().Where(x => x.BaselineId == baselineId);
            var buildRecord = db.Database.IsSqlite()
                ? (await buildQuery.ToListAsync(ct)).OrderByDescending(x => x.RecordedAt).FirstOrDefault()
                : await buildQuery.OrderByDescending(x => x.RecordedAt).FirstOrDefaultAsync(ct);
            Guid? selectedBuildId = buildRecord?.Id;

            var procedureCandidateRows = await (from coverage in db.TestCoverage.AsNoTracking()
                             .Where(x => verificationRequirementId != Guid.Empty
                                 && x.RequirementRevisionId == verificationRequirementId && !x.IsSuspect)
                         join revision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals revision.Id
                         join item in db.TestProcedures.AsNoTracking()
                             .Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case)
                             on revision.ProcedureId equals item.Id
                         select new
                         {
                             item.Id,
                             RevisionId = revision.Id,
                             DisplayNumber = item.BaseNumber + "." + revision.Revision.ToString("D2"),
                             Level = item.Level.ToString(),
                             ArtifactKind = item.ArtifactKind.ToString(),
                             State = revision.State.ToString()
                         }).ToListAsync(ct);
            var candidateTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                procedureCandidateRows.Select(x => x.RevisionId).Distinct().ToList(), ct);
            IReadOnlyList<PathProcedureCandidate> procedureCandidates = procedureCandidateRows.Select(x =>
                new PathProcedureCandidate(x.Id, x.RevisionId, x.DisplayNumber,
                    candidateTitles[x.RevisionId].Title, x.Level, x.ArtifactKind, x.State)).ToList();

            var candidateRevisionIds = procedureCandidates.Select(x => x.RevisionId).ToList();
            IReadOnlyList<TestExecution> candidateRuns = candidateRevisionIds.Count == 0
                ? Array.Empty<TestExecution>()
                : await db.TestExecutions.AsNoTracking()
                    .Where(x => candidateRevisionIds.Contains(x.ProcedureRevisionId) && x.ReleaseId == baseline.ReleaseId
                        && x.SoftwareBuildId == selectedBuildId)
                    .ToListAsync(ct);
            var latestByProcedure = candidateRuns.GroupBy(x => x.ProcedureRevisionId)
                .ToDictionary(x => x.Key, x => x
                    .OrderByDescending(run => run.ExecutedAt)
                    .ThenByDescending(run => run.RecordedAt).First());
            var candidateRunIds = latestByProcedure.Values.Select(x => x.Id).ToList();
            IReadOnlyList<Guid> evidencedRunIds = candidateRunIds.Count == 0
                ? Array.Empty<Guid>()
                : await db.TestExecutionEvidence.AsNoTracking().Where(x => candidateRunIds.Contains(x.TestExecutionId))
                    .Select(x => x.TestExecutionId).Distinct().ToListAsync(ct);
            var evidenced = evidencedRunIds.ToHashSet();

            // Prefer a genuinely complete path, then any build-scoped result, and use the controlled number only
            // as a stable tie-breaker. Choosing the first procedure number could report a gap while another exact
            // confirmed procedure already carried the result and immutable evidence the reader asked to see.
            var procedure = procedureCandidates
                .OrderByDescending(x => latestByProcedure.TryGetValue(x.RevisionId, out var run) && evidenced.Contains(run.Id))
                .ThenByDescending(x => latestByProcedure.ContainsKey(x.RevisionId))
                .ThenBy(x => x.DisplayNumber)
                .FirstOrDefault();

            object? execution = null;
            if (procedure is not null)
            {
                latestByProcedure.TryGetValue(procedure.RevisionId, out var run);
                if (run is not null)
                {
                    var files = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => x.TestExecutionId == run.Id)
                                       join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id
                                       select new { item.Id, item.OriginalFileName, item.Sha256, item.Size, item.UploadedAt }).ToListAsync(ct);
                    execution = new
                    {
                        run.Id,
                        outcome = run.Outcome.ToString(),
                        run.ExecutedBy,
                        run.ExecutedAt,
                        run.Determination,
                        run.EvidenceReference,
                        evidence = files,
                    };
                }
            }

            var build = buildRecord is null ? null : new
            {
                buildRecord.Id,
                buildRecord.BuildNumber,
                state = buildRecord.State.ToString(),
                buildRecord.RecordedAt,
                buildRecord.ReleasedAt,
            };
            var verificationArtifact = procedure is null ? null : new
            {
                id = procedure.Id,
                revisionId = procedure.RevisionId,
                displayNumber = procedure.DisplayNumber,
                procedure.Title,
                artifactKind = procedure.ArtifactKind.ToString(),
                level = procedure.Level,
                state = procedure.State,
            };

            return Results.Ok(new
            {
                baselineId,
                baseline = new { baseline.DisplayNumber, baseline.Name },
                focusRevisionId = focus.revisionId,
                nodes = pathIds.Select(id => byRevision[id]),
                artifact = verificationArtifact,
                procedure = verificationArtifact, // compatibility alias
                execution,
                build,
            });
        });

        // Discussion on a verification artifact, the same conversation a requirement carries.
        //
        // ArtifactComment was already generic — it keys on an artifact type and identifier — so only the route
        // was requirement-shaped. The discriminator preserves the product distinction — System Procedure or
        // software Case — while both use the same table and endpoint implementation.
        app.MapGet("/api/test-{artifactRoute:regex(procedures|cases)}/{id:guid}/comments", async (string artifactRoute, Guid id, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var artifact = await db.TestProcedures.Where(x => x.Id == id)
                .Select(x => new { x.ProjectId, x.Level, x.ArtifactKind }).SingleOrDefaultAsync(ct);
            if (artifact is null || !ArtifactRouteAllows(artifactRoute, artifact.Level, artifact.ArtifactKind)) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
            var artifactType = artifact.ArtifactKind == VerificationArtifactKind.Procedure ? "TestProcedure" : "TestCase";
            var comments = await db.ArtifactComments.AsNoTracking()
                .Where(x => x.ArtifactId == id && x.ArtifactType == artifactType).ToListAsync(ct);
            return Results.Ok(comments.OrderBy(x => x.CreatedAt).Select(x => new
            {
                x.Id, x.RevisionId, x.ParentCommentId, x.Body, x.MentionsJson, state = x.State.ToString(),
                x.CreatedBy, x.CreatedAt, x.ResolvedBy, x.ResolvedAt, x.Disposition,
            }));
        });

        app.MapPost("/api/test-{artifactRoute:regex(procedures|cases)}/{id:guid}/comments", async (string artifactRoute, Guid id, CreateProcedureCommentRequest request,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (procedure is null || !ArtifactRouteAllows(artifactRoute, procedure.Level, procedure.ArtifactKind)) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();
            var policy = await policyResolver.ResolveAsync(procedure.ProjectId, ct);
            var discussionEnabled = procedure.Level switch
            {
                TestProcedureLevel.System => policy.VerificationProfile(RequirementLevel.System).Enables(VerificationArtifactKind.Procedure),
                TestProcedureLevel.HighLevel => policy.VerificationProfile(RequirementLevel.HighLevel).Enables(procedure.ArtifactKind),
                TestProcedureLevel.LowLevel => policy.VerificationProfile(RequirementLevel.LowLevel).Enables(procedure.ArtifactKind),
                _ => false,
            };
            if (!discussionEnabled)
                return Results.BadRequest(new { error = "Discussion is unavailable for this disabled verification artifact.", code = "verification_discussion_disabled" });
            if (request.RevisionId is not null
                && !await db.TestProcedureRevisions.AnyAsync(x => x.Id == request.RevisionId && x.ProcedureId == id, ct))
                return Results.BadRequest(new { error = $"The comment revision is not part of this {ArtifactNoun(procedure.Level, procedure.ArtifactKind)}." });
            if (request.ParentCommentId is not null
                && !await db.ArtifactComments.AnyAsync(x => x.Id == request.ParentCommentId && x.ArtifactId == id, ct))
                return Results.BadRequest(new { error = $"The parent comment is not part of this {ArtifactNoun(procedure.Level, procedure.ArtifactKind)}." });
            var releaseDecision = await VerificationDiscussionReleaseAuthority.ValidateAsync(db, procedure.ProjectId,
                request.ReleaseId, request.RevisionId, id, ct);
            if (!releaseDecision.Allowed)
                return Results.BadRequest(new { error = releaseDecision.Error, code = releaseDecision.Code });
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                var mentions = request.Mentions ?? [];
                var artifactType = procedure.ArtifactKind == VerificationArtifactKind.Procedure ? "TestProcedure" : "TestCase";
                var comment = new ArtifactComment(procedure.ProjectId, artifactType, id, request.RevisionId,
                    request.ParentCommentId, request.Body, JsonSerializer.Serialize(mentions), actor, now);
                db.ArtifactComments.Add(comment);

                // Mentioning somebody has to reach them, or the discussion is only identical to a requirement's
                // in appearance. Procedures carry no watch list, so the audience is who was named plus whoever
                // is being replied to.
                var requested = mentions.Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
                if (request.ParentCommentId is not null)
                    requested.Add((await db.ArtifactComments.Where(x => x.Id == request.ParentCommentId)
                        .Select(x => x.CreatedBy).SingleAsync(ct)).ToLowerInvariant());
                var recipients = await db.UserAccounts.AsNoTracking()
                    .Where(x => requested.Contains(x.UserName) && x.UserName != actor)
                    .Select(x => x.UserName).ToListAsync(ct);
                var notificationKind = procedure.ArtifactKind == VerificationArtifactKind.Procedure ? "Procedure" : "Case";
                var notificationRoute = procedure.ArtifactKind == VerificationArtifactKind.Procedure ? "procedure" : "case";
                foreach (var recipient in recipients)
                    db.UserNotifications.Add(new(procedure.ProjectId, recipient, $"Test{notificationKind}Comment",
                        $"Discussion on {procedure.BaseNumber}", $"{actor}: {request.Body}",
                        $"{notificationRoute}:{id}", id, now));

                await db.SaveChangesAsync(ct);
                var artifactRouteRoot = procedure.ArtifactKind == VerificationArtifactKind.Procedure ? "test-procedures" : "test-cases";
                return Results.Created($"/api/{artifactRouteRoot}/{id}/comments/{comment.Id}",
                    new { comment.Id, notified = recipients.Count });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// How a procedure came to say what it says.
        ///
        /// A procedure is read by somebody deciding whether to trust it, and "who wrote this, when, and what
        /// made them change it" is most of that decision. Its revisions were reachable only by reading the
        /// procedure itself, one revision at a time, with no way to see what drove any of them.
        ///
        /// The change request behind a revision is not recorded on the revision — it is reached through the
        /// verification decision that resolved to it, which is the record that actually connects the two. A
        /// revision written outside that path has no change request, and says so rather than guessing.
        app.MapGet("/api/test-{artifactRoute:regex(procedures|cases)}/{id:guid}/history", async (string artifactRoute, Guid id, Guid? revisionId, Guid? releaseId, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.OwnerId, x.Level, x.ArtifactKind, x.CreatedAt, x.Version })
                .SingleOrDefaultAsync(ct);
            if (procedure is null || !ArtifactRouteAllows(artifactRoute, procedure.Level, procedure.ArtifactKind)) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

            var revisions = (await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == id).ToListAsync(ct))
                .OrderByDescending(x => x.Revision).ToList();
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (revisionId is not null && !revisionIds.Contains(revisionId.Value)) return Results.NotFound();
            Guid? effectiveRevisionId = null;
            if (releaseId is not null)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, procedure.ProjectId, releaseId.Value, ct);
                if (effectivity is not null && effectivity.RevisionByProcedure.TryGetValue(id, out var carriedRevisionId))
                    effectiveRevisionId = carriedRevisionId;
                // A request for one exact revision is a build-effectivity assertion and must match the
                // manifest. Omitting revisionId is the broad historical view: legacy and draft procedures
                // remain readable there even when no build ever carried them.
                if (revisionId is not null && revisionId != effectiveRevisionId) return Results.NotFound();
            }

            var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, revisionIds, ct);
            var provenance = await TestProcedureProvenanceProjection.ForRevisionsAsync(db, revisionIds, ct);
            var parentLinks = await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var parentLifecycleIds = parentLinks.Where(x => x.ExactLinkSuspectLifecycleId is not null)
                .Select(x => x.ExactLinkSuspectLifecycleId!.Value).ToList();
            var parentLifecycles = await db.ExactLinkSuspectLifecycles.AsNoTracking()
                .Where(x => parentLifecycleIds.Contains(x.Id) && x.LinkKind == ExactLinkKind.CaseProcedure)
                .ToDictionaryAsync(x => x.Id, ct);

            // The requirements each revision covers, so a reader sees what it is for without leaving the page.
            var coverage = await (from link in db.TestCoverage.AsNoTracking()
                                  join revision in db.RequirementRevisions.AsNoTracking() on link.RequirementRevisionId equals revision.Id
                                  join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                  where revisionIds.Contains(link.ProcedureRevisionId)
                                  select new { link.ProcedureRevisionId, artifact.BaseNumber, revision.Revision }).ToListAsync(ct);

            var selectedId = revisionId ?? effectiveRevisionId ?? revisions.FirstOrDefault()?.Id;
            var selectedTitle = selectedId is Guid selected && titles.TryGetValue(selected, out var title)
                ? title
                : null;
            return Results.Ok(new
            {
                artifactId = procedure.Id,
                artifactKind = procedure.ArtifactKind.ToString(),
                procedure.Id,
                procedure.BaseNumber,
                title = selectedTitle?.Title ?? "",
                titleIsExact = selectedTitle?.IsExact ?? false,
                titleIsLegacy = selectedTitle?.IsLegacy ?? false,
                titleNote = selectedTitle?.Note,
                level = procedure.Level.ToString(),
                procedure.OwnerId,
                procedure.CreatedAt,
                procedure.Version,
                selectedRevisionId = selectedId,
                revisions = revisions.Select(revision =>
                {
                    var revisionTitle = titles[revision.Id];
                    var source = provenance[revision.Id];
                    return new
                    {
                        revision.Id,
                        displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
                        revision.Revision,
                        title = revisionTitle.Title,
                        titleIsExact = revisionTitle.IsExact,
                        titleIsLegacy = revisionTitle.IsLegacy,
                        titleNote = revisionTitle.Note,
                        state = revision.State.ToString(),
                        revision.AuthorId,
                        revision.CreatedAt,
                        revision.Objective,
                        revision.Preconditions,
                        revision.Steps,
                        revision.ExpectedResult,
                        environmentSetup = revision.EnvironmentSetup,
                        testData = revision.TestData,
                        orderedSteps = revision.OrderedSteps,
                        expectedObservations = revision.ExpectedObservations,
                        cleanup = revision.Cleanup,
                        toolingAutomation = revision.ToolingAutomation,
                        parentKind = revision.ParentKind.ToString(),
                        derivedRationale = revision.DerivedRationale,
                        retirementRationale = revision.RetirementRationale,
                        caseRevisionIds = parentLinks.Where(x => x.ProcedureRevisionId == revision.Id)
                            .Select(x => x.CaseRevisionId).ToArray(),
                        caseParents = parentLinks.Where(x => x.ProcedureRevisionId == revision.Id)
                            .Select(x => new
                            {
                                linkId = x.Id,
                                x.CaseRevisionId,
                                state = x.ExactLinkSuspectLifecycleId is Guid lifecycleId
                                    && parentLifecycles.TryGetValue(lifecycleId, out var lifecycle)
                                        ? lifecycle.State.ToString()
                                        : "Confirmed",
                                outcome = x.ExactLinkSuspectLifecycleId is Guid outcomeLifecycleId
                                    && parentLifecycles.TryGetValue(outcomeLifecycleId, out var outcomeLifecycle)
                                    && outcomeLifecycle.Outcome is not null
                                        ? outcomeLifecycle.Outcome.ToString()
                                        : null,
                            }).ToArray(),
                        selected = revision.Id == selectedId,
                        revision.SourceTestChangeRequestId,
                        package = source.Package,
                        provenanceNote = source.Note,
                        drivenBy = source.Drivers.Select(driver => new
                        {
                            changeRequest = driver.ChangeRequest,
                            package = driver.Package,
                            subjectDisplayNumber = driver.SubjectDisplayNumber,
                            action = driver.Action,
                            isLegacy = driver.IsLegacy,
                        }).ToList(),
                        covers = coverage.Where(x => x.ProcedureRevisionId == revision.Id)
                            .Select(x => $"{x.BaseNumber}.{x.Revision:D2}").Distinct().OrderBy(x => x).ToList(),
                    };
                }).ToList(),
            });
        });

        // The Trace & impact tab's exact revision-scoped projection (#399). A count is not a trace: this
        // answers which exact requirement revisions the selected effective procedure revision verifies,
        // whether each link is Confirmed or Suspect, and which TCR/change package produced the procedure
        // revision. The selected build's exact procedure manifest is authoritative (#214): a later procedure
        // revision or a relationship belonging to another build is never substituted for what this build
        // actually carries.
        app.MapGet("/api/test-{artifactRoute:regex(procedures|cases)}/{id:guid}/trace", async (string artifactRoute, Guid id, Guid? releaseId, Guid? revisionId,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.Level, x.ArtifactKind })
                .SingleOrDefaultAsync(ct);
            if (procedure is null || !ArtifactRouteAllows(artifactRoute, procedure.Level, procedure.ArtifactKind)) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

            Guid? selectedRevisionId = revisionId;
            Guid? effectiveBaselineId = null;
            Guid? requirementBaselineId = null;
            var isExactManifest = false;
            if (releaseId is not null)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                    db, procedure.ProjectId, releaseId.Value, ct);
                if (effectivity is null || !effectivity.RevisionByProcedure.TryGetValue(id, out var carriedRevisionId))
                    return Results.NotFound(new
                    {
                        error = $"This {ArtifactNoun(procedure.Level, procedure.ArtifactKind)} is not carried by the selected build.",
                        code = "procedure_not_carried_by_build"
                    });
                // A request for one exact revision is a build-effectivity assertion and must match the
                // manifest, exactly as the history endpoint enforces.
                if (revisionId is not null && revisionId != carriedRevisionId)
                    return Results.NotFound(new
                    {
                        error = $"The requested {ArtifactNoun(procedure.Level, procedure.ArtifactKind)} revision is not the revision the selected build carries.",
                        code = "cross_build_procedure_revision"
                    });
                selectedRevisionId = carriedRevisionId;
                isExactManifest = effectivity.IsExactManifest;
                effectiveBaselineId = effectivity.BaselineId;
                // The requirement manifest is the other half of build effectivity: the same build's exact
                // BaselineRequirements, not a project-global view of what any build ever carried.
                requirementBaselineId = await BuildScope.EffectiveBaselineAsync(
                    db, procedure.ProjectId, releaseId.Value, ct);
                if (requirementBaselineId is null)
                    return Results.NotFound(new
                    {
                        error = "The selected build has no controlled requirement baseline to intersect the trace against.",
                        code = "requirement_baseline_unavailable"
                    });
            }
            selectedRevisionId ??= await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == id).OrderByDescending(x => x.Revision)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (selectedRevisionId is null) return Results.NotFound();
            var selectedRevisionIdValue = selectedRevisionId.Value;
            if (!await db.TestProcedureRevisions.AsNoTracking()
                    .AnyAsync(x => x.Id == selectedRevisionIdValue && x.ProcedureId == id, ct))
                return Results.NotFound();

            var revision = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.Id == selectedRevisionIdValue)
                .Select(x => new
                {
                    x.Id, x.Revision, x.State, x.AuthorId, x.CreatedAt, x.SourceTestChangeRequestId
                })
                .SingleAsync(ct);

            var caseLinks = procedure.ArtifactKind == VerificationArtifactKind.Procedure && procedure.Level != TestProcedureLevel.System
                ? await db.TestCaseProcedureLinks.AsNoTracking()
                    .Where(x => x.ProcedureRevisionId == selectedRevisionIdValue).ToListAsync(ct)
                : [];
            var caseRevisionIds = caseLinks.Select(x => x.CaseRevisionId).Distinct().ToList();
            var caseRevisions = caseRevisionIds.Count == 0
                ? []
                : await (from caseRevision in db.TestProcedureRevisions.AsNoTracking()
                         join caseArtifact in db.TestProcedures.AsNoTracking() on caseRevision.ProcedureId equals caseArtifact.Id
                         where caseRevisionIds.Contains(caseRevision.Id)
                         select new { caseRevision.Id, caseRevision.ProcedureId, caseArtifact.BaseNumber, caseRevision.Revision })
                    .ToListAsync(ct);
            var caseTitles = caseRevisionIds.Count == 0
                ? new Dictionary<Guid, TestProcedureRevisionTitleSnapshot>()
                : await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, caseRevisionIds, ct);
            var caseLifecycleIds = caseLinks.Where(x => x.ExactLinkSuspectLifecycleId is not null)
                .Select(x => x.ExactLinkSuspectLifecycleId!.Value).Distinct().ToList();
            var caseLifecycles = caseLifecycleIds.Count == 0
                ? new Dictionary<Guid, ExactLinkSuspectLifecycle>()
                : await db.ExactLinkSuspectLifecycles.AsNoTracking()
                    .Where(x => caseLifecycleIds.Contains(x.Id) && x.LinkKind == ExactLinkKind.CaseProcedure)
                    .ToDictionaryAsync(x => x.Id, ct);

            // The exact coverage rows this revision owns, joined to the exact requirement revisions they
            // name. Nothing here is derived from counts, display strings or project-global latest records.
            var covered = await (from link in db.TestCoverage.AsNoTracking()
                                 where link.ProcedureRevisionId == selectedRevisionIdValue
                                 join requirementRevision in db.RequirementRevisions.AsNoTracking()
                                     on link.RequirementRevisionId equals requirementRevision.Id
                                 join artifact in db.Requirements.AsNoTracking()
                                     on requirementRevision.ArtifactId equals artifact.Id
                                 select new
                                 {
                                     artifact.Id,
                                     RequirementRevisionId = requirementRevision.Id,
                                     DisplayNumber = artifact.BaseNumber + "." + requirementRevision.Revision.ToString("D2"),
                                     Level = artifact.Level.ToString(),
                                     requirementRevision.Statement,
                                     link.IsSuspect
                                 }).ToListAsync(ct);
            if (requirementBaselineId is not null)
            {
                // A selected build must never claim a RequirementRevision outside that build's exact
                // requirement manifest as one its carried procedure verifies. Out-of-scope coverage rows are
                // historical database evidence and stay in place; they are simply not presented as this
                // build's controlled traceability.
                var manifestRevisionIds = await db.BaselineRequirements.AsNoTracking()
                    .Where(x => x.BaselineId == requirementBaselineId.Value)
                    .Select(x => x.RevisionId).ToListAsync(ct);
                var manifest = manifestRevisionIds.ToHashSet();
                covered = covered.Where(x => manifest.Contains(x.RequirementRevisionId)).ToList();
            }

            // CoverageState is the product's single definition, read from the same projection the release
            // gate and the requirement workspace use. Build-scoped: the exact carried revision's own state
            // decides, so a later-build sibling revision cannot make a released build's link suspect.
            var coverageStates = (await VerificationCoverageProjection.ForRequirementRevisionsAsync(db,
                    covered.Select(x => x.RequirementRevisionId).Distinct().ToList(), ct,
                    buildScoped: true, effectiveProcedureRevisionIds: [selectedRevisionIdValue]))
                .GroupBy(x => x.RequirementRevisionId)
                .ToDictionary(x => x.Key, x => x.First().CoverageState);

            var title = (await TestProcedureRevisionTitleProjection.ForRevisionsAsync(
                db, [selectedRevisionIdValue], ct))[selectedRevisionIdValue];
            var provenance = (await TestProcedureProvenanceProjection.ForRevisionsAsync(
                db, [selectedRevisionIdValue], ct))[selectedRevisionIdValue];

            var requirements = covered
                .Select(x => new
                {
                    x.Id,
                    revisionId = x.RequirementRevisionId,
                    x.DisplayNumber,
                    x.Level,
                    x.Statement,
                    // A link that has no projection entry must not claim Confirmed.
                    coverageState = coverageStates.TryGetValue(x.RequirementRevisionId, out var state)
                        ? state
                        : RequirementCoverageState.Suspect,
                    x.IsSuspect
                })
                .OrderBy(x => x.DisplayNumber)
                .ToList();
            var softwareProcedureRequirements = procedure.ArtifactKind == VerificationArtifactKind.Procedure
                && procedure.Level != TestProcedureLevel.System
                    ? (object)Array.Empty<object>()
                    : requirements;
            var caseParents = caseLinks.Select(link =>
            {
                var parent = caseRevisions.SingleOrDefault(x => x.Id == link.CaseRevisionId);
                var title = caseTitles.TryGetValue(link.CaseRevisionId, out var parentTitle) ? parentTitle.Title : null;
                var lifecycle = link.ExactLinkSuspectLifecycleId is Guid lifecycleId
                    && caseLifecycles.TryGetValue(lifecycleId, out var parentLifecycle)
                    ? parentLifecycle
                    : null;
                return new
                {
                    linkId = link.Id,
                    caseRevisionId = link.CaseRevisionId,
                    displayNumber = parent is null ? null : $"{parent.BaseNumber}.{parent.Revision:D2}",
                    title,
                    state = lifecycle?.State.ToString() ?? "Confirmed",
                    outcome = lifecycle?.Outcome?.ToString(),
                };
            }).OrderBy(x => x.displayNumber).ToList();

            return Results.Ok(new
            {
                artifactId = procedure.Id,
                procedureId = procedure.Id, // compatibility alias for the pre-Case contract
                artifactKind = procedure.ArtifactKind.ToString(),
                procedure.BaseNumber,
                title = title.Title,
                titleIsExact = title.IsExact,
                titleIsLegacy = title.IsLegacy,
                titleNote = title.Note,
                level = procedure.Level.ToString(),
                revisionId = selectedRevisionIdValue,
                displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
                revision.Revision,
                state = revision.State.ToString(),
                revision.AuthorId,
                revision.CreatedAt,
                revision.SourceTestChangeRequestId,
                package = provenance.Package,
                provenanceNote = provenance.Note,
                requirements = softwareProcedureRequirements,
                caseParents,
                provenance = provenance.Drivers.Select(driver => new
                {
                    changeRequest = driver.ChangeRequest,
                    package = driver.Package,
                    subjectDisplayNumber = driver.SubjectDisplayNumber,
                    action = driver.Action,
                    isLegacy = driver.IsLegacy,
                }).ToList(),
                build = releaseId is null ? null : new
                {
                    releaseId = releaseId.Value, effectiveBaselineId, requirementBaselineId, isExactManifest
                },
            });
        });

        // The workspace rendered every procedure it was given — 440 cards on the software side — with no
        // search, filter or page. This returns a bounded page and the total, and every predicate below runs
        // in the database, because a page of twenty-five that costs a full table read is not paging.
        app.MapGet("/api/{artifactRoute:regex(test-procedures|test-cases|verification-artifacts)}", async (string artifactRoute, Guid projectId, Guid? releaseId, string? search, string? scope, string? state,
            string? owner, string? outcome, Guid? requirementRevisionId, string? sort, int? page, int? pageSize, string? ids,
            Guid? documentId, Guid? sectionId, string? artifactKind,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            // This endpoint read a Project's controlled procedures without checking the caller was in it.
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            var allowedProcedureLevels = ladderPolicy.OrderedLevels
                .Where(level => ladderPolicy.Definition(level).Verification is not null)
                .Select(level => ladderPolicy.ProcedureLevel(level)).ToArray();
            var enabledKeys = ladderPolicy.Definitions
                .Where(level => level.VerificationProfile is not null)
                .SelectMany(level => level.VerificationProfile!.Definitions)
                .Select(definition => definition.Key)
                .ToHashSet();
            var systemCaseEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Case));
            var systemProcedureEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure));
            var highCaseEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case));
            var highProcedureEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure));
            var lowCaseEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case));
            var lowProcedureEnabled = enabledKeys.Contains(new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure));
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
            source = allowedProcedureLevels.Length == 0
                ? source.Where(_ => false)
                : source.Where(x => allowedProcedureLevels.Contains(x.Level));
            // The effective profile is the authority for both level and kind. Historical rows must not leak into
            // a combined inventory merely because the aggregate still contains them.
            var legacyRoute = artifactRoute switch { "test-procedures" => "procedures", "test-cases" => "cases", _ => artifactRoute };
            var legacyProcedureAlias = string.Equals(legacyRoute, "procedures", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(artifactKind);
            var requestedKind = Enum.TryParse<VerificationArtifactKind>(artifactKind, true, out var parsedKind)
                ? (VerificationArtifactKind?)parsedKind
                : string.Equals(legacyRoute, "cases", StringComparison.OrdinalIgnoreCase)
                    ? VerificationArtifactKind.Case
                    : string.Equals(scope, "System", StringComparison.OrdinalIgnoreCase)
                        ? VerificationArtifactKind.Procedure
                        : null;
            source = source.Where(x =>
                (x.Level == TestProcedureLevel.System && ((x.ArtifactKind == VerificationArtifactKind.Case && systemCaseEnabled) || (x.ArtifactKind == VerificationArtifactKind.Procedure && systemProcedureEnabled)))
                || (x.Level == TestProcedureLevel.HighLevel && ((x.ArtifactKind == VerificationArtifactKind.Case && highCaseEnabled) || (x.ArtifactKind == VerificationArtifactKind.Procedure && highProcedureEnabled)))
                || (x.Level == TestProcedureLevel.LowLevel && ((x.ArtifactKind == VerificationArtifactKind.Case && lowCaseEnabled) || (x.ArtifactKind == VerificationArtifactKind.Procedure && lowProcedureEnabled))));
            if (requestedKind is not null)
                source = source.Where(x => x.ArtifactKind == requestedKind.Value);
            else if (legacyProcedureAlias)
                source = source.Where(x => (x.Level == TestProcedureLevel.System && x.ArtifactKind == VerificationArtifactKind.Procedure)
                    || (x.Level != TestProcedureLevel.System && x.ArtifactKind == VerificationArtifactKind.Case));
            Dictionary<Guid, Guid>? scopedRevisions = null;
            if(releaseId is not null)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
                // `views` is part of this response's shape, so the empty answer carries it too. A reply that
                // drops a field the caller reads is a reply that crashes the caller.
                if(effectivity is null)return Results.Ok(new{page=currentPage,pageSize=size,totalCount=0,totalPages=0,views=Array.Empty<object>(),items=Array.Empty<object>()});
                scopedRevisions = effectivity.RevisionByProcedure.ToDictionary(x => x.Key, x => x.Value);
                var effectiveProcedureIds = scopedRevisions.Keys.ToList();
                source=source.Where(x=>effectiveProcedureIds.Contains(x.Id));
            }
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel||x.Level==TestProcedureLevel.LowLevel);
            else if(string.Equals(scope,"HighLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel);
            else if(string.Equals(scope,"LowLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.LowLevel);
            // Narrowing to one document, or to one section of it, is what the rail does when a reader picks an
            // entry — the same act as picking a specification on the requirements side.
            if (documentId is not null || sectionId is not null)
            {
                var placed = db.TestProcedureDocumentNodes.AsNoTracking()
                    .Where(x => x.ProcedureId != null && x.Type == TestProcedureDocumentNodeType.Procedure);
                if (documentId is not null) placed = placed.Where(x => x.DocumentId == documentId);
                if (sectionId is not null) placed = placed.Where(x => x.ParentId == sectionId);
                var placedIds = await placed.Select(x => x.ProcedureId!.Value).ToListAsync(ct);
                source = source.Where(x => placedIds.Contains(x.Id));
            }
            // Eligibility is Project plus the selected build's exact procedure manifest plus discipline.
            // Hydration of an already selected exact procedure runs against this scoped source without the
            // search predicate, so a selection beyond the current result page stays reachable.
            var eligibility = source;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                // Deep links use the controlled display number, including its revision suffix, while the
                // procedure owns only the base number. Let either form find the same controlled procedure.
                var requestedRevision = -1;
                var hasRevision = q.Length > 3 && q[^3] == '.' && int.TryParse(q[^2..], out requestedRevision);
                var baseQuery = hasRevision ? q[..^3] : q;
                var scopedRevisionIds = scopedRevisions?.Values.ToList();
                // Search the same exact revision title returned below. Raw TCR text is not authoritative:
                // in particular, a supplied Retire title is discarded in favour of its predecessor title.
                var titleCandidateRevisionIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                                       join procedure in source on revision.ProcedureId equals procedure.Id
                                                       where scopedRevisionIds == null
                                                           ? revision.Revision == db.TestProcedureRevisions
                                                               .Where(other => other.ProcedureId == procedure.Id)
                                                               .Max(other => other.Revision)
                                                           : scopedRevisionIds.Contains(revision.Id)
                                                       select revision.Id).ToListAsync(ct);
                var titleMatchRevisionIds = await TestProcedureRevisionTitleProjection.MatchingRevisionIdsAsync(
                    db, titleCandidateRevisionIds, q, ct);
                var titleMatches = await db.TestProcedureRevisions.AsNoTracking()
                    .Where(x => titleMatchRevisionIds.Contains(x.Id))
                    .Select(x => x.ProcedureId).Distinct().ToListAsync(ct);
                source = source.Where(x => x.BaseNumber.ToLower().Contains(baseQuery)
                                           || titleMatches.Contains(x.Id));
                if (hasRevision && scopedRevisions is not null)
                {
                    var matchingProcedureIds = await db.TestProcedureRevisions.AsNoTracking()
                        .Where(x => scopedRevisionIds!.Contains(x.Id) && x.Revision == requestedRevision)
                        .Select(x => x.ProcedureId).ToListAsync(ct);
                    source = source.Where(x => matchingProcedureIds.Contains(x.Id));
                }
            }
            if (!string.IsNullOrWhiteSpace(owner)) { var o = owner.Trim().ToLower(); source = source.Where(x => x.OwnerId.ToLower() == o); }
            // Lifecycle state belongs to the current revision, so the predicate names it rather than matching
            // any revision a procedure has ever had.
            if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<TestProcedureState>(state, true, out var parsedState))
            {
                var scopedRevisionIds = scopedRevisions?.Values.ToList();
                source = scopedRevisionIds is null
                    ? source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                        && r.Revision == db.TestProcedureRevisions.Where(o => o.ProcedureId == x.Id).Max(o => o.Revision)
                        && r.State == parsedState))
                    : source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                        && scopedRevisionIds.Contains(r.Id) && r.State == parsedState));
            }
            if (requirementRevisionId is not null)
                source = source.Where(x => db.TestCoverage.Any(c => c.RequirementRevisionId == requirementRevisionId
                    && db.TestProcedureRevisions.Any(r => r.Id == c.ProcedureRevisionId && r.ProcedureId == x.Id)));
            // Latest outcome means the most recent run, not any run the procedure ever had — a procedure that
            // failed and was then fixed must answer to Pass and not to Fail.
            //
            // SQLite can neither order nor aggregate a DateTimeOffset, so "most recent" cannot be expressed in
            // SQL here. The comparison is made in memory over the ids the other predicates have already
            // narrowed to, and only when this filter is actually used; the page itself is still taken in the
            // database.
            if (!string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<TestOutcome>(outcome, true, out var parsedOutcome))
            {
                var candidateIds = await source.Select(x => x.Id).ToListAsync(ct);
                var scopedRevisionIds = scopedRevisions?.Where(x => candidateIds.Contains(x.Key)).Select(x => x.Value).ToList();
                var runs = await (from execution in db.TestExecutions.AsNoTracking()
                                  join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                                  where candidateIds.Contains(revision.ProcedureId)
                                      && (scopedRevisionIds == null || scopedRevisionIds.Contains(revision.Id))
                                  select new { revision.ProcedureId, execution.Outcome, execution.ExecutedAt, execution.RecordedAt }).ToListAsync(ct);
                var matching = runs.GroupBy(x => x.ProcedureId)
                    .Where(group => group.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).First().Outcome == parsedOutcome)
                    .Select(group => group.Key).ToList();
                source = source.Where(x => matching.Contains(x.Id));
            }

            var totalCount = await source.CountAsync(ct);
            // Every sort ends on the controlled number, so a page boundary cannot depend on tie order.
            var ordered = sort?.ToLowerInvariant() switch
            {
                "owner" => source.OrderBy(x => x.OwnerId).ThenBy(x => x.BaseNumber),
                "level" => source.OrderBy(x => x.Level).ThenBy(x => x.BaseNumber),
                _ => source.OrderBy(x => x.BaseNumber).ThenBy(x => x.BaseNumber),
            };
            List<ProcedureListItem> items;
            if (string.Equals(sort, "title", StringComparison.OrdinalIgnoreCase))
            {
                // Title ordering must use the same immutable revision title returned to the client. Ordering
                // by the mutable catalog value would make an old build move when a successor is modified.
                var candidates = await source.Select(x => new ProcedureListItem(
                    x.Id, x.BaseNumber, x.OwnerId, x.Level, x.ArtifactKind, x.CreatedAt, x.Version)).ToListAsync(ct);
                var candidateIds = candidates.Select(x => x.Id).ToList();
                var candidateRevisions = await db.TestProcedureRevisions.AsNoTracking()
                    .Where(x => candidateIds.Contains(x.ProcedureId)).ToListAsync(ct);
                var candidateRevisionByProcedure = scopedRevisions is null
                    ? candidateRevisions.GroupBy(x => x.ProcedureId)
                        .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Revision).First().Id)
                    : scopedRevisions.Where(x => candidateIds.Contains(x.Key))
                        .ToDictionary(x => x.Key, x => x.Value);
                var candidateTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                    candidateRevisionByProcedure.Values.Distinct().ToList(), ct);
                items = candidates.OrderBy(x => candidateTitles[candidateRevisionByProcedure[x.Id]].Title)
                    .ThenBy(x => x.BaseNumber).Skip((currentPage - 1) * size).Take(size).ToList();
            }
            else
            {
                items = await ordered.Skip((currentPage - 1) * size).Take(size)
                    .Select(x => new ProcedureListItem(x.Id, x.BaseNumber, x.OwnerId, x.Level, x.ArtifactKind, x.CreatedAt, x.Version))
                    .ToListAsync(ct);
            }
            var requestedIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToList();
            var hydrated = requestedIds.Count == 0
                ? []
                : await eligibility.Where(x => requestedIds.Contains(x.Id))
                    .Select(x => new ProcedureListItem(x.Id, x.BaseNumber, x.OwnerId, x.Level, x.ArtifactKind, x.CreatedAt, x.Version))
                    .ToListAsync(ct);
            var all = items.Concat(hydrated).DistinctBy(x => x.Id).ToList();
            var allIds = all.Select(x => x.Id).ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => allIds.Contains(x.ProcedureId)).ToListAsync(ct);
            var parentCounts = await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => revisions.Select(r => r.Id).Contains(x.ProcedureRevisionId))
                .GroupBy(x => x.ProcedureRevisionId)
                .Select(x => new { RevisionId = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.RevisionId, x => x.Count, ct);
            var parentIds = await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => revisions.Select(r => r.Id).Contains(x.ProcedureRevisionId))
                .GroupBy(x => x.ProcedureRevisionId)
                .ToDictionaryAsync(x => x.Key, x => x.Select(link => link.CaseRevisionId).ToArray(), ct);
            var selectedRevisionIds = scopedRevisions is null
                ? revisions.GroupBy(x => x.ProcedureId).Select(group => group.OrderByDescending(x => x.Revision).First().Id).ToList()
                : scopedRevisions.Where(x => allIds.Contains(x.Key)).Select(x => x.Value).ToList();
            var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, selectedRevisionIds, ct);
            var coverage = await db.TestCoverage.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var executions = await db.TestExecutions.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            // Keep the order produced by the server-side page (including requested sort). Reordering here by
            // BaseNumber would make page boundaries lie whenever owner/level/title sorting was requested.
            var projected = all
                .Select(x => { var latest = scopedRevisions is not null && scopedRevisions.TryGetValue(x.Id, out var selectedRevisionId)
                        ? revisions.SingleOrDefault(r => r.Id == selectedRevisionId)
                        : revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault();
                    var exactTitle = latest is null || !titles.TryGetValue(latest.Id, out var projectedTitle)
                        ? null
                        : projectedTitle;
                    var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                return new { x.Id, displayNumber = latest is null ? x.BaseNumber : x.BaseNumber + "." + latest.Revision.ToString("D2"),
                    title = exactTitle?.Title ?? "", titleIsExact = exactTitle?.IsExact ?? false,
                    titleIsLegacy = exactTitle?.IsLegacy ?? false, titleNote = exactTitle?.Note,
                    x.OwnerId, version = x.Version, level = x.Level.ToString(), artifactKind = x.ArtifactKind.ToString(),
                    artifactLabel = x.ArtifactKind == VerificationArtifactKind.Case ? "Case" : "Procedure",
                    revisionId = latest?.Id, revision = latest?.Revision, state = latest?.State.ToString(), objective = latest?.Objective,
                    // No selectedApproverId. It existed to route a procedure-level signature, and that
                    // signature is gone; the package's approver is the one who approved this work. The stored
                    // value stays on legacy revisions as the honest record of who was once named.
                    requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id),
                    parentCount = latest is null ? 0 : parentCounts.GetValueOrDefault(latest.Id),
                    preconditions = latest?.Preconditions,
                    steps = latest?.Steps,
                    expectedResult = latest?.ExpectedResult,
                    environmentSetup = latest?.EnvironmentSetup,
                    testData = latest?.TestData,
                    orderedSteps = latest?.OrderedSteps,
                    expectedObservations = latest?.ExpectedObservations,
                    cleanup = latest?.Cleanup,
                    toolingAutomation = latest?.ToolingAutomation,
                    parentKind = latest?.ParentKind.ToString(),
                    derivedRationale = latest?.DerivedRationale,
                    retirementRationale = latest?.RetirementRationale,
                    caseRevisionIds = latest is null ? Array.Empty<Guid>() : parentIds.GetValueOrDefault(latest.Id) ?? [],
                    lastOutcome = lastRun?.Outcome.ToString(), lastExecutedAt = lastRun?.ExecutedAt }; })
                .ToList();
            // Carried on the list response rather than fetched separately, exactly as the requirements
            // workspace carries its own: the views are part of what this list offers, and a second request
            // for them would show a worklist rail that arrives after the worklist.
            var actorId = http.UserAccount().Id;
            var views = await db.SavedProcedureViews.AsNoTracking()
                .Where(x => x.ProjectId == projectId && (x.OwnerId == actorId || x.IsShared))
                .OrderBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.QueryJson, x.ColumnsJson, x.IsShared, owned = x.OwnerId == actorId })
                .ToListAsync(ct);
            return Results.Ok(new { page = currentPage, pageSize = size, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)size),
                views, items = projected });
        });

        app.MapPost("/api/test-{artifactRoute:regex(procedures|cases)}/views", async (string artifactRoute, CreateSavedViewRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, request.ProjectId, ct)) return Results.Forbid();
            var name = (request.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "A saved view needs a name.", code = "saved_view_name_required" });
            // Validated before storage, not on the way out. A view is a worklist somebody else opens, so a
            // field this Explorer cannot apply or a column it cannot show must never reach the record.
            var contract = ProcedureSavedViewContract.Normalize(request.QueryJson, request.ColumnsJson);
            if (!contract.Valid) return Results.BadRequest(new { error = contract.Error, code = "saved_view_contract_invalid" });
            var owner = http.UserAccount().Id;
            if (await db.SavedProcedureViews.AnyAsync(x => x.ProjectId == request.ProjectId && x.OwnerId == owner && x.Name == name, ct))
                return Results.Conflict(new { error = $"You already have a saved view named '{name}'. Rename it, or update the existing one.", code = "saved_view_duplicate_name" });
            var view = new SavedProcedureView(request.ProjectId, owner, name, contract.QueryJson, contract.ColumnsJson, request.IsShared, DateTimeOffset.UtcNow);
            db.SavedProcedureViews.Add(view);
            try { await db.SaveChangesAsync(ct); return Results.Created($"/api/test-{artifactRoute}/views/{view.Id}", new { view.Id }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "A saved view with that name already exists.", code = "saved_view_duplicate_name" }); }
        });

        // Owner-only, and answered as Not Found rather than Forbidden for somebody else's view: a shared view
        // is readable, and confirming that a particular id exists but is not yours is more than a reader of a
        // shared list needs to know.
        app.MapPut("/api/test-{artifactRoute:regex(procedures|cases)}/views/{id:guid}", async (Guid id, UpdateSavedViewRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var owner = http.UserAccount().Id;
            var view = await db.SavedProcedureViews.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct);
            if (view is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            if (request.Name is not null)
            {
                var name = request.Name.Trim();
                if (name.Length == 0) return Results.BadRequest(new { error = "A saved view needs a name.", code = "saved_view_name_required" });
                if (!string.Equals(name, view.Name, StringComparison.Ordinal) && await db.SavedProcedureViews.AnyAsync(x => x.ProjectId == view.ProjectId && x.OwnerId == owner && x.Name == name && x.Id != id, ct))
                    return Results.Conflict(new { error = $"You already have a saved view named '{name}'.", code = "saved_view_duplicate_name" });
                view.Rename(name, now);
            }
            if (request.IsShared is not null) view.SetShared(request.IsShared.Value, now);
            if (request.QueryJson is not null || request.ColumnsJson is not null)
            {
                var contract = ProcedureSavedViewContract.Normalize(request.QueryJson ?? view.QueryJson, request.ColumnsJson ?? view.ColumnsJson);
                if (!contract.Valid) return Results.BadRequest(new { error = contract.Error, code = "saved_view_contract_invalid" });
                view.Replace(contract.QueryJson, contract.ColumnsJson, now);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { view.Id, view.Name, view.IsShared, view.QueryJson, view.ColumnsJson });
        });

        app.MapDelete("/api/test-{artifactRoute:regex(procedures|cases)}/views/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var view = await db.SavedProcedureViews.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == http.UserAccount().Id, ct);
            if (view is null) return Results.NotFound();
            db.Remove(view); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        // No procedure-level approval route. The test change request carrying this procedure is what gets
        // approved, and materialisation writes the revision as Approved on that authority — a separate
        // signature on the procedure revision would be a second approval of the same controlled work. The
        // decision that asked for the procedure is settled by the materialiser, which is where the approved
        // revision now comes into existence.

        app.MapPost("/api/test-executions", async (RecordTestExecutionRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, IProjectLadderPolicyResolver policyResolver,
            CancellationToken ct) =>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.TestEngineer))return Results.Forbid();
            var artifactRevisionId = request.ArtifactRevisionId ?? request.ProcedureRevisionId;
            if (artifactRevisionId == Guid.Empty) return Results.BadRequest(new { error = "A verification artifact revision is required." });
            var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactRevisionId, ct); if (revision is null) return Results.NotFound();
            var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct);
            var artifactWord = ArtifactWord(procedure.Level, procedure.ArtifactKind);
            var artifactNoun = ArtifactNoun(procedure.Level, procedure.ArtifactKind);
            // #726: the write boundary resolves the EFFECTIVE executable kind for every submission, never
            // only when the submitted kind happens to be a software Procedure. Case-only software accepts
            // Cases and rejects software Procedures; the full Case+Procedure profile accepts Procedures and
            // rejects Cases; System accepts System Procedures. Draft, Retired, cross-project, wrong-level,
            // and non-effective identities remain refused below.
            var ladderPolicy = await policyResolver.ResolveAsync(request.ProjectId, ct);
            if (!EffectiveExecutableArtifact.IsExecutable(ladderPolicy, procedure.Level,
                    procedure.ArtifactKind))
                return Results.BadRequest(new
                {
                    error = $"The {artifactWord} is not the effective executable for the project's verification profile; execute the effective executable revision instead.",
                    code = "not_effective_executable"
                });
            if (revision.State != TestProcedureState.Approved) return Results.BadRequest(new { error = $"Only an approved {artifactWord} revision can be executed." });
            if (procedure.ProjectId != request.ProjectId) return Results.BadRequest(new { error = $"The {artifactWord} belongs to a different project." });
            Guid? softwareBuildReleaseId = null;
            if (request.SoftwareBuildId is not null)
            {
                softwareBuildReleaseId = await db.SoftwareBuilds.AsNoTracking()
                    .Where(x => x.Id == request.SoftwareBuildId && x.ProjectId == request.ProjectId)
                    .Select(x => (Guid?)x.ReleaseId).SingleOrDefaultAsync(ct);
                if (softwareBuildReleaseId is null) return Results.BadRequest(new { error = "The software build belongs to a different project." });
            }
            Guid? activeReleaseId = Guid.TryParse(http.Request.Headers["X-AeroLink-Build-Context"].FirstOrDefault(), out var parsedReleaseId)
                ? parsedReleaseId
                : null;
            if (activeReleaseId is not null && softwareBuildReleaseId is not null && softwareBuildReleaseId != activeReleaseId)
                return Results.Conflict(new { error = "The software build belongs to a different active build workspace.", code = "cross_build_resource" });
            var executionReleaseId = activeReleaseId ?? softwareBuildReleaseId;
            // A released build is read-only, and this endpoint has to say so itself.
            //
            // The workspace middleware already refuses this, but only when the caller supplies the build-context
            // header. That is a browser guarantee, not a product one: a service account, an integration or a
            // script that omits the header reached the final validation with the released boundary never
            // checked, and a well-formed request would have written an immutable determination against a
            // released build. Checked here, before the campaign-freeze and retest rules, so no unrelated
            // failure can mask the refusal and make the endpoint look protected when it is not.
            if (executionReleaseId is not null && await db.Releases.AsNoTracking()
                    .AnyAsync(x => x.Id == executionReleaseId && x.ProjectId == request.ProjectId && x.IsReleased, ct))
            {
                var version = await db.Releases.AsNoTracking().Where(x => x.Id == executionReleaseId)
                    .Select(x => x.Version).SingleAsync(ct);
                return Results.Conflict(new
                {
                    error = $"Build {version} is released and read-only. Exit this workspace and select an in-work build to make changes.",
                    code = "released_build_read_only"
                });
            }
            // #422: a build/release-scoped execution is configuration evidence. The exact procedure revision
            // being executed must be the revision the selected configuration's controlled manifest carries.
            // Approved and same-Project are not membership, and coverage rows are not membership.
            //
            // Explicitly scoped requests MUST establish configuration authority:
            // - an exact or compatibility effectivity projection is authoritative, and a mismatched
            //   revision is refused with procedure_revision_not_carried_by_build;
            // - null effectivity means no configuration authority can be established, so the request is
            //   refused with procedure_manifest_unavailable (never a Project-global Approved fallback);
            // - only the completely unscoped path (neither SoftwareBuildId nor X-AeroLink-Build-Context)
            //   may retain the legacy Approved-revision behavior under DEC-097.
            if (request.SoftwareBuildId is not null)
            {
                var buildBaselineId = await db.SoftwareBuilds.AsNoTracking()
                    .Where(x => x.Id == request.SoftwareBuildId && x.ProjectId == request.ProjectId)
                    .Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
                if (buildBaselineId is not null)
                {
                    var effectivity = await TestProcedureEffectivity.ForBaselineAsync(db, buildBaselineId.Value, ct);
                    if (effectivity is null)
                    {
                        return Results.Conflict(new
                        {
                            error = $"The selected build has no controlled {artifactNoun} manifest; an exact carried revision cannot be established.",
                            code = "procedure_manifest_unavailable"
                        });
                    }
                    if (!effectivity.RevisionByProcedure.TryGetValue(procedure.Id, out var carried)
                        || carried != revision.Id)
                    {
                        return Results.Conflict(new
                        {
                            error = $"The exact {artifactNoun} revision is not carried by the selected build's controlled manifest.",
                            code = "procedure_revision_not_carried_by_build"
                        });
                    }
                }
            }
            else if (activeReleaseId is not null)
            {
                if (!await db.Releases.AsNoTracking()
                        .AnyAsync(x => x.Id == activeReleaseId && x.ProjectId == request.ProjectId, ct))
                {
                    return Results.Conflict(new
                    {
                        error = "The active build workspace release does not belong to the requested project.",
                        code = "cross_project_release"
                    });
                }
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                    db, request.ProjectId, activeReleaseId.Value, ct);
                if (effectivity is null)
                {
                    return Results.Conflict(new
                    {
                        error = $"The selected release has no controlled {artifactNoun} manifest; an exact carried revision cannot be established.",
                        code = "procedure_manifest_unavailable"
                    });
                }
                if (!effectivity.RevisionByProcedure.TryGetValue(procedure.Id, out var carried)
                    || carried != revision.Id)
                {
                    return Results.Conflict(new
                    {
                        error = $"The exact {artifactNoun} revision is not carried by the selected release's controlled manifest.",
                        code = "procedure_revision_not_carried_by_build"
                    });
                }
            }
            if (request.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == request.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (request.RetestOfExecutionId is { } predecessorId)
            {
                var predecessor = await (from execution in db.TestExecutions.AsNoTracking()
                                         join priorRevision in db.TestProcedureRevisions.AsNoTracking()
                                             on execution.ProcedureRevisionId equals priorRevision.Id
                                         where execution.Id == predecessorId
                                         select new { Execution = execution, priorRevision.ProcedureId })
                    .SingleOrDefaultAsync(ct);
                if (predecessor is null || predecessor.Execution.ProjectId != request.ProjectId)
                    return Results.BadRequest(new { error = "A retest must reference an execution in the same Project.", code = "retest_target_invalid" });
                // A corrective build can carry the approved successor revision of the procedure that failed.
                // The stable procedure identity is the lineage; demanding the obsolete exact revision would
                // make a legitimate revised corrective test impossible to record.
                if (predecessor.ProcedureId != procedure.Id)
                    return Results.BadRequest(new { error = $"A retest must reference an earlier execution in the same controlled {artifactNoun} lineage.", code = "retest_procedure_mismatch" });
                // The retest link is the structural succession proof. ExecutedAt is a reported fact checked
                // only for consistency: a retest must not be reported as strictly earlier than the execution
                // it supersedes. An equal instant is not evidence of priority (client clocks and second-level
                // input precision can collide), so only a strictly earlier reported time is refused.
                if (predecessor.Execution.ExecutedAt > request.ExecutedAt)
                    return Results.BadRequest(new { error = "A retest cannot be reported earlier than the execution it supersedes.", code = "retest_not_successor" });
            }
            try { var execution = new TestExecution(request.ProjectId, artifactRevisionId, request.SoftwareBuildId, request.RetestOfExecutionId,
                request.Outcome, http.UserAccount().UserName, request.Configuration, request.Determination, request.EvidenceReference, request.ExecutedAt, DateTimeOffset.UtcNow, executionReleaseId);
                db.TestExecutions.Add(execution); await db.SaveChangesAsync(ct); return Results.Created($"/api/test-executions/{execution.Id}", new { execution.Id, outcome = execution.Outcome.ToString() }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/test-executions", async (Guid projectId, Guid? releaseId, Guid? buildId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (releaseId is not null && !await db.Releases.AsNoTracking()
                    .AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new
                {
                    error = "The selected release does not belong to this Project.",
                    code = "release_project_mismatch"
                });
            if (buildId is not null)
            {
                var build = await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == buildId)
                    .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                if (build is null || build.ProjectId != projectId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to this Project.",
                        code = "build_project_mismatch"
                    });
                if (releaseId is not null && build.ReleaseId != releaseId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to the selected release.",
                        code = "build_release_mismatch"
                    });
            }
            var source = db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId && (buildId == null || x.SoftwareBuildId == buildId)
                && (releaseId == null || x.ReleaseId == releaseId
                    || x.ReleaseId == null && x.SoftwareBuildId != null && db.SoftwareBuilds.Any(b => b.Id == x.SoftwareBuildId && b.ReleaseId == releaseId)));
            var rowsQuery = from execution in source join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                              join procedure in db.TestProcedures.AsNoTracking()
                                  on revision.ProcedureId equals procedure.Id
                              select new { execution.Id, artifactRevisionId = revision.Id, displayNumber = procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                  outcome = execution.Outcome.ToString(), execution.ExecutedBy, execution.Configuration, execution.Determination,
                                  execution.EvidenceReference, execution.ExecutedAt, execution.RecordedAt, execution.ReleaseId, execution.SoftwareBuildId, execution.RetestOfExecutionId };
            var rows = await (db.Database.IsSqlite() ? rowsQuery.OrderByDescending(x => x.Id) : rowsQuery.OrderByDescending(x => x.ExecutedAt)).ToListAsync(ct); var rowIds = rows.Select(x => x.Id).ToList();
            var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => rowIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.Size, item.Sha256, item.UploadedAt }).ToListAsync(ct);
            var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
                rows.Select(x => x.artifactRevisionId).Distinct().ToList(), ct);
            return Results.Ok(rows.Select(x => new
            {
                x.Id,
                x.artifactRevisionId,
                procedureRevisionId = x.artifactRevisionId, // compatibility alias for the pre-Case execution contract
                x.displayNumber,
                title = titles[x.artifactRevisionId].Title,
                x.outcome,
                x.ExecutedBy,
                x.Configuration,
                x.Determination,
                x.EvidenceReference,
                x.ExecutedAt,
                x.RecordedAt,
                x.ReleaseId,
                x.SoftwareBuildId,
                x.RetestOfExecutionId,
                evidence = evidence.Where(e => e.TestExecutionId == x.Id).Select(e => new { e.Id, e.OriginalFileName, e.Size, e.Sha256, e.UploadedAt })
            }));
        });

        app.MapGet("/api/verification-coverage", async (Guid projectId, Guid? baselineId, Guid? buildId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (buildId is not null)
            {
                var build = await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == buildId)
                    .Select(x => new { x.ProjectId, x.BaselineId }).SingleOrDefaultAsync(ct);
                if (build is null || build.ProjectId != projectId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to this Project.",
                        code = "build_project_mismatch"
                    });
                if (baselineId is not null && baselineId != build.BaselineId)
                    return Results.BadRequest(new
                    {
                        error = "The selected baseline is not the baseline carried by this software build.",
                        code = "baseline_build_mismatch"
                    });
                baselineId = build.BaselineId;
            }
            if (baselineId is null) return Results.BadRequest(new { error = "Select a materialized baseline or software build." });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var requirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                                      join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                      join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                      orderby artifact.BaseNumber select new { artifact.Id, revisionId = revision.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, revision.Statement }).ToListAsync(ct);
            var requirementIds = requirements.Select(x => x.revisionId).ToList();
            var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId.Value, ct);
            var coverageLinks = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, requirementIds, ct,
                buildScoped: true, effectiveProcedureRevisionIds: procedureEffectivity?.RevisionIds ?? []);
            var procedureRevisionIds = coverageLinks.Select(x => x.ProcedureRevisionId).Distinct().ToList();
            var executions = await db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && (buildId == null || x.SoftwareBuildId == buildId)).ToListAsync(ct);
            var items = requirements.Select(req =>
            {
                var coveredBy = coverageLinks.Where(x => x.RequirementRevisionId == req.revisionId).Select(link =>
                {
                    var latest = executions.Where(e => e.ProcedureRevisionId == link.ProcedureRevisionId)
                        .OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                    return new
                    {
                        artifactId = link.ProcedureId,
                        procedureId = link.ProcedureId, // compatibility alias for the pre-Case contract
                        artifactKind = link.ArtifactKind,
                        level = link.Level,
                        revisionId = link.ProcedureRevisionId,
                        link.DisplayNumber,
                        link.Title,
                        state = link.ProcedureState,
                        link.IsSuspect,
                        link.CoverageState,
                        latestOutcome = latest?.Outcome.ToString(),
                        latestExecutionId = latest?.Id
                    };
                }).ToList();
                var disposition = coveredBy.Any(x => x.CoverageState == "Confirmed")
                    ? RequirementCoverageState.Covered
                    : coveredBy.Count != 0 ? RequirementCoverageState.Suspect : RequirementCoverageState.Uncovered;
                var covered = disposition == RequirementCoverageState.Covered;
                return new { req.Id, req.revisionId, req.displayNumber, req.Statement, disposition, covered, verified = coveredBy.Any(x => x.CoverageState == "Confirmed" && x.latestOutcome == "Pass"), coveredBy };
            }).ToList();
            return Results.Ok(new
            {
                baselineId,
                buildId,
                total = items.Count,
                covered = items.Count(x => x.disposition == RequirementCoverageState.Covered),
                suspect = items.Count(x => x.disposition == RequirementCoverageState.Suspect),
                verified = items.Count(x => x.verified),
                uncovered = items.Count(x => x.disposition == RequirementCoverageState.Uncovered),
                items
            });
        });
    }

    private static async Task<Guid?> ExactLinkProjectIdAsync(ExactLinkKind kind, Guid linkId,
        AeroLinkDbContext db, CancellationToken ct)
    {
        if (kind == ExactLinkKind.RequirementTrace)
            return await db.RequirementTraces.AsNoTracking().Where(x => x.Id == linkId)
                .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
        if (kind == ExactLinkKind.CaseProcedure)
            return await (from link in db.TestCaseProcedureLinks.AsNoTracking().Where(x => x.Id == linkId)
                          join revision in db.TestProcedureRevisions.AsNoTracking()
                              on link.CaseRevisionId equals revision.Id
                          join artifact in db.TestProcedures.AsNoTracking()
                              on revision.ProcedureId equals artifact.Id
                          select (Guid?)artifact.ProjectId).SingleOrDefaultAsync(ct);
        return null;
    }

    private static async Task<IResult> ReadExactLinkLifecycleAsync(ExactLinkKind kind, Guid linkId,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var projectId = await ExactLinkProjectIdAsync(kind, linkId, db, ct);
        if (projectId is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
        var lifecycle = await db.ExactLinkSuspectLifecycles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LinkKind == kind && x.LinkId == linkId, ct);
        if (lifecycle is null) return Results.Ok(new
        {
            linkId, linkKind = kind.ToString(), lifecycleId = (Guid?)null,
            state = kind == ExactLinkKind.RequirementTrace ? (string?)null : "Confirmed",
            causeKind = (string?)null, causeRequirementRevisionId = (Guid?)null,
            causeBaselineImportId = (Guid?)null, causeVerificationRevisionId = (Guid?)null,
            raisedBy = (string?)null, raisedAt = (DateTimeOffset?)null, raisedRationale = (string?)null,
            acknowledgedBy = (string?)null, acknowledgedAt = (DateTimeOffset?)null,
            acknowledgementRationale = (string?)null, outcome = (string?)null, resolvedBy = (string?)null,
            resolvedAt = (DateTimeOffset?)null, resolutionRationale = (string?)null, events = Array.Empty<object>(),
        });
        var events = (await db.ExactLinkSuspectEvents.AsNoTracking()
                .Where(x => x.LifecycleId == lifecycle.Id).ToListAsync(ct))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => new
            {
                id = x.Id, type = x.EventType.ToString(), x.ActorId, x.OccurredAt, x.Rationale,
                causeKind = x.CauseKind.ToString(), x.CauseRequirementRevisionId,
                x.CauseBaselineImportId, x.CauseVerificationRevisionId,
                outcome = x.Outcome == null ? null : x.Outcome.ToString(),
            }).ToList();
        return Results.Ok(new
        {
            linkId, linkKind = lifecycle.LinkKind.ToString(), lifecycleId = lifecycle.Id,
            state = lifecycle.State.ToString(), causeKind = lifecycle.CauseKind.ToString(),
            lifecycle.CauseRequirementRevisionId, lifecycle.CauseBaselineImportId,
            lifecycle.CauseVerificationRevisionId, raisedBy = lifecycle.RaisedBy,
            raisedAt = lifecycle.RaisedAt, raisedRationale = lifecycle.RaisedRationale,
            acknowledgedBy = lifecycle.AcknowledgedBy, acknowledgedAt = lifecycle.AcknowledgedAt,
            acknowledgementRationale = lifecycle.AcknowledgementRationale,
            outcome = lifecycle.Outcome?.ToString(), resolvedBy = lifecycle.ResolvedBy,
            resolvedAt = lifecycle.ResolvedAt, resolutionRationale = lifecycle.ResolutionRationale, events,
        });
    }

    private static async Task<IResult> MutateExactLinkLifecycleAsync(ExactLinkKind kind, Guid linkId,
        string rationale, ExactLinkResolutionOutcome? outcome, HttpContext http, AeroLinkDbContext db,
        IdentityService identity, ExactLinkLifecycleService service, CancellationToken ct)
    {
        var projectId = await ExactLinkProjectIdAsync(kind, linkId, db, ct);
        if (projectId is null) return Results.NotFound();
        var authorized = kind == ExactLinkKind.RequirementTrace
            ? await http.HasProjectRoleAsync(db, identity, projectId.Value, ct,
                ProgramRole.Engineer, ProgramRole.ConfigurationManager)
            : await http.HasProjectRoleAsync(db, identity, projectId.Value, ct,
                ProgramRole.TestEngineer, ProgramRole.TestLead, ProgramRole.ConfigurationManager);
        if (!authorized) return Results.Forbid();
        var codePrefix = kind == ExactLinkKind.RequirementTrace ? "trace" : "case_procedure";
        var noun = kind == ExactLinkKind.RequirementTrace ? "trace" : "Case-to-Procedure";
        try
        {
            var lifecycle = outcome is null
                ? await service.AcknowledgeAsync(kind, linkId, http.UserAccount().UserName,
                    rationale, DateTimeOffset.UtcNow, ct)
                : await service.ResolveAsync(kind, linkId, outcome.Value, http.UserAccount().UserName,
                    rationale, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new
            {
                linkId, state = lifecycle.State.ToString(), outcome = lifecycle.Outcome?.ToString(),
                acknowledgedBy = lifecycle.AcknowledgedBy, acknowledgedAt = lifecycle.AcknowledgedAt,
                resolvedBy = lifecycle.ResolvedBy, resolvedAt = lifecycle.ResolvedAt,
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new
            {
                error = $"The exact {noun} lifecycle changed; reload its immutable event history and retry.",
                code = $"{codePrefix}_lifecycle_concurrency",
            });
        }
        catch (DomainException ex)
        {
            return Results.Conflict(new { error = ex.Message, code = $"{codePrefix}_lifecycle_mutation_refused" });
        }
    }

    private sealed record PathProcedureCandidate(
        Guid Id,
        Guid RevisionId,
        string DisplayNumber,
        string Title,
        string Level,
        string ArtifactKind,
        string State);

    private sealed record ProcedureListItem(Guid Id, string BaseNumber, string OwnerId,
        TestProcedureLevel Level, VerificationArtifactKind ArtifactKind, DateTimeOffset CreatedAt, long Version);

    private static string ArtifactWord(TestProcedureLevel level, VerificationArtifactKind kind) =>
        level == TestProcedureLevel.System || kind == VerificationArtifactKind.Procedure
            ? "test procedure" : "test case";

    private static string ArtifactNoun(TestProcedureLevel level, VerificationArtifactKind kind) =>
        level == TestProcedureLevel.System || kind == VerificationArtifactKind.Procedure ? "procedure" : "case";

    private static bool ArtifactRouteAllows(string artifactRoute, TestProcedureLevel level,
        VerificationArtifactKind? kind = null) =>
        !string.Equals(artifactRoute, "cases", StringComparison.OrdinalIgnoreCase)
        || (level is TestProcedureLevel.HighLevel or TestProcedureLevel.LowLevel
            && (kind is null || kind == VerificationArtifactKind.Case));
}
