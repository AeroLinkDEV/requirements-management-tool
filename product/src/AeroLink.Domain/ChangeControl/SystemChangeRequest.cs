using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.ChangeControl;

public enum ChangeRequestState { Draft, InReview, Approved, Deferred, SelectedForBaseline, Withdrawn }
public enum ChangeRequestType { System, Software, Interface }

/// <summary>
/// Which identifier prefix a change request carries, decided by what it is allowed to change.
///
/// This is the single authority: the allocator asks it what to number a new record, and the data migration
/// that renamed the existing ones asked it the same question. A change request that could not answer it
/// would be a controlled record with no name, which is why a software change request must declare its level
/// before it exists rather than acquiring one later.
/// </summary>
public static class ChangeRequestNumbering
{
    public const string SystemPrefix = "SRCR";
    public const string HighLevelPrefix = "HLRCR";
    public const string LowLevelPrefix = "LLRCR";
    public const string InterfacePrefix = "ICDCR";

    public static string Prefix(ChangeRequestType type, RequirementLevel? softwareLevel) =>
        LegacyLadderPolicy.Instance.ChangeRequestPrefix(type, softwareLevel);
}

public sealed class SystemChangeRequest
{
    public const int CurrentSnapshotContractVersion = 3;
    private readonly List<RequirementChange> _requirementChanges = [];
    private readonly List<ReviewCycle> _reviewCycles = [];
    private readonly List<AuditEvent> _auditEvents = [];
    private readonly List<ChangeRequestUpstreamLink> _upstreamLinks = [];
    private readonly List<ChangeRequestUpstreamHistory> _upstreamHistory = [];
    // Not persisted. Only the clean historical showcase seed may use this one-shot compatibility path;
    // migrated v1 drafts re-entering review must upgrade to the current exact-parent contract.
    private bool _allowLegacyHistoricalSubmission;
    private SystemChangeRequest() { }

