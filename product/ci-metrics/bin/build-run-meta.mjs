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

const value = (name) => process.env[name] ?? null
const enabled = (name) => process.env[name] === 'true'

function readEventContext() {
  const eventPath = value('GITHUB_EVENT_PATH')
  if (!eventPath || !existsSync(eventPath)) return { pr: null, baseSha: null, headSha: null }
  try {
    const event = JSON.parse(readFileSync(eventPath, 'utf8'))
    return {
      pr: event.pull_request?.number ?? null,
      baseSha: event.pull_request?.base?.sha ?? null,
      headSha: event.pull_request?.head?.sha ?? null,
    }
  } catch {
    return { pr: null, baseSha: null, headSha: null }
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

const event = value('GITHUB_EVENT_NAME') ?? ''
const ref = value('GITHUB_REF') ?? ''
const eventContext = readEventContext()
const docsOnly = enabled('CLASS_DOCS_ONLY')
const backend = enabled('CLASS_BACKEND')
const client = enabled('CLASS_CLIENT')
const browser = enabled('CLASS_BROWSER')
const postgresql = enabled('CLASS_POSTGRESQL')

requireClassification(['CLASS_DOCS_ONLY', 'CLASS_BACKEND', 'CLASS_CLIENT', 'CLASS_BROWSER', 'CLASS_POSTGRESQL'])

const isPullRequestEvent = event === 'pull_request' || event === 'merge_group'
const isPushEvent = event === 'push'
const isScheduledEvent = event === 'schedule' || event === 'workflow_dispatch'

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
  for (let shard = 1; shard <= 4; shard += 1) addSelected('browser-pr', `browser-pr-${shard}`, ['changes'])
} else {
  const reason = !browser ? 'browser classification is false' : `event ${event} does not run browser-pr`
  skipJob('browser-pr', ['browser-pr-1', 'browser-pr-2', 'browser-pr-3', 'browser-pr-4'], reason)
}

if (!isPushEvent && browser) {
  addSelected('browser-production', 'browser-production', ['changes'])
} else {
  addSkipped('browser-production', 'browser-production', !browser ? 'browser classification is false' : 'push events skip browser-production')
}

if (isScheduledEvent && browser) {
  for (let shard = 1; shard <= 3; shard += 1) addSelected('browser-full', `browser-full-${shard}`, ['changes'])
} else {
  const reason = !browser ? 'browser classification is false' : `event ${event} does not run browser-full`
  skipJob('browser-full', ['browser-full-1', 'browser-full-2', 'browser-full-3'], reason)
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
for (const group of ['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'browser-pr', 'browser-production', 'postgresql-smoke']) {
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
