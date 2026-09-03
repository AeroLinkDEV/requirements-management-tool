import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import DigitalThreadNetwork from "./DigitalThreadNetwork"
import DigitalThreadInsideChange from "./DigitalThreadInsideChange"
import DigitalThreadArtifact from "./DigitalThreadArtifact"
import ExactArtifactLink from "./ExactArtifactLink"
import ExactLinkLifecyclePanel, { type ExactLinkLifecycle } from "./ExactLinkLifecyclePanel"
import { stateLabel } from "./presentation"
import { traceRelationLabel } from "./tracePresentation"
import type { ExactTraceArtifact, ThreadView } from "./routing"
import type { NetworkNode, NetworkProjection } from "./changeNetworkPresentation"
import type { ProposalContent } from "./changeProposalPresentation"
import { artifactThreadUrl, type ArtifactThreadFocalKind, type ArtifactThreadNode } from "./artifactThreadContract"
import "./DigitalThreadPage.css"

/**
 * The Digital Thread page (#880 §4).
 *
 * This replaces the shipped page outright, as the product owner directed: the fixed lifecycle-path strip, the
 * stacked layer boxes and the separate selected-node block are gone, and the three canvas views of §5 are the
 * page. What is replaced is the *presentation*. The server projections, exact-identity rules, provenance,
 * baseline truth and authorization beneath it are reused unchanged — per #866 decision 23 there is one graph
 * and one trace definition, and none of it is rebuilt here.
 *
 * Two capabilities from the old page survive because they are functions rather than chrome (§4.5): the
 * evidence table, which is the list alternative WCAG 2.2 and `DESIGN_VISION_AND_DASHBOARDS.md` require beside
 * any graph view, and the Trace PDF / DOCX exports that certification reviewers work from. Both are here, as
 * compact toolbar controls rather than the two large buttons they used to be.
 *
 * The page owns no header of its own. #880 §4.2 reclaims roughly 210px of vertical space: the back link, the
 * eyebrow, the H1, the description sentence and a tab strip with exactly one tab are all gone, and the canvas
 * begins directly beneath the application shell's breadcrumb — which already carries the build context, the
 * `In work` state and Copy link, and is kept.
 */

/**
 * How a focal artifact is named in the route.
 *
 * `requirement` carries no URL segment — `/traceability/{id}` has always meant one — but it is still a kind a
 * caller can hand over, because the Requirements Explorer's `Open Digital Thread` navigates with that word.
 */
export type ThreadFocalKind =
  | "requirement" | "change-request" | "case" | "procedure" | "execution" | "build" | undefined

export type DigitalThreadPageProps = {
  api: string
  projectId: string
  releaseId: string
  buildLabel?: string
  /** The focal artifact the address names, if any. */
  focalId?: string
  focalKind?: ThreadFocalKind
  /** The view the address names. Absent means "whatever the focal artifact implies". */
  view?: ThreadView
  /**
   * Publish a new address.
   *
   * The page never holds focal or view in component state alone: #880 §6.4 requires both to survive refresh,
   * back, forward and Copy link, and component memory survives none of those. Every change of view or focal
   * goes through the router and comes back as props.
   */
  onRoute: (next: { view: ThreadView; focalId?: string; focalKind?: ThreadFocalKind }) => void
  orderedLevels?: readonly string[]
  /** Exact route for a controlled record, when this workspace can open it. Absent renders non-openable. */
  hrefFor?: (record: { id: string; displayNumber: string }) => string | undefined
  /**
   * Exact route for a *related* record, through the shared exact-artifact helper.
   *
   * A relation is only openable when both its aggregate id and its exact revision are known: opening it by
   * aggregate alone would resolve whatever revision is current today rather than the one this baseline
   * records, which is the resolution a traceability surface must never make on the reader's behalf.
   */
  traceArtifactHref?: (node: ExactTraceArtifact) => string | undefined
}

