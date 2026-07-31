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
            var coverage = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db,selectedIds,ct);
            var procedureRevisionIds=coverage.Select(x=>x.ProcedureRevisionId).Distinct().ToList();
            var executionQuery=db.TestExecutions.AsNoTracking().Where(x=>procedureRevisionIds.Contains(x.ProcedureRevisionId));
            var executions=await(db.Database.IsSqlite()?executionQuery.OrderByDescending(x=>x.Id):executionQuery.OrderByDescending(x=>x.ExecutedAt)).ToListAsync(ct);
            var executionIds=executions.Select(x=>x.Id).ToList();
            var evidence=await(from link in db.TestExecutionEvidence.AsNoTracking().Where(x=>executionIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new{link.TestExecutionId,item.Id,item.OriginalFileName,item.Sha256,item.Size,item.UploadedAt}).ToListAsync(ct);
            var items = selected.Select(x => new { x.artifact.Id, revisionId = x.revision.Id, displayNumber = x.artifact.BaseNumber + "." + x.revision.Revision.ToString("D2"), level = x.artifact.Level.ToString(), x.revision.Statement,
                parents = links.Where(l => l.SourceRevisionId == x.revision.Id).Select(l => new { id = l.TargetRevisionId, displayNumber = related[l.TargetRevisionId].BaseNumber + "." + related[l.TargetRevisionId].Revision.ToString("D2"), related[l.TargetRevisionId].level, type = l.Type.ToString() }),
                children = links.Where(l => l.TargetRevisionId == x.revision.Id).Select(l => new { id = l.SourceRevisionId, displayNumber = related[l.SourceRevisionId].BaseNumber + "." + related[l.SourceRevisionId].Revision.ToString("D2"), related[l.SourceRevisionId].level, type = l.Type.ToString() }),
                testCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && !c.IsSuspect),
                suspectTestCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && c.IsSuspect),
                tests=coverage.Where(c=>c.RequirementRevisionId==x.revision.Id).Select(c=>new{procedureId=c.ProcedureId,revisionId=c.ProcedureRevisionId,c.DisplayNumber,c.Title,c.Level,state=c.ProcedureState,c.IsSuspect,c.CoverageState,executions=executions.Where(e=>e.ProcedureRevisionId==c.ProcedureRevisionId).Select(e=>new{e.Id,outcome=e.Outcome.ToString(),e.ExecutedBy,e.ExecutedAt,e.RecordedAt,e.SoftwareBuildId,e.RetestOfExecutionId,e.Determination,e.EvidenceReference,evidence=evidence.Where(a=>a.TestExecutionId==e.Id).Select(a=>new{a.Id,a.OriginalFileName,a.Sha256,a.Size,a.UploadedAt})})}) });
            return Results.Ok(new { baselineId, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        // The workspace rendered every procedure it was given — 440 cards on the software side — with no
        // search, filter or page. This returns a bounded page and the total, and every predicate below runs
        // in the database, because a page of twenty-five that costs a full table read is not paging.
        /// How a procedure came to say what it says.
        ///
        /// A procedure is read by somebody deciding whether to trust it, and "who wrote this, when, and what
        /// made them change it" is most of that decision. Its revisions were reachable only by reading the
        /// procedure itself, one revision at a time, with no way to see what drove any of them.
        ///
        /// The change request behind a revision is not recorded on the revision — it is reached through the
        /// verification decision that resolved to it, which is the record that actually connects the two. A
        /// revision written outside that path has no change request, and says so rather than guessing.
        app.MapGet("/api/test-procedures/{id:guid}/history", async (Guid id, Guid? revisionId, Guid? releaseId, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt })
                .SingleOrDefaultAsync(ct);
            if (procedure is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

            var revisions = (await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == id).ToListAsync(ct))
                .OrderByDescending(x => x.Revision).ToList();
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (revisionId is not null && !revisionIds.Contains(revisionId.Value)) return Results.NotFound();
            if (releaseId is not null && revisionId is not null)
            {
                var effectiveBaselineId = await BuildScope.EffectiveBaselineAsync(db, procedure.ProjectId, releaseId.Value, ct);
                if (effectiveBaselineId is null) return Results.NotFound();
                var belongsToBuild = await db.TestCoverage.AsNoTracking().AnyAsync(link =>
                    link.ProcedureRevisionId == revisionId
                    && db.BaselineRequirements.AsNoTracking().Any(member =>
                        member.BaselineId == effectiveBaselineId && member.RevisionId == link.RequirementRevisionId), ct);
                if (!belongsToBuild) return Results.NotFound();
            }

            // What each revision answered for: the verification decision that resolved to it, the package
            // that decision belonged to, and the change request that package was raised from.
            var drivers = await (from item in db.VerificationImpactItems.AsNoTracking()
                                 join review in db.TestChangeReviews.AsNoTracking() on item.TestChangeReviewId equals review.Id
                                 where item.ResolvedProcedureRevisionId != null && revisionIds.Contains(item.ResolvedProcedureRevisionId.Value)
                                 select new
                                 {
                                     RevisionId = item.ResolvedProcedureRevisionId!.Value,
                                     item.SubjectDisplayNumber,
                                     ChangeRequest = review.SourceChangeRequestNumber,
                                     Package = review.BaseNumber,
                                     Action = item.ProcedureChangeAction,
                                 }).ToListAsync(ct);

            // The requirements each revision covers, so a reader sees what it is for without leaving the page.
            var coverage = await (from link in db.TestCoverage.AsNoTracking()
                                  join revision in db.RequirementRevisions.AsNoTracking() on link.RequirementRevisionId equals revision.Id
                                  join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                  where revisionIds.Contains(link.ProcedureRevisionId)
                                  select new { link.ProcedureRevisionId, artifact.BaseNumber, revision.Revision }).ToListAsync(ct);

            return Results.Ok(new
            {
                procedure.Id,
                procedure.BaseNumber,
                procedure.Title,
                level = procedure.Level.ToString(),
                procedure.OwnerId,
                procedure.CreatedAt,
                selectedRevisionId = revisionId,
                revisions = revisions.Select(revision => new
                {
                    revision.Id,
                    displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
                    revision.Revision,
                    state = revision.State.ToString(),
                    revision.AuthorId,
                    revision.CreatedAt,
                    revision.Objective,
                    revision.Preconditions,
                    revision.Steps,
                    revision.ExpectedResult,
                    selected = revision.Id == revisionId,
                    drivenBy = drivers.Where(x => x.RevisionId == revision.Id)
                        .Select(x => new { x.ChangeRequest, x.Package, x.SubjectDisplayNumber, action = x.Action.ToString() })
                        .Distinct().ToList(),
                    covers = coverage.Where(x => x.ProcedureRevisionId == revision.Id)
                        .Select(x => $"{x.BaseNumber}.{x.Revision:D2}").Distinct().OrderBy(x => x).ToList(),
                }).ToList(),
            });
        });

        app.MapGet("/api/test-procedures", async (Guid projectId, Guid? releaseId, string? search, string? scope, string? state,
            string? owner, string? outcome, Guid? requirementRevisionId, string? sort, int? page, int? pageSize,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            // This endpoint read a Project's controlled procedures without checking the caller was in it.
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
            Dictionary<Guid, Guid>? scopedRevisions = null;
            if(releaseId is not null)
            {
                var effectiveBaselineId=await BuildScope.EffectiveBaselineAsync(db,projectId,releaseId.Value,ct);
                if(effectiveBaselineId is null)return Results.Ok(new{page=currentPage,pageSize=size,totalCount=0,totalPages=0,items=Array.Empty<object>()});
                var effectiveRows = await (from coverageLink in db.TestCoverage.AsNoTracking()
                                           join member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == effectiveBaselineId) on coverageLink.RequirementRevisionId equals member.RevisionId
                                           join revision in db.TestProcedureRevisions.AsNoTracking() on coverageLink.ProcedureRevisionId equals revision.Id
                                           join procedure in db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId) on revision.ProcedureId equals procedure.Id
                                           select new { procedure.Id, RevisionId = revision.Id, revision.Revision }).ToListAsync(ct);
                scopedRevisions = effectiveRows.GroupBy(x => x.Id).ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(x => x.Revision).First().RevisionId);
                var effectiveProcedureIds = scopedRevisions.Keys.ToList();
                source=source.Where(x=>effectiveProcedureIds.Contains(x.Id));
            }
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel||x.Level==TestProcedureLevel.LowLevel);
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q)); }
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
                "title" => source.OrderBy(x => x.Title).ThenBy(x => x.BaseNumber),
                "owner" => source.OrderBy(x => x.OwnerId).ThenBy(x => x.BaseNumber),
                "level" => source.OrderBy(x => x.Level).ThenBy(x => x.BaseNumber),
                _ => source.OrderBy(x => x.BaseNumber).ThenBy(x => x.BaseNumber),
            };
            var items = await ordered.Skip((currentPage - 1) * size).Take(size)
                .Select(x => new { x.Id, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt }).ToListAsync(ct);
            var ids = items.Select(x => x.Id).ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.ProcedureId)).ToListAsync(ct);
            var selectedRevisionIds = scopedRevisions is null
                ? revisions.GroupBy(x => x.ProcedureId).Select(group => group.OrderByDescending(x => x.Revision).First().Id).ToList()
                : scopedRevisions.Where(x => ids.Contains(x.Key)).Select(x => x.Value).ToList();
            var coverage = await db.TestCoverage.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var executions = await db.TestExecutions.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            return Results.Ok(new { page = currentPage, pageSize = size, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)size),
                items = items.Select(x => { var latest = scopedRevisions is not null && scopedRevisions.TryGetValue(x.Id, out var selectedRevisionId)
                        ? revisions.SingleOrDefault(r => r.Id == selectedRevisionId)
                        : revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault();
                    var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                return new { x.Id, displayNumber = latest is null ? x.BaseNumber : x.BaseNumber + "." + latest.Revision.ToString("D2"), x.Title, x.OwnerId, level = x.Level.ToString(),
                    revisionId = latest?.Id, revision = latest?.Revision, state = latest?.State.ToString(), objective = latest?.Objective,
                    selectedApproverId = latest?.SelectedApproverId,
                    requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id), lastOutcome = lastRun?.Outcome.ToString(), lastExecutedAt = lastRun?.ExecutedAt }; }) });
        });

        app.MapPost("/api/test-procedures", async (CreateTestProcedureRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.TestEngineer))return Results.Forbid();
            if (request.RequirementRevisionIds.Count == 0)
                return Results.BadRequest(new { error = "Select at least one exact requirement revision from a materialized baseline.", code = "materialized_requirement_required" });
            if (string.IsNullOrWhiteSpace(request.ApproverId))
                return Results.BadRequest(new { error = "Select an independent procedure approver." });
            var selectedApprover = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserName == request.ApproverId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
            if (selectedApprover is null) return Results.BadRequest(new { error = "Select an active AeroLink procedure approver." });
            var procedureProgramId = await db.Projects.Where(x => x.Id == request.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
            if (!await identity.HasRoleAsync(selectedApprover.Id, procedureProgramId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                return Results.BadRequest(new { error = $"{selectedApprover.DisplayName} does not hold Approver authority for this Program." });
            if (string.Equals(selectedApprover.UserName, http.UserAccount().UserName, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "The procedure approver must be independent from its author." });
            await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            var requirementIds = request.RequirementRevisionIds.Distinct().ToList();
            if (requirementIds.Count == 0)
                return Results.BadRequest(new { error = "Select at least one exact requirement revision from a materialized baseline.", code = "materialized_requirement_required" });
            var validRequirementIds = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x=>requirementIds.Contains(x.Id))
                                             join artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId) on revision.ArtifactId equals artifact.Id
                                             join member in db.BaselineRequirements.AsNoTracking() on revision.Id equals member.RevisionId
                                             join baseline in db.CandidateBaselines.AsNoTracking().Where(x=>x.RequirementsMaterializedAt!=null) on member.BaselineId equals baseline.Id
                                             select revision.Id).Distinct().CountAsync(ct);
            if (validRequirementIds != requirementIds.Count) return Results.BadRequest(new { error = "Every coverage link must reference an authoritative requirement revision in this project." });
            if (requirementIds.Count > 0 && await (from member in db.BaselineRequirements.AsNoTracking().Where(x => requirementIds.Contains(x.RevisionId))
                                                   join campaign in db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == request.ProjectId && x.State == ReleaseCampaignState.InReview) on member.BaselineId equals campaign.BaselineId
                                                   select member.Id).AnyAsync(ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            try { var actor = http.UserAccount().UserName; var baseNumber=await IdentifierAllocator.NextTestProcedureAsync(db,request.Level,ct);var procedure = new TestProcedure(request.ProjectId, baseNumber, request.Title, actor, DateTimeOffset.UtcNow, request.Level);
                var revision = new TestProcedureRevision(procedure.Id, 0, request.Objective, request.Preconditions, request.Steps, request.ExpectedResult, TestProcedureState.Draft, actor, DateTimeOffset.UtcNow, selectedApprover.UserName);
                db.AddRange(procedure, revision); db.TestCoverage.AddRange(requirementIds.Select(id => new TestRequirementCoverage(revision.Id, id)));
                db.UserNotifications.Add(new(request.ProjectId, selectedApprover.UserName, "ProcedureApprovalRequested",
                    $"Review procedure {procedure.BaseNumber}.00", $"{http.UserAccount().DisplayName} selected you to approve this controlled test procedure revision.",
                    "verification", procedure.Id, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
                return Results.Created($"/api/test-procedures/{procedure.Id}", new { procedure.Id, revisionId = revision.Id, displayNumber = procedure.BaseNumber + ".00", state = revision.State.ToString(), revision.SelectedApproverId }); }
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
            if (request.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == request.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (request.RetestOfExecutionId is not null && !await db.TestExecutions.AnyAsync(x => x.Id == request.RetestOfExecutionId && x.ProcedureRevisionId == request.ProcedureRevisionId, ct)) return Results.BadRequest(new { error = "A retest must reference an earlier execution of the same procedure revision." });
            try { var execution = new TestExecution(request.ProjectId, request.ProcedureRevisionId, request.SoftwareBuildId, request.RetestOfExecutionId,
                request.Outcome, http.UserAccount().UserName, request.Configuration, request.Determination, request.EvidenceReference, request.ExecutedAt, DateTimeOffset.UtcNow, executionReleaseId);
                db.TestExecutions.Add(execution); await db.SaveChangesAsync(ct); return Results.Created($"/api/test-executions/{execution.Id}", new { execution.Id, outcome = execution.Outcome.ToString() }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/test-executions", async (Guid projectId, Guid? releaseId, Guid? buildId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var source = db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId && (buildId == null || x.SoftwareBuildId == buildId)
                && (releaseId == null || x.ReleaseId == releaseId
                    || x.ReleaseId == null && x.SoftwareBuildId != null && db.SoftwareBuilds.Any(b => b.Id == x.SoftwareBuildId && b.ReleaseId == releaseId)));
            var rowsQuery = from execution in source join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                              join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                              select new { execution.Id, procedureRevisionId = revision.Id, displayNumber = procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                  procedure.Title, outcome = execution.Outcome.ToString(), execution.ExecutedBy, execution.Configuration, execution.Determination,
                                  execution.EvidenceReference, execution.ExecutedAt, execution.RecordedAt, execution.ReleaseId, execution.SoftwareBuildId, execution.RetestOfExecutionId };
            var rows = await (db.Database.IsSqlite() ? rowsQuery.OrderByDescending(x => x.Id) : rowsQuery.OrderByDescending(x => x.ExecutedAt)).ToListAsync(ct); var rowIds = rows.Select(x => x.Id).ToList();
            var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => rowIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.Size, item.Sha256, item.UploadedAt }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, x.procedureRevisionId, x.displayNumber, x.Title, x.outcome, x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt, x.ReleaseId, x.SoftwareBuildId, x.RetestOfExecutionId, evidence = evidence.Where(e => e.TestExecutionId == x.Id).Select(e => new { e.Id, e.OriginalFileName, e.Size, e.Sha256, e.UploadedAt }) }));
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
            var requirementIds = requirements.Select(x => x.revisionId).ToList();
            var coverageLinks = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, requirementIds, ct, buildScoped: true);
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
                        procedureId = link.ProcedureId,
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
}
