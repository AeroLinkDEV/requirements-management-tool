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
        var derivedIndexFields = new[] { nameof(ProblemReport.NumberSequence) };

        Assert.Equal(aggregateFields, evidenceFields.Concat(derivedIndexFields)
            .Order(StringComparer.Ordinal).ToArray());

        using var json = JsonDocument.Parse(ProblemReportEvidenceContract.Serialize(NewReport()));
        Assert.Equal(ProblemReportEvidenceContract.Contract, json.RootElement.GetProperty("contract").GetString());
        Assert.Equal(ProblemReportEvidenceContract.SchemaVersion,
            json.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var field in evidenceFields)
            Assert.True(json.RootElement.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(field), out _),
                $"Problem Report field {field} is absent from immutable evidence.");
        Assert.False(json.RootElement.TryGetProperty("numberSequence", out _),
            "The derived paging index must not change the controlled evidence contract.");
    }

    [Fact]
    public void Category_and_workaround_each_change_the_content_commitment_without_a_version_change()
    {
        var snapshot = ProblemReportEvidenceContract.Create(NewReport());

        var categoryChange = snapshot with { Category = ProblemReportCategory.CodeFunctional.ToString() };
        var workaroundChange = snapshot with { Workaround = "Use redundant input until correction is released." };

        Assert.Equal(snapshot.Version, categoryChange.Version);
        Assert.Equal(snapshot.Version, workaroundChange.Version);
        Assert.NotEqual(ProblemReportEvidenceContract.Hash(snapshot), ProblemReportEvidenceContract.Hash(categoryChange));
        Assert.NotEqual(ProblemReportEvidenceContract.Hash(snapshot), ProblemReportEvidenceContract.Hash(workaroundChange));
        Assert.Contains("\"category\":\"CodeFunctional\"", ProblemReportEvidenceContract.Serialize(categoryChange));
        Assert.Contains("\"workaround\":\"Use redundant input until correction is released.\"",
            ProblemReportEvidenceContract.Serialize(workaroundChange));
    }

    /// <summary>
    /// How the category was arrived at is committed evidence in its own right, not a display hint. A report
    /// the migration classified and a report a person classified are different records even when they name
    /// the same category, and the hash has to be able to tell them apart.
    /// </summary>
    [Fact]
    public void Category_provenance_is_committed_separately_from_the_category()
    {
        var derived = ProblemReportEvidenceContract.Create(NewReport())
            with { Category = ProblemReportCategory.CodeFunctional.ToString(),
                   CategoryProvenance = ProblemReportCategoryProvenance.MigrationDerived.ToString() };
        var selected = derived with { CategoryProvenance = ProblemReportCategoryProvenance.Selected.ToString() };

        Assert.NotEqual(ProblemReportEvidenceContract.Hash(derived), ProblemReportEvidenceContract.Hash(selected));
        Assert.Contains("\"categoryProvenance\":\"MigrationDerived\"", ProblemReportEvidenceContract.Serialize(derived));
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
    public void Pre_image_layout_schema_recomputes_byte_identically_while_new_records_use_current_schema()
    {
        var report = NewReport();
        var imageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        report.UpdateDetails("verification.engineer", report.Title, report.Problem,
            $$"""{"blocks":[{"type":"image","attachmentId":"{{imageId}}","alt":"Bus timing","widthPercent":50}]}""",
            report.AdditionalInformation, report.AdditionalInformationRich, report.Analysis, report.RootCause,
            report.CorrectiveAction, report.SystemAircraftImpact, report.ImpactAssessmentJson, report.Severity,
            report.Priority, Now.AddMinutes(1));
        var historical = ProblemReportEvidenceContract.SerializeForSchema(report, 4);
        var current = ProblemReportEvidenceContract.Serialize(report);

        Assert.Equal(historical, ProblemReportEvidenceContract.SerializeForSchema(report, 4));
        using var oldJson = JsonDocument.Parse(historical);
        using var currentJson = JsonDocument.Parse(current);
        Assert.Equal(4, oldJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain("widthPercent", oldJson.RootElement.GetProperty("problemRich").GetString());
        Assert.Equal(ProblemReportEvidenceContract.SchemaVersion,
            currentJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("\"widthPercent\":50", currentJson.RootElement.GetProperty("problemRich").GetString());
        Assert.NotEqual(historical, current);
    }

    [Fact]
    public void Persisted_v4_fixture_keeps_hash_and_reader_path_after_the_current_contract_advanced()
    {
        // This is a byte-for-byte envelope captured before image width was added. It is intentionally not
        // rebuilt from today's aggregate: a historical reader must prove that its stored bytes still parse
        // and hash under their own schema, rather than trusting the current serializer to recreate them.
        const string persistedV4 = "{\"contract\":\"aerolink.problem-report-evidence\",\"schemaVersion\":4,\"id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"projectId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"reportNumber\":\"PR-000099\",\"revision\":2,\"displayNumber\":\"PR-000099.02\",\"title\":\"Historical image report\",\"problem\":\"Observed reset.\",\"analysis\":\"Historical analysis\",\"analysisRich\":\"\",\"reportedBy\":\"history.engineer\",\"responsibleEngineerId\":\"history.engineer\",\"targetReleaseId\":null,\"problemRich\":\"{\\u0022blocks\\u0022:[{\\u0022type\\u0022:\\u0022paragraph\\u0022,\\u0022text\\u0022:\\u0022Observed reset.\\u0022}]}\",\"additionalInformation\":\"\",\"additionalInformationRich\":\"\",\"systemAircraftImpact\":\"\",\"systemAircraftImpactRich\":\"\",\"category\":\"CodeFunctional\",\"categoryProvenance\":\"Selected\",\"workaround\":\"\",\"workaroundRich\":\"\",\"impactAssessmentJson\":\"{}\",\"classification\":\"Software anomaly\",\"severity\":\"Major\",\"priority\":\"Normal\",\"origin\":\"Test execution\",\"affectedConfiguration\":\"FMS 1.5\",\"rootCause\":\"\",\"rootCauseRich\":\"\",\"effects\":\"\",\"effectsRich\":\"\",\"containment\":\"\",\"containmentRich\":\"\",\"correctiveAction\":\"\",\"correctiveActionRich\":\"\",\"disposition\":null,\"dispositionRationale\":\"\",\"resolutionVerificationExecutionId\":null,\"closureApprovedBy\":null,\"closureApprovedByName\":\"\",\"closureApprovedAt\":null,\"isReleaseBlocker\":false,\"releaseBlockerVersion\":0,\"waiverRationale\":\"\",\"waivedBy\":\"\",\"waivedAt\":null,\"state\":\"Closed\",\"createdAt\":\"2026-01-02T03:04:05+00:00\",\"updatedAt\":\"2026-02-03T04:05:06+00:00\",\"version\":7}";
        Assert.Equal("ab4f09997353f1829c9e56323dbc244c37a1bf169f24aeaef36a00da5f26e823",
            ProblemReportEvidenceContract.Hash(persistedV4));

        var snapshot = JsonSerializer.Deserialize<ProblemReportEvidenceSnapshot>(persistedV4,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot!.SchemaVersion);
        Assert.Equal("PR-000099.02", snapshot.DisplayNumber);
        Assert.Equal("CodeFunctional", snapshot.Category);
        Assert.DoesNotContain("widthPercent", snapshot.ProblemRich);
        Assert.Equal(persistedV4, ProblemReportEvidenceContract.Serialize(snapshot));
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
        Assert.Equal(0, revision.EventSchemaVersion);
        Assert.Empty(revision.Detail);
        Assert.Null(revision.EvidenceJson);
        using var parsed = JsonDocument.Parse(revision.SnapshotJson);
        Assert.Equal("Original evidence", parsed.RootElement.GetProperty("Title").GetString());
    }

    [Fact]
    public void Lifecycle_event_metadata_does_not_rewrite_the_canonical_snapshot_commitment()
    {
        var report = NewReport();
        var snapshot = report.CanonicalSnapshot();
        var hash = report.CanonicalHash();
        const string evidence = "{\"policy\":\"DraftCorrectiveActionImplementationV1\"}";

        var revision = new ProblemReportRevision(report.Id, report.Revision,
            "ImplementationStartedByLinkedChangeRequest", "engineer", hash, snapshot, Now,
            detail: "Automatically entered Implementing from SRCR-00001.", evidenceJson: evidence);

        Assert.Equal(snapshot, revision.SnapshotJson);
        Assert.Equal(hash, revision.SnapshotHash);
        Assert.Equal(ProblemReportEvidenceContract.SchemaVersion, revision.SnapshotSchemaVersion);
        Assert.Equal(1, revision.EventSchemaVersion);
        Assert.Equal(evidence, revision.EvidenceJson);
    }

    private static ProblemReport NewReport() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PR-000001", "Navigation reset", "The unit resets during approach.", "Initial analysis",
        "verification.engineer", Now, responsibleEngineerId: "verification.engineer");
}
