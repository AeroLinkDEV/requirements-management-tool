import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import "./LifecycleExplorer.css";
import "./ControlledDownloads.css";
import DocumentActions from "./DocumentActions";
import { documentTypeLabel, stateLabel, verificationArtifactNoun } from "./presentation";
import ExactLinkLifecyclePanel from "./ExactLinkLifecyclePanel";
import ExactArtifactLink from "./ExactArtifactLink";
import { traceKindLabel, traceProvenanceLabel, traceRelationLabel } from "./tracePresentation";

type Baseline = { id: string; releaseId: string; releaseVersion: string; displayNumber: string; name: string; requirementsMaterializedAt?: string };
type Document = { id: string; type: string; displayNumber: string; title: string; contentHash: string; artifactCount: number; release: string; baselineId: string; baseline: string; generatedAt: string };
type TraceEvidence = { id: string; originalFileName: string; sha256: string; size: number; uploadedAt: string };
type TraceExecution = { id: string; outcome: string; executedBy: string; executedAt: string; determination: string; evidenceReference: string; evidence: TraceEvidence[] };
type TraceTest = { artifactId: string; procedureId?: string; artifactRevisionId: string; revisionId?: string; displayNumber: string; title: string; level: string; artifactState: string; state?: string; isSuspect: boolean; coverageState: "Confirmed" | "Suspect"; executions: TraceExecution[] };
type TraceLifecycle = { state: string; causeKind: string; causeRequirementRevisionId?: string; causeBaselineImportId?: string; outcome?: string; events: { type: string; actorId: string; occurredAt: string; rationale: string; outcome?: string }[] };
type TraceRelation = { id: string; linkId: string; displayNumber: string; level: string; type: string; lifecycle?: TraceLifecycle | null };
type Trace = { id: string; revisionId: string; displayNumber: string; level: string; statement: string; testCount: number; suspectTestCount: number; parents: TraceRelation[]; children: TraceRelation[]; tests: TraceTest[] };
type CompletePath = {
  baselineId: string;
  focusRevisionId: string;
  baseline: { displayNumber: string; name: string };
  nodes: { id: string; revisionId: string; displayNumber: string; level: string; statement: string }[];
  artifact?: { id: string; revisionId: string; displayNumber: string; title: string; artifactKind: string; level: string; state: string };
  procedure?: { id: string; revisionId: string; displayNumber: string; title: string; artifactKind?: string; level: string; state: string };
  execution?: TraceExecution;
  build?: { id: string; buildNumber: string; state: string; recordedAt: string; releasedAt?: string };
};
type ChangeRequestProposal = { id: string; displayNumber: string; level: string; kind: string; statement: string; rationale?: string };
type ChangeRequestDetail = {
  id: string; displayNumber: string; revision: number; projectId: string; targetReleaseId: string;
  type: string; title: string; state: string; requirementChanges: ChangeRequestProposal[];
};
type ChangeRequestTraceNode = {
  id: string; kind: string; displayNumber: string; title?: string | null; state?: string | null; revisionId?: string | null;
  projectId?: string | null; buildId?: string | null; buildVersion?: string | null; revision?: number | null;
  level?: string | null; artifactId?: string | null; baselineMembershipIds?: string[] | null;
};
type ChangeRequestTraceEdge = {
  fromId: string; fromKind: string; toId: string; toKind: string; relation: string;
  provenance: { kind: string; sourceId?: string | null; isLive?: boolean; rationale?: string | null }[];
};
type ChangeRequestTrace = {
  projectId: string; rootChangeRequestId: string; rootArtifactId?: string; rootArtifactKind?: string;
  nodes: ChangeRequestTraceNode[]; edges: ChangeRequestTraceEdge[];
  state?: { upstream: string; downstream: string; overall: string; warnings: string[] } | null;
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
  initialArtifactKind?: string;
  requirementHref?: (artifactId: string) => string;
  traceArtifactHref?: (node: ChangeRequestTraceNode) => string | undefined;
  onBack: () => void;
};

