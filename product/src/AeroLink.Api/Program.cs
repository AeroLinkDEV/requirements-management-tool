using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Api;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Diagnostics;
using System.Data;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAeroLinkInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(AeroLinkAuthorizationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AeroLinkAuthorizationHandler>(AeroLinkAuthorizationHandler.SchemeName, _ => { });
builder.Services.AddSingleton<BrowserMutationProtector>();
var loginRateLimit = Math.Max(1, builder.Configuration.GetValue<int?>("Identity:LoginRateLimitPerMinute") ?? 30);
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = loginRateLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("service-api", context => RateLimitPartition.GetFixedWindowLimiter(IntegrationSecurityService.Hash(context.Request.Headers.Authorization.ToString()), _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(10,builder.Configuration.GetValue<int?>("Integrations:ApiRateLimitPerMinute")??240), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
var configuredOrigins=builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()??[];
var allowedOrigins=configuredOrigins.Length>0?configuredOrigins:builder.Environment.IsDevelopment()?["http://localhost:5173","http://127.0.0.1:5173","http://127.0.0.1:5174"]:[];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if(allowedOrigins.Length>0)policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    else policy.SetIsOriginAllowed(_=>false);
}));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

await using (var scope = app.Services.CreateAsyncScope())
{
    var seedDemoAccounts = builder.Configuration.GetValue<bool>("Identity:SeedDemoAccounts");
    var allowDemoAccounts = builder.Configuration.GetValue<bool>("Identity:AllowDemoAccounts");
    if (seedDemoAccounts && !app.Environment.IsDevelopment() && !allowDemoAccounts)
        throw new InvalidOperationException("Demo identity seeding is disabled outside Development. Set Identity:AllowDemoAccounts only for an explicitly isolated non-production showcase.");
    var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
    if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    if (builder.Configuration.GetValue<bool>("DemoData:Enabled"))
        await scope.ServiceProvider.GetRequiredService<FmsShowcaseSeeder>().EnsureSeededAsync();
    if (seedDemoAccounts)
        await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().EnsureSeededAsync();
    await scope.ServiceProvider.GetRequiredService<EnterpriseWorkspaceSeeder>().EnsureAllAsync();
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var isApi = path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    var isAnonymous = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/setup/bootstrap", StringComparison.OrdinalIgnoreCase);
    if (!isApi || isAnonymous) { await next(); return; }
    if(path.StartsWith("/api/v1",StringComparison.OrdinalIgnoreCase))
    {
        var security=context.RequestServices.GetRequiredService<IntegrationSecurityService>();var service=await security.ResolveAsync(context.Request.Headers.Authorization.ToString(),DateTimeOffset.UtcNow,context.RequestAborted);
        if(service is null){context.Response.StatusCode=StatusCodes.Status401Unauthorized;await context.Response.WriteAsJsonAsync(new{error="A valid AeroLink service API key is required.",code="service_authentication_required"});return;}
        context.Items["AeroLink.ServiceIdentity"]=service;await next();return;
    }
    var identity = context.RequestServices.GetRequiredService<IdentityService>();
    var user = await identity.ResolveAsync(context.Request.Cookies[IdentityService.CookieName], DateTimeOffset.UtcNow, context.RequestAborted);
    if (user is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; await context.Response.WriteAsJsonAsync(new { error = "Authentication required.", code = "authentication_required" }); return; }
    context.Items["AeroLink.User"] = user;
    if(user.MustChangePassword&&!path.Equals("/api/auth/password",StringComparison.OrdinalIgnoreCase)&&!path.Equals("/api/auth/logout",StringComparison.OrdinalIgnoreCase)&&!path.Equals("/api/auth/me",StringComparison.OrdinalIgnoreCase)&&!path.Equals("/api/auth/csrf",StringComparison.OrdinalIgnoreCase))
    {context.Response.StatusCode=StatusCodes.Status403Forbidden;await context.Response.WriteAsJsonAsync(new{error="A password change is required before using AeroLink.",code="password_change_required"});return;}
    var browserMutation=!string.IsNullOrWhiteSpace(context.Request.Headers.Origin)||!string.IsNullOrWhiteSpace(context.Request.Headers["Sec-Fetch-Site"]);
    if(browserMutation&&context.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE"))
    {
        var valid=context.RequestServices.GetRequiredService<BrowserMutationProtector>().Validate(context.Request.Cookies[IdentityService.CookieName],context.Request.Headers["X-AeroLink-CSRF"].ToString());
        if(!valid){context.Response.StatusCode=StatusCodes.Status400BadRequest;await context.Response.WriteAsJsonAsync(new{error="The browser mutation token is missing or expired. Refresh and try again.",code="antiforgery_validation_failed"});return;}
    }
    var db=context.RequestServices.GetRequiredService<AeroLinkDbContext>();Guid? scopedProjectId=null;
    if(context.Request.Query.TryGetValue("projectId",out var rawProject)&&Guid.TryParse(rawProject.FirstOrDefault(),out var queryProject))scopedProjectId=queryProject;
    var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);if(scopedProjectId is null&&segments.Length>=3&&Guid.TryParse(segments[2],out var resourceId))
    {scopedProjectId=segments[1] switch{"scrs"=>await db.SystemChangeRequests.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"baselines"=>await db.CandidateBaselines.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"builds"=>await db.SoftwareBuilds.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"requirements"=>await db.Requirements.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"documents"=>await db.ControlledDocuments.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"traceability"=>await db.CandidateBaselines.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"release-campaigns"=>await db.ReleaseCampaigns.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"test-executions"=>await db.TestExecutions.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"trace-links"=>await db.RequirementTraces.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"evidence"=>await db.EvidenceRecords.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),_=>null};}
    if(scopedProjectId is not null&&!user.IsAdministrator){var programId=await db.Projects.Where(x=>x.Id==scopedProjectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(context.RequestAborted);if(programId is not null&&!user.Programs.Any(x=>x.ProgramId==programId)){context.Response.StatusCode=StatusCodes.Status403Forbidden;await context.Response.WriteAsJsonAsync(new{error="You are not authorized for this Program.",code="program_scope_forbidden"});return;}}
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AeroLink API", check="liveness" }));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy", service = "AeroLink API", check="liveness" }));
app.MapGet("/health/ready", async (AeroLinkDbContext db,CancellationToken ct) => await db.Database.CanConnectAsync(ct)?Results.Ok(new{status="ready",service="AeroLink API",database="connected"}):Results.Json(new{status="not_ready",service="AeroLink API",database="unavailable"},statusCode:StatusCodes.Status503ServiceUnavailable));
app.MapAeroLinkOperationsEndpoints();
app.MapAeroLinkPublicationEndpoints();
app.MapAeroLinkQualityIntelligenceEndpoints();

app.MapGet("/api/setup/status", async (AeroLinkDbContext db, IConfiguration configuration, CancellationToken ct) =>
{
    var bootstrapRequired = !await db.UserAccounts.AsNoTracking().AnyAsync(ct);
    return Results.Ok(new
    {
        bootstrapRequired,
        bootstrapEnabled = bootstrapRequired && BootstrapSecret(configuration) is not null
    });
});

