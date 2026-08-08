import { useEffect, useState } from 'react'
import { RichCaseField } from './RichContent'
import ProblemReportPicker from './ProblemReportPicker'
import { fromPlainText, toPlainText } from './richContentModel'
import { apiRequest, operationError } from './apiClient'
import type { TestDiscipline } from './TestResultsWorkspace'

type SourceChoice = { changeRequestId: string; displayNumber: string; title: string; state: string; selectable: boolean; reason?: string }

const labelFor = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

/**
 * Raising a test change request deliberately.
 *
 * A manually raised SYSTCR/HLRTCR/LLRTCR is an engineering package, not another assessment: it is itself
 * the conclusion that test work is required, so it is numbered at creation and must name the approved
 * changes whose test work it controls. The case is authored here, the same way a change request's case is,
 * and the package is held by the engineer who raises it (DEC-102).
 */
export default function TestChangeRequestCreateDialog({ api, projectId, releaseId, discipline, onClose, onCreated }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  onClose: () => void
  onCreated: (id: string, displayNumber: string) => void
}) {
  const label = labelFor(discipline)
  const [title, setTitle] = useState('')
  const [problemRich, setProblemRich] = useState(fromPlainText(''))
  const [analysisRich, setAnalysisRich] = useState(fromPlainText(''))
  const [solutionRich, setSolutionRich] = useState(fromPlainText(''))
  const [choices, setChoices] = useState<SourceChoice[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [problemReportIds, setProblemReportIds] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [loadError, setLoadError] = useState('')

  useEffect(() => {
    let active = true
    fetch(`${api}/api/releases/${releaseId}/test-change-request-sources?discipline=${discipline}`)
      .then(async response => {
        if (!response.ok) throw new Error('Selectable source changes could not be loaded.')
        return response.json()
      })
      .then((value: SourceChoice[]) => {
        if (!active) return
        setChoices(value)
      })
      .catch(reason => { if (active) setLoadError(reason instanceof Error ? reason.message : 'Selectable source changes could not be loaded.') })
    return () => { active = false }
  }, [api, discipline, releaseId])

  const toggle = (id: string) =>
    setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])

  const create = async () => {
    if (busy) return
    setBusy(true)
    setError('')
    try {
      const result = await apiRequest<{ id: string; displayNumber: string }>(
        `${api}/api/releases/${releaseId}/test-change-requests`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            discipline,
            changeRequestIds: selected,
            problemReportIds,
            title: title.trim(),
            problem: toPlainText(problemRich),
            analysis: toPlainText(analysisRich),
            solution: toPlainText(solutionRich),
            problemRich,
            analysisRich,
            solutionRich,
          }),
        })
      onCreated(result.id, result.displayNumber)
    } catch (problem) {
      setError(operationError(problem, 'The test change request could not be created.'))
    } finally {
      setBusy(false)
    }
  }

  const complete = title.trim().length > 0
    && toPlainText(problemRich).trim().length > 0
    && toPlainText(analysisRich).trim().length > 0
    && toPlainText(solutionRich).trim().length > 0
    && selected.length > 0

  return (
    <div className="downstreamDialogBackdrop" role="presentation">
      <section className="downstreamDecisionDialog tcrCreateDialog" role="dialog" aria-modal="true" aria-labelledby="tcr-create-title">
        <p className="eyebrow">NEW {label.toUpperCase()} TEST CHANGE REQUEST</p>
        <h2 id="tcr-create-title">Raise a {label} test change request</h2>
        <p>
          A deliberately raised package is itself the conclusion that test work is required, so it receives its
          controlled {label === 'System' ? 'SYSTCR' : label === 'HLR' ? 'HLRTCR' : 'LLRTCR'} number immediately
          and is held by the engineer who raises it.
        </p>
        {error && <div className="workspaceError" role="alert">{error}</div>}
        {loadError && <div className="workspaceError" role="alert">{loadError}</div>}

        <label className="tcrField">Title
          <input value={title} onChange={event => setTitle(event.target.value)} placeholder="What this package is for" />
        </label>
        <div className="tcrCaseFields">
          <RichCaseField api={api} projectId={projectId} label="Problem" value={problemRich}
            onChange={setProblemRich} placeholder="What is affected and why this package exists" />
          <RichCaseField api={api} projectId={projectId} label="Analysis" value={analysisRich}
            onChange={setAnalysisRich} placeholder="What was considered and what it means for the procedures" />
          <RichCaseField api={api} projectId={projectId} label="Solution" value={solutionRich}
            onChange={setSolutionRich} placeholder="What controlled outcome is proposed" />
        </div>

        <fieldset className="tcrSourceChoices">
          <legend>Approved changes this package answers for</legend>
          {choices.length === 0
            ? <p className="drawerEmpty">No approved change requests are available in this build yet. Approve engineering changes first.</p>
            : choices.map(choice => (
              <label key={choice.changeRequestId} className={choice.selectable ? '' : 'tcrSourceUnavailable'}>
                <input type="checkbox" checked={selected.includes(choice.changeRequestId)}
                  disabled={!choice.selectable} onChange={() => toggle(choice.changeRequestId)} />
                <span><b>{choice.displayNumber}</b> {choice.title} <i>{choice.state}</i>
                  {!choice.selectable && choice.reason && <small>{choice.reason}</small>}</span>
              </label>
            ))}
        </fieldset>

        <ProblemReportPicker api={api} projectId={projectId} releaseId={releaseId}
          selected={problemReportIds} onChange={setProblemReportIds}
          legend={`PRs driving this ${label} TCR (optional)`} />

        <div className="downstreamDialogActions">
          <button type="button" className="quiet" disabled={busy} onClick={onClose}>Cancel</button>
          <button type="button" disabled={busy || !complete} onClick={() => void create()}>
            {busy ? 'Raising…' : `Raise ${label === 'System' ? 'SYSTCR' : label === 'HLR' ? 'HLRTCR' : 'LLRTCR'}`}
          </button>
        </div>
      </section>
    </div>
  )
}
