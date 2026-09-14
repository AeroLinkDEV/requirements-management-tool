import { execFileSync, spawn } from "node:child_process";
import {
  createWriteStream,
  existsSync,
  mkdirSync,
  writeFileSync,
} from "node:fs";
import { isAbsolute, join, relative, resolve, sep } from "node:path";
import { tmpdir } from "node:os";

const connection = process.env.AEROLINK_E2E_CONNECTION_STRING ?? "";

function connectionProperties(value) {
  const properties = {};
  const allowed = new Set(["host", "port", "database", "username", "password", "include error detail"]);
  const required = new Set(["host", "port", "database", "username"]);
  for (const part of value.split(";")) {
    const separator = part.indexOf("=");
    if (separator < 1) throw new Error("The PostgreSQL connection must use the strict Host=;Port=;Database=;Username= key/value shape.");
    const key = part.slice(0, separator).trim().replace(/\s+/g, " ").toLocaleLowerCase();
    if (!allowed.has(key)) throw new Error(`Unsupported PostgreSQL connection key: ${part.slice(0, separator).trim()}.`);
    if (Object.prototype.hasOwnProperty.call(properties, key)) throw new Error(`Duplicate PostgreSQL connection key: ${key}.`);
    let item = part.slice(separator + 1).trim();
    if ((item.startsWith("'") && item.endsWith("'")) || (item.startsWith('"') && item.endsWith('"'))) {
      item = item.slice(1, -1);
    }
    properties[key] = item;
  }
  for (const key of required) {
    if (!properties[key]) throw new Error(`Missing PostgreSQL connection key: ${key}.`);
  }
  return {
    host: properties.host,
    port: properties.port,
    database: properties.database,
    username: properties.username,
  };
}

let database;
try {
  database = connectionProperties(connection);
} catch (error) {
  throw new Error(
    `Invalid AEROLINK_E2E_CONNECTION_STRING: ${error instanceof Error ? error.message : String(error)}`,
  );
}
const loopbackHosts = new Set(["127.0.0.1", "localhost", "::1"]);
const databaseName = database.database ?? "";
const port = Number(database.port);
if (
  !connection
  || !loopbackHosts.has(String(database.host).toLocaleLowerCase())
  || !Number.isInteger(port)
  || port < 1
  || port > 65535
  || port === 54329
  || !/^aerolink_1037_[a-z0-9][a-z0-9_-]*$/i.test(databaseName)
) {
  throw new Error(
    "AEROLINK_E2E_CONNECTION_STRING must name an owned loopback PostgreSQL database named aerolink_1037_* on an explicit port other than 54329.",
  );
}

const initialRepositoryStatus = execFileSync("git", ["status", "--porcelain"], {
  cwd: process.cwd(),
  encoding: "utf8",
}).trim();
if (initialRepositoryStatus) {
  throw new Error("The restart recovery runner requires a clean repository before creating run artifacts or building.");
}

