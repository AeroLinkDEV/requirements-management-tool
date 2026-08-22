using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Hierarchy;

/// <summary>
/// The compiled consumer inventory that gates a project ladder activation.  The names are stable identifiers,
/// rather than display labels, so a missing registration cannot silently turn into a passing boolean.
///
/// Every stable matrix consumer is registered through infrastructure DI. Activation compares this complete
/// inventory with the routed seams so a project cannot become Active on a partial runtime graph.
/// </summary>
public interface ILadderConsumerRegistration
{
    string Id { get; }
    string Description { get; }
}

public sealed record LadderConsumerRegistration(string Id, string Description) : ILadderConsumerRegistration;

public sealed record LadderConsumerManifest(string Version, string Hash,
    IReadOnlyList<LadderConsumerStatus> Consumers,
    IReadOnlyList<LadderConsumerRegistration> UnknownRegistrations)
{
    public IReadOnlyList<LadderConsumerRegistration> MissingOrUnrouted =>
        Consumers.Where(x => !x.Routed).Select(x => new LadderConsumerRegistration(x.Id, x.Description)).ToArray();

    public bool IsReady => Consumers.Count > 0 && MissingOrUnrouted.Count == 0 && UnknownRegistrations.Count == 0;

    public bool HasOnlyKnownConsumers(IEnumerable<string> ids) =>
        ids.All(id => Consumers.Any(x => x.Id == id)) && UnknownRegistrations.Count == 0;
}

public sealed record LadderConsumerStatus(string Id, string Description, bool Routed);

/// <summary>A v2 registration proves the consumer understands concrete artifact keys and required capabilities.</summary>
public interface IVerificationArtifactConsumerRegistration : ILadderConsumerRegistration
{
    IReadOnlySet<VerificationArtifactKey> SupportedArtifactKeys { get; }
    VerificationArtifactCapability SupportedCapabilities { get; }
}

public sealed record VerificationArtifactConsumerRegistration(
    string Id,
    string Description,
    IReadOnlySet<VerificationArtifactKey> SupportedArtifactKeys,
    VerificationArtifactCapability SupportedCapabilities) : IVerificationArtifactConsumerRegistration
{
    public VerificationArtifactConsumerRegistration(string id, string description,
        IEnumerable<VerificationArtifactKey> supportedArtifactKeys,
        VerificationArtifactCapability supportedCapabilities)
        : this(id, description, supportedArtifactKeys.ToHashSet(), supportedCapabilities) { }
}

public sealed record LadderConsumerArtifactCoverage(
    string ConsumerId,
    VerificationArtifactKey ArtifactKey,
    VerificationArtifactCapability RequiredCapabilities,
    bool SupportsKey,
    bool SupportsCapabilities)
{
    /// <summary>The capabilities declared by this consumer for the concrete key.</summary>
    public VerificationArtifactCapability DeclaredCapabilities { get; init; }
    public bool IsCovered => SupportsKey && SupportsCapabilities;
}

/// <summary>
/// Typed v2 readiness evidence. A routed string ID is insufficient: every effective artifact obligation must be
/// covered by the relevant registrations that name the key and collectively declare the required capabilities.
/// </summary>
public sealed record LadderConsumerManifestV2(
    string Version,
    string Hash,
    IReadOnlyList<LadderConsumerStatus> Consumers,
    IReadOnlyList<LadderConsumerRegistration> UnknownRegistrations,
    IReadOnlyList<LadderConsumerArtifactCoverage> ArtifactCoverage)
{
    public IReadOnlySet<VerificationArtifactKey> RequiredArtifactKeys { get; init; } = new HashSet<VerificationArtifactKey>();
    public IReadOnlyList<LadderConsumerRegistration> MissingOrUnrouted =>
        Consumers.Where(x => !x.Routed).Select(x => new LadderConsumerRegistration(x.Id, x.Description)).ToArray();
    public IReadOnlyList<LadderConsumerArtifactCoverage> MissingArtifactCoverage => MissingCoverage();
    public bool IsReady => Consumers.Count > 0 && MissingOrUnrouted.Count == 0
        && UnknownRegistrations.Count == 0 && MissingArtifactCoverage.Count == 0;

    private IReadOnlyList<LadderConsumerArtifactCoverage> MissingCoverage()
    {
        var missing = new List<LadderConsumerArtifactCoverage>();
        foreach (var key in RequiredArtifactKeys)
        {
            var coverage = ArtifactCoverage.Where(x => x.ArtifactKey == key).ToArray();
            if (coverage.Length == 0)
            {
                missing.Add(new LadderConsumerArtifactCoverage("", key,
                    VerificationArtifactCapability.None, false, false));
                continue;
            }

            var required = coverage[0].RequiredCapabilities;
            var relevant = coverage.Where(x => x.SupportsKey).ToArray();
            var aggregate = relevant.Aggregate(VerificationArtifactCapability.None,
                (capabilities, row) => capabilities | row.DeclaredCapabilities);
            var uncovered = required & ~aggregate;
            if (uncovered == VerificationArtifactCapability.None) continue;
            if (relevant.Length == 0)
            {
                missing.Add(coverage[0] with
                {
                    RequiredCapabilities = uncovered,
                    SupportsKey = false,
                    SupportsCapabilities = false
                });
                continue;
            }

            missing.AddRange(relevant.Select(row => row with
            {
                RequiredCapabilities = uncovered,
                SupportsCapabilities = false
            }));
        }
        return missing;
    }
}

