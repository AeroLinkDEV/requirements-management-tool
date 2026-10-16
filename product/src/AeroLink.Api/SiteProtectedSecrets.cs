using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// The secrets a site server's API reads for itself at start-up (#1614 1.7, 1.8).
///
/// The API runs as a Windows service under its installation's virtual account. Its non-secret settings are in the
/// service's environment (Install-AeroLinkServer.ps1); its two secrets are not, because the service key is readable
/// far more widely than they may be. They live in the protected-config store the installer wrote them to
/// (AeroLinkProtectedConfig.psm1): DPAPI LocalMachine, entropy bound to the installation root and the secret's
/// name, one reader SID, and a protected ACL naming only that reader, SYSTEM and Administrators.
///
///   postgres-application   appended to ConnectionStrings:AeroLink as its password
///   api-bootstrap          Identity:BootstrapSecret, until the first administrator exists and it is removed
///
/// The record is checked exactly as the PowerShell reader checks it, and any doubt is a refusal to start: a site API
/// that came up without its own password would fail later and less clearly.
/// </summary>
public static class SiteProtectedSecrets
{
    public const string InstallationRootKey = "SiteServer:InstallationRoot";
    public const string ProtectedConfigRootKey = "SiteServer:ProtectedConfigRoot";
    private const string Purpose = "site-secret";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";

    /// <summary>Adds the site's secrets to the configuration, when SiteServer:InstallationRoot names one.</summary>
    public static void AddTo(ConfigurationManager configuration)
    {
        var root = configuration[InstallationRootKey];
        if (string.IsNullOrWhiteSpace(root)) return;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("A site server's protected secrets are Windows-only.");
        var storeRoot = configuration[ProtectedConfigRootKey];
        var values = new Dictionary<string, string?>();

        var connection = configuration["ConnectionStrings:AeroLink"] ?? "";
        if (connection.Contains("password", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A site server's connection string must not carry a password; the API reads it from the protected store.");
        var password = Read(root, "postgres-application", storeRoot, required: true)!;
        values["ConnectionStrings:AeroLink"] = connection.TrimEnd(';') + ";Password=" + password;

        var bootstrap = Read(root, "api-bootstrap", storeRoot, required: false);
        if (bootstrap is not null && string.IsNullOrWhiteSpace(configuration["Identity:BootstrapSecret"]))
            values["Identity:BootstrapSecret"] = bootstrap;
        configuration.AddInMemoryCollection(values);
    }

    [SupportedOSPlatform("windows")]
    internal static string? Read(string installationRoot, string name, string? storeRoot, bool required)
    {
        var root = Path.GetFullPath(installationRoot).TrimEnd('\\');
        var store = string.IsNullOrWhiteSpace(storeRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AeroLink", "protected-config")
            : Path.GetFullPath(storeRoot);
        var directory = Path.Combine(store, "site", Sha256Hex(root.ToLowerInvariant()), name);
        var file = Path.Combine(directory, "secret.json");
        if (!File.Exists(file))
        {
            // A record this account may not even see reads as absent; say which it is.
            if (Directory.Exists(directory)) throw Refuse(name, $"is not readable by this process ({WindowsIdentity.GetCurrent().Name})");
            if (required) throw Refuse(name, "is not configured for " + root);
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var record = document.RootElement;
        if (Int(record, "schemaVersion") != 1 || Text(record, "purpose") != Purpose || Text(record, "name") != name
            || Text(record, "installationRootHash") != Sha256Hex(root.ToLowerInvariant()) || string.IsNullOrWhiteSpace(Text(record, "protectedSecret")))
            throw Refuse(name, "has an unexpected identity or schema");
        var reader = Text(record, "readerSid") ?? throw Refuse(name, "names no reader");
        AssertProtected(new DirectoryInfo(directory).GetAccessControl(), reader, name);
        AssertProtected(new FileInfo(file).GetAccessControl(), reader, name);
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(new SecurityIdentifier(reader)))
            throw Refuse(name, $"is stored for {reader}, which this process ({WindowsIdentity.GetCurrent().Name}) does not hold");
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(root + "|AeroLink protected site secret v1|" + name));
        var protectedBytes = Convert.FromBase64String(Text(record, "protectedSecret")!);
        byte[] plain;
        try { plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.LocalMachine); }
        catch (CryptographicException) { throw Refuse(name, "could not be decrypted by this Windows installation"); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        try
        {
            var value = Encoding.UTF8.GetString(plain);
            if (string.IsNullOrEmpty(value)) throw Refuse(name, "is empty");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProtected(FileSystemSecurity security, string reader, string name)
    {
        if (!security.AreAccessRulesProtected) throw Refuse(name, "inherits permissions");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { reader, SystemSid, AdministratorsSid };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference.Value;
            if (rule.AccessControlType != AccessControlType.Allow || !allowed.Contains(sid)
                || (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                throw Refuse(name, "has an unexpected access entry");
            seen.Add(sid);
        }
        if (!allowed.All(seen.Contains)) throw Refuse(name, "is missing a required access entry");
    }

    private static InvalidOperationException Refuse(string name, string why)
        => new($"AeroLink site server refused to start: the protected site secret '{name}' {why}.");

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Text(JsonElement record, string property)
        => record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Int(JsonElement record, string property)
        => record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;
}
