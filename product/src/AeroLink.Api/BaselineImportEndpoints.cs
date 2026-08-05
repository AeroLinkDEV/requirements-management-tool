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

/// <param name="InImportedBaseline">
/// False for an object the source retired before the baseline being imported. It is recorded so a reference
/// to it can be answered, and it joins nothing.
/// </param>
/// <param name="History">What the source reported about this object earlier, verbatim. Optional per import.</param>
public sealed record SourceRecordRequest(string SourceModule, string SourceObjectKey, string SourceIdentifier,
    bool InImportedBaseline, SourceHistoryRequest[]? History);
public sealed record SourceHistoryRequest(string SourceBaselineName, string? Statement, string? ChangedBy,
    DateTimeOffset? ChangedAt, string? SourceChangeReference);
public sealed record RecordSourceRecordsRequest(SourceRecordRequest[] Records);

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
            return Results.Ok(Detail(import, await TallyAsync(db, id, ct)));
        });

        /// What this import recorded from the extract, so the Reconcile gate can be shown honestly rather
        /// than as a number somebody has to take on trust.
        app.MapGet("/api/baseline-imports/{id:guid}/source-records", async (Guid id, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var import = await db.BaselineImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            const int page = 2000;
            var matching = db.SourceIdentities.AsNoTracking().Where(x => x.BaselineImportId == id);
            // A real extract runs to thousands of objects, so this page is reached in ordinary use. The total
            // is reported alongside it: a capped list that does not say it was capped reads as the whole set,
            // which on this endpoint means reading a partial import as a complete one.
            var total = await matching.CountAsync(ct);
            var identities = await matching
                .OrderBy(x => x.SourceModule).ThenBy(x => x.SourceIdentifier)
                .Take(page).ToListAsync(ct);
            var ids = identities.Select(x => x.Id).ToList();
            var history = await db.SourceHistoryEntries.AsNoTracking()
                .Where(x => ids.Contains(x.SourceIdentityId)).ToListAsync(ct);
            return Results.Ok(new
            {
                total,
                returned = identities.Count,
                records = identities.Select(x => new
            {
                x.Id, x.SourceModule, x.SourceObjectKey, x.SourceIdentifier, x.InImportedBaseline,
                x.FirstSeenAt, x.LastSeenAt,
                sourceHistory = history.Where(entry => entry.SourceIdentityId == x.Id)
                    .OrderBy(entry => entry.SourceBaselineName)
                    .Select(entry => new
                    {
                        entry.SourceBaselineName, entry.Statement, entry.ChangedBy, entry.ChangedAt,
                        entry.SourceChangeReference
                    })
                })
            });
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
                return Results.Created($"/api/baseline-imports/{import.Id}", Detail(import, new ImportTally(0, 0, 0)));
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

        // What the extract actually contained. Separate from the Map gate because mapping is a judgement
        // about kinds of thing and this is the things themselves, and because an import may be told about
        // its objects more than once — a re-extract is a delta, never a duplicate set.
        app.MapPost("/api/baseline-imports/{id:guid}/source-records", async (Guid id,
            RecordSourceRecordsRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            CancellationToken ct) =>
        {
            var import = await db.BaselineImports.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            if (!await AuthorizedAsync(import.ProjectId, http, db, identity, ct)) return Results.Forbid();
            var records = request.Records ?? [];
            if (records.Length == 0)
                return Results.BadRequest(new { error = "No source records were supplied." });

            // Two objects claiming the same source identity means neither can be keyed reliably, so a
            // re-extract could not tell them apart. Refused here rather than reported at Reconcile, because
            // there is no mapping decision that makes it safe.
            var duplicate = records
                .GroupBy(x => (Module: x.SourceModule?.Trim() ?? "", ObjectKey: x.SourceObjectKey?.Trim() ?? ""))
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return Results.BadRequest(new
                {
                    error = $"'{duplicate.Key.ObjectKey}' appears {duplicate.Count()} times in {duplicate.Key.Module}. "
                        + "Two objects claiming the same source identity cannot be told apart by a later extract."
                });

            var now = DateTimeOffset.UtcNow;
            var existing = await db.SourceIdentities
                .Where(x => x.ProjectId == import.ProjectId && x.SourceSystem == import.SourceSystem)
                .ToDictionaryAsync(x => new { x.SourceModule, x.SourceObjectKey }, ct);
            var recorded = 0;
            var seenAgain = 0;
            try
            {
                foreach (var record in records)
                {
                    var module = record.SourceModule?.Trim() ?? "";
                    var key = record.SourceObjectKey?.Trim() ?? "";
                    SourceIdentity subject;
                    if (existing.TryGetValue(new { SourceModule = module, SourceObjectKey = key }, out var already))
                    {
                        // A delta, not a duplicate: the same object seen by a later extract. Who first
                        // recorded it stays as it was.
                        already.SeenAgain(now);
                        subject = already;
                        seenAgain++;
                    }
                    else
                    {
                        subject = record.InImportedBaseline
                            ? new SourceIdentity(import.ProjectId, import.Id, import.SourceSystem, module, key,
                                record.SourceIdentifier, now)
                            : SourceIdentity.FromHistoryOnly(import.ProjectId, import.Id, import.SourceSystem,
                                module, key, record.SourceIdentifier, now);
                        db.SourceIdentities.Add(subject);
                        recorded++;
                    }

                    foreach (var entry in record.History ?? [])
                        db.SourceHistoryEntries.Add(new SourceHistoryEntry(import.ProjectId, subject.Id, import.Id,
                            entry.SourceBaselineName, entry.Statement ?? "", entry.ChangedBy ?? "", entry.ChangedAt,
                            entry.SourceChangeReference ?? ""));
                }
                // Everything in this payload is accounted for, whether it was new here or already known.
                import.NoteSourceRecordsAccountedFor(records.Length, now);
                await db.SaveChangesAsync(ct);
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Ok(new
            {
                accountedFor = records.Length,
                recorded,
                // A re-extract of the same program: the same object, not a second one. Reported separately
                // because "5,412 objects, 0 new" is the answer somebody re-importing needs to see.
                seenAgain,
                historyEntries = records.Sum(x => x.History?.Length ?? 0),
                state = import.State.ToString()
            });
        });

        // Gate 4. Every source object accounted for, before anything is committed.
        app.MapPost("/api/baseline-imports/{id:guid}/reconciliation", (Guid id, RecordReconciliationRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.RecordReconciliation(request.ReconciliationJson, now)));

        // Abandoning takes what the attempt recorded with it. Only rows this import owns are removed, so an
        // object first recorded by an earlier accepted import is never touched by a later attempt failing.
        app.MapPost("/api/baseline-imports/{id:guid}/abandon", (Guid id, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
            MutateAsync(id, http, db, identity, ct, (import, now) => import.Abandon(now), async () =>
            {
                await db.SourceHistoryEntries.Where(x => x.BaselineImportId == id).ExecuteDeleteAsync(ct);
                await db.SourceIdentityLinks.Where(x => x.BaselineImportId == id).ExecuteDeleteAsync(ct);
                await db.SourceIdentities.Where(x => x.BaselineImportId == id).ExecuteDeleteAsync(ct);
            }));

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
                return Results.Ok(Detail(import, await TallyAsync(db, id, ct)));
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
            // Reported rather than left implicit. On this endpoint above all others, a list that was quietly
            // cut short is read as the answer — and the answer people come here for is whether a source
            // identifier is still known at all.
            var total = await source.CountAsync(ct);
            var identities = await source.OrderBy(x => x.SourceIdentifier).Take(200).ToListAsync(ct);
            var ids = identities.Select(x => x.Id).ToList();
            var links = await db.SourceIdentityLinks.AsNoTracking()
                .Where(x => ids.Contains(x.SourceIdentityId)).ToListAsync(ct);
            var history = await db.SourceHistoryEntries.AsNoTracking()
                .Where(x => ids.Contains(x.SourceIdentityId)).ToListAsync(ct);
            return Results.Ok(new
            {
                total,
                returned = identities.Count,
                matches = identities.Select(x => new
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
                })
            });
        });

        /// <param name="after">
        /// Rows to remove once the aggregate has agreed to the change, so a refused gate deletes nothing.
        /// </param>
        async Task<IResult> MutateAsync(Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            CancellationToken ct, Action<BaselineImport, DateTimeOffset> act, Func<Task>? after = null)
        {
            var import = await db.BaselineImports.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (import is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, import.ProjectId, ct)) return Results.Forbid();
            if (!await AuthorizedAsync(import.ProjectId, http, db, identity, ct)) return Results.Forbid();
            try
            {
                act(import, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                if (after is not null) await after();
                return Results.Ok(Detail(import, await TallyAsync(db, id, ct)));
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

    /// <param name="InImportedBaseline">Objects that will become requirements and carry a provenance link.</param>
    /// <param name="HistoryOnly">Objects the source retired earlier: answerable, joined to nothing.</param>
    private sealed record ImportTally(int InImportedBaseline, int HistoryOnly, int HistoryEntries)
    {
        public int Total => InImportedBaseline + HistoryOnly;
    }

    private static async Task<ImportTally> TallyAsync(AeroLinkDbContext db, Guid importId, CancellationToken ct) =>
        new(await db.SourceIdentities.CountAsync(x => x.BaselineImportId == importId && x.InImportedBaseline, ct),
            await db.SourceIdentities.CountAsync(x => x.BaselineImportId == importId && !x.InImportedBaseline, ct),
            await db.SourceHistoryEntries.CountAsync(x => x.BaselineImportId == importId, ct));

    private static object Detail(BaselineImport x, ImportTally tally) => new
    {
        x.Id, x.ProjectId, state = x.State.ToString(), carries = x.Carries.ToString(),
        x.SourceSystem, x.SourceSystemVersion, x.SourceBaselineName, x.SourceBaselineDate,
        x.ExtractFileName, x.ExtractSha256, x.ExtractSizeBytes,
        x.ExtractedBy, x.ExtractedAt, x.StartedBy, x.StartedAt,
        mappingJson = x.MappingJson, reconciliationJson = x.ReconciliationJson,
        x.AcceptedBy, x.AcceptedAt, x.ReleaseId, x.Version,
        // What this import accounted for, which on a re-extract exceeds what it newly recorded.
        x.SourceRecordCount,
        sourceIdentityCount = tally.Total,
        // Split, because the two are different assertions. The first set becomes controlled requirements
        // this build carries; the second is answerable and carries nothing.
        sourceRecords = new { inImportedBaseline = tally.InImportedBaseline, historyOnly = tally.HistoryOnly },
        sourceHistoryEntryCount = tally.HistoryEntries,
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
