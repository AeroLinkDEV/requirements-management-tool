using System.IO.Compression;
using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One actual decompression budget shared by all parts of one source archive.</summary>
internal sealed class InceptionArchiveReader : IDisposable
{
    internal const long ExpandedLimit = 100L * 1024 * 1024;
    private readonly ZipArchive archive;
    private readonly byte[] bytes;
    private readonly IReadOnlyList<Part> parts;
    private long remaining = ExpandedLimit;
    public InceptionArchiveReader(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        bytes = copy.ToArray();
        parts = ValidateDirectory(bytes);
        archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read, false);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(x => x.Length) > ExpandedLimit)
            throw new InvalidOperationException("The source archive exceeds its expanded size or entry limit.");
        if (archive.Entries.GroupBy(x => x.FullName, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("The source archive contains duplicate ZIP entries.");
    }
    public IReadOnlyList<ZipArchiveEntry> Entries => archive.Entries;
    public ZipArchiveEntry? Entry(string name) => archive.GetEntry(name);
    public XDocument ReadXml(ZipArchiveEntry entry)
    {
        var index = archive.Entries.IndexOf(entry);
        if (index < 0) throw new InvalidOperationException("The source part does not belong to this archive.");
        var metadata = parts[index];
        using var compressed = new MemoryStream(bytes, metadata.Offset, metadata.Compressed, false);
        // The inflater exposes unconsumed bytes; DeflateStream buffers past its first member and
        // cannot prove that the declared compressed slice contains exactly one member.
        var inflater = metadata.Method == 8 ? new Inflater(true) : null;
        inflater?.SetInput(bytes, metadata.Offset, metadata.Compressed);
        using var counted = new MemoryStream();
        var buffer = new byte[81920];
        var crc = uint.MaxValue;
        int read;
        while ((read = inflater is null ? compressed.Read(buffer, 0, buffer.Length) : inflater.Inflate(buffer)) > 0)
        {
            remaining -= read;
            if (remaining < 0) throw new InvalidOperationException("The source archive exceeds its actual expanded size limit.");
            foreach (var value in buffer.AsSpan(0, read)) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            counted.Write(buffer, 0, read);
        }
        if (inflater is not null && (!inflater.IsFinished || inflater.RemainingInput != 0))
            throw new InvalidOperationException("The source archive part must contain exactly one complete deflate member.");
        if (counted.Length != metadata.Expanded || ~crc != metadata.Crc)
            throw new InvalidOperationException("The source archive part failed its length or CRC integrity check.");
        counted.Position = 0;
        using var reader = XmlReader.Create(counted, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = ExpandedLimit, MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
    public void Dispose() => archive.Dispose();

    private sealed record Part(int Offset, int Compressed, int Expanded, ushort Method, uint Crc);
    private static IReadOnlyList<Part> ValidateDirectory(byte[] data)
    {
        InvalidOperationException Invalid() => new("The source archive has inconsistent or unsupported ZIP metadata.");
        uint U32(int at) { if (at < 0 || at > data.Length - 4) throw Invalid(); return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4)); }
        ushort U16(int at) { if (at < 0 || at > data.Length - 2) throw Invalid(); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2)); }
        var end = -1;
        for (var index = data.Length - 22; index >= Math.Max(0, data.Length - 65_557); index--)
            if (U32(index) == 0x06054b50 && index + 22 + U16(index + 20) == data.Length) { end = index; break; }
        if (end < 0 || U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != U16(end + 10)) throw Invalid();
        var total = U16(end + 10);
        var start = U32(end + 16);
        var size = U32(end + 12);
        if (total > 4096 || (long)start + size != end) throw Invalid();
        var cursor = checked((int)start);
        var result = new List<Part>();
        var intervals = new List<(int Start, long End)>();
        for (var index = 0; index < total; index++)
        {
            if (U32(cursor) != 0x02014b50) throw Invalid();
            var flags = U16(cursor + 8); var method = U16(cursor + 10); var crc = U32(cursor + 16);
            var compressed = U32(cursor + 20); var expanded = U32(cursor + 24);
            var nameLength = U16(cursor + 28); var extra = U16(cursor + 30); var comment = U16(cursor + 32);
            var local = U32(cursor + 42);
            if ((flags & 0x0041) != 0 || method is not (0 or 8) || compressed > int.MaxValue || expanded > int.MaxValue
                || local > int.MaxValue || U16(cursor + 34) != 0 || nameLength == 0) throw Invalid();
            var position = (int)local;
            if (U32(position) != 0x04034b50 || U16(position + 6) != flags || U16(position + 8) != method
                || U16(position + 26) != nameLength) throw Invalid();
            var offset = checked(position + 30 + nameLength + U16(position + 28));
            var contentEnd = (long)offset + compressed;
            if (contentEnd > start || cursor + 46L + nameLength + extra + comment > end
                || !data.AsSpan(cursor + 46, nameLength).SequenceEqual(data.AsSpan(position + 30, nameLength))) throw Invalid();
            if ((flags & 8) == 0)
            {
                if (U32(position + 14) != crc || U32(position + 18) != compressed || U32(position + 22) != expanded) throw Invalid();
            }
            else
            {
                var descriptor = checked((int)contentEnd);
                if (U32(descriptor) == 0x08074b50) descriptor += 4;
                if (U32(descriptor) != crc || U32(descriptor + 4) != compressed || U32(descriptor + 8) != expanded) throw Invalid();
                contentEnd = descriptor + 12L;
            }
            if (contentEnd > start || intervals.Any(x => position < x.End && contentEnd > x.Start)) throw Invalid();
            intervals.Add((position, contentEnd));
            result.Add(new(offset, (int)compressed, (int)expanded, method, crc));
            cursor += 46 + nameLength + extra + comment;
        }
        if (cursor != end) throw Invalid();
        return result;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
            table[index] = value;
        }
        return table;
    }
}
