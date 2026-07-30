import { useCallback, useEffect, useState } from "react";
import { stateLabel } from './presentation'
import { SignatureDialog } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import { PersonAvatar } from "./People";
import { decisionPeople, demoPerson } from "./PeopleRegistry";
import "./LifecycleDecisionRoom.css";

type Gate = { code: string; name: string; complete: boolean; completed: number; total: number; detail: string; action: string; evaluationState: "Evaluated" | "WaitingForPrerequisite"; prerequisiteCode?: string };
type Approval = { position: number; approverId: string; approverName: string; state: string; approvedAt?: string };
type CampaignDetail = {
  id: string; name: string; state: string; releaseId: string; release: string; baselineId: string; baseline: string; baselineState: string;
  requirementsHash?: string; softwareBuildId?: string; releaseHash?: string;
  readiness: { percent: number; readyForRelease: boolean; gates: Gate[] };
  changes: { id: string; displayNumber: string; title: string; type: string; state: string; authorId: string; requirementCount: number; included: boolean }[];
  impacts: { id: string; scrId: string; scr: string; title: string; kind: string; artifactReference: string; description: string; state: string; rationale: string }[];
  approvals: Approval[];
  events: { eventType: string; actorId: string; detail: string; occurredAt: string }[];
};
type Comparison = {
  fromRelease: string; toRelease: string; fromBaseline?: string; toBaseline?: string; toMaterialized: boolean;
  summary: { added: number; modified: number; retired: number; unchanged: number; proposed: number };
  proposed: { scrId: string; scr: string; title: string; state: string; type: string; displayNumber: string; level: string; kind: string; statement: string }[];
};
type ScrDetail = {
  id: string; displayNumber: string; title: string; problem: string; analysis: string; solution: string; authorId: string; state: string; updatedAt: string;
  requirementChanges: { id: string; baseNumber: string; revision: number; displayNumber: string; level: string; kind: string; statement: string; rationale: string; verificationMethod: string }[];
  reviewCycles: { steps: { position: number; approverId: string; approverName: string; state: string; decidedAt?: string }[] }[];
};
type RequirementImpact = {
  id: string; displayNumber: string;
  parents: { id: string; displayNumber: string; statement: string; type: string }[];
  children: { id: string; displayNumber: string; statement: string; type: string }[];
  tests: { id: string; revisionId: string; displayNumber: string; title: string; state: string; isSuspect: boolean; coverageState: "Confirmed" | "Suspect" }[];
  baselines: { baseline: string; release: string; state: string }[];
  documents: { id: string; documentNumber: string; revision: number; title: string; type: string; contentHash: string }[];
};
type AffectedRequirement = ScrDetail["requirementChanges"][number] & { artifactId?: string; currentStatement?: string; impact?: RequirementImpact };
type DocumentRecord = { id: string; type: string; displayNumber: string; title: string; contentHash: string; artifactCount: number; generatedAt: string; release: string; baseline: string };
type Screen = "readiness" | "impact" | "decision";

const compactNumber = (value?: string) => value?.replace(/-0{2}(\d{6})(?=\.|$)/, "-$1") ?? "";
const countLabel = (value: number, noun: string) => `${value.toLocaleString()} ${noun}${value === 1 ? "" : "s"}`;

