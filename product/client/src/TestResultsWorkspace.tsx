import { useCallback, useEffect, useRef, useState } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import './TestResultsWorkspace.css'

/// The three ways a build's test work is split, as the API names them.
export type TestDiscipline = 'System' | 'HighLevelSoftware' | 'LowLevelSoftware'

type SetProcedure = {
  procedureRevisionId: string
  displayNumber: string
  title: string
  reason: string
  note: string
  addedBy: string
  addedAt: string
  latestOutcome?: string | null
  hasEvidence: boolean
}
type TestSet = { id: string; discipline: TestDiscipline; releaseId: string; version: number; procedures: SetProcedure[] }
type Candidate = { revisionId: string; displayNumber: string; title: string; state: string }

/// Why a procedure was chosen, said the way somebody would say it.
const reasonLabel = (reason: string) => reason === 'ChangedRequirement' ? 'Covers a change'
  : reason === 'CoverageArea' ? 'Area sweep'
  : reason === 'CorrectiveAction' ? 'Corrective retest'
  : 'Chosen'

/**
 * What this build has to run, and what happened when it was run.
 *
 * A build is rarely worth its whole test suite. Somebody decides which procedures this one needs — those
 * covering what changed, plus whatever areas the change makes worth re-exercising — and the release is then
 * measured against that decision. This page is where the decision is made and where the results against it
 * are read.
 *
 * The set is a working list rather than a controlled artefact: procedures are added and removed as a build
 * progresses, and a procedure added after a defect is found is the normal case rather than an exception.
 * Every entry records who put it there and why, so the shape of the plan survives the people who made it.
 */