app.MapPost("/api/setup/bootstrap", async (BootstrapAdministratorRequest request, HttpContext http, AeroLinkDbContext db, IConfiguration configuration, CancellationToken ct) =>
{
    var configuredSecret = BootstrapSecret(configuration);
    if (configuredSecret is null)
        return Results.Json(new { error = "First-install bootstrap is not configured.", code = "bootstrap_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!FixedTimeSecretEquals(http.Request.Headers["X-AeroLink-Bootstrap-Secret"].ToString(), configuredSecret))
        return Results.Json(new { error = "Bootstrap authorization failed.", code = "bootstrap_authorization_failed" }, statusCode: StatusCodes.Status401Unauthorized);
    var passwordError = BootstrapPasswordError(request.Password);
    if (passwordError is not null) return Results.BadRequest(new { error = passwordError, code = "bootstrap_password_policy" });

    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    if (await db.UserAccounts.AnyAsync(ct))
        return Results.Conflict(new { error = "First-install bootstrap has already been completed.", code = "bootstrap_complete" });
    try
    {
        var now = DateTimeOffset.UtcNow;
        var administrator = new UserAccount(IdentityService.SystemAdministratorUserName, request.DisplayName, request.Email, IdentityService.HashPassword(request.Password), now);
        db.UserAccounts.Add(administrator);
        db.SecurityAuditEvents.Add(new("BootstrapAdministratorCreated", "system.bootstrap", administrator.UserName, "Success", "Created the one-time first-install global administrator.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/admin/users/{administrator.Id}", new { administrator.Id, administrator.UserName, administrator.DisplayName });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "First-install bootstrap was completed by another request.", code = "bootstrap_complete" }); }
}).RequireRateLimiting("authentication");

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IdentityService identity, CancellationToken ct) =>
{
    var result = await identity.LoginAsync(request.UserName, request.Password, http.Connection.RemoteIpAddress?.ToString() ?? "local", http.Request.Headers.UserAgent.ToString(), DateTimeOffset.UtcNow,request.MfaCode, ct);
    if (result is null) return Results.Json(new { error = "The credentials or second-factor code were not accepted." }, statusCode: 401);
    var secureCookie = builder.Configuration.GetValue<bool?>("Identity:CookieSecure") ?? !app.Environment.IsDevelopment();
    http.Response.Cookies.Append(IdentityService.CookieName, result.Token, new CookieOptions { HttpOnly = true, Secure = secureCookie, SameSite = SameSiteMode.Lax, Expires = result.ExpiresAt, Path = "/" });
    return Results.Ok(result.User);
}).RequireRateLimiting("authentication");
app.MapPost("/api/auth/logout", async (HttpContext http, IdentityService identity, CancellationToken ct) => { await identity.LogoutAsync(http.Request.Cookies[IdentityService.CookieName], http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow, ct); http.Response.Cookies.Delete(IdentityService.CookieName); return Results.NoContent(); });
app.MapGet("/api/auth/sessions", async (HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
{
    var actor=http.UserAccount();return Results.Ok(await db.UserSessions.AsNoTracking().Where(x=>x.UserId==actor.Id).OrderByDescending(x=>x.LastSeenAt).Select(x=>new{x.Id,x.IpAddress,x.UserAgent,x.CreatedAt,x.LastSeenAt,x.ExpiresAt,x.RevokedAt}).ToListAsync(ct));
});
app.MapPost("/api/auth/sessions/revoke-others", async (HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
{
    var actor=http.UserAccount();var currentHash=IdentityService.TokenDigest(http.Request.Cookies[IdentityService.CookieName]);var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==actor.Id&&x.RevokedAt==null&&x.TokenHash!=currentHash).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("SessionsRevoked",actor.UserName,"session","Success",$"Revoked {sessions.Count} other active session(s).",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);return Results.Ok(new{revoked=sessions.Count});
});
app.MapPost("/api/auth/password", async (ChangeOwnPasswordRequest request,HttpContext http,IdentityService identity,AeroLinkDbContext db,CancellationToken ct) =>
{
    var actor=http.UserAccount();if(!await identity.ConfirmPasswordAsync(actor.Id,request.CurrentPassword,ct))return Results.Json(new{error="Current password confirmation failed."},statusCode:401);try{var user=await db.UserAccounts.SingleAsync(x=>x.Id==actor.Id,ct);user.ChangePassword(IdentityService.HashPassword(request.NewPassword));var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==actor.Id&&x.RevokedAt==null).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("PasswordChanged",actor.UserName,user.UserName,"Success","Password changed and all sessions revoked.",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);http.Response.Cookies.Delete(IdentityService.CookieName);return Results.NoContent();}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/auth/mfa/enroll",async(HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    var actor=http.UserAccount();var prior=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);
    if(prior?.Confirmed==true)return Results.Conflict(new{error="MFA is already enabled. Confirm the current factor before rotating it.",code="mfa_already_enabled"});
    if(prior is not null)db.UserMfaEnrollments.Remove(prior);var secret=IdentityService.CreateMfaSecret();var enrollment=new UserMfaEnrollment(actor.Id,identity.ProtectMfaSecret(secret),actor.UserName,DateTimeOffset.UtcNow);db.UserMfaEnrollments.Add(enrollment);await db.SaveChangesAsync(ct);
    var label=Uri.EscapeDataString($"AeroLink:{actor.UserName}");var issuer=Uri.EscapeDataString("AeroLink");return Results.Ok(new{enrollment.Id,secret,otpauthUri=$"otpauth://totp/{label}?secret={Uri.EscapeDataString(secret)}&issuer={issuer}&algorithm=SHA1&digits=6&period=30"});
});
app.MapPost("/api/auth/mfa/confirm",async(ConfirmMfaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>{var actor=http.UserAccount();var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);if(enrollment is null||enrollment.Confirmed)return Results.NotFound();if(!IdentityService.VerifyTotp(identity.RevealMfaSecret(enrollment.Secret),request.Code,DateTimeOffset.UtcNow))return Results.BadRequest(new{error="The authenticator code is not valid."});enrollment.Confirm(DateTimeOffset.UtcNow);var codes=Enumerable.Range(0,10).Select(_=>IdentityService.NewRecoveryCode()).ToList();db.MfaRecoveryCodes.RemoveRange(db.MfaRecoveryCodes.Where(x=>x.UserId==actor.Id));db.MfaRecoveryCodes.AddRange(codes.Select(code=>new MfaRecoveryCode(actor.Id,IdentityService.RecoveryHash(code),DateTimeOffset.UtcNow)));db.SecurityAuditEvents.Add(new("MfaEnabled",actor.UserName,"mfa","Success","Authenticator enrollment confirmed and recovery codes generated.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.Ok(new{recoveryCodes=codes});});
app.MapGet("/api/auth/security",async(HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var actor=http.UserAccount();var enrollment=await db.UserMfaEnrollments.AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);var recovery=await db.MfaRecoveryCodes.AsNoTracking().Where(x=>x.UserId==actor.Id).ToListAsync(ct);var sessions=await db.UserSessions.AsNoTracking().Where(x=>x.UserId==actor.Id&&x.RevokedAt==null).ToListAsync(ct);return Results.Ok(new{mfaEnabled=enrollment?.Confirmed==true,mfaPending=enrollment is not null&&!enrollment.Confirmed,recoveryCodesRemaining=recovery.Count(x=>x.UsedAt==null),activeSessions=sessions.Count});});
app.MapPost("/api/auth/mfa/disable",async(DisableMfaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>{var actor=http.UserAccount();if(!await identity.ConfirmPasswordAsync(actor.Id,request.Password,ct))return Results.Json(new{error="Password confirmation failed."},statusCode:401);var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id&&x.Confirmed,ct);if(enrollment is null)return Results.NotFound();var valid=IdentityService.VerifyTotp(identity.RevealMfaSecret(enrollment.Secret),request.Code,DateTimeOffset.UtcNow);if(!valid){var recovery=await db.MfaRecoveryCodes.SingleOrDefaultAsync(x=>x.UserId==actor.Id&&x.CodeHash==IdentityService.RecoveryHash(request.Code)&&x.UsedAt==null,ct);valid=recovery is not null;}if(!valid)return Results.Json(new{error="A current authenticator or unused recovery code is required."},statusCode:401);db.UserMfaEnrollments.Remove(enrollment);db.MfaRecoveryCodes.RemoveRange(db.MfaRecoveryCodes.Where(x=>x.UserId==actor.Id));db.SecurityAuditEvents.Add(new("MfaDisabled",actor.UserName,"mfa","Success","MFA was disabled after password and second-factor confirmation.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.NoContent();});
app.MapGet("/api/auth/me", (HttpContext http) => Results.Ok(http.UserAccount()));
app.MapGet("/api/auth/csrf", (HttpContext http,BrowserMutationProtector protector) =>
{
    var session=http.Request.Cookies[IdentityService.CookieName];return Results.Ok(new{token=protector.Issue(session!),header="X-AeroLink-CSRF"});
});

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

app.MapPost("/api/scrs/{id:guid}/retarget", async (Guid id, RetargetScrRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (!await db.Releases.AnyAsync(x => x.Id == request.TargetReleaseId && x.ProjectId == scr.ProjectId && !x.IsReleased, ct)) return Results.BadRequest(new { error = "Choose an unreleased target release in this project." });
    // Verification work follows its change request. Left behind, it would hold a release the change no longer
    // belongs to and go missing from the one it does.
    try
    {
        var now = DateTimeOffset.UtcNow;
        scr.Retarget(http.UserAccount().UserName, request.TargetReleaseId, request.Reason, now);
        await verificationImpact.RetargetAsync(scr.Id, request.TargetReleaseId, now, ct);
        await repository.SaveAsync(ct);
        return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/next-revision", async (Guid id, ActorRequest request, HttpContext http, IScrRepository repository, CancellationToken ct) =>
{
    var approved = await repository.GetAsync(id, ct); if (approved is null) return Results.NotFound();
    if (request.ExpectedVersion is not null && approved.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This approved request changed after it was opened. Refresh before revising.", code = "stale_version" });
    try
    {
        var next = approved.StartNextRevision(http.UserAccount().UserName, DateTimeOffset.UtcNow);
        await repository.AddAsync(next, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/scrs/{next.Id}", ApiMap.ScrDetail(next));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DbUpdateException) { return Results.Conflict(new { error = $"A later revision of {approved.BaseNumber} already exists." }); }
});

app.MapGet("/api/scrs", async (Guid projectId, Guid? releaseId, int? page, int? pageSize, string? search, ScrState? state, IScrRepository repository, CancellationToken ct) =>
{
    var result = await repository.QueryAsync(new ScrQuery(projectId, page is null or 0 ? 1 : page.Value, pageSize is null or 0 ? 50 : pageSize.Value, search, state, releaseId), ct);
    return Results.Ok(new { result.Page, result.PageSize, result.TotalCount, result.TotalPages, items = result.Items.Select(ApiMap.ScrSummary) });
});

app.MapGet("/api/scrs/{id:guid}", async (Guid id, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct);
    return scr is null ? Results.NotFound() : Results.Ok(ApiMap.ScrDetail(scr));
});

app.MapGet("/api/authoring/context", async (Guid projectId, ChangeRequestType type, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
    var prefixes = type == ChangeRequestType.System ? new[] { "SYSR" } : new[] { "HLR", "LLR" };
    var numbers = new Dictionary<string, string>();
    foreach (var prefix in prefixes) numbers[prefix] = await IdentifierAllocator.NextRequirementAsync(db, prefix, ct);
    return Results.Ok(new
    {
        type = type.ToString(),
        changeRequestNumber = await IdentifierAllocator.NextChangeRequestAsync(db, type, ct),
        author = new { http.UserAccount().UserName, http.UserAccount().DisplayName },
        requirementNumbers = numbers
    });
});

app.MapGet("/api/authoring/requirements", async (Guid projectId, string scope, string? search, int? limit, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
    var artifacts = db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId);
    artifacts = scope.Equals("System", StringComparison.OrdinalIgnoreCase)
        ? artifacts.Where(x => x.Level == RequirementLevel.System)
        : artifacts.Where(x => x.Level == RequirementLevel.HighLevel || x.Level == RequirementLevel.LowLevel);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim().ToLower();
        artifacts = artifacts.Where(x => x.BaseNumber.ToLower().Contains(term));
    }
    var rows = await (from artifact in artifacts
                      join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                      where revision.Revision == db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).Max(x => x.Revision)
                      orderby artifact.BaseNumber
                      select new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString() })
        .Take(Math.Clamp(limit ?? 12, 1, 50)).ToListAsync(ct);
    return Results.Ok(rows.Select(x => new { x.Id, x.BaseNumber, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.level, x.Revision, nextRevision = x.Revision + 1, x.Statement, x.Rationale, x.VerificationMethod, x.state }));
});

app.MapGet("/api/scrs/{id:guid}/download", async (Guid id, string? format, ChangeRequestOutputGenerator generator, CancellationToken ct) =>
{
    var output = await generator.GenerateAsync(id, format ?? "docx", ct); return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
});

app.MapPut("/api/scrs/{id:guid}/draft", (Guid id) => Results.Json(new
{
    error = "Direct controlled-content updates are retired. Autosave the edit session and use the universal check-in endpoint.",
    code = "universal_check_in_required",
    artifactId = id,
    checkInRoute = "/api/controlled-editing/sessions/{sessionId}/check-in"
}, statusCode: StatusCodes.Status410Gone));

app.MapGet("/api/requirement-changes", async (Guid projectId, int page, int pageSize, string? search, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page);
    pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = db.RequirementChanges.AsNoTracking()
        .Where(x => db.SystemChangeRequests.Any(scr => scr.Id == x.ScrId && scr.ProjectId == projectId));
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        source = source.Where(x => EF.Functions.ILike(x.BaseNumber, $"%{term}%") || EF.Functions.ILike(x.Statement, $"%{term}%"));
    }
    var totalCount = await source.CountAsync(ct);
    var items = await source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(), x.Statement, x.VerificationMethod, x.ScrId })
        .ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
});

// Historical discovery endpoints deliberately include every revision and lifecycle state.
app.MapGet("/api/history/scrs", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId, ChangeRequestType? type, string? state,
    int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (type is not null) source = source.Where(x => x.Type == type);
    if (!string.IsNullOrWhiteSpace(state))
    {
        if (state.Equals("ApprovedOrSelected", StringComparison.OrdinalIgnoreCase))
            source = source.Where(x => x.State == ScrState.Approved || x.State == ScrState.SelectedForBaseline);
        else if (Enum.TryParse<ScrState>(state, true, out var parsedState)) source = source.Where(x => x.State == parsedState);
        else return Results.BadRequest(new { error = "The requested lifecycle state is not recognized." });
    }
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
        x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q) || x.Problem.ToLower().Contains(q) ||
        x.Analysis.ToLower().Contains(q) || x.Solution.ToLower().Contains(q)); }
    if (releaseId is not null) source = source.Where(x => x.TargetReleaseId == releaseId);
    var selectedBaselineId = baselineId;
    if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
    if (selectedBaselineId is not null) source = source.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ScrId == x.Id));
    var total = await source.CountAsync(ct);
    var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision) : source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.BaseNumber).ThenByDescending(x => x.Revision);
    var items = await ordered
        .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision,
            x.BaseNumber, x.Revision, x.Title, state = x.State.ToString(), x.AuthorId, x.TargetReleaseId, requirementCount = x.RequirementChanges.Count, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/history/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId,
    int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var scrs = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (releaseId is not null) scrs = scrs.Where(x => x.TargetReleaseId == releaseId);
    var selectedBaselineId = baselineId;
    if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
    if (selectedBaselineId is not null) scrs = scrs.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ScrId == x.Id));
    var source = from r in db.RequirementChanges.AsNoTracking() join s in scrs on r.ScrId equals s.Id select new { r, s };
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
        x.r.BaseNumber.ToLower().Contains(q) || x.r.Statement.ToLower().Contains(q) ||
        x.r.Rationale.ToLower().Contains(q) || x.s.Title.ToLower().Contains(q)); }
    var total = await source.CountAsync(ct);
    var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision) : source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision).ThenByDescending(x => x.s.UpdatedAt);
    var items = await ordered
        .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.r.Id, displayNumber = x.r.BaseNumber + "." + (x.r.Revision < 10 ? "0" : "") + x.r.Revision,
            x.r.BaseNumber, x.r.Revision, level = x.r.Level.ToString(), kind = x.r.Kind.ToString(), x.r.Statement, x.r.Rationale, x.r.VerificationMethod,
            scrId = x.s.Id, scrDisplayNumber = x.s.BaseNumber + "." + (x.s.Revision < 10 ? "0" : "") + x.s.Revision, scrTitle = x.s.Title, scrState = x.s.State.ToString(), x.s.TargetReleaseId }).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/builds", async (Guid projectId, string? search, AeroLinkDbContext db, CancellationToken ct) =>
{
    var source = db.SoftwareBuilds.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BuildNumber.ToLower().Contains(q) || x.Description.ToLower().Contains(q)); }
    var joined = from build in source join release in db.Releases.AsNoTracking() on build.ReleaseId equals release.Id join baseline in db.CandidateBaselines.AsNoTracking() on build.BaselineId equals baseline.Id
        select new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
            releaseId = release.Id, release.Version, baselineId = baseline.Id, baselineDisplayNumber = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision,
            baseline.ContentHash, scrCount = baseline.Selections.Count };
    var items = await (db.Database.IsSqlite() ? joined.OrderByDescending(x => x.BuildNumber) : joined.OrderByDescending(x => x.RecordedAt)).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/builds", async (CreateBuildRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.BaselineId, ct);
    if (baseline is null) return Results.NotFound();
    if (baseline.State != CandidateBaselineState.Frozen) return Results.BadRequest(new { error = "A build can only reference a frozen baseline." });
    if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the authoritative requirement baseline before recording a software build." });
    if (baseline.ProjectId != request.ProjectId || baseline.ReleaseId != request.ReleaseId) return Results.BadRequest(new { error = "Build, release, and baseline must belong to the same project context." });
    if (await db.SoftwareBuilds.AnyAsync(x => x.ProjectId == request.ProjectId && x.BuildNumber == request.BuildNumber.Trim(), ct)) return Results.Conflict(new { error = "That build number already exists in this project." });
    try { var build = new SoftwareBuild(request.ProjectId, request.ReleaseId, request.BaselineId, request.BuildNumber, request.Description, http.UserAccount().UserName, DateTimeOffset.UtcNow);
        db.SoftwareBuilds.Add(build); await db.SaveChangesAsync(ct); return Results.Created($"/api/builds/{build.Id}", new { build.Id, build.BuildNumber }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/builds/{id:guid}", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{
    var build = await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (build is null) return Results.NotFound();
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == build.BaselineId, ct);
    var scrIds = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baseline.Id).Select(x => x.ScrId).ToListAsync(ct);
    var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).ToListAsync(ct);
    var effectiveRequirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                                       join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                       join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                       orderby artifact.BaseNumber select new { artifact.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                           level = artifact.Level.ToString(), revision.Statement, revision.VerificationMethod }).ToListAsync(ct);
    return Results.Ok(new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
        build.ProjectId, build.ReleaseId, baseline = new { baseline.Id, baseline.DisplayNumber, baseline.Name, baseline.ContentHash, baseline.FrozenAt },
        effectiveRequirements, scrs = scrs.Select(x => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString(), requirements = x.RequirementChanges.OrderBy(r => r.BaseNumber).ThenByDescending(r => r.Revision).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement }) }) });
});

app.MapPost("/api/scrs", async (CreateScrRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
    try
    {
        var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, ct);
        var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
            request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, DateTimeOffset.UtcNow, request.Type);
        await repository.AddAsync(scr, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scr-drafts", async (CreateScrDraftRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer)) return Results.Forbid();
    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
        var now = DateTimeOffset.UtcNow;
        var actor = http.UserAccount().UserName;
        var baseNumber = await IdentifierAllocator.NextChangeRequestAsync(db, request.Type, ct);
        var scr = new SystemChangeRequest(baseNumber, 0, request.ProjectId, request.TargetReleaseId,
            request.Title, request.Problem, request.Analysis, request.Solution, http.UserAccount().UserName, now, request.Type);
        var nextNumbers = new Dictionary<string, int>();
        foreach (var change in request.RequirementChanges)
        {
            if (request.Type == ChangeRequestType.System && change.Level != RequirementLevel.System)
                return Results.BadRequest(new { error = "A System SCR can contain only System requirement changes." });
            if (request.Type == ChangeRequestType.Software && change.Level == RequirementLevel.System)
                return Results.BadRequest(new { error = "A Software SWCR can contain only HLR and LLR changes." });
            if (change.IsDerived && string.IsNullOrWhiteSpace(change.Rationale))
                return Results.BadRequest(new { error = "Every derived software requirement requires an explicit engineering rationale." });
            string requirementNumber; int revision;
            if (change.Kind == RequirementChangeKind.Introduce)
            {
                var prefix = change.Level switch { RequirementLevel.System => "SYSR", RequirementLevel.HighLevel => "HLR", _ => "LLR" };
                if (!nextNumbers.TryGetValue(prefix, out var next))
                    next = IdentifierAllocator.Sequence(await IdentifierAllocator.NextRequirementAsync(db, prefix, ct));
                requirementNumber = IdentifierAllocator.Format(prefix, next);
                revision = 0;
                nextNumbers[prefix] = next + 1;
            }
            else
            {
                var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId && x.BaseNumber == change.BaseNumber.Trim().ToUpper(), ct);
                if (artifact is null) return Results.BadRequest(new { error = $"Select an existing controlled requirement before proposing a {change.Kind.ToString().ToLowerInvariant()}." });
                if (artifact.Level != change.Level) return Results.BadRequest(new { error = $"{artifact.BaseNumber} is not a {change.Level} requirement." });
                requirementNumber = artifact.BaseNumber;
                revision = await db.RequirementRevisions.Where(x => x.ArtifactId == artifact.Id).MaxAsync(x => x.Revision, ct) + 1;
            }
            var attributes = JsonSerializer.Serialize(new { derived = change.Level != RequirementLevel.System && change.IsDerived });
            scr.AddRequirementChange(actor, requirementNumber, revision, change.Level, change.Kind,
                change.Statement, change.Rationale, change.VerificationMethod, now, change.RichText, attributes, change.ImpactDispositionJson);
        }
        await repository.AddAsync(scr, ct);
        await repository.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "Another author created an artifact at the same instant. No duplicate was saved; submit again to receive the next available numbers." }); }
});

