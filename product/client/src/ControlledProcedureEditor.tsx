import { useCallback, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, FormEvent } from 'react'
import { useDebouncedSave } from './autosave'

type Procedure = { revisionId: string; displayNumber: string }
type Session = { id: string; version: number; expiresAt: string; draftJson: string }
type Draft = {
  procedureId: string; baseNumber: string; title: string; ownerId: string; level: string; version: number
  revisionId: string; revision: number; objective: string; preconditions: string; steps: string
  expectedResult: string; state: string; authorId: string
}

type Props = { api: string; procedure: Procedure; onClose: () => void; onCommitted: () => Promise<void> }
type CheckoutOutcome = { session: Session } | { error: string }

// React StrictMode deliberately mounts, unmounts, and mounts a component again in development.
// A lease checkout is a state-changing operation, so both mounts must share one in-flight request.
const checkoutFlights = new Map<string, Promise<CheckoutOutcome>>()

const checkoutProcedure = (api: string, procedureId: string) => {
  const key = `${api}|${procedureId}`
  const existing = checkoutFlights.get(key)
  if (existing) return existing
  const request = (async (): Promise<CheckoutOutcome> => {
    try {
      const response = await fetch(`${api}/api/controlled-editing/checkout`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ artifactType: 'TestProcedure', artifactId: procedureId, leaseMinutes: 15 }),
      })
      const body = await response.json() as Session & { error?: string }
      return response.ok ? { session: body } : { error: body.error || 'This procedure could not be checked out.' }
    } catch { return { error: 'This procedure could not be checked out.' } }
  })()
  checkoutFlights.set(key, request)
  void request.finally(() => checkoutFlights.delete(key))
  return request
}

export default function ControlledProcedureEditor({ api, procedure, onClose, onCommitted }: Props) {
  const [session, setSession] = useState<Session>()
  const [draft, setDraft] = useState<Draft>()
  const [status, setStatus] = useState('Acquiring lease…')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const sessionRef = useRef<Session | undefined>(undefined)
  const draftRef = useRef('')
  const lastSavedRef = useRef('')
  const savingRef = useRef(false)

  useEffect(() => { sessionRef.current = session }, [session])
  useEffect(() => { if (draft) draftRef.current = JSON.stringify(draft) }, [draft])

  useEffect(() => {
    let live = true
    void (async () => {
      const outcome = await checkoutProcedure(api, procedure.revisionId)
      if ('error' in outcome) {
        if (live) { setError(outcome.error); setStatus('Checkout unavailable') }
        return
      }
      const value = outcome.session
      try {
        const recovered = JSON.parse(value.draftJson) as Draft
        if (!live) return
        sessionRef.current = value; draftRef.current = value.draftJson; lastSavedRef.current = value.draftJson
        setSession(value); setDraft(recovered); setStatus('Autosaved')
      } catch { if (live) { setError('The server recovery snapshot could not be opened.'); setStatus('Snapshot error') } }
    })()
    return () => { live = false }
  }, [api, procedure.revisionId])

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
        setError(body.error || 'The recovery snapshot could not be saved.'); setStatus(response.status === 409 ? 'Edit conflict' : 'Autosave failed')
        return undefined
      }
      const value = await response.json() as { version: number; expiresAt: string }
      const next = { ...current, version: value.version, expiresAt: value.expiresAt }
      sessionRef.current = next; lastSavedRef.current = latest; setSession(next); setStatus('Autosaved')
      return next
    } catch { setError('The recovery snapshot could not be saved.'); setStatus('Autosave failed'); return undefined }
    finally { savingRef.current = false }
  }, [api])

  // A second after typing stops, rather than on a fixed timer: a timer either fires on an idle form or
  // leaves the last seconds of typing unprotected. The ceiling covers writing without pausing.
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
  }, [api, autosave, session?.id])

  const discard = async () => {
    const current = sessionRef.current
    if (current) {
      const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/discard`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedVersion: current.version, reason: 'Author discarded the checked-out test procedure draft.' }),
      })
      if (!response.ok) { setError('The checkout could not be discarded.'); return }
    }
    onClose()
  }

  const checkIn = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!sessionRef.current || !draft) return
    setBusy(true); setError('')
    while (savingRef.current) await new Promise(resolve => window.setTimeout(resolve, 25))
    const current = await autosave()
    if (!current) { setBusy(false); return }
    const response = await fetch(`${api}/api/controlled-editing/sessions/${current.id}/check-in`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedVersion: current.version }),
    })
    if (!response.ok) { const body = await response.json() as { error?: string }; setError(body.error || 'The procedure could not be checked in.'); setBusy(false); return }
    await onCommitted(); onClose()
  }

  const field = (key: keyof Draft) => (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
    setDraft(current => current ? { ...current, [key]: event.target.value } : current)

  return <div className="procedureEditorBackdrop" role="presentation"><section className="procedureEditor" role="dialog" aria-modal="true" aria-label={`Edit ${procedure.displayNumber}`}>
    <header><div><p className="eyebrow">CONTROLLED DRAFT / EXCLUSIVE LEASE</p><h2>{procedure.displayNumber}</h2><p>Edits are saved to the server before check-in. Approval remains independent.</p></div><button className="modalClose" onClick={discard} aria-label="Close controlled editor">×</button></header>
    {error && <div className="procedureEditorError">{error}</div>}
    {!draft ? <div className="procedureEditorLoading">{status}</div> : <form onSubmit={checkIn}>
      <div className="procedureEditorMeta"><span>{status}</span><small>Lease expires {session ? new Date(session.expiresAt).toLocaleTimeString() : '—'}</small></div>
      <label>Procedure title<input value={draft.title} onChange={field('title')} required /></label>
      <div className="procedureEditorGrid"><label>Owner<input value={draft.ownerId} onChange={field('ownerId')} required /></label><label>Objective<textarea value={draft.objective} onChange={field('objective')} required /></label></div>
      <label>Preconditions<textarea value={draft.preconditions} onChange={field('preconditions')} /></label>
      <label>Procedure steps<textarea value={draft.steps} onChange={field('steps')} required /></label>
      <label>Expected result<textarea value={draft.expectedResult} onChange={field('expectedResult')} required /></label>
      <footer><button type="button" className="outline" onClick={discard} disabled={busy}>Discard draft</button><button disabled={busy || status === 'Edit conflict'}>{busy ? 'Checking in…' : 'Check in controlled revision'}</button></footer>
    </form>}
  </section></div>
}
