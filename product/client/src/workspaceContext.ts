import { projectSlugOf } from "./routing";
import type { AppRoute } from "./routing";

export type WorkspaceRelease = { id: string; version: string; isReleased: boolean; predecessorReleaseId?: string | null };
export type Workspace = {
  program: { id: string; name: string; code: string };
  projects: {
    project: { id: string; name: string; softwareProduct: string };
    releases: WorkspaceRelease[];
  }[];
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isIdentifier(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function invalidWorkspace(): never {
  throw new Error("Workspace response is invalid.");
}

/** Validate the server projection before its IDs can become navigation or mutation context. */
export function decodeWorkspaces(value: unknown): Workspace[] {
  if (!Array.isArray(value)) return invalidWorkspace();
  const entries: unknown[] = value;
  return entries.map(entry => {
    if (!isRecord(entry) || !isRecord(entry.program) || !Array.isArray(entry.projects)) return invalidWorkspace();
    const { program } = entry;
    if (!isIdentifier(program.id) || typeof program.name !== "string" || typeof program.code !== "string") return invalidWorkspace();
    const projects: unknown[] = entry.projects;
    return {
      program: { id: program.id, name: program.name, code: program.code },
      projects: projects.map(item => {
        if (!isRecord(item) || !isRecord(item.project) || !Array.isArray(item.releases)) return invalidWorkspace();
        const { project } = item;
        if (!isIdentifier(project.id) || typeof project.name !== "string" || typeof project.softwareProduct !== "string") return invalidWorkspace();
        const releases: unknown[] = item.releases;
        return {
          project: { id: project.id, name: project.name, softwareProduct: project.softwareProduct },
          releases: releases.map(release => {
            if (!isRecord(release) || !isIdentifier(release.id) || typeof release.version !== "string"
              || typeof release.isReleased !== "boolean"
              || (release.predecessorReleaseId != null && !isIdentifier(release.predecessorReleaseId))) return invalidWorkspace();
            return { id: release.id, version: release.version, isReleased: release.isReleased,
              predecessorReleaseId: release.predecessorReleaseId };
          }),
        };
      }),
    };
  });
}

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
