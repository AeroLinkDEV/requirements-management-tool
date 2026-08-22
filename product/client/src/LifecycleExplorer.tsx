import { useCallback, useEffect, useMemo, useState } from "react";
import "./LifecycleExplorer.css";
import "./ControlledDownloads.css";
import DocumentActions from "./DocumentActions";
import { documentTypeLabel } from "./presentation";

type Baseline = { id: string; releaseId: string; releaseVersion: string; displayNumber: string; name: string; requirementsMaterializedAt?: string };
type Document = { id: string; type: string; displayNumber: string; title: string; contentHash: string; artifactCount: number; release: string; baselineId: string; baseline: string; generatedAt: string };
type TraceEvidence = { id: string; originalFileName: string; sha256: string; size: number; uploadedAt: string };
type TraceExecution = { id: string; outcome: string; executedBy: string; executedAt: string; determination: string; evidenceReference: string; evidence: TraceEvidence[] };
type TraceTest = { procedureId: string; revisionId: string; displayNumber: string; title: string; level: string; state: string; isSuspect: boolean; coverageState: "Confirmed" | "Suspect"; executions: TraceExecution[] };
type TraceLifecycle = { state: string; causeKind: string; causeRequirementRevisionId?: string; causeBaselineImportId?: string; outcome?: string; events: { type: string; actorId: string; occurredAt: string; rationale: string; outcome?: string }[] };
type TraceRelation = { id: string; linkId: string; displayNumber: string; level: string; type: string; lifecycle?: TraceLifecycle | null };
type Trace = { id: string; revisionId: string; displayNumber: string; level: string; statement: string; testCount: number; suspectTestCount: number; parents: TraceRelation[]; children: TraceRelation[]; tests: TraceTest[] };
type CompletePath = {
  baselineId: string;
  focusRevisionId: string;
  baseline: { displayNumber: string; name: string };
  nodes: { id: string; revisionId: string; displayNumber: string; level: string; statement: string }[];
  procedure?: { id: string; revisionId: string; displayNumber: string; title: string; level: string; state: string };
  execution?: TraceExecution;
  build?: { id: string; buildNumber: string; state: string; recordedAt: string; releasedAt?: string };
};
type Props = {
  api: string;
  projectId: string;
  activeReleaseId: string;
  releases: { id: string; version: string; isReleased?: boolean }[];
  /**
   * The requirement to open on, as a stable artifact identity rather than a revision or a display number —
   * revisions move and display numbers can be re-read in another context. Carried in the route, so a refresh
   * or a shared link lands on the same record.
   */
  initialArtifactId?: string;
  onBack: () => void;
};

