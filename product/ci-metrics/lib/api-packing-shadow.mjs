// Advisory API shard-packing analysis for #942.
//
// This module deliberately does not select or execute tests.  It compares the current class-count
// partition with a deterministic candidate that uses duration observations as a ranking signal and
// test count as a second, explicit component.  Duration sums are not wall-clock predictions: fixtures,
// database setup, runner contention and xUnit scheduling make that inference unsafe.

import { createHash } from 'node:crypto'

export const API_PACKING_SHADOW_SCHEMA_VERSION = 'aerolink-api-packing-shadow/v1'
export const API_DISCOVERY_SCHEMA_VERSION = 'aerolink-api-discovery/v1'
export const API_OBSERVATIONS_SCHEMA_VERSION = 'aerolink-api-packing-observations/v1'
export const API_REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
export const API_DISCOVERY_SOURCE = 'dotnet-list-tests'
export const API_PROJECT = 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj'
export const DEFAULT_API_SHARD_COUNT = 3
export const MIN_MATCHED_SOURCE_RUNS = 8

const SHA40 = /^[0-9a-f]{40}$/i
const SHA64 = /^[0-9a-f]{64}$/i
const MAX_TESTS = 30_000
const MAX_CLASS_NAME = 300
const MAX_TEST_NAME = 1_000
const MAX_COHORT = 80

function compareStrings(a, b) {
  return a < b ? -1 : a > b ? 1 : 0
}

function requireObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`)
  return value
}

function requireNonEmptyString(value, label, max = 256) {
  if (typeof value !== 'string' || value.length === 0 || value.length > max || /[\r\n]/.test(value)) {
    throw new Error(`${label} must be a bounded non-empty string.`)
  }
  return value
}

function requireSha(value, label, length = 40) {
  const expression = length === 40 ? SHA40 : SHA64
  if (typeof value !== 'string' || !expression.test(value)) throw new Error(`${label} must be a ${length}-character hexadecimal SHA.`)
  return value.toLowerCase()
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 1) throw new Error(`${label} must be a safe positive integer.`)
  return value
}

function canonicalJson(value) {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return JSON.stringify(value)
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new Error('Canonical JSON cannot contain a non-finite number.')
    return JSON.stringify(value)
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`
  if (typeof value === 'object') {
    return `{${Object.keys(value).sort(compareStrings).map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`
  }
  throw new Error('Canonical JSON cannot contain undefined or a function.')
}

export function sha256Json(value) {
  return createHash('sha256').update(canonicalJson(value), 'utf8').digest('hex')
}

function classNameForTest(testName) {
  const withoutArguments = testName.split('(', 1)[0]
  const dot = withoutArguments.lastIndexOf('.')
  if (dot <= 0 || dot === withoutArguments.length - 1) throw new Error(`Cannot derive a test class from '${testName}'.`)
  const className = withoutArguments.slice(0, dot)
  if (!className.startsWith('AeroLink.')) throw new Error(`Test '${testName}' is outside the API test namespace.`)
  if (!/^AeroLink(?:\.[A-Za-z_][A-Za-z0-9_]*)+$/.test(className)) throw new Error('Ambiguous or unsafe API class/filter identity.')
  if (className.length > MAX_CLASS_NAME) throw new Error(`Test class '${className}' is too long.`)
  return className
}

function normalizeTests(tests) {
  if (!Array.isArray(tests) || tests.length === 0 || tests.length > MAX_TESTS) throw new Error(`discovery.tests must contain 1..${MAX_TESTS} tests.`)
  const names = []
  const seen = new Set()
  for (const [index, entry] of tests.entries()) {
    const name = typeof entry === 'string' ? entry : requireObject(entry, `discovery.tests[${index}]`).fullyQualifiedName
    requireNonEmptyString(name, `discovery.tests[${index}].fullyQualifiedName`, MAX_TEST_NAME)
    if (seen.has(name)) throw new Error(`discovery.tests contains duplicate test '${name}'.`)
    classNameForTest(name)
    seen.add(name)
    names.push(name)
  }
  return names.sort(compareStrings)
}