app.MapPost("/api/scrs/{id:guid}/requirements", async (Guid id, RequirementChangeRequest request, HttpContext http, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    try
    {
        scr.AddRequirementChange(http.UserAccount().UserName, request.BaseNumber, request.Revision, request.Level, request.Kind,
            request.Statement, request.Rationale, request.VerificationMethod, DateTimeOffset.UtcNow);
        await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/submit", async (Guid id, SubmitReviewRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This SCR changed after it was opened. Refresh it before submitting.", code = "stale_version" });
    var now=DateTimeOffset.UtcNow;var editSessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==id&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in editSessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);if(db.ChangeTracker.HasChanges())await db.SaveChangesAsync(ct);var activeEdit=editSessions.FirstOrDefault(x=>x.State==EditSessionState.Active);if(activeEdit is not null)return Results.Conflict(new{error=$"Review cannot begin while {activeEdit.UserName} has the Draft checked out.",code="active_edit_session",activeEdit.ExpiresAt});
    try
    {
        var actor = http.UserAccount();
        if (scr.AuthorId != actor.UserName && !actor.IsAdministrator) return Results.Forbid();
        var known = await db.UserAccounts.AsNoTracking().Where(x => request.Approvers.Select(a => a.UserId.ToLower()).Contains(x.UserName) && x.State == AccountState.Active).Select(x => new { x.UserName, x.DisplayName }).ToListAsync(ct);
        if (known.Count != request.Approvers.Count) return Results.BadRequest(new { error = "Every approver must be an active AeroLink user." });
        var directory = known.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
        var selections = request.Approvers.Select(x => new ApproverSelection(directory[x.UserId].UserName, directory[x.UserId].DisplayName)).ToList();
        var cycle = scr.SubmitForReview(actor.UserName, selections, now, request.Mode);
        foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
            db.UserNotifications.Add(new(scr.ProjectId, step.ApproverId, "ReviewActivated", $"Review {scr.DisplayNumber}", $"You are now authorized to review {scr.DisplayNumber}: {scr.Title}", $"scr:{scr.Id}", scr.Id, now));
        await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/approve", async (Guid id, SignatureRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, IdentityService identity, VerificationImpactService verificationImpact, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
    var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
    var programId = await db.Projects.Where(x => x.Id == scr.ProjectId).Join(db.Programs, x => x.ProgramId, x => x.Id, (_, p) => p.Id).SingleAsync(ct);
    if (!await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct)) return Results.Forbid();
    try { var now = DateTimeOffset.UtcNow; var snapshotHash = scr.ActiveReviewCycle?.SnapshotHash ?? ""; var activeBefore=scr.ActiveReviewCycle!.Steps.Where(x=>x.State==ApprovalStepState.Active).Select(x=>x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase); scr.ApproveActiveStage(actor.UserName, now); var activated=scr.ActiveReviewCycle?.Steps.Where(x=>x.State==ApprovalStepState.Active&&!activeBefore.Contains(x.ApproverId)).ToList()??[];foreach(var step in activated)db.UserNotifications.Add(new(scr.ProjectId,step.ApproverId,"ReviewActivated",$"Review {scr.DisplayNumber}",$"The prior stage is complete. You are now authorized to review {scr.DisplayNumber}: {scr.Title}",$"scr:{scr.Id}",scr.Id,now)); db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "SCR", scr.Id, scr.DisplayNumber, "Approve", request.Meaning, snapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
        // Approval is what settles the engineering decision, so verification work is raised here rather than
        // waiting for baseline inclusion. Saved in the same unit of work as the approval itself.
        await verificationImpact.RaiseForApprovedChangeRequestAsync(scr, now, ct);
        await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/request-changes", async (Guid id, RequestChangesRequest request, HttpContext http, IScrRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
    try { var now=DateTimeOffset.UtcNow;scr.RequestChanges(http.UserAccount().UserName, request.Reason, now);db.UserNotifications.Add(new(scr.ProjectId,scr.AuthorId,"ReviewChangesRequested",$"Changes requested for {scr.DisplayNumber}",request.Reason,$"scr:{scr.Id}",scr.Id,now)); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var items = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId)
        .OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Name, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count }).ToListAsync(ct);
    return Results.Ok(items.Select(x => new { x.Id, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Name, x.state, x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.selectionCount }));
});

app.MapGet("/api/baselines/predecessors", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var items = await (from baseline in db.CandidateBaselines.AsNoTracking()
                       join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id
                       where baseline.ProjectId == projectId && baseline.ReleaseId != releaseId && baseline.RequirementsMaterializedAt != null
                       orderby release.IsReleased descending, release.Version descending, baseline.FrozenAt descending
                       select new { baseline.Id, baseline.DisplayNumber, baseline.Name, baseline.ReleaseId, release = release.Version, release.IsReleased, baseline.RequirementsHash, requirementCount = db.BaselineRequirements.Count(x => x.BaselineId == baseline.Id) }).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/baselines", async (CreateBaselineRequest request, HttpContext http, IBaselineRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
    try
    {
        if (!await db.Releases.AnyAsync(x => x.Id == request.ReleaseId && x.ProjectId == request.ProjectId && !x.IsReleased, ct))
            return Results.BadRequest(new { error = "Candidate baselines can only be created for an unreleased version in this project." });
        var priorProductExists = await db.CandidateBaselines.AnyAsync(x => x.ProjectId == request.ProjectId && x.ReleaseId != request.ReleaseId && x.RequirementsMaterializedAt != null, ct);
        if (priorProductExists && request.PredecessorBaselineId is null)
            return Results.BadRequest(new { error = "Select the exact predecessor product baseline that this candidate inherits." });
        if (request.PredecessorBaselineId is not null && !await db.CandidateBaselines.AnyAsync(x => x.Id == request.PredecessorBaselineId && x.ProjectId == request.ProjectId && x.ReleaseId != request.ReleaseId && x.RequirementsMaterializedAt != null, ct))
            return Results.BadRequest(new { error = "The predecessor must be a materialized baseline from this project." });
        var baseline = new CandidateBaseline(request.BaseNumber, request.Revision, request.ProjectId, request.ReleaseId,
            request.PredecessorBaselineId, request.Name, http.UserAccount().UserName, DateTimeOffset.UtcNow);
        await repository.AddAsync(baseline, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/baselines/{baseline.Id}", ApiMap.Baseline(baseline));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines/{id:guid}", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var scrIds = baseline.Selections.Select(x => x.ScrId).ToList();
    var selected = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id))
        .Include(x => x.RequirementChanges).ToListAsync(ct);
    return Results.Ok(ApiMap.BaselineDetail(baseline, selected));
});

app.MapGet("/api/baselines/{id:guid}/eligible-scrs", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var items = await db.SystemChangeRequests.AsNoTracking()
        .Where(x => x.ProjectId == baseline.ProjectId && x.TargetReleaseId == baseline.ReleaseId && x.State == ScrState.Approved)
        .OrderBy(x => x.BaseNumber).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title, requirementCount = x.RequirementChanges.Count, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/baselines/{id:guid}/selections", async (Guid id, BaselineSelectionRequest request, HttpContext http, IBaselineRepository baselines, IScrRepository scrs, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
    var scr = await scrs.GetAsync(request.ScrId, ct); if (scr is null) return Results.NotFound();
    try { baseline.Select(scr, http.UserAccount().UserName, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.Ok(ApiMap.Baseline(baseline)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/baselines/{id:guid}/selections/{scrId:guid}", async (Guid id, Guid scrId, HttpContext http, IBaselineRepository baselines, IScrRepository scrs, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
    var scr = await scrs.GetAsync(scrId, ct); if (scr is null) return Results.NotFound();
    try { baseline.Remove(scr, http.UserAccount().UserName, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Freezing is deliberately not gated on verification decisions. Freezing then materializing is what
// creates the requirement revisions a test engineer needs in order to write a procedure at all, so
// blocking the freeze would withhold the test team's own inputs and deadlock the release. The gate the
// verification queue belongs to is release approval, where it appears as a named readiness gate.
app.MapPost("/api/baselines/{id:guid}/freeze", async (Guid id, BaselineActorRequest request, HttpContext http, IBaselineRepository repository, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    if (!await http.HasProjectRoleAsync(db, identity, baseline.ProjectId, ct, ProgramRole.ConfigurationManager)) return Results.Forbid();
    try { baseline.Freeze(http.UserAccount().UserName, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.Baseline(baseline)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/baselines/{id:guid}/materialize-requirements", async (Guid id, BaselineActorRequest request, HttpContext http, RequirementBaselineMaterializer materializer, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var projectId=await db.CandidateBaselines.Where(x=>x.Id==id).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct); if(projectId is null)return Results.NotFound(); if(!await http.HasProjectRoleAsync(db,identity,projectId.Value,ct,ProgramRole.ConfigurationManager))return Results.Forbid();
    try { return Results.Ok(await materializer.MaterializeAsync(id, http.UserAccount().UserName, DateTimeOffset.UtcNow, ct)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines/{id:guid}/swrd", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound();
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct);
    var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
    var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id)
                      join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                      join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                      join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                      orderby artifact.BaseNumber
                      select new { artifact.Id, artifact.BaseNumber, revisionId = revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                          level = artifact.Level.ToString(), revision.Statement, revision.Rationale, revision.VerificationMethod, sourceScrId = scr.Id,
                          sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision }).ToListAsync(ct);
    return Results.Ok(new { documentType = "SWRD", title = $"{project.SoftwareProduct} Software Requirements Document", release = release.Version,
        baseline = baseline.DisplayNumber, baseline.Name, baseline.RequirementsHash, baseline.RequirementsMaterializedAt, requirementCount = rows.Count, requirements = rows });
});

app.MapGet("/api/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, string? scope, bool? includeRetired, int? page, int? pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    var resolvedPage=Math.Max(1,page??1);var resolvedPageSize=Math.Clamp(pageSize??50,1,200);
    var source = from artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                 join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                 join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                 select new { artifact, revision, scr };
    if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.System);
    else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.HighLevel||x.artifact.Level==RequirementLevel.LowLevel);
    if (baselineId is not null) source = source.Where(x => db.BaselineRequirements.Any(m => m.BaselineId == baselineId && m.RevisionId == x.revision.Id));
    else if (includeRetired != true) source = source.Where(x => x.revision.State == AeroLink.Domain.Requirements.RequirementRevisionState.Active);
    if (releaseId is not null) source = source.Where(x => db.CandidateBaselines.Any(b => b.Id == x.revision.EffectiveBaselineId && b.ReleaseId == releaseId));
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q) || x.revision.Rationale.ToLower().Contains(q)); }
    var total = await source.CountAsync(ct);
    var items = await source.OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision).Skip((resolvedPage - 1) * resolvedPageSize).Take(resolvedPageSize)
        .Select(x => new { x.artifact.Id, x.artifact.BaseNumber, level = x.artifact.Level.ToString(), revisionId = x.revision.Id, x.revision.Revision,
            displayNumber = x.artifact.BaseNumber + "." + (x.revision.Revision < 10 ? "0" : "") + x.revision.Revision, x.revision.Statement, x.revision.Rationale,
            x.revision.VerificationMethod, state = x.revision.State.ToString(), x.revision.EffectiveBaselineId, sourceScrId = x.scr.Id,
            sourceScr = x.scr.BaseNumber + "." + (x.scr.Revision < 10 ? "0" : "") + x.scr.Revision, x.revision.CreatedAt }).ToListAsync(ct);
    return Results.Ok(new { page=resolvedPage, pageSize=resolvedPageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)resolvedPageSize), items });
});

app.MapGet("/api/requirements/{id:guid}/history", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (artifact is null) return Results.NotFound();
    if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
    var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == id)
                           join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                           join baseline in db.CandidateBaselines.AsNoTracking() on revision.EffectiveBaselineId equals baseline.Id
                           orderby revision.Revision descending select new { revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                               revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString(), revision.CreatedAt,
                               sourceScrId = scr.Id, sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision,
                               baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
    return Results.Ok(new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revisions });
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

