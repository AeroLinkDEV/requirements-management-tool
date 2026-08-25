namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Snippet-level regression tests for the revision-state tripwire scanner.
///
/// The repository-scan tripwire in <see cref="LegacyControlledProcedureDocumentSnapshotTests"/> proves today's
/// tree is clean; these tests prove the scanner itself still detects and still tolerates the right things, so
/// a future pattern edit cannot pass merely because the repository happens to contain no offender. Each case
/// is a minimal synthetic source snippet — the point is the textual shape, not compilability.
/// </summary>
public sealed class RevisionStateTripwireSnippetTests
{
    private static IReadOnlyList<string> Scan(string source, string relativePath = "") =>
        RevisionStateTripwire.Inspect(source, relativePath).Select(x => x.PatternName).ToList();

    private const string BulkUpdate = """
        await db.TestProcedureRevisions.Where(x => x.ProcedureId == procedureId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, v => "Approved"));
        """;

    private const string TypedEntriesAssignment = """
        foreach (var entry in db.ChangeTracker.Entries<TestProcedureRevision>())
        {
            entry.CurrentValues["State"] = "Approved";
        }
        """;

    private const string EntryPropertyAssignment = """
        var revision = await db.TestProcedureRevisions.SingleAsync();
        db.Entry(revision).Property(r => r.State).CurrentValue = "Approved";
        """;

    private const string EntryIndexerAssignment = """
        db.Entry(revision).CurrentValues["State"] = "Approved";
        """;

    private const string RawSqlRegularString = """
        migrationBuilder.Sql("UPDATE \"test_procedure_revisions\" SET \"State\" = 'Approved' WHERE \"ProcedureId\" = @p0");
        """;

    private const string RawSqlVerbatimString = """
        migrationBuilder.Sql(@"UPDATE ""test_procedure_revisions"" SET ""State"" = 'Approved' WHERE ""ProcedureId"" = @p0");
        """;

    private const string UpdateDataCall = """
        migrationBuilder.UpdateData(
            table: "test_procedure_revisions",
            keyColumn: "Id",
            keyValue: someId,
            column: "State",
            value: "Approved");
        """;

    private const string DynamicConcatenatedSql = """
        migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = 'Approved' WHERE \"Id\" = @p0");
        """;

    private const string DynamicInterpolatedSql = """
        migrationBuilder.Sql($"UPDATE {table} SET {column} = 'Approved' WHERE \"Id\" = @p0");
        """;

    private const string AuditedMigrationSource = """
        foreach (var (table, column) in ControlledIdentifierColumns())
            migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = regexp_replace(\"" + column + "\", '-00([0-9]{6})', '-\\1') WHERE \"" + column + "\" ~ '-00[0-9]{6}';");
        """;

