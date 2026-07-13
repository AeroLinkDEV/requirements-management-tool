import { useEffect, useMemo, useRef, useState } from 'react'
import type { RouteContext, View, Discipline } from './routing'
import { artifactPath, routePath } from './routing'
import './CommandPalette.css'

type Result={id:string;kind:string;identifier:string;title:string;state:string;discipline:string;updatedAt?:string}
type Props={api:string;context:RouteContext;open:boolean;onClose:()=>void;onNavigate:(view:View,discipline?:Discipline,artifactId?:string,artifactKind?:string)=>void}

export default function CommandPalette({api,context,open,onClose,onNavigate}:Props){
 const [query,setQuery]=useState(''),[results,setResults]=useState<Result[]>([]),[busy,setBusy]=useState(false)
 const input=useRef<HTMLInputElement>(null)
 const commands=useMemo(()=>[
  ['Command Center','dashboard','system'],['My Work','mywork','system'],['New System SCR','createSystemScr','system'],['New Software SWCR','createSoftwareChange','software'],
  ['System Requirements','requirements','system'],['Software Requirements','requirements','software'],['System Verification','verification','systemTest'],['Software Verification','verification','softwareTest'],
  ['Traceability','lifecycle','system'],['Release Planning','planning','system'],['Baselines','baselines','system'],['Release Campaign','release','system'],['Enterprise Control','enterprise','system']
 ] as [string,View,Discipline][],[])
 useEffect(()=>{if(open){setTimeout(()=>input.current?.focus(),0)}else{setQuery('');setResults([])}},[open])
 useEffect(()=>{if(!open||query.trim().length<2){setResults([]);return}const controller=new AbortController();const timer=setTimeout(async()=>{setBusy(true);try{const response=await fetch(`${api}/api/search?projectId=${context.projectId}&query=${encodeURIComponent(query.trim())}&limit=30`,{signal:controller.signal});if(response.ok)setResults((await response.json()).items)}finally{setBusy(false)}},180);return()=>{clearTimeout(timer);controller.abort()}},[api,context.projectId,open,query])
 if(!open)return null
 const matching=commands.filter(x=>x[0].toLowerCase().includes(query.toLowerCase()))
 return <div className="paletteBackdrop" onMouseDown={e=>{if(e.target===e.currentTarget)onClose()}} role="presentation"><section className="commandPalette" role="dialog" aria-modal="true" aria-label="Quick navigation"><header><span aria-hidden="true">⌕</span><input ref={input} value={query} onChange={e=>setQuery(e.target.value)} placeholder="Search pages, SCRs, requirements, tests, baselines, builds…" onKeyDown={e=>{if(e.key==='Escape')onClose()}}/><kbd>Esc</kbd></header><div className="paletteResults">
  {matching.length>0&&<div className="paletteGroup"><small>PAGES</small>{matching.map(([label,view,discipline])=><a key={label} href={routePath(context,view,discipline)} onClick={e=>{e.preventDefault();onNavigate(view,discipline);onClose()}}><i>↗</i><div><b>{label}</b><span>Open workspace</span></div></a>)}</div>}
  {query.trim().length>=2&&<div className="paletteGroup"><small>ARTIFACTS {busy?'· SEARCHING…':''}</small>{results.map(item=><a key={`${item.kind}-${item.id}`} href={artifactPath(context,item.kind,item.id,item.discipline)} onClick={e=>{e.preventDefault();if(item.kind==='change-request')onNavigate('scr','system',item.id);else if(item.kind==='requirement')onNavigate('requirements',item.discipline==='software'?'software':'system',item.id);else onNavigate('artifact','system',item.id,item.kind);onClose()}}><i className={`kind ${item.kind}`}>{item.kind.slice(0,2).toUpperCase()}</i><div><b>{item.identifier}</b><span>{item.title}</span></div><em>{item.state}</em></a>)}{!busy&&!results.length&&<p>No authorized artifacts match this search.</p>}</div>}
 </div><footer><span><kbd>Ctrl</kbd><kbd>K</kbd> open anywhere</span><span>Results respect Program authority</span></footer></section></div>
}
