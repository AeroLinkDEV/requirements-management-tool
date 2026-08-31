import { useCallback, useEffect, useRef, useState } from 'react'
import { useDebouncedSave } from './autosave'
import { RichContentEditor } from './RichContent'
import { emptyRichContent, fromPlainText, toPlainText } from './richContentModel'
import ProblemReportCategoryPicker from './ProblemReportCategoryPicker'
import { PROBLEM_REPORT_NARRATIVE as NARRATIVE } from './problemReportFields'
import ControlledAttachments from './ControlledAttachments'

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
  analysisRich: string
  rootCauseRich: string
  effectsRich: string
  containmentRich: string
  correctiveActionRich: string
  systemAircraftImpactRich: string
  workaroundRich: string
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
  const [savedAt, setSavedAt] = useState('')
  const [uploadsPending, setUploadsPending] = useState(0)
  const [inlineUploadsPending, setInlineUploadsPending] = useState(0)
  const [attachmentUploadsPending, setAttachmentUploadsPending] = useState(0)
  // The working copy exactly as the checkout returned it. This is what "changed" is measured against.
  const baseline = useRef<string>('')
  // The working copy as of the last recovery snapshot. Deliberately separate from the baseline: these are
  // two different facts and conflating them is why the save state read wrongly at first. After pressing
  // Save there is nothing *unsaved*, but there is still something *uncommitted* — the snapshot is not the
  // record — so the indicator must go quiet while the check-in control stays offered.
  const [savedSnapshot, setSavedSnapshot] = useState('')
  // Everything the server sent, including fields this form does not show. Check-in writes the whole working
  // copy back, so a field dropped here would be a field silently erased.
  const workingCopy = useRef<Record<string, unknown>>({})
  const sessionRef = useRef<Session | undefined>(undefined)
  const draftRef = useRef('')
  const lastSavedRef = useRef('')
  const savingRef = useRef(false)
  const uploadsPendingRef = useRef(0)
  const inlineUploadsPendingRef = useRef(0)
  const attachmentUploadsPendingRef = useRef(0)

  const onUploadingChange = useCallback((uploading: boolean) => {
    inlineUploadsPendingRef.current = Math.max(0, inlineUploadsPendingRef.current + (uploading ? 1 : -1))
    uploadsPendingRef.current = Math.max(0, uploadsPendingRef.current + (uploading ? 1 : -1))
    setInlineUploadsPending(inlineUploadsPendingRef.current)
    setUploadsPending(uploadsPendingRef.current)
    if (uploading) setStatus('Storing inline image…')
    else setStatus(current => current === 'Storing inline image…' ? 'Checked out' : current)
  }, [])

  const onAttachmentBusyChange = useCallback((uploading: boolean) => {
    attachmentUploadsPendingRef.current = Math.max(0, attachmentUploadsPendingRef.current + (uploading ? 1 : -1))
    uploadsPendingRef.current = Math.max(0, uploadsPendingRef.current + (uploading ? 1 : -1))
    setAttachmentUploadsPending(attachmentUploadsPendingRef.current)
    setUploadsPending(uploadsPendingRef.current)
    if (uploading) setStatus('Storing supporting file…')
    else setStatus(current => current === 'Storing supporting file…' ? 'Checked out' : current)
  }, [])

  const serialize = useCallback((value: Editable) => JSON.stringify({
    ...workingCopy.current,
    title: value.title,
    problem: toPlainText(value.problemRich),
    problemRich: value.problemRich,
    additionalInformation: toPlainText(value.additionalInformationRich),
    additionalInformationRich: value.additionalInformationRich,
    // Each rich field writes its plain projection alongside, because the plain column is what search,
    // the generated documents and every reader that cannot show structure actually read.
    ...Object.fromEntries(NARRATIVE.flatMap(field => [
      [field.key, value[field.key]],
      [field.plain, toPlainText(value[field.key])],
    ])),
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
          ...Object.fromEntries(NARRATIVE.map(field => [
            field.key,
            // A field authored before it could hold structure has only its plain value, and that is
            // adopted rather than shown as empty — the record is not blank, it just predates the editor.
            (recovered[field.key] as string) || fromPlainText(String(recovered[field.plain] ?? '')) || emptyRichContent,
          ])) as Pick<Editable, (typeof NARRATIVE)[number]['key']>,
          category: categoryOf(recovered.category),
          severity: String(recovered.severity ?? 'Major'),
          priority: String(recovered.priority ?? 'High'),
          impacts: impactsFrom(recovered.impactAssessmentJson, impactFields),
        }
        sessionRef.current = value
        draftRef.current = value.draftJson
        lastSavedRef.current = value.draftJson
        baseline.current = serialize(editable)
        setSavedSnapshot(baseline.current)
        setSession(value); setDraft(editable); setStatus('Checked out')
      } catch { if (live) { setError('The server recovery snapshot could not be opened.'); setStatus('Snapshot error') } }
    })()
    return () => { live = false }
    // serialize is memoized with no dependencies of its own, so naming it here costs nothing and keeps
    // the checkout from re-running on anything but a genuinely different report.
  }, [api, impactFields, report.id, serialize])

  const autosave = useCallback(async (): Promise<Session | undefined> => {
    const current = sessionRef.current
    const latest = draftRef.current
    if (uploadsPendingRef.current > 0) {
      setStatus('Waiting for inline image upload…')
      return undefined
    }
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

  useDebouncedSave(draftRef.current, async () => {
    const pending = draft ? serialize(draft) : ''
    if (await autosave()) { setSavedSnapshot(pending); setSavedAt(new Date().toLocaleTimeString()) }
  }, { delaySeconds: 1, maximumSeconds: 10, enabled: !!session })

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
    if (uploadsPendingRef.current > 0) {
      setError('Wait for image and supporting-file uploads to finish before discarding the checkout.')
      setStatus('Upload in progress')
      return
    }
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

  const checkIn = async () => {
    if (!sessionRef.current || !draft) return
    if (!draft.title.trim() || !toPlainText(draft.problemRich).trim()) {
      setError('Title and Problem Description cannot be blank.'); return
    }
    if (uploadsPendingRef.current > 0) {
      setError('Wait for image and supporting-file uploads to finish before saving or checking in.')
      setStatus('Upload in progress')
      return
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

  /**
   * Whether anything has changed since the checkout, compared against the working copy the server handed
   * back rather than against the last autosave — otherwise pressing Save would make the record look
   * unchanged while an uncommitted edit was still sitting in the session.
   */
  const serialized = draft ? serialize(draft) : ''
  /** Changed since checkout — that is, there is something a check-in would commit. */
  const dirty = !!draft && serialized !== baseline.current
  /** Changed since the last recovery snapshot — that is, there is something a Save would write. */
  const unsaved = !!draft && serialized !== savedSnapshot

  /** Writes the recovery snapshot now. The controlled record is untouched until check-in. */
  const saveOnly = async () => {
    if (uploadsPendingRef.current > 0) {
      setError('Wait for image and supporting-file uploads to finish before saving or checking in.')
      setStatus('Upload in progress')
      return
    }
    const pending = serialized
    setBusy(true); setError('')
    const saved = await autosave()
    setBusy(false)
    if (!saved) return
    setSavedSnapshot(pending)
    setSavedAt(new Date().toLocaleTimeString())
  }

  const set = <K extends keyof Editable>(key: K, value: Editable[K]) =>
    setDraft(current => current ? { ...current, [key]: value } : current)

  return <div className="prModal" role="dialog" aria-label={`Edit ${report.displayNumber}`}>
    {!draft
      ? <section className="prCheckoutState"><button type="button" className="close" aria-label="Close" onClick={onClose}>×</button><p>{report.displayNumber}</p><h2>{status}</h2>{error && <div className="workspaceError" role="alert">{error}</div>}</section>
      : <div className="prWholeRecord">
        <header className="prEditorHead">
          {/* Named for what it does. It discards the checkout, exactly like the footer control, and two
              buttons in one dialog both announcing themselves as "Close" left a screen-reader user unable
              to tell the dismiss from the one that keeps the work. */}
          <button type="button" className="close" aria-label="Discard checkout and close" disabled={busy || uploadsPending > 0} onClick={() => void discard()}>×</button>
          <p>{report.displayNumber} · CONTROLLED DRAFT / EXCLUSIVE LEASE</p>
          <h2>Edit Problem Report</h2>
          <div className="prCheckoutMeta"><span>{status}</span><small>Lease expires {session ? new Date(session.expiresAt).toLocaleTimeString() : '—'}</small></div>
        </header>
        <div className="prEditorBody">
        {error && <div className="workspaceError" role="alert">{error}</div>}
        {uploadsPending > 0 && <div className="workspaceNotice" role="status" aria-live="polite">
          {inlineUploadsPending > 0 && attachmentUploadsPending > 0
            ? `Storing ${inlineUploadsPending} inline image${inlineUploadsPending === 1 ? '' : 's'} and ${attachmentUploadsPending} supporting file${attachmentUploadsPending === 1 ? '' : 's'}…`
            : inlineUploadsPending > 0
              ? `Storing ${inlineUploadsPending} inline image${inlineUploadsPending === 1 ? '' : 's'}…`
              : `Storing ${attachmentUploadsPending} supporting file${attachmentUploadsPending === 1 ? '' : 's'}…`}
          {' '}Save and check in will be enabled when the upload finishes.
        </div>}
        <label>Title<input required value={draft.title} onChange={event => set('title', event.target.value)} /></label>
        <RichContentEditor api={api} projectId={projectId} editSessionId={session?.id} label="Problem Description" value={draft.problemRich} documentLike showDocumentGuidance onUploadingChange={onUploadingChange} onChange={value => set('problemRich', value)} />
        <RichContentEditor api={api} projectId={projectId} editSessionId={session?.id} label="Additional Information" value={draft.additionalInformationRich} documentLike onUploadingChange={onUploadingChange} onChange={value => set('additionalInformationRich', value)} />
        <div className="prFormGrid">
          <label>Severity<select value={draft.severity} onChange={event => set('severity', event.target.value)}>{['Critical', 'High', 'Major', 'Minor', 'Trivial'].map(x => <option key={x}>{x}</option>)}</select></label>
          <label>Priority<select value={draft.priority} onChange={event => set('priority', event.target.value)}>{['Urgent', 'High', 'Normal', 'Low'].map(x => <option key={x}>{x}</option>)}</select></label>
        </div>
        <label>Category<ProblemReportCategoryPicker api={api} value={draft.category} required onChange={value => set('category', value)} /></label>
        {NARRATIVE.map(field =>
          <RichContentEditor key={field.key} api={api} projectId={projectId} editSessionId={session?.id} label={field.label} documentLike onUploadingChange={onUploadingChange}
            value={draft[field.key]} onChange={value => set(field.key, value)} />)}
        <section className="prSupportingFiles" aria-label="Problem Report supporting files">
          <div className="prSectionHeading"><h3>Supporting files</h3><p>Controlled files stay beside the narrative and are versioned independently.</p></div>
          {session && <ControlledAttachments api={api} projectId={projectId} artifactType="ProblemReport" artifactId={report.id}
            editSessionId={session.id} canAttach onBusyChange={onAttachmentBusyChange} />}
        </section>
        <fieldset className="prImpactEditor"><legend>Impact matrix</legend>{impactFields.map(([key, label]) =>
          <label key={key}>{label}<select aria-label={label} value={draft.impacts[key] ?? 'Unknown'} onChange={event => set('impacts', { ...draft.impacts, [key]: event.target.value })}>{['Unknown', 'No', 'Yes'].map(value => <option key={value}>{value}</option>)}</select></label>)}
        </fieldset>
        </div>
        {/* Three controls, and one of them changes what it is.
            Discard throws away everything since checkout. Save writes the recovery snapshot the autosave
            already writes and keeps the window open — the controlled record, its version and its hash
            change once, on check-in, so one editing session is one entry in History. Close becomes
            "Save and check in" the moment anything has actually changed, because a window that offers to
            commit when there is nothing to commit teaches people to ignore it. */}
        <footer className="prCheckoutFoot">
          <button type="button" className="danger" disabled={busy || uploadsPending > 0} onClick={() => void discard()}>Discard checkout</button>
          <span className="prFootSpacer" />
          {unsaved
            ? <span className="prDirty">● Unsaved changes</span>
            : savedAt && <span className="prSaved">✓ Saved {savedAt}</span>}
          <button type="button" className="quiet" disabled={busy || uploadsPending > 0 || !unsaved} onClick={() => void saveOnly()}>Save</button>
          {dirty
            ? <button type="button" className="primaryAction" disabled={busy || uploadsPending > 0} onClick={() => void checkIn()}>{busy ? 'Checking in…' : uploadsPending > 0 ? 'Waiting for upload…' : 'Save and check in'}</button>
            : <button type="button" className="quiet" disabled={busy || uploadsPending > 0} onClick={() => void discard()}>Close</button>}
        </footer>
      </div>}
  </div>
}
