import { useEffect, useMemo, useRef, useState } from 'react'
import { stateLabel } from './presentation'
import type { RouteContext, View, Discipline } from './routing'
import { artifactPath, routePath } from './routing'
import './CommandPalette.css'

type Result={id:string;kind:string;identifier:string;title:string;state:string;discipline:string;updatedAt?:string}
type Props={api:string;context:RouteContext;open:boolean;onClose:()=>void;onNavigate:(view:View,discipline?:Discipline,artifactId?:string,artifactKind?:string)=>void}
type PaletteEntry={key:string;category:'page'|'artifact';label:string;detail:string;state?:string;view:View;discipline:Discipline;artifactId?:string;artifactKind?:string;icon:string;updatedAt?:string}

const commandDefinitions:{label:string;view:View;discipline:Discipline;detail:string;icon:string}[]=[
  {label:'Command Center',view:'dashboard',discipline:'system',detail:'Program health and next actions',icon:'⌂'},
  {label:'My Work',view:'mywork',discipline:'system',detail:'Reviews, signatures, and owned drafts',icon:'◎'},
  {label:'New System SCR',view:'createSystemScr',discipline:'system',detail:'Start a governed system change',icon:'+'},
  {label:'New Software SWCR',view:'createSoftwareChange',discipline:'software',detail:'Start a governed software change',icon:'+'},
  {label:'System Requirements Explorer',view:'requirements',discipline:'system',detail:'Read, trace, and compare controlled system requirements',icon:'≡'},
  {label:'Software Requirements Explorer',view:'requirements',discipline:'software',detail:'Read, trace, and compare controlled HLRs and LLRs',icon:'≡'},
  {label:'System Verification',view:'verification',discipline:'systemTest',detail:'Coverage, procedures, and immutable results',icon:'✓'},
  {label:'Software Verification',view:'verification',discipline:'softwareTest',detail:'Software coverage and test evidence',icon:'✓'},
  {label:'Digital Thread',view:'lifecycle',discipline:'system',detail:'Traceability and outputs across the released evidence path',icon:'↗'},
  {label:'Product Versions',view:'planning',discipline:'system',detail:'Plan versions and release lineage',icon:'②'},
  {label:'Baselines',view:'baselines',discipline:'system',detail:'Candidate and frozen controlled sets',icon:'⌘'},
  {label:'Lifecycle Decision Room',view:'release',discipline:'system',detail:'Release readiness, change impact, evidence, and authority',icon:'◆'},
  {label:'System Operations',view:'enterprise',discipline:'system',detail:'Operational controls and integrity',icon:'◈'},
]

const commandEntries:PaletteEntry[]=commandDefinitions.map(item=>({...item,key:`page-${item.view}-${item.discipline}`,category:'page'}))
const recentKey='aerolink-recent-destinations'
const readRecent=():PaletteEntry[]=>{try{return JSON.parse(localStorage.getItem(recentKey)??'[]').slice(0,5)}catch{return[]}}

