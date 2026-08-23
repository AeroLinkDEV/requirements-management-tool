import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import type { AuthUser } from './IdentityCenter'
import { RichCaseField } from './RichContent'
import ProblemReportPicker from './ProblemReportPicker'
import { fromPlainText, toPlainText } from './richContentModel'
import ControlledProcedureEditor from './ControlledProcedureEditor'
import { apiRequest, operationError } from './apiClient'
import type { TestDiscipline } from './TestResultsWorkspace'
import { verificationArtifactNoun, verificationArtifactWord } from './presentation'
import './ChangeRequestEditor.css'

/**
 * Raising a test change request, on a page.
 *
 * This was a pop-up while its requirements counterpart was a full page, and the two are the same act: a
 * controlled proposal, authored with a complete engineering case, that an approver will be asked to sign. A
 * dialog reads as the lesser of the two, and it is not the lesser of the two.
 *
 * Deliberately built on `ChangeRequestEditor.css` rather than a stylesheet of its own — the two-stage rail,
 * the numbered stage cards and the identity row are the same components doing the same job, so they are the
 * same markup. What differs is that stage two proposes procedure changes rather than requirement changes.
 */

type SourceChoice = {
  changeRequestId: string
  displayNumber: string
  title: string
  state: string
  selectable: boolean
  reason?: string
}

type ProcedureChangeKind = 'Introduce' | 'Modify' | 'Retire'

/** One proposed procedure decision, as it stands on the page before the package exists. */
type ProcedureChangeDraft = {
  key: string
  kind: ProcedureChangeKind
  baseNumber: string
  /** The revision this proposal becomes, locked from the library for Modify and Retire. */
  revision: number
  title: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  rationale: string
}

const labelFor = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'
const acronymFor = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'SYSTPCR' : discipline === 'HighLevelSoftware' ? 'HLRTCCR' : 'LLRTCCR'
const levelFor = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HighLevel' : 'LowLevel'

const emptyDraft = (kind: ProcedureChangeKind = 'Introduce'): ProcedureChangeDraft => ({
  key: `draft-${Math.random().toString(36).slice(2)}`,
  kind,
  baseNumber: '',
  revision: 0,
  title: '',
  objective: '',
  preconditions: '',
  steps: '',
  expectedResult: '',
  rationale: '',
})

/**
 * A decision is complete when it says what it does and why.
 *
 * A retirement withdraws a procedure rather than writing one, so it needs the procedure it withdraws and the
 * reason — asking for steps and an expected result would be asking an engineer to invent content for a
 * procedure they are removing.
 */
const draftComplete = (draft: ProcedureChangeDraft) =>
  draft.kind === 'Retire'
    ? draft.baseNumber.trim().length > 0 && draft.rationale.trim().length > 0
    : draft.baseNumber.trim().length > 0
      && draft.title.trim().length > 0
      && draft.objective.trim().length > 0
      && draft.steps.trim().length > 0
      && draft.expectedResult.trim().length > 0
      && draft.rationale.trim().length > 0

