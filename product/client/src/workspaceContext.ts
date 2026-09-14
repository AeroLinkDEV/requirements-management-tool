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

export type AuthorizedProject = Workspace["projects"][number]["project"] & {
  programId: string;
  programName: string;
  programCode: string;
  releases: WorkspaceRelease[];
};

const internalProjectProgramName = /\sbacking scope [0-9a-f]{32}$/i;

/**
 * New Project inception reserves an internal Program identity for server-side ownership. That
 * backing name is an implementation detail; legacy workspaces still present their authored
 * Program name in the established context surfaces.
 */
export function isInternalProjectWorkspace(
  workspace: Workspace,
  project?: Workspace["projects"][number],
) {
  return Boolean(project && internalProjectProgramName.test(workspace.program.name.trim()));
}

export function workspaceDisplayName(
  workspace: Workspace,
  project?: Workspace["projects"][number],
) {
  return isInternalProjectWorkspace(workspace, project)
    ? project?.project.name || "Project"
    : workspace.program.name;
}

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

/** Flatten the server's authorized projection without adding sample or inferred projects. */
export function authorizedProjects(workspaces: Workspace[]): AuthorizedProject[] {
  return workspaces
    .flatMap(workspace => workspace.projects.map(entry => ({
      ...entry.project,
      programId: workspace.program.id,
      programName: workspace.program.name,
      programCode: workspace.program.code,
      releases: entry.releases,
    })))
    .sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id));
}

const projectLevelViews = new Set<AppRoute["view"]>([
  "projects", "projectSetup", "builds", "baselineImports", "personnel", "approvalConfiguration", "projectConfiguration",
]);

function projectMatch(workspaces: Workspace[], requestedId: string, activeProgramId?: string) {
  const candidates = workspaces.flatMap(workspace => workspace.projects
    .filter(() => !activeProgramId || workspace.program.id === activeProgramId)
    .map(entry => ({ workspace, entry })));
  const exact = candidates.filter(candidate => candidate.entry.project.id === requestedId);
  if (exact.length === 1) return exact[0];
  if (exact.length > 1) return undefined;

  // Slug routes predate stable project identity. They remain readable only when the name maps to one
  // authorized project; choosing the first collision would silently switch controlled context.
  const legacy = candidates.filter(candidate => projectSlugOf(candidate.entry.project.name) === requestedId);
  return legacy.length === 1 ? legacy[0] : undefined;
}

/** Explicit destinations never borrow another project's context or a different build. */
export function resolveWorkspaceContext(workspaces: Workspace[], route: AppRoute) {
  const active = route.programId
    ? workspaces.find(item => item.program.id === route.programId)
    : route.projectId
      ? projectMatch(workspaces, route.projectId)?.workspace
      : workspaces.find(item => item.projects.some(entry => entry.releases.length)) ?? workspaces[0];
  const matched = route.projectId
    ? projectMatch(workspaces, route.projectId, active?.program.id)
    : undefined;
  const project = matched?.entry ?? (route.projectId ? undefined : active?.projects[0]);
  // Context-free project pages do not enter a build. A build is required only by the existing
  // build-scoped workspaces, where an explicit release is part of the route.
  const requiresBuild = !projectLevelViews.has(route.view) && route.view !== "managedDocuments";
  const release = route.releaseId && requiresBuild
    ? project?.releases.find(item => item.id === route.releaseId)
    : undefined;
  const unavailable = !!((route.programId && !active) || (route.projectId && !project)
    || (requiresBuild && route.releaseId && !release));
  return unavailable ? { active: undefined, project: undefined, release: undefined, unavailable } : { active, project, release, unavailable };
}
