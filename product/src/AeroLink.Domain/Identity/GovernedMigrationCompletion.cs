using AeroLink.Domain.Common;

namespace AeroLink.Domain.Identity;

/// <summary>
/// One atomic, database-enforced completion claim for a governed platform migration. A unique marker makes
/// the claim exclusive: concurrent startup instances cannot insert duplicate completion evidence after
/// per-project work has committed.
/// </summary>
public sealed class GovernedMigrationCompletion
{
    private GovernedMigrationCompletion() { }

    public GovernedMigrationCompletion(string marker, string actor, DateTimeOffset completedAt,
        string totalsJson)
    {
        if (string.IsNullOrWhiteSpace(marker))
            throw new DomainException("A governed migration completion requires its marker.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new DomainException("A governed migration completion requires an actor.");
        if (string.IsNullOrWhiteSpace(totalsJson))
            throw new DomainException("A governed migration completion requires totals evidence.");
        Id = Guid.NewGuid();
        Marker = marker.Trim();
        Actor = actor.Trim();
        CompletedAt = completedAt;
        TotalsJson = totalsJson.Trim();
    }

    public Guid Id { get; private set; }
    public string Marker { get; private set; } = string.Empty;
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; private set; }
    public string TotalsJson { get; private set; } = string.Empty;
}
