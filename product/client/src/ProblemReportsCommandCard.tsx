import { useEffect, useState } from "react";

type Scope = "build" | "all" | "unassigned";

type Dashboard = {
  summary: { active: number; closureAwaitingApproval: number; closed: number; releaseBlockers: number };
  byState: { state: string; count: number }[];
  activeBySeverity?: { severity: string; count: number }[];
  attention: { id: string; displayNumber: string; title: string; state: string; severity: string; responsibleEngineerDisplayName?: string; responsibleEngineerId?: string }[];
};

const lifecycle: [string, string][] = [
  ["Draft", "Draft"],
  ["ReadyForSccb", "Ready for SCCB"],
  ["Open", "Open"],
  ["Implementing", "Implementing"],
  ["Verifying", "Verifying"],
  ["WaitingForSqaToClose", "Awaiting SQA"],
];
const stateLabel = (state: string) => lifecycle.find(([id]) => id === state)?.[1] ?? state;
const severities = ["Critical", "High", "Major", "Minor", "Trivial"];

/**
 * Problem Reports on Command Center (#1113 S2). Reports belong to the project, so the card states its scope
 * and defaults to the reports targeting the active build; every number reads the same dashboard endpoint
 * the Problem Report center uses, so the two cannot disagree.
 */
export default function ProblemReportsCommandCard({ api, projectId, releaseId, releaseVersion, onOpenList, onOpenReport }: {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  onOpenList: (targetBuild?: string) => void;
  onOpenReport: (id: string) => void;
}) {
  const [scope, setScope] = useState<Scope>("build");
  const [data, setData] = useState<Dashboard>();
  const [error, setError] = useState("");
  useEffect(() => {
    let live = true;
    setError("");
    const query = new URLSearchParams({ projectId });
    if (scope === "build" && releaseId) query.set("targetReleaseId", releaseId);
    if (scope === "unassigned") query.set("targetUnassigned", "true");
    fetch(`${api}/api/problem-reports/dashboard?${query}`)
      .then(async (response) => {
        if (!response.ok) throw new Error();
        const body = await response.json();
        // A malformed summary is unavailable, never a crash: this card must not take Command Center down.
        if (!body?.summary || !Array.isArray(body.byState) || !Array.isArray(body.attention)) throw new Error();
        return body as Dashboard;
      })
      .then((body) => { if (live) setData(body); })
      .catch(() => { if (live) setError("Problem Report summary is unavailable. Refresh to try again."); });
    return () => { live = false; };
  }, [api, projectId, releaseId, scope]);
  const targetBuild = scope === "build" ? releaseId : scope === "unassigned" ? "unassigned" : undefined;
  const count = (state: string) => data?.byState.find((item) => item.state === state)?.count ?? 0;
  const activeMax = Math.max(1, ...lifecycle.map(([state]) => count(state)));
  const severity = (name: string) => data?.activeBySeverity?.find((item) => item.severity === name)?.count ?? 0;
  return (
    <section className="dashboardAreaCard problemReports" aria-label="Problem Reports summary">
      <header>
        <div><span>PROBLEM REPORTS</span><h2>Problem Reports</h2></div>
        <div className="prCardScope" role="group" aria-label="Problem Report scope">
          <button type="button" aria-pressed={scope === "build"} onClick={() => setScope("build")}>Build {releaseVersion}</button>
          <button type="button" aria-pressed={scope === "all"} onClick={() => setScope("all")}>All builds</button>
          <button type="button" aria-pressed={scope === "unassigned"} onClick={() => setScope("unassigned")}>Unassigned</button>
        </div>
        <i>PR</i>
      </header>
      {error ? <p role="status" className="prCardError">{error}</p> : !data ? <p className="prCardLoading">Loading Problem Reports…</p> : <>
        <div className="prCardHeadline">
          <button type="button" onClick={() => onOpenList(targetBuild)}><strong>{data.summary.active}</strong><span>Active</span></button>
          <button type="button" onClick={() => onOpenList(targetBuild)}><strong>{data.summary.closureAwaitingApproval}</strong><span>Awaiting SQA</span></button>
          <button type="button" onClick={() => onOpenList(targetBuild)} className={data.summary.releaseBlockers > 0 ? "blocking" : ""}><strong>{data.summary.releaseBlockers}</strong><span>Release blockers</span></button>
          <button type="button" onClick={() => onOpenList(targetBuild)}><strong>{data.summary.closed}</strong><span>Closed</span></button>
        </div>
        <div className="prCardLifecycle" aria-label="Active reports by lifecycle state">
          {lifecycle.map(([state, label]) => (
            <div key={state}>
              <span>{label}</span>
              <i style={{ inlineSize: `${(count(state) / activeMax) * 100}%` }} aria-hidden="true" />
              <b>{count(state)}</b>
            </div>
          ))}
        </div>
        <p className="prCardSeverity" aria-label="Active reports by severity">
          {severities.map((name) => <span key={name} className={name.toLowerCase()}><i aria-hidden="true" />{name} <b>{severity(name)}</b></span>)}
        </p>
        <div className="prCardAttention">
          <h3>Needs attention</h3>
          {data.attention.length === 0
            ? <p>No active Problem Reports in this scope.</p>
            : data.attention.slice(0, 3).map((item) => (
              <button type="button" key={item.id} onClick={() => onOpenReport(item.id)}>
                <b>{item.displayNumber}</b>
                <em className={item.severity.toLowerCase()}>{item.severity}</em>
                <span>{item.title}</span>
                <small>{stateLabel(item.state)}{item.responsibleEngineerDisplayName ? ` · ${item.responsibleEngineerDisplayName}` : ""}</small>
              </button>
            ))}
        </div>
        <button type="button" className="prCardOpen" onClick={() => onOpenList(targetBuild)}>Open Problem Reports →</button>
      </>}
    </section>
  );
}
