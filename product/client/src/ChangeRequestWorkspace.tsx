import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { artifactAcronym, changeRequestAllocation, changeRequestState, stateLabel } from './presentation'
import type { FormEvent } from "react";
import { SignatureDialog } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import ControlledRequirementEditor from "./ControlledRequirementEditor";
import { canDeclareVerificationMethod, decideKindChange, firstPermittedMethod, useVerificationVocabulary, verificationBlockedReason } from "./verificationMethods";
import type {
  ControlledRequirementDraft,
  RequirementKind,
  RequirementLevel,
} from "./ControlledRequirementEditor";
import PersonPicker from "./PersonPicker";
import ControlledAttachments from "./ControlledAttachments";
import ChangeRequestJiraLink from "./ChangeRequestJiraLink";
import { PersonName } from "./People";
import { personLabel } from "./PeopleRegistry";
import ReviewCycleCard from "./ReviewCycleCard";
import { EarlierCycleComments, ReviewCommentBlock, useReviewComments } from "./ReviewComments";
import { ReviewEndedNotice } from "./ReviewEndedNotice";
import {
  ControlledChangeAuthoringActions,
  ControlledChangeAuthoringForm,
  ControlledChangeCaseCard,
  ControlledChangePage,
  ControlledChangeReadLayout,
  ControlledStatusCard,
} from "./ControlledChangePage";
import { RichCaseField, RichContentView } from "./RichContent";
import { useDebouncedSave } from "./autosave";
import { emptyRichContent, fromPlainText, toPlainText } from "./richContentModel";
import ProblemReportPicker from "./ProblemReportPicker";
import "./ChangeRequestWorkspace.css";
import "./ReviewMode.css";

type Requirement = {
  id: string;
  displayNumber: string;
  level: RequirementLevel;
  kind: RequirementKind;
  statement: string;
  rationale: string;
  verificationMethod: string;
  richText: string;
  attributesJson: string;
  impactDispositionJson: string;
  targetSectionId?: string;
  upstreamRevisionIds?: string[];
};
/// What this change does to the requirement, as a noun.
///
/// The chip beside a proposal read "HighLevel · Modify". The level was already settled by the identifier —
/// HLR-000149 is a high-level requirement and cannot be anything else — so it spent the space repeating
/// something the reader had just read. What was left described the change as an instruction to somebody
/// rather than as the thing being proposed.
const changeKindLabel = (kind: string) =>
  kind === "Introduce" ? "Introduction" : kind === "Modify" ? "Modification" : kind === "Retire" ? "Retirement" : kind;

/// The headline of an audit entry, written for a reader rather than derived from a type name.
///
/// Splitting the stored event name on its capitals produced "Scr Approved" and "Scr Created" — the product's
/// own abbreviation title-cased by an algorithm that had no idea it was one. "Selected For Baseline" was
/// worse than untidy: selection is an internal step, and what actually happened, the thing a reader came to
/// find out, is that the change was allocated to a build. So that one says which build.
const auditEventTitle = (eventType: string, buildVersion: string) => {
  if (eventType === "SelectedForBaseline") return buildVersion ? `Allocated to Build ${buildVersion}` : "Allocated to a build";
  return eventType
    .replace(/([A-Z])/g, " $1").trim().split(/\s+/)
    // "Scr" is the internal name of the aggregate, not a statement about this record's prefix. Rendering it
    // as SRCR would have labelled an HLRCR's own history "SRCR approved", which is exactly backwards: the
    // identifier at the top of the page already says which kind of change request this is.
    .map((word) => (word === "Scr" || word === "Swcr" ? "Change request" : word))
    .map((word, index) => (index === 0 || word === word.toUpperCase() ? word : word.toLowerCase()))
    .join(" ");
};

type Step = {
  position: number;
  approverId: string;
  approverName: string;
  authority: string;
  stageName: string;
  rationale?: string;
  state: string;
  decidedAt?: string;
};
type Cycle = {
  id: string;
  sequence: number;
  mode: "Sequential" | "Parallel";
  state: string;
  snapshotHash: string;
  /** 0 means this historical cycle predates the trace snapshot contract. */
  snapshotContractVersion?: number;
  snapshotJson?: string;
  startedAt: string;
  completedAt?: string;
  closureReason?: string;
  steps: Step[];
};
type UpstreamDraftLink = { upstreamChangeRequestId: string; rationale: string };
type UpstreamDetail = UpstreamDraftLink & {
  id: string;
  upstreamDisplayNumber: string;
  upstreamBuildId?: string | null;
  upstreamBuildVersion: string;
  actor: string;
  statedAt: string;
};
type UpstreamHistory = {
  id: string;
  action: string;
  upstreamChangeRequestId?: string | null;
  upstreamDisplayNumber?: string | null;
  upstreamBuildId?: string | null;
  upstreamBuildVersion?: string | null;
  rationale: string;
  actor: string;
  occurredAt: string;
};
type UpstreamCandidate = {
  id: string;
  displayNumber: string;
  title: string;
  build: string;
  earlierBuild: boolean;
  assessmentDerived: boolean;
};
type DerivedUpstreamEdge = {
  upstreamChangeRequestId: string;
  upstreamDisplayNumber: string;
  upstreamBuildId: string;
  upstreamBuildVersion: string;
  assessmentId: string;
  assessmentLinkId: string;
};
type Audit = {
  eventType: string;
  actorId: string;
  /** The sentence a reader sees. Never parsed for meaning. */
  detail: string;
  occurredAt: string;
  /** Structured technical evidence, when the event carries any. */
  evidenceJson?: string | null;
  /** 0 for events written before evidence was separated from the narrative. */
  schemaVersion?: number;
};
type ChangeRequestDetail = {
  id: string;
  baseNumber: string;
  revision: number;
  displayNumber: string;
  projectId: string;
  targetReleaseId: string;
  type: "System" | "Software" | "Interface";
  softwareLevel?: "HighLevel" | "LowLevel";
  title: string;
  problem: string;
  analysis: string;
  solution: string;
  problemRich: string;
  analysisRich: string;
  solutionRich: string;
  authorId: string;
  version: number;
  state: string;
  /** How far it had got when it was shelved. Present only while State is Deferred. */
  deferredFromState?: string | null;
  /** How far it had got when it was taken back. Present only while State is Withdrawn. */
  withdrawnFromState?: string | null;
  noUpstream?: { rationale: string; actor: string; statedAt: string } | null;
  upstream?: UpstreamDetail[];
  upstreamHistory?: UpstreamHistory[];
  inheritedUpstream?: { inheritedFromChangeRequestId?: string; inheritedUpstreamContextJson: string; affirmed: boolean; affirmedBy?: string; affirmedAt?: string } | null;
  /** Set when a build was reopened underneath this, taking back the revision it was written against. */
  rebaseRequiredReason?: string | null;
  createdAt: string;
  updatedAt: string;
  requirementChanges: Requirement[];
  reviewCycles: Cycle[];
  audit: Audit[];
};
type ProblemReportSummary = {
  id: string;
  displayNumber: string;
  title: string;
  state: string;
};
type DraftRequirement = ControlledRequirementDraft;
type Approver = { userId: string; name: string };
type ApplicableStage = {
  position: number;
  name: string;
  kind?: "Review" | "Approval";
  requiredRole: string;
  candidates: { userId: string; name: string; role: string }[];
};
type ApplicableWorkflow = {
  required?: boolean;
  minimum?: number;
  allowsAdditional?: boolean;
  name?: string;
  version?: number;
  mode?: string;
  stages?: ApplicableStage[];
};
type EditLock = {
  id: string;
  version: number;
  userName: string;
  openedAt: string;
  lastActivityAt: string;
  expiresAt: string;
  draftJson: string;
  resumed: boolean;
};
type LockStatus = {
  editable: boolean;
  locked: boolean;
  sessionId?: string;
  holder?: string;
  openedAt?: string;
  lastActivityAt?: string;
  expiresAt?: string;
  mine?: boolean;
};
type ScrDraft = {
  title: string; problem: string; analysis: string; solution: string;
  problemRich: string; analysisRich: string; solutionRich: string;
  problemReportIds?: string[];
  upstreamLinks?: UpstreamDraftLink[];
  noUpstreamRationale?: string | null;
  upstreamAnswerAffirmed?: boolean;
};
type AuthoringContext = {
  type: "System" | "Software" | "Interface";
  changeRequestNumber: string;
  author: { userName: string; displayName: string };
  requirementNumbers: Partial<Record<"SYSR" | "HLR" | "LLR" | "ICDR", string>>;
};
type Props = {
  api: string;
  changeRequestId: string;
  user: AuthUser;
  onBack: () => void;
  onChanged: () => Promise<void>;
  onOpenScr: (id: string) => void;
  onOpenRequirement: (id: string, level: RequirementLevel) => void;
  onOpenProblemReport: (id: string) => void;
  onDisciplineResolved: (discipline: "system" | "software", changeRequestType?: "Interface") => void;
  digitalThreadHref?: string;
  /**
   * The project's builds, so this record's own target can be resolved once it loads. Without them the rail
   * can only report the stored state, and "Selected for baseline" is the one wording nobody wants to read.
   */
  releases: { id: string; version: string; isReleased: boolean }[];
};

const pendingImpact = JSON.stringify({
  trace: "Pending",
  verification: "Pending",
  documents: "Pending",
  baseline: "Pending",
  collaboration: "Pending",
});
const base = (display: string) => display.replace(/\.\d{2}$/, "");
const revision = (display: string) => Number(display.match(/\.(\d{2})$/)?.[1] ?? 0);
const prefixFor = (level: RequirementLevel) =>
  level === "System" ? "SYSR" : level === "HighLevel" ? "HLR" : level === "LowLevel" ? "LLR" : "ICDR";
