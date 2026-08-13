using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Notifications;
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
using System.Net;

// How an AeroLink API process is put together, and nothing else.
//
// This file held 154 endpoints alongside the composition, which made the two questions people actually bring
// to it — "what does startup do?" and "where is this route handled?" — both answerable only by reading two
// thousand lines. The endpoints now live in modules named after the part of the lifecycle they serve, in the
// same shape the modules that were already split out use. What is left here is the order things happen in:
// services, then the middleware every request passes through, then the route table.
//
// The middleware order below is load-bearing and worth reading before changing. Authentication and Program
// scope run *before* endpoint routing, which means they decide reachability from the path alone and cannot
// see anything declared on an endpoint.

var builder = WebApplication.CreateBuilder(args);
var restoreValidationReadOnly = builder.Configuration.GetValue<bool>("RestoreValidation:ReadOnly");
var restoreValidationToken = builder.Configuration["RestoreValidation:Token"] ?? "";
if (restoreValidationReadOnly && restoreValidationToken.Length < 32)
    throw new InvalidOperationException("Read-only restore validation requires a one-use token of at least 32 characters.");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAeroLinkInfrastructure(builder.Configuration);
if (restoreValidationReadOnly)
{
    // The validation host exists only to exercise integrity-verifying reads. Suppress every
    // worker so restored metadata cannot be seeded, reconciled, dispatched, or otherwise changed.
    foreach (var worker in builder.Services.Where(x => x.ServiceType == typeof(IHostedService)).ToList())
        builder.Services.Remove(worker);
}
builder.Services.AddAuthentication(AeroLinkAuthorizationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AeroLinkAuthorizationHandler>(AeroLinkAuthorizationHandler.SchemeName, _ => { });
builder.Services.AddSingleton<BrowserMutationProtector>();
var loginRateLimit = Math.Max(1, builder.Configuration.GetValue<int?>("Identity:LoginRateLimitPerMinute") ?? 600);
builder.Services.AddRateLimiter(options =>
{
    // This limiter is flood control for one network address, and nothing more.
    //
    // Guessing at a password is stopped by the account itself, which locks after eight failed attempts no
    // matter where they come from. What this stops is a firehose of requests at the sign-in endpoint.
    //
    // The old default of thirty a minute was written as though one address meant one person. AeroLink is
    // on-premises: an entire engineering group reaches it through one corporate proxy and presents one
    // address, so thirty a minute was a budget shared by the whole site. Measurement at a hundred and fifty
    // users showed a hundred and twenty of them refused at sign-in on an ordinary morning — a denial of
    // service the product inflicted on itself. The default now assumes a site, not a person.
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginRateLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
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
    if (restoreValidationReadOnly)
    {
        if (!await db.Database.CanConnectAsync())
            throw new InvalidOperationException("The read-only restore-validation database is unavailable.");
        if ((await db.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("The restored database schema does not match this AeroLink validation build.");
    }
    else if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    if (!restoreValidationReadOnly && builder.Configuration.GetValue<bool>("DemoData:Enabled"))
    {
        await scope.ServiceProvider.GetRequiredService<FmsShowcaseSeeder>().EnsureSeededAsync();
        // Before the identity seeding below, which grants the demo directory membership of every Program
        // that exists by then. A practice Program created afterwards would have no members at all.
        await scope.ServiceProvider.GetRequiredService<ImportPracticeSeeder>().EnsureSeededAsync();
    }
    if (!restoreValidationReadOnly && seedDemoAccounts)
        await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().EnsureSeededAsync();
    if (!restoreValidationReadOnly && builder.Configuration.GetValue<bool>("DemoData:Enabled"))
        await scope.ServiceProvider.GetRequiredService<ManagedDocumentShowcaseSeeder>().EnsureSeededAsync();
    if (!restoreValidationReadOnly)
        await scope.ServiceProvider.GetRequiredService<EnterpriseWorkspaceSeeder>().EnsureAllAsync();
    // Outside the demo-data guard: every Project has test procedure documents, not only a seeded showcase.
    // Additive and idempotent — it creates what is absent and never moves a procedure somebody has filed.
    if (!restoreValidationReadOnly)
        await scope.ServiceProvider.GetRequiredService<TestProcedureDocumentBootstrap>().EnsureAllAsync();
}

// Whether this process also serves the built client, decided once at startup. Null means it does not, and
// every response is an API response — which is what a deployment serving the client through its own reverse
// proxy relies on, so nothing below changes for it.
var clientRoot = ClientHosting.ResolveClientRoot(app.Environment, builder.Configuration);

// Security headers on every response.
//
// These matter most on the endpoints that stream stored files, where the content type was chosen by whoever
// uploaded the bytes. Where the client is served by a reverse proxy instead, the headers protecting the HTML
// document are that proxy's responsibility and are recorded in SECURITY_AND_IDENTITY_MODEL.md.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Never let a browser second-guess the content type of a file somebody uploaded.
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    // A document and an API need opposite policies, and applying the API's to a document serves a blank page:
    // `default-src 'none'` forbids the bundle from loading at all. Both are in ClientHosting, with the reason
    // each directive is what it is.
    headers["Content-Security-Policy"] = clientRoot is not null && !ClientHosting.IsApiPath(context.Request.Path)
        ? ClientHosting.DocumentContentSecurityPolicy
        : ClientHosting.ApiContentSecurityPolicy;
    await next();
});

// Before the session gate below, so a stylesheet costs no database work. That gate already lets every
// non-API path through, and the entry document has to be reachable unauthenticated in any case — it is
// what draws the sign-in form.
if (clientRoot is not null) app.UseAeroLinkClient(clientRoot);

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var isApi = path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    // This list is the only thing that makes an endpoint reachable without a session. `.AllowAnonymous()`
    // on the endpoint itself does nothing here, because this middleware runs before endpoint routing and
    // has no endpoint metadata to read — which is exactly how the unsubscribe link in every notification
    // email came to return 401 to anybody who clicked it.
    var isAnonymous = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/setup/bootstrap", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/document-connector/", StringComparison.OrdinalIgnoreCase)
        // Reached from a mail client, where the reader is not authenticated. The link proves it was issued
        // by this deployment through an HMAC over the recipient, so anonymity here is the design.
        || path.Equals("/api/notifications/unsubscribe", StringComparison.OrdinalIgnoreCase);
    if (restoreValidationReadOnly)
    {
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) { await next(); return; }
        var supplied = context.Request.Headers["X-AeroLink-Restore-Validation"].ToString();
        var loopback = context.Connection.RemoteIpAddress is not null && IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
        var exactRead = context.Request.Method is "GET" or "HEAD"
            && path.StartsWith("/api/managed-documents/attachments/", StringComparison.OrdinalIgnoreCase);
        var validToken = supplied.Length == restoreValidationToken.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(restoreValidationToken));
        if (!loopback || !exactRead || !validToken)
        {
            context.Response.StatusCode = exactRead ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This isolated restore-validation process permits only authenticated controlled-attachment reads.", code = "restore_validation_read_only" });
            return;
        }
        context.Items["AeroLink.User"] = new AuthenticatedUser(Guid.Empty, "system.restore-validator",
            "Restore Validator", "restore-validator@localhost", true, []);
        await next(); return;
    }
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
    var db=context.RequestServices.GetRequiredService<AeroLinkDbContext>();
    Guid? activeBuildId=null;
    if(context.Request.Headers.TryGetValue("X-AeroLink-Build-Context",out var rawBuildContext))
    {
        if(!Guid.TryParse(rawBuildContext.FirstOrDefault(),out var parsedBuildId))
        {context.Response.StatusCode=StatusCodes.Status400BadRequest;await context.Response.WriteAsJsonAsync(new{error="The active build context is invalid.",code="build_context_invalid"});return;}
        var selectedBuild=await db.Releases.AsNoTracking().Where(x=>x.Id==parsedBuildId)
            .Select(x=>new{x.Id,x.ProjectId,x.IsReleased,x.Version,ProgramId=db.Projects.Where(p=>p.Id==x.ProjectId).Select(p=>p.ProgramId).Single()})
            .SingleOrDefaultAsync(context.RequestAborted);
        if(selectedBuild is null)
        {context.Response.StatusCode=StatusCodes.Status404NotFound;await context.Response.WriteAsJsonAsync(new{error="The selected build no longer exists.",code="build_context_not_found"});return;}
        if(!user.IsAdministrator&&!user.Programs.Any(x=>x.ProgramId==selectedBuild.ProgramId))
        {context.Response.StatusCode=StatusCodes.Status403Forbidden;await context.Response.WriteAsJsonAsync(new{error="You are not authorized to enter this build.",code="build_context_forbidden"});return;}
        if(context.Request.Query.TryGetValue("projectId",out var contextProject)&&Guid.TryParse(contextProject.FirstOrDefault(),out var requestedProject)&&requestedProject!=selectedBuild.ProjectId)
        {context.Response.StatusCode=StatusCodes.Status409Conflict;await context.Response.WriteAsJsonAsync(new{error="The request addresses a different project than the active build.",code="build_project_mismatch"});return;}
        var projectScopedManagedDocumentRequest=path.StartsWith("/api/managed-documents",StringComparison.OrdinalIgnoreCase)
            ||path.StartsWith("/api/document-connector",StringComparison.OrdinalIgnoreCase);
        if(!projectScopedManagedDocumentRequest&&context.Request.Query.TryGetValue("releaseId",out var contextRelease)&&Guid.TryParse(contextRelease.FirstOrDefault(),out var requestedRelease)&&requestedRelease!=selectedBuild.Id)
        {context.Response.StatusCode=StatusCodes.Status409Conflict;await context.Response.WriteAsJsonAsync(new{error="The request addresses a different build than the active workspace.",code="build_context_mismatch"});return;}
        activeBuildId=selectedBuild.Id;

        var primaryMutationPrefixes=new[]{"/api/change-request","/api/baseline","/api/build","/api/requirement","/api/enterprise-requirements","/api/test","/api/evidence","/api/release","/api/document","/api/managed-document","/api/trace","/api/problem-report","/api/downstream-assessment","/api/controlled-editing","/api/edit-sessions","/api/content/images","/api/reqif","/api/publication"};
        // Bringing a program in from another tool is not a mutation of the active build: it creates a new
        // build from a source that is already released (DEC-093). It is named here rather than left to the
        // prefix list because "/api/baseline" is deliberately loose enough to catch "/api/baselines", and so
        // catches "/api/baseline-imports" by accident — which would refuse every import gate with a message
        // about a build the import does not touch.
        var portsAnotherProgramIn=path.StartsWith("/api/baseline-imports",StringComparison.OrdinalIgnoreCase);
        // Problem Reports are Project-scoped (DEC-089), so the build in the browser header cannot make their
        // lifecycle read-only. Keep the exception exact: direct PR routes qualify; universal editing routes
        // qualify only when the authoritative request/session identifies a Problem Report. A forged query
        // parameter therefore cannot turn editing for a build-owned artifact into a Project-scoped mutation.
        var projectScopedProblemReportMutation=path.StartsWith("/api/problem-reports",StringComparison.OrdinalIgnoreCase);
        // Managed Documentation Center records are Project-scoped. A build header may remain while a user
        // follows a legacy deep link, but it cannot make the Project document or its connector workflow read-only.
        var projectScopedManagedDocumentMutation=projectScopedManagedDocumentRequest;
        if(!projectScopedProblemReportMutation&&path.Equals("/api/controlled-editing/checkout",StringComparison.OrdinalIgnoreCase)
            &&context.Request.Method=="POST")
        {
            context.Request.EnableBuffering();
            try
            {
                var checkout=await context.Request.ReadFromJsonAsync<UniversalCheckoutRequest>(cancellationToken:context.RequestAborted);
                projectScopedProblemReportMutation=checkout is not null
                    &&ControlledArtifactEditPolicies.TryResolve(checkout.ArtifactType,out var checkoutPolicy)
                    &&checkoutPolicy.Family==ControlledArtifactFamily.ProblemReport;
            }
            catch(JsonException) { /* The endpoint returns the authoritative malformed-payload response. */ }
            finally { context.Request.Body.Position=0; }
        }
        if(!projectScopedProblemReportMutation&&path.StartsWith("/api/controlled-editing/sessions/",StringComparison.OrdinalIgnoreCase))
        {
            var editSegments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
            projectScopedProblemReportMutation=editSegments.Length>=4&&Guid.TryParse(editSegments[3],out var editSessionId)
                &&await db.ArtifactEditSessions.AsNoTracking().AnyAsync(session=>session.Id==editSessionId
                    &&session.ArtifactType=="ProblemReport",context.RequestAborted);
        }
        var unsafeBuildMutation=!portsAnotherProgramIn&&!projectScopedProblemReportMutation&&!projectScopedManagedDocumentMutation
            &&context.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE")
            &&primaryMutationPrefixes.Any(prefix=>path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
        if(selectedBuild.IsReleased&&unsafeBuildMutation)
        {context.Response.StatusCode=StatusCodes.Status409Conflict;await context.Response.WriteAsJsonAsync(new{error=$"Build {selectedBuild.Version} is released and read-only. Exit this workspace and select an in-work build to make changes.",code="released_build_read_only"});return;}
    }

    Guid? scopedProjectId=null;
    if(context.Request.Query.TryGetValue("projectId",out var rawProject)&&Guid.TryParse(rawProject.FirstOrDefault(),out var queryProject))scopedProjectId=queryProject;
    var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);if(scopedProjectId is null&&segments.Length>=3&&Guid.TryParse(segments[2],out var resourceId))
    {scopedProjectId=segments[1] switch{"baseline-imports"=>await db.BaselineImports.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"change-requests"=>await db.SystemChangeRequests.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"downstream-assessments"=>await db.DownstreamChangeAssessments.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"baselines"=>await db.CandidateBaselines.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"builds"=>await db.SoftwareBuilds.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"requirements"=>await db.Requirements.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"documents"=>await db.ControlledDocuments.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"traceability"=>await db.CandidateBaselines.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"release-campaigns"=>await db.ReleaseCampaigns.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"test-executions"=>await db.TestExecutions.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"trace-links"=>await db.RequirementTraces.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),"evidence"=>await db.EvidenceRecords.Where(x=>x.Id==resourceId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(context.RequestAborted),_=>null};}
    if(scopedProjectId is not null&&!user.IsAdministrator){var programId=await db.Projects.Where(x=>x.Id==scopedProjectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(context.RequestAborted);if(programId is not null&&!user.Programs.Any(x=>x.ProgramId==programId)){context.Response.StatusCode=StatusCodes.Status403Forbidden;await context.Response.WriteAsJsonAsync(new{error="You are not authorized for this Program.",code="program_scope_forbidden"});return;}}
    if(activeBuildId is not null&&segments.Length>=3&&Guid.TryParse(segments[2],out var buildOwnedResourceId))
    {
        Guid? resourceBuildId=segments[1] switch{
            "change-requests"=>await db.SystemChangeRequests.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.TargetReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "downstream-assessments"=>await db.DownstreamChangeAssessments.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "baselines"=>await db.CandidateBaselines.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "builds"=>await db.SoftwareBuilds.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "documents"=>await db.ControlledDocuments.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "release-campaigns"=>await db.ReleaseCampaigns.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            "releases"=>await db.Releases.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.Id).SingleOrDefaultAsync(context.RequestAborted),
            "verification-impact"=>await db.VerificationImpactItems.Where(x=>x.Id==buildOwnedResourceId).Select(x=>(Guid?)x.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            // Baseline imports are deliberately absent too. An import does not belong to a build — it
            // creates one — so there is no build it could disagree with (DEC-093).
            // Problem Reports are deliberately absent from this list. They are one Project-level database read
            // the same from any build (DEC-089): a report names a target build, but that is an attribute of the
            // record rather than the build that owns it, so opening one from another workspace is ordinary
            // rather than a cross-build violation.
            "test-executions"=>await (from execution in db.TestExecutions.Where(x=>x.Id==buildOwnedResourceId)
                join build in db.SoftwareBuilds on execution.SoftwareBuildId equals build.Id into buildRows
                from build in buildRows.DefaultIfEmpty()
                select execution.ReleaseId ?? (Guid?)build.ReleaseId).SingleOrDefaultAsync(context.RequestAborted),
            _=>null};
        if(resourceBuildId is not null&&resourceBuildId!=activeBuildId)
        {context.Response.StatusCode=StatusCodes.Status409Conflict;await context.Response.WriteAsJsonAsync(new{error="This controlled record belongs to a different build. Exit the workspace and select that build explicitly.",code="cross_build_resource"});return;}
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AeroLink API", check="liveness" }));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy", service = "AeroLink API", check="liveness" }));
app.MapGet("/health/ready", async (AeroLinkDbContext db,CancellationToken ct) => await db.Database.CanConnectAsync(ct)?Results.Ok(new{status="ready",service="AeroLink API",database="connected"}):Results.Json(new{status="not_ready",service="AeroLink API",database="unavailable"},statusCode:StatusCodes.Status503ServiceUnavailable));
// The API surface, one module per part of the lifecycle. Registration order does not decide which route
// matches — routing resolves that by precedence — so these read in the order somebody meets the product:
// sign in, find your work, propose a change, freeze it, verify it, release it, administer it.
app.MapAuthEndpoints();
app.MapWorkspaceEndpoints();
app.MapChangeRequestEndpoints();
app.MapDownstreamAssessmentEndpoints();
app.MapBaselineImportEndpoints();
app.MapRequirementsEndpoints();
app.MapCodeTraceabilityEndpoints();
app.MapBaselineEndpoints();
app.MapVerificationEndpoints();
app.MapAeroLinkBuildTestSetEndpoints();
app.MapReleaseCampaignEndpoints();
app.MapManagedDocumentEndpoints();
app.MapEditSessionEndpoints();
app.MapAdministrationEndpoints();
app.MapPersonnelEndpoints();
app.MapApprovalConfigurationEndpoints();
app.MapTestProcedureDocumentEndpoints();

// Modules that already lived in their own files. Three of these register further modules of their own:
// Operations brings external identity and verification impact, Integration brings controlled editing and
// problem reports, and the product-line module brings its completion endpoints.
app.MapAeroLinkOperationsEndpoints();
app.MapAeroLinkPublicationEndpoints();
app.MapAeroLinkQualityIntelligenceEndpoints();
app.MapWorkflowEndpoints();
app.MapJiraEndpoints();

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
