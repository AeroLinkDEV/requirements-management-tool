import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { useDebouncedSave } from './autosave'
import { RichContentEditor } from './RichContent'
import { emptyRichContent, fromPlainText, toPlainText } from './richContentModel'
import ProblemReportCategoryPicker from './ProblemReportCategoryPicker'

type Session = { id: string; version: number; expiresAt: string; draftJson: string }
type Report = { id: string; displayNumber: string }
type Props = {
  api: string
  projectId: string
  report: Report
  impactFields: readonly (readonly [string, string])[]
  onClose: () => void
  onCommitted: () => Promise<void>
}

/**
 * The editable half of a checked-out Problem Report.
 *
 * A Problem Report used to be edited through a form of its own that posted the whole record with an expected
 * version and hoped nobody else was doing the same. Every other controlled record in AeroLink is edited the
 * same way instead: an exclusive server lease, a recovery snapshot saved while you type, and an explicit
 * check-in. There is no reason a Problem Report should be the exception, and it is the record most likely to
 * be corrected while the work it describes is still moving.
 *
 * The working copy the server hands back is kept whole and only the fields on this form are written over it,
 * so a field this editor does not show is carried through check-in rather than quietly reverted.
 */
type Editable = {
  title: string
  problemRich: string
  additionalInformationRich: string
  analysis: string
  rootCause: string
  correctiveAction: string
  systemAircraftImpact: string
  workaround: string
  category: string
  severity: string
  priority: string
  impacts: Record<string, string>
}

// React StrictMode mounts a component twice in development, and a lease checkout changes server state, so
// both mounts share one in-flight request rather than racing for the same lock.
const flights = new Map<string, Promise<{ session: Session } | { error: string }>>()
const checkout = (api: string, reportId: string) => {
  const key = `${api}|${reportId}`
  const existing = flights.get(key)
  if (existing) return existing
  const request = (async () => {
    try {
      const response = await fetch(`${api}/api/controlled-editing/checkout`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ artifactType: 'ProblemReport', artifactId: reportId, leaseMinutes: 15 }),
      })
      const body = await response.json() as Session & { error?: string; holder?: string }
      return response.ok
        ? { session: body }
        : { error: body.holder ? `${body.holder} has this Problem Report checked out.` : body.error || 'This Problem Report could not be checked out.' }
    } catch { return { error: 'This Problem Report could not be checked out.' } }
  })()
  flights.set(key, request)
  void request.finally(() => flights.delete(key))
  return request
}

/** The working copy carries the category as the resolved object the record sends, or as a bare name. */
const categoryOf = (value: unknown): string => {
  if (typeof value === 'string') return value
  if (value && typeof value === 'object' && 'value' in value) return String((value as { value: unknown }).value ?? '')
  return ''
}

const impactsFrom = (value: unknown, fields: readonly (readonly [string, string])[]) => {
  const base = Object.fromEntries(fields.map(([key]) => [key, 'Unknown']))
  try { return { ...base, ...JSON.parse(typeof value === 'string' && value ? value : '{}') } as Record<string, string> }
  catch { return base as Record<string, string> }
}

