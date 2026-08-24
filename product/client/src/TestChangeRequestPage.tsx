import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import ControlledAttachments from './ControlledAttachments'
import ControlledProcedureEditor, {
  type ProcedureChangeKind,
  type ProcedureProposal,
} from './ControlledProcedureEditor'
import {
  ControlledChangeAuthoringActions,
  ControlledChangeAuthoringForm,
  ControlledChangeCaseCard,
  ControlledChangePage,
  ControlledChangeReadLayout,
  ControlledStatusCard,
} from './ControlledChangePage'
import { PersonName } from './People'
import ReviewCycleCard, { type ReviewCycleSummary } from './ReviewCycleCard'
import { RichCaseField, RichContentView } from './RichContent'
import { ApiError, apiRequest, operationError } from './apiClient'
import { useDebouncedSave } from './autosave'
import { fromPlainText, toPlainText } from './richContentModel'
import { changeRequestAllocation, changeRequestState, isVerificationProcedureKind, verificationArtifactChangeSegment, verificationArtifactNoun, verificationArtifactWord, verificationOriginLabel } from './presentation'
import type { TestDiscipline } from './TestResultsWorkspace'
import './ChangeRequestWorkspace.css'

type Release = { id: string; version: string; isReleased: boolean }

type ProcedureChange = {
  id: string
  displayNumber: string
  baseNumber: string
  revision: number
  level: string
  kind: ProcedureChangeKind
  title: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  rationale: string
  drivingRequirementRevisionIdsJson?: string
  removedRequirementRevisionIdsJson?: string
  coverageChangeRationale?: string
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
  problemRich?: string
  analysisRich?: string
  solutionRich?: string
  state: string
  deferredFromState?: string | null
  deferralReason?: string
  authorId: string
  assignedEngineerId?: string | null
  discipline: string
  sourceChangeRequestNumber: string
  originKind?: string
  originReferenceId?: string
  originDisplayIdentity?: string
  originDisplayTitle?: string
  originDisplayLabel?: string
  version: number
  updatedAt?: string
  artifactKind?: string
  artifactLabel?: string
  artifactLevel?: string
  procedureLevel?: string
  artifactChanges?: ProcedureChange[]
  capabilities?: {
    canProposeArtifactChange?: boolean
    canWithdrawArtifactChange?: boolean
    canProposeProcedureChange?: boolean
    canWithdrawProcedureChange?: boolean
    canRevise?: boolean
    canApprove?: boolean
    canReturn?: boolean
  }
  reviewCycle?: ReviewCycleSummary
  procedureChanges?: ProcedureChange[]
  coveredChangeRequests: { id: string; number: string; title: string; originating: boolean }[]
}

type SignatureEvidence = {
  id: string
  displayName: string
  action: string
  meaning: string
  artifactRevision: string
  contentHash: string
  signedAt: string
  signatureStatus?: string
  isSuperseded?: boolean
  supersession?: { migration?: string; reason?: string; oldArtifactIdentity?: string; oldSignatureHash?: string; newArtifactIdentity?: string; newContentHash?: string }
}

type EditLock = {
  id: string
  version: number
  userName: string
  openedAt: string
  lastActivityAt: string
  expiresAt: string
  draftJson: string
  resumed: boolean
}

type LockStatus = {
  editable: boolean
  locked: boolean
  holder?: string
  mine?: boolean
  lastActivityAt?: string
  expiresAt?: string
}

type ProcedureDraft = ProcedureProposal & {
  level: string
  drivingRequirementRevisionIdsJson: string
  removedRequirementRevisionIdsJson: string
  coverageChangeRationale: string
}

type WorkingDraft = {
  packageVersion: number
  title: string
  problem: string
  analysis: string
  solution: string
  problemRich: string
  analysisRich: string
  solutionRich: string
  procedureChanges: ProcedureDraft[]
}

const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

const procedureLevel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HighLevel' : 'LowLevel'

