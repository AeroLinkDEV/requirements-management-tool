import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { configuredProcedureTargetsFor, stateLabel, verificationArtifactApiRoot, verificationArtifactNoun, verificationArtifactWord } from './presentation'
import DocumentActions from './DocumentActions'
import { loadCoverage, type Coverage } from './verificationCoverage'
import {
  ControlledArtifactExplorerHeader,
  ControlledArtifactExplorerLayout,
  ControlledArtifactInspector,
  ControlledArtifactInspectorEmpty,
} from './ControlledArtifactExplorer'
// The requirements explorer's stylesheet, imported rather than copied. Browsing a controlled artifact is the
// same job whichever discipline owns it, so the inspector is literally the same one.
import './RequirementsWorkspace.css'
import './TestingCoverageWorkspace.css'
import './TestProcedureExplorer.css'
import { LadderCapability, ladderAllows, ladderEnablesArtifactKind } from './projectLadder'
import type { LadderLevel, ProjectLadderProjection } from './projectLadder'
import ExactLinkLifecyclePanel from './ExactLinkLifecyclePanel'

type Procedure = {
  id: string
  version?: number
  revisionId: string
  revision?: number
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
  environmentSetup?: string
  testData?: string
  orderedSteps?: string
  expectedObservations?: string
  cleanup?: string
  toolingAutomation?: string
  parentKind?: string
  derivedRationale?: string
  retirementRationale?: string
  caseRevisionIds?: string[]
  parentCount?: number
  level: 'System' | 'HighLevel' | 'LowLevel'
  /// The most recent run's outcome, already projected by the listing endpoint.
  lastOutcome?: string
  lastExecutedAt?: string
  artifactKind?: 'Case' | 'Procedure'
  artifactLabel?: string
}
type Revision = {
  id: string; displayNumber: string; revision: number; title: string
  titleIsExact?: boolean; titleIsLegacy?: boolean; titleNote?: string
  state: string; authorId: string; createdAt: string
  objective: string; preconditions: string; steps: string; expectedResult: string
  environmentSetup?: string; testData?: string; orderedSteps?: string
  expectedObservations?: string; cleanup?: string; toolingAutomation?: string
  parentKind?: string; derivedRationale?: string; caseRevisionIds?: string[]
  caseParents?: { linkId: string; caseRevisionId: string; state: string; outcome?: string }[]
  retirementRationale?: string
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
/** Stable hand-off contract for App: the selected exact artifact stays a verification concern. */
export type TestArtifactChangeContext = {
  mode: 'new' | 'existing'
  projectId: string
  releaseId: string
  artifactId: string
  artifactRevisionId: string
  artifactKind: 'Case' | 'Procedure'
  artifactLevel?: 'System' | 'HighLevel' | 'LowLevel'
  displayNumber: string
  testChangeReviewId?: string
  proposalId?: string
}
type TestChangeCandidate = {
  id: string; displayNumber: string; title: string; state: string; outcome: string
  artifactKey: string; version: number; eligible: boolean; reasonCode?: string; reason?: string
  existingProposalId?: string
}
type TestChangeCandidatePage = {
  artifactKey: string; artifactDisplayNumber: string; page: number; pageSize: number
  totalCount: number; totalPages: number; items: TestChangeCandidate[]
}
/// The document a discipline's procedures are written into, and the sections inside it.
type ProcedureDocument = {
  id: string
  documentNumber: string
  title: string
  level: string
  description: string
  artifactCount: number
  procedureCount?: number
  sections: { id: string; heading: string; position: number; artifactCount: number; procedureCount?: number }[]
}
type Comment = {
  id: string; revisionId?: string; body: string; state: string; createdBy: string; createdAt: string; disposition?: string
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
type TraceCaseParent = {
  linkId: string
  caseRevisionId: string
  displayNumber?: string
  title?: string
  state: string
  outcome?: string
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
  caseParents?: TraceCaseParent[]
  provenance: TraceProvenance[]
  build?: { releaseId: string; effectiveBaselineId: string; requirementBaselineId?: string; isExactManifest: boolean }
}

type Tab = 'details' | 'trace' | 'history' | 'discussion'
type ProcedureScope = 'System' | 'Software'
type ProcedureLevel = ProcedureScope | 'HighLevel' | 'LowLevel'
/**
 * The two questions this page answers, kept apart.
 *
 * "Which procedures does this build carry, and what happened to each" is browsing an inventory. "Is this
 * build covered, and what is not" is a report about requirements. Both are about procedures as they stand,
 * which is why they are on one page — but they are different questions and a reader is asking one of them.
 */
/** The Requirements-equivalent scope: all software procedures by default, optionally narrowed by level. */
const scopeOf = (discipline: ProcedureScope, level: ProcedureLevel) => {
  if (discipline === 'System') return 'System'
  if (level === 'HighLevel') return 'HighLevelSoftware'
  if (level === 'LowLevel') return 'LowLevelSoftware'
  return 'Software'
}
const disciplineLabel = (discipline: ProcedureScope) => discipline === 'System' ? 'System' : 'Software'
const procedureLevelLabel = (level: Procedure['level']) =>
  level === 'HighLevel' ? 'HLR' : level === 'LowLevel' ? 'LLR' : 'System'
const validLevel = (value: string | null, discipline: ProcedureScope, ladder: ProjectLadderProjection | null): ProcedureLevel => {
  if (discipline === 'Software' && (!value || value === 'Software')) return 'Software'
  if (discipline === 'Software' && value === 'HighLevel' && ladderAllows(ladder, 'HighLevel', LadderCapability.Verification)) return value
  if (discipline === 'Software' && value === 'LowLevel' && ladderAllows(ladder, 'LowLevel', LadderCapability.Verification)) return value
  if (discipline === 'Software' && ladderAllows(ladder, 'HighLevel', LadderCapability.Verification)) return 'HighLevel'
  if (discipline === 'Software' && ladderAllows(ladder, 'LowLevel', LadderCapability.Verification)) return 'LowLevel'
  return discipline
}

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
  released, onBack, onOpenRequirementRevision, onOpenTestChangeRequest, initialLevel, ladder }: {
  api: string; projectId: string; releaseId: string; discipline: ProcedureScope; buildName: string
  /** The build's own version, which the document actions name. `buildName` is the display label, not this. */
  releaseVersion: string
  released: boolean
  onBack?: () => void
  onOpenRequirementRevision: (requirement: { id: string; revisionId: string; level: string }) => void
  /** App owns navigation to the TCR workspace; this component owns exact artifact selection and proposal choice. */
  onOpenTestChangeRequest?: (context: TestArtifactChangeContext) => void
  initialLevel?: 'HighLevel' | 'LowLevel'
  ladder: ProjectLadderProjection | null
}) {
  const opening = useRef(typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams()).current
  // Cases and Procedures share one combined Explorer with an artifact-kind filter. The filter defaults to
  // the complete inventory so the page communicates the controlled Case -> Procedure model immediately.
  const [artifactKindFilter, setArtifactKindFilter] = useState<'all' | 'Case' | 'Procedure'>(() => {
    // The neutral Explorer route opens the combined inventory, while the legacy Case and Procedure routes
    // retain their original kind intent for links already in circulation.
    const pathKind = typeof location !== 'undefined' && location.pathname.endsWith('/procedures')
      ? 'procedure' : typeof location !== 'undefined' && location.pathname.endsWith('/cases') ? 'case' : ''
    const kind = opening.get('artifactKind')?.toLowerCase() || pathKind
    if (kind === 'procedure') return 'Procedure'
    if (kind === 'case') return 'Case'
    return 'all'
  })
  const isSystemScope = discipline === 'System'
  const currentArtifactWord = isSystemScope ? verificationArtifactWord('System') : artifactKindFilter === 'Case' ? verificationArtifactWord('HighLevel', 'Case') : artifactKindFilter === 'Procedure' ? verificationArtifactWord('HighLevel', 'Procedure') : 'test artifact'
  const currentArtifactShortWord = verificationArtifactNoun(isSystemScope ? 'System' : 'HighLevel', artifactKindFilter === 'all' ? 'Case' : artifactKindFilter).toLowerCase()
  const currentArtifactDisplayWord = artifactKindFilter === 'all' && !isSystemScope ? 'test artifact' : currentArtifactShortWord
  const currentArtifactShortPlural = isSystemScope ? 'procedures' : artifactKindFilter === 'Case' ? 'test cases' : artifactKindFilter === 'Procedure' ? 'test procedures' : 'test artifacts'
  const currentArtifactPlural = isSystemScope ? 'test procedures' : artifactKindFilter === 'Case' ? 'test cases' : artifactKindFilter === 'Procedure' ? 'test procedures' : 'test artifacts'
  const currentArtifactNoun = () => isSystemScope ? 'Procedure' : artifactKindFilter === 'Case' ? 'Case' : artifactKindFilter === 'Procedure' ? 'Procedure' : 'Test Artifact'
  const artifactApiRoot = isSystemScope ? verificationArtifactApiRoot(discipline) : '/api/verification-artifacts'
  const queryKey = useCallback((suffix = '') =>
    `${isSystemScope ? 'procedure' : 'artifact'}${suffix}`, [isSystemScope])
  const queryValue = useCallback((params: URLSearchParams, suffix = '') =>
    params.get(queryKey(suffix)) ?? (discipline === 'System' ? null : params.get(`procedure${suffix}`) ?? params.get(`case${suffix}`)),
  [discipline, queryKey])
  const [data, setData] = useState<Page>()
  // Seeded from the address, so a link to one procedure opens on that procedure rather than on page one of
  // everything. The number narrows the list to it; the identifier selects it once the list arrives. These are
  // the parameter names the coverage page used before its library moved here, so links already in circulation
  // — and the requirement trace's "Open test procedure" — keep working.
  const [level, setLevel] = useState<ProcedureLevel>(() =>
    validLevel(queryValue(opening, 'Level') ?? initialLevel ?? null, discipline, ladder))
  const enabledSoftwareLevels = level === 'HighLevel' ? ['HighLevel'] : level === 'LowLevel' ? ['LowLevel'] : ['HighLevel', 'LowLevel']
  const caseEnabled = isSystemScope || enabledSoftwareLevels.some(item => ladderEnablesArtifactKind(ladder, item as LadderLevel, 'Case'))
  const procedureEnabled = isSystemScope || enabledSoftwareLevels.some(item => ladderEnablesArtifactKind(ladder, item as LadderLevel, 'Procedure'))
  const procedureDocumentTargets = configuredProcedureTargetsFor(ladder, discipline, level,
    artifactKindFilter === 'all' ? undefined : artifactKindFilter)
  const procedureDocumentsEnabled = procedureDocumentTargets.length > 0
  useEffect(() => {
    if (artifactKindFilter === 'Procedure' && !procedureEnabled) setArtifactKindFilter(caseEnabled ? 'Case' : 'all')
    if (artifactKindFilter === 'Case' && !caseEnabled) setArtifactKindFilter(procedureEnabled ? 'Procedure' : 'all')
  }, [artifactKindFilter, caseEnabled, procedureEnabled])
  const [query, setQuery] = useState(queryValue(opening) ?? '')
  const [procedureState, setProcedureState] = useState(queryValue(opening, 'State') ?? '')
  const [procedureOutcome, setProcedureOutcome] = useState(queryValue(opening, 'Outcome') ?? '')
  const [page, setPage] = useState(Number(queryValue(opening, 'Page') ?? '1') || 1)
  // Rows, the document rail's selection and the saved view are all part of the address, so a filtered
  // worklist survives a reload and the back button — the same contract the requirements Explorer keeps.
  const [pageSize, setPageSize] = useState(Number(queryValue(opening, 'Rows') ?? '25') || 25)
  const [documentId, setDocumentId] = useState(queryValue(opening, 'Document') ?? '')
  const [sectionId, setSectionId] = useState(queryValue(opening, 'Section') ?? '')
  const [documents, setDocuments] = useState<ProcedureDocument[]>([])
  const [showSaveView, setShowSaveView] = useState(false)
  // The applied view is in the address too, so "here is the worklist I mean" is a link somebody can send.
  const [viewId, setViewId] = useState(queryValue(opening, 'View') ?? '')
  const initialViewId = useRef(queryValue(opening, 'View') ?? '').current
  const appliedInitialView = useRef(false)
  const lastDiscreteState = useRef<string | null>(null)
  const [selectedId, setSelectedId] = useState(queryValue(opening, 'Id') ?? '')
  const [selectedRevisionId, setSelectedRevisionId] = useState(queryValue(opening, 'RevisionId') ?? '')
  const [selectedDisplayNumber, setSelectedDisplayNumber] = useState(queryValue(opening) ?? '')
  const [tab, setTab] = useState<Tab>(() => {
    const seeded = queryValue(opening, 'Tab')
    return seeded === 'trace' || seeded === 'history' || seeded === 'discussion' ? seeded : 'details'
  })
  const [history, setHistory] = useState<History>()
  const [trace, setTrace] = useState<ProcedureTrace>()
  const [traceError, setTraceError] = useState(false)
  const [comments, setComments] = useState<Comment[]>([])
  const [error, setError] = useState('')
  const [showAdvanced, setShowAdvanced] = useState(false)
  const [coverage, setCoverage] = useState<Coverage>()
  const [coverageRead, setCoverageRead] = useState(false)
  const [showAllCoverage, setShowAllCoverage] = useState(false)
  const [proposalOpen, setProposalOpen] = useState(false)
  const [proposalSearch, setProposalSearch] = useState('')
  const [proposalCandidates, setProposalCandidates] = useState<TestChangeCandidatePage>()
  const [proposalPage, setProposalPage] = useState(1)
  const [proposalBusy, setProposalBusy] = useState(false)
  const proposalTriggerRef = useRef<HTMLButtonElement>(null)
  const proposalWasOpen = useRef(false)

  const scope = scopeOf(discipline, level)
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
        `${api}${artifactApiRoot}?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}` +
        (artifactKindFilter === 'all' ? '' : `&artifactKind=${artifactKindFilter}`) +
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
      setError(operationError(problem, `The ${currentArtifactWord} library could not be loaded.`))
    }
  }, [api, artifactApiRoot, artifactKindFilter, projectId, releaseId, scope, query, procedureState, procedureOutcome, page, pageSize, documentId, sectionId, currentArtifactWord])

  // The documents this discipline's procedures are written into. Read once per project and scope: the rail
  // is structure, not a result set, and re-reading it on every keystroke would make it flicker.
  useEffect(() => {
    let active = true
    fetch(`${api}/api/projects/${projectId}/${isSystemScope ? 'test-procedure-documents' : 'test-artifacts'}?scope=${discipline}`)
      .then(response => response.ok ? response.json() : [])
      .then((value: ProcedureDocument[]) => { if (active) setDocuments(value) })
      .catch(() => { if (active) setDocuments([]) })
    return () => { active = false }
  }, [api, projectId, discipline, isSystemScope])
  useEffect(() => { void load() }, [load])

  /**
   * Saved view lifecycle, the same contract the requirements workspace keeps. The server is the authority —
   * it answers Not Found for a view that is not yours — so these read the failure the API reports rather
   * than deciding locally who may do what.
   */
  const mutateView = async (view: SavedView, method: 'PUT' | 'DELETE', body?: unknown) => {
    setError('')
    try {
      await apiRequest(`${api}/api/test-cases/views/${view.id}`, {
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
       setLevel(validLevel(saved.level || null, discipline, ladder))
       setDocumentId(saved.documentId || '')
       setSectionId(saved.sectionId || '')
      setArtifactKindFilter(saved.artifactKind === 'Procedure' ? 'Procedure' : saved.artifactKind === 'all' ? 'all' : 'Case')
      setViewId(view.id)
      setPage(1)
    } catch {
      setError('Saved view configuration is invalid.')
    }
  }, [discipline, ladder])
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
      await apiRequest(`${api}/api/test-cases/views`, {
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
            level,
            documentId,
            sectionId,
            artifactKind: artifactKindFilter,
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
    if (discipline !== 'System') {
      for (const suffix of ['', 'State', 'Outcome', 'Level', 'Page', 'Rows', 'Document', 'Section', 'View', 'Id', 'RevisionId', 'Tab']) {
        params.delete(`procedure${suffix}`)
        params.delete(`case${suffix}`)
      }
    }
    apply(queryKey(), selectedId ? selectedDisplayNumber : query)
    if (discipline !== 'System') apply('artifactKind', artifactKindFilter === 'all' ? '' : artifactKindFilter)
    apply(queryKey('State'), procedureState)
    apply(queryKey('Outcome'), procedureOutcome)
    apply(queryKey('Level'), level === discipline ? '' : level)
    apply(queryKey('Page'), page > 1 ? String(page) : '')
    apply(queryKey('Rows'), pageSize === 25 ? '' : String(pageSize))
    apply(queryKey('Document'), documentId)
    apply(queryKey('Section'), sectionId)
    apply(queryKey('View'), viewId)
    apply(queryKey('Id'), selectedId)
    apply(queryKey('RevisionId'), selectedRevisionId)
    apply(queryKey('Tab'), tab === 'details' ? '' : tab)
    // Seeded from what the address already says, so the reader's first change after a reload still earns a
    // history entry rather than being mistaken for arrival.
    const discrete = `${level}|${artifactKindFilter}|${procedureState}|${procedureOutcome}|${page}|${pageSize}|${documentId}|${sectionId}|${viewId}`
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
  }, [discipline, level, query, artifactKindFilter, procedureState, procedureOutcome, page, pageSize, documentId, sectionId, viewId, selectedId, selectedDisplayNumber, selectedRevisionId, tab, queryKey])

  // The browser's own navigation must move the list, not just the address bar.
  useEffect(() => {
    const restore = () => {
      const params = new URLSearchParams(location.search)
      setQuery(queryValue(params) ?? '')
      setProcedureState(queryValue(params, 'State') ?? '')
      setProcedureOutcome(queryValue(params, 'Outcome') ?? '')
      const kind = params.get('artifactKind')?.toLowerCase()
      setArtifactKindFilter(kind === 'case' ? 'Case' : kind === 'procedure' ? 'Procedure' : 'all')
      setLevel(validLevel(queryValue(params, 'Level'), discipline, ladder))
      setPage(Number(queryValue(params, 'Page') ?? '1') || 1)
      setPageSize(Number(queryValue(params, 'Rows') ?? '25') || 25)
      setDocumentId(queryValue(params, 'Document') ?? '')
      setSectionId(queryValue(params, 'Section') ?? '')
      setViewId(queryValue(params, 'View') ?? '')
      setSelectedId(queryValue(params, 'Id') ?? '')
      setSelectedRevisionId(queryValue(params, 'RevisionId') ?? '')
      setSelectedDisplayNumber(queryValue(params) ?? '')
      const seeded = queryValue(params, 'Tab')
      setTab(seeded === 'trace' || seeded === 'history' || seeded === 'discussion' ? seeded : 'details')
    }
    addEventListener('popstate', restore)
    return () => removeEventListener('popstate', restore)
  }, [discipline, ladder, queryValue])

  const procedures = data?.items ?? []
  // Read once, defensively. A build with no effective procedures answers with a deliberately empty page, and
  // a rail that crashes the workspace because a field it wanted was absent takes the whole page down with it.
  const savedViews = data?.views ?? []
  // Keyed on the page rather than the derived array, so the identity stays stable between renders. The
  // history and discussion effects watch this object, and a fresh one every render would refetch forever.
  const selected = useMemo(() => data?.items.find(x => x.id === selectedId), [data, selectedId])
  const selectedArtifactApiRoot = verificationArtifactApiRoot(isSystemScope ? 'System' : 'Software', selected?.artifactKind)
  const selectedRevision = selectedRevisionId || selected?.revisionId || ''
  const selectedIsProcedure = selected?.artifactKind === 'Procedure'
  const selectedIsSoftwareProcedure = selectedIsProcedure && !isSystemScope
  const selectedArtifactWord = selectedIsProcedure ? 'test procedure' : 'test case'
  const selectedArtifactShortWord = selectedIsProcedure ? 'procedure' : 'case'

  // Loaded when the tab is opened rather than with the list. A reader browsing forty procedures does not need
  // forty revision histories fetched on their behalf.
  useEffect(() => {
    if (!selected || tab !== 'history') return
    let active = true
    void (async () => {
      try {
        const response = await fetch(
           `${api}${selectedArtifactApiRoot}/${selected.id}/history?releaseId=${releaseId}&revisionId=${selectedRevision}`)
        if (response.ok && active) setHistory(await response.json())
      } catch { if (active) setHistory(undefined) }
    })()
    return () => { active = false }
  }, [api, selectedArtifactApiRoot, releaseId, selected, selectedRevision, tab])

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
           `${api}${selectedArtifactApiRoot}/${selected.id}/trace?releaseId=${releaseId}&revisionId=${selectedRevision}`)
        if (response.ok && active) setTrace(await response.json())
        else if (active) setTraceError(true)
      } catch { if (active) setTraceError(true) }
    })()
    return () => { active = false }
  }, [api, selectedArtifactApiRoot, releaseId, selected, selectedRevision, tab])

  const loadComments = useCallback(async (procedureId: string) => {
    try {
      const response = await fetch(`${api}${selectedArtifactApiRoot}/${procedureId}/comments`)
      if (response.ok) setComments(await response.json())
    } catch { setComments([]) }
  }, [api, selectedArtifactApiRoot])
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
      await apiRequest(`${api}${selectedArtifactApiRoot}/${selected.id}/comments`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ releaseId, revisionId: selected.revisionId, body, mentions }),
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
        body: JSON.stringify({ releaseId, disposition }),
      })
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be resolved.')) }
  }

  // Coverage is an advanced report over the same inventory, not a second page mode. It stays behind the
  // same Advanced control the Requirements Explorer uses, so the two Explorers remain identical by default
  // without deleting the governed suspect-coverage workflow.
  useEffect(() => {
    setShowAdvanced(false)
    setCoverage(undefined)
    setCoverageRead(false)
    setShowAllCoverage(false)
  }, [api, projectId, releaseId, scope, artifactKindFilter])

  useEffect(() => {
    if ((!isSystemScope && artifactKindFilter !== 'Case') || !showAdvanced || coverageRead) return
    let active = true
    void (async () => {
      const { coverage: next, failed } = await loadCoverage(api, projectId, releaseId, scope)
      if (!active) return
      setCoverageRead(true)
      if (next) setCoverage(next)
      if (failed) setError('The requirement coverage for this build could not be read.')
    })()
    return () => { active = false }
  }, [api, projectId, releaseId, scope, showAdvanced, coverageRead, artifactKindFilter, isSystemScope])

  const uncovered = coverage?.items.filter(item => item.disposition === 'Uncovered') ?? []
  const suspect = coverage?.items.filter(item => item.disposition === 'Suspect') ?? []

  const open = (procedure: Procedure) => {
    setSelectedId(procedure.id); setSelectedRevisionId(procedure.revisionId); setTab('details'); setHistory(undefined); setTrace(undefined); setTraceError(false)
    const params = new URLSearchParams(location.search)
    if (discipline !== 'System')
      for (const suffix of ['', 'Id', 'RevisionId', 'Tab']) params.delete(`procedure${suffix}`)
    const exactDisplayNumber = procedure.displayNumber.includes('.')
      ? procedure.displayNumber
      : `${procedure.displayNumber}.${String(procedure.revision ?? 0).padStart(2, '0')}`
    setSelectedDisplayNumber(exactDisplayNumber)
    params.set(queryKey(), exactDisplayNumber)
    params.set(queryKey('Id'), procedure.id)
    params.set(queryKey('RevisionId'), procedure.revisionId)
    params.delete(queryKey('Tab'))
    window.history.replaceState({}, '', `${location.pathname}?${params}`)
  }

  const proposalContext = useMemo(() => selected ? {
    projectId, releaseId, artifactId: selected.id, artifactRevisionId: selected.revisionId,
    artifactKind: selected.artifactKind === 'Procedure' || discipline === 'System' ? 'Procedure' as const : 'Case' as const,
    artifactLevel: selected.level,
    displayNumber: selected.displayNumber,
  } : undefined, [discipline, projectId, releaseId, selected])
  const openProposal = () => {
    if (!selected || released) return
    setProposalSearch('')
    setProposalPage(1)
    setProposalCandidates(undefined)
    setProposalOpen(true)
  }
  const loadProposalCandidates = useCallback(async () => {
    if (!proposalOpen || !proposalContext) return
    try {
      const params = new URLSearchParams({ projectId, releaseId, artifactRevisionId: proposalContext.artifactRevisionId,
        page: String(proposalPage), pageSize: '25' })
      if (proposalSearch.trim()) params.set('search', proposalSearch.trim())
      const response = await fetch(`${api}/api/verification-artifacts/${proposalContext.artifactId}/test-change-request-candidates?${params}`)
      if (!response.ok) throw new Error(String(response.status))
      setProposalCandidates(await response.json())
    } catch (problem) {
      setError(operationError(problem, 'Eligible Test Change Requests could not be loaded.'))
      setProposalCandidates(undefined)
    }
  }, [api, projectId, releaseId, proposalOpen, proposalContext, proposalSearch, proposalPage])
  useEffect(() => { void loadProposalCandidates() }, [loadProposalCandidates])
  useEffect(() => {
    if (proposalOpen) {
      proposalWasOpen.current = true
      const dismiss = (event: KeyboardEvent) => {
        if (event.key === 'Escape' && !proposalBusy) {
          event.preventDefault()
          setProposalOpen(false)
        }
      }
      window.addEventListener('keydown', dismiss)
      const frame = window.requestAnimationFrame(() => {
        const dialog = document.querySelector<HTMLElement>('[aria-labelledby="test-change-dialog-title"]')
        dialog?.querySelector<HTMLElement>('button, input, [tabindex]:not([tabindex="-1"])')?.focus()
      })
      return () => { window.cancelAnimationFrame(frame); window.removeEventListener('keydown', dismiss) }
    }
    if (proposalWasOpen.current) {
      proposalWasOpen.current = false
      proposalTriggerRef.current?.focus()
    }
  }, [proposalBusy, proposalOpen])
  const selectProposal = async (candidate: TestChangeCandidate) => {
    if (!proposalContext) return
    if (!candidate.eligible && candidate.existingProposalId) {
      onOpenTestChangeRequest?.({ ...proposalContext, mode: 'existing', testChangeReviewId: candidate.id, proposalId: candidate.existingProposalId })
      setProposalOpen(false)
      return
    }
    if (!candidate.eligible) return
    setProposalBusy(true)
    setError('')
    try {
      const response = await apiRequest<{ testChangeReviewId: string; proposalId: string }>(`${api}/api/verification-artifacts/${proposalContext.artifactId}/test-change-request-proposal`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ projectId, releaseId, artifactRevisionId: proposalContext.artifactRevisionId,
          testChangeReviewId: candidate.id, expectedVersion: candidate.version }),
      })
      onOpenTestChangeRequest?.({ ...proposalContext, mode: 'existing', testChangeReviewId: response.testChangeReviewId, proposalId: response.proposalId })
      setProposalOpen(false)
    } catch (problem) {
      setError(operationError(problem, 'The test artifact could not be added to that Draft. Refresh and try again.'))
    } finally { setProposalBusy(false) }
  }
  const close = () => {
    setSelectedId('')
    setSelectedRevisionId('')
    setSelectedDisplayNumber('')
    const params = new URLSearchParams(location.search)
    for (const suffix of ['Id', 'RevisionId', 'Tab']) {
      params.delete(queryKey(suffix))
      if (discipline !== 'System') params.delete(`procedure${suffix}`)
    }
    window.history.replaceState({}, '', `${location.pathname}${params.size ? `?${params}` : ''}`)
  }
  // The tab is part of the address, so a direct deep link to a procedure's Trace & impact tab reopens the
  // same trace context after a refresh. Deliberately a replace: choosing a tab is not somewhere the reader
  // went so much as how they are reading the procedure they already chose.
  const selectTab = (next: Tab) => {
    setTab(next)
    const params = new URLSearchParams(location.search)
    if (discipline !== 'System') params.delete('procedureTab')
    if (next === 'details') params.delete(queryKey('Tab')); else params.set(queryKey('Tab'), next)
    window.history.replaceState({}, '', `${location.pathname}${params.size ? `?${params}` : ''}`)
  }

  // A workspace is its own <main>: the shell supplies the navigation and context bar, not the landmark.
  return <main className="reqWorkspace">
    <ControlledArtifactExplorerHeader
      back={onBack ? { label: 'Command Center', onClick: onBack } : undefined}
      eyebrow={`CONTROLLED ${currentArtifactPlural.toUpperCase()} / READ-ONLY EXPLORER`}
      title={discipline === 'System' ? 'System Test Procedure Explorer' : 'Software Test Case/Procedure Explorer'}
    />
    {error && <div className="workspaceError" role="alert">{error}</div>}

    {/* The document these procedures are written into, offered where they are read — the same place, shape
        and rule the requirements Explorer uses. Which one you get follows the build: approved for a released
        one, a stamped draft for an in-work one. */}
    {procedureDocumentsEnabled && <DocumentActions
      api={api}
      projectId={projectId}
      release={{ id: releaseId, version: releaseVersion, isReleased: released }}
      targets={procedureDocumentTargets}
      heading={released ? `Approved documents for ${releaseVersion}` : `Draft documents for ${releaseVersion}`}
    />}

    <>
    {/* Browsing, not just searching. The software side of the demonstration Program carries 440 procedures,
        so a list that could only be searched meant knowing the number of the thing you were looking for
        before you could look for it. State and latest result are how somebody actually narrows this: "the
        drafts", "what failed last time". */}
    <section className="reqCommand">
      {/* Inline, unlabelled controls, as the requirements Explorer has: a filter bar reads as one row of
          things you can narrow by, and a caption stacked over every control turns it into a form. The names
          are carried by `aria-label`, so nothing is lost to a screen reader or to a test. */}
      <div className="reqSearch procedureFind">
        <span>⌕</span>
        <input aria-label={`Find a ${currentArtifactDisplayWord}`} value={query}
          onChange={event => { setQuery(event.target.value); setPage(1) }}
          placeholder="Search any identifier fragment or title…" />
        {/* The count belongs on the search, where the requirements Explorer puts it: a filtered list whose
            size you cannot see is a list you cannot trust you have read all of. */}
        <kbd className="resultCount">{(data?.totalCount ?? 0).toLocaleString()} found</kbd>
      </div>
      {discipline === 'Software' && (
        <select aria-label="Level filter"
          value={level}
          onChange={event => {
            setLevel(event.target.value as ProcedureLevel)
            setDocumentId(''); setSectionId(''); setPage(1)
          }}>
          <option value="Software">All software {currentArtifactPlural}</option>
           {ladderAllows(ladder, 'HighLevel', LadderCapability.Verification) && <option value="HighLevel">Software HLR</option>}
           {ladderAllows(ladder, 'LowLevel', LadderCapability.Verification) && <option value="LowLevel">Software LLR</option>}
       </select>
      )}
      {discipline === 'Software' && <select aria-label="Artifact filter" value={artifactKindFilter}
        onChange={event => { setArtifactKindFilter(event.target.value as 'all' | 'Case' | 'Procedure'); setDocumentId(''); setSectionId(''); setPage(1) }}>
        <option value="all">All test artifacts</option>
        {caseEnabled && <option value="Case">Test cases</option>}
        {procedureEnabled && <option value="Procedure">Test procedures</option>}
      </select>}
      <select aria-label={`${currentArtifactDisplayWord} state`} value={procedureState}
        onChange={event => { setProcedureState(event.target.value); setPage(1) }}>
        <option value="">All states</option>
        <option value="Draft">Draft</option>
        <option value="InReview">In review</option>
        <option value="Approved">Approved</option>
      </select>
      <select aria-label="Latest result" value={procedureOutcome}
        onChange={event => { setProcedureOutcome(event.target.value); setPage(1) }}>
        <option value="">All outcomes</option>
        <option value="Pass">Pass</option>
        <option value="Fail">Fail</option>
        <option value="Blocked">Blocked</option>
      </select>
      {(isSystemScope || artifactKindFilter === 'Case') && <button type="button" className={showAdvanced ? 'advanced active' : 'advanced'}
        aria-expanded={showAdvanced} onClick={() => setShowAdvanced(current => !current)}>
        Advanced
      </button>}
      <button type="button" className="clear"
         disabled={level === discipline && artifactKindFilter === 'all' && !query && !procedureState && !procedureOutcome && !documentId && !sectionId && !viewId}
        onClick={() => {
          setViewId('')
           setQuery(''); setProcedureState(''); setProcedureOutcome(''); setArtifactKindFilter('all')
          setLevel(discipline)
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
    </section>

      {(isSystemScope || artifactKindFilter === 'Case') && showAdvanced && <div className="explorerCoverage" aria-label={`Advanced ${currentArtifactDisplayWord} coverage`}>
      <section className="coverageSummary" aria-label="Coverage summary">
        <article><b>{coverage?.total ?? 0}</b><span>Requirements</span></article>
        <article><b>{coverage?.covered ?? 0}</b><span>With a {currentArtifactDisplayWord}</span></article>
        <article className={uncovered.length ? 'attention' : ''}><b>{uncovered.length}</b><span>With none</span></article>
        <article className={suspect.length ? 'attention' : ''}><b>{suspect.length}</b><span>Suspect coverage</span></article>
      </section>

      {(uncovered.length > 0 || suspect.length > 0) && <section className="coverageCard">
        <div className="cardTitle">
          <h2>Requirements needing attention</h2>
          <p>A requirement with no {currentArtifactDisplayWord} cannot be verified, and coverage carried across a change nobody reconfirmed does not count.</p>
        </div>
        {uncovered.slice(0, 25).map(item => <article className="coverageRow attention" key={item.revisionId}>
          <div><b>{item.displayNumber}</b><i>No {currentArtifactDisplayWord}</i></div>
          <p>{item.statement}</p>
        </article>)}
        {suspect.slice(0, 25).map(item => <article className="coverageRow attention" key={`suspect-${item.revisionId}`}>
          <div><b>{item.displayNumber}</b><i>Suspect</i></div>
          <p>{item.statement}</p>
          <small>Covered by {item.coveredBy.map(procedure => procedure.displayNumber).join(', ')}, written against earlier wording.</small>
        </article>)}
      </section>}

      <section className="coverageCard">
        <div className="cardTitle">
          <h2>Requirement coverage</h2>
          <p>Every effective requirement in this build and the {currentArtifactShortPlural} that verify it.</p>
        </div>
        <button type="button" className="quiet" onClick={() => setShowAllCoverage(current => !current)}>
          {showAllCoverage ? 'Show only what needs attention' : `Show all ${coverage?.total ?? 0} requirements`}
        </button>
        {showAllCoverage && <div className="fullCoverage">
          {(coverage?.items ?? []).map(item => <article
            className={`coverageRow ${item.covered ? '' : 'attention'}`} key={`all-${item.revisionId}`}>
            <div>
              <b>{item.displayNumber}</b>
              <i>{item.verified ? 'Verified'
                : item.coveredBy.some(procedure => procedure.coverageState === 'Suspect') ? 'Suspect'
                : item.covered ? 'Covered'
                : `No ${currentArtifactDisplayWord}`}</i>
            </div>
            <p>{item.statement}</p>
            {item.coveredBy.length > 0 && <small>
              {item.coveredBy.map(procedure => `${procedure.displayNumber} (${procedure.state})`).join(', ')}
            </small>}
          </article>)}
        </div>}
      </section>

      {coverageRead && !coverage && <p className="coverageNone">
        This build has not materialized its requirements, so there is nothing to report coverage against yet.
      </p>}
    </div>}

    <ControlledArtifactExplorerLayout inspecting resizableKey="test-procedure-explorer">
      {/* The documents these procedures are written into, in the place and shape the requirements Explorer
          puts its specifications. Procedures had no container until they were given one; this is the rail
          that was impossible before. */}
      <div className="specRail">
      {procedureDocumentsEnabled && <nav className="procedureDocumentRail" aria-label={`${currentArtifactWord} documents`}>
        <div className="railTitle"><b>Documents</b><span>{documents.length}</span></div>
        <button type="button" className={!documentId && !sectionId ? 'railEntry selected' : 'railEntry'}
          aria-pressed={!documentId && !sectionId}
          onClick={() => { setDocumentId(''); setSectionId(''); setPage(1) }}>
          <b>All {currentArtifactShortPlural}</b>
          <small>{(data?.totalCount ?? 0).toLocaleString()} in this build</small>
        </button>
        {documents.map(document => (
          <div key={document.id} className="railDocument">
            <button type="button" data-document={document.documentNumber}
              className={documentId === document.id && !sectionId ? 'railEntry selected' : 'railEntry'}
              aria-pressed={documentId === document.id && !sectionId}
              onClick={() => { setDocumentId(document.id); setSectionId(''); setLevel(discipline); setPage(1) }}>
              <b>{document.documentNumber}</b>
              <small>{document.artifactCount} · {document.title}</small>
            </button>
            {document.sections.map(section => (
              <button type="button" key={section.id} className={sectionId === section.id ? 'railSection selected' : 'railSection'}
                aria-pressed={sectionId === section.id}
                onClick={() => { setDocumentId(document.id); setSectionId(section.id); setLevel(discipline); setPage(1) }}>
                {section.heading} <i>{section.artifactCount}</i>
              </button>
            ))}
          </div>
        ))}
        {documents.length === 0 && (
          <p className="railEmpty">No {currentArtifactDisplayWord} document for this discipline yet.</p>
        )}
      </nav>}

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

      <section className="reqResults" aria-label={currentArtifactPlural}>
        {/* What the requirements Explorer puts above its list, in the same markup and from the same
            stylesheet: how many records answer, where in them you are, and that the index is live and
            permission-aware. The count alone was in the search box; where you were in the results was
            nowhere. */}
        <div className="resultSummary">
          <div>
            <b>{(data?.totalCount ?? 0).toLocaleString()} {currentArtifactShortPlural}</b>
            <span>
              {!data
                ? 'Refreshing controlled index…'
                : `Page ${data.page} of ${data.totalPages} · exact current revisions`}
            </span>
          </div>
          <div className="confidence">
            <i /> Live counts · respects your access
          </div>
        </div>
        {procedures.length === 0
          ? <p className="procedureEmpty">{query || procedureState || procedureOutcome || documentId || sectionId
            ? `No ${currentArtifactDisplayWord} matches that. Clear the search or the filters to see the rest.`
            : `This build has no controlled ${disciplineLabel(discipline).toLowerCase()} ${currentArtifactPlural} yet.`}</p>
          : (
            <div className="reqTable procedureList" role="table" aria-label={`Controlled ${currentArtifactPlural}`}>
              <div className="reqTableHead" role="row">
                <span role="columnheader">Identifier &amp; title</span>
                 <span role="columnheader">Artifact</span>
                 <span role="columnheader">Level</span>
                <span role="columnheader">Parent / Verifies</span>
                <span role="columnheader">Latest result</span>
                <span role="columnheader">State</span>
                <span role="columnheader">Discussion</span>
              </div>
                {procedures.map(procedure => (
                  // `procedureRow` is kept on the table row. It is the hook the procedure journeys have
                  // always selected a row by — bounded rendering, deep links and the trace all reach a
                  // procedure through it — and the list becoming a table is a change of presentation, not of
                  // what a row is.
                  // The whole row opens the procedure, as it did when the row was itself a button. Anywhere
                  // in a record's row is where people click; the identifier cell keeps its own button so the
                  // row is still reachable and operable from the keyboard.
                  <article key={procedure.id} role="row" data-procedure={procedure.displayNumber}
                    className={procedure.id === selectedId ? 'procedureRow selected' : 'procedureRow'}>
                      <button type="button" aria-pressed={procedure.id === selectedId}
                        onClick={() => open(procedure)}>
                        <b>{procedure.displayNumber}</b>
                        <p>{procedure.title}</p>
                      </button>
                    <span className={`artifactBadge ${procedure.artifactKind?.toLowerCase() ?? 'unknown'}`}>{procedure.artifactLabel ?? procedure.artifactKind ?? 'Artifact'}</span>
                    <span>{procedureLevelLabel(procedure.level)}</span>
                    <span>{procedure.artifactKind === 'Procedure' && procedure.level !== 'System'
                      ? procedure.parentCount ?? 0
                      : procedure.requirementCount ?? 0}</span>
                    <span>{procedure.lastOutcome ?? 'Not run'}</span>
                    <i className={procedure.state.toLowerCase()}>{stateLabel(procedure.state)}</i>
                    <span>○ 0</span>
                  </article>
                ))}
            </div>
          )}
        <div className="pager">
          <button disabled={(data?.page ?? 1) <= 1} onClick={() => setPage(x => x - 1)}>← Previous</button>
          <span>
            {(data?.totalCount ?? 0) > 0
              ? `${((data?.page ?? 1) - 1) * (data?.pageSize ?? 25) + 1}–` +
                `${Math.min((data?.page ?? 1) * (data?.pageSize ?? 25), data?.totalCount ?? 0)} ` +
                `of ${(data?.totalCount ?? 0).toLocaleString()}`
              : `0 ${currentArtifactShortPlural}`}
          </span>
          <button disabled={(data?.page ?? 1) >= (data?.totalPages ?? 1)}
            onClick={() => setPage(x => x + 1)}>Next →</button>
        </div>
      </section>

      {selected ? <ControlledArtifactInspector
        artifactType={`${procedureLevelLabel(selected.level).toUpperCase()} ${selectedArtifactWord.toUpperCase()}`}
        displayNumber={selected.displayNumber}
         closeLabel={`Close ${selectedArtifactShortWord} detail`}
        onClose={close}
        tabs={[
          { id: 'details', label: 'Overview' },
          { id: 'trace', label: <>Trace &amp; impact</> },
          { id: 'history', label: 'History' },
          { id: 'discussion', label: <>Discussion <span>{comments.length}</span></> },
        ]}
        activeTab={tab}
        onTab={next => selectTab(next as Tab)}
      >

          {tab === 'details' && (
            <div className="inspectorBody">
              {selected.titleNote && <p className="inspectorNote warn">{selected.titleNote}</p>}
              {!released
                ? <button type="button" className="impactLaunch" ref={proposalTriggerRef} onClick={openProposal}>
                    Propose test change →
                  </button>
                : <p className="changeBoundaryNote"><b>Read-only historical record — {buildName}</b><br />Exit this workspace and select an in-work build to propose a test change.</p>}
              {proposalOpen && proposalContext && (
                  <section className="artifactChangeDialog" role="dialog" aria-modal="true"
                  aria-labelledby="test-change-dialog-title" onKeyDown={event => {
                    if (event.key === 'Escape' && !proposalBusy) {
                      event.preventDefault()
                      setProposalOpen(false)
                    }
                  }}>
                  <div className="artifactChangeDialogHead">
                    <div>
                      <span className="eyebrow">VERIFICATION CHANGE CONTROL</span>
                      <h3 id="test-change-dialog-title">Propose {proposalContext.displayNumber}</h3>
                    </div>
                    <button type="button" className="quiet" onClick={() => setProposalOpen(false)}>Close</button>
                  </div>
                  <p>Choose a Test Change Request. The selected exact {proposalContext.artifactKind.toLowerCase()} revision remains unchanged.</p>
                  <div className="artifactChangeDialogActions">
                    <button type="button" onClick={() => {
                      onOpenTestChangeRequest?.({ ...proposalContext, mode: 'new' })
                      setProposalOpen(false)
                    }}>
                      Raise new Test Change Request
                    </button>
                    <label>Find an existing Draft
                      <input aria-label="Find an existing Test Change Request" value={proposalSearch}
                        onChange={event => setProposalSearch(event.target.value)} placeholder="Identifier or title" />
                    </label>
                  </div>
                  {!proposalCandidates
                    ? <p className="inspectorNote">Loading Test Change Request candidates…</p>
                    : proposalCandidates.items.length === 0
                      ? <p className="inspectorNote">No Test Change Requests match that search.</p>
                      : <ul className="artifactChangeCandidateList" aria-label="Test Change Request candidates">
                        {proposalCandidates.items.map(candidate => <li key={candidate.id}>
                          <div>
                            <b>{candidate.displayNumber}</b><span>{candidate.title || 'Untitled Test Change Request'}</span>
                            <small>{candidate.state} · {candidate.artifactKey}</small>
                            {!candidate.eligible && <p className="inspectorNote warn">{candidate.reason}</p>}
                          </div>
                          <button type="button" disabled={proposalBusy || (!candidate.eligible && !candidate.existingProposalId)}
                            onClick={() => void selectProposal(candidate)}>
                            {candidate.existingProposalId ? 'Open existing proposal' : 'Add exact revision'}
                          </button>
                        </li>)}
                      </ul>}
                  {proposalCandidates && proposalCandidates.totalPages > 1 && (
                    <nav className="pager artifactChangeCandidatePager" aria-label="Test Change Request candidate pages">
                      <button type="button" disabled={proposalBusy || proposalPage <= 1}
                        onClick={() => setProposalPage(value => Math.max(1, value - 1))}>← Previous</button>
                      <span>Page {proposalCandidates.page} of {proposalCandidates.totalPages} · {proposalCandidates.totalCount} total</span>
                      <button type="button" disabled={proposalBusy || proposalPage >= proposalCandidates.totalPages}
                        onClick={() => setProposalPage(value => Math.min(proposalCandidates.totalPages, value + 1))}>Next →</button>
                    </nav>
                  )}
                </section>
              )}
               <h3>{selectedIsProcedure ? 'Procedure' : 'Case'} title</h3>
              <div className="richRequirement">{selected.title}</div>
              <dl className="procedureCase">
                <dt>Objective</dt><dd>{selected.objective || 'Not recorded'}</dd>
                <dt>Preconditions</dt><dd>{selected.preconditions || 'None'}</dd>
                <dt>Steps</dt><dd>{selected.steps || 'Not recorded'}</dd>
                <dt>Expected result</dt><dd>{selected.expectedResult || 'Not recorded'}</dd>
                {selectedIsProcedure && <>
                  <dt>Environment / setup</dt><dd>{selected.environmentSetup || 'Not recorded'}</dd>
                  <dt>Test data</dt><dd>{selected.testData || 'Not recorded'}</dd>
                  <dt>Ordered executable steps</dt><dd>{selected.orderedSteps || 'Not recorded'}</dd>
                  <dt>Expected observations</dt><dd>{selected.expectedObservations || 'Not recorded'}</dd>
                  <dt>Cleanup</dt><dd>{selected.cleanup || 'Not recorded'}</dd>
                  <dt>Tooling / automation</dt><dd>{selected.toolingAutomation || 'Not recorded'}</dd>
                  <dt>Parent classification</dt><dd>{selected.parentKind || 'Not recorded'}</dd>
                  {selected.derivedRationale && <><dt>Derived rationale</dt><dd>{selected.derivedRationale}</dd></>}
                </>}
                <dt>State</dt><dd>{selected.state}</dd>
                <dt>Owner</dt><dd><PersonName userName={selected.ownerId} /></dd>
              </dl>
            </div>
          )}

          {tab === 'trace' && (
            <div className="inspectorBody">
              {/* A Case traces to the requirements it verifies. A software Procedure traces to its exact Case
                  parents and their Case <-> Procedure lifecycle; System remains the direct requirement trace. */}
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
                  {selectedIsProcedure && selected.level !== 'System' ? (
                    trace.caseParents?.length ? <>
                      <p className="inspectorNote">This Procedure runs against {trace.caseParents.length} exact Case parent{trace.caseParents.length === 1 ? '' : 's'}.</p>
                      <ul className="traceRequirements" aria-label="Exact Case parents">
                        {trace.caseParents.map(parent => <li key={parent.linkId} className="traceRequirement">
                          <div className="traceRequirementHead">
                            <b>{parent.displayNumber ?? parent.caseRevisionId}</b>
                            <span>Case</span>
                            <i className="traceCoverageBadge confirmed">{parent.state}</i>
                          </div>
                          {parent.title && <p>{parent.title}</p>}
                          <small>Exact Case revision {parent.caseRevisionId}</small>
                          {parent.outcome && <small>Disposition: {parent.outcome}</small>}
                          <ExactLinkLifecyclePanel api={api} routeRoot="case-procedure-links"
                            linkId={parent.linkId} initialState={parent.state} />
                        </li>)}
                      </ul>
                    </> : <p className="inspectorNote warn">No exact Case parent is linked to this Procedure revision.</p>
                  ) : <>
                    <p className="inspectorNote">
                      This {trace.level === 'System' ? 'procedure' : 'Case'} verifies {trace.requirements.length} requirement{trace.requirements.length === 1 ? '' : 's'}.
                    </p>
                    {trace.requirements.length === 0 ? (
                      <p className="inspectorNote warn">
                        Nothing is verified by {trace.displayNumber}. Either it has not been linked yet, or the
                        requirement it was written against has been retired.
                      </p>
                    ) : <ul className="traceRequirements">
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
                    </ul>}
                  </>}
                </>
              ) : traceError ? (
                <p className="inspectorNote warn">The trace for this {selectedArtifactShortWord} revision could not be loaded.</p>
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
                    {selectedIsSoftwareProcedure && <span className="revisionDriver">
                      {revision.parentKind ?? 'Unspecified'} · {revision.caseRevisionIds?.length ?? 0} exact Case parent{revision.caseRevisionIds?.length === 1 ? '' : 's'}
                      {revision.derivedRationale ? ` · ${revision.derivedRationale}` : ''}
                    </span>}
                    {selectedIsSoftwareProcedure && revision.caseParents?.map(parent =>
                      <div key={parent.linkId}>
                        <span className="revisionDriver">
                          Exact Case {parent.caseRevisionId} · relationship {parent.state}
                          {parent.outcome ? ` · ${parent.outcome}` : ''}
                        </span>
                        <ExactLinkLifecyclePanel api={api} routeRoot="case-procedure-links"
                          linkId={parent.linkId} initialState={parent.state}
                          onChanged={async () => {
                            const response = await fetch(
                              `${api}${artifactApiRoot}/${selected.id}/history?revisionId=${selected.revisionId}`)
                            if (response.ok) setHistory(await response.json())
                          }} />
                      </div>)}
                    {revision.retirementRationale && <span className="inspectorNote">Retirement: {revision.retirementRationale}</span>}
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
                  {!comment.revisionId && (
                    <small className="inspectorNote">Historical comment has no exact revision context; it is read-only.</small>
                  )}
                  <footer>
                    <i>{stateLabel(comment.state)}</i>
                    {comment.revisionId && comment.state === 'Open' && !released && (
                      <button onClick={() => void resolveComment(comment.id)}>Resolve / disposition</button>
                    )}
                  </footer>
                </article>
              ))}
            </div>
          )}
      </ControlledArtifactInspector> : <ControlledArtifactInspectorEmpty
        title={currentArtifactNoun()}
        description={`Choose a controlled ${currentArtifactWord} to review its overview, trace, history, and discussion.`}
      />}
    </ControlledArtifactExplorerLayout>
    </>
  </main>
}
