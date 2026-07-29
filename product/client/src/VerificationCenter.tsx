import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { PersonName } from './People'
import { SignatureDialog } from './IdentityCenter'
import type { AuthUser } from './IdentityCenter'
import ControlledProcedureEditor from './ControlledProcedureEditor'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import './VerificationCenter.css'
import './EvidenceUpload.css'

type Baseline={id:string;displayNumber:string;name:string;state:string;requirementsMaterializedAt?:string}
type Build={id:string;buildNumber:string;baselineId:string;version:string}
type Requirement={revisionId:string;displayNumber:string;statement:string}
type Procedure={id:string;revisionId:string;displayNumber:string;title:string;ownerId:string;state:string;objective:string;requirementCount:number;lastOutcome?:string}
type Execution={id:string;procedureRevisionId:string;displayNumber:string;title:string;outcome:string;executedBy:string;determination:string;evidenceReference:string;executedAt:string;retestOfExecutionId?:string;evidence:{id:string;originalFileName:string;size:number;sha256:string}[]}
type Coverage={total:number;covered:number;verified:number;uncovered:number;items:{revisionId:string;displayNumber:string;statement:string;covered:boolean;verified:boolean;coveredBy:{procedureId:string;revisionId:string;displayNumber:string;title:string;state:string;isSuspect:boolean;coverageState:"Confirmed"|"Suspect";latestOutcome?:string;latestExecutionId?:string}[]}[]}
type ImpactItem={id:string;trigger:string;state:string;subjectDisplayNumber:string;declaredVerificationMethod:string;requirementRevisionId?:string;procedureId?:string;assignedEngineerId?:string;assignedByLeadId?:string;assignedAt?:string;outcome?:string;resolutionRationale:string;resolvedBy?:string;resolvedAt?:string;raisedAt:string;blocksBaselineApproval:boolean;resolvedProcedure?:{id:string;revisionId:string;displayNumber:string;title:string;level:string;state:string;configuration:{requirementRevisionId?:string;procedureRevisionId:string}};decisionHistory:{id:string;action:string;outcome?:string;procedureId?:string;procedureRevisionId?:string;rationale:string;actor:string;occurredAt:string}[]}
type CorrectiveAction={problemReportId:string;problemReportNumber:string;available:boolean;discipline:string|null;reason:string;executionId?:string;procedureId?:string;procedureRevisionId?:string;procedureNumber?:string;procedureTitle?:string;requiredRole:string}
type Props={api:string;programId:string;projectId:string;releaseId:string;scope:'System'|'Software';user:AuthUser;correctiveProblemReportId?:string;onBack:()=>void}

/**
 * Current local wall time, in the format a `datetime-local` control expects.
 *
 * The field was seeded by truncating an ISO UTC string, which produces something that looks right and is
 * wrong by the reader's offset — the control reads whatever it is given as local wall time. In Toronto a run
 * recorded at 23:20 prefilled as 03:20 the following day, moving the calendar date as well as the hour. The
 * submit path already converts local input to an exact instant and the history already renders that instant
 * back in local time; only the default value was ever wrong.
 */
const localWallTimeNow=()=>{
 const now=new Date(),pad=(value:number)=>String(value).padStart(2,'0')
 return `${now.getFullYear()}-${pad(now.getMonth()+1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`
}

