using AeroLink.Domain.Verification;
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
        app.MapGet("/api/projects/{projectId:guid}/test-procedure-documents", async (Guid projectId,
            string? scope, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();

            var documents = await db.TestProcedureDocuments.AsNoTracking()
                .Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.Level).ThenBy(x => x.DocumentNumber)
                .ToListAsync(ct);
            // One discipline's Explorer speaks for one level, exactly as the requirements side does.
            var levels = ScopeLevels(scope);
            if (levels is not null) documents = documents.Where(x => levels.Contains(x.Level)).ToList();

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
                    description = document.Description,
                    // The count the rail shows beside the document, the way the requirements rail shows one.
                    procedureCount = own.Count(x => x.Type == TestProcedureDocumentNodeType.Procedure),
                    sections = sections.Select(section => new
                    {
                        id = section.Id,
                        heading = section.Heading,
                        position = section.Position,
                        procedureCount = own.Count(x => x.ParentId == section.Id
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
