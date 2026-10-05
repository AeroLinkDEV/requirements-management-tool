// Builds the run metadata (expectedRun + expectedJobs + skippedJobs + provenance) for the current-run
// aggregator.
//
// The expected topology mirrors the event and classifier predicates in .github/workflows/ci.yml exactly:
// which jobs run is derived from the event type plus the `changes` classifier outputs, never from fragment
// claims. Deliberately skipped jobs are listed separately so an absent fragment is distinguishable from a
// job that never existed.
//
// Trust semantics: on pull_request and merge_group runs this script executes from the PR-controlled merge
// checkout, so the produced metadata is labelled `shadow` and the merged record cannot claim trusted
// identity until a trusted post-run collector (phase B) validates it. On default-branch push/schedule/
// workflow_dispatch runs the checkout is the trusted workflow itself.

import { writeFileSync, mkdirSync, readFileSync, existsSync } from 'node:fs'
import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { SHARDED_JOB_GROUPS } from '../lib/merge-authority.mjs'

const value = (name) => process.env[name] ?? null
const enabled = (name) => process.env[name] === 'true'

function readEventContext() {
  const eventPath = value('GITHUB_EVENT_PATH')
  const configuredPr = value('PULL_REQUEST_NUMBER')
  const configuredBaseSha = value('PULL_REQUEST_BASE_SHA')
  const configuredHeadSha = value('PULL_REQUEST_HEAD_SHA')
  const fallback = {
    pr: configuredPr && /^[1-9][0-9]*$/.test(configuredPr) ? Number(configuredPr) : null,
    baseSha: configuredBaseSha && /^[0-9a-f]{40}$/.test(configuredBaseSha) ? configuredBaseSha : null,
    headSha: configuredHeadSha && /^[0-9a-f]{40}$/.test(configuredHeadSha) ? configuredHeadSha : null,
  }
  if (!eventPath || !existsSync(eventPath)) return fallback
  try {
    const event = JSON.parse(readFileSync(eventPath, 'utf8'))
    return {
      pr: event.pull_request?.number ?? fallback.pr,
      baseSha: event.pull_request?.base?.sha ?? fallback.baseSha,
      headSha: event.pull_request?.head?.sha ?? fallback.headSha,
    }
  } catch {
    return fallback
  }
}

function requireClassification(names) {
  for (const name of names) {
    if (process.env[name] !== 'true' && process.env[name] !== 'false') {
      console.error(`[ci-metrics] ${name} is missing or not a boolean; expected topology cannot be derived.`)
      process.exit(1)
    }
  }
}

const tree = value('METRICS_TREE_SHA')
if (!tree || !/^[0-9a-f]{40}$/.test(tree)) {
  console.error('[ci-metrics] METRICS_TREE_SHA is missing or malformed; run metadata will not be authoritative.')
  process.exit(1)
}

// The browser-pr shard count, read from the workflow this checkout runs (#1358): its instances follow the matrix, so
// changing the number of shards is a change to ci.yml alone. The list must be exactly 1..N for a size the verifier
// accepts (SHARDED_JOB_GROUPS); anything else, or a workflow that cannot be read, fails closed.
function browserPrShards() {
  const path = value('METRICS_WORKFLOW_PATH') ?? fileURLToPath(new URL('../../../.github/workflows/ci.yml', import.meta.url))
  const fail = (why) => {
    console.error(`[ci-metrics] browser-pr matrix: ${why}; expected topology cannot be derived.`)
    process.exit(1)
  }
  let lines = []
  try {
    lines = readFileSync(path, 'utf8').split(/\r?\n/)
  } catch {
    fail(`cannot read ${path}`)
  }
  const start = lines.indexOf('  browser-pr:')
  if (start < 0) fail('no browser-pr job in the workflow')
  const next = lines.findIndex((line, index) => index > start && /^  [a-z0-9-]+:$/.test(line))
  const lists = lines.slice(start, next < 0 ? lines.length : next).map((line) => /^        shard: \[([0-9, ]+)\]$/.exec(line)).filter(Boolean)
  if (lists.length !== 1) fail(`expected one shard list in the browser-pr job, found ${lists.length}`)
  const shards = lists[0][1].split(',').map((entry) => Number(entry.trim()))
  const accepted = SHARDED_JOB_GROUPS.find((group) => group.name === 'Browser journeys').acceptedShards
  if (!accepted.includes(shards.length) || shards.some((shard, index) => shard !== index + 1)) {
    fail(`shard: [${shards.join(', ')}] is not 1..N for a size the verifier accepts (${accepted.join(' or ')})`)
  }
  return shards.length
}
const browserPrInstances = Array.from({ length: browserPrShards() }, (_, index) => `browser-pr-${index + 1}`)

