using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AeroLink.Domain.Common;

namespace AeroLink.Infrastructure.Persistence;

public sealed record IntegrityMember(string Path, long Size, string Sha256, string Kind);
public sealed record IntegrityScope(string[] Projects, string[] Types, string[] ItemIds, string InventoryAcceptedBy,
    string FreezeReference, string HistoryDepth, string RelationshipPolicy, string AttachmentDepth);
public sealed record IntegrityManifest(int Version, Guid SourceInstanceId, string SourceName, string ServerVersion,
    string Query, string CapturedAt, string EvidenceMode, string Completeness, IntegrityScope Scope,
    IntegrityMember[] Members, string[] Findings);
public sealed record IntegrityField(string Name, string Type, JsonElement Value, string Representation = "text", string? SourceDisplay = null);
public sealed record IntegrityAttachment(string Field, string Name, string Path, string ContentType,
    string? SourceAuthor = null, string? SourceDate = null, string? SourceUri = null);
public sealed record IntegritySourceItem(string Id, string Project, string Type, string Snapshot,
    IntegrityField[] Fields, JsonElement History, JsonElement Annotations, JsonElement Relationships,
    IntegrityAttachment[] Attachments, string[] Findings);
public sealed record IntegritySourceDate(string Raw, string Meaning, DateTimeOffset? Instant, string? Finding,
    string Representation = "text");

/// <summary>
/// Reads the versioned package described in INTEGRITY_IMPORT.md. No archive member is extracted to a path,
/// no URI is fetched, and all members are verified before any controlled write is permitted (#1186 §11).
/// </summary>
public sealed class IntegritySourcePackage
{
    public const int MaximumBytes = 50 * 1024 * 1024;
    public const int MaximumExpandedBytes = 100 * 1024 * 1024;
    public const int MaximumMembers = 10_000;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 48, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public IntegrityManifest Manifest { get; }
    public string ManifestHash { get; }
    public string PackageHash { get; }
    public IReadOnlyDictionary<string, byte[]> Members { get; }
    public IReadOnlyList<(string Path, IntegritySourceItem Item)> Items { get; }
    private IntegritySourcePackage(IntegrityManifest manifest, string root, string packageHash,
        Dictionary<string, byte[]> members, List<(string, IntegritySourceItem)> items)
        => (Manifest, ManifestHash, PackageHash, Members, Items) = (manifest, root, packageHash, members, items);