    public SystemChangeRequest(string baseNumber, int revision, Guid projectId, Guid targetReleaseId,
        string title, string problem, string analysis, string solution, string authorId, DateTimeOffset now,
        ChangeRequestType type = ChangeRequestType.System,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null,
        RequirementLevel? softwareLevel = null, ILadderPolicy? ladderPolicy = null)
    {
        var policy = ladderPolicy ?? LegacyLadderPolicy.Instance;
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A change request title is required.");
        Id = Guid.NewGuid();
        BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Revision = revision;
        ProjectId = projectId;
        TargetReleaseId = targetReleaseId;
        OriginReleaseId = targetReleaseId;
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
        AuthorId = authorId;
        Type = type;
        // Every change-request classification has one exact scope binding. In particular, Interface change
        // requests are not Software requests with an empty level: their own prefix and review subject are
        // persisted as a distinct controlled classification.
        if (!policy.IsChangeRequestScopeValid(type, softwareLevel))
            throw new DomainException(type switch
            {
                ChangeRequestType.System => "A System change request cannot declare a software requirement level.",
                ChangeRequestType.Software => "A software change request must declare HLR or LLR scope.",
                ChangeRequestType.Interface => "An Interface change request cannot declare a software requirement level.",
                _ => $"The {type} change request has an invalid ladder scope.",
            });
        if (policy.ChangeRequestPrefix(type, softwareLevel) is var expected
            && !BaseNumber.StartsWith(expected + "-", StringComparison.Ordinal))
            throw new DomainException($"A {(type switch
            {
                ChangeRequestType.System => "System",
                ChangeRequestType.Interface => "Interface",
                _ => expected == ChangeRequestNumbering.HighLevelPrefix ? "HLR" : "LLR",
            })} change request must be numbered {expected}-.");
        SoftwareLevel = softwareLevel;
        State = ChangeRequestState.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Audit("ScrCreated", authorId, $"Created {DisplayNumber}.", now);
    }

    public Guid Id { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public string DisplayNumber => ArtifactNumber.Display(BaseNumber, Revision);
    public Guid ProjectId { get; private set; }
    /// <summary>
    /// The build this change request was raised in, which never changes.
    ///
    /// TargetReleaseId is where the work is going and moves with it. Once a change request raised in 1.6 is
    /// reinstated into 1.7, nothing on the record would otherwise say it began in 1.6, and it is no longer
    /// Deferred, so it would vanish from 1.6 entirely — a reader there would see work that simply disappeared.
    /// The move is in the audit trail, but a build listing cannot be driven off audit text.
    /// </summary>
    public Guid OriginReleaseId { get; private set; }

    public Guid TargetReleaseId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    /// <summary>
    /// How far this change request had got when it was deferred, or null when it is not on the shelf.
    ///
    /// State and allocation are two different facts and `ChangeRequestState` was carrying both. "Which build is this going
    /// into" and "how far has it got" are answered separately now: Deferred is where the work sits, and this is
    /// how far it got before it went there.
    /// </summary>
    public ChangeRequestState? DeferredFromState { get; private set; }

    /// <summary>How far it had got when it was taken back, so the record says what was abandoned.</summary>
    public ChangeRequestState? WithdrawnFromState { get; private set; }

    /// <summary>
    /// Why this change request has to be re-pointed before it means anything, or null when nothing is owed.
    ///
    /// Set when a build is reopened underneath it: the revision its author wrote against was taken back, so
    /// the wording they were changing no longer exists and the revision they numbered onto is not the next
    /// one any more. The audit trail records the same fact, but a work list cannot be driven off audit text,
    /// and a change request that silently means nothing is worse than one that says so.
    /// </summary>
    public string? RebaseRequiredReason { get; private set; }
    public string Problem { get; private set; } = string.Empty;
    public string Analysis { get; private set; } = string.Empty;
    public string Solution { get; private set; } = string.Empty;

    // The rich forms are what the author wrote; the plain forms above are their readable projection, derived
    // here rather than supplied, so the two can never disagree about what the change case says. Everything
    // that predates rich authoring — and every consumer that cannot render structure — keeps reading the
    // plain form and is unaffected.
    public string ProblemRich { get; private set; } = Content.RichContent.Empty;
    public string AnalysisRich { get; private set; } = Content.RichContent.Empty;
    public string SolutionRich { get; private set; } = Content.RichContent.Empty;
    public string AuthorId { get; private set; } = string.Empty;
    public ChangeRequestType Type { get; private set; }
    /// <summary>
    /// The HLR or LLR workspace in which an empty Software Draft was started. Once proposals exist their
    /// controlled levels remain authoritative, but this field keeps a case-only Draft discoverable instead
    /// of making it disappear from both engineering work lists.
    /// </summary>
    public RequirementLevel? SoftwareLevel { get; private set; }
    public ChangeRequestState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;
    /// <summary>Version of the canonical signed snapshot bytes; legacy rows remain on version 1.</summary>
    public int SnapshotContractVersion { get; private set; } = CurrentSnapshotContractVersion;
    public string? NoUpstreamRationale { get; private set; }
    public string? NoUpstreamStatedBy { get; private set; }
    public DateTimeOffset? NoUpstreamStatedAt { get; private set; }
    /// <summary>Serialized predecessor answer shown as inherited context until the successor author acts.</summary>
    public string? InheritedUpstreamContextJson { get; private set; }
    public Guid? InheritedFromChangeRequestId { get; private set; }
    public DateTimeOffset? InheritedAt { get; private set; }
    public bool UpstreamAnswerAffirmed { get; private set; }
    public string? UpstreamAnswerAffirmedBy { get; private set; }
    public DateTimeOffset? UpstreamAnswerAffirmedAt { get; private set; }
    public IReadOnlyCollection<RequirementChange> RequirementChanges => _requirementChanges.AsReadOnly();
    public IReadOnlyCollection<ReviewCycle> ReviewCycles => _reviewCycles.AsReadOnly();
    public IReadOnlyCollection<AuditEvent> AuditEvents => _auditEvents.AsReadOnly();
    public IReadOnlyCollection<ChangeRequestUpstreamLink> UpstreamLinks => _upstreamLinks.AsReadOnly();
    public IReadOnlyCollection<ChangeRequestUpstreamHistory> UpstreamHistory => _upstreamHistory.AsReadOnly();
    public ReviewCycle? ActiveReviewCycle => _reviewCycles.LastOrDefault(x => x.State == ReviewCycleState.Active);

    public void AddUpstreamLink(string actorId, Guid upstreamChangeRequestId, string upstreamDisplayNumber,
        Guid upstreamBuildId, string upstreamBuildVersion, string rationale, DateTimeOffset now,
        bool replaceNoUpstream = false, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        if (_upstreamLinks.Any(x => x.UpstreamChangeRequestId == upstreamChangeRequestId))
            throw new DomainException("That upstream change request is already linked.");
        if (NoUpstreamRationale is not null && !replaceNoUpstream)
            throw new DomainException("Remove the authored no-upstream answer before naming an upstream change request.");
        if (replaceNoUpstream)
        {
            RecordNoUpstreamHistory("NoUpstreamReplaced", actorId, now);
            NoUpstreamRationale = null; NoUpstreamStatedBy = null; NoUpstreamStatedAt = null;
        }
        var link = new ChangeRequestUpstreamLink(Id, upstreamChangeRequestId, upstreamDisplayNumber,
            upstreamBuildId, upstreamBuildVersion, rationale, actorId, now);
        _upstreamLinks.Add(link);
        InheritedUpstreamContextJson = null; InheritedFromChangeRequestId = null; InheritedAt = null;
        UpstreamAnswerAffirmed = true; UpstreamAnswerAffirmedBy = actorId; UpstreamAnswerAffirmedAt = now;
        _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, "Added", link.Id, link.UpstreamChangeRequestId,
            link.UpstreamDisplayNumber, link.UpstreamBuildId, link.UpstreamBuildVersion, link.Rationale, actorId, now));
        SnapshotContractVersion = CurrentSnapshotContractVersion;
        UpdatedAt = now; Version++;
        Audit("UpstreamChangeRequestLinked", actorId, $"Linked exact upstream {link.UpstreamDisplayNumber}.", now);
    }

    public void RemoveUpstreamLink(string actorId, Guid linkId, string reason, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority); EnsureDraft();
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to remove an upstream link.");
        var link = _upstreamLinks.SingleOrDefault(x => x.Id == linkId)
            ?? throw new DomainException("That upstream link is not part of this change request.");
        _upstreamLinks.Remove(link);
        _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, "Removed", link.Id, link.UpstreamChangeRequestId,
            link.UpstreamDisplayNumber, link.UpstreamBuildId, link.UpstreamBuildVersion, reason, actorId, now));
        SnapshotContractVersion = CurrentSnapshotContractVersion; UpdatedAt = now; Version++;
        Audit("UpstreamChangeRequestUnlinked", actorId, $"Removed exact upstream {link.UpstreamDisplayNumber}: {reason.Trim()}", now);
    }

    public void ChangeUpstreamLinkRationale(string actorId, Guid linkId, string rationale, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority); EnsureDraft();
        var link = _upstreamLinks.SingleOrDefault(x => x.Id == linkId)
            ?? throw new DomainException("That upstream link is not part of this change request.");
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("An upstream rationale is required.");
        // Link identity is immutable evidence. A rationale revision therefore replaces the current row and
        // appends history, rather than mutating the signed identity a review may already have captured.
        _upstreamLinks.Remove(link);
        var replacement = new ChangeRequestUpstreamLink(Id, link.UpstreamChangeRequestId,
            link.UpstreamDisplayNumber, link.UpstreamBuildId, link.UpstreamBuildVersion,
            rationale, actorId, now);
        _upstreamLinks.Add(replacement);
        _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, "Changed", link.Id, link.UpstreamChangeRequestId,
            link.UpstreamDisplayNumber, link.UpstreamBuildId, link.UpstreamBuildVersion, replacement.Rationale, actorId, now));
        SnapshotContractVersion = CurrentSnapshotContractVersion; UpdatedAt = now; Version++;
        Audit("UpstreamChangeRequestRationaleChanged", actorId, $"Changed the rationale for exact upstream {link.UpstreamDisplayNumber}.", now);
    }

    public void SetNoUpstreamRationale(string actorId, string rationale, DateTimeOffset now,
        bool replaceNamedLinks = false, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority); EnsureDraft();
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("A no-upstream rationale is required.");
        if (_upstreamLinks.Count != 0 && !replaceNamedLinks)
            throw new DomainException("Remove the named upstream change requests before stating that no upstream applies.");
        if (_upstreamLinks.Count != 0)
        {
            foreach (var link in _upstreamLinks)
                _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, "NoUpstreamReplaced", link.Id, link.UpstreamChangeRequestId,
                    link.UpstreamDisplayNumber, link.UpstreamBuildId, link.UpstreamBuildVersion, rationale, actorId, now));
            _upstreamLinks.Clear();
        }
        NoUpstreamRationale = rationale.Trim(); NoUpstreamStatedBy = actorId; NoUpstreamStatedAt = now;
        InheritedUpstreamContextJson = null; InheritedFromChangeRequestId = null; InheritedAt = null;
        UpstreamAnswerAffirmed = true; UpstreamAnswerAffirmedBy = actorId; UpstreamAnswerAffirmedAt = now;
        SnapshotContractVersion = CurrentSnapshotContractVersion; UpdatedAt = now; Version++;
        Audit("NoUpstreamAnswerRecorded", actorId, $"Recorded no-upstream rationale: {NoUpstreamRationale}", now);
    }

    public void ClearNoUpstreamRationale(string actorId, string reason, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority); EnsureDraft();
        if (NoUpstreamRationale is null) throw new DomainException("This change request has no authored no-upstream answer.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to change the no-upstream answer.");
        RecordNoUpstreamHistory("Removed", actorId, now, reason);
        NoUpstreamRationale = null; NoUpstreamStatedBy = null; NoUpstreamStatedAt = null;
        UpstreamAnswerAffirmed = false; UpstreamAnswerAffirmedBy = null; UpstreamAnswerAffirmedAt = null;
        SnapshotContractVersion = CurrentSnapshotContractVersion; UpdatedAt = now; Version++;
        Audit("NoUpstreamAnswerRemoved", actorId, reason.Trim(), now);
    }

    public void AffirmInheritedUpstreamAnswer(string actorId, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority); EnsureDraft();
        if (string.IsNullOrWhiteSpace(InheritedUpstreamContextJson))
            throw new DomainException("There is no inherited upstream answer to affirm.");
        if (UpstreamAnswerAffirmed) throw new DomainException("The inherited upstream answer is already affirmed.");
        try
        {
            using var document = JsonDocument.Parse(InheritedUpstreamContextJson);
            if (TryGetJsonProperty(document.RootElement, "links", "Links", out var links))
                foreach (var item in links.EnumerateArray())
                {
                    var upstreamChangeRequestId = GetJsonProperty(item, "upstreamChangeRequestId", "UpstreamChangeRequestId").GetGuid();
                    var upstreamDisplayNumber = GetJsonProperty(item, "upstreamDisplayNumber", "UpstreamDisplayNumber").GetString() ?? "";
                    var upstreamBuildId = GetJsonProperty(item, "upstreamBuildId", "UpstreamBuildId").GetGuid();
                    var upstreamBuildVersion = GetJsonProperty(item, "upstreamBuildVersion", "UpstreamBuildVersion").GetString() ?? "";
                    var rationale = GetJsonProperty(item, "rationale", "Rationale").GetString() ?? "";
                    var link = new ChangeRequestUpstreamLink(Id,
                        upstreamChangeRequestId, upstreamDisplayNumber, upstreamBuildId, upstreamBuildVersion,
                        rationale, actorId, now);
                    _upstreamLinks.Add(link);
                    _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, "InheritedAffirmed", link.Id,
                        link.UpstreamChangeRequestId, link.UpstreamDisplayNumber, link.UpstreamBuildId,
                        link.UpstreamBuildVersion, link.Rationale, actorId, now));
                }
            if (TryGetJsonProperty(document.RootElement, "noUpstreamRationale", "NoUpstreamRationale", out var noUpstream)
                && noUpstream.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(noUpstream.GetString()))
            {
                NoUpstreamRationale = noUpstream.GetString()!.Trim();
                NoUpstreamStatedBy = actorId;
                NoUpstreamStatedAt = now;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
        {
            throw new DomainException("The inherited upstream answer is malformed and cannot be affirmed.");
        }
        UpstreamAnswerAffirmed = true; UpstreamAnswerAffirmedBy = actorId; UpstreamAnswerAffirmedAt = now;
        SnapshotContractVersion = CurrentSnapshotContractVersion; UpdatedAt = now; Version++;
        Audit("InheritedUpstreamAnswerAffirmed", actorId, "Affirmed the predecessor upstream answer for this revision.", now);
    }

    private static bool TryGetJsonProperty(JsonElement element, string camelName, string pascalName, out JsonElement value) =>
        element.TryGetProperty(camelName, out value) || element.TryGetProperty(pascalName, out value);

    private static JsonElement GetJsonProperty(JsonElement element, string camelName, string pascalName) =>
        TryGetJsonProperty(element, camelName, pascalName, out var value)
            ? value
            : throw new KeyNotFoundException($"Missing inherited upstream property '{camelName}'.");

    private void RecordNoUpstreamHistory(string action, string actorId, DateTimeOffset now, string? rationale = null) =>
        _upstreamHistory.Add(new ChangeRequestUpstreamHistory(Id, action, null, null, "", null, "", rationale ?? NoUpstreamRationale ?? "", actorId, now));

    /// <summary>
    /// System change requests govern System requirements. Software change requests govern HLRs and LLRs.
    /// Keeping the rule beside the aggregate prevents imports, integrations, seed reconciliation, and future
    /// endpoints from creating a request which looks valid until its downstream assessment is raised.
    /// </summary>
    public static bool AcceptsRequirementLevel(ChangeRequestType type, RequirementLevel level) =>
        LegacyLadderPolicy.Instance.AcceptsChangeRequest(type, level);

    public RequirementChange AddRequirementChange(string actorId, string baseNumber, int revision,
        RequirementLevel level, RequirementChangeKind kind, string statement, string rationale,
        string verificationMethod, DateTimeOffset now, string richText = "", string attributesJson = "{}",
        string impactDispositionJson = RequirementAuthoringJson.CompleteImpactDispositions,
        Guid? targetSectionId = null, bool administratorAuthority = false,
        string proposedUpstreamRevisionIdsJson = "[]", bool allowIncomplete = false, ILadderPolicy? ladderPolicy = null)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        EnsureRequirementLevel(level, ladderPolicy);
        // Complete by default, because every other caller is a request being submitted rather than work being
        // put down: seven API endpoints build a change request from a payload, and a payload that omits the
        // statement is malformed rather than unfinished.
        //
        // `allowIncomplete` is passed only by the controlled-editing check-in path, where the author is
        // parking a working copy mid-sentence. ValidateReadyForReview refuses whatever is still unfinished
        // when the record is offered to an approver, so the relaxation cannot escape the Draft.
        if (!allowIncomplete && string.IsNullOrWhiteSpace(statement) && kind != RequirementChangeKind.Retire)
            throw new DomainException("A requirement statement is required.");
        var change = new RequirementChange(Id, baseNumber, revision, level, kind, statement, rationale, verificationMethod,
            richText, attributesJson, impactDispositionJson, targetSectionId, proposedUpstreamRevisionIdsJson);
        _requirementChanges.Add(change);
        SnapshotContractVersion = CurrentSnapshotContractVersion;
        // Requirement proposals are aggregate mutations, including when an existing Draft is extended from
        // the Requirements Explorer. Advancing the optimistic-concurrency version here makes two concurrent
        // proposal additions a real one-winner operation instead of allowing both callers to pass the same
        // expected version and silently overwrite each other's intent.
        UpdatedAt = now;
        Version++;
        Audit("RequirementChangeAdded", actorId,
            $"Added {change.Kind} {(string.IsNullOrWhiteSpace(change.DisplayNumber) ? "for a requirement not yet chosen" : change.DisplayNumber)}" +
            (RequirementAuthoringJson.IsDerived(change.AttributesJson) ? " as a derived requirement with a documented rationale." : "."), now);
        return change;
    }

    /// <summary>
    /// Explicit compatibility seam for the pre-#738 showcase/import history. It is intentionally opt-in:
    /// ordinary authored packages remain on the current snapshot contract and must resolve every configured
    /// non-root requirement at review submission. The materializer carries this marker through as legacy
    /// Unspecified revision evidence rather than inventing an allocation or derived rationale.
    /// </summary>
    internal void MarkAsLegacyHistoricalPackage(string actorId, DateTimeOffset now)
    {
        EnsureAuthor(actorId, administratorAuthority: false);
        EnsureDraft();
        if (_requirementChanges.Count == 0)
            throw new DomainException("A legacy historical package must contain at least one requirement change.");
        SnapshotContractVersion = 1;
        _allowLegacyHistoricalSubmission = true;
        UpdatedAt = now;
        Audit("LegacyHistoricalPackageMarked", actorId,
            $"Preserved {DisplayNumber} as pre-exact-parent historical evidence.", now);
    }

    /// <summary>
    /// Moves one requirement change onto the result of another change request that reached the requirement
    /// first, carrying wording the author has re-applied against the new text.
    ///
    /// The alternative to this is losing the work. A change request refused at submission can drop the
    /// contested requirement or wait; neither keeps an analysis that is still valid and only disagrees with
    /// the winner about text.
    ///
    /// The caller establishes that the winner is approved and that its change is a modification rather than a
    /// retirement — both are facts about a different aggregate, and asking this one to know them would mean
    /// handing it a repository. What this owns is what happens to the change request itself: the revision
    /// moves, the author's re-applied statement replaces theirs, and an approval given against the earlier
    /// text does not survive.
    /// </summary>
    public void RebaseRequirementChange(string actorId, Guid requirementChangeId, int ontoRevision,
        string reappliedStatement, string ontoDisplayNumber, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State is not (ChangeRequestState.Draft or ChangeRequestState.Approved))
            throw new DomainException("Only a draft or approved change request can be rebased.");
        var change = _requirementChanges.SingleOrDefault(x => x.Id == requirementChangeId)
            ?? throw new DomainException("That requirement change is not part of this change request.");

        var from = change.Revision;
        change.Rebase(ontoRevision, reappliedStatement);
        // Re-pointing it is exactly what a reopen asked for, so the flag has been answered.
        RebaseRequiredReason = null;

        // An approval describes wording. Once the wording moves, the signatures describe something that is no
        // longer proposed, so the change request goes back for review rather than carrying them forward. The
        // same rule applies when a change request moves between builds.
        var wasApproved = State == ChangeRequestState.Approved;
        if (wasApproved) State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("RequirementChangeRebased", actorId,
            $"Rebased {change.DisplayNumber} from revision {from} onto revision {ontoRevision}, the result of {ontoDisplayNumber}."
            + (wasApproved ? " Returned to Draft; the approvals described the earlier wording." : string.Empty), now);
    }

    /// <summary>
    /// Takes a change request back, keeping the record of it.
    ///
    /// Work is abandoned for ordinary reasons -- the problem turns out not to exist, the approach is wrong, it
    /// is superseded. Until now the only options were to defer it, which says "later" rather than "never", or
    /// to leave it in the register misrepresenting the plan.
    ///
    /// Withdrawn, not deleted. A change request that has been in front of reviewers has signatures against it,
    /// and removing the evidence that an approval happened is worse than the problem it solves. Somebody
    /// looking for SRCR-00110 should find that it was approved and then withdrawn, by whom and why, rather
    /// than finding nothing. Deleting outright is reserved for a draft nobody has ever reviewed, where there
    /// is no decision to be accountable for.
    ///
    /// Nothing is unwound here, and that is not an omission. Approving a change request does not move the
    /// requirement: the revision is created when a baseline is frozen and materialized. So a change request
    /// withdrawn before its baseline is frozen has produced no revision to take back, and one whose baseline
    /// has been frozen cannot be withdrawn at all until that baseline is reopened -- which is a deliberate act
    /// of its own rather than a silent consequence of somebody withdrawing their work.
    /// </summary>
    public void Withdraw(string actorId, string reason, DateTimeOffset now, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State == ChangeRequestState.Withdrawn) throw new DomainException("The change request is already withdrawn.");
        if (State == ChangeRequestState.SelectedForBaseline)
            throw new DomainException("Remove the change request from its candidate baseline before withdrawing it.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A withdrawal reason is required.");

        // The approvers were asked about work that is being taken away. Leaving the cycle open would leave
        // signatures outstanding against a package nobody intends to ship.
        if (State == ChangeRequestState.InReview) ActiveReviewCycle?.Cancel(reason.Trim(), now);

        var from = State;
        State = ChangeRequestState.Withdrawn;
        WithdrawnFromState = from;
        UpdatedAt = now;
        Audit("ChangeRequestWithdrawn", actorId,
            $"Withdrawn from {from}: {reason.Trim()}", now);
    }

    /// <summary>
    /// Tells a change request that the ground moved under it when a build was reopened.
    ///
    /// Reopening takes back the revisions a build materialized. Anything written against one of them is left
    /// pointing at wording that no longer exists: a modification numbered onto revision 03 when the
    /// requirement is back at revision 01, or a change to a requirement the reopen removed altogether. Neither
    /// is wrong to have written, and neither is something this system should silently re-point -- the author
    /// wrote their words against text they read, and moving them onto different text would assert they read
    /// something they never saw. So it is flagged and left for them.
    ///
    /// A review in flight is cancelled rather than left standing. The approvers were asked about a change
    /// against a revision that has since been taken back, so their signatures would describe a comparison
    /// nobody can now make. This is the same reasoning `Reinstate` uses when it refuses to restore `InReview`.
    /// </summary>
    public void StrandByReopenedBaseline(string actorId, string baselineDisplayNumber,
        IReadOnlyList<string> requirements, DateTimeOffset now)
    {
        if (State is not (ChangeRequestState.Draft or ChangeRequestState.InReview))
            throw new DomainException("Only a draft or in-review change request is stranded by a reopened baseline.");
        if (string.IsNullOrWhiteSpace(baselineDisplayNumber))
            throw new DomainException("The baseline that was reopened must be named.");
        if (requirements.Count == 0)
            throw new DomainException("Stranding a change request must name what it was left pointing at.");

        var subjects = string.Join(", ", requirements);
        var wasInReview = State == ChangeRequestState.InReview;
        if (wasInReview)
        {
            ActiveReviewCycle?.Cancel($"{baselineDisplayNumber} was reopened and the revisions this was written against were taken back.", now);
            State = ChangeRequestState.Draft;
        }

        RebaseRequiredReason =
            $"{baselineDisplayNumber} was reopened and took back the revision this was written against. "
            + $"Re-point {subjects} onto what the requirement says now.";
        UpdatedAt = now;
        Audit("ChangeRequestStrandedByReopen", actorId,
            $"{baselineDisplayNumber} was reopened, taking back the revisions {subjects} were written against."
            + (wasInReview ? " The review was cancelled and it returned to Draft; the approvers were asked about wording that no longer exists." : string.Empty), now);
    }

    /// <summary>
    /// Takes a requirement change back off a draft.
    ///
    /// An author who added the wrong requirement, or whose analysis concluded a requirement should not change
    /// after all, previously had to abandon the whole change request and start again — losing the problem
    /// statement, the analysis, and any review comments already written against it. It is also the remedy for
    /// a change request refused at submission because another one holds the requirement: drop the contested
    /// one and send the rest.
    ///
    /// Draft only. A package in front of reviewers must not change under the people reading it, and a package
    /// that has been approved says what was approved.
    ///
    /// Removing the last one is allowed. A change request with nothing in it is a legitimate intermediate
    /// state while an author reconsiders; it is submission that requires at least one, and
    /// <see cref="ValidateReadyForReview"/> already refuses that.
    /// </summary>
    public void RemoveRequirementChange(string actorId, Guid requirementChangeId, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        var change = _requirementChanges.SingleOrDefault(x => x.Id == requirementChangeId)
            ?? throw new DomainException("That requirement change is not part of this change request.");
        _requirementChanges.Remove(change);
        SnapshotContractVersion = CurrentSnapshotContractVersion;
        UpdatedAt = now;
        Audit("RequirementChangeRemoved", actorId,
            $"Removed {change.DisplayNumber} from {DisplayNumber}.", now);
    }

    public void UpdateDraft(string actorId, string title, string problem, string analysis, string solution,
        IReadOnlyList<RequirementChangeDraft> changes, DateTimeOffset now,
        string? problemRich = null, string? analysisRich = null, string? solutionRich = null,
        bool administratorAuthority = false, bool allowIncomplete = false, ILadderPolicy? ladderPolicy = null)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A change request title is required.");
        foreach (var item in changes) EnsureRequirementLevel(item.Level, ladderPolicy);
        Title = title.Trim();
        SetCase(problem, analysis, solution, problemRich, analysisRich, solutionRich);
        _requirementChanges.Clear();
        foreach (var item in changes)
        {
            // Complete by default; unfinished only where the caller says so. See AddRequirementChange.
            if (!allowIncomplete && string.IsNullOrWhiteSpace(item.Statement) && item.Kind != RequirementChangeKind.Retire)
                throw new DomainException("A requirement statement is required unless the requirement is being retired.");
            _requirementChanges.Add(new RequirementChange(Id, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, item.RichText, item.AttributesJson, item.ImpactDispositionJson,
                item.TargetSectionId, item.ProposedUpstreamRevisionIdsJson));
        }
        SnapshotContractVersion = CurrentSnapshotContractVersion;
        UpdatedAt = now;
        var derivedCount = _requirementChanges.Count(x => RequirementAuthoringJson.IsDerived(x.AttributesJson));
        Audit("ScrDraftUpdated", actorId,
            $"Updated {DisplayNumber} Draft with {changes.Count} proposed requirement changes and {derivedCount} documented derived exception(s).", now);
    }

    public ReviewCycle SubmitForReview(string actorId, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null,
        bool administratorAuthority = false, ILadderPolicy? ladderPolicy = null,
        VerificationMethodPolicy? verificationPolicy = null,
        ChangeRequestTraceReviewEvidence? traceEvidence = null)
        => SubmitForReviewCore(actorId, approvers, now, mode, workflow, administratorAuthority,
            ladderPolicy, verificationPolicy, traceEvidence, allowUnresolvedTraceSeed: true);

    /// <summary>
    /// Authoritative review boundary used by the API after it resolves the effective ladder and assessment
    /// evidence. Unlike the legacy/seed compatibility overload, this path fails closed when evidence is absent.
    /// </summary>
    public ReviewCycle SubmitForReviewWithResolvedTrace(string actorId, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode = ReviewMode.Sequential, ReviewWorkflowSpecification? workflow = null,
        bool administratorAuthority = false, ILadderPolicy? ladderPolicy = null,
        VerificationMethodPolicy? verificationPolicy = null,
        ChangeRequestTraceReviewEvidence? traceEvidence = null)
        => SubmitForReviewCore(actorId, approvers, now, mode, workflow, administratorAuthority,
            ladderPolicy, verificationPolicy, traceEvidence, allowUnresolvedTraceSeed: false);

    private ReviewCycle SubmitForReviewCore(string actorId, IReadOnlyList<ApproverSelection> approvers,
        DateTimeOffset now, ReviewMode mode, ReviewWorkflowSpecification? workflow,
        bool administratorAuthority, ILadderPolicy? ladderPolicy,
        VerificationMethodPolicy? verificationPolicy, ChangeRequestTraceReviewEvidence? traceEvidence,
        bool allowUnresolvedTraceSeed)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureDraft();
        // Before anything mutates. A refused submission must leave the package byte-identical — same
        // snapshot contract version, same review cycles, same declared methods — because an author whose
        // vocabulary check failed still has the draft they had a moment ago.
        ValidateVerificationVocabulary(verificationPolicy, ladderPolicy);
        // Existing review cycles keep their own v1/v2 byte contract. A live Draft entering the current
        // authoritative boundary must, however, freeze its new trace facts under v3. The old overload is an
        // explicit seed/history seam and is the only path allowed to preserve an older contract for a new
        // cycle; the API always uses SubmitForReviewWithResolvedTrace.
        if (SnapshotContractVersion < CurrentSnapshotContractVersion && !allowUnresolvedTraceSeed
            && !_allowLegacyHistoricalSubmission)
            SnapshotContractVersion = CurrentSnapshotContractVersion;
        ValidateReadyForReview(ladderPolicy, traceEvidence, allowUnresolvedTraceSeed);
        var cycle = new ReviewCycle(Id, _reviewCycles.Count + 1, ComputeSnapshotHash(traceEvidence), approvers, now, mode, workflow,
            SnapshotContractVersion, BuildTraceSnapshotJson(traceEvidence));
        _reviewCycles.Add(cycle);
        _allowLegacyHistoricalSubmission = false;
        // Offering it to approvers is the author saying they have dealt with whatever a reopen left them, so
        // the flag comes off here rather than lingering into a review it no longer describes. What happened is
        // still in the audit trail, which is where a reviewer asking "why was this returned" looks.
        RebaseRequiredReason = null;
        State = ChangeRequestState.InReview;
        UpdatedAt = now;
        Audit("ReviewStarted", actorId,
            $"Started {cycle.Mode.ToString().ToLowerInvariant()} review cycle {cycle.Sequence} with {approvers.Count} approvers" +
            (workflow is null ? "." : $" following {workflow.Name} v{workflow.Version}."), now);
        return cycle;
    }

    /// <summary>
    /// Records a reviewer's remark about one part of the package under review, as a draft only they can see.
    ///
    /// Deliberately not audited. The audit trail records controlled events — who signed what, and why a
    /// package was returned — and a reviewer typing a note is neither. Publishing one is not audited either:
    /// the decision that published it already is.
    /// </summary>
    public ReviewComment AddReviewComment(string actorId, ReviewCommentAnchor anchor, Guid? requirementChangeId,
        string body, DateTimeOffset now)
    {
        EnsureInReview();
        // A comment must name a revision that is actually in the package being reviewed. Without this a
        // reviewer could anchor to a revision from a different change request and the author would open a
        // comment about something they never submitted.
        if (requirementChangeId is not null && _requirementChanges.All(x => x.Id != requirementChangeId))
            throw new DomainException("That requirement revision is not in this package.");
        return ActiveReviewCycle!.AddComment(actorId, anchor, requirementChangeId, body, now);
    }

    public void ReviseReviewComment(string actorId, Guid commentId, string body, DateTimeOffset now)
    {
        EnsureInReview();
        ActiveReviewCycle!.ReviseComment(commentId, actorId, body, now);
    }

    public void RemoveReviewComment(string actorId, Guid commentId)
    {
        EnsureInReview();
        ActiveReviewCycle!.RemoveComment(commentId, actorId);
    }

    public void ApproveActiveStage(string actorId, DateTimeOffset now, string? rationale = null)
    {
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var fullyApproved = cycle.Approve(actorId, rationale, now);
        Audit("ApprovalRecorded", actorId, $"Approved review cycle {cycle.Sequence} stage." + (string.IsNullOrWhiteSpace(rationale) ? "" : $" Reason: {rationale.Trim()}"), now);
        if (fullyApproved)
        {
            State = ChangeRequestState.Approved;
            Audit("ScrApproved", actorId, $"Unanimously approved {DisplayNumber}.", now);
        }
        UpdatedAt = now;
    }

    public void RequestChanges(string actorId, string reason, DateTimeOffset now)
    {
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var active = cycle.Steps.SingleOrDefault(x => x.State == ApprovalStepState.Active && string.Equals(x.ApproverId, actorId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new DomainException("Only the active approver can request changes.");
        cycle.ReturnActiveStep(actorId, reason, now);
        State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("ChangesRequested", actorId, $"Returned {DisplayNumber} to Draft at the same revision: {reason}", now);
    }

    /// <summary>
    /// Takes a change request out of review and puts it back in Draft, at the same revision.
    ///
    /// `RequestChanges` already did something close to this, and only the reviewer whose turn it was could do
    /// it — which is the wrong shape for the common case. An author who submitted too early, or a lead who can
    /// see the review is pointless because the change is being reworked, had no way to stop it. Their only
    /// options were to wait for a reviewer to reject work everybody already knew was going to change, or to
    /// ask that reviewer to do it for them.
    ///
    /// The two are kept apart deliberately. Requesting changes is a reviewer's decision about the content and
    /// is recorded as one; cancelling is a decision to stop the review itself, and reads that way in the
    /// history. Both land in Draft at the same revision, because neither is a rejection of the record.
    ///
    /// Who may do it is decided by the caller, which knows the actor's Program roles. What is settled here is
    /// that a reason is required: a review that stopped for no recorded reason leaves the next reader unable
    /// to tell whether it was withdrawn, superseded, or abandoned by accident.
    /// </summary>
    public void CancelReview(string actorId, string reason, DateTimeOffset now)
    {
        EnsureInReview();
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("A cancelling actor is required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why this review is being cancelled.");
        ActiveReviewCycle!.Cancel(reason, now);
        State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("ReviewCancelled", actorId, $"Cancelled the review of {DisplayNumber} and returned it to Draft at the same revision: {reason.Trim()}", now);
    }

    public void ReplaceFutureApprover(string actorId, int position, ApproverSelection replacement,
        DateTimeOffset now, ReviewWorkflowSpecification? workflow = null, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureInReview();
        var cycle = ActiveReviewCycle!;
        var previous = cycle.Steps.Single(x => x.Position == position).ApproverName;
        cycle.ReplaceFutureApprover(position, replacement, workflow);
        UpdatedAt = now;
        Audit("FutureApproverReplaced", actorId, $"Replaced position {position + 1}: {previous} -> {replacement.Name}.", now);
    }

    public ReviewCycle CancelAndRestartForWrongApprover(string actorId, string reason,
        IReadOnlyList<ApproverSelection> correctedApprovers, DateTimeOffset now,
        ReviewWorkflowSpecification? workflow = null, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        EnsureInReview();
        // Cancelling an in-flight review discards recorded approval authority, so it is exactly the kind of
        // act that must carry an attributable reason. Every other controlled action here demands one.
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to cancel and restart a review.");
        if (correctedApprovers is null || correctedApprovers.Count == 0)
            throw new DomainException("At least one corrected approver is required.");
        var prior = ActiveReviewCycle!;
        prior.Cancel(reason, now);
        var replacement = new ReviewCycle(Id, _reviewCycles.Count + 1, prior.SnapshotHash, correctedApprovers, now, prior.Mode, workflow,
            prior.SnapshotContractVersion, prior.SnapshotJson);
        _reviewCycles.Add(replacement);
        UpdatedAt = now;
        Audit("ReviewCancelledAndRestarted", actorId,
            $"Cancelled cycle {prior.Sequence} and restarted as cycle {replacement.Sequence}: {reason}", now);
        return replacement;
    }

    /// <summary>
    /// Supersedes this revision with the next one, carrying the same content forward as a Draft.
    ///
    /// Approved and SelectedForBaseline are the same fact to the person asking: the engineering is signed for.
    /// They differ only in whether a candidate baseline has picked the row up yet, and in a working programme
    /// every approved change request gets picked up — which is why requiring exactly `Approved` made this
    /// unreachable across a 113-record programme where not one change request sat in that state.
    ///
    /// What must not happen is revising a change request already incorporated in a *released* build. That
    /// content is frozen history, and a `.01` of it would claim the release said something it never said. The
    /// answer there is a new change request against the in-work build, so the caller passes the release's
    /// state in rather than the rule living outside the aggregate where a second caller could forget it.
    /// </summary>
    public SystemChangeRequest StartNextRevision(string actorId, DateTimeOffset now, bool targetReleaseIsReleased,
        bool administratorAuthority = false, ILadderPolicy? ladderPolicy = null)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline))
            throw new DomainException("Only an approved change request can advance to its next revision.");
        if (targetReleaseIsReleased)
            throw new DomainException(
                "This change request is incorporated in a released build and cannot be revised. Raise a new one against the in-work build.");
        var next = new SystemChangeRequest(BaseNumber, Revision + 1, ProjectId, TargetReleaseId,
            Title, Problem, Analysis, Solution, AuthorId, now, Type, ProblemRich, AnalysisRich, SolutionRich, SoftwareLevel, ladderPolicy);
        foreach (var item in _requirementChanges)
            next.AddRequirementChange(actorId, item.BaseNumber, item.Revision, item.Level, item.Kind,
                item.Statement, item.Rationale, item.VerificationMethod, now, item.RichText, item.AttributesJson, item.ImpactDispositionJson,
                item.TargetSectionId, administratorAuthority, item.ProposedUpstreamRevisionIdsJson,
                ladderPolicy: ladderPolicy);
        if (_upstreamLinks.Count != 0 || NoUpstreamRationale is not null)
        {
            next.InheritedUpstreamContextJson = JsonSerializer.Serialize(new
            {
                links = _upstreamLinks.OrderBy(x => x.UpstreamDisplayNumber).Select(x => new
                {
                    x.UpstreamChangeRequestId, x.UpstreamDisplayNumber, x.UpstreamBuildId,
                    x.UpstreamBuildVersion, x.Rationale
                }),
                noUpstreamRationale = NoUpstreamRationale,
                statedBy = NoUpstreamStatedBy,
                statedAt = NoUpstreamStatedAt,
            });
            next.InheritedFromChangeRequestId = Id;
            next.InheritedAt = now;
            next.UpstreamAnswerAffirmed = false;
            next.Audit("UpstreamAnswerInherited", actorId,
                $"Inherited the predecessor upstream answer from {DisplayNumber}; the successor author must affirm or change it before review.", now);
        }
        return next;
    }

    public void MarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ChangeRequestState.Approved) throw new DomainException("Only an approved change request can be selected for a baseline.");
        State = ChangeRequestState.SelectedForBaseline;
        UpdatedAt = now;
        // Says what happened to the change, not what happened to the baseline. Selection into a candidate
        // baseline is the mechanism; being allocated to a build is the fact a reader opened the history for.
        // The event type is unchanged, because it names an event that is already recorded thousands of times
        // and renaming it would make the old entries and the new ones look like different things.
        Audit("SelectedForBaseline", actorId, $"Allocated {DisplayNumber} to the build.", now);
    }

    public void UnmarkSelectedForBaseline(string actorId, DateTimeOffset now)
    {
        if (State != ChangeRequestState.SelectedForBaseline) throw new DomainException("The change request is not selected for a baseline.");
        State = ChangeRequestState.Approved;
        UpdatedAt = now;
        Audit("RemovedFromCandidateBaseline", actorId, $"Returned {DisplayNumber} to Approved eligibility.", now);
    }

    /// <summary>
    /// Puts the change request away for another day, from wherever it currently is.
    ///
    /// Deferral used to be reachable only from Draft and Approved, which left the middle of the lifecycle
    /// with nowhere to go: a change request under review that the programme had decided to drop had to be
    /// rejected — throwing away a review that raised no engineering objection — or left in review forever,
    /// holding a release gate that would never clear. Neither is what happened, so neither should be what the
    /// record says.
    ///
    /// From review, the cycle in flight is cancelled rather than abandoned, carrying the deferral reason as
    /// its closure. Approvals already given keep their decisions and their attribution; what they lose is
    /// force, because the revision they were given against is no longer heading for this release.
    ///
    /// Not reachable from SelectedForBaseline, and that is not an omission: a change request already chosen
    /// into a candidate baseline has to be taken out of it first, which is an explicit, attributable act with
    /// its own audit event rather than a side effect of deferring.
    /// </summary>
    public void Defer(string actorId, string reason, DateTimeOffset now, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State == ChangeRequestState.Deferred) throw new DomainException("The change request is already deferred.");
        if (State == ChangeRequestState.SelectedForBaseline)
            throw new DomainException("Remove the change request from its candidate baseline before deferring it.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A deferral reason is required.");
        if (State == ChangeRequestState.InReview) ActiveReviewCycle?.Cancel(reason.Trim(), now);
        // Remembered, because deferring changes where the work sits and not how far it got. A change request put
        // away while approved is still approved work; one put away as a Draft still needs writing. Storing only
        // "Deferred" loses that, and a shelf that cannot tell a signed-off change from an unwritten one is a
        // shelf nobody can plan from. Reinstate puts it back exactly where it was.
        DeferredFromState = State;
        State = ChangeRequestState.Deferred; UpdatedAt = now; Audit("ChangeRequestDeferred", actorId, reason.Trim(), now);
    }

    /// <summary>
    /// Takes a deferred change request off the shelf.
    ///
    /// It comes back as a Draft, whatever it was when it went away, and its approvals do not come with it.
    ///
    /// This used to restore the prior state exactly, on the reasoning that a change request put away while
    /// approved is still approved work. That is true of the work and false of the approval. Reviewers approved
    /// a change into a particular build, against that build's baseline and the requirement revisions current
    /// at the time; a deferred change request is reinstated into whichever build is open now, and carrying the
    /// signature across asserts something nobody was asked. The requirement it modifies may have moved on, and
    /// the build it lands in has different content.
    ///
    /// DeferredFromState is still recorded and still shown, because the shelf does need to say how far
    /// something got — a reader planning a build wants to know a change was written and reviewed, not only
    /// that it exists. It informs the reader rather than restoring the state.
    /// </summary>
    public void Reinstate(string actorId, DateTimeOffset now, bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State != ChangeRequestState.Deferred) throw new DomainException("Only a deferred change request can be reinstated.");
        var reached = DeferredFromState;
        State = ChangeRequestState.Draft;
        DeferredFromState = null;
        UpdatedAt = now;
        Audit("ChangeRequestReinstated", actorId,
            reached is null or ChangeRequestState.Draft
                ? $"Reinstated {DisplayNumber} as a Draft."
                : $"Reinstated {DisplayNumber} as a Draft; it had reached {reached} before deferral, and those approvals do not carry into a new build.",
            now);
    }

    public void Retarget(string actorId, Guid targetReleaseId, string reason, DateTimeOffset now,
        bool administratorAuthority = false)
    {
        EnsureAuthor(actorId, administratorAuthority);
        if (State is not (ChangeRequestState.Draft or ChangeRequestState.Approved or ChangeRequestState.Deferred))
            throw new DomainException("Only a Draft, Approved, or Deferred change request can move to another release.");
        if (targetReleaseId == Guid.Empty || targetReleaseId == TargetReleaseId)
            throw new DomainException("Choose a different target release.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A retarget rationale is required.");
        var prior = TargetReleaseId;
        TargetReleaseId = targetReleaseId;
        // Approval does not travel between builds. Reviewers approved this into a particular build, against
        // that build's baseline and the requirement revisions current at the time, and none of that is true
        // of the build it is moving into. Deferring and reinstating already returns a change request to
        // Draft; moving it directly must not be the privileged route that keeps a signature the other one
        // drops, or the two ways to the same place would carry different evidence.
        var wasApproved = State == ChangeRequestState.Approved;
        if (wasApproved) State = ChangeRequestState.Draft;
        UpdatedAt = now;
        Audit("TargetReleaseChanged", actorId,
            $"Moved {DisplayNumber} from release {prior} to {targetReleaseId}: {reason.Trim()}"
            + (wasApproved ? " Returned to Draft; approvals do not carry into another build." : string.Empty), now);
    }

    /// <summary>
    /// The #701 vocabulary boundary: no requirement crosses into review declaring a verification method the
    /// project does not permit.
    ///
    /// Enforced here because this is the authoritative transition. Before it, a draft is a working record an
    /// author is still writing and controlled editing deliberately tolerates half-finished fields; after it,
    /// approvers sign the package, generated documents render the declared method verbatim, and auditors
    /// filter on it. A value refused here is named rather than corrected: re-spelling "test" as "Test" would
    /// be the product deciding two engineering terms mean the same thing and rewriting the author's
    /// declaration on the way to a signature. Nothing about this method changes the draft.
    ///
    /// Which changes carry a method is answered by the effective ladder, not by naming levels here. A level
    /// without <see cref="LevelCapabilities.HasVerification"/> — Interface today — has no verification
    /// artifact at all and its changes carry the product's "Not applicable" sentinel; holding those to a
    /// method vocabulary would demand a verification declaration from the one requirement kind that has
    /// none. Retirements are exempt for the same reason they are exempt from needing a statement: nothing is
    /// being declared, only withdrawn.
    ///
    /// A null <paramref name="verificationPolicy"/> means no project vocabulary reached this seam — seeders
    /// and focused domain tests that construct aggregates directly. It disables the check rather than
    /// substituting a default set, deliberately: a conventional fallback would be a second source of truth
    /// for what a project permits, invisible to the configuration screen and to the reconciliation report.
    /// The API always resolves the project's persisted vocabulary before submitting.
    /// </summary>
    private void ValidateVerificationVocabulary(VerificationMethodPolicy? verificationPolicy,
        ILadderPolicy? ladderPolicy)
    {
        if (verificationPolicy is null) return;
        var policy = ladderPolicy ?? LegacyLadderPolicy.Instance;
        foreach (var change in _requirementChanges)
        {
            if (change.Kind == RequirementChangeKind.Retire) continue;
            // A level the project ladder does not configure at all is not this rule's business:
            // ValidateReadyForReview below names it in the terms the author needs. Asking a resolved policy
            // for a capability it has no definition for would replace that message with a ladder-lookup one.
            if (!policy.OrderedLevels.Contains(change.Level)) continue;
            if (!policy.HasVerification(change.Level)) continue;
            if (verificationPolicy.IsPermitted(change.VerificationMethod)) continue;
            var identity = string.IsNullOrWhiteSpace(change.DisplayNumber)
                ? $"A new {change.Level} requirement"
                : change.DisplayNumber;
            var declared = string.IsNullOrWhiteSpace(change.VerificationMethod)
                ? "declares no verification method"
                : $"declares the verification method '{change.VerificationMethod}', which is not";
            throw new DomainException(
                $"{identity} {declared} in this project's permitted vocabulary. Permitted verification methods: {verificationPolicy.DescribePermitted()}.");
        }
    }

    private void ValidateReadyForReview(ILadderPolicy? ladderPolicy = null,
        ChangeRequestTraceReviewEvidence? traceEvidence = null, bool allowUnresolvedTraceSeed = false)
    {
        if (string.IsNullOrWhiteSpace(Problem) || string.IsNullOrWhiteSpace(Analysis) || string.IsNullOrWhiteSpace(Solution))
            throw new DomainException("Problem, Analysis, and Solution are required before review.");
        if (_requirementChanges.Count == 0)
            throw new DomainException("At least one requirement change is required before review.");
        foreach (var item in _requirementChanges) EnsureRequirementLevel(item.Level, ladderPolicy);
        // Moved here from the Draft. A proposal may rest unfinished for as long as its author needs, but it
        // cannot be put in front of an approver that way, and it must never reach materialization — where a
        // requirement revision with no statement would flow into baselines, generated documents and traces.
        //
        // Named rather than counted: "one of your proposals is unfinished" sends the author hunting through
        // a list they have already read.
        foreach (var item in _requirementChanges)
        {
            var identity = string.IsNullOrWhiteSpace(item.BaseNumber) ? "A new requirement" : item.BaseNumber;
            if (item.Kind != RequirementChangeKind.Retire && string.IsNullOrWhiteSpace(item.Statement))
                throw new DomainException($"{identity} has no statement. Finish or remove it before review.");
            if (item.Kind != RequirementChangeKind.Introduce && string.IsNullOrWhiteSpace(item.BaseNumber))
                throw new DomainException("A proposal that changes an existing requirement must name it before review.");
            var parentLevels = ParentLevels(ladderPolicy, item.Level);
            var proposed = ProposedParentIds(item.ProposedUpstreamRevisionIdsJson, identity);
            var derived = RequirementAuthoringJson.IsDerived(item.AttributesJson);
            if (SnapshotContractVersion >= CurrentSnapshotContractVersion
                && parentLevels.Count > 0 && item.Kind != RequirementChangeKind.Retire)
                ExactParentSelectionPolicy.Validate(
                    derived ? ExactParentClassification.Derived : ExactParentClassification.Allocated,
                    proposed, derived ? item.Rationale : "", "requirement revision");
        }
        if (traceEvidence is { DerivedUpstreamLinks.Count: > 0 } && !string.IsNullOrWhiteSpace(NoUpstreamRationale))
            throw new DomainException("An assessment-derived upstream edge cannot be combined with a no-upstream answer.");
        if (_upstreamLinks.Count > 0 && !string.IsNullOrWhiteSpace(NoUpstreamRationale))
            throw new DomainException("Named upstream change requests cannot be combined with a no-upstream answer.");
        if (traceEvidence is not null && !traceEvidence.IsTopOfLadder
            && _upstreamLinks.Count == 0 && string.IsNullOrWhiteSpace(NoUpstreamRationale)
            && traceEvidence.DerivedUpstreamLinks.Count == 0)
            throw new DomainException("Name at least one upstream change request or state why no upstream change request applies before review.");
        // Direct aggregate construction is retained for historical seed fixtures that have no persistence
        // boundary from which to resolve ladder evidence. The live API always supplies the resolved object;
        // a current-contract submission with a resolved policy may not omit it.
        if (SnapshotContractVersion >= CurrentSnapshotContractVersion && traceEvidence is null
            && !_allowLegacyHistoricalSubmission && !allowUnresolvedTraceSeed)
            throw new DomainException("The server must resolve the effective ladder and upstream trace before review.");
        if (!string.IsNullOrWhiteSpace(InheritedUpstreamContextJson) && !UpstreamAnswerAffirmed)
            throw new DomainException("The inherited upstream answer must be explicitly affirmed or changed before review.");
    }

    private static IReadOnlyList<RequirementLevel> ParentLevels(ILadderPolicy? policy, RequirementLevel child)
    {
        // An empty relationship list is a root exemption only after the level
        // itself has been positively resolved by the configured policy.  Do not
        // let a policy lookup failure look like a configured root.
        var resolved = policy ?? LegacyLadderPolicy.Instance;
        _ = resolved.Definition(child);
        return resolved.ParentLevels(child);
    }

    private static IReadOnlyList<Guid> ProposedParentIds(string json, string identity)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [],
                identity);
        }
        catch (JsonException)
        {
            throw new DomainException($"{identity} carries malformed exact upstream revisions.");
        }
    }

    private void EnsureRequirementLevel(RequirementLevel level, ILadderPolicy? ladderPolicy = null)
    {
        var policy = ladderPolicy ?? LegacyLadderPolicy.Instance;
        if (!policy.AcceptsChangeRequest(Type, level))
            throw new DomainException(Type switch
            {
                ChangeRequestType.System => "A System change request can contain System requirements only. Use an HLRCR or LLRCR for software work.",
                ChangeRequestType.Interface => "An Interface change request can contain Interface requirements only. Use an SRCR for System work.",
                _ => "A Software change request can contain HLR or LLR requirements only. Use an SRCR for System work.",
            });
        if (Type == ChangeRequestType.Software && SoftwareLevel is not null
            && !policy.AcceptsChangeRequest(Type, SoftwareLevel, level))
            throw new DomainException($"This Software Draft belongs to the {(SoftwareLevel == RequirementLevel.HighLevel ? "HLR" : "LLR")} workspace and cannot contain {(level == RequirementLevel.HighLevel ? "HLR" : "LLR")} changes.");
    }

    /// <summary>
    /// Sets the change case from whichever form the author supplied.
    ///
    /// When rich content is given it is authoritative and the plain text is derived from it; when it is not,
    /// the plain text is what was written and the rich form is that same text as a single paragraph. Either
    /// way both are populated and both say the same thing, so no reader has to know which one the author
    /// used.
    /// </summary>
    private void SetCase(string problem, string analysis, string solution,
        string? problemRich, string? analysisRich, string? solutionRich)
    {
        (Problem, ProblemRich) = Resolve(problem, problemRich);
        (Analysis, AnalysisRich) = Resolve(analysis, analysisRich);
        (Solution, SolutionRich) = Resolve(solution, solutionRich);

        static (string Plain, string Rich) Resolve(string plain, string? rich)
        {
            if (string.IsNullOrWhiteSpace(rich)) return (plain.Trim(), Content.RichContent.FromPlainText(plain));
            var canonical = Content.RichContent.Canonicalize(rich);
            return (Content.RichContent.ToPlainText(canonical), canonical);
        }
    }

    private string ComputeSnapshotHash(ChangeRequestTraceReviewEvidence? traceEvidence = null)
    {
        // The rich forms are in the hash in their own right. Two different structures can reduce to the same
        // readable text — a table and a list of lines, say — and hashing only the projection would let the
        // thing an approver actually looked at change underneath a recorded signature.
        var changes = _requirementChanges.OrderBy(x => x.DisplayNumber).Select(x =>
            SnapshotContractVersion == 1
                ? $"{x.DisplayNumber}:{x.Level}:{x.Kind}:{x.Statement}:{x.Rationale}:{x.VerificationMethod}:{x.RichText}:{x.AttributesJson}:{x.ImpactDispositionJson}:{x.ProposedUpstreamRevisionIdsJson}"
                : $"{x.DisplayNumber}:{x.Level}:{x.Kind}:{x.Statement}:{x.Rationale}:{x.VerificationMethod}:{x.RichText}:{x.AttributesJson}:{x.ImpactDispositionJson}:" +
                  $"{(RequirementAuthoringJson.IsDerived(x.AttributesJson) ? "Derived" : ProposedParentIds(x.ProposedUpstreamRevisionIdsJson, x.DisplayNumber).Count > 0 ? "Allocated" : "Unspecified")}:" +
                  $"{(RequirementAuthoringJson.IsDerived(x.AttributesJson) ? x.Rationale : string.Empty)}:" +
                  $"{string.Join(",", ProposedParentIds(x.ProposedUpstreamRevisionIdsJson, x.DisplayNumber).OrderBy(id => id))}");
        var trace = SnapshotContractVersion < 3 ? "" : string.Join(";",
            _upstreamLinks.OrderBy(x => x.UpstreamChangeRequestId).Select(x =>
                $"{x.UpstreamChangeRequestId}:{x.UpstreamBuildId}:{x.UpstreamBuildVersion}:{x.Rationale}:{x.ActorId}:{x.StatedAt:O}"));
        var inherited = SnapshotContractVersion < 3 ? "" : InheritedUpstreamContextJson ?? "";
        var noUpstream = SnapshotContractVersion < 3 ? "" :
            $"{NoUpstreamRationale}:{NoUpstreamStatedBy}:{NoUpstreamStatedAt:O}:{UpstreamAnswerAffirmed}:{UpstreamAnswerAffirmedBy}:{UpstreamAnswerAffirmedAt:O}";
        var derived = SnapshotContractVersion < 3 || traceEvidence is null ? "" : string.Join(";",
            traceEvidence.DerivedUpstreamLinks.OrderBy(x => x.UpstreamChangeRequestId).ThenBy(x => x.AssessmentLinkId)
                .Select(x => $"{x.UpstreamChangeRequestId}:{x.AssessmentId}:{x.AssessmentLinkId}:{x.BuildId}:{x.UpstreamDisplayNumber}"));
        if (SnapshotContractVersion < 3)
        {
            // v1 and v2 are historical byte contracts. Their hashes must remain exactly reproducible after
            // the v3 trace material was introduced, including the original field count and separators.
            var historicalContent = string.Join("|", DisplayNumber, Title, Problem, Analysis, Solution,
                ProblemRich, AnalysisRich, SolutionRich, string.Join(";", changes));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(historicalContent))).ToLowerInvariant();
        }
        var frozenTraceSnapshot = BuildTraceSnapshotJson(traceEvidence);
        var content = string.Join("|", DisplayNumber, Title, Problem, Analysis, Solution,
            ProblemRich, AnalysisRich, SolutionRich, string.Join(";", changes), trace, inherited, noUpstream, derived,
            frozenTraceSnapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private string BuildTraceSnapshotJson(ChangeRequestTraceReviewEvidence? evidence)
    {
        if (SnapshotContractVersion < 3) return string.Empty;
        return JsonSerializer.Serialize(new
        {
            contractVersion = SnapshotContractVersion,
            authoredLinks = _upstreamLinks.OrderBy(x => x.UpstreamChangeRequestId).Select(x => new
            {
                x.Id, x.UpstreamChangeRequestId, x.UpstreamDisplayNumber, x.UpstreamBuildId,
                x.UpstreamBuildVersion, x.Rationale, x.ActorId, x.StatedAt
            }),
            noUpstreamRationale = NoUpstreamRationale,
            noUpstreamStatedBy = NoUpstreamStatedBy,
            noUpstreamStatedAt = NoUpstreamStatedAt,
            inheritedFromChangeRequestId = InheritedFromChangeRequestId,
            inheritedUpstreamContextJson = InheritedUpstreamContextJson,
            inheritedAnswerAffirmed = UpstreamAnswerAffirmed,
            inheritedAnswerAffirmedBy = UpstreamAnswerAffirmedBy,
            inheritedAnswerAffirmedAt = UpstreamAnswerAffirmedAt,
            derivedLinks = (evidence?.DerivedUpstreamLinks ?? [])
                .OrderBy(x => x.UpstreamChangeRequestId).ThenBy(x => x.AssessmentLinkId)
                .ThenBy(x => x.AssessmentId).ToArray(),
            isTopOfLadder = evidence?.IsTopOfLadder ?? false,
        });
    }

    private void Audit(string type, string actor, string detail, DateTimeOffset now) =>
        _auditEvents.Add(new AuditEvent(Id, type, actor, detail, now));
    private void EnsureAuthor(string actorId, bool administratorAuthority)
    {
        if (!administratorAuthority && !string.Equals(AuthorId, actorId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Only the change request author can perform this action.");
    }
    private void EnsureDraft() { if (State != ChangeRequestState.Draft) throw new DomainException("The change request must be in Draft."); }
    private void EnsureInReview() { if (State != ChangeRequestState.InReview || ActiveReviewCycle is null) throw new DomainException("The change request is not in active review."); }
}