/// <summary>The exact stable matrix consumer inventory carried forward from #702.</summary>
public static class LadderConsumerManifestCatalog
{
    public const string Version = "#702-legacy-consumers-v1";
    public const string VersionV2 = "#729-verification-consumers-v2";

    // This is the compiled/generated required inventory for the #702 matrix. Implementations are supplied
    // separately: each production seam registers its routed adapter instead of flipping a readiness boolean.
    private static readonly IReadOnlyList<LadderConsumerRegistration> RequiredConsumers =
    [
        new("change-request.authoring", "Change-request level/type acceptance and authoring"),
        new("change-request.identifier-allocation", "Requirement and change-request controlled prefixes"),
        new("change-request.upstream-allocation", "Upstream picker and exact parent validation"),
        new("change-request.downstream-impact", "Approved-change downstream assessment creation"),
        new("reqif.commit", "ReqIF imported-level parsing and commit allocation"),
        new("approval.workflow-subject", "Approval workflow review-subject mapping"),
        new("verification.procedure-level", "Procedure/requirement level mapping"),
        new("verification.test-change-workflow", "Test-change review discipline mapping"),
        new("verification.coverage", "Same-level verification coverage"),
        new("baseline.controlled-documents", "Controlled output document hierarchy"),
        new("build.test-sets", "Build test-set discipline inventory"),
        new("enterprise.schema-catalogue", "Enterprise schema/specification catalogue"),
        new("enterprise.import-aliases", "Enterprise level import aliases"),
        new("trace.generic-mutation", "Generic trace mutation acceptance/refusal"),
        new("release.readiness", "Release readiness trace and coverage obligations"),
        new("release.reconciliation", "Release trace carry-forward"),
        new("controlled-editing.identity", "Controlled editing identity and check-in"),
        new("navigation.primary", "Primary navigation grouping and compatibility routes"),
    ];

    public static IReadOnlySet<string> RequiredConsumerIds =>
        RequiredConsumers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

    public static LadderConsumerManifest Current { get; } = Build(Array.Empty<ILadderConsumerRegistration>());

    public static LadderConsumerManifest BuildForTests(IEnumerable<ILadderConsumerRegistration> routedConsumers) =>
        Build(routedConsumers.ToArray());

    public static LadderConsumerManifest BuildForRegistrations(IEnumerable<ILadderConsumerRegistration> routedConsumers) =>
        Build(routedConsumers.ToArray());

    public static LadderConsumerManifestV2 BuildForRegistrationsV2(
        IEnumerable<ILadderConsumerRegistration> routedConsumers,
        IEnumerable<VerificationArtifactDefinition> effectiveProfile) =>
        BuildV2(routedConsumers, effectiveProfile);

    public static LadderConsumerManifestV2 BuildForTestsV2(
        IEnumerable<IVerificationArtifactConsumerRegistration> routedConsumers,
        IEnumerable<VerificationArtifactDefinition> effectiveProfile) =>
        BuildV2(routedConsumers.Cast<ILadderConsumerRegistration>(), effectiveProfile);