const runId = `${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
const runDirectory = resolve(
  process.env.AEROLINK_RESTART_RECOVERY_RUN_DIR
    ?? join(tmpdir(), `aerolink-1037-restart-recovery-${runId}`),
);
const stateFile = resolve(
  process.env.AEROLINK_RESTART_RECOVERY_STATE_FILE ?? join(runDirectory, "state.json"),
);
const resultsDirectory = resolve(
  process.env.AEROLINK_E2E_OUTPUT_DIR ?? join(runDirectory, "results"),
);
const logsDirectory = resolve(
  process.env.AEROLINK_RESTART_RECOVERY_LOG_DIR ?? join(runDirectory, "logs"),
);

const temporaryRoot = resolve(tmpdir());
function assertOwnedTemporaryPath(path, label) {
  const relativePath = relative(temporaryRoot, path);
  if (!relativePath || isAbsolute(relativePath) || relativePath === ".." || relativePath.startsWith(`..${sep}`)) {
    throw new Error(`${label} must be inside the operating system temporary directory (${temporaryRoot}): ${path}`);
  }
}

assertOwnedTemporaryPath(runDirectory, "The restart recovery run directory");
assertOwnedTemporaryPath(stateFile, "The restart recovery state file");
assertOwnedTemporaryPath(resultsDirectory, "The restart recovery results directory");
assertOwnedTemporaryPath(logsDirectory, "The restart recovery logs directory");

function requireFreshPath(path, label) {
  if (existsSync(path)) {
    throw new Error(`${label} already exists; provide a new unique path rather than overwriting it: ${path}`);
  }
  mkdirSync(path, { recursive: true });
}

if (existsSync(runDirectory)) {
  throw new Error(`The restart recovery run directory already exists; choose a new unique path: ${runDirectory}`);
}
mkdirSync(runDirectory, { recursive: true });
if (existsSync(stateFile)) {
  throw new Error(`The restart recovery state file already exists; refusing to overwrite it: ${stateFile}`);
}
requireFreshPath(resultsDirectory, "The restart recovery results directory");
requireFreshPath(logsDirectory, "The restart recovery logs directory");

const manifestPath = join(runDirectory, "run-manifest.json");
const playwrightCli = resolve("node_modules/playwright/cli.js");
if (!existsSync(playwrightCli)) {
  throw new Error(`The local Playwright CLI was not found: ${playwrightCli}`);
}

function repositorySha() {
  return execFileSync("git", ["rev-parse", "HEAD"], {
    cwd: process.cwd(),
    encoding: "utf8",
  }).trim();
}

function repositoryStatus() {
  return execFileSync("git", ["status", "--porcelain"], {
    cwd: process.cwd(),
    encoding: "utf8",
  }).trim();
}

function repositoryState() {
  return { sha: repositorySha(), dirty: repositoryStatus() };
}

function assertRepositoryUnchanged(label) {
  const current = repositoryState();
  if (current.sha !== manifest.commitBefore || current.dirty !== manifest.dirtyBefore) {
    throw new Error(
      `The repository changed during ${label} (${manifest.commitBefore}/${manifest.dirtyBefore || "clean"} to ${current.sha}/${current.dirty || "clean"}).`,
    );
  }
  return current;
}

const manifest = {
  runId,
  status: "Running",
  startedAt: new Date().toISOString(),
  connection: { host: database.host, port, database: databaseName },
  stateFile,
  resultsDirectory,
  logsDirectory,
  commitBefore: repositorySha(),
  dirtyBefore: repositoryStatus(),
  phases: [],
};

function saveManifest() {
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2), "utf8");
}

saveManifest();

const commonArgs = [
  "test",
  "--config=playwright.pg-allpath.config.ts",
  "tests/project-inception-restart-recovery.spec.ts",
  "--workers=1",
];

function runBuildStep(name, executable, args) {
  return new Promise((resolveBuild, rejectBuild) => {
    const logPath = join(logsDirectory, `${name}.log`);
    const log = createWriteStream(logPath, { flags: "wx" });
    const buildRecord = {
      phase: name,
      status: "Running",
      startedAt: new Date().toISOString(),
      commitBefore: repositorySha(),
      dirtyBefore: repositoryStatus(),
      command: [executable, ...args],
      runnerPid: null,
      logPath,
    };
    manifest.phases.push(buildRecord);
    saveManifest();
    const child = spawn(executable, args, {
      cwd: process.cwd(),
      env: process.env,
      stdio: ["ignore", "pipe", "pipe"],
      shell: false,
    });
    buildRecord.runnerPid = child.pid ?? null;
    saveManifest();
    const forward = (chunk) => {
      process.stdout.write(chunk);
      log.write(chunk);
    };
    child.stdout?.on("data", forward);
    child.stderr?.on("data", forward);
    let finished = false;
    const finish = (status, error, code, signal) => {
      if (finished) return;
      finished = true;
      buildRecord.status = status;
      buildRecord.exitCode = code;
      buildRecord.signal = signal;
      if (error) buildRecord.error = error;
      buildRecord.finishedAt = new Date().toISOString();
      const after = repositoryState();
      buildRecord.commitAfter = after.sha;
      buildRecord.dirtyAfter = after.dirty;
      log.end();
      saveManifest();
      if (status === "Passed" && (after.sha !== manifest.commitBefore || after.dirty !== manifest.dirtyBefore)) {
        const stateError = `The repository changed during ${name} (${manifest.commitBefore}/${manifest.dirtyBefore || "clean"} to ${after.sha}/${after.dirty || "clean"}).`;
        buildRecord.status = "Failed";
        buildRecord.error = stateError;
        saveManifest();
        rejectBuild(new Error(stateError));
        return;
      }
      if (status === "Passed") resolveBuild();
      else rejectBuild(new Error(error ?? `${name} exited with ${signal ?? `code ${code}`}.`));
    };
    child.once("error", (error) => finish("Failed", error.message));
    child.once("exit", (code, signal) => finish(
      code === 0 ? "Passed" : "Failed",
      code === 0 ? undefined : `${name} exited with ${signal ?? `code ${code}`}.`,
      code,
      signal,
    ));
  });
}

function runPhase(phase) {
  return new Promise((resolvePhase, rejectPhase) => {
    const phaseResultsDirectory = join(resultsDirectory, phase);
    requireFreshPath(phaseResultsDirectory, `The ${phase} results directory`);
    const logPath = join(logsDirectory, `${phase}.log`);
    const apiLogPath = join(logsDirectory, `${phase}-api.log`);
    const log = createWriteStream(logPath, { flags: "wx" });
    const phaseRecord = {
      phase,
      status: "Running",
      startedAt: new Date().toISOString(),
      commitBefore: repositorySha(),
      dirtyBefore: repositoryStatus(),
      runnerPid: null,
      logPath,
      apiLogPath,
      resultsDirectory: phaseResultsDirectory,
    };
    manifest.phases.push(phaseRecord);
    saveManifest();
    const child = spawn(process.execPath, [playwrightCli, ...commonArgs], {
      cwd: process.cwd(),
      env: {
        ...process.env,
        AEROLINK_RESTART_RECOVERY_PHASE: phase,
        AEROLINK_RESTART_RECOVERY_STATE_FILE: stateFile,
        AEROLINK_E2E_OUTPUT_DIR: phaseResultsDirectory,
        AEROLINK_E2E_API_LOG: apiLogPath,
        AEROLINK_E2E_API_LOG_LABEL: `restart-recovery-${phase}-api`,
        // A fresh owned database gets the supported FMS fixture through globalSetup. Callers may
        // explicitly set true only when they have independently prepared the same owned fixture.
        AEROLINK_E2E_SKIP_SHOWCASE_SEED: process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED ?? "false",
      },
      stdio: ["ignore", "pipe", "pipe"],
      shell: false,
    });
    phaseRecord.runnerPid = child.pid ?? null;
    saveManifest();
    const forward = (chunk) => {
      process.stdout.write(chunk);
      log.write(chunk);
    };
    child.stdout?.on("data", forward);
    child.stderr?.on("data", forward);
    child.once("error", (error) => {
      phaseRecord.status = "Failed";
      phaseRecord.error = error.message;
      phaseRecord.finishedAt = new Date().toISOString();
      const after = repositoryState();
      phaseRecord.commitAfter = after.sha;
      phaseRecord.dirtyAfter = after.dirty;
      log.end();
      saveManifest();
      rejectPhase(error);
    });
    child.once("exit", (code, signal) => {
      phaseRecord.exitCode = code;
      phaseRecord.signal = signal;
      phaseRecord.finishedAt = new Date().toISOString();
      const after = repositoryState();
      phaseRecord.commitAfter = after.sha;
      phaseRecord.dirtyAfter = after.dirty;
      phaseRecord.status = code === 0 ? "Passed" : "Failed";
      if (code === 0 && (after.sha !== manifest.commitBefore || after.dirty !== manifest.dirtyBefore)) {
        phaseRecord.status = "Failed";
        phaseRecord.error = `The repository changed during ${phase} (${manifest.commitBefore}/${manifest.dirtyBefore || "clean"} to ${after.sha}/${after.dirty || "clean"}).`;
      }
      log.end();
      saveManifest();
      if (phaseRecord.status === "Passed") {
        resolvePhase();
      } else {
        rejectPhase(new Error(phaseRecord.error ?? `Restart recovery ${phase} exited with ${signal ?? `code ${code}`}.`));
      }
    });
  });
}

try {
  await runBuildStep("server-build", "dotnet", ["build", resolve("../AeroLink.slnx"), "--configuration", "Release"]);
  await runBuildStep("client-typecheck", process.execPath, [resolve("node_modules/typescript/bin/tsc"), "-b", "--pretty", "false"]);
  await runBuildStep("client-bundle", process.execPath, [resolve("node_modules/vite/bin/vite.js"), "build"]);
  assertRepositoryUnchanged("the complete exact-SHA build");
  await runPhase("prepare");
  console.log("The prepare Playwright invocation exited; verify will use a new API process.");
  await runPhase("verify");
  assertRepositoryUnchanged("the restart recovery run");
  manifest.status = "Passed";
} catch (error) {
  manifest.status = "Failed";
  manifest.error = error instanceof Error ? error.message : String(error);
  console.error(manifest.error);
  process.exitCode = 1;
} finally {
  const final = repositoryState();
  manifest.commitAfter = final.sha;
  manifest.dirtyAfter = final.dirty;
  if (manifest.status === "Passed" && (final.sha !== manifest.commitBefore || final.dirty !== manifest.dirtyBefore)) {
    manifest.status = "Failed";
    manifest.error = `The repository changed before the run completed (${manifest.commitBefore}/${manifest.dirtyBefore || "clean"} to ${final.sha}/${final.dirty || "clean"}).`;
    process.exitCode = 1;
  }
  manifest.finishedAt = new Date().toISOString();
  saveManifest();
  console.log(`Restart recovery ${manifest.status.toLocaleLowerCase()} for all five paths. Run manifest: ${manifestPath}`);
}
