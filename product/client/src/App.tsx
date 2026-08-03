import { lazy, Suspense, useCallback, useEffect, useState } from "react";
import type { ComponentType, FormEvent } from "react";
import CommandPalette from "./CommandPalette";
import { officialBuildName } from "./presentation";
import ExperienceControls from "./ExperienceControls";
import type { MotionPreference, WorkspaceDensity } from "./ExperienceControls";
import { readRoute, routePath } from "./routing";
import type { AppRoute, Discipline, HistoryStateIntent, HistoryTypeIntent, RouteContext, View } from "./routing";
import { usePasswordVisibilityControls } from "./PasswordVisibility";
import {
  AdministrationCenter,
  LoginPage,
  MyWorkCenter,
  RequiredPasswordChange,
} from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import { PersonAvatar } from "./People";
import ProjectsLanding from "./ProjectsLanding";
import SoftwareBuildsLanding from "./SoftwareBuildsLanding";
// Eager, unlike the other fourteen workspaces. See the note above `lazyView`.
import EnterpriseControlCenter from "./EnterpriseControlCenter";
import { apiRequest, operationError, recordClientOperationFailure } from "./apiClient";
import "./App.css";
import "./Onboarding.css";
import "./DashboardInteractions.css";
import "./Showcase.css";
import "./PortalNavigation.css";
import "./ShowcaseRefresh.css";
import "./ExperiencePolish.css";
import "./People.css";
import "./CohesionPass.css";

/**
 * Each workspace is fetched the first time somebody opens it, rather than every time anybody signs in.
 *
 * Nobody uses all fifteen of these in one sitting. A test engineer recording determinations never opens the
 * integration center; an approver reading a change request never opens the requirements explorer. Loading
 * every one of them up front means the whole product must be parsed and executed before the Command Center
 * paints, and on the modest workstation this is specified to run on that is CPU time, not bandwidth — which
 * is why it costs the same on a fast local network as on a slow one.
 *
 * A route arriving a moment late would be a poor trade, so `warm` fetches a workspace's code the instant a
 * navigation entry is hovered or focused, well before the click lands.
 *
 * System Operations is deliberately not in this list. Splitting a workspace out also moves its stylesheet,
 * which then arrives after everything already on the page instead of in its usual place — and CohesionPass.css
 * corrects the type size on thirty-seven of that surface's selectors by being loaded later, not by being more
 * specific. Split it and every one of those reverts to 7–9 px production text, which is the defect the
 * cohesion pass was written to fix. Untangling that means the rules belong to the surface rather than to a
 * catch-all file, which is on the roadmap; until then this one workspace is worth 40 kB.
 */
function lazyView<P>(load: () => Promise<{ default: ComponentType<P> }>) {
  return Object.assign(lazy(load), { warm: () => { void load(); } });
}

const ScrEditor = lazyView(() => import("./ScrEditor"));
const ScrWorkspace = lazyView(() => import("./ScrWorkspace"));
const BaselineCenter = lazyView(() => import("./BaselineCenter"));
const HistoryExplorer = lazyView(() => import("./HistoryExplorer"));
const VerificationLanding = lazyView(() => import("./VerificationLanding"));
const DocumentCenter = lazyView(() => import("./DocumentCenter"));
const ProblemReportCenter = lazyView(() => import("./ProblemReportCenter"));
const CodeTraceabilityCenter = lazyView(() => import("./CodeTraceabilityCenter"));
const LifecycleExplorer = lazyView(() => import("./LifecycleExplorer"));
const ReleaseCampaignCenter = lazyView(() => import("./ReleaseCampaignCenter"));
const LifecycleDecisionRoom = lazyView(() => import("./LifecycleDecisionRoom"));
const ReleasePlanningCenter = lazyView(() => import("./ReleasePlanningCenter"));
const RequirementsWorkspace = lazyView(() => import("./RequirementsWorkspace"));
const IntegrationCommandCenter = lazyView(() => import("./IntegrationCommandCenter"));
const ReviewWorkflowCenter = lazyView(() => import("./ReviewWorkflowCenter"));
const TestResultsWorkspace = lazyView(() => import("./TestResultsWorkspace"));
const TestingCoverageWorkspace = lazyView(() => import("./TestingCoverageWorkspace"));
const ArtifactRecordPage = lazyView(() => import("./ArtifactRecordPage"));

/** Which code a navigation target needs, so hovering the entry can start fetching it. */
const viewCode: Partial<Record<View, { warm: () => void }>> = {
  scr: ScrWorkspace,
  createSystemScr: ScrEditor,
  createSoftwareChange: ScrEditor,
  baselines: BaselineCenter,
  history: HistoryExplorer,
  requirements: RequirementsWorkspace,
  verification: VerificationLanding,
  testResults: TestResultsWorkspace,
  testingCoverage: TestingCoverageWorkspace,
  documents: DocumentCenter,
  problemReports: ProblemReportCenter,
  code: CodeTraceabilityCenter,
  lifecycle: LifecycleExplorer,
  release: LifecycleDecisionRoom,
  releaseImpact: LifecycleDecisionRoom,
  releaseDecision: LifecycleDecisionRoom,
  releaseOperations: ReleaseCampaignCenter,
  planning: ReleasePlanningCenter,
  reviewWorkflows: ReviewWorkflowCenter,
  integrations: IntegrationCommandCenter,
  artifact: ArtifactRecordPage,
};

