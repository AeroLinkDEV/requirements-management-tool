using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
using System.ComponentModel.DataAnnotations.Schema;

namespace AeroLink.Domain.Hierarchy;

/// <summary>The persisted provenance class of a project ladder.</summary>
public enum ProjectLadderConfigurationClassification { LegacyDefault, NonDefault }

/// <summary>
/// Storage lifecycle for authored project ladders. Stored LegacyDefault and Active NonDefault rows are runtime
/// authority through the effective resolver; Draft remains on the prior effective behavior until activation.
/// </summary>
public enum ProjectLadderConfigurationState { Stored, Draft, Active, Retired }

/// <summary>A project-owned persisted ladder envelope whose Stored/Active graph is compiled into runtime policy.</summary>
public sealed class ProjectLadderConfiguration
{
    private ProjectLadderConfiguration() { }

    public ProjectLadderConfiguration(Guid projectId, DateTimeOffset now)
        : this(projectId, ProjectLadderConfigurationClassification.LegacyDefault,
            ProjectLadderConfigurationState.Stored, now, null, null, null, null) { }

    /// <summary>Creates the authoring draft owned by #713; activation remains owned exclusively by this slice's service.</summary>
    public static ProjectLadderConfiguration CreateDraft(Guid projectId, DateTimeOffset now) =>
        new(projectId, ProjectLadderConfigurationClassification.NonDefault,
            ProjectLadderConfigurationState.Draft, now, null, null, null, null);