function ChangeRequestThread({
  detail,
  trace,
  activeBaselineId,
  exactPath,
  exactPathError,
  onRetry,
  requirementHref,
  traceArtifactHref,
}: {
  detail: ChangeRequestDetail;
  trace: ChangeRequestTrace;
  activeBaselineId?: string;
  exactPath?: CompletePath;
  exactPathError?: string;
  onRetry: () => void;
  requirementHref?: (artifactId: string) => string;
  traceArtifactHref?: (node: ChangeRequestTraceNode) => string | undefined;
}) {
  const root = trace.nodes.find(node => node.kind === "ChangeRequest" && node.id === detail.id);
  const materialized = trace.nodes.filter(node => node.kind === "RequirementRevision"
    && Boolean(activeBaselineId) && activeBaselineId !== undefined && node.baselineMembershipIds?.includes(activeBaselineId)
    && node.artifactId && trace.edges.some(edge => edge.fromId === detail.id
      && edge.toId === node.id && edge.relation === "OwnsRequirementRevision"));
  const nodeById = new Map(trace.nodes.map(node => [node.id, node]));
  const [selectedNodeId, setSelectedNodeId] = useState(detail.id);
  useEffect(() => setSelectedNodeId(detail.id), [detail.id]);
  const selected = nodeById.get(selectedNodeId) ?? root;
  const nodeHref = (node: ChangeRequestTraceNode) => {
    const fallback = node.kind === "RequirementRevision" && node.artifactId && requirementHref ? requirementHref(node.artifactId) : undefined;
    return traceArtifactHref?.(node) ?? (fallback && fallback !== "#" ? fallback : undefined);
  };
  const exactPathNodeHref = (node: CompletePath["nodes"][number]) => traceArtifactHref?.({
    id: node.revisionId,
    kind: "RequirementRevision",
    displayNumber: node.displayNumber,
    level: node.level,
    artifactId: node.id,
  });
  const exactPathArtifactHref = (kind: string, item: { id: string; revisionId?: string; displayNumber?: string; level?: string }) => traceArtifactHref?.({
    id: item.id,
    kind,
    revisionId: item.revisionId,
    displayNumber: item.displayNumber ?? item.id,
    level: item.level,
  });
  const layers = [
    { id: "change", title: "CHANGE CONTROL", kinds: new Set(["ChangeRequest"]) },
    { id: "requirement", title: "REQUIREMENTS", kinds: new Set(["RequirementRevision"]) },
    { id: "verification", title: "VERIFICATION / EVIDENCE", kinds: new Set(["TestChangeRequest", "CodeTraceability"]) },
  ];
  const assigned = new Set<string>();
  const layerNodes = layers.map(layer => ({ ...layer, nodes: trace.nodes.filter(node => {
    const match = layer.kinds.has(node.kind) && !assigned.has(node.id);
    if (match) assigned.add(node.id);
    return match;
  }) }));
  const otherNodes = trace.nodes.filter(node => !assigned.has(node.id));
  if (otherNodes.length) layerNodes.push({ id: "other", title: "CONNECTED ARTIFACTS", kinds: new Set<string>(), nodes: otherNodes });
  const selectedEdges = selected ? trace.edges.filter(edge => edge.fromId === selected.id || edge.toId === selected.id) : [];

  return <section className="crDigitalThread" aria-label={`Digital Thread for ${detail.displayNumber}`}>
    <header className="crDigitalThreadHeader">
      <div>
        <p className="eyebrow">CHANGE REQUEST DIGITAL THREAD</p>
        <h2><ExactArtifactLink href={root ? nodeHref(root) : undefined}>{root?.displayNumber ?? detail.displayNumber}</ExactArtifactLink></h2>
        <p>{detail.title}</p>
      </div>
      <dl>
        <div><dt>Exact identity</dt><dd>{detail.id}</dd></div>
        <div><dt>Revision</dt><dd>{detail.revision.toString().padStart(2, "0")}</dd></div>
        <div><dt>Lifecycle</dt><dd>{stateLabel(detail.state)}</dd></div>
      </dl>
    </header>

    <section className="crChain" aria-labelledby="cr-chain-heading">
      <div className="crSectionHeading"><div><p className="eyebrow">SERVER-COMPOSED PROJECTION</p><h3 id="cr-chain-heading">Connected controlled story</h3></div><span>{trace.nodes.length} exact nodes · {trace.edges.length} typed edges</span></div>
      <ChangeRequestGraphMap layers={layerNodes} trace={trace} selectedId={selected?.id} onSelect={setSelectedNodeId} nodeHref={nodeHref} />
      {trace.state && <section className="crTraceState" aria-label="Authoritative trace state">
        <h4>Authoritative trace state</h4><dl><div><dt>Upstream</dt><dd>{stateLabel(trace.state.upstream)}</dd></div><div><dt>Downstream</dt><dd>{stateLabel(trace.state.downstream)}</dd></div><div><dt>Overall</dt><dd>{stateLabel(trace.state.overall)}</dd></div></dl>
        {trace.state.warnings.length > 0 && <ul>{trace.state.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>}
      </section>}
    </section>

    {selected && <aside className="crSelectedNode" aria-label="Selected node details">
      <div className="crSectionHeading"><div><p className="eyebrow">SELECTED NODE</p><h3><ExactArtifactLink href={nodeHref(selected)}>{selected.displayNumber}</ExactArtifactLink></h3></div><span>{selectedEdges.length} connected relationship{selectedEdges.length === 1 ? "" : "s"}</span></div>
      <p>{selected.title || "No additional title is recorded for this exact node."}</p>
      <dl><div><dt>Type</dt><dd>{traceKindLabel(selected.kind)}</dd></div><div><dt>State</dt><dd>{selected.state ? stateLabel(selected.state) : "Not stated"}</dd></div>{selected.buildVersion && <div><dt>Build</dt><dd>{selected.buildVersion}</dd></div>}{selected.revision !== null && selected.revision !== undefined && <div><dt>Revision</dt><dd>{String(selected.revision).padStart(2, "0")}</dd></div>}</dl>
      <ExactArtifactLink href={nodeHref(selected)}>Open exact artifact →</ExactArtifactLink>
    </aside>}

    <section className="crProposalLayer" aria-labelledby="cr-proposal-heading">
      <div className="crSectionHeading"><div><p className="eyebrow">IN-WORK CONTENT</p><h3 id="cr-proposal-heading">Proposed requirement changes</h3></div><span>Not baseline truth</span></div>
      <p className="crTruthNote">These Introduce, Modify, and Retire entries are proposals carried by this change request. They are not materialized requirement revisions, effective baseline nodes, verification results, evidence, or release content.</p>
      {detail.requirementChanges.length ? <div className="crProposalGrid">{detail.requirementChanges.map(change => <article key={change.id}>
        <div><b>{change.displayNumber}</b><span>{change.kind} · {change.level}</span></div><p>{change.statement || "No proposed statement is recorded."}</p>
        {change.rationale && <small>Rationale: {change.rationale}</small>}<code>Proposal identity {change.id}</code>
      </article>)}</div> : <p className="crUnavailable">This change request carries no requirement changes.</p>}
    </section>

    <section className="crMaterializedLayer" aria-labelledby="cr-materialized-heading">
      <div className="crSectionHeading"><div><p className="eyebrow">BASELINE-EXACT CONTENT</p><h3 id="cr-materialized-heading">Materialized requirement revisions</h3></div></div>
      {materialized.length ? <div className="crMaterializedGrid">{materialized.map(node => <article key={node.id}><ExactArtifactLink href={nodeHref(node)}>{node.displayNumber}</ExactArtifactLink><span>{node.id}</span><ExactArtifactLink href={nodeHref(node)}>Open exact requirement thread →</ExactArtifactLink></article>)}</div>
        : <p className="crUnavailable">No requirement revision carried by this change request is materialized in the active baseline. Proposed content remains outside the baseline-exact thread.</p>}
      {exactPathError && <div className="crUnavailable" role="alert"><span>{exactPathError}</span><button type="button" onClick={onRetry}>Retry</button></div>}
      {exactPath && <section className="crBaselinePath" aria-labelledby="cr-baseline-path-heading">
        <h4 id="cr-baseline-path-heading">Existing baseline-exact requirement path</h4>
        <div className="completeThreadPath" role="list" aria-label="Existing baseline-exact requirement path">
          {exactPath.nodes.map((node, index) => <div className="completeThreadStep" key={node.revisionId}>
            {index > 0 && <i className="threadConnector" aria-hidden="true">›</i>}
            <article><small>{node.level === "System" ? "SYSTEM REQUIREMENT" : node.level === "HighLevel" ? "HLR" : "LLR"}</small><ExactArtifactLink className="completeThreadExactIdentifier" href={exactPathNodeHref(node)}>{node.displayNumber}</ExactArtifactLink><span>{node.statement}</span></article>
          </div>)}
          <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!exactPath.artifact ? "missing" : ""}><small>TEST ARTIFACT</small>{exactPath.artifact ? <ExactArtifactLink className="completeThreadExactIdentifier completeThreadTestArtifact" href={exactPathArtifactHref(exactPath.artifact.artifactKind === "Procedure" ? "TestProcedure" : "TestCase", exactPath.artifact)}>{exactPath.artifact.displayNumber}</ExactArtifactLink> : <b>Not linked</b>}<span>{exactPath.artifact?.title ?? "Verification linkage required"}</span></article></div>
          <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!exactPath.execution ? "missing" : ""}><small>TEST RESULT</small>{exactPath.execution ? <ExactArtifactLink href={exactPathArtifactHref("TestExecution", exactPath.execution)}>{exactPath.execution.outcome}</ExactArtifactLink> : <b>Not executed</b>}<span>{exactPath.execution?.determination ?? "Authoritative result required"}</span></article></div>
          <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!exactPath.execution?.evidence.length ? "missing" : ""}><small>TEST EVIDENCE</small>{exactPath.execution?.evidence[0] ? <ExactArtifactLink href={exactPathArtifactHref("Evidence", exactPath.execution.evidence[0])}>{exactPath.execution.evidence[0].originalFileName}</ExactArtifactLink> : <b>Not attached</b>}<span>{exactPath.execution?.evidence[0]?.sha256 ?? (exactPath.execution?.evidenceReference ? `External reference only: ${exactPath.execution.evidenceReference}` : "Checksummed evidence required")}</span></article></div>
          <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!exactPath.build ? "missing" : ""}><small>BUILD</small>{exactPath.build ? <ExactArtifactLink href={exactPathArtifactHref("Build", exactPath.build)}>{exactPath.build.buildNumber}</ExactArtifactLink> : <b>{exactPath.baseline.displayNumber}</b>}<span>{exactPath.build ? `${exactPath.build.state} · ${exactPath.baseline.displayNumber}` : exactPath.baseline.name}</span></article></div>
        </div>
      </section>}
    </section>
  </section>;
}

