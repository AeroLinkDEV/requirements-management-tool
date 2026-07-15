import { useCallback, useEffect, useRef, useState } from "react";
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
  onOpenPlanning: () => void;
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
  onOpenPlanning,
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
    [loading, setLoading] = useState(true),
    [workbenchOpen, setWorkbenchOpen] = useState(false),
    [confirmingRelease, setConfirmingRelease] = useState(false),
    [signing, setSigning] = useState(false);
  const workbenchRef = useRef<HTMLDetailsElement>(null),
    approvalRef = useRef<HTMLElement>(null);
  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [campaignsResponse, buildsResponse] = await Promise.all([
        fetch(`${api}/api/release-campaigns?projectId=${projectId}`),
        fetch(`${api}/api/builds?projectId=${projectId}`),
      ]);
      if (!campaignsResponse.ok)
        throw new Error("Release readiness could not be loaded.");
      const campaigns = await campaignsResponse.json();
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
        if (!response.ok)
          throw new Error("The selected release campaign is unavailable.");
        setDetail(await response.json());
      } else setDetail(undefined);
      const activeIndex = releases.findIndex((x) => x.id === activeReleaseId);
      if (activeIndex > 0) {
        const response = await fetch(
          `${api}/api/release-comparison?projectId=${projectId}&fromReleaseId=${releases[activeIndex - 1].id}&toReleaseId=${activeReleaseId}`,
        );
        if (response.ok) setComparison(await response.json());
      } else setComparison(undefined);
      setError("");
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "Release readiness could not be loaded.",
      );
    } finally {
      setLoading(false);
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
  const blockers = detail?.readiness.gates.filter((x) => !x.complete) ?? [];
  const firstBlocker = blockers[0];
  const gateGroups = [
    {
      key: "change",
      label: "Change control",
      codes: ["change_control", "impact_disposition"],
    },
    {
      key: "configuration",
      label: "Configuration",
      codes: ["baseline", "documents"],
    },
    {
      key: "assurance",
      label: "Assurance evidence",
      codes: ["traceability", "coverage", "verification", "evidence"],
    },
    {
      key: "authority",
      label: "Release authority",
      codes: ["release_approval"],
    },
  ]
    .map((group) => ({
      ...group,
      gates: blockers.filter((gate) => group.codes.includes(gate.code)),
    }))
    .filter((group) => group.gates.length);
  const pendingImpactGroups = Object.values(
    (detail?.impacts.filter((x) => x.state === "Pending") ?? []).reduce<
      Record<string, { scr: string; title: string; items: Impact[] }>
    >((groups, item) => {
      const group = groups[item.scrId] ?? {
        scr: item.scr,
        title: item.title,
        items: [],
      };
      group.items.push(item);
      groups[item.scrId] = group;
      return groups;
    }, {}),
  );
  const openWorkbench = () => {
    setWorkbenchOpen(true);
    setTimeout(
      () =>
        workbenchRef.current?.scrollIntoView({
          behavior: "smooth",
          block: "start",
        }),
      0,
    );
  };
  const openApproval = () =>
    setTimeout(
      () =>
        approvalRef.current?.scrollIntoView({
          behavior: "smooth",
          block: "center",
        }),
      0,
    );
  const actionForGate = (gate: Gate) => {
    if (["coverage", "verification", "evidence"].includes(gate.code))
      return { label: "Open Verification", run: onOpenVerification };
    if (["traceability", "documents"].includes(gate.code))
      return { label: "Open Traceability & Outputs", run: onOpenDocuments };
    if (gate.code === "release_approval")
      return { label: "Complete release approval", run: openApproval };
    return { label: "Open release workbench", run: openWorkbench };
  };
  const nextAction =
    detail?.state === "Released"
      ? {
          tone: "complete",
          eyebrow: "CONTROLLED RELEASE",
          title: `Version ${detail.release} is released`,
          detail:
            "The immutable release package and generated outputs remain available for inspection.",
          label: "View controlled outputs",
          run: onOpenDocuments,
        }
      : detail?.readiness.readyForRelease
        ? {
            tone: "complete",
            eyebrow: "RELEASE AUTHORITY",
            title: "Every release gate is complete",
            detail: `Review the exact ${detail.baseline} package before recording the final release transition.`,
            label: `Release version ${detail.release}`,
            run: () => setConfirmingRelease(true),
          }
        : firstBlocker
          ? {
              tone: "attention",
              eyebrow: "NEXT CONTROLLED ACTION",
              title: firstBlocker.name,
              detail: firstBlocker.detail,
              label: actionForGate(firstBlocker).label,
              run: actionForGate(firstBlocker).run,
            }
          : undefined;
  const renderGate = (gate: Gate) => (
    <article className={gate.complete ? "complete" : "blocked"} key={gate.code}>
      <div>
        <span>{gate.complete ? "✓" : "!"}</span>
        <b>{gate.name}</b>
      </div>
      <strong>
        {gate.completed}/{gate.total}
      </strong>
      <p>{gate.complete ? "Gate complete." : gate.detail}</p>
      <small>
        {gate.complete ? "Configuration evidence is current." : gate.action}
      </small>
    </article>
  );
  const renderImpact = (item: Impact) => (
    <article key={item.id}>
      <div>
        <button className="impactScr" onClick={() => onOpenScr(item.scrId)}>
          {item.scr}
        </button>
        <i className={item.state.toLowerCase()}>{item.state}</i>
      </div>
      <b>
        {item.kind} · {item.artifactReference}
      </b>
      <p>{item.description}</p>
      {item.rationale && <small>{item.rationale}</small>}
      {item.state === "Pending" && (
        <button onClick={() => setImpact(item)}>Disposition impact</button>
      )}
    </article>
  );
  const gateCompleted =
    detail?.readiness.gates.reduce((total, gate) => total + gate.completed, 0) ??
    0;
  const gateTotal =
    detail?.readiness.gates.reduce((total, gate) => total + gate.total, 0) ?? 0;
  const pendingImpactCount = pendingImpactGroups.reduce(
    (total, group) => total + group.items.length,
    0,
  );
  const approvedCount =
    detail?.approvals.filter((approval) => approval.state === "Approved")
      .length ?? 0;
  return (
    <main className="campaignPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            RELEASE COMMAND{" "}
            {detail?.release ? `/ VERSION ${detail.release}` : ""}
          </p>
          <h1>{detail?.name ?? "Release Campaign"}</h1>
          <p>
            One controlled path from unresolved evidence to formal release
            authority.
          </p>
        </div>
        {detail && (
          <div className={`campaignState ${detail.state.toLowerCase()}`}>
            {detail.state}
          </div>
        )}
      </header>
      {error && <div className="workspaceError">{error}</div>}
      {loading && (
        <div className="campaignLoading" role="status">
          <i />
          <b>Calculating release readiness</b>
          <span>
            Checking the selected version, baseline, evidence, and approval
            state…
          </span>
        </div>
      )}
      {!loading && !detail && !error && (
        <section className="campaignEmpty">
          <span aria-hidden="true">◆</span>
          <h2>No release campaign for this version</h2>
          <p>
            Create a campaign from an eligible candidate baseline before
            collecting release evidence and authority.
          </p>
          <button onClick={onOpenPlanning}>Open Product Versions</button>
        </section>
      )}
      {!loading && detail && (
        <>
          {nextAction && (
            <section className={`campaignNextAction ${nextAction.tone}`}>
              <div>
                <p className="eyebrow">{nextAction.eyebrow}</p>
                <h2>{nextAction.title}</h2>
                <p>{nextAction.detail}</p>
              </div>
              <button onClick={nextAction.run}>{nextAction.label} →</button>
            </section>
          )}
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
              <h2>Version {detail.release}</h2>
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
              <div className="progressionMark">
                <b>{blockers.length}</b>
                <span>open gate{blockers.length === 1 ? "" : "s"}</span>
              </div>
            )}
          </section>
          <section className="campaignGateRail" aria-label="Sequential release gates">
            {detail.readiness.gates.map((gate, index) => (
              <article
                className={
                  gate.complete
                    ? "complete"
                    : gate.completed > 0
                      ? "warning"
                      : "pending"
                }
                key={gate.code}
              >
                <span>{String(index + 1).padStart(2, "0")} · GATE</span>
                <b>{gate.name}</b>
                <small>
                  {gate.complete
                    ? "Complete"
                    : `${gate.completed}/${gate.total} · ${gate.action}`}
                </small>
              </article>
            ))}
          </section>
          <div className="releaseCommandGrid">
          <section className="readinessFocus">
            <div className="sectionHeading">
              <div>
                <p className="eyebrow">RELEASE DECISION</p>
                <h2>
                  {detail.readiness.readyForRelease
                    ? "All release gates are complete"
                    : "Release blockers by decision area"}
                </h2>
              </div>
              <b>{blockers.length} blocking</b>
            </div>
            {blockers.length ? (
              <section className="blockerGroups">
                {gateGroups.map((group, index) => (
                  <details
                    className="blockerGroup"
                    open={index === 0}
                    key={group.key}
                  >
                    <summary>
                      <div>
                        <b>{group.label}</b>
                        <span>{group.gates[0].action}</span>
                      </div>
                      <em>
                        {group.gates.length} blocker
                        {group.gates.length === 1 ? "" : "s"}
                      </em>
                    </summary>
                    <section className="gateGrid">
                      {group.gates.map(renderGate)}
                    </section>
                  </details>
                ))}
              </section>
            ) : (
              <div className="allGatesClear">
                <b>✓ Ready for release authority</b>
                <span>All configuration evidence is current.</span>
              </div>
            )}
            <details className="completedGates">
              <summary>
                {detail.readiness.gates.filter((x) => x.complete).length}{" "}
                completed gates <span>Review evidence</span>
              </summary>
              <section className="gateGrid">
                {detail.readiness.gates
                  .filter((x) => x.complete)
                  .map(renderGate)}
              </section>
            </details>
          </section>
          <aside className="evidencePulse">
            <div className="evidencePulseTitle">
              <div>
                <p className="eyebrow">EVIDENCE PULSE</p>
                <h2>Current release scope</h2>
              </div>
              <span>{detail.softwareBuildId ? "Fresh" : "Build needed"}</span>
            </div>
            <div className="evidencePulseScore">
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
              <p><b>Claims backed by current configuration</b><span>{gateCompleted.toLocaleString()} of {gateTotal.toLocaleString()} gate checks are complete.</span></p>
            </div>
            <div className="evidencePulseStats">
              <div><b>{detail.changes.filter((change) => change.included).length}</b><span>Included changes</span></div>
              <div><b>{pendingImpactCount}</b><span>Impact decisions</span></div>
              <div><b>{builds.filter((build) => build.releaseId === detail.releaseId).length}</b><span>Release builds</span></div>
              <div><b>{approvedCount}/{detail.approvals.length}</b><span>Approvals</span></div>
            </div>
            <dl className="releasePackagePulse">
              <div><dt>Baseline manifest</dt><dd className={detail.baselineId ? "ready" : "pending"}>{detail.baselineId ? "Ready" : "Needed"}</dd></div>
              <div><dt>Verification build</dt><dd className={detail.softwareBuildId ? "ready" : "pending"}>{detail.softwareBuildId ? "Selected" : "Needed"}</dd></div>
              <div><dt>Requirements hash</dt><dd className={detail.requirementsHash ? "ready" : "pending"}>{detail.requirementsHash ? "Verified" : "Needed"}</dd></div>
              <div><dt>Release authority</dt><dd className={detail.state === "Released" ? "ready" : "pending"}>{detail.state === "Released" ? "Released" : "In progress"}</dd></div>
            </dl>
          </aside>
          </div>
          <details
            className="campaignDisclosure"
            ref={workbenchRef}
            open={workbenchOpen}
            onToggle={(event) => setWorkbenchOpen(event.currentTarget.open)}
          >
            <summary>
              <div>
                <b>Release execution workbench</b>
                <span>Drive controlled blockers to authoritative evidence</span>
              </div>
              <em>{workbenchOpen ? "Close workbench" : "Open workbench"}</em>
            </summary>
            <ReleaseExecutionWorkbench
              api={api}
              detail={detail}
              builds={builds}
              onRefresh={load}
              onError={setError}
              onOpenScr={onOpenScr}
              onOpenVerification={onOpenVerification}
              onOpenDocuments={onOpenDocuments}
            />
          </details>
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
                  {pendingImpactGroups.reduce(
                    (total, group) => total + group.items.length,
                    0,
                  )}{" "}
                  pending
                </b>
              </div>
              {pendingImpactGroups.length ? (
                <div className="pendingImpactGroups">
                  {pendingImpactGroups.map((group) => (
                    <details className="pendingImpactGroup" key={group.scr}>
                      <summary>
                        <div>
                          <b>{group.scr}</b>
                          <span>{group.title}</span>
                        </div>
                        <em>{group.items.length} pending</em>
                      </summary>
                      <div>{group.items.map(renderImpact)}</div>
                    </details>
                  ))}
                </div>
              ) : (
                <div className="impactClear">
                  <b>✓ Every change impact is dispositioned</b>
                  <span>No pending lifecycle impact decisions remain.</span>
                </div>
              )}
              {!!detail.impacts.filter((x) => x.state !== "Pending").length && (
                <details className="closedImpacts">
                  <summary>
                    {detail.impacts.filter((x) => x.state !== "Pending").length}{" "}
                    completed dispositions <span>Show history</span>
                  </summary>
                  <div>
                    {detail.impacts
                      .filter((x) => x.state !== "Pending")
                      .map(renderImpact)}
                  </div>
                </details>
              )}
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
                  <option value="">
                    Select version {detail.release} build…
                  </option>
                  {builds
                    .filter((x) => x.releaseId === detail.releaseId)
                    .map((x) => (
                      <option value={x.id} key={x.id}>
                        {x.buildNumber}
                      </option>
                    ))}
                </select>
              </section>
              <section className="campaignCard" ref={approvalRef}>
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
              <details className="campaignCard campaignHistory">
                <summary>
                  <div>
                    <b>Campaign history</b>
                    <span>Append-only release events</span>
                  </div>
                  <em>{detail.events.length}</em>
                </summary>
                <div>
                  {detail.events.map((x, i) => (
                    <div className="campaignEvent" key={i}>
                      <i />
                      <div>
                        <b>{x.eventType.replace(/([A-Z])/g, " $1").trim()}</b>
                        <p>{x.detail}</p>
                        <small>
                          {x.actorId} ·{" "}
                          {new Date(x.occurredAt).toLocaleString()}
                        </small>
                      </div>
                    </div>
                  ))}
                </div>
              </details>
            </aside>
          </div>
          {comparison && (
            <details className="campaignDisclosure comparisonDisclosure">
              <summary>
                <div>
                  <b>
                    Compare {comparison.fromRelease} → {comparison.toRelease}
                  </b>
                  <span>Requirement and proposed-change differences</span>
                </div>
                <em>Open comparison</em>
              </summary>
              <section className="campaignCard comparison">
                <div className="campaignTitle">
                  <div>
                    <h2>
                      Version {comparison.fromRelease} → {comparison.toRelease}
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
              </section>
            </details>
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
      {confirmingRelease && detail && (
        <div
          className="campaignModal releaseConfirm"
          role="dialog"
          aria-modal="true"
          aria-labelledby="release-confirm-title"
        >
          <div>
            <p className="eyebrow">FINAL CONTROLLED TRANSITION</p>
            <h2 id="release-confirm-title">
              Release version {detail.release}?
            </h2>
            <p>
              This will release <b>{detail.baseline}</b> as the immutable
              product configuration after all recorded gates and approvals.
            </p>
            <dl>
              <div>
                <dt>Baseline</dt>
                <dd>{detail.baseline}</dd>
              </div>
              <div>
                <dt>Release gates</dt>
                <dd>
                  {detail.readiness.gates.length}/
                  {detail.readiness.gates.length} complete
                </dd>
              </div>
              <div>
                <dt>Release approvals</dt>
                <dd>
                  {
                    detail.approvals.filter((x) => x.state === "Approved")
                      .length
                  }
                  /{detail.approvals.length} approved
                </dd>
              </div>
            </dl>
            <div>
              <button
                className="outline"
                onClick={() => setConfirmingRelease(false)}
              >
                Cancel
              </button>
              <button
                onClick={async () => {
                  if (await call("release", {})) setConfirmingRelease(false);
                }}
              >
                Confirm controlled release
              </button>
            </div>
          </div>
        </div>
      )}
      {signing && detail && (
        <SignatureDialog
          title={`Authorize version ${detail.release}`}
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