/**
 * Parse the bounded API-test lines emitted by `dotnet test --list-tests`.  This helper only parses text
 * supplied by its caller; it does not execute VSTest or authenticate the resulting inventory.
 */
export function parseVstestList(text) {
  if (typeof text !== 'string' || text.length === 0) throw new Error('VSTest list output must be non-empty text.')
  const tests = []
  for (const line of text.split(/\r?\n/)) {
    // Keep the same prefix boundary as the CI grep. normalizeApiDiscovery then rejects any captured
    // line that cannot derive an AeroLink.* class, instead of silently dropping a CI-selected line.
    if (!line.startsWith('    AeroLink')) continue
    const name = line.slice(4)
    if (name.length > MAX_TEST_NAME || /[\r\n]/.test(name)) throw new Error('VSTest list contains an invalid test name.')
    tests.push(name)
  }
  if (tests.length === 0) throw new Error('VSTest list contained no AeroLink API tests.')
  return tests
}

/**
 * Normalize a live `dotnet test --list-tests` inventory.  The inventory is the coverage authority for
 * both plans; no static class inventory can stand in for it because test classes can be added by source.
 */
export function normalizeApiDiscovery(discovery) {
  const value = requireObject(discovery, 'discovery')
  if (value.schemaVersion !== API_DISCOVERY_SCHEMA_VERSION) throw new Error(`discovery.schemaVersion must be ${API_DISCOVERY_SCHEMA_VERSION}.`)
  if (value.repository !== API_REPOSITORY) throw new Error(`discovery.repository must be ${API_REPOSITORY}.`)
  if (value.source !== API_DISCOVERY_SOURCE) throw new Error(`discovery.source must be ${API_DISCOVERY_SOURCE}.`)
  if (value.project !== API_PROJECT) throw new Error(`discovery.project must be ${API_PROJECT}.`)
  const commitSha = requireSha(value.commitSha, 'discovery.commitSha')
  const treeSha = requireSha(value.treeSha, 'discovery.treeSha')
  const tests = normalizeTests(value.tests)
  const byClass = new Map()
  for (const fullyQualifiedName of tests) {
    const className = classNameForTest(fullyQualifiedName)
    const list = byClass.get(className) ?? []
    list.push(fullyQualifiedName)
    byClass.set(className, list)
  }
  const classes = [...byClass.entries()]
    .map(([className, classTests]) => ({ className, tests: classTests, testCount: classTests.length }))
    .sort((a, b) => compareStrings(a.className, b.className))
  for (const entry of classes) {
    if (classes.some((other) => other !== entry && `${other.className}.`.includes(`${entry.className}.`))) {
      throw new Error('Ambiguous API class substring filters overlap.')
    }
  }
  const collections = []
  const classesByName = new Set(classes.map((entry) => entry.className))
  const claimedCollectionClasses = new Set()
  if (value.collections !== undefined) {
    if (!Array.isArray(value.collections) || value.collections.length > 100) throw new Error('discovery.collections must be an array of at most 100 entries.')
    for (const [index, raw] of value.collections.entries()) {
      const collection = requireObject(raw, `discovery.collections[${index}]`)
      const name = requireNonEmptyString(collection.name, `discovery.collections[${index}].name`, MAX_CLASS_NAME)
      if (collections.some((entry) => entry.name === name)) throw new Error(`discovery collections repeats '${name}'.`)
      if (collection.preserveTogether !== true) throw new Error(`discovery collection '${name}' must explicitly set preserveTogether=true.`)
      if (!Array.isArray(collection.classNames) || collection.classNames.length < 1) throw new Error(`discovery collection '${name}' must list at least one class.`)
      const rawClassNames = collection.classNames.map((className, classIndex) => requireNonEmptyString(className, `discovery.collections[${index}].classNames[${classIndex}]`, MAX_CLASS_NAME))
      if (new Set(rawClassNames).size !== rawClassNames.length) throw new Error(`discovery collection '${name}' repeats a class.`)
      const classNames = rawClassNames.sort(compareStrings)
      for (const className of classNames) {
        if (!classesByName.has(className)) throw new Error(`discovery collection '${name}' names unknown class '${className}'.`)
        if (claimedCollectionClasses.has(className)) throw new Error(`discovery class '${className}' appears in more than one collection.`)
        claimedCollectionClasses.add(className)
      }
      collections.push({ name, classNames, preserveTogether: true })
    }
    collections.sort((a, b) => compareStrings(a.name, b.name))
  }
  const digest = sha256Json({
    schemaVersion: API_DISCOVERY_SCHEMA_VERSION,
    repository: API_REPOSITORY,
    source: API_DISCOVERY_SOURCE,
    project: API_PROJECT,
    tests,
    collections,
  })
  if (value.discoveryDigest !== undefined && value.discoveryDigest !== digest) throw new Error('discovery.discoveryDigest does not match the normalized test inventory.')
  return { schemaVersion: API_DISCOVERY_SCHEMA_VERSION, repository: API_REPOSITORY, source: API_DISCOVERY_SOURCE, project: API_PROJECT, commitSha, treeSha, tests, classes, collections, digest }
}

