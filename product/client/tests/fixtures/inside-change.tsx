/**
 * Test-only mount for `DigitalThreadInsideChange`.
 *
 * The component is not on any route until slice 6, so rendered behaviour would otherwise be unverifiable —
 * and a pure-logic spec cannot prove a card carries a class, a hop badge appears, or an error leaves the
 * canvas mounted. This mounts it with static props and no server, so the spec drives the real component in a
 * real browser without any production page wiring.
 *
 * The scenario is chosen by `?case=` so one fixture serves every rendered assertion.
 */
import { createRoot } from "react-dom/client"
import DigitalThreadInsideChange from "../../src/DigitalThreadInsideChange"
import type {
  ProposalContent,
  ProposalItem,
  VerificationProposalItem,
} from "../../src/changeProposalPresentation"
import type { NetworkNode } from "../../src/changeNetworkPresentation"

const opened: NetworkNode = {
  id: "cr-1",
  kind: "ChangeRequest",
  displayNumber: "SRCR-00100.00",
  title: "Sequencing rework",
  state: "Draft",
  level: "System",
  buildVersion: "1.6",
}

const sibling: NetworkNode = {
  id: "cr-2",
  kind: "ChangeRequest",
  displayNumber: "SRCR-00101.00",
  title: "Annunciation rework",
  state: "InReview",
  level: "System",
  buildVersion: "1.6",
}

const requirementItem = (over: Partial<ProposalItem> & { id: string; kind: string }): ProposalItem => ({
  displayNumber: over.id,
  level: "System",
  statement: "The FMS shall sequence oceanic waypoints in round-robin order.",
  allocatedDownstream: [],
  disposition: "Allocated",
  ...over,
})

const verificationItem = (
  over: Partial<VerificationProposalItem> & { id: string; kind: string },
): VerificationProposalItem => ({
  displayNumber: over.id,
  level: "System",
  artifactKind: "Procedure",
  proposedContent: null,
  finalCoverage: [],
  addedCoverage: [],
  removedCoverage: [],
  parentKind: "Allocated",
  exactParents: [],
  referenceGaps: [],
  ...over,
})

const requirementModify: ProposalContent = {
  ownerKind: "ChangeRequest",
  changeRequestId: "cr-1",
  projectId: "p-1",
  displayNumber: "SRCR-00100.00",
  items: [
    requirementItem({
      id: "SR-00010.01",
      kind: "Modify",
      supersededStatement: "The FMS shall sequence oceanic waypoints in the order entered.",
      supersededRevision: 1,
      baseRevisionId: "rev-1",
      allocatedDownstream: [
        {
          id: "hlr-1",
          displayNumber: "HLR-00020.00",
          level: "HighLevel",
          statement: "The FMS shall compute the next waypoint.",
          isProposed: false,
          linkType: "AllocatedFrom",
        },
      ],
    }),
    requirementItem({
      id: "SR-00011.00",
      kind: "Retire",
      statement: "",
      baseRevisionId: "rev-2",
      allocatedDownstream: [
        {
          id: "hlr-2",
          displayNumber: "HLR-00021.00",
          level: "HighLevel",
          statement: "Fixed-order behaviour.",
          isProposed: false,
        },
      ],
    }),
    requirementItem({
      id: "SR-00012.01",
      kind: "Modify",
      disposition: "BehindTarget",
      supersededRevision: 1,
      latestRevision: 2,
      latestRevisionState: "Active",
    }),
  ],
}

const fullBody = {
  title: "Oceanic sequencing",
  objective: "Verify round-robin sequencing.",
  preconditions: "Configured product.",
  steps: "Enter five oceanic waypoints.",
  orderedSteps: "1. Select round robin. 2. Sequence.",
  expectedResult: "The sequence is correct.",
  expectedObservations: "The active mode is annunciated.",
  environmentSetup: "Bench rig.",
  testData: "Five oceanic waypoints.",
  cleanup: "Restore fixed order.",
  toolingAutomation: "Manual.",
}

const verificationContent: ProposalContent = {
  ownerKind: "TestChangeRequest",
  ownerId: "tcr-1",
  projectId: "p-1",
  releaseId: "r-1",
  displayNumber: "SYSTPCR-00200.00",
  discipline: "System",
  artifactKind: "Procedure",
  items: [
    verificationItem({
      id: "SYSTP-00030.01",
      kind: "Modify",
      proposedContent: fullBody,
      supersededRevision: 0,
      baseRevisionId: "vrev-0",
      // Only the objective and the environment differ, so a steps-only comparison would report no change.
      supersededContent: {
        ...fullBody,
        objective: "Verify fixed-order sequencing.",
        environmentSetup: "Desk check.",
      },
      finalCoverage: [
        {
          revisionId: "rev-a",
          artifactId: "art-a",
          displayNumber: "SR-00010.01",
          level: "System",
          statement: "Sequencing requirement.",
        },
      ],
      exactParents: [
        { revisionId: "case-1", kind: "Case", resolved: true, displayNumber: "HLRTC-00040.00", level: "HighLevel" },
        { revisionId: "missing-1", kind: null, resolved: false },
      ],
      referenceGaps: [
        { revisionId: "missing-1", role: "ExactParent", expectedKind: "Case", reason: "UnresolvedReference" },
        { revisionId: null, role: "AddedCoverage", expectedKind: "Requirement", reason: "MalformedReferenceList" },
      ],
    }),
    verificationItem({ id: "SYSTP-00031.00", kind: "Retire", artifactKind: "Case", baseRevisionId: "vrev-1" }),
  ],
}

const params = new URLSearchParams(window.location.search)
const scenario = params.get("case") ?? "requirement"

const shared = {
  opened,
  register: [opened, sibling],
  orderedLevels: ["System", "HighLevel", "LowLevel"] as const,
  onOpenChange: (node: NetworkNode) => {
    // Recorded on the document so a spec can prove the callback fired without a production route.
    document.body.dataset.openedChange = node.displayNumber
  },
}

const root = createRoot(document.getElementById("root")!)

root.render(
  scenario === "verification" ? (
    <DigitalThreadInsideChange {...shared} content={verificationContent} />
  ) : scenario === "loading" ? (
    <DigitalThreadInsideChange {...shared} content={null} loading />
  ) : scenario === "error" ? (
    <DigitalThreadInsideChange
      {...shared}
      content={requirementModify}
      error="The proposal content could not be read."
      onRetry={() => {
        document.body.dataset.retried = "yes"
      }}
    />
  ) : (
    <DigitalThreadInsideChange {...shared} content={requirementModify} />
  ),
)
