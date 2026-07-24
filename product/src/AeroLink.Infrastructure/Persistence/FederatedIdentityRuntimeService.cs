using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ExternalIdentityAccountBindingView(Guid Id, Guid ProviderId, string ExternalSubject, Guid UserId, string UserName, bool Enabled, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? LastAuthenticatedAt);
public sealed record ValidatedExternalPrincipal(Guid ProviderId, string Issuer, string Subject, IReadOnlyList<string> Groups);
public sealed record FederatedLoginResult(AuthenticatedUser User, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Consumes only a principal that has already passed protocol signature, issuer, audience,
/// lifetime, nonce and authorization-code/PKCE validation. It never parses or trusts raw tokens.
/// </summary>
public sealed class FederatedIdentityRuntimeService(AeroLinkDbContext db, ExternalIdentityAdministrationService externalIdentity)
{
    public async Task<ExternalIdentityAccountBindingView> BindAsync(Guid providerId, string externalSubject, Guid userId, string actor, string ip, DateTimeOffset now, CancellationToken ct)
    {
        var subject = NormalizeSubject(externalSubject);
        var provider = (await externalIdentity.ListProvidersAsync(ct)).SingleOrDefault(x => x.Id == providerId)
            ?? throw new KeyNotFoundException("Identity provider was not found.");
        if (!provider.Enabled || provider.Protocol != ExternalIdentityProtocol.OpenIdConnect)
            throw new InvalidOperationException("Only an enabled OpenID Connect provider can receive account bindings.");

        var user = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new KeyNotFoundException("User account was not found.");

        var id = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await ExecuteAsync("INSERT INTO external_identity_account_bindings (id,provider_id,external_subject,user_id,created_by,created_at,last_authenticated_at,disabled_at) VALUES (@id,@providerId,@subject,@userId,@actor,@now,NULL,NULL)", ct,
                ("id", id), ("providerId", providerId), ("subject", subject), ("userId", userId), ("actor", actor), ("now", now));
            db.SecurityAuditEvents.Add(new("ExternalIdentityAccountBound", actor, user.UserName, "Success", $"Bound provider {provider.Key} subject to local account.", ip, now));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(id, providerId, subject, userId, user.UserName, true, actor, now, null);
        }
        catch (DbException ex)
        {
            await transaction.RollbackAsync(ct);
            db.SecurityAuditEvents.Add(new("ExternalIdentityAccountBindRejected", actor, user.UserName, "Conflict", "The provider subject or local account already has a binding.", ip, now));
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("The provider subject or local account already has a binding.", ex);
        }
    }

    public async Task<IReadOnlyList<ExternalIdentityAccountBindingView>> ListBindingsAsync(Guid? providerId, CancellationToken ct)
    {
        var sql = "SELECT b.id,b.provider_id,b.external_subject,b.user_id,u.\"UserName\",b.created_by,b.created_at,b.last_authenticated_at,b.disabled_at FROM external_identity_account_bindings b JOIN user_accounts u ON u.\"Id\"=b.user_id";
        if (providerId is not null) sql += " WHERE b.provider_id=@providerId";
        sql += " ORDER BY b.provider_id,b.external_subject";
        var rows = new List<ExternalIdentityAccountBindingView>();
        await using var command = CreateCommand(sql);
        if (providerId is not null) Add(command, "providerId", providerId.Value);
        await OpenAsync(command.Connection!, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.IsDBNull(8), reader.GetString(5), ReadDate(reader, 6), reader.IsDBNull(7) ? null : ReadDate(reader, 7)));
        return rows;
    }

    public async Task<bool> SetBindingEnabledAsync(Guid id, bool enabled, string actor, string ip, DateTimeOffset now, CancellationToken ct)
    {
        var affected = await ExecuteAsync("UPDATE external_identity_account_bindings SET disabled_at=@disabledAt WHERE id=@id", ct,
            ("disabledAt", enabled ? DBNull.Value : now), ("id", id));
        if (affected == 0) return false;
        db.SecurityAuditEvents.Add(new(enabled ? "ExternalIdentityAccountBindingEnabled" : "ExternalIdentityAccountBindingDisabled", actor, id.ToString(), "Success", enabled ? "Enabled external account binding." : "Disabled external account binding.", ip, now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<FederatedLoginResult?> CreateSessionAsync(ValidatedExternalPrincipal principal, string ip, string userAgent, DateTimeOffset now, CancellationToken ct)
    {
        if (principal.ProviderId == Guid.Empty || string.IsNullOrWhiteSpace(principal.Issuer) || string.IsNullOrWhiteSpace(principal.Subject) || principal.Groups is null)
            return await DenyAsync("unknown", "Malformed validated principal.", ip, now, ct);

        var provider = (await externalIdentity.ListProvidersAsync(ct)).SingleOrDefault(x => x.Id == principal.ProviderId);
        if (provider is null || !provider.Enabled || provider.Protocol != ExternalIdentityProtocol.OpenIdConnect || !string.Equals(provider.Issuer.TrimEnd('/'), principal.Issuer.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return await DenyAsync(principal.Subject, "Provider is disabled, missing, not OIDC, or issuer did not match.", ip, now, ct);

        var subject = NormalizeSubject(principal.Subject);
        Guid? userId = null;
        await using (var command = CreateCommand("SELECT user_id FROM external_identity_account_bindings WHERE provider_id=@providerId AND external_subject=@subject AND disabled_at IS NULL"))
        {
            Add(command, "providerId", principal.ProviderId); Add(command, "subject", subject);
            await OpenAsync(command.Connection!, ct);
            var scalar = await command.ExecuteScalarAsync(ct);
            if (scalar is Guid guid) userId = guid;
            else if (scalar is string text && Guid.TryParse(text, out var parsed)) userId = parsed;
        }
        if (userId is null) return await DenyAsync(subject, "No enabled local account binding exists.", ip, now, ct);

        var user = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == userId.Value, ct);
        if (user is null || user.State != AccountState.Active) return await DenyAsync(subject, "The bound local account is unavailable.", ip, now, ct);

        var groups = principal.Groups.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0) return await DenyAsync(subject, "The validated principal contained no usable groups.", ip, now, ct);

        var programIds = await db.Programs.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var accesses = new List<UserProgramAccess>();
        foreach (var programId in programIds)
        {
            var roles = await externalIdentity.ResolveRolesAsync(principal.ProviderId, principal.Issuer, groups, programId, ct);
            if (roles.Count > 0) accesses.Add(new(programId, roles.Select(x => x.ToString()).Distinct().Order().ToList()));
        }
        if (accesses.Count == 0) return await DenyAsync(subject, "No enabled Program role mapping granted authority.", ip, now, ct);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = now.AddHours(12);
        db.UserSessions.Add(new(user.Id, IdentityService.TokenDigest(token)!, ip, userAgent, now, expiresAt));
        user.LoginSucceeded(now);
        db.SecurityAuditEvents.Add(new("FederatedLogin", user.UserName, "session", "Success", $"OIDC session created through provider {provider.Key}.", ip, now));
        await ExecuteAsync("UPDATE external_identity_account_bindings SET last_authenticated_at=@now WHERE provider_id=@providerId AND external_subject=@subject", ct, ("now", now), ("providerId", principal.ProviderId), ("subject", subject));
        await db.SaveChangesAsync(ct);
        return new(new(user.Id, user.UserName, user.DisplayName, user.Email, user.UserName == IdentityService.SystemAdministratorUserName, accesses, false), token, expiresAt);
    }

    private async Task<FederatedLoginResult?> DenyAsync(string subject, string reason, string ip, DateTimeOffset now, CancellationToken ct)
    {
        db.SecurityAuditEvents.Add(new("FederatedLogin", subject, "session", "Denied", reason, ip, now));
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string NormalizeSubject(string value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("externalSubject is required.", nameof(value));
        if (normalized.Length > 500) throw new ArgumentException("externalSubject cannot exceed 500 characters.", nameof(value));
        return normalized;
    }

    private DbCommand CreateCommand(string sql) { var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql; command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); return command; }
    private static void Add(DbCommand command, string name, object value) { var parameter = command.CreateParameter(); parameter.ParameterName = "@" + name; parameter.Value = value; command.Parameters.Add(parameter); }
    private static async Task OpenAsync(DbConnection connection, CancellationToken ct) { if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct); }
    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] values) { await using var command = CreateCommand(sql); foreach (var value in values) Add(command, value.Name, value.Value); await OpenAsync(command.Connection!, ct); return await command.ExecuteNonQueryAsync(ct); }
    private static DateTimeOffset ReadDate(DbDataReader reader, int ordinal) { var value = reader.GetValue(ordinal); return value is DateTimeOffset dto ? dto : new DateTimeOffset(Convert.ToDateTime(value)); }
}
