export type View =
  | "projects" | "builds" | "dashboard" | "createSystemScr" | "createSoftwareChange" | "scr" | "baselines" | "history" | "requirements"
  | "verification" | "testingCoverage" | "testResults" | "documents" | "code" | "problemReports" | "lifecycle" | "release" | "releaseImpact" | "releaseDecision" | "releaseOperations" | "planning" | "mywork" | "admin" | "enterprise" | "integrations" | "reviewWorkflows" | "artifact" | "notFound";

export type Discipline = "system" | "software" | "systemTest" | "softwareTest";

/// Which branch of verification a page belongs to. System has one; software has two, because HLR and LLR
/// test work is planned, done and approved by different people and asked about separately.
const verificationBranch = (discipline: Discipline, artifactKind?: string) =>
  discipline === "softwareTest"
    ? `software-verification/${artifactKind === "LowLevel" ? "llr" : "hlr"}`
    : "system-verification";

export type HistoryStateIntent =
  | "Draft"
  | "InReview"
  | "Approved"
  | "SelectedForBaseline"
  // Work put away for another release. Reachable as its own view because that is the whole point of it —
  // shelved work nobody can find has not been shelved, it has been lost.
  | "Deferred"
  | "ApprovedOrSelected";

export type HistoryTypeIntent = "System" | "Software" | "All";

