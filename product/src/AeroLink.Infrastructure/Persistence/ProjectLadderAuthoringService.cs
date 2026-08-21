using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ProjectLadderEditCommand(
    long ExpectedVersion,
    string Reason,
    IReadOnlyList<LadderStepDraft>? Steps,
    IReadOnlyList<LadderRelationshipDraft>? Relationships);

public sealed record ProjectLadderActivationCommand(long ExpectedVersion, string Reason);

public sealed record ProjectLadderHistoryReadModel(
    long Revision, string Actor, DateTimeOffset OccurredAt, string Reason, string CanonicalSnapshot, string SnapshotHash);

public sealed record LadderCatalogueReadModel(string CatalogueEntry, LevelCapabilities SupportedCapabilities);

public sealed record ProjectLadderReadModel(
    Guid ProjectId,
    Guid ConfigurationId,
    ProjectLadderConfigurationClassification Classification,
    ProjectLadderConfigurationState State,
    long Version,
    string? ActivationManifestVersion,
    string? ActivationManifestHash,
    IReadOnlyList<LadderStepDraft> Steps,
    IReadOnlyList<LadderRelationshipDraft> Relationships,
    IReadOnlyList<ProjectLadderHistoryReadModel> History,
    LadderConsumerManifest Readiness,
    IReadOnlyList<LadderCatalogueReadModel> Catalogue,
    bool CanManage);

public sealed record ProjectLadderEditResult(
    ProjectLadderEditResultKind Kind, ProjectLadderReadModel? Configuration = null, string? Error = null);

public enum ProjectLadderEditResultKind { NotFound, Success, Conflict, Invalid }

public sealed record ProjectLadderActivationResult(
    ProjectLadderActivationResultKind Kind, ProjectLadderReadModel? Configuration = null,
    string? Error = null, LadderConsumerManifest? Readiness = null);

public enum ProjectLadderActivationResultKind { NotFound, Refused, Conflict, Invalid }