const event = value('GITHUB_EVENT_NAME') ?? ''
const ref = value('GITHUB_REF') ?? ''
const pullRequestNumber = value('PULL_REQUEST_NUMBER') ?? ''
// workflow_dispatch is used for both trusted PR readiness and manual diagnostics. The former follows
// the pull-request topology; only the latter may select the scheduled full-browser proof, and only when
// the dispatch explicitly requested it. An absent value preserves the workflow input's default for local
// invocations of this helper.
const fullDiagnostics = value('FULL_DIAGNOSTICS') !== 'false'
const eventContext = readEventContext()
const docsOnly = enabled('CLASS_DOCS_ONLY')
const backend = enabled('CLASS_BACKEND')
const client = enabled('CLASS_CLIENT')
const browser = enabled('CLASS_BROWSER')
const postgresql = enabled('CLASS_POSTGRESQL')

requireClassification(['CLASS_DOCS_ONLY', 'CLASS_BACKEND', 'CLASS_CLIENT', 'CLASS_BROWSER', 'CLASS_POSTGRESQL'])

const isPullRequestEvent = event === 'pull_request' || event === 'merge_group' ||
  (event === 'workflow_dispatch' && pullRequestNumber !== '')
const isPushEvent = event === 'push'
const isScheduledEvent = event === 'schedule' ||
  (event === 'workflow_dispatch' && pullRequestNumber === '' && fullDiagnostics)

const selected = []
const skipped = []

const addSelected = (group, instance, needs) => {
  selected.push({ group, instance, needs })
}
const addSkipped = (group, instance, reason) => {
  skipped.push({ group, instance, reason })
}

const skipJob = (group, instances, reason) => {
  for (const instance of instances) addSkipped(group, instance, reason)
}

const docsReason = 'documentation-only classification'

addSelected('changes', 'changes', [])
addSelected('metrics-tooling', 'metrics-tooling', [])

if (!docsOnly && backend) {
  addSelected('backend-api', 'backend-api-1', ['changes'])
  addSelected('backend-api', 'backend-api-2', ['changes'])
  addSelected('backend-api', 'backend-api-3', ['changes'])
  addSelected('backend-core-domain', 'backend-core-domain', ['changes'])
  addSelected('backend-core-infrastructure', 'backend-core-infrastructure', ['changes'])
} else {
  const reason = docsOnly ? docsReason : 'backend classification is false'
  skipJob('backend-api', ['backend-api-1', 'backend-api-2', 'backend-api-3'], reason)
  addSkipped('backend-core-domain', 'backend-core-domain', reason)
  addSkipped('backend-core-infrastructure', 'backend-core-infrastructure', reason)
}

if (!docsOnly && client) {
  addSelected('client', 'client', ['changes'])
} else {
  addSkipped('client', 'client', docsOnly ? docsReason : 'client classification is false')
}

if (!docsOnly) {
  addSelected('script-contracts', 'script-contracts', ['changes'])
} else {
  addSkipped('script-contracts', 'script-contracts', docsReason)
}

