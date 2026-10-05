using AeroLink.Domain.Common;

namespace AeroLink.Domain.Notifications;

public enum NotificationMode { Disabled, Capture, ControlledTest, Live }
public enum NotificationGenerationState { HeldAdmission, Pending, RetryDue, Claimed, TransmissionStarted, SmtpAccepted, Captured, TestAccepted, PermanentFailed, ConfigBlocked, AcceptanceUnknown, RetryExhausted, Suppressed }
public enum NotificationAttemptOutcome { InProgress, SmtpAccepted, Captured, TestAccepted, TransientRefused, PermanentRefused, ConfigBlocked, AcceptanceUnknown, AbandonedBeforeTransmission }

/// <summary>Immutable administrator values. Installation restrictions are resolved independently.</summary>
public sealed class NotificationSettingsRevision
{
    private NotificationSettingsRevision() { }
    public NotificationSettingsRevision(string installationId, long version, NotificationMode mode, string host,
        int port, string sender, string displayName, string baseUrl, string userName, string protectedCredential,
        string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(installationId) || version < 1 || port is < 1 or > 65535)
            throw new DomainException("Notification settings identity, version and port are required.");
        Id = Guid.NewGuid(); InstallationId = installationId; Version = version; Mode = mode;
        Host = host.Trim(); Port = port; Sender = sender.Trim(); DisplayName = displayName.Trim();
        BaseUrl = baseUrl.Trim(); UserName = userName.Trim(); ProtectedCredential = protectedCredential;
        Actor = actor; CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public string InstallationId { get; private set; } = "";
    public long Version { get; private set; }
    public NotificationMode Mode { get; private set; }
    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string Sender { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string ProtectedCredential { get; private set; } = "";
    public string AllowedEventTypesJson { get; private set; } = "[\"ReviewActivated\",\"ApprovalActivated\",\"TestChangeRequestApprovalRequested\",\"DocumentReviewActivated\",\"DocumentApprovalActivated\",\"ReviewChangesRequested\",\"DocumentReturned\",\"RequirementAssignment\",\"ManagedDocumentStewardAssigned\",\"ManagedDocumentRevisionAssigned\",\"NotificationTransportTest\"]";
    public void NarrowEvents(string json) { AllowedEventTypesJson = json; }
    public string Actor { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class NotificationInstallationState
{
    private NotificationInstallationState() { }
    public NotificationInstallationState(string installationId) { InstallationId = installationId; Version = 1; }
    public string InstallationId { get; private set; } = "";
    public Guid? SettingsRevisionId { get; private set; }
    public Guid? AdmissionEpochId { get; private set; }
    public long Version { get; private set; }
    public bool SendingEnabled { get; private set; }
    public void SetSettings(Guid id, long expectedVersion, bool pauseAdmission = false)
    { Check(expectedVersion); SettingsRevisionId = id; if (pauseAdmission) SendingEnabled = false; Version++; }
    public void Admit(Guid id, long expectedVersion)
    { Check(expectedVersion); AdmissionEpochId = id; SendingEnabled = true; Version++; }
    public void Pause(long expectedVersion) { Check(expectedVersion); SendingEnabled = false; Version++; }
    private void Check(long expectedVersion)
    { if (Version != expectedVersion) throw new DomainException("Notification installation settings changed; recover the original operation or refresh."); }
}

public sealed class NotificationAdmissionEpoch
{
    private NotificationAdmissionEpoch() { }
    public NotificationAdmissionEpoch(string installationId, NotificationMode mode, Guid settingsRevisionId,
        Guid sendGeneration, long cutoffSequence, string actor, DateTimeOffset now)
    {
        if (mode == NotificationMode.Disabled) throw new DomainException("Disabled does not admit notification work.");
        Id = Guid.NewGuid(); InstallationId = installationId; Mode = mode; SettingsRevisionId = settingsRevisionId;
        SendGeneration = sendGeneration; CutoffSequence = cutoffSequence; Actor = actor; CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public string InstallationId { get; private set; } = "";
    public NotificationMode Mode { get; private set; }
    public Guid SettingsRevisionId { get; private set; }
    public Guid SendGeneration { get; private set; }
    public long CutoffSequence { get; private set; }
    public string Actor { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>One concrete message. Retry retains its destination, Date, Message-ID and protected MIME.</summary>
public sealed class NotificationDeliveryGeneration
{
    private NotificationDeliveryGeneration() { }
    public NotificationDeliveryGeneration(Guid deliveryId, Guid epochId, Guid settingsId, NotificationMode mode,
        Guid sendGeneration, string protectedAddress, string addressHash, DateTimeOffset now,
        Guid? predecessorId = null, bool diagnostic = false, bool acknowledgeDuplicateRisk = false)
    {
        Id = Guid.NewGuid(); DeliveryId = deliveryId; AdmissionEpochId = epochId; OriginalAdmissionEpochId = epochId; SettingsRevisionId = settingsId;
        Mode = mode; SendGeneration = sendGeneration; OriginalSendGeneration = sendGeneration; ProtectedAddress = protectedAddress; AddressHash = addressHash;
        CreatedAt = now; CreatedTicks = now.UtcTicks; MessageDate = now; DeadlineTicks = now.AddHours(24).UtcTicks; DueTicks = now.UtcTicks;
        OriginalDeadlineTicks = DeadlineTicks; MessageId = $"{Id:N}@notifications.aerolink.invalid"; PredecessorId = predecessorId;
        MaximumAttempts = diagnostic ? 1 : 14; MaximumPhysicalAttempts = diagnostic ? 1 : int.MaxValue; State = NotificationGenerationState.Pending; Version = 1;
        InitialDeliveryId = predecessorId is null ? deliveryId : null; DuplicateRiskAcknowledged = acknowledgeDuplicateRisk;
    }
    public Guid Id { get; private set; }
    public Guid DeliveryId { get; private set; }
    public Guid? InitialDeliveryId { get; private set; }
    public bool DuplicateRiskAcknowledged { get; private set; }
    public Guid AdmissionEpochId { get; private set; }
    public Guid OriginalAdmissionEpochId { get; private set; }
    public Guid OriginalSendGeneration { get; private set; }
    public long OriginalDeadlineTicks { get; private set; }
    public Guid SettingsRevisionId { get; private set; }
    public NotificationMode Mode { get; private set; }
    public Guid SendGeneration { get; private set; }
    public Guid? PredecessorId { get; private set; }
    public string MessageId { get; private set; } = "";
    public DateTimeOffset MessageDate { get; private set; }
    public string ProtectedAddress { get; private set; } = "";
    public string AddressHash { get; private set; } = "";
    public string ProtectedMime { get; private set; } = "";
    public string BodyHash { get; private set; } = "";
    public string MessageConfigurationHash { get; private set; } = "";
    public string BlockedEffectiveSettingsHash { get; private set; } = "";
    public int TemplateVersion { get; private set; } = 1;
    public int ContentPolicyVersion { get; private set; } = 1;
    public NotificationGenerationState State { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public Guid? CurrentAttemptId { get; private set; }
    public long LeaseUntilTicks { get; private set; }
    public long DueTicks { get; private set; }
    public long DeadlineTicks { get; private set; }
    public int Attempts { get; private set; }
    public int TransientFailures { get; private set; }
    public int MaximumAttempts { get; private set; }
    public int MaximumPhysicalAttempts { get; private set; }
    public long Version { get; private set; }
    public string SafeCode { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public long CreatedTicks { get; private set; }
    public void FreezeMime(string protectedMime, string hash, string configurationHash)
    {
        if (ProtectedMime.Length > 0 && (ProtectedMime != protectedMime || BodyHash != hash))
            throw new DomainException("Concrete notification content is immutable.");
        ProtectedMime = protectedMime; BodyHash = hash; MessageConfigurationHash = configurationHash;
    }
    public void Hold(string code, NotificationGenerationState state = NotificationGenerationState.ConfigBlocked, string blockedSettingsHash = "")
    { State = state; SafeCode = code; BlockedEffectiveSettingsHash = blockedSettingsHash; ClaimToken = null; Version++; }
    public void Readmit(Guid epochId, Guid sendGeneration, DateTimeOffset now, bool diagnostic = false, bool acknowledgeDuplicateRisk = false)
    {
        if (State is not (NotificationGenerationState.RetryExhausted or NotificationGenerationState.ConfigBlocked or NotificationGenerationState.HeldAdmission))
            throw new DomainException("Only held known-unsent work can be readmitted.");
        AdmissionEpochId = epochId; SendGeneration = sendGeneration;
        DuplicateRiskAcknowledged |= acknowledgeDuplicateRisk;
        State = NotificationGenerationState.Pending; DeadlineTicks = now.AddHours(24).UtcTicks;
        DueTicks = now.UtcTicks; MaximumAttempts = TransientFailures + (diagnostic ? 1 : 14); MaximumPhysicalAttempts = diagnostic ? Attempts + 1 : int.MaxValue; SafeCode = ""; Version++;
    }
    public void QueueUnknownReplay(Guid epochId, DateTimeOffset now)
    {
        if (State != NotificationGenerationState.AcceptanceUnknown) throw new DomainException("Only acceptance-unknown mail uses deliberate replay.");
        AdmissionEpochId = epochId; DuplicateRiskAcknowledged = true; State = NotificationGenerationState.Pending;
        ClaimToken = null; DueTicks = now.UtcTicks; DeadlineTicks = now.AddHours(24).UtcTicks;
        MaximumPhysicalAttempts = Attempts + 1; MaximumAttempts = TransientFailures + 1; SafeCode = ""; Version++;
    }
}

/// <summary>Durable permission precedes all socket activity. Expiry is attention, never socket takeover.</summary>
public sealed class NotificationPhysicalAttempt
{
    private NotificationPhysicalAttempt() { }
    public NotificationPhysicalAttempt(Guid generationId, Guid claimToken, Guid settingsId, string policyHash,
        string hostIdentity, int processId, long processStartTicks, DateTimeOffset now, string protectedSettingsSnapshot = "", string effectiveSettingsHash = "")
    {
        Id = Guid.NewGuid(); GenerationId = generationId; ClaimToken = claimToken; SettingsRevisionId = settingsId;
        PolicyHash = policyHash; HostIdentity = hostIdentity; ProcessId = processId; ProcessStartTicks = processStartTicks;
        ClaimedAt = now; Outcome = NotificationAttemptOutcome.InProgress; ProtectedSettingsSnapshot = protectedSettingsSnapshot; EffectiveSettingsHash = effectiveSettingsHash;
    }
    public Guid Id { get; private set; }
    public Guid GenerationId { get; private set; }
    public Guid ClaimToken { get; private set; }
    public Guid SettingsRevisionId { get; private set; }
    public string PolicyHash { get; private set; } = "";
    public string ProtectedSettingsSnapshot { get; private set; } = "";
    public string EffectiveSettingsHash { get; private set; } = "";
    public string HostIdentity { get; private set; } = "";
    public int ProcessId { get; private set; }
    public long ProcessStartTicks { get; private set; }
    public DateTimeOffset ClaimedAt { get; private set; }
    public DateTimeOffset? TransmissionStartedAt { get; private set; }
    public NotificationAttemptOutcome Outcome { get; private set; }
    public string Phase { get; private set; } = "Prepared";
    public int? SmtpStatus { get; private set; }
    public string SafeCode { get; private set; } = "";
    public bool TransportDisposed { get; private set; }
    public bool CleanupWarning { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? DisposedAt { get; private set; }
    public void Start(DateTimeOffset now)
    { if (TransmissionStartedAt is not null) throw new DomainException("A physical attempt starts once."); TransmissionStartedAt = now; }
    public void Complete(NotificationAttemptOutcome outcome, string phase, int? status, string code, bool disposed, DateTimeOffset now, bool cleanupWarning = false)
    {
        if (Outcome != NotificationAttemptOutcome.InProgress) throw new DomainException("A physical attempt's outcome is immutable.");
        Outcome = outcome; Phase = phase; SmtpStatus = status; SafeCode = code; TransportDisposed = disposed; CleanupWarning = cleanupWarning; CompletedAt = now;
        if (disposed) DisposedAt = now;
    }
    public void AcknowledgeDisposal(DateTimeOffset now, bool cleanupWarning = false) { if (!TransportDisposed) { TransportDisposed = true; DisposedAt = now; } CleanupWarning |= cleanupWarning; }
}

/// <summary>A committed command result, recovered before mutable state is checked again.</summary>
public sealed class NotificationOperation
{
    private NotificationOperation() { }
    public NotificationOperation(string installationId, string actor, string family, Guid operationKey,
        string payloadHash, DateTimeOffset now)
    {
        if (operationKey == Guid.Empty) throw new DomainException("Generate an operation key before submitting the command.");
        Id = Guid.NewGuid(); InstallationId = installationId; Actor = actor; Family = family;
        OperationKey = operationKey; PayloadHash = payloadHash; CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public string InstallationId { get; private set; } = "";
    public string Actor { get; private set; } = "";
    public string Family { get; private set; } = "";
    public Guid OperationKey { get; private set; }
    public string PayloadHash { get; private set; } = "";
    public Guid? NotificationId { get; private set; }
    public Guid? GenerationId { get; private set; }
    public string ResultJson { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public void Record(string json, Guid? notificationId = null, Guid? generationId = null)
    {
        if (ResultJson.Length > 0) throw new DomainException("A committed notification command receipt is immutable.");
        ResultJson = json; NotificationId = notificationId; GenerationId = generationId;
    }
}

/// <summary>One explicit browser preference confirmation; GET requests never create this receipt.</summary>
public sealed class NotificationPreferenceConfirmation
{
    private NotificationPreferenceConfirmation() { }
    public NotificationPreferenceConfirmation(string challengeHash, DateTimeOffset now)
    { ChallengeHash = challengeHash; ConsumedAt = now; }
    public string ChallengeHash { get; private set; } = "";
    public DateTimeOffset ConsumedAt { get; private set; }
}
