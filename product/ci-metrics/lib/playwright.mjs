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
        const durationMs = results.reduce((sum, result) => sum + (Number.isFinite(result.duration) ? result.duration : 0), 0)
        const finalStatus = results.at(-1)?.status ?? 'unknown'
        const failedCount = results.filter((result) => isFailure(result.status)).length
        const passedCount = results.filter((result) => result.status === 'passed').length
        const retries = Number.isInteger(test.retries) ? test.retries : Math.max(0, results.length - 1)
        out.tests.push({
          title: String(test.title ?? spec.title ?? 'untitled').slice(0, 400),
          file,
          status: String(finalStatus),
          retries,
          durationMs: Math.round(durationMs),
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
  if (![expected, unexpected, flaky, skipped].every(Number.isInteger)) {
    throw new PlaywrightParseError('Playwright stats are not all non-negative integers.')
  }

  const collected = { tests: [] }
  walkSuites(input.suites, collected)
  const tests = collected.tests
  const flakyTitles = tests.filter((test) => test.flaky).slice(0, 20).map((test) => test.title)

  return {
    totals: { expected, unexpected, flaky, skipped, executed: expected + unexpected + flaky + skipped },
    tests,
    flakyTitles,
    detailMissing: input.suites === undefined ? 'Report has no suites hierarchy; per-test detail is unavailable.' : null,
  }
}

export function specDurations(tests) {
  const byFile = new Map()
  for (const test of tests) {
    if (!test.file) continue
    const entry = byFile.get(test.file) ?? { name: test.file, durationMs: 0, tests: 0 }
    entry.durationMs += test.durationMs ?? 0
    entry.tests += 1
    byFile.set(test.file, entry)
  }
  return [...byFile.values()].sort((a, b) => b.durationMs - a.durationMs || a.name.localeCompare(b.name))
}