export default function CommandPalette({api,context,open,onClose,onNavigate}:Props){
  const [query,setQuery]=useState(''),[results,setResults]=useState<Result[]>([]),[busy,setBusy]=useState(false),[activeIndex,setActiveIndex]=useState(0),[recent,setRecent]=useState<PaletteEntry[]>([])
  const input=useRef<HTMLInputElement>(null)
  useEffect(()=>{if(open){setRecent(readRecent());setTimeout(()=>input.current?.focus(),0)}else{setQuery('');setResults([]);setActiveIndex(0)}},[open])
  useEffect(()=>{if(!open||query.trim().length<2){setResults([]);setBusy(false);return}const controller=new AbortController();const timer=setTimeout(async()=>{setBusy(true);try{const response=await fetch(`${api}/api/search?projectId=${context.projectId}&releaseId=${context.releaseId}&query=${encodeURIComponent(query.trim())}&limit=30`,{signal:controller.signal});if(response.ok)setResults((await response.json()).items)}catch(error){if((error as Error).name!=='AbortError')setResults([])}finally{if(!controller.signal.aborted)setBusy(false)}},180);return()=>{clearTimeout(timer);controller.abort()}},[api,context.projectId,context.releaseId,open,query])
  const pageEntries=useMemo(()=>commandEntries.filter(item=>item.label.toLowerCase().includes(query.trim().toLowerCase())||item.detail.toLowerCase().includes(query.trim().toLowerCase())),[query])
  const artifactEntries=useMemo<PaletteEntry[]>(()=>results.map(item=>({key:`artifact-${item.kind}-${item.id}`,category:'artifact',label:item.identifier,detail:item.title,state:item.state,view:item.kind==='change-request'?'scr':item.kind==='requirement'?'requirements':'artifact',discipline:(item.kind==='change-request'||item.kind==='requirement')&&item.discipline==='software'?'software':'system',artifactId:item.id,artifactKind:item.kind,icon:item.kind.slice(0,2).toUpperCase(),updatedAt:item.updatedAt})),[results])
  const showingRecent=query.trim()===''&&recent.length>0
  const visiblePages=query.trim()===''?(showingRecent?commandEntries.slice(0,6):commandEntries.slice(0,8)):pageEntries
  const entries=showingRecent?[...recent,...visiblePages.filter(page=>!recent.some(item=>item.key===page.key))]:[...visiblePages,...artifactEntries]
  const active=entries[Math.min(activeIndex,Math.max(0,entries.length-1))]
  useEffect(()=>setActiveIndex(0),[query,results])
  if(!open)return null
  const remember=(entry:PaletteEntry)=>{const next=[entry,...readRecent().filter(item=>item.key!==entry.key)].slice(0,5);localStorage.setItem(recentKey,JSON.stringify(next));setRecent(next)}
  const openEntry=(entry:PaletteEntry)=>{remember(entry);onNavigate(entry.view,entry.discipline,entry.artifactId,entry.artifactKind);onClose()}
  const keyDown=(event:React.KeyboardEvent<HTMLInputElement>)=>{if(event.key==='Escape'){onClose();return}if(event.key==='ArrowDown'){event.preventDefault();setActiveIndex(index=>(index+1)%Math.max(entries.length,1))}if(event.key==='ArrowUp'){event.preventDefault();setActiveIndex(index=>(index-1+Math.max(entries.length,1))%Math.max(entries.length,1))}if(event.key==='Enter'&&active){event.preventDefault();openEntry(active)}}
  const render=(entry:PaletteEntry,index:number)=><a key={entry.key} href={entry.category==='artifact'?artifactPath(context,entry.artifactKind!,entry.artifactId!,entry.discipline):routePath(context,entry.view,entry.discipline)} data-active={index===activeIndex} aria-current={index===activeIndex?'true':undefined} onMouseEnter={()=>setActiveIndex(index)} onFocus={()=>setActiveIndex(index)} onClick={event=>{event.preventDefault();openEntry(entry)}}><i className={entry.category==='artifact'?'kind':''}>{entry.icon}</i><div><b>{entry.label}</b><span>{entry.detail}</span></div>{entry.state&&<em>{stateLabel(entry.state)}</em>}</a>
  let cursor=0
  return <div className="paletteBackdrop" onMouseDown={event=>{if(event.target===event.currentTarget)onClose()}} role="presentation"><section className="commandPalette" role="dialog" aria-modal="true" aria-label="Quick navigation"><header><span aria-hidden="true">⌕</span><input ref={input} value={query} onChange={event=>setQuery(event.target.value)} placeholder="Search pages, SCRs, requirements, tests, baselines, builds…" onKeyDown={keyDown} aria-label="Search AeroLink"/><kbd>Esc</kbd></header><div className="paletteWorkspace"><div className="paletteResults">
    {showingRecent&&<div className="paletteGroup"><small>RECENT DESTINATIONS</small>{recent.map(entry=>render(entry,cursor++))}</div>}
    {visiblePages.length>0&&<div className="paletteGroup"><small>{query.trim()?'PAGES':'SUGGESTED WORKSPACES'}</small>{visiblePages.filter(page=>!showingRecent||!recent.some(item=>item.key===page.key)).map(entry=>render(entry,cursor++))}</div>}
    {query.trim().length>=2&&<div className="paletteGroup"><small>AUTHORIZED ARTIFACTS {busy?'· SEARCHING…':''}</small>{artifactEntries.map(entry=>render(entry,cursor++))}{!busy&&!artifactEntries.length&&<div className="paletteZero"><span>⌕</span><b>No matching controlled records</b><p>Try an identifier, title, or statement fragment. Results always respect your Program authority.</p></div>}</div>}
  </div><aside className="palettePreview">{active?<><span className="previewIcon">{active.icon}</span><p>{active.category==='artifact'?'CONTROLLED ARTIFACT':'WORKSPACE'}</p><h2>{active.label}</h2><div className="previewState">{active.state??'Ready to open'}</div><p className="previewDetail">{active.detail}</p><dl><div><dt>Scope</dt><dd>{active.discipline.replace('Test',' verification')}</dd></div><div><dt>Access</dt><dd>Current Program authority</dd></div>{active.updatedAt&&<div><dt>Updated</dt><dd>{new Date(active.updatedAt).toLocaleString()}</dd></div>}</dl><footer><kbd>Enter</kbd><span>Open selection</span></footer></>:<><span className="previewIcon">⌕</span><h2>Search AeroLink</h2><p className="previewDetail">Jump directly to an authorized workspace or controlled artifact without losing your active release context.</p></>}</aside></div><footer><span><kbd>↑</kbd><kbd>↓</kbd> select <kbd>Enter</kbd> open</span><span>Results respect Program authority</span></footer></section></div>
}