if (isPullRequestEvent && browser) {
  for (const instance of browserPrInstances) addSelected('browser-pr', instance, ['changes'])
} else {
  const reason = !browser ? 'browser classification is false' : `event ${event} does not run browser-pr`
  skipJob('browser-pr', browserPrInstances, reason)
}

if (!isPushEvent && browser) {
  addSelected('browser-production', 'browser-production', ['changes'])
} else {
  addSkipped('browser-production', 'browser-production', !browser ? 'browser classification is false' : 'push events skip browser-production')
}

// The browser-full matrix in ci.yml has six shards (#1340); build-run-meta.test.mjs compares the two.
const browserFullInstances = [1, 2, 3, 4, 5, 6].map((shard) => `browser-full-${shard}`)
if (isScheduledEvent && browser) {
  for (const instance of browserFullInstances) addSelected('browser-full', instance, ['changes'])
} else {
  const reason = !browser ? 'browser classification is false' : `event ${event} does not run browser-full`
  skipJob('browser-full', browserFullInstances, reason)
}

if (postgresql) {
  addSelected('postgresql-smoke', 'postgresql-smoke', ['changes'])
} else {
  addSkipped('postgresql-smoke', 'postgresql-smoke', 'postgresql classification is false')
}

if (isPushEvent) {
  addSelected('warm-chromium-cache', 'warm-chromium-cache', [])
} else {
  addSkipped('warm-chromium-cache', 'warm-chromium-cache', `event ${event} does not run warm-chromium-cache`)
}

// The gate always runs (if: always()). Its metrics dependency list mirrors the workflow's static needs
// minus the groups this event/classification deliberately skips, so partial runs produce a real critical
// path and never a "dependency group has no instances" contradiction.
const selectedGroups = new Set(selected.map((job) => job.group))
const gateNeeds = ['changes', 'metrics-tooling']
for (const group of ['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'browser-pr', 'browser-production', 'browser-full', 'postgresql-smoke']) {
  if (selectedGroups.has(group)) gateNeeds.push(group)
}
addSelected('gate', 'gate', gateNeeds)

// Provenance: PR-controlled checkouts can never self-attest. Default-branch contexts may.
let provenanceMode = 'shadow'
let provenanceReason = ''
if (event === 'pull_request' || event === 'merge_group') {
  provenanceReason = 'Same-workflow checkout is PR-controlled; trusted post-run validation is phase B.'
} else if (ref === 'refs/heads/main') {
  provenanceMode = 'trusted'
  provenanceReason = `Default-branch ${event} checkout is the trusted workflow itself.`
} else {
  provenanceReason = `${event} on ${ref} is not a default-branch context; treated as shadow until trusted validation exists.`
}

const meta = {
  schemaVersion: 'aerolink-ci-run-meta/v1',
  queueDelayMs: null,
  provenance: {
    mode: provenanceMode,
    reason: provenanceReason,
  },
  expectedRun: {
    id: Number(value('GITHUB_RUN_ID')),
    attempt: Number(value('GITHUB_RUN_ATTEMPT') ?? 1),
    event,
    sha: value('GITHUB_SHA'),
    tree,
    ref,
    pr: eventContext.pr,
    baseSha: eventContext.baseSha,
    headSha: eventContext.headSha,
    workflow: value('GITHUB_WORKFLOW'),
    workflowRef: value('GITHUB_WORKFLOW_REF'),
    repository: value('GITHUB_REPOSITORY'),
  },
  expectedJobs: selected,
  skippedJobs: skipped,
}

const output = value('METRICS_RUN_META_PATH')
if (!output) {
  console.error('[ci-metrics] METRICS_RUN_META_PATH is not set.')
  process.exit(1)
}
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, `${JSON.stringify(meta, null, 2)}\n`, 'utf8')
console.log(`[ci-metrics] Wrote run metadata with ${selected.length} expected job instances, ${skipped.length} deliberate skips, provenance=${provenanceMode}.`)
