import { useEffect, useMemo, useState } from "react";
import { stateLabel } from "./presentation";
import "./TeamWork.css";

type TeamWorkRelease = {
  id: string;
  version: string;
  isReleased: boolean;
};

type TeamWorkAllocation = {
  baselineId: string;
  releaseId: string;
  releaseVersion: string;
  baselineNumber: string;
  baselineRevision: number;
  isReleased: boolean;
};

type TeamWorkPerson = {
  userName: string;
  displayName: string;
  holds: number;
  byLane: { work: number; review: number; sign: number; approved: number };
};

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
type TeamWorkFamily = "system" | "software" | "interface" | "verification" | "problemReport" | "assessment";
type TeamWorkHolderBasis =
  | "none" | "author" | "assignedEngineer" | "responsibleEngineer" | "activeReviewStage"
  | "activeApprovalStage" | "activeReviewAndApprovalStages" | "selectedAssessmentApprover";

const lanes: ReadonlyArray<{ id: LaneId; title: string; description: string }> = [
  { id: "work", title: "In work", description: "Active authoring and assigned work" },
  { id: "review", title: "In review", description: "Review obligations in progress" },
  { id: "sign", title: "Awaiting signature", description: "Approval obligations in progress" },
  { id: "approved", title: "Approved", description: "Approved controlled work" },
];

const laneIds = new Set<LaneId>(lanes.map(lane => lane.id));
const familyIds = new Set<TeamWorkFamily>(["system", "software", "interface", "verification", "problemReport", "assessment"]);
const holderBasisIds = new Set<TeamWorkHolderBasis>([
  "none", "author", "assignedEngineer", "responsibleEngineer", "activeReviewStage", "activeApprovalStage",
  "activeReviewAndApprovalStages", "selectedAssessmentApprover",
]);

const holderBasisLabels: Record<string, string> = {
  none: "No active holder obligation",
  author: "Author action",
  assignedEngineer: "Assigned engineer obligation",
  responsibleEngineer: "Responsible engineer obligation",
  activeReviewStage: "Active review obligation",
  activeApprovalStage: "Active approval obligation",
  activeReviewAndApprovalStages: "Active review and approval obligations",
  selectedAssessmentApprover: "Selected assessment approver obligation",
};

const originLabels: Record<string, string> = {
  changeRequest: "source change request",
  problemReport: "source problem report",
  caseChange: "source case change",
  caseAssessment: "source case assessment",
  caseReview: "source case review",
};

const guidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const guidExpression = new RegExp(`^${guidPattern}$`, "i");
const canonicalOpenUrlExpression = new RegExp(
  `^/open/(change-request|test-change-request|problem-report|downstream-assessment)/(${guidPattern})$`,
  "i",
);
const canonicalOpenKindByFamily: Record<TeamWorkFamily, string> = {
  system: "change-request",
  software: "change-request",
  interface: "change-request",
  verification: "test-change-request",
  problemReport: "problem-report",
  assessment: "downstream-assessment",
};

const isSafeCanonicalOpenUrl = (value: unknown): value is string =>
  typeof value === "string"
  && canonicalOpenUrlExpression.test(value);

const isRecord = (value: unknown): value is Record<string, unknown> =>
  !!value && typeof value === "object" && !Array.isArray(value);

const isNonBlankString = (value: unknown): value is string =>
  typeof value === "string" && value.trim().length > 0;

const isOptionalString = (value: unknown): value is string | null | undefined =>
  value === null || value === undefined || isNonBlankString(value);

const isNonNegativeInteger = (value: unknown): value is number =>
  Number.isInteger(value) && (value as number) >= 0;

const isLane = (value: unknown): value is LaneId =>
  isNonBlankString(value) && laneIds.has(value as LaneId);

const isFamily = (value: unknown): value is TeamWorkFamily =>
  isNonBlankString(value) && familyIds.has(value as TeamWorkFamily);

const isHolderBasis = (value: unknown): value is TeamWorkHolderBasis =>
  isNonBlankString(value) && holderBasisIds.has(value as TeamWorkHolderBasis);

const isValidRelease = (value: unknown): value is TeamWorkRelease =>
  isRecord(value)
  && isNonBlankString(value.id) && guidExpression.test(value.id)
  && isNonBlankString(value.version)
  && typeof value.isReleased === "boolean";

