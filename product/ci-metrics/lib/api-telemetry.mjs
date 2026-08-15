// Aggregates #563 phase-1 startup-floor telemetry.
//
// Factory JSONL lines (construction latency, host build, disposal, attributed by call site) are combined
// with TRX per-test durations: per-test wall time comes from TRX, per-test startup comes from the factory
// records for that class/method. Output is bounded, credential-scanned, and never publishes absolute
// source paths.

import { median, percentile } from './rolling.mjs'
import { looksLikeCredential } from './fragment.mjs'

export const API_TELEMETRY_SCHEMA_VERSION = 'aerolink-api-telemetry/v2'
export const MAX_RECORDS = 60_000
export const MAX_CLASSES = 300

export function parseTelemetryLines(text) {
  const records = []
  const malformed = []
  let truncated = false
  for (const line of String(text).split('\n')) {
    if (!line.trim()) continue
    if (records.length >= MAX_RECORDS) {
      truncated = true
      break
    }
    let parsed
    try {
      parsed = JSON.parse(line)
    } catch {
      malformed.push('Unparseable JSON line.')
      continue
    }
    if (parsed?.type !== 'factory') {
      malformed.push('Line is not a factory record.')
      continue
    }
    if (!Number.isInteger(parsed.factoryId) || parsed.factoryId < 1) {
      malformed.push('factoryId is invalid.')
      continue
    }
    if (typeof parsed.class !== 'string' || parsed.class.length === 0 || parsed.class.length > 200) {
      malformed.push('class is invalid.')
      continue
    }
    if (typeof parsed.method !== 'string' || parsed.method.length === 0 || parsed.method.length > 300) {
      malformed.push('method is invalid.')
      continue
    }
    if (!['host', 'dispose', 'connectionOpen', 'dbOpen'].includes(parsed.phase)) {
      malformed.push(`phase "${parsed.phase}" is invalid.`)
      continue
    }
    if (parsed.phase === 'dbOpen' && parsed.schemaVersion !== undefined && parsed.schemaVersion !== 'aerolink-api-telemetry/v1') {
      malformed.push('legacy dbOpen phase requires schema aerolink-api-telemetry/v1.')
      continue
    }
    if (parsed.schemaVersion !== undefined && !['aerolink-api-telemetry/v1', 'aerolink-api-telemetry/v2'].includes(parsed.schemaVersion)) {
      malformed.push(`schemaVersion "${parsed.schemaVersion}" is invalid.`)
      continue
    }
    let invalidTiming = false
    for (const key of ['constructionMs', 'ms']) {
      if (typeof parsed[key] !== 'number' || !Number.isFinite(parsed[key]) || parsed[key] < 0) {
        malformed.push(`${key} is invalid.`)
        invalidTiming = true
        break
      }
    }
    if (invalidTiming) continue
    if (looksLikeCredential(JSON.stringify(parsed))) {
      malformed.push('Credential-shaped value.')
      continue
    }
    records.push(parsed)
  }
  return { records, malformed, truncated }
}

function quantiles(values) {
  if (values.length === 0) return { p10: null, median: null, p75: null, p95: null }
  return { p10: percentile(values, 10), median: median(values), p75: percentile(values, 75), p95: percentile(values, 95) }
}

