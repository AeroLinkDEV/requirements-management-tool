namespace AeroLink.Domain.Common;

/// <summary>The one official software-build name used for the release and its controlled baseline.</summary>
public static class SoftwareBuildIdentifier
{
    public static string FromVersion(string version)
    {
        var parts = (version ?? "").Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)
            || major is < 0 or > 99 || minor is < 0 or > 9)
            throw new DomainException("Software build versions must use major.minor format, for example 1.6.");
        return $"SW-{major:D2}.{minor * 10:D2}";
    }
}