/**
 * Shown while a workspace's code is on its way. It holds the shape of a page rather than announcing a wait,
 * because on a local network this is visible for a few milliseconds and a spinner appearing and vanishing
 * reads as a fault. The label is for anybody who cannot see the shape.
 */
function WorkspaceLoading() {
  // A <main>, because the density rules frame every workspace page by that element rather than by name, and
  // a fallback that skipped the frame would shift the page as the real workspace arrived. `aria-busy` rather
  // than role="status", so the landmark stays a landmark.
  return (
    <main className="workspaceLoading" aria-busy="true" aria-label="Opening workspace">
      <div className="dashboardSkeleton"><span className="skeletonLine medium"/><i className="skeletonMetric"/><span className="skeletonLine short"/></div>
      <div className="dashboardSkeleton"><span className="skeletonLine"/><span className="skeletonLine medium"/><span className="skeletonLine short"/></div>
    </main>
  );
}

type ChangeMetrics = {
  total: number;
  draft: number;
  inReview: number;
  approved: number;
  deferred: number;
};
type VerificationMetrics = {
  totalChangeRequests: number;
  triagedChangeRequests: number;
  openDecisions: number;
  resolvedDecisions: number;
};
type Metrics = {
  system: ChangeMetrics;
  software: ChangeMetrics;
  verification: { system: VerificationMetrics; hlr: VerificationMetrics; llr: VerificationMetrics };
};
type Release = { id: string; version: string; isReleased: boolean };
type Workspace = {
  program: { id: string; name: string; code: string };
  projects: {
    project: { id: string; name: string; softwareProduct: string };
    releases: Release[];
  }[];
};
/**
 * Where the API is.
 *
 * A production build defaults to the empty string, which makes every request relative and therefore
 * same-origin: the API process serves this bundle, so it is already the right host, whatever address the
 * workstation answers on. Baking one in would mean a build that only runs on the machine it was built for.
 *
 * `npm run dev` serves the client on its own port and has to be told where the API is, so the development
 * default points at the local one. `VITE_API_URL` overrides either, which is how the browser journeys aim at
 * their own isolated instance.
 */
const API = import.meta.env.VITE_API_URL ?? (import.meta.env.DEV ? "http://127.0.0.1:5080" : "");

