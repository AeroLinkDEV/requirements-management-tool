import { useState } from "react";
import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import type { AuthorizedProject } from "./workspaceContext";
import type { ProjectSetupDraftSummary } from "./ProjectSetupWalkthrough";
import { setupDraftDisplayName, type DiscardSetupOutcome } from "./projectSetupDrafts";
import { projectAreaPath } from "./routing";
import "./ProjectsLanding.css";

export type ProjectIconName =
  | "project" | "fms" | "import" | "satellite" | "navigation" | "certification" | "coverage"
  | "integrity" | "route" | "sensors" | "map" | "display";

/**
 * Project marks. A real project gets a distinctive mark only when it is the showcase that mark was drawn for
 * (FMS, the import practice); every other authorized project keeps the neutral mark, so FMS imagery is never
 * put on somebody else's work.
 */
export function ProjectIcon({ name }: { name: ProjectIconName }) {
  const paths: Record<ProjectIconName, React.ReactNode> = {
    project: <><rect x="4" y="5" width="26" height="24" rx="3" /><path d="M4 12h26M10 5v7m14-7v7M10 18h14M10 23h9" /></>,
    fms: <><rect x="5" y="4" width="22" height="26" rx="3"/><rect x="9" y="8" width="14" height="10" rx="1"/><path d="M9 23h2m3 0h2m3 0h2M9 27h2m3 0h2m3 0h2"/></>,
    import: <><path d="M17 4v16m0 0-6-6m6 6 6-6"/><path d="M5 23v4a3 3 0 0 0 3 3h18a3 3 0 0 0 3-3v-4"/></>,
    satellite: <><path d="m13 13 6 6m-8-4 6-6 6 6-6 6zM8 7l5 5-4 4-5-5zm16 16 5 5-5 2-4-4z"/><path d="M20 11c4-3 8-2 10 0M22 8c5-4 9-3 11-1"/></>,
    navigation: <><circle cx="17" cy="17" r="13"/><path d="m21 10-3 9-9 3 3-9zM17 1v4m0 24v4M1 17h4m24 0h4"/></>,
    certification: <><path d="M7 3h15l6 6v20H7zM22 3v7h6M11 15h12m-12 5h8"/><circle cx="23" cy="24" r="5"/><path d="m20 29-1 4 4-2 3 2 1-5"/></>,
    coverage: <><path d="M17 25V14m-4 11h8M9 31h16"/><circle cx="17" cy="10" r="2"/><path d="M10 17a10 10 0 0 1 14 0M6 13a15 15 0 0 1 22 0M3 9a20 20 0 0 1 28 0"/></>,
    integrity: <><path d="M17 3 29 8v8c0 8-5 13-12 16C10 29 5 24 5 16V8z"/><path d="m11 17 4 4 8-9"/></>,
    route: <><circle cx="6" cy="27" r="3"/><circle cx="14" cy="12" r="3"/><path d="M8 25c4-2 2-7 5-10m4-2c5 1 6 7 10 6"/><path d="m24 7 7 3-6 3 1-3z"/></>,
    sensors: <><circle cx="17" cy="17" r="4"/><circle cx="17" cy="17" r="9"/><path d="M17 3v3m0 22v3M3 17h3m22 0h3M7 7l3 3m14 14 3 3m0-20-3 3M10 24l-3 3"/><path d="m25 25 5 5m0-5-5 5"/></>,
    map: <><path d="m4 7 8-3 10 3 8-3v24l-8 3-10-3-8 3zM12 4v24M22 7v24"/><path d="M17 14c0-3 5-3 5 0 0 2-2.5 5-2.5 5S17 16 17 14z"/></>,
    display: <><rect x="3" y="5" width="28" height="23" rx="3"/><path d="M8 22V11h18M10 19l4-4 4 2 5-6M12 32h10m-5-4v4"/></>,
  };
  return <svg viewBox="0 0 34 34" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg>;
}

