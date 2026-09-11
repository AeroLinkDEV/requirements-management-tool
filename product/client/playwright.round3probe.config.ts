import { defineConfig } from "@playwright/test"

// Temporary Round-3 probe config for #1022 — review artifact, not product code.
export default defineConfig({
  testDir: "./tests",
  testMatch: "zz-1022-plan-round3-probe.spec.ts",
  workers: 1,
  reporter: [["line"]],
  projects: [{ name: "round3-probe" }],
})
