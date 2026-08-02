using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>Why a procedure is in the set — the question "why are we running this?" asked once, in advance.</summary>
public enum TestSelectionReason
{
    /// <summary>A requirement this build changed is covered by it.</summary>
    ChangedRequirement,
    /// <summary>It belongs to an area somebody decided to exercise, whether or not anything in it changed.</summary>
    CoverageArea,
    /// <summary>A problem report asked for it to be run again.</summary>
    CorrectiveAction,
    /// <summary>Somebody judged it worth running and said why.</summary>
    Chosen
}

/// <summary>
/// The procedures a build has to run before it can ship.
///
/// A build is rarely worth its whole test suite. Somebody decides which procedures this one needs — the ones
/// covering what changed, plus whatever areas the change makes worth re-exercising — and that decision is
/// what the release is then measured against. Until now the product had no such record: "must be run before
/// release" was a checkbox on individual verification decisions, so the answer to "what are we running for
/// 1.6?" existed only as a filter across scattered items, and nobody could add a procedure that no changed
/// requirement had raised.
///
/// One set per build per discipline, matching how test change requests are already split: System, software
/// HLR and software LLR are finished by different people and are asked about separately.
///
/// It is a working list, not a controlled artefact. It has no number and no signature, because deciding what
/// to run is a planning judgement that changes as a build progresses — a procedure added after a defect is
/// found is the normal case, not an exception to be signed for. What it does have is a record of who added
/// each procedure, when, and why, so the shape of the decision survives the people who made it.
///
/// Nothing here concerns results. The set says what must be run; whether it was run, and what happened, is
/// recorded against executions and read back by the release gates.
/// </summary>
public sealed class BuildTestSet
{
    private readonly List<BuildTestSetEntry> _entries = [];

    private BuildTestSet() { }

    public BuildTestSet(Guid projectId, Guid releaseId, TestChangeReviewDiscipline discipline, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A test set requires its Project.");
        if (releaseId == Guid.Empty) throw new DomainException("A test set requires its software build.");
        if (!Enum.IsDefined(discipline)) throw new DomainException("A test set requires a known discipline.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        Discipline = discipline;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public TestChangeReviewDiscipline Discipline { get; private set; }
    public IReadOnlyCollection<BuildTestSetEntry> Entries => _entries.AsReadOnly();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    /// <summary>
    /// Puts a procedure revision in the set, or leaves it where it is.
    ///
    /// Adding one that is already there is not an error. Selection happens from several directions at once —
    /// what changed, an area somebody wants exercised, a defect that needs a retest — and those overlap by
    /// design. Refusing the second route would make a reader think the procedure had been left out.
    ///
    /// The first reason recorded is kept. It is the answer to "why did this get into the set", and the
    /// earliest one is the true answer; a later route arriving at the same procedure did not put it there.
    /// </summary>
    public bool Include(string actorId, Guid procedureRevisionId, TestSelectionReason reason, string note, DateTimeOffset now)
    {
        if (procedureRevisionId == Guid.Empty) throw new DomainException("A test set entry requires its procedure revision.");
        if (!Enum.IsDefined(reason)) throw new DomainException("A test set entry requires a known selection reason.");
        var actor = Required(actorId, "selecting engineer");
        var existing = _entries.SingleOrDefault(x => x.ProcedureRevisionId == procedureRevisionId);
        if (existing is not null)
        {
            // A discretionary selection can later become required because this build changed a requirement it
            // covers. Mandatory scope wins; otherwise a lead could remove it using its older "Chosen" reason.
            if (reason == TestSelectionReason.ChangedRequirement && existing.MakeMandatory(note)) Touch(now);
            return false;
        }
        _entries.Add(new BuildTestSetEntry(Id, procedureRevisionId, reason, note?.Trim() ?? "", actor, now));
        Touch(now);
        return true;
    }

    /// <summary>
    /// Takes a procedure back out.
    ///
    /// Deliberately unconditional on results: a procedure removed after it has been run keeps its execution,
    /// because the run happened and its record is evidence. What changes is whether the build is still
    /// measured against it.
    /// </summary>
    public bool Exclude(Guid procedureRevisionId, DateTimeOffset now)
    {
        var entry = _entries.SingleOrDefault(x => x.ProcedureRevisionId == procedureRevisionId);
        if (entry is null) return false;
        if (entry.Reason == TestSelectionReason.ChangedRequirement)
            throw new DomainException("A procedure covering a requirement changed by this build is mandatory before release.");
        _entries.Remove(entry);
        Touch(now);
        return true;
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; Version++; }
    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A {name} is required.") : value.Trim();
}

/// <summary>
/// One procedure a build has to run, and the record of how it came to be in the set.
///
/// The procedure *revision* rather than the procedure, because a build runs an exact approved revision and a
/// later revision is a different thing to have run. The note carries whatever the reason cannot: which area
/// was chosen, which problem report asked for the retest.
/// </summary>
public sealed class BuildTestSetEntry
{
    private BuildTestSetEntry() { }

    public BuildTestSetEntry(Guid buildTestSetId, Guid procedureRevisionId, TestSelectionReason reason,
        string note, string addedBy, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        BuildTestSetId = buildTestSetId;
        ProcedureRevisionId = procedureRevisionId;
        Reason = reason;
        Note = note;
        AddedBy = addedBy;
        AddedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid BuildTestSetId { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
    public TestSelectionReason Reason { get; private set; }
    public string Note { get; private set; } = "";
    public string AddedBy { get; private set; } = "";
    public DateTimeOffset AddedAt { get; private set; }

    internal bool MakeMandatory(string note)
    {
        if (Reason == TestSelectionReason.ChangedRequirement) return false;
        Reason = TestSelectionReason.ChangedRequirement;
        Note = note?.Trim() ?? "";
        return true;
    }
}
