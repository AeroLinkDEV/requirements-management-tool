import fs from 'node:fs'
import path from 'node:path'

const root = process.cwd()
const inventory = JSON.parse(fs.readFileSync(path.join(root, 'product/test-contracts/api-test-intent.json'), 'utf8'))
const testsRoot = path.join(root, 'product/tests/AeroLink.Api.Tests')
const files = fs.readdirSync(testsRoot).filter((name) => name.endsWith('.cs'))
const byClass = new Map()
for (const file of files) {
  const full = path.join(testsRoot, file)
  const text = fs.readFileSync(full, 'utf8')
  for (const match of text.matchAll(/(?:public\s+)?(?:sealed\s+)?class\s+([A-Za-z0-9_]+)/g)) {
    byClass.set(match[1], { file, lines: text.split(/\r?\n/) })
  }
}

const httpRegex = /\b(?:HttpClient|CreateClient|GetAsync|PostAsync|PutAsync|DeleteAsync|SendAsync|GetFromJsonAsync|PostAsJsonAsync|PutAsJsonAsync|ReadFromJsonAsync)\b|\/api\//
const serviceRegex = /\.Services\b|GetRequiredService|CreateScope|CreateAsyncScope|AeroLinkDbContext/
const rows = inventory.tests.filter((row) => row.intent === 'ef-translation')
const audited = rows.map((row) => {
  const source = byClass.get(row.cls)
  const start = Math.max(0, Number(row.sourceLines?.start ?? 1) - 1)
  const end = Math.max(start + 1, Number(row.sourceLines?.end ?? start + 1))
  const body = source ? source.lines.slice(start, end).join('\n') : ''
  const evidence = Array.isArray(row.hostEvidence) ? row.hostEvidence : []
  const http = evidence.includes('factory-client') || httpRegex.test(body)
  const services = evidence.includes('factory-services') || serviceRegex.test(body)
  return { ...row, file: source?.file ?? null, http, services, bodyHttp: httpRegex.test(body), bodyServices: serviceRegex.test(body) }
})

const buckets = {
  http: audited.filter((row) => row.http),
  serviceOnly: audited.filter((row) => !row.http && row.services),
  neither: audited.filter((row) => !row.http && !row.services),
}
const summarize = (items) => ({ methods: items.length, cases: items.reduce((n, row) => n + (row.cases ?? 0), 0), classes: new Set(items.map((row) => row.cls)).size })
console.log('EF_TRANSLATION_TOTAL=' + JSON.stringify(summarize(audited)))
console.log('EF_TRANSLATION_HTTP=' + JSON.stringify(summarize(buckets.http)))
console.log('EF_TRANSLATION_SERVICE_ONLY=' + JSON.stringify(summarize(buckets.serviceOnly)))
console.log('EF_TRANSLATION_NEITHER=' + JSON.stringify(summarize(buckets.neither)))

for (const [name, items] of Object.entries(buckets)) {
  console.log(`\n=== ${name} ===`)
  const grouped = new Map()
  for (const row of items) {
    const list = grouped.get(row.cls) ?? []
    list.push(row)
    grouped.set(row.cls, list)
  }
  for (const [cls, classRows] of [...grouped.entries()].sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]))) {
    console.log(`${cls}: ${classRows.length} methods / ${classRows.reduce((n, row) => n + (row.cases ?? 0), 0)} cases`)
    for (const row of classRows) console.log(`  - ${row.test} [${row.hosted}; ${row.hostEvidence.join(',') || 'no-host-evidence'}]`)
  }
}

const out = { total: summarize(audited), buckets: Object.fromEntries(Object.entries(buckets).map(([key, value]) => [key, { summary: summarize(value), rows: value.map(({ cls, test, cases, hosted, hostEvidence, file, bodyHttp, bodyServices }) => ({ cls, test, cases, hosted, hostEvidence, file, bodyHttp, bodyServices })) }])) }
fs.writeFileSync(path.join(process.env.RUNNER_TEMP ?? root, 'aerolink-566-ef-ceiling-audit.json'), JSON.stringify(out, null, 2) + '\n')