export default function VerificationCenter({api,programId,projectId,releaseId,scope,user,correctiveProblemReportId,onBack}:Props){
 const [baselines,setBaselines]=useState<Baseline[]>([]),[baselineId,setBaselineId]=useState(''),[builds,setBuilds]=useState<Build[]>([]),[buildId,setBuildId]=useState(''),[requirements,setRequirements]=useState<Requirement[]>([]),[procedures,setProcedures]=useState<Procedure[]>([]),[executions,setExecutions]=useState<Execution[]>([]),[coverage,setCoverage]=useState<Coverage>(),[creating,setCreating]=useState(false),[recording,setRecording]=useState<Procedure>(),[approving,setApproving]=useState<Procedure>(),[editing,setEditing]=useState<Procedure>(),[retest,setRetest]=useState<Execution>(),[error,setError]=useState(''),[mutationBusy,setMutationBusy]=useState(false)
 // This used to open on coverage, on the reasoning that it is the orientation view and the tab badge would do
 // the signalling. The badge was not enough: somebody arriving to do verification work saw a table of
 // everything in the release and no sign of the items the last approval had just made their problem.
 //
 // Opens on the work an approved change created, not on the coverage inventory. The inventory answers "what
 // does this release contain"; the queue answers "what has to happen next, and whose job is it" — and only the
 // second is a reason to come here on a Tuesday morning. Landing on coverage meant a verification engineer saw
 // a table of everything and no sign of the four things the last approval had just made their problem.
 const workspaceTabs=['impact','coverage','procedures','executions'] as const
 const initialTab=(typeof location!=='undefined'?new URLSearchParams(location.search).get('tab'):null)
 const [workspaceTab,setWorkspaceTab]=useState<typeof workspaceTabs[number]>(workspaceTabs.includes(initialTab as typeof workspaceTabs[number])?initialTab as typeof workspaceTabs[number]:'impact')
 const [impact,setImpact]=useState<ImpactItem[]>([]),[resolving,setResolving]=useState<ImpactItem>(),[assigning,setAssigning]=useState<ImpactItem>(),[reopening,setReopening]=useState<ImpactItem>()
 const [requirementQuery,setRequirementQuery]=useState(''),[selectedRequirementIds,setSelectedRequirementIds]=useState<string[]>([])
 // Procedure browsing is server-side: 440 software procedures rendered as cards with no way to find one.
 //
 // The state is seeded from the address and written back to it, so a filtered list can be refreshed, shared
 // or reached with the browser's back button — a worklist somebody cannot hand to a colleague is half a
 // worklist. Discrete changes push a history entry; typing replaces one, because a keystroke is not a place.
  const lastDiscreteState=useRef<string|null>(null)
  const suppressNextPush=useRef(false)
 const initialProcedureQuery=typeof location!=='undefined'?new URLSearchParams(location.search):new URLSearchParams()
 const [procedureQuery,setProcedureQuery]=useState(initialProcedureQuery.get('procedure')??''),[procedureState,setProcedureState]=useState(initialProcedureQuery.get('procedureState')??''),[procedureOutcome,setProcedureOutcome]=useState(initialProcedureQuery.get('procedureOutcome')??''),[procedurePage,setProcedurePage]=useState(Number(initialProcedureQuery.get('procedurePage')??'1')||1),[procedureTotal,setProcedureTotal]=useState(0),[procedurePages,setProcedurePages]=useState(1)
 // Seeded from what the address already says, so the reader's first change after a reload still earns a
 // history entry rather than being mistaken for arrival.
 if(lastDiscreteState.current===null)lastDiscreteState.current=`${procedureState}|${procedureOutcome}|${procedurePage}|${workspaceTab}`
 useEffect(()=>{
  const params=new URLSearchParams(location.search)
  const before=params.toString()
  const apply=(key:string,value:string,fallback='')=>{if(value&&value!==fallback)params.set(key,value);else params.delete(key)}
  apply('procedure',procedureQuery);apply('procedureState',procedureState);apply('procedureOutcome',procedureOutcome)
  apply('procedurePage',procedurePage>1?String(procedurePage):'')
  // The tab travels too, or a shared link restores the filters into a view that is not showing them.
  apply('tab',workspaceTab==='impact'?'':workspaceTab)
  if(params.toString()===before)return
  const next=`${location.pathname}${params.toString()?`?${params}`:''}`
  // Choosing a filter, a page or a tab is somewhere the reader went, so it earns a history entry and the
  // back button returns to the previous list. Typing in the search box is not somewhere they went; pushing
  // per keystroke would mean pressing back a dozen times to leave one search.
  const discrete=`${procedureState}|${procedureOutcome}|${procedurePage}|${workspaceTab}`
  // The exception is arriving from a problem report, which switches the tab for the reader. That is part of
  // the navigation that brought them here, not a move they made, so it must not consume their back button.
  const push=discrete!==lastDiscreteState.current&&!suppressNextPush.current
  suppressNextPush.current=false
  lastDiscreteState.current=discrete
  if(push)history.pushState({},'',next);else history.replaceState({},'',next)
 },[procedureQuery,procedureState,procedureOutcome,procedurePage,workspaceTab])
 // The browser's own navigation must move the list, not just the address bar.
 useEffect(()=>{
  const restore=()=>{
   const params=new URLSearchParams(location.search)
   setProcedureQuery(params.get('procedure')??'');setProcedureState(params.get('procedureState')??'')
   setProcedureOutcome(params.get('procedureOutcome')??'');setProcedurePage(Number(params.get('procedurePage')??'1')||1)
   const tab=params.get('tab');if(workspaceTabs.includes(tab as typeof workspaceTabs[number]))setWorkspaceTab(tab as typeof workspaceTabs[number]);else setWorkspaceTab('impact')
  }
  addEventListener('popstate',restore)
  return()=>removeEventListener('popstate',restore)
 },[])
 const [outcome,setOutcome]=useState<'Pass'|'Fail'|'Blocked'>('Pass')
 const selectedProgram=user.programs.find(program=>program.programId===programId)
 const canTest=user.isAdministrator||!!selectedProgram?.roles.includes('TestEngineer')
 const canApprove=user.isAdministrator||!!selectedProgram?.roles.includes('Approver')
 const canLead=user.isAdministrator||!!selectedProgram?.roles.includes('TestLead')
 const canDecideImpact=canTest||canLead
 const hasMaterializedBaseline=baselines.length>0
 const load=useCallback(async()=>{const [a,b,c,d]=await Promise.all([fetch(`${api}/api/baselines?projectId=${projectId}&releaseId=${releaseId}`),fetch(`${api}/api/builds?projectId=${projectId}`),fetch(`${api}/api/test-procedures?projectId=${projectId}&scope=${scope}&search=${encodeURIComponent(procedureQuery)}&state=${procedureState}&outcome=${procedureOutcome}&page=${procedurePage}&pageSize=25`),fetch(`${api}/api/test-executions?projectId=${projectId}${buildId?`&buildId=${buildId}`:''}`)]);const bs:Baseline[]=a.ok?await a.json():[];setBaselines(bs.filter(x=>x.requirementsMaterializedAt));if(b.ok)setBuilds(await b.json());if(c.ok){const paged=await c.json();setProcedures(paged.items);setProcedureTotal(paged.totalCount);setProcedurePages(paged.totalPages)}if(d.ok)setExecutions(await d.json());setBaselineId(current=>current||bs.find(x=>x.requirementsMaterializedAt)?.id||'')},[api,projectId,releaseId,buildId,scope,procedureQuery,procedureState,procedureOutcome,procedurePage])
 const loadCoverage=useCallback(async()=>{if(!baselineId&&!buildId)return;const key=buildId?`buildId=${buildId}`:`baselineId=${baselineId}`;const [a,b]=await Promise.all([fetch(`${api}/api/verification-coverage?projectId=${projectId}&${key}`),fetch(`${api}/api/requirements?projectId=${projectId}&baselineId=${baselineId||builds.find(x=>x.id===buildId)?.baselineId}&scope=${scope}&includeRetired=false&page=1&pageSize=200`)]);if(a.ok){const raw:Coverage=await a.json(),items=raw.items.filter(x=>scope==='System'?x.displayNumber.startsWith('SYSR-'):!x.displayNumber.startsWith('SYSR-'));setCoverage({...raw,items,total:items.length,covered:items.filter(x=>x.covered).length,verified:items.filter(x=>x.verified).length,uncovered:items.filter(x=>!x.covered).length})}if(b.ok)setRequirements((await b.json()).items)},[api,projectId,baselineId,buildId,builds,scope])
 const loadImpact=useCallback(async()=>{const response=await fetch(`${api}/api/releases/${releaseId}/verification-impact`);if(response.ok)setImpact(await response.json())},[api,releaseId])
 useEffect(()=>{load()},[load]);useEffect(()=>{loadCoverage()},[loadCoverage]);useEffect(()=>{loadImpact()},[loadImpact])
 // Arriving from a problem report. The workspace opens on the procedure that failed and says which record it
 // is correcting, rather than on a generic change-impact tab with nothing selected.
 const [corrective,setCorrective]=useState<CorrectiveAction>()
 useEffect(()=>{
  if(!correctiveProblemReportId){setCorrective(undefined);return}
  let cancelled=false
  ;(async()=>{
   const response=await fetch(`${api}/api/problem-reports/${correctiveProblemReportId}/corrective-action`)
   if(!response.ok||cancelled)return
   const target=await response.json() as CorrectiveAction
   if(cancelled)return
   setCorrective(target)
   suppressNextPush.current=true
   setWorkspaceTab('procedures')
  })()
  return()=>{cancelled=true}
 },[api,correctiveProblemReportId])
 useEffect(()=>{if(!canTest){setCreating(false);setRecording(undefined);setRetest(undefined)}if(!canApprove)setApproving(undefined)},[canTest,canApprove,programId])
 const createProcedure=async(e:FormEvent<HTMLFormElement>)=>{e.preventDefault();setError('');if(!canTest){setError('Test Engineer authority is required in the selected Program.');return}const form=new FormData(e.currentTarget);if(!selectedRequirementIds.length){setError('Select at least one exact requirement revision.');return}if(mutationBusy)return;setMutationBusy(true);try{const created=await apiRequest<{displayNumber?:string;baseNumber?:string}>(`${api}/api/test-procedures`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId,baseNumber:'SERVER-ALLOCATED',title:form.get('title'),objective:form.get('objective'),preconditions:form.get('preconditions'),steps:form.get('steps'),expectedResult:form.get('expectedResult'),requirementRevisionIds:selectedRequirementIds,level:form.get('level')})});setCreating(false);setSelectedRequirementIds([]);setRequirementQuery('');setWorkspaceTab('procedures');
  // The list is paged now, and a new procedure takes the highest controlled number — so it lands on the
  // last page and the author would not see what they just made. Show it by finding it.
  setProcedureQuery((created?.baseNumber??created?.displayNumber??'').replace(/\.\d{2}$/,''));setProcedureState('');setProcedureOutcome('');setProcedurePage(1);await loadCoverage()}catch(error){recordClientOperationFailure('verification.procedure.create',error);setError(operationError(error,'Procedure could not be created.'))}finally{setMutationBusy(false)}}
 const startRecording=(procedure?:Procedure,prior?:Execution)=>{if(!canTest){setError('Test Engineer authority is required in the selected Program.');return}if(!procedure){setError('The approved procedure revision could not be resolved for this result.');return}setOutcome('Pass');setRetest(prior);setRecording(procedure)}
 const record=async(e:FormEvent<HTMLFormElement>)=>{e.preventDefault();if(!recording||!canTest){setError('Test Engineer authority is required in the selected Program.');return}if(mutationBusy)return;const form=new FormData(e.currentTarget);setMutationBusy(true);setError('');try{await apiRequest(`${api}/api/test-executions`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId,procedureRevisionId:recording.revisionId,softwareBuildId:buildId||null,retestOfExecutionId:retest?.id||null,outcome,configuration:form.get('configuration'),determination:form.get('determination'),evidenceReference:form.get('evidenceReference'),executedAt:new Date(String(form.get('executedAt'))).toISOString()})});setRecording(undefined);setRetest(undefined);setWorkspaceTab('executions');await load();await loadCoverage()}catch(error){recordClientOperationFailure('verification.result.record',error);setError(operationError(error,'Result could not be recorded.'))}finally{setMutationBusy(false)}}
 const upload=async(execution:Execution,file:File)=>{if(!canTest){setError('Test Engineer authority is required to attach evidence in the selected Program.');return}if(mutationBusy)return;const form=new FormData();form.append('file',file);form.append('projectId',projectId);setMutationBusy(true);setError('');try{const evidence=await apiRequest<{id:string}>(`${api}/api/evidence`,{method:'POST',body:form});await apiRequest(`${api}/api/test-executions/${execution.id}/evidence/${evidence.id}`,{method:'POST'});await load();await loadCoverage()}catch(error){recordClientOperationFailure('verification.evidence.upload',error);setError(operationError(error,'Evidence could not be stored and linked to this execution.'))}finally{setMutationBusy(false)}}
 const approve=async(password:string,meaning:string)=>{if(!approving||!canApprove){setError('Approver authority is required in the selected Program.');return}if(mutationBusy)return;setMutationBusy(true);setError('');try{await apiRequest(`${api}/api/test-procedures/${approving.revisionId}/approve`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password,meaning})});setApproving(undefined);await load();await loadCoverage()}catch(error){recordClientOperationFailure('verification.procedure.approve',error);setError(operationError(error,'Procedure approval could not be recorded.'))}finally{setMutationBusy(false)}}
 const assignImpact=async(item:ImpactItem,engineerId:string)=>{if(mutationBusy)return;setMutationBusy(true);setError('');try{await apiRequest(`${api}/api/verification-impact/${item.id}/assign`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({engineerId})});setAssigning(undefined);await loadImpact()}catch(error){recordClientOperationFailure('verification.impact.assign',error);setError(operationError(error,'The item could not be assigned.'))}finally{setMutationBusy(false)}}
 const resolveImpact=async(e:FormEvent<HTMLFormElement>)=>{e.preventDefault();if(!resolving||mutationBusy)return;setMutationBusy(true);setError('');const form=new FormData(e.currentTarget);const outcomeValue=String(form.get('outcome'));const procedureId=String(form.get('procedureId')||'');try{await apiRequest(`${api}/api/verification-impact/${resolving.id}/resolve`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({outcome:outcomeValue,rationale:form.get('rationale'),procedureId:outcomeValue==='ProcedureCoverageConfirmed'?procedureId:null})});setResolving(undefined);await loadImpact();await loadCoverage()}catch(error){recordClientOperationFailure('verification.impact.resolve',error);setError(operationError(error,'The decision could not be recorded.'))}finally{setMutationBusy(false)}}
 const reopenImpact=async(e:FormEvent<HTMLFormElement>)=>{e.preventDefault();if(!reopening||mutationBusy)return;setMutationBusy(true);setError('');const form=new FormData(e.currentTarget);try{await apiRequest(`${api}/api/verification-impact/${reopening.id}/reopen`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({rationale:form.get('rationale')})});setReopening(undefined);await loadImpact();await loadCoverage()}catch(error){recordClientOperationFailure('verification.impact.reopen',error);setError(operationError(error,'The decision could not be reopened.'))}finally{setMutationBusy(false)}}
 // Which procedure the reader was sent to look at, so it can be marked when the procedures tab opens.
 const [procedureFocus,setProcedureFocus]=useState('')
 const outstandingImpact=impact.filter(x=>x.blocksBaselineApproval)
 const mineImpact=outstandingImpact.filter(x=>x.assignedEngineerId===user.userName)
 /**
  * The queue, grouped by the thing that created it.
  *
  * Every item here exists because an approved change made it exist, and the three causes need different work:
  * a new requirement needs a procedure written, a changed one needs its existing procedure reconfirmed against
  * new wording, and a retirement leaves a procedure covering nothing. Ordered so the group that blocks the
  * release soonest reads first.
  */
 const impactGroups=[
  {trigger:'RequirementIntroduced',heading:'New requirements need a procedure',
   hint:'An approved change introduced these. Nothing verifies them yet.',
   items:impact.filter(x=>x.trigger==='RequirementIntroduced'&&x.blocksBaselineApproval)},
  {trigger:'RequirementModified',heading:'Changed requirements — coverage is suspect until reconfirmed',
   hint:'The wording moved under an existing procedure. Open the procedure and judge whether it still verifies the requirement.',
   items:impact.filter(x=>x.trigger==='RequirementModified'&&x.blocksBaselineApproval)},
  {trigger:'ProcedureOrphaned',heading:'Retired requirements leave procedures covering nothing',
   hint:'The requirement these verified is no longer effective in this release.',
   items:impact.filter(x=>x.trigger==='ProcedureOrphaned'&&x.blocksBaselineApproval)},
  {trigger:'RequirementIntroducedResolved',heading:'New requirement verification decisions',
   hint:'Recorded procedure coverage and explicit no-test determinations for introduced requirements.',
   items:impact.filter(x=>x.trigger==='RequirementIntroduced'&&!x.blocksBaselineApproval)},
  {trigger:'RequirementModifiedResolved',heading:'Changed requirement verification decisions',
   hint:'Recorded applicability decisions for changed exact requirement revisions.',
   items:impact.filter(x=>x.trigger==='RequirementModified'&&!x.blocksBaselineApproval)},
  {trigger:'ProcedureOrphanedResolved',heading:'Retired requirement procedure decisions',
   hint:'Recorded dispositions for procedures left without an effective requirement.',
   items:impact.filter(x=>x.trigger==='ProcedureOrphaned'&&!x.blocksBaselineApproval)},
 ]
 const impactLabels:Record<string,string>={RequirementIntroduced:'New requirement',RequirementModified:'Modified requirement',ProcedureOrphaned:'Orphaned procedure'}
 const visibleRequirements=requirements.filter(x=>!requirementQuery.trim()||`${x.displayNumber} ${x.statement}`.toLowerCase().includes(requirementQuery.trim().toLowerCase())).slice(0,40)
 return <main className="verificationPage"><header><div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">VERIFICATION CONTROL / EXTERNAL EXECUTION</p><h1>Verification & Evidence</h1><p>Control procedures, coverage, human result determinations, evidence, and retest history.</p></div><button disabled={!canTest||!hasMaterializedBaseline} title={!canTest?'Test Engineer authority is required in this Program.':!hasMaterializedBaseline?'Materialize the candidate requirement baseline before creating a procedure.':undefined} onClick={()=>setCreating(true)}>+ New Test Procedure</button></header>{error&&<div className="workspaceError" role="alert" aria-live="assertive">{error}</div>}
 <section className="verificationContext"><label>Materialized baseline<select value={baselineId} onChange={e=>{setBaselineId(e.target.value);setBuildId('')}}><option value="">Select baseline…</option>{baselines.map(x=><option value={x.id} key={x.id}>{x.displayNumber} · {x.name}</option>)}</select></label><label>Software build (optional result context)<select value={buildId} onChange={e=>{setBuildId(e.target.value);if(e.target.value)setBaselineId(builds.find(x=>x.id===e.target.value)?.baselineId||'')}}><option value="">Baseline-wide coverage</option>{builds.map(x=><option value={x.id} key={x.id}>{x.buildNumber}</option>)}</select></label></section>
 {!hasMaterializedBaseline&&<section className="materializationPrerequisite" role="status"><div><b>Procedure authoring waits for materialization</b><p>This release has no immutable requirement revisions yet, so a new procedure cannot be bound to an exact target. Existing inherited procedures remain visible against their predecessor revisions; planned work for new or modified requirements remains in Change impact and cannot count as confirmed coverage yet.</p></div><div><span>Next governed step</span><b>Open Product Versions, select the approved changes, then freeze and materialize the candidate baseline.</b></div></section>}
 {coverage&&<section className="coverageMetrics"><article><b>{coverage.total}</b><span>Effective requirements</span></article><article><b>{coverage.covered}</b><span>With procedure coverage</span></article><article><b>{coverage.verified}</b><span>Latest result Pass</span></article><article className={coverage.uncovered?'warning':''}><b>{coverage.uncovered}</b><span>Coverage gaps</span></article></section>}
 {creating&&<form className="procedureForm" onSubmit={createProcedure}><div className="procedureLead"><div><h2>Create {scope} test procedure</h2><p>The server assigns the next controlled identifier and records {user.displayName} as the authenticated author.</p></div><span>{selectedRequirementIds.length} requirement{selectedRequirementIds.length===1?'':'s'} selected</span></div><label>Test level<select name="level">{scope==='System'?<option value="System">System Test</option>:<><option value="HighLevel">HLR Test</option><option value="LowLevel">LLR Test</option></>}</select></label><label className="wide">Title<input name="title" required/></label><label className="wide">Objective<textarea name="objective" required/></label><label>Preconditions<textarea name="preconditions"/></label><label>Expected result<textarea name="expectedResult" required/></label><label className="wide">Procedure steps<textarea name="steps" required/></label><fieldset className="wide requirementPicker"><legend>Exact requirements covered</legend><input aria-label="Search requirements to cover" value={requirementQuery} onChange={e=>setRequirementQuery(e.target.value)} placeholder="Search identifier or statement…"/><small>Showing {visibleRequirements.length} of {requirements.length}. Selected revisions remain attached while you search.</small>{visibleRequirements.map(x=><label className="reqCheck" key={x.revisionId}><input type="checkbox" checked={selectedRequirementIds.includes(x.revisionId)} onChange={()=>setSelectedRequirementIds(ids=>ids.includes(x.revisionId)?ids.filter(id=>id!==x.revisionId):[...ids,x.revisionId])}/><span><b>{x.displayNumber}</b>{x.statement}</span></label>)}{!visibleRequirements.length&&<div className="verificationEmpty"><b>No matching requirements</b><span>Try another identifier or statement fragment.</span></div>}</fieldset><div><button type="button" className="outline" disabled={mutationBusy} onClick={()=>{setCreating(false);setSelectedRequirementIds([]);setRequirementQuery('')}}>Cancel</button><button disabled={!selectedRequirementIds.length||mutationBusy}>{mutationBusy?'Creating Procedure…':'Create Procedure'}</button></div></form>}
 {recording&&<form className="resultForm" onSubmit={record}><div><h2>{retest?'Record retest':'Record external test result'}</h2><p>{recording.displayNumber} · {recording.title}</p></div><label>Outcome<select name="outcome" value={outcome} onChange={e=>setOutcome(e.target.value as 'Pass'|'Fail'|'Blocked')}><option>Pass</option><option>Fail</option><option>Blocked</option></select></label><label>Executed by / human determination owner<input value={`${user.displayName} (${user.userName})`} readOnly aria-readonly="true"/><small>Recorded from the authenticated session; it cannot be reassigned.</small></label><label>Execution time<input type="datetime-local" name="executedAt" defaultValue={localWallTimeNow()} required/><small>Local time, {Intl.DateTimeFormat().resolvedOptions().timeZone}. Stored as an exact instant.</small></label><label>Configuration<textarea name="configuration" placeholder="Hardware, software build, tools, environment…"/></label><label>Evidence reference<input name="evidenceReference" required={outcome!=='Blocked'} aria-required={outcome!=='Blocked'} placeholder={outcome==='Blocked'?'Optional when execution is blocked':'Required file path, evidence ID, or repository URL'}/><small>{outcome==='Blocked'?'Optional for a blocked run.':'Required for Pass and Fail results.'}</small></label><label className="wide">Human determination<textarea name="determination" placeholder="Why this run passed, failed, or was blocked" required/></label><div><button type="button" className="outline" disabled={mutationBusy} onClick={()=>{setRecording(undefined);setRetest(undefined)}}>Cancel</button><button disabled={mutationBusy}>{mutationBusy?'Recording result…':'Record immutable result'}</button></div></form>}
 {workspaceTab==='procedures'&&canTest&&procedures.some(x=>x.state==='Draft')&&<section className="draftEditorLaunch"><div><b>Controlled Draft editor</b><span>Open a Draft procedure with an exclusive lease, server recovery, and atomic check-in.</span></div><select aria-label="Choose Draft test procedure" onChange={e=>setEditing(procedures.find(x=>x.revisionId===e.target.value))} value={editing?.revisionId||''}><option value="">Choose a Draft procedure…</option>{procedures.filter(x=>x.state==='Draft').map(x=><option value={x.revisionId} key={x.revisionId}>{x.displayNumber} · {x.title}</option>)}</select></section>}
 {corrective&&<section className="correctiveBanner" role="status" aria-label="Corrective verification action"><div><p className="eyebrow">CORRECTING {corrective.problemReportNumber}</p><b>{corrective.procedureNumber?`Record a passing successor execution against ${corrective.procedureNumber}`:"Record a passing successor execution"}</b><p>{corrective.reason}</p></div>{canTest?(corrective.procedureId&&procedures.some(x=>x.id===corrective.procedureId)?<button onClick={()=>startRecording(procedures.find(x=>x.id===corrective.procedureId))}>Record successor execution →</button>:<span className="correctiveHint">Choose the procedure below, then record its result.</span>):<span className="correctiveHint">Recording an execution needs the {corrective.requiredRole} role in this Program. {corrective.problemReportNumber} stays selected here so this can be handed to somebody who holds it.</span>}</section>}
 <nav className="verificationTabs" aria-label="Verification workspace"><button className={workspaceTab==='impact'?'active':''} onClick={()=>setWorkspaceTab('impact')}>Change impact <span className={outstandingImpact.length?'attention':''}>{outstandingImpact.length}</span></button><button className={workspaceTab==='coverage'?'active':''} onClick={()=>setWorkspaceTab('coverage')}>Requirement coverage <span>{coverage?.total??0}</span></button><button className={workspaceTab==='procedures'?'active':''} onClick={()=>setWorkspaceTab('procedures')}>Test procedures <span>{procedures.length}</span></button><button className={workspaceTab==='executions'?'active':''} onClick={()=>setWorkspaceTab('executions')}>Execution history <span>{executions.length}</span></button></nav>
 <div className="verificationFocus">
  {workspaceTab==='impact'&&<section className="verificationCard">
   <div className="cardTitle"><h2>Change impact</h2><p>Verification owed by approved changes to this release. Raised on approval, before any baseline exists.</p></div>
   <div className="impactSummary">
    <article><b>{outstandingImpact.length}</b><span>Undecided</span></article>
    <article><b>{mineImpact.length}</b><span>Assigned to you</span></article>
    <article><b>{impact.length-outstandingImpact.length}</b><span>Decided</span></article>
    <article className={outstandingImpact.length?'held':'clear'}><b>{outstandingImpact.length?'Held':'Clear'}</b><span>Release gate</span></article>
   </div>
   {outstandingImpact.length>0&&<div className="impactGate" role="status">
    <b>{outstandingImpact.length} decision{outstandingImpact.length===1?'':'s'} outstanding</b>
    <span>The release cannot be approved until every new or modified requirement has an approved procedure or a recorded decision that no test is required. Freezing and materializing the baseline is unaffected — that is what creates the requirement revisions a procedure is written against.</span>
   </div>}
   {/* Grouped by what created the work, because the three causes need different things done to them: a new
       requirement needs a procedure written, a changed one needs its existing procedure reconfirmed, and a
       retired one needs a link taken away. A flat list made a reader sort that out per row. */}
   {impactGroups.map(group=>group.items.length>0&&<div className="impactGroup" key={group.trigger}>
    <div className="impactGroupHead" data-trigger={group.trigger}>
     <b>{group.heading}</b>
     <span>{group.items.length}</span>
    </div>
    <p className="impactGroupHint">{group.hint}</p>
   {group.items.map(x=><article className={`impactRow ${x.blocksBaselineApproval?'open':'resolved'}`} key={x.id}>
    <div className="impactHead">
     <b>{x.subjectDisplayNumber}</b>
     <i className={x.trigger==='RequirementIntroduced'?'new':x.trigger==='RequirementModified'?'modified':'orphan'}>{impactLabels[x.trigger]??x.trigger}</i>
     {x.blocksBaselineApproval?<span className="impactState blocking">{x.state==='Assigned'?`Assigned · ${x.assignedEngineerId}`:'Unassigned'}</span>
      :<span className="impactState done">{x.outcome==='NoTestRequired'?'No test required':x.outcome==='ProcedureCoverageConfirmed'?'Coverage confirmed':x.outcome==='ProcedureRetired'?'Procedure retired':'Procedure retained'}</span>}
    </div>
    {x.declaredVerificationMethod&&<p className="impactMethod">Author declared <b>{x.declaredVerificationMethod}</b>{x.blocksBaselineApproval?' — a verification engineer still confirms what testing this needs.':'. The governed decision evidence is recorded below.'}</p>}
    {x.assignedEngineerId&&<p className="impactAssignment">Assigned to <PersonName userName={x.assignedEngineerId}/>{x.assignedByLeadId&&<> by <PersonName userName={x.assignedByLeadId}/></>}{x.assignedAt&&<> · {new Date(x.assignedAt).toLocaleString()}</>}</p>}
    {x.resolvedProcedure&&<section className="impactDecisionEvidence covered"><span>Exact procedure coverage</span><b>{x.resolvedProcedure.displayNumber} · {x.resolvedProcedure.title}</b><small>{x.resolvedProcedure.level} · {x.resolvedProcedure.state} · revision {x.resolvedProcedure.revisionId}</small><small>Requirement configuration {x.resolvedProcedure.configuration.requirementRevisionId??'awaiting materialization'}</small><button className="outline" onClick={()=>{setWorkspaceTab('procedures');setProcedureFocus(x.resolvedProcedure?.id??'')}}>Open selected procedure →</button></section>}
    {x.outcome==='NoTestRequired'&&<section className="impactDecisionEvidence waived"><span>No-test determination</span><b>Verification accepted without a test procedure</b><small>This is an attributable engineering determination, not procedure coverage.</small></section>}
    {x.resolutionRationale&&<p className="impactRationale">{x.resolutionRationale}<small>Decided by <PersonName userName={x.resolvedBy} />{x.resolvedAt&&<> · {new Date(x.resolvedAt).toLocaleString()}</>}</small></p>}
    {!!x.decisionHistory.length&&<details className="impactHistory"><summary>Decision history · {x.decisionHistory.length}</summary>{x.decisionHistory.map(entry=><article key={entry.id}><b>{entry.action==='Reopened'?'Decision reopened':entry.outcome==='ProcedureCoverageConfirmed'?'Coverage confirmed':entry.outcome==='NoTestRequired'?'No test required':entry.outcome}</b><span><PersonName userName={entry.actor}/> · {new Date(entry.occurredAt).toLocaleString()}</span><p>{entry.rationale}</p>{entry.procedureRevisionId&&<code>{entry.procedureRevisionId}</code>}</article>)}</details>}
    {x.blocksBaselineApproval&&<div className="impactActions">
     {canLead&&<button className="outline" onClick={()=>setAssigning(x)}>Assign…</button>}
     {/* The procedure is opened and read before anything is confirmed. Judging whether a procedure still
         verifies a requirement whose wording has moved is not a decision anybody should make from a row in a
         list, so this takes the engineer to the procedure rather than offering a shortcut past it. */}
     {x.procedureId&&<button className="outline" onClick={()=>{setWorkspaceTab('procedures');setProcedureFocus(x.procedureId??'')}}>Open procedure →</button>}
     {canDecideImpact&&<button onClick={()=>setResolving(x)}>Record decision…</button>}
     {!canDecideImpact&&<span className="impactHint">Verification authority is required to decide this.</span>}
    </div>}
    {!x.blocksBaselineApproval&&canDecideImpact&&<div className="impactActions"><button className="outline" onClick={()=>setReopening(x)}>Reopen / change decision…</button></div>}
   </article>)}
   </div>)}
   {!impact.length&&<div className="verificationEmpty"><b>No verification impact for this release</b><span>Approving a change that introduces or modifies a requirement raises work here.</span></div>}
  </section>}
  {workspaceTab==='coverage'&&<section className="verificationCard"><div className="cardTitle"><h2>Requirement coverage</h2><p>Exact, version-aware links for the selected configuration.</p></div>{coverage?.items.map(x=>{const suspect=x.coveredBy.some(p=>p.isSuspect);return <article className={`coverageRow${suspect&&!x.covered?' suspect':''}`} key={x.revisionId}><div><b>{x.displayNumber}</b><i className={x.verified?'pass':x.covered?'covered':suspect?'suspect':'gap'}>{x.verified?'Verified':x.covered?'Covered':suspect?'Suspect':'Gap'}</i></div><p>{x.statement}</p>{x.coveredBy.map(p=><small key={p.revisionId}>{p.displayNumber} · {p.title} · {p.isSuspect?'Suspect applicability — not confirmed coverage':p.latestOutcome||'Not executed'}</small>)}{suspect&&!x.covered&&<button onClick={()=>setWorkspaceTab('impact')}>Resolve in Change impact →</button>}</article>})}{coverage&&!coverage.items.length&&<div className="verificationEmpty"><b>No effective requirements in this view</b><span>Choose a materialized baseline or another verification scope.</span></div>}</section>}
  {workspaceTab==='procedures'&&<section className="verificationCard"><div className="cardTitle"><h2>Test procedures</h2><p>Reusable controlled revisions and their latest result.</p></div><div className="procedureFilters"><label className="procedureSearch"><span>⌕</span><input aria-label="Find a procedure" value={procedureQuery} placeholder="Procedure number or title" onChange={e=>{setProcedureQuery(e.target.value);setProcedurePage(1)}}/></label><select aria-label="Procedure state filter" value={procedureState} onChange={e=>{setProcedureState(e.target.value);setProcedurePage(1)}}><option value="">All states</option><option value="Draft">Draft</option><option value="InReview">In review</option><option value="Approved">Approved</option></select><select aria-label="Latest result filter" value={procedureOutcome} onChange={e=>{setProcedureOutcome(e.target.value);setProcedurePage(1)}}><option value="">All outcomes</option><option value="Pass">Pass</option><option value="Fail">Fail</option><option value="Blocked">Blocked</option></select><span className="procedureCount">{procedureTotal} procedure{procedureTotal===1?"":"s"}</span></div>{procedures.map(x=><article className={`procedureRow ${procedureFocus===x.id?"focused":""}`} key={x.id}><div><b>{x.displayNumber}</b><i className={x.state==='Draft'?'draft':''}>{x.state==='Draft'?'Awaiting approval':x.lastOutcome||'Not run'}</i></div><p>{x.title}</p><small>{x.requirementCount} exact requirement links · {x.state} · authored by <PersonName userName={x.ownerId}/></small>{x.state==='Approved'?(canTest?<button onClick={()=>startRecording(x)}>Record result</button>:<span className="procedureHold">Test Engineer authority is required to record results in this Program.</span>):canApprove&&x.ownerId!==user.userName?<button onClick={()=>setApproving(x)}>Review & approve</button>:<span className="procedureHold">{x.ownerId===user.userName?'Independent approval is required before execution.':'Approver authority is required in this Program.'}</span>}</article>)}{!procedures.length&&<div className="verificationEmpty"><b>{procedureQuery||procedureState||procedureOutcome?"No procedure matches these filters":`No ${scope.toLowerCase()} procedures`}</b><span>{procedureQuery||procedureState||procedureOutcome?"Clear the search or the filters to see the rest.":canTest?"Create the first procedure from an exact requirement revision.":"Test Engineer authority is required to create procedures in this Program."}</span></div>}{procedurePages>1&&<div className="procedurePager"><button disabled={procedurePage<=1} onClick={()=>setProcedurePage(p=>Math.max(1,p-1))}>Previous</button><span>Page {procedurePage} of {procedurePages}</span><button disabled={procedurePage>=procedurePages} onClick={()=>setProcedurePage(p=>Math.min(procedurePages,p+1))}>Next</button></div>}</section>}
  {workspaceTab==='executions'&&<section className="verificationCard"><div className="cardTitle"><h2>Execution history</h2><p>Immutable determinations, evidence, and retest lineage.</p></div>{executions.map(x=><article className="executionRow" key={x.id}><div><b>{x.displayNumber}</b><i className={x.outcome.toLowerCase()}>{x.outcome}</i></div><p>{x.determination}</p><small><PersonName userName={x.executedBy} /> · {new Date(x.executedAt).toLocaleString()}</small><small>Reference: {x.evidenceReference||'Blocked before evidence produced'}</small>{x.evidence.map(e=><small key={e.id}>✓ {e.originalFileName} · SHA-256 {e.sha256.slice(0,12)}…</small>)}{canTest&&<label className="evidenceUpload">Upload evidence<input type="file" onChange={e=>{const file=e.target.files?.[0];if(file)upload(x,file)}}/></label>}{canTest&&x.outcome!=='Pass'&&<button onClick={()=>startRecording(procedures.find(p=>p.revisionId===x.procedureRevisionId),x)}>Record retest</button>}</article>)}{!executions.length&&<div className="verificationEmpty"><b>No recorded executions</b><span>Choose a procedure and record its first external result.</span></div>}</section>}
 </div>
 {approving&&<SignatureDialog title={`Approve ${approving.displayNumber}`} meaning="I approve this exact test procedure revision for controlled verification use." onCancel={()=>setApproving(undefined)} onSign={approve}/>}
{assigning&&<div className="impactDialog" role="dialog" aria-label="Assign verification work"><form onSubmit={e=>{e.preventDefault();assignImpact(assigning,String(new FormData(e.currentTarget).get('engineerId')||''))}}><h2>Assign {assigning.subjectDisplayNumber}</h2><p>Distribute this decision to a verification engineer.</p><label>Verification engineer<input name="engineerId" required placeholder="username"/></label><div><button type="button" className="outline" onClick={()=>setAssigning(undefined)}>Cancel</button><button>Assign</button></div></form></div>}
 {resolving&&<div className="impactDialog" role="dialog" aria-label="Record verification decision"><form onSubmit={resolveImpact}><h2>Decide {resolving.subjectDisplayNumber}</h2><p>{impactLabels[resolving.trigger]??resolving.trigger}{resolving.declaredVerificationMethod?` · author declared ${resolving.declaredVerificationMethod}`:''}</p>
  <label>Decision<select name="outcome" defaultValue={resolving.trigger==='ProcedureOrphaned'?'ProcedureRetired':'ProcedureCoverageConfirmed'}>{resolving.trigger==='ProcedureOrphaned'?<><option value="ProcedureRetired">Procedure retired</option><option value="ProcedureRetained">Procedure deliberately retained</option></>:<><option value="ProcedureCoverageConfirmed">An approved procedure covers this</option><option value="NoTestRequired">No test required</option></>}</select></label>
  {resolving.trigger!=='ProcedureOrphaned'&&<label>Covering procedure<select name="procedureId" defaultValue=""><option value="">Select an approved procedure…</option>{procedures.filter(x=>x.state==='Approved').map(x=><option value={x.id} key={x.id}>{x.displayNumber} · {x.title}</option>)}</select><small>Required when confirming coverage. Only approved procedures are accepted.</small></label>}
 <label className="wide">Rationale<textarea name="rationale" required placeholder="Why this is the right verification decision"/></label>
  <div><button type="button" className="outline" onClick={()=>setResolving(undefined)}>Cancel</button><button>Record decision</button></div></form></div>}
 {reopening&&<div className="impactDialog" role="dialog" aria-label="Reopen verification decision"><form onSubmit={reopenImpact}><h2>Reopen {reopening.subjectDisplayNumber}</h2><p>The current decision remains in immutable history. Reopening returns this item to the release gate and any selected coverage to suspect.</p><label className="wide">Reopen rationale<textarea name="rationale" required placeholder="Why the recorded decision must be reconsidered"/></label><div><button type="button" className="outline" onClick={()=>setReopening(undefined)}>Cancel</button><button>Reopen decision</button></div></form></div>}
 {editing&&<ControlledProcedureEditor api={api} procedure={editing} onClose={()=>setEditing(undefined)} onCommitted={async()=>{await load();await loadCoverage()}}/>}
 </main>
}
