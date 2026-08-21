using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The centrally maintained inventory of content whose meaning depends on the project ladder. A new qualifying
/// production path must add one registration here and use <see cref="ProjectLadderSealAuthority"/> before it
/// saves. Preview rows, empty workspaces, schemas, specifications, and other scaffolding are deliberately absent.
/// </summary>
public sealed record LadderBoundContentRegistration(string Id, string Description);

public static class LadderBoundContentCatalog
{
    public static IReadOnlyList<LadderBoundContentRegistration> Current { get; } =
    [
        new("draft-requirement-change", "A draft RequirementChange proposed at a configured ladder level."),
        new("requirement-artifact", "A materialized controlled RequirementArtifact."),
        new("requirement-revision", "A materialized controlled RequirementRevision."),
        new("test-procedure", "A materialized controlled TestProcedure."),
        new("test-change-review", "A controlled verification change review bound to a ladder discipline."),
        new("trace-link", "A controlled requirement trace relationship between configured ladder levels."),
        new("code-traceability", "A controlled code-traceability mapping owed by a configured ladder level."),
    ];

    public static bool IsKnown(string id) => Current.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal));
}

public enum ProjectLadderSealResultKind { NotFound, Sealed, AlreadySealed }

public sealed record ProjectLadderSealResult(ProjectLadderSealResultKind Kind,
    ProjectLadderConfiguration? Configuration = null, string? Error = null);

/// <summary>Raised after the database identifies the losing first-content seal writer.</summary>
public sealed class ProjectLadderSealConcurrencyException(string message) : InvalidOperationException(message);

/// <summary>
/// One persistence/application authority for ladder sealing. It prepares the configuration update and immutable
/// evidence in the caller's unit of work; the caller's SaveChanges/transaction commits content and seal together.
/// It intentionally does not expose a privileged edit route.
/// </summary>
public sealed class ProjectLadderSealAuthority(AeroLinkDbContext db)
{
    public async Task<ProjectLadderSealResult> SealAsync(Guid projectId, string contentKind,
        string contentIdentity, string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!LadderBoundContentCatalog.IsKnown(contentKind))
            throw new DomainException($"Unknown ladder-bound content kind '{contentKind}'. Register the qualifying route before sealing it.");
        if (string.IsNullOrWhiteSpace(contentIdentity))
            throw new DomainException("Ladder-bound content requires a stable identity before the ladder can be sealed.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new DomainException("Ladder sealing requires an attributable actor.");

        // A project creation transaction may persist its ladder and first content together. Prefer an Added local
        // graph (or an already fully loaded graph) so the same UoW can seal before its INSERT is issued; otherwise
        // reload the persisted graph with its children before resolving it.
        var local = db.ProjectLadderConfigurations.Local.SingleOrDefault(x => x.ProjectId == projectId);
        var localEntry = local is null ? null : db.Entry(local);
        var configuration = local is not null
            && (localEntry!.State == EntityState.Added
                || (localEntry.Collection(x => x.Steps).IsLoaded && localEntry.Collection(x => x.AllowedUpstream).IsLoaded))
            ? local
            : await db.ProjectLadderConfigurations
                .Include(x => x.Steps).Include(x => x.AllowedUpstream)
                .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null)
            configuration = local;
        if (configuration is null)
            return new(ProjectLadderSealResultKind.NotFound, Error: "The project has no persisted ladder configuration.");
        if (configuration.IsSealed)
            return new(ProjectLadderSealResultKind.AlreadySealed, configuration);

        // A seal is only meaningful for a valid, resolved graph. Fail before mutating the configuration or
        // adding history so malformed persisted ladder data cannot be hidden by the first content write.
        _ = ProjectLadderResolver.Resolve(configuration);

        var steps = configuration.Steps.OrderBy(x => x.Position)
            .Select(x => new LadderStepDraft(x.CatalogueEntry, x.Position, x.Capabilities)).ToArray();
        var byId = configuration.Steps.ToDictionary(x => x.Id);
        var relationships = configuration.AllowedUpstream
            .Select(x => new LadderRelationshipDraft(byId[x.ParentStepId].CatalogueEntry,
                byId[x.ChildStepId].CatalogueEntry)).ToArray();
        var canonical = ProjectLadderSnapshot.Canonicalize(steps, relationships);
        var hash = ProjectLadderSnapshot.Hash(canonical);
        db.PendingLadderSeal = (projectId, contentKind, contentIdentity);
        configuration.Seal(contentKind, contentIdentity, actor, now);
        db.ProjectLadderConfigurationHistories.Add(new ProjectLadderConfigurationHistory(
            configuration.Id, projectId, configuration.Version, actor, now,
            $"Sealed ladder with first {contentKind} '{contentIdentity}'.", canonical, hash));
        return new(ProjectLadderSealResultKind.Sealed, configuration);
    }

    public static string ConflictExplanation(ProjectLadderConfiguration configuration) =>
        configuration.IsSealed
            ? $"The project ladder is sealed by {configuration.SealedContentKind} '{configuration.SealedContentIdentity}'. Structural edits are no longer allowed."
            : "Another first ladder-bound content write won the ladder seal race; retry against the current project state.";
}
