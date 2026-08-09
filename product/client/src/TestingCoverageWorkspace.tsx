import { useCallback, useEffect, useRef, useState } from 'react'
import { PersonName } from './People'
import PersonPicker from './PersonPicker'
import ProblemReportPicker, { type ProblemReportOption } from './ProblemReportPicker'
import TestChangeRequestWorkspace from './TestChangeRequestWorkspace'
import TestChangeRequestCreateDialog from './TestChangeRequestCreateDialog'
import type { AuthUser } from './IdentityCenter'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import type { TestDiscipline } from './TestResultsWorkspace'
import './DownstreamAssessmentQueue.css'
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
type ChangeRequestCover = { id: string; number: string; title: string; originating: boolean }
type TestChangeRequest = {
  id: string
  displayNumber: string
  title?: string
  problem?: string
  analysis?: string
  solution?: string
  caseContractVersion: number
  procedureDecisionCount: number
  discipline: string
  state: string
  version: number
  assignedEngineerId?: string
  selectedApproverId?: string
  outcome: 'Pending' | 'ChangeRequired' | 'NoChangeRequired'
  noChangeRationale?: string
  decidedBy?: string
  totalItems: number
  resolvedItems: number
  coveredChangeRequests: ChangeRequestCover[]
  problemReports?: ProblemReportOption[]
  capabilities: { canAssign: boolean; canDecide: boolean; canSubmit: boolean; canApprove: boolean; canReturn: boolean }
  reviewCycle?: {
    id: string
    sequence: number
    mode: string
    state: string
    workflowId?: string
    workflowLogicalId?: string
    workflowName?: string
    workflowVersion?: number
    steps: { position: number; stageName: string; authority: string; approverId: string; approverName: string; state: string; decidedAt?: string }[]
  }
}
type PickerRequirement = { revisionId: string; displayNumber: string; statement: string }
type PickerProcedure = { id: string; displayNumber: string; title: string; state: string }
type RequirementPickerPage = { page: number; pageSize: number; totalCount: number; totalPages: number; items: PickerRequirement[] }
type ProcedurePickerPage = { page: number; pageSize: number; totalCount: number; totalPages: number; items: PickerProcedure[] }
type ImpactItem = {
  id: string
  testChangeReviewId: string
  trigger: string
  state: string
  subjectDisplayNumber: string
  subjectStatement: string
  declaredVerificationMethod: string
  requirementRevisionId?: string
  assignedEngineerId?: string
  outcome?: string
  resolutionRationale: string
  resolvedProcedure?: { id:string; revisionId:string; displayNumber:string; title:string; state:string }
  holdsRelease?: boolean
  decisionHistory: { id: string; action: string; outcome?: string; rationale: string; actor: string; occurredAt: string }[]
}

const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'Software HLR' : 'Software LLR'

/// What the assessment is called, and what a test change request raised from it is called.
const assessmentName = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System Test' : discipline === 'HighLevelSoftware' ? 'HLR Test' : 'LLR Test'
const tcrAcronym = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'SYSTCR' : discipline === 'HighLevelSoftware' ? 'HLRTCR' : 'LLRTCR'
const missingCaseFields = (request: TestChangeRequest) => [
  ['Title', request.title], ['Problem', request.problem],
  ['Analysis', request.analysis], ['Solution', request.solution],
].filter(([, value]) => !value?.trim()).map(([name]) => name)
const tcrNewLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

/**
 * Whether the test assessment has been done, and what it concluded.
 *
 * Word for word the answer the requirements disciplines give, because it is the same question asked of
 * verification: an approved change either needs work here or it does not. The two pages showed the same
 * stage of the same workflow in two unrelated vocabularies, so a reader could not carry what they had
 * learned from one to the other.
 */
const testAssessmentStatus = (request: TestChangeRequest, discipline: TestDiscipline) => {
  const name = assessmentName(discipline)
  const acronym = tcrAcronym(discipline)
  if (request.state === 'Superseded') return `${name} Assessment Superseded`
  if (request.outcome === 'Pending') return `${name} Assessment Required`
  if (request.outcome === 'NoChangeRequired')
    return request.state === 'Approved'
      ? `${name} Assessment Complete – No ${acronym} Required`
      : `${name} Assessment Complete – No ${acronym} Required Pending Approval`
  // The controlled number is what makes it a test change request, so having one is what distinguishes
  // "one is needed" from "one exists" — exactly as a linked HLRCR does on the requirements side.
  return request.displayNumber.startsWith(acronym)
    ? `${name} Assessment Complete – ${acronym} Created`
    : `${name} Assessment Complete – Draft ${acronym} Required`
}

/**
 * The coverage a requirement already has, shown beside the decision about it.
 *
 * Three answers, and they are not the same. A requirement with a confirmed procedure may only need that
 * procedure named. One whose only procedure was written against earlier wording is the case most likely to
 * be answered wrongly, because a reader who sees "covered" stops looking — so suspect coverage is called
 * out as suspect and the procedure that has to be reconfirmed or replaced is named. One with nothing is
 * where "a new procedure is required" is the honest answer.
 *
 * Until the target build materializes its requirements there is no revision to look coverage up against;
 * that is stated rather than rendered as "no procedure", which would read as a finding.
 */
