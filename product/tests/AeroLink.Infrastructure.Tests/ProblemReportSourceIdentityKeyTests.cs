using System.Text;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AeroLink.Infrastructure.Tests;

// The existing importer compares Trimmed SourceKey with the real OrdinalIgnoreCase comparer, and
// Trimmed SourceSystem exactly. These primary codec tests own agreement with that independent oracle,
// not provider enforcement (which requires its own generated-column/unique-index proof).
public sealed class ProblemReportSourceIdentityKeyTests
{
    public static TheoryData<string, string, string> IdentityPairs => new()
    {
        { "ASCII", " PR-abc ", "PR-ABC" }, { "Latin", "é", "É" }, { "Greek", "ς", "Σ" }, { "Cyrillic", "я", "Я" },
        { "sharp S", "ß", "SS" }, { "dotted I", "İ", "i" }, { "dotless I", "ı", "I" }, { "normalization", "é", "e\u0301" },
        { "fullwidth", "ａ", "Ａ" }, { "long s", "ſ", "S" }, { "Deseret", "\U00010428", "\U00010400" },
        { "Garay", "\U00010D70", "\U00010D50" }, { "unpaired high", "\uD800", "\uFFFD" }, { "unpaired low", "\uDC00", "\uFFFD" },
        { "NUL", "a\0b", "AB" }, { "mixed", "p\U00010D70\uD800q", "P\U00010D50\uD800Q" },
    };

