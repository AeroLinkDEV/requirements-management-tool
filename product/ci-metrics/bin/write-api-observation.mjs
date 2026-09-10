// Write one attempt-scoped API observation artifact from the same list-tests/partition/TRX files used by CI.
// This is advisory telemetry only. Missing inputs are recorded in the artifact and never change the test job.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { execFileSync } from 'node:child_process'
import { buildCurrentCountPlan, normalizeApiDiscovery, parseVstestList, API_DISCOVERY_SCHEMA_VERSION, API_PROJECT, API_REPOSITORY, API_DISCOVERY_SOURCE } from '../lib/api-packing-shadow.mjs'
import { API_OBSERVATION_ARTIFACT_SCHEMA, API_SHARD_COUNT, decodeXmlAttribute, looksLikeObservationCredential } from '../lib/api-observations.mjs'
import { parseTrx } from '../lib/trx.mjs'

const env = (name) => process.env[name] ?? ''
const [listedPath, partitionPath, trxPath, outputPath] = process.argv.slice(2)

function sha(value, label) {
  if (!/^[0-9a-f]{40}$/i.test(value ?? '')) throw new Error(`${label} was not a valid 40-character SHA.`)
  return value.toLowerCase()
}

function treeSha() {
  try { return sha(execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { encoding: 'utf8' }).trim(), 'tree SHA') } catch { return null }
}

function timing() {
  const path = env('API_OBSERVATION_TIMING_FILE')
  const markers = new Map()
  if (path && existsSync(path)) {
    for (const line of readFileSync(path, 'utf8').split(/\r?\n/).filter(Boolean)) {
      try {
        const marker = JSON.parse(line)
        if (typeof marker.name === 'string' && Number.isSafeInteger(marker.at) && marker.at >= 0) markers.set(marker.name, marker.at)
      } catch { /* malformed markers are visible through missing timing fields */ }
    }
  }
  return {
    jobStartMs: markers.get('job-start') ?? null,
    setupEndMs: markers.get('setup-end') ?? null,
    testEndMs: markers.get('test-end') ?? null,
    capturedAtMs: Date.now(),
  }
}

function partition(path) {
  if (!path || !existsSync(path)) return { expected: null, filter: null }
  const lines = readFileSync(path, 'utf8').split(/\r?\n/).filter((line) => line.length > 0)
  const expected = Number(lines[0] ?? NaN)
  return { expected: Number.isSafeInteger(expected) && expected >= 0 ? expected : null, filter: lines[1] ?? null }
}

function missingFiles() {
  return [
    ['list-tests', listedPath],
    ['partition', partitionPath],
    ['TRX', trxPath],
  ].filter(([, path]) => !path || !existsSync(path)).map(([name]) => name)
}