function AppNavigation({ user, workspaces, activeId, selectedProjectId, selectedReleaseId, view, discipline, artifactKind, context, density, onNavigate, onSearch, onDisplay, onExitBuild, onSignOut }:{
  user:AuthUser;workspaces:Workspace[];activeId:string;selectedProjectId:string;selectedReleaseId:string;view:View;discipline:Discipline;context?:RouteContext;
  artifactKind:string;density:WorkspaceDensity;onNavigate:(view:View,discipline?:Discipline,artifactId?:string,artifactKind?:string)=>void;onSearch:()=>void;onDisplay:()=>void;onExitBuild:()=>void;onSignOut:()=>void;
}) {
  const active = workspaces.find(x => x.program.id === activeId) ?? workspaces[0];
  const project = active?.projects.find(x => x.project.id === selectedProjectId) ?? active?.projects[0];
  const release = project?.releases.find(x => x.id === selectedReleaseId) ?? project?.releases.at(-1);
  const officialBuild = release ? officialBuildName(release.version) : "";
  // `kind` carries the software level for the verification pages, which split into HLR and LLR. It is the
  // same field the change-request routes use for it, so no new axis had to be threaded through the shell.
  const item = (label:string,target:View,icon:string,area:Discipline="system",accessibleLabel=label,kind?:string,topLevel=false) => {
    const activeItem = (view===target || (target==="history" && view==="scr") || (target==="release" && ["releaseImpact","releaseDecision","releaseOperations"].includes(view))) && discipline===area && (!kind || artifactKind===kind);
    // Fetched on hover or keyboard focus, so the workspace's code is usually already here by the time the
    // click is. Both events, because a keyboard user never hovers anything.
    const warm = () => viewCode[target]?.warm();
    return <a href={context ? routePath(context,target,area,undefined,kind) : "#"} className={`${topLevel?"navSectionLink ":""}${activeItem?"active":""}`.trim()} aria-label={accessibleLabel} aria-current={activeItem?"page":undefined} onPointerEnter={warm} onFocus={warm} onClick={event=>{event.preventDefault();onNavigate(target,area,undefined,kind)}}>
      <i aria-hidden="true">{icon}</i><span>{topLevel?label.toUpperCase():label}</span>
    </a>;
  };
  const engineeringView = ["createSystemScr","createSoftwareChange","history","requirements","scr","lifecycle"].includes(view)
    || (view === "documents" && (discipline === "system" || discipline === "software"));
  const releaseView = ["release","releaseImpact","releaseDecision","releaseOperations","enterprise"].includes(view);
  const engineeringScope:Discipline = discipline==="software" ? "software" : "system";
  const verificationScope:Discipline = discipline==="softwareTest"||discipline==="software" ? "softwareTest" : "systemTest";
  return (
    <aside className="appNavigation">
      <div className="brand"><span aria-hidden="true">▲</span><b>AeroLink</b></div>
      <button className="quickSearch" onClick={onSearch}><span aria-hidden="true">⌕</span> Search &amp; navigate <kbd>Ctrl K</kbd></button>
      <div className="program">
        <small>ACTIVE CONTEXT</small>
        <strong className="activeProgram" title={active?.program.name}>{active?.program.name}</strong>
        <span title={project?.project.name}>{project?.project.name}</span>
        {/* "Active build <version>" stays contiguous so the informal version a person reads elsewhere —
            the breadcrumb says "Build 1.6" — is how this can be found by name too. */}
        {/* Named the way the breadcrumb names it. The card led with the configuration identifier, SW-01.60,
            which is the build's formal name and nobody's shorthand for it: every other surface, and every
            person, says "Build 1.6". Three type sizes competing in one small card did not help either, so the
            standing "BUILD" label goes and the name carries it. The formal identifier stays reachable on
            hover and in the accessible name, because it is what a configuration record is filed under. */}
        <div className="activeBuildIdentity" aria-label={`Active build ${release?.version} (${officialBuild})`} title={officialBuild}>
          <strong>Build {release?.version}</strong>
          <span>{release?.isReleased ? "Released · read-only" : "In work"}</span>
        </div>
        <button type="button" className="exitBuild" onClick={onExitBuild}>← Back to Software Builds</button>
      </div>
      <nav className="primaryNavigation" aria-label="Primary navigation">
        <div className="navHome">{item("Command Center","dashboard","⌂")}{item("My Work","mywork","◎")}</div>
        <details className="navGroup" open={engineeringView}><summary>REQUIREMENTS</summary><div className="navScopeSwitch" role="group" aria-label="Requirements scope"><button type="button" aria-pressed={engineeringScope==="system"} onClick={()=>onNavigate(view==="history"||view==="requirements"||view==="documents"?view:"history","system")}>System</button><button type="button" aria-pressed={engineeringScope==="software"} onClick={()=>onNavigate(view==="history"||view==="requirements"||view==="documents"?view:"history","software")}>Software</button></div>{item("Change Requests","history","◇",engineeringScope,engineeringScope==="software"?"Software Change Requests":"System Change Requests")}{item("Requirements Explorer","requirements","≡",engineeringScope,engineeringScope==="software"?"Software Requirements Explorer":"System Requirements Explorer")}{item("Documents","documents","▤",engineeringScope,engineeringScope==="software"?"Software Requirements Documents":"System Requirements Documents")}{item("Digital Thread","lifecycle","↗","system","Digital Thread")}</details>
        <details className="navGroup" open={view==="verification"||view==="testingCoverage"||view==="testResults"||(view==="documents"&&(discipline==="systemTest"||discipline==="softwareTest"))}><summary>VERIFICATION</summary><div className="navScopeSwitch" role="group" aria-label="Verification scope"><button type="button" aria-pressed={verificationScope==="systemTest"} onClick={()=>onNavigate("verification","systemTest")}>System</button><button type="button" aria-pressed={verificationScope==="softwareTest"} onClick={()=>onNavigate("verification","softwareTest")}>Software</button></div>{verificationScope==="softwareTest"
          ? <>{item("HLR Testing Coverage","testingCoverage","◫","softwareTest","Software HLR Testing Coverage","HighLevel")}{item("HLR Test Results","testResults","▦","softwareTest","Software HLR Test Results","HighLevel")}{item("LLR Testing Coverage","testingCoverage","◫","softwareTest","Software LLR Testing Coverage","LowLevel")}{item("LLR Test Results","testResults","▦","softwareTest","Software LLR Test Results","LowLevel")}</>
          : <>{item("Testing Coverage","testingCoverage","◫","systemTest","System Testing Coverage")}{item("Test Results","testResults","▦","systemTest","System Test Results")}</>}{item("Documents","documents","▤",verificationScope,verificationScope==="softwareTest"?"Software Verification Documents":"System Verification Documents")}</details>
        <div className="navStandalone">{item("Code","code","{ }","software","Code traceability",undefined,true)}</div>
        <div className="navStandalone">{item("Problem Reports","problemReports","!","system","Problem Reports",undefined,true)}</div>
        <details className="navGroup" open={releaseView}><summary>RELEASE</summary>{item("Lifecycle Decision Room","release","◆","system","Lifecycle Decision Room / Release Readiness")}</details>
        {user.isAdministrator&&<details className="navGroup" open={view==="admin"||view==="enterprise"||view==="integrations"||view==="reviewWorkflows"}><summary>ADMINISTRATION</summary>{item("People & Authority","admin","⚙")}{item("Review Workflows","reviewWorkflows","⇉","system","Review Workflows / Change Review Procedure")}{item("Integration Center","integrations","↗","system","Integration Command Center")}{item("System Operations","enterprise","◈","system","System Operations / Enterprise Control")}</details>}
      </nav>
      <footer><PersonAvatar userName={user.userName} displayName={user.displayName} size="large"/><div><b>{user.displayName}</b><small>{user.userName}</small></div><button className="signOut" onClick={onSignOut}>Sign out</button><button className="workspaceDisplay" onClick={onDisplay} aria-label="Open workspace display settings"><span>Aa</span><div><b>Workspace display</b><small>{density} density</small></div><i aria-hidden="true">›</i></button></footer>
    </aside>
  );
}

