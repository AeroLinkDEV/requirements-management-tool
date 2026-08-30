import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent } from "react";
import { createPortal } from "react-dom";
import type { AuthUser } from "./IdentityCenter";
import { stateLabel } from "./presentation";
import "./TeamWork.css";

type TeamWorkRelease = { id: string; version: string; isReleased: boolean };
type TeamWorkAllocation = {
  baselineId: string;
  releaseId: string;
  releaseVersion: string;
  baselineNumber: string;
  baselineRevision: number;
  isReleased: boolean;
};
type AccountState = "active" | "disabled" | "locked";
type DisciplineAffinity = "system" | "software";
type ModernBaseRole =
  | "SystemEngineer" | "SoftwareEngineer" | "SystemTestEngineer" | "SoftwareTestEngineer"
  | "ProjectEngineer" | "EngineeringManager" | "ProgramManager" | "ConfigurationManager"
  | "SoftwareQualityAnalyst" | "Airworthiness";
type TeamWorkPerson = {
  userId: string | null;
  userName: string;
  displayName: string;
  isCurrentProjectMember: boolean;
  accountState: AccountState | null;
  baseRoles: ModernBaseRole[];
  disciplineAffinities: DisciplineAffinity[];
  holds: number;
  byLane: { work: number; review: number; sign: number; approved: number };
};
type TeamWorkStageObligation = { holderId: string; stageKind: "review" | "approval" };
type TeamWorkItem = {
  id: string;
  family: TeamWorkFamily;
  category?: string | null;
  prefix?: string | null;
  number?: string | null;
  title: string;
  lane: LaneId;
  nativeState: string;
  nativeOutcome?: string | null;
  currentHolderIds: string[];
  holderBasis: TeamWorkHolderBasis;
  activeStageObligations: TeamWorkStageObligation[];
  raisedById?: string | null;
  raisedByKind?: string | null;
  release?: TeamWorkRelease | null;
  deferred: boolean;
  allocation?: TeamWorkAllocation | null;
  deferredFromState?: string | null;
  updatedAt: string;
  openUrl: string;
};
type TeamWorkResponse = {
  generatedAt: string;
  totals: { items: number; returned: number; unheld: number };
  people: TeamWorkPerson[];
  items: TeamWorkItem[];
};
type LaneId = "work" | "review" | "sign" | "approved";
type TeamWorkFamily =
  | "system" | "software" | "interface" | "verification" | "problemReport" | "assessment";
type TeamWorkHolderBasis =
  | "none" | "author" | "assignedEngineer" | "responsibleEngineer"
  | "activeReviewStage" | "activeApprovalStage" | "activeReviewAndApprovalStages"
  | "selectedAssessmentApprover";

const lanes: ReadonlyArray<{ id: LaneId; title: string; description: string }> = [
  { id: "work", title: "In work", description: "Active authoring and assigned work" },
  { id: "review", title: "In review", description: "Review obligations in progress" },
  { id: "sign", title: "Awaiting signature", description: "Approval obligations in progress" },
  { id: "approved", title: "Approved", description: "Approved controlled work" },
];
const laneIds = new Set<LaneId>(lanes.map(lane => lane.id));
const laneKeys = ["work", "review", "sign", "approved"] as const;
const familyIds = new Set<TeamWorkFamily>([
  "system", "software", "interface", "verification", "problemReport", "assessment",
]);
const holderBasisIds = new Set<TeamWorkHolderBasis>([
  "none", "author", "assignedEngineer", "responsibleEngineer", "activeReviewStage",
  "activeApprovalStage", "activeReviewAndApprovalStages", "selectedAssessmentApprover",
]);
const stageKinds = new Set(["review", "approval"]);
const accountStates = new Set<AccountState>(["active", "disabled", "locked"]);
const modernRoles = new Set<ModernBaseRole>([
  "SystemEngineer", "SoftwareEngineer", "SystemTestEngineer", "SoftwareTestEngineer",
  "ProjectEngineer", "EngineeringManager", "ProgramManager", "ConfigurationManager",
  "SoftwareQualityAnalyst", "Airworthiness",
]);
const knownAffinities = new Set<DisciplineAffinity>(["system", "software"]);
const familyBadgeLabels: Record<TeamWorkFamily, string> = {
  system: "System", software: "Software", interface: "Interface", verification: "Verification",
  problemReport: "Problem Report", assessment: "Assessment",
};
const roleLabels: Record<ModernBaseRole, string> = {
  SystemEngineer: "System Engineer", SoftwareEngineer: "Software Engineer",
  SystemTestEngineer: "System Test Engineer", SoftwareTestEngineer: "Software Test Engineer",
  ProjectEngineer: "Project Engineer", EngineeringManager: "Engineering Manager",
  ProgramManager: "Program Manager", ConfigurationManager: "Configuration Manager",
  SoftwareQualityAnalyst: "Software Quality Analyst", Airworthiness: "Airworthiness",
};
const holderBasisLabels: Record<TeamWorkHolderBasis, string> = {
  none: "No active holder obligation", author: "Author action",
  assignedEngineer: "Assigned engineer obligation", responsibleEngineer: "Responsible engineer obligation",
  activeReviewStage: "Active review obligation", activeApprovalStage: "Active approval obligation",
  activeReviewAndApprovalStages: "Active review and approval obligations",
  selectedAssessmentApprover: "Selected assessment approver obligation",
};
const originLabels: Record<string, string> = {
  changeRequest: "source change request", problemReport: "source problem report",
  caseChange: "source case change", caseAssessment: "source case assessment", caseReview: "source case review",
};
const guidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const guidExpression = new RegExp(`^${guidPattern}$`, "i");
const canonicalOpenUrlExpression = new RegExp(
  `^/open/(change-request|test-change-request|problem-report|downstream-assessment)/(${guidPattern})$`,
  "i",
);
const canonicalOpenKindByFamily: Record<TeamWorkFamily, string> = {
  system: "change-request", software: "change-request", interface: "change-request",
  verification: "test-change-request", problemReport: "problem-report", assessment: "downstream-assessment",
};
const affinityStorageKey = "aerolink-teamwork-affinity";

