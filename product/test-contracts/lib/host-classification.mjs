import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const MIGRATABLE = new Set(['in-process-logic', 'rule-matrix'])
const NEEDS_FRESH = new Set(['filesystem', 'startup-config'])

const CUSTOM_HOST_RULES = [
  ['showcase-template-copy', /\b(?:showcase|fixture)\s*\.\s*CreateFactory\s*\(/],
  ['service-replacement', /WithWebHostBuilder|ConfigureServices|ConfigureTestServices|RemoveAll\s*</],
  ['interceptor-or-fault-injection', /\b(?:commandInterceptor|storageFaultInjector|telemetryObserver)\s*:/],
  ['factory-host-options', /new\s+AeroLinkApiFactory\s*\([^)]*(?:seedDemoAccounts|allowDemoAccounts|showcaseTemplate|staticFilesRoot)\s*:/s],
]

function classCustomization(source) {
  return CUSTOM_HOST_RULES.filter(([, pattern]) => pattern.test(source)).map(([key]) => key)
}

export function classifyClass({ cls, source, rows, override }) {
  if (override) {
    if (!['fresh-host', 'reusable-host', 'converted', 'migration-candidate'].includes(override.classification)) {
      throw new Error(`Unsupported reviewed host classification for ${cls}: ${override.classification}`)
    }
    if (typeof override.reason !== 'string' || override.reason.trim() === '') {
      throw new Error(`Reviewed host classification for ${cls} must include a reason`)
    }
    return {
      cls,
      tests: rows.length,
      classification: override.classification,
      reason: override.reason,
      intents: [...new Set(rows.map((row) => row.intent))].sort(),
    }
  }
  const customizations = classCustomization(source)
  const intents = new Set(rows.map((row) => row.intent))
  const tests = rows.length
  if (customizations.length > 0) {
    return {
      cls,
      tests,
      classification: 'fresh-host',
      reason: `Customizes host state (${customizations.join(', ')}); sharing could change the subject or leak configuration.`,
      intents: [...intents].sort(),
    }
  }
  if (rows.some((row) => row.hosted === 'unknown')) {
    return {
      cls,
      tests,
      classification: 'fresh-host',
      reason: 'Host use is unknown for one or more invocations; isolate it until method-level evidence is completed.',
      intents: [...intents].sort(),
    }
  }
  if (cls !== 'SharedApiHost' && /\bSharedApiHost\b/.test(source)) {
    return {
      cls,
      tests,
      classification: 'converted',
      reason: 'Already sharing a host through the #563 pilot.',
      intents: [...intents].sort(),
    }
  }
  if (rows.some((row) => row.hosted === 'not-hosted') && rows.some((row) => row.hosted === 'hosted')) {
    return {
      cls,
      tests,
      classification: 'fresh-host',
      reason: 'Mixed hosted and non-hosted invocations require an explicit fixture boundary before reuse.',
      intents: [...intents].sort(),
    }
  }
  if ([...intents].every((intent) => MIGRATABLE.has(intent))) {
    return {
      cls,
      tests,
      classification: 'migration-candidate',
      reason: 'Every test is in-process logic or a rule matrix; it needs a database or nothing, not a host.',
      intents: [...intents].sort(),
    }
  }
  if ([...intents].some((intent) => NEEDS_FRESH.has(intent))) {
    return {
      cls,
      tests,
      classification: 'fresh-host',
      reason: `Owns host-scoped state (${[...intents].filter((intent) => NEEDS_FRESH.has(intent)).join(', ')}); sharing would leak it between tests.`,
      intents: [...intents].sort(),
    }
  }
  return {
    cls,
    tests,
    classification: 'reusable-host',
    reason: 'Exercises the HTTP surface with no host-scoped state or custom host configuration; a shared host is safe only with per-test clients.',
    intents: [...intents].sort(),
  }
}

export function classifyInventory({ testsDirectory, inventory, overrides = {} }) {
  const sourceByClass = new Map()
  for (const row of inventory.tests) {
    if (!sourceByClass.has(row.cls)) {
      const file = join(testsDirectory, `${row.cls}.cs`)
      sourceByClass.set(row.cls, requireText(file))
    }
  }
  const rowsByClass = new Map()
  for (const row of inventory.tests) {
    const rows = rowsByClass.get(row.cls) ?? []
    rows.push(row)
    rowsByClass.set(row.cls, rows)
  }
  for (const cls of Object.keys(overrides)) {
    if (!rowsByClass.has(cls)) {
      throw new Error(`Reviewed host classification names a class absent from the current inventory: ${cls}`)
    }
  }
  const classes = [...rowsByClass.entries()].map(([cls, rows]) => classifyClass({ cls, source: sourceByClass.get(cls), rows, override: overrides[cls] }))
  classes.sort((a, b) => a.classification.localeCompare(b.classification) || b.tests - a.tests || a.cls.localeCompare(b.cls))
  const summary = {}
  for (const row of classes) {
    summary[row.classification] = summary[row.classification] ?? { classes: 0, tests: 0 }
    summary[row.classification].classes += 1
    summary[row.classification].tests += row.tests
  }
  return { classes, summary, totals: { classes: classes.length, tests: classes.reduce((sum, row) => sum + row.tests, 0) } }
}

/** Build the committed artifact shape from a current-source intent inventory. */
export function buildHostArtifact({ testsDirectory, inventory, overrides = {} }) {
  const result = classifyInventory({ testsDirectory, inventory, overrides })
  return {
    schemaVersion: 'aerolink-api-host-classification/v2',
    totals: result.totals,
    summary: result.summary,
    classes: result.classes,
  }
}

function requireText(file) { return readFileSync(file, 'utf8') }

export { CUSTOM_HOST_RULES }
