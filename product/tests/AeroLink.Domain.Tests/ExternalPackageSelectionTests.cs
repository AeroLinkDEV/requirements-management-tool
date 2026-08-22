using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Imports;

namespace AeroLink.Domain.Tests;

public sealed class ExternalPackageSelectionTests
{
    [Fact]
    public void One_draft_can_bind_multiple_packages_but_a_package_cannot_bind_two_candidates()
    {
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = ReconciledImport(projectId, now, "first");
        var second = ReconciledImport(projectId, now, "second");
        var firstItem = new BaselineImportPackageItem(projectId, first.Id, Guid.NewGuid(), "CUSR-000101", 0,
            "First customer requirement.", "", "REQ-101", now);
        var secondItem = new BaselineImportPackageItem(projectId, second.Id, Guid.NewGuid(), "CUSR-000102", 0,
            "Second customer requirement.", "", "REQ-102", now);
        var candidate = new CandidateBaseline("SW-10.00", 0, projectId, releaseId, null, "Candidate", "cm", now);

        candidate.SelectExternalPackage(first, new[] { firstItem }, "cm", now);
        candidate.SelectExternalPackage(second, new[] { secondItem }, "cm", now);

        Assert.Equal(2, candidate.ExternalPackageSelections.Count);
        var other = new CandidateBaseline("SW-10.01", 0, projectId, releaseId, null, "Other", "cm", now);
        var error = Assert.Throws<DomainException>(() => other.SelectExternalPackage(first, new[] { firstItem }, "cm", now));
        Assert.Contains("exactly one candidate baseline", error.Message);
    }

    [Fact]
    public void Reselecting_the_same_package_content_does_not_change_the_freeze_hash()
    {
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var import = ReconciledImport(projectId, now, "same");
        var item = new BaselineImportPackageItem(projectId, import.Id, Guid.NewGuid(), "CUSR-000103", 0,
            "Stable customer requirement.", "", "REQ-103", now);
        var candidate = new CandidateBaseline("SW-10.02", 0, projectId, releaseId, null, "Candidate", "cm", now);

        candidate.SelectExternalPackage(import, new[] { item }, "cm", now);
        candidate.Freeze("cm", now);
        var firstHash = candidate.ContentHash;
        candidate.Reopen("cm", "Reselect the same immutable package.", now.AddMinutes(1));
        candidate.RemoveExternalPackage(import.Id, "cm", now.AddMinutes(1));
        candidate.SelectExternalPackage(import, new[] { item }, "cm", now.AddMinutes(1));
        candidate.Freeze("cm", now.AddMinutes(1));

        Assert.Equal(firstHash, candidate.ContentHash);
    }

    private static BaselineImport ReconciledImport(Guid projectId, DateTimeOffset now, string name)
    {
        var import = new BaselineImport(projectId, "DOORS", "1", name, now, $"{name}.reqif",
            "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38", 1,
            ImportedArtifactKinds.Requirements, "source", now, "cm", now);
        import.RecordAnalysis(now); import.RecordMapping("{}", now);
        import.NoteSourceRecordsAccountedFor(1, now); import.RecordReconciliation("{}", now);
        return import;
    }
}