const historyStateIntents: readonly HistoryStateIntent[] = [
  "Draft",
  "InReview",
  "Approved",
  "SelectedForBaseline",
  "Deferred",
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

export function parseRoute(pathname: string, search = ""): AppRoute {
  const embeddedQuery = pathname.indexOf("?");
  if (embeddedQuery >= 0 && !search) {
    search = pathname.slice(embeddedQuery);
    pathname = pathname.slice(0, embeddedQuery);
  }
  const parts = pathname.split("/").filter(Boolean);
  const query = new URLSearchParams(search);
  if (!parts.length || (parts.length === 1 && parts[0] === "projects"))
    return { view: "projects", discipline: "system" };
  if (parts.length === 3 && parts[0] === "projects" && parts[1] === "fms-product-development" && parts[2] === "builds")
    return { view: "builds", discipline: "system" };
  if (parts[0] !== "programs" || parts[2] !== "projects" || parts[4] !== "releases")
    return { view: "notFound", discipline: "system" };

  const base = { programId: decoded(parts[1]), projectId: decoded(parts[3]), releaseId: decoded(parts[5]) };
  const tail = parts.slice(6);
  const path = tail.join("/");
  if (!path || path === "command-center") return { ...base, view: "dashboard", discipline: "system" };
  if (path === "my-work") return { ...base, view: "mywork", discipline: "system" };
  if (path === "systems/change-requests") return { ...base, view: "history", discipline: "system", historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "System" };
  if (path === "software/change-requests") return { ...base, view: "history", discipline: "software", artifactId: query.get("assessment") || undefined, artifactKind: query.get("level") === "LLR" ? "LowLevel" : "HighLevel", historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "Software" };
  if (path === "systems/change-requests/new") return { ...base, view: "createSystemScr", discipline: "system", artifactId: query.get("requirement") || undefined };
  if (path === "software/change-requests/new") return { ...base, view: "createSoftwareChange", discipline: "software", artifactId: query.get("requirement") || undefined, artifactKind: query.get("level") === "HLR" ? "HighLevel" : query.get("level") === "LLR" ? "LowLevel" : undefined };
  if (tail[0] === "systems" && tail[1] === "change-requests" && tail[2]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[2]) };
  if (tail[0] === "software" && tail[1] === "change-requests" && tail[2]) return { ...base, view: "scr", discipline: "software", artifactId: decoded(tail[2]) };
  // Compatibility for links created before typed change-request routes existed. The detail view replaces this
  // with the canonical typed path after the authorized record reveals its type.
  if (tail[0] === "change-requests" && tail[1]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "systems/requirements") return { ...base, view: "requirements", discipline: "system", savedViewId: query.get("view") || undefined };
  if (path === "software/requirements") return { ...base, view: "requirements", discipline: "software", savedViewId: query.get("view") || undefined };
  if (path === "systems/documents") return { ...base, view: "documents", discipline: "system" };
  if (path === "software/documents") return { ...base, view: "documents", discipline: "software" };
  if (path === "system-verification/documents") return { ...base, view: "documents", discipline: "systemTest" };
  if (path === "software-verification/documents") return { ...base, view: "documents", discipline: "softwareTest" };
  if (tail[0] === "requirements" && tail[1]) return { ...base, view: "requirements", discipline: query.get("discipline") === "software" ? "software" : "system", artifactId: decoded(tail[1]) };
  // The two paths a build's verification work splits into, and their software HLR and LLR pairs. Placed
  // before the rules below that read any second segment as a problem report identifier, which would
  // otherwise take "results" for the name of a corrective action.
  //
  // The software level rides on artifactKind rather than on a new discipline. `discipline` is threaded
  // through the shell — breadcrumbs, navigation highlighting, scope switches — and adding values to it means
  // auditing every comparison for one that silently treats an unrecognised value as System. artifactKind
  // already carries exactly HighLevel and LowLevel for software change requests.
  //
  // A results route may carry the problem report a corrective action came from, so refresh and back return
  // to the same remediation rather than to a generic workspace. It hangs off results rather than off the
  // branch root because recording the successor determination is the whole of what it asks for.
  if (path === "system-verification/coverage") return { ...base, view: "testingCoverage", discipline: "systemTest" };
  if (path === "system-verification/results") return { ...base, view: "testResults", discipline: "systemTest" };
  if (tail[0] === "system-verification" && tail[1] === "results" && tail[2]) return { ...base, view: "testResults", discipline: "systemTest", artifactId: decoded(tail[2]) };
  if (path === "software-verification/hlr/coverage") return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: "HighLevel" };
  if (path === "software-verification/hlr/results") return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "HighLevel" };
  if (tail[0] === "software-verification" && tail[1] === "hlr" && tail[2] === "results" && tail[3]) return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "HighLevel", artifactId: decoded(tail[3]) };
  if (path === "software-verification/llr/coverage") return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: "LowLevel" };
  if (path === "software-verification/llr/results") return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "LowLevel" };
  if (tail[0] === "software-verification" && tail[1] === "llr" && tail[2] === "results" && tail[3]) return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "LowLevel", artifactId: decoded(tail[3]) };
  if (path === "system-verification") return { ...base, view: "verification", discipline: "systemTest" };
  if (path === "software-verification") return { ...base, view: "verification", discipline: "softwareTest" };
  if (path === "code") return { ...base, view: "code", discipline: "software" };
  if (path === "problem-reports") return { ...base, view: "problemReports", discipline: "system" };
  if (tail[0] === "problem-reports" && tail[1]) return { ...base, view: "problemReports", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "traceability") return { ...base, view: "lifecycle", discipline: "system" };
  // The focused artifact is part of the address, not just component state. Without it the route rewrote
  // itself to a bare /traceability, the app re-read that URL, and the requirement the reader arrived from
  // was cleared before the thread could open on it.
  if (tail[0] === "traceability" && tail[1]) return { ...base, view: "lifecycle", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "release-planning" || path === "baselines") return { ...base, view: "notFound", discipline: "system" };
  if (path === "release-readiness" || path === "release-campaign") return { ...base, view: "release", discipline: "system" };
  if (tail[0] === "release-readiness" && tail[1] === "changes" && tail[2]) return { ...base, view: "releaseImpact", discipline: "system", artifactId: decoded(tail[2]) };
  if (path === "release-readiness/evidence") return { ...base, view: "releaseDecision", discipline: "system" };
  if (path === "release-readiness/operations") return { ...base, view: "releaseOperations", discipline: "system" };
  if (path === "enterprise-control") return { ...base, view: "enterprise", discipline: "system" };
  if (path === "integration-command-center") return { ...base, view: "integrations", discipline: "system" };
  if (path === "administration") return { ...base, view: "admin", discipline: "system" };
  if (path === "review-workflows") return { ...base, view: "reviewWorkflows", discipline: "system" };
  if (tail[0] === "artifacts" && tail[1] && tail[2]) {
    const artifactKind = decoded(tail[1]);
    if (artifactKind && ["problem-report", "baseline", "build"].includes(artifactKind))
      return { ...base, view: "notFound", discipline: "system" };
    return { ...base, view: "artifact", discipline: "system", artifactKind, artifactId: decoded(tail[2]) };
  }
  return { ...base, view: "notFound", discipline: "system" };
}

