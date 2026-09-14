import { spawn } from "node:child_process";
import { join } from "node:path";
import { tmpdir } from "node:os";

const connection = process.env.AEROLINK_E2E_CONNECTION_STRING ?? "";
const normalizedConnection = connection.toLocaleLowerCase();
if (!connection || normalizedConnection.includes("54329") || normalizedConnection.includes(".local")) {
  throw new Error(
    "AEROLINK_E2E_CONNECTION_STRING must name an owned disposable PostgreSQL database (54329 and .local are rejected).",
  );
}

const stateFile = process.env.AEROLINK_RESTART_RECOVERY_STATE_FILE
  ?? join(tmpdir(), "aerolink-1037-restart-recovery-state.json");
const npx = process.platform === "win32" ? "npx.cmd" : "npx";
const commonArgs = [
  "playwright",
  "test",
  "--config=playwright.pg-allpath.config.ts",
  "tests/project-inception-restart-recovery.spec.ts",
  "--workers=1",
];

function runPhase(phase) {
  return new Promise((resolve, reject) => {
    const child = spawn(npx, commonArgs, {
      env: {
        ...process.env,
        AEROLINK_RESTART_RECOVERY_PHASE: phase,
        AEROLINK_RESTART_RECOVERY_STATE_FILE: stateFile,
        // The database is supplied explicitly by the operator. Showcase seeding is unrelated to
        // restart recovery and remains opt-in for callers that deliberately set it.
        AEROLINK_E2E_SKIP_SHOWCASE_SEED: process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED ?? "true",
      },
      stdio: "inherit",
    });
    console.log(`Restart recovery ${phase} runner started with PID ${child.pid ?? "unknown"}.`);
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(`Restart recovery ${phase} exited with ${signal ?? `code ${code}`}.`));
    });
  });
}

await runPhase("prepare");
console.log("The prepare Playwright invocation has exited; start the verify invocation as a new API process.");
await runPhase("verify");
console.log(`Restart recovery completed for all five paths. State file: ${stateFile}`);
