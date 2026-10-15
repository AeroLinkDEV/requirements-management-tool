using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record AuthenticatedUser(Guid Id, string UserName, string DisplayName, string Email, bool IsAdministrator, IReadOnlyList<UserProgramAccess> Programs, bool MustChangePassword = false);
public sealed record UserProgramAccess(Guid ProgramId, IReadOnlyList<string> Roles);
public sealed record LoginResult(AuthenticatedUser User, string Token, DateTimeOffset ExpiresAt);
/// <summary>A session, or the refusal code the sign-in page explains (#1615).</summary>
public sealed record WindowsSignInOutcome(LoginResult? Session, string? RefusalCode);

public sealed class IdentityService(AeroLinkDbContext db, IDataProtectionProvider? dataProtection = null)
{
    private const string ProtectedMfaPrefix = "dp:v1:";
    private const int NewPasswordIterations = 600_000;
    private readonly IDataProtector _mfaProtector = (dataProtection ?? new EphemeralDataProtectionProvider()).CreateProtector("AeroLink.Identity.MfaSecret.v1");
    public const string SystemAdministratorUserName = "admin";
    public static string HashPassword(string password)
    {
        if (password.Length < 10) throw new ArgumentException("Password must contain at least 10 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, NewPasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${NewPasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
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
        if (user is null) { db.SecurityAuditEvents.Add(new SecurityAuditEvent("Login", normalized, "session", "Denied", "Unknown account.", ip, now).WithAuthentication(PasswordMethod)); await db.SaveChangesAsync(ct); return null; }
        if (!await VerifyPasswordAndSecondFactorAsync(user, password, mfaCode, "Login", ip, now, ct)) return null;
        return await IssueSessionAsync(user, ip, userAgent, now, PasswordMethod, null, ct);
    }

    public const string PasswordMethod = "password";
    public const string WindowsMethod = "windows";

    /// <summary>
    /// The password and second-factor proof shared by password sign-in and the Windows link (#1615), with the
    /// same lockout counting. An account created by Windows sign-in has no password: it is refused without
    /// counting a failure, so guessing at its name cannot lock somebody out of their Windows sign-in.
    /// </summary>
    private async Task<bool> VerifyPasswordAndSecondFactorAsync(UserAccount user, string password, string? mfaCode, string eventType, string ip, DateTimeOffset now, CancellationToken ct)
    {
        if (!user.HasPassword) { db.SecurityAuditEvents.Add(new SecurityAuditEvent(eventType, user.UserName, "session", "Denied", "This account signs in with Windows and has no AeroLink password.", ip, now).WithAuthentication(PasswordMethod)); await db.SaveChangesAsync(ct); return false; }
        if (user.State != AccountState.Active || !VerifyPassword(password, user.PasswordHash)) { user.LoginFailed(); db.SecurityAuditEvents.Add(new SecurityAuditEvent(eventType, user.UserName, "session", "Denied", user.State == AccountState.Active ? "Invalid credentials." : $"Account is {user.State}.", ip, now).WithAuthentication(PasswordMethod)); await db.SaveChangesAsync(ct); return false; }
        var enrollment=await db.UserMfaEnrollments.SingleOrDefaultAsync(x=>x.UserId==user.Id&&x.Confirmed,ct);if(enrollment is not null){var valid=VerifyTotp(RevealMfaSecret(enrollment.Secret),mfaCode??"",now);if(!valid&&!string.IsNullOrWhiteSpace(mfaCode)){var recovery=await db.MfaRecoveryCodes.SingleOrDefaultAsync(x=>x.UserId==user.Id&&x.CodeHash==RecoveryHash(mfaCode)&&x.UsedAt==null,ct);if(recovery is not null){recovery.Use(now);valid=true;}}if(!valid){db.SecurityAuditEvents.Add(new SecurityAuditEvent("MfaChallenge",user.UserName,"session","Denied","A valid authenticator or unused recovery code is required.",ip,now).WithAuthentication(PasswordMethod));await db.SaveChangesAsync(ct);return false;}}
        return true;
    }

    /// <summary>Creates the session for an account that has just proved who it is, and records how it did.</summary>
    private async Task<LoginResult> IssueSessionAsync(UserAccount user, string ip, string userAgent, DateTimeOffset now, string method, string? windowsPrincipal, CancellationToken ct)
    {
        user.LoginSucceeded(now); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); var expires = now.AddHours(12);
        db.UserSessions.Add(new(user.Id, TokenHash(token), ip, userAgent, now, expires));
        db.SecurityAuditEvents.Add(new SecurityAuditEvent("Login", user.UserName, "session", "Success", "Authenticated session created.", ip, now).WithAuthentication(method, windowsPrincipal));
        await db.SaveChangesAsync(ct);
        return new(await MapAsync(user, now, ct), token, expires);
    }

