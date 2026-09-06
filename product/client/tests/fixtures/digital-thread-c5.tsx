import { useState } from "react"
import { createRoot } from "react-dom/client"
import "../../src/index.css"
import "../../src/Density.css"
import DigitalThreadNetwork from "../../src/DigitalThreadNetwork"
import DigitalThreadArtifact from "../../src/DigitalThreadArtifact"
import DigitalThreadInsideChange from "../../src/DigitalThreadInsideChange"
import type { ProposalContent, ProposalItem } from "../../src/changeProposalPresentation"
import type { NetworkNode } from "../../src/changeNetworkPresentation"
import { V5_FIXTURE_IDS as ids, V5_ARTIFACT_THREAD_RESPONSE, V5_NETWORK_PROJECTION_RESPONSE } from "./digital-thread-v5"

// Static, explicit inputs to the real components. This is never a product route or a database seed.
const params = new URLSearchParams(location.search)
document.documentElement.dataset.density = params.get("density") === "compact" ? "compact" : "comfortable"
const opened: NetworkNode = {
  id: "opened-change", kind: "ChangeRequest", displayNumber: "SRCR-000000000031.00",
  title: "Oceanic sequencing change", level: "System", state: "SelectedForBaseline", buildVersion: "1.6",
}
const item = (over: Partial<ProposalItem> & Pick<ProposalItem, "id" | "kind">): ProposalItem => ({
  displayNumber: over.id, level: "System", statement: "The FMS shall sequence the selected route.",
  allocatedDownstream: [], disposition: "NoAllocationRecorded", ...over,
})
const content: ProposalContent = {
  ownerKind: "ChangeRequest", changeRequestId: opened.id, projectId: ids.project, displayNumber: opened.displayNumber,
  items: [
    item({ id: "SYSR-00151.00", kind: "Introduce", disposition: "TargetNotYetCreated" }),
    item({ id: "SYSR-00075.02", kind: "Modify", disposition: "BaseRevisionUnresolved" }),
    item({ id: "SYSR-00076.02", kind: "Modify", baseRevisionId: "exact-before-revision",
      supersededRevision: 1, supersededStatement: "The FMS shall sequence the entered route." }),
    item({ id: "SYSR-00077.01", kind: "Retire", baseRevisionId: "exact-retired-revision",
      supersededRevision: 1, supersededStatement: "The FMS shall use the obsolete route.", statement: "" }),
  ], covering: [], buildEffect: [],
}

function Fixture() {
  const [representation, setRepresentation] = useState<"map" | "table">("map")
  const [openedCount, setOpenedCount] = useState(0)
  const onOpen = () => setOpenedCount(value => value + 1)
  const hrefFor = (node: { id: string }) => `#exact-${node.id}`
  return <>
    <header style={{ height: 72, padding: 12 }}>
      <strong>Synthetic C5 acceptance fixture</strong>{" "}
      <button onClick={() => setRepresentation("map")}>Fixture Map</button>{" "}
      <button onClick={() => setRepresentation("table")}>Fixture Table</button>{" "}
      <output aria-label="Open change activations">{openedCount}</output>
    </header>
    {/* Reserve the product sidebar width so a desktop viewport is not mistaken for canvas width. */}
    <main style={{ marginLeft: 280, height: "calc(100vh - 96px)", minHeight: 0 }}>
      {params.get("view") === "inside" ? <DigitalThreadInsideChange
        opened={opened} register={[opened]} content={content} orderedLevels={["System", "HighLevel", "LowLevel"]}
        hrefFor={hrefFor} onOpenChange={onOpen} onBackToNetwork={() => undefined} representation={representation}
      /> : params.get("view") === "artifact" ? <DigitalThreadArtifact
        response={V5_ARTIFACT_THREAD_RESPONSE} hrefFor={hrefFor} onOpenChange={onOpen} representation={representation}
      /> : <DigitalThreadNetwork projection={V5_NETWORK_PROJECTION_RESPONSE} focalId={ids.hlr}
        hrefFor={hrefFor} onOpenChange={onOpen} buildLabel="Synthetic build 925" representation={representation} />}
    </main>
  </>
}

createRoot(document.getElementById("root")!).render(<Fixture />)
