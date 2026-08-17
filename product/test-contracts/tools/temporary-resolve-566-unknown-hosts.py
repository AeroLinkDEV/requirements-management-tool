from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[3]


def replace_once(path, pattern, replacement, flags=re.S):
    file = ROOT / path
    text = file.read_text(encoding='utf-8')
    updated, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise SystemExit(f'{path}: expected one replacement, found {count}')
    file.write_text(updated, encoding='utf-8', newline='')


def apply():
    replace_once(
        'product/tests/AeroLink.Api.Tests/ApiTestTelemetryTests.cs',
        r'\n    \[Fact\]\n    public void Reset_for_test_clears_telemetry_state\(\)\n    \{\n        ApiTestTelemetry\.ResetForTest\(\);\n        Assert\.Null\(ApiTestTelemetry\.UnavailableReason\);\n    \}\n(?=\})',
        '\n',
    )
    (ROOT / 'product/tests/AeroLink.Api.Tests/ApiTestTelemetryStateTests.cs').write_text(
        '''namespace AeroLink.Api.Tests;\n\npublic sealed class ApiTestTelemetryStateTests\n{\n    [Fact]\n    public void Reset_for_test_clears_telemetry_state()\n    {\n        ApiTestTelemetry.ResetForTest();\n        Assert.Null(ApiTestTelemetry.UnavailableReason);\n    }\n}\n''',
        encoding='utf-8', newline='')

    authority = ROOT / 'product/tests/AeroLink.Api.Tests/ServerAuthorityContractTests.cs'
    text = authority.read_text(encoding='utf-8')
    if text.count('using System.Reflection;\n') != 1:
        raise SystemExit('ServerAuthorityContractTests: reflection using anchor drifted')
    authority.write_text(text.replace('using System.Reflection;\n', ''), encoding='utf-8', newline='')
    replace_once(
        'product/tests/AeroLink.Api.Tests/ServerAuthorityContractTests.cs',
        r'\n    private static readonly string\[\] AuthenticatedMutationContracts =.*?\n    \[Fact\]\n    public async Task Legacy_identity_fields_are_ignored_and_cannot_spoof_change_author',
        '\n    [Fact]\n    public async Task Legacy_identity_fields_are_ignored_and_cannot_spoof_change_author',
    )
    replace_once(
        'product/tests/AeroLink.Api.Tests/ServerAuthorityContractTests.cs',
        r'\n    \[Fact\]\n    public void Standard_diagnostics_contains_no_human_login_or_committed_password\(\).*?\n    private static string FindProductRoot\(\).*?\n    \}\n(?=\})',
        '\n',
    )
    (ROOT / 'product/tests/AeroLink.Api.Tests/ServerAuthorityIdentityShapeTests.cs').write_text(
        '''using System.Reflection;\n\nnamespace AeroLink.Api.Tests;\n\npublic sealed class ServerAuthorityIdentityShapeTests\n{\n    private static readonly string[] AuthenticatedMutationContracts =\n    [\n        "CreateChangeRequestRequest",\n        "CreateChangeRequestDraftRequest",\n        "RequirementChangeRequest",\n        "SubmitReviewRequest",\n        "ActorRequest",\n        "RequestChangesRequest",\n        "CreateBaselineRequest",\n        "BaselineSelectionRequest",\n        "EmptyMutationRequest",\n        "CreateBuildRequest",\n        "RecordTestExecutionRequest",\n        "DispositionImpactRequest",\n        "BulkDispositionImpactRequest",\n        "SelectBuildRequest",\n        "StartReleaseReviewRequest"\n    ];\n\n    private static readonly string[] CallerSelectableIdentityProperties =\n    [\n        "ActorId", "AuthorId", "RecordedBy", "ExecutedBy", "OwnerId"\n    ];\n\n    [Fact]\n    public void Authenticated_browser_contracts_expose_no_caller_selectable_identity()\n    {\n        var contracts = typeof(Program).Assembly.GetTypes()\n            .Where(type => AuthenticatedMutationContracts.Contains(type.Name))\n            .ToDictionary(type => type.Name);\n\n        Assert.Equal(AuthenticatedMutationContracts.Length, contracts.Count);\n        foreach (var contractName in AuthenticatedMutationContracts)\n        {\n            var properties = contracts[contractName].GetProperties(BindingFlags.Instance | BindingFlags.Public);\n            Assert.DoesNotContain(properties, property =>\n                CallerSelectableIdentityProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase));\n        }\n    }\n}\n''', encoding='utf-8', newline='')
    (ROOT / 'product/tests/AeroLink.Api.Tests/ServerAuthorityDiagnosticsTests.cs').write_text(
        '''namespace AeroLink.Api.Tests;\n\npublic sealed class ServerAuthorityDiagnosticsTests\n{\n    [Fact]\n    public void Standard_diagnostics_contains_no_human_login_or_committed_password()\n    {\n        var productRoot = FindProductRoot();\n        var script = File.ReadAllText(Path.Combine(productRoot, "scripts", "Get-AeroLinkDiagnostics.ps1"));\n\n        Assert.DoesNotContain("/api/auth/login", script, StringComparison.OrdinalIgnoreCase);\n        Assert.DoesNotContain("AeroLink!2026", script, StringComparison.Ordinal);\n        Assert.DoesNotContain("[string]$Password", script, StringComparison.OrdinalIgnoreCase);\n        Assert.Contains("/health/live", script, StringComparison.Ordinal);\n        Assert.Contains("/health/ready", script, StringComparison.Ordinal);\n        Assert.Contains("CreatesBrowserSession = $false", script, StringComparison.Ordinal);\n    }\n\n    private static string FindProductRoot()\n    {\n        var current = new DirectoryInfo(AppContext.BaseDirectory);\n        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx")))\n            current = current.Parent;\n        return current?.FullName ?? throw new InvalidOperationException("Could not locate the product root.");\n    }\n}\n''', encoding='utf-8', newline='')

    replace_once(
        'product/tests/AeroLink.Api.Tests/SecurityBoundaryTests.cs',
        r'\n    \[Fact\]\n    public void File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler\(\).*?\n    \}\n\n    internal static void AssertSqliteConfiguration',
        '\n    internal static void AssertSqliteConfiguration',
    )
    (ROOT / 'product/tests/AeroLink.Api.Tests/SqliteContentionContractTests.cs').write_text(
        '''using System.Diagnostics;\nusing Microsoft.Data.Sqlite;\n\nnamespace AeroLink.Api.Tests;\n\npublic sealed class SqliteContentionContractTests\n{\n    [Fact]\n    public void File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler()\n    {\n        var path = Path.Combine(Path.GetTempPath(), $"aerolink-sqlite-contention-{Guid.NewGuid():N}.db");\n        try\n        {\n            var holderConnectionString = new SqliteConnectionStringBuilder\n            {\n                DataSource = path,\n                Pooling = false,\n                DefaultTimeout = AeroLinkApiFactory.CommandTimeoutSeconds,\n            }.ToString();\n            using var holder = new SqliteConnection(holderConnectionString);\n            holder.Open();\n            using (var journalMode = holder.CreateCommand())\n            {\n                journalMode.CommandText = "PRAGMA journal_mode=WAL;";\n                Assert.Equal("wal", journalMode.ExecuteScalar()?.ToString()?.ToLowerInvariant());\n            }\n            using (var createTable = holder.CreateCommand())\n            {\n                createTable.CommandText = "CREATE TABLE lock_probe (id INTEGER PRIMARY KEY);";\n                createTable.ExecuteNonQuery();\n            }\n\n            using var holderTransaction = holder.BeginTransaction();\n            using (var holderWrite = holder.CreateCommand())\n            {\n                holderWrite.Transaction = holderTransaction;\n                holderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";\n                holderWrite.ExecuteNonQuery();\n            }\n\n            var contenderConnectionString = new SqliteConnectionStringBuilder\n            {\n                DataSource = path,\n                Pooling = false,\n                DefaultTimeout = 1,\n            }.ToString();\n            using var contender = new SqliteConnection(contenderConnectionString);\n            contender.Open();\n            Assert.Equal(1, contender.DefaultTimeout);\n\n            using var busyTimeout = contender.CreateCommand();\n            busyTimeout.CommandText = "PRAGMA busy_timeout;";\n            Assert.Equal(0L, Convert.ToInt64(busyTimeout.ExecuteScalar()));\n\n            using var contenderWrite = contender.CreateCommand();\n            Assert.Equal(1, contenderWrite.CommandTimeout);\n            contenderWrite.CommandText = "INSERT INTO lock_probe DEFAULT VALUES;";\n            var stopwatch = Stopwatch.StartNew();\n            var error = Assert.Throws<SqliteException>(() => contenderWrite.ExecuteNonQuery());\n            stopwatch.Stop();\n            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(750),\n                $"The provider returned SQLITE_BUSY too early after {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");\n            Assert.Equal(5, error.SqliteErrorCode);\n        }\n        finally\n        {\n            SqliteConnection.ClearAllPools();\n            AeroLinkApiFactory.DeleteDatabaseArtifacts(path);\n        }\n    }\n}\n''', encoding='utf-8', newline='')


