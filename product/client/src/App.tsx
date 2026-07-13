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
type View =
  | "dashboard" | "createSystemScr" | "createSoftwareChange" | "scr" | "baselines" | "history" | "requirements"
  | "verification" | "lifecycle" | "release" | "planning" | "mywork" | "admin" | "enterprise";
type Discipline = "system" | "software" | "systemTest" | "softwareTest";
const API = "http://127.0.0.1:5080";

function AppNavigation({ user, workspaces, activeId, selectedReleaseId, view, discipline, onProgram, onRelease, onNavigate, onDiscipline, onSignOut }:{
  user:AuthUser;workspaces:Workspace[];activeId:string;selectedReleaseId:string;view:View;discipline:Discipline;
  onProgram:(id:string)=>void;onRelease:(id:string)=>void;onNavigate:(view:View)=>void;onDiscipline:(value:Discipline)=>void;onSignOut:()=>void;
}) {
  const active=workspaces.find(x=>x.program.id===activeId)??workspaces[0],project=active?.projects[0],release=project?.releases.find(x=>x.id===selectedReleaseId)??project?.releases.at(-1);
  const go=(target:View,area?:Discipline)=>{if(area)onDiscipline(area);onNavigate(target)};
  const item=(label:string,target:View,icon:string,area?:Discipline)=> <button className={view===target&&(!area||discipline===area)?"active":""} onClick={()=>go(target,area)}><i>{icon}</i>{label}</button>;
  return <aside className="appNavigation"><div className="brand"><span>▲</span><b>AeroLink</b></div><div className="program"><small>ACTIVE PROGRAM</small><select value={activeId} onChange={event=>onProgram(event.target.value)}>{workspaces.map(x=><option value={x.program.id} key={x.program.id}>{x.program.name}</option>)}</select><span>{project?.project.name}</span><select className="releaseSelector" value={release?.id??""} onChange={event=>onRelease(event.target.value)} aria-label="Active release">{project?.releases.map(item=><option value={item.id} key={item.id}>{item.version} · {item.isReleased?"Released":"In work"}</option>)}</select></div>
    <nav className="primaryNavigation"><div className="navGroup"><small>PERSONAL</small>{item("Command Center","dashboard","⌂")}{item("My Work","mywork","◎")}</div>
      <div className="navGroup"><small>SYSTEMS</small>{item("New System SCR","createSystemScr","+")}{item("System Change Requests","history","◇","system")}{item("System Requirements","requirements","≡","system")}{item("System Verification","verification","✓","systemTest")}{item("SYSRD & Traceability","lifecycle","↗","system")}</div>
      <div className="navGroup"><small>SOFTWARE</small>{item("New Software SWCR","createSoftwareChange","+")}{item("Software Change Requests","history","◇","software")}{item("HLR & LLR Requirements","requirements","≡","software")}{item("Software Verification","verification","✓","softwareTest")}{item("SWRD & Traceability","lifecycle","↗","software")}</div>
      <div className="navGroup"><small>RELEASE & ASSURANCE</small>{item("Release Planning","planning","⑂")}{item("Baselines","baselines","⌘")}{item("Release Campaign","release","◆")}{item("Enterprise Control","enterprise","◈")}</div>
      {user.isAdministrator&&<div className="navGroup"><small>ADMINISTRATION</small>{item("People & Authority","admin","⚙")}</div>}
    </nav><footer><div className="avatar">{user.displayName.split(" ").map(x=>x[0]).join("").slice(0,2)}</div><div><b>{user.displayName}</b><small>{user.userName}</small></div><button className="signOut" onClick={onSignOut}>Sign out</button></footer></aside>;
}

