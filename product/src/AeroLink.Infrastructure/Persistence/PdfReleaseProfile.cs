using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The bounded, static, single-version PDF profile a managed-document release rendition must satisfy.
///
/// Structure is parsed with the maintained PDFsharp reader (Apache/MIT-licensed, genuine package);
/// the profile additionally enforces file-boundary hygiene that a tolerant reader would repair silently,
/// and walks the parsed object graph to refuse active/embedded capabilities that the approved
/// Microsoft Word export workflow never produces.
/// </summary>
public static class PdfReleaseProfile
{
    public const int MaximumPageCount = 10_000;
    public const int MaximumObjectCount = 100_000;
    public const double MaximumPageExtentPoints = 14_400;
    private const int MaximumTraversalDepth = 128;

    private static readonly string[] ProhibitedKeys =
    [
        "/JS", "/JavaScript", "/Launch", "/EmbeddedFile", "/EmbeddedFiles", "/Filespec", "/FileSpec",
        "/RichMedia", "/Rendition", "/Sound", "/Movie", "/3D", "/AA", "/OpenAction", "/AcroForm", "/XFA"
    ];

    private static readonly string[] ProhibitedActionTypes = ["/JavaScript", "/Launch", "/Rendition", "/Movie", "/Sound", "/RichMedia"];
    private static readonly string[] ProhibitedAnnotationTypes = ["/RichMedia", "/Movie", "/Sound", "/3D", "/FileAttachment"];

    public static PdfProfileValidation Validate(byte[] bytes)
    {
        var header = HeaderFailure(bytes);
        if (header is not null) return PdfProfileValidation.Reject("pdf_structure_invalid", header);

        try
        {
            using var stream = new MemoryStream(bytes, false);
            PdfDocument? document = null;
            try
            {
                document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                if (document.SecuritySettings.IsEncrypted)
                    return PdfProfileValidation.Reject("pdf_encrypted", "Password-protected or encrypted PDFs are not accepted as release renditions.");
                if (document.PageCount < 1)
                    return PdfProfileValidation.Reject("pdf_no_pages", "The release PDF renders no pages.");
                if (document.PageCount > MaximumPageCount)
                    return PdfProfileValidation.Reject("pdf_too_complex", "The release PDF contains an unreasonable number of pages.");
                if (document.Internals.GetAllObjects().Count() > MaximumObjectCount)
                    return PdfProfileValidation.Reject("pdf_too_complex", "The release PDF contains an unreasonable number of objects.");
                foreach (var page in document.Pages)
                {
                    var width = page.Width.Point;
                    var height = page.Height.Point;
                    if (width is < 1 or > MaximumPageExtentPoints || height is < 1 or > MaximumPageExtentPoints)
                        return PdfProfileValidation.Reject("pdf_page_geometry_invalid", "A release PDF page has an unsupported size.");
                }
                var feature = FindProhibitedFeature(document.Internals);
                if (feature is not null)
                    return PdfProfileValidation.Reject("pdf_prohibited_feature", $"The release PDF contains a prohibited active or embedded capability ({feature}). Only static Word-export renditions are accepted.");
                return PdfProfileValidation.Valid();
            }
            catch (PdfReaderException)
            {
                return PdfProfileValidation.Reject("pdf_structure_invalid", "The release PDF is malformed, truncated, or password protected.");
            }
            catch (Exception) when (document is not null && document.SecuritySettings.IsEncrypted)
            {
                return PdfProfileValidation.Reject("pdf_encrypted", "Password-protected or encrypted PDFs are not accepted as release renditions.");
            }
            finally
            {
                document?.Dispose();
            }
        }
        catch (Exception)
        {
            return PdfProfileValidation.Reject("pdf_structure_invalid", "The release PDF could not be structurally validated as a static rendition.");
        }
    }

    private static string? HeaderFailure(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F' || bytes[4] != (byte)'-')
            return "The release rendition is not a PDF file.";
        var major = bytes[5] - (byte)'0';
        if (bytes[6] != (byte)'.' || major is < 1 or > 2 || bytes[7] < (byte)'0' || bytes[7] > (byte)'9')
            return "The release PDF declares an unsupported version.";
        if (!EndsWithEndOfFileMarker(bytes))
            return "The release PDF is truncated or carries trailing data after its end marker.";
        if (HasDisallowedIncrementalUpdate(bytes))
            return "The release PDF must be a single-version export without incremental updates.";
        return null;
    }

