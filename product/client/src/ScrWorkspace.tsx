import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { changeRequestAllocation, changeRequestState, stateLabel } from './presentation'
import type { FormEvent } from "react";
import { SignatureDialog } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import ControlledRequirementEditor from "./ControlledRequirementEditor";
import type {
  ControlledRequirementDraft,
  RequirementKind,
  RequirementLevel,
} from "./ControlledRequirementEditor";
import PersonPicker from "./PersonPicker";
import { demoPerson } from "./PeopleRegistry";
import ControlledAttachments from "./ControlledAttachments";
import ScrJiraLink from "./ScrJiraLink";
import { PersonName } from "./People";
import { personLabel } from "./PeopleRegistry";
import { RichCaseField, RichContentView } from "./RichContent";
import { useDebouncedSave } from "./autosave";
import { emptyRichContent, fromPlainText, toPlainText } from "./richContentModel";
import ProblemReportPicker from "./ProblemReportPicker";
import "./ScrWorkspace.css";
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

/// What this approver is to the review: their recorded stage, their authority, or the role they hold here.
///
/// This printed "Authority unresolved" whenever neither of the first two was populated, which on the showcase
/// is always. That is an internal admission that a field is empty, dressed up as a fact about a colleague.
const approverRole = (step: { approverId: string; stageName: string; authority: string }) =>
  step.stageName || step.authority || demoPerson(step.approverId)?.role || "Reviewer";

/// Where an approver stands, said the way somebody waiting on them would say it.
///
/// The old line paired every step with its stored state — "Active", "Pending" — which describes rows in a
/// table, not people in a queue. "Pending" in particular was given to everybody who had not been reached
/// yet, so second-in-line and sixth-in-line read identically, and neither told the reader whose move it was.
///
/// A parallel review has no queue: everyone is being waited on at once, so everyone says so.
const approvalStanding = (cycle: { mode: string; steps: { position: number; state: string }[] }, step: { position: number; state: string }) => {
  if (step.state === "Approved") return "Approved";
  if (cycle.mode === "Parallel" || step.state === "Active") return "Awaiting approval";
  const ahead = cycle.steps.filter((other) => other.position < step.position && other.state !== "Approved").length;
  if (ahead <= 1) return "Next in line for approval";
  return `Waiting on ${ahead} earlier approvals`;
};

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
    .map((word) => (word === "Scr" ? "SCR" : word === "Swcr" ? "SWCR" : word))
    .map((word, index) => (index === 0 || word === word.toUpperCase() ? word : word.toLowerCase()))
    .join(" ");
};

