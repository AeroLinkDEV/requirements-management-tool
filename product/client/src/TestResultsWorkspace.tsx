import { useCallback, useEffect, useRef, useState } from 'react'
import { PersonName } from './People'
import type { AuthUser } from './IdentityCenter'
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
const localWallTime = (instant = new Date()) => {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${instant.getFullYear()}-${pad(instant.getMonth() + 1)}-${pad(instant.getDate())}T${pad(instant.getHours())}:${pad(instant.getMinutes())}:${pad(instant.getSeconds())}`
}
type TestSet = { id: string; discipline: TestDiscipline; releaseId: string; version: number; procedures: SetProcedure[] }
type Execution = {
  id: string
  procedureRevisionId: string
  displayNumber: string
  outcome: string
  executedBy: string
  determination: string
  evidenceReference: string
  executedAt: string
  retestOfExecutionId?: string
  evidence: { id: string; originalFileName: string }[]
}
type Candidate = { revisionId: string; displayNumber: string; title: string; state: string }
/// What a problem report is asking somebody to do here: run a named procedure again and record the result.
type CorrectiveAction = {
  problemReportId: string
  problemReportNumber: string
  available: boolean
  discipline: string | null
  reason: string
  executionId?: string
  procedureId?: string
  procedureRevisionId?: string
  procedureNumber?: string
  procedureTitle?: string
  requiredRole: string
}

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
export default function TestResultsWorkspace({ api, projectId, releaseId, discipline, buildName, readOnly, programId, user, correctiveProblemReportId, onOpenProcedure }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  buildName: string
  readOnly: boolean
  programId: string
  user: AuthUser
  /// Carried in the route, so refreshing or going back returns to the same remediation.
  correctiveProblemReportId?: string
  onOpenProcedure?: (procedureRevisionId: string) => void
}) {
  // Recording a determination is a Test Engineer's act, and the server refuses anybody else. Reflected here
  // so the page says who may do it rather than offering a control that answers 403.
  const roles = user.programs.find(program => program.programId === programId)?.roles ?? []
  const canTest = !readOnly && (user.isAdministrator || roles.includes('TestEngineer'))
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
  const [executions, setExecutions] = useState<Execution[]>([])
  const [showRuns, setShowRuns] = useState('')
  // Set when a retest supersedes a specific earlier run rather than simply the latest one, which is what a
  // corrective action does: it answers a named failure, not "whatever happened last".
  const [supersedesExecutionId, setSupersedesExecutionId] = useState<string>()
  const [corrective, setCorrective] = useState<CorrectiveAction>()

  const openRecording = (procedure: SetProcedure, predecessorId?: string | null) => {
    setOutcome('Pass')
    setSupersedesExecutionId(predecessorId ?? undefined)
    setRecording(procedure)
  }
  const recordingTime = () => {
    const predecessor = executions.find(run => run.id === supersedesExecutionId)
    const successorFloor = predecessor ? new Date(predecessor.executedAt).getTime() + 1_000 : 0
    return localWallTime(new Date(Math.max(Date.now(), successorFloor)))
  }

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
    // Every run against this build, so a determination can be read with the ones before it. A result read
    // alone says what happened; read with its history it says whether the build is getting better or worse.
    const runs = await fetch(`${api}/api/test-executions?projectId=${projectId}&releaseId=${releaseId}`)
    const ran = runs.ok ? await runs.json() : []
    if (mine !== loadTicket.current) return
    setExecutions(ran)
    setSets(body)
    setBuildId(current => built.some((x: { id: string }) => x.id === current) ? current : built[0]?.id ?? '')
  }, [api, projectId, releaseId])

  useEffect(() => { void load() }, [load])

  // Arriving from a problem report. The page says which record is being corrected and offers the retest
  // against the procedure that failed, rather than leaving somebody to find it among everything the build
  // runs. A corrective retest answers a named execution, which is why the run itself is carried.
  useEffect(() => {
    if (!correctiveProblemReportId) { setCorrective(undefined); return }
    let cancelled = false
    void (async () => {
      const response = await fetch(`${api}/api/problem-reports/${correctiveProblemReportId}/corrective-action`)
      if (!response.ok || cancelled) return
      const target = await response.json() as CorrectiveAction
      if (!cancelled) setCorrective(target)
    })()
    return () => { cancelled = true }
  }, [api, correctiveProblemReportId])

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
    const execution = await apiRequest<{ id: string }>(`${api}/api/test-executions`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        projectId,
        procedureRevisionId: procedure.procedureRevisionId,
        softwareBuildId: buildId || null,
        // A retest names the run it supersedes, so a failure and its remedy stay attached to each other.
        // When a specific earlier run was chosen it is that one, not merely the latest — a corrective action
        // answers a named failure.
        retestOfExecutionId: supersedesExecutionId ?? procedure.latestExecutionId ?? null,
        outcome: form.get('outcome'),
        configuration: form.get('configuration'),
        determination,
        evidenceReference: form.get('evidenceReference'),
        executedAt: new Date(String(form.get('executedAt'))).toISOString(),
      }),
    })
    // When the engineer arrived from a Verifying PR and ran its identified procedure, a passing result is
    // explicitly selected as closure evidence. Other passing runs remain ordinary build evidence.
    if (correctiveProblemReportId && corrective?.procedureRevisionId === procedure.procedureRevisionId && form.get('outcome') === 'Pass') {
      const report = await apiRequest<{ version: number; state: string }>(`${api}/api/problem-reports/${correctiveProblemReportId}`)
      if (report.state === 'Verifying') {
        await apiRequest(`${api}/api/problem-reports/${correctiveProblemReportId}/verify`, {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ expectedVersion: report.version, testExecutionId: execution.id }),
        })
        setSaved(`Recorded against ${procedure.displayNumber} and selected as PR closure evidence.`)
      }
    }
    setRecording(undefined)
    setSupersedesExecutionId(undefined)
    if (!correctiveProblemReportId || corrective?.procedureRevisionId !== procedure.procedureRevisionId || form.get('outcome') !== 'Pass')
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

      {corrective && (
        <section className="correctiveBanner" role="status" aria-label="Corrective verification action">
          <div>
            <p className="eyebrow">CORRECTING {corrective.problemReportNumber}</p>
            <b>{corrective.procedureNumber
              ? `Record a passing successor execution against ${corrective.procedureNumber}`
              : 'Record a passing successor execution'}</b>
            <p>{corrective.reason}</p>
          </div>
          {(() => {
            if (readOnly) return <span className="correctiveHint">This build is released. Its results are read-only.</span>
            const target = set?.procedures.find(x => x.procedureRevisionId === corrective.procedureRevisionId)
            if (!target) return <span className="correctiveHint">Add {corrective.procedureNumber ?? 'the procedure'} to this build&apos;s test set below, then record its result.</span>
            return <button type="button" disabled={busy} onClick={() => openRecording(target, corrective.executionId)}>Record successor execution →</button>
          })()}
        </section>
      )}

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
              {!readOnly && (canTest
                ? (
                  <button type="button" disabled={busy} onClick={() => openRecording(procedure, procedure.latestExecutionId)}>
                    {procedure.latestOutcome ? 'Record retest' : 'Record result'}
                  </button>
                )
                : <span className="procedureHold">Test Engineer authority is required to record results in this Program.</span>
              )}
              {canTest && procedure.latestExecutionId && !procedure.hasEvidence && (
                <label className="evidenceAttach">
                  <span>Attach evidence</span>
                  <input type="file" aria-label={`Attach evidence for ${procedure.displayNumber}`} onChange={event => {
                    const file = event.target.files?.[0]
                    event.target.value = ''
                    if (file) void attachEvidence(procedure, file)
                  }} />
                </label>
              )}
              {procedure.latestExecutionId && (
                <button type="button" className="quiet" onClick={() => setShowRuns(current => current === procedure.procedureRevisionId ? '' : procedure.procedureRevisionId)}>
                  {showRuns === procedure.procedureRevisionId ? 'Hide runs' : 'Runs'}
                </button>
              )}
              {onOpenProcedure && <button type="button" className="quiet" onClick={() => onOpenProcedure(procedure.procedureRevisionId)}>Open</button>}
              {!readOnly && <button type="button" className="quiet" disabled={busy} onClick={() => void exclude(procedure.procedureRevisionId)}>Remove</button>}
            </div>
            {/* Every run against this build, newest first. A determination read alone says what happened;
                read beside the ones before it, it says whether the build is getting better or worse — and a
                retest can then answer the specific failure rather than whatever happened last. */}
            {showRuns === procedure.procedureRevisionId && (
              <ol className="runList">
                {executions
                  .filter(x => x.procedureRevisionId === procedure.procedureRevisionId)
                  .map(run => (
                    <li key={run.id}>
                      <i className={run.outcome.toLowerCase()}>{run.outcome}</i>
                      <b>{new Date(run.executedAt).toLocaleDateString()}</b>
                      <small><PersonName userName={run.executedBy} /> · {run.determination}</small>
                      {run.evidence.length > 0 && <span className="runEvidence">{run.evidence.length} evidence file{run.evidence.length === 1 ? '' : 's'}</span>}
                      {run.retestOfExecutionId && <span className="runEvidence">retest</span>}
                      {canTest && run.outcome !== 'Pass' && (
                        <button type="button" className="quiet" disabled={busy} onClick={() => openRecording(procedure, run.id)}>Retest this run</button>
                      )}
                    </li>
                  ))}
                {!executions.some(x => x.procedureRevisionId === procedure.procedureRevisionId) && <li className="runNone">No run is recorded against this build yet.</li>}
              </ol>
            )}
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
            {/* Who is signing this determination, stated rather than assumed. It is the authenticated account
                either way — the server takes it from the session — but somebody recording a result on a
                shared rig needs to see whose name is going on it before they commit. */}
            <label>Executed by / human determination owner
              <input value={`${user.displayName} (${user.userName})`} readOnly aria-readonly="true" />
            </label>
            <label>Execution time
              <input type="datetime-local" name="executedAt" step="1" aria-describedby="execution-time-help" defaultValue={recordingTime()} required />
              {/* The field is a wall clock and the record is an instant. Saying which zone the wall clock is
                  in is the difference between a reader trusting the time and having to work it out. */}
              <small id="execution-time-help">Local time, {Intl.DateTimeFormat().resolvedOptions().timeZone}. Stored as an exact instant.</small>
            </label>
            <label>Configuration under test<input name="configuration" placeholder="Build, rig, data set" required /></label>
            <label>Determination<textarea name="determination" placeholder="What the run showed, and why it means what it means." required /></label>
            <label>Evidence reference{outcome === "Blocked" ? " (optional)" : ""}<input name="evidenceReference" placeholder="Where the recorded evidence lives" required={outcome !== "Blocked"} /></label>
            <div className="recordResultActions">
              <button type="submit" disabled={busy}>Record determination</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => { setRecording(undefined); setSupersedesExecutionId(undefined) }}>Cancel</button>
            </div>
          </form>
        </div>
      )}
    </main>
  )
}