function ExistingCoverage({ item, coverage }: { item: ImpactItem; coverage?: Coverage }) {
  if (!item.requirementRevisionId)
    return <span className="existingCoverage pending">Existing coverage is known once this build materializes its requirements.</span>
  const row = coverage?.items.find(x => x.revisionId === item.requirementRevisionId)
  if (!row || !row.coveredBy.length)
    return <span className="existingCoverage none">No approved procedure covers this requirement yet.</span>
  const suspect = row.coveredBy.filter(x => x.coverageState === 'Suspect')
  const confirmed = row.coveredBy.filter(x => x.coverageState === 'Confirmed')
  return <span className={`existingCoverage ${suspect.length ? 'suspect' : 'covered'}`}>
    {confirmed.length > 0 && <>Covered by {confirmed.map(x => `${x.displayNumber} (${x.state})`).join(', ')}. </>}
    {suspect.length > 0 && <>{suspect.map(x => x.displayNumber).join(', ')} {suspect.length === 1 ? 'was' : 'were'} written against earlier wording and {suspect.length === 1 ? 'does' : 'do'} not count as coverage until reconfirmed or replaced.</>}
  </span>
}

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
export default function TestingCoverageWorkspace({ api, projectId, releaseId, discipline, buildName, readOnly, programId, user, onOpenRequirementRevision }: {
  api: string
  projectId: string
  releaseId: string
  discipline: TestDiscipline
  buildName: string
  readOnly: boolean
  programId: string
  user: AuthUser
  onOpenRequirementRevision: (requirement: { id: string; revisionId: string; level: string }) => void
}) {
  // Authority is per Program, and it is the server that enforces it. Reflecting it here is about not offering
  // somebody a control that will refuse them — an approval they cannot give is worse than no button at all.
  const roles = user.programs.find(program => program.programId === programId)?.roles ?? []
  // The server accepts both roles everywhere this page acts; a TestLead-only user must see the same
  // controls a TestEngineer does, or the button would predictably refuse nothing and never appear.
  const canTest = !readOnly && (user.isAdministrator || roles.includes('TestEngineer') || roles.includes('TestLead'))
  // No procedure-level approval authority is read here. Approving a procedure is approving the test change
  // request that carries it, and that authority arrives per request in its own capabilities.
  const [coverage, setCoverage] = useState<Coverage>()
  const [requests, setRequests] = useState<TestChangeRequest[]>([])
  // Approved procedures only, and only so a decision can name the one that already covers a requirement.
  // The approved-procedure picker: bounded, server-searched and paged with totals, so a valid procedure
  // beyond the first 200 rows is findable and an exact selected procedure is hydrated by ID.
  const [procedureQuery, setProcedureQuery] = useState('')
  const [procedurePage, setProcedurePage] = useState(1)
  const [procedurePicker, setProcedurePicker] = useState<ProcedurePickerPage>()
  const [procedureChoice, setProcedureChoice] = useState('')
  const [effectiveBaseline, setEffectiveBaseline] = useState('')
  const [impact, setImpact] = useState<ImpactItem[]>([])
  const [opened, setOpened] = useState('')
  /** The test change request whose procedure decisions are open for authoring, if any. */
  const [authoring, setAuthoring] = useState('')
  const [resolving, setResolving] = useState<ImpactItem>()
  const [reopening, setReopening] = useState<ImpactItem>()
  const [outcome, setOutcome] = useState('ProcedureCoverageConfirmed')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState('')
  const [creating, setCreating] = useState(false)
  /** The deliberate TCR creation dialog, opened from the page header. */
  const [creatingTcr, setCreatingTcr] = useState(false)
  const [canCreate, setCanCreate] = useState(false)
  // The package a proposal belongs to. A procedure change has to be carried by one, so authoring is only
  // reachable from a decision that names it.
  const [authoringReviewId, setAuthoringReviewId] = useState("")
  const [authoringNumber, setAuthoringNumber] = useState("")
  const [createError, setCreateError] = useState('')
  const [reviewDecision, setReviewDecision] = useState<{ request: TestChangeRequest; action: 'approve' | 'return' }>()
  const [linkingProblemReports, setLinkingProblemReports] = useState<TestChangeRequest>()
  const [problemReportIds, setProblemReportIds] = useState<string[]>([])
  const [submitting, setSubmitting] = useState<TestChangeRequest>()
  const [reviewWorkflow, setReviewWorkflow] = useState<{
    required: boolean
    name?: string
    version?: number
    mode?: string
    stages: { position: number; name: string; requiredRole: string; candidates: { userId: string; name: string; role: string }[] }[]
  }>()
  const [stageApprovers, setStageApprovers] = useState<Record<number, string>>({})
  const [workflowError, setWorkflowError] = useState<string | null>(null)
  const [workflowReload, setWorkflowReload] = useState(0)
  /// The assessment being concluded as needing no test work, which has to say why.
  const [decliningTest, setDecliningTest] = useState<TestChangeRequest>()
  const [declineRationale, setDeclineRationale] = useState('')
  const [reviewApprover, setReviewApprover] = useState({ userId: '', name: '' })
  const [revision, setRevision] = useState(0)
  // No procedure search, filter or page in the address any more. Those described a library this page no
  // longer has; the Explorer keeps its own. A stale link carrying them is simply ignored rather than
  // The requirement picker for a proposed procedure: bounded, server-searched and paged with totals, so a
  // requirement beyond the first 200 rows is findable, and the exact requirement a decision arrived from is
  // hydrated by ID even when it is outside the current page.
  const [requirementQuery, setRequirementQuery] = useState('')
  const [requirementPage, setRequirementPage] = useState(1)
  const [requirementPicker, setRequirementPicker] = useState<RequirementPickerPage>()
  const [requirementSelection, setRequirementSelection] = useState<string[]>([])

  // One ticket per loader, not one for the page.
  //
  // Only the newest reply of a given loader may write the screen — but the two loaders here are independent,
  // and sharing a counter made each of them cancel the other. The procedure search runs on mount behind a
  // debounce, bumps the count, and the coverage load that was already in flight then discards everything it
  // read: no packages, no coverage, no impact, and no error, because nothing failed. It presented as the
  // software HLR queue being empty while the API demonstrably returned a package for that build.
  const loadTicket = useRef(0)
  const procedureTicket = useRef(0)
  const requirementTicket = useRef(0)
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
    setEffectiveBaseline(effective ? String(effective) : '')
    const builds = await fetch(`${api}/api/builds?projectId=${projectId}`)
    const build = builds.ok ? (await builds.json()).find((x: { releaseId: string }) => x.releaseId === releaseId) : undefined
    const configuration = build ? `buildId=${build.id}` : effective ? `baselineId=${effective}` : ''

    const [coverageResponse, requestResponse, impactResponse] = await Promise.all([
      configuration ? fetch(`${api}/api/verification-coverage?projectId=${projectId}&${configuration}`) : undefined,
      fetch(`${api}/api/releases/${releaseId}/test-change-reviews`),
      fetch(`${api}/api/releases/${releaseId}/verification-impact`),
    ])
    const rawCoverage = coverageResponse?.ok ? await coverageResponse.json() as Coverage : undefined
    const nextRequests = requestResponse.ok
      ? await requestResponse.json() as { canCreate?: boolean; items?: TestChangeRequest[] }
      : undefined
    const nextImpact = impactResponse.ok ? await impactResponse.json() : undefined
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
    if (nextRequests) {
      setRequests(nextRequests.items ?? [])
      setCanCreate(Boolean(nextRequests.canCreate))
    }
    if (nextImpact) setImpact(nextImpact)
    if (!requestResponse.ok) {
      recordClientOperationFailure('verification.coverage.load', new Error(`HTTP ${requestResponse.status}`))
      setError('The test change requests for this build could not be loaded.')
    }
    if (coverageResponse && !coverageResponse.ok) {
      recordClientOperationFailure('verification.coverage.load', new Error(`HTTP ${coverageResponse.status}`))
      setError('The requirement coverage for this build could not be read.')
    }
  }, [api, projectId, releaseId, discipline])

  useEffect(() => { void load() }, [load])

  // The approved procedures a decision may name as already covering a requirement: bounded, server-searched
  // and paged with totals, so a valid procedure beyond the first 200 rows is findable. The exact procedure a
  // resolved decision names is hydrated by ID even when it lies beyond the current page.
  useEffect(() => {
    const mine = ++procedureTicket.current
    const params = new URLSearchParams({ projectId, releaseId, scope, state: 'Approved',
      page: String(procedurePage), pageSize: '50', search: procedureQuery })
    if (resolving?.resolvedProcedure?.id) params.set('ids', resolving.resolvedProcedure.id)
    void (async () => {
      const response = await fetch(`${api}/api/test-procedures?${params}`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== procedureTicket.current) return
      setProcedurePicker(paged)
    })()
  }, [api, projectId, releaseId, scope, revision, procedureQuery, procedurePage, resolving?.resolvedProcedure?.id])

  // The requirements a new procedure can be written against — read from the effective baseline rather than
  // from the coverage list, bounded, server-searched and paged with totals. The exact requirement a decision
  // arrived from is hydrated by ID even when it lies beyond the first page.
  useEffect(() => {
    if (!creating) return
    const mine = ++requirementTicket.current
    const timer = setTimeout(async () => {
      const params = new URLSearchParams({ projectId, scope, includeRetired: 'false',
        page: String(requirementPage), pageSize: '50', search: requirementQuery })
      if (effectiveBaseline) params.set('baselineId', effectiveBaseline)
      const selected = [...new Set(requirementSelection)]
      if (selected.length) params.set('ids', selected.join(','))
      const response = await fetch(`${api}/api/requirements?${params}`)
      if (!response.ok) return
      const paged = await response.json()
      if (mine !== requirementTicket.current) return
      setRequirementPicker(paged)
    }, 180)
    return () => clearTimeout(timer)
  }, [api, projectId, scope, creating, effectiveBaseline, requirementQuery, requirementPage, requirementSelection])

  const mine = requests.filter(x => x.discipline === discipline)

  const act = async (work: () => Promise<void>, failure: string) => {
    if (busy) return
    setBusy(true); setError(''); setSaved('')
    // Both lists are re-read, not just coverage. Proposing procedure work changes the package and the
    // inventory it will produce, and refreshing only the coverage side left the other list stale until the
    // reader happened to type in the search box.
    try { await work(); await load(); setRevision(current => current + 1) }
    catch (problem) { recordClientOperationFailure('verification.coverage.change', problem); setError(operationError(problem, failure)) }
    finally { setBusy(false) }
  }

  // Taking a package on assigns every decision in it. A package half-assigned has no owner anybody can name,
  // which is the state this queue exists to make impossible.
  /**
   * Answering the assessment, which is what may raise the test change request.
   *
   * Concluding that work is required allocates the controlled number; concluding that none is required
   * produces nothing and therefore goes for approval. Same shape as the requirements side, same reason.
   */
  const conclude = (request: TestChangeRequest, testChangeRequired: boolean, rationale?: string) => act(async () => {
    const result = await apiRequest<{ displayNumber: string }>(`${api}/api/test-change-reviews/${request.id}/conclusion`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ testChangeRequired, rationale: rationale ?? '', expectedVersion: request.version }),
    })
    setSaved(testChangeRequired
      ? `${result.displayNumber} raised for ${request.coveredChangeRequests.map(x => x.number).join(', ')}.`
      : `No ${tcrAcronym(discipline)} required for ${request.coveredChangeRequests.map(x => x.number).join(', ')}.`)
  }, 'The test assessment could not be recorded.')

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

  const advance = (request: TestChangeRequest, action: 'submit' | 'approve' | 'return', rationale?: string, approverId?: string, approvers?: { userId: string }[], password?: string, meaning?: string) => act(async () => {
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/${action}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(action === 'submit'
        ? { approverId, approvers, expectedVersion: request.version }
        : action === 'approve'
          ? { rationale: rationale ?? '', password: password ?? '', meaning: meaning ?? '' }
          : { rationale: rationale ?? '' }),
    })
    setSaved(action === 'submit' ? `${request.displayNumber} sent for approval.`
      : action === 'approve' ? `${request.displayNumber} approved.`
      : `${request.displayNumber} returned for more work.`)
    if (action === 'submit') { setSubmitting(undefined); setReviewApprover({ userId: '', name: '' }) }
  }, 'The package could not be moved on.')

  const workflowSubject = discipline === 'System' ? 'SystemTest'
    : discipline === 'HighLevelSoftware' ? 'HighLevelSoftwareTest' : 'LowLevelSoftwareTest'
  useEffect(() => {
    if (!submitting) return
    let active = true
    setReviewWorkflow(undefined)
    setStageApprovers({})
    setWorkflowError(null)
    fetch(`${api}/api/review-workflows/applicable?projectId=${projectId}&type=${workflowSubject}`)
      .then(async response => {
        if (!response.ok) throw new Error('The recorded review procedure could not be loaded.')
        return response.json()
      })
      .then((value: {
        required?: boolean
        name?: string
        version?: number
        mode?: string
        stages?: { position: number; name: string; requiredRole: string; candidates: { userId: string; name: string; role: string }[] }[]
      }) => {
        if (!active) return
        setReviewWorkflow({
          required: Boolean(value.required),
          name: value.name,
          version: value.version,
          mode: value.mode,
          stages: value.stages ?? [],
        })
      })
      .catch(() => {
        if (active) setWorkflowError('The recorded review procedure could not be loaded. Retry before submitting this package.')
      })
    return () => { active = false }
  }, [api, projectId, submitting, workflowSubject, workflowReload])

  const linkReports = (request: TestChangeRequest) => act(async () => {
    await apiRequest(`${api}/api/test-change-reviews/${request.id}/problem-reports`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ problemReportIds, expectedVersion: request.version }),
    })
    setLinkingProblemReports(undefined)
    setSaved(`PR links updated for ${request.displayNumber}.`)
  }, 'The PR links could not be updated.')

  /**
   * Proposes introducing a procedure, on the package that asked for it.
   *
   * A procedure is not created here, or anywhere else a person can press. It is introduced, modified or
   * retired by a test change request carrying the proposal through review and materialisation into the
   * build — exactly as a requirement is only changed by a change request. The previous control wrote a
   * procedure straight into the library with no package behind it and no record of why it existed.
   */
  const proposeProcedure = async (form: FormData) => {
    if (busy) return
    // The selection is state, not the form: options on other pages of the searchable picker are not in the
    // DOM, and a selection made on one page must survive paging to another and back.
    const requirementRevisionIds = requirementSelection
    if (!requirementRevisionIds.length) { setCreateError('A procedure has to say which requirements it verifies.'); return }
    if (!authoringReviewId) { setCreateError('This proposal has no test change request to belong to.'); return }
    setBusy(true); setCreateError(''); setError(''); setSaved('')
    try {
      await apiRequest(`${api}/api/test-change-reviews/${authoringReviewId}/procedure-changes`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          kind: 'Introduce',
          // Omitted when introducing: the number is allocated by the server so two engineers cannot pick the
          // same one, and it is not a controlled procedure until the package that proposes it is approved.
          baseNumber: null,
          revision: 0,
          title: form.get('title'),
          objective: form.get('objective'),
          preconditions: form.get('preconditions'),
          steps: form.get('steps'),
          expectedResult: form.get('expectedResult'),
          rationale: form.get('rationale'),
          drivingRequirementRevisionIds: requirementRevisionIds,
        }),
      })
      setCreating(false)
      setSaved(`Proposed on ${authoringNumber || 'the test change request'}. It becomes controlled when that package is approved.`)
      await load()
      setRevision(current => current + 1)
    } catch (problem) {
      recordClientOperationFailure('verification.procedure.propose', problem)
      setCreateError(operationError(problem, 'The procedure change could not be proposed.'))
    } finally { setBusy(false) }
  }

  // No openProcedure, no history dialog, no controlled editor. Reading a procedure, its revisions and what
  // drove each of them is the Test Procedure Explorer, and a second reader here reachable only by a stale
  // link would be a second answer to "where do I read a procedure".
  return (
    <main className="testingCoveragePage">
      <header>
        <div>
          <p className="eyebrow">VERIFICATION / {disciplineLabel(discipline).toUpperCase()}</p>
          <h1>Change Requests</h1>
          <p>The {tcrAcronym(discipline)}s controlling {buildName}'s test procedures, and the approved changes waiting for one.</p>
        </div>
        {canCreate && (
          <button type="button" className="newTcrAction" onClick={() => setCreatingTcr(true)}>
            + New {tcrNewLabel(discipline)} Test Change Request
          </button>
        )}
      </header>
      {error && <div className="workspaceError" role="alert" aria-live="assertive">{error}</div>}
      {saved && <div className="workspaceSaved" role="status">{saved}</div>}

      {/* Coverage is not summarised here any more. This page is about the change requests controlling test
          work, exactly as the requirements-side Change Requests page is about the ones controlling
          requirements; "is this build covered" is a question about procedures as they stand, and it is
          answered in the Test Procedure Explorer beside the procedures it is about. */}

      {/* A build with nothing materialized has no exact revisions to bind a procedure to. Said plainly, and
          said here, because this is the page a decision asks for a procedure from — the alternative is an
          authoring form whose requirement list is empty for no stated reason, which reads as a broken page
          rather than as work that has not happened yet. */}
      {!effectiveBaseline && (
        <section className="materializationPrerequisite" role="status">
          <div>
            <b>Procedure authoring waits for governed requirement materialization</b>
            <p>
              This build has no immutable requirement revisions yet, so a new procedure cannot be bound to an
              exact target. Existing inherited procedures remain visible against their predecessor revisions;
              planned work for new or modified requirements stays in the test change requests below and
              cannot count as confirmed coverage yet.
            </p>
          </div>
          <div>
            <span>Current limitation</span>
            <b>Requirement materialization is not exposed in this workspace.</b>
          </div>
        </section>
      )}

      {/* The queue, before the inventory. Somebody arriving to do verification work needs to know what this
          build's changes have made their problem — a wall of green coverage says nothing about that. */}
      <section className="coverageCard">
        <div className="cardTitle">
          {/* Named for the question, not for the artefact one answer to it produces. Approved changes arrive
              here to be assessed; a test change request is what an assessment raises when it finds work. */}
          <h2>Downstream test assessments</h2>
          <p>Approved upstream changes waiting for an explicit {assessmentName(discipline)} conclusion.</p>
        </div>
        {!mine.length && (
          <div className="coverageEmpty">
            <b>No {disciplineLabel(discipline)} test assessments for this build</b>
            <span>Nothing has been approved into this build that {disciplineLabel(discipline)} verification must answer for.</span>
          </div>
        )}
        {/* The requirements queue's row, from its own stylesheet. Identifying text on the left, what the
            assessment concluded in the middle, and one control on the right in every state — what can be done
            about an assessment is decided inside it, not chosen from a row of peers. */}
        {mine.map(request => (
          <article className="downstreamAssessment" data-state={request.state} key={request.id}>
            <div className="downstreamSource">
              <b>{request.coveredChangeRequests.map(x => x.number).join(', ')}</b>
              <span>{request.coveredChangeRequests[0]?.title ?? ''}</span>
              <i>{assessmentName(discipline)} assessment</i>
            </div>
            <div className="downstreamConclusion">
              <strong>{testAssessmentStatus(request, discipline)}</strong>
              {request.outcome === 'ChangeRequired' && request.displayNumber.startsWith(tcrAcronym(discipline)) && (
                <button type="button" className="linkedScr" onClick={() => setAuthoring(request.id)}>
                  {request.displayNumber} · {request.state === 'InReview' ? 'In review' : request.state}
                </button>
              )}
              {request.outcome === 'NoChangeRequired' && request.noChangeRationale && <p>{request.noChangeRationale}</p>}
            </div>
            <div className="downstreamActions">
              <button type="button" className="openAssessment" onClick={() => setOpened(request.id)}>Open assessment</button>
            </div>
          </article>
        ))}
        <p className="downstreamHelp">
          One {tcrAcronym(discipline)} may answer several assessments, and an assessment records one decision
          for every requirement the change touched.
        </p>
      </section>

      {/* What Open assessment opens. Everything that used to sit on the row lives here, which is where the
          requirements queue has always put it. */}
      {opened && (() => {
        const request = mine.find(x => x.id === opened)
        if (!request) return null
        return <div className="downstreamDrawerBackdrop" role="presentation">
          <aside className="downstreamDrawer" role="dialog" aria-modal="true" aria-labelledby="test-assessment-title">
            <header>
              <div>
                <p className="eyebrow">{assessmentName(discipline).toUpperCase()} ENGINEERING DECISION</p>
                <h2 id="test-assessment-title">{request.coveredChangeRequests.map(x => x.number).join(', ')} test impact</h2>
                <strong>{testAssessmentStatus(request, discipline)}</strong>
              </div>
              <button type="button" className="quiet" onClick={() => setOpened('')} aria-label="Close test assessment">Close</button>
            </header>

            <section className="downstreamDecisionWorkbench">
              <h3>Engineering conclusion</h3>
              {request.outcome !== 'Pending'
                ? <div className="recordedConclusion" data-outcome={request.outcome}>
                    <b>Recorded conclusion</b>
                    <p>{request.outcome === 'NoChangeRequired'
                      ? `No ${tcrAcronym(discipline)} is required.`
                      : `${tcrAcronym(discipline)} work is required, and is controlled by the linked package.`}</p>
                    {request.decidedBy && <span className="conclusionAuthor">Recorded by <PersonName userName={request.decidedBy} /></span>}
                    {request.noChangeRationale && <p className="conclusionRationale">{request.noChangeRationale}</p>}
                  </div>
                : <p className="drawerEmpty">Nobody has answered this yet.</p>}
              {request.outcome === 'ChangeRequired' && request.displayNumber.startsWith(tcrAcronym(discipline)) && (
                <ul className="drawerChanges">
                  <li className="linkedDraft">
                    <button type="button" className="drawerArtifactLink" onClick={() => setAuthoring(request.id)}>{request.displayNumber}</button>
                    <b>{request.state === 'InReview' ? 'In review' : request.state} · {request.resolvedItems} of {request.totalItems} decisions recorded</b>
                    <span>Opens in its own workspace, as a change request does from the requirements drawer.</span>
                  </li>
                </ul>
              )}
              <div className="drawerDecisionActions">
                {/* No claim step. Answering an unheld package is what takes it on. */}
                {request.outcome === 'Pending' && request.capabilities.canDecide && (
                  <>
                    <button type="button" disabled={busy} onClick={() => void conclude(request, true)}>{tcrAcronym(discipline)} required</button>
                    <button type="button" className="quiet" disabled={busy} onClick={() => setDecliningTest(request)}>No {tcrAcronym(discipline)} required</button>
                  </>
                )}
                {canTest && request.state === 'Open' && (
                  <button type="button" className="quiet" disabled={busy} onClick={() => {
                    setProblemReportIds((request.problemReports ?? []).map(report => report.id))
                    setLinkingProblemReports(request)
                  }}>Link Problem Reports{request.problemReports?.length ? ` · ${request.problemReports.length}` : ''}</button>
                )}
                {/* Submission is offered only once every decision is recorded. The server refuses otherwise, and
                    offering an action that will be refused is a worse answer than not offering it. */}
                {request.capabilities.canSubmit && request.totalItems > 0 && request.resolvedItems === request.totalItems && (
                  missingCaseFields(request).length
                    ? <button type="button" disabled={busy} onClick={() => setAuthoring(request.id)}>Complete engineering case</button>
                    : request.outcome === 'ChangeRequired' && request.procedureDecisionCount === 0
                    ? <button type="button" disabled={busy} onClick={() => setAuthoring(request.id)}>Add a procedure decision</button>
                    : <button type="button" disabled={busy} onClick={() => setSubmitting(request)}>Send for approval</button>
                )}
                {request.capabilities.canApprove && (
                  <>
                    <button type="button" disabled={busy} onClick={() => setReviewDecision({ request, action: 'approve' })}>Approve</button>
                    <button type="button" className="quiet" disabled={busy} onClick={() => setReviewDecision({ request, action: 'return' })}>Return</button>
                  </>
                )}
                {!canTest && <p className="drawerEmpty">Test engineering authority is required to act on this assessment.</p>}
              </div>
            </section>

            <section>
              {/* No requirements-side counterpart: a requirement change is read there, but a test change has to
                  be answered requirement by requirement. They live here rather than in the package because they
                  exist even when the conclusion is that no package is needed. */}
              <h3>What must be tested</h3>
              <div className="testAssessmentDecisions">
              <ul className="decisionList">
                {impact.filter(x => x.testChangeReviewId === request.id).map(item => (
                  <li key={item.id}>
                    <b>{item.subjectDisplayNumber}</b>
                    <i>{item.state === 'Resolved' ? (item.outcome ?? 'Resolved') : item.state}</i>
                    {item.subjectStatement&&<p>{item.subjectStatement}</p>}
                    {/* What already tests this requirement, stated before the decision rather than after it.
                        A verification engineer deciding whether a procedure must be written is answering a
                        question about the library as it stands, and asking them to hold that in their head —
                        or to leave and look it up — is how "a procedure probably exists" becomes a decision. */}
                    {item.trigger !== 'ProcedureOrphaned' && <ExistingCoverage item={item} coverage={coverage} />}
                    <small>
                      Author declared {item.declaredVerificationMethod || 'no method'}
                      {item.assignedEngineerId ? <> · <PersonName userName={item.assignedEngineerId} /></> : ''}
                      {item.resolutionRationale ? ` · ${item.resolutionRationale}` : ''}
                    </small>
                    {item.resolvedProcedure&&<div className="resolvedProcedure"><b>{item.resolvedProcedure.displayNumber}</b><span>{item.resolvedProcedure.title} · {item.resolvedProcedure.state}</span></div>}
                    {request.capabilities.canDecide && item.state !== 'Resolved' && (
                      <button type="button" className="quiet" disabled={busy} onClick={() => {
                        setOutcome(item.trigger === 'ProcedureOrphaned' ? 'ProcedureRetired' : 'ProcedureCoverageConfirmed')
                        setProcedureQuery('')
                        setProcedurePage(1)
                        setProcedureChoice(item.resolvedProcedure?.id ?? '')
                        setResolving(item)
                      }}>Decide</button>
                    )}
                    {/* A decision can be wrong, and a decision nobody can revisit is a decision people work
                        around. Reopening keeps what was decided in immutable history, returns the item to the
                        release gate, and puts any coverage it claimed back to suspect. */}
                    {/* Authoring the procedure the decision asked for, from the decision itself. This is the
                        only way in: the library used to offer a control that wrote a procedure with no memory
                        of why it existed, and it is gone. Starting here keeps the chain — change request,
                        requirement, decision, proposal — and the decision settles itself when the package is
                        approved and its procedure is materialised into the build. */}
                    {canTest && item.outcome === 'NewProcedureRequired' && (item.requirementRevisionId
                      ? (
                        <button type="button" disabled={busy} onClick={() => {
                          setAuthoringReviewId(item.testChangeReviewId)
                          setAuthoringNumber(request.displayNumber)
                          setRequirementQuery('')
                          setRequirementPage(1)
                          setRequirementSelection(item.requirementRevisionId ? [item.requirementRevisionId] : [])
                          setCreateError('')
                          setCreating(true)
                        }}>Author the procedure</button>
                      )
                      // A procedure binds to an exact approved revision, and a build that has not materialized
                      // its requirements has none — the decision is still worth recording, so the reason the
                      // work cannot start yet is stated rather than the action silently missing.
                      : <span className="procedureHold">The procedure can be written once this build materializes its requirements.</span>
                    )}
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
                {!impact.some(x => x.testChangeReviewId === request.id) && <li className="decisionNone">Nothing this change touched needs a decision.</li>}
              </ul>
              </div>
            </section>

            <section>
              <h3>Source change requests</h3>
              <ul className="drawerChanges">
                {request.coveredChangeRequests.map(change => (
                  <li key={change.id}><b>{change.number}</b><span>{change.title}</span></li>
                ))}
              </ul>
              <dl className="sourceCase">
                <div><dt>Responsibility</dt><dd>{request.assignedEngineerId
                  ? <><PersonName userName={request.assignedEngineerId} /> owns this assessment</>
                  : 'Unassigned'}</dd></div>
                {request.reviewCycle ? (
                  <div>
                    <dt>Review cycle</dt>
                    <dd>
                      {request.reviewCycle.workflowName
                        ? <>{request.reviewCycle.workflowName} v{request.reviewCycle.workflowVersion} · </> : null}
                      Cycle {request.reviewCycle.sequence} · {request.reviewCycle.mode}
                      <ul className="reviewStageList">
                        {request.reviewCycle.steps.map(step => (
                          <li key={step.position} data-state={step.state.toLowerCase()}>
                            <b>{step.stageName || `Stage ${step.position + 1}`}</b>
                            <span><PersonName userName={step.approverId} /> · {step.authority}</span>
                            <i>{step.state === 'Active'
                              ? 'Actionable now'
                              : step.state === 'Approved'
                                ? `Approved${step.decidedAt ? ' · ' + new Date(step.decidedAt).toLocaleString() : ''}`
                                : step.state}</i>
                          </li>
                        ))}
                      </ul>
                    </dd>
                  </div>
                ) : request.selectedApproverId ? (
                  <div><dt>Independent approver</dt>
                    <dd><PersonName userName={request.selectedApproverId} /></dd></div>
                ) : null}
                <div><dt>Linked Problem Reports</dt><dd>{request.problemReports?.length
                  ? request.problemReports.map(report => `${report.displayNumber} · ${report.title}`).join('\n')
                  : 'None linked'}</dd></div>
              </dl>
            </section>
          </aside>
        </div>
      })()}

      {/* Requirement coverage, and the requirements needing attention, moved to the Test Procedure
          Explorer. They are a report about the procedures a build carries, so they belong beside the
          procedures rather than on the page about the change requests that produce them.

          The procedure library moved with them, and is not duplicated here. #369 built the Explorer as the
          place a procedure is browsed, read and discussed; a second list on this page would be a second
          answer to "where do I find a procedure", and the two would drift. */}

      {creating && (
        <div className="decisionModal" role="dialog" aria-label="Propose a test procedure">
          <form onSubmit={event => { event.preventDefault(); void proposeProcedure(new FormData(event.currentTarget)) }}>
            <p className="eyebrow">PROPOSED PROCEDURE CHANGE</p>
            <h2>Introduce a {disciplineLabel(discipline)} test procedure</h2>
            <p>
              This is proposed on {authoringNumber || 'this test change request'}, as a requirement change is
              proposed on a change request. It becomes a controlled procedure when that package is approved and
              carried into the build — nothing here writes one on its own.
            </p>
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
              <select name="requirement" aria-describedby="procedure-requirements-help" multiple size={6} required
                value={requirementSelection}
                onChange={event => setRequirementSelection(Array.from(event.target.selectedOptions, option => option.value))}>
                {(requirementPicker?.items ?? []).map(item => (
                  <option key={item.revisionId} value={item.revisionId}>{item.displayNumber} - {item.statement.slice(0, 70)}</option>
                ))}
              </select>
            </label>
            <input aria-label="Search requirements" className="pickerSearch" value={requirementQuery}
              onChange={event => { setRequirementQuery(event.target.value); setRequirementPage(1) }}
              placeholder="Search by number or wording..." />
            <div className="pickerMeta">
              <span>
                {requirementPicker?.totalCount ?? 0} requirement{(requirementPicker?.totalCount ?? 0) === 1 ? '' : 's'} in scope -
                showing {(requirementPicker?.items ?? []).length}. Search by number or wording to find any requirement in this build.
              </span>
              <span className="pickerPager">
                <button type="button" disabled={(requirementPicker?.page ?? 1) <= 1}
                  onClick={() => setRequirementPage(page => Math.max(1, page - 1))}>Previous</button>
                <button type="button" disabled={(requirementPicker?.page ?? 1) >= (requirementPicker?.totalPages ?? 1)}
                  onClick={() => setRequirementPage(page => page + 1)}>Next</button>
              </span>
            </div>
            <small id="procedure-requirements-help">Choose one or more. Selections are kept while you search or page.</small>
            {/* No approver picked here. The package carries this proposal to its own review, and choosing a
                second approver for the procedure alone would be a second approval of the same work. */}
            <label>Why it is needed<textarea name="rationale" required /></label>
            <div className="decisionActions">
              <button type="submit" disabled={busy}>{busy ? 'Proposing…' : 'Propose procedure'}</button>
              <button type="button" className="quiet" disabled={busy} onClick={() => { setCreating(false); setCreateError('') }}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {/* The test change request itself: the room where procedures are created, modified and retired. It is
          the same drawer the requirements queue uses, from the same stylesheet, because it is the same kind of
          work asked of a different discipline. */}
      {creatingTcr && (
        <TestChangeRequestCreateDialog
          api={api}
          projectId={projectId}
          releaseId={releaseId}
          discipline={discipline}
          onClose={() => setCreatingTcr(false)}
          onCreated={(id, displayNumber) => {
            setCreatingTcr(false)
            setSaved(`${displayNumber} raised.`)
            void load()
            // The package opens onto its workspace so the engineer can start its procedure decisions.
            setAuthoring(id)
          }}
        />
      )}

      {authoring && (
        <TestChangeRequestWorkspace
          api={api}
          projectId={projectId}
          reviewId={authoring}
          canAuthor={canTest}
          onClose={() => setAuthoring('')}
          onChanged={() => void load()}
          onOpenRequirementRevision={onOpenRequirementRevision}
        />
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
            if (reviewWorkflow?.required) {
              const approvers = reviewWorkflow.stages
                .map(stage => ({ userId: stageApprovers[stage.position] }))
                .filter(entry => Boolean(entry.userId))
              if (approvers.length === reviewWorkflow.stages.length) {
                void advance(submitting, 'submit', undefined, undefined, approvers)
              }
            } else if (reviewApprover.userId) {
              void advance(submitting, 'submit', undefined, reviewApprover.userId)
            }
          }}>
            <p className="eyebrow">INDEPENDENT REVIEW</p>
            <h2>Send {submitting.displayNumber} for approval</h2>
            {workflowError
              ? <div className="decisionModalError">
                  <div className="workspaceError" role="alert">{workflowError}</div>
                  <button type="button" className="quiet" onClick={() => setWorkflowReload(value => value + 1)}>Retry</button>
                </div>
              : reviewWorkflow === undefined
              ? <p>Loading the recorded review procedure…</p>
              : reviewWorkflow.required
                ? <>
                    <p>The recorded review procedure {reviewWorkflow.name} v{reviewWorkflow.version} ({reviewWorkflow.mode}) requires one approver for each stage.</p>
                    {reviewWorkflow.stages.map(stage => (
                      <label key={stage.position}>{stage.name} · {stage.requiredRole}
                        <select value={stageApprovers[stage.position] ?? ''}
                          onChange={event => setStageApprovers(current => ({ ...current, [stage.position]: event.target.value }))}>
                          <option value="">Choose the {stage.requiredRole} for this stage…</option>
                          {stage.candidates.map(candidate => (
                            <option key={candidate.userId} value={candidate.userId}>{candidate.name}</option>
                          ))}
                        </select>
                      </label>
                    ))}
                  </>
                : <>
                    <p>Select the person who will independently review this exact package of test-procedure decisions.</p>
                    <PersonPicker api={api} projectId={projectId} value={reviewApprover.userId} name={reviewApprover.name}
                      index={9102} label="Independent test change request approver" excludeUserNames={[submitting.assignedEngineerId??user.userName,user.userName]} onSelect={setReviewApprover} />
                  </>}
            <div className="decisionActions">
              <button type="submit" disabled={busy || (reviewWorkflow?.required
                ? reviewWorkflow.stages.some(stage => !stageApprovers[stage.position])
                : !reviewApprover.userId) || Boolean(workflowError)}>Send for approval</button>
              <button type="button" className="quiet" onClick={() => setSubmitting(undefined)}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {decliningTest && (
        <div className="decisionModal" role="dialog"
          aria-label={`Record no test change for ${decliningTest.coveredChangeRequests.map(x => x.number).join(', ')}`}>
          <form onSubmit={event => {
            event.preventDefault()
            if (declineRationale.trim()) {
              void conclude(decliningTest, false, declineRationale.trim())
              setDecliningTest(undefined); setDeclineRationale('')
            }
          }}>
            <p className="eyebrow">{assessmentName(discipline).toUpperCase()} ASSESSMENT</p>
            <h2>No {tcrAcronym(discipline)} required</h2>
            {/* This conclusion raises nothing, so nothing downstream will ever examine it. The reasoning is
                the only record of the judgement, which is why it cannot be skipped. */}
            <p>
              Recording this raises no test change request for
              {' '}{decliningTest.coveredChangeRequests.map(x => x.number).join(', ')}. It goes to a test lead
              for approval, because nothing else downstream will examine it.
            </p>
            <label>Why no test-procedure work is required
              <textarea value={declineRationale} onChange={event => setDeclineRationale(event.target.value)} rows={4} />
            </label>
            <div className="decisionActions">
              <button type="submit" disabled={busy || !declineRationale.trim()}>Record the conclusion</button>
              <button type="button" className="quiet" onClick={() => { setDecliningTest(undefined); setDeclineRationale('') }}>Cancel</button>
            </div>
          </form>
        </div>
      )}

      {reviewDecision && (
        <div className="decisionModal" role="dialog" aria-label={`${reviewDecision.action === 'approve' ? 'Approve' : 'Return'} ${reviewDecision.request.displayNumber}`}>
          <form onSubmit={event => {
            event.preventDefault()
            const form = new FormData(event.currentTarget)
            const rationale = String(form.get('rationale') ?? '').trim()
            if (!rationale) return
            const selected = reviewDecision
            const password = String(form.get('password') ?? '')
            const meaning = String(form.get('meaning') ?? '').trim()
            if (selected.action === 'approve' && (!password || !meaning)) return
            setReviewDecision(undefined)
            void advance(selected.request, selected.action, rationale, undefined, undefined, password, meaning)
          }}>
            <p className="eyebrow">INDEPENDENT REVIEW</p>
            <h2>{reviewDecision.action === 'approve' ? 'Approve' : 'Return'} {reviewDecision.request.displayNumber}</h2>
            <p>{reviewDecision.action === 'approve'
              ? 'Record why this exact package of test-procedure decisions is acceptable.'
              : 'State what the test engineer must update before this package can be approved.'}</p>
            <label>{reviewDecision.action === 'approve' ? 'Approval rationale' : 'Rationale'}<textarea name="rationale" required autoFocus /></label>
            {reviewDecision.action === 'approve' && <>
              <label>Signature meaning<input name="meaning" defaultValue="I approve this exact test change request review stage." required /></label>
              <label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
            </>}
            <div className="decisionActions">
              <button type="button" className="quiet" disabled={busy} onClick={() => setReviewDecision(undefined)}>Cancel</button>
              <button type="submit" disabled={busy}>{reviewDecision.action === 'approve' ? 'Sign and approve package' : 'Return for changes'}</button>
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
                    {/* The ordinary answer for a newly introduced requirement, and until now it could not be
                        given: an engineer whose honest answer was "a procedure has to be written" had to leave
                        the item unanswered and go away to write one. */}
                    <option value="NewProcedureRequired">A test is required and no procedure exists yet</option>
                    <option value="NoTestRequired">No test required</option>
                  </>
                )}
              </select>
            </label>
            {outcome === 'ProcedureCoverageConfirmed' && (
              <label>Covering procedure
                <input aria-label="Search approved procedures" className="pickerSearch" value={procedureQuery}
                  onChange={event => { setProcedureQuery(event.target.value); setProcedurePage(1) }}
                  placeholder="Search by number or title..." />
                <select name="procedureId" aria-label="Covering procedure" aria-describedby="covering-procedure-help" required
                  value={procedureChoice} onChange={event => setProcedureChoice(event.target.value)}>
                  <option value="">Choose an approved procedure...</option>
                  {(procedurePicker?.items ?? []).filter(x => x.state === 'Approved').map(x => (
                    <option key={x.id} value={x.id}>{x.displayNumber} - {x.title.slice(0, 60)}</option>
                  ))}
                </select>
                <div className="pickerMeta">
                  <span>
                    {procedurePicker?.totalCount ?? 0} approved procedure{(procedurePicker?.totalCount ?? 0) === 1 ? '' : 's'} in this build -
                    showing {(procedurePicker?.items ?? []).length}. Search by number or title to find any approved procedure.
                  </span>
                  <span className="pickerPager">
                    <button type="button" disabled={(procedurePicker?.page ?? 1) <= 1}
                      onClick={() => setProcedurePage(page => Math.max(1, page - 1))}>Previous</button>
                    <button type="button" disabled={(procedurePicker?.page ?? 1) >= (procedurePicker?.totalPages ?? 1)}
                      onClick={() => setProcedurePage(page => page + 1)}>Next</button>
                  </span>
                </div>
                <small id="covering-procedure-help">Only approved procedures carried by this build. Search by number or title to bring more into this list.</small>
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

    </main>
  )
}
