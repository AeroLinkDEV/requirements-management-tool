/**
 * Test-only mount for `DigitalThreadNetwork`.
 *
 * Added while fixing #905 inside the slice 5B PR. The change network had pure-logic coverage only, so a defect
 * that is purely about rendered geometry — a three-letter badge overflowing its fixed box and colliding with
 * the identifier beside it — could not be asserted anywhere. Card layout is not something a presentation spec
 * can see.
 *
 * The projection below deliberately carries every badge `badgeOf` can return: PR, SYS, HLR, LLR, IFC, CUS and
 * TCR. The ladder includes Customer and Interface so those two levels get lanes and their records are drawn
 * rather than counted as off-ladder.
 */
import { createRoot } from "react-dom/client"
// The product stylesheet, because the card typography is written against its tokens. Without it every
// `font: var(--weight-strong) 8.5px …` shorthand is invalid at computed-value time and silently falls back to
// the 16px default, which would measure type the product never renders.
import "../../src/index.css"
import DigitalThreadNetwork from "../../src/DigitalThreadNetwork"
import { exactCardIdentity } from "../../src/DigitalThreadPage"
import { exactTraceArtifactPath } from "../../src/routing"
import type { NetworkEdge, NetworkNode, NetworkProjection } from "../../src/changeNetworkPresentation"

const node = (over: Partial<NetworkNode> & { id: string; kind: string; displayNumber: string }): NetworkNode => ({
  title: "Oceanic round-robin routing rework",
  state: "InReview",
  buildVersion: "1.6",
  ...over,
})

/** One record per badge `badgeOf` can produce, each with a long identifier so collisions are visible. */
const nodes: NetworkNode[] = [
  node({ id: "pr-1", kind: "ProblemReport", displayNumber: "PR-00003.00", state: "Open" }),
  node({ id: "cus-1", kind: "ChangeRequest", displayNumber: "CUSCR-000112.00", level: "Customer" }),
  node({ id: "ifc-1", kind: "ChangeRequest", displayNumber: "IFCCR-000118.00", level: "Interface" }),
  node({ id: "sys-1", kind: "ChangeRequest", displayNumber: "SRCR-00039.00", level: "System", state: "Approved" }),
  node({ id: "hlr-1", kind: "ChangeRequest", displayNumber: "HLRCR-00127.00", level: "HighLevel" }),
  node({ id: "llr-1", kind: "ChangeRequest", displayNumber: "LLRCR-00061.00", level: "LowLevel", state: "Draft" }),
  node({ id: "tcr-1", kind: "TestChangeRequest", displayNumber: "LLRTPCR-000009.00" }),
]

const edge = (fromId: string, fromKind: string, toId: string, toKind: string, relation: string): NetworkEdge => ({
  fromId, fromKind, toId, toKind, relation, provenance: [], isSuspect: false,
})

const projection: NetworkProjection = {
  projectId: "5f6e1b0a-1c2d-4e3f-8a9b-0c1d2e3f4a5b",
  releaseId: "a1b2c3d4-e5f6-4708-9a0b-1c2d3e4f5061",
  nodes,
  edges: [
    edge("pr-1", "ProblemReport", "sys-1", "ChangeRequest", "ResolvedBy"),
    edge("cus-1", "ChangeRequest", "ifc-1", "ChangeRequest", "AllocatesTo"),
    edge("ifc-1", "ChangeRequest", "sys-1", "ChangeRequest", "AllocatesTo"),
    edge("sys-1", "ChangeRequest", "hlr-1", "ChangeRequest", "AllocatesTo"),
    edge("hlr-1", "ChangeRequest", "llr-1", "ChangeRequest", "AllocatesTo"),
    edge("llr-1", "ChangeRequest", "tcr-1", "TestChangeRequest", "VerifiedBy"),
  ],
  truncated: false,
  orderedLevels: ["Customer", "Interface", "System", "HighLevel", "LowLevel"],
}

/**
 * The corrected change network in the server's own relation vocabulary (#925 F5/V5): a problem report
 * resolved by a System change, an upstream chain presented upstream → downstream, and verification
 * branches off the HLR and LLR changes. `Upstream` edges are emitted parent → child by the corrected
 * projection, so the canvas phrases them "allocates to" reading along the arrow.
 */
