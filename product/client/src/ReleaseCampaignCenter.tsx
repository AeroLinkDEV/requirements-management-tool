import { useCallback, useEffect, useState } from "react";
import { SignatureDialog } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import ReleaseExecutionWorkbench from "./ReleaseExecutionWorkbench";
import "./ReleaseCampaignCenter.css";
type Gate = {
  code: string;
  name: string;
  complete: boolean;
  completed: number;
  total: number;
  detail: string;
  action: string;
};
type Impact = {
  id: string;
  scrId: string;
  scr: string;
  title: string;
  kind: string;
  artifactReference: string;
  description: string;
  state: string;
  rationale: string;
};
type Approval = {
  position: number;
  approverId: string;
  approverName: string;
  state: string;
  approvedAt?: string;
};
type Detail = {
  id: string;
  name: string;
  state: string;
  projectId: string;
  releaseId: string;
  release: string;
  baselineId: string;
  baseline: string;
  baselineState: string;
  requirementsHash?: string;
  softwareBuildId?: string;
  releaseHash?: string;
  readiness: { percent: number; readyForRelease: boolean; gates: Gate[] };
  changes: {
    id: string;
    displayNumber: string;
    title: string;
    type: string;
    state: string;
    authorId: string;
    requirementCount: number;
    included: boolean;
  }[];
  impacts: Impact[];
  approvals: Approval[];
  events: {
    eventType: string;
    actorId: string;
    detail: string;
    occurredAt: string;
  }[];
};
type Build = {
  id: string;
  buildNumber: string;
  releaseId: string;
  baselineId: string;
  state: string;
};
type Comparison = {
  fromRelease: string;
  toRelease: string;
  toMaterialized: boolean;
  summary: {
    added: number;
    modified: number;
    retired: number;
    unchanged: number;
    proposed: number;
  };
  proposed: {
    scr: string;
    title: string;
    state: string;
    type: string;
    displayNumber: string;
    level: string;
    kind: string;
    statement: string;
  }[];
};
type Props = {
  api: string;
  projectId: string;
  activeReleaseId: string;
  releases: { id: string; version: string }[];
  user: AuthUser;
  onBack: () => void;
  onOpenScr: (id: string) => void;
  onOpenVerification: () => void;
  onOpenDocuments: () => void;
};
export default function ReleaseCampaignCenter({
  api,
  projectId,
  activeReleaseId,
  releases,
  user,
  onBack,
  onOpenScr,
  onOpenVerification,
  onOpenDocuments,
}: Props) {
  const [campaignId, setCampaignId] = useState(""),
    [detail, setDetail] = useState<Detail>(),
    [builds, setBuilds] = useState<Build[]>([]),
    [comparison, setComparison] = useState<Comparison>(),
    [impact, setImpact] = useState<Impact>(),
    [rationale, setRationale] = useState(""),
    [error, setError] = useState(""),
    [signing, setSigning] = useState(false);
  const load = useCallback(async () => {
    const [campaignsResponse, buildsResponse] = await Promise.all([
      fetch(`${api}/api/release-campaigns?projectId=${projectId}`),
      fetch(`${api}/api/builds?projectId=${projectId}`),
    ]);
    const campaigns = campaignsResponse.ok
      ? await campaignsResponse.json()
      : [];
    const matching = campaigns.find(
      (x: { releaseId: string }) => x.releaseId === activeReleaseId,
    );
    const id =
      (campaignId &&
      campaigns.some(
        (x: { id: string }) => x.id === campaignId && x.id === matching?.id,
      )
        ? campaignId
        : matching?.id) || "";
    if (id !== campaignId) setCampaignId(id);
    if (buildsResponse.ok) setBuilds(await buildsResponse.json());
    if (id) {
      const response = await fetch(`${api}/api/release-campaigns/${id}`);
      if (response.ok) setDetail(await response.json());
    } else setDetail(undefined);
    const activeIndex = releases.findIndex((x) => x.id === activeReleaseId);
    if (activeIndex > 0) {
      const response = await fetch(
        `${api}/api/release-comparison?projectId=${projectId}&fromReleaseId=${releases[activeIndex - 1].id}&toReleaseId=${activeReleaseId}`,
      );
      if (response.ok) setComparison(await response.json());
    }
  }, [api, projectId, releases, campaignId, activeReleaseId]);
  useEffect(() => {
    load();
  }, [load]);
  const call = async (path: string, body: unknown) => {
    setError("");
    const response = await fetch(
      `${api}/api/release-campaigns/${campaignId}/${path}`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      },
    );
    if (!response.ok) {
      const result = await response.json();
      setError(
        (result.error || "Operation failed.") +
          (result.blockers ? `: ${result.blockers.join(", ")}` : ""),
      );
      return false;
    }
    await load();
    return true;
  };
  const disposition = async (state: "Addressed" | "NotApplicable") => {
    if (!impact || !rationale.trim()) return;
    const response = await fetch(
      `${api}/api/impact-dispositions/${impact.id}`,
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ state, rationale }),
      },
    );
    if (!response.ok) {
      setError((await response.json()).error);
      return;
    }
    setImpact(undefined);
    setRationale("");
    await load();
  };
  const activeApproval = detail?.approvals.find((x) => x.state === "Active");
  const renderGate = (gate: Gate) => (
    <article className={gate.complete ? "complete" : "blocked"} key={gate.code}>
      <div><span>{gate.complete ? "✓" : "!"}</span><b>{gate.name}</b></div>
      <strong>{gate.completed}/{gate.total}</strong>
      <p>{gate.complete ? "Gate complete." : gate.detail}</p>
      <small>{gate.complete ? "Configuration evidence is current." : gate.action}</small>
    </article>
  );
  const renderImpact = (item: Impact) => (
    <article key={item.id}>
      <div><span role="button" tabIndex={0} onClick={() => onOpenScr(item.scrId)}>{item.scr}</span><i className={item.state.toLowerCase()}>{item.state}</i></div>
      <b>{item.kind} · {item.artifactReference}</b>
      <p>{item.description}</p>
      {item.rationale && <small>{item.rationale}</small>}
      {item.state === "Pending" && <button onClick={() => setImpact(item)}>Disposition impact</button>}
    </article>
  );
  return (
    <main className="campaignPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            RELEASE CONTROL / FMS {detail?.release ?? ""}
          </p>
          <h1>{detail?.name ?? "Release Campaign"}</h1>
          <p>
            Digital-thread readiness, evidence, controlled outputs, and formal
            release authority.
          </p>
        </div>
        {detail && (
          <div className={`campaignState ${detail.state.toLowerCase()}`}>
            {detail.state}
          </div>
        )}
      </header>
      {error && <div className="workspaceError">{error}</div>}
      {detail && (
        <>
          <section className="readinessHero">
            <div
              className="readinessDial"
              style={
                {
                  "--ready": `${detail.readiness.percent * 3.6}deg`,
                } as React.CSSProperties
              }
            >
              <b>{detail.readiness.percent}%</b>
              <span>ready</span>
            </div>
            <div>
              <p className="eyebrow">TARGET RELEASE</p>
              <h2>FMS {detail.release}</h2>
              <p>
                {detail.baseline} · {detail.baselineState} ·{" "}
                {detail.softwareBuildId
                  ? "verification build selected"
                  : "no verification build selected"}
              </p>
            </div>
            <div className="readinessSummary">
              <b>
                {detail.readiness.gates.filter((x) => x.complete).length}/
                {detail.readiness.gates.length}
              </b>
              <span>release gates complete</span>
            </div>
            {detail.state === "Released" ? (
              <div className="releasedSeal">
                ✓ RELEASED
                <br />
                <small>{detail.releaseHash?.slice(0, 16)}…</small>
              </div>
            ) : (
              <button
                disabled={!detail.readiness.readyForRelease}
                onClick={() => call("release", {})}
              >
                Release FMS {detail.release}
              </button>
            )}
          </section>
          <section className="readinessFocus">
            <div className="sectionHeading"><div><p className="eyebrow">RELEASE DECISION</p><h2>{detail.readiness.readyForRelease?"All release gates are complete":"Resolve the remaining release blockers"}</h2></div><b>{detail.readiness.gates.filter(x=>!x.complete).length} blocking</b></div>
            {detail.readiness.gates.some(x=>!x.complete)
              ? <section className="gateGrid">{detail.readiness.gates.filter(x=>!x.complete).map(renderGate)}</section>
              : <div className="allGatesClear"><b>✓ Ready for release authority</b><span>All configuration evidence is current.</span></div>}
            <details className="completedGates"><summary>{detail.readiness.gates.filter(x=>x.complete).length} completed gates <span>Review evidence</span></summary><section className="gateGrid">{detail.readiness.gates.filter(x=>x.complete).map(renderGate)}</section></details>
          </section>
          <details className="campaignDisclosure"><summary><div><b>Release execution workbench</b><span>Drive controlled blockers to authoritative evidence</span></div><em>Open workbench</em></summary><ReleaseExecutionWorkbench api={api} detail={detail} builds={builds} onRefresh={load} onError={setError} onOpenScr={onOpenScr} onOpenVerification={onOpenVerification} onOpenDocuments={onOpenDocuments}/></details>
          <div className="campaignGrid">
            <section className="campaignCard impacts">
              <div className="campaignTitle">
                <div>
                  <h2>Change-impact closure</h2>
                  <p>
                    Requirement, traceability, verification, and document
                    dispositions
                  </p>
                </div>
                <b>
                  {detail.impacts.filter((x) => x.state === "Pending").length}{" "}
                  pending
                </b>
              </div>
              {detail.impacts.filter(x=>x.state==="Pending").map(renderImpact)}
              {!!detail.impacts.filter(x=>x.state!=="Pending").length&&<details className="closedImpacts"><summary>{detail.impacts.filter(x=>x.state!=="Pending").length} completed dispositions <span>Show history</span></summary><div>{detail.impacts.filter(x=>x.state!=="Pending").map(renderImpact)}</div></details>}
            </section>
            <aside>
              <section className="campaignCard">
                <div className="campaignTitle">
                  <div>
                    <h2>Verification build</h2>
                    <p>Exact build used for release evidence</p>
                  </div>
                </div>
                <select
                  value={detail.softwareBuildId ?? ""}
                  onChange={(e) =>
                    call("verification-build", {
                      softwareBuildId: e.target.value,
                    })
                  }
                >
                  <option value="">Select FMS {detail.release} build…</option>
                  {builds
                    .filter((x) => x.releaseId === detail.releaseId)
                    .map((x) => (
                      <option value={x.id} key={x.id}>
                        {x.buildNumber}
                      </option>
                    ))}
                </select>
              </section>
              <section className="campaignCard">
                <div className="campaignTitle">
                  <div>
                    <h2>Ordered release approval</h2>
                    <p>Unanimous electronic authorization required</p>
                  </div>
                </div>
                {detail.approvals.map((x) => (
                  <div className="releaseApprover" key={x.position}>
                    <span>{x.state === "Approved" ? "✓" : x.position + 1}</span>
                    <div>
                      <b>{x.approverName}</b>
                      <small>
                        {x.approverId} · {x.state}
                      </small>
                    </div>
                  </div>
                ))}
                {!detail.approvals.length && (
                  <button
                    disabled={detail.readiness.gates.some(
                      (x) => x.code !== "release_approval" && !x.complete,
                    )}
                    onClick={() =>
                      call("review", {
                        approvers: [
                          {
                            userId: "systems.lead",
                            name: "Systems Engineering Lead",
                          },
                          {
                            userId: "software.lead",
                            name: "Software Engineering Lead",
                          },
                          {
                            userId: "program.manager",
                            name: "Program Manager",
                          },
                        ],
                      })
                    }
                  >
                    Start release review
                  </button>
                )}
                {activeApproval &&
                  (activeApproval.approverId === user.userName ? (
                    <button onClick={() => setSigning(true)}>
                      Review & electronically approve
                    </button>
                  ) : (
                    <div className="snapshotNote">
                      <b>Awaiting {activeApproval.approverName}</b>
                      <p>Only the assigned identity can sign this stage.</p>
                    </div>
                  ))}
              </section>
              <details className="campaignCard campaignHistory"><summary><div><b>Campaign history</b><span>Append-only release events</span></div><em>{detail.events.length}</em></summary><div>{detail.events.map((x, i) => <div className="campaignEvent" key={i}><i/><div><b>{x.eventType.replace(/([A-Z])/g, " $1").trim()}</b><p>{x.detail}</p><small>{x.actorId} · {new Date(x.occurredAt).toLocaleString()}</small></div></div>)}</div></details>
            </aside>
          </div>
          {comparison && (
            <details className="campaignDisclosure comparisonDisclosure"><summary><div><b>Compare FMS {comparison.fromRelease} → {comparison.toRelease}</b><span>Requirement and proposed-change differences</span></div><em>Open comparison</em></summary><section className="campaignCard comparison">
              <div className="campaignTitle">
                <div>
                  <h2>
                    FMS {comparison.fromRelease} → {comparison.toRelease}
                  </h2>
                  <p>
                    {comparison.toMaterialized
                      ? "Effective baseline comparison"
                      : "Proposed change comparison while release remains in work"}
                  </p>
                </div>
              </div>
              <div className="comparisonStats">
                {Object.entries(comparison.summary).map(([k, v]) => (
                  <article key={k}>
                    <b>{v}</b>
                    <span>{k}</span>
                  </article>
                ))}
              </div>
              {comparison.proposed.map((x) => (
                <article
                  className="proposedChange"
                  key={`${x.scr}-${x.displayNumber}`}
                >
                  <span>{x.scr}</span>
                  <b>
                    {x.displayNumber} · {x.kind}
                  </b>
                  <p>{x.statement}</p>
                  <small>
                    {x.type} · {x.level} · {x.state}
                  </small>
                </article>
              ))}
            </section></details>
          )}
        </>
      )}
      {impact && (
        <div className="campaignModal">
          <div>
            <p className="eyebrow">
              {impact.scr} / {impact.kind}
            </p>
            <h2>Disposition {impact.artifactReference}</h2>
            <p>{impact.description}</p>
            <textarea
              value={rationale}
              onChange={(e) => setRationale(e.target.value)}
              placeholder="Required rationale and evidence of disposition"
            />
            <div>
              <button className="outline" onClick={() => setImpact(undefined)}>
                Cancel
              </button>
              <button
                disabled={!rationale.trim()}
                onClick={() => disposition("NotApplicable")}
              >
                Not applicable
              </button>
              <button
                disabled={!rationale.trim()}
                onClick={() => disposition("Addressed")}
              >
                Mark addressed
              </button>
            </div>
          </div>
        </div>
      )}
      {signing && detail && (
        <SignatureDialog
          title={`Authorize FMS ${detail.release}`}
          meaning="I approve this exact release campaign package and authorize progression toward the controlled product release."
          onCancel={() => setSigning(false)}
          onSign={async (password, meaning) => {
            if (await call("approve", { password, meaning })) setSigning(false);
          }}
        />
      )}
    </main>
  );
}