function normalizeShardCount(shardCount) {
  if (!Number.isInteger(shardCount) || shardCount < 2 || shardCount > 16) throw new Error('shardCount must be an integer from 2 through 16.')
  return shardCount
}

function emptyShard(shard) {
  return { shard, classes: [], tests: [], caseCount: 0, durationLoadMs: null, compositeLoad: null, filter: '' }
}

function coverageForPlan(plan, discovery) {
  const expected = discovery.tests.length
  const assigned = plan.shards.flatMap((shard) => shard.tests)
  const counts = new Map()
  for (const test of assigned) counts.set(test, (counts.get(test) ?? 0) + 1)
  const missing = discovery.tests.filter((test) => !counts.has(test))
  const duplicated = [...counts.entries()].filter(([, count]) => count !== 1).map(([test, count]) => ({ test, count }))
  const classShardCounts = new Map()
  for (const shard of plan.shards) {
    for (const className of shard.classes) classShardCounts.set(className, (classShardCounts.get(className) ?? 0) + 1)
  }
  const splitClasses = [...classShardCounts.entries()].filter(([, count]) => count !== 1).map(([className, count]) => ({ className, count }))
  return {
    complete: assigned.length === expected && missing.length === 0 && duplicated.length === 0 && splitClasses.length === 0,
    expected,
    assigned: assigned.length,
    unique: counts.size,
    missing,
    duplicated,
    splitClasses,
  }
}

function materializePlan({ discovery, shardCount, assignments, weights = null, algorithm, grouping, filterOrder = null }) {
  const shards = Array.from({ length: shardCount }, (_, index) => emptyShard(index + 1))
  for (const entry of discovery.classes) {
    const shardNumber = assignments.get(entry.className)
    if (!Number.isInteger(shardNumber) || shardNumber < 1 || shardNumber > shardCount) throw new Error(`Class '${entry.className}' has no valid shard assignment.`)
    const shard = shards[shardNumber - 1]
    shard.classes.push(entry.className)
    shard.tests.push(...entry.tests)
    shard.caseCount += entry.testCount
    if (weights) {
      const weight = weights.get(entry.className)
      shard.durationLoadMs += weight.durationMs ?? 0
      shard.compositeLoad += weight.compositeWeight
    }
  }
  for (const shard of shards) {
    shard.classes.sort(compareStrings)
    shard.tests.sort(compareStrings)
    const filterClasses = filterOrder ? filterOrder.filter((name) => shard.classes.includes(name)) : shard.classes
    shard.filter = filterClasses.map((className) => `FullyQualifiedName~${className}.`).join('|')
    if (!shard.filter) throw new Error('API packing produced an empty shard filter.')
    if (!weights) {
      shard.durationLoadMs = null
      shard.compositeLoad = null
    } else {
      shard.durationLoadMs = Math.round(shard.durationLoadMs)
      shard.compositeLoad = Math.round(shard.compositeLoad * 100) / 100
    }
  }
  const plan = { algorithm, shardCount, grouping, shards }
  plan.coverage = coverageForPlan(plan, discovery)
  if (!plan.coverage.complete) throw new Error(`${algorithm} produced an incomplete or overlapping test plan.`)
  return plan
}

