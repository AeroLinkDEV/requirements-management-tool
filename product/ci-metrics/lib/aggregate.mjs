// Aggregates one workflow run's job fragments into a bounded merged record and a concise Markdown summary.
//
// Missing data is represented as missing with a reason. Nothing here ever interprets "no fragment" as a
// zero-duration success, and no value from a fragment is used as a command, path, expression, or script.
// Expected-job metadata (trusted default-branch input) is required to distinguish an absent fragment from a
// job that never existed, and any uncertainty makes the critical path unavailable rather than numerically
// smaller.

import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { validationErrors, MAX_FRAGMENT_BYTES } from './fragment.mjs'

export const RUN_SCHEMA_VERSION = 'aerolink-ci-run/v2'
export const MAX_FRAGMENTS = 200
export const MAX_TOTAL_INPUT_BYTES = 20 * 1024 * 1024
export const MAX_MERGED_BYTES = 512 * 1024
export const MAX_MISSING_ENTRIES = 100
export const MAX_MARKDOWN_BYTES = 128 * 1024

export function readFragments(directory) {
  const entries = []
  let files = []
  try {
    files = readdirSync(directory)
  } catch (error) {
    return { fragments: [], missing: [{ job: 'fragments-directory', reason: `Could not read fragment directory: ${error.message}` }], truncated: false }
  }
  let totalBytes = 0
  let truncated = false
  for (const file of files.filter((name) => name.endsWith('.json')).sort()) {
    if (entries.length >= MAX_FRAGMENTS) {
      truncated = true
      break
    }
    const path = join(directory, file)
    let parsed
    try {
      const content = readFileSync(path, 'utf8')
      const bytes = Buffer.byteLength(content, 'utf8')
      if (bytes > MAX_FRAGMENT_BYTES) throw new Error('Fragment file exceeds the bounded size.')
      totalBytes += bytes
      if (totalBytes > MAX_TOTAL_INPUT_BYTES) throw new Error('Total fragment input exceeds the bounded size.')
      parsed = JSON.parse(content)
    } catch (error) {
      entries.push({ file, error: error.message, fragment: null })
      continue
    }
    const errors = validationErrors(parsed)
    if (errors.length > 0) {
      entries.push({ file, error: errors.join('; '), fragment: null })
      continue
    }
    entries.push({ file, error: null, fragment: parsed })
  }
  const fragments = entries.filter((entry) => entry.fragment !== null).map((entry) => entry.fragment)
  const missing = entries.filter((entry) => entry.fragment === null).map((entry) => ({
    job: entry.file.replace(/\.json$/, ''),
    reason: entry.error,
  }))
  return { fragments, missing, truncated }
}

function totalDuration(fragment) {
  const start = fragment.timings.jobStartMs
  const end = fragment.timings.jobEndMs
  return start !== null && end !== null && end >= start ? end - start : null
}

function validateExpectedJobs(expectedJobs) {
  const errors = []
  if (!Array.isArray(expectedJobs) || expectedJobs.length === 0 || expectedJobs.length > MAX_FRAGMENTS) {
    return ['expectedJobs must be a non-empty array bounded by the fragment limit.']
  }
  const instances = new Set()
  for (const job of expectedJobs) {
    if (!job || typeof job !== 'object') { errors.push('An expected job is not an object.'); continue }
    if (typeof job.group !== 'string' || job.group.length === 0 || job.group.length > 100) errors.push(`Expected job ${job.instance ?? '?'} has an invalid group.`)
    if (typeof job.instance !== 'string' || job.instance.length === 0 || job.instance.length > 120) errors.push(`Expected job ${job.instance ?? '?'} has an invalid instance.`)
    if (instances.has(job.instance)) errors.push(`Duplicate expected job instance "${job.instance}".`)
    instances.add(job.instance)
    if (!Array.isArray(job.needs) || job.needs.some((need) => typeof need !== 'string' || need.length === 0 || need.length > 100)) {
      errors.push(`Expected job "${job.instance}" has an invalid needs list.`)
    }
  }
  return errors
}

