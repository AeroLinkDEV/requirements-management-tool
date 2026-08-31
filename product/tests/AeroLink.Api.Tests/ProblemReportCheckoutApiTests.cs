using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A Problem Report is edited under the same exclusive server lease as every other controlled record.
///
/// It used to be the exception: a form of its own posted the whole record with an expected version and hoped
/// nobody else was doing the same, and the edit policy still named lifecycle states the MVP no longer
/// produces — so in practice only a Draft could be checked out at all. These cover the contract that replaced
/// it: which states may be checked out, what a check-in writes, what a discard restores, and what stays
/// immutable throughout.
/// </summary>
public sealed class ProblemReportCheckoutApiTests
{
    /// <summary>
    /// Checks a report out, applies <paramref name="edit"/> to the working copy the server handed back, and
    /// checks it in. Shared with the lifecycle tests so they exercise the real editing path rather than a
    /// test-only shortcut. Returns the report's new controlled version.
    /// </summary>
    public static async Task<long> EditUnderCheckoutAsync(HttpClient client, Guid reportId, Action<JsonObject> edit)
    {
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.True(checkout.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Checkout returned {checkout.StatusCode}: {await checkout.Content.ReadAsStringAsync()}");
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt64();

        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        edit(draft);
        using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = sessionVersion });
        Assert.True(checkIn.IsSuccessStatusCode, $"Check-in returned {checkIn.StatusCode}: {await checkIn.Content.ReadAsStringAsync()}");

