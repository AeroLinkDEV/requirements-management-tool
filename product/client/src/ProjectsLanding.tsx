import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import type { AuthorizedProject } from "./workspaceContext";
import type { ProjectSetupDraftSummary } from "./ProjectSetupWalkthrough";
import { projectAreaPath } from "./routing";
import "./ProjectsLanding.css";

export type ProjectIconName = "project";

/** A neutral project mark; the portal never assigns FMS imagery to another authorized project. */
export function ProjectIcon({ name: _name }: { name: ProjectIconName }) {
  return <svg viewBox="0 0 34 34" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
    <rect x="4" y="5" width="26" height="24" rx="3" />
    <path d="M4 12h26M10 5v7m14-7v7M10 18h14M10 23h9" />
  </svg>;
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
        <span className="projectIcon"><ProjectIcon name="project" /></span>
        <span className="projectBadge active">Authorized</span>
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
  drafts,
  onCreateProject,
  onResumeSetup,
  onOpenProject,
  onSignOut,
}: {
  user: AuthUser;
  projects: AuthorizedProject[];
  drafts: ProjectSetupDraftSummary[];
  onCreateProject: () => void;
  onResumeSetup: (draft: ProjectSetupDraftSummary) => void;
  onOpenProject: (project: AuthorizedProject) => void;
  onSignOut: () => void;
}) {
  return (
    <div className="projectsPage">
      <PortalHeader user={user} onSignOut={onSignOut} />
      <main className="projectsMain">
        <header>
          <div>
            <p className="eyebrow">AUTHORIZED PROJECTS</p>
            <h1>Projects</h1>
            <p>{projects.length ? "Select a project to continue." : "You do not have access to any projects yet."}</p>
          </div>
        </header>
        {projects.length ? (
          <section className="projectsSections" aria-label="Authorized projects">
            <div className="projectsGrid" data-project-list>
              {projects.map(project => <ProjectCard key={project.id} project={project} onOpen={() => onOpenProject(project)} />)}
            </div>
            {(user.isAdministrator || drafts.length > 0) && <SetupDrafts drafts={drafts} canCreate={user.isAdministrator} onCreateProject={onCreateProject} onResumeSetup={onResumeSetup} />}
          </section>
        ) : (
          <section className="projectsSections" aria-label="No authorized projects">
            <div className="projectsEmptyState">
            <span className="projectsEmptyIcon"><ProjectIcon name="project" /></span>
            <h2>No authorized projects</h2>
            <p>Projects become available here when an AeroLink administrator grants your account access.</p>
            </div>
            {(user.isAdministrator || drafts.length > 0) && <SetupDrafts drafts={drafts} canCreate={user.isAdministrator} onCreateProject={onCreateProject} onResumeSetup={onResumeSetup} />}
          </section>
        )}
      </main>
    </div>
  );
}

function SetupDrafts({ drafts, canCreate, onCreateProject, onResumeSetup }: {
  drafts: ProjectSetupDraftSummary[];
  canCreate: boolean;
  onCreateProject: () => void;
  onResumeSetup: (draft: ProjectSetupDraftSummary) => void;
}) {
  return <section className="setupDraftsSection" aria-labelledby="setup-drafts-heading">
    <div className="setupDraftsHeading"><div><h2 id="setup-drafts-heading">Setup drafts</h2><p>Saved project creation work remains recoverable until it is finalized.</p></div>{canCreate && <button type="button" onClick={onCreateProject}>Create New Project</button>}</div>
    {drafts.length ? <div className="setupDraftsList">{drafts.map(draft => <article key={draft.draftId} className="setupDraftCard" data-setup-draft-id={draft.draftId}><div><strong>{draft.project.name || "Untitled Project"}</strong><span>{draft.project.softwareProduct || "Software product not provided"}</span></div><small>{draft.state === "Finalizing" ? "Finalizing — resume to recover the result" : `Step ${draft.currentStep} · ${draft.lastSavedAt ? `Saved ${new Date(draft.lastSavedAt).toLocaleString()}` : "Not saved yet"}`}</small><button type="button" onClick={() => onResumeSetup(draft)}>Resume setup</button></article>)}</div> : canCreate ? <p className="setupDraftsEmpty">No saved setup drafts. Start a Project when you are ready.</p> : null}
  </section>;
}