function runIdentity(fragment) {
  const run = fragment.run
  return `${run.id}:${run.attempt}:${run.sha}:${run.tree}:${run.workflowRef}:${run.repository}`
}

function matchesExpectedRun(fragment, expectedRun) {
  const run = fragment.run
  return run.id === expectedRun.id && run.attempt === expectedRun.attempt && run.sha === expectedRun.sha &&
    run.tree === expectedRun.tree && run.workflowRef === expectedRun.workflowRef && run.repository === expectedRun.repository
}

function sameNeeds(left, right) {
  const a = [...left].sort()
  const b = [...right].sort()
  return a.length === b.length && a.every((value, index) => value === b[index])
}

export function criticalPath({ fragments, expectedJobs = null, expectedTopology = expectedJobs !== null, trustedTopology = expectedJobs !== null }) {
  const byInstance = new Map(fragments.map((fragment) => [fragment.job.instance, fragment]))
  const instances = expectedJobs ? expectedJobs.map((job) => job.instance) : [...byInstance.keys()]
  const instanceSet = new Set(instances)

  // Duplicate instances are contradictory topology. `usesExpected` decides the graph source: expected-job
  // metadata drives the graph and missing detection whenever it is provided, even on PR runs where the
  // same-workflow metadata is shadow. `trusted` is only the label: PR-controlled metadata can never claim
  // trusted provenance until a trusted post-run collector validates it (phase B).
  const usesExpected = expectedJobs !== null && expectedTopology
  const trusted = usesExpected && trustedTopology
  const unavailable = (unavailableReason) => ({ job: null, durationMs: null, path: [], unavailableReason, trustedTopology: trusted, expectedTopology: usesExpected })

  if (byInstance.size !== fragments.length) return unavailable('Duplicate job instance identity in the fragment set.')

  // A group-level need must resolve to at least one expected/present instance.
  const groupToInstances = new Map()
  const register = (group, instance) => {
    const list = groupToInstances.get(group) ?? []
    list.push(instance)
    groupToInstances.set(group, list)
  }
  if (expectedJobs) for (const job of expectedJobs) register(job.group, job.instance)
  else for (const fragment of fragments) register(fragment.job.group, fragment.job.instance)

  const duration = new Map()
  const unknown = []
  const absent = []
  for (const instance of instances) {
    const fragment = byInstance.get(instance)
    if (!fragment) {
      absent.push(instance)
      duration.set(instance, null)
      continue
    }
    const value = totalDuration(fragment)
    duration.set(instance, value)
    if (value === null) unknown.push(instance)
  }
  for (const fragment of fragments) if (!instanceSet.has(fragment.job.instance)) absent.push(fragment.job.instance)

  if (unknown.length > 0 || absent.length > 0) {
    const reasons = []
    if (unknown.length > 0) reasons.push(`duration unknown for: ${unknown.join(', ')}`)
    if (absent.length > 0) reasons.push(`expected fragment absent for: ${absent.join(', ')}`)
    return unavailable(reasons.join('; '))
  }

  // Build the DAG over instances. With trusted expected-jobs metadata, the dependency graph comes
  // exclusively from that metadata; a fragment whose needs disagree with the trusted topology is a
  // contradiction, not a correction. Without it, the fragment-controlled graph is used and labelled as
  // untrusted rather than presented as authoritative.
  const edges = new Map()
  const topologyDisagreements = []
  if (usesExpected) {
    for (const fragment of fragments) {
      const trustedJob = expectedJobs.find((job) => job.instance === fragment.job.instance)
      if (trustedJob && !sameNeeds(fragment.job.needs, trustedJob.needs)) {
        topologyDisagreements.push(fragment.job.instance)
      }
    }
    for (const job of expectedJobs) {
      const dependents = []
      for (const need of job.needs) {
        const targets = groupToInstances.get(need)
        if (!targets || targets.length === 0) {
          return unavailable(`Dependency group "${need}" of "${job.instance}" has no known instances.`)
        }
        for (const target of targets) dependents.push(target)
      }
      edges.set(job.instance, dependents)
    }
  } else {
    for (const fragment of fragments) {
      const dependents = edges.get(fragment.job.instance) ?? []
      for (const need of fragment.job.needs) {
        const targets = groupToInstances.get(need)
        if (!targets || targets.length === 0) {
          return unavailable(`Dependency group "${need}" of "${fragment.job.instance}" has no known instances.`)
        }
        for (const target of targets) dependents.push(target)
      }
      edges.set(fragment.job.instance, dependents)
    }
  }

  const best = new Map()
  const parent = new Map()
  const visiting = new Set()
  const finished = new Set()

  const visit = (id) => {
    if (finished.has(id)) return best.get(id)
    if (visiting.has(id)) throw new Error(`Cycle detected while computing the critical path at "${id}".`)
    visiting.add(id)
    let longest = duration.get(id) ?? 0
    for (const dependency of edges.get(id) ?? []) {
      const candidate = visit(dependency) + (duration.get(id) ?? 0)
      if (candidate > longest) {
        longest = candidate
        parent.set(id, dependency)
      }
    }
    best.set(id, longest)
    visiting.delete(id)
    finished.add(id)
    return longest
  }

  try {
    for (const instance of instances) visit(instance)
  } catch (error) {
    return unavailable(error.message)
  }

  let critical = null
  for (const instance of instances) {
    const candidate = best.get(instance)
    if (critical === null || candidate > critical.durationMs) critical = { id: instance, durationMs: candidate }
  }

  const path = []
  const visited = new Set()
  let cursor = critical?.id
  while (cursor && !visited.has(cursor)) {
    visited.add(cursor)
    path.unshift(cursor)
    cursor = parent.get(cursor)
  }
  return { job: critical?.id ?? null, durationMs: critical?.durationMs ?? null, path, unavailableReason: null, trustedTopology: trusted, expectedTopology: usesExpected, topologyDisagreements }
}

