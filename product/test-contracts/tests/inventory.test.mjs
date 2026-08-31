import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { buildHostArtifact, classifyClass } from '../lib/host-classification.mjs'
import {
  buildIntentArtifact,
  classifyFile,
  INVENTORY_SUMMARY_END,
  INVENTORY_SUMMARY_START,
  renderInventorySummary,
  summariseInventory,
} from '../lib/test-intent.mjs'

const intentArtifact = JSON.parse(readFileSync(new URL('../api-test-intent.json', import.meta.url), 'utf8'))
const hostArtifact = JSON.parse(readFileSync(new URL('../api-host-classification.json', import.meta.url), 'utf8'))
const hostOverrides = JSON.parse(readFileSync(new URL('../api-host-classification-overrides.json', import.meta.url), 'utf8'))
const testsDirectory = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tests', 'AeroLink.Api.Tests')
const inventoryDocumentation = readFileSync(new URL('../../docs/API_TEST_INTENT_INVENTORY.md', import.meta.url), 'utf8')

test('brace-bearing InlineData is parsed as an attribute, not as the method body', () => {
  const source = `
public sealed class ProcedureSavedViewApiTests
{
    [Theory]
    [InlineData("""{"""state""":"""Closed"""}""")]
    public async Task A_json_theory_still_proves_the_http_boundary()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/views", null);
        Assert.Equal(400, (int)response.StatusCode);
    }
}`

  const [row] = classifyFile(source, 'ProcedureSavedViewApiTests')
  assert.equal(row.test, 'A_json_theory_still_proves_the_http_boundary')
  assert.equal(row.cases, 1)
  assert.equal(row.hosted, 'hosted')
  assert.deepEqual(row.hostEvidence, ['factory-construction', 'factory-client'])
  assert.equal(row.intent, 'http-boundary')
})

test('client-backed helper theories stay HTTP-boundary instead of becoming rule-matrix candidates', () => {
  const source = `
public sealed class RegisterTests
{
    [Theory]
    [InlineData("search=Current")]
    [InlineData("search=SRCR-00050")]
    public async Task Search_uses_the_register_http_helper(string query)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var body = await RegisterAsync(client, query);
        Assert.NotNull(body);
    }

    private static Task<object> RegisterAsync(HttpClient client, string query) =>
        client.GetFromJsonAsync<object>($"/api/history/test-change-requests?{query}");
}`
  const [row] = classifyFile(source, 'RegisterTests')
  assert.equal(row.cases, 2)
  assert.equal(row.hosted, 'hosted')
  assert.equal(row.intent, 'http-boundary')
})

test('combined xUnit attributes count InlineData arguments containing closing brackets', () => {
  const source = `
public sealed class CombinedAttributeTests
{
    [Theory, InlineData("value ]"), InlineData(@"verbatim ]")]
    public void Combined_theory_is_one_method_with_two_cases()
    {
        Assert.True(true);
    }
}`

  const [row] = classifyFile(source, 'CombinedAttributeTests')
  assert.equal(row.test, 'Combined_theory_is_one_method_with_two_cases')
  assert.equal(row.kind, 'Theory')
  assert.equal(row.cases, 2)
  assert.equal(row.caseEvidence, 'InlineData')
  assert.equal(row.hosted, 'not-hosted')
})

test('attribute-looking text and properties do not capture non-test methods', () => {
  const source = `
public sealed class AttributeFalsePositiveTests
{
    // [Fact]
    private const string Fake = "[Theory, InlineData(\"]\")]";

    [Theory]
    public string Not_a_test_property => "not a method";

    public void Real_method_without_an_attribute()
    {
        Assert.True(true);
    }

    [Fact]
    public void Real_fact_is_still_found()
    {
        Assert.True(true);
    }
}`

  assert.deepEqual(classifyFile(source, 'AttributeFalsePositiveTests').map((row) => row.test), ['Real_fact_is_still_found'])
})

test('mixed hosted and non-hosted methods do not inherit class-level host evidence', () => {
  const source = `
public sealed class MixedTests
{
    [Fact]
    public void Pure_rule_does_not_start_a_host()
    {
        Assert.True(true);
    }

    [Fact]
    public async Task Endpoint_starts_a_host()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/health/ready");
    }
}`

  const rows = classifyFile(source, 'MixedTests')
  assert.equal(rows.find((row) => row.test === 'Pure_rule_does_not_start_a_host').hosted, 'unknown')
  assert.equal(rows.find((row) => row.test === 'Endpoint_starts_a_host').hosted, 'hosted')
})