const isValidAllocation = (value: unknown): value is TeamWorkAllocation =>
  isRecord(value)
  && isNonBlankString(value.baselineId) && guidExpression.test(value.baselineId)
  && isNonBlankString(value.releaseId) && guidExpression.test(value.releaseId)
  && isNonBlankString(value.releaseVersion)
  && isNonBlankString(value.baselineNumber)
  && isNonNegativeInteger(value.baselineRevision)
  && typeof value.isReleased === "boolean";

const laneKeys = ["work", "review", "sign", "approved"] as const;

const isValidPerson = (value: unknown): value is TeamWorkPerson => {
  if (!isRecord(value) || !isNonBlankString(value.userName) || !isNonBlankString(value.displayName)
    || !isNonNegativeInteger(value.holds) || !isRecord(value.byLane)) return false;
  const byLane = value.byLane;
  if (!laneKeys.every(lane => isNonNegativeInteger(byLane[lane]))) return false;
  const laneTotal = laneKeys.reduce((total, lane) => total + (byLane[lane] as number), 0);
  return value.holds === laneTotal;
};

const isValidItem = (value: unknown, people: Map<string, TeamWorkPerson>): value is TeamWorkItem => {
  if (!isRecord(value)) return false;
  const { family, lane, holderBasis, currentHolderIds, updatedAt } = value;
  if (!isNonBlankString(value.id) || !guidExpression.test(value.id) || !isFamily(family)
    || !isLane(lane) || !isHolderBasis(holderBasis)
    || !isNonBlankString(value.title) || !isNonBlankString(value.nativeState)
    || !isSafeCanonicalOpenUrl(value.openUrl) || !Array.isArray(currentHolderIds)
    || !currentHolderIds.every(isNonBlankString)
    || !isOptionalString(value.category) || !isOptionalString(value.prefix) || !isOptionalString(value.number)
    || !isOptionalString(value.nativeOutcome) || typeof value.deferred !== "boolean"
    || !isOptionalString(value.deferredFromState) || !isNonBlankString(updatedAt)
    || Number.isNaN(Date.parse(updatedAt))) return false;

  // Controlled families retain their governed number and prefix. Problem reports may not have a category
  // while still in Draft, but an identity without both a number and a prefix is never honest to display.
  if (family === "assessment") {
    if (!isNonBlankString(value.category) || (value.number !== null && value.number !== undefined)
      || (value.prefix !== null && value.prefix !== undefined)) return false;
  } else if (!isNonBlankString(value.number) || !isNonBlankString(value.prefix)
    || (value.family !== "problemReport" && !isNonBlankString(value.category))) {
    return false;
  }

  if (value.release !== null && value.release !== undefined && !isValidRelease(value.release)) return false;
  if (value.allocation !== null && value.allocation !== undefined && !isValidAllocation(value.allocation)) return false;
  if (!isOptionalString(value.raisedById) || !isOptionalString(value.raisedByKind)) return false;
  const raisedByKind = value.raisedByKind;
  const raisedById = value.raisedById;
  if (raisedByKind === null || raisedByKind === undefined) {
    if (raisedById !== null && raisedById !== undefined) return false;
  } else if (!originLabels[raisedByKind] && raisedByKind !== "author" && raisedByKind !== "reportedBy") {
    return false;
  } else if ((raisedByKind === "author" || raisedByKind === "reportedBy") && !isNonBlankString(raisedById)) {
    return false;
  }
  const distinctHolders = new Set<string>();
  if (currentHolderIds.some(holderId => !distinctHolders.add(holderId.toLowerCase()))) return false;
  // An authoritative obligation can be unassigned. Its non-none basis still explains the missing named
  // holder; only a `none` basis paired with holder IDs is contradictory.
  if (holderBasis === "none" && currentHolderIds.length !== 0) return false;
  const canonicalOpenUrlMatch = value.openUrl.match(canonicalOpenUrlExpression);
  if (!canonicalOpenUrlMatch) return false;
  return canonicalOpenUrlMatch[2].toLowerCase() === value.id.toLowerCase()
    && canonicalOpenUrlMatch[1].toLowerCase() === canonicalOpenKindByFamily[family]
    && currentHolderIds.every(holderId => people.has(holderId.toLowerCase()));
};

