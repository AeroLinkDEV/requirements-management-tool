import { createRoot } from "react-dom/client"
import { useState } from "react"
import DigitalThreadCanvas, { type CanvasEdge, type CanvasNode } from "../../src/DigitalThreadCanvas"
import "../../src/index.css"

/**
 * Shared-canvas contract fixture for #1022 (test-only).
 *
 * The subject sits in a lane of its own; the linked record lives three lanes to the right at row 12, so it is far
 * below every lane window while its own lane is otherwise empty. That combination — out of view, with usable
 * space — is the available-space branch of the reveal, and with a narrow viewport the linked lane also starts
 * horizontally hidden, which is the first-exposure case. Deliberate row offsets like this are allowed by
 * `CanvasNode`; no production adapter emits them, and nothing here should be read as a claim that one does.
 */
// Six lanes: at the landing zoom a board this wide does not fit a normal viewport, which is what makes the
// right-most lane genuinely hidden until the reader pans to it.
const lanes = ["HLR", "LLR", "A", "B", "C", "TEST"]
const nodes: CanvasNode[] = [
  { id: "subj", lane: 0, row: 2 },
  { id: "spacer", lane: 1, row: 0 },
  { id: "link", lane: 5, row: 12 },
]
const edges: CanvasEdge[] = [{ from: "subj", to: "link", label: "verified by" }]

function Harness() {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  return (
    <div style={{ height: "100%" }}>
      <DigitalThreadCanvas
        lanes={lanes}
        nodes={nodes}
        edges={edges}
        scopeKey="contract|hidden-lane"
        selectedId={selectedId}
        onSelect={setSelectedId}
        renderCard={node => (
          <div
            className={`probeCard probe-${node.id}`}
            style={{ height: "100%", boxSizing: "border-box", padding: 8, border: "1px solid #cbd6df", borderRadius: 6, background: "#fff" }}
          >
            <strong>{node.id}</strong>
          </div>
        )}
      />
    </div>
  )
}

createRoot(document.getElementById("root")!).render(<Harness />)
