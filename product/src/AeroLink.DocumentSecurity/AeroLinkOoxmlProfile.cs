using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AeroLink.DocumentSecurity;

public sealed record OoxmlProfileLimits(
    long MaximumCompressedBytes = 100L * 1024 * 1024,
    int MaximumEntries = 2048,
    int MaximumPartNameBytes = 512,
    long MaximumEntryBytes = 32L * 1024 * 1024,
    long MaximumExpandedBytes = 256L * 1024 * 1024,
    double MaximumCompressionRatio = 200,
    long MaximumXmlCharacters = 16L * 1024 * 1024,
    long MaximumTotalXmlCharacters = 64L * 1024 * 1024,
    int MaximumXmlDepth = 128,
    long MaximumXmlNodes = 1_000_000,
    int MaximumAttributesPerElement = 128,
    long MaximumMediaBytes = 16L * 1024 * 1024,
    int MaximumImageDimension = 12_000,
    long MaximumImagePixels = 40_000_000,
    TimeSpan? MaximumProcessingTime = null)
{
    public TimeSpan ProcessingTime => MaximumProcessingTime ?? TimeSpan.FromSeconds(10);
}

public sealed record OoxmlValidationResult(string Profile, string Result, int Entries, long ExpandedBytes, long XmlCharacters);

