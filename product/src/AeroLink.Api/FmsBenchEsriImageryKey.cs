using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>
/// The owner's Esri World Imagery API key (DEC-151), for the imagery relay's worldwide fallback
/// (<see cref="FmsBenchImageryEndpoints"/>). It is stored on the machine by <c>CONFIGURE_AEROLINK_IMAGERY.bat</c>
/// (<c>AeroLinkProtectedConfig.psm1</c>): Windows DPAPI, LocalMachine scope with purpose entropy, in a file whose ACL
/// admits only its owner, SYSTEM and Administrators. This process reads it itself, so the key never passes through a
/// launcher, a transition spool, an environment block or a command line, and a key stored or rotated while the API runs
/// is used from the next tile on.
///
/// A key that is absent, unreadable, owned by another account or exposed by its ACL is simply not used: the view then
/// draws relief outside the USGS coverage, as it did before. It never fails the API.
/// <c>FmsBench:EsriImageryKey</c> supplies a key directly, for tests only; production never sets it.
/// </summary>
public sealed class FmsBenchEsriImageryKey(IConfiguration configuration, ILogger<FmsBenchEsriImageryKey> logger)
{
    public const string KeyFileSetting = "FmsBench:EsriImageryKeyFile";
    public const string DirectKeySetting = "FmsBench:EsriImageryKey";
    /// <summary>Shared with <c>AeroLinkProtectedConfig.psm1</c>; the two must change together.</summary>
    internal const string Entropy = "AeroLink protected Esri imagery v1";
    internal const string Purpose = "esri-world-imagery";

    private readonly Lock gate = new();
    private (DateTime WrittenUtc, long Length, string? Key)? cached;
    private string? lastProblem;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AeroLink", "protected-config", "imagery", "esri-world-imagery.json");

    /// <summary>The key, or null when none is usable.</summary>
    public string? Current()
    {
        var direct = configuration[DirectKeySetting];
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
        if (!OperatingSystem.IsWindows()) return null;
        var path = configuration[KeyFileSetting] is { Length: > 0 } configured ? configured : DefaultPath;
        var file = new FileInfo(path);
        if (!file.Exists) { lock (gate) cached = null; return null; }
        lock (gate)
        {
            if (cached is { } hit && hit.WrittenUtc == file.LastWriteTimeUtc && hit.Length == file.Length) return hit.Key;
            string? key = null;
            try { key = Read(file); lastProblem = null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException
                or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                // Reported once per state, and never with the key: the reason is about the file, not its contents.
                if (lastProblem != exception.Message) logger.LogWarning("The Esri imagery key at {Path} is not used: {Reason}", path, exception.Message);
                lastProblem = exception.Message;
            }
            cached = (file.LastWriteTimeUtc, file.Length, key);
            return key;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Read(FileInfo file)
    {
        var owner = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidDataException("this process's account could not be established");
        AssertLockedDown(file.Directory!.GetAccessControl(), owner, "its directory");
        AssertLockedDown(file.GetAccessControl(), owner, "the file");
        using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1
            || root.GetProperty("purpose").GetString() != Purpose)
            throw new InvalidDataException("its schema or purpose is unexpected");
        if (root.GetProperty("ownerSid").GetString() != owner)
            throw new InvalidDataException("it was stored by another Windows account");
        var protectedKey = Convert.FromBase64String(root.GetProperty("protectedKey").GetString() ?? "");
        var plain = ProtectedData.Unprotect(protectedKey, Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.LocalMachine);
        try
        {
            var key = Encoding.UTF8.GetString(plain).Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace)) throw new InvalidDataException("the stored key is empty or malformed");
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Inheritance off, and only its owner, SYSTEM and Administrators, as the store writes it.</summary>
    [SupportedOSPlatform("windows")]
    private static void AssertLockedDown(FileSystemSecurity security, string owner, string what)
    {
        if (!security.AreAccessRulesProtected) throw new InvalidDataException($"{what} inherits permissions");
        string[] allowed = [owner, "S-1-5-18", "S-1-5-32-544"];
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !allowed.Contains(rule.IdentityReference.Value))
                throw new InvalidDataException($"{what} grants access beyond its owner, SYSTEM and Administrators");
    }
}
