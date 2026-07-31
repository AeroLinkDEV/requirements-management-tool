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
  latestExecutionId?: string | null
  latestExecutedAt?: string | null
  hasEvidence: boolean
}

/**
 * The moment a person's determination becomes the record.
 *
 * AeroLink never executes anything. Somebody ran the procedure, decided what it showed, and this is where
 * they say so — which is why the determination is written by hand and required, rather than derived from the
 * outcome they picked. "Pass" is a verdict; the determination is the reasoning behind it, and a release
 * reconstructed years later needs the second one.
 */
const localWallTimeNow = () => {
  const now = new Date(), pad = (value: number) => String(value).padStart(2, '0')
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`
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
  const [recording, setRecording] = useState<SetProcedure>()
  const [buildId, setBuildId] = useState('')
  // Tracked so the evidence field can be required exactly where the product requires it. A Pass or a Fail
  // is a claim about what was observed and has to say where the observation is recorded; a Blocked run
  // observed nothing, so demanding evidence of it would be demanding evidence of an absence.
  const [outcome, setOutcome] = useState<'Pass' | 'Fail' | 'Blocked'>('Pass')

  // One ticket per loader, not one for the page. Sharing a counter between two independent loaders makes
  // each cancel the other: the candidate search runs on mount behind a debounce, bumps the count, and the
  // set that was already in flight discards what it read — silently, because nothing failed.
  const loadTicket = useRef(0)
  const candidateTicket = useRef(0)

  const load = useCallback(async () => {
    const mine = ++loadTicket.current
    const response = await fetch(`${api}/api/releases/${releaseId}/test-sets`)
    if (!response.ok) {
      recordClientOperationFailure('verification.testSet.load', new Error(`HTTP ${response.status}`))
      if (mine === loadTicket.current) setError('The test set for this build could not be loaded.')
      return
    }
    const body = await response.json()
    // The build a result is recorded against. A determination that named no build would be a statement about
    // the procedure rather than about anything that shipped.
    const builds = await fetch(`${api}/api/builds?projectId=${projectId}&releaseId=${releaseId}`)
    const built = builds.ok ? await builds.json() : []
    if (mine !== loadTicket.current) return
    setSets(body)
    setBuildId(current => built.some((x: { id: string }) => x.id === current) ? current : built[0]?.id ?? '')
  }, [api, projectId, releaseId])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    const mine = ++candidateTicket.current
    const timer = setTimeout(async () => {
      const scope = discipline === 'System' ? 'System' : 'Software'
      const response = await fetch(`${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}&search=${encodeURIComponent(query)}&state=Approved&page=1&pageSize=25`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== candidateTicket.current) return
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

  const recordResult = (procedure: SetProcedure, form: FormData) => act(async () => {
    const determination = String(form.get('determination') ?? '').trim()
    if (!determination) { setError('Say what the run showed. A verdict without reasoning cannot be read back.'); return }
    await apiRequest(`${api}/api/test-executions`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        projectId,
        procedureRevisionId: procedure.procedureRevisionId,
        softwareBuildId: buildId || null,
        // A retest names the run it supersedes, so a failure and its remedy stay attached to each other.
        retestOfExecutionId: procedure.latestExecutionId ?? null,
        outcome: form.get('outcome'),
        configuration: form.get('configuration'),
        determination,
        evidenceReference: form.get('evidenceReference'),
        executedAt: new Date(String(form.get('executedAt'))).toISOString(),
      }),
    })
    setRecording(undefined)
    setSaved(`Recorded against ${procedure.displayNumber}.`)
  }, 'The result could not be recorded.')

  const attachEvidence = (procedure: SetProcedure, file: File) => act(async () => {
    if (!procedure.latestExecutionId) { setError('Record a result before attaching its evidence.'); return }
    const body = new FormData()
    body.append('file', file)
    body.append('projectId', projectId)
    const evidence = await apiRequest<{ id: string }>(`${api}/api/evidence`, { method: 'POST', body })
    await apiRequest(`${api}/api/test-executions/${procedure.latestExecutionId}/evidence/${evidence.id}`, { method: 'POST' })
    setSaved(`Evidence attached to ${procedure.displayNumber}.`)
  }, 'The evidence could not be stored and linked to this result.')

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
              {!readOnly && (
                <button type="button" disabled={busy} onClick={() => { setOutcome("Pass"); setRecording(procedure) }}>
                  {procedure.latestOutcome ? 'Record retest' : 'Record result'}
                </button>
              )}
              {!readOnly && procedure.latestExecutionId && !procedure.hasEvidence && (
                <label className="evidenceAttach">
                  <span>Attach evidence</span>
                  <input type="file" aria-label={`Attach evidence for ${procedure.displayNumber}`} onChange={event => {
                    const file = event.target.files?.[0]
                    event.target.value = ''
                    if (file) void attachEvidence(procedure, file)
                  }} />
                </label>
              )}
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

      {recording && (
        <div className="recordResultModal" role="dialog" aria-label={`Record a result for ${recording.displayNumber}`}>
          <form onSubmit={event => { event.preventDefault(); void recordResult(recording, new FormData(event.currentTarget)) }}>
            <p className="eyebrow">HUMAN DETERMINATION</p>
            <h2>{recording.displayNumber}</h2>
            <p>{recording.title}</p>
            {/* AeroLink never executes anything. Somebody ran this and decided what it showed; the form asks
                for that decision and for the reasoning behind it, because a verdict alone cannot be read back
                years later by somebody reconstructing why a build was released. */}
            <label>Outcome
              <select name="outcome" value={outcome} onChange={event => setOutcome(event.target.value as "Pass" | "Fail" | "Blocked")}>
                <option value="Pass">Pass</option>
                <option value="Fail">Fail</option>
                <option value="Blocked">Blocked</option>
              </select>
            </label>
            <label>Executed at<input type="datetime-local" name="executedAt" defaultValue={localWallTimeNow()} required /></label>
            <label>Configuration under test<input name="configuration" placeholder="Build, rig, data set" required /></label>
            <label>Determination<textarea name="determination" placeholder="What the run showed, and why it means what it means." required /></label>
            <label>Evidence reference{outcome === "Blocked" ? " (optional)" : ""}<input name="evidenceReference" placeholder="Where the recorded evidence lives" required={outcome !== "Blocked"} /></label>
            <div className="recordResultActions">
              <button type="submit" disabled={busy}>Record determination</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => setRecording(undefined)}>Cancel</button>
            </div>
          </form>
        </div>
      )}
    </main>
  )
}
