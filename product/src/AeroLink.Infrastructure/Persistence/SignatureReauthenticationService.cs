using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record SignatureAuthenticationResult(string AuthenticationMethod);

/// <summary>
/// The one server-side boundary for electronic-signature reauthentication. Route handlers retain resource and
/// authority checks; this service chooses the credential proof from the freshly loaded account link, records
/// lockout failures, and returns the method to persist with the immutable signature.
/// </summary>
public sealed class SignatureReauthenticationService(
    AeroLinkDbContext db,
    IWindowsCredentialValidator? windowsCredentials = null)
{
    public const string WindowsPasswordMethod = "windows-password";

    public async Task<SignatureAuthenticationResult?> AuthenticateAsync(
        Guid userId, string? password, string remoteAddress, DateTimeOffset now, CancellationToken ct)
    {
        var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;
        var method = user.WindowsSid is not null ? WindowsPasswordMethod : IdentityService.PasswordMethod;
        var valid = false;
        if (user.State == AccountState.Active && !string.IsNullOrEmpty(password))
        {
            if (user.WindowsSid is not null)
            {
                valid = windowsCredentials is not null
                    && !string.IsNullOrWhiteSpace(user.WindowsPrincipal)
                    && await windowsCredentials.ValidateAsync(user.WindowsPrincipal!, user.WindowsSid, password, ct);
            }
            else if (user.HasPassword)
            {
                valid = IdentityService.VerifyPassword(password, user.PasswordHash);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (valid)
        {
            user.LoginSucceeded(now);
            return new(method);
        }

        if (user.State == AccountState.Active) user.LoginFailed();
        db.SecurityAuditEvents.Add(new SecurityAuditEvent(
            "SignatureAuthentication", user.UserName, "electronic-signature", "Denied",
            "Electronic signature confirmation failed.", remoteAddress, now)
            .WithAuthentication(method, user.WindowsPrincipal));
        await db.SaveChangesAsync(ct);
        return null;
    }
}