/** The two showcase programs whose marks were drawn for them. Keyed by governed program code, never by name. */
const showcaseMarks: Record<string, ProjectIconName> = { FMSLIVE: "fms", IMPORTLAB: "import" };

/** The mark for a governed program: its drawn showcase mark, or the neutral project mark. */
export const projectMark = (programCode: string): ProjectIconName => showcaseMarks[programCode] ?? "project";

/**
 * The mock catalogue the owner asked to have back beside the real projects (#1047), as it looked before #1038.
 * These are pictures of what a portfolio could hold, not records: never links, never counted as authorized
 * projects, never opening a build, and each carries a Mock badge so none can be taken for real work.
 */
const mockProjects: readonly { id: string; name: string; description: string; icon: ProjectIconName }[] = [
  { id: "gps-receiver-modernization", name: "GPS Receiver Modernization", description: "Upgrade planning and architecture study.", icon: "satellite" },
  { id: "integrated-navigation-suite", name: "Integrated Navigation Suite", description: "Concept phase for integrated navigation subsystem.", icon: "navigation" },
  { id: "fms-certification-block-2", name: "FMS Certification Block 2", description: "Certification planning and requirements package.", icon: "certification" },
  { id: "waas-sbas-upgrade", name: "WAAS / SBAS Upgrade", description: "Upgrade planning for performance and coverage.", icon: "coverage" },
  { id: "gnss-integrity-monitor", name: "GNSS Integrity Monitor", description: "Integrity monitoring and fault-detection concept.", icon: "integrity" },
  { id: "flight-planning-core", name: "Flight Planning Core", description: "Core algorithms and route-optimization planning.", icon: "route" },
  { id: "multi-sensor-position-engine", name: "Multi-Sensor Position Engine", description: "Fusion algorithms and sensor-integration concept.", icon: "sensors" },
  { id: "avionics-map-database", name: "Avionics Map Database", description: "Database architecture and update strategy.", icon: "map" },
  { id: "fms-hmi-refresh", name: "FMS HMI Refresh", description: "User-interface modernization and usability study.", icon: "display" },
];

function MockProjectCard({ mock }: { mock: (typeof mockProjects)[number] }) {
  return (
    <article className="projectCard mockProjectCard" data-mock-project={mock.id} aria-disabled="true">
      <div className="projectCardTop">
        <span className="projectIcon"><ProjectIcon name={mock.icon} /></span>
        <span className="projectBadge mock">Mock</span>
      </div>
      <h2>{mock.name}</h2>
      <p>{mock.description}</p>
      <footer>
        <small><span aria-hidden="true">□</span>Mock project</small>
      </footer>
    </article>
  );
}

function ProjectCard({ project, onOpen }: { project: AuthorizedProject; onOpen: () => void }) {
  const releasedCount = project.releases.filter(release => release.isReleased).length;
  const state = project.releases.length === 0
    ? "No builds"
    : `${releasedCount} released · ${project.releases.length - releasedCount} in work`;
  return (
    <a
      className="projectCard activeProjectCard"
      data-project-card
      data-project-id={project.id}
      href={projectAreaPath(project.id, "builds")}
      onClick={event => { event.preventDefault(); onOpen(); }}
      aria-label={`Open ${project.name}`}
    >
      <div className="projectCardTop">
        <span className="projectIcon"><ProjectIcon name={projectMark(project.programCode)} /></span>
      </div>
      <h2>{project.name}</h2>
      <p>{project.softwareProduct}</p>
      <footer>
        <strong>Open project <span aria-hidden="true">→</span></strong>
        <small><span aria-hidden="true">◇</span>{state}</small>
      </footer>
    </a>
  );
}