function packingUnits(discovery, weights = null) {
  const byClass = new Map(discovery.classes.map((entry) => [entry.className, entry]))
  const claimed = new Set()
  const units = []
  for (const collection of discovery.collections ?? []) {
    const entries = collection.classNames.map((className) => byClass.get(className))
    const unit = {
      unitName: `collection:${collection.name}`,
      classNames: entries.map((entry) => entry.className).sort(compareStrings),
      testCount: entries.reduce((sum, entry) => sum + entry.testCount, 0),
      durationMs: weights ? entries.reduce((sum, entry) => sum + weights.get(entry.className).durationMs, 0) : null,
    }
    units.push(unit)
    for (const entry of entries) claimed.add(entry.className)
  }
  for (const entry of discovery.classes) {
    if (claimed.has(entry.className)) continue
    units.push({ unitName: `class:${entry.className}`, classNames: [entry.className], testCount: entry.testCount, durationMs: weights ? weights.get(entry.className).durationMs : null })
  }
  return units
}

/** Build the exact current count-first class partition used by ci.yml. */
export function buildCurrentCountPlan(discovery, shardCount = DEFAULT_API_SHARD_COUNT) {
  const normalized = normalizeApiDiscovery(discovery)
  const count = normalizeShardCount(shardCount)
  const loads = Array.from({ length: count }, () => 0)
  const assignments = new Map()
  // This intentionally mirrors the individual-class greedy packer in ci.yml. Collection metadata is
  // not part of the current execution selector and must never silently change the baseline plan.
  const classes = [...normalized.classes].sort((a, b) => b.testCount - a.testCount || compareStrings(a.className, b.className))
  for (const entry of classes) {
    let best = 0
    for (let index = 1; index < count; index += 1) if (loads[index] < loads[best]) best = index
    loads[best] += entry.testCount
    assignments.set(entry.className, best + 1)
  }
  return materializePlan({
    discovery: normalized,
    shardCount: count,
    assignments,
    filterOrder: classes.map((entry) => entry.className),
    algorithm: 'current-count-greedy',
    grouping: {
      mode: 'individual-class',
      collectionConstraintsApplied: false,
      collectionsIgnoredByCurrentCi: normalized.collections.map((collection) => collection.name),
    },
  })
}

function invalidEvidence(reason) {
  return { structurallyCompatible: false, reasons: [reason], sourceRuns: [], weights: new Map(), missingWeightClasses: [], completeWeightCoverage: false, cohort: null, matchedRunCount: 0, minimumEvidenceCountMet: false, adoptionEligible: false }
}

/**
 * Validate observations against the live discovery.  Any missing/stale/unverified evidence is a
 * fallback condition.  It never authorizes a scheduler change and never fabricates a weight.
 */
