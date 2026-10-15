using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AeroLink.Infrastructure.Tests;

// #1614 1.5. A site server installation replaces its program folder on every release, which changes the content root
// - and the content root is the default Data Protection application discriminator. Its MFA secrets, webhook and
// notification credentials must survive that, and its key ring must not sit unencrypted in an installation folder.
// HOME configures none of this, and must keep reading what it protected before (#1614: changing HOME orphans MFA).
public sealed class DataProtectionInstallationSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"aerolink-keyring-{Guid.NewGuid():N}");
    private string Ring => Path.Combine(root, "keys", "data-protection");

    [WindowsFact]
    public void A_site_installation_reads_its_keys_after_a_release_moves_the_program_folder_and_keeps_them_dpapi_protected()
    {
        var site = new Dictionary<string, string?>
        {
            ["DataProtection:KeyRingPath"] = Ring,
            ["DataProtection:ApplicationName"] = "AeroLink",
            ["DataProtection:ProtectKeysWith"] = "DpapiLocalMachine",
        };
        var protectedByRelease1 = Protect(site, Path.Combine(root, "program", "versions", "1.0.0"), "mfa secret");

        Assert.Equal("mfa secret", Unprotect(site, Path.Combine(root, "program", "versions", "1.1.0"), protectedByRelease1));
        var key = File.ReadAllText(Assert.Single(Directory.GetFiles(Ring, "key-*.xml")));
        Assert.Contains("DpapiXmlDecryptor", key);
        Assert.DoesNotContain("<masterKey", key);
    }

    [Fact]
    public void Without_installation_settings_the_discriminator_and_key_protection_are_unchanged()
    {
        // HOME's shape: no application name, no key protector. The content root still discriminates, so nothing
        // HOME protected before this change is read under a different discriminator after it.
        var home = new Dictionary<string, string?> { ["DataProtection:KeyRingPath"] = Ring };
        var protectedByHome = Protect(home, Path.Combine(root, "home-checkout"), "mfa secret");

        Assert.Equal("mfa secret", Unprotect(home, Path.Combine(root, "home-checkout"), protectedByHome));
        Assert.ThrowsAny<CryptographicException>(() => Unprotect(home, Path.Combine(root, "other-checkout"), protectedByHome));
        Assert.DoesNotContain("encryptedSecret", File.ReadAllText(Assert.Single(Directory.GetFiles(Ring, "key-*.xml"))));
    }

    [Fact]
    public void An_unknown_key_protector_is_refused_rather_than_leaving_keys_unencrypted()
    {
        var misspelt = new Dictionary<string, string?> { ["DataProtection:KeyRingPath"] = Ring, ["DataProtection:ProtectKeysWith"] = "DpapiMachine" };
        var error = Assert.Throws<InvalidOperationException>(() => Build(misspelt, root));
        Assert.Contains("DataProtection:ProtectKeysWith", error.Message);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, string contentRoot)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IHostEnvironment>(new FixedHostEnvironment(contentRoot));
        DataProtectionInstallationSettings.Apply(services.AddDataProtection(), configuration);
        return services.BuildServiceProvider();
    }

    private static string Protect(Dictionary<string, string?> settings, string contentRoot, string value)
    {
        using var provider = Build(settings, contentRoot);
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AeroLink.Mfa").Protect(value);
    }

    private static string Unprotect(Dictionary<string, string?> settings, string contentRoot, string payload)
    {
        using var provider = Build(settings, contentRoot);
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AeroLink.Mfa").Unprotect(payload);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FixedHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AeroLink.Api";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
        }
    }
}