function App() {
  usePasswordVisibilityControls();
  const [user, setUser] = useState<AuthUser | null | undefined>(undefined);
  const [initialRoute] = useState<AppRoute>(() => readRoute());
  const [metrics, setMetrics] = useState<Metrics>({
      system:{total:0,draft:0,inReview:0,approved:0,deferred:0},
      software:{total:0,draft:0,inReview:0,approved:0,deferred:0},
      verification:{
        system:{totalChangeRequests:0,triagedChangeRequests:0,openDecisions:0,resolvedDecisions:0},
        hlr:{totalChangeRequests:0,triagedChangeRequests:0,openDecisions:0,resolvedDecisions:0},
        llr:{totalChangeRequests:0,triagedChangeRequests:0,openDecisions:0,resolvedDecisions:0},
      },
    }),
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
    [pendingAssessmentLink,setPendingAssessmentLink]=useState<{assessmentId:string;targetLevel:'HighLevel'|'LowLevel';sourceNumber:string;changeRequestId?:string}>(),
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
      const routedProgram=initialRoute.programId?next.find(x=>x.program.id===initialRoute.programId):undefined;
      const routedProject=initialRoute.projectId?routedProgram?.projects.find(x=>x.project.id===initialRoute.projectId):undefined;
      const routedRelease=initialRoute.releaseId?routedProject?.releases.find(x=>x.id===initialRoute.releaseId):undefined;
      if((initialRoute.programId||initialRoute.projectId||initialRoute.releaseId)&&(!routedProgram||!routedProject||!routedRelease))setView("notFound");
      const program=routedProgram??next[0],project=routedProject??program?.projects[0],release=routedRelease??[...(project?.releases??[])].reverse().find(x=>!x.isReleased)??project?.releases.at(-1);
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
      const response = await fetch(
        `${API}/api/dashboard?projectId=${project.project.id}&releaseId=${release?.id ?? ""}`,
      );
      if (!response.ok) throw new Error("Dashboard unavailable.");
      setMetrics(await response.json());
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
    if (saving) return;
    setSaving(true);
    setError("");
    const form = new FormData(e.currentTarget),
      body = {
        ...Object.fromEntries(form),
        initialReleaseIsReleased: form.has("initialReleaseIsReleased"),
      };
    try {
      const created = await apiRequest<{ program: { id: string } }>(`${API}/api/workspaces`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      setActiveId(created.program.id);
      await loadWorkspaces();
    } catch (reason) {
      recordClientOperationFailure("workspace.create", reason);
      setError(operationError(reason, "Unable to create program."));
    } finally {
      setSaving(false);
    }
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
  const linkPendingAssessment=async(changeRequestId:string)=>{
    if(!pendingAssessmentLink)return true
    try{await apiRequest(`${API}/api/downstream-assessments/${pendingAssessmentLink.assessmentId}/change-requests`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({changeRequestId})});setPendingAssessmentLink(undefined);return true}
    catch{return false}
  }
  const openVerificationProcedure=(procedure?:{procedureId:string;revisionId?:string;displayNumber?:string;level?:string})=>{
    const area:Discipline=procedure?.level==="System"?"systemTest":"softwareTest";
    setView("testingCoverage");setDiscipline(area);setSelectedArtifactId("");setSelectedArtifactKind(procedure?.level??"");
    if(context){const path=routePath(context,"testingCoverage",area,undefined,procedure?.level);const params=new URLSearchParams();if(procedure?.displayNumber)params.set("procedure",procedure.displayNumber);if(procedure?.procedureId)params.set("procedureId",procedure.procedureId);if(procedure?.revisionId)params.set("procedureRevisionId",procedure.revisionId);history.pushState({},"",`${path}${params.size?`?${params}`:""}`)}
  };
  if(location.pathname==="/")history.replaceState({},"","/projects");
  const signOut=async()=>{
    // Signing out must not be able to fail. Logout is a mutation, so the patched fetch first fetches a CSRF
    // token from /api/auth/csrf — which is itself behind the session gate and answers 401 once a session has
    // gone. The token fetch then throws, the await rejects, and this handler used to end right there with
    // `setUser(null)` never reached: the shell stayed exactly as it was and Sign out did nothing. An expired
    // session is precisely when somebody reaches for that button, so the local session is cleared whatever
    // the server says. The server side is already correct — it revokes the session and deletes the cookie.
    try { await fetch(`${API}/api/auth/logout`,{method:"POST"}) } catch { /* the session is gone either way */ }
    setUser(null);
  };
  const buildsPath=routePath(context??{programId:"",projectId:"",releaseId:""},"builds");
  const showProjects=()=>{setView("projects");history.pushState({},"","/projects")};
  const exitBuild=()=>{setPaletteOpen(false);setDisplayOpen(false);setView("builds");setSelectedArtifactId("");setSelectedArtifactKind("");setSelectedScrId("");history.pushState({},"",buildsPath)};
  if(view==="projects")return <ProjectsLanding user={user} workspaceHref={buildsPath} onOpenWorkspace={()=>{setView("builds");history.pushState({},"",buildsPath)}} onSignOut={signOut}/>;
  if(view==="builds")return <SoftwareBuildsLanding user={user} releases={project?.releases??[]} onProjectOverview={showProjects} onOpenBuild={(selected)=>{if(!active||!project||!project.releases.some(item=>item.id===selected.id))return;setSelectedReleaseId(selected.id);setView("dashboard");history.pushState({},"",routePath({programId:active.program.id,projectId:project.project.id,releaseId:selected.id},"dashboard"))}} onSignOut={signOut}/>;
  const navigation=<AppNavigation user={user} workspaces={workspaces} activeId={activeId} selectedProjectId={project?.project.id??selectedProjectId} selectedReleaseId={release?.id??selectedReleaseId} view={view} discipline={discipline} artifactKind={selectedArtifactKind} context={context} density={density} onNavigate={navigate} onSearch={()=>setPaletteOpen(true)} onDisplay={()=>setDisplayOpen(true)} onExitBuild={exitBuild} onSignOut={signOut}/>;
  const labels:Record<View,string>={projects:"Projects",builds:"Software Builds",dashboard:"Command Center",createSystemScr:"New System SCR",createSoftwareChange:"New Software SWCR",scr:"Change Request",baselines:"Baselines",history:"Change Requests",requirements:"Requirements Explorer",verification:"Verification",testingCoverage:"Testing Coverage",testResults:"Test Results",documents:"Documents",code:"Code",problemReports:"Problem Reports",lifecycle:"Digital Thread",release:"Release Readiness",releaseImpact:"Change Impact Review",releaseDecision:"Release Evidence & Decision",releaseOperations:"Release Operations",planning:"Product Versions",mywork:"My Work",admin:"Administration",enterprise:"System Operations",integrations:"Integration Command Center",reviewWorkflows:"Review Workflows",artifact:"Artifact",notFound:"Not Found"};
  const scopedLabel=view==="history"?`${discipline==="software"?"Software":"System"} ${labels[view]}`:view==="scr"?`${discipline==="software"?"Software":"System"} ${labels[view]}`:view==="requirements"?`${discipline==="software"?"Software":"System"} ${labels[view]}`:view==="verification"?`${discipline==="softwareTest"?"Software":"System"} Verification`:labels[view];
  const copyLink=async()=>{try{await navigator.clipboard.writeText(location.href);setToast('Link copied to clipboard')}catch{setToast('This browser blocked clipboard access')}};
  const contextBar=<div className="contextBar"><nav aria-label="Breadcrumb"><span title={active?.program.name}>{active?.program.name}</span><b aria-hidden="true">›</b><span title={project?.project.name}>{project?.project.name}</span><b aria-hidden="true">›</b><span>Build {release?.version}</span><b aria-hidden="true">›</b><strong>{scopedLabel}</strong></nav><div className="contextActions"><span className="contextReleaseState">{release?.isReleased?"Released · read-only":"In work"}</span><button aria-label="Copy link to this page" onClick={copyLink}>Copy link</button></div></div>;
  const palette=context?<CommandPalette api={API} context={context} open={paletteOpen} onClose={()=>setPaletteOpen(false)} onNavigate={navigate}/>:null;
  const experience=<ExperienceControls open={displayOpen} density={density} motion={motion} onDensityChange={next=>{setDensity(next);setToast(`${next==='compact'?'Compact':'Comfortable'} density applied`)}} onMotionChange={next=>{setMotion(next);setToast(`${next==='reduced'?'Reduced':'Purposeful'} motion applied`)}} onClose={()=>setDisplayOpen(false)}/>;
  const feedback=toast?<div className="experienceToast" role="status" aria-live="polite"><span>✓</span><b>{toast}</b></div>:null;
  const overlays=<>{palette}{experience}{feedback}</>;
  const inShell=(content:React.ReactNode)=><div className="shell">{navigation}<div className="workspaceStage">{contextBar}<div className="workspaceView" key={`${view}-${discipline}`}><Suspense fallback={<WorkspaceLoading/>}>{content}</Suspense></div></div>{overlays}</div>;
  if(view==="notFound")return inShell(<main className="artifactState"><div><span>?</span><h1>Page not found</h1><p>This AeroLink route is not recognized. Use quick navigation to find an authorized workspace or artifact.</p><button onClick={()=>navigate("dashboard")}>Return to Command Center</button></div></main>);
  if(view==="artifact"&&selectedArtifactId&&selectedArtifactKind)return inShell(<ArtifactRecordPage api={API} kind={selectedArtifactKind} id={selectedArtifactId} onBack={()=>navigate("dashboard")} onOpen={(kind,id)=>{if(kind==="change-request")navigate("scr","system",id);else if(kind==="requirement")navigate("requirements","system",id);else navigate("artifact","system",id,kind)}}/>);
  // A released build is closed, so this says so instead of opening an editor whose save the server will refuse.
  // The action stays visible on the navigation rather than disappearing: somebody looking for how to raise a
  // change needs to be told where to raise it, and a menu item that vanishes when you switch build teaches
  // nothing. Enforced server-side too — see ReleasedBuildRefusalAsync — because this panel is a courtesy.
  if ((view === "createSystemScr" || view === "createSoftwareChange") && project && release?.isReleased) {
    const inWork = [...project.releases].reverse().find((item) => !item.isReleased);
    return inShell(
      <main className="closedReleaseNotice">
        <button className="back" type="button" onClick={() => navigate("dashboard")}>← Command Center</button>
        <p className="eyebrow">CHANGE CONTROL / {release.version}</p>
        <h1>{release.version} has been released</h1>
        <p>
          A released build is closed. Its content was fixed when it shipped, so a change request raised against
          it could never reach a baseline or be incorporated — it would be a record filed against a decision
          already made.
        </p>
        {inWork ? (
          <>
            <p>Exit this read-only workspace, then select {inWork.version} from Software Builds.</p>
            <button type="button" className="primary" onClick={exitBuild}>
              Back to Software Builds
            </button>
          </>
        ) : (
          <p>This product has no in-work build available for a new change request.</p>
        )}
      </main>,
    );
  }
  if ((view === "createSystemScr" || view === "createSoftwareChange") && project && release)
    return inShell(
      <ScrEditor
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        scope={view === "createSystemScr" ? "System" : "Software"}
        softwareLevel={view === "createSoftwareChange" && (selectedArtifactKind === "HighLevel" || selectedArtifactKind === "LowLevel") ? selectedArtifactKind : undefined}
        user={user}
        sourceRequirementId={selectedArtifactId || undefined}
        onCancel={() => navigate("dashboard")}
        onSaved={async (scrId, displayNumber) => {
          await loadData();
          // Said out loud, because landing on a new page is not the same as being told the save worked —
          // it was asked for twice. The toast outlives this navigation because it is held up here.
          const linked=await linkPendingAssessment(scrId)
          if(linked)setToast(pendingAssessmentLink?`${displayNumber} saved and linked to the ${pendingAssessmentLink.sourceNumber} downstream assessment.`:`${displayNumber} saved as a Draft.`)
          else{setPendingAssessmentLink(current=>current?{...current,changeRequestId:scrId}:current);setToast(`${displayNumber} saved, but its downstream assessment link needs attention.`)}
          navigate("scr",view === "createSoftwareChange" ? "software" : "system",scrId);
        }}
      />
    );
  if (view === "scr" && selectedScrId)
    return inShell(
      <>{pendingAssessmentLink?.changeRequestId===selectedScrId&&<section className="assessmentLinkRecovery" role="alert"><div><b>Downstream assessment link needs attention</b><span>This Draft is saved. Retry its link to the {pendingAssessmentLink.sourceNumber} assessment.</span></div><button type="button" onClick={async()=>{if(await linkPendingAssessment(selectedScrId))setToast(`Draft linked to the ${pendingAssessmentLink.sourceNumber} downstream assessment.`);else setToast('The downstream assessment link still could not be recorded.')}}>Retry assessment link</button></section>}<ScrWorkspace
        api={API}
        scrId={selectedScrId}
        user={user}
        onBack={() => navigate("history", discipline)}
        onChanged={loadData}
        onOpenScr={(id) => navigate("scr", discipline, id)}
        onOpenRequirement={(id,level)=>navigate("requirements",level==="System"?"system":"software",id)}
        onOpenProblemReport={(id)=>navigate("problemReports","system",id)}
        onDisciplineResolved={(resolved) => {
          if (resolved !== discipline) setDiscipline(resolved);
          if (context) history.replaceState({}, "", routePath(context, "scr", resolved, selectedScrId));
        }}
        releases={release ? [release] : []}
      /></>
    );
  if (view === "baselines" && project && release)
    return inShell(
      <BaselineCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        readOnly={release.isReleased}
        productName={project.project.softwareProduct}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "history" && project)
    return inShell(
      <HistoryExplorer
        api={API}
        projectId={project.project.id}
        releases={release ? [release] : []}
        activeReleaseId={release?.id??""}
        scope={discipline === "software" ? "Software" : "System"}
        initialSoftwareLevel={selectedArtifactKind === "LowLevel" ? "LowLevel" : "HighLevel"}
        initialAssessmentId={selectedArtifactId||undefined}
        initialStateIntent={historyStateIntent}
        onSoftwareLevelChange={(level)=>{
          setSelectedArtifactId("");
          setSelectedArtifactKind(level);
          if(context)history.pushState({},"",routePath(context,"history","software",undefined,level,historyStateIntent,historyTypeIntent));
        }}
        onAssessmentSelected={(id)=>{setSelectedArtifactId(id??"");if(context)history.pushState({},"",routePath(context,"history","software",id,selectedArtifactKind,historyStateIntent,historyTypeIntent))}}
        onStateIntentChange={(stateIntent)=>{
          setHistoryStateIntent(stateIntent);
          if(context)history.replaceState({},"",routePath(context,"history",discipline,undefined,selectedArtifactKind,stateIntent,historyTypeIntent));
        }}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenRequirement={(id,level)=>navigate("requirements",level==="System"?"system":"software",id)}
        onCreateSystem={() => navigate("createSystemScr","system")}
        onCreateSoftware={(level,assessmentId,sourceNumber) => {if(assessmentId&&sourceNumber)setPendingAssessmentLink({assessmentId,targetLevel:level,sourceNumber});navigate("createSoftwareChange","software",undefined,level)}}
        user={user}
      />
    );
  if (view === "requirements" && project)
    return inShell(
      <RequirementsWorkspace
        api={API}
        projectId={project.project.id}
        scope={discipline === "software" ? "Software" : "System"}
        release={release}
        initialViewId={initialRoute.savedViewId}
        initialArtifactId={view === "requirements" ? selectedArtifactId || undefined : undefined}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onProposeChange={(id, level) => navigate(discipline === "software" ? "createSoftwareChange" : "createSystemScr", discipline, id, level)}
        onOpenRequirement={(id) => navigate("requirements",discipline,id)}
        onCloseRequirement={() => navigate("requirements", discipline, undefined, undefined, true)}
        onOpenTraceability={(artifactId) => navigate("lifecycle", discipline, artifactId, artifactId ? "requirement" : undefined)}
        onOpenVerification={openVerificationProcedure}
      />
    );
  // The two paths a build.s verification work splits into.
  if (view === "testingCoverage" && project && release)
    return inShell(
      <TestingCoverageWorkspace
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        discipline={discipline === "softwareTest"
          ? (selectedArtifactKind === "LowLevel" ? "LowLevelSoftware" : "HighLevelSoftware")
          : "System"}
        buildName={`Build ${release.version}`}
        readOnly={release.isReleased}
        programId={active?.program.id ?? ""}
        user={user}
      />
    );

  if (view === "testResults" && project && release)
    return inShell(
      <TestResultsWorkspace
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        discipline={discipline === "softwareTest"
          ? (selectedArtifactKind === "LowLevel" ? "LowLevelSoftware" : "HighLevelSoftware")
          : "System"}
        buildName={`Build ${release.version}`}
        readOnly={release.isReleased}
        programId={active?.program.id ?? ""}
        user={user}
        // Carried in the route, so refreshing or going back returns to the same remediation.
        correctiveProblemReportId={selectedArtifactId || undefined}
        onOpenProcedure={() => navigate("testingCoverage", discipline, undefined, selectedArtifactKind)}
      />
    );

  if (view === "verification" && project && release)
    return inShell(
      <VerificationLanding
        scope={discipline === "softwareTest" ? "Software" : "System"}
        buildName={`Build ${release.version}`}
        onOpen={(target, level) => navigate(target, discipline, undefined, level)}
      />
    );
  if (view === "documents" && project && release)
    return inShell(
      <DocumentCenter
        api={API}
        projectId={project.project.id}
        release={release}
        discipline={discipline}
        onBack={() => navigate(
          discipline === "systemTest" || discipline === "softwareTest" ? "verification" : "requirements",
          discipline,
        )}
      />
    );
  if (view === "problemReports" && project)
    return inShell(
      <ProblemReportCenter
        api={API}
        projectId={project.project.id}
        releaseId={release?.id ?? ""}
        releases={project.releases}
        readOnly={release?.isReleased ?? false}
        user={user}
        initialReportId={selectedArtifactId||undefined}
        onSelected={(id)=>navigate("problemReports","system",id,undefined,true)}
        onBack={() => navigate("dashboard")}
        onOpenVerification={(target) => navigate("testResults", target?.discipline === "software" ? "softwareTest" : "systemTest", target?.problemReportId, target?.discipline === "software" ? "HighLevel" : undefined)}
        onOpenArtifact={(kind,id,identifier)=>{if(kind==="change-request")navigate("scr",identifier?.startsWith("SWCR-")?"software":"system",id);else if(kind==="problem-report")navigate("problemReports","system",id);else if(kind==="requirement")navigate("requirements",identifier?.startsWith("SYSR-")?"system":"software",id);else navigate("artifact","system",id,kind)}}
      />
    );
  if (view === "code" && project && release)
    return inShell(
      <CodeTraceabilityCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        readOnly={release.isReleased}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "lifecycle" && project)
    return inShell(
      <LifecycleExplorer
        api={API}
        projectId={project.project.id}
        releases={release ? [release] : []}
        activeReleaseId={release?.id??""}
        initialArtifactId={selectedArtifactId || undefined}
        onBack={() => navigate("dashboard")}
      />
    );
  if (["release","releaseImpact","releaseDecision"].includes(view) && project)
    return inShell(
      <LifecycleDecisionRoom
        api={API}
        projectId={project.project.id}
        activeReleaseId={release?.id ?? ""}
        releases={release ? [release] : []}
        user={user}
        screen={view === "releaseImpact" ? "impact" : view === "releaseDecision" ? "decision" : "readiness"}
        selectedScrId={view === "releaseImpact" ? selectedArtifactId || undefined : undefined}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr","system",id)}
        onOpenImpact={(id) => navigate("releaseImpact","system",id)}
        onOpenDecision={() => navigate("releaseDecision")}
        onBackToReadiness={() => navigate("release")}
        onOpenVerification={() => navigate("testingCoverage","systemTest")}
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
        releases={release ? [release] : []}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id) => navigate("scr",discipline,id)}
        onOpenVerification={() => navigate("testingCoverage",discipline === "software" ? "softwareTest" : "systemTest")}
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
        onSelectRelease={exitBuild}
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
        releaseId={release?.id ?? ""}
        user={user}
        onBack={() => navigate("dashboard")}
        onOpenScr={(id, resolved) => navigate("scr",resolved,id)}
        onOpenRelease={() => navigate("release")}
        onOpenVerification={(resolved) => navigate("testingCoverage", resolved === "System" ? "systemTest" : "softwareTest", undefined,
          resolved === "LowLevelSoftware" ? "LowLevel" : resolved === "HighLevelSoftware" ? "HighLevel" : undefined)}
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
  if (view === "reviewWorkflows" && project)
    return inShell(
      <ReviewWorkflowCenter
        api={API}
        projectId={project.project.id}
        onBack={() => navigate("dashboard")}
      />
    );
  if (view === "enterprise" && project)
    return inShell(
      <EnterpriseControlCenter
        api={API}
        projectId={project.project.id}
        // The stable link pointed at Enterprise Control's own path with ?enterpriseView=, which nothing
        // reads. A saved view is a requirements query, so its link is the Requirements route with ?view=,
        // which the router already resolves and the workspace already applies.
        viewLink={(id, area) => `${routePath(context!, "requirements", area)}?view=${encodeURIComponent(id)}`}
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
  const openChanges=(area:"system"|"software",stateIntent?:HistoryStateIntent)=>
    navigate("history",area,undefined,undefined,false,stateIntent,area==="software"?"Software":"System");
  const changeCard=(title:string,area:"system"|"software",summary:ChangeMetrics)=>
    <section className={`dashboardAreaCard ${area}`}>
      <header><div><span>{area==="system"?"SYSTEMS":"SOFTWARE"}</span><h2>{title}</h2></div><i>{area==="system"?"SYS":"SW"}</i></header>
      <button className="dashboardTotal" onClick={()=>openChanges(area)}><strong>{summary.total}</strong><span>Total changes</span><small>Open {area} change requests →</small></button>
      <div className="dashboardStateGrid">
        <button onClick={()=>openChanges(area,"Draft")}><b>{summary.draft}</b><span>Draft</span></button>
        <button onClick={()=>openChanges(area,"InReview")}><b>{summary.inReview}</b><span>In review</span></button>
        <button onClick={()=>openChanges(area,"ApprovedOrSelected")}><b>{summary.approved}</b><span>Approved</span></button>
      </div>
      {summary.deferred>0&&<button className="dashboardDeferred" onClick={()=>openChanges(area,"Deferred")}><b>{summary.deferred}</b><span>Deferred changes remain visible in Build {release?.version}</span><em>Open →</em></button>}
    </section>;
  const verificationRow=(label:string,summary:VerificationMetrics)=>
    <article><div><b>{label}</b><span>{summary.triagedChangeRequests} of {summary.totalChangeRequests} change requests triaged</span></div><strong>{summary.openDecisions}</strong><small>open decision{summary.openDecisions===1?"":"s"}</small><em>{summary.resolvedDecisions} resolved</em></article>;
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
          </div>
        </header>
        <section className={`release buildSummary ${release?.isReleased?"released":"inWork"}`}>
          <div>
            <span className="tag">ACTIVE BUILD</span>
            <h2>
              {project?.project.softwareProduct} {release?.version}
            </h2>
            <p>
              {release?.isReleased
                ? "Released historical workspace · read-only"
                : "Current development workspace"}
            </p>
          </div>
          <div className="buildStateSeal"><b>{release?.isReleased?"✓ Released":"In Work"}</b></div>
          <button onClick={() => navigate("release")}>Lifecycle Decision Room →</button>
        </section>
        <section className="dashboardTriptych" aria-busy={dashboardLoading} aria-label="Build work summary">
          {dashboardLoading?<>{Array.from({length:3},(_,index)=><div className="dashboardSkeleton dashboardAreaCard" key={index}><span className="skeletonLine medium"/><i className="skeletonMetric"/><span className="skeletonLine"/></div>)}</>:<>
            {changeCard("System change control","system",metrics.system)}
            {changeCard("Software change control","software",metrics.software)}
            <section className="dashboardAreaCard verification">
              <header><div><span>VERIFICATION</span><h2>Change triage</h2></div><i>V&amp;V</i></header>
              <p className="verificationIntro">Engineering impact decisions for change requests in Build {release?.version}. Procedure redesign remains in the Verification workspace.</p>
              <div className="verificationTriageRows">
                {verificationRow("System",metrics.verification.system)}
                {verificationRow("Software HLR",metrics.verification.hlr)}
                {verificationRow("Software LLR",metrics.verification.llr)}
              </div>
              <button className="verificationOpen" onClick={()=>navigate("verification","systemTest")}>Open Verification →</button>
            </section>
          </>}
        </section>
      </main></div></div>
      {overlays}
    </div>
  );
}
export default App;
