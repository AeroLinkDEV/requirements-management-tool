import { useCallback, useEffect, useState } from 'react'
import { PersonName } from './People'
import { ApiError, apiRequest, operationError } from './apiClient'
import { RichCaseField, RichContentView } from './RichContent'
import { fromPlainText, toPlainText } from './richContentModel'
// The requirements queue's stylesheet, imported rather than copied. The testing side is meant to be the same
// surface for the same kind of work, and a second stylesheet that merely looked like it would drift the first
// time either was touched.
import './DownstreamAssessmentQueue.css'

type Kind='Introduce'|'Modify'|'Retire'
type ProcedureChange={id:string;displayNumber:string;baseNumber:string;revision:number;kind:Kind;level:string;title:string;objective:string;preconditions:string;steps:string;expectedResult:string;rationale:string;drivingRequirementRevisionIds:string[];removedRequirementRevisionIds:string[];coverageChangeRationale:string;coverageChangedBy:string}
/** A requirement this package's changes touched, which a procedure here may be written against. */
type RequirementChoice={id:string;revisionId:string;displayNumber:string;statement:string;level:string}
/** A controlled procedure a Modify or Retire may target, with the revision it actually sits at. */
type CurrentCoverage={id:string;revisionId:string;displayNumber:string;statement:string;level:string;isSuspect:boolean}
type ProcedureTarget={baseNumber:string;title:string;currentRevision:number;currentCoverage:CurrentCoverage[]}
type Capabilities={canProposeProcedureChange:boolean;canWithdrawProcedureChange:boolean;canRevise:boolean}
type Package={id:string;displayNumber:string;baseNumber:string;revision:number;discipline:string;state:string;outcome:string;procedureLevel:string;sourceChangeRequestNumber:string;assignedEngineerId?:string;version:number;caseContractVersion:number;title:string;problem:string;analysis:string;solution:string;problemRich:string;analysisRich:string;solutionRich:string;procedureChanges:ProcedureChange[];capabilities:Capabilities;drivingRequirementChoices:RequirementChoice[];procedureTargets:ProcedureTarget[]}

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
const missingCaseFields=(item:Package)=>[
  ['Title',item.title],['Problem',item.problem],['Analysis',item.analysis],['Solution',item.solution],
].filter(([,value])=>!value?.trim()).map(([name])=>name)

const emptyDraft={kind:'Introduce' as Kind,baseNumber:'',revision:0,title:'',objective:'',preconditions:'',steps:'',expectedResult:'',rationale:'',driving:[] as string[],removed:[] as string[],coverageRationale:''}

/**
 * The room where test procedures are actually created, modified and retired.
 *
 * The test-side counterpart of the change request editor: a test change request carries procedure decisions
 * exactly as a change request carries requirement changes, so this is shaped like that editor rather than
 * invented. Nothing proposed here is a controlled procedure revision until the package is approved and
 * materialised into a build.
 */