const isRecord = (value: unknown): value is Record<string, unknown> =>
  !!value && typeof value === "object" && !Array.isArray(value);
const isNonBlankString = (value: unknown): value is string =>
  typeof value === "string" && value.trim().length > 0;
const isOptionalString = (value: unknown): value is string | null | undefined =>
  value === null || value === undefined || isNonBlankString(value);
const isNonNegativeInteger = (value: unknown): value is number =>
  Number.isInteger(value) && (value as number) >= 0;
const isGuid = (value: unknown): value is string =>
  typeof value === "string" && guidExpression.test(value);
const isLane = (value: unknown): value is LaneId =>
  typeof value === "string" && laneIds.has(value as LaneId);
const isFamily = (value: unknown): value is TeamWorkFamily =>
  typeof value === "string" && familyIds.has(value as TeamWorkFamily);
const isHolderBasis = (value: unknown): value is TeamWorkHolderBasis =>
  typeof value === "string" && holderBasisIds.has(value as TeamWorkHolderBasis);
const isValidRelease = (value: unknown): value is TeamWorkRelease =>
  isRecord(value) && isGuid(value.id) && isNonBlankString(value.version) && typeof value.isReleased === "boolean";
const isValidAllocation = (value: unknown): value is TeamWorkAllocation =>
  isRecord(value) && isGuid(value.baselineId) && isGuid(value.releaseId)
  && isNonBlankString(value.releaseVersion) && isNonBlankString(value.baselineNumber)
  && isNonNegativeInteger(value.baselineRevision) && typeof value.isReleased === "boolean";

function isUniqueStringList(values: unknown[], known: Set<string>): values is string[] {
  const seen = new Set<string>();
  return values.every(value => {
    if (typeof value !== "string" || !known.has(value) || seen.has(value)) return false;
    seen.add(value);
    return true;
  });
}

function isValidPerson(value: unknown): value is TeamWorkPerson {
  if (!isRecord(value) || !(value.userId === null || isGuid(value.userId))
    || !isNonBlankString(value.userName) || !isNonBlankString(value.displayName)
    || typeof value.isCurrentProjectMember !== "boolean"
    || !(value.accountState === null || typeof value.accountState === "string"
      && accountStates.has(value.accountState as AccountState))
    || !Array.isArray(value.baseRoles) || !isUniqueStringList(value.baseRoles, modernRoles)
    || !Array.isArray(value.disciplineAffinities) || !isUniqueStringList(value.disciplineAffinities, knownAffinities)
    || !isNonNegativeInteger(value.holds) || !isRecord(value.byLane)
    ) return false;
  const byLane = value.byLane;
  if (!laneKeys.every(lane => isNonNegativeInteger(byLane[lane]))) return false;
  if (value.isCurrentProjectMember && (value.userId === null || value.accountState === null)) return false;
  if (!value.isCurrentProjectMember && value.userId === null && value.accountState !== null) return false;
  const expected: DisciplineAffinity[] = [];
  if (value.baseRoles.some(role => role === "SystemEngineer" || role === "SystemTestEngineer")) expected.push("system");
  if (value.baseRoles.some(role => role === "SoftwareEngineer" || role === "SoftwareTestEngineer")) expected.push("software");
  return JSON.stringify(value.disciplineAffinities) === JSON.stringify(expected)
    && value.holds === laneKeys.reduce((total, lane) => total + (byLane[lane] as number), 0);
}

