import { projectSlugOf } from "./routing";
import type { AppRoute } from "./routing";

export type WorkspaceRelease = { id: string; version: string; isReleased: boolean };
export type Workspace = {
  program: { id: string; name: string; code: string };
  projects: {
    project: { id: string; name: string; softwareProduct: string };
    releases: WorkspaceRelease[];
  }[];
};

/** Explicit destinations never borrow another project's context or a different build. */
export function resolveWorkspaceContext(workspaces: Workspace[], route: AppRoute) {
  const active = route.programId
    ? workspaces.find(item => item.program.id === route.programId)
    : route.projectSlug
      ? workspaces.find(item => item.projects.some(entry => projectSlugOf(entry.project.name) === route.projectSlug))
      : workspaces.find(item => item.projects.some(entry => entry.releases.length)) ?? workspaces[0];
  const project = route.projectId
    ? active?.projects.find(item => item.project.id === route.projectId)
    : route.projectSlug
      ? active?.projects.find(item => projectSlugOf(item.project.name) === route.projectSlug)
      : active?.projects[0];
  // Legacy Documentation Center URLs carry a build, but this surface is project-wide.
  const requiresBuild = route.view !== "managedDocuments";
  const release = route.releaseId && requiresBuild
    ? project?.releases.find(item => item.id === route.releaseId)
    : [...(project?.releases ?? [])].reverse().find(item => !item.isReleased) ?? project?.releases.at(-1);
  const unavailable = !!((route.programId && !active) || ((route.projectId || route.projectSlug) && !project)
    || (requiresBuild && route.releaseId && !release));
  return unavailable ? { active: undefined, project: undefined, release: undefined, unavailable } : { active, project, release, unavailable };
}
