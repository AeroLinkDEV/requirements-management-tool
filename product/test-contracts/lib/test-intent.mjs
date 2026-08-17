import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'

export const INTENTS = [
  {
    key: 'auth-policy',
    label: 'Authentication / authorization wiring',
    level: 'API (must stay hosted)',
    test: (body) => /Unauthorized|Forbidden|StatusCodes\.Status401|StatusCodes\.Status403|SignInAsync\(.*wrong|WithoutRole|AsRole|RequireAuth|AllowAnonymous/.test(body),
  },
  {
    key: 'filesystem',
    label: 'Filesystem / evidence-root behaviour',
    level: 'API or Infrastructure (must stay hosted)',
    test: (body) => /EvidenceRoot|Path\.Combine|File\.(Exists|ReadAll|WriteAll)|Directory\.(Exists|Create)|FileStream|\.zip"/.test(body),
  },
  {
    key: 'startup-config',
    label: 'Startup, hosting and configuration',
    level: 'API (must stay hosted)',
    test: (body) => /CreateHost|UseSetting|IConfiguration|Environment\.|Program\.|StaticFiles|Kestrel|appsettings/.test(body),
  },
  {
    key: 'ef-translation',
    label: 'EF translation / relational constraints',
    level: 'Infrastructure (needs a database, not a host)',
    test: (body) => /DbUpdate|UniqueConstraint|SaveChangesAsync\(\)[\s\S]{0,80}Assert|AsNoTracking|Include\(|ThenInclude|FromSql|ExecuteUpdate|ExecuteDelete/.test(body),
  },
  {
    key: 'http-boundary',
    label: 'HTTP boundary: route, status, JSON shape',
    level: 'API (must stay hosted)',
    test: (body) => /HttpStatusCode\.|StatusCode|EnsureSuccessStatusCode|\.Content\.ReadFromJsonAsync|GetFromJsonAsync|PostAsJsonAsync|PutAsJsonAsync|\.(GetAsync|PostAsync|PutAsync|DeleteAsync|PatchAsync|SendAsync)\(/.test(body),
  },
  {
    key: 'rule-matrix',
    label: 'Business-rule matrix over data variations',
    level: 'Domain (migration candidate)',
    test: (_body, test) => test.inlineData >= 2,
  },
]

const FALLBACK = {
  key: 'in-process-logic',
  label: 'In-process logic with no HTTP and no client',
  level: 'Domain or Infrastructure (migration candidate)',
}

const CLIENT = /CreateClient|HttpClient|\bclient\b|_host\b/

const HOST_EVIDENCE = [
  ['factory-construction', /\bnew\s+(?:AeroLinkApiFactory|SharedApiHost)\s*\(/],
  ['shared-host-fixture', /\b(?:SharedApiHost|_host)\b/],
  ['showcase-factory', /\b(?:showcase|fixture)\s*\.\s*CreateFactory\s*\(/],
  ['factory-client', /\bCreateClient\s*\(/],
  ['factory-services', /\b(?:factory|root|_host|host)\s*\.\s*(?:Services|ConnectionString|Factory)\b/],
  ['host-customization', /\bWithWebHostBuilder\s*\(/],
]

const CLASS_HOST_CONTEXT = /\bnew\s+(?:AeroLinkApiFactory|SharedApiHost)\s*\(|\b(?:SharedApiHost|ShowcaseApiFixture|IClassFixture<|ShowcaseApiCollection)\b|\bCreateFactory\s*\(/s

function walk(dir) {
  const out = []
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) out.push(...walk(full))
    else if (entry.endsWith('.cs')) out.push(full)
  }
  return out
}

function matchingBrace(source, open) {
  let depth = 0
  let state = 'code'
  let rawQuoteLength = 0
  for (let i = open; i < source.length; i += 1) {
    const ch = source[i]
    const next = source[i + 1]
    if (state === 'line-comment') {
      if (ch === '\n') state = 'code'
      continue
    }
    if (state === 'block-comment') {
      if (ch === '*' && next === '/') { state = 'code'; i += 1 }
      continue
    }
    if (state === 'string') {
      if (ch === '\\') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'verbatim-string') {
      if (ch === '"' && next === '"') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'char') {
      if (ch === '\\') { i += 1; continue }
      if (ch === "'") state = 'code'
      continue
    }
    if (state === 'raw-string') {
      if (source.slice(i, i + rawQuoteLength) === '"'.repeat(rawQuoteLength)) {
        i += rawQuoteLength - 1
        state = 'code'
      }
      continue
    }
    if (ch === '/' && next === '/') { state = 'line-comment'; i += 1; continue }
    if (ch === '/' && next === '*') { state = 'block-comment'; i += 1; continue }
    if ((ch === '@' && next === '$' && source[i + 2] === '"') || (ch === '$' && next === '@' && source[i + 2] === '"')) {
      state = 'verbatim-string'; i += 2; continue
    }
    if (ch === '@' && next === '"') { state = 'verbatim-string'; i += 1; continue }
    if (ch === '"') {
      let quotes = 1
      while (source[i + quotes] === '"') quotes += 1
      if (quotes >= 3) { state = 'raw-string'; rawQuoteLength = quotes; i += quotes - 1; continue }
      state = 'string'; continue
    }
    if (ch === "'") { state = 'char'; continue }
    if (ch === '{') depth += 1
    if (ch === '}' && --depth === 0) return i
  }
  return -1
}

/** Find an attribute-list closing bracket while ignoring brackets inside C# literals/comments. */
function matchingBracket(source, open) {
  let depth = 0
  let state = 'code'
  let rawQuoteLength = 0
  for (let i = open; i < source.length; i += 1) {
    const ch = source[i]
    const next = source[i + 1]
    if (state === 'line-comment') {
      if (ch === '\n') state = 'code'
      continue
    }
    if (state === 'block-comment') {
      if (ch === '*' && next === '/') { state = 'code'; i += 1 }
      continue
    }
    if (state === 'string') {
      if (ch === '\\') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'verbatim-string') {
      if (ch === '"' && next === '"') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'char') {
      if (ch === '\\') { i += 1; continue }
      if (ch === "'") state = 'code'
      continue
    }
    if (state === 'raw-string') {
      if (source.slice(i, i + rawQuoteLength) === '"'.repeat(rawQuoteLength)) {
        i += rawQuoteLength - 1
        state = 'code'
      }
      continue
    }
    if (ch === '/' && next === '/') { state = 'line-comment'; i += 1; continue }
    if (ch === '/' && next === '*') { state = 'block-comment'; i += 1; continue }
    if ((ch === '@' && next === '$' && source[i + 2] === '"') || (ch === '$' && next === '@' && source[i + 2] === '"')) {
      state = 'verbatim-string'; i += 2; continue
    }
    if (ch === '@' && next === '"') { state = 'verbatim-string'; i += 1; continue }
    if (ch === '"') {
      let quotes = 1
      while (source[i + quotes] === '"') quotes += 1
      if (quotes >= 3) { state = 'raw-string'; rawQuoteLength = quotes; i += quotes - 1; continue }
      state = 'string'; continue
    }
    if (ch === "'") { state = 'char'; continue }
    if (ch === '[') depth += 1
    if (ch === ']' && --depth === 0) return i
  }
  return -1
}

function splitAttributeItems(source) {
  const items = []
  let start = 0
  let state = 'code'
  let rawQuoteLength = 0
  let parentheses = 0
  let braces = 0
  let brackets = 0
  for (let i = 0; i < source.length; i += 1) {
    const ch = source[i]
    const next = source[i + 1]
    if (state === 'line-comment') {
      if (ch === '\n') state = 'code'
      continue
    }
    if (state === 'block-comment') {
      if (ch === '*' && next === '/') { state = 'code'; i += 1 }
      continue
    }
    if (state === 'string') {
      if (ch === '\\') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'verbatim-string') {
      if (ch === '"' && next === '"') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'char') {
      if (ch === '\\') { i += 1; continue }
      if (ch === "'") state = 'code'
      continue
    }
    if (state === 'raw-string') {
      if (source.slice(i, i + rawQuoteLength) === '"'.repeat(rawQuoteLength)) {
        i += rawQuoteLength - 1
        state = 'code'
      }
      continue
    }
    if (ch === '/' && next === '/') { state = 'line-comment'; i += 1; continue }
    if (ch === '/' && next === '*') { state = 'block-comment'; i += 1; continue }
    if ((ch === '@' && next === '$' && source[i + 2] === '"') || (ch === '$' && next === '@' && source[i + 2] === '"')) {
      state = 'verbatim-string'; i += 2; continue
    }
    if (ch === '@' && next === '"') { state = 'verbatim-string'; i += 1; continue }
    if (ch === '"') {
      let quotes = 1
      while (source[i + quotes] === '"') quotes += 1
      if (quotes >= 3) { state = 'raw-string'; rawQuoteLength = quotes; i += quotes - 1; continue }
      state = 'string'; continue
    }
    if (ch === "'") { state = 'char'; continue }
    if (ch === '(') parentheses += 1
    else if (ch === ')' && parentheses > 0) parentheses -= 1
    else if (ch === '{') braces += 1
    else if (ch === '}' && braces > 0) braces -= 1
    else if (ch === '[') brackets += 1
    else if (ch === ']' && brackets > 0) brackets -= 1
    else if (ch === ',' && parentheses === 0 && braces === 0 && brackets === 0) {
      items.push(source.slice(start, i))
      start = i + 1
    }
  }
  items.push(source.slice(start))
  return items
}

function attributeName(item) {
  return /^\s*(?:(?:global::)?[A-Za-z_]\w*\.)*(Fact|Theory|InlineData)(?:Attribute)?\b/.exec(item)?.[1] ?? null
}

function attributePosition(source, index) {
  const lineStart = source.lastIndexOf('\n', index) + 1
  const prefix = source.slice(lineStart, index).trim()
  if (!prefix) return true
  let previous = index - 1
  while (previous >= lineStart && /\s/.test(source[previous])) previous -= 1
  return source[previous] === ']'
}

/** Collect attribute lists without treating bracket text in comments or strings as attributes. */
function attributeBlocks(source) {
  const blocks = []
  let state = 'code'
  let rawQuoteLength = 0
  for (let i = 0; i < source.length; i += 1) {
    const ch = source[i]
    const next = source[i + 1]
    if (state === 'line-comment') {
      if (ch === '\n') state = 'code'
      continue
    }
    if (state === 'block-comment') {
      if (ch === '*' && next === '/') { state = 'code'; i += 1 }
      continue
    }
    if (state === 'string') {
      if (ch === '\\') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'verbatim-string') {
      if (ch === '"' && next === '"') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'char') {
      if (ch === '\\') { i += 1; continue }
      if (ch === "'") state = 'code'
      continue
    }
    if (state === 'raw-string') {
      if (source.slice(i, i + rawQuoteLength) === '"'.repeat(rawQuoteLength)) {
        i += rawQuoteLength - 1
        state = 'code'
      }
      continue
    }
    if (ch === '/' && next === '/') { state = 'line-comment'; i += 1; continue }
    if (ch === '/' && next === '*') { state = 'block-comment'; i += 1; continue }
    if ((ch === '@' && next === '$' && source[i + 2] === '"') || (ch === '$' && next === '@' && source[i + 2] === '"')) {
      state = 'verbatim-string'; i += 2; continue
    }
    if (ch === '@' && next === '"') { state = 'verbatim-string'; i += 1; continue }
    if (ch === '"') {
      let quotes = 1
      while (source[i + quotes] === '"') quotes += 1
      if (quotes >= 3) { state = 'raw-string'; rawQuoteLength = quotes; i += quotes - 1; continue }
      state = 'string'; continue
    }
    if (ch === "'") { state = 'char'; continue }
    if (ch !== '[' || !attributePosition(source, i)) continue
    const close = matchingBracket(source, i)
    if (close < 0) continue
    const names = splitAttributeItems(source.slice(i + 1, close)).map(attributeName).filter(Boolean)
    blocks.push({ start: i, end: close + 1, names, kind: names.find((name) => name === 'Fact' || name === 'Theory') ?? null })
    i = close
  }
  return blocks
}

function skipTriviaAndAttributes(source, start) {
  let position = start
  while (position < source.length) {
    while (/\s/.test(source[position] ?? '')) position += 1
    if (source.startsWith('//', position)) {
      const newline = source.indexOf('\n', position + 2)
      position = newline < 0 ? source.length : newline + 1
      continue
    }
    if (source.startsWith('/*', position)) {
      const close = source.indexOf('*/', position + 2)
      position = close < 0 ? source.length : close + 2
      continue
    }
    if (source[position] === '[') {
      const close = matchingBracket(source, position)
      if (close >= 0) { position = close + 1; continue }
    }
    break
  }
  return position
}

/** Find the semicolon terminating an expression-bodied method, ignoring nested expressions and literals. */
function matchingExpressionSemicolon(source, start) {
  let state = 'code'
  let rawQuoteLength = 0
  let parentheses = 0
  let brackets = 0
  let braces = 0
  for (let i = start; i < source.length; i += 1) {
    const ch = source[i]
    const next = source[i + 1]
    if (state === 'line-comment') {
      if (ch === '\n') state = 'code'
      continue
    }
    if (state === 'block-comment') {
      if (ch === '*' && next === '/') { state = 'code'; i += 1 }
      continue
    }
    if (state === 'string') {
      if (ch === '\\') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'verbatim-string') {
      if (ch === '"' && next === '"') { i += 1; continue }
      if (ch === '"') state = 'code'
      continue
    }
    if (state === 'char') {
      if (ch === '\\') { i += 1; continue }
      if (ch === "'") state = 'code'
      continue
    }
    if (state === 'raw-string') {
      if (source.slice(i, i + rawQuoteLength) === '"'.repeat(rawQuoteLength)) {
        i += rawQuoteLength - 1
        state = 'code'
      }
      continue
    }
    if (ch === '/' && next === '/') { state = 'line-comment'; i += 1; continue }
    if (ch === '/' && next === '*') { state = 'block-comment'; i += 1; continue }
    if ((ch === '@' && next === '$' && source[i + 2] === '"') || (ch === '$' && next === '@' && source[i + 2] === '"')) {
      state = 'verbatim-string'; i += 2; continue
    }
    if (ch === '@' && next === '"') { state = 'verbatim-string'; i += 1; continue }
    if (ch === '"') {
      let quotes = 1
      while (source[i + quotes] === '"') quotes += 1
      if (quotes >= 3) { state = 'raw-string'; rawQuoteLength = quotes; i += quotes - 1; continue }
      state = 'string'
      continue
    }
    if (ch === "'") { state = 'char'; continue }
    if (ch === '(') parentheses += 1
    else if (ch === ')' && parentheses > 0) parentheses -= 1
    else if (ch === '[') brackets += 1
    else if (ch === ']' && brackets > 0) brackets -= 1
    else if (ch === '{') braces += 1
    else if (ch === '}' && braces > 0) braces -= 1
    else if (ch === ';' && parentheses === 0 && brackets === 0 && braces === 0) return i
  }
  return -1
}

/** Split test methods after the method signature, so braces in attributes cannot become the body. */
export function extractTests(source) {
  const out = []
  const blocks = attributeBlocks(source)
  for (const testAttribute of blocks.filter((block) => block.kind)) {
    const signatureStart = skipTriviaAndAttributes(source, testAttribute.end)
    const window = source.slice(signatureStart, signatureStart + 4000)
    const signatureMatch = /^(?:public|private|internal|protected)\s+(?:(?:static|async|virtual|override|sealed|new)\s+)*[\w<>\[\],\s?]+?\s+(\w+)\s*\([^)]*\)\s*(?:=>|\{)/.exec(window)
    if (!signatureMatch) continue
    const marker = signatureMatch[0].slice(-1)
    const bodyStart = signatureStart + signatureMatch[0].length - (marker === '{' ? 1 : 0)
    const end = marker === '{'
      ? matchingBrace(source, bodyStart)
      : matchingExpressionSemicolon(source, bodyStart)
    if (end === -1) continue
    const inlineData = blocks
      .filter((block) => block.start >= testAttribute.start && block.end <= signatureStart)
      .reduce((count, block) => count + block.names.filter((name) => name === 'InlineData').length, 0)
    out.push({
      name: signatureMatch[1],
      body: source.slice(bodyStart, end + 1),
      inlineData,
      kind: testAttribute.kind,
      cases: testAttribute.kind === 'Fact' ? 1 : inlineData > 0 ? inlineData : null,
      caseEvidence: testAttribute.kind === 'Fact' ? 'Fact' : inlineData > 0 ? 'InlineData' : 'runtime-theory-data',
      sourceLines: {
        start: source.slice(0, testAttribute.start).split('\n').length,
        end: source.slice(0, end + 1).split('\n').length,
      },
    })
  }
  return out
}

export function hostEvidence(body) {
  return HOST_EVIDENCE.filter(([, pattern]) => pattern.test(body)).map(([key]) => key)
}

export function classifyIntent(test) {
  const directIntent = INTENTS.find((candidate) => candidate.key !== 'rule-matrix' && candidate.test(test.body, test))
  if (directIntent) return directIntent
  if (CLIENT.test(test.body)) return INTENTS.find((item) => item.key === 'http-boundary')
  const matrix = INTENTS.find((item) => item.key === 'rule-matrix')
  if (matrix.test(test.body, test)) return matrix
  return FALLBACK
}

export function classifyFile(source, cls) {
  const classHasHostContext = CLASS_HOST_CONTEXT.test(source)
  return extractTests(source).map((test) => {
    const intent = classifyIntent(test)
    const evidence = hostEvidence(test.body)
    const hosted = evidence.length > 0 ? 'hosted' : classHasHostContext ? 'unknown' : 'not-hosted'
    return {
      cls,
      test: test.name,
      kind: test.kind,
      intent: intent.key,
      label: intent.label,
      level: intent.level,
      cases: test.cases,
      caseEvidence: test.caseEvidence,
      hosted,
      hostEvidence: evidence,
      sourceLines: test.sourceLines,
    }
  })
}

export function inventoryFromDirectory(root) {
  return walk(root).flatMap((file) => classifyFile(fileContents(file), file.replace(/\\/g, '/').split('/').pop().replace(/\.cs$/, '')))
}

/** Build the committed artifact shape directly from the current C# tree. */
export function buildIntentArtifact(root) {
  const rows = inventoryFromDirectory(root)
  const summary = summariseInventory(rows)
  return {
    schemaVersion: 'aerolink-api-test-intent/v2',
    totals: {
      tests: summary.totalTests,
      cases: summary.totalCases,
      classes: summary.classes,
      hostedTests: summary.hostedTests,
      hostedCases: summary.hostedCases,
      nonHostedTests: summary.nonHostedTests,
      nonHostedCases: summary.nonHostedCases,
      unknownHostTests: summary.unknownHostTests,
      unknownHostCases: summary.unknownHostCases,
      hostedCandidateTests: summary.hostedCandidateTests,
      hostedCandidateCases: summary.hostedCandidateCases,
      unknownCandidateTests: summary.unknownCandidateTests,
      unknownCandidateCases: summary.unknownCandidateCases,
      unknownCaseTests: summary.unknownCaseTests,
      criterion7: summary.criterion7,
    },
    intents: summary.intents,
    tests: rows
      .slice()
      .sort((a, b) => a.cls.localeCompare(b.cls) || a.test.localeCompare(b.test))
      .map((row) => ({
        cls: row.cls,
        test: row.test,
        kind: row.kind,
        intent: row.intent,
        cases: row.cases,
        caseEvidence: row.caseEvidence,
        hosted: row.hosted,
        hostEvidence: row.hostEvidence,
        sourceLines: row.sourceLines,
      })),
  }
}

function fileContents(file) {
  return readFileSync(file, 'utf8')
}

function sumKnown(rows, selector = (row) => row.cases) {
  return rows.reduce((sum, row) => sum + (selector(row) ?? 0), 0)
}

export function summariseInventory(rows) {
  const byIntent = new Map()
  for (const row of rows) {
    const entry = byIntent.get(row.intent) ?? { label: row.label, level: row.level, tests: 0, cases: 0, unknownCases: 0, classes: new Set() }
    entry.tests += 1
    if (row.cases === null) entry.unknownCases += 1
    else entry.cases += row.cases
    entry.classes.add(row.cls)
    byIntent.set(row.intent, entry)
  }
  const candidates = (row) => /^(in-process-logic|rule-matrix)$/.test(row.intent)
  const hosted = rows.filter((row) => row.hosted === 'hosted')
  const notHosted = rows.filter((row) => row.hosted === 'not-hosted')
  const unknown = rows.filter((row) => row.hosted === 'unknown')
  const hostedCandidates = hosted.filter(candidates)
  const unknownCandidates = unknown.filter(candidates)
  const unknownCaseRows = rows.filter((row) => row.cases === null)
  const hostedCases = sumKnown(hosted)
  const hostedCandidateCases = sumKnown(hostedCandidates)
  const criterion7 = unknown.length > 0 || unknownCaseRows.length > 0 || unknownCandidates.length > 0
    ? 'unresolved'
    : hostedCandidateCases / hostedCases >= 0.2 ? 'target-reachable' : 'escape-clause-supported'
  const ordered = [...byIntent.entries()].sort((a, b) => b[1].tests - a[1].tests || a[0].localeCompare(b[0]))
  return {
    totalTests: rows.length,
    totalCases: sumKnown(rows),
    unknownCaseTests: unknownCaseRows.length,
    classes: new Set(rows.map((row) => row.cls)).size,
    hostedTests: hosted.length,
    hostedCases,
    nonHostedTests: notHosted.length,
    nonHostedCases: sumKnown(notHosted),
    unknownHostTests: unknown.length,
    unknownHostCases: sumKnown(unknown),
    hostedCandidateTests: hostedCandidates.length,
    hostedCandidateCases,
    unknownCandidateTests: unknownCandidates.length,
    unknownCandidateCases: sumKnown(unknownCandidates),
    criterion7,
    intents: Object.fromEntries(ordered.map(([key, entry]) => [key, {
      label: entry.label,
      level: entry.level,
      tests: entry.tests,
      cases: entry.cases,
      unknownCases: entry.unknownCases,
      classes: entry.classes.size,
    }])),
  }
}

export { CLASS_HOST_CONTEXT, HOST_EVIDENCE }