export default function TestChangeRequestWorkspace({api,projectId,reviewId,canAuthor,onClose,onChanged,onOpenRequirementRevision}:{api:string;projectId:string;reviewId:string;canAuthor:boolean;onClose:()=>void;onChanged:()=>void;onOpenRequirementRevision:(requirement:{id:string;revisionId:string;level:string})=>void}){
  const [item,setItem]=useState<Package>()
  const [busy,setBusy]=useState(false),[error,setError]=useState('')
  const [draft,setDraft]=useState(emptyDraft),[proposing,setProposing]=useState(false)
  const [editingCase,setEditingCase]=useState(false)
  const [caseTitle,setCaseTitle]=useState('')
  const [caseProblemRich,setCaseProblemRich]=useState(fromPlainText(''))
  const [caseAnalysisRich,setCaseAnalysisRich]=useState(fromPlainText(''))
  const [caseSolutionRich,setCaseSolutionRich]=useState(fromPlainText(''))
  const [caseVersion,setCaseVersion]=useState<number|undefined>(undefined)

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
      // The requirements this procedure is written against. Without them the procedure revision cannot be
      // bound to what caused it, and the decision that asked for it never settles.
      drivingRequirementRevisionIds:draft.driving,
      removedRequirementRevisionIds:draft.removed,
      coverageChangeRationale:draft.coverageRationale,
      expectedVersion:item?.version}
    if(await act(()=>apiRequest(`${api}/api/test-change-reviews/${reviewId}/procedure-changes`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}))){
      setProposing(false);setDraft(emptyDraft)
    }
  }
  const withdraw=(changeId:string)=>void act(()=>apiRequest(
    `${api}/api/test-change-reviews/${reviewId}/procedure-changes/${changeId}?expectedVersion=${item?.version}`,
    {method:'DELETE'}))
  const revise=()=>void act(()=>apiRequest(`${api}/api/test-change-reviews/${reviewId}/revise`,{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'}))
  const saveCase=async()=>{
    setBusy(true);setError('')
    try{
      await apiRequest(`${api}/api/test-change-reviews/${reviewId}/case`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
        title:caseTitle.trim(),
        problem:toPlainText(caseProblemRich),
        analysis:toPlainText(caseAnalysisRich),
        solution:toPlainText(caseSolutionRich),
        problemRich:caseProblemRich,
        analysisRich:caseAnalysisRich,
        solutionRich:caseSolutionRich,
        expectedVersion:caseVersion,
      })})
      setEditingCase(false);await load();onChanged()
    }catch(problem){
      if(problem instanceof ApiError && problem.status===409 && problem.code==='stale_version'){
        setError('This package changed in another session. Your edits are preserved here; copy them, refresh, and reapply.')
      }else{
        setError(operationError(problem,'The test change request case could not be saved.'))
      }
    }
    finally{setBusy(false)}
  }
  const openCaseEditor=()=>{
    if(!item)return
    setCaseTitle(item.title??'');setCaseProblemRich(item.problemRich||fromPlainText(item.problem||''))
    setCaseAnalysisRich(item.analysisRich||fromPlainText(item.analysis||''));setCaseSolutionRich(item.solutionRich||fromPlainText(item.solution||''))
    setCaseVersion(item.version);setError('');setEditingCase(true)
  }

  // Answered by the server, not inferred from a broad role here. The workspace was offering authoring to
  // anyone with test authority while the endpoints refused anyone but the engineer holding the package.
  const open=item?.state==='Open'
  const mayEdit=canAuthor&&(item?.capabilities?.canProposeProcedureChange??false)
  const mayEditCase=canAuthor&&(item?.capabilities?.canWithdrawProcedureChange??false)
  const retiring=draft.kind==='Retire'
  const selectedTarget=(item?.procedureTargets??[]).find(x=>x.baseNumber===draft.baseNumber)
  const currentCoverage=selectedTarget?.currentCoverage??[]
  const currentCoverageIds=new Set(currentCoverage.map(x=>x.revisionId))
  const governedIds=new Set((item?.drivingRequirementChoices??[]).map(x=>x.revisionId))
  const addedCoverage=draft.driving.filter(id=>!currentCoverageIds.has(id))
  const coverageDeltaChanged=draft.kind==='Modify'&&(addedCoverage.length>0||draft.removed.length>0)
  const finalCoverageCount=currentCoverage.filter(x=>!draft.removed.includes(x.revisionId)).length+addedCoverage.length
  const missingRequiredCoverage=draft.kind==='Introduce'
    ?draft.driving.length===0
    :draft.kind==='Modify'&&draft.baseNumber!==''&&finalCoverageCount===0
  const requirementDetails=(revisionId:string)=>
    (item?.drivingRequirementChoices??[]).find(x=>x.revisionId===revisionId)
      ??(item?.procedureTargets??[]).flatMap(x=>x.currentCoverage).find(x=>x.revisionId===revisionId)
  const requirementLabel=(revisionId:string)=>{
    const known=requirementDetails(revisionId)
    return known?`${known.displayNumber} · ${known.statement}`:revisionId
  }
  const requirementLinks=(ids:string[])=>ids.map((revisionId,index)=>{
    const known=requirementDetails(revisionId)
    return known
      ? <span key={revisionId}>{index>0?', ':''}<button type="button" className="drawerArtifactLink" onClick={()=>onOpenRequirementRevision(known)}>{known.displayNumber}</button> · {known.statement}</span>
      : <span key={revisionId}>{index>0?', ':''}{revisionId}</span>
  })
  const procedureCoverageDelta=(change:ProcedureChange)=>{
    const current=(item?.procedureTargets??[]).find(x=>x.baseNumber===change.baseNumber)?.currentCoverage??[]
    const removed=new Set(change.removedRequirementRevisionIds)
    const currentIds=new Set(current.map(x=>x.revisionId))
    const retained=current.map(x=>x.revisionId).filter(id=>!removed.has(id))
    const added=change.drivingRequirementRevisionIds.filter(id=>!currentIds.has(id))
    return {retained,added,removed:change.removedRequirementRevisionIds,final:[...retained,...added]}
  }

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
        <section className="tcrCaseSection">
          <h3>Engineering case</h3>
          {missingCaseFields(item).length>0&&item.state==='Open'&&<p className="drawerEmpty">Missing before review: {missingCaseFields(item).join(', ')}. This package cannot be sent for approval until the complete engineering case is recorded.</p>}
          {missingCaseFields(item).length>0&&item.caseContractVersion===0&&item.state!=='Open'&&<p className="drawerEmpty">This historical package predates the complete engineering-case contract. Its recorded content is shown unchanged; no case text has been fabricated.</p>}
          {item.title
            ? <dl className="sourceCase">
                <div><dt>Title</dt><dd>{item.title}</dd></div>
                <div><dt>Problem</dt><dd><RichContentView api={api} value={item.problemRich} empty={item.problem || 'Not recorded'} /></dd></div>
                <div><dt>Analysis</dt><dd><RichContentView api={api} value={item.analysisRich} empty={item.analysis || 'Not recorded'} /></dd></div>
                <div><dt>Solution</dt><dd><RichContentView api={api} value={item.solutionRich} empty={item.solution || 'Not recorded'} /></dd></div>
              </dl>
            : <p className="drawerEmpty">No engineering case is recorded for this package.</p>}
          {mayEditCase&&<button type="button" className="linkedScr" disabled={busy} onClick={openCaseEditor}>{missingCaseFields(item).length?'Write engineering case':'Edit case'}</button>}
        </section>

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
                  {change.kind==='Introduce'&&<span>Verified requirements: {requirementLinks(change.drivingRequirementRevisionIds)}</span>}
                  {change.kind==='Modify'&&<>
                    <span>Retained coverage: {procedureCoverageDelta(change).retained.map(requirementLabel).join(', ')||'none'}</span>
                    <span>Added coverage: {procedureCoverageDelta(change).added.map(requirementLabel).join(', ')||'none'}</span>
                    <span>Removed coverage: {procedureCoverageDelta(change).removed.map(requirementLabel).join(', ')||'none'}</span>
                    <span>Approved final coverage: {procedureCoverageDelta(change).final.map(requirementLabel).join(', ')||'none'}</span>
                  </>}
                  {change.coverageChangeRationale&&<span>Coverage rationale: {change.coverageChangeRationale} · {change.coverageChangedBy}</span>}
                  {mayEdit&&<button type="button" className="linkedScr" disabled={busy} onClick={()=>withdraw(change.id)}>Withdraw this decision</button>}
                </li>)}</ul>
            : <p className="drawerEmpty">No procedure decisions are proposed yet. A test change request exists because test work is required, so it is not finished until it says what that work is.</p>}
          <div className="drawerDecisionActions">
            {mayEdit&&<button type="button" disabled={busy} onClick={()=>{setDraft(emptyDraft);setProposing(true)}}>Propose a procedure change</button>}
            {/* Revising is the test-side twin of revising a change request: reopening approved work to correct
                it, which carries the existing decisions into the next revision. */}
            {canAuthor&&(item.capabilities?.canRevise??false)&&<button type="button" className="quiet reopenAssessment" disabled={busy} onClick={revise}>Revise this test change request</button>}
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
          <select value={draft.kind} onChange={event=>setDraft(current=>({...current,
            kind:event.target.value as Kind,baseNumber:'',revision:0,driving:[],removed:[],coverageRationale:''}))}>
            <option value="Introduce">Introduce a new procedure</option>
            <option value="Modify">Modify an existing procedure</option>
            <option value="Retire">Retire an existing procedure</option>
          </select>
        </label>
        {/* Introducing allocates the number centrally, so it is not asked for; anything else has to name the
            procedure it acts on, because allocating a fresh number for a modification would quietly make it a
            different procedure. */}
        {draft.kind!=='Introduce'&&<>
          {/* Chosen from the controlled library, not typed. A number entered by hand could be a typo, another
              project's, or the wrong level, and it survived approval to fail at materialization — which turns
              an authoring mistake into a release-path problem. Selecting also fixes the revision, so the
              engineer is not asked to know what the procedure currently sits at. */}
          <label>Procedure
            <select value={draft.baseNumber} onChange={event=>{
              const target=(item?.procedureTargets??[]).find(x=>x.baseNumber===event.target.value)
              setDraft(current=>({...current,baseNumber:event.target.value,revision:(target?.currentRevision??-1)+1,
                driving:[],removed:[],coverageRationale:''}))
            }}>
              <option value="">Choose the procedure this acts on…</option>
              {(item?.procedureTargets??[]).map(target=>
                <option value={target.baseNumber} key={target.baseNumber}>
                  {target.baseNumber}.{String(Math.max(target.currentRevision,0)).padStart(2,'0')} · {target.title}
                </option>)}
            </select>
          </label>
          {draft.baseNumber&&<p className="drawerEmpty">This becomes revision {String(draft.revision).padStart(2,'0')}.</p>}
        </>}
        {!retiring&&<>
          <label>Title<input value={draft.title} onChange={event=>setDraft(current=>({...current,title:event.target.value}))}/></label>
          <label>Objective<textarea value={draft.objective} onChange={event=>setDraft(current=>({...current,objective:event.target.value}))}/></label>
          <label>Preconditions<textarea value={draft.preconditions} onChange={event=>setDraft(current=>({...current,preconditions:event.target.value}))}/></label>
          <label>Steps<textarea value={draft.steps} onChange={event=>setDraft(current=>({...current,steps:event.target.value}))}/></label>
          <label>Expected result<textarea value={draft.expectedResult} onChange={event=>setDraft(current=>({...current,expectedResult:event.target.value}))}/></label>
        </>}
        {draft.kind==='Modify'&&draft.baseNumber&&<fieldset className="drivingRequirements">
          <legend>Current exact coverage</legend>
          {currentCoverage.length?currentCoverage.map(coverage=>{
            const mayRemove=governedIds.has(coverage.revisionId)
            const retained=!draft.removed.includes(coverage.revisionId)
            return <label key={coverage.revisionId} className="drivingChoice">
              <input type="checkbox" checked={retained} disabled={!mayRemove}
                onChange={event=>setDraft(current=>({...current,removed:event.target.checked
                  ?current.removed.filter(id=>id!==coverage.revisionId)
                  :[...current.removed,coverage.revisionId]}))}/>
              <span><b>{coverage.displayNumber}</b> {coverage.statement} · {coverage.isSuspect?'Suspect':'Confirmed'}
                {!mayRemove?' · retained; outside this package change scope':''}</span>
            </label>
          }):<p className="drawerEmpty">The carried procedure has no current requirement coverage.</p>}
          <p className="drawerEmpty">Checked links carry forward automatically. Clearing an authorized link is an explicit removal.</p>
        </fieldset>}
        {/* What this procedure is written against. Only the requirements this package's own changes touched
            are offered — a procedure verifies what its change request altered, not anything in the project —
            and the link is what lets the decision that asked for the procedure settle when it arrives. */}
        {!retiring&&<fieldset className="drivingRequirements">
          <legend>Requirements this procedure verifies</legend>
          {(item?.drivingRequirementChoices??[]).length
            ?(item?.drivingRequirementChoices??[]).map(choice=>
              <label key={choice.revisionId} className="drivingChoice">
                <input type="checkbox" checked={draft.driving.includes(choice.revisionId)}
                  onChange={event=>setDraft(current=>({...current,
                    driving:event.target.checked
                      ?[...current.driving,choice.revisionId]
                      :current.driving.filter(id=>id!==choice.revisionId)}))}/>
                <span><b>{choice.displayNumber}</b> {choice.statement}</span>
              </label>)
            :<p className="drawerEmpty">This build has not materialized its requirements yet, so there is no exact revision to write against. Introduce and Modify decisions wait until exact requirement revisions are available.</p>}
          {missingRequiredCoverage&&<p className="drawerEmpty" role="alert">{draft.kind==='Introduce'
            ?'Select at least one exact requirement this new procedure verifies.'
            :'A modified procedure must retain or add at least one exact requirement. Retire it instead if it verifies nothing in this build.'}</p>}
        </fieldset>}
        {coverageDeltaChanged&&<label>Why coverage is being added or removed
          <textarea value={draft.coverageRationale} onChange={event=>setDraft(current=>({...current,coverageRationale:event.target.value}))}/>
        </label>}
        {draft.kind==='Modify'&&draft.baseNumber&&<p className="drawerEmpty">
          Proposed coverage: {currentCoverage.length-draft.removed.length} retained, {addedCoverage.length} added, {draft.removed.length} removed.
        </p>}
        <label>Why this procedure work is required<textarea value={draft.rationale} onChange={event=>setDraft(current=>({...current,rationale:event.target.value}))}/></label>
        <div className="downstreamDialogActions">
          <button type="button" className="quiet" disabled={busy} onClick={()=>{setProposing(false);setError('')}}>Cancel</button>
          <button type="button" disabled={busy||missingRequiredCoverage||(!retiring&&(!draft.title.trim()||!draft.objective.trim()||!draft.steps.trim()))||(draft.kind!=='Introduce'&&!draft.baseNumber.trim())||(coverageDeltaChanged&&!draft.coverageRationale.trim())} onClick={()=>void propose()}>
            {busy?'Recording…':'Propose decision'}
          </button>
        </div>
      </section>
    </div>}

    {editingCase&&item&&<div className="downstreamDialogBackdrop" role="presentation">
      <section className="downstreamDecisionDialog" role="dialog" aria-modal="true" aria-labelledby="tcr-case-title">
        <p className="eyebrow">TEST CHANGE REQUEST CASE</p>
        <h2 id="tcr-case-title">Edit the case of {item.displayNumber}</h2>
        <p>What the reviewer is asked to judge. The case is fixed once the package is with its approver.</p>
        {error&&<div className="workspaceError" role="alert">{error}</div>}
        <label>Title
          <input value={caseTitle} onChange={event=>setCaseTitle(event.target.value)} placeholder="What this package is for" />
        </label>
        <div className="tcrCaseFields">
          <RichCaseField api={api} projectId={projectId} label="Problem" value={caseProblemRich}
            onChange={setCaseProblemRich} placeholder="What is affected and why this package exists" />
          <RichCaseField api={api} projectId={projectId} label="Analysis" value={caseAnalysisRich}
            onChange={setCaseAnalysisRich} placeholder="What was considered and what it means for the procedures" />
          <RichCaseField api={api} projectId={projectId} label="Solution" value={caseSolutionRich}
            onChange={setCaseSolutionRich} placeholder="What controlled outcome is proposed" />
        </div>
        <div className="downstreamDialogActions">
          <button type="button" className="quiet" disabled={busy} onClick={()=>{setEditingCase(false);setError('')}}>Cancel</button>
          <button type="button" disabled={busy||!caseTitle.trim()} onClick={()=>void saveCase()}>{busy?'Saving…':'Save case'}</button>
        </div>
      </section>
    </div>}
  </div>
}
