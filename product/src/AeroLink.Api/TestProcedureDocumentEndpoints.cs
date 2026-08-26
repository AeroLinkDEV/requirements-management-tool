using AeroLink.Domain.Verification;
using AeroLink.Domain.Hierarchy;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The documents a project's test procedures are written into, for the rail that groups them.
///
/// The Requirements Explorer groups by the requirements document a requirement belongs to — SYSRD, HLRD,
/// LLRD — and the Test Procedure Explorer had nothing to group by because procedures had no container. They
/// have one now; this is what the rail reads.
/// </summary>
public static class TestProcedureDocumentEndpoints
{
    public static void MapTestProcedureDocumentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId:guid}/{artifactRoute:regex(test-procedure-documents|test-case-documents|test-artifacts)}", async (Guid projectId,
            string artifactRoute, string? scope, HttpContext http, AeroLinkDbContext db, IProjectLadderPolicyResolver policyResolver, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var ladderPolicy = await policyResolver.ResolveAsync(projectId, ct);
            var profiles = ladderPolicy.Definitions
                .Where(level => level.VerificationProfile is not null)
                .Select(level => level.VerificationProfile!)
                .ToArray();
            var requestedKeys = artifactRoute == "test-artifacts"
                ? profiles.SelectMany(profile => profile.Definitions).Select(definition => definition.Key).ToHashSet()
                : artifactRoute == "test-case-documents"
                ? profiles.SelectMany(profile => profile.Definitions)
                    .Where(definition => definition.Kind == VerificationArtifactKind.Case)
                    .Select(definition => definition.Key).ToHashSet()
                // Compatibility contract: the historical route means the verification artifact executed by
                // each configured level. Legacy/default software executes Cases; a Procedure-enabled profile
                // executes Procedures. The canonical Case route above remains exact and never follows this alias.
                : profiles.Select(profile => profile.ExecutableKey).ToHashSet();

            var documents = await db.TestProcedureDocuments.AsNoTracking()
                .Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.Level).ThenBy(x => x.DocumentNumber)
                .ToListAsync(ct);
            // Software mirrors the Requirements Explorer and carries both levels; an exact level remains
            // available for compatible deep links and other focused readers.
            var levels = ScopeLevels(scope);
            documents = documents.Where(x => requestedKeys.Contains(x.ArtifactKey)
                    && (levels is null || levels.Contains(x.Level)))
                .ToList();

            var documentIds = documents.Select(x => x.Id).ToList();
            var nodes = await db.TestProcedureDocumentNodes.AsNoTracking()
                .Where(x => documentIds.Contains(x.DocumentId))
                .OrderBy(x => x.Position)
                .ToListAsync(ct);

            return Results.Ok(documents.Select(document =>
            {
                var own = nodes.Where(x => x.DocumentId == document.Id).ToList();
                var sections = own.Where(x => x.Type == TestProcedureDocumentNodeType.Section).ToList();
                return new
                {
                    id = document.Id,
                    documentNumber = document.DocumentNumber,
                    title = document.Title,
                    level = document.Level.ToString(),
                    artifactKind = document.ArtifactKind.ToString(),
                    description = document.Description,
                    // The count the rail shows beside the document, the way the requirements rail shows one.
                    artifactCount = own.Count(x => x.Type == TestProcedureDocumentNodeType.Procedure),
                    procedureCount = own.Count(x => x.Type == TestProcedureDocumentNodeType.Procedure), // compatibility alias
                    sections = sections.Select(section => new
                    {
                        id = section.Id,
                        heading = section.Heading,
                        position = section.Position,
                        artifactCount = own.Count(x => x.ParentId == section.Id
                            && x.Type == TestProcedureDocumentNodeType.Procedure),
                        procedureCount = own.Count(x => x.ParentId == section.Id // compatibility alias
                            && x.Type == TestProcedureDocumentNodeType.Procedure),
                    }),
                };
            }));
        });
    }

    /// <summary>The levels an Explorer scope speaks for, or null for every level in the project.</summary>
    private static TestProcedureLevel[]? ScopeLevels(string? scope) => scope?.ToLowerInvariant() switch
    {
        "system" => [TestProcedureLevel.System],
        "highlevelsoftware" => [TestProcedureLevel.HighLevel],
        "lowlevelsoftware" => [TestProcedureLevel.LowLevel],
        "software" => [TestProcedureLevel.HighLevel, TestProcedureLevel.LowLevel],
        _ => null,
    };
}