export default function ProjectsLanding({
  user,
  projects,
  workspaceStatus = "ready",
  drafts,
  draftStatus,
  onRetryProjects,
  onRetryDrafts,
  onCreateProject,
  onResumeSetup,
  onDiscardSetup,
  onOpenProject,
  onSignOut,
}: {
  user: AuthUser;
  projects: AuthorizedProject[];
  workspaceStatus?: "loading" | "ready" | "error";
  drafts: ProjectSetupDraftSummary[];
  draftStatus: "loading" | "ready" | "error";
  onRetryProjects: () => void;
  onRetryDrafts: () => void;
  onCreateProject: () => void;
  onResumeSetup: (draft: ProjectSetupDraftSummary) => void;
  onDiscardSetup: (draft: ProjectSetupDraftSummary) => Promise<DiscardSetupOutcome>;
  onOpenProject: (project: AuthorizedProject) => void;
  onSignOut: () => void;
}) {
  const showSetupDrafts = user.isAdministrator || drafts.length > 0 || draftStatus !== "ready";
  const setupDraftsSection = showSetupDrafts && (
    <SetupDrafts
      drafts={drafts}
      draftStatus={draftStatus}
      onRetryDrafts={onRetryDrafts}
      canCreate={user.isAdministrator}
      onCreateProject={onCreateProject}
      onResumeSetup={onResumeSetup}
      onDiscardSetup={onDiscardSetup}
    />
  );
  return (
    <div className="projectsPage">
      <PortalHeader user={user} onSignOut={onSignOut} />
      <main className="projectsMain">
        <header>
          <div>
            <p className="eyebrow">AUTHORIZED PROJECTS</p>
            <h1>Projects</h1>
            <p>{workspaceStatus === "loading"
              ? "Loading authorized projects…"
              : workspaceStatus === "error"
                ? "Project access could not be loaded."
                : projects.length
                  ? "Select a project to continue."
                  : "You do not have access to any projects yet."}</p>
          </div>
        </header>
        {workspaceStatus === "loading" ? (
          <section className="projectsSections" aria-label="Authorized projects loading">
            <div className="projectsEmptyState" role="status">
              <p>Loading authorized projects…</p>
            </div>
            {setupDraftsSection}
          </section>
        ) : workspaceStatus === "error" ? (
          <section className="projectsSections" aria-label="Authorized projects unavailable">
            <div className="projectsEmptyState" role="alert">
              <span className="projectsEmptyIcon"><ProjectIcon name="project" /></span>
              <h2>Projects unavailable</h2>
              <p>Authorized projects could not be loaded. Retry when workspace access is available.</p>
              <button type="button" onClick={onRetryProjects}>Retry project discovery</button>
            </div>
            {setupDraftsSection}
          </section>
        ) : projects.length ? (
          <section className="projectsSections" aria-label="Authorized projects">
            <div className="projectsGrid" data-project-list>
              {projects.map(project => <ProjectCard key={project.id} project={project} onOpen={() => onOpenProject(project)} />)}
              {mockProjects.map(mock => <MockProjectCard key={mock.id} mock={mock} />)}
            </div>
            {setupDraftsSection}
          </section>
        ) : (
          <section className="projectsSections" aria-label="No authorized projects">
            <div className="projectsEmptyState">
            <span className="projectsEmptyIcon"><ProjectIcon name="project" /></span>
            <h2>No authorized projects</h2>
            <p>Projects become available here when an AeroLink administrator grants your account access.</p>
            </div>
            {setupDraftsSection}
          </section>
        )}
      </main>
    </div>
  );
}