test('expression-bodied xUnit methods are included and terminate after nested literals', () => {
  const source = `
public sealed class ExpressionBodyTests
{
    [Fact]
    public void A_semicolon_inside_a_string_is_not_the_method_end()
        => Assert.Contains(";", "a;b");

    [Fact]
    public void A_lambda_body_does_not_end_the_method_early()
        => Assert.True(new[] { 1, 2 }.Any(value => value > 0));

    [Fact]
    public void An_at_dollar_literal_does_not_end_the_method_early()
        => Assert.Contains(";", @$"a"";""b");
}`

  const rows = classifyFile(source, 'ExpressionBodyTests')
  assert.deepEqual(rows.map((row) => row.test), [
    'A_semicolon_inside_a_string_is_not_the_method_end',
    'A_lambda_body_does_not_end_the_method_early',
    'An_at_dollar_literal_does_not_end_the_method_early',
  ])
  assert.deepEqual(rows.map((row) => row.hosted), ['not-hosted', 'not-hosted', 'not-hosted'])
  assert.ok(rows.every((row) => row.sourceLines.end > row.sourceLines.start))
})

test('a class with no host context is explicitly non-hosted', () => {
  const [row] = classifyFile(`
public sealed class DomainOnlyTests
{
    [Fact]
    public void Rule_is_deterministic()
    {
        Assert.True(true);
    }
}`, 'DomainOnlyTests')
  assert.equal(row.hosted, 'not-hosted')
})

test('criterion-7 summary counts cases and stays unresolved when candidate host use is unknown', () => {
  const summary = summariseInventory([
    { cls: 'A', test: 'one', intent: 'in-process-logic', label: 'candidate', level: 'migration candidate', cases: 2, hosted: 'hosted' },
    { cls: 'B', test: 'two', intent: 'in-process-logic', label: 'candidate', level: 'migration candidate', cases: 1, hosted: 'unknown' },
    { cls: 'C', test: 'three', intent: 'http-boundary', label: 'hosted', level: 'hosted', cases: 1, hosted: 'not-hosted' },
  ])
  assert.equal(summary.hostedCases, 2)
  assert.equal(summary.hostedCandidateCases, 2)
  assert.equal(summary.unknownCandidateCases, 1)
  assert.equal(summary.criterion7, 'unresolved')
})

test('custom host service replacement is conservatively fresh-host', () => {
  const result = classifyClass({
    cls: 'ReleasedExecutionEvidenceApiTests',
    source: 'root.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.RemoveAll<AeroLinkDbContext>()));',
    rows: [{ intent: 'http-boundary', hosted: 'hosted' }],
  })
  assert.equal(result.classification, 'fresh-host')
  assert.match(result.reason, /service-replacement/)
})

test('custom factory options and interceptors are conservatively fresh-host', () => {
  for (const source of [
    'new AeroLinkApiFactory(seedDemoAccounts: true);',
    'new AeroLinkApiFactory(commandInterceptor: interceptor);',
  ]) {
    const result = classifyClass({ cls: 'CustomHostTests', source, rows: [{ intent: 'http-boundary', hosted: 'hosted' }] })
    assert.equal(result.classification, 'fresh-host')
  }
})

test('showcase template-copy factories are conservatively fresh-host', () => {
  const result = classifyClass({
    cls: 'DraftDocumentApiTests',
    source: 'using var factory = showcase.CreateFactory(); using var client = factory.CreateClient();',
    rows: [{ intent: 'http-boundary', hosted: 'hosted' }],
  })
  assert.equal(result.classification, 'fresh-host')
  assert.match(result.reason, /showcase-template-copy/)
})

test('reusable-host remains a conversion candidate until client and logical-data isolation are implemented', () => {
  const result = classifyClass({
    cls: 'CandidateApiTests',
    source: 'using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();',
    rows: [{ intent: 'http-boundary', hosted: 'hosted' }],
  })
  assert.equal(result.classification, 'reusable-host')
  assert.match(result.reason, /Static reuse candidate, not an implementation-ready safety finding/)
  assert.match(result.reason, /fresh per-test clients/)
  assert.match(result.reason, /unique per-test tagged logical data/)
  assert.match(result.reason, /assertions scoped to that data/)
  for (const row of hostArtifact.classes.filter((candidate) => candidate.classification === 'reusable-host')) {
    assert.equal(row.reason, result.reason, row.cls)
  }
})

