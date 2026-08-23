using System.IO.Compression;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class ReleaseCampaignPersistenceTests(ShowcaseDatabaseFixture showcaseFixture)
{
    [Fact]
    public async Task Showcase_campaign_has_real_gates_impacts_outputs_and_checksummed_evidence()
    {
        // A private copy of the showcase, rather than a 69-second rebuild of one.
        using var showcase = showcaseFixture.Create(); var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-evidence-{Guid.NewGuid():N}");
        var options = showcase.Options;
        try
            // The in-work build's campaign specifically: the released build now has its own closed one.
        {
            await using var db = showcase.Context(); var summary = showcaseFixture.Summary;
            var campaign = await db.ReleaseCampaigns.SingleAsync(x => x.ProjectId == summary.ProjectId && x.ReleaseId == summary.ActiveReleaseId); Assert.Equal(ReleaseCampaignState.Verification, campaign.State);
            Assert.Equal(32, await db.ImpactDispositions.CountAsync(x => x.CampaignId == campaign.Id)); Assert.Equal(8, await db.ImpactDispositions.CountAsync(x => x.CampaignId == campaign.Id && x.State == ImpactDispositionState.Addressed));
            var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default); Assert.False(readiness.ReadyForRelease); Assert.Contains(readiness.Gates, x => x.Code == "change_control" && x.Completed == 2 && x.Total == 7);
            var blocker = new ProblemReport(summary.ProjectId, "PR-00001", "Unresolved release-impacting failure", "A failed verification result remains unresolved.", "", "verification.engineer", DateTimeOffset.UtcNow);
            blocker.SetReleaseBlocker("verification.engineer", true, DateTimeOffset.UtcNow); db.ProblemReports.Add(blocker); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "problem_reports" && !x.Complete && x.Total == 1 && x.Detail.Contains("PR-00001.00"));
            var waiverActorId = Guid.NewGuid(); var waiverAt = DateTimeOffset.UtcNow;
            blocker.RecordReleaseWaiverDecision("independent.quality", waiverAt);
            var waiver = new ReadinessWaiver(blocker.ProjectId, "ProblemReportReleaseBlocker", blocker.Id,
                blocker.Revision, blocker.ReleaseBlockerVersion, "A bounded release interval was independently accepted.",
                waiverActorId, "independent.quality", "SoftwareQualityAnalyst",
                "IndependentProblemReportReleaseWaiver", waiverAt.AddDays(2), "independent.quality", waiverAt);
            db.ReadinessWaivers.Add(waiver); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "problem_reports" && x.Complete && x.Total == 0);
            waiver = await db.ReadinessWaivers.SingleAsync(x => x.Id == waiver.Id);
            waiver.Revoke("independent.quality", "The bounded interval ended.", waiverAt.AddHours(1));
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "problem_reports" && !x.Complete && x.Total == 1);
            var expired = new ReadinessWaiver(blocker.ProjectId, "ProblemReportReleaseBlocker", blocker.Id,
                blocker.Revision, blocker.ReleaseBlockerVersion, "An expired bounded interval.", Guid.NewGuid(),
                "independent.quality", "SoftwareQualityAnalyst", "IndependentProblemReportReleaseWaiver",
                waiverAt.AddHours(-1), "independent.quality", waiverAt.AddHours(-2));
            db.ReadinessWaivers.Add(expired); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "problem_reports" && !x.Complete && x.Total == 1);
            var documentId = await db.ControlledDocuments.Where(x => x.BaselineId == summary.ReleasedBaselineId).Select(x => x.Id).FirstAsync(); var generator = new ControlledOutputGenerator(db, new RichContentPublisher(db, new EvidenceFileStore(Path.Combine(Path.GetTempPath(), $"aerolink-evidence-{Guid.NewGuid():N}"))));
            var docx = await generator.GenerateAsync(documentId, "docx", default); var pdf = await generator.GenerateAsync(documentId, "pdf", default); Assert.NotNull(docx); Assert.NotNull(pdf); Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(pdf!.Content, 0, 8));
            using (var archive = new ZipArchive(new MemoryStream(docx!.Content), ZipArchiveMode.Read))
            {
                var part = archive.GetEntry("word/document.xml"); Assert.NotNull(part); using var reader = new StreamReader(part!.Open()); var xml = await reader.ReadToEndAsync();
                Assert.Contains("Document Control", xml); Assert.Contains("Approval Register", xml); Assert.Contains("Development Assurance Reviewer", xml); Assert.Contains("Manifest SHA-256", xml);
            }
            var historical = await db.SystemChangeRequests.Where(x => x.ProjectId == summary.ProjectId)
                .Select(x => new { x.Id, x.AuthorId }).FirstAsync();
            const string historicalDisplayName = "Named Historical Author";
            db.UserAccounts.Add(new AeroLink.Domain.Identity.UserAccount(historical.AuthorId, historicalDisplayName,
                "historical.author@example.invalid", "not-used", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            var scrOutput = await new ChangeRequestOutputGenerator(db).GenerateAsync(historical.Id, "docx", default); Assert.NotNull(scrOutput);
            using (var archive = new ZipArchive(new MemoryStream(scrOutput!.Content), ZipArchiveMode.Read)) { using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open()); var xml = await reader.ReadToEndAsync(); Assert.Contains("APPROVALS RECORDED FOR THIS PUBLICATION", xml); Assert.Contains("Change Request Definition", xml); Assert.Contains("Audit History", xml); Assert.Contains(historicalDisplayName, xml); Assert.DoesNotContain($">{historical.AuthorId}<", xml); }
            var procedureDocumentId = await db.ControlledDocuments.Where(x => x.BaselineId == summary.ReleasedBaselineId && x.Type == AeroLink.Domain.Traceability.ControlledDocumentType.SystemTestProcedures).Select(x => x.Id).SingleAsync();
            var procedureOutput = await generator.GenerateAsync(procedureDocumentId, "docx", default); Assert.NotNull(procedureOutput);
            string procedureXml;
            using (var archive = new ZipArchive(new MemoryStream(procedureOutput!.Content), ZipArchiveMode.Read)) { using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open()); procedureXml = await reader.ReadToEndAsync(); Assert.Contains("System Test Procedure Document", procedureXml); Assert.Contains("Procedure steps", procedureXml); Assert.Contains("Expected result", procedureXml); Assert.Contains("Approval Register", procedureXml); }
            var generatedAt = await db.ControlledDocuments.Where(x => x.Id == procedureDocumentId).Select(x => x.GeneratedAt).SingleAsync();
            var laterProcedure = new TestProcedure(summary.ProjectId, "SYSTP-00000999", "Future procedure excluded from the historical publication", "test.author", generatedAt.AddMinutes(1), TestProcedureLevel.System);
            var laterRevision = new TestProcedureRevision(laterProcedure.Id, 0, "Verify later behavior.", "Later configuration.", "Execute later behavior.", "Later result is observed.", TestProcedureState.Approved, "test.author", generatedAt.AddMinutes(1));
            db.AddRange(laterProcedure, laterRevision); await db.SaveChangesAsync();
            var regenerated = await generator.GenerateAsync(procedureDocumentId, "docx", default); Assert.NotNull(regenerated);
            using (var archive = new ZipArchive(new MemoryStream(regenerated!.Content), ZipArchiveMode.Read)) { using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open()); var regeneratedXml = await reader.ReadToEndAsync(); Assert.Equal(procedureXml, regeneratedXml); Assert.DoesNotContain(laterProcedure.Title, regeneratedXml); }
            var store = new EvidenceFileStore(evidenceRoot);
            var stored = await store.StoreAsync(new MemoryStream("evidence payload"u8.ToArray()), "run.json", "application/json", default); Assert.Equal(64, stored.Sha256.Length); await using var opened = store.OpenRead(stored.StorageKey); Assert.Equal(stored.Size, opened.Length);
        }
        finally { if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true); }
    }

    [Fact]
    public async Task Release_execution_reconciles_versioned_links_and_imports_an_exact_build_manifest()
    {
        // A private copy of the showcase, rather than a 44-second rebuild of one.
        using var showcase = showcaseFixture.Create(); var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-execution-evidence-{Guid.NewGuid():N}");
        var options = showcase.Options;
        try
        {
            await using var db = showcase.Context(); var summary = showcaseFixture.Summary;
            var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleAsync(x => x.ProjectId == summary.ProjectId && x.ReleaseId == summary.ActiveReleaseId);
            var baseline = await db.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleAsync(x => x.Id == campaign.BaselineId);
            var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).Where(x => x.TargetReleaseId == campaign.ReleaseId).ToListAsync();
            var now = new DateTimeOffset(2025, 1, 10, 14, 0, 0, TimeSpan.Zero);
            foreach (var request in requests.Where(x => x.State != ChangeRequestState.Deferred && x.State != ChangeRequestState.SelectedForBaseline))
            {
                // The copied showcase contains a small number of pre-#738 approved/in-review v1 packages.
                // Re-enter those packages through their ordinary lifecycle before making the author's new
                // classification decision.  This preserves their historical review evidence while ensuring
                // the successor review is current and cannot rely on the seed-only v1 materialization seam.
                if (request.SnapshotContractVersion < SystemChangeRequest.CurrentSnapshotContractVersion)
                {
                    if (request.State == ChangeRequestState.Approved)
                    {
                        request.Defer(request.AuthorId, "Re-open the historical package for release-execution qualification.", now);
                        request.Reinstate(request.AuthorId, now);
                    }
                    else if (request.State == ChangeRequestState.InReview)
                        request.CancelReview(request.AuthorId, "Re-open the historical package for release-execution qualification.", now);
                }
                if (request.State == ChangeRequestState.Draft)
                {
                    // These requests are re-entering review through this test journey after the historical
                    // showcase was rehydrated. Make the reviewer's classification explicit: non-root legacy
                    // drafts are intentionally documented as independent Derived work, with no fabricated
                    // parent identity. The production seed remains untouched and preserves its v1 evidence.
                    var authored = request.RequirementChanges.Select(change => new RequirementChangeDraft(
                        change.BaseNumber, change.Revision, change.Level, change.Kind, change.Statement,
                        string.IsNullOrWhiteSpace(change.Rationale) ? "Release execution qualification." : change.Rationale,
                        change.VerificationMethod, change.RichText,
                        change.Level == RequirementLevel.System ? "{}" : "{\"derived\":true}",
                        change.ImpactDispositionJson, change.TargetSectionId, "[]")).ToList();
                    request.UpdateDraft(request.AuthorId, request.Title, request.Problem, request.Analysis,
                        request.Solution, authored, now);
                    request.SubmitForReview(request.AuthorId, [new ApproverSelection("release.reviewer", "Release Reviewer")], now);
                    await db.SaveChangesAsync();
                }
                while (request.State == ChangeRequestState.InReview) { request.ApproveActiveStage(request.ActiveReviewCycle!.Steps.Single(x => x.State == ApprovalStepState.Active).ApproverId, now); await db.SaveChangesAsync(); }
                baseline.Select(request, "cm.test", now); await db.SaveChangesAsync();
            }
            baseline.Freeze("cm.test", now); await db.SaveChangesAsync();
            var materialized = await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(baseline.Id, "cm.test", now, default); Assert.Equal(1251, materialized.ActiveRequirementCount);
            var service = new ReleaseExecutionService(db, new EvidenceFileStore(evidenceRoot)); var reconciled = await service.ReconcileAsync(campaign.Id, "assurance.test", now, default);
            Assert.Equal(0, reconciled.TraceLinksCreated);
            // Exact trace carry-forward is atomic with requirement materialisation and marked suspect where the
            // upstream requirement changed; reconciliation reports that state rather than creating history.
            Assert.True(reconciled.SuspectCoverage > 0);
            Assert.True(reconciled.UncoveredRequirements >= 1);

            var introducedRevision = await (from member in db.BaselineRequirements where member.BaselineId == baseline.Id join artifact in db.Requirements on member.ArtifactId equals artifact.Id where artifact.BaseNumber == "SYSR-000151" select member.RevisionId).SingleAsync();
            var procedure = new TestProcedure(summary.ProjectId, "SYSTP-000076", "Verify round-robin waypoint sequencing", "test.author", now, TestProcedureLevel.System);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify round-robin sequencing.", "Load the release candidate.", "Exercise every eligible sequencing transition.", "Every transition follows the approved round-robin requirement.", TestProcedureState.Approved, "test.author", now);
            db.AddRange(procedure, revision, new TestRequirementCoverage(revision.Id, introducedRevision));
            var build = new SoftwareBuild(summary.ProjectId, campaign.ReleaseId, baseline.Id, "FMS-1.6-RC1", "Release-candidate verification build.", "build.engineer", now); db.SoftwareBuilds.Add(build);
            campaign.SelectVerificationBuild(build.Id, "release.manager", now); await db.SaveChangesAsync();

            var template = JsonSerializer.Deserialize<List<VerificationManifestRow>>(await service.CreateVerificationTemplateAsync(campaign.Id, default))!; Assert.Equal(516, template.Count);
            var completed = template.Select(x => x with { Outcome = "Pass", ExecutedAt = now, ExecutedBy = "test.team", Configuration = build.BuildNumber, Determination = "Observed results satisfy the approved expected result." }).ToList();
            await using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(completed)); await using var evidence = new MemoryStream("signed verification campaign evidence"u8.ToArray());
            var imported = await service.ImportVerificationAsync(campaign.Id, manifest, evidence, "FMS-1.6-RC1-verification.zip", "application/zip", "test.lead", now, default);
            Assert.Equal(516, imported.ExecutionsRecorded); Assert.Equal(516, imported.Passed); Assert.Equal(516, await db.TestExecutionEvidence.CountAsync(x => x.EvidenceId == imported.EvidenceId));

            // Derived from the projection rather than a fixed count. The gate owes evidence for exactly the
            // LLR revisions this build changed, so a hard-coded total would silently keep passing if that set
            // were ever redefined — which is how a five-record demonstration cap came to stand in for the rule.
            var requiredCode = await CodeTraceabilityProjection.RequiredAsync(db, campaign.ProjectId, campaign.ReleaseId, baseline.Id, default);
            Assert.All(requiredCode, x => Assert.True(x.ChangedInBuild));
            var mappedCodeIds = await db.CodeTraceabilityRecords.Where(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId).Select(x => x.RequirementRevisionId).ToListAsync();
            var missingCode = requiredCode.Where(x => !mappedCodeIds.Contains(x.RevisionId)).ToList();
            Assert.NotEmpty(missingCode);

            var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "code_traceability" && !x.Complete
                && x.Completed == requiredCode.Count - missingCode.Count && x.Total == requiredCode.Count);
            var beforeCodeMappingHash = await service.ComputeReviewManifestHashAsync(campaign.Id, default);

            foreach (var owed in missingCode)
                db.CodeTraceabilityRecords.Add(new CodeTraceabilityRecord(campaign.ProjectId, campaign.ReleaseId, owed.ArtifactId, owed.RevisionId,
                    CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
                    "The exact LLR change is limited to clarification of already implemented behavior.", false, "software.lead", now));
            await db.SaveChangesAsync();
            var initialManifestHash = await service.ComputeReviewManifestHashAsync(campaign.Id, default);
            Assert.NotEqual(beforeCodeMappingHash, initialManifestHash);
            readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
            Assert.Contains(readiness.Gates, x => x.Code == "code_traceability" && x.Complete
                && x.Completed == requiredCode.Count && x.Total == requiredCode.Count);
            Assert.Equal(64, initialManifestHash.Length); Assert.Equal(initialManifestHash, await service.ComputeReviewManifestHashAsync(campaign.Id, default));
            // The stable catalog may describe today's draft, but a signed release-review manifest is built
            // from exact revision titles. Editing that catalog metadata cannot rewrite the frozen evidence.
            procedure.UpdateDraft("Mutable catalog title changed after the exact revision", procedure.OwnerId,
                now.AddSeconds(1));
            await db.SaveChangesAsync();
            Assert.Equal(initialManifestHash, await service.ComputeReviewManifestHashAsync(campaign.Id, default));
            var pendingImpact = await db.ImpactDispositions.FirstAsync(x => x.CampaignId == campaign.Id && x.State == ImpactDispositionState.Pending);
            pendingImpact.Disposition(ImpactDispositionState.Addressed, "Disposition changes are part of the signed release package.", "assurance.test", now.AddMinutes(1)); await db.SaveChangesAsync();
            var changedManifestHash = await service.ComputeReviewManifestHashAsync(campaign.Id, default); Assert.NotEqual(initialManifestHash, changedManifestHash);

            campaign.BeginReleaseReview("release.manager", [("release.reviewer", "Release Reviewer")], changedManifestHash, now.AddMinutes(2));
            db.ReleaseApprovals.AddRange(campaign.Approvals); await db.SaveChangesAsync();
            var frozenReconciliation = await Assert.ThrowsAsync<DomainException>(() => service.ReconcileAsync(campaign.Id, "assurance.test", now.AddMinutes(3), default));
            Assert.Contains("frozen", frozenReconciliation.Message, StringComparison.OrdinalIgnoreCase);
            await using var blockedManifest = new MemoryStream(); await using var blockedEvidence = new MemoryStream();
            var frozenImport = await Assert.ThrowsAsync<DomainException>(() => service.ImportVerificationAsync(campaign.Id, blockedManifest, blockedEvidence, "blocked.zip", "application/zip", "test.lead", now.AddMinutes(3), default));
            Assert.Contains("frozen", frozenImport.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true); }
    }
}
