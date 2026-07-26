using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace AeroLink.Api;

/// <summary>
/// Signing in, proving who you are, and standing a deployment up for the first time.
///
/// Nothing else in the product can be reached without passing through here. Two of these are deliberately
/// reachable without a session, because a deployment has to be brought up by somebody before any session can
/// exist: the bootstrap secret and a password policy are what stand in for one.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/setup/status", async (AeroLinkDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            var bootstrapRequired = !await db.UserAccounts.AsNoTracking().AnyAsync(ct);
            return Results.Ok(new
            {
                bootstrapRequired,
                bootstrapEnabled = bootstrapRequired && BootstrapSecret(configuration) is not null
            });
        });

        app.MapPost("/api/setup/bootstrap", async (BootstrapAdministratorRequest request, HttpContext http, AeroLinkDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            var configuredSecret = BootstrapSecret(configuration);
            if (configuredSecret is null)
                return Results.Json(new { error = "First-install bootstrap is not configured.", code = "bootstrap_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!FixedTimeSecretEquals(http.Request.Headers["X-AeroLink-Bootstrap-Secret"].ToString(), configuredSecret))
                return Results.Json(new { error = "Bootstrap authorization failed.", code = "bootstrap_authorization_failed" }, statusCode: StatusCodes.Status401Unauthorized);
            var passwordError = BootstrapPasswordError(request.Password);
            if (passwordError is not null) return Results.BadRequest(new { error = passwordError, code = "bootstrap_password_policy" });

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await db.UserAccounts.AnyAsync(ct))
                return Results.Conflict(new { error = "First-install bootstrap has already been completed.", code = "bootstrap_complete" });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var administrator = new UserAccount(IdentityService.SystemAdministratorUserName, request.DisplayName, request.Email, IdentityService.HashPassword(request.Password), now);
                db.UserAccounts.Add(administrator);
                db.SecurityAuditEvents.Add(new("BootstrapAdministratorCreated", "system.bootstrap", administrator.UserName, "Success", "Created the one-time first-install global administrator.", http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/admin/users/{administrator.Id}", new { administrator.Id, administrator.UserName, administrator.DisplayName });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "First-install bootstrap was completed by another request.", code = "bootstrap_complete" }); }
        }).RequireRateLimiting("authentication");

        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, IdentityService identity, CancellationToken ct) =>
        {
            var result = await identity.LoginAsync(request.UserName, request.Password, http.Connection.RemoteIpAddress?.ToString() ?? "local", http.Request.Headers.UserAgent.ToString(), DateTimeOffset.UtcNow,request.MfaCode, ct);
            if (result is null) return Results.Json(new { error = "The credentials or second-factor code were not accepted." }, statusCode: 401);
            var secureCookie = app.Configuration.GetValue<bool?>("Identity:CookieSecure") ?? !app.Environment.IsDevelopment();
            http.Response.Cookies.Append(IdentityService.CookieName, result.Token, new CookieOptions { HttpOnly = true, Secure = secureCookie, SameSite = SameSiteMode.Lax, Expires = result.ExpiresAt, Path = "/" });
            return Results.Ok(result.User);
        }).RequireRateLimiting("authentication");

        app.MapPost("/api/auth/logout", async (HttpContext http, IdentityService identity, CancellationToken ct) => { await identity.LogoutAsync(http.Request.Cookies[IdentityService.CookieName], http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow, ct); http.Response.Cookies.Delete(IdentityService.CookieName); return Results.NoContent(); });

        app.MapGet("/api/auth/sessions", async (HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
        {
            var actor=http.UserAccount();return Results.Ok(await db.UserSessions.AsNoTracking().Where(x=>x.UserId==actor.Id).OrderByDescending(x=>x.LastSeenAt).Select(x=>new{x.Id,x.IpAddress,x.UserAgent,x.CreatedAt,x.LastSeenAt,x.ExpiresAt,x.RevokedAt}).ToListAsync(ct));
        });

        app.MapPost("/api/auth/sessions/revoke-others", async (HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
        {
            var actor=http.UserAccount();var currentHash=IdentityService.TokenDigest(http.Request.Cookies[IdentityService.CookieName]);var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==actor.Id&&x.RevokedAt==null&&x.TokenHash!=currentHash).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("SessionsRevoked",actor.UserName,"session","Success",$"Revoked {sessions.Count} other active session(s).",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);return Results.Ok(new{revoked=sessions.Count});
        });

        app.MapPost("/api/auth/password", async (ChangeOwnPasswordRequest request,HttpContext http,IdentityService identity,AeroLinkDbContext db,CancellationToken ct) =>
        {
            var actor=http.UserAccount();if(!await identity.ConfirmPasswordAsync(actor.Id,request.CurrentPassword,ct))return Results.Json(new{error="Current password confirmation failed."},statusCode:401);try{var user=await db.UserAccounts.SingleAsync(x=>x.Id==actor.Id,ct);user.ChangePassword(IdentityService.HashPassword(request.NewPassword));var now=DateTimeOffset.UtcNow;var sessions=await db.UserSessions.Where(x=>x.UserId==actor.Id&&x.RevokedAt==null).ToListAsync(ct);foreach(var session in sessions)session.Revoke(now);db.SecurityAuditEvents.Add(new("PasswordChanged",actor.UserName,user.UserName,"Success","Password changed and all sessions revoked.",http.Connection.RemoteIpAddress?.ToString()??"local",now));await db.SaveChangesAsync(ct);http.Response.Cookies.Delete(IdentityService.CookieName);return Results.NoContent();}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/auth/mfa/enroll",async(HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            var actor=http.UserAccount();var prior=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);
            if(prior?.Confirmed==true)return Results.Conflict(new{error="MFA is already enabled. Confirm the current factor before rotating it.",code="mfa_already_enabled"});
            if(prior is not null)db.UserMfaEnrollments.Remove(prior);var secret=IdentityService.CreateMfaSecret();var enrollment=new UserMfaEnrollment(actor.Id,identity.ProtectMfaSecret(secret),actor.UserName,DateTimeOffset.UtcNow);db.UserMfaEnrollments.Add(enrollment);await db.SaveChangesAsync(ct);
            var label=Uri.EscapeDataString($"AeroLink:{actor.UserName}");var issuer=Uri.EscapeDataString("AeroLink");return Results.Ok(new{enrollment.Id,secret,otpauthUri=$"otpauth://totp/{label}?secret={Uri.EscapeDataString(secret)}&issuer={issuer}&algorithm=SHA1&digits=6&period=30"});
        });

        app.MapPost("/api/auth/mfa/confirm",async(ConfirmMfaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>{var actor=http.UserAccount();var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);if(enrollment is null||enrollment.Confirmed)return Results.NotFound();if(!IdentityService.VerifyTotp(identity.RevealMfaSecret(enrollment.Secret),request.Code,DateTimeOffset.UtcNow))return Results.BadRequest(new{error="The authenticator code is not valid."});enrollment.Confirm(DateTimeOffset.UtcNow);var codes=Enumerable.Range(0,10).Select(_=>IdentityService.NewRecoveryCode()).ToList();db.MfaRecoveryCodes.RemoveRange(db.MfaRecoveryCodes.Where(x=>x.UserId==actor.Id));db.MfaRecoveryCodes.AddRange(codes.Select(code=>new MfaRecoveryCode(actor.Id,IdentityService.RecoveryHash(code),DateTimeOffset.UtcNow)));db.SecurityAuditEvents.Add(new("MfaEnabled",actor.UserName,"mfa","Success","Authenticator enrollment confirmed and recovery codes generated.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.Ok(new{recoveryCodes=codes});});

        app.MapGet("/api/auth/security",async(HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var actor=http.UserAccount();var enrollment=await db.UserMfaEnrollments.AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==actor.Id,ct);var recovery=await db.MfaRecoveryCodes.AsNoTracking().Where(x=>x.UserId==actor.Id).ToListAsync(ct);var sessions=await db.UserSessions.AsNoTracking().Where(x=>x.UserId==actor.Id&&x.RevokedAt==null).ToListAsync(ct);return Results.Ok(new{mfaEnabled=enrollment?.Confirmed==true,mfaPending=enrollment is not null&&!enrollment.Confirmed,recoveryCodesRemaining=recovery.Count(x=>x.UsedAt==null),activeSessions=sessions.Count});});

        app.MapPost("/api/auth/mfa/disable",async(DisableMfaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>{var actor=http.UserAccount();if(!await identity.ConfirmPasswordAsync(actor.Id,request.Password,ct))return Results.Json(new{error="Password confirmation failed."},statusCode:401);var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==actor.Id&&x.Confirmed,ct);if(enrollment is null)return Results.NotFound();var valid=IdentityService.VerifyTotp(identity.RevealMfaSecret(enrollment.Secret),request.Code,DateTimeOffset.UtcNow);if(!valid){var recovery=await db.MfaRecoveryCodes.SingleOrDefaultAsync(x=>x.UserId==actor.Id&&x.CodeHash==IdentityService.RecoveryHash(request.Code)&&x.UsedAt==null,ct);valid=recovery is not null;}if(!valid)return Results.Json(new{error="A current authenticator or unused recovery code is required."},statusCode:401);db.UserMfaEnrollments.Remove(enrollment);db.MfaRecoveryCodes.RemoveRange(db.MfaRecoveryCodes.Where(x=>x.UserId==actor.Id));db.SecurityAuditEvents.Add(new("MfaDisabled",actor.UserName,"mfa","Success","MFA was disabled after password and second-factor confirmation.",http.Connection.RemoteIpAddress?.ToString()??"local",DateTimeOffset.UtcNow));await db.SaveChangesAsync(ct);return Results.NoContent();});

        app.MapGet("/api/auth/me", (HttpContext http) => Results.Ok(http.UserAccount()));

        app.MapGet("/api/auth/csrf", (HttpContext http,BrowserMutationProtector protector) =>
        {
            var session=http.Request.Cookies[IdentityService.CookieName];return Results.Ok(new{token=protector.Issue(session!),header="X-AeroLink-CSRF"});
        });
    }

    static string? BootstrapSecret(IConfiguration configuration)
    {
        var value = configuration["Identity:BootstrapSecret"]?.Trim();
        return value is { Length: >= 32 } ? value : null;
    }

    static bool FixedTimeSecretEquals(string supplied, string configured)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
    }

    static string? BootstrapPasswordError(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 14)
            return "The bootstrap administrator password must contain at least 14 characters.";
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || !password.Any(ch => !char.IsLetterOrDigit(ch)))
            return "The bootstrap administrator password must include uppercase, lowercase, numeric, and symbol characters.";
        return null;
    }
}
