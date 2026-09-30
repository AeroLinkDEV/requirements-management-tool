// Regenerates the chase view's helicopter model (helicopterModel.ts, beside this file) as
// public/fms-cdu/models/helicopter-light-twin.glb. The output is deterministic; fms-helicopter-model.spec.ts checks
// that the committed file is exactly what this writes. Run from product/client: node src/fmsCdu/buildHelicopterModel.mjs
// It lives in the bench directory, with the model it builds, so the test planner treats it as part of the bench.
import { writeFileSync } from "node:fs";
import { buildHelicopterGlb } from "./helicopterModel.ts";

const bytes = buildHelicopterGlb();
writeFileSync(new URL("../../public/fms-cdu/models/helicopter-light-twin.glb", import.meta.url), bytes);
console.log(`helicopter-light-twin.glb: ${bytes.byteLength} bytes`);