function escapeMarkdown(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n/g, ' ')
}

function boundedMissing(missing) {
  const truncated = missing.length > MAX_MISSING_ENTRIES
  return { entries: missing.slice(0, MAX_MISSING_ENTRIES), truncated, total: missing.length }
}

export function aggregateFragments({ fragments, missing = [], runMeta = null }) {
  const expectedJobs = Array.isArray(runMeta?.expectedJobs) ? runMeta.expectedJobs : null
  const expectedJobsErrors = expectedJobs === null ? [] : validateExpectedJobs(expectedJobs)
  const topologyErrors = [...expectedJobsErrors]
  const provenanceMode = runMeta?.provenance?.mode === 'trusted' ? 'trusted' : 'shadow'
  const provenanceReason = runMeta?.provenance?.reason
    ? String(runMeta.provenance.reason).slice(0, 300)
    : runMeta === null
      ? 'No run metadata was provided.'
      : 'Run metadata did not declare provenance.'
  const topologyTrusted = expectedJobs !== null && provenanceMode === 'trusted'

  // Run identity is trusted input. With trusted expectedRun metadata, fragments that disagree are excluded
  // from every derived aggregate and each exclusion is recorded. Without it, fragment order must never
  // choose the winner: conflicting identities make the aggregate unavailable rather than publishing one
  // side as authoritative totals. A fully consistent set is still aggregated but labelled untrusted.
  const expectedRun = runMeta?.expectedRun ?? null
  let referenceRun = expectedRun
  let consistent = []
  const excluded = []
  let runIdentityConflict = false
  if (expectedRun) {
    for (const fragment of fragments) {
      if (!matchesExpectedRun(fragment, expectedRun)) {
        excluded.push({ job: fragment.job.instance, reason: 'Run identity does not match the trusted run metadata; excluded from the merged record.' })
      } else if (expectedJobs !== null && !expectedJobs.some((job) => job.instance === fragment.job.instance)) {
        excluded.push({ job: fragment.job.instance, reason: 'Job instance is not part of the expected topology; excluded from the merged record.' })
      } else {
        consistent.push(fragment)
      }
    }
  } else if (fragments.length > 0) {
    const first = fragments[0].run
    runIdentityConflict = fragments.some((fragment) => runIdentity(fragment) !== runIdentity(fragments[0]))
    if (runIdentityConflict) {
      const reported = new Set()
      for (const fragment of fragments) {
        const identity = runIdentity(fragment)
        if (reported.has(identity)) continue
        reported.add(identity)
        excluded.push({ job: fragment.job.instance, reason: 'No trusted expectedRun metadata and fragment identities conflict; no identity was selected as authoritative.' })
      }
    } else {
      referenceRun = first
      for (const fragment of fragments) {
        if (expectedJobs !== null && !expectedJobs.some((job) => job.instance === fragment.job.instance)) {
          excluded.push({ job: fragment.job.instance, reason: 'Job instance is not part of the expected topology; excluded from the merged record.' })
        } else {
          consistent.push(fragment)
        }
      }
    }
  }
  const effectiveTopologyErrors = [...topologyErrors]
  const conflictUnavailable = runIdentityConflict
    ? 'No trusted expectedRun metadata and fragment identities conflict; aggregate and critical path are unavailable.'
    : null

  let duplicateUnavailable = null
  const instanceCounts = new Map()
  for (const fragment of consistent) instanceCounts.set(fragment.job.instance, (instanceCounts.get(fragment.job.instance) ?? 0) + 1)
  const duplicated = [...instanceCounts.entries()].filter(([, count]) => count > 1)
  if (duplicated.length > 0) {
    for (const [instance, count] of duplicated) {
      excluded.push({ job: instance, reason: `Duplicate job instance identity (${count} fragments); derived aggregates are not published.` })
    }
    consistent = []
    duplicateUnavailable = 'Duplicate job instance identity in the fragment set; derived aggregates and critical path are unavailable.'
  }

  const expectedAbsent = []
  if (expectedJobs) {
    const present = new Set(consistent.map((fragment) => fragment.job.instance))
    for (const job of expectedJobs) if (!present.has(job.instance)) expectedAbsent.push(job.instance)
  }
  const absentMissing = expectedAbsent.map((instance) => ({ job: instance, reason: 'Expected job instance uploaded no fragment (cancelled before cleanup or never ran).' }))

  const allMissing = [...missing, ...absentMissing, ...excluded].map((entry) => ({ job: String(entry.job).slice(0, 120), reason: String(entry.reason).slice(0, 300) }))
  const missingModel = boundedMissing(allMissing)

  const path = conflictUnavailable !== null
    ? { job: null, durationMs: null, path: [], unavailableReason: conflictUnavailable, trustedTopology: topologyTrusted }
    : duplicateUnavailable !== null
      ? { job: null, durationMs: null, path: [], unavailableReason: duplicateUnavailable, trustedTopology: topologyTrusted }
    : effectiveTopologyErrors.length > 0
      ? { job: null, durationMs: null, path: [], unavailableReason: effectiveTopologyErrors.join('; '), trustedTopology: topologyTrusted }
      : criticalPath({ fragments: consistent, expectedJobs, expectedTopology: expectedJobs !== null, trustedTopology: topologyTrusted })

  const cache = { nuget: { hit: 0, miss: 0 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 0, miss: 0 } }
  const flakyTests = []
  const flakyTitleEvidence = { unavailable: [], truncated: [], reasons: {} }
  const countSummary = { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, sourcedJobs: 0 }
  for (const fragment of consistent) {
    for (const kind of ['nuget', 'npm', 'chromium']) {
      if (fragment.cache[kind] === 'hit') cache[kind].hit += 1
      if (fragment.cache[kind] === 'miss') cache[kind].miss += 1
    }
    for (const title of fragment.flakyTests ?? []) if (!flakyTests.includes(title)) flakyTests.push(title)
    if (fragment.flakyTitlesUnavailable === true) flakyTitleEvidence.unavailable.push(fragment.job.instance)
    if (fragment.flakyTitlesTruncated === true) flakyTitleEvidence.truncated.push(fragment.job.instance)
    if ((fragment.flakyTitlesUnavailable === true || fragment.flakyTitlesTruncated === true) && fragment.counts.missing) {
      flakyTitleEvidence.reasons[fragment.job.instance] = String(fragment.counts.missing).slice(0, 300)
    }
    if (fragment.counts.source) {
      countSummary.sourcedJobs += 1
      for (const key of ['expected', 'executed', 'passed', 'failed', 'skipped', 'flaky']) {
        if (fragment.counts[key] !== null) countSummary[key] = (countSummary[key] ?? 0) + fragment.counts[key]
      }
    }
  }
  const unionTruncated = flakyTests.length > 20
  const finalFlakyTests = flakyTests.slice(0, 20)

  // Test families are modelled separately from the sourced subtotal: a selected test-bearing job without
  // structured counts is listed, and the totals are never presented as the full run total.
  const TEST_FAMILY_GROUPS = new Set([
    'backend-api', 'backend-core', 'browser-pr', 'browser-production', 'browser-full',
    'metrics-tooling', 'script-contracts', 'postgresql-smoke',
  ])
  const missingFamilies = []
  if (expectedJobs) {
    const present = new Map(consistent.map((fragment) => [fragment.job.instance, fragment]))
    for (const job of expectedJobs) {
      if (!TEST_FAMILY_GROUPS.has(job.group)) continue
      const fragment = present.get(job.instance)
      if (!fragment) {
        missingFamilies.push({ group: job.group, instance: job.instance, reason: 'Expected test family uploaded no fragment.' })
      } else if (!fragment.counts.source) {
        missingFamilies.push({
          group: job.group,
          instance: job.instance,
          reason: fragment.counts.missing ?? 'This family has no structured test output.',
        })
      }
    }
  }
  missingFamilies.sort((a, b) => a.group.localeCompare(b.group) || a.instance.localeCompare(b.instance))

  const sourcedFamilyGroups = new Set()
  for (const fragment of consistent) {
    if (fragment.counts.source) sourcedFamilyGroups.add(fragment.job.group)
  }

  const queueDelayMs = runMeta?.queueDelayMs ?? null
  const run = expectedRun ?? consistent[0]?.run ?? null
  const merged = {
    schemaVersion: RUN_SCHEMA_VERSION,
    run: run
      ? {
          id: run.id,
          attempt: run.attempt,
          event: run.event,
          sha: run.sha,
          tree: run.tree,
          ref: run.ref,
          pr: run.pr,
          workflow: run.workflow,
          repository: run.repository,
        }
      : null,
    jobs: consistent.slice(0, MAX_FRAGMENTS).map((fragment) => ({
      group: fragment.job.group,
      instance: fragment.job.instance,
      name: fragment.job.name,
      matrix: fragment.job.matrix,
      needs: fragment.job.needs,
      result: fragment.job.result,
      timings: fragment.timings,
      counts: fragment.counts,
      slowest: Array.isArray(fragment.slowest) ? fragment.slowest.slice(0, 50) : [],
      cache: fragment.cache,
      classification: fragment.classification,
      flakyTitlesUnavailable: fragment.flakyTitlesUnavailable === true,
      flakyTitlesTruncated: fragment.flakyTitlesTruncated === true,
      flakyTitleMissingReason: fragment.counts.missing ? String(fragment.counts.missing).slice(0, 300) : null,
    })),
    criticalPath: path,
    runIdentityTrusted: expectedRun !== null && provenanceMode === 'trusted',
    provenance: {
      mode: provenanceMode,
      reason: provenanceReason,
    },
    skipped: Array.isArray(runMeta?.skippedJobs)
      ? runMeta.skippedJobs.slice(0, MAX_FRAGMENTS).map((job) => ({
          group: String(job.group ?? '').slice(0, 100),
          instance: String(job.instance ?? '').slice(0, 120),
          reason: String(job.reason ?? '').slice(0, 300),
        }))
      : [],
    queue: {
      delayMs: queueDelayMs,
      unavailableReason: queueDelayMs === null && runMeta === null
        ? 'GitHub API queue timing is collected by the rolling collector (phase B).'
        : queueDelayMs === null ? 'runMeta did not include queueDelayMs.' : null,
    },
    counts: countSummary,
    countsModel: {
      sourcedFamilies: sourcedFamilyGroups.size,
      sourcedJobInstances: countSummary.sourcedJobs,
      missingFamilies,
      totalIsPartial: missingFamilies.length > 0,
      label: 'sourced families with structured output only',
    },
    cache,
    flakyTests: finalFlakyTests,
    flakyTitlesTruncated: unionTruncated || flakyTitleEvidence.truncated.length > 0,
    flakyTitleEvidence,
    missing: missingModel.entries,
    missingTruncated: missingModel.truncated,
    missingTotal: missingModel.total,
    classifications: {
      docsOnly: consistent.filter((f) => f.classification?.docsOnly === true).length,
      backend: consistent.filter((f) => f.classification?.backend === true).length,
      client: consistent.filter((f) => f.classification?.client === true).length,
      browser: consistent.filter((f) => f.classification?.browser === true).length,
      postgresql: consistent.filter((f) => f.classification?.postgresql === true).length,
      unavailable: consistent.filter((f) => f.classification?.unavailable === true).length,
    },
    bounds: {
      maxFragments: MAX_FRAGMENTS,
      maxMergedBytes: MAX_MERGED_BYTES,
      mergedBytes: 0,
    },
  }
  const mergedJson = JSON.stringify(merged)
  if (Buffer.byteLength(mergedJson, 'utf8') > MAX_MERGED_BYTES) {
    throw new Error('Merged run metrics exceed the bounded size.')
  }
  merged.bounds.mergedBytes = Buffer.byteLength(mergedJson, 'utf8')
  return merged
}

