using System.Security.Cryptography;

namespace AeroLink.Domain.Identity;

public enum AccountState { Active, Disabled, Locked }
public enum ProgramRole { Engineer, Reviewer, Approver, ConfigurationManager, TestEngineer, ProgramManager, Administrator }
public enum ExternalIdentityProtocol { OpenIdConnect, Saml2 }

public sealed class ExternalIdentityProvider
{
    private ExternalIdentityProvider() { }
    public ExternalIdentityProvider(string key, string displayName, ExternalIdentityProtocol protocol, string issuer, string subjectClaim, string groupClaim, string createdBy, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        Key = NormalizeKey(key);
        DisplayName = Required(displayName, nameof(displayName));
        Protocol = protocol;
        Issuer = Required(issuer, nameof(issuer));
        SubjectClaim = Required(subjectClaim, nameof(subjectClaim));
        GroupClaim = Required(groupClaim, nameof(groupClaim));
        CreatedBy = Required(createdBy, nameof(createdBy));
        CreatedAt = now;
        Enabled = true;
    }
    public Guid Id { get; private set; }
    public string Key { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public ExternalIdentityProtocol Protocol { get; private set; }
    public string Issuer { get; private set; } = "";
    public string SubjectClaim { get; private set; } = "";
    public string GroupClaim { get; private set; } = "";
    public bool Enabled { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }
    public void Disable(DateTimeOffset now) { Enabled = false; DisabledAt = now; }
    public void Enable() { Enabled = true; DisabledAt = null; }
    private static string NormalizeKey(string value) => Required(value, nameof(value)).ToLowerInvariant();
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value.Trim();
}

public sealed class ExternalGroupRoleMapping
{
    private ExternalGroupRoleMapping() { }
    public ExternalGroupRoleMapping(Guid providerId, string externalGroup, Guid programId, ProgramRole role, string createdBy, DateTimeOffset now)
    {
        if (providerId == Guid.Empty) throw new ArgumentException("providerId is required.", nameof(providerId));
        if (programId == Guid.Empty) throw new ArgumentException("programId is required.", nameof(programId));
        Id = Guid.NewGuid();
        ProviderId = providerId;
        ExternalGroup = NormalizeGroup(externalGroup);
        ProgramId = programId;
        Role = role;
        CreatedBy = Required(createdBy, nameof(createdBy));
        CreatedAt = now;
        Enabled = true;
    }
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public string ExternalGroup { get; private set; } = "";
    public Guid ProgramId { get; private set; }
    public ProgramRole Role { get; private set; }
    public bool Enabled { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }
    public bool Matches(Guid providerId, string externalGroup) => Enabled && ProviderId == providerId && ExternalGroup == NormalizeGroup(externalGroup);
    public void Disable(DateTimeOffset now) { Enabled = false; DisabledAt = now; }
    public void Enable() { Enabled = true; DisabledAt = null; }
    private static string NormalizeGroup(string value) => Required(value, nameof(value)).ToLowerInvariant();
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value.Trim();
}

public sealed class UserAccount
{
    private UserAccount() { }
    public UserAccount(string userName, string displayName, string email, string passwordHash, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); UserName = Normalize(userName); DisplayName = Required(displayName, nameof(displayName));
        Email = email.Trim(); PasswordHash = passwordHash; State = AccountState.Active; CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public AccountState State { get; private set; }
    public bool MustChangePassword { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }
    public void LoginSucceeded(DateTimeOffset now) { FailedLoginCount = 0; LastLoginAt = now; }
    public void LoginFailed() { FailedLoginCount++; if (FailedLoginCount >= 8) State = AccountState.Locked; }
    public void ChangePassword(string hash) { PasswordHash = hash; MustChangePassword = false; FailedLoginCount = 0; State = AccountState.Active; }
    public void RequirePasswordChange(string temporaryHash) { PasswordHash = temporaryHash; MustChangePassword = true; FailedLoginCount = 0; State = AccountState.Active; }
    public void RefreshDirectoryProfile(string displayName, string email) { DisplayName = Required(displayName, nameof(displayName)); Email = Required(email, nameof(email)); }
    public void Disable(DateTimeOffset now) { State = AccountState.Disabled; DisabledAt = now; }
    public void Enable() { State = AccountState.Active; DisabledAt = null; FailedLoginCount = 0; }
    private static string Normalize(string value) => Required(value, nameof(value)).ToLowerInvariant();
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value.Trim();
}

public sealed class ProgramMembership
{
    private ProgramMembership() { }
    public ProgramMembership(Guid userId, Guid programId, ProgramRole role, string grantedBy, DateTimeOffset now)
    { Id = Guid.NewGuid(); UserId = userId; ProgramId = programId; Role = role; GrantedBy = grantedBy; GrantedAt = now; }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ProgramId { get; private set; }
    public ProgramRole Role { get; private set; }
    public string GrantedBy { get; private set; } = "";
    public DateTimeOffset GrantedAt { get; private set; }
}

public sealed class UserSession
{
    private UserSession() { }
    public UserSession(Guid userId, string tokenHash, string ipAddress, string userAgent, DateTimeOffset now, DateTimeOffset expiresAt)
    { Id = Guid.NewGuid(); UserId = userId; TokenHash = tokenHash; IpAddress = ipAddress; UserAgent = userAgent; CreatedAt = now; LastSeenAt = now; ExpiresAt = expiresAt; }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public string IpAddress { get; private set; } = "";
    public string UserAgent { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public bool IsValid(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
    public void Touch(DateTimeOffset now) => LastSeenAt = now;
    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}

public sealed class UserMfaEnrollment
{
    private UserMfaEnrollment() { }
    public UserMfaEnrollment(Guid userId,string secret,string actor,DateTimeOffset now)
    {Id=Guid.NewGuid();UserId=userId;Secret=secret;CreatedBy=actor;CreatedAt=now;}
    public Guid Id {get;private set;} public Guid UserId {get;private set;} public string Secret {get;private set;}=""; public bool Confirmed {get;private set;} public string CreatedBy {get;private set;}=""; public DateTimeOffset CreatedAt {get;private set;} public DateTimeOffset? ConfirmedAt {get;private set;}
    public void Confirm(DateTimeOffset now){Confirmed=true;ConfirmedAt=now;}
}

public sealed class MfaRecoveryCode
{
    private MfaRecoveryCode() { }
    public MfaRecoveryCode(Guid userId,string codeHash,DateTimeOffset now){Id=Guid.NewGuid();UserId=userId;CodeHash=codeHash;CreatedAt=now;}
    public Guid Id {get;private set;} public Guid UserId {get;private set;} public string CodeHash {get;private set;}=""; public DateTimeOffset CreatedAt {get;private set;} public DateTimeOffset? UsedAt {get;private set;}
    public void Use(DateTimeOffset now){if(UsedAt is not null)throw new InvalidOperationException("Recovery code has already been used.");UsedAt=now;}
}

public sealed class RoleDelegation
{
    private RoleDelegation() { }
    public RoleDelegation(Guid programId, Guid delegatorUserId, Guid delegateUserId, ProgramRole role, DateTimeOffset startsAt, DateTimeOffset endsAt, string reason, string createdBy, DateTimeOffset now)
    { if (endsAt <= startsAt) throw new ArgumentException("Delegation end must be after its start."); Id = Guid.NewGuid(); ProgramId = programId; DelegatorUserId = delegatorUserId; DelegateUserId = delegateUserId; Role = role; StartsAt = startsAt; EndsAt = endsAt; Reason = reason.Trim(); CreatedBy = createdBy; CreatedAt = now; }
    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public Guid DelegatorUserId { get; private set; }
    public Guid DelegateUserId { get; private set; }
    public ProgramRole Role { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public string Reason { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && StartsAt <= now && EndsAt > now;
    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}

public sealed class ElectronicSignature
{
    private ElectronicSignature() { }
    public ElectronicSignature(Guid userId, string userName, string displayName, Guid programId, string artifactType, Guid artifactId, string artifactRevision, string action, string meaning, string contentHash, string ipAddress, DateTimeOffset now)
    { Id = Guid.NewGuid(); UserId = userId; UserName = userName; DisplayName = displayName; ProgramId = programId; ArtifactType = artifactType; ArtifactId = artifactId; ArtifactRevision = artifactRevision; Action = action; Meaning = meaning; ContentHash = contentHash; IpAddress = ipAddress; SignedAt = now; }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public Guid ProgramId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string ArtifactRevision { get; private set; } = "";
    public string Action { get; private set; } = "";
    public string Meaning { get; private set; } = "";
    public string ContentHash { get; private set; } = "";
    public string IpAddress { get; private set; } = "";
    public DateTimeOffset SignedAt { get; private set; }
}

public sealed class SecurityAuditEvent
{
    private SecurityAuditEvent() { }
    public SecurityAuditEvent(string eventType, string actorId, string target, string outcome, string detail, string ipAddress, DateTimeOffset now)
    { Id = Guid.NewGuid(); EventType = eventType; ActorId = actorId; Target = target; Outcome = outcome; Detail = detail; IpAddress = ipAddress; OccurredAt = now; }
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = "";
    public string ActorId { get; private set; } = "";
    public string Target { get; private set; } = "";
    public string Outcome { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string IpAddress { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}