import { createRoot } from "react-dom/client"
import "../../src/index.css"
import DownstreamAssessmentQueue from "../../src/DownstreamAssessmentQueue"

/**
 * #1016 S04. The real `DownstreamAssessmentQueue`, in each of the four states it can be in.
 *
 * The heading and the description are rendered by all four — rows, loading, empty and failed — so a test that
 * only ever sees a populated queue proves the wording for one of them. `?scenario=` picks which.
 *
 * The description is the part under correction. It used to say the queue held upstream changes "awaiting" a
 * downstream conclusion, which the rows themselves disprove: a Superseded row and four Complete variants are
 * already dispositioned. The mixed scenario below therefore carries both kinds deliberately — an assertion
 * about that sentence is only worth anything when the collection actually contradicts the old wording.
 */

const PROJECT_ID = "00000000-0000-0000-0000-0000000000b1"
const RELEASE_ID = "00000000-0000-0000-0000-0000000000b2"

type Row = Record<string, unknown>

const assessment = (id: string, number: string, title: string, state: string, outcome: string, extra: Row = {}): Row => ({
  id, sourceChangeRequestId: `${id}-source`, sourceChangeRequestNumber: number, sourceTitle: title,
  sourceProblem: "Upstream problem.", sourceAnalysis: "Upstream analysis.", sourceSolution: "Upstream solution.",
  sourceChanges: [{
    id: `${id}-change`, displayNumber: "SYSR-000150.02", level: "System", kind: "Modify",
    statement: "The FMS shall present configured review authority readably.",
  }],
  targetLevel: "HighLevel", state, outcome, rationale: "", supersededReason: "", buildReleased: false,
  linkedChangeRequests: [], reopenings: [],
  capabilities: { canAssign: false, canEdit: false, canSubmit: false, canApprove: false, canReturn: false, canReopen: false },
  ...extra,
})

// Pending and dispositioned in one collection: an undecided row, a superseded row, a completed no-change row
// and a completed row whose change is carried by a linked Draft.
const mixed: Row[] = [
  assessment("a1", "SRCR-00143", "Preserve configured review authority", "Open", "Pending"),
  assessment("a2", "SRCR-00140", "Withdrawn upstream approach", "Superseded", "Pending", {
    supersededByAssessmentId: "a1", supersededReason: "Replaced by a later approved change.",
  }),
  assessment("a3", "SRCR-00138", "Clarify altitude capture wording", "Approved", "NoChangeRequired", {
    rationale: "The HLR already states the behaviour.", decidedBy: "dana.systems",
    decidedAt: "2026-08-02T10:00:00Z", approvedBy: "mira.lead", approvedAt: "2026-08-03T10:00:00Z",
  }),
  assessment("a4", "SRCR-00136", "Split the engage interlock", "Approved", "ChangeRequestsLinked", {
    rationale: "Two HLRs must change.", decidedBy: "dana.systems", decidedAt: "2026-07-20T10:00:00Z",
    approvedBy: "mira.lead", approvedAt: "2026-07-21T10:00:00Z",
    linkedChangeRequests: [{
      changeRequestId: "cr-1", changeRequestNumber: "HLRCR-00021", title: "Split the engage interlock",
      state: "Draft",
    }],
  }),
]

const scenario = new URLSearchParams(window.location.search).get("scenario") ?? "mixed"

const rowsFor: Record<string, Row[]> = { mixed, empty: [] }

// The failed scenario fails the first read and succeeds the second, so the retry control can be shown to do
// something rather than merely to exist.
let assessmentReads = 0

const json = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), {
    status, headers: { "Content-Type": "application/json" },
  }))

const nativeFetch = window.fetch.bind(window)
const never = new Promise<Response>(() => {})

window.fetch = (input, init) => {
  const url = String(typeof input === "string" || input instanceof URL ? input : (input as Request).url)
  const path = new URL(url, window.location.origin).pathname

  if (path === "/api/downstream-assessments") {
    assessmentReads += 1
    if (scenario === "loading") return never
    if (scenario === "failed" && assessmentReads === 1) {
      return json({ error: "The downstream assessment queue could not be read for this build." }, 503)
    }
    return json(rowsFor[scenario] ?? mixed)
  }
  if (path === "/api/history/change-requests") return json({ items: [], total: 0 })
  if (path === "/api/authoring/impact") return json({ baseNumber: "SYSR-000150", known: true, derivedRequirements: [] })
  return nativeFetch(input, init)
}

createRoot(document.getElementById("root")!).render(
  <DownstreamAssessmentQueue
    api=""
    projectId={PROJECT_ID}
    releaseId={RELEASE_ID}
    targetLevel="HighLevel"
    user={{
      id: "systems.author", userName: "systems.author", displayName: "Sam Author", email: "",
      isAdministrator: false, mustChangePassword: false, programs: [],
    }}
    onOpenScr={() => {}}
    onOpenRequirement={() => {}}
    onCreateScr={() => {}}
    onAssessmentSelected={() => {}}
  />,
)