const changeKindLabel = (kind: string) =>
  kind === 'Introduce' ? 'Introduction' : kind === 'Retire' ? 'Retirement' : 'Modification'

const emptyProcedure = (discipline: TestDiscipline, kind: ProcedureChangeKind): ProcedureDraft => ({
  key: `draft-${crypto.randomUUID()}`,
  kind,
  baseNumber: '',
  revision: 0,
  level: procedureLevel(discipline),
  title: '',
  objective: '',
  preconditions: '',
  steps: '',
  expectedResult: '',
  rationale: '',
  drivingRequirementRevisionIdsJson: '[]',
  removedRequirementRevisionIdsJson: '[]',
  coverageChangeRationale: '',
})

const proposalCanCheckIn = (proposal: ProcedureDraft) => proposal.kind === 'Retire'
  ? Boolean(proposal.baseNumber.trim() && proposal.rationale.trim())
  : Boolean(
      proposal.baseNumber.trim()
      && proposal.title.trim()
      && proposal.objective.trim()
      && proposal.steps.trim()
      && proposal.rationale.trim())

const normalizeCheckedOutDraft = (value: string, item: Package): WorkingDraft => {
  const parsed = JSON.parse(value) as Partial<WorkingDraft> & { procedureChanges?: Partial<ProcedureDraft>[] }
  return {
    packageVersion: parsed.packageVersion ?? item.version,
    title: parsed.title ?? '',
    problem: parsed.problem ?? '',
    analysis: parsed.analysis ?? '',
    solution: parsed.solution ?? '',
    problemRich: parsed.problemRich || fromPlainText(parsed.problem ?? ''),
    analysisRich: parsed.analysisRich || fromPlainText(parsed.analysis ?? ''),
    solutionRich: parsed.solutionRich || fromPlainText(parsed.solution ?? ''),
    procedureChanges: (parsed.procedureChanges ?? []).map((change, index) => ({
      ...emptyProcedure(item.discipline as TestDiscipline, change.kind ?? 'Introduce'),
      ...change,
      key: change.key || `checkout-${change.baseNumber || index}-${change.revision ?? 0}`,
      level: change.level || procedureLevel(item.discipline as TestDiscipline),
      drivingRequirementRevisionIdsJson: change.drivingRequirementRevisionIdsJson ?? '[]',
      removedRequirementRevisionIdsJson: change.removedRequirementRevisionIdsJson ?? '[]',
      coverageChangeRationale: change.coverageChangeRationale ?? '',
    })),
  }
}

