import { useCallback, useEffect, useMemo, useState } from 'react'
import type { AuthUser } from './IdentityCenter'
import PersonPicker from './PersonPicker'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import './DownstreamAssessmentQueue.css'

type Level='HighLevel'|'LowLevel'
type SourceChange={id:string;displayNumber:string;level:string;kind:string;statement:string}
type Reopening={id:string;previousState:string;previousOutcome:string;previousRationale:string;previousDecidedBy?:string;previousDecidedAt?:string;previousApprovedBy?:string;previousApprovedAt?:string;detachedChangeRequestNumbers:string;reason:string;actorId:string;occurredAt:string}
type Assessment={id:string;sourceChangeRequestId:string;sourceChangeRequestNumber:string;sourceTitle:string;sourceProblem:string;sourceAnalysis:string;sourceSolution:string;sourceChanges:SourceChange[];targetLevel:Level;state:'Open'|'InReview'|'Approved'|'Superseded';outcome:'Pending'|'ChangeRequired'|'NoChangeRequired'|'ChangeRequestsLinked';assignedEngineerId?:string;selectedApproverId?:string;rationale:string;decidedBy?:string;decidedAt?:string;approvedBy?:string;approvedAt?:string;supersededReason:string;buildReleased:boolean;linkedChangeRequests:{changeRequestId:string;changeRequestNumber:string;title:string;state:string}[];reopenings:Reopening[];capabilities:{canAssign:boolean;canEdit:boolean;canSubmit:boolean;canApprove:boolean;canReturn:boolean;canReopen:boolean}}
type Draft={id:string;displayNumber:string;title:string;requirementCount:number}
type RationaleDecision={assessmentId:string;sourceNumber:string;kind:'no-change'|'return'|'reopen'}
type Impact={baseNumber:string;known:boolean;derivedRequirements:{id:string;displayNumber:string;level:string;statement:string;linkType:string}[]}

const levelName=(level:Level)=>level==='HighLevel'?'HLR':'LLR'
/**
 * The one question the drawer asks before it offers anything.
 *
 * The queue used to answer it with the entry button's label — "Review" for work in progress, "View" once
 * approved — which put the state in the place a reader looks for an action and then showed the same controls
 * underneath either way. State belongs here, where it decides what can actually be done.
 */
type DrawerMode='superseded'|'inReview'|'approved'|'unclaimed'|'undecided'|'concluded'
const drawerMode=(row:Assessment):DrawerMode=>
  row.state==='Superseded'?'superseded'
    :row.state==='InReview'?'inReview'
    :row.state==='Approved'?'approved'
    :!row.assignedEngineerId?'unclaimed'
    :row.outcome==='Pending'?'undecided':'concluded'
const conclusionText=(row:Assessment)=>{
  const level=levelName(row.targetLevel)
  if(row.outcome==='NoChangeRequired')return `No ${level} requirement change is required.`
  if(row.outcome==='ChangeRequestsLinked')return `A ${level} requirement change is required, and is controlled by the linked Draft ${level}CR.`
  if(row.outcome==='ChangeRequired')return `A ${level} requirement change is required. No Draft ${level}CR carries it yet.`
  return ''
}
const outcomeWords=(outcome:string)=>outcome==='NoChangeRequired'?'no change required'
  :outcome==='ChangeRequired'?'change required'
    :outcome==='ChangeRequestsLinked'?'change required, change request linked':'undecided'
const when=(value?:string)=>value?new Date(value).toLocaleDateString(undefined,{year:'numeric',month:'short',day:'numeric'}):''
const engineeringStatus=(row:Assessment)=>{
  const level=levelName(row.targetLevel)
  if(row.state==='Superseded')return `${level} assessment superseded`
  if(row.state==='InReview')return `${level} assessment in review`
  if(row.state==='Approved'&&row.outcome==='NoChangeRequired')return `Assessment complete — no ${level} change required`
  if(row.state==='Approved')return `Assessment complete — ${level} change required`
  if(!row.assignedEngineerId)return `${level} impact pending`
  if(row.outcome==='Pending')return `${level} assessment in progress`
  if(row.outcome==='ChangeRequired')return `Draft ${level}CR needed`
  if(row.outcome==='ChangeRequestsLinked')return `Draft linked — ready for review`
  return `${level} no downstream change — ready for review`
}

