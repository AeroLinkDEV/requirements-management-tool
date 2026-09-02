import { useEffect, useState, type ReactNode } from 'react'
import { changeRequestAllocation, changeRequestState, formatOrdinaryDateTime } from './presentation'
import { PersonName } from './People'
import ChangeRequestInspector from './ChangeRequestInspector'
import { ControlledArtifactInspectorEmpty } from './ControlledArtifactExplorer'
import './HistoryExplorer.css'

/**
 * The register a change request is read from, whichever discipline raised it.
 *
 * The requirements and verification sides show the same register over different artifacts: a controlled
 * number, what it is called, how many controlled changes it proposes, who raised it, which build it is
 * allocated to, how far it has got, and when it last moved. That was true of the requirements page and not of
 * the verification one, which showed a bare table of numbers with a `Title` column reading "Not written up
 * yet" and a `Procedure decisions` count nobody could interpret.
 *
 * So it is one component rendered by both rather than two that happen to look alike. What differs between
 * them is passed in — the noun for what a package proposes, the heading, the action, and the rows themselves.
 * Everything a reader recognises as "the register" is here, once.
 */

export type RegisterRelease = { id: string; version: string; isReleased: boolean }

export type RegisterRow = {
  id: string
  baseNumber: string
  revision: number
  displayNumber: string
  title: string
  state: string
  deferredFromState?: string | null
  authorId: string
  targetReleaseId: string
  /** How many controlled changes this record proposes — requirement changes, or procedure changes. */
  changeCount: number
  updatedAt: string
  revisionCount: number
  /** Anything the artifact wants beside its number, such as the requirements HLR + LLR badge. */
  badge?: ReactNode
}

type Props = {
  /** What a record proposes, in the plural: "requirement changes", "procedure changes". */
  changeNoun: string
  /** What these records are called in a sentence: "system change requests". */
  recordNoun: string
  contextLabel: string
  activeRelease?: RegisterRelease
  releases: RegisterRelease[]
  rows: RegisterRow[]
  totalCount: number
  page: number
  totalPages: number
  onPageChange: (page: number) => void
  query: string
  onQueryChange: (query: string) => void
  stateIntent: string
  onStateIntentChange: (intent: string) => void
  stateOptions: { value: string; label: string }[]
  onOpen: (id: string) => void
  onSelect?: (id: string) => void
  selectedId?: string
  registerHref: (id: string) => string
  inspector?: {
    api: string
    kind: 'ChangeRequest' | 'TestChangeRequest'
    projectId: string
    releaseId: string
    registerType?: string
    artifactHref?: (node: { id: string; kind: string; displayNumber?: string | null; level?: string | null; buildId?: string | null; artifactId?: string | null }) => string | undefined
    digitalThreadHref?: (id: string) => string | undefined
  }
  /** Earlier revisions of one record, fetched only when the reader asks to see them. */
  onLoadRevisions: (row: RegisterRow) => Promise<RegisterRow[]>
}

