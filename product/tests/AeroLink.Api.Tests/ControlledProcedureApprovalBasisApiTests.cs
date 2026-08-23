using System.IO.Compression;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Adversarial #420 coverage: approval evidence follows the exact carried procedure revision, not the
/// current baseline's TCR-selection list. This is what preserves the original authority for an unchanged
/// revision inherited by a successor baseline and prevents another discipline's selected package from being
/// printed as though it approved this document.
/// </summary>
public sealed class ControlledProcedureApprovalBasisApiTests
{
    [Fact]
    public async Task Approval_basis_uses_exact_carried_source_tcrs_and_never_leaks_into_requirement_documents()
    {
        using var factory = new AeroLinkApiFactory();
        Guid systemDocumentId;
        Guid requirementDocumentId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = new DateTimeOffset(2026, 8, 10, 4, 0, 0, TimeSpan.Zero);
            var generatedAt = now.AddMinutes(10);

            var program = new ProgramRecord("Document authority boundary", "DAB");
            var project = new ProjectRecord(program.Id, "Authority project", "Authority product");
            var release = new SoftwareRelease(project.Id, "7.1", false);

            var change = new SystemChangeRequest("SRCR-990001", 0, project.Id, release.Id,
                "Authority source", "Problem", "Analysis", "Solution", "change.author", now);
            change.AddRequirementChange("change.author", "SYSR-990001", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The product shall preserve approval authority.",
                "Authority must remain exact.", "Test", now);
            change.SubmitForReview("change.author",
                [new ApproverSelection("requirement.approver", "Requirement Approver")], now);
            change.ApproveActiveStage("requirement.approver", now.AddMinutes(1));

            // A second source package is deliberately not selected into this successor baseline. Its System
            // procedure is inherited exact content, so its original TCR signatures must still be printed.
            var inheritedSource = new SystemChangeRequest("SRCR-990002", 0, project.Id, release.Id,
                "Second inherited authority source", "Problem", "Analysis", "Solution",
                "change.author", now);

            var baseline = new CandidateBaseline("SW-07.10", 0, project.Id, release.Id, null,
                "Successor authority baseline", "cm", now);
            baseline.Select(change, "cm", now.AddMinutes(2));

            var systemTcr = ApprovedTcr(project.Id, release.Id, change.Id, change.DisplayNumber,
                TestChangeReviewDiscipline.System, "SYSTPCR-990001", "SYSTP-990001",
                TestProcedureLevel.System, "system.tcr.approver", now.AddMinutes(3));
            var secondSystemTcr = ApprovedTcr(project.Id, release.Id, inheritedSource.Id,
                inheritedSource.DisplayNumber, TestChangeReviewDiscipline.System, "SYSTPCR-990002",
                "SYSTP-990002", TestProcedureLevel.System, "system.tcr.approver", now.AddMinutes(4));
            var hlrTcr = ApprovedTcr(project.Id, release.Id, change.Id, change.DisplayNumber,
                TestChangeReviewDiscipline.HighLevelSoftware, "HLRTCCR-990001", "HLRTP-990001",
                TestProcedureLevel.HighLevel, "hlr.tcr.approver", now.AddMinutes(5));

            // Deliberately select only the HLR package. The two System revisions below represent unchanged
            // predecessor content carried by this baseline: neither original System TCR is selected again.
            // Approval evidence must follow those exact carried revisions and exclude the unrelated HLR TCR.
            baseline.SelectTestChangeRequest(hlrTcr, "cm", now.AddMinutes(8));

            var systemProcedure = new TestProcedure(project.Id, "SYSTP-990001",
                "Inherited system procedure", "system.author", now, TestProcedureLevel.System);
            var systemRevision = new TestProcedureRevision(systemProcedure.Id, 0,
                "Verify the inherited system behaviour.", "Configured system.", "Exercise system behaviour.",
                "System behaviour is correct.", TestProcedureState.Approved, "system.author", now,
                sourceTestChangeRequestId: systemTcr.Id);
            var secondSystemProcedure = new TestProcedure(project.Id, "SYSTP-990002",
                "Second inherited system procedure", "system.author", now, TestProcedureLevel.System);
            var secondSystemRevision = new TestProcedureRevision(secondSystemProcedure.Id, 0,
                "Verify the second inherited system behaviour.", "Configured system.",
                "Exercise the second system behaviour.", "The second system behaviour is correct.",
                TestProcedureState.Approved, "system.author", now,
                sourceTestChangeRequestId: secondSystemTcr.Id);
            var hlrProcedure = new TestProcedure(project.Id, "HLRTP-990001",
                "Selected HLR procedure", "hlr.author", now, TestProcedureLevel.HighLevel);
            var hlrRevision = new TestProcedureRevision(hlrProcedure.Id, 0,
                "Verify the HLR behaviour.", "Configured software.", "Exercise HLR behaviour.",
                "HLR behaviour is correct.", TestProcedureState.Approved, "hlr.author", now,
                sourceTestChangeRequestId: hlrTcr.Id);

            var requirementDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.Sysrd, "SYSRD-990001", "Authority System Requirements", 0,
                new string('a', 64), 0, generatedAt);
            var systemDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.SystemTestProcedures, "SYSTD-990001", "Authority System Procedures", 0,
                new string('b', 64), 2, generatedAt);

            db.AddRange(program, project, release, change, inheritedSource, baseline, systemTcr,
                secondSystemTcr, hlrTcr, systemProcedure, systemRevision, secondSystemProcedure,
                secondSystemRevision, hlrProcedure, hlrRevision,
                new BaselineTestProcedureSelection(baseline.Id, systemProcedure.Id, systemRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, secondSystemProcedure.Id, secondSystemRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, hlrProcedure.Id, hlrRevision.Id),
                requirementDocument, systemDocument);
            await db.SaveChangesAsync();
            systemDocumentId = systemDocument.Id;
            requirementDocumentId = requirementDocument.Id;
        }

        using var renderScope = factory.Services.CreateScope();
        var generator = renderScope.ServiceProvider.GetRequiredService<ControlledOutputGenerator>();
        var systemXml = await DocumentXmlAsync(
            Assert.IsType<GeneratedOutput>(await generator.GenerateAsync(systemDocumentId, "docx", default)));
        var requirementXml = await DocumentXmlAsync(
            Assert.IsType<GeneratedOutput>(await generator.GenerateAsync(requirementDocumentId, "docx", default)));

        Assert.Contains("SYSTPCR-990001.00", systemXml);
        Assert.Contains("SYSTPCR-990002.00", systemXml);
        Assert.Contains("Test Change Authority · SYSTPCR-990001.00 · cycle 1 · Reviewer", systemXml);
        Assert.Contains("Test Change Authority · SYSTPCR-990001.00 · cycle 1 · Approver", systemXml);
        Assert.Contains("Test Change Authority · SYSTPCR-990002.00 · cycle 1 · Reviewer", systemXml);
        Assert.Contains("Test Change Authority · SYSTPCR-990002.00 · cycle 1 · Approver", systemXml);
        Assert.Contains("system.tcr.approver.reviewer", systemXml);
        Assert.Contains("system.tcr.approver", systemXml);
        Assert.Contains("signed 04:04 UTC", systemXml);
        Assert.Contains("signed 04:06 UTC", systemXml);
        Assert.Contains("snapshot ", systemXml);
        Assert.Contains("Upstream Change Authority", systemXml);
        Assert.Contains("requirement.approver", systemXml);
        Assert.DoesNotContain("hlr.tcr.approver", systemXml);

        Assert.Contains("Change Authority", requirementXml);
        Assert.Contains("requirement.approver", requirementXml);
        Assert.DoesNotContain("Test Change Authority", requirementXml);
        Assert.DoesNotContain("system.tcr.approver", requirementXml);
        Assert.DoesNotContain("hlr.tcr.approver", requirementXml);
    }

    private static TestChangeReview ApprovedTcr(Guid projectId, Guid releaseId, Guid changeRequestId,
        string changeRequestNumber, TestChangeReviewDiscipline discipline, string tcrNumber,
        string procedureNumber, TestProcedureLevel level, string approverId, DateTimeOffset now)
    {
        var review = new TestChangeReview(projectId, releaseId, changeRequestId, discipline,
            changeRequestNumber, now);
        review.RecordTestChangeRequired("verification.author", now);
        review.AssignControlledNumber(tcrNumber, now);
        review.AddProcedureChange("verification.author", new TestProcedureChangeDraft(procedureNumber, 0,
            level, TestProcedureChangeKind.Introduce, $"Procedure from {tcrNumber}",
            "Verify exact authority.", "Configured product.", "Exercise the controlled behaviour.",
            "The controlled behaviour is correct.", "Approval-basis regression.",
            JsonSerializer.Serialize(new[] { Guid.NewGuid() })), now);
        review.WriteCase("verification.author", "Authority package", "Problem", "Analysis", "Solution", now);
        var reviewerId = approverId + ".reviewer";
        review.SubmitForReview("verification.author",
            [
                new ApproverSelection(reviewerId, approverId + " Reviewer", ProgramRole.Reviewer),
                new ApproverSelection(approverId, approverId + " Approver", ProgramRole.Approver),
            ], true, now);
        review.ApproveActiveStage(reviewerId, "Reviewed exact procedure authority.", now.AddMinutes(1));
        review.ApproveActiveStage(approverId, "Approved exact procedure authority.", now.AddMinutes(2));
        return review;
    }

    private static async Task<string> DocumentXmlAsync(GeneratedOutput output)
    {
        using var archive = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var part = archive.GetEntry("word/document.xml");
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return await reader.ReadToEndAsync();
    }
}