    [Theory]
    [MemberData(nameof(IdentityPairs))]
    public void Source_fields_match_the_existing_comparers_without_normalization(string label, string left, string right)
    {
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(left.Trim(), right.Trim()) ==
            ProblemReportSourceIdentityKey.SourceKey(left).AsSpan().SequenceEqual(ProblemReportSourceIdentityKey.SourceKey(right)), label);
        Assert.True(StringComparer.Ordinal.Equals(left.Trim(), right.Trim()) ==
            ProblemReportSourceIdentityKey.SourceSystem(left).AsSpan().SequenceEqual(ProblemReportSourceIdentityKey.SourceSystem(right)), label);
    }

    [Fact]
    public void Every_valid_scalar_agrees_in_both_directions_including_supplementary_classes()
    {
        ProblemReportSourceIdentityKey.EnsureCompatible();
        var comparerClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyClasses = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var scalar = 0; scalar <= 0x10ffff; scalar++)
        {
            if (!Rune.IsValid(scalar)) continue;
            // Surrounding non-whitespace keeps Trim from collapsing standalone whitespace scalars.
            var value = "[" + new Rune(scalar) + "]";
            var key = Convert.ToHexString(ProblemReportSourceIdentityKey.SourceKey(value));
            if (comparerClasses.TryGetValue(value, out var priorKey))
                Assert.True(key == priorKey, $"Equal comparer values split at U+{scalar:X}.");
            else comparerClasses.Add(value, key);
            if (keyClasses.TryGetValue(key, out var priorValue))
                Assert.True(StringComparer.OrdinalIgnoreCase.Equals(value, priorValue), $"Distinct comparer values merge at U+{scalar:X}.");
            else keyClasses.Add(key, value);
        }
    }

    [Fact]
    public void Every_UTF16_trim_boundary_matches_actual_Trim_for_both_source_fields()
    {
        var expectedKey = ProblemReportSourceIdentityKey.SourceKey("pr-X");
        var expectedSystem = ProblemReportSourceIdentityKey.SourceSystem("System");
        for (var character = 0; character <= char.MaxValue; character++)
        {
            var key = (char)character + "pr-X" + (char)character;
            var system = (char)character + "System" + (char)character;
            Assert.Equal(key.Trim() == "pr-X", expectedKey.AsSpan().SequenceEqual(ProblemReportSourceIdentityKey.SourceKey(key)));
            Assert.Equal(system.Trim() == "System", expectedSystem.AsSpan().SequenceEqual(ProblemReportSourceIdentityKey.SourceSystem(system)));
        }
    }

    [Fact]
    public void The_key_keeps_full_token_bytes_and_preserves_unpaired_units_and_NUL()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 65, 0, 0, 0xD8, 0, 0, 0, 0, 0, 0, 1, 4, 0 },
            ProblemReportSourceIdentityKey.SourceKey("a\uD800\0\U00010428"));
        Assert.Equal(new byte[] { 0, 0, 0, 97 }, ProblemReportSourceIdentityKey.SourceSystem(" a "));
    }

    [Fact]
    public void Frozen_keys_for_historical_reads_do_not_require_runtime_write_admission()
    {
        // Invariant globalization on .NET 10.0.9 newly equates U+1C8A/U+1C89. Historical v1 keys
        // retain their frozen distinction, even when explicit write admission refuses that context.
        if (StringComparer.OrdinalIgnoreCase.Equals("\u1c8a", "\u1c89"))
            Assert.Contains("writes are refused", Assert.Throws<InvalidOperationException>(
                ProblemReportSourceIdentityKey.EnsureCompatible).Message);
        Assert.Equal(new byte[] { 0, 0, 0x1c, 0x8a }, ProblemReportSourceIdentityKey.SourceKey(" \u1c8a "));
        Assert.Equal(new byte[] { 0, 0, 0x1c, 0x89 }, ProblemReportSourceIdentityKey.SourceKey("\u1c89"));
        Assert.Equal(new byte[] { 0, 0, 0x1c, 0x8a }, ProblemReportSourceIdentityKey.SourceSystem(" \u1c8a "));
    }

    [Fact]
    public void Sqlite_generated_key_uses_complete_TEXT_bytes_including_NUL_and_refuses_a_supplied_key()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        // The generated key takes complete TEXT bytes, including NUL. This native-provider boundary
        // needs a persisted-string oracle in addition to the scalar codec oracle.
        var strictUtf8 = new UTF8Encoding(false, true);
        connection.CreateFunction("source_key", (byte[] value) => ProblemReportSourceIdentityKey.SourceKey(strictUtf8.GetString(value)), isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE fixture (rawkey TEXT NOT NULL, sourcekey BLOB GENERATED ALWAYS AS (source_key(CAST(rawkey AS BLOB))) STORED UNIQUE);
            INSERT INTO fixture(rawkey) VALUES ($first),($second);
            """;
        const string first = "PR-\0a";
        const string second = "PR-\0b";
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT rawkey,sourcekey FROM fixture ORDER BY rowid";
        using (var rows = command.ExecuteReader())
        {
            foreach (var expected in new[] { first, second })
            {
                Assert.True(rows.Read());
                var stored = rows.GetString(0);
                Assert.Equal(expected, stored);
                Assert.Equal(ProblemReportSourceIdentityKey.SourceKey(stored), (byte[])rows[1]);
            }
            Assert.False(rows.Read());
        }
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(first, second));
        command.CommandText = "INSERT INTO fixture(rawkey) VALUES ($equal)";
        command.Parameters.AddWithValue("$equal", "pr-\0A");
        Assert.Equal(19, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        command.CommandText = "INSERT INTO fixture(rawkey,sourcekey) VALUES ('forged',NULL)";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Compatibility_guard_refuses_a_frozen_class_that_the_runtime_keeps_distinct()
    {
        var map = new Dictionary<int, int>(ProblemReportSourceIdentityTableV1.CaseMappings) { [0x17f] = 'S' };
        var error = Assert.Throws<InvalidOperationException>(() => ProblemReportSourceIdentityKey.ValidateCompatibility(
            "candidate-table", map, ProblemReportSourceIdentityTableV1.TrimCharacters));
        Assert.Contains("candidate-table", error.Message);
        Assert.Contains("U+17F", error.Message);
        Assert.Contains("writes are refused", error.Message);
    }

    [Theory]
    [InlineData(0x61)]
    [InlineData(0x10d70)]
    public void Compatibility_guard_refuses_missing_equivalences_even_when_each_mapping_is_individually_valid(int scalar)
    {
        var map = new Dictionary<int, int>(ProblemReportSourceIdentityTableV1.CaseMappings);
        Assert.True(map.Remove(scalar));
        var error = Assert.Throws<InvalidOperationException>(() => ProblemReportSourceIdentityKey.ValidateCompatibility(
            "candidate-table", map, ProblemReportSourceIdentityTableV1.TrimCharacters));
        Assert.Contains("runtime equates", error.Message);
        Assert.Contains($"U+{scalar:X}", error.Message);
    }

    [Fact]
    public void Compatibility_guard_refuses_a_changed_Trim_set_without_reinterpreting_stored_identities()
    {
        var trim = ProblemReportSourceIdentityTableV1.TrimCharacters.Where(character => character != 0x20).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => ProblemReportSourceIdentityKey.ValidateCompatibility(
            "candidate-table", ProblemReportSourceIdentityTableV1.CaseMappings, trim));
        Assert.Contains("Trim classification changed for U+0020", error.Message);
        Assert.Contains("without rewriting historical source fields", error.Message);
    }
}