    /// <summary>
    /// Signs in somebody Windows has already authenticated and the endpoint has already checked against the
    /// domain allow-list and the directory (#1615).
    ///
    /// The SID is the identity. An account that answers to it is reused whatever the person is called today;
    /// otherwise one is created, with Engineer in every Program. A Windows-created account takes its display
    /// name and email from the directory on every sign-in. A local account linked to Windows (the
    /// administrator) keeps its own AeroLink profile, and is signed in without its second factor: the owner
    /// decided that the Windows sign-in is enough there, and that MFA guards only the password break-glass.
    /// </summary>
    public async Task<WindowsSignInOutcome> SignInWindowsAsync(WindowsDirectoryIdentity identity, string ip, string userAgent, DateTimeOffset now, CancellationToken ct)
    {
        var sid = identity.Sid.Trim().ToUpperInvariant();
        var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.WindowsSid == sid, ct);
        if (user is null)
        {
            // First-install bootstrap is open only while no account exists. Creating one here first would close it
            // with nobody able to administer the installation, so provisioning waits for the administrator.
            if (!await db.UserAccounts.AnyAsync(x => x.UserName == SystemAdministratorUserName, ct))
                return await RefuseWindowsAsync(identity, "setup_incomplete", "No administrator exists yet; complete first-install setup before Windows sign-in.", ip, now, ct);
            var userName = identity.Principal.Trim().ToLowerInvariant();
            if (await db.UserAccounts.AnyAsync(x => x.UserName == userName, ct))
                return await RefuseWindowsAsync(identity, "account_name_in_use", $"An account named {userName} already exists without this Windows SID.", ip, now, ct);
            user = UserAccount.ProvisionFromWindows(sid, identity.Principal, identity.DisplayName, identity.Email, now);
            db.UserAccounts.Add(user);
            await WindowsDefaultAccess.GrantForNewAccountAsync(db, user.Id, now, ct);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("AccountProvisioned", WindowsDefaultAccess.Actor, user.UserName, "Success",
                $"Created on first Windows sign-in for {identity.Principal} ({sid}) with {WindowsDefaultAccess.DefaultRole} in every Program.", ip, now)
                .WithAuthentication(WindowsMethod, identity.Principal));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // A second first sign-in for the same SID won the unique index. Use the account it created.
                db.ChangeTracker.Clear();
                user = await db.UserAccounts.SingleOrDefaultAsync(x => x.WindowsSid == sid, ct);
                if (user is null) return await RefuseWindowsAsync(identity, "provisioning_failed", "The account could not be created.", ip, now, ct);
            }
        }
        if (user.State != AccountState.Active)
            return await RefuseWindowsAsync(identity, "account_" + user.State.ToString().ToLowerInvariant(), $"Account is {user.State}.", ip, now, ct);
        user.RecordWindowsPrincipal(identity.Principal);
        if (user.Origin == AccountOrigin.Windows) user.RefreshDirectoryProfile(identity.DisplayName, identity.Email);
        return new(await IssueSessionAsync(user, ip, userAgent, now, WindowsMethod, identity.Principal, ct), null);
    }

    /// <summary>
    /// Records that a Windows sign-in was refused before any account was involved: the endpoint's domain,
    /// account-type and directory checks.
    /// </summary>
    public async Task<WindowsSignInOutcome> RefuseWindowsAsync(WindowsDirectoryIdentity identity, string code, string detail, string ip, DateTimeOffset now, CancellationToken ct)
    {
        db.SecurityAuditEvents.Add(new SecurityAuditEvent("Login", identity.Principal.Trim().ToLowerInvariant(), "session", "Denied", $"{detail} ({code}, {identity.Sid})", ip, now)
            .WithAuthentication(WindowsMethod, identity.Principal));
        await db.SaveChangesAsync(ct);
        return new(null, code);
    }

    /// <summary>
    /// Links a Windows logon to an existing AeroLink account whose password (and second factor, when enrolled)
    /// the person has just entered — how the owner attaches his Windows logon to <c>admin</c> (#1615). Failures
    /// count toward that account's lockout exactly as a password sign-in does. A SID already linked to another
    /// account is refused, unless that account is a Windows-created one an administrator has disabled: that is
    /// how somebody who signed in with Windows before linking frees their SID.
    /// </summary>
    public async Task<WindowsSignInOutcome> LinkWindowsAsync(WindowsDirectoryIdentity identity, string userName, string password, string? mfaCode, string ip, string userAgent, DateTimeOffset now, CancellationToken ct)
    {
        var sid = identity.Sid.Trim().ToUpperInvariant();
        var normalized = userName.Trim().ToLowerInvariant();
        var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.UserName == normalized, ct);
        if (user is null)
        {
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("WindowsLink", normalized, "session", "Denied", "Unknown account.", ip, now).WithAuthentication(PasswordMethod, identity.Principal));
            await db.SaveChangesAsync(ct);
            return new(null, "credentials");
        }
        if (!await VerifyPasswordAndSecondFactorAsync(user, password, mfaCode, "WindowsLink", ip, now, ct)) return new(null, "credentials");
        if (user.WindowsSid is not null && user.WindowsSid != sid)
            return await RefuseWindowsAsync(identity, "account_already_linked", $"{user.UserName} is already linked to another Windows logon.", ip, now, ct);
        if (user.WindowsSid is null)
        {
            var holder = await db.UserAccounts.SingleOrDefaultAsync(x => x.WindowsSid == sid, ct);
            if (holder is not null && (holder.Origin != AccountOrigin.Windows || holder.State != AccountState.Disabled))
                return await RefuseWindowsAsync(identity, "windows_already_linked", $"This Windows logon already signs in as {holder.UserName}.", ip, now, ct);
            // Two saves, because the SID's unique index must see it leave the disabled account before it arrives.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (holder is not null)
            {
                holder.UnlinkWindows();
                await db.SaveChangesAsync(ct);
            }
            user.LinkWindows(sid, identity.Principal);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("WindowsLinked", user.UserName, user.UserName, "Success",
                $"Linked to Windows logon {identity.Principal} ({sid})" + (holder is null ? "." : $", released from disabled account {holder.UserName}."), ip, now)
                .WithAuthentication(PasswordMethod, identity.Principal));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return new(await IssueSessionAsync(user, ip, userAgent, now, WindowsMethod, identity.Principal, ct), null);
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
    /// <summary>
    /// The roles a membership row may answer a demand with.
    ///
    /// <c>ProgramRoleAuthority.Satisfying</c> still folds the retired position roles into Reviewer, Approver
    /// and Engineer so a stored workflow stage naming one keeps resolving. A membership carrying one must not
    /// answer for it, though: that is the conflation #816 removes, and honouring it would let a roster grant
    /// hand out a lead's review authority with no assignment anywhere. Positions answer through
    /// <see cref="HoldsLeadershipDemandAsync"/> instead. The four base eligibility roles are still jobs: they
    /// answer their own base-role questions, and Project Engineer or Engineering Manager still answers an
    /// ordinary Engineer question.
    /// </summary>
    private static IReadOnlyList<ProgramRole> MembershipAnswerableRoles(ProgramRole demanded)
    {
        return [.. ProgramRoleAuthority.Satisfying(demanded)
            .Where(x => !SingularProgramRoles.IsSingular(x))];
    }

    public async Task<bool> HasRoleAsync(AuthenticatedUser user, Guid programId, ProgramRole role, DateTimeOffset now, CancellationToken ct)
    {
        if (user.IsAdministrator) return true;
        // A more precise job title must never remove capability: somebody recorded as a System Engineer is
        // an engineer, and every place that asks for Engineer has to accept them. Position roles are the
        // exception since #816 — a SystemEngineeringLead membership is a legacy row or an eligibility, not
        // the post, so it is the leadership pass below that answers for it.
        var accepted = MembershipAnswerableRoles(role).Select(x => x.ToString()).ToList();
        if (user.Programs.Any(x => x.ProgramId == programId && x.Roles.Any(accepted.Contains))) return true;
        if (await HoldsLeadershipDemandAsync(user.Id, programId, role, ct)) return true;
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
        // Position roles are excluded: since #816 a backup of a position is a ProjectLeadershipBackup, and
        // honouring the legacy row here as well is what left a removed backup still signing. Reading it in
        // one gate and not the others also gave three different answers to the same question.
        var accepted = ProgramRoleAuthority.Satisfying(role);
        if (!backedRoles.Any(x => !SingularProgramRoles.IsPositionGoverned(x) && accepted.Contains(x))) return false;
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
        var accepted = MembershipAnswerableRoles(role);
        if (await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == userId && x.ProgramId == programId && x.EndedAt == null && accepted.Contains(x.Role), ct)) return true;
        if (await HoldsLeadershipDemandAsync(userId, programId, role, ct)) return true;
        if (await IsStandingBackupAsync(userId, programId, role, ct)) return true;
        var delegations = await db.RoleDelegations.AsNoTracking().Where(x => x.ProgramId == programId && x.DelegateUserId == userId && x.Role == role && x.RevokedAt == null).ToListAsync(ct);
        return delegations.Any(x => x.StartsAt <= now && x.EndsAt > now);
    }

    /// <summary>
    /// Whether an active Project Leadership assignment or standing backup makes this person answer the
    /// demanded role — the #816 central rule, consulted by both HasRoleAsync paths.
    ///
    /// Holding the Project Engineering Lead or a discipline-lead membership used to be what carried these
    /// authorities; under the leadership model the elevation is a separate persisted fact, so the check
    /// runs here instead of in the membership set. Fail-closed on every axis: the assignment or backup
    /// designation must be active, the account active, the person a current member of the program, and
    /// they must still hold the base role the position requires. Any of those lapses and the authority is
    /// gone, however recent the designation is.
    /// </summary>
    private async Task<bool> HoldsLeadershipDemandAsync(Guid userId, Guid programId, ProgramRole demanded, CancellationToken ct)
    {
        if (await db.UserAccounts.AsNoTracking().AnyAsync(x => x.Id == userId && x.State != AccountState.Active, ct)) return false;
        // Delegated to the one resolver, which validates each position that answers the demand against that
        // position's own eligibility. The version that lived here collected every position the person held,
        // asked whether any of them answered the demand, then tested the *union* of their required base
        // roles — so a System Engineering Lead who lost `SystemEngineer` kept the lead's Reviewer authority
        // on the strength of an unrelated Configuration Manager position they still held.
        var decision = await new ProjectAuthorityResolver(db)
            .ResolveAnyLeadershipSatisfyingAsync(userId, programId, demanded, ct);
        return decision.Granted;
    }

    /// <summary>
    /// The Project Leadership positions this person currently answers, whether as primary or as standing
    /// backup — the projection Personnel, workflow candidate resolution and Team Work should read instead
    /// of scanning singular memberships.
    /// </summary>
    public async Task<IReadOnlyList<(ProjectLeadershipPosition Position, bool AsPrimary)>> HeldLeadershipPositionsAsync(Guid userId, Guid programId, CancellationToken ct)
    {
        var primaries = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.HolderUserId == userId && x.EndedAt == null)
            .Select(x => x.Position).ToListAsync(ct);
        var backups = await db.ProjectLeadershipBackups.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.BackupUserId == userId && x.RemovedAt == null)
            .Select(x => x.Position).ToListAsync(ct);
        return primaries.Select(x => (x, true)).Concat(backups.Select(x => (x, false))).ToList();
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

    /// <summary>
    /// Every username the identity directory can seed (#913): the curated cast, the generated
    /// population, and the administrator. Consumer contracts that must cover the whole seeded cast —
    /// the FMS showcase portrait manifest among them — assert against this list rather than a
    /// hand-copied roster, so a newly seeded account cannot silently miss them.
    /// </summary>
    public static IReadOnlyCollection<string> SeedDirectoryUserNames()
    {
        var names = People.Select(x => x.User).ToList();
        names.AddRange(GeneratedPeople().Select(x => x.User));
        return names;
    }

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
        // Demo-account seeding is restricted to the explicitly named showcase Programs. User-created
        // Projects must retain only the memberships an administrator deliberately grants; treating every
        // Program row as a fixture silently copied the FMS directory into new owner Projects on restart.
        var demoProgramCodes = new[] { FmsShowcaseSeeder.ProgramCode, ImportPracticeSeeder.ProgramCode };
        var programs = await db.Programs.AsNoTracking().Where(x => demoProgramCodes.Contains(x.Code)).Select(x => x.Id).ToListAsync(ct);
        // FMS closure authority is controlled by FmsShowcaseSeeder's fresh-program path or an explicit
        // operator upgrade. The directory pass may grant the other seeded roles, but must not silently create
        // a missing SQA authority on an existing FMS database before backup/target confirmation.
        var fmsProgramId = await db.Programs.AsNoTracking()
            .Where(x => x.Code == FmsShowcaseSeeder.ProgramCode)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var demoPasswordHash = IdentityService.HashPassword(DemoPassword);
        var directory = People.Concat(GeneratedPeople()).ToList();
        var userNames = directory.Select(x => x.User).ToList();
        var users = (await db.UserAccounts.Where(x => userNames.Contains(x.UserName)).ToListAsync(ct))
            .ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
        // FMSLIVE is an operator-owned controlled showcase once its Program row exists. Its memberships
        // and Project Leadership assignments are created by the fresh FMS transaction or the explicit,
        // backup-confirmed showcase upgrade; the ordinary directory pass must not silently repair them.
        var identitySeedPrograms = programs.Where(x => x != fmsProgramId).ToList();
        var membershipKeys = (await db.ProgramMemberships.AsNoTracking()
                .Where(x => identitySeedPrograms.Contains(x.ProgramId))
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
            // #816 Slice 3: curated seed accounts have their directory profile reconciled ONLY when the
            // account has never been edited by an administrator. Once an admin changes the current display
            // name or email through the identity-edit endpoint, that edit is authoritative for the current
            // identity and must survive restarts and re-seeding. The IdentityUpdated audit event records
            // the change; this reconciliation must not silently undo it.
            else if (curatedUsers.Contains(person.User)
                && !await db.SecurityAuditEvents.AsNoTracking().AnyAsync(
                    x => x.EventType == "IdentityUpdated" && x.Target == user.UserName, ct)
                && (user.DisplayName != person.Name || user.Email != person.Email))
                user.RefreshDirectoryProfile(person.Name, person.Email);
            foreach (var program in identitySeedPrograms) foreach (var role in person.Roles)
                if (membershipKeys.Add((user.Id, program, role)))
                    db.ProgramMemberships.Add(new(user.Id, program, role, "system.bootstrap", now));
        }
        await db.SaveChangesAsync(ct);
        await EnsureSeededLeadershipAsync(identitySeedPrograms, now, ct);
    }

    /// <summary>
    /// Elevates the seeded leads into the Project Leadership positions their roles used to imply.
    ///
    /// Since #816 a <c>SoftwareEngineeringLead</c> membership is a retired position role that grants nothing
    /// on its own — the authority travels with the assignment. Real deployments get their assignments from
    /// the migration, which only runs on PostgreSQL; a fresh SQLite database seeds its state instead and
    /// would otherwise come up with eight vacant positions and a roster of leads who cannot sign anything.
    ///
    /// Only the roles the seeded people actually hold are elevated, and only into a position that has never
    /// been filled. A position whose assignment was ended was vacated by somebody deliberately, and a seed
    /// run must not undo that — "no active assignment" alone would refill it on the next startup.
    /// </summary>
    private async Task EnsureSeededLeadershipAsync(IReadOnlyList<Guid> programs, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var program in programs)
        {
            foreach (var position in ProjectLeadership.All)
            {
                if (await db.ProjectLeadershipAssignments.AnyAsync(
                        x => x.ProgramId == program && x.Position == position, ct))
                    continue;

                // Prefer whoever the seed named as the legacy position holder; otherwise the earliest person
                // holding the position's base role. Deterministic either way, so a reseed cannot silently
                // move a position between two equally eligible people.
                var requiredBaseRole = ProjectLeadership.RequiredBaseRole(position);
                var legacyRole = LegacySeedRole(position);
                var holder = await FirstHolderAsync(program, legacyRole, ct)
                    ?? await FirstHolderAsync(program, requiredBaseRole, ct);
                if (holder is null) continue;

                // The same named derivation the migration uses: the lead membership is the evidence of the
                // discipline, so the base role the position requires is granted here rather than assumed.
                if (!await db.ProgramMemberships.AnyAsync(x => x.UserId == holder && x.ProgramId == program
                        && x.Role == requiredBaseRole && x.EndedAt == null, ct))
                    db.ProgramMemberships.Add(new(holder.Value, program, requiredBaseRole, "system.bootstrap", now));

                db.ProjectLeadershipAssignments.Add(
                    new ProjectLeadershipAssignment(program, position, holder.Value, "system.bootstrap", now));
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The person the seed makes the holder of a position, chosen deterministically.
    ///
    /// The seed grants every role in one batch with a single timestamp, and several seeded people hold
    /// <c>ProgramManager</c>, so ordering by <c>GrantedAt</c> alone leaves a tie. Breaking it on the account's
    /// user name rather than its GUID is what makes a reseed land on the same person every time; a GUID
    /// tie-break is a coin toss dressed up as an ordering.
    /// </summary>
    private async Task<Guid?> FirstHolderAsync(Guid programId, ProgramRole? role, CancellationToken ct)
    {
        if (role is null) return null;
        var value = role.Value;
        // SQLite cannot translate DateTimeOffset ordering. Keep the database-side filter and projection,
        // then apply the deterministic seed ordering to the small candidate set in memory. PostgreSQL and
        // SQLite now choose the same holder instead of the browser fixture failing before any journey runs.
        var candidates = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null && x.Role == value)
            .Join(db.UserAccounts.AsNoTracking(), m => m.UserId, u => u.Id,
                (m, u) => new { m.GrantedAt, u.UserName, u.Id, u.State })
            .Where(x => x.State == AccountState.Active)
            .ToListAsync(ct);
        return candidates.OrderBy(x => x.GrantedAt).ThenBy(x => x.UserName, StringComparer.Ordinal)
            .Select(x => (Guid?)x.Id).FirstOrDefault();
    }

    /// <summary>The retired position role a seeded person may still carry for each position, if any.</summary>
    private static ProgramRole? LegacySeedRole(ProjectLeadershipPosition position) => position switch
    {
        ProjectLeadershipPosition.SystemEngineeringLead => ProgramRole.SystemEngineeringLead,
        ProjectLeadershipPosition.SoftwareEngineeringLead => ProgramRole.SoftwareEngineeringLead,
        ProjectLeadershipPosition.SystemTestLead => ProgramRole.SystemTestLead,
        ProjectLeadershipPosition.SoftwareTestLead => ProgramRole.SoftwareTestLead,
        ProjectLeadershipPosition.ProjectEngineer => ProgramRole.ProjectEngineeringLead,
        _ => null,
    };

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
