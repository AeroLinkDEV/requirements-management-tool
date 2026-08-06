import { useCallback, useEffect, useState } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
// The requirements queue's stylesheet, imported rather than copied. The testing side is meant to be the same
// surface for the same kind of work, and a second stylesheet that merely looked like it would drift the first
// time either was touched.
import './DownstreamAssessmentQueue.css'

type Kind='Introduce'|'Modify'|'Retire'
type ProcedureChange={id:string;displayNumber:string;baseNumber:string;revision:number;kind:Kind;level:string;title:string;objective:string;preconditions:string;steps:string;expectedResult:string;rationale:string;drivingRequirementRevisionIds:string[]}
type Package={id:string;displayNumber:string;baseNumber:string;revision:number;discipline:string;state:string;outcome:string;procedureLevel:string;sourceChangeRequestNumber:string;assignedEngineerId?:string;procedureChanges:ProcedureChange[]}

const levelName=(discipline:string)=>discipline==='System'?'SYS':discipline==='HighLevelSoftware'?'HLR':'LLR'
const procedureWord=(level:string)=>level==='System'?'SYSTP':level==='HighLevel'?'HLRTP':'LLRTP'
/**
 * What the package is, and what has happened to it. The same two facts the requirements drawer states, in the
 * same order, because a reader moving between the two should not have to learn a second vocabulary.
 */
const packageStatus=(item:Package)=>{
  const level=levelName(item.discipline)
  if(item.state==='Superseded')return `${level}TCR Superseded`
  if(item.state==='Approved')return `${level}TCR Approved`
  if(item.state==='InReview')return `${level}TCR In Review – Awaiting Approval`
  return item.procedureChanges.length
    ? `${level}TCR Open – ${item.procedureChanges.length} procedure ${item.procedureChanges.length===1?'decision':'decisions'} proposed`
    : `${level}TCR Open – No Procedure Decisions Yet`
}
const kindWords=(kind:Kind)=>kind==='Introduce'?'New procedure':kind==='Modify'?'Modified procedure':'Retired procedure'

const emptyDraft={kind:'Introduce' as Kind,baseNumber:'',revision:0,title:'',objective:'',preconditions:'',steps:'',expectedResult:'',rationale:''}

/**
 * The room where test procedures are actually created, modified and retired.
 *
 * The test-side counterpart of the change request editor: a test change request carries procedure decisions
 * exactly as a change request carries requirement changes, so this is shaped like that editor rather than
 * invented. Nothing proposed here is a controlled procedure revision until the package is approved and
 * materialised into a build.
 */
