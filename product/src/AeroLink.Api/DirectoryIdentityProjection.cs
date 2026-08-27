using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves current directory display names for account handles a response already carries.
///
/// AeroLink shows two different kinds of person identity, and they must not be resolved the same way:
///
/// <list type="bullet">
/// <item><description>
/// <b>Current identity</b> — who holds an assignment <i>now</i>. A Problem Report's responsible engineer is
/// this: the field is mutable, it answers "who is on this today", and it should follow the directory. If the
/// person is renamed, the correct answer changes with them. That is what this class is for.
/// </description></item>
/// <item><description>
/// <b>Historical identity</b> — who did something, once, in an immutable event. That name must be captured
/// when the event is written and read back verbatim afterwards, never resolved through here. See
/// <c>ProblemReportRevision.ActorDisplayName</c> for why: resolving a frozen audit entry against today's
/// directory lets a rename silently rewrite what the record says happened, with no controlled revision to
/// explain the change.
/// </description></item>
/// </list>
///
/// The handle is never replaced. Both are returned so a reader sees a person and an auditor can still
/// reconcile that person against the identity provider.
///
/// One set-wise query per response, never one per row — <c>#777</c> was raised about exactly that N+1 shape,
/// and a history with a hundred events must not become a hundred lookups.
///
/// This resolves only handles the caller already holds, which are handles from a controlled record the caller
/// has already been authorised to read. It is not a directory search and must not be used as one: it never
/// widens what a response exposes beyond names for actors that response already names, and it returns display
/// names only — no email, no contact details, no other directory metadata.
/// </summary>
public static class DirectoryIdentityProjection
{
    /// <summary>
    /// Current display names for the given account handles, keyed case-insensitively by handle.
    ///
    /// Handles with no matching account are simply absent: an account can be removed after it acted, and the
    /// honest answer then is the handle the caller already has rather than a fabricated name.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(
        AeroLinkDbContext db, IEnumerable<string?> userNames, CancellationToken ct)
    {
        var wanted = userNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Materialised, then keyed in memory: the provider comparison for the IN clause is ordinal, while the
        // dictionary a caller reads from must match handles the same way the rest of the product does.
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => wanted.Contains(x.UserName))
            .Select(x => new { x.UserName, x.DisplayName })
            .ToListAsync(ct);

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
            if (!string.IsNullOrWhiteSpace(account.DisplayName))
                resolved[account.UserName] = account.DisplayName;
        return resolved;
    }

    /// <summary>
    /// The current display name for one handle, or null when the account is gone or unnamed. Null means "show
    /// the handle", never "invent something".
    /// </summary>
    public static string? Current(this IReadOnlyDictionary<string, string> names, string? userName) =>
        !string.IsNullOrWhiteSpace(userName) && names.TryGetValue(userName.Trim(), out var display) ? display : null;
}