export default function LifecycleDecisionRoom({ api, projectId, activeReleaseId, releases, user, screen, selectedScrId, onBack, onOpenScr, onOpenImpact, onOpenDecision, onBackToReadiness, onOpenVerification, onOpenDocuments, onOpenOperations }: {
  api: string; projectId: string; activeReleaseId: string; releases: { id: string; version: string }[]; user: AuthUser; screen: Screen; selectedScrId?: string;
  onBack: () => void; onOpenScr: (id: string) => void; onOpenImpact: (id: string) => void; onOpenDecision: () => void; onBackToReadiness: () => void;
  onOpenVerification: () => void; onOpenDocuments: () => void; onOpenOperations: () => void;
}) {
  const [detail, setDetail] = useState<CampaignDetail>();
  const [comparison, setComparison] = useState<Comparison>();
  const [documents, setDocuments] = useState<DocumentRecord[]>([]);
  const [scr, setScr] = useState<ScrDetail>();
  const [affected, setAffected] = useState<AffectedRequirement[]>([]);
  const [selectedRequirement, setSelectedRequirement] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [decision, setDecision] = useState<"Approve" | "Approve with conditions" | "Return for rework">("Approve");
  const [signing, setSigning] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [campaignResponse, documentResponse] = await Promise.all([
        fetch(`${api}/api/release-campaigns?projectId=${projectId}`),
        fetch(`${api}/api/documents?projectId=${projectId}`),
      ]);
      if (!campaignResponse.ok) throw new Error("Release readiness could not be loaded.");
      const campaigns = await campaignResponse.json();
      const campaign = campaigns.find((item: { releaseId: string }) => item.releaseId === activeReleaseId);
      if (!campaign) { setDetail(undefined); setDocuments([]); return; }
      const detailResponse = await fetch(`${api}/api/release-campaigns/${campaign.id}`);
      if (!detailResponse.ok) throw new Error("The release campaign is unavailable.");
      setDetail(await detailResponse.json());
      if (documentResponse.ok) setDocuments(await documentResponse.json());
      const activeIndex = releases.findIndex((item) => item.id === activeReleaseId);
      if (activeIndex > 0) {
        const compareResponse = await fetch(`${api}/api/release-comparison?projectId=${projectId}&fromReleaseId=${releases[activeIndex - 1].id}&toReleaseId=${activeReleaseId}`);
        if (compareResponse.ok) setComparison(await compareResponse.json());
      }
      setError("");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Release readiness could not be loaded.");
    } finally { setLoading(false); }
  }, [api, projectId, activeReleaseId, releases]);

  useEffect(() => { load(); }, [load]);

  const effectiveScrId = selectedScrId || detail?.changes.find((item) => item.included)?.id || detail?.changes[0]?.id;
  useEffect(() => {
    if (screen !== "impact" || !effectiveScrId) return;
    let cancelled = false;
    (async () => {
      const response = await fetch(`${api}/api/scrs/${effectiveScrId}`);
      if (!response.ok) { setError("The selected controlled change could not be loaded."); return; }
      const nextScr: ScrDetail = await response.json();
      const nextAffected = await Promise.all(nextScr.requirementChanges.map(async (change): Promise<AffectedRequirement> => {
        if (change.kind === "Introduce") return change;
        const scope = change.level === "System" ? "System" : "Software";
        const lookup = await fetch(`${api}/api/authoring/requirements?projectId=${projectId}&scope=${scope}&search=${encodeURIComponent(change.baseNumber)}&limit=5`);
        if (!lookup.ok) return change;
        const rows = await lookup.json();
        const artifact = rows.find((item: { baseNumber: string }) => item.baseNumber === change.baseNumber) ?? rows[0];
        if (!artifact) return change;
        const impactResponse = await fetch(`${api}/api/enterprise-requirements/${artifact.id}/impact`);
        return { ...change, artifactId: artifact.id, currentStatement: artifact.statement, impact: impactResponse.ok ? await impactResponse.json() : undefined };
      }));
      if (!cancelled) { setScr(nextScr); setAffected(nextAffected); setSelectedRequirement(0); }
    })();
    return () => { cancelled = true; };
  }, [api, projectId, effectiveScrId, screen]);

  const gate = useCallback((code: string) => detail?.readiness.gates.find((item) => item.code === code), [detail]);
  const blockers = detail?.readiness.gates.filter((item) => !item.complete) ?? [];
  const selected = affected[selectedRequirement];
  const activeApproval = detail?.approvals.find((item) => item.state === "Active");

  const openGate = (item: Gate) => {
    if (item.evaluationState === "WaitingForPrerequisite") setNotice("This governed prerequisite is not available in the simplified workspace.");
    else if (["coverage", "verification", "evidence"].includes(item.code)) onOpenVerification();
    else if (["traceability", "documents"].includes(item.code)) onOpenDocuments();
    else if (item.code === "release_approval") onOpenDecision();
    else onOpenOperations();
  };
  const sign = async (password: string, meaning: string) => {
    if (!detail) return;
    const response = await fetch(`${api}/api/release-campaigns/${detail.id}/approve`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password, meaning }) });
    if (!response.ok) { setError((await response.json()).error ?? "The decision could not be recorded."); return; }
    setSigning(false); setNotice("Release approval recorded against the exact decision package."); await load();
  };
  const recordDecision = () => {
    if (decision !== "Approve") { setNotice(`${decision} requires disposition in controlled release operations.`); onOpenOperations(); return; }
    if (activeApproval?.approverId === user.userName) setSigning(true);
    else { setNotice(activeApproval ? `Awaiting ${demoPerson(activeApproval.approverId, activeApproval.approverName)?.name ?? activeApproval.approverName}.` : "Complete the evidence gates before starting the ordered release review."); onOpenOperations(); }
  };

  if (loading) return <main className="decisionRoom decisionState"><div className="decisionLoader" /><h1>Building the decision view</h1><p>Connecting release scope, evidence, and authority…</p></main>;
  if (!detail) return <main className="decisionRoom decisionState"><span>◇</span><h1>Release readiness is not configured</h1><p>{error || "No lifecycle decision package is available for this build."}</p><button onClick={onBack}>Return to Command Center</button></main>;

  return <main className="decisionRoom">
    {error && <div className="decisionAlert" role="alert">{error}<button onClick={() => setError("")}>×</button></div>}
    {notice && <div className="decisionNotice" role="status">✓ {notice}<button onClick={() => setNotice("")}>×</button></div>}
    {screen === "readiness" && <ReadinessView detail={detail} comparison={comparison} blockers={blockers} gate={gate} onBack={onBack} onOpenDecision={onOpenDecision} onOpenImpact={onOpenImpact} openGate={openGate} />}
    {screen === "impact" && <ImpactView detail={detail} scr={scr} affected={affected} selectedIndex={selectedRequirement} selected={selected} onSelect={setSelectedRequirement} onBack={onBackToReadiness} onOpenScr={() => effectiveScrId && onOpenScr(effectiveScrId)} onOpenVerification={onOpenVerification} />}
    {screen === "decision" && <DecisionView detail={detail} comparison={comparison} documents={documents} blockers={blockers} decision={decision} onDecision={setDecision} onBack={onBackToReadiness} onRecord={recordDecision} onOpenDocuments={onOpenDocuments} openGate={openGate} />}
    {signing && <SignatureDialog title={`Approve release ${detail.release}`} meaning="I approve this exact release decision package and authorize controlled progression." onCancel={() => setSigning(false)} onSign={sign} />}
  </main>;
}