export function evaluateApiPackingObservations({ discovery, observations, minSourceRuns = MIN_MATCHED_SOURCE_RUNS }) {
  const normalized = normalizeApiDiscovery(discovery)
  const fallback = (reason) => invalidEvidence(reason)
  try {
    if (!Number.isSafeInteger(minSourceRuns) || minSourceRuns < 1 || minSourceRuns > 100) return fallback('minSourceRuns must be a safe integer from 1 through 100.')
    const value = requireObject(observations, 'observations')
    if (value.schemaVersion !== API_OBSERVATIONS_SCHEMA_VERSION) return fallback(`observations.schemaVersion must be ${API_OBSERVATIONS_SCHEMA_VERSION}.`)
    if (value.repository !== API_REPOSITORY) return fallback(`observations.repository must be ${API_REPOSITORY}.`)
    if (value.provenance !== 'offline-shadow-claim') return fallback('observations.provenance must explicitly be offline-shadow-claim.')
    const cohort = requireNonEmptyString(value.cohort, 'observations.cohort', MAX_COHORT)
    if (!Array.isArray(value.sourceRuns) || value.sourceRuns.length === 0 || value.sourceRuns.length > 100) return fallback('observations.sourceRuns must contain 1..100 source runs.')
    const sourceRuns = []
    const sourceRunIds = new Set()
    for (const [index, raw] of value.sourceRuns.entries()) {
      const run = requireObject(raw, `observations.sourceRuns[${index}]`)
      const runId = String(requirePositiveInteger(run.runId, `observations.sourceRuns[${index}].runId`))
      if (sourceRunIds.has(runId)) return fallback(`observations.sourceRuns repeats run ${runId}.`)
      sourceRunIds.add(runId)
      if (run.event !== 'merge_group') return fallback(`source run ${runId} is not a merge_group run.`)
      if (run.workflow !== 'Product quality gate') return fallback(`source run ${runId} is not from Product quality gate.`)
      if (run.conclusion !== 'success') return fallback(`source run ${runId} did not conclude success.`)
      if (run.cohort !== cohort) return fallback(`source run ${runId} has a mixed cohort.`)
      const commitSha = requireSha(run.commitSha, `source run ${runId}.commitSha`)
      const treeSha = requireSha(run.treeSha, `source run ${runId}.treeSha`)
      const discoveryDigest = requireSha(run.discoveryDigest, `source run ${runId}.discoveryDigest`, 64)
      if (discoveryDigest !== normalized.digest) return fallback(`source run ${runId} has stale discovery ${discoveryDigest}; current discovery is ${normalized.digest}.`)
      if (run.validatedTree !== true) return fallback(`source run ${runId} lacks the required validatedTree claim.`)
      sourceRuns.push({ runId, event: run.event, workflow: run.workflow, conclusion: run.conclusion, cohort, commitSha, treeSha, discoveryDigest, treeEvidence: 'unverified-offline-claim' })
    }
    if (!Array.isArray(value.weights) || value.weights.length === 0) return fallback('observations.weights is missing.')
    const expectedClasses = new Set(normalized.classes.map((entry) => entry.className))
    const weights = new Map()
    for (const [index, raw] of value.weights.entries()) {
      const weight = requireObject(raw, `observations.weights[${index}]`)
      const className = requireNonEmptyString(weight.className, `observations.weights[${index}].className`, MAX_CLASS_NAME)
      if (!expectedClasses.has(className)) return fallback(`weight names unknown class '${className}'.`)
      if (weights.has(className)) return fallback(`weight repeats class '${className}'.`)
      if (typeof weight.durationMs !== 'number' || !Number.isFinite(weight.durationMs) || weight.durationMs <= 0 || weight.durationMs > 1e12) return fallback(`weight for '${className}' has an invalid durationMs.`)
      if (!Array.isArray(weight.sourceRunIds) || weight.sourceRunIds.length === 0) return fallback(`weight for '${className}' has no source runs.`)
      const referenced = new Set(weight.sourceRunIds.map((runId) => String(runId)))
      if (referenced.size !== weight.sourceRunIds.length) return fallback(`weight for '${className}' repeats a source run.`)
      if ([...referenced].some((runId) => !sourceRunIds.has(runId))) return fallback(`weight for '${className}' references an unknown source run.`)
      if (referenced.size !== sourceRunIds.size) return fallback(`weight for '${className}' does not cover every source run.`)
      weights.set(className, { className, durationMs: weight.durationMs, sourceRunIds: [...referenced].sort(compareStrings) })
    }
    const missingWeightClasses = [...expectedClasses].filter((className) => !weights.has(className)).sort(compareStrings)
    const completeWeightCoverage = missingWeightClasses.length === 0
    // The median observed duration per case is only a scale for a composite score.  It is not a
    // prediction of wall time and is never rendered as one.
    const observedPerCase = normalized.classes
      .filter((entry) => weights.has(entry.className))
      .map((entry) => weights.get(entry.className).durationMs / entry.testCount)
      .sort((a, b) => a - b)
    if (observedPerCase.length === 0) return fallback('observations contain no usable class durations.')
    const perCase = observedPerCase
    const midpoint = Math.floor(perCase.length / 2)
    const durationPerCaseScale = perCase.length % 2 === 1 ? perCase[midpoint] : (perCase[midpoint - 1] + perCase[midpoint]) / 2
    for (const entry of normalized.classes) {
      const weight = weights.get(entry.className) ?? { className: entry.className, durationMs: null, sourceRunIds: [] }
      // The retained telemetry is bounded to the slowest classes.  An absent tail class stays in the
      // count-based candidate; it never receives a fabricated duration.  A partial candidate is visible
      // for investigation but is never adoption-eligible.
      weight.compositeWeight = weight.durationMs === null ? entry.testCount : (weight.durationMs / durationPerCaseScale) + entry.testCount
      weights.set(entry.className, weight)
    }
    const reasons = []
    if (!completeWeightCoverage) reasons.push(`${missingWeightClasses.length} classes have no duration weight; those classes use count-based placement.`)
    const minimumEvidenceCountMet = sourceRuns.length >= minSourceRuns
    if (!minimumEvidenceCountMet) reasons.push(`Only ${sourceRuns.length} matched source runs; ${minSourceRuns} are required for the minimum evidence count.`)
    // Offline JSON cannot authenticate GitHub run identity, tree provenance, discovery freshness, or cohort
    // comparability. The candidate remains a hypothetical calculation and is never adoption-eligible here.
    return { structurallyCompatible: true, reasons, sourceRuns, weights, missingWeightClasses, completeWeightCoverage, cohort, matchedRunCount: sourceRuns.length, durationPerCaseScale, minimumEvidenceCountMet, adoptionEligible: false }
  } catch (error) {
    return fallback(error instanceof Error ? error.message : 'Observation evidence is malformed.')
  }
}

