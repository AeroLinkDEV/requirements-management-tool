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
          revisionId: "hlr-1-rev",
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
          revisionId: "hlr-2-rev",
          displayNumber: "HLR-00021.00",
          level: "HighLevel",
          statement: "Fixed-order behaviour.",
          isProposed: false,
        },
        {
          // The SAME controlled artifact as HLR-00020.00 above, at a different exact revision. Keyed by
          // artifact id, one of these two cards would silently disappear.
          id: "hlr-1",
          revisionId: "hlr-1-rev-b",
          displayNumber: "HLR-00020.01",
          level: "HighLevel",
          statement: "The FMS shall compute the next waypoint, revised.",
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
  covering: [
    // One procedure revision covering TWO requirement revisions, with different link states. The server
    // returns a row per link, so this arrives twice; the board must show one card and keep both edges, and
    // the differing states must not fight over the node.
    {
      requirementRevisionId: "hlr-1-rev",
      artifactId: "tp-art",
      artifactRevisionId: "tp-rev",
      displayNumber: "HLRTP-00090.00",
      title: "Waypoint computation procedure",
      level: "HighLevel",
      artifactKind: "Procedure",
      artifactState: "Approved",
      coverageState: "Suspect",
    },
    {
      requirementRevisionId: "hlr-2-rev",
      artifactId: "tp-art",
      artifactRevisionId: "tp-rev",
      displayNumber: "HLRTP-00090.00",
      title: "Waypoint computation procedure",
      level: "HighLevel",
      artifactKind: "Procedure",
      artifactState: "Approved",
      coverageState: "Covered",
    },
  ],
  buildEffect: [
    { baselineId: "bl-1", displayNumber: "SW-91.00.00", name: "Build 1.6 candidate", state: "Draft", isPredecessor: false },
    { baselineId: "bl-0", displayNumber: "SW-90.00.00", name: "Build 1.5", state: "Released", isPredecessor: true },
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
    verificationItem({ id: "SYSTP-00031.00", kind: "Retire", baseRevisionId: "vrev-1" }),
  ],
  executions: [
    {
      id: "run-1",
      procedureRevisionId: "vrev-0",
      outcome: "Pass",
      executedBy: "verification.engineer",
      executedAt: "2026-08-30T10:00:00.000Z",
      determination: "Sequencing behaved as specified.",
    },
  ],
  buildEffect: [
    { baselineId: "bl-1", displayNumber: "SW-91.00.00", name: "Build 1.6 candidate", state: "Draft", isPredecessor: false },
    { baselineId: "bl-0", displayNumber: "SW-90.00.00", name: "Build 1.5", state: "Released", isPredecessor: true },
  ],
}

/**
 * A Case package, in its own envelope.
 *
 * The server sets every item's artifact kind from the owning TestChangeReview, so one review cannot emit a
 * Procedure envelope containing a Case item. An earlier fixture did exactly that, which meant the Case-retire
 * assertion passed on data production could never return.
 */
const caseContent: ProposalContent = {
  ownerKind: "TestChangeRequest",
  ownerId: "tcr-2",
  projectId: "p-1",
  releaseId: "r-1",
  displayNumber: "HLRTCCR-00300.00",
  discipline: "HighLevelSoftware",
  artifactKind: "Case",
  items: [
    verificationItem({
      id: "HLRTC-00050.00",
      kind: "Retire",
      artifactKind: "Case",
      level: "HighLevel",
      baseRevisionId: "crev-1",
    }),
  ],
  executions: [],
  buildEffect: [],
}

const openedCaseTcr: NetworkNode = {
  id: "tcr-2",
  kind: "TestChangeRequest",
  displayNumber: "HLRTCCR-00300.00",
  title: "Waypoint case retirement",
  state: "Draft",
  level: "HighLevel",
  buildVersion: "1.6",
}

/**
 * The opened record for the verification scenario is a real TestChangeRequest node.
 *
 * Using a ChangeRequest here would have left the TEST branches untested: lane labels, the register chip
 * semantics and the owner shape are all chosen from `opened.kind`, so a mismatched fixture identity would
 * silently exercise the requirement path while appearing to test the verification one.
 */
const openedTcr: NetworkNode = {
  id: "tcr-1",
  kind: "TestChangeRequest",
  displayNumber: "SYSTPCR-00200.00",
  title: "Sequencing procedure rework",
  state: "Draft",
  level: "System",
  buildVersion: "1.6",
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

/**
 * Re-renders into the SAME root so the component is updated rather than remounted.
 *
 * That is what makes the loading assertion meaningful: if the fixture tore the tree down and built it again,
 * a stable-looking frame would prove nothing about whether the board jumped.
 */
const renderLoaded = () =>
  root.render(<DigitalThreadInsideChange {...shared} content={requirementModify} />)
;(window as unknown as { __loadInsideChange?: () => void }).__loadInsideChange = renderLoaded

root.render(
  scenario === "verification" ? (
    <DigitalThreadInsideChange
      {...shared}
      opened={openedTcr}
      register={[opened, sibling, openedTcr]}
      content={verificationContent}
    />
  ) : scenario === "case" ? (
    <DigitalThreadInsideChange
      {...shared}
      opened={openedCaseTcr}
      register={[opened, sibling, openedCaseTcr]}
      content={caseContent}
    />
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
