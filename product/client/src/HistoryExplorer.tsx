import { useCallback, useEffect, useState } from 'react'
// stateLabel is still used, for a software build's state — a different kind of state, so it keeps the plain word.
import { changeRequestAllocation, changeRequestState, stateLabel } from './presentation'
import { PersonName } from './People'
import type { FormEvent } from 'react'
import type { HistoryStateIntent, HistoryTypeIntent } from './routing'
import { identityInitials, identityLabel } from './presentation'
import './HistoryExplorer.css'
import './Swrd.css'

type Release={id:string;version:string;isReleased:boolean}
type Scr={id:string;baseNumber:string;revision:number;displayNumber:string;title:string;state:string;deferredFromState?:string|null;authorId:string;targetReleaseId:string;requirementCount:number;updatedAt:string;revisionCount:number}
type Build={id:string;buildNumber:string;description:string;state:string;recordedBy:string;recordedAt:string;releaseId:string;version:string;baselineId:string;baselineDisplayNumber:string;contentHash:string;scrCount:number}
type Baseline={id:string;displayNumber:string;name:string;state:string;contentHash?:string;selectionCount:number;releaseId:string}
type BuildDetail=Build&{baseline:{id:string;displayNumber:string;name:string;contentHash:string;frozenAt:string};effectiveRequirements:{id:string;displayNumber:string;level:string;statement:string;verificationMethod:string}[];scrs:{id:string;displayNumber:string;title:string;state:string;requirements:{id:string;displayNumber:string;level:string;kind:string;statement:string}[]}[]}
type Props={api:string;projectId:string;releases:Release[];activeReleaseId:string;scope:HistoryTypeIntent;initialStateIntent?:HistoryStateIntent;onStateIntentChange:(intent?:HistoryStateIntent)=>void;onTypeIntentChange:(intent:HistoryTypeIntent)=>void;onBack:()=>void;onOpenScr:(id:string)=>void}

const stateLabels:Record<HistoryStateIntent,string>={Draft:'Draft',InReview:'In review',Approved:'Approved',SelectedForBaseline:'Allocated to a build',Deferred:'Deferred',ApprovedOrSelected:'Approved or allocated'}
// One place decides how a change request's state reads, and it is `changeRequestStateLabel`. This file used to
// keep its own map saying "Baseline selected", so the same record read one way here and another on the Command
// Center — and neither told the reader which build it was going into or whether that build had shipped.
const matchesStateIntent=(state:string,intent?:HistoryStateIntent)=>!intent||(intent==='ApprovedOrSelected'?(state==='Approved'||state==='SelectedForBaseline'):state===intent)

