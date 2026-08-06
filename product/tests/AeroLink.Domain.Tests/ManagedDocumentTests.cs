using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;

namespace AeroLink.Domain.Tests;

public sealed class ManagedDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Draft_is_a_label_and_watermark_state_not_a_different_artifact_acronym()
    {
        var document = new ManagedDocument(Guid.NewGuid(), "SDP-000001", "sdp", "Software Development Plan", "FMS Software Development Plan", "software.author", Now);
        var revision = new ManagedDocumentRevision(document.Id, Guid.NewGuid(), 1, "software.author", "Update the development lifecycle.", Now);

        Assert.Equal("SDP", document.Acronym);
        Assert.Equal("SDP-000001.01", ArtifactNumber.Display(document.DocumentNumber, revision.Revision));
        Assert.Equal(ManagedDocumentState.Draft, revision.State);
    }

    [Fact]
    public void Author_cannot_approve_and_reviewers_must_be_independent()
    {
        var revision = NewCheckedInRevision();
        var duplicate = Assert.Throws<DomainException>(() => revision.SubmitForReview("software.author", "abc",
            [new("software.lead", "Rina Shah", "Technical"), new("software.lead", "Rina Shah", "Final")], Now));
        Assert.Contains("cannot appear twice", duplicate.Message);

        var self = Assert.Throws<DomainException>(() => revision.SubmitForReview("software.author", "abc",
            [new("software.author", "Author", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now));
        Assert.Contains("author cannot approve", self.Message);
    }

    [Fact]
    public void Final_signature_is_refused_until_exact_docx_and_pdf_candidate_exists()
    {
        var revision = NewCheckedInRevision();
        revision.SubmitForReview("software.author", "abc", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now);
        Assert.False(revision.Approve("software.lead", "Technically complete.", Now.AddMinutes(1)));
        var error = Assert.Throws<DomainException>(() => revision.Approve("quality.analyst", "Release.", Now.AddMinutes(2)));
        Assert.Contains("exact DOCX and PDF", error.Message);

        revision.RecordReleaseCandidate(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "quality.analyst", Now.AddMinutes(2));
        Assert.True(revision.Approve("quality.analyst", "Exact candidate authorized.", Now.AddMinutes(3)));
        Assert.Equal(ManagedDocumentState.Released, revision.State);
    }

    [Fact]
    public void Return_preserves_prior_review_evidence_and_reopens_the_same_formal_revision()
    {
        var revision = NewCheckedInRevision();
        revision.SubmitForReview("software.author", "abc", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now);
        revision.Return("software.lead", "Clarify the interface timing.", Now.AddMinutes(1));
        Assert.Equal(ManagedDocumentState.Returned, revision.State);
        Assert.Contains(revision.ReviewSteps, step => step.State == ManagedDocumentReviewStepState.Returned);

        revision.RecordCheckIn(Guid.NewGuid(), "software.author", "Clarified interface timing.", Now.AddMinutes(2));
        Assert.Equal(ManagedDocumentState.Draft, revision.State);
        Assert.Equal(1, revision.CurrentReviewCycle);
    }

    private static ManagedDocumentRevision NewCheckedInRevision()
    {
        var revision = new ManagedDocumentRevision(Guid.NewGuid(), Guid.NewGuid(), 1, "software.author", "Build 1.6 update.", Now);
        revision.RecordCheckIn(Guid.NewGuid(), "software.author", "Initial checked-in draft.", Now);
        return revision;
    }
}
