using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1614 1.8: a site server's API service reads its database password and bootstrap secret from the protected store
/// itself, so neither is ever in the service's environment. The records here are written in the store's format
/// (AeroLinkProtectedConfig.psm1 Set-AeroLinkProtectedSiteSecret): DPAPI LocalMachine with the root-and-name entropy,
/// one reader SID, a protected ACL of reader, SYSTEM and Administrators.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SiteProtectedSecretsTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("aerolink-site-secrets-").FullName;
    private string Root => Path.Combine(_work, "AeroLink Production");
    private string Store => Path.Combine(_work, "protected-config");

    public void Dispose() => RemoveOwnedTree(_work);

    /// <summary>
    /// A record stored for another reader locks this process out of it. As the owner it may still rewrite the
    /// permissions (WRITE_DAC), so it takes access back, top down, before deleting.
    /// </summary>
    internal static void RemoveOwnedTree(string root)
    {
        var me = new SecurityIdentifier(Me());
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                var security = new DirectorySecurity();
                security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var fileSecurity = new FileSecurity();
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(file).SetAccessControl(fileSecurity);
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_service_reads_its_password_and_bootstrap_secret_from_the_store_and_never_from_its_environment()
    {
        Assert.True(OperatingSystem.IsWindows(), "Site secrets are DPAPI LocalMachine, Windows-only; this suite runs on Windows (backend-api).");
        Write("postgres-application", "app-password-0123456789", Me());
        Write("api-bootstrap", "bootstrap-secret-0123456789-0123456789", Me());
        var configuration = Configuration(new() { ["ConnectionStrings:AeroLink"] = "Host=127.0.0.1;Port=55432;Database=aerolink;Username=aerolink_app" });

        SiteProtectedSecrets.AddTo(configuration);

        Assert.Equal("Host=127.0.0.1;Port=55432;Database=aerolink;Username=aerolink_app;Password=app-password-0123456789", configuration["ConnectionStrings:AeroLink"]);
        Assert.Equal("bootstrap-secret-0123456789-0123456789", configuration["Identity:BootstrapSecret"]);
    }

    [Fact]
    public void A_site_refuses_to_start_without_its_password_with_a_password_in_its_environment_or_with_a_widened_record()
    {
        Assert.True(OperatingSystem.IsWindows(), "Site secrets are DPAPI LocalMachine, Windows-only; this suite runs on Windows (backend-api).");
        var connection = new Dictionary<string, string?> { ["ConnectionStrings:AeroLink"] = "Host=127.0.0.1;Username=aerolink_app" };
        Assert.Contains("is not configured", Assert.Throws<InvalidOperationException>(() => SiteProtectedSecrets.AddTo(Configuration(connection))).Message);

        Write("postgres-application", "app-password-0123456789", Me());
        Assert.Contains("must not carry a password", Assert.Throws<InvalidOperationException>(() =>
            SiteProtectedSecrets.AddTo(Configuration(new() { ["ConnectionStrings:AeroLink"] = "Host=x;Username=a;Password=y" }))).Message);

        // Somebody else may read it: the record is no longer the reader's alone.
        var file = new FileInfo(Path.Combine(DirectoryOf("postgres-application"), "secret.json"));
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
        Assert.Contains("unexpected access entry", Assert.Throws<InvalidOperationException>(() => SiteProtectedSecrets.AddTo(Configuration(connection))).Message);
    }

    [Fact]
    public void A_record_stored_for_another_account_is_refused()
    {
        Assert.True(OperatingSystem.IsWindows(), "Site secrets are DPAPI LocalMachine, Windows-only; this suite runs on Windows (backend-api).");
        // NT SERVICE\AeroLink-Production: the real reader on the host, which this test process does not hold.
        Write("postgres-application", "app-password-0123456789", "S-1-5-80-2690428161-1426446298-3366839011-2245624913-2830003612");
        var error = Assert.Throws<InvalidOperationException>(() =>
            SiteProtectedSecrets.AddTo(Configuration(new() { ["ConnectionStrings:AeroLink"] = "Host=127.0.0.1;Username=aerolink_app" })));
        Assert.Matches("(not readable by|which) this process", error.Message);
    }

    private ConfigurationManager Configuration(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        values[SiteProtectedSecrets.InstallationRootKey] = Root;
        values[SiteProtectedSecrets.ProtectedConfigRootKey] = Store;
        configuration.AddInMemoryCollection(values);
        return configuration;
    }

    private static string Me() => WindowsIdentity.GetCurrent().User!.Value;

    private string DirectoryOf(string name)
        => Path.Combine(Store, "site", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant()))), name);

    [SupportedOSPlatform("windows")]
    private void Write(string name, string secret, string readerSid)
    {
        var directory = Directory.CreateDirectory(DirectoryOf(name));
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(Root + "|AeroLink protected site secret v1|" + name));
        var record = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            purpose = "site-secret",
            name,
            installationRootHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant()))),
            readerSid,
            protectedSecret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), entropy, DataProtectionScope.LocalMachine)),
        });
        var file = new FileInfo(Path.Combine(directory.FullName, "secret.json"));
        File.WriteAllText(file.FullName, record);
        // Reader, SYSTEM and Administrators, inheritance off - plus this process, which must be able to clean up;
        // when the reader is this process that is the same entry.
        var grants = new[] { readerSid, "S-1-5-18", "S-1-5-32-544" };
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(true, false);
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(true, false);
        foreach (var sid in grants)
        {
            directorySecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        file.SetAccessControl(fileSecurity);
        directory.SetAccessControl(directorySecurity);
    }
}