const serverChainProjection: NetworkProjection = {
  projectId: projection.projectId,
  releaseId: projection.releaseId,
  nodes: [
    node({ id: "pr-1", kind: "ProblemReport", displayNumber: "PR-00003.00", state: "Open" }),
    node({ id: "sys-1", kind: "ChangeRequest", displayNumber: "SRCR-00039.00", level: "System", state: "Approved" }),
    node({ id: "hlr-1", kind: "ChangeRequest", displayNumber: "HLRCR-00127.00", level: "HighLevel" }),
    node({ id: "hlr-2", kind: "ChangeRequest", displayNumber: "HLRCR-00128.00", level: "HighLevel" }),
    node({ id: "llr-1", kind: "ChangeRequest", displayNumber: "LLRCR-00061.00", level: "LowLevel", state: "Draft" }),
    node({ id: "llr-2", kind: "ChangeRequest", displayNumber: "LLRCR-00062.00", level: "LowLevel", state: "Draft" }),
    node({ id: "tcr-1", kind: "TestChangeRequest", displayNumber: "HLRTPCR-000007.00" }),
    node({ id: "tcr-2", kind: "TestChangeRequest", displayNumber: "LLRTPCR-000009.00" }),
  ],
  edges: [
    edge("pr-1", "ProblemReport", "sys-1", "ChangeRequest", "ProblemReportResolution"),
    edge("sys-1", "ChangeRequest", "hlr-1", "ChangeRequest", "Upstream"),
    edge("sys-1", "ChangeRequest", "hlr-2", "ChangeRequest", "Upstream"),
    edge("hlr-1", "ChangeRequest", "llr-1", "ChangeRequest", "Upstream"),
    edge("hlr-1", "ChangeRequest", "llr-2", "ChangeRequest", "Upstream"),
    edge("hlr-1", "ChangeRequest", "tcr-1", "TestChangeRequest", "CoveredByTestChangeRequest"),
    edge("llr-1", "ChangeRequest", "tcr-2", "TestChangeRequest", "CoveredByTestChangeRequest"),
  ],
  truncated: false,
  orderedLevels: ["System", "HighLevel", "LowLevel"],
}

const scenario = new URLSearchParams(window.location.search).get("case") ?? "default"
const hoverProjection: NetworkProjection = {
  ...serverChainProjection,
  nodes: [
    node({ id: "pr-5", kind: "ProblemReport", displayNumber: "PR-00005", state: "Open" }),
    ...Array.from({ length: 18 }, (_, i) => node({ id: `other-${i}`, kind: "ChangeRequest", displayNumber: `HLRCR-${String(i).padStart(5, "0")}`, level: "HighLevel" })),
    node({ id: "hlr-127", kind: "ChangeRequest", displayNumber: "HLRCR-00127", level: "HighLevel" }),
    ...Array.from({ length: 18 }, (_, i) => node({ id: `case-${i}`, kind: "TestChangeRequest", displayNumber: `HLRTCCR-${String(i).padStart(6, "0")}`, level: "Case" })),
    node({ id: "case-34", kind: "TestChangeRequest", displayNumber: "HLRTCCR-000034", level: "Case" }),
    node({ id: "proc-34", kind: "TestChangeRequest", displayNumber: "HLRTPCR-000034", level: "Procedure" }),
  ],
  edges: [
    edge("pr-5", "ProblemReport", "hlr-127", "ChangeRequest", "ProblemReportResolution"),
    edge("hlr-127", "ChangeRequest", "case-34", "TestChangeRequest", "CoveredByTestChangeRequest"),
    edge("case-34", "TestChangeRequest", "proc-34", "TestChangeRequest", "Upstream"),
  ],
}
const denseProjection = { ...hoverProjection, edges: [...hoverProjection.edges,
  ...Array.from({ length: 18 }, (_, index) => edge("hlr-127", "ChangeRequest", `case-${index}`, "TestChangeRequest", "CoveredByTestChangeRequest")),
] }
/**
 * #1016 S13A. Verification packages with and without a controlled number, side by side.
 *
 * The two unnumbered rows are raised from the same approved change and therefore carry the same label by
 * design: that is what makes them the case worth drawing. They must remain two cards, in a stable order,
 * neither dropped nor merged, and neither wearing the badge of a controlled test change request.
 *
 * The identities here are the fixture's own. Nothing in this file reproduces the original observation.
 */