export function renderMarkdown(merged) {
  const lines = []
  const push = (line) => lines.push(String(line))
  push('# CI run metrics')
  push('')
  push(`- Run: ${merged.run ? `#${escapeMarkdown(merged.run.id)} (attempt ${escapeMarkdown(merged.run.attempt)}, ${escapeMarkdown(merged.run.event)})` : 'unavailable'}`)
  push(`- Repository: ${merged.run?.repository ? escapeMarkdown(merged.run.repository) : 'unavailable'}`)
  push(`- Tested tree: ${merged.run?.tree ? `\`${escapeMarkdown(merged.run.tree)}\`` : 'unavailable'}`)
  push(`- Run identity: ${merged.runIdentityTrusted ? 'trusted (default-branch provenance)' : 'shadow (not yet validated by a trusted post-run collector)'}`)
  push(`- Provenance: ${escapeMarkdown(merged.provenance?.reason ?? 'not declared')}`)
  push(`- Fragments: ${merged.jobs.length} valid, ${merged.missingTotal} missing/unreadable${merged.missingTruncated ? ' (list truncated)' : ''}`)
  if (merged.skipped.length > 0) {
    push(`- Deliberately skipped jobs: ${merged.skipped.length}`)
  }
  push(`- Sourced test totals (${escapeMarkdown(merged.countsModel?.label ?? 'sourced families only')}): expected ${merged.counts.expected ?? 'unavailable'}, executed ${merged.counts.executed ?? 'unavailable'}, passed ${merged.counts.passed ?? 'unavailable'}, failed ${merged.counts.failed ?? 'unavailable'}, skipped ${merged.counts.skipped ?? 'unavailable'}, flaky ${merged.counts.flaky ?? 'unavailable'}`)
  if (merged.countsModel?.missingFamilies.length > 0) {
    push(`- Families without structured counts (${merged.countsModel.missingFamilies.length}): ${merged.countsModel.missingFamilies.map((entry) => `${escapeMarkdown(entry.instance)} (${escapeMarkdown(entry.reason)})`).join('; ')}`)
  }
  const critical = merged.criticalPath
  if (critical.trustedTopology === false) {
    push(critical.expectedTopology === true
      ? '- Topology: same-workflow (shadow) expected-job metadata; trusted validation pending phase B'
      : '- Topology: fragment-controlled (trusted expectedJobs metadata was not provided)')
  }
  if (critical.topologyDisagreements?.length > 0) {
    push(`- Topology disagreements (trusted graph won): ${critical.topologyDisagreements.map(escapeMarkdown).join(', ')}`)
  }
  if (critical.unavailableReason) {
    push(`- Critical path: unavailable — ${escapeMarkdown(critical.unavailableReason)}`)
  } else {
    push(`- Critical path: ${escapeMarkdown(critical.job ?? 'unavailable')} (${critical.durationMs === null ? 'unknown duration' : `${Math.round(critical.durationMs / 1000)}s`})`)
  }
  push('')
  push('## Jobs')
  push('')
  push('| Job | Result | Total | Setup | Test | After test | Counts |')
  push('|---|---|---|---|---|---|---|')
  for (const job of merged.jobs) {
    const seconds = (ms) => (ms === null ? '—' : `${(ms / 1000).toFixed(1)}s`)
    const counts = job.counts.source ? `${job.counts.executed ?? '?'}/${job.counts.expected ?? '?'}` : '—'
    const total = job.timings.jobStartMs !== null && job.timings.jobEndMs !== null ? job.timings.jobEndMs - job.timings.jobStartMs : null
    push(`| ${escapeMarkdown(job.name)} | ${escapeMarkdown(job.result)} | ${seconds(total)} | ${seconds(job.timings.setupMs)} | ${seconds(job.timings.testMs)} | ${seconds(job.timings.postTestMs)} | ${counts} |`)
  }
  push('')
  if (merged.skipped.length > 0) {
    push('## Deliberately skipped')
    push('')
    for (const job of merged.skipped) {
      push(`- ${escapeMarkdown(job.instance)}: ${escapeMarkdown(job.reason)}`)
    }
    push('')
  }
  if (merged.missing.length > 0) {
    push('## Missing data')
    push('')
    for (const entry of merged.missing) push(`- ${escapeMarkdown(entry.job)}: ${escapeMarkdown(entry.reason)}`)
    if (merged.missingTruncated) push(`- …and ${merged.missingTotal - merged.missing.length} more.`)
    push('')
  }
  if (merged.queue.unavailableReason) push(`## Queue timing\n\n${escapeMarkdown(merged.queue.unavailableReason)}\n`)
  if (merged.flakyTests.length > 0) {
    push('## Flaky / retried browser tests')
    push('')
    for (const title of merged.flakyTests) push(`- ${escapeMarkdown(title)}`)
    if (merged.flakyTitlesTruncated) push(`- (titles truncated at 20)`)
    push('')
  }
  if (merged.flakyTitleEvidence && (merged.flakyTitleEvidence.unavailable.length > 0 || merged.flakyTitleEvidence.truncated.length > 0)) {
    push('## Flaky title evidence')
    push('')
    for (const instance of merged.flakyTitleEvidence.unavailable) {
      push(`- ${escapeMarkdown(instance)}: exact flaky titles unavailable — ${escapeMarkdown(merged.flakyTitleEvidence.reasons[instance] ?? 'no reason recorded')}`)
    }
    for (const instance of merged.flakyTitleEvidence.truncated) {
      push(`- ${escapeMarkdown(instance)}: flaky titles truncated — ${escapeMarkdown(merged.flakyTitleEvidence.reasons[instance] ?? 'no reason recorded')}`)
    }
    push('')
  }
  if (merged.cache.nuget.hit + merged.cache.nuget.miss + merged.cache.npm.hit + merged.cache.npm.miss + merged.cache.chromium.hit + merged.cache.chromium.miss > 0) {
    push(`## Caches\n\n- NuGet: ${merged.cache.nuget.hit} hit / ${merged.cache.nuget.miss} miss\n- npm: ${merged.cache.npm.hit} hit / ${merged.cache.npm.miss} miss\n- Chromium: ${merged.cache.chromium.hit} hit / ${merged.cache.chromium.miss} miss\n`)
  }
  const markdown = lines.join('\n')
  if (Buffer.byteLength(markdown, 'utf8') > MAX_MARKDOWN_BYTES) throw new Error('Rendered Markdown exceeds the bounded size.')
  return markdown
}
