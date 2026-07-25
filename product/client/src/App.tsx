import { useCallback, useEffect, useState } from "react";
import { stateLabel } from './presentation'
import type { FormEvent } from "react";
import ScrEditor from "./ScrEditor";
import ScrWorkspace from "./ScrWorkspace";
import BaselineCenter from "./BaselineCenter";
import HistoryExplorer from "./HistoryExplorer";
import VerificationCenter from "./VerificationCenter";
import ProblemReportCenter from "./ProblemReportCenter";
import LifecycleExplorer from "./LifecycleExplorer";
import ReleaseCampaignCenter from "./ReleaseCampaignCenter";
import LifecycleDecisionRoom from "./LifecycleDecisionRoom";
import ReleasePlanningCenter from "./ReleasePlanningCenter";
import RequirementsWorkspace from "./RequirementsWorkspace";
import EnterpriseControlCenter from "./EnterpriseControlCenter";
import IntegrationCommandCenter from "./IntegrationCommandCenter";
import CommandPalette from "./CommandPalette";
import ExperienceControls from "./ExperienceControls";
import type { MotionPreference, WorkspaceDensity } from "./ExperienceControls";
import ArtifactRecordPage from "./ArtifactRecordPage";
import { identityInitials, identityLabel } from "./presentation";
import { readRoute, routePath } from "./routing";
import type { AppRoute, Discipline, HistoryStateIntent, HistoryTypeIntent, RouteContext, View } from "./routing";
import {
  AccountSecurityDialog,
  AdministrationCenter,
  LoginPage,
  MyWorkCenter,
  RequiredPasswordChange,
} from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import { PersonAvatar } from "./People";
import "./App.css";
import "./Onboarding.css";
import "./DashboardInteractions.css";
import "./Showcase.css";
import "./PortalNavigation.css";
import "./ShowcaseRefresh.css";
import "./ExperiencePolish.css";
import "./People.css";
import "./CohesionPass.css";
import "./ProblemReportCenter.css";

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

