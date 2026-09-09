using AeroLink.Scale;
using Npgsql;
using System.Text.Json;

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
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Repository_identity_and_canonical_store_ignore_foreign_caller_cwd()
    {
        var original = Directory.GetCurrentDirectory();
        var foreign = Path.Combine(Path.GetTempPath(), "aerolink-cq14-foreign", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign);
        try
        {
            Directory.SetCurrentDirectory(foreign);
            var repository = ScaleQualificationSafety.RepositoryRoot();
            Assert.True(Directory.Exists(Path.Combine(repository, "product")));
            Assert.Equal(Path.GetFullPath(Path.Combine(repository, "product", ".local")),
                ScaleQualificationSafety.PersistentProductStorePath());
            Assert.Throws<InvalidOperationException>(() =>
                ScaleQualificationSafety.RequireEvidenceRoot(ScaleQualificationSafety.CanonicalPersistentProductStorePath()));
            Assert.Matches("^[0-9a-f]{40}$", ScaleQualificationSafety.RequireSourceCommit());
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(foreign, true);
        }
    }

    [Fact]
    public void Manifest_path_rejects_physical_junction_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink-cq14-junction", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "aerolink-cq14-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(root, "child");
        try
        {
            try { Directory.CreateSymbolicLink(junction, outside); }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }
            catch (IOException) { return; }
            Assert.Throws<InvalidOperationException>(() =>
                ScaleQualificationSafety.RequireManifestPath(Path.Combine(junction, "escape.json"), root));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Prepared_manifest_requires_dataset_preparation_status_and_preserves_no_query_secret()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink-cq14-prepared", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identity = Identity();
        var manifest = ScaleQualificationSafety.CreateManifest("dataset-prepared", "workspace", new string('a', 40), identity,
            "disposable-postgresql", "Host=127.0.0.1;Port=55495;Database=aerolink_995_qualify", root, true, true);
        var path = Path.Combine(root, "workspace.json");
        try
        {
            ScaleQualificationSafety.WriteImmutableManifest(path, manifest);
            Assert.Equal("dataset-prepared", ScaleQualificationSafety.ReadPreparedManifest(path).Status);

            var wrong = Path.Combine(root, "wrong.json");
            File.WriteAllText(wrong, JsonSerializer.Serialize(manifest with { Status = "preflight-only" }));
            Assert.Throws<InvalidOperationException>(() => ScaleQualificationSafety.ReadPreparedManifest(wrong));

            var error = Assert.Throws<InvalidOperationException>(() =>
                ScaleQualificationSafety.RejectApiOption(true, "https://127.0.0.1:5175/?token=supersecret"));
            Assert.DoesNotContain("supersecret", error.Message, StringComparison.OrdinalIgnoreCase);
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
