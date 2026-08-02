import { useEffect, useState } from 'react'
import './ProblemReportPicker.css'

export type ProblemReportOption = { id: string; displayNumber: string; title: string; state: string }

export default function ProblemReportPicker({ api, projectId, releaseId, selected, locked = [], onChange, legend = 'Related problem reports' }: {
  api: string
  projectId: string
  releaseId: string
  selected: string[]
  locked?: string[]
  onChange: (ids: string[]) => void
  legend?: string
}) {
  const [reports, setReports] = useState<ProblemReportOption[]>([])
  const [error, setError] = useState('')
  useEffect(() => {
    let active = true
    fetch(`${api}/api/problem-reports?projectId=${projectId}&releaseId=${releaseId}&pageSize=200`)
      .then(async response => {
        if (!response.ok) throw new Error('Problem reports could not be loaded.')
        return response.json()
      })
      .then(value => { if (active) setReports(value.items ?? []) })
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'Problem reports could not be loaded.') })
    return () => { active = false }
  }, [api, projectId, releaseId])

  const toggle = (id: string) => onChange(selected.includes(id)
    ? selected.filter(value => value !== id)
    : [...selected, id])

  return <fieldset className="problemReportPicker">
    <legend>{legend}</legend>
    <small>A PR may drive this change. Requirement changes never create a PR automatically.</small>
    {error && <span role="alert">{error}</span>}
    {!error && !reports.length && <span>No PRs are recorded for this build.</span>}
    {reports.map(report => <label key={report.id}>
      <input type="checkbox" checked={selected.includes(report.id)} disabled={locked.includes(report.id)} onChange={() => toggle(report.id)} />
      <b>{report.displayNumber}</b><span>{report.title}</span><i>{report.state.replace(/([A-Z])/g, ' $1')}</i>
    </label>)}
  </fieldset>
}