export default function ChangeRequestRegister({
  changeNoun, recordNoun, contextLabel, activeRelease, releases, rows, totalCount, page, totalPages,
  onPageChange, query, onQueryChange, stateIntent, onStateIntentChange, stateOptions, onOpen, onSelect, selectedId,
  registerHref, inspector, onLoadRevisions,
}: Props) {
  const [expanded, setExpanded] = useState<Record<string, RegisterRow[] | 'loading'>>({})
  const [error, setError] = useState('')
  const [internalSelectedId, setInternalSelectedId] = useState(selectedId ?? '')
  useEffect(() => setInternalSelectedId(selectedId ?? ''), [selectedId])
  const select = (id: string) => {
    if (internalSelectedId === id) return
    setInternalSelectedId(id)
    onSelect?.(id)
  }

  const stateLabelFor = (value: string) => stateOptions.find(x => x.value === value)?.label ?? value

  const toggleRevisions = async (row: RegisterRow) => {
    if (expanded[row.baseNumber]) {
      setExpanded(current => { const next = { ...current }; delete next[row.baseNumber]; return next })
      return
    }
    setExpanded(current => ({ ...current, [row.baseNumber]: 'loading' }))
    try {
      const behind = await onLoadRevisions(row)
      setExpanded(current => ({
        ...current,
        [row.baseNumber]: behind.filter(x => x.revision < row.revision).sort((a, b) => b.revision - a.revision),
      }))
    } catch {
      setExpanded(current => { const next = { ...current }; delete next[row.baseNumber]; return next })
      setError('The earlier revisions could not be loaded.')
    }
  }

  // The allocation and state helpers the requirements register already uses, so a deferred record reads the
  // same on both sides: the allocation says it is on the shelf, the state says how far it got before it went.
  const facts = (row: RegisterRow, superseded = false) => ({
    state: row.state,
    deferredFromState: row.deferredFromState,
    targetRelease: releases.find(x => x.id === row.targetReleaseId),
    superseded,
  })

  const rowMarkup = (row: RegisterRow, superseded = false) => (
    <a className={`${superseded ? 'historyRow allocation superseded' : 'historyRow allocation'}${internalSelectedId === row.id ? ' selected' : ''}`}
      key={row.id} data-register-row={row.displayNumber} data-register-id={row.id}
      data-selected={internalSelectedId === row.id ? 'true' : 'false'}
      aria-current={internalSelectedId === row.id ? 'true' : undefined}
      href={registerHref(row.id)}
      onClick={event => {
        // A primary click is selection, not navigation. The real href remains available for middle/Ctrl-click
        // and for assistive technology users; the inspector's explicit Open control is the intentional opener.
        if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
          event.preventDefault(); select(row.id)
        }
      }}
      onKeyDown={event => {
        if (!event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey && (event.key === ' ' || event.key === 'Spacebar')) { event.preventDefault(); select(row.id) }
        if (!event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey && event.key === 'Enter') { event.preventDefault(); select(row.id) }
      }}
      onDoubleClick={event => { if (!event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) { event.preventDefault(); onOpen(row.id) } }}>
      <div>
        <b>{row.displayNumber}</b>
        {row.badge}
        <p>{row.title || 'Not written up yet'}</p>
        <small>
          {row.changeCount} {changeNoun}
          {row.authorId
            // Authorship is accountability, so the register names the person the way the detail surface does
            // — PersonName resolves seeded accounts to a person and falls back to the account name for
            // anyone else. The role stays visible but secondary; the initials chip used here before rendered
            // "SR · Systems Requirements Author", which attributed controlled work to a role.
            ? <> · <span className="personMeta"><PersonName userName={row.authorId} withRole /></span></>
            // Most packages exist because an assessment concluded test work was required. Saying so is more
            // use than a name nobody chose.
            : <> · <span className="personMeta raisedAutomatically">Raised by assessment</span></>}
        </small>
      </div>
      <span className={row.state === 'Deferred' ? 'allocationCell deferred' : 'allocationCell'}>
        {changeRequestAllocation(facts(row))}
      </span>
      <i className={`historyState ${(superseded ? 'superseded' : row.state).toLowerCase()}`}
        data-state={superseded ? 'Superseded' : row.state}>{changeRequestState(facts(row, superseded))}</i>
      <time dateTime={row.updatedAt}>{formatOrdinaryDateTime(row.updatedAt)}</time>
    </a>
  )

  const emptyState = query
    ? <div className="historyEmpty">No {recordNoun} match “{query}”{stateIntent ? ` within the ${stateLabelFor(stateIntent).toLowerCase()} filter` : ''} for Build {activeRelease?.version}. <button type="button" onClick={() => onQueryChange('')}>Clear search</button></div>
    : stateIntent
      ? <div className="historyEmpty">No {stateLabelFor(stateIntent).toLowerCase()} {recordNoun} match Build {activeRelease?.version}. <button type="button" onClick={() => onStateIntentChange('')}>Clear lifecycle filter</button></div>
      : <div className="historyEmpty">No {recordNoun} are recorded for Build {activeRelease?.version}.</div>

  return <>
    {error && <div className="workspaceError">{error}</div>}
    <section className="historyTools">
      <div className="historyContext">
        <b>Build {activeRelease?.version}</b>
        <span>{contextLabel} · {totalCount} records</span>
      </div>
      <div className="historyFilters">
        <input aria-label="Search change requests" value={query}
          onChange={event => onQueryChange(event.target.value)}
          placeholder="Search number, title, statement, rationale…" />
        <select aria-label="Lifecycle state filter" value={stateIntent}
          onChange={event => onStateIntentChange(event.target.value)}>
          <option value="">All lifecycle states</option>
          {stateOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
        </select>
      </div>
    </section>

    {stateIntent && <div className="historyActiveFilter" role="status">
      <div>
        <span>ACTIVE FILTER</span>
        <b>{stateLabelFor(stateIntent)}</b>
        <small>{totalCount} matching record{totalCount === 1 ? '' : 's'} in Build {activeRelease?.version}</small>
      </div>
      <button type="button" onClick={() => onStateIntentChange('')}
        aria-label={`Clear ${stateLabelFor(stateIntent)} lifecycle filter`}>Clear filter ×</button>
    </div>}

    <section className={inspector ? 'registerInspectorLayout' : undefined}>
    <div className="historyTable">
      <div className="tableHead allocation">
        <span>Change request revision</span><span>Build allocation</span><span>State</span><span>Last activity</span>
      </div>
      {rows.map(row => {
        const behind = expanded[row.baseNumber]
        return <div className="historyGroup" key={row.id}>
          {rowMarkup(row)}
          {row.revisionCount > 1 && <button type="button" className="revisionToggle" aria-expanded={Boolean(behind)}
            onClick={() => toggleRevisions(row)}>
            {behind ? 'Hide' : 'Show'} {row.revisionCount - 1} superseded revision{row.revisionCount - 1 === 1 ? '' : 's'}
          </button>}
          {behind === 'loading' && <div className="revisionHistory"><span>Loading earlier revisions…</span></div>}
          {Array.isArray(behind) && <div className="revisionHistory">{behind.map(prior => rowMarkup(prior, true))}</div>}
        </div>
      })}
      {!rows.length && emptyState}
      {totalPages > 1 && <div className="historyPager">
        <button type="button" disabled={page <= 1} onClick={() => onPageChange(Math.max(1, page - 1))}>← Previous</button>
        <span>Page {page} of {totalPages} · {totalCount} records</span>
        <button type="button" disabled={page >= totalPages} onClick={() => onPageChange(Math.min(totalPages, page + 1))}>Next →</button>
      </div>}
    </div>
    {inspector && internalSelectedId
      ? <ChangeRequestInspector api={inspector.api} id={internalSelectedId} kind={inspector.kind} projectId={inspector.projectId} releaseId={inspector.releaseId} registerType={inspector.registerType} href={registerHref(internalSelectedId)} artifactHref={inspector.artifactHref} digitalThreadHref={inspector.digitalThreadHref?.(internalSelectedId)} onClose={() => { setInternalSelectedId(''); onSelect?.('') }} onOpen={onOpen} />
      : inspector ? <ControlledArtifactInspectorEmpty title="change request" description="Choose a row to inspect its controlled revision, trace, history, and discussion." /> : null}
    </section>
  </>
}
