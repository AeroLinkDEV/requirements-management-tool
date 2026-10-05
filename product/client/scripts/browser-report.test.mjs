import test from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { createRequire } from 'node:module'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { randomUUID } from 'node:crypto'
import { browserStoragePath, removeBrowserStorage } from './browser-storage.mjs'
import { listZipEntries, readZipEntry } from '../../ci-metrics/lib/zip.mjs'
import { browserReportMetadata } from './browser-report-metadata.mjs'

const client = fileURLToPath(new URL('../', import.meta.url))
const require = createRequire(import.meta.url)
const cli = require.resolve('@playwright/test/cli')
const checkout = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: client, encoding: 'utf8' }).trim()
const workflow = readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8')
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:/Program Files', 'Git/bin/bash.exe') : 'bash'

test('execution provenance is opt-in and refuses an incomplete or invalid identity', () => {
  const identity = { AEROLINK_E2E_REPORT_EXECUTION: '1', GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '2',
    AEROLINK_E2E_REPORT_SHA: checkout, METRICS_JOB_ID: 'browser-pr', AEROLINK_E2E_SHARD: '1', AEROLINK_E2E_REPORT_SHARD_TOTAL: '1' }
  assert.deepEqual(browserReportMetadata({ ...identity, AEROLINK_E2E_REPORT_EXECUTION: '' }), {})
  for (const [field, invalid] of [
    ['AEROLINK_E2E_REPORT_EXECUTION', 'discovery'], ['GITHUB_RUN_ID', 'unknown'], ['GITHUB_RUN_ATTEMPT', '0'],
    ['AEROLINK_E2E_REPORT_SHA', 'main'], ['METRICS_JOB_ID', 'browser-production'], ['AEROLINK_E2E_SHARD', '2'],
    ['AEROLINK_E2E_REPORT_SHARD_TOTAL', '0'],
  ]) {
    for (const value of [undefined, invalid]) {
      if (field === 'AEROLINK_E2E_REPORT_EXECUTION' && value === undefined) continue
      assert.throws(() => browserReportMetadata({ ...identity, [field]: value }), /identity is missing or invalid/)
    }
  }
})

function shardScript(job, name) {
  const body = workflow.split(`\n  ${job}:\n`)[1]?.split(/\n  [a-z][a-z-]+:\n/)[0]
  assert.ok(body, `missing ${job}`)
  const step = body.split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0]
  const run = step?.split('        run: |\n')[1]
  assert.ok(run, `missing ${name} executable script`)
  return run.split('\n').map(line => line.startsWith('          ') ? line.slice(10) : line).join('\n')
    .replaceAll('${{ matrix.shard }}', '1').replaceAll('${{ strategy.job-total }}', '1')
}

function htmlData(path) {
  const text = readFileSync(path, 'utf8')
  const encoded = /<template id="playwrightReportBase64">data:application\/zip;base64,([^<]+)<\/template>/.exec(text)
  assert.ok(encoded, 'actual HTML must contain its Playwright report archive')
  const zip = Buffer.from(encoded[1], 'base64')
  const entries = listZipEntries(zip)
  return name => JSON.parse(readZipEntry(zip, entries.find(entry => entry.name === name)).toString())
}

