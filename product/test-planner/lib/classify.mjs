// The changed-area classifier, in one place.
//
// This logic decides which product gates run for a change. It previously existed only as inline bash
// inside `.github/workflows/ci.yml`, which meant nothing else could ask "given what I changed, what
// should I run?" — so agents either ran far more than necessary locally, or pushed and spent a ten
// minute cycle discovering a lint error. It also meant the local and CI answers could differ with
// nothing to detect it.
//
// Two silent-green failures came out of this file, and both inform its design:
//
//   1. A two-dot `git diff` attributed main's commits to the branch. Once main moved ahead, a pull
//      request that touched only the workflow classified as client-only on its second run, the backend
//      suites skipped, and the gate went green having never run the tests the change was about. The
//      diff is three-dot and the caller is responsible for supplying it that way.
//   2. `ci.yml` keyed `browser` and `postgresql` but not `backend` and `client`, so a change to how the
//      backend tests run did not run the backend tests.
//
// Every rule below is expressed once and consumed by both the workflow and the local planner.

/** Paths that are documentation or design assets rather than product code. */
const NON_PRODUCT = /(^|\/)(docs?|design|showcase)\//
const MARKDOWN = /\.md$/

/**
 * The workflow file selects and shards every suite, so a change to it can alter any gate — including
 * by not running one. It therefore keys every area.
 */
const WORKFLOW = '^\\.github/workflows/ci\\.yml$'

export const AREA_PATTERNS = {
  backend: new RegExp(`^product/(src|tests)/|^product/.*\\.(cs|csproj|slnx|props|targets)$|${WORKFLOW}`),
  client: new RegExp(`^product/client/|${WORKFLOW}`),
  browser: new RegExp(`^product/(client|src/AeroLink\\.Api|src/AeroLink\\.Domain|src/AeroLink\\.Infrastructure)/|${WORKFLOW}`),
  // Keyed on persistence as well as the migration/identity keywords: a change to an EF query needs the
  // real provider even when no schema moves, because translation is not portable and the SQLite path
  // every other gate runs on will accept an expression Npgsql cannot produce.
  postgresql: new RegExp(
    `^product/(src/AeroLink\\.Api|src/AeroLink\\.Infrastructure|tests)/.*(migration|database|identity|auth|bootstrap|postgres|persistence)|^product/src/AeroLink\\.Infrastructure/Persistence/Migrations/|${WORKFLOW}`,
    'i',
  ),
}

/** Events that classify broadly rather than by diff. */
export const BROAD_EVENTS = new Set(['schedule', 'workflow_dispatch', 'push', 'merge_group'])

/**
 * Classify a list of changed paths.
 *
 * `event` matters: a merge-group event carries no base to diff against — both `pull_request.base.sha`
 * and `event.before` are null — and it is the last gate before the commit reaches main, which is the
 * moment to classify broadly rather than narrowly.
 */
export function classify(changedPaths, { event = 'pull_request' } = {}) {
  if (BROAD_EVENTS.has(event)) {
    return {
      docsOnly: false,
      backend: true,
      client: true,
      browser: true,
      postgresql: true,
      reason: `The ${event} event classifies every area, because it has no single base to diff against and is the last gate before main.`,
      unclassified: false,
    }
  }

  const paths = (Array.isArray(changedPaths) ? changedPaths : []).filter((p) => typeof p === 'string' && p.length > 0)
  const productFiles = paths.filter((path) => !NON_PRODUCT.test(path) && !MARKDOWN.test(path))
  const docsOnly = productFiles.length === 0

  const result = {
    docsOnly,
    backend: paths.some((path) => AREA_PATTERNS.backend.test(path)),
    client: paths.some((path) => AREA_PATTERNS.client.test(path)),
    browser: paths.some((path) => AREA_PATTERNS.browser.test(path)),
    postgresql: paths.some((path) => AREA_PATTERNS.postgresql.test(path)),
    reason: null,
    unclassified: false,
  }

  // A change that is neither documentation nor recognised product code used to select nothing: the gate
  // ran, every step skipped on its condition, and the job reported success having executed no test at
  // all. A launcher script, a root configuration file or a new top-level directory all landed there.
  // Being slower on a file nobody anticipated is the right trade against a green tick that means nothing.
  if (!result.docsOnly && !result.backend && !result.client) {
    result.backend = true
    result.client = true
    result.unclassified = true
    result.reason = 'Unclassified product change; running full backend and client validation rather than reporting a skipped pass.'
  }

  return result
}

/** The area each changed path selected, for explaining a decision rather than just stating it. */
export function explain(changedPaths) {
  const rows = []
  for (const path of Array.isArray(changedPaths) ? changedPaths : []) {
    if (typeof path !== 'string' || path.length === 0) continue
    const areas = Object.entries(AREA_PATTERNS)
      .filter(([, pattern]) => pattern.test(path))
      .map(([area]) => area)
    const isProduct = !NON_PRODUCT.test(path) && !MARKDOWN.test(path)
    rows.push({ path, areas, product: isProduct })
  }
  return rows
}

/**
 * What to run locally before pushing, given a classification.
 *
 * Deliberately narrower than CI. The point is fast pre-push feedback on the things that break most
 * often and cost a full cycle to discover, not a local reproduction of the gate.
 */
export function localPlan(classification) {
  const steps = []
  if (classification.docsOnly) {
    return [{ label: 'Nothing', command: null, why: 'Documentation-only change; no product gate applies.' }]
  }
  if (classification.backend) {
    steps.push({
      label: 'Build the solution',
      command: 'dotnet build product/AeroLink.slnx --configuration Release',
      why: 'A compile error costs a full CI cycle to discover and seconds to find here.',
    })
    // Two invocations, not one. `dotnet test` accepts a single project, solution or directory target;
    // passing two produces `MSBUILD : error MSB1008: Only one project can be specified` before either
    // suite runs. The first version of this printed exactly that broken command, which is what happens
    // when a tool's output is reviewed by reading it rather than by running it.
    steps.push({
      label: 'Domain suite',
      command: 'dotnet test product/tests/AeroLink.Domain.Tests --configuration Release --no-build',
      why: 'Fast, no host construction, and covers most backend rule changes.',
    })
    steps.push({
      label: 'Infrastructure suite',
      command: 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build',
      why: 'Persistence and EF behaviour, still without building an API host.',
    })
  }
  if (classification.client) {
    steps.push({
      label: 'Client lint, type-check and build',
      command: 'npm --prefix product/client run lint && npm --prefix product/client run build',
      why: 'The client gate is under a minute in CI but blocks the whole merge.',
    })
  }
  if (classification.browser) {
    steps.push({
      label: 'Browser smoke journeys',
      command: 'npm --prefix product/client run test:smoke',
      why: 'A bounded subset; the full journey set belongs in CI, not on a laptop.',
    })
  }
  if (classification.postgresql) {
    steps.push({
      label: 'PostgreSQL-sensitive checks',
      command: null,
      why: 'This change touches persistence, migrations or identity. CI runs these against a real PostgreSQL service container; SQLite locally will accept expressions Npgsql cannot produce, so a local pass is not evidence.',
    })
  }
  return steps
}

// The CI forecast is deliberately not implemented here. A hand-written list of jobs per area is a
// restatement of the workflow that is wrong the first time either changes, with nothing to detect it —
// the same class of drift this module exists to remove. `selectJobs` in ./workflow-jobs.mjs parses the
// conditions out of ci.yml and evaluates them, so the forecast comes from the text the runner obeys.
export { selectJobs } from './workflow-jobs.mjs'
