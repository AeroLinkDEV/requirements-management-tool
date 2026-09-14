using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;

namespace AeroLink.Domain.Tests;

public sealed class ProjectSetupSourcePackageTests
{
    private static readonly Guid DraftId = Guid.NewGuid();
    private static readonly Guid BaselineId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 1, 30, 0, TimeSpan.Zero);
    private const string Hash = "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38";

    [Fact]
    public void Native_snapshot_has_no_staged_file_and_retains_exact_source_observation()
    {
        var package = new ProjectSetupSourcePackage(DraftId, ProjectSetupSourceKind.AeroLinkBaseline,
            "baseline.aerolink", "AeroLinkBaseline", Hash, 0, [], "administrator", Now, BaselineId);

        package.RecordNativeSnapshot(Guid.NewGuid(), "Frozen", """{"baselineId":"source"}""",
            """{"format":"AeroLinkBaseline","objects":[]}""", Now.AddMinutes(1));

        Assert.Equal(ProjectSetupSourceStage.Analysed, package.Stage);
        Assert.Equal(0, package.SizeBytes);
        Assert.Empty(package.Payload);
        Assert.Equal("Frozen", package.SourceState);
        Assert.Equal("""{"baselineId":"source"}""", package.MetadataJson);
    }

    [Fact]
    public void External_source_requires_the_exact_uploaded_payload()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Throws<DomainException>(() => new ProjectSetupSourcePackage(DraftId,
            ProjectSetupSourceKind.ExternalBaseline, "source.csv", "CSV", Hash, bytes.Length + 1,
            bytes, "administrator", Now));

        var package = new ProjectSetupSourcePackage(DraftId, ProjectSetupSourceKind.ExternalBaseline,
            "source.csv", "CSV", Hash, bytes.Length, bytes, "administrator", Now);
        package.RecordAnalysis("Importer", "{}", """{"objects":[]}""", Now.AddMinutes(1));
        package.RecordConfiguration("""["Requirements"]""", """{"sourceSha256":"x"}""", Now.AddMinutes(2));

        Assert.Equal(bytes, package.Payload);
        Assert.Equal(ProjectSetupSourceStage.Analysed, package.Stage);
        Assert.Equal("{}", package.MetadataJson);
    }

    [Fact]
    public void Browser_configuration_cannot_replace_parser_metadata()
    {
        var package = new ProjectSetupSourcePackage(DraftId, ProjectSetupSourceKind.ExternalBaseline,
            "source.csv", "CSV", Hash, 1, [7], "administrator", Now);
        package.RecordAnalysis("Importer", """{"observed":"source"}""", """{"objects":[]}""", Now);
        package.RecordConfiguration("""["Requirements"]""", "{}", Now.AddMinutes(1));

        Assert.Equal("""{"observed":"source"}""", package.MetadataJson);
    }
}
