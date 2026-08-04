using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The document a release is heading towards, generated before anything is frozen.
///
/// The demonstration data exercises two of the three ways a change reaches a draft — it introduces one system
/// requirement and modifies one high-level requirement — and retires nothing. So the branch that removes a
/// requirement was the one nobody would notice was wrong, which is exactly the branch worth a test.
/// </summary>
[Collection(ShowcaseApiCollection.Name)]
public sealed class DraftDocumentApiTests(ShowcaseApiFixture showcase)
{
    [Fact]
    public async Task A_draft_applies_introductions_modifications_and_retirements_and_ignores_unapproved_change()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);

        Guid releaseId;
        string retiredNumber, modifiedNumber, introducedNumber = "SYSR-900001";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var summary = showcase.Summary;
            releaseId = summary.ActiveReleaseId;

            // Two requirements already in the released baseline: one will be reworded, one taken out. The
            // seeder names that baseline, so there is no need to go looking for it.
            var members = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == summary.ReleasedBaselineId)
                                 join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == RequirementLevel.System)
                                     on member.ArtifactId equals artifact.Id
                                 orderby artifact.BaseNumber
                                 select artifact.BaseNumber).Take(2).ToListAsync();
            Assert.Equal(2, members.Count);
            modifiedNumber = members[0];
            retiredNumber = members[1];

            var approved = new SystemChangeRequest("SRCR-90001", 0, summary.ProjectId, releaseId,
                "Draft document coverage", "Problem", "Analysis", "Solution", "admin", DateTimeOffset.UtcNow);
            approved.AddRequirementChange("admin", introducedNumber, 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall do a newly introduced thing.", "New", "Test", DateTimeOffset.UtcNow);
            approved.AddRequirementChange("admin", modifiedNumber, 1, RequirementLevel.System,
                RequirementChangeKind.Modify, "The FMS shall do the reworded thing.", "Reworded", "Analysis", DateTimeOffset.UtcNow);
            approved.AddRequirementChange("admin", retiredNumber, 1, RequirementLevel.System,
                RequirementChangeKind.Retire, "", "No longer applicable", "Test", DateTimeOffset.UtcNow);
            Approve(approved);

            // A second change request that is still a Draft. Nothing of it may reach the document — a draft
            // shows what has been agreed, not what somebody is currently typing.
            var unapproved = new SystemChangeRequest("SRCR-90002", 0, summary.ProjectId, releaseId,
                "Not agreed yet", "Problem", "Analysis", "Solution", "admin", DateTimeOffset.UtcNow);
            unapproved.AddRequirementChange("admin", "SYSR-900002", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall do an unapproved thing.", "Draft", "Test", DateTimeOffset.UtcNow);

            db.SystemChangeRequests.AddRange(approved, unapproved);
            await db.SaveChangesAsync();
        }

        // Called directly first. The endpoint wraps failures in ProblemDetails, which is right for a caller and
        // useless for a diagnosis — a 500 with no detail says only that something threw.
        using (var scope = factory.Services.CreateScope())
        {
            var generator = scope.ServiceProvider.GetRequiredService<DraftDocumentGenerator>();
            var direct = await generator.GenerateAsync(releaseId, AeroLink.Domain.Traceability.ControlledDocumentType.Sysrd, "pdf", "Tester", default);
            Assert.NotNull(direct);
            Assert.Contains("DRAFT_SYSRD-000016.", direct.FileName);
        }

        using var response = await client.GetAsync($"/api/releases/{releaseId}/draft-document?type=Sysrd&format=pdf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {Encoding.UTF8.GetString(payload.AsSpan(0, Math.Min(1500, payload.Length)))}");
        var pdf = Encoding.Latin1.GetString(payload);

        // Introduced and modified both reach the document; retired leaves it.
        Assert.Contains(introducedNumber, pdf);
        Assert.Contains(modifiedNumber, pdf);
        Assert.DoesNotContain(retiredNumber, pdf);

        // And nothing from the change request that has not been agreed.
        Assert.DoesNotContain("SYSR-900002", pdf);
        Assert.DoesNotContain("unapproved thing", pdf);

        // Every page carries the stamp, cover included, and the document says plainly what it is.
        var pages = System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page[^s]").Count;
        var stamps = System.Text.RegularExpressions.Regex.Matches(pdf, @"\(DRAFT\)").Count;
        Assert.True(pages > 1, $"expected a multi-page document, got {pages}");
        Assert.Equal(pages, stamps);
        Assert.Contains("DRAFT - NOT APPROVED", pdf);

        // No manifest hash. One would assert that this content is fixed and reproducible, and it is neither.
        Assert.Contains("not applicable to a draft", pdf);
    }

    [Fact]
    public async Task An_in_work_test_procedure_document_is_available_as_a_living_draft()
    {
        using var factory = showcase.CreateFactory();
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var releaseId = showcase.Summary.ActiveReleaseId;

        // Test-procedure documents now live in Assurance. The in-work build exposes their latest approved
        // procedure content as an explicitly non-approved living draft.
        using var response = await client.GetAsync($"/api/releases/{releaseId}/draft-document?type=SystemTestProcedures&format=pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var pdf = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("DRAFT - NOT APPROVED", pdf);
        Assert.Contains("System Test Procedure Document", pdf);
        Assert.Contains("SYSTD", pdf);
        Assert.Contains("DRAFT_SYSTD-000016.", response.Content.Headers.ContentDisposition?.FileName ?? "");
    }

    [Fact]
    public async Task Drafts_keep_the_approved_document_acronym_and_docx_pages_share_the_draft_watermark()
    {
        using var factory = showcase.CreateFactory();
        using var scope = factory.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<DraftDocumentGenerator>();
        var releaseId = showcase.Summary.ActiveReleaseId;
        var expected = new[]
        {
            (AeroLink.Domain.Traceability.ControlledDocumentType.Sysrd, "SYSRD"),
            (AeroLink.Domain.Traceability.ControlledDocumentType.SwrdHighLevel, "HLRD"),
            (AeroLink.Domain.Traceability.ControlledDocumentType.SwrdLowLevel, "LLRD"),
            (AeroLink.Domain.Traceability.ControlledDocumentType.SystemTestProcedures, "SYSTD"),
            (AeroLink.Domain.Traceability.ControlledDocumentType.HighLevelTestProcedures, "HLRTD"),
            (AeroLink.Domain.Traceability.ControlledDocumentType.LowLevelTestProcedures, "LLRTD"),
        };

        GeneratedOutput? highLevelDraft = null;
        foreach (var (type, acronym) in expected)
        {
            var output = await generator.GenerateAsync(releaseId, type, "docx", "Tester", default);
            Assert.NotNull(output);
            Assert.StartsWith($"DRAFT_{acronym}-000016.", output.FileName);
            if (type == AeroLink.Domain.Traceability.ControlledDocumentType.SwrdHighLevel) highLevelDraft = output;
        }

        Assert.NotNull(highLevelDraft);
        using var archive = new ZipArchive(new MemoryStream(highLevelDraft.Content), ZipArchiveMode.Read);
        var header = await ReadPartAsync(archive, "word/header1.xml");
        var document = await ReadPartAsync(archive, "word/document.xml");
        Assert.Contains("AeroLinkWatermark", header);
        Assert.Contains("string=\"DRAFT\"", header);
        Assert.Contains("<w:headerReference", document);
        Assert.Contains("<w:pStyle w:val=\"Heading1\"/><w:pageBreakBefore/>", document);
        Assert.Contains("HLRD-000016", document);
        Assert.Contains("High-Level Software Requirements Document (HLRD)", document);
        Assert.DoesNotContain("SWRD-HLR", document);
    }

    /// <summary>Drives a change request through review to Approved the way the workflow does.</summary>
    private static void Approve(SystemChangeRequest scr)
    {
        var now = DateTimeOffset.UtcNow;
        scr.SubmitForReview("admin", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new { displayName = "Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<string> ReadPartAsync(ZipArchive archive, string name)
    {
        var part = archive.GetEntry(name);
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return await reader.ReadToEndAsync();
    }
}