function isValidItem(value: unknown, people: Map<string, TeamWorkPerson>): value is TeamWorkItem {
  if (!isRecord(value)) return false;
  const { family, lane, holderBasis, currentHolderIds, activeStageObligations, updatedAt } = value;
  if (!isGuid(value.id) || typeof value.title !== "string" || !isNonBlankString(value.nativeState)
    || !isOptionalString(value.category) || !isOptionalString(value.prefix) || !isOptionalString(value.number)
    || !isOptionalString(value.nativeOutcome) || typeof value.deferred !== "boolean"
    || !isOptionalString(value.deferredFromState) || !isNonBlankString(updatedAt)
    || Number.isNaN(Date.parse(updatedAt)) || !isFamily(family) || !isLane(lane)
    || !isHolderBasis(holderBasis) || !Array.isArray(currentHolderIds)
    || !currentHolderIds.every(isNonBlankString) || !Array.isArray(activeStageObligations)
    || !activeStageObligations.every(obligation => isRecord(obligation)
      && isNonBlankString(obligation.holderId) && typeof obligation.stageKind === "string"
      && stageKinds.has(obligation.stageKind))) return false;
  if (!isNonBlankString(value.openUrl) || !canonicalOpenUrlExpression.test(value.openUrl)) return false;
  if (family === "assessment") {
    if (!isNonBlankString(value.category) || value.number !== null && value.number !== undefined
      || value.prefix !== null && value.prefix !== undefined) return false;
  } else if (!isNonBlankString(value.number)
    || family !== "problemReport" && !isNonBlankString(value.category)) return false;
  if (value.release !== null && value.release !== undefined && !isValidRelease(value.release)) return false;
  if (value.allocation !== null && value.allocation !== undefined && !isValidAllocation(value.allocation)) return false;
  if (!isOptionalString(value.raisedById) || !isOptionalString(value.raisedByKind)) return false;
  if (value.raisedByKind !== null && value.raisedByKind !== undefined
    && !originLabels[value.raisedByKind] && value.raisedByKind !== "author" && value.raisedByKind !== "reportedBy") return false;
  if ((value.raisedByKind === "author" || value.raisedByKind === "reportedBy") && !isNonBlankString(value.raisedById)) return false;
  const match = value.openUrl.match(canonicalOpenUrlExpression);
  if (!match || match[2].toLowerCase() !== value.id.toLowerCase()
    || match[1].toLowerCase() !== canonicalOpenKindByFamily[family]) return false;
  const holders = new Set<string>();
  if (currentHolderIds.some(holder => {
    const key = holder.toLowerCase();
    if (holders.has(key)) return true;
    holders.add(key);
    return false;
  })
    || holderBasis === "none" && currentHolderIds.length !== 0
    || currentHolderIds.some(holder => !people.has(holder.toLowerCase()))) return false;
  const obligations = new Set<string>();
  if (activeStageObligations.some(obligation => {
    const key = `${obligation.holderId.toLowerCase()}|${obligation.stageKind}`;
    if (obligations.has(key) || !holders.has(obligation.holderId.toLowerCase())) return true;
    obligations.add(key);
    return false;
  })) return false;
  const obligationKinds = activeStageObligations.map(obligation => obligation.stageKind);
  const obligationHolders = new Set(
    activeStageObligations.map(obligation => obligation.holderId.toLowerCase()),
  );
  const everyHolderHasAnObligation = obligationHolders.size === holders.size
    && [...holders].every(holder => obligationHolders.has(holder));
  const stageBasedHolderBasis = holderBasis === "activeReviewStage"
    || holderBasis === "activeApprovalStage"
    || holderBasis === "activeReviewAndApprovalStages";
  if (stageBasedHolderBasis
    && (family === "problemReport" || family === "assessment" || value.nativeState !== "InReview"
      || activeStageObligations.length === 0)) return false;
  if (holderBasis === "activeReviewStage" && lane !== "review") return false;
  if ((holderBasis === "activeApprovalStage" || holderBasis === "activeReviewAndApprovalStages")
    && lane !== "sign") return false;
  if (holderBasis === "activeReviewStage"
    && (!everyHolderHasAnObligation
      || obligationKinds.some(kind => kind !== "review"))) return false;
  if (holderBasis === "activeApprovalStage"
    && (!everyHolderHasAnObligation
      || obligationKinds.some(kind => kind !== "approval"))) return false;
  if (holderBasis === "activeReviewAndApprovalStages"
    && (!everyHolderHasAnObligation
      || !obligationKinds.includes("review") || !obligationKinds.includes("approval"))) return false;
  if (!holderBasis.startsWith("activeReview") && holderBasis !== "activeApprovalStage"
    && activeStageObligations.length !== 0) return false;
  return true;
}

function validateProjection(value: unknown): TeamWorkResponse {
  if (!isRecord(value) || !isNonBlankString(value.generatedAt) || Number.isNaN(Date.parse(value.generatedAt))
    || !Array.isArray(value.people) || !Array.isArray(value.items) || !isRecord(value.totals)
    || !isNonNegativeInteger(value.totals.items) || !isNonNegativeInteger(value.totals.returned)
    || !isNonNegativeInteger(value.totals.unheld) || value.totals.returned !== value.items.length
    || value.totals.items < value.totals.returned || value.totals.unheld > value.totals.items) {
    throw new Error("The Team Work response is invalid.");
  }
  const peopleByIdentity = new Map<string, TeamWorkPerson>();
  if (value.people.some(person => !isValidPerson(person))) {
    throw new Error("A Team Work person has an invalid identity, account state, roles, affinity, hold count, or lane counts.");
  }
  for (const person of value.people as TeamWorkPerson[]) {
    const nameKey = person.userName.toLowerCase();
    if (peopleByIdentity.has(nameKey)) throw new Error("The Team Work response contains duplicate people.");
    peopleByIdentity.set(nameKey, person);
    if (person.userId) {
      const idKey = person.userId.toLowerCase();
      if (peopleByIdentity.has(idKey)) throw new Error("The Team Work response contains duplicate people.");
      peopleByIdentity.set(idKey, person);
    }
  }
  if (value.items.some(item => !isValidItem(item, peopleByIdentity))) {
    throw new Error("The Team Work response contains an unknown family, lane, holder basis, or invalid identity, lifecycle, holder obligation, release, or canonical open link.");
  }
  const items = value.items as TeamWorkItem[];
  if (value.totals.items === value.totals.returned) {
    const observed = new Map<string, { holds: number; byLane: Record<LaneId, number> }>();
    for (const item of items) for (const holder of item.currentHolderIds) {
      const key = holder.toLowerCase();
      const current = observed.get(key) ?? { holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } };
      current.holds++;
      current.byLane[item.lane]++;
      observed.set(key, current);
    }
    if (value.totals.unheld !== items.filter(item => item.currentHolderIds.length === 0).length) {
      throw new Error("The Team Work response has invalid unheld totals.");
    }
    for (const person of value.people as TeamWorkPerson[]) {
      const actual = observed.get(person.userName.toLowerCase())
        ?? { holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } };
      if (actual.holds !== person.holds || laneKeys.some(lane => actual.byLane[lane] !== person.byLane[lane])) {
        throw new Error("The Team Work response has inconsistent person hold totals.");
      }
    }
  }
  return {
    generatedAt: value.generatedAt,
    totals: { items: value.totals.items, returned: value.totals.returned, unheld: value.totals.unheld },
    people: value.people as TeamWorkPerson[],
    items,
  };
}

type AffinityStore = {
  version: 1;
  viewers: Record<string, Record<string, Record<string, number>>>;
};
const emptyAffinityStore = (): AffinityStore => ({ version: 1, viewers: {} });