test('every ShowcaseApiFixture consumer is fresh-host in the committed classification', () => {
  for (const cls of [
    'CodeTraceabilityApiTests',
    'ConfigurationPublicationApiTests',
    'DraftDocumentApiTests',
    'ProcedureDiscussionApiTests',
  ]) {
    const row = hostArtifact.classes.find((candidate) => candidate.cls === cls)
    assert.equal(row?.classification, 'fresh-host', cls)
    assert.match(row?.reason ?? '', /showcase-template-copy/, cls)
  }
})

test('reviewed #563 holds keep every unsafe reusable class out of reuse headroom', () => {
  const expectedHeldClasses = [
    'AdministratorChangeRequestApiTests',
    'BaselineImportApiTests',
    'ChangeRequestRenameApiTests',
    'ControlledEditingCheckInApiTests',
    'HistoricalPublicationFreezeApiTests',
    'IdentifierAllocationTests',
    'LegacyProcedureManifestBootstrapApiTests',
    'ManagedDocumentRecoveryApiTests',
    'OpenDigitalThreadTests',
    'ProblemReportActiveMetricApiTests',
    'ProblemReportApiTests',
    'ProblemReportDispositionApiTests',
    'ProblemReportDuplicateDispositionApiTests',
    'ProblemReportVerificationApiTests',
    'ProblemReportWaiverApiTests',
    'ProblemReportCheckoutApiTests',
    'ProductLineApiTests',
    'ReleaseCampaignExactIntentApiTests',
    'SecurityHardeningTests',
    'SavedViewLifecycleApiTests',
    'TestChangeRequestReviewWorkflowTests',
    'TestExecutionEffectivityApiTests',
    'VerificationProgramIsolationApiTests',
  ].sort()
  assert.deepEqual(Object.keys(hostOverrides.classes).sort(), expectedHeldClasses)
  for (const [cls, override] of Object.entries(hostOverrides.classes)) {
    const row = hostArtifact.classes.find((candidate) => candidate.cls === cls)
    assert.equal(row?.classification, override.classification, cls)
    assert.equal(row?.reason, override.reason, cls)
    assert.match(row?.reason ?? '', /^Reviewed #563 hold:/, cls)
  }
  assert.deepEqual(hostArtifact.summary['reusable-host'], { classes: 44, tests: 279, knownCases: 310, unknownCaseTests: 0 })
  assert.deepEqual(hostArtifact.summary['fresh-host'], { classes: 44, tests: 289, knownCases: 321, unknownCaseTests: 0 })
  assert.deepEqual(hostArtifact.summary.converted, { classes: 26, tests: 160, knownCases: 176, unknownCaseTests: 0 })
  assert.deepEqual(hostArtifact.summary['migration-candidate'], { classes: 2, tests: 2, knownCases: 7, unknownCaseTests: 0 })
})

test('host classification preserves unknown theory case counts instead of treating them as known zero', () => {
  const result = classifyClass({
    cls: 'RuntimeTheoryTests',
    source: '',
    rows: [{ intent: 'http-boundary', hosted: 'hosted', cases: null }],
  })
  assert.equal(result.knownCases, 0)
  assert.equal(result.unknownCaseTests, 1)
})

test('host-classification case totals join exactly to the intent inventory', () => {
  const rowsByClass = new Map()
  for (const row of intentArtifact.tests) {
    const rows = rowsByClass.get(row.cls) ?? []
    rows.push(row)
    rowsByClass.set(row.cls, rows)
  }
  for (const hostRow of hostArtifact.classes) {
    const rows = rowsByClass.get(hostRow.cls) ?? []
    assert.equal(hostRow.tests, rows.length, hostRow.cls)
    assert.equal(hostRow.knownCases, rows.reduce((sum, row) => sum + (typeof row.cases === 'number' ? row.cases : 0), 0), hostRow.cls)
    assert.equal(hostRow.unknownCaseTests, rows.filter((row) => typeof row.cases !== 'number').length, hostRow.cls)
  }
  assert.equal(hostArtifact.totals.tests, intentArtifact.totals.tests)
  assert.equal(hostArtifact.totals.knownCases, intentArtifact.totals.cases)
  assert.equal(hostArtifact.totals.unknownCaseTests, intentArtifact.totals.unknownCaseTests)
  const reusable = hostArtifact.summary['reusable-host']
  assert.equal(reusable.tests - reusable.classes, 235)
  assert.equal(reusable.knownCases - reusable.classes, 266)
})

test('host classification CLI distinguishes known cases from unknown-case methods', () => {
  const temporaryDirectory = mkdtempSync(join(tmpdir(), 'aerolink-host-classification-cli-'))
  try {
    const output = execFileSync(process.execPath, [
      fileURLToPath(new URL('../tools/generate-host-classification.mjs', import.meta.url)),
      join(temporaryDirectory, 'artifact.json'),
    ], { encoding: 'utf8' })
    assert.match(output, /classification\s+classes\s+methods\s+known cases\s+unknown-case methods\s+share of methods/)
  assert.match(output, /reusable-host\s+44\s+279\s+310\s+0\s+38.2%/)
  assert.match(output, /fresh-host\s+44\s+289\s+321\s+0\s+39.6%/)
    assert.match(output, /Remaining reuse headroom:\s+44 classes, 279 methods, 310 known cases/)
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true })
  }
})