    private ProjectLadderConfiguration(Guid projectId, ProjectLadderConfigurationClassification classification,
        ProjectLadderConfigurationState state, DateTimeOffset now, DateTimeOffset? activatedAt = null,
        string? activatedBy = null, DateTimeOffset? retiredAt = null, string? retiredBy = null,
        string? activationManifestVersion = null, string? activationManifestHash = null)
    {
        if (projectId == Guid.Empty) throw new DomainException("A project ladder requires a project.");
        ValidateShape(classification, state, activatedAt, activatedBy, retiredAt, retiredBy,
            activationManifestVersion, activationManifestHash);
        Id = Guid.NewGuid(); ProjectId = projectId; Classification = classification; State = state;
        CreatedAt = UpdatedAt = now; Version = 1; ActivatedAt = activatedAt; ActivatedBy = activatedBy?.Trim();
        RetiredAt = retiredAt; RetiredBy = retiredBy?.Trim();
        ActivationManifestVersion = activationManifestVersion?.Trim();
        ActivationManifestHash = activationManifestHash?.Trim();
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ProjectLadderConfigurationClassification Classification { get; private set; }
    public ProjectLadderConfigurationState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public string? ActivatedBy { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public string? RetiredBy { get; private set; }
    /// <summary>
    /// Permanently closes normal structural authoring once controlled content depends on this graph. This is
    /// intentionally independent from Stored/Draft/Active: a legacy Stored graph can be sealed without claiming
    /// that it was activated by a user.
    /// </summary>
    public bool IsSealed { get; private set; }
    public DateTimeOffset? SealedAt { get; private set; }
    public string? SealedBy { get; private set; }
    public string? SealedContentKind { get; private set; }
    public string? SealedContentIdentity { get; private set; }
    /// <summary>Evidence for the most recent governed platform representation upgrade, if any.</summary>
    public DateTimeOffset? LastUpgradeAt { get; private set; }
    public string? LastUpgradeBy { get; private set; }
    public string? LastUpgradeVersion { get; private set; }
    public string? LastUpgradeManifestHash { get; private set; }
    /// <summary>The manifest accepted by a successful activation; null until that act occurs.</summary>
    public string? ActivationManifestVersion { get; private set; }
    public string? ActivationManifestHash { get; private set; }
    public long Version { get; private set; }
    /// <summary>Schema marker for the dormant neutral verification profile representation.</summary>
    public int VerificationProfileSchemaVersion { get; private set; } = VerificationArtifactProfileSchema.Current;
    public int ProfileSchemaVersion => VerificationProfileSchemaVersion;
    public ICollection<ProjectLadderStep> Steps { get; } = new List<ProjectLadderStep>();
    public ICollection<ProjectLadderAllowedUpstream> AllowedUpstream { get; } = new List<ProjectLadderAllowedUpstream>();

    /// <summary>
    /// Converts the legacy stored inventory into the one editable draft owned by this project.  This is the only
    /// lifecycle transition exposed to the authoring service; no public operation can produce Active.
    /// </summary>
    public void BeginDraftEdit(DateTimeOffset now)
    {
        if (IsSealed)
            throw new DomainException($"The project ladder is sealed by {SealedContentKind} '{SealedContentIdentity}' and cannot be structurally edited.");
        if (Classification == ProjectLadderConfigurationClassification.LegacyDefault
            && State == ProjectLadderConfigurationState.Stored)
        {
            Classification = ProjectLadderConfigurationClassification.NonDefault;
            State = ProjectLadderConfigurationState.Draft;
        }
        if (Classification != ProjectLadderConfigurationClassification.NonDefault
            || State != ProjectLadderConfigurationState.Draft)
            throw new DomainException("Only a non-default draft ladder can be edited.");
        UpdatedAt = now;
        Version++;
    }

    /// <summary>
    /// Records the one controlled transition from an authored draft to runtime authority. The service that owns
    /// this call has already validated the complete consumer manifest; keeping the mutation here ensures no
    /// endpoint, seeder, or persistence helper can write Active without the required evidence pair.
    /// </summary>
    internal void Activate(string actor, DateTimeOffset now, string manifestVersion, string manifestHash)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Activation requires an actor.");
        if (string.IsNullOrWhiteSpace(manifestVersion) || string.IsNullOrWhiteSpace(manifestHash))
            throw new DomainException("Activation requires manifest version and hash evidence.");
        if (Classification != ProjectLadderConfigurationClassification.NonDefault
            || State != ProjectLadderConfigurationState.Draft)
            throw new DomainException("Only a non-default draft ladder can be activated.");

        State = ProjectLadderConfigurationState.Active;
        UpdatedAt = now;
        Version++;
        ActivatedAt = now;
        ActivatedBy = actor.Trim();
        ActivationManifestVersion = manifestVersion.Trim();
        ActivationManifestHash = manifestHash.Trim().ToLowerInvariant();
        ValidateShape(Classification, State, ActivatedAt, ActivatedBy, RetiredAt, RetiredBy,
            ActivationManifestVersion, ActivationManifestHash);
    }

    /// <summary>
    /// Permanently closes normal structural authoring because a controlled record now depends on this graph.
    /// The internal persistence authority owns this operation; it is not a public aggregate setter.
    /// </summary>
    internal void Seal(string contentKind, string contentIdentity, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(contentKind)) throw new DomainException("Ladder sealing requires a content kind.");
        if (string.IsNullOrWhiteSpace(contentIdentity)) throw new DomainException("Ladder sealing requires a content identity.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Ladder sealing requires an actor.");
        if (IsSealed)
            throw new DomainException($"The project ladder is already sealed by {SealedContentKind} '{SealedContentIdentity}'.");

        IsSealed = true;
        SealedAt = now;
        SealedBy = actor.Trim();
        SealedContentKind = contentKind.Trim();
        SealedContentIdentity = contentIdentity.Trim();
        UpdatedAt = now;
        Version++;
        ValidateSealingEvidence();
    }

