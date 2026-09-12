import { useEffect, useState } from 'react'
import type { ProblemReportOption } from './ProblemReportPicker'

export type ProblemReportSource = { id: string; kind: 'ChangeRequest' | 'TestChangeRequest'; displayNumber: string }
type Candidate = ProblemReportOption & { projectId: string; sources: ProblemReportSource[] }

/** Offers context for an explicit author decision. It never changes the caller's direct links on refresh. */
export default function InheritedProblemReports({ api, projectId, releaseId, sources, selected, locked = [], onChange }: {
  api: string; projectId: string; releaseId: string; sources: ProblemReportSource[]
  selected: string[]; locked?: string[]; onChange: (ids: string[]) => void
}) {
  const sourceKey = JSON.stringify([...new Map(sources.map(source => [`${source.kind}:${source.id}`, source])).values()]
    .sort((a, b) => a.id.localeCompare(b.id)))
  const contextKey = JSON.stringify([api, projectId, sourceKey])
  const [snapshot, setSnapshot] = useState<{ key: string; candidates: Candidate[] }>()
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [refresh, setRefresh] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    const currentSources = JSON.parse(sourceKey) as ProblemReportSource[]
    setLoading(true); setError('')
    void Promise.all(currentSources.map(async source => {
      const response = await fetch(`${api}/api/problem-reports/linked/${source.kind}/${source.id}`, { signal: controller.signal })
      if (!response.ok) throw new Error('Inherited Problem Report context could not be loaded. Your direct selections are unchanged.')
      return { source, reports: await response.json() as (ProblemReportOption & { projectId: string })[] }
    })).then(results => {
      if (controller.signal.aborted) return
      const candidates = new Map<string, Candidate>()
      for (const { source, reports } of results) for (const report of reports) {
        if (report.projectId !== projectId) continue
        const existing = candidates.get(report.id)
        if (existing) existing.sources.push(source)
        else candidates.set(report.id, { ...report, sources: [source] })
      }
      setSnapshot({ key: contextKey, candidates: [...candidates.values()].sort((a, b) => a.displayNumber.localeCompare(b.displayNumber)) })
    }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Inherited context is unavailable.') })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [api, projectId, sourceKey, contextKey, refresh])
  if (!sources.length) return null
  const candidates = snapshot?.key === contextKey ? snapshot.candidates : []
  return <fieldset className="problemReportPicker" aria-label="Inherited Problem Report context">
    <legend>Problem Reports from upstream context</legend>
    <p>These reports are linked to the named upstream records. Choose the reports that also belong on this change; inheritance alone does not establish a direct link or close a report.</p>
    <button type="button" className="quiet" disabled={loading} onClick={() => setRefresh(value => value + 1)}>Refresh inherited context</button>
    {loading && <p role="status">Loading inherited Problem Reports…</p>}
    {error && <p role="alert">{error}</p>}
    {!loading && !error && !candidates.length && <p>No linked Problem Reports are recorded on these upstream sources.</p>}
    {candidates.map(report => {
      const accepted = selected.includes(report.id) || locked.includes(report.id)
      const eligible = report.targetReleaseId === releaseId
      return <label key={report.id}>
        <input type="checkbox" checked={accepted} disabled={loading || !!error || locked.includes(report.id) || !eligible}
          onChange={event => onChange(event.target.checked ? [...new Set([...selected, report.id])] : selected.filter(id => id !== report.id))} />
        <span><b>{report.displayNumber}</b> {report.title}
          <small>Inherited from {report.sources.map(source => source.displayNumber).join(', ')}.</small>
          <small>{locked.includes(report.id) ? 'Direct link already recorded.' : accepted ? 'Selected for a direct link when you save.' : 'Context only — not linked to this change.'}</small>
          {!eligible && <small>Outside the target build; retained as context and unavailable for a new direct link.</small>}
        </span>
      </label>
    })}
  </fieldset>
}
