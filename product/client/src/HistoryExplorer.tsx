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
type SoftwareLevel='HighLevel'|'LowLevel'
type Scr={id:string;baseNumber:string;revision:number;displayNumber:string;title:string;state:string;deferredFromState?:string|null;authorId:string;targetReleaseId:string;requirementCount:number;hasHighLevelChanges:boolean;hasLowLevelChanges:boolean;updatedAt:string;revisionCount:number}
type Props={
 api:string;projectId:string;releases:Release[];activeReleaseId:string;scope:Scope;
 initialSoftwareLevel:SoftwareLevel;onSoftwareLevelChange:(level:SoftwareLevel)=>void;
 initialStateIntent?:HistoryStateIntent;onStateIntentChange:(intent?:HistoryStateIntent)=>void;
 onBack:()=>void;onOpenScr:(id:string)=>void;onCreateSystem:()=>void;
 onCreateSoftware:(level:'HighLevel'|'LowLevel')=>void
 user:AuthUser
}

const stateLabels:Record<HistoryStateIntent,string>={Draft:'Draft',InReview:'In review',Approved:'Approved',SelectedForBaseline:'Allocated to a build',Deferred:'Deferred',ApprovedOrSelected:'Approved or allocated'}
const matchesStateIntent=(state:string,intent?:HistoryStateIntent)=>!intent||(intent==='ApprovedOrSelected'?(state==='Approved'||state==='SelectedForBaseline'):state===intent)