    /// <summary>
    /// Applies a governed, attributable platform representation upgrade while retaining the seal. Only the
    /// internal upgrade authority may call this seam; ordinary project roles have no path to it.
    /// </summary>
    internal void RecordPlatformUpgrade(string version, string actor, string manifestHash, DateTimeOffset now)
    {
        if (!IsSealed) throw new DomainException("A platform upgrade requires an already sealed ladder.");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(manifestHash))
            throw new DomainException("A platform upgrade requires version, actor, and readiness evidence.");
        // A legacy Stored graph may be upgraded by the governed internal seam, but the resulting representation
        // is an authored Draft until its ordinary activation authority accepts it. This keeps the runtime on the
        // prior effective policy while making the structural transform attributable and reviewable.
        if (Classification == ProjectLadderConfigurationClassification.LegacyDefault
            && State == ProjectLadderConfigurationState.Stored)
        {
            Classification = ProjectLadderConfigurationClassification.NonDefault;
            State = ProjectLadderConfigurationState.Draft;
        }
        LastUpgradeVersion = version.Trim();
        LastUpgradeBy = actor.Trim();
        LastUpgradeManifestHash = manifestHash.Trim().ToLowerInvariant();
        LastUpgradeAt = now;
        UpdatedAt = now;
        Version++;
        ValidateSealingEvidence();
    }

    internal void ValidateSealingEvidence()
    {
        if (!IsSealed)
        {
            if (SealedAt is not null || SealedBy is not null || SealedContentKind is not null || SealedContentIdentity is not null)
                throw new DomainException("An unsealed ladder cannot carry seal evidence.");
            if (LastUpgradeAt is not null || LastUpgradeBy is not null || LastUpgradeVersion is not null || LastUpgradeManifestHash is not null)
                throw new DomainException("An unsealed ladder cannot carry platform-upgrade evidence.");
            return;
        }
        if (SealedAt is null || string.IsNullOrWhiteSpace(SealedBy)
            || string.IsNullOrWhiteSpace(SealedContentKind) || string.IsNullOrWhiteSpace(SealedContentIdentity))
            throw new DomainException("A sealed ladder requires timestamp, actor, content kind, and content identity evidence.");
        if ((LastUpgradeAt is null) != (LastUpgradeBy is null)
            || (LastUpgradeAt is null) != (LastUpgradeVersion is null)
            || (LastUpgradeAt is null) != (LastUpgradeManifestHash is null))
            throw new DomainException("Platform upgrade evidence requires timestamp, actor, version, and readiness hash.");
    }

    internal static void ValidateShape(ProjectLadderConfigurationClassification classification,
        ProjectLadderConfigurationState state, DateTimeOffset? activatedAt, string? activatedBy,
        DateTimeOffset? retiredAt, string? retiredBy,
        string? activationManifestVersion = null, string? activationManifestHash = null)
    {
        if (!Enum.IsDefined(classification)) throw new DomainException("Unknown project ladder classification.");
        if (!Enum.IsDefined(state)) throw new DomainException("Unknown project ladder state.");
        if (classification == ProjectLadderConfigurationClassification.LegacyDefault
            && state != ProjectLadderConfigurationState.Stored)
            throw new DomainException("A legacy-default ladder must remain in Stored state.");
        if (classification == ProjectLadderConfigurationClassification.NonDefault
            && state == ProjectLadderConfigurationState.Stored)
            throw new DomainException("A non-default ladder cannot use Stored state.");
        if (activatedBy is not null && string.IsNullOrWhiteSpace(activatedBy))
            throw new DomainException("Activation actor evidence cannot be blank.");
        if (retiredBy is not null && string.IsNullOrWhiteSpace(retiredBy))
            throw new DomainException("Retirement actor evidence cannot be blank.");
        if (activationManifestVersion is not null && string.IsNullOrWhiteSpace(activationManifestVersion))
            throw new DomainException("Activation manifest version evidence cannot be blank.");
        if (activationManifestHash is not null && string.IsNullOrWhiteSpace(activationManifestHash))
            throw new DomainException("Activation manifest hash evidence cannot be blank.");
        if ((activationManifestVersion is null) != (activationManifestHash is null))
            throw new DomainException("Activation manifest evidence requires both version and hash.");
        var activationAtPresent = activatedAt is not null;
        var activationByPresent = !string.IsNullOrWhiteSpace(activatedBy);
        var retirementAtPresent = retiredAt is not null;
        var retirementByPresent = !string.IsNullOrWhiteSpace(retiredBy);
        if (activationAtPresent != activationByPresent)
            throw new DomainException("Activation evidence requires both timestamp and actor.");
        if (retirementAtPresent != retirementByPresent)
            throw new DomainException("Retirement evidence requires both timestamp and actor.");
        var hasActivation = activationAtPresent && activationByPresent;
        var hasRetirement = retirementAtPresent && retirementByPresent;
        if (state == ProjectLadderConfigurationState.Active && (!hasActivation || hasRetirement
            || activationManifestVersion is null || activationManifestHash is null))
            throw new DomainException("An active project ladder requires activation evidence and cannot be retired.");
        if (state == ProjectLadderConfigurationState.Retired && (!hasActivation || !hasRetirement
            || activationManifestVersion is null || activationManifestHash is null))
            throw new DomainException("A retired project ladder requires activation, manifest, and retirement evidence.");
        if (state is ProjectLadderConfigurationState.Stored or ProjectLadderConfigurationState.Draft
            && (hasActivation || hasRetirement || activationManifestVersion is not null || activationManifestHash is not null))
            throw new DomainException("A stored or draft project ladder cannot carry activation or retirement evidence.");
    }
}