function buildCompositePlan(discovery, shardCount, evidence) {
  const loads = Array.from({ length: shardCount }, () => ({ composite: 0, cases: 0 }))
  const assignments = new Map()
  const units = packingUnits(discovery, evidence.weights).sort((a, b) => {
    const weightA = a.durationMs / evidence.durationPerCaseScale + a.testCount
    const weightB = b.durationMs / evidence.durationPerCaseScale + b.testCount
    return weightB - weightA || b.testCount - a.testCount || compareStrings(a.unitName, b.unitName)
  })
  for (const unit of units) {
    const weight = unit.durationMs / evidence.durationPerCaseScale + unit.testCount
    let best = 0
    for (let index = 1; index < shardCount; index += 1) {
      if (loads[index].composite < loads[best].composite ||
        (loads[index].composite === loads[best].composite && loads[index].cases < loads[best].cases)) best = index
    }
    loads[best].composite += weight
    loads[best].cases += unit.testCount
    for (const className of unit.classNames) assignments.set(className, best + 1)
  }
  return materializePlan({
    discovery,
    shardCount,
    assignments,
    weights: evidence.weights,
    algorithm: 'composite-duration-and-case-shadow',
    grouping: {
      mode: discovery.collections.length > 0 ? 'hypothetical-collection-aware' : 'individual-class',
      collectionConstraintsApplied: discovery.collections.length > 0,
      collectionGroupsUnverified: discovery.collections.length > 0,
      collections: discovery.collections.map((collection) => collection.name),
    },
  })
}

function maxOf(shards, field) {
  return Math.max(...shards.map((shard) => shard[field]))
}

function classAssignments(plan) {
  const assignments = new Map()
  for (const shard of plan.shards) for (const className of shard.classes) assignments.set(className, shard.shard)
  return assignments
}

