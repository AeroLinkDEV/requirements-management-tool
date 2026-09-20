using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class ProjectRepositoryEndpoints
{
    public static void MapProjectRepositoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/repository", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var record = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            var canManage = await CanManage(http, db, identity, projectId, ct);
            return Results.Ok(new { repository = record is null ? null : View(record), canManage });
        });

        app.MapPut("/api/projects/{projectId:guid}/repository", async (Guid projectId, ConfigureProjectRepository request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await CanManage(http, db, identity, projectId, ct)) return Results.Forbid();
            if (!Enum.TryParse<ProjectRepositorySetupMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
                return Results.BadRequest(new { error = "Choose Connect now or Configure later." });
            if (mode == ProjectRepositorySetupMode.ConnectNow && !string.Equals(request.Provider, "GitLab", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Choose the supported GitLab provider." });
            await using var writeScope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
            try
            {
                var freshActor = await http.FreshUserForScopeAsync(identity, writeScope, ct);
                if (freshActor is null || !await http.HasFreshProjectRoleAsync(db, identity, writeScope, ct,
                        ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                    return Results.Forbid();
                var record = await db.ProjectRepositoryConfigurations.SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
                var actor = freshActor.UserName;
                var now = DateTimeOffset.UtcNow;
                if (record is null)
                {
                    if (request.ExpectedVersion != 0) return Changed();
                    record = new(projectId, mode, request.Provider, request.Endpoint, actor, now);
                    db.ProjectRepositoryConfigurations.Add(record);
                }
                else
                {
                    if (record.Version != request.ExpectedVersion) return Changed();
                    record.Configure(request.ExpectedVersion, mode, request.Provider, request.Endpoint, actor, now);
                }
                Audit(db, http, projectId, freshActor.UserName, "RepositoryConfigured", new { mode, record.Provider, record.Endpoint, record.Version });
                await db.SaveChangesAsync(ct);
                await writeScope.CommitAsync(ct);
                return Results.Ok(View(record));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Changed(); }
        });

        app.MapPost("/api/projects/{projectId:guid}/repository/verify", async (Guid projectId, VerifyProjectRepository request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, GitLabProjectConnectionProbe probe, CancellationToken ct) =>
        {
            if (!await CanManage(http, db, identity, projectId, ct)) return Results.Forbid();
            var record = await db.ProjectRepositoryConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (record is null || record.Mode != ProjectRepositorySetupMode.ConnectNow)
                return Results.Conflict(new { code = "repository_pending", error = "Configure the repository before verifying its connection." });
            if (record.Version != request.ExpectedVersion) return Changed();
            var probedProvider = record.Provider;
            var probedEndpoint = record.Endpoint;
            // No transaction spans GitLab. The tracked optimistic version rejects an edit made while awaiting it.
            var result = await probe.ProbeAsync(probedProvider, probedEndpoint, ct);
            await using var writeScope = await ProjectControlledWriteScope.AcquireAsync(db, projectId, ct);
            try
            {
                var freshActor = await http.FreshUserForScopeAsync(identity, writeScope, ct);
                if (freshActor is null || !await http.HasFreshProjectRoleAsync(db, identity, writeScope, ct,
                        ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                    return Results.Forbid();
                record = await db.ProjectRepositoryConfigurations.SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
                if (record is null || record.Mode != ProjectRepositorySetupMode.ConnectNow || record.Version != request.ExpectedVersion
                    || !string.Equals(record.Provider, probedProvider, StringComparison.Ordinal)
                    || !string.Equals(record.Endpoint, probedEndpoint, StringComparison.Ordinal))
                    return Changed();
                if (result.Verified)
                    record.RecordVerification(freshActor.UserName, DateTimeOffset.UtcNow, result.RemoteProjectId!.Value, result.RemotePath!);
                else
                    record.RecordVerificationFailure(freshActor.UserName, DateTimeOffset.UtcNow);
                Audit(db, http, projectId, freshActor.UserName, "RepositoryConnectionObserved", new { result.Verified, result.Code, result.RemoteProjectId, result.RemotePath, record.Version });
                await db.SaveChangesAsync(ct);
                // Return the committed provider representation. PostgreSQL stores DateTimeOffset at microsecond
                // precision; the tracked value can retain finer local ticks and would otherwise disagree with
                // the immutable evidence snapshot read by the next request.
                var persisted = await db.ProjectRepositoryConfigurations.AsNoTracking()
                    .SingleAsync(x => x.ProjectId == projectId, ct);
                await writeScope.CommitAsync(ct);
                return Results.Ok(new { repository = View(persisted), observation = result });
            }
            catch (DbUpdateConcurrencyException) { return Changed(); }
        });
    }

    private static Task<bool> CanManage(HttpContext http, AeroLinkDbContext db, IdentityService identity, Guid projectId, CancellationToken ct) =>
        http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator);
    private static IResult Changed() => Results.Conflict(new { code = "repository_changed", error = "Repository setup changed. Refresh and review the current configuration before retrying." });
    private static object View(ProjectRepositoryConfiguration record) => new
    {
        record.ProjectId, mode = record.Mode.ToString(), status = record.Status.ToString(), record.Provider, record.Endpoint,
        record.Version, record.ConfiguredBy, record.ConfiguredAt, record.LastVerifiedAt, record.LastVerifiedBy,
        record.RemoteProjectId, remotePath = record.RemotePathWithNamespace,
        record.LastVerificationFailureAt, record.LastVerificationFailureBy,
    };
    private static void Audit(AeroLinkDbContext db, HttpContext http, Guid projectId, string actorName, string kind, object detail) =>
        db.SecurityAuditEvents.Add(new(kind, actorName, $"Project:{projectId}", "Success",
            JsonSerializer.Serialize(detail), http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
    public sealed record ConfigureProjectRepository(long ExpectedVersion, string Mode, string? Provider, string? Endpoint);
    public sealed record VerifyProjectRepository(long ExpectedVersion);
}
