using AeroLink.Domain.Common;

namespace AeroLink.Domain.Baselines;

/// <summary>
/// One deterministically ordered, bounded chunk of the exact per-baseline Case→Procedure provenance recorded
/// by the governed #726 execution cutover.
///
/// The summary <see cref="BaselineEvent"/> stays safely below its 4,000-character column limit under every
/// population size; the exact mapping identities live here in durable, sequence-numbered rows linked to that
/// event. <c>(BaselineId, Sequence)</c> is unique, so reruns and crash recovery can never duplicate or
/// partially overwrite a chunk.
/// </summary>
public sealed class BaselineExecutionCutoverProvenance
{
    private BaselineExecutionCutoverProvenance() { }

    public BaselineExecutionCutoverProvenance(Guid baselineId, Guid eventId, int sequence,
        int totalMappings, string canonicalAggregateHash, IReadOnlyList<string> entries)
    {
        if (baselineId == Guid.Empty || eventId == Guid.Empty)
            throw new DomainException("Execution cutover provenance requires a baseline and summary event identity.");
        if (sequence < 0)
            throw new DomainException("Execution cutover provenance sequence cannot be negative.");
        if (entries is null || entries.Count == 0)
            throw new DomainException("Execution cutover provenance requires at least one exact mapping.");
        if (totalMappings < entries.Count)
            throw new DomainException(
                "Execution cutover provenance total mapping count cannot be smaller than a chunk.");
        if (string.IsNullOrWhiteSpace(canonicalAggregateHash) || canonicalAggregateHash.Trim().Length != 64)
            throw new DomainException(
                "Execution cutover provenance requires a SHA-256 canonical aggregate hash.");
        var content = string.Join(";", entries.Select(entry =>
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new DomainException("Execution cutover provenance entries cannot be blank.");
            return entry.Trim();
        }));
        if (content.Length > 2000)
            throw new DomainException("Execution cutover provenance chunk exceeds the 2,000-character bound.");
        Id = Guid.NewGuid();
        BaselineId = baselineId;
        EventId = eventId;
        Sequence = sequence;
        TotalMappings = totalMappings;
        CanonicalAggregateHash = canonicalAggregateHash.Trim().ToLowerInvariant();
        EntryCount = entries.Count;
        Content = content;
    }

    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    /// <summary>The bounded <see cref="BaselineEvent"/> summary this chunk belongs to.</summary>
    public Guid EventId { get; private set; }
    public int Sequence { get; private set; }
    public int TotalMappings { get; private set; }
    public string CanonicalAggregateHash { get; private set; } = string.Empty;
    public int EntryCount { get; private set; }
    public string Content { get; private set; } = string.Empty;
}
