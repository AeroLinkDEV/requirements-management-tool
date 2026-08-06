using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Baselines;

public enum CandidateBaselineState { Draft, Frozen, Released }

public sealed class BaselineChangeRequestSelection
{
    private BaselineChangeRequestSelection() { }
    internal BaselineChangeRequestSelection(Guid baselineId, Guid changeRequestId, string scrDisplayNumber)
    { Id = Guid.NewGuid(); BaselineId = baselineId; ChangeRequestId = changeRequestId; ChangeRequestDisplayNumber = scrDisplayNumber; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid ChangeRequestId { get; private set; }
    public string ChangeRequestDisplayNumber { get; private set; } = string.Empty;
}

/// <summary>
/// An approved test change request whose procedure decisions this baseline carries.
///
/// Held separately from <see cref="BaselineChangeRequestSelection"/> rather than derived from it. A test change
/// request is raised from a change request already selected here, but it is approved on its own schedule by its
/// own discipline — which is the whole reason it is a separate package. Deriving membership would mean a build
/// could not close its requirements until its test work was finished.
/// </summary>
public sealed class BaselineTestChangeRequestSelection
{
    private BaselineTestChangeRequestSelection() { }
    internal BaselineTestChangeRequestSelection(Guid baselineId, Guid testChangeRequestId, string tcrDisplayNumber)
    { Id = Guid.NewGuid(); BaselineId = baselineId; TestChangeRequestId = testChangeRequestId; TestChangeRequestDisplayNumber = tcrDisplayNumber; }
    public Guid Id { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid TestChangeRequestId { get; private set; }
    public string TestChangeRequestDisplayNumber { get; private set; } = string.Empty;
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
    private readonly List<BaselineChangeRequestSelection> _selections = [];
    private readonly List<BaselineTestChangeRequestSelection> _testChangeSelections = [];
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
    /// <summary>When this baseline's test procedures were fixed. Independent of the requirement manifest.</summary>
    public DateTimeOffset? TestProceduresMaterializedAt { get; private set; }
    public string? TestProceduresHash { get; private set; }
    public IReadOnlyCollection<BaselineChangeRequestSelection> Selections => _selections.AsReadOnly();
    public IReadOnlyCollection<BaselineTestChangeRequestSelection> TestChangeSelections => _testChangeSelections.AsReadOnly();
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
        if (scr.State != ChangeRequestState.Approved) throw new DomainException("Only approved SCRs can be selected.");
        if (scr.ProjectId != ProjectId || scr.TargetReleaseId != ReleaseId)
            throw new DomainException("The change request does not belong to this project and target release.");
        if (_selections.Any(x => x.ChangeRequestId == scr.Id)) throw new DomainException("The change request is already selected.");
        _selections.Add(new BaselineChangeRequestSelection(Id, scr.Id, scr.DisplayNumber));
        UpdatedAt = now;
        scr.MarkSelectedForBaseline(actorId, now);
        Event("ScrSelected", actorId, $"Selected {scr.DisplayNumber}.", now);
    }

    public void Remove(SystemChangeRequest scr, string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        var selection = _selections.SingleOrDefault(x => x.ChangeRequestId == scr.Id)
            ?? throw new DomainException("The change request is not selected in this baseline.");
        _selections.Remove(selection);
        UpdatedAt = now;
        scr.UnmarkSelectedForBaseline(actorId, now);
        Event("ScrRemoved", actorId, $"Removed {scr.DisplayNumber}.", now);
    }

