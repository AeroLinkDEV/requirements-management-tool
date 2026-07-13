export type View =
  | "dashboard" | "createSystemScr" | "createSoftwareChange" | "scr" | "baselines" | "history" | "requirements"
  | "verification" | "lifecycle" | "release" | "planning" | "mywork" | "admin" | "enterprise" | "artifact" | "notFound";

export type Discipline = "system" | "software" | "systemTest" | "softwareTest";

export type AppRoute = {
  view: View;
  discipline: Discipline;
  programId?: string;
  projectId?: string;
  releaseId?: string;
  artifactId?: string;
  artifactKind?: string;
  savedViewId?: string;
};

const decoded = (value: string | undefined) => value ? decodeURIComponent(value) : undefined;

export function readRoute(): AppRoute {
  const parts = location.pathname.split("/").filter(Boolean);
  const query = new URLSearchParams(location.search);
  if (!parts.length) return { view: "dashboard", discipline: "system" };
  if (parts[0] !== "programs" || parts[2] !== "projects" || parts[4] !== "releases")
    return { view: "notFound", discipline: "system" };

  const base = { programId: decoded(parts[1]), projectId: decoded(parts[3]), releaseId: decoded(parts[5]) };
  const tail = parts.slice(6);
  const path = tail.join("/");
  if (!path || path === "command-center") return { ...base, view: "dashboard", discipline: "system" };
  if (path === "my-work") return { ...base, view: "mywork", discipline: "system" };
  if (path === "systems/change-requests") return { ...base, view: "history", discipline: "system" };
  if (path === "software/change-requests") return { ...base, view: "history", discipline: "software" };
  if (path === "systems/change-requests/new") return { ...base, view: "createSystemScr", discipline: "system" };
  if (path === "software/change-requests/new") return { ...base, view: "createSoftwareChange", discipline: "software" };
  if (tail[0] === "change-requests" && tail[1]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "systems/requirements") return { ...base, view: "requirements", discipline: "system", savedViewId: query.get("view") || undefined };
  if (path === "software/requirements") return { ...base, view: "requirements", discipline: "software", savedViewId: query.get("view") || undefined };
  if (tail[0] === "requirements" && tail[1]) return { ...base, view: "requirements", discipline: query.get("discipline") === "software" ? "software" : "system", artifactId: decoded(tail[1]) };
  if (path === "system-verification") return { ...base, view: "verification", discipline: "systemTest" };
  if (path === "software-verification") return { ...base, view: "verification", discipline: "softwareTest" };
  if (path === "traceability") return { ...base, view: "lifecycle", discipline: "system" };
  if (path === "release-planning") return { ...base, view: "planning", discipline: "system" };
  if (path === "baselines") return { ...base, view: "baselines", discipline: "system" };
  if (path === "release-campaign") return { ...base, view: "release", discipline: "system" };
  if (path === "enterprise-control") return { ...base, view: "enterprise", discipline: "system" };
  if (path === "administration") return { ...base, view: "admin", discipline: "system" };
  if (tail[0] === "artifacts" && tail[1] && tail[2]) return { ...base, view: "artifact", discipline: "system", artifactKind: decoded(tail[1]), artifactId: decoded(tail[2]) };
  return { ...base, view: "notFound", discipline: "system" };
}

export type RouteContext = { programId: string; projectId: string; releaseId: string };

export function routePath(context: RouteContext, view: View, discipline: Discipline = "system", artifactId?: string, artifactKind?: string) {
  const root = `/programs/${context.programId}/projects/${context.projectId}/releases/${context.releaseId}`;
  switch (view) {
    case "dashboard": return `${root}/command-center`;
    case "mywork": return `${root}/my-work`;
    case "createSystemScr": return `${root}/systems/change-requests/new`;
    case "createSoftwareChange": return `${root}/software/change-requests/new`;
    case "scr": return `${root}/change-requests/${artifactId}`;
    case "history": return `${root}/${discipline === "software" ? "software" : "systems"}/change-requests`;
    case "requirements": return artifactId ? `${root}/requirements/${artifactId}?discipline=${discipline === "software" ? "software" : "system"}` : `${root}/${discipline === "software" ? "software" : "systems"}/requirements`;
    case "verification": return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification`;
    case "lifecycle": return `${root}/traceability`;
    case "planning": return `${root}/release-planning`;
    case "baselines": return `${root}/baselines`;
    case "release": return `${root}/release-campaign`;
    case "enterprise": return `${root}/enterprise-control`;
    case "admin": return `${root}/administration`;
    case "artifact": return `${root}/artifacts/${artifactKind}/${artifactId}`;
    default: return root;
  }
}

export function artifactPath(context: RouteContext, kind: string, id: string, discipline = "system") {
  if (kind === "change-request") return routePath(context, "scr", "system", id);
  if (kind === "requirement") return routePath(context, "requirements", discipline === "software" ? "software" : "system", id);
  return routePath(context, "artifact", "system", id, kind);
}
