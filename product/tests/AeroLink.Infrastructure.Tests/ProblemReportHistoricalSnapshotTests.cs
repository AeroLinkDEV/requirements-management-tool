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
}
