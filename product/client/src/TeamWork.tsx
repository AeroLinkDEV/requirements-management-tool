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
  family: string;
  category?: string | null;
  prefix?: string | null;
  number?: string | null;
  title: string;
  lane: string;
  nativeState: string;
  nativeOutcome?: string | null;
  currentHolderIds: string[];
  holderBasis: string;
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

const lanes: ReadonlyArray<{ id: LaneId; title: string; description: string }> = [
  { id: "work", title: "In work", description: "Active authoring and assigned work" },
  { id: "review", title: "In review", description: "Review obligations in progress" },
  { id: "sign", title: "Awaiting signature", description: "Approval obligations in progress" },
  { id: "approved", title: "Approved", description: "Approved controlled work" },
];

const laneIds = new Set<string>(lanes.map(lane => lane.id));
const familyIds = new Set(["system", "software", "interface", "verification", "problemReport", "assessment"]);
const holderBasisIds = new Set([
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

const isSafeCanonicalOpenUrl = (value: unknown): value is string =>
  typeof value === "string"
  && /^\/open\/(?:change-request|test-change-request|problem-report|downstream-assessment)\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);

function validateProjection(value: unknown): TeamWorkResponse {
  if (!value || typeof value !== "object") throw new Error("The Team Work response is not an object.");
  const response = value as Partial<TeamWorkResponse>;
  if (!Array.isArray(response.items) || !Array.isArray(response.people))
    throw new Error("The Team Work response is missing its people or item collections.");
  if (!response.totals || !Number.isInteger(response.totals.items) || response.totals.items < 0
    || !Number.isInteger(response.totals.returned) || response.totals.returned < 0
    || !Number.isInteger(response.totals.unheld) || response.totals.unheld < 0
    || response.totals.returned !== response.items.length)
    throw new Error("The Team Work response has invalid totals.");
  const badItem = response.items.find(item =>
    !item || typeof item !== "object"
    || !familyIds.has(item.family)
    || !laneIds.has(item.lane)
    || !holderBasisIds.has(item.holderBasis)
    || !isSafeCanonicalOpenUrl(item.openUrl)
    || !Array.isArray(item.currentHolderIds)
    || item.currentHolderIds.some(holderId => typeof holderId !== "string" || !holderId.trim())
    || typeof item.updatedAt !== "string" || Number.isNaN(Date.parse(item.updatedAt)));
  if (badItem) throw new Error("A Team Work item has an unknown family, lane, holder basis, invalid timestamp, or unsafe/missing canonical open link.");
  return response as TeamWorkResponse;
}

const displayNameFor = (id: string, people: Map<string, string>) => people.get(id.toLowerCase()) ?? "Holder identity unavailable";
const laneFor = (id: string) => lanes.find(lane => lane.id === id);

function TeamWorkCard({ item, people }: { item: TeamWorkItem; people: Map<string, string> }) {
  const holders = item.currentHolderIds.map(id => displayNameFor(id, people));
  const badge = item.family === "assessment" ? "Assessment" : item.prefix || item.category || "Controlled record";
  const identity = item.number || item.category || "Assessment";
  const raisedBy = item.raisedById && item.raisedByKind
    ? originLabels[item.raisedByKind]
      ? `Origin: ${originLabels[item.raisedByKind]}`
      : item.raisedByKind === "author"
        ? displayNameFor(item.raisedById, people)
        : item.raisedByKind === "reportedBy"
          ? displayNameFor(item.raisedById, people)
        : undefined
    : undefined;
  const basis = holderBasisLabels[item.holderBasis] ?? "Holder basis unavailable";
  const lane = laneFor(item.lane);

  return (
    <a className="teamWorkCard" data-team-work-card="true" href={item.openUrl}>
      <div className="teamWorkCardTopline">
        <span className={`teamWorkCardBadge family-${item.family}`} data-family={item.family}>{badge}</span>
        {item.deferred && <span className="teamWorkCardDeferred">Deferred</span>}
      </div>
      <div className="teamWorkCardIdentity"><strong>{identity}</strong>{item.category && item.number && <span>{item.category}</span>}</div>
      <h3>{item.title}</h3>
      <div className={`teamWorkLanePill lane-${item.lane}`}><i aria-hidden="true"/>{lane?.title ?? "Unknown lane"}<span aria-hidden="true">→</span></div>
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
        if (reason instanceof Error && reason.name === "AbortError") return;
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