    private static bool EndsWithEndOfFileMarker(byte[] bytes)
    {
        var tail = bytes.AsSpan(Math.Max(0, bytes.Length - 2048));
        var marker = "%%EOF"u8;
        var index = tail.LastIndexOf(marker);
        if (index < 0) return false;
        for (var position = index + marker.Length; position < tail.Length; position++)
        {
            var value = tail[position];
            if (value is not ((byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t' or 0x0C))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when the PDF carries a real incremental update rather than Microsoft Word's harmless
    /// trailing empty cross-reference section (<c>xref\n0 0\ntrailer ...</c>).
    /// </summary>
    public static bool HasDisallowedIncrementalUpdate(byte[] bytes)
    {
        const int tailWindow = 65536;
        var offset = Math.Max(0, bytes.Length - tailWindow);
        var tail = Encoding.ASCII.GetString(bytes, offset, bytes.Length - offset);
        var lastEndMarker = tail.LastIndexOf("%%EOF", StringComparison.Ordinal);
        if (lastEndMarker < 0) return false; // the end-marker check reports the clearer failure first
        var previousEndMarker = tail.LastIndexOf("%%EOF", lastEndMarker - 1, StringComparison.Ordinal);
        if (previousEndMarker < 0) return false;
        return !IsBenignEmptyXrefSection(tail[(previousEndMarker + 5)..lastEndMarker]);
    }

    /// <summary>
    /// Microsoft Word's PDF export appends a harmless empty cross-reference section
    /// (<c>xref\n0 0\ntrailer ...</c>). Any additional section that actually adds entries is
    /// a real incremental update and is refused.
    /// </summary>
    private static bool IsBenignEmptyXrefSection(string section)
    {
        if (section.Any(character => character != '\r' && character != '\n' && character != ' ' && character != '\t'
            && (character < 32 || character > 126)))
            return false;
        var xref = section.IndexOf("xref", StringComparison.Ordinal);
        if (xref < 0 || section.IndexOf("trailer", StringComparison.Ordinal) < 0
            || section.IndexOf("startxref", StringComparison.Ordinal) < 0)
            return false;
        var index = xref + 4;
        return TryReadInteger(section, ref index, out var first) && first == 0
            && TryReadInteger(section, ref index, out var second) && second == 0;
    }

    private static bool TryReadInteger(string text, ref int index, out int value)
    {
        value = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        var start = index;
        while (index < text.Length && char.IsDigit(text[index])) index++;
        return index > start && int.TryParse(text.AsSpan(start, index - start), out value);
    }

    private static string? FindProhibitedFeature(PdfInternals internals)
    {
        var visited = new HashSet<ulong>();
        foreach (var obj in internals.GetAllObjects())
        {
            var finding = ScanObject(obj, visited, 0);
            if (finding is not null) return finding;
        }
        return null;
    }

    private static string? ScanObject(PdfObject obj, HashSet<ulong> visited, int depth)
    {
        if (depth > MaximumTraversalDepth) return null;
        if (obj is PdfDictionary dictionary) return ScanDictionary(dictionary, visited, depth);
        if (obj is PdfArray array) return ScanArray(array, visited, depth);
        return null;
    }

    private static string? ScanDictionary(PdfDictionary dictionary, HashSet<ulong> visited, int depth)
    {
        var action = NameValue(dictionary, "/S");
        if (action is not null && Array.IndexOf(ProhibitedActionTypes, action) >= 0) return action;
        var annotation = NameValue(dictionary, "/Subtype");
        if (annotation is not null && Array.IndexOf(ProhibitedAnnotationTypes, annotation) >= 0) return annotation;
        foreach (var key in dictionary.Elements.KeyNames)
        {
            var keyName = key.ToString();
            if (Array.IndexOf(ProhibitedKeys, keyName) >= 0) return keyName;
            var finding = ScanItem(dictionary.Elements[key], visited, depth + 1);
            if (finding is not null) return finding;
        }
        return null;
    }

    private static string? NameValue(PdfDictionary dictionary, string key)
    {
        try
        {
            return dictionary.Elements[key] is PdfName name ? NormalizeName(name.Value) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.StartsWith('/') ? name : "/" + name;
    }

    private static string? ScanArray(PdfArray array, HashSet<ulong> visited, int depth)
    {
        foreach (var item in array.Elements.Items)
        {
            var finding = ScanItem(item, visited, depth + 1);
            if (finding is not null) return finding;
        }
        return null;
    }

    private static string? ScanItem(PdfItem? item, HashSet<ulong> visited, int depth)
    {
        if (item is null || depth > MaximumTraversalDepth) return null;
        if (item is PdfReference reference)
        {
            var identity = ((ulong)reference.ObjectID.ObjectNumber << 8) | (uint)reference.ObjectID.GenerationNumber;
            if (!visited.Add(identity)) return null;
            try
            {
                return ScanObject(reference.Value, visited, depth);
            }
            catch (Exception)
            {
                return "/DamagedReference";
            }
        }
        if (item is PdfObject obj) return ScanObject(obj, visited, depth);
        return null;
    }
}

public sealed record PdfProfileValidation(bool IsValid, string Code, string Message)
{
    public static PdfProfileValidation Valid() => new(true, "pdf_ok", "The release PDF conforms to the controlled static rendition profile.");
    public static PdfProfileValidation Reject(string code, string message) => new(false, code, message);
}
