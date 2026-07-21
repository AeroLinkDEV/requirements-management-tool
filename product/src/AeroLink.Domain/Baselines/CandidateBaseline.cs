using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Baselines;

public enum CandidateBaselineState { Draft, Frozen, Released }

public sealed class BaselineScrSelection
{
    private BaselineScrSelection() { }
    internal BaselineScrSelection(Guid baselineId, Guid scrId, string scrDisplayNumber)
    { Id = Guid.NewGuid(); BaselineId = baselineId; ScrId = scrId; ScrDisplayNumber = scrDisplayNumber; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid ScrId { get; private set; }
    public string ScrDisplayNumber { get; private set; } = string.Empty;
}

public sealed class BaselineEvent
{
    private BaselineEvent() { }
    internal BaselineEvent(Guid baselineId, string eventType, string actorId, string detail, DateTimeOffset occurredAt)
    { Id = Guid.NewGuid(); BaselineId = baselineId; EventType = eventType; ActorId = actorId; Detail = detail; OccurredAt = occurredAt; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string ActorId { get; private set; } = string.Empty;
    public string Detail { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}

public sealed class CandidateBaseline
{
    private readonly List<BaselineScrSelection> _selections = [];
    private readonly List<BaselineEvent> _events = [];
    private CandidateBaseline() { }

    public CandidateBaseline(string baseNumber, int revision, Guid projectId, Guid releaseId,
        Guid? predecessorBaselineId, string name, string actorId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); BaseNumber = ArtifactNumber.ValidateBase(baseNumber); Revision = revision;
        ProjectId = projectId; ReleaseId = releaseId; PredecessorBaselineId = predecessorBaselineId;
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A baseline name is required.");
        Name = name.Trim(); CreatedAt = now; UpdatedAt = now; State = CandidateBaselineState.Draft;
        Event("CandidateBaselineCreated", actorId, $"Created {DisplayNumber}.", now);
    }

    public Guid Id { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid? PredecessorBaselineId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public CandidateBaselineState State { get; private set; }
    public string? ContentHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? FrozenAt { get; private set; }
    public DateTimeOffset? RequirementsMaterializedAt { get; private set; }
    public string? RequirementsHash { get; private set; }
    public IReadOnlyCollection<BaselineScrSelection> Selections => _selections.AsReadOnly();
    public IReadOnlyCollection<BaselineEvent> Events => _events.AsReadOnly();

    public void UpdateDraft(string name, string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A baseline name is required.");
        Name = name.Trim(); UpdatedAt = now;
        Event("CandidateBaselineDraftUpdated", actorId, $"Updated draft {DisplayNumber}.", now);
    }

    public void Select(SystemChangeRequest scr, string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (scr.State != ScrState.Approved) throw new DomainException("Only approved SCRs can be selected.");
        if (scr.ProjectId != ProjectId || scr.TargetReleaseId != ReleaseId)
            throw new DomainException("The SCR does not belong to this project and target release.");
        if (_selections.Any(x => x.ScrId == scr.Id)) throw new DomainException("The SCR is already selected.");
        _selections.Add(new BaselineScrSelection(Id, scr.Id, scr.DisplayNumber));
        UpdatedAt = now;
        scr.MarkSelectedForBaseline(actorId, now);
        Event("ScrSelected", actorId, $"Selected {scr.DisplayNumber}.", now);
    }

    public void Remove(SystemChangeRequest scr, string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        var selection = _selections.SingleOrDefault(x => x.ScrId == scr.Id)
            ?? throw new DomainException("The SCR is not selected in this baseline.");
        _selections.Remove(selection);
        UpdatedAt = now;
        scr.UnmarkSelectedForBaseline(actorId, now);
        Event("ScrRemoved", actorId, $"Removed {scr.DisplayNumber}.", now);
    }

    public void Freeze(string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (_selections.Count == 0) throw new DomainException("At least one approved SCR must be selected before freezing a baseline.");
        var manifest = string.Join("|", DisplayNumber, ProjectId, ReleaseId,
            string.Join(";", _selections.OrderBy(x => x.ScrDisplayNumber).Select(x => $"{x.ScrId}:{x.ScrDisplayNumber}")));
        ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        State = CandidateBaselineState.Frozen;
        FrozenAt = now;
        UpdatedAt = now;
        Event("CandidateBaselineFrozen", actorId, $"Frozen {DisplayNumber} with {_selections.Count} exact SCR revisions and hash {ContentHash}.", now);
    }

    public void MarkRequirementsMaterialized(string actorId, string requirementsHash, int activeCount, DateTimeOffset now)
    {
        if (State != CandidateBaselineState.Frozen) throw new DomainException("Only a frozen baseline can be materialized.");
        if (RequirementsMaterializedAt is not null) throw new DomainException("The requirement baseline is already materialized and immutable.");
        if (string.IsNullOrWhiteSpace(requirementsHash) || requirementsHash.Length != 64) throw new DomainException("A valid requirement manifest hash is required.");
        RequirementsHash = requirementsHash; RequirementsMaterializedAt = now;
        UpdatedAt = now;
        Event("RequirementsMaterialized", actorId, $"Materialized {activeCount} effective requirement revisions with hash {requirementsHash}.", now);
    }

    public void MarkReleased(string actorId, DateTimeOffset now)
    {
        if (State != CandidateBaselineState.Frozen || RequirementsMaterializedAt is null) throw new DomainException("Only a frozen, materialized baseline can be released.");
        State = CandidateBaselineState.Released; UpdatedAt = now; Event("BaselineReleased", actorId, $"Released immutable baseline {DisplayNumber}.", now);
    }

    private void EnsureDraft() { if (State != CandidateBaselineState.Draft) throw new DomainException("A frozen baseline is immutable."); }
    private void Event(string type, string actorId, string detail, DateTimeOffset now) => _events.Add(new BaselineEvent(Id, type, actorId, detail, now));
}
