using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Idempotently adds a small, believable controlled-document library to the FMS showcase.</summary>
public sealed class ManagedDocumentShowcaseSeeder(AeroLinkDbContext db, ManagedDocumentFileService files)
{
    private sealed record DocumentSeed(string Acronym, string Type, string Title, string Purpose, string Owner, string WorkingState);

    private static readonly DocumentSeed[] Documents =
    [
        new("PSAC", "Plan for Software Aspects of Certification", "FMS Plan for Software Aspects of Certification", "Defines the certification approach, software level, lifecycle evidence and authority interfaces for the FMS product.", "project.lead", "Released"),
        new("SDP", "Software Development Plan", "FMS Software Development Plan", "Defines the software lifecycle, development standards, transition criteria, environments and engineering responsibilities.", "software.author", "Draft"),
        new("SVP", "Software Verification Plan", "FMS Software Verification Plan", "Defines verification independence, methods, coverage objectives, environments, records and completion criteria.", "test.author", "InReview"),
        new("SCMP", "Software Configuration Management Plan", "FMS Software Configuration Management Plan", "Defines configuration identification, change control, status accounting, build control and archive practices.", "cm.fms", "Released"),
        new("SQAP", "Software Quality Assurance Plan", "FMS Software Quality Assurance Plan", "Defines lifecycle assurance surveillance, conformity reviews, nonconformance control and release participation.", "quality.analyst", "Released"),
        new("SAS", "Software Accomplishment Summary", "FMS Software Accomplishment Summary", "Summarizes compliance, delivered configuration, verification results and remaining lifecycle considerations for release.", "project.lead", "Draft"),
        new("ICD", "Interface Control Document", "FMS Navigation Interface Control Document", "Defines the controlled data, timing, integrity and failure-handling interfaces between the FMS and aircraft systems.", "systems.author", "Returned")
    ];

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == FmsShowcaseSeeder.ProgramCode, ct);
        if (program is null) return;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.ProgramId == program.Id, ct);
        if (await db.ManagedDocuments.AnyAsync(x => x.ProjectId == project.Id, ct)) return;
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == project.Id).ToListAsync(ct);
        var release15 = releases.Single(x => x.Version == "1.5");
        var release16 = releases.Single(x => x.Version == "1.6");
        var people = await db.UserAccounts.AsNoTracking().Where(x =>
            x.UserName == "software.lead" || x.UserName == "quality.analyst" || x.UserName == "assurance.reviewer").ToDictionaryAsync(x => x.UserName, ct);
        if (people.Count != 3) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = new DateTimeOffset(2026, 7, 31, 15, 0, 0, TimeSpan.Zero);
        foreach (var (seed, index) in Documents.Select((value, index) => (value, index)))
        {
            var document = new ManagedDocument(project.Id, $"{seed.Acronym}-000001", seed.Acronym, seed.Type, seed.Title, seed.Owner, now.AddMinutes(index));
            db.ManagedDocuments.Add(document);
            var released = new ManagedDocumentRevision(document.Id, 0, seed.Owner, "Initial controlled Project issue.", now.AddMinutes(index));
            db.ManagedDocumentRevisions.Add(released);
            await db.SaveChangesAsync(ct);

            await SeedReleasedRevisionAsync(document, released, project.Name, release15.Version, program.Id, program.Name, people, now.AddMinutes(index), ct);
            if (seed.WorkingState != "Released")
            {
                var parent = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == released.ReleasedDocxAttachmentId, ct);
                var working = new ManagedDocumentRevision(document.Id, 1, seed.Owner, WorkingSummary(seed.Acronym), now.AddDays(1).AddMinutes(index),
                    released.Id, parent.Id, parent.Sha256, ManagedDocumentFileService.SuccessorTransformationProfile);
                db.ManagedDocumentRevisions.Add(working);
                await db.SaveChangesAsync(ct);
                await SeedWorkingRevisionAsync(document, working, project.Name, release16.Version, program.Name, seed.WorkingState, people, now.AddDays(1).AddMinutes(index), ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task SeedReleasedRevisionAsync(ManagedDocument document, ManagedDocumentRevision revision, string project,
        string build, Guid programId, string program, IReadOnlyDictionary<string, UserAccount> people, DateTimeOffset now, CancellationToken ct)
    {
        var finalApproverId = revision.ResponsibleOwnerId == "quality.analyst" ? "assurance.reviewer" : "quality.analyst";
        var finalApprover = people[finalApproverId];
        var draft = Render(document, revision, project, build, program, "Draft", "DRAFT", []);
        var draftAttachment = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, revision.Id, 1,
            "Working Word document", "Initial checked-in working copy.", draft.FileName, draft.ContentType, draft.Content, null, revision.ResponsibleOwnerId, now, ct);
        db.ControlledAttachments.Add(draftAttachment); revision.RecordCheckIn(draftAttachment.Id, now);
        db.ManagedDocumentCheckIns.Add(new(revision.Id, draftAttachment.Id, 1, revision.ResponsibleOwnerId,
            "Initial checked-in working copy.", null, null, draftAttachment.Sha256, null, null,
            $"showcase-initial:{revision.Id:N}", now));
        var cycle = revision.SubmitForReview(revision.ResponsibleOwnerId, draftAttachment.Sha256,
        [
            new("software.lead", people["software.lead"].DisplayName, "Technical review"),
            new(finalApproverId, finalApprover.DisplayName, "SQA / assurance release authorization", Kind: ReviewStageKind.Approval)
        ], now.AddHours(1));
        db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle));
        db.ManagedDocumentReviewContributors.Add(new(revision.Id, cycle, revision.ResponsibleOwnerId, draftAttachment.Sha256, now.AddHours(1)));
        revision.Approve("software.lead", "Technical content is complete and consistent with the approved lifecycle.", now.AddHours(2));

        var approvals = new[]
        {
            new PublicationApproval("Technical reviewer", people["software.lead"].DisplayName, "software.lead", "Approved", now.AddHours(2)),
            new PublicationApproval("Software quality assurance", finalApprover.DisplayName, finalApproverId, "Approved", now.AddHours(3))
        };
        var docx = Render(document, revision, project, build, program, "Released", null, approvals);
        var pdf = ProfessionalPublicationRenderer.Render(Publication(document, revision, project, build, program, "Released", null, approvals), "pdf", FileStem(document, revision));
        var releaseDocx = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1,
            "Released DOCX", "Approved editable source retained as an immutable record.", docx.FileName, docx.ContentType, docx.Content, null, finalApproverId, now.AddHours(3), ct);
        var releasePdf = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, Guid.NewGuid(), 1,
            "Approved PDF", "Approved read-only rendition.", pdf.FileName, pdf.ContentType, pdf.Content, null, finalApproverId, now.AddHours(3), ct);
        db.ControlledAttachments.AddRange(releaseDocx, releasePdf);
        var manifest = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{releaseDocx.Sha256}:{releasePdf.Sha256}:{revision.FormalSummaryHash}:{revision.FormalSummaryVersion}"));
        revision.RecordReleaseCandidate(releaseDocx.Id, releasePdf.Id, manifest, finalApproverId, now.AddHours(3));
        revision.Approve(finalApproverId, "SQA or assurance confirms the exact DOCX/PDF candidate and authorizes controlled release.", now.AddHours(3));
        db.ElectronicSignatures.AddRange(
            Signature(people["software.lead"], programId, document, revision, "TechnicalApprove", "Technical review completed", draftAttachment.Sha256, now.AddHours(2)),
            Signature(finalApprover, programId, document, revision, "Release", "SQA or assurance authorizes this exact controlled release", manifest, now.AddHours(3)));
        Audit(document.Id, "DocumentReleased", finalApproverId, $"Released Project document {document.DocumentNumber}.{revision.Revision:D2} with immutable DOCX and PDF renditions.", now.AddHours(3));
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedWorkingRevisionAsync(ManagedDocument document, ManagedDocumentRevision revision, string project,
        string build, string program, string state, IReadOnlyDictionary<string, UserAccount> people, DateTimeOffset now, CancellationToken ct)
    {
        var draft = Render(document, revision, project, build, program, "Draft", "DRAFT", []);
        var attachment = await files.StoreAsync(document.ProjectId, document.Id, revision.Id, revision.Id, 1,
            "Working Word document", "Most recent checked-in draft.", draft.FileName, draft.ContentType, draft.Content, null, revision.ResponsibleOwnerId, now, ct);
        db.ControlledAttachments.Add(attachment); revision.RecordCheckIn(attachment.Id, now);
        db.ManagedDocumentCheckIns.Add(new(revision.Id, attachment.Id, 1, revision.ResponsibleOwnerId,
            "Most recent checked-in draft.", revision.ParentReleasedDocxAttachmentId, revision.ParentReleasedDocxSha256,
            attachment.Sha256, null, null, $"showcase-successor:{revision.Id:N}", now));
        if (state is "InReview" or "Returned")
        {
            var cycle = revision.SubmitForReview(revision.ResponsibleOwnerId, attachment.Sha256,
            [
                new("software.lead", people["software.lead"].DisplayName, "Technical review"),
                new("quality.analyst", people["quality.analyst"].DisplayName, "SQA release authorization", Kind: ReviewStageKind.Approval)
            ], now.AddHours(1));
            db.ManagedDocumentReviewSteps.AddRange(revision.ReviewSteps.Where(x => x.Cycle == cycle));
            db.ManagedDocumentReviewContributors.Add(new(revision.Id, cycle, revision.ResponsibleOwnerId, attachment.Sha256, now.AddHours(1)));
            if (state == "Returned") revision.Return("software.lead", "Clarify the external data validity timing and identify the governing interface requirement.", now.AddHours(2));
        }
        Audit(document.Id, state == "Returned" ? "DocumentReturned" : state == "InReview" ? "DocumentSubmitted" : "DocumentCheckedIn",
            state == "Returned" ? "software.lead" : revision.ResponsibleOwnerId, $"Project document {document.DocumentNumber}.{revision.Revision:D2} is {state}.", now.AddHours(2));
        await db.SaveChangesAsync(ct);
    }

    private GeneratedOutput Render(ManagedDocument document, ManagedDocumentRevision revision, string project, string build,
        string program, string status, string? watermark, IReadOnlyList<PublicationApproval> approvals) =>
        ProfessionalPublicationRenderer.Render(Publication(document, revision, project, build, program, status, watermark, approvals), "docx", FileStem(document, revision));

    private static ProfessionalPublication Publication(ManagedDocument document, ManagedDocumentRevision revision, string project,
        string build, string program, string status, string? watermark, IReadOnlyList<PublicationApproval> approvals)
    {
        var fingerprint = ManagedDocumentFileService.Sha256(Encoding.UTF8.GetBytes($"{document.DocumentNumber}|{revision.Revision}|{revision.FormalChangeSummary}|{status}"));
        return new ProfessionalPublication("AeroLink FMS", program, project, document.DocumentType, document.Title,
            "Controlled Project lifecycle document", document.DocumentNumber, revision.Revision.ToString("D2"), status, "Project-wide",
            "All software builds", revision.ResponsibleOwnerId, revision.UpdatedAt, fingerprint,
            [("Document steward", document.StewardId), ("Revision responsible owner", revision.ResponsibleOwnerId), ("Revision initiated by", revision.InitiatedBy), ("Applicability", "Project-wide; build links are contextual only"), ("Formal revision scope", revision.FormalChangeSummary), ("Storage authority", "AeroLink Documentation Center")],
            approvals, [(revision.Revision.ToString("D2"), status, revision.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd"), revision.ResponsibleOwnerId)],
            Sections(document, revision)) { Watermark = watermark, ControlledStatusControls = true };
    }

    private static IReadOnlyList<PublicationSection> Sections(ManagedDocument document, ManagedDocumentRevision revision) =>
    [
        new("1. Purpose and scope", "This document is part of the controlled lifecycle data for the Flight Management System.",
        [new("1.1", "Purpose", document.Title, Purpose(document.Acronym), [("Applicability", "FMS software product"), ("Target revision", revision.Revision.ToString("D2"))]),
         new("1.2", "Scope", "Lifecycle scope", "Applies to airborne software planning, development, verification, configuration control and assurance activities performed for this Project.", [("Exclusions", "Aircraft installation approval and operator procedures")])]),
        new("2. References and responsibilities", "Referenced records are controlled separately; links in AeroLink identify the exact applicable records.",
        [new("2.1", "References", "Governing lifecycle data", "DO-178C objectives, approved project plans, controlled requirements, verification procedures, problem reports and change requests apply as identified by the project configuration.", [("Precedence", "Approved project and certification data")]),
         new("2.2", "Responsibilities", "Lifecycle accountability", "The responsible revision owner coordinates technical content. Independent reviewers evaluate correctness. Software Quality Assurance confirms conformity and authorizes release of the exact DOCX and PDF pair.", [("Responsible owner", revision.ResponsibleOwnerId), ("Final authority", "Software Quality Assurance")])]),
        new("3. Controlled process", "The process below is tailored to this document type and is configuration controlled.",
        [new("3.1", "Lifecycle", "Authoring and change control", Process(document.Acronym), [("Working format", "Macro-free Microsoft Word DOCX"), ("Released formats", "Immutable DOCX and approved PDF")]),
         new("3.2", "Verification", "Review and release", "A technical review evaluates accuracy, completeness, consistency and traceability. A separate final SQA review confirms the exact candidate files, electronic signatures and release evidence.", [("Independence", "The author cannot approve their own revision")]),
         new("3.3", "Records", "Retained evidence", "AeroLink retains working check-ins, checkout ownership, review decisions, electronic signatures, SHA-256 hashes, approved renditions, Project applicability and linked lifecycle artifacts.", [("Audit", "Append-only event history")])]),
        new("4. Compliance and completion", "Completion is evaluated against objective evidence rather than document status alone.",
        [new("4.1", "Completion criteria", "Ready for release", "All planned content is complete; referenced items are resolved or dispositioned; reviewers are independent; the exact DOCX and PDF candidate hashes are recorded; and SQA release authorization is electronically signed.", [("Release condition", "All review stages approved")]),
         new("4.2", "Change control", "Subsequent changes", "A change starts the single active successor revision for this Project document. Prior released revisions remain immutable and available from History.", [("Formal revision scope", revision.FormalChangeSummary)])])
    ];

    private static ElectronicSignature Signature(UserAccount user, Guid programId, ManagedDocument document, ManagedDocumentRevision revision,
        string action, string meaning, string hash, DateTimeOffset now) => new(user.Id, user.UserName, user.DisplayName,
        programId, "ManagedDocument", document.Id, $"{document.DocumentNumber}.{revision.Revision:D2}", action, meaning, hash, "showcase.seed", now);

    private void Audit(Guid documentId, string eventType, string actor, string detail, DateTimeOffset now) =>
        db.ManagedDocumentEvents.Add(new ManagedDocumentEvent(documentId, eventType, actor, detail, now));

    private static string FileStem(ManagedDocument document, ManagedDocumentRevision revision) => $"{document.DocumentNumber}.{revision.Revision:D2}";
    private static string WorkingSummary(string acronym) => acronym switch
    {
        "SDP" => "Add GitLab merge-request traceability and desktop connector responsibilities.",
        "SVP" => "Add mandatory impacted-test selection and pre-release verification evidence.",
        "SAS" => "Prepare the accomplishment summary framework and open-evidence register.",
        "ICD" => "Clarify navigation-source validity timing and disagreement annunciation interfaces.",
        _ => "Update controlled Project lifecycle content."
    };
    private static string Purpose(string acronym) => Documents.Single(x => x.Acronym == acronym).Purpose;
    private static string Process(string acronym) => acronym switch
    {
        "PSAC" => "Certification planning identifies software level, lifecycle processes, standards, transition criteria, coordination points and the evidence used to show compliance.",
        "SDP" => "Development proceeds from approved HLRs through LLRs, source code and integration under controlled standards. Each change preserves traceability to the authorizing change record.",
        "SVP" => "Verification combines reviews, analyses and tests. Changed requirements automatically identify impacted procedures that remain mandatory until satisfactory execution evidence is recorded.",
        "SCMP" => "Configuration items are uniquely identified, baselined and changed only through approved control. GitLab is the source of truth for code while AeroLink retains approved LLR-to-commit and merge-request evidence.",
        "SQAP" => "SQA independently monitors lifecycle activities, records nonconformities, verifies conformity of controlled records and approves final document release candidates.",
        "SAS" => "The accomplishment summary reconciles approved plans, delivered source and executable configuration, verification completion, problem-report disposition and remaining certification considerations.",
        _ => "Interface data is defined by source, destination, units, ranges, validity, timing, integrity monitoring, failure behavior and the requirements that govern each exchanged item."
    };
}
