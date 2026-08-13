// Parses a .NET TRX result file into bounded structured test totals and per-test outcomes.
//
// TRX is XML with a stable, machine-generated shape. The whole document is never needed; the parser reads
// exactly the elements the metrics fragment consumes:
//   <Counters total=... executed=... passed=... failed=... .../>
//   <UnitTest ...><TestMethod className="Namespace.Class" name="..." .../></UnitTest>
//   <UnitTestResult testId=... testName=... outcome=... duration="00:00:01.234" .../>

const MAX_TRX_BYTES = 50 * 1024 * 1024

export class TrxParseError extends Error {}

function attributes(tag) {
  const attrs = {}
  const re = /([A-Za-z_][A-Za-z0-9_.-]*)\s*=\s*"((?:\\.|[^"\\])*)"/g
  let match
  while ((match = re.exec(tag)) !== null) attrs[match[1]] = match[2]
  return attrs
}

export function parseDuration(value) {
  // TRX durations look like "00:00:01.2345678" (days can appear as "1.00:00:01").
  if (typeof value !== 'string') return null
  const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value.trim())
  if (!match) return null
  const [, days, hours, minutes, seconds, fraction] = match
  const fractionMs = fraction ? Number(`0.${fraction}`) * 1000 : 0
  return Math.round(((Number(days ?? 0) * 24 + Number(hours)) * 60 + Number(minutes)) * 60 * 1000 +
    Number(seconds) * 1000 + fractionMs)
}

export function parseTrx(xml) {
  if (typeof xml !== 'string') throw new TrxParseError('TRX input is not text.')
  if (Buffer.byteLength(xml, 'utf8') > MAX_TRX_BYTES) throw new TrxParseError('TRX exceeds the 50 MB bounded parse limit.')

  const countersMatch = /<Counters\b([^>]*)\/>/.exec(xml)
  if (!countersMatch) throw new TrxParseError('TRX has no <Counters> element; the file is not a usable test result.')
  const counters = attributes(countersMatch[1])

  const totals = {
    total: Number(counters.total ?? NaN),
    executed: Number(counters.executed ?? NaN),
    passed: Number(counters.passed ?? NaN),
    failed: Number(counters.failed ?? NaN),
    skipped: Number(counters.notExecuted ?? NaN),
  }
  if (!Number.isInteger(totals.total) || totals.total < 0) throw new TrxParseError('TRX <Counters total> is not a non-negative integer.')

  const classByTestId = new Map()
  const unitTestRe = /<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g
  let unitMatch
  while ((unitMatch = unitTestRe.exec(xml)) !== null) {
    const unit = attributes(unitMatch[1])
    const method = /<TestMethod\b([^>]*)\/>/.exec(unitMatch[2])
    if (unit.id && method) {
      const methodAttrs = attributes(method[1])
      classByTestId.set(unit.id, methodAttrs.className ?? '')
    }
  }

  const tests = []
  const resultRe = /<UnitTestResult\b([^>]*)\/>/g
  let resultMatch
  while ((resultMatch = resultRe.exec(xml)) !== null) {
    const result = attributes(resultMatch[1])
    tests.push({
      className: classByTestId.get(result.testId ?? '') ?? '',
      name: result.testName ?? '',
      outcome: result.outcome ?? '',
      durationMs: parseDuration(result.duration),
    })
  }

  return { totals, tests }
}

export function classDurations(tests) {
  const byClass = new Map()
  for (const test of tests) {
    if (!test.className) continue
    const entry = byClass.get(test.className) ?? { name: test.className, durationMs: 0, tests: 0 }
    entry.durationMs += test.durationMs ?? 0
    entry.tests += 1
    byClass.set(test.className, entry)
  }
  return [...byClass.values()].sort((a, b) => b.durationMs - a.durationMs || a.name.localeCompare(b.name))
}
