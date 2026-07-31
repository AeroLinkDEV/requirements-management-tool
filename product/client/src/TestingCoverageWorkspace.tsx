import { useCallback, useEffect, useRef, useState } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import type { TestDiscipline } from './TestResultsWorkspace'
import './TestingCoverageWorkspace.css'

type CoverageItem = {
  revisionId: string
  displayNumber: string
  statement: string
  covered: boolean
  verified: boolean
  coveredBy: { procedureId: string; revisionId: string; displayNumber: string; title: string; state: string; coverageState: 'Confirmed' | 'Suspect' }[]
}
type Coverage = { total: number; covered: number; verified: number; uncovered: number; items: CoverageItem[] }
type ChangeRequestCover = { id: string; number: string; originating: boolean }
type TestChangeRequest = {
  id: string
  displayNumber: string
  discipline: string
  state: string
  assignedEngineerId?: string
  totalItems: number
  resolvedItems: number
  coveredChangeRequests: ChangeRequestCover[]
}
type Procedure = { id: string; revisionId: string; displayNumber: string; title: string; state: string; requirementCount: number; ownerId: string }
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
}
type Revision = {
  id: string
  displayNumber: string
  revision: number
  state: string
  authorId: string
  createdAt: string
  drivenBy: { changeRequest: string; package: string; subjectDisplayNumber: string; action: string }[]
  covers: string[]
}
type History = { id: string; baseNumber: string; title: string; ownerId: string; createdAt: string; revisions: Revision[] }

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
export default function TestingCoverageWorkspace({ api, projectId, releaseId, discipline, buildName, readOnly, user }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  buildName: string
  readOnly: boolean
  user: { userName: string }
}) {
  const [coverage, setCoverage] = useState<Coverage>()
  const [requests, setRequests] = useState<TestChangeRequest[]>([])
  const [procedures, setProcedures] = useState<Procedure[]>([])
  const [total, setTotal] = useState(0)
  const [query, setQuery] = useState('')
  const [history, setHistory] = useState<History>()
  const [impact, setImpact] = useState<ImpactItem[]>([])
  const [opened, setOpened] = useState('')
  const [resolving, setResolving] = useState<ImpactItem>()
  const [outcome, setOutcome] = useState('ProcedureCoverageConfirmed')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')

  // One ticket per loader, not one for the page.
  //
  // Only the newest reply of a given loader may write the screen — but the two loaders here are independent,
  // and sharing a counter made each of them cancel the other. The procedure search runs on mount behind a
  // debounce, bumps the count, and the coverage load that was already in flight then discards everything it
  // read: no packages, no coverage, no impact, and no error, because nothing failed. It presented as the
  // software HLR queue being empty while the API demonstrably returned a package for that build.
  const loadTicket = useRef(0)
  const procedureTicket = useRef(0)
  const scope = discipline === 'System' ? 'System' : 'Software'

  const load = useCallback(async () => {
    const mine = ++loadTicket.current
    const [coverageResponse, requestResponse, impactResponse] = await Promise.all([
      fetch(`${api}/api/verification-coverage?projectId=${projectId}&buildId=${releaseId}`),
      fetch(`${api}/api/releases/${releaseId}/test-change-reviews`),
      fetch(`${api}/api/releases/${releaseId}/verification-impact`),
    ])
    const nextCoverage = coverageResponse.ok ? await coverageResponse.json() : undefined
    const nextRequests = requestResponse.ok ? await requestResponse.json() : undefined
    const nextImpact = impactResponse.ok ? await impactResponse.json() : undefined
    if (mine !== loadTicket.current) return
    if (nextCoverage) setCoverage(nextCoverage)
    if (nextRequests) setRequests(nextRequests)
    if (nextImpact) setImpact(nextImpact)
    if (!requestResponse.ok) {
      recordClientOperationFailure('verification.coverage.load', new Error(`HTTP ${requestResponse.status}`))
      setError('The test change requests for this build could not be loaded.')
    }
  }, [api, projectId, releaseId])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    const mine = ++procedureTicket.current
    const timer = setTimeout(async () => {
      const response = await fetch(`${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}&search=${encodeURIComponent(query)}&page=1&pageSize=25`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== procedureTicket.current) return
      setProcedures(paged.items)
      setTotal(paged.totalCount)
    }, 200)
    return () => clearTimeout(timer)
  }, [api, projectId, releaseId, scope, query])

  const mine = requests.filter(x => x.discipline === discipline)
  const unstarted = mine.filter(x => x.state === 'Open' && !x.assignedEngineerId)
  const uncovered = coverage?.items.filter(x => !x.covered) ?? []
  const suspect = coverage?.items.filter(x => x.coveredBy.some(link => link.coverageState === 'Suspect')) ?? []

  const act = async (work: () => Promise<void>, failure: string) => {
    if (busy) return
    setBusy(true); setError(''); setSaved('')
    try { await work(); await load() }
    catch (problem) { recordClientOperationFailure('verification.coverage.change', problem); setError(operationError(problem, failure)) }
    finally { setBusy(false) }
  }

  // Taking a package on assigns every decision in it. A package half-assigned has no owner anybody can name,
  // which is the state this queue exists to make impossible.
  const takeOn = (request: TestChangeRequest) => act(async () => {
    const items = impact.filter(x => x.testChangeReviewId === request.id && x.state === 'Open')
    for (const item of items)
      await apiRequest(`${api}/api/verification-impact/${item.id}/assign`, {
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

  const advance = (request: TestChangeRequest, action: 'submit' | 'approve' | 'return', rationale?: string) => act(async () => {
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rationale: rationale ?? '' }),
    })
    setSaved(action === 'submit' ? `${request.displayNumber} sent for approval.`
      : action === 'approve' ? `${request.displayNumber} approved.`
      : `${request.displayNumber} returned for more work.`)
  }, 'The package could not be moved on.')

  const openHistory = async (procedureId: string) => {
    setError('')
    const response = await fetch(`${api}/api/test-procedures/${procedureId}/history`)
    if (!response.ok) { setError('That procedure’s history could not be read.'); return }
    setHistory(await response.json())
  }

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
              {!readOnly && request.state === 'Open' && !request.assignedEngineerId && (
                <button type="button" className="quiet" disabled={busy} onClick={() => void takeOn(request)}>Take it on</button>
              )}
              {/* Submission is offered only once every decision is recorded. The server refuses otherwise, and
                  offering an action that will be refused is a worse answer than not offering it. */}
              {!readOnly && request.state === 'Open' && request.totalItems > 0 && request.resolvedItems === request.totalItems && (
                <button type="button" disabled={busy} onClick={() => void advance(request, 'submit')}>Send for approval</button>
              )}
              {!readOnly && request.state === 'InReview' && (
                <>
                  <button type="button" disabled={busy} onClick={() => {
                    const rationale = window.prompt(`Why is ${request.displayNumber} approved?`)
                    if (rationale?.trim()) void advance(request, 'approve', rationale)
                  }}>Approve</button>
                  <button type="button" className="quiet" disabled={busy} onClick={() => {
                    const rationale = window.prompt(`Why is ${request.displayNumber} going back?`)
                    if (rationale?.trim()) void advance(request, 'return', rationale)
                  }}>Return</button>
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
                    {!readOnly && item.state !== 'Resolved' && (
                      <button type="button" className="quiet" disabled={busy} onClick={() => {
                        setOutcome(item.trigger === 'ProcedureOrphaned' ? 'ProcedureRetired' : 'ProcedureCoverageConfirmed')
                        setResolving(item)
                      }}>Decide</button>
                    )}
                  </li>
                ))}
                {!impact.some(x => x.testChangeReviewId === request.id) && <li className="decisionNone">This package has no decisions recorded against it.</li>}
              </ul>
            )}
          </article>
        ))}
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
          <h2>Test procedures</h2>
          <p>{total} controlled {scope.toLowerCase()} procedure{total === 1 ? '' : 's'}. Open one to see who wrote it and what changed it.</p>
        </div>
        <label className="coverageSearch">
          <span>Find a procedure</span>
          <input value={query} onChange={event => setQuery(event.target.value)} placeholder="Procedure number or title" />
        </label>
        {procedures.map(procedure => (
          <article className="coverageRow" key={procedure.id}>
            <div><b>{procedure.displayNumber}</b><i>{procedure.state === 'Draft' ? 'Awaiting approval' : procedure.state}</i></div>
            <p>{procedure.title}</p>
            <small>{procedure.requirementCount} exact requirement link{procedure.requirementCount === 1 ? '' : 's'} · authored by <PersonName userName={procedure.ownerId} /></small>
            <div className="coverageRowActions">
              <button type="button" className="quiet" onClick={() => void openHistory(procedure.id)}>History</button>
            </div>
          </article>
        ))}
        {!procedures.length && <p className="coverageNone">{query ? 'No procedure matches that.' : 'This build has no controlled procedures yet.'}</p>}
      </section>

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
                <select name="procedureId" required>
                  <option value="">Choose an approved procedure…</option>
                  {procedures.filter(x => x.state === 'Approved').map(x => (
                    <option key={x.id} value={x.id}>{x.displayNumber} · {x.title.slice(0, 60)}</option>
                  ))}
                </select>
                <small>Only approved procedures in this Project. Search above to bring more into this list.</small>
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

      {history && (
        <div className="procedureHistoryModal" role="dialog" aria-label={`History of ${history.baseNumber}`}>
          <div>
            <p className="eyebrow">CONTROLLED PROCEDURE</p>
            <h2>{history.baseNumber}</h2>
            <p>{history.title}</p>
            <small>Created by <PersonName userName={history.ownerId} /> on {new Date(history.createdAt).toLocaleDateString()}</small>
            <ol className="revisionList">
              {history.revisions.map(revision => (
                <li key={revision.id}>
                  <b>{revision.displayNumber}</b>
                  <i>{revision.state}</i>
                  <small>Written by <PersonName userName={revision.authorId} /> on {new Date(revision.createdAt).toLocaleDateString()}</small>
                  {/* What made somebody write this revision. Reached through the verification decision that
                      resolved to it, which is the record that actually connects a procedure to a change. */}
                  {revision.drivenBy.length
                    ? <span className="revisionDriver">Driven by {revision.drivenBy.map(x => `${x.changeRequest} (${x.package})`).join(', ')}</span>
                    : <span className="revisionDriver quiet">No change request is recorded against this revision.</span>}
                  {revision.covers.length > 0 && <span className="revisionCovers">Covers {revision.covers.join(', ')}</span>}
                </li>
              ))}
            </ol>
            <button type="button" onClick={() => setHistory(undefined)}>Close</button>
          </div>
        </div>
      )}
    </main>
  )
}
