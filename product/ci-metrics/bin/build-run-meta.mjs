// Builds the trusted run metadata (expectedRun + expectedJobs) for the current-run aggregator.
//
// This is default-branch workflow code executed by the non-authoritative metrics-report job. It is the only
// source of the expected topology: fragments are untrusted data, so dependency edges and expected instances
// come from here, not from fragment claims.

import { writeFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'

const value = (name) => process.env[name] ?? null
const enabled = (name) => process.env[name] === 'true'

const jobs = []
const docsOnly = enabled('CLASS_DOCS_ONLY')
if (!docsOnly) {
  jobs.push({ group: 'changes', instance: 'changes', needs: [] })
  if (enabled('CLASS_BACKEND')) {
    jobs.push(
      { group: 'backend-api', instance: 'backend-api-1', needs: ['changes'] },
      { group: 'backend-api', instance: 'backend-api-2', needs: ['changes'] },
      { group: 'backend-api', instance: 'backend-api-3', needs: ['changes'] },
      { group: 'backend-core', instance: 'backend-core', needs: ['changes'] })
  }
  if (enabled('CLASS_CLIENT')) jobs.push({ group: 'client', instance: 'client', needs: ['changes'] })
  if (enabled('CLASS_BROWSER')) {
    jobs.push(
      { group: 'browser-pr', instance: 'browser-pr-1', needs: ['changes'] },
      { group: 'browser-pr', instance: 'browser-pr-2', needs: ['changes'] },
      { group: 'browser-pr', instance: 'browser-pr-3', needs: ['changes'] },
      { group: 'browser-pr', instance: 'browser-pr-4', needs: ['changes'] },
      { group: 'browser-production', instance: 'browser-production', needs: ['changes'] })
  }
  if (enabled('CLASS_POSTGRESQL')) jobs.push({ group: 'postgresql-smoke', instance: 'postgresql-smoke', needs: ['changes'] })
  jobs.push({ group: 'script-contracts', instance: 'script-contracts', needs: ['changes'] })
  jobs.push({
    group: 'gate',
    instance: 'gate',
    needs: ['changes', 'backend-api', 'backend-core', 'client', 'script-contracts', 'browser-pr', 'browser-production', 'postgresql-smoke', 'metrics-tooling'],
  })
}
jobs.push({ group: 'metrics-tooling', instance: 'metrics-tooling', needs: [] })

const event = value('GITHUB_EVENT_NAME')
if (event === 'push') jobs.push({ group: 'warm-chromium-cache', instance: 'warm-chromium-cache', needs: [] })
if (event === 'schedule' || event === 'workflow_dispatch') {
  jobs.push(
    { group: 'browser-full', instance: 'browser-full-1', needs: ['changes'] },
    { group: 'browser-full', instance: 'browser-full-2', needs: ['changes'] },
    { group: 'browser-full', instance: 'browser-full-3', needs: ['changes'] })
}

const tree = value('METRICS_TREE_SHA')
if (!tree || !/^[0-9a-f]{40}$/.test(tree)) {
  console.error('[ci-metrics] METRICS_TREE_SHA is missing or malformed; run metadata will not be authoritative.')
  process.exit(1)
}

const meta = {
  queueDelayMs: null,
  expectedRun: {
    id: Number(value('GITHUB_RUN_ID')),
    attempt: Number(value('GITHUB_RUN_ATTEMPT') ?? 1),
    event: value('GITHUB_EVENT_NAME'),
    sha: value('GITHUB_SHA'),
    tree,
    workflowRef: value('GITHUB_WORKFLOW_REF'),
    repository: value('GITHUB_REPOSITORY'),
  },
  expectedJobs: jobs,
}

const output = value('METRICS_RUN_META_PATH')
if (!output) {
  console.error('[ci-metrics] METRICS_RUN_META_PATH is not set.')
  process.exit(1)
}
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, `${JSON.stringify(meta, null, 2)}\n`, 'utf8')
console.log(`[ci-metrics] Wrote run metadata with ${jobs.length} expected job instances.`)
