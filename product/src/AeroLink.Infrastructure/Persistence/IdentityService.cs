using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record AuthenticatedUser(Guid Id, string UserName, string DisplayName, string Email, bool IsAdministrator, IReadOnlyList<UserProgramAccess> Programs, bool MustChangePassword = false);
public sealed record UserProgramAccess(Guid ProgramId, IReadOnlyList<string> Roles);
public sealed record LoginResult(AuthenticatedUser User, string Token, DateTimeOffset ExpiresAt);

public sealed class IdentityService(AeroLinkDbContext db, IDataProtectionProvider? dataProtection = null)
{
    private const string ProtectedMfaPrefix = "dp:v1:";
    private readonly IDataProtector _mfaProtector = (dataProtection ?? new EphemeralDataProtectionProvider()).CreateProtector("AeroLink.Identity.MfaSecret.v1");
    public const string CookieName = "aerolink_session";
    public const string SystemAdministratorUserName = "admin";
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
        => await LoginAsync(userName,password,ip,userAgent,now,null,ct);
    public async Task<LoginResult?> LoginAsync(string userName, string password, string ip, string userAgent, DateTimeOffset now, string? mfaCode, CancellationToken ct)
    {
        var normalized = userName.Trim().ToLowerInvariant(); var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.UserName == normalized, ct);
        if (user is null) { db.SecurityAuditEvents.Add(new("Login", normalized, "session", "Denied", "Unknown account.", ip, now)); await db.SaveChangesAsync(ct); return null; }
        if (user.State != AccountState.Active || !VerifyPassword(password, user.PasswordHash)) { user.LoginFailed(); db.SecurityAuditEvents.Add(new("Login", normalized, "session", "Denied", user.State == AccountState.Active ? "Invalid credentials." : $"Account is {user.State}.", ip, now)); await db.SaveChangesAsync(ct); return null; }
        var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==user.Id&&x.Confirmed,ct);if(enrollment is not null){var valid=VerifyTotp(RevealMfaSecret(enrollment.Secret),mfaCode??"",now);if(!valid&&!string.IsNullOrWhiteSpace(mfaCode)){var recovery=await db.MfaRecoveryCodes.SingleOrDefaultAsync(x=>x.UserId==user.Id&&x.CodeHash==RecoveryHash(mfaCode)&&x.UsedAt==null,ct);if(recovery is not null){recovery.Use(now);valid=true;}}if(!valid){db.SecurityAuditEvents.Add(new("MfaChallenge",user.UserName,"session","Denied","A valid authenticator or unused recovery code is required.",ip,now));await db.SaveChangesAsync(ct);return null;}}
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
        if (user.IsAdministrator) return true;
        // A more precise job title must never remove capability: somebody recorded as a System Engineer is
        // an engineer, and every place that asks for Engineer has to accept them.
        var accepted = ProgramRoleAuthority.Satisfying(role).Select(x => x.ToString()).ToList();
        if (user.Programs.Any(x => x.ProgramId == programId && x.Roles.Any(accepted.Contains))) return true;
        if (await IsStandingBackupAsync(user.Id, programId, role, ct)) return true;
        var delegations = await db.RoleDelegations.AsNoTracking().Where(x => x.ProgramId == programId && x.DelegateUserId == user.Id && x.Role == role && x.RevokedAt == null).ToListAsync(ct);
        return delegations.Any(x => x.StartsAt <= now && x.EndsAt > now);
    }

    /// <summary>
    /// Whether somebody may act in a role because the project named them the backup for it.
    ///
    /// Deliberately unbounded in time: a backup stands until it is removed, so unlike a delegation there is no
    /// interval to test. The backup must still be a current member of the project — naming somebody who has
    /// since left must not keep letting them sign.
    /// </summary>
    private async Task<bool> IsStandingBackupAsync(Guid userId, Guid programId, ProgramRole role, CancellationToken ct)
    {
        var backedRoles = await db.ProjectRoleBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.BackupUserId == userId && x.RemovedAt == null)
            .Select(x => x.Role)
            .ToListAsync(ct);
        if (backedRoles.Count == 0) return false;
        if (!backedRoles.Any(ProgramRoleAuthority.Satisfying(role).Contains)) return false;
        return await db.ProgramMemberships.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null, ct);
    }
    public async Task<bool> HasRoleAsync(Guid userId, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.State == AccountState.Active, ct);
        if (account is null) return false;
        if (account.UserName == SystemAdministratorUserName) return true;
        // The same implication as the overload above. Two copies of this check exist and both are reached
        // from live authorization paths, so a rule applied to only one of them is a rule that holds by luck.
        var accepted = ProgramRoleAuthority.Satisfying(role);
        if (await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role), ct)) return true;
        if (await IsStandingBackupAsync(userId, programId, role, ct)) return true;
        var delegations = await db.RoleDelegations.AsNoTracking().Where(x => x.ProgramId == programId && x.DelegateUserId == userId && x.Role == role && x.RevokedAt == null).ToListAsync(ct);
        return delegations.Any(x => x.StartsAt <= now && x.EndsAt > now);
    }
    private async Task<AuthenticatedUser> MapAsync(UserAccount user, DateTimeOffset now, CancellationToken ct)
    {
        // Ended memberships are retained as history and must never reach a session's authority set.
        var memberships = await db.ProgramMemberships.AsNoTracking().Where(x => x.UserId == user.Id && x.EndedAt == null).ToListAsync(ct);
        var programs = memberships.GroupBy(x => x.ProgramId).Select(g => new UserProgramAccess(g.Key, g.Select(x => x.Role.ToString()).Order().ToList())).ToList();
        return new(user.Id, user.UserName, user.DisplayName, user.Email, user.UserName == SystemAdministratorUserName, programs, user.MustChangePassword);
    }
    public static string? TokenDigest(string? token) => string.IsNullOrWhiteSpace(token)?null:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public static string CreateMfaSecret()=>Base32Encode(RandomNumberGenerator.GetBytes(20));
    public string ProtectMfaSecret(string secret)=>ProtectedMfaPrefix+_mfaProtector.Protect(secret);
    public string RevealMfaSecret(string storedSecret)=>storedSecret.StartsWith(ProtectedMfaPrefix,StringComparison.Ordinal)?_mfaProtector.Unprotect(storedSecret[ProtectedMfaPrefix.Length..]):storedSecret;
    public static bool VerifyTotp(string secret,string code,DateTimeOffset now)
    {if(code.Length!=6||!code.All(char.IsDigit))return false;return Enumerable.Range(-1,3).Select(offset=>Totp(secret,now.AddSeconds(offset*30))).Any(expected=>CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(code)));}
    public static string RecoveryHash(string code)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()))).ToLowerInvariant();
    public static string NewRecoveryCode()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
    private static string Totp(string secret,DateTimeOffset now){var counter=BitConverter.GetBytes(now.ToUnixTimeSeconds()/30);if(BitConverter.IsLittleEndian)Array.Reverse(counter);using var hmac=new HMACSHA1(DecodeMfaSecret(secret));var hash=hmac.ComputeHash(counter);var offset=hash[^1]&15;var value=((hash[offset]&127)<<24)|(hash[offset+1]<<16)|(hash[offset+2]<<8)|hash[offset+3];return (value%1_000_000).ToString("D6");}
    private static byte[] DecodeMfaSecret(string secret)
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var normalized=secret.Trim().TrimEnd('=').ToUpperInvariant();var output=new List<byte>();var buffer=0;var bits=0;
        foreach(var ch in normalized){var value=alphabet.IndexOf(ch);if(value<0){try{return Convert.FromBase64String(secret);}catch{throw new FormatException("MFA secret is not valid Base32.");}}buffer=(buffer<<5)|value;bits+=5;if(bits>=8){bits-=8;output.Add((byte)((buffer>>bits)&255));}}
        return output.ToArray();
    }
    private static string Base32Encode(byte[] bytes)
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var output=new StringBuilder();var buffer=0;var bits=0;
        foreach(var value in bytes){buffer=(buffer<<8)|value;bits+=8;while(bits>=5){bits-=5;output.Append(alphabet[(buffer>>bits)&31]);}}
        if(bits>0)output.Append(alphabet[(buffer<<(5-bits))&31]);return output.ToString();
    }
    private static string TokenHash(string token) => TokenDigest(token)!;
}

