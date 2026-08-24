using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class TestChangeReviewProcedurePackageTests
{
    [Fact]
    public void SoftwareProcedureVocabularyHasIndependentPrefixAndReviewSubject()
    {
        var high = VerificationArtifactVocabulary.Definition(new VerificationArtifactKey(
            VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure));
        var low = VerificationArtifactVocabulary.Definition(new VerificationArtifactKey(
            VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure));

        Assert.Equal("HLRTPCR", high.TestChangeRequestPrefix);
        Assert.Equal("LLRTPCR", low.TestChangeRequestPrefix);
        Assert.Equal(ReviewSubject.HighLevelSoftwareProcedure, high.ReviewSubject);
        Assert.Equal(ReviewSubject.LowLevelSoftwareProcedure, low.ReviewSubject);
        Assert.NotEqual(high.TestChangeRequestPrefix,
            VerificationArtifactVocabulary.Definition(new VerificationArtifactKey(
                VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case)).TestChangeRequestPrefix);
    }

    [Fact]
    public void ProcedurePackageRecordsExactCaseChangeOriginAndPreservesItOnRevision()
    {
        var key = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
            VerificationArtifactKind.Procedure);
        var source = Guid.NewGuid();
        var package = TestChangeReview.FromCaseChange(Guid.NewGuid(), Guid.NewGuid(), source, key,
            "HLRTCCR-000001.00", DateTimeOffset.UtcNow, baseNumber: "HLRTPCR-000001");
        package.RecordTestChangeRequired("engineer", DateTimeOffset.UtcNow);
        package.WriteCase("engineer", "Procedure correction", "Problem", "Analysis", "Solution", DateTimeOffset.UtcNow);
        package.AddProcedureChange("engineer", new TestProcedureChangeDraft("HLRTP-000001", 1,
            TestProcedureLevel.HighLevel, TestProcedureChangeKind.Retire, "", "", "", "", "", "Retire obsolete procedure"),
            DateTimeOffset.UtcNow);
        package.SubmitForReview("engineer", [new("approver", "Approver")], true, DateTimeOffset.UtcNow);
        package.Approve("approver", "Approved for controlled correction.", DateTimeOffset.UtcNow);
        Assert.Equal(key, package.ArtifactKey);
        Assert.Equal(TestChangeReviewOriginKind.CaseChange, package.OriginKind);
        Assert.Equal(source, package.OriginReferenceId);

        var next = package.StartNextRevision("engineer", DateTimeOffset.UtcNow, targetReleaseIsReleased: false);
        Assert.Equal(key, next.ArtifactKey);
        Assert.Equal(TestChangeReviewOriginKind.CaseChange, next.OriginKind);
        Assert.Equal(source, next.OriginReferenceId);
    }

    [Fact]
    public void CaseOriginCannotCreateSystemOrCasePackage()
    {
        Assert.Throws<DomainException>(() => TestChangeReview.FromCaseChange(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure),
            "SYSTPCR-000001.00", DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => TestChangeReview.FromCaseAssessment(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            "HLRTCCR-000001.00", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LegacyOriginDiscriminatorMustMatchItsTypedForeignKey()
    {
        var changeId = Guid.NewGuid();
        var changePackage = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), changeId,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-000001.00", DateTimeOffset.UtcNow);
        typeof(TestChangeReview).GetProperty(nameof(TestChangeReview.OriginReferenceId))!
            .SetValue(changePackage, Guid.NewGuid());
        Assert.Throws<DomainException>(changePackage.ValidateOriginForPersistence);

        var reportId = Guid.NewGuid();
        var reportPackage = TestChangeReview.FromProblemReport(Guid.NewGuid(), Guid.NewGuid(), reportId,
            TestChangeReviewDiscipline.System, "PR-000001.00", DateTimeOffset.UtcNow);
        typeof(TestChangeReview).GetProperty(nameof(TestChangeReview.OriginReferenceId))!
            .SetValue(reportPackage, Guid.NewGuid());
        Assert.Throws<DomainException>(reportPackage.ValidateOriginForPersistence);
    }
}
