export type View =
  | "projects" | "builds" | "baselineImports" | "personnel" | "approvalConfiguration" | "projectConfiguration" | "dashboard" | "createSystemScr" | "createSoftwareChange" | "createInterfaceChange" | "scr" | "baselines" | "history" | "requirements"
  | "verification" | "testingCoverage" | "testChangeRequests" | "testChangeRequest" | "createTestChangeRequest" | "procedureExplorer" | "testResults" | "documents" | "managedDocuments" | "code" | "problemReports" | "lifecycle" | "release" | "releaseImpact" | "releaseDecision" | "releaseOperations" | "planning" | "mywork" | "teamwork" | "admin" | "enterprise" | "integrations" | "reviewWorkflows" | "artifact" | "notFound";

export type Discipline = "system" | "software" | "systemTest" | "softwareTest";

/// Which branch of verification a page belongs to. System has one; software has two, because HLR and LLR
/// test work is planned, done and approved by different people and asked about separately.
const verificationBranch = (discipline: Discipline, artifactKind?: string) =>
  discipline === "softwareTest"
    ? `software-verification/${artifactKind?.toLowerCase().includes("lowlevel") ? "llr" : "hlr"}`
    : "system-verification";

const verificationArtifactKind = (level: string, query: URLSearchParams) =>
  query.get("kind")?.toLowerCase() === "procedure" ? `${level}Procedure` : level;

const verificationKindSuffix = (artifactKind?: string) =>
  artifactKind?.toLowerCase().includes("procedure") ? "?kind=Procedure" : "";

export type HistoryStateIntent =
  | "Draft"
  | "InReview"
  | "Approved"
  | "SelectedForBaseline"
  // Work put away for another release. Reachable as its own view because that is the whole point of it —
  // shelved work nobody can find has not been shelved, it has been lost.
  | "Deferred"
  | "ApprovedOrSelected";

