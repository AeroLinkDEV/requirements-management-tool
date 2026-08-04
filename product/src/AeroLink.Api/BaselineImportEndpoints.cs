using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record StartBaselineImportRequest(Guid ProjectId, string SourceSystem, string SourceSystemVersion,
    string SourceBaselineName, DateTimeOffset SourceBaselineDate, string ExtractFileName, string ExtractSha256,
    long ExtractSizeBytes, string[] Carries, string ExtractedBy, DateTimeOffset ExtractedAt);
public sealed record RecordMappingRequest(string MappingJson);
public sealed record RecordReconciliationRequest(string ReconciliationJson);
public sealed record AcceptBaselineImportRequest(string Version);

/// <summary>
/// Bringing in a program that already exists in another requirements tool.
///
/// Deliberately not routed through change requests. Nobody here approved these requirements, so this creates
/// a released baseline directly with its provenance rather than manufacturing an approval nobody gave. Each
/// gate refuses to run before the one before it — see DEC-093 and issue #332.
/// </summary>
public static class BaselineImportEndpoints
{
    public static void MapBaselineImportEndpoints(this WebApplication app)
    {
        // Porting a program in is a Program-setup act, not engineering work on a build, so it takes the
        // authority that establishes Projects rather than the authority that proposes changes.
        static Task<bool> AuthorizedAsync(Guid projectId, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
            http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator);

        app.MapGet("/api/baseline-imports", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            // SQLite cannot order a DateTimeOffset server-side, so the list is materialized then sorted.
            var rows = (await db.BaselineImports.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct))
                .OrderByDescending(x => x.StartedAt).ToList();
            return Results.Ok(rows.Select(Summary));
        });

        app.MapGet("/api/baseline-imports/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var import = await db.BaselineImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            var identities = await db.SourceIdentities.AsNoTracking()
                .Where(x => x.BaselineImportId == id).CountAsync(ct);
            return Results.Ok(Detail(import, identities));
        });

        // Gate 1. The extract is accepted and hashed; nothing is parsed yet.
        app.MapPost("/api/baseline-imports", async (StartBaselineImportRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, request.ProjectId, ct)) return Results.Forbid();
            if (!await AuthorizedAsync(request.ProjectId, http, db, identity, ct)) return Results.Forbid();
            var carries = ImportedArtifactKinds.None;
            foreach (var kind in request.Carries ?? [])
            {
                if (!Enum.TryParse<ImportedArtifactKinds>(kind, true, out var parsed) || parsed == ImportedArtifactKinds.None)
                    return Results.BadRequest(new { error = $"'{kind}' is not a kind of record an import can carry." });
                carries |= parsed;
            }
            try
            {
                var import = new BaselineImport(request.ProjectId, request.SourceSystem, request.SourceSystemVersion,
                    request.SourceBaselineName, request.SourceBaselineDate, request.ExtractFileName,
                    request.ExtractSha256, request.ExtractSizeBytes, carries, request.ExtractedBy,
                    request.ExtractedAt, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.BaselineImports.Add(import);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/baseline-imports/{import.Id}", Detail(import, 0));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Gate 2.
        app.MapPost("/api/baseline-imports/{id:guid}/analysis", (Guid id, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.RecordAnalysis(now)));

        // Gate 3. The judgement: modules to levels, attributes to fields, link types to traces.
        app.MapPost("/api/baseline-imports/{id:guid}/mapping", (Guid id, RecordMappingRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.RecordMapping(request.MappingJson, now)));

        // Gate 4. Every source object accounted for, before anything is committed.
        app.MapPost("/api/baseline-imports/{id:guid}/reconciliation", (Guid id, RecordReconciliationRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.RecordReconciliation(request.ReconciliationJson, now)));

        app.MapPost("/api/baseline-imports/{id:guid}/abandon", (Guid id, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.Abandon(now)));

        // Gate 5. A named person accepts it, and the build exists from here.
        app.MapPost("/api/baseline-imports/{id:guid}/accept", async (Guid id, AcceptBaselineImportRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var import = await db.BaselineImports.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            if (!await AuthorizedAsync(import.ProjectId, http, db, identity, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Version))
                return Results.BadRequest(new { error = "Name the build this import becomes, for example 1.0." });
            var version = request.Version.Trim();
            if (await db.Releases.AnyAsync(x => x.ProjectId == import.ProjectId && x.Version == version, ct))
                return Results.Conflict(new { error = $"Build {version} already exists in this Project." });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var release = new SoftwareRelease(import.ProjectId, version, isReleased: false);
                // Accept first, so an import that has not cleared its gates is refused before a build for it
                // exists even in memory.
                import.Accept(http.UserAccount().UserName, release.Id, now);
                // Released on arrival. Readiness gates evaluate a build before it is released, and an
                // imported baseline is already past that: its review, approval and verification are credited
                // to the source's own release, which this product does not claim to have performed.
                release.MarkReleased(now);
                db.Releases.Add(release);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Detail(import, await db.SourceIdentities.CountAsync(x => x.BaselineImportId == id, ct)));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Answering "where did SYS-01233 go?" — the question that decides whether porting a program in was
        /// worth doing. An empty result reads as the tool having lost it.
        app.MapGet("/api/source-identities", async (Guid projectId, string? search, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var source = db.SourceIdentities.AsNoTracking().Where(x => x.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                source = source.Where(x => EF.Functions.Like(x.SourceIdentifier, $"%{term}%"));
            }
            var identities = await source.OrderBy(x => x.SourceIdentifier).Take(200).ToListAsync(ct);
            var ids = identities.Select(x => x.Id).ToList();
            var links = await db.SourceIdentityLinks.AsNoTracking()
                .Where(x => ids.Contains(x.SourceIdentityId)).ToListAsync(ct);
            var history = await db.SourceHistoryEntries.AsNoTracking()
                .Where(x => ids.Contains(x.SourceIdentityId)).ToListAsync(ct);
            return Results.Ok(identities.Select(x => new
            {
                x.Id, x.SourceSystem, x.SourceModule, x.SourceObjectKey, x.SourceIdentifier,
                // False means the object was in the source's history but not the baseline that was imported.
                // It is answerable, and it joins nothing.
                x.InImportedBaseline,
                requirementRevisionId = links.SingleOrDefault(link => link.SourceIdentityId == x.Id)?.RequirementRevisionId,
                sourceHistory = history.Where(entry => entry.SourceIdentityId == x.Id)
                    .OrderBy(entry => entry.SourceBaselineName)
                    .Select(entry => new
                    {
                        entry.SourceBaselineName, entry.Statement, entry.ChangedBy, entry.ChangedAt,
                        entry.SourceChangeReference
                    })
            }));
        });

        async Task<IResult> MutateAsync(Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            CancellationToken ct, Action<BaselineImport, DateTimeOffset> act)
        {
            var import = await db.BaselineImports.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            if (!await AuthorizedAsync(import.ProjectId, http, db, identity, ct)) return Results.Forbid();
            try
            {
                act(import, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Detail(import, await db.SourceIdentities.CountAsync(x => x.BaselineImportId == id, ct)));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }
    }

    private static object Summary(BaselineImport x) => new
    {
        x.Id, x.ProjectId, state = x.State.ToString(), carries = x.Carries.ToString(),
        x.SourceSystem, x.SourceBaselineName, x.SourceBaselineDate, x.ExtractFileName,
        x.StartedBy, x.StartedAt, x.AcceptedBy, x.AcceptedAt, x.ReleaseId
    };

    private static object Detail(BaselineImport x, int sourceIdentityCount) => new
    {
        x.Id, x.ProjectId, state = x.State.ToString(), carries = x.Carries.ToString(),
        x.SourceSystem, x.SourceSystemVersion, x.SourceBaselineName, x.SourceBaselineDate,
        x.ExtractFileName, x.ExtractSha256, x.ExtractSizeBytes,
        x.ExtractedBy, x.ExtractedAt, x.StartedBy, x.StartedAt,
        mappingJson = x.MappingJson, reconciliationJson = x.ReconciliationJson,
        x.AcceptedBy, x.AcceptedAt, x.ReleaseId, sourceIdentityCount, x.Version,
        // Stated rather than implied: what accepting this import does and does not assert.
        asserts = new[]
        {
            "The extract is a true copy of the named source baseline.",
            "The mapping is correct for this program.",
            "Any recorded gaps are accepted."
        },
        doesNotAssert = "That these requirements were reviewed or approved in AeroLink. They were not."
    };
}