const verificationIdentityProjection: NetworkProjection = {
  projectId: projection.projectId,
  releaseId: projection.releaseId,
  nodes: [
    node({ id: "sys-9", kind: "ChangeRequest", displayNumber: "SRCR-00039.00", level: "System", state: "Approved" }),
    node({
      id: "asmt-a", kind: "TestChangeRequest", level: "Procedure", state: "Draft",
      displayNumber: "Unnumbered assessment",
      verification: {
        hasControlledNumber: false, outcome: "Pending", artifactKind: "Procedure", discipline: "System",
        originKind: "ChangeRequest", originReferenceId: "sys-9", sourceDisplayNumber: "SRCR-00039.00",
      },
    }),
    node({
      id: "asmt-b", kind: "TestChangeRequest", level: "Procedure", state: "Draft",
      displayNumber: "Unnumbered assessment",
      verification: {
        hasControlledNumber: false, outcome: "NoChangeRequired", artifactKind: "Procedure",
        discipline: "System", originKind: "ProblemReport", originReferenceId: "pr-9",
        // Deliberately long: the card truncates rather than spilling, and the inspector shows it whole.
        sourceDisplayNumber: "PR-00004321.00 oceanic round-robin sequencing field report",
      },
    }),
    node({
      id: "tcr-9", kind: "TestChangeRequest", level: "Procedure", state: "InReview",
      displayNumber: "SYSTPCR-000012.00",
      verification: {
        hasControlledNumber: true, controlledNumber: "SYSTPCR-000012", controlledRevision: 0,
        outcome: "ChangeRequired", artifactKind: "Procedure", discipline: "System",
        originKind: "ChangeRequest", originReferenceId: "sys-9", sourceDisplayNumber: "SRCR-00039.00",
      },
    }),
  ],
  edges: [
    edge("sys-9", "ChangeRequest", "asmt-a", "TestChangeRequest", "CoveredByTestChangeRequest"),
    edge("sys-9", "ChangeRequest", "asmt-b", "TestChangeRequest", "CoveredByTestChangeRequest"),
    edge("sys-9", "ChangeRequest", "tcr-9", "TestChangeRequest", "CoveredByTestChangeRequest"),
  ],
  truncated: false,
  orderedLevels: ["System"],
}
const chosen = scenario === "dense" ? denseProjection : scenario === "hover" ? hoverProjection : scenario === "server" ? serverChainProjection : scenario === "verification-identity" ? verificationIdentityProjection : projection

/**
 * #1016 S13A. The real adapter and the real router, wired exactly as the page wires them.
 *
 * `DigitalThreadPage` builds an identity from the node with `exactCardIdentity` and hands it to
 * `exactTraceArtifactPath`. That rebuild used to drop the verification facts, so the router fell back to
 * reading the identifier's prefix even though the node stated its discipline — and an unnumbered System
 * assessment addressed the software workspace. Composing them here means the rendered href is the one the
 * page produces, not one this fixture computes for itself.
 */
const routeContext = { programId: "program-a", projectId: projection.projectId, releaseId: projection.releaseId }
const hrefFor = (node: Parameters<typeof exactCardIdentity>[0]) => {
  const identity = exactCardIdentity(node)
  return identity ? exactTraceArtifactPath(routeContext, identity) : undefined
}

// The Table is the accessible representation of the same projection, and it is a prop rather than internal
// state, so the fixture selects it the way the page does.
const representation = new URLSearchParams(window.location.search).get("view") === "table" ? "table" : "map"

createRoot(document.getElementById("root")!).render(
  <DigitalThreadNetwork
    projection={chosen}
    buildLabel="Build 1.6"
    hrefFor={hrefFor}
    representation={representation}
  />,
)