const parseObject = (value: string | undefined): Record<string, unknown> => {
  try {
    return JSON.parse(value || "{}") as Record<string, unknown>;
  } catch {
    return {};
  }
};
type InheritedTraceAnswer = {
  links?: { upstreamChangeRequestId?: string; upstreamDisplayNumber?: string; upstreamBuildVersion?: string; rationale?: string; UpstreamChangeRequestId?: string; UpstreamDisplayNumber?: string; UpstreamBuildVersion?: string; Rationale?: string }[];
  noUpstreamRationale?: string;
};
type FrozenTraceSnapshot = {
  isTopOfLadder?: boolean;
  authoredLinks?: { upstreamChangeRequestId?: string; upstreamDisplayNumber?: string; upstreamBuildVersion?: string; rationale?: string; UpstreamChangeRequestId?: string; UpstreamDisplayNumber?: string; UpstreamBuildVersion?: string; Rationale?: string }[];
  noUpstreamRationale?: string | null;
  derivedLinks?: { upstreamChangeRequestId?: string; assessmentLinkId?: string; assessmentId?: string; upstreamDisplayNumber?: string; UpstreamChangeRequestId?: string; AssessmentLinkId?: string; AssessmentId?: string; UpstreamDisplayNumber?: string }[];
};
const inheritedTraceAnswer = (value: string | undefined): InheritedTraceAnswer => {
  try {
    return JSON.parse(value || "{}") as InheritedTraceAnswer;
  } catch {
    return {};
  }
};
const addToIdentifier = (identifier: string | undefined, offset: number) => {
  if (!identifier) return "";
  const match = identifier.match(/^([A-Z]+)-(\d+)$/);
  if (!match) return identifier;
  return `${match[1]}-${(Number(match[2]) + offset).toString().padStart(6, "0")}`;
};
/** As in the new change request editor: the verification method comes from the project vocabulary (#701). */
const createRequirement = (
  level: RequirementLevel,
  kind: RequirementKind,
  baseNumber = "",
  defaultVerificationMethod = "",
): DraftRequirement => ({
  baseNumber,
  revision: 0,
  level,
  kind,
  statement: "",
  rationale: "",
  verificationMethod: level === "Interface" ? "Not applicable" : defaultVerificationMethod,
  richText: "",
  attributesJson: JSON.stringify({ criticality: "Normal", owner: "" }),
  impactDispositionJson: pendingImpact,
  isDerived: false,
  // Empty means unchanged, as in the new change request editor.
  targetSectionId: "",
  upstreamRevisionIds: [],
});
const normalizeRequirement = (
  item: Partial<DraftRequirement>,
  fallbackLevel: RequirementLevel,
): DraftRequirement => {
  const attributes = parseObject(item.attributesJson);
  return {
    ...createRequirement(fallbackLevel, item.kind || "Introduce"),
    ...item,
    baseNumber: item.baseNumber || "",
    level: item.level || fallbackLevel,
    richText: item.richText || "",
    attributesJson: item.attributesJson || JSON.stringify({ criticality: "Normal", owner: "" }),
    impactDispositionJson:
      item.impactDispositionJson && item.impactDispositionJson !== "{}"
        ? item.impactDispositionJson
        : pendingImpact,
    isDerived: item.isDerived ?? attributes.derived === true,
  };
};
const mapRequirements = (items: Requirement[]) =>
  items.map((item) =>
    normalizeRequirement(
      {
        baseNumber: base(item.displayNumber),
        revision: revision(item.displayNumber),
        level: item.level,
        kind: item.kind,
        statement: item.statement,
        rationale: item.rationale,
        verificationMethod: item.verificationMethod,
        richText: item.richText,
        attributesJson: item.attributesJson,
        impactDispositionJson: item.impactDispositionJson,
        targetSectionId: item.targetSectionId ?? "",
        upstreamRevisionIds: item.upstreamRevisionIds ?? [],
      },
      item.level,
    ),
  );
