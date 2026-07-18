export type View =
  | "dashboard" | "createSystemScr" | "createSoftwareChange" | "scr" | "baselines" | "history" | "requirements"
  | "verification" | "lifecycle" | "release" | "releaseImpact" | "releaseDecision" | "releaseOperations" | "planning" | "mywork" | "admin" | "enterprise" | "integrations" | "artifact" | "notFound";

export type Discipline = "system" | "software" | "systemTest" | "softwareTest";

export type HistoryStateIntent =
  | "Draft"
  | "InReview"
  | "Approved"
  | "SelectedForBaseline"
  | "ApprovedOrSelected";

export type HistoryTypeIntent = "System" | "Software" | "All";

const historyStateIntents: readonly HistoryStateIntent[] = [
  "Draft",
  "InReview",
  "Approved",
  "SelectedForBaseline",
  "ApprovedOrSelected",
];

const historyStateIntent = (value: string | null): HistoryStateIntent | undefined =>
  historyStateIntents.includes(value as HistoryStateIntent)
    ? (value as HistoryStateIntent)
    : undefined;

export type AppRoute = {
  view: View;
  discipline: Discipline;
  programId?: string;
  projectId?: string;
  releaseId?: string;
  artifactId?: string;
  artifactKind?: string;
  savedViewId?: string;
  historyStateIntent?: HistoryStateIntent;
  historyTypeIntent?: HistoryTypeIntent;
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
  if (path === "systems/change-requests") return { ...base, view: "history", discipline: "system", historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "System" };
  if (path === "software/change-requests") return { ...base, view: "history", discipline: "software", historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "Software" };
  if (path === "systems/change-requests/new") return { ...base, view: "createSystemScr", discipline: "system", artifactId: query.get("requirement") || undefined };
  if (path === "software/change-requests/new") return { ...base, view: "createSoftwareChange", discipline: "software", artifactId: query.get("requirement") || undefined };
  if (tail[0] === "change-requests" && tail[1]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "systems/requirements") return { ...base, view: "requirements", discipline: "system", savedViewId: query.get("view") || undefined };
  if (path === "software/requirements") return { ...base, view: "requirements", discipline: "software", savedViewId: query.get("view") || undefined };
  if (tail[0] === "requirements" && tail[1]) return { ...base, view: "requirements", discipline: query.get("discipline") === "software" ? "software" : "system", artifactId: decoded(tail[1]) };
  if (path === "system-verification") return { ...base, view: "verification", discipline: "systemTest" };
  if (path === "software-verification") return { ...base, view: "verification", discipline: "softwareTest" };
  if (path === "traceability") return { ...base, view: "lifecycle", discipline: "system" };
  if (path === "release-planning") return { ...base, view: "planning", discipline: "system" };
  if (path === "baselines") return { ...base, view: "baselines", discipline: "system" };
  if (path === "release-readiness" || path === "release-campaign") return { ...base, view: "release", discipline: "system" };
  if (tail[0] === "release-readiness" && tail[1] === "changes" && tail[2]) return { ...base, view: "releaseImpact", discipline: "system", artifactId: decoded(tail[2]) };
  if (path === "release-readiness/evidence") return { ...base, view: "releaseDecision", discipline: "system" };
  if (path === "release-readiness/operations") return { ...base, view: "releaseOperations", discipline: "system" };
  if (path === "enterprise-control") return { ...base, view: "enterprise", discipline: "system" };
  if (path === "integration-command-center") return { ...base, view: "integrations", discipline: "system" };
  if (path === "administration") return { ...base, view: "admin", discipline: "system" };
  if (tail[0] === "artifacts" && tail[1] && tail[2]) return { ...base, view: "artifact", discipline: "system", artifactKind: decoded(tail[1]), artifactId: decoded(tail[2]) };
  return { ...base, view: "notFound", discipline: "system" };
}

export type RouteContext = { programId: string; projectId: string; releaseId: string };

export function routePath(context: RouteContext, view: View, discipline: Discipline = "system", artifactId?: string, artifactKind?: string, stateIntent?: HistoryStateIntent, typeIntent?: HistoryTypeIntent) {
  const root = `/programs/${context.programId}/projects/${context.projectId}/releases/${context.releaseId}`;
  const historyPath = (scope: "systems" | "software") => {
    const path = `${root}/${scope}/change-requests`;
    const query = new URLSearchParams();
    if (stateIntent) query.set("state", stateIntent);
    if (typeIntent === "All") query.set("type", "All");
    return query.size ? `${path}?${query}` : path;
  };
  switch (view) {
    case "dashboard": return `${root}/command-center`;
    case "mywork": return `${root}/my-work`;
    case "createSystemScr": return `${root}/systems/change-requests/new${artifactId ? `?requirement=${encodeURIComponent(artifactId)}` : ""}`;
    case "createSoftwareChange": return `${root}/software/change-requests/new${artifactId ? `?requirement=${encodeURIComponent(artifactId)}` : ""}`;
    case "scr": return `${root}/change-requests/${artifactId}`;
    case "history": return historyPath(discipline === "software" ? "software" : "systems");
    case "requirements": return artifactId ? `${root}/requirements/${artifactId}?discipline=${discipline === "software" ? "software" : "system"}` : `${root}/${discipline === "software" ? "software" : "systems"}/requirements`;
    case "verification": return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification`;
    case "lifecycle": return `${root}/traceability`;
    case "planning": return `${root}/release-planning`;
    case "baselines": return `${root}/baselines`;
    case "release": return `${root}/release-readiness`;
    case "releaseImpact": return `${root}/release-readiness/changes/${artifactId}`;
    case "releaseDecision": return `${root}/release-readiness/evidence`;
    case "releaseOperations": return `${root}/release-readiness/operations`;
    case "enterprise": return `${root}/enterprise-control`;
    case "integrations": return `${root}/integration-command-center`;
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