function SetupDrafts({ drafts, draftStatus, onRetryDrafts, canCreate, onCreateProject, onResumeSetup, onDiscardSetup }: {
  drafts: ProjectSetupDraftSummary[];
  draftStatus: "loading" | "ready" | "error";
  onRetryDrafts: () => void;
  canCreate: boolean;
  onCreateProject: () => void;
  onResumeSetup: (draft: ProjectSetupDraftSummary) => void;
  onDiscardSetup: (draft: ProjectSetupDraftSummary) => Promise<DiscardSetupOutcome>;
}) {
  // Discarding is confirmed against the named setup and keyed by stable draft identity, so a refresh that
  // reorders the list cannot move one confirmation onto another setup. Cancel writes nothing.
  const [confirmingId, setConfirmingId] = useState("");
  const [discardingId, setDiscardingId] = useState("");
  const [discardError, setDiscardError] = useState("");
  const [discardedNotice, setDiscardedNotice] = useState("");
  const discard = async (draft: ProjectSetupDraftSummary) => {
    const name = setupDraftDisplayName(draft);
    setDiscardingId(draft.draftId);
    setDiscardError("");
    const outcome = await onDiscardSetup(draft);
    if (outcome.ok) {
      setConfirmingId("");
      // The row leaves the list, so the outcome is stated here rather than only implied by the absence.
      setDiscardedNotice(
        `Discarded the unfinished setup “${name}”. No Project, build or controlled record was deleted.`,
      );
    } else {
      setDiscardError(outcome.message);
    }
    setDiscardingId("");
  };
  return <section className="setupDraftsSection" aria-labelledby="setup-drafts-heading">
    <div className="setupDraftsHeading"><div><h2 id="setup-drafts-heading">Setup drafts</h2><p>Saved project creation work remains recoverable until it is finalized.</p></div>{canCreate && <button type="button" onClick={onCreateProject}>Create New Project</button>}</div>
    {discardedNotice && <p className="setupDraftsNotice" role="status">{discardedNotice}</p>}
    {draftStatus === "error" && <p className="setupDraftsError" role="alert">Saved setup drafts could not be loaded. Existing draft rows are retained. <button type="button" onClick={onRetryDrafts}>Retry draft discovery</button></p>}
    {drafts.length ? <div className="setupDraftsList">{drafts.map(draft => {
      const name = setupDraftDisplayName(draft);
      const confirming = confirmingId === draft.draftId;
      const discarding = discardingId === draft.draftId;
      return <article key={draft.draftId} className="setupDraftCard" data-setup-draft-id={draft.draftId}>
        <div><strong>{name}</strong><span>{draft.project.softwareProduct || "Software product not provided"}</span></div>
        <small>{draft.state === "Finalizing" ? "Finalizing — resume to recover the result" : `Step ${draft.currentStep} · ${draft.lastSavedAt ? `Saved ${new Date(draft.lastSavedAt).toLocaleString()}` : "Not saved yet"}`}</small>
        {confirming ? <div className="setupDraftDiscard" role="group" aria-label={`Confirm discarding ${name}`}>
          <p>Discard the unfinished setup <strong>{name}</strong>? It stops being offered for resume and can no longer be saved or finalized. This is not Project deletion: no Project, build or controlled record is removed, and it cannot be undone from here.</p>
          {discardError && <p className="setupDraftDiscardError" role="alert">{discardError}</p>}
          <div className="setupDraftDiscardActions">
            <button type="button" className="quiet" disabled={discarding} onClick={() => { setConfirmingId(""); setDiscardError(""); }}>Cancel</button>
            <button type="button" disabled={discarding} onClick={() => void discard(draft)}>{discarding ? "Discarding…" : "Discard setup"}</button>
          </div>
        </div> : <div className="setupDraftCardActions">
          <button type="button" onClick={() => onResumeSetup(draft)}>Resume setup</button>
          {/* A setup that is being finalized is not an offerable discard target, so the control is not shown
              for it rather than being offered and then refused. */}
          {draft.state === "Draft" && <button type="button" className="quiet" onClick={() => { setConfirmingId(draft.draftId); setDiscardError(""); setDiscardedNotice(""); }}>Discard setup</button>}
        </div>}
      </article>;
    })}</div> : draftStatus === "loading" ? <p className="setupDraftsEmpty" role="status">Loading saved setup drafts…</p> : draftStatus === "ready" && canCreate ? <p className="setupDraftsEmpty">No saved setup drafts. Start a Project when you are ready.</p> : null}
  </section>;
}
