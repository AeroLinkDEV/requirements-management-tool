import { useEffect, useRef, useState, type FormEvent } from 'react'
import ExactArtifactLink from './ExactArtifactLink'
import { PersonName } from './People'
import { stateLabel } from './presentation'
import type { ExactTraceArtifact } from './routing'
import { useCodeRead, type CodeRelationship } from './codeWorkspaceData'

export default function CodeRelationshipList({ api, projectId, items, readOnly, onChanged,
  empty = 'No direct relationship is recorded.', traceArtifactHref }: { api: string; projectId: string; items: CodeRelationship[];
  readOnly: boolean; onChanged: () => void; empty?: string;
  traceArtifactHref?: (target: ExactTraceArtifact) => string | undefined }) {
    return <section className="codeRelationships"><h3>Linked AeroLink artifacts</h3>{items.length === 0 ? <p>{empty}</p> : <ul>
    {items.map(item => <Relationship key={`${projectId}/${item.relationshipKind}/${item.id}/${item.version}`}
      {...{ api, projectId, item, readOnly, onChanged, traceArtifactHref }} />)}</ul>}</section>
}

function Relationship({ api, projectId, item, readOnly, onChanged, traceArtifactHref }: { api: string; projectId: string;
  item: CodeRelationship; readOnly: boolean; onChanged: () => void;
  traceArtifactHref?: (target: ExactTraceArtifact) => string | undefined }) {
  const [showHistory, setShowHistory] = useState(false)
  const [withdrawing, setWithdrawing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  const base = `${api}/api/projects/${projectId}/code/relationships/${item.relationshipKind}/${item.id}`
  const history = useCodeRead<{ events: { id: string; eventKind: string; actor: string; occurredAt: string; rationale: string }[] }>(showHistory ? `${base}/history` : undefined)
  const change = async (action: 'withdraw' | 're-add', rationale?: string) => {
    if (busy || readOnly) return
    const controller = new AbortController(); pending.current = controller
    setBusy(true); setError('')
    try {
      const response = await fetch(`${base}/${action}`, { method: 'POST', signal: controller.signal,
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedVersion: item.version, rationale }) })
      const result = await response.json()
      if (!response.ok) throw new Error(result.error ?? 'Relationship changed. Refresh before trying again.')
      if (!controller.signal.aborted) onChanged()
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Relationship update failed.')
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  const withdraw = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    void change('withdraw', String(new FormData(event.currentTarget).get('rationale') ?? ''))
  }
  const exactTarget = (() => {
    const base = { id: item.targetIdentityId, displayNumber: item.targetDisplaySnapshot }
    switch (item.targetKind) {
      case 'RequirementRevision': return { ...base, kind: 'RequirementRevision', artifactId: item.targetOwnerIdentityId }
      case 'ChangeRequestRevision': return { ...base, kind: 'ChangeRequest' }
      case 'RequirementProposal': return { ...base, kind: 'RequirementProposal', artifactId: item.targetOwnerIdentityId }
      case 'ProblemReportRevision': return { ...base, kind: 'ProblemReportRevision', artifactId: item.targetOwnerIdentityId }
      default: return undefined
    }
  })()
  const targetHref = exactTarget ? traceArtifactHref?.(exactTarget) : undefined
  return <li><ExactArtifactLink href={targetHref}>{item.targetDisplaySnapshot || 'Retained exact target'}</ExactArtifactLink>
    {item.mergeRequestIid && <p>{item.mergeRequestUrlSnapshot
      ? <a href={item.mergeRequestUrlSnapshot} target="_blank" rel="noreferrer">GitLab !{item.mergeRequestIid} · {item.mergeRequestTitleSnapshot || 'Recorded merge request'}</a>
      : `Associated GitLab MR !${item.mergeRequestIid}`}</p>}
    <p>{stateLabel(item.targetKind)} · {stateLabel(item.meaning)} · {item.isActive ? 'Active' : 'Withdrawn'}</p>
    {item.path && <p>{item.path}{item.startLine ? ` · Lines ${item.startLine}–${item.endLine}` : ''}</p>}
    {item.withdrawalRationale && <p>{item.withdrawalRationale}</p>}
    <small>Recorded by <PersonName userName={item.recordedBy} /> · {new Date(item.recordedAt).toLocaleString()}</small>
    <div className="codeCommandBar"><button aria-expanded={showHistory} onClick={() => setShowHistory(value => !value)}>Relationship history</button>
      {!readOnly && item.capabilities.canWithdraw && <button disabled={busy} onClick={() => setWithdrawing(true)}>Withdraw relationship</button>}
      {!readOnly && item.capabilities.canReAdd && <button disabled={busy} onClick={() => void change('re-add')}>Re-add exact relationship</button>}</div>
    {withdrawing && !readOnly && item.capabilities.canWithdraw && <form onSubmit={withdraw}>
      <label>Withdrawal rationale<textarea name="rationale" required maxLength={2000} disabled={busy} /></label>
      <button disabled={busy}>Confirm withdrawal</button><button type="button" disabled={busy} onClick={() => setWithdrawing(false)}>Cancel</button></form>}
    {error && <p role="alert">{error}</p>}
    {showHistory && <section aria-label="Relationship history">{history.loading && <p>Loading history…</p>}
      {history.error && <p role="alert">{history.error}</p>}
      <ol>{history.value?.events.map(event => <li key={event.id}>{stateLabel(event.eventKind)} · <PersonName userName={event.actor} /> · {new Date(event.occurredAt).toLocaleString()}
        {event.rationale && <p>{event.rationale}</p>}</li>)}</ol></section>}
  </li>
}