/** The exact requirement route for a relation, or nothing when its exact identity is incomplete. */
const relationHref = (
  traceArtifactHref: ((node: ExactTraceArtifact) => string | undefined) | undefined,
  item: { id: string; revisionId?: string; artifactId?: string; displayNumber: string; level: string },
) => {
  const revisionId = item.revisionId ?? item.id
  const artifactId = item.artifactId ?? (item.revisionId ? undefined : item.id)
  return artifactId
    ? traceArtifactHref?.({ id: revisionId, kind: "RequirementRevision", artifactId, displayNumber: item.displayNumber, level: item.level })
    : undefined
}

/** Which view an address resolves to when it does not name one. */
export const viewForFocal = (focalKind: ThreadFocalKind, focalId?: string): ThreadView =>
  // A change request lands on the network, not inside itself: #880 §4.4 wants the change seen in the context
  // of everything around it, with `Open this change` one click away.
  focalKind === "change-request" ? "network" : focalId ? "artifact" : "network"

/**
 * The route's focal-kind vocabulary mapped to the artifact-thread contract's.
 *
 * `requirement` is spelled out rather than left to the default. The Requirements Explorer's own
 * `Open Digital Thread` has always navigated with that word, so an entry that predates #880 arrives carrying
 * it; treating only an absent kind as a requirement refused the thread for the one affordance §4.4 names
 * first. The URL still carries no segment for it, so the address is unchanged either way.
 */
const ARTIFACT_FOCAL_KIND: Record<string, ArtifactThreadFocalKind> = {
  requirement: "Requirement",
  case: "Case",
  procedure: "Procedure",
  execution: "Execution",
  build: "Build",
}

type Baseline = { id: string; displayNumber: string; name: string; requirementsMaterializedAt?: string }

/** What the build states about its own controlled configuration, including one it inherits. */
type BuildContext = {
  effectiveBaselineId?: string
  inheritedBaseline?: boolean
  effectiveBaseline?: {
    id: string; baseNumber: string; revision: number; name: string; releaseId: string; releaseVersion: string
  }
}
// `linkId` keys the row: one artifact can be related twice under different relation types, so the artifact id
// alone is not unique among a requirement's parents or children.
type TraceRelation = {
  id: string; linkId: string; displayNumber: string; level: string; type: string
  revisionId?: string; artifactId?: string
  /** Server-stated. Suspectness and its acknowledgement are never derived here. */
  lifecycle?: ExactLinkLifecycle
}
type TraceEvidence = { id: string; originalFileName: string; sha256: string }
type TraceExecution = {
  id: string; outcome: string; executedBy: string; executedAt: string; evidence: TraceEvidence[]
  /**
   * An external evidence locator recorded instead of an attached file.
   *
   * Kept distinct from an attached, checksummed file on purpose: "evidence exists somewhere else" and
   * "evidence is held here and hashed" are different claims, and a certification reader must be able to see
   * which one a result actually makes.
   */
  evidenceReference?: string | null
}
type TraceTest = { artifactRevisionId: string; displayNumber: string; title: string; isSuspect: boolean; executions: TraceExecution[] }
type TraceRow = {
  id: string
  revisionId: string
  displayNumber: string
  level: string
  statement: string
  parents: TraceRelation[]
  children: TraceRelation[]
  tests: TraceTest[]
}

