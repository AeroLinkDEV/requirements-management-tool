using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// A collision-free database key for the existing imported-source identity rule. Original source fields
/// remain source facts. Frozen comparer classes are checked against the active runtime before use;
/// changing runtime/globalization semantics must never silently reinterpret historical identities.
/// </summary>
internal static class ProblemReportSourceIdentityKey
{
    private static readonly Lazy<bool> Compatible = new(() =>
    {
        ValidateCompatibility(ProblemReportSourceIdentityTableV1.Version,
            ProblemReportSourceIdentityTableV1.CaseMappings, ProblemReportSourceIdentityTableV1.TrimCharacters);
        return true;
    });

    public static byte[] SourceKey(string value) => Encode(value, foldCase: true);
    public static byte[] SourceSystem(string value) => Encode(value, foldCase: false);
    public static void EnsureCompatible() => _ = Compatible.Value;

    private static byte[] Encode(string value, bool foldCase)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureCompatible();
        var text = value.Trim();
        // Four bytes per scalar (or preserved unpaired UTF-16 code unit), never a digest or process hash.
        var bytes = new byte[text.Length * sizeof(int)];
        var written = 0;
        for (var index = 0; index < text.Length; index++)
        {
            int scalar = text[index];
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                scalar = char.ConvertToUtf32(text[index], text[++index]);
            // EnumerateRunes replaces malformed UTF-16 with U+FFFD. Identity must preserve those units.
            if (foldCase && ProblemReportSourceIdentityTableV1.CaseMappings.TryGetValue(scalar, out var representative))
                scalar = representative;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(written, sizeof(int)), scalar);
            written += sizeof(int);
        }
        return bytes[..written];
    }

    /// <summary>Validates both directions of equivalence, including classes newly introduced by a runtime.</summary>
    internal static void ValidateCompatibility(string version, IReadOnlyDictionary<int, int> mappings,
        IReadOnlyList<int> trimCharacters)
    {
        var classes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in mappings)
            if (!Rune.IsValid(source) || !Rune.IsValid(target))
                Refuse(version, $"invalid scalar mapping U+{source:X} to U+{target:X}");
        for (var scalar = 0; scalar <= 0x10ffff; scalar++)
        {
            if (!Rune.IsValid(scalar)) continue;
            var value = new Rune(scalar).ToString();
            var representative = mappings.TryGetValue(scalar, out var mapped) ? mapped : scalar;
            if (!StringComparer.OrdinalIgnoreCase.Equals(value, new Rune(representative).ToString()))
                Refuse(version, $"frozen class U+{representative:X} no longer equals U+{scalar:X}");
            if (classes.TryGetValue(value, out var priorRepresentative))
            {
                if (priorRepresentative != representative)
                    Refuse(version, $"runtime equates U+{scalar:X} with class U+{priorRepresentative:X}, but the frozen key uses U+{representative:X}");
            }
            else classes.Add(value, representative);
        }
        var trim = trimCharacters.ToHashSet();
        for (var character = 0; character <= char.MaxValue; character++)
        {
            var runtimeTrims = ((char)character + "x" + (char)character).Trim() == "x";
            if (runtimeTrims != trim.Contains(character))
                Refuse(version, $"Trim classification changed for U+{character:X4}");
        }
    }

    private static void Refuse(string version, string cause) => throw new InvalidOperationException(
        $"Problem Report source identity table {version} is incompatible with .NET {Environment.Version} " +
        $"(invariant sort {CultureInfo.InvariantCulture.CompareInfo.Version.FullVersion}): {cause}. " +
        "Source identity writes are refused; qualify a versioned compatibility transition without rewriting historical source fields.");
}
