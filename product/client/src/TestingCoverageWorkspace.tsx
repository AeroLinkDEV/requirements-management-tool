import { useCallback, useEffect, useRef, useState } from 'react'
import { PersonName } from './People'
import { SignatureDialog } from './IdentityCenter'
import PersonPicker from './PersonPicker'
import ProblemReportPicker, { type ProblemReportOption } from './ProblemReportPicker'
import ControlledProcedureEditor from './ControlledProcedureEditor'
import type { AuthUser } from './IdentityCenter'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import type { TestDiscipline } from './TestResultsWorkspace'
import './TestingCoverageWorkspace.css'

type CoverageItem = {
  revisionId: string
  displayNumber: string
  statement: string
  covered: boolean
  verified: boolean
  disposition: 'Covered' | 'Suspect' | 'Uncovered'
  coveredBy: { procedureId: string; revisionId: string; displayNumber: string; title: string; state: string; coverageState: 'Confirmed' | 'Suspect' }[]
}
type Coverage = { total: number; covered: number; suspect: number; verified: number; uncovered: number; items: CoverageItem[] }
type ChangeRequestCover = { id: string; number: string; originating: boolean }
type TestChangeRequest = {
  id: string
  displayNumber: string
  discipline: string
  state: string
  assignedEngineerId?: string
  selectedApproverId?: string
  totalItems: number
  resolvedItems: number
  coveredChangeRequests: ChangeRequestCover[]
  problemReports?: ProblemReportOption[]
  capabilities: { canAssign: boolean; canDecide: boolean; canSubmit: boolean; canApprove: boolean; canReturn: boolean }
}
type Procedure = { id: string; revisionId: string; displayNumber: string; title: string; state: string; requirementCount: number; ownerId: string; selectedApproverId?: string }
type ImpactItem = {
  id: string
  testChangeReviewId: string
  trigger: string
  state: string
  subjectDisplayNumber: string
  declaredVerificationMethod: string
  assignedEngineerId?: string
  outcome?: string
  resolutionRationale: string
  holdsRelease?: boolean
  decisionHistory: { id: string; action: string; outcome?: string; rationale: string; actor: string; occurredAt: string }[]
}
type Revision = {
  id: string
  displayNumber: string
  revision: number
  state: string
  authorId: string
  createdAt: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  selected: boolean
  drivenBy: { changeRequest: string; package: string; subjectDisplayNumber: string; action: string }[]
  covers: string[]
}
type History = { id: string; baseNumber: string; title: string; ownerId: string; createdAt: string; selectedRevisionId?: string; revisions: Revision[] }
type CreatedProcedure = { id: string; revisionId: string; displayNumber: string; state: string; selectedApproverId: string }

const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'Software HLR' : 'Software LLR'

/**
 * What this build's requirements are tested by, and what still has nobody looking at it.
 *
 * Two questions are asked here and they are different. "Is this requirement covered by a procedure?" is
 * about the library as it stands. "Has the test work this build's changes created been picked up?" is about
 * people and queues. A page that answered only the first would show a healthy green wall while nobody had
 * started on the changes that are about to ship.
 *
 * It is also where a test change request is picked up or started. Packages are raised automatically when a
 * change request is approved, so nothing goes unnoticed; an engineer can also raise one deliberately when a
 * set of changes is best tested together.
 */
