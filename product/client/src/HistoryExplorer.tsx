import { useCallback, useEffect, useState } from 'react'
import { changeRequestAllocation, changeRequestState } from './presentation'
import { identityInitials, identityLabel } from './presentation'
import type { HistoryStateIntent } from './routing'
import type { AuthUser } from './IdentityCenter'
import DownstreamAssessmentQueue from './DownstreamAssessmentQueue'
import './HistoryExplorer.css'
import './Swrd.css'

type Release={id:string;version:string;isReleased:boolean}
type Scope='System'|'Software'
type Scr={id:string;baseNumber:string;revision:number;displayNumber:string;title:string;state:string;deferredFromState?:string|null;authorId:string;targetReleaseId:string;requirementCount:number;updatedAt:string;revisionCount:number}
type Props={
 api:string;projectId:string;releases:Release[];activeReleaseId:string;scope:Scope;
 initialStateIntent?:HistoryStateIntent;onStateIntentChange:(intent?:HistoryStateIntent)=>void;
 onBack:()=>void;onOpenScr:(id:string)=>void;onCreateSystem:()=>void;
 onCreateSoftware:(level:'HighLevel'|'LowLevel')=>void
 user:AuthUser
}

const stateLabels:Record<HistoryStateIntent,string>={Draft:'Draft',InReview:'In review',Approved:'Approved',SelectedForBaseline:'Allocated to a build',Deferred:'Deferred',ApprovedOrSelected:'Approved or allocated'}
const matchesStateIntent=(state:string,intent?:HistoryStateIntent)=>!intent||(intent==='ApprovedOrSelected'?(state==='Approved'||state==='SelectedForBaseline'):state===intent)