export default function LifecycleExplorer({ api, projectId, activeReleaseId, releases, initialArtifactId, onBack }: Props) {
  const [tab, setTab] = useState<"thread" | "documents">("thread");
  // Released and draft documents are different kinds of thing, so the tab asks which you came for rather than
  // mixing a controlled record that carries a content hash in among generated-on-the-spot drafts that
  // deliberately carry none. Released first: it is the answer to "what did we ship".
  const [documentKind, setDocumentKind] = useState<"released" | "draft">("released");
  const [threadMode, setThreadMode] = useState<"map" | "evidence">("map");
  const [baselines, setBaselines] = useState<Baseline[]>([]);
  const [baselineId, setBaselineId] = useState("");
  const [documents, setDocuments] = useState<Document[]>([]);
  const [traces, setTraces] = useState<Trace[]>([]);
  const [completePath, setCompletePath] = useState<CompletePath>();
  const [focusId, setFocusId] = useState("");
  const [query, setQuery] = useState("");
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const contextResponse = await fetch(`${api}/api/build-context?projectId=${projectId}&releaseId=${activeReleaseId}`);
      if (!contextResponse.ok) throw new Error("The active build context could not be loaded.");
      const buildContext = await contextResponse.json() as {
        effectiveBaselineId?: string;
        inheritedBaseline: boolean;
        effectiveBaseline?: { id: string; baseNumber: string; revision: number; name: string; requirementsMaterializedAt?: string; releaseId: string; releaseVersion: string };
      };
      const lists = await Promise.all(
        releases.map(async (release) => {
          const response = await fetch(`${api}/api/baselines?projectId=${projectId}&releaseId=${release.id}`);
          if (!response.ok) throw new Error("Controlled baselines could not be loaded.");
          const items = (await response.json()) as Omit<Baseline, "releaseId" | "releaseVersion">[];
          return items.map((item) => ({ ...item, releaseId: release.id, releaseVersion: release.version }));
        }),
      );
      const inherited = buildContext.inheritedBaseline && buildContext.effectiveBaseline
        ? {
            id: buildContext.effectiveBaseline.id,
            displayNumber: `${buildContext.effectiveBaseline.baseNumber}.${String(buildContext.effectiveBaseline.revision).padStart(2, "0")}`,
            name: `${buildContext.effectiveBaseline.name} · inherited from Build ${buildContext.effectiveBaseline.releaseVersion}`,
            requirementsMaterializedAt: buildContext.effectiveBaseline.requirementsMaterializedAt,
            releaseId: buildContext.effectiveBaseline.releaseId,
            releaseVersion: buildContext.effectiveBaseline.releaseVersion,
          }
        : undefined;
      const bs = [...lists.flat().filter((item) => item.requirementsMaterializedAt),
        ...(inherited && !lists.flat().some((item) => item.id === inherited.id) ? [inherited] : [])];
      setBaselines(bs);
      const chosen = bs.some((item) => item.id === baselineId)
        ? baselineId
        : buildContext.effectiveBaselineId || bs.find((item) => item.releaseId === activeReleaseId)?.id || bs[0]?.id || "";
      if (chosen !== baselineId) setBaselineId(chosen);
      const [documentResponse, traceResponse] = await Promise.all([
        fetch(`${api}/api/documents?projectId=${projectId}&releaseId=${activeReleaseId}`),
        chosen
          ? fetch(`${api}/api/traceability?projectId=${projectId}&baselineId=${chosen}&search=${encodeURIComponent(query)}&page=1&pageSize=200`)
          : undefined,
      ]);
      if (!documentResponse.ok || (traceResponse && !traceResponse.ok)) throw new Error("Traceability evidence could not be loaded.");
      const allDocuments = (await documentResponse.json()) as Document[];
      setDocuments(allDocuments.filter((item) => item.baselineId === chosen));
      if (traceResponse) {
        const body = (await traceResponse.json()) as { items: Trace[]; totalCount: number };
        setTraces(body.items);
        setTotal(body.totalCount);
        // Order matters: keep whatever the reader is already looking at, otherwise honour the artifact the
        // route asked for, and only then fall back to the first row. This line used to go straight to the
        // first row, which is why arriving from SYSR-000011 focused HLR-000001.00.
        setFocusId((current) => body.items.some((item) => item.revisionId === current)
          ? current
          : body.items.find((item) => item.id === initialArtifactId)?.revisionId ?? body.items[0]?.revisionId ?? "");
      } else {
        setTraces([]);
        setTotal(0);
        setFocusId("");
      }
      setError("");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Traceability evidence could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, [api, projectId, releases, activeReleaseId, baselineId, query, initialArtifactId]);

  useEffect(() => {
    const timer = setTimeout(load, 150);
    return () => clearTimeout(timer);
  }, [load]);

  useEffect(() => {
    if (!baselineId || !focusId) {
      setCompletePath(undefined);
      return;
    }
    let cancelled = false;
    (async () => {
      const response = await fetch(`${api}/api/traceability/path?projectId=${projectId}&baselineId=${baselineId}&requirementRevisionId=${focusId}`);
      if (!response.ok) throw new Error("The complete lifecycle path could not be loaded.");
      const body = await response.json() as CompletePath;
      if (!cancelled) setCompletePath(body);
    })().catch((reason) => {
      if (!cancelled) setError(reason instanceof Error ? reason.message : "The complete lifecycle path could not be loaded.");
    });
    return () => { cancelled = true; };
  }, [api, projectId, baselineId, focusId]);

  // The thread loads one page of traces, so a requirement outside that page cannot be focused however
  // precisely the route asks for it — which is why passing the identity through was necessary but not
  // sufficient. Seeding the search with the requested record's number is exactly what a reader does by hand
  // today, and what traverse() already does to move focus. The number only loads the row; focus is still
  // resolved from the stable artifact id.
  useEffect(() => {
    if (!initialArtifactId) return;
    let cancelled = false;
    (async () => {
      try {
        const response = await fetch(`${api}/api/enterprise-requirements/${initialArtifactId}`);
        if (!response.ok || cancelled) return;
        const body = (await response.json()) as { baseNumber?: string };
        if (body.baseNumber && !cancelled) setQuery(body.baseNumber);
      } catch {
        // Leave the thread on its default page; requestedButAbsent explains what happened.
      }
    })();
    return () => { cancelled = true; };
  }, [api, initialArtifactId]);

  const generate = async () => {
    if (!baselineId) return;
    const response = await fetch(`${api}/api/baselines/${baselineId}/generate-documents`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    if (!response.ok) {
      setError((await response.json()).error || "Controlled outputs could not be generated.");
      return;
    }
    await load();
    setTab("documents");
  };

  const traverse = (relation: TraceRelation) => {
    setQuery(relation.displayNumber.replace(/\.\d{2}$/, ""));
    setThreadMode("map");
  };
  // Named for the reader, not for the URL. The draft header says which build it is a draft of, and "Draft
  // documents for " followed by a bare identifier is worse than saying nothing.
  const activeVersion = releases.find((item) => item.id === activeReleaseId)?.version ?? "the release in work";
  const focus = traces.find((item) => item.revisionId === focusId) ?? traces[0];
  // A requested artifact outside the loaded page — filtered out by the search box, or on another baseline —
  // must not be answered with an unrelated record and no explanation.
  const requestedButAbsent = Boolean(initialArtifactId) && traces.length > 0 && !traces.some((item) => item.id === initialArtifactId);
  const selectedBaseline = baselines.find((item) => item.id === baselineId);
  const confirmedTests = useMemo(() => focus?.tests.filter((test) => !test.isSuspect) ?? [], [focus]);
  const executions = useMemo(() => confirmedTests.flatMap((test) => test.executions), [confirmedTests]);
  const evidence = useMemo(() => executions.flatMap((execution) => execution.evidence), [executions]);
  const threadPercent = focus ? Math.round(([true, confirmedTests.length > 0, executions.length > 0, evidence.length > 0].filter(Boolean).length / 4) * 100) : 0;
  const answer = !focus
    ? "Select a requirement to inspect its digital thread."
    : !confirmedTests.length
      ? focus.tests.some((test) => test.isSuspect)
        ? "Not yet — procedure applicability is suspect after the requirement changed and does not count as confirmed coverage."
        : "Not yet — no verification procedure is linked to this controlled revision."
      : !executions.length
        ? "Not yet — linked procedures are awaiting an authoritative execution result."
        : !evidence.length
          ? "Partially — an execution result exists, but no immutable evidence file is attached."
          : `Yes — ${confirmedTests.length} confirmed procedure${confirmedTests.length === 1 ? "" : "s"}, ${executions.length} execution${executions.length === 1 ? "" : "s"}, and ${evidence.length} evidence file${evidence.length === 1 ? "" : "s"} support this claim.`;

  return (
    <main className="lifecyclePage">
      <header>
        <div>
          <button className="back" onClick={onBack}>← Command Center</button>
          <p className="eyebrow">ASSURANCE / DIGITAL THREAD FOCUS</p>
          <h1>Digital Thread</h1>
          <p>Answer one engineering question across requirement derivation, verification, immutable evidence, and release configuration.</p>
        </div>
      </header>
      {error && <div className="workspaceError" role="alert">{error}<button onClick={load}>Retry</button></div>}
      {loading && !baselines.length && <section className="traceEmpty"><b>Loading exact configuration…</b><p>Resolving the active release baseline and its evidence network.</p></section>}
      <div className="lifeTabs">
        <button className={tab === "thread" ? "active" : ""} onClick={() => setTab("thread")}>Digital Thread</button>
      </div>

      {tab === "thread" ? <>
        <section className="traceTools">
          <select aria-label="Traceability baseline" value={baselineId} onChange={(event) => setBaselineId(event.target.value)}>{baselines.map((item) => <option value={item.id} key={item.id}>{item.displayNumber} · {item.name}</option>)}</select>
          <input aria-label="Search digital thread" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search any identifier fragment…" />
          <b>{total.toLocaleString()} requirements</b>
          <div className="downloadLinks"><a href={`${api}/api/traceability/${baselineId}/download?format=pdf`}>Trace PDF</a><a href={`${api}/api/traceability/${baselineId}/download?format=docx`}>Trace DOCX</a></div>
        </section>
        <section className="threadToolbar">
          <div className="threadViewSwitch" role="group" aria-label="Digital thread view">
            <button className={threadMode === "map" ? "active" : ""} onClick={() => setThreadMode("map")}>Relationship map</button>
            <button className={threadMode === "evidence" ? "active" : ""} onClick={() => setThreadMode("evidence")}>Evidence table</button>
          </div>
          <div className="threadChips"><span>{selectedBaseline?.releaseVersion ? `Release ${selectedBaseline.releaseVersion}` : "Controlled baseline"}</span><strong>{threadPercent}% complete</strong></div>
        </section>

        {threadMode === "map" ? focus ? <section className="digitalThreadStage">
          <div className="threadCanvas">
            {requestedButAbsent && <p className="threadFocusMissing" role="status">The requested requirement is not in the current baseline or search results, so the thread is showing {focus.displayNumber} instead. Clear the search or choose the baseline that contains it.</p>}
            <header><div><span>COMPLETE LIFECYCLE PATH</span><b>{focus.displayNumber}</b></div><small>Select a requirement to traverse; every card is exact to this baseline</small></header>
            {completePath ? <div className="completeThreadPath" role="list" aria-label={`Complete digital thread for ${focus.displayNumber}`}>
              {completePath.nodes.map((node, index) => <div className="completeThreadStep" key={node.revisionId}>
                {index > 0 && <i className="threadConnector" aria-hidden="true">›</i>}
                <button type="button" className={node.revisionId === focus.revisionId ? "selected" : ""} onClick={() => { setQuery(node.displayNumber.replace(/\.\d{2}$/, "")); setFocusId(node.revisionId); }}>
                  <small>{node.level === "System" ? "SYSTEM REQUIREMENT" : node.level === "HighLevel" ? "HLR" : "LLR"}</small>
                  <b>{node.displayNumber}</b><span>{node.statement}</span>
                </button>
              </div>)}
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.procedure ? "missing" : ""}><small>TEST PROCEDURE</small><b>{completePath.procedure?.displayNumber ?? "Not linked"}</b><span>{completePath.procedure?.title ?? "Procedure linkage required"}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.execution ? "missing" : ""}><small>TEST RESULT</small><b>{completePath.execution?.outcome ?? "Not executed"}</b><span>{completePath.execution?.determination ?? "Authoritative result required"}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.execution?.evidence.length ? "missing" : ""}><small>TEST EVIDENCE</small><b>{completePath.execution?.evidence[0]?.originalFileName ?? "Not attached"}</b><span>{completePath.execution?.evidence[0]?.sha256 ?? (completePath.execution?.evidenceReference ? `External reference only: ${completePath.execution.evidenceReference}` : "Checksummed evidence required")}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.build ? "missing" : ""}><small>BUILD</small><b>{completePath.build?.buildNumber ?? completePath.baseline.displayNumber}</b><span>{completePath.build ? `${completePath.build.state} · ${completePath.baseline.displayNumber}` : completePath.baseline.name}</span></article></div>
            </div> : <div className="traceEmpty"><b>Resolving complete path…</b><p>Following exact requirement revisions into verification evidence and build configuration.</p></div>}
            <footer><b>{completePath ? `${completePath.nodes.length} requirement levels in this path` : "Resolving connected records"}</b><span>SYSR → HLR → LLR → procedure → result → evidence → build</span></footer>
          </div>
          <aside className="threadInspector">
            <div className="threadFocusMark"><i>{focus.level.slice(0, 2).toUpperCase()}</i><span>{focus.level}</span></div>
            <p className="eyebrow">SELECTED CONTROLLED RECORD</p>
            <h2>{focus.displayNumber}</h2>
            <p>{focus.statement}</p>
            <div className="threadCompleteness"><div><b>Thread completeness</b><strong>{threadPercent}%</strong></div><span><i style={{ width: `${threadPercent}%` }} /></span></div>
            <section className="threadAnswer"><small>THE QUESTION THIS ANSWERS</small><h3>Can this baseline claim verification evidence for the selected requirement?</h3><p>{answer}</p></section>
            <div className="threadRelations"><div><span>Parents</span><b>{focus.parents.length}</b></div><div><span>Children</span><b>{focus.children.length}</b></div><div><span>Confirmed procedures</span><b>{confirmedTests.length}</b></div><div><span>Evidence</span><b>{evidence.length}</b></div></div>
            {!!focus.children.length && <section className="threadDownstream"><small>DOWNSTREAM</small>{focus.children.slice(0, 4).map((child) => <button key={child.id} onClick={() => traverse(child)}><span>{child.displayNumber}</span><b>Open path →</b></button>)}</section>}
          </aside>
        </section> : <section className="traceEmpty"><b>No matching digital thread</b><p>Broaden the identifier search or choose another controlled baseline.</p></section> :
        <section className="traceList">{traces.map((item) => <article key={item.revisionId} className={item.revisionId === focusId ? "focused" : ""}>
          <div className="traceIdentity"><button onClick={() => { setFocusId(item.revisionId); setThreadMode("map"); }}>{item.displayNumber}</button><i>{item.level}</i><span>{item.testCount} confirmed{item.suspectTestCount ? ` · ${item.suspectTestCount} suspect` : ""}</span></div><p>{item.statement}</p>
          <details className="traceDetails" open={query.trim() !== "" && traces.length === 1 ? true : undefined}><summary><span>Explore relationships and evidence</span><small>{item.parents.length} parent{item.parents.length === 1 ? "" : "s"} · {item.children.length} child{item.children.length === 1 ? "" : "ren"} · {item.tests.length} procedure{item.tests.length === 1 ? "" : "s"}</small></summary>
            <div className="traceRelations"><div><small>PARENT / DERIVED FROM</small>{item.parents.map((parent) => <button key={parent.linkId} onClick={() => traverse(parent)}>{parent.displayNumber} · {parent.level}{parent.lifecycle && <i className={`traceLinkState ${parent.lifecycle.state.toLowerCase()}`}>{parent.lifecycle.state}</i>}</button>)}{!item.parents.length && <em>Top-level requirement</em>}</div><div><small>CHILDREN / SATISFIED BY</small>{item.children.slice(0, 8).map((child) => <button key={child.linkId} onClick={() => traverse(child)}>{child.displayNumber} · {child.level}{child.lifecycle && <i className={`traceLinkState ${child.lifecycle.state.toLowerCase()}`}>{child.lifecycle.state}</i>}</button>)}{item.children.length > 8 && <em>+ {item.children.length - 8} additional children</em>}{!item.children.length && <em>Leaf-level requirement</em>}</div></div>
            {item.tests.length > 0 && <div className="traceVerification"><small>VERIFICATION / RESULTS / EVIDENCE</small>{item.tests.map((test) => <section className={test.isSuspect ? "suspect" : ""} key={test.revisionId}><div><b>{test.displayNumber}</b><span>{test.title} · {test.isSuspect ? "Suspect applicability — not coverage" : "Confirmed applicability"}</span></div>{!test.isSuspect && test.executions.map((run) => <article key={run.id}><i className={run.outcome.toLowerCase()}>{run.outcome}</i><p>{run.determination}</p><small>{run.executedBy} · {new Date(run.executedAt).toLocaleString()}</small>{run.evidence.map((file) => <a key={file.id} href={`${api}/api/evidence/${file.id}`}><b>{file.originalFileName}</b><code>{file.sha256}</code></a>)}</article>)}{test.isSuspect ? <em>Resolve this applicability in Verification change impact.</em> : !test.executions.length && <em>Approved procedure awaiting execution</em>}</section>)}</div>}
          </details>
        </article>)}</section>}
      </> : <>
        {/* Two different kinds of thing, so the reader says which they came for instead of finding both mixed
            together. A released document is a controlled record with a content hash. A draft is generated on
            request from the released baseline with every approved change folded in, carries the revision the
            released document will carry, is stamped DRAFT on every page, and is deliberately never stored —
            the content is still moving, and a controlled record of that would be a record of nothing. */}
        <div className="documentKindSwitch" role="group" aria-label="Which documents">
          <button type="button" aria-pressed={documentKind === "released"} onClick={() => setDocumentKind("released")}>Released documents</button>
          <button type="button" aria-pressed={documentKind === "draft"} onClick={() => setDocumentKind("draft")}>Draft documents</button>
        </div>
        {documentKind === "draft" ? (
          <DocumentActions
            api={api}
            projectId={projectId}
            release={{ id: activeReleaseId, version: activeVersion, isReleased: false }}
            targets={[
              { type: "Sysrd", label: documentTypeLabel("Sysrd") },
              { type: "SwrdHighLevel", label: documentTypeLabel("SwrdHighLevel") },
              { type: "SwrdLowLevel", label: documentTypeLabel("SwrdLowLevel") },
            ]}
            heading={`Draft documents for ${activeVersion}`}
          />
        ) : <>
        <div className="documentActions"><select value={baselineId} onChange={(event) => setBaselineId(event.target.value)}>{baselines.map((item) => <option value={item.id} key={item.id}>{item.displayNumber} · {item.name}</option>)}</select><button onClick={generate}>Generate / refresh outputs</button></div>
        <section className="documentGrid">{documents.map((item) => <article key={item.id}><div><span>{documentTypeLabel(item.type)}</span><i>CONTROLLED</i></div><h2>{item.displayNumber}</h2><h3>{item.title}</h3><dl><div><dt>Release</dt><dd>{item.release}</dd></div><div><dt>Baseline</dt><dd>{item.baseline}</dd></div><div><dt>Artifacts</dt><dd>{item.artifactCount.toLocaleString()}</dd></div><div><dt>Generated</dt><dd>{new Date(item.generatedAt).toLocaleDateString()}</dd></div></dl><code>{item.contentHash}</code><div className="downloadLinks"><a href={`${api}/api/documents/${item.id}/download?format=docx`}>Download DOCX</a><a href={`${api}/api/documents/${item.id}/download?format=pdf`}>Download PDF</a></div></article>)}{!loading && !documents.length && <div className="traceEmpty"><b>No outputs for this baseline</b><p>Generate controlled documents only after the selected requirement baseline has been materialized.</p></div>}</section>
        </>}
      </>}
    </main>
  );
}
