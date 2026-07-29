namespace AeroLink.Domain.Common;

/// <summary>
/// The next controlled number for one numbering scope, claimed atomically.
///
/// Every controlled identifier was allocated by loading all existing numbers of its prefix, taking the
/// maximum in application memory and adding one, then relying on a unique-index violation to catch two
/// writers that picked the same value. That is wrong in two directions at once: two legitimate concurrent
/// creates fail and have to be resubmitted by hand, and every create scans an identifier set that only ever
/// grows.
///
/// A claim is a single row update, so the database — not a retry loop — decides who gets which number. This
/// type only defines the row; the claim itself is one statement issued by `IdentifierAllocator`, deliberately
/// not a tracked read-modify-write, because a tracked increment would take effect only at the caller's save
/// and put the read and the write back on opposite sides of the same race.
///
/// **Scope is repository-wide per prefix.** `SCR`, `SWCR`, `SYSR`, `HLR`, `LLR`, `SYSTP`, `HLRTP`, `LLRTP`
/// and `PR` each number independently and continuously across every Program and Project, which is what the
/// existing unique indexes already enforce and what the identifier documentation already describes.
///
/// **Gaps are accepted and expected.** A number is claimed before the record it names is committed, so a
/// rolled-back create consumes its number permanently. That is the correct trade for a controlled
/// identifier: reusing a number that a failed attempt might have printed, exported or referenced elsewhere
/// would be far worse than a gap in the sequence. Nothing in the product infers meaning from contiguity.
/// </summary>
public sealed class IdentifierSequence
{
    private IdentifierSequence() { }

    public IdentifierSequence(string scope, long nextValue)
    {
        Id = Guid.NewGuid();
        Scope = scope.Trim().ToUpperInvariant();
        NextValue = nextValue;
    }

    public Guid Id { get; private set; }
    /// <summary>The identifier prefix this sequence numbers, upper-cased.</summary>
    public string Scope { get; private set; } = "";
    /// <summary>The value the next claim will return.</summary>
    public long NextValue { get; private set; }
    /// <summary>Incremented on every claim so two concurrent claims cannot both succeed against one read.</summary>
    public long ConcurrencyStamp { get; private set; }
}
