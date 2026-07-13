import { useCallback, useEffect, useState } from 'react'
import './LifecycleExplorer.css'
import './ControlledDownloads.css'

type Baseline={id:string;displayNumber:string;name:string;requirementsMaterializedAt?:string}
type Document={id:string;type:string;displayNumber:string;title:string;contentHash:string;artifactCount:number;release:string;baseline:string;generatedAt:string}
type TraceEvidence={id:string;originalFileName:string;sha256:string;size:number;uploadedAt:string}
type TraceExecution={id:string;outcome:string;executedBy:string;executedAt:string;determination:string;evidenceReference:string;evidence:TraceEvidence[]}
type TraceTest={procedureId:string;revisionId:string;displayNumber:string;title:string;level:string;executions:TraceExecution[]}
type TraceRelation={id:string;displayNumber:string;level:string;type:string}
type Trace={id:string;revisionId:string;displayNumber:string;level:string;statement:string;testCount:number;parents:TraceRelation[];children:TraceRelation[];tests:TraceTest[]}
type Props={api:string;projectId:string;releases:{id:string;version:string}[];onBack:()=>void}

export default function LifecycleExplorer({api,projectId,releases,onBack}:Props){
 const [tab,setTab]=useState<'trace'|'documents'>('trace')
 const [baselines,setBaselines]=useState<Baseline[]>([])
 const [baselineId,setBaselineId]=useState('')
 const [documents,setDocuments]=useState<Document[]>([])
 const [traces,setTraces]=useState<Trace[]>([])
 const [query,setQuery]=useState('')
 const [total,setTotal]=useState(0)
 const load=useCallback(async()=>{
  const lists=await Promise.all(releases.map(r=>fetch(`${api}/api/baselines?projectId=${projectId}&releaseId=${r.id}`).then(x=>x.json())))
  const bs:Baseline[]=lists.flat().filter((x:Baseline)=>x.requirementsMaterializedAt)
  setBaselines(bs)
  const chosen=baselineId||bs[0]?.id||''
  if(!baselineId&&chosen)setBaselineId(chosen)
  const [d,t]=await Promise.all([
   fetch(`${api}/api/documents?projectId=${projectId}`),
   chosen?fetch(`${api}/api/traceability?projectId=${projectId}&baselineId=${chosen}&search=${encodeURIComponent(query)}&page=1&pageSize=200`):undefined
  ])
  if(d.ok)setDocuments(await d.json())
  if(t?.ok){const body=await t.json();setTraces(body.items);setTotal(body.totalCount)}
 },[api,projectId,releases,baselineId,query])
 useEffect(()=>{const timer=setTimeout(load,150);return()=>clearTimeout(timer)},[load])
 const generate=async()=>{if(!baselineId)return;await fetch(`${api}/api/baselines/${baselineId}/generate-documents`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({actorId:'configuration.manager'})});await load();setTab('documents')}
 const traverse=(relation:TraceRelation)=>setQuery(relation.displayNumber.replace(/\.\d{2}$/,''))
 return <main className="lifecyclePage">
  <header><div><button className="back" onClick={onBack}>← Command Center</button><p className="eyebrow">CONFIGURATION INDEX / CONTROLLED OUTPUTS</p><h1>Traceability &amp; Documents</h1><p>Navigate exact requirement derivation, verification procedures, results, and immutable evidence.</p></div></header>
  <div className="lifeTabs"><button className={tab==='trace'?'active':''} onClick={()=>setTab('trace')}>Requirement Traceability</button><button className={tab==='documents'?'active':''} onClick={()=>setTab('documents')}>Controlled Documents <span>{documents.length}</span></button></div>
  {tab==='trace'?<>
   <section className="traceTools"><select value={baselineId} onChange={e=>setBaselineId(e.target.value)}>{baselines.map(x=><option value={x.id} key={x.id}>{x.displayNumber} · {x.name}</option>)}</select><input value={query} onChange={e=>setQuery(e.target.value)} placeholder="Search any identifier fragment…"/><b>{total.toLocaleString()} requirements</b><div className="downloadLinks"><a href={`${api}/api/traceability/${baselineId}/download?format=pdf`}>Generate trace PDF</a><a href={`${api}/api/traceability/${baselineId}/download?format=docx`}>Generate trace DOCX</a></div></section>
   <section className="traceList">{traces.map(x=><article key={x.revisionId}>
    <div className="traceIdentity"><b>{x.displayNumber}</b><i>{x.level}</i><span>{x.testCount} test link{x.testCount===1?'':'s'}</span></div><p>{x.statement}</p>
    <div className="traceRelations"><div><small>PARENT / DERIVED FROM</small>{x.parents.map(p=><button key={p.id} onClick={()=>traverse(p)}>{p.displayNumber} · {p.level}</button>)}{!x.parents.length&&<em>Top-level requirement</em>}</div><div><small>CHILDREN / SATISFIED BY</small>{x.children.slice(0,8).map(c=><button key={c.id} onClick={()=>traverse(c)}>{c.displayNumber} · {c.level}</button>)}{x.children.length>8&&<em>+ {x.children.length-8} additional children</em>}{!x.children.length&&<em>Leaf-level requirement</em>}</div></div>
    {x.tests.length>0&&<div className="traceVerification"><small>VERIFICATION / RESULTS / EVIDENCE</small>{x.tests.map(test=><section key={test.revisionId}><div><b>{test.displayNumber}</b><span>{test.title}</span></div>{test.executions.map(run=><article key={run.id}><i className={run.outcome.toLowerCase()}>{run.outcome}</i><p>{run.determination}</p><small>{run.executedBy} · {new Date(run.executedAt).toLocaleString()}</small>{run.evidence.map(file=><a key={file.id} href={`${api}/api/evidence/${file.id}`}><b>{file.originalFileName}</b><code>{file.sha256}</code></a>)}</article>)}{!test.executions.length&&<em>Approved procedure awaiting execution</em>}</section>)}</div>}
   </article>)}</section>
  </>:<>
   <div className="documentActions"><select value={baselineId} onChange={e=>setBaselineId(e.target.value)}>{baselines.map(x=><option value={x.id} key={x.id}>{x.displayNumber} · {x.name}</option>)}</select><button onClick={generate}>Generate / refresh outputs</button></div>
   <section className="documentGrid">{documents.map(x=><article key={x.id}><div><span>{x.type.replace(/([A-Z])/g,' $1').trim()}</span><i>CONTROLLED</i></div><h2>{x.displayNumber}</h2><h3>{x.title}</h3><dl><div><dt>Release</dt><dd>{x.release}</dd></div><div><dt>Baseline</dt><dd>{x.baseline}</dd></div><div><dt>Artifacts</dt><dd>{x.artifactCount.toLocaleString()}</dd></div><div><dt>Generated</dt><dd>{new Date(x.generatedAt).toLocaleDateString()}</dd></div></dl><code>{x.contentHash}</code><div className="downloadLinks"><a href={`${api}/api/documents/${x.id}/download?format=docx`}>Download DOCX</a><a href={`${api}/api/documents/${x.id}/download?format=pdf`}>Download PDF</a></div></article>)}</section>
  </>}
 </main>
}
