import { useCallback, useEffect, useRef, useState } from "react";
import { PersonName } from "./People";
import { AutosaveState, DraftRestore } from "./DraftNotice";
import { useFormDraft } from "./autosave";
import { stateLabel } from './presentation'
import LegacyProcedureBootstrapPanel from './LegacyProcedureBootstrapPanel'
import { ReopenBaselinePanel } from './ReopenBaselinePanel'
import type { FormEvent } from "react";
import "./BaselineCenter.css";
import "./Swrd.css";

type BaselineSummary = {
  id: string;
  displayNumber: string;
  name: string;
  state: string;
  predecessorBaselineId?: string;
  contentHash?: string;
  requirementsHash?: string;
  requirementsMaterializedAt?: string;
  testProceduresHash?: string;
  testProceduresMaterializedAt?: string;
  selectionCount: number;
  createdAt: string;
  frozenAt?: string;
};
type Predecessor = {
  id: string;
  displayNumber: string;
  name: string;
  release: string;
  isReleased: boolean;
  requirementCount: number;
};
type Requirement = {
  id: string;
  displayNumber: string;
  level: string;
  kind: string;
  statement: string;
  verificationMethod: string;
};
type Selection = {
  id: string;
  displayNumber: string;
  title: string;
  requirementChanges: Requirement[];
};
type Event = {
  eventType: string;
  actorId: string;
  detail: string;
  occurredAt: string;
};
type Detail = BaselineSummary & {
  projectId: string;
  releaseId: string;
  selections: Selection[];
  events: Event[];
};
type Swrd = {
  title: string;
  release: string;
  baseline: string;
  requirementsHash: string;
  requirementCount: number;
  requirements: {
    id: string;
    displayNumber: string;
    level: string;
    statement: string;
    rationale: string;
    verificationMethod: string;
    sourceScr: string;
  }[];
};
type Eligible = {
  id: string;
  displayNumber: string;
  title: string;
  requirementCount: number;
  updatedAt: string;
};
type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  readOnly: boolean;
  productName: string;
  onBack: () => void;
};

