import { createRoot } from "react-dom/client"
import "../../src/index.css"
import TeamWork from "../../src/TeamWork"

const lanes = ["work", "review", "sign", "approved"] as const
const items = lanes.flatMap((lane, laneIndex) => Array.from({ length: 8 }, (_, index) => ({
  id: `00000000-0000-0000-0000-${String(laneIndex * 8 + index + 1).padStart(12, "0")}`,
  family: "system", layer: "System", artifactType: "SRCR", category: "System", prefix: "SRCR",
  number: `SRCR-${String(laneIndex * 8 + index + 1).padStart(5, "0")}`, title: "Clarify route selection and failure reporting",
  lane, nativeState: lane === "approved" ? "Approved" : lane === "work" ? "Draft" : "InReview",
  currentHolderIds: lane === "approved" ? [] : ["engineer"],
  holderBasis: lane === "approved" ? "none" : lane === "work" ? "author" : lane === "review" ? "activeReviewStage" : "activeApprovalStage",
  activeStageObligations: lane === "review" || lane === "sign" ? [{ holderId: "engineer", stageKind: lane === "review" ? "review" : "approval" }] : [],
  release: { id: "00000000-0000-0000-0000-000000000099", version: "1.6", isReleased: false },
  deferred: false, updatedAt: "2026-09-10T12:00:00Z", openUrl: `/open/change-request/00000000-0000-0000-0000-${String(laneIndex * 8 + index + 1).padStart(12, "0")}`,
})))
const response = { generatedAt: "2026-09-10T12:00:00Z", totals: { items: 32, returned: 32, unheld: 8 }, items,
  people: [{ userId: "00000000-0000-0000-0000-000000000098", userName: "engineer", displayName: "Alex Engineer",
    isCurrentProjectMember: true, accountState: "active", baseRoles: ["SystemEngineer"], disciplineAffinities: ["system"],
    holds: 24, byLane: { work: 8, review: 8, sign: 8, approved: 0 } }] }
const nativeFetch = window.fetch.bind(window)
window.fetch = (input, init) => String(input).includes("/api/team-work")
  ? Promise.resolve(new Response(JSON.stringify(response), { headers: { "Content-Type": "application/json" } }))
  : nativeFetch(input, init)
createRoot(document.getElementById("root")!).render(<TeamWork api="" projectId="synthetic" user={{
  id: "viewer", userName: "viewer", displayName: "Viewer", email: "", isAdministrator: false, mustChangePassword: false, programs: [],
}} />)