public sealed class OoxmlValidationException(string code, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The bounded, macro-free OOXML profile accepted by AeroLink and its desktop connector.
/// Keep this dependency-free project shared by both processes so policy cannot drift.
/// </summary>
public static partial class AeroLinkOoxmlProfile
{
    public const string Version = "aerolink-ooxml-safe-v1";
    public const string AcceptedResult = "accepted";

    private const string RelationshipsContentType = "application/vnd.openxmlformats-package.relationships+xml";
    private const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string HyperlinkRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly HashSet<string> RequiredParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "[Content_Types].xml", "_rels/.rels", "word/document.xml"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        RelationshipsContentType,
        "application/xml", "text/xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.webSettings+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.glossary+xml",
        "application/vnd.openxmlformats-officedocument.theme+xml",
        "application/vnd.openxmlformats-package.core-properties+xml",
        "application/vnd.openxmlformats-officedocument.extended-properties+xml",
        "application/vnd.openxmlformats-officedocument.custom-properties+xml",
        "application/vnd.openxmlformats-officedocument.customXmlProperties+xml",
        "application/vnd.ms-word.stylesWithEffects+xml",
        "application/vnd.ms-office.comments+xml",
        "application/vnd.ms-office.commentsExtended+xml",
        "application/vnd.ms-office.commentsIds+xml",
        "application/vnd.ms-office.people+xml",
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/bmp"
    };

    private static readonly string[] ProhibitedPartFragments =
    [
        "/vbaproject", "/vbadata", "/activex/", "/embeddings/", "/customui/", "/webextensions/",
        "/afchunk", "/attachedtemplate", "/scripts/", "/oleobject"
    ];

    private static readonly HashSet<string> DangerousFieldCommands = new(StringComparer.OrdinalIgnoreCase)
    { "DDE", "DDEAUTO", "LINK", "INCLUDETEXT", "INCLUDEPICTURE", "DATABASE" };

    private static readonly HashSet<string> AllowedInternalRelationshipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        OfficeDocumentRelationship,
        "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties",
        "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
        "http://schemas.microsoft.com/office/2007/relationships/stylesWithEffects",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/webSettings",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/fontTable",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/glossaryDocument",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXmlProps",
        "http://schemas.microsoft.com/office/2011/relationships/commentsExtended",
        "http://schemas.microsoft.com/office/2016/09/relationships/commentsIds",
        "http://schemas.microsoft.com/office/2011/relationships/people"
    };

    public static OoxmlValidationResult ValidateFile(string fileName, long? expectedSize = null,
        string? expectedSha256 = null, CancellationToken cancellationToken = default, OoxmlProfileLimits? limits = null)
    {
        using var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
        if (expectedSize is not null && stream.Length != expectedSize)
            Reject("ooxml_hash_mismatch", "The Word document size does not match the controlled attachment.");
        if (expectedSha256 is not null)
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant())))
                Reject("ooxml_hash_mismatch", "The Word document hash does not match the controlled attachment.");
            stream.Position = 0;
        }
        return Validate(stream, cancellationToken, limits);
    }

    public static OoxmlValidationResult Validate(byte[] bytes, CancellationToken cancellationToken = default,
        OoxmlProfileLimits? limits = null) => Validate(new MemoryStream(bytes, false), cancellationToken, limits);

    public static OoxmlValidationResult Validate(Stream package, CancellationToken cancellationToken = default,
        OoxmlProfileLimits? limits = null)
    {
        limits ??= new OoxmlProfileLimits();
        if (!package.CanRead || !package.CanSeek)
            Reject("ooxml_zip_unsupported", "The Word package must be a readable seekable stream.");
        if (package.Length is <= 0 || package.Length > limits.MaximumCompressedBytes)
            Reject("ooxml_compressed_limit", "The Word package exceeds the compressed-size profile limit.");

        var started = Stopwatch.StartNew();
        try
        {
            package.Position = 0;
            var centralDirectory = ReadCentralDirectory(package, limits, started, cancellationToken);
            package.Position = 0;
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, true, Encoding.UTF8);
            var entries = archive.Entries.ToList();
            if (entries.Count > limits.MaximumEntries)
                Reject("ooxml_entry_count_limit", "The Word package contains too many parts.");
            if (entries.Count != centralDirectory.Count)
                Reject("ooxml_zip_unsupported", "The Word package central directory is inconsistent.");

            var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in entries)
            {
                CheckBudget(started, limits, cancellationToken);
                var name = CanonicalPartName(entry.FullName);
                if (!parts.TryAdd(name, entry))
                    Reject("ooxml_part_collision", "The Word package contains duplicate or canonically equivalent parts.");
                if (!centralDirectory.TryGetValue(name, out var central)
                    || central.CompressedLength != entry.CompressedLength || central.ExpandedLength != entry.Length)
                    Reject("ooxml_zip_unsupported", "The Word package central directory is inconsistent.");
                if (name.EndsWith('/')) continue;
                if (entry.Length < 0 || entry.Length > limits.MaximumEntryBytes)
                    Reject("ooxml_entry_size_limit", "A Word package part exceeds the expanded-size profile limit.");
                expanded = checked(expanded + entry.Length);
                if (expanded > limits.MaximumExpandedBytes)
                    Reject("ooxml_expanded_size_limit", "The Word package exceeds the total expanded-size profile limit.");
                if (entry.Length > 4096 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio))
                    Reject("ooxml_compression_ratio_limit", "A Word package part exceeds the compression-ratio profile limit.");
                RejectProhibitedPart(name);
            }
            foreach (var required in RequiredParts)
                if (!parts.ContainsKey(required)) Reject("ooxml_core_part_missing", "The Word package is missing required core metadata.");

            long totalXmlCharacters = 0;
            long totalXmlNodes = 0;
            var relationships = new List<PackageRelationship>();
            ContentTypeManifest? contentTypes = null;
            foreach (var (name, entry) in parts)
            {
                if (name.EndsWith('/')) continue;
                CheckBudget(started, limits, cancellationToken);
                if (IsXml(name))
                {
                    var scan = ScanXml(entry, name, limits, started, cancellationToken);
                    if (scan.Crc32 != centralDirectory[name].Crc32)
                        Reject("ooxml_zip_unsupported", "A Word package part failed its CRC integrity check.");
                    totalXmlCharacters = checked(totalXmlCharacters + scan.Characters);
                    totalXmlNodes = checked(totalXmlNodes + scan.Nodes);
                    if (totalXmlCharacters > limits.MaximumTotalXmlCharacters || totalXmlNodes > limits.MaximumXmlNodes)
                        Reject("ooxml_xml_limit", "The Word package exceeds the XML complexity profile limit.");
                    if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) contentTypes = scan.ContentTypes;
                    if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) relationships.AddRange(scan.Relationships);
                }
                else
                {
                    var crc32 = ReadAndCheckBinary(entry, name, limits, started, cancellationToken);
                    if (crc32 != centralDirectory[name].Crc32)
                        Reject("ooxml_zip_unsupported", "A Word package part failed its CRC integrity check.");
                }
            }

            ValidateContentTypes(contentTypes, parts);
            ValidateRelationships(relationships, parts);
            return new OoxmlValidationResult(Version, AcceptedResult, entries.Count, expanded, totalXmlCharacters);
        }
        catch (OoxmlValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (XmlException ex) { throw new OoxmlValidationException("ooxml_xml_invalid", "The Word package contains malformed or prohibited XML.", ex); }
        catch (InvalidDataException ex) { throw new OoxmlValidationException("ooxml_zip_unsupported", "The Word package is corrupt, encrypted, or uses an unsupported ZIP feature.", ex); }
        catch (OverflowException ex) { throw new OoxmlValidationException("ooxml_expanded_size_limit", "The Word package exceeds the total expanded-size profile limit.", ex); }
    }

    private static XmlScan ScanXml(ZipArchiveEntry entry, string partName, OoxmlProfileLimits limits,
        Stopwatch started, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaximumXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CloseInput = false
        };
        long characters = 0;
        long nodes = 0;
        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        var relationships = new List<PackageRelationship>();
        var contentTypes = partName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ? new ContentTypeManifest() : null;
        var complexFields = new Stack<StringBuilder>();
        string? rootLocalName = null;
        string? rootNamespace = null;
        using var source = entry.Open();
        using var counting = new CountingReadStream(source, entry.Length, limits.MaximumXmlCharacters);
        using var reader = XmlReader.Create(counting, settings);
        while (reader.Read())
        {
            CheckBudget(started, limits, cancellationToken);
            nodes++;
            if (nodes > limits.MaximumXmlNodes || reader.Depth > limits.MaximumXmlDepth)
                Reject("ooxml_xml_limit", "A Word package XML part exceeds the complexity profile limit.");
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (rootLocalName is null)
            {
                rootLocalName = reader.LocalName;
                rootNamespace = reader.NamespaceURI;
            }
            if (reader.AttributeCount > limits.MaximumAttributesPerElement)
                Reject("ooxml_xml_limit", "A Word package XML element contains too many attributes.");

            if (reader.NamespaceURI == WordNamespace && reader.LocalName is "altChunk" or "object" or "oleObject")
                Reject("ooxml_active_content", "The Word package contains unsupported embedded or imported content.");
            if (reader.NamespaceURI == WordNamespace && reader.LocalName == "fldSimple")
                RejectDangerousField(reader.GetAttribute("instr", WordNamespace) ?? reader.GetAttribute("w:instr") ?? reader.GetAttribute("instr"));
            if (reader.NamespaceURI == WordNamespace && reader.LocalName == "fldChar")
            {
                var kind = reader.GetAttribute("fldCharType", WordNamespace) ?? reader.GetAttribute("w:fldCharType") ?? reader.GetAttribute("fldCharType");
                if (string.Equals(kind, "begin", StringComparison.OrdinalIgnoreCase))
                {
                    complexFields.Push(new StringBuilder());
                }
                else if (string.Equals(kind, "end", StringComparison.OrdinalIgnoreCase))
                {
                    if (complexFields.Count == 0)
                        Reject("ooxml_dangerous_field", "The Word package contains an unmatched field boundary.");
                    RejectDangerousField(complexFields.Pop().ToString());
                }
            }
            if (reader.NamespaceURI == WordNamespace && reader.LocalName == "instrText")
            {
                var instructionFragment = reader.ReadElementContentAsString();
                if (complexFields.Count == 0)
                    Reject("ooxml_dangerous_field", "The Word package contains a field instruction outside a bounded field.");
                complexFields.Peek().Append(instructionFragment);
            }

            if (partName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                && reader.NamespaceURI == PackageRelationshipsNamespace && reader.LocalName == "Relationship")
            {
                var id = RequiredAttribute(reader, "Id", "ooxml_relationship_broken");
                if (!relationshipIds.Add(id)) Reject("ooxml_relationship_broken", "A relationship identifier is duplicated.");
                relationships.Add(new PackageRelationship(partName, id,
                    RequiredAttribute(reader, "Type", "ooxml_relationship_broken"),
                    RequiredAttribute(reader, "Target", "ooxml_relationship_broken"),
                    string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)));
            }

            if (contentTypes is not null && reader.NamespaceURI == ContentTypesNamespace)
            {
                if (reader.LocalName == "Default") contentTypes.AddDefault(
                    RequiredAttribute(reader, "Extension", "ooxml_content_type_unsupported"),
                    RequiredAttribute(reader, "ContentType", "ooxml_content_type_unsupported"));
                else if (reader.LocalName == "Override") contentTypes.AddOverride(
                    RequiredAttribute(reader, "PartName", "ooxml_content_type_unsupported"),
                    RequiredAttribute(reader, "ContentType", "ooxml_content_type_unsupported"));
            }
        }
        characters = counting.BytesRead;
        if (counting.BytesRead != entry.Length)
            Reject("ooxml_zip_unsupported", "A Word package part did not match its declared expanded size.");
        if (partName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
            && (rootLocalName != "Relationships" || rootNamespace != PackageRelationshipsNamespace))
            Reject("ooxml_xml_invalid", "A Word package relationship part has an invalid root namespace.");
        if (partName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
            && (rootLocalName != "Types" || rootNamespace != ContentTypesNamespace))
            Reject("ooxml_xml_invalid", "The Word package content-type manifest has an invalid root namespace.");
        if (partName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
            && (rootLocalName != "document" || rootNamespace != WordNamespace))
            Reject("ooxml_xml_invalid", "The Word package document root has an invalid namespace.");
        if (complexFields.Count > 0)
            Reject("ooxml_dangerous_field", "The Word package contains an unterminated field instruction.");
        return new XmlScan(characters, nodes, counting.Crc32, relationships, contentTypes);
    }

    private static void ValidateContentTypes(ContentTypeManifest? manifest, IReadOnlyDictionary<string, ZipArchiveEntry> parts)
    {
        if (manifest is null) Reject("ooxml_content_type_unsupported", "The Word package has no readable content-type manifest.");
        if (manifest!.DeclaredContentTypes.Any(type => !AllowedContentTypes.Contains(type)))
            Reject("ooxml_content_type_unsupported", "The Word package declares an unsupported content type.");
        foreach (var (name, _) in parts.Where(part => !part.Key.EndsWith('/')))
        {
            if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) continue;
            var type = manifest!.Resolve(name);
            if (type is null || !AllowedContentTypes.Contains(type))
                Reject("ooxml_content_type_unsupported", "The Word package contains an unsupported content type.");
        }
        var mainType = manifest!.Resolve("word/document.xml");
        if (!string.Equals(mainType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", StringComparison.OrdinalIgnoreCase))
            Reject("ooxml_content_type_unsupported", "The package is not a macro-free Word DOCX document.");
    }

    private static void ValidateRelationships(IReadOnlyList<PackageRelationship> relationships,
        IReadOnlyDictionary<string, ZipArchiveEntry> parts)
    {
        var rootOffice = relationships.Where(x => x.RelationshipPart.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)
            && x.Type.Equals(OfficeDocumentRelationship, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rootOffice.Count != 1 || rootOffice[0].External || !ResolveTarget(rootOffice[0]).Equals("word/document.xml", StringComparison.OrdinalIgnoreCase))
            Reject("ooxml_relationship_broken", "The package must identify exactly one internal Word document root.");

        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in relationships)
        {
            var owner = RelationshipOwner(relationship.RelationshipPart);
            if (owner.Length > 0 && !parts.ContainsKey(owner))
                Reject("ooxml_relationship_broken", "A relationship part has no corresponding source part.");
            if (relationship.External)
            {
                if (!relationship.Type.Equals(HyperlinkRelationship, StringComparison.OrdinalIgnoreCase)
                    || !owner.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(relationship.Target, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https" or "mailto")
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    Reject("ooxml_relationship_external", "A Word package relationship is missing or external because the external target is prohibited.");
                continue;
            }
            if (!AllowedInternalRelationshipTypes.Contains(relationship.Type))
                Reject("ooxml_relationship_type_unsupported", "The Word package contains an unsupported internal relationship type.");
            var target = ResolveTarget(relationship);
            if (!parts.ContainsKey(target)) Reject("ooxml_relationship_broken", "A Word package relationship target is missing.");
            if (!graph.TryGetValue(owner, out var edges)) graph[owner] = edges = [];
            edges.Add(target);
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Keys) Visit(node);
        void Visit(string node)
        {
            if (visited.Contains(node)) return;
            if (!visiting.Add(node)) Reject("ooxml_relationship_cycle", "The Word package contains a cyclic relationship graph.");
            if (graph.TryGetValue(node, out var edges)) foreach (var edge in edges) Visit(edge);
            visiting.Remove(node); visited.Add(node);
        }

    }

    private static uint ReadAndCheckBinary(ZipArchiveEntry entry, string name, OoxmlProfileLimits limits,
        Stopwatch started, CancellationToken cancellationToken)
    {
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".docm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            Reject("ooxml_active_content", "The Word package contains a nested package.");
        var media = name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase);
        if (media && entry.Length > limits.MaximumMediaBytes)
            Reject("ooxml_media_limit", "A Word package media part exceeds the profile limit.");
        var prefix = media ? new MemoryStream((int)Math.Min(entry.Length, 1024 * 1024)) : null;
        var crc = new Crc32Accumulator();
        using var source = entry.Open();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            CheckBudget(started, limits, cancellationToken);
            totalRead += read;
            crc.Append(buffer.AsSpan(0, read));
            if (prefix is not null && prefix.Length < prefix.Capacity)
                prefix.Write(buffer, 0, Math.Min(read, prefix.Capacity - (int)prefix.Length));
        }
        if (totalRead != entry.Length)
            Reject("ooxml_zip_unsupported", "A Word package part did not match its declared expanded size.");
        if (prefix is not null) ValidateImage(prefix.ToArray(), limits);
        return crc.Value;
    }

    private static void ValidateImage(byte[] bytes, OoxmlProfileLimits limits)
    {
        var dimensions = ImageDimensions(bytes);
        if (dimensions is null) Reject("ooxml_media_unsupported", "The Word package contains an unsupported image format.");
        var (width, height) = dimensions!.Value;
        if (width <= 0 || height <= 0 || width > limits.MaximumImageDimension || height > limits.MaximumImageDimension
            || (long)width * height > limits.MaximumImagePixels)
            Reject("ooxml_media_limit", "A Word package image exceeds the dimension profile limit.");
    }

    private static (int Width, int Height)? ImageDimensions(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        if (bytes.Length >= 10 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a"))
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)));
        if (bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            var width = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4)));
            var height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4)));
            return width > int.MaxValue || height > int.MaxValue ? (int.MaxValue, int.MaxValue) : ((int)width, (int)height);
        }
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 9 < bytes.Length)
            {
                if (bytes[offset++] != 0xff) continue;
                var marker = bytes[offset++];
                if (marker is 0xd8 or 0xd9) continue;
                if (offset + 2 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)));
                offset += length;
            }
        }
        return null;
    }

    private static string CanonicalPartName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\\') || raw.StartsWith('/') || raw.Contains(':')
            || raw.Contains('\0') || raw.Contains('\uFFFD') || raw.Any(ch => char.IsControl(ch)))
            Reject("ooxml_part_name_invalid", "The Word package contains an invalid part name.");
        var normalized = raw.Normalize(NormalizationForm.FormC);
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            Reject("ooxml_part_name_invalid", "The Word package contains an unsafe part path.");
        return normalized;
    }

    private static void RejectProhibitedPart(string name)
    {
        var slashName = "/" + name;
        if (ProhibitedPartFragments.Any(fragment => slashName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && name.Contains("vba", StringComparison.OrdinalIgnoreCase))
            Reject("ooxml_active_content", "The Word package contains prohibited active or embedded content.");
    }

    private static bool IsXml(string name) => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static string RequiredAttribute(XmlReader reader, string name, string code) =>
        string.IsNullOrWhiteSpace(reader.GetAttribute(name))
            ? throw new OoxmlValidationException(code, "Required Word package metadata is missing.")
            : reader.GetAttribute(name)!;

    private static void RejectDangerousField(string? fieldText)
    {
        if (string.IsNullOrWhiteSpace(fieldText)) return;
        var command = FieldCommandRegex().Match(fieldText);
        if (command.Success && DangerousFieldCommands.Contains(command.Value))
            Reject("ooxml_dangerous_field", "The Word package contains a prohibited field instruction.");
    }

    private static string RelationshipOwner(string relationshipPart)
    {
        if (relationshipPart.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)) return "";
        var marker = relationshipPart.LastIndexOf("/_rels/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || !relationshipPart.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            Reject("ooxml_relationship_broken", "A relationship part is stored at an unsupported location.");
        var directory = relationshipPart[..marker];
        var file = relationshipPart[(marker + 7)..^5];
        return directory.Length == 0 ? file : directory + "/" + file;
    }

    private static string ResolveTarget(PackageRelationship relationship)
    {
        if (relationship.Target.Contains('\\') || relationship.Target.StartsWith('/') || relationship.Target.StartsWith("//"))
            Reject("ooxml_relationship_broken", "A relationship target has an unsafe path.");
        var owner = RelationshipOwner(relationship.RelationshipPart);
        var directory = owner.Contains('/') ? owner[..owner.LastIndexOf('/')] : "";
        string decoded;
        try { decoded = Uri.UnescapeDataString(relationship.Target.Split('#')[0]); }
        catch (UriFormatException) { Reject("ooxml_relationship_broken", "A relationship target is malformed."); return ""; }
        var stack = new List<string>();
        if (directory.Length > 0) stack.AddRange(directory.Split('/'));
        foreach (var segment in decoded.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (stack.Count == 0) Reject("ooxml_relationship_broken", "A relationship target escapes the package."); stack.RemoveAt(stack.Count - 1); }
            else stack.Add(segment);
        }
        return CanonicalPartName(string.Join('/', stack));
    }

    private static void CheckBudget(Stopwatch started, OoxmlProfileLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (started.Elapsed > limits.ProcessingTime)
            Reject("ooxml_timeout", "The Word package exceeded the validation time budget.");
    }

    private static void Reject(string code, string message) => throw new OoxmlValidationException(code, message);

    private static Dictionary<string, CentralDirectoryPart> ReadCentralDirectory(Stream package, OoxmlProfileLimits limits,
        Stopwatch started, CancellationToken cancellationToken)
    {
        const uint endSignature = 0x06054b50;
        const uint centralSignature = 0x02014b50;
        var tailLength = (int)Math.Min(package.Length, 65_557);
        var tail = new byte[tailLength];
        package.Position = package.Length - tailLength;
        package.ReadExactly(tail);
        var endOffset = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) != endSignature) continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));
            if (index + 22 + commentLength == tail.Length) { endOffset = index; break; }
        }
        if (endOffset < 0) Reject("ooxml_zip_unsupported", "The Word package has no unambiguous ZIP directory.");

        var end = tail.AsSpan(endOffset);
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..6]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..8]);
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[8..10]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..12]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..16]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..20]);
        if (disk != 0 || directoryDisk != 0 || diskEntries != totalEntries || totalEntries == ushort.MaxValue
            || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
            Reject("ooxml_zip_unsupported", "Multi-volume and ZIP64 Word packages are outside the controlled profile.");
        if (totalEntries > limits.MaximumEntries)
            Reject("ooxml_entry_count_limit", "The Word package contains too many parts.");
        if ((long)directoryOffset + directorySize > package.Length)
            Reject("ooxml_zip_unsupported", "The Word package central directory is out of bounds.");

        package.Position = directoryOffset;
        using var reader = new BinaryReader(package, Encoding.UTF8, true);
        var result = new Dictionary<string, CentralDirectoryPart>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < totalEntries; index++)
        {
            CheckBudget(started, limits, cancellationToken);
            if (reader.ReadUInt32() != centralSignature)
                Reject("ooxml_zip_unsupported", "The Word package central directory is malformed.");
            _ = reader.ReadUInt16(); _ = reader.ReadUInt16();
            var flags = reader.ReadUInt16();
            var method = reader.ReadUInt16();
            _ = reader.ReadUInt16(); _ = reader.ReadUInt16();
            var crc32 = reader.ReadUInt32();
            var compressed = reader.ReadUInt32();
            var expanded = reader.ReadUInt32();
            var nameLength = reader.ReadUInt16();
            var extraLength = reader.ReadUInt16();
            var commentLength = reader.ReadUInt16();
            _ = reader.ReadUInt16(); _ = reader.ReadUInt16(); _ = reader.ReadUInt32(); _ = reader.ReadUInt32();
            if ((flags & 0x0041) != 0 || method is not (0 or 8) || compressed == uint.MaxValue || expanded == uint.MaxValue)
                Reject("ooxml_zip_unsupported", "The Word package is encrypted or uses an unsupported ZIP feature.");
            if (nameLength == 0 || nameLength > limits.MaximumPartNameBytes)
                Reject("ooxml_part_name_invalid", "A Word package part name exceeds the controlled profile limit.");
            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength)
                Reject("ooxml_zip_unsupported", "The Word package central directory is truncated.");
            string rawName;
            try
            {
                if ((flags & 0x0800) == 0 && nameBytes.Any(value => value > 0x7f))
                    Reject("ooxml_part_name_invalid", "A Word package part name uses an ambiguous legacy encoding.");
                rawName = new UTF8Encoding(false, true).GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                Reject("ooxml_part_name_invalid", "A Word package part name is not valid UTF-8.");
                return result;
            }
            var name = CanonicalPartName(rawName);
            if (!result.TryAdd(name, new CentralDirectoryPart(compressed, expanded, crc32)))
                Reject("ooxml_part_collision", "The Word package contains duplicate or canonically equivalent parts.");
            if (package.Position + extraLength + commentLength > package.Length)
                Reject("ooxml_zip_unsupported", "The Word package central directory is truncated.");
            package.Seek(extraLength + commentLength, SeekOrigin.Current);
        }
        if (package.Position != (long)directoryOffset + directorySize)
            Reject("ooxml_zip_unsupported", "The Word package central directory is inconsistent.");
        return result;
    }

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex FieldCommandRegex();

    private sealed record PackageRelationship(string RelationshipPart, string Id, string Type, string Target, bool External);
    private sealed record CentralDirectoryPart(long CompressedLength, long ExpandedLength, uint Crc32);
    private sealed record XmlScan(long Characters, long Nodes, uint Crc32, IReadOnlyList<PackageRelationship> Relationships, ContentTypeManifest? ContentTypes);

    private sealed class ContentTypeManifest
    {
        private readonly Dictionary<string, string> defaults = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> DeclaredContentTypes => defaults.Values.Concat(overrides.Values);
        public void AddDefault(string extension, string type)
        {
            if (!defaults.TryAdd(extension.TrimStart('.'), type)) Reject("ooxml_content_type_unsupported", "A default content type is duplicated.");
        }
        public void AddOverride(string name, string type)
        {
            var canonical = CanonicalPartName(name.TrimStart('/'));
            if (!overrides.TryAdd(canonical, type)) Reject("ooxml_content_type_unsupported", "A part content type is duplicated.");
        }
        public string? Resolve(string name)
        {
            if (overrides.TryGetValue(name, out var exact)) return exact;
            var dot = name.LastIndexOf('.');
            return dot >= 0 && defaults.TryGetValue(name[(dot + 1)..], out var fallback) ? fallback : null;
        }
    }

    private sealed class CountingReadStream(Stream inner, long expectedLength, long maximumBytes) : Stream
    {
        private readonly Crc32Accumulator crc = new();
        public long BytesRead { get; private set; }
        public uint Crc32 => crc.Value;
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); BytesRead += read; crc.Append(buffer.AsSpan(offset, read)); CheckLength(); return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); BytesRead += read; crc.Append(buffer[..read]); CheckLength(); return read; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        private void CheckLength()
        {
            if (BytesRead > expectedLength) Reject("ooxml_entry_size_limit", "A Word package part exceeded its declared size.");
            if (BytesRead > maximumBytes) Reject("ooxml_xml_limit", "A Word package XML part exceeds the character profile limit.");
        }
    }

    private sealed class Crc32Accumulator
    {
        private static readonly uint[] Table = BuildTable();
        private uint value = uint.MaxValue;
        public uint Value => ~value;
        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var item in bytes)
                value = Table[(value ^ item) & 0xff] ^ (value >> 8);
        }
        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var item = index;
                for (var bit = 0; bit < 8; bit++)
                    item = (item >> 1) ^ (0xedb88320u & (uint)-(int)(item & 1));
                table[index] = item;
            }
            return table;
        }
    }
}