test('committed inventories expose per-row case and host evidence', () => {
  assert.equal(intentArtifact.schemaVersion, 'aerolink-api-test-intent/v2')
  assert.equal(intentArtifact.totals.tests, 730)
  assert.equal(intentArtifact.totals.cases, 814)
  assert.equal(intentArtifact.totals.criterion7, 'unresolved')
  assert.ok(intentArtifact.tests.every((row) => Object.hasOwn(row, 'cases') && Object.hasOwn(row, 'hosted') && Array.isArray(row.hostEvidence) && row.sourceLines.start <= row.sourceLines.end))
  assert.equal(hostArtifact.schemaVersion, 'aerolink-api-host-classification/v3')
  assert.equal(hostArtifact.totals.knownCases, 814)
  assert.equal(hostArtifact.totals.unknownCaseTests, 0)
  assert.ok(hostArtifact.classes.every((row) => Number.isInteger(row.knownCases) && Number.isInteger(row.unknownCaseTests)))
  for (const cls of ['ReleasedExecutionEvidenceApiTests', 'ReleasedExecutionEvidenceAuthorityMismatchTests', 'ProblemReportPagingApiTests', 'ProductionRoutingTests']) {
    assert.equal(hostArtifact.classes.find((row) => row.cls === cls)?.classification, 'fresh-host', cls)
  }
})

test('committed inventories exactly match the current C# source tree', () => {
  const expectedIntent = buildIntentArtifact(testsDirectory)
  assert.deepEqual(intentArtifact, expectedIntent)
  assert.deepEqual(hostArtifact, buildHostArtifact({ testsDirectory, inventory: expectedIntent, overrides: hostOverrides.classes }))
})

test('committed generated artifacts are byte-stable', () => {
  const expectedIntent = buildIntentArtifact(testsDirectory)
  const expectedHost = buildHostArtifact({ testsDirectory, inventory: expectedIntent, overrides: hostOverrides.classes })
  assert.equal(readFileSync(new URL('../api-test-intent.json', import.meta.url), 'utf8'), `${JSON.stringify(expectedIntent, null, 2)}\n`)
  assert.equal(readFileSync(new URL('../api-host-classification.json', import.meta.url), 'utf8'), `${JSON.stringify(expectedHost, null, 2)}\n`)
})

test('committed Markdown inventory summary is generated and byte-stable', () => {
  const start = inventoryDocumentation.indexOf(INVENTORY_SUMMARY_START)
  const end = inventoryDocumentation.indexOf(INVENTORY_SUMMARY_END)
  assert.notEqual(start, -1)
  assert.equal(inventoryDocumentation.indexOf(INVENTORY_SUMMARY_START, start + INVENTORY_SUMMARY_START.length), -1)
  assert.equal(inventoryDocumentation.indexOf(INVENTORY_SUMMARY_END, end + INVENTORY_SUMMARY_END.length), -1)
  assert.ok(end > start)
  const generated = inventoryDocumentation.slice(start + INVENTORY_SUMMARY_START.length, end).replace(/^\n|\n$/g, '')
  assert.equal(generated, renderInventorySummary(intentArtifact))
})
