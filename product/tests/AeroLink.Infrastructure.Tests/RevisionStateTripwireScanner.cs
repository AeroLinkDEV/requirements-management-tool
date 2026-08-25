using System.Text.RegularExpressions;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The scanning half of the legacy controlled-document tripwire, extracted so the patterns themselves can be
/// regression-tested against synthetic snippets rather than only against whatever happens to be in the tree
/// today.
///
/// What this is: a tripwire and a backstop. It exists so the likely accident — a state backfill copy-pasted
/// from the migration next door — fails loudly. It is not a proof that `TestProcedureRevision.State` is
/// immutable; a determined rewrite evades any textual check, and the invariant itself rests on review. The
/// reconstruction site's note says the same thing; keep the two in agreement.
///
/// What it deliberately is not: a C# dataflow engine. Dynamic migration SQL whose table/column names are
/// built by concatenation or interpolation cannot be resolved by a regex, so such construction is classified
/// as review-required and surfaced, with a single narrow, path-specific allowlist for the one already-audited
/// repository site. The allowlist is load-bearing only for the dynamic-SQL pattern; a literal
/// `test_procedure_revisions … SET … State =` in an allowlisted file is still flagged.
/// </summary>
internal static class RevisionStateTripwire
{
    public sealed record Finding(string PatternName, int Index);