function validateProjection(value: unknown): TeamWorkResponse {
  if (!isRecord(value)) throw new Error("The Team Work response is not an object.");
  if (!isNonBlankString(value.generatedAt) || Number.isNaN(Date.parse(value.generatedAt)))
    throw new Error("The Team Work response has an invalid generated timestamp.");
  if (!Array.isArray(value.items) || !Array.isArray(value.people))
    throw new Error("The Team Work response is missing its people or item collections.");
  if (!isRecord(value.totals) || !isNonNegativeInteger(value.totals.items)
    || !isNonNegativeInteger(value.totals.returned) || !isNonNegativeInteger(value.totals.unheld)
    || value.totals.returned !== value.items.length || value.totals.items < value.totals.returned
    || value.totals.unheld > value.totals.items)
    throw new Error("The Team Work response has invalid totals.");

  const people = value.people as unknown[];
  const validatedPeople = new Map<string, TeamWorkPerson>();
  if (people.some(person => !isValidPerson(person)))
    throw new Error("A Team Work person has an invalid identity, hold count, or lane counts.");
  for (const person of people as TeamWorkPerson[]) {
    const key = person.userName.toLowerCase();
    if (validatedPeople.has(key)) throw new Error("The Team Work response contains duplicate people.");
    validatedPeople.set(key, person);
  }

  const items = value.items as unknown[];
  if (items.some(item => !isValidItem(item, validatedPeople)))
    throw new Error("A Team Work item has an unknown family, lane, holder basis, invalid identity, malformed holder, timestamp, release, or unsafe/missing canonical open link.");
  const validatedItems = items as TeamWorkItem[];
  if (value.totals.items === value.totals.returned) {
    const unheld = validatedItems.filter(item => item.currentHolderIds.length === 0).length;
    if (value.totals.unheld !== unheld)
      throw new Error("The Team Work response has invalid unheld totals.");
    const observed = new Map<string, { holds: number; byLane: Record<LaneId, number> }>();
    for (const item of validatedItems) for (const holderId of item.currentHolderIds) {
      const key = holderId.toLowerCase();
      const current = observed.get(key) ?? { holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } };
      current.holds += 1;
      current.byLane[item.lane] += 1;
      observed.set(key, current);
    }
    for (const person of validatedPeople.values()) {
      const actual = observed.get(person.userName.toLowerCase()) ?? { holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } };
      if (person.holds !== actual.holds || laneKeys.some(lane => person.byLane[lane] !== actual.byLane[lane]))
        throw new Error("The Team Work response has inconsistent person hold totals.");
    }
  }
  return {
    generatedAt: value.generatedAt,
    totals: { items: value.totals.items, returned: value.totals.returned, unheld: value.totals.unheld },
    people: validatedPeople.size ? [...validatedPeople.values()] : [],
    items: validatedItems,
  };
}

const holderDisplayNameFor = (id: string, people: Map<string, string>) => people.get(id.toLowerCase())!;
const provenanceIdentityFor = (id: string, people: Map<string, string>) => people.get(id.toLowerCase()) ?? id;
const laneFor = (id: string) => lanes.find(lane => lane.id === id);

function TeamWorkCard({ item, people }: { item: TeamWorkItem; people: Map<string, string> }) {
  const holders = item.currentHolderIds.map(id => holderDisplayNameFor(id, people));
  const badge = item.family === "assessment" ? "Assessment" : item.prefix!;
  const identity = item.family === "assessment" ? item.category! : item.number!;
  const raisedBy = item.raisedByKind
    ? originLabels[item.raisedByKind]
      ? `Origin: ${originLabels[item.raisedByKind]}`
      : item.raisedById
        ? provenanceIdentityFor(item.raisedById, people)
        : undefined
    : undefined;
  const basis = holderBasisLabels[item.holderBasis];
  const lane = laneFor(item.lane);

  return (
    <a className="teamWorkCard" data-team-work-card="true" href={item.openUrl}>
      <div className="teamWorkCardTopline">
        <span className={`teamWorkCardBadge family-${item.family}`} data-family={item.family}>{badge}</span>
        {item.deferred && <span className="teamWorkCardDeferred">Deferred</span>}
      </div>
      <div className="teamWorkCardIdentity"><strong>{identity}</strong>{item.category && item.number && <span>{item.category}</span>}</div>
      <h3>{item.title}</h3>
      <div className={`teamWorkLanePill lane-${item.lane}`}><i aria-hidden="true"/>{lane!.title}<span aria-hidden="true">→</span></div>
      <dl className="teamWorkCardFacts">
        <div><dt>Native state</dt><dd>{stateLabel(item.nativeState)}{item.nativeOutcome ? ` · ${stateLabel(item.nativeOutcome)}` : ""}</dd></div>
        <div><dt>Current holder{holders.length === 1 ? "" : "s"}</dt><dd>{holders.length ? holders.join(", ") : "No current holder"}</dd></div>
        <div><dt>Basis</dt><dd>{basis}</dd></div>
        {raisedBy && <div><dt>{raisedBy.startsWith("Origin:") ? "Origin" : "Raised by"}</dt><dd>{raisedBy.replace(/^Origin: /, "")}</dd></div>}
        <div><dt>Release</dt><dd>{item.release ? `Build ${item.release.version}` : "Release not recorded"}</dd></div>
        {item.allocation && <div><dt>Allocation</dt><dd>Build {item.allocation.releaseVersion}{item.allocation.isReleased ? " · released" : " · in work"}</dd></div>}
        {item.deferredFromState && <div><dt>Deferred from</dt><dd>{stateLabel(item.deferredFromState)}</dd></div>}
        <div><dt>Last updated</dt><dd>{new Date(item.updatedAt).toLocaleString()}</dd></div>
      </dl>
    </a>
  );
}

