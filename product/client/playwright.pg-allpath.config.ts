import { defineConfig, devices } from "@playwright/test";

const apiPort = process.env.AEROLINK_E2E_API_PORT ?? "5095";
const clientPort = process.env.AEROLINK_E2E_CLIENT_PORT ?? "5195";
const database = process.env.AEROLINK_E2E_CONNECTION_STRING ?? "";
if (!database || database.includes("54329") || database.includes(".local")) {
  throw new Error("An owned disposable PostgreSQL connection is required.");
}
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
      command: `dotnet run --configuration Release --no-build --project ../src/AeroLink.Api --urls http://127.0.0.1:${apiPort}`,
      env: {
        Database__Provider: "PostgreSql",
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