export default function HistoryExplorer({api,projectId,releases,activeReleaseId,scope,initialStateIntent,onStateIntentChange,onBack,onOpenScr,onCreateSystem,onCreateSoftware,user}:Props){
 const [query,setQuery]=useState(''),[stateIntent,setStateIntent]=useState<HistoryStateIntent|undefined>(initialStateIntent),[scrPage,setScrPage]=useState(1),[scrTotal,setScrTotal]=useState(0),[scrTotalPages,setScrTotalPages]=useState(1),[scrs,setScrs]=useState<Scr[]>([]),[softwareChoice,setSoftwareChoice]=useState(false),[error,setError]=useState('')
 const activeRelease=releases.find(x=>x.id===activeReleaseId)
 const load=useCallback(async()=>{const params=new URLSearchParams({projectId,page:String(scrPage),pageSize:'50',releaseId:activeReleaseId,type:scope});if(stateIntent)params.set('state',stateIntent);if(query)params.set('search',query)
  const response=await fetch(`${api}/api/history/scrs?${params}`)
  if(response.ok){const body=await response.json();setScrs(body.items);setScrTotal(body.totalCount);setScrTotalPages(Math.max(1,body.totalPages))}
 },[activeReleaseId,api,projectId,query,scope,scrPage,stateIntent])
 useEffect(()=>{const timer=setTimeout(load,180);return()=>clearTimeout(timer)},[load])
 useEffect(()=>{setScrPage(1);setSoftwareChoice(false)},[activeReleaseId,scope])
 useEffect(()=>{setStateIntent(initialStateIntent);setScrPage(1)},[initialStateIntent])
 const changeStateIntent=(intent?:HistoryStateIntent)=>{setStateIntent(intent);setScrPage(1);onStateIntentChange(intent)}
 const visibleScrs=scrs.filter(x=>matchesStateIntent(x.state,stateIntent))
 const [expanded,setExpanded]=useState<Record<string,Scr[]|'loading'>>({})
 const toggleRevisions=async(row:Scr)=>{
  if(expanded[row.baseNumber]){setExpanded(current=>{const next={...current};delete next[row.baseNumber];return next});return}
  setExpanded(current=>({...current,[row.baseNumber]:'loading'}))
  const params=new URLSearchParams({projectId,baseNumber:row.baseNumber,page:'1',pageSize:'50'})
  const response=await fetch(`${api}/api/history/scrs?${params}`)
  if(!response.ok){setExpanded(current=>{const next={...current};delete next[row.baseNumber];return next});setError('The earlier revisions could not be loaded.');return}
  const body=await response.json() as {items:Scr[]}
  setExpanded(current=>({...current,[row.baseNumber]:body.items.filter(x=>x.revision<row.revision).sort((a,b)=>b.revision-a.revision)}))
 }
 const facts=(x:Scr,superseded=false)=>({state:x.state,deferredFromState:x.deferredFromState,targetRelease:releases.find(r=>r.id===x.targetReleaseId),superseded})
 const startSoftware=(level:'HighLevel'|'LowLevel')=>{setSoftwareChoice(false);onCreateSoftware(level)}
 return <main className="historyPage">
  <header className="historyHeader">
   <div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / BUILD {activeRelease?.version}</p><h1>{scope} Change Requests</h1><p>{activeRelease?.isReleased?`Released ${scope.toLowerCase()} change history owned by Build ${activeRelease.version}.`:`Active and deferred ${scope.toLowerCase()} change requests owned by Build ${activeRelease?.version}.`}</p></div>
   {!activeRelease?.isReleased&&(scope==='System'
    ?<button className="recordBuild" onClick={onCreateSystem}>+ New System Change Request</button>
    :<button className="recordBuild" aria-expanded={softwareChoice} onClick={()=>setSoftwareChoice(value=>!value)}>+ New Software Change Request</button>)}
  </header>
  {softwareChoice&&<section className="softwareChangeChoice" aria-label="Choose software requirement level"><div><span>NEW SOFTWARE CHANGE REQUEST</span><h2>Which software level is changing?</h2><p>The selected level keeps this change request focused while it moves through review.</p></div><button onClick={()=>startSoftware('HighLevel')}><b>HLR change request</b><span>High-level software behavior</span></button><button onClick={()=>startSoftware('LowLevel')}><b>LLR change request</b><span>Low-level implementation behavior</span></button><button className="choiceCancel" onClick={()=>setSoftwareChoice(false)}>Cancel</button></section>}
  {error&&<div className="workspaceError">{error}</div>}
  {scope==='Software'&&<DownstreamAssessmentQueue api={api} projectId={projectId} releaseId={activeReleaseId} user={user} onOpenScr={onOpenScr}/>}
  <section className="historyTools"><div className="historyContext"><b>Build {activeRelease?.version}</b><span>{scope} area</span></div><div className="historyFilters"><input aria-label="Search change requests" value={query} onChange={e=>{setQuery(e.target.value);setScrPage(1)}} placeholder="Search number, title, statement, rationale…"/><select aria-label="Lifecycle state filter" value={stateIntent??''} onChange={e=>changeStateIntent(e.target.value?e.target.value as HistoryStateIntent:undefined)}><option value="">All lifecycle states</option><option value="Draft">Draft</option><option value="InReview">In review</option><option value="Approved">Approved</option><option value="SelectedForBaseline">Allocated to a build</option><option value="Deferred">Deferred</option><option value="ApprovedOrSelected">Approved or allocated</option></select></div></section>
  {stateIntent&&<div className="historyActiveFilter" role="status"><div><span>ACTIVE FILTER</span><b>{stateLabels[stateIntent]}</b><small>{scrTotal} matching {scope.toLowerCase()} record{scrTotal===1?'':'s'} in Build {activeRelease?.version}</small></div><button type="button" onClick={()=>changeStateIntent(undefined)} aria-label={`Clear ${stateLabels[stateIntent]} lifecycle filter`}>Clear filter ×</button></div>}
  <section className="historyTable"><div className="tableHead allocation"><span>Change request revision</span><span>Build allocation</span><span>State</span><span>Last activity</span></div>{visibleScrs.map(x=>{
   const behind=expanded[x.baseNumber]
   return <div className="historyGroup" key={x.id}>
    <button className="historyRow allocation" onClick={()=>onOpenScr(x.id)}>
     <div><b>{x.displayNumber}</b><p>{x.title}</p><small>{x.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(x.authorId)}</i>{identityLabel(x.authorId)}</span></small></div>
     <span className={x.state==='Deferred'?'allocationCell deferred':'allocationCell'}>{changeRequestAllocation(facts(x))}</span>
     <i className={`historyState ${x.state.toLowerCase()}`} data-state={x.state}>{changeRequestState(facts(x))}</i>
     <time>{new Date(x.updatedAt).toLocaleString()}</time>
    </button>
    {x.revisionCount>1&&<button type="button" className="revisionToggle" aria-expanded={Boolean(behind)} onClick={()=>toggleRevisions(x)}>{behind?'Hide':'Show'} {x.revisionCount-1} superseded revision{x.revisionCount-1===1?'':'s'}</button>}
    {behind==='loading'&&<div className="revisionHistory"><span>Loading earlier revisions…</span></div>}
    {Array.isArray(behind)&&<div className="revisionHistory">{behind.map(prior=><button className="historyRow allocation superseded" key={prior.id} onClick={()=>onOpenScr(prior.id)}><div><b>{prior.displayNumber}</b><p>{prior.title}</p><small>{prior.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(prior.authorId)}</i>{identityLabel(prior.authorId)}</span></small></div><span className="allocationCell">{changeRequestAllocation(facts(prior))}</span><i className="historyState superseded" data-state="Superseded">{changeRequestState(facts(prior,true))}</i><time>{new Date(prior.updatedAt).toLocaleString()}</time></button>)}</div>}
   </div>
  })}{!visibleScrs.length&&<div className="historyEmpty">No {stateIntent?stateLabels[stateIntent].toLowerCase()+' ':''}{scope.toLowerCase()} change requests are recorded for Build {activeRelease?.version}.</div>}{scrTotalPages>1&&<div className="historyPager"><button type="button" disabled={scrPage<=1} onClick={()=>setScrPage(page=>Math.max(1,page-1))}>← Previous</button><span>Page {scrPage} of {scrTotalPages} · {scrTotal} records</span><button type="button" disabled={scrPage>=scrTotalPages} onClick={()=>setScrPage(page=>Math.min(scrTotalPages,page+1))}>Next →</button></div>}</section>
 </main>
}