function TeamWorkBoard({ response }: { response: TeamWorkResponse }) {
  const people = useMemo(() => new Map(response.people.map(person => [person.userName.toLowerCase(), person.displayName])), [response.people]);
  return (
    <div className="teamWorkBoard" data-team-work-board="true">
      {lanes.map(lane => {
        const items = response.items.filter(item => item.lane === lane.id);
        return (
          <section className="teamWorkLane" data-lane={lane.id} aria-labelledby={`team-work-lane-${lane.id}`} key={lane.id}>
            <header>
              <div><h2 id={`team-work-lane-${lane.id}`}>{lane.title}</h2><p>{lane.description}</p></div>
              <span className="teamWorkLaneCount" aria-label={`${items.length} items`}>{items.length}</span>
            </header>
            <div className="teamWorkLaneItems">
              {items.length ? items.map(item => <TeamWorkCard key={`${item.family}-${item.id}`} item={item} people={people} />) : <p className="teamWorkLaneEmpty">No items in this lane.</p>}
            </div>
          </section>
        );
      })}
    </div>
  );
}

export default function TeamWork({ api, projectId }: { api: string; projectId: string }) {
  const [response, setResponse] = useState<TeamWorkResponse | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);

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
        if (!result.ok) throw new Error(result.status === 403 ? "You do not have access to this project’s Team Work." : "Team Work is unavailable.");
        return validateProjection(await result.json());
      })
      .then(next => { if (!controller.signal.aborted) setResponse(next); })
      .catch(reason => {
        if (controller.signal.aborted) return;
        setError(reason instanceof Error ? reason.message : "Team Work is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [api, projectId]);

  if (loading) return <main className="teamWorkPage" aria-busy="true" aria-label="Team Work loading"><div className="teamWorkPageHeader"><span className="teamWorkEyebrow">TEAM WORK</span><h1>Team Work</h1></div><div className="teamWorkBoard teamWorkBoardLoading" aria-hidden="true">{lanes.map(lane => <section className="teamWorkLane" key={lane.id}><div className="teamWorkLoadingLine"/><div className="teamWorkLoadingCard"/><div className="teamWorkLoadingCard short"/></section>)}</div></main>;
  if (error) return <main className="teamWorkPage" aria-label="Team Work"><div className="teamWorkPageHeader"><span className="teamWorkEyebrow">TEAM WORK</span><h1>Team Work</h1></div><section className="teamWorkMessage teamWorkError" role="alert"><strong>Team Work could not be displayed</strong><p>{error}</p></section></main>;
  if (!response || response.items.length === 0) return <main className="teamWorkPage" aria-label="Team Work"><div className="teamWorkPageHeader"><span className="teamWorkEyebrow">TEAM WORK</span><h1>Team Work</h1><p>Project-wide lifecycle view across every build.</p></div><section className="teamWorkMessage"><strong>No controlled work is recorded in this project yet.</strong></section></main>;
  return <main className="teamWorkPage" aria-label="Team Work"><header className="teamWorkPageHeader"><div><span className="teamWorkEyebrow">TEAM WORK</span><h1>Team Work</h1><p>Project-wide lifecycle view across every build.</p></div><dl className="teamWorkTotals"><div><dt>Unique items</dt><dd>{response.totals.items}</dd></div><div><dt>People holding work</dt><dd>{response.people.filter(person => person.holds > 0).length}</dd></div><div><dt>No current holder</dt><dd>{response.totals.unheld}</dd></div></dl></header><TeamWorkBoard response={response}/></main>;
}