    [Theory]
    [InlineData(BulkUpdate, "ExecuteUpdate/SetProperty")]
    [InlineData(TypedEntriesAssignment, "Change-tracker")]
    [InlineData(EntryPropertyAssignment, "Change-tracker")]
    [InlineData(EntryIndexerAssignment, "Change-tracker")]
    [InlineData(RawSqlRegularString, "Raw SQL")]
    [InlineData(RawSqlVerbatimString, "Raw SQL")]
    [InlineData(UpdateDataCall, "UpdateData")]
    public void Mutation_routes_are_detected(string source, string expectedPatternFragment)
    {
        var patterns = Scan(source);
        Assert.Contains(patterns, p => p.Contains(expectedPatternFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dynamic_migration_sql_is_surfaced_as_review_required()
    {
        var concatenated = Scan(DynamicConcatenatedSql);
        Assert.Contains(concatenated, p => p.Contains("review-required", StringComparison.OrdinalIgnoreCase));
        var interpolated = Scan(DynamicInterpolatedSql);
        Assert.Contains(interpolated, p => p.Contains("review-required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_audited_dynamic_migration_is_allowed_only_by_its_exact_path()
    {
        // Under its own file name the dynamic construction is suppressed: this migration's
        // ControlledIdentifierColumns() inventory (requirements, requirement_changes, test_procedures,
        // controlled_documents, requirement_specifications against BaseNumber/DocumentNumber) can never
        // touch test_procedure_revisions.State.
        Assert.Empty(Scan(AuditedMigrationSource, "src/AeroLink.Infrastructure/Persistence/Migrations/"
            + RevisionStateTripwire.AuditedDynamicSqlMigrationFiles.First()));
        // The allowance is load-bearing: the same source under any other path is surfaced for review.
        Assert.Contains(Scan(AuditedMigrationSource, "src/AeroLink.Infrastructure/Persistence/Migrations/SomeOtherMigration.cs"),
            p => p.Contains("review-required", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("""
        var prior = db.Entry(revision).Property(r => r.State).CurrentValue;
        """)]
    [InlineData("""
        if (db.Entry(revision).Property(r => r.State).CurrentValue == "Draft") { }
        """)]
    [InlineData("""
        db.Entry(job).Property(j => j.State).IsModified = true;
        """)]
    [InlineData("""
        db.Entry(job).Property(j => j.State).CurrentValue = "Running";
        """)]
    [InlineData("""
        db.Entry(problemReport).CurrentValues["State"] = "Closed";
        """)]
    [InlineData("""
        migrationBuilder.Sql("UPDATE \"test_procedure_revisions\" SET \"SomethingElse\" = 'x' WHERE \"State\" = 'Approved'");
        """)]
    [InlineData("""
        migrationBuilder.Sql("UPDATE \"test_procedure_revisions\" SET \"Note\" = \"State\"");
        """)]
    public void Non_mutating_or_unrelated_shapes_are_not_flagged(string source) =>
        Assert.Empty(Scan(source));

    [Fact]
    public void Each_audited_dynamic_site_exists_in_the_real_tree_and_is_handled_by_path()
    {
        // Every allowlist entry must stay load-bearing against the real repository shape, not a paraphrase:
        // the actual migration file fires the review-required pattern under any other name, and is silent
        // only under its own. This also fails when a migration is renamed, forcing the allowlist to follow.
        var root = RevisionStateTripwire.LocateProductRoot();
        Assert.NotEmpty(RevisionStateTripwire.AuditedDynamicSqlMigrationFiles);
        foreach (var fileName in RevisionStateTripwire.AuditedDynamicSqlMigrationFiles)
        {
            var relative = "src/AeroLink.Infrastructure/Persistence/Migrations/" + fileName;
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Allowlisted migration '{fileName}' is missing from the tree; prune or move the allowlist entry.");
            var source = File.ReadAllText(path);
            Assert.Contains(RevisionStateTripwire.Inspect(source, "Migrations/SomeOtherFile.cs"),
                x => x.PatternName.Contains("review-required", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(RevisionStateTripwire.Inspect(source, relative));
        }
    }

    [Fact]
    public void The_allowlist_is_narrow_a_literal_state_update_inside_an_allowlisted_file_still_fires()
    {
        var allowlistedPath = "src/AeroLink.Infrastructure/Persistence/Migrations/"
            + RevisionStateTripwire.AuditedDynamicSqlMigrationFiles.First();
        Assert.Contains(
            Scan(RawSqlRegularString, allowlistedPath),
            p => p.Contains("Raw SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sql_arithmetic_inside_a_literal_is_not_dynamic_construction()
    {
        // The concat markers require the plus to hug a string-literal delimiter, so ordinary SQL arithmetic
        // inside a literal — `+ INTERVAL`, `+ 1` — is not surfaced. A migration whose UPDATE is genuinely
        // built by concatenation still fires (see Dynamic_migration_sql_is_surfaced_as_review_required).
        var arithmetic = """
            migrationBuilder.Sql("UPDATE artifact_edit_sessions SET \"ExpiresAt\" = \"UpdatedAt\" + INTERVAL '15 minutes'");
            """;
        Assert.DoesNotContain(Scan(arithmetic), p => p.Contains("review-required", StringComparison.OrdinalIgnoreCase));
        var numeric = """
            migrationBuilder.Sql("UPDATE \"project_ladder_configurations\" SET \"Version\" = \"Version\" + 1");
            """;
        Assert.DoesNotContain(Scan(numeric), p => p.Contains("review-required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skip_rules_exclude_build_output_tests_and_operator_scratch()
    {
        Assert.True(RevisionStateTripwire.ShouldSkip("src/AeroLink.Api/bin/Debug/x.cs"));
        Assert.True(RevisionStateTripwire.ShouldSkip("src/AeroLink.Api/obj/Release/x.cs"));
        Assert.True(RevisionStateTripwire.ShouldSkip("tests/AeroLink.Infrastructure.Tests/x.cs"));
        Assert.True(RevisionStateTripwire.ShouldSkip("client/tests/helpers/x.cs"));
        Assert.True(RevisionStateTripwire.ShouldSkip(".local/pg/data/x.cs"));
        // A clone checked out under a directory named bin must not make every path skip.
        Assert.False(RevisionStateTripwire.ShouldSkip("src/AeroLink.Api/Program.cs"));
    }
}