function xml(value) {
  return String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

// Keep the artifact useful for later TRX reconciliation without retaining failure messages, stacks, paths,
// or other arbitrary XML content. Existing failure diagnostics continue to retain the original TRX only in
// their separately scoped, failure-only artifact.
function safeTrx(xmlText) {
  const parsed = parseTrx(xmlText)
  const definitions = parsed.tests.map((test, index) => {
    const classNameValue = decodeXmlAttribute(test.className, `TRX result ${index}.className`)
    const nameValue = decodeXmlAttribute(test.name, `TRX result ${index}.name`)
    if (looksLikeObservationCredential(classNameValue) || looksLikeObservationCredential(nameValue)) throw new Error('TRX identity contains a credential-shaped value.')
    const id = `observation-${index + 1}`
    const className = xml(classNameValue)
    const method = xml(nameValue.split('(', 1)[0].split('.').at(-1))
    const durationMs = test.durationMs
    const duration = durationMs === null ? '' : (() => {
      const totalSeconds = Math.floor(durationMs / 1000)
      const days = Math.floor(totalSeconds / 86_400)
      const hours = Math.floor((totalSeconds % 86_400) / 3_600)
      const minutes = Math.floor((totalSeconds % 3_600) / 60)
      const seconds = totalSeconds % 60
      const prefix = days > 0 ? `${days}.` : ''
      return ` duration="${prefix}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(durationMs % 1000).padStart(3, '0')}0000"`
    })()
    return { id, className, method, name: xml(nameValue), outcome: xml(decodeXmlAttribute(test.outcome, `TRX result ${index}.outcome`)), duration }
  })
  const counters = parsed.totals
  return `<TestRun><ResultSummary><Counters total="${counters.total}" executed="${counters.executed}" passed="${counters.passed}" failed="${counters.failed}" notExecuted="${counters.skipped}" /></ResultSummary><TestDefinitions>${definitions.map((entry) => `<UnitTest id="${entry.id}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}</TestDefinitions><Results>${definitions.map((entry) => `<UnitTestResult testId="${entry.id}" testName="${entry.name}" outcome="${entry.outcome}"${entry.duration} />`).join('')}</Results></TestRun>`
}

function main() {
  if (!outputPath || !isAbsolute(outputPath)) throw new Error('An absolute observation output directory is required.')
  const repository = env('GITHUB_REPOSITORY') || API_REPOSITORY
  if (repository !== API_REPOSITORY) throw new Error(`GITHUB_REPOSITORY must be ${API_REPOSITORY}.`)
  const commitSha = sha(env('GITHUB_SHA'), 'GITHUB_SHA')
  const tree = treeSha()
  if (!tree) throw new Error('The exact tested Git tree could not be resolved.')
  const shard = Number(env('API_OBSERVATION_SHARD'))
  if (!Number.isSafeInteger(shard) || shard < 1 || shard > API_SHARD_COUNT) throw new Error('API_OBSERVATION_SHARD must be 1, 2, or 3.')
  const output = outputPath
  mkdirSync(output, { recursive: true })
  const missing = missingFiles()
  let sanitizedTrx = null
  if (existsSync(trxPath)) {
    try { sanitizedTrx = safeTrx(readFileSync(trxPath, 'utf8')) } catch (error) { missing.push(`TRX parse failed: ${error.message}`) }
  }
  let discovery = null
  let plan = null
  if (existsSync(listedPath)) {
    const tests = parseVstestList(readFileSync(listedPath, 'utf8'))
    discovery = normalizeApiDiscovery({
      schemaVersion: API_DISCOVERY_SCHEMA_VERSION,
      repository,
      source: API_DISCOVERY_SOURCE,
      project: API_PROJECT,
      commitSha,
      treeSha: tree,
      tests,
    })
    plan = buildCurrentCountPlan(discovery, API_SHARD_COUNT)
  }
  const actual = partition(partitionPath)
  const result = {
    schemaVersion: API_OBSERVATION_ARTIFACT_SCHEMA,
    repository,
    run: {
      id: Number(env('GITHUB_RUN_ID')) || null,
      attempt: Number(env('GITHUB_RUN_ATTEMPT')) || 1,
      event: env('GITHUB_EVENT_NAME') || 'unknown',
      sha: commitSha,
      tree,
      workflow: env('GITHUB_WORKFLOW') || 'Product quality gate',
      workflowRef: env('GITHUB_WORKFLOW_REF') || 'unknown',
    },
    shard,
    shardCount: API_SHARD_COUNT,
    discovery,
    plan,
    actual,
    timing: timing(),
    constraints: {
      collectionTopology: 'unknown-unobserved',
      reason: 'VSTest output does not expose xUnit collection or fixture topology; no grouping claim is made.',
    },
    toolchain: {
      runnerOs: env('API_OBSERVATION_RUNNER_OS') || env('RUNNER_OS') || null,
      image: env('API_OBSERVATION_IMAGE') || env('ImageOS') || null,
      dotnet: env('API_OBSERVATION_DOTNET') || '10.0.x',
    },
    missing,
  }
  writeFileSync(join(output, 'api-observation.json'), `${JSON.stringify(result, null, 2)}\n`, 'utf8')
  if (sanitizedTrx !== null) writeFileSync(join(output, 'shard.trx'), sanitizedTrx, 'utf8')
  console.log(`[ci-metrics] API observation artifact: shard ${shard}; inventory=${discovery?.tests.length ?? 'unavailable'}; missing=${missing.join(',') || 'none'}`)
}

try { main() } catch (error) {
  console.error(`[ci-metrics] API observation artifact unavailable: ${error.message}`)
  process.exit(0)
}

function isAbsolute(path) {
  return /^[A-Za-z]:[\\/]/.test(path) || path.startsWith('\\\\') || path.startsWith('/')
}