// The owning workflow commands, real Playwright reporters and real product config participate. Pure metadata
// assertions cannot see discovery HTML surviving an execution that excluded the HTML reporter (#1460).
for (const [job, step, jsonName] of [
  ['browser-pr', 'Run browser journey shard', 'journey-durations-1.json'],
  ['browser-full', 'Run full browser shard', 'journey-durations-full-1.json'],
]) {
  test(`${job} retains executed reports with exact provenance and native pass/fail evidence`, { timeout: 60_000 }, () => {
    const parent = join(client, 'test-results')
    mkdirSync(parent, { recursive: true })
    const root = mkdtempSync(join(parent, 'report-regression-'))
    assert.equal(dirname(resolve(root)), resolve(parent))
    const config = join(root, 'playwright.config.ts')
    const report = join(root, 'html')
    const results = join(root, 'results')
    mkdirSync(join(root, 'scripts'))
    for (const script of ['plan-journey-shard.mjs', 'browser-storage-reporter.mjs', 'socket-snapshot-reporter.mjs']) {
      const source = pathToFileURL(join(client, 'scripts', script)).href
      writeFileSync(join(root, 'scripts', script), script === 'plan-journey-shard.mjs'
        ? `import ${JSON.stringify(source)};` : `export { default } from ${JSON.stringify(source)};`)
    }
    writeFileSync(config, `import original from ${JSON.stringify(join(client, 'playwright.config.ts').replaceAll('\\', '/'))};
      export default { ...original, globalSetup: undefined, webServer: [], testDir: '.', testMatch: 'report.spec.ts',
        outputDir: ${JSON.stringify(results)}, retries: 0, reporter: [['list'], ['html', { open: 'never', outputFolder: ${JSON.stringify(report)} }]],
        use: { ...original.use, trace: 'retain-on-failure', screenshot: 'only-on-failure' } };`)
    const script = `npx() {
      local status=0
      node "$REPORT_TEST_CLI" "\u0024{@:2}" "--config=$REPORT_TEST_CONFIG" || status=$?
      if [[ "$*" == *--list* && -f "$PLAYWRIGHT_HTML_OUTPUT_DIR/index.html" ]]; then
        echo 'Discovery created HTML under the execution-report path' >&2
        return 42
      fi
      return "$status"
    }
    ${shardScript(job, step)}`
    const runIds = []
    try {
      for (const fails of [false, true]) {
        const runId = randomUUID(); runIds.push(runId)
        writeFileSync(join(root, 'report.spec.ts'), `import playwright from ${JSON.stringify(require.resolve('@playwright/test').replaceAll('\\', '/'))}; const { test, expect } = playwright;
          test('first native assertion', async ({ page }) => { await page.setContent('<p>first</p>'); await expect(page.locator('p')).toHaveText('first'); });
          test('second native assertion', async ({ page }) => { await page.setContent('<p>second</p>'); expect(${fails ? '1' : '2'}).toBe(2); });`)
        const executed = spawnSync(bash, ['-c', script], { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 45_000,
          env: { ...process.env, CI: '', FORCE_COLOR: '0', FMS_ONLY: 'false',
            REPORT_TEST_CLI: cli.replaceAll('\\', '/'), REPORT_TEST_CONFIG: config.replaceAll('\\', '/'),
            PLAYWRIGHT_HTML_OPEN: 'never', PLAYWRIGHT_HTML_OUTPUT_DIR: report, PLAYWRIGHT_JSON_OUTPUT_NAME: jsonName,
            AEROLINK_E2E_RUN_ID: runId, AEROLINK_E2E_SHARD: '1',
            AEROLINK_E2E_REPORT_EXECUTION: '', AEROLINK_E2E_REPORT_SHA: '', AEROLINK_E2E_REPORT_SHARD_TOTAL: '',
            GITHUB_RUN_ID: '37075488558', GITHUB_RUN_ATTEMPT: '2', METRICS_JOB_ID: job } })
        const diagnostic = `${executed.stdout}\n${executed.stderr}`
        assert.equal(executed.error, undefined, diagnostic)
        assert.equal(executed.status, fails ? 1 : 0, diagnostic)
        const native = JSON.parse(readFileSync(join(root, jsonName), 'utf8'))
        const data = htmlData(join(report, 'index.html'))
        const html = data('report.json')
        const identity = { reportKind: 'execution', runId: '37075488558', runAttempt: '2', checkoutSha: checkout, job, shard: '1', shardTotal: '1' }
        assert.deepEqual(native.config.metadata.browserExecution, identity)
        assert.deepEqual(html.metadata.browserExecution, identity)
        for (const stats of [native.stats, html.stats]) {
          assert.equal(stats.expected, fails ? 1 : 2)
          assert.equal(stats.unexpected, fails ? 1 : 0)
          assert.equal(stats.skipped, 0)
          assert.equal(stats.flaky, 0)
        }
        const records = html.files.flatMap(file => data(`${file.fileId}.json`).tests)
        assert.equal(records.length, 2)
        assert.ok(records.every(record => record.results.length === 1), 'discovery-only records have no executed result')
        const nativeRecords = []
        const collect = suite => {
          nativeRecords.push(...suite.specs.flatMap(spec => spec.tests.flatMap(item => item.results)))
          ;(suite.suites ?? []).forEach(collect)
        }
        native.suites.forEach(collect)
        assert.equal(nativeRecords.length, 2)
        if (fails) {
          const failure = records.find(record => record.outcome === 'unexpected').results[0]
          assert.ok(failure.errors.length > 0, 'native assertion failure stays visible')
          const nativeFailure = nativeRecords.find(record => record.status === 'failed')
          assert.ok(nativeFailure.errors.length > 0)
          for (const name of ['screenshot', 'trace']) {
            const attachment = failure.attachments.find(item => item.name === name)
            assert.ok(attachment, `missing native ${name}`)
            assert.ok(existsSync(join(report, attachment.path)), `missing retained ${name} bytes`)
            const nativeAttachment = nativeFailure.attachments.find(item => item.name === name)
            assert.ok(nativeAttachment, `missing native JSON ${name}`)
            assert.ok(existsSync(nativeAttachment.path), `missing native JSON ${name} bytes`)
          }
        } else {
          assert.equal(readFileSync(join(root, 'plan.txt'), 'utf8').trim().split('\n')[0], '2')
        }
        assert.equal(existsSync(browserStoragePath(runId)), false, 'existing storage reporter still completes cleanup')
        rmSync(report, { recursive: true, force: true })
      }
    } finally {
      for (const runId of runIds) removeBrowserStorage(runId)
      rmSync(root, { recursive: true, force: true })
    }
  })
}