export default function DownstreamAssessmentQueue({api,projectId,releaseId,targetLevel,user,onOpenScr,onOpenRequirement,onCreateScr,initialAssessmentId,onAssessmentSelected}:{api:string;projectId:string;releaseId:string;targetLevel:Level;user:AuthUser;onOpenScr:(id:string)=>void;onOpenRequirement:(id:string,level:string)=>void;onCreateScr:(level:Level,assessmentId:string,sourceNumber:string)=>void;initialAssessmentId?:string;onAssessmentSelected:(id?:string)=>void}){
  const [rows,setRows]=useState<Assessment[]>([]),[drafts,setDrafts]=useState<Draft[]>([])
  const [busy,setBusy]=useState(''),[error,setError]=useState(''),[revision,setRevision]=useState(0)
  const [approvers,setApprovers]=useState<Record<string,{userId:string;name:string}>>({})
  const [decision,setDecision]=useState<RationaleDecision>(),[rationale,setRationale]=useState('')
  const [selectedId,setSelectedId]=useState(initialAssessmentId??''),[impacts,setImpacts]=useState<Impact[]>([]),[impactBusy,setImpactBusy]=useState(false)
  const load=useCallback(async()=>{const [assessments,requests]=await Promise.all([fetch(`${api}/api/downstream-assessments?projectId=${projectId}&releaseId=${releaseId}&targetLevel=${targetLevel}`),fetch(`${api}/api/history/change-requests?projectId=${projectId}&releaseId=${releaseId}&type=Software&level=${targetLevel}&state=Draft&page=1&pageSize=100`)]);if(assessments.ok)setRows(await assessments.json());if(requests.ok)setDrafts((await requests.json()).items)},[api,projectId,releaseId,targetLevel])
  useEffect(()=>{void load()},[load,revision])
  useEffect(()=>{setSelectedId(initialAssessmentId??'')},[initialAssessmentId])
  const selected=useMemo(()=>rows.find(row=>row.id===selectedId),[rows,selectedId])
  useEffect(()=>{if(!selected){setImpacts([]);return}let active=true;setImpactBusy(true);Promise.all(selected.sourceChanges.map(change=>fetch(`${api}/api/authoring/impact?projectId=${projectId}&baseNumber=${encodeURIComponent(change.displayNumber.replace(/\.\d{2}$/,''))}`).then(response=>response.ok?response.json():undefined))).then(values=>{if(active)setImpacts(values.filter(Boolean) as Impact[])}).catch(()=>{if(active)setImpacts([])}).finally(()=>{if(active)setImpactBusy(false)});return()=>{active=false}},[api,projectId,selected])
  const openAssessment=(id:string)=>{setSelectedId(id);onAssessmentSelected(id)},closeAssessment=()=>{setSelectedId('');onAssessmentSelected(undefined)}
  const act=async(id:string,path:string,body:object={})=>{setBusy(id);setError('');try{await apiRequest(`${api}/api/downstream-assessments/${id}/${path}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});setRevision(value=>value+1);return true}catch(problem){setError(operationError(problem,'The downstream assessment could not be updated.'));return false}finally{setBusy('')}}
  const openDecision=(row:Assessment,kind:RationaleDecision['kind'])=>{setError('');setRationale('');setDecision({assessmentId:row.id,sourceNumber:row.sourceChangeRequestNumber,kind})},closeDecision=()=>{if(busy)return;setDecision(undefined);setRationale('');setError('')}
  // Reopening states a reason for withdrawing an answer rather than a rationale for giving one, and the
  // server names the field for what it is.
  const confirmDecision=async()=>{if(!decision||!rationale.trim())return
    const body=decision.kind==='reopen'?{reason:rationale.trim()}:{rationale:rationale.trim()}
    if(await act(decision.assessmentId,decision.kind,body)){setDecision(undefined);setRationale('')}}
  const downward=impacts.flatMap(impact=>impact.derivedRequirements)
  if(!rows.length)return null
  return <section className="downstreamQueue" aria-labelledby="downstream-title">
    <header><div><p className="eyebrow">CONSUMING ENGINEERING</p><h2 id="downstream-title">Downstream change assessments</h2><p>Approved upstream changes waiting for an explicit HLR or LLR engineering conclusion.</p></div></header>{error&&<div className="workspaceError" role="alert">{error}</div>}
    {rows.map(row=><article className={`downstreamAssessment ${row.state.toLowerCase()}`} data-state={row.state} key={row.id}>
      <button type="button" className="downstreamSource" onClick={()=>openAssessment(row.id)} aria-label={`Open ${levelName(row.targetLevel)} assessment for ${row.sourceChangeRequestNumber}`}><b>{row.sourceChangeRequestNumber}</b><span>{row.sourceTitle}</span><i>{levelName(row.targetLevel)} assessment</i></button>
      <div className="downstreamConclusion"><strong>{engineeringStatus(row)}</strong>{row.state==='Superseded'&&<p>{row.supersededReason}</p>}{row.linkedChangeRequests.map(link=><button type="button" className="linkedScr" key={link.changeRequestId} onClick={()=>onOpenScr(link.changeRequestId)}>{link.changeRequestNumber} · {link.state}</button>)}</div>
      {/* One control, one word, in every state. What can be done about the assessment is decided inside it. */}
      <div className="downstreamActions"><button type="button" className="openAssessment" onClick={()=>openAssessment(row.id)}>Open assessment</button></div>
    </article>)}<p className="downstreamHelp">One Draft may answer several assessments, and one assessment may link several Drafts.</p>
    {selected&&(()=>{const mode=drawerMode(selected);return <div className="downstreamDrawerBackdrop" role="presentation"><aside className="downstreamDrawer" role="dialog" aria-modal="true" aria-labelledby="downstream-context-title" data-mode={mode}><header><div><p className="eyebrow">{levelName(selected.targetLevel)} ENGINEERING DECISION</p><h2 id="downstream-context-title">{selected.sourceChangeRequestNumber} downstream impact</h2><strong>{engineeringStatus(selected)}</strong></div><button type="button" className="quiet" onClick={closeAssessment} aria-label="Close downstream assessment">Close</button></header>
      <section className="downstreamDecisionWorkbench"><h3>Engineering conclusion</h3>
        {/* The conclusion is stated outright wherever one exists, so a reader never has to work out whether
            the question was answered from which buttons happen to be enabled. */}
        {selected.outcome!=='Pending'
          ?<div className="recordedConclusion" data-outcome={selected.outcome}><b>Recorded conclusion</b><p>{conclusionText(selected)}</p>
            <span className="conclusionAuthor">{selected.decidedBy?<>Recorded by <PersonName userName={selected.decidedBy}/>{selected.decidedAt&&<> on {when(selected.decidedAt)}</>}</>:'Recorded before AeroLink captured the deciding engineer by name.'}{selected.state==='Approved'&&selected.approvedBy&&<> · Approved by <PersonName userName={selected.approvedBy}/>{selected.approvedAt&&<> on {when(selected.approvedAt)}</>}</>}</span>
            {selected.rationale&&<p className="conclusionRationale">{selected.rationale}</p>}</div>
          :selected.rationale&&<div className="assessmentRationale"><b>Returned for rework</b><p>{selected.rationale}</p></div>}
        {selected.linkedChangeRequests.length>0&&<ul className="drawerChanges">{selected.linkedChangeRequests.map(link=><li className="linkedDraft" key={link.changeRequestId}><button type="button" className="drawerArtifactLink" onClick={()=>onOpenScr(link.changeRequestId)}>{link.changeRequestNumber}</button><b>{link.state}</b><span>{link.title}</span></li>)}</ul>}<div className="drawerDecisionActions">
        {mode==='superseded'&&<p className="drawerEmpty">{selected.supersededReason||'A newer assessment replaced this one. Work that assessment instead.'}</p>}
        {mode==='unclaimed'&&(selected.capabilities.canAssign
          ?<button type="button" disabled={busy===selected.id} onClick={()=>void act(selected.id,'assign',{engineerId:user.userName})}>Take it on</button>
          :<p className="drawerEmpty">{selected.buildReleased
            ?'This software build is released. Its downstream assessments are read-only.'
            :'Software engineering authority is required to claim this assessment.'}</p>)}
        {/* The only state in which both conclusions are offered: nobody has answered yet. */}
        {mode==='undecided'&&(selected.capabilities.canEdit
          ?<><button type="button" className="quiet" disabled={busy===selected.id} onClick={()=>openDecision(selected,'no-change')}>No change required</button><button type="button" disabled={busy===selected.id} onClick={()=>void act(selected.id,'change-required')}>Change required</button></>
          :<p className="drawerEmpty"><PersonName userName={selected.assignedEngineerId??''}/> holds this assessment and records its conclusion.</p>)}
        {mode==='concluded'&&(selected.capabilities.canEdit
          ?<>{selected.outcome==='ChangeRequired'&&selected.linkedChangeRequests.length===0&&<button type="button" onClick={()=>onCreateScr(selected.targetLevel,selected.id,selected.sourceChangeRequestNumber)}>Create Draft {levelName(selected.targetLevel)}CR</button>}{(selected.outcome==='ChangeRequired'||selected.outcome==='ChangeRequestsLinked')&&<label>Link {selected.linkedChangeRequests.length?'another':'an existing'} Draft change request<select value="" onChange={event=>{if(event.target.value)void act(selected.id,'change-requests',{changeRequestId:event.target.value})}}><option value="">Choose…</option>{drafts.filter(d=>!selected.linkedChangeRequests.some(link=>link.changeRequestId===d.id)).map(d=><option value={d.id} key={d.id}>{d.displayNumber} · {d.title}</option>)}</select></label>}{selected.capabilities.canSubmit&&<><PersonPicker api={api} projectId={projectId} value={approvers[selected.id]?.userId??''} name={approvers[selected.id]?.name??''} index={9300} label={`Approver for ${selected.sourceChangeRequestNumber}`} excludeUserNames={[selected.assignedEngineerId??user.userName,user.userName]} onSelect={person=>setApprovers(current=>({...current,[selected.id]:person}))}/><button type="button" disabled={busy===selected.id||!approvers[selected.id]?.userId} onClick={()=>void act(selected.id,'submit',{approverId:approvers[selected.id].userId})}>Send for approval</button></>}</>
          :<p className="drawerEmpty"><PersonName userName={selected.assignedEngineerId??''}/> holds this assessment and carries it to review.</p>)}
        {mode==='inReview'&&<><span>Selected approver: <PersonName userName={selected.selectedApproverId??''}/></span>{selected.capabilities.canApprove&&<button type="button" onClick={()=>void act(selected.id,'approve')}>Approve</button>}{selected.capabilities.canReturn&&<button type="button" className="quiet" onClick={()=>openDecision(selected,'return')}>Return</button>}{!selected.capabilities.canApprove&&!selected.capabilities.canReturn&&<p className="drawerEmpty">This assessment is with its approver. It cannot be edited until they approve or return it.</p>}</>}
        {selected.capabilities.canReopen&&<button type="button" className="quiet reopenAssessment" disabled={busy===selected.id} onClick={()=>openDecision(selected,'reopen')}>Reopen assessment</button>}
      </div></section>
      {selected.reopenings.length>0&&<section className="withdrawnConclusions"><h3>Withdrawn conclusions</h3><ul className="drawerChanges">{selected.reopenings.map(entry=><li key={entry.id}><b>{outcomeWords(entry.previousOutcome)}{entry.previousState==='Approved'?' · approved':''}</b><span>Recorded by {entry.previousDecidedBy?<PersonName userName={entry.previousDecidedBy}/>:'an engineer AeroLink did not record'}{entry.previousDecidedAt&&<> on {when(entry.previousDecidedAt)}</>}. Withdrawn by <PersonName userName={entry.actorId}/> on {when(entry.occurredAt)}.</span>{entry.previousRationale&&<span>Previous rationale: {entry.previousRationale}</span>}{entry.detachedChangeRequestNumbers&&<span>Detached from {entry.detachedChangeRequestNumbers}.</span>}<span>Reason for withdrawal: {entry.reason}</span></li>)}</ul></section>}<section><h3>Source change request</h3><button type="button" className="drawerArtifactLink" onClick={()=>onOpenScr(selected.sourceChangeRequestId)}>{selected.sourceChangeRequestNumber} · {selected.sourceTitle}</button><dl className="sourceCase"><div><dt>Problem</dt><dd>{selected.sourceProblem||'Not recorded'}</dd></div><div><dt>Analysis</dt><dd>{selected.sourceAnalysis||'Not recorded'}</dd></div><div><dt>Solution</dt><dd>{selected.sourceSolution||'Not recorded'}</dd></div></dl></section><section><h3>Approved requirement changes</h3>{selected.sourceChanges.length?<ul className="drawerChanges">{selected.sourceChanges.map(change=><li key={change.id}><b>{change.displayNumber}</b><span>{change.kind} · {change.statement}</span></li>)}</ul>:<p className="drawerEmpty">No approved requirement changes were recorded on the source request.</p>}</section><section><h3>Current downward requirement trace</h3>{impactBusy?<p>Loading current trace…</p>:downward.length?<ul className="drawerChanges">{downward.map(requirement=><li key={`${requirement.id}-${requirement.displayNumber}`}><button type="button" className="drawerArtifactLink" onClick={()=>onOpenRequirement(requirement.id,requirement.level)}>{requirement.displayNumber}</button><span>{requirement.linkType} · {requirement.statement}</span></li>)}</ul>:<p className="drawerEmpty">No current downward requirement trace is recorded for the changed requirements.</p>}</section></aside></div>})()}
    {decision&&<div className="downstreamDialogBackdrop" role="presentation"><section className="downstreamDecisionDialog" role="dialog" aria-modal="true" aria-labelledby="downstream-decision-title"><p className="eyebrow">DOWNSTREAM ASSESSMENT</p><h2 id="downstream-decision-title">{decision.kind==='no-change'?`Record no-change conclusion for ${decision.sourceNumber}`:decision.kind==='return'?`Return ${decision.sourceNumber} assessment`:`Reopen the ${decision.sourceNumber} assessment`}</h2><p>{decision.kind==='no-change'?'Explain why the approved upstream change requires no downstream requirement revision.':decision.kind==='return'?'State exactly what the assigned engineer must update before this assessment can be approved.':'The recorded conclusion is withdrawn and the assessment returns to undecided. The previous conclusion, its author and any linked Draft change request are kept in this assessment’s withdrawn-conclusions record; linked Drafts are detached but not otherwise changed.'}</p>{error&&<div className="workspaceError" role="alert">{error}</div>}<label>{decision.kind==='reopen'?'Reason for withdrawing the conclusion':'Decision rationale'}<textarea autoFocus value={rationale} onChange={event=>setRationale(event.target.value)}/></label><div className="downstreamDialogActions"><button type="button" className="quiet" disabled={!!busy} onClick={closeDecision}>Cancel</button><button type="button" disabled={!!busy||!rationale.trim()} onClick={()=>void confirmDecision()}>{busy?'Recording…':decision.kind==='no-change'?'Record no-change conclusion':decision.kind==='return'?'Return assessment':'Reopen assessment'}</button></div></section></div>}
  </section>
}