export default function TestChangeRequestPage({
  api, releaseId, releases, packageId, discipline, currentUser, onBack,
  onOpenTestChangeRequest,
}: {
  api: string
  releaseId: string
  releases: Release[]
  packageId: string
  discipline: TestDiscipline
  currentUser: string
  onBack: () => void
  onOpenRequirementRevision: (requirement: { id: string; revisionId: string; level: string }) => void
  onOpenTestChangeRequest: (id: string) => void
}) {
  const [item, setItem] = useState<Package>()
  const [signatures, setSignatures] = useState<SignatureEvidence[]>([])
  const [signatureError, setSignatureError] = useState('')
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')
  const [busy, setBusy] = useState(false)
  const [mode, setMode] = useState<'view' | 'edit'>('view')
  const [lock, setLock] = useState<EditLock>()
  const [lockStatus, setLockStatus] = useState<LockStatus>()
  const [autosaveStatus, setAutosaveStatus] = useState<'Saved' | 'Unsaved' | 'Saving' | 'Error' | 'Conflict'>('Saved')
  const [draft, setDraft] = useState<WorkingDraft>()
  const artifactWord = verificationArtifactWord(item?.artifactLevel ?? discipline, item?.artifactKind)
  const artifactNoun = verificationArtifactNoun(item?.artifactLevel ?? discipline, item?.artifactKind)
  const lockRef = useRef<EditLock | undefined>(undefined)
  const draftRef = useRef('')
  const lastSavedRef = useRef('')
  const checkoutSnapshotRef = useRef('')
  const savingRef = useRef(false)

  const load = useCallback(async () => {
    setError('')
    setSignatureError('')
    try {
      const initialSegment = verificationArtifactChangeSegment(discipline)
      const detailPromise = apiRequest<Package>(`${api}/api/test-change-reviews/${packageId}/${initialSegment}`).catch(problem => {
        // A software Procedure package can be opened from a discipline-only deep link. Resolve its
        // exact kind through the Procedure route without broadening the server's Case route.
        if (initialSegment !== 'case-changes' || discipline === 'System' || !(problem instanceof ApiError) || problem.status !== 404) throw problem
        return apiRequest<Package>(`${api}/api/test-change-reviews/${packageId}/procedure-changes`)
      })
      const [detail, list] = await Promise.all([
        detailPromise,
        apiRequest<{ items: Package[] }>(`${api}/api/releases/${releaseId}/test-change-reviews`),
      ])
      let signatureRows: SignatureEvidence[] = []
      try {
        signatureRows = await apiRequest<SignatureEvidence[]>(`${api}/api/signatures?artifactId=${packageId}`)
      } catch (reason) {
        setSignatureError(operationError(reason, 'Signature evidence could not be loaded.'))
      }
      const row = list.items.find(x => x.id === packageId)
      setSignatures(signatureRows)
      setItem({
        ...detail,
        ...(row ?? {}),
        capabilities: { ...detail.capabilities, ...(row?.capabilities ?? {}) },
        artifactChanges: detail.artifactChanges ?? detail.procedureChanges ?? [],
        procedureChanges: detail.artifactChanges ?? detail.procedureChanges ?? [],
      })
    } catch (reason) {
      setError(operationError(reason, 'The test change request could not be loaded.'))
    }
  }, [api, discipline, packageId, releaseId])

  const loadStatus = useCallback(async () => {
    const response = await fetch(`${api}/api/controlled-editing/status?artifactType=TestChangeRequest&artifactId=${packageId}`)
    if (response.ok) setLockStatus(await response.json())
  }, [api, packageId])

  useEffect(() => { void load(); void loadStatus() }, [load, loadStatus])

  const serializedWorkingCopy = useMemo(() => draft ? JSON.stringify(draft) : '', [draft])
  useEffect(() => { lockRef.current = lock }, [lock])
  useEffect(() => {
    draftRef.current = serializedWorkingCopy
    if (mode === 'edit' && serializedWorkingCopy && serializedWorkingCopy !== lastSavedRef.current)
      setAutosaveStatus(current => current === 'Saving' || current === 'Conflict' ? current : 'Unsaved')
  }, [mode, serializedWorkingCopy])

  const autosave = useCallback(async (): Promise<EditLock | undefined> => {
    const current = lockRef.current
    const currentDraft = draftRef.current
    if (!current || savingRef.current || !currentDraft || currentDraft === lastSavedRef.current) return current
    savingRef.current = true
    setAutosaveStatus('Saving')
    try {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/autosave`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, draftJson: currentDraft, leaseMinutes: 15 }),
      })
      if (!response.ok) {
        setAutosaveStatus(response.status === 409 ? 'Conflict' : 'Error')
        const body = await response.json() as { error?: string }
        setError(body.error || 'Server autosave failed.')
        return undefined
      }
      const value = await response.json() as { version: number; updatedAt: string; expiresAt: string }
      const next = { ...current, version: value.version, lastActivityAt: value.updatedAt, expiresAt: value.expiresAt }
      setLock(next); lockRef.current = next; lastSavedRef.current = currentDraft; setAutosaveStatus('Saved')
      return next
    } catch {
      setAutosaveStatus('Error')
      return undefined
    } finally {
      savingRef.current = false
    }
  }, [api])

  useDebouncedSave(serializedWorkingCopy, async () => { await autosave() }, {
    delaySeconds: 1,
    maximumSeconds: 10,
    enabled: mode === 'edit',
  })

  useEffect(() => {
    if (mode !== 'edit' || !lockRef.current) return
    const heartbeat = window.setInterval(async () => {
      const current = lockRef.current
      if (!current || savingRef.current) return
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/heartbeat`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, leaseMinutes: 15 }),
      })
      if (!response.ok) { setAutosaveStatus('Conflict'); return }
      const value = await response.json() as { version: number; updatedAt: string; expiresAt: string }
      const next = { ...current, version: value.version, lastActivityAt: value.updatedAt, expiresAt: value.expiresAt }
      setLock(next); lockRef.current = next
    }, 60_000)
    return () => window.clearInterval(heartbeat)
  }, [api, mode])

  const beginEdit = async () => {
    if (!item) return
    setBusy(true); setError(''); setSaved('')
    try {
      const response = await fetch(`${api}/api/controlled-editing/checkout`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ artifactType: 'TestChangeRequest', artifactId: item.id, leaseMinutes: 15 }),
      })
      if (!response.ok) {
        const body = await response.json() as { error?: string }
        setError(body.error || 'This Draft could not be checked out.')
        await loadStatus()
        return
      }
      const opened = await response.json() as EditLock
      const recovered = normalizeCheckedOutDraft(opened.draftJson, item)
      const serialized = JSON.stringify(recovered)
      setLock(opened); lockRef.current = opened; setDraft(recovered)
      draftRef.current = serialized; lastSavedRef.current = serialized; checkoutSnapshotRef.current = serialized
      setAutosaveStatus('Saved'); setMode('edit'); await loadStatus()
    } catch (reason) {
      setError(operationError(reason, 'This Draft could not be checked out.'))
    } finally { setBusy(false) }
  }

  const discard = async () => {
    const current = lockRef.current
    if (current) {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/discard`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, reason: 'Author discarded the checked-out working copy.' }),
      })
      if (!response.ok) {
        const body = await response.json() as { error?: string }
        setError(body.error || 'The checkout could not be discarded.')
        return
      }
    }
    setLock(undefined); lockRef.current = undefined; setDraft(undefined); checkoutSnapshotRef.current = ''
    setMode('view'); await load(); await loadStatus()
  }

  const saveWorkingCopy = async () => {
    setError(''); setSaved('')
    if (await autosave()) setSaved('Working copy saved. Checkout remains active.')
  }

  const checkIn = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    // Matches the button beside it. An unfinished proposal is checked in as it stands.
    if (!draft || !lockRef.current || !draft.title.trim()) {
      setError('Give the Draft a title before checking it in.')
      return
    }
    setBusy(true); setError(''); setSaved('')
    try {
      while (savingRef.current) await new Promise(resolve => window.setTimeout(resolve, 25))
      const current = await autosave()
      if (!current) { setError('The latest recovery snapshot could not be saved for check-in.'); return }
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/check-in`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version }),
      })
      if (!response.ok) {
        const body = await response.json() as { error?: string }
        setError(body.error || 'Draft could not be saved.')
        return
      }
      setLock(undefined); lockRef.current = undefined; setDraft(undefined); checkoutSnapshotRef.current = ''
      setMode('view'); await load(); await loadStatus(); setSaved('Draft checked in.')
    } catch (reason) {
      setError(operationError(reason, 'Draft could not be saved.'))
    } finally { setBusy(false) }
  }

  const act = async (work: () => Promise<void>, failure: string, success: string) => {
    setBusy(true); setError(''); setSaved('')
    try { await work(); setSaved(success); await load() }
    catch (reason) { setError(operationError(reason, failure)) }
    finally { setBusy(false) }
  }

  const defer = () => {
    const reason = prompt(`Why is ${item?.displayNumber ?? 'this package'} being put away for another day?`)
    if (reason === null || !reason.trim()) return
    void act(() => apiRequest(`${api}/api/test-change-reviews/${packageId}/defer`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason: reason.trim() }),
    }), 'The package could not be deferred.', 'Put away for another day.')
  }

  const reinstate = () => void act(
    () => apiRequest(`${api}/api/test-change-reviews/${packageId}/reinstate`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}',
    }), 'The package could not be reinstated.', 'Back off the shelf.')

  const revise = () => void (async () => {
    setBusy(true); setError(''); setSaved('')
    try {
      const next = await apiRequest<{ id: string }>(`${api}/api/test-change-reviews/${packageId}/revise`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}',
      })
      onOpenTestChangeRequest(next.id)
    } catch (reason) { setError(operationError(reason, 'The next test change request revision could not be started.')) }
    finally { setBusy(false) }
  })()

  if (!item) return <main className="scrPage">
    {error ? <div className="workspaceError" role="alert">{error}</div> : <p className="workspaceLoading">Loading the test change request…</p>}
  </main>

  const facts = {
    state: item.state,
    deferredFromState: item.deferredFromState,
    targetRelease: releases.find(x => x.id === item.releaseId),
    superseded: item.state === 'Superseded',
  }
  const canAuthor = Boolean(item.capabilities?.canProposeArtifactChange
    || item.capabilities?.canWithdrawArtifactChange || item.capabilities?.canProposeProcedureChange
    || item.capabilities?.canWithdrawProcedureChange || item.capabilities?.canRevise)
  const isAuthor = !item.authorId || item.authorId.toLowerCase() === currentUser.toLowerCase() || currentUser.toLowerCase() === 'admin'
  const editable = item.state === 'Draft'
  const raisedFrom = item.coveredChangeRequests.find(x => x.originating)
  const caseComplete = Boolean(draft?.title.trim() && toPlainText(draft.problemRich).trim()
    && toPlainText(draft.analysisRich).trim() && toPlainText(draft.solutionRich).trim())
  const proposalsComplete = Boolean(draft?.procedureChanges.every(proposalCanCheckIn))
  const hasUnsavedChanges = Boolean(draft && serializedWorkingCopy !== lastSavedRef.current)

  const updateProcedure = (key: string, field: keyof ProcedureProposal, value: string | number) =>
    setDraft(current => current ? {
      ...current,
      procedureChanges: current.procedureChanges.map(proposal => proposal.key === key
        ? { ...proposal, [field]: value }
        : proposal),
    } : current)

  const actions = <>
    {editable && canAuthor && isAuthor && <button type="button" className="outline"
      disabled={busy || Boolean(lockStatus?.locked && !lockStatus.mine)} onClick={beginEdit}>
      {busy ? 'Checking lock…' : lockStatus?.locked && !lockStatus.mine
        ? `Read only · ${lockStatus.holder ?? 'another engineer'}`
        : 'Check out & edit'}
    </button>}
    {item.state === 'Approved' && item.capabilities?.canRevise && <button type="button" className="reviseAction" disabled={busy} onClick={revise}>
      {busy ? 'Creating revision…' : 'Revise'}
    </button>}
    {editable && isAuthor && <button type="button" className="deferAction" disabled={busy} onClick={defer}>
      {busy ? 'Deferring…' : 'Defer'}
    </button>}
    {item.state === 'Deferred' && isAuthor && <button type="button" className="reviseAction" disabled={busy} onClick={reinstate}>Reinstate</button>}
  </>

  return <ControlledChangePage
    backLabel={isVerificationProcedureKind(item.artifactKind) && discipline !== 'System'
      ? disciplineLabel(discipline) + ' Procedure Change Requests'
      : `${disciplineLabel(discipline)} Test Change Requests`}
    onBack={onBack}
    eyebrow={`TEST CHANGE CONTROL / ${item.displayNumber}`}
    title={item.title || 'Not written up yet'}
    description={`Revision-controlled change case, ${artifactWord} proposals, and review authority.`}
    allocation={changeRequestAllocation(facts)}
    state={changeRequestState(facts)}
    stateCode={item.state}
    version={item.version}
    docxHref={`${api}/api/test-change-reviews/${item.id}/download?format=docx`}
    pdfHref={`${api}/api/test-change-reviews/${item.id}/download?format=pdf`}
    error={error}
    saved={saved}
  >
    {mode === 'edit' && draft ? <ControlledChangeAuthoringForm
      onSubmit={checkIn}
      stages={[
        { href: '#checked-change-case', label: 'Change case', status: caseComplete ? 'Complete' : 'Required', complete: caseComplete, active: !caseComplete },
        { href: '#checked-procedures', label: `${artifactNoun} changes`, status: proposalsComplete ? 'Complete' : 'In progress', complete: proposalsComplete, active: caseComplete && !proposalsComplete },
      ]}
      actions={<ControlledChangeAuthoringActions
        summary={caseComplete && proposalsComplete ? 'Ready for review after check-in' : 'Draft can be checked in before review readiness'}
        detail={hasUnsavedChanges ? 'Working copy has unsaved changes' : `Working copy: ${autosaveStatus.toLowerCase()}`}
        busy={busy}
        saving={autosaveStatus === 'Saving'}
        // Save stays available while a working copy is checked out. Saving one that autosave has already
        // written is a no-op, so there was never an invariant behind greying it — only a convention, and the
        // reader asked for the reassurance of an explicit save.
        canSave={autosaveStatus !== 'Conflict'}
        // An unfinished proposal no longer blocks. A checkout is somewhere to put work down, and an engineer
        // interrupted halfway through a proposal should be able to hand the lock back with the half-written
        // work attached rather than choosing between holding it overnight and deleting what they started.
        //
        // The completeness this used to enforce now lives in `TestChangeReview.SubmitForReview`, which is
        // where it is a claim about readiness for another person. An approver still cannot be shown a
        // procedure with no steps; an author can simply stop mid-sentence.
        canCheckIn={autosaveStatus !== 'Conflict' && Boolean(draft.title.trim())}
        checkInBlockedReason={autosaveStatus === 'Conflict'
          ? 'Another edit reached this Draft first. Reload to see it before checking in.'
          : 'Give the Draft a title before checking it in.'}
        onDiscard={() => void discard()}
        onSave={() => void saveWorkingCopy()}
      />}
    >
      <section className="workspaceCard authoringCard" id="checked-change-case">
        <div className="workspaceTitle">
          <div><span className="stageKicker">STAGE 1</span><h2>Change case</h2><p>Keep the decision context concise, complete, and attributable.</p></div>
          <div className={`autosaveState ${autosaveStatus.toLowerCase()}`}><i />{autosaveStatus}{lock && <small>Lock expires {new Date(lock.expiresAt).toLocaleTimeString()}</small>}</div>
        </div>
        <div className="checkoutBanner"><b>Checked out by <PersonName userName={currentUser} /></b><span>Opened {lock && new Date(lock.openedAt).toLocaleString()} · other users remain read-only</span></div>
        <div className="editFields">
          <label>Title<input value={draft.title} onChange={event => setDraft(current => current ? { ...current, title: event.target.value } : current)} required /></label>
          <RichCaseField api={api} projectId={item.projectId} label="Problem" value={draft.problemRich}
            placeholder="What need, defect, or risk exists?" required={false}
            onChange={value => setDraft(current => current ? { ...current, problemRich: value, problem: toPlainText(value) } : current)} />
          <RichCaseField api={api} projectId={item.projectId} label="Analysis" value={draft.analysisRich}
            placeholder="What is affected and what alternatives were considered?" required={false}
            onChange={value => setDraft(current => current ? { ...current, analysisRich: value, analysis: toPlainText(value) } : current)} />
          <RichCaseField api={api} projectId={item.projectId} label="Solution" value={draft.solutionRich}
            placeholder="What controlled outcome is proposed?" required={false}
            onChange={value => setDraft(current => current ? { ...current, solutionRich: value, solution: toPlainText(value) } : current)} />
        </div>
      </section>

      <section className="workspaceCard authoringCard" id="checked-procedures">
        <div className="workspaceTitle">
          <div><span className="stageKicker">STAGE 2</span><h2>Controlled {artifactWord} authoring</h2><p>One shared editor for {artifactWord} content and classification.</p></div>
          <span className={proposalsComplete ? 'completionBadge complete' : 'completionBadge'}>
            {proposalsComplete ? 'Complete' : `${draft.procedureChanges.length} proposal${draft.procedureChanges.length === 1 ? '' : 's'}`}
          </span>
        </div>
        <div className="workspaceProposalActions">
          <span>Add proposal</span>
            <button type="button" onClick={() => setDraft(current => current ? { ...current, procedureChanges: [...current.procedureChanges, emptyProcedure(discipline, 'Introduce')] } : current)}>+ Introduce {disciplineLabel(discipline)} {artifactWord}</button>
          <button type="button" onClick={() => setDraft(current => current ? { ...current, procedureChanges: [...current.procedureChanges, emptyProcedure(discipline, 'Modify')] } : current)}>Modify existing</button>
          <button type="button" onClick={() => setDraft(current => current ? { ...current, procedureChanges: [...current.procedureChanges, emptyProcedure(discipline, 'Retire')] } : current)}>Retire existing</button>
        </div>
        {draft.procedureChanges.map((proposal, index) => <ControlledProcedureEditor
          key={proposal.key}
          api={api}
          projectId={item.projectId}
          releaseId={item.releaseId}
          scope={discipline}
          artifactKind={item.artifactKind}
          levelLabel={disciplineLabel(discipline)}
          item={proposal}
          index={index}
          onChange={(field, value) => updateProcedure(proposal.key, field, value)}
          onRemove={() => setDraft(current => current ? { ...current, procedureChanges: current.procedureChanges.filter(value => value.key !== proposal.key) } : current)}
        />)}
        {!draft.procedureChanges.length && <div className="workspaceEmptyState"><b>No {artifactNoun.toLowerCase()} proposals</b><p>Add the smallest controlled set needed to deliver this verification change.</p></div>}
      </section>
    </ControlledChangeAuthoringForm> : <ControlledChangeReadLayout>
      <div className="workspaceStack">
        <ControlledChangeCaseCard
          actions={actions}
          note={item.state === 'Deferred' && item.deferralReason ? <p className="snapshotNote">Put away because: {item.deferralReason}</p> : undefined}
          fields={[
            { key: 'P', label: 'Problem', value: <RichContentView api={api} value={item.problemRich || fromPlainText(item.problem)} empty="Not written up yet" /> },
            { key: 'A', label: 'Analysis', value: <RichContentView api={api} value={item.analysisRich || fromPlainText(item.analysis)} empty="Not written up yet" /> },
            { key: 'S', label: 'Solution', value: <RichContentView api={api} value={item.solutionRich || fromPlainText(item.solution)} empty="Not written up yet" /> },
          ]}
        />

        <section className="workspaceCard">
          <div className="workspaceTitle"><div><h2>Raised from</h2><p>What concluded that this test work was required</p></div></div>
          {item.originKind
            ? <p className="sourceRecord"><b>{item.originDisplayLabel || verificationOriginLabel(item.originKind)}</b>{' '}
                <strong>{item.originDisplayIdentity || item.sourceChangeRequestNumber || item.originReferenceId}</strong>{' '}
                {item.originDisplayTitle || raisedFrom?.title || ''}</p>
            : raisedFrom
              ? <p className="sourceRecord"><b>{raisedFrom.number}</b> {raisedFrom.title}</p>
              : <p className="sourceRecord"><b>{item.sourceChangeRequestNumber || 'Problem Report'}</b></p>}
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle"><div><h2>Supporting files</h2><p>Evidence an approver needs alongside the change case</p></div></div>
          <ControlledAttachments api={api} projectId={item.projectId} artifactType="TestChangeRequest" artifactId={item.id} canAttach={editable && canAuthor} />
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle"><div><h2>{artifactNoun} impact</h2><p>{(item.artifactChanges ?? item.procedureChanges ?? []).length} proposed controlled change{(item.artifactChanges ?? item.procedureChanges ?? []).length === 1 ? '' : 's'}</p></div></div>
          {!(item.artifactChanges ?? item.procedureChanges ?? []).length && <p className="workspaceEmpty">No {artifactNoun.toLowerCase()} changes are proposed yet.</p>}
          {(item.artifactChanges ?? item.procedureChanges ?? []).map(change => <article className="requirementView" key={change.id} data-procedure-change={change.displayNumber}>
            <div><b>{change.displayNumber}</b><span>{changeKindLabel(change.kind)}</span></div>
            <p>{change.objective || change.title}</p>
            {change.rationale && <small>{change.rationale}</small>}
          </article>)}
        </section>

        <section className="workspaceCard">
          <div className="workspaceTitle"><div><h2>Audit history</h2></div></div>
          <p className="workspaceEmpty">Controlled checkout, check-in, review, and approval evidence is retained with this record.</p>
        </section>

        <section className="workspaceCard" data-signature-evidence>
          <div className="workspaceTitle"><div><h2>Signature evidence</h2><p>Original signatures remain immutable; migration status is shown from append-only provenance.</p></div></div>
          {signatureError
            ? <p className="workspaceEmpty">{signatureError}</p>
            : signatures.length
            ? <div className="workspaceStack">{signatures.map(signature => <article className="requirementView" key={signature.id}>
              <div><b>{signature.isSuperseded ? 'Superseded signature' : signature.action}</b><span>{signature.displayName}</span></div>
              <p>{signature.isSuperseded ? signature.supersession?.reason ?? 'Identity migration superseded this signature.' : signature.meaning}</p>
              {signature.isSuperseded && signature.supersession?.migration && <small>Migration: {signature.supersession.migration}{signature.supersession.newArtifactIdentity ? ` · replacement identity ${signature.supersession.newArtifactIdentity}` : ''}{signature.supersession.newContentHash ? ` · replacement hash ${signature.supersession.newContentHash.slice(0, 12)}…` : ''}</small>}
              <code>{signature.contentHash}</code>
            </article>)}</div>
            : <p className="workspaceEmpty">No signatures are recorded for this test change request.</p>}
        </section>
      </div>

      <aside className="reviewRail">
        <ControlledStatusCard
          displayNumber={item.displayNumber}
          fields={[
            { label: 'Allocation', value: changeRequestAllocation(facts), data: { name: 'allocation', value: item.state === 'Deferred' ? 'Deferred' : 'Build' } },
            { label: 'State', value: changeRequestState(facts), data: { name: 'state', value: item.state } },
            { label: 'Author', value: item.authorId ? <PersonName userName={item.authorId} withRole /> : 'Raised by assessment' },
            { label: 'Revision', value: item.revision },
            { label: 'Updated', value: item.updatedAt ? new Date(item.updatedAt).toLocaleDateString() : '—' },
          ]}
        >
          {editable && isAuthor && !(item.artifactChanges ?? item.procedureChanges ?? []).length && <div className="railReadiness"><b>Draft needs authoring</b><span>Complete the {artifactNoun.toLowerCase()} proposals.</span><button type="button" disabled={busy} onClick={beginEdit}>Complete Draft readiness</button></div>}
        </ControlledStatusCard>
        <ReviewCycleCard cycle={item.reviewCycle} />
      </aside>
    </ControlledChangeReadLayout>}
  </ControlledChangePage>
}
