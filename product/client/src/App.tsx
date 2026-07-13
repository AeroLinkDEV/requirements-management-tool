import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import ScrEditor from "./ScrEditor";
import ScrWorkspace from "./ScrWorkspace";
import BaselineCenter from "./BaselineCenter";
import HistoryExplorer from "./HistoryExplorer";
import VerificationCenter from "./VerificationCenter";
import LifecycleExplorer from "./LifecycleExplorer";
import ReleaseCampaignCenter from "./ReleaseCampaignCenter";
import ReleasePlanningCenter from "./ReleasePlanningCenter";
import RequirementsWorkspace from "./RequirementsWorkspace";
import EnterpriseControlCenter from "./EnterpriseControlCenter";
import CommandPalette from "./CommandPalette";
import ArtifactRecordPage from "./ArtifactRecordPage";
import { readRoute, routePath } from "./routing";
import type { AppRoute, Discipline, RouteContext, View } from "./routing";
import {
  AdministrationCenter,
  LoginPage,
  MyWorkCenter,
} from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import "./App.css";
import "./Onboarding.css";
import "./DashboardInteractions.css";
import "./Showcase.css";
import "./PortalNavigation.css";

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
type Overview = {
  systemRequirements: number;
  highLevelRequirements: number;
  lowLevelRequirements: number;
  historicalScrs: number;
  historicalSwcrs: number;
  activeRequests: number;
  traceLinks: number;
  testProcedures: number;
  testExecutions: number;
  controlledDocuments: number;
  softwareBuilds: number;
};
type CampaignSummary = {
  id: string;
  releaseId: string;
  state: string;
  readiness: { percent: number; readyForRelease: boolean };
};
type Release = { id: string; version: string; isReleased: boolean };
type Workspace = {
  program: { id: string; name: string; code: string };
  projects: {
    project: { id: string; name: string; softwareProduct: string };
    releases: Release[];
  }[];
};
const API = import.meta.env.VITE_API_URL || "http://127.0.0.1:5080";

function AppNavigation({ user, workspaces, activeId, selectedProjectId, selectedReleaseId, view, discipline, context, onProgram, onProject, onRelease, onNavigate, onSearch, onSignOut }:{
  user:AuthUser;workspaces:Workspace[];activeId:string;selectedProjectId:string;selectedReleaseId:string;view:View;discipline:Discipline;context?:RouteContext;
  onProgram:(id:string)=>void;onProject:(id:string)=>void;onRelease:(id:string)=>void;onNavigate:(view:View,discipline?:Discipline)=>void;onSearch:()=>void;onSignOut:()=>void;
}) {
  const active=workspaces.find(x=>x.program.id===activeId)??workspaces[0],project=active?.projects.find(x=>x.project.id===selectedProjectId)??active?.projects[0],release=project?.releases.find(x=>x.id===selectedReleaseId)??project?.releases.at(-1);
  const item=(label:string,target:View,icon:string,area:Discipline="system")=> <a href={context?routePath(context,target,area):"#"} className={view===target&&discipline===area?"active":""} onClick={event=>{event.preventDefault();onNavigate(target,area)}}><i>{icon}</i>{label}</a>;
  return <aside className="appNavigation"><div className="brand"><span>▲</span><b>AeroLink</b></div><button className="quickSearch" onClick={onSearch}><span>⌕</span> Search &amp; navigate <kbd>Ctrl K</kbd></button><div className="program"><small>ACTIVE CONTEXT</small><select value={activeId} onChange={event=>onProgram(event.target.value)} aria-label="Active program">{workspaces.map(x=><option value={x.program.id} key={x.program.id}>{x.program.name}</option>)}</select>{active?.projects.length>1?<select value={project?.project.id??""} onChange={event=>onProject(event.target.value)} aria-label="Active project">{active.projects.map(x=><option value={x.project.id} key={x.project.id}>{x.project.name}</option>)}</select>:<span>{project?.project.name}</span>}<select className="releaseSelector" value={release?.id??""} onChange={event=>onRelease(event.target.value)} aria-label="Active release">{project?.releases.map(item=><option value={item.id} key={item.id}>{item.version} · {item.isReleased?"Released":"In work"}</option>)}</select></div>
    <nav className="primaryNavigation"><details className="navGroup" open><summary>MY WORK</summary>{item("Command Center","dashboard","⌂")}{item("My Work","mywork","◎")}</details>
      <details className="navGroup" open><summary>SYSTEMS ENGINEERING</summary>{item("New System SCR","createSystemScr","+")}{item("System Change Requests","history","◇","system")}{item("System Requirements","requirements","≡","system")}</details>
      <details className="navGroup" open><summary>SOFTWARE ENGINEERING</summary>{item("New Software SWCR","createSoftwareChange","+")}{item("Software Change Requests","history","◇","software")}{item("HLR & LLR Requirements","requirements","≡","software")}</details>
      <details className="navGroup" open><summary>VERIFICATION</summary>{item("System Verification","verification","✓","systemTest")}{item("Software Verification","verification","✓","softwareTest")}{item("Traceability & Outputs","lifecycle","↗","system")}</details>
      <details className="navGroup" open><summary>RELEASE & CONFIGURATION</summary>{item("Release Planning","planning","⑂")}{item("Baselines","baselines","⌘")}{item("Release Campaign","release","◆")}{item("Enterprise Control","enterprise","◈")}</details>
      {user.isAdministrator&&<details className="navGroup" open><summary>ENTERPRISE ADMINISTRATION</summary>{item("People & Authority","admin","⚙")}</details>}
    </nav><footer><div className="avatar">{user.displayName.split(" ").map(x=>x[0]).join("").slice(0,2)}</div><div><b>{user.displayName}</b><small>{user.userName}</small></div><button className="signOut" onClick={onSignOut}>Sign out</button></footer></aside>;
}

