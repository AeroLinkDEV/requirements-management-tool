// #566 criterion 1: classify every API test by primary intent.
//
// The question the issue asks is "what does this test prove, and is a hosted server necessary to
// prove it?" Intent is read from what the test body actually does, not from its name. Each test gets
// exactly one primary intent, resolved in a fixed order, because a test that touches persistence *and*
// asserts a status code is primarily an HTTP-boundary test — the persistence is setup.

import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const ROOT = join(repoRoot, 'product/tests/AeroLink.Api.Tests')

function walk(dir) {
  const out = []
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) out.push(...walk(full))
    else if (entry.endsWith('.cs')) out.push(full)
  }
  return out
}

/** Split into test methods by brace depth from each [Fact]/[Theory], keeping the attribute block. */
function tests(source) {
  const out = []
  const attr = /^[ \t]*\[(Fact|Theory)\]/gm
  let match
  while ((match = attr.exec(source)) !== null) {
    const signature = /public\s+(?:async\s+)?[\w<>\[\]?]+\s+(\w+)\s*\(/.exec(source.slice(match.index, match.index + 600))
    const open = source.indexOf('{', match.index)
    if (open === -1) continue
    let depth = 0
    let end = open
    for (let i = open; i < source.length; i += 1) {
      if (source[i] === '{') depth += 1
      else if (source[i] === '}') { depth -= 1; if (depth === 0) { end = i; break } }
    }
    const attrBlock = source.slice(match.index, open)
    out.push({
      name: signature?.[1] ?? '(unnamed)',
      body: source.slice(open, end + 1),
      inlineData: (attrBlock.match(/\[InlineData\(/g) || []).length,
      kind: match[1],
    })
  }
  return out
}

// Resolved in order. The first match wins, so the ordering encodes "what is this test really for".
const INTENTS = [
  {
    key: 'auth-policy',
    label: 'Authentication / authorization wiring',
    level: 'API (must stay hosted)',
    test: (b) => /Unauthorized|Forbidden|StatusCodes\.Status401|StatusCodes\.Status403|SignInAsync\(.*wrong|WithoutRole|AsRole|RequireAuth|AllowAnonymous/.test(b),
  },
  {
    key: 'filesystem',
    label: 'Filesystem / evidence-root behaviour',
    level: 'API or Infrastructure (must stay hosted)',
    test: (b) => /EvidenceRoot|Path\.Combine|File\.(Exists|ReadAll|WriteAll)|Directory\.(Exists|Create)|FileStream|\.zip"/.test(b),
  },
  {
    key: 'startup-config',
    label: 'Startup, hosting and configuration',
    level: 'API (must stay hosted)',
    test: (b) => /CreateHost|UseSetting|IConfiguration|Environment\.|Program\.|StaticFiles|Kestrel|appsettings/.test(b),
  },
  {
    key: 'ef-translation',
    label: 'EF translation / relational constraints',
    level: 'Infrastructure (needs a database, not a host)',
    test: (b) => /DbUpdate|UniqueConstraint|SaveChangesAsync\(\)[\s\S]{0,80}Assert|AsNoTracking|Include\(|ThenInclude|FromSql|ExecuteUpdate|ExecuteDelete/.test(b),
  },
  {
    key: 'http-boundary',
    label: 'HTTP boundary: route, status, JSON shape',
    level: 'API (must stay hosted)',
    test: (b) => /HttpStatusCode\.|StatusCode|EnsureSuccessStatusCode|\.Content\.ReadFromJsonAsync|GetFromJsonAsync|PostAsJsonAsync|PutAsJsonAsync|\.(GetAsync|PostAsync|PutAsync|DeleteAsync|PatchAsync|SendAsync)\(/.test(b),
  },
  {
    key: 'rule-matrix',
    label: 'Business-rule matrix over data variations',
    level: 'Domain (migration candidate)',
    test: (_b, t) => t.inlineData >= 2,
  },
]

const FALLBACK = { key: 'in-process-logic', label: 'In-process logic with no HTTP and no client', level: 'Domain or Infrastructure (migration candidate)' }
const CLIENT = /CreateClient|HttpClient|\bclient\b|_host\b/

const rows = []
for (const file of walk(ROOT)) {
  const source = readFileSync(file, 'utf8')
  const cls = file.replace(/\\/g, '/').split('/').pop().replace(/\.cs$/, '')
  for (const t of tests(source)) {
    let intent = INTENTS.find((candidate) => candidate.test(t.body, t))
    if (!intent) {
      // Nothing matched: if it never names a client it is genuinely in-process; if it does, it is an
      // HTTP test whose assertions run through a helper.
      intent = CLIENT.test(t.body)
        ? { key: 'http-boundary', label: INTENTS.find((i) => i.key === 'http-boundary').label, level: 'API (must stay hosted)' }
        : FALLBACK
    }
    rows.push({ cls, test: t.name, intent: intent.key, label: intent.label, level: intent.level, cases: Math.max(1, t.inlineData) })
  }
}

const byIntent = new Map()
for (const row of rows) {
  const entry = byIntent.get(row.intent) ?? { label: row.label, level: row.level, tests: 0, cases: 0, classes: new Set() }
  entry.tests += 1
  entry.cases += row.cases
  entry.classes.add(row.cls)
  byIntent.set(row.intent, entry)
}

const totalTests = rows.length
const totalCases = rows.reduce((sum, r) => sum + r.cases, 0)

console.log(`Classified ${totalTests} test methods (${totalCases} cases) across ${new Set(rows.map((r) => r.cls)).size} classes\n`)
console.log('intent                tests   cases   classes   level')
const ordered = [...byIntent.entries()].sort((a, b) => b[1].tests - a[1].tests)
for (const [key, entry] of ordered) {
  console.log(
    `${key.padEnd(20)}${String(entry.tests).padStart(6)}${String(entry.cases).padStart(8)}${String(entry.classes.size).padStart(10)}   ${entry.level}`,
  )
}

const migratable = ordered.filter(([, e]) => /migration candidate/.test(e.level))
const migratableTests = migratable.reduce((s, [, e]) => s + e.tests, 0)
console.log('')
console.log(`Migration candidates: ${migratableTests} of ${totalTests} test methods (${((migratableTests / totalTests) * 100).toFixed(1)}%)`)
console.log(`#566 criterion 7 target: >= 20% of hosted test invocations removed or consolidated`)
console.log(migratableTests / totalTests >= 0.2 ? '  -> target reachable' : '  -> target NOT reachable; the escape clause applies')

const outputPath = process.argv[2] ?? join(repoRoot, 'product/test-contracts/api-test-intent.json')
writeFileSync(outputPath, `${JSON.stringify({
  schemaVersion: 'aerolink-api-test-intent/v1',
  totals: { tests: totalTests, cases: totalCases, classes: new Set(rows.map((r) => r.cls)).size },
  intents: Object.fromEntries(ordered.map(([key, e]) => [key, { label: e.label, level: e.level, tests: e.tests, cases: e.cases, classes: e.classes.size }])),
  tests: rows.map(({ cls, test, intent }) => ({ cls, test, intent })).sort((a, b) => a.cls.localeCompare(b.cls) || a.test.localeCompare(b.test)),
}, null, 2)}\n`, 'utf8')
console.log(`\nwrote ${outputPath}`)
