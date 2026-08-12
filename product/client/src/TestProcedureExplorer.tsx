import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { procedureTargetsFor, stateLabel } from './presentation'
import DocumentActions from './DocumentActions'
import { loadCoverage, type Coverage } from './verificationCoverage'
import type { TestDiscipline } from './TestResultsWorkspace'
// The requirements explorer's stylesheet, imported rather than copied. Browsing a controlled artifact is the
// same job whichever discipline owns it, so the inspector is literally the same one.
import './RequirementsWorkspace.css'
// Coverage moved here from the test change request page, and its card and row styling came with it rather
// than being restyled to look almost the same.
import './TestingCoverageWorkspace.css'
import './TestProcedureExplorer.css'

type Procedure = {
  id: string
  revisionId: string
  displayNumber: string
  title: string
  titleIsExact?: boolean
  titleIsLegacy?: boolean
  titleNote?: string
  state: string
  requirementCount: number
  ownerId: string
  objective?: string
  preconditions?: string
  steps?: string
  expectedResult?: string
  /// The most recent run's outcome, already projected by the listing endpoint.
  lastOutcome?: string
  lastExecutedAt?: string
}
type Revision = {
  id: string; displayNumber: string; revision: number; title: string
  titleIsExact?: boolean; titleIsLegacy?: boolean; titleNote?: string
  state: string; authorId: string; createdAt: string
  objective: string; preconditions: string; steps: string; expectedResult: string
  sourceTestChangeRequestId?: string; package?: string; provenanceNote?: string
  drivenBy: { changeRequest: string; package: string; subjectDisplayNumber: string; action: string; isLegacy?: boolean }[]
  covers: string[]
}
type History = {
  id: string; baseNumber: string; title: string; titleIsExact?: boolean; titleIsLegacy?: boolean
  titleNote?: string; ownerId: string; createdAt: string; revisions: Revision[]
}
/// A named worklist over this library, owned by whoever saved it and optionally shared.
type SavedView = { id: string; name: string; queryJson: string; columnsJson: string; isShared: boolean; owned: boolean }
/** `views` is optional because the empty-build reply is a different object; read it through `savedViews`. */
type Page = { page: number; pageSize: number; totalCount: number; totalPages: number; views?: SavedView[]; items: Procedure[] }
/// The document a discipline's procedures are written into, and the sections inside it.
type ProcedureDocument = {
  id: string
  documentNumber: string
  title: string
  level: string
  description: string
  procedureCount: number
  sections: { id: string; heading: string; position: number; procedureCount: number }[]
}
type Comment = {
  id: string; body: string; state: string; createdBy: string; createdAt: string; disposition?: string
}
type TraceRequirement = {
  id: string
  revisionId: string
  displayNumber: string
  level: string
  statement: string
  coverageState: 'Confirmed' | 'Suspect'
  isSuspect: boolean
}
type TraceProvenance = {
  changeRequest: string; package: string; subjectDisplayNumber: string; action: string; isLegacy?: boolean
}
type ProcedureTrace = {
  procedureId: string
  baseNumber: string
  title: string
  titleIsExact?: boolean
  titleIsLegacy?: boolean
  titleNote?: string
  level: string
  revisionId: string
  displayNumber: string
  revision: number
  state: string
  authorId: string
  createdAt: string
  sourceTestChangeRequestId?: string
  package?: string
  provenanceNote?: string
  requirements: TraceRequirement[]
  provenance: TraceProvenance[]
  build?: { releaseId: string; effectiveBaselineId: string; requirementBaselineId?: string; isExactManifest: boolean }
}

type Tab = 'details' | 'trace' | 'history' | 'discussion'
/**
 * The two questions this page answers, kept apart.
 *
 * "Which procedures does this build carry, and what happened to each" is browsing an inventory. "Is this
 * build covered, and what is not" is a report about requirements. Both are about procedures as they stand,
 * which is why they are on one page — but they are different questions and a reader is asking one of them.
 */
type PageTab = 'procedures' | 'coverage'

/**
 * The scope the procedure list is asked for, which is the discipline's own name.
 *
 * This used to map to "system", "highLevel" and "lowLevel". The endpoint matches "System", "Software",
 * "HighLevelSoftware" and "LowLevelSoftware", so none of those matched anything and no filter was applied:
 * every discipline's Explorer listed all 515 procedures in the Project, System and HLR and LLR together. It
 * went unnoticed because nothing asserted the count, and it matters now that this is the only place
 * procedures are browsed — an HLR engineer confirming coverage could pick an LLR procedure off the list.
 */
const scopeOf = (discipline: TestDiscipline) => discipline
const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'Software HLR' : 'Software LLR'

/**
 * Every controlled test procedure in this build, browsed the way requirements are browsed.
 *
 * The requirements explorer answers "what does this artifact say, what does it trace to, what happened to it,
 * and what has anybody said about it". Those are the same four questions asked of a procedure, so this is that
 * component's inspector rather than a second one that resembles it — same tabs, same stylesheet, same order.
 *
 * The trace runs the other way, and that is the one real difference: a requirement's trace shows what derives
 * from it, while a procedure's shows the requirements that drive it. A procedure exists because something has
 * to be verified.
 */
