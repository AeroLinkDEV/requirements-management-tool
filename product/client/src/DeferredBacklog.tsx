import { useCallback, useEffect, useState } from 'react'
import { PersonName } from './People'

/**
 * The deferred backlog: work shelved by any earlier build and never taken up.
 *
 * Deliberately not part of a build's own list. A build's list is what it is taking and what it raised, and
 * mixing the shelf into it makes the plan read as though it already contained work nobody has committed to.
 * Bringing one in is the explicit act that moves it, and until somebody does, it sits here.
 *
 * Scoped to the register it is read from — deferred SRCRs beside SRCRs, HLRCRs beside HLRCRs — because
 * offering somebody work they cannot bring into the view they are in is offering them nothing.
 */

export type DeferredItem = {
  id: string
  displayNumber: string
  title: string
  authorId: string
  updatedAt: string
  shelvedFromReleaseId: string
  deferredFromState: string | null
  requirementCount: number
}

type Release = { id: string; version: string; isReleased?: boolean }

export default function DeferredBacklog({ api, projectId, type, softwareLevel, activeRelease, releases, onOpen, registerHref, onBroughtIn }: {
  api: string
  projectId: string
  type: 'System' | 'Software' | 'Interface'
  softwareLevel?: 'HighLevel' | 'LowLevel'
  activeRelease?: Release
  releases: Release[]
  onOpen: (id: string) => void
  registerHref: (id: string) => string
  onBroughtIn: () => void
}) {
  const [items, setItems] = useState<DeferredItem[]>([])
  const [busy, setBusy] = useState('')
  const [failure, setFailure] = useState('')

  const reload = useCallback(async () => {
    const query = new URLSearchParams({ projectId, type })
    if (softwareLevel) query.set('softwareLevel', softwareLevel)
    try {
      const response = await fetch(`${api}/api/change-requests/deferred?${query}`)
      if (!response.ok) { setItems([]); return }
      setItems(((await response.json()) as { items: DeferredItem[] }).items ?? [])
    } catch {
      // A backlog that cannot be fetched must not take the register down with it.
      setItems([])
    }
  }, [api, projectId, type, softwareLevel])

  useEffect(() => { void reload() }, [reload])

  const versionOf = (id: string) => releases.find(x => x.id === id)?.version ?? 'an earlier build'

  const bringIn = async (item: DeferredItem) => {
    if (!activeRelease) return
    setBusy(item.id); setFailure('')
    try {
      const response = await fetch(`${api}/api/change-requests/${item.id}/reinstate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ intoReleaseId: activeRelease.id }),
      })
      if (!response.ok) {
        const body = await response.json().catch(() => undefined) as { error?: string } | undefined
        setFailure(body?.error ?? `${item.displayNumber} could not be brought in.`)
        return
      }
      await reload()
      onBroughtIn()
    } catch {
      setFailure(`${item.displayNumber} could not be brought in.`)
    } finally {
      setBusy('')
    }
  }

  if (items.length === 0)
    return <div className="historyEmpty">Nothing is deferred. Work shelved by any build appears here until it is brought into one.</div>

  return (
    <section className="deferredBacklog">
      {failure && <div className="workspaceError">{failure}</div>}
      {items.map(item => (
        <div className="deferredRow" key={item.id}>
          <a className="deferredOpen" href={registerHref(item.id)} onClick={event => {
            if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
              event.preventDefault()
              onOpen(item.id)
            }
          }}>
            <b>{item.displayNumber}</b>
            <p>{item.title || 'Not written up yet'}</p>
            <small>
              {item.requirementCount} requirement change{item.requirementCount === 1 ? '' : 's'}
              {' · '}shelved from Build {versionOf(item.shelvedFromReleaseId)}
              {/* How far it got matters to somebody deciding whether to take it on: written, reviewed, or signed. */}
              {item.deferredFromState && item.deferredFromState !== 'Draft' && <> · reached {item.deferredFromState}</>}
              {' · '}<span className="personMeta"><PersonName userName={item.authorId} /></span>
            </small>
          </a>
          {activeRelease && !activeRelease.isReleased && (
            <button type="button" className="primary bringIn" disabled={busy === item.id}
              onClick={() => void bringIn(item)}>
              {busy === item.id ? 'Bringing in…' : `Bring into Build ${activeRelease.version}`}
            </button>
          )}
        </div>
      ))}
    </section>
  )
}