app.MapGet("/api/documents", async (Guid projectId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var rows = await (from document in db.ControlledDocuments.AsNoTracking().Where(x => x.ProjectId == projectId)
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
app.MapGet("/api/documents/{id:guid}/manifest",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    var document=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(document is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,document.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==document.BaselineId,ct);var release=await db.Releases.AsNoTracking().SingleAsync(x=>x.Id==document.ReleaseId,ct);var approvals=await(from approval in db.ReleaseApprovals.AsNoTracking() join campaign in db.ReleaseCampaigns.AsNoTracking() on approval.CampaignId equals campaign.Id where campaign.ProjectId==document.ProjectId&&campaign.ReleaseId==document.ReleaseId&&campaign.BaselineId==document.BaselineId&&approval.ApprovedAt!=null&&approval.ApprovedAt<=document.GeneratedAt select new{campaignId=campaign.Id,approval.ApproverId,approval.ApproverName,state=approval.State.ToString(),approval.ApprovedAt}).OrderBy(x=>x.ApprovedAt).ToListAsync(ct);return Results.Ok(new{format="AeroLink controlled-publication-manifest/v1",document=new{document.Id,document.DocumentNumber,document.Revision,document.Title,type=document.Type.ToString(),document.ContentHash,document.ArtifactCount,document.GeneratedAt},source=new{baseline=baseline.DisplayNumber,baseline.ContentHash,baseline.RequirementsHash,release=release.Version},approvalEvidence=approvals,reproducibility=new{renderer="AeroLink professional publication renderer",contentHash=document.ContentHash,deterministic=true}});
});

app.MapGet("/api/traceability/{baselineId:guid}/download", async (Guid baselineId,string? format,HttpContext http,AeroLinkDbContext db,ControlledOutputGenerator generator,CancellationToken ct) =>
{
    var projectId=await db.CandidateBaselines.Where(x=>x.Id==baselineId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
    var output=await generator.GenerateTraceabilityAsync(baselineId,format??"pdf",ct);return output is null?Results.NotFound():Results.File(output.Content,output.ContentType,output.FileName);
});

app.MapPost("/api/baselines/{id:guid}/generate-documents", async (Guid id, GenerateDocumentsRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound(); if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the requirement baseline before generating controlled outputs." });
    if(!await http.HasProjectRoleAsync(db,identity,baseline.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();
    if (await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.BaselineId == id && x.State == ReleaseCampaignState.InReview, ct))
        return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct); var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
    var requirementCounts = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id group artifact by artifact.Level into g select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    var testCounts = await db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == project.Id).GroupBy(x => x.Level).Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    var suffix = release.Version.Replace(".", ""); var specs = new[] {
        (ControlledDocumentType.Sysrd,$"SYSRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} System Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.System)),
        (ControlledDocumentType.SwrdHighLevel,$"HLRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} High-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.HighLevel)),
        (ControlledDocumentType.SwrdLowLevel,$"LLRD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} Low-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.LowLevel)),
        (ControlledDocumentType.SystemTestProcedures,$"SYSTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} System Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.System)),
        (ControlledDocumentType.HighLevelTestProcedures,$"HLRTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} HLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.HighLevel)),
        (ControlledDocumentType.LowLevelTestProcedures,$"LLRTD-{int.Parse(suffix):D6}",$"{project.SoftwareProduct} LLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.LowLevel)) };
    var existing = await db.ControlledDocuments.Where(x => x.BaselineId == id).ToListAsync(ct); foreach (var spec in specs.Where(s => existing.All(x => x.Type != s.Item1))) { var content = $"{baseline.RequirementsHash}|{spec.Item1}|{spec.Item4}|{http.UserAccount().UserName}"; var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(); db.ControlledDocuments.Add(new ControlledDocument(project.Id, release.Id, baseline.Id, spec.Item1, spec.Item2, spec.Item3, 0, hash, spec.Item4, DateTimeOffset.UtcNow)); }
    await db.SaveChangesAsync(ct); return Results.Ok(new { generated = await db.ControlledDocuments.CountAsync(x => x.BaselineId == id, ct) });
});

app.MapGet("/api/release-campaigns", async (Guid projectId, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    var campaigns = (await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList(); var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
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

app.MapGet("/api/release-campaigns/{id:guid}", async (Guid id, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == campaign.ReleaseId, ct); var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
    var impacts = await (from impact in db.ImpactDispositions.AsNoTracking().Where(x => x.CampaignId == id) join scr in db.SystemChangeRequests.AsNoTracking() on impact.ScrId equals scr.Id orderby scr.BaseNumber, impact.Kind select new { impact.Id, impact.ScrId, scr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, scr.Title, kind = impact.Kind.ToString(), impact.ArtifactReference, impact.Description, state = impact.State.ToString(), impact.Rationale, impact.DispositionedBy, impact.DispositionedAt }).ToListAsync(ct);
    var changes = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == campaign.ReleaseId).OrderBy(x => x.BaseNumber).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title, type = x.Type.ToString(), state = x.State.ToString(), x.AuthorId, requirementCount = x.RequirementChanges.Count, included = db.BaselineSelections.Any(s => s.BaselineId == baseline.Id && s.ScrId == x.Id) }).ToListAsync(ct);
    return Results.Ok(new { campaign.Id, campaign.Name, state = campaign.State.ToString(), campaign.ProjectId, campaign.ReleaseId, release = release.Version, campaign.BaselineId, baseline = baseline.DisplayNumber, baselineState = baseline.State.ToString(), baseline.RequirementsHash, campaign.SoftwareBuildId, campaign.OwnerId, campaign.CreatedAt, campaign.ReleasedAt, campaign.ReleaseHash,
        readiness = await readiness.CalculateAsync(id, ct), changes, impacts, approvals = campaign.Approvals.OrderBy(x => x.Position).Select(x => new { x.Position, x.ApproverId, x.ApproverName, state = x.State.ToString(), x.ApprovedAt }), events = campaign.Events.OrderByDescending(x => x.OccurredAt).Select(x => new { x.EventType, x.ActorId, x.Detail, x.OccurredAt }) });
});

app.MapPut("/api/impact-dispositions/{id:guid}", async (Guid id, DispositionImpactRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var impact = await db.ImpactDispositions.SingleOrDefaultAsync(x => x.Id == id, ct); if (impact is null) return Results.NotFound();
    var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleAsync(x => x.Id == impact.CampaignId, ct);
    if (!await http.HasProjectAccessAsync(db, campaign.ProjectId, ct)) return Results.Forbid();
    if (campaign.State == ReleaseCampaignState.InReview) return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
    if (campaign.State == ReleaseCampaignState.Released) return Results.BadRequest(new { error = "A released campaign is immutable." });
    try { impact.Disposition(request.State, request.Rationale, http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/release-campaigns/{id:guid}/impact-dispositions", async (Guid id, BulkDispositionImpactRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
    if (campaign.State == ReleaseCampaignState.InReview) return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
    if (campaign.State == ReleaseCampaignState.Released) return Results.BadRequest(new { error = "A released campaign is immutable." });
    var impacts = await db.ImpactDispositions.Where(x => x.CampaignId == id && x.State == ImpactDispositionState.Pending && (request.ScrId == null || x.ScrId == request.ScrId)).ToListAsync(ct);
    if (impacts.Count == 0) return Results.BadRequest(new { error = "No pending impacts match this disposition." });
    try { foreach (var impact in impacts) impact.Disposition(request.State, request.Rationale, http.UserAccount().UserName, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.Ok(new { dispositioned = impacts.Count }); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/reconcile-lifecycle-links", async (Guid id, CampaignActorRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseExecutionService execution, CancellationToken ct) =>
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
        db.ReleaseApprovals.AddRange(campaign.Approvals);
        await db.SaveChangesAsync(ct); return Results.Ok(new{manifestHash});
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/approve", async (Guid id, SignatureRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.Meaning))return Results.BadRequest(new{error="An explicit electronic signature meaning is required."});
    if(string.IsNullOrWhiteSpace(campaign.ReleaseHash)||campaign.ReleaseHash.Length!=64)return Results.Conflict(new{error="Release review is not bound to a valid package manifest.",code="release_manifest_missing"});
    var actor = http.UserAccount(); if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password, ct)) return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
    var programId = await db.Projects.Where(x => x.Id == campaign.ProjectId).Select(x => x.ProgramId).SingleAsync(ct); if (!await identity.HasRoleAsync(actor, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct)) return Results.Forbid();
    try { var now = DateTimeOffset.UtcNow; var contentHash = campaign.ReleaseHash; var complete = campaign.Approve(actor.UserName, now); db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId, "ReleaseCampaign", campaign.Id, campaign.Name, "ApproveRelease", request.Meaning, contentHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct); return Results.Ok(new { complete, manifestHash=contentHash }); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/release", async (Guid id, CompleteReleaseRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, ReleaseReadinessService readiness, ReleaseExecutionService execution, CancellationToken ct) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct); var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    if (!await http.HasProjectRoleAsync(db, identity, campaign.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
    var status = await readiness.CalculateAsync(id, ct); if (!status.ReadyForRelease) return Results.BadRequest(new { error = "Every release-readiness gate must be complete.", blockers = status.Gates.Where(x => !x.Complete).Select(x => x.Name) });
    if (campaign.SoftwareBuildId is null) return Results.BadRequest(new { error = "Select the verified release build." });
    var baseline = await db.CandidateBaselines.Include(x => x.Events).SingleAsync(x => x.Id == campaign.BaselineId, ct); var release = await db.Releases.SingleAsync(x => x.Id == campaign.ReleaseId, ct); var build = await db.SoftwareBuilds.SingleAsync(x => x.Id == campaign.SoftwareBuildId, ct);
    var hash=await execution.ComputeReviewManifestHashAsync(id,ct);if(!string.Equals(campaign.ReleaseHash,hash,StringComparison.OrdinalIgnoreCase))return Results.Conflict(new{error="The release package changed after review began. Cancel and restart release review against the current manifest.",code="release_manifest_changed",reviewedManifestHash=campaign.ReleaseHash,currentManifestHash=hash});
    try { var actor = http.UserAccount().UserName; campaign.Release(build.Id, hash, actor, DateTimeOffset.UtcNow); baseline.MarkReleased(actor, DateTimeOffset.UtcNow); release.MarkReleased(DateTimeOffset.UtcNow); build.MarkReleased(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Ok(new { release = release.Version, build.BuildNumber, releaseHash = hash }); }
    catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
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

app.MapGet("/api/release-comparison", async (Guid projectId, Guid fromReleaseId, Guid toReleaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId && (x.Id == fromReleaseId || x.Id == toReleaseId)).ToListAsync(ct); if (releases.Count != 2) return Results.BadRequest(new { error = "Select two releases from the same project." });
    var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && (x.ReleaseId == fromReleaseId || x.ReleaseId == toReleaseId)).ToListAsync(ct); var fromBaseline = baselines.Where(x => x.ReleaseId == fromReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault(); var toBaseline = baselines.Where(x => x.ReleaseId == toReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault();
    async Task<Dictionary<string, (int Revision, string Statement, string Level)>> Set(Guid? baselineId) { if (baselineId is null) return []; var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id select new { artifact.BaseNumber, revision.Revision, revision.Statement, Level = artifact.Level.ToString() }).ToListAsync(ct); return rows.ToDictionary(x => x.BaseNumber, x => (x.Revision, x.Statement, x.Level)); }
    var effectiveAvailable = toBaseline?.RequirementsMaterializedAt is not null;
    var from = effectiveAvailable ? await Set(fromBaseline?.Id) : []; var to = effectiveAvailable ? await Set(toBaseline?.Id) : []; var keys = from.Keys.Union(to.Keys).OrderBy(x => x).ToList();
    var effective = keys.Select(key => { var hasFrom = from.TryGetValue(key, out var a); var hasTo = to.TryGetValue(key, out var b); var kind = !hasFrom ? "Added" : !hasTo ? "Retired" : a.Revision != b.Revision || a.Statement != b.Statement ? "Modified" : "Unchanged"; return new { baseNumber = key, kind, fromRevision = hasFrom ? a.Revision : (int?)null, toRevision = hasTo ? b.Revision : (int?)null, level = hasTo ? b.Level : a.Level }; }).ToList();
    var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == toReleaseId).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ToListAsync(ct);
    var proposed = requests.SelectMany(scr => scr.RequirementChanges.Select(change => new { scrId = scr.Id, scr = scr.DisplayNumber, scr.Title, state = scr.State.ToString(), type = scr.Type.ToString(), change.DisplayNumber, level = change.Level.ToString(), kind = change.Kind.ToString(), change.Statement })).ToList();
    return Results.Ok(new { fromRelease = releases.Single(x => x.Id == fromReleaseId).Version, toRelease = releases.Single(x => x.Id == toReleaseId).Version,
        fromBaseline = fromBaseline?.DisplayNumber, toBaseline = toBaseline?.DisplayNumber, toMaterialized = effectiveAvailable,
        summary = new { added = effective.Count(x => x.kind == "Added"), modified = effective.Count(x => x.kind == "Modified"), retired = effective.Count(x => x.kind == "Retired"), unchanged = effective.Count(x => x.kind == "Unchanged"), proposed = proposed.Count }, effective, proposed });
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

app.MapGet("/api/dashboard", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor = http.UserAccount();
    if (projectId is not null && !await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
    var allowedProjects = actor.IsAdministrator ? null : await db.Projects.AsNoTracking().Where(x => actor.Programs.Select(p => p.ProgramId).Contains(x.ProgramId)).Select(x => x.Id).ToListAsync(ct);
    var source = db.SystemChangeRequests.AsNoTracking().Where(x => (allowedProjects == null || allowedProjects.Contains(x.ProjectId)) && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId));
    return Results.Ok(new {
        totalScrs = await source.CountAsync(ct),
        draft = await source.CountAsync(x => x.State == ScrState.Draft, ct),
        inReview = await source.CountAsync(x => x.State == ScrState.InReview, ct),
        approved = await source.CountAsync(x => x.State == ScrState.Approved || x.State == ScrState.SelectedForBaseline, ct)
    });
});
app.MapGet("/api/quality/metric-contracts",async(Guid? projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(projectId is not null&&!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();var actor=http.UserAccount();var allowed=actor.IsAdministrator?null:await db.Projects.Where(x=>actor.Programs.Select(p=>p.ProgramId).Contains(x.ProgramId)).Select(x=>x.Id).ToListAsync(ct);var scope=(allowed is null?db.Projects.AsNoTracking():db.Projects.AsNoTracking().Where(x=>allowed.Contains(x.Id))).Where(x=>projectId==null||x.Id==projectId);var ids=await scope.Select(x=>x.Id).ToListAsync(ct);var verificationCoverage=await(from coverage in db.TestCoverage.AsNoTracking() join revision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals revision.Id join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id where ids.Contains(procedure.ProjectId) select coverage.Id).CountAsync(ct);var generated=DateTimeOffset.UtcNow;return Results.Ok(new{generatedAt=generated,scope=new{projectCount=ids.Count,permissionSafe=true},contracts=new[]{new{key="controlled_changes",definition="System change requests in Draft, InReview, Approved, or SelectedForBaseline state.",value=await db.SystemChangeRequests.CountAsync(x=>ids.Contains(x.ProjectId),ct),unit="records"},new{key="open_problem_reports",definition="Problem reports not in Closed state.",value=await db.ProblemReports.CountAsync(x=>ids.Contains(x.ProjectId)&&x.State!=ProblemReportState.Closed,ct),unit="records"},new{key="verification_coverage",definition="Requirement revision-to-test procedure coverage links in the authorized portfolio.",value=verificationCoverage,unit="links"},new{key="controlled_publications",definition="Generated controlled documents for authorized projects.",value=await db.ControlledDocuments.CountAsync(x=>ids.Contains(x.ProjectId),ct),unit="records"}}});
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

app.MapGet("/api/signatures", async (Guid? artifactId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor=http.UserAccount();var query = db.ElectronicSignatures.AsNoTracking().AsQueryable();
    if(!actor.IsAdministrator){var allowed=actor.Programs.Select(x=>x.ProgramId).ToList();query=query.Where(x=>allowed.Contains(x.ProgramId));}
    if (artifactId is not null) query = query.Where(x => x.ArtifactId == artifactId);
    var projected=query.Select(x => new { x.Id, x.ArtifactType, x.ArtifactId, x.ArtifactRevision, x.Action, x.Meaning, x.ContentHash, x.UserName, x.DisplayName, x.SignedAt });
    if(db.Database.IsSqlite()){var rows=await projected.ToListAsync(ct);return Results.Ok(rows.OrderByDescending(x=>x.SignedAt).Take(500));}
    return Results.Ok(await projected.OrderByDescending(x => x.SignedAt).Take(500).ToListAsync(ct));
});

app.MapGet("/api/admin/users", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (!http.UserAccount().IsAdministrator) return Results.Forbid();
    var users = await db.UserAccounts.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct); var memberships = await db.ProgramMemberships.AsNoTracking().ToListAsync(ct);
    return Results.Ok(users.Select(x => new { x.Id, x.UserName, x.DisplayName, x.Email, state = x.State.ToString(), x.LastLoginAt, x.CreatedAt, memberships = memberships.Where(m => m.UserId == x.Id).Select(m => new { m.ProgramId, role = m.Role.ToString() }) }));
});
app.MapPost("/api/admin/users", async (CreateUserRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor = http.UserAccount(); if (!actor.IsAdministrator) return Results.Forbid(); var userName = request.UserName.Trim().ToLowerInvariant(); if (await db.UserAccounts.AnyAsync(x => x.UserName == userName, ct)) return Results.Conflict(new { error = "Username already exists." });
    try { var user = new UserAccount(userName, request.DisplayName, request.Email, IdentityService.HashPassword(request.TemporaryPassword), DateTimeOffset.UtcNow);user.RequirePasswordChange(user.PasswordHash); db.UserAccounts.Add(user); db.SecurityAuditEvents.Add(new("AccountCreated", actor.UserName, userName, "Success", $"Created account for {request.DisplayName}; password rotation is required.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.Created($"/api/admin/users/{user.Id}", new { user.Id, user.UserName, user.DisplayName, user.MustChangePassword }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/admin/users/{id:guid}/memberships", async (Guid id, GrantRoleRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor = http.UserAccount(); if (!actor.IsAdministrator) return Results.Forbid(); if (!await db.UserAccounts.AnyAsync(x => x.Id == id, ct) || !await db.Programs.AnyAsync(x => x.Id == request.ProgramId, ct)) return Results.NotFound();
    if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == id && x.ProgramId == request.ProgramId && x.Role == request.Role, ct)) db.ProgramMemberships.Add(new(id, request.ProgramId, request.Role, actor.UserName, DateTimeOffset.UtcNow));
    db.SecurityAuditEvents.Add(new("RoleGranted", actor.UserName, id.ToString(), "Success", $"Granted {request.Role} for program {request.ProgramId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.NoContent();
});
app.MapDelete("/api/admin/users/{id:guid}/memberships/{programId:guid}/{role}", async (Guid id, Guid programId, ProgramRole role, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor = http.UserAccount(); if (!actor.IsAdministrator) return Results.Forbid();
    var membership = await db.ProgramMemberships.SingleOrDefaultAsync(x => x.UserId == id && x.ProgramId == programId && x.Role == role, ct); if (membership is null) return Results.NotFound();
    db.ProgramMemberships.Remove(membership);
    db.SecurityAuditEvents.Add(new("RoleRevoked", actor.UserName, id.ToString(), "Success", $"Revoked {role} for program {programId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
    await db.SaveChangesAsync(ct); return Results.NoContent();
});
app.MapPost("/api/admin/users/{id:guid}/state", async (Guid id, SetAccountStateRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var actor = http.UserAccount(); if (!actor.IsAdministrator) return Results.Forbid();
    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound(); if (user.Id == actor.Id && !request.Enabled) return Results.BadRequest(new { error = "You cannot disable your own active account." });
    var now = DateTimeOffset.UtcNow; var revokedSessions = 0;
    if (request.Enabled) user.Enable();
    else
    {
        user.Disable(now);
        var sessions = await db.UserSessions.Where(x => x.UserId == id && x.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.Revoke(now);
        revokedSessions = sessions.Count;
    }
    db.SecurityAuditEvents.Add(new(request.Enabled ? "AccountEnabled" : "AccountDisabled", actor.UserName, user.UserName, "Success", $"Account state set to {(request.Enabled ? "Active" : "Disabled")}; revoked {revokedSessions} outstanding session(s).", http.Connection.RemoteIpAddress?.ToString() ?? "local", now)); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
});
app.MapPost("/api/admin/users/{id:guid}/reset-password",async(Guid id,ResetPasswordRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    var actor=http.UserAccount();if(!actor.IsAdministrator)return Results.Forbid();try{var user=await db.UserAccounts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(user is null)return Results.NotFound();user.RequirePasswordChange(IdentityService.HashPassword(request.TemporaryPassword));var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==id&&x.RevokedAt==null).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("AdministratorPasswordReset",actor.UserName,user.UserName,"Success",$"Issued temporary password requiring rotation and revoked {sessions.Count} session(s). Reason: {request.Reason.Trim()}",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);return Results.NoContent();}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/delegations", async (CreateDelegationRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
{
    var actor = http.UserAccount(); if (request.DelegatorUserId != actor.Id && !actor.IsAdministrator) return Results.Forbid(); if (request.DelegatorUserId == request.DelegateUserId) return Results.BadRequest(new { error = "A person cannot delegate a role to themselves." });
    var activeUsers=await db.UserAccounts.AsNoTracking().Where(x=>(x.Id==request.DelegatorUserId||x.Id==request.DelegateUserId)&&x.State==AccountState.Active).Select(x=>x.Id).ToListAsync(ct);
    if(activeUsers.Count!=2)return Results.BadRequest(new{error="Both delegation participants must be active AeroLink users."});
    var members=await db.ProgramMemberships.AsNoTracking().Where(x=>x.ProgramId==request.ProgramId&&(x.UserId==request.DelegatorUserId||x.UserId==request.DelegateUserId)).Select(x=>x.UserId).Distinct().ToListAsync(ct);
    if(members.Count!=2)return Results.BadRequest(new{error="Both delegation participants must belong to the selected Program."});
    if(!await identity.HasRoleAsync(request.DelegatorUserId,request.ProgramId,request.Role,DateTimeOffset.UtcNow,ct))return Results.Forbid();
    try { var delegation = new RoleDelegation(request.ProgramId, request.DelegatorUserId, request.DelegateUserId, request.Role, request.StartsAt, request.EndsAt, request.Reason, actor.UserName, DateTimeOffset.UtcNow); db.RoleDelegations.Add(delegation); db.SecurityAuditEvents.Add(new("DelegationCreated", actor.UserName, request.DelegateUserId.ToString(), "Success", $"Delegated {request.Role} through {request.EndsAt:u}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct); return Results.Created($"/api/delegations/{delegation.Id}", new { delegation.Id }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/delegations/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
{
    var delegation = await db.RoleDelegations.SingleOrDefaultAsync(x => x.Id == id, ct); if (delegation is null) return Results.NotFound();
    var actor = http.UserAccount(); if (!actor.IsAdministrator && actor.Id != delegation.DelegatorUserId) return Results.Forbid();
    if (delegation.RevokedAt is not null) return Results.NoContent();
    delegation.Revoke(DateTimeOffset.UtcNow);
    db.SecurityAuditEvents.Add(new("DelegationRevoked", actor.UserName, delegation.DelegateUserId.ToString(), "Success", $"Revoked delegated {delegation.Role} authority for program {delegation.ProgramId}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
    await db.SaveChangesAsync(ct); return Results.NoContent();
});
app.MapGet("/api/admin/security-audit", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) => http.UserAccount().IsAdministrator
    ? Results.Ok(await db.SecurityAuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(1000).ToListAsync(ct)) : Results.Forbid());

// Enterprise Requirements Workspace: configurable schemas, structured specifications,
// collaboration, saved views, governed bulk operations, redlines, and onboarding.
app.MapGet("/api/enterprise-requirements/workspace", async (Guid projectId, Guid? specificationId, string? search, string? level, string? verification, string? tag,string? state,string? owner,string? sourceScr,Guid? baselineId,bool? openComments,string? sort,int page, int pageSize,
    HttpContext http, AeroLinkDbContext db, EnterpriseRequirementsService enterprise, CancellationToken ct) =>
{
    if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
    var timer=Stopwatch.StartNew();
    await enterprise.SynchronizeProjectAsync(projectId,http.UserAccount().UserName,ct);page=Math.Max(1,page==0?1:page);pageSize=Math.Clamp(pageSize==0?100:pageSize,1,250);
    var artifacts=db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId);
    if(string.Equals(level,"Software",StringComparison.OrdinalIgnoreCase))artifacts=artifacts.Where(x=>x.Level==RequirementLevel.HighLevel||x.Level==RequirementLevel.LowLevel);
    else if(!string.IsNullOrWhiteSpace(level)&&Enum.TryParse<RequirementLevel>(level,true,out var parsedLevel))artifacts=artifacts.Where(x=>x.Level==parsedLevel);
    if(specificationId is not null)artifacts=artifacts.Where(x=>db.SpecificationNodes.Any(n=>n.SpecificationId==specificationId&&n.RequirementArtifactId==x.Id));
    var current=from artifact in artifacts
                join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)
                select new{artifact,revision};
    if(!string.IsNullOrWhiteSpace(search)){var q=search.Trim().ToLower();current=current.Where(x=>x.artifact.BaseNumber.ToLower().Contains(q)||x.revision.Statement.ToLower().Contains(q)||x.revision.Rationale.ToLower().Contains(q));}
    if(!string.IsNullOrWhiteSpace(verification)){var v=verification.Trim().ToLower();current=current.Where(x=>x.revision.VerificationMethod.ToLower()==v);}
    if(!string.IsNullOrWhiteSpace(tag)){var t=tag.Trim().ToLower();current=current.Where(x=>db.RequirementRevisionProfiles.Any(p=>p.RevisionId==x.revision.Id&&p.TagsJson.ToLower().Contains(t)));}
    if(!string.IsNullOrWhiteSpace(state)&&Enum.TryParse<RequirementRevisionState>(state,true,out var parsedState))current=current.Where(x=>x.revision.State==parsedState);
    if(!string.IsNullOrWhiteSpace(owner)){var o=owner.Trim().ToLower();current=current.Where(x=>db.RequirementRevisionProfiles.Any(p=>p.RevisionId==x.revision.Id&&p.AttributesJson.ToLower().Contains(o)));}
    if(!string.IsNullOrWhiteSpace(sourceScr)){var s=sourceScr.Trim().ToLower();current=current.Where(x=>db.SystemChangeRequests.Any(scr=>scr.Id==x.revision.SourceScrId&&(scr.BaseNumber.ToLower().Contains(s)||scr.Title.ToLower().Contains(s))));}
    if(baselineId is not null)current=current.Where(x=>db.BaselineRequirements.Any(b=>b.BaselineId==baselineId&&b.RevisionId==x.revision.Id));
    if(openComments==true)current=current.Where(x=>db.ArtifactComments.Any(c=>c.ArtifactId==x.artifact.Id&&c.ArtifactType=="Requirement"&&c.State==CollaborationState.Open));
    var ordered=sort?.ToLowerInvariant() switch{"updated" when !db.Database.IsSqlite()=>current.OrderByDescending(x=>x.revision.CreatedAt).ThenBy(x=>x.artifact.BaseNumber),"verification"=>current.OrderBy(x=>x.revision.VerificationMethod).ThenBy(x=>x.artifact.BaseNumber),"state"=>current.OrderBy(x=>x.revision.State).ThenBy(x=>x.artifact.BaseNumber),_=>current.OrderBy(x=>x.artifact.BaseNumber)};
    var total=await current.CountAsync(ct);var rows=await ordered.Skip((page-1)*pageSize).Take(pageSize)
        .Select(x=>new{x.artifact.Id,x.artifact.BaseNumber,level=x.artifact.Level.ToString(),revisionId=x.revision.Id,x.revision.Revision,x.revision.Statement,x.revision.Rationale,x.revision.VerificationMethod,state=x.revision.State.ToString(),x.revision.SourceScrId,x.revision.CreatedAt}).ToListAsync(ct);
    var revisionIds=rows.Select(x=>x.revisionId).ToList();var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToDictionaryAsync(x=>x.RevisionId,ct);
    var commentCounts=await db.ArtifactComments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ArtifactType=="Requirement"&&rows.Select(r=>r.Id).Contains(x.ArtifactId)).GroupBy(x=>x.ArtifactId).Select(x=>new{x.Key,Count=x.Count(),Open=x.Count(c=>c.State==CollaborationState.Open)}).ToDictionaryAsync(x=>x.Key,ct);
    var schemas=await db.ArtifactSchemas.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.IsActive).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Key,x.Name,x.AppliesTo,x.Description,x.Version,fields=x.Fields.OrderBy(f=>f.SortOrder).Select(f=>new{f.Id,f.Key,f.Label,type=f.Type.ToString(),f.IsRequired,f.SortOrder,f.OptionsJson})}).ToListAsync(ct);
    var specificationRows=await db.RequirementSpecifications.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Level).Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,nodeCount=db.SpecificationNodes.Count(n=>n.SpecificationId==x.Id&&n.Type==SpecificationNodeType.Requirement)}).ToListAsync(ct);
    var specificationIds=specificationRows.Select(x=>x.Id).ToList();var sectionRows=await db.SpecificationNodes.AsNoTracking().Where(n=>specificationIds.Contains(n.SpecificationId)&&n.Type==SpecificationNodeType.Section).OrderBy(n=>n.Position).Select(n=>new{n.Id,n.SpecificationId,n.Heading,n.Position,count=db.SpecificationNodes.Count(c=>c.ParentId==n.Id)}).ToListAsync(ct);
    var specifications=specificationRows.Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,x.nodeCount,sections=sectionRows.Where(s=>s.SpecificationId==x.Id).Select(s=>new{s.Id,s.Heading,s.Position,s.count})}).ToList();
    var views=await db.SavedRequirementViews.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OwnerId==http.UserAccount().Id||x.IsShared)).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Name,x.QueryJson,x.ColumnsJson,x.IsShared,owned=x.OwnerId==http.UserAccount().Id}).ToListAsync(ct);
    timer.Stop();return Results.Ok(new{page,pageSize,totalCount=total,totalPages=(int)Math.Ceiling(total/(double)pageSize),queryElapsedMs=timer.ElapsedMilliseconds,schemas,specifications,views,items=rows.Select(x=>{profiles.TryGetValue(x.revisionId,out var profile);commentCounts.TryGetValue(x.Id,out var comments);return new{x.Id,x.BaseNumber,displayNumber=$"{x.BaseNumber}.{x.Revision:D2}",x.level,x.revisionId,x.Revision,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.SourceScrId,x.CreatedAt,richText=profile?.RichText??x.Statement,attributesJson=profile?.AttributesJson??"{}",tagsJson=profile?.TagsJson??"[]",commentCount=comments?.Count??0,openCommentCount=comments?.Open??0};})});
});

app.MapGet("/api/enterprise-requirements/{artifactId:guid}", async (Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
{
    var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();
    if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
    var history=await (from r in db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId) join s in db.SystemChangeRequests.AsNoTracking() on r.SourceScrId equals s.Id orderby r.Revision descending select new{r.Id,r.Revision,displayNumber=artifact.BaseNumber+"."+(r.Revision<10?"0":"")+r.Revision,r.Statement,r.Rationale,r.VerificationMethod,state=r.State.ToString(),r.SourceScrId,sourceScr=s.BaseNumber+"."+(s.Revision<10?"0":"")+s.Revision,r.CreatedAt}).ToListAsync(ct);
    var revisionIds=history.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
    var placements=await (from n in db.SpecificationNodes.AsNoTracking().Where(x=>x.RequirementArtifactId==artifactId) join spec in db.RequirementSpecifications.AsNoTracking() on n.SpecificationId equals spec.Id join parent in db.SpecificationNodes.AsNoTracking() on n.ParentId equals parent.Id select new{spec.Id,spec.DocumentNumber,spec.Title,section=parent.Heading,n.Position}).ToListAsync(ct);
    var traces=await db.RequirementTraces.AsNoTracking().CountAsync(x=>revisionIds.Contains(x.SourceRevisionId)||revisionIds.Contains(x.TargetRevisionId),ct);var tests=await db.TestCoverage.AsNoTracking().CountAsync(x=>revisionIds.Contains(x.RequirementRevisionId),ct);
    return Results.Ok(new{artifact.Id,artifact.BaseNumber,level=artifact.Level.ToString(),history=history.Select(x=>new{x.Id,x.Revision,x.displayNumber,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.SourceScrId,x.sourceScr,x.CreatedAt,richText=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.RichText,attributesJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.AttributesJson??"{}",tagsJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.TagsJson??"[]"}),placements,traceCount=traces,testCoverageCount=tests});
});

app.MapGet("/api/enterprise-requirements/{artifactId:guid}/redline",async(Guid artifactId,Guid fromRevisionId,Guid toRevisionId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    var projectId=await db.Requirements.Where(x=>x.Id==artifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
    var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.Id==fromRevisionId||x.Id==toRevisionId)).ToListAsync(ct);if(revisions.Count!=2)return Results.BadRequest(new{error="Select two revisions of the same requirement."});var from=revisions.Single(x=>x.Id==fromRevisionId);var to=revisions.Single(x=>x.Id==toRevisionId);
    var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId).ToListAsync(ct);var fromProfile=profiles.SingleOrDefault(x=>x.RevisionId==fromRevisionId);var toProfile=profiles.SingleOrDefault(x=>x.RevisionId==toRevisionId);
    var files=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId)).ToListAsync(ct);var attachmentChanges=files.Select(x=>new{x.Id,x.LogicalId,x.Version,x.Label,x.OriginalFileName,x.Sha256,kind=x.RevisionId==toRevisionId?"added":"removed"}).ToList();
    return Results.Ok(new{from=from.Revision,to=to.Revision,statement=EnterpriseRequirementsService.Diff(from.Statement,to.Statement),rationale=EnterpriseRequirementsService.Diff(from.Rationale,to.Rationale),richText=EnterpriseRequirementsService.Diff(fromProfile?.RichText??from.Statement,toProfile?.RichText??to.Statement),attributesChanged=(fromProfile?.AttributesJson??"{}")!=(toProfile?.AttributesJson??"{}"),fromAttributes=fromProfile?.AttributesJson??"{}",toAttributes=toProfile?.AttributesJson??"{}",verificationChanged=from.VerificationMethod!=to.VerificationMethod,fromVerification=from.VerificationMethod,toVerification=to.VerificationMethod,attachmentChanges});
});

app.MapGet("/api/enterprise-requirements/{artifactId:guid}/impact",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
    var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId).ToListAsync(ct);var current=revisions.OrderByDescending(x=>x.Revision).First();
    var parents=await (from link in db.RequirementTraces.AsNoTracking().Where(x=>x.SourceRevisionId==current.Id) join revision in db.RequirementRevisions.AsNoTracking() on link.TargetRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
    var children=await (from link in db.RequirementTraces.AsNoTracking().Where(x=>x.TargetRevisionId==current.Id) join revision in db.RequirementRevisions.AsNoTracking() on link.SourceRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
    var tests=await (from coverage in db.TestCoverage.AsNoTracking().Where(x=>x.RequirementRevisionId==current.Id) join revision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals revision.Id join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id select new{procedure.Id,displayNumber=procedure.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,procedure.Title,level=procedure.Level.ToString(),state=revision.State.ToString()}).ToListAsync(ct);
    var baselines=await (from selection in db.BaselineRequirements.AsNoTracking().Where(x=>x.ArtifactId==artifactId) join baseline in db.CandidateBaselines.AsNoTracking() on selection.BaselineId equals baseline.Id join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id select new{baseline.Id,baseline=baseline.BaseNumber+"."+(baseline.Revision<10?"0":"")+baseline.Revision,baseline.Name,state=baseline.State.ToString(),release=release.Version,selection.RevisionId}).ToListAsync(ct);
    var baselineIds=baselines.Select(x=>x.Id).ToList();var builds=await db.SoftwareBuilds.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.BuildNumber,x.Description,state=x.State.ToString()}).ToListAsync(ct);var documents=await db.ControlledDocuments.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.DocumentNumber,x.Revision,x.Title,type=x.Type.ToString(),x.ContentHash}).ToListAsync(ct);
    var activeChanges=await (from change in db.RequirementChanges.AsNoTracking().Where(x=>x.BaseNumber==artifact.BaseNumber) join scr in db.SystemChangeRequests.AsNoTracking() on change.ScrId equals scr.Id where scr.State==ScrState.Draft||scr.State==ScrState.InReview||scr.State==ScrState.Approved select new{scr.Id,displayNumber=scr.BaseNumber+"."+(scr.Revision<10?"0":"")+scr.Revision,scr.Title,state=scr.State.ToString(),kind=change.Kind.ToString(),proposedRevision=change.Revision}).ToListAsync(ct);
    var openComments=await db.ArtifactComments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==CollaborationState.Open,ct);var openAssignments=await db.ArtifactAssignments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==AssignmentState.Open,ct);
    var categories=new[]{new{key="trace",label="Trace relationships",count=parents.Count+children.Count,needsAction=parents.Count+children.Count==0},new{key="verification",label="Verification coverage",count=tests.Count,needsAction=tests.Count==0},new{key="baseline",label="Baselines and builds",count=baselines.Count+builds.Count,needsAction=false},new{key="document",label="Controlled documents",count=documents.Count,needsAction=false},new{key="collaboration",label="Open collaboration",count=openComments+openAssignments,needsAction=openComments+openAssignments>0}};
    return Results.Ok(new{artifact.Id,artifact.BaseNumber,currentRevision=current.Revision,displayNumber=artifact.BaseNumber+"."+(current.Revision<10?"0":"")+current.Revision,parents,children,tests,baselines,builds,documents,activeChanges,openComments,openAssignments,categories});
});

