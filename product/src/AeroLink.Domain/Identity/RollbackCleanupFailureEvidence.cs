using AeroLink.Domain.Common;

namespace AeroLink.Domain.Identity;

/// <summary>
/// One deterministic, bounded chunk of rollback-cleanup failure evidence recorded through an ISOLATED clean
/// persistence boundary after a failed Procedure cutover transaction.
///
/// The failed transaction itself is rolled back; this table is written by a fresh DbContext so the evidence
/// save can never resave rolled-back cutover entities. <c>(OperationId, Sequence)</c> is unique, so a retry
/// of the same failure can never duplicate a chunk, and every failed storage key remains recoverable from
/// the bounded <see cref="Content"/> rows plus the canonical aggregate hash.
/// </summary>
public sealed class RollbackCleanupFailureEvidence
{
    private RollbackCleanupFailureEvidence() { }

    public RollbackCleanupFailureEvidence(Guid operationId, int sequence, int totalKeys,
        string canonicalAggregateHash, IReadOnlyList<string> storageKeys, DateTimeOffset createdAt)
    {
        if (operationId == Guid.Empty)
            throw new DomainException("Rollback cleanup evidence requires an operation identity.");
        if (sequence < 0)
            throw new DomainException("Rollback cleanup evidence sequence cannot be negative.");
        if (storageKeys is null || storageKeys.Count == 0)
            throw new DomainException("Rollback cleanup evidence requires at least one failed storage key.");
        if (totalKeys < storageKeys.Count)
            throw new DomainException("Rollback cleanup evidence total key count cannot be smaller than a chunk.");
        if (string.IsNullOrWhiteSpace(canonicalAggregateHash) || canonicalAggregateHash.Trim().Length != 64)
            throw new DomainException("Rollback cleanup evidence requires a SHA-256 canonical aggregate hash.");
        var content = string.Join(";", storageKeys.Select(key =>
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains(';'))
                throw new DomainException("Rollback cleanup evidence storage keys must be nonblank and semicolon-free.");
            return key.Trim();
        }));
        if (content.Length > 1500)
            throw new DomainException("Rollback cleanup evidence chunk exceeds the 1,500-character bound.");
        Id = Guid.NewGuid();
        OperationId = operationId;
        Sequence = sequence;
        TotalKeys = totalKeys;
        CanonicalAggregateHash = canonicalAggregateHash.Trim().ToLowerInvariant();
        EntryCount = storageKeys.Count;
        Content = content;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public int Sequence { get; private set; }
    public int TotalKeys { get; private set; }
    public string CanonicalAggregateHash { get; private set; } = string.Empty;
    public int EntryCount { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
