using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>PostgreSQL-only qualification for the forward #723 identity migration.</summary>
[CollectionDefinition("Issue723Postgres", DisableParallelization = true)]
public sealed class Issue723PostgresCollection : ICollectionFixture<object>;

[Collection("Issue723Postgres")]
public sealed class TestChangeRequestPrefixPostgresQualificationTests
{
    private const string Predecessor = "20260822170000_RenameSoftwareVerificationArtifactsToCases";
    private const string DatabaseName = "aerolink_723_qualify";

    [DisposablePostgresFact]
    public async Task Exact_predecessor_upgrade_preserves_history_rewrites_current_sites_and_completes_idempotently()
    {
        var connection = QualificationConnectionOrThrow();
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-723-authority-{Guid.NewGuid():N}");
        try
        {
            await using var db = new AeroLinkDbContext(Options(connection));
            await db.Database.EnsureDeletedAsync();
            await db.Database.GetService<IMigrator>().MigrateAsync(Predecessor);

            var now = new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("#723 qualification", "C723");
            var project = new ProjectRecord(program.Id, "#723 project", "#723 product");
            var release = new SoftwareRelease(project.Id, "1.0", true);
            var sourceSystem = Source(project, release, "SRCR-00001", now);
            var sourceHigh = Source(project, release, "SRCR-00002", now);
            var sourceLow = Source(project, release, "SRCR-00003", now);
            var sourceUnattributed = Source(project, release, "SRCR-00004", now);
            var baseline = new CandidateBaseline("BL-00723", 0, project.Id, release.Id, null, "#723 baseline", "qualification", now);
            baseline.Select(sourceSystem, "qualification", now);
            baseline.Freeze("qualification", now);

            var system = Procedure(project.Id, "SYSTP-000001", TestProcedureLevel.System, now);
            var high = Procedure(project.Id, "HLRTC-000002", TestProcedureLevel.HighLevel, now);
            var low = Procedure(project.Id, "LLRTC-000003", TestProcedureLevel.LowLevel, now);
            var systemReview = Review(project, release, sourceSystem, TestChangeReviewDiscipline.System, "SYSTCR-000004", system, now);
            var highReview = Review(project, release, sourceHigh, TestChangeReviewDiscipline.HighLevelSoftware, "HLRTCR-000005", high, now);
            var lowReview = Review(project, release, sourceLow, TestChangeReviewDiscipline.LowLevelSoftware, "LLRTCR-000006", low, now);
            var systemRevision = Revision(system, "system procedure", "{\"baseNumber\":\"SYSTCR-000008\"}", now, systemReview.Id);
            var highRevision = Revision(high, "high case", "{\"baseNumber\":\"HLRTCR-000123\",\"prose\":\"HLRTCR-000123\"}", now, highReview.Id);
            var lowRevision = Revision(low, "low case", "{\"baseNumber\":\"LLRTCR-000456\"}", now, lowReview.Id);
            var unaffectedBaseline = new CandidateBaseline("BL-00724", 0, project.Id, release.Id, null,
                "#723 unaffected baseline", "legacy/unattributed document qualification", now);
            unaffectedBaseline.Select(sourceUnattributed, "qualification", now);
            unaffectedBaseline.Freeze("qualification", now);
            var unaffectedProcedure = Procedure(project.Id, "HLRTC-000004", TestProcedureLevel.HighLevel, now);
            var unaffectedRevision = new TestProcedureRevision(unaffectedProcedure.Id, 0, "unattributed legacy case",
                "preconditions", "steps", "expected", TestProcedureState.Approved, "author", now);
            var systemCycle = systemReview.ReviewCycles.Single();
            var highCycle = highReview.ReviewCycles.Single();
            var lowCycle = lowReview.ReviewCycles.Single();

            var oldManifestHash = new string('a', 64);
            var oldSystemBytes = Encoding.UTF8.GetBytes("old system controlled bytes");
            var oldHighBytes = Encoding.UTF8.GetBytes("old high controlled bytes");
            var oldLowBytes = Encoding.UTF8.GetBytes("old low controlled bytes");
            var files = new EvidenceFileStore(evidenceRoot);
            var systemArtifact = await StoreArtifactAsync(files, oldSystemBytes, "system.docx", now);
            var highArtifact = await StoreArtifactAsync(files, oldHighBytes, "high.docx", now);
            var lowArtifact = await StoreArtifactAsync(files, oldLowBytes, "low.docx", now);
            var systemDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.SystemTestProcedures, "SYSTD-000723", "System procedures", 0, new string('b', 64), 1, now);
            var highDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.HighLevelTestCases, "HLRTD-000723", "High-level cases", 0, new string('c', 64), 1, now);
            var lowDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.LowLevelTestCases, "LLRTD-000723", "Low-level cases", 0, new string('d', 64), 1, now);
            var affectedNoArtifactDocument = new ControlledDocument(project.Id, release.Id, baseline.Id,
                ControlledDocumentType.HighLevelTestCases, "HLRTD-000724", "Affected on-demand cases", 0,
                new string('e', 64), 0, now);
            var unaffectedDocument = new ControlledDocument(project.Id, release.Id, unaffectedBaseline.Id,
                ControlledDocumentType.HighLevelTestCases, "HLRTD-000725", "Legacy unattributed cases", 0,
                new string('f', 64), 1, now);
            var systemStored = new ControlledDocumentArtifact(systemDocument.Id, "docx", systemArtifact.StorageKey, systemArtifact.OriginalFileName,
                systemArtifact.ContentType, systemArtifact.Size, systemArtifact.Sha256, now);
            var highStored = new ControlledDocumentArtifact(highDocument.Id, "docx", highArtifact.StorageKey, highArtifact.OriginalFileName,
                highArtifact.ContentType, highArtifact.Size, highArtifact.Sha256, now);
            var lowStored = new ControlledDocumentArtifact(lowDocument.Id, "docx", lowArtifact.StorageKey, lowArtifact.OriginalFileName,
                lowArtifact.ContentType, lowArtifact.Size, lowArtifact.Sha256, now);

            var oldSystemSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "TestChangeRequest", systemReview.Id, systemReview.DisplayNumber, "Approve", "old system package", systemCycle.SnapshotHash, "127.0.0.1", now);
            var oldHighSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "TestChangeRequest", highReview.Id, highReview.DisplayNumber, "Approve", "old high package", highCycle.SnapshotHash, "127.0.0.1", now,
                reviewStepId: highCycle.Steps.Single().Id, reviewCycle: highCycle.Sequence);
            var oldLowSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "TestChangeRequest", lowReview.Id, lowReview.DisplayNumber, "Approve", "old low package", lowCycle.SnapshotHash, "127.0.0.1", now,
                reviewStepId: lowCycle.Steps.Single().Id, reviewCycle: lowCycle.Sequence);
            var crossCycleSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "TestChangeRequest", highReview.Id, highReview.DisplayNumber, "Approve", "cross-cycle evidence", highCycle.SnapshotHash, "127.0.0.1", now,
                reviewStepId: systemCycle.Steps.Single().Id, reviewCycle: highCycle.Sequence);
            var oldArtifactSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "ControlledDocumentArtifact", highStored.Id, "HLRTD-000723.00/docx", "Approve", "old output", highStored.Sha256, "127.0.0.1", now);
            var oldDocumentSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "ControlledDocument", highDocument.Id, "HLRTD-000723.00", "Approve", "old document", highDocument.ContentHash, "127.0.0.1", now);
            var oldNoArtifactDocumentSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "ControlledDocument", affectedNoArtifactDocument.Id, "HLRTD-000724.00", "Approve", "old on-demand document", affectedNoArtifactDocument.ContentHash, "127.0.0.1", now);
            var mismatchedArtifactSignature = new ElectronicSignature(Guid.NewGuid(), "reviewer", "Reviewer", program.Id,
                "ControlledDocumentArtifact", highStored.Id, "HLRTD-000723.00/docx", "Approve", "mismatched old output", new string('0', 64), "127.0.0.1", now);

            var session = new ArtifactEditSession(project.Id, "TestChangeRequest", highReview.Id, highRevision.Id, oldManifestHash,
                "{\"artifactIdentity\":\"HLRTCR-000321\",\"prose\":\"HLRTCR-000321\"}", "qualification", now);
            var snapshot = new ArtifactDraftSnapshot(project.Id, session.Id, "TestChangeRequest", highReview.Id, 1,
                "{\"baseNumber\":[\"HLRTCR-000322\",{\"displayNumber\":\"LLRTCR-000326\",\"prose\":\"LLRTCR-000327\"}],\"search\":\"HLRTCR-000330\",\"number\":\"HLRTCR-000331\"}", oldManifestHash, "qualification", now);
            var malformedSnapshot = new ArtifactDraftSnapshot(project.Id, session.Id, "TestChangeRequest", highReview.Id, 2,
                "{\"baseNumber\":\"HLRTCR-000332\"", oldManifestHash, "qualification", now);
            var merge = new ArtifactMergeConflict(project.Id, highReview.Id, session.Id, session.Id,
                "{\"baseNumber\":\"HLRTCR-000323\"}", "{\"baseNumber\":\"HLRTCR-000324\"}", "{\"baseNumber\":\"HLRTCR-000325\"}", "qualification", now);
            var notification = new UserNotification(project.Id, "reviewer", "TestChangeRequest",
                "Review HLRTCR-000328", "Approve HLRTCR-000329", $"testChangeRequest:{highReview.Id}", highReview.Id, now);

            db.AddRange(program, project, release, sourceSystem, sourceHigh, sourceLow, sourceUnattributed, baseline,
                system, high, low, systemRevision, highRevision, lowRevision,
                systemReview, highReview, lowReview,
                unaffectedBaseline, unaffectedProcedure, unaffectedRevision,
                systemDocument, highDocument, lowDocument, affectedNoArtifactDocument, unaffectedDocument,
                systemStored, highStored, lowStored,
                oldSystemSignature, oldHighSignature, oldLowSignature, crossCycleSignature, oldArtifactSignature, oldDocumentSignature,
                oldNoArtifactDocumentSignature, mismatchedArtifactSignature,
                session, snapshot, malformedSnapshot, merge, notification,
                new BaselineTestProcedureSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineTestProcedureSelection(baseline.Id, low.Id, lowRevision.Id),
                new BaselineTestProcedureSelection(unaffectedBaseline.Id, unaffectedProcedure.Id, unaffectedRevision.Id),
                new IdentifierSequence("SYSTCR", 8), new IdentifierSequence("HLRTCR", 12), new IdentifierSequence("LLRTCR", 20));
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequirementsMaterializedAt, now)
                .SetProperty(x => x.TestProceduresMaterializedAt, now)
                .SetProperty(x => x.TestProceduresHash, oldManifestHash));
            await db.CandidateBaselines.Where(x => x.Id == unaffectedBaseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequirementsMaterializedAt, now)
                .SetProperty(x => x.TestProceduresMaterializedAt, now)
                .SetProperty(x => x.TestProceduresHash, oldManifestHash));
            db.ChangeTracker.Clear();

            await db.Database.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(new[] { "HLRTCCR-000005", "LLRTCCR-000006", "SYSTPCR-000004" },
                await db.TestChangeReviews.AsNoTracking().OrderBy(x => x.Discipline).Select(x => x.BaseNumber).ToArrayAsync());
            Assert.Equal(new[] { "HLRTC-000002", "HLRTC-000004", "LLRTC-000003", "SYSTP-000001" },
                await db.TestProcedures.AsNoTracking().OrderBy(x => x.BaseNumber).Select(x => x.BaseNumber).ToArrayAsync());
            using (var provenance = JsonDocument.Parse(await db.TestProcedureRevisions.AsNoTracking()
                       .Where(x => x.Id == highRevision.Id).Select(x => x.SourceChangeRequestsJson).SingleAsync()))
            {
                Assert.Equal("HLRTCCR-000123", provenance.RootElement.GetProperty("baseNumber").GetString());
                Assert.Equal("HLRTCR-000123", provenance.RootElement.GetProperty("prose").GetString());
            }
            Assert.Equal(new long[] { 333L, 457L, 9L }, await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "SYSTPCR" || x.Scope == "HLRTCCR" || x.Scope == "LLRTCCR")
                .OrderBy(x => x.Scope).Select(x => x.NextValue).ToArrayAsync());
            Assert.Equal(new long[] { 333L, 457L, 9L }, await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "HLRTCR" || x.Scope == "LLRTCR" || x.Scope == "SYSTCR")
                .OrderBy(x => x.Scope).Select(x => x.NextValue).ToArrayAsync());
            Assert.Equal("HLRTCCR-000321", JsonDocument.Parse(await db.ArtifactEditSessions.AsNoTracking().Where(x => x.Id == session.Id).Select(x => x.DraftJson).SingleAsync()).RootElement.GetProperty("artifactIdentity").GetString());
            var rewrittenSnapshot = await db.ArtifactDraftSnapshots.AsNoTracking()
                .Where(x => x.Id == snapshot.Id).Select(x => x.DraftJson).SingleAsync();
            using (var snapshotJson = JsonDocument.Parse(rewrittenSnapshot))
            {
                var baseNumber = snapshotJson.RootElement.GetProperty("baseNumber");
                Assert.Equal("HLRTCCR-000322", baseNumber[0].GetString());
                Assert.Equal("LLRTCCR-000326", baseNumber[1].GetProperty("displayNumber").GetString());
                Assert.Equal("LLRTCR-000327", baseNumber[1].GetProperty("prose").GetString());
                Assert.Equal("HLRTCR-000330", snapshotJson.RootElement.GetProperty("search").GetString());
                Assert.Equal("HLRTCR-000331", snapshotJson.RootElement.GetProperty("number").GetString());
            }
            Assert.Equal("{\"baseNumber\":\"HLRTCR-000332\"", await db.ArtifactDraftSnapshots.AsNoTracking()
                .Where(x => x.Id == malformedSnapshot.Id).Select(x => x.DraftJson).SingleAsync());
            Assert.Equal(new string('f', 64), await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == unaffectedDocument.Id).Select(x => x.ContentHash).SingleAsync());
            Assert.Equal("Review HLRTCCR-000328", await db.UserNotifications.AsNoTracking().Where(x => x.Id == notification.Id).Select(x => x.Title).SingleAsync());
            Assert.Equal(oldHighBytes, await ReadAsync(files, highStored.StorageKey, highStored.Size, highStored.Sha256));
            Assert.Equal(oldHighSignature.ContentHash, await db.ElectronicSignatures.AsNoTracking().Where(x => x.Id == oldHighSignature.Id).Select(x => x.ContentHash).SingleAsync());
            Assert.DoesNotContain(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(),
                x => x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                    && x.Target == $"ElectronicSignature:{crossCycleSignature.Id}");

            var authority = new TestChangeRequestPrefixMigrationAuthority(db,
                new ControlledOutputGenerator(db, new RichContentPublisher(db, files)), files);
            var oldNoArtifactDocumentHash = affectedNoArtifactDocument.ContentHash;
            var oldHighArtifactHash = highStored.Sha256;
            var oldHighArtifactStorageKey = highStored.StorageKey;
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.EnsureCompletedAsync());
            Assert.Contains("incomplete", failed.Message, StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            Assert.Empty(await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == TestChangeRequestPrefixMigrationAuthority.MigrationMarker + ".Completed")
                .ToListAsync());
            Assert.Equal(oldHighArtifactHash, await db.ControlledDocumentArtifacts.AsNoTracking()
                .Where(x => x.Id == highStored.Id).Select(x => x.Sha256).SingleAsync());
            Assert.Equal(oldHighArtifactStorageKey, await db.ControlledDocumentArtifacts.AsNoTracking()
                .Where(x => x.Id == highStored.Id).Select(x => x.StorageKey).SingleAsync());
            Assert.Equal(oldNoArtifactDocumentHash, await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == affectedNoArtifactDocument.Id).Select(x => x.ContentHash).SingleAsync());
            await db.ElectronicSignatures.Where(x => x.Id == mismatchedArtifactSignature.Id).ExecuteDeleteAsync();
            db.ChangeTracker.Clear();
            await authority.EnsureCompletedAsync();
            var completed = await db.SecurityAuditEvents.AsNoTracking().Where(x => x.EventType == TestChangeRequestPrefixMigrationAuthority.MigrationMarker + ".Completed").ToListAsync();
            Assert.Single(completed);
            var replacementCycleHash = await db.ReviewCycles.AsNoTracking().Where(x => x.Id == highCycle.Id).Select(x => x.SnapshotHash).SingleAsync();
            Assert.NotEqual(oldHighSignature.ContentHash, replacementCycleHash);
            Assert.Equal(oldHighSignature.ContentHash, await db.ElectronicSignatures.AsNoTracking().Where(x => x.Id == oldHighSignature.Id).Select(x => x.ContentHash).SingleAsync());
            Assert.Contains(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(), x => x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted" && x.Target == $"ElectronicSignature:{oldHighSignature.Id}");
            Assert.Contains(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(), x => x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted" && x.Target == $"ElectronicSignature:{oldSystemSignature.Id}");
            Assert.Equal(oldNoArtifactDocumentHash, await db.ElectronicSignatures.AsNoTracking()
                .Where(x => x.Id == oldNoArtifactDocumentSignature.Id).Select(x => x.ContentHash).SingleAsync());
            var noArtifactCompletion = await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                    && x.Target == $"ElectronicSignature:{oldNoArtifactDocumentSignature.Id}")
                .SingleAsync();
            var noArtifactReplacementHash = await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == affectedNoArtifactDocument.Id).Select(x => x.ContentHash).SingleAsync();
            using (var noArtifactCompletionJson = JsonDocument.Parse(noArtifactCompletion.Detail))
            {
                Assert.Equal(noArtifactReplacementHash, noArtifactCompletionJson.RootElement.GetProperty("newContentHash").GetString());
                Assert.Contains("content basis changed", noArtifactCompletionJson.RootElement.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("bytes were regenerated", noArtifactCompletionJson.RootElement.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(0, await db.ControlledDocumentArtifacts.AsNoTracking()
                .CountAsync(x => x.DocumentId == affectedNoArtifactDocument.Id));
            var replacementArtifact = await db.ControlledDocumentArtifacts.AsNoTracking().SingleAsync(x => x.Id == highStored.Id);
            Assert.NotEqual(highStored.Sha256, replacementArtifact.Sha256);
            var artifactCompletion = await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == "VerificationIdentityMigration.SignatureSupersessionCompleted"
                    && x.Target == $"ElectronicSignature:{oldArtifactSignature.Id}")
                .SingleAsync();
            using (var artifactCompletionJson = JsonDocument.Parse(artifactCompletion.Detail))
                Assert.Contains("stored controlled rendition bytes were regenerated", artifactCompletionJson.RootElement.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(oldHighSignature.ContentHash, replacementCycleHash);
            Assert.NotEqual(new string('e', 64), await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == affectedNoArtifactDocument.Id).Select(x => x.ContentHash).SingleAsync());
            Assert.Equal(new string('f', 64), await db.ControlledDocuments.AsNoTracking()
                .Where(x => x.Id == unaffectedDocument.Id).Select(x => x.ContentHash).SingleAsync());
            var noArtifactBasis = await db.SecurityAuditEvents.AsNoTracking()
                .Where(x => x.EventType == "VerificationIdentityMigration.DocumentContentBasisRewritten"
                    && x.Target == $"ControlledDocument:{affectedNoArtifactDocument.Id}")
                .SingleAsync();
            Assert.Contains("\"outputBytesRegenerated\":false", noArtifactBasis.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(await db.SecurityAuditEvents.AsNoTracking().ToListAsync(),
                x => x.EventType == "VerificationIdentityMigration.DocumentContentBasisRewritten"
                    && x.Target == $"ControlledDocument:{unaffectedDocument.Id}");

            var counts = await db.TestChangeReviews.CountAsync();
            await authority.EnsureCompletedAsync();
            Assert.Equal(1, await db.SecurityAuditEvents.CountAsync(x => x.EventType == TestChangeRequestPrefixMigrationAuthority.MigrationMarker + ".Completed"));
            Assert.Equal(counts, await db.TestChangeReviews.CountAsync());
            await db.Database.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(new long[] { 333L, 457L, 9L }, await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "SYSTPCR" || x.Scope == "HLRTCCR" || x.Scope == "LLRTCCR")
                .OrderBy(x => x.Scope).Select(x => x.NextValue).ToArrayAsync());
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
        }
    }

    private static SystemChangeRequest Source(ProjectRecord project, SoftwareRelease release, string number, DateTimeOffset now)
    {
        var source = new SystemChangeRequest(number, 0, project.Id, release.Id, "Source", "Problem", "Analysis", "Solution", "author", now,
            ChangeRequestType.System);
        source.AddRequirementChange("author", "SYSR-00001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall preserve the qualification behavior.",
            "Qualification source", "Test", now);
        source.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        source.ApproveActiveStage("reviewer", now.AddMinutes(1));
        return source;
    }

    private static TestProcedure Procedure(Guid projectId, string number, TestProcedureLevel level, DateTimeOffset now) =>
        new(projectId, number, number + " title", "author", now, level);

    private static TestProcedureRevision Revision(TestProcedure procedure, string objective, string provenance, DateTimeOffset now, Guid sourceTestChangeRequestId) =>
        new(procedure.Id, 0, objective, "preconditions", "steps", "expected", TestProcedureState.Approved,
            "author", now, sourceTestChangeRequestId: sourceTestChangeRequestId, sourceChangeRequestsJson: provenance);

    private static TestChangeReview Review(ProjectRecord project, SoftwareRelease release, SystemChangeRequest source,
        TestChangeReviewDiscipline discipline, string oldNumber, TestProcedure procedure, DateTimeOffset now)
    {
        var review = new TestChangeReview(project.Id, release.Id, source.Id, discipline, source.DisplayNumber, now, oldNumber, authorId: "author");
        review.RecordTestChangeRequired("author", now);
        review.WriteCase("author", "Identity-only rename", "Old identity", "Identity is changing", "Body is preserved", now);
        review.AddProcedureChange("author", new TestProcedureChangeDraft(procedure.BaseNumber, 0, procedure.Level,
            TestProcedureChangeKind.Modify, procedure.Title, "objective", "preconditions", "steps", "expected", "identity"), now);
        review.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], true, now);
        review.ApproveActiveStage("reviewer", "Approved", now.AddMinutes(1));
        return review;
    }

    private static async Task<StoredEvidence> StoreArtifactAsync(EvidenceFileStore files, byte[] bytes, string name, DateTimeOffset now)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await files.StoreAsync(stream, name, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", CancellationToken.None);
    }

    private static async Task<byte[]> ReadAsync(EvidenceFileStore files, string key, long size, string hash)
    {
        await using var stream = await files.OpenVerifiedReadAsync(key, size, hash, CancellationToken.None);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static DbContextOptions<AeroLinkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options;

    private static string QualificationConnectionOrThrow()
    {
        var connection = Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Issue #723 PostgreSQL qualification requires AEROLINK_MIGRATIONS_CONNECTION.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (!string.Equals(builder.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || builder.Port == 54329 || !string.Equals(builder.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue #723 qualification requires loopback, non-54329 PostgreSQL and database aerolink_723_qualify.");
        return connection;
    }

    private sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROLINK_MIGRATIONS_CONNECTION")))
                Skip = "Issue #723 PostgreSQL qualification skipped: set AEROLINK_MIGRATIONS_CONNECTION to the dedicated disposable database.";
        }
    }
}