export type HistoryTypeIntent = "System" | "Software" | "Interface" | "All";

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
  /// A Project addressed by name, on the two pages that sit above any build.
  projectSlug?: string;
  releaseId?: string;
  artifactId?: string;
  artifactKind?: string;
  /// The exact verification artifact revision a trace deep link opens. Immutable, so a procedure/case link
  /// cannot silently fall forward to the release-effective or latest revision after later revisions exist.
  artifactRevisionId?: string;
  /// The exact requirement revision a procedure trace deep link opens. Immutable, so a trace that names a
  /// requirement revision keeps naming it after later revisions exist.
  requirementRevisionId?: string;
  /// The exact requirement proposal a Requirements Explorer action should focus in the Draft.
  requirementProposalId?: string;
  savedViewId?: string;
  historyStateIntent?: HistoryStateIntent;
  historyTypeIntent?: HistoryTypeIntent;
  historySelectionId?: string;
  testChangeRequestSelectionId?: string;
  /// The exact proposal a verification TCR page should focus after an Explorer action.
  testChangeRequestProposalId?: string;
  /// An immutable ProblemReportRevision.Id for the read-only historical Problem Report page.
  historicalProblemReportSnapshotId?: string;
  projectConfigurationSection?: "ladder" | "assurance" | "history" | "readiness" | "approvals";
  /** Opens the existing Explorer's authoritative coverage report, never the legacy assessment workspace. */
  coverageReport?: boolean;
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
  // Addressed by the Project's own name rather than a fixed one. These two pages sit above a build — you
  // reach them without having entered one — so they carry no release, and a second Project needs its own
  // slug rather than the single hardcoded one these used to assume.
  if (parts.length === 3 && parts[0] === "projects" && parts[2] === "builds")
    return { view: "builds", discipline: "system", projectSlug: decoded(parts[1]) };
  // Alongside Software Builds rather than inside a build, because an import does not belong to a build — it
  // creates one. There is no build to have entered when this page is the thing you need.
  if (parts.length === 3 && parts[0] === "projects" && parts[2] === "imported-baselines")
    return { view: "baselineImports", discipline: "system", projectSlug: decoded(parts[1]) };
  if (parts[0] === "programs" && parts[2] === "projects" && parts[4] === "documentation-center" && parts.length <= 6)
    return { programId: decoded(parts[1]), projectId: decoded(parts[3]), view: "managedDocuments", discipline: "system", artifactId: decoded(parts[5]) };
  // Also above a build: who is on the Project, and what they are authorised to do, is the same across every
  // build the Project has. A person is not added to 1.6 and withheld from 1.5.
  if (parts.length === 3 && parts[0] === "projects" && parts[2] === "personnel")
    return { view: "personnel", discipline: "system", projectSlug: decoded(parts[1]) };
  // What each artifact requires before release is a property of the Project, and answering whether anybody
  // can sign it needs the roster — which is also above any one build.
  if (parts.length === 3 && parts[0] === "projects" && parts[2] === "approval-configuration")
    return { view: "approvalConfiguration", discipline: "system", projectSlug: decoded(parts[1]) };
  if (parts.length === 4 && parts[0] === "projects" && parts[2] === "configuration" && parts[3] === "approvals")
    return { view: "projectConfiguration", discipline: "system", projectSlug: decoded(parts[1]), projectConfigurationSection: "approvals" };
  if (parts.length === 4 && parts[0] === "projects" && parts[2] === "configuration" && parts[3] === "assurance")
    return { view: "projectConfiguration", discipline: "system", projectSlug: decoded(parts[1]), projectConfigurationSection: "assurance" };
  if (parts.length === 3 && parts[0] === "projects" && parts[2] === "configuration")
    return { view: "projectConfiguration", discipline: "system", projectSlug: decoded(parts[1]) };
  if (parts[0] !== "programs" || parts[2] !== "projects" || parts[4] !== "releases")
    return { view: "notFound", discipline: "system" };

  const base = { programId: decoded(parts[1]), projectId: decoded(parts[3]), releaseId: decoded(parts[5]) };
  const tail = parts.slice(6);
  const path = tail.join("/");
  const coverageReport = query.get("coverage") === "report";
  if (!path || path === "command-center") return { ...base, view: "dashboard", discipline: "system" };
  if (path === "my-work") return { ...base, view: "mywork", discipline: "system" };
  if (path === "team-work") return { ...base, view: "teamwork", discipline: "system" };
  if (path === "systems/change-requests") return { ...base, view: "history", discipline: "system", historySelectionId: query.get("selection") || undefined, historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "System" };
  if (path === "software/change-requests") return { ...base, view: "history", discipline: "software", artifactId: query.get("assessment") || undefined, artifactKind: query.get("level") === "LLR" ? "LowLevel" : "HighLevel", historySelectionId: query.get("selection") || undefined, historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: query.get("type") === "All" ? "All" : "Software" };
  if (path === "interfaces/change-requests") return { ...base, view: "history", discipline: "system", historySelectionId: query.get("selection") || undefined, historyStateIntent: historyStateIntent(query.get("state")), historyTypeIntent: "Interface" };
  if (path === "systems/change-requests/new") return { ...base, view: "createSystemScr", discipline: "system", artifactId: query.get("requirement") || undefined };
  if (path === "software/change-requests/new") return { ...base, view: "createSoftwareChange", discipline: "software", artifactId: query.get("requirement") || undefined, artifactKind: query.get("level") === "HLR" ? "HighLevel" : query.get("level") === "LLR" ? "LowLevel" : undefined };
  if (path === "interfaces/change-requests/new") return { ...base, view: "createInterfaceChange", discipline: "system", artifactId: query.get("requirement") || undefined, artifactKind: "Interface" };
  if (tail[0] === "interfaces" && tail[1] === "change-requests" && tail[2]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[2]), artifactKind: "Interface", requirementProposalId: query.get("proposalId") || undefined };
  if (tail[0] === "systems" && tail[1] === "change-requests" && tail[2]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[2]), requirementProposalId: query.get("proposalId") || undefined };
  if (tail[0] === "software" && tail[1] === "change-requests" && tail[2]) return { ...base, view: "scr", discipline: "software", artifactId: decoded(tail[2]), requirementProposalId: query.get("proposalId") || undefined };
  // Compatibility for links created before typed change-request routes existed. The detail view replaces this
  // with the canonical typed path after the authorized record reveals its type.
  if (tail[0] === "change-requests" && tail[1]) return { ...base, view: "scr", discipline: "system", artifactId: decoded(tail[1]), requirementProposalId: query.get("proposalId") || undefined };
  if (path === "systems/requirements") return { ...base, view: "requirements", discipline: "system", savedViewId: query.get("view") || undefined, requirementRevisionId: query.get("requirementRevisionId") || undefined };
  if (path === "software/requirements") return { ...base, view: "requirements", discipline: "software", savedViewId: query.get("view") || undefined, requirementRevisionId: query.get("requirementRevisionId") || undefined };
  if (path === "systems/documents") return { ...base, view: "documents", discipline: "system" };
  if (path === "software/documents") return { ...base, view: "documents", discipline: "software" };
  if (path === "system-verification/documents") return { ...base, view: "documents", discipline: "systemTest" };
  if (path === "software-verification/documents") return { ...base, view: "documents", discipline: "softwareTest" };
  if (tail[0] === "requirements" && tail[1]) return { ...base, view: "requirements", discipline: query.get("discipline") === "software" ? "software" : "system", artifactId: decoded(tail[1]), requirementRevisionId: query.get("requirementRevisionId") || undefined };
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
  // Raising a package is a page, not a dialog, exactly as raising a change request is. A controlled proposal
  // authored in a pop-up reads as a lesser thing than the one authored on a page, and it is not one.
  if (path === "system-verification/change-requests/new")
    return { ...base, view: "createTestChangeRequest", discipline: "systemTest" };
  if (path === "software-verification/hlr/change-requests/new")
    return { ...base, view: "createTestChangeRequest", discipline: "softwareTest", artifactKind: verificationArtifactKind("HighLevel", query) };
  if (path === "software-verification/llr/change-requests/new")
    return { ...base, view: "createTestChangeRequest", discipline: "softwareTest", artifactKind: verificationArtifactKind("LowLevel", query) };
  // The register, which is a page of its own here as it is on the requirements side. Declared after the
  // `/new` routes above so raising one is not read as a package whose id happens to be "new".
  if (path === "system-verification/change-requests")
    return { ...base, view: "testChangeRequests", discipline: "systemTest", testChangeRequestSelectionId: query.get("selection") || undefined };
  if (path === "software-verification/hlr/change-requests")
    return { ...base, view: "testChangeRequests", discipline: "softwareTest", artifactKind: verificationArtifactKind("HighLevel", query), testChangeRequestSelectionId: query.get("selection") || undefined };
  if (path === "software-verification/llr/change-requests")
    return { ...base, view: "testChangeRequests", discipline: "softwareTest", artifactKind: verificationArtifactKind("LowLevel", query), testChangeRequestSelectionId: query.get("selection") || undefined };
  if (tail[0] === "system-verification" && tail[1] === "change-requests" && tail[2])
    return { ...base, view: "testChangeRequest", discipline: "systemTest", artifactKind: query.get("kind")?.toLowerCase() === "procedure" ? "Procedure" : undefined, artifactId: decoded(tail[2]), testChangeRequestProposalId: query.get("proposalId") || undefined };
  if (tail[0] === "software-verification" && tail[1] === "hlr" && tail[2] === "change-requests" && tail[3])
    return { ...base, view: "testChangeRequest", discipline: "softwareTest", artifactKind: verificationArtifactKind("HighLevel", query), artifactId: decoded(tail[3]), testChangeRequestProposalId: query.get("proposalId") || undefined };
  if (tail[0] === "software-verification" && tail[1] === "llr" && tail[2] === "change-requests" && tail[3])
    return { ...base, view: "testChangeRequest", discipline: "softwareTest", artifactKind: verificationArtifactKind("LowLevel", query), artifactId: decoded(tail[3]), testChangeRequestProposalId: query.get("proposalId") || undefined };
  if (path === "system-verification/coverage") return { ...base, view: "testingCoverage", discipline: "systemTest" };
  if (tail[0] === "system-verification" && tail[1] === "coverage" && tail[2]) return { ...base, view: "testingCoverage", discipline: "systemTest", artifactId: decoded(tail[2]) };
  if (path === "system-verification/procedures") return { ...base, view: "procedureExplorer", discipline: "systemTest", coverageReport };
  if (path === "software-verification/cases" || path === "software-verification/procedures")
    return { ...base, view: "procedureExplorer", discipline: "softwareTest", artifactKind: path.endsWith("/procedures") ? "Procedure" : "Case", coverageReport };
  if (path === "software-verification/test-artifacts")
    return { ...base, view: "procedureExplorer", discipline: "softwareTest", artifactKind: query.get("artifactLevel") === "LowLevel" ? "LowLevel" : query.get("artifactLevel") === "HighLevel" ? "HighLevel" : undefined, coverageReport };
  if (path === "system-verification/results") return { ...base, view: "testResults", discipline: "systemTest" };
  if (tail[0] === "system-verification" && tail[1] === "results" && tail[2]) return { ...base, view: "testResults", discipline: "systemTest", artifactId: decoded(tail[2]) };
  if (path === "software-verification/hlr/coverage") return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: verificationArtifactKind("HighLevel", query) };
  if (tail[0] === "software-verification" && tail[1] === "hlr" && tail[2] === "coverage" && tail[3]) return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: verificationArtifactKind("HighLevel", query), artifactId: decoded(tail[3]) };
  if (path === "software-verification/hlr/cases" || path === "software-verification/hlr/procedures")
    return { ...base, view: "procedureExplorer", discipline: "softwareTest", artifactKind: path.endsWith("/procedures") ? "HighLevelProcedure" : "HighLevel", coverageReport };
  if (path === "software-verification/hlr/results") return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "HighLevel" };
  if (tail[0] === "software-verification" && tail[1] === "hlr" && tail[2] === "results" && tail[3]) return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "HighLevel", artifactId: decoded(tail[3]) };
  if (path === "software-verification/llr/coverage") return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: verificationArtifactKind("LowLevel", query) };
  if (tail[0] === "software-verification" && tail[1] === "llr" && tail[2] === "coverage" && tail[3]) return { ...base, view: "testingCoverage", discipline: "softwareTest", artifactKind: verificationArtifactKind("LowLevel", query), artifactId: decoded(tail[3]) };
  if (path === "software-verification/llr/cases" || path === "software-verification/llr/procedures")
    return { ...base, view: "procedureExplorer", discipline: "softwareTest", artifactKind: path.endsWith("/procedures") ? "LowLevelProcedure" : "LowLevel", coverageReport };
  if (path === "software-verification/llr/results") return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "LowLevel" };
  if (tail[0] === "software-verification" && tail[1] === "llr" && tail[2] === "results" && tail[3]) return { ...base, view: "testResults", discipline: "softwareTest", artifactKind: "LowLevel", artifactId: decoded(tail[3]) };
  if (path === "system-verification") return { ...base, view: "verification", discipline: "systemTest" };
  if (path === "software-verification") return { ...base, view: "verification", discipline: "softwareTest" };
  if (path === "code") return { ...base, view: "code", discipline: "software" };
  if (path === "documentation-center") return { ...base, view: "managedDocuments", discipline: "system" };
  if (tail[0] === "documentation-center" && tail[1]) return { ...base, view: "managedDocuments", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "problem-reports") return { ...base, view: "problemReports", discipline: "system", historicalProblemReportSnapshotId: query.get("snapshotId") || undefined };
  if (tail[0] === "problem-reports" && tail[1]) return { ...base, view: "problemReports", discipline: "system", artifactId: decoded(tail[1]), historicalProblemReportSnapshotId: query.get("snapshotId") || undefined };
  if (path === "traceability") return { ...base, view: "lifecycle", discipline: "system" };
  if (tail[0] === "traceability" && tail[1] === "change-requests" && tail[2])
    return { ...base, view: "lifecycle", discipline: "system", artifactId: decoded(tail[2]), artifactKind: "change-request" };
  // The focused artifact is part of the address, not just component state. Without it the route rewrote
  // itself to a bare /traceability, the app re-read that URL, and the requirement the reader arrived from
  // was cleared before the thread could open on it.
  if (tail[0] === "traceability" && tail[1]) return { ...base, view: "lifecycle", discipline: "system", artifactId: decoded(tail[1]) };
  if (path === "release-planning") return { ...base, view: "notFound", discipline: "system" };
  if (path === "baselines") return { ...base, view: "baselines", discipline: "system" };
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
    return { ...base, view: "artifact", discipline: "system", artifactKind, artifactId: decoded(tail[2]), artifactRevisionId: query.get("revisionId") || undefined };
  }
  return { ...base, view: "notFound", discipline: "system" };
}

