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

createRoot(document.getElementById("root")!).render(
  <DigitalThreadNetwork projection={projection} buildLabel="Build 1.6" />,
)
