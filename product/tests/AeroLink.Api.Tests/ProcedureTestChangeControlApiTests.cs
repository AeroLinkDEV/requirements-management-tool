using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// API qualification for the software Procedure test-change package.  The Case packages used as origins are
/// deliberately seeded as already-approved controlled history: #727 owns creating the assessment and its
/// automatic Case-origin package, while #725 owns the exact-origin Procedure package lifecycle after that work
/// exists.
/// </summary>
public sealed class ProcedureTestChangeControlApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid HlrCaseChangeId, Guid LlrCaseChangeId);
    private sealed record ExplorerFixture(Guid ProjectId, Guid ReleaseId, Guid ArtifactId, Guid RevisionId,
        Guid LaterRevisionId, Guid RequirementRevisionId, Guid ReviewId, long ReviewVersion);

    [Fact]
    public async Task Verification_explorer_proposal_route_is_bound_to_the_test_change_api()
    {
        // Boundary characterization for the Explorer seam. A nonexistent release fails closed before any
        // artifact or TCR lookup; the route must remain an API route rather than becoming a client-only claim.
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory);
        await LoginAsync(client, "procedure.author");
        using var response = await client.PostAsJsonAsync(
            $"/api/verification-artifacts/{Guid.NewGuid()}/test-change-request-proposal", new
            {
                projectId = Guid.NewGuid(), releaseId = Guid.NewGuid(), artifactRevisionId = Guid.NewGuid(),
                testChangeReviewId = Guid.NewGuid(), expectedVersion = 1L,
            });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Verification_explorer_enforces_exact_effectivity_version_session_assignment_and_duplicate_rules()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedExplorerScenarioAsync(factory);
        await LoginAsync(client, "procedure.author");

        using var candidateResponse = await client.GetAsync(
            $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-candidates"
            + $"?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}&artifactRevisionId={fixture.RevisionId}");
        var candidateBody = await candidateResponse.Content.ReadAsStringAsync();
        Assert.True(candidateResponse.IsSuccessStatusCode, $"{(int)candidateResponse.StatusCode}: {candidateBody}");
        using var candidateDocument = JsonDocument.Parse(candidateBody);
        var candidates = candidateDocument.RootElement;
        var candidate = Assert.Single(candidates.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == fixture.ReviewId);
        Assert.True(candidate.GetProperty("eligible").GetBoolean());
        Assert.Equal("System:Procedure", candidate.GetProperty("artifactKey").GetString());
        Assert.Equal(fixture.ReviewVersion, candidate.GetProperty("version").GetInt64());

        // The mutation requires the candidate version; an omitted/default token cannot bypass concurrency.
        using (var missingVersion = await client.PostAsJsonAsync(
                   $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-proposal", new
                   {
                       projectId = fixture.ProjectId, releaseId = fixture.ReleaseId,
                       artifactRevisionId = fixture.RevisionId, testChangeReviewId = fixture.ReviewId,
                       expectedVersion = 0L,
                   }))
        {
            Assert.Equal(HttpStatusCode.Conflict, missingVersion.StatusCode);
            Assert.Contains("stale_version", await missingVersion.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        // A different revision row is not interchangeable with the exact revision carried by the build.
        using (var wrongRevision = await client.PostAsJsonAsync(
                   $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-proposal", new
                   {
                       projectId = fixture.ProjectId, releaseId = fixture.ReleaseId,
                       artifactRevisionId = fixture.LaterRevisionId, testChangeReviewId = fixture.ReviewId,
                       expectedVersion = fixture.ReviewVersion,
                   }))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongRevision.StatusCode);
            Assert.Contains("artifact_revision_not_current", await wrongRevision.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        // An active whole-draft controlled-edit session is refused even to its owner: direct AddProcedureChange
        // cannot update that session's snapshot and would be erased by a later check-in.
        await AddActiveReviewEditSessionAsync(factory, fixture, "procedure.author");
        using (var checkedOut = await client.PostAsJsonAsync(
                   $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-proposal", new
                   {
                       projectId = fixture.ProjectId, releaseId = fixture.ReleaseId,
                       artifactRevisionId = fixture.RevisionId, testChangeReviewId = fixture.ReviewId,
                       expectedVersion = fixture.ReviewVersion,
                   }))
        {
            Assert.Equal(HttpStatusCode.Conflict, checkedOut.StatusCode);
            Assert.Contains("active_edit_session", await checkedOut.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
        await CloseReviewEditSessionAsync(factory, fixture.ReviewId);

        using (var authored = await client.PostAsJsonAsync(
                   $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-proposal", new
                   {
                       projectId = fixture.ProjectId, releaseId = fixture.ReleaseId,
                       artifactRevisionId = fixture.RevisionId, testChangeReviewId = fixture.ReviewId,
                       expectedVersion = fixture.ReviewVersion,
                       rationale = "Modify the exact System Procedure selected in the explorer.",
                   }))
        {
            var body = await authored.Content.ReadAsStringAsync();
            Assert.True(authored.StatusCode == HttpStatusCode.OK, $"{(int)authored.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.GetProperty("duplicate").GetBoolean());
            Assert.Equal(fixture.ReviewId, json.RootElement.GetProperty("testChangeReviewId").GetGuid());
        }

        long currentVersion;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.AsNoTracking().Include(x => x.ProcedureChanges)
                .SingleAsync(x => x.Id == fixture.ReviewId);
            currentVersion = review.Version;
            Assert.Single(review.ProcedureChanges);
            Assert.Equal(TestProcedureChangeKind.Modify, review.ProcedureChanges.Single().Kind);
            Assert.Equal("SYSTP-786001", review.ProcedureChanges.Single().BaseNumber);
            Assert.Equal(1, review.ProcedureChanges.Single().Revision);
            Assert.Equal(fixture.RevisionId, await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.Id == fixture.RevisionId).Select(x => x.Id).SingleAsync());
        }

        // Repeating the same selection opens the already-created proposal instead of creating a second one.
        using (var duplicate = await client.PostAsJsonAsync(
                   $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-proposal", new
                   {
                       projectId = fixture.ProjectId, releaseId = fixture.ReleaseId,
                       artifactRevisionId = fixture.RevisionId, testChangeReviewId = fixture.ReviewId,
                       expectedVersion = currentVersion,
                   }))
        {
            var body = await duplicate.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("duplicate").GetBoolean());
        }

        // The candidate list explains a Draft held by another assigned work-holder rather than offering it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var assigned = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.ReviewId);
            assigned.Assign("procedure.author", "another.engineer", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using var reassignedResponse = await client.GetAsync(
            $"/api/verification-artifacts/{fixture.ArtifactId}/test-change-request-candidates"
            + $"?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}&artifactRevisionId={fixture.RevisionId}");
        var reassignedBody = await reassignedResponse.Content.ReadAsStringAsync();
        Assert.True(reassignedResponse.IsSuccessStatusCode, $"{(int)reassignedResponse.StatusCode}: {reassignedBody}");
        using var reassignedDocument = JsonDocument.Parse(reassignedBody);
        var reassignedCandidates = reassignedDocument.RootElement;
        var reassigned = Assert.Single(reassignedCandidates.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == fixture.ReviewId);
        Assert.False(reassigned.GetProperty("eligible").GetBoolean());
        Assert.Equal("assigned_work_holder", reassigned.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task A_valid_case_origin_uses_the_procedure_key_workflow_and_preserves_origin_through_revision()
    {
        // #726 activated the software Procedure tier: the public activation gate now accepts the
        // Procedure-enabled profile, and software Procedure packages become available on the production host.
        using (var guardedFactory = new AeroLinkApiFactory())
        {
            using var guardedClient = guardedFactory.CreateClient();
            var guardedFixture = await SeedAsync(guardedFactory);
            await AuthorSoftwareProcedureProfileDraftAsync(guardedClient, guardedFixture.ProjectId);
            using var activation = await guardedClient.PostAsJsonAsync(
                $"/api/projects/{guardedFixture.ProjectId}/configuration/activate", new
                {
                    expectedVersion = 2,
                    reason = "Activate the #726 software Procedure tier from the public API.",
                });
            var activationBody = await activation.Content.ReadAsStringAsync();
            Assert.True(activation.StatusCode == HttpStatusCode.OK,
                $"{(int)activation.StatusCode}: {activationBody}");
            guardedFixture = await SeedCaseSourcesAsync(guardedFactory, guardedFixture);
            using (var stateScope = guardedFactory.Services.CreateScope())
            {
                var stateDb = stateScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var state = await stateDb.ProjectLadderConfigurations.AsNoTracking()
                    .SingleAsync(x => x.ProjectId == guardedFixture.ProjectId);
                Assert.Equal(ProjectLadderConfigurationState.Active, state.State);
                Assert.NotNull(state.ActivationManifestVersion);
                Assert.NotNull(state.ActivationManifestHash);
            }

            await LoginAsync(guardedClient, "procedure.author");
            using var procedurePackage = await guardedClient.PostAsJsonAsync(
                $"/api/releases/{guardedFixture.ReleaseId}/test-change-requests", new
                {
                    discipline = "HighLevelSoftware",
                    artifactKind = "Procedure",
                    caseChangeIds = new[] { guardedFixture.HlrCaseChangeId },
                    title = "HLR Procedure package from the activated production profile",
                });
            Assert.True(procedurePackage.StatusCode == HttpStatusCode.Created,
                $"{(int)procedurePackage.StatusCode}: {await procedurePackage.Content.ReadAsStringAsync()}");
        }

        // This test-only resolver represents a future governed profile solely inside this disposable API host.
        // It does not call or bypass the production activation endpoint and writes no activation evidence.
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await CreateProcedureWorkflowAsync(client, fixture.ProjectId);
        fixture = await SeedCaseSourcesAsync(factory, fixture);

        await LoginAsync(client, "procedure.author");
        var hlr = await CreateProcedurePackageAsync(client, fixture.ReleaseId, "HighLevelSoftware",
            fixture.HlrCaseChangeId, "HLR Procedure package");
        Assert.StartsWith("HLRTPCR-", hlr.GetProperty("displayNumber").GetString(), StringComparison.Ordinal);
        Assert.Equal("Procedure", hlr.GetProperty("artifactKind").GetString());
        Assert.Equal("CaseChange", hlr.GetProperty("originKind").GetString());
        Assert.Equal(fixture.HlrCaseChangeId, hlr.GetProperty("originReferenceId").GetGuid());
        Assert.Equal("HLRTC-725001.00", hlr.GetProperty("originDisplayIdentity").GetString());

        var llr = await CreateProcedurePackageAsync(client, fixture.ReleaseId, "LowLevelSoftware",
            fixture.LlrCaseChangeId, "LLR Procedure package");
        Assert.StartsWith("LLRTPCR-", llr.GetProperty("displayNumber").GetString(), StringComparison.Ordinal);
        Assert.Equal("Procedure", llr.GetProperty("artifactKind").GetString());
        Assert.Equal("CaseChange", llr.GetProperty("originKind").GetString());
        Assert.Equal(fixture.LlrCaseChangeId, llr.GetProperty("originReferenceId").GetGuid());
        Assert.Equal("LLRTC-725001.00", llr.GetProperty("originDisplayIdentity").GetString());

        // The source discriminator is exact: an LLR Case change cannot raise an HLR Procedure package.
        using (var wrongDiscipline = await client.PostAsJsonAsync(
                   $"/api/releases/{fixture.ReleaseId}/test-change-requests", new
                   {
                       discipline = "HighLevelSoftware",
                       artifactKind = "Procedure",
                       caseChangeIds = new[] { fixture.LlrCaseChangeId },
                       title = "Wrong source discipline",
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongDiscipline.StatusCode);
            Assert.Contains("exact software Case change", await wrongDiscipline.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        var hlrId = hlr.GetProperty("id").GetGuid();
        using (var authored = await client.PostAsJsonAsync($"/api/test-change-reviews/{hlrId}/procedure-changes",
                   new
                   {
                       kind = "Introduce",
                       revision = 0,
                       title = "Introduce the HLR Procedure",
                       objective = "Verify the changed HLR behavior.",
                       preconditions = "The configured software build is available.",
                       steps = "Exercise the controlled behavior.",
                       expectedResult = "The expected behavior is observed.",
                        rationale = "The exact Case change requires Procedure coverage.",
                        parentKind = "Derived",
                        parentRevisionIds = Array.Empty<Guid>(),
                        derivedRationale = "This introductory Procedure is standalone until a Case revision is selected.",
                        environmentSetup = "The configured software build is available.",
                        testData = "Controlled scenario data.",
                        orderedSteps = "Exercise the controlled behavior and record observations.",
                        expectedObservations = "The expected behavior is observed.",
                        cleanup = "Restore the controlled fixture.",
                        toolingAutomation = "Qualified verification runner.",
                    }))
        {
            Assert.True(authored.IsSuccessStatusCode, await authored.Content.ReadAsStringAsync());
        }

        using (var caseWritten = await client.PostAsJsonAsync($"/api/test-change-reviews/{hlrId}/case", new
                   {
                       title = "HLR Procedure change case",
                       problem = "The approved Case change requires a new Procedure.",
                       analysis = "The Procedure package is governed independently from its exact Case origin.",
                       solution = "Introduce and approve the standalone Procedure.",
                   }))
        {
            Assert.True(caseWritten.IsSuccessStatusCode, await caseWritten.Content.ReadAsStringAsync());
        }

        using (var submitted = await client.PostAsJsonAsync($"/api/test-change-reviews/{hlrId}/submit", new
                   {
                       approvers = new[] { new { userId = "procedure.reviewer" } },
                   }))
        {
            var body = await submitted.Content.ReadAsStringAsync();
            Assert.True(submitted.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("InReview", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("stageCount").GetInt32());
        }

        await LoginAsync(client, "procedure.reviewer");
        using (var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{hlrId}/approve", new
                   {
                       password = AeroLinkApiFactory.MemberPassword,
                       rationale = "The Procedure package is complete and traceable to the exact Case change.",
                       meaning = "I approve this exact software Procedure change-control package.",
                   }))
        {
            var body = await approved.Content.ReadAsStringAsync();
            Assert.True(approved.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Approved", json.RootElement.GetProperty("state").GetString());
        }

        // Advance the exact source Case package first. Its original revision becomes Superseded, but that
        // historical approved origin must remain valid when the dependent Procedure package is revised.
        var sourceCaseReviewId = await SourceCaseReviewIdAsync(factory, fixture.HlrCaseChangeId);
        await LoginAsync(client, "case.author");
        using (var sourceRevision = await client.PostAsJsonAsync($"/api/test-change-reviews/{sourceCaseReviewId}/revise", new { }))
        {
            var sourceBody = await sourceRevision.Content.ReadAsStringAsync();
            Assert.True(sourceRevision.IsSuccessStatusCode, sourceBody);
            using var sourceJson = JsonDocument.Parse(sourceBody);
            Assert.Equal(1, sourceJson.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal("Draft", sourceJson.RootElement.GetProperty("state").GetString());
        }

        await LoginAsync(client, "procedure.author");
        using var revisedResponse = await client.PostAsJsonAsync($"/api/test-change-reviews/{hlrId}/revise", new { });
        var revisedBody = await revisedResponse.Content.ReadAsStringAsync();
        Assert.True(revisedResponse.IsSuccessStatusCode, revisedBody);
        using var revisedJson = JsonDocument.Parse(revisedBody);
        var revisedId = revisedJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1, revisedJson.RootElement.GetProperty("revision").GetInt32());

        var register = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var successor = register.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == revisedId);
        Assert.Equal("Draft", successor.GetProperty("state").GetString());
        Assert.Equal("Procedure", successor.GetProperty("artifactKind").GetString());
        Assert.Equal("CaseChange", successor.GetProperty("originKind").GetString());
        Assert.Equal(fixture.HlrCaseChangeId, successor.GetProperty("originReferenceId").GetGuid());
        Assert.Equal("HLRTC-725001.00", successor.GetProperty("originDisplayIdentity").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var history = await db.TestChangeReviews.AsNoTracking()
            .Where(x => x.Id == hlrId || x.Id == revisedId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(TestChangeReviewState.Superseded, history[0].State);
        Assert.Equal(TestChangeReviewState.Draft, history[1].State);
        Assert.All(history, x =>
        {
            Assert.Equal(TestChangeReviewOriginKind.CaseChange, x.OriginKind);
            Assert.Equal(fixture.HlrCaseChangeId, x.OriginReferenceId);
            Assert.Equal("HLRTC-725001.00", x.SourceCaseOriginNumber);
        });
    }

    [Fact]
    public async Task A_referenced_case_assessment_cannot_be_reopened_through_the_api()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var now = DateTimeOffset.UtcNow;
        Guid assessmentId;
        var sourceChange = new SystemChangeRequest("HLRCR-725002", 0, fixture.ProjectId, fixture.ReleaseId,
            "Assessment source change", "Problem", "Analysis", "Solution", "case.author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var caseKey = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case);
        var caseReview = new TestChangeReview(fixture.ProjectId, fixture.ReleaseId, sourceChange.Id, caseKey,
            "HLRCR-725002.00", now, baseNumber: "HLRTCCR-725002", authorId: "case.author");
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(sourceChange, caseReview);
            await db.SaveChangesAsync();

            var item = VerificationImpactItem.ForIntroducedRequirement(fixture.ProjectId, fixture.ReleaseId,
                sourceChange.Id, caseReview.Id, Guid.NewGuid(), "HLRR-ASSESS-725.00", "Test", now);
            item.LinkRequirementRevision(Guid.NewGuid(), now);
            item.AssignToEngineer("case.author", "procedure.author", now);
            item.Resolve("procedure.author", VerificationImpactOutcome.NewProcedureRequired,
                "The exact Case assessment requires a new Procedure.", now);
            assessmentId = item.Id;
            db.Add(item);
            await db.SaveChangesAsync();

            var procedureKey = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                VerificationArtifactKind.Procedure);
            var procedurePackage = TestChangeReview.FromCaseAssessment(fixture.ProjectId, fixture.ReleaseId,
                item.Id, procedureKey, item.SubjectDisplayNumber, now,
                baseNumber: "HLRTPCR-ASSESS-725", revision: 1, authorId: "procedure.author");
            db.Add(procedurePackage);
            await db.SaveChangesAsync();
        }

        await LoginAsync(client, "procedure.author");
        using var reopen = await client.PostAsJsonAsync($"/api/verification-impact/{assessmentId}/reopen", new
        {
            rationale = "Attempt to reopen the assessment after Procedure issuance.",
        });
        var reopenBody = await reopen.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
        Assert.Contains("immutable_case_assessment_origin", reopenBody, StringComparison.OrdinalIgnoreCase);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(VerificationImpactState.Resolved,
            (await verifyDb.VerificationImpactItems.AsNoTracking().SingleAsync(x => x.Id == assessmentId)).State);
    }

    private static async Task<ExplorerFixture> SeedExplorerScenarioAsync(AeroLinkApiFactory factory)
    {
        var fixture = await SeedAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = await db.Projects.SingleAsync(x => x.Id == fixture.ProjectId);
        var release = await db.Releases.SingleAsync(x => x.Id == fixture.ReleaseId);
        var source = new SystemChangeRequest("SRCR-786001", 0, project.Id, release.Id,
            "Explorer source", "Problem", "Analysis", "Solution", "procedure.author", now);
        source.AddRequirementChange(actorId: "procedure.author", baseNumber: "SYSR-786001", revision: 0,
            level: RequirementLevel.System, kind: RequirementChangeKind.Introduce,
            statement: "The system shall preserve exact verification identity.", rationale: "Explorer qualification",
            verificationMethod: "Test", now: now);
        source.SubmitForReview("procedure.author", [new ApproverSelection("procedure.reviewer", "Reviewer")], now);
        source.ApproveActiveStage("procedure.reviewer", now);

        var baseline = new CandidateBaseline("SW-78.60", 0, project.Id, release.Id, null,
            "Explorer qualification build", "procedure.author", now);
        baseline.Select(source, "procedure.author", now);
        baseline.Freeze("procedure.author", now);
        baseline.MarkRequirementsMaterialized("procedure.author", new string('a', 64), 1, now);
        baseline.MarkTestProceduresMaterialized("procedure.author", new string('b', 64), 1, now);

        var requirement = new RequirementArtifact(project.Id, "SYSR-786001", RequirementLevel.System, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The system shall preserve exact verification identity.", "Explorer qualification", "Test",
            RequirementRevisionState.Active, source.Id, baseline.Id, now);
        var artifact = new TestProcedure(project.Id, "SYSTP-786001", "Explorer exact System Procedure",
            "procedure.author", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(artifact.Id, 0, "Verify exact identity", "Use the qualified build",
            "Exercise the exact selected behavior", "The exact behavior is observed.", TestProcedureState.Approved,
            "procedure.author", now, effectiveBaselineId: baseline.Id,
            parentKind: VerificationProcedureParentKind.Allocated);
        var laterRevision = new TestProcedureRevision(artifact.Id, 1, "Later identity", "Use the later build",
            "Exercise the later behavior", "The later behavior is observed.", TestProcedureState.Draft,
            "procedure.author", now.AddSeconds(1));
        var key = new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure);
        var review = new TestChangeReview(project.Id, release.Id, source.Id, key, source.DisplayNumber, now,
            "SYSTPCR-786001", authorId: "procedure.author");
        review.RecordTestChangeRequired("procedure.author", now);
        var impact = VerificationImpactItem.ForIntroducedRequirement(project.Id, release.Id, source.Id, review.Id,
            source.RequirementChanges.Single().Id, source.RequirementChanges.Single().DisplayNumber, "Test", now);
        impact.LinkRequirementRevision(requirementRevision.Id, now);

        db.AddRange(source, baseline, requirement, requirementRevision, artifact, revision, laterRevision, review, impact);
        db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, requirement.Id,
            requirementRevision.Id));
        db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, artifact.Id, revision.Id));
        db.TestCoverage.Add(new TestRequirementCoverage(revision.Id, requirementRevision.Id));
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, artifact.Id, revision.Id, laterRevision.Id, requirementRevision.Id,
            review.Id, review.Version);
    }

    private static async Task AddActiveReviewEditSessionAsync(AeroLinkApiFactory factory, ExplorerFixture fixture,
        string actor)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var snapshot = "{}";
        var hash = EnterpriseRequirementsService.Hash(System.Text.Encoding.UTF8.GetBytes(snapshot));
        db.ArtifactEditSessions.Add(new ArtifactEditSession(fixture.ProjectId, "TestChangeRequest",
            fixture.ReviewId, null, hash, snapshot, actor, now, true, 15));
        await db.SaveChangesAsync();
    }

    private static async Task CloseReviewEditSessionAsync(AeroLinkApiFactory factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var session = await db.ArtifactEditSessions.SingleAsync(x => x.ArtifactId == reviewId
            && x.ArtifactType == "TestChangeRequest" && x.State == EditSessionState.Active);
        session.Close(EditSessionState.Committed, session.Version, DateTimeOffset.UtcNow, "procedure.author",
            "Explorer API qualification");
        await db.SaveChangesAsync();
    }

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Procedure Package API Program", "PPA");
        var project = new ProjectRecord(program.Id, "Software", "Procedure Package API Project");
        var release = new SoftwareRelease(project.Id, "7.25", false);
        db.AddRange(program, project, release);

        foreach (var (name, role) in new[]
                 {
                     ("procedure.author", ProgramRole.TestEngineer),
                     ("procedure.config", ProgramRole.ConfigurationManager),
                     ("procedure.reviewer", ProgramRole.Reviewer),
                     ("case.author", ProgramRole.TestEngineer),
                 })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "issue-725-test", now));
        }

        await db.SaveChangesAsync();
        return new(project.Id, release.Id, Guid.Empty, Guid.Empty);
    }

    private static async Task<Fixture> SeedCaseSourcesAsync(AeroLinkApiFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var highSource = new SystemChangeRequest("HLRCR-725001", 0, fixture.ProjectId, fixture.ReleaseId,
            "Approved HLR Case source", "P", "A", "S", "case.author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        var lowSource = new SystemChangeRequest("LLRCR-725001", 0, fixture.ProjectId, fixture.ReleaseId,
            "Approved LLR Case source", "P", "A", "S", "case.author", now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
        db.AddRange(highSource, lowSource);
        await db.SaveChangesAsync();

        var highCase = ApprovedCasePackage(fixture.ProjectId, fixture.ReleaseId, highSource.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            TestProcedureLevel.HighLevel, "HLRTCCR-725001", "HLRTC-725001");
        var lowCase = ApprovedCasePackage(fixture.ProjectId, fixture.ReleaseId, lowSource.Id,
            new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case),
            TestProcedureLevel.LowLevel, "LLRTCCR-725001", "LLRTC-725001");
        db.AddRange(highCase, lowCase);
        await db.SaveChangesAsync();
        return fixture with
        {
            HlrCaseChangeId = highCase.ProcedureChanges.Single().Id,
            LlrCaseChangeId = lowCase.ProcedureChanges.Single().Id,
        };
    }

    private static TestChangeReview ApprovedCasePackage(Guid projectId, Guid releaseId, Guid sourceChangeId,
        VerificationArtifactKey key, TestProcedureLevel level, string packageNumber, string changeNumber)
    {
        var now = DateTimeOffset.UtcNow;
        var review = new TestChangeReview(projectId, releaseId, sourceChangeId,
            key, key.Discipline == VerificationDiscipline.HighLevelSoftware ? "HLRCR-725001.00" : "LLRCR-725001.00",
            now, packageNumber, authorId: "case.author");
        review.RecordTestChangeRequired("case.author", now);
        review.WriteCase("case.author", "Approved Case source package", "The source Case changed.",
            "The source Case change is complete.", "The source Case package is approved.", now);
        review.AddProcedureChange("case.author", new TestProcedureChangeDraft(changeNumber, 0, level,
            TestProcedureChangeKind.Introduce, "Approved Case change", "Verify the changed Case behavior.",
            "The software build is available.", "Exercise the Case.", "The expected Case behavior is observed.",
            "The exact Case change is the source of the Procedure package.", ParentKind: VerificationProcedureParentKind.Derived,
            DerivedRationale: "This seeded Case source is a standalone controlled artifact for API qualification."), now);
        review.SubmitForReview("case.author", [new ApproverSelection("case.approver", "Case Approver")],
            everyItemResolved: true, now: now);
        review.ApproveActiveStage("case.approver", "Approved exact Case source.", now);
        return review;
    }

    private static async Task<JsonElement> CreateProcedurePackageAsync(HttpClient client, Guid releaseId,
        string discipline, Guid sourceChangeId, string title)
    {
        using var response = await client.PostAsJsonAsync($"/api/releases/{releaseId}/test-change-requests", new
        {
            discipline,
            artifactKind = "Procedure",
            caseChangeIds = new[] { sourceChangeId },
            title,
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        using var created = JsonDocument.Parse(body);
        var id = created.RootElement.GetProperty("id").GetGuid();
        var register = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{releaseId}/test-change-reviews");
        return register.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == id);
    }

    private static async Task AuthorSoftwareProcedureProfileDraftAsync(HttpClient client, Guid projectId)
    {
        await LoginAsync(client, "procedure.config");
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/configuration");
        var steps = current.GetProperty("effectiveSteps").EnumerateArray().Select(step =>
        {
            var entry = step.GetProperty("catalogueEntry").GetString()!;
            var kinds = entry is "HighLevel" or "LowLevel"
                ? new[] { "Case", "Procedure" }
                : step.GetProperty("enabledArtifactKinds").EnumerateArray()
                    .Select(x => x.GetString()!).ToArray();
            return new
            {
                catalogueEntry = entry,
                position = step.GetProperty("position").GetInt32(),
                capabilities = step.GetProperty("capabilities").ValueKind == JsonValueKind.Number
                    ? (LevelCapabilities)step.GetProperty("capabilities").GetInt32()
                    : Enum.Parse<LevelCapabilities>(step.GetProperty("capabilities").GetString()!, ignoreCase: true),
                enabledArtifactKinds = kinds,
            };
        }).ToArray();
        var relationships = current.GetProperty("relationships").EnumerateArray().Select(edge => new
        {
            parent = edge.GetProperty("parent").GetString(),
            child = edge.GetProperty("child").GetString(),
        }).ToArray();
        using var edit = await client.PutAsJsonAsync($"/api/projects/{projectId}/configuration", new
        {
            expectedVersion = current.GetProperty("version").GetInt64(),
            reason = "Enable the typed v2 software Procedure profile for issue 725 API qualification.",
            steps,
            relationships,
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
    }

    private static async Task CreateProcedureWorkflowAsync(HttpClient client, Guid projectId)
    {
        await LoginAsync(client, "procedure.config");
        using var created = await client.PostAsJsonAsync("/api/review-workflows", new
        {
            projectId,
            name = "Software Procedure API review",
            appliesTo = "HighLevelSoftwareProcedure",
            mode = "Sequential",
            stages = new[] { new { name = "Procedure reviewer", requiredRole = "Reviewer" } },
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, body);
        var id = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetGuid();
        using var activated = await client.PostAsJsonAsync($"/api/review-workflows/{id}/activate", new { });
        Assert.True(activated.IsSuccessStatusCode, await activated.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> SourceCaseReviewIdAsync(AeroLinkApiFactory factory, Guid procedureChangeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.Set<TestProcedureChange>().AsNoTracking()
            .Where(x => x.Id == procedureChangeId)
            .Select(x => x.TestChangeReviewId)
            .SingleAsync();
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = user,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.True(login.StatusCode == HttpStatusCode.OK,
            $"Login for {user} failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
