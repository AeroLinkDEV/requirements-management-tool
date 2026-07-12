import { useEffect, useState } from 'react'
import './App.css'

type Scr = { id:string; displayNumber:string; title:string; state:string; authorId:string; requirementCount:number; updatedAt:string }
type Metrics = { totalScrs:number; draft:number; inReview:number; approved:number }
const API = 'http://127.0.0.1:5080'

function App() {
  const [scrs,setScrs]=useState<Scr[]>([]), [metrics,setMetrics]=useState<Metrics>({totalScrs:0,draft:0,inReview:0,approved:0})
  const [connected,setConnected]=useState(false)
  const load=async()=>{try{const [a,b]=await Promise.all([fetch(`${API}/api/scrs`),fetch(`${API}/api/dashboard`)]);setScrs(await a.json());setMetrics(await b.json());setConnected(true)}catch{setConnected(false)}}
  useEffect(()=>{load()},[])
  return <div className="shell">
    <aside><div className="brand"><span>▲</span><b>AeroLink</b></div><div className="program"><small>ACTIVE PROGRAM</small><strong>Flight Management System</strong><span>Software 3.3</span></div><nav>{['▦  Command Center','◇  Change Requests','≡  Requirements','✓  Verification','⌘  Baselines','↗  Traceability'].map((x,i)=><button className={i===0?'active':''} key={x}>{x}</button>)}</nav><footer><div className="avatar">SE</div><div><b>Systems Engineer</b><small>Engineering</small></div></footer></aside>
    <main><header><div><p className="eyebrow">PROGRAM OVERVIEW / SOFTWARE 3.3</p><h1>Command Center</h1><p>Assurance status, release readiness, and work requiring attention.</p></div><div className={`connection ${connected?'ok':''}`}><i/> {connected?'Live data':'API offline'}</div></header>
      <section className="release"><div><span className="tag">TARGET RELEASE</span><h2>FMS Software 3.3</h2><p>Building from released baseline 3.2</p></div><div className="readiness"><b>42%</b><span>Release readiness</span><div><i/></div></div><button>View release →</button></section>
      <section className="metrics">{[['Total SCRs',metrics.totalScrs,'#1d66f5'],['In draft',metrics.draft,'#a16a12'],['In review',metrics.inReview,'#7552d6'],['Approved',metrics.approved,'#16815f']].map(([l,v,c])=><article key={String(l)} style={{'--accent':c} as React.CSSProperties}><span>{l}</span><strong>{v}</strong><small>Software 3.3 scope</small></article>)}</section>
      <section className="grid"><div className="panel work"><div className="panelhead"><div><h3>Change request flow</h3><p>Controlled changes targeting this release</p></div><button onClick={load}>Refresh</button></div>{scrs.length? scrs.map(x=><div className="row" key={x.id}><div className="scricon">SCR</div><div><b>{x.displayNumber} · {x.title}</b><p>{x.requirementCount} requirement change{x.requirementCount===1?'':'s'} · {x.authorId}</p></div><span className={`state ${x.state.toLowerCase()}`}>{x.state}</span><time>{new Date(x.updatedAt).toLocaleDateString()}</time></div>):<div className="empty"><b>No change requests yet</b><p>Create the first SCR through the API; it will appear here as real persisted data.</p></div>}</div>
        <div className="panel attention"><div className="panelhead"><div><h3>Needs attention</h3><p>Items blocking forward progress</p></div></div><div className="signal amber"><b>Review queue</b><strong>{metrics.inReview}</strong><p>SCRs awaiting ordered approval</p></div><div className="signal blue"><b>Requirements changed</b><strong>{scrs.reduce((n,x)=>n+x.requirementCount,0)}</strong><p>Proposed revisions in release scope</p></div><div className="signal green"><b>Approved changes</b><strong>{metrics.approved}</strong><p>Eligible for candidate baseline</p></div></div></section>
    </main>
  </div>
}
export default App