export default function HistoryExplorer({api,projectId,releases,activeReleaseId,scope,initialSoftwareLevel,onSoftwareLevelChange,initialStateIntent,onStateIntentChange,onBack,onOpenScr,onCreateSystem,onCreateSoftware,user}:Props){
 const [query,setQuery]=useState(''),[softwareLevel,setSoftwareLevel]=useState<SoftwareLevel>(initialSoftwareLevel),[stateIntent,setStateIntent]=useState<HistoryStateIntent|undefined>(initialStateIntent),[scrPage,setScrPage]=useState(1),[scrTotal,setScrTotal]=useState(0),[scrTotalPages,setScrTotalPages]=useState(1),[scrs,setScrs]=useState<Scr[]>([]),[error,setError]=useState('')
 const activeRelease=releases.find(x=>x.id===activeReleaseId)
 const load=useCallback(async()=>{const params=new URLSearchParams({projectId,page:String(scrPage),pageSize:'50',releaseId:activeReleaseId,type:scope});if(scope==='Software')params.set('level',softwareLevel);if(stateIntent)params.set('state',stateIntent);if(query)params.set('search',query)
  const response=await fetch(`${api}/api/history/scrs?${params}`)
  if(response.ok){const body=await response.json();setScrs(body.items);setScrTotal(body.totalCount);setScrTotalPages(Math.max(1,body.totalPages))}
 },[activeReleaseId,api,projectId,query,scope,scrPage,softwareLevel,stateIntent])
 useEffect(()=>{const timer=setTimeout(load,180);return()=>clearTimeout(timer)},[load])
 useEffect(()=>{setScrPage(1)},[activeReleaseId,scope])
 useEffect(()=>{setSoftwareLevel(initialSoftwareLevel);setScrPage(1)},[initialSoftwareLevel])
 useEffect(()=>{setStateIntent(initialStateIntent);setScrPage(1)},[initialStateIntent])
 const changeStateIntent=(intent?:HistoryStateIntent)=>{setStateIntent(intent);setScrPage(1);onStateIntentChange(intent)}
 const visibleScrs=scrs.filter(x=>matchesStateIntent(x.state,stateIntent))
 const emptyState=query
  ?<div className="historyEmpty">No {scope.toLowerCase()} change requests match “{query}”{stateIntent?` within the ${stateLabels[stateIntent].toLowerCase()} filter`:''} for Build {activeRelease?.version}. <button type="button" onClick={()=>{setQuery('');setScrPage(1)}}>Clear search</button></div>
  :stateIntent
   ?<div className="historyEmpty">No {stateLabels[stateIntent].toLowerCase()} {scope.toLowerCase()} change requests match Build {activeRelease?.version}. <button type="button" onClick={()=>changeStateIntent(undefined)}>Clear lifecycle filter</button></div>
   :<div className="historyEmpty">No {scope.toLowerCase()} change requests are recorded for Build {activeRelease?.version}.</div>
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
 const changeSoftwareLevel=(level:SoftwareLevel)=>{setSoftwareLevel(level);setScrPage(1);onSoftwareLevelChange(level)}
 return <main className="historyPage">
  <header className="historyHeader">
   <div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / BUILD {activeRelease?.version}</p><h1>{scope} Change Requests</h1><p>{activeRelease?.isReleased?`Released ${scope.toLowerCase()} change history owned by Build ${activeRelease.version}.`:`Active and deferred ${scope.toLowerCase()} change requests owned by Build ${activeRelease?.version}.`}</p></div>
   {!activeRelease?.isReleased&&(scope==='System'
    ?<button className="recordBuild" onClick={onCreateSystem}>+ New System Change Request</button>
    :<button className="recordBuild" onClick={()=>onCreateSoftware(softwareLevel)}>+ New {softwareLevel==='HighLevel'?'HLR':'LLR'} Change Request</button>)}
  </header>
  {scope==='Software'&&<nav className="softwareLevelTabs" aria-label="Software requirement level"><button type="button" aria-current={softwareLevel==='HighLevel'?'page':undefined} onClick={()=>changeSoftwareLevel('HighLevel')}><b>HLR</b><span>High-level requirements</span></button><button type="button" aria-current={softwareLevel==='LowLevel'?'page':undefined} onClick={()=>changeSoftwareLevel('LowLevel')}><b>LLR</b><span>Low-level requirements</span></button></nav>}
  {error&&<div className="workspaceError">{error}</div>}
  {scope==='Software'&&<DownstreamAssessmentQueue api={api} projectId={projectId} releaseId={activeReleaseId} targetLevel={softwareLevel} user={user} onOpenScr={onOpenScr}/>}
  <section className="historyTools"><div className="historyContext"><b>Build {activeRelease?.version}</b><span>{scope==='Software'?(softwareLevel==='HighLevel'?'HLR':'LLR'):'System'} area · {scrTotal} records</span></div><div className="historyFilters"><input aria-label="Search change requests" value={query} onChange={e=>{setQuery(e.target.value);setScrPage(1)}} placeholder="Search number, title, statement, rationale…"/><select aria-label="Lifecycle state filter" value={stateIntent??''} onChange={e=>changeStateIntent(e.target.value?e.target.value as HistoryStateIntent:undefined)}><option value="">All lifecycle states</option><option value="Draft">Draft</option><option value="InReview">In review</option><option value="Approved">Approved</option><option value="SelectedForBaseline">Allocated to a build</option><option value="Deferred">Deferred</option><option value="ApprovedOrSelected">Approved or allocated</option></select></div></section>
  {stateIntent&&<div className="historyActiveFilter" role="status"><div><span>ACTIVE FILTER</span><b>{stateLabels[stateIntent]}</b><small>{scrTotal} matching {scope.toLowerCase()} record{scrTotal===1?'':'s'} in Build {activeRelease?.version}</small></div><button type="button" onClick={()=>changeStateIntent(undefined)} aria-label={`Clear ${stateLabels[stateIntent]} lifecycle filter`}>Clear filter ×</button></div>}
  <section className="historyTable"><div className="tableHead allocation"><span>Change request revision</span><span>Build allocation</span><span>State</span><span>Last activity</span></div>{visibleScrs.map(x=>{
   const behind=expanded[x.baseNumber]
   return <div className="historyGroup" key={x.id}>
    <button className="historyRow allocation" onClick={()=>onOpenScr(x.id)}>
     <div><b>{x.displayNumber}</b>{x.hasHighLevelChanges&&x.hasLowLevelChanges&&<i className="mixedLevelBadge">HLR + LLR</i>}<p>{x.title}</p><small>{x.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(x.authorId)}</i>{identityLabel(x.authorId)}</span></small></div>
     <span className={x.state==='Deferred'?'allocationCell deferred':'allocationCell'}>{changeRequestAllocation(facts(x))}</span>
     <i className={`historyState ${x.state.toLowerCase()}`} data-state={x.state}>{changeRequestState(facts(x))}</i>
     <time>{new Date(x.updatedAt).toLocaleString()}</time>
    </button>
    {x.revisionCount>1&&<button type="button" className="revisionToggle" aria-expanded={Boolean(behind)} onClick={()=>toggleRevisions(x)}>{behind?'Hide':'Show'} {x.revisionCount-1} superseded revision{x.revisionCount-1===1?'':'s'}</button>}
    {behind==='loading'&&<div className="revisionHistory"><span>Loading earlier revisions…</span></div>}
    {Array.isArray(behind)&&<div className="revisionHistory">{behind.map(prior=><button className="historyRow allocation superseded" key={prior.id} onClick={()=>onOpenScr(prior.id)}><div><b>{prior.displayNumber}</b><p>{prior.title}</p><small>{prior.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(prior.authorId)}</i>{identityLabel(prior.authorId)}</span></small></div><span className="allocationCell">{changeRequestAllocation(facts(prior))}</span><i className="historyState superseded" data-state="Superseded">{changeRequestState(facts(prior,true))}</i><time>{new Date(prior.updatedAt).toLocaleString()}</time></button>)}</div>}
   </div>
  })}{!visibleScrs.length&&emptyState}{scrTotalPages>1&&<div className="historyPager"><button type="button" disabled={scrPage<=1} onClick={()=>setScrPage(page=>Math.max(1,page-1))}>← Previous</button><span>Page {scrPage} of {scrTotalPages} · {scrTotal} records</span><button type="button" disabled={scrPage>=scrTotalPages} onClick={()=>setScrPage(page=>Math.min(scrTotalPages,page+1))}>Next →</button></div>}</section>
 </main>
}
