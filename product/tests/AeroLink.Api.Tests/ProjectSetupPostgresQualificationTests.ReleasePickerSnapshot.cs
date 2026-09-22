using System.Data.Common;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AeroLink.Api.Tests;

// Required PostgreSQL qualification for the frozen Release picker membership (#1040). These tests run in
// the required setup-Postgres runner (filtered by FullyQualifiedName~ProjectSetupPostgresQualificationTests)
// and are never skipped when that runner executes. Raw connections drive the deterministic fence schedules;
// the API drives page-one/continuation so paging behavior is proven through the real endpoint.
public sealed partial class ProjectSetupPostgresQualificationTests
{
    private const string PrecedingMigrationId = "20260920114500_ProtectReleasedSyntheticSourceSupplement";

    private static async Task<Guid> SeedPickerProjectAsync(AeroLinkApiFactory factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"Picker program {code}", code);
        var project = new ProjectRecord(program.Id, $"Picker project {code}", "Software");
        var release = new SoftwareRelease(project.Id, "1.0", isReleased: true);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<Guid> CreateReleaseViaApiAsync(HttpClient client, Guid projectId, string version)
    {
        using var response = await client.PostAsJsonAsync("/api/releases", new { projectId, version });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> LinkOptionsPageAsync(HttpClient client, Guid projectId, int pageSize, string? cursor = null)
    {
        var url = $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize={pageSize}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> DisplayNumbers(JsonElement page)
        => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayNumber").GetString()!).ToList();

    private static async Task WalkContinuationAsync(HttpClient client, Guid projectId, string cursor, List<string> into)
    {
        var walk = cursor;
        while (true)
        {
            var page = await LinkOptionsPageAsync(client, projectId, pageSize: 1, walk);
            into.AddRange(DisplayNumbers(page));
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            walk = page.GetProperty("nextCursor").GetString()!;
        }
    }

    private static async Task InsertRawReleaseAsync(string connection, Guid projectId, string version, bool isReleased = false)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @project, @version, @released)";
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("released", isReleased);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Serializes rows losslessly for byte-preservation comparison: typed renderings (base64 for byte
    /// arrays, UTC ticks for moments, invariant culture for numbers) rather than ToString(), whose
    /// byte-array type names and culture-sensitive formatting are not preservation evidence.
    /// </summary>
    private static async Task<string> CaptureTableRowsAsync(string connection, string table)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1";
        return await RenderRowsAsync(command);
    }

    /// <summary>The fixed controlled-column projection for software_releases, stable across the migration.</summary>
    private static async Task<string> CaptureReleaseRowsAsync(string connection)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ProjectId", "Version", "CanonicalIdentity", "PredecessorReleaseId", "IsReleased", "ReleasedAt"
            FROM software_releases ORDER BY "Id"
            """;
        return await RenderRowsAsync(command);
    }

    private static async Task<string> RenderRowsAsync(DbCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var field = 0; field < reader.FieldCount; field++)
            {
                rows.Append(reader.GetName(field)).Append('=');
                rows.Append(RenderValue(reader.IsDBNull(field) ? DBNull.Value : reader.GetValue(field)));
                rows.Append('|');
            }
            rows.Append(';');
        }
        return rows.ToString();
    }

    private static string RenderValue(object value) => value switch
    {
        DBNull => "<null>",
        byte[] bytes => "b64:" + Convert.ToBase64String(bytes),
        Guid guid => "G:" + guid.ToString("D"),
        bool flag => "B:" + (flag ? "1" : "0"),
        DateTimeOffset moment => "T:" + moment.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime moment => "D:" + moment.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        System.IFormattable number => "N:" + number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        string text => "J:" + System.Text.Json.JsonSerializer.Serialize(text),
        _ => "O:" + value
    };

    private static async Task<long> ScalarAsync(string connection, string sql, Action<NpgsqlParameterCollection>? bind = null)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<T> ScalarAsync<T>(string connection, string sql, Action<NpgsqlParameterCollection>? bind = null)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    [RequiredSetupPostgresFact]
    public async Task Picker_upgrade_preserves_legacy_rows_and_freezes_continuations()
    {
        await WithDatabaseAsync(async connection =>
        {
            // Downgrade to the preceding main schema WITHOUT starting the current API host: the factory
            // constructor runs Program.cs, whose startup migration would defeat this whole test.
            await using (var preUpgrade = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            {
                await preUpgrade.Database.MigrateAsync(PrecedingMigrationId);
                var applied = await preUpgrade.Database.GetAppliedMigrationsAsync();
                Assert.Equal(PrecedingMigrationId, applied.Last());
            }

            // Prove the membership column and allocator machinery are absent at the preceding schema.
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'software_releases' AND column_name = 'PickerInsertionOrdinal'"));
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = 'software_releases'::regclass AND tgname LIKE 'aerolink_release_picker%' AND NOT tgisinternal"));
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM pg_sequences WHERE sequencename = 'aerolink_release_picker_ordinal_seq'"));

            // Seed at the OLD schema via raw SQL: released and in-work builds, a null-canonical historical
            // version, a predecessor relationship, and meaningful controlled values.
            var projectId = Guid.NewGuid();
            var release10 = Guid.NewGuid();
            var release20 = Guid.NewGuid();
            var programId = Guid.NewGuid();
            await using (var raw = new NpgsqlConnection(connection))
            {
                await raw.OpenAsync();
                await using var program = raw.CreateCommand();
                program.CommandText = "INSERT INTO programs (\"Id\", \"Name\", \"Code\") VALUES (@id, @n, @c)";
                program.Parameters.AddWithValue("id", programId);
                program.Parameters.AddWithValue("n", "Picker upgrade program");
                program.Parameters.AddWithValue("c", "PICKERUPG");
                await program.ExecuteNonQueryAsync();
                await using var project = raw.CreateCommand();
                project.CommandText = "INSERT INTO projects (\"Id\", \"ProgramId\", \"Name\", \"SoftwareProduct\") VALUES (@id, @p, @n, @s)";
                project.Parameters.AddWithValue("id", projectId);
                project.Parameters.AddWithValue("p", programId);
                project.Parameters.AddWithValue("n", "Picker upgrade project");
                project.Parameters.AddWithValue("s", "Software");
                await project.ExecuteNonQueryAsync();
                foreach (var (id, version, canonical, released, releasedAt, predecessor) in new[]
                         {
                             (release10, "1.0", (object?)"SW-01.00", (object?)true, (object?)"2026-01-15T10:00:00+00:00", (object?)null),
                             (Guid.NewGuid(), "1.5", (object?)null, (object?)false, (object?)null, (object?)null),
                             (release20, "2.0", (object?)"SW-02.00", (object?)true, (object?)"2026-02-20T12:00:00+00:00", (object?)release10),
                         })
                {
                    await using var command = raw.CreateCommand();
                    command.CommandText = """
                        INSERT INTO software_releases ("Id", "ProjectId", "Version", "CanonicalIdentity", "IsReleased", "ReleasedAt", "PredecessorReleaseId")
                        VALUES (@id, @p, @v, @c, @r, @ra::timestamptz, @pred)
                        """;
                    command.Parameters.AddWithValue("id", id);
                    command.Parameters.AddWithValue("p", projectId);
                    command.Parameters.AddWithValue("v", version);
                    command.Parameters.AddWithValue("c", canonical ?? DBNull.Value);
                    command.Parameters.AddWithValue("r", released);
                    command.Parameters.AddWithValue("ra", releasedAt ?? DBNull.Value);
                    command.Parameters.AddWithValue("pred", predecessor ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync();
                }
            }

            // Representative release-referencing records at the old schema: a candidate baseline and a
            // software build (provenance) bound to the released successor. No SoftwareRelease is added
            // through EF here, so the save-time validator never queries the not-yet-existing column.
            Guid baselineId;
            Guid buildId;
            string manifestPayload;
            string manifestHash;

            await using (var oldSchema = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            {
                var project = await oldSchema.Projects.AsNoTracking().SingleAsync();
                // The raw-seeded project needs its ladder configuration before any host can start.
                oldSchema.Add(AeroLink.Domain.Hierarchy.NewProjectLadderFactory.Create(project.Id, DateTimeOffset.UtcNow));
                await oldSchema.SaveChangesAsync();
                var releaseId = await ScalarAsync<Guid>(connection,
                    "SELECT \"Id\" FROM software_releases WHERE \"Version\" = '2.0'");
                var baseline = new AeroLink.Domain.Baselines.CandidateBaseline(
                    "SW-02.00", 0, project.Id, releaseId, null, "Upgrade preservation baseline", "picker.upgrade", DateTimeOffset.UtcNow);
                oldSchema.Add(baseline);
                await oldSchema.SaveChangesAsync();
                baselineId = baseline.Id;
                var build = new AeroLink.Domain.Programs.SoftwareBuild(project.Id, releaseId, baselineId,
                    "SW-02.00", "Upgrade preservation provenance build.", "picker.upgrade", DateTimeOffset.UtcNow);
                oldSchema.Add(build);
                await oldSchema.SaveChangesAsync();
                buildId = build.Id;

                // Representative stored manifest/signature evidence, driven through the existing domain
                // support: the manifest payload is persisted as a campaign event, its SHA-256 is bound as
                // the campaign's release hash through the review state machine, and an ElectronicSignature
                // (domain constructor) signs that exact content hash against the campaign artifact.
                manifestPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    release = "SW-02.00",
                    baseline = baselineId,
                    build = buildId,
                    contents = new[] { "requirements-baseline:SW-02.00", "provenance-build:SW-02.00" },
                    recordedBy = "picker.upgrade"
                });
                manifestHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(manifestPayload))).ToLowerInvariant();
                Assert.Equal(64, manifestHash.Length);
                var campaign = new AeroLink.Domain.Releases.ReleaseCampaign(project.Id, releaseId, baselineId,
                    "Upgrade preservation campaign", "picker.upgrade.owner", DateTimeOffset.UtcNow);
                campaign.StartVerification("picker.upgrade.owner", DateTimeOffset.UtcNow);
                campaign.SelectVerificationBuild(buildId, "picker.upgrade.owner", DateTimeOffset.UtcNow);
                campaign.RecordExecutionProgress("ReleaseManifestRecorded", manifestPayload, "picker.upgrade.owner", DateTimeOffset.UtcNow);
                campaign.BeginReleaseReview("picker.upgrade.owner",
                    new List<(string Id, string Name)> { ("picker.upgrade.approver", "Picker Upgrade Approver") },
                    manifestHash, DateTimeOffset.UtcNow);
                Assert.True(campaign.Approve("picker.upgrade.approver", DateTimeOffset.UtcNow));
                campaign.Release(buildId, manifestHash, "picker.upgrade.owner", DateTimeOffset.UtcNow);
                oldSchema.Add(campaign);
                await oldSchema.SaveChangesAsync();
                oldSchema.Add(new AeroLink.Domain.Identity.ElectronicSignature(
                    Guid.NewGuid(), "picker.upgrade.signer", "Picker Upgrade Signer", programId,
                    "ReleaseCampaign", campaign.Id, "SW-02.00", "Release",
                    "Authorized release of build SW-02.00 against the recorded manifest.",
                    manifestHash, "127.0.0.1", DateTimeOffset.UtcNow, "ProgramManager",
                    rationale: "Upgrade preservation fixture signature."));
                await oldSchema.SaveChangesAsync();

                // The persisted payload, its digest and the signature binding are paired and nonempty.
                Assert.Equal(manifestHash, campaign.ReleaseHash);
                Assert.Equal(manifestHash, Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(manifestPayload))).ToLowerInvariant());
                Assert.Equal(1L, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM release_campaign_events WHERE \"EventType\" = 'ReleaseManifestRecorded' AND \"Detail\" = @detail",
                    parameters => parameters.AddWithValue("detail", manifestPayload)));
                Assert.Equal(1L, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM electronic_signatures WHERE \"ArtifactType\" = 'ReleaseCampaign' AND \"ArtifactId\" = @id AND \"ContentHash\" = @hash",
                    parameters =>
                    {
                        parameters.AddWithValue("id", campaign.Id);
                        parameters.AddWithValue("hash", manifestHash);
                    }));
            }

            // Capture preservation evidence BEFORE the new migration is applied. The release projection is
            // a fixed controlled-column list: SELECT * could not be byte-equal across an additive column.
            var releasesBefore = await CaptureReleaseRowsAsync(connection);
            var baselinesBefore = await CaptureTableRowsAsync(connection, "candidate_baselines");
            var buildsBefore = await CaptureTableRowsAsync(connection, "software_builds");
            var campaignsBefore = await CaptureTableRowsAsync(connection, "release_campaigns");
            var approvalsBefore = await CaptureTableRowsAsync(connection, "release_approvals");
            var eventsBefore = await CaptureTableRowsAsync(connection, "release_campaign_events");
            var signaturesBefore = await CaptureTableRowsAsync(connection, "electronic_signatures");
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM release_campaigns WHERE \"ReleaseHash\" IS NOT NULL AND length(\"ReleaseHash\") = 64 AND \"ReleasedAt\" IS NOT NULL"));
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM release_approvals WHERE \"State\" = 'Approved' AND \"ApprovedAt\" IS NOT NULL"));
            // The persisted manifest payload text (JSON-encoded in the lossless rendering) and the
            // signature's bound hash are present pre-migration.
            Assert.Contains("ReleaseManifestRecorded", eventsBefore);
            Assert.Contains(System.Text.Json.JsonSerializer.Serialize(manifestPayload), eventsBefore);
            Assert.Contains(manifestHash, signaturesBefore);

            // Apply the new migration; reapplication must be a no-op.
            await using (var upgrade = new AeroLinkDbContext(
                new DbContextOptionsBuilder<AeroLinkDbContext>().UseNpgsql(connection).Options))
            {
                await upgrade.Database.MigrateAsync();
                Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
                await upgrade.Database.MigrateAsync();
                Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
            }

            // Preservation: identical rows (lossless rendering), and every pre-existing release is the
            // legacy cohort (NULL ordinal). The stored manifest payload, its hash and the signature
            // record must be preserved exactly.
            Assert.Equal(releasesBefore, await CaptureReleaseRowsAsync(connection));
            Assert.Equal(baselinesBefore, await CaptureTableRowsAsync(connection, "candidate_baselines"));
            Assert.Equal(buildsBefore, await CaptureTableRowsAsync(connection, "software_builds"));
            Assert.Equal(campaignsBefore, await CaptureTableRowsAsync(connection, "release_campaigns"));
            Assert.Equal(approvalsBefore, await CaptureTableRowsAsync(connection, "release_approvals"));
            Assert.Equal(eventsBefore, await CaptureTableRowsAsync(connection, "release_campaign_events"));
            Assert.Equal(signaturesBefore, await CaptureTableRowsAsync(connection, "electronic_signatures"));
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(\"PickerInsertionOrdinal\") FROM software_releases"));
            Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM software_releases"));

            // Only after the upgrade does the current host start (its startup migration is a no-op).
            using (var factory = new AeroLinkApiFactory(postgresConnection: connection))
            {
                var client = factory.CreateClient();
                await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
                var pageOne = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(pageOne));
                var frozenWalk = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, pageOne.GetProperty("nextCursor").GetString()!, frozenWalk);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], frozenWalk);

                // The in-work legacy 1.5 correctly refuses a new successor through the API, so the
                // post-upgrade build is inserted through a non-EF writer: the shipped trigger still owns
                // its allocation.
                await InsertRawReleaseAsync(connection, projectId, "9.9");
                await using (var check = new NpgsqlConnection(connection))
                {
                    await check.OpenAsync();
                    await using var command = check.CreateCommand();
                    command.CommandText = "SELECT COUNT(*), COUNT(\"PickerInsertionOrdinal\") FROM software_releases";
                    await using var reader = await command.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    Assert.Equal(4, reader.GetInt32(0));
                    Assert.Equal(1, reader.GetInt32(1)); // only the post-upgrade build carries an ordinal
                }

                var refrozen = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, pageOne.GetProperty("nextCursor").GetString()!, refrozen);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], refrozen);
                var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0", "BUILD-9.9"], DisplayNumbers(fresh));
                Assert.Contains("In work", fresh.GetProperty("items").GetRawText());
            }
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Picker_continuation_on_a_larger_fixture_stays_bounded_and_records_latency()
    {
        var capture = new SqlCaptureInterceptor();
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection, commandInterceptor: capture);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERSCALE");

            // A representative larger inventory: 600 additional in-work builds, each through the shipped
            // allocator trigger (set-based INSERT, one fenced nextval per row).
            await using (var raw = new NpgsqlConnection(connection))
            {
                await raw.OpenAsync();
                await using var seed = raw.CreateCommand();
                seed.CommandText = """
                    INSERT INTO software_releases ("Id", "ProjectId", "Version", "IsReleased")
                    SELECT gen_random_uuid(), @p, '3.' || lpad(g::text, 3, '0'), false
                    FROM generate_series(1, 600) g
                    """;
                seed.Parameters.AddWithValue("p", projectId);
                await seed.ExecuteNonQueryAsync();
            }
            Assert.Equal(601L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM software_releases WHERE \"ProjectId\" = @p",
                parameters => parameters.AddWithValue("p", projectId)));

            var pageWatch = System.Diagnostics.Stopwatch.StartNew();
            var pageOne = await LinkOptionsPageAsync(client, projectId, pageSize: 100);
            pageWatch.Stop();
            Assert.Equal(100, pageOne.GetProperty("items").GetArrayLength());
            Assert.True(pageOne.GetProperty("hasMore").GetBoolean());
            var cursor = pageOne.GetProperty("nextCursor").GetString()!;

            // The continuation request: bounded commands, bounded materialization, recorded latency.
            capture.Statements.Clear();
            var continuationWatch = System.Diagnostics.Stopwatch.StartNew();
            var continuation = await LinkOptionsPageAsync(client, projectId, pageSize: 100, cursor);
            continuationWatch.Stop();
            Assert.Equal(100, continuation.GetProperty("items").GetArrayLength());
            Assert.True(continuationWatch.ElapsedMilliseconds < 2000,
                $"continuation took {continuationWatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"PICKER_SCALE pageOneMs={pageWatch.ElapsedMilliseconds} continuationMs={continuationWatch.ElapsedMilliseconds}");

            // Full traversal: every candidate exactly once, in canonical order, no duplicates.
            var seen = new List<string>(601);
            seen.AddRange(DisplayNumbers(pageOne));
            seen.AddRange(DisplayNumbers(continuation));
            var walk = continuation.GetProperty("nextCursor").GetString();
            while (walk is not null)
            {
                var page = await LinkOptionsPageAsync(client, projectId, pageSize: 100, walk);
                seen.AddRange(DisplayNumbers(page));
                walk = page.GetProperty("hasMore").GetBoolean() ? page.GetProperty("nextCursor").GetString() : null;
            }
            Assert.Equal(601, seen.Count);
            Assert.Equal(601, seen.Distinct().Count());

            // The captured continuation command stays the bounded keyset query; retain its plan.
            var paged = capture.Statements.FirstOrDefault(text =>
                text.Contains("PickerInsertionOrdinal", StringComparison.Ordinal)
                && text.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(paged);
            Assert.Contains("LIMIT 101", paged);
            await using var explain = new NpgsqlConnection(connection);
            await explain.OpenAsync();
            await using var command = explain.CreateCommand();
            command.CommandText = "EXPLAIN (ANALYZE ON, COSTS ON, TIMING OFF) " + paged;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                Console.WriteLine("PICKER_SCALE_PLAN: " + reader.GetString(0));
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Picker_database_guard_rejects_forbidden_membership_mutations()
    {
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERGUARD");

            // Emulate the upgraded-database shape: one allocated row and one NULL-ordinal legacy row.
            await using (var raw = new NpgsqlConnection(connection))
            {
                await raw.OpenAsync();
                await using var disable = raw.CreateCommand();
                disable.CommandText = "ALTER TABLE software_releases DISABLE TRIGGER aerolink_release_picker_alloc_ins";
                await disable.ExecuteNonQueryAsync();
                await InsertRawReleaseAsync(connection, projectId, "0.5");
                await using var enable = raw.CreateCommand();
                enable.CommandText = "ALTER TABLE software_releases ENABLE TRIGGER aerolink_release_picker_alloc_ins";
                await enable.ExecuteNonQueryAsync();
            }
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(\"PickerInsertionOrdinal\") FROM software_releases WHERE \"Version\" = '0.5'"));

            async Task AssertRejectedAsync(string sql, string expectedMessageFragment)
            {
                await using var c = new NpgsqlConnection(connection);
                await c.OpenAsync();
                await using var command = c.CreateCommand();
                command.CommandText = sql;
                var message = (string?)null;
                try { await command.ExecuteNonQueryAsync(); }
                catch (PostgresException ex) { message = ex.MessageText; }
                Assert.True(message is not null, "expected the database guard to reject: " + sql);
                Assert.Contains(expectedMessageFragment, message);
            }

            var legacyRow = "SELECT \"Id\" FROM software_releases WHERE \"Version\" = '0.5'";
            var allocatedRow = "SELECT \"Id\" FROM software_releases WHERE \"Version\" = '1.0'";
            await AssertRejectedAsync($"INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\", \"PickerInsertionOrdinal\") VALUES ('{Guid.NewGuid()}', '{projectId}', '7.7', false, 42)",
                "picker insertion ordinal is database-allocated");
            await AssertRejectedAsync($"UPDATE software_releases SET \"PickerInsertionOrdinal\" = 999 WHERE \"Id\" = ({legacyRow})",
                "picker insertion membership is immutable");
            await AssertRejectedAsync($"UPDATE software_releases SET \"PickerInsertionOrdinal\" = 999 WHERE \"Id\" = ({allocatedRow})",
                "picker insertion membership is immutable");
            await AssertRejectedAsync($"UPDATE software_releases SET \"PickerInsertionOrdinal\" = NULL WHERE \"Id\" = ({allocatedRow})",
                "picker insertion membership is immutable");

            // Ordinary lifecycle mutation remains valid and the legacy cohort keeps its NULL membership.
            await InsertRawReleaseAsync(connection, projectId, "1.2", isReleased: true);
            await using (var update = new NpgsqlConnection(connection))
            {
                await update.OpenAsync();
                await using var command = update.CreateCommand();
                command.CommandText = "UPDATE software_releases SET \"IsReleased\" = false WHERE \"Version\" = '1.2'";
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(\"PickerInsertionOrdinal\") FROM software_releases WHERE \"Version\" = '0.5'"));
        });
    }

    private sealed class SqlCaptureInterceptor : DbCommandInterceptor
    {
        public readonly List<string> Statements = new();

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            lock (Statements)
            {
                var inlined = command.CommandText;
                foreach (DbParameter parameter in command.Parameters)
                {
                    var literal = parameter.Value == DBNull.Value
                        ? "NULL"
                        : parameter.Value switch
                        {
                            bool flag => flag ? "true" : "false",
                            string text => "'" + text.Replace("'", "''") + "'",
                            Guid guid => "'" + guid.ToString("D") + "'",
                            DateTimeOffset moment => "'" + moment.ToString("O") + "'",
                            _ => parameter.Value.ToString() ?? "NULL"
                        };
                    inlined = inlined.Replace(parameter.ParameterName, literal);
                }
                Statements.Add(inlined);
            }
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    [RequiredSetupPostgresFact]
    public async Task Picker_continuation_generated_sql_and_parameters_are_captured_for_plan_evidence()
    {
        var capture = new SqlCaptureInterceptor();
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection, commandInterceptor: capture);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERSQL");
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                db.AddRange(new SoftwareRelease(projectId, "1.5", false), new SoftwareRelease(projectId, "2.0", true));
                await db.SaveChangesAsync();
            }

            // Page one over a representative nonterminal set: its cursor binds the continuation.
            var pageOne = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
            Assert.Equal(["BUILD-1.0"], DisplayNumbers(pageOne));
            Assert.True(pageOne.GetProperty("hasMore").GetBoolean());
            var cursor = pageOne.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrEmpty(cursor));

            // Decode the emitted cursor so the continuation command can be matched against the exact
            // after-key value the client would replay.
            var encodedCursor = cursor!.Replace('-', '+').Replace('_', '/');
            encodedCursor += new string('=', (4 - encodedCursor.Length % 4) % 4);
            var cursorValue = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encodedCursor)))
                .RootElement.GetProperty("Value").GetString();
            Assert.False(string.IsNullOrEmpty(cursorValue));

            // Isolate the capture so the only paged statement recorded IS the continuation request.
            capture.Statements.Clear();
            var continuation = await LinkOptionsPageAsync(client, projectId, pageSize: 1, cursor);
            Assert.Equal(["BUILD-1.5"], DisplayNumbers(continuation));

            var paged = capture.Statements.FirstOrDefault(text =>
                text.Contains("PickerInsertionOrdinal", StringComparison.Ordinal)
                && text.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(paged);
            // The actual continuation command must carry the bound after-key predicate (the cursor's sort
            // key of BUILD-1.0), the frozen-membership predicate, the project scope and pageSize+1.
            Assert.Contains(cursorValue!, paged);
            Assert.Contains("PickerInsertionOrdinal", paged);
            Assert.Contains(projectId.ToString(), paged);
            Assert.Contains("LIMIT 2", paged);
            Console.WriteLine("PICKER_SQL_PAGED: " + paged);

            await using var explain = new NpgsqlConnection(connection);
            await explain.OpenAsync();
            await using var command = explain.CreateCommand();
            command.CommandText = "EXPLAIN (ANALYZE ON, COSTS ON, TIMING OFF) " + paged;
            var plan = new StringBuilder();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.AppendLine(reader.GetString(0));
                    Console.WriteLine("PICKER_PLAN: " + reader.GetString(0));
                }
            }

            // Retain the complete multiline SQL and plan when an evidence directory is supplied.
            var evidencePath = Environment.GetEnvironmentVariable("AEROLINK_PICKER_SQL_EVIDENCE");
            if (evidencePath is not null)
                await File.WriteAllTextAsync(Path.Combine(evidencePath, "picker-continuation-sql-plan.txt"),
                    paged + Environment.NewLine + Environment.NewLine + plan.ToString(), CancellationToken.None);
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Stale_snapshot_writers_cannot_enter_frozen_release_continuations()
    {
        foreach (var isolation in new[] { System.Data.IsolationLevel.RepeatableRead, System.Data.IsolationLevel.Serializable })
        {
            await WithDatabaseAsync(async connection =>
            {
                using var factory = new AeroLinkApiFactory(postgresConnection: connection);
                var client = factory.CreateClient();
                await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
                var projectId = await SeedPickerProjectAsync(factory,
                    $"PICKERSNAP{(isolation == System.Data.IsolationLevel.RepeatableRead ? "RR" : "SER")}");
                await CreateReleaseViaApiAsync(client, projectId, "1.5");   // in-work successor, ordinal 2

                // Writer A establishes an old MVCC snapshot (max committed ordinal = 2).
                await using var writerA = new NpgsqlConnection(connection);
                await writerA.OpenAsync();
                await using (var snapshot = writerA.CreateCommand())
                {
                    snapshot.CommandText = "SELECT COALESCE(MAX(\"PickerInsertionOrdinal\"), 0) FROM software_releases WHERE \"ProjectId\" = @p";
                    snapshot.Parameters.AddWithValue("p", projectId);
                    Assert.Equal(2L, await snapshot.ExecuteScalarAsync());
                }
                await using (var openSnapshot = writerA.CreateCommand())
                {
                    var level = isolation == System.Data.IsolationLevel.RepeatableRead ? "REPEATABLE READ" : "SERIALIZABLE";
                    openSnapshot.CommandText = $"BEGIN ISOLATION LEVEL {level}";
                    await openSnapshot.ExecuteNonQueryAsync();
                    openSnapshot.CommandText = "SELECT 1";
                    await openSnapshot.ExecuteScalarAsync();
                }

                // Writer B (Read Committed) commits another build; a page one taken now re-fences at the
                // advanced cutoff, while the earlier traversal keeps its original boundary.
                var early = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(early));
                Assert.True(early.GetProperty("hasMore").GetBoolean());
                await InsertRawReleaseAsync(connection, projectId, "2.0");
                var mid = await LinkOptionsPageAsync(client, projectId, pageSize: 1);
                Assert.Equal(["BUILD-1.0"], DisplayNumbers(mid));

                // Writer A's late INSERT allocates the global nextval, never a stale maximum.
                await using (var insert = writerA.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false) RETURNING \"PickerInsertionOrdinal\"";
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("p", projectId);
                    Assert.True((long)(await insert.ExecuteScalarAsync())! > 2L, "late INSERT must allocate beyond the snapshot maximum");
                }
                await using (var commit = writerA.CreateCommand())
                {
                    commit.CommandText = "COMMIT";
                    await commit.ExecuteNonQueryAsync();
                }

                // The early traversal (boundary before writer B) keeps its original members and excludes
                // both later builds.
                var earlyWalked = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, early.GetProperty("nextCursor").GetString()!, earlyWalked);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5"], earlyWalked);

                // The mid traversal (boundary after B, before A) admits B and still excludes A's late build.
                var midWalked = new List<string> { "BUILD-1.0" };
                await WalkContinuationAsync(client, projectId, mid.GetProperty("nextCursor").GetString()!, midWalked);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0"], midWalked);

                // A traversal started after the commits sees everything in canonical order.
                var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
                Assert.Equal(["BUILD-1.0", "BUILD-1.5", "BUILD-2.0", "BUILD-9.9"], DisplayNumbers(fresh));
            });
        }
    }

    /// <summary>
    /// Correlated, decisive fence-wait observation: the ungranted advisory lock must carry this project's
    /// fence key in this test's database, and the waiting backend's query is retained as diagnostics.
    /// </summary>
    private static async Task<bool> ObserveCorrelatedFenceWaitAsync(string connection, Guid projectId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var probe = new NpgsqlConnection(connection);
            await probe.OpenAsync();
            await using var command = probe.CreateCommand();
            command.CommandText = """
                SELECT a.pid, a.wait_event_type, left(a.query, 120)
                FROM pg_locks l
                JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE l.locktype = 'advisory' AND NOT l.granted
                  AND l.objid = hashtext('aerolink-release-picker:' || @p)
                  AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                """;
            command.Parameters.AddWithValue("p", projectId);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var waitEvent = reader.IsDBNull(1) ? "<none>" : reader.GetString(1);
                var query = reader.IsDBNull(2) ? "<null>" : reader.GetString(2);
                Console.WriteLine($"PICKER_FENCE_WAIT: pid={reader.GetInt64(0)} waitEventType={waitEvent} query={query}");
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<long> CountProjectFenceLocksAsync(string connection, Guid projectId)
        => await ScalarAsync(connection,
            """
            SELECT COUNT(*) FROM pg_locks
            WHERE locktype = 'advisory'
              AND objid = hashtext('aerolink-release-picker:' || @p)
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """,
            parameters => parameters.AddWithValue("p", projectId));

    [RequiredSetupPostgresFact]
    public async Task Page_one_fence_waits_for_uncommitted_allocator_then_includes_the_committed_build()
    {
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERFENCE");

            // An uncommitted allocator holds the shared fence: the insert trigger acquires the advisory key.
            await using var writer = new NpgsqlConnection(connection);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN";
                await begin.ExecuteNonQueryAsync();
            }
            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false)";
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("p", projectId);
                await insert.ExecuteNonQueryAsync();
            }

            // Page one must block on THIS project's fence: the observed waiter carries this project's key
            // in this database, and the waiting backend is retained as diagnostics.
            var pageOneTask = client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=50");
            Assert.True(await ObserveCorrelatedFenceWaitAsync(connection, projectId),
                "page one was never observed waiting on this project's advisory fence");

            // The allocator commits; the waited fence captures the advanced cutoff and includes the build.
            await using (var commit = writer.CreateCommand()) { commit.CommandText = "COMMIT"; await commit.ExecuteNonQueryAsync(); }
            using var pageOne = await pageOneTask;
            Assert.True(pageOne.IsSuccessStatusCode, await pageOne.Content.ReadAsStringAsync());
            var page = await pageOne.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["BUILD-1.0", "BUILD-9.9"], DisplayNumbers(page));

            // Cleanup is asserted for the owned key/database, not for a shared server.
            Assert.Equal(0L, await CountProjectFenceLocksAsync(connection, projectId));
        });
    }

    [RequiredSetupPostgresFact]
    public async Task Cancelled_page_one_releases_the_fence_without_stuck_writers()
    {
        await WithDatabaseAsync(async connection =>
        {
            using var factory = new AeroLinkApiFactory(postgresConnection: connection);
            var client = factory.CreateClient();
            await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
            var projectId = await SeedPickerProjectAsync(factory, "PICKERCANCEL");

            await using var writer = new NpgsqlConnection(connection);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN";
                await begin.ExecuteNonQueryAsync();
            }
            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = "INSERT INTO software_releases (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @p, '9.9', false)";
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("p", projectId);
                await insert.ExecuteNonQueryAsync();
            }

            // The request is proven to be inside the fence wait BEFORE cancellation, so the cancel cannot
            // be mistaken for an abort during earlier host work.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var cancelledRequest = client.GetAsync(
                $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=50", cancellation.Token);
            Assert.True(await ObserveCorrelatedFenceWaitAsync(connection, projectId),
                "the cancelled request was never observed waiting on this project's advisory fence");
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest.WaitAsync(TimeSpan.FromSeconds(20)));

            // The canceled reader leaves no lock on the owned key/database; the writer proceeds unharmed.
            await using (var rollback = writer.CreateCommand()) { rollback.CommandText = "ROLLBACK"; await rollback.ExecuteNonQueryAsync(); }
            Assert.Equal(0L, await CountProjectFenceLocksAsync(connection, projectId));
            await using var leaked = new NpgsqlConnection(connection);
            await leaked.OpenAsync();
            await using var leakedCommand = leaked.CreateCommand();
            leakedCommand.CommandText = "SELECT COUNT(*) FROM software_releases WHERE \"Version\" = '9.9'";
            Assert.Equal(0L, await leakedCommand.ExecuteScalarAsync());

            var fresh = await LinkOptionsPageAsync(client, projectId, pageSize: 50);
            Assert.Equal(["BUILD-1.0"], DisplayNumbers(fresh));
        });
    }
}