/// <summary>One ordered, capability-bearing catalogue entry in a project ladder.</summary>
public sealed class ProjectLadderStep
{
    private ProjectLadderStep() { }

    public ProjectLadderStep(Guid configurationId, Guid projectId, RequirementLevel level, int position,
        LevelCapabilities capabilities, DateTimeOffset now,
        IEnumerable<VerificationArtifactKind>? enabledArtifactKinds = null)
    {
        if (configurationId == Guid.Empty || projectId == Guid.Empty)
            throw new DomainException("A ladder step requires a project configuration and project.");
        if (position < 1) throw new DomainException("A ladder step position must be positive.");
        if ((capabilities & ~AllCapabilities) != 0)
            throw new DomainException("A ladder step contains an unknown capability flag.");
        var definition = LegacyLadderPolicy.Instance.Definition(level);
        var kinds = enabledArtifactKinds?.ToArray()
            ?? (capabilities.HasFlag(LevelCapabilities.HasVerification)
                ? definition.VerificationProfile?.EnabledKinds.ToArray() ?? []
                : []);
        if (capabilities.HasFlag(LevelCapabilities.HasVerification))
        {
            if (definition.VerificationProfile is null)
                throw new DomainException($"The {level} definition has no verification profile.");
            VerificationArtifactProfile.ValidateEnabledKinds(
                definition.VerificationProfile.Discipline, kinds);
        }
        else if (kinds.Length != 0)
            throw new DomainException($"A level without verification capability cannot enable verification artifacts.");
        Id = Guid.NewGuid(); ConfigurationId = configurationId; ProjectId = projectId;
        CatalogueEntry = level.ToString(); Position = position; Capabilities = capabilities;
        EnabledArtifactKindsValue = VerificationArtifactProfile.SerializeKinds(kinds);
        CreatedAt = UpdatedAt = now; Version = 1;
    }

    private const LevelCapabilities AllCapabilities = LevelCapabilities.HasChangeControl
        | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument
        | LevelCapabilities.HasCodeTraceability;

    public Guid Id { get; private set; }
    public Guid ConfigurationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string CatalogueEntry { get; private set; } = string.Empty;
    public int Position { get; private set; }
    public LevelCapabilities Capabilities { get; private set; }
    /// <summary>The normalized persisted profile shape; the list is derived and cannot be independently edited.</summary>
    public string EnabledArtifactKindsValue { get; private set; } = string.Empty;
    [NotMapped]
    public string EnabledArtifactKindsJson => EnabledArtifactKindsValue;
    [NotMapped]
    public IReadOnlyList<VerificationArtifactKind> EnabledArtifactKinds =>
        VerificationArtifactProfile.ParseKinds(EnabledArtifactKindsValue);
    [NotMapped]
    public IReadOnlyList<VerificationArtifactKind> EnabledKinds => EnabledArtifactKinds;

    /// <summary>
    /// Governed #726 cutover: enables the exact artifact kinds for this step through the platform upgrade
    /// authority, never through a public structural-edit route. The step remains sealed; only the persisted
    /// enabled-kinds evidence changes, and the configuration history records the upgrade.
    /// </summary>
    internal void ApplyPlatformUpgradeKinds(IReadOnlyList<VerificationArtifactKind> kinds)
    {
        var ordered = kinds.Distinct().ToArray();
        var level = Enum.Parse<RequirementLevel>(CatalogueEntry, false);
        var discipline = level switch
        {
            RequirementLevel.System => VerificationDiscipline.System,
            RequirementLevel.HighLevel => VerificationDiscipline.HighLevelSoftware,
            RequirementLevel.LowLevel => VerificationDiscipline.LowLevelSoftware,
            _ => throw new DomainException($"The {level} level has no verification discipline.")
        };
        VerificationArtifactProfile.ValidateEnabledKinds(discipline, ordered);
        EnabledArtifactKindsValue = VerificationArtifactProfile.SerializeKinds(ordered);
    }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
}

/// <summary>An allowed parent/child edge in one persisted project ladder.</summary>
public sealed class ProjectLadderAllowedUpstream
{
    private ProjectLadderAllowedUpstream() { }

