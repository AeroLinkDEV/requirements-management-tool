import { createRoot } from "react-dom/client"
import "../../src/index.css"
import ChangeRequestWorkspace from "../../src/ChangeRequestWorkspace"

/**
 * #1016 S02. The configured review-setup panel, mounted against server-shaped payloads.
 *
 * This is the real `ChangeRequestWorkspace`, not a copy of its markup: what renders here is what renders in
 * the application, for the payload shapes the endpoints actually return. What it is *not* is the application's
 * routing and backend — a test here proves presentation and the outgoing command, and nothing about the route
 * that reaches this screen or the server's answer to what it sends.
 *
 * The payloads are kept faithful to the contract rather than merely plausible. In particular
 * `/api/review-workflows/applicable` reports a candidate's `userId` as the account's **UserName**
 * (`WorkflowEndpoints.cs:236-243`, projecting `StageCandidate.UserId` from `accountById[...].UserName`), and
 * `/submit` resolves an approver by that same username (`ChangeRequestEndpoints.cs:1086`). So the candidate
 * identities below are canonical usernames, not GUIDs. Stages also carry `authorityKind`, `isLegacy`,
 * `requiredAuthority` and the server's own `authorityLabel`, and candidates carry `via`; they are included so a
 * test that claims to tell a base-role stage from a leadership-position stage is looking at a payload that
 * actually distinguishes them.
 *
 * Scenarios are selected with `?scenario=`; each one is a different configured policy, not a different
 * component.
 */

const CHANGE_REQUEST_ID = "00000000-0000-0000-0000-0000000000c1"
const PROJECT_ID = "00000000-0000-0000-0000-0000000000b1"
const RELEASE_ID = "00000000-0000-0000-0000-0000000000b2"

const completeImpacts = JSON.stringify({
  trace: "Not Affected", verification: "Not Affected", documents: "Not Affected",
  baseline: "Not Affected", collaboration: "Not Affected",
})

const changeRequest = {
  id: CHANGE_REQUEST_ID, baseNumber: "SRCR-00143", revision: 0, displayNumber: "SRCR-00143.00",
  projectId: PROJECT_ID, targetReleaseId: RELEASE_ID, type: "System",
  title: "Preserve configured review authority in the row that requires it",
  problem: "The row hid what it was for.", analysis: "Stage and purpose lived in the accessible name.",
  solution: "Show them, and keep showing them.",
  problemRich: "", analysisRich: "", solutionRich: "",
  authorId: "systems.author", version: 3, state: "Draft",
  createdAt: "2026-09-11T09:00:00Z", updatedAt: "2026-09-11T09:00:00Z",
  requirementChanges: [{
    id: "00000000-0000-0000-0000-0000000000a1", baseNumber: "SYSR-000150", revision: 2,
    displayNumber: "SYSR-000150.02", level: "System", kind: "Modify",
    statement: "The FMS shall present configured review authority readably.",
    rationale: "A signer cannot confirm authority they cannot read.",
    verificationMethod: "Inspection", richText: "", attributesJson: "{}",
    impactDispositionJson: completeImpacts, upstreamRevisionIds: [],
  }],
  reviewCycles: [], audit: [], upstream: [], upstreamHistory: [],
}

// A base-role stage and a leadership-position stage: the two kinds a stage can require, and the two names from
// the observation. Keeping both proves the panel does not flatten one into the other. Note that for a
// leadership stage the server reports the candidate's `role` as the accountable *position*, not a held
// ProgramRole — that is the endpoint's own rule, reproduced here rather than smoothed over.
const reviewStage = {
  position: 0, name: "Systems review", kind: "Review",
  requiredRole: "SystemEngineer", authorityKind: "BaseRole", isLegacy: false,
  requiredAuthority: { kind: "BaseRole", role: "SystemEngineer" },
  authorityLabel: "System Engineer",
  candidates: [
    { userId: "dana.systems", name: "Dana Systems", role: "SystemEngineer", via: "ProgramMembership" },
    { userId: "ravi.test", name: "Ravi Test", role: "SystemTestEngineer", via: "ProgramMembership" },
  ],
}

const approvalStage = {
  position: 1, name: "Systems engineering independent airworthiness and certification approval",
  kind: "Approval", requiredRole: "SystemEngineeringLead", authorityKind: "LeadershipPosition", isLegacy: false,
  requiredAuthority: { kind: "LeadershipPosition", position: "SystemEngineeringLead" },
  authorityLabel: "Project Leadership · System Engineering Lead",
  candidates: [
    { userId: "mira.lead", name: "Mira Lead", role: "SystemEngineeringLead", via: "LeadershipAssignment" },
  ],
}

