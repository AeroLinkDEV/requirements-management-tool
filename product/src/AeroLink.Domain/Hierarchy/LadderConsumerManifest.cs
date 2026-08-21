using System.Security.Cryptography;
using System.Text;

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

/// <summary>The exact stable matrix consumer inventory carried forward from #702.</summary>
public static class LadderConsumerManifestCatalog
{
    public const string Version = "#702-legacy-consumers-v1";

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
