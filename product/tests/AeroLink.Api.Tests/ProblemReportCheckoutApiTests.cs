using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Text;
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Problem_report_output_reads_retained_legacy_snapshot_schemas_without_rewriting_their_hash(int schema)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId, _) = await SeedAsync(factory, $"PRLEGACY{schema}");
        var reportId = await RaiseAsync(client, projectId, releaseId);

        string historicalJson;
        string historicalHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            historicalJson = LegacyOutputSnapshot(report, schema);
            historicalHash = ProblemReportEvidenceContract.Hash(historicalJson);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                $"LegacyV{schema}Fixture", "history.engineer", historicalHash, historicalJson,
                DateTimeOffset.UtcNow.AddMinutes(1), snapshotSchemaVersion: schema));
            await db.SaveChangesAsync();
        }

        using var output = await client.GetAsync($"/api/problem-reports/{reportId}/download?revision=0&format=docx");
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await output.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var document = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var documentText = await document.ReadToEndAsync();
        Assert.Contains($"Snapshot schema", documentText);
        Assert.Contains($">{schema}<", documentText);
        Assert.Equal(historicalHash, ProblemReportEvidenceContract.Hash(historicalJson));
        if (schema == 2)
            Assert.Contains("Legacy type", documentText);
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

    private static async Task<Guid> UploadImageAsync(HttpClient client, Guid projectId, string fileName)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(projectId.ToString()), "projectId");
        content.Add(new StringContent("A controlled test figure"), "alt");
        var image = new ByteArrayContent(Png());
        image.Headers.ContentType = new("image/png");
        content.Add(image, "file", fileName);
        using var response = await client.PostAsync("/api/content/images", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static string LegacyOutputSnapshot(ProblemReport report, int schema)
    {
        var snapshot = JsonNode.Parse(ProblemReportEvidenceContract.Serialize(report))!.AsObject();
        snapshot["schemaVersion"] = schema;
        if (schema == 1)
            snapshot["contract"] = "aerolink.problem-report-closure-review";
        if (schema <= 2)
        {
            snapshot["type"] = "Code";
            snapshot.Remove("category");
            snapshot.Remove("categoryProvenance");
        }

        // These fields were added after the legacy envelopes. Leaving them absent proves the reader falls
        // back to the exact plain value recorded by the old schema instead of borrowing today's rich content.
        if (schema <= 3)
            foreach (var field in new[] { "analysisRich", "problemRich", "additionalInformationRich", "systemAircraftImpactRich",
                                          "workaroundRich", "rootCauseRich", "effectsRich", "containmentRich", "correctiveActionRich" })
                snapshot.Remove(field);
        return snapshot.ToJsonString();
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
        var raw = new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255, 255 };
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
            target.Write(length); target.Write(Encoding.ASCII.GetBytes(type)); target.Write(data); target.Write(new byte[4]);
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
}