function App() {
  const [user, setUser] = useState<AuthUser | null | undefined>(undefined);
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
    [activeId, setActiveId] = useState(""),
    [selectedReleaseId, setSelectedReleaseId] = useState(""),
    [connected, setConnected] = useState(false),
    [error, setError] = useState(""),
    [saving, setSaving] = useState(false),
    [selectedScrId, setSelectedScrId] = useState(""),
    [discipline,setDiscipline]=useState<Discipline>("system"),
    [view, setView] = useState<View>(() =>
      new URLSearchParams(location.search).has("enterpriseView")
        ? "requirements"
        : "dashboard",
    );
  useEffect(() => {
    fetch(`${API}/api/auth/me`)
      .then(async (r) => setUser(r.ok ? await r.json() : null))
      .catch(() => setUser(null));
  }, []);
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
    release =
      project?.releases.find((x) => x.id === selectedReleaseId) ??
      [...(project?.releases ?? [])].reverse().find((x) => !x.isReleased) ??
      project?.releases.at(-1);
  useEffect(() => {
    if (release && release.id !== selectedReleaseId)
      setSelectedReleaseId(release.id);
  }, [release, selectedReleaseId]);
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
  const navigate=(target:View)=>setView(target);
  const navigation=<AppNavigation user={user} workspaces={workspaces} activeId={activeId} selectedReleaseId={release?.id??selectedReleaseId} view={view} discipline={discipline} onProgram={setActiveId} onRelease={setSelectedReleaseId} onNavigate={navigate} onDiscipline={setDiscipline} onSignOut={async()=>{await fetch(`${API}/api/auth/logout`,{method:"POST"});setUser(null)}}/>;
  const inShell=(content:React.ReactNode)=><div className="shell">{navigation}<div className="workspaceStage">{content}</div></div>;
  if ((view === "createSystemScr" || view === "createSoftwareChange") && project && release)
    return inShell(
      <ScrEditor
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        releaseVersion={release.version}
        scope={view === "createSystemScr" ? "System" : "Software"}
        user={user}
        onCancel={() => setView("dashboard")}
        onSaved={async (scrId) => {
          await loadData();
          setSelectedScrId(scrId);
          setView("scr");
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
          onBack={() => setView("dashboard")}
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
        onBack={() => setView("dashboard")}
      />
    );
  if (view === "history" && project)
    return inShell(
      <HistoryExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        scope={discipline === "software" ? "Software" : "System"}
        onBack={() => setView("dashboard")}
        onOpenScr={(id) => {
          setSelectedScrId(id);
          setView("scr");
        }}
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
        initialViewId={
          new URLSearchParams(location.search).get("enterpriseView") ||
          undefined
        }
        onBack={() => setView("dashboard")}
        onOpenScr={(id) => {
          setSelectedScrId(id);
          setView("scr");
        }}
      />
    );
  if (view === "verification" && project && release)
    return inShell(
      <VerificationCenter
        api={API}
        projectId={project.project.id}
        releaseId={release.id}
        scope={discipline === "softwareTest" ? "Software" : "System"}
        onBack={() => setView("dashboard")}
      />
    );
  if (view === "lifecycle" && project)
    return inShell(
      <LifecycleExplorer
        api={API}
        projectId={project.project.id}
        releases={project.releases}
        onBack={() => setView("dashboard")}
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
        onBack={() => setView("dashboard")}
        onOpenScr={(id) => {
          setSelectedScrId(id);
          setView("scr");
        }}
        onOpenVerification={() => setView("verification")}
        onOpenDocuments={() => setView("lifecycle")}
      />
    );
  if (view === "planning" && project && release)
    return inShell(
      <ReleasePlanningCenter
        api={API}
        projectId={project.project.id}
        productName={project.project.softwareProduct}
        activeReleaseId={release.id}
        onBack={() => setView("dashboard")}
        onSelectRelease={setSelectedReleaseId}
        onOpenBaselines={() => setView("baselines")}
        onOpenCampaign={() => setView("release")}
        onChanged={loadWorkspaces}
      />
    );
  if (view === "mywork" && project)
    return inShell(
      <MyWorkCenter
        api={API}
        projectId={project.project.id}
        user={user}
        onBack={() => setView("dashboard")}
        onOpenScr={(id) => {
          setSelectedScrId(id);
          setView("scr");
        }}
        onOpenRelease={() => setView("release")}
      />
    );
  if (view === "admin" && active)
    return inShell(
      <AdministrationCenter
        api={API}
        programId={active.program.id}
        onBack={() => setView("dashboard")}
      />
    );
  if (view === "enterprise" && project)
    return inShell(
      <EnterpriseControlCenter
        api={API}
        projectId={project.project.id}
        onBack={() => setView("dashboard")}
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
              setView(release?.isReleased ? "planning" : "release")
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
                <button onClick={() => setView("history")}>
                  Search released changes
                </button>
              ) : (
                <button onClick={() => setView("createSystemScr")}>+ New System SCR</button>
              )}
            </div>
            {scrs.length ? (
              scrs.map((scr) => (
                <div
                  className="row"
                  key={scr.id}
                  role="button"
                  tabIndex={0}
                  onClick={() => {
                    setSelectedScrId(scr.id);
                    setView("scr");
                  }}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      setSelectedScrId(scr.id);
                      setView("scr");
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
                <button onClick={() => setView("createSystemScr")}>
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