const proposalComplete = (item: DraftRequirement) =>
  Boolean(
    item.baseNumber &&
      (item.kind === "Retire" || item.statement.trim()) &&
      (!(item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
        item.rationale.trim()) &&
      (item.level === "System" || item.level === "Interface" ||
        (item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
        Boolean(item.upstreamRevisionIds?.length)),
  );

// The rule this held — a proposal needs its identifier and statement — now lives in
// `SystemChangeRequest.ValidateReadyForReview`, where it gates review submission rather than check-in. Kept
// in one place rather than two, so the client cannot drift into refusing something the aggregate accepts.

const workingCopyJson = (draft: ScrDraft, problemReportIds: string[], requirements: DraftRequirement[]) =>
  JSON.stringify({
    ...draft,
    problemReportIds: [...problemReportIds].sort(),
    requirementChanges: requirements,
  });

/**
 * The sentence to show for an audit event.
 *
 * Events written before evidence was separated stored a serialized payload in the narrative field, so the
 * timeline rendered a wall of GUIDs and hashes as the audit story. Those are recognised by their schema
 * version — never by parsing the string — and given a plain description; the payload itself moves into the
 * evidence panel, where it belongs and where it is labelled as unrecognised.
 */
const auditSummary = (event: Audit) => {
  const legacyPayload = (event.schemaVersion ?? 0) === 0 && event.detail.trimStart().startsWith("{");
  if (!legacyPayload) return event.detail;
  return `${event.eventType.replace(/([A-Z])/g, " $1").trim().toLowerCase()} — recorded before this event carried a written summary.`;
};

/**
 * Technical evidence, collapsed and labelled, never the headline.
 *
 * Field names come from the record rather than a hardcoded list, so an evidence shape that gains a field
 * shows it instead of silently dropping it. Values are rendered as text: nothing here is interpreted, so no
 * stored string can become markup or be mistaken for a semantic signal.
 */
function AuditEvidence({ event }: { event: Audit }) {
  const legacyPayload = (event.schemaVersion ?? 0) === 0 && event.detail.trimStart().startsWith("{");
  const raw = event.evidenceJson ?? (legacyPayload ? event.detail : null);
  if (!raw) return null;

  let fields: [string, string][] = [];
  try {
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed))
      fields = Object.entries(parsed).map(([key, value]) => [key, value === null || value === undefined ? "—" : String(value)]);
  } catch {
    fields = [];
  }

  return (
    <details className="auditEvidence">
      <summary>{legacyPayload && !event.evidenceJson ? "Technical evidence (recorded in an earlier format)" : "Technical evidence"}</summary>
      {fields.length ? (
        <dl>
          {fields.map(([key, value]) => (
            <div key={key}>
              <dt>{key.replace(/([A-Z])/g, " $1").replace(/^./, first => first.toUpperCase())}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
      ) : (
        <p className="auditEvidenceRaw">{raw}</p>
      )}
      <button type="button" onClick={() => void navigator.clipboard?.writeText(raw)}>Copy evidence</button>
    </details>
  );
}

function ExactUpstreamReferences({api,projectId,releaseId,childLevel,revisionIds,onOpen}:{api:string;projectId:string;releaseId:string;childLevel:RequirementLevel;revisionIds:string[];onOpen:(id:string,level:RequirementLevel)=>void}) {
  const [references,setReferences]=useState<{revisionId:string;artifactId:string;displayNumber:string;level:RequirementLevel}[]>([])
  const [loaded,setLoaded]=useState(false)
  const revisionKey=revisionIds.join(',')
  useEffect(()=>{
    let active=true
    if(!revisionKey){setReferences([]);setLoaded(true);return()=>{active=false}}
    setLoaded(false)
    fetch(`${api}/api/authoring/upstream-requirements?projectId=${projectId}&releaseId=${releaseId}&childLevel=${childLevel}&selected=${encodeURIComponent(revisionKey)}&limit=50`)
      .then(response=>response.ok?response.json():[])
      .then(rows=>{if(active)setReferences(rows)})
      .catch(()=>{if(active)setReferences([])})
      .finally(()=>{if(active)setLoaded(true)})
    return()=>{active=false}
  },[api,childLevel,projectId,releaseId,revisionKey])
  if(!revisionKey)return <span>No exact upstream revisions allocated</span>
  if(!loaded)return <span>Loading exact upstream revisions…</span>
  return <span className="artifactReferenceCloud">{references.map(reference=><button type="button" key={reference.revisionId} onClick={()=>onOpen(reference.artifactId,reference.level)}>{reference.displayNumber}</button>)}{references.length<revisionIds.length&&<i>Unavailable controlled revision</i>}</span>
}

export default function ChangeRequestWorkspace({
  api,
  changeRequestId,
  user,
  onBack,
  onChanged,
  onOpenScr,
  onOpenRequirement,
  onOpenProblemReport,
  onDisciplineResolved,
  digitalThreadHref,
  releases,
}: Props) {
  const [scr, setScr] = useState<ChangeRequestDetail>();
  const [drivingProblemReports, setDrivingProblemReports] = useState<ProblemReportSummary[]>([]);
  const [context, setContext] = useState<AuthoringContext>();
  const [mode, setMode] = useState<"view" | "edit" | "approvers">("view");
  const [reviewMode, setReviewMode] = useState<"Sequential" | "Parallel">("Sequential");
  const [error, setError] = useState("");
  const [loadFailure, setLoadFailure] = useState("");
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState("");
  const [approvalRationale, setApprovalRationale] = useState("");
  const [signing, setSigning] = useState(false);
  // #701: a proposal added to a checked-out Draft starts on a method this project permits, exactly as in the
  // new change request editor.
  const verification = useVerificationVocabulary(api, scr?.projectId);
  const defaultVerificationMethod = firstPermittedMethod(verification);
  // As in the new change request editor: an ICD declares no method, everything else waits for the project
  // to say what it permits rather than starting a proposal on a blank the select would not display.
  const verificationBlocked = scr?.type !== "Interface" && !canDeclareVerificationMethod(verification);
  const [lock, setLock] = useState<EditLock>();
  const [lockStatus, setLockStatus] = useState<LockStatus>();
  const [autosaveStatus, setAutosaveStatus] = useState<"Saved" | "Unsaved" | "Saving" | "Error" | "Conflict">("Saved");
  // Pressing a button that saves should say so. Without this the only signal that a check-in worked was the
  // form changing mode, which is easy to miss and indistinguishable from nothing having happened.
  const [saved, setSaved] = useState("");
  const [draft, setDraft] = useState<ScrDraft>({
    title: "", problem: "", analysis: "", solution: "",
    problemRich: emptyRichContent, analysisRich: emptyRichContent, solutionRich: emptyRichContent,
  });
  const [requirements, setRequirements] = useState<DraftRequirement[]>([]);
  const [problemReportIds, setProblemReportIds] = useState<string[]>([]);
  const [upstreamCandidates, setUpstreamCandidates] = useState<UpstreamCandidate[]>([]);
  const [derivedUpstreamEdges, setDerivedUpstreamEdges] = useState<DerivedUpstreamEdge[]>([]);
  const [upstreamAnswerComplete, setUpstreamAnswerComplete] = useState(false);
  const [upstreamSearch, setUpstreamSearch] = useState("");
  const [upstreamCandidatesTop, setUpstreamCandidatesTop] = useState(false);
  const [includeEarlierBuilds, setIncludeEarlierBuilds] = useState(false);
  const [approvers, setApprovers] = useState<Approver[]>([]);
  // Null means the applicable workflow has not been resolved yet (or its lookup failed): the picker stays
  // unfiltered and the server remains authoritative. True means no workflow is configured, so only users
  // holding Approver authority can legitimately be selected.
  const [fallbackApproverOnly, setFallbackApproverOnly] = useState<boolean | null>(null);
  const [applicableWorkflow, setApplicableWorkflow] = useState<ApplicableWorkflow | null>(null);
  const lockRef = useRef<EditLock | undefined>(undefined);
  const draftRef = useRef("");
  const lastSavedRef = useRef("");
  const checkoutSnapshotRef = useRef("");
  const resumedWorkingCopyRef = useRef(false);
  const savingRef = useRef(false);

  const serializedWorkingCopy = useMemo(
    () => workingCopyJson(draft, problemReportIds, requirements),
    [draft, problemReportIds, requirements],
  );

  const loadStatus = useCallback(async () => {
    const response = await fetch(`${api}/api/controlled-editing/status?artifactType=ChangeRequest&artifactId=${changeRequestId}`);
    if (response.ok) setLockStatus((await response.json()) as LockStatus);
  }, [api, changeRequestId]);

  const load = useCallback(async () => {
    setLoadFailure("");
    try {
      const response = await fetch(`${api}/api/change-requests/${changeRequestId}`);
      if (!response.ok) {
        setLoadFailure(response.status === 404
          ? "No originating change request is available for this requirement."
          : "The originating change request could not be loaded in this build workspace.");
        return;
      }
      {
      const detail = (await response.json()) as ChangeRequestDetail;
      onDisciplineResolved(detail.type === "Software" ? "software" : "system", detail.type === "Interface" ? "Interface" : undefined);
      setScr(detail);
      let reports: ProblemReportSummary[] = [];
      try {
        const reportsResponse = await fetch(`${api}/api/problem-reports/linked/ChangeRequest/${detail.id}`);
        reports = reportsResponse.ok
          ? (await reportsResponse.json()) as ProblemReportSummary[]
          : [];
        setDrivingProblemReports(reports);
      } catch {
        // The controlled change remains usable if this supplementary trace view cannot be loaded.
        setDrivingProblemReports([]);
      }
      if (mode !== "edit") {
        setDraft({
          title: detail.title,
          problem: detail.problem,
          analysis: detail.analysis,
          solution: detail.solution,
          problemRich: detail.problemRich || fromPlainText(detail.problem),
          analysisRich: detail.analysisRich || fromPlainText(detail.analysis),
          solutionRich: detail.solutionRich || fromPlainText(detail.solution),
        });
        setRequirements(mapRequirements(detail.requirementChanges));
        setProblemReportIds(reports.map((report) => report.id));
      }
      }
    } catch {
      setLoadFailure("The originating change request could not be loaded. Check the AeroLink service and try again.");
    }
    await loadStatus();
  }, [api, loadStatus, mode, onDisciplineResolved, changeRequestId]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!scr) return;
    const controller = new AbortController();
    const query = upstreamSearch.trim() ? `&search=${encodeURIComponent(upstreamSearch.trim())}` : "";
    fetch(`${api}/api/change-requests/${scr.id}/upstream-candidates?limit=25&includeEarlierBuilds=${includeEarlierBuilds}${query}`, { signal: controller.signal })
      .then(async (response) => response.ok ? response.json() as Promise<{ isTopOfLadder: boolean; upstreamAnswerComplete?: boolean; candidates: UpstreamCandidate[]; derivedEdges?: DerivedUpstreamEdge[] }> : undefined)
      .then((value) => {
        if (!value) return;
        setUpstreamCandidatesTop(value.isTopOfLadder);
        setUpstreamAnswerComplete(value.upstreamAnswerComplete ?? false);
        setDerivedUpstreamEdges(value.derivedEdges ?? []);
        if (mode === "edit") setUpstreamCandidates(value.candidates ?? []);
      })
      .catch(() => { /* The server remains authoritative at check-in when candidate search is unavailable. */ });
    return () => controller.abort();
  }, [api, includeEarlierBuilds, mode, scr, upstreamSearch]);

  useEffect(() => {
    if (!scr) return;
    let cancelled = false;
    fetch(`${api}/api/authoring/context?projectId=${scr.projectId}&type=${scr.type}${scr.type === "Software" ? `&softwareLevel=${scr.softwareLevel ?? "HighLevel"}` : ""}`)
      .then(async (response) => {
        if (!response.ok) throw new Error("Authoring context unavailable.");
        return response.json() as Promise<AuthoringContext>;
      })
      .then((value) => {
        if (!cancelled) setContext(value);
      })
      .catch(() => {
        if (!cancelled) setContext(undefined);
      });
    return () => {
      cancelled = true;
    };
  }, [api, scr]);

  useEffect(() => {
    lockRef.current = lock;
  }, [lock]);

  useEffect(() => {
    draftRef.current = serializedWorkingCopy;
    if (mode === "edit" && serializedWorkingCopy !== lastSavedRef.current && autosaveStatus !== "Saving" && autosaveStatus !== "Conflict")
      setAutosaveStatus("Unsaved");
  }, [autosaveStatus, mode, serializedWorkingCopy]);


  /**
   * Runs work that disables the toolbar, and always gives the toolbar back.
   *
   * `busy` drives both the disabled state and the "Checking lock…" label, and every handler used to clear it
   * on the paths it thought of. A throw was not one of them — so a request that failed before it reached the
   * network left the change request looking frozen: no error, no spinner finishing, every button dead until
   * the page was reloaded. A control that can enter a state it cannot leave is worse than one that fails.
   */
  const withBusy = async <T,>(work: () => Promise<T>, whenItFails: string): Promise<T | undefined> => {
    setBusy(true);
    setError("");
    setSaved("");
    try {
      return await work();
    } catch (failure) {
      setError(failure instanceof Error ? `${whenItFails} ${failure.message}` : whenItFails);
      return undefined;
    } finally {
      setBusy(false);
    }
  };

  const beginEdit = async () => {
    const response = await withBusy(
      () =>
        fetch(`${api}/api/controlled-editing/checkout`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ artifactType: "ChangeRequest", artifactId: changeRequestId, leaseMinutes: 15 }),
        }),
      "This Draft could not be checked out.",
    );
    if (!response) return;
    if (!response.ok) {
      const body = (await response.json()) as { error?: string };
      setError(body.error || "This Draft could not be checked out.");
      await loadStatus();
      return;
    }
    const value = (await response.json()) as EditLock;
    try {
      const recovered = JSON.parse(value.draftJson) as ScrDraft & {
        requirementChanges?: Partial<DraftRequirement>[];
      };
      const fallbackLevel: RequirementLevel = scr?.type === "Software" ? "HighLevel" : scr?.type === "Interface" ? "Interface" : "System";
      const recoveredDraft = {
        title: recovered.title,
        problem: recovered.problem,
        analysis: recovered.analysis,
        solution: recovered.solution,
        // A recovery snapshot written before the change case could carry structure holds only the plain
        // fields. It reopens as paragraphs rather than as an empty form.
        problemRich: recovered.problemRich || fromPlainText(recovered.problem),
        analysisRich: recovered.analysisRich || fromPlainText(recovered.analysis),
        solutionRich: recovered.solutionRich || fromPlainText(recovered.solution),
        upstreamLinks: recovered.upstreamLinks ?? [],
        noUpstreamRationale: recovered.noUpstreamRationale ?? null,
        upstreamAnswerAffirmed: recovered.upstreamAnswerAffirmed ?? false,
      };
      const recoveredRequirements = (recovered.requirementChanges ?? [])
        .map((item) => normalizeRequirement(item, fallbackLevel));
      const recoveredReports = recovered.problemReportIds ?? drivingProblemReports.map((report) => report.id);
      const normalizedWorkingCopy = workingCopyJson(recoveredDraft, recoveredReports, recoveredRequirements);
      setLock(value);
      setDraft(recoveredDraft);
      setRequirements(recoveredRequirements);
      setProblemReportIds(recoveredReports);
      draftRef.current = normalizedWorkingCopy;
      lastSavedRef.current = normalizedWorkingCopy;
      checkoutSnapshotRef.current = normalizedWorkingCopy;
      resumedWorkingCopyRef.current = value.resumed;
      setAutosaveStatus("Saved");
      setMode("edit");
      await loadStatus();
    } catch {
      setError("The checked-out recovery snapshot could not be opened.");
    }
  };

  const autosave = useCallback(async (): Promise<EditLock | undefined> => {
    const currentLock = lockRef.current;
    const currentDraft = draftRef.current;
    if (
      !currentLock ||
      savingRef.current ||
      !currentDraft ||
      currentDraft === lastSavedRef.current
    )
      return currentLock;
    savingRef.current = true;
    setAutosaveStatus("Saving");
    try {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${currentLock.id}/autosave`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          expectedVersion: currentLock.version,
          draftJson: currentDraft,
          leaseMinutes: 15,
        }),
      });
      if (!response.ok) {
        setAutosaveStatus(response.status === 409 ? "Conflict" : "Error");
        const body = (await response.json()) as { error?: string };
        setError(body.error || "Server autosave failed.");
        return undefined;
      }
      const value = (await response.json()) as {
        version: number;
        updatedAt: string;
        expiresAt: string;
      };
      const next = {
        ...currentLock,
        version: value.version,
        lastActivityAt: value.updatedAt,
        expiresAt: value.expiresAt,
      };
      setLock(next);
      lockRef.current = next;
      lastSavedRef.current = currentDraft;
      setAutosaveStatus("Saved");
      return next;
    } catch {
      setAutosaveStatus("Error");
      return undefined;
    } finally {
      savingRef.current = false;
    }
  }, [api]);

  // Saved a second after typing stops rather than on a fixed timer. A timer either fires on an idle form,
  // wasting a write, or leaves the last seconds of typing unprotected; a pause after the last keystroke is
  // what a person means by "saved as I go". The ten-second ceiling covers writing a long paragraph without
  // pausing, which is the case somebody actually loses work in.
  useDebouncedSave(serializedWorkingCopy, async () => { await autosave(); }, {
    delaySeconds: 1,
    maximumSeconds: 10,
    enabled: mode === "edit",
  });

  useEffect(() => {
    if (mode !== "edit" || !lockRef.current) return;
    const heartbeat = window.setInterval(async () => {
      const current = lockRef.current;
      if (!current || savingRef.current) return;
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/heartbeat`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedVersion: current.version, leaseMinutes: 15 }),
      });
      if (response.ok) {
        const value = (await response.json()) as {
          version: number;
          updatedAt: string;
          expiresAt: string;
        };
        const next = {
          ...current,
          version: value.version,
          lastActivityAt: value.updatedAt,
          expiresAt: value.expiresAt,
        };
        setLock(next);
        lockRef.current = next;
      } else {
        setAutosaveStatus("Conflict");
      }
    }, 60_000);
    return () => {
      window.clearInterval(heartbeat);
    };
  }, [api, autosave, mode]);

  // Above the early returns below, because hooks cannot be called conditionally. The store fetches nothing
  // until the record has actually been submitted for review at least once.
  const comments = useReviewComments(api, changeRequestId, (scr?.reviewCycles.length ?? 0) > 0);

  const discard = async () => {
    const current = lockRef.current;
    if (current) {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/discard`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          expectedVersion: current.version,
          reason: "Author discarded the checked-out working copy.",
        }),
      });
      if (!response.ok) {
        const body = (await response.json()) as { error?: string };
        setError(body.error || "The checkout could not be discarded.");
        return;
      }
    }
    setLock(undefined);
    resumedWorkingCopyRef.current = false;
    checkoutSnapshotRef.current = "";
    setMode("view");
    await load();
  };

  const call = async (path: string, body: unknown) => {
    const outcome = await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${changeRequestId}/${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        const value = (await response.json()) as { error?: string };
        setError(value.error || "The operation could not be completed.");
        return false;
      }
      await load();
      await onChanged();
      setMode("view");
      return true;
    }, "The operation could not be completed.");
    return outcome === true;
  };

  /**
   * Putting this change request away for another day, and taking it back off the shelf.
   *
   * The reason is required by the domain and asked for here rather than defaulted, because a shelf whose entries
   * do not say why they are on it is a shelf nobody can plan from. Reinstating needs no reason: coming back to
   * work is the expected end of a deferral, not an exception to explain.
   */
  const defer = async () => {
    if (!scr) return;
    const reason = window.prompt(`Why is ${scr.displayNumber} being put away for another day?`);
    if (reason === null) return;
    if (!reason.trim()) { setError("A deferral reason is required."); return; }
    await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}/defer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason }),
      });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The change request could not be deferred.");
        return;
      }
      await load();
      await onChanged();
      setSaved("Put away for another day.");
    }, "The change request could not be deferred.");
  };

  /**
   * Stopping a review that should not be running, and putting the change request back in Draft.
   *
   * Asked for twice on purpose. A review in flight has people's attention booked against it, and the person
   * cancelling it is usually not the person who will next look at it — so the reason is the message to them.
   * The confirmation names the change request, because this control sits next to the review status and a
   * misplaced click should not silently unwind somebody's queue.
   */
  const cancelReview = async () => {
    if (!scr) return;
    if (!window.confirm(`Stop the review of ${scr.displayNumber} and return it to Draft at the same revision?`)) return;
    const reason = window.prompt(`Why is the review of ${scr.displayNumber} being cancelled?`);
    if (reason === null) return;
    if (!reason.trim()) { setError("Say why this review is being cancelled."); return; }
    await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}/cancel-review`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason, expectedVersion: scr.version }),
      });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The review could not be cancelled.");
        return;
      }
      await load();
      await onChanged();
      setSaved("Review cancelled. Back in Draft at the same revision.");
    }, "The review could not be cancelled.");
  };

  /**
   * Taking this change request back, and deleting one nobody ever reviewed.
   *
   * The reason is asked for and required, because a register entry that says only "withdrawn" tells the next
   * person nothing they needed. Deleting asks for confirmation instead of a reason: nothing was decided about
   * a draft nobody submitted, so there is nobody owed an explanation — but the record does go, and that is
   * worth a click.
   *
   * A refusal is shown as it arrives from the server. When the build is frozen the server names reopening as
   * the way through, and paraphrasing that here would give the reader a second, worse version of it.
   */
  const withdraw = async () => {
    if (!scr) return;
    const reason = window.prompt(`Why is ${scr.displayNumber} being taken back?`);
    if (reason === null) return;
    if (!reason.trim()) { setError("Say why this change request is being taken back."); return; }
    await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}/withdraw`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason }),
      });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The change request could not be withdrawn.");
        return;
      }
      await load();
      await onChanged();
      setSaved("Taken back. The record and its review history stay readable.");
    }, "The change request could not be withdrawn.");
  };

  const remove = async () => {
    if (!scr) return;
    if (!window.confirm(`Delete ${scr.displayNumber}? Nobody has reviewed it, so the record goes entirely.`)) return;
    await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}`, { method: "DELETE" });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The change request could not be deleted.");
        return;
      }
      await onChanged();
    }, "The change request could not be deleted.");
  };

  const reinstate = async () => {
    if (!scr) return;
    await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}/reinstate`, { method: "POST" });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The change request could not be reinstated.");
        return;
      }
      await load();
      await onChanged();
      setSaved("Back off the shelf.");
    }, "The change request could not be reinstated.");
  };

  const revise = async () => {
    if (!scr) return;
    const next = await withBusy(async () => {
      const response = await fetch(`${api}/api/change-requests/${scr.id}/next-revision`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedVersion: scr.version }),
      });
      if (!response.ok) {
        const value = (await response.json()) as { error?: string };
        setError(value.error || "The next controlled revision could not be created.");
        return undefined;
      }
      const created = (await response.json()) as { id: string };
      await onChanged();
      return created;
    }, "The next controlled revision could not be created.");
    if (next) onOpenScr(next.id);
  };

  const updateRequirement = (
    index: number,
    key: keyof DraftRequirement,
    value: string | number | boolean | string[],
  ) =>
    setRequirements((items) =>
      items.map((item, position) => {
        if (position !== index) return item;
        return { ...item, [key]: value } as DraftRequirement;
      }),
    );

  const nextIdentifier = (level: RequirementLevel) => {
    const prefix = prefixFor(level);
    const start = context?.requirementNumbers[prefix];
    if (!start) return "";
    const startNumber = Number(start.split("-")[1]);
    const reserved = requirements.filter((item) => {
      if (item.kind !== "Introduce" || prefixFor(item.level) !== prefix) return false;
      const value = Number(item.baseNumber.split("-")[1]);
      return Number.isFinite(value) && value >= startNumber;
    }).length;
    return addToIdentifier(start, reserved);
  };

  const addProposal = (kind: RequirementKind, level: RequirementLevel) =>
    setRequirements((items) => [
      ...items,
      createRequirement(level, kind, kind === "Introduce" ? nextIdentifier(level) : "", defaultVerificationMethod),
    ]);

  // What a proposal does to a requirement, changed after the card exists. Not a field update: the kind decides
  // what the identifier means, so the identity is re-derived rather than carried across. Same rule as the new
  // change request editor, and here for the same reason — an author editing a checked-out Draft changes their
  // mind about a proposal as readily as one writing it for the first time.
  const changeRequirementKind = (index: number, kind: RequirementKind) => {
    const target = requirements[index];
    // #701: as in the new change request editor. A blank retirement becoming an Introduce or a Modify is the
    // same act as adding a verification-bearing proposal, and is refused in the same states.
    const decision = target
      ? decideKindChange(verification, { level: target.level, toKind: kind, currentMethod: target.verificationMethod })
      : ({ allowed: false, reason: "" } as const);
    if (!decision.allowed) {
      setError(decision.reason);
      return;
    }
    setRequirements((items) =>
      items.map((item, position) => {
        if (position !== index || item.kind === kind) return item;
        return {
          ...item,
          kind,
          baseNumber: kind === "Introduce" ? nextIdentifier(item.level) : "",
          revision: 0,
          statement: kind === "Retire" ? "" : item.statement,
          richText: kind === "Retire" ? "" : item.richText,
          verificationMethod: decision.verificationMethod,
        };
      }),
    );
  };

  const saveWorkingCopy = async () => {
    setError("");
    setSaved("");
    const current = await autosave();
    if (current) setSaved("Working copy saved. Checkout remains active.");
  };

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!scr || !lockRef.current) return;
    // Matches the button beside it, and says which of the two is missing rather than naming both every time.
    if (!draft.title.trim()) {
      setError("Give the change request a title before checking it in.");
      return;
    }
    await withBusy(async () => {
      while (savingRef.current)
        await new Promise((resolve) => window.setTimeout(resolve, 25));
      const current = await autosave();
      if (!current) {
        setError("The latest recovery snapshot could not be saved for check-in.");
        return;
      }
      savingRef.current = true;
      let response: Response;
      try {
        response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/check-in`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            expectedVersion: current.version,
          }),
        });
      } finally {
        // Its own finally: a throw here used to leave this set, and every later autosave then waited on a
        // save that had already failed.
        savingRef.current = false;
      }
      if (!response.ok) {
        const value = (await response.json()) as { error?: string };
        setError(value.error || "Draft could not be saved.");
        return;
      }
      setLock(undefined);
      resumedWorkingCopyRef.current = false;
      checkoutSnapshotRef.current = "";
      setMode("view");
      await load();
      await onChanged();
      setSaved("Draft checked in.");
    }, "Draft could not be saved.");
  };

  const move = (index: number, direction: number) =>
    setApprovers((items) => {
      const target = index + direction;
      if (target < 0 || target >= items.length) return items;
      const next = [...items];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });

  if (!scr && loadFailure) return <main className="scrLoading missingControlledRecord">
    <section>
      <p className="eyebrow">CHANGE REQUEST DETAILS</p>
      <h1>No change request record is available</h1>
      <p>{loadFailure}</p>
      <p>This is where the details of the change request that introduced or modified this requirement would be shown.</p>
    </section>
  </main>;
  if (!scr) return <main className="scrLoading">Loading controlled record…</main>;

  const latest = [...scr.reviewCycles].sort((a, b) => b.sequence - a.sequence)[0];
  const frozenTraceCycles = scr.reviewCycles.filter((cycle) => cycle.snapshotContractVersion === 3 && cycle.snapshotJson);
  const active =
    latest?.steps.find((step) => step.state === "Active" && step.approverId === user.userName) ??
    latest?.steps.find((step) => step.state === "Active");
  const isAuthor = scr.authorId === user.userName || user.isAdministrator;
  // Anyone holding a step on the open cycle may write, not only whoever is active. A later reviewer reads
  // ahead, and refusing them somewhere to put it only means the observation arrives by some other route.
  const canComment = scr.state === "InReview" && latest?.state === "Active"
    && (latest?.steps ?? []).some((step) => step.approverId.toLowerCase() === user.userName.toLowerCase());
  const targetRelease = releases.find((item) => item.id === scr.targetReleaseId);
  const inheritedAnswer = inheritedTraceAnswer(scr.inheritedUpstream?.inheritedUpstreamContextJson);
  const upstreamById = new Map((scr.upstream ?? []).map((link) => [link.upstreamChangeRequestId, link]));
  // Signed for, whichever of the two stored states it sits in. See StartNextRevision in the domain for why
  // both count, and why a released target build takes the action away again.
  const isSignedFor = scr.state === "Approved" || scr.state === "SelectedForBaseline";
  const revisable = isSignedFor && targetRelease?.isReleased === false;
  const scrFacts = { state: scr.state, deferredFromState: scr.deferredFromState, targetRelease };
  // People with a stake in this review: whoever wrote it, anybody it is waiting on, and whoever is
  // accountable for the Program. Deliberately not everyone with access — halting a review one has no part in
  // should not be an accident anybody can have. The server enforces the same set.
  const canCancelReview = scr.state === "InReview" && (
    user.isAdministrator ||
    scr.authorId.toLowerCase() === user.userName.toLowerCase() ||
    (latest?.steps ?? []).some((step) => step.approverId.toLowerCase() === user.userName.toLowerCase()) ||
    user.programs.some((membership) => membership.roles.includes("ProgramManager")));
  // Any state can go on the shelf except one already picked into a candidate baseline, which has to be taken out
  // of it first — an explicit, attributable act rather than a side effect of deferring. A released build's work is
  // history and cannot be shelved either.
  const deferrable = isAuthor && scr.state !== "Deferred" && scr.state !== "SelectedForBaseline"
    && targetRelease?.isReleased === false;
  // Two verbs, split on whether anybody was ever asked. Deleting is offered only for a draft with no review
  // history, because removing the evidence that an approval happened is worse than the problem it solves.
  // Everything else is withdrawn, and the server refuses either one for a frozen or released build — the
  // refusal names the way out rather than this hiding a control the reader would have been right to want.
  const deletable = isAuthor && scr.state === "Draft" && scr.reviewCycles.length === 0;
  const withdrawable = isAuthor && !deletable && scr.state !== "Withdrawn"
    && targetRelease?.isReleased === false;
  const caseComplete = [draft.title, draft.problem, draft.analysis, draft.solution].every((value) =>
    value.trim(),
  );
  const proposalsComplete = requirements.length > 0 && requirements.every(proposalComplete);
  const localTraceAnswerComplete = upstreamAnswerComplete || upstreamCandidatesTop || derivedUpstreamEdges.length > 0
    || (draft.upstreamLinks ?? []).length > 0 || Boolean(draft.noUpstreamRationale?.trim())
    || draft.upstreamAnswerAffirmed === true;
  const reviewReady = caseComplete && proposalsComplete && requirements.length > 0 && localTraceAnswerComplete;
  const hasUnsavedChanges = mode === "edit" && serializedWorkingCopy !== lastSavedRef.current;
  // An unfinished proposal no longer blocks a check-in. `SystemChangeRequest.ValidateReadyForReview` now
  // carries the completeness the aggregate used to demand on every Draft write, so a proposal can rest
  // half-written and still cannot be put in front of an approver.
  const draftCanCheckIn = Boolean(draft.title.trim());
  const uniqueApprovers = new Set(approvers.map((item) => item.userId).filter(Boolean));
  const selectedApproverCount = uniqueApprovers.size;
  const reviewerSetupValid =
    approvers.length > 0 &&
    approvers.every((item) => item.userId) &&
    uniqueApprovers.size === approvers.length;

  const openReviewerSetup = () => {
    setApprovers([]);
    setReviewMode("Sequential");
    setMode("approvers");
    setFallbackApproverOnly(null);
    setApplicableWorkflow(null);
    void (async () => {
      try {
        const subject = scr.type === "Software" ? "Software" : scr.type === "Interface" ? "Interface" : "System";
        const response = await fetch(
          `${api}/api/review-workflows/applicable?projectId=${scr.projectId}&type=${subject}`);
        if (!response.ok) return;
        const body = (await response.json()) as ApplicableWorkflow;
        setApplicableWorkflow(body);
        setFallbackApproverOnly(body.required === false);
        if (body.required) {
          setApprovers((body.stages ?? []).map(() => ({ userId: "", name: "" })));
          if (body.mode === "Parallel" || body.mode === "Sequential") setReviewMode(body.mode);
        }
      } catch {
        // Unknown stays unfiltered; the server refuses ineligible selections with a clear message.
      }
    })();
  };

  return (
    <ControlledChangePage
      backLabel={`${scr.type === "Software" ? "Software" : scr.type === "Interface" ? "Interface / ICD" : "System"} Change Requests`}
      onBack={onBack}
      eyebrow={`CHANGE CONTROL / ${scr.displayNumber}`}
      title={scr.title}
      description="Revision-controlled change case, requirement proposals, and review authority."
      allocation={changeRequestAllocation(scrFacts)}
      state={changeRequestState(scrFacts)}
      stateCode={scr.state}
      version={scr.version}
      docxHref={`${api}/api/change-requests/${scr.id}/download?format=docx`}
      pdfHref={`${api}/api/change-requests/${scr.id}/download?format=pdf`}
      error={error}
      saved={saved}
    >

      {mode === "edit" ? (
        <ControlledChangeAuthoringForm
          onSubmit={save}
          stages={[
            { href: "#checked-change-case", label: "Change case", status: caseComplete ? "Complete" : "Required", complete: caseComplete, active: !caseComplete },
            { href: "#checked-requirements", label: "Requirement changes", status: proposalsComplete ? "Complete" : "In progress", complete: proposalsComplete, active: caseComplete && !proposalsComplete },
          ]}
          actions={<ControlledChangeAuthoringActions
            summary={reviewReady ? "Ready for review after check-in" : "Draft can be checked in before review readiness"}
            detail={hasUnsavedChanges ? "Working copy has unsaved changes" : `Working copy: ${autosaveStatus.toLowerCase()}`}
            busy={busy}
            saving={autosaveStatus === "Saving"}
            // The same rule as the test change request page. Save stays available while checked out, and
            // `hasCheckoutChanges` is gone so an untouched checkout can be handed back rather than only
            // discarded. The proposal minimum stays, because check-in applies the draft to the aggregate and
            // a half-created proposal is rejected there as a 400 — but it now says so instead of greying in
            // silence.
            canSave={autosaveStatus !== "Conflict"}
            canCheckIn={autosaveStatus !== "Conflict" && draftCanCheckIn}
            checkInBlockedReason={autosaveStatus === "Conflict"
              ? "Another edit reached this change request first. Reload to see it before checking in."
              : "Give the change request a title before checking it in."}
            onDiscard={() => void discard()}
            onSave={() => void saveWorkingCopy()}
          />}
        >

          <section className="workspaceCard authoringCard" id="checked-change-case">
            <div className="workspaceTitle">
              <div>
                <span className="stageKicker">STAGE 1</span>
                <h2>Change case</h2>
                <p>Keep the decision context concise, complete, and attributable.</p>
              </div>
              <div className={`autosaveState ${autosaveStatus.toLowerCase()}`}>
                <i />{autosaveStatus}
                {lock && <small>Lock expires {new Date(lock.expiresAt).toLocaleTimeString()}</small>}
              </div>
            </div>
            <div className="checkoutBanner">
              <b>Checked out by {user.displayName}</b>
              <span>Opened {lock && new Date(lock.openedAt).toLocaleString()} · other users remain read-only</span>
            </div>
            <div className="editFields">
              <label>
                Title
                <input name="title" value={draft.title} onChange={(event) => setDraft((value) => ({ ...value, title: event.target.value }))} required />
              </label>
              <RichCaseField api={api} projectId={scr.projectId} label="Problem" value={draft.problemRich}
                placeholder="What need, defect, or risk exists?" required={false}
                onChange={(value) => setDraft((current) => ({ ...current, problemRich: value, problem: toPlainText(value) }))} />
              <RichCaseField api={api} projectId={scr.projectId} label="Analysis" value={draft.analysisRich}
                placeholder="What is affected and what alternatives were considered?" required={false}
                onChange={(value) => setDraft((current) => ({ ...current, analysisRich: value, analysis: toPlainText(value) }))} />
              <RichCaseField api={api} projectId={scr.projectId} label="Solution" value={draft.solutionRich}
                placeholder="What controlled outcome is proposed?" required={false}
                onChange={(value) => setDraft((current) => ({ ...current, solutionRich: value, solution: toPlainText(value) }))} />
            </div>
            <ProblemReportPicker api={api} projectId={scr.projectId} scope="target-build" releaseId={scr.targetReleaseId}
              selected={problemReportIds} onChange={setProblemReportIds}
              legend={`PRs driving this ${artifactAcronym(scr.displayNumber, "changeRequest")} (optional)`} />
            <section className="upstreamAnswerEditor" aria-labelledby="upstream-answer-title">
              <div className="workspaceTitle">
                <div>
                  <span className="stageKicker">TRACE ANSWER</span>
                  <h3 id="upstream-answer-title">Upstream change requests</h3>
                  <p>Choose exact direct-parent change requests, or explain why none applies. The server revalidates every choice at check-in.</p>
                </div>
              </div>
              {scr.inheritedUpstream && !draft.upstreamAnswerAffirmed && (
                <div className="inheritedTraceAnswer">
                  <b>Inherited from the predecessor revision</b>
                  {inheritedAnswer.links?.map((link, index) => <p key={`${link.upstreamChangeRequestId ?? link.UpstreamChangeRequestId ?? "link"}-${index}`}>
                    {link.upstreamDisplayNumber ?? link.UpstreamDisplayNumber ?? link.upstreamChangeRequestId ?? link.UpstreamChangeRequestId ?? "Named upstream"}
                    {(link.upstreamBuildVersion ?? link.UpstreamBuildVersion) ? ` · upstream build ${link.upstreamBuildVersion ?? link.UpstreamBuildVersion}` : ""}
                    {(link.rationale ?? link.Rationale) ? ` · ${link.rationale ?? link.Rationale}` : ""}
                  </p>)}
                  {inheritedAnswer.noUpstreamRationale && <p>No upstream: {inheritedAnswer.noUpstreamRationale}</p>}
                  <button type="button" onClick={() => setDraft((value) => ({ ...value, upstreamAnswerAffirmed: true }))}>
                    Affirm this inherited answer
                  </button>
                </div>
              )}
              {!upstreamCandidatesTop && (
                <>
                  <label><input type="checkbox" checked={includeEarlierBuilds} onChange={(event) => setIncludeEarlierBuilds(event.target.checked)} /> Include earlier builds (only signed, exact upstream revisions are eligible)</label>
                  <label>
                    Find a direct parent
                    <input value={upstreamSearch} onChange={(event) => setUpstreamSearch(event.target.value)} placeholder="Search number or title" />
                  </label>
                  <div className="upstreamCandidateList">
                    {upstreamCandidates.filter((candidate) => !draft.upstreamLinks?.some((link) => link.upstreamChangeRequestId === candidate.id))
                      .filter((candidate) => !candidate.assessmentDerived).map((candidate) => (
                        <button type="button" key={candidate.id}
                          onClick={() => {
                            if (draft.noUpstreamRationale && !window.confirm("Replace the authored no-upstream answer with a named upstream link?")) return;
                            setDraft((value) => ({ ...value, noUpstreamRationale: null,
                              upstreamLinks: [...(value.upstreamLinks ?? []), { upstreamChangeRequestId: candidate.id, rationale: "" }] }));
                          }}>
                          {candidate.displayNumber} · {candidate.title} (current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {candidate.build}{candidate.earlierBuild ? ", earlier build" : ""})
                        </button>
                      ))}
                  </div>
                </>
              )}
              {derivedUpstreamEdges.length > 0 && <div className="snapshotNote"><b>Assessment-derived upstream edges (read-only)</b>{derivedUpstreamEdges.map((edge) => <p key={edge.assessmentLinkId}>{edge.upstreamDisplayNumber} · current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {edge.upstreamBuildVersion || edge.upstreamBuildId} · assessment {edge.assessmentId} · link {edge.assessmentLinkId}</p>)}</div>}
              {(draft.upstreamLinks ?? []).map((link) => {
                const candidate = upstreamCandidates.find((value) => value.id === link.upstreamChangeRequestId);
                const stored = upstreamById.get(link.upstreamChangeRequestId);
                const displayNumber = candidate?.displayNumber ?? stored?.upstreamDisplayNumber ?? link.upstreamChangeRequestId;
                const upstreamBuild = candidate?.build ?? stored?.upstreamBuildVersion ?? "unknown";
                return <div className="upstreamDraftRow" key={link.upstreamChangeRequestId}>
                  <b>{displayNumber}<small>current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {upstreamBuild}</small></b>
                  <input aria-label={`Rationale for ${displayNumber}`} value={link.rationale}
                    onChange={(event) => setDraft((value) => ({ ...value, upstreamLinks: (value.upstreamLinks ?? []).map((item) => item.upstreamChangeRequestId === link.upstreamChangeRequestId ? { ...item, rationale: event.target.value } : item) }))}
                    placeholder="Why is this exact change request upstream?" />
                  <button type="button" onClick={() => setDraft((value) => ({ ...value, upstreamLinks: (value.upstreamLinks ?? []).filter((item) => item.upstreamChangeRequestId !== link.upstreamChangeRequestId) }))}>Remove</button>
                </div>;
              })}
              {!upstreamCandidatesTop && derivedUpstreamEdges.length === 0 && (draft.upstreamLinks ?? []).length === 0 && (
                <label>
                  No upstream change-request rationale
                  <textarea value={draft.noUpstreamRationale ?? ""}
                    onChange={(event) => setDraft((value) => ({ ...value, noUpstreamRationale: event.target.value || null }))}
                    placeholder="Explain why no direct upstream change request applies." />
                </label>
              )}
              {upstreamCandidatesTop && <p className="muted">This level is at the top of the configured ladder; its upstream answer is derived.</p>}
            </section>
          </section>

          <section className="workspaceCard authoringCard" id="checked-requirements">
            <div className="workspaceTitle">
              <div>
                <span className="stageKicker">STAGE 2</span>
                <h2>Controlled requirement authoring</h2>
                <p>One shared editor for requirement content and classification.</p>
              </div>
              <span className={proposalsComplete ? "completionBadge complete" : "completionBadge"}>
                {proposalsComplete ? "Complete" : `${requirements.length} proposal${requirements.length === 1 ? "" : "s"}`}
              </span>
            </div>
            <div className="workspaceProposalActions">
              <span>Add proposal</span>
              {scr.type === "System" ? (
                <>
                  <button type="button" disabled={!context || verificationBlocked} onClick={() => addProposal("Introduce", "System")}>+ Introduce System requirement</button>
                  <button type="button" disabled={verificationBlocked} onClick={() => addProposal("Modify", "System")}>Modify existing</button>
                  <button type="button" onClick={() => addProposal("Retire", "System")}>Retire existing</button>
                </>
              ) : scr.type === "Interface" ? (
                <>
                  <button type="button" disabled={!context} onClick={() => addProposal("Introduce", "Interface")}>+ Introduce Interface / ICD requirement</button>
                  <button type="button" onClick={() => addProposal("Modify", "Interface")}>Modify existing Interface / ICD</button>
                  <button type="button" onClick={() => addProposal("Retire", "Interface")}>Retire existing Interface / ICD</button>
                </>
              ) : (
                <>
                  <button type="button" disabled={!context || verificationBlocked} onClick={() => addProposal("Introduce", "HighLevel")}>+ Introduce HLR</button>
                  <button type="button" disabled={!context || verificationBlocked} onClick={() => addProposal("Introduce", "LowLevel")}>+ Introduce LLR</button>
                  <button type="button" disabled={verificationBlocked} onClick={() => addProposal("Modify", "HighLevel")}>Modify existing HLR</button>
                  <button type="button" onClick={() => addProposal("Retire", "HighLevel")}>Retire existing HLR</button>
                  <button type="button" disabled={verificationBlocked} onClick={() => addProposal("Modify", "LowLevel")}>Modify existing LLR</button>
                  <button type="button" onClick={() => addProposal("Retire", "LowLevel")}>Retire existing LLR</button>
                </>
              )}
              {verificationBlocked && <span className="proposalUnavailable" role={verification.error ? "alert" : "status"}>{verificationBlockedReason(verification)}</span>}
            </div>
            {requirements.map((item, index) => (
              <ControlledRequirementEditor
                api={api}
                projectId={scr.projectId}
                releaseId={scr.targetReleaseId}
                scope={scr.type}
                item={item}
                index={index}
                key={`${index}-${item.kind}`}
                identityLocked={Boolean(item.baseNumber)}
                verification={verification}
                onChange={(key, value) => updateRequirement(index, key, value)}
                onKindChange={(kind) => changeRequirementKind(index, kind)}
                onRemove={() => setRequirements((items) => items.filter((_, position) => position !== index))}
              />
            ))}
            {!requirements.length && (
              <div className="workspaceEmptyState">
                <b>No requirement proposals</b>
                <p>Add the smallest controlled set needed to deliver this change.</p>
              </div>
            )}
          </section>

        </ControlledChangeAuthoringForm>
      ) : mode === "approvers" ? (
        <section className="workspaceCard approverSetup">
          <div className="reviewSetupIntro">
            <span>FINAL HANDOFF</span>
            <h2>Configure review authority</h2>
            <p>Select only the people who have decision authority for this exact controlled snapshot.</p>
            <div><b>{scr.displayNumber}</b><span>{requirements.length} requirement proposal{requirements.length === 1 ? "" : "s"} ready for review</span></div>
            {applicableWorkflow?.required && <p><b>{applicableWorkflow.name} v{applicableWorkflow.version}</b> is the active policy for this submission. Its configured rows are the minimum; extra active Program participants may be added.</p>}
          </div>
          {applicableWorkflow?.required ? (
            <div className="reviewModePolicy" role="status">
              <b>{reviewMode} review mode</b>
              <span>Set by {applicableWorkflow.name} v{applicableWorkflow.version}; authors cannot change the active policy.</span>
            </div>
          ) : (
            <div className="reviewModePicker">
              <button type="button" className={reviewMode === "Sequential" ? "active" : ""} onClick={() => setReviewMode("Sequential")}>
                <b>Sequential</b><span>Activate one reviewer at a time in this order.</span>
              </button>
              <button type="button" className={reviewMode === "Parallel" ? "active" : ""} onClick={() => setReviewMode("Parallel")}>
                <b>Parallel</b><span>Activate all reviewers when review begins.</span>
              </button>
            </div>
          )}

          {!approvers.length && (
            <div className="reviewerEmpty">
              <span>0</span>
              <div><b>No reviewers selected</b><p>Start with the minimum accountable review authority; no identities are prefilled.</p></div>
            </div>
          )}
          {approvers.map((person, index) => (
            <div className="approverRow" key={index}>
              <span>{reviewMode === "Sequential" ? index + 1 : "•"}</span>
              {applicableWorkflow?.required && index < (applicableWorkflow.stages ?? []).length ? (() => {
                const stage = applicableWorkflow.stages![index];
                return <label className="configuredApproverSelect">
                  <span className="srOnly">{stage.name} · {stage.kind ?? 'Review'} · {stage.requiredRole}</span>
                  <select value={person.userId} aria-label={`${stage.name} · ${stage.kind ?? 'Review'} · ${stage.requiredRole}`} onChange={event => {
                    const selected = stage.candidates.find(candidate => candidate.userId === event.target.value);
                    setApprovers(items => items.map((item, position) => position === index
                      ? { userId: event.target.value, name: selected?.name ?? "" } : item));
                  }}>
                    <option value="">Choose {stage.requiredRole} for {stage.name} ({stage.kind ?? 'Review'})…</option>
                    {stage.candidates.map(candidate => <option value={candidate.userId} key={candidate.userId}>{candidate.name} · {candidate.role}</option>)}
                  </select>
                </label>;
              })() : <PersonPicker
                api={api}
                projectId={scr.projectId}
                value={person.userId}
                name={person.name}
                index={index}
                allowedRoles={fallbackApproverOnly === true ? ["Approver", "Administrator"] : undefined}
                onSelect={(selected) =>
                  setApprovers((items) =>
                    items.map((item, position) => (position === index ? selected : item)),
                  )
                }
              />}
              {!(applicableWorkflow?.required && index < (applicableWorkflow.stages ?? []).length) && <>
                <button type="button" aria-label={`Move approver ${index + 1} up`} disabled={reviewMode === "Parallel" || index <= (applicableWorkflow?.minimum ?? 0)} onClick={() => move(index, -1)}>↑</button>
                <button type="button" aria-label={`Move approver ${index + 1} down`} disabled={reviewMode === "Parallel" || index === approvers.length - 1} onClick={() => move(index, 1)}>↓</button>
                <button type="button" className="remove" onClick={() => setApprovers((items) => items.filter((_, position) => position !== index))}>Remove</button>
              </>}
            </div>
          ))}
          <button type="button" className="outline addApprover" onClick={() => setApprovers((items) => [...items, { userId: "", name: "" }])}>+ Add {applicableWorkflow?.required ? "extra signer" : "approver"}</button>
          {uniqueApprovers.size !== approvers.filter((item) => item.userId).length && (
            <div className="reviewerWarning">Each reviewer may appear only once.</div>
          )}
          {fallbackApproverOnly === true && (
            <div className="reviewerWarning">
              No review workflow is configured for this discipline. Only users holding Approver authority can
              review; the picker is filtered to them.
            </div>
          )}
          {applicableWorkflow?.required && (
            <div className="reviewerWarning">
              <b>{applicableWorkflow.name} v{applicableWorkflow.version}</b> requires the configured rows above in order.
              Additional distinct active Program participants are allowed and remain part of this review cycle.
            </div>
          )}
          <div className="snapshotNote">
            <b>Snapshot protection</b>
            <p>Submission freezes the exact content hash. Each activated reviewer receives a My Work deep link and must re-authenticate to sign.</p>
          </div>
          <div className="workspaceActions reviewerActions">
            <div><b>{selectedApproverCount} reviewer{selectedApproverCount === 1 ? "" : "s"} selected</b><span>{reviewMode} authority path</span></div>
            <button type="button" className="outline" onClick={() => setMode("view")}>Cancel</button>
            <button type="button" disabled={busy || !reviewerSetupValid || !reviewReady} onClick={() => void call("submit", { expectedVersion: scr.version, approvers, mode: reviewMode })}>
              {busy ? "Submitting…" : "Submit for Review"}
            </button>
          </div>
        </section>
      ) : (
        <ControlledChangeReadLayout>
          <div className="workspaceStack">
            {/* Served by the server rather than worked out here: whether a revision still exists is not a
                question the browser should be answering on its own. */}
            {scr.rebaseRequiredReason && (
              <div className="rebaseRequiredNotice" data-testid="rebase-required">
                <b>The build this was written against was reopened</b>
                <span>{scr.rebaseRequiredReason}</span>
              </div>
            )}
            <ControlledChangeCaseCard
              actions={<>
                {digitalThreadHref && (
                  <a className="outline" href={digitalThreadHref}>Open Digital Thread →</a>
                )}
                {scr.state === "Draft" && isAuthor && (
                  <button className="outline" type="button" disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>
                    {busy ? "Checking lock…" : lockStatus?.locked && !lockStatus.mine ? `Read only · ${personLabel(lockStatus.holder)}` : "Check out & edit"}
                  </button>
                )}
                {revisable && isAuthor && (
                  <button className="reviseAction" type="button" disabled={busy} onClick={revise}
                    title={`Creates ${scr.baseNumber}.${String(scr.revision + 1).padStart(2, "0")} as a Draft. This approved revision stays unchanged.`}>
                    {busy ? "Creating revision…" : "Revise"}
                  </button>
                )}
                {deferrable && (
                  <button className="deferAction" type="button" disabled={busy} onClick={defer}
                    title="Puts this change request away for another day. Its state is remembered.">
                    {busy ? "Deferring…" : "Defer"}
                  </button>
                )}
                {scr.state === "Deferred" && isAuthor && (
                  <button className="reviseAction" type="button" disabled={busy} onClick={reinstate}
                    title={scr.deferredFromState === "InReview"
                      ? "Comes back as a Draft: the review was cancelled when it was deferred."
                      : `Comes back as ${stateLabel(scr.deferredFromState ?? "Draft")}.`}>
                    {busy ? "Reinstating…" : "Reinstate"}
                  </button>
                )}
                {withdrawable && (
                  <button className="withdrawAction" type="button" disabled={busy} onClick={withdraw}
                    title="Stops pursuing this change request. The record, its review history and its signatures stay readable.">
                    {busy ? "Withdrawing…" : "Withdraw"}
                  </button>
                )}
                {deletable && (
                  <button className="withdrawAction" type="button" disabled={busy} onClick={remove}
                    title="Nobody has reviewed this, so there is nothing to keep a record of. It goes entirely.">
                    {busy ? "Deleting…" : "Delete"}
                  </button>
                )}
              </>}
              note={lockStatus?.locked && !lockStatus.mine
                ? <div className="readOnlyLock"><b>Read-only while checked out</b><span><PersonName userName={lockStatus.holder} /> · active {lockStatus.lastActivityAt && new Date(lockStatus.lastActivityAt).toLocaleString()} · expires {lockStatus.expiresAt && new Date(lockStatus.expiresAt).toLocaleTimeString()}</span></div>
                : undefined}
              fields={[
                { key: "P", label: "Problem", value: <RichContentView api={api} value={scr.problemRich || fromPlainText(scr.problem)} empty="Not yet provided" /> },
                { key: "A", label: "Analysis", value: <RichContentView api={api} value={scr.analysisRich || fromPlainText(scr.analysis)} empty="Not yet provided" /> },
                { key: "S", label: "Solution", value: <RichContentView api={api} value={scr.solutionRich || fromPlainText(scr.solution)} empty="Not yet provided" /> },
              ]}
            />

            <ReviewCommentBlock store={comments} anchor="ChangeCase" canComment={canComment} label="the change case" />

            {/* Arrives only when the server flagged the redirect, which it does solely for somebody who held
                a step on a cycle that has since closed. */}
            <ReviewEndedNotice
              currentState={stateLabel(scr.state)}
              outcome={latest && latest.state !== "Active"
                ? { state: latest.state, completedAt: latest.completedAt, closureReason: latest.closureReason }
                : undefined}
            />

            <section className="workspaceCard" aria-labelledby="trace-answer-title">
              <div className="workspaceTitle"><div><h2 id="trace-answer-title">Upstream trace answer</h2><p>Authored links and the evidence frozen when this change request entered review.</p></div></div>
              {(scr.upstream ?? []).length > 0 ? <div className="upstreamReferenceList">
                {(scr.upstream ?? []).map((link) => <article className="upstreamReference" key={link.id}>
                  <div><b>{link.upstreamDisplayNumber}</b><span>current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {link.upstreamBuildVersion}</span></div>
                  <p>{link.rationale}</p>
                  <small>Stated by <PersonName userName={link.actor} /> · {new Date(link.statedAt).toLocaleString()}</small>
                </article>)}
              </div> : scr.noUpstream ? <div className="snapshotNote"><b>No direct upstream change request</b><p>{scr.noUpstream.rationale}</p><small>Stated by <PersonName userName={scr.noUpstream.actor} /> · {new Date(scr.noUpstream.statedAt).toLocaleString()}</small></div>
                : upstreamCandidatesTop ? <div className="snapshotNote"><b>Top of ladder</b><p>No upstream change request applies under this Project's effective ladder.</p></div>
                : scr.state === "Draft"
                  ? <p className="muted">No upstream answer has been authored yet. Complete it before review.</p>
                  : <p className="muted">No authored upstream answer is recorded; this is a historical record from before trace authoring.</p>}
              {scr.inheritedUpstream && <div className="snapshotNote"><b>Revision context</b><p>Inherited from {scr.inheritedUpstream.inheritedFromChangeRequestId ?? "the predecessor revision"}; {scr.inheritedUpstream.affirmed ? "affirmed" : "not yet affirmed"}.</p></div>}
              {(scr.upstreamHistory ?? []).length > 0 && <details className="traceHistory"><summary>Answer history ({scr.upstreamHistory!.length})</summary>
                {scr.upstreamHistory!.map((entry) => <div className="auditRow" key={entry.id}><i /><div><b>{entry.action}</b><p>{entry.upstreamDisplayNumber ?? "No-upstream answer"}{entry.upstreamBuildVersion ? ` · upstream build ${entry.upstreamBuildVersion}` : ""}{entry.rationale ? ` · ${entry.rationale}` : ""}</p><small><PersonName userName={entry.actor} /> · {new Date(entry.occurredAt).toLocaleString()}</small></div></div>)}
              </details>}
              {frozenTraceCycles.length > 0 && <details className="traceHistory" open>
                <summary>Frozen review evidence ({frozenTraceCycles.length} snapshot{frozenTraceCycles.length === 1 ? "" : "s"})</summary>
                {frozenTraceCycles.map((cycle) => {
                  const snapshot = parseObject(cycle.snapshotJson) as FrozenTraceSnapshot;
                  return <article className="snapshotNote" key={cycle.id}>
                    <b>Review cycle {cycle.sequence} · contract v{cycle.snapshotContractVersion} · {cycle.snapshotHash}</b>
                    <p>{snapshot.isTopOfLadder ? "Top-of-ladder upstream state." : "Resolved upstream state."}</p>
                    {(snapshot.authoredLinks ?? []).map((link, index) => <small key={`authored-${index}`}>Frozen authored link: {link.upstreamDisplayNumber ?? link.UpstreamDisplayNumber ?? link.upstreamChangeRequestId ?? link.UpstreamChangeRequestId ?? "upstream"} · current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {link.upstreamBuildVersion ?? link.UpstreamBuildVersion ?? "unknown"} · {link.rationale ?? link.Rationale ?? "No rationale"}</small>)}
                    {snapshot.noUpstreamRationale && <small>Frozen no-upstream rationale: {snapshot.noUpstreamRationale}</small>}
                    {(snapshot.derivedLinks ?? []).map((edge, index) => {
                      const assessmentId = edge.assessmentId ?? edge.AssessmentId;
                      const assessmentLinkId = edge.assessmentLinkId ?? edge.AssessmentLinkId;
                      const isAbsentFromLiveAssessment = assessmentId && assessmentLinkId
                        ? !derivedUpstreamEdges.some((live) => live.assessmentId === assessmentId && live.assessmentLinkId === assessmentLinkId)
                        : false;
                      return <small key={`${assessmentLinkId ?? "edge"}-${index}`}>Derived edge: {edge.upstreamDisplayNumber ?? edge.UpstreamDisplayNumber ?? edge.upstreamChangeRequestId ?? edge.UpstreamChangeRequestId ?? "upstream"} · assessment {assessmentId ?? "unknown"} · link {assessmentLinkId ?? "unknown"}{isAbsentFromLiveAssessment ? " · frozen review evidence; no longer present in the live assessment (reopened/corrected)" : ""}</small>;
                    })}
                  </article>;
                })}
              </details>}
              {derivedUpstreamEdges.length > 0 && <div className="snapshotNote"><b>Live assessment-derived edges</b>{derivedUpstreamEdges.map((edge) => <p key={edge.assessmentLinkId}>{edge.upstreamDisplayNumber} · current build {targetRelease?.version ?? scr.targetReleaseId} → upstream build {edge.upstreamBuildVersion || edge.upstreamBuildId} · assessment {edge.assessmentId} · link {edge.assessmentLinkId}</p>)}</div>}
            </section>

            {drivingProblemReports.length > 0 && (
              <section className="workspaceCard">
                <div className="workspaceTitle"><div><h2>Driving Problem Reports</h2><p>The problem records that authorized this engineering response</p></div></div>
                {drivingProblemReports.map((report) => (
                  <button type="button" className="requirementView artifactReferenceCard" key={report.id} onClick={()=>onOpenProblemReport(report.id)}>
                    <div><b>{report.displayNumber}</b><span>{stateLabel(report.state)}</span></div>
                    <p>{report.title}</p>
                    <footer><small>Linked as a proposed corrective action</small><em>Open controlled PR →</em></footer>
                  </button>
                ))}
              </section>
            )}

            <ChangeRequestJiraLink api={api} changeRequestId={scr.id} displayNumber={scr.displayNumber} />

            <section className="workspaceCard">
              <div className="workspaceTitle">
                <div><h2>Supporting files</h2><p>Evidence an approver needs alongside the change case</p></div>
              </div>
              {/* Attached where the change is decided, not in a separate vault. The datasheet that justifies
                  a change request belongs beside the change request. */}
              <ControlledAttachments
                api={api}
                projectId={scr.projectId}
                artifactType="ChangeRequest"
                artifactId={scr.id}
                canAttach={scr.state === "Draft" && isAuthor}
              />
            </section>

            <section className="workspaceCard">
              <div className="workspaceTitle"><div><h2>Requirement impact</h2><p>{scr.requirementChanges.length} proposed controlled change{scr.requirementChanges.length === 1 ? "" : "s"}</p></div></div>
              {scr.requirementChanges.map((item) => {
                return (
                  <article className="requirementView" key={item.id}>
                    <div><b>{item.displayNumber}</b><span>{changeKindLabel(item.kind)}</span></div>
                    <p>{item.kind === "Retire" && !item.statement ? "Requirement will be retired." : item.statement}</p>
                    {item.level !== "System" && (
                      <div className="upstreamReferences">
                        {parseObject(item.attributesJson).derived === true
                          ? `Derived exception — ${item.rationale}`
                          : <ExactUpstreamReferences api={api} projectId={scr.projectId} releaseId={scr.targetReleaseId} childLevel={item.level} revisionIds={item.upstreamRevisionIds??[]} onOpen={onOpenRequirement}/>}
                      </div>
                    )}
                    <footer><small>{item.verificationMethod} · {item.rationale || "No rationale recorded"}</small><em>Downstream impact assessed after approval</em></footer>
                    <ReviewCommentBlock store={comments} anchor="RequirementRevision" requirementChangeId={item.id}
                      canComment={canComment} label={item.displayNumber} />
                  </article>
                );
              })}
              <EarlierCycleComments store={comments} />
            </section>

            <section className="workspaceCard">
              <div className="workspaceTitle"><div><h2>Audit history</h2></div></div>
              {scr.audit.map((event, index) => (
                <div className="auditRow" key={`${event.occurredAt}-${index}`}><i /><div><b>{auditEventTitle(event.eventType, targetRelease?.version ?? "")}</b><p>{auditSummary(event)}</p><AuditEvidence event={event} /><small><PersonName userName={event.actorId} /> · {new Date(event.occurredAt).toLocaleString()}</small></div></div>
              ))}
            </section>
          </div>

          <aside className="reviewRail">
            <ControlledStatusCard
              displayNumber={scr.displayNumber}
              fields={[
                { label: "Allocation", value: changeRequestAllocation(scrFacts), data: { name: "allocation", value: scr.state === "Deferred" ? "Deferred" : "Build" } },
                { label: "State", value: changeRequestState(scrFacts), data: { name: "state", value: scr.state } },
                { label: "Author", value: <PersonName userName={scr.authorId} withRole /> },
                { label: "Revision", value: scr.revision },
                { label: "Updated", value: new Date(scr.updatedAt).toLocaleDateString() },
              ]}
            >
              {scr.state === "Draft" && isAuthor && reviewReady && (
                <><div className="railReadiness ready"><b>Ready for review</b><span>The change case, requirement proposals, and upstream trace answer are complete.</span></div><button type="button" className="primaryFull" onClick={openReviewerSetup}>Configure & Submit Review</button></>
              )}
              {scr.state === "Draft" && isAuthor && !reviewReady && (
                <div className="railReadiness"><b>Draft needs authoring</b><span>{!caseComplete ? "Complete the change case." : !proposalsComplete ? "Complete the requirement proposals." : "Complete the upstream trace answer."}</span><button type="button" disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>Complete Draft readiness</button></div>
              )}
              {/* No second Revise button here. The action lives in the Change case header with Check out &
                  edit, so there is one place to act; this only explains what it will do. */}
              {revisable && (
                <p className="snapshotNote">
                  This approved revision is immutable. <b>Revise</b> creates{" "}
                  {scr.baseNumber}.{String(scr.revision + 1).padStart(2, "0")} as a Draft with the same
                  content, and leaves this revision and its signatures untouched.
                </p>
              )}
              {isSignedFor && targetRelease?.isReleased && (
                <p className="snapshotNote">
                  {targetRelease.version} has been released, so this revision is frozen history and cannot be
                  revised. Raise a new change request against the in-work build instead.
                </p>
              )}
            </ControlledStatusCard>

            {latest && (
              <ReviewCycleCard cycle={latest}>
                {scr.state === "InReview" && active && (
                  <div className="reviewActions">
                    <p><b><PersonName userName={active.approverId} displayName={active.approverName} /></b> is the active reviewer.</p>
                    {active.approverId === user.userName ? (
                      <><button type="button" disabled={busy} onClick={() => setSigning(true)}>Review & electronically approve</button><textarea aria-label="Reason for approval" placeholder="Reason for approval (recorded on the review step)" value={approvalRationale} onChange={(event) => setApprovalRationale(event.target.value)} /><textarea aria-label="Reason for requested changes" placeholder="Reason for requested changes" value={reason} onChange={(event) => setReason(event.target.value)} /><button type="button" className="danger" disabled={busy || !reason.trim()} onClick={() => void call("request-changes", { expectedVersion: scr.version, reason })}>Request changes</button></>
                    ) : (
                      <div className="snapshotNote"><b>Waiting for assigned reviewer</b><p>Only the assigned identity can act on this stage.</p></div>
                    )}
                    {/* Stopping the review is not the same decision as rejecting the content, so it does not
                        sit behind the reviewer's controls. An author who submitted too early, or a lead who
                        can see the change is being reworked, previously had to ask the active reviewer to
                        reject work everybody already knew was going to change. */}
                    {canCancelReview && (
                      <button type="button" className="secondary" disabled={busy} onClick={() => void cancelReview()}>Cancel review</button>
                    )}
                  </div>
                )}
              </ReviewCycleCard>
            )}
          </aside>
        </ControlledChangeReadLayout>
      )}

      {signing && (
        <SignatureDialog
          title={`Approve ${scr.displayNumber}`}
          meaning="I approve this exact change request revision and its proposed requirement changes as suitable for controlled progression."
          onCancel={() => setSigning(false)}
          onSign={async (password, meaning) => {
            const ok = await call("approve", { password, meaning, rationale: approvalRationale.trim(), expectedVersion: scr.version });
            if (ok) setSigning(false);
          }}
        />
      )}
    </ControlledChangePage>
  );
}
