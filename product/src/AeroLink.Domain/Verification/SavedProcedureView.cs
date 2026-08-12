using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>
/// A named worklist over the test procedure library, owned by the person who saved it and optionally shared.
///
/// The verification twin of <see cref="AeroLink.Domain.Requirements.SavedRequirementView"/>, with the same
/// lifecycle: renaming, resharing and replacing are separate from creating, because a view somebody else has
/// a link to must keep its identity when its owner tidies it up.
/// </summary>
public sealed class SavedProcedureView
{
    private SavedProcedureView() { }

    public SavedProcedureView(Guid projectId, Guid ownerId, string name, string queryJson, string columnsJson,
        bool shared, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; OwnerId = ownerId; Name = name.Trim();
        QueryJson = queryJson; ColumnsJson = columnsJson; IsShared = shared; CreatedAt = now; UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = "";
    public string QueryJson { get; private set; } = "{}";
    public string ColumnsJson { get; private set; } = "[]";
    public bool IsShared { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string name, DateTimeOffset now)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new DomainException("A saved view needs a name.");
        Name = trimmed; UpdatedAt = now;
    }

    public void SetShared(bool shared, DateTimeOffset now) { IsShared = shared; UpdatedAt = now; }

    public void Replace(string queryJson, string columnsJson, DateTimeOffset now)
    { QueryJson = queryJson; ColumnsJson = columnsJson; UpdatedAt = now; }
}
