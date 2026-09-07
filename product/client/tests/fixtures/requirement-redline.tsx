import { createRoot } from "react-dom/client";
import { useState } from "react";
import RequirementRedline from "../../src/RequirementRedline";
import type { RequirementRedlineData } from "../../src/RequirementRedline";
import "../../src/index.css";
import "../../src/RequirementsWorkspace.css";

const scenario = new URLSearchParams(location.search).get("case");
const prefix = Array.from({ length: 400 }, () => "requirement").join(" ");
const data: RequirementRedlineData = {
  from: 0,
  to: 1,
  statement: scenario === "long"
    ? [{ kind: "removed", text: prefix + " reject" }, { kind: "added", text: prefix + " accept" }]
    : [{ kind: "same", text: "The FMS shall" }, { kind: "added", text: "safely" }, { kind: "same", text: "navigate." }],
  rationale: [{ kind: "same", text: "Operational capability." }],
  comparison: { isComplete: scenario !== "incomplete", statement: scenario === "long" ? "WholeField" : "Detailed", rationale: "Detailed" },
  verificationChanged: true,
  fromVerification: "Test",
  toVerification: "Analysis",
};

function Fixture() {
  const [open, setOpen] = useState(true);
  return open ? <RequirementRedline data={data} onClose={() => setOpen(false)} /> : <p>Comparison closed</p>;
}
createRoot(document.getElementById("root")!).render(<Fixture />);
