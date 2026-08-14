// Parses a Playwright JSON report into bounded totals, per-test outcomes, retries, and flaky titles.
//
// The Playwright JSON reporter nests tests under suites[].specs[].tests[], with file/title metadata on the
// spec and status/duration/retry on each test result. The parser walks that real hierarchy; a report that
// only exposes `stats` still yields totals but reports the missing detail explicitly.

const MAX_PLAYWRIGHT_BYTES = 50 * 1024 * 1024

export class PlaywrightParseError extends Error {}

function isFailure(status) {
  return status === 'failed' || status === 'timedOut' || status === 'interrupted'
}

function walkSuites(suites, out) {
  for (const suite of Array.isArray(suites) ? suites : []) {
    for (const spec of Array.isArray(suite.specs) ? suite.specs : []) {
      const file = String(spec.file ?? '').slice(0, 300)
      for (const test of Array.isArray(spec.tests) ? spec.tests : []) {
        const results = Array.isArray(test.results) ? test.results : []
        const durations = results.map((result) => result.duration)
        const durationMs = durations.every(Number.isFinite) ? Math.round(durations.reduce((sum, value) => sum + value, 0)) : null
        const finalStatus = results.at(-1)?.status ?? 'unknown'
        const failedCount = results.filter((result) => isFailure(result.status)).length
        const passedCount = results.filter((result) => result.status === 'passed').length
        const retries = Number.isInteger(test.retries) ? test.retries : Math.max(0, results.length - 1)
        out.tests.push({
          title: String(test.title ?? spec.title ?? 'untitled').slice(0, 400),
          file,
          status: String(finalStatus),
          retries,
          durationMs,
          flaky: passedCount > 0 && failedCount > 0,
        })
      }
    }
    walkSuites(suite.suites, out)
  }
}

export function parsePlaywrightJson(input) {
  if (typeof input === 'string') {
    if (Buffer.byteLength(input, 'utf8') > MAX_PLAYWRIGHT_BYTES) throw new PlaywrightParseError('Playwright JSON exceeds the 50 MB bounded parse limit.')
    try {
      input = JSON.parse(input)
    } catch {
      throw new PlaywrightParseError('Playwright JSON is not valid JSON.')
    }
  }
  if (input === null || typeof input !== 'object') throw new PlaywrightParseError('Playwright JSON is not an object.')

  const stats = input.stats ?? {}
  const expected = Number(stats.expected ?? NaN)
  const unexpected = Number(stats.unexpected ?? NaN)
  const flaky = Number(stats.flaky ?? NaN)
  const skipped = Number(stats.skipped ?? NaN)
  if (![expected, unexpected, flaky, skipped].every((value) => Number.isInteger(value) && value >= 0)) {
    throw new PlaywrightParseError('Playwright stats must all be non-negative integers.')
  }

  const collected = { tests: [] }
  walkSuites(input.suites, collected)
  const tests = collected.tests

  if (input.suites !== undefined) {
    const passedRows = tests.filter((test) => test.status === 'passed').length
    const failedRows = tests.filter((test) => test.status === 'failed' || test.status === 'timedOut' || test.status === 'interrupted').length
    const skippedRows = tests.filter((test) => test.status === 'skipped').length
    const flakyRows = tests.filter((test) => test.flaky).length
    if (passedRows !== expected + flaky || failedRows !== unexpected || skippedRows !== skipped || flakyRows !== flaky) {
      throw new PlaywrightParseError(
        `Playwright stats are inconsistent with the test rows: stats expected=${expected} flaky=${flaky} unexpected=${unexpected} skipped=${skipped}, rows passed=${passedRows} failed=${failedRows} skipped=${skippedRows} flakyRows=${flakyRows}.`)
    }
  }
  const flakyTitlesAll = tests.filter((test) => test.flaky).map((test) => test.title)
  const flakyTitles = flakyTitlesAll.slice(0, 20)
  const planned = expected + unexpected + flaky + skipped

  return {
    totals: {
      expected,
      unexpected,
      flaky,
      skipped,
      planned,
      executed: planned - skipped,
      passed: expected + flaky,
    },
    tests,
    flakyTitles,
    flakyTitlesTotal: flakyTitlesAll.length,
    detailMissing: input.suites === undefined ? 'Report has no suites hierarchy; per-test detail is unavailable.' : null,
  }
}

export function specDurations(tests) {
  const byFile = new Map()
  for (const test of tests) {
    if (!test.file) continue
    const entry = byFile.get(test.file) ?? { name: test.file, durationMs: 0, tests: 0 }
    if (test.durationMs === null) entry.durationMs = null
    else if (entry.durationMs !== null) entry.durationMs += test.durationMs
    entry.tests += 1
    byFile.set(test.file, entry)
  }
  return [...byFile.values()].sort((a, b) => (b.durationMs ?? -1) - (a.durationMs ?? -1) || a.name.localeCompare(b.name))
}