export default function TestResultsWorkspace({ api, projectId, releaseId, discipline, buildName, readOnly, onOpenProcedure }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  buildName: string
  readOnly: boolean
  onOpenProcedure?: (procedureRevisionId: string) => void
}) {
  const [sets, setSets] = useState<TestSet[]>([])
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [query, setQuery] = useState('')
  const [chosen, setChosen] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')

  // Only the newest reply may write the screen. Typing in the search box starts a request while the previous
  // one is still in flight, and the broad reply is the slow one — see VerificationCenter for the same guard
  // and the defect that made it necessary.
  const ticket = useRef(0)

  const load = useCallback(async () => {
    const mine = ++ticket.current
    const response = await fetch(`${api}/api/releases/${releaseId}/test-sets`)
    if (!response.ok) {
      recordClientOperationFailure('verification.testSet.load', new Error(`HTTP ${response.status}`))
      if (mine === ticket.current) setError('The test set for this build could not be loaded.')
      return
    }
    const body = await response.json()
    if (mine === ticket.current) setSets(body)
  }, [api, releaseId])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    const mine = ++ticket.current
    const timer = setTimeout(async () => {
      const scope = discipline === 'System' ? 'System' : 'Software'
      const response = await fetch(`${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}&search=${encodeURIComponent(query)}&state=Approved&page=1&pageSize=25`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== ticket.current) return
      setCandidates(paged.items.map((x: { revisionId: string; displayNumber: string; title: string; state: string }) => x))
    }, 200)
    return () => clearTimeout(timer)
  }, [api, projectId, releaseId, discipline, query])

  const set = sets.find(x => x.discipline === discipline)
  const inSet = new Set(set?.procedures.map(x => x.procedureRevisionId) ?? [])
  const run = set?.procedures.filter(x => x.latestOutcome) ?? []
  const passed = run.filter(x => x.latestOutcome === 'Pass').length
  const evidenced = (set?.procedures ?? []).filter(x => x.hasEvidence).length

  const act = async (work: () => Promise<void>, failure: string) => {
    if (busy) return
    setBusy(true); setError(''); setSaved('')
    try { await work(); await load() }
    catch (problem) { recordClientOperationFailure('verification.testSet.change', problem); setError(operationError(problem, failure)) }
    finally { setBusy(false) }
  }

  const include = (reason: string, note: string, ids: string[]) => act(async () => {
    if (!ids.length) { setError('Choose at least one procedure.'); return }
    await apiRequest(`${api}/api/releases/${releaseId}/test-sets/${discipline}/procedures`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ procedureRevisionIds: ids, reason, note }),
    })
    setChosen([])
    setSaved(`${ids.length} procedure${ids.length === 1 ? '' : 's'} considered for this build.`)
  }, 'The procedures could not be added to the test set.')

  const exclude = (procedureRevisionId: string) => act(async () => {
    await apiRequest(`${api}/api/releases/${releaseId}/test-sets/${discipline}/procedures/${procedureRevisionId}`, { method: 'DELETE' })
    setSaved('Taken out of the test set. Any result it already has is kept.')
  }, 'The procedure could not be removed from the test set.')

  return (
    <main className="testResultsPage">
      <header>
        <div>
          <p className="eyebrow">VERIFICATION / {discipline === 'System' ? 'SYSTEM' : discipline === 'HighLevelSoftware' ? 'SOFTWARE HLR' : 'SOFTWARE LLR'}</p>
          <h1>Test Results</h1>
          <p>What {buildName} has to run, and what happened when it was run.</p>
        </div>
      </header>
      {error && <div className="workspaceError" role="alert" aria-live="assertive">{error}</div>}
      {saved && <div className="workspaceSaved" role="status">{saved}</div>}

      <section className="testSetSummary" aria-label="Test set progress">
        <article><b>{set?.procedures.length ?? 0}</b><span>In the test set</span></article>
        <article><b>{run.length}</b><span>Recorded</span></article>
        <article><b>{passed}</b><span>Passed</span></article>
        <article><b>{evidenced}</b><span>With evidence</span></article>
      </section>

      <section className="testSetCard">
        <div className="cardTitle">
          <h2>The test set</h2>
          <p>Chosen for this build. The release is measured against exactly this list.</p>
        </div>
        {!set?.procedures.length && (
          <div className="testSetEmpty">
            <b>Nothing has been chosen for this build yet</b>
            <span>A build is rarely worth its whole suite. Choose the procedures covering what changed, and any area worth re-exercising.</span>
          </div>
        )}
        {set?.procedures.map(procedure => (
          <article className="testSetRow" key={procedure.procedureRevisionId}>
            <div>
              <b>{procedure.displayNumber}</b>
              <i className={procedure.latestOutcome ? procedure.latestOutcome.toLowerCase() : 'notrun'}>
                {procedure.latestOutcome ?? 'Not run'}
              </i>
            </div>
            <p>{procedure.title}</p>
            <small>
              {reasonLabel(procedure.reason)}{procedure.note ? ` · ${procedure.note}` : ''} · added by <PersonName userName={procedure.addedBy} />
              {procedure.hasEvidence ? ' · evidence attached' : procedure.latestOutcome ? ' · no evidence yet' : ''}
            </small>
            <div className="testSetRowActions">
              {onOpenProcedure && <button type="button" className="quiet" onClick={() => onOpenProcedure(procedure.procedureRevisionId)}>Open</button>}
              {!readOnly && <button type="button" className="quiet" disabled={busy} onClick={() => void exclude(procedure.procedureRevisionId)}>Remove</button>}
            </div>
          </article>
        ))}
      </section>

      {!readOnly && (
        <section className="testSetCard">
          <div className="cardTitle">
            <h2>Add to the test set</h2>
            <p>Find approved procedures by number or title, then say why this build needs them.</p>
          </div>
          <label className="testSetSearch">
            <span>Find an approved procedure</span>
            <input value={query} onChange={event => setQuery(event.target.value)} placeholder="Procedure number or title" />
          </label>
          <div className="testSetCandidates">
            {candidates.filter(x => !inSet.has(x.revisionId)).map(candidate => (
              <label key={candidate.revisionId}>
                <input
                  type="checkbox"
                  checked={chosen.includes(candidate.revisionId)}
                  onChange={event => setChosen(current => event.target.checked
                    ? [...current, candidate.revisionId]
                    : current.filter(x => x !== candidate.revisionId))}
                />
                <b>{candidate.displayNumber}</b>
                <span>{candidate.title}</span>
              </label>
            ))}
            {!candidates.filter(x => !inSet.has(x.revisionId)).length && (
              <p className="testSetNoCandidates">{query ? 'No approved procedure matches that.' : 'Every approved procedure found is already in the set.'}</p>
            )}
          </div>
          {/* The two routes a lead arrives by, kept apart because the reason is recorded and read later:
              "we tested this because it changed" and "we tested this because we swept the area" are
              different answers to an auditor asking why a build ran what it ran. */}
          <div className="testSetActions">
            <button type="button" disabled={busy || !chosen.length} onClick={() => void include('ChangedRequirement', 'Covers a requirement this build changed.', chosen)}>
              Add — covers a change
            </button>
            <button type="button" disabled={busy || !chosen.length} onClick={() => void include('CoverageArea', 'Area worth re-exercising for this build.', chosen)}>
              Add — area sweep
            </button>
            <button type="button" disabled={busy || !chosen.length} onClick={() => void include('Chosen', 'Judged worth running for this build.', chosen)}>
              Add — judged worth running
            </button>
          </div>
        </section>
      )}
    </main>
  )
}