export default function TestChangeRequestWorkspace({api,reviewId,canAuthor,onClose,onChanged}:{api:string;reviewId:string;canAuthor:boolean;onClose:()=>void;onChanged:()=>void}){
  const [item,setItem]=useState<Package>()
  const [busy,setBusy]=useState(false),[error,setError]=useState('')
  const [draft,setDraft]=useState(emptyDraft),[proposing,setProposing]=useState(false)

  const load=useCallback(async()=>{
    setError('')
    try{setItem(await apiRequest<Package>(`${api}/api/test-change-reviews/${reviewId}/procedure-changes`))}
    catch(problem){setError(operationError(problem,'The test change request could not be loaded.'))}
  },[api,reviewId])
  useEffect(()=>{void load()},[load])

  const act=async(run:()=>Promise<unknown>)=>{
    setBusy(true);setError('')
    try{await run();await load();onChanged();return true}
    catch(problem){setError(operationError(problem,'The test change request could not be updated.'));return false}
    finally{setBusy(false)}
  }
  const propose=async()=>{
    const body={...draft,
      // Introducing allocates the number on the server, so the client does not send one and cannot pick one.
      baseNumber:draft.kind==='Introduce'?undefined:draft.baseNumber.trim(),
      drivingRequirementRevisionIds:[]}
    if(await act(()=>apiRequest(`${api}/api/test-change-reviews/${reviewId}/procedure-changes`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}))){
      setProposing(false);setDraft(emptyDraft)
    }
  }
  const withdraw=(changeId:string)=>void act(()=>apiRequest(`${api}/api/test-change-reviews/${reviewId}/procedure-changes/${changeId}`,{method:'DELETE'}))
  const revise=()=>void act(()=>apiRequest(`${api}/api/test-change-reviews/${reviewId}/revise`,{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'}))

  const open=item?.state==='Open'
  const mayEdit=canAuthor&&open&&item?.outcome==='ChangeRequired'
  const retiring=draft.kind==='Retire'

  return <div className="downstreamDrawerBackdrop" role="presentation">
    <aside className="downstreamDrawer" role="dialog" aria-modal="true" aria-labelledby="tcr-workspace-title">
      <header>
        <div>
          <p className="eyebrow">{item?`${levelName(item.discipline)} TEST CHANGE REQUEST`:'TEST CHANGE REQUEST'}</p>
          <h2 id="tcr-workspace-title">{item?`${item.displayNumber} procedure decisions`:'Loading…'}</h2>
          {item&&<strong>{packageStatus(item)}</strong>}
        </div>
        <button type="button" className="quiet" onClick={onClose} aria-label="Close test change request">Close</button>
      </header>
      {error&&<div className="workspaceError" role="alert">{error}</div>}

      {item&&<>
        <section className="downstreamDecisionWorkbench">
          <h3>Procedure decisions</h3>
          {/* Stated outright, as the requirements drawer states its conclusion, so a reader never has to infer
              what the package does from which buttons happen to be enabled. */}
          {item.procedureChanges.length
            ? <ul className="drawerChanges">{item.procedureChanges.map(change=>
                <li className="linkedDraft" key={change.id}>
                  <b>{change.displayNumber} · {kindWords(change.kind)}</b>
                  <span>{change.title||'No title recorded'}</span>
                  {change.objective&&<span>{change.objective}</span>}
                  {change.rationale&&<span>Why: {change.rationale}</span>}
                  {mayEdit&&<button type="button" className="linkedScr" disabled={busy} onClick={()=>withdraw(change.id)}>Withdraw this decision</button>}
                </li>)}</ul>
            : <p className="drawerEmpty">No procedure decisions are proposed yet. A test change request exists because test work is required, so it is not finished until it says what that work is.</p>}
          <div className="drawerDecisionActions">
            {mayEdit&&<button type="button" disabled={busy} onClick={()=>{setDraft(emptyDraft);setProposing(true)}}>Propose a procedure change</button>}
            {/* Revising is the test-side twin of revising a change request: reopening approved work to correct
                it, which carries the existing decisions into the next revision. */}
            {canAuthor&&item.state==='Approved'&&<button type="button" className="quiet reopenAssessment" disabled={busy} onClick={revise}>Revise this test change request</button>}
            {!canAuthor&&<p className="drawerEmpty">{item.assignedEngineerId
              ? <><PersonName userName={item.assignedEngineerId}/> holds this test change request and records its procedure decisions.</>
              : 'Test engineering authority is required to propose procedure work.'}</p>}
            {canAuthor&&!open&&item.state!=='Approved'&&<p className="drawerEmpty">This test change request is with its approver. Its procedure decisions cannot change until they approve or return it.</p>}
          </div>
        </section>

        <section>
          <h3>Source change request</h3>
          <p>{item.sourceChangeRequestNumber}</p>
          <dl className="sourceCase">
            <div><dt>Discipline</dt><dd>{levelName(item.discipline)} verification</dd></div>
            <div><dt>Procedure level</dt><dd>{procedureWord(item.procedureLevel)} — this package may only carry {item.procedureLevel} procedures</dd></div>
            <div><dt>Revision</dt><dd>{item.revision.toString().padStart(2,'0')}</dd></div>
          </dl>
        </section>
      </>}
    </aside>

    {proposing&&<div className="downstreamDialogBackdrop" role="presentation">
      <section className="downstreamDecisionDialog" role="dialog" aria-modal="true" aria-labelledby="tcr-propose-title">
        <p className="eyebrow">PROCEDURE DECISION</p>
        <h2 id="tcr-propose-title">Propose a procedure change</h2>
        <p>{retiring
          ? 'A retirement withdraws the procedure rather than restating it, so it needs no body — only the procedure it acts on and why.'
          : 'What this test change request proposes to do to the procedures. Nothing here becomes a controlled procedure revision until the package is approved and materialised into a build.'}</p>
        {error&&<div className="workspaceError" role="alert">{error}</div>}
        <label>What is being done
          <select value={draft.kind} onChange={event=>setDraft(current=>({...current,kind:event.target.value as Kind}))}>
            <option value="Introduce">Introduce a new procedure</option>
            <option value="Modify">Modify an existing procedure</option>
            <option value="Retire">Retire an existing procedure</option>
          </select>
        </label>
        {/* Introducing allocates the number centrally, so it is not asked for; anything else has to name the
            procedure it acts on, because allocating a fresh number for a modification would quietly make it a
            different procedure. */}
        {draft.kind!=='Introduce'&&<>
          <label>Procedure number
            <input value={draft.baseNumber} onChange={event=>setDraft(current=>({...current,baseNumber:event.target.value}))} placeholder={`${procedureWord(item?.procedureLevel??'System')}-000001`}/>
          </label>
          <label>New revision
            <input type="number" min={1} value={draft.revision} onChange={event=>setDraft(current=>({...current,revision:Number(event.target.value)}))}/>
          </label>
        </>}
        {!retiring&&<>
          <label>Title<input value={draft.title} onChange={event=>setDraft(current=>({...current,title:event.target.value}))}/></label>
          <label>Objective<textarea value={draft.objective} onChange={event=>setDraft(current=>({...current,objective:event.target.value}))}/></label>
          <label>Preconditions<textarea value={draft.preconditions} onChange={event=>setDraft(current=>({...current,preconditions:event.target.value}))}/></label>
          <label>Steps<textarea value={draft.steps} onChange={event=>setDraft(current=>({...current,steps:event.target.value}))}/></label>
          <label>Expected result<textarea value={draft.expectedResult} onChange={event=>setDraft(current=>({...current,expectedResult:event.target.value}))}/></label>
        </>}
        <label>Why this procedure work is required<textarea value={draft.rationale} onChange={event=>setDraft(current=>({...current,rationale:event.target.value}))}/></label>
        <div className="downstreamDialogActions">
          <button type="button" className="quiet" disabled={busy} onClick={()=>{setProposing(false);setError('')}}>Cancel</button>
          <button type="button" disabled={busy||(!retiring&&(!draft.title.trim()||!draft.objective.trim()||!draft.steps.trim()))||(draft.kind!=='Introduce'&&!draft.baseNumber.trim())} onClick={()=>void propose()}>
            {busy?'Recording…':'Propose decision'}
          </button>
        </div>
      </section>
    </div>}
  </div>
}
