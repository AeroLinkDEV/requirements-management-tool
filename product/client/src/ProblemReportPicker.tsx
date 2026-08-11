import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import './ProblemReportPicker.css'

export type ProblemReportOption = {
  id: string
  displayNumber: string
  title: string
  state: string
  targetReleaseId?: string
}

type SelectionScope = 'project' | 'target-build'

const pageSize = 50

export default function ProblemReportPicker({ api, projectId, scope, releaseId, selected, locked = [], onChange, legend = 'Related problem reports' }: {
  api: string
  projectId: string
  scope: SelectionScope
  releaseId?: string
  selected: string[]
  locked?: string[]
  onChange: (ids: string[]) => void
  legend?: string
}) {
  const [reports, setReports] = useState<ProblemReportOption[]>([])
  const [remembered, setRemembered] = useState<Record<string,ProblemReportOption>>({})
  const [query,setQuery] = useState('')
  const [page,setPage] = useState(1)
  const [totalCount,setTotalCount] = useState(0)
  const [busy,setBusy] = useState(false)
  const [error, setError] = useState('')
  const userSelected = useRef(new Set<string>())
  const priorTarget = useRef(releaseId)

  if (scope === 'target-build' && !releaseId) {
    throw new Error('A target-build Problem Report picker requires a releaseId.')
  }

  useEffect(() => {
    setPage(1)
    setReports([])
    setTotalCount(0)
  }, [projectId, query, releaseId, scope])

  useEffect(() => {
    if (scope !== 'target-build' || priorTarget.current === releaseId) return
    priorTarget.current = releaseId
    const invalidUncommitted = userSelected.current
    if (invalidUncommitted.size) {
      onChange(selected.filter(id => !invalidUncommitted.has(id)))
      invalidUncommitted.clear()
    }
  }, [onChange, releaseId, scope, selected])

  useEffect(() => {
    let active = true
    const controller = new AbortController()
    const timer=window.setTimeout(()=>{
      setBusy(true);setError('')
      const params = new URLSearchParams({ projectId, page: String(page), pageSize: String(pageSize) })
      if (scope === 'target-build') params.set('targetReleaseId', releaseId!)
      if (query.trim()) params.set('search', query.trim())
      fetch(`${api}/api/problem-reports?${params}`, { signal: controller.signal })
        .then(async response => {
          if (!response.ok) throw new Error('Problem reports could not be loaded.')
          return response.json()
        })
        .then(value => {
          if (!active) return
          const items = (value.items ?? []) as ProblemReportOption[]
          setReports(current => page === 1 ? items : [...current, ...items.filter(item => !current.some(existing => existing.id === item.id))])
          setTotalCount(value.totalCount ?? items.length)
          setRemembered(current=>({...current,...Object.fromEntries(items.map(item=>[item.id,item]))}))
        })
        .catch(reason => {
          if (active && reason?.name !== 'AbortError') setError(reason instanceof Error ? reason.message : 'Problem reports could not be loaded.')
        })
        .finally(()=>{if(active)setBusy(false)})
    },180)
    return () => { active = false;controller.abort();window.clearTimeout(timer) }
  }, [api, page, projectId, query, releaseId, scope])

  useEffect(()=>{
    let active=true
    const missing=[...new Set([...selected,...locked])].filter(id=>!remembered[id])
    if(!missing.length)return()=>{active=false}
    Promise.all(missing.map(async id=>{const response=await fetch(`${api}/api/problem-reports/${id}`);return response.ok?await response.json() as ProblemReportOption:undefined}))
      .then(items=>{if(active)setRemembered(current=>({...current,...Object.fromEntries(items.filter(Boolean).map(item=>[item!.id,item!]))}))})
      .catch(()=>{})
    return()=>{active=false}
  },[api,locked,remembered,selected])

  const isCandidate = useCallback((report: ProblemReportOption | undefined) => Boolean(report)
    && (scope === 'project' || report!.targetReleaseId === releaseId), [releaseId, scope])
  const toggle = (id: string) => {
    if (selected.includes(id)) {
      userSelected.current.delete(id)
      onChange(selected.filter(value => value !== id))
      return
    }
    if (!isCandidate(remembered[id])) return
    userSelected.current.add(id)
    onChange([...selected, id])
  }

  const pinnedIds=useMemo(()=>new Set([...selected,...locked]),[locked,selected])
  const visible=useMemo(()=>[
    ...Object.values(remembered).filter(report=>pinnedIds.has(report.id)),
    ...reports.filter(report=>!pinnedIds.has(report.id) && isCandidate(report)),
  ],[isCandidate,pinnedIds,reports,remembered])
  const hasMore = reports.filter(isCandidate).length < totalCount

  return <fieldset className="problemReportPicker">
    <legend>{legend}</legend>
    <label className="problemReportSearch"><span>Find controlled PR</span><input type="search" value={query} onChange={event=>setQuery(event.target.value)} placeholder="Search PR number, title, problem, or root cause"/></label>
    {error && <span role="alert">{error}</span>}
    {busy&&<span role="status">Searching problem reports…</span>}
    {!busy&&!error&&!visible.length && <span>{query.trim()?'No problem reports match this search.':scope === 'target-build'?'No PRs are recorded for this build.':'No PRs are recorded for this Project.'}</span>}
    {visible.map(report => {
      const historical = !isCandidate(report)
      const isLocked = locked.includes(report.id)
      const explanation = historical
        ? isLocked
          ? 'Historical relationship: this PR is no longer targeted to this build.'
          : 'Stale relationship: this PR is no longer targeted to this build. Remove it before saving.'
        : ''
      return <label key={report.id} className={historical?'problemReportHistorical':''}>
        <input type="checkbox" checked={selected.includes(report.id)} disabled={isLocked || historical && !selected.includes(report.id)} onChange={() => toggle(report.id)} />
        <b>{report.displayNumber}</b><span>{report.title}{explanation&&<small>{explanation}</small>}</span><i>{report.state.replace(/([A-Z])/g, ' $1')}</i>
      </label>
    })}
    {hasMore&&<button type="button" className="problemReportMore" disabled={busy} onClick={()=>setPage(current=>current+1)}>Load more problem reports</button>}
  </fieldset>
}
