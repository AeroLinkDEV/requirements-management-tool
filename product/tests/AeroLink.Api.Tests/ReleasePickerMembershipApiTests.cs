using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Release picker membership freeze (#1040): functional API and EF behavior on the SQLite fast provider.
// PostgreSQL concurrency, migration and plan evidence live in the required setup-Postgres runner.
public sealed class ReleasePickerMembershipApiTests
{
    private static async Task<(Guid ProjectId, HttpClient Client, AeroLinkApiFactory Factory)> SeedAsync(
        string programCode, params (string Version, bool IsReleased)[] releases)
    {
        var factory = new AeroLinkApiFactory();
        var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord($"Picker program {programCode}", programCode);
            var project = new ProjectRecord(program.Id, $"Picker project {programCode}", "Software");
            db.AddRange(program, project);
            Guid? predecessor = null;
            foreach (var (version, isReleased) in releases)
            {
                var release = new SoftwareRelease(project.Id, version, isReleased, predecessor);
                db.Add(release);
                predecessor = release.Id;
            }
            await db.SaveChangesAsync();
        }
        return (await GetProjectIdAsync(factory, programCode), client, factory);
    }

    private static async Task<Guid> GetProjectIdAsync(AeroLinkApiFactory factory, string programCode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await db.Projects.AsNoTracking()
            .Where(p => p.Name == $"Picker project {programCode}")
            .Select(p => p.Id).SingleAsync();
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, Guid projectId, int pageSize, string? cursor = null, string? search = null)
    {
        var url = $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize={pageSize}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (search is not null) url += $"&search={Uri.EscapeDataString(search)}";
        using var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> DisplayNumbers(JsonElement page)
        => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayNumber").GetString()!).ToList();

    [Fact]
    public async Task Release_continuation_freezes_membership_and_a_fresh_traversal_shows_later_builds()
    {
        var (projectId, client, factory) = await SeedAsync("PICKER101",
            ("1.0", true), ("1.5", false), ("2.0", true));
        using var _ = factory;

        var pageOne = await PageAsync(client, projectId, pageSize: 2);
        Assert.Equal(["BUILD-1.0", "BUILD-1.5"], DisplayNumbers(pageOne));
        var cursor = pageOne.GetProperty("nextCursor").GetString()!;

        // Two builds committed after page one: one sorting before the cursor, one after it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.AddRange(new SoftwareRelease(projectId, "1.2", false), new SoftwareRelease(projectId, "9.9", false));
            await db.SaveChangesAsync();
        }

        var continuation = await PageAsync(client, projectId, pageSize: 50, cursor);
        Assert.Equal(["BUILD-2.0"], DisplayNumbers(continuation));

        var fresh = await PageAsync(client, projectId, pageSize: 50);
        Assert.Equal(["BUILD-1.0", "BUILD-1.2", "BUILD-1.5", "BUILD-2.0", "BUILD-9.9"], DisplayNumbers(fresh));
    }

    [Fact]
    public async Task Old_release_v1_cursor_fails_closed_with_a_start_again_path()
    {
        var (projectId, client, factory) = await SeedAsync("PICKER102", ("1.0", true), ("1.5", false), ("2.0", true));
        using var _ = factory;
        var pageOne = await PageAsync(client, projectId, pageSize: 1);
        var v2Cursor = pageOne.GetProperty("nextCursor").GetString()!;

        // Downgrade the captured v2 token to the legacy v1 shape and expect the recoverable refusal.
        var encoded = v2Cursor.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))).RootElement;
        var body = "{" + string.Join(',', json.EnumerateObject().Select(property =>
            property.Name == "Version"
                ? "\"Version\":1"
                : property.Value.ValueKind == JsonValueKind.String
                    ? $"\"{property.Name}\":{JsonSerializer.Serialize(property.Value.GetString())}"
                    : $"\"{property.Name}\":{property.Value.GetRawText()}")) + "}";
        var v1Cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var response = await client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=1&cursor={Uri.EscapeDataString(v1Cursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_cursor", await response.Content.ReadAsStringAsync());
        Assert.Contains("Start again from the first page", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Oversized_malformed_and_out_of_contract_cursors_fail_bounded()
    {
        var (projectId, client, factory) = await SeedAsync("PICKER103", ("1.0", true), ("1.5", false), ("2.0", true));
        using var _ = factory;
        var pageOne = await PageAsync(client, projectId, pageSize: 1);
        var cursor = pageOne.GetProperty("nextCursor").GetString()!;

        async Task AssertRejectedAsync(string rawCursor, int page = 1)
        {
            using var response = await client.GetAsync(
                $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize={page}&cursor={Uri.EscapeDataString(rawCursor)}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_cursor", await response.Content.ReadAsStringAsync());
        }

        await AssertRejectedAsync(new string('A', 4097));                       // over the pre-decode bound
        await AssertRejectedAsync("not-a-cursor");                             // malformed
        await AssertRejectedAsync(cursor + "x");                               // corrupted

        var encoded = cursor.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))).RootElement;

        string Reencode(Action<IDictionary<string, string>> mutate)
        {
            var fields = json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetRawText());
            mutate(fields);
            var rebuilt = "{" + string.Join(',', fields.Select(field => $"\"{field.Key}\":{field.Value}")) + "}";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(rebuilt)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        await AssertRejectedAsync(Reencode(fields => fields.Remove("CutoffOrdinal")));                 // missing cutoff
        await AssertRejectedAsync(Reencode(fields => fields["CutoffOrdinal"] = "-1"));                 // negative
        await AssertRejectedAsync(Reencode(fields => fields["FilterKey"] = "\"0\""));                   // wrong filter binding
        await AssertRejectedAsync(Reencode(fields => fields["TieBreaker"] = "\"7\""));                  // non-Release tie-breaker
    }

    [Fact]
    public async Task Search_binding_page_sizes_and_termination_are_preserved()
    {
        var (projectId, client, factory) = await SeedAsync("PICKER104", ("1.0", true), ("1.5", false), ("2.0", true));
        using var _ = factory;

        var pageOne = await PageAsync(client, projectId, pageSize: 1);
        var cursor = pageOne.GetProperty("nextCursor").GetString()!;
        using var changedFilter = await client.GetAsync(
            $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=1&search=9&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);

        using var tooSmall = await client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=0");
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
        using var tooLarge = await client.GetAsync($"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);

        var search = await PageAsync(client, projectId, pageSize: 50, search: "1.");
        Assert.Equal(["BUILD-1.0", "BUILD-1.5"], DisplayNumbers(search));
        Assert.False(search.GetProperty("hasMore").GetBoolean());
        Assert.True(search.GetProperty("nextCursor").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Release_v2_cursor_round_trips_maximum_historical_labels()
    {
        var factory = new AeroLinkApiFactory();
        using var _ = factory;
        var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        Guid projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Picker label program", "PICKERLBL");
            var project = new ProjectRecord(program.Id, "Picker label project", "Software");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            projectId = project.Id;
            var release = new SoftwareRelease(projectId, "1.0", true);
            db.Add(release);
            await db.SaveChangesAsync();
        }

        // Pre-validation historical rows: two DISTINCT 40-character multi-byte labels (the mapped
        // PostgreSQL limit), no canonical identity. Their fallback SortKey repeats the label twice,
        // producing the largest legitimate cursor payloads.
        var historicalLabel = new string('é', 39);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var version in new[] { historicalLabel + "1", historicalLabel + "2" })
            {
                Assert.Equal(40, version.Length);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES ($id, $p, $v, 1)";
                command.Parameters.AddWithValue("$id", Guid.NewGuid());
                command.Parameters.AddWithValue("$p", projectId);
                command.Parameters.AddWithValue("$v", version);
                await command.ExecuteNonQueryAsync();
            }
        }

        // Page one ends with the FIRST historical label, so the emitted cursor must carry its large
        // fallback sort key and still be accepted within the 4096-character bound — while remaining
        // larger than the retired 640-character cap, which is the regression this guards.
        var pageOne = await PageAsync(client, projectId, pageSize: 2);
        Assert.Equal(["BUILD-1.0", $"BUILD-{historicalLabel}1"], DisplayNumbers(pageOne));
        Assert.True(pageOne.GetProperty("hasMore").GetBoolean());
        var cursor = pageOne.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length > 640, $"emitted cursor was only {cursor.Length} characters");
        Assert.True(cursor.Length <= 4096, $"emitted cursor was {cursor.Length} characters");

        var encoded = cursor.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var carriedValue = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
            .RootElement.GetProperty("Value").GetString()!;
        Assert.Contains(historicalLabel, carriedValue);          // the historical sort key is in the cursor
        Assert.StartsWith("1|", carriedValue);                    // via the legacy fallback grammar

        var pageTwo = await PageAsync(client, projectId, pageSize: 2, cursor);
        Assert.Equal([$"BUILD-{historicalLabel}2"], DisplayNumbers(pageTwo));
        Assert.False(pageTwo.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Oversized_whitespace_and_missing_required_fields_fail_the_v2_contract()
    {
        var (projectId, client, factory) = await SeedAsync("PICKER105", ("1.0", true), ("1.5", false), ("2.0", true));
        using var _ = factory;
        var pageOne = await PageAsync(client, projectId, pageSize: 1);
        var cursor = pageOne.GetProperty("nextCursor").GetString()!;

        // Whitespace longer than the raw bound is rejected before being treated as a fresh page request.
        using var whitespace = await client.GetAsync(
            $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=1&cursor={Uri.EscapeDataString(new string(' ', 4097))}");
        Assert.Equal(HttpStatusCode.BadRequest, whitespace.StatusCode);
        Assert.Contains("invalid_cursor", await whitespace.Content.ReadAsStringAsync());

        var encoded = cursor.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))).RootElement;

        string Reencode(Action<IDictionary<string, string>> mutate)
        {
            var fields = json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetRawText());
            mutate(fields);
            var rebuilt = "{" + string.Join(',', fields.Select(field => $"\"{field.Key}\":{field.Value}")) + "}";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(rebuilt)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        async Task AssertRejectedAsync(string rawCursor)
        {
            using var response = await client.GetAsync(
                $"/api/managed-documents/link-options?projectId={projectId}&artifactType=Release&pageSize=1&cursor={Uri.EscapeDataString(rawCursor)}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_cursor", await response.Content.ReadAsStringAsync());
        }

        await AssertRejectedAsync(Reencode(fields => fields.Remove("SnapshotAt")));   // missing required field
        await AssertRejectedAsync(Reencode(fields => fields.Remove("CutoffOrdinal"))); // missing cutoff
    }

    [Fact]
    public async Task Release_ordinal_is_database_owned_across_ef_lifecycle_saves_on_sqlite()
    {
        var factory = new AeroLinkApiFactory();
        using var _ = factory;
        var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        Guid projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Picker ordinal program", "PICKERORD");
            var project = new ProjectRecord(program.Id, "Picker ordinal project", "Software");
            db.AddRange(program, project);
            await db.SaveChangesAsync();
            projectId = project.Id;

            var first = new SoftwareRelease(projectId, "1.0", true);
            var second = new SoftwareRelease(projectId, "1.1", false);
            db.AddRange(first, second);
            await db.SaveChangesAsync();
            // SQLite allocates MAX+1 per project inside the fenced write transaction, so absolute values
            // depend on the project's own history; the contract is distinct, positive, database-allocated
            // values read back after the INSERT.
            Assert.True(first.PickerInsertionOrdinal is > 0);
            Assert.True(second.PickerInsertionOrdinal is > 0);
            Assert.NotEqual(first.PickerInsertionOrdinal, second.PickerInsertionOrdinal);
            var ordinalAtInsert = second.PickerInsertionOrdinal;

            // Ordinary lifecycle save must not touch the membership ordinal.
            second.MarkReleased(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(ordinalAtInsert, second.PickerInsertionOrdinal);

            // A whole-entity update writes every mapped column yet still leaves the membership ordinal alone.
            var tracked = await db.Releases.SingleAsync(x => x.Id == second.Id);
            db.Update(tracked);
            await db.SaveChangesAsync();
            var afterWholeEntity = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == second.Id);
            Assert.Equal(ordinalAtInsert, afterWholeEntity.PickerInsertionOrdinal);
        }

        // A failed save followed by a corrected retry allocates a fresh per-project ordinal without harm.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var before = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
                .MaxAsync(x => x.PickerInsertionOrdinal);
            var conflicting = new SoftwareRelease(projectId, "1.1", false);
            db.Add(conflicting);
            await Assert.ThrowsAsync<AeroLink.Domain.Common.DomainException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var retry = new SoftwareRelease(projectId, "1.2", false);
            db.Add(retry);
            await db.SaveChangesAsync();
            Assert.True(retry.PickerInsertionOrdinal > before);
        }
    }
}
