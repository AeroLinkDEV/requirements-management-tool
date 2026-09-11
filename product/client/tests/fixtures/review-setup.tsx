import { createRoot } from "react-dom/client"
import "../../src/index.css"
import ChangeRequestWorkspace from "../../src/ChangeRequestWorkspace"

/**
 * #1016 S02. The configured review-setup panel, mounted against server-shaped payloads.
 *
 * The integrated journey reaches this panel only by clicking in from My Work — the route does not rehydrate
 * from a cold address — and standing a whole seeded Program up to assert four sentences of presentation is a
 * poor trade. These payloads are the shapes the endpoints actually return, so what renders here is what the
 * component renders in the application.
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

// A base role and a leadership position: the two kinds a stage can require, and the two names from the
// observation. Keeping both proves the panel does not flatten one into the other.
const applicableWorkflow = {
  required: true, minimum: 2, allowsAdditional: true,
  name: "System change review", version: 4, mode: "Sequential",
  stages: [
    {
      position: 0, name: "Systems review", kind: "Review", requiredRole: "SystemEngineer",
      candidates: [
        { userId: "00000000-0000-0000-0000-0000000000u1", name: "Dana Systems", role: "SystemEngineer" },
        { userId: "00000000-0000-0000-0000-0000000000u2", name: "Ravi Test", role: "SystemTestEngineer" },
      ],
    },
    {
      position: 1, name: "Systems engineering independent airworthiness and certification approval",
      kind: "Approval", requiredRole: "SystemEngineeringLead",
      candidates: [{ userId: "00000000-0000-0000-0000-0000000000u3", name: "Mira Lead", role: "SystemEngineeringLead" }],
    },
  ],
}

const json = (body: unknown) =>
  Promise.resolve(new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } }))

const nativeFetch = window.fetch.bind(window)
window.fetch = (input, init) => {
  const url = String(typeof input === "string" ? input : (input as Request).url)
  if (url.includes(`/api/change-requests/${CHANGE_REQUEST_ID}/upstream-candidates`)) {
    // Top of the ladder: the trace answer is derived, not authored, so review readiness does not wait on it.
    return json({ isTopOfLadder: true, upstreamAnswerComplete: true, candidates: [], derivedEdges: [] })
  }
  if (url.includes(`/api/change-requests/${CHANGE_REQUEST_ID}`)) return json(changeRequest)
  if (url.includes("/api/review-workflows/applicable")) return json(applicableWorkflow)
  if (url.includes("/api/problem-reports/linked/")) return json([])
  if (url.includes("/api/controlled-editing/status")) return json({ sessions: [] })
  if (url.includes("/api/authoring/context")) {
    return json({ sections: [], levels: ["System"], verificationMethods: ["Inspection", "Test"] })
  }
  if (url.includes("/api/projects/")) return json({ methods: ["Inspection", "Test"] })
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