export default function BaselineCenter({
  api,
  projectId,
  releaseId,
  releaseVersion,
  readOnly,
  productName,
  onBack,
}: Props) {
  const [items, setItems] = useState<BaselineSummary[]>([]),
    [predecessors, setPredecessors] = useState<Predecessor[]>([]),
    [selectedId, setSelectedId] = useState(""),
    [detail, setDetail] = useState<Detail>(),
    [eligible, setEligible] = useState<Eligible[]>([]),
    [swrd, setSwrd] = useState<Swrd>(),
    [creating, setCreating] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  const loadList = useCallback(async () => {
    const [response, priorResponse] = await Promise.all([
      fetch(
        `${api}/api/baselines?projectId=${projectId}&releaseId=${releaseId}`,
      ),
      fetch(
        `${api}/api/baselines/predecessors?projectId=${projectId}&releaseId=${releaseId}`,
      ),
    ]);
    if (response.ok) {
      const list = await response.json();
      setItems(list);
      if (!selectedId && list.length) setSelectedId(list[0].id);
    }
    if (priorResponse.ok) setPredecessors(await priorResponse.json());
  }, [api, projectId, releaseId, selectedId]);
  const loadDetail = useCallback(async () => {
    if (!selectedId) {
      setDetail(undefined);
      return;
    }
    const [a, b] = await Promise.all([
      fetch(`${api}/api/baselines/${selectedId}`),
      fetch(`${api}/api/baselines/${selectedId}/eligible-scrs`),
    ]);
    if (a.ok) {
      const next = await a.json();
      setDetail(next);
      if (next.requirementsMaterializedAt) {
        const response = await fetch(`${api}/api/baselines/${selectedId}/swrd`);
        if (response.ok) setSwrd(await response.json());
      } else setSwrd(undefined);
    }
    if (b.ok) setEligible(await b.json());
  }, [api, selectedId]);
  useEffect(() => {
    loadList();
  }, [loadList]);
  useEffect(() => {
    loadDetail();
  }, [loadDetail]);
  const createForm = useRef<HTMLFormElement>(null);
  // A baseline is named and numbered by hand; losing that to a stray navigation is the kind of small
  // annoyance that makes people distrust a tool.
  const createDraft = useFormDraft(createForm, `aerolink:new-baseline:${projectId}:${releaseId}`);

  const create = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/baselines`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        baseNumber: form.get("baseNumber"),
        revision: Number(form.get("revision")),
        projectId,
        releaseId,
        name: form.get("name"),
        predecessorBaselineId: form.get("predecessorBaselineId") || null,
      }),
    });
    if (!response.ok) {
      setError(
        (await response.json()).error ||
          "Candidate baseline could not be created.",
      );
      setBusy(false);
      return;
    }
    const created = await response.json();
    // The baseline exists now, so the browser copy has nothing left to protect.
    createDraft.clear();
    setSelectedId(created.id);
    setCreating(false);
    await loadList();
    setBusy(false);
  };
  const action = async (path: string, method = "POST", body?: unknown) => {
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/baselines/${selectedId}/${path}`, {
      method,
      headers: { "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      setError((await response.json()).error || "Baseline operation failed.");
      setBusy(false);
      return;
    }
    await loadList();
    await loadDetail();
    setBusy(false);
  };
  const allRequirements =
    detail?.selections.flatMap((x) =>
      x.requirementChanges.map((r) => ({ ...r, scr: x.displayNumber })),
    ) ?? [];
  return (
    <main className="baselinePage">
      <header className="baselineHeader">
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            CONFIGURATION CONTROL / RELEASE {releaseVersion}
          </p>
          <h1>Candidate Baselines</h1>
          <p>
            Assemble exact Approved change request revisions into an immutable release
            manifest.
          </p>
        </div>
        {readOnly?<span className="statusPill">Released build · read-only</span>:<button className="newBaseline" onClick={() => setCreating(true)}>
          + New Candidate
        </button>}
      </header>
      {error && <div className="workspaceError">{error}</div>}
      {creating ? (
        <form className="createBaseline" onSubmit={create} ref={createForm}>
          <div>
            <p className="eyebrow">NEW RELEASE MANIFEST</p>
            <h2>Create candidate baseline</h2>
            {createDraft.offered && (
              <DraftRestore
                savedAt={createDraft.offered.savedAt}
                description="An unfinished baseline was left in this browser. Nothing was created."
                onRestore={createDraft.apply}
                onDiscard={createDraft.discard}
              />
            )}
            <AutosaveState status={createDraft.status} savedAt={createDraft.savedAt} where="this browser" />
            <p>
              {productName} · release {releaseVersion}
            </p>
          </div>
          <div className="baselineFields">
            <label>
              Baseline number
              <input
                name="baseNumber"
                defaultValue="SW-01.60"
                pattern="SW-[0-9]{2}\.[0-9]{2}"
                required
              />
            </label>
            <label>
              Revision
              <input
                name="revision"
                type="number"
                min="0"
                defaultValue="0"
                required
              />
            </label>
            <label>
              Authority
              <input value="Authenticated configuration manager" readOnly />
            </label>
            <label className="full">
              Exact predecessor product baseline
              <select
                name="predecessorBaselineId"
                defaultValue={predecessors[0]?.id ?? ""}
                required={predecessors.length > 0}
              >
                <option value="">
                  {predecessors.length
                    ? "Select inherited baseline…"
                    : "No prior materialized baseline (initial product baseline)"}
                </option>
                {predecessors.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.displayNumber} · release {item.release} ·{" "}
                    {item.requirementCount.toLocaleString()} requirements
                    {item.isReleased ? " · RELEASED" : ""}
                  </option>
                ))}
              </select>
              <small>
                The candidate inherits every effective requirement revision in
                this exact baseline before selected Approved changes are
                applied.
              </small>
            </label>
            <label className="full">
              Baseline name
              <input
                name="name"
                defaultValue={`${productName} ${releaseVersion} Candidate`}
                required
              />
            </label>
          </div>
          <div className="baselineActions">
            <button
              type="button"
              className="outline"
              onClick={() => setCreating(false)}
            >
              Cancel
            </button>
            <button disabled={busy}>
              {busy ? "Creating…" : "Create Candidate Baseline"}
            </button>
          </div>
        </form>
      ) : (
        <div className="baselineLayout">
          <aside className="baselineList">
            <div className="listTitle">
              <b>Release {releaseVersion}</b>
              <span>
                {items.length} baseline{items.length === 1 ? "" : "s"}
              </span>
            </div>
            {items.map((item) => (
              <button
                className={item.id === selectedId ? "selected" : ""}
                onClick={() => setSelectedId(item.id)}
                key={item.id}
              >
                <div>
                  <b>{item.displayNumber}</b>
                  <span className={`baselineState ${item.state.toLowerCase()}`}>
                    {stateLabel(item.state)}
                  </span>
                </div>
                <p>{item.name}</p>
                <small>
                  {item.selectionCount} exact change request revision
                  {item.selectionCount === 1 ? "" : "s"}
                </small>
              </button>
            ))}
            {!items.length && (
              <div className="baselineEmpty">
                No candidate baseline exists for this release.
              </div>
            )}
          </aside>
          <section className="baselineContent">
            {detail ? (
              <>
                <div className="manifestHero">
                  <div>
                    <span
                      className={`baselineState ${detail.state.toLowerCase()}`}
                    >
                      {stateLabel(detail.state)}
                    </span>
                    <h2>{detail.displayNumber}</h2>
                    <p>{detail.name}</p>
                    {detail.predecessorBaselineId && (
                      <small className="inherits">
                        Inherits exact product baseline{" "}
                        {predecessors.find(
                          (x) => x.id === detail.predecessorBaselineId,
                        )?.displayNumber ?? detail.predecessorBaselineId}
                        {predecessors.find(
                          (x) => x.id === detail.predecessorBaselineId,
                        )?.release
                          ? ` · release ${predecessors.find((x) => x.id === detail.predecessorBaselineId)?.release}`
                          : ""}
                      </small>
                    )}
                  </div>
                  <div className="manifestStats">
                    <div>
                      <b>{detail.selections.length}</b>
                      <span>change request revisions</span>
                    </div>
                    <div>
                      <b>{allRequirements.length}</b>
                      <span>Requirement changes</span>
                    </div>
                  </div>
                  {!readOnly && detail.state === "Draft" ? (
                    <button
                      disabled={busy || !detail.selections.length}
                      onClick={() => action("freeze", "POST", {})}
                    >
                      Freeze Baseline
                    </button>
                  ) : detail.requirementsMaterializedAt ? (
                    <div className="frozenMark">✓ SWRD materialized</div>
                  ) : !readOnly ? (
                    <button
                      disabled={busy}
                      onClick={() =>
                        action("materialize-requirements", "POST", {})
                      }
                    >
                      {busy ? "Materializing…" : "Materialize SWRD"}
                    </button>
                  ) : <div className="frozenMark">Released build · read-only</div>}
                  {/* The way back through the freeze, for a build that has not shipped. Offered beside
                      materializing rather than hidden behind a menu: a build found to be wrong the morning
                      after it was frozen is an ordinary situation, and the alternative was releasing it. */}
                  {!readOnly && detail.state === "Frozen" && (
                    <ReopenBaselinePanel
                      api={api}
                      baselineId={detail.id}
                      displayNumber={detail.displayNumber}
                      onReopened={async () => { await loadList(); await loadDetail(); }}
                    />
                  )}
                  {/* The procedure manifest closes separately and later. Procedures are written against the
                      requirements this baseline fixes, so they are finished after it is frozen — which is why
                      this is a second act rather than part of materializing the SWRD. */}
                  {detail.requirementsMaterializedAt && (
                    detail.testProceduresMaterializedAt
                      ? <div className="frozenMark">✓ Verification artifacts materialized</div>
                      : !readOnly && (
                        <button
                          disabled={busy}
                          onClick={() => action("materialize-test-procedures", "POST", {})}
                        >
                          {busy ? "Materializing…" : "Materialize verification artifacts"}
                        </button>
                      )
                  )}
                </div>
                {detail.requirementsMaterializedAt && (
        <LegacyProcedureBootstrapPanel
          api={api}
          baselineId={detail.id}
          onCompleted={async () => {
            await loadList();
            await loadDetail();
          }}
        />
      )}
      {detail.state === "Frozen" && (
                  <div className="hashPanel">
                    <span>SHA-256 CONTENT HASH</span>
                    <code>{detail.contentHash}</code>
                    <p>
                      This hash identifies the exact ordered set of selected change request
                      revisions.
                    </p>
                  </div>
                )}
                {swrd && (
                  <section className="baselineCard swrdPanel">
                    <div className="baselineCardTitle">
                      <div>
                        <p className="eyebrow">
                          CONTROLLED OUTPUT / RELEASE {swrd.release}
                        </p>
                        <h3>{swrd.title}</h3>
                        <p>
                          {swrd.baseline} · {swrd.requirementCount} effective
                          requirement revisions
                        </p>
                      </div>
                    </div>
                    <div className="swrdHash">
                      <span>REQUIREMENT MANIFEST SHA-256</span>
                      <code>{swrd.requirementsHash}</code>
                    </div>
                    {swrd.requirements.map((item) => (
                      <article className="manifestRequirement" key={item.id}>
                        <div>
                          <b>{item.displayNumber}</b>
                          <span>{item.level}</span>
                        </div>
                        <p>{item.statement}</p>
                        <small>
                          Verification: {item.verificationMethod} · Source{" "}
                          {item.sourceScr}
                        </small>
                      </article>
                    ))}
                  </section>
                )}
                <div className="baselineColumns">
                  <div className="baselineMain">
                    <section className="baselineCard">
                      <div className="baselineCardTitle">
                        <div>
                          <h3>Selected change request revisions</h3>
                          <p>Exact approved inputs to this candidate</p>
                        </div>
                      </div>
                      {detail.selections.map((item) => (
                        <article className="selectedScr" key={item.id}>
                          <div>
                            <b>{item.displayNumber}</b>
                            <span>
                              {item.requirementChanges.length} changes
                            </span>
                          </div>
                          <p>{item.title}</p>
                          {!readOnly && detail.state === "Draft" && (
                            <button
                              onClick={() =>
                                action(`selections/${item.id}`, "DELETE")
                              }
                            >
                              Remove
                            </button>
                          )}
                        </article>
                      ))}
                      {!detail.selections.length && (
                        <div className="baselineEmpty">
                          Select at least one eligible approved change request.
                        </div>
                      )}
                    </section>
                    <section className="baselineCard">
                      <div className="baselineCardTitle">
                        <div>
                          <h3>Derived requirement-revision set</h3>
                          <p>
                            Proposed changes carried by the exact selected change request
                            revisions
                          </p>
                        </div>
                      </div>
                      {allRequirements.map((item) => (
                        <article
                          className="manifestRequirement"
                          key={`${item.scr}-${item.id}`}
                        >
                          <div>
                            <b>{item.displayNumber}</b>
                            <span>
                              {item.level} · {item.kind}
                            </span>
                          </div>
                          <p>{item.statement || "Requirement retirement"}</p>
                          <small>
                            Source {item.scr} · Verification:{" "}
                            {item.verificationMethod}
                          </small>
                        </article>
                      ))}
                      {!allRequirements.length && (
                        <div className="baselineEmpty">
                          Requirement changes will appear as change requests are selected.
                        </div>
                      )}
                    </section>
                  </div>
                  <aside className="baselineSide">
                    {!readOnly && detail.state === "Draft" && (
                      <section className="baselineCard">
                        <div className="baselineCardTitle">
                          <div>
                            <h3>Eligible approved change requests</h3>
                            <p>Same project and target release only</p>
                          </div>
                        </div>
                        {eligible.map((scr) => (
                          <article className="eligibleScr" key={scr.id}>
                            <div>
                              <b>{scr.displayNumber}</b>
                              <span>{scr.requirementCount}</span>
                            </div>
                            <p>{scr.title}</p>
                            <button
                              disabled={busy}
                              onClick={() =>
                                action("selections", "POST", {
                                  changeRequestId: scr.id,
                                })
                              }
                            >
                              + Select exact revision
                            </button>
                          </article>
                        ))}
                        {!eligible.length && (
                          <div className="baselineEmpty">
                            No additional approved change requests are eligible.
                          </div>
                        )}
                      </section>
                    )}
                    <section className="baselineCard">
                      <div className="baselineCardTitle">
                        <div>
                          <h3>Baseline history</h3>
                          <p>Append-only control events</p>
                        </div>
                      </div>
                      {detail.events.map((event, index) => (
                        <div
                          className="baselineEvent"
                          key={`${event.occurredAt}-${index}`}
                        >
                          <i />
                          <div>
                            <b>
                              {event.eventType
                                .replace(/([A-Z])/g, " $1")
                                .trim()}
                            </b>
                            <p>{event.detail}</p>
                            <small>
                              <PersonName userName={event.actorId} /> ·{" "}
                              {new Date(event.occurredAt).toLocaleString()}
                            </small>
                          </div>
                        </div>
                      ))}
                    </section>
                  </aside>
                </div>
              </>
            ) : (
              <div className="baselineEmpty large">
                Create or select a candidate baseline to begin assembly.
              </div>
            )}
          </section>
        </div>
      )}
    </main>
  );
}
