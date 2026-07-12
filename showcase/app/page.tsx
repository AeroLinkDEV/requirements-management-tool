"use client";

import { useMemo, useState } from "react";

type View = "dashboard" | "scr" | "requirements" | "verification" | "baseline" | "traceability";
type Role = "manager" | "engineer";

const navItems: { id: View; label: string; icon: string }[] = [
  { id: "dashboard", label: "Dashboard", icon: "⌂" },
  { id: "scr", label: "Change Control", icon: "◇" },
  { id: "requirements", label: "Requirements", icon: "▤" },
  { id: "verification", label: "Verification", icon: "✓" },
  { id: "baseline", label: "Baselines", icon: "▱" },
  { id: "traceability", label: "Traceability", icon: "⌁" },
];

const approvers = [
  { name: "Maya Chen", role: "Systems Engineering Lead", state: "approved" },
  { name: "David Lee", role: "Software Engineering Lead", state: "active" },
  { name: "Sarah Rodriguez", role: "Verification Lead", state: "pending" },
  { name: "James Turner", role: "Configuration Management", state: "pending" },
];

const systemRequirements = [
  { id: "SYSR-00002375.01", title: "Provide selectable Round Robin route sequencing", hlrs: ["HLR-00003142.01", "HLR-00003143.01"], status: "Ready" },
  { id: "SYSR-00002376.01", title: "Advance to the next eligible waypoint after the configured trigger", hlrs: ["HLR-00003144.01"], status: "Suspect link" },
  { id: "SYSR-00002377.01", title: "Skip ineligible waypoints without corrupting sequence state", hlrs: ["HLR-00003145.01", "HLR-00003146.01"], status: "Ready" },
  { id: "SYSR-00002378.01", title: "Display current Round Robin state and selected waypoint", hlrs: ["HLR-00003147.01"], status: "Ready" },
  { id: "SYSR-00002379.01", title: "Preserve sequence state across applicable mode transitions", hlrs: ["HLR-00003148.01", "HLR-00003149.01"], status: "Ready" },
];

const prFixes = [
  { id: "PR-00002841", title: "Route discontinuity remains after waypoint deletion", state: "Verified" },
  { id: "PR-00002842", title: "Hold page displays stale leg information", state: "Verified" },
  { id: "PR-00002843", title: "Crossfill duplicates advisory after rapid reconnect", state: "In verification" },
  { id: "PR-00002844", title: "Invalid database record can block route activation", state: "Coverage gap" },
];

function Status({ children, tone = "neutral" }: { children: React.ReactNode; tone?: "green" | "amber" | "red" | "cyan" | "neutral" }) {
  return <span className={`status status-${tone}`}>{children}</span>;
}

function Metric({ label, value, detail, tone, onClick }: { label: string; value: string; detail: string; tone?: string; onClick?: () => void }) {
  return (
    <button className="metric-card" onClick={onClick} aria-label={`${label}: ${value}. ${detail}`}>
      <span className="metric-label">{label}</span>
      <strong className={tone ? `metric-${tone}` : ""}>{value}</strong>
      <span className="metric-detail">{detail}</span>
      <span className="metric-link">View contributing records →</span>
    </button>
  );
}

