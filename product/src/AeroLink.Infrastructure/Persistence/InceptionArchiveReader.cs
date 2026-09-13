using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One actual decompression budget shared by all parts of one source archive.</summary>
internal sealed class InceptionArchiveReader : IDisposable
{
    internal const long ExpandedLimit = 100L * 1024 * 1024;
    private readonly ZipArchive archive;
    private long remaining = ExpandedLimit;
    public InceptionArchiveReader(Stream stream)
    {
        archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(x => x.Length) > ExpandedLimit)
            throw new InvalidOperationException("The source archive exceeds its expanded size or entry limit.");
        if (archive.Entries.GroupBy(x => x.FullName, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("The source archive contains duplicate ZIP entries.");
    }
    public IReadOnlyList<ZipArchiveEntry> Entries => archive.Entries;
    public ZipArchiveEntry? Entry(string name) => archive.GetEntry(name);
    public XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var part = entry.Open();
        using var counted = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = part.Read(buffer, 0, buffer.Length)) > 0)
        {
            remaining -= read;
            if (remaining < 0) throw new InvalidOperationException("The source archive exceeds its actual expanded size limit.");
            counted.Write(buffer, 0, read);
        }
        counted.Position = 0;
        using var reader = XmlReader.Create(counted, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = ExpandedLimit, MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
    public void Dispose() => archive.Dispose();
}
