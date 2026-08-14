// Parses the JUnit XML emitted by Node's built-in test runner (`node --test --test-reporter=junit`)
// into bounded structured totals and per-file durations.
//
// Node 24 emits a small, stable shape:
//   <testsuites>
//     <testcase name="..." time="0.026026" classname="test" file="...">
//       <skipped/> | <failure>...</failure>
//     </testcase>
//     <!-- tests 8 --> <!-- pass 8 --> ...
//   </testsuites>
// Counts are derived from testcase elements (the comment totals are sanity-checked when present) and
// never from console text.

const MAX_JUNIT_BYTES = 10 * 1024 * 1024

export class JunitParseError extends Error {}

function attributes(tag) {
  const attrs = {}
  const re = /([A-Za-z_][A-Za-z0-9_.-]*)\s*=\s*"((?:\\.|[^"\\])*)"/g
  let match
  while ((match = re.exec(tag)) !== null) attrs[match[1]] = match[2]
  return attrs
}

export function parseJunitXml(xml) {
  if (typeof xml !== 'string') throw new JunitParseError('JUnit input is not text.')
  if (Buffer.byteLength(xml, 'utf8') > MAX_JUNIT_BYTES) throw new JunitParseError('JUnit XML exceeds the 10 MB bounded parse limit.')
  if (!/<testsuites\b/.test(xml)) throw new JunitParseError('JUnit XML has no <testsuites> root; the file is not a usable test result.')

  const tests = []
  const startRe = /<testcase\b/g
  const positions = []
  let start
  while ((start = startRe.exec(xml)) !== null) positions.push(start.index)
  if (positions.length === 0) throw new JunitParseError('JUnit XML contains no testcase elements.')
  for (let index = 0; index < positions.length; index += 1) {
    const chunk = xml.slice(positions[index], index + 1 < positions.length ? positions[index + 1] : xml.length)
    const tagEnd = chunk.indexOf('>')
    if (tagEnd < 0) continue
    const attrs = attributes(chunk.slice('<testcase'.length, tagEnd))
    const selfClosing = chunk[tagEnd - 1] === '/'
    let inner = ''
    if (!selfClosing) {
      const rest = chunk.slice(tagEnd + 1)
      const close = rest.indexOf('</testcase>')
      if (close < 0) continue
      inner = rest.slice(0, close)
    }
    const skipped = /<skipped\b/.test(inner)
    const failed = /<failure\b/.test(inner)
    const cancelled = /<cancelled\b/.test(inner)
    const durationMs = /^\d+(?:\.\d+)?$/.test(attrs.time ?? '')
      ? Math.round(Number(attrs.time) * 1000)
      : null
    tests.push({
      name: attrs.name ?? '',
      file: attrs.file ?? '',
      className: attrs.classname ?? '',
      durationMs,
      skipped,
      failed,
      cancelled,
    })
  }

  const expected = tests.length
  const skipped = tests.filter((test) => test.skipped).length
  const failed = tests.filter((test) => test.failed || test.cancelled).length
  const executed = expected - skipped
  const passed = executed - failed
  if (passed < 0) throw new JunitParseError('JUnit totals are inconsistent: failures exceed executed tests.')

  const commentRe = /<!--\s*tests\s+(\d+)\s*-->/g
  const commentTotals = [...xml.matchAll(commentRe)].map((m) => Number(m[1]))
  if (commentTotals.length > 0 && commentTotals.some((count) => count !== expected)) {
    throw new JunitParseError(`JUnit testcase count (${expected}) does not match the reported comment totals.`)
  }

  return {
    totals: { total: expected, executed, passed, failed, skipped },
    tests,
  }
}

export function fileDurations(tests) {
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

// Report paths are never published verbatim: a Windows profile or CI workspace path would leak absolute
// user/workspace layout into the metrics artifact. Only the final path segment (bounded) is kept.
export function sanitizeFilePath(value) {
  if (typeof value !== 'string') return ''
  const segments = value.split(/[\\/]/).filter(Boolean)
  return (segments.at(-1) ?? '').slice(0, 300)
}