export function readRoute(): AppRoute {
  return parseRoute(location.pathname, location.search);
}

export type RouteContext = { programId: string; projectId: string; releaseId: string };

export function routePath(context: RouteContext, view: View, discipline: Discipline = "system", artifactId?: string, artifactKind?: string, stateIntent?: HistoryStateIntent, typeIntent?: HistoryTypeIntent) {
  const root = `/programs/${context.programId}/projects/${context.projectId}/releases/${context.releaseId}`;
  const historyPath = (scope: "systems" | "software") => {
    const path = `${root}/${scope}/change-requests`;
    const query = new URLSearchParams();
    if (stateIntent) query.set("state", stateIntent);
    if (typeIntent === "All") query.set("type", "All");
    if (scope === "software") query.set("level", artifactKind === "LowLevel" ? "LLR" : "HLR");
    if (scope === "software" && artifactId) query.set("assessment", artifactId);
    return query.size ? `${path}?${query}` : path;
  };
  switch (view) {
    case "projects": return "/projects";
    case "builds": return "/projects/fms-product-development/builds";
    case "dashboard": return `${root}/command-center`;
    case "mywork": return `${root}/my-work`;
    case "createSystemScr": return `${root}/systems/change-requests/new${artifactId ? `?requirement=${encodeURIComponent(artifactId)}` : ""}`;
    case "createSoftwareChange": {
      const query = new URLSearchParams();
      if (artifactId) query.set("requirement", artifactId);
      if (artifactKind === "HighLevel") query.set("level", "HLR");
      if (artifactKind === "LowLevel") query.set("level", "LLR");
      return `${root}/software/change-requests/new${query.size ? `?${query}` : ""}`;
    }
    case "scr": return `${root}/${discipline === "software" ? "software" : "systems"}/change-requests/${artifactId}`;
    case "history": return historyPath(discipline === "software" ? "software" : "systems");
    case "requirements": return artifactId ? `${root}/requirements/${artifactId}?discipline=${discipline === "software" ? "software" : "system"}` : `${root}/${discipline === "software" ? "software" : "systems"}/requirements`;
    case "verification": return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification`;
    case "testingCoverage": return `${root}/${verificationBranch(discipline, artifactKind)}/coverage`;
    case "testResults": return `${root}/${verificationBranch(discipline, artifactKind)}/results${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}`;
    case "documents": {
      if (discipline === "systemTest" || discipline === "softwareTest")
        return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification/documents`;
      return `${root}/${discipline === "software" ? "software" : "systems"}/documents`;
    }
    case "problemReports": return `${root}/problem-reports${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}`;
    case "code": return `${root}/code`;
    case "lifecycle": return artifactId ? `${root}/traceability/${encodeURIComponent(artifactId)}` : `${root}/traceability`;
    case "planning": return `${root}/release-planning`;
    case "baselines": return `${root}/baselines`;
    case "release": return `${root}/release-readiness`;
    case "releaseImpact": return `${root}/release-readiness/changes/${artifactId}`;
    case "releaseDecision": return `${root}/release-readiness/evidence`;
    case "releaseOperations": return `${root}/release-readiness/operations`;
    case "enterprise": return `${root}/enterprise-control`;
    case "integrations": return `${root}/integration-command-center`;
    case "admin": return `${root}/administration`;
    case "reviewWorkflows": return `${root}/review-workflows`;
    case "artifact": return `${root}/artifacts/${artifactKind}/${artifactId}`;
    default: return root;
  }
}

export function artifactPath(context: RouteContext, kind: string, id: string, discipline = "system") {
  if (kind === "change-request") return routePath(context, "scr", discipline === "software" ? "software" : "system", id);
  if (kind === "requirement") return routePath(context, "requirements", discipline === "software" ? "software" : "system", id);
  return routePath(context, "artifact", "system", id, kind);
}