export default function TestingCoverageWorkspace({ api, projectId, releaseId, discipline, buildName, readOnly, programId, user }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  buildName: string
  readOnly: boolean
  programId: string
  user: AuthUser
}) {
  // Authority is per Program, and it is the server that enforces it. Reflecting it here is about not offering
  // somebody a control that will refuse them — an approval they cannot give is worse than no button at all.
  const roles = user.programs.find(program => program.programId === programId)?.roles ?? []
  const canTest = !readOnly && (user.isAdministrator || roles.includes('TestEngineer'))
  const canApprove = !readOnly && (user.isAdministrator || roles.includes('Approver'))
  const [coverage, setCoverage] = useState<Coverage>()
  const [requests, setRequests] = useState<TestChangeRequest[]>([])
  const [procedures, setProcedures] = useState<Procedure[]>([])
  const [total, setTotal] = useState(0)
  const [query, setQuery] = useState(typeof location !== 'undefined' ? new URLSearchParams(location.search).get('procedure') ?? '' : '')
  const [history, setHistory] = useState<History>()
  const [impact, setImpact] = useState<ImpactItem[]>([])
  const [opened, setOpened] = useState('')
  const [resolving, setResolving] = useState<ImpactItem>()
  const [reopening, setReopening] = useState<ImpactItem>()
  const [outcome, setOutcome] = useState('ProcedureCoverageConfirmed')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState('')
  const [procedureView, setProcedureView] = useState<'record' | 'history'>('record')
  const [editing, setEditing] = useState<Procedure>()
  const [approving, setApproving] = useState<Procedure>()
  const [reviewDecision, setReviewDecision] = useState<{ request: TestChangeRequest; action: 'approve' | 'return' }>()
  const [linkingProblemReports, setLinkingProblemReports] = useState<TestChangeRequest>()
  const [problemReportIds, setProblemReportIds] = useState<string[]>([])
  const [submitting, setSubmitting] = useState<TestChangeRequest>()
  const [reviewApprover, setReviewApprover] = useState({ userId: '', name: '' })
  const [procedureApprover, setProcedureApprover] = useState({ userId: '', name: '' })
  const [showAll, setShowAll] = useState(false)
  const [revision, setRevision] = useState(0)
  // Seeded from the address, so a shared or reloaded worklist opens on the list it names rather than on the
  // unfiltered first page.
  const opening = useRef(typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams()).current
  const openingProcedureId = opening.get('procedureId') ?? ''
  const openingProcedureRevisionId = opening.get('procedureRevisionId') ?? ''
  const [procedureState, setProcedureState] = useState(opening.get('procedureState') ?? '')
  const [procedureOutcome, setProcedureOutcome] = useState(opening.get('procedureOutcome') ?? '')
  const [procedurePage, setProcedurePage] = useState(Number(opening.get('procedurePage') ?? '1') || 1)
  const lastDiscreteState = useRef<string | null>(null)
  const [requirements, setRequirements] = useState<{ revisionId: string; displayNumber: string; statement: string }[]>([])

  // One ticket per loader, not one for the page.
  //
  // Only the newest reply of a given loader may write the screen — but the two loaders here are independent,
  // and sharing a counter made each of them cancel the other. The procedure search runs on mount behind a
  // debounce, bumps the count, and the coverage load that was already in flight then discards everything it
  // read: no packages, no coverage, no impact, and no error, because nothing failed. It presented as the
  // software HLR queue being empty while the API demonstrably returned a package for that build.
  const loadTicket = useRef(0)
  const procedureTicket = useRef(0)
  const scope = discipline

  const load = useCallback(async () => {
    const mine = ++loadTicket.current

    // Coverage is asked for by configuration, not by release.
    //
    // A release is a plan; what carries requirements is a materialized baseline or the software build that
    // froze one. A build in work has neither of its own yet and carries its predecessor's, which is exactly
    // what build-context calls the effective baseline. Passing the release id here answers 400 — and because
    // a failed coverage read leaves the section empty rather than loud, it read as "this build has no
    // requirements at all" on a page whose whole job is to say what is untested.
    const context = await fetch(`${api}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)
    const effective = context.ok ? (await context.json())?.effectiveBaselineId : undefined
    const builds = await fetch(`${api}/api/builds?projectId=${projectId}`)
    const build = builds.ok ? (await builds.json()).find((x: { releaseId: string }) => x.releaseId === releaseId) : undefined
    const configuration = build ? `buildId=${build.id}` : effective ? `baselineId=${effective}` : ''

    const [coverageResponse, requestResponse, impactResponse, requirementResponse] = await Promise.all([
      configuration ? fetch(`${api}/api/verification-coverage?projectId=${projectId}&${configuration}`) : undefined,
      fetch(`${api}/api/releases/${releaseId}/test-change-reviews`),
      fetch(`${api}/api/releases/${releaseId}/verification-impact`),
      // The requirements a new procedure can be written against — read from the effective baseline rather
      // than from the coverage list, so an author is not blocked when coverage cannot be computed.
      effective
        ? fetch(`${api}/api/requirements?projectId=${projectId}&baselineId=${effective}&scope=${scope}&includeRetired=false&page=1&pageSize=200`)
        : undefined,
    ])
    const rawCoverage = coverageResponse?.ok ? await coverageResponse.json() as Coverage : undefined
    const nextRequests = requestResponse.ok ? await requestResponse.json() : undefined
    const nextImpact = impactResponse.ok ? await impactResponse.json() : undefined
    const listed = requirementResponse?.ok ? (await requirementResponse.json()).items : undefined
    if (mine !== loadTicket.current) return
    if (rawCoverage) {
      // Coverage is computed for the whole configuration; this page speaks for one discipline.
      const prefix = discipline === 'System' ? 'SYSR-' : discipline === 'HighLevelSoftware' ? 'HLR-' : 'LLR-'
      const items = rawCoverage.items.filter(x => x.displayNumber.startsWith(prefix))
      setCoverage({
        items,
        total: items.length,
        covered: items.filter(x => x.disposition === 'Covered').length,
        suspect: items.filter(x => x.disposition === 'Suspect').length,
        verified: items.filter(x => x.verified).length,
        uncovered: items.filter(x => x.disposition === 'Uncovered').length,
      })
    }
    if (nextRequests) setRequests(nextRequests)
    if (nextImpact) setImpact(nextImpact)
    if (listed) setRequirements(listed)
    if (!requestResponse.ok) {
      recordClientOperationFailure('verification.coverage.load', new Error(`HTTP ${requestResponse.status}`))
      setError('The test change requests for this build could not be loaded.')
    }
    if (coverageResponse && !coverageResponse.ok) {
      recordClientOperationFailure('verification.coverage.load', new Error(`HTTP ${coverageResponse.status}`))
      setError('The requirement coverage for this build could not be read.')
    }
  }, [api, projectId, releaseId, scope, discipline])

  useEffect(() => { void load() }, [load])

  // The worklist is in the address, so it can be reloaded, shared and stepped back through.
  useEffect(() => {
    const params = new URLSearchParams(location.search)
    const before = params.toString()
    const apply = (key: string, value: string) => { if (value) params.set(key, value); else params.delete(key) }
    apply('procedure', query)
    apply('procedureState', procedureState)
    apply('procedureOutcome', procedureOutcome)
    apply('procedurePage', procedurePage > 1 ? String(procedurePage) : '')
    // Seeded from what the address already says, so the reader's first change after a reload still earns a
    // history entry rather than being mistaken for arrival.
    const discrete = `${procedureState}|${procedureOutcome}|${procedurePage}`
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
  }, [query, procedureState, procedureOutcome, procedurePage])

  // The browser's own navigation must move the list, not just the address bar.
  useEffect(() => {
    const restore = () => {
      const params = new URLSearchParams(location.search)
      setQuery(params.get('procedure') ?? '')
      setProcedureState(params.get('procedureState') ?? '')
      setProcedureOutcome(params.get('procedureOutcome') ?? '')
      setProcedurePage(Number(params.get('procedurePage') ?? '1') || 1)
    }
    addEventListener('popstate', restore)
    return () => removeEventListener('popstate', restore)
  }, [])

  // Browsing, not just searching. The software side of the demonstration Program carries 440 procedures, so
  // a list that could only be searched meant knowing the number of the thing you were looking for before you
  // could look for it. State and latest result are how somebody actually narrows this: "the drafts", "what
  // failed last time".
  useEffect(() => {
    const mine = ++procedureTicket.current
    const timer = setTimeout(async () => {
      const filters = `&state=${procedureState}&outcome=${procedureOutcome}&page=${procedurePage}&pageSize=25`
      const response = await fetch(`${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}&search=${encodeURIComponent(query)}${filters}`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== procedureTicket.current) return
      setProcedures(paged.items)
      setTotal(paged.totalCount)
    }, 200)
    return () => clearTimeout(timer)
  }, [api, projectId, releaseId, scope, query, procedureState, procedureOutcome, procedurePage, revision])

  const mine = requests.filter(x => x.discipline === discipline)
  const unstarted = mine.filter(x => x.state === 'Open' && !x.assignedEngineerId)
  const uncovered = coverage?.items.filter(x => x.disposition === 'Uncovered') ?? []
  const suspect = coverage?.items.filter(x => x.disposition === 'Suspect') ?? []

  const act = async (work: () => Promise<void>, failure: string) => {
    if (busy) return
    setBusy(true); setError(''); setSaved('')
    // Both lists are re-read, not just coverage. Creating and approving a procedure change the inventory,
    // and refreshing only the coverage side left the row that was just approved still reading "Awaiting
    // approval" until the reader happened to type in the search box.
    try { await work(); await load(); setRevision(current => current + 1) }
    catch (problem) { recordClientOperationFailure('verification.coverage.change', problem); setError(operationError(problem, failure)) }
    finally { setBusy(false) }
  }

  // Taking a package on assigns every decision in it. A package half-assigned has no owner anybody can name,
  // which is the state this queue exists to make impossible.
  const takeOn = (request: TestChangeRequest) => act(async () => {
    const items = impact.filter(x => x.testChangeReviewId === request.id && x.state === 'Open')
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/assign`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ engineerId: user.userName }),
    })
    setSaved(`${request.displayNumber} is yours — ${items.length} decision${items.length === 1 ? '' : 's'}.`)
  }, 'The package could not be assigned.')

  const resolve = (item: ImpactItem, form: FormData) => act(async () => {
    const chosen = String(form.get('outcome'))
    await apiRequest(`${api}/api/verification-impact/${item.id}/resolve`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        outcome: chosen,
        rationale: form.get('rationale'),
        procedureId: chosen === 'ProcedureCoverageConfirmed' ? String(form.get('procedureId') || '') || null : null,
        procedureChangeAction: chosen === 'NoTestRequired' ? 'NoTestRequired' : form.get('procedureChangeAction') || null,
        retargetedRequirementRevisionId: chosen === 'ProcedureRetargeted' ? String(form.get('retargeted') || '') || null : null,
      }),
    })
    setResolving(undefined)
    setSaved(`Decision recorded for ${item.subjectDisplayNumber}.`)
  }, 'The decision could not be recorded.')

  const reopen = (item: ImpactItem, rationale: string) => act(async () => {
    await apiRequest(`${api}/api/verification-impact/${item.id}/reopen`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rationale }),
    })
    setReopening(undefined)
    setSaved(`${item.subjectDisplayNumber} is open again. What was decided stays in its history.`)
  }, 'The decision could not be reopened.')

  const advance = (request: TestChangeRequest, action: 'submit' | 'approve' | 'return', rationale?: string, approverId?: string) => act(async () => {
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(action === 'submit' ? { approverId } : { rationale: rationale ?? '' }),
    })
    setSaved(action === 'submit' ? `${request.displayNumber} sent for approval.`
      : action === 'approve' ? `${request.displayNumber} approved.`
      : `${request.displayNumber} returned for more work.`)
    if (action === 'submit') { setSubmitting(undefined); setReviewApprover({ userId: '', name: '' }) }
  }, 'The package could not be moved on.')

  const linkReports = (request: TestChangeRequest) => act(async () => {
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/problem-reports`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ problemReportIds }),
    })
    setLinkingProblemReports(undefined)
    setSaved(`PR links updated for ${request.displayNumber}.`)
  }, 'The PR links could not be updated.')

  const createProcedure = async (form: FormData) => {
    if (busy) return
    const requirementRevisionIds = form.getAll('requirement').map(String).filter(Boolean)
    if (!requirementRevisionIds.length) { setCreateError('A procedure has to say which requirements it verifies.'); return }
    setBusy(true); setCreateError(''); setError(''); setSaved('')
    try {
      const created = await apiRequest<CreatedProcedure>(`${api}/api/test-procedures`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          projectId,
          // The server issues the controlled number. A client that chose one would be choosing it twice under
          // concurrency, which is the whole reason identifiers are claimed from a sequence.
          baseNumber: 'SERVER-ALLOCATED',
          title: form.get('title'),
          objective: form.get('objective'),
          preconditions: form.get('preconditions'),
          steps: form.get('steps'),
          expectedResult: form.get('expectedResult'),
          requirementRevisionIds,
          approverId: procedureApprover.userId,
          level: discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HighLevel' : 'LowLevel',
        }),
      })
      setCreating(false)
      setProcedureApprover({ userId: '', name: '' })
      setQuery(created.displayNumber)
      setProcedurePage(1)
      setSaved(`${created.displayNumber} created as a Draft. It needs independent approval before it can be run.`)
      await load()
      setRevision(current => current + 1)
    } catch (problem) {
      recordClientOperationFailure('verification.procedure.create', problem)
      setCreateError(operationError(problem, 'The procedure could not be created.'))
    } finally { setBusy(false) }
  }

  // Approval is a signature, and it is somebody else's. A procedure approved by its own author is a
  // formality rather than an independent judgement, which the server refuses and this does not offer.
  const approveProcedure = (procedure: Procedure, password: string, meaning: string) => act(async () => {
    await apiRequest(`${api}/api/test-procedures/${procedure.revisionId}/approve`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password, meaning }),
    })
    setApproving(undefined)
    setSaved(`${procedure.displayNumber} approved and available to run.`)
  }, 'The approval could not be recorded.')

  const openProcedure = useCallback(async (procedureId: string, procedureRevisionId?: string,
    view: 'record' | 'history' = 'record', updateAddress = true) => {
    setError('')
    const params = new URLSearchParams({ releaseId })
    if (procedureRevisionId) params.set('revisionId', procedureRevisionId)
    const response = await fetch(`${api}/api/test-procedures/${procedureId}/history?${params}`)
    if (!response.ok) { setError('That procedure’s history could not be read.'); return }
    setHistory(await response.json())
    setProcedureView(view)
    if (updateAddress) {
      const address = new URLSearchParams(location.search)
      address.set('procedureId', procedureId)
      if (procedureRevisionId) address.set('procedureRevisionId', procedureRevisionId)
      else address.delete('procedureRevisionId')
      address.set('procedureView', view)
      window.history.pushState({}, '', `${location.pathname}?${address}`)
    }
  }, [api, releaseId])

  const closeHistory = () => {
    setHistory(undefined)
    const params = new URLSearchParams(location.search)
    params.delete('procedureId')
    params.delete('procedureRevisionId')
    params.delete('procedureView')
    params.delete('procedure')
    window.history.replaceState({}, '', `${location.pathname}${params.toString() ? `?${params}` : ''}`)
    setQuery('')
  }

  useEffect(() => {
    if (openingProcedureId) void openProcedure(openingProcedureId, openingProcedureRevisionId || undefined,
      opening.get('procedureView') === 'history' ? 'history' : 'record', false)
  }, [openProcedure, opening, openingProcedureId, openingProcedureRevisionId])

  const selectedProcedureRevision = history?.revisions.find(item => item.id === history.selectedRevisionId)
    ?? history?.revisions[0]

  return (
    <main className="testingCoveragePage">
      <header>
        <div>
          <p className="eyebrow">VERIFICATION / {disciplineLabel(discipline).toUpperCase()}</p>
          <h1>Testing Coverage</h1>
          <p>What {buildName} is tested by, and what still has nobody looking at it.</p>
        </div>
      </header>
      {error && <div className="workspaceError" role="alert" aria-live="assertive">{error}</div>}
      {saved && <div className="workspaceSaved" role="status">{saved}</div>}

      <section className="coverageSummary" aria-label="Coverage summary">
        <article><b>{coverage?.total ?? 0}</b><span>Requirements</span></article>
        <article><b>{coverage?.covered ?? 0}</b><span>With a procedure</span></article>
        <article className={uncovered.length ? 'attention' : ''}><b>{uncovered.length}</b><span>With none</span></article>
        <article className={suspect.length ? 'attention' : ''}><b>{suspect.length}</b><span>Suspect coverage</span></article>
      </section>

      {/* The queue, before the inventory. Somebody arriving to do verification work needs to know what this
          build's changes have made their problem — a wall of green coverage says nothing about that. */}
      <section className="coverageCard">
        <div className="cardTitle">
          <h2>Test change requests</h2>
          <p>Raised when a change request is approved. {unstarted.length ? `${unstarted.length} not yet picked up.` : 'All picked up.'}</p>
        </div>
        {!mine.length && (
          <div className="coverageEmpty">
            <b>No {disciplineLabel(discipline)} test change requests for this build</b>
            <span>Nothing approved so far has created {disciplineLabel(discipline)} test work.</span>
          </div>
        )}
        {mine.map(request => (
          <article className={`coverageRow ${request.state === 'Open' && !request.assignedEngineerId ? 'attention' : ''}`} key={request.id}>
            <div><b>{request.displayNumber}</b><i>{request.state === 'InReview' ? 'In review' : request.state}</i></div>
            <p>Covers {request.coveredChangeRequests.map(x => x.number).join(', ')}</p>
            <small>
              {request.resolvedItems} of {request.totalItems} decisions recorded
              {request.assignedEngineerId ? <> · <PersonName userName={request.assignedEngineerId} /></> : ' · nobody has picked this up'}
            </small>
            <div className="coverageRowActions">
              <button type="button" className="quiet" onClick={() => setOpened(current => current === request.id ? '' : request.id)}>
                {opened === request.id ? 'Hide decisions' : 'Decisions'}
              </button>
              {canTest && request.state === 'Open' && <button type="button" className="quiet" disabled={busy} onClick={() => {
                setProblemReportIds((request.problemReports ?? []).map(report => report.id))
                setLinkingProblemReports(request)
              }}>Link PRs{request.problemReports?.length ? ` · ${request.problemReports.length}` : ''}</button>}
              {request.capabilities.canAssign && (
                <button type="button" className="quiet" disabled={busy} onClick={() => void takeOn(request)}>Take it on</button>
              )}
              {/* Submission is offered only once every decision is recorded. The server refuses otherwise, and
                  offering an action that will be refused is a worse answer than not offering it. */}
              {request.capabilities.canSubmit && request.totalItems > 0 && request.resolvedItems === request.totalItems && (
                <button type="button" disabled={busy} onClick={() => setSubmitting(request)}>Send for approval</button>
              )}
              {request.capabilities.canApprove && (
                <>
                  <button type="button" disabled={busy} onClick={() => setReviewDecision({ request, action: 'approve' })}>Approve</button>
                  <button type="button" className="quiet" disabled={busy} onClick={() => setReviewDecision({ request, action: 'return' })}>Return</button>
                </>
              )}
            </div>
            {opened === request.id && (
              <ul className="decisionList">
                {impact.filter(x => x.testChangeReviewId === request.id).map(item => (
                  <li key={item.id}>
                    <b>{item.subjectDisplayNumber}</b>
                    <i>{item.state === 'Resolved' ? (item.outcome ?? 'Resolved') : item.state}</i>
                    <small>
                      Author declared {item.declaredVerificationMethod || 'no method'}
                      {item.assignedEngineerId ? <> · <PersonName userName={item.assignedEngineerId} /></> : ''}
                      {item.resolutionRationale ? ` · ${item.resolutionRationale}` : ''}
                    </small>
                    {request.capabilities.canDecide && item.state !== 'Resolved' && (
                      <button type="button" className="quiet" disabled={busy} onClick={() => {
                        setOutcome(item.trigger === 'ProcedureOrphaned' ? 'ProcedureRetired' : 'ProcedureCoverageConfirmed')
                        setResolving(item)
                      }}>Decide</button>
                    )}
                    {/* A decision can be wrong, and a decision nobody can revisit is a decision people work
                        around. Reopening keeps what was decided in immutable history, returns the item to the
                        release gate, and puts any coverage it claimed back to suspect. */}
                    {request.capabilities.canDecide && item.state === 'Resolved' && (
                      <button type="button" className="quiet" disabled={busy} onClick={() => setReopening(item)}>Reopen / change decision…</button>
                    )}
                    {!!item.decisionHistory?.length && (
                      <details className="decisionHistory">
                        <summary>Decision history · {item.decisionHistory.length}</summary>
                        {item.decisionHistory.map(entry => (
                          <article key={entry.id}>
                            <b>{entry.action === 'Reopened' ? 'Decision reopened'
                              : entry.outcome === 'ProcedureCoverageConfirmed' ? 'Coverage confirmed'
                              : entry.outcome === 'NoTestRequired' ? 'No test required'
                              : entry.outcome}</b>
                            <span><PersonName userName={entry.actor} /> · {new Date(entry.occurredAt).toLocaleString()}</span>
                            <p>{entry.rationale}</p>
                          </article>
                        ))}
                      </details>
                    )}
                  </li>
                ))}
                {!impact.some(x => x.testChangeReviewId === request.id) && <li className="decisionNone">This package has no decisions recorded against it.</li>}
              </ul>
            )}
          </article>
        ))}
      </section>

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

      {/* Its own class as well as the shared card: requirement rows and procedure rows both render as
          .coverageRow, and a reader — or a test — looking for a procedure by number would otherwise match
          the requirement that names it as its coverage. */}
      <section className="coverageCard procedureLibrary">
        <div className="cardTitle">
          <h2>Test procedures</h2>
          <p>{total} controlled {disciplineLabel(discipline).toLowerCase()} procedure{total === 1 ? '' : 's'}. Open one to see who wrote it and what changed it.</p>
          {!readOnly && (
            <button
              type="button"
              disabled={!canTest || !requirements.length}
              title={requirements.length ? undefined : 'Materialize the software build requirements before creating a procedure.'}
              onClick={() => { setCreateError(''); setCreating(true) }}
            >+ New test procedure</button>
          )}
        </div>

        {/* A project with nothing materialized has no exact revisions to bind a procedure to. Said plainly,
            because the alternative is a create form whose requirement list is empty for no stated reason —
            which reads as a broken page rather than as work that has not happened yet. */}
        {!requirements.length && (
          <section className="materializationPrerequisite" role="status">
            <div>
              <b>Procedure authoring waits for governed requirement materialization</b>
              <p>
                This build has no immutable requirement revisions yet, so a new procedure cannot be bound to an
                exact target. Existing inherited procedures remain visible against their predecessor revisions;
                planned work for new or modified requirements stays in the test change requests above and
                cannot count as confirmed coverage yet.
              </p>
            </div>
            <div>
              <span>Current limitation</span>
              <b>Requirement materialization is not exposed in this workspace.</b>
            </div>
          </section>
        )}
        <div className="procedureFilters">
          <label className="coverageSearch">
            <span>Find a procedure</span>
            <input value={query} onChange={event => { setQuery(event.target.value); setProcedurePage(1) }} placeholder="Procedure number or title" />
          </label>
          <label>
            <span>Procedure state</span>
            <select value={procedureState} onChange={event => { setProcedureState(event.target.value); setProcedurePage(1) }}>
              <option value="">All states</option>
              <option value="Draft">Draft</option>
              <option value="InReview">In review</option>
              <option value="Approved">Approved</option>
            </select>
          </label>
          <label>
            <span>Latest result</span>
            <select value={procedureOutcome} onChange={event => { setProcedureOutcome(event.target.value); setProcedurePage(1) }}>
              <option value="">All outcomes</option>
              <option value="Pass">Pass</option>
              <option value="Fail">Fail</option>
              <option value="Blocked">Blocked</option>
            </select>
          </label>
        </div>
        {procedures.map(procedure => (
          <article className="coverageRow" key={procedure.id}>
            <div><button type="button" className="procedureRecordLink" aria-label={`Open procedure ${procedure.displayNumber}`}
              onClick={() => void openProcedure(procedure.id, procedure.revisionId)}><b>{procedure.displayNumber}</b></button><i>{procedure.state === 'Draft' ? 'Awaiting approval' : procedure.state}</i></div>
            <p><button type="button" className="procedureTitleLink" aria-label={`Open procedure ${procedure.title}`}
              onClick={() => void openProcedure(procedure.id, procedure.revisionId)}>{procedure.title}</button></p>
            <small>{procedure.requirementCount} exact requirement link{procedure.requirementCount === 1 ? '' : 's'} · authored by <PersonName userName={procedure.ownerId} /></small>
            <div className="coverageRowActions">
              {/* A Draft cannot be run, so approving it is the action that matters here. The server refuses
                  an author approving their own, which is what makes the approval independent rather than a
                  formality — so this is offered and may still be declined. */}
              {!readOnly && procedure.state === 'Draft' && (
                canApprove && procedure.ownerId !== user.userName && procedure.selectedApproverId === user.userName
                  ? <button type="button" disabled={busy} onClick={() => setApproving(procedure)}>Review &amp; approve</button>
                  : <span className="procedureHold">{procedure.ownerId === user.userName ? 'Independent approval is required before execution.' : procedure.selectedApproverId ? <>Awaiting <PersonName userName={procedure.selectedApproverId} />.</> : 'A named approver is required.'}</span>
              )}
              {!readOnly && procedure.state === 'Draft' && canTest && (user.isAdministrator || procedure.ownerId === user.userName) &&
                <button type="button" className="quiet" onClick={() => setEditing(procedure)}>Edit</button>}
              <button type="button" className="quiet" onClick={() => void openProcedure(procedure.id, undefined, 'history')}>History</button>
            </div>
          </article>
        ))}
        {!procedures.length && (
          <p className="coverageNone">
            {query || procedureState || procedureOutcome ? 'No procedure matches that. Clear the search or the filters to see the rest.' : 'This build has no controlled procedures yet.'}
          </p>
        )}
        {total > 25 && (
          <div className="procedurePager">
            <button type="button" disabled={procedurePage <= 1} onClick={() => setProcedurePage(value => Math.max(1, value - 1))}>Previous</button>
            <span>Page {procedurePage} of {Math.max(1, Math.ceil(total / 25))}</span>
            <button type="button" disabled={procedurePage >= Math.ceil(total / 25)} onClick={() => setProcedurePage(value => value + 1)}>Next</button>
          </div>
        )}
      </section>

      {creating && (
        <div className="decisionModal" role="dialog" aria-label="Create a test procedure">
          <form onSubmit={event => { event.preventDefault(); void createProcedure(new FormData(event.currentTarget)) }}>
            <p className="eyebrow">CONTROLLED PROCEDURE</p>
            <h2>New {disciplineLabel(discipline)} test procedure</h2>
            <p>The server issues the next controlled number. It is created as a Draft and needs independent approval before it can be run.</p>
            {createError && <div className="createProcedureError" role="alert" aria-live="assertive">{createError}</div>}
            <label>Title<input name="title" required /></label>
            <label>Objective<textarea name="objective" required /></label>
            <label>Preconditions<textarea name="preconditions" required /></label>
            <label>Steps<textarea name="steps" required /></label>
            <label>Expected result<textarea name="expectedResult" required /></label>
            {/* A procedure that verifies nothing is not a controlled procedure. The requirements are chosen
                here rather than linked afterwards, because a procedure with no exact link never counts as
                coverage and would sit in the library looking like work that had been done. */}
            <label>Requirements it verifies
              <select name="requirement" aria-describedby="procedure-requirements-help" multiple size={6} required>
                {requirements.map(item => (
                  <option key={item.revisionId} value={item.revisionId}>{item.displayNumber} · {item.statement.slice(0, 70)}</option>
                ))}
              </select>
              <small id="procedure-requirements-help">Choose one or more. Hold Ctrl to pick several.</small>
            </label>
            <label>Independent approver</label>
            <PersonPicker api={api} projectId={projectId} value={procedureApprover.userId} name={procedureApprover.name}
              index={9101} label="Independent procedure approver" excludeUserNames={[user.userName]} onSelect={setProcedureApprover} />
            <div className="decisionActions">
              <button type="submit" disabled={busy || !procedureApprover.userId}>{busy ? 'Creating procedureâ€¦' : 'Create procedure'}</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => { setCreating(false); setCreateError('') }}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {linkingProblemReports && (
        <div className="decisionModal" role="dialog" aria-label={`Link PRs to ${linkingProblemReports.displayNumber}`}>
          <form onSubmit={event => { event.preventDefault(); void linkReports(linkingProblemReports) }}>
            <p className="eyebrow">CONTROLLED TRACEABILITY</p>
            <h2>Link PRs to {linkingProblemReports.displayNumber}</h2>
            <ProblemReportPicker api={api} projectId={projectId} releaseId={releaseId}
              selected={problemReportIds} locked={(linkingProblemReports.problemReports ?? []).map(report => report.id)}
              onChange={setProblemReportIds} legend="PRs verified by this TCR" />
            <div className="decisionActions">
              <button type="submit" disabled={busy}>Save links</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => setLinkingProblemReports(undefined)}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {submitting && (
        <div className="decisionModal" role="dialog" aria-label={`Select approver for ${submitting.displayNumber}`}>
          <form onSubmit={event => {
            event.preventDefault()
            if (reviewApprover.userId) void advance(submitting, 'submit', undefined, reviewApprover.userId)
          }}>
            <p className="eyebrow">INDEPENDENT REVIEW</p>
            <h2>Send {submitting.displayNumber} for approval</h2>
            <p>Select the person who will independently review this exact package of test-procedure decisions.</p>
            <PersonPicker api={api} projectId={projectId} value={reviewApprover.userId} name={reviewApprover.name}
              index={9102} label="Independent test change request approver" excludeUserNames={[submitting.assignedEngineerId??user.userName,user.userName]} onSelect={setReviewApprover} />
            <div className="decisionActions">
              <button type="submit" disabled={busy || !reviewApprover.userId}>Send for approval</button>
              <button type="button" className="quiet" onClick={() => setSubmitting(undefined)}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {approving && (
        <SignatureDialog
          title={`Approve ${approving.displayNumber}`}
          meaning="I approve this exact test procedure revision for controlled verification use."
          onCancel={() => setApproving(undefined)}
          onSign={(password, meaning) => approveProcedure(approving, password, meaning)}
        />
      )}

      {editing && <ControlledProcedureEditor api={api} procedure={editing} onClose={() => setEditing(undefined)}
        onCommitted={async () => { await load(); setRevision(current => current + 1) }} />}

      {reviewDecision && (
        <div className="decisionModal" role="dialog" aria-label={`${reviewDecision.action === 'approve' ? 'Approve' : 'Return'} ${reviewDecision.request.displayNumber}`}>
          <form onSubmit={event => {
            event.preventDefault()
            const rationale = String(new FormData(event.currentTarget).get('rationale') ?? '').trim()
            if (!rationale) return
            const selected = reviewDecision
            setReviewDecision(undefined)
            void advance(selected.request, selected.action, rationale)
          }}>
            <p className="eyebrow">INDEPENDENT REVIEW</p>
            <h2>{reviewDecision.action === 'approve' ? 'Approve' : 'Return'} {reviewDecision.request.displayNumber}</h2>
            <p>{reviewDecision.action === 'approve'
              ? 'Record why this exact package of test-procedure decisions is acceptable.'
              : 'State what the test engineer must update before this package can be approved.'}</p>
            <label>Rationale<textarea name="rationale" required autoFocus /></label>
            <div className="decisionActions">
              <button type="button" className="quiet" disabled={busy} onClick={() => setReviewDecision(undefined)}>Cancel</button>
              <button type="submit" disabled={busy}>{reviewDecision.action === 'approve' ? 'Approve package' : 'Return for changes'}</button>
            </div>
          </form>
        </div>
      )}

      {reopening && (
        <div className="decisionModal" role="dialog" aria-label="Reopen verification decision">
          <form onSubmit={event => {
            event.preventDefault()
            void reopen(reopening, String(new FormData(event.currentTarget).get('rationale') ?? ''))
          }}>
            <p className="eyebrow">VERIFICATION DECISION</p>
            <h2>Reopen {reopening.subjectDisplayNumber}</h2>
            <p>The current decision stays in immutable history. Reopening returns this item to the release gate, and any coverage it claimed goes back to suspect.</p>
            <label>Reopen rationale
              <textarea name="rationale" required placeholder="Why the recorded decision must be reconsidered" />
            </label>
            <div className="decisionActions">
              <button type="button" className="quiet" onClick={() => setReopening(undefined)}>Cancel</button>
              <button type="submit" disabled={busy}>Reopen decision</button>
            </div>
          </form>
        </div>
      )}

      {resolving && (
        <div className="decisionModal" role="dialog" aria-label={`Decide ${resolving.subjectDisplayNumber}`}>
          <form onSubmit={event => { event.preventDefault(); void resolve(resolving, new FormData(event.currentTarget)) }}>
            <p className="eyebrow">VERIFICATION DECISION</p>
            <h2>{resolving.subjectDisplayNumber}</h2>
            {/* Every outcome is an explicit judgement. There is deliberately no value meaning "nobody looked",
                because a requirement must never reach an approved baseline without somebody having decided. */}
            <label>Decision
              <select name="outcome" value={outcome} onChange={event => setOutcome(event.target.value)}>
                {resolving.trigger === 'ProcedureOrphaned' ? (
                  <>
                    <option value="ProcedureRetired">Procedure retired</option>
                    <option value="ProcedureRetargeted">Procedure moved to another requirement</option>
                    <option value="ProcedureRetained">Procedure deliberately retained</option>
                  </>
                ) : (
                  <>
                    <option value="ProcedureCoverageConfirmed">An approved procedure covers this</option>
                    <option value="NoTestRequired">No test required</option>
                  </>
                )}
              </select>
            </label>
            {outcome === 'ProcedureCoverageConfirmed' && (
              <label>Covering procedure
                <select name="procedureId" aria-label="Covering procedure" aria-describedby="covering-procedure-help" required>
                  <option value="">Choose an approved procedure…</option>
                  {procedures.filter(x => x.state === 'Approved').map(x => (
                    <option key={x.id} value={x.id}>{x.displayNumber} · {x.title.slice(0, 60)}</option>
                  ))}
                </select>
                <small id="covering-procedure-help">Only approved procedures in this Project. Search above to bring more into this list.</small>
              </label>
            )}
            {outcome === 'ProcedureRetargeted' && (
              <label>Requirement it moves to
                <select name="retargeted" required>
                  <option value="">Choose a requirement…</option>
                  {(coverage?.items ?? []).map(x => (
                    <option key={x.revisionId} value={x.revisionId}>{x.displayNumber} · {x.statement.slice(0, 60)}</option>
                  ))}
                </select>
              </label>
            )}
            <label>Rationale<textarea name="rationale" placeholder="Why this is the right answer for this requirement." required /></label>
            <div className="decisionActions">
              <button type="submit" disabled={busy}>Record decision</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => setResolving(undefined)}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {history && selectedProcedureRevision && (
        <div className="procedureHistoryModal" role="dialog" aria-modal="true"
          aria-label={procedureView === 'record' ? `Procedure ${selectedProcedureRevision.displayNumber}` : `History of ${history.baseNumber}`}>
          <div>
            <p className="eyebrow">CONTROLLED PROCEDURE</p>
            <h2>{procedureView === 'record' ? selectedProcedureRevision.displayNumber : history.baseNumber}</h2>
            <p>{history.title}</p>
            <small>Created by <PersonName userName={history.ownerId} /> on {new Date(history.createdAt).toLocaleDateString()}</small>
            {procedureView === 'record' ? (
              <div className="procedureRecordContent">
                <div className="procedureRecordMeta"><i>{selectedProcedureRevision.state}</i><span>Written by <PersonName userName={selectedProcedureRevision.authorId} /> on {new Date(selectedProcedureRevision.createdAt).toLocaleDateString()}</span></div>
                <dl><dt>Objective</dt><dd>{selectedProcedureRevision.objective}</dd><dt>Preconditions</dt><dd>{selectedProcedureRevision.preconditions}</dd><dt>Steps</dt><dd>{selectedProcedureRevision.steps}</dd><dt>Expected result</dt><dd>{selectedProcedureRevision.expectedResult}</dd></dl>
                {selectedProcedureRevision.drivenBy.length
                  ? <span className="revisionDriver">Driven by {selectedProcedureRevision.drivenBy.map(x => `${x.changeRequest} (${x.package})`).join(', ')}</span>
                  : <span className="revisionDriver quiet">No change request is recorded against this revision.</span>}
                {selectedProcedureRevision.covers.length > 0 && <span className="revisionCovers">Covers {selectedProcedureRevision.covers.join(', ')}</span>}
              </div>
            ) : (
              <ol className="revisionList">
                {history.revisions.map(revision => (
                  <li key={revision.id} className={revision.selected ? 'selectedRevision' : undefined}>
                    <b>{revision.displayNumber}</b>
                    <i>{revision.state}</i>
                    {revision.selected && <strong>{history.revisions[0]?.id === revision.id ? 'Selected exact revision' : 'Selected historical build revision'}</strong>}
                    <small>Written by <PersonName userName={revision.authorId} /> on {new Date(revision.createdAt).toLocaleDateString()}</small>
                    <p>{revision.objective}</p>
                    <details><summary>Controlled procedure content</summary><dl><dt>Preconditions</dt><dd>{revision.preconditions}</dd><dt>Steps</dt><dd>{revision.steps}</dd><dt>Expected result</dt><dd>{revision.expectedResult}</dd></dl></details>
                    {revision.drivenBy.length
                      ? <span className="revisionDriver">Driven by {revision.drivenBy.map(x => `${x.changeRequest} (${x.package})`).join(', ')}</span>
                      : <span className="revisionDriver quiet">No change request is recorded against this revision.</span>}
                    {revision.covers.length > 0 && <span className="revisionCovers">Covers {revision.covers.join(', ')}</span>}
                  </li>
                ))}
              </ol>
            )}
            <div className="procedureRecordActions">
              {procedureView === 'record' && <button type="button" className="quiet" onClick={() => void openProcedure(history.id, selectedProcedureRevision.id, 'history')}>History</button>}
              <button type="button" onClick={closeHistory}>Close</button>
            </div>
          </div>
        </div>
      )}
    </main>
  )
}
