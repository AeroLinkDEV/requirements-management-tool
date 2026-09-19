import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import CodeEvidenceDecision, { type CodeEvidence } from './CodeEvidenceDecision'
import { stateLabel } from './presentation'
import { projectConfigurationRepositoryPath } from './routing'
import { useLatestRequest } from './useLatestRequest'
import './CodeTraceabilityCenter.css'

type Mapping={verifiedRemoteProjectId?:number;verifiedRepositoryEndpoint?:string;verifiedRepositoryPath?:string;repositoryConfigurationVersion?:number;repositoryVerifiedAt?:string;repositoryVerifiedBy?:string;id:string;disposition:string;repositoryPath:string;mergeRequestReference:string;mergeRequestTitle:string;mergeRequestUrl:string;mergeCommitSha:string;mergedAt?:string;noCodeChangeRationale:string;isDemonstration:boolean;recordedBy:string;recordedAt:string}
type Requirement={artifactId:string;revisionId:string;displayNumber:string;statement:string;mapping?:Mapping;evidence?:CodeEvidence}
type Waiting={detail:string;action:string;recordedCount:number}
type Overview={repository?:{status:string;canRecordGitLabMerge:boolean;detail:string};build:{version:string;readOnly:boolean};sourceOfTruth:string;evaluationState:'Evaluated'|'WaitingForPrerequisite';demonstrationScope:boolean;waiting?:Waiting;summary:{required:number;mapped:number;missing:number;percent:number;gateComplete:boolean}|null;requirements:Requirement[]}

