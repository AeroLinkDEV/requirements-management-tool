import { useEffect, useMemo, useState } from 'react'
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
  const [remembered, setRemembered] = useState<Record<string,ProblemReportOption>>({})
  const [query,setQuery] = useState('')
  const [busy,setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    let active = true
    const timer=window.setTimeout(()=>{
      setBusy(true);setError('')
      const search=query.trim()?`&search=${encodeURIComponent(query.trim())}`:''
      fetch(`${api}/api/problem-reports?projectId=${projectId}&pageSize=50${search}`)
        .then(async response => {
          if (!response.ok) throw new Error('Problem reports could not be loaded.')
          return response.json()
        })
        .then(value => { if (active) { const items=value.items??[];setReports(items);setRemembered(current=>({...current,...Object.fromEntries(items.map((item:ProblemReportOption)=>[item.id,item]))})) } })
        .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'Problem reports could not be loaded.') })
        .finally(()=>{if(active)setBusy(false)})
    },180)
    return () => { active = false;window.clearTimeout(timer) }
  }, [api, projectId, query, releaseId])

  useEffect(()=>{
    let active=true
    const missing=[...new Set([...selected,...locked])].filter(id=>!remembered[id])
    if(!missing.length)return()=>{active=false}
    Promise.all(missing.map(async id=>{const response=await fetch(`${api}/api/problem-reports/${id}`);return response.ok?await response.json() as ProblemReportOption:undefined}))
      .then(items=>{if(active)setRemembered(current=>({...current,...Object.fromEntries(items.filter(Boolean).map(item=>[item!.id,item!]))}))})
      .catch(()=>{})
    return()=>{active=false}
  },[api,locked,remembered,selected])

  const toggle = (id: string) => onChange(selected.includes(id)
    ? selected.filter(value => value !== id)
    : [...selected, id])

  const pinnedIds=useMemo(()=>new Set([...selected,...locked]),[locked,selected])
  const visible=useMemo(()=>[...Object.values(remembered).filter(report=>pinnedIds.has(report.id)),...reports.filter(report=>!pinnedIds.has(report.id))],[pinnedIds,reports,remembered])

  return <fieldset className="problemReportPicker">
    <legend>{legend}</legend>
    <label className="problemReportSearch"><span>Find controlled PR</span><input type="search" value={query} onChange={event=>setQuery(event.target.value)} placeholder="Search PR number, title, problem, or root cause"/></label>
    {error && <span role="alert">{error}</span>}
    {busy&&<span role="status">Searching problem reports…</span>}
    {!busy&&!error&&!visible.length && <span>{query.trim()?'No problem reports match this search.':'No PRs are recorded for this build.'}</span>}
    {visible.map(report => <label key={report.id}>
      <input type="checkbox" checked={selected.includes(report.id)} disabled={locked.includes(report.id)} onChange={() => toggle(report.id)} />
      <b>{report.displayNumber}</b><span>{report.title}</span><i>{report.state.replace(/([A-Z])/g, ' $1')}</i>
    </label>)}
  </fieldset>
}