app.MapPost("/api/enterprise-requirements/{artifactId:guid}/propose",async(Guid artifactId,ProposeRequirementChangeRequest request,HttpContext http,AeroLinkDbContext db,IScrRepository repository,IdentityService identity,CancellationToken ct)=>
{
    var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(request.Kind is not (RequirementChangeKind.Modify or RequirementChangeKind.Retire))return Results.BadRequest(new{error="An existing requirement can only be modified or retired."});if(!await http.HasProjectRoleAsync(db,identity,artifact.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==artifact.ProjectId&&!x.IsReleased,ct))return Results.BadRequest(new{error="Select an unreleased target release from this Project."});
    var actor=http.UserAccount().UserName;var current=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId).OrderByDescending(x=>x.Revision).FirstAsync(ct);var profile=await db.RequirementRevisionProfiles.AsNoTracking().SingleOrDefaultAsync(x=>x.RevisionId==current.Id,ct);SystemChangeRequest scr;
    if(request.ExistingScrId is not null){var existing=await repository.GetAsync(request.ExistingScrId.Value,ct);if(existing is null)return Results.NotFound(new{error="The selected Draft SCR/SWCR was not found."});scr=existing;if(scr.ProjectId!=artifact.ProjectId||scr.TargetReleaseId!=request.TargetReleaseId)return Results.BadRequest(new{error="The selected change request has a different Project or release."});}
    else{var type=artifact.Level==RequirementLevel.System?ChangeRequestType.System:ChangeRequestType.Software;var prefix=type==ChangeRequestType.System?"SCR":"SWCR";var numbers=await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==artifact.ProjectId&&x.BaseNumber.StartsWith(prefix+"-")).Select(x=>x.BaseNumber).ToListAsync(ct);var next=numbers.Select(x=>int.TryParse(x[(x.IndexOf('-')+1)..],out var n)?n:0).DefaultIfEmpty().Max()+1;scr=new SystemChangeRequest($"{prefix}-{next:D8}",0,artifact.ProjectId,request.TargetReleaseId,string.IsNullOrWhiteSpace(request.Title)?$"{request.Kind} {artifact.BaseNumber}":request.Title,$"A controlled change is proposed for {artifact.BaseNumber}.",$"Assess parent/child traceability, verification coverage, specifications, baselines, builds, and open collaboration for {artifact.BaseNumber}.",$"Implement the approved {request.Kind.ToString().ToLowerInvariant()} through this exact {prefix} revision.",actor,DateTimeOffset.UtcNow,type);await repository.AddAsync(scr,ct);}
    if(scr.AuthorId!=actor)return Results.Forbid();if(scr.State!=ScrState.Draft)return Results.BadRequest(new{error="Requirement proposals can be added only to a Draft SCR/SWCR."});if(scr.RequirementChanges.Any(x=>x.BaseNumber==artifact.BaseNumber))return Results.Conflict(new{error="This Draft already contains a proposal for the selected requirement."});var dispositions=JsonSerializer.Serialize(new{trace="Pending",verification="Pending",documents="Pending",baseline="Pending",collaboration="Pending"});scr.AddRequirementChange(actor,artifact.BaseNumber,current.Revision+1,artifact.Level,request.Kind,request.Kind==RequirementChangeKind.Retire?"":current.Statement,current.Rationale,current.VerificationMethod,DateTimeOffset.UtcNow,profile?.RichText??current.Statement,profile?.AttributesJson??"{}",dispositions);
    if(!await db.ArtifactWatches.AnyAsync(x=>x.ArtifactId==artifactId&&x.UserName==actor,ct))db.ArtifactWatches.Add(new(artifact.ProjectId,"Requirement",artifactId,actor,actor,DateTimeOffset.UtcNow));try{await repository.SaveAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{error="Another controlled change was created concurrently. Refresh and retry."});}return Results.Created($"/api/scrs/{scr.Id}",new{scr.Id,scr.DisplayNumber,scr.Title});
});

