using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectLadderPersistenceTests : IAsyncLifetime
{
    private string _path = string.Empty;
    private DbContextOptions<AeroLinkDbContext> _options = null!;
    private Guid _fmsProjectId;
    private Guid _otherProjectId;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"aerolink-project-ladder-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False;Foreign Keys=True")
            .Options;

        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var fmsProgram = new ProgramRecord("FMS program", "FMS");
        var otherProgram = new ProgramRecord("Other program", "OTH");
        var fms = new ProjectRecord(fmsProgram.Id, "FMS", "Flight Management System");
        var other = new ProjectRecord(otherProgram.Id, "Other", "Other software");
        db.AddRange(fmsProgram, otherProgram, fms, other);
        await db.SaveChangesAsync();
        _fmsProjectId = fms.Id;
        _otherProjectId = other.Id;

        await SeedLegacyAsync(fms.Id);
        await SeedLegacyAsync(other.Id);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Each_seeded_project_shape_resolves_to_the_exact_legacy_default_including_FMS()
    {
        await using var db = Context();
        var configurations = await db.ProjectLadderConfigurations
            .Include(x => x.Steps)
            .Include(x => x.AllowedUpstream)
            .AsNoTracking()
            .OrderBy(x => x.ProjectId)
            .ToListAsync();

        Assert.Equal(2, configurations.Count);
        Assert.All(configurations, configuration =>
        {
            var resolved = ProjectLadderResolver.Resolve(configuration);
            Assert.True(resolved.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, configuration.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Stored, configuration.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel],
                resolved.Steps.Select(x => x.Level));
            Assert.Equal([7, 7, 15], resolved.Steps.Select(x => (int)x.Capabilities));
        });

        var fms = configurations.Single(x => x.ProjectId == _fmsProjectId);
        Assert.Equal("FMS", (await db.Projects.SingleAsync(x => x.Id == fms.ProjectId)).Name);
        Assert.Equal(2, fms.AllowedUpstream.Count);
    }

    [Fact]
    public async Task Effective_project_resolver_reads_the_stored_shape_and_preserves_legacy_trace_compatibility()
    {
        await using var db = Context();
        var resolver = new EffectiveProjectLadderPolicyResolver(db);

        var policy = await resolver.ResolveAsync(_fmsProjectId);

        Assert.IsAssignableFrom<ILegacyLadderCompatibilityPolicy>(policy);
        Assert.Equal([RequirementLevel.HighLevel], policy.DownstreamLevels(RequirementLevel.System));
        RequirementTracePolicy.Validate(policy, RequirementLevel.System, RequirementLevel.System,
            RequirementTraceType.AllocatedFrom);
    }

    [Fact]
    public async Task Draft_uses_prior_legacy_runtime_and_active_uses_the_persisted_subset()
    {
        var projectId = Guid.Empty;
        await using (var db = Context())
        {
            var program = new ProgramRecord("Draft authority program", $"DRA{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Draft authority", "Draft authority software");
            projectId = project.Id;
            db.AddRange(program, project);
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var configuration = ProjectLadderConfiguration.CreateDraft(project.Id, now);
            var system = new ProjectLadderStep(configuration.Id, project.Id, RequirementLevel.System, 1,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
            var low = new ProjectLadderStep(configuration.Id, project.Id, RequirementLevel.LowLevel, 2,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
            configuration.Steps.Add(system);
            configuration.Steps.Add(low);
            configuration.AllowedUpstream.Add(new(configuration.Id, project.Id, system.Id, low.Id, now));
            db.ProjectLadderConfigurations.Add(configuration);
            await db.SaveChangesAsync();
        }

        await using (var draft = Context())
        {
            var policy = await new EffectiveProjectLadderPolicyResolver(draft).ResolveAsync(projectId);
            Assert.Same(LegacyLadderPolicy.Instance, policy);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel], policy.OrderedLevels);
        }

        await using (var activate = Context())
        {
            var configuration = await activate.ProjectLadderConfigurations
                .SingleAsync(x => x.ProjectId == projectId);
            await activate.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE project_ladder_configurations
                SET Classification = 'NonDefault', State = 'Active', Version = 2,
                    ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'test.manager',
                    ActivationManifestVersion = 'test-manifest-v1',
                    ActivationManifestHash = {'a'.ToString().PadLeft(64, 'a')}
                WHERE Id = {configuration.Id}
                """);
        }

        await using (var active = Context())
        {
            var policy = await new EffectiveProjectLadderPolicyResolver(active).ResolveAsync(projectId);
            Assert.IsType<ResolvedProjectLadderPolicy>(policy);
            Assert.Equal([RequirementLevel.System, RequirementLevel.LowLevel], policy.OrderedLevels);
            Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.System));
            RequirementTracePolicy.Validate(policy, RequirementLevel.LowLevel, RequirementLevel.System,
                RequirementTraceType.DerivedFrom);
            Assert.Throws<Domain.Common.DomainException>(() => RequirementTracePolicy.Validate(policy,
                RequirementLevel.System, RequirementLevel.LowLevel, RequirementTraceType.DerivedFrom));
        }
    }

    [Fact]
    public async Task Database_rejects_duplicate_step_position_or_catalogue_entry()
    {
        await using var db = Context();
        var configuration = await db.ProjectLadderConfigurations
            .Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var duplicate = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, DateTimeOffset.UtcNow);
        db.ProjectLadderSteps.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_an_upstream_edge_that_crosses_projects()
    {
        await using var db = Context();
        var first = await db.ProjectLadderConfigurations.Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var second = await db.ProjectLadderConfigurations.Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _otherProjectId);
        var firstSystem = first.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.System));
        var foreignHigh = second.Steps.Single(x => x.CatalogueEntry == nameof(RequirementLevel.HighLevel));
        db.ProjectLadderAllowedUpstreams.Add(new ProjectLadderAllowedUpstream(
            first.Id, first.ProjectId, firstSystem.Id, foreignHigh.Id, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Configuration_state_and_activation_evidence_are_checked_as_a_pair_by_the_database()
    {
        await using var db = Context();
        var configuration = await db.ProjectLadderConfigurations
            .SingleAsync(x => x.ProjectId == _fmsProjectId);

        FormattableString sql = $"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Active', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = NULL WHERE Id = {configuration.Id}";
        Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlInterpolated(sql));
    }

    [Fact]
    public async Task Database_rejects_manifest_evidence_on_stored_and_incomplete_active_or_retired_rows()
    {
        await using var read = Context();
        var configuration = await read.ProjectLadderConfigurations.AsNoTracking()
            .SingleAsync(x => x.ProjectId == _fmsProjectId);

        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET ActivationManifestVersion = 'v1' WHERE Id = {configuration.Id}");
        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Active', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'manager', ActivationManifestVersion = 'v1', ActivationManifestHash = NULL WHERE Id = {configuration.Id}");
        await AssertInvalidUpdateAsync($"UPDATE project_ladder_configurations SET Classification = 'NonDefault', State = 'Retired', ActivatedAt = CURRENT_TIMESTAMP, ActivatedBy = 'manager', RetiredAt = CURRENT_TIMESTAMP, RetiredBy = 'manager', ActivationManifestVersion = NULL, ActivationManifestHash = NULL WHERE Id = {configuration.Id}");
    }

    [Fact]
    public async Task Resolver_fails_closed_when_persisted_catalogue_data_is_unknown()
    {
        await using (var db = Context())
        {
            var configuration = await db.ProjectLadderConfigurations
                .SingleAsync(x => x.ProjectId == _fmsProjectId);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE project_ladder_steps SET CatalogueEntry = 'Unknown' WHERE ConfigurationId = {configuration.Id} AND Position = 1");
        }

        await using var check = Context();
        var malformed = await check.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        var error = Assert.Throws<AeroLink.Domain.Common.DomainException>(() => ProjectLadderResolver.Resolve(malformed));
        Assert.Contains("Unknown persisted", error.Message);
    }

    [Fact]
    public async Task Database_rejects_a_software_procedure_without_case_or_a_kind_without_capability()
    {
        await using var db = Context();
        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE project_ladder_steps SET EnabledArtifactKindsValue = 'Procedure' WHERE ConfigurationId = {configuration.Id} AND CatalogueEntry = 'HighLevel'"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE project_ladder_steps SET Capabilities = 1, EnabledArtifactKindsValue = 'Case' WHERE ConfigurationId = {configuration.Id} AND CatalogueEntry = 'System'"));
    }

    [Fact]
    public async Task Version_is_optimistic_concurrency_protection_for_a_persisted_ladder()
    {
        await using var first = Context();
        await using var second = Context();
        var firstConfiguration = await first.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var secondConfiguration = await second.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var updatedAt = DateTimeOffset.UtcNow;
        first.Entry(firstConfiguration).Property(nameof(ProjectLadderConfiguration.UpdatedAt)).CurrentValue = updatedAt;
        first.Entry(firstConfiguration).Property(nameof(ProjectLadderConfiguration.Version)).CurrentValue = 2L;
        second.Entry(secondConfiguration).Property(nameof(ProjectLadderConfiguration.UpdatedAt)).CurrentValue = updatedAt.AddSeconds(1);
        second.Entry(secondConfiguration).Property(nameof(ProjectLadderConfiguration.Version)).CurrentValue = 2L;

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task First_draft_requirement_change_seals_ladder_and_structural_edit_returns_dependency_conflict()
    {
        await using var db = Context();
        var release = new SoftwareRelease(_fmsProjectId, "2.0", true);
        var now = DateTimeOffset.UtcNow;
        var request = new SystemChangeRequest("SRCR-70701", 0, _fmsProjectId, release.Id,
            "First controlled draft", "Problem", "Analysis", "Solution", "author", now);
        var change = request.AddRequirementChange("author", "SYSR-00000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall hold the ladder.", "Initial controlled content.",
            "Review", now);
        db.AddRange(release, request);

        await db.SaveChangesAsync();

        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        Assert.True(configuration.IsSealed);
        Assert.Equal("draft-requirement-change", configuration.SealedContentKind);
        Assert.Equal(change.DisplayNumber, configuration.SealedContentIdentity);
        Assert.Equal("author", configuration.SealedBy);
        Assert.Single(await db.ProjectLadderConfigurationHistories.ToListAsync());

        var service = new ProjectLadderAuthoringService(db, LegacyLadderPolicy.Instance,
            Array.Empty<ILadderConsumerRegistration>());
        var edit = await service.EditAsync(_fmsProjectId,
            new ProjectLadderEditCommand(configuration.Version, "attempt after content", [], []),
            "editor", now, CancellationToken.None);
        Assert.Equal(ProjectLadderEditResultKind.Conflict, edit.Kind);
        Assert.Contains("draft-requirement-change", edit.Error);
        Assert.Contains(change.DisplayNumber, edit.Error);
    }

    public static IEnumerable<object[]> Catalogued_ladder_bound_content_kinds()
        => LadderBoundContentCatalog.Current.Select(x => new object[] { x.Id });

    [Theory]
    [MemberData(nameof(Catalogued_ladder_bound_content_kinds))]
    public async Task Every_catalogued_entity_seam_is_guarded_by_the_central_save_authority(string contentKind)
    {
        await using var db = Context();
        var now = DateTimeOffset.UtcNow;
        var expectedIdentity = await AddCataloguedEntityAsync(db, contentKind, now);
        if (contentKind == "code-traceability")
            Assert.Contains(db.ChangeTracker.Entries<RequirementArtifact>(), x => x.State == EntityState.Added);
        await db.SaveChangesAsync();

        var persisted = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        Assert.True(persisted.IsSealed);
        Assert.NotNull(persisted.SealedContentKind);
        Assert.NotNull(persisted.SealedContentIdentity);
        Assert.Equal(1, await db.ProjectLadderConfigurationHistories.CountAsync());
        Assert.Contains(contentKind switch
        {
            "draft-requirement-change" => "draft-requirement-change",
            "requirement-artifact" => "requirement-artifact",
            "requirement-revision" => "requirement-artifact", // The artifact prerequisite is the first qualifying row.
            "test-procedure" => "test-procedure",
            "test-change-review" => "test-change-review",
            "trace-link" => "requirement-artifact", // Revisions and their artifact are prerequisites.
            "code-traceability" => "code-traceability", // Deterministic kind ordering makes this candidate first in the same UoW.
            _ => throw new InvalidOperationException(contentKind)
        }, persisted.SealedContentKind);
        Assert.True(expectedIdentity.Length > 0);
    }

    [Fact]
    public async Task Baseline_import_scaffolding_does_not_seal_ladder()
    {
        await using var db = Context();
        var now = DateTimeOffset.UtcNow;
        var import = new BaselineImport(_fmsProjectId, "legacy-tool", "1.0", "baseline-1", now,
            "baseline.json", new string('a', 64), 10, ImportedArtifactKinds.Requirements,
            "extractor", now, "operator", now);
        db.BaselineImports.Add(import);

        await db.SaveChangesAsync();

        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        Assert.False(configuration.IsSealed);
        Assert.Null(configuration.SealedContentKind);
        Assert.Empty(await db.ProjectLadderConfigurationHistories.ToListAsync());
    }

    [Fact]
    public async Task Qualifying_content_without_a_ladder_row_materializes_and_seals_legacy_default_atomically()
    {
        await using var db = Context();
        var program = new ProgramRecord("Missing ladder program", $"ML{Guid.NewGuid():N}"[..10]);
        var project = new ProjectRecord(program.Id, "Missing ladder", "Missing ladder software");
        var release = new SoftwareRelease(project.Id, "1.0", true);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var request = new SystemChangeRequest("SRCR-70704", 0, project.Id, release.Id,
            "Missing ladder content", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", "SYSR-00000003", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The content cannot be persisted without its ladder.", "Invariant test", "Review", now);
        db.SystemChangeRequests.Add(request);

        await db.SaveChangesAsync();

        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == project.Id);
        Assert.True(configuration.IsSealed);
        Assert.Equal("draft-requirement-change", configuration.SealedContentKind);
        Assert.Equal(1, await db.SystemChangeRequests.CountAsync(x => x.ProjectId == project.Id));
        Assert.Equal(1, await db.RequirementChanges.CountAsync());
    }

    [Fact]
    public async Task Competing_ladder_edit_and_first_content_leave_one_commit_and_no_partial_loser()
    {
        Guid releaseId;
        await using (var setup = Context())
        {
            var release = new SoftwareRelease(_fmsProjectId, "3.0", true);
            releaseId = release.Id;
            setup.Add(release);
            await setup.SaveChangesAsync();
        }

        await using var editDb = Context();
        await using var contentDb = Context();
        var editConfiguration = await editDb.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        _ = await contentDb.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        editConfiguration.BeginDraftEdit(DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;
        var request = new SystemChangeRequest("SRCR-70702", 0, _fmsProjectId, releaseId,
            "Race content", "Problem", "Analysis", "Solution", "content.author", now);
        request.AddRequirementChange("content.author", "SYSR-00000002", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall record race content.", "Race test", "Review", now);
        contentDb.SystemChangeRequests.Add(request);

        async Task<string> SaveEditAsync()
        {
            try
            {
                await editDb.SaveChangesAsync();
                return "edit";
            }
            catch (DbUpdateConcurrencyException)
            {
                return "edit-lost";
            }
        }

        async Task<string> SaveContentAsync()
        {
            try
            {
                await contentDb.SaveChangesAsync();
                return "content";
            }
            catch (ProjectLadderSealConcurrencyException)
            {
                return "content-lost";
            }
        }

        var outcomes = await Task.WhenAll(SaveEditAsync(), SaveContentAsync());
        Assert.Single(outcomes, x => x is "edit" or "content");
        Assert.Contains(outcomes, x => x == "edit-lost" || x == "content-lost");

        await using var check = Context();
        var configuration = await check.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var contentWon = outcomes.Contains("content");
        Assert.Equal(contentWon, configuration.IsSealed);
        Assert.Equal(contentWon ? 1 : 0, await check.ProjectLadderConfigurationHistories.CountAsync());
        Assert.Equal(contentWon ? 1 : 0, await check.SystemChangeRequests.CountAsync(x => x.ProjectId == _fmsProjectId));
        Assert.Equal(contentWon ? 1 : 0, await check.RequirementChanges.CountAsync());
        if (!contentWon)
            Assert.Equal(ProjectLadderConfigurationState.Draft, configuration.State);
    }

    [Fact]
    public async Task Multi_project_first_content_conflict_identifies_the_actual_losing_candidate()
    {
        await using var loser = Context();
        _ = await loser.ProjectLadderConfigurations.Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        _ = await loser.ProjectLadderConfigurations.Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == _otherProjectId);

        await using (var winner = Context())
        {
            var release = new SoftwareRelease(_fmsProjectId, "5.0", true);
            var request = new SystemChangeRequest("SRCR-70706", 0, _fmsProjectId, release.Id,
                "Winning content", "Problem", "Analysis", "Solution", "winner", DateTimeOffset.UtcNow);
            request.AddRequirementChange("winner", "SYSR-00000005", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "Winner", "Winner", "Review", DateTimeOffset.UtcNow);
            winner.AddRange(release, request);
            await winner.SaveChangesAsync();
        }

        var secondRelease = new SoftwareRelease(_fmsProjectId, "5.1", true);
        var otherRelease = new SoftwareRelease(_otherProjectId, "1.1", true);
        var losingRequest = new SystemChangeRequest("SRCR-70707", 0, _fmsProjectId, secondRelease.Id,
            "Losing content", "Problem", "Analysis", "Solution", "loser", DateTimeOffset.UtcNow);
        var losingChange = losingRequest.AddRequirementChange("loser", "SYSR-00000006", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "Losing content", "Losing content", "Review", DateTimeOffset.UtcNow);
        var otherRequest = new SystemChangeRequest("SRCR-70708", 0, _otherProjectId, otherRelease.Id,
            "Independent content", "Problem", "Analysis", "Solution", "other", DateTimeOffset.UtcNow);
        otherRequest.AddRequirementChange("other", "SYSR-00000007", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "Independent content", "Independent content", "Review", DateTimeOffset.UtcNow);
        loser.AddRange(secondRelease, otherRelease, losingRequest, otherRequest);

        var error = await Assert.ThrowsAsync<ProjectLadderSealConcurrencyException>(() => loser.SaveChangesAsync());
        Assert.Contains(_fmsProjectId.ToString("D"), error.Message);
        Assert.Contains(losingChange.DisplayNumber, error.Message);
        Assert.DoesNotContain(otherRequest.Id.ToString("D"), error.Message);

        await using var check = Context();
        var otherConfiguration = await check.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _otherProjectId);
        Assert.False(otherConfiguration.IsSealed);
        Assert.Empty(await check.RequirementChanges.Where(x => x.ChangeRequestId == otherRequest.Id).ToListAsync());
    }

    [Fact]
    public async Task Internal_upgrade_seam_requires_readiness_and_records_the_structural_transform()
    {
        await using var db = Context();
        var release = new SoftwareRelease(_fmsProjectId, "4.0", true);
        var now = DateTimeOffset.UtcNow;
        var request = new SystemChangeRequest("SRCR-70705", 0, _fmsProjectId, release.Id,
            "Seal for upgrade", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", "SYSR-00000004", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall support the governed upgrade.", "Upgrade test", "Review", now);
        db.AddRange(release, request);
        await db.SaveChangesAsync();

        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
        var steps = new[]
        {
            new LadderStepDraft(nameof(RequirementLevel.System), 1,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities),
            new LadderStepDraft(nameof(RequirementLevel.LowLevel), 2,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities)
        };
        var relationships = new[]
        {
            new LadderRelationshipDraft(nameof(RequirementLevel.System), nameof(RequirementLevel.LowLevel))
        };
        var typedConsumers = consumers.Select(registration =>
            (IVerificationArtifactConsumerRegistration)LadderConsumerManifestCatalog.TypedRegistration(registration))
            .ToArray();
        var authority = new ProjectLadderUpgradeAuthority(db, LegacyLadderPolicy.Instance, consumers, typedConsumers);
        var result = await authority.UpgradeAsync(_fmsProjectId,
            new ProjectLadderUpgradeCommand(configuration.Version, "platform-v2", "Replace governed graph", steps, relationships),
            "platform.owner", now.AddMinutes(1));

        Assert.Equal(ProjectLadderUpgradeResultKind.Success, result.Kind);
        var upgraded = await db.ProjectLadderConfigurations.Include(x => x.Steps)
            .SingleAsync(x => x.ProjectId == _fmsProjectId);
        Assert.True(upgraded.IsSealed);
        Assert.Equal("platform-v2", upgraded.LastUpgradeVersion);
        Assert.Equal("platform.owner", upgraded.LastUpgradeBy);
        Assert.Equal(2, upgraded.Steps.Count);
        Assert.Equal(2, await db.ProjectLadderConfigurationHistories.CountAsync());
    }

    [Fact]
    public async Task Internal_upgrade_refuses_when_typed_artifact_readiness_is_incomplete()
    {
        await using var db = Context();
        var consumers = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)).ToArray();
        var typedConsumers = consumers.Select(registration =>
        {
            var typed = LadderConsumerManifestCatalog.TypedRegistration(registration);
            return (IVerificationArtifactConsumerRegistration)(typed.Id == "verification.test-change-workflow"
                ? typed with { SupportedCapabilities = VerificationArtifactCapability.None }
                : typed);
        }).ToArray();
        var authority = new ProjectLadderUpgradeAuthority(db, LegacyLadderPolicy.Instance, consumers, typedConsumers);
        var configuration = await db.ProjectLadderConfigurations.SingleAsync(x => x.ProjectId == _fmsProjectId);
        var result = await authority.UpgradeAsync(_fmsProjectId,
            new ProjectLadderUpgradeCommand(configuration.Version, "platform-v2", "Refuse incomplete typed graph",
                [new LadderStepDraft(nameof(RequirementLevel.System), 1,
                    LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities)], []),
            "platform.owner", DateTimeOffset.UtcNow);

        Assert.Equal(ProjectLadderUpgradeResultKind.Refused, result.Kind);
        Assert.NotNull(result.ArtifactReadiness);
        Assert.False(result.ArtifactReadiness!.IsReady);
        Assert.Contains(result.ArtifactReadiness.MissingArtifactCoverage,
            x => x.ConsumerId == "verification.test-change-workflow"
                && x.ArtifactKey.Kind == VerificationArtifactKind.Procedure
                && !x.SupportsCapabilities);
    }

    private AeroLinkDbContext Context() => new(_options);

    private async Task<string> AddCataloguedEntityAsync(AeroLinkDbContext db, string contentKind, DateTimeOffset now)
    {
        switch (contentKind)
        {
            case "draft-requirement-change":
            {
                var release = new SoftwareRelease(_fmsProjectId, "9.0", true);
                var request = new SystemChangeRequest("SRCR-70710", 0, _fmsProjectId,
                    release.Id, "Catalog draft", "Problem", "Analysis", "Solution", "catalog.test", now);
                var change = request.AddRequirementChange("catalog.test", "SYSR-00000010", 0,
                    RequirementLevel.System, RequirementChangeKind.Introduce, "Catalog content", "Catalog content",
                    "Review", now);
                db.AddRange(release, request);
                return change.DisplayNumber;
            }
            case "requirement-artifact":
            {
                var artifact = new RequirementArtifact(_fmsProjectId, "SYS-00001",
                    RequirementLevel.System, now);
                db.Requirements.Add(artifact);
                return artifact.BaseNumber;
            }
            case "requirement-revision":
            {
                var release = new SoftwareRelease(_fmsProjectId, "9.0", true);
                var request = new SystemChangeRequest("SRCR-70710", 0, _fmsProjectId,
                    release.Id, "Catalog revision", "Problem", "Analysis", "Solution", "catalog.test", now);
                var baseline = new CandidateBaseline("BL-00001", 0, _fmsProjectId,
                    release.Id, null, "Catalog baseline", "catalog.test", now);
                var artifact = new RequirementArtifact(_fmsProjectId, "SYS-00001",
                    RequirementLevel.System, now);
                var revision = new RequirementRevision(artifact.Id, 0, "Catalog revision statement", "Catalog rationale",
                    "Inspection", RequirementRevisionState.Active, request.Id, baseline.Id, now);
                db.AddRange(release, request, baseline, artifact, revision);
                return revision.Id.ToString("D");
            }
            case "test-procedure":
            {
                var procedure = new TestProcedure(_fmsProjectId, "HLRTP-00001",
                    "Catalog procedure", "catalog.test", now, TestProcedureLevel.HighLevel);
                db.TestProcedures.Add(procedure);
                return procedure.BaseNumber;
            }
            case "test-change-review":
            {
                var release = new SoftwareRelease(_fmsProjectId, "9.0", true);
                var request = new SystemChangeRequest("SRCR-70710", 0, _fmsProjectId,
                    release.Id, "Catalog review", "Problem", "Analysis", "Solution", "catalog.test", now);
                var review = new TestChangeReview(_fmsProjectId, release.Id, request.Id,
                    TestChangeReviewDiscipline.System, request.DisplayNumber, now, authorId: "catalog.test");
                db.AddRange(release, request, review);
                return review.Id.ToString("D");
            }
            case "trace-link":
            {
                var (release, request, baseline, first, second) = CreateRevisionPrerequisites(now);
                var link = new RequirementTraceLink(_fmsProjectId, first.Revision.Id, second.Revision.Id,
                    RequirementTraceType.DerivedFrom, "Catalog trace", now);
                db.AddRange(release, request, baseline, first.Artifact, second.Artifact, first.Revision, second.Revision, link);
                return link.Id.ToString("D");
            }
            case "code-traceability":
            {
                var (release, request, baseline, first, _) = CreateRevisionPrerequisites(now);
                var code = new CodeTraceabilityRecord(_fmsProjectId, release.Id, first.Artifact.Id, first.Revision.Id,
                    CodeTraceDisposition.NoCodeChangeRequired, "", "", "", "", "", null,
                    "Catalog has no code change.", false, "catalog.test", now);
                db.AddRange(release, request, baseline, first.Artifact, first.Revision, code);
                return code.Id.ToString("D");
            }
            default:
                throw new InvalidOperationException($"Unregistered catalog test kind '{contentKind}'.");
        }
    }

    private (SoftwareRelease Release, SystemChangeRequest Request, CandidateBaseline Baseline,
        (RequirementArtifact Artifact, RequirementRevision Revision) First,
        (RequirementArtifact Artifact, RequirementRevision Revision) Second) CreateRevisionPrerequisites(DateTimeOffset now)
    {
        var release = new SoftwareRelease(_fmsProjectId, "9.0", true);
        var request = new SystemChangeRequest("SRCR-70710", 0, _fmsProjectId,
            release.Id, "Catalog revisions", "Problem", "Analysis", "Solution", "catalog.test", now);
        var baseline = new CandidateBaseline("BL-00001", 0, _fmsProjectId,
            release.Id, null, "Catalog baseline", "catalog.test", now);
        var firstArtifact = new RequirementArtifact(_fmsProjectId, "SYS-00001",
            RequirementLevel.System, now);
        var secondArtifact = new RequirementArtifact(_fmsProjectId, "HLR-00001",
            RequirementLevel.HighLevel, now);
        var firstRevision = new RequirementRevision(firstArtifact.Id, 0, "Catalog source statement", "Catalog rationale",
            "Inspection", RequirementRevisionState.Active, request.Id, baseline.Id, now);
        var secondRevision = new RequirementRevision(secondArtifact.Id, 0, "Catalog target statement", "Catalog rationale",
            "Inspection", RequirementRevisionState.Active, request.Id, baseline.Id, now);
        return (release, request, baseline, (firstArtifact, firstRevision), (secondArtifact, secondRevision));
    }

    private async Task SeedLegacyAsync(Guid projectId)
    {
        await using var db = Context();
        var now = DateTimeOffset.UtcNow;
        var configuration = LegacyDefaultProjectLadderFactory.Create(projectId, now);
        db.ProjectLadderConfigurations.Add(configuration);
        await db.SaveChangesAsync();
    }

    private async Task AssertInvalidUpdateAsync(FormattableString sql)
    {
        await using var db = Context();
        Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlInterpolated(sql));
    }
}
