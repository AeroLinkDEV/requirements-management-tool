using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The shell everything else hangs from — programs, projects, releases — with the queues and
/// dashboards that tell somebody where their attention is needed.
///
/// These are the reads that run on every navigation, so what they cost is what the product feels like.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        // Unsubscribe is reachable without signing in, because it is followed from a mail client. The signed
        // token is what proves the link came from this deployment; without it anyone could silence anyone else's
        // approval notices. Always answers the same way, so the endpoint cannot be used to discover who exists.
        app.MapGet("/api/notifications/unsubscribe", async (string? recipient, string? token, AeroLinkDbContext db, UnsubscribeTokenService tokens, CancellationToken ct) =>
        {
            const string answer = "If that link was valid, email notification is now off for that account. Sign in to AeroLink to turn it back on.";
            if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(token) || !tokens.Validate(recipient, token))
                return Results.Text(answer);
            var name = recipient.Trim().ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var preference = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.Recipient == name, ct);
            if (preference is null) { preference = new NotificationPreference(name, now); db.NotificationPreferences.Add(preference); }
            preference.SetEmailEnabled(false, now);
            db.SecurityAuditEvents.Add(new("NotificationEmailDisabled", name, name, "Success", "Email notification turned off from an unsubscribe link.", "local", now));
            await db.SaveChangesAsync(ct);
            return Results.Text(answer);
        }).AllowAnonymous();

        app.MapPost("/api/showcase/seed", async (HttpContext http,FmsShowcaseSeeder seeder, IdentitySeeder identities, EnterpriseRequirementsService workspace, IConfiguration configuration, CancellationToken ct) => {if(!http.UserAccount().IsAdministrator)return Results.Forbid();if(!configuration.GetValue<bool>("Identity:SeedDemoAccounts"))return Results.NotFound();var result=await seeder.EnsureSeededAsync(ct); await identities.EnsureSeededAsync(ct); await workspace.SynchronizeProjectAsync(result.ProjectId,"system.workspace",ct); return Results.Ok(result); });

        app.MapGet("/api/programs", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet();
            return Results.Ok(await db.Programs.AsNoTracking().Where(p=>allowed==null||allowed.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct));
        });

        app.MapPost("/api/workspaces", async (CreateWorkspaceRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if(!http.UserAccount().IsAdministrator)return Results.Forbid();
            if (await db.Programs.AnyAsync(x => x.Code == request.ProgramCode.Trim().ToUpper(), ct))
                return Results.Conflict(new { error = "A program with that code already exists." });
            try
            {
                var program = new ProgramRecord(request.ProgramName, request.ProgramCode);
                var project = new ProjectRecord(program.Id, request.ProjectName, request.SoftwareProduct);
                var release = new SoftwareRelease(project.Id, request.InitialRelease, request.InitialReleaseIsReleased);
                db.AddRange(program, project, release);
                var actor = http.UserAccount(); db.ProgramMemberships.Add(new ProgramMembership(actor.Id, program.Id, ProgramRole.Administrator, actor.UserName, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/programs/{program.Id}", ApiMap.Workspace(program, project, release));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/workspaces", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet();
            var programs = await db.Programs.AsNoTracking().Where(x=>allowed==null||allowed.Contains(x.Id)).ToListAsync(ct);
            var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
            var releases = await db.Releases.AsNoTracking().ToListAsync(ct);
            return Results.Ok(programs.Select(program => new
            {
                program = new { program.Id, program.Name, program.Code },
                projects = projects.Where(x => x.ProgramId == program.Id).Select(project => new
                {
                    project = new { project.Id, project.Name, project.SoftwareProduct },
                    releases = releases.Where(x => x.ProjectId == project.Id).OrderBy(x => x.Version)
                        .Select(x => new { x.Id, x.Version, x.IsReleased, x.PredecessorReleaseId })
                })
            }));
        });

        app.MapGet("/api/context", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) => { var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet(); var programs=await db.Programs.AsNoTracking().Where(x=>allowed==null||allowed.Contains(x.Id)).ToListAsync(ct); var programIds=programs.Select(x=>x.Id).ToList(); var projects=await db.Projects.AsNoTracking().Where(x=>programIds.Contains(x.ProgramId)).ToListAsync(ct); return Results.Ok(new
        {
            programs, projects,
            releases = await db.Releases.AsNoTracking().Where(x=>projects.Select(p=>p.Id).Contains(x.ProjectId)).OrderBy(x => x.Version).ToListAsync(ct)
        }); });

        app.MapGet("/api/release-planning", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.PredecessorBaselineId, x.DisplayNumber, x.Name, state = x.State.ToString(), x.RequirementsMaterializedAt, selectionCount = x.Selections.Count }).ToListAsync(ct);
            var campaigns = await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.BaselineId, state = x.State.ToString(), x.Name }).ToListAsync(ct);
            var changes = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId)
                .GroupBy(x => new { x.TargetReleaseId, x.State }).Select(x => new { releaseId = x.Key.TargetReleaseId, state = x.Key.State.ToString(), count = x.Count() }).ToListAsync(ct);
            return Results.Ok(new { releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased, x.ReleasedAt, x.PredecessorReleaseId }), baselines, campaigns, changes });
        });

        app.MapPost("/api/releases", async (CreateReleaseRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            var version = request.Version.Trim();
            if (string.IsNullOrWhiteSpace(version)) return Results.BadRequest(new { error = "A release version is required." });
            var current = await db.Releases.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == request.ProjectId && !x.IsReleased, ct);
            if (current is not null) return Results.Conflict(new { error = $"Release {current.Version} is still in work. Release or formally close it before planning its successor." });
            if (await db.Releases.AnyAsync(x => x.ProjectId == request.ProjectId && x.Version.ToLower() == version.ToLower(), ct)) return Results.Conflict(new { error = $"Release {version} already exists in this project." });
            if (request.PredecessorReleaseId is not null)
            {
                var predecessor = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PredecessorReleaseId && x.ProjectId == request.ProjectId, ct);
                if (predecessor is null) return Results.BadRequest(new { error = "The predecessor release does not belong to this project." });
                if (!predecessor.IsReleased) return Results.BadRequest(new { error = "A successor release can only branch from a released product version." });
            }
            var release = new SoftwareRelease(request.ProjectId, version, false, request.PredecessorReleaseId); db.Releases.Add(release);
            var actor = http.UserAccount(); db.SecurityAuditEvents.Add(new SecurityAuditEvent("ReleaseCreated", actor.UserName, $"Release:{release.Id}", "Success", $"Created in-work release {version} from predecessor {request.PredecessorReleaseId?.ToString() ?? "none"}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct); return Results.Created($"/api/releases/{release.Id}", new { release.Id, release.Version, release.IsReleased, request.PredecessorReleaseId });
        });

        app.MapGet("/api/showcase/overview", async (Guid projectId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Version).ToListAsync(ct);
            var releasedIds = releases.Where(x => x.IsReleased).Select(x => x.Id).ToArray(); var activeIds = releases.Where(x => !x.IsReleased).Select(x => x.Id).ToArray();
            var requests = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
            var requirements = db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId);
            return Results.Ok(new {
                releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased }),
                systemRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.System, ct),
                highLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.HighLevel, ct),
                lowLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.LowLevel, ct),
                historicalScrs = await requests.CountAsync(x => x.Type == ChangeRequestType.System && releasedIds.Contains(x.TargetReleaseId), ct),
                historicalSwcrs = await requests.CountAsync(x => x.Type == ChangeRequestType.Software && releasedIds.Contains(x.TargetReleaseId), ct),
                activeRequests = await requests.CountAsync(x => activeIds.Contains(x.TargetReleaseId), ct),
                traceLinks = await db.RequirementTraces.CountAsync(x => x.ProjectId == projectId, ct),
                testProcedures = await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct),
                testExecutions = await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct),
                controlledDocuments = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct),
                softwareBuilds = await db.SoftwareBuilds.CountAsync(x => x.ProjectId == projectId, ct)
            });
        });

        app.MapGet("/api/dashboard", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (projectId is not null && !await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var allowedProjects = actor.IsAdministrator ? null : await db.Projects.AsNoTracking().Where(x => actor.Programs.Select(p => p.ProgramId).Contains(x.ProgramId)).Select(x => x.Id).ToListAsync(ct);
            var source = db.SystemChangeRequests.AsNoTracking().Where(x => (allowedProjects == null || allowedProjects.Contains(x.ProjectId)) && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId));
            // Deferred is counted across the project rather than within the selected release, and split by
            // discipline. A change request that has been put away is precisely one that is not part of the
            // build being worked on, so scoping the count to that build would hide the records this collection
            // exists to hold — and systems and software keep their own, because they are worked by different
            // people who should not be reading each other's shelved work.
            var everywhere = db.SystemChangeRequests.AsNoTracking().Where(x => (allowedProjects == null || allowedProjects.Contains(x.ProjectId)) && (projectId == null || x.ProjectId == projectId));
            return Results.Ok(new {
                totalScrs = await source.CountAsync(ct),
                draft = await source.CountAsync(x => x.State == ScrState.Draft, ct),
                inReview = await source.CountAsync(x => x.State == ScrState.InReview, ct),
                approved = await source.CountAsync(x => x.State == ScrState.Approved || x.State == ScrState.SelectedForBaseline, ct),
                deferredSystem = await everywhere.CountAsync(x => x.State == ScrState.Deferred && x.Type == ChangeRequestType.System, ct),
                deferredSoftware = await everywhere.CountAsync(x => x.State == ScrState.Deferred && x.Type == ChangeRequestType.Software, ct)
            });
        });

        app.MapGet("/api/directory", async (Guid? programId, Guid? projectId, string? search, int? limit, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var selectedProgram = programId ?? (projectId is null ? null : await db.Projects.Where(x=>x.Id==projectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(ct));
            if(selectedProgram is null)return Results.BadRequest(new{error="Choose a Program or Project directory context."});
            var actor=http.UserAccount();if(!actor.IsAdministrator&&!actor.Programs.Any(x=>x.ProgramId==selectedProgram.Value))return Results.Forbid();
            var members = await (from membership in db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == selectedProgram)
                                 join user in db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active) on membership.UserId equals user.Id
                                 select new { user.Id, user.UserName, user.DisplayName, user.Email, role = membership.Role.ToString() }).ToListAsync(ct);
            var people=members.GroupBy(x => new { x.Id, x.UserName, x.DisplayName, x.Email }).Select(x => {var roles=x.Select(r=>r.role).Order().ToList();return new{x.Key.Id,x.Key.UserName,x.Key.DisplayName,x.Key.Email,title=DirectoryTitles.For(x.Key.UserName,roles),roles};});
            if(!string.IsNullOrWhiteSpace(search)){var q=search.Trim();people=people.Where(x=>x.DisplayName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.UserName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Email.Contains(q,StringComparison.OrdinalIgnoreCase)||x.title.Contains(q,StringComparison.OrdinalIgnoreCase)||x.roles.Any(r=>r.Contains(q,StringComparison.OrdinalIgnoreCase)));}
            return Results.Ok(people.OrderBy(x=>x.DisplayName).Take(Math.Clamp(limit??50,1,200)));
        });

        app.MapGet("/api/my-work", async (Guid? projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
            var activeScrSteps = await (from step in db.ApprovalSteps.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ApprovalStepState.Active)
                                        join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                                        join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ScrId equals scr.Id
                                        where projectId == null || scr.ProjectId == projectId
                                        select new { id = scr.Id, type = "SCR approval", artifact = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, title = scr.Title, priority = "High", dueAt = cycle.StartedAt.AddDays(5), ageDays = (int)(now - cycle.StartedAt).TotalDays, route = "scr" }).ToListAsync(ct);
            activeScrSteps = activeScrSteps.OrderBy(x => x.dueAt).ToList();
            var releaseSteps = await (from step in db.ReleaseApprovals.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ReleaseApprovalState.Active)
                                      join campaign in db.ReleaseCampaigns.AsNoTracking() on step.CampaignId equals campaign.Id
                                      where projectId == null || campaign.ProjectId == projectId
                                      select new { id = campaign.Id, type = "Release approval", artifact = campaign.Name, title = "Authorize the controlled release package", priority = "Critical", dueAt = campaign.CreatedAt.AddDays(10), ageDays = (int)(now - campaign.CreatedAt).TotalDays, route = "release" }).ToListAsync(ct);
            releaseSteps = releaseSteps.OrderBy(x => x.dueAt).ToList();
            // Ordered after materialisation: SQLite cannot ORDER BY a DateTimeOffset, and this set is bounded by
            // the drafts one person authored, so sorting in memory costs nothing and works on every provider.
            var authoredDrafts = (await db.SystemChangeRequests.AsNoTracking().Where(x => x.AuthorId == actor.UserName && x.State == ScrState.Draft && (projectId == null || x.ProjectId == projectId))
                .Select(x => new { id = x.Id, type = "Draft to complete", artifact = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, title = x.Title, priority = "Normal", dueAt = x.UpdatedAt.AddDays(10), ageDays = (int)(now - x.UpdatedAt).TotalDays, route = "scr" }).ToListAsync(ct))
                .OrderByDescending(x => x.dueAt).ToList();
            var tasks = activeScrSteps.Cast<object>().Concat(releaseSteps).Concat(authoredDrafts).ToList();
            return Results.Ok(new { generatedAt = now, summary = new { total = tasks.Count, approvals = activeScrSteps.Count + releaseSteps.Count, overdue = activeScrSteps.Count(x => x.dueAt < now) + releaseSteps.Count(x => x.dueAt < now) + authoredDrafts.Count(x => x.dueAt < now), drafts = authoredDrafts.Count }, tasks });
        });

        // Bounded, Program-scoped universal search. Results are identifiers plus stable IDs;
        // the client owns the durable URL so every result can be opened in a new tab.
        app.MapGet("/api/search",async(Guid projectId,string query,int? limit,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            var q=(query??string.Empty).Trim().ToLowerInvariant();if(q.Length<2)return Results.Ok(new{query,items=Array.Empty<SearchResultDto>()});var take=Math.Clamp(limit??30,1,50);var items=new List<SearchResultDto>();
            items.AddRange(await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.BaseNumber.ToLower().Contains(q)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"change-request",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),x.Type==ChangeRequestType.Software?"software":"system",x.UpdatedAt)).ToListAsync(ct));
            items.AddRange(await db.ProblemReports.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.ReportNumber.ToLower().Contains(q)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q)||x.RootCause.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"problem-report",x.ReportNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),"assurance",x.UpdatedAt)).ToListAsync(ct));
            var requirementRows=await(from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)&&(artifact.BaseNumber.ToLower().Contains(q)||revision.Statement.ToLower().Contains(q)||revision.Rationale.ToLower().Contains(q)) select new{artifact.Id,artifact.BaseNumber,artifact.Level,revision.Revision,revision.Statement,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct);
            items.AddRange(requirementRows.Select(x=>new SearchResultDto(x.Id,"requirement",$"{x.BaseNumber}.{x.Revision:D2}",x.Statement,x.State.ToString(),x.Level==RequirementLevel.System?"system":"software",x.CreatedAt)));
            items.AddRange(await db.CandidateBaselines.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.BaseNumber.ToLower().Contains(q)||x.Name.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"baseline",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.BuildNumber.ToLower().Contains(q)||x.Description.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"build",x.BuildNumber,x.Description,x.State.ToString(),"software",x.RecordedAt)).ToListAsync(ct));
            items.AddRange(await db.TestProcedures.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.BaseNumber.ToLower().Contains(q)||x.Title.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"test-procedure",x.BaseNumber,x.Title,"Controlled",x.Level==TestProcedureLevel.System?"system":"software",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.ControlledDocuments.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.DocumentNumber.ToLower().Contains(q)||x.Title.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"document",x.DocumentNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,"Generated",x.Type==ControlledDocumentType.Sysrd?"system":"software",x.GeneratedAt)).ToListAsync(ct));
            items.AddRange(await db.ReleaseCampaigns.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Name.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release-campaign",x.Name,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.Releases.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Version.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release",x.Version,"Software release "+x.Version,x.IsReleased?"Released":"InWork","configuration",x.ReleasedAt)).ToListAsync(ct));
            var executionRows=await(from execution in db.TestExecutions.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id where procedure.BaseNumber.ToLower().Contains(q)||procedure.Title.ToLower().Contains(q)||execution.Determination.ToLower().Contains(q)||execution.EvidenceReference.ToLower().Contains(q) select new{execution.Id,identifier=procedure.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,procedure.Title,execution.Outcome,execution.RecordedAt,procedure.Level}).Take(take).ToListAsync(ct);
            items.AddRange(executionRows.Select(x=>new SearchResultDto(x.Id,"test-execution",x.identifier,$"{x.Title} result",x.Outcome.ToString(),x.Level==TestProcedureLevel.System?"system":"software",x.RecordedAt)));
            items.AddRange(await db.EvidenceRecords.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OriginalFileName.ToLower().Contains(q)||x.Sha256.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"evidence",x.OriginalFileName,x.Sha256,"Immutable","verification",x.UploadedAt)).ToListAsync(ct));
            var ordered=items.OrderByDescending(x=>x.Identifier.ToLowerInvariant().Contains(q)).ThenByDescending(x=>x.UpdatedAt).ThenBy(x=>x.Identifier).Take(take).ToList();return Results.Ok(new{query,items=ordered});
        });

        app.MapGet("/api/artifacts/{kind}/{id:guid}",async(string kind,Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            static Dictionary<string,object?> Details(params (string Key,object? Value)[] values)=>values.ToDictionary(x=>x.Key,x=>x.Value);
            var normalized=kind.Trim().ToLowerInvariant();
            if(normalized=="baseline")
            {var item=await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var members=await db.BaselineRequirements.CountAsync(x=>x.BaselineId==id,ct);var changes=await db.BaselineSelections.CountAsync(x=>x.BaselineId==id,ct);var related=(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.BaselineId==id).Select(x=>new RelatedArtifactDto("build",x.Id,x.BuildNumber,x.Description)).ToListAsync(ct));return Results.Ok(new{kind=normalized,item.Id,identifier=item.DisplayNumber,title=item.Name,state=item.State.ToString(),subtitle="Exact candidate baseline manifest",updatedAt=item.FrozenAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("requirementRevisions",members),("selectedChangeRequests",changes),("contentHash",item.ContentHash),("requirementsHash",item.RequirementsHash),("createdAt",item.CreatedAt)),related});}
            if(normalized=="build")
            {var item=await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.BuildNumber,title=item.Description,state=item.State.ToString(),subtitle="Immutable software build provenance",updatedAt=item.ReleasedAt??item.RecordedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("recordedBy",item.RecordedBy),("recordedAt",item.RecordedAt),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="document")
            {var item=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{item.DocumentNumber}.{item.Revision:D2}",title=item.Title,state="Generated",subtitle=$"{item.Type} controlled output",updatedAt=item.GeneratedAt,details=Details(("baseline",baseline.DisplayNumber),("artifactCount",item.ArtifactCount),("contentHash",item.ContentHash),("generatedAt",item.GeneratedAt)),related});}
            if(normalized=="test-procedure")
            {var item=await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var revisions=await db.TestProcedureRevisions.AsNoTracking().Where(x=>x.ProcedureId==id).OrderByDescending(x=>x.Revision).ToListAsync(ct);var latest=revisions.FirstOrDefault();var coverage=latest is null?0:await db.TestCoverage.CountAsync(x=>x.ProcedureRevisionId==latest.Id,ct);return Results.Ok(new{kind=normalized,item.Id,identifier=latest is null?item.BaseNumber:$"{item.BaseNumber}.{latest.Revision:D2}",title=item.Title,state=latest?.State.ToString()??"Draft",subtitle=$"{item.Level} verification procedure",updatedAt=latest?.CreatedAt??item.CreatedAt,details=Details(("owner",item.OwnerId),("revisionCount",revisions.Count),("coveredRequirements",coverage),("objective",latest?.Objective),("expectedResult",latest?.ExpectedResult)),related=Array.Empty<RelatedArtifactDto>()});}
            if(normalized=="release-campaign")
            {var item=await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.Name,title=item.Name,state=item.State.ToString(),subtitle="Governed release readiness and approval campaign",updatedAt=item.ReleasedAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("verificationBuildId",item.SoftwareBuildId),("releaseHash",item.ReleaseHash)),related});}
            if(normalized=="release")
            {var item=await db.Releases.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var related=await db.CandidateBaselines.AsNoTracking().Where(x=>x.ReleaseId==id).Select(x=>new RelatedArtifactDto("baseline",x.Id,x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name)).ToListAsync(ct);return Results.Ok(new{kind=normalized,item.Id,identifier=item.Version,title="Software release "+item.Version,state=item.IsReleased?"Released":"InWork",subtitle="Explicitly governed product-version record",updatedAt=item.ReleasedAt,details=Details(("predecessorReleaseId",item.PredecessorReleaseId),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="test-execution")
            {var item=await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var revision=await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x=>x.Id==item.ProcedureRevisionId,ct);var procedure=await db.TestProcedures.AsNoTracking().SingleAsync(x=>x.Id==revision.ProcedureId,ct);var related=new[]{new RelatedArtifactDto("test-procedure",procedure.Id,$"{procedure.BaseNumber}.{revision.Revision:D2}",procedure.Title)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{procedure.BaseNumber}.{revision.Revision:D2}",title=procedure.Title+" result",state=item.Outcome.ToString(),subtitle="Immutable attributable verification determination",updatedAt=item.RecordedAt,details=Details(("executedBy",item.ExecutedBy),("executedAt",item.ExecutedAt),("configuration",item.Configuration),("determination",item.Determination),("evidenceReference",item.EvidenceReference),("retestOfExecutionId",item.RetestOfExecutionId)),related});}
            if(normalized=="evidence")
            {var item=await db.EvidenceRecords.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var executionIds=await db.TestExecutionEvidence.AsNoTracking().Where(x=>x.EvidenceId==id).Select(x=>x.TestExecutionId).ToListAsync(ct);var related=await db.TestExecutions.AsNoTracking().Where(x=>executionIds.Contains(x.Id)).Select(x=>new RelatedArtifactDto("test-execution",x.Id,x.Id.ToString(),x.Determination)).ToListAsync(ct);return Results.Ok(new{kind=normalized,item.Id,identifier=item.OriginalFileName,title=item.OriginalFileName,state="Immutable",subtitle="Content-addressed verification evidence",updatedAt=item.UploadedAt,details=Details(("sha256",item.Sha256),("contentType",item.ContentType),("size",item.Size),("uploadedBy",item.UploadedBy),("uploadedAt",item.UploadedAt)),related});}
            if(normalized is "problem-report" or "problemreport" or "pr")
            {var item=await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var links=await db.ProblemReportLinks.AsNoTracking().Where(x=>x.ProblemReportId==id).ToListAsync(ct);var related=links.Select(x=>new RelatedArtifactDto(ProblemReportIntegrationMap.ArtifactKind(x.ArtifactType),x.ArtifactId,x.Relationship,ProblemReportIntegrationMap.ArtifactLabel(x.ArtifactType))).ToList();return Results.Ok(new{kind="problem-report",item.Id,identifier=item.DisplayNumber,title=item.Title,state=item.State.ToString(),subtitle="Controlled problem report with immutable lifecycle evidence",updatedAt=item.UpdatedAt,details=Details(("classification",item.Classification),("severity",item.Severity.ToString()),("priority",item.Priority.ToString()),("reportedBy",item.ReportedBy),("origin",item.Origin),("affectedConfiguration",item.AffectedConfiguration),("rootCause",item.RootCause),("correctiveAction",item.CorrectiveAction),("disposition",item.Disposition?.ToString()),("releaseBlocker",item.IsReleaseBlocker),("waiver",item.WaiverRationale),("verificationExecutionId",item.ResolutionVerificationExecutionId)),related});}
            return Results.NotFound();
        });

        // Exclusive controlled editing for SCR/SWCR Drafts. The pre-existing enterprise
        // merge endpoints remain available for artifacts configured for optimistic editing.
    }
}
