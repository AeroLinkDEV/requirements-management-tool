import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import ScrEditor from "./ScrEditor";
import ScrWorkspace from "./ScrWorkspace";
import BaselineCenter from "./BaselineCenter";
import HistoryExplorer from "./HistoryExplorer";
import VerificationCenter from "./VerificationCenter";
import LifecycleExplorer from "./LifecycleExplorer";
import ReleaseCampaignCenter from "./ReleaseCampaignCenter";
import { AdministrationCenter, LoginPage, MyWorkCenter } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import "./App.css";
import "./Onboarding.css";
import "./DashboardInteractions.css";
import "./Showcase.css";

type Scr = {
  id: string;
  displayNumber: string;
  title: string;
  state: string;
  authorId: string;
  requirementCount: number;
  updatedAt: string;
};
type Metrics = {
  totalScrs: number;
  draft: number;
  inReview: number;
  approved: number;
};
type Overview = {systemRequirements:number;highLevelRequirements:number;lowLevelRequirements:number;historicalScrs:number;historicalSwcrs:number;activeRequests:number;traceLinks:number;testProcedures:number;testExecutions:number;controlledDocuments:number;softwareBuilds:number};
type CampaignSummary={id:string;releaseId:string;state:string;readiness:{percent:number;readyForRelease:boolean}}
type Release = { id: string; version: string; isReleased: boolean };
type Workspace = {
  program: { id: string; name: string; code: string };
  projects: {
    project: { id: string; name: string; softwareProduct: string };
    releases: Release[];
  }[];
};
const API = "http://127.0.0.1:5080";

