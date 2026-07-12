using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record AuthenticatedUser(Guid Id, string UserName, string DisplayName, string Email, bool IsAdministrator, IReadOnlyList<UserProgramAccess> Programs);
public sealed record UserProgramAccess(Guid ProgramId, IReadOnlyList<string> Roles);
public sealed record LoginResult(AuthenticatedUser User, string Token, DateTimeOffset ExpiresAt);

public sealed class IdentityService(AeroLinkDbContext db)
{
    public const string CookieName = "aerolink_session";
    public static string HashPassword(string password)
    {
        if (password.Length < 10) throw new ArgumentException("Password must contain at least 10 characters.");
        var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$310000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool VerifyPassword(string password, string encoded)
    {
        try { var p = encoded.Split('$'); var salt = Convert.FromBase64String(p[2]); var expected = Convert.FromBase64String(p[3]); var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length); return CryptographicOperations.FixedTimeEquals(actual, expected); }
        catch { return false; }
    }
    public async Task<LoginResult?> LoginAsync(string userName, string password, string ip, string userAgent, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = userName.Trim().ToLowerInvariant(); var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.UserName == normalized, ct);
        if (user is null) { db.SecurityAuditEvents.Add(new("Login", normalized, "session", "Denied", "Unknown account.", ip, now)); await db.SaveChangesAsync(ct); return null; }
        if (user.State != AccountState.Active || !VerifyPassword(password, user.PasswordHash)) { user.LoginFailed(); db.SecurityAuditEvents.Add(new("Login", normalized, "session", "Denied", user.State == AccountState.Active ? "Invalid credentials." : $"Account is {user.State}.", ip, now)); await db.SaveChangesAsync(ct); return null; }
        user.LoginSucceeded(now); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); var expires = now.AddHours(12);
        db.UserSessions.Add(new(user.Id, TokenHash(token), ip, userAgent, now, expires)); db.SecurityAuditEvents.Add(new("Login", user.UserName, "session", "Success", "Authenticated session created.", ip, now)); await db.SaveChangesAsync(ct);
        return new(await MapAsync(user, now, ct), token, expires);
    }
    public async Task<AuthenticatedUser?> ResolveAsync(string? token, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null; var hash = TokenHash(token); var session = await db.UserSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (session is null || !session.IsValid(now)) return null; var user = await db.UserAccounts.SingleAsync(x => x.Id == session.UserId, ct); if (user.State != AccountState.Active) return null;
        if ((now - session.LastSeenAt).TotalMinutes >= 5) { session.Touch(now); await db.SaveChangesAsync(ct); }
        return await MapAsync(user, now, ct);
    }
    public async Task LogoutAsync(string? token, string ip, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return; var hash = TokenHash(token); var session = await db.UserSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, ct); if (session is null) return;
        session.Revoke(now); var userName = await db.UserAccounts.Where(x => x.Id == session.UserId).Select(x => x.UserName).SingleAsync(ct); db.SecurityAuditEvents.Add(new("Logout", userName, "session", "Success", "Session revoked.", ip, now)); await db.SaveChangesAsync(ct);
    }
    public async Task<bool> ConfirmPasswordAsync(Guid userId, string password, CancellationToken ct) { var hash = await db.UserAccounts.Where(x => x.Id == userId).Select(x => x.PasswordHash).SingleAsync(ct); return VerifyPassword(password, hash); }
    public async Task<bool> HasRoleAsync(AuthenticatedUser user, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        if (user.IsAdministrator) return true; if (user.Programs.Any(x => x.ProgramId == programId && x.Roles.Contains(role.ToString()))) return true;
        return await db.RoleDelegations.AnyAsync(x => x.ProgramId == programId && x.DelegateUserId == user.Id && x.Role == role && x.RevokedAt == null && x.StartsAt <= now && x.EndsAt > now, ct);
    }
    private async Task<AuthenticatedUser> MapAsync(UserAccount user, DateTimeOffset now, CancellationToken ct)
    {
        var memberships = await db.ProgramMemberships.AsNoTracking().Where(x => x.UserId == user.Id).ToListAsync(ct);
        var programs = memberships.GroupBy(x => x.ProgramId).Select(g => new UserProgramAccess(g.Key, g.Select(x => x.Role.ToString()).Order().ToList())).ToList();
        return new(user.Id, user.UserName, user.DisplayName, user.Email, user.UserName == "admin" || memberships.Any(x => x.Role == ProgramRole.Administrator), programs);
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed class IdentitySeeder(AeroLinkDbContext db)
{
    public const string DemoPassword = "AeroLink!2026";
    private static readonly (string User, string Name, string Email, ProgramRole[] Roles)[] People =
    [
        ("admin", "AeroLink Administrator", "admin@aerolink.local", [ProgramRole.Administrator]),
        ("engineer.demo", "Sean Engineer", "sean.engineer@aerolink.local", [ProgramRole.Engineer]),
        ("systems.author", "Systems Requirements Author", "systems.author@aerolink.local", [ProgramRole.Engineer]),
        ("software.author", "Software Requirements Author", "software.author@aerolink.local", [ProgramRole.Engineer]),
        ("systems.reviewer", "Systems Engineer", "systems.reviewer@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("assurance.reviewer", "Development Assurance Reviewer", "assurance@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("lead.reviewer", "Engineering Lead", "engineering.lead@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("software.lead", "Software Engineering Lead", "software.lead@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("systems.lead", "Systems Engineering Lead", "systems.lead@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("engineering.manager", "Engineering Manager", "engineering.manager@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver]),
        ("manager.reviewer", "Engineering Manager", "manager.reviewer@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver]),
        ("program.manager", "Program Manager", "program.manager@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver]),
        ("release.manager", "Release Manager", "release.manager@aerolink.local", [ProgramRole.ConfigurationManager, ProgramRole.ProgramManager]),
        ("cm.fms", "Configuration Manager", "configuration@aerolink.local", [ProgramRole.ConfigurationManager]),
        ("test.author", "Verification Author", "test.author@aerolink.local", [ProgramRole.TestEngineer]),
        ("test.engineer", "Verification Engineer", "test.engineer@aerolink.local", [ProgramRole.TestEngineer])
    ];
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow; var programs = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        foreach (var person in People)
        {
            var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.UserName == person.User, ct);
            if (user is null) { user = new(person.User, person.Name, person.Email, IdentityService.HashPassword(DemoPassword), now); db.UserAccounts.Add(user); await db.SaveChangesAsync(ct); }
            foreach (var program in programs) foreach (var role in person.Roles)
                if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == user.Id && x.ProgramId == program && x.Role == role, ct)) db.ProgramMemberships.Add(new(user.Id, program, role, "system.bootstrap", now));
        }
        await db.SaveChangesAsync(ct);
    }
}