app.MapPost("/api/enterprise-requirements/schemas",async(CreateArtifactSchemaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Administrator))return Results.Forbid();try{var schema=new ArtifactSchemaDefinition(request.ProjectId,request.Key,request.Name,request.AppliesTo,request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ArtifactSchemas.Add(schema);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/schemas/{schema.Id}",new{schema.Id});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/enterprise-requirements/schemas/{id:guid}/fields",async(Guid id,CreateSchemaFieldRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    var schema=await db.ArtifactSchemas.Include(x=>x.Fields).SingleOrDefaultAsync(x=>x.Id==id,ct);if(schema is null)return Results.NotFound();if(!http.UserAccount().IsAdministrator)return Results.Forbid();try{schema.AddField(request.Key,request.Label,request.Type,request.IsRequired,request.SortOrder,request.OptionsJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/enterprise-requirements/specifications",async(CreateSpecificationRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var spec=new RequirementSpecification(request.ProjectId,request.DocumentNumber,request.Title,request.Level,request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementSpecifications.Add(spec);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{spec.Id}",new{spec.Id});}catch(Exception ex)when(ex is DomainException or ArgumentException){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/enterprise-requirements/specifications/{id:guid}/sections",async(Guid id,CreateSectionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    var projectId=await db.RequirementSpecifications.Where(x=>x.Id==id).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,projectId.Value,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();var node=new SpecificationNode(id,request.ParentId,request.Position,SpecificationNodeType.Section,request.Heading,null,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.SpecificationNodes.Add(node);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{id}/sections/{node.Id}",new{node.Id});
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
app.MapPost("/api/enterprise-requirements/comments/{id:guid}/resolve",async(Guid id,ResolveCommentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{var comment=await db.ArtifactComments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(comment is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,comment.ProjectId,ct))return Results.Forbid();try{comment.Resolve(http.UserAccount().UserName,request.Disposition??"",DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

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
{if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();var view=new SavedRequirementView(request.ProjectId,http.UserAccount().Id,request.Name,request.QueryJson,request.ColumnsJson,request.IsShared,DateTimeOffset.UtcNow);db.SavedRequirementViews.Add(view);try{await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/views/{view.Id}",new{view.Id});}catch(DbUpdateException){return Results.Conflict(new{error="A saved view with that name already exists."});}});
app.MapDelete("/api/enterprise-requirements/views/{id:guid}",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var view=await db.SavedRequirementViews.SingleOrDefaultAsync(x=>x.Id==id&&x.OwnerId==http.UserAccount().Id,ct);if(view is null)return Results.NotFound();db.Remove(view);await db.SaveChangesAsync(ct);return Results.NoContent();});

app.MapPost("/api/enterprise-requirements/bulk/preview",async(BulkRequirementRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
    if(request.SpecificationId is not null&&!await db.RequirementSpecifications.AnyAsync(x=>x.Id==request.SpecificationId&&x.ProjectId==request.ProjectId,ct))return Results.BadRequest(new{error="The target specification is not part of this Project."});
    if(request.SectionId is not null&&!await db.SpecificationNodes.AnyAsync(x=>x.Id==request.SectionId&&x.SpecificationId==request.SpecificationId&&x.Type==SpecificationNodeType.Section,ct))return Results.BadRequest(new{error="The target section is not part of this specification."});
    var valid=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId&&request.ArtifactIds.Contains(x.Id)).Select(x=>x.Id).ToListAsync(ct);var payload=JsonSerializer.Serialize(new BulkJobPayload(valid,request.Tag,request.SpecificationId,request.SectionId));var job=new EnterpriseOperationJob(request.ProjectId,"RequirementBulkClassify",payload,valid.Count,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,requested=request.ArtifactIds.Count,valid=valid.Count,rejected=request.ArtifactIds.Count-valid.Count,operation=$"Add tag '{request.Tag}'"+(request.SpecificationId is null?"":" and place in specification")});
});
app.MapPost("/api/enterprise-requirements/bulk/{id:guid}/commit",async(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();if(job.State!=EnterpriseJobState.Preview)return Results.BadRequest(new{error="This bulk job is no longer awaiting commit."});var payload=JsonSerializer.Deserialize<BulkJobPayload>(job.RequestJson)!;var revisions=await db.RequirementRevisions.Where(x=>payload.ArtifactIds.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);var current=revisions.GroupBy(x=>x.ArtifactId).Select(x=>x.First()).ToList();var revisionIds=current.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);foreach(var profile in profiles)profile.AddTag(payload.Tag,http.UserAccount().UserName,DateTimeOffset.UtcNow);
    if(payload.SpecificationId is not null){var parent=payload.SectionId;var existing=(await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.RequirementArtifactId!=null).Select(x=>x.RequirementArtifactId!.Value).ToListAsync(ct)).ToHashSet();var position=await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.ParentId==parent).Select(x=>(int?)x.Position).MaxAsync(ct)??0;foreach(var artifactId in payload.ArtifactIds.Where(x=>!existing.Contains(x)))db.SpecificationNodes.Add(new(payload.SpecificationId.Value,parent,++position,SpecificationNodeType.Requirement,"",artifactId,http.UserAccount().UserName,DateTimeOffset.UtcNow));}
    job.Complete(profiles.Count,0,JsonSerializer.Serialize(new{tagged=profiles.Count,placed=payload.SpecificationId is not null}),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,state=job.State.ToString(),job.SucceededCount,job.ResultJson});
});

app.MapGet("/api/enterprise-requirements/interchange",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var jobs=await db.RequirementInterchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);var mappings=await db.RequirementImportMappings.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);return Results.Ok(new{mappings=mappings.Select(x=>new{x.Id,x.Name,x.MappingJson,x.Version,x.UpdatedAt}),jobs=jobs.OrderByDescending(x=>x.CreatedAt).Take(50).Select(x=>new{x.Id,x.FileName,x.Sha256,x.ValidRows,x.InvalidRows,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.CreatedScrId,x.CompletedAt})});
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

app.MapPost("/api/enterprise-requirements/import/preview",async(Guid projectId,Guid? mappingId,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer))return Results.Forbid();if(!http.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data with a CSV or XLSX file."});var form=await http.Request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty CSV or XLSX file."});if(file.Length>25*1024*1024)return Results.BadRequest(new{error="Import files are limited to 25 MB."});if(!file.FileName.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)&&!file.FileName.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Only CSV and XLSX files are supported."});
    await using var stream=file.OpenReadStream();using var memory=new MemoryStream();await stream.CopyToAsync(memory,ct);var bytes=memory.ToArray();memory.Position=0;IReadOnlyList<InterchangeRequirementRow> parsed;try{parsed=EnterpriseRequirementsService.ParseImport(memory,file.FileName);}catch(Exception ex){return Results.BadRequest(new{error=$"The workbook could not be read: {ex.Message}"});}
    var existing=(await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).Select(x=>x.BaseNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);var duplicates=parsed.GroupBy(x=>x.Identifier,StringComparer.OrdinalIgnoreCase).Where(x=>x.Count()>1).Select(x=>x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var rows=parsed.Select(x=>{var errors=x.Errors.ToList();if(existing.Contains(x.Identifier))errors.Add("Identifier already exists in this Project; use an SCR modification workflow.");if(duplicates.Contains(x.Identifier))errors.Add("Identifier is duplicated in this import.");return x with{Valid=errors.Count==0,Errors=errors};}).ToList();var mapping=mappingId is null?"{\"mode\":\"standard-columns\"}":await db.RequirementImportMappings.Where(x=>x.Id==mappingId&&x.ProjectId==projectId).Select(x=>x.MappingJson).SingleOrDefaultAsync(ct)??"{\"mode\":\"standard-columns\"}";var job=new RequirementInterchangeJob(projectId,file.FileName,EnterpriseRequirementsService.Hash(bytes),mapping,JsonSerializer.Serialize(rows),rows.Count(x=>x.Valid),rows.Count(x=>!x.Valid),http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementInterchangeJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,job.FileName,job.Sha256,total=rows.Count,job.ValidRows,job.InvalidRows,rows=rows.Take(200)});
}).DisableAntiforgery();
app.MapPost("/api/enterprise-requirements/import/{id:guid}/commit",async(Guid id,CommitImportRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{
    var job=await db.RequirementInterchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(job.InvalidRows>0)return Results.BadRequest(new{error="Resolve every invalid row before committing this import."});if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();var rows=JsonSerializer.Deserialize<List<InterchangeRequirementRow>>(job.RowsJson)??[];try{var now=DateTimeOffset.UtcNow;var baseNumber=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,ct);var scr=new SystemChangeRequest(baseNumber,0,job.ProjectId,request.TargetReleaseId,request.Title,request.Problem,request.Analysis,request.Solution,http.UserAccount().UserName,now,request.Type);foreach(var row in rows){EnterpriseRequirementsService.TryLevel(row.Level,out var reqLevel);scr.AddRequirementChange(http.UserAccount().UserName,row.Identifier,0,reqLevel,RequirementChangeKind.Introduce,row.Statement,row.Rationale,row.VerificationMethod,now);}db.SystemChangeRequests.Add(scr);job.Commit(scr.Id,now);await db.SaveChangesAsync(ct);return Results.Created($"/api/scrs/{scr.Id}",new{scr.Id,scr.DisplayNumber,imported=rows.Count});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
});

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
    return Results.Ok(new{generatedAt=DateTimeOffset.UtcNow,repository=new{artifacts,revisions,attachments=activeAttachments.Count,attachmentVersions=attachments.Count,attachmentBytes=attachments.Sum(x=>x.Size),missingFiles,views=views.Count},jobs=jobs.Select(x=>new{x.Id,x.JobType,state=x.State.ToString(),x.ItemCount,x.SucceededCount,x.FailedCount,x.ProgressPercent,x.Attempt,x.IdempotencyKey,x.LastError,x.CreatedBy,x.CreatedAt,x.UpdatedAt,x.CompletedAt,x.ResultJson}),sessions=sessions.Select(x=>new{x.Id,x.ArtifactId,x.ArtifactType,x.UserName,x.State,x.OpenedAt,x.UpdatedAt,x.Version}),conflicts=conflicts.Select(x=>new{x.Id,x.ArtifactId,x.LocalSessionId,x.CompetingSessionId,x.CreatedBy,x.CreatedAt}),views,checkpoint=checkpoint is null?null:new{checkpoint.Id,state=checkpoint.State.ToString(),checkpoint.ManifestHash,checkpoint.Detail,checkpoint.CreatedAt,checkpoint.CreatedBy}});
});