type TraceGraphLayer = { id: string; title: string; nodes: ChangeRequestTraceNode[] };
type TraceConnector = {
  key: string;
  path: string;
  selected: boolean;
  relation: string;
  route: "cross-layer" | "cross-layer-offset" | "same-layer-horizontal" | "same-layer-vertical" | "same-layer-rail";
};

/**
 * Render the server-composed graph as a map, not as unrelated category lists. Cards stay in semantic layers,
 * while the SVG connector overlay joins each exact pair using the same edge set exposed to the accessible edge
 * register below. Coordinates are presentation-only; no client-side relationship or topology is inferred.
 */
function ChangeRequestGraphMap({
  layers,
  trace,
  selectedId,
  onSelect,
  nodeHref,
}: {
  layers: TraceGraphLayer[];
  trace: ChangeRequestTrace;
  selectedId?: string;
  onSelect: (id: string) => void;
  nodeHref: (node: ChangeRequestTraceNode) => string | undefined;
}) {
  const boardRef = useRef<HTMLDivElement>(null);
  const nodeRefs = useRef(new Map<string, HTMLElement>());
  const [connectors, setConnectors] = useState<TraceConnector[]>([]);
  const nodeByKey = useMemo(() => new Map(trace.nodes.map(node => [`${node.kind}:${node.id}`, node])), [trace.nodes]);
  const representableEdgeCount = useMemo(() => trace.edges.filter(edge => nodeByKey.has(`${edge.fromKind}:${edge.fromId}`)
    && nodeByKey.has(`${edge.toKind}:${edge.toId}`)).length, [nodeByKey, trace.edges]);

  const measure = useCallback(() => {
    const board = boardRef.current;
    if (!board) return;
    const boardRect = board.getBoundingClientRect();
    const next: TraceConnector[] = [];
    const railOrdinals = new Map<HTMLElement, [number, number]>();
    for (let edgeIndex = 0; edgeIndex < trace.edges.length; edgeIndex += 1) {
      const edge = trace.edges[edgeIndex];
      const from = nodeByKey.get(`${edge.fromKind}:${edge.fromId}`);
      const to = nodeByKey.get(`${edge.toKind}:${edge.toId}`);
      const fromElement = from && nodeRefs.current.get(`${from.kind}:${from.id}`);
      const toElement = to && nodeRefs.current.get(`${to.kind}:${to.id}`);
      if (!fromElement || !toElement) continue;
      const fromRect = fromElement.getBoundingClientRect();
      const toRect = toElement.getBoundingClientRect();
      const fromLayer = fromElement.closest<HTMLElement>(".crGraphLayer");
      const toLayer = toElement.closest<HTMLElement>(".crGraphLayer");
      const sameLayer = Boolean(fromLayer && fromLayer === toLayer);
      const fromCenterX = fromRect.left + fromRect.width / 2 - boardRect.left;
      const toCenterX = toRect.left + toRect.width / 2 - boardRect.left;
      const fromCenterY = fromRect.top + fromRect.height / 2 - boardRect.top;
      const toCenterY = toRect.top + toRect.height / 2 - boardRect.top;
      const directChangeVerification = !sameLayer
        && ((from.kind === "ChangeRequest" && to.kind === "TestChangeRequest")
          || (from.kind === "TestChangeRequest" && to.kind === "ChangeRequest"));
      let route: TraceConnector["route"] = "cross-layer";
      let path: string;

      if (directChangeVerification) {
        // A direct CR-to-TCR edge can span the Requirements layer. A normal midpoint-to-midpoint curve would
        // disappear below that intervening card and falsely read as CR -> Requirement -> TCR. Take an offset
        // route above the cards, then descend along the verification layer's outside edge before entering the
        // exact target. This is presentation-only geometry; the server-composed edge remains the authority.
        route = "cross-layer-offset";
        const side = edgeIndex % 2 === 0 ? 1 : -1;
        const targetLayerRect = toLayer?.getBoundingClientRect();
        const targetRailX = targetLayerRect
          ? (side > 0 ? targetLayerRect.right : targetLayerRect.left) - boardRect.left + side * 5
          : (side > 0 ? boardRect.width - 6 : 6);
        const startX = fromCenterX;
        const startY = fromRect.top - boardRect.top;
        const endX = toCenterX;
        const endY = toRect.top - boardRect.top;
        const railY = Math.max(8, Math.min(startY, endY) - 10);
        const sourceBend = Math.max(12, (startY - railY) * 0.45);
        path = `M ${startX} ${startY} C ${startX} ${startY - sourceBend}, ${startX} ${railY}, ${startX} ${railY} L ${targetRailX} ${railY} L ${targetRailX} ${endY} C ${targetRailX} ${endY}, ${endX} ${endY}, ${endX} ${endY}`;
      } else if (sameLayer && Math.abs(toCenterY - fromCenterY) > 8) {
        // A layer can contain a root CR plus multiple upstream/downstream CRs. A left/right route between
        // their cards is hidden by the cards themselves, so same-layer edges use the truthful direction of
        // the stacked nodes: bottom-to-top when travelling down, top-to-bottom when travelling up. If a
        // branch skips a card, take a presentation-only rail just outside the layer so the whole edge stays
        // visible without ever placing it above controlled artifact text.
        const down = toCenterY > fromCenterY;
        const startY = (down ? fromRect.bottom : fromRect.top) - boardRect.top;
        const endY = (down ? toRect.top : toRect.bottom) - boardRect.top;
        const intermediate = fromLayer && [...fromLayer.querySelectorAll<HTMLElement>(".crGraphNode")].some(candidate => {
          if (candidate === fromElement || candidate === toElement) return false;
          const rect = candidate.getBoundingClientRect();
          return rect.top > Math.min(fromRect.top, toRect.top) + 1 && rect.bottom < Math.max(fromRect.bottom, toRect.bottom) - 1;
        });
        if (intermediate && fromLayer) {
          route = "same-layer-rail";
          const layerRect = fromLayer.getBoundingClientRect();
          // Keep rails inside the board's padding. Each side receives its own offset sequence so a fan-out
          // cannot collapse into one unreadable stroke while preserving source/target direction.
          const side = edgeIndex % 2 === 0 ? 1 : -1;
          const ordinals = railOrdinals.get(fromLayer) ?? [0, 0];
          const ordinal = side > 0 ? ordinals[0]++ : ordinals[1]++;
          railOrdinals.set(fromLayer, ordinals);
          const railX = (side > 0 ? layerRect.right : layerRect.left) - boardRect.left + side * (5 + ordinal * 7);
          const startX = fromCenterX;
          const endX = toCenterX;
          const sourceBend = startX + (railX - startX) * 0.55;
          const targetBend = railX + (endX - railX) * 0.55;
          path = `M ${startX} ${startY} C ${sourceBend} ${startY}, ${railX} ${startY}, ${railX} ${startY} L ${railX} ${endY} C ${railX} ${endY}, ${targetBend} ${endY}, ${endX} ${endY}`;
        } else {
          route = "same-layer-vertical";
          const distance = Math.max(18, Math.abs(endY - startY) * 0.35);
          const startX = fromCenterX;
          const endX = toCenterX;
          path = `M ${startX} ${startY} C ${startX} ${startY + distance * (down ? 1 : -1)}, ${endX} ${endY - distance * (down ? 1 : -1)}, ${endX} ${endY}`;
        }
      } else {
        const startX = fromRect.right - boardRect.left;
        const startY = fromCenterY;
        const endX = toRect.left - boardRect.left;
        const endY = toCenterY;
        const distance = Math.max(24, Math.abs(endX - startX) * 0.45);
        const direction = endX >= startX ? 1 : -1;
        route = sameLayer ? "same-layer-horizontal" : "cross-layer";
        path = `M ${startX} ${startY} C ${startX + distance * direction} ${startY}, ${endX - distance * direction} ${endY}, ${endX} ${endY}`;
      }
      next.push({
        key: `${edge.fromKind}:${edge.fromId}:${edge.toKind}:${edge.toId}:${edge.relation}`,
        path,
        selected: edge.fromId === selectedId || edge.toId === selectedId,
        relation: traceRelationLabel(edge.relation),
        route,
      });
    }
    setConnectors(next);
  }, [nodeByKey, selectedId, trace.edges]);

  useLayoutEffect(() => {
    const frame = requestAnimationFrame(measure);
    const board = boardRef.current;
    const observer = typeof ResizeObserver === "undefined" || !board ? undefined : new ResizeObserver(measure);
    if (observer && board) observer.observe(board);
    window.addEventListener("resize", measure);
    return () => {
      cancelAnimationFrame(frame);
      observer?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, [measure]);

  return <>
    <div className="crGraphBoard" ref={boardRef} role="group" aria-label="Connected Digital Thread map. At narrow widths, scroll horizontally to inspect every connected card and arrow." tabIndex={0}
      data-representable-edge-count={representableEdgeCount}
      data-rendered-connector-count={connectors.length}
      data-unrepresentable-edge-count={trace.edges.length - representableEdgeCount}>
      <svg className="crGraphConnectors" aria-hidden="true">
        <defs>
          <marker id="crGraphArrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto" markerUnits="strokeWidth">
            <path d="M 0 0 L 8 4 L 0 8 z" />
          </marker>
        </defs>
        {connectors.map(connector => <path className={`crGraphConnector${connector.selected ? " selected" : ""}`} key={connector.key} d={connector.path} data-route={connector.route} data-edge-key={connector.key} markerEnd="url(#crGraphArrow)"><title>{connector.relation}</title></path>)}
      </svg>
      <div className="crGraphBoardLayers" role="list" aria-label="Connected Digital Thread nodes">
        {layers.map(layer => <section className={`crGraphLayer crGraphLayer-${layer.id}`} key={layer.id} aria-label={layer.title}>
          <h4>{layer.title}</h4>
          <div className="crGraphNodeList">
            {layer.nodes.map(node => {
              const key = `${node.kind}:${node.id}`;
              const selected = node.id === selectedId;
              return <article
                className={`crGraphNode${selected ? " selected" : ""}${node.kind === "RequirementRevision" && !node.baselineMembershipIds?.length ? " proposed" : ""}`}
                role="listitem"
                key={key}
                ref={element => { if (element) nodeRefs.current.set(key, element); else nodeRefs.current.delete(key); }}
              >
                <button type="button" className="crGraphNodeFocus" aria-pressed={selected} aria-label={`Focus ${node.displayNumber}`} onClick={() => onSelect(node.id)}>
                  <small>{traceKindLabel(node.kind)}</small>
                  <span>{node.title || "Exact connected artifact"}</span>
                  {node.state && <em>{stateLabel(node.state)}</em>}
                </button>
                <ExactArtifactLink href={nodeHref(node)}>{node.displayNumber}</ExactArtifactLink>
                {(node.buildVersion || node.revision !== null && node.revision !== undefined) && <small className="crGraphNodeMeta">{node.revision !== null && node.revision !== undefined ? `Revision ${String(node.revision).padStart(2, "0")}` : ""}{node.buildVersion ? ` · Build ${node.buildVersion}` : ""}</small>}
              </article>;
            })}
            {!layer.nodes.length && <p className="crUnavailable">No connected records in this layer.</p>}
          </div>
        </section>)}
      </div>
      <p className="crGraphPanHint">Scroll horizontally to inspect every connected card and arrow.</p>
    </div>
    <div className="crGraphEdges" role="list" aria-label="Connected Digital Thread relationships">
      {trace.edges.length ? trace.edges.map(edge => {
        const from = nodeByKey.get(`${edge.fromKind}:${edge.fromId}`), to = nodeByKey.get(`${edge.toKind}:${edge.toId}`);
        return <article className="crGraphEdge" key={`${edge.fromKind}-${edge.fromId}-${edge.toKind}-${edge.toId}-${edge.relation}`} role="listitem">
          <ExactArtifactLink href={from ? nodeHref(from) : undefined}>{from?.displayNumber ?? "Exact source"}</ExactArtifactLink>
          <b aria-hidden="true">→</b>
          <ExactArtifactLink href={to ? nodeHref(to) : undefined}>{to?.displayNumber ?? "Exact target"}</ExactArtifactLink>
          <span>{traceRelationLabel(edge.relation)}</span>
          <div>{edge.provenance.map(fact => <em key={`${fact.kind}-${fact.sourceId ?? "none"}`}>{traceProvenanceLabel(fact.kind)}{fact.isLive === false ? " · Historical evidence" : ""}</em>)}</div>
        </article>;
      }) : <p className="crUnavailable">No connected edges are present in the server projection.</p>}
    </div>
  </>;
}

export default function LifecycleExplorer({ api, projectId, activeReleaseId, releases, initialArtifactId, initialArtifactKind, requirementHref, traceArtifactHref, onBack }: Props) {
  const isChangeRequestRoute = initialArtifactKind === "change-request" && Boolean(initialArtifactId);
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
  const [changeRequest, setChangeRequest] = useState<ChangeRequestDetail>();
  const [changeRequestTrace, setChangeRequestTrace] = useState<ChangeRequestTrace>();
  const [changeRequestBaselineId, setChangeRequestBaselineId] = useState<string>();
  const [changeRequestPathError, setChangeRequestPathError] = useState<string>();
  const completePathNodeHref = (node: CompletePath["nodes"][number]) => traceArtifactHref?.({
    id: node.revisionId,
    kind: "RequirementRevision",
    displayNumber: node.displayNumber,
    level: node.level,
    artifactId: node.id,
  });
  const completePathArtifactHref = (item: NonNullable<CompletePath["artifact"]>) => traceArtifactHref?.({
    id: item.id,
    kind: item.artifactKind === "Procedure" ? "TestProcedure" : "TestCase",
    revisionId: item.revisionId,
    displayNumber: item.displayNumber,
    level: item.level,
  });

  const load = useCallback(async () => {
    setLoading(true);
    try {
      if (isChangeRequestRoute && initialArtifactId) {
        setChangeRequest(undefined); setChangeRequestTrace(undefined); setChangeRequestBaselineId(undefined);
        setCompletePath(undefined); setChangeRequestPathError(undefined);
        const [detailResponse, traceResponse, contextResponse] = await Promise.all([
          fetch(`${api}/api/change-requests/${initialArtifactId}`),
          fetch(`${api}/api/change-requests/${initialArtifactId}/trace`),
          fetch(`${api}/api/build-context?projectId=${projectId}&releaseId=${activeReleaseId}`),
        ]);
        if (!detailResponse.ok || !traceResponse.ok || !contextResponse.ok)
          throw new Error("This change request is unavailable in the selected Project or build.");
        const detail = await detailResponse.json() as ChangeRequestDetail;
        const trace = await traceResponse.json() as ChangeRequestTrace;
        const buildContext = await contextResponse.json() as { effectiveBaselineId?: string };
        if (detail.id !== initialArtifactId || detail.projectId !== projectId || detail.targetReleaseId !== activeReleaseId
          || trace.projectId !== projectId || trace.rootArtifactId !== initialArtifactId
          || trace.rootArtifactKind !== "ChangeRequest")
          throw new Error("This change request is unavailable in the selected Project or build.");
        const materialized = trace.nodes.find(node => node.kind === "RequirementRevision"
          && node.baselineMembershipIds?.includes(buildContext.effectiveBaselineId ?? "") && node.artifactId
          && trace.edges.some(edge => edge.fromId === initialArtifactId && edge.toId === node.id
            && edge.relation === "OwnsRequirementRevision"));
        let exactPath: CompletePath | undefined;
        let exactPathError: string | undefined;
        if (materialized && buildContext.effectiveBaselineId) {
          const pathResponse = await fetch(`${api}/api/traceability/path?projectId=${projectId}&baselineId=${buildContext.effectiveBaselineId}&requirementRevisionId=${materialized.id}`);
          if (!pathResponse.ok) exactPathError = "The exact baseline requirement path is unavailable. Retry to verify the controlled path.";
          else {
            const candidatePath = await pathResponse.json() as CompletePath;
            if (candidatePath.baselineId !== buildContext.effectiveBaselineId
              || candidatePath.focusRevisionId !== materialized.id)
              exactPathError = "The exact baseline requirement path did not match the selected revision. Retry to verify the controlled path.";
            else exactPath = candidatePath;
          }
        }
        setChangeRequest(detail);
        setChangeRequestTrace(trace);
        setChangeRequestBaselineId(buildContext.effectiveBaselineId);
        setCompletePath(exactPath);
        setChangeRequestPathError(exactPathError);
        setBaselines([]); setDocuments([]); setTraces([]); setFocusId("");
        setError("");
        return;
      }
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
  }, [api, projectId, releases, activeReleaseId, baselineId, query, initialArtifactId, isChangeRequestRoute]);

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
    if (!initialArtifactId || isChangeRequestRoute) return;
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
  }, [api, initialArtifactId, isChangeRequestRoute]);

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
  const artifactNoun = verificationArtifactNoun(focus?.level ?? completePath?.artifact?.level ?? completePath?.procedure?.level);
  const executions = useMemo(() => confirmedTests.flatMap((test) => test.executions), [confirmedTests]);
  const evidence = useMemo(() => executions.flatMap((execution) => execution.evidence), [executions]);
  const threadPercent = focus ? Math.round(([true, confirmedTests.length > 0, executions.length > 0, evidence.length > 0].filter(Boolean).length / 4) * 100) : 0;
  const answer = !focus
    ? "Select a requirement to inspect its digital thread."
    : !confirmedTests.length
      ? focus.tests.some((test) => test.isSuspect)
        ? `Not yet — ${artifactNoun.toLowerCase()} applicability is suspect after the requirement changed and does not count as confirmed coverage.`
        : `Not yet — no verification ${artifactNoun.toLowerCase()} is linked to this controlled revision.`
      : !executions.length
        ? `Not yet — linked ${artifactNoun.toLowerCase()}s are awaiting an authoritative execution result.`
        : !evidence.length
          ? "Partially — an execution result exists, but no immutable evidence file is attached."
          : `Yes — ${confirmedTests.length} confirmed ${artifactNoun.toLowerCase()}${confirmedTests.length === 1 ? "" : "s"}, ${executions.length} execution${executions.length === 1 ? "" : "s"}, and ${evidence.length} evidence file${evidence.length === 1 ? "" : "s"} support this claim.`;

  return (
    <main className="lifecyclePage">
      <header>
        <div>
          <button className="back" onClick={onBack}>← Command Center</button>
          <p className="eyebrow">ASSURANCE / DIGITAL THREAD FOCUS</p>
          <h1>{isChangeRequestRoute ? "Digital Thread · Change Request" : "Digital Thread"}</h1>
          <p>Answer one engineering question across requirement derivation, verification, immutable evidence, and release configuration.</p>
        </div>
      </header>
      {error && <div className="workspaceError" role="alert">{error}<button onClick={load}>Retry</button></div>}
      {loading && !baselines.length && <section className="traceEmpty"><b>Loading exact configuration…</b><p>Resolving the active release baseline and its evidence network.</p></section>}
      <div className="lifeTabs">
        <button className={tab === "thread" ? "active" : ""} onClick={() => setTab("thread")}>Digital Thread</button>
      </div>

      {tab === "thread" ? isChangeRequestRoute ? (
        changeRequest && changeRequestTrace
          ? <ChangeRequestThread detail={changeRequest} trace={changeRequestTrace} activeBaselineId={changeRequestBaselineId} exactPath={completePath} exactPathError={changeRequestPathError} onRetry={load} requirementHref={requirementHref} traceArtifactHref={traceArtifactHref} />
          : <section className="traceEmpty"><b>Change request thread unavailable</b><p>The selected controlled record could not be resolved in this Project and build.</p></section>
      ) : <>
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
                <article className={node.revisionId === focus.revisionId ? "selected" : ""}>
                  <small>{node.level === "System" ? "SYSTEM REQUIREMENT" : node.level === "HighLevel" ? "HLR" : "LLR"}</small>
                  <ExactArtifactLink className="completeThreadExactIdentifier" href={completePathNodeHref(node)}>{node.displayNumber}</ExactArtifactLink>
                  <button type="button" className="completeThreadFocus" onClick={() => { setQuery(node.displayNumber.replace(/\.\d{2}$/, "")); setFocusId(node.revisionId); }}>Focus exact requirement</button>
                  <span>{node.statement}</span>
                </article>
              </div>)}
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.artifact ? "missing" : ""}><small>TEST {artifactNoun.toUpperCase()}</small>{completePath.artifact ? <ExactArtifactLink className="completeThreadExactIdentifier completeThreadTestArtifact" href={completePathArtifactHref(completePath.artifact)}>{completePath.artifact.displayNumber}</ExactArtifactLink> : <b>Not linked</b>}<span>{completePath.artifact?.title ?? `${artifactNoun} linkage required`}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.execution ? "missing" : ""}><small>TEST RESULT</small><b>{completePath.execution?.outcome ?? "Not executed"}</b><span>{completePath.execution?.determination ?? "Authoritative result required"}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.execution?.evidence.length ? "missing" : ""}><small>TEST EVIDENCE</small><b>{completePath.execution?.evidence[0]?.originalFileName ?? "Not attached"}</b><span>{completePath.execution?.evidence[0]?.sha256 ?? (completePath.execution?.evidenceReference ? `External reference only: ${completePath.execution.evidenceReference}` : "Checksummed evidence required")}</span></article></div>
              <div className="completeThreadStep"><i className="threadConnector" aria-hidden="true">›</i><article className={!completePath.build ? "missing" : ""}><small>BUILD</small><b>{completePath.build?.buildNumber ?? completePath.baseline.displayNumber}</b><span>{completePath.build ? `${completePath.build.state} · ${completePath.baseline.displayNumber}` : completePath.baseline.name}</span></article></div>
            </div> : <div className="traceEmpty"><b>Resolving complete path…</b><p>Following exact requirement revisions into verification evidence and build configuration.</p></div>}
            <footer><b>{completePath ? `${completePath.nodes.length} requirement levels in this path` : "Resolving connected records"}</b><span>SYSR → HLR → LLR → {artifactNoun.toLowerCase()} → result → evidence → build</span></footer>
          </div>
          <aside className="threadInspector">
            <div className="threadFocusMark"><i>{focus.level.slice(0, 2).toUpperCase()}</i><span>{focus.level}</span></div>
            <p className="eyebrow">SELECTED CONTROLLED RECORD</p>
            <h2>{focus.displayNumber}</h2>
            <p>{focus.statement}</p>
            <div className="threadCompleteness"><div><b>Thread completeness</b><strong>{threadPercent}%</strong></div><span><i style={{ width: `${threadPercent}%` }} /></span></div>
            <section className="threadAnswer"><small>THE QUESTION THIS ANSWERS</small><h3>Can this baseline claim verification evidence for the selected requirement?</h3><p>{answer}</p></section>
            <div className="threadRelations"><div><span>Parents</span><b>{focus.parents.length}</b></div><div><span>Children</span><b>{focus.children.length}</b></div><div><span>Confirmed {artifactNoun.toLowerCase()}s</span><b>{confirmedTests.length}</b></div><div><span>Evidence</span><b>{evidence.length}</b></div></div>
            {!!focus.children.length && <section className="threadDownstream"><small>DOWNSTREAM</small>{focus.children.slice(0, 4).map((child) => <button key={child.id} onClick={() => traverse(child)}><span>{child.displayNumber}</span><b>Open path →</b></button>)}</section>}
          </aside>
        </section> : <section className="traceEmpty"><b>No matching digital thread</b><p>Broaden the identifier search or choose another controlled baseline.</p></section> :
        <section className="traceList">{traces.map((item) => <article key={item.revisionId} className={item.revisionId === focusId ? "focused" : ""}>
          <div className="traceIdentity"><button onClick={() => { setFocusId(item.revisionId); setThreadMode("map"); }}>{item.displayNumber}</button><i>{item.level}</i><span>{item.testCount} confirmed{item.suspectTestCount ? ` · ${item.suspectTestCount} suspect` : ""}</span></div><p>{item.statement}</p>
          <details className="traceDetails" open={query.trim() !== "" && traces.length === 1 ? true : undefined}><summary><span>Explore relationships and evidence</span><small>{item.parents.length} parent{item.parents.length === 1 ? "" : "s"} · {item.children.length} child{item.children.length === 1 ? "" : "ren"} · {item.tests.length} {verificationArtifactNoun(item.level).toLowerCase()}{item.tests.length === 1 ? "" : "s"}</small></summary>
            <div className="traceRelations"><div><small>PARENT / DERIVED FROM</small>{item.parents.map((parent) => <div key={parent.linkId}><button onClick={() => traverse(parent)}>{parent.displayNumber} · {parent.level}{parent.lifecycle && <i className={`traceLinkState ${parent.lifecycle.state.toLowerCase()}`}>{parent.lifecycle.state}</i>}</button>{parent.lifecycle && <ExactLinkLifecyclePanel api={api} routeRoot="trace-links" linkId={parent.linkId} initialLifecycle={{ ...parent.lifecycle, linkId: parent.linkId }} onChanged={load} />}</div>)}{!item.parents.length && <em>Top-level requirement</em>}</div><div><small>CHILDREN / SATISFIED BY</small>{item.children.slice(0, 8).map((child) => <div key={child.linkId}><button onClick={() => traverse(child)}>{child.displayNumber} · {child.level}{child.lifecycle && <i className={`traceLinkState ${child.lifecycle.state.toLowerCase()}`}>{child.lifecycle.state}</i>}</button>{child.lifecycle && <ExactLinkLifecyclePanel api={api} routeRoot="trace-links" linkId={child.linkId} initialLifecycle={{ ...child.lifecycle, linkId: child.linkId }} onChanged={load} />}</div>)}{item.children.length > 8 && <em>+ {item.children.length - 8} additional children</em>}{!item.children.length && <em>Leaf-level requirement</em>}</div></div>
            {item.tests.length > 0 && <div className="traceVerification"><small>VERIFICATION / RESULTS / EVIDENCE</small>{item.tests.map((test) => <section className={test.isSuspect ? "suspect" : ""} key={test.artifactRevisionId}><div><b>{test.displayNumber}</b><span>{test.title} · {test.isSuspect ? "Suspect applicability — not coverage" : "Confirmed applicability"}</span></div>{!test.isSuspect && test.executions.map((run) => <article key={run.id}><i className={run.outcome.toLowerCase()}>{run.outcome}</i><p>{run.determination}</p><small>{run.executedBy} · {new Date(run.executedAt).toLocaleString()}</small>{run.evidence.map((file) => <a key={file.id} href={`${api}/api/evidence/${file.id}`}><b>{file.originalFileName}</b><code>{file.sha256}</code></a>)}</article>)}{test.isSuspect ? <em>Resolve this applicability in Verification change impact.</em> : !test.executions.length && <em>Approved {verificationArtifactNoun(test.level).toLowerCase()} awaiting execution</em>}</section>)}</div>}
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
