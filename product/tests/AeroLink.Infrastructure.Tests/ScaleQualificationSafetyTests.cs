using AeroLink.Scale;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

public sealed class ScaleQualificationSafetyTests
{
    [Fact]
    public void Missing_connection_and_protected_targets_fail_closed_without_connection_contents()
    {
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireSafeConnection(null));
        var error = Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireSafeConnection(
            "Host=127.0.0.1;Port=54329;Database=aerolink_scale;Username=postgres;Password=secret"));
        Assert.Contains("54329", error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Host=10.1.2.3;Port=55495;Database=aerolink_995_qualify")]
    [InlineData("Host=127.0.0.1;Port=55495;Database=aerolink")]
    [InlineData("Host=127.0.0.1;Port=55495;Database=postgres")]
    public void Non_disposable_targets_are_refused(string connection) =>
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireSafeConnection(connection));

    [Fact]
    public void Dedicated_loopback_target_is_returned_unchanged()
    {
        const string connection = "Host=127.0.0.1;Port=55495;Database=aerolink_995_qualify;Username=postgres";
        Assert.Equal(connection, ScaleQualificationSafety.RequireSafeConnection(connection));
    }

    [Fact]
    public void Write_load_requires_two_positive_opt_ins()
    {
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireOptIns(false, true, "session-load"));
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireOptIns(true, false, "session-load"));
        ScaleQualificationSafety.RequireOptIns(true, true, "session-load");
    }

    [Fact]
    public void Evidence_root_rejects_persistent_product_store_and_manifest_overwrite()
    {
        var productStore = ScaleQualificationSafety.PersistentProductStorePath();
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireEvidenceRoot(productStore));
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireEvidenceRoot(Directory.GetParent(productStore)!.FullName));
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.RequireEvidenceRoot(Path.Combine(productStore, "nested")));

        var root = Path.Combine(Path.GetTempPath(), "aerolink-cq14", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preflight.json");
        var identity = Identity();
        var manifest = ScaleQualificationSafety.CreateManifest("preflight-only", "preflight", new string('a', 40), identity,
            "disposable-postgresql", "Host=127.0.0.1;Port=55495;Database=aerolink_995_qualify", root, true, true);
        try
        {
            ScaleQualificationSafety.WriteImmutableManifest(path, manifest);
            Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.WriteImmutableManifest(path, manifest));
            Assert.Contains("preflight-only", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Exact_dataset_validation_refuses_scope_or_content_hash_mismatch()
    {
        var identity = Identity();
        var expected = new ScaleQualificationScope(identity.ProgramId, identity.ProjectId, identity.ReleaseId,
            identity.BaselineId, identity.DatasetSeed, ScaleQualificationSafety.ComputeDatasetHash(identity));
        ScaleQualificationSafety.ValidateExactDataset(expected, identity);
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.ValidateExactDataset(
            expected with { ProjectId = Guid.NewGuid() }, identity));
        Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.ValidateExactDataset(
            expected with { DatasetHash = new string('b', 64) }, identity));
    }

    private static ScaleDatasetIdentity Identity() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), "Qualification Program", "QUAL",
        Guid.Parse("20000000-0000-0000-0000-000000000002"), "Qualification Project", "Qualification Product",
        Guid.Parse("30000000-0000-0000-0000-000000000003"), "10.0",
        Guid.Parse("40000000-0000-0000-0000-000000000004"), "SYSBL-00000001", 10_000, "4754",
        new string('c', 64));
}