export default function DigitalThreadPage({
  api,
  projectId,
  releaseId,
  buildLabel,
  focalId,
  focalKind,
  view,
  onRoute,
  orderedLevels,
  hrefFor,
  traceArtifactHref,
}: DigitalThreadPageProps) {
  const active: ThreadView = view ?? viewForFocal(focalKind, focalId)

  /**
   * Map or table.
   *
   * Component state, deliberately, and the one piece that is: it is a presentation preference about the same
   * records rather than a different address, and #880 §4.5 asks for a representation toggle rather than a
   * second page. The focal artifact and the view — the two things §6.4 names — are both in the URL.
   */
  const [representation, setRepresentation] = useState<"map" | "table">("map")
  const [exportOpen, setExportOpen] = useState(false)

  const [baselineId, setBaselineId] = useState("")
  const [baselines, setBaselines] = useState<Baseline[]>([])
  const [network, setNetwork] = useState<NetworkProjection | null>(null)
  const [proposal, setProposal] = useState<ProposalContent | null>(null)
  const [thread, setThread] = useState<unknown>(null)
  const [rows, setRows] = useState<TraceRow[]>([])
  const [rowTotal, setRowTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const exportRef = useRef<HTMLDetailsElement | null>(null)

  const retry = useCallback(() => setAttempt(value => value + 1), [])

  /** The build's change network, plus the configuration context every other read is scoped by. */
  useEffect(() => {
    let cancelled = false
    const run = async () => {
      setLoading(true)
      setError(null)
      try {
        const [contextResponse, networkResponse, baselineResponse] = await Promise.all([
          fetch(`${api}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`),
          fetch(`${api}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`),
          fetch(`${api}/api/baselines?projectId=${projectId}&releaseId=${releaseId}`),
        ])
        if (!contextResponse.ok) throw new Error("The active build context could not be loaded.")
        if (!networkResponse.ok) throw new Error("The change network for this build could not be loaded.")
        // Stated rather than swallowed. An empty baseline list and a failed baseline read look identical on
        // screen, and the difference decides whether the reader is looking at a build with no controlled
        // configuration yet or at a page that quietly lost one (#880 §6.8).
        if (!baselineResponse.ok)
          throw new Error(`The controlled baselines for this build could not be loaded (${baselineResponse.status}).`)
        const context = await contextResponse.json() as BuildContext
        const projection = await networkResponse.json() as NetworkProjection
        const own = (await baselineResponse.json()) as Baseline[]
        if (cancelled) return

        /**
         * The controlled configurations this build can be read against.
         *
         * Two sources, and both are needed. A release's own candidate baselines are only readable once their
         * requirements are materialized — an unmaterialized candidate has no content to trace. And a build in
         * work usually has no materialized baseline of its own at all: it **inherits** the one from its
         * released predecessor, which `/api/baselines` for this release never returns because the baseline
         * belongs to the other one. Reading only this release's list is what left the page with nothing to
         * scope to, and the table, the exports and the artifact thread all silently empty.
         */
        const inherited = context.inheritedBaseline && context.effectiveBaseline
          ? [{
              id: context.effectiveBaseline.id,
              displayNumber:
                `${context.effectiveBaseline.baseNumber}.${String(context.effectiveBaseline.revision).padStart(2, "0")}`,
              name: `${context.effectiveBaseline.name} · inherited from Build ${context.effectiveBaseline.releaseVersion}`,
            }]
          : []
        const materialized = own.filter(item => item.requirementsMaterializedAt)
        const list = [
          ...materialized,
          ...inherited.filter(item => !materialized.some(candidate => candidate.id === item.id)),
        ]

        setNetwork(projection)
        setBaselines(list)
        // The build's own effective baseline first. Falling back to the first readable one only when the
        // build states none, and the selector always names which is in view — nothing is silently resolved.
        setBaselineId(context.effectiveBaselineId ?? list[0]?.id ?? "")
        setError(null)
      } catch (failure) {
        if (!cancelled) setError(failure instanceof Error ? failure.message : "The Digital Thread could not be loaded.")
      } finally {
        if (!cancelled) setLoading(false)
      }
    }
    void run()
    return () => { cancelled = true }
  }, [api, attempt, projectId, releaseId])

  /** The opened change's proposal content, for the inside-a-change lanes. */
  useEffect(() => {
    if (active !== "inside" || !focalId) { setProposal(null); return undefined }
    let cancelled = false
    const run = async () => {
      try {
        const response = await fetch(`${api}/api/change-requests/${focalId}/proposal-content`)
        if (!response.ok) throw new Error("The proposed content for this change could not be loaded.")
        const content = await response.json() as ProposalContent
        if (!cancelled) setProposal(content)
      } catch (failure) {
        if (!cancelled) setError(failure instanceof Error ? failure.message : "The change could not be opened.")
      }
    }
    void run()
    return () => { cancelled = true }
  }, [active, api, attempt, focalId])

  /**
   * The exact revision a requirement focal names.
   *
   * The Requirements Explorer's `Open Digital Thread` has always addressed a requirement by its **artifact**
   * id, and every link already in circulation does the same. The artifact-thread read is rooted on an exact
   * **revision**, so the two have to be joined somewhere.
   *
   * Asked of the server against this baseline, rather than scanned out of the evidence table's first page.
   * The table holds 100 rows of a baseline that holds 1,250, so a scan would resolve the requirements that
   * happened to sort early and quietly fail the rest. The read is exact rather than "latest": a baseline
   * materialises exactly one revision of an artifact, so the answer is the revision this configuration
   * carries — not the newest one that happens to exist. An id that is already a revision comes back as
   * itself, so the per-kind addresses introduced by §4.4 are unaffected.
   */
  const [resolvedFocalId, setResolvedFocalId] = useState<string | undefined>(undefined)
  useEffect(() => {
    const isRequirement = !focalKind || focalKind === "requirement"
    if (!focalId || !isRequirement) { setResolvedFocalId(focalId); return undefined }
    if (!baselineId) { setResolvedFocalId(undefined); return undefined }
    let cancelled = false
    const run = async () => {
      try {
        const response = await fetch(`${api}/api/requirements?projectId=${projectId}&baselineId=${baselineId}`
          + `&page=1&pageSize=1&ids=${encodeURIComponent(focalId)}`)
        if (!response.ok) throw new Error(`That requirement could not be resolved in this build (${response.status}).`)
        const body = await response.json() as { items?: { id: string; revisionId: string }[] }
        const match = (body.items ?? []).find(item => item.id === focalId || item.revisionId === focalId)
        if (cancelled) return
        // Unresolved stays unresolved. Falling back to the address as given would send an artifact id to a
        // read rooted on a revision, and the reader would be told the thread is unavailable rather than that
        // this build does not carry the record they named.
        setResolvedFocalId(match?.revisionId)
      } catch (failure) {
        if (!cancelled) { setResolvedFocalId(undefined); setError(failure instanceof Error ? failure.message : "That requirement could not be resolved.") }
      }
    }
    void run()
    return () => { cancelled = true }
  }, [api, attempt, baselineId, focalId, focalKind, projectId])

  /**
   * The artifact thread for the focal record.
   *
   * The raw body is handed to the view, which parses it through the slice-5B0 contract seam. Parsing here
   * would put a second, unvalidated reading of the wire between the server and the canvas.
   */
  useEffect(() => {
    if (active !== "artifact" || !resolvedFocalId || !baselineId) { setThread(null); return undefined }
    let cancelled = false
    const run = async () => {
      try {
        const kind = focalKind ? ARTIFACT_FOCAL_KIND[focalKind] : "Requirement"
        if (!kind) throw new Error("That artifact kind cannot open an artifact thread.")
        const response = await fetch(api + artifactThreadUrl({
          projectId, baselineId, focalKind: kind, focalId: resolvedFocalId!,
        }))
        if (!response.ok) throw new Error("This artifact thread is unavailable in the selected Project and build.")
        const body = await response.json() as unknown
        if (!cancelled) setThread(body)
      } catch (failure) {
        if (!cancelled) setError(failure instanceof Error ? failure.message : "The artifact thread could not be loaded.")
      }
    }
    void run()
    return () => { cancelled = true }
  }, [active, api, attempt, baselineId, focalKind, projectId, resolvedFocalId])

  /**
   * The evidence table's rows.
   *
   * Fetched as soon as the baseline is known rather than when the reader switches representation. The table is
   * the accessible alternative to the canvas, not a secondary screen, so it should be ready when it is asked
   * for — and gating the read on the toggle made the fetch depend on the order two pieces of state happened to
   * settle in, which is how it came up empty against a baseline holding 1,250 requirements.
   */
  useEffect(() => {
    if (!baselineId) return undefined
    let cancelled = false
    const run = async () => {
      try {
        const response = await fetch(
          `${api}/api/traceability?projectId=${projectId}&baselineId=${baselineId}&page=1&pageSize=100`)
        if (!response.ok) throw new Error(`The evidence table could not be loaded (${response.status}).`)
        const body = await response.json() as { items?: TraceRow[]; totalCount?: number } | TraceRow[]
        const items = Array.isArray(body) ? body : body.items ?? []
        if (cancelled) return
        setRows(items)
        // The baseline's real size, not the page size. Reporting the rows in hand as the total would state
        // that a 1,250-requirement baseline holds 100 — the kind of quiet understatement a traceability
        // surface must never make about what it is showing.
        setRowTotal(Array.isArray(body) ? items.length : body.totalCount ?? items.length)
      } catch (failure) {
        if (!cancelled) setError(failure instanceof Error ? failure.message : "The evidence table could not be loaded.")
      }
    }
    void run()
    return () => { cancelled = true }
  }, [api, attempt, baselineId, projectId])

  useEffect(() => {
    if (!exportOpen) return undefined
    const close = (event: MouseEvent) => {
      if (!exportRef.current?.contains(event.target as Node)) setExportOpen(false)
    }
    document.addEventListener("mousedown", close)
    return () => document.removeEventListener("mousedown", close)
  }, [exportOpen])

  const register = useMemo(
    () => (network?.nodes ?? []).filter(node => node.kind !== "ProblemReport"),
    [network],
  )
  const opened = useMemo(
    () => (focalId ? register.find(node => node.id === focalId) ?? null : null),
    [focalId, register],
  )

  const go = useCallback(
    (next: ThreadView, id?: string, kind?: ThreadFocalKind) => onRoute({ view: next, focalId: id, focalKind: kind }),
    [onRoute],
  )

  /** Opening a change request from any view lands inside it, keeping the address honest about where you are. */
  const openChange = useCallback(
    (node: { id: string }) => go("inside", node.id, "change-request"),
    [go],
  )

  const VIEWS: readonly (readonly [ThreadView, string])[] = [
    ["network", "Change network"],
    ["inside", "Inside a change"],
    ["artifact", "Artifact thread"],
  ]

  return (
    <main className="dtPage" aria-label="Digital Thread">
      {/* The page's own toolbar, directly beneath the shell's breadcrumb. There is no second application
          shell here: the prototype's navy bar stands in for the one AeroLink already draws around this page. */}
      <div className="dtPageToolbar">
        <div className="dtPageViews" role="group" aria-label="Digital Thread view">
          {VIEWS.map(([key, label]) => (
            <button
              type="button"
              key={key}
              aria-pressed={active === key}
              className={active === key ? "is-on" : ""}
              // Inside-a-change is only meaningful with a change open, so it keeps the current focal when one
              // is a change request and is otherwise unavailable rather than opening on nothing.
              disabled={key === "inside" && !(focalKind === "change-request" && focalId)}
              onClick={() => go(key, key === "network" && focalKind !== "change-request" ? undefined : focalId,
                key === "network" && focalKind !== "change-request" ? undefined : focalKind)}
            >
              {label}
            </button>
          ))}
        </div>

        <div className="dtPageTrailing">
          {/* Which controlled configuration everything on this page is read against. The replaced page had
              this and it is a function rather than chrome: without it the reader cannot tell which baseline
              the table, the exports and the artifact thread are scoped to, and a build still in work may have
              no effective baseline for the page to assume. */}
          {baselines.length ? (
            <label className="dtPageBaseline">
              <span className="dtPageVisuallyHidden">Controlled baseline</span>
              <select value={baselineId} onChange={event => setBaselineId(event.target.value)}>
                {baselines.map(item => (
                  <option value={item.id} key={item.id}>{item.displayNumber} · {item.name}</option>
                ))}
              </select>
            </label>
          ) : null}

          <div className="dtPageRepresentation" role="group" aria-label="Representation">
            {/* The list alternative required beside a graph view, not an optional extra (§4.5). */}
            {(["map", "table"] as const).map(mode => (
              <button
                type="button"
                key={mode}
                aria-pressed={representation === mode}
                className={representation === mode ? "is-on" : ""}
                onClick={() => setRepresentation(mode)}
              >
                {mode === "map" ? "Map" : "Table"}
              </button>
            ))}
          </div>

          {/* Grouped behind one compact control rather than the two large buttons the old page spent its
              width on. The behaviour and authorization of the reports themselves are untouched. */}
          <details
            className={`dtPageExport${baselineId ? "" : " is-unavailable"}`}
            ref={exportRef}
            open={exportOpen}
            onToggle={event => setExportOpen((event.currentTarget as HTMLDetailsElement).open)}
          >
            <summary aria-label="Export this trace">Export</summary>
            <div className="dtPageExportMenu">
              {baselineId ? (
                <>
                  <a href={`${api}/api/traceability/${baselineId}/download?format=pdf`}>Trace PDF</a>
                  <a href={`${api}/api/traceability/${baselineId}/download?format=docx`}>Trace DOCX</a>
                </>
              ) : (
                // Rather than two links that would resolve to a report of nothing.
                <p className="dtPageExportEmpty">No controlled baseline is in view to export.</p>
              )}
            </div>
          </details>
        </div>
      </div>

      {representation === "table" ? (
        <EvidenceTable api={api} rows={rows} total={rowTotal} baselines={baselines} baselineId={baselineId}
          hrefFor={hrefFor} traceArtifactHref={traceArtifactHref} error={error} onRetry={retry}
          onRelationChanged={retry} />
      ) : active === "inside" ? (
        <DigitalThreadInsideChange
          opened={opened ?? ({ id: focalId ?? "", kind: "ChangeRequest", displayNumber: "" } as NetworkNode)}
          register={register}
          content={proposal}
          orderedLevels={network?.orderedLevels ?? orderedLevels}
          loading={loading}
          error={error}
          onRetry={retry}
          hrefFor={hrefFor}
          onOpenChange={node => openChange(node)}
          onBackToNetwork={() => go("network")}
        />
      ) : active === "artifact" ? (
        <DigitalThreadArtifact
          response={thread}
          loading={loading || (!!focalId && !thread && !error)}
          error={error}
          onRetry={retry}
          hrefFor={(node: ArtifactThreadNode) =>
            hrefFor?.({ id: node.artifactId ?? node.id, displayNumber: node.displayNumber ?? "" })}
          evidenceHref={file => `${api}/api/evidence/${file.id}`}
          onOpenChange={node => openChange(node)}
        />
      ) : (
        <DigitalThreadNetwork
          projection={network}
          loading={loading}
          error={error}
          onRetry={retry}
          orderedLevels={orderedLevels}
          buildLabel={buildLabel}
          hrefFor={node => hrefFor?.({ id: node.artifactId ?? node.id, displayNumber: node.displayNumber })}
          onOpenChange={node => openChange(node)}
        />
      )}
    </main>
  )
}

/**
 * The evidence table: the same relationships the canvas draws, reachable without entering the canvas at all.
 *
 * A real table with real headers, so it is navigable by row and column and announced as a table. #880 §6.9 and
 * `DESIGN_VISION_AND_DASHBOARDS.md` both require the list alternative to expose the same relationships the
 * graph does, which is why the parent, child and verification columns are here rather than a bare list of
 * identifiers.
 */
function EvidenceTable({
  api,
  rows,
  total,
  baselines,
  baselineId,
  hrefFor,
  traceArtifactHref,
  error,
  onRetry,
  onRelationChanged,
}: {
  api: string
  rows: TraceRow[]
  total: number
  baselines: Baseline[]
  baselineId: string
  hrefFor?: (record: { id: string; displayNumber: string }) => string | undefined
  traceArtifactHref?: (node: ExactTraceArtifact) => string | undefined
  error?: string | null
  onRetry?: () => void
  onRelationChanged?: () => void
}) {
  /**
   * One relationship, with the exact record it names and the shared exact-link lifecycle controls.
   *
   * These controls are not a table decoration: acknowledging a suspect relationship with a rationale is the
   * act that turns a flagged link into an assessed one, and it is the reader of the thread who is placed to
   * do it. The panel is the same component every other exact-link surface uses, so the rationale gate and the
   * recorded event are identical wherever the judgement is made.
   */
  const Relation = ({ item }: { item: TraceRelation }) => (
    <span key={item.linkId}>
      <ExactArtifactLink href={relationHref(traceArtifactHref, item)}>
        {item.displayNumber} · {item.level}
      </ExactArtifactLink>{" "}
      <small>{traceRelationLabel(item.type)}</small>
      {item.lifecycle ? (
        <ExactLinkLifecyclePanel api={api} routeRoot="trace-links" linkId={item.linkId}
          initialLifecycle={{ ...item.lifecycle, linkId: item.linkId }} onChanged={onRelationChanged} />
      ) : null}
    </span>
  )
  const baseline = baselines.find(item => item.id === baselineId)
  return (
    <section className="dtPageTable" aria-label="Digital Thread evidence table">
      <p className="dtPageTableContext">
        {baseline ? `${baseline.displayNumber} · ${baseline.name}` : "Controlled baseline"} ·{" "}
        {total.toLocaleString()} requirement{total === 1 ? "" : "s"}
        {rows.length < total ? ` · showing the first ${rows.length.toLocaleString()}` : ""}
      </p>
      <div className="dtPageTableScroll">
        <table>
          <caption className="dtPageVisuallyHidden">
            Requirements in this baseline with their upstream and downstream relationships, verification
            artifacts, results and evidence
          </caption>
          <thead>
            <tr>
              <th scope="col">Requirement</th>
              <th scope="col">Level</th>
              <th scope="col">Upstream</th>
              <th scope="col">Downstream</th>
              <th scope="col">Verification</th>
              <th scope="col">Result and evidence</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(row => (
              <tr key={row.revisionId}>
                <th scope="row">
                  <ExactArtifactLink href={hrefFor?.({ id: row.id, displayNumber: row.displayNumber })}>
                    {row.displayNumber}
                  </ExactArtifactLink>
                  <span>{row.statement}</span>
                </th>
                <td>{row.level}</td>
                <td>
                  {row.parents.length
                    ? row.parents.map(item => <Relation key={item.linkId} item={item} />)
                    : <em>None recorded</em>}
                </td>
                <td>
                  {row.children.length
                    ? row.children.map(item => <Relation key={item.linkId} item={item} />)
                    : <em>None recorded</em>}
                </td>
                <td>
                  {row.tests.length
                    ? row.tests.map(test => (
                      <span key={test.artifactRevisionId}>
                        {test.displayNumber}
                        {/* Suspect never rides on colour alone here either. */}
                        {test.isSuspect ? <small> · suspect applicability</small> : null}
                      </span>
                    ))
                    : <em>Not linked</em>}
                </td>
                <td>
                  {row.tests.flatMap(test => test.executions).length
                    ? row.tests.flatMap(test => test.executions).map(run => (
                      <span key={run.id}>
                        {stateLabel(run.outcome)} · {run.executedBy}
                        {run.evidence.map(file => (
                          <small key={file.id}> · {file.originalFileName} <code>{file.sha256.slice(0, 12)}</code></small>
                        ))}
                        {/* Not attached is stated, and an external locator is stated as external rather than
                            being allowed to read as attached, checksummed evidence. */}
                        {!run.evidence.length ? (
                          <small> · Not attached{run.evidenceReference ? ` · External reference only: ${run.evidenceReference}` : ""}</small>
                        ) : null}
                      </span>
                    ))
                    : <em>Not executed</em>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {error ? (
        <div className="dtPageTableEmpty" role="alert">
          <b>The evidence table could not be loaded.</b> {error}
          {onRetry ? <button type="button" onClick={onRetry}>Try again</button> : null}
        </div>
      ) : !rows.length ? (
        <p className="dtPageTableEmpty" role="status">
          No requirements are materialized in this baseline yet.
        </p>
      ) : null}
    </section>
  )
}
