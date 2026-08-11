using System.Text.Json;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProblemReportEvidenceContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_problem_report_value_is_deliberately_bound_by_the_evidence_contract()
    {
        var aggregateFields = typeof(ProblemReport).GetProperties().Select(property => property.Name)
            .Order(StringComparer.Ordinal).ToArray();
        var evidenceFields = typeof(ProblemReportEvidenceSnapshot).GetProperties()
            .Where(property => property.Name is not nameof(ProblemReportEvidenceSnapshot.Contract)
                and not nameof(ProblemReportEvidenceSnapshot.SchemaVersion))
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(aggregateFields, evidenceFields);

        using var json = JsonDocument.Parse(ProblemReportEvidenceContract.Serialize(NewReport()));
        Assert.Equal(ProblemReportEvidenceContract.Contract, json.RootElement.GetProperty("contract").GetString());
        Assert.Equal(ProblemReportEvidenceContract.SchemaVersion,
            json.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var field in aggregateFields)
            Assert.True(json.RootElement.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(field), out _),
                $"Problem Report field {field} is absent from immutable evidence.");
    }

    [Fact]
    public void Type_and_workaround_each_change_the_content_commitment_without_a_version_change()
    {
        var snapshot = ProblemReportEvidenceContract.Create(NewReport());

        var typeChange = snapshot with { Type = ProblemReportType.Code.ToString() };
        var workaroundChange = snapshot with { Workaround = "Use redundant input until correction is released." };

        Assert.Equal(snapshot.Version, typeChange.Version);
        Assert.Equal(snapshot.Version, workaroundChange.Version);
        Assert.NotEqual(ProblemReportEvidenceContract.Hash(snapshot), ProblemReportEvidenceContract.Hash(typeChange));
        Assert.NotEqual(ProblemReportEvidenceContract.Hash(snapshot), ProblemReportEvidenceContract.Hash(workaroundChange));
        Assert.Contains("\"type\":\"Code\"", ProblemReportEvidenceContract.Serialize(typeChange));
        Assert.Contains("\"workaround\":\"Use redundant input until correction is released.\"",
            ProblemReportEvidenceContract.Serialize(workaroundChange));
    }

    [Fact]
    public void Canonical_json_is_repeatable_and_delimiters_cannot_alias_different_fields()
    {
        var snapshot = ProblemReportEvidenceContract.Create(NewReport());
        var first = snapshot with { Title = "reset|during", Problem = "approach\\segment\nA" };
        var second = snapshot with { Title = "reset", Problem = "during|approach\\segment\nA" };

        var serialized = ProblemReportEvidenceContract.Serialize(first);
        Assert.Equal(serialized, ProblemReportEvidenceContract.Serialize(first));
        Assert.Equal(ProblemReportEvidenceContract.Hash(first), ProblemReportEvidenceContract.Hash(first));
        Assert.NotEqual(ProblemReportEvidenceContract.Hash(first), ProblemReportEvidenceContract.Hash(second));
        using var parsed = JsonDocument.Parse(serialized);
        Assert.Equal("reset|during", parsed.RootElement.GetProperty("title").GetString());
        Assert.Equal("approach\\segment\nA", parsed.RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public void Legacy_revision_evidence_keeps_its_original_json_hash_and_schema()
    {
        const string legacyJson = "{\"Id\":\"legacy\",\"Title\":\"Original evidence\"}";
        var legacyHash = new string('a', 64);
        var revision = new ProblemReportRevision(Guid.NewGuid(), 0, "LegacyImported", "system",
            legacyHash, legacyJson, Now, snapshotSchemaVersion: 0);

        Assert.Equal(0, revision.SnapshotSchemaVersion);
        Assert.Equal(legacyJson, revision.SnapshotJson);
        Assert.Equal(legacyHash, revision.SnapshotHash);
        using var parsed = JsonDocument.Parse(revision.SnapshotJson);
        Assert.Equal("Original evidence", parsed.RootElement.GetProperty("Title").GetString());
    }

    private static ProblemReport NewReport() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PR-000001", "Navigation reset", "The unit resets during approach.", "Initial analysis",
        "verification.engineer", Now, responsibleEngineerId: "verification.engineer");
}