function readAffinityStore(): AffinityStore {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(affinityStorageKey) || "");
    if (!isRecord(parsed) || parsed.version !== 1 || !isRecord(parsed.viewers)) return emptyAffinityStore();
    const valid: AffinityStore = { version: 1, viewers: {} };
    let remainingEntries = 24;
    for (const [viewerId, projectsValue] of Object.entries(parsed.viewers)) {
      if (remainingEntries === 0) break;
      if (!guidExpression.test(viewerId) || !isRecord(projectsValue)) continue;
      const projects: Record<string, Record<string, number>> = {};
      for (const [projectId, countsValue] of Object.entries(projectsValue)) {
        if (remainingEntries === 0) break;
        if (!guidExpression.test(projectId) || !isRecord(countsValue)) continue;
        const counts: Record<string, number> = {};
        for (const [personId, count] of Object.entries(countsValue)) {
          if (guidExpression.test(personId) && typeof count === "number" && Number.isInteger(count) && Number.isFinite(count) && count > 0) {
            counts[personId] = Math.min(999, count);
          }
        }
        projects[projectId] = Object.fromEntries(Object.entries(counts).slice(0, 64));
        remainingEntries--;
      }
      if (Object.keys(projects).length) valid.viewers[viewerId] = projects;
    }
    return valid;
  } catch {
    return emptyAffinityStore();
  }
}

function writeAffinity(viewerId: string, projectId: string, person: TeamWorkPerson) {
  if (!isGuid(viewerId) || !isGuid(projectId) || !person.userId || !isGuid(person.userId)) return;
  const store = readAffinityStore();
  const viewer = store.viewers[viewerId] ?? {};
  const project = viewer[projectId] ?? {};
  const key = person.userId.toLowerCase();
  project[key] = Math.min(999, (project[key] ?? 0) + 1);
  const retained = Object.entries(project)
    .filter(([personId, count]) => personId !== key && Number.isFinite(count) && count > 0)
    .slice(0, 63);
  viewer[projectId] = Object.fromEntries(
    [[key, project[key]], ...retained],
  );
  store.viewers[viewerId] = viewer;

  // Keep the current viewer/project entry, then retain at most 23 other valid entries. The bounded store is
  // a local ordering hint, never an unbounded behavioural history.
  const bounded: AffinityStore = { version: 1, viewers: { [viewerId]: { [projectId]: viewer[projectId] } } };
  let remainingEntries = 23;
  for (const [candidateViewer, projects] of Object.entries(store.viewers)) {
    for (const [candidateProject, counts] of Object.entries(projects)) {
      if (candidateViewer === viewerId && candidateProject === projectId) continue;
      if (remainingEntries-- <= 0) break;
      (bounded.viewers[candidateViewer] ??= {})[candidateProject] = counts;
    }
    if (remainingEntries < 0) break;
  }
  try { localStorage.setItem(affinityStorageKey, JSON.stringify(bounded)); } catch { /* optional local preference */ }
}

function initials(person: Pick<TeamWorkPerson, "displayName" | "userName">) {
  const parts = (person.displayName.trim() || person.userName).split(/\s+/).filter(Boolean);
  return (parts.length > 1 ? `${parts[0][0]}${parts.at(-1)![0]}` : parts[0].slice(0, 2)).toUpperCase();
}
function personMatches(person: TeamWorkPerson, key: string) {
  return person.userName.toLowerCase() === key.toLowerCase() || !!person.userId && person.userId.toLowerCase() === key.toLowerCase();
}
function displayNameFor(id: string, people: Map<string, TeamWorkPerson>) {
  return people.get(id.toLowerCase())?.displayName ?? id;
}
function urlWithoutHolder(url = new URL(window.location.href)) {
  url.searchParams.delete("holder");
  return `${url.pathname}${url.search}${url.hash}`;
}

function TeamWorkCard({ item, people }: { item: TeamWorkItem; people: Map<string, TeamWorkPerson> }) {
  const badge = item.family === "assessment" ? "Assessment" : item.prefix || familyBadgeLabels[item.family];
  const identity = item.family === "assessment" ? item.category! : item.number!;
  const holders = item.currentHolderIds.map(id => displayNameFor(id, people));
  const raisedBy = item.raisedByKind
    ? originLabels[item.raisedByKind]
      ? `Origin: ${originLabels[item.raisedByKind]}`
      : item.raisedById ? `Raised by: ${displayNameFor(item.raisedById, people)}` : undefined
    : undefined;
  return (
    <a className="teamWorkCard" data-team-work-card="true" href={item.openUrl}>
      <div className="teamWorkCardTopline">
        <span className={`teamWorkCardBadge family-${item.family}`} data-family={item.family}>{badge}</span>
        {item.deferred && <span className="teamWorkCardDeferred">Deferred</span>}
      </div>
      <div className="teamWorkCardIdentity">
        <strong>{identity}</strong>
        {item.category && item.number && <span>{item.category}</span>}
      </div>
      <h3>{item.title.trim() || "Title not recorded"}</h3>
      <div className={`teamWorkLanePill lane-${item.lane}`}>
        <i aria-hidden="true" />
        {lanes.find(lane => lane.id === item.lane)?.title}
        <span aria-hidden="true">→</span>
      </div>
      <dl className="teamWorkCardFacts">
        <div>
          <dt>Native state</dt>
          <dd>{stateLabel(item.nativeState)}{item.nativeOutcome ? ` · ${stateLabel(item.nativeOutcome)}` : ""}</dd>
        </div>
        <div>
          <dt>Current holder{holders.length === 1 ? "" : "s"}</dt>
          <dd>{holders.length ? holders.join(", ") : "No current holder"}</dd>
        </div>
        <div>
          <dt>Basis</dt>
          <dd>{holderBasisLabels[item.holderBasis]}</dd>
        </div>
        {raisedBy && (
          <div>
            <dt>{raisedBy.startsWith("Origin:") ? "Origin" : "Raised by"}</dt>
            <dd>{raisedBy.replace(/^(Origin|Raised by): /, "")}</dd>
          </div>
        )}
        <div>
          <dt>Release</dt>
          <dd>{item.release ? `Build ${item.release.version}` : "Release not recorded"}</dd>
        </div>
        {item.allocation && (
          <div>
            <dt>Allocation</dt>
            <dd>Build {item.allocation.releaseVersion}{item.allocation.isReleased ? " · released" : " · in work"}</dd>
          </div>
        )}
        {item.deferredFromState && (
          <div>
            <dt>Deferred from</dt>
            <dd>{stateLabel(item.deferredFromState)}</dd>
          </div>
        )}
        <div>
          <dt>Last updated</dt>
          <dd>{new Date(item.updatedAt).toLocaleString()}</dd>
        </div>
      </dl>
    </a>
  );
}