export default function ControlledProblemReportEditor({ api, projectId, report, impactFields, onClose, onCommitted }: Props) {
  const [session, setSession] = useState<Session>()
  const [draft, setDraft] = useState<Editable>()
  const [status, setStatus] = useState('Acquiring lease…')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  // Everything the server sent, including fields this form does not show. Check-in writes the whole working
  // copy back, so a field dropped here would be a field silently erased.
  const workingCopy = useRef<Record<string, unknown>>({})
  const sessionRef = useRef<Session | undefined>(undefined)
  const draftRef = useRef('')
  const lastSavedRef = useRef('')
  const savingRef = useRef(false)

  const serialize = useCallback((value: Editable) => JSON.stringify({
    ...workingCopy.current,
    title: value.title,
    problem: toPlainText(value.problemRich),
    problemRich: value.problemRich,
    additionalInformation: toPlainText(value.additionalInformationRich),
    additionalInformationRich: value.additionalInformationRich,
    analysis: value.analysis,
    rootCause: value.rootCause,
    correctiveAction: value.correctiveAction,
    systemAircraftImpact: value.systemAircraftImpact,
    workaround: value.workaround,
    // Written back as the bare name. The working copy was handed the resolved object the detail
    // response sends, and the check-in engine reads either shape.
    category: value.category || null,
    impactAssessmentJson: JSON.stringify(value.impacts),
    severity: value.severity,
    priority: value.priority,
  }), [])

  useEffect(() => { if (draft) draftRef.current = serialize(draft) }, [draft, serialize])

  useEffect(() => {
    let live = true
    void (async () => {
      const outcome = await checkout(api, report.id)
      if ('error' in outcome) { if (live) { setError(outcome.error); setStatus('Checkout unavailable') } return }
      const value = outcome.session
      try {
        const recovered = JSON.parse(value.draftJson) as Record<string, unknown>
        if (!live) return
        workingCopy.current = recovered
        const editable: Editable = {
          title: String(recovered.title ?? ''),
          problemRich: (recovered.problemRich as string) || fromPlainText(String(recovered.problem ?? '')) || emptyRichContent,
          additionalInformationRich: (recovered.additionalInformationRich as string) || fromPlainText(String(recovered.additionalInformation ?? '')) || emptyRichContent,
          analysis: String(recovered.analysis ?? ''),
          rootCause: String(recovered.rootCause ?? ''),
          correctiveAction: String(recovered.correctiveAction ?? ''),
          systemAircraftImpact: String(recovered.systemAircraftImpact ?? ''),
          workaround: String(recovered.workaround ?? ''),
          category: categoryOf(recovered.category),
          severity: String(recovered.severity ?? 'Major'),
          priority: String(recovered.priority ?? 'High'),
          impacts: impactsFrom(recovered.impactAssessmentJson, impactFields),
        }
        sessionRef.current = value
        draftRef.current = value.draftJson
        lastSavedRef.current = value.draftJson
        setSession(value); setDraft(editable); setStatus('Checked out')
      } catch { if (live) { setError('The server recovery snapshot could not be opened.'); setStatus('Snapshot error') } }
    })()
    return () => { live = false }
  }, [api, impactFields, report.id])

  const autosave = useCallback(async (): Promise<Session | undefined> => {
    const current = sessionRef.current
    const latest = draftRef.current
    if (!current || savingRef.current || !latest || latest === lastSavedRef.current) return current
    savingRef.current = true; setStatus('Saving recovery snapshot…')
    try {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/autosave`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, draftJson: latest, leaseMinutes: 15 }),
      })
      if (!response.ok) {
        const body = await response.json() as { error?: string }
        setError(body.error || 'The recovery snapshot could not be saved.')
        setStatus(response.status === 409 ? 'Edit conflict' : 'Autosave failed')
        return undefined
      }
      const value = await response.json() as { version: number; expiresAt: string }
      const next = { ...current, version: value.version, expiresAt: value.expiresAt }
      sessionRef.current = next; lastSavedRef.current = latest; setSession(next); setStatus('Autosaved')
      return next
    } catch { setError('The recovery snapshot could not be saved.'); setStatus('Autosave failed'); return undefined }
    finally { savingRef.current = false }
  }, [api])

  useDebouncedSave(draftRef.current, async () => { await autosave() }, { delaySeconds: 1, maximumSeconds: 10, enabled: !!session })

  useEffect(() => {
    if (!session?.id) return
    const heartbeat = window.setInterval(() => {
      const current = sessionRef.current
      if (!current || savingRef.current) return
      void fetch(`${api}/api/controlled-editing/sessions/${current.id}/heartbeat`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, leaseMinutes: 15 }),
      }).then(async response => {
        if (!response.ok) { setStatus('Edit conflict'); return }
        const value = await response.json() as { version: number; expiresAt: string }
        const next = { ...current, version: value.version, expiresAt: value.expiresAt }
        sessionRef.current = next; setSession(next)
      })
    }, 60_000)
    return () => { window.clearInterval(heartbeat) }
  }, [api, session?.id])

  const discard = async () => {
    const current = sessionRef.current
    if (current) {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/discard`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, reason: 'The owner discarded the checked-out Problem Report draft.' }),
      })
      if (!response.ok) { setError('The checkout could not be discarded.'); return }
    }
    await onCommitted()
    onClose()
  }

  const checkIn = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!sessionRef.current || !draft) return
    if (!draft.title.trim() || !toPlainText(draft.problemRich).trim()) {
      setError('Title and Problem Description cannot be blank.'); return
    }
    setBusy(true); setError('')
    while (savingRef.current) await new Promise(resolve => window.setTimeout(resolve, 25))
    const current = await autosave()
    if (!current) { setBusy(false); return }
    const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/check-in`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedVersion: current.version }),
    })
    if (!response.ok) {
      const body = await response.json() as { error?: string }
      setError(body.error || 'The Problem Report could not be checked in.'); setBusy(false); return
    }
    await onCommitted()
    onClose()
  }

  const set = <K extends keyof Editable>(key: K, value: Editable[K]) =>
    setDraft(current => current ? { ...current, [key]: value } : current)

  return <div className="prModal" role="dialog" aria-label={`Edit ${report.displayNumber}`}>
    {!draft
      ? <section className="prCheckoutState"><button type="button" className="close" aria-label="Close" onClick={onClose}>×</button><p>{report.displayNumber}</p><h2>{status}</h2>{error && <div className="workspaceError" role="alert">{error}</div>}</section>
      : <form onSubmit={checkIn}>
        <button type="button" className="close" aria-label="Close" onClick={() => void discard()}>×</button>
        <p>{report.displayNumber} · CONTROLLED DRAFT / EXCLUSIVE LEASE</p>
        <h2>Edit Problem Report details</h2>
        <div className="prCheckoutMeta"><span>{status}</span><small>Lease expires {session ? new Date(session.expiresAt).toLocaleTimeString() : '—'}</small></div>
        {error && <div className="workspaceError" role="alert">{error}</div>}
        <label>Title<input required value={draft.title} onChange={event => set('title', event.target.value)} /></label>
        <RichContentEditor api={api} projectId={projectId} label="Problem Description" value={draft.problemRich} onChange={value => set('problemRich', value)} />
        <RichContentEditor api={api} projectId={projectId} label="Additional Information" value={draft.additionalInformationRich} onChange={value => set('additionalInformationRich', value)} />
        <div className="prFormGrid">
          <label>Severity<select value={draft.severity} onChange={event => set('severity', event.target.value)}>{['Critical', 'High', 'Major', 'Minor', 'Trivial'].map(x => <option key={x}>{x}</option>)}</select></label>
          <label>Priority<select value={draft.priority} onChange={event => set('priority', event.target.value)}>{['Urgent', 'High', 'Normal', 'Low'].map(x => <option key={x}>{x}</option>)}</select></label>
        </div>
        <label>Analysis<textarea value={draft.analysis} onChange={event => set('analysis', event.target.value)} /></label>
        <label>Category<ProblemReportCategoryPicker api={api} value={draft.category} required onChange={value => set('category', value)} /></label>
        <label>Root cause<textarea value={draft.rootCause} onChange={event => set('rootCause', event.target.value)} /></label>
        {/* What can be done in the meantime. Empty is a real answer — it means none has been found. */}
        <label>Workaround<textarea value={draft.workaround} onChange={event => set('workaround', event.target.value)} /></label>
        <label>Corrective-action narrative<textarea value={draft.correctiveAction} onChange={event => set('correctiveAction', event.target.value)} /></label>
        <label>System / aircraft impact<textarea value={draft.systemAircraftImpact} onChange={event => set('systemAircraftImpact', event.target.value)} /></label>
        <fieldset className="prImpactEditor"><legend>Impact matrix</legend>{impactFields.map(([key, label]) =>
          <label key={key}>{label}<select value={draft.impacts[key] ?? 'Unknown'} onChange={event => set('impacts', { ...draft.impacts, [key]: event.target.value })}>{['Unknown', 'No', 'Yes'].map(value => <option key={value}>{value}</option>)}</select></label>)}
        </fieldset>
        <div className="prCheckoutFoot">
          <button type="button" className="quiet" disabled={busy} onClick={() => void discard()}>Discard checkout</button>
          <button className="primaryAction" disabled={busy}>{busy ? 'Checking in…' : 'Check in'}</button>
        </div>
      </form>}
  </div>
}