function collectionGroupingChanges(discovery, currentPlan, proposedPlan) {
  const current = classAssignments(currentPlan)
  const proposed = classAssignments(proposedPlan)
  return discovery.collections.map((collection) => {
    const currentShards = [...new Set(collection.classNames.map((className) => current.get(className)))].sort((a, b) => a - b)
    const proposedShards = [...new Set(collection.classNames.map((className) => proposed.get(className)))].sort((a, b) => a - b)
    return {
      name: collection.name,
      classNames: collection.classNames,
      currentShards,
      proposedShards,
      changed: currentShards.length !== proposedShards.length || currentShards.some((shard, index) => shard !== proposedShards[index]),
      source: 'caller-supplied-hypothetical',
    }
  })
}

/** Build a complete, advisory-only comparison of current and candidate packing. */
export function buildApiPackingShadowReport({ discovery, observations, shardCount = DEFAULT_API_SHARD_COUNT, minSourceRuns = MIN_MATCHED_SOURCE_RUNS }) {
  const normalized = normalizeApiDiscovery(discovery)
  const countPlan = buildCurrentCountPlan(normalized, shardCount)
  const evidence = evaluateApiPackingObservations({ discovery: normalized, observations, minSourceRuns })
  const proposedPlan = evidence.structurallyCompatible ? buildCompositePlan(normalized, shardCount, evidence) : countPlan
  const fallbackUsed = !evidence.structurallyCompatible
  return {
    schemaVersion: API_PACKING_SHADOW_SCHEMA_VERSION,
    repository: API_REPOSITORY,
    discovery: {
      authenticity: 'unverified-offline-claim',
      freshDiscoveryClaim: false,
      source: normalized.source,
      project: normalized.project,
      commitSha: normalized.commitSha,
      treeSha: normalized.treeSha,
      digest: normalized.digest,
      testCount: normalized.tests.length,
      classCount: normalized.classes.length,
    },
    shardCount,
    mode: 'shadow-only',
    executionSelectorChanged: false,
    noSpeedupClaim: true,
    measurementLimits: [
      'Class duration sums are rank signals only; they are not additive wall-clock predictions.',
      'This offline report does not execute dotnet --list-tests; supplied discovery is an unverified claim.',
      'Caller-supplied run, tree, cohort and validatedTree fields are unverified claims, not authenticated GitHub evidence.',
      'Caller-supplied collection groups are hypothetical and unverified; grouped adoption is unsupported.',
      'A candidate plan does not alter CI execution, required checks, merge authority, or shard count.',
      'Adoption eligibility is permanently false for this offline prototype; a future trusted collector must be separate work.',
    ],
    evidence: {
      structurallyCompatible: evidence.structurallyCompatible,
      authenticity: 'unverified-offline-claims',
      fallbackUsed,
      reasons: evidence.reasons,
      cohort: evidence.cohort,
      matchedRunCount: evidence.matchedRunCount,
      minimumEvidenceCountMet: evidence.minimumEvidenceCountMet,
      completeWeightCoverage: evidence.completeWeightCoverage,
      missingWeightClasses: evidence.missingWeightClasses,
      sourceRuns: evidence.sourceRuns.map((run) => ({ runId: run.runId, commitSha: run.commitSha, treeSha: run.treeSha, discoveryDigest: run.discoveryDigest, cohort: run.cohort, event: run.event, workflow: run.workflow, conclusion: run.conclusion, treeEvidence: run.treeEvidence })),
      durationPerCaseScaleMs: evidence.durationPerCaseScale ?? null,
      adoptionEligible: false,
    },
    currentPlan: countPlan,
    proposedPlan,
    comparison: {
      currentMaxCaseCount: maxOf(countPlan.shards, 'caseCount'),
      proposedMaxCaseCount: maxOf(proposedPlan.shards, 'caseCount'),
      currentCaseCounts: countPlan.shards.map((shard) => shard.caseCount),
      proposedCaseCounts: proposedPlan.shards.map((shard) => shard.caseCount),
      proposedDurationLoadsMs: evidence.structurallyCompatible ? proposedPlan.shards.map((shard) => shard.durationLoadMs) : null,
      proposedCompositeLoads: evidence.structurallyCompatible ? proposedPlan.shards.map((shard) => shard.compositeLoad) : null,
      collectionGrouping: collectionGroupingChanges(normalized, countPlan, proposedPlan),
    },
    recommendation: fallbackUsed ? 'retain-current-count-plan-until-evidence-is-repaired' : 'candidate-is-shadow-only-and-requires-independent-adoption-review',
  }
}