    /// <summary>
    /// Adds an approved test change request's procedure decisions to this baseline.
    ///
    /// Allowed after the freeze, unlike selecting a change request, and that difference is deliberate. Freezing
    /// fixes which requirements the build contains; the procedures that verify them are written against those
    /// requirements and so are finished later. Requiring both before the freeze would either hold the requirement
    /// baseline open waiting for test work or force the test work to be guessed in advance. What closes here is
    /// the procedure manifest, at <see cref="MarkTestProceduresMaterialized"/>.
    /// </summary>
    public void SelectTestChangeRequest(TestChangeReview tcr, string actorId, DateTimeOffset now)
    {
        EnsureTestProceduresOpen();
        if (tcr.State != TestChangeReviewState.Approved)
            throw new DomainException("Only an approved test change request can be selected into a baseline.");
        if (tcr.Outcome != TestChangeReviewOutcome.ChangeRequired)
            throw new DomainException("This assessment concluded that no test work was required, so it has no procedure decisions to carry.");
        if (tcr.ProjectId != ProjectId || tcr.ReleaseId != ReleaseId)
            throw new DomainException("The test change request does not belong to this project and target release.");
        if (_testChangeSelections.Any(x => x.TestChangeRequestId == tcr.Id))
            throw new DomainException("The test change request is already selected.");
        _testChangeSelections.Add(new BaselineTestChangeRequestSelection(Id, tcr.Id, tcr.DisplayNumber));
        UpdatedAt = now;
        Event("TestChangeRequestSelected", actorId, $"Selected {tcr.DisplayNumber}.", now);
    }

    public void RemoveTestChangeRequest(TestChangeReview tcr, string actorId, DateTimeOffset now)
    {
        EnsureTestProceduresOpen();
        var selection = _testChangeSelections.SingleOrDefault(x => x.TestChangeRequestId == tcr.Id)
            ?? throw new DomainException("The test change request is not selected in this baseline.");
        _testChangeSelections.Remove(selection);
        UpdatedAt = now;
        Event("TestChangeRequestRemoved", actorId, $"Removed {tcr.DisplayNumber}.", now);
    }

    /// <summary>
    /// Fixes the exact set of procedure revisions this baseline carries, as its requirement counterpart does.
    ///
    /// Deliberately not a precondition of <see cref="MarkReleased"/>. Every build that exists today was released
    /// without one, and retrofitting the gate would make those builds retrospectively invalid rather than simply
    /// unmaterialized. Whether a future release should require it is a decision to take openly, not a side effect
    /// of adding the capability.
    /// </summary>
    public void MarkTestProceduresMaterialized(string actorId, string proceduresHash, int activeCount, DateTimeOffset now)
    {
        if (State == CandidateBaselineState.Draft)
            throw new DomainException("Freeze the baseline before materializing its test procedures.");
        if (RequirementsMaterializedAt is null)
            throw new DomainException("Materialize the requirement baseline before its test procedures — a procedure verifies a requirement that has to exist first.");
        if (TestProceduresMaterializedAt is not null)
            throw new DomainException("The test procedure baseline is already materialized and immutable.");
        if (string.IsNullOrWhiteSpace(proceduresHash) || proceduresHash.Length != 64)
            throw new DomainException("A valid test procedure manifest hash is required.");
        TestProceduresHash = proceduresHash; TestProceduresMaterializedAt = now;
        UpdatedAt = now;
        Event("TestProceduresMaterialized", actorId, $"Materialized {activeCount} effective test procedure revisions with hash {proceduresHash}.", now);
    }

    public void Freeze(string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (_selections.Count == 0) throw new DomainException("At least one approved change request must be selected before freezing a baseline.");
        var manifest = string.Join("|", DisplayNumber, ProjectId, ReleaseId,
            string.Join(";", _selections.OrderBy(x => x.ChangeRequestDisplayNumber).Select(x => $"{x.ChangeRequestId}:{x.ChangeRequestDisplayNumber}")));
        ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        State = CandidateBaselineState.Frozen;
        FrozenAt = now;
        UpdatedAt = now;
        Event("CandidateBaselineFrozen", actorId, $"Frozen {DisplayNumber} with {_selections.Count} exact change request revisions and hash {ContentHash}.", now);
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
    private void EnsureTestProceduresOpen()
    { if (TestProceduresMaterializedAt is not null) throw new DomainException("The test procedure baseline is already materialized and immutable."); }
    private void Event(string type, string actorId, string detail, DateTimeOffset now) => _events.Add(new BaselineEvent(Id, type, actorId, detail, now));
}