export default function TestChangeRequestEditor({
  user,
  api,
  projectId,
  releaseId,
  releaseVersion,
  discipline,
  onCancel,
  onRaised,
}: {
  user: AuthUser
  api: string
  projectId: string
  releaseId: string
  releaseVersion: string
  discipline: TestDiscipline
  onCancel: () => void
  onRaised: (id: string, displayNumber: string) => void
}) {
  const label = labelFor(discipline)
  const acronym = acronymFor(discipline)
  const artifactWord = verificationArtifactWord(discipline)
  const artifactNoun = verificationArtifactNoun(discipline)

  const [title, setTitle] = useState('')
  const [problemRich, setProblemRich] = useState(fromPlainText(''))
  const [analysisRich, setAnalysisRich] = useState(fromPlainText(''))
  const [solutionRich, setSolutionRich] = useState(fromPlainText(''))
  const [choices, setChoices] = useState<SourceChoice[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [problemReportIds, setProblemReportIds] = useState<string[]>([])
  const [procedureChanges, setProcedureChanges] = useState<ProcedureChangeDraft[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [loadError, setLoadError] = useState('')

  const updateDraft = (key: string, change: Partial<ProcedureChangeDraft>) =>
    setProcedureChanges(current => current.map(draft => draft.key === key ? { ...draft, ...change } : draft))

  // The act is chosen before the card exists, as it is on the requirements side, so a reader never commits to
  // a proposal and then has to tell it what it is.
  const addProposal = (kind: ProcedureChangeKind) =>
    setProcedureChanges(current => [...current, emptyDraft(kind)])

  /**
   * The procedures that already verify what the selected changes touched, offered as Modify proposals.
   *
   * When an assessment concludes a change needs test work, the work is almost always re-aligning the
   * procedures that verify the changed requirement — and the engineer had to go and find them by hand. These
   * arrive as ordinary proposals: editable, removable, and not saved until the package is.
   */
  const suggestCoverage = useCallback(async () => {
    if (!selected.length) return
    try {
      const suggestions = await apiRequest<{ baseNumber: string; currentRevision: number; title: string }[]>(
        `${api}/api/releases/${releaseId}/test-change-request-coverage` +
        `?discipline=${discipline}&changeRequestIds=${selected.join(',')}`)
      setProcedureChanges(current => {
        // Never replace what the engineer has written, and never suggest the same procedure twice.
        const already = new Set(current.map(x => x.baseNumber.trim().toUpperCase()).filter(Boolean))
        const additions = suggestions
          .filter(x => !already.has(x.baseNumber.toUpperCase()))
          .map(x => ({
            ...emptyDraft('Modify'),
            baseNumber: x.baseNumber,
            revision: x.currentRevision + 1,
            title: x.title,
          }))
        return additions.length ? [...current, ...additions] : current
      })
    } catch {
      // A suggestion that cannot be fetched is not an error the author needs to act on; the package can still
      // be raised and the procedures added by hand.
    }
  }, [api, discipline, releaseId, selected])

  const load = useCallback(async () => {
    try {
      setLoadError('')
      setChoices(await apiRequest<SourceChoice[]>(
        `${api}/api/releases/${releaseId}/test-change-request-sources?discipline=${discipline}`))
    } catch (failure) {
      setLoadError(operationError(failure, 'The changes this package could answer for could not be loaded.'))
    }
  }, [api, discipline, releaseId])

  useEffect(() => { void load() }, [load])

  const toggle = (id: string) =>
    setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])

  // A package must say what concluded the work was required, and either kind of driver says it: an approved
  // change at this package's own level, or a Problem Report (DEC-113).
  const hasDriver = selected.length > 0 || problemReportIds.length > 0
  const caseComplete = useMemo(() =>
    title.trim().length > 0
    && toPlainText(problemRich).trim().length > 0
    && toPlainText(analysisRich).trim().length > 0
    && toPlainText(solutionRich).trim().length > 0,
  [title, problemRich, analysisRich, solutionRich])

  // Every decision written must be well formed. An empty stage two is allowed — a package may be raised and
  // its procedure work written afterwards — but a half-written decision is not, because the server would
  // refuse it and lose the whole create.
  const proposalsComplete = procedureChanges.every(draftComplete)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (busy || !caseComplete || !hasDriver || !proposalsComplete) return
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
            artifactChanges: procedureChanges.map(draft => ({
              baseNumber: draft.baseNumber.trim(),
              revision: draft.revision,
              level: levelFor(discipline),
              kind: draft.kind,
              title: draft.title.trim(),
              objective: draft.objective.trim(),
              preconditions: draft.preconditions.trim(),
              steps: draft.steps.trim(),
              expectedResult: draft.expectedResult.trim(),
              rationale: draft.rationale.trim(),
            })),
          }),
        })
      onRaised(result.id, result.displayNumber)
    } catch (failure) {
      setError(operationError(failure, 'The test change request could not be raised.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="changeRequestEditor" data-tcr-editor={discipline}>
      <button type="button" className="editorBack" onClick={onCancel}>← Change Requests</button>

      <header className="editorHeading">
        <div>
          <p className="eyebrow">VERIFICATION CHANGE CONTROL / NEW {acronym}</p>
          <h1>Create {label} Test Change Request</h1>
          <p>Build the engineering case and name the {artifactWord} work for Build {releaseVersion}.</p>
        </div>
        <span className="draftChip">DRAFT</span>
      </header>

      {error && <div className="workspaceError" role="alert">{error}</div>}
      {loadError && <div className="workspaceError" role="alert">{loadError}</div>}

      <nav className="authoringStages twoStages" aria-label="Test change authoring progress">
        <a href="#change-case" className={caseComplete ? 'complete' : 'active'}>
          <span>1</span><b>Change case</b><small>{caseComplete ? 'Complete' : 'In progress'}</small>
        </a>
        <a href="#procedure-changes" className={procedureChanges.length && proposalsComplete ? 'complete' : caseComplete ? 'active' : ''}>
          <span>2</span><b>{artifactNoun} changes</b>
          <small>{procedureChanges.length
            ? `${procedureChanges.length} proposal${procedureChanges.length === 1 ? '' : 's'}`
            : '0 proposals'}</small>
        </a>
      </nav>

      <form className="editorForm" onSubmit={submit}>
        <section className="editorCard authoringStage" id="change-case">
          <div className="sectionTitle">
            <span>01</span>
            <div>
              <h2>Change case</h2>
              <p>Identity, ownership, and the complete engineering reason for the test work</p>
            </div>
            <i className={caseComplete ? 'stageState complete' : 'stageState'}>
              {caseComplete ? 'Complete' : 'Required'}
            </i>
          </div>

          <div className="fields three identityFields">
            <label>
              {acronym} number
              <input aria-describedby="tcr-number-help" value="Assigned on save" readOnly />
              <small id="tcr-number-help">A deliberately raised package is numbered the moment it is raised.</small>
            </label>
            <label>
              Target build
              <input value={releaseVersion} readOnly />
            </label>
            <label>
              Author
              <input aria-describedby="tcr-author-help" value={`${user.displayName} (${user.userName})`} readOnly />
              <small id="tcr-author-help">Derived from the authenticated session.</small>
            </label>
            <label className="wide">
              Title
              <input value={title} onChange={event => setTitle(event.target.value)}
                placeholder="A concise, decision-ready description" />
            </label>
          </div>

          <div className="fields three">
            <RichCaseField api={api} projectId={projectId} label="Problem" value={problemRich}
              onChange={setProblemRich} placeholder="What need, defect, or risk exists?" />
            <RichCaseField api={api} projectId={projectId} label="Analysis" value={analysisRich}
              onChange={setAnalysisRich} placeholder="What is affected and what alternatives were considered?" />
            <RichCaseField api={api} projectId={projectId} label="Solution" value={solutionRich}
              onChange={setSolutionRich} placeholder="What controlled outcome is proposed?" />
          </div>

          <fieldset className="tcrSourceChoices">
            <legend>Approved {label} changes this package answers for</legend>
            {choices.length === 0
              ? <p className="drawerEmpty">
                  No approved {label} change requests in this build. A {label} {artifactNoun.toLowerCase()} verifies {label}{' '}
                  requirements, so only {label} changes can drive this package — changes at other levels are
                  not offered.
                </p>
              : choices.map(choice => (
                <label key={choice.changeRequestId} className={choice.selectable ? '' : 'tcrSourceUnavailable'}>
                  <input type="checkbox" checked={selected.includes(choice.changeRequestId)}
                    disabled={!choice.selectable} onChange={() => toggle(choice.changeRequestId)} />
                  <span><b>{choice.displayNumber}</b> {choice.title} <i>{choice.state}</i>
                    {!choice.selectable && choice.reason && <small>{choice.reason}</small>}</span>
                </label>
              ))}
          </fieldset>

          <ProblemReportPicker api={api} projectId={projectId} scope="target-build" releaseId={releaseId}
            selected={problemReportIds} onChange={setProblemReportIds}
            legend={`Problem Reports driving this ${label} TCR`} />

          {!hasDriver && (
            <p className="tcrDriverHint">
              Name at least one driver — an approved {label} change request above, or a Problem Report. A
              package has to say what concluded the test work was required.
            </p>
          )}
        </section>

        <section className="editorCard authoringStage" id="procedure-changes">
          <div className="sectionTitle">
            <span>02</span>
            <div>
              <h2>{artifactNoun} changes</h2>
              <p>Which {label} {artifactWord}s this package introduces, modifies or retires</p>
            </div>
            <i className={procedureChanges.length && proposalsComplete ? 'stageState complete' : 'stageState'}>
              {procedureChanges.length ? (proposalsComplete ? 'Complete' : 'Needs content') : 'Optional'}
            </i>
          </div>

          <p className="stageHelp">
            Written here and saved with the package, the way a change request is created together with the
            requirement changes it proposes. A package may also be raised with none and its {artifactWord} work
            written afterwards.
          </p>
          {/* The three acts a package can propose, offered as three buttons, exactly as the requirements
              editor offers them. One button labelled "add a procedure decision" made the reader choose the
              act from a dropdown after committing to a card. */}
          <div className="proposalActions" aria-label={`Add ${artifactNoun.toLowerCase()} proposal`}>
            <span>Add a focused proposal:</span>
            <button type="button" onClick={() => addProposal("Introduce")}>+ Introduce {label} {artifactWord}</button>
            <button type="button" onClick={() => addProposal("Modify")}>Modify existing</button>
            <button type="button" onClick={() => addProposal("Retire")}>Retire existing</button>
            {/* The common case, one click instead of a hunt: the procedures that already verify what the
                selected changes touched, brought in as Modify proposals to re-align. */}
            {selected.length > 0 && (
              <button type="button" className="suggestCoverage" onClick={() => void suggestCoverage()}>
                Add the {artifactNoun.toLowerCase()}s these changes affect
              </button>
            )}
          </div>

          <div className="proposalStack">
            {procedureChanges.map((draft, index) => (
              <ControlledProcedureEditor
                key={draft.key}
                api={api}
                projectId={projectId}
                releaseId={releaseId}
                scope={discipline}
                levelLabel={label}
                item={draft}
                index={index}
                onChange={(field, value) => updateDraft(draft.key, { [field]: value } as Partial<ProcedureChangeDraft>)}
                onRemove={() => setProcedureChanges(current => current.filter(x => x.key !== draft.key))}
              />
            ))}
          </div>
        </section>

        <div className="editorActions">
          <button type="button" className="quiet" onClick={onCancel} disabled={busy}>Cancel</button>
          <button type="submit" disabled={busy || !caseComplete || !hasDriver || !proposalsComplete}>
            {busy ? 'Raising…' : `Raise ${acronym}`}
          </button>
        </div>
      </form>
    </main>
  )
}
