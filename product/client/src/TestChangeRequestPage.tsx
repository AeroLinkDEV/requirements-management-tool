import { useCallback, useEffect, useState } from 'react'
import ControlledAttachments from './ControlledAttachments'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { changeRequestAllocation, changeRequestState, stateLabel } from './presentation'
import type { TestDiscipline } from './TestResultsWorkspace'
import './ChangeRequestWorkspace.css'

/**
 * A test change request, on a page of its own.
 *
 * Clicking one used to open a drawer over the coverage workspace headed "System test engineering decision",
 * which is the assessment's view of it rather than the package's own. There was no way to read a package the
 * way a change request is read: its case, what it proposes, who signed it, what it was raised from, and the
 * controlled document an approver takes away.
 *
 * This is the change request page over a package. The sections, their order and their names are the same,
 * because a reader moving between them is doing the same job on a different artifact.
 */

type Release = { id: string; version: string; isReleased: boolean }

type ProcedureChange = {
  id: string
  displayNumber: string
  baseNumber: string
  revision: number
  level: string
  kind: string
  title: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  rationale: string
}

type Package = {
  id: string
  projectId: string
  releaseId: string
  displayNumber: string
  baseNumber: string
  revision: number
  title: string
  problem: string
  analysis: string
  solution: string
  state: string
  deferredFromState?: string | null
  deferralReason?: string
  authorId: string
  assignedEngineerId?: string | null
  discipline: string
  sourceChangeRequestNumber: string
  version: number
  procedureChanges: ProcedureChange[]
  coveredChangeRequests: { id: string; number: string; title: string; originating: boolean }[]
}

const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

const changeKindLabel = (kind: string) =>
  kind === 'Introduce' ? 'New procedure' : kind === 'Retire' ? 'Retired' : 'Modified'

