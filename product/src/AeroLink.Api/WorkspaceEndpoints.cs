using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
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

        // The practice Program is seeded here as well as at boot, because a demonstration database is seeded
        // at boot while the journeys seed through this endpoint. Before the identities, which grant the demo
        // directory membership of every Program that exists by then.
        // The procedure documents are ensured last, after every seeder that can create a Project or a
        // procedure: seeding through this endpoint happens long after the startup bootstrap ran, so nothing
        // it creates would be written into a document until the next restart.
        app.MapPost("/api/showcase/seed", async (HttpContext http,FmsShowcaseSeeder seeder, ImportPracticeSeeder practice, IdentitySeeder identities, ManagedDocumentShowcaseSeeder documents, EnterpriseRequirementsService workspace, TestProcedureDocumentBootstrap procedureDocuments, IConfiguration configuration, CancellationToken ct) => {if(!http.UserAccount().IsAdministrator)return Results.Forbid();if(!configuration.GetValue<bool>("Identity:SeedDemoAccounts"))return Results.NotFound();var result=await seeder.EnsureSeededAsync(ct); await practice.EnsureSeededAsync(ct); await identities.EnsureSeededAsync(ct); await workspace.SynchronizeProjectAsync(result.ProjectId,"system.workspace",ct); await documents.EnsureSeededAsync(ct); await procedureDocuments.EnsureAllAsync(ct); return Results.Ok(result); });

        // What the showcase upgrade has and has not applied to this installation, and whether the invariants
        // it is meant to guarantee actually hold. An upgrade that reports success is not the same as a
        // database that is correct, so this reports the two separately and an operator can read both.
        app.MapGet("/api/showcase/upgrade-state", async (HttpContext http, AeroLinkDbContext db, FmsShowcaseSeeder seeder, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == FmsShowcaseSeeder.ProgramCode, ct);
            if (program is null) return Results.Ok(new { seeded = false, steps = Array.Empty<object>(), invariants = Array.Empty<object>() });
            var steps = (await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == program.Id).ToListAsync(ct))
                .OrderBy(x => x.AppliedAt).Select(x => new { x.StepKey, x.Detail, x.AppliedAt }).ToList();
            var invariants = await seeder.CheckInvariantsAsync(program.Id, ct);
            return Results.Ok(new { seeded = true, programId = program.Id, steps, healthy = invariants.All(x => x.Holds), invariants });
        });

        // The repair command for an existing local showcase: apply any outstanding steps and report what
        // changed. Safe to run repeatedly, and safe to run again after an interrupted attempt.
        app.MapPost("/api/showcase/upgrade", async (HttpContext http, AeroLinkDbContext db, FmsShowcaseSeeder seeder, TestProcedureDocumentBootstrap procedureDocuments, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == FmsShowcaseSeeder.ProgramCode, ct);
            if (program is null) return Results.NotFound(new { error = "No showcase Program is installed.", code = "showcase_absent" });
            var applied = await seeder.UpgradeAsync(program.Id, ct);
            // An upgrade step can add procedures, and a procedure in no document is invisible to the rail.
            await procedureDocuments.EnsureAllAsync(ct);
            var invariants = await seeder.CheckInvariantsAsync(program.Id, ct);
            return Results.Ok(new { applied, healthy = invariants.All(x => x.Holds), invariants });
        });

        app.MapGet("/api/programs", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet();
            return Results.Ok(await db.Programs.AsNoTracking().Where(p=>allowed==null||allowed.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct));
        });

        app.MapPost("/api/workspaces", async (CreateWorkspaceRequest request, HttpContext http, AeroLinkDbContext db, TestProcedureDocumentBootstrap procedureDocuments, CancellationToken ct) =>
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
                // Every Project has its three test procedure documents from the moment it exists. The startup
                // bootstrap backfills projects created before this existed; it cannot help a project created
                // after it ran, and the Explorer's document rail would be empty until the next restart.
                await procedureDocuments.EnsureForProjectAsync(project.Id, ct);
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

        app.MapGet("/api/build-context", async (Guid projectId, Guid releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
            if (release is null) return Results.NotFound(new { error = "The selected build does not exist in this project." });
            var effectiveBaselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
            var effectiveBaseline = effectiveBaselineId is null
                ? null
                : await (from baseline in db.CandidateBaselines.AsNoTracking()
                         join origin in db.Releases.AsNoTracking() on baseline.ReleaseId equals origin.Id
                         where baseline.Id == effectiveBaselineId
                         select new { baseline.Id, baseline.BaseNumber, baseline.Revision, baseline.Name, baseline.RequirementsMaterializedAt, ReleaseId = origin.Id, ReleaseVersion = origin.Version }).SingleAsync(ct);
            return Results.Ok(new
            {
                projectId,
                releaseId = release.Id,
                release.Version,
                release.IsReleased,
                release.PredecessorReleaseId,
                effectiveBaselineId,
                effectiveBaseline,
                inheritedBaseline = effectiveBaseline is not null && effectiveBaseline.ReleaseId != release.Id
            });
        });

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

        app.MapGet("/api/showcase/overview", async (Guid projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Version).ToListAsync(ct);
            var selectedReleaseIds = releaseId is null ? releases.Select(x => x.Id).ToArray() : [releaseId.Value];
            var requests = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.TargetReleaseId));
            var effectiveBaselineId = releaseId is null ? null : await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId.Value, ct);
            var revisionIds = effectiveBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == effectiveBaselineId).Select(x => x.RevisionId).ToListAsync(ct);
            var artifactIds = effectiveBaselineId is null
                ? await db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct)
                : await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == effectiveBaselineId).Select(x => x.ArtifactId).ToListAsync(ct);
            var requirements = db.Requirements.AsNoTracking().Where(x => artifactIds.Contains(x.Id));
            var procedureEffectivity = releaseId is null
                ? null
                : await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
            var procedureIds = procedureEffectivity is not null
                ? procedureEffectivity.ProcedureIds.ToList()
                : await (from coverage in db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.RequirementRevisionId))
                         join procedureRevision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals procedureRevision.Id
                         select procedureRevision.ProcedureId).Distinct().ToListAsync(ct);
            var executionBuildIds = await db.SoftwareBuilds.AsNoTracking().Where(x => selectedReleaseIds.Contains(x.ReleaseId)).Select(x => x.Id).ToListAsync(ct);
            return Results.Ok(new {
                releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased }),
                systemRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.System, ct),
                highLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.HighLevel, ct),
                lowLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.LowLevel, ct),
                historicalScrs = await requests.CountAsync(x => x.Type == ChangeRequestType.System, ct),
                historicalSwcrs = await requests.CountAsync(x => x.Type == ChangeRequestType.Software, ct),
                activeRequests = await requests.CountAsync(x => x.State != ChangeRequestState.Deferred, ct),
                traceLinks = await db.RequirementTraces.CountAsync(x => revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId), ct),
                testProcedures = await db.TestProcedures.CountAsync(x => procedureIds.Contains(x.Id), ct),
                testExecutions = await db.TestExecutions.CountAsync(x => x.SoftwareBuildId != null && executionBuildIds.Contains(x.SoftwareBuildId.Value), ct),
                controlledDocuments = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.ReleaseId), ct),
                softwareBuilds = await db.SoftwareBuilds.CountAsync(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.ReleaseId), ct)
            });
        });

        app.MapGet("/api/dashboard", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (projectId is not null && !await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var allowedProjects = actor.IsAdministrator ? null : await db.Projects.AsNoTracking().Where(x => actor.Programs.Select(p => p.ProgramId).Contains(x.ProgramId)).Select(x => x.Id).ToListAsync(ct);
            var source = db.SystemChangeRequests.AsNoTracking().Where(x => (allowedProjects == null || allowedProjects.Contains(x.ProjectId)) && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId));
            var requests = await source.Select(x => new { x.Id, x.Type, x.State }).ToListAsync(ct);
            var requestIds = requests.Select(x => x.Id).ToList();
            var impacts = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => requestIds.Contains(x.ChangeRequestId))
                .Select(x => new { x.ChangeRequestId, x.RequirementChangeId, x.ProcedureId, x.State })
                .ToListAsync(ct);
            var requirementChangeIds = impacts.Where(x => x.RequirementChangeId is not null).Select(x => x.RequirementChangeId!.Value).ToList();
            var requirementLevels = await db.RequirementChanges.AsNoTracking().Where(x => requirementChangeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
            var procedureIds = impacts.Where(x => x.ProcedureId is not null).Select(x => x.ProcedureId!.Value).ToList();
            var procedureLevels = await db.TestProcedures.AsNoTracking().Where(x => procedureIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
            ChangeDashboardSummary ChangeSummary(ChangeRequestType type)
            {
                var rows = requests.Where(x => x.Type == type).ToList();
                return new(rows.Count, rows.Count(x => x.State == ChangeRequestState.Draft), rows.Count(x => x.State == ChangeRequestState.InReview),
                    rows.Count(x => x.State is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline), rows.Count(x => x.State == ChangeRequestState.Deferred));
            }
            VerificationDashboardSummary VerificationSummary(string area)
            {
                var areaRequestIds = requests.Where(x => area == "System" ? x.Type == ChangeRequestType.System : x.Type == ChangeRequestType.Software).Select(x => x.Id).ToHashSet();
                var rows = impacts.Where(x =>
                {
                    if (!areaRequestIds.Contains(x.ChangeRequestId)) return false;
                    if (area == "System") return true;
                    if (x.RequirementChangeId is Guid requirementChangeId && requirementLevels.TryGetValue(requirementChangeId, out var requirementLevel))
                        return area == "HLR" ? requirementLevel == RequirementLevel.HighLevel : requirementLevel == RequirementLevel.LowLevel;
                    if (x.ProcedureId is Guid procedureId && procedureLevels.TryGetValue(procedureId, out var procedureLevel))
                        return area == "HLR" ? procedureLevel == TestProcedureLevel.HighLevel : procedureLevel == TestProcedureLevel.LowLevel;
                    return false;
                }).ToList();
                var current = rows.Where(x => x.State != VerificationImpactState.Superseded).ToList();
                var currentGrouped = current.GroupBy(x => x.ChangeRequestId).ToList();
                return new(currentGrouped.Count, currentGrouped.Count(group => group.All(x => x.State == VerificationImpactState.Resolved)),
                    current.Count(x => x.State != VerificationImpactState.Resolved), current.Count(x => x.State == VerificationImpactState.Resolved));
            }
            return Results.Ok(new {
                system = ChangeSummary(ChangeRequestType.System),
                software = ChangeSummary(ChangeRequestType.Software),
                verification = new {
                    system = VerificationSummary("System"),
                    hlr = VerificationSummary("HLR"),
                    llr = VerificationSummary("LLR")
                }
            });
        });

        app.MapGet("/api/directory", async (Guid? programId, Guid? projectId, string? search, int? limit, string? authority, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var selectedProgram = programId ?? (projectId is null ? null : await db.Projects.Where(x=>x.Id==projectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(ct));
            if(selectedProgram is null)return Results.BadRequest(new{error="Choose a Program or Project directory context."});
            if(!string.IsNullOrWhiteSpace(authority)
                && !string.Equals(authority,ProblemReportOwnerAuthority.DirectoryAuthority,StringComparison.Ordinal)
                && !string.Equals(authority,ManagedDocumentAssignmentPolicy.DirectoryAuthority,StringComparison.Ordinal))
                return Results.BadRequest(new{error="The requested directory authority is not supported.",code="directory_authority_unsupported"});
            if(string.Equals(authority,ManagedDocumentAssignmentPolicy.DirectoryAuthority,StringComparison.Ordinal)&&projectId is null)
                return Results.BadRequest(new{error="Managed-document author eligibility requires a Project directory context.",code="project_context_required"});
            var actor=http.UserAccount();if(!actor.IsAdministrator&&!actor.Programs.Any(x=>x.ProgramId==selectedProgram.Value))return Results.Forbid();
            var members = await (from membership in db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == selectedProgram && x.EndedAt == null)
                                 join user in db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active) on membership.UserId equals user.Id
                                 select new { user.Id, user.UserName, user.DisplayName, user.Email, role = membership.Role }).ToListAsync(ct);
            HashSet<string>? managedDocumentAuthors = null;
            if (string.Equals(authority, ManagedDocumentAssignmentPolicy.DirectoryAuthority, StringComparison.Ordinal))
            {
                managedDocumentAuthors = await ManagedDocumentAssignmentPolicy.EligibleUserNamesAsync(db, identity, projectId!.Value, DateTimeOffset.UtcNow, ct);
                var existingIds = members.Select(x => x.Id).ToHashSet();
                var delegated = await (from delegation in db.RoleDelegations.AsNoTracking().Where(x => x.ProgramId == selectedProgram && x.Role == ProgramRole.Engineer && x.RevokedAt == null)
                                       join user in db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active) on delegation.DelegateUserId equals user.Id
                                       where !existingIds.Contains(user.Id)
                                       select new { user.Id, user.UserName, user.DisplayName, user.Email, role = ProgramRole.Engineer }).ToListAsync(ct);
                members.AddRange(delegated.Where(x => managedDocumentAuthors.Contains(x.UserName)));
            }
            var people=members.GroupBy(x => new { x.Id, x.UserName, x.DisplayName, x.Email })
                .Where(x=>string.IsNullOrWhiteSpace(authority)
                    || string.Equals(authority,ProblemReportOwnerAuthority.DirectoryAuthority,StringComparison.Ordinal)&&ProblemReportOwnerAuthority.IsEligible(x.Select(r=>r.role))
                    || string.Equals(authority,ManagedDocumentAssignmentPolicy.DirectoryAuthority,StringComparison.Ordinal)&&managedDocumentAuthors!.Contains(x.Key.UserName))
                .Select(x => {var roles=x.Select(r=>r.role.ToString()).Order().ToList();return new{x.Key.Id,x.Key.UserName,x.Key.DisplayName,x.Key.Email,title=DirectoryTitles.For(x.Key.UserName,roles),roles};});
            var q=search?.Trim()??"";
            if(q.Length>0)people=people.Where(x=>x.DisplayName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.UserName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Email.Contains(q,StringComparison.OrdinalIgnoreCase)||x.title.Contains(q,StringComparison.OrdinalIgnoreCase)||x.roles.Any(r=>r.Contains(q,StringComparison.OrdinalIgnoreCase)));
            // Exact account/display-name matches lead the suggestions. Handles remain hidden in the picker,
            // but typing a known person must not let ten same-titled generated accounts crowd them out.
            return Results.Ok(people.OrderBy(x=>q.Length>0&&!string.Equals(x.UserName,q,StringComparison.OrdinalIgnoreCase)&&!string.Equals(x.DisplayName,q,StringComparison.OrdinalIgnoreCase))
                .ThenBy(x=>q.Length>0&&!x.DisplayName.StartsWith(q,StringComparison.OrdinalIgnoreCase))
                .ThenBy(x=>x.DisplayName).Take(Math.Clamp(limit??50,1,200)));
        });

        app.MapGet("/api/my-work", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
            var activeScrSteps = await (from step in db.ApprovalSteps.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ApprovalStepState.Active)
                                        join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                                        join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals scr.Id
                                        where (projectId == null || scr.ProjectId == projectId) && (releaseId == null || scr.TargetReleaseId == releaseId)
                                        select new { id = scr.Id, type = "Change request approval", artifact = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, title = scr.Title, priority = "High", dueAt = cycle.StartedAt.AddDays(5), ageDays = (int)(now - cycle.StartedAt).TotalDays, route = "scr", discipline = scr.Type == ChangeRequestType.Software ? "software" : "system" }).ToListAsync(ct);
            activeScrSteps = activeScrSteps.OrderBy(x => x.dueAt).ToList();
            var releaseSteps = await (from step in db.ReleaseApprovals.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ReleaseApprovalState.Active)
                                      join campaign in db.ReleaseCampaigns.AsNoTracking() on step.CampaignId equals campaign.Id
                                      where (projectId == null || campaign.ProjectId == projectId) && (releaseId == null || campaign.ReleaseId == releaseId)
                                      select new { id = campaign.Id, type = "Release approval", artifact = campaign.Name, title = "Authorize the controlled release package", priority = "Critical", dueAt = campaign.CreatedAt.AddDays(10), ageDays = (int)(now - campaign.CreatedAt).TotalDays, route = "release" }).ToListAsync(ct);
            releaseSteps = releaseSteps.OrderBy(x => x.dueAt).ToList();
            // Ordered after materialisation: SQLite cannot ORDER BY a DateTimeOffset, and this set is bounded by
            // the drafts one person authored, so sorting in memory costs nothing and works on every provider.
            var authoredDrafts = (await db.SystemChangeRequests.AsNoTracking().Where(x => x.AuthorId == actor.UserName && x.State == ChangeRequestState.Draft && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId))
                .Select(x => new { id = x.Id, type = "Draft to complete", artifact = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, title = x.Title, priority = "Normal", dueAt = x.UpdatedAt.AddDays(10), ageDays = (int)(now - x.UpdatedAt).TotalDays, route = "scr", discipline = x.Type == ChangeRequestType.Software ? "software" : "system" }).ToListAsync(ct))
                .OrderBy(x => x.dueAt).ToList();
            // Comments a reviewer published while the cycle is still open. Nothing is sent for these: the
            // author cannot edit the package until the cycle closes, so an email would be noise followed
            // minutes later by the one that actually matters. My Work notices them when it is opened, which
            // is where this author already looks for what is on their plate.
            var commentsToRead = (await (from comment in db.ReviewComments.AsNoTracking()
                                             .Where(x => x.State == ReviewCommentState.Published)
                                         join cycle in db.ReviewCycles.AsNoTracking() on comment.ReviewCycleId equals cycle.Id
                                         join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals scr.Id
                                         where scr.AuthorId == actor.UserName && cycle.State == ReviewCycleState.Active
                                             && (projectId == null || scr.ProjectId == projectId)
                                             && (releaseId == null || scr.TargetReleaseId == releaseId)
                                         select new { scr.Id, scr.BaseNumber, scr.Revision, scr.Title, scr.Type, cycle.StartedAt })
                    .ToListAsync(ct))
                .GroupBy(x => x.Id)
                .Select(g => new
                {
                    id = g.Key,
                    type = "Reviewer comments",
                    artifact = g.First().BaseNumber + "." + (g.First().Revision < 10 ? "0" : "") + g.First().Revision,
                    title = g.Count() == 1 ? "A reviewer commented on your package" : $"{g.Count()} reviewers' comments on your package",
                    priority = "Normal",
                    dueAt = g.First().StartedAt.AddDays(5),
                    ageDays = (int)(now - g.First().StartedAt).TotalDays,
                    route = "scr",
                    discipline = g.First().Type == ChangeRequestType.Software ? "software" : "system",
                })
                .OrderBy(x => x.dueAt).ToList();
            var assignedTestWork = (await db.TestChangeReviews.AsNoTracking().Where(x =>
                    x.AssignedEngineerId == actor.UserName && x.State == TestChangeReviewState.Draft
                    && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.ReleaseId == releaseId))
                .ToListAsync(ct)).OrderBy(x => x.UpdatedAt).Select(x => new
                {
                    id = x.Id,
                    type = "Test change request",
                    artifact = x.DisplayNumber,
                    title = "Resolve verification impact decisions",
                    priority = "High",
                    dueAt = x.UpdatedAt.AddDays(5),
                    ageDays = (int)(now - x.UpdatedAt).TotalDays,
                    route = "testingCoverage",
                    discipline = x.Discipline.ToString()
                }).ToList();
            // Project documents are intentionally independent of releaseId. Their actionable
            // description is the formal revision scope, never the most recent check-in note.
            var managedOwnerWork = (await (from revision in db.ManagedDocumentRevisions.AsNoTracking()
                                            join document in db.ManagedDocuments.AsNoTracking() on revision.DocumentId equals document.Id
                                            where revision.ResponsibleOwnerId == actor.UserName
                                                && (revision.State == ManagedDocumentState.Draft || revision.State == ManagedDocumentState.Returned)
                                                && (projectId == null || document.ProjectId == projectId)
                                            select new { id = document.Id, type = "Project document to complete", artifact = document.DocumentNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, title = revision.FormalChangeSummary, priority = revision.State == ManagedDocumentState.Returned ? "High" : "Normal", dueAt = revision.UpdatedAt.AddDays(10), ageDays = (int)(now - revision.UpdatedAt).TotalDays, route = "managedDocuments", discipline = "project" }).ToListAsync(ct)).OrderBy(x => x.dueAt).ToList();
            var managedReviewWork = (await (from step in db.ManagedDocumentReviewSteps.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ManagedDocumentReviewStepState.Active)
                                             join revision in db.ManagedDocumentRevisions.AsNoTracking() on step.RevisionId equals revision.Id
                                             join document in db.ManagedDocuments.AsNoTracking() on revision.DocumentId equals document.Id
                                             where projectId == null || document.ProjectId == projectId
                                             select new { id = document.Id, type = "Project document review", artifact = document.DocumentNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, title = revision.FormalChangeSummary, priority = "High", dueAt = revision.SubmittedAt!.Value.AddDays(5), ageDays = (int)(now - revision.SubmittedAt!.Value).TotalDays, route = "managedDocuments", discipline = "project" }).ToListAsync(ct)).OrderBy(x => x.dueAt).ToList();
            var managedRecoveryWork = new List<object>();
            if (projectId is not null && await http.HasProjectRoleAsync(db, identity, projectId.Value, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.ProjectEngineeringLead))
            {
                var eligibleUsers = await ManagedDocumentAssignmentPolicy.EligibleUserNamesAsync(db, identity, projectId.Value, now, ct);
                var recoveryCandidates = await (from revision in db.ManagedDocumentRevisions.AsNoTracking()
                                                join document in db.ManagedDocuments.AsNoTracking() on revision.DocumentId equals document.Id
                                                where document.ProjectId == projectId.Value && (revision.State == ManagedDocumentState.Draft || revision.State == ManagedDocumentState.Returned)
                                                select new { id = document.Id, owner = revision.ResponsibleOwnerId, artifact = document.DocumentNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, dueAt = revision.UpdatedAt }).ToListAsync(ct);
                managedRecoveryWork = recoveryCandidates.Where(x => !eligibleUsers.Contains(x.owner)).Select(x => (object)new { x.id, type = "Project document owner recovery", x.artifact, title = "Reassign disabled or departed owner: " + x.owner, priority = "High", x.dueAt, ageDays = (int)(now - x.dueAt).TotalDays, route = "managedDocuments", discipline = "project" }).ToList();
            }
            // An exclusive connector checkout is recoverable in-work evidence for its holder, and it is
            // Project-wide exactly like the document it edits: the task never depends on a selected build.
            var managedCheckoutWork = (await (from session in db.ArtifactEditSessions.AsNoTracking()
                                              join document in db.ManagedDocuments.AsNoTracking() on session.ArtifactId equals document.Id
                                              join revision in db.ManagedDocumentRevisions.AsNoTracking() on session.RevisionId!.Value equals revision.Id
                                              where session.ArtifactType == "ManagedDocument" && session.IsExclusive
                                                  && session.State == EditSessionState.Active && session.UserName == actor.UserName
                                                  && (projectId == null || document.ProjectId == projectId)
                                              select new { id = document.Id, type = "Project document checkout", artifact = document.DocumentNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, title = "Recover the active desktop checkout", priority = "Normal", dueAt = session.ExpiresAt, ageDays = (int)(now - session.OpenedAt).TotalDays, route = "managedDocuments", discipline = "project" }).ToListAsync(ct)).OrderBy(x => x.dueAt).ToList();
            var tasks = activeScrSteps.Cast<object>().Concat(releaseSteps).Concat(authoredDrafts).Concat(commentsToRead).Concat(assignedTestWork).Concat(managedOwnerWork).Concat(managedReviewWork).Concat(managedRecoveryWork).Concat(managedCheckoutWork).ToList();
            return Results.Ok(new { generatedAt = now, summary = new { total = tasks.Count, approvals = activeScrSteps.Count + releaseSteps.Count + managedReviewWork.Count, overdue = activeScrSteps.Count(x => x.dueAt < now) + releaseSteps.Count(x => x.dueAt < now) + authoredDrafts.Count(x => x.dueAt < now) + managedOwnerWork.Count(x => x.dueAt < now) + managedReviewWork.Count(x => x.dueAt < now) + managedRecoveryWork.Count, drafts = authoredDrafts.Count + managedOwnerWork.Count }, tasks });
        });

        // Notifications and Jira emitted paths such as /systems/change-requests/{id}. The client router
        // accepts application routes only beneath /programs/{p}/projects/{pr}/releases/{r}/, so a recipient
        // received a valid-looking link to a controlled record and landed on Not Found. One resolver owns
        // that mapping now, rather than every emitter holding a copy of the URL shape.
        //
        // Deliberately not under /api: this is opened from a mail client, and the session gate answers an
        // unauthenticated /api request with a JSON 401. Missing, unauthorized and unauthenticated all end at
        // the workspace root, so probing cannot distinguish an artifact that exists from one that does not.
        app.MapGet("/open/{kind}/{id:guid}", async (string kind, Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var user = await identity.ResolveAsync(http.Request.Cookies[IdentityService.CookieName], DateTimeOffset.UtcNow, ct);
            if (user is null) return Results.Redirect("/");
            http.Items["AeroLink.User"] = user;

            var normalized = kind.Trim().ToLowerInvariant();
            Guid? projectId = null, releaseId = null; var tail = ""; var projectWide = false;
            Guid? reviewedDocumentId = null;
            switch (normalized)
            {
                case "scr" or "swcr" or "change-request":
                {
                    var record = await db.SystemChangeRequests.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.TargetReleaseId, x.Type }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId; releaseId = record.TargetReleaseId;
                        tail = $"/{(record.Type == ChangeRequestType.Software ? "software" : "systems")}/change-requests/{id}";
                    }
                    break;
                }
                case "requirement":
                {
                    var record = await db.Requirements.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId;
                        tail = $"/requirements/{id}?discipline={(record.Level == RequirementLevel.System ? "system" : "software")}";
                    }
                    break;
                }
                case "procedure":
                {
                    var record = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId;
                        tail = record.Level == TestProcedureLevel.System ? "/system-verification" : "/software-verification";
                    }
                    break;
                }
                case "baseline":
                {
                    var record = await db.CandidateBaselines.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; releaseId = record.ReleaseId; tail = "/baselines"; }
                    break;
                }
                case "document":
                {
                    var record = await db.ControlledDocuments.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; releaseId = record.ReleaseId; tail = "/traceability"; }
                    break;
                }
                case "problem-report":
                {
                    var record = await db.ProblemReports.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; tail = "/problem-reports"; }
                    break;
                }
                case "test-change-request":
                {
                    // Test change requests live under the verification branch that owns them, and the three
                    // branches are separate pages rather than one page with a filter. Resolving to the wrong
                    // branch would land the approver on a register that does not contain their package.
                    var record = await db.TestChangeReviews.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.ReleaseId, x.Discipline }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId; releaseId = record.ReleaseId;
                        var branch = record.Discipline switch
                        {
                            TestChangeReviewDiscipline.HighLevelSoftware => "software-verification/hlr",
                            TestChangeReviewDiscipline.LowLevelSoftware => "software-verification/llr",
                            _ => "system-verification",
                        };
                        tail = $"/{branch}/change-requests/{id}";
                    }
                    break;
                }
                case "managed-document":
                {
                    // A managed document is Project-wide: the resolver must never fall back to guessing a
                    // software build. The identifier may be a document or a specific formal revision; both
                    // resolve to the canonical Project-level Documentation Center record.
                    var document = await db.ManagedDocuments.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.Id, x.ProjectId }).SingleOrDefaultAsync(ct);
                    if (document is not null)
                    {
                        projectId = document.ProjectId;
                        tail = $"/documentation-center/{document.Id}";
                    }
                    else
                    {
                        var revision = await (from item in db.ManagedDocumentRevisions.AsNoTracking()
                                              join owner in db.ManagedDocuments.AsNoTracking() on item.DocumentId equals owner.Id
                                              where item.Id == id
                                              select new { owner.Id, owner.ProjectId }).SingleOrDefaultAsync(ct);
                        if (revision is not null)
                        {
                            projectId = revision.ProjectId;
                            tail = $"/documentation-center/{revision.Id}";
                        }
                    }
                    if (projectId is not null) projectWide = true;
                    // Both spellings of the identifier resolve to the same document, and the closed-review
                    // check below needs that document rather than whichever id happened to be followed.
                    if (projectId is not null)
                        reviewedDocumentId = document?.Id ?? await db.ManagedDocumentRevisions.AsNoTracking()
                            .Where(x => x.Id == id).Select(x => (Guid?)x.DocumentId).SingleOrDefaultAsync(ct);
                    break;
                }
            }

            if (projectId is null) return Results.Redirect("/");
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Redirect("/");

            // Somebody following a review link after the review ended gets told so rather than left to work
            // out why the decision controls are missing. Asked only after the access check above, and only
            // of someone who actually held a step: the resolver's standing promise is that missing,
            // unauthorized and unauthenticated all end in the same place, so this must never be the thing
            // that reveals a record exists to a person who could not already see it.
            if (normalized is "scr" or "swcr" or "change-request")
            {
                var mySteps = from step in db.ApprovalSteps.AsNoTracking()
                              join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                              where cycle.ChangeRequestId == id && step.ApproverId == user.UserName
                              select cycle.State;
                var states = await mySteps.ToListAsync(ct);
                // An open cycle wins: they still have a live decision, so there is nothing to explain.
                if (states.Count > 0 && states.All(x => x != ReviewCycleState.Active))
                    tail += tail.Contains('?') ? "&reviewEnded=1" : "?reviewEnded=1";
            }

            // The same question for a document review, over a different aggregate. A managed document keeps
            // its steps on the revision with an integer round rather than on a ReviewCycle, so "still open"
            // is the revision's own state.
            if (reviewedDocumentId is Guid documentId)
            {
                var myDocumentStates = from step in db.ManagedDocumentReviewSteps.AsNoTracking()
                                       join revision in db.ManagedDocumentRevisions.AsNoTracking() on step.RevisionId equals revision.Id
                                       where revision.DocumentId == documentId && step.ApproverId == user.UserName
                                       select revision.State;
                var documentStates = await myDocumentStates.ToListAsync(ct);
                if (documentStates.Count > 0 && documentStates.All(x => x != ManagedDocumentState.InReview))
                    tail += tail.Contains('?') ? "&reviewEnded=1" : "?reviewEnded=1";
            }

            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
            if (programId is null) return Results.Redirect("/");
            if (projectWide) return Results.Redirect($"/programs/{programId}/projects/{projectId}{tail}");
            // A record that does not carry a release of its own opens in the one being worked, which is where
            // the reader would have gone looking for it anyway.
            releaseId ??= await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.IsReleased).ThenByDescending(x => x.Version)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (releaseId is null) return Results.Redirect("/");
            return Results.Redirect($"/programs/{programId}/projects/{projectId}/releases/{releaseId}{tail}");
        });

        // Bounded, Program-scoped universal search. Results are identifiers plus stable IDs;
        // the client owns the durable URL so every result can be opened in a new tab.
        app.MapGet("/api/search",async(Guid projectId,Guid? releaseId,string query,int? limit,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            var q=(query??string.Empty).Trim().ToLowerInvariant();if(q.Length<2)return Results.Ok(new{query,items=Array.Empty<SearchResultDto>()});var identifierQ=q.Length>3&&q[^3]=='.'&&char.IsDigit(q[^2])&&char.IsDigit(q[^1])?q[..^3]:q;var take=Math.Clamp(limit??30,1,50);var items=new List<SearchResultDto>();
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,projectId,releaseId.Value,ct);
            var procedureEffectivity=releaseId is null?null:await TestProcedureEffectivity.ForReleaseAsync(db,projectId,releaseId.Value,ct);
            items.AddRange(await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.TargetReleaseId==releaseId)&&(x.BaseNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"change-request",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),x.Type==ChangeRequestType.Software?"software":"system",x.UpdatedAt)).ToListAsync(ct));
            items.AddRange(await db.ProblemReports.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||db.ProblemReportLinks.Any(link=>link.ProblemReportId==x.Id&&link.ArtifactType=="Release"&&link.ArtifactId==releaseId))&&(x.ReportNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q)||x.RootCause.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"problem-report",x.ReportNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),"assurance",x.UpdatedAt)).ToListAsync(ct));
            var managedStateQuery = Enum.TryParse<ManagedDocumentState>(q, ignoreCase: true, out var parsedManagedState)
                ? parsedManagedState : (ManagedDocumentState?)null;
            var hasManagedStateFilter = managedStateQuery.HasValue;
            var managedStateValue = managedStateQuery ?? default;
            int? exactManagedRevision = null;
            if (q.Length > 3 && q[^3] == '.' && char.IsDigit(q[^2]) && char.IsDigit(q[^1])
                && int.TryParse(q[^2..], out var managedRevision))
                exactManagedRevision = managedRevision;
            var managedCandidates = await (from document in db.ManagedDocuments.AsNoTracking().Where(x=>x.ProjectId==projectId)
                                           join revision in db.ManagedDocumentRevisions.AsNoTracking() on document.Id equals revision.DocumentId
                                           where (document.DocumentNumber.ToLower().Contains(identifierQ)
                                                      ||document.Title.ToLower().Contains(q)
                                                      ||document.Acronym.ToLower().Contains(q)
                                                      ||document.DocumentType.ToLower().Contains(q)
                                                      ||document.StewardId.ToLower().Contains(q)
                                                      ||revision.FormalChangeSummary.ToLower().Contains(q)
                                                      ||revision.ResponsibleOwnerId.ToLower().Contains(q)
                                                      ||(hasManagedStateFilter && revision.State == managedStateValue))
                                               && (exactManagedRevision == null || revision.Revision == exactManagedRevision.Value)
                                           select new SearchResultDto(document.Id,"managed-document",document.DocumentNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,document.Title+": "+revision.FormalChangeSummary,revision.State.ToString(),"project",revision.UpdatedAt)).Take(take*2).ToListAsync(ct);
            // One row per document: a number-only search surfaces the newest formal revision rather than
            // one near-identical row per revision, while a number.revision search returns that exact revision.
            items.AddRange(exactManagedRevision is null
                ? managedCandidates.GroupBy(item => item.Id).Select(group => group.OrderByDescending(item => item.Identifier).First()).Take(take)
                : managedCandidates.Take(take));
            var requirementRows=effectiveBaselineId is not null
                ? await(from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId) on artifact.Id equals member.ArtifactId join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id where artifact.BaseNumber.ToLower().Contains(identifierQ)||revision.Statement.ToLower().Contains(q)||revision.Rationale.ToLower().Contains(q) select new{artifact.Id,artifact.BaseNumber,artifact.Level,revision.Revision,revision.Statement,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct)
                : await(from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)&&(artifact.BaseNumber.ToLower().Contains(identifierQ)||revision.Statement.ToLower().Contains(q)||revision.Rationale.ToLower().Contains(q)) select new{artifact.Id,artifact.BaseNumber,artifact.Level,revision.Revision,revision.Statement,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct);
            items.AddRange(requirementRows.Select(x=>new SearchResultDto(x.Id,"requirement",$"{x.BaseNumber}.{x.Revision:D2}",x.Statement,x.State.ToString(),x.Level==RequirementLevel.System?"system":"software",x.CreatedAt)));
            items.AddRange(await db.CandidateBaselines.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.BaseNumber.ToLower().Contains(q)||x.Name.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"baseline",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.BuildNumber.ToLower().Contains(q)||x.Description.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"build",x.BuildNumber,x.Description,x.State.ToString(),"software",x.RecordedAt)).ToListAsync(ct));
            var effectiveProcedureRevisionIds=procedureEffectivity?.RevisionIds.ToList();
            var procedureSearchRevisionIds=releaseId is null
                ? await(from revision in db.TestProcedureRevisions.AsNoTracking()
                        join procedure in db.TestProcedures.AsNoTracking().Where(x=>x.ProjectId==projectId)
                            on revision.ProcedureId equals procedure.Id
                        where revision.Revision==db.TestProcedureRevisions
                            .Where(other=>other.ProcedureId==procedure.Id).Max(other=>other.Revision)
                        select revision.Id).ToListAsync(ct)
                : effectiveProcedureRevisionIds??[];
            var matchingProcedureTitleRevisionIds=await TestProcedureRevisionTitleProjection.MatchingRevisionIdsAsync(
                db,procedureSearchRevisionIds,q,ct);
            var procedureCandidates=await(from revision in db.TestProcedureRevisions.AsNoTracking()
                join procedure in db.TestProcedures.AsNoTracking().Where(x=>x.ProjectId==projectId)
                    on revision.ProcedureId equals procedure.Id
                where (releaseId==null
                    ? revision.Revision==db.TestProcedureRevisions
                        .Where(other=>other.ProcedureId==procedure.Id).Max(other=>other.Revision)
                    : effectiveProcedureRevisionIds!=null&&effectiveProcedureRevisionIds.Contains(revision.Id))
                    &&(procedure.BaseNumber.ToLower().Contains(identifierQ)
                        ||matchingProcedureTitleRevisionIds.Contains(revision.Id))
                select new{procedure.Id,procedure.BaseNumber,procedure.Level,revisionId=revision.Id,
                    revision.Revision,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct);
            var procedureTitles=await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,procedureCandidates.Select(x=>x.revisionId).Distinct().ToList(),ct);
            items.AddRange(procedureCandidates.Select(x=>new SearchResultDto(x.Id,"test-procedure",$"{x.BaseNumber}.{x.Revision:D2}",procedureTitles[x.revisionId].Title,x.State.ToString(),x.Level==TestProcedureLevel.System?"system":"software",x.CreatedAt)));
            items.AddRange(await db.ControlledDocuments.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.DocumentNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"document",x.DocumentNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,"Generated",x.Type==ControlledDocumentType.Sysrd?"system":"software",x.GeneratedAt)).ToListAsync(ct));
            items.AddRange(await db.ReleaseCampaigns.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&x.Name.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release-campaign",x.Name,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.Releases.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.Id==releaseId)&&x.Version.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release",x.Version,"Software release "+x.Version,x.IsReleased?"Released":"InWork","configuration",x.ReleasedAt)).ToListAsync(ct));
            var executionSearchRevisionIds=await(from execution in db.TestExecutions.AsNoTracking()
                    .Where(x=>x.ProjectId==projectId)
                join revision in db.TestProcedureRevisions.AsNoTracking()
                    on execution.ProcedureRevisionId equals revision.Id
                where releaseId==null
                    ||(effectiveProcedureRevisionIds!=null&&effectiveProcedureRevisionIds.Contains(revision.Id))
                select revision.Id).Distinct().ToListAsync(ct);
            var matchingExecutionTitleRevisionIds=await TestProcedureRevisionTitleProjection.MatchingRevisionIdsAsync(
                db,executionSearchRevisionIds,q,ct);
            var executionRows=await(from execution in db.TestExecutions.AsNoTracking().Where(x=>x.ProjectId==projectId)
                join revision in db.TestProcedureRevisions.AsNoTracking()
                    on execution.ProcedureRevisionId equals revision.Id
                join procedure in db.TestProcedures.AsNoTracking()
                    on revision.ProcedureId equals procedure.Id
                where (releaseId==null
                    ||(effectiveProcedureRevisionIds!=null&&effectiveProcedureRevisionIds.Contains(revision.Id)))
                    &&(procedure.BaseNumber.ToLower().Contains(identifierQ)
                        ||matchingExecutionTitleRevisionIds.Contains(revision.Id)
                        ||execution.Determination.ToLower().Contains(q)
                        ||execution.EvidenceReference.ToLower().Contains(q))
                select new{execution.Id,revisionId=revision.Id,
                    identifier=procedure.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,
                    procedure.BaseNumber,execution.Determination,execution.EvidenceReference,
                    execution.Outcome,execution.RecordedAt,procedure.Level}).Take(take).ToListAsync(ct);
            var executionTitles=await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,executionRows.Select(x=>x.revisionId).Distinct().ToList(),ct);
            items.AddRange(executionRows.Select(x=>new SearchResultDto(x.Id,"test-execution",x.identifier,$"{executionTitles[x.revisionId].Title} result",x.Outcome.ToString(),x.Level==TestProcedureLevel.System?"system":"software",x.RecordedAt)));
            items.AddRange(await db.EvidenceRecords.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OriginalFileName.ToLower().Contains(q)||x.Sha256.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"evidence",x.OriginalFileName,x.Sha256,"Immutable","verification",x.UploadedAt)).ToListAsync(ct));
            var ordered=items.OrderByDescending(x=>x.Identifier.ToLowerInvariant().Contains(q)).ThenByDescending(x=>x.UpdatedAt).ThenBy(x=>x.Identifier).Take(take).ToList();return Results.Ok(new{query,items=ordered});
        });

        app.MapGet("/api/artifacts/{kind}/{id:guid}",async(string kind,Guid id,Guid? releaseId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            static Dictionary<string,object?> Details(params (string Key,object? Value)[] values)=>values.ToDictionary(x=>x.Key,x=>x.Value);
            var normalized=kind.Trim().ToLowerInvariant();
            if(normalized=="baseline")
            {var item=await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var members=await db.BaselineRequirements.CountAsync(x=>x.BaselineId==id,ct);var changes=await db.BaselineSelections.CountAsync(x=>x.BaselineId==id,ct);var related=(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.BaselineId==id).Select(x=>new RelatedArtifactDto("build",x.Id,x.BuildNumber,x.Description)).ToListAsync(ct));return Results.Ok(new{kind=normalized,item.Id,identifier=item.DisplayNumber,title=item.Name,state=item.State.ToString(),subtitle="Exact candidate baseline manifest",updatedAt=item.FrozenAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("requirementRevisions",members),("selectedChangeRequests",changes),("contentHash",item.ContentHash),("requirementsHash",item.RequirementsHash),("createdAt",item.CreatedAt)),related});}
            if(normalized=="build")
            {var item=await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.BuildNumber,title=item.Description,state=item.State.ToString(),subtitle="Immutable software build provenance",updatedAt=item.ReleasedAt??item.RecordedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("recordedBy",item.RecordedBy),("recordedAt",item.RecordedAt),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="document")
            {var item=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{item.DocumentNumber}.{item.Revision:D2}",title=item.Title,state="Generated",subtitle=$"{ApiMap.ControlledDocumentTypeLabel(item.Type)} controlled output",updatedAt=item.GeneratedAt,details=Details(("baseline",baseline.DisplayNumber),("artifactCount",item.ArtifactCount),("contentHash",item.ContentHash),("generatedAt",item.GeneratedAt)),related});}

if(normalized=="test-procedure")
{
    var item=await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
    if(item is null)return Results.NotFound();
    if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();
    var revisions=await db.TestProcedureRevisions.AsNoTracking().Where(x=>x.ProcedureId==id)
        .OrderByDescending(x=>x.Revision).ToListAsync(ct);
    TestProcedureRevision? selected;
    if(releaseId is Guid scopedReleaseId)
    {
        if(!await db.Releases.AsNoTracking().AnyAsync(
               x=>x.Id==scopedReleaseId&&x.ProjectId==item.ProjectId,ct))
            return Results.NotFound();
        var effectivity=await TestProcedureEffectivity.ForReleaseAsync(
            db,item.ProjectId,scopedReleaseId,ct);
        if(effectivity is null
           || !effectivity.RevisionByProcedure.TryGetValue(item.Id,out var selectedRevisionId))
            return Results.NotFound();
        selected=revisions.SingleOrDefault(x=>x.Id==selectedRevisionId);
        if(selected is null)return Results.NotFound();
    }
    else selected=revisions.FirstOrDefault();
    var coverage=selected is null?0:await db.TestCoverage.CountAsync(
        x=>x.ProcedureRevisionId==selected.Id,ct);
    var projectedTitle=selected is null?item.Title:
        (await TestProcedureRevisionTitleProjection.ForRevisionsAsync(
            db,[selected.Id],ct))[selected.Id].Title;
    return Results.Ok(new
    {
        kind=normalized,item.Id,
        identifier=selected is null?item.BaseNumber:$"{item.BaseNumber}.{selected.Revision:D2}",
        title=projectedTitle,state=selected?.State.ToString()??"Draft",
        subtitle=$"{item.Level} verification procedure",
        updatedAt=selected?.CreatedAt??item.CreatedAt,
        details=Details(("owner",item.OwnerId),("revisionCount",revisions.Count),
            ("revisionId",selected?.Id),("effectiveReleaseId",releaseId),
            ("coveredRequirements",coverage),("objective",selected?.Objective),
            ("expectedResult",selected?.ExpectedResult)),
        related=Array.Empty<RelatedArtifactDto>()
    });
}
            if(normalized=="release-campaign")
            {var item=await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.Name,title=item.Name,state=item.State.ToString(),subtitle="Governed release readiness and approval campaign",updatedAt=item.ReleasedAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("verificationBuildId",item.SoftwareBuildId),("releaseHash",item.ReleaseHash)),related});}
            if(normalized=="release")
            {var item=await db.Releases.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var related=await db.CandidateBaselines.AsNoTracking().Where(x=>x.ReleaseId==id).Select(x=>new RelatedArtifactDto("baseline",x.Id,x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name)).ToListAsync(ct);return Results.Ok(new{kind=normalized,item.Id,identifier=item.Version,title="Software release "+item.Version,state=item.IsReleased?"Released":"InWork",subtitle="Explicitly governed product-version record",updatedAt=item.ReleasedAt,details=Details(("predecessorReleaseId",item.PredecessorReleaseId),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="test-execution")
            {var item=await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var revision=await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x=>x.Id==item.ProcedureRevisionId,ct);var procedure=await db.TestProcedures.AsNoTracking().SingleAsync(x=>x.Id==revision.ProcedureId,ct);var projectedTitle=(await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,[revision.Id],ct))[revision.Id].Title;var related=new[]{new RelatedArtifactDto("test-procedure",procedure.Id,$"{procedure.BaseNumber}.{revision.Revision:D2}",projectedTitle)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{procedure.BaseNumber}.{revision.Revision:D2}",title=projectedTitle+" result",state=item.Outcome.ToString(),subtitle="Immutable attributable verification determination",updatedAt=item.RecordedAt,details=Details(("executedBy",item.ExecutedBy),("executedAt",item.ExecutedAt),("configuration",item.Configuration),("determination",item.Determination),("evidenceReference",item.EvidenceReference),("retestOfExecutionId",item.RetestOfExecutionId)),related});}
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

internal sealed record ChangeDashboardSummary(int Total, int Draft, int InReview, int Approved, int Deferred);
internal sealed record VerificationDashboardSummary(int TotalChangeRequests, int TriagedChangeRequests, int OpenDecisions, int ResolvedDecisions);