def post():
    intent_path = ROOT / 'product/test-contracts/api-test-intent.json'
    host_path = ROOT / 'product/test-contracts/api-host-classification.json'
    intent = json.loads(intent_path.read_text(encoding='utf-8'))
    host = json.loads(host_path.read_text(encoding='utf-8'))
    t = intent['totals']
    expected = {
        'tests': 442, 'cases': 492, 'classes': 85,
        'hostedTests': 430, 'hostedCases': 471,
        'nonHostedTests': 12, 'nonHostedCases': 21,
        'unknownHostTests': 0, 'unknownHostCases': 0,
        'hostedCandidateTests': 3, 'hostedCandidateCases': 3,
        'unknownCandidateTests': 0, 'unknownCandidateCases': 0,
        'unknownCaseTests': 0, 'criterion7': 'escape-clause-supported',
    }
    for key, value in expected.items():
        if t.get(key) != value:
            raise SystemExit(f'intent total {key}: expected {value!r}, got {t.get(key)!r}')
    expected_summary = {
        'converted': {'classes': 10, 'tests': 52, 'knownCases': 52, 'unknownCaseTests': 0},
        'fresh-host': {'classes': 41, 'tests': 200, 'knownCases': 225, 'unknownCaseTests': 0},
        'migration-candidate': {'classes': 3, 'tests': 3, 'knownCases': 3, 'unknownCaseTests': 0},
        'reusable-host': {'classes': 31, 'tests': 187, 'knownCases': 212, 'unknownCaseTests': 0},
    }
    if host['summary'] != expected_summary:
        raise SystemExit(f'unexpected host summary: {host["summary"]!r}')
    if host['totals'] != {'classes': 85, 'tests': 442, 'knownCases': 492, 'unknownCaseTests': 0}:
        raise SystemExit(f'unexpected host totals: {host["totals"]!r}')
    rows = {f"{row['cls']}.{row['test']}": row for row in intent['tests']}
    for key in [
        'ApiTestTelemetryStateTests.Reset_for_test_clears_telemetry_state',
        'ServerAuthorityIdentityShapeTests.Authenticated_browser_contracts_expose_no_caller_selectable_identity',
        'ServerAuthorityDiagnosticsTests.Standard_diagnostics_contains_no_human_login_or_committed_password',
        'SqliteContentionContractTests.File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler',
    ]:
        if rows.get(key, {}).get('hosted') != 'not-hosted':
            raise SystemExit(f'{key} did not become explicitly not-hosted: {rows.get(key)!r}')

    test_path = ROOT / 'product/test-contracts/tests/inventory.test.mjs'
    text = test_path.read_text(encoding='utf-8')
    replacements = {
        "assert.deepEqual(hostArtifact.summary['reusable-host'], { classes: 30, tests: 186, knownCases: 211, unknownCaseTests: 0 })": "assert.deepEqual(hostArtifact.summary['reusable-host'], { classes: 31, tests: 187, knownCases: 212, unknownCaseTests: 0 })",
        "assert.deepEqual(hostArtifact.summary['fresh-host'], { classes: 40, tests: 203, knownCases: 228, unknownCaseTests: 0 })": "assert.deepEqual(hostArtifact.summary['fresh-host'], { classes: 41, tests: 200, knownCases: 225, unknownCaseTests: 0 })",
        "assert.deepEqual(hostArtifact.summary['migration-candidate'], { classes: 1, tests: 1, knownCases: 1, unknownCaseTests: 0 })": "assert.deepEqual(hostArtifact.summary['migration-candidate'], { classes: 3, tests: 3, knownCases: 3, unknownCaseTests: 0 })",
        r"reusable-host\s+30\s+186\s+211\s+0\s+42\.1%": r"reusable-host\s+31\s+187\s+212\s+0\s+42\.3%",
        r"fresh-host\s+40\s+203\s+228\s+0\s+45\.9%": r"fresh-host\s+41\s+200\s+225\s+0\s+45\.2%",
        r"Remaining reuse headroom:\s+30 classes, 186 methods, 211 known cases": r"Remaining reuse headroom:\s+31 classes, 187 methods, 212 known cases",
        "assert.equal(intentArtifact.totals.criterion7, 'unresolved')": "assert.equal(intentArtifact.totals.criterion7, 'escape-clause-supported')\n  assert.equal(intentArtifact.totals.unknownHostTests, 0)\n  assert.equal(intentArtifact.totals.unknownCandidateTests, 0)\n  assert.equal(intentArtifact.totals.hostedCandidateCases, 3)",
    }
    for old, new in replacements.items():
        count = text.count(old)
        if count != 1:
            raise SystemExit(f'expected one snapshot anchor {old!r}, found {count}')
        text = text.replace(old, new)
    test_path.write_text(text, encoding='utf-8', newline='')


if __name__ == '__main__':
    if sys.argv[1:] == ['apply']:
        apply()
    elif sys.argv[1:] == ['post']:
        post()
    else:
        raise SystemExit('usage: temporary-resolve-566-unknown-hosts.py apply|post')