export default function CodeTraceabilityCenter({api,projectId,releaseId,readOnly,onBack,embedded=false}:{api:string;projectId:string;releaseId:string;readOnly:boolean;onBack:()=>void;embedded?:boolean}){
 const [overview,setOverview]=useState<Overview>(),[error,setError]=useState(''),[busy,setBusy]=useState(false),[showForm,setShowForm]=useState(false)
 const [selected,setSelected]=useState(''),[disposition,setDisposition]=useState('GitLabMerge')
 const {begin,invalidate}=useLatestRequest()
 const saveController=useRef<AbortController|null>(null)
 const load=useCallback(async(signal?:AbortSignal)=>{
  const isCurrent=begin();setOverview(undefined);setError('')
  try{
   const response=await fetch(`${api}/api/code-traceability?projectId=${projectId}&releaseId=${releaseId}`,{signal})
   if(!response.ok)throw new Error('Code traceability could not be loaded.')
   const body=await response.json() as Overview
   if(signal?.aborted||!isCurrent())return
   setOverview(body);setDisposition(body.repository?.canRecordGitLabMerge===false?'NoCodeChangeRequired':'GitLabMerge')
   setSelected(body.requirements.find(item=>!item.mapping&&!item.evidence)?.revisionId??'')
  }catch{if(!signal?.aborted&&isCurrent())setError('Code traceability could not be loaded.')}
 },[api,projectId,releaseId,begin])
 useEffect(()=>{const controller=new AbortController();setShowForm(false);setBusy(false);void load(controller.signal);return()=>{controller.abort();saveController.current?.abort();invalidate()}},[load,invalidate])
 const save=async(event:FormEvent<HTMLFormElement>)=>{
  event.preventDefault();const requirement=overview?.requirements.find(item=>item.revisionId===selected)
  if(!requirement||busy)return
  const form=new FormData(event.currentTarget),isCurrent=begin(),controller=new AbortController()
  saveController.current?.abort();saveController.current=controller;setBusy(true);setError('')
  try{
   const response=await fetch(`${api}/api/code-traceability`,{method:'POST',signal:controller.signal,headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId,releaseId,requirementArtifactId:requirement.artifactId,requirementRevisionId:requirement.revisionId,disposition,repositoryPath:form.get('repositoryPath'),mergeRequestReference:form.get('mergeRequestReference'),mergeRequestTitle:form.get('mergeRequestTitle'),mergeRequestUrl:form.get('mergeRequestUrl'),mergeCommitSha:form.get('mergeCommitSha'),mergedAt:form.get('mergedAt')?new Date(String(form.get('mergedAt'))).toISOString():null,noCodeChangeRationale:form.get('noCodeChangeRationale')})})
   const body=await response.json()
   if(controller.signal.aborted||!isCurrent())return
   if(!response.ok){setError(body.error??'The code mapping could not be recorded.');return}
   setBusy(false);setShowForm(false);await load(controller.signal)
  }catch{if(!controller.signal.aborted&&isCurrent())setError('The code mapping could not be recorded. Refresh to check its status before retrying.')}
  finally{if(isCurrent())setBusy(false)}
 }
 const locked=readOnly||overview?.build.readOnly===true
 const gitLabAllowed=overview?.repository?.canRecordGitLabMerge!==false
 const missing=overview?.requirements.filter(item=>!item.mapping&&!item.evidence)??[]
 const Container=embedded?'section':'main'
 return <Container className="codeTracePage"><header><div>{!embedded&&<button className="back" onClick={onBack}>← Command Center</button>}<p className="eyebrow">SOFTWARE / CODE TRACEABILITY</p>{embedded?<h2>Implementation evidence</h2>:<h1>Code</h1>}<p>Exact approved requirement revisions mapped to the GitLab changes that implement them.</p></div>{!locked&&missing.length>0&&<button className="primaryAction" onClick={()=>setShowForm(true)}>+ Record code mapping</button>}</header>
  {error&&<div className="workspaceError" role="alert">{error}<button onClick={()=>setError('')}>Dismiss</button></div>}
  <section className="gitLabBoundary"><span>GL</span><div><b>GitLab is the source of truth</b><p>{overview?.sourceOfTruth}</p></div></section>
  {overview?.repository&&!gitLabAllowed&&<aside className="demoDataNotice"><b>Repository {overview.repository.status==="Pending"?"Pending":"unverified"}</b><span>{overview.repository.detail} <a href={projectConfigurationRepositoryPath(projectId)}>Open repository configuration</a></span></aside>}
  {overview?.demonstrationScope&&<aside className="demoDataNotice"><b>Demonstration data</b><span>These example merge requests and commit SHAs illustrate the integration contract. They are not production GitLab records.</span></aside>}
  {/* Waiting says so in words and shows no number. A percentage here was computed from the baseline this
      build inherits rather than the one its release is decided against, so the page reported "80%" for a gate
      the Decision Room had not evaluated at all. */}
  {!overview?<section className="codeGate waiting"><div><h2>{error?'Code traceability unavailable':'Loading code traceability'}</h2><p>The build gate is not shown until its current evidence is available.</p><button onClick={()=>void load()}>Retry</button></div><strong aria-hidden="true">—</strong></section>:overview.evaluationState==='WaitingForPrerequisite'
   ?<section className="codeGate waiting"><div><small>RELEASE GATE · BUILD {overview.build.version}</small><h2>Not evaluated yet</h2><p>{overview.waiting?.detail}</p><p className="codeGateAction">{overview.waiting?.action}</p>{!!overview.waiting?.recordedCount&&<p className="codeGateAction">{overview.waiting.recordedCount} earlier mapping{overview.waiting.recordedCount===1?'':'s'} recorded against this build remain auditable.</p>}</div><strong aria-hidden="true">—</strong></section>
   :<section className={`codeGate ${overview?.summary?.gateComplete?'complete':'open'}`}><div><small>RELEASE GATE · BUILD {overview?.build.version}</small><h2>{overview?.summary?.mapped??0} of {overview?.summary?.required??0} exact requirement revisions mapped</h2><p>{overview?.summary?.gateComplete?'Code traceability is complete for this build scope.':`${overview?.summary?.missing??0} requirement revision${overview?.summary?.missing===1?'':'s'} still need a GitLab merge or a justified no-code decision.`}</p></div><strong>{overview?.summary?.percent??0}%</strong></section>}
  <section className="codeRecords"><div><h2>Requirement-to-code evidence</h2><span>{locked?'Historical · read-only':'Active development'}</span></div>{overview?.requirements.map(requirement=><article key={requirement.revisionId} className={requirement.evidence?.countsAsImplementation||requirement.mapping?'mapped':'missing'}><header><div><b>{requirement.displayNumber}</b><p>{requirement.statement}</p></div><span>{requirement.evidence?stateLabel(requirement.evidence.state==='Accepted'?requirement.evidence.disposition??'Accepted':requirement.evidence.state):requirement.mapping?requirement.mapping.disposition==='NoCodeChangeRequired'?'No code change required':'Mapped':'Missing'}</span></header>{requirement.evidence?<CodeEvidenceDecision evidence={requirement.evidence}/>:requirement.mapping?.disposition==='GitLabMerge'?<div className="mergeEvidence"><div><small>GITLAB MERGE REQUEST</small><a href={requirement.mapping.mergeRequestUrl} target="_blank" rel="noreferrer">{requirement.mapping.mergeRequestReference} · {requirement.mapping.mergeRequestTitle} ↗</a><span>{requirement.mapping.repositoryPath}</span>{requirement.mapping.verifiedRemoteProjectId&&<small>Verified remote project {requirement.mapping.verifiedRemoteProjectId} · configuration {requirement.mapping.repositoryConfigurationVersion} · {requirement.mapping.repositoryVerifiedAt&&new Date(requirement.mapping.repositoryVerifiedAt).toLocaleString()}<br/>{requirement.mapping.verifiedRepositoryEndpoint}</small>}</div><div><small>IMMUTABLE MERGE COMMIT</small><code>{requirement.mapping.mergeCommitSha}</code><span>{requirement.mapping.mergedAt&&new Date(requirement.mapping.mergedAt).toLocaleString()}</span></div></div>:requirement.mapping?<p className="noCodeRationale">{requirement.mapping.noCodeChangeRationale}</p>:<p className="missingCallout">Record the implementing GitLab merge request, or explain why the exact approved requirement requires no code change.</p>}{requirement.mapping&&<footer>{requirement.mapping.isDemonstration&&<em>DEMO</em>}Recorded by <PersonName userName={requirement.mapping.recordedBy}/> · {new Date(requirement.mapping.recordedAt).toLocaleString()}</footer>}</article>)}</section>
  {showForm&&<div className="codeModal" role="dialog" aria-label="Record code mapping"><form onSubmit={save}><button type="button" className="close" aria-label="Close" onClick={()=>setShowForm(false)}>×</button><p className="eyebrow">EXACT REQUIREMENT REVISION</p><h2>Record code traceability</h2><label>Approved requirement revision<select value={selected} onChange={event=>setSelected(event.target.value)}>{missing.map(item=><option value={item.revisionId} key={item.revisionId}>{item.displayNumber} · {item.statement}</option>)}</select></label><fieldset><legend>Disposition</legend><label><input type="radio" disabled={!gitLabAllowed} checked={disposition==='GitLabMerge'} onChange={()=>setDisposition('GitLabMerge')}/> GitLab merge</label><label><input type="radio" checked={disposition==='NoCodeChangeRequired'} onChange={()=>setDisposition('NoCodeChangeRequired')}/> No code change required</label></fieldset>{disposition==='GitLabMerge'&&gitLabAllowed?<><label>Repository path<input name="repositoryPath" placeholder="group/project" required/></label><label>Merge request reference<input name="mergeRequestReference" placeholder="!1842" required/></label><label>Merge request title<input name="mergeRequestTitle" required/></label><label>GitLab merge request URL<input name="mergeRequestUrl" type="url" placeholder="https://gitlab.example/group/project/-/merge_requests/1842" required/></label><label>Merge commit SHA<input name="mergeCommitSha" minLength={40} maxLength={64} required/></label><label>Merged at<input name="mergedAt" type="datetime-local" required/></label></>:<label>No-code rationale<textarea name="noCodeChangeRationale" required/></label>}<button className="primaryAction" disabled={busy||!selected||(disposition==='GitLabMerge'&&!gitLabAllowed)}>Record immutable mapping</button></form></div>}
 </Container>
}