export default function TestProcedureExplorer({ api, projectId, releaseId, discipline, buildName, releaseVersion,
  released, onOpenRequirementRevision }: {
  api: string; projectId: string; releaseId: string; discipline: TestDiscipline; buildName: string
  /** The build's own version, which the document actions name. `buildName` is the display label, not this. */
  releaseVersion: string
  released: boolean
  onOpenRequirementRevision: (requirement: { id: string; revisionId: string; level: string }) => void
}) {
  const [data, setData] = useState<Page>()
  // Seeded from the address, so a link to one procedure opens on that procedure rather than on page one of
  // everything. The number narrows the list to it; the identifier selects it once the list arrives. These are
  // the parameter names the coverage page used before its library moved here, so links already in circulation
  // — and the requirement trace's "Open test procedure" — keep working.
  const opening = useRef(typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams()).current
  const [query, setQuery] = useState(opening.get('procedure') ?? '')
  const [procedureState, setProcedureState] = useState(opening.get('procedureState') ?? '')
  const [procedureOutcome, setProcedureOutcome] = useState(opening.get('procedureOutcome') ?? '')
  const [page, setPage] = useState(Number(opening.get('procedurePage') ?? '1') || 1)
  // Rows, the document rail's selection and the saved view are all part of the address, so a filtered
  // worklist survives a reload and the back button — the same contract the requirements Explorer keeps.
  const [pageSize, setPageSize] = useState(Number(opening.get('procedureRows') ?? '25') || 25)
  const [documentId, setDocumentId] = useState(opening.get('procedureDocument') ?? '')
  const [sectionId, setSectionId] = useState(opening.get('procedureSection') ?? '')
  const [documents, setDocuments] = useState<ProcedureDocument[]>([])
  const [showSaveView, setShowSaveView] = useState(false)
  // The applied view is in the address too, so "here is the worklist I mean" is a link somebody can send.
  const [viewId, setViewId] = useState(opening.get('procedureView') ?? '')
  const initialViewId = useRef(opening.get('procedureView') ?? '').current
  const appliedInitialView = useRef(false)
  const lastDiscreteState = useRef<string | null>(null)
  const [selectedId, setSelectedId] = useState(opening.get('procedureId') ?? '')
  const [tab, setTab] = useState<Tab>(() => {
    const seeded = opening.get('procedureTab')
    return seeded === 'trace' || seeded === 'history' || seeded === 'discussion' ? seeded : 'details'
  })
  const [history, setHistory] = useState<History>()
  const [trace, setTrace] = useState<ProcedureTrace>()
  const [traceError, setTraceError] = useState(false)
  const [comments, setComments] = useState<Comment[]>([])
  const [error, setError] = useState('')
  const [pageTab, setPageTab] = useState<PageTab>('procedures')
  const [coverage, setCoverage] = useState<Coverage>()
  const [coverageRead, setCoverageRead] = useState(false)
  const [showAll, setShowAll] = useState(false)

  const scope = scopeOf(discipline)
  // A page at a time, at the requirements explorer's own default. A build holds hundreds of procedures, and
  // the reader is looking for one of them.
  // Only the newest request may write the list.
  //
  // Changing a filter starts a second request while the first is still in flight, and nothing ordered the
  // replies. The unfiltered query is by far the slower one — it scans every procedure's coverage back to the
  // effective baseline — so the narrow filtered reply routinely arrived first and was then buried by the broad
  // reply behind it: the reader typed a search, saw the procedure they wanted, and watched the whole list they
  // had just filtered away come back over the top of it, with their search term still in the box.
  const listTicket = useRef(0)
  const load = useCallback(async () => {
    const mine = ++listTicket.current
    setError('')
    try {
      const response = await fetch(
        `${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}` +
        `&search=${encodeURIComponent(query)}&state=${procedureState}&outcome=${procedureOutcome}` +
        (documentId ? `&documentId=${documentId}` : '') +
        (sectionId ? `&sectionId=${sectionId}` : '') +
        `&page=${page}&pageSize=${pageSize}`)
      if (!response.ok) throw new Error(String(response.status))
      const paged = await response.json()
      if (mine !== listTicket.current) return
      setData(paged)
    } catch (problem) {
      if (mine !== listTicket.current) return
      setError(operationError(problem, 'The procedure library could not be loaded.'))
    }
  }, [api, projectId, releaseId, scope, query, procedureState, procedureOutcome, page, pageSize, documentId, sectionId])

  // The documents this discipline's procedures are written into. Read once per project and scope: the rail
  // is structure, not a result set, and re-reading it on every keystroke would make it flicker.
  useEffect(() => {
    let active = true
    fetch(`${api}/api/projects/${projectId}/test-procedure-documents?scope=${scope}`)
      .then(response => response.ok ? response.json() : [])
      .then((value: ProcedureDocument[]) => { if (active) setDocuments(value) })
      .catch(() => { if (active) setDocuments([]) })
    return () => { active = false }
  }, [api, projectId, scope])
  useEffect(() => { void load() }, [load])

  /**
   * Saved view lifecycle, the same contract the requirements workspace keeps. The server is the authority —
   * it answers Not Found for a view that is not yours — so these read the failure the API reports rather
   * than deciding locally who may do what.
   */
  const mutateView = async (view: SavedView, method: 'PUT' | 'DELETE', body?: unknown) => {
    setError('')
    try {
      await apiRequest(`${api}/api/test-procedures/views/${view.id}`, {
        method,
        ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
      })
      await load()
    } catch (reason) {
      setError(operationError(reason, 'The saved view could not be updated.'))
    }
  }
  const renameView = async (view: SavedView) => {
    const name = prompt('Rename saved view', view.name)
    if (name === null || name.trim() === '' || name.trim() === view.name) return
    await mutateView(view, 'PUT', { name: name.trim() })
  }
  const shareView = (view: SavedView, isShared: boolean) => mutateView(view, 'PUT', { isShared })
  const deleteView = async (view: SavedView) => {
    if (!confirm(`Delete the saved view "${view.name}"? Anyone holding its link will no longer be able to open it.`)) return
    await mutateView(view, 'DELETE')
  }
  const applyView = useCallback((view: SavedView) => {
    try {
      const saved = JSON.parse(view.queryJson)
      setQuery(saved.search || '')
      setProcedureState(saved.state || '')
      setProcedureOutcome(saved.outcome || '')
      setDocumentId(saved.documentId || '')
      setSectionId(saved.sectionId || '')
      setViewId(view.id)
      setPage(1)
    } catch {
      setError('Saved view configuration is invalid.')
    }
  }, [])
  // A link to a saved view opens that worklist, once. Re-applying it on every list refresh would undo the
  // reader's own filtering the moment they changed anything.
  useEffect(() => {
    if (appliedInitialView.current || !initialViewId || !data?.views?.length) return
    const view = data.views.find(x => x.id === initialViewId)
    if (!view) return
    appliedInitialView.current = true
    applyView(view)
  }, [data?.views, initialViewId, applyView])

  const saveView = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const fields = new FormData(event.currentTarget)
    setError('')
    try {
      await apiRequest(`${api}/api/test-procedures/views`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          projectId,
          name: fields.get('name'),
          isShared: fields.has('shared'),
          queryJson: JSON.stringify({
            search: query,
            state: procedureState,
            outcome: procedureOutcome,
            documentId,
            sectionId,
          }),
          columnsJson: '["identifier","level","verifies","latestResult","state"]',
        }),
      })
      setShowSaveView(false)
      await load()
    } catch (reason) {
      setError(operationError(reason, 'The saved view could not be created.'))
    }
  }

  // The worklist is in the address, so it can be reloaded, shared and stepped back through.
  useEffect(() => {
    const params = new URLSearchParams(location.search)
    const before = params.toString()
    const apply = (key: string, value: string) => { if (value) params.set(key, value); else params.delete(key) }
    apply('procedure', query)
    apply('procedureState', procedureState)
    apply('procedureOutcome', procedureOutcome)
    apply('procedurePage', page > 1 ? String(page) : '')
    apply('procedureRows', pageSize === 25 ? '' : String(pageSize))
    apply('procedureDocument', documentId)
    apply('procedureSection', sectionId)
    apply('procedureView', viewId)
    // Seeded from what the address already says, so the reader's first change after a reload still earns a
    // history entry rather than being mistaken for arrival.
    const discrete = `${procedureState}|${procedureOutcome}|${page}|${pageSize}|${documentId}|${sectionId}|${viewId}`
    if (lastDiscreteState.current === null) lastDiscreteState.current = discrete
    if (params.toString() === before) return
    const next = `${location.pathname}${params.toString() ? `?${params}` : ''}`
    // Choosing a filter or a page is somewhere the reader went, so it earns a history entry and the back
    // button returns to the previous list. Typing in the search box is not somewhere they went; pushing per
    // keystroke would mean pressing back a dozen times to leave one search.
    const push = discrete !== lastDiscreteState.current
    lastDiscreteState.current = discrete
    // window.history explicitly: this component has its own `history` — the revision history of a procedure —
    // and the bare name resolves to that, which throws rather than navigating.
    if (push) window.history.pushState({}, '', next); else window.history.replaceState({}, '', next)
  }, [query, procedureState, procedureOutcome, page, pageSize, documentId, sectionId, viewId])

  // The browser's own navigation must move the list, not just the address bar.
  useEffect(() => {
    const restore = () => {
      const params = new URLSearchParams(location.search)
      setQuery(params.get('procedure') ?? '')
      setProcedureState(params.get('procedureState') ?? '')
      setProcedureOutcome(params.get('procedureOutcome') ?? '')
      setPage(Number(params.get('procedurePage') ?? '1') || 1)
      setPageSize(Number(params.get('procedureRows') ?? '25') || 25)
      setDocumentId(params.get('procedureDocument') ?? '')
      setSectionId(params.get('procedureSection') ?? '')
      setViewId(params.get('procedureView') ?? '')
      const seeded = params.get('procedureTab')
      setTab(seeded === 'trace' || seeded === 'history' || seeded === 'discussion' ? seeded : 'details')
    }
    addEventListener('popstate', restore)
    return () => removeEventListener('popstate', restore)
  }, [])

  const procedures = data?.items ?? []
  // Read once, defensively. A build with no effective procedures answers with a deliberately empty page, and
  // a rail that crashes the workspace because a field it wanted was absent takes the whole page down with it.
  const savedViews = data?.views ?? []
  // Keyed on the page rather than the derived array, so the identity stays stable between renders. The
  // history and discussion effects watch this object, and a fresh one every render would refetch forever.
  const selected = useMemo(() => data?.items.find(x => x.id === selectedId), [data, selectedId])

  // Loaded when the tab is opened rather than with the list. A reader browsing forty procedures does not need
  // forty revision histories fetched on their behalf.
  useEffect(() => {
    if (!selected || tab !== 'history') return
    let active = true
    void (async () => {
      try {
        const response = await fetch(
          `${api}/api/test-procedures/${selected.id}/history?releaseId=${releaseId}&revisionId=${selected.revisionId}`)
        if (response.ok && active) setHistory(await response.json())
      } catch { if (active) setHistory(undefined) }
    })()
    return () => { active = false }
  }, [api, releaseId, selected, tab])

  // Loaded when the tab is opened rather than with the list, like history: a reader browsing procedures does
  // not need every trace fetched on their behalf. The server projection is authoritative, naming the exact
  // coverage rows of the exact revision this build carries rather than a count derived in the browser.
  useEffect(() => {
    if (!selected || tab !== 'trace') return
    let active = true
    setTraceError(false)
    void (async () => {
      try {
        const response = await fetch(
          `${api}/api/test-procedures/${selected.id}/trace?releaseId=${releaseId}&revisionId=${selected.revisionId}`)
        if (response.ok && active) setTrace(await response.json())
        else if (active) setTraceError(true)
      } catch { if (active) setTraceError(true) }
    })()
    return () => { active = false }
  }, [api, releaseId, selected, tab])

  const loadComments = useCallback(async (procedureId: string) => {
    try {
      const response = await fetch(`${api}/api/test-procedures/${procedureId}/comments`)
      if (response.ok) setComments(await response.json())
    } catch { setComments([]) }
  }, [api])
  // On selection rather than on opening the tab, because the tab wears the count. A number fetched only once
  // somebody looks is a number that is wrong until they do.
  useEffect(() => {
    if (!selected) return
    setComments([])
    void loadComments(selected.id)
  }, [loadComments, selected])

  const addComment = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected) return
    const form = event.currentTarget
    const body = String(new FormData(form).get('body'))
    const mentions = [...body.matchAll(/@([a-z0-9._-]+)/gi)].map(match => match[1])
    setError('')
    try {
      await apiRequest(`${api}/api/test-procedures/${selected.id}/comments`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revisionId: selected.revisionId, body, mentions }),
      })
      form.reset()
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be added.')) }
  }

  // The resolve route reads ArtifactComments by identifier alone, so a procedure comment settles through the
  // same endpoint a requirement comment does rather than a second one that behaves almost the same.
  const resolveComment = async (id: string) => {
    if (!selected) return
    const disposition = window.prompt('Disposition or resolution rationale (optional):') ?? ''
    setError('')
    try {
      await apiRequest(`${api}/api/enterprise-requirements/comments/${id}/resolve`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ disposition }),
      })
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be resolved.')) }
  }

  // Switching discipline or build is a different page asking a different question, and this component stays
  // mounted across that switch. Without this, moving from HLR to LLR kept the coverage already read — the LLR
  // page would have shown HLR's numbers under an LLR heading, which is worse than showing nothing. The tab
  // goes back to the procedures it is named for, too.
  useEffect(() => {
    setCoverage(undefined)
    setCoverageRead(false)
    setShowAll(false)
    setPageTab('procedures')
  }, [api, projectId, releaseId, discipline])

  // Read when the coverage tab is first opened, not with the procedure list. Coverage is three requests and a
  // whole-configuration computation, and a reader who came to find one procedure by number should not pay for
  // a report they did not ask for. Read once and kept, because it does not change while the page is open.
  useEffect(() => {
    if (pageTab !== 'coverage' || coverageRead) return
    let active = true
    void (async () => {
      const { coverage: next, failed } = await loadCoverage(api, projectId, releaseId, discipline)
      if (!active) return
      setCoverageRead(true)
      if (next) setCoverage(next)
      if (failed) setError('The requirement coverage for this build could not be read.')
    })()
    return () => { active = false }
  }, [api, projectId, releaseId, discipline, pageTab, coverageRead])

  const uncovered = coverage?.items.filter(x => x.disposition === 'Uncovered') ?? []
  const suspect = coverage?.items.filter(x => x.disposition === 'Suspect') ?? []

  const open = (procedure: Procedure) => {
    setSelectedId(procedure.id); setTab('details'); setHistory(undefined); setTrace(undefined); setTraceError(false)
    const params = new URLSearchParams(location.search)
    params.set('procedure', procedure.displayNumber)
    params.set('procedureId', procedure.id)
    params.set('procedureRevisionId', procedure.revisionId)
    params.delete('procedureTab')
    window.history.replaceState({}, '', `${location.pathname}?${params}`)
  }
  const close = () => {
    setSelectedId('')
    const params = new URLSearchParams(location.search)
    params.delete('procedureId')
    params.delete('procedureRevisionId')
    params.delete('procedureTab')
    window.history.replaceState({}, '', `${location.pathname}${params.size ? `?${params}` : ''}`)
  }
  // The tab is part of the address, so a direct deep link to a procedure's Trace & impact tab reopens the
  // same trace context after a refresh. Deliberately a replace: choosing a tab is not somewhere the reader
  // went so much as how they are reading the procedure they already chose.
  const selectTab = (next: Tab) => {
    setTab(next)
    const params = new URLSearchParams(location.search)
    if (next === 'details') params.delete('procedureTab'); else params.set('procedureTab', next)
    window.history.replaceState({}, '', `${location.pathname}${params.size ? `?${params}` : ''}`)
  }

  // A workspace is its own <main>: the shell supplies the navigation and context bar, not the landmark.
  return <main className="procedureExplorer">
    <header className="procedureExplorerHead">
      <div>
        <p className="eyebrow">VERIFICATION / {disciplineLabel(discipline).toUpperCase()}</p>
        <h1>Test Procedure Explorer</h1>
        <p>Every controlled {disciplineLabel(discipline)} procedure {buildName} carries, and what it covers.</p>
      </div>
    </header>
    {error && <div className="workspaceError" role="alert">{error}</div>}

    {/* The document these procedures are written into, offered where they are read — the same place, shape
        and rule the requirements Explorer uses. Which one you get follows the build: approved for a released
        one, a stamped draft for an in-work one. */}
    <DocumentActions
      api={api}
      projectId={projectId}
      release={{ id: releaseId, version: releaseVersion, isReleased: released }}
      targets={procedureTargetsFor(discipline)}
      heading={released ? `Approved documents for ${releaseVersion}` : `Draft documents for ${releaseVersion}`}
    />

    <div className="explorerTabs" role="tablist" aria-label="Test procedure views">
      <button type="button" role="tab" aria-selected={pageTab === 'procedures'}
        className={pageTab === 'procedures' ? 'active' : ''}
        onClick={() => setPageTab('procedures')}>Procedures</button>
      <button type="button" role="tab" aria-selected={pageTab === 'coverage'}
        className={pageTab === 'coverage' ? 'active' : ''}
        onClick={() => setPageTab('coverage')}>Requirement coverage</button>
    </div>

    {pageTab === 'coverage' && (
      <div className="explorerCoverage">
        <section className="coverageSummary" aria-label="Coverage summary">
          <article><b>{coverage?.total ?? 0}</b><span>Requirements</span></article>
          <article><b>{coverage?.covered ?? 0}</b><span>With a procedure</span></article>
          <article className={uncovered.length ? 'attention' : ''}><b>{uncovered.length}</b><span>With none</span></article>
          <article className={suspect.length ? 'attention' : ''}><b>{suspect.length}</b><span>Suspect coverage</span></article>
        </section>

        {(uncovered.length > 0 || suspect.length > 0) && (
          <section className="coverageCard">
            <div className="cardTitle">
              <h2>Requirements needing attention</h2>
              <p>A requirement with no procedure cannot be verified, and coverage carried across a change nobody reconfirmed does not count.</p>
            </div>
            {uncovered.slice(0, 25).map(item => (
              <article className="coverageRow attention" key={item.revisionId}>
                <div><b>{item.displayNumber}</b><i>No procedure</i></div>
                <p>{item.statement}</p>
              </article>
            ))}
            {suspect.slice(0, 25).map(item => (
              <article className="coverageRow attention" key={`suspect-${item.revisionId}`}>
                <div><b>{item.displayNumber}</b><i>Suspect</i></div>
                <p>{item.statement}</p>
                <small>Covered by {item.coveredBy.map(x => x.displayNumber).join(', ')}, written against earlier wording.</small>
              </article>
            ))}
          </section>
        )}

        <section className="coverageCard">
          <div className="cardTitle">
            <h2>Requirement coverage</h2>
            <p>Every effective requirement in this build and the procedures that verify it.</p>
          </div>
          {/* Attention first, then everything. A reader arriving to do work needs the requirements that cannot
              be verified as things stand; a reader answering "is this build covered" needs the whole set. The
              second is much the longer list, so it is asked for rather than imposed. */}
          <button type="button" className="quiet" onClick={() => setShowAll(current => !current)}>
            {showAll ? 'Show only what needs attention' : `Show all ${coverage?.total ?? 0} requirements`}
          </button>
          {showAll && (
            <div className="fullCoverage">
              {(coverage?.items ?? []).map(item => (
                <article className={`coverageRow ${item.covered ? '' : 'attention'}`} key={`all-${item.revisionId}`}>
                  <div>
                    <b>{item.displayNumber}</b>
                    {/* Suspect is read before "no procedure". A requirement whose only procedure was written
                        against an earlier revision is not covered — but saying nothing is testing it hides the
                        procedure somebody has to reconfirm or replace, which is the actual work. */}
                    <i>{item.verified ? 'Verified'
                      : item.coveredBy.some(x => x.coverageState === 'Suspect') ? 'Suspect'
                      : item.covered ? 'Covered'
                      : 'No procedure'}</i>
                  </div>
                  <p>{item.statement}</p>
                  {item.coveredBy.length > 0 && <small>{item.coveredBy.map(x => `${x.displayNumber} (${x.state})`).join(', ')}</small>}
                </article>
              ))}
            </div>
          )}
        </section>

        {coverageRead && !coverage && (
          <p className="coverageNone">
            This build has not materialized its requirements, so there is nothing to report coverage against yet.
          </p>
        )}
      </div>
    )}

    {pageTab === 'procedures' && <>
    {/* Browsing, not just searching. The software side of the demonstration Program carries 440 procedures,
        so a list that could only be searched meant knowing the number of the thing you were looking for
        before you could look for it. State and latest result are how somebody actually narrows this: "the
        drafts", "what failed last time". */}
    <div className="procedureFilters">
      <label className="procedureFind">
        <span>Find a procedure</span>
        <input value={query} onChange={event => { setQuery(event.target.value); setPage(1) }}
          placeholder="Search any identifier fragment or title…" />
        {/* The count belongs on the search, where the requirements Explorer puts it: a filtered list whose
            size you cannot see is a list you cannot trust you have read all of. */}
        <b className="resultCount">{(data?.totalCount ?? 0).toLocaleString()} found</b>
      </label>
      <label>
        <span>Procedure state</span>
        <select value={procedureState} onChange={event => { setProcedureState(event.target.value); setPage(1) }}>
          <option value="">All states</option>
          <option value="Draft">Draft</option>
          <option value="InReview">In review</option>
          <option value="Approved">Approved</option>
        </select>
      </label>
      <label>
        <span>Latest result</span>
        <select value={procedureOutcome} onChange={event => { setProcedureOutcome(event.target.value); setPage(1) }}>
          <option value="">All outcomes</option>
          <option value="Pass">Pass</option>
          <option value="Fail">Fail</option>
          <option value="Blocked">Blocked</option>
        </select>
      </label>
      <button type="button" className="clear"
        disabled={!query && !procedureState && !procedureOutcome && !documentId && !sectionId && !viewId}
        onClick={() => {
          setViewId('')
          setQuery(''); setProcedureState(''); setProcedureOutcome('')
          setDocumentId(''); setSectionId(''); setPage(1)
        }}>
        Clear
      </button>
      <label className="pageSizeControl">
        <span>Rows</span>
        <select aria-label="Rows per page" value={pageSize}
          onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}>
          <option value={25}>25</option>
          <option value={50}>50</option>
          <option value={100}>100</option>
        </select>
      </label>
    </div>

    <div className="procedureExplorerSplit">
      {/* The documents these procedures are written into, in the place and shape the requirements Explorer
          puts its specifications. Procedures had no container until they were given one; this is the rail
          that was impossible before. */}
      <div className="procedureRailColumn">
      <nav className="procedureDocumentRail" aria-label="Test procedure documents">
        <h2>Documents</h2>
        <button type="button" className={!documentId && !sectionId ? 'railEntry selected' : 'railEntry'}
          aria-pressed={!documentId && !sectionId}
          onClick={() => { setDocumentId(''); setSectionId(''); setPage(1) }}>
          <b>All procedures</b>
          <small>{(data?.totalCount ?? 0).toLocaleString()} in this build</small>
        </button>
        {documents.map(document => (
          <div key={document.id} className="railDocument">
            <button type="button" data-document={document.documentNumber}
              className={documentId === document.id && !sectionId ? 'railEntry selected' : 'railEntry'}
              aria-pressed={documentId === document.id && !sectionId}
              onClick={() => { setDocumentId(document.id); setSectionId(''); setPage(1) }}>
              <b>{document.documentNumber}</b>
              <small>{document.procedureCount} · {document.title}</small>
            </button>
            {document.sections.map(section => (
              <button type="button" key={section.id} className={sectionId === section.id ? 'railSection selected' : 'railSection'}
                aria-pressed={sectionId === section.id}
                onClick={() => { setDocumentId(document.id); setSectionId(section.id); setPage(1) }}>
                {section.heading} <i>{section.procedureCount}</i>
              </button>
            ))}
          </div>
        ))}
        {documents.length === 0 && (
          <p className="railEmpty">No procedure document for this discipline yet.</p>
        )}
      </nav>

      {/* Saved views, in the place and shape the requirements Explorer keeps them. Owners can tidy their
          own; non-owners see a shared view and no controls, which is the same authority the server enforces
          rather than a second opinion about it.
          Beside the documents rather than inside it: a navigation landmark announced as "Test procedure
          documents" should not also contain a form for naming worklists. */}
      <section className="savedViewsPanel" aria-label="Saved views">
        <details className="savedViews">
          <summary>
            <b>Saved views</b>
            <span>{savedViews.length}</span>
          </summary>
          <div>
            {savedViews.map(view => (
              <div className="savedViewRow" key={view.id}>
                <button type="button" data-saved-view={view.name} onClick={() => applyView(view)}>
                  <i>{view.isShared ? '◉' : '○'}</i>
                  <div>
                    <b>{view.name}</b>
                    <small>{view.isShared ? 'Shared' : 'Personal'}</small>
                  </div>
                </button>
                {view.owned && (
                  <div className="savedViewActions">
                    <button type="button" title="Rename this view" onClick={() => renameView(view)}>Rename</button>
                    <button type="button" title={view.isShared ? 'Make personal' : 'Share with authorized'}
                      onClick={() => shareView(view, !view.isShared)}>
                      {view.isShared ? 'Unshare' : 'Share'}
                    </button>
                    <button type="button" title="Delete this view" onClick={() => deleteView(view)}>Delete</button>
                  </div>
                )}
              </div>
            ))}
            {savedViews.length === 0 && <p className="railEmpty">No saved views yet.</p>}
            {showSaveView ? (
              <form className="saveViewForm" onSubmit={saveView}>
                <label>
                  <span>Name this worklist</span>
                  <input name="name" required maxLength={200} placeholder="Failed since the last build" />
                </label>
                <label className="saveViewShare">
                  <input type="checkbox" name="shared" />
                  <span>Share with everyone on this project</span>
                </label>
                <div className="saveViewActions">
                  <button type="submit">Save view</button>
                  <button type="button" onClick={() => setShowSaveView(false)}>Cancel</button>
                </div>
              </form>
            ) : (
              <button type="button" className="saveViewOpen" onClick={() => setShowSaveView(true)}>
                Save this view
              </button>
            )}
          </div>
        </details>
      </section>
      </div>

      <section className="procedureList" aria-label="Test procedures">
        {procedures.length === 0
          ? <p className="procedureEmpty">{query || procedureState || procedureOutcome || documentId || sectionId
            ? 'No procedure matches that. Clear the search or the filters to see the rest.'
            : `This build has no controlled ${disciplineLabel(discipline).toLowerCase()} procedures yet.`}</p>
          : (
            <table className="procedureTable">
              <thead>
                <tr>
                  <th scope="col">Identifier &amp; title</th>
                  <th scope="col">Level</th>
                  <th scope="col">Verifies</th>
                  <th scope="col">Latest result</th>
                  <th scope="col">State</th>
                </tr>
              </thead>
              <tbody>
                {procedures.map(procedure => (
                  // `procedureRow` is kept on the table row. It is the hook the procedure journeys have
                  // always selected a row by — bounded rendering, deep links and the trace all reach a
                  // procedure through it — and the list becoming a table is a change of presentation, not of
                  // what a row is.
                  // The whole row opens the procedure, as it did when the row was itself a button. Anywhere
                  // in a record's row is where people click; the identifier cell keeps its own button so the
                  // row is still reachable and operable from the keyboard.
                  <tr key={procedure.id} data-procedure={procedure.displayNumber}
                    onClick={() => open(procedure)}
                    className={`procedureRow${procedure.id === selectedId ? ' selected' : ''}`}>
                    <td>
                      <button type="button" className="procedureOpen" aria-pressed={procedure.id === selectedId}
                        onClick={() => open(procedure)}>
                        <b>{procedure.displayNumber}</b>
                        <span>{procedure.title}</span>
                      </button>
                    </td>
                    <td>{disciplineLabel(discipline)}</td>
                    <td className="procedureCountCell">{procedure.requirementCount}</td>
                    <td>{procedure.lastOutcome ?? 'Not run'}</td>
                    <td><span className={`procedureState ${procedure.state.toLowerCase()}`}>{procedure.state}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        <div className="pager">
          <button disabled={(data?.page ?? 1) <= 1} onClick={() => setPage(x => x - 1)}>← Previous</button>
          <span>
            {(data?.totalCount ?? 0) > 0
              ? `${((data?.page ?? 1) - 1) * (data?.pageSize ?? 25) + 1}–` +
                `${Math.min((data?.page ?? 1) * (data?.pageSize ?? 25), data?.totalCount ?? 0)} ` +
                `of ${(data?.totalCount ?? 0).toLocaleString()}`
              : '0 procedures'}
          </span>
          <button disabled={(data?.page ?? 1) >= (data?.totalPages ?? 1)}
            onClick={() => setPage(x => x + 1)}>Next →</button>
        </div>
      </section>

      {selected && (
        <aside className="requirementInspector" aria-label={`${selected.displayNumber} detail`}>
          <div className="inspectorTop">
            <div>
              <b>{selected.displayNumber}</b>
              <span>{selected.title}</span>
            </div>
            <button type="button" className="inspectorClose" onClick={close}
              aria-label="Close procedure detail">×</button>
          </div>
          <div className="inspectorTabs">
            <button className={tab === 'details' ? 'active' : ''} onClick={() => selectTab('details')}>Overview</button>
            <button className={tab === 'trace' ? 'active' : ''} onClick={() => selectTab('trace')}>Trace &amp; impact</button>
            <button className={tab === 'history' ? 'active' : ''} onClick={() => selectTab('history')}>History</button>
            <button className={tab === 'discussion' ? 'active' : ''} onClick={() => selectTab('discussion')}>
              Discussion <span>{comments.length}</span>
            </button>
          </div>

          {tab === 'details' && (
            <div className="inspectorBody">
              {selected.titleNote && <p className="inspectorNote warn">{selected.titleNote}</p>}
              <dl className="procedureCase">
                <dt>Objective</dt><dd>{selected.objective || 'Not recorded'}</dd>
                <dt>Preconditions</dt><dd>{selected.preconditions || 'None'}</dd>
                <dt>Steps</dt><dd>{selected.steps || 'Not recorded'}</dd>
                <dt>Expected result</dt><dd>{selected.expectedResult || 'Not recorded'}</dd>
                <dt>State</dt><dd>{selected.state}</dd>
                <dt>Owner</dt><dd><PersonName userName={selected.ownerId} /></dd>
              </dl>
            </div>
          )}

          {tab === 'trace' && (
            <div className="inspectorBody">
              {/* The other direction from a requirement's trace: a requirement shows what derives from it, a
                  procedure shows what it exists to verify. The server projection names the exact revision this
                  build carries and every exact requirement revision it verifies, with its Confirmed or Suspect
                  coverage state and the TCR/change provenance that produced the procedure revision. */}
              {trace ? (
                <>
                  <div className="traceRevisionIdentity">
                    <b>{trace.displayNumber}</b>
                    <span>{trace.title}</span>
                    <span>{stateLabel(trace.state)} · {trace.level} · revision {trace.revisionId}</span>
                    <small>Written by <PersonName userName={trace.authorId} /> · {new Date(trace.createdAt).toLocaleString()}</small>
                  </div>
                  {trace.titleNote && <p className="inspectorNote warn">{trace.titleNote}</p>}
                  {trace.provenance.length > 0 && (
                    <p className="traceProvenance">
                      {trace.provenance.some(driver => driver.isLegacy) ? 'Related controlled impact: ' : 'Produced by '}
                      {trace.provenance.map(driver => `${driver.package} (${driver.changeRequest})`).join(', ')}
                    </p>
                  )}
                  {trace.provenance.length === 0 && trace.package && (
                    <p className="traceProvenance">Produced by {trace.package}</p>
                  )}
                  {trace.provenanceNote && <p className="inspectorNote warn">{trace.provenanceNote}</p>}
                  <p className="inspectorNote">
                    This procedure verifies {trace.requirements.length} requirement{trace.requirements.length === 1 ? '' : 's'}.
                  </p>
                  {trace.requirements.length === 0 ? (
                    <p className="inspectorNote warn">
                      Nothing is verified by {trace.displayNumber}. Either it has not been linked yet, or the
                      requirement it was written against has been retired.
                    </p>
                  ) : (
                    <ul className="traceRequirements">
                      {trace.requirements.map(item => (
                        <li key={item.revisionId}
                          className={`traceRequirement${item.coverageState === 'Suspect' ? ' suspect' : ''}`}>
                          <div className="traceRequirementHead">
                            <b>{item.displayNumber}</b>
                            <span>{item.level}</span>
                            <i className={`traceCoverageBadge ${item.coverageState === 'Suspect' ? 'suspect' : 'confirmed'}`}>
                              {item.coverageState}
                            </i>
                          </div>
                          <p>{item.statement}</p>
                          <small>Revision {item.revisionId}</small>
                          <button type="button" className="linkedArtifactText"
                            onClick={() => onOpenRequirementRevision(item)}>
                            Open requirement →
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </>
              ) : traceError ? (
                <p className="inspectorNote warn">The trace for this procedure revision could not be loaded.</p>
              ) : (
                <p className="inspectorNote">Loading trace…</p>
              )}
            </div>
          )}

          {tab === 'history' && (
            <div className="inspectorBody">
              {history
                ? <ul className="revisionList">{history.revisions.map(revision => (
                  <li key={revision.id}>
                    <b>{revision.displayNumber} — {revision.title}</b>
                    <span>{revision.state} · written by <PersonName userName={revision.authorId} /></span>
                    {revision.titleNote && <span className="inspectorNote warn">{revision.titleNote}</span>}
                    {revision.drivenBy.length > 0 && (
                      <span className="revisionDriver">
                        {revision.drivenBy.some(driver => driver.isLegacy) ? 'Related controlled impact: ' : ''}
                        {revision.drivenBy.map(driver => `${driver.package} · ${driver.changeRequest}`).join(', ')}
                      </span>
                    )}
                    {revision.drivenBy.length === 0 && revision.package && (
                      <span className="revisionDriver">Produced by {revision.package}</span>
                    )}
                    {revision.provenanceNote && (
                      <span className="inspectorNote warn">{revision.provenanceNote}</span>
                    )}
                  </li>))}</ul>
                : <p className="inspectorNote">Loading history…</p>}
            </div>
          )}

          {tab === 'discussion' && (
            <div className="inspectorBody discussionPane">
              {!released ? <form onSubmit={addComment}>
                <textarea name="body" required
                  placeholder="Add an attributable comment. Use @username to mention someone." />
                <div className="commentFoot"><button>Add comment</button></div>
              </form> : <div className="traceEmpty">
                <span>Discussion is read-only in released {buildName}.</span>
              </div>}
              {comments.map(comment => (
                <article key={comment.id} className={comment.state.toLowerCase()}>
                  <div>
                    <b><PersonName userName={comment.createdBy} /></b>
                    <span>{new Date(comment.createdAt).toLocaleString()}</span>
                  </div>
                  <p>{comment.body}</p>
                  {comment.disposition && <small>Disposition: {comment.disposition}</small>}
                  <footer>
                    <i>{stateLabel(comment.state)}</i>
                    {comment.state === 'Open' && !released && (
                      <button onClick={() => void resolveComment(comment.id)}>Resolve / disposition</button>
                    )}
                  </footer>
                </article>
              ))}
            </div>
          )}
        </aside>
      )}
    </div>
    </>}
  </main>
}