app.MapGet("/api/enterprise-hardening/attachments",async(Guid projectId,string artifactType,Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
    var rows=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ArtifactType==artifactType&&x.ArtifactId==artifactId).OrderBy(x=>x.LogicalId).ThenByDescending(x=>x.Version).ToListAsync(ct);
    return Results.Ok(rows.Select(x=>new{x.Id,x.LogicalId,x.Version,x.RevisionId,x.Label,x.Description,x.OriginalFileName,x.ContentType,x.Size,x.Sha256,state=x.State.ToString(),x.UploadedBy,x.UploadedAt,x.IntegrityVerifiedAt,x.SupersedesId}));
});

app.MapPost("/api/enterprise-hardening/attachments",async(HttpRequest request,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
{
    if(!request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data."});var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
    if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty file."});if(!Guid.TryParse(form["projectId"],out var projectId)||!Guid.TryParse(form["artifactId"],out var artifactId))return Results.BadRequest(new{error="Project and artifact identifiers are required."});
    if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var artifactType=string.IsNullOrWhiteSpace(form["artifactType"])?"Requirement":form["artifactType"].ToString();
    if(artifactType!="Requirement"||!await db.Requirements.AnyAsync(x=>x.Id==artifactId&&x.ProjectId==projectId,ct))return Results.BadRequest(new{error="The controlled artifact does not belong to this Project."});
    Guid? revisionId=Guid.TryParse(form["revisionId"],out var parsedRevision)?parsedRevision:null;if(revisionId is not null&&!await db.RequirementRevisions.AnyAsync(x=>x.Id==revisionId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The selected revision does not belong to this requirement."});
    var logicalId=Guid.TryParse(form["logicalId"],out var parsedLogical)?parsedLogical:Guid.NewGuid();var previous=await db.ControlledAttachments.Where(x=>x.ProjectId==projectId&&x.ArtifactId==artifactId&&x.LogicalId==logicalId&&x.State==ControlledAttachmentState.Active).OrderByDescending(x=>x.Version).FirstOrDefaultAsync(ct);
    var stored=await store.StoreAsync(file.OpenReadStream(),file.FileName,file.ContentType,ct);try{previous?.Supersede();var attachment=new ControlledAttachment(projectId,artifactType,artifactId,revisionId,logicalId,(previous?.Version??0)+1,form["label"].ToString(),form["description"].ToString(),stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256,stored.StorageKey,previous?.Id,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ControlledAttachments.Add(attachment);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-hardening/attachments/{attachment.Id}",new{attachment.Id,attachment.LogicalId,attachment.Version,attachment.Sha256});}catch{store.Delete(stored.StorageKey);throw;}
}).DisableAntiforgery();

app.MapGet("/api/enterprise-hardening/attachments/{id:guid}/download",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
{var item=await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();return Results.File(store.OpenRead(item.StorageKey),item.ContentType,item.OriginalFileName,enableRangeProcessing:true);});

app.MapPost("/api/enterprise-hardening/attachments/{id:guid}/verify",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
{var item=await db.ControlledAttachments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var actual=await store.ComputeSha256Async(item.StorageKey,ct);var valid=CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual),Convert.FromHexString(item.Sha256));if(valid){item.RecordIntegrityVerification(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);}return Results.Ok(new{valid,expected=item.Sha256,actual,verifiedAt=item.IntegrityVerifiedAt});});

app.MapPost("/api/enterprise-hardening/jobs",async(CreateEnterpriseJobRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();var key=string.IsNullOrWhiteSpace(request.IdempotencyKey)?Guid.NewGuid().ToString("N"):request.IdempotencyKey.Trim();var existing=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==request.ProjectId&&x.IdempotencyKey==key,ct);if(existing is not null)return Results.Ok(new{existing.Id,state=existing.State.ToString(),reused=true});
    var type=request.JobType is "RepositoryExport"?"BackgroundRepositoryExport":"BackgroundIntegrityScan";var count=await db.Requirements.CountAsync(x=>x.ProjectId==request.ProjectId,ct);var job=new EnterpriseOperationJob(request.ProjectId,type,request.RequestJson,count,http.UserAccount().UserName,DateTimeOffset.UtcNow,key);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Accepted($"/api/enterprise-hardening/jobs/{job.Id}",new{job.Id,state=job.State.ToString(),reused=false});
});
app.MapPost("/api/enterprise-hardening/jobs/{id:guid}/retry",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();try{job.Retry(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Accepted();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});
app.MapPost("/api/enterprise-hardening/jobs/{id:guid}/cancel",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();try{job.Cancel(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});
app.MapGet("/api/enterprise-hardening/jobs/{id:guid}/download",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>{var job=await db.EnterpriseOperationJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();if(job.State!=EnterpriseJobState.Completed)return Results.BadRequest(new{error="The output is not complete."});using var result=JsonDocument.Parse(job.ResultJson);if(!result.RootElement.TryGetProperty("StorageKey",out var storage)&&!result.RootElement.TryGetProperty("storageKey",out storage))return Results.BadRequest(new{error="This job has no downloadable output."});var fileName=result.RootElement.TryGetProperty("OriginalFileName",out var name)||result.RootElement.TryGetProperty("originalFileName",out name)?name.GetString():$"aerolink-export-{id:N}.bin";var contentType=result.RootElement.TryGetProperty("ContentType",out var type)||result.RootElement.TryGetProperty("contentType",out type)?type.GetString():"application/octet-stream";return Results.File(store.OpenRead(storage.GetString()!),contentType??"application/octet-stream",fileName,enableRangeProcessing:true);});

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
app.MapGet("/api/edit-sessions/status",async(string artifactType,Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(!artifactType.Equals("SCR",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="This controlled editor currently supports SCR and SWCR Drafts."});var scr=await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(scr is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,scr.ProjectId,ct))return Results.Forbid();var now=DateTimeOffset.UtcNow;var sessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==artifactId&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in sessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);if(db.ChangeTracker.HasChanges())await db.SaveChangesAsync(ct);var active=sessions.FirstOrDefault(x=>x.State==EditSessionState.Active);return Results.Ok(active is null?new{editable=scr.State==ScrState.Draft,locked=false}:new{editable=scr.State==ScrState.Draft,locked=true,sessionId=active.Id,holder=active.UserName,openedAt=active.OpenedAt,lastActivityAt=active.UpdatedAt,expiresAt=active.ExpiresAt,mine=active.UserName==http.UserAccount().UserName});
});
app.MapPost("/api/edit-sessions/checkout",async(CheckoutEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(!request.ArtifactType.Equals("SCR",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="This controlled editor currently supports SCR and SWCR Drafts."});var scr=await db.SystemChangeRequests.Include(x=>x.RequirementChanges).SingleOrDefaultAsync(x=>x.Id==request.ArtifactId,ct);if(scr is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,scr.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount();if(scr.State!=ScrState.Draft)return Results.Conflict(new{error="Approved, frozen, or in-review change requests cannot be checked out for editing."});if(scr.AuthorId!=actor.UserName&&!actor.IsAdministrator)return Results.Forbid();var now=DateTimeOffset.UtcNow;var sessions=await db.ArtifactEditSessions.Where(x=>x.ArtifactId==scr.Id&&x.ArtifactType=="SCR"&&x.IsExclusive&&x.State==EditSessionState.Active).ToListAsync(ct);foreach(var expired in sessions.Where(x=>x.ExpiresAt<=now))expired.Expire(now);await db.SaveChangesAsync(ct);var active=sessions.FirstOrDefault(x=>x.State==EditSessionState.Active);if(active is not null){if(active.UserName==actor.UserName){var latest=await db.ArtifactDraftSnapshots.AsNoTracking().Where(x=>x.SessionId==active.Id).OrderByDescending(x=>x.Sequence).FirstOrDefaultAsync(ct);return Results.Ok(EditSessionMap(active,latest?.DraftJson??active.DraftJson,true));}return Results.Conflict(new{error=$"{active.UserName} has this artifact checked out.",code="exclusive_lock",holder=active.UserName,active.OpenedAt,lastActivityAt=active.UpdatedAt,active.ExpiresAt,readOnly=true});}
    var draft=ControlledScrDraft(scr);var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(draft));var session=new ArtifactEditSession(scr.ProjectId,"SCR",scr.Id,null,hash,draft,actor.UserName,now,true,request.LeaseMinutes??15);db.ArtifactEditSessions.Add(session);db.ArtifactDraftSnapshots.Add(new(scr.ProjectId,session.Id,"SCR",scr.Id,1,draft,hash,actor.UserName,now));db.AuditEvents.Add(new(scr.Id,"ArtifactCheckedOut",actor.UserName,$"Checked out {scr.DisplayNumber} until {session.ExpiresAt:O}.",now));try{await db.SaveChangesAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{error="Another user obtained the edit lock first. Refresh to see the current holder.",code="exclusive_lock"});}return Results.Created($"/api/edit-sessions/{session.Id}",EditSessionMap(session,draft,false));
});
app.MapPut("/api/edit-sessions/{id:guid}/autosave",async(Guid id,AutosaveEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{
    if(Encoding.UTF8.GetByteCount(request.DraftJson)>2_000_000)return Results.BadRequest(new{error="The recoverable draft exceeds the 2 MB controlled autosave limit."});try{using var parsed=JsonDocument.Parse(request.DraftJson);if(parsed.RootElement.ValueKind!=JsonValueKind.Object)return Results.BadRequest(new{error="The autosave payload must be a JSON object."});}catch(JsonException){return Results.BadRequest(new{error="The autosave payload is not valid JSON."});}var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();try{var now=DateTimeOffset.UtcNow;session.Save(request.DraftJson,request.ExpectedVersion,now,request.LeaseMinutes??15);var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(request.DraftJson));db.ArtifactDraftSnapshots.Add(new(session.ProjectId,session.Id,session.ArtifactType,session.ArtifactId,session.Version,request.DraftJson,hash,http.UserAccount().UserName,now));await db.SaveChangesAsync(ct);return Results.Ok(new{session.Id,session.Version,session.UpdatedAt,session.ExpiresAt,status="Saved",hash});}catch(DomainException ex){return Results.Conflict(new{error=ex.Message,code="edit_session_conflict"});}
});
app.MapPost("/api/edit-sessions/{id:guid}/heartbeat",async(Guid id,HeartbeatEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();try{session.Heartbeat(request.ExpectedVersion,DateTimeOffset.UtcNow,request.LeaseMinutes??15);await db.SaveChangesAsync(ct);return Results.Ok(new{session.Id,session.Version,session.UpdatedAt,session.ExpiresAt});}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}});
app.MapPost("/api/edit-sessions/{id:guid}/discard",async(Guid id,CloseEditSessionRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
{var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct)||session.UserName!=http.UserAccount().UserName)return Results.Forbid();try{var now=DateTimeOffset.UtcNow;session.Close(EditSessionState.Abandoned,request.ExpectedVersion,now,http.UserAccount().UserName,string.IsNullOrWhiteSpace(request.Reason)?"Draft checkout discarded.":request.Reason);db.AuditEvents.Add(new(session.ArtifactId,"EditSessionDiscarded",http.UserAccount().UserName,session.ClosedReason??"Draft checkout discarded.",now));await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}});
app.MapPost("/api/edit-sessions/{id:guid}/force-unlock",async(Guid id,ForceUnlockEditSessionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
{var session=await db.ArtifactEditSessions.SingleOrDefaultAsync(x=>x.Id==id&&x.IsExclusive,ct);if(session is null)return Results.NotFound();var actor=http.UserAccount();if(!await http.HasProjectAccessAsync(db,session.ProjectId,ct))return Results.Forbid();if(!actor.IsAdministrator&&!await http.HasProjectRoleAsync(db,identity,session.ProjectId,ct,ProgramRole.ConfigurationManager))return Results.Forbid();try{var now=DateTimeOffset.UtcNow;session.ForceUnlock(actor.UserName,request.Reason,now);db.AuditEvents.Add(new(session.ArtifactId,"EditSessionForceUnlocked",actor.UserName,$"Force-unlocked {session.ArtifactType} held by {session.UserName}. Reason: {request.Reason}",now));db.SecurityAuditEvents.Add(new("ForcedUnlock",actor.UserName,$"{session.ArtifactType}:{session.ArtifactId}","Success",request.Reason,http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

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
    if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();var artifactCount=await db.Requirements.CountAsync(x=>x.ProjectId==request.ProjectId,ct);var revisionCount=await(from revision in db.RequirementRevisions join artifact in db.Requirements.Where(x=>x.ProjectId==request.ProjectId) on revision.ArtifactId equals artifact.Id select revision.Id).CountAsync(ct);var attachments=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId).ToListAsync(ct);var failedJobs=await db.EnterpriseOperationJobs.CountAsync(x=>x.ProjectId==request.ProjectId&&x.State==EnterpriseJobState.Failed,ct);var conflicts=await db.ArtifactMergeConflicts.CountAsync(x=>x.ProjectId==request.ProjectId&&x.ResolvedAt==null,ct);var missing=attachments.Count(x=>!store.Exists(x.StorageKey));var material=$"{request.ProjectId:N}|{artifactCount}|{revisionCount}|{attachments.Count}|{attachments.Sum(x=>x.Size)}|{failedJobs}|{conflicts}|{missing}";var hash=EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(material));var state=missing>0?IntegrityCheckpointState.Failed:failedJobs+conflicts>0?IntegrityCheckpointState.Attention:IntegrityCheckpointState.Healthy;var checkpoint=new EnterpriseIntegrityCheckpoint(request.ProjectId,artifactCount,revisionCount,attachments.Count,attachments.Sum(x=>x.Size),failedJobs,conflicts,hash,state,$"{missing} missing file(s); {failedJobs} failed job(s); {conflicts} open merge conflict(s).",http.UserAccount().UserName,DateTimeOffset.UtcNow);db.EnterpriseIntegrityCheckpoints.Add(checkpoint);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-hardening/integrity-checkpoints/{checkpoint.Id}",new{checkpoint.Id,state=checkpoint.State.ToString(),checkpoint.ManifestHash,checkpoint.Detail});
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

static string ControlledScrDraft(SystemChangeRequest scr)=>JsonSerializer.Serialize(new{scrVersion=scr.Version,title=scr.Title,problem=scr.Problem,analysis=scr.Analysis,solution=scr.Solution,requirementChanges=scr.RequirementChanges.Select(x=>new{baseNumber=x.BaseNumber,revision=x.Revision,level=x.Level.ToString(),kind=x.Kind.ToString(),statement=x.Statement,rationale=x.Rationale,verificationMethod=x.VerificationMethod,richText=x.RichText,attributesJson=x.AttributesJson,impactDispositionJson=x.ImpactDispositionJson})});
static object EditSessionMap(ArtifactEditSession session,string draftJson,bool resumed)=>new{session.Id,session.ArtifactType,session.ArtifactId,session.Version,session.UserName,session.OpenedAt,lastActivityAt=session.UpdatedAt,session.ExpiresAt,session.BaseSnapshotHash,draftJson,resumed,readOnly=false,status="Saved"};

static string? BootstrapSecret(IConfiguration configuration)
{
    var value = configuration["Identity:BootstrapSecret"]?.Trim();
    return value is { Length: >= 32 } ? value : null;
}

static bool FixedTimeSecretEquals(string supplied, string configured)
{
    if (string.IsNullOrWhiteSpace(supplied)) return false;
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
}

static string? BootstrapPasswordError(string password)
{
    if (string.IsNullOrWhiteSpace(password) || password.Length < 14)
        return "The bootstrap administrator password must contain at least 14 characters.";
    if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || !password.Any(ch => !char.IsLetterOrDigit(ch)))
        return "The bootstrap administrator password must include uppercase, lowercase, numeric, and symbol characters.";
    return null;
}

app.MapAeroLinkIntegrationEndpoints();
app.MapAeroLinkReqIfEndpoints();
app.MapProductLineConfigurationEndpoints();

app.Run();

public partial class Program { }

public sealed class BrowserMutationProtector(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider)
{
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector=provider.CreateProtector("AeroLink.BrowserMutation.v1");
    public string Issue(string sessionToken)=>_protector.Protect(sessionToken);
    public bool Validate(string? sessionToken,string? requestToken)
    {
        if(string.IsNullOrWhiteSpace(sessionToken)||string.IsNullOrWhiteSpace(requestToken))return false;
        try{var protectedSession=_protector.Unprotect(requestToken);var left=Encoding.UTF8.GetBytes(sessionToken);var right=Encoding.UTF8.GetBytes(protectedSession);return left.Length==right.Length&&CryptographicOperations.FixedTimeEquals(left,right);}catch(CryptographicException){return false;}
    }
}

public sealed class AeroLinkAuthorizationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AeroLinkContext";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) { Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) { Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
}

record LoginRequest(string UserName, string Password,string? MfaCode=null);
record ChangeOwnPasswordRequest(string CurrentPassword,string NewPassword);
record ConfirmMfaRequest(string Code);
record DisableMfaRequest(string Password,string Code);
record ResetPasswordRequest(string TemporaryPassword,string Reason);
record BootstrapAdministratorRequest(string DisplayName, string Email, string Password);
record CreateScrRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, ChangeRequestType Type = ChangeRequestType.System);
record DraftRequirementRequest(string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod,string RichText="",string AttributesJson="{}",string ImpactDispositionJson="{}",bool IsDerived=false);
record CreateScrDraftRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, List<DraftRequirementRequest> RequirementChanges, ChangeRequestType Type = ChangeRequestType.System);
record CreateWorkspaceRequest(string ProgramName, string ProgramCode, string ProjectName, string SoftwareProduct, string InitialRelease, bool InitialReleaseIsReleased);
record CreateReleaseRequest(Guid ProjectId, string Version, Guid? PredecessorReleaseId);
record RetargetScrRequest(Guid TargetReleaseId, string Reason);
record RequirementChangeRequest(string ActorId, string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record ApproverRequest(string UserId, string Name);
record SubmitReviewRequest(string ActorId, long? ExpectedVersion, List<ApproverRequest> Approvers, ReviewMode Mode=ReviewMode.Sequential);
record ActorRequest(string ActorId, long? ExpectedVersion);
record SignatureRequest(string Password, string Meaning, long? ExpectedVersion);
record RequestChangesRequest(string ActorId, long? ExpectedVersion, string Reason);
record CreateBaselineRequest(string BaseNumber, int Revision, Guid ProjectId, Guid ReleaseId, Guid? PredecessorBaselineId, string Name, string ActorId);
record CreateReleaseCampaignRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string Name);
record BaselineSelectionRequest(Guid ScrId, string ActorId);
record BaselineActorRequest(string ActorId);
record CreateBuildRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string BuildNumber, string Description, string RecordedBy);
record CreateTestProcedureRequest(Guid ProjectId, string BaseNumber, string Title, string OwnerId, string Objective, string Preconditions, string Steps, string ExpectedResult, List<Guid> RequirementRevisionIds, TestProcedureLevel Level = TestProcedureLevel.HighLevel);
record RecordTestExecutionRequest(Guid ProjectId, Guid ProcedureRevisionId, Guid? SoftwareBuildId, Guid? RetestOfExecutionId, TestOutcome Outcome, string ExecutedBy, string Configuration, string Determination, string EvidenceReference, DateTimeOffset ExecutedAt);
record DispositionImpactRequest(ImpactDispositionState State, string Rationale, string ActorId);
record BulkDispositionImpactRequest(Guid? ScrId, ImpactDispositionState State, string Rationale, string ActorId);
record SelectBuildRequest(Guid SoftwareBuildId, string ActorId);
record CampaignActorRequest(string ActorId);
record StartReleaseReviewRequest(string ActorId, List<ApproverRequest> Approvers);
record CompleteReleaseRequest(string ActorId);
record CreateTraceLinkRequest(Guid ProjectId, Guid SourceRevisionId, Guid TargetRevisionId, RequirementTraceType Type, string Rationale);
record GenerateDocumentsRequest(string ActorId);
record CreateUserRequest(string UserName, string DisplayName, string Email, string TemporaryPassword);
record GrantRoleRequest(Guid ProgramId, ProgramRole Role);
record SetAccountStateRequest(bool Enabled);
record CreateDelegationRequest(Guid ProgramId, Guid DelegatorUserId, Guid DelegateUserId, ProgramRole Role, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);
record CreateArtifactSchemaRequest(Guid ProjectId,string Key,string Name,string AppliesTo,string Description);
record CreateSchemaFieldRequest(string Key,string Label,SchemaFieldType Type,bool IsRequired,int SortOrder,string OptionsJson);
record CreateSpecificationRequest(Guid ProjectId,string DocumentNumber,string Title,string Level,string Description);
record CreateSectionRequest(Guid? ParentId,int Position,string Heading);
record CreateCommentRequest(Guid? RevisionId,Guid? ParentCommentId,string Body,List<string>? Mentions);
record ResolveCommentRequest(string? Disposition);
record CreateSavedViewRequest(Guid ProjectId,string Name,string QueryJson,string ColumnsJson,bool IsShared);
record BulkRequirementRequest(Guid ProjectId,List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record BulkJobPayload(List<Guid> ArtifactIds,string Tag,Guid? SpecificationId,Guid? SectionId);
record CommitImportRequest(Guid TargetReleaseId,string BaseNumber,string Title,string Problem,string Analysis,string Solution,ChangeRequestType Type=ChangeRequestType.Software);
record ProposeRequirementChangeRequest(Guid TargetReleaseId,RequirementChangeKind Kind,string? Title,Guid? ExistingScrId);
record CreateAssignmentRequest(string AssignedTo,string Title,string Description,DateTimeOffset? DueAt,Guid? CommentId);
record CompleteAssignmentRequest(long ExpectedVersion);
record CreateImportMappingRequest(Guid ProjectId,string Name,string MappingJson);
record CreateEnterpriseJobRequest(Guid ProjectId,string JobType,string RequestJson,string? IdempotencyKey);
record OpenEditSessionRequest(Guid ProjectId,Guid ArtifactId);
record SaveEditSessionRequest(long ExpectedVersion,string DraftJson);
record ResolveMergeConflictRequest(string ResolutionJson);
record CheckoutEditSessionRequest(string ArtifactType,Guid ArtifactId,int? LeaseMinutes=null);
record AutosaveEditSessionRequest(long ExpectedVersion,string DraftJson,int? LeaseMinutes=null);
record HeartbeatEditSessionRequest(long ExpectedVersion,int? LeaseMinutes=null);
record CloseEditSessionRequest(long ExpectedVersion,string? Reason=null);
record ForceUnlockEditSessionRequest(string Reason);
record CreateIntegrityCheckpointRequest(Guid ProjectId);
record PerformanceSample(string Name,long TargetMs,long P95Ms,bool Passed,List<long> Timings);
record SearchResultDto(Guid Id,string Kind,string Identifier,string Title,string State,string Discipline,DateTimeOffset? UpdatedAt);
record RelatedArtifactDto(string Kind,Guid Id,string Identifier,string Title);

static class IdentityHttpExtensions
{
    public static AuthenticatedUser UserAccount(this HttpContext context) => context.Items.TryGetValue("AeroLink.User", out var value) && value is AuthenticatedUser user
        ? user : throw new InvalidOperationException("Authenticated user context is unavailable.");
    public static async Task<bool> HasProjectRoleAsync(this HttpContext context, AeroLinkDbContext db, IdentityService identity, Guid projectId, CancellationToken ct, params ProgramRole[] roles)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct); if (programId is null) return false;
        foreach (var role in roles) if (await identity.HasRoleAsync(context.UserAccount(), programId.Value, role, DateTimeOffset.UtcNow, ct)) return true;
        return false;
    }
    public static async Task<bool> HasProjectAccessAsync(this HttpContext context, AeroLinkDbContext db, Guid projectId, CancellationToken ct)
    {
        var actor=context.UserAccount();if(actor.IsAdministrator)return true;
        var programId=await db.Projects.Where(x=>x.Id==projectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        return programId is not null&&actor.Programs.Any(x=>x.ProgramId==programId.Value);
    }
}