function App() {
  const [user,setUser]=useState<AuthUser|null|undefined>(undefined);
  const [scrs, setScrs] = useState<Scr[]>([]),
    [metrics, setMetrics] = useState<Metrics>({
      totalScrs: 0,
      draft: 0,
      inReview: 0,
      approved: 0,
    }),
    [overview,setOverview]=useState<Overview>(),
    [campaigns,setCampaigns]=useState<CampaignSummary[]>([]),
    [workspaces, setWorkspaces] = useState<Workspace[]>([]),
    [activeId, setActiveId] = useState(""),
    [connected, setConnected] = useState(false),
    [error, setError] = useState(""),
    [saving, setSaving] = useState(false),
    [selectedScrId, setSelectedScrId] = useState(""),
    [view, setView] = useState<"dashboard" | "createScr" | "scr" | "baselines" | "history" | "verification" | "lifecycle" | "release" | "mywork" | "admin">("dashboard");
  useEffect(()=>{fetch(`${API}/api/auth/me`).then(async r=>setUser(r.ok?await r.json():null)).catch(()=>setUser(null))},[]);
  const loadWorkspaces = useCallback(async () => {
    try {
      const response = await fetch(`${API}/api/workspaces`),
        next = await response.json();
      setWorkspaces(next);
      setActiveId((current) => current || next[0]?.program.id || "");
      setConnected(true);
    } catch {
      setConnected(false);
    }
  }, []);
  const active =
      workspaces.find((x) => x.program.id === activeId) ?? workspaces[0],
    project = active?.projects[0],
    release = project?.releases.at(-1);
  const loadData = useCallback(async () => {
    if (!project) return;
    try {
      const [a, b, c, d] = await Promise.all([
        fetch(`${API}/api/scrs?projectId=${project.project.id}&releaseId=${release?.id ?? ''}`),
        fetch(`${API}/api/dashboard?projectId=${project.project.id}&releaseId=${release?.id ?? ''}`),
        fetch(`${API}/api/showcase/overview?projectId=${project.project.id}`),
        fetch(`${API}/api/release-campaigns?projectId=${project.project.id}`),
      ]);
      const page = await a.json();
      setScrs(Array.isArray(page.items) ? page.items : []);
      setMetrics(await b.json());
      if(c.ok)setOverview(await c.json());
      if(d.ok)setCampaigns(await d.json());
    } catch {
      setConnected(false);
    }
  }, [project, release]);
  useEffect(() => {
    if(!user)return;
    loadWorkspaces();
  }, [loadWorkspaces,user]);
  useEffect(() => {
    loadData();
  }, [loadData]);
  const createWorkspace = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    setError("");
    const form = new FormData(e.currentTarget),
      body = {
        ...Object.fromEntries(form),
        initialReleaseIsReleased: form.has("initialReleaseIsReleased"),
      };
    const response = await fetch(`${API}/api/workspaces`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      setError((await response.json()).error || "Unable to create program.");
      setSaving(false);
      return;
    }
    const created = await response.json();
    setActiveId(created.program.id);
    await loadWorkspaces();
    setSaving(false);
  };
  if(user===undefined)return <div className="appBoot">Establishing secure session…</div>;
  if(user===null)return <LoginPage api={API} onLogin={setUser}/>;
  if (connected && !workspaces.length)
    return (
      <div className="onboarding">
        <div className="onboardBrand">
          <span>▲</span> AeroLink
        </div>
        <div className="setup">
          <p className="step">GET STARTED · STEP 1 OF 1</p>
          <h1>Create your first program</h1>
          <p>
            Start with a clean, controlled workspace. Add more projects,
            releases, users, and imported baseline data later.
          </p>
          <form onSubmit={createWorkspace}>
            <label>
              Program name
              <input
                name="programName"
                placeholder="e.g. Navigation Systems"
                required
              />
            </label>
            <label>
              Program code
              <input
                name="programCode"
                placeholder="e.g. NAV"
                maxLength={30}
                required
              />
            </label>
            <label>
              Project name
              <input
                name="projectName"
                placeholder="e.g. Navigation Software"
                required
              />
            </label>
            <label>
              Software product
              <input
                name="softwareProduct"
                placeholder="e.g. Integrated Navigation Software"
                required
              />
            </label>
            <label>
              Initial release or baseline
              <input name="initialRelease" placeholder="e.g. 1.0" required />
            </label>
            <label className="check">
              <input type="checkbox" name="initialReleaseIsReleased" /> This
              version is already released
            </label>
            {error && <div className="formError">{error}</div>}
            <button disabled={saving}>
              {saving ? "Creating workspace…" : "Create program workspace →"}
            </button>
          </form>
          <small>
            No demonstration records are created. This becomes your real
            starting point.
          </small>
        </div>
      </div>
    );
  if (view === "createScr" && project && release)
    return (
      <ScrEditor
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        onCancel={() => setView("dashboard")}
        onSaved={async (scrId) => {
          await loadData();
          setSelectedScrId(scrId);
          setView("scr");
        }}
      />
    );
  if (view === "scr" && selectedScrId)
    return (
      <>
        <div className="scrPublicationTools">
          <span>Professional controlled publication</span>
          <a href={`${API}/api/scrs/${selectedScrId}/download?format=docx`}>Download DOCX</a>
          <a href={`${API}/api/scrs/${selectedScrId}/download?format=pdf`}>Download PDF</a>
        </div>
        <ScrWorkspace
          api={API}
          scrId={selectedScrId}
          user={user}
          onBack={() => setView("dashboard")}
          onChanged={loadData}
        />
      </>
    );
  if (view === "baselines" && project && release)
    return (
      <BaselineCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        productName={project.project.softwareProduct}
        onBack={() => setView("dashboard")}
      />
    );
  if (view === "history" && project)
    return (
      <HistoryExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        onBack={() => setView("dashboard")}
        onOpenScr={(id) => { setSelectedScrId(id); setView("scr"); }}
      />
    );
  if (view === "verification" && project && release)
    return <VerificationCenter api={API} projectId={project.project.id} releaseId={release.id} onBack={() => setView("dashboard")}/>;
  if (view === "lifecycle" && project)
    return <LifecycleExplorer api={API} projectId={project.project.id} releases={project.releases} onBack={() => setView("dashboard")}/>;
  if(view==="release"&&project)
    return <ReleaseCampaignCenter api={API} projectId={project.project.id} releases={project.releases} user={user} onBack={()=>setView("dashboard")} onOpenScr={(id)=>{setSelectedScrId(id);setView("scr")}} onOpenVerification={()=>setView("verification")} onOpenDocuments={()=>setView("lifecycle")}/>;
  if(view==="mywork"&&project)return <MyWorkCenter api={API} projectId={project.project.id} user={user} onBack={()=>setView("dashboard")} onOpenScr={(id)=>{setSelectedScrId(id);setView("scr")}} onOpenRelease={()=>setView("release")}/>;
  if(view==="admin"&&active)return <AdministrationCenter api={API} programId={active.program.id} onBack={()=>setView("dashboard")}/>;
  return (
    <div className="shell">
      <aside>
        <div className="brand">
          <span>▲</span>
          <b>AeroLink</b>
        </div>
        <div className="program">
          <small>ACTIVE PROGRAM</small>
          <select
            value={activeId}
            onChange={(e) => setActiveId(e.target.value)}
          >
            {workspaces.map((x) => (
              <option value={x.program.id} key={x.program.id}>
                {x.program.name}
              </option>
            ))}
          </select>
          <span>
            {project?.project.name} · {release?.version}
          </span>
        </div>
        <nav>
          <button onClick={()=>setView("mywork")}>◎&nbsp; My Work</button>
          {[
            "▦  Command Center",
            "◇  Change Requests",
            "≡  Requirements",
            "✓  Verification",
            "⌘  Baselines",
            "↗  Traceability",
            "▤  Documents",
            "◆  Release Campaign",
          ].map((x, i) => (
            <button className={i === 0 ? "active" : ""} key={x} onClick={() => {
              if (x.includes("Baselines")) setView("baselines");
              if (x.includes("Verification")) setView("verification");
              if (x.includes("Traceability") || x.includes("Documents")) setView("lifecycle");
              if (x.includes("Release Campaign")) setView("release");
              if (x.includes("Change Requests") || x.includes("Requirements")) setView("history");
            }}>
              {x}
            </button>
          ))}
          {user.isAdministrator&&<button onClick={()=>setView("admin")}>⚙&nbsp; Administration</button>}
        </nav>
        <footer>
          <div className="avatar">{user.displayName.split(' ').map(x=>x[0]).join('').slice(0,2)}</div>
          <div>
            <b>{user.displayName}</b>
            <small>{user.userName}</small>
          </div>
          <button className="signOut" onClick={async()=>{await fetch(`${API}/api/auth/logout`,{method:'POST'});setUser(null)}}>Sign out</button>
        </footer>
      </aside>
      <main>
        <header>
          <div>
            <p className="eyebrow">
              {active?.program.code} / {project?.project.name} /{" "}
              {release?.version}
            </p>
            <h1>Command Center</h1>
            <p>
              Assurance status, release readiness, and work requiring attention.
            </p>
          </div>
          <div className={`connection ${connected ? "ok" : ""}`}>
            <i /> {connected ? "Live data" : "API offline"}
          </div>
        </header>
        <section className="release">
          <div>
            <span className="tag">ACTIVE RELEASE</span>
            <h2>
              {project?.project.softwareProduct} {release?.version}
            </h2>
            <p>
              {release?.isReleased
                ? "Released reference version"
                : "Development release"}
            </p>
          </div>
          <div className="readiness">
            <b>{campaigns.find(x=>x.releaseId===release?.id)?.readiness.percent??0}%</b>
            <span>Release readiness</span>
            <div>
              <i style={{ width: `${campaigns.find(x=>x.releaseId===release?.id)?.readiness.percent??0}%` }} />
            </div>
          </div>
          <button onClick={()=>setView("release")}>Configure release →</button>
        </section>
        <section className="metrics">
          {[
            ["Total SCRs", metrics.totalScrs, "#1d66f5"],
            ["In draft", metrics.draft, "#a16a12"],
            ["In review", metrics.inReview, "#7552d6"],
            ["Approved", metrics.approved, "#16815f"],
          ].map(([label, value, color]) => (
            <article
              key={String(label)}
              style={{ "--accent": color } as React.CSSProperties}
            >
              <span>{label}</span>
              <strong>{value}</strong>
              <small>Current workspace</small>
            </article>
          ))}
        </section>
        {overview&&overview.systemRequirements>0&&<section className="programInventory"><div><p className="eyebrow">RELEASED 1.5 PRODUCT BASELINE</p><h3>Complete FMS lifecycle inventory</h3><span>Active release 1.6 inherits this controlled foundation.</span></div>{[["System requirements",overview.systemRequirements],["HLR",overview.highLevelRequirements],["LLR",overview.lowLevelRequirements],["Trace links",overview.traceLinks],["Test procedures",overview.testProcedures],["Test executions",overview.testExecutions]].map(([label,value])=><article key={String(label)}><b>{Number(value).toLocaleString()}</b><small>{label}</small></article>)}</section>}
        <section className="grid">
          <div className="panel work">
            <div className="panelhead">
              <div>
                <h3>Change request flow</h3>
                <p>Controlled changes targeting this release</p>
              </div>
              <button onClick={() => setView("createScr")}>+ New SCR</button>
            </div>
            {scrs.length ? (
              scrs.map((scr) => (
                <div className="row" key={scr.id} role="button" tabIndex={0} onClick={() => { setSelectedScrId(scr.id); setView("scr"); }} onKeyDown={(event) => { if (event.key === "Enter") { setSelectedScrId(scr.id); setView("scr"); } }}>
                  <div className="scricon">SCR</div>
                  <div>
                    <b>
                      {scr.displayNumber} · {scr.title}
                    </b>
                    <p>
                      {scr.requirementCount} requirement change
                      {scr.requirementCount === 1 ? "" : "s"} · {scr.authorId}
                    </p>
                  </div>
                  <span className={`state ${scr.state.toLowerCase()}`}>
                    {scr.state}
                  </span>
                  <time>{new Date(scr.updatedAt).toLocaleDateString()}</time>
                </div>
              ))
            ) : (
              <div className="empty">
                <b>Your controlled lifecycle starts here</b>
                <p>No change requests exist in this new workspace yet.</p>
                <button onClick={() => setView("createScr")}>
                  Create first SCR →
                </button>
              </div>
            )}
          </div>
          <div className="panel attention">
            <div className="panelhead">
              <div>
                <h3>Workspace readiness</h3>
                <p>Foundation for controlled development</p>
              </div>
            </div>
            <div className="signal green">
              <b>Program workspace</b>
              <strong>✓</strong>
              <p>{active?.program.name} is configured</p>
            </div>
            <div className="signal blue">
              <b>Initial release</b>
              <strong>✓</strong>
              <p>{release?.version} establishes the starting context</p>
            </div>
            <div className="signal amber">
              <b>Next action</b>
              <strong>1</strong>
              <p>Create or import the first controlled artifact</p>
            </div>
          </div>
        </section>
      </main>
    </div>
  );
}
export default App;
