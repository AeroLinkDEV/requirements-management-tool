// Parses a Playwright JSON report into bounded totals, per-test outcomes, retries, and flaky titles.

const MAX_PLAYWRIGHT_BYTES = 50 * 1024 * 1024

export class PlaywrightParseError extends Error {}

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

  const tests = Array.isArray(input.tests) ? input.tests : []
  const flakyTitles = []
  for (const test of tests) {
    const results = Array.isArray(test.results) ? test.results : []
    const passed = results.some((r) => r.status === 'passed')
    const failed = results.some((r) => r.status === 'failed' || r.status === 'timedOut' || r.status === 'interrupted')
    const retried = (results.length - 1) > 0
    if (passed && failed) {
      const title = test.title ?? test.fullTitle ?? 'untitled'
      if (flakyTitles.length < 20) flakyTitles.push(String(title).slice(0, 400))
    }
    void retried
  }

  return {
    totals: { expected, unexpected, flaky, skipped, executed: expected + unexpected + flaky + skipped },
    tests: tests.map((test) => ({
      title: String(test.title ?? test.fullTitle ?? 'untitled').slice(0, 400),
      file: String(test.file ?? '').slice(0, 300),
      status: test.status ?? 'unknown',
      retries: Number.isInteger(test.retries) ? test.retries : 0,
      durationMs: Number.isFinite(test.duration) ? Math.round(test.duration) : null,
    })),
    flakyTitles,
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
