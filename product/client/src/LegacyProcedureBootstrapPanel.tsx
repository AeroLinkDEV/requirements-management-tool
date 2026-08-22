import { useCallback, useEffect, useState } from 'react'
import { PersonName } from './People'

type Preview = {
  baselineId: string
  baselineDisplayNumber: string
  proceduresHash: string
  activeProcedureCount: number
  retiredProcedureCount: number
  draftRevisionCount: number
  selectionRule: string
  alreadyBootstrapped: boolean
  recordedAt?: string
  recordedBy?: string
}

type Props = {
  api: string
  baselineId: string
  onCompleted: () => void | Promise<void>
}

export default function LegacyProcedureBootstrapPanel({ api, baselineId, onCompleted }: Props) {
  const [preview, setPreview] = useState<Preview>()
  const [hidden, setHidden] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    setError('')
    const response = await fetch(`${api}/api/baselines/${baselineId}/legacy-procedure-manifest-bootstrap`)
    if ([400, 403, 404].includes(response.status)) {
      setHidden(true)
      setPreview(undefined)
      return
    }
    if (!response.ok) {
      setError('The legacy verification artifact snapshot preview could not be loaded.')
      return
    }
    setHidden(false)
    setPreview(await response.json())
  }, [api, baselineId])

  useEffect(() => {
    setPreview(undefined)
    setHidden(false)
    setConfirmed(false)
    void load()
  }, [load])

  const establish = async () => {
    if (!preview || !confirmed || busy) return
    setBusy(true)
    setError('')
    const response = await fetch(
      `${api}/api/baselines/${baselineId}/legacy-procedure-manifest-bootstrap`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          expectedHash: preview.proceduresHash,
          confirmLegacySnapshot: true,
        }),
      },
    )
    const body = await response.json().catch(() => ({})) as Preview & { error?: string }
    if (!response.ok) {
      setError(body.error || 'The legacy verification artifact snapshot could not be established.')
      setBusy(false)
      return
    }
    setPreview(body)
    setConfirmed(false)
    setBusy(false)
    await onCompleted()
  }

  if (hidden || !preview) return error ? <div className="workspaceError">{error}</div> : null

  return (
    <section className="baselineCard" aria-label="Legacy verification artifact manifest bootstrap">
      <div className="baselineCardTitle">
        <div>
          <p className="eyebrow">LEGACY CONFIGURATION MIGRATION</p>
          <h3>Legacy verification artifact manifest</h3>
          <p>
            Exact verification artifact membership for {preview.baselineDisplayNumber}, established from the
            controlled inventory that exists now.
          </p>
        </div>
        {preview.alreadyBootstrapped && <div className="frozenMark">✓ Snapshot established</div>}
      </div>
      <div className="hashPanel">
        <span>VERIFICATION ARTIFACT MANIFEST SHA-256</span>
        <code>{preview.proceduresHash}</code>
        <p>{preview.selectionRule}</p>
      </div>
      <div className="manifestStats">
        <div><b>{preview.activeProcedureCount}</b><span>active revisions included</span></div>
        <div><b>{preview.retiredProcedureCount}</b><span>retired identities suppressed</span></div>
        <div><b>{preview.draftRevisionCount}</b><span>draft revisions excluded</span></div>
      </div>
      {preview.alreadyBootstrapped ? (
        <p>
          Recorded {preview.recordedAt ? new Date(preview.recordedAt).toLocaleString() : 'at the controlled event time'}
          {preview.recordedBy && <> by <PersonName userName={preview.recordedBy} /></>}.
          This is an immutable legacy bootstrap snapshot, not reconstructed historical release evidence.
        </p>
      ) : (
        <>
          <div className="workspaceWarning" role="note">
            This operation records a migration snapshot of the current legacy controlled inventory.
            It does not claim that this exact membership was recorded when the historical build was released.
          </div>
          <label>
            <input
              type="checkbox"
              checked={confirmed}
              onChange={event => setConfirmed(event.target.checked)}
            />{' '}
            I confirm the displayed hash and understand the historical-evidence limitation.
          </label>
          {error && <div className="workspaceError">{error}</div>}
          <div className="baselineActions">
            <button type="button" disabled={!confirmed || busy} onClick={establish}>
              {busy ? 'Establishing…' : 'Establish legacy verification artifact snapshot'}
            </button>
          </div>
        </>
      )}
    </section>
  )
}
