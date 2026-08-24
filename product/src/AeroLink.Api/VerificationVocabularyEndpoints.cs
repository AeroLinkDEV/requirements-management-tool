using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

/// <summary>
/// A project's permitted verification methods, and the stored values that do not match them (#701).
///
/// Reading is open to every project member, because requirement authoring needs the permitted set wherever a
/// change is written. Replacing the vocabulary decides what every future submission will accept, which is
/// exactly the authority Project Configuration already holds — the same roles that author the ladder, under
/// the same optimistic-version contract, so a configuration manager who has not re-read the vocabulary
/// cannot overwrite somebody else's edit.
/// </summary>
public static class VerificationVocabularyEndpoints
{
    public static void MapVerificationVocabularyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/verification-methods", async (Guid projectId, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, ProjectVerificationVocabularyService service,
            CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var canManage = await http.HasProjectRoleAsync(db, identity, projectId, ct,
                ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator);
            var read = await service.ReadAsync(projectId, canManage, ct);
            return read is null ? Results.NotFound(new { error = "That project does not exist." }) : Results.Ok(Projection(read));
        });

        app.MapPut("/api/projects/{projectId:guid}/verification-methods", async (Guid projectId,
            VerificationVocabularyEditRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, ProjectVerificationVocabularyService service, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (!await http.HasProjectRoleAsync(db, identity, projectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            var actor = http.UserAccount();
            var result = await service.ReplaceAsync(projectId, request.Methods ?? [], request.ExpectedVersion,
                request.Reason ?? "", actor.UserName,
                http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow, ct);
            return result.Kind switch
            {
                VerificationVocabularyEditResultKind.NotFound => Results.NotFound(new { error = result.Error }),
                VerificationVocabularyEditResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
                VerificationVocabularyEditResultKind.Conflict => Results.Conflict(new
                {
                    error = result.Error,
                    strandedMethods = result.StrandedMethods ?? [],
                }),
                _ => Results.Ok(Projection(result.Vocabulary!)),
            };
        });
    }

    private static object Projection(VerificationVocabularyReadModel read) => new
    {
        read.Persisted,
        read.Version,
        read.Methods,
        read.CanManage,
        NonConforming = read.NonConforming.Select(x => new
        {
            x.Value, x.ChangeCount, x.RevisionCount, x.TotalCount, x.Examples,
        }),
    };
}

public sealed class VerificationVocabularyEditRequest
{
    /// <summary>The vocabulary version the caller read. Zero means "this project carries none yet".</summary>
    public long ExpectedVersion { get; set; }

    /// <summary>Why the permitted set is changing; recorded as attributable audit evidence.</summary>
    public string? Reason { get; set; }

    /// <summary>The permitted methods, in the order authoring should offer them.</summary>
    public List<string>? Methods { get; set; }
}