function App() {
  const [user, setUser] = useState<AuthUser | null | undefined>(undefined);
  const [initialRoute] = useState<AppRoute>(() => readRoute());
  const [scrs, setScrs] = useState<Scr[]>([]),
    [metrics, setMetrics] = useState<Metrics>({
      totalScrs: 0,
      draft: 0,
      inReview: 0,
      approved: 0,
    }),
    [overview, setOverview] = useState<Overview>(),
    [campaigns, setCampaigns] = useState<CampaignSummary[]>([]),
    [workspaces, setWorkspaces] = useState<Workspace[]>([]),
    [activeId, setActiveId] = useState(initialRoute.programId ?? ""),
    [selectedProjectId, setSelectedProjectId] = useState(initialRoute.projectId ?? ""),
    [selectedReleaseId, setSelectedReleaseId] = useState(initialRoute.releaseId ?? ""),
    [connected, setConnected] = useState(false),
    [error, setError] = useState(""),
    [saving, setSaving] = useState(false),
    [selectedScrId, setSelectedScrId] = useState(initialRoute.view === "scr" ? initialRoute.artifactId ?? "" : ""),
    [selectedArtifactId,setSelectedArtifactId]=useState(initialRoute.artifactId ?? ""),
    [selectedArtifactKind,setSelectedArtifactKind]=useState(initialRoute.artifactKind ?? ""),
    [paletteOpen,setPaletteOpen]=useState(false),
    [discipline,setDiscipline]=useState<Discipline>(initialRoute.discipline),
    [view, setView] = useState<View>(initialRoute.view);
  useEffect(() => {
    fetch(`${API}/api/auth/me`)
      .then(async (r) => setUser(r.ok ? await r.json() : null))
      .catch(() => setUser(null));
  }, []);
  const loadWorkspaces = useCallback(async () => {
    try {
      const response = await fetch(`${API}/api/workspaces`),
        next = await response.json() as Workspace[];
      setWorkspaces(next);
      const program=next.find(x=>x.program.id===initialRoute.programId)??next[0],project=program?.projects.find(x=>x.project.id===initialRoute.projectId)??program?.projects[0],release=project?.releases.find(x=>x.id===initialRoute.releaseId)??[...(project?.releases??[])].reverse().find(x=>!x.isReleased)??project?.releases.at(-1);
      setActiveId((current) => next.some(x=>x.program.id===current)?current:program?.program.id||"");
      setSelectedProjectId((current)=>program?.projects.some(x=>x.project.id===current)?current:project?.project.id||"");
      setSelectedReleaseId((current)=>project?.releases.some(x=>x.id===current)?current:release?.id||"");
      setConnected(true);
    } catch {
      setConnected(false);
    }
  }, [initialRoute]);
  const active =
      workspaces.find((x) => x.program.id === activeId) ?? workspaces[0],
    project = active?.projects.find(x=>x.project.id===selectedProjectId)??active?.projects[0],
    release =
      project?.releases.find((x) => x.id === selectedReleaseId) ??
      [...(project?.releases ?? [])].reverse().find((x) => !x.isReleased) ??
      project?.releases.at(-1);
  useEffect(() => {
    if (release && release.id !== selectedReleaseId)
      setSelectedReleaseId(release.id);
  }, [release, selectedReleaseId]);
  useEffect(()=>{const handler=()=>{const route=readRoute();setView(route.view);setDiscipline(route.discipline);if(route.programId)setActiveId(route.programId);if(route.projectId)setSelectedProjectId(route.projectId);if(route.releaseId)setSelectedReleaseId(route.releaseId);setSelectedArtifactId(route.artifactId??"");setSelectedArtifactKind(route.artifactKind??"");setSelectedScrId(route.view==="scr"?route.artifactId??"":"")};addEventListener("popstate",handler);return()=>removeEventListener("popstate",handler)},[]);
  useEffect(()=>{const handler=(event:KeyboardEvent)=>{if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==="k"){event.preventDefault();setPaletteOpen(true)}if(event.key==="Escape")setPaletteOpen(false)};addEventListener("keydown",handler);return()=>removeEventListener("keydown",handler)},[]);
  const loadData = useCallback(async () => {
    if (!project) return;
    try {
      const [a, b, c, d] = await Promise.all([
        fetch(
          `${API}/api/scrs?projectId=${project.project.id}&releaseId=${release?.id ?? ""}`,
        ),
        fetch(
          `${API}/api/dashboard?projectId=${project.project.id}&releaseId=${release?.id ?? ""}`,
        ),
        fetch(`${API}/api/showcase/overview?projectId=${project.project.id}`),
        fetch(`${API}/api/release-campaigns?projectId=${project.project.id}`),
      ]);
      const page = await a.json();
      setScrs(Array.isArray(page.items) ? page.items : []);
      setMetrics(await b.json());
      if (c.ok) setOverview(await c.json());
      if (d.ok) setCampaigns(await d.json());
    } catch {
      setConnected(false);
    }
  }, [project, release]);
  useEffect(() => {
    if (!user) return;
    loadWorkspaces();
  }, [loadWorkspaces, user]);
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
  if (user === undefined)
    return <div className="appBoot">Establishing secure session…</div>;
  if (user === null) return <LoginPage api={API} onLogin={setUser} />;
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
  const context:RouteContext|undefined=active&&project&&release?{programId:active.program.id,projectId:project.project.id,releaseId:release.id}:undefined;
  const navigate=(target:View,area:Discipline=discipline,artifactId?:string,artifactKind?:string,replace=false)=>{setView(target);setDiscipline(area);setSelectedArtifactId(artifactId??"");setSelectedArtifactKind(artifactKind??"");setSelectedScrId(target==="scr"?artifactId??"":["scr"].includes(target)?selectedScrId:"");if(context){const path=routePath(context,target,area,artifactId,artifactKind);history[replace?"replaceState":"pushState"]({},"",path)}};
  const changeProgram=(id:string)=>{const next=workspaces.find(x=>x.program.id===id),nextProject=next?.projects[0],nextRelease=[...(nextProject?.releases??[])].reverse().find(x=>!x.isReleased)??nextProject?.releases.at(-1);if(!nextProject||!nextRelease)return;setActiveId(id);setSelectedProjectId(nextProject.project.id);setSelectedReleaseId(nextRelease.id);const nextContext={programId:id,projectId:nextProject.project.id,releaseId:nextRelease.id};history.pushState({},"",routePath(nextContext,view,discipline,selectedArtifactId,selectedArtifactKind))};
  const changeProject=(id:string)=>{const next=active?.projects.find(x=>x.project.id===id),nextRelease=[...(next?.releases??[])].reverse().find(x=>!x.isReleased)??next?.releases.at(-1);if(!next||!nextRelease)return;setSelectedProjectId(id);setSelectedReleaseId(nextRelease.id);history.pushState({},"",routePath({programId:active!.program.id,projectId:id,releaseId:nextRelease.id},view,discipline,selectedArtifactId,selectedArtifactKind))};
  const changeRelease=(id:string)=>{setSelectedReleaseId(id);if(active&&project)history.pushState({},"",routePath({programId:active.program.id,projectId:project.project.id,releaseId:id},view,discipline,selectedArtifactId,selectedArtifactKind))};
  if(context&&location.pathname==="/")history.replaceState({},"",routePath(context,"dashboard"));
  const navigation=<AppNavigation user={user} workspaces={workspaces} activeId={activeId} selectedProjectId={project?.project.id??selectedProjectId} selectedReleaseId={release?.id??selectedReleaseId} view={view} discipline={discipline} context={context} onProgram={changeProgram} onProject={changeProject} onRelease={changeRelease} onNavigate={navigate} onSearch={()=>setPaletteOpen(true)} onSignOut={async()=>{await fetch(`${API}/api/auth/logout`,{method:"POST"});setUser(null)}}/>;
  const labels:Record<View,string>={dashboard:"Command Center",createSystemScr:"New System SCR",createSoftwareChange:"New Software SWCR",scr:"Change Request",baselines:"Baselines",history:"Change Requests",requirements:"Requirements",verification:"Verification",lifecycle:"Traceability",release:"Release Campaign",planning:"Release Planning",mywork:"My Work",admin:"Administration",enterprise:"Enterprise Control",artifact:"Artifact",notFound:"Not Found"};
  const contextBar=<div className="contextBar"><nav aria-label="Breadcrumb"><span>{active?.program.name}</span><b>›</b><span>{project?.project.name}</span><b>›</b><span>{release?.version}</span><b>›</b><strong>{labels[view]}</strong></nav><button onClick={async()=>navigator.clipboard.writeText(location.href)}>Copy link</button></div>;
  const palette=context?<CommandPalette api={API} context={context} open={paletteOpen} onClose={()=>setPaletteOpen(false)} onNavigate={navigate}/>:null;
  const inShell=(content:React.ReactNode)=><div className="shell">{navigation}<div className="workspaceStage">{contextBar}{content}</div>{palette}</div>;
  if(view==="notFound")return inShell(<main className="artifactState"><div><span>?</span><h1>Page not found</h1><p>This AeroLink route is not recognized. Use quick navigation to find an authorized workspace or artifact.</p><button onClick={()=>navigate("dashboard")}>Return to Command Center</button></div></main>);
  if(view==="artifact"&&selectedArtifactId&&selectedArtifactKind)return inShell(<ArtifactRecordPage api={API} kind={selectedArtifactKind} id={selectedArtifactId} onBack={()=>navigate("dashboard")} onOpen={(kind,id)=>{if(kind==="change-request")navigate("scr","system",id);else if(kind==="requirement")navigate("requirements","system",id);else navigate("artifact","system",id,kind)}}/>);
  if ((view === "createSystemScr" || view === "createSoftwareChange") && project && release)
    return inShell(
      <ScrEditor
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        scope={view === "createSystemScr" ? "System" : "Software"}
        user={user}
        onCancel={() => navigate("dashboard")}
        onSaved={async (scrId) => {
          await loadData();
          navigate("scr",view === "createSoftwareChange" ? "software" : "system",scrId);
        }}
      />
    );
  if (view === "scr" && selectedScrId)
    return inShell(
      <>
        <div className="scrPublicationTools">
          <span>Professional controlled publication</span>
          <a href={`${API}/api/scrs/${selectedScrId}/download?format=docx`}>
            Download DOCX
          </a>
          <a href={`${API}/api/scrs/${selectedScrId}/download?format=pdf`}>
            Download PDF
          </a>
        </div>
        <ScrWorkspace
          api={API}
          scrId={selectedScrId}
          user={user}
          onBack={() => navigate("dashboard")}
          onChanged={loadData}
        />
      </>
    );
  if (view === "baselines" && project && release)
    return inShell(
      <BaselineCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        productName={project.project.softwareProduct}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "history" && project)
    return inShell(
      <HistoryExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        scope={discipline === "software" ? "Software" : "System"}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
      />
    );
  if (view === "requirements" && project)
    return inShell(
      <RequirementsWorkspace
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        user={user}
        scope={discipline === "software" ? "Software" : "System"}
        initialViewId={initialRoute.savedViewId}
        initialArtifactId={view === "requirements" ? selectedArtifactId || undefined : undefined}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenRequirement={(id) => navigate("requirements",discipline,id)}
      />
    );
  if (view === "verification" && project && release)
    return inShell(
      <VerificationCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        scope={discipline === "softwareTest" ? "Software" : "System"}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "lifecycle" && project)
    return inShell(
      <LifecycleExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "release" && project)
    return inShell(
      <ReleaseCampaignCenter
        api={API}
        projectId={project.project.id}
        activeReleaseId={release?.id ?? ""}
        releases={project.releases}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenVerification={() => navigate("verification",discipline === "software" ? "softwareTest" : "systemTest")}
        onOpenDocuments={() => navigate("lifecycle")}
      />
    );
  if (view === "planning" && project && release)
    return inShell(
      <ReleasePlanningCenter
        api={API}
        projectId={project.project.id}
        productName={project.project.softwareProduct}
        activeReleaseId={release.id}
        onBack={() => navigate("dashboard")}
        onSelectRelease={changeRelease}
        onOpenBaselines={() => navigate("baselines")}
        onOpenCampaign={() => navigate("release")}
        onChanged={loadWorkspaces}
      />
    );
  if (view === "mywork" && project)
    return inShell(
      <MyWorkCenter
        api={API}
        projectId={project.project.id}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenRelease={() => navigate("release")}
      />
    );
  if (view === "admin" && active)
    return inShell(
      <AdministrationCenter
        api={API}
        programId={active.program.id}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "enterprise" && project)
    return inShell(
      <EnterpriseControlCenter
        api={API}
        projectId={project.project.id}
        onBack={() => navigate("dashboard")}
      />
    );
  return (
    <div className="shell">
      {navigation}
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
            <b>
              {campaigns.find((x) => x.releaseId === release?.id)?.readiness
                .percent ?? 0}
              %
            </b>
            <span>Release readiness</span>
            <div>
              <i
                style={{
                  width: `${campaigns.find((x) => x.releaseId === release?.id)?.readiness.percent ?? 0}%`,
                }}
              />
            </div>
          </div>
          <button
            onClick={() =>
              navigate(release?.isReleased ? "planning" : "release")
            }
          >
            {release?.isReleased
              ? "View product line →"
              : "Configure release →"}
          </button>
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
        {overview && overview.systemRequirements > 0 && (
          <section className="programInventory">
            <div>
              <p className="eyebrow">RELEASED 1.5 PRODUCT BASELINE</p>
              <h3>Complete FMS lifecycle inventory</h3>
              <span>
                Active release 1.6 inherits this controlled foundation.
              </span>
            </div>
            {[
              ["System requirements", overview.systemRequirements],
              ["HLR", overview.highLevelRequirements],
              ["LLR", overview.lowLevelRequirements],
              ["Trace links", overview.traceLinks],
              ["Test procedures", overview.testProcedures],
              ["Test executions", overview.testExecutions],
            ].map(([label, value]) => (
              <article key={String(label)}>
                <b>{Number(value).toLocaleString()}</b>
                <small>{label}</small>
              </article>
            ))}
          </section>
        )}
        <section className="grid">
          <div className="panel work">
            <div className="panelhead">
              <div>
                <h3>Change request flow</h3>
                <p>Controlled changes targeting this release</p>
              </div>
              {release?.isReleased ? (
                <button onClick={() => navigate("history",discipline)}>
                  Search released changes
                </button>
              ) : (
                <button onClick={() => navigate("createSystemScr")}>+ New System SCR</button>
              )}
            </div>
            {scrs.length ? (
              scrs.map((scr) => (
                <div
                  className="row"
                  key={scr.id}
                  role="button"
                  tabIndex={0}
                  onClick={() => navigate("scr",discipline,scr.id)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      navigate("scr",discipline,scr.id);
                    }
                  }}
                >
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
                <button onClick={() => navigate("createSystemScr")}>
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
