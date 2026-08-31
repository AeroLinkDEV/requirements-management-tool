import { useCallback, useEffect, useState } from 'react'
import ChangeRequestRegister, { type RegisterRow } from './ChangeRequestRegister'
import type { TestDiscipline } from './TestResultsWorkspace'
import { isVerificationProcedureKind, testChangeRequestAcronym, verificationArtifactNoun } from './presentation'
import type { ExactTraceArtifact } from './routing'
import './HistoryExplorer.css'

/**
 * The verification side's change request register.
 *
 * The same page as the requirements one, over test change requests. It was not a page at all: the packages
 * controlling a build's test procedures were a bare table inside the coverage workspace, with a `Title` column
 * that mostly read "Not written up yet", a `Procedure decisions` count nobody could interpret, and no build
 * allocation, search, lifecycle filter or paging. A reader moving from Change Requests on the requirements
 * side to Change Requests here arrived somewhere that did not resemble what they had just left.
 *
 * The register itself is {@link ChangeRequestRegister}, shared with the requirements side. Only what a package
 * is and where its rows come from is here.
 */

type Release = { id: string; version: string; isReleased: boolean }

type TestChangeRequestRow = {
  id: string
  baseNumber: string
  revision: number
  displayNumber: string
  title: string
  state: string
  deferredFromState?: string | null
  authorId: string
  targetReleaseId: string
  discipline: string
  artifactKind?: string
  artifactLabel?: string
  artifactCount?: number
  procedureCount?: number
  updatedAt: string
  revisionCount: number
}

// The lifecycle a package actually has. `SelectedForBaseline` is a change request's answer to "which build",
// which a package answers by the build it was raised against, so it is not offered here.
const stateOptions = [
  { value: 'Draft', label: 'Draft' },
  { value: 'InReview', label: 'In review' },
  { value: 'Approved', label: 'Approved' },
  { value: 'Deferred', label: 'Deferred' },
  { value: 'Superseded', label: 'Superseded' },
]

const disciplineArea = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'HLR' : 'LLR'

export default function TestChangeRequestRegisterPage({
  api, projectId, releases, activeReleaseId, discipline, artifactKind, registerHref, selectedId, onSelect, onBack, onOpen, onCreate, artifactHref, embedded = false,
}: {
  api: string
  projectId: string
  releases: Release[]
  activeReleaseId: string
  discipline: TestDiscipline
  artifactKind?: string
  registerHref: (id: string) => string
  selectedId?: string
  onSelect?: (id?: string) => void
  onBack?: () => void
  onOpen: (id: string) => void
  onCreate?: () => void
  artifactHref?: (node: ExactTraceArtifact) => string | undefined
  embedded?: boolean
}) {
  const [query, setQuery] = useState('')
  const [stateIntent, setStateIntent] = useState('')
  const [page, setPage] = useState(1)
  const [totalCount, setTotalCount] = useState(0)
  const [totalPages, setTotalPages] = useState(1)
  const [rows, setRows] = useState<TestChangeRequestRow[]>([])
  const activeRelease = releases.find(x => x.id === activeReleaseId)
  const artifactNoun = verificationArtifactNoun(discipline, artifactKind)
  const procedurePackage = discipline === 'System' || isVerificationProcedureKind(artifactKind)
  const registerTitle = procedurePackage
    ? disciplineLabel(discipline) + ' Procedure Change Requests'
    : disciplineLabel(discipline) + ' Test Change Requests'

  const load = useCallback(async () => {
    const params = new URLSearchParams({
      projectId, releaseId: activeReleaseId, discipline, page: String(page), pageSize: '50',
    })
    if (artifactKind) params.set('artifactKind', isVerificationProcedureKind(artifactKind) ? 'Procedure' : 'Case')
    if (stateIntent) params.set('state', stateIntent)
    if (query) params.set('search', query)
    const response = await fetch(`${api}/api/history/test-change-requests?${params}`)
    if (!response.ok) return
    const body = await response.json()
    setRows(body.items)
    setTotalCount(body.totalCount)
    setTotalPages(Math.max(1, body.totalPages))
  }, [activeReleaseId, api, artifactKind, discipline, page, projectId, query, stateIntent])

  // Debounced the same way the requirements register is, so typing a search does not fire a request per key.
  useEffect(() => { const timer = setTimeout(load, 180); return () => clearTimeout(timer) }, [load])
  useEffect(() => { setPage(1) }, [activeReleaseId, artifactKind, discipline])

  const toRegisterRow = (row: TestChangeRequestRow): RegisterRow => ({
    id: row.id, baseNumber: row.baseNumber, revision: row.revision, displayNumber: row.displayNumber,
    title: row.title, state: row.state, deferredFromState: row.deferredFromState, authorId: row.authorId,
    targetReleaseId: row.targetReleaseId, changeCount: row.artifactCount ?? row.procedureCount ?? 0, updatedAt: row.updatedAt,
    revisionCount: row.revisionCount,
  })

  const loadRevisions = async (row: RegisterRow) => {
    const params = new URLSearchParams({ projectId, baseNumber: row.baseNumber, page: '1', pageSize: '50' })
    if (artifactKind) params.set('artifactKind', isVerificationProcedureKind(artifactKind) ? 'Procedure' : 'Case')
    const response = await fetch(`${api}/api/history/test-change-requests?${params}`)
    if (!response.ok) throw new Error(String(response.status))
    const body = await response.json() as { items: TestChangeRequestRow[] }
    return body.items.map(toRegisterRow)
  }

  const register = <ChangeRequestRegister
    changeNoun={`${artifactNoun} changes`}
    recordNoun={`${testChangeRequestAcronym(discipline, artifactKind)} ${artifactNoun} change requests`}
    contextLabel={`${disciplineArea(discipline)} ${artifactNoun.toLowerCase()} area`}
    activeRelease={activeRelease} releases={releases}
    rows={rows.map(toRegisterRow)} totalCount={totalCount}
    page={page} totalPages={totalPages} onPageChange={setPage}
    query={query} onQueryChange={value => { setQuery(value); setPage(1) }}
    stateIntent={stateIntent} onStateIntentChange={value => { setStateIntent(value); setPage(1) }}
    stateOptions={stateOptions}
    onOpen={onOpen} onSelect={onSelect} selectedId={selectedId} registerHref={registerHref} inspector={{api,kind:'TestChangeRequest',projectId,releaseId:activeReleaseId,registerType:artifactKind ? (isVerificationProcedureKind(artifactKind) ? 'Procedure' : 'Case') : undefined,artifactHref}} onLoadRevisions={loadRevisions} />

  if (embedded) return register

  return <main className="historyPage">
    <header className="historyHeader">
      <div>
        {onBack && <button className="back" onClick={onBack}>← Command Center</button>}
        <p className="eyebrow">{disciplineArea(discipline).toUpperCase()} TEST CHANGE CONTROL / BUILD {activeRelease?.version}</p>
        <h1>{registerTitle}</h1>
        <p>{activeRelease?.isReleased
          ? `Released ${disciplineLabel(discipline)} test change history owned by Build ${activeRelease.version}.`
          : `Active and deferred ${disciplineLabel(discipline)} test change requests owned by Build ${activeRelease?.version}.`}</p>
      </div>
      {!activeRelease?.isReleased && onCreate && (
        <button className="recordBuild" onClick={onCreate}>
          + New {disciplineLabel(discipline)} Test {procedurePackage ? 'Procedure' : 'Case'} Change Request
        </button>
      )}
    </header>

    {register}
  </main>
}