export default function HistoryExplorer({api,projectId,releases,activeReleaseId,scope,initialStateIntent,onStateIntentChange,onTypeIntentChange,onBack,onOpenScr}:Props){
 const [tab,setTab]=useState<'scrs'|'builds'>('scrs'),[query,setQuery]=useState(''),[releaseId,setReleaseId]=useState(activeReleaseId),[stateIntent,setStateIntent]=useState<HistoryStateIntent|undefined>(initialStateIntent),[scrPage,setScrPage]=useState(1),[scrTotal,setScrTotal]=useState(0),[scrTotalPages,setScrTotalPages]=useState(1),[scrs,setScrs]=useState<Scr[]>([]),[builds,setBuilds]=useState<Build[]>([]),[baselines,setBaselines]=useState<Baseline[]>([]),[selectedBuild,setSelectedBuild]=useState<BuildDetail>(),[showCreate,setShowCreate]=useState(false),[error,setError]=useState('')
 const releaseName=(id:string)=>releases.find(x=>x.id===id)?.version??'Unknown'
 // There was a Requirement History tab here, and it was empty for everyone. It asked for requirements *as of
 // the selected release*, and the page opens on the in-work build, which has no materialized baseline yet — so
 // it answered 0 rows while the released build held 1,703. Not broken, just answering a question nobody asked:
 // a requirement's own revision history is richer and is reached from the requirement itself. Removed rather
 // than made to fall back to the released build, which would have shown content the filter said was excluded.
 const load=useCallback(async()=>{const params=new URLSearchParams({projectId,page:String(scrPage),pageSize:'50'});if(scope!=='All')params.set('type',scope);if(stateIntent)params.set('state',stateIntent);if(query)params.set('search',query);if(releaseId)params.set('releaseId',releaseId)
  const [a,c]=await Promise.all([fetch(`${api}/api/history/scrs?${params}`),fetch(`${api}/api/builds?projectId=${projectId}&search=${encodeURIComponent(query)}`)])
  if(a.ok){const body=await a.json();setScrs(body.items);setScrTotal(body.totalCount);setScrTotalPages(Math.max(1,body.totalPages))}if(c.ok)setBuilds(await c.json())},[api,projectId,query,releaseId,scope,scrPage,stateIntent])
 useEffect(()=>{const timer=setTimeout(load,180);return()=>clearTimeout(timer)},[load])
 useEffect(()=>{setReleaseId(activeReleaseId);setScrPage(1)},[activeReleaseId])
 useEffect(()=>{setStateIntent(initialStateIntent);setScrPage(1)},[initialStateIntent])
 const changeStateIntent=(intent?:HistoryStateIntent)=>{setStateIntent(intent);setScrPage(1);onStateIntentChange(intent)}
 const changeTab=(next:'scrs'|'builds')=>{setTab(next);if(next!=='scrs'&&stateIntent)changeStateIntent(undefined)}
 const visibleScrs=scrs.filter(x=>matchesStateIntent(x.state,stateIntent))
 // Which collapsed rows the reader has opened, and the earlier revisions behind them. Fetched per change request
 // on expand rather than up front: almost no change request has more than one revision, and loading every
 // history to keep a handful of them ready is a request made to be discarded.
 const [expanded,setExpanded]=useState<Record<string,Scr[]|'loading'>>({})
 const toggleRevisions=async(row:Scr)=>{
  if(expanded[row.baseNumber]){setExpanded(current=>{const next={...current};delete next[row.baseNumber];return next});return}
  setExpanded(current=>({...current,[row.baseNumber]:'loading'}))
  const params=new URLSearchParams({projectId,baseNumber:row.baseNumber,page:'1',pageSize:'50'})
  const response=await fetch(`${api}/api/history/scrs?${params}`)
  if(!response.ok){setExpanded(current=>{const next={...current};delete next[row.baseNumber];return next});setError('The earlier revisions could not be loaded.');return}
  const body=await response.json() as {items:Scr[]}
  // The newest revision is already the row that was clicked, so only what came before it is added underneath.
  setExpanded(current=>({...current,[row.baseNumber]:body.items.filter(x=>x.revision<row.revision).sort((a,b)=>b.revision-a.revision)}))
 }
 const facts=(x:Scr,superseded=false)=>({state:x.state,deferredFromState:x.deferredFromState,targetRelease:releases.find(r=>r.id===x.targetReleaseId),superseded})
 const prepareCreate=async()=>{const lists=await Promise.all(releases.map(async r=>(await fetch(`${api}/api/baselines?projectId=${projectId}&releaseId=${r.id}`).then(x=>x.json())).map((x:Baseline)=>({...x,releaseId:r.id}))));setBaselines(lists.flat().filter((x:Baseline)=>x.state==='Frozen'));setShowCreate(true)}
 const createBuild=async(e:FormEvent<HTMLFormElement>)=>{e.preventDefault();const form=new FormData(e.currentTarget),baseline=baselines.find(x=>x.id===form.get('baselineId'));if(!baseline)return;const response=await fetch(`${api}/api/builds`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId,releaseId:baseline.releaseId,baselineId:baseline.id,buildNumber:form.get('buildNumber'),description:form.get('description'),recordedBy:form.get('recordedBy')})});if(!response.ok){setError((await response.json()).error||'Build could not be recorded.');return}setShowCreate(false);await load();setTab('builds')}
 const openBuild=async(id:string)=>{const response=await fetch(`${api}/api/builds/${id}`);if(response.ok)setSelectedBuild(await response.json())}
 return <main className="historyPage"><header className="historyHeader"><div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / COMPLETE HISTORY</p><h1>{scope} Change Requests</h1><p>Find controlled change packages and inspect their exact revision, release, and build provenance.</p></div>{tab==='builds'&&<button className="recordBuild" onClick={prepareCreate}>+ Record Software Build</button>}</header>
 {error&&<div className="workspaceError">{error}</div>}{showCreate&&<form className="buildForm" onSubmit={createBuild}><div><h2>Record immutable build provenance</h2><p>A build points to one exact frozen baseline. Its change and requirement contents remain reproducible.</p></div><label>Build number<input name="buildNumber" placeholder="e.g. PRODUCT-3.3.0-rc1" required/></label><label>Frozen baseline<select name="baselineId" required><option value="">Select exact manifest…</option>{baselines.map(b=><option value={b.id} key={b.id}>{releaseName(b.releaseId)} · {b.displayNumber} · {b.name} · {b.selectionCount} changes</option>)}</select></label><label>Authority<input value="Authenticated configuration manager" readOnly/><input type="hidden" name="recordedBy" value="server-derived"/></label><label className="wide">Description<input name="description" placeholder="Purpose, configuration, or build notes"/></label><div><button type="button" className="outline" onClick={()=>setShowCreate(false)}>Cancel</button><button>Record Build</button></div></form>}
 <section className="historyTools"><div className="historyTabs"><button className={tab==='scrs'?'active':''} onClick={()=>changeTab('scrs')}>SCR History <span>{scrTotal}</span></button><button className={tab==='builds'?'active':''} onClick={()=>changeTab('builds')}>Software Builds <span>{builds.length}</span></button></div><div className="historyFilters"><input aria-label="Search history" value={query} onChange={e=>{setQuery(e.target.value);setScrPage(1)}} placeholder="Search number, title, statement, rationale…"/><select aria-label="Release filter" value={releaseId} onChange={e=>{setReleaseId(e.target.value);setScrPage(1)}}><option value="">All releases</option>{releases.map(r=><option value={r.id} key={r.id}>Release {r.version}</option>)}</select>{tab==='scrs'&&<><select aria-label="Change request type filter" value={scope} onChange={e=>{setScrPage(1);onTypeIntentChange(e.target.value as HistoryTypeIntent)}}><option value="All">System and software</option><option value="System">System SCRs</option><option value="Software">Software SWCRs</option></select><select aria-label="Lifecycle state filter" value={stateIntent??''} onChange={e=>changeStateIntent(e.target.value?e.target.value as HistoryStateIntent:undefined)}><option value="">All lifecycle states</option><option value="Draft">Draft</option><option value="InReview">In review</option><option value="Approved">Approved</option><option value="SelectedForBaseline">Allocated to a build</option><option value="Deferred">Deferred — put away for later</option><option value="ApprovedOrSelected">Approved or allocated</option></select></>}</div></section>
 {tab==='scrs'&&(stateIntent||scope==='All')&&<div className="historyActiveFilter" role="status"><div><span>DASHBOARD DRILL-DOWN</span><b>{stateIntent?stateLabels[stateIntent]:'All lifecycle states'} · {scope==='All'?'System and software':scope}</b><small>{scrTotal} matching record{scrTotal===1?'':'s'} in the selected release</small></div>{stateIntent&&<button type="button" onClick={()=>changeStateIntent(undefined)} aria-label={`Clear ${stateLabels[stateIntent]} lifecycle filter`}>Clear filter ×</button>}</div>}
 {/* Allocation and state read as separate columns, because they are separate questions: which build is this
     going into, and how far has it got. One stored value used to answer both badly. */}
 {tab==='scrs'&&<section className="historyTable"><div className="tableHead allocation"><span>SCR revision</span><span>Allocation</span><span>State</span><span>Last activity</span></div>{visibleScrs.map(x=>{
  const behind=expanded[x.baseNumber]
  return <div className="historyGroup" key={x.id}>
   <button className="historyRow allocation" onClick={()=>onOpenScr(x.id)}>
    <div><b>{x.displayNumber}</b><p>{x.title}</p><small>{x.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(x.authorId)}</i>{identityLabel(x.authorId)}</span></small></div>
    <span className={x.state==='Deferred'?'allocationCell deferred':'allocationCell'}>{changeRequestAllocation(facts(x))}</span>
    <i className={`historyState ${x.state.toLowerCase()}`} data-state={x.state}>{changeRequestState(facts(x))}</i>
    <time>{new Date(x.updatedAt).toLocaleString()}</time>
   </button>
   {/* Collapsed by default and never hidden: the row says how many revisions exist and opens to show them.
       A superseded revision is the same work read at an earlier moment, so it belongs underneath its
       successor rather than beside it in the main list. */}
   {x.revisionCount>1&&<button type="button" className="revisionToggle" aria-expanded={Boolean(behind)} onClick={()=>toggleRevisions(x)}>
    {behind?'Hide':'Show'} {x.revisionCount-1} superseded revision{x.revisionCount-1===1?'':'s'}
   </button>}
   {behind==='loading'&&<div className="revisionHistory"><span>Loading earlier revisions…</span></div>}
   {Array.isArray(behind)&&<div className="revisionHistory">{behind.map(prior=><button className="historyRow allocation superseded" key={prior.id} onClick={()=>onOpenScr(prior.id)}>
    <div><b>{prior.displayNumber}</b><p>{prior.title}</p><small>{prior.requirementCount} requirement changes · <span className="personMeta"><i>{identityInitials(prior.authorId)}</i>{identityLabel(prior.authorId)}</span></small></div>
    <span className="allocationCell">{changeRequestAllocation(facts(prior))}</span>
    <i className="historyState superseded" data-state="Superseded">{changeRequestState(facts(prior,true))}</i>
    <time>{new Date(prior.updatedAt).toLocaleString()}</time>
   </button>)}</div>}
  </div>
 })}{!visibleScrs.length&&<div className="historyEmpty">No {stateIntent?stateLabels[stateIntent].toLowerCase()+' ':''}{scope==='All'?'system or software':scope.toLowerCase()} change requests match these filters.</div>}{scrTotalPages>1&&<div className="historyPager"><button type="button" disabled={scrPage<=1} onClick={()=>setScrPage(page=>Math.max(1,page-1))}>← Previous</button><span>Page {scrPage} of {scrTotalPages} · {scrTotal} records</span><button type="button" disabled={scrPage>=scrTotalPages} onClick={()=>setScrPage(page=>Math.min(scrTotalPages,page+1))}>Next →</button></div>}</section>}
 {tab==='builds'&&<div className="buildGrid"><section className="buildList">{builds.map(x=><button className={selectedBuild?.id===x.id?'active':''} onClick={()=>openBuild(x.id)} key={x.id}><div><b>{x.buildNumber}</b><i>{stateLabel(x.state)}</i></div><p>Software {x.version} · {x.baselineDisplayNumber}</p><small>{x.scrCount} exact SCR revisions · {new Date(x.recordedAt).toLocaleString()}</small></button>)}{!builds.length&&<div className="historyEmpty">No software builds have been recorded yet.</div>}</section><section className="buildDetail">{selectedBuild?<><div className="buildHero"><span>SOFTWARE BUILD PROVENANCE</span><h2>{selectedBuild.buildNumber}</h2><p>{selectedBuild.description||'No build notes recorded.'}</p><dl><div><dt>Release</dt><dd>{releaseName(selectedBuild.releaseId)}</dd></div><div><dt>Frozen baseline</dt><dd>{selectedBuild.baseline.displayNumber}</dd></div><div><dt>Recorded by</dt><dd><PersonName userName={selectedBuild.recordedBy} /></dd></div><div><dt>Exact SCRs</dt><dd>{selectedBuild.scrs.length}</dd></div><div><dt>Effective requirements</dt><dd>{selectedBuild.effectiveRequirements.length}</dd></div></dl><code>{selectedBuild.baseline.contentHash}</code></div><div className="effectiveSet"><h3>Effective software requirements</h3>{selectedBuild.effectiveRequirements.map(r=><article key={r.id}><b>{r.displayNumber}</b><p>{r.statement}</p><small>{r.level} · Verification: {r.verificationMethod}</small></article>)}</div>{selectedBuild.scrs.map(scr=><article className="buildScr" key={scr.id}><button onClick={()=>onOpenScr(scr.id)}>{scr.displayNumber}</button><div><b>{scr.title}</b><p>{scr.requirements.length} requirement changes introduced by this SCR</p>{scr.requirements.map(r=><small key={r.id}>{r.displayNumber} · {r.kind} · {r.statement||'Retired'}</small>)}</div></article>)}</>:<div className="historyEmpty large">Select a build to inspect its exact baseline, SCR revisions, and requirement impact.</div>}</section></div>}
 </main>
}
