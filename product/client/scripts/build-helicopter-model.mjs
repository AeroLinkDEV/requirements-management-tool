// Regenerates the chase view's helicopter model (src/fmsCdu/helicopterModel.ts) as
// public/fms-cdu/models/helicopter-light-twin.glb. The output is deterministic; fms-helicopter-model.spec.ts checks
// that the committed file is exactly what this writes. Run: node scripts/build-helicopter-model.mjs
import { writeFileSync } from "node:fs";
import { buildHelicopterGlb } from "../src/fmsCdu/helicopterModel.ts";

const bytes = buildHelicopterGlb();
writeFileSync(new URL("../public/fms-cdu/models/helicopter-light-twin.glb", import.meta.url), bytes);
console.log(`helicopter-light-twin.glb: ${bytes.byteLength} bytes`);
