import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { formatEvidentiaryDateTime, stateLabel } from './presentation'

export type ExactLinkLifecycle = {
  linkId: string
  state?: string | null
  outcome?: string | null
  raisedBy?: string | null
  raisedAt?: string | null
  raisedRationale?: string | null
  events: {
    id?: string
    type: string
    actorId: string
    occurredAt: string
    rationale: string
    outcome?: string | null
  }[]
}

/**
 * One control for #709's one lifecycle. Requirement links and Case-to-Procedure links supply different route
 * roots, but deliberately share the same state, rationale, outcome, permission failure, and immutable event
 * presentation. Domain-specific explorers must not fork this workflow.
 */
export default function ExactLinkLifecyclePanel({ api, routeRoot, linkId, initialState, initialLifecycle, onChanged }: {
  api: string
  routeRoot: 'trace-links' | 'case-procedure-links'
  linkId: string
  initialState?: string | null
  initialLifecycle?: ExactLinkLifecycle
  onChanged?: () => void | Promise<void>
}) {
  const [lifecycle, setLifecycle] = useState<ExactLinkLifecycle | undefined>(initialLifecycle)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setLifecycle(await apiRequest<ExactLinkLifecycle>(`${api}/api/${routeRoot}/${linkId}/lifecycle`))
      setError('')
    } catch (reason) {
      setError(operationError(reason, 'The exact-link lifecycle could not be loaded.'))
    }
  }, [api, linkId, routeRoot])

  // Digital Thread already projects the full lifecycle for every relation. Do not turn a 200-row trace page
  // into 200 duplicate GETs; Case history supplies only a summary and therefore loads on demand here.
  useEffect(() => {
    if (initialLifecycle) setLifecycle(initialLifecycle)
    else void load()
  }, [initialLifecycle, load])

  const mutate = async (event: FormEvent<HTMLFormElement>, action: 'acknowledge' | 'resolve') => {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    try {
      setBusy(true)
      setError('')
      await apiRequest(`${api}/api/${routeRoot}/${linkId}/lifecycle/${action}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          rationale: form.get('rationale'),
          ...(action === 'resolve' ? { outcome: form.get('outcome') } : {}),
        }),
      })
      await load()
      await onChanged?.()
    } catch (reason) {
      setError(operationError(reason, 'The lifecycle decision could not be recorded.'))
    } finally {
      setBusy(false)
    }
  }

  const state = lifecycle?.state ?? initialState
  if (!state || state === 'Confirmed') return null
  const open = state !== 'Closed'
  return <section className="discussionPane exactLinkLifecycle" aria-label={`Exact link lifecycle ${state}`}>
    <p className={open ? 'inspectorNote warn' : 'inspectorNote'}>
      Relationship {stateLabel(state)}{lifecycle?.outcome ? ` · ${stateLabel(lifecycle.outcome)}` : ''}
    </p>
    {error && <p className="workspaceError" role="alert">{error}</p>}
    {open && state === 'Suspect' && <form onSubmit={event => void mutate(event, 'acknowledge')}>
      <textarea name="rationale" required placeholder="Record why this exact relationship is under assessment." />
      <div className="commentFoot"><button disabled={busy}>Acknowledge relationship</button></div>
    </form>}
    {open && <form onSubmit={event => void mutate(event, 'resolve')}>
      <label>
        <span>Assessment outcome</span>
        <select name="outcome" required defaultValue="ExistingDownstreamRevisionRemainsValid">
          <option value="ExistingDownstreamRevisionRemainsValid">Existing downstream revision remains valid</option>
          <option value="NoDownstreamChangeRequired">No downstream change required</option>
          <option value="DownstreamChangeRequiredNotYetApproved">Downstream change required, not yet approved</option>
        </select>
      </label>
      <textarea name="rationale" required placeholder="Record the controlled disposition and supporting rationale." />
      <div className="commentFoot"><button disabled={busy}>Record resolution</button></div>
    </form>}
    {lifecycle?.events.map(item => <article key={item.id ?? `${item.type}-${item.occurredAt}`}>
      <div><b>{stateLabel(item.type)}</b><span><PersonName userName={item.actorId} /> · <time dateTime={item.occurredAt}>{formatEvidentiaryDateTime(item.occurredAt)}</time></span></div>
      <p>{item.rationale}</p>
      {item.outcome && <small>Outcome: {stateLabel(item.outcome)}</small>}
    </article>)}
  </section>
}