/// <summary>
/// The one application authority for project-ladder edits and activation attempts.  The edit operation never
/// accepts lifecycle fields, and activation is deliberately refused until every #702 consumer is routed by a later
/// slice.  No seeder, migration, or aggregate operation can create an Active row through another public path.
/// </summary>
public sealed class ProjectLadderAuthoringService(
    AeroLinkDbContext db, ILadderPolicy policy, IEnumerable<ILadderConsumerRegistration> consumerRegistrations)
{
    private readonly IReadOnlyList<ILadderConsumerRegistration> _consumerRegistrations =
        consumerRegistrations?.ToArray() ?? throw new ArgumentNullException(nameof(consumerRegistrations));

    public async Task<ProjectLadderReadModel?> ReadAsync(Guid projectId, CancellationToken ct, bool canManage = false)
    {
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return configuration is null ? null : await ToReadModelAsync(configuration, ct, canManage);
    }

    public async Task<ProjectLadderEditResult> EditAsync(Guid projectId, ProjectLadderEditCommand command,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (command.ExpectedVersion < 1) return Invalid("A positive expected ladder version is required.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("A meaningful reason is required for a ladder edit.");
        if (string.IsNullOrWhiteSpace(actor)) return Invalid("A ladder edit requires an authenticated actor.");

        var configuration = await db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null) return new(ProjectLadderEditResultKind.NotFound, Error: "The project has no ladder configuration.");
        if (configuration.Version != command.ExpectedVersion)
            return new(ProjectLadderEditResultKind.Conflict, Error: "Another ladder edit was saved. Refresh before editing again.");

        IReadOnlyList<LadderStepDraft> steps;
        IReadOnlyList<LadderRelationshipDraft> relationships;
        try
        {
            (steps, relationships) = ProjectLadderDraftValidator.Validate(command.Steps ?? [], command.Relationships ?? [], policy);
        }
        catch (DomainException ex) { return Invalid(ex.Message); }

        // Validate against a detached draft first. Existing persisted children are not touched until every rule has
        // passed, so an invalid request cannot partially turn the legacy inventory into a non-default draft.
        var checkedDraft = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var byName = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var entity = new ProjectLadderStep(checkedDraft.Id, projectId, level, step.Position, step.Capabilities, now);
            checkedDraft.Steps.Add(entity); byName.Add(step.CatalogueEntry, entity);
        }
        foreach (var edge in relationships)
            checkedDraft.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(checkedDraft.Id, projectId,
                byName[edge.Parent].Id, byName[edge.Child].Id, now));
        _ = ProjectLadderResolver.Resolve(checkedDraft, policy);

        var canonical = ProjectLadderSnapshot.Canonicalize(steps, relationships);
        var hash = ProjectLadderSnapshot.Hash(canonical);
        // Required children have immutable identities and concurrency tokens. Replace them inside the same
        // transaction, after an explicit SQL delete, rather than asking EF's relationship fix-up to infer whether
        // a newly authored row is an update or a replacement of a tracked row.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Claim the configuration version before any destructive statement. The UPDATE's concurrency predicate
        // makes one of two simultaneous v1 writers lose, while the transaction keeps the winning row lock through
        // child replacement and immutable history insertion.
        db.ChangeTracker.Clear();
        configuration = await db.ProjectLadderConfigurations.SingleOrDefaultAsync(x => x.Id == configuration.Id, ct);
        if (configuration is null)
            return new(ProjectLadderEditResultKind.NotFound, Error: "The project has no ladder configuration.");
        if (configuration.Version != command.ExpectedVersion)
            return new(ProjectLadderEditResultKind.Conflict, Error: "Another ladder edit was saved. Refresh before editing again.");
        configuration.BeginDraftEdit(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (DbUpdateException ex) when (IsSqliteLock(ex))
        {
            return Conflict();
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"project_ladder_allowed_upstreams\" WHERE \"ConfigurationId\" = {configuration.Id} AND \"ProjectId\" = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"project_ladder_steps\" WHERE \"ConfigurationId\" = {configuration.Id} AND \"ProjectId\" = {projectId}", ct);
        db.ChangeTracker.Clear();
        configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.Id == configuration.Id, ct);
        var persistedByName = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var entity = new ProjectLadderStep(configuration.Id, projectId, level, step.Position, step.Capabilities, now);
            configuration.Steps.Add(entity); persistedByName.Add(step.CatalogueEntry, entity);
        }
        foreach (var edge in relationships)
            configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
                persistedByName[edge.Parent].Id, persistedByName[edge.Child].Id, now));
        db.ProjectLadderSteps.AddRange(configuration.Steps);
        db.ProjectLadderAllowedUpstreams.AddRange(configuration.AllowedUpstream);
        db.ProjectLadderConfigurationHistories.Add(new ProjectLadderConfigurationHistory(configuration.Id, projectId,
            configuration.Version, actor, now, command.Reason, canonical, hash));
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (DbUpdateException ex) when (IsSqliteLock(ex))
        {
            return Conflict();
        }
        var result = await ReadAsync(projectId, ct, canManage: true);
        return new(ProjectLadderEditResultKind.Success, result);
    }

    /// <summary>The sole activation authority. #713 intentionally returns named unrouted blockers.</summary>
    public async Task<ProjectLadderActivationResult> ActivateAsync(Guid projectId,
        ProjectLadderActivationCommand command, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (command.ExpectedVersion < 1) return ActivationInvalid("A positive expected ladder version is required.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return ActivationInvalid("A meaningful reason is required for activation.");
        if (string.IsNullOrWhiteSpace(actor)) return ActivationInvalid("An activation attempt requires an authenticated actor.");
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null) return new(ProjectLadderActivationResultKind.NotFound, Error: "The project has no ladder configuration.");
        if (configuration.Version != command.ExpectedVersion)
            return new(ProjectLadderActivationResultKind.Conflict, Error: "Another ladder edit was saved. Refresh before activating.");
        if (configuration.Classification != ProjectLadderConfigurationClassification.NonDefault
            || configuration.State != ProjectLadderConfigurationState.Draft)
            return ActivationInvalid("Only a non-default draft ladder can be activated.");
        try { _ = ProjectLadderResolver.Resolve(configuration, policy); }
        catch (DomainException ex) { return ActivationInvalid(ex.Message); }
        var readiness = LadderConsumerManifestCatalog.BuildForRegistrations(_consumerRegistrations);
        var blockers = string.Join(", ", readiness.MissingOrUnrouted.Select(x => x.Id)
            .Concat(readiness.UnknownRegistrations.Select(x => $"unknown:{x.Id}")));
        return new(ProjectLadderActivationResultKind.Refused, Error:
            $"Activation is refused until routing is complete. Unrouted consumers: {blockers}.", Readiness: readiness);
    }

    private static ProjectLadderEditResult Conflict() =>
        new(ProjectLadderEditResultKind.Conflict, Error: "Another ladder edit was saved. Refresh before editing again.");
    private static ProjectLadderEditResult Invalid(string error) => new(ProjectLadderEditResultKind.Invalid, Error: error);
    private static ProjectLadderActivationResult ActivationInvalid(string error) =>
        new(ProjectLadderActivationResultKind.Invalid, Error: error);

    private static bool IsSqliteLock(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    private async Task<ProjectLadderReadModel> ToReadModelAsync(ProjectLadderConfiguration configuration, CancellationToken ct, bool canManage = false)
    {
        _ = ProjectLadderResolver.Resolve(configuration, policy);
        var history = await db.ProjectLadderConfigurationHistories.AsNoTracking()
            .Where(x => x.ConfigurationId == configuration.Id).OrderByDescending(x => x.Revision)
            .Select(x => new ProjectLadderHistoryReadModel(x.Revision, x.Actor, x.OccurredAt, x.Reason, x.CanonicalSnapshot, x.SnapshotHash))
            .ToListAsync(ct);
        return new(configuration.ProjectId, configuration.Id, configuration.Classification, configuration.State,
            configuration.Version, configuration.ActivationManifestVersion, configuration.ActivationManifestHash,
            configuration.Steps.OrderBy(x => x.Position).Select(x => new LadderStepDraft(x.CatalogueEntry, x.Position, x.Capabilities)).ToArray(),
            configuration.AllowedUpstream.Select(x => new LadderRelationshipDraft(
                configuration.Steps.Single(s => s.Id == x.ParentStepId).CatalogueEntry,
                configuration.Steps.Single(s => s.Id == x.ChildStepId).CatalogueEntry)).ToArray(), history,
            LadderConsumerManifestCatalog.BuildForRegistrations(_consumerRegistrations),
            policy.OrderedLevels.Select(level => new LadderCatalogueReadModel(level.ToString(), policy.Definition(level).Capabilities)).ToArray(),
            canManage);
    }
}