export function readRoute(): AppRoute {
  return parseRoute(location.pathname, location.search);
}

export type RouteContext = { programId: string; projectId: string; releaseId: string };

/// A Project's name as a URL segment. The two pages above a build address Projects by name, so this is what
/// makes "fms-product-development" a consequence of the Project being called FMS Product Development rather
/// than a constant that happened to match it.
export const projectSlugOf = (name: string) =>
  name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");

const projectAreaSegments = {
  builds: "builds",
  baselineImports: "imported-baselines",
  personnel: "personnel",
  approvalConfiguration: "approval-configuration",
  projectConfiguration: "configuration",
} as const;

export const projectAreaPath = (slug: string, area: keyof typeof projectAreaSegments) =>
  `/projects/${slug}/${projectAreaSegments[area]}`;

export const projectConfigurationApprovalsPath = (slug: string) =>
  `${projectAreaPath(slug, "projectConfiguration")}/approvals`;

export const projectConfigurationAssurancePath = (slug: string) =>
  `${projectAreaPath(slug, "projectConfiguration")}/assurance`;

export function routePath(context: RouteContext, view: View, discipline: Discipline = "system", artifactId?: string, artifactKind?: string, stateIntent?: HistoryStateIntent, typeIntent?: HistoryTypeIntent, selectionId?: string, proposalId?: string, artifactRevisionId?: string) {
  const root = `/programs/${context.programId}/projects/${context.projectId}/releases/${context.releaseId}`;
  const historyPath = (scope: "systems" | "software" | "interfaces") => {
    const path = `${root}/${scope}/change-requests`;
    const query = new URLSearchParams();
    if (stateIntent) query.set("state", stateIntent);
    if (typeIntent === "All") query.set("type", "All");
    if (scope === "software") query.set("level", artifactKind === "LowLevel" ? "LLR" : "HLR");
    if (scope === "software" && artifactId) query.set("assessment", artifactId);
    if (selectionId) query.set("selection", selectionId);
    return query.size ? `${path}?${query}` : path;
  };
  switch (view) {
    case "projects": return "/projects";
    // Both are reached through projectAreaPath, which knows the Project's name. These remain so a stray
    // routePath call still lands somewhere real rather than on Not Found.
    case "builds": return projectAreaPath("fms-product-development", "builds");
    case "baselineImports": return projectAreaPath("fms-product-development", "baselineImports");
    case "personnel": return projectAreaPath("fms-product-development", "personnel");
    case "approvalConfiguration": return projectAreaPath("fms-product-development", "approvalConfiguration");
    case "projectConfiguration": return projectAreaPath("fms-product-development", "projectConfiguration");
    case "dashboard": return `${root}/command-center`;
    case "mywork": return `${root}/my-work`;
    case "teamwork": return `${root}/team-work`;
    case "createSystemScr": return `${root}/systems/change-requests/new${artifactId ? `?requirement=${encodeURIComponent(artifactId)}` : ""}`;
    case "createSoftwareChange": {
      const query = new URLSearchParams();
      if (artifactId) query.set("requirement", artifactId);
      if (artifactKind === "HighLevel") query.set("level", "HLR");
      if (artifactKind === "LowLevel") query.set("level", "LLR");
      return `${root}/software/change-requests/new${query.size ? `?${query}` : ""}`;
    }
    case "createInterfaceChange": return `${root}/interfaces/change-requests/new${artifactId ? `?requirement=${encodeURIComponent(artifactId)}` : ""}`;
    case "scr": return artifactKind === "Interface"
      ? `${root}/interfaces/change-requests/${artifactId}`
      : `${root}/${discipline === "software" ? "software" : "systems"}/change-requests/${artifactId}`;
    case "history": return historyPath(typeIntent === "Interface" ? "interfaces" : discipline === "software" ? "software" : "systems");
    case "requirements": return artifactId ? `${root}/requirements/${artifactId}?discipline=${discipline === "software" ? "software" : "system"}` : `${root}/${discipline === "software" ? "software" : "systems"}/requirements`;
    case "verification": return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification`;
    case "testingCoverage": return `${root}/${verificationBranch(discipline, artifactKind)}/coverage${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}${verificationKindSuffix(artifactKind)}`;
    case "testChangeRequests": {
      const path = `${root}/${verificationBranch(discipline, artifactKind)}/change-requests`;
      const query = new URLSearchParams();
      if (artifactKind?.toLowerCase().includes("procedure")) query.set("kind", "Procedure");
      if (selectionId) query.set("selection", selectionId);
      return `${path}${query.size ? `?${query}` : ""}`;
    }
    case "testChangeRequest": {
      const path = `${root}/${verificationBranch(discipline, artifactKind)}/change-requests/${encodeURIComponent(artifactId ?? "")}${verificationKindSuffix(artifactKind)}`;
      return proposalId ? `${path}${path.includes("?") ? "&" : "?"}proposalId=${encodeURIComponent(proposalId)}` : path;
    }
    case "createTestChangeRequest": return `${root}/${verificationBranch(discipline, artifactKind)}/change-requests/new${verificationKindSuffix(artifactKind)}`;
    case "procedureExplorer": return discipline === "softwareTest"
      ? `${root}/software-verification/test-artifacts`
      : `${root}/system-verification/procedures`;
    case "testResults": return `${root}/${verificationBranch(discipline, artifactKind)}/results${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}`;
    case "documents": {
      if (discipline === "systemTest" || discipline === "softwareTest")
        return `${root}/${discipline === "softwareTest" ? "software" : "system"}-verification/documents`;
      return `${root}/${discipline === "software" ? "software" : "systems"}/documents`;
    }
    case "problemReports": return `${root}/problem-reports${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}`;
    case "managedDocuments": return `/programs/${context.programId}/projects/${context.projectId}/documentation-center${artifactId ? `/${encodeURIComponent(artifactId)}` : ""}`;
    case "code": return `${root}/code`;
    case "lifecycle": return artifactId
      ? artifactKind === "change-request"
        ? `${root}/traceability/change-requests/${encodeURIComponent(artifactId)}`
        : `${root}/traceability/${encodeURIComponent(artifactId)}`
      : `${root}/traceability`;
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
    case "artifact": {
      const path = `${root}/artifacts/${artifactKind}/${artifactId}`;
      return artifactRevisionId ? `${path}?revisionId=${encodeURIComponent(artifactRevisionId)}` : path;
    }
    default: return root;
  }
}

/**
 * Coverage is an Explorer report over the exact build-scoped verification inventory. Keep this separate from
 * `testingCoverage`, whose `/coverage` routes remain compatibility addresses for Downstream Assessments.
 */
export function coverageExplorerPath(context: RouteContext, discipline: "systemTest" | "softwareTest", level?: "HighLevel" | "LowLevel") {
  const path = routePath(context, "procedureExplorer", discipline);
  const query = new URLSearchParams({ coverage: "report" });
  if (discipline === "softwareTest") {
    query.set("artifactLevel", level === "LowLevel" ? "LowLevel" : "HighLevel");
    query.set("artifactKind", "Case");
  }
  return `${path}?${query}`;
}

export function artifactPath(context: RouteContext, kind: string, id: string, discipline = "system", level?: string) {
  if (kind === "change-request") return routePath(context, "scr", discipline === "software" ? "software" : "system", id, level === "Interface" ? "Interface" : undefined);
  if (kind === "requirement") return routePath(context, "requirements", discipline === "software" ? "software" : "system", id);
  if (kind === "managed-document") return `/programs/${context.programId}/projects/${context.projectId}/documentation-center/${encodeURIComponent(id)}`;
  return routePath(context, "artifact", "system", id, kind);
}

/**
 * Build the canonical browser address for a node returned by the server-owned Change Request trace
 * projection. This is intentionally a presentation/router primitive: it does not infer a relationship or
 * choose a revision. The node's exact id (and, for a requirement revision, its exact revision id) is carried
 * through to the existing authorized surface. A missing discriminator returns undefined so callers can make
 * a truthful non-openable value instead of inventing a route.
 */
export type ExactTraceArtifact = {
  id: string
  kind: string
  displayNumber?: string | null
  level?: string | null
  buildId?: string | null
  artifactId?: string | null
  revisionId?: string | null
}

export function exactTraceArtifactPath(context: RouteContext, node: ExactTraceArtifact): string | undefined {
  if (!node.id) return undefined;
  const scoped = node.buildId ? { ...context, releaseId: node.buildId } : context;
  const display = (node.displayNumber ?? '').toUpperCase();

  if (node.kind === 'ChangeRequest') {
    const discipline = node.level === 'HighLevel' || node.level === 'LowLevel' || display.startsWith('HLRCR-') || display.startsWith('LLRCR-')
      ? 'software' : 'system';
    return routePath(scoped, 'scr', discipline, node.id, node.level === 'Interface' || display.startsWith('ICDCR-') ? 'Interface' : undefined);
  }

  if (node.kind === 'TestChangeRequest') {
    const isSystem = display.startsWith('SYSTP') || display.startsWith('SYSTCR');
    const discipline: Discipline = isSystem ? 'systemTest' : 'softwareTest';
    const procedure = node.level?.toLowerCase().includes('procedure') || display.startsWith('SYSTPCR-') || display.startsWith('HLRTPCR-') || display.startsWith('LLRTPCR-');
    const level = isSystem ? (procedure ? 'Procedure' : undefined) : display.startsWith('LLR') ? (procedure ? 'LowLevelProcedure' : 'LowLevel') : (procedure ? 'HighLevelProcedure' : 'HighLevel');
    return routePath(scoped, 'testChangeRequest', discipline, node.id, level);
  }

  if (node.kind === 'RequirementRevision') {
    if (!node.artifactId) return undefined;
    const discipline = node.level === 'HighLevel' || node.level === 'LowLevel' ? 'software' : 'system';
    const path = routePath(scoped, 'requirements', discipline, node.artifactId);
    return `${path}&requirementRevisionId=${encodeURIComponent(node.id)}`;
  }

  if (node.kind === 'TestProcedure' || node.kind === 'TestCase')
    return routePath(scoped, 'artifact', 'system', node.id, node.kind === 'TestProcedure' ? 'test-procedure' : 'test-case', undefined, undefined, undefined, undefined, node.revisionId ?? undefined);
  if (node.kind === 'TestExecution') return routePath(scoped, 'artifact', 'system', node.id, 'test-execution');
  if (node.kind === 'Evidence') return routePath(scoped, 'artifact', 'system', node.id, 'evidence');

  // Build and code-traceability records currently have no exact authorized
  // artifact-record route (the build page is deliberately NotFound). Keep
  // their identifiers intentionally non-openable; never invent an URL.
  return undefined;
}