    /// <summary>
    /// The audited dynamic-SQL sites. The dynamic pattern is deliberately trigger-happy — it cannot resolve
    /// C# string concatenation, and SQL arithmetic inside a literal (for example `+ INTERVAL` or `+ 1`) also
    /// matches — so every site it surfaces gets read by a human and either fixed or allowlisted here by
    /// exact file name, with the reason it can never mutate test_procedure_revisions.State:
    ///
    ///   * ShortenRequirementAndDocumentIdentifiers — its ControlledIdentifierColumns() inventory is
    ///     requirements | requirement_changes | test_procedures | controlled_documents |
    ///     requirement_specifications, always against BaseNumber or DocumentNumber.
    ///   * AddRichAuthoredContent — builds UPDATE system_change_requests SET …Rich over the fixed column
    ///     list Problem | Analysis | Solution.
    ///
    /// The allowance is keyed to exact migration file names and applies only to the dynamic-SQL pattern; a
    /// literal `test_procedure_revisions … SET … State =` in an allowlisted file is still flagged. A new
    /// dynamic site — including a copy of an allowlisted one under a new name — is surfaced for review.
    /// </summary>
    public static readonly IReadOnlySet<string> AuditedDynamicSqlMigrationFiles = new HashSet<string>(
        [
            "20260718174806_ShortenRequirementAndDocumentIdentifiers.cs",
            "20260725210752_AddRichAuthoredContent.cs",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>True only for an exact audited migration file name; never a suffix or prefix match.</summary>
    public static bool IsAllowlistedDynamicSite(string relativePath) =>
        AuditedDynamicSqlMigrationFiles.Contains(Path.GetFileName(relativePath.TrimEnd('/', '\\')));

    // EF bulk update, via either the DbSet property or Set<TestProcedureRevision>(). The type name must
    // appear within the window; an unrelated SetProperty on some other entity does not match.
    private static readonly Regex ExecuteUpdate = new(
        @"TestProcedureRevision[\s\S]{0,2000}?SetProperty\s*\([^)]*\.State\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Change-tracker writes. Two branches, both requiring a genuine assignment anchor: an equals sign that
    // is not part of ==, !=, <=, >= or =>, because reading or comparing .CurrentValue must never flag.
    //
    //   * Entries<TestProcedureRevision> is statically typed and matched directly.
    //   * Entry(...) cannot be typed textually, so this branch requires the entity identifier inside the
    //     call to name the revision (revision / procedureRevision / testProcedureRevision …). A differently
    //     named variable of this type is out of textual reach — that is the tripwire trade-off, not a
    //     guarantee. Unrelated entities (job, problemReport, alert …) do not match.
    // CurrentValues["State"] is in scope on both branches; the bracket assignment is one of the anchors.
    private static readonly Regex ChangeTrackerAssignment = new(
        @"(?:Entries\s*<\s*TestProcedureRevision\s*>|Entry\s*\(\s*\w*revision\w*)[\s\S]{0,600}?
            (?:Property\s*\([^)]*(?:\.State\b|""State"")|CurrentValues\s*\[\s*""State""\s*\])
            [\s\S]{0,20}?(?<![=!<>])=(?![=>])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    // migrationBuilder.UpdateData emits no UPDATE token at all, so it needs its own pattern. Argument order
    // is not assumed beyond table/column both appearing within the window.
    private static readonly Regex UpdateData = new(
        @"UpdateData\s*\([\s\S]{0,500}?test_procedure_revisions[\s\S]{0,300}?(?:column:\s*)?""State""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Raw SQL assigning the column, with or without schema qualification, in either quote spelling (the
    // caller collapses \" and "" before this runs). The tempered SET…State span stops at WHERE, so a
    // legitimate SET "SomethingElse" = x … WHERE "State" = y is not flagged.
    private static readonly Regex RawSqlUpdate = new(
        @"UPDATE\s+(?:""?\w+""?\s*\.\s*)?""?test_procedure_revisions""?[\s\S]{0,400}?SET((?!WHERE)[\s\S]){0,300}?""?State""?\s*(?<![=!<>])=(?![=>])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Dynamic migration SQL: a migrationBuilder.Sql invocation whose statement shows UPDATE together with
    // genuine C# string concatenation or interpolation, so the table or column name is built at runtime.
    // The concat markers require the plus to hug a string-literal delimiter — SQL arithmetic inside a
    // literal (`+ INTERVAL`, `+ 1`) does not match, because its plus never sits against a C# quote with a
    // non-numeric operand and either a continuing concat or the closing parenthesis after it. This cannot
    // know what the fragments resolve to, so it is review-required rather than a verdict — except on the
    // allowlisted audited migrations above. The [^;] spans keep the lookaheads inside one statement.
    private static readonly Regex DynamicSqlConstruction = new(
        @"migrationBuilder\s*\.\s*Sql\s*\(
            (?=[^;]{0,1000}?\bUPDATE\b)
            (?=[^;]{0,1000}?(?:
                ""\s*\+\s*(?!\d)\w+\s*[+)]
                |\w+\s*\+\s*""
                |\$""))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    /// <summary>Scans one source text and reports every pattern hit with a stable pattern name.</summary>
    public static IReadOnlyList<Finding> Inspect(string source, string relativePath = "")
    {
        // Both ways C# embeds a quote in a string literal are collapsed so the SQL patterns can see the
        // column name: a verbatim string doubles it (UPDATE ""test_procedure_revisions""), a regular string
        // escapes it (UPDATE \"test_procedure_revisions\"). The escaped form is what this repository's
        // migrations overwhelmingly use.
        var text = source.Replace("\\\"", "\"").Replace("\"\"", "\"");
        var findings = new List<Finding>();
        if (ExecuteUpdate.IsMatch(text)) findings.Add(new Finding("ExecuteUpdate/SetProperty on TestProcedureRevision.State", IndexOf(text, ExecuteUpdate)));
        if (ChangeTrackerAssignment.IsMatch(text)) findings.Add(new Finding("Change-tracker State assignment via CurrentValue/CurrentValues", IndexOf(text, ChangeTrackerAssignment)));
        if (UpdateData.IsMatch(text)) findings.Add(new Finding("migrationBuilder.UpdateData on test_procedure_revisions.State", IndexOf(text, UpdateData)));
        if (RawSqlUpdate.IsMatch(text)) findings.Add(new Finding("Raw SQL UPDATE of test_procedure_revisions.State", IndexOf(text, RawSqlUpdate)));
        // The dynamic-SQL pattern is review-required: it fires on unresolvable construction, so the audited
        // migrations are allowed — by exact file name, and only for this pattern.
        if (DynamicSqlConstruction.IsMatch(text) && !IsAllowlistedDynamicSite(relativePath))
            findings.Add(new Finding("Dynamic/concatenated migration UPDATE construction (review-required)", IndexOf(text, DynamicSqlConstruction)));
        return findings;
    }

    /// <summary>
    /// Path segments that remove a file from the scan. "tests" excludes every tests directory under product
    /// (product/tests, ci-metrics/tests, client/tests, test-contracts/tests, test-planner/tests) because test
    /// code may legitimately construct states the domain cannot express. ".local" is operator scratch and is
    /// excluded before enumeration reaches it, not filtered by extension afterwards.
    /// </summary>
    public static bool ShouldSkip(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj") || segments.Contains("tests")
            || segments.Contains(".local") || segments.Contains(".git");
    }

    /// <summary>Walks the product tree and returns every file with at least one finding.</summary>
    public static (int Scanned, List<(string RelativePath, IReadOnlyList<Finding> Findings)> Offenders) ScanTree(string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var scanned = 0;
        var offenders = new List<(string, IReadOnlyList<Finding>)>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file[prefix.Length..] : file;
            // Scope the skip decision to the path BELOW the scan root, so a clone living under a directory
            // named bin or obj cannot skip every file and pass vacuously.
            if (ShouldSkip(relative)) continue;
            scanned++;
            var findings = Inspect(File.ReadAllText(file), relative);
            if (findings.Count > 0) offenders.Add((relative, findings));
        }
        return (scanned, offenders);
    }

    private static int IndexOf(string text, Regex regex) =>
        regex.Match(text) is { Success: true } match ? match.Index : -1;

    /// <summary>
    /// Locates the product/ tree by walking up from the test assembly, so the repository scan is
    /// path-independent. The whole product tree is scanned, not just src: AeroLink.Scale and
    /// AeroLink.DocumentConnector ship too and reference AeroLinkDbContext.
    /// </summary>
    public static string LocateProductRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "product");
            if (Directory.Exists(Path.Combine(candidate, "src"))) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the product tree from the test assembly location.");
    }
}
