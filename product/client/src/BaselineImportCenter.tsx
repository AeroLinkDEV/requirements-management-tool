import { useCallback, useEffect, useState } from 'react'
import type { AuthUser } from './IdentityCenter'
import PortalHeader from './PortalHeader'
import { apiRequest, operationError } from './apiClient'
import './BaselineImportCenter.css'

type ImportState='Draft'|'Analysed'|'Mapped'|'Reconciled'|'Accepted'|'Abandoned'
type Summary={id:string;projectId:string;state:ImportState;carries:string;sourceSystem:string;sourceBaselineName:string;sourceBaselineDate:string;extractFileName:string;startedBy:string;startedAt:string;acceptedBy?:string;acceptedAt?:string;releaseId?:string}
type Detail=Summary&{sourceSystemVersion:string;extractSha256:string;extractSizeBytes:number;extractedBy:string;extractedAt:string;mappingJson:string;reconciliationJson:string;sourceRecordCount:number;sourceIdentityCount:number;sourceRecords:{inImportedBaseline:number;historyOnly:number};sourceHistoryEntryCount:number;asserts:string[];doesNotAssert:string}
type HistoryEntry={sourceBaselineName:string;statement:string;changedBy:string;changedAt?:string;sourceChangeReference:string}
type SourceRecord={id:string;sourceModule:string;sourceObjectKey:string;sourceIdentifier:string;inImportedBaseline:boolean;firstSeenAt:string;lastSeenAt:string;sourceHistory:HistoryEntry[]}
type RecordPage={total:number;returned:number;records:SourceRecord[]}
/// A search crosses every import in the Project, so a match names the system it came from — two programs
/// ported from different tools can both hold an object numbered 1234.
type SourceMatch={id:string;sourceSystem:string;sourceModule:string;sourceObjectKey:string;sourceIdentifier:string;inImportedBaseline:boolean;requirementRevisionId?:string;sourceHistory:HistoryEntry[]}
type SearchResult={total:number;returned:number;matches:SourceMatch[]}

/**
 * The five gates, in the order they run. Each is refused until the one before it has happened, so the rail
 * shows position rather than offering a choice — a gate you cannot reach is not a tab you forgot to click.
 */
const gates=[
  {key:'Draft',name:'Source',hint:'Extract accepted, hashed'},
  {key:'Analysed',name:'Analyse',hint:'What the extract contains'},
  {key:'Mapped',name:'Map',hint:'Levels, attributes, links'},
  {key:'Reconciled',name:'Reconcile',hint:'Account for every object'},
  {key:'Accepted',name:'Accept',hint:'Signed, creates baseline'},
] as const
const reached=(state:ImportState)=>state==='Abandoned'?-1:gates.findIndex(gate=>gate.key===state)
/**
 * Rendered in UTC, deliberately.
 *
 * Every date on this page is a provenance fact — the day a baseline was cut in the source system, the day
 * somebody took the extract, the day the source recorded a change. Formatting those in the reader's local
 * zone shifts them by a day for anyone west of UTC, so a baseline dated 30 June is read as 29 June. A record
 * whose whole purpose is fidelity to another system must not change depending on who opens it.
 */
const when=(value?:string)=>value
  ?new Date(value).toLocaleDateString(undefined,{year:'numeric',month:'short',day:'numeric',timeZone:'UTC'})
  :''
const megabytes=(bytes:number)=>`${(bytes/1_000_000).toFixed(1)} MB`
/// Shown in full on hover and in the accessible name. A truncated hash is a courtesy to the eye, never the
/// record — the whole point of holding it is that somebody can check the file against it years later.
const shortHash=(hash:string)=>`${hash.slice(0,8)}…${hash.slice(-8)}`
const carriesWords=(carries:string)=>carries.split(',').map(part=>part.trim())
  .map(part=>part==='TestProcedures'?'test procedures':part.toLowerCase()).join(' and ')