function PageHeader({ eyebrow, title, subtitle, onBack, backLabel, actionLabel, onAction, secondaryLabel, onSecondary }: { eyebrow: string; title: string; subtitle: string; onBack?: () => void; backLabel?: string; actionLabel: string; onAction: () => void; secondaryLabel?: string; onSecondary?: () => void }) {
  return <header className="decisionHeader"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p>{subtitle}</p></div><div className="decisionHeaderActions">{onBack && !secondaryLabel && <button className="decisionSecondary" onClick={onBack}>← {backLabel}</button>}{secondaryLabel && onSecondary && <button className="decisionSecondary" onClick={onSecondary}>{secondaryLabel}</button>}<button className="decisionPrimary" onClick={onAction}>{actionLabel}</button></div></header>;
}

function ReleaseHero({ detail, decision = false }: { detail: CampaignDetail; decision?: boolean }) {
  const percent = detail.readiness.percent;
  return <section className="decisionHero"><div><p>{decision ? "DECISION PACKAGE" : "ACTIVE RELEASE"}</p><h2>{decision ? `Candidate baseline ${compactNumber(detail.baseline)}` : `Flight Management System ${detail.release}`}</h2><span>{decision ? `Flight Management System ${detail.release} · Built from baseline 1.5` : `${compactNumber(detail.baseline)} · ${detail.baselineState}`}</span>{decision && <small>◇ Integrity {detail.requirementsHash ? "verified" : "awaiting checkpoint"}</small>}</div><div className={`decisionScore${decision ? " compact" : ""}`}>{!decision && <div className="readinessRing" style={{ "--readiness": `${percent * 3.6}deg` } as React.CSSProperties}><b>{percent}%</b></div>}{decision && <b>{percent}%</b>}<span>Decision readiness</span><small>{detail.readiness.gates.filter((item) => !item.complete).length} blockers remain</small>{decision && <i><em style={{ width: `${percent}%` }} /></i>}</div></section>;
}

