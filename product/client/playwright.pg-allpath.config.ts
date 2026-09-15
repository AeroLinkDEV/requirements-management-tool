import { defineConfig, devices } from "@playwright/test";
import { existsSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { createBrowserStorage } from './scripts/browser-storage.mjs';

const apiPort = process.env.AEROLINK_E2E_API_PORT ?? "5095";
const clientPort = process.env.AEROLINK_E2E_CLIENT_PORT ?? "5195";
const database = process.env.AEROLINK_E2E_CONNECTION_STRING ?? "";
const dotnetCandidates = [
  process.env.AEROLINK_DOTNET,
  process.env.USERPROFILE && join(process.env.USERPROFILE, ".dotnet", "dotnet.exe"),
  join(homedir(), ".dotnet", "dotnet"),
  "dotnet",
].filter((candidate): candidate is string => Boolean(candidate));
const dotnet = dotnetCandidates.find((candidate) => candidate === "dotnet" || existsSync(candidate)) ?? "dotnet";

function validateOwnedPostgresConnection(value: string) {
  const allowed = new Set(["host", "port", "database", "username", "password", "include error detail"]);
  const seen = new Set<string>();
  for (const part of value.split(";")) {
    const separator = part.indexOf("=");
    if (separator < 1) {
      throw new Error("Use the strict Host=;Port=;Database=;Username= PostgreSQL connection shape.");
    }
    const key = part.slice(0, separator).trim().replace(/\s+/g, " ").toLocaleLowerCase();
    if (!allowed.has(key)) throw new Error(`Unsupported PostgreSQL connection key: ${part.slice(0, separator).trim()}.`);
    if (seen.has(key)) throw new Error(`Duplicate PostgreSQL connection key: ${key}.`);
    seen.add(key);
  }
  for (const required of ["host", "port", "database", "username"]) {
    if (!seen.has(required)) throw new Error(`Missing PostgreSQL connection key: ${required}.`);
  }
  const properties = Object.fromEntries(value.split(";").map((part) => {
    const separator = part.indexOf("=");
    return [part.slice(0, separator).trim().replace(/\s+/g, " ").toLocaleLowerCase(), part.slice(separator + 1).trim()];
  }));
  for (const required of ["host", "port", "database", "username"]) {
    if (!properties[required]) throw new Error(`Empty PostgreSQL connection value: ${required}.`);
  }
  const parsedPort = Number(properties.port);
  if (
    !["127.0.0.1", "localhost", "::1"].includes(properties.host.toLocaleLowerCase())
    || !Number.isInteger(parsedPort)
    || parsedPort < 1
    || parsedPort > 65535
    || parsedPort === 54329
    || !/^aerolink_1037_[a-z0-9][a-z0-9_-]*$/i.test(properties.database)
  ) {
    throw new Error("An owned loopback PostgreSQL database named aerolink_1037_* on an explicit port other than 54329 is required.");
  }
}
validateOwnedPostgresConnection(database);
const runId = process.env.AEROLINK_E2E_RUN_ID;
if (!runId) throw new Error('The restart-recovery owner must provide AEROLINK_E2E_RUN_ID.');
const storage = createBrowserStorage(runId);
process.env.AEROLINK_E2E_API_BASE = `http://127.0.0.1:${apiPort}`;

export default defineConfig({
  testDir: "./tests",
  globalSetup: "./tests/global-setup.ts",
  outputDir: process.env.AEROLINK_E2E_OUTPUT_DIR ?? "test-results",
  fullyParallel: false,
  workers: 1,
  expect: { timeout: 15_000 },
  reporter: [["list"]],
  use: { baseURL: `http://127.0.0.1:${clientPort}`, trace: "retain-on-failure", screenshot: "only-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      // The recovery wrapper builds the exact SHA before either phase. This existing repository
      // helper owns the API process tree and keeps a durable transcript for each phase.
      command: "node scripts/run-api-with-log.mjs",
      env: {
        AEROLINK_E2E_API_ARGV: JSON.stringify([
          dotnet,
          "run",
          "--configuration",
          "Release",
          "--project",
          "../src/AeroLink.Api",
          "--urls",
          `http://127.0.0.1:${apiPort}`,
        ]),
        ...(process.env.AEROLINK_E2E_API_LOG
          ? {
              AEROLINK_E2E_API_LOG: process.env.AEROLINK_E2E_API_LOG,
              AEROLINK_E2E_API_LOG_LABEL: process.env.AEROLINK_E2E_API_LOG_LABEL ?? "restart-recovery-api",
            }
          : {}),
        Database__Provider: "PostgreSql",
        Evidence__Root: storage.evidence,
        ConnectionStrings__AeroLink: database,
        DemoData__Enabled: "false",
        Identity__SeedDemoAccounts: "true",
        Identity__AllowDemoAccounts: "true",
        Identity__CookieSecure: "false",
        Cors__AllowedOrigins__0: `http://127.0.0.1:${clientPort}`,
      },
      url: `http://127.0.0.1:${apiPort}/health`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: `npm run dev -- --host 127.0.0.1 --port ${clientPort} --strictPort`,
      env: { VITE_API_URL: `http://127.0.0.1:${apiPort}` },
      url: `http://127.0.0.1:${clientPort}`,
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
});