function AppNavigation({ user, workspaces, activeId, selectedProjectId, selectedReleaseId, view, discipline, context, density, onProgram, onProject, onRelease, onNavigate, onSearch, onDisplay, onSignOut }:{
  user:AuthUser;workspaces:Workspace[];activeId:string;selectedProjectId:string;selectedReleaseId:string;view:View;discipline:Discipline;context?:RouteContext;
  density:WorkspaceDensity;onProgram:(id:string)=>void;onProject:(id:string)=>void;onRelease:(id:string)=>void;onNavigate:(view:View,discipline?:Discipline)=>void;onSearch:()=>void;onDisplay:()=>void;onSignOut:()=>void;
}) {
  const [securityOpen,setSecurityOpen]=useState(false);
  const active = workspaces.find(x => x.program.id === activeId) ?? workspaces[0];
  const project = active?.projects.find(x => x.project.id === selectedProjectId) ?? active?.projects[0];
  const release = project?.releases.find(x => x.id === selectedReleaseId) ?? project?.releases.at(-1);
  const item = (label:string,target:View,icon:string,area:Discipline="system",accessibleLabel=label) => {
    const activeItem = (view===target || (target==="release" && ["releaseImpact","releaseDecision","releaseOperations"].includes(view))) && discipline===area;
    return <a href={context ? routePath(context,target,area) : "#"} className={activeItem?"active":""} aria-label={accessibleLabel} aria-current={activeItem?"page":undefined} onClick={event=>{event.preventDefault();onNavigate(target,area)}}>
      <i aria-hidden="true">{icon}</i><span>{label}</span>
    </a>;
  };
  const engineeringView = ["createSystemScr","createSoftwareChange","history","requirements","scr"].includes(view);
  const releaseView = ["planning","baselines","release","releaseImpact","releaseDecision","releaseOperations","enterprise"].includes(view);
  const engineeringScope:Discipline = discipline==="software" ? "software" : "system";
  const verificationScope:Discipline = discipline==="softwareTest"||discipline==="software" ? "softwareTest" : "systemTest";
  const newChangeView:View = engineeringScope==="software" ? "createSoftwareChange" : "createSystemScr";
  return (
    <aside className="appNavigation">
      <div className="brand"><span aria-hidden="true">▲</span><b>AeroLink</b></div>
      <button className="quickSearch" onClick={onSearch}><span aria-hidden="true">⌕</span> Search &amp; navigate <kbd>Ctrl K</kbd></button>
      <div className="program">
        <small>ACTIVE CONTEXT</small>
        {workspaces.length > 1
          ? <select value={activeId} onChange={event=>onProgram(event.target.value)} aria-label="Active program">{workspaces.map(x=><option value={x.program.id} key={x.program.id}>{x.program.name}</option>)}</select>
          : <strong className="activeProgram" title={active?.program.name}>{active?.program.name}</strong>}
        {active?.projects.length>1
          ? <select value={project?.project.id??""} onChange={event=>onProject(event.target.value)} aria-label="Active project">{active.projects.map(x=><option value={x.project.id} key={x.project.id}>{x.project.name}</option>)}</select>
          : <span title={project?.project.name}>{project?.project.name}</span>}
        <select className="releaseSelector" value={release?.id??""} onChange={event=>onRelease(event.target.value)} aria-label="Active release">{project?.releases.map(item=><option value={item.id} key={item.id}>{item.version} · {item.isReleased?"Released":"In work"}</option>)}</select>
      </div>
      <nav className="primaryNavigation" aria-label="Primary navigation">
        <div className="navHome">{item("Command Center","dashboard","⌂")}{item("My Work","mywork","◎")}</div>
        <details className="navGroup" open={engineeringView}><summary>ENGINEERING</summary><div className="navScopeSwitch" role="group" aria-label="Engineering scope"><button type="button" aria-pressed={engineeringScope==="system"} onClick={()=>onNavigate(view==="history"||view==="requirements"?view:"history","system")}>System</button><button type="button" aria-pressed={engineeringScope==="software"} onClick={()=>onNavigate(view==="history"||view==="requirements"?view:"history","software")}>Software</button></div>{item("Change Requests","history","◇",engineeringScope,engineeringScope==="software"?"Software Change Requests":"System Change Requests")}{item("Requirements Explorer","requirements","≡",engineeringScope,engineeringScope==="software"?"Software Requirements Explorer":"System Requirements Explorer")}{item("New Change Request",newChangeView,"+",engineeringScope,engineeringScope==="software"?"New Software SWCR":"New System SCR")}</details>
        <details className="navGroup" open={view==="verification"||view==="problemReports"||view==="lifecycle"}><summary>ASSURANCE</summary><div className="navScopeSwitch" role="group" aria-label="Assurance scope"><button type="button" aria-pressed={verificationScope==="systemTest"} onClick={()=>onNavigate("verification","systemTest")}>System</button><button type="button" aria-pressed={verificationScope==="softwareTest"} onClick={()=>onNavigate("verification","softwareTest")}>Software</button></div>{item("Verification","verification","✓",verificationScope,verificationScope==="softwareTest"?"Software Verification":"System Verification")}{item("Problem Reports","problemReports","!","system","Issue lifecycle records")}{item("Digital Thread","lifecycle","↗","system","Traceability & Outputs / Digital Thread")}</details>
        <details className="navGroup" open={releaseView}><summary>RELEASE</summary>{item("Product Versions","planning","⑂","system","Product Versions / Release Planning")}{item("Baselines","baselines","⌘")}{item("Lifecycle Decision Room","release","◆","system","Lifecycle Decision Room / Release Readiness")}</details>
        {user.isAdministrator&&<details className="navGroup" open={view==="admin"||view==="enterprise"||view==="integrations"}><summary>ADMINISTRATION</summary>{item("People & Authority","admin","⚙")}{item("Integration Center","integrations","↗","system","Integration Command Center")}{item("System Operations","enterprise","◈","system","System Operations / Enterprise Control")}</details>}
      </nav>
      <footer><PersonAvatar userName={user.userName} displayName={user.displayName} size="large"/><div><b>{user.displayName}</b><small>{user.userName}</small></div><button className="accountSecurity" onClick={()=>setSecurityOpen(true)}>Account security</button><button className="signOut" onClick={onSignOut}>Sign out</button><button className="workspaceDisplay" onClick={onDisplay} aria-label="Open workspace display settings"><span>Aa</span><div><b>Workspace display</b><small>{density} density</small></div><i aria-hidden="true">›</i></button></footer>
      {securityOpen&&<AccountSecurityDialog api={API} onClose={()=>setSecurityOpen(false)}/>}
    </aside>
  );
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
    [displayOpen,setDisplayOpen]=useState(false),
    [density,setDensity]=useState<WorkspaceDensity>(()=>(localStorage.getItem('aerolink-density')==='compact'?'compact':'comfortable')),
    [motion,setMotion]=useState<MotionPreference>(()=>(localStorage.getItem('aerolink-motion')==='reduced'?'reduced':'full')),
    [toast,setToast]=useState(''),
    [dashboardLoading,setDashboardLoading]=useState(true),
    [historyStateIntent,setHistoryStateIntent]=useState<HistoryStateIntent|undefined>(initialRoute.historyStateIntent),
    [historyTypeIntent,setHistoryTypeIntent]=useState<HistoryTypeIntent|undefined>(initialRoute.historyTypeIntent),
    [discipline,setDiscipline]=useState<Discipline>(initialRoute.discipline),
    [view, setView] = useState<View>(initialRoute.view);
  useEffect(() => {
    fetch(`${API}/api/auth/me`)
      .then(async (r) => setUser(r.ok ? await r.json() : null))
      .catch(() => setUser(null));
  }, []);
  const loadWorkspaces = useCallback(async () => {
    try {
      const response = await fetch(`${API}/api/workspaces`);if(!response.ok)throw new Error('Workspace access is unavailable.');const next = await response.json() as Workspace[];if(!Array.isArray(next))throw new Error('Workspace response is invalid.');
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
  useEffect(()=>{const handler=()=>{const route=readRoute();setView(route.view);setDiscipline(route.discipline);setHistoryStateIntent(route.historyStateIntent);setHistoryTypeIntent(route.historyTypeIntent);if(route.programId)setActiveId(route.programId);if(route.projectId)setSelectedProjectId(route.projectId);if(route.releaseId)setSelectedReleaseId(route.releaseId);setSelectedArtifactId(route.artifactId??"");setSelectedArtifactKind(route.artifactKind??"");setSelectedScrId(route.view==="scr"?route.artifactId??"":"")};addEventListener("popstate",handler);return()=>removeEventListener("popstate",handler)},[]);
  useEffect(()=>{const handler=(event:KeyboardEvent)=>{if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==="k"){event.preventDefault();setPaletteOpen(true)}if(event.key==="Escape"){setPaletteOpen(false);setDisplayOpen(false)}};addEventListener("keydown",handler);return()=>removeEventListener("keydown",handler)},[]);
  useEffect(()=>{document.documentElement.dataset.density=density;localStorage.setItem('aerolink-density',density)},[density]);
  useEffect(()=>{document.documentElement.dataset.motion=motion;localStorage.setItem('aerolink-motion',motion)},[motion]);
  useEffect(()=>{if(!toast)return;const timer=setTimeout(()=>setToast(''),2600);return()=>clearTimeout(timer)},[toast]);
  const loadData = useCallback(async () => {
    if (!project) return;
    setDashboardLoading(true);
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
    } finally {
      setDashboardLoading(false);
    }
  }, [project, release]);
  useEffect(() => {
    if (!user||user.mustChangePassword) return;
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
    return <div className="appBoot"><div className="bootMark">▲</div><div><p>AEROLINK CONTROLLED WORKSPACE</p><h1>Establishing your secure session</h1><span>Confirming identity, authority, and active program context…</span><i><b/></i></div></div>;
  if (user === null) return <LoginPage api={API} onLogin={setUser} />;
  if (user.mustChangePassword) return <RequiredPasswordChange api={API} onComplete={()=>setUser(null)} />;
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
  const navigate=(target:View,area:Discipline=discipline,artifactId?:string,artifactKind?:string,replace=false,stateIntent?:HistoryStateIntent,typeIntent?:HistoryTypeIntent)=>{const nextStateIntent=target==="history"?stateIntent:undefined,nextTypeIntent=target==="history"?(typeIntent??(area==="software"?"Software":"System")):undefined;setView(target);setDiscipline(area);setHistoryStateIntent(nextStateIntent);setHistoryTypeIntent(nextTypeIntent);setSelectedArtifactId(artifactId??"");setSelectedArtifactKind(artifactKind??"");setSelectedScrId(target==="scr"?artifactId??"":["scr"].includes(target)?selectedScrId:"");if(context){const path=routePath(context,target,area,artifactId,artifactKind,nextStateIntent,nextTypeIntent);history[replace?"replaceState":"pushState"]({},"",path)}};
  const changeProgram=(id:string)=>{const next=workspaces.find(x=>x.program.id===id),nextProject=next?.projects[0],nextRelease=[...(nextProject?.releases??[])].reverse().find(x=>!x.isReleased)??nextProject?.releases.at(-1);if(!nextProject||!nextRelease)return;setActiveId(id);setSelectedProjectId(nextProject.project.id);setSelectedReleaseId(nextRelease.id);const nextContext={programId:id,projectId:nextProject.project.id,releaseId:nextRelease.id};history.pushState({},"",routePath(nextContext,view,discipline,selectedArtifactId,selectedArtifactKind,view==="history"?historyStateIntent:undefined,view==="history"?historyTypeIntent:undefined))};
  const changeProject=(id:string)=>{const next=active?.projects.find(x=>x.project.id===id),nextRelease=[...(next?.releases??[])].reverse().find(x=>!x.isReleased)??next?.releases.at(-1);if(!next||!nextRelease)return;setSelectedProjectId(id);setSelectedReleaseId(nextRelease.id);history.pushState({},"",routePath({programId:active!.program.id,projectId:id,releaseId:nextRelease.id},view,discipline,selectedArtifactId,selectedArtifactKind,view==="history"?historyStateIntent:undefined,view==="history"?historyTypeIntent:undefined))};
  const changeRelease=(id:string)=>{setSelectedReleaseId(id);if(active&&project)history.pushState({},"",routePath({programId:active.program.id,projectId:project.project.id,releaseId:id},view,discipline,selectedArtifactId,selectedArtifactKind,view==="history"?historyStateIntent:undefined,view==="history"?historyTypeIntent:undefined))};
  if(context&&location.pathname==="/")history.replaceState({},"",routePath(context,"dashboard"));
  const navigation=<AppNavigation user={user} workspaces={workspaces} activeId={activeId} selectedProjectId={project?.project.id??selectedProjectId} selectedReleaseId={release?.id??selectedReleaseId} view={view} discipline={discipline} context={context} density={density} onProgram={changeProgram} onProject={changeProject} onRelease={changeRelease} onNavigate={navigate} onSearch={()=>setPaletteOpen(true)} onDisplay={()=>setDisplayOpen(true)} onSignOut={async()=>{await fetch(`${API}/api/auth/logout`,{method:"POST"});setUser(null)}}/>;
  const labels:Record<View,string>={dashboard:"Command Center",createSystemScr:"New System SCR",createSoftwareChange:"New Software SWCR",scr:"Change Request",baselines:"Baselines",history:"Change Requests",requirements:"Requirements Explorer",verification:"Verification",problemReports:"Problem Reports",lifecycle:"Digital Thread",release:"Release Readiness",releaseImpact:"Change Impact Review",releaseDecision:"Release Evidence & Decision",releaseOperations:"Release Operations",planning:"Product Versions",mywork:"My Work",admin:"Administration",enterprise:"System Operations",integrations:"Integration Command Center",artifact:"Artifact",notFound:"Not Found"};
  const scopedLabel=view==="history"?`${historyTypeIntent==="All"?"All":discipline==="software"?"Software":"System"} ${labels[view]}`:view==="requirements"?`${discipline==="software"?"Software":"System"} ${labels[view]}`:view==="verification"?`${discipline==="softwareTest"?"Software":"System"} Verification`:labels[view];
  const scopeSwitch=view==="history"?<div className="contextScopeSwitch" role="group" aria-label="Engineering scope"><button aria-pressed={historyTypeIntent==="All"} onClick={()=>navigate("history",discipline,undefined,undefined,false,historyStateIntent,"All")}>All</button><button aria-pressed={historyTypeIntent!=="All"&&discipline!=="software"} onClick={()=>navigate("history","system",undefined,undefined,false,historyStateIntent,"System")}>System</button><button aria-pressed={historyTypeIntent!=="All"&&discipline==="software"} onClick={()=>navigate("history","software",undefined,undefined,false,historyStateIntent,"Software")}>Software</button></div>:view==="requirements"?<div className="contextScopeSwitch" role="group" aria-label="Engineering scope"><button aria-pressed={discipline!=="software"} onClick={()=>navigate(view,"system")}>System</button><button aria-pressed={discipline==="software"} onClick={()=>navigate(view,"software")}>Software</button></div>:view==="verification"?<div className="contextScopeSwitch" role="group" aria-label="Verification scope"><button aria-pressed={discipline!=="softwareTest"} onClick={()=>navigate("verification","systemTest")}>System</button><button aria-pressed={discipline==="softwareTest"} onClick={()=>navigate("verification","softwareTest")}>Software</button></div>:null;
  const copyLink=async()=>{try{await navigator.clipboard.writeText(location.href);setToast('Link copied to clipboard')}catch{setToast('This browser blocked clipboard access')}};
  const contextBar=<div className="contextBar"><nav aria-label="Breadcrumb"><span title={active?.program.name}>{active?.program.name}</span><b aria-hidden="true">›</b><span title={project?.project.name}>{project?.project.name}</span><b aria-hidden="true">›</b><span>{release?.version}</span><b aria-hidden="true">›</b><strong>{scopedLabel}</strong></nav><div className="contextActions">{scopeSwitch}<span className="contextReleaseState">{release?.isReleased?"Released":"In work"}</span><button aria-label="Copy link to this page" onClick={copyLink}>Copy link</button></div></div>;
  const palette=context?<CommandPalette api={API} context={context} open={paletteOpen} onClose={()=>setPaletteOpen(false)} onNavigate={navigate}/>:null;
  const experience=<ExperienceControls open={displayOpen} density={density} motion={motion} onDensityChange={next=>{setDensity(next);setToast(`${next==='compact'?'Compact':'Comfortable'} density applied`)}} onMotionChange={next=>{setMotion(next);setToast(`${next==='reduced'?'Reduced':'Purposeful'} motion applied`)}} onClose={()=>setDisplayOpen(false)}/>;
  const feedback=toast?<div className="experienceToast" role="status" aria-live="polite"><span>✓</span><b>{toast}</b></div>:null;
  const overlays=<>{palette}{experience}{feedback}</>;
  const inShell=(content:React.ReactNode)=><div className="shell">{navigation}<div className="workspaceStage">{contextBar}<div className="workspaceView" key={`${view}-${discipline}`}>{content}</div></div>{overlays}</div>;
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
        sourceRequirementId={selectedArtifactId || undefined}
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
          onOpenScr={(id) => navigate("scr", discipline, id)}
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
        activeReleaseId={release?.id??""}
        scope={historyTypeIntent??(discipline === "software" ? "Software" : "System")}
        initialStateIntent={historyStateIntent}
        onStateIntentChange={(stateIntent)=>{
          setHistoryStateIntent(stateIntent);
          if(context)history.replaceState({},"",routePath(context,"history",discipline,undefined,undefined,stateIntent,historyTypeIntent));
        }}
        onTypeIntentChange={(typeIntent)=>navigate("history",typeIntent==="Software"?"software":typeIntent==="System"?"system":discipline,undefined,undefined,true,historyStateIntent,typeIntent)}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
      />
    );
  if (view === "requirements" && project)
    return inShell(
      <RequirementsWorkspace
        api={API}
        projectId={project.project.id}
        scope={discipline === "software" ? "Software" : "System"}
        initialViewId={initialRoute.savedViewId}
        initialArtifactId={view === "requirements" ? selectedArtifactId || undefined : undefined}
        onScopeChange={(nextScope) => navigate("requirements",nextScope==="Software"?"software":"system")}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onProposeChange={(id) => navigate(discipline === "software" ? "createSoftwareChange" : "createSystemScr", discipline, id)}
        onOpenRequirement={(id) => navigate("requirements",discipline,id)}
        onCloseRequirement={() => navigate("requirements", discipline, undefined, undefined, true)}
        onOpenTraceability={() => navigate("lifecycle")}
      />
    );
  if (view === "verification" && project && release)
    return inShell(
      <VerificationCenter
        api={API}
        programId={active?.program.id ?? ""}
        projectId={project.project.id}
        releaseId={release.id}
        scope={discipline === "softwareTest" ? "Software" : "System"}
        user={user}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "problemReports" && project)
    return inShell(
      <ProblemReportCenter
        api={API}
        projectId={project.project.id}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenVerification={() => navigate("verification", "systemTest")}
      />
    );
  if (view === "lifecycle" && project)
    return inShell(
      <LifecycleExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        activeReleaseId={release?.id??""}
        onBack={() => navigate("dashboard")}
      />
    );
  if (["release","releaseImpact","releaseDecision"].includes(view) && project)
    return inShell(
      <LifecycleDecisionRoom
        api={API}
        projectId={project.project.id}
        activeReleaseId={release?.id ?? ""}
        releases={project.releases}
        user={user}
        screen={view === "releaseImpact" ? "impact" : view === "releaseDecision" ? "decision" : "readiness"}
        selectedScrId={view === "releaseImpact" ? selectedArtifactId || undefined : undefined}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr","system",id)}
        onOpenImpact={(id) => navigate("releaseImpact","system",id)}
        onOpenDecision={() => navigate("releaseDecision")}
        onBackToReadiness={() => navigate("release")}
        onOpenPlanning={() => navigate("planning")}
        onOpenVerification={() => navigate("verification","systemTest")}
        onOpenDocuments={() => navigate("lifecycle")}
        onOpenOperations={() => navigate("releaseOperations")}
      />
    );
  if (view === "releaseOperations" && project)
    return inShell(
      <ReleaseCampaignCenter
        api={API}
        projectId={project.project.id}
        activeReleaseId={release?.id ?? ""}
        releases={project.releases}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenPlanning={() => navigate("planning")}
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
  if (view === "integrations" && project)
    return inShell(
      <IntegrationCommandCenter
        api={API}
        projectId={project.project.id}
        releaseId={release?.id??""}
        onBack={() => navigate("dashboard")}
      />
    );
  const activeCampaign=campaigns.find(x=>x.releaseId===release?.id);
  const dashboardScope:Discipline=discipline==="software"?"software":"system";
  const referenceRelease=[...(project?.releases??[])].reverse().find(x=>x.isReleased);
  const releaseAction=release?.isReleased
    ? {label:"View product versions →",target:"planning" as View}
    : !activeCampaign
      ? {label:"Set up release readiness →",target:"planning" as View}
      : activeCampaign.readiness.readyForRelease
        ? {label:"Review release package →",target:"release" as View}
        : {label:"Resolve release blockers →",target:"release" as View};
  const dashboardMetrics:{label:string;value:number;color:string;accessibleLabel:string;stateIntent?:HistoryStateIntent}[]=[
    {label:"Total changes",value:metrics.totalScrs,color:"#1d66f5",accessibleLabel:"All controlled changes"},
    {label:"In draft",value:metrics.draft,color:"#a16a12",accessibleLabel:"Draft changes",stateIntent:"Draft"},
    {label:"In review",value:metrics.inReview,color:"#7552d6",accessibleLabel:"Changes awaiting review",stateIntent:"InReview"},
    {label:"Approved / selected",value:metrics.approved,color:"#16815f",accessibleLabel:"Approved and baseline-selected changes",stateIntent:"ApprovedOrSelected"},
  ];
  return (
    <div className="shell">
      {navigation}
      <div className="workspaceStage"><div className="workspaceView" key={`${view}-${discipline}`}><main className="commandCenterPage">
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
              {activeCampaign?.readiness.percent ?? 0}
              %
            </b>
            <span>Release readiness</span>
            <div>
              <i
                style={{
                  width: `${activeCampaign?.readiness.percent ?? 0}%`,
                }}
              />
            </div>
          </div>
          <button onClick={() => navigate(releaseAction.target)}>{releaseAction.label}</button>
        </section>
        <section className="metrics" aria-busy={dashboardLoading} aria-label="Change request metrics">
          {dashboardLoading ? Array.from({length:4},(_,index)=><div className="dashboardSkeleton" key={index}><span className="skeletonLine medium"/><i className="skeletonMetric"/><span className="skeletonLine short"/></div>) : dashboardMetrics.map(({label,value,color,accessibleLabel,stateIntent}) => (
            <button
              key={label}
              type="button"
              aria-label={`Open ${accessibleLabel}`}
              onClick={()=>navigate("history",dashboardScope,undefined,undefined,false,stateIntent,"All")}
              style={{ "--accent": color } as React.CSSProperties}
            >
              <span>{label}</span>
              <strong>{value}</strong>
              <small>Open matching records →</small>
            </button>
          ))}
        </section>
        {overview && overview.systemRequirements > 0 && (
          <section className="programInventory">
            <div>
              <p className="eyebrow">{referenceRelease?`RELEASED ${referenceRelease.version} PRODUCT BASELINE`:"CONTROLLED PRODUCT FOUNDATION"}</p>
              <h3>Complete {project?.project.softwareProduct??"product"} lifecycle inventory</h3>
              <span>
                {release?.isReleased?`Release ${release.version} is the selected controlled context.`:`Release ${release?.version??"in work"} inherits this controlled foundation.`}
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
                <button onClick={() => navigate("history",dashboardScope)}>
                  Search released changes
                </button>
              ) : (
                <button onClick={() => navigate(dashboardScope==="software"?"createSoftwareChange":"createSystemScr",dashboardScope)}>+ New {dashboardScope==="software"?"Software SWCR":"System SCR"}</button>
              )}
            </div>
            {dashboardLoading ? (
              <div className="dashboardRowSkeletons" aria-label="Loading change requests">{Array.from({length:4},(_,index)=><div className="row dashboardSkeleton" key={index}><span className="skeletonAvatar"/><div><span className="skeletonLine medium"/><span className="skeletonLine"/></div><span className="skeletonPill"/><span className="skeletonLine"/></div>)}</div>
            ) : scrs.length ? (
              scrs.slice(0,5).map((scr) => (
                <div
                  className="row"
                  key={scr.id}
                  role="button"
                  tabIndex={0}
                  onClick={() => navigate("scr",dashboardScope,scr.id)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      navigate("scr",dashboardScope,scr.id);
                    }
                  }}
                >
                  <div className="scricon">SCR</div>
                  <div>
                    <b>
                      {scr.displayNumber} · {scr.title}
                    </b>
                    <p>{scr.requirementCount} requirement change{scr.requirementCount === 1 ? "" : "s"} · <span className="personMeta"><i>{identityInitials(scr.authorId)}</i>{identityLabel(scr.authorId)}</span></p>
                  </div>
                  <span className={`state ${scr.state.toLowerCase()}`}>
                    {stateLabel(scr.state)}
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
            {scrs.length>5&&<div className="panelFooter"><span>Showing the 5 most recent of {scrs.length} change requests</span><button onClick={()=>navigate("history",dashboardScope)}>View complete history →</button></div>}
          </div>
          <div className="panel attention">
            <div className="panelhead">
              <div>
                <h3>{metrics.totalScrs?"Release attention":"Workspace readiness"}</h3>
                <p>{metrics.totalScrs?"The decisions and work that need focus now":"Foundation for controlled development"}</p>
              </div>
            </div>
            {metrics.totalScrs?<><button className={metrics.inReview?"signal amber":"signal green"} onClick={()=>navigate(metrics.inReview?"mywork":"history",dashboardScope,undefined,undefined,false,metrics.inReview?undefined:"InReview",metrics.inReview?undefined:"All")}><b>Awaiting review decisions</b><strong>{metrics.inReview}</strong><p>{metrics.inReview?"Open the accountable review queue":"No change requests are waiting for review"}</p></button><button className={metrics.draft?"signal blue":"signal green"} onClick={()=>navigate("history",dashboardScope,undefined,undefined,false,"Draft","All")}><b>Draft change packages</b><strong>{metrics.draft}</strong><p>{metrics.draft?"Open controlled work in progress":"No draft change packages remain"}</p></button><button className={activeCampaign?.readiness.readyForRelease?"signal green":"signal amber"} onClick={()=>navigate(activeCampaign?"release":"planning")}><b>Release readiness</b><strong>{activeCampaign?.readiness.percent??0}%</strong><p>{activeCampaign?.readiness.readyForRelease?"Review the complete release package":"Open the next release-readiness action"}</p></button></>:<><div className="signal green"><b>Program workspace</b><strong>✓</strong><p>{active?.program.name} is configured</p></div><div className="signal blue"><b>Initial release</b><strong>✓</strong><p>{release?.version} establishes the starting context</p></div><button className="signal amber" onClick={()=>navigate(dashboardScope==="software"?"createSoftwareChange":"createSystemScr",dashboardScope)}><b>Next action</b><strong>1</strong><p>Create the first controlled change request →</p></button></>}
          </div>
        </section>
      </main></div></div>
      {overlays}
    </div>
  );
}
export default App;