export function aggregateApiTelemetry({ factoryRecords, trxTests = [] }) {
  // Per factory, startup is the sum of three non-overlapping intervals: construction latency (factory
  // construction to host-build start, captured BEFORE base.CreateHost), the host build itself, and
  // disposal. The dispose record repeats constructionMs for provenance only and it must not be added
  // again. connectionOpen is informational (every SQLite connection open over the factory lifetime, not
  // only host startup) and is reported separately, never added to the startup total.
  const byFactory = new Map()
  for (const record of factoryRecords) {
    const entry = byFactory.get(record.factoryId) ?? { class: record.class, method: record.method, constructionMs: null, hostMs: null, disposeMs: null, connectionOpenMs: null }
    if (record.phase === 'host') {
      entry.constructionMs = record.constructionMs
      entry.hostMs = record.ms
    }
    if (record.phase === 'dispose') entry.disposeMs = record.ms
    if (record.phase === 'connectionOpen' || record.phase === 'dbOpen') entry.connectionOpenMs = (entry.connectionOpenMs ?? 0) + record.ms
    byFactory.set(record.factoryId, entry)
  }
  const factories = [...byFactory.values()]

  const trxByClass = new Map()
  for (const test of trxTests) {
    const className = test.className?.split('.').at(-1) ?? 'unknown'
    const list = trxByClass.get(className) ?? []
    list.push(test)
    trxByClass.set(className, list)
  }

  const byTest = new Map()
  const ambiguousTheories = []
  const unmatchedMethods = []
  for (const factory of factories) {
    const key = `${factory.class}.${factory.method}`
    const entry = byTest.get(key) ?? {
      className: factory.class,
      method: factory.method,
      factoryCount: 0,
      startupMs: 0,
      connectionOpenMs: 0,
      wallMs: null,
    }
    entry.factoryCount += 1
    entry.startupMs += (factory.constructionMs ?? 0) + (factory.hostMs ?? 0) + (factory.disposeMs ?? 0)
    entry.connectionOpenMs += factory.connectionOpenMs ?? 0
    if (entry.wallMs === null) {
      const trxList = trxByClass.get(factory.class) ?? []
      const matched = trxList.find((test) =>
        test.name === factory.method ||
        test.name.endsWith(`.${factory.method}`) ||
        test.name.startsWith(`${factory.method}(`) ||
        test.name.includes(`.${factory.method}(`) ||
        test.name.startsWith(`${factory.method}_`))
      entry.wallMs = matched?.durationMs ?? null
    }
    byTest.set(key, entry)
  }

  const methodBaseOf = (name) => {
    // The method name ends at the first '('; arguments may contain dots and parentheses, so the last
    // dot in the full TRX name cannot be trusted. Take the head first, then the segment after its last dot.
    const open = name.indexOf('(')
    const head = (open === -1 ? name : name.slice(0, open)).trim()
    const dot = head.lastIndexOf('.')
    return dot === -1 ? head : head.slice(dot + 1)
  }

  const perTest = []
  for (const entry of byTest.values()) {
    const trxList = trxByClass.get(entry.className) ?? []
    const rows = trxList.filter((test) =>
      test.name === entry.method ||
      test.name.endsWith(`.${entry.method}`) ||
      test.name.startsWith(`${entry.method}(`) ||
      test.name.includes(`.${entry.method}(`) ||
      test.name.startsWith(`${entry.method}_`))
    if (rows.length === 0) {
      // Fixture/helper factories (e.g., ShowcaseApiFixture.CreateFactory) have no TRX row of their own.
      unmatchedMethods.push({ className: entry.className, method: entry.method, factories: entry.factoryCount })
      continue
    }
    if (rows.length > 1) {
      // Parameterized theory rows cannot be mapped to individual factories from call-site attribution
      // alone. They are reported as ambiguous rather than merged into a fabricated multi-factory test.
      ambiguousTheories.push({
        className: entry.className,
        method: entry.method,
        trxRows: rows.length,
        factories: entry.factoryCount,
        startupMs: Math.round(entry.startupMs),
        connectionOpenMs: Math.round(entry.connectionOpenMs),
      })
      continue
    }
    const wallMs = rows[0].durationMs
    perTest.push({
      className: entry.className,
      method: entry.method,
      factoryCount: entry.factoryCount,
      startupMs: Math.round(entry.startupMs),
      connectionOpenMs: Math.round(entry.connectionOpenMs),
      wallMs: wallMs === null ? null : Math.round(wallMs),
      bodyMs: wallMs === null ? null : Math.max(0, Math.round(wallMs - entry.startupMs)),
    })
  }

  const classes = new Map()
  for (const entry of perTest) {
    const classEntry = classes.get(entry.className) ?? {
      className: entry.className,
      tests: 0,
      factories: 0,
      connectionOpenMs: 0,
      theoryRows: 0,
      wallMs: [],
      startupMs: [],
    }
    classEntry.tests += 1
    classEntry.factories += entry.factoryCount
    classEntry.connectionOpenMs += entry.connectionOpenMs
    classEntry.startupMs.push(entry.startupMs)
    if (entry.wallMs !== null) classEntry.wallMs.push(entry.wallMs)
    classes.set(entry.className, classEntry)
  }
  for (const theory of ambiguousTheories) {
    const classEntry = classes.get(theory.className) ?? {
      className: theory.className,
      tests: 0,
      factories: 0,
      connectionOpenMs: 0,
      theoryRows: 0,
      wallMs: [],
      startupMs: [],
    }
    classEntry.theoryRows += theory.trxRows
    classEntry.factories += theory.factories
    classEntry.connectionOpenMs += theory.connectionOpenMs
    classes.set(theory.className, classEntry)
  }

  const classSummary = [...classes.values()].map((entry) => ({
    className: entry.className,
    tests: entry.tests,
    factories: entry.factories,
    theoryRows: entry.theoryRows,
    connectionOpenMs: Math.round(entry.connectionOpenMs),
    wall: quantiles(entry.wallMs),
    startup: quantiles(entry.startupMs),
    summedStartupMs: Math.round(entry.startupMs.reduce((sum, value) => sum + value, 0)),
    summedWallMs: Math.round(entry.wallMs.reduce((sum, value) => sum + value, 0)),
    startupFraction: entry.wallMs.length > 0
      ? Math.round((entry.startupMs.reduce((sum, value) => sum + value, 0) / Math.max(1, entry.wallMs.reduce((sum, value) => sum + value, 0))) * 100) / 100
      : null,
  })).sort((a, b) => b.summedStartupMs - a.summedStartupMs || a.className.localeCompare(b.className))

  const multiFactoryTests = perTest.filter((entry) => entry.factoryCount > 1).sort((a, b) => b.factoryCount - a.factoryCount).slice(0, 50)

  const allWall = perTest.map((entry) => entry.wallMs).filter((value) => value !== null)
  const allStartup = perTest.map((entry) => entry.startupMs)
  const trxWithoutFactoryTelemetry = []
  for (const test of trxTests) {
    const shortClass = test.className?.split('.').at(-1) ?? 'unknown'
    const base = methodBaseOf(test.name)
    if (!byTest.has(`${shortClass}.${base}`)) {
      trxWithoutFactoryTelemetry.push({
        className: test.className,
        method: test.name,
        durationMs: test.durationMs,
      })
    }
  }
  return {
    schemaVersion: API_TELEMETRY_SCHEMA_VERSION,
    totals: {
      trxTests: trxTests.length,
      tests: perTest.length,
      ambiguousTheoryRows: ambiguousTheories.reduce((sum, entry) => sum + entry.trxRows, 0),
      unmatchedMethods: unmatchedMethods.length,
      trxWithoutFactoryTelemetry: trxWithoutFactoryTelemetry.length,
      factories: factories.length,
      classes: classes.size,
      summedWallMs: Math.round(allWall.reduce((sum, value) => sum + value, 0)),
      summedStartupMs: Math.round(allStartup.reduce((sum, value) => sum + value, 0)),
      summedConnectionOpenMs: Math.round(perTest.reduce((sum, entry) => sum + entry.connectionOpenMs, 0)),
      wall: quantiles(allWall),
      startup: quantiles(allStartup),
      startupFraction: allWall.length > 0 ? Math.round((allStartup.reduce((sum, value) => sum + value, 0) / Math.max(1, allWall.reduce((sum, value) => sum + value, 0))) * 100) / 100 : null,
    },
    classes: classSummary.slice(0, MAX_CLASSES),
    slowestStartupTests: [...perTest].sort((a, b) => b.startupMs - a.startupMs).slice(0, 50),
    multipleFactoryTests: multiFactoryTests,
    ambiguousTheoryRows: ambiguousTheories,
    unmatchedMethods: unmatchedMethods.slice(0, 50),
    trxWithoutFactoryTelemetry: trxWithoutFactoryTelemetry.sort((a, b) => (b.durationMs ?? 0) - (a.durationMs ?? 0)).slice(0, 50),
  }
}