function PersonStrip({ people, selected, search, viewer, onSelect }: {
  people: TeamWorkPerson[];
  selected: string | null;
  search: string;
  viewer: AuthUser;
  onSelect: (person: TeamWorkPerson, trigger: HTMLElement) => void;
}) {
  const strip = useRef<HTMLDivElement>(null);
  const visible = people.filter(person =>
    !search || `${person.displayName} ${person.userName} ${person.baseRoles.map(role => roleLabels[role]).join(" ")}`
      .toLowerCase().includes(search.toLowerCase()));

  return (
    <section className="teamWorkPeople" aria-labelledby="team-work-people-title">
      <div className="teamWorkPeopleHeader">
        <div>
          <h2 id="team-work-people-title">People</h2>
          <p>Every current project member, including people holding no work</p>
        </div>
        <div className="teamWorkPeopleControls">
          <button type="button" aria-label="Previous people" onClick={() => strip.current?.scrollBy({ left: -280, behavior: "smooth" })}>
            ‹
          </button>
          <button type="button" aria-label="Next people" onClick={() => strip.current?.scrollBy({ left: 280, behavior: "smooth" })}>
            ›
          </button>
        </div>
      </div>
      <div className="teamWorkPeopleStrip" ref={strip} role="list">
        {visible.map(person => {
          const isViewer = personMatches(person, viewer.userName) || personMatches(person, viewer.id);
          const selectedClass = selected === person.userName.toLowerCase() ? "selected" : "";
          const accountLabel = person.accountState === "disabled"
            ? "Account disabled"
            : person.accountState === "locked" ? "Account locked" : null;
          const roleLabel = person.baseRoles.length
            ? person.baseRoles.map(role => roleLabels[role]).join(" · ")
            : "Project member";
          return (
            <div role="listitem" key={person.userName}>
              <button
                type="button"
                className={`teamWorkPerson ${selectedClass}`}
                aria-pressed={selected === person.userName.toLowerCase()}
                onClick={event => onSelect(person, event.currentTarget)}
              >
                <span className="teamWorkPersonAvatar" aria-hidden="true">{initials(person)}</span>
                <span className="teamWorkPersonInfo">
                  <strong>{person.displayName}{isViewer ? " (you)" : ""}</strong>
                  <small>{roleLabel}</small>
                  <small className={`teamWorkAccountState account-${person.accountState ?? "unknown"}`}>
                    {accountLabel ? `${accountLabel} · ` : ""}
                    {person.holds} hold{person.holds === 1 ? "" : "s"}
                  </small>
                </span>
                <span className="teamWorkLoadShape" aria-label={`${person.holds} holds`}>
                  <i style={{ height: `${Math.min(100, person.byLane.work * 18)}%` }} />
                  <i style={{ height: `${Math.min(100, person.byLane.review * 18)}%` }} />
                  <i style={{ height: `${Math.min(100, person.byLane.sign * 18)}%` }} />
                  <i style={{ height: `${Math.min(100, person.byLane.approved * 18)}%` }} />
                </span>
              </button>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function TeamWorkDrawer({ person, items, people, onClose }: {
  person: TeamWorkPerson;
  items: TeamWorkItem[];
  people: Map<string, TeamWorkPerson>;
  onClose: () => void;
}) {
  const dialog = useRef<HTMLDivElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const personItems = items.filter(item => item.currentHolderIds.some(id => id.toLowerCase() === person.userName.toLowerCase()));
  const approvals = new Set(
    personItems
      .filter(item => item.activeStageObligations.some(obligation =>
        obligation.holderId.toLowerCase() === person.userName.toLowerCase()
        && obligation.stageKind === "approval"))
      .map(item => item.id),
  );
  const stats = {
    holds: personItems.length,
    signature: approvals.size,
    work: personItems.filter(item => item.lane === "work").length,
    shared: personItems.filter(item => item.currentHolderIds.length > 1).length,
  };

  useEffect(() => {
    close.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
        return;
      }
      if (event.key !== "Tab" || !dialog.current) return;
      const controls = [...dialog.current.querySelectorAll<HTMLElement>(
        "button, a[href], [tabindex]:not([tabindex='-1'])",
      )].filter(control => !control.hasAttribute("disabled"));
      if (!controls.length) return;
      const first = controls[0];
      const last = controls.at(-1)!;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);
  const closeOnBackdrop = (event: ReactMouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) onClose();
  };

  return (
    <div className="teamWorkDrawerBackdrop" onMouseDown={closeOnBackdrop}>
      <aside
        className="teamWorkDrawer"
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="team-work-drawer-title"
      >
        <header>
          <div>
            <span className="teamWorkEyebrow">CURRENT HOLDER</span>
            <h2 id="team-work-drawer-title">{person.displayName}</h2>
          </div>
          <button type="button" aria-label="Close current holder" ref={close} onClick={onClose}>×</button>
        </header>
        <div className="teamWorkDrawerStats">
          <div><strong>{stats.holds}</strong><span>Currently holds</span></div>
          <div><strong>{stats.signature}</strong><span>Awaiting their signature</span></div>
          <div><strong>{stats.work}</strong><span>In work</span></div>
          <div><strong>{stats.shared}</strong><span>Shared with others</span></div>
        </div>
        <div className="teamWorkDrawerLoad">
          <span>Load shape</span>
          <div
            className="teamWorkDrawerLoadBar"
            aria-label={`${person.byLane.work} in work, ${person.byLane.review} in review, ${person.byLane.sign} awaiting signature, ${person.byLane.approved} approved`}
          >
            {laneKeys.map(lane => person.byLane[lane] > 0 && (
              <i
                className={`load-${lane}`}
                key={lane}
                style={{ width: `${person.holds ? person.byLane[lane] / person.holds * 100 : 0}%` }}
              />
            ))}
          </div>
        </div>
        <div className="teamWorkDrawerRows">
          {lanes.map(lane => {
            const laneItems = personItems.filter(item => item.lane === lane.id);
            if (!laneItems.length) return null;
            return (
              <section key={lane.id}>
                <h3>{lane.title}<span>{laneItems.length}</span></h3>
                {laneItems.map(item => (
                  <a className="teamWorkDrawerRow" href={item.openUrl} key={item.id}>
                    <strong>{item.number ?? item.category}</strong>
                    <span>{item.title.trim() || "Title not recorded"}</span>
                    {item.currentHolderIds.length > 1 && (
                      <small>
                        Also {item.currentHolderIds
                          .filter(id => id.toLowerCase() !== person.userName.toLowerCase())
                          .map(id => displayNameFor(id, people)).join(", ")}
                      </small>
                    )}
                  </a>
                ))}
              </section>
            );
          })}
          {!personItems.length && <p>Nothing currently requires {person.displayName}.</p>}
        </div>
      </aside>
    </div>
  );
}

function TeamWorkBoard({ items, group, people, onHolder }: {
  items: TeamWorkItem[];
  group: "lifecycle" | "holder";
  people: Map<string, TeamWorkPerson>;
  onHolder: (person: TeamWorkPerson, trigger?: HTMLElement) => void;
}) {
  if (group === "lifecycle") {
    return (
      <div className="teamWorkBoard" data-team-work-board="true">
        {lanes.map(lane => {
          const laneItems = items.filter(item => item.lane === lane.id);
          return (
            <section
              className="teamWorkLane"
              data-lane={lane.id}
              aria-labelledby={`team-work-lane-${lane.id}`}
              key={lane.id}
            >
              <header>
                <div>
                  <h2 id={`team-work-lane-${lane.id}`}>{lane.title}</h2>
                  <p>{lane.description}</p>
                </div>
                <span className="teamWorkLaneCount" aria-label={`${laneItems.length} items`}>
                  {laneItems.length}
                </span>
              </header>
              <div className="teamWorkLaneItems">
                {laneItems.length
                  ? laneItems.map(item => (
                    <TeamWorkCard key={`${item.family}-${item.id}`} item={item} people={people} />
                  ))
                  : <p className="teamWorkLaneEmpty">No items in this lane.</p>}
              </div>
            </section>
          );
        })}
      </div>
    );
  }

  const groups = new Map<string, TeamWorkItem[]>();
  for (const item of items) {
    const holders = item.currentHolderIds.length ? item.currentHolderIds : ["__none__"];
    for (const holder of holders) {
      const key = holder.toLowerCase();
      const current = groups.get(key) ?? [];
      current.push(item);
      groups.set(key, current);
    }
  }
  const ordered = [...groups.entries()].sort(([a], [b]) => {
    if (a === "__none__") return 1;
    if (b === "__none__") return -1;
    return displayNameFor(a, people).localeCompare(displayNameFor(b, people), undefined, { sensitivity: "base" });
  });

  return (
    <div className="teamWorkHolderBoard" data-team-work-board="true">
      {ordered.map(([holder, holderItems]) => {
        const person = holder === "__none__" ? undefined : people.get(holder);
        return (
          <section className="teamWorkHolderGroup" key={holder}>
            <header>
              <div>
                {person
                  ? (
                    <button
                      type="button"
                      className="teamWorkHolderHeading"
                      onClick={event => onHolder(person, event.currentTarget)}
                    >
                      {person.displayName}
                    </button>
                  )
                  : <h2>No current holder</h2>}
              </div>
              <span>{holderItems.length}</span>
            </header>
            <div className="teamWorkHolderItems">
              {holderItems.map(item => (
                <TeamWorkCard key={`${holder}-${item.family}-${item.id}`} item={item} people={people} />
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

export default function TeamWork({ api, projectId, user }: {
  api: string;
  projectId: string;
  user: AuthUser;
}) {
  const [response, setResponse] = useState<TeamWorkResponse | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [build, setBuild] = useState("all");
  const [family, setFamily] = useState<TeamWorkFamily | "all">("all");
  const [group, setGroup] = useState<"lifecycle" | "holder">("lifecycle");
  const [selectedPerson, setSelectedPerson] = useState<string | null>(null);
  const [drawerHolder, setDrawerHolder] = useState<TeamWorkPerson | null>(null);
  const [affinityStore, setAffinityStore] = useState(readAffinityStore);
  const lastTrigger = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    setResponse(null);
    if (!projectId) {
      setLoading(false);
      setError("A project is required to open Team Work.");
      return () => controller.abort();
    }

    fetch(`${api}/api/team-work?projectId=${encodeURIComponent(projectId)}`, { signal: controller.signal })
      .then(async result => {
        if (!result.ok) {
          throw new Error(result.status === 403
            ? "You do not have access to this project’s Team Work."
            : "Team Work is unavailable.");
        }
        return validateProjection(await result.json());
      })
      .then(next => {
        if (!controller.signal.aborted) setResponse(next);
      })
      .catch(reason => {
        if (!controller.signal.aborted) {
          setError(reason instanceof Error ? reason.message : "Team Work is unavailable.");
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [api, projectId]);

  const people = useMemo(() => response?.people ?? [], [response]);
  const peopleByIdentity = useMemo(() => {
    const map = new Map<string, TeamWorkPerson>();
    for (const person of people) {
      map.set(person.userName.toLowerCase(), person);
      if (person.userId) map.set(person.userId.toLowerCase(), person);
    }
    return map;
  }, [people]);
  const memberPeople = useMemo(() => {
    const project = affinityStore.viewers[user.id]?.[projectId] ?? {};
    const viewer = people.find(person => personMatches(person, user.userName) || personMatches(person, user.id));
    const viewerAffinities = new Set(viewer?.disciplineAffinities ?? []);
    const hasAffinity = (person: TeamWorkPerson) => viewerAffinities.size > 0
      && person.disciplineAffinities.some(item => viewerAffinities.has(item));
    const topUsage = people
      .filter(person => person.isCurrentProjectMember && hasAffinity(person) && person.userId)
      .map(person => ({
        person,
        count: Number.isInteger(project[person.userId!.toLowerCase()])
          && project[person.userId!.toLowerCase()] > 0
          ? project[person.userId!.toLowerCase()]
          : 0,
      }))
      .filter(entry => entry.count > 0)
      .sort((a, b) => (b.count - a.count)
        || a.person.userName.localeCompare(b.person.userName, undefined, { sensitivity: "base" }))
      .slice(0, 3);
    const usageRank = (person: TeamWorkPerson) => topUsage.findIndex(entry =>
      entry.person.userId?.toLowerCase() === person.userId?.toLowerCase());

    return people.filter(person => person.isCurrentProjectMember).sort((a, b) => {
      const self = (person: TeamWorkPerson) =>
        personMatches(person, user.userName) || personMatches(person, user.id) ? 0 : 1;
      const affinity = (person: TeamWorkPerson) => hasAffinity(person) ? 0 : 1;
      const rank = (person: TeamWorkPerson) => {
        const value = usageRank(person);
        return value < 0 || !hasAffinity(person) ? 3 : value;
      };
      return self(a) - self(b)
        || affinity(a) - affinity(b)
        || rank(a) - rank(b)
        || `${a.displayName}\u0000${a.userName}`.localeCompare(
          `${b.displayName}\u0000${b.userName}`, undefined, { sensitivity: "base" });
    });
  }, [affinityStore, people, projectId, user.id, user.userName]);

  const buildOptions = useMemo(() => {
    if (!response) return [];
    return [...new Map(
      response.items.filter(item => item.release).map(item => [item.release!.id, item.release!]),
    ).values()].sort((a, b) => a.version.localeCompare(b.version, undefined, { numeric: true }));
  }, [response]);
  const searchMatch = useCallback((item: TeamWorkItem) => {
    const query = search.trim().toLowerCase();
    const holderFacts = item.currentHolderIds
      .map(id => `${id} ${displayNameFor(id, peopleByIdentity)}`).join(" ");
    const raisedByFacts = item.raisedById
      ? `${item.raisedById} ${displayNameFor(item.raisedById, peopleByIdentity)}`
      : "";
    const searchableFacts = `${item.title} ${item.number ?? ""} ${item.prefix ?? ""} ${item.category ?? ""}
      ${item.nativeState} ${item.nativeOutcome ?? ""} ${raisedByFacts} ${item.raisedByKind ?? ""}
      ${item.release?.version ?? ""} ${holderFacts}`;
    return !query || searchableFacts.toLowerCase().includes(query);
  }, [peopleByIdentity, search]);
  const filterItems = useCallback((includeFamily: boolean) => (response?.items ?? []).filter(item => {
    const matchesBuild = build === "all"
      || build === "deferred" && item.deferred
      || build !== "deferred" && item.release?.id === build;
    const matchesPerson = !selectedPerson
      || item.currentHolderIds.some(id => id.toLowerCase() === selectedPerson);
    const matchesFamily = !includeFamily || family === "all" || item.family === family;
    return searchMatch(item) && matchesBuild && matchesPerson && matchesFamily;
  }), [build, family, response, searchMatch, selectedPerson]);
  const facetItems = useMemo(() => filterItems(false), [filterItems]);
  const filteredItems = useMemo(() => filterItems(true), [filterItems]);

  const closeDrawer = useCallback(() => {
    if (!drawerHolder) return;
    setDrawerHolder(null);
    const url = new URL(window.location.href);
    if (url.searchParams.has("holder")) {
      history.pushState({}, "", urlWithoutHolder(url));
    }
    window.setTimeout(() => lastTrigger.current?.focus(), 0);
  }, [drawerHolder]);
  const selectPerson = useCallback((person: TeamWorkPerson, trigger?: HTMLElement, recordAffinity = true) => {
    setSelectedPerson(person.userName.toLowerCase());
    setDrawerHolder(person);
    lastTrigger.current = trigger ?? null;
    if (recordAffinity) {
      writeAffinity(user.id, projectId, person);
      setAffinityStore(readAffinityStore());
    }
    const url = new URL(window.location.href);
    if (url.searchParams.get("holder")?.toLowerCase() !== person.userName.toLowerCase()) {
      url.searchParams.set("holder", person.userName);
      history.pushState({}, "", `${url.pathname}${url.search}${url.hash}`);
    }
  }, [projectId, user.id]);

  useEffect(() => {
    if (!response) return;
    const syncUrl = () => {
      const holder = new URL(window.location.href).searchParams.get("holder");
      if (!holder) {
        setDrawerHolder(null);
        return;
      }
      const person = people.find(candidate => candidate.userName.toLowerCase() === holder.toLowerCase());
      if (!person) {
        const url = new URL(window.location.href);
        if (url.searchParams.has("holder")) {
          url.searchParams.delete("holder");
          history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
        }
        setDrawerHolder(null);
        setSelectedPerson(null);
        return;
      }
      setSelectedPerson(person.userName.toLowerCase());
      setDrawerHolder(person);
    };
    syncUrl();
    window.addEventListener("popstate", syncUrl);
    return () => window.removeEventListener("popstate", syncUrl);
  }, [response, people]);
  useEffect(() => {
    if (!drawerHolder || people.some(person =>
      person.userName.toLowerCase() === drawerHolder.userName.toLowerCase())) return;
    setDrawerHolder(null);
    setSelectedPerson(null);
    const url = new URL(window.location.href);
    if (url.searchParams.has("holder")) {
      url.searchParams.delete("holder");
      history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
    }
  }, [drawerHolder, people]);

  const clearFilters = () => {
    setSearch("");
    setBuild("all");
    setFamily("all");
    setSelectedPerson(null);
    if (drawerHolder) closeDrawer();
  };
  if (loading) {
    return (
      <main className="teamWorkPage" aria-busy="true" aria-label="Team Work loading">
        <div className="teamWorkPageHeader">
          <span className="teamWorkEyebrow">TEAM WORK</span>
          <h1>Team Work</h1>
        </div>
        <div className="teamWorkBoard teamWorkBoardLoading" aria-hidden="true">
          {lanes.map(lane => (
            <section className="teamWorkLane" key={lane.id}>
              <div className="teamWorkLoadingLine" />
              <div className="teamWorkLoadingCard" />
              <div className="teamWorkLoadingCard short" />
            </section>
          ))}
        </div>
      </main>
    );
  }
  if (error) {
    return (
      <main className="teamWorkPage" aria-label="Team Work">
        <div className="teamWorkPageHeader">
          <span className="teamWorkEyebrow">TEAM WORK</span>
          <h1>Team Work</h1>
        </div>
        <section className="teamWorkMessage teamWorkError" role="alert">
          <strong>Team Work could not be displayed</strong>
          <p>{error}</p>
        </section>
      </main>
    );
  }
  if (!response) return null;

  const projectEmpty = response.items.length === 0;
  const selected = selectedPerson
    ? people.find(person => person.userName.toLowerCase() === selectedPerson)
    : undefined;
  const noFilteredItems = filteredItems.length === 0;
  const selectedBuild = build === "deferred"
    ? "Deferred"
    : build === "all"
      ? ""
      : `Build ${buildOptions.find(option => option.id === build)?.version ?? build}`;
  const filterDescription = `${family !== "all" ? familyBadgeLabels[family] : "Controlled work"}${selectedBuild ? ` on ${selectedBuild}` : ""}`;

  return (
    <main className="teamWorkPage" aria-label="Team Work">
      <header className="teamWorkPageHeader">
        <div>
          <span className="teamWorkEyebrow">TEAM WORK</span>
          <h1>Team Work</h1>
          <p>Project scope · every build</p>
        </div>
        <dl className="teamWorkTotals">
          <div><dt>Unique items</dt><dd>{response.totals.items}</dd></div>
          <div><dt>People holding work</dt><dd>{people.filter(person => person.holds > 0).length}</dd></div>
          <div><dt>No current holder</dt><dd>{response.totals.unheld}</dd></div>
        </dl>
      </header>
      <PersonStrip
        people={memberPeople}
        selected={selectedPerson}
        search={search}
        viewer={user}
        onSelect={selectPerson}
      />
      {projectEmpty ? (
        <section className="teamWorkMessage">
          <strong>No controlled work is recorded in this project yet.</strong>
        </section>
      ) : (
        <>
          <section className="teamWorkFilters" aria-label="Team Work filters">
            <label className="teamWorkSearch">
              <span>Search</span>
              <input
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Search records or people"
              />
            </label>
            <div className="teamWorkFilterRow">
              <div className="teamWorkFilterGroup" role="group" aria-label="Group by">
                <span>Group by</span>
                <button type="button" className={group === "lifecycle" ? "active" : ""} onClick={() => setGroup("lifecycle")}>
                  Lifecycle stage
                </button>
                <button type="button" className={group === "holder" ? "active" : ""} onClick={() => setGroup("holder")}>
                  Current holder
                </button>
              </div>
              <div className="teamWorkFilterGroup" role="group" aria-label="Build">
                <span>Build</span>
                <button type="button" className={build === "all" ? "active" : ""} onClick={() => setBuild("all")}>All</button>
                {buildOptions.map(option => (
                  <button type="button" className={build === option.id ? "active" : ""} key={option.id} onClick={() => setBuild(option.id)}>
                    Build {option.version}
                  </button>
                ))}
                {response.items.some(item => item.deferred) && (
                  <button type="button" className={build === "deferred" ? "active" : ""} onClick={() => setBuild("deferred")}>
                    Deferred
                  </button>
                )}
              </div>
              <div className="teamWorkFilterGroup" role="group" aria-label="Record type">
                <span>Type</span>
                <button type="button" className={family === "all" ? "active" : ""} onClick={() => setFamily("all")}>
                  All ({facetItems.length})
                </button>
                {[...familyIds].map(id => (
                  <button type="button" key={id} className={family === id ? "active" : ""} onClick={() => setFamily(id)}>
                    {familyBadgeLabels[id]} ({facetItems.filter(item => item.family === id).length})
                  </button>
                ))}
              </div>
            </div>
          </section>
          {selected && selected.holds === 0 ? (
            <section className="teamWorkMessage">
              <strong>Nothing currently requires {selected.displayName}.</strong>
            </section>
          ) : noFilteredItems ? (
            <section className="teamWorkMessage teamWorkFilteredEmpty">
              <strong>
                {family !== "all" || selectedBuild
                  ? <>No {filterDescription} — <button type="button" onClick={clearFilters}>Clear filters</button></>
                  : <>No controlled work matches these filters — <button type="button" onClick={clearFilters}>Clear filters</button></>}
              </strong>
              <p>
                Search, person, build, and record-type filters compose against the authorized project result.
              </p>
            </section>
          ) : (
            <TeamWorkBoard
              items={filteredItems}
              group={group}
              people={peopleByIdentity}
              onHolder={(person, trigger) => selectPerson(person, trigger, false)}
            />
          )}
        </>
      )}
      {drawerHolder && createPortal(
        <TeamWorkDrawer
          person={drawerHolder}
          items={response.items}
          people={peopleByIdentity}
          onClose={closeDrawer}
        />,
        document.body,
      )}
    </main>
  );
}
