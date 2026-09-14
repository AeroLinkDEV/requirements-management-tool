using System.Reflection;
using AeroLink.Domain.Programs;
using AeroLink.Api;

namespace AeroLink.Api.Tests;

public sealed class WorkspaceReleaseOrderingTests
{
    [Fact]
    public void Release_projections_sort_by_canonical_numeric_identity_and_preserve_raw_values()
    {
        var projectId = Guid.NewGuid();
        var releases = new[]
        {
            new SoftwareRelease(projectId, "10.5", false),
            new SoftwareRelease(projectId, "9.0", true),
            new SoftwareRelease(projectId, "1.3", true),
        };

        var ordered = Order(releases);

        Assert.Equal(["1.3", "9.0", "10.5"], ordered.Select(x => x.Version));
        Assert.Equal("SW-01.30", releases[2].CanonicalIdentity);
    }

    [Fact]
    public void Historical_rows_without_canonical_identity_still_use_their_valid_raw_identity()
    {
        var projectId = Guid.NewGuid();
        var historical = HistoricalRelease(projectId, "1.30");
        var releases = new[]
        {
            new SoftwareRelease(projectId, "10.5", false),
            new SoftwareRelease(projectId, "9.0", true),
            new SoftwareRelease(projectId, "1.3", true),
            historical,
        };

        var ordered = Order(releases);

        Assert.Equal(["1.3", "1.30", "9.0", "10.5"], ordered.Select(x => x.Version));
        Assert.Null(historical.CanonicalIdentity);
    }

    private static IReadOnlyList<SoftwareRelease> Order(IEnumerable<SoftwareRelease> releases)
    {
        var method = typeof(WorkspaceEndpoints).GetMethod("OrderReleasesAscending",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<SoftwareRelease>)method.Invoke(null, [releases])!;
    }

    private static SoftwareRelease HistoricalRelease(Guid projectId, string version)
    {
        var release = (SoftwareRelease)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(SoftwareRelease));
        SetPrivate(release, nameof(SoftwareRelease.Id), Guid.NewGuid());
        SetPrivate(release, nameof(SoftwareRelease.ProjectId), projectId);
        SetPrivate(release, nameof(SoftwareRelease.Version), version);
        SetPrivate(release, nameof(SoftwareRelease.CanonicalIdentity), null);
        SetPrivate(release, nameof(SoftwareRelease.IsReleased), true);
        return release;
    }

    private static void SetPrivate(SoftwareRelease release, string propertyName, object? value) =>
        typeof(SoftwareRelease).GetProperty(propertyName)!.GetSetMethod(nonPublic: true)!.Invoke(release, [value]);
}
