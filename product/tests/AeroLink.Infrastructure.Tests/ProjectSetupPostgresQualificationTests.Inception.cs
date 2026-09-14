using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

public sealed partial class ProjectSetupPostgresQualificationTests
{
    [DisposablePostgresFact]
    public async Task Inception_uploads_survive_provider_restart_and_replay_for_all_external_formats_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            var password = "PG-Inception!2026";
            var now = DateTimeOffset.UtcNow;
            using var provider = SetupProvider(connection);
            Guid accountId;
            using (var seedScope = provider.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var account = new UserAccount("pg-inception-owner", "PG Inception Owner",
                    "pg-inception-owner@example.test", IdentityService.HashPassword(password), now);
                db.Add(account);
                await db.SaveChangesAsync();
                accountId = account.Id;
            }
            var actor = new AuthenticatedUser(accountId, "pg-inception-owner", "PG Inception Owner",
                "pg-inception-owner@example.test", true, []);

            foreach (var fileName in new[] { "inception.csv", "inception.xlsx", "inception.reqif" })
            {
                await using var createScope = provider.CreateAsyncScope();
                var service = createScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                var inception = createScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                var draft = await service.CreateAsync(actor, $"PG {fileName}", CancellationToken.None);
                draft = await PrepareExternalDraftAsync(service, draft, actor, fileName);
                var bytes = ExternalSourceBytes(fileName);
                var uploaded = await inception.UploadAsync(draft.Id, actor, draft.Version, fileName,
                    new MemoryStream(bytes, writable: false), CancellationToken.None);

                // A new service provider is an application restart. The upload and parser observation must be
                // available from the durable package without replaying the upload or relying on this tracker.
                await using (var restartScope = provider.CreateAsyncScope())
                {
                    var restartedInception = restartScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                    var source = await restartedInception.ReadSourceAsync(draft.Id, actor, CancellationToken.None);
                    Assert.NotNull(source);
                    Assert.Equal(uploaded.Package.Id, source!.Id);
                    Assert.Equal(uploaded.Package.Sha256, source.Sha256);
                    Assert.Equal(fileName.EndsWith(".reqif", StringComparison.Ordinal) ? 2 : 1,
                        source.Modules.SelectMany(x => x.Objects!).Count());
                }

                await using var configureScope = provider.CreateAsyncScope();
                var configureInception = configureScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                var durableSource = await configureInception.ReadSourceAsync(draft.Id, actor, CancellationToken.None);
                Assert.NotNull(durableSource);
                var sourceVersion = uploaded.DraftVersion;
                var categories = durableSource!.Relations.Count > 0 ? "[\"Requirements\",\"Traces\"]" : "[\"Requirements\"]";
                if (fileName.EndsWith(".reqif", StringComparison.Ordinal))
                {
                    var missingParent = JsonNode.Parse(MappingJson(durableSource))!;
                    var excluded = missingParent["relations"]![0]!;
                    excluded["include"] = false;
                    excluded["exclusionReason"] = "Deliberately omit the required parent for gate qualification.";
                    var rejectedMapping = await configureInception.SaveConfigurationAsync(draft.Id, actor,
                        new InceptionConfigurationCommand(sourceVersion, categories, missingParent.ToJsonString(), "{}"),
                        CancellationToken.None);
                    sourceVersion = rejectedMapping.DraftVersion;
                    Assert.NotEqual(ProjectSetupSourceStage.Reconciled, rejectedMapping.Package.Stage);
                    await using var rejectedScope = provider.CreateAsyncScope();
                    var finalizer = rejectedScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                    await Assert.ThrowsAsync<ProjectSetupInvalidException>(() => finalizer.FinalizeAsync(draft.Id, actor,
                        sourceVersion, "missing-parent-must-not-complete", CancellationToken.None, password, "", true));
                    Assert.False(await configureScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>()
                        .Projects.AnyAsync(x => x.Id == draft.ProjectId));
                }
                var configured = await configureInception.SaveConfigurationAsync(draft.Id, actor,
                        new InceptionConfigurationCommand(sourceVersion, categories,
                        MappingJson(durableSource!), "{}"), CancellationToken.None);
                if (configured.Package.Stage != ProjectSetupSourceStage.Reconciled)
                    throw new Xunit.Sdk.XunitException($"{fileName}: source mapping did not reconcile.");
                Assert.Equal(ProjectSetupSourceStage.Reconciled, configured.Package.Stage);

                // The assertion is reconstructed by the server after another restart, then finalization is
                // replayed from the original client token to model a response lost after commit.
                await using (var finalScope = provider.CreateAsyncScope())
                {
                    var finalInception = finalScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                    var finalSource = await finalInception.ReadSourceAsync(draft.Id, actor, CancellationToken.None);
                    Assert.NotNull(finalSource?.Assertion);
                    var finalService = finalScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                    var result = await finalService.FinalizeAsync(draft.Id, actor, configured.DraftVersion,
                        $"pg-{fileName}", CancellationToken.None, password, finalSource!.Assertion!.Hash, true);
                    Assert.False(result.AlreadyCompleted);
                    Assert.Equal("SW-01.30", result.OfficialBuildName);
                }
                await using (var replayScope = provider.CreateAsyncScope())
                {
                    var replay = await replayScope.ServiceProvider.GetRequiredService<ProjectSetupService>()
                        .FinalizeAsync(draft.Id, actor, configured.DraftVersion, $"pg-{fileName}-retry",
                            CancellationToken.None);
                    Assert.True(replay.AlreadyCompleted);
                    Assert.Equal(draft.ProjectId, replay.ProjectId);
                    var db = replayScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                    Assert.Single(await db.BaselineImports.AsNoTracking().Where(x => x.ProjectId == replay.ProjectId).ToListAsync());
                    Assert.Equal(fileName.EndsWith(".reqif", StringComparison.Ordinal) ? 3 : 1,
                        await db.ProjectInceptionSourceRecords.CountAsync(x => x.ProjectId == replay.ProjectId));
                    if (fileName.EndsWith(".reqif", StringComparison.Ordinal))
                        await AssertInheritedHierarchyAsync(db, replay.ProjectId, RequirementRevisionOriginKind.ExternalSourcePackage);
                    Assert.Empty(await db.TestExecutions.AsNoTracking().Where(x => x.SoftwareBuildId != null
                        && db.SoftwareBuilds.Any(build => build.Id == x.SoftwareBuildId && build.ProjectId == replay.ProjectId)).ToListAsync());
                }
            }
        });
    }

    [DisposablePostgresFact]
    public async Task Native_capture_survives_restart_and_competing_finalization_on_postgresql()
    {
        await WithSetupDatabaseAsync(async connection =>
        {
            var password = "PG-Native-Inception!2026";
            var now = DateTimeOffset.UtcNow;
            using var provider = SetupProvider(connection);
            Guid accountId;
            Guid sourceBaselineId;
            using (var seedScope = provider.CreateScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var account = new UserAccount("pg-native-owner", "PG Native Owner", "pg-native-owner@example.test",
                    IdentityService.HashPassword(password), now);
                var program = new ProgramRecord("PG Native source program", "PGNSP");
                var project = new ProjectRecord(program.Id, "PG Native source", "PG Native product");
                var release = new SoftwareRelease(project.Id, "1.0", false);
                var baseline = new CandidateBaseline("SW-91.02", 0, project.Id, release.Id, null,
                    "PG Native frozen source", "source.manager", now);
                var sourceChange = new SystemChangeRequest("SRCR-910021", 0, project.Id, release.Id,
                    "PG native requirement", "Problem", "Analysis", "Solution", "source.author", now);
                var requirement = new RequirementArtifact(project.Id, "SYSR-910021", RequirementLevel.System, now);
                var revision = new RequirementRevision(requirement.Id, 0, "The PG native requirement shall remain exact.",
                    "PG source rationale", "Inspection", RequirementRevisionState.Active, sourceChange.Id, baseline.Id, now);
                var child = new RequirementArtifact(project.Id, "HLR-910022", RequirementLevel.HighLevel, now);
                var childRevision = new RequirementRevision(child.Id, 0, "The PG native child shall retain its exact parent.",
                    "Child rationale", "Inspection", RequirementRevisionState.Active, sourceChange.Id, baseline.Id, now,
                    RequirementParentKind.Allocated, parentRevisionIds: [revision.Id]);
                var allocation = new RequirementTraceLink(project.Id, childRevision.Id, revision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact source allocation", now);
                baseline.FreezeForInception("source.manager", now);
                baseline.MarkRequirementsMaterialized("source.manager", new string('c', 64), 2, now);
                db.AddRange(account, program, project, release, baseline, sourceChange, requirement, revision,
                    new BaselineRequirementSelection(baseline.Id, requirement.Id, revision.Id), child, childRevision,
                    allocation, new BaselineRequirementSelection(baseline.Id, child.Id, childRevision.Id));
                await db.SaveChangesAsync();
                accountId = account.Id;
                sourceBaselineId = baseline.Id;
            }
            var actor = new AuthenticatedUser(accountId, "pg-native-owner", "PG Native Owner",
                "pg-native-owner@example.test", true, []);
            Guid draftId;
            long configuredVersion;
            string assertionHash;
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var service = setupScope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                var inception = setupScope.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                var draft = await service.CreateAsync(actor, "PG Native destination", CancellationToken.None);
                draft = await service.UpdateAsync(draft.Id, actor, new ProjectSetupUpdateCommand(draft.Version,
                    ProjectSetupStep.StartingPoint, "PG Native destination", "PG Native destination product",
                    ProjectSetupStartKind.Fresh, null, null, "1.3", "[]", "{}",
                    ProjectSetupReviewRules.SuggestedJson("{}", draft.ProjectId), true,
                    "{\"mode\":\"ConfigureLater\"}", "{}"), CancellationToken.None);
                var captured = await inception.CaptureNativeAsync(draft.Id, actor, draft.Version, sourceBaselineId, CancellationToken.None);
                draftId = draft.Id;

                // Reopen through a fresh provider before configuring the captured source.
                using var restartedProvider = SetupProvider(connection);
                await using var restarted = restartedProvider.CreateAsyncScope();
                var restartedInception = restarted.ServiceProvider.GetRequiredService<ProjectSetupInceptionService>();
                var source = await restartedInception.ReadSourceAsync(draftId, actor, CancellationToken.None);
                Assert.NotNull(source);
                var configured = await restartedInception.SaveConfigurationAsync(draftId, actor,
                    new InceptionConfigurationCommand(captured.DraftVersion, "[\"Requirements\",\"Traces\"]",
                        NativeMappingJson(source!), "{}"), CancellationToken.None);
                Assert.Equal(ProjectSetupSourceStage.Reconciled, configured.Package.Stage);
                configuredVersion = configured.DraftVersion;
                var ready = await restartedInception.ReadSourceAsync(draftId, actor, CancellationToken.None);
                assertionHash = ready!.Assertion!.Hash;
            }

            var barrier = new DraftReadBarrier();
            using var racingProvider = SetupProvider(connection, barrier);
            async Task<ProjectSetupFinalizationResult?> FinalizeAsync()
            {
                await using var scope = racingProvider.CreateAsyncScope();
                try
                {
                    var service = scope.ServiceProvider.GetRequiredService<ProjectSetupService>();
                    return await service.FinalizeAsync(draftId, actor, configuredVersion, "pg-native-same-operation",
                        CancellationToken.None, password, assertionHash, true);
                }
                catch (ProjectSetupConflictException) { return null; }
            }
            var outcomes = await Task.WhenAll(FinalizeAsync(), FinalizeAsync());
            Assert.Equal(2, barrier.Reads);
            var completed = Assert.Single(outcomes, x => x is not null)!;
            await using (var replayScope = provider.CreateAsyncScope())
            {
                var replay = await replayScope.ServiceProvider.GetRequiredService<ProjectSetupService>()
                    .FinalizeAsync(draftId, actor, configuredVersion, "pg-native-response-retry", CancellationToken.None);
                Assert.True(replay.AlreadyCompleted);
                Assert.Equal(completed.ProjectId, replay.ProjectId);
                var db = replayScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                Assert.Single(await db.Projects.Where(x => x.Id == completed.ProjectId).ToListAsync());
                var sourceRecords = await db.ProjectInceptionSourceRecords.Where(x => x.ProjectId == completed.ProjectId)
                    .ToListAsync();
                Assert.Equal(4, sourceRecords.Count);
                Assert.Contains(sourceRecords, x => x.TargetKind == "AeroLinkBaselineSnapshot");
                Assert.Contains(sourceRecords, x => x.TargetKind == "Requirement");
                await AssertInheritedHierarchyAsync(db, completed.ProjectId, RequirementRevisionOriginKind.InheritedAeroLinkBaseline);
                Assert.Empty(await db.TestExecutions.Where(x => x.SoftwareBuildId != null
                    && db.SoftwareBuilds.Any(build => build.Id == x.SoftwareBuildId && build.ProjectId == completed.ProjectId)).ToListAsync());
            }
        });
    }

    private static async Task AssertInheritedHierarchyAsync(AeroLinkDbContext db, Guid projectId,
        RequirementRevisionOriginKind origin)
    {
        var rows = await (from revision in db.RequirementRevisions
                          join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                          where artifact.ProjectId == projectId
                          select new { revision, artifact }).ToListAsync();
        Assert.Equal(2, rows.Count);
        var parent = Assert.Single(rows, x => x.artifact.Level == RequirementLevel.System).revision;
        var child = Assert.Single(rows, x => x.artifact.Level == RequirementLevel.HighLevel).revision;
        Assert.All(rows, x => Assert.Equal(origin, x.revision.OriginKind));
        Assert.Equal(RequirementParentKind.Allocated, child.ParentKind);
        Assert.Equal(parent.Id, Assert.Single(child.ParentRevisionIds));
        var allocation = await db.RequirementTraces.SingleAsync(x => x.ProjectId == projectId);
        Assert.Equal(RequirementTraceType.AllocatedFrom, allocation.Type);
        Assert.Equal(child.Id, allocation.SourceRevisionId);
        Assert.Equal(parent.Id, allocation.TargetRevisionId);
        Assert.Equal(child.EffectiveBaselineId, parent.EffectiveBaselineId);
        Assert.Equal("Inspection", child.VerificationMethod);
    }

    private static async Task<ProjectSetupDraft> PrepareExternalDraftAsync(ProjectSetupService service,
        ProjectSetupDraft draft, AuthenticatedUser actor, string fileName)
    {
        var rules = ProjectSetupReviewRules.SuggestedJson("{}", draft.ProjectId);
        return await service.UpdateAsync(draft.Id, actor, new ProjectSetupUpdateCommand(draft.Version,
            ProjectSetupStep.StartingPoint, $"PG {fileName}", "PG inception product", ProjectSetupStartKind.Fresh,
            null, null, "1.3", "[]", "{}", rules, true, "{\"mode\":\"ConfigureLater\"}", "{}"),
            CancellationToken.None);
    }

    private static string NativeMappingJson(ProjectSetupSourceView source)
    {
        var objects = source.Modules.SelectMany(x => x.Objects ?? [])
            .Select(item => new
            {
                sourceKey = item.Key,
                include = true,
                level = item.Attributes.FirstOrDefault(x => IsSourceField(x.Key, "Level")).Value ?? "System",
                attributes = item.Attributes.Select(attribute => new
                {
                    sourceAttribute = attribute.Key,
                    destination = attribute.Key switch
                    {
                        _ when IsSourceField(attribute.Key, "Statement") => "Statement",
                        _ when IsSourceField(attribute.Key, "Rationale") => "Rationale",
                        _ when IsSourceField(attribute.Key, "VerificationMethod") => "VerificationMethod",
                        _ => "SourceOnly",
                    },
                    reason = IsSourceField(attribute.Key, "Statement")
                        || IsSourceField(attribute.Key, "Rationale")
                        || IsSourceField(attribute.Key, "VerificationMethod")
                        ? null : "Retain exact source fact.",
                }).ToArray(),
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            sourceSha256 = source.Sha256, objects,
            relations = source.Relations.Select(x => new { sourceKey = x.Key, include = true,
                type = "AllocatedFrom", sourceIsParent = false }).ToArray(),
            findingResolutions = new Dictionary<string, string>(),
        });
    }

    private static string MappingJson(ProjectSetupSourceView source)
    {
        var objects = source.Modules.SelectMany(x => x.Objects ?? [])
            .Select(item => new
            {
                sourceKey = item.Key,
                include = true,
                level = item.Attributes.FirstOrDefault(x => IsSourceField(x.Key, "Level")).Value ?? "System",
                attributes = item.Attributes.Select(attribute => new
                {
                    sourceAttribute = attribute.Key,
                    destination = IsSourceField(attribute.Key, "Identifier")
                        ? "SourceIdentifier" : IsSourceField(attribute.Key, "Statement")
                            ? "Statement" : IsSourceField(attribute.Key, "VerificationMethod")
                                ? "VerificationMethod" : "SourceOnly",
                    reason = IsSourceField(attribute.Key, "Identifier") || IsSourceField(attribute.Key, "Statement")
                        ? null : "Retain exact source fact.",
                }).ToArray(),
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            sourceSha256 = source.Sha256,
            objects,
            relations = source.Relations.Select(x => new { sourceKey = x.Key, include = true,
                type = "AllocatedFrom", sourceIsParent = true }).ToArray(),
            findingResolutions = new Dictionary<string, string>(),
        });
    }

    private static bool IsSourceField(string key, string field) => key.Equals(field, StringComparison.OrdinalIgnoreCase)
        || key.EndsWith($":{field}", StringComparison.OrdinalIgnoreCase);

    private static byte[] ExternalSourceBytes(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".csv" => Encoding.UTF8.GetBytes("Identifier,Level,Statement\r\nPG-FOREIGN-1,System,PG imported wording\r\n"),
        ".xlsx" => WorkbookBytes(),
        ".reqif" => Encoding.UTF8.GetBytes("""
            <REQ-IF>
              <REQ-IF-HEADER><SOURCE-TOOL-ID>PG External Tool</SOURCE-TOOL-ID></REQ-IF-HEADER>
              <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="REQ">
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="id" LONG-NAME="Identifier"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="level" LONG-NAME="Level"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="statement" LONG-NAME="Statement"/>
                <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="VerificationMethod" LONG-NAME="VerificationMethod"/>
              </SPEC-OBJECT-TYPE></SPEC-TYPES>
              <SPEC-OBJECTS><SPEC-OBJECT IDENTIFIER="pg-foreign-1">
                <TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="PG-FOREIGN-1"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="System"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="PG imported wording"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                </VALUES>
              </SPEC-OBJECT><SPEC-OBJECT IDENTIFIER="a-child-before-parent">
                <TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="PG-CHILD-2"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="HighLevel"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="PG imported child wording"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                  <ATTRIBUTE-VALUE-STRING THE-VALUE="Inspection"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>VerificationMethod</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
                </VALUES>
              </SPEC-OBJECT></SPEC-OBJECTS>
              <SPEC-RELATIONS><SPEC-RELATION IDENTIFIER="allocation">
                <SOURCE><SPEC-OBJECT-REF>pg-foreign-1</SPEC-OBJECT-REF></SOURCE>
                <TARGET><SPEC-OBJECT-REF>a-child-before-parent</SPEC-OBJECT-REF></TARGET>
              </SPEC-RELATION></SPEC-RELATIONS>
            </REQ-IF>
            """),
        _ => throw new ArgumentException($"Unsupported source format: {fileName}"),
    };

    private static byte[] WorkbookBytes()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Requirements' sheetId='1' r:id='rId1'/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>Identifier</t></is></c><c r='B1' t='inlineStr'><is><t>Level</t></is></c><c r='C1' t='inlineStr'><is><t>Statement</t></is></c></row><row r='2'><c r='A2' t='inlineStr'><is><t>PG-FOREIGN-1</t></is></c><c r='B2' t='inlineStr'><is><t>System</t></is></c><c r='C2' t='inlineStr'><is><t>PG imported wording</t></is></c></row></sheetData></worksheet>");
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