export default function BaselineImportCenter({user,api,projectId,onBackToBuilds,onSignOut}:{
  user:AuthUser
  api:string
  projectId:string
  onBackToBuilds:()=>void
  onSignOut:()=>void
}){
  const [rows,setRows]=useState<Summary[]>([])
  const [openId,setOpenId]=useState('')
  const [detail,setDetail]=useState<Detail>()
  const [page,setPage]=useState<RecordPage>()
  const [search,setSearch]=useState('')
  const [found,setFound]=useState<SearchResult>()
  const [error,setError]=useState('')
  const [busy,setBusy]=useState('')
  const [starting,setStarting]=useState(false)

  const loadList=useCallback(async()=>{
    try{setRows(await apiRequest<Summary[]>(`${api}/api/baseline-imports?projectId=${projectId}`))}
    catch(problem){setError(operationError(problem,'The imported baselines could not be loaded.'))}
  },[api,projectId])
  useEffect(()=>{loadList()},[loadList])

  const loadDetail=useCallback(async(id:string)=>{
    try{
      setDetail(await apiRequest<Detail>(`${api}/api/baseline-imports/${id}`))
      setPage(await apiRequest<RecordPage>(`${api}/api/baseline-imports/${id}/source-records`))
    }catch(problem){setError(operationError(problem,'That import could not be opened.'))}
  },[api])
  useEffect(()=>{if(openId)loadDetail(openId)},[openId,loadDetail])

  const runSearch=async(event:React.FormEvent)=>{
    event.preventDefault()
    setError('')
    try{
      setFound(await apiRequest(`${api}/api/source-identities?projectId=${projectId}&search=${encodeURIComponent(search)}`))
    }catch(problem){setError(operationError(problem,'That source identifier could not be looked up.'))}
  }

  const startImport=async(body:unknown)=>{
    setBusy('source');setError('')
    try{
      const created=await apiRequest<Detail>(`${api}/api/baseline-imports`,{
        method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...(body as object),projectId}),
      })
      setStarting(false);await loadList();setOpenId(created.id)
    }catch(problem){setError(operationError(problem,'That import could not be started.'))}
    finally{setBusy('')}
  }

  const advance=async(id:string,gate:string,body?:unknown)=>{
    setBusy(gate);setError('')
    try{
      await apiRequest(`${api}/api/baseline-imports/${id}/${gate}`,{
        method:'POST',
        ...(body?{headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}:{}),
      })
      await loadDetail(id);await loadList()
    }catch(problem){setError(operationError(problem,'That step could not be recorded.'))}
    finally{setBusy('')}
  }

  const position=detail?reached(detail.state):-1

  return (
    <div className="importCenter">
      <PortalHeader user={user} onSignOut={onSignOut}/>
      <main>
        <nav className="importCrumbs" aria-label="Breadcrumb">
          <button type="button" onClick={onBackToBuilds}>← Software Builds</button>
        </nav>
        <p className="eyebrow">Program setup / Bring in an existing program</p>
        <h1>Imported baselines</h1>
        <p className="lede">
          A program that already exists elsewhere is not a change request. Nobody here approved these
          requirements, so an import brings them in as an <b>externally sourced baseline</b> — recorded with
          its provenance, permanently marked as imported, and never presented as something this tool approved.
        </p>

        {error&&<p className="importError" role="alert">{error}</p>}

        <section className="importFind" aria-labelledby="find-heading">
          <h2 id="find-heading">Where did a source identifier go?</h2>
          <p>
            Every drawing, CDRL and test procedure outside this tool still names the source identifier, so it
            survives the import as a record of its own — including objects the source retired before the
            baseline that was imported.
          </p>
          <form onSubmit={runSearch}>
            <label htmlFor="source-search">Source identifier</label>
            <input id="source-search" value={search} onChange={event=>setSearch(event.target.value)} placeholder="SYS-01233"/>
            <button type="submit">Look it up</button>
          </form>
          {found&&(found.matches.length
            ?<>
              {/* Reported, never implied. A list quietly cut short is read as the answer, and the answer
                  people come here for is whether an identifier is still known at all. */}
              {found.total>found.matches.length&&<p className="importCapped">Showing {found.matches.length} of {found.total} matches.</p>}
              <ul className="importFound">
                {found.matches.map(match=>(
                  <li key={match.id}>
                    <b>{match.sourceIdentifier}</b>
                    <span>{match.sourceSystem} · {match.sourceModule} · object {match.sourceObjectKey}</span>
                    {match.inImportedBaseline
                      ?<em>In the imported baseline.</em>
                      /* False is the whole reason this record exists: an object gone before the imported
                         baseline is answerable, and joins nothing. */
                      :<em className="importRetired">Not in the imported baseline — the source retired it earlier. It is recorded so this question can be answered, and nothing originates from it.</em>}
                    {match.sourceHistory.length>0&&(
                      <ul className="importHistory">
                        {match.sourceHistory.map((entry,index)=>(
                          <li key={index}>
                            <b>{entry.sourceBaselineName}</b>
                            {entry.statement&&<span>{entry.statement}</span>}
                            <small>
                              {/* Recorded as the source reported it. A source that kept no author or date is
                                  described as it was found rather than filled in with something plausible. */}
                              {entry.changedBy||'author not recorded by the source'}
                              {entry.changedAt?` · ${when(entry.changedAt)}`:' · date not recorded by the source'}
                              {entry.sourceChangeReference&&` · ${entry.sourceChangeReference}`}
                            </small>
                          </li>
                        ))}
                      </ul>
                    )}
                  </li>
                ))}
              </ul>
            </>
            :<p className="importEmpty">No source identifier matching “{search}” has been recorded by an import in this Project.</p>)}
        </section>

        <section aria-labelledby="imports-heading">
          <div className="importSectionHead">
            <h2 id="imports-heading">Imports</h2>
            {/* Offered to everyone who can read this page. Whether somebody may actually start an import is
                the API's answer, and it holds the authority to give it — hiding the control on a guess about
                a role this page does not know would hide it from the configuration managers it is for. */}
            {!starting&&<button type="button" className="importPrimary" onClick={()=>setStarting(true)}>Import a baseline</button>}
          </div>
          {starting&&<StartImportForm busy={!!busy} onCancel={()=>setStarting(false)} onStart={startImport}/>}
          {rows.length===0&&!starting
            ?<p className="importEmpty">No program has been brought in from another tool yet.</p>
            :rows.length===0?null
            :<ul className="importList">
              {rows.map(row=>(
                <li key={row.id}>
                  <button type="button" aria-expanded={openId===row.id} onClick={()=>setOpenId(openId===row.id?'':row.id)}>
                    <b>{row.sourceBaselineName}</b>
                    <span>{row.sourceSystem} · {row.extractFileName}</span>
                    <em data-state={row.state}>{row.state==='Accepted'?`Accepted by ${row.acceptedBy}`:row.state}</em>
                  </button>
                </li>
              ))}
            </ul>}
        </section>

        {detail&&(
          <div className="importShell">
            <nav className="importRail" aria-label="Import stages">
              <p>Five gates</p>
              {gates.map((gate,index)=>(
                <div key={gate.key} data-state={detail.state==='Abandoned'?'locked':index<position?'done':index===position?'current':'locked'}
                  {...(index===position?{'aria-current':'step' as const}:{})}>
                  <span aria-hidden="true">{index<position?'✓':index+1}</span>
                  <span><b>{gate.name}</b><span>{gate.hint}</span></span>
                </div>
              ))}
            </nav>

            <div className="importMain">
              <section className="importCard">
                <header>
                  <div>
                    <h3>Where this came from</h3>
                    <p>Recorded permanently against the baseline this import creates. The hash is what makes the claim checkable later.</p>
                  </div>
                  <span className="importPill">Externally sourced</span>
                </header>
                <dl className="importProvenance">
                  <div><dt>Source system</dt><dd>{detail.sourceSystem}<small>{detail.sourceSystemVersion}</small></dd></div>
                  <div><dt>Source baseline</dt><dd>{detail.sourceBaselineName}<small>Baselined {when(detail.sourceBaselineDate)}</small></dd></div>
                  <div><dt>Extract file</dt><dd>{detail.extractFileName}<small>{megabytes(detail.extractSizeBytes)}</small></dd></div>
                  <div><dt>SHA-256</dt><dd className="importHash" title={detail.extractSha256} aria-label={`SHA-256 ${detail.extractSha256}`}>{shortHash(detail.extractSha256)}<small>Verified on upload</small></dd></div>
                  <div><dt>Extracted by</dt><dd>{detail.extractedBy}<small>{when(detail.extractedAt)}</small></dd></div>
                  <div><dt>Carries</dt><dd>{carriesWords(detail.carries)}<small>Started by {detail.startedBy}</small></dd></div>
                </dl>
              </section>

              <section className="importCard">
                <header>
                  <div>
                    <h3>What this import would do</h3>
                    <p>Every object the extract held is accounted for before anything is committed.</p>
                  </div>
                </header>
                <div className="importTiles">
                  <div><span>Objects accounted for</span><b>{detail.sourceRecordCount.toLocaleString()}</b><em>Recorded from the extract</em></div>
                  <div className="importTileOut"><span>In the imported baseline</span><b>{detail.sourceRecords.inImportedBaseline.toLocaleString()}</b><em>Become controlled requirements</em></div>
                  {/* Kept apart from the count above, because the two are different assertions: the first
                      becomes requirements this build carries, the second carries nothing. */}
                  <div className="importTileHistory"><span>Retired before this baseline</span><b>{detail.sourceRecords.historyOnly.toLocaleString()}</b><em>Answerable, joined to nothing</em></div>
                  <div><span>Source history entries</span><b>{detail.sourceHistoryEntryCount.toLocaleString()}</b><em>Reported, never asserted</em></div>
                </div>
              </section>

              {page&&page.records.length>0&&(
                <section className="importCard">
                  <header>
                    <div>
                      <h3>What the extract held</h3>
                      <p>The source identifier is kept forever — every drawing and test procedure at your company still says it.</p>
                    </div>
                  </header>
                  {page.total>page.returned&&<p className="importCapped">Showing {page.returned} of {page.total} objects.</p>}
                  <div className="importScroller">
                    <table>
                      <thead>
                        <tr>
                          <th scope="col">Source identifier</th><th scope="col">Module</th>
                          <th scope="col">Object</th><th scope="col">In this baseline</th><th scope="col">History</th>
                        </tr>
                      </thead>
                      <tbody>
                        {page.records.map(record=>(
                          <tr key={record.id}>
                            <td><code>{record.sourceIdentifier}</code></td>
                            <td>{record.sourceModule}</td>
                            <td className="importNumeric">{record.sourceObjectKey}</td>
                            <td>{record.inImportedBaseline?'Yes':'Retired earlier'}</td>
                            <td className="importNumeric">{record.sourceHistory.length||'—'}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              )}

              <section className="importCard">
                <header><div><h3>{detail.state==='Accepted'?'This import was accepted':'Accepting this import'}</h3></div></header>
                <div className="importAssert">
                  <h4>{detail.state==='Accepted'?'What the signature asserted':'What your signature will assert'}</h4>
                  <ul>{detail.asserts.map(claim=><li key={claim}>{claim}</li>)}</ul>
                  <p>
                    It does <b>not</b> assert that these requirements were reviewed or approved in AeroLink.
                    They were not. The baseline this creates is marked <span className="importPill">Externally sourced</span> wherever
                    it appears, and its provenance record — file hash, mapping, this reconciliation — travels
                    with it permanently.
                  </p>
                </div>

                {detail.state==='Accepted'
                  ?<p className="importAccepted">Accepted by {detail.acceptedBy} on {when(detail.acceptedAt)}. An accepted import is immutable — its baseline exists.</p>
                  :detail.state==='Abandoned'
                    ?<p className="importAccepted">This import was abandoned. Nothing was committed.</p>
                    :<GateActions detail={detail} busy={busy} onAdvance={advance}/>}
              </section>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}

/**
 * The Source gate: what the extract is, and where it came from.
 *
 * Every field here is provenance recorded permanently against the baseline the import creates, which is why
 * it is asked for before anything is read. The hash and size are typed rather than computed, because until
 * an extract can be uploaded there is no file on this side to compute them from — so the form says where to
 * get them rather than leaving somebody to guess at a 64-character field.
 */
function StartImportForm({busy,onCancel,onStart}:{
  busy:boolean
  onCancel:()=>void
  onStart:(body:unknown)=>void
}){
  const [form,setForm]=useState({
    sourceSystem:'IBM Rational DOORS',sourceSystemVersion:'',sourceBaselineName:'',sourceBaselineDate:'',
    extractFileName:'',extractSha256:'',extractSizeBytes:'',extractedBy:'',extractedAt:'',
  })
  const [carries,setCarries]=useState({Requirements:true,TestProcedures:false})
  const set=(key:keyof typeof form)=>(event:{target:{value:string}})=>setForm({...form,[key]:event.target.value})
  const kinds=Object.entries(carries).filter(([,on])=>on).map(([kind])=>kind)

  return (
    <form className="importCard importStart" onSubmit={event=>{
      event.preventDefault()
      onStart({
        ...form,
        // Dates are entered as days and recorded as instants in UTC, so the day stays the day it was.
        sourceBaselineDate:new Date(`${form.sourceBaselineDate}T00:00:00Z`).toISOString(),
        extractedAt:new Date(`${form.extractedAt}T00:00:00Z`).toISOString(),
        extractSizeBytes:Number(form.extractSizeBytes),
        carries:kinds,
      })
    }}>
      <header><div>
        <h3>Import a baseline</h3>
        <p>Recorded permanently against the baseline this creates. Nothing is read from the extract yet.</p>
      </div></header>

      <div className="importFields">
        <label>Source system<input required value={form.sourceSystem} onChange={set('sourceSystem')} placeholder="IBM Rational DOORS"/></label>
        <label>Source system version<input required value={form.sourceSystemVersion} onChange={set('sourceSystemVersion')} placeholder="9.6.1.13"/></label>
        <label>Source baseline name<input required value={form.sourceBaselineName} onChange={set('sourceBaselineName')} placeholder="FMS Sys Req v4.2"/></label>
        <label>Source baseline date<input required type="date" value={form.sourceBaselineDate} onChange={set('sourceBaselineDate')}/></label>
        <label>Extract file name<input required value={form.extractFileName} onChange={set('extractFileName')} placeholder="FMS_SYSTEM_REQUIREMENTS.reqifz"/></label>
        <label>Extract size in bytes<input required type="number" min={1} value={form.extractSizeBytes} onChange={set('extractSizeBytes')} placeholder="43842112"/></label>
        <label>Taken from the source by<input required value={form.extractedBy} onChange={set('extractedBy')} placeholder="m.chen"/></label>
        <label>Taken on<input required type="date" value={form.extractedAt} onChange={set('extractedAt')}/></label>
      </div>

      <label className="importWide">
        Extract SHA-256
        <input required pattern="[0-9a-fA-F]{64}" value={form.extractSha256} onChange={set('extractSha256')}
          placeholder="64 hexadecimal characters"/>
        {/* What makes "this is a true copy" checkable years later, so it is worth the trouble of getting it. */}
        <small>In PowerShell: <code>Get-FileHash -Algorithm SHA256 &lt;file&gt;</code></small>
      </label>

      <fieldset className="importWide">
        <legend>What this extract carries</legend>
        <label className="importCheck"><input type="checkbox" checked={carries.Requirements}
          onChange={event=>setCarries({...carries,Requirements:event.target.checked})}/>Requirements</label>
        <label className="importCheck"><input type="checkbox" checked={carries.TestProcedures}
          onChange={event=>setCarries({...carries,TestProcedures:event.target.checked})}/>Test procedures</label>
        {/* Declared now so requirements arriving from one system and test procedures from another stays open
            later. An import that carries nothing is not an import. */}
        {kinds.length===0&&<small>An import has to carry at least one kind of record.</small>}
      </fieldset>

      <div className="importActions">
        <button type="submit" className="importPrimary" disabled={busy||kinds.length===0}>Start the import</button>
        <button type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  )
}

/**
 * Only the next gate is offered.
 *
 * The API refuses a gate whose predecessor has not run, so showing all five as buttons would invite a
 * refusal the person could have been spared. What is missing is stated instead of being left as a control
 * that does nothing.
 */
function GateActions({detail,busy,onAdvance}:{
  detail:Detail
  busy:string
  onAdvance:(id:string,gate:string,body?:unknown)=>void
}){
  const [mapping,setMapping]=useState('')
  const [reconciliation,setReconciliation]=useState('')
  const [version,setVersion]=useState('')
  const {id,state}=detail

  if(state==='Draft')
    return <div className="importActions">
      <button type="button" disabled={!!busy} onClick={()=>onAdvance(id,'analysis')}>Record the analysis</button>
      <small>Nothing has been read from the extract yet.</small>
    </div>

  if(state==='Analysed')
    return <form className="importActions importForm" onSubmit={event=>{event.preventDefault();onAdvance(id,'mapping',{mappingJson:mapping})}}>
      <label htmlFor="mapping">Mapping — modules to levels, attributes to fields, link types to traces</label>
      <textarea id="mapping" rows={4} value={mapping} onChange={event=>setMapping(event.target.value)}
        placeholder={'{"modules":{"FMS_System_Requirements":"System"}}'} required/>
      <button type="submit" disabled={!!busy}>Record the mapping</button>
    </form>

  if(state==='Mapped')
    return <form className="importActions importForm" onSubmit={event=>{event.preventDefault();onAdvance(id,'reconciliation',{reconciliationJson:reconciliation})}}>
      <label htmlFor="reconciliation">Reconciliation — counts in against counts out, and every object not imported with the reason</label>
      <textarea id="reconciliation" rows={4} value={reconciliation} onChange={event=>setReconciliation(event.target.value)}
        placeholder={'{"objectsIn":5412,"requirementsOut":5180}'} required/>
      <button type="submit" disabled={!!busy||detail.sourceRecordCount===0}>Record the reconciliation</button>
      {/* Stated rather than left as a refusal from the server. Reconciling against no objects would be
          vacuously true, and would create an empty build claiming a program was brought in from elsewhere. */}
      {detail.sourceRecordCount===0&&<small>This import has not been told what the extract held, so there is nothing to reconcile yet.</small>}
    </form>

  return <form className="importActions importForm" onSubmit={event=>{event.preventDefault();onAdvance(id,'accept',{version})}}>
    <label htmlFor="version">Name the build this import becomes</label>
    <input id="version" value={version} onChange={event=>setVersion(event.target.value)} placeholder="1.0" required/>
    <button type="submit" className="importPrimary" disabled={!!busy}>Accept and create baseline</button>
    <small>The build is released on arrival: its review, approval and verification belong to the source's own release.</small>
  </form>
}