export default function TestChangeRequestPage({
  api, releaseId, releases, packageId, discipline, currentUser, onBack,
}: {
  api: string
  releaseId: string
  releases: Release[]
  packageId: string
  discipline: TestDiscipline
  currentUser: string
  onBack: () => void
}) {
  const [item, setItem] = useState<Package>()
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setError('')
    try {
      // The package's own read carries its case and its procedure changes together. The register's list
      // carries a count rather than the changes themselves — enough for a row, not for a page.
      const detail = await apiRequest<Package>(`${api}/api/test-change-reviews/${packageId}/procedure-changes`)
      // What it was raised from, and where it sits, come from the register's read; the detail read does not
      // carry them, and inventing either on a controlled record would be worse than asking twice.
      const list = await apiRequest<{ items: Package[] }>(`${api}/api/releases/${releaseId}/test-change-reviews`)
      const row = list.items.find(x => x.id === packageId)
      if (!row && !detail) { setError('That test change request is not in this build.'); return }
      setItem({ ...detail, ...(row ?? {}), procedureChanges: detail.procedureChanges ?? [] })
    } catch (reason) {
      setError(operationError(reason, 'The test change request could not be loaded.'))
    }
  }, [api, packageId, releaseId])

  useEffect(() => { void load() }, [load])

  const act = async (work: () => Promise<void>, failure: string, success: string) => {
    setBusy(true); setError(''); setSaved('')
    try { await work(); setSaved(success); await load() }
    catch (reason) { setError(operationError(reason, failure)) }
    finally { setBusy(false) }
  }

  const defer = () => {
    const reason = prompt('Why is this package being put away?')
    if (reason === null || !reason.trim()) return
    void act(
      () => apiRequest(`${api}/api/test-change-reviews/${packageId}/defer`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason: reason.trim() }),
      }),
      'The package could not be deferred.', 'Deferred.')
  }

  const reinstate = () => void act(
    () => apiRequest(`${api}/api/test-change-reviews/${packageId}/reinstate`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}',
    }),
    'The package could not be reinstated.', 'Back off the shelf.')

  if (!item) {
    return <main className="scrWorkspace">
      {error
        ? <div className="workspaceError" role="alert">{error}</div>
        : <p className="workspaceLoading">Loading the test change request…</p>}
    </main>
  }

  // The same two facts the requirements page shows, read through the same helpers so a deferred package reads
  // identically on both sides: allocation says where it sits, state says how far it got.
  const facts = {
    state: item.state,
    deferredFromState: item.deferredFromState,
    targetRelease: releases.find(x => x.id === item.releaseId),
    superseded: item.state === 'Superseded',
  }
  const isAuthor = !item.authorId || item.authorId.toLowerCase() === currentUser.toLowerCase()
  const editable = item.state === 'Draft'
  const raisedFrom = item.coveredChangeRequests.find(x => x.originating)

  return <main className="scrWorkspace">
    <header className="scrHeader">
      <div>
        <button className="back" type="button" onClick={onBack}>← {disciplineLabel(discipline)} Test Change Requests</button>
        <p className="eyebrow">TEST CHANGE CONTROL / {item.displayNumber}</p>
        <h1>{item.title || 'Not written up yet'}</h1>
        <p>Revision-controlled change case, procedure proposals, and review authority.</p>
      </div>
      <div className="headerState">
        <span className={`stateBadge ${item.state.toLowerCase()}`} data-state={item.state}>
          {changeRequestAllocation(facts)} · {changeRequestState(facts)}
        </span>
        <small>Record version {item.version}</small>
        <div className="scrPublicationTools">
          <span>Professional controlled publication</span>
          <a href={`${api}/api/test-change-reviews/${item.id}/download?format=docx`}>Download DOCX</a>
          <a href={`${api}/api/test-change-reviews/${item.id}/download?format=pdf`}>Download PDF</a>
        </div>
      </div>
    </header>

    {error && <div className="workspaceError" role="alert">{error}</div>}
    {saved && <div className="workspaceSaved" role="status">✓ {saved}</div>}

    <div className="scrLayout">
      <div className="scrMain">
        <section className="workspaceCard">
          <div className="workspaceTitle">
            <div><h2>Change case</h2><p>Problem, analysis, and proposed solution</p></div>
            <div className="workspaceActions">
              {editable && isAuthor && (
                <button type="button" className="primary" disabled={busy}
                  onClick={() => { window.location.href = `${window.location.pathname.replace(/\/[^/]*$/, '')}/new?package=${item.id}` }}>
                  Check out &amp; edit
                </button>
              )}
              {editable && isAuthor && (
                <button type="button" className="quiet" disabled={busy} onClick={defer}>
                  {busy ? 'Deferring…' : 'Defer'}
                </button>
              )}
              {item.state === 'Deferred' && isAuthor && (
                <button type="button" className="quiet" disabled={busy} onClick={reinstate}>Reinstate</button>
              )}
            </div>
          </div>
          {item.state === 'Deferred' && item.deferralReason && (
            <p className="snapshotNote">Put away because: {item.deferralReason}</p>
          )}
          <div className="caseRecord"><span>P</span><div><b>Problem</b><p>{item.problem || 'Not written up yet'}</p></div></div>
          <div className="caseRecord"><span>A</span><div><b>Analysis</b><p>{item.analysis || 'Not written up yet'}</p></div></div>
          <div className="caseRecord"><span>S</span><div><b>Solution</b><p>{item.solution || 'Not written up yet'}</p></div></div>
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle">
            <div><h2>Raised from</h2><p>What concluded that this test work was required</p></div>
          </div>
          {raisedFrom
            ? <p className="sourceRecord"><b>{raisedFrom.number}</b> {raisedFrom.title}</p>
            : <p className="sourceRecord"><b>{item.sourceChangeRequestNumber || 'A Problem Report'}</b></p>}
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle">
            <div><h2>Supporting files</h2><p>Evidence an approver needs alongside the change case</p></div>
          </div>
          {/* The same component the change request uses, against this artifact type. Evidence belongs beside
              the record it justifies. */}
          <ControlledAttachments
            api={api}
            projectId={item.projectId}
            artifactType="TestChangeRequest"
            artifactId={item.id}
            canAttach={editable && isAuthor}
          />
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle">
            <div>
              <h2>Procedure impact</h2>
              <p>{item.procedureChanges.length} proposed controlled change{item.procedureChanges.length === 1 ? '' : 's'}</p>
            </div>
          </div>
          {!item.procedureChanges.length && (
            <p className="workspaceEmpty">No procedure changes are proposed yet.</p>
          )}
          {item.procedureChanges.map(change => (
            <article className="requirementView" key={change.id} data-procedure-change={change.displayNumber}>
              <div><b>{change.displayNumber}</b><span>{changeKindLabel(change.kind)}</span></div>
              <p>{change.objective || change.title}</p>
              {change.rationale && <small>{change.rationale}</small>}
            </article>
          ))}
        </section>
      </div>

      <aside className="scrRail">
        <section className="workspaceCard controlStatusCard">
          <div className="workspaceTitle"><div><h2>Control status</h2><p>{item.displayNumber}</p></div></div>
          <dl>
            <div><dt>Allocation</dt><dd data-allocation={item.state === 'Deferred' ? 'Deferred' : 'Build'}>{changeRequestAllocation(facts)}</dd></div>
            <div><dt>State</dt><dd data-state={item.state}>{changeRequestState(facts)}</dd></div>
            <div><dt>Author</dt><dd>{item.authorId ? <PersonName userName={item.authorId} withRole /> : 'Raised by assessment'}</dd></div>
            <div><dt>Discipline</dt><dd>{stateLabel(item.discipline)}</dd></div>
            <div><dt>Revision</dt><dd>{item.revision}</dd></div>
          </dl>
          {editable && isAuthor && !item.procedureChanges.length && (
            <div className="railReadiness">
              <b>Draft needs authoring</b>
              <span>Complete the procedure proposals.</span>
            </div>
          )}
        </section>
      </aside>
    </div>
  </main>
}
