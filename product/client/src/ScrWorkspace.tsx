import { useCallback, useEffect, useRef, useState } from "react";
import { stateLabel } from './presentation'
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
import ControlledAttachments from "./ControlledAttachments";
import ScrJiraLink from "./ScrJiraLink";
import { RichCaseField, RichContentView } from "./RichContent";
import { useDebouncedSave } from "./autosave";
import { emptyRichContent, fromPlainText, toPlainText } from "./richContent";
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
};
type Step = {
  position: number;
  approverId: string;
  approverName: string;
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
  detail: string;
  occurredAt: string;
};
type ScrDetail = {
  id: string;
  baseNumber: string;
  revision: number;
  displayNumber: string;
  projectId: string;
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
  createdAt: string;
  updatedAt: string;
  requirementChanges: Requirement[];
  reviewCycles: Cycle[];
  audit: Audit[];
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
};

const pendingImpact = JSON.stringify({
  trace: "Pending",
  verification: "Pending",
  documents: "Pending",
  baseline: "Pending",
  collaboration: "Pending",
});
const impactKeys = [
  "trace",
  "verification",
  "documents",
  "baseline",
  "collaboration",
] as const;
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
    richText: item.richText || item.statement || "",
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
      },
      item.level,
    ),
  );
const impactsComplete = (item: DraftRequirement) => {
  const values = parseObject(item.impactDispositionJson);
  return impactKeys.every((key) => values[key] && values[key] !== "Pending");
};
const proposalComplete = (item: DraftRequirement) =>
  Boolean(
    item.baseNumber &&
      (item.kind === "Retire" || item.statement.trim()) &&
      (!(item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
        item.rationale.trim()),
  );

export default function ScrWorkspace({
  api,
  scrId,
  user,
  onBack,
  onChanged,
  onOpenScr,
}: Props) {
  const [scr, setScr] = useState<ScrDetail>();
  const [context, setContext] = useState<AuthoringContext>();
  const [mode, setMode] = useState<"view" | "edit" | "approvers">("view");
  const [reviewMode, setReviewMode] = useState<"Sequential" | "Parallel">("Sequential");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState("");
  const [signing, setSigning] = useState(false);
  const [lock, setLock] = useState<EditLock>();
  const [lockStatus, setLockStatus] = useState<LockStatus>();
  const [autosaveStatus, setAutosaveStatus] = useState<"Saved" | "Saving" | "Error" | "Conflict">("Saved");
  const [draft, setDraft] = useState<ScrDraft>({
    title: "", problem: "", analysis: "", solution: "",
    problemRich: emptyRichContent, analysisRich: emptyRichContent, solutionRich: emptyRichContent,
  });
  const [requirements, setRequirements] = useState<DraftRequirement[]>([]);
  const [approvers, setApprovers] = useState<Approver[]>([]);
  const lockRef = useRef<EditLock | undefined>(undefined);
  const draftRef = useRef("");
  const lastSavedRef = useRef("");
  const savingRef = useRef(false);

  const loadStatus = useCallback(async () => {
    const response = await fetch(`${api}/api/controlled-editing/status?artifactType=SCR&artifactId=${scrId}`);
    if (response.ok) setLockStatus((await response.json()) as LockStatus);
  }, [api, scrId]);

  const load = useCallback(async () => {
    const response = await fetch(`${api}/api/scrs/${scrId}`);
    if (response.ok) {
      const detail = (await response.json()) as ScrDetail;
      setScr(detail);
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
      }
    }
    await loadStatus();
  }, [api, loadStatus, mode, scrId]);

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
    draftRef.current = JSON.stringify({ ...draft, requirementChanges: requirements });
  }, [draft, requirements]);


  const beginEdit = async () => {
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/controlled-editing/checkout`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ artifactType: "SCR", artifactId: scrId, leaseMinutes: 15 }),
    });
    setBusy(false);
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
      setLock(value);
      setDraft({
        title: recovered.title,
        problem: recovered.problem,
        analysis: recovered.analysis,
        solution: recovered.solution,
        // A recovery snapshot written before the change case could carry structure holds only the plain
        // fields. It reopens as paragraphs rather than as an empty form.
        problemRich: recovered.problemRich || fromPlainText(recovered.problem),
        analysisRich: recovered.analysisRich || fromPlainText(recovered.analysis),
        solutionRich: recovered.solutionRich || fromPlainText(recovered.solution),
      });
      if (recovered.requirementChanges)
        setRequirements(
          recovered.requirementChanges.map((item) => normalizeRequirement(item, fallbackLevel)),
        );
      lastSavedRef.current = value.draftJson;
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
  useDebouncedSave(draftRef.current, async () => { await autosave(); }, {
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
    setMode("view");
    await load();
  };

  const call = async (path: string, body: unknown) => {
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/scrs/${scrId}/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      const value = (await response.json()) as { error?: string };
      setError(value.error || "The operation could not be completed.");
      setBusy(false);
      return false;
    }
    await load();
    await onChanged();
    setBusy(false);
    setMode("view");
    return true;
  };

  const revise = async () => {
    if (!scr) return;
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/scrs/${scr.id}/next-revision`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedVersion: scr.version }),
    });
    if (!response.ok) {
      const value = (await response.json()) as { error?: string };
      setError(value.error || "The next controlled revision could not be created.");
      setBusy(false);
      return;
    }
    const next = (await response.json()) as { id: string };
    await onChanged();
    setBusy(false);
    onOpenScr(next.id);
  };

  const updateRequirement = (
    index: number,
    key: keyof DraftRequirement,
    value: string | number | boolean,
  ) =>
    setRequirements((items) =>
      items.map((item, position) => {
        if (position !== index) return item;
        const next = { ...item, [key]: value } as DraftRequirement;
        if (key === "statement" && (!item.richText || toPlainText(item.richText) === item.statement))
          next.richText = fromPlainText(String(value));
        return next;
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

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!scr || !lockRef.current) return;
    const caseComplete = [draft.title, draft.problem, draft.analysis, draft.solution].every((value) =>
      value.trim(),
    );
    if (!caseComplete || !requirements.length || !requirements.every(proposalComplete)) {
      setError("Complete the change case and every requirement proposal before checking in.");
      return;
    }
    setBusy(true);
    setError("");
    while (savingRef.current)
      await new Promise((resolve) => window.setTimeout(resolve, 25));
    const current = await autosave();
    if (!current) {
      setError("The latest recovery snapshot could not be saved for check-in.");
      setBusy(false);
      return;
    }
    savingRef.current = true;
    const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/check-in`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        expectedVersion: current.version,
      }),
    });
    savingRef.current = false;
    if (!response.ok) {
      const value = (await response.json()) as { error?: string };
      setError(value.error || "Draft could not be saved.");
      setBusy(false);
      return;
    }
    setLock(undefined);
    setMode("view");
    await load();
    await onChanged();
    setBusy(false);
  };

  const move = (index: number, direction: number) =>
    setApprovers((items) => {
      const target = index + direction;
      if (target < 0 || target >= items.length) return items;
      const next = [...items];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });

  if (!scr) return <main className="scrLoading">Loading controlled record…</main>;

  const latest = [...scr.reviewCycles].sort((a, b) => b.sequence - a.sequence)[0];
  const active =
    latest?.steps.find((step) => step.state === "Active" && step.approverId === user.userName) ??
    latest?.steps.find((step) => step.state === "Active");
  const isAuthor = scr.authorId === user.userName || user.isAdministrator;
  const caseComplete = [draft.title, draft.problem, draft.analysis, draft.solution].every((value) =>
    value.trim(),
  );
  const proposalsComplete = requirements.length > 0 && requirements.every(proposalComplete);
  const impactCount = requirements.filter(impactsComplete).length;
  const reviewReady =
    caseComplete && proposalsComplete && impactCount === requirements.length && requirements.length > 0;
  const uniqueApprovers = new Set(approvers.map((item) => item.userId).filter(Boolean));
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
          <button className="back" type="button" onClick={onBack}>← Command Center</button>
          <p className="eyebrow">CHANGE CONTROL / {scr.displayNumber}</p>
          <h1>{scr.title}</h1>
          <p>Revision-controlled change case, requirement proposals, and review authority.</p>
        </div>
        <div className="headerState">
          <span className={`stateBadge ${scr.state.toLowerCase()}`} data-state={scr.state}>{stateLabel(scr.state)}</span>
          <small>Record version {scr.version}</small>
        </div>
      </header>

      {error && <div className="workspaceError" role="alert">{error}</div>}

      {mode === "edit" ? (
        <form onSubmit={save} className="workspaceStack">
          <nav className="workspaceStages" aria-label="Checked-out authoring progress">
            <a href="#checked-change-case" className={caseComplete ? "complete" : "active"}>
              <span>1</span><div><b>Change case</b><small>{caseComplete ? "Complete" : "Required"}</small></div>
            </a>
            <a href="#checked-requirements" className={proposalsComplete ? "complete" : caseComplete ? "active" : ""}>
              <span>2</span><div><b>Requirement changes</b><small>{proposalsComplete ? "Complete" : "In progress"}</small></div>
            </a>
            <a href="#checked-readiness" className={reviewReady ? "complete" : proposalsComplete ? "active" : ""}>
              <span>3</span><div><b>Impact & readiness</b><small>{reviewReady ? "Review ready" : `${impactCount}/${requirements.length} closed`}</small></div>
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
                placeholder="What need, defect, or risk exists?"
                onChange={(value) => setDraft((current) => ({ ...current, problemRich: value, problem: toPlainText(value) }))} />
              <RichCaseField api={api} projectId={scr.projectId} label="Analysis" value={draft.analysisRich}
                placeholder="What is affected and what alternatives were considered?"
                onChange={(value) => setDraft((current) => ({ ...current, analysisRich: value, analysis: toPlainText(value) }))} />
              <RichCaseField api={api} projectId={scr.projectId} label="Solution" value={draft.solutionRich}
                placeholder="What controlled outcome is proposed?"
                onChange={(value) => setDraft((current) => ({ ...current, solutionRich: value, solution: toPlainText(value) }))} />
            </div>
          </section>

          <section className="workspaceCard authoringCard" id="checked-requirements">
            <div className="workspaceTitle">
              <div>
                <span className="stageKicker">STAGE 2</span>
                <h2>Controlled requirement authoring</h2>
                <p>One shared editor for content, classification, and lifecycle impact decisions.</p>
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
                  <button type="button" onClick={() => addProposal("Modify", "HighLevel")}>Modify existing</button>
                  <button type="button" onClick={() => addProposal("Retire", "HighLevel")}>Retire existing</button>
                </>
              )}
            </div>
            {requirements.map((item, index) => (
              <ControlledRequirementEditor
                api={api}
                projectId={scr.projectId}
                scope={scr.type}
                item={item}
                index={index}
                key={`${index}-${item.kind}`}
                identityLocked={Boolean(item.baseNumber)}
                onChange={(key, value) => updateRequirement(index, key, value)}
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

          <section className="workspaceCard authoringCard" id="checked-readiness">
            <div className="workspaceTitle">
              <div>
                <span className="stageKicker">STAGE 3</span>
                <h2>Impact & review readiness</h2>
                <p>Check in an honest Draft now; submit only when every gate is closed.</p>
              </div>
              <span className={reviewReady ? "completionBadge complete" : "completionBadge"}>
                {reviewReady ? "Review ready" : "Draft only"}
              </span>
            </div>
            <div className="workspaceReadiness">
              <article className={caseComplete ? "complete" : ""}><span>{caseComplete ? "✓" : "1"}</span><div><b>Change case</b><p>{caseComplete ? "Complete" : "Needs required context"}</p></div></article>
              <article className={proposalsComplete ? "complete" : ""}><span>{proposalsComplete ? "✓" : "2"}</span><div><b>Proposal content</b><p>{proposalsComplete ? "Complete" : "Resolve incomplete identities or content"}</p></div></article>
              <article className={impactCount === requirements.length && requirements.length ? "complete" : ""}><span>{impactCount === requirements.length && requirements.length ? "✓" : "3"}</span><div><b>Impact decisions</b><p>{impactCount}/{requirements.length} proposals closed</p></div></article>
            </div>
            <div className={reviewReady ? "workspaceNext ready" : "workspaceNext"}>
              <b>{reviewReady ? "Next: check in and assign reviewers" : "Next: close the highlighted authoring gaps"}</b>
              <p>{reviewReady ? "The checked-in snapshot will be ready for explicit reviewer selection." : "You may check in completed proposal content with pending impacts, but review stays blocked."}</p>
            </div>
          </section>

          <div className="workspaceActions stickyWorkspaceActions">
            <div>
              <b>{reviewReady ? "Ready to check in" : "Draft can be checked in before review readiness"}</b>
              <span>Server recovery: {autosaveStatus.toLowerCase()}</span>
            </div>
            <button type="button" className="outline" onClick={discard}>Discard checkout</button>
            <button type="button" className="outline" onClick={() => void autosave()} disabled={autosaveStatus === "Saving"}>Save recovery snapshot</button>
            <button disabled={busy || autosaveStatus === "Conflict" || !caseComplete || !proposalsComplete}>
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
            <div><b>{scr.displayNumber}</b><span>{requirements.length} proposals · 5/5 impact decisions complete</span></div>
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
            <div><b>{approvers.length} reviewer{approvers.length === 1 ? "" : "s"} selected</b><span>{reviewMode} authority path</span></div>
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
                {scr.state === "Draft" && isAuthor && (
                  <button className="outline" type="button" disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>
                    {busy ? "Checking lock…" : lockStatus?.locked && !lockStatus.mine ? `Read only · ${lockStatus.holder}` : "Check out & edit"}
                  </button>
                )}
              </div>
              {lockStatus?.locked && !lockStatus.mine && (
                <div className="readOnlyLock"><b>Read-only while checked out</b><span>{lockStatus.holder} · active {lockStatus.lastActivityAt && new Date(lockStatus.lastActivityAt).toLocaleString()} · expires {lockStatus.expiresAt && new Date(lockStatus.expiresAt).toLocaleTimeString()}</span></div>
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
                const draftItem = normalizeRequirement({ ...item, baseNumber: base(item.displayNumber), revision: revision(item.displayNumber) }, item.level);
                return (
                  <article className="requirementView" key={item.id}>
                    <div><b>{item.displayNumber}</b><span>{item.level} · {item.kind}</span></div>
                    <p>{item.kind === "Retire" && !item.statement ? "Requirement will be retired." : item.statement}</p>
                    <footer><small>{item.verificationMethod} · {item.rationale || "No rationale recorded"}</small><em className={impactsComplete(draftItem) ? "closed" : "pending"}>{impactsComplete(draftItem) ? "5/5 impacts closed" : "Impact decisions pending"}</em></footer>
                  </article>
                );
              })}
            </section>

            <section className="workspaceCard">
              <div className="workspaceTitle"><div><h2>Audit history</h2><p>Append-only material events</p></div></div>
              {scr.audit.map((event, index) => (
                <div className="auditRow" key={`${event.occurredAt}-${index}`}><i /><div><b>{event.eventType.replace(/([A-Z])/g, " $1").trim()}</b><p>{event.detail}</p><small>{event.actorId} · {new Date(event.occurredAt).toLocaleString()}</small></div></div>
              ))}
            </section>
          </div>

          <aside className="reviewRail">
            <section className="workspaceCard controlStatusCard">
              <div className="workspaceTitle"><div><h2>Control status</h2><p>{scr.displayNumber}</p></div></div>
              <dl>
                <div><dt>State</dt><dd data-state={scr.state}>{stateLabel(scr.state)}</dd></div>
                <div><dt>Author</dt><dd>{scr.authorId}</dd></div>
                <div><dt>Revision</dt><dd>{scr.revision}</dd></div>
                <div><dt>Updated</dt><dd>{new Date(scr.updatedAt).toLocaleDateString()}</dd></div>
              </dl>
              {scr.state === "Draft" && isAuthor && reviewReady && (
                <><div className="railReadiness ready"><b>Ready for review</b><span>Case, proposals, and impacts are complete.</span></div><button type="button" className="primaryFull" onClick={openReviewerSetup}>Configure & Submit Review</button></>
              )}
              {scr.state === "Draft" && isAuthor && !reviewReady && (
                <div className="railReadiness"><b>Draft needs authoring</b><span>{!caseComplete ? "Complete the change case." : !proposalsComplete ? "Complete requirement proposals." : `Close impact decisions on ${requirements.length - impactCount} proposal${requirements.length - impactCount === 1 ? "" : "s"}.`}</span><button type="button" disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>Complete Draft readiness</button></div>
              )}
              {scr.state === "Approved" && isAuthor && (
                <button type="button" className="primaryFull" disabled={busy} onClick={revise}>{busy ? "Creating revision…" : `Create ${scr.baseNumber}.${String(scr.revision + 1).padStart(2, "0")} Draft`}</button>
              )}
              {scr.state === "Approved" && <p className="snapshotNote">The approved revision remains immutable. A new Draft copies its exact content into the next revision.</p>}
            </section>

            {latest && (
              <section className="workspaceCard">
                <div className="workspaceTitle"><div><h2>Review cycle {latest.sequence}</h2><p>{stateLabel(latest.state)} · {latest.snapshotHash.slice(0, 12)}…</p></div></div>
                <div className="approvalPath">
                  {latest.steps.map((step) => (
                    <div className={`approvalStep ${step.state.toLowerCase()}`} key={step.position}><span>{step.state === "Approved" ? "✓" : step.position + 1}</span><div><b>{step.approverName}</b><small>{step.approverId} · {stateLabel(step.state)}</small></div></div>
                  ))}
                </div>
                {latest.closureReason && <div className="closure"><b>Closure reason</b><p>{latest.closureReason}</p></div>}
                {scr.state === "InReview" && active && (
                  <div className="reviewActions">
                    <p><b>{active.approverName}</b> is the active reviewer.</p>
                    {active.approverId === user.userName ? (
                      <><button type="button" disabled={busy} onClick={() => setSigning(true)}>Review & electronically approve</button><textarea placeholder="Reason for requested changes" value={reason} onChange={(event) => setReason(event.target.value)} /><button type="button" className="danger" disabled={busy || !reason.trim()} onClick={() => void call("request-changes", { expectedVersion: scr.version, reason })}>Request changes</button></>
                    ) : (
                      <div className="snapshotNote"><b>Waiting for assigned reviewer</b><p>Only the assigned identity can act on this stage.</p></div>
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