function escapeMarkdown(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n/g, ' ')
}

export function renderApiTelemetryMarkdown(report) {
  const seconds = (ms) => (ms === null ? '—' : `${(ms / 1000).toFixed(1)}s`)
  const lines = []
  lines.push('# API startup-floor telemetry')
  lines.push('')
  lines.push(`- TRX tests: ${report.totals.trxTests}; attributed (non-ambiguous) tests: ${report.totals.tests}; ambiguous theory rows: ${report.totals.ambiguousTheoryRows}; unmatched factory methods: ${report.totals.unmatchedMethods}; TRX tests without factory telemetry: ${report.totals.trxWithoutFactoryTelemetry}`)
  lines.push(`- Factories: ${report.totals.factories}; classes: ${report.totals.classes}`)
  lines.push(`- Summed wall: ${seconds(report.totals.summedWallMs)}; summed startup: ${seconds(report.totals.summedStartupMs)} (${Math.round((report.totals.startupFraction ?? 0) * 100)}% of wall)`)
  lines.push(`- Connection open (informational; all connection opens over the factory lifetime, never added to startup): ${seconds(report.totals.summedConnectionOpenMs)}`)
  lines.push(`- Wall p10/median/p75/p95: ${seconds(report.totals.wall.p10)} / ${seconds(report.totals.wall.median)} / ${seconds(report.totals.wall.p75)} / ${seconds(report.totals.wall.p95)}`)
  lines.push(`- Startup p10/median/p75/p95: ${seconds(report.totals.startup.p10)} / ${seconds(report.totals.startup.median)} / ${seconds(report.totals.startup.p75)} / ${seconds(report.totals.startup.p95)}`)
  lines.push('')
  lines.push('## Classes by summed startup')
  lines.push('')
  lines.push('| Class | Tests | Theory rows | Factories | Startup p50 | Wall p50 | Startup sum | Wall sum | Startup fraction |')
  lines.push('|---|---|---:|---:|---:|---:|---:|---:|---:|')
  for (const entry of report.classes) {
    lines.push(`| ${escapeMarkdown(entry.className)} | ${entry.tests} | ${entry.theoryRows} | ${entry.factories} | ${seconds(entry.startup.median)} | ${seconds(entry.wall.median)} | ${seconds(entry.summedStartupMs)} | ${seconds(entry.summedWallMs)} | ${entry.startupFraction === null ? '—' : `${Math.round(entry.startupFraction * 100)}%`} |`)
  }
  lines.push('')
  if (report.ambiguousTheoryRows.length > 0) {
    lines.push('## Ambiguous parameterized theory rows')
    lines.push('')
    lines.push('Per-invocation identity is not available from call-site attribution alone; these rows are')
    lines.push('reported separately and never merged into a fabricated multi-factory test.')
    lines.push('')
    for (const entry of report.ambiguousTheoryRows.slice(0, 30)) {
      lines.push(`- ${escapeMarkdown(entry.className)}.${escapeMarkdown(entry.method)}: ${entry.trxRows} TRX rows, ${entry.factories} factories, startup ${seconds(entry.startupMs)} (wall unavailable)`)
    }
    lines.push('')
  }
  if (report.unmatchedMethods.length > 0) {
    lines.push('## Unmatched factory methods (fixture/helpers)')
    lines.push('')
    for (const entry of report.unmatchedMethods.slice(0, 20)) {
      lines.push(`- ${escapeMarkdown(entry.className)}.${escapeMarkdown(entry.method)}: ${entry.factories} factories, no TRX row`)
    }
    lines.push('')
  }
  if (report.trxWithoutFactoryTelemetry.length > 0) {
    lines.push('## TRX tests without factory telemetry')
    lines.push('')
    lines.push('Tests with no factory telemetry record: either they do not build a hosted factory, or their')
    lines.push('telemetry was unavailable (for example the unwritable-path guard test). They are not claimed')
    lines.push('as hosted measurements.')
    lines.push('')
    for (const entry of report.trxWithoutFactoryTelemetry.slice(0, 20)) {
      lines.push(`- ${escapeMarkdown(entry.className)}.${escapeMarkdown(entry.method)}: ${seconds(entry.durationMs)}`)
    }
    lines.push('')
  }
  if (report.multipleFactoryTests.length > 0) {
    lines.push('## Tests creating multiple factories')
    lines.push('')
    for (const entry of report.multipleFactoryTests) {
      lines.push(`- ${escapeMarkdown(entry.className)}.${escapeMarkdown(entry.method)}: ${entry.factoryCount} factories`)
    }
    lines.push('')
  }
  if (report.slowestStartupTests.length > 0) {
    lines.push('## Slowest startup tests')
    lines.push('')
    for (const entry of report.slowestStartupTests.slice(0, 20)) {
      lines.push(`- ${escapeMarkdown(entry.className)}.${escapeMarkdown(entry.method)}: startup ${seconds(entry.startupMs)} / wall ${seconds(entry.wallMs)} / body ${seconds(entry.bodyMs)}`)
    }
    lines.push('')
  }
  return lines.join('\n')
}
