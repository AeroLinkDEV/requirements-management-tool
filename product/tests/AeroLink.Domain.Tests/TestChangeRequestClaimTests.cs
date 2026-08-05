using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// A test change request usually answers for one change request, and may be told to answer for more.
///
/// The default stays one to one. Sometimes two changes are sensibly tested as a single package, and the
/// engineer building it says so rather than raising a second package that duplicates the first's procedures.
/// </summary>
public sealed class TestChangeRequestClaimTests
{
    /// <summary>A package that has been assessed as needing test work, which is what gives it its number.</summary>
    private static TestChangeReview Package(string number = "SYSTCR-000001")
    {
        var package = new TestChangeReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TestChangeReviewDiscipline.System, "SRCR-00031", DateTimeOffset.UtcNow, number);
        package.RecordTestChangeRequired("verification.engineer", DateTimeOffset.UtcNow);
        return package;
    }

    [Fact]
    public void A_package_carries_a_controlled_number_and_a_revision()
    {
        var package = Package();
        Assert.Equal("SYSTCR-000001", package.BaseNumber);
        Assert.Equal(0, package.Revision);
        Assert.Equal("SYSTCR-000001.00", package.DisplayNumber);
    }

    /// <summary>
    /// Rows raised before packages had numbers keep answering by the change request they came from, rather
    /// than being retrospectively given a number they never had.
    /// </summary>
    [Fact]
    public void A_package_raised_before_numbering_still_identifies_itself()
    {
        var package = Package("");
        Assert.Equal("SRCR-00031", package.DisplayNumber);
    }

    [Fact]
    public void By_default_it_answers_only_for_the_change_it_was_raised_from()
    {
        var package = Package();
        Assert.Empty(package.AdditionalSources);
        Assert.Equal([package.ChangeRequestId], package.CoveredChangeRequestIds);
    }

    [Fact]
    public void A_second_change_request_can_be_folded_in()
    {
        var package = Package();
        var second = Guid.NewGuid();
        package.IncludeChangeRequest("test.engineer", second, "SRCR-00032", DateTimeOffset.UtcNow);

        Assert.Equal([package.ChangeRequestId, second], package.CoveredChangeRequestIds);
        var claim = package.AdditionalSources.Single();
        // Who folded it in and when: the decision to test two changes together is a judgement somebody made.
        Assert.Equal("test.engineer", claim.ClaimedBy);
        Assert.Equal("SRCR-00032", claim.ChangeRequestNumber);
    }

    [Fact]
    public void The_change_it_was_raised_from_cannot_be_folded_in_twice()
    {
        var package = Package();
        var error = Assert.Throws<DomainException>(() =>
            package.IncludeChangeRequest("test.engineer", package.ChangeRequestId, "SRCR-00031", DateTimeOffset.UtcNow));
        Assert.Contains("already covers it", error.Message);
    }

    [Fact]
    public void The_same_change_request_cannot_be_folded_in_twice()
    {
        var package = Package();
        var second = Guid.NewGuid();
        package.IncludeChangeRequest("test.engineer", second, "SRCR-00032", DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() =>
            package.IncludeChangeRequest("test.engineer", second, "SRCR-00032", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_folded_in_change_can_be_taken_back_out()
    {
        var package = Package();
        var second = Guid.NewGuid();
        package.IncludeChangeRequest("test.engineer", second, "SRCR-00032", DateTimeOffset.UtcNow);
        package.ExcludeChangeRequest(second, DateTimeOffset.UtcNow);

        // Released for another package to claim: exclusivity would be a trap otherwise.
        Assert.Empty(package.AdditionalSources);
        Assert.Equal([package.ChangeRequestId], package.CoveredChangeRequestIds);
    }

    /// <summary>
    /// Once submitted, the reviewer is judging a fixed set of decisions. Quietly widening what they are
    /// approving is the one thing an approval must not allow.
    /// </summary>
    [Fact]
    public void What_a_package_covers_is_fixed_once_it_is_submitted()
    {
        var package = Package();
        var now = DateTimeOffset.UtcNow;
        package.Submit("test.engineer", "test.approver", everyItemResolved: true, now);

        Assert.Throws<DomainException>(() =>
            package.IncludeChangeRequest("test.engineer", Guid.NewGuid(), "SRCR-00033", now));
        Assert.Throws<DomainException>(() => package.ExcludeChangeRequest(Guid.NewGuid(), now));
    }

    [Fact]
    public void Folding_in_a_change_that_is_not_there_is_refused_rather_than_ignored()
    {
        var package = Package();
        Assert.Throws<DomainException>(() => package.ExcludeChangeRequest(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}
