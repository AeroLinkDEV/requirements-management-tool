using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Baselines;

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

public sealed class CandidateBaseline
{
    private readonly List<BaselineScrSelection> _selections = [];
    private readonly List<AuditEvent> _auditEvents = [];
    private CandidateBaseline() { }

    public CandidateBaseline(string baseNumber, int revision, Guid projectId, Guid releaseId,
        Guid? predecessorBaselineId, string name, string actorId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); BaseNumber = ArtifactNumber.ValidateBase(baseNumber); Revision = revision;
        ProjectId = projectId; ReleaseId = releaseId; PredecessorBaselineId = predecessorBaselineId;
        Name = name.Trim(); CreatedAt = now;
        _auditEvents.Add(new AuditEvent(Id, "CandidateBaselineCreated", actorId, $"Created {DisplayNumber}.", now));
    }

    public Guid Id { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid? PredecessorBaselineId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<BaselineScrSelection> Selections => _selections.AsReadOnly();
    public IReadOnlyCollection<AuditEvent> AuditEvents => _auditEvents.AsReadOnly();

    public void Select(SystemChangeRequest scr, string actorId, DateTimeOffset now)
    {
        if (scr.State != ScrState.Approved) throw new DomainException("Only approved SCRs can be selected.");
        if (scr.ProjectId != ProjectId || scr.TargetReleaseId != ReleaseId)
            throw new DomainException("The SCR does not belong to this project and target release.");
        if (_selections.Any(x => x.ScrId == scr.Id)) throw new DomainException("The SCR is already selected.");
        _selections.Add(new BaselineScrSelection(Id, scr.Id, scr.DisplayNumber));
        scr.MarkSelectedForBaseline(actorId, now);
        _auditEvents.Add(new AuditEvent(Id, "ScrSelected", actorId, $"Selected {scr.DisplayNumber}.", now));
    }
}
