using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Programs;

/// <summary>
/// The major AeroLink modules a project can switch off (#1113). Command Center and My Work are not here:
/// they are always present and show whatever the enabled features contribute.
/// </summary>
[Flags]
public enum ProjectFeature
{
    None = 0,
    TeamWork = 1,
    Requirements = 2,
    Verification = 4,
    Code = 8,
    DocumentationCenter = 16,
    ProblemReports = 32,
    Release = 64,
}

public static class ProjectFeatures
{
    public const ProjectFeature All = ProjectFeature.TeamWork | ProjectFeature.Requirements
        | ProjectFeature.Verification | ProjectFeature.Code | ProjectFeature.DocumentationCenter
        | ProjectFeature.ProblemReports | ProjectFeature.Release;

    public static IReadOnlyList<ProjectFeature> Each { get; } =
    [
        ProjectFeature.TeamWork, ProjectFeature.Requirements, ProjectFeature.Verification, ProjectFeature.Code,
        ProjectFeature.DocumentationCenter, ProjectFeature.ProblemReports, ProjectFeature.Release,
    ];

    public static string Label(ProjectFeature feature) => feature switch
    {
        ProjectFeature.TeamWork => "Team Work",
        ProjectFeature.Requirements => "Requirements",
        ProjectFeature.Verification => "Verification",
        ProjectFeature.Code => "Code",
        ProjectFeature.DocumentationCenter => "Documentation Center",
        ProjectFeature.ProblemReports => "Problem Reports",
        ProjectFeature.Release => "Release",
        _ => feature.ToString(),
    };

    /// <summary>
    /// The combinations a project may hold. Code evidence and verification coverage both answer to
    /// requirements, so neither may stand without them until standalone verification exists (#1113 S5).
    /// Returns the refusal, or null when the set is valid.
    /// </summary>
    public static string? Refusal(ProjectFeature enabled)
    {
        if ((enabled & ~All) != 0) return "The feature set contains an unknown feature.";
        var requirements = enabled.HasFlag(ProjectFeature.Requirements);
        if (enabled.HasFlag(ProjectFeature.Code) && !requirements)
            return "Code needs Requirements: code is traced to the requirements it implements.";
        if (enabled.HasFlag(ProjectFeature.Verification) && !requirements)
            return "Verification needs Requirements until standalone verification is available: verification today covers requirements.";
        return null;
    }
}

/// <summary>
/// A project's switched-on features (#1113). A project with no row has every feature, which is how every
/// project before #1113 keeps its behavior without a backfill. Switching a feature off never deletes or
/// rewrites anything: the service allows it only while the feature holds no records, and the save boundary
/// refuses new records for a feature that is off.
/// </summary>
public sealed class ProjectFeatureSet
{
    private ProjectFeatureSet() { }

    public ProjectFeatureSet(Guid projectId, ProjectFeature enabled, string actor, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A feature set requires a project.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        Apply(enabled, actor, now);
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ProjectFeature Enabled { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    public bool Has(ProjectFeature feature) => Enabled.HasFlag(feature);

    public void Change(ProjectFeature enabled, string actor, DateTimeOffset now)
    {
        Apply(enabled, actor, now);
        Version++;
    }

    private void Apply(ProjectFeature enabled, string actor, DateTimeOffset now)
    {
        if (ProjectFeatures.Refusal(enabled) is { } refusal) throw new DomainException(refusal);
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("A feature change requires an actor.");
        Enabled = enabled;
        UpdatedBy = actor.Trim();
        UpdatedAt = now;
    }
}

/// <summary>One attributed change to a project's feature set. Append-only.</summary>
public sealed class ProjectFeatureSetHistory
{
    private ProjectFeatureSetHistory() { }

    public ProjectFeatureSetHistory(ProjectFeatureSet set, ProjectFeature previous, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why the project's features are changing.");
        Id = Guid.NewGuid();
        ProjectId = set.ProjectId;
        Version = set.Version;
        Previous = previous;
        Enabled = set.Enabled;
        Actor = set.UpdatedBy;
        Reason = reason.Trim();
        OccurredAt = now;
        var snapshot = $"project={ProjectId:D};version={Version};previous={(int)Previous};enabled={(int)Enabled};actor={Actor};at={OccurredAt.UtcDateTime:O};reason={Reason}";
        Snapshot = snapshot;
        SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public long Version { get; private set; }
    public ProjectFeature Previous { get; private set; }
    public ProjectFeature Enabled { get; private set; }
    public string Actor { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public string Snapshot { get; private set; } = "";
    public string SnapshotHash { get; private set; } = "";
}