    public static LadderConsumerManifestV2 BuildV2(
        IEnumerable<ILadderConsumerRegistration> routedConsumers,
        IEnumerable<VerificationArtifactDefinition> effectiveProfile)
    {
        ArgumentNullException.ThrowIfNull(routedConsumers);
        ArgumentNullException.ThrowIfNull(effectiveProfile);
        var consumers = routedConsumers.ToArray();
        var typed = consumers.OfType<IVerificationArtifactConsumerRegistration>().ToArray();
        var legacy = Build(consumers);
        var required = effectiveProfile.ToArray();
        var coverage = required.SelectMany(definition =>
            typed.Select(consumer => new LadderConsumerArtifactCoverage(consumer.Id, definition.Key,
                definition.RequiredCapabilities,
                consumer.SupportedArtifactKeys.Contains(definition.Key),
                (consumer.SupportedCapabilities & definition.RequiredCapabilities) == definition.RequiredCapabilities)
            {
                DeclaredCapabilities = consumer.SupportedCapabilities
            }))
            .ToArray();
        // Keep the hash independent of registration enumeration order, making readiness evidence reproducible.
        var canonical = string.Join("\n", legacy.Consumers.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => $"consumer|{x.Id}|{x.Description}|{(x.Routed ? "registered" : "unrouted")}"))
            + "\n" + string.Join("\n", coverage.OrderBy(x => x.ArtifactKey.ToString(), StringComparer.Ordinal)
                .ThenBy(x => x.ConsumerId, StringComparer.Ordinal)
                .Select(x => $"artifact|{x.ConsumerId}|{x.ArtifactKey}|{x.RequiredCapabilities}|{x.DeclaredCapabilities}|{x.SupportsKey}|{x.SupportsCapabilities}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{VersionV2}\n{canonical}")))
            .ToLowerInvariant();
        return new(VersionV2, hash, legacy.Consumers, legacy.UnknownRegistrations, coverage)
        {
            RequiredArtifactKeys = required.Select(x => x.Key).ToHashSet()
        };
    }

    public static LadderConsumerManifestV2 BuildV2(
        IEnumerable<ILadderConsumerRegistration> routedConsumers,
        IEnumerable<IVerificationArtifactConsumerRegistration> typedConsumers,
        IEnumerable<VerificationArtifactDefinition> effectiveProfile)
    {
        ArgumentNullException.ThrowIfNull(routedConsumers);
        ArgumentNullException.ThrowIfNull(typedConsumers);
        var typed = typedConsumers.ToArray();
        var typedIds = typed.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        // A typed registration is the v2 replacement for its legacy ID. Keep one row per stable consumer so
        // the v1 inventory remains exact while v2 readiness evaluates the typed declaration.
        var merged = routedConsumers.Where(x => !typedIds.Contains(x.Id))
            .Concat(typed.Cast<ILadderConsumerRegistration>());
        return BuildV2(merged, effectiveProfile);
    }

    public static VerificationArtifactConsumerRegistration TypedRegistration(
        ILadderConsumerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var keys = registration.Id is "verification.procedure-level" or "verification.test-change-workflow"
            or "verification.coverage" or "baseline.controlled-documents" or "release.readiness"
            ? VerificationArtifactVocabulary.Definitions.Select(x => x.Key)
            : [];
        var capabilities = registration.Id switch
        {
            "verification.procedure-level" => VerificationArtifactCapability.Identity
                | VerificationArtifactCapability.Header | VerificationArtifactCapability.Revision
                | VerificationArtifactCapability.Lifecycle,
            "verification.test-change-workflow" => VerificationArtifactCapability.ChangeReview,
            "verification.coverage" => VerificationArtifactCapability.Coverage,
            "baseline.controlled-documents" => VerificationArtifactCapability.ControlledDocument,
            "release.readiness" => VerificationArtifactCapability.Execution,
            _ => VerificationArtifactCapability.None
        };
        return new(registration.Id, registration.Description, keys, capabilities);
    }

    private static LadderConsumerManifest Build(IReadOnlyList<ILadderConsumerRegistration> routedConsumers)
    {
        var requiredIds = RequiredConsumers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = routedConsumers.Where(x => !requiredIds.Contains(x.Id))
            .Select(x => new LadderConsumerRegistration(x.Id, x.Description));
        var duplicateIds = routedConsumers.GroupBy(x => x.Id, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => new LadderConsumerRegistration(x.Key, "Duplicate routed registration"));
        var unknownRegistrations = unknown.Concat(duplicateIds).DistinctBy(x => x.Id).ToArray();
        var registeredIds = routedConsumers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var consumers = RequiredConsumers.Select(x => new LadderConsumerStatus(x.Id, x.Description, registeredIds.Contains(x.Id))).ToArray();
        var canonical = string.Join("\n", consumers.Select(x =>
            $"{x.Id}|{x.Description}|{(x.Routed ? "registered" : "unrouted")}"))
            + (unknownRegistrations.Length == 0 ? string.Empty : $"\nunknown|{string.Join(",", unknownRegistrations.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal))}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}\n{canonical}")))
            .ToLowerInvariant();
        return new(Version, hash, consumers, unknownRegistrations);
    }
}
