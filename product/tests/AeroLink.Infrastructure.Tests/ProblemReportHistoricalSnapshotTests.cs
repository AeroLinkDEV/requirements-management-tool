using System.Text.Json;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProblemReportHistoricalSnapshotTests
{
    [Theory]
    [InlineData(1, "aerolink.problem-report-closure-review", "type", "Code", "AwaitingSqaClosure")]
    [InlineData(2, "aerolink.problem-report-evidence", "type", "Code", "AwaitingSqaClosure")]
    [InlineData(3, "aerolink.problem-report-evidence", "category", "CodeFunctional", "WaitingForSqaToClose")]
    public void Historical_candidate_schema_recreates_its_original_envelope(int schema, string contract,
        string categoryProperty, string categoryValue, string stateValue)
    {
        var report = new ProblemReport(Guid.NewGuid(), "PR-000321", "Historical report", "A controlled problem.",
            "Analysis", "author", new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero),
            category: ProblemReportCategory.CodeFunctional);
        // The aggregate intentionally creates identifiers. Normalize that generated identity in the fixture
        // bytes so the approval contract is reproducible without changing production aggregate semantics.
        var fixtureId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var now = new DateTimeOffset(2026, 8, 30, 1, 2, 4, TimeSpan.Zero);
        report.ReadyForSccb("author", now);
        report.OpenBySccb("sccb", now.AddSeconds(1));
        report.BeginImplementation("author", now.AddSeconds(2));
        report.ProposeResolution("author", "Apply controlled correction.", now.AddSeconds(3));
        report.RecordResolutionVerification("tester", Guid.NewGuid(), now.AddSeconds(4));
        var json = ProblemReportClosureCandidateService.ReportSnapshotForSchema(report, schema);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(contract, root.GetProperty("contract").GetString());
        Assert.Equal(schema, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(categoryValue, root.GetProperty(categoryProperty).GetString());
        Assert.Equal(stateValue, root.GetProperty("state").GetString());
        if (schema < 3)
            Assert.False(root.TryGetProperty("category", out _));
        else
            Assert.False(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty(schema == 1 ? "ProblemRich" : "problemRich", out _));
        Assert.True(root.TryGetProperty(schema == 1 ? "AdditionalInformationRich" : "additionalInformationRich", out _));
    }

    [Fact]
    public void Historical_candidate_snapshots_are_stable_and_hashable_for_approval_revalidation()
    {
        var report = new ProblemReport(Guid.NewGuid(), "PR-000322", "Historical report", "A controlled problem.",
            "Analysis", "author", DateTimeOffset.UtcNow, category: ProblemReportCategory.TestBlocking);

        foreach (var schema in new[] { 1, 2, 3 })
        {
            var first = ProblemReportClosureCandidateService.ReportSnapshotForSchema(report, schema);
            var second = ProblemReportClosureCandidateService.ReportSnapshotForSchema(report, schema);
            Assert.Equal(first, second);
            Assert.Equal(64, ProblemReportClosureCandidateService.Hash(first).Length);
        }
    }

    [Fact]
    public void Supported_candidate_schemas_have_immutable_byte_fixtures_and_legacy_normalization()
    {
        var report = new ProblemReport(
            Guid.Parse("20000000-0000-0000-0000-000000000002"), "PR-000323", "Fixture report", "A controlled problem.",
            "Analysis", "author", new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero),
            category: ProblemReportCategory.CodeFunctional);
        var now = new DateTimeOffset(2026, 8, 30, 1, 2, 4, TimeSpan.Zero);
        report.ReadyForSccb("author", now);
        report.OpenBySccb("sccb", now.AddSeconds(1));
        report.BeginImplementation("author", now.AddSeconds(2));
        report.ProposeResolution("author", "Apply controlled correction.", now.AddSeconds(3));
        report.RecordResolutionVerification("tester",
            Guid.Parse("30000000-0000-0000-0000-000000000003"), now.AddSeconds(4));
        // The aggregate intentionally creates identifiers. Normalize that generated identity in the fixture
        // bytes so the approval contract is reproducible without changing production aggregate semantics.
        var fixtureId = Guid.Parse("10000000-0000-0000-0000-000000000001");

        var snapshots = new[] { 1, 2, 3, 5 }
            .Select(schema => (schema, json: ProblemReportClosureCandidateService.ReportSnapshotForSchema(report, schema)
                .Replace(report.Id.ToString(), fixtureId.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        // These hashes are byte-level approval fixtures. A serializer-property reorder or an accidental
        // rewrite of a historical envelope must fail this test instead of silently changing revalidation.
        var expectedHashes = new Dictionary<int, string>
        {
            [1] = "9d2095e3c7aa3d59234a846056b6f2c5b17af2c93eba4671323e55928220184a",
            [2] = "c6f5fbe049b20fc9b54065e5ad8a8db196d530047f5df590080636f4d387294c",
            [3] = "ffe5547ba63087a67dd64d523b02a1fe93afe4bc261474bee8a8e4fcf45791fa",
            [5] = "b870b6d789ba8e189610b519aa21d7cf15205f6a21ad2bd162c2cbc160e22d53",
        };
        foreach (var (schema, json) in snapshots)
            Assert.Equal(expectedHashes[schema], ProblemReportClosureCandidateService.Hash(json));

        foreach (var (schema, json) in snapshots)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(schema, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(schema is 1 or 2 ? "AwaitingSqaClosure" : "WaitingForSqaToClose",
                root.GetProperty("state").GetString());
            if (schema is 1 or 2)
                Assert.Equal("Code", root.GetProperty("type").GetString());
            else
                Assert.Equal("CodeFunctional", root.GetProperty("category").GetString());
        }
    }
}