        return (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}")).GetProperty("version").GetInt64();
    }

    private static async Task<(Guid ProjectId, Guid ReleaseId, string SccbUserName)> SeedAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var admin = db.UserAccounts.Single(account => account.UserName == "admin");
        var sccbUserName = $"{prefix.ToLowerInvariant()}.sccb.{Guid.NewGuid():N}";
        var sccb = new UserAccount(sccbUserName, "SCCB Project Engineer",
            $"{sccbUserName}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.AddRange(program, project, release,
            sccb, new ProgramMembership(sccb.Id, program.Id, ProgramRole.ProjectEngineer, "test.setup", DateTimeOffset.UtcNow),
            new ProgramMembership(sccb.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (project.Id, release.Id, sccbUserName);
    }

    private static async Task<Guid> RaiseAsync(HttpClient client, Guid projectId, Guid releaseId)
    {
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new
        { category = "CodeFunctional",
            projectId, releaseId, title = "Autopilot disconnect tone is late",
            problem = "The tone follows the disconnect by roughly a second.",
            problemRich = "{\"blocks\":[]}", additionalInformation = "Reported by two crews.",
            additionalInformationRich = "{\"blocks\":[]}", systemAircraftImpact = "Crew may not associate the tone with the disconnect.",
            impactAssessmentJson = "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"Unknown\",\"Llr\":\"Unknown\",\"Code\":\"Yes\",\"Tests\":\"Yes\",\"Documents\":\"No\",\"SystemAircraft\":\"Yes\",\"Safety\":\"Unknown\"}"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Walks the report from Draft to Open so the checkout under test is not a Draft one.</summary>
    private static async Task OpenAsync(HttpClient client, Guid id, string sccbUserName)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var ready = await client.PostAsJsonAsync($"/api/problem-reports/{id}/ready-for-sccb", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        version = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = sccbUserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var opened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/sccb/open", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        using var restore = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_open_report_is_checked_out_edited_and_checked_in_like_every_other_controlled_record()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRCO");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.Equal("Open", status.GetProperty("state").GetString());
        // The state that used to refuse a checkout outright.
        Assert.True(status.GetProperty("editable").GetBoolean());
        Assert.False(status.GetProperty("locked").GetBoolean());

        await EditUnderCheckoutAsync(client, id, draft =>
        {
            draft["title"] = "Autopilot disconnect tone lags the disconnect";
            draft["rootCause"] = "The tone is queued behind the mode-annunciation refresh.";
            draft["severity"] = "High";
            draft["priority"] = "Urgent";
            draft["impactAssessmentJson"] = "{\"SystemRequirements\":\"Yes\",\"Hlr\":\"Yes\",\"Llr\":\"Yes\",\"Code\":\"Yes\",\"Tests\":\"Yes\",\"Documents\":\"No\",\"SystemAircraft\":\"Yes\",\"Safety\":\"No\"}";
        });

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Autopilot disconnect tone lags the disconnect", detail.GetProperty("title").GetString());
        Assert.Equal("The tone is queued behind the mode-annunciation refresh.", detail.GetProperty("rootCause").GetString());
        Assert.Equal("High", detail.GetProperty("severity").GetString());
        Assert.Equal("Urgent", detail.GetProperty("priority").GetString());
        // Editing did not move the record's lifecycle, and a field the edit did not name is not reverted.
        Assert.Equal("Open", detail.GetProperty("state").GetString());
        Assert.Equal("Reported by two crews.", detail.GetProperty("additionalInformation").GetString());
        // And the change is in the report's own History, with who made it and when.
        var entry = Assert.Single(detail.GetProperty("revisions").EnumerateArray(),
            revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.Equal("admin", entry.GetProperty("actor").GetString());
        Assert.True(entry.GetProperty("occurredAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        // The lease is released by the check-in, so the record is immediately checkable-out again.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.False(after.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Check_in_derives_plain_problem_report_narratives_from_the_typed_record()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRCANON");
        var id = await RaiseAsync(client, projectId, releaseId);
        const string canonical = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Canonical controlled narrative\"}]}";

        await EditUnderCheckoutAsync(client, id, draft =>
        {
            draft["problem"] = "Forged plain problem";
            draft["problemRich"] = canonical;
            draft["analysis"] = "Forged plain analysis";
            draft["analysisRich"] = canonical;
        });

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Canonical controlled narrative", detail.GetProperty("problem").GetString());
        Assert.Equal("Canonical controlled narrative", detail.GetProperty("analysis").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revision = await db.ProblemReportRevisions.AsNoTracking()
            .SingleAsync(x => x.ProblemReportId == id && x.EventType == "DetailsCheckedIn");
        Assert.Contains("Canonical controlled narrative", revision.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Forged plain", revision.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discarding_a_checkout_restores_the_last_checked_in_content()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRDI");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt64();
        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        draft["title"] = "A title nobody decided to keep";
        using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, autosave.StatusCode);
        sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var discard = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/discard",
            new { expectedVersion = sessionVersion, reason = "The correction was not needed after all." });
        Assert.True(discard.IsSuccessStatusCode, await discard.Content.ReadAsStringAsync());

        // An autosave is a recovery snapshot, not a save: nothing it held reached the controlled record.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal(before.GetProperty("title").GetString(), after.GetProperty("title").GetString());
        Assert.Equal(before.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.DoesNotContain(after.GetProperty("revisions").EnumerateArray(),
            revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.False((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task A_closed_report_refuses_the_checkout_and_reopening_is_the_route_back()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRCL");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);

        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var dispositioned = await client.PostAsJsonAsync($"/api/problem-reports/{id}/disposition",
            new { expectedVersion = version, disposition = "Rejected", rationale = "The behaviour is as specified." });
        Assert.Equal(HttpStatusCode.OK, dispositioned.StatusCode);

        var closed = await client.GetFromJsonAsync<JsonElement>($"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.False(closed.GetProperty("editable").GetBoolean());
        using var refused = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("artifact_not_editable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Reopening is the route back to editable, and it keeps its own rationale requirement.
        version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var qualityLogin = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = sccbUserName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, qualityLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        using var reopened = await client.PostAsJsonAsync($"/api/problem-reports/{id}/reopen",
            new { expectedVersion = version, rationale = "A second crew report shows the tone really is late." });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenedDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Draft", reopenedDetail.GetProperty("state").GetString());
        using var adminLogin = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        Assert.True((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Identity_fields_stay_immutable_while_the_report_is_checked_out()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRIM");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");

        foreach (var (field, value) in new (string, string)[]
                 { ("reportNumber", "PR-99999"), ("reportedBy", "somebody.else"), ("responsibleEngineerId", "somebody.else") })
        {
            using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
                new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
            var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = session.GetProperty("id").GetGuid();
            var sessionVersion = session.GetProperty("version").GetInt64();
            var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
            draft[field] = value;
            using var autosave = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
                new { expectedVersion = sessionVersion, draftJson = draft.ToJsonString(), leaseMinutes = 15 });
            sessionVersion = (await autosave.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

            using var checkIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
                new { expectedVersion = sessionVersion });
            Assert.False(checkIn.IsSuccessStatusCode, $"Check-in accepted a changed {field}.");

            using var abandon = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/discard",
                new { expectedVersion = sessionVersion, reason = "Test cleanup." });
            Assert.True(abandon.IsSuccessStatusCode);
        }

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal(before.GetProperty("reportNumber").GetString(), after.GetProperty("reportNumber").GetString());
        Assert.Equal(before.GetProperty("reportedBy").GetString(), after.GetProperty("reportedBy").GetString());
        Assert.Equal(before.GetProperty("responsibleEngineerId").GetString(), after.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal(before.GetProperty("createdAt").GetDateTimeOffset(), after.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Inline_image_references_are_project_scoped_typed_and_withdrawal_aware()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var first = await SeedAsync(factory, "PRIMAGES");
        var second = await SeedAsync(factory, "PRIMAGES2");

        var ownerRecoveryImage = await UploadImageAsync(client, first.ProjectId, "owner-recovery.png", problemReportRecovery: true);
        var sameProjectMember = await SeedBystanderAsync(factory, first.ProjectId, "PRIMGDRAFT");
        await LoginAsync(client, sameProjectMember, AeroLinkApiFactory.MemberPassword);
        using (var foreignDraft = await client.GetAsync($"/api/content/images/{ownerRecoveryImage}"))
            Assert.Equal(HttpStatusCode.Forbidden, foreignDraft.StatusCode);
        using (var genericDrafts = await client.GetAsync($"/api/enterprise-hardening/attachments?projectId={first.ProjectId}&artifactType=InlineImageDraft&artifactId={first.ProjectId}"))
        {
            Assert.Equal(HttpStatusCode.OK, genericDrafts.StatusCode);
            Assert.Empty((await genericDrafts.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        }
        using (var genericDraftDownload = await client.GetAsync($"/api/enterprise-hardening/attachments/{ownerRecoveryImage}/download"))
            Assert.Equal(HttpStatusCode.NotFound, genericDraftDownload.StatusCode);
        await LoginAsync(client, "admin", AeroLinkApiFactory.AdministratorPassword);
        using (var ownerPreview = await client.GetAsync($"/api/content/images/{ownerRecoveryImage}"))
            Assert.Equal(HttpStatusCode.OK, ownerPreview.StatusCode);

        var sameProjectImage = await UploadImageAsync(client, first.ProjectId, "same-project.png");
        var allowed = await CreateWithImageAsync(client, first.ProjectId, first.ReleaseId, sameProjectImage,
            "Same project image");
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var allowedBody = await allowed.Content.ReadFromJsonAsync<JsonElement>();
        var allowedId = allowedBody.GetProperty("id").GetGuid();

        var crossProjectImage = await UploadImageAsync(client, second.ProjectId, "cross-project.png");
        using var crossProject = await CreateWithImageAsync(client, first.ProjectId, first.ReleaseId,
            crossProjectImage, "Cross project image");
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);

        Guid wrongType;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var item = new ControlledAttachment(first.ProjectId, "Evidence", first.ProjectId, null,
                Guid.NewGuid(), 1, "Not an inline image", "", "evidence.bin", "application/octet-stream", 8,
                new string('c', 64), "test/evidence.bin", null, "admin", DateTimeOffset.UtcNow);
            db.ControlledAttachments.Add(item);
            await db.SaveChangesAsync();
            wrongType = item.Id;
        }
        using var wrongTypeResponse = await CreateWithImageAsync(client, first.ProjectId, first.ReleaseId,
            wrongType, "Wrong type image");
        Assert.Equal(HttpStatusCode.BadRequest, wrongTypeResponse.StatusCode);

        var withdrawnImage = await UploadImageAsync(client, first.ProjectId, "withdrawn.png");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var item = await db.ControlledAttachments.SingleAsync(x => x.Id == withdrawnImage);
            item.Withdraw();
            await db.SaveChangesAsync();
        }
        using var withdrawn = await CreateWithImageAsync(client, first.ProjectId, first.ReleaseId,
            withdrawnImage, "Withdrawn image");
        Assert.Equal(HttpStatusCode.BadRequest, withdrawn.StatusCode);

        // A recovery endpoint may retain an untrusted draft so the owner can recover it, but the controlled
        // check-in must reject the forged reference before mutating the report or writing its evidence hash.
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = allowedId, leaseMinutes = 15 });
        Assert.True(checkout.IsSuccessStatusCode, await checkout.Content.ReadAsStringAsync());
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        draft["problemRich"] = ImageRich(crossProjectImage, "Forged cross-project image");
        draft["problem"] = "The image reference was forged in a recovery snapshot.";
        using var recovery = await client.PutAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/autosave",
            new { expectedVersion = session.GetProperty("version").GetInt64(), draftJson = draft.ToJsonString(), leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var recoveryVersion = (await recovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        using var forgedCheckIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = recoveryVersion });
        Assert.Equal(HttpStatusCode.BadRequest, forgedCheckIn.StatusCode);

        // Withdraw the exact image the accepted report revision references. The immutable revision must remain
        // renderable from its frozen snapshot, while current output must truthfully identify that the live
        // attachment is no longer available rather than silently embedding withdrawn bytes.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var item = await db.ControlledAttachments.SingleAsync(x => x.Id == sameProjectImage);
            item.Withdraw();
            await db.SaveChangesAsync();
        }

        // The current workspace must not keep serving withdrawn bytes simply because its authored JSON still
        // names the attachment. The historical-output path below is deliberately separate and exact.
        using var withdrawnCurrentImage = await client.GetAsync($"/api/content/images/{sameProjectImage}");
        Assert.Equal(HttpStatusCode.NotFound, withdrawnCurrentImage.StatusCode);

        using var historical = await client.GetAsync($"/api/problem-reports/{allowedId}/download?revision=0&format=docx");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        var historicalBytes = await historical.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(historicalBytes), ZipArchiveMode.Read);
        var historicalMedia = Assert.Single(zip.Entries,
            entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        await using (var mediaStream = historicalMedia.Open())
        using (var mediaBytes = new MemoryStream())
        {
            await mediaStream.CopyToAsync(mediaBytes);
            Assert.Equal(Png(), mediaBytes.ToArray());
        }
        using var historicalPdf = await client.GetAsync($"/api/problem-reports/{allowedId}/download?revision=0&format=pdf");
        Assert.Equal(HttpStatusCode.OK, historicalPdf.StatusCode);
        var historicalPdfText = Encoding.ASCII.GetString(await historicalPdf.Content.ReadAsByteArrayAsync());
        Assert.Contains("/Subtype /Image", historicalPdfText);
        using var currentPdf = await client.GetAsync($"/api/problem-reports/{allowedId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, currentPdf.StatusCode);
        Assert.Equal("application/pdf", currentPdf.Content.Headers.ContentType?.MediaType);
        var currentPdfText = Encoding.ASCII.GetString(await currentPdf.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("/Subtype /Image", currentPdfText);
        Assert.Contains("Image not retrieved", currentPdfText);
        using var currentDocx = await client.GetAsync($"/api/problem-reports/{allowedId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, currentDocx.StatusCode);
        using var currentZip = new ZipArchive(new MemoryStream(await currentDocx.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.DoesNotContain(currentZip.Entries, entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        using var currentDocument = new StreamReader(currentZip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("Image not retrieved", await currentDocument.ReadToEndAsync());
    }

    [Fact]
    public async Task Frozen_problem_report_output_never_resolves_an_inline_image_from_another_project()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var first = await SeedAsync(factory, "PRFROZENONE");
        var second = await SeedAsync(factory, "PRFROZENTWO");
        var reportId = await RaiseAsync(client, first.ProjectId, first.ReleaseId);
        var crossProjectImage = await UploadImageAsync(client, second.ProjectId, "other-project.png");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            var snapshot = JsonNode.Parse(ProblemReportEvidenceContract.Serialize(report))!.AsObject();
            snapshot["problemRich"] = ImageRich(crossProjectImage, "Cross-project historical figure");
            var snapshotJson = snapshot.ToJsonString();
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "LegacyImported", "history.engineer", ProblemReportEvidenceContract.Hash(snapshotJson), snapshotJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: ProblemReportEvidenceContract.SchemaVersion));
            await db.SaveChangesAsync();
        }

        using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format=docx");
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("Image not retrieved: Figure", await document.ReadToEndAsync());
    }

    [Fact]
    public async Task Problem_report_output_selects_the_exact_snapshot_when_numeric_revisions_repeat()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PREXACTOUTPUT");
        var reportId = await RaiseAsync(client, seeded.ProjectId, seeded.ReleaseId);
        var otherReportId = await RaiseAsync(client, seeded.ProjectId, seeded.ReleaseId);

        Guid firstSnapshotId;
        Guid secondSnapshotId;
        Guid foreignSnapshotId;
        string firstTitle;
        const string secondTitle = "The second lifecycle event at the same numeric revision";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var first = (await db.ProblemReportRevisions.AsNoTracking()
                    .Where(x => x.ProblemReportId == reportId && x.Revision == 0).ToListAsync())
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).First();
            firstSnapshotId = first.Id;
            using (var firstDocument = JsonDocument.Parse(first.SnapshotJson))
                firstTitle = firstDocument.RootElement.GetProperty("title").GetString()!;

            var secondJson = JsonNode.Parse(first.SnapshotJson)!.AsObject();
            secondJson["title"] = secondTitle;
            var serializedSecond = secondJson.ToJsonString();
            var second = new ProblemReportRevision(reportId, 0, "SameRevisionFollowUp", "history.engineer",
                ProblemReportEvidenceContract.Hash(serializedSecond), serializedSecond,
                first.OccurredAt.AddSeconds(1), first.SnapshotSchemaVersion);
            db.ProblemReportRevisions.Add(second);
            await db.SaveChangesAsync();
            secondSnapshotId = second.Id;
            foreignSnapshotId = await db.ProblemReportRevisions.AsNoTracking()
                .Where(x => x.ProblemReportId == otherReportId).Select(x => x.Id).FirstAsync();
        }

        static async Task<string> DocumentXml(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            return await reader.ReadToEndAsync();
        }

        using var firstOutput = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&snapshotId={firstSnapshotId}&format=docx");
        var firstXml = await DocumentXml(firstOutput);
        Assert.Contains(firstTitle, firstXml);
        Assert.DoesNotContain(secondTitle, firstXml);
        Assert.DoesNotContain("SameRevisionFollowUp", firstXml);

        using var secondOutput = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&snapshotId={secondSnapshotId}&format=docx");
        var secondXml = await DocumentXml(secondOutput);
        Assert.Contains(secondTitle, secondXml);
        Assert.Contains("SameRevisionFollowUp", secondXml);

        using var historicalPage = await client.GetAsync($"/api/problem-reports/{reportId}/history/{firstSnapshotId}");
        Assert.Equal(HttpStatusCode.OK, historicalPage.StatusCode);
        var historicalBody = await historicalPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(historicalBody.GetProperty("historicalReadOnly").GetBoolean());
        Assert.Equal(firstSnapshotId, historicalBody.GetProperty("snapshotId").GetGuid());
        Assert.Equal(firstTitle, historicalBody.GetProperty("title").GetString());
        Assert.False(historicalBody.GetProperty("capabilities").GetProperty("canApproveSqaClosure").GetBoolean());

        using var foreign = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&snapshotId={foreignSnapshotId}&format=docx");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var missing = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&snapshotId={Guid.NewGuid()}&format=docx");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var foreignPage = await client.GetAsync($"/api/problem-reports/{reportId}/history/{foreignSnapshotId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignPage.StatusCode);
        using var missingPage = await client.GetAsync($"/api/problem-reports/{reportId}/history/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missingPage.StatusCode);
    }

    [Fact]
    public async Task Current_and_frozen_problem_report_output_refuse_a_changed_inline_image_blob()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRIMAGEHASH");
        var imageId = await UploadImageAsync(client, seeded.ProjectId, "controlled-image.png");
        using var created = await CreateWithImageAsync(client, seeded.ProjectId, seeded.ReleaseId, imageId,
            "Image integrity report");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reportId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var attachment = await db.ControlledAttachments.SingleAsync(x => x.Id == imageId);
            var altered = Png();
            altered[^1] ^= 0x01;
            await File.WriteAllBytesAsync(Path.Combine(store.RootPath,
                attachment.StorageKey.Replace('/', Path.DirectorySeparatorChar)), altered);
        }

        using var direct = await client.GetAsync($"/api/content/images/{imageId}");
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        foreach (var revision in new[] { "", "&revision=0" })
        {
            using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?format=docx{revision}");
            Assert.Equal(HttpStatusCode.OK, output.StatusCode);
            using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));
            using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            Assert.Contains("Image not retrieved", await document.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task Problem_report_output_reads_a_persisted_v4_snapshot_without_rewriting_its_hash()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRV4OUT");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        string historicalJson;
        string historicalHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            historicalJson = ProblemReportEvidenceContract.SerializeForSchema(report, 4);
            historicalHash = ProblemReportEvidenceContract.Hash(historicalJson);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "LegacyV4Fixture", "history.engineer", historicalHash, historicalJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: 4));
            await db.SaveChangesAsync();
        }

        using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format=docx");
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            output.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var documentText = await document.ReadToEndAsync();
        Assert.Contains("Snapshot schema", documentText);
        Assert.Contains("PRV4OUT", documentText);
        Assert.Equal(historicalHash, ProblemReportEvidenceContract.Hash(historicalJson));
    }

    [Fact]
    public async Task Problem_report_output_round_trips_a_pinned_v5_snapshot_and_hash_for_docx_and_pdf()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRV5OUT");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        string historicalJson;
        string historicalHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            // Pin the exact bytes before publication. The output reader must consume this v5 envelope as-is;
            // it must not reserialize it as today's schema-6 aggregate or infer today's attachments.
            historicalJson = ProblemReportEvidenceContract.SerializeForSchema(report, 5);
            historicalHash = ProblemReportEvidenceContract.Hash(historicalJson);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "PinnedLegacyV5Fixture", "history.engineer", historicalHash, historicalJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: 5));
            await db.SaveChangesAsync();
        }

        foreach (var format in new[] { "docx", "pdf" })
        {
            using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format={format}");
            Assert.Equal(HttpStatusCode.OK, output.StatusCode);
            Assert.Equal(historicalHash, ProblemReportEvidenceContract.Hash(historicalJson));
            if (format == "docx")
            {
                using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
                using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                var text = await document.ReadToEndAsync();
                Assert.Contains(historicalHash, text);
                Assert.Contains(">5<", text);
            }
            else
            {
                var text = Encoding.ASCII.GetString(await output.Content.ReadAsByteArrayAsync());
                Assert.Contains(historicalHash, text);
                Assert.Contains("SNAPSHOT SCHEMA", text);
            }
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var persisted = await db.ProblemReportRevisions.SingleAsync(x => x.ProblemReportId == reportId
                && x.EventType == "PinnedLegacyV5Fixture");
            Assert.Equal(historicalJson, persisted.SnapshotJson);
            Assert.Equal(historicalHash, persisted.SnapshotHash);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Problem_report_output_reads_retained_legacy_snapshot_schemas_without_rewriting_their_hash(int schema)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, $"PRLEGACY{schema}");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        string historicalJson;
        string historicalHash;
        int historicalRevision;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            historicalRevision = report.Revision;
            historicalJson = PinnedHistoricalSnapshot(report, projectId, schema);
            using (var pinnedDocument = JsonDocument.Parse(historicalJson))
            {
                Assert.Equal(report.Id, pinnedDocument.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(projectId, pinnedDocument.RootElement.GetProperty("projectId").GetGuid());
                Assert.Equal(report.Revision, pinnedDocument.RootElement.GetProperty("revision").GetInt32());
            }
            historicalHash = ProblemReportEvidenceContract.Hash(historicalJson);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                $"LegacyV{schema}Fixture", "history.engineer", historicalHash, historicalJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: schema));
            await db.SaveChangesAsync();
            var persisted = await db.ProblemReportRevisions.AsNoTracking()
                .Where(item => item.ProblemReportId == report.Id && item.Revision == report.Revision)
                .ToListAsync();
            persisted = persisted.OrderByDescending(item => item.OccurredAt).ToList();
            var latestPersisted = persisted.First();
            Assert.Equal(historicalHash, latestPersisted.SnapshotHash);
            Assert.Equal(historicalJson, latestPersisted.SnapshotJson);
            Assert.Equal(schema, latestPersisted.SnapshotSchemaVersion);
        }

        using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision={historicalRevision}&format=docx");
        Assert.True(output.StatusCode == HttpStatusCode.OK,
            $"Historical output failed for schema {schema}: {output.StatusCode} {await output.Content.ReadAsStringAsync()}");
        using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var documentText = await document.ReadToEndAsync();
        Assert.Contains($"Snapshot schema", documentText);
        Assert.Contains($">{schema}<", documentText);
        Assert.Contains("Pinned historical report", documentText);
        Assert.Equal(historicalHash, ProblemReportEvidenceContract.Hash(historicalJson));
        if (schema is 1 or 2)
            Assert.Contains("Legacy type", documentText);

        using var pdfOutput = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision={historicalRevision}&format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdfOutput.StatusCode);
        var pdfText = Encoding.ASCII.GetString(await pdfOutput.Content.ReadAsByteArrayAsync());
        Assert.Contains("Pinned historical report", pdfText);
        Assert.Contains("PR-HIST-001", pdfText);
    }

    [Fact]
    public async Task A_tampered_persisted_snapshot_is_refused_by_both_output_formats()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRTAMPER");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            var source = ProblemReportEvidenceContract.Serialize(report);
            var tampered = JsonNode.Parse(source)!.AsObject();
            tampered["title"] = "Changed outside controlled history";
            var tamperedJson = tampered.ToJsonString();
            // Retain the authentic hash while changing the bytes. The generator must fail closed before it
            // can publish a document that claims to be the committed revision.
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "TamperedFixture", "history.engineer", ProblemReportEvidenceContract.Hash(source), tamperedJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: ProblemReportEvidenceContract.SchemaVersion));
            await db.SaveChangesAsync();
        }

        foreach (var format in new[] { "docx", "pdf" })
        {
            using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format={format}");
            Assert.Equal(HttpStatusCode.NotFound, output.StatusCode);
        }
    }

    [Fact]
    public async Task A_non_member_cannot_download_current_or_frozen_problem_report_output()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRAUTHOUT");
        var reportId = await RaiseAsync(client, projectId, releaseId);
        var nonMember = await SeedNonMemberAsync(factory, "PRAUTHOUT");
        await LoginAsync(client, nonMember, AeroLinkApiFactory.MemberPassword);

        using var current = await client.GetAsync($"/api/problem-reports/{reportId}/download?format=docx");
        Assert.Equal(HttpStatusCode.Forbidden, current.StatusCode);
        using var frozen = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format=pdf");
        Assert.Equal(HttpStatusCode.Forbidden, frozen.StatusCode);
    }

    [Fact]
    public async Task Inline_image_upload_requires_authoring_authority_or_the_actors_live_checkout()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRIMGINTENT");
        var reportId = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, reportId, sccbUserName);
        await VerifyingAsync(client, reportId);
        var bystander = await SeedBystanderAsync(factory, projectId, "PRIMGINTENT");
        await LoginAsync(client, bystander, AeroLinkApiFactory.MemberPassword);

        using (var unbound = await client.PostAsync(ImageUploadPath(projectId),
                   ImageUpload(projectId, "unbound.png")))
            Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);

        string imageRoot;
        int stagedBefore;
        using (var storageScope = factory.Services.CreateScope())
        {
            var store = storageScope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            imageRoot = store.RootPath;
            stagedBefore = Directory.Exists(imageRoot)
                ? Directory.EnumerateFiles(imageRoot, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        var fakeSessionId = Guid.NewGuid();
        // This deliberately malformed multipart body would make ReadFormAsync fail if the endpoint parsed it.
        // A 403 proves the exact fake-session preflight happens before multipart parsing or file access.
        using var malformed = new StringContent("not a multipart body");
        malformed.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        malformed.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", "unread"));
        using var fakeRequest = new HttpRequestMessage(HttpMethod.Post, ImageUploadPath(projectId, fakeSessionId))
        {
            Content = malformed
        };
        using (var fakeSession = await client.SendAsync(fakeRequest))
            Assert.Equal(HttpStatusCode.Forbidden, fakeSession.StatusCode);
        var stagedAfter = Directory.Exists(imageRoot)
            ? Directory.EnumerateFiles(imageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(stagedBefore, stagedAfter);

        Guid unrelatedSessionId;
        using (var unrelatedScope = factory.Services.CreateScope())
        {
            var unrelatedDb = unrelatedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var unrelated = new ArtifactEditSession(projectId, "Requirement", Guid.NewGuid(), null,
                new string('c', 64), "{}", bystander, DateTimeOffset.UtcNow, exclusive: false);
            unrelatedDb.ArtifactEditSessions.Add(unrelated);
            await unrelatedDb.SaveChangesAsync();
            unrelatedSessionId = unrelated.Id;
        }
        using (var unrelated = await client.PostAsync(ImageUploadPath(projectId, unrelatedSessionId),
                   ImageUpload(projectId, "unrelated-session.png", unrelatedSessionId)))
            Assert.Equal(HttpStatusCode.Forbidden, unrelated.StatusCode);

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.True(checkout.IsSuccessStatusCode, await checkout.Content.ReadAsStringAsync());
        var checkedOut = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = checkedOut.GetProperty("id").GetGuid();
        var sessionVersion = checkedOut.GetProperty("version").GetInt64();
        using var bound = await client.PostAsync(ImageUploadPath(projectId, sessionId),
            ImageUpload(projectId, "checked-out.png", sessionId));
        Assert.Equal(HttpStatusCode.Created, bound.StatusCode);
        var imageId = (await bound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var stored = await db.ControlledAttachments.AsNoTracking().SingleAsync(x => x.Id == imageId);
        Assert.Equal(reportId, stored.ArtifactId);
        Assert.Equal(bystander, stored.UploadedBy);

        using var discard = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/discard",
            new { expectedVersion = sessionVersion, reason = "Intent-bound upload test complete." });
        Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        using var abandoned = await client.PostAsync(ImageUploadPath(projectId, sessionId),
            ImageUpload(projectId, "abandoned-session.png", sessionId));
        Assert.Equal(HttpStatusCode.Forbidden, abandoned.StatusCode);
    }

    [Fact]
    public async Task Inline_image_upload_enforces_the_cumulative_project_budget_without_storing_more_bytes()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRIMGQUOTA");
        string root;
        int filesBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            root = store.RootPath;
            filesBefore = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
                : 0;
            db.ControlledAttachments.Add(new ControlledAttachment(seeded.ProjectId, "InlineImage", seeded.ProjectId,
                null, Guid.NewGuid(), 1, "Existing controlled image budget", "", "existing.png", "image/png",
                RequirementsEndpoints.MaximumInlineImageBytesPerProject, new string('a', 64), "test/quota-existing.png",
                null, "test.setup", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsync(ImageUploadPath(seeded.ProjectId), ImageUpload(seeded.ProjectId, "over-budget.png"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inline_image_project_quota", body.GetProperty("code").GetString());
        var filesAfter = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(filesBefore, filesAfter);
    }

    [Fact]
    public async Task Inline_image_upload_enforces_an_attributable_per_actor_budget()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRIMGACTOR");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ControlledAttachments.Add(new ControlledAttachment(seeded.ProjectId, "InlineImage", seeded.ProjectId,
                null, Guid.NewGuid(), 1, "Actor recovery-image budget", "", "existing.png", "image/png",
                RequirementsEndpoints.MaximumInlineImageBytesPerActorPerProject, new string('d', 64),
                "test/actor-quota-existing.png", null, "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsync(ImageUploadPath(seeded.ProjectId), ImageUpload(seeded.ProjectId, "actor-over-budget.png"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inline_image_actor_quota", body.GetProperty("code").GetString());
        Assert.Equal(RequirementsEndpoints.MaximumInlineImageBytesPerActorPerProject,
            body.GetProperty("limitBytes").GetInt64());
    }

    [Fact]
    public async Task Problem_report_creation_claims_recovery_images_and_expired_unclaimed_images_are_reclaimed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRIMGRECOVERY");

        using var upload = await client.PostAsync(ImageUploadPath(seeded.ProjectId),
            ImageUpload(seeded.ProjectId, "recovery.png", problemReportRecovery: true));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var recoveryId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var preview = await client.GetAsync($"/api/content/images/{recoveryId}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using var created = await CreateWithImageAsync(client, seeded.ProjectId, seeded.ReleaseId, recoveryId,
            "Claimed recovery image");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reportId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Guid expiredId;
        string expiredStorageKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            var claimed = await db.ControlledAttachments.SingleAsync(x => x.Id == recoveryId);
            Assert.Equal("InlineImage", claimed.ArtifactType);
            Assert.Equal(reportId, claimed.ArtifactId);

            var stored = await store.StoreAsync(new MemoryStream(Png()), "expired.png", "image/png", CancellationToken.None);
            var expired = new ControlledAttachment(seeded.ProjectId, "InlineImageDraft", seeded.ProjectId, null,
                Guid.NewGuid(), 1, "Expired browser recovery image", "", stored.OriginalFileName, stored.ContentType,
                stored.Size, stored.Sha256, stored.StorageKey, null, "admin", DateTimeOffset.UtcNow.AddDays(-31));
            db.ControlledAttachments.Add(expired);
            await db.SaveChangesAsync();
            expiredId = expired.Id;
            expiredStorageKey = stored.StorageKey;
            Assert.True(store.Exists(expiredStorageKey));
        }

        using var expiredClaim = await CreateWithImageAsync(client, seeded.ProjectId, seeded.ReleaseId, expiredId,
            "Expired recovery claim");
        Assert.Equal(HttpStatusCode.BadRequest, expiredClaim.StatusCode);
        var expiredClaimBody = await expiredClaim.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inline_image_recovery_expired", expiredClaimBody.GetProperty("code").GetString());

        using var replacement = await client.PostAsync(ImageUploadPath(seeded.ProjectId),
            ImageUpload(seeded.ProjectId, "current-recovery.png", problemReportRecovery: true));
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            Assert.False(await db.ControlledAttachments.AnyAsync(x => x.Id == expiredId));
            Assert.False(store.Exists(expiredStorageKey));
        }
    }

    [Fact]
    public async Task Inline_image_upload_rejects_a_truncated_jpeg_after_the_signature_probe()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRJPEGTRUNC");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(seeded.ProjectId.ToString()), "projectId");
        form.Add(new StringContent("ProblemReport"), "authoringContext");
        var jpeg = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x00, 0x00]);
        jpeg.Headers.ContentType = new("image/jpeg");
        form.Add(jpeg, "file", "truncated.jpg");

        using var response = await client.PostAsync(ImageUploadPath(seeded.ProjectId), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not the image type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_inline_image_uploads_cannot_overrun_the_cumulative_project_budget()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var seeded = await SeedAsync(factory, "PRIMGCONCURRENT");
        var imageBytes = Png();
        string root;
        int filesBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<EvidenceFileStore>();
            root = store.RootPath;
            filesBefore = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
                : 0;
            db.ControlledAttachments.Add(new ControlledAttachment(seeded.ProjectId, "InlineImage", seeded.ProjectId,
                null, Guid.NewGuid(), 1, "Nearly full controlled image budget", "", "existing.png", "image/png",
                RequirementsEndpoints.MaximumInlineImageBytesPerProject - imageBytes.LongLength,
                new string('b', 64), "test/quota-nearly-full.png", null, "test.setup", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var first = client.PostAsync(ImageUploadPath(seeded.ProjectId), ImageUpload(seeded.ProjectId, "first.png"));
        var second = client.PostAsync(ImageUploadPath(seeded.ProjectId), ImageUpload(seeded.ProjectId, "second.png"));
        var responses = await Task.WhenAll(first, second);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var total = await db.ControlledAttachments.AsNoTracking()
                .Where(x => x.ProjectId == seeded.ProjectId && x.ArtifactType == "InlineImage")
                .SumAsync(x => x.Size);
            Assert.Equal(RequirementsEndpoints.MaximumInlineImageBytesPerProject, total);
            Assert.Equal(filesBefore + 1,
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private static async Task<Guid> UploadImageAsync(HttpClient client, Guid projectId, string fileName,
        bool problemReportRecovery = false)
    {
        using var content = ImageUpload(projectId, fileName, problemReportRecovery: problemReportRecovery);
        using var response = await client.PostAsync(ImageUploadPath(projectId), content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent ImageUpload(Guid projectId, string fileName, Guid? editSessionId = null,
        bool problemReportRecovery = false)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(projectId.ToString()), "projectId");
        if (editSessionId is Guid sessionId)
            content.Add(new StringContent(sessionId.ToString()), "editSessionId");
        if (problemReportRecovery)
            content.Add(new StringContent("ProblemReport"), "authoringContext");
        content.Add(new StringContent("A controlled test figure"), "alt");
        var image = new ByteArrayContent(Png());
        image.Headers.ContentType = new("image/png");
        content.Add(image, "file", fileName);
        return content;
    }

    private static string ImageUploadPath(Guid projectId, Guid? editSessionId = null) =>
        editSessionId is Guid sessionId
            ? $"/api/content/images?projectId={projectId:D}&editSessionId={sessionId:D}"
            : $"/api/content/images?projectId={projectId:D}";

    private static string PinnedHistoricalSnapshot(ProblemReport report, Guid projectId, int schema)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProblemReportSnapshots", $"v{schema}.json");
        // EF's required snapshot string is trimmed on write; keep the fixture file's terminal newline pinned
        // for its raw-byte hash test, but use the exact envelope bytes the historical row stores here.
        var json = File.ReadAllText(path, Encoding.UTF8).TrimEnd('\r', '\n');
        return json
            .Replace("10000000-0000-0000-0000-000000000001", report.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("20000000-0000-0000-0000-000000000002", projectId.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> SeedNonMemberAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var userName = $"{prefix.ToLowerInvariant()}.external.{Guid.NewGuid():N}";
        db.UserAccounts.Add(new UserAccount(userName, "External reviewer", $"{userName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return userName;
    }

    private static Task<HttpResponseMessage> CreateWithImageAsync(HttpClient client, Guid projectId, Guid releaseId,
        Guid imageId, string title) => client.PostAsJsonAsync("/api/problem-reports", new
        {
            category = "CodeFunctional", projectId, releaseId, title,
            problem = "The controlled record includes an inline figure.",
            problemRich = ImageRich(imageId, title),
            additionalInformation = "", additionalInformationRich = "{\"blocks\":[]}",
            classification = "Engineering anomaly", severity = "Major", priority = "Normal",
            origin = "Manual report", impactAssessmentJson = "{}",
        });

    private static string ImageRich(Guid id, string alt) =>
        $"{{\"blocks\":[{{\"type\":\"paragraph\",\"text\":\"Figure context.\"}},{{\"type\":\"image\",\"attachmentId\":\"{id}\",\"alt\":\"{alt}\",\"caption\":\"Figure\",\"widthPercent\":60}}]}}";

    private static byte[] Png()
    {
        // Two RGB scanlines: one filter byte followed by six pixel bytes each. Keep the fixture
        // structurally valid so it exercises publication rather than relying on a tolerant PNG decoder.
        var raw = new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255 };
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(raw);
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(output, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
        static void Chunk(Stream target, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            target.Write(length);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            target.Write(body);
            Span<byte> crc = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            target.Write(crc);
        }
        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var input in bytes)
            {
                crc ^= input;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
            }
            return ~crc;
        }
    }

    /// <summary>
    /// Seeds a Program member who has access to the Project and nothing else — no engineering role, and
    /// not the report's responsible engineer. Reviewer is deliberate: it is a membership, so Project
    /// access is satisfied, and it is absent from both the engineering-authority set and the four roles
    /// the shared checkout endpoint demands.
    /// </summary>
    private static async Task<string> SeedBystanderAsync(AeroLinkApiFactory factory, Guid projectId, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var programId = db.Projects.Single(project => project.Id == projectId).ProgramId;
        var userName = $"{prefix.ToLowerInvariant()}.bystander.{Guid.NewGuid():N}";
        var account = new UserAccount(userName, "Bystander Reviewer", $"{userName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
        db.AddRange(account,
            new ProgramMembership(account.Id, programId, ProgramRole.Reviewer, "test.setup", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return userName;
    }

    private static async Task LoginAsync(HttpClient client, string userName, string password)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>Walks an Open report to Verifying, which needs nothing but Project access.</summary>
    private static async Task VerifyingAsync(HttpClient client, Guid id)
    {
        foreach (var target in new[] { "Implementing", "Verifying" })
        {
            var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
            using var moved = await client.PostAsJsonAsync($"/api/problem-reports/{id}/transition",
                new { expectedVersion = version, targetState = target });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }
    }

    /// <summary>
    /// The reported defect: a report in Verifying offered no way in to anybody but its responsible
    /// engineer, so the tester who found the mistake in it could only raise a second report saying so.
    ///
    /// Every other test in this class runs as the administrator, which was always exempt from the
    /// governing-author comparison — which is exactly why the whole suite stayed green while the rule it
    /// was meant to cover was wrong. This one runs as an ordinary member holding no engineering role.
    /// </summary>
    [Fact]
    public async Task A_project_member_who_does_not_own_the_report_can_correct_it_in_Verifying()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRBY");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        await VerifyingAsync(client, id);
        var bystander = await SeedBystanderAsync(factory, projectId, "PRBY");

        var owner = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}"))
            .GetProperty("responsibleEngineerId").GetString();
        Assert.NotEqual(bystander, owner);

        await LoginAsync(client, bystander, AeroLinkApiFactory.MemberPassword);
        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.Equal("Verifying", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("editable").GetBoolean(),
            "A Verifying report must offer a checkout to any member of its Project.");

        await EditUnderCheckoutAsync(client, id, draft => draft["rootCause"] = "The tone is queued behind the annunciator.");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("The tone is queued behind the annunciator.", detail.GetProperty("rootCause").GetString());
        // Ownership is a separate decision from correctness, and correcting the report does not take it.
        Assert.Equal(owner, detail.GetProperty("responsibleEngineerId").GetString());
        // The correction is credited to whoever made it, not to whoever the report is assigned to.
        var checkIn = detail.GetProperty("revisions").EnumerateArray()
            .First(revision => revision.GetProperty("eventType").GetString() == "DetailsCheckedIn");
        Assert.Equal(bystander, checkIn.GetProperty("actor").GetString());
    }

    /// <summary>
    /// The lease is what makes shared editing safe, so widening who may check out must not widen how many
    /// may hold it at once.
    /// </summary>
    [Fact]
    public async Task A_second_member_is_refused_while_somebody_else_holds_the_lease()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRLS");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var first = await SeedBystanderAsync(factory, projectId, "PRLS");
        var second = await SeedBystanderAsync(factory, projectId, "PRLT");

        await LoginAsync(client, first, AeroLinkApiFactory.MemberPassword);
        using var held = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.True(held.IsSuccessStatusCode, await held.Content.ReadAsStringAsync());

        await LoginAsync(client, second, AeroLinkApiFactory.MemberPassword);
        using var refused = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = id, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exclusive_lock", body.GetProperty("code").GetString());
        Assert.Equal(first, body.GetProperty("holder").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}");
        Assert.True(status.GetProperty("locked").GetBoolean());
        Assert.False(status.GetProperty("mine").GetBoolean());
    }

    /// <summary>
    /// A finished report is revived, never edited in place. The capability reports whether the reopen edge
    /// is available to this actor, and it is the same SQA-only edge the transition policy already governs.
    /// </summary>
    [Fact]
    public async Task Only_Software_Quality_is_offered_the_revive_on_a_rejected_report()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, sccbUserName) = await SeedAsync(factory, "PRRV");
        var id = await RaiseAsync(client, projectId, releaseId);
        await OpenAsync(client, id, sccbUserName);
        var bystander = await SeedBystanderAsync(factory, projectId, "PRRV");

        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}")).GetProperty("version").GetInt64();
        using var rejected = await client.PostAsJsonAsync($"/api/problem-reports/{id}/disposition",
            new { expectedVersion = version, disposition = "Rejected", rationale = "The behaviour is as specified." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await LoginAsync(client, bystander, AeroLinkApiFactory.MemberPassword);
        var toMember = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.False(toMember.GetProperty("capabilities").GetProperty("canRevive").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());

        await LoginAsync(client, sccbUserName, AeroLinkApiFactory.MemberPassword);
        var toQuality = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        var capabilities = toQuality.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("canRevive").GetBoolean());
        Assert.Equal("Draft", capabilities.GetProperty("reviveTargetState").GetString());

        // Reviving is the reopen it always was: the rationale is mandatory and the revision advances.
        var beforeRevision = toQuality.GetProperty("revision").GetInt32();
        version = toQuality.GetProperty("version").GetInt64();
        using var revived = await client.PostAsJsonAsync($"/api/problem-reports/{id}/transition",
            new { expectedVersion = version, targetState = "Draft", rationale = "A second crew report shows the tone really is late." });
        Assert.Equal(HttpStatusCode.OK, revived.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{id}");
        Assert.Equal("Draft", after.GetProperty("state").GetString());
        Assert.Equal(beforeRevision + 1, after.GetProperty("revision").GetInt32());
        Assert.True((await client.GetFromJsonAsync<JsonElement>(
            $"/api/controlled-editing/status?artifactType=ProblemReport&artifactId={id}")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Problem_report_supporting_files_are_allowlisted_versioned_hashed_and_retained_in_history()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRATTACH");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.True(checkout.IsSuccessStatusCode, await checkout.Content.ReadAsStringAsync());
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var firstBytes = OfficePackage("xl/workbook.xml", "navigation analysis v1");
        // The target is an authorization input, not a multipart form field. Missing or foreign query
        // targets must be rejected before the request body is parsed or any storage/quota work begins.
        using var missingQuery = await client.PostAsync("/api/enterprise-hardening/attachments",
            SupportingFile(projectId, reportId, sessionId, "NavigationAnalysis.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", firstBytes));
        Assert.Equal(HttpStatusCode.BadRequest, missingQuery.StatusCode);
        using var foreignQuery = await client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={Guid.NewGuid()}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "NavigationAnalysis.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", firstBytes));
        Assert.Equal(HttpStatusCode.BadRequest, foreignQuery.StatusCode);
        using var first = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "NavigationAnalysis.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", firstBytes));
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"Supporting attachment upload returned {first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("id").GetGuid();
        var logicalId = firstBody.GetProperty("logicalId").GetGuid();
        Assert.Equal(1, firstBody.GetProperty("version").GetInt32());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstBytes)).ToLowerInvariant(),
            firstBody.GetProperty("sha256").GetString());

        // Attachment writes stay inside the same exclusive checkout. The lease is rebound to the exact
        // schema-6 evidence hash so the subsequent check-in records the active file manifest rather than an
        // aggregate snapshot with an implicit empty attachment list.
        var detailAfterFirst = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}");
        var firstEvidence = detailAfterFirst.GetProperty("revisions").EnumerateArray()
            .Single(item => item.GetProperty("eventType").GetString() == "SupportingAttachmentAdded");
        var resumedSession = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, resumedSession.StatusCode);
        var resumedBody = await resumedSession.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstEvidence.GetProperty("snapshotHash").GetString(),
            resumedBody.GetProperty("baseSnapshotHash").GetString());
        using var firstCheckIn = await client.PostAsJsonAsync($"/api/controlled-editing/sessions/{sessionId}/check-in",
            new { expectedVersion = session.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, firstCheckIn.StatusCode);

        using var nextCheckout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, nextCheckout.StatusCode);
        var nextSession = await nextCheckout.Content.ReadFromJsonAsync<JsonElement>();
        var nextSessionId = nextSession.GetProperty("id").GetGuid();

        using var badType = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "notes.exe", "application/octet-stream", [1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        using var mismatchedType = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "NavigationAnalysis.xlsx", "application/pdf", firstBytes));
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedType.StatusCode);
        using var unsafeName = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "../escape.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", firstBytes));
        Assert.Equal(HttpStatusCode.BadRequest, unsafeName.StatusCode);
        using var malformedJpeg = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "screenshot.jpg", "image/jpeg", [0xff, 0xd8, 0xff, 0xd9]));
        Assert.Equal(HttpStatusCode.BadRequest, malformedJpeg.StatusCode);
        using var macroPackage = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "macro.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                OfficePackage("xl/workbook.xml", "macro", ("xl/vbaProject.bin", "not executable here"))));
        Assert.Equal(HttpStatusCode.BadRequest, macroPackage.StatusCode);
        using var externalPackage = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "external.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                OfficePackage("xl/workbook.xml", "external", ("xl/_rels/workbook.xml.rels",
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"https://example.test\" TargetMode=\"External\" /></Relationships>"))));
        Assert.Equal(HttpStatusCode.BadRequest, externalPackage.StatusCode);
        using var duplicatePackage = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "duplicate.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                OfficePackage("xl/workbook.xml", "duplicate", ("xl/workbook.xml", "same path twice"))));
        Assert.Equal(HttpStatusCode.BadRequest, duplicatePackage.StatusCode);
        using var zipBomb = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "bomb.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                OfficePackage("xl/workbook.xml", new string('A', 8 * 1024 * 1024))));
        Assert.Equal(HttpStatusCode.BadRequest, zipBomb.StatusCode);

        using var download = await client.GetAsync($"/api/enterprise-hardening/attachments/{firstId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(firstBytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("NavigationAnalysis.xlsx", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName ?? "");
        using var verified = await client.PostAsync($"/api/enterprise-hardening/attachments/{firstId}/verify", content: null);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.True((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean());

        var secondBytes = OfficePackage("xl/workbook.xml", "navigation analysis v2");
        using var replacement = await client.PostAsync($"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={nextSessionId}",
            SupportingFile(projectId, reportId, nextSessionId, "NavigationAnalysis.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", secondBytes, logicalId));
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        var secondBody = await replacement.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondBody.GetProperty("id").GetGuid();
        Assert.Equal(2, secondBody.GetProperty("version").GetInt32());

        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}");
        Assert.Equal(2, listed.GetArrayLength());
        Assert.Contains(listed.EnumerateArray(), item => item.GetProperty("id").GetGuid() == firstId
            && item.GetProperty("state").GetString() == "Superseded");
        Assert.Contains(listed.EnumerateArray(), item => item.GetProperty("id").GetGuid() == secondId
            && item.GetProperty("state").GetString() == "Active");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Equal(2, await db.ManagedDocumentStorageOperations.CountAsync(item => item.ProjectId == projectId
                && item.OperationType == "ProblemReportAttachment" && item.State == ManagedDocumentStorageOperationState.Available));
        }

        var replacementDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}");
        var replacementRevision = replacementDetail.GetProperty("revisions").EnumerateArray()
            .Single(item => item.GetProperty("eventType").GetString() == "SupportingAttachmentReplaced");
        Assert.Equal(replacementRevision.GetProperty("snapshotHash").GetString(),
            replacementDetail.GetProperty("snapshotHash").GetString());

        using (var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?format=docx"))
        {
            Assert.Equal(HttpStatusCode.OK, output.StatusCode);
            using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var text = await document.ReadToEndAsync();
            Assert.Contains("Supporting Attachments", text);
            Assert.Contains("NavigationAnalysis.xlsx", text);
            Assert.Contains(secondBody.GetProperty("sha256").GetString()!, text);
        }

        using var remove = await client.PostAsJsonAsync($"/api/enterprise-hardening/attachments/{secondId}/withdraw",
            new { editSessionId = nextSessionId, reason = "The current analysis was withdrawn pending a corrected supplier issue." });
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        var afterRemove = await client.GetFromJsonAsync<JsonElement>(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}");
        Assert.Contains(afterRemove.EnumerateArray(), item => item.GetProperty("id").GetGuid() == secondId
            && item.GetProperty("state").GetString() == "Withdrawn");

        // Removal changes the current manifest but never removes the immutable file or its prior evidence.
        using var historicalBytes = await client.GetAsync($"/api/enterprise-hardening/attachments/{secondId}/download");
        Assert.Equal(HttpStatusCode.OK, historicalBytes.StatusCode);
        Assert.Equal(secondBytes, await historicalBytes.Content.ReadAsByteArrayAsync());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{reportId}");
        var revisions = detail.GetProperty("revisions").EnumerateArray().ToList();
        var added = revisions.Single(item => item.GetProperty("eventType").GetString() == "SupportingAttachmentAdded");
        var replaced = revisions.Single(item => item.GetProperty("eventType").GetString() == "SupportingAttachmentReplaced");
        var removed = revisions.Single(item => item.GetProperty("eventType").GetString() == "SupportingAttachmentRemoved");
        Assert.Equal(1, JsonDocument.Parse(added.GetProperty("snapshotJson").GetString()!).RootElement.GetProperty("supportingAttachments").GetArrayLength());
        Assert.Equal(1, JsonDocument.Parse(replaced.GetProperty("snapshotJson").GetString()!).RootElement.GetProperty("supportingAttachments").GetArrayLength());
        Assert.Equal(0, JsonDocument.Parse(removed.GetProperty("snapshotJson").GetString()!).RootElement.GetProperty("supportingAttachments").GetArrayLength());
        Assert.NotEqual(added.GetProperty("snapshotHash").GetString(), removed.GetProperty("snapshotHash").GetString());
        Assert.Equal(removed.GetProperty("snapshotHash").GetString(), detail.GetProperty("snapshotHash").GetString());

        async Task AssertHistoricalAttachmentOutput(JsonElement revision, string expectedFileName, string expectedHash,
            bool expectsAttachment)
        {
            var snapshotId = revision.GetProperty("id").GetGuid();
            using var page = await client.GetAsync($"/api/problem-reports/{reportId}/history/{snapshotId}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var pageBody = await page.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(pageBody.GetProperty("historicalReadOnly").GetBoolean());
            Assert.Equal(snapshotId, pageBody.GetProperty("snapshotId").GetGuid());
            Assert.Equal(expectsAttachment ? 1 : 0, pageBody.GetProperty("supportingAttachments").GetArrayLength());

            using var docx = await client.GetAsync($"/api/problem-reports/{reportId}/download?snapshotId={snapshotId}&format=docx");
            Assert.Equal(HttpStatusCode.OK, docx.StatusCode);
            using (var archive = new ZipArchive(new MemoryStream(await docx.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read))
            using (var document = new StreamReader(archive.GetEntry("word/document.xml")!.Open()))
            {
                var text = await document.ReadToEndAsync();
                if (expectsAttachment)
                {
                    Assert.Contains(expectedFileName, text);
                    Assert.Contains(expectedHash, text);
                }
                else
                    Assert.DoesNotContain("NavigationAnalysis.xlsx", text);
            }

            using var pdf = await client.GetAsync($"/api/problem-reports/{reportId}/download?snapshotId={snapshotId}&format=pdf");
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            var pdfText = Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync());
            if (expectsAttachment)
            {
                Assert.Contains(expectedFileName, pdfText);
                Assert.Contains(expectedHash, pdfText);
            }
            else
                Assert.DoesNotContain("NavigationAnalysis.xlsx", pdfText);
        }

        await AssertHistoricalAttachmentOutput(added, "NavigationAnalysis.xlsx",
            Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant(), expectsAttachment: true);
        await AssertHistoricalAttachmentOutput(replaced, "NavigationAnalysis.xlsx",
            Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant(), expectsAttachment: true);
        await AssertHistoricalAttachmentOutput(removed, "", "", expectsAttachment: false);
    }

    [Fact]
    public async Task Concurrent_supporting_attachment_mutations_preserve_history_and_map_quota_conflicts()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, "PRATTACHCONC");
        var reportId = await RaiseAsync(client, projectId, releaseId);
        using var checkout = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "ProblemReport", artifactId = reportId, leaseMinutes = 15 });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var session = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var firstBytes = Encoding.UTF8.GetBytes("concurrency fixture v1\n");
        using var first = await client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "concurrency.txt", "text/plain", firstBytes));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var logicalId = firstBody.GetProperty("logicalId").GetGuid();

        // Same-report uploads are serialized by the Problem Report lock. Both may commit as immutable
        // versions, or one may receive a deterministic 409 if its provider transaction loses the race;
        // neither outcome may become a 5xx or overwrite the other's history.
        var oneTask = client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "concurrency.txt", "text/plain", Encoding.UTF8.GetBytes("concurrency fixture v2a\n"), logicalId));
        var twoTask = client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "concurrency.txt", "text/plain", Encoding.UTF8.GetBytes("concurrency fixture v2b\n"), logicalId));
        await Task.WhenAll(oneTask, twoTask);
        using var one = await oneTask;
        using var two = await twoTask;
        Assert.InRange((int)one.StatusCode, 200, 499);
        Assert.InRange((int)two.StatusCode, 200, 499);
        Assert.True(one.StatusCode == HttpStatusCode.Created || two.StatusCode == HttpStatusCode.Created);

        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}");
        Assert.Equal(1, listed.EnumerateArray().Count(item => item.GetProperty("state").GetString() == "Active"));
        var activeId = listed.EnumerateArray().Single(item => item.GetProperty("state").GetString() == "Active").GetProperty("id").GetGuid();

        // Removal and replacement use the same arbitration boundary. Depending on lock acquisition order,
        // removal can win and the replacement becomes the next active version, or replacement wins and the
        // removal correctly receives attachment_not_current for the superseded row.
        var replacementTask = client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "concurrency.txt", "text/plain", Encoding.UTF8.GetBytes("concurrency fixture v3\n"), logicalId));
        var removalTask = client.PostAsJsonAsync($"/api/enterprise-hardening/attachments/{activeId}/withdraw",
            new { editSessionId = sessionId, reason = "Concurrent mutation fixture removal." });
        await Task.WhenAll(replacementTask, removalTask);
        using var replacement = await replacementTask;
        using var removal = await removalTask;
        Assert.InRange((int)replacement.StatusCode, 200, 499);
        Assert.InRange((int)removal.StatusCode, 200, 499);
        Assert.True(replacement.StatusCode == HttpStatusCode.Created || removal.StatusCode == HttpStatusCode.NoContent);
        var afterMutations = await client.GetFromJsonAsync<JsonElement>(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}");
        Assert.Equal(1, afterMutations.EnumerateArray().Count(item => item.GetProperty("state").GetString() == "Active"));

        // Quota rejection happens after the same row lock and before filesystem staging. A retained historical
        // row is enough to exercise the limit without writing a 256 MB fixture or touching persistent state.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.ControlledAttachments.Add(new ControlledAttachment(projectId, "ProblemReport", reportId, null,
                Guid.NewGuid(), 1, "Quota fixture", "", "quota-fixture.txt", "text/plain",
                256L * 1024 * 1024, new string('d', 64), "test/quota-fixture.txt", null, "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var quota = await client.PostAsync(
            $"/api/enterprise-hardening/attachments?projectId={projectId}&artifactType=ProblemReport&artifactId={reportId}&editSessionId={sessionId}",
            SupportingFile(projectId, reportId, sessionId, "quota.txt", "text/plain", Encoding.UTF8.GetBytes("quota request\n")));
        Assert.Equal(HttpStatusCode.Conflict, quota.StatusCode);
    }

    private static MultipartFormDataContent SupportingFile(Guid projectId, Guid artifactId, Guid sessionId,
        string fileName, string contentType, byte[] bytes, Guid? logicalId = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(projectId.ToString()), "projectId");
        content.Add(new StringContent(artifactId.ToString()), "artifactId");
        content.Add(new StringContent("ProblemReport"), "artifactType");
        content.Add(new StringContent(sessionId.ToString()), "editSessionId");
        if (logicalId is Guid value) content.Add(new StringContent(value.ToString()), "logicalId");
        content.Add(new StringContent("Navigation analysis"), "label");
        content.Add(new StringContent("Exact supplier analysis retained with the Problem Report."), "description");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        content.Add(file, "file", fileName);
        return content;
    }

    private static byte[] OfficePackage(string requiredPart, string content, params (string Name, string Content)[] extras)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var types = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open(), Encoding.UTF8, 1024, leaveOpen: false))
                types.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            using (var part = new StreamWriter(archive.CreateEntry(requiredPart).Open(), Encoding.UTF8, 1024, leaveOpen: false))
            {
                var escaped = System.Security.SecurityElement.Escape(content);
                part.Write(requiredPart.StartsWith("xl/", StringComparison.Ordinal)
                    ? $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><scenario>{escaped}</scenario></workbook>"
                    : $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{escaped}</w:t></w:r></w:p></w:body></w:document>");
            }
            foreach (var extra in extras)
            {
                using var extraPart = new StreamWriter(archive.CreateEntry(extra.Name).Open(), Encoding.UTF8, 1024, leaveOpen: false);
                extraPart.Write(extra.Content);
            }
        }
        return output.ToArray();
    }
}
