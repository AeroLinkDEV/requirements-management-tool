using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The real host boundary for a Windows-password signature reauthentication. The application never receives a
/// directory password outside this call, and the validator returns only whether the supplied proof matched the
/// persisted SID and principal.
/// </summary>
public interface IWindowsCredentialValidator
{
    Task<bool> ValidateAsync(string principal, string expectedSid, string password, CancellationToken ct);
}

/// <summary>Validates a persisted Windows identity with the non-interactive network logon contract.</summary>
[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryWindowsCredentialValidator : IWindowsCredentialValidator
{
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(
        string userName, string domain, string password, int logonType, int logonProvider,
        out SafeAccessTokenHandle token);

    public Task<bool> ValidateAsync(string principal, string expectedSid, string password, CancellationToken ct)
    {
        // LogonUser is a blocking native call. Cancellation stops the request's wait, but cannot interrupt the
        // operating-system operation; no callback from it mutates application state.
        return Task.Run(() => ValidateCore(principal, expectedSid, password), CancellationToken.None).WaitAsync(ct);
    }

    private static bool ValidateCore(string principal, string expectedSid, string password)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(password)) return false;
        var separator = principal.LastIndexOf('\\');
        if (separator <= 0 || separator == principal.Length - 1) return false;
        var domain = principal[..separator].Trim();
        var userName = principal[(separator + 1)..].Trim();
        if (domain.Length == 0 || userName.Length == 0) return false;
        if (!LogonUser(userName, domain, password, Logon32LogonNetwork, Logon32ProviderDefault, out var token))
        {
            _ = new Win32Exception(Marshal.GetLastWin32Error());
            return false;
        }
        using (token)
        {
            return WindowsIdentity.RunImpersonated(token, () =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                var actualSid = identity.User?.Value;
                var actualPrincipal = identity.Name;
                return string.Equals(actualSid, expectedSid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(actualPrincipal, principal, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
