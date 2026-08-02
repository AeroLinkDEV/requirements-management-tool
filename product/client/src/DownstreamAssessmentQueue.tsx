import { useCallback, useEffect, useState } from 'react'
import type { AuthUser } from './IdentityCenter'
import PersonPicker from './PersonPicker'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import './DownstreamAssessmentQueue.css'

type Assessment = {
  id:string; sourceChangeRequestNumber:string; sourceTitle:string; targetLevel:'HighLevel'|'LowLevel'
  state:'Open'|'InReview'|'Approved'|'Superseded'; outcome:'Pending'|'NoChangeRequired'|'ChangeRequestsLinked'
  assignedEngineerId?:string; selectedApproverId?:string; rationale:string; supersededReason:string
  linkedChangeRequests:{changeRequestId:string;changeRequestNumber:string}[]
  capabilities:{canAssign:boolean;canEdit:boolean;canSubmit:boolean;canApprove:boolean;canReturn:boolean}
}
type Draft = {id:string;displayNumber:string;title:string;requirementCount:number}
type RationaleDecision = {assessmentId:string;sourceNumber:string;kind:'no-change'|'return'}

export default function DownstreamAssessmentQueue({api,projectId,releaseId,targetLevel,user,onOpenScr}:{
  api:string;projectId:string;releaseId:string;targetLevel:'HighLevel'|'LowLevel';user:AuthUser;onOpenScr:(id:string)=>void
}) {
  const [rows,setRows]=useState<Assessment[]>([]),[drafts,setDrafts]=useState<Draft[]>([])
  const [busy,setBusy]=useState(''),[error,setError]=useState(''),[revision,setRevision]=useState(0)
  const [approvers,setApprovers]=useState<Record<string,{userId:string;name:string}>>({})
  const [decision,setDecision]=useState<RationaleDecision>(),[rationale,setRationale]=useState('')
  const load=useCallback(async()=>{
    const [assessments,requests]=await Promise.all([
      fetch(`${api}/api/downstream-assessments?projectId=${projectId}&releaseId=${releaseId}&targetLevel=${targetLevel}`),
      fetch(`${api}/api/history/scrs?projectId=${projectId}&releaseId=${releaseId}&type=Software&level=${targetLevel}&state=Draft&page=1&pageSize=100`),
    ])
    if(assessments.ok)setRows(await assessments.json())
    if(requests.ok)setDrafts((await requests.json()).items)
  },[api,projectId,releaseId,targetLevel])
  useEffect(()=>{void load()},[load,revision])
  const act=async(id:string,path:string,body:object={})=>{
    setBusy(id);setError('')
    try{await apiRequest(`${api}/api/downstream-assessments/${id}/${path}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});setRevision(x=>x+1);return true}
    catch(problem){setError(operationError(problem,'The downstream assessment could not be updated.'));return false}
    finally{setBusy('')}
  }
  const openDecision=(row:Assessment,kind:RationaleDecision['kind'])=>{setError('');setRationale('');setDecision({assessmentId:row.id,sourceNumber:row.sourceChangeRequestNumber,kind})}
  const closeDecision=()=>{if(busy)return;setDecision(undefined);setRationale('');setError('')}
  const confirmDecision=async()=>{
    if(!decision||!rationale.trim())return
    if(await act(decision.assessmentId,decision.kind,{rationale:rationale.trim()})){setDecision(undefined);setRationale('')}
  }
  if(!rows.length)return null
  return <section className="downstreamQueue" aria-labelledby="downstream-title">
    <header><div><p className="eyebrow">CONSUMING ENGINEERING</p><h2 id="downstream-title">Downstream change assessments</h2><p>Approved upstream changes waiting for an explicit HLR or LLR engineering conclusion.</p></div></header>
    {error&&<div className="workspaceError" role="alert">{error}</div>}
    {rows.map((row,index)=><article className={`downstreamAssessment ${row.state.toLowerCase()}`} key={row.id}>
      <div className="downstreamSource"><b>{row.sourceChangeRequestNumber}</b><span>{row.sourceTitle}</span><i>{row.targetLevel==='HighLevel'?'HLR':'LLR'} assessment</i></div>
      <div className="downstreamConclusion">
        <strong>{row.state==='Superseded'?'Out of date — update required':row.state==='InReview'?'Awaiting approval':row.state}</strong>
        {row.state==='Superseded'&&<p>{row.supersededReason}</p>}
        {row.rationale&&<p>{row.rationale}</p>}
        {row.linkedChangeRequests.map(link=><button type="button" className="linkedScr" key={link.changeRequestId} onClick={()=>onOpenScr(link.changeRequestId)}>{link.changeRequestNumber}</button>)}
      </div>
      <div className="downstreamActions">
        {row.capabilities.canAssign&&<button type="button" disabled={busy===row.id} onClick={()=>void act(row.id,'assign',{engineerId:user.userName})}>Take it on</button>}
        {row.capabilities.canEdit&&<>
          <button type="button" className="quiet" disabled={busy===row.id} onClick={()=>openDecision(row,'no-change')}>No change required</button>
          <label>Link Draft SWCR<select defaultValue="" onChange={event=>{if(event.target.value)void act(row.id,'change-requests',{changeRequestId:event.target.value})}}><option value="">Choose…</option>{drafts.map(d=><option value={d.id} key={d.id}>{d.displayNumber} · {d.title}</option>)}</select></label>
          {row.capabilities.canSubmit&&<><PersonPicker api={api} projectId={projectId} value={approvers[row.id]?.userId??''} name={approvers[row.id]?.name??''} index={9300+index} label={`Approver for ${row.sourceChangeRequestNumber}`} excludeUserNames={[row.assignedEngineerId??user.userName,user.userName]} onSelect={person=>setApprovers(current=>({...current,[row.id]:person}))}/><button type="button" disabled={busy===row.id||!approvers[row.id]?.userId} onClick={()=>void act(row.id,'submit',{approverId:approvers[row.id].userId})}>Send for approval</button></>}
        </>}
        {row.state==='Open'&&!row.assignedEngineerId&&!row.capabilities.canAssign&&<span>Software engineering authority is required to claim this assessment.</span>}
        {row.state==='InReview'&&<span>Selected approver: <PersonName userName={row.selectedApproverId??''}/></span>}
        {row.capabilities.canApprove&&<button type="button" onClick={()=>void act(row.id,'approve')}>Approve</button>}{row.capabilities.canReturn&&<button type="button" className="quiet" onClick={()=>openDecision(row,'return')}>Return</button>}
      </div>
    </article>)}
    <p className="downstreamHelp">Need a new downstream SWCR? Create the HLR or LLR Draft first, then link it here. One Draft may answer several assessments, and one assessment may link several Drafts.</p>
    {decision&&<div className="downstreamDialogBackdrop" role="presentation">
      <section className="downstreamDecisionDialog" role="dialog" aria-modal="true" aria-labelledby="downstream-decision-title">
        <p className="eyebrow">DOWNSTREAM ASSESSMENT</p>
        <h2 id="downstream-decision-title">{decision.kind==='no-change'?`Record no-change conclusion for ${decision.sourceNumber}`:`Return ${decision.sourceNumber} assessment`}</h2>
        <p>{decision.kind==='no-change'?'Explain why the approved upstream change requires no downstream requirement revision.':'State exactly what the assigned engineer must update before this assessment can be approved.'}</p>
        {error&&<div className="workspaceError" role="alert">{error}</div>}
        <label>Decision rationale<textarea autoFocus value={rationale} onChange={event=>setRationale(event.target.value)}/></label>
        <div className="downstreamDialogActions">
          <button type="button" className="quiet" disabled={!!busy} onClick={closeDecision}>Cancel</button>
          <button type="button" disabled={!!busy||!rationale.trim()} onClick={()=>void confirmDecision()}>{busy?'Recording…':decision.kind==='no-change'?'Record no-change conclusion':'Return assessment'}</button>
        </div>
      </section>
    </div>}
  </section>
}
