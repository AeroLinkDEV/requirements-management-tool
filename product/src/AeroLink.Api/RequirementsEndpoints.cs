using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// The requirements workspace: schemas, specifications, saved views, comments, imports, and the
/// bulk operations a team of engineers spends its day in.
///
/// Requirements are read-only here by design. Every change to one arrives through a controlled change
/// request, so these endpoints author the structure and the discussion around requirements rather than the
/// requirements themselves.
/// </summary>
public static class RequirementsEndpoints
{
    public static void MapRequirementsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId,
            string? scope, bool? includeRetired, int? page, int? pageSize, string? ids,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var resolvedPage=Math.Max(1,page??1);var resolvedPageSize=Math.Clamp(pageSize??50,1,200);
            var source = from artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                         join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                         join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id into sourceRequests
                         from scr in sourceRequests.DefaultIfEmpty()
                         select new { artifact, revision, scr };
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.HighLevel||x.artifact.Level==RequirementLevel.LowLevel);
            else if(string.Equals(scope,"HighLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.HighLevel);
            else if(string.Equals(scope,"LowLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.LowLevel);
            if (baselineId is not null) source = source.Where(x => db.BaselineRequirements.Any(m => m.BaselineId == baselineId && m.RevisionId == x.revision.Id));
            else if (includeRetired != true) source = source.Where(x => x.revision.State == AeroLink.Domain.Requirements.RequirementRevisionState.Active);
            if (releaseId is not null) source = source.Where(x => db.CandidateBaselines.Any(b => b.Id == x.revision.EffectiveBaselineId && b.ReleaseId == releaseId));
            // Eligibility is the scoped source; search narrows the page but must never hide an already
            // selected exact revision. Hydration therefore runs against the scoped source without the
            // search predicate, so a selected item outside the current result page stays reachable.
            var scoped = source;
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q) || x.revision.Rationale.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct);
            var requestedIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToList();
            var paged = await source.OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision)
                .Skip((resolvedPage - 1) * resolvedPageSize).Take(resolvedPageSize).ToListAsync(ct);
            var hydrated = requestedIds.Count == 0
                ? []
                : await scoped.Where(x => requestedIds.Contains(x.revision.Id)).ToListAsync(ct);
            var rows = paged.Concat(hydrated).DistinctBy(x => x.revision.Id)
                .OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision).ToList();
            var items = rows.Select(x => new { x.artifact.Id, x.artifact.BaseNumber, level = x.artifact.Level.ToString(), revisionId = x.revision.Id, x.revision.Revision,
                    displayNumber = x.artifact.BaseNumber + "." + (x.revision.Revision < 10 ? "0" : "") + x.revision.Revision, x.revision.Statement, x.revision.Rationale,
                    x.revision.VerificationMethod, state = x.revision.State.ToString(), x.revision.EffectiveBaselineId,
                    originKind = x.revision.OriginKind.ToString(), x.revision.SourceBaselineImportId,
                    sourceChangeRequestId = x.scr == null ? (Guid?)null : x.scr.Id,
                    sourceScr = x.scr == null ? null : x.scr.BaseNumber + "." + (x.scr.Revision < 10 ? "0" : "") + x.scr.Revision,
                    x.revision.CreatedAt }).ToList();
            return Results.Ok(new { page=resolvedPage, pageSize=resolvedPageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)resolvedPageSize), items });
        });

        app.MapGet("/api/requirements/{id:guid}/history", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (artifact is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == id)
                                   join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id into sourceRequests
                                   from scr in sourceRequests.DefaultIfEmpty()
                                   join baseline in db.CandidateBaselines.AsNoTracking() on revision.EffectiveBaselineId equals baseline.Id
                                   orderby revision.Revision descending select new { revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                       revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString(), revision.CreatedAt,
                                       originKind = revision.OriginKind.ToString(), revision.SourceBaselineImportId,
                                       sourceChangeRequestId = scr == null ? (Guid?)null : scr.Id,
                                       sourceScr = scr == null ? null : scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision,
                                       baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
            return Results.Ok(new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revisions });
        });

        // Enterprise Requirements Workspace: configurable schemas, structured specifications,
        // collaboration, saved views, governed bulk operations, redlines, and onboarding.
        app.MapGet("/api/enterprise-requirements/workspace", async (Guid projectId, Guid? releaseId, Guid? specificationId, Guid? sectionId, string? search, string? level, string? verification, string? tag,string? state,string? owner,string? sourceScr,Guid? baselineId,bool? openComments,string? coverageState,string? sort,int page, int pageSize,
            HttpContext http, AeroLinkDbContext db, EnterpriseRequirementsService enterprise,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            var allowedLevels = ladderPolicy.OrderedLevels.ToArray();
            // Direct filters are validated against the same contract a saved view is stored under, so a
            // worklist means the same thing whether it arrives as a query string or as a saved record — and
            // a sort or coverage state this workspace cannot apply is refused rather than silently ignored.
            var submitted=new JsonObject();
            void Submit(string key,string? value){if(RequirementFilterValue.HasValue(value))submitted[key]=value;}
            Submit("search",search);Submit("level",level);Submit("verification",verification);Submit("tag",tag);
            Submit("state",state);Submit("owner",owner);Submit("sourceScr",sourceScr);Submit("coverageState",coverageState);Submit("sort",sort);
            var contract=SavedViewContract.Normalize(submitted.ToJsonString(),"[]");
            if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="requirement_filter_invalid"});

            var timer=Stopwatch.StartNew();
            await enterprise.SynchronizeProjectAsync(projectId,http.UserAccount().UserName,ct);page=Math.Max(1,page==0?1:page);pageSize=Math.Clamp(pageSize==0?100:pageSize,1,250);
            var artifacts=db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&allowedLevels.Contains(x.Level));
            if(string.Equals(level,"Software",StringComparison.OrdinalIgnoreCase))artifacts=artifacts.Where(x=>x.Level==RequirementLevel.HighLevel||x.Level==RequirementLevel.LowLevel);
            else if(!string.IsNullOrWhiteSpace(level)&&Enum.TryParse<RequirementLevel>(level,true,out var parsedLevel))artifacts=artifacts.Where(x=>x.Level==parsedLevel);
            if(specificationId is not null)artifacts=artifacts.Where(x=>db.SpecificationNodes.Any(n=>n.SpecificationId==specificationId&&n.RequirementArtifactId==x.Id));
            // A section is a node inside a specification, and a requirement sits under it as a child node. The
            // headings were rendered as labels with counts beside them and could not be acted on, so a reader
            // could see that a section held forty requirements and had no way to see which forty.
            if(sectionId is not null)artifacts=artifacts.Where(x=>db.SpecificationNodes.Any(n=>n.ParentId==sectionId&&n.RequirementArtifactId==x.Id));
            var effectiveBaselineId=baselineId??(releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,projectId,releaseId.Value,ct));
            var procedureEffectivity = releaseId is not null
                ? await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct)
                : effectiveBaselineId is not null
                    ? await TestProcedureEffectivity.ForBaselineAsync(db, effectiveBaselineId.Value, ct)
                    : null;
            var effectiveProcedureRevisionIds = await EffectiveCoverageRevisionIdsAsync(
                db, effectiveBaselineId, ladderPolicy, procedureEffectivity, ct);
            var isExactProcedureSnapshot = procedureEffectivity is not null && (releaseId is null ||
                await db.CandidateBaselines.AsNoTracking().AnyAsync(x =>
                    x.Id == procedureEffectivity.BaselineId && x.ReleaseId == releaseId.Value, ct));
            var current=effectiveBaselineId is not null
                ? from artifact in artifacts
                  join member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId) on artifact.Id equals member.ArtifactId
                  join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                  select new{artifact,revision}
                : from artifact in artifacts
                  join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                  where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)
                  select new{artifact,revision};
            if(!string.IsNullOrWhiteSpace(search)){var q=search.Trim().ToLower();current=current.Where(x=>x.artifact.BaseNumber.ToLower().Contains(q)||x.revision.Statement.ToLower().Contains(q)||x.revision.Rationale.ToLower().Contains(q));}
            if(!string.IsNullOrWhiteSpace(verification)){var v=verification.Trim().ToLower();current=current.Where(x=>x.revision.VerificationMethod.ToLower()==v);}
            // Exact tag membership against the normalized index, not a substring of the serialized array —
            // the tag "safe" matched every requirement tagged "failsafe", and a leading-wildcard scan over
            // raw JSON can use no index at all.
            if(RequirementFilterValue.HasValue(tag)){var t=RequirementFilterValue.Normalize(tag);current=current.Where(x=>db.RequirementRevisionTags.Any(p=>p.RevisionId==x.revision.Id&&p.Tag==t));}
            if(!string.IsNullOrWhiteSpace(state)&&Enum.TryParse<RequirementRevisionState>(state,true,out var parsedState))current=current.Where(x=>x.revision.State==parsedState);
            // The declared owner field, exactly, rather than any attribute value that happens to contain it.
            if(RequirementFilterValue.HasValue(owner)){var o=RequirementFilterValue.Normalize(owner);current=current.Where(x=>db.RequirementRevisionProfiles.Any(p=>p.RevisionId==x.revision.Id&&p.Owner==o));}
            if(!string.IsNullOrWhiteSpace(sourceScr)){var s=sourceScr.Trim().ToLower();current=current.Where(x=>db.SystemChangeRequests.Any(scr=>scr.Id==x.revision.SourceChangeRequestId&&(scr.BaseNumber.ToLower().Contains(s)||scr.Title.ToLower().Contains(s))));}
            if(baselineId is not null)current=current.Where(x=>db.BaselineRequirements.Any(b=>b.BaselineId==baselineId&&b.RevisionId==x.revision.Id));
            if(openComments==true)current=current.Where(x=>db.ArtifactComments.Any(c=>c.ArtifactId==x.artifact.Id&&c.ArtifactType=="Requirement"&&c.State==CollaborationState.Open));
            // Which requirements are uncovered, or covered only by something that no longer counts, was a
            // question the workspace could not answer at all — it filtered on the verification *method* an
            // author declared, which says what kind of evidence is intended and nothing about whether any
            // exists. Both subqueries stay composable so this filters in the database alongside every other
            // predicate, before the count and the page.
            if(!string.IsNullOrWhiteSpace(coverageState)&&RequirementCoverageState.TryParse(coverageState,out var parsedCoverage))
            {
                var settled=VerificationCoverageProjection.SettledCoveredRequirementRevisionIds(db,effectiveProcedureRevisionIds,isExactProcedureSnapshot);var linked=VerificationCoverageProjection.LinkedRequirementRevisionIds(db,effectiveProcedureRevisionIds);
                current=parsedCoverage switch{
                    RequirementCoverageState.Covered=>current.Where(x=>settled.Contains(x.revision.Id)),
                    RequirementCoverageState.Suspect=>current.Where(x=>!settled.Contains(x.revision.Id)&&linked.Contains(x.revision.Id)),
                    _=>current.Where(x=>!linked.Contains(x.revision.Id))};
            }
            var ordered=sort?.ToLowerInvariant() switch{"updated" when !db.Database.IsSqlite()=>current.OrderByDescending(x=>x.revision.CreatedAt).ThenBy(x=>x.artifact.BaseNumber),"verification"=>current.OrderBy(x=>x.revision.VerificationMethod).ThenBy(x=>x.artifact.BaseNumber),"state"=>current.OrderBy(x=>x.revision.State).ThenBy(x=>x.artifact.BaseNumber),_=>current.OrderBy(x=>x.artifact.BaseNumber)};
            var total=await current.CountAsync(ct);var rows=await ordered.Skip((page-1)*pageSize).Take(pageSize)
                .Select(x=>new{x.artifact.Id,x.artifact.BaseNumber,level=x.artifact.Level.ToString(),revisionId=x.revision.Id,x.revision.Revision,x.revision.Statement,x.revision.Rationale,x.revision.VerificationMethod,state=x.revision.State.ToString(),originKind=x.revision.OriginKind.ToString(),x.revision.SourceChangeRequestId,x.revision.SourceBaselineImportId,x.revision.CreatedAt}).ToListAsync(ct);
            var revisionIds=rows.Select(x=>x.revisionId).ToList();
            // The controlled number of the change request that authorized each revision. The inspector names
            // its source authority after this rather than after the workspace it is being read in — a fixed
            // A fixed "Open SCR" was wrong every time it appeared on an HLR or LLR, whose authority is an HLRCR or LLRCR.
            var sourceScrIds=rows.Select(x=>x.SourceChangeRequestId).Distinct().ToList();
            // Number and owning release together. The link needs both: the number to read, and the release the
            // change request belongs to so it opens in its own build rather than whichever one is selected.
            var sourceRequests=await db.SystemChangeRequests.AsNoTracking().Where(x=>sourceScrIds.Contains(x.Id))
                .Select(x=>new{x.Id,x.BaseNumber,x.Revision,x.TargetReleaseId}).ToDictionaryAsync(x=>x.Id,ct);
            var sourceNumbers=sourceRequests.ToDictionary(x=>x.Key,x=>x.Value.BaseNumber+"."+(x.Value.Revision<10?"0":"")+x.Value.Revision);
            var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToDictionaryAsync(x=>x.RevisionId,ct);
            var coverageStates=await VerificationCoverageProjection.StatesAsync(db,revisionIds,ct,effectiveProcedureRevisionIds,isExactProcedureSnapshot);
            var commentCounts=await db.ArtifactComments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ArtifactType=="Requirement"&&rows.Select(r=>r.Id).Contains(x.ArtifactId)).GroupBy(x=>x.ArtifactId).Select(x=>new{x.Key,Count=x.Count(),Open=x.Count(c=>c.State==CollaborationState.Open)}).ToDictionaryAsync(x=>x.Key,ct);
            var allowedLevelNames = allowedLevels.Select(x=>x.ToString()).ToArray();
            var schemas=await db.ArtifactSchemas.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.IsActive&&allowedLevelNames.Contains(x.AppliesTo)).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Key,x.Name,x.AppliesTo,x.Description,x.Version,fields=x.Fields.OrderBy(f=>f.SortOrder).Select(f=>new{f.Id,f.Key,f.Label,type=f.Type.ToString(),f.IsRequired,f.SortOrder,f.OptionsJson})}).ToListAsync(ct);
            var specificationRows=await db.RequirementSpecifications.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.IsActive&&allowedLevelNames.Contains(x.Level)).OrderBy(x=>x.Level).Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,nodeCount=db.SpecificationNodes.Count(n=>n.SpecificationId==x.Id&&n.Type==SpecificationNodeType.Requirement)}).ToListAsync(ct);
            var specificationIds=specificationRows.Select(x=>x.Id).ToList();var sectionRows=await db.SpecificationNodes.AsNoTracking().Where(n=>specificationIds.Contains(n.SpecificationId)&&n.Type==SpecificationNodeType.Section).OrderBy(n=>n.Position).Select(n=>new{n.Id,n.SpecificationId,n.Heading,n.Position,count=db.SpecificationNodes.Count(c=>c.ParentId==n.Id)}).ToListAsync(ct);
            var specifications=specificationRows.Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,x.nodeCount,sections=sectionRows.Where(s=>s.SpecificationId==x.Id).Select(s=>new{s.Id,s.Heading,s.Position,s.count})}).ToList();
            var views=await db.SavedRequirementViews.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OwnerId==http.UserAccount().Id||x.IsShared)).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Name,x.QueryJson,x.ColumnsJson,x.IsShared,owned=x.OwnerId==http.UserAccount().Id}).ToListAsync(ct);
            var build=releaseId is null?null:await db.Releases.AsNoTracking().Where(x=>x.Id==releaseId&&x.ProjectId==projectId).Select(x=>new{x.Id,x.Version,x.IsReleased}).SingleOrDefaultAsync(ct);
            timer.Stop();return Results.Ok(new{page,pageSize,totalCount=total,totalPages=(int)Math.Ceiling(total/(double)pageSize),queryElapsedMs=timer.ElapsedMilliseconds,effectiveBaselineId,build,schemas,specifications,views,items=rows.Select(x=>{profiles.TryGetValue(x.revisionId,out var profile);commentCounts.TryGetValue(x.Id,out var comments);Guid? sourceId=x.SourceChangeRequestId;var hasSource=sourceId is Guid;return new{x.Id,x.BaseNumber,displayNumber=$"{x.BaseNumber}.{x.Revision:D2}",x.level,x.revisionId,x.Revision,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.originKind,x.SourceChangeRequestId,x.SourceBaselineImportId,sourceChangeRequestReleaseId=hasSource&&sourceRequests.TryGetValue(sourceId!.Value,out var sourceRequest)?sourceRequest.TargetReleaseId:(Guid?)null,sourceScr=hasSource&&sourceNumbers.TryGetValue(sourceId!.Value,out var sourceNumber)?sourceNumber:"",x.CreatedAt,richText=profile?.RichText??x.Statement,attributesJson=profile?.AttributesJson??"{}",tagsJson=profile?.TagsJson??"[]",commentCount=comments?.Count??0,openCommentCount=comments?.Open??0,coverageState=coverageStates.TryGetValue(x.revisionId,out var rowCoverage)?rowCoverage:RequirementCoverageState.Uncovered};})});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}", async (Guid artifactId,Guid? releaseId,HttpContext http,
            AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();
            if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(artifact.ProjectId, ct);
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,artifact.ProjectId,releaseId.Value,ct);
            if(releaseId is not null&&(effectiveBaselineId is null||!await db.BaselineRequirements.AnyAsync(x=>x.BaselineId==effectiveBaselineId&&x.ArtifactId==artifactId,ct)))return Results.NotFound(new{error="This requirement is not primary content in the active build.",code="cross_build_requirement"});
            var history=await (from r in db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId)
                               join s in db.SystemChangeRequests.AsNoTracking() on r.SourceChangeRequestId equals s.Id into sourceRequests
                               from s in sourceRequests.DefaultIfEmpty()
                               join b in db.CandidateBaselines.AsNoTracking() on r.EffectiveBaselineId equals b.Id
                               join release in db.Releases.AsNoTracking() on b.ReleaseId equals release.Id
                               // The source change request's own release travels with it. A historical revision is by
                               // definition sourced from an earlier build, so opening that change request in the build
                               // currently selected would present a released, frozen record inside an in-work context.
                               orderby r.Revision descending select new{r.Id,r.Revision,displayNumber=artifact.BaseNumber+"."+(r.Revision<10?"0":"")+r.Revision,r.Statement,r.Rationale,r.VerificationMethod,state=r.State.ToString(),originKind=r.OriginKind.ToString(),r.SourceBaselineImportId,sourceChangeRequestId=s==null?(Guid?)null:s.Id,sourceChangeRequestReleaseId=s==null?(Guid?)null:s.TargetReleaseId,sourceScr=s==null?null:s.BaseNumber+"."+(s.Revision<10?"0":"")+s.Revision,r.CreatedAt,originBuild=release.Version,isHistorical=releaseId!=null&&release.Id!=releaseId}).ToListAsync(ct);
            var revisionIds=history.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
            var traceScopeIds=effectiveBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId).Select(x=>x.RevisionId).ToListAsync(ct);
            var placements=await (from n in db.SpecificationNodes.AsNoTracking().Where(x=>x.RequirementArtifactId==artifactId) join spec in db.RequirementSpecifications.AsNoTracking() on n.SpecificationId equals spec.Id join parent in db.SpecificationNodes.AsNoTracking() on n.ParentId equals parent.Id select new{spec.Id,spec.DocumentNumber,spec.Title,section=parent.Heading,n.Position}).ToListAsync(ct);
            var procedureEffectivity=releaseId is null?null:await TestProcedureEffectivity.ForReleaseAsync(db,artifact.ProjectId,releaseId.Value,ct);
            var effectiveProcedureRevisionIds = await EffectiveCoverageRevisionIdsAsync(
                db, effectiveBaselineId, ladderPolicy, procedureEffectivity, ct);
            var traceQuery=db.RequirementTraces.AsNoTracking();
            if(effectiveBaselineId is not null)
                traceQuery=traceQuery.Where(x=>traceScopeIds.Contains(x.SourceRevisionId)&&traceScopeIds.Contains(x.TargetRevisionId)&&(x.ExactLinkSuspectLifecycleId==null||db.ExactLinkSuspectLifecycles.Any(lifecycle=>lifecycle.Id==x.ExactLinkSuspectLifecycleId&&lifecycle.LinkKind==ExactLinkKind.RequirementTrace&&lifecycle.State==ExactLinkLifecycleState.Closed)));
            else
                traceQuery=traceQuery.Where(x=>(revisionIds.Contains(x.SourceRevisionId)||revisionIds.Contains(x.TargetRevisionId))&&x.ExactLinkSuspectLifecycleId==null);
            var traces=await traceQuery.CountAsync(ct);var testSource=db.TestCoverage.AsNoTracking().Where(x=>revisionIds.Contains(x.RequirementRevisionId)&&!x.IsSuspect);if(effectiveProcedureRevisionIds is not null)testSource=testSource.Where(x=>effectiveProcedureRevisionIds.Contains(x.ProcedureRevisionId));var tests=await testSource.CountAsync(ct);
            return Results.Ok(new{artifact.Id,artifact.BaseNumber,level=artifact.Level.ToString(),activeBuildId=releaseId,effectiveBaselineId,history=history.Select(x=>new{x.Id,x.Revision,x.displayNumber,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.originKind,x.SourceBaselineImportId,x.sourceChangeRequestId,x.sourceChangeRequestReleaseId,x.sourceScr,x.CreatedAt,x.originBuild,x.isHistorical,richText=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.RichText,attributesJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.AttributesJson??"{}",tagsJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.TagsJson??"[]"}),placements,traceCount=traces,testCoverageCount=tests});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/redline",async(Guid artifactId,Guid fromRevisionId,Guid toRevisionId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var projectId=await db.Requirements.Where(x=>x.Id==artifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.Id==fromRevisionId||x.Id==toRevisionId)).ToListAsync(ct);if(revisions.Count!=2)return Results.BadRequest(new{error="Select two revisions of the same requirement."});var from=revisions.Single(x=>x.Id==fromRevisionId);var to=revisions.Single(x=>x.Id==toRevisionId);
            var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId).ToListAsync(ct);var fromProfile=profiles.SingleOrDefault(x=>x.RevisionId==fromRevisionId);var toProfile=profiles.SingleOrDefault(x=>x.RevisionId==toRevisionId);
            var files=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId)).ToListAsync(ct);var attachmentChanges=files.Select(x=>new{x.Id,x.LogicalId,x.Version,x.Label,x.OriginalFileName,x.Sha256,kind=x.RevisionId==toRevisionId?"added":"removed"}).ToList();
            return Results.Ok(new{from=from.Revision,to=to.Revision,statement=EnterpriseRequirementsService.Diff(from.Statement,to.Statement),rationale=EnterpriseRequirementsService.Diff(from.Rationale,to.Rationale),richText=EnterpriseRequirementsService.Diff(fromProfile?.RichText??from.Statement,toProfile?.RichText??to.Statement),attributesChanged=(fromProfile?.AttributesJson??"{}")!=(toProfile?.AttributesJson??"{}"),fromAttributes=fromProfile?.AttributesJson??"{}",toAttributes=toProfile?.AttributesJson??"{}",verificationChanged=from.VerificationMethod!=to.VerificationMethod,fromVerification=from.VerificationMethod,toVerification=to.VerificationMethod,attachmentChanges});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/impact",async(Guid artifactId,Guid? releaseId,HttpContext http,
            AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(artifact.ProjectId, ct);
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,artifact.ProjectId,releaseId.Value,ct);
            var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId).ToListAsync(ct);
            var effectiveRevisionId=effectiveBaselineId is null?null:await db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId&&x.ArtifactId==artifactId).Select(x=>(Guid?)x.RevisionId).SingleOrDefaultAsync(ct);
            var current=effectiveBaselineId is null
                ? revisions.OrderByDescending(x=>x.Revision).First()
                : revisions.SingleOrDefault(x=>x.Id==effectiveRevisionId);
            if(current is null)return Results.NotFound(new{error="This requirement is not primary content in the active build.",code="cross_build_requirement"});
            Guid? impactBaselineId=effectiveBaselineId??(Guid?)current.EffectiveBaselineId;
            var impactScopeIds=impactBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==impactBaselineId).Select(x=>x.RevisionId).ToListAsync(ct);
            var parentLinks=db.RequirementTraces.AsNoTracking().Where(x=>x.SourceRevisionId==current.Id);
            var childLinks=db.RequirementTraces.AsNoTracking().Where(x=>x.TargetRevisionId==current.Id);
            if(impactBaselineId is not null)
            {
                parentLinks=parentLinks.Where(x=>impactScopeIds.Contains(x.TargetRevisionId));
                childLinks=childLinks.Where(x=>impactScopeIds.Contains(x.SourceRevisionId));
            }
            if(effectiveBaselineId is null)
            {
                parentLinks=parentLinks.Where(x=>x.ExactLinkSuspectLifecycleId==null);
                childLinks=childLinks.Where(x=>x.ExactLinkSuspectLifecycleId==null);
            }
            else
            {
                parentLinks=parentLinks.Where(x=>x.ExactLinkSuspectLifecycleId==null||db.ExactLinkSuspectLifecycles.Any(lifecycle=>lifecycle.Id==x.ExactLinkSuspectLifecycleId&&lifecycle.LinkKind==ExactLinkKind.RequirementTrace&&lifecycle.State==ExactLinkLifecycleState.Closed));
                childLinks=childLinks.Where(x=>x.ExactLinkSuspectLifecycleId==null||db.ExactLinkSuspectLifecycles.Any(lifecycle=>lifecycle.Id==x.ExactLinkSuspectLifecycleId&&lifecycle.LinkKind==ExactLinkKind.RequirementTrace&&lifecycle.State==ExactLinkLifecycleState.Closed));
            }
            var parents=await (from link in parentLinks join revision in db.RequirementRevisions.AsNoTracking() on link.TargetRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
            var children=await (from link in childLinks join revision in db.RequirementRevisions.AsNoTracking() on link.SourceRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
            var procedureEffectivity=releaseId is null?null:await TestProcedureEffectivity.ForReleaseAsync(db,artifact.ProjectId,releaseId.Value,ct);
            var isExactProcedureSnapshot=procedureEffectivity is not null&&await db.CandidateBaselines.AsNoTracking().AnyAsync(x=>x.Id==procedureEffectivity.BaselineId&&x.ReleaseId==releaseId,ct);
            var effectiveCoverageRevisionIds = await EffectiveCoverageRevisionIdsAsync(
                db, impactBaselineId, ladderPolicy, procedureEffectivity, ct);
            var coverageLinks=await VerificationCoverageProjection.ForRequirementRevisionsAsync(
                db, [current.Id], ct, isExactProcedureSnapshot, effectiveCoverageRevisionIds);
            var tests=coverageLinks.Select(x=>new
            {
                id=x.ArtifactId,
                artifactId=x.ArtifactId,
                artifactRevisionId=x.ArtifactRevisionId,
                artifactKind=x.Level=="System"?"Procedure":"Case",
                artifactState=x.ArtifactState,
                procedureId=x.ProcedureId, // compatibility alias
                procedureRevisionId=x.ProcedureRevisionId, // compatibility alias
                x.DisplayNumber,x.Title,x.Level,
                state=x.ArtifactState,
                x.IsSuspect,x.CoverageState
            }).ToList();
            var baselines=await (from selection in db.BaselineRequirements.AsNoTracking().Where(x=>x.ArtifactId==artifactId) join baseline in db.CandidateBaselines.AsNoTracking() on selection.BaselineId equals baseline.Id join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id select new{baseline.Id,baseline=baseline.BaseNumber+"."+(baseline.Revision<10?"0":"")+baseline.Revision,baseline.Name,state=baseline.State.ToString(),release=release.Version,selection.RevisionId}).ToListAsync(ct);
            var baselineIds=baselines.Select(x=>x.Id).ToList();var builds=await db.SoftwareBuilds.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.BuildNumber,x.Description,state=x.State.ToString()}).ToListAsync(ct);var documents=await db.ControlledDocuments.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.DocumentNumber,x.Revision,x.Title,type=x.Type.ToString(),x.ContentHash}).ToListAsync(ct);
            var activeChanges=await (from change in db.RequirementChanges.AsNoTracking().Where(x=>x.BaseNumber==artifact.BaseNumber) join scr in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals scr.Id where scr.State==ChangeRequestState.Draft||scr.State==ChangeRequestState.InReview||scr.State==ChangeRequestState.Approved select new{scr.Id,displayNumber=scr.BaseNumber+"."+(scr.Revision<10?"0":"")+scr.Revision,scr.Title,state=scr.State.ToString(),kind=change.Kind.ToString(),proposedRevision=change.Revision}).ToListAsync(ct);
            var openComments=await db.ArtifactComments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==CollaborationState.Open,ct);var openAssignments=await db.ArtifactAssignments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==AssignmentState.Open,ct);
            var confirmedCoverage=coverageLinks.Count(x=>x.CoverageState=="Confirmed");
            var categories=new[]{new{key="trace",label="Trace relationships",count=parents.Count+children.Count,needsAction=parents.Count+children.Count==0},new{key="verification",label="Verification coverage",count=confirmedCoverage,needsAction=confirmedCoverage==0},new{key="baseline",label="Baselines and builds",count=baselines.Count+builds.Count,needsAction=false},new{key="document",label="Controlled documents",count=documents.Count,needsAction=false},new{key="collaboration",label="Open collaboration",count=openComments+openAssignments,needsAction=openComments+openAssignments>0}};
            return Results.Ok(new{artifact.Id,artifact.BaseNumber,currentRevision=current.Revision,requirementRevisionId=current.Id,displayNumber=artifact.BaseNumber+"."+(current.Revision<10?"0":"")+current.Revision,parents,children,tests,baselines,builds,documents,activeChanges,openComments,openAssignments,categories});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/propose-options", async (Guid artifactId,
            Guid targetReleaseId, string? search, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
            if (artifact is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, artifact.ProjectId, ct, ProgramRole.Engineer))
                return Results.Forbid();

            var ladderPolicy = await policyResolver.ResolveAsync(artifact.ProjectId, ct);
            var targetRelease = await db.Releases.AsNoTracking()
                .Where(x => x.Id == targetReleaseId && x.ProjectId == artifact.ProjectId)
                .Select(x => new { x.Id, x.Version, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (targetRelease is null)
                return Results.BadRequest(new { error = "Select a build from this Project." });

            var current = await CurrentRequirementRevisionAsync(db, artifact, targetReleaseId, ct);
            if (current is null)
                return Results.BadRequest(new { error = "This requirement is not active in the selected build.", code = "requirement_not_carried_by_build" });
            var binding = ladderPolicy.Definition(artifact.Level).ChangeRequest;
            var bindingValid = binding is not null
                && ladderPolicy.AcceptsChangeRequest(binding.Type, binding.SoftwareLevel, artifact.Level);
            var actor = http.UserAccount();
            var now = DateTimeOffset.UtcNow;
            var sessions = await db.ArtifactEditSessions.AsNoTracking()
                .Where(x => x.ProjectId == artifact.ProjectId && (x.ArtifactType == "ChangeRequest" || x.ArtifactType == "SCR")
                    && x.IsExclusive && x.State == EditSessionState.Active)
                .ToListAsync(ct);
            var query = db.SystemChangeRequests.AsNoTracking()
                .Include(x => x.RequirementChanges)
                .Where(x => x.ProjectId == artifact.ProjectId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                query = query.Where(x => x.BaseNumber.ToLower().Contains(term)
                    || x.Title.ToLower().Contains(term));
            }
            var drafts = await query.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
                .Take(100).ToListAsync(ct);
            var rows = drafts.Select(scr =>
            {
                var session = sessions.FirstOrDefault(x => x.ArtifactId == scr.Id && x.ExpiresAt > now);
                var sameBinding = bindingValid && ladderPolicy.AcceptsChangeRequest(scr.Type, scr.SoftwareLevel, artifact.Level);
                var duplicate = scr.RequirementChanges.Any(x => string.Equals(x.BaseNumber, artifact.BaseNumber, StringComparison.OrdinalIgnoreCase));
                var existingProposalId = scr.RequirementChanges
                    .Where(x => string.Equals(x.BaseNumber, artifact.BaseNumber, StringComparison.OrdinalIgnoreCase))
                    .Select(x => (Guid?)x.Id).FirstOrDefault();
                var eligible = !targetRelease.IsReleased && sameBinding && scr.TargetReleaseId == targetReleaseId
                    && scr.State == ChangeRequestState.Draft
                    && (scr.AuthorId.Equals(actor.UserName, StringComparison.OrdinalIgnoreCase) || actor.IsAdministrator)
                    && !duplicate && session is null;
                string? reason = eligible ? null
                    : targetRelease.IsReleased ? $"Build {targetRelease.Version} is released and read-only."
                    : scr.TargetReleaseId != targetReleaseId ? "This Draft belongs to a different build."
                    : !sameBinding ? $"This Draft is not bound to the {artifact.Level} requirement level."
                    : scr.State != ChangeRequestState.Draft ? $"This change request is {scr.State}, not Draft."
                    : !(scr.AuthorId.Equals(actor.UserName, StringComparison.OrdinalIgnoreCase) || actor.IsAdministrator) ? "Only the Draft author or an administrator can add a proposal."
                    : duplicate ? "This Draft already contains the selected requirement."
                    : session is not null ? $"This Draft is checked out by {session.UserName}; finish or discard that edit before adding a proposal."
                    : "This Draft is not eligible for a requirement proposal.";
                return new
                {
                    id = scr.Id, scr.BaseNumber, scr.Revision, displayNumber = scr.DisplayNumber, scr.Title,
                    state = scr.State.ToString(), scr.TargetReleaseId, scr.Type,
                    softwareLevel = scr.SoftwareLevel?.ToString(), scr.Version,
                    requirementCount = scr.RequirementChanges.Count, eligible, reason, existingProposalId,
                    heldBy = session?.UserName, heldByCurrentUser = session?.UserName.Equals(actor.UserName, StringComparison.OrdinalIgnoreCase) == true
                };
            }).ToList();
            return Results.Ok(new
            {
                requirement = new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(),
                    revisionId = current.Id, revision = current.Revision, displayNumber = $"{artifact.BaseNumber}.{current.Revision:D2}" },
                targetRelease, drafts = rows
            });
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/propose", async (Guid artifactId,
            ProposeRequirementChangeRequest request, HttpContext http, AeroLinkDbContext db,
            IChangeRequestRepository repository, IdentityService identity,
            IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
            if (artifact is null) return Results.NotFound();
            if (request.Kind is not (RequirementChangeKind.Modify or RequirementChangeKind.Retire))
                return Results.BadRequest(new { error = "An existing requirement can only be modified or retired." });
            var principal = http.UserAccount();
            if (!await http.HasProjectRoleAsync(db, identity, artifact.ProjectId, ct, ProgramRole.Engineer))
                return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(artifact.ProjectId, ct);
            var targetRelease = await db.Releases.AsNoTracking()
                .Where(x => x.Id == request.TargetReleaseId && x.ProjectId == artifact.ProjectId)
                .Select(x => new { x.Id, x.Version, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (targetRelease is null)
                return Results.BadRequest(new { error = "Select a build from this Project." });
            if (targetRelease.IsReleased)
                return Results.BadRequest(new { error = $"Build {targetRelease.Version} is released and read-only.", code = "released_build_read_only" });

            var current = await CurrentRequirementRevisionAsync(db, artifact, request.TargetReleaseId, ct);
            if (current is null)
                return Results.BadRequest(new { error = "This requirement is not active in the selected build.", code = "requirement_not_carried_by_build" });
            if (request.RequirementRevisionId is Guid expectedRevision && expectedRevision != current.Id)
                return Results.Conflict(new { error = "This requirement changed in the selected build. Refresh and select it again.", code = "stale_requirement_revision", currentRevisionId = current.Id });
            var binding = ladderPolicy.Definition(artifact.Level).ChangeRequest;
            if (binding is null || !ladderPolicy.AcceptsChangeRequest(binding.Type, binding.SoftwareLevel, artifact.Level))
                return Results.BadRequest(new { error = $"The configured project ladder does not allow {artifact.Level} change control.", code = "change_control_disabled" });

            var actor = principal.UserName;
            var now = DateTimeOffset.UtcNow;
            SystemChangeRequest scr;
            if (request.ExistingScrId is Guid existingId)
            {
                var loadedScr = await repository.GetAsync(existingId, ct);
                if (loadedScr is null)
                    return Results.NotFound(new { error = "The selected Draft change request was not found.", code = "change_request_not_found" });
                scr = loadedScr;
                if (scr.ProjectId != artifact.ProjectId)
                    return Results.BadRequest(new { error = "The selected change request belongs to a different Project.", code = "project_mismatch" });
                if (scr.TargetReleaseId != request.TargetReleaseId)
                    return Results.BadRequest(new { error = "The selected change request has a different build.", code = "build_mismatch" });
                if (scr.Type != binding.Type || scr.SoftwareLevel != binding.SoftwareLevel)
                    return Results.BadRequest(new { error = $"The selected change request is not bound to the {artifact.Level} requirement level.", code = "level_binding_mismatch" });
                if (scr.State != ChangeRequestState.Draft)
                    return Results.BadRequest(new { error = "Requirement proposals can be added only to a Draft change request.", code = "draft_required" });
                if (scr.AuthorId != actor && !principal.IsAdministrator)
                    return Results.Forbid();
                if (request.RequirementRevisionId is not Guid requestedRevisionId || requestedRevisionId != current.Id)
                    return Results.Conflict(new { error = "This requirement changed in the selected build. Refresh and select the exact current revision.", code = "stale_requirement_revision", currentRevisionId = current.Id });
                // A whole-draft checkout can overwrite an aggregate mutation when it checks in its
                // stale snapshot. This remains incompatible even when the checkout belongs to the
                // current user; finish or discard every active exclusive session first.
                var activeSessions = await db.ArtifactEditSessions
                    .Where(x => x.ArtifactId == scr.Id && (x.ArtifactType == "ChangeRequest" || x.ArtifactType == "SCR")
                        && x.IsExclusive && x.State == EditSessionState.Active)
                    .ToListAsync(ct);
                foreach (var expired in activeSessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now);
                var active = activeSessions.FirstOrDefault(x => x.State == EditSessionState.Active);
                if (active is not null)
                    return Results.Conflict(new { error = $"This Draft is checked out by {active.UserName}; finish or discard that edit before adding a proposal.", code = "active_edit_session", holder = active.UserName, active.ExpiresAt });
                var existingProposal = scr.RequirementChanges.FirstOrDefault(x =>
                    string.Equals(x.BaseNumber, artifact.BaseNumber, StringComparison.OrdinalIgnoreCase));
                // Retrying a successful browser request after a lost response must reopen the exact proposal
                // rather than turning the retry into a misleading stale-version error. This is idempotent only
                // for the same authoritative requirement revision and operation kind; an older proposal still
                // needs deliberate refresh/reselection.
                if (existingProposal is not null && existingProposal.Revision == current.Revision + 1
                    && existingProposal.Kind == request.Kind)
                    return Results.Ok(new
                    {
                        scr.Id, scr.DisplayNumber, scr.Title, scr.Version, proposalId = existingProposal.Id,
                        requirementRevisionId = current.Id, requirementDisplayNumber = $"{artifact.BaseNumber}.{current.Revision:D2}",
                        duplicate = true
                    });
                if (request.ExpectedVersion is null || scr.Version != request.ExpectedVersion)
                    return Results.Conflict(new { error = "This Draft changed after it was loaded. Refresh before adding the requirement.", code = "stale_version", currentVersion = scr.Version });
            }
            else
            {
                var number = await IdentifierAllocator.NextChangeRequestAsync(db, binding.Type, binding.SoftwareLevel, ct, ladderPolicy);
                scr = new SystemChangeRequest(number, 0, artifact.ProjectId, request.TargetReleaseId,
                    string.IsNullOrWhiteSpace(request.Title) ? $"{request.Kind} {artifact.BaseNumber}" : request.Title,
                    $"A controlled change is proposed for {artifact.BaseNumber}.",
                    $"Assess parent/child traceability, verification coverage, specifications, software builds, and open collaboration for {artifact.BaseNumber}.",
                    $"Implement the approved {request.Kind.ToString().ToLowerInvariant()} through this exact {number[..number.IndexOf('-')]} revision.",
                    actor, now, binding.Type, softwareLevel: binding.SoftwareLevel, ladderPolicy: ladderPolicy);
                await repository.AddAsync(scr, ct);
            }

            if (scr.RequirementChanges.Any(x => string.Equals(x.BaseNumber, artifact.BaseNumber, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = "This Draft already contains a proposal for the selected requirement.", code = "duplicate_requirement_proposal" });
            var profile = await db.RequirementRevisionProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == current.Id, ct);
            var dispositions = RequirementAuthoringJson.PendingImpactDispositions;
            try
            {
                var change = scr.AddRequirementChange(actor, artifact.BaseNumber, current.Revision + 1, artifact.Level,
                    request.Kind, request.Kind == RequirementChangeKind.Retire ? "" : current.Statement,
                    current.Rationale, current.VerificationMethod, now, profile?.RichText ?? current.Statement,
                    profile?.AttributesJson ?? "{}", dispositions, administratorAuthority: principal.IsAdministrator,
                    ladderPolicy: ladderPolicy);
                if (!await db.ArtifactWatches.AnyAsync(x => x.ArtifactId == artifactId && x.UserName == actor, ct))
                    db.ArtifactWatches.Add(new(artifact.ProjectId, "Requirement", artifactId, actor, actor, now));
                await repository.SaveAsync(ct);
                return Results.Created($"/api/change-requests/{scr.Id}", new
                {
                    scr.Id, scr.DisplayNumber, scr.Title, scr.Version, proposalId = change.Id,
                    requirementRevisionId = current.Id, requirementDisplayNumber = $"{artifact.BaseNumber}.{current.Revision:D2}"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException)
            {
                var currentValues = await db.Entry(scr).GetDatabaseValuesAsync(ct);
                var currentVersion = currentValues?.GetValue<long>(nameof(SystemChangeRequest.Version));
                return Results.Conflict(new { error = "This Draft changed after it was loaded. Refresh before adding the requirement.", code = "stale_version", currentVersion });
            }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Another controlled change was created concurrently. Refresh and retry.", code = "concurrent_change" }); }
        });

        app.MapPost("/api/enterprise-requirements/schemas",async(CreateArtifactSchemaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Administrator))return Results.Forbid();
            try
            {
                var policy=await policyResolver.ResolveAsync(request.ProjectId,ct);
                if(!EnterpriseRequirementsService.TryLevel(request.AppliesTo,out var level,policy)
                    || policy.Definition(level).RequirementsCatalogue is null)
                    return Results.BadRequest(new{error="A schema must target a configured requirements level."});
                var schema=new ArtifactSchemaDefinition(request.ProjectId,request.Key,request.Name,level.ToString(),request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ArtifactSchemas.Add(schema);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/schemas/{schema.Id}",new{schema.Id});
            }
            catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/schemas/{id:guid}/fields",async(Guid id,CreateSchemaFieldRequest request,HttpContext http,AeroLinkDbContext db,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            var schema=await db.ArtifactSchemas.Include(x=>x.Fields).SingleOrDefaultAsync(x=>x.Id==id,ct);if(schema is null)return Results.NotFound();if(!http.UserAccount().IsAdministrator)return Results.Forbid();try{var policy=await policyResolver.ResolveAsync(schema.ProjectId,ct);if(!schema.IsActive||!EnterpriseRequirementsService.TryLevel(schema.AppliesTo,out var level,policy)||policy.Definition(level).RequirementsCatalogue is null)return Results.BadRequest(new{error="The selected schema is not part of the effective project ladder."});schema.AddField(request.Key,request.Label,request.Type,request.IsRequired,request.SortOrder,request.OptionsJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/specifications",async(CreateSpecificationRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var policy=await policyResolver.ResolveAsync(request.ProjectId,ct);if(!EnterpriseRequirementsService.TryLevel(request.Level,out var level,policy)||policy.Definition(level).RequirementsCatalogue is null)return Results.BadRequest(new{error="A specification must target a configured requirements level."});var spec=new RequirementSpecification(request.ProjectId,request.DocumentNumber,request.Title,level.ToString(),request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementSpecifications.Add(spec);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{spec.Id}",new{spec.Id});}catch(Exception ex)when(ex is DomainException or ArgumentException){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/specifications/{id:guid}/sections",async(Guid id,CreateSectionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            var specification=await db.RequirementSpecifications.SingleOrDefaultAsync(x=>x.Id==id,ct);if(specification is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,specification.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();var policy=await policyResolver.ResolveAsync(specification.ProjectId,ct);if(!specification.IsActive||!EnterpriseRequirementsService.TryLevel(specification.Level,out var level,policy)||policy.Definition(level).RequirementsCatalogue is null)return Results.BadRequest(new{error="The selected specification is not part of the effective project ladder."});var node=new SpecificationNode(id,request.ParentId,request.Position,SpecificationNodeType.Section,request.Heading,null,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.SpecificationNodes.Add(node);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{id}/sections/{node.Id}",new{node.Id});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/comments",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var projectId=await db.Requirements.Where(x=>x.Id==artifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var comments=await db.ArtifactComments.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&x.ArtifactType=="Requirement").ToListAsync(ct);
            return Results.Ok(comments.OrderBy(x=>x.CreatedAt).Select(x=>new{x.Id,x.RevisionId,x.ParentCommentId,x.Body,x.MentionsJson,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.ResolvedBy,x.ResolvedAt,x.Disposition}));
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/comments",async(Guid artifactId,CreateCommentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();if(request.RevisionId is not null&&!await db.RequirementRevisions.AnyAsync(x=>x.Id==request.RevisionId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The comment revision is not part of this requirement."});if(request.ParentCommentId is not null&&!await db.ArtifactComments.AnyAsync(x=>x.Id==request.ParentCommentId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The parent comment is not part of this requirement."});try{var actor=http.UserAccount().UserName;var now=DateTimeOffset.UtcNow;var comment=new ArtifactComment(artifact.ProjectId,"Requirement",artifactId,request.RevisionId,request.ParentCommentId,request.Body,JsonSerializer.Serialize(request.Mentions??[]),actor,now);db.ArtifactComments.Add(comment);var requested=(request.Mentions??[]).Select(x=>x.Trim().ToLowerInvariant()).ToHashSet();var watchers=await db.ArtifactWatches.AsNoTracking().Where(x=>x.ArtifactId==artifactId).Select(x=>x.UserName).ToListAsync(ct);requested.UnionWith(watchers);if(request.ParentCommentId is not null){var parentAuthor=await db.ArtifactComments.Where(x=>x.Id==request.ParentCommentId).Select(x=>x.CreatedBy).SingleAsync(ct);requested.Add(parentAuthor.ToLowerInvariant());}var recipients=await db.UserAccounts.AsNoTracking().Where(x=>requested.Contains(x.UserName)&&x.UserName!=actor).Select(x=>x.UserName).ToListAsync(ct);foreach(var recipient in recipients)db.UserNotifications.Add(new(artifact.ProjectId,recipient,"RequirementComment",$"Discussion on {artifact.BaseNumber}",$"{actor}: {request.Body}",$"requirement:{artifactId}",artifactId,now));await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/{artifactId}/comments/{comment.Id}",new{comment.Id,notified=recipients.Count});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/comments/{id:guid}/resolve", async (Guid id, ResolveCommentRequest request,
            HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            var comment = await db.ArtifactComments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (comment is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, comment.ProjectId, ct)) return Results.Forbid();
            var procedure = comment.ArtifactType is "TestCase" or "TestProcedure"
                ? await db.TestProcedures.AsNoTracking()
                    .Where(x => x.Id == comment.ArtifactId)
                    .Select(x => new { x.ProjectId, x.Level, x.ArtifactKind }).SingleOrDefaultAsync(ct)
                : null;
            if (procedure is not null && procedure.ProjectId == comment.ProjectId)
            {
                var policy = await policyResolver.ResolveAsync(comment.ProjectId, ct);
                var enabled = procedure.Level switch
                {
                    TestProcedureLevel.System => policy.VerificationProfile(RequirementLevel.System).Enables(procedure.ArtifactKind),
                    TestProcedureLevel.HighLevel => policy.VerificationProfile(RequirementLevel.HighLevel).Enables(procedure.ArtifactKind),
                    TestProcedureLevel.LowLevel => policy.VerificationProfile(RequirementLevel.LowLevel).Enables(procedure.ArtifactKind),
                    _ => false,
                };
                if (!enabled) return Results.BadRequest(new { error = "Discussion is unavailable for this disabled verification artifact.", code = "verification_discussion_disabled" });
                var releaseDecision = await VerificationDiscussionReleaseAuthority.ValidateAsync(db, comment.ProjectId,
                    request.ReleaseId, comment.RevisionId, comment.ArtifactId, ct);
                if (!releaseDecision.Allowed)
                    return Results.BadRequest(new { error = releaseDecision.Error, code = releaseDecision.Code });
            }
            try
            {
                comment.Resolve(http.UserAccount().UserName, request.Disposition ?? "", DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/collaboration",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;
            var watchers=await db.ArtifactWatches.AsNoTracking().Where(x=>x.ArtifactId==artifactId).OrderBy(x=>x.UserName).Select(x=>new{x.UserName,x.CreatedAt,isCurrent=x.UserName==actor}).ToListAsync(ct);var assignments=await db.ArtifactAssignments.AsNoTracking().Where(x=>x.ArtifactId==artifactId).ToListAsync(ct);
            return Results.Ok(new{watching=watchers.Any(x=>x.isCurrent),watchers,assignments=assignments.OrderBy(x=>x.State).ThenBy(x=>x.DueAt).Select(x=>new{x.Id,x.CommentId,x.AssignedTo,x.Title,x.Description,x.DueAt,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.UpdatedAt,x.Version,x.CompletedBy})});
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/watch",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;var existing=await db.ArtifactWatches.SingleOrDefaultAsync(x=>x.ArtifactId==artifactId&&x.UserName==actor,ct);if(existing is null)db.ArtifactWatches.Add(new(artifact.ProjectId,"Requirement",artifactId,actor,actor,DateTimeOffset.UtcNow));else db.ArtifactWatches.Remove(existing);await db.SaveChangesAsync(ct);return Results.Ok(new{watching=existing is null});
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/assignments",async(Guid artifactId,CreateAssignmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var assignee=request.AssignedTo.Trim().ToLowerInvariant();if(!await db.UserAccounts.AnyAsync(x=>x.UserName==assignee,ct))return Results.BadRequest(new{error="The assigned AeroLink user does not exist."});if(request.CommentId is not null&&!await db.ArtifactComments.AnyAsync(x=>x.Id==request.CommentId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The linked comment is not part of this requirement."});try{var actor=http.UserAccount().UserName;var now=DateTimeOffset.UtcNow;var assignment=new ArtifactAssignment(artifact.ProjectId,"Requirement",artifactId,request.CommentId,assignee,request.Title,request.Description,request.DueAt,actor,now);db.ArtifactAssignments.Add(assignment);db.UserNotifications.Add(new(artifact.ProjectId,assignee,"RequirementAssignment",request.Title,$"{actor} assigned work on {artifact.BaseNumber}.",$"requirement:{artifactId}",artifactId,now));await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/assignments/{assignment.Id}",new{assignment.Id});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/assignments/{id:guid}/complete",async(Guid id,CompleteAssignmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var assignment=await db.ArtifactAssignments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(assignment is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,assignment.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;if(assignment.AssignedTo!=actor&&!http.UserAccount().IsAdministrator)return Results.Forbid();try{assignment.Complete(actor,request.ExpectedVersion,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}
        });

        app.MapGet("/api/enterprise-requirements/work-queue",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;var assignments=await db.ArtifactAssignments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.AssignedTo==actor&&x.State==AssignmentState.Open).ToListAsync(ct);var ids=assignments.Select(x=>x.ArtifactId).ToList();var artifacts=await db.Requirements.AsNoTracking().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);var notifications=await db.UserNotifications.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Recipient==actor&&x.State==NotificationState.Unread).Take(100).ToListAsync(ct);return Results.Ok(new{assignments=assignments.OrderBy(x=>x.DueAt).Select(x=>new{x.Id,x.ArtifactId,requirement=artifacts.TryGetValue(x.ArtifactId,out var a)?a.BaseNumber:"Requirement",x.Title,x.Description,x.DueAt,x.Version,overdue=x.DueAt<DateTimeOffset.UtcNow}),notifications=notifications.OrderByDescending(x=>x.CreatedAt).Select(x=>new{x.Id,x.Type,x.Title,x.Detail,x.Route,x.ArtifactId,x.CreatedAt})});
        });

        app.MapPost("/api/enterprise-requirements/notifications/{id:guid}/read",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {var notification=await db.UserNotifications.SingleOrDefaultAsync(x=>x.Id==id&&x.Recipient==http.UserAccount().UserName,ct);if(notification is null)return Results.NotFound();notification.MarkRead(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();});

        app.MapPost("/api/enterprise-requirements/views",async(CreateSavedViewRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();
            var name=(request.Name??"").Trim();
            if(name.Length==0)return Results.BadRequest(new{error="A saved view needs a name.",code="saved_view_name_required"});
            // Validated before storage, not on the way out. A view is a worklist somebody else opens, so a
            // field this workspace cannot apply or a column it cannot show must never reach the record.
            var contract=SavedViewContract.Normalize(request.QueryJson,request.ColumnsJson);
            if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="saved_view_contract_invalid"});
            var owner=http.UserAccount().Id;
            // Deliberate rather than incidental: a repeat name is refused and says so, instead of quietly
            // creating the second of two views nobody could tell apart or remove.
            if(await db.SavedRequirementViews.AnyAsync(x=>x.ProjectId==request.ProjectId&&x.OwnerId==owner&&x.Name==name,ct))
                return Results.Conflict(new{error=$"You already have a saved view named '{name}'. Rename it, or update the existing one.",code="saved_view_duplicate_name"});
            var view=new SavedRequirementView(request.ProjectId,owner,name,contract.QueryJson,contract.ColumnsJson,request.IsShared,DateTimeOffset.UtcNow);db.SavedRequirementViews.Add(view);
            try{await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/views/{view.Id}",new{view.Id});}catch(DbUpdateException){return Results.Conflict(new{error="A saved view with that name already exists.",code="saved_view_duplicate_name"});}
        });

        // Owner-only, and answered as Not Found rather than Forbidden for somebody else's view: a shared view
        // is readable, and confirming that a particular id exists but is not yours is more than a reader of a
        // shared list needs to know.
        app.MapPut("/api/enterprise-requirements/views/{id:guid}",async(Guid id,UpdateSavedViewRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var owner=http.UserAccount().Id;
            var view=await db.SavedRequirementViews.SingleOrDefaultAsync(x=>x.Id==id&&x.OwnerId==owner,ct);
            if(view is null)return Results.NotFound();
            var now=DateTimeOffset.UtcNow;
            if(request.Name is not null)
            {
                var name=request.Name.Trim();
                if(name.Length==0)return Results.BadRequest(new{error="A saved view needs a name.",code="saved_view_name_required"});
                if(!string.Equals(name,view.Name,StringComparison.Ordinal)&&await db.SavedRequirementViews.AnyAsync(x=>x.ProjectId==view.ProjectId&&x.OwnerId==owner&&x.Name==name&&x.Id!=id,ct))
                    return Results.Conflict(new{error=$"You already have a saved view named '{name}'.",code="saved_view_duplicate_name"});
                view.Rename(name,now);
            }
            if(request.IsShared is not null)view.SetShared(request.IsShared.Value,now);
            if(request.QueryJson is not null||request.ColumnsJson is not null)
            {
                var contract=SavedViewContract.Normalize(request.QueryJson??view.QueryJson,request.ColumnsJson??view.ColumnsJson);
                if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="saved_view_contract_invalid"});
                view.Replace(contract.QueryJson,contract.ColumnsJson,now);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new{view.Id,view.Name,view.IsShared,view.QueryJson,view.ColumnsJson});
        });

        app.MapDelete("/api/enterprise-requirements/views/{id:guid}",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var view=await db.SavedRequirementViews.SingleOrDefaultAsync(x=>x.Id==id&&x.OwnerId==http.UserAccount().Id,ct);if(view is null)return Results.NotFound();db.Remove(view);await db.SaveChangesAsync(ct);return Results.NoContent();});

        app.MapPost("/api/enterprise-requirements/bulk/preview",async(BulkRequirementRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
            var policy=await policyResolver.ResolveAsync(request.ProjectId,ct);
            var allowedLevels=policy.Definitions.Where(x=>x.RequirementsCatalogue is not null).Select(x=>x.Level).ToHashSet();
            var selectedSpecification=request.SpecificationId is null?null:await db.RequirementSpecifications.SingleOrDefaultAsync(x=>x.Id==request.SpecificationId&&x.ProjectId==request.ProjectId,ct);
            if(selectedSpecification is not null&&(!selectedSpecification.IsActive||!EnterpriseRequirementsService.TryLevel(selectedSpecification.Level,out var selectedLevel,policy)||!allowedLevels.Contains(selectedLevel)))return Results.BadRequest(new{error="The target specification is not part of the effective project ladder."});
            if(request.SpecificationId is not null&&selectedSpecification is null)return Results.BadRequest(new{error="The target specification is not part of this Project."});
            if(request.SectionId is not null&&!await db.SpecificationNodes.AnyAsync(x=>x.Id==request.SectionId&&x.SpecificationId==request.SpecificationId&&x.Type==SpecificationNodeType.Section,ct))return Results.BadRequest(new{error="The target section is not part of this specification."});
            var valid=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId&&request.ArtifactIds.Contains(x.Id)&&allowedLevels.Contains(x.Level)).Select(x=>x.Id).ToListAsync(ct);var payload=JsonSerializer.Serialize(new BulkJobPayload(valid,request.Tag,request.SpecificationId,request.SectionId));var job=new EnterpriseOperationJob(request.ProjectId,"RequirementBulkClassify",payload,valid.Count,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,requested=request.ArtifactIds.Count,valid=valid.Count,rejected=request.ArtifactIds.Count-valid.Count,operation=$"Add tag '{request.Tag}'"+(request.SpecificationId is null?"":" and place in specification")});
        });

        app.MapPost("/api/enterprise-requirements/bulk/{id:guid}/commit",async(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();if(job.State!=EnterpriseJobState.Preview)return Results.BadRequest(new{error="This bulk job is no longer awaiting commit."});var policy=await policyResolver.ResolveAsync(job.ProjectId,ct);var allowedLevels=policy.Definitions.Where(x=>x.RequirementsCatalogue is not null).Select(x=>x.Level).ToHashSet();var payload=JsonSerializer.Deserialize<BulkJobPayload>(job.RequestJson)!;var selectedSpecification=payload.SpecificationId is null?null:await db.RequirementSpecifications.SingleOrDefaultAsync(x=>x.Id==payload.SpecificationId&&x.ProjectId==job.ProjectId,ct);if(payload.SpecificationId is not null&&(selectedSpecification is null||!selectedSpecification.IsActive||!EnterpriseRequirementsService.TryLevel(selectedSpecification.Level,out var selectedLevel,policy)||!allowedLevels.Contains(selectedLevel)))return Results.BadRequest(new{error="The target specification is not part of the effective project ladder."});if(payload.SectionId is not null&&(!payload.SpecificationId.HasValue||!await db.SpecificationNodes.AnyAsync(x=>x.Id==payload.SectionId&&x.SpecificationId==payload.SpecificationId&&x.Type==SpecificationNodeType.Section,ct)))return Results.BadRequest(new{error="The target section is not part of this specification."});var invalidArtifacts=await db.Requirements.AsNoTracking().Where(x=>payload.ArtifactIds.Contains(x.Id)&&x.ProjectId==job.ProjectId&&!allowedLevels.Contains(x.Level)).Select(x=>x.Id).ToListAsync(ct);if(invalidArtifacts.Count!=0)return Results.BadRequest(new{error="The bulk selection contains a requirement outside the effective project ladder."});var revisions=await db.RequirementRevisions.Where(x=>payload.ArtifactIds.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);var current=revisions.GroupBy(x=>x.ArtifactId).Select(x=>x.First()).ToList();var revisionIds=current.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);foreach(var profile in profiles)profile.AddTag(payload.Tag,http.UserAccount().UserName,DateTimeOffset.UtcNow);
            if(payload.SpecificationId is not null){var parent=payload.SectionId;var existing=(await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.RequirementArtifactId!=null).Select(x=>x.RequirementArtifactId!.Value).ToListAsync(ct)).ToHashSet();var position=await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.ParentId==parent).Select(x=>(int?)x.Position).MaxAsync(ct)??0;foreach(var artifactId in payload.ArtifactIds.Where(x=>!existing.Contains(x)))db.SpecificationNodes.Add(new(payload.SpecificationId.Value,parent,++position,SpecificationNodeType.Requirement,"",artifactId,http.UserAccount().UserName,DateTimeOffset.UtcNow));}
            job.Complete(profiles.Count,0,JsonSerializer.Serialize(new{tagged=profiles.Count,placed=payload.SpecificationId is not null}),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,state=job.State.ToString(),job.SucceededCount,job.ResultJson});
        });

        app.MapGet("/api/enterprise-requirements/interchange",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var jobs=await db.RequirementInterchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);var mappings=await db.RequirementImportMappings.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);return Results.Ok(new{mappings=mappings.Select(x=>new{x.Id,x.Name,x.MappingJson,x.Version,x.UpdatedAt}),jobs=jobs.OrderByDescending(x=>x.CreatedAt).Take(50).Select(x=>new{x.Id,x.FileName,x.Sha256,x.ValidRows,x.InvalidRows,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.CreatedChangeRequestId,x.CompletedAt})});
        });

        app.MapPost("/api/enterprise-requirements/import-mappings",async(CreateImportMappingRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();try{using var _=JsonDocument.Parse(request.MappingJson);var mapping=new RequirementImportMapping(request.ProjectId,request.Name,request.MappingJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementImportMappings.Add(mapping);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/import-mappings/{mapping.Id}",new{mapping.Id});}catch(Exception ex)when(ex is DomainException or DbUpdateException or JsonException){return Results.BadRequest(new{error=ex.Message});}});

        app.MapGet("/api/enterprise-requirements/import/{id:guid}/errors.csv",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var job=await db.RequirementInterchangeJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();var rows=JsonSerializer.Deserialize<List<InterchangeRequirementRow>>(job.RowsJson)??[];static string Csv(string value){if(value.Length>0&&"=+-@".Contains(value[0]))value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";}var text="Row,Identifier,Level,Statement,Errors\r\n"+string.Join("\r\n",rows.Where(x=>!x.Valid).Select(x=>$"{x.RowNumber},{Csv(x.Identifier)},{Csv(x.Level)},{Csv(x.Statement)},{Csv(string.Join("; ",x.Errors))}"));return Results.Text(text,"text/csv",Encoding.UTF8,200);
        });

        app.MapGet("/api/enterprise-requirements/performance",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var total=await db.Requirements.AsNoTracking().CountAsync(x=>x.ProjectId==projectId,ct);var samples=new List<PerformanceSample>();async Task Measure(string name,long target,Func<Task> action){await action();var timings=new List<long>();for(var i=0;i<3;i++){var sw=Stopwatch.StartNew();await action();sw.Stop();timings.Add(sw.ElapsedMilliseconds);}var p95=timings.Max();samples.Add(new(name,target,p95,p95<=target,timings));}await Measure("page_100",500,async()=>{_=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.BaseNumber).Take(100).Select(x=>new{x.Id,x.BaseNumber}).ToListAsync(ct);});await Measure("exact_identifier",300,async()=>{_=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber=="SYSR-000001").Select(x=>x.Id).FirstOrDefaultAsync(ct);});await Measure("open_collaboration",500,async()=>{_=await db.ArtifactComments.AsNoTracking().CountAsync(x=>x.ProjectId==projectId&&x.State==CollaborationState.Open,ct);});return Results.Ok(new{totalRequirements=total,scaleTarget=50_000,measuredAt=DateTimeOffset.UtcNow,allPassed=samples.All(x=>x.Passed),samples});
        });

        app.MapPost("/api/enterprise-requirements/import/preview",async(Guid projectId,Guid? mappingId,HttpContext http,AeroLinkDbContext db,IdentityService identity,ILadderPolicy ladderPolicy,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer))return Results.Forbid();
            ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            if(!http.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data with a CSV or XLSX file."});var form=await http.Request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty CSV or XLSX file."});if(file.Length>25*1024*1024)return Results.BadRequest(new{error="Import files are limited to 25 MB."});if(!file.FileName.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)&&!file.FileName.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Only CSV and XLSX files are supported."});
            await using var stream=file.OpenReadStream();using var memory=new MemoryStream();await stream.CopyToAsync(memory,ct);var bytes=memory.ToArray();memory.Position=0;IReadOnlyList<InterchangeRequirementRow> parsed;try{parsed=EnterpriseRequirementsService.ParseImport(memory,file.FileName,ladderPolicy);}catch(Exception ex){return Results.BadRequest(new{error=$"The workbook could not be read: {ex.Message}"});}
            var existing=(await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).Select(x=>x.BaseNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);var duplicates=parsed.GroupBy(x=>x.Identifier,StringComparer.OrdinalIgnoreCase).Where(x=>x.Count()>1).Select(x=>x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var rows=parsed.Select(x=>{var errors=x.Errors.ToList();if(existing.Contains(x.Identifier))errors.Add("Identifier already exists in this Project; use a change-request modification workflow.");if(duplicates.Contains(x.Identifier))errors.Add("Identifier is duplicated in this import.");return x with{Valid=errors.Count==0,Errors=errors};}).ToList();var mapping=mappingId is null?"{\"mode\":\"standard-columns\"}":await db.RequirementImportMappings.Where(x=>x.Id==mappingId&&x.ProjectId==projectId).Select(x=>x.MappingJson).SingleOrDefaultAsync(ct)??"{\"mode\":\"standard-columns\"}";var job=new RequirementInterchangeJob(projectId,file.FileName,EnterpriseRequirementsService.Hash(bytes),mapping,JsonSerializer.Serialize(rows),rows.Count(x=>x.Valid),rows.Count(x=>!x.Valid),http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementInterchangeJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,job.FileName,job.Sha256,total=rows.Count,job.ValidRows,job.InvalidRows,rows=rows.Take(200)});
        }).DisableAntiforgery();

        app.MapPost("/api/enterprise-requirements/import/{id:guid}/commit",async(Guid id,CommitImportRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,ILadderPolicy ladderPolicy,IProjectLadderPolicyResolver policyResolver,CancellationToken ct)=>
        {
            var job=await db.RequirementInterchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(job.InvalidRows>0)return Results.BadRequest(new{error="Resolve every invalid row before committing this import."});if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();ladderPolicy = await policyResolver.ResolveAsync(job.ProjectId, ct);var rows=JsonSerializer.Deserialize<List<InterchangeRequirementRow>>(job.RowsJson)??[];try{var now=DateTimeOffset.UtcNow;var baseNumber=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,request.SoftwareLevel,ct,ladderPolicy);var scr=new SystemChangeRequest(baseNumber,0,job.ProjectId,request.TargetReleaseId,request.Title,request.Problem,request.Analysis,request.Solution,http.UserAccount().UserName,now,request.Type,softwareLevel:request.SoftwareLevel, ladderPolicy: ladderPolicy);foreach(var row in rows){if(!EnterpriseRequirementsService.TryLevel(row.Level,out var reqLevel,ladderPolicy))throw new DomainException($"The imported row names an unconfigured level: {row.Level}.");scr.AddRequirementChange(http.UserAccount().UserName,row.Identifier,0,reqLevel,RequirementChangeKind.Introduce,row.Statement,row.Rationale,row.VerificationMethod,now,impactDispositionJson:RequirementAuthoringJson.PendingImpactDispositions, ladderPolicy: ladderPolicy);}db.SystemChangeRequests.Add(scr);job.Commit(scr.Id,now);await db.SaveChangesAsync(ct);return Results.Created($"/api/change-requests/{scr.Id}",new{scr.Id,scr.DisplayNumber,imported=rows.Count});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        // Enterprise hardening: controlled content, durable operations, merge protection,
        // integrity qualification, and operator-facing health evidence.

        // Inline images are their own surface rather than a use of the attachment vault.
        //
        // An image inside a requirement statement is not a document somebody attached; it is part of what the
        // statement says, and it has to be storable before the record that references it exists, because an author
        // writes the figure into the paragraph as they are drafting it. Uploading here stores and hashes the file
        // against the project, and the authored content then references it by identifier. The file is never
        // duplicated into the record, so one diagram used in five requirements is stored once and stays one thing.
        app.MapPost("/api/content/images",async(HttpRequest request,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            if(!request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data."});
            var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
            if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty image."});
            if(!Guid.TryParse(form["projectId"],out var projectId))return Results.BadRequest(new{error="A project identifier is required."});
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            // Only formats every renderer here can produce. An image the workspace shows but the generated Word
            // document cannot would make a controlled document disagree with the record it came from.
            var contentType=(file.ContentType??"").ToLowerInvariant();
            if(contentType is not("image/png" or "image/jpeg"))return Results.BadRequest(new{error="Inline images must be PNG or JPEG so every generated document can render them."});
            if(file.Length>12*1024*1024)return Results.BadRequest(new{error="Inline images are limited to 12 MB. Attach larger files as controlled attachments instead."});
            // The declared content type is a claim by whoever uploaded the file. This image is streamed back inline
            // from this deployment's own origin, so the claim has to be checked against the bytes: a file that says
            // PNG and contains markup would otherwise be stored, referenced from a requirement, and served to an
            // approver by us.
            var signature=new byte[8];
            await using(var probe=file.OpenReadStream())
            {
                var read=await probe.ReadAtLeastAsync(signature,signature.Length,throwOnEndOfStream:false,ct);
                if(read<signature.Length||!PngImage.IsDeclaredImage(signature,contentType))
                    return Results.BadRequest(new{error="That file is not the image type it claims to be."});
            }
            var stored=await store.StoreAsync(file.OpenReadStream(),file.FileName,contentType,ct);
            try
            {
                var attachment=new ControlledAttachment(projectId,"InlineImage",projectId,null,Guid.NewGuid(),1,
                    string.IsNullOrWhiteSpace(form["alt"])?stored.OriginalFileName:form["alt"].ToString(),"",
                    stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256,stored.StorageKey,null,
                    http.UserAccount().UserName,DateTimeOffset.UtcNow);
                db.ControlledAttachments.Add(attachment);await db.SaveChangesAsync(ct);
                return Results.Created($"/api/content/images/{attachment.Id}",new{attachment.Id,attachment.OriginalFileName,attachment.Size,attachment.Sha256});
            }
            catch{store.Delete(stored.StorageKey);throw;}
        }).DisableAntiforgery();

        app.MapGet("/api/content/images/{id:guid}",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            var item=await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ArtifactType=="InlineImage",ct);
            if(item is null)return Results.NotFound();
            if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();
            if(!store.Exists(item.StorageKey))return Results.NotFound();
            return Results.File(store.OpenRead(item.StorageKey),item.ContentType,enableRangeProcessing:true);
        });
    }

    private static async Task<RequirementRevision?> CurrentRequirementRevisionAsync(
        AeroLinkDbContext db, RequirementArtifact artifact, Guid releaseId, CancellationToken ct)
    {
        var baselineId = await BuildScope.EffectiveBaselineAsync(db, artifact.ProjectId, releaseId, ct);
        if (baselineId is Guid effectiveBaselineId)
        {
            var revisionId = await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == effectiveBaselineId && x.ArtifactId == artifact.Id)
                .Select(x => (Guid?)x.RevisionId).SingleOrDefaultAsync(ct);
            return revisionId is null
                ? null
                : await db.RequirementRevisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == revisionId && x.ArtifactId == artifact.Id
                        && x.State == RequirementRevisionState.Active, ct);
        }

        // A project-latest revision is not evidence that this build carries that revision. Until the
        // selected build has an authoritative materialized baseline, the proposal operation must fail closed.
        return null;
    }

    /// <summary>
    /// The COVERAGE side of the one typed effectivity population. Requirement coverage remains on exact
    /// software Case revisions (and System Procedure revisions), so the coverage filter set is never the
    /// executable Procedure set of a #726 baseline. Pre-manifest compatibility projections keep their own
    /// coverage-derived identities.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>?> EffectiveCoverageRevisionIdsAsync(
        AeroLinkDbContext db, Guid? effectiveBaselineId, ILadderPolicy ladderPolicy,
        TestProcedureEffectivityResult? effectivity, CancellationToken ct)
    {
        if (effectivity is null || effectiveBaselineId is null) return effectivity?.RevisionIds;
        if (!effectivity.IsExactManifest) return effectivity.RevisionIds;
        var procedureEnabledLevels = ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).VerificationProfile?.Enables(
                    VerificationArtifactKind.Procedure) == true
                && ladderPolicy.Definition(level).VerificationProfile?.Enables(
                    VerificationArtifactKind.Case) == true)
            .Select(ladderPolicy.ProcedureLevel)
            .ToHashSet();
        return (await BaselineExecutableMembership.ForPopulationAsync(
            db, effectiveBaselineId.Value, procedureEnabledLevels, ct)).CoverageRevisionIds;
    }

}