export default function Home() {
  const [view, setView] = useState<View>("dashboard");
  const [role, setRole] = useState<Role>("manager");
  const [selectedScr, setSelectedScr] = useState<"round-robin" | "pr-fixes">("round-robin");
  const [approvalStep, setApprovalStep] = useState(1);
  const [futureApprover, setFutureApprover] = useState("Sarah Rodriguez");
  const [cycleCancelled, setCycleCancelled] = useState(false);
  const [impactResolved, setImpactResolved] = useState(false);
  const [coverageResolved, setCoverageResolved] = useState(false);
  const [testRetested, setTestRetested] = useState(false);
  const [toast, setToast] = useState("");

  const readiness = useMemo(() => {
    let score = 58;
    if (impactResolved) score += 12;
    if (coverageResolved) score += 12;
    if (testRetested) score += 10;
    if (approvalStep >= 4) score += 8;
    return score;
  }, [impactResolved, coverageResolved, testRetested, approvalStep]);

  function notify(message: string) {
    setToast(message);
    window.setTimeout(() => setToast(""), 2800);
  }

  function openScr(which: "round-robin" | "pr-fixes") {
    setSelectedScr(which);
    setView("scr");
  }

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand"><span className="brand-mark">A</span><div><b>AEROLINK</b><small>ASSURANCE PLATFORM</small></div></div>
        <div className="program-context"><span>PROGRAM</span><b>Program Atlas</b><small>Flight Management System</small><small>Target · Software 3.3</small></div>
        <nav aria-label="Primary">
          {navItems.map((item) => (
            <button key={item.id} className={view === item.id ? "active" : ""} onClick={() => setView(item.id)}>
              <span className="nav-icon" aria-hidden="true">{item.icon}</span>{item.label}
            </button>
          ))}
        </nav>
        <div className="sidebar-footer"><span className="online-dot" /> Local concept · Fictional data</div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div><span className="breadcrumb">Program Atlas / FMS / Software 3.3</span><h1>{view === "dashboard" ? "Release Command Center" : navItems.find((n) => n.id === view)?.label}</h1></div>
          <div className="top-actions">
            <div className="role-switch" aria-label="Dashboard role">
              <button className={role === "manager" ? "selected" : ""} onClick={() => { setRole("manager"); setView("dashboard"); }}>Manager</button>
              <button className={role === "engineer" ? "selected" : ""} onClick={() => { setRole("engineer"); setView("dashboard"); }}>System Engineer</button>
            </div>
            <button className="icon-button" aria-label="Notifications">◉<span>4</span></button>
            <div className="avatar">AS</div>
          </div>
        </header>

        {view === "dashboard" && (
          <div className="page dashboard-page">
            <div className="page-intro"><div><span className="eyebrow">{role === "manager" ? "MANAGEMENT VIEW" : "ENGINEERING WORKSPACE"}</span><h2>{role === "manager" ? "FMS 3.3 is progressing—with four items blocking readiness" : "Your next actions are concentrated in three controlled records"}</h2><p>Every summary is scoped to Program Atlas · FMS · Software 3.3 and drills into the exact fictional records behind it.</p></div><div className="readiness-ring" style={{ "--score": `${readiness * 3.6}deg` } as React.CSSProperties}><div><strong>{readiness}%</strong><span>READY</span></div></div></div>

            <div className="metrics-grid">
              <Metric label="SCR approval" value={`${approvalStep}/4`} detail="Round Robin review sequence" tone="cyan" onClick={() => openScr("round-robin")} />
              <Metric label="Traceability" value={impactResolved ? "100%" : "96%"} detail={impactResolved ? "All required links reviewed" : "1 suspect link needs review"} tone={impactResolved ? "green" : "amber"} onClick={() => setView("traceability")} />
              <Metric label="Verification" value={coverageResolved ? (testRetested ? "100%" : "94%") : "88%"} detail={!coverageResolved ? "1 coverage gap" : !testRetested ? "1 blocked execution" : "All applicable evidence accepted"} tone={testRetested ? "green" : "amber"} onClick={() => setView("verification")} />
              <Metric label="Baseline checks" value={`${[impactResolved, coverageResolved, testRetested, approvalStep >= 4].filter(Boolean).length + 8}/12`} detail="Candidate BL-00000217" tone={readiness === 100 ? "green" : "cyan"} onClick={() => setView("baseline")} />
            </div>

            {role === "manager" ? (
              <div className="dashboard-columns">
                <section className="panel release-panel"><div className="panel-heading"><div><span className="eyebrow">RELEASE PACKAGE</span><h3>Two SCRs build Software 3.3 from baseline 3.2</h3></div><Status tone="amber">4 blockers</Status></div>
                  <button className="release-row" onClick={() => openScr("round-robin")}><span className="release-id">SCR-0001049.01</span><span><b>Introduce Round Robin function</b><small>5 system requirements · 8 HLRs · 4 test procedures</small></span><Status tone={approvalStep >= 4 ? "green" : "cyan"}>{approvalStep >= 4 ? "Approved" : `Approval ${approvalStep + 1} of 4`}</Status></button>
                  <button className="release-row" onClick={() => openScr("pr-fixes")}><span className="release-id">SCR-0001050.01</span><span><b>Resolve four FMS problem reports</b><small>4 PRs · 1 coverage gap · 1 blocked execution</small></span><Status tone="amber">In work</Status></button>
                </section>
                <section className="panel attention-panel"><div className="panel-heading"><div><span className="eyebrow">NEEDS ATTENTION</span><h3>What is blocking release readiness</h3></div></div>
                  {[
                    [!impactResolved, "Suspect link", "SYSR-00002376.01 → TP-00004502.01", () => setView("traceability")],
                    [!coverageResolved, "Coverage gap", "PR-00002844 requires an additional negative test", () => setView("verification")],
                    [!testRetested, "Blocked execution", "EXEC-00008794.01 could not reach a valid conclusion", () => setView("verification")],
                    [approvalStep < 4, "Approval pending", `${approvers[Math.min(approvalStep, 3)].name} is next in sequence`, () => openScr("round-robin")],
                  ].filter((x) => x[0]).map((x) => <button className="attention-row" key={String(x[1])} onClick={x[3] as () => void}><span className="attention-dot" /><span><b>{String(x[1])}</b><small>{String(x[2])}</small></span><span>→</span></button>)}
                  {readiness === 100 && <div className="all-clear"><b>All candidate checks are satisfied.</b><span>Software 3.3 is ready for baseline approval.</span></div>}
                </section>
              </div>
            ) : (
              <div className="dashboard-columns">
                <section className="panel work-panel"><div className="panel-heading"><div><span className="eyebrow">MY CONTROLLED WORK</span><h3>Three actions move the release forward</h3></div></div>
                  <button className="work-item" onClick={() => openScr("round-robin")}><span className="priority priority-high">1</span><span><b>Review Round Robin SCR package</b><small>Compare system requirements and allocated HLRs before your approval stage.</small></span><Status tone="cyan">Active</Status></button>
                  <button className="work-item" onClick={() => setView("traceability")}><span className="priority">2</span><span><b>Disposition suspect verification link</b><small>SYSR-00002376.01 changed during SCR review.</small></span><Status tone={impactResolved ? "green" : "amber"}>{impactResolved ? "Resolved" : "Required"}</Status></button>
                  <button className="work-item" onClick={() => setView("verification")}><span className="priority">3</span><span><b>Close PR-00002844 coverage gap</b><small>Add a negative test and review the corrected execution evidence.</small></span><Status tone={testRetested ? "green" : "amber"}>{testRetested ? "Closed" : "Required"}</Status></button>
                </section>
                <section className="panel activity-panel"><div className="panel-heading"><div><span className="eyebrow">RECENT ACTIVITY</span><h3>Changes relevant to you</h3></div></div>
                  {["Maya Chen approved SCR-0001049.01", "PR-00002844 flagged as not fully covered", "EXEC-00008794.01 recorded as Blocked", "BL-00000217 candidate checks recalculated"].map((a, i) => <div className="timeline-row" key={a}><span className="timeline-node" /><span><b>{a}</b><small>{i + 1} hour{i ? "s" : ""} ago · Program Atlas</small></span></div>)}
                </section>
              </div>
            )}
          </div>
        )}

        {view === "scr" && (
          <div className="page">
            <div className="subnav"><button className={selectedScr === "round-robin" ? "active" : ""} onClick={() => setSelectedScr("round-robin")}>SCR-0001049.01 · Round Robin</button><button className={selectedScr === "pr-fixes" ? "active" : ""} onClick={() => setSelectedScr("pr-fixes")}>SCR-0001050.01 · Four PR fixes</button></div>
            {selectedScr === "round-robin" ? (
              <>
                <div className="detail-header"><div><span className="eyebrow">SYSTEM CHANGE REQUEST</span><h2>Introduce Round Robin function</h2><p>Target release · FMS Software 3.3</p></div><div className="header-status"><Status tone="cyan">In review</Status><span>Revision-qualified ID · SCR-0001049.01</span></div></div>
                <div className="workflow-strip">
                  {approvers.map((a, i) => { const effectiveName = i === 2 ? futureApprover : a.name; const state = cycleCancelled ? (i === 0 ? "active" : "pending") : i < approvalStep ? "approved" : i === approvalStep ? "active" : "pending"; return <div key={`${effectiveName}-${i}`} className={`workflow-step ${state}`}><span>{i + 1}</span><div><b>{effectiveName}</b><small>{i === 2 ? "Verification Lead" : a.role}</small><em>{state === "approved" ? "Approved" : state === "active" ? "Reviewing now" : "Waiting"}</em></div>{state === "pending" && <button onClick={() => { if (i > approvalStep) { setFutureApprover(futureApprover === "Sarah Rodriguez" ? "Priya Nair" : "Sarah Rodriguez"); notify("Future approver changed and audit event recorded."); } }} disabled={i <= approvalStep}>{i > approvalStep ? "Replace" : "Locked"}</button>}</div>; })}
                </div>
                {cycleCancelled && <div className="alert alert-red"><b>Previous review cycle cancelled</b><span>A wrong completed approver was identified. Historical decisions no longer count; the corrected sequence restarted from step 1.</span></div>}
                <div className="scr-layout">
                  <section className="panel scr-content"><div className="pas-grid"><div><span>PROBLEM</span><p>FMS route sequencing requires a controlled method to cycle repeatedly through an eligible waypoint set.</p></div><div><span>ANALYSIS</span><p>Existing sequencing supports linear progression but does not preserve deterministic Round Robin state across mode transitions.</p></div><div><span>SOLUTION</span><p>Add selectable Round Robin sequencing with eligibility, display, transition, and recovery behavior allocated to software HLRs.</p></div></div>
                    <div className="section-heading"><h3>Contained requirement changes</h3><button onClick={() => setView("requirements")}>Open decomposition →</button></div>
                    {systemRequirements.slice(0, 3).map((r) => <div className="requirement-row" key={r.id}><span><b>{r.id}</b><small>{r.title}</small></span><span className="hlr-count">{r.hlrs.length} HLR{r.hlrs.length > 1 ? "s" : ""}</span><Status tone={r.status === "Ready" || impactResolved ? "green" : "amber"}>{r.status === "Suspect link" && impactResolved ? "Resolved" : r.status}</Status></div>)}
                  </section>
                  <aside className="panel review-panel"><span className="eyebrow">ACTIVE APPROVAL</span><h3>{cycleCancelled ? "Maya Chen" : approvers[Math.min(approvalStep, 3)].name}</h3><p>Approving the complete submitted snapshot: P/A/S, five system requirements, eight HLRs and affected verification.</p><div className="snapshot"><span>Submitted snapshot</span><b>Cycle {cycleCancelled ? "2" : "1"} · SCR-0001049.01</b><small>Content hash · 9c82…4fa1</small></div>
                    <button className="primary-action" onClick={() => { setCycleCancelled(false); setApprovalStep(Math.min(4, approvalStep + 1)); notify(approvalStep >= 3 ? "SCR unanimously approved." : "Approval recorded; workflow advanced."); }}>{approvalStep >= 3 ? "Complete final approval" : "Approve and advance"}</button>
                    <button className="secondary-action" onClick={() => { setApprovalStep(0); notify("SCR returned to Draft at revision .01; review-cycle history preserved."); }}>Request changes</button>
                    <button className="danger-link" onClick={() => { setCycleCancelled(true); setApprovalStep(0); notify("Review cycle cancelled and restarted from the first approver."); }}>Wrong completed approver · cancel cycle</button>
                  </aside>
                </div>
              </>
            ) : (
              <>
                <div className="detail-header"><div><span className="eyebrow">SYSTEM CHANGE REQUEST</span><h2>Resolve four FMS problem reports</h2><p>Target release · FMS Software 3.3</p></div><div className="header-status"><Status tone="amber">In work</Status><span>SCR-0001050.01</span></div></div>
                <div className="panel pr-panel"><div className="pas-grid compact"><div><span>PROBLEM</span><p>Four released FMS behaviors have confirmed defects.</p></div><div><span>ANALYSIS</span><p>Each issue requires controlled requirement and regression-test impact.</p></div><div><span>SOLUTION</span><p>Deliver four fixes together in Software 3.3 with complete verification evidence.</p></div></div>
                  <h3>Linked problem reports</h3>{prFixes.map((pr) => <button className="pr-row" key={pr.id} onClick={() => pr.id === "PR-00002844" && setView("verification")}><span><b>{pr.id}</b><small>{pr.title}</small></span><Status tone={pr.state === "Verified" ? "green" : "amber"}>{pr.id === "PR-00002844" && coverageResolved ? "Procedure added" : pr.state}</Status><span>→</span></button>)}
                </div>
              </>
            )}
          </div>
        )}

        {view === "requirements" && (
          <div className="page"><div className="detail-header"><div><span className="eyebrow">ROUND ROBIN DECOMPOSITION</span><h2>System intent allocated to software HLRs</h2><p>All proposed revisions are contained in SCR-0001049.01 and become effective only through baseline selection.</p></div><Status tone="cyan">5 SYSR · 8 HLR</Status></div>
            <div className="trace-list">{systemRequirements.map((r, i) => <div className="trace-row" key={r.id}><div className="trace-node sys"><span>SYSTEM REQUIREMENT</span><b>{r.id}</b><p>{r.title}</p></div><div className="trace-arrow"><span>ALLOCATED TO</span>→</div><div className="hlr-stack">{r.hlrs.map((h, j) => <button key={h} className="trace-node hlr"><span>SOFTWARE HLR</span><b>{h}</b><p>{["Maintain the active Round Robin waypoint set.", "Apply deterministic selection and sequencing rules.", "Advance when the configured trigger is satisfied.", "Evaluate waypoint eligibility before selection.", "Preserve sequence index when an item is skipped.", "Publish active waypoint and mode state to the display.", "Serialize Round Robin state during mode exit.", "Restore or reset state according to transition rules."][Math.min(i * 2 + j, 7)]}</p></button>)}</div><Status tone={r.status === "Ready" || impactResolved ? "green" : "amber"}>{r.status === "Suspect link" && impactResolved ? "Resolved" : r.status}</Status></div>)}</div>
          </div>
        )}

        {view === "verification" && (
          <div className="page"><div className="detail-header"><div><span className="eyebrow">PR-00002844 · VERIFICATION COVERAGE</span><h2>Close the gap without hiding the blocked execution</h2><p>Invalid database record handling · FMS Software 3.3</p></div><Status tone={testRetested ? "green" : "amber"}>{testRetested ? "Verified" : coverageResolved ? "Retest required" : "Coverage gap"}</Status></div>
            <div className="verification-layout"><section className="panel evidence-main"><div className="coverage-callout"><div><span className="eyebrow">APPLICABLE REQUIREMENT</span><h3>SYSR-00001987.03</h3><p>The FMS shall reject an invalid navigation database record without preventing route activation using remaining valid records.</p></div><button className="primary-action small" onClick={() => { setCoverageResolved(true); notify("TP-00004511.01 added; coverage recalculated."); }} disabled={coverageResolved}>{coverageResolved ? "Procedure added" : "Add negative test procedure"}</button></div>
              <div className="procedure-card"><div><span className="eyebrow">TEST PROCEDURE</span><h3>{coverageResolved ? "TP-00004511.01 · Invalid record rejection" : "No complete verification procedure"}</h3></div><Status tone={coverageResolved ? "cyan" : "red"}>{coverageResolved ? "Ready for execution" : "Gap"}</Status></div>
              <div className="execution-history"><h3>Execution history</h3><div className="execution blocked"><span className="execution-marker">!</span><div><b>EXEC-00008794.01 · Blocked</b><p>Bench configuration could not inject the invalid record while preserving remaining database content.</p><small>Human conclusion · No valid verification result reached</small></div><Status tone="amber">Blocked</Status></div>{testRetested && <div className="execution pass"><span className="execution-marker">✓</span><div><b>EXEC-00008821.01 · Pass</b><p>Corrected procedure and bench configuration verified rejection behavior and successful route activation.</p><small>Reviewed by Priya Nair · Evidence hash 6d4a…91f7</small></div><Status tone="green">Pass</Status></div>}</div>
              <button className="primary-action" disabled={!coverageResolved || testRetested} onClick={() => { setTestRetested(true); notify("Retest recorded and human-approved Pass applied."); }}>{testRetested ? "Retest complete" : coverageResolved ? "Run simulated retest" : "Resolve coverage gap first"}</button>
              </section><aside className="panel evidence-side"><span className="eyebrow">CONTROLLED EVIDENCE</span><h3>{testRetested ? "3 attachments" : "Blocked execution evidence"}</h3>{["System log · 256 KB", "Waveform capture · 1.2 MB", "Bench configuration · JSON"].slice(0, testRetested ? 3 : 1).map((e) => <div className="file-row" key={e}><span>▤</span><b>{e}</b><small>SHA-256 recorded</small></div>)}<div className="provenance"><span>Procedure revision</span><b>{coverageResolved ? "TP-00004511.01" : "—"}</b><span>Environment</span><b>Iron Bird Bench 03</b><span>Decision authority</span><b>Qualified human reviewer</b></div></aside></div>
          </div>
        )}

        {view === "baseline" && (
          <div className="page"><div className="detail-header"><div><span className="eyebrow">CANDIDATE BASELINE</span><h2>FMS Software 3.3 · BL-00000217</h2><p>Predecessor · FMS Software 3.2 Released Baseline</p></div><Status tone={readiness === 100 ? "green" : "amber"}>{readiness === 100 ? "Ready for approval" : `${12 - ([impactResolved, coverageResolved, testRetested, approvalStep >= 4].filter(Boolean).length + 8)} blockers`}</Status></div>
            <div className="baseline-grid"><section className="panel baseline-content"><h3>Exact selected change package</h3><div className="baseline-row"><span><b>SCR-0001049.01</b><small>Round Robin · 5 SYSR · 8 HLR · 4 procedures</small></span><Status tone={approvalStep >= 4 ? "green" : "amber"}>{approvalStep >= 4 ? "Approved" : "Approval incomplete"}</Status></div><div className="baseline-row"><span><b>SCR-0001050.01</b><small>4 PR fixes · affected requirements and regression evidence</small></span><Status tone={testRetested ? "green" : "amber"}>{testRetested ? "Evidence complete" : "Verification incomplete"}</Status></div><div className="baseline-rule"><b>Baseline rule</b><span>Only exact requirement and relationship revisions authorized by selected approved SCR revisions become effective.</span></div></section>
              <section className="panel checks-panel"><h3>Readiness checks</h3>{[["SCR approvals complete", approvalStep >= 4],["No suspect required links", impactResolved],["No verification coverage gaps", coverageResolved],["No unresolved blocked evidence", testRetested],["Exact predecessor identified", true],["Document template selected", true]].map(([label, ok]) => <button key={String(label)} onClick={() => !ok && (String(label).includes("link") ? setView("traceability") : String(label).includes("SCR") ? openScr("round-robin") : setView("verification"))}><span className={ok ? "check-ok" : "check-bad"}>{ok ? "✓" : "!"}</span><b>{String(label)}</b><Status tone={ok ? "green" : "amber"}>{ok ? "Pass" : "Blocking"}</Status></button>)}</section></div>
          </div>
        )}

        {view === "traceability" && (
          <div className="page"><div className="detail-header"><div><span className="eyebrow">INTERACTIVE TRACEABILITY</span><h2>One released story, with the suspect path visible</h2><p>FMS Software 3.3 · Round Robin path</p></div><Status tone={impactResolved ? "green" : "amber"}>{impactResolved ? "Complete path" : "1 suspect link"}</Status></div>
            <div className="trace-explorer panel"><div className="trace-toolbar"><button>Release · Software 3.3</button><button>Relationship · Required</button><button>State · Candidate</button><span>{impactResolved ? "6 of 6 required links reviewed" : "5 of 6 required links reviewed"}</span></div><div className="node-chain">{[
              ["SCR", "SCR-0001049.01", "Round Robin"], ["SYSR", "SYSR-00002376.01", "Advance waypoint"], ["HLR", "HLR-00003144.01", "Trigger logic"], ["PROCEDURE", "TP-00004502.01", "Waypoint advance"], ["EXECUTION", testRetested ? "EXEC-00008821.01" : "Planned", testRetested ? "Completed" : "Awaiting"], ["RESULT", testRetested ? "PASS" : "—", testRetested ? "Human approved" : "No result"],
            ].map((n, i) => <div className="chain-wrap" key={n[1]}>{i > 0 && <div className={`chain-link ${i === 3 && !impactResolved ? "suspect" : ""}`}><span>{i === 1 ? "INTRODUCES" : i === 2 ? "ALLOCATES" : i === 3 ? "VERIFIED BY" : i === 4 ? "EXECUTED AS" : "RESULT OF"}</span>→</div>}<button className={`chain-node node-${n[0].toLowerCase()}`}><span>{n[0]}</span><b>{n[1]}</b><small>{n[2]}</small></button></div>)}</div>
              {!impactResolved && <div className="suspect-banner"><div><b>Suspect relationship</b><span>SYSR-00002376.01 and HLR-00003144.01 changed during SCR review. Confirm TP-00004502.01 still verifies the approved intent.</span></div><button className="primary-action small" onClick={() => { setImpactResolved(true); notify("Link reviewed; traceability completeness recalculated."); }}>Review and resolve</button></div>}
              {impactResolved && <div className="alert alert-green"><b>Complete required path</b><span>Every displayed relationship was reviewed against the current candidate revisions.</span></div>}
            </div>
          </div>
        )}
      </section>
      {toast && <div className="toast" role="status">{toast}</div>}
    </main>
  );
}