    public static IntegritySourcePackage Read(byte[] bytes, string expectedManifestHash, bool allowFixtures)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new DomainException("Choose an Integrity package of at most 50 MB.");
        if (!Regex.IsMatch(expectedManifestHash ?? "", "\\A[0-9a-fA-F]{64}\\z"))
            throw new DomainException("Enter the manifest SHA-256 recorded when the package was acquired.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            if (zip.Entries.Count is 0 or > MaximumMembers) throw new DomainException("The package member count is invalid.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                ValidatePath(entry.FullName);
                if (!names.Add(entry.FullName)) throw new DomainException("The package has duplicate or colliding paths.");
                // Regular files only; directory entries, links and other Unix special-file modes are refused.
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if (mode != 0 && mode != 0x8000) throw new DomainException("The package contains a non-regular member.");
                total = checked(total + entry.Length);
                if (entry.Length > MaximumExpandedBytes || total > MaximumExpandedBytes)
                    throw new DomainException("The expanded package exceeds 100 MB.");
                using var input = entry.Open();
                using var output = new MemoryStream();
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    if (output.Length + read > entry.Length) throw new DomainException("An archive member exceeds its declared size.");
                    output.Write(buffer, 0, read);
                }
                if (output.Length != entry.Length) throw new DomainException("An archive member is incomplete.");
                contents.Add(entry.FullName, output.ToArray());
            }
            if (!contents.TryGetValue("manifest.json", out var manifestBytes)) throw new DomainException("The package has no manifest.");
            var root = Hash(manifestBytes);
            if (!root.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The manifest hash does not match the independently recorded hash.");
            RejectDuplicateProperties(manifestBytes);
            var manifest = JsonSerializer.Deserialize<IntegrityManifest>(manifestBytes, Json)
                ?? throw new DomainException("The manifest is missing.");
            if (manifest.Version != 1) throw new DomainException("This Integrity package version is not supported.");
            if (manifest.SourceInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(manifest.SourceName)
                || string.IsNullOrWhiteSpace(manifest.ServerVersion) || string.IsNullOrWhiteSpace(manifest.Query))
                throw new DomainException("The package must identify its source server, version and query.");
            if (ReadDate(manifest.CapturedAt).Meaning != "explicit-offset") throw new DomainException("The capture time needs an explicit offset and microsecond precision.");
            if (manifest.EvidenceMode != "qualified" && !(allowFixtures && manifest.EvidenceMode == "fixture"))
                throw new DomainException("Live server qualification is pending. Fixture packages are accepted only on a disposable fixture-enabled host.");
            if (manifest.Completeness != "complete-within-scope" || manifest.Findings is null || manifest.Findings.Length != 0)
                throw new DomainException("The first version refuses incomplete packages, exclusions and extraction losses.");
            var scope = manifest.Scope;
            if (scope is null || scope.Projects is not { Length: > 0 } || scope.Types is not { Length: > 0 }
                || scope.ItemIds is not { Length: > 0 } || string.IsNullOrWhiteSpace(scope.InventoryAcceptedBy)
                || string.IsNullOrWhiteSpace(scope.FreezeReference))
                throw new DomainException("The package requires an accepted scope inventory and a source-freeze reference.");
            if (scope.HistoryDepth != "full-audit-history" || scope.RelationshipPolicy != "preserve-references-without-following"
                || scope.AttachmentDepth != "current-and-any-admin-inventoried-historical-bytes")
                throw new DomainException("The package must declare the supported history, relationship and attachment scope.");
            if (manifest.Members is null || manifest.Members.Length != contents.Count - 1)
                throw new DomainException("The manifest must name every package member exactly once.");
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in manifest.Members)
            {
                if (member is null) throw new DomainException("A manifest member is missing.");
                ValidatePath(member.Path);
                if (member.Path == "manifest.json" || !listed.Add(member.Path)) throw new DomainException("The manifest contains duplicate members.");
                if (!contents.TryGetValue(member.Path, out var content) || member.Size != content.LongLength || Hash(content) != member.Sha256)
                    throw new DomainException($"Package member failed its size/hash check: {member.Path}");
                if (member.Kind is not ("item" or "attachment" or "schema" or "users" or "capabilities"))
                    throw new DomainException("An unknown member kind cannot be silently ignored.");
            }
            foreach (var kind in new[] { "schema", "users", "capabilities" })
            {
                var member = manifest.Members.SingleOrDefault(x => x.Kind == kind)
                    ?? throw new DomainException($"The package requires one {kind} member.");
                RejectDuplicateProperties(contents[member.Path]);
                using var metadata = JsonDocument.Parse(contents[member.Path]);
                if (metadata.RootElement.ValueKind != (kind == "users" ? JsonValueKind.Array : JsonValueKind.Object))
                    throw new DomainException($"The {kind} member has an unsupported shape.");
                if (kind == "capabilities")
                {
                    var value = metadata.RootElement;
                    if (!value.TryGetProperty("mode", out var mode) || mode.GetString() != manifest.EvidenceMode)
                        throw new DomainException("The qualification mode contradicts the manifest.");
                    if (manifest.EvidenceMode == "qualified")
                    {
                        if (!value.TryGetProperty("operatorEvidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
                            throw new DomainException("The package has no target-server qualification evidence.");
                        string QualificationText(string name) => evidence.TryGetProperty(name, out var property)
                            && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
                        if (new[] { "acceptedBy", "evidenceReference", "capabilityMatrixReference", "sdkSha256", "profileSha256" }
                                .Any(x => string.IsNullOrWhiteSpace(QualificationText(x)))
                            || QualificationText("serverVersion") != manifest.ServerVersion
                            || !Guid.TryParse(QualificationText("sourceInstanceId"), out var sourceId) || sourceId != manifest.SourceInstanceId
                            || !evidence.TryGetProperty("historicalAttachmentsInventoriedAbsent", out var historical) || historical.ValueKind != JsonValueKind.True
                            || !value.TryGetProperty("sdkSha256", out var sdk) || sdk.GetString() != QualificationText("sdkSha256")
                            || !value.TryGetProperty("responseProfile", out var profile) || profile.ValueKind != JsonValueKind.Object
                            || Hash(profile.GetRawText()) != QualificationText("profileSha256"))
                            throw new DomainException("Target-server qualification does not cover this source, SDK, response profile or attachment scope.");
                    }
                }
            }
            var items = new List<(string, IntegritySourceItem)>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var usedAttachments = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in manifest.Members.Where(x => x.Kind == "item").OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                RejectDuplicateProperties(contents[member.Path]);
                var item = JsonSerializer.Deserialize<IntegritySourceItem>(contents[member.Path], Json)
                    ?? throw new DomainException("A source item is empty.");
                // Integrity live item IDs are positive decimal IDs, not a user-renumbered title/custom field.
                if (!Regex.IsMatch(item.Id ?? "", "\\A[1-9][0-9]{0,18}\\z") || !keys.Add(item.Id!))
                    throw new DomainException("Live Integrity item IDs must be distinct positive decimal IDs.");
                if (!scope.Projects.Contains(item.Project, StringComparer.Ordinal) || !scope.Types.Contains(item.Type, StringComparer.Ordinal)
                    || item.Snapshot != manifest.CapturedAt) throw new DomainException("An item lies outside the declared scope or capture.");
                if (item.Fields is null || item.Attachments is null || item.Findings is null || item.Findings.Length != 0
                    || item.History.ValueKind != JsonValueKind.Array || item.Annotations.ValueKind != JsonValueKind.Array
                    || item.Relationships.ValueKind != JsonValueKind.Array)
                    throw new DomainException("An item's preservation records are missing or extraction reported a gap.");
                if (item.Fields.Length > 2000 || item.Fields.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Type)
                    || x.Value.ValueKind == JsonValueKind.Undefined
                    || x.Representation is not ("text" or "xhtml" or "entity-protected-xhtml"))
                    || item.Fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != item.Fields.Length)
                    throw new DomainException("Source field names must be unique and their representation declared.");
                foreach (var attachment in item.Attachments)
                {
                    if (attachment is null || string.IsNullOrWhiteSpace(attachment.Name) || string.IsNullOrWhiteSpace(attachment.Field)
                        || string.IsNullOrWhiteSpace(attachment.ContentType)
                        || !manifest.Members.Any(x => x.Path == attachment.Path && x.Kind == "attachment"))
                        throw new DomainException("An attachment reference has no preserved bytes.");
                    usedAttachments.Add(attachment.Path);
                }
                if (item.Attachments.Where(x => x.SourceUri is not null).GroupBy(x => x.SourceUri, StringComparer.Ordinal).Any(x => x.Count() != 1))
                    throw new DomainException("An embedded source-image URI has ambiguous attachment attribution.");
                items.Add((member.Path, item));
            }
            if (scope.ItemIds.Length != keys.Count || !keys.SetEquals(scope.ItemIds)) throw new DomainException("The captured items do not match the accepted scope inventory.");
            if (manifest.Members.Any(x => x.Kind == "attachment" && !usedAttachments.Contains(x.Path)))
                throw new DomainException("An attachment has no source-item attribution.");
            return new(manifest, root, Hash(bytes), contents, items);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or OverflowException or ArgumentException or InvalidOperationException)
        { throw new DomainException("The Integrity package is malformed: " + ex.Message); }
    }

    public static IntegritySourceDate ReadDate(string? raw)
    {
        raw ??= "";
        if (raw.Length == 0) return new(raw, "blank", null, null);
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new(raw, "date-only", null, null);
        if (!Regex.IsMatch(raw, "\\A\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})?\\z"))
            return new(raw, "unreadable", null, "Source date retained verbatim; no timestamp inferred.");
        var fraction = Regex.Match(raw, "\\.(\\d+)").Groups[1].Value;
        if (fraction.Length > 6 && fraction[6..].Any(x => x != '0'))
            return new(raw, "unrepresentable", null, "Source date retained verbatim; precision exceeds one microsecond.");
        var normalized = Regex.Replace(raw, "\\.(\\d{6})\\d+", ".$1");
        var hasOffset = Regex.IsMatch(raw, "(?:Z|[+-]\\d{2}:\\d{2})\\z");
        if (hasOffset && DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return new(raw, "explicit-offset", instant.ToUniversalTime(), null);
        if (!hasOffset && DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new(raw, "local-unknown-zone", null, null);
        return new(raw, "unreadable", null, "Source date retained verbatim; no timestamp inferred.");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static void ValidatePath(string path)
    {
        if (path is null || path.Length > 240 || !Regex.IsMatch(path, "\\A[a-z0-9][a-z0-9._/-]*\\z")
            || path.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.')))
            throw new DomainException("Package paths must be canonical relative ASCII file paths.");
    }
    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new DomainException("Duplicate JSON properties are ambiguous.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var entry in value.EnumerateArray()) Visit(entry);
        }
        Visit(document.RootElement);
    }
}