    public ProjectLadderAllowedUpstream(Guid configurationId, Guid projectId, Guid parentStepId, Guid childStepId,
        DateTimeOffset now)
    {
        if (configurationId == Guid.Empty || projectId == Guid.Empty || parentStepId == Guid.Empty || childStepId == Guid.Empty)
            throw new DomainException("A ladder relationship requires configuration, project, and step endpoints.");
        if (parentStepId == childStepId) throw new DomainException("A ladder relationship cannot point a step to itself.");
        Id = Guid.NewGuid(); ConfigurationId = configurationId; ProjectId = projectId;
        ParentStepId = parentStepId; ChildStepId = childStepId; CreatedAt = UpdatedAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ConfigurationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ParentStepId { get; private set; }
    public Guid ChildStepId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
}

/// <summary>
/// Builds the fixed legacy ladder for a project at the same persistence seam as project creation. The effective
/// resolver compiles this stored graph while retaining the characterized legacy compatibility marker.
/// </summary>
public static class LegacyDefaultProjectLadderFactory
{
    public static ProjectLadderConfiguration Create(Guid projectId, DateTimeOffset now)
    {
        var configuration = new ProjectLadderConfiguration(projectId, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var step = new ProjectLadderStep(configuration.Id, projectId, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now);
            configuration.Steps.Add(step);
            steps.Add(step);
        }

        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        return configuration;
    }
}

public sealed record ResolvedProjectLadderStep(RequirementLevel Level, int Position, LevelCapabilities Capabilities,
    IReadOnlyList<VerificationArtifactKind>? EnabledArtifactKinds = null)
{
    public IReadOnlyList<VerificationArtifactKind>? EnabledKinds => EnabledArtifactKinds;
}
public sealed record ResolvedProjectLadderRelationship(RequirementLevel Parent, RequirementLevel Child);

/// <summary>
/// A read-only comparison of persisted data with the code-owned ladder. It is intentionally a resolver, not a
/// policy implementation: no runtime consumer calls it to decide authoring, verification, or release behavior.
/// </summary>
public sealed record ResolvedProjectLadder(
    Guid ProjectId,
    ProjectLadderConfigurationClassification Classification,
    ProjectLadderConfigurationState State,
    IReadOnlyList<ResolvedProjectLadderStep> Steps,
    IReadOnlyList<ResolvedProjectLadderRelationship> AllowedUpstream)
{
    public bool AgreesWithLegacyDefault(ILadderPolicy? policy = null)
    {
        policy ??= LegacyLadderPolicy.Instance;
        if (Classification != ProjectLadderConfigurationClassification.LegacyDefault
            || State != ProjectLadderConfigurationState.Stored
            || Steps.Count != policy.OrderedLevels.Count)
            return false;
        return Steps.OrderBy(x => x.Position).Select(x => x.Level).SequenceEqual(policy.OrderedLevels)
            && Steps.All(x => x.Capabilities == policy.Definition(x.Level).Capabilities)
            && Steps.All(x => x.EnabledArtifactKinds is not null
                && x.EnabledArtifactKinds.SequenceEqual(policy.Definition(x.Level).VerificationProfile?.EnabledKinds ?? []))
            && AllowedUpstream.OrderBy(x => x.Parent).ThenBy(x => x.Child)
                .SequenceEqual(policy.ParentRelationships.Select(x => new ResolvedProjectLadderRelationship(x.Parent, x.Child))
                    .OrderBy(x => x.Parent).ThenBy(x => x.Child));
    }
}

public static class ProjectLadderResolver
{
    public static ResolvedProjectLadder Resolve(ProjectLadderConfiguration configuration, ILadderPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        policy ??= LegacyLadderPolicy.Instance;
        ProjectLadderConfiguration.ValidateShape(configuration.Classification, configuration.State,
            configuration.ActivatedAt, configuration.ActivatedBy, configuration.RetiredAt, configuration.RetiredBy,
            configuration.ActivationManifestVersion, configuration.ActivationManifestHash);
        configuration.ValidateSealingEvidence();
        if (configuration.Version < 1) throw new DomainException("A project ladder version must be positive.");

        var steps = configuration.Steps.ToList();
        if (steps.Count == 0)
            throw new DomainException("A project ladder must contain at least one catalogue step.");
        if (steps.Any(x => x.ProjectId != configuration.ProjectId || x.ConfigurationId != configuration.Id
                           || x.Id == Guid.Empty || x.Position < 1 || x.Version < 1))
            throw new DomainException("A project ladder contains a step from another configuration or an invalid step.");
        if (steps.Select(x => x.Position).Distinct().Count() != steps.Count)
            throw new DomainException("A project ladder contains duplicate positions.");
        if (!steps.Select(x => x.Position).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, steps.Count)))
            throw new DomainException("A project ladder step positions must be contiguous starting at one.");
        if (steps.Select(x => x.CatalogueEntry).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new DomainException("A project ladder contains duplicate catalogue entries.");
        if (steps.Select(x => x.Id).Distinct().Count() != steps.Count)
            throw new DomainException("A project ladder contains duplicate step identities.");

        var resolvedSteps = new List<ResolvedProjectLadderStep>();
        var byId = new Dictionary<Guid, RequirementLevel>();
        foreach (var step in steps)
        {
            if (!Enum.TryParse<RequirementLevel>(step.CatalogueEntry, ignoreCase: false, out var level)
                || !Enum.IsDefined(level))
                throw new DomainException($"Unknown persisted ladder catalogue entry '{step.CatalogueEntry}'.");
            var definition = policy.Definition(level);
            if ((step.Capabilities & ~definition.Capabilities) != 0)
                throw new DomainException($"Persisted capabilities for {level} exceed the code-owned catalogue.");
            byId.Add(step.Id, level);
            var kinds = step.EnabledArtifactKinds;
            if (step.Capabilities.HasFlag(LevelCapabilities.HasVerification))
            {
                var profile = definition.VerificationProfile
                    ?? throw new DomainException($"The {level} definition has no verification profile.");
                VerificationArtifactProfile.ValidateEnabledKinds(profile.Discipline, kinds);
            }
            else if (kinds.Count != 0)
                throw new DomainException($"A level without verification capability cannot enable verification artifacts.");
            resolvedSteps.Add(new(level, step.Position, step.Capabilities, kinds));
        }

        var edges = configuration.AllowedUpstream.ToList();
        if (edges.Any(x => x.Id == Guid.Empty || x.ProjectId != configuration.ProjectId || x.ConfigurationId != configuration.Id
                           || x.ParentStepId == x.ChildStepId || x.Version < 1
                           || !byId.ContainsKey(x.ParentStepId) || !byId.ContainsKey(x.ChildStepId)))
            throw new DomainException("A persisted ladder relationship has an invalid endpoint.");
        if (edges.Select(x => (x.ParentStepId, x.ChildStepId)).Distinct().Count() != edges.Count)
            throw new DomainException("A project ladder contains duplicate relationship edges.");

        var resolvedEdges = edges.Select(x => new ResolvedProjectLadderRelationship(byId[x.ParentStepId], byId[x.ChildStepId])).ToList();
        if (resolvedEdges.Distinct().Count() != resolvedEdges.Count)
            throw new DomainException("A project ladder contains duplicate catalogue relationships.");
        var positionByLevel = resolvedSteps.ToDictionary(x => x.Level, x => x.Position);
        if (resolvedEdges.Any(edge => positionByLevel[edge.Parent] >= positionByLevel[edge.Child]))
            throw new DomainException("A project ladder relationship parent must have an earlier position than its child.");
        var childrenByParent = resolvedEdges.GroupBy(x => x.Parent)
            .ToDictionary(x => x.Key, x => x.Select(edge => edge.Child).ToArray());
        var visiting = new HashSet<RequirementLevel>();
        var visited = new HashSet<RequirementLevel>();
        bool HasCycle(RequirementLevel level)
        {
            if (!visiting.Add(level)) return true;
            if (visited.Contains(level)) { visiting.Remove(level); return false; }
            if (childrenByParent.TryGetValue(level, out var children) && children.Any(HasCycle)) return true;
            visiting.Remove(level); visited.Add(level); return false;
        }

        if (resolvedSteps.Select(x => x.Level).Any(HasCycle))
            throw new DomainException("A project ladder relationship graph cannot contain a cycle.");
        return new(configuration.ProjectId, configuration.Classification, configuration.State,
            resolvedSteps.OrderBy(x => x.Position).ToArray(), resolvedEdges);
    }
}