function ReadinessView({ detail, comparison, blockers, gate, onBack, onOpenDecision, onOpenImpact, openGate }: { detail: CampaignDetail; comparison?: Comparison; blockers: Gate[]; gate: (code: string) => Gate | undefined; onBack: () => void; onOpenDecision: () => void; onOpenImpact: (id: string) => void; openGate: (gate: Gate) => void }) {
  const trace = gate("traceability"), verification = gate("verification") ?? gate("coverage"), approval = gate("release_approval");
  const gateStatus = (item?: Gate) => !item ? "Not evaluated" : item.complete ? "Complete" : item.evaluationState === "WaitingForPrerequisite" ? "Waiting for baseline" : item.total > 0 ? `${Math.max(0, item.total - item.completed)} gaps` : "Review required";
  const stages = [
    ["Requirements", comparison?.summary.proposed ?? detail.changes.reduce((sum, item) => sum + item.requirementCount, 0), true],
    ["Changes", detail.changes.filter((item) => item.included).length, detail.changes.some((item) => item.included)],
    ["Trace", gateStatus(trace), trace?.complete],
    ["Verification", gateStatus(verification), verification?.complete],
    ["Baseline", compactNumber(detail.baseline), detail.baselineState === "Frozen" || detail.baselineState === "Released"],
    ["Release", detail.state === "Released" ? "Authorized" : "Awaiting decision", detail.state === "Released"],
  ] as [string, string | number, boolean | undefined][];
  return <>
    <PageHeader eyebrow={`FMS LIVE / RELEASE ${detail.release}`} title="Release Readiness" subtitle="A decision-ready view of the complete lifecycle." onBack={onBack} backLabel="Command Center" secondaryLabel={`Explore changes vs ${comparison?.fromRelease ?? "1.5"} →`} onSecondary={() => detail.changes[0] && onOpenImpact(detail.changes[0].id)} actionLabel="Review release evidence" onAction={onOpenDecision} />
    <ReleaseHero detail={detail} />
    <section className="lifecycleRail" aria-label="Release lifecycle">{stages.map(([label, value, complete], index) => <article className={complete ? "complete" : index < 4 ? "attention" : "pending"} key={label}><i>{complete ? "✓" : index === 5 ? "◇" : "!"}</i><b>{label}</b><span>{value}</span></article>)}</section>
    <div className="readinessGrid">
      {/* Every field here comes from the readiness gate the server computed. This panel previously showed an
          owner, a due date and a priority for each blocker, all invented in the browser and keyed by position
          in the list — the first blocker was always "Maya Patel, 24 Jul 2026, High" whatever it happened to
          be. AeroLink's whole claim is that it is the authoritative record; a decision screen must not make
          organisational facts up, least of all in the view a release is approved from. Remaining work and the
          action that clears it are real, and are what a reader actually needs. */}
      <section className="attentionCard"><div className="decisionCardTitle"><div><h2>What needs attention</h2><p>The exact work preventing a release decision</p></div><b>{blockers.length}</b></div>{blockers.map((item) => <article className={item.evaluationState === "WaitingForPrerequisite" ? "waiting" : ""} key={item.code}><span className="attentionIcon">{item.evaluationState === "WaitingForPrerequisite" ? "…" : "!"}</span><div className="attentionCopy"><b>{item.name}</b><small>{item.detail}</small></div><div className="attentionRemaining"><span className="attentionMeta">{item.evaluationState === "WaitingForPrerequisite" ? "Status" : "Remaining"}</span><b>{item.evaluationState === "WaitingForPrerequisite" ? "Not evaluated" : item.total > 0 ? `${Math.max(0, item.total - item.completed)} of ${item.total}` : "Not started"}</b></div><div className="attentionAction"><span className="attentionMeta">To clear this</span><small>{item.action}</small></div><button onClick={() => openGate(item)}>{item.evaluationState === "WaitingForPrerequisite" ? "Open prerequisite →" : "Open exact work →"}</button></article>)}{!blockers.length && <div className="decisionEmpty"><b>✓ Every release gate is complete</b><span>The package is ready for formal authority.</span></div>}</section>
      <aside className="comparisonCard"><div className="decisionCardTitle"><div><h2>Release comparison</h2><p>Released baseline to current candidate</p></div></div><div className="versionFlow"><div><b>{comparison?.fromRelease ?? "1.5"}</b><span>Released</span></div><i>→</i><div><b>{detail.release}</b><span>Candidate</span></div></div><dl>{[["Introduced", comparison?.summary.added ?? 0, "added"],["Modified", comparison?.summary.modified ?? comparison?.summary.proposed ?? 0, "modified"],["Retired", comparison?.summary.retired ?? 0, "retired"],["Unchanged", comparison?.summary.unchanged ?? 0, "unchanged"]].map(([label, value, tone]) => <div key={String(label)} className={String(tone)}><dt>{label}</dt><dd>{comparison?.toMaterialized ? value : "—"}</dd></div>)}</dl>{comparison && !comparison.toMaterialized && <p className="comparisonPending">Counts become exact when the candidate baseline is materialized.</p>}<button onClick={() => detail.changes[0] && onOpenImpact(detail.changes[0].id)}>Explore baseline changes →</button></aside>
    </div>
    <section className="healthStrip"><HealthCard label="Trace coverage" gate={trace} tone="green" /><HealthCard label="Verification pass" gate={verification} tone="amber" /><HealthCard label="Approval progress" gate={approval} tone="blue" /></section>
  </>;
}

