import { useCallback, useEffect, useState } from 'react'
import ChangeRequestRegister, { type RegisterRow } from './ChangeRequestRegister'
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
 initialAssessmentId?:string;onAssessmentSelected:(id?:string)=>void;
 initialStateIntent?:HistoryStateIntent;onStateIntentChange:(intent?:HistoryStateIntent)=>void;
 onBack:()=>void;onOpenScr:(id:string)=>void;onOpenRequirement:(id:string,level:string)=>void;onCreateSystem:()=>void;
 onCreateSoftware:(level:'HighLevel'|'LowLevel',assessmentId?:string,sourceNumber?:string)=>void
 user:AuthUser
}

const stateLabels:Record<HistoryStateIntent,string>={Draft:'Draft',InReview:'In review',Approved:'Approved',SelectedForBaseline:'Allocated to a build',Deferred:'Deferred',ApprovedOrSelected:'Approved or allocated'}
// The lifecycle filter's options, in the order the register offers them.
const registerStateOptions=(Object.keys(stateLabels) as HistoryStateIntent[]).map(value=>({value,label:stateLabels[value]}))
const matchesStateIntent=(state:string,intent?:HistoryStateIntent)=>!intent||(intent==='ApprovedOrSelected'?(state==='Approved'||state==='SelectedForBaseline'):state===intent)

export default function HistoryExplorer({api,projectId,releases,activeReleaseId,scope,initialSoftwareLevel,onSoftwareLevelChange,initialAssessmentId,onAssessmentSelected,initialStateIntent,onStateIntentChange,onBack,onOpenScr,onOpenRequirement,onCreateSystem,onCreateSoftware,user}:Props){
 const [query,setQuery]=useState(''),[softwareLevel,setSoftwareLevel]=useState<SoftwareLevel>(initialSoftwareLevel),[stateIntent,setStateIntent]=useState<HistoryStateIntent|undefined>(initialStateIntent),[scrPage,setScrPage]=useState(1),[scrTotal,setScrTotal]=useState(0),[scrTotalPages,setScrTotalPages]=useState(1),[scrs,setScrs]=useState<Scr[]>([])
 const activeRelease=releases.find(x=>x.id===activeReleaseId)
 const load=useCallback(async()=>{const params=new URLSearchParams({projectId,page:String(scrPage),pageSize:'50',releaseId:activeReleaseId,type:scope});if(scope==='Software')params.set('level',softwareLevel);if(stateIntent)params.set('state',stateIntent);if(query)params.set('search',query)
  const response=await fetch(`${api}/api/history/change-requests?${params}`)
  if(response.ok){const body=await response.json();setScrs(body.items);setScrTotal(body.totalCount);setScrTotalPages(Math.max(1,body.totalPages))}
 },[activeReleaseId,api,projectId,query,scope,scrPage,softwareLevel,stateIntent])
 useEffect(()=>{const timer=setTimeout(load,180);return()=>clearTimeout(timer)},[load])
 useEffect(()=>{setScrPage(1)},[activeReleaseId,scope])
 useEffect(()=>{setSoftwareLevel(initialSoftwareLevel);setScrPage(1)},[initialSoftwareLevel])
 useEffect(()=>{setStateIntent(initialStateIntent);setScrPage(1)},[initialStateIntent])
 const changeStateIntent=(intent?:HistoryStateIntent)=>{setStateIntent(intent);setScrPage(1);onStateIntentChange(intent)}
 const visibleScrs=scrs.filter(x=>matchesStateIntent(x.state,stateIntent))
 // The register itself is shared with the verification side, so what a reader recognises as "the register"
 // cannot drift between them. Only the mapping into its row shape is the requirements side's own.
 const toRegisterRow=(x:Scr):RegisterRow=>({
  id:x.id,baseNumber:x.baseNumber,revision:x.revision,displayNumber:x.displayNumber,title:x.title,
  state:x.state,deferredFromState:x.deferredFromState,authorId:x.authorId,targetReleaseId:x.targetReleaseId,
  changeCount:x.requirementCount,updatedAt:x.updatedAt,revisionCount:x.revisionCount,
  badge:x.hasHighLevelChanges&&x.hasLowLevelChanges?<i className="mixedLevelBadge">HLR + LLR</i>:undefined,
 })
 const loadRevisions=async(row:RegisterRow)=>{
  const params=new URLSearchParams({projectId,baseNumber:row.baseNumber,page:'1',pageSize:'50'})
  const response=await fetch(`${api}/api/history/change-requests?${params}`)
  if(!response.ok)throw new Error(String(response.status))
  const body=await response.json() as {items:Scr[]}
  return body.items.map(toRegisterRow)
 }
 const changeSoftwareLevel=(level:SoftwareLevel)=>{setSoftwareLevel(level);setScrPage(1);onAssessmentSelected(undefined);onSoftwareLevelChange(level)}
 return <main className="historyPage">
  <header className="historyHeader">
   <div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / BUILD {activeRelease?.version}</p><h1>{scope} Change Requests</h1><p>{activeRelease?.isReleased?`Released ${scope.toLowerCase()} change history owned by Build ${activeRelease.version}.`:`Active and deferred ${scope.toLowerCase()} change requests owned by Build ${activeRelease?.version}.`}</p></div>
   {!activeRelease?.isReleased&&(scope==='System'
    ?<button className="recordBuild" onClick={onCreateSystem}>+ New System Change Request</button>
    :<button className="recordBuild" onClick={()=>onCreateSoftware(softwareLevel)}>+ New {softwareLevel==='HighLevel'?'HLR':'LLR'} Change Request</button>)}
  </header>
  {scope==='Software'&&<nav className="softwareLevelTabs" aria-label="Software requirement level"><button type="button" aria-current={softwareLevel==='HighLevel'?'page':undefined} onClick={()=>changeSoftwareLevel('HighLevel')}><b>HLR</b><span>High-level requirements</span></button><button type="button" aria-current={softwareLevel==='LowLevel'?'page':undefined} onClick={()=>changeSoftwareLevel('LowLevel')}><b>LLR</b><span>Low-level requirements</span></button></nav>}
  {scope==='Software'&&<DownstreamAssessmentQueue api={api} projectId={projectId} releaseId={activeReleaseId} targetLevel={softwareLevel} user={user} onOpenScr={onOpenScr} onOpenRequirement={onOpenRequirement} onCreateScr={onCreateSoftware} initialAssessmentId={initialAssessmentId} onAssessmentSelected={onAssessmentSelected}/>}
  <ChangeRequestRegister
   changeNoun="requirement changes"
   recordNoun={`${scope.toLowerCase()} change requests`}
   contextLabel={`${scope==='Software'?(softwareLevel==='HighLevel'?'HLR':'LLR'):'System'} area`}
   activeRelease={activeRelease} releases={releases}
   rows={visibleScrs.map(toRegisterRow)} totalCount={scrTotal}
   page={scrPage} totalPages={scrTotalPages} onPageChange={setScrPage}
   query={query} onQueryChange={value=>{setQuery(value);setScrPage(1)}}
   stateIntent={stateIntent??''}
   onStateIntentChange={value=>changeStateIntent(value?value as HistoryStateIntent:undefined)}
   stateOptions={registerStateOptions}
   onOpen={onOpenScr} onLoadRevisions={loadRevisions}/>
 </main>
}