public sealed class IdentitySeeder(AeroLinkDbContext db)
{
    public const string DemoPassword = "AeroLink!2026";
    private static readonly (string User, string Name, string Email, ProgramRole[] Roles)[] People =
    [
        ("admin", "AeroLink Administrator", "admin@aerolink.local", [ProgramRole.Administrator]),
        ("engineer.demo", "Sean Engineer", "sean.engineer@aerolink.local", [ProgramRole.Engineer]),
        ("systems.author", "Systems Requirements Author", "systems.author@aerolink.local", [ProgramRole.Engineer, ProgramRole.SystemEngineer]),
        ("software.author", "Software Requirements Author", "software.author@aerolink.local", [ProgramRole.Engineer, ProgramRole.SoftwareEngineer]),
        ("systems.reviewer", "Systems Engineer", "systems.reviewer@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("assurance.reviewer", "Development Assurance Reviewer", "assurance@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("lead.reviewer", "Maya Patel", "maya.patel@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver]),
        ("software.lead", "Rina Shah", "software.lead@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.SoftwareEngineeringLead]),
        ("systems.lead", "Systems Engineering Lead", "systems.lead@aerolink.local", [ProgramRole.Reviewer, ProgramRole.Approver, ProgramRole.SystemEngineeringLead]),
        ("engineering.manager", "Engineering Manager", "engineering.manager@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver, ProgramRole.EngineeringManager]),
        // Named, like the rest of the cast. "Engineering Manager" is what this person does, not who they are,
        // and an approval step that reads it as a name leaves the reader unable to tell a colleague from a job
        // title. The title still says Engineering Manager — it is derived from the account, not the name.
        ("manager.reviewer", "Olivia Chen", "manager.reviewer@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver]),
        ("program.manager", "Olivia Chen", "olivia.chen@aerolink.local", [ProgramRole.ProgramManager, ProgramRole.Approver]),
        ("release.manager", "Daniel Reyes", "daniel.reyes@aerolink.local", [ProgramRole.ConfigurationManager, ProgramRole.ProgramManager]),
        ("cm.fms", "Configuration Manager", "configuration@aerolink.local", [ProgramRole.ConfigurationManager]),
        ("test.author", "Verification Author", "test.author@aerolink.local", [ProgramRole.TestEngineer]),
        ("test.engineer", "Ethan Brooks", "ethan.brooks@aerolink.local", [ProgramRole.TestEngineer]),
        // The two oversight roles, and the lead who answers for the Project as a whole. They read everything
        // in the Program, which membership alone grants, and hold no authority over engineering content.
        ("airworthiness.lead", "Priya Raman", "priya.raman@aerolink.local", [ProgramRole.Airworthiness]),
        ("quality.analyst", "Marcus Hale", "marcus.hale@aerolink.local", [ProgramRole.SoftwareQualityAnalyst]),
        ("project.lead", "Nadia Okoro", "nadia.okoro@aerolink.local", [ProgramRole.ProjectEngineeringLead])
    ];
    private static readonly string[] FirstNames = ["Avery","Blake","Cameron","Casey","Devon","Emerson","Finley","Harper","Jordan","Kai","Logan","Morgan","Parker","Quinn","Reese","Riley","Rowan","Sage","Sawyer","Taylor","Alex","Jamie","Robin"];
    private static readonly string[] LastNames = ["Anderson","Bennett","Campbell","Chen","Clarke","Dubois","Evans","Foster","Garcia","Gupta","Harris","Ibrahim","Johnson","Kim","Lewis","Martin","Nguyen","Patel","Robinson","Wilson"];
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var programs = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var demoPasswordHash = IdentityService.HashPassword(DemoPassword);
        var directory = People.Concat(GeneratedPeople()).ToList();
        var userNames = directory.Select(x => x.User).ToList();
        var users = (await db.UserAccounts.Where(x => userNames.Contains(x.UserName)).ToListAsync(ct))
            .ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
        var membershipKeys = (await db.ProgramMemberships.AsNoTracking()
                .Where(x => programs.Contains(x.ProgramId))
                .Select(x => new { x.UserId, x.ProgramId, x.Role })
                .ToListAsync(ct))
            .Select(x => (x.UserId, x.ProgramId, x.Role))
            .ToHashSet();
        var curatedUsers = People.Select(x => x.User).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var person in directory)
        {
            if (!users.TryGetValue(person.User, out var user))
            {
                user = new(person.User, person.Name, person.Email, demoPasswordHash, now);
                db.UserAccounts.Add(user);
                users[person.User] = user;
            }
            else if (curatedUsers.Contains(person.User) && (user.DisplayName != person.Name || user.Email != person.Email))
                user.RefreshDirectoryProfile(person.Name, person.Email);
            foreach (var program in programs) foreach (var role in person.Roles)
                if (membershipKeys.Add((user.Id, program, role)))
                    db.ProgramMemberships.Add(new(user.Id, program, role, "system.bootstrap", now));
        }
        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<(string User, string Name, string Email, ProgramRole[] Roles)> GeneratedPeople()
    {
        for (var index = 0; index < 184; index++)
        {
            var name = $"{FirstNames[index % FirstNames.Length]} {LastNames[(index * 7) % LastNames.Length]}";
            string group; ProgramRole[] roles;
            if (index < 42) { group = "system.engineer"; roles = [ProgramRole.Engineer]; }
            else if (index < 104) { group = "software.engineer"; roles = [ProgramRole.Engineer]; }
            else if (index < 138) { group = "verification.engineer"; roles = [ProgramRole.TestEngineer]; }
            else if (index < 160) { group = index % 2 == 0 ? "systems.lead" : "software.lead"; roles = [ProgramRole.Reviewer, ProgramRole.Approver]; }
            else if (index < 174) { group = "engineering.manager"; roles = [ProgramRole.ProgramManager, ProgramRole.Approver]; }
            else { group = "configuration.specialist"; roles = [ProgramRole.ConfigurationManager]; }
            var sequence = index + 1; var user = $"{group}.{sequence:D3}";
            yield return (user, name, $"{user}@aerolink.local", roles);
        }
    }
}
