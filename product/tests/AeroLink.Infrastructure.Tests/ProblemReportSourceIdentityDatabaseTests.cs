using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroLink.Infrastructure.Tests;

// Authoring gate: raw provider inserts must not omit or forge the source-key backstop. Pure comparer
// tests cannot protect generated-column SQL, existing-open connection registration, encoding or schema upgrades.
public sealed class ProblemReportSourceIdentityDatabaseTests
{
    internal static readonly (string A, string B)[] Pairs = [
        ("Nav", "NAV"), ("longſ", "LONGS"), ("ı", "I"), ("İ", "i"), ("K", "K"), ("ß", "SS"),
        ("é", "e\u0301"), ("\u2000 x \u3000", "x"), ("Σ", "ς"),
        (char.ConvertFromUtf32(0x10400), char.ConvertFromUtf32(0x10428)),
        (char.ConvertFromUtf32(0x10D50), char.ConvertFromUtf32(0x10D70)),
        ("A\0B", "a\0b"), ("A\0B", "a\0c"),
    ];

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task Supplied_closed_connection_opened_by_its_owner_uses_the_actual_persisted_encoding(string encoding)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-source-connection-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await Execute(connection, $"PRAGMA encoding='{encoding}'; CREATE TABLE original(raw TEXT); INSERT INTO original VALUES ('Key');");
            await connection.CloseAsync();
            await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            // Native Open deliberately bypasses EF's ConnectionOpened interceptor.
            await connection.OpenAsync();
            var key = await db.Database.SqlQueryRaw<string>("SELECT hex(aerolink_source_identity_v1(CAST(raw AS BLOB),1,CAST('A' AS BLOB))) AS \"Value\" FROM original").SingleAsync();
            Assert.Equal("0000004B0000004500000059", key);
            await Execute(connection, """
                CREATE TABLE derived (raw TEXT, identity BLOB GENERATED ALWAYS AS (aerolink_source_identity_v1(CAST(raw AS BLOB),1,CAST('A' AS BLOB))) VIRTUAL UNIQUE);
                INSERT INTO derived(raw) VALUES ('Key');
                """);
            await connection.CloseAsync(); await connection.OpenAsync();
            // First SQL after reopening an existing generated schema must already have deterministic callbacks.
            Assert.Equal("0000004B0000004500000059", await db.Database.SqlQueryRaw<string>("SELECT hex(identity) AS \"Value\" FROM derived").SingleAsync());
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => Execute(connection,
                "INSERT INTO derived(raw) VALUES ('KEY')"))).SqliteErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task Database_derived_keys_match_the_independent_comparer_and_refuse_omitted_or_forged_keys(string encoding)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await Execute(connection, $"PRAGMA encoding='{encoding}'");
        // Supplied and already-open: no Opened event occurs when this context starts using the connection.
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var project = await SeedProject(db);
        var sequence = 0;
        foreach (var pair in Pairs)
        {
            var system = $"Case{sequence}";
            var a = Imported(project, $"PR-{++sequence}", system, pair.A);
            var b = Imported(project, $"PR-{++sequence}", system, pair.B);
            await InsertRaw(db, a);
            var equivalent = StringComparer.OrdinalIgnoreCase.Equals(pair.A.Trim(), pair.B.Trim());
            if (equivalent) Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => InsertRaw(db, b))).SqliteErrorCode);
            else await InsertRaw(db, b);
            Assert.Equal(equivalent ? 1 : 2, await db.ProblemReports.CountAsync(x => x.ProjectId == project && x.SourceSystem == system));
        }
        // SourceSystem is exact after Trim and never case folded.
        await InsertRaw(db, Imported(project, $"PR-{++sequence}", "Jira", "Key"));
        await InsertRaw(db, Imported(project, $"PR-{++sequence}", "jira", "key"));
        var normal = new ProblemReport(project, $"PR-{++sequence}", "Native", "Native problem", "", "author", DateTimeOffset.UtcNow);
        await InsertRaw(db, normal);
        var nativeKeys = await db.ProblemReports.Where(x => x.Id == normal.Id)
            .Select(x => new { System = EF.Property<byte[]>(x, "SourceSystemIdentityV1"), Key = EF.Property<byte[]>(x, "SourceKeyIdentityV1") }).SingleAsync();
        Assert.Null(nativeKeys.System); Assert.Null(nativeKeys.Key);
        var forged = await Assert.ThrowsAsync<SqliteException>(() => InsertRaw(db,
            Imported(project, $"PR-{++sequence}", "Jira", "Forged"), suppliedKey: [1]));
        Assert.Contains("generated column", forged.Message);
        foreach (var malformed in new (object? System, object? Key)[] { (null, "Partial"), ("Jira", null), (" ", "Empty"), (" Jira", "Untrimmed"), ("Jira", "Key\u3000") })
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => InsertRaw(db,
                Imported(project, $"PR-{++sequence}", "Valid", "Valid"), malformed.System, malformed.Key, overrideSource: true))).SqliteErrorCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("partial")]
    [InlineData("untrimmed")]
    [InlineData("encoding")]
    public async Task Existing_local_schema_upgrade_refuses_conflicts_and_invalid_shape_without_rewriting_history(string invalid)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await Execute(connection, """
            CREATE TABLE problem_reports ("Id" TEXT PRIMARY KEY, "ProjectId" TEXT, "SourceSystem" TEXT, "SourceKey" TEXT, "Revision" INTEGER, "SourceState" TEXT);
            CREATE TABLE problem_report_import_batches ("Id" TEXT PRIMARY KEY, "PreviewHash" TEXT, "ImportedBy" TEXT);
            CREATE TABLE original_evidence ("Id" TEXT PRIMARY KEY, "SnapshotJson" TEXT, "SnapshotHash" TEXT, "SignatureHash" TEXT);
            INSERT INTO problem_reports VALUES ('original','project','Jira','longſ',7,'Closed');
            INSERT INTO original_evidence VALUES ('original','{"revision":7}','original-snapshot-hash','original-signature-hash');
            INSERT INTO problem_report_import_batches VALUES ('unsigned','original-preview-hash','original-actor');
            """);
        await Execute(connection, invalid switch
        {
            "duplicate" => "INSERT INTO problem_reports VALUES ('conflict','project','Jira','LONGſ',2,'Open')",
            "partial" => "INSERT INTO problem_reports VALUES ('conflict','project',NULL,'Partial',2,'Open')",
            "encoding" => "INSERT INTO problem_reports VALUES ('conflict','project','Jira',CAST(X'FF' AS TEXT),2,'Open')",
            _ => "INSERT INTO problem_reports VALUES ('conflict','project',' Jira','Key',2,'Open')",
        });
        var before = await Inventory(connection);
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => ProblemReportImportSqliteGuard.EnsureInstalledAsync(db));
        Assert.Contains("conflict", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await Inventory(connection));
        await using var columns = connection.CreateCommand(); columns.CommandText = "SELECT count(*) FROM pragma_table_xinfo('problem_reports') WHERE name='SourceKeyIdentityV1'";
        Assert.Equal(0L, await columns.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Existing_local_schema_upgrade_preserves_history_and_enforces_new_raw_writes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await Execute(connection, """
            CREATE TABLE problem_reports ("Id" TEXT PRIMARY KEY, "ProjectId" TEXT, "SourceSystem" TEXT, "SourceKey" TEXT, "Revision" INTEGER, "SourceState" TEXT);
            CREATE TABLE problem_report_import_batches ("Id" TEXT PRIMARY KEY, "ProjectId" TEXT, "PreviewHash" TEXT, "ImportedBy" TEXT);
            CREATE TABLE original_evidence ("Id" TEXT PRIMARY KEY, "SnapshotJson" TEXT, "SnapshotHash" TEXT, "SignatureHash" TEXT);
            INSERT INTO problem_reports VALUES ('original','project','Jira','longſ',7,'Closed');
            INSERT INTO original_evidence VALUES ('original','{"revision":7}','original-snapshot-hash','original-signature-hash');
            INSERT INTO problem_report_import_batches VALUES ('unsigned','project','original-preview-hash','original-actor');
            """);
        var before = await Inventory(connection);
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        await ProblemReportImportSqliteGuard.EnsureInstalledAsync(db);
        Assert.Equal(before, await Inventory(connection));
        await ProblemReportImportSqliteGuard.EnsureInstalledAsync(db);
        Assert.Equal(before, await Inventory(connection));
        Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => Execute(connection,
            "UPDATE problem_report_import_batches SET \"PreviewHash\"='rewritten' WHERE \"Id\"='unsigned'"))).SqliteErrorCode);
        Assert.Equal(before, await Inventory(connection));
        Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => Execute(connection,
            "INSERT INTO problem_reports (\"Id\",\"ProjectId\",\"SourceSystem\",\"SourceKey\",\"Revision\",\"SourceState\") VALUES ('duplicate','project','Jira','LONGſ',1,'Open')"))).SqliteErrorCode);
        // long s and ASCII S are distinct under the retained .NET identity contract.
        await Execute(connection, "INSERT INTO problem_reports (\"Id\",\"ProjectId\",\"SourceSystem\",\"SourceKey\",\"Revision\",\"SourceState\") VALUES ('distinct','project','Jira','LONGS',1,'Open')");
        await using var unsigned = connection.CreateCommand();
        unsigned.CommandText = "SELECT count(*) FROM problem_report_import_batches WHERE \"Id\"='unsigned' AND \"OperationId\" IS NULL AND \"ActorId\" IS NULL AND \"RequestHash\" IS NULL AND \"ReceiptJson\" IS NULL";
        Assert.Equal(1L, await unsigned.ExecuteScalarAsync());
    }

    internal static async Task<Guid> SeedProject(AeroLinkDbContext db)
    {
        var program = new ProgramRecord("Source identity", "S" + Guid.NewGuid().ToString("N")[..20]);
        var project = new ProjectRecord(program.Id, "Source identity", "Source identity");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    internal static ProblemReport Imported(Guid project, string number, string system, string key) =>
        ProblemReport.Import(project, number, "Source report", "Original source problem", "", "author", "author", DateTimeOffset.UtcNow,
            ProblemReportSeverity.Major, ProblemReportPriority.Normal, ProblemReportCategory.CodeFunctional, null,
            system, key, "source actor", DateTimeOffset.Parse("2024-01-02T03:04:05Z"), "Closed", ProblemReportState.Closed, true);

    internal static async Task InsertRaw(AeroLinkDbContext db, ProblemReport report, object? system = null,
        object? key = null, bool overrideSource = false, byte[]? suppliedKey = null)
    {
        var entry = db.Entry(report);
        var table = StoreObjectIdentifier.Table("problem_reports", null);
        var properties = entry.Metadata.GetProperties().Where(x => x.GetComputedColumnSql() is null).ToArray();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var names = new List<string>();
        foreach (var property in properties)
        {
            var value = overrideSource && property.Name == nameof(ProblemReport.SourceSystem) ? system
                : overrideSource && property.Name == nameof(ProblemReport.SourceKey) ? key : entry.Property(property.Name).CurrentValue;
            var parameter = property.GetRelationalTypeMapping().CreateParameter(command, $"p{names.Count}", value, property.IsNullable);
            command.Parameters.Add(parameter); names.Add(property.GetColumnName(table)!);
        }
        if (suppliedKey is not null)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = $"p{names.Count}"; parameter.Value = suppliedKey;
            command.Parameters.Add(parameter); names.Add("SourceKeyIdentityV1");
        }
        command.CommandText = $"INSERT INTO problem_reports ({string.Join(',', names.Select(x => $"\"{x}\""))}) VALUES ({string.Join(',', names.Select((_, i) => $"@p{i}"))})";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Inventory(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_group_array(json_array(\"Id\",\"ProjectId\",hex(CAST(\"SourceSystem\" AS BLOB)),hex(CAST(\"SourceKey\" AS BLOB)),\"Revision\",\"SourceState\")) FROM problem_reports";
        var reports = (string)(await command.ExecuteScalarAsync())!;
        command.CommandText = "SELECT json_group_array(json_array(\"Id\",\"SnapshotJson\",\"SnapshotHash\",\"SignatureHash\")) FROM original_evidence";
        var evidence = (string)(await command.ExecuteScalarAsync())!;
        command.CommandText = "SELECT json_group_array(json_array(\"Id\",\"PreviewHash\",\"ImportedBy\")) FROM problem_report_import_batches";
        return reports + evidence + (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task Execute(SqliteConnection connection, string sql)
    { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
}
