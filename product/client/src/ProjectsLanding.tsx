import { useState } from "react";
import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import type { AuthorizedProject } from "./workspaceContext";
import type { ProjectSetupDraftSummary } from "./ProjectSetupWalkthrough";
import { setupDraftDisplayName, type DiscardSetupOutcome } from "./projectSetupDrafts";
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
