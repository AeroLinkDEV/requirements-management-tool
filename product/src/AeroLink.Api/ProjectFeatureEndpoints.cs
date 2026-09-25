using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// A project's switched-on features (#1113). Every member reads them, because every surface depends on
/// them. Changing them is Project Configuration authority: Configuration Manager, Program Manager or
/// Administrator, with a reason, under the optimistic version.
/// </summary>
public static class ProjectFeatureEndpoints
{
    private static readonly ProgramRole[] ManagingRoles =
        [ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator];

    public static void MapProjectFeatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/features", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectFeatureService service, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var read = await service.ReadAsync(projectId, ct);
            if (read is null) return Results.NotFound(new { error = "That project does not exist." });
            return Results.Ok(Projection(read, await http.HasProjectRoleAsync(db, identity, projectId, ct, ManagingRoles)));
        });

        app.MapPut("/api/projects/{projectId:guid}/features", async (Guid projectId, ProjectFeatureEditRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, ProjectFeatureService service,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, ManagingRoles)) return Results.Forbid();
            var enabled = ProjectFeature.None;
            foreach (var name in request.Enabled ?? [])
            {
                if (!Enum.TryParse<ProjectFeature>(name, ignoreCase: false, out var feature) || feature == ProjectFeature.None
                    || !ProjectFeatures.Each.Contains(feature))
                    return Results.BadRequest(new { error = $"'{name}' is not a project feature." });
                enabled |= feature;
            }
            var result = await service.ChangeAsync(projectId, enabled, request.ExpectedVersion, request.Reason ?? "",
                http.UserAccount().UserName, http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow, ct);
            return result.Kind switch
            {
                ProjectFeatureChangeKind.NotFound => Results.NotFound(new { error = result.Error }),
                ProjectFeatureChangeKind.Invalid => Results.BadRequest(new { error = result.Error }),
                ProjectFeatureChangeKind.Conflict => Results.Conflict(new { error = result.Error }),
                _ => Results.Ok(Projection(result.Read!, canManage: true)),
            };
        });
    }

    /// <summary>
    /// Releases the in-work build of a project that has switched Release off (#1113, DEC-138), so it can move
    /// to a successor build. There is no readiness evidence to release on, so this is a password-confirmed
    /// signed decision with a reason, recorded as released without readiness evidence and never presented as
    /// a readiness-backed release. Where Release is on, the release campaign remains the only way.
    /// </summary>
    public static void MapReleaseWithoutReadinessEndpoint(this WebApplication app)
    {
        app.MapPost("/api/releases/{releaseId:guid}/release-without-readiness", async (Guid releaseId,
            ReleaseWithoutReadinessRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            CancellationToken ct) =>
        {
            var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == releaseId, ct);
            if (release is null) return Results.NotFound(new { error = "That build does not exist." });
            if (!await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            if ((await ProjectFeatureService.EffectiveAsync(db, release.ProjectId, ct)).HasFlag(ProjectFeature.Release))
                return Results.Conflict(new { error = "This project uses Release, so a build is released through its release campaign.", code = "release_feature_enabled" });
            if (release.IsReleased) return Results.Conflict(new { error = $"Build {release.Version} is already released." });
            var reason = request.Reason?.Trim() ?? "";
            if (reason.Length < 10) return Results.BadRequest(new { error = "Say why this build is being released (at least 10 characters)." });
            var actor = http.UserAccount();
            if (!await identity.ConfirmPasswordAsync(actor.Id, request.Password ?? "", ct))
                return Results.Json(new { error = "Electronic signature confirmation failed." }, statusCode: 401);
            var programId = await db.Projects.Where(x => x.Id == release.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var content = $"project={release.ProjectId:D};release={release.Id:D};version={release.Version};basis=WithoutReadinessEvidence;reason={reason};actor={actor.UserName};at={now.UtcDateTime:O}";
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
            release.MarkReleasedWithoutReadiness(now);
            db.ElectronicSignatures.Add(new ElectronicSignature(actor.Id, actor.UserName, actor.DisplayName, programId,
                "SoftwareRelease", release.Id, release.Version, "ReleaseWithoutReadiness",
                "Released without readiness evidence", hash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now,
                rationale: reason));
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("ReleasedWithoutReadiness", actor.UserName, $"Release:{release.Id}",
                "Success", $"Build {release.Version} released without readiness evidence: {reason}",
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { release.Id, release.Version, release.IsReleased, release.ReleasedAt, release.ReleasedWithoutReadiness, signatureHash = hash });
        });
    }

    private static object Projection(ProjectFeatureRead read, bool canManage) => new
    {
        read.Persisted,
        read.Version,
        canManage,
        enabled = ProjectFeatures.Each.Where(f => read.Enabled.HasFlag(f)).Select(x => x.ToString()),
        features = ProjectFeatures.Each.Select(x => new
        {
            id = x.ToString(),
            label = ProjectFeatures.Label(x),
            enabled = read.Enabled.HasFlag(x),
            hasRecords = read.HasRecords[x],
        }),
        history = read.History.Select(x => new
        {
            x.Version,
            previous = ProjectFeatures.Each.Where(f => x.Previous.HasFlag(f)).Select(f => f.ToString()),
            enabled = ProjectFeatures.Each.Where(f => x.Enabled.HasFlag(f)).Select(f => f.ToString()),
            x.Actor, x.Reason, x.OccurredAt, x.SnapshotHash,
        }),
    };
}

public sealed class ProjectFeatureEditRequest
{
    /// <summary>The feature-set version the caller read. Zero means the project has never changed its features.</summary>
    public long ExpectedVersion { get; set; }
    public string? Reason { get; set; }
    /// <summary>The complete set of features that should be on, by name.</summary>
    public List<string>? Enabled { get; set; }
}

public sealed class ReleaseWithoutReadinessRequest
{
    public string? Reason { get; set; }
    public string? Password { get; set; }
}
