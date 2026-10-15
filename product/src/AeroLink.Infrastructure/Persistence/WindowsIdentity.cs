using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Somebody Windows has authenticated, as the directory describes them today (#1615).
/// </summary>
public sealed record WindowsDirectoryIdentity(string Sid, string Principal, string DisplayName, string Email);

/// <summary>What the directory says about one account, looked up by its SID.</summary>
public sealed record WindowsDirectoryEntry(string DisplayName, string Email);

/// <summary>
/// The directory lookup behind Windows sign-in. An interface so tests can stand in for a domain controller;
/// production uses <see cref="ActiveDirectoryWindowsDirectory"/>.
/// </summary>
public interface IWindowsDirectory
{
    /// <summary>
    /// The directory entry for <paramref name="sid"/> in <paramref name="domain"/>, or null when the directory
    /// has no such account. A directory that cannot be reached throws; nothing is cached either way.
    /// </summary>
    Task<WindowsDirectoryEntry?> FindBySidAsync(string domain, string sid, CancellationToken ct);
}

/// <summary>
/// Active Directory through <c>System.DirectoryServices.AccountManagement</c>, bound as the service account
/// (the installation's virtual service account acts as the computer account on the network). Every call asks the directory afresh: a failed
/// lookup is never remembered, so a domain controller that was briefly unreachable does not refuse people later.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryWindowsDirectory : IWindowsDirectory
{
    public Task<WindowsDirectoryEntry?> FindBySidAsync(string domain, string sid, CancellationToken ct) => Task.Run(() =>
    {
        using var context = new PrincipalContext(ContextType.Domain, domain);
        using var principal = UserPrincipal.FindByIdentity(context, IdentityType.Sid, sid);
        if (principal is null) return null;
        var displayName = string.IsNullOrWhiteSpace(principal.DisplayName) ? principal.SamAccountName : principal.DisplayName;
        return new WindowsDirectoryEntry(displayName ?? "", principal.EmailAddress ?? "");
    }, ct);
}

/// <summary>
/// Which Windows accounts may sign in at all, decided from the SID alone (#1615).
///
/// Only a domain user account qualifies: a SID of the form <c>S-1-5-21-a-b-c-RID</c> whose domain part
/// (<c>S-1-5-21-a-b-c</c>) is on the configured allow-list and whose relative identifier is at least 1000.
/// Every other shape is a well-known or built-in identity (SYSTEM, NETWORK SERVICE, BUILTIN groups), and a RID
/// below 1000 is a built-in account of the domain itself (Administrator 500, Guest 501, krbtgt 502). A local
/// machine account has the same shape as a domain one, but its machine SID is not on the allow-list.
/// </summary>
public static class WindowsAccountPolicy
{
    public const string ForeignDomain = "foreign_domain";
    public const string BuiltInAccount = "builtin_account";
    public const string MachineAccount = "machine_account";

    /// <summary>The refusal code for this Windows account, or null when it may sign in.</summary>
    public static string? Refusal(string sid, string principal, IReadOnlyCollection<string> allowedDomainSids)
    {
        var domain = DomainSid(sid, out var rid);
        if (domain is null || rid < 1000) return BuiltInAccount;
        // Computer accounts and (group) managed service accounts have ordinary domain SIDs; their names end in '$'.
        if ((principal ?? "").TrimEnd().EndsWith('$')) return MachineAccount;
        return allowedDomainSids.Any(x => string.Equals(x.Trim(), domain, StringComparison.OrdinalIgnoreCase)) ? null : ForeignDomain;
    }

    /// <summary>The domain part of a domain account SID, or null when the SID is not one.</summary>
    public static string? DomainSid(string sid, out long rid)
    {
        rid = -1;
        var parts = (sid ?? "").Trim().ToUpperInvariant().Split('-');
        // S, 1, 5, 21, three domain sub-authorities, RID.
        if (parts.Length != 8 || parts[0] != "S" || parts[1] != "1" || parts[2] != "5" || parts[3] != "21") return null;
        if (!parts.Skip(4).All(x => x.Length > 0 && x.All(char.IsAsciiDigit) && uint.TryParse(x, out _))) return null;
        rid = long.Parse(parts[7]);
        return string.Join('-', parts.Take(7));
    }
}

/// <summary>
/// The access a Windows-created account starts with: Engineer in every Program, including Programs created
/// later (#1615). Exactly two moments grant it — the account's creation and a Program's creation — so an
/// administrator who narrows somebody's access is never overruled on the next sign-in or restart.
/// </summary>
public static class WindowsDefaultAccess
{
    public const string Actor = "system.windows-provisioning";
    public const ProgramRole DefaultRole = ProgramRole.Engineer;

    public static async Task GrantForNewAccountAsync(AeroLinkDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var programs = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        foreach (var programId in programs)
            db.ProgramMemberships.Add(new ProgramMembership(userId, programId, DefaultRole, Actor, now));
    }

    /// <summary>
    /// Grants the default role in a Program being created to every active Windows-created account that still
    /// answers to a SID, and audits the grant as one event.
    /// </summary>
    public static async Task GrantForNewProgramAsync(AeroLinkDbContext db, Guid programId, DateTimeOffset now, CancellationToken ct)
    {
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => x.Origin == AccountOrigin.Windows && x.State == AccountState.Active && x.WindowsSid != null)
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var userId in accounts)
            db.ProgramMemberships.Add(new ProgramMembership(userId, programId, DefaultRole, Actor, now));
        if (accounts.Count > 0)
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("RoleGranted", Actor, $"Program:{programId}", "Success",
                $"Granted {DefaultRole} for program {programId} to {accounts.Count} Windows account(s) on the Program's creation.", "local", now));
    }
}