function HealthCard({ label, gate, tone }: { label: string; gate?: Gate; tone: string }) {
  const measured = Boolean(gate && gate.total > 0);
  const percent = gate?.complete && !measured ? 100 : measured ? Math.round((gate!.completed / gate!.total) * 100) : 0;
  const waiting = gate?.evaluationState === "WaitingForPrerequisite";
  return <article className={`${tone}${!measured && !gate?.complete ? " pendingMetric" : ""}`}><span>{label}</span><b>{measured || gate?.complete ? `${percent}%` : "Pending"}</b><small>{gate?.complete ? "Target achieved" : waiting ? "Waiting for baseline materialization" : measured ? `${Math.max(0, gate!.total - gate!.completed)} checks remaining` : "Evaluation not started"}</small><i><em style={{ width: `${percent}%` }} /></i></article>;
}

function ImpactView({ detail, scr, affected, selectedIndex, selected, onSelect, onBack, onOpenScr, onOpenVerification }: { detail: CampaignDetail; scr?: ScrDetail; affected: AffectedRequirement[]; selectedIndex: number; selected?: AffectedRequirement; onSelect: (index: number) => void; onBack: () => void; onOpenScr: () => void; onOpenVerification: () => void }) {
  const [query, setQuery] = useState("");
  if (!scr) return <section className="decisionState"><div className="decisionLoader" /><h1>Loading controlled impact</h1></section>;
  const counts = scr.requirementChanges.reduce<Record<string, number>>((total, item) => ({ ...total, [item.kind]: (total[item.kind] ?? 0) + 1 }), {});
  const person = demoPerson(scr.authorId, scr.authorId, "Change owner") ?? decisionPeople.systems;
  const impact = selected?.impact;
  const tests = impact?.tests.slice(0, 2) ?? [];
  const traceCount = (impact?.parents.length ?? 0) + (impact?.children.length ?? 0);
  const gapCount = Math.max(0, tests.length ? tests.filter((item) => item.state !== "Approved" || item.isSuspect).length : 1);
  const visibleAffected = affected.map((item, index) => ({ item, index })).filter(({ item }) => !query.trim() || `${item.displayNumber} ${item.statement} ${item.kind}`.toLowerCase().includes(query.trim().toLowerCase()));
  return <>
    <PageHeader eyebrow={`FMS LIVE / RELEASE ${detail.release} / ${compactNumber(scr.displayNumber)}`} title="Change Impact Review" subtitle="Understand the full lifecycle effect before making a decision." onBack={onBack} backLabel="Back to readiness" actionLabel="Open in Changes" onAction={onOpenScr} />
    <section className="changeSummary"><div className="changeStatus">{scr.state.replace(/([A-Z])/g, " $1").trim()}</div><div className="changeIdentity"><h2>{compactNumber(scr.displayNumber)} · {scr.title}</h2><p>{scr.solution}</p><div><PersonAvatar userName={scr.authorId} displayName={person.name} size="small" /><span><small>Owner</small><b>{person.name}</b></span><span><small>Target</small><b>Release {detail.release}</b></span><span><small>Updated</small><b>{new Date(scr.updatedAt).toLocaleDateString()}</b></span></div></div><div className="changeCounts"><span><b>{counts.Introduce ?? 0}</b>Introduced</span><span><b>{counts.Modify ?? 0}</b>Modified</span><span><b>{counts.Retire ?? 0}</b>Retired</span></div><div className="impactBadge">△ {scr.requirementChanges.length > 5 ? "Medium" : "Focused"} impact</div></section>
    <div className="impactGrid">
      <section className="affectedCard"><div className="decisionCardTitle"><div><h2>Affected requirements</h2><p>{countLabel(affected.length, "controlled revision")}</p></div></div><input className="affectedSearch" aria-label="Search affected requirements" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search affected requirements…" />{visibleAffected.map(({ item, index }) => <button className={index === selectedIndex ? "selected" : ""} key={item.id} onClick={() => onSelect(index)}><i className={item.kind.toLowerCase()}>{item.kind === "Introduce" ? "+" : item.kind === "Retire" ? "−" : "○"}</i><span><b>{compactNumber(item.displayNumber)}</b><small>{item.statement.slice(0, 58)}{item.statement.length > 58 ? "…" : ""}</small></span><em>{item.kind}</em></button>)}{!visibleAffected.length && <div className="affectedEmpty">No affected requirements match this search.</div>}</section>
      <section className="impactMap"><div className="decisionCardTitle"><div><h2>Lifecycle impact</h2><p>A readable path from change to evidence</p></div><span className="mapZoom">⌕　⛶　100%</span></div><div className="mapStats"><span>Upstream <b>{impact?.parents.length ?? 0}</b></span><span>Downstream <b>{(impact?.children.length ?? 0) + tests.length}</b></span><span>Gaps <b>{gapCount}</b></span></div><div className="impactCanvas"><div className="mapColumn requirements"><Node tone="blue" title={compactNumber(selected?.displayNumber) || "Requirement"} subtitle="Requirement" /><Node tone="blue" title={compactNumber(impact?.parents[0]?.displayNumber) || "Parent trace"} subtitle="Upstream" /></div><div className="mapConnector"><span>modifies</span></div><Node tone="navy" title={compactNumber(scr.displayNumber)} subtitle="Change" /><div className="mapConnector"><span>verified by</span></div><div className="mapColumn tests"><Node tone="violet" title={compactNumber(tests[0]?.displayNumber) || "Test procedure"} subtitle="Verification" /><Node tone="violet" title={compactNumber(tests[1]?.displayNumber) || "Coverage review"} subtitle="Verification" /></div><div className="mapConnector"><span>produced</span></div><div className="mapColumn evidence"><Node tone="green" title="Latest result" subtitle={gapCount ? "Review" : "Passed"} /><Node tone="amber" title="Trace evidence" subtitle={traceCount ? "Linked" : "Gap"} /></div></div></section>
      <aside className="decisionContext"><h2>Decision context</h2><section><h3>Why this change</h3><p>{scr.problem}</p></section><section><h3>Trace health</h3><p>{traceCount ? `${traceCount} relationship paths identified` : "Trace review is required"}</p><i><em style={{ width: `${traceCount ? 88 : 35}%` }} /></i></section><section className="openConcern"><b>△ Open concern</b><p>{gapCount ? "Verification evidence requires review." : "No open verification concern."}</p></section><section><h3>Baseline effect</h3><p>{detail.changes.find((item) => item.id === scr.id)?.included ? `Included in ${compactNumber(detail.baseline)}` : `Proposed for release ${detail.release}`}</p></section><button onClick={onOpenVerification}>Open missing verification →</button></aside>
    </div>
    <section className="revisionPreview"><div className="decisionCardTitle"><div><h2>Requirement revision preview</h2><p>{compactNumber(selected?.displayNumber)}</p></div><b>{selected?.kind === "Introduce" ? `New requirement → Revision ${selected?.revision ?? 0}` : `Revision ${Math.max(0, (selected?.revision ?? 1) - 1)} → Revision ${selected?.revision ?? 1}`}</b></div><div><article><span>1.5 baseline</span><p>{selected?.currentStatement || "This requirement is introduced in release 1.6."}</p></article><article><span>1.6 proposed</span><p>{selected?.statement}</p></article></div><button onClick={onOpenScr}>View complete controlled change →</button></section>
  </>;
}