type Step = {
  position: number;
  approverId: string;
  approverName: string;
  authority: string;
  stageName: string;
  state: string;
  decidedAt?: string;
};
type Cycle = {
  id: string;
  sequence: number;
  mode: "Sequential" | "Parallel";
  state: string;
  snapshotHash: string;
  startedAt: string;
  completedAt?: string;
  closureReason?: string;
  steps: Step[];
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
type ScrDetail = {
  id: string;
  baseNumber: string;
  revision: number;
  displayNumber: string;
  projectId: string;
  targetReleaseId: string;
  type: "System" | "Software";
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
};
type AuthoringContext = {
  type: "System" | "Software";
  changeRequestNumber: string;
  author: { userName: string; displayName: string };
  requirementNumbers: Partial<Record<"SYSR" | "HLR" | "LLR", string>>;
};
type Props = {
  api: string;
  scrId: string;
  user: AuthUser;
  onBack: () => void;
  onChanged: () => Promise<void>;
  onOpenScr: (id: string) => void;
  onOpenRequirement: (id: string, level: RequirementLevel) => void;
  onOpenProblemReport: (id: string) => void;
  onDisciplineResolved: (discipline: "system" | "software") => void;
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
  level === "System" ? "SYSR" : level === "HighLevel" ? "HLR" : "LLR";
const parseObject = (value: string | undefined): Record<string, unknown> => {
  try {
    return JSON.parse(value || "{}") as Record<string, unknown>;
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
const createRequirement = (
  level: RequirementLevel,
  kind: RequirementKind,
  baseNumber = "",
): DraftRequirement => ({
  baseNumber,
  revision: 0,
  level,
  kind,
  statement: "",
  rationale: "",
  verificationMethod: "Test",
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
      (item.level === "System" ||
        (item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
        Boolean(item.upstreamRevisionIds?.length)),
  );

// Check-in returns a controlled Draft to the shared record; it is not review submission. It therefore accepts
// unfinished case analysis, impact decisions, and upward allocation, while still refusing a half-created
// proposal that the aggregate itself cannot represent.
const proposalCanCheckIn = (item: DraftRequirement) => {
  const derived = item.isDerived ?? parseObject(item.attributesJson).derived === true;
  return Boolean(
    (item.kind === "Introduce" || item.baseNumber) &&
      (item.kind === "Retire" || item.statement.trim()) &&
      (!derived || item.rationale.trim()),
  );
};

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

export default function ScrWorkspace({
  api,
  scrId,
  user,
  onBack,
  onChanged,
  onOpenScr,
  onOpenRequirement,
  onOpenProblemReport,
  onDisciplineResolved,
  releases,
}: Props) {
  const [scr, setScr] = useState<ScrDetail>();
  const [drivingProblemReports, setDrivingProblemReports] = useState<ProblemReportSummary[]>([]);
  const [context, setContext] = useState<AuthoringContext>();
  const [mode, setMode] = useState<"view" | "edit" | "approvers">("view");
  const [reviewMode, setReviewMode] = useState<"Sequential" | "Parallel">("Sequential");
  const [error, setError] = useState("");
  const [loadFailure, setLoadFailure] = useState("");
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState("");
  const [signing, setSigning] = useState(false);
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
  const [approvers, setApprovers] = useState<Approver[]>([]);
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
    const response = await fetch(`${api}/api/controlled-editing/status?artifactType=SCR&artifactId=${scrId}`);
    if (response.ok) setLockStatus((await response.json()) as LockStatus);
  }, [api, scrId]);

  const load = useCallback(async () => {
    setLoadFailure("");
    try {
      const response = await fetch(`${api}/api/scrs/${scrId}`);
      if (!response.ok) {
        setLoadFailure(response.status === 404
          ? "No originating change request is available for this requirement."
          : "The originating change request could not be loaded in this build workspace.");
        return;
      }
      {
      const detail = (await response.json()) as ScrDetail;
      onDisciplineResolved(detail.type === "Software" ? "software" : "system");
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
  }, [api, loadStatus, mode, onDisciplineResolved, scrId]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!scr) return;
    let cancelled = false;
    fetch(`${api}/api/authoring/context?projectId=${scr.projectId}&type=${scr.type}`)
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
          body: JSON.stringify({ artifactType: "SCR", artifactId: scrId, leaseMinutes: 15 }),
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
      const fallbackLevel: RequirementLevel = scr?.type === "Software" ? "HighLevel" : "System";
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
      const response = await fetch(`${api}/api/scrs/${scrId}/${path}`, {
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
      const response = await fetch(`${api}/api/scrs/${scr.id}/defer`, {
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
      const response = await fetch(`${api}/api/scrs/${scr.id}/cancel-review`, {
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

  const reinstate = async () => {
    if (!scr) return;
    await withBusy(async () => {
      const response = await fetch(`${api}/api/scrs/${scr.id}/reinstate`, { method: "POST" });
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
      const response = await fetch(`${api}/api/scrs/${scr.id}/next-revision`, {
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
      createRequirement(level, kind, kind === "Introduce" ? nextIdentifier(level) : ""),
    ]);

  // What a proposal does to a requirement, changed after the card exists. Not a field update: the kind decides
  // what the identifier means, so the identity is re-derived rather than carried across. Same rule as the new
  // change request editor, and here for the same reason — an author editing a checked-out Draft changes their
  // mind about a proposal as readily as one writing it for the first time.
  const changeRequirementKind = (index: number, kind: RequirementKind) =>
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
        };
      }),
    );

  const saveWorkingCopy = async () => {
    setError("");
    setSaved("");
    const current = await autosave();
    if (current) setSaved("Working copy saved. Checkout remains active.");
  };

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!scr || !lockRef.current) return;
    if (!draft.title.trim() || !requirements.every(proposalCanCheckIn)) {
      setError("Add a title and finish or remove each started requirement proposal before checking in this Draft.");
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
  const active =
    latest?.steps.find((step) => step.state === "Active" && step.approverId === user.userName) ??
    latest?.steps.find((step) => step.state === "Active");
  const isAuthor = scr.authorId === user.userName || user.isAdministrator;
  const targetRelease = releases.find((item) => item.id === scr.targetReleaseId);
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
  const caseComplete = [draft.title, draft.problem, draft.analysis, draft.solution].every((value) =>
    value.trim(),
  );
  const proposalsComplete = requirements.length > 0 && requirements.every(proposalComplete);
  const reviewReady = caseComplete && proposalsComplete && requirements.length > 0;
  const hasUnsavedChanges = mode === "edit" && serializedWorkingCopy !== lastSavedRef.current;
  const hasCheckoutChanges = mode === "edit" && (
    resumedWorkingCopyRef.current || serializedWorkingCopy !== checkoutSnapshotRef.current
  );
  const draftCanCheckIn = Boolean(draft.title.trim()) && requirements.every(proposalCanCheckIn);
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
  };

  return (
    <main className="scrPage">
      <header className="scrHeader">
        <div>
          <button className="back" type="button" onClick={onBack}>← {scr.type === "Software" ? "Software" : "System"} Change Requests</button>
          <p className="eyebrow">CHANGE CONTROL / {scr.displayNumber}</p>
          <h1>{scr.title}</h1>
          <p>Revision-controlled change case, requirement proposals, and review authority.</p>
        </div>
        <div className="headerState">
          {/* Both facts in the header badge, in the order somebody reads them: where the work sits, then how far
              it got. "Deferred · Approved" is a sentence; "Deferred" alone loses that it was signed off. */}
          <span className={`stateBadge ${scr.state.toLowerCase()}`} data-state={scr.state}>{changeRequestAllocation(scrFacts)} · {changeRequestState(scrFacts)}</span>
          <small>Record version {scr.version}</small>
          {/* In the header, in flow. These download links used to be a `position: fixed` overlay pinned to
              the viewport's top right, which is the same place this state badge and record version sit — so
              on every change request the buttons covered them. Nothing about them needs to float. */}
          <div className="scrPublicationTools">
            <span>Professional controlled publication</span>
            <a href={`${api}/api/scrs/${scr.id}/download?format=docx`}>Download DOCX</a>
            <a href={`${api}/api/scrs/${scr.id}/download?format=pdf`}>Download PDF</a>
          </div>
        </div>
      </header>

      {error && <div className="workspaceError" role="alert">{error}</div>}
      {saved && <div className="workspaceSaved" role="status">✓ {saved}</div>}

      {mode === "edit" ? (
        <form onSubmit={save} className="workspaceStack">
          <nav className="workspaceStages" aria-label="Checked-out authoring progress">
            <a href="#checked-change-case" className={caseComplete ? "complete" : "active"}>
              <span>1</span><div><b>Change case</b><small>{caseComplete ? "Complete" : "Required"}</small></div>
            </a>
            <a href="#checked-requirements" className={proposalsComplete ? "complete" : caseComplete ? "active" : ""}>
              <span>2</span><div><b>Requirement changes</b><small>{proposalsComplete ? "Complete" : "In progress"}</small></div>
            </a>
          </nav>

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
            <ProblemReportPicker api={api} projectId={scr.projectId} releaseId={scr.targetReleaseId}
              selected={problemReportIds} onChange={setProblemReportIds}
              legend={`PRs driving this ${scr.type === "Software" ? "SWCR" : "SCR"} (optional)`} />
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
                  <button type="button" disabled={!context} onClick={() => addProposal("Introduce", "System")}>+ Introduce System requirement</button>
                  <button type="button" onClick={() => addProposal("Modify", "System")}>Modify existing</button>
                  <button type="button" onClick={() => addProposal("Retire", "System")}>Retire existing</button>
                </>
              ) : (
                <>
                  <button type="button" disabled={!context} onClick={() => addProposal("Introduce", "HighLevel")}>+ Introduce HLR</button>
                  <button type="button" disabled={!context} onClick={() => addProposal("Introduce", "LowLevel")}>+ Introduce LLR</button>
                  <button type="button" onClick={() => addProposal("Modify", "HighLevel")}>Modify existing HLR</button>
                  <button type="button" onClick={() => addProposal("Retire", "HighLevel")}>Retire existing HLR</button>
                  <button type="button" onClick={() => addProposal("Modify", "LowLevel")}>Modify existing LLR</button>
                  <button type="button" onClick={() => addProposal("Retire", "LowLevel")}>Retire existing LLR</button>
                </>
              )}
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

          <div className="workspaceActions stickyWorkspaceActions">
            <div>
              <b>{reviewReady ? "Ready for review after check-in" : "Draft can be checked in before review readiness"}</b>
              <span>{hasUnsavedChanges ? "Working copy has unsaved changes" : `Working copy: ${autosaveStatus.toLowerCase()}`}</span>
            </div>
            <button type="button" className="outline" onClick={discard}>Discard checkout</button>
            <button type="button" className="outline" onClick={() => void saveWorkingCopy()}
              disabled={busy || autosaveStatus === "Saving" || autosaveStatus === "Conflict" || !hasUnsavedChanges}>Save</button>
            <button disabled={busy || autosaveStatus === "Conflict" || !hasCheckoutChanges || !draftCanCheckIn}>
              {busy ? "Checking in…" : "Save & check in"}
            </button>
          </div>
        </form>
      ) : mode === "approvers" ? (
        <section className="workspaceCard approverSetup">
          <div className="reviewSetupIntro">
            <span>FINAL HANDOFF</span>
            <h2>Configure review authority</h2>
            <p>Select only the people who have decision authority for this exact controlled snapshot.</p>
            <div><b>{scr.displayNumber}</b><span>{requirements.length} requirement proposal{requirements.length === 1 ? "" : "s"} ready for review</span></div>
          </div>
          <div className="reviewModePicker">
            <button type="button" className={reviewMode === "Sequential" ? "active" : ""} onClick={() => setReviewMode("Sequential")}>
              <b>Sequential</b><span>Activate one reviewer at a time in this order.</span>
            </button>
            <button type="button" className={reviewMode === "Parallel" ? "active" : ""} onClick={() => setReviewMode("Parallel")}>
              <b>Parallel</b><span>Activate all reviewers when review begins.</span>
            </button>
          </div>

          {!approvers.length && (
            <div className="reviewerEmpty">
              <span>0</span>
              <div><b>No reviewers selected</b><p>Start with the minimum accountable review authority; no identities are prefilled.</p></div>
            </div>
          )}
          {approvers.map((person, index) => (
            <div className="approverRow" key={index}>
              <span>{reviewMode === "Sequential" ? index + 1 : "•"}</span>
              <PersonPicker
                api={api}
                projectId={scr.projectId}
                value={person.userId}
                name={person.name}
                index={index}
                onSelect={(selected) =>
                  setApprovers((items) =>
                    items.map((item, position) => (position === index ? selected : item)),
                  )
                }
              />
              <button type="button" aria-label={`Move approver ${index + 1} up`} disabled={reviewMode === "Parallel" || index === 0} onClick={() => move(index, -1)}>↑</button>
              <button type="button" aria-label={`Move approver ${index + 1} down`} disabled={reviewMode === "Parallel" || index === approvers.length - 1} onClick={() => move(index, 1)}>↓</button>
              <button type="button" className="remove" onClick={() => setApprovers((items) => items.filter((_, position) => position !== index))}>Remove</button>
            </div>
          ))}
          <button type="button" className="outline addApprover" onClick={() => setApprovers((items) => [...items, { userId: "", name: "" }])}>+ Add approver</button>
          {uniqueApprovers.size !== approvers.filter((item) => item.userId).length && (
            <div className="reviewerWarning">Each reviewer may appear only once.</div>
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
        <div className="workspaceGrid">
          <div className="workspaceStack">
            <section className="workspaceCard">
              <div className="workspaceTitle">
                <div><h2>Change case</h2><p>Problem, analysis, and proposed solution</p></div>
                {/* One position holds whatever you do to this change request, so it is always the same place
                    on the page and its label says which of the two applies. A Draft is checked out and
                    edited in place. An approved revision is immutable and cannot be — it is superseded, and
                    the action that does that is Revise. It was previously buried in the rail below a
                    definition list and labelled "Create SCR-31.01 Draft", which describes the
                    mechanism rather than the intent, and nobody found it. */}
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
                {/* Alongside the other actions, so putting work down is as reachable as picking it up. Available
                    from Draft, In Review and Approved alike: what gets shelved is the work, at whatever stage it
                    reached, and the state it reached is remembered so reinstating puts it back there. */}
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
              </div>
              {lockStatus?.locked && !lockStatus.mine && (
                <div className="readOnlyLock"><b>Read-only while checked out</b><span><PersonName userName={lockStatus.holder} /> · active {lockStatus.lastActivityAt && new Date(lockStatus.lastActivityAt).toLocaleString()} · expires {lockStatus.expiresAt && new Date(lockStatus.expiresAt).toLocaleTimeString()}</span></div>
              )}
              <div className="pasView">
                {/* Rendered as the author wrote it, tables and figures included. An approver signing for a
                    change must read what the change actually says, not a flattened copy of it. */}
                {([["P", "Problem", scr.problemRich || fromPlainText(scr.problem)],
                   ["A", "Analysis", scr.analysisRich || fromPlainText(scr.analysis)],
                   ["S", "Solution", scr.solutionRich || fromPlainText(scr.solution)]] as const).map((item) => (
                  <article key={item[0]}><span>{item[0]}</span><div><b>{item[1]}</b>
                    <RichContentView api={api} value={item[2]} empty="Not yet provided" />
                  </div></article>
                ))}
              </div>
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

            <ScrJiraLink api={api} scrId={scr.id} displayNumber={scr.displayNumber} />

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
                  </article>
                );
              })}
            </section>

            <section className="workspaceCard">
              <div className="workspaceTitle"><div><h2>Audit history</h2></div></div>
              {scr.audit.map((event, index) => (
                <div className="auditRow" key={`${event.occurredAt}-${index}`}><i /><div><b>{auditEventTitle(event.eventType, targetRelease?.version ?? "")}</b><p>{auditSummary(event)}</p><AuditEvidence event={event} /><small><PersonName userName={event.actorId} /> · {new Date(event.occurredAt).toLocaleString()}</small></div></div>
              ))}
            </section>
          </div>

          <aside className="reviewRail">
            <section className="workspaceCard controlStatusCard">
              <div className="workspaceTitle"><div><h2>Control status</h2><p>{scr.displayNumber}</p></div></div>
              {/* Two rows, because they are two questions. Allocation says which build this is going into, or
                  that it has been put away; State says how far it has got. One stored value used to answer both
                  and served neither — a reader asking either got a word that half answered the other. */}
              <dl>
                <div><dt>Allocation</dt><dd data-allocation={scr.state === "Deferred" ? "Deferred" : "Build"}>{changeRequestAllocation(scrFacts)}</dd></div>
                <div><dt>State</dt><dd data-state={scr.state}>{changeRequestState(scrFacts)}</dd></div>
                <div><dt>Author</dt><dd><PersonName userName={scr.authorId} withRole /></dd></div>
                <div><dt>Revision</dt><dd>{scr.revision}</dd></div>
                <div><dt>Updated</dt><dd>{new Date(scr.updatedAt).toLocaleDateString()}</dd></div>
              </dl>
              {scr.state === "Draft" && isAuthor && reviewReady && (
                <><div className="railReadiness ready"><b>Ready for review</b><span>The change case and requirement proposals are complete.</span></div><button type="button" className="primaryFull" onClick={openReviewerSetup}>Configure & Submit Review</button></>
              )}
              {scr.state === "Draft" && isAuthor && !reviewReady && (
                <div className="railReadiness"><b>Draft needs authoring</b><span>{!caseComplete ? "Complete the change case." : "Complete the requirement proposals."}</span><button type="button" disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>Complete Draft readiness</button></div>
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
            </section>

            {latest && (
              <section className="workspaceCard">
                <div className="workspaceTitle"><div><h2>Review cycle {latest.sequence}</h2><p>{stateLabel(latest.state)}</p></div></div>
                <div className="approvalPath">
                  {latest.steps.map((step) => (
                    <div className={`approvalStep ${step.state.toLowerCase()}`} key={step.position}><span>{step.state === "Approved" ? "✓" : step.position + 1}</span><div><b><PersonName userName={step.approverId} displayName={step.approverName} /></b><small>{approverRole(step)} · {approvalStanding(latest, step)}</small></div></div>
                  ))}
                </div>
                {latest.closureReason && <div className="closure"><b>Closure reason</b><p>{latest.closureReason}</p></div>}
                {scr.state === "InReview" && active && (
                  <div className="reviewActions">
                    <p><b><PersonName userName={active.approverId} displayName={active.approverName} /></b> is the active reviewer.</p>
                    {active.approverId === user.userName ? (
                      <><button type="button" disabled={busy} onClick={() => setSigning(true)}>Review & electronically approve</button><textarea aria-label="Reason for requested changes" placeholder="Reason for requested changes" value={reason} onChange={(event) => setReason(event.target.value)} /><button type="button" className="danger" disabled={busy || !reason.trim()} onClick={() => void call("request-changes", { expectedVersion: scr.version, reason })}>Request changes</button></>
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
              </section>
            )}
          </aside>
        </div>
      )}

      {signing && (
        <SignatureDialog
          title={`Approve ${scr.displayNumber}`}
          meaning="I approve this exact SCR revision and its proposed requirement changes as suitable for controlled progression."
          onCancel={() => setSigning(false)}
          onSign={async (password, meaning) => {
            const ok = await call("approve", { password, meaning, expectedVersion: scr.version });
            if (ok) setSigning(false);
          }}
        />
      )}
    </main>
  );
}