function markdownCell(value) {
  return String(value).replace(/\|/g, '\\|').replace(/[\r\n]/g, ' ')
}

function renderPlan(lines, title, plan) {
  lines.push(`## ${title}`)
  lines.push('')
  lines.push(`Algorithm: \`${plan.algorithm}\`; grouping: \`${plan.grouping.mode}\`; exact coverage: ${plan.coverage.complete ? 'complete' : 'INVALID'}.`)
  lines.push('')
  lines.push('| Shard | Cases | Classes | Duration load (ms) | Composite load | Filter |')
  lines.push('|---:|---:|---:|---:|---:|---|')
  for (const shard of plan.shards) {
    lines.push(`| ${shard.shard} | ${shard.caseCount} | ${shard.classes.length} | ${shard.durationLoadMs ?? '—'} | ${shard.compositeLoad ?? '—'} | ${markdownCell(shard.filter)} |`)
  }
  lines.push('')
}

export function renderApiPackingShadowMarkdown(report) {
  const lines = ['# API packing shadow report', '', `- Mode: **${report.mode}**; execution selector changed: **${report.executionSelectorChanged}**`, `- Discovery: ${report.discovery.testCount} tests across ${report.discovery.classCount} classes; commit \`${report.discovery.commitSha}\`; tree \`${report.discovery.treeSha}\`; digest \`${report.discovery.digest}\``, `- Discovery authenticity: **${report.discovery.authenticity}**; fresh discovery executed: **${report.discovery.freshDiscoveryClaim}**`, `- Evidence: ${report.evidence.structurallyCompatible ? 'structurally compatible offline claims' : 'fallback to current count plan'}; authenticity: **${report.evidence.authenticity}**; cohort claim: ${report.evidence.cohort ?? '—'}; source-run claims: ${report.evidence.matchedRunCount}`, `- Minimum evidence count met: **${report.evidence.minimumEvidenceCountMet}**; adoption eligible: **false**; speedup claim: **none**`, '']
  if (report.evidence.reasons.length > 0) {
    lines.push('## Evidence disposition')
    lines.push('')
    for (const reason of report.evidence.reasons) lines.push(`- ${markdownCell(reason)}`)
    lines.push('')
  }
  renderPlan(lines, 'Current count plan', report.currentPlan)
  renderPlan(lines, 'Proposed composite shadow plan', report.proposedPlan)
  lines.push('## Collection grouping effects')
  lines.push('')
  lines.push('Collection groups are caller-supplied hypothetical claims. They affect the proposed shadow plan only and are not complete or adoption evidence.')
  lines.push('')
  if (report.comparison.collectionGrouping.length === 0) {
    lines.push('No collection grouping claims were supplied.')
  } else {
    lines.push('| Collection claim | Current shards | Proposed shards | Changed |')
    lines.push('|---|---:|---:|---|')
    for (const entry of report.comparison.collectionGrouping) lines.push(`| ${markdownCell(entry.name)} | ${entry.currentShards.join(', ')} | ${entry.proposedShards.join(', ')} | ${entry.changed} |`)
  }
  lines.push('')
  lines.push('## Coverage and limits')
  lines.push('')
  lines.push(`Both plans must cover exactly ${report.discovery.testCount} discovered tests with no duplicate or split class. Current: ${report.currentPlan.coverage.complete}; proposed: ${report.proposedPlan.coverage.complete}.`)
  lines.push('')
  for (const limit of report.measurementLimits) lines.push(`- ${markdownCell(limit)}`)
  lines.push('')
  lines.push(`Recommendation: **${report.recommendation}**.`)
  lines.push('')
  return lines.join('\n')
}