// A configured stage the Program cannot currently staff: the authority is real and required, and nobody
// active holds it. The endpoint returns the stage with an empty candidate list rather than dropping it,
// because a stage that vanishes would let a submission look complete while an authority went unsigned.
const unstaffedApprovalStage = { ...approvalStage, candidates: [] }

const scenarioName = new URLSearchParams(window.location.search).get("scenario") ?? "sequential"

const workflows: Record<string, Record<string, unknown>> = {
  sequential: {
    required: true, minimum: 2, allowsAdditional: true,
    name: "System change review", version: 4, mode: "Sequential",
    stages: [reviewStage, approvalStage],
  },
  unstaffed: {
    required: true, minimum: 2, allowsAdditional: true,
    name: "System change review", version: 4, mode: "Sequential",
    stages: [reviewStage, unstaffedApprovalStage],
  },
  parallel: {
    required: true, minimum: 2, allowsAdditional: true,
    name: "System concurrent review", version: 2, mode: "Parallel",
    stages: [reviewStage, approvalStage],
  },
}

const applicableWorkflow = workflows[scenarioName] ?? workflows.sequential

type RecordedCall = { method: string; url: string; body: unknown }

declare global {
  interface Window {
    __apiCalls: RecordedCall[]
    __unexpectedMutations: RecordedCall[]
  }
}

window.__apiCalls = []
window.__unexpectedMutations = []

const json = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), {
    status, headers: { "Content-Type": "application/json" },
  }))

const nativeFetch = window.fetch.bind(window)

window.fetch = async (input, init) => {
  const request = typeof input === "string" || input instanceof URL
    ? { url: String(input), method: (init?.method ?? "GET").toUpperCase(), body: init?.body }
    : { url: (input as Request).url, method: (input as Request).method.toUpperCase(), body: init?.body }
  const path = new URL(request.url, window.location.origin).pathname
  const parsed = typeof request.body === "string" ? JSON.parse(request.body) : undefined

  if (request.method !== "GET") {
    window.__apiCalls.push({ method: request.method, url: path, body: parsed })
    // Exact path and method. The earlier version of this fixture matched the change-request URL by substring
    // and would have answered a POST .../submit with the GET detail body, which is how a test can "pass" a
    // submission that never carried the right command.
    if (path === `/api/change-requests/${CHANGE_REQUEST_ID}/submit`) {
      return json({ ...changeRequest, state: "InReview", version: changeRequest.version + 1 })
    }
    // Anything else that would change state is refused here rather than escaping to a real backend. A test
    // that provokes an unexpected write should fail on the record, not quietly mutate something.
    window.__unexpectedMutations.push({ method: request.method, url: path, body: parsed })
    return json({ error: `Unexpected ${request.method} ${path} from a presentation fixture.` }, 500)
  }

  if (path === `/api/change-requests/${CHANGE_REQUEST_ID}/upstream-candidates`) {
    // Top of the ladder: the trace answer is derived, not authored, so review readiness does not wait on it.
    return json({ isTopOfLadder: true, upstreamAnswerComplete: true, candidates: [], derivedEdges: [] })
  }
  if (path === `/api/change-requests/${CHANGE_REQUEST_ID}`) return json(changeRequest)
  if (path === "/api/review-workflows/applicable") return json(applicableWorkflow)
  if (path.startsWith("/api/problem-reports/linked/")) return json([])
  if (path === "/api/controlled-editing/status") return json({ sessions: [] })
  if (path === "/api/authoring/context") {
    return json({ sections: [], levels: ["System"], verificationMethods: ["Inspection", "Test"] })
  }
  if (path.startsWith("/api/projects/")) return json({ methods: ["Inspection", "Test"] })
  return nativeFetch(input, init)
}

createRoot(document.getElementById("root")!).render(
  <ChangeRequestWorkspace
    api=""
    changeRequestId={CHANGE_REQUEST_ID}
    user={{
      id: "systems.author", userName: "systems.author", displayName: "Sam Author", email: "",
      isAdministrator: false, mustChangePassword: false, programs: [],
    }}
    onBack={() => {}}
    onChanged={() => {}}
    onOpenScr={() => {}}
    onDisciplineResolved={() => {}}
    releases={[{ id: RELEASE_ID, version: "1.6", isReleased: false }]}
  />,
)
