using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
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

        app.MapGet("/api/evidence/{id:guid}", async (Guid id, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct) =>
        {
            var evidence = await db.EvidenceRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); return evidence is null ? Results.NotFound() : Results.File(store.OpenRead(evidence.StorageKey), evidence.ContentType, evidence.OriginalFileName, enableRangeProcessing: true);
        });

        app.MapPost("/api/trace-links", async (CreateTraceLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.Id == request.SourceRevisionId || x.Id == request.TargetRevisionId)
                                   join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                   select new { revision.Id, artifact.ProjectId }).ToListAsync(ct);
            if (revisions.Count != 2) return Results.BadRequest(new { error = "Both exact requirement revisions must exist." });
            if (revisions.Any(x => x.ProjectId != request.ProjectId)) return Results.BadRequest(new { error = "Both revisions must belong to the selected project." });
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (await (from member in db.BaselineRequirements.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId))
                       join campaign in db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == request.ProjectId && x.State == ReleaseCampaignState.InReview) on member.BaselineId equals campaign.BaselineId
                       select member.Id).AnyAsync(ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            try { var link = new RequirementTraceLink(request.ProjectId, request.SourceRevisionId, request.TargetRevisionId, request.Type, request.Rationale, DateTimeOffset.UtcNow); db.RequirementTraces.Add(link); await db.SaveChangesAsync(ct); return Results.Created($"/api/trace-links/{link.Id}", new { link.Id }); }
            catch (Exception ex) when (ex is DomainException or DbUpdateException) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/trace-links/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var link = await db.RequirementTraces.SingleOrDefaultAsync(x => x.Id == id, ct); if (link is null) return Results.NotFound();
            if(!await http.HasProjectRoleAsync(db,identity,link.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
            var revisionIds = new[] { link.SourceRevisionId, link.TargetRevisionId };
            if(await db.BaselineRequirements.AsNoTracking().AnyAsync(x=>revisionIds.Contains(x.RevisionId),ct))
                return Results.Conflict(new{error="Trace links involving a baselined requirement revision are controlled history and cannot be deleted. Create the corrected revision and superseding link instead.",code="controlled_trace_history"});
            db.RequirementTraces.Remove(link); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapGet("/api/traceability", async (Guid projectId, Guid? baselineId, string? search, int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
        {
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            if (baselineId is null) baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null).OrderByDescending(x => x.FrozenAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (baselineId is null) return Results.Ok(new { page, pageSize, totalCount = 0, items = Array.Empty<object>() });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                         join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                         join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                         select new { artifact, revision };
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct); var selected = await source.OrderBy(x => x.artifact.BaseNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var selectedIds = selected.Select(x => x.revision.Id).ToList(); var links = await db.RequirementTraces.AsNoTracking().Where(x => selectedIds.Contains(x.SourceRevisionId) || selectedIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
            var relatedIds = links.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
            var related = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => relatedIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id select new { revision.Id, artifact.BaseNumber, revision.Revision, level = artifact.Level.ToString() }).ToDictionaryAsync(x => x.Id, ct);
            var coverage = await db.TestCoverage.AsNoTracking().Where(x => selectedIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
            var procedureRevisionIds=coverage.Select(x=>x.ProcedureRevisionId).Distinct().ToList();
            var procedureRevisions=await db.TestProcedureRevisions.AsNoTracking().Where(x=>procedureRevisionIds.Contains(x.Id)).ToListAsync(ct);
            var procedureIds=procedureRevisions.Select(x=>x.ProcedureId).Distinct().ToList();
            var procedures=await db.TestProcedures.AsNoTracking().Where(x=>procedureIds.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);
            var executionQuery=db.TestExecutions.AsNoTracking().Where(x=>procedureRevisionIds.Contains(x.ProcedureRevisionId));
            var executions=await(db.Database.IsSqlite()?executionQuery.OrderByDescending(x=>x.Id):executionQuery.OrderByDescending(x=>x.ExecutedAt)).ToListAsync(ct);
            var executionIds=executions.Select(x=>x.Id).ToList();
            var evidence=await(from link in db.TestExecutionEvidence.AsNoTracking().Where(x=>executionIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new{link.TestExecutionId,item.Id,item.OriginalFileName,item.Sha256,item.Size,item.UploadedAt}).ToListAsync(ct);
            var items = selected.Select(x => new { x.artifact.Id, revisionId = x.revision.Id, displayNumber = x.artifact.BaseNumber + "." + x.revision.Revision.ToString("D2"), level = x.artifact.Level.ToString(), x.revision.Statement,
                parents = links.Where(l => l.SourceRevisionId == x.revision.Id).Select(l => new { id = l.TargetRevisionId, displayNumber = related[l.TargetRevisionId].BaseNumber + "." + related[l.TargetRevisionId].Revision.ToString("D2"), related[l.TargetRevisionId].level, type = l.Type.ToString() }),
                children = links.Where(l => l.TargetRevisionId == x.revision.Id).Select(l => new { id = l.SourceRevisionId, displayNumber = related[l.SourceRevisionId].BaseNumber + "." + related[l.SourceRevisionId].Revision.ToString("D2"), related[l.SourceRevisionId].level, type = l.Type.ToString() }),
                testCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id),
                tests=coverage.Where(c=>c.RequirementRevisionId==x.revision.Id).Select(c=>{var revision=procedureRevisions.Single(r=>r.Id==c.ProcedureRevisionId);var procedure=procedures[revision.ProcedureId];return new{procedureId=procedure.Id,revisionId=revision.Id,displayNumber=$"{procedure.BaseNumber}.{revision.Revision:D2}",procedure.Title,level=procedure.Level.ToString(),executions=executions.Where(e=>e.ProcedureRevisionId==revision.Id).Select(e=>new{e.Id,outcome=e.Outcome.ToString(),e.ExecutedBy,e.ExecutedAt,e.RecordedAt,e.SoftwareBuildId,e.RetestOfExecutionId,e.Determination,e.EvidenceReference,evidence=evidence.Where(a=>a.TestExecutionId==e.Id).Select(a=>new{a.Id,a.OriginalFileName,a.Sha256,a.Size,a.UploadedAt})})};}) });
            return Results.Ok(new { baselineId, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        app.MapGet("/api/test-procedures", async (Guid projectId, string? search, string? scope, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel||x.Level==TestProcedureLevel.LowLevel);
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q)); }
            var items = await source.OrderBy(x => x.BaseNumber).Select(x => new { x.Id, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt }).ToListAsync(ct);
            var ids = items.Select(x => x.Id).ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.ProcedureId)).ToListAsync(ct);
            var revisionIds = revisions.Select(x => x.Id).ToList(); var coverage = await db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var executions = await db.TestExecutions.AsNoTracking().Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            return Results.Ok(items.Select(x => { var latest = revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault(); var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                return new { x.Id, displayNumber = latest is null ? x.BaseNumber : x.BaseNumber + "." + latest.Revision.ToString("D2"), x.Title, x.OwnerId, level = x.Level.ToString(),
                    revisionId = latest?.Id, revision = latest?.Revision, state = latest?.State.ToString(), objective = latest?.Objective,
                    requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id), lastOutcome = lastRun?.Outcome.ToString(), lastExecutedAt = lastRun?.ExecutedAt }; }));
        });

        app.MapPost("/api/test-procedures", async (CreateTestProcedureRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.TestEngineer))return Results.Forbid();
            await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            var requirementIds = request.RequirementRevisionIds.Distinct().ToList();
            var validRequirementIds = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x=>requirementIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId) on revision.ArtifactId equals artifact.Id select revision.Id).CountAsync(ct);
            if (validRequirementIds != requirementIds.Count) return Results.BadRequest(new { error = "Every coverage link must reference an authoritative requirement revision in this project." });
            if (requirementIds.Count > 0 && await (from member in db.BaselineRequirements.AsNoTracking().Where(x => requirementIds.Contains(x.RevisionId))
                                                   join campaign in db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == request.ProjectId && x.State == ReleaseCampaignState.InReview) on member.BaselineId equals campaign.BaselineId
                                                   select member.Id).AnyAsync(ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            try { var actor = http.UserAccount().UserName; var baseNumber=await IdentifierAllocator.NextTestProcedureAsync(db,request.Level,ct);var procedure = new TestProcedure(request.ProjectId, baseNumber, request.Title, actor, DateTimeOffset.UtcNow, request.Level);
                var revision = new TestProcedureRevision(procedure.Id, 0, request.Objective, request.Preconditions, request.Steps, request.ExpectedResult, TestProcedureState.Draft, actor, DateTimeOffset.UtcNow);
                db.AddRange(procedure, revision); db.TestCoverage.AddRange(requirementIds.Select(id => new TestRequirementCoverage(revision.Id, id))); await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
                return Results.Created($"/api/test-procedures/{procedure.Id}", new { procedure.Id, revisionId = revision.Id, displayNumber = procedure.BaseNumber + ".00", state = revision.State.ToString() }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Another test procedure was created at the same instant. Submit again to receive the next server number." }); }
        });

        app.MapPost("/api/test-procedures/{revisionId:guid}/approve", async (Guid revisionId, SignatureRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var revision = await db.TestProcedureRevisions.SingleOrDefaultAsync(x => x.Id == revisionId, ct); if (revision is null) return Results.NotFound();
            var procedure = await db.TestProcedures.SingleAsync(x => x.Id == revision.ProcedureId, ct);
            if (!await http.HasProjectRoleAsync(db, identity, procedure.ProjectId, ct, ProgramRole.Approver)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Meaning)) return Results.BadRequest(new { error = "An explicit electronic signature meaning is required." });
            var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            try
            {
                revision.Approve(actor.UserName);
                var requirementRevisionIds = await db.TestCoverage.AsNoTracking().Where(x => x.ProcedureRevisionId == revision.Id).OrderBy(x => x.RequirementRevisionId).Select(x => x.RequirementRevisionId).ToListAsync(ct);
                var snapshot = JsonSerializer.Serialize(new { procedureId = procedure.Id, procedure.ProjectId, procedure.BaseNumber, procedure.Title, procedure.Level, revisionId = revision.Id, revision.Revision, revision.Objective, revision.Preconditions, revision.Steps, revision.ExpectedResult, revision.AuthorId, requirementRevisionIds });
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
                var programId = await db.Projects.AsNoTracking().Where(x => x.Id == procedure.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
                var now = DateTimeOffset.UtcNow;
                db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "TestProcedureRevision", revision.Id, $"{procedure.BaseNumber}.{revision.Revision:D2}", "Approve", request.Meaning, contentHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
                db.UserNotifications.Add(new(procedure.ProjectId, revision.AuthorId, "ProcedureApproved", $"Procedure {procedure.BaseNumber}.{revision.Revision:D2} approved", $"{actor.DisplayName} approved the controlled procedure revision for execution.", "verification", procedure.Id, now));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { revision.Id, state = revision.State.ToString(), contentHash });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-executions", async (RecordTestExecutionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.TestEngineer))return Results.Forbid();
            var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ProcedureRevisionId, ct); if (revision is null) return Results.NotFound();
            if (revision.State != TestProcedureState.Approved) return Results.BadRequest(new { error = "Only an approved test procedure revision can be executed." });
            var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct); if (procedure.ProjectId != request.ProjectId) return Results.BadRequest(new { error = "The test procedure belongs to a different project." });
            if (request.SoftwareBuildId is not null && !await db.SoftwareBuilds.AnyAsync(x => x.Id == request.SoftwareBuildId && x.ProjectId == request.ProjectId, ct)) return Results.BadRequest(new { error = "The software build belongs to a different project." });
            if (request.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == request.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (request.RetestOfExecutionId is not null && !await db.TestExecutions.AnyAsync(x => x.Id == request.RetestOfExecutionId && x.ProcedureRevisionId == request.ProcedureRevisionId, ct)) return Results.BadRequest(new { error = "A retest must reference an earlier execution of the same procedure revision." });
            try { var execution = new TestExecution(request.ProjectId, request.ProcedureRevisionId, request.SoftwareBuildId, request.RetestOfExecutionId,
                request.Outcome, http.UserAccount().UserName, request.Configuration, request.Determination, request.EvidenceReference, request.ExecutedAt, DateTimeOffset.UtcNow);
                db.TestExecutions.Add(execution); await db.SaveChangesAsync(ct); return Results.Created($"/api/test-executions/{execution.Id}", new { execution.Id, outcome = execution.Outcome.ToString() }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/test-executions", async (Guid projectId, Guid? buildId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var source = db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId && (buildId == null || x.SoftwareBuildId == buildId));
            var rowsQuery = from execution in source join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                              join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                              select new { execution.Id, procedureRevisionId = revision.Id, displayNumber = procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                  procedure.Title, outcome = execution.Outcome.ToString(), execution.ExecutedBy, execution.Configuration, execution.Determination,
                                  execution.EvidenceReference, execution.ExecutedAt, execution.RecordedAt, execution.SoftwareBuildId, execution.RetestOfExecutionId };
            var rows = await (db.Database.IsSqlite() ? rowsQuery.OrderByDescending(x => x.Id) : rowsQuery.OrderByDescending(x => x.ExecutedAt)).ToListAsync(ct); var rowIds = rows.Select(x => x.Id).ToList();
            var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => rowIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.Size, item.Sha256, item.UploadedAt }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, x.procedureRevisionId, x.displayNumber, x.Title, x.outcome, x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt, x.SoftwareBuildId, x.RetestOfExecutionId, evidence = evidence.Where(e => e.TestExecutionId == x.Id).Select(e => new { e.Id, e.OriginalFileName, e.Size, e.Sha256, e.UploadedAt }) }));
        });

        app.MapGet("/api/verification-coverage", async (Guid projectId, Guid? baselineId, Guid? buildId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (buildId is not null) baselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
            if (baselineId is null) return Results.BadRequest(new { error = "Select a materialized baseline or software build." });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var requirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                                      join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                      join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                      orderby artifact.BaseNumber select new { artifact.Id, revisionId = revision.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, revision.Statement }).ToListAsync(ct);
            var requirementIds = requirements.Select(x => x.revisionId).ToList(); var links = await db.TestCoverage.AsNoTracking().Where(x => requirementIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
            var procedureRevisionIds = links.Select(x => x.ProcedureRevisionId).Distinct().ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)).ToListAsync(ct);
            var procedures = await db.TestProcedures.AsNoTracking().Where(x => revisions.Select(r => r.ProcedureId).Contains(x.Id)).ToListAsync(ct);
            var executions = await db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && (buildId == null || x.SoftwareBuildId == buildId)).ToListAsync(ct);
            var items = requirements.Select(req => { var coveredBy = links.Where(x => x.RequirementRevisionId == req.revisionId).Select(link => { var rev = revisions.Single(r => r.Id == link.ProcedureRevisionId); var proc = procedures.Single(p => p.Id == rev.ProcedureId); var latest = executions.Where(e => e.ProcedureRevisionId == rev.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault(); return new { revisionId = rev.Id, displayNumber = proc.BaseNumber + "." + rev.Revision.ToString("D2"), proc.Title, latestOutcome = latest?.Outcome.ToString(), latestExecutionId = latest?.Id }; }).ToList(); return new { req.Id, req.revisionId, req.displayNumber, req.Statement, covered = coveredBy.Count > 0, verified = coveredBy.Any(x => x.latestOutcome == "Pass"), coveredBy }; }).ToList();
            return Results.Ok(new { baselineId, buildId, total = items.Count, covered = items.Count(x => x.covered), verified = items.Count(x => x.verified), uncovered = items.Count(x => !x.covered), items });
        });
    }
}
