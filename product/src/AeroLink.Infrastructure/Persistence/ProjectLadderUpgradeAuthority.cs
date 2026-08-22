using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroLink.Infrastructure.Persistence;

internal sealed record ProjectLadderUpgradeCommand(long ExpectedVersion, string Version, string Reason,
    IReadOnlyList<LadderStepDraft> Steps, IReadOnlyList<LadderRelationshipDraft> Relationships);

internal enum ProjectLadderUpgradeResultKind { NotFound, Success, Refused, Conflict, Invalid }

internal sealed record ProjectLadderUpgradeResult(ProjectLadderUpgradeResultKind Kind,
    ProjectLadderReadModel? Configuration = null, string? Error = null, LadderConsumerManifest? Readiness = null,
    LadderConsumerManifestV2? ArtifactReadiness = null);

/// <summary>
/// Internal seam for future product-owned representation upgrades. There is intentionally no
/// endpoint registration for this authority. It uses the same persisted resolver, readiness manifest, optimistic
/// version token, and immutable history evidence as normal activation while retaining the existing seal.
/// </summary>
internal sealed class ProjectLadderUpgradeAuthority(
    AeroLinkDbContext db, ILadderPolicy policy, IEnumerable<ILadderConsumerRegistration> consumerRegistrations,
    IEnumerable<IVerificationArtifactConsumerRegistration>? artifactConsumerRegistrations = null)
{
    private readonly IReadOnlyList<ILadderConsumerRegistration> registrations =
        consumerRegistrations?.ToArray() ?? throw new ArgumentNullException(nameof(consumerRegistrations));
    private readonly IReadOnlyList<IVerificationArtifactConsumerRegistration> artifactRegistrations =
        artifactConsumerRegistrations?.ToArray() ?? [];

    public async Task<ProjectLadderUpgradeResult> UpgradeAsync(Guid projectId, ProjectLadderUpgradeCommand command,
        string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        if (command.ExpectedVersion < 1) return Invalid("A positive expected ladder version is required.");
        if (string.IsNullOrWhiteSpace(command.Version)) return Invalid("A platform upgrade requires a version.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("A platform upgrade requires a meaningful reason.");
        if (string.IsNullOrWhiteSpace(actor)) return Invalid("A platform upgrade requires an attributable actor.");
        LadderConsumerManifest readiness = LadderConsumerManifestCatalog.BuildForRegistrations(registrations);
        LadderConsumerManifestV2 artifactReadiness;
        try
        {
            artifactReadiness = LadderConsumerManifestCatalog.BuildV2(registrations, artifactRegistrations,
                EffectiveArtifactProfile(command.Steps));
        }
        catch (DomainException ex) { return Invalid(ex.Message); }
        if (!readiness.IsReady || !artifactReadiness.IsReady)
            return new(ProjectLadderUpgradeResultKind.Refused,
                Error: $"The platform upgrade is refused until routing is complete: {string.Join(", ",
                    readiness.MissingOrUnrouted.Select(x => x.Id)
                        .Concat(artifactReadiness.MissingArtifactCoverage.Select(x => $"artifact:{x.ConsumerId}:{x.ArtifactKey}")))}.",
                Readiness: readiness, ArtifactReadiness: artifactReadiness);

        var initial = await LoadAsync(projectId, ct);
        if (initial is null) return new(ProjectLadderUpgradeResultKind.NotFound, Error: "The project has no ladder configuration.");
        if (!initial.IsSealed)
            return Invalid("A platform representation upgrade requires an already sealed ladder.");
        if (initial.Version != command.ExpectedVersion)
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");
        try { ValidateDraft(projectId, command.Steps, command.Relationships); }
        catch (DomainException ex) { return Invalid(ex.Message); }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var configuration = await db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null) return new(ProjectLadderUpgradeResultKind.NotFound, Error: "The project has no ladder configuration.");
        if (!configuration.IsSealed)
            return Invalid("A platform representation upgrade requires an already sealed ladder.");
        if (configuration.Version != command.ExpectedVersion)
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");

        var canonical = ProjectLadderSnapshot.CanonicalizeV2(command.Steps, command.Relationships, policy);
        var snapshotHash = ProjectLadderSnapshot.Hash(canonical);
        configuration.RecordPlatformUpgrade(command.Version, actor, artifactReadiness.Hash, now);
        try
        {
            // Claim the optimistic version while the transaction holds the configuration row. A competing
            // upgrade therefore loses before either writer deletes or replaces a child graph.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");
        }
        catch (DbUpdateException ex) when (IsSqliteLock(ex) || IsHistoryRevisionConflict(ex))
        {
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"project_ladder_allowed_upstreams\" WHERE \"ConfigurationId\" = {configuration.Id} AND \"ProjectId\" = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"project_ladder_steps\" WHERE \"ConfigurationId\" = {configuration.Id} AND \"ProjectId\" = {projectId}", ct);
        db.ChangeTracker.Clear();
        configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.Id == configuration.Id, ct);
        var persistedByName = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in command.Steps.OrderBy(x => x.Position))
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var entity = new ProjectLadderStep(configuration.Id, projectId, level, step.Position, step.Capabilities, now,
                step.EnabledArtifactKinds);
            configuration.Steps.Add(entity); persistedByName.Add(step.CatalogueEntry, entity);
        }
        foreach (var edge in command.Relationships)
            configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, projectId,
                persistedByName[edge.Parent].Id, persistedByName[edge.Child].Id, now));
        db.ProjectLadderSteps.AddRange(configuration.Steps);
        db.ProjectLadderAllowedUpstreams.AddRange(configuration.AllowedUpstream);
        db.ProjectLadderConfigurationHistories.Add(new ProjectLadderConfigurationHistory(configuration.Id, projectId,
            configuration.Version, actor, now,
            $"Platform upgrade {command.Version}: {command.Reason.Trim()} (readiness {artifactReadiness.Version}/{artifactReadiness.Hash}; legacy {readiness.Version}/{readiness.Hash}).",
            canonical, snapshotHash, ProjectLadderSnapshot.CurrentSchemaVersion));
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");
        }
        catch (DbUpdateException ex) when (IsSqliteLock(ex) || IsHistoryRevisionConflict(ex))
        {
            return Conflict("Another ladder edit, seal, or platform upgrade was saved. Refresh before upgrading.");
        }

        var read = await new ProjectLadderAuthoringService(db, policy, registrations, artifactRegistrations)
            .ReadAsync(projectId, ct, true);
        return new(ProjectLadderUpgradeResultKind.Success, read, Readiness: readiness, ArtifactReadiness: artifactReadiness);
    }

    private async Task<ProjectLadderConfiguration?> LoadAsync(Guid projectId, CancellationToken ct) =>
        await db.ProjectLadderConfigurations.AsNoTracking().Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);

    private void ValidateDraft(Guid projectId, IReadOnlyList<LadderStepDraft> steps,
        IReadOnlyList<LadderRelationshipDraft> relationships)
    {
        var validated = ProjectLadderDraftValidator.Validate(steps, relationships, policy);
        var draft = ProjectLadderConfiguration.CreateDraft(projectId, DateTimeOffset.UtcNow);
        var byName = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in validated.Steps)
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var entity = new ProjectLadderStep(draft.Id, projectId, level, step.Position, step.Capabilities, draft.CreatedAt,
                step.EnabledArtifactKinds);
            draft.Steps.Add(entity); byName.Add(step.CatalogueEntry, entity);
        }
        foreach (var edge in validated.Relationships)
            draft.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(draft.Id, projectId,
                byName[edge.Parent].Id, byName[edge.Child].Id, draft.CreatedAt));
        _ = ProjectLadderResolver.Resolve(draft, policy);
    }

    private IReadOnlyList<VerificationArtifactDefinition> EffectiveArtifactProfile(
        IReadOnlyList<LadderStepDraft> steps)
    {
        var definitions = new List<VerificationArtifactDefinition>();
        foreach (var step in steps)
        {
            var level = Enum.Parse<RequirementLevel>(step.CatalogueEntry, false);
            var definition = policy.Definition(level);
            if (!step.Capabilities.HasFlag(LevelCapabilities.HasVerification)) continue;
            var profile = definition.VerificationProfile
                ?? throw new DomainException($"The {level} definition has no verification profile.");
            var kinds = step.EnabledArtifactKinds ?? profile.EnabledKinds;
            foreach (var kind in kinds)
                definitions.Add(profile.Definitions.Single(x => x.Kind == kind));
        }
        return definitions;
    }

    private static ProjectLadderUpgradeResult Invalid(string error) => new(ProjectLadderUpgradeResultKind.Invalid, Error: error);
    private static ProjectLadderUpgradeResult Conflict(string error) => new(ProjectLadderUpgradeResultKind.Conflict, Error: error);
    private static bool IsSqliteLock(DbUpdateException exception) => exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    private static bool IsHistoryRevisionConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite
            && sqlite.Message.Contains("project_ladder_configuration_history", StringComparison.Ordinal)
            && sqlite.Message.Contains("ConfigurationId", StringComparison.Ordinal)
            && sqlite.Message.Contains("Revision", StringComparison.Ordinal)
        || exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_project_ladder_configuration_history_ConfigurationId_Revisi~"
        };
}
