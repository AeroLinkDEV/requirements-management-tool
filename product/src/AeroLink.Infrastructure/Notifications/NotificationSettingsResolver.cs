using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Notifications;

public sealed record NotificationInstallationPolicy(string InstallationId, string HostIdentity, Guid SendGeneration,
    Guid PolicyRevision, NotificationMode MaximumMode, string[] RelayHosts, string[] Senders,
    string[] RecipientDomains, string[] RecipientAddresses, string DiagnosticTarget, string BaseUrl,
    bool AllowManagedCredentials = false, string[]? TrustAnchorsPem = null);

public sealed record ResolvedNotificationSettings(string InstallationId, long Version, Guid SettingsId,
    NotificationMode Mode, string Host, int Port, string Sender, string DisplayName, string BaseUrl,
    string UserName, string Credential, NotificationInstallationPolicy? Policy, string PolicyHash,
    string SafeCode, bool RelayLocked, bool SenderLocked, bool BaseUrlLocked, bool CredentialsLocked)
{
    public string[] EnabledEventTypes { get; init; } = NotificationOperationsService.InitialEventTypes;
    public bool IsExternal => Mode is NotificationMode.ControlledTest or NotificationMode.Live;
    public bool AdmissionEnabled { get; init; }
    public Guid? AdmissionEpochId { get; init; }
    public bool CanSend => Mode != NotificationMode.Disabled && SafeCode.Length == 0;
    public Guid SendGeneration => IsExternal ? Policy?.SendGeneration ?? Guid.Empty : Guid.Empty;
    public bool PermitsAddress(string address, bool diagnostic)
    {
        if (Mode == NotificationMode.Capture) return true;
        if (Policy is null) return false;
        if (diagnostic || Mode == NotificationMode.ControlledTest)
            return string.Equals(address, Policy.DiagnosticTarget, StringComparison.OrdinalIgnoreCase);
        var at = address.LastIndexOf('@');
        return Policy.RecipientAddresses.Contains(address, StringComparer.OrdinalIgnoreCase)
            || at > 0 && Policy.RecipientDomains.Contains(address[(at + 1)..], StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Authority lives outside database/configuration backups. Application code never creates it.</summary>
public sealed class NotificationInstallationAuthority(IConfiguration configuration)
{
    public string InstallationId => Guid.TryParse(configuration["Instance:InstanceId"], out var id) ? id.ToString("D") : "unconfigured";
    public string HostIdentity => Environment.MachineName;
    public string AuthorityPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("AEROLINK_NOTIFICATION_AUTHORITY_ROOT");
            root = string.IsNullOrWhiteSpace(root)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroLink", "notification-authority")
                : Path.GetFullPath(root);
            return Path.Combine(root, InstallationId + ".json");
        }
    }
    public (NotificationInstallationPolicy? Policy, string Hash) Read()
    {
        try
        {
            if (InstallationId == "unconfigured" || !File.Exists(AuthorityPath)) return (null, "");
            var file = new FileInfo(AuthorityPath);
            if (file.Length > 32_768 || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, "");
            var witnessPath = AuthorityPath + ".generation";
            var witness = new FileInfo(witnessPath);
            if (!witness.Exists || witness.Length > 1024 || witness.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, "");
            var generation = File.ReadAllText(witnessPath).Trim();
            var json = File.ReadAllText(AuthorityPath);
            var policy = JsonSerializer.Deserialize<NotificationInstallationPolicy>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (policy is null || policy.InstallationId != InstallationId || policy.HostIdentity != HostIdentity
                || generation != policy.SendGeneration.ToString("D") || policy.SendGeneration == Guid.Empty || policy.PolicyRevision == Guid.Empty
                || policy.RelayHosts is null || policy.Senders is null || policy.RecipientDomains is null || policy.RecipientAddresses is null)
                return (null, "");
            return (policy, Hash(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return (null, ""); }
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class NotificationContentProtection(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("AeroLink.Notifications.ProtectedEvidence.v1");
    public string Protect(string value) => value.Length == 0 ? "" : protector.Protect(value);
    public string Unprotect(string value) => value.Length == 0 ? "" : protector.Unprotect(value);
    public static string AddressHash(string value) => NotificationInstallationAuthority.Hash(value.Trim().ToLowerInvariant());
}

/// <summary>One coherent immutable resolution per attempt, independent of the business save boundary.</summary>
public sealed class NotificationSettingsResolver(AeroLinkDbContext db, IConfiguration configuration,
    NotificationInstallationAuthority authority, NotificationContentProtection protection)
{
    public async Task<ResolvedNotificationSettings> ResolveAsync(CancellationToken ct)
    {
        var state = await db.NotificationInstallationStates.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == authority.InstallationId, ct);
        var saved = state?.SettingsRevisionId is Guid id
            ? await db.NotificationSettingsRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var (policy, hash) = authority.Read();
        string Bootstrap(string key) => configuration["Notifications:" + key]?.Trim() ?? "";
        bool Locked(string key) => Bootstrap(key).Length > 0;
        try
        {
            string Value(string key, string? stored) => Locked(key) ? Bootstrap(key) : protection.Unprotect(stored ?? "");
            var host = Value("Smtp:Host", saved?.Host); var sender = Value("Smtp:From", saved?.Sender);
            var baseUrl = Value("BaseUrl", saved?.BaseUrl); var user = Value("Smtp:UserName", saved?.UserName);
            var credential = Value("Smtp:Password", saved?.ProtectedCredential);
            var port = Locked("Smtp:Port") ? (int.TryParse(Bootstrap("Smtp:Port"), out var configuredPort) && configuredPort is > 0 and <= 65535 ? configuredPort : 0) : saved?.Port ?? 25;
            var mode = saved?.Mode ?? NotificationMode.Disabled;
            var code = "";
            if (mode != NotificationMode.Disabled)
            {
                if (port is < 1 or > 65535 || !MimeKit.MailboxAddress.TryParse(sender, out _)) code = "InvalidSettings";
                if (mode == NotificationMode.Capture)
                {
                    // Do not resolve arbitrary DNS names and call them loopback after a TOCTOU check.
                    if (host is not ("127.0.0.1" or "::1")) code = "CaptureRequiresOwnedLoopback";
                }
                else
                {
                    if (policy is null || mode > policy.MaximumMode) code = "InstallationAuthorityBlocked";
                    else if (!policy.RelayHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
                        || !policy.Senders.Contains(sender, StringComparer.OrdinalIgnoreCase)
                        || baseUrl != policy.BaseUrl || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                        || uri.Scheme != "https" || mode == NotificationMode.Live && uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
                        code = "InstallationPolicyBlocked";
                }
            }
            return new(authority.InstallationId, state?.Version ?? 0, saved?.Id ?? Guid.Empty, mode, host,
                port, sender, saved?.DisplayName ?? "AeroLink", baseUrl.TrimEnd('/'), user, credential, policy,
                hash, code, Locked("Smtp:Host"), Locked("Smtp:From"), Locked("BaseUrl"),
                Locked("Smtp:UserName") || Locked("Smtp:Password") || !(policy?.AllowManagedCredentials ?? false))
            { AdmissionEnabled = state?.SendingEnabled == true, AdmissionEpochId = state?.AdmissionEpochId, EnabledEventTypes = saved is null ? NotificationOperationsService.InitialEventTypes : JsonSerializer.Deserialize<string[]>(saved.AllowedEventTypesJson) ?? [] };
        }
        catch (CryptographicException)
        {
            return new(authority.InstallationId, state?.Version ?? 0, saved?.Id ?? Guid.Empty,
                saved?.Mode ?? NotificationMode.Disabled, "", 25, "", "AeroLink", "", "", "", policy, hash,
                "CredentialKeyringBlocked", true, true, true, true);
        }
    }
}