static class IdentifierAllocator
{
    public static async Task<string> NextChangeRequestAsync(AeroLinkDbContext db, ChangeRequestType type, CancellationToken ct)
    {
        var prefix = type == ChangeRequestType.System ? "SCR" : "SWCR";
        var numbers = await db.SystemChangeRequests.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return FormatChangeRequest(prefix, Max(numbers, prefix) + 1);
    }

    public static async Task<string> NextRequirementAsync(AeroLinkDbContext db, string prefix, CancellationToken ct)
    {
        var authoritative = await db.Requirements.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        var proposed = await db.RequirementChanges.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return Format(prefix, Math.Max(Max(authoritative, prefix), Max(proposed, prefix)) + 1);
    }

    public static async Task<string> NextTestProcedureAsync(AeroLinkDbContext db, TestProcedureLevel level, CancellationToken ct)
    {
        var prefix = level switch { TestProcedureLevel.System => "SYSTP", TestProcedureLevel.HighLevel => "HLRTP", _ => "LLRTP" };
        var numbers = await db.TestProcedures.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber).ToListAsync(ct);
        return Format(prefix, Max(numbers, prefix) + 1);
    }

    public static async Task<string> NextProblemReportAsync(AeroLinkDbContext db, CancellationToken ct)
    {
        var numbers = await db.ProblemReports.AsNoTracking().Where(x => x.ReportNumber.StartsWith("PR-")).Select(x => x.ReportNumber).ToListAsync(ct);
        return $"PR-{Max(numbers, "PR") + 1:D5}";
    }

    public static int Sequence(string number) => int.TryParse(number[(number.LastIndexOf('-') + 1)..], out var value) ? value : 1;
    public static string Format(string prefix, int sequence) => $"{prefix}-{sequence:D6}";
    private static string FormatChangeRequest(string prefix, int sequence) => $"{prefix}-{sequence:D8}";
    private static int Max(IEnumerable<string> numbers, string prefix) => numbers.Select(x => x.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[(prefix.Length + 1)..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
}

static class DirectoryTitles
{
    public static string For(string userName,IReadOnlyCollection<string> roles)
    {
        if(userName.StartsWith("system.engineer"))return "System Engineer";
        if(userName.StartsWith("software.engineer"))return "Software Engineer";
        if(userName.StartsWith("verification.engineer"))return "Verification Engineer";
        if(userName.StartsWith("systems.lead"))return "Systems Engineering Lead";
        if(userName.StartsWith("software.lead"))return "Software Engineering Lead";
        if(userName.StartsWith("engineering.manager"))return "Engineering Manager";
        if(userName.StartsWith("configuration"))return "Configuration Management Specialist";
        if(roles.Contains("ProgramManager"))return "Program Manager";
        if(roles.Contains("TestEngineer"))return "Test Engineer";
        if(roles.Contains("Approver"))return "Designated Approver";
        if(roles.Contains("Engineer"))return "Engineer";
        return "AeroLink User";
    }
}

static class ProblemReportIntegrationMap
{
    public static string ArtifactKind(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "requirement" => "requirement",
        "changerequest" or "scr" or "swcr" => "change-request",
        "testexecution" => "test-execution",
        "softwarebuild" or "build" => "build",
        "baseline" => "baseline",
        "document" => "document",
        "evidence" => "evidence",
        "release" => "release",
        "problemreport" or "pr" => "problem-report",
        _ => "artifact"
    };

    public static string ArtifactLabel(string artifactType) => artifactType.Trim().ToLowerInvariant() switch
    {
        "changerequest" or "scr" or "swcr" => "Controlled change",
        "testexecution" => "Verification execution",
        "softwarebuild" or "build" => "Software build",
        "problemreport" or "pr" => "Related problem report",
        _ => artifactType
    };
}

static class ApiMap
{
    public static object Workspace(ProgramRecord program, ProjectRecord project, SoftwareRelease release) => new
    {
        program = new { program.Id, program.Name, program.Code },
        project = new { project.Id, project.Name, project.SoftwareProduct },
        release = new { release.Id, release.Version, release.IsReleased }
    };
    public static object ScrSummary(ScrListItem x) => new { x.Id, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Title, state = x.State.ToString(), type = x.Type.ToString(), x.AuthorId, x.TargetReleaseId, x.RequirementCount, x.UpdatedAt };
    public static object ScrDetail(SystemChangeRequest x) => new
    {
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, type = x.Type.ToString(), x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId, x.Version,
        state = x.State.ToString(), x.CreatedAt, x.UpdatedAt,
        requirementChanges = x.RequirementChanges.Select(r => new { r.Id, r.BaseNumber, r.Revision, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.Rationale, r.VerificationMethod,r.RichText,r.AttributesJson,r.ImpactDispositionJson }),
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, mode=c.Mode.ToString(), state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, a.Detail, a.OccurredAt })
    };
    public static object Baseline(CandidateBaseline x) => new { x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count };
    public static object BaselineDetail(CandidateBaseline x, IReadOnlyList<SystemChangeRequest> selected) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, x.PredecessorBaselineId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt,
        selections = selected.OrderBy(scr => scr.DisplayNumber).Select(scr => new
        {
            scr.Id, scr.DisplayNumber, scr.Title,
            requirementChanges = scr.RequirementChanges.OrderBy(r => r.DisplayNumber).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.VerificationMethod })
        }),
        events = x.Events.OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.ActorId, e.Detail, e.OccurredAt })
    };
}
