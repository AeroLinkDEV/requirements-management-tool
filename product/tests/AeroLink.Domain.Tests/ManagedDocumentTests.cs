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
        var revision = Successor(document.Id, 1, "Update the development lifecycle.");

        Assert.Equal("SDP", document.Acronym);
        Assert.Equal("SDP-000001.01", ArtifactNumber.Display(document.DocumentNumber, revision.Revision));
        Assert.Equal(ManagedDocumentState.Draft, revision.State);
    }

    [Fact]
    public void Successor_requires_exact_released_parent_evidence_but_initial_revision_does_not_require_a_build()
    {
        var documentId = Guid.NewGuid();
        var initial = new ManagedDocumentRevision(documentId, 0, "software.author", "Initial Project issue.", Now);
        Assert.Equal(0, initial.Revision);
        Assert.Null(initial.ParentRevisionId);

        var error = Assert.Throws<DomainException>(() => new ManagedDocumentRevision(documentId, 1, "software.author", "Successor.", Now));
        Assert.Contains("exact released parent DOCX", error.Message);
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

        revision.RecordCheckIn(Guid.NewGuid(), Now.AddMinutes(2));
        Assert.Equal(ManagedDocumentState.Draft, revision.State);
        Assert.Equal(1, revision.CurrentReviewCycle);
    }

    [Fact]
    public void Repeated_check_ins_do_not_rewrite_formal_scope_or_revision_responsibility()
    {
        var revision = Successor(Guid.NewGuid(), 1, "Add deterministic configuration reporting.");
        var originalHash = revision.FormalSummaryHash;
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();

        revision.RecordCheckIn(first, Now.AddMinutes(1));
        revision.RecordCheckIn(second, Now.AddMinutes(2));
        revision.RecordCheckIn(third, Now.AddMinutes(3));
        var evidence = new[]
        {
            new ManagedDocumentCheckIn(revision.Id, first, 1, "software.author", "Corrected section 1.", null, null, new string('a', 64), null, null, "one", Now.AddMinutes(1)),
            new ManagedDocumentCheckIn(revision.Id, second, 2, "software.author", "Corrected section 2.", first, new string('a', 64), new string('b', 64), first, null, "two", Now.AddMinutes(2)),
            new ManagedDocumentCheckIn(revision.Id, third, 3, "software.author", "Corrected section 3.", second, new string('b', 64), new string('c', 64), second, null, "three", Now.AddMinutes(3))
        };

        Assert.Equal("Add deterministic configuration reporting.", revision.FormalChangeSummary);
        Assert.Equal(originalHash, revision.FormalSummaryHash);
        Assert.Equal(1, revision.FormalSummaryVersion);
        Assert.Equal("software.author", revision.OwnerId);
        Assert.Equal(["Corrected section 1.", "Corrected section 2.", "Corrected section 3."], evidence.Select(x => x.Comment));
    }

    [Fact]
    public void Formal_scope_correction_is_versioned_and_review_binds_the_exact_summary()
    {
        var revision = NewCheckedInRevision();
        var priorHash = revision.FormalSummaryHash;

        revision.ReviseFormalSummary("Clarify deterministic configuration reporting.", "Correct the approved scope wording.", revision.Version, Now.AddMinutes(1));

        Assert.NotEqual(priorHash, revision.FormalSummaryHash);
        Assert.Equal(2, revision.FormalSummaryVersion);
        revision.SubmitForReview("software.author", "snapshot", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now.AddMinutes(2));
        Assert.Equal(revision.FormalSummaryHash, revision.SubmittedFormalSummaryHash);
        Assert.Equal(revision.FormalSummaryVersion, revision.SubmittedFormalSummaryVersion);
    }

    [Fact]
    public void Formal_scope_correction_rejects_stale_reviewed_approved_or_released_revision()
    {
        var revision = NewCheckedInRevision();
        var stale = Assert.Throws<DomainException>(() => revision.ReviseFormalSummary("New scope.", "Correction.", revision.Version - 1, Now));
        Assert.Contains("changed after this page loaded", stale.Message);

        revision.SubmitForReview("software.author", "snapshot", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now);
        var reviewed = Assert.Throws<DomainException>(() => revision.ReviseFormalSummary("New scope.", "Correction.", revision.Version, Now));
        Assert.Contains("Draft or returned", reviewed.Message, StringComparison.OrdinalIgnoreCase);

        revision.Approve("software.lead", "Technically complete.", Now.AddMinutes(1));
        Assert.Throws<DomainException>(() => revision.ReviseFormalSummary("New scope.", "Correction.", revision.Version, Now.AddMinutes(1)));
        revision.RecordReleaseCandidate(Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), "quality.analyst", Now.AddMinutes(2));
        revision.Approve("quality.analyst", "Release.", Now.AddMinutes(3));
        Assert.Throws<DomainException>(() => revision.ReviseFormalSummary("New scope.", "Correction.", revision.Version, Now.AddMinutes(4)));
    }

    [Fact]
    public void Returned_revision_allows_an_audited_formal_scope_correction()
    {
        var revision = NewCheckedInRevision();
        revision.SubmitForReview("software.author", "snapshot", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now);
        revision.Return("software.lead", "Clarify the exact lifecycle evidence.", Now.AddMinutes(1));

        revision.ReviseFormalSummary("Clarify the exact lifecycle evidence and release scope.", "Resolve returned scope ambiguity.", revision.Version, Now.AddMinutes(2));
        var correctedAttachment = Guid.NewGuid();
        var resolution = new ManagedDocumentCheckIn(revision.Id, correctedAttachment, 2, "software.author", "Updated section 4 after review.", Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid(), Guid.NewGuid(), "return-resolution", Now.AddMinutes(3), "Resolved the exact lifecycle-evidence wording.");
        revision.RecordCheckIn(correctedAttachment, Now.AddMinutes(3));
        revision.SubmitForReview("software.author", "corrected-snapshot", [new("software.lead", "Rina Shah", "Technical"), new("quality.analyst", "Marcus Hale", "Final")], Now.AddMinutes(4));

        Assert.Equal(ManagedDocumentState.InReview, revision.State);
        Assert.Equal(2, revision.CurrentReviewCycle);
        Assert.Equal(2, revision.FormalSummaryVersion);
        Assert.Equal("Resolved the exact lifecycle-evidence wording.", resolution.ReturnResolutionNote);
        Assert.Equal("Updated section 4 after review.", resolution.Comment);
        Assert.Contains(revision.ReviewSteps, step => step.Rationale == "Clarify the exact lifecycle evidence.");
    }

    [Fact]
    public void Check_in_evidence_rejects_blank_or_oversized_comments_before_persistence()
    {
        var revisionId = Guid.NewGuid(); var attachmentId = Guid.NewGuid();
        Assert.Throws<DomainException>(() => new ManagedDocumentCheckIn(revisionId, attachmentId, 1, "software.author", " ", null, null, new string('a', 64), null, null, "operation", Now));
        Assert.Throws<DomainException>(() => new ManagedDocumentCheckIn(revisionId, attachmentId, 1, "software.author", new string('x', 4001), null, null, new string('a', 64), null, null, "operation", Now));
    }

    [Fact]
    public void Stewardship_and_revision_responsibility_are_distinct_versioned_assignments()
    {
        var document = new ManagedDocument(Guid.NewGuid(), "SDP-000001", "SDP", "Software Development Plan", "Plan", "program.manager", Now, "configuration.manager");
        var revision = new ManagedDocumentRevision(document.Id, 0, "software.author", "Initial scope.", Now, initiatedBy: "configuration.manager");
        var priorSteward = document.ReassignSteward("project.lead", document.Version, Now.AddMinutes(1));
        var priorOwner = revision.ReassignResponsibleOwner("software.lead", revision.Version, Now.AddMinutes(1));

        Assert.Equal("program.manager", priorSteward); Assert.Equal("project.lead", document.StewardId); Assert.Equal("configuration.manager", document.CreatedBy);
        Assert.Equal("software.author", priorOwner); Assert.Equal("software.lead", revision.ResponsibleOwnerId); Assert.Equal("configuration.manager", revision.InitiatedBy);
        Assert.Throws<DomainException>(() => revision.ReassignResponsibleOwner("software.author", revision.Version - 1, Now.AddMinutes(2)));
        revision.RecordCheckIn(Guid.NewGuid(), Now.AddMinutes(2));
        revision.SubmitForReview("software.lead", "snapshot", [new("system.reviewer", "Reviewer", "Technical"), new("quality.analyst", "Quality", "Final")], Now.AddMinutes(3));
        Assert.Throws<DomainException>(() => revision.ReassignResponsibleOwner("software.author", revision.Version, Now.AddMinutes(4)));
    }

    [Fact]
    public void Relationship_policy_is_typed_bounded_and_manifest_hash_changes_with_canonical_evidence()
    {
        var revisionId = Guid.NewGuid(); var projectId = Guid.NewGuid(); var releaseId = Guid.NewGuid();
        var first = new ManagedDocumentLink(revisionId, "ChangeRequest", Guid.NewGuid(), "SRCR-00001.00", "Canonical change", "Approved",
            projectId, releaseId, "1.5", "/canonical/change", "MotivatedBy", "software.author", Now);
        var second = new ManagedDocumentLink(revisionId, "Release", releaseId, "BUILD-1.5", "Build 1.5", "Released",
            projectId, releaseId, "1.5", "/canonical/build", "RelatedBuild", "software.author", Now);

        var one = ManagedDocumentRelationshipPolicy.Manifest([first]); var both = ManagedDocumentRelationshipPolicy.Manifest([second, first]);

        Assert.Equal(64, one.Hash.Length); Assert.NotEqual(one.Hash, both.Hash);
        Assert.Equal(both.Json, ManagedDocumentRelationshipPolicy.Manifest([first, second]).Json);
        Assert.Throws<DomainException>(() => new ManagedDocumentLink(revisionId, "ChangeRequest", Guid.NewGuid(), "SRCR-00002.00", "Change", "Draft",
            projectId, releaseId, "1.5", "/canonical/change", "VerificationImpact", "software.author", Now));
        Assert.Throws<DomainException>(() => ManagedDocumentRelationshipPolicy.CanonicalType("Requirement"));
    }

    private static ManagedDocumentRevision NewCheckedInRevision()
    {
        var revision = Successor(Guid.NewGuid(), 1, "Project document update.");
        revision.RecordCheckIn(Guid.NewGuid(), Now);
        return revision;
    }

    private static ManagedDocumentRevision Successor(Guid documentId, int revision, string summary) =>
        new(documentId, revision, "software.author", summary, Now, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "aerolink-managed-document-successor-v1");
}