function Node({ tone, title, subtitle }: { tone: string; title: string; subtitle: string }) { return <div className={`impactNode ${tone}`}><b>{title}</b><span>{subtitle}</span></div>; }

function DecisionView({ detail, comparison, documents, blockers, decision, onDecision, onBack, onRecord, onOpenDocuments, openGate }: { detail: CampaignDetail; comparison?: Comparison; documents: DocumentRecord[]; blockers: Gate[]; decision: "Approve" | "Approve with conditions" | "Return for rework"; onDecision: (decision: "Approve" | "Approve with conditions" | "Return for rework") => void; onBack: () => void; onRecord: () => void; onOpenDocuments: () => void; openGate: (gate: Gate) => void }) {
  const checklist = [
    ["Requirements baseline", detail.requirementsHash ? "Exact revisions · Complete" : "Materialization required", "baseline"],
    ["Selected changes", `${countLabel(detail.changes.filter((item) => item.included).length, "approved SCR")} · ${detail.changes.some((item) => item.included) ? "Complete" : "Attention"}`, "change_control"],
    ["Traceability", detail.readiness.gates.find((item) => item.code === "traceability")?.detail ?? "Trace evidence current", "traceability"],
    ["Verification results", detail.readiness.gates.find((item) => item.code === "verification")?.detail ?? "Results current", "verification"],
    ["Controlled documents", detail.readiness.gates.find((item) => item.code === "documents")?.detail ?? "Outputs generated", "documents"],
    ["Integrity checkpoint", detail.requirementsHash ? "Hashes verified · Complete" : "Checkpoint pending", "baseline"],
  ] as const;
  const approvalPeople = [decisionPeople.systems, decisionPeople.verification, decisionPeople.safety, decisionPeople.release];
  const approvals = detail.approvals.length ? detail.approvals.map((item) => ({ ...item, person: demoPerson(item.approverId, item.approverName) })) : approvalPeople.map((person, index) => ({ position: index + 1, approverId: ["systems.lead", "test.engineer", "program.manager", "release.manager"][index], approverName: person.name, state: "Waiting", person }));
  const approved = detail.approvals.filter((item) => item.state === "Approved").length;
  const stages = [["Scope locked", Boolean(detail.baselineId)],["Trace reviewed", detail.readiness.gates.find((item) => item.code === "traceability")?.complete],["Tests accepted", detail.readiness.gates.find((item) => item.code === "verification")?.complete],["Approvals complete", detail.approvals.length > 0 && approved === detail.approvals.length],["Release authorized", detail.state === "Released"]] as [string, boolean | undefined][];
  const currentDocs = documents.filter((item) => item.release === detail.release || item.baseline === detail.baseline).slice(0, 3);
  return <>
    <PageHeader eyebrow={`FMS LIVE / RELEASE ${detail.release} / DECISION PACKAGE`} title="Release Evidence & Decision" subtitle={`Review the exact evidence supporting candidate baseline ${compactNumber(detail.baseline)}.`} onBack={onBack} backLabel="Back to readiness" actionLabel={blockers.length ? `Resolve ${blockers.length} blockers` : "Record decision"} onAction={blockers[0] ? () => openGate(blockers[0]) : onRecord} />
    <ReleaseHero detail={detail} decision />
    <section className="decisionStageRail">{stages.map(([label, complete], index) => <article className={complete ? "complete" : index === stages.findIndex((item) => !item[1]) ? "active" : "locked"} key={label}><i>{complete ? "✓" : index + 1}</i><b>{label}</b></article>)}</section>
    <div className="evidenceGrid"><section className="evidenceChecklist"><div className="decisionCardTitle"><div><h2>Evidence checklist</h2><p>Every claim opens its authoritative record</p></div></div><div>{checklist.map(([label, status, code]) => { const item = detail.readiness.gates.find((candidate) => candidate.code === code); const complete = item?.complete ?? status.includes("Complete"); return <button key={label} onClick={() => item ? openGate(item) : onOpenDocuments()}><i className={complete ? "complete" : "attention"}>{complete ? "✓" : "△"}</i><span><b>{label}</b><small>{status}</small></span><em>View →</em></button>; })}</div></section>
      <section className="releaseNarrative"><div className="decisionCardTitle"><div><h2>Release narrative</h2><p>What changes from 1.5 to {detail.release}</p></div></div><div className="narrativeFlow"><span>Baseline {comparison?.fromRelease ?? "1.5"}</span><i>→</i><b>Candidate<br />{compactNumber(detail.baseline)}</b>{[[comparison?.summary.added ?? 0,"introduced"],[comparison?.summary.modified ?? comparison?.summary.proposed ?? 0,"modified"],[comparison?.summary.retired ?? 0,"retired"],[comparison?.summary.unchanged ?? 0,"unchanged"]].map(([value, label]) => <em key={String(label)}><strong>{value}</strong>{label}</em>)}</div><ul>{detail.changes.slice(0, 3).map((item) => <li key={item.id}>✓ {item.title}</li>)}</ul><button onClick={onOpenDocuments}>Review complete baseline comparison →</button></section>
      <section className="approvalPath"><div className="decisionCardTitle"><div><h2>Approval path</h2><p>Ordered electronic authorization</p></div></div><div className="approvalTimeline">{approvals.map((item) => <article key={item.position} className={item.state.toLowerCase()}><i>{item.position}</i><PersonAvatar userName={item.approverId} displayName={item.person?.name ?? item.approverName} size="small" /><span><b>{item.person?.role ?? item.approverName}</b><small>{item.person?.name ?? item.approverName} · {stateLabel(item.state)}</small></span></article>)}</div><div className="approvalProgress"><span>{approved} of {Math.max(detail.approvals.length, approvals.length)} approvals complete</span><i><em style={{ width: `${approved / Math.max(1, detail.approvals.length || approvals.length) * 100}%` }} /></i></div></section>
      <aside className="decisionSide"><section className="decisionBlockers"><h2>△ Decision blockers</h2>{blockers.slice(0, 3).map((item) => <button key={item.code} onClick={() => openGate(item)}><span>{item.evaluationState === "WaitingForPrerequisite" ? "Waiting" : Math.max(0, item.total - item.completed)} · {item.name}</span><b>Open →</b></button>)}</section>{/* A released build has no decision left to take. The controls were only ever disabled by outstanding
          blockers, and a shipped release has none — so this panel offered to approve a release that had
          already happened, which is an invitation to sign something twice. It reports the decision instead.
          The domain refuses the mutation regardless; this is so nobody is asked to attempt it. */}
      {detail.state === "Released"
        ? <section className="yourDecision decided"><h2>Decision recorded</h2><p>{detail.approvals.length === 1 ? "One approver signed" : `${detail.approvals.length} approvers signed`} this release, and the build shipped. A released campaign is closed: its evidence, approvals and package are fixed as they were at release.</p></section>
        : <section className="yourDecision"><h2>Your decision</h2><div>{(["Approve", "Approve with conditions", "Return for rework"] as const).map((item) => <button disabled={Boolean(blockers.length)} className={decision === item ? "selected" : ""} onClick={() => onDecision(item)} key={item}>{item}</button>)}</div><p>{blockers.length ? "Complete the evidence gates before choosing and signing a decision." : "A rationale and electronic signature are required."}</p><button className="decisionPrimary" onClick={blockers[0] ? () => openGate(blockers[0]) : onRecord}>{blockers.length ? "Resolve blockers →" : "Record decision"}</button></section>}</aside>
    </div>
    <section className="controlledOutputs"><div className="decisionCardTitle"><div><h2>Controlled outputs</h2><p>Exact generated artifacts and integrity state</p></div></div><div>{(currentDocs.length ? currentDocs : [{ id: "sysrd", displayNumber: "SYSRD-000016.00", title: "System Requirements Document", contentHash: detail.requirementsHash ?? "", type: "Sysrd", artifactCount: 0, generatedAt: "", release: detail.release, baseline: detail.baseline }, { id: "trace", displayNumber: "TRACE-000016.00", title: "Traceability Annex", contentHash: detail.requirementsHash ?? "", type: "Trace", artifactCount: 0, generatedAt: "", release: detail.release, baseline: detail.baseline }]).map((item) => <button key={item.id} onClick={onOpenDocuments}><i>▤</i><span><b>{compactNumber(item.displayNumber)}</b><small>{item.title}</small></span><em>{item.contentHash ? `Generated · ${item.contentHash.slice(0, 10)}…` : "Pending decision"}</em></button>)}<button className="pendingOutput"><i>▤</i><span><b>Release Decision Record</b><small>Created when authority is complete</small></span><em>Pending decision</em></button></div><button onClick={onOpenDocuments}>Open artifact &amp; build explorer →</button></section>
  </>;
}
