using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure;

/// <summary>
/// Where an installation keeps its Data Protection key ring, under which application name, and how the keys are
/// protected at rest (#1614 1.5).
/// </summary>
/// <remarks>
/// <para>MFA secrets, webhook and notification credentials and CSRF tokens are all unreadable without the ring that
/// protected them. Two things decide whether a payload is readable later: the keys, and the application
/// discriminator, which defaults to the content root path. A site server installation moves its program folder on
/// every release, so it fixes the discriminator with <c>DataProtection:ApplicationName</c> and keeps the ring in an
/// installation folder protected with DPAPI-LocalMachine, not the run-as account's profile.</para>
/// <para>Every setting is opt-in. An installation that sets none of them - HOME today - keeps the per-user default ring
/// and the content-root discriminator it already has. Turning either on for HOME would orphan its existing MFA
/// secrets, so nothing here changes without configuration.</para>
/// </remarks>
public static class DataProtectionInstallationSettings
{
    public const string DpapiLocalMachine = "DpapiLocalMachine";

    public static IDataProtectionBuilder Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        // A deployment-owned ring supports restart-stable protected notification evidence. Directory
        // access remains lazy: a broken ring blocks protected operations, not unrelated business saves.
        var keyRingPath = configuration["DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyRingPath))
            builder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

        var applicationName = configuration["DataProtection:ApplicationName"];
        if (!string.IsNullOrWhiteSpace(applicationName))
            builder.SetApplicationName(applicationName.Trim());

        var protectKeysWith = configuration["DataProtection:ProtectKeysWith"];
        if (string.IsNullOrWhiteSpace(protectKeysWith)) return builder;
        // Fail closed: a misspelled protector would otherwise leave a file-system ring's keys unencrypted.
        if (!string.Equals(protectKeysWith.Trim(), DpapiLocalMachine, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"DataProtection:ProtectKeysWith is '{protectKeysWith}'. AeroLink supports '{DpapiLocalMachine}'.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"DataProtection:ProtectKeysWith '{DpapiLocalMachine}' requires Windows.");
        return builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
    }
}
