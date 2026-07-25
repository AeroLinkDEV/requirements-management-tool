using System.Buffers.Binary;
using System.IO.Compression;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Reads a PNG far enough to put it in a document.
///
/// PNG is what an engineer actually produces — a screen capture of a bus timing diagram, an exported plot,
/// a boxes-and-arrows sketch — and all of those are line art, where re-encoding to JPEG smears the edges of
/// exactly the detail somebody is being asked to approve. Word embeds PNG directly, but PDF has no PNG
/// filter, so a controlled PDF needs the pixels. That is what this does: decode to eight-bit RGB, which the
/// PDF writer then Flate-compresses.
///
/// The subset is deliberate: eight-bit, non-interlaced, in the colour types a capture tool emits. Anything
/// outside it is reported as undecodable and the document falls back to the image's alt text, which is
/// honest, rather than to a corrupted picture, which is not.
/// </summary>
public static class PngImage
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(byte[] bytes) => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(Signature);

    /// <summary>Reads the dimensions from IHDR alone, without decoding pixels.</summary>
    public static (int Width, int Height) Size(byte[] bytes)
    {
        if (!IsPng(bytes) || bytes.Length < 24) return (640, 360);
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// Decodes to packed eight-bit RGB. Returns false rather than throwing, because one unreadable image
    /// must not stop a document that is otherwise complete from being produced.
    /// </summary>
    public static bool TryDecodeRgb(byte[] bytes, out int width, out int height, out byte[] rgb)
    {
        width = 0; height = 0; rgb = [];
        if (!IsPng(bytes)) return false;
        try { return Decode(bytes, out width, out height, out rgb); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool Decode(byte[] bytes, out int width, out int height, out byte[] rgb)
    {
        width = 0; height = 0; rgb = [];
        int bitDepth = 0, colorType = 0;
        byte[]? palette = null;
        var compressed = new MemoryStream();

        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = offset + 8;
            if (length < 0 || data + length > bytes.Length) return false;
            switch (type)
            {
                case "IHDR":
                    if (length < 13) return false;
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(data, 4));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(data + 4, 4));
                    bitDepth = bytes[data + 8];
                    colorType = bytes[data + 9];
                    // Interlaced images arrive as seven interleaved passes. A capture tool does not produce
                    // them, and half-implementing the reassembly would render a scrambled picture.
                    if (bytes[data + 12] != 0) return false;
                    break;
                case "PLTE":
                    palette = bytes.AsSpan(data, length).ToArray();
                    break;
                case "IDAT":
                    compressed.Write(bytes, data, length);
                    break;
                case "IEND":
                    offset = bytes.Length;
                    continue;
            }
            offset = data + length + 4;
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || compressed.Length == 0) return false;
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return false;
        if (colorType == 3 && palette is null) return false;
        // A very large capture would decode into hundreds of megabytes. The document is better served by the
        // alt text than by exhausting the server that generates it.
        if ((long)width * height > 40_000_000L) return false;

        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var scanline = width * channels;
        var pixels = raw.ToArray();
        if (pixels.Length < (long)(scanline + 1) * height) return false;

        var output = new byte[(long)width * height * 3];
        var previous = new byte[scanline];
        var current = new byte[scanline];
        var source = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = pixels[source++];
            Buffer.BlockCopy(pixels, source, current, 0, scanline);
            source += scanline;
            Unfilter(filter, current, previous, channels);

            var target = (long)row * width * 3;
            for (var x = 0; x < width; x++)
            {
                var at = x * channels;
                byte r, g, b;
                switch (colorType)
                {
                    case 0:
                    case 4:
                        r = g = b = current[at];
                        break;
                    case 3:
                        {
                            var index = current[at] * 3;
                            if (index + 2 >= palette!.Length) return false;
                            r = palette[index]; g = palette[index + 1]; b = palette[index + 2];
                            break;
                        }
                    default:
                        r = current[at]; g = current[at + 1]; b = current[at + 2];
                        break;
                }
                // Transparency composited onto white. A PDF page is white, and a diagram drawn in dark ink
                // on nothing must not come out as dark ink on black.
                if (colorType is 4 or 6)
                {
                    var alpha = current[at + channels - 1] / 255d;
                    r = (byte)(r * alpha + 255 * (1 - alpha));
                    g = (byte)(g * alpha + 255 * (1 - alpha));
                    b = (byte)(b * alpha + 255 * (1 - alpha));
                }
                output[target + x * 3] = r;
                output[target + x * 3 + 1] = g;
                output[target + x * 3 + 2] = b;
            }
            (previous, current) = (current, previous);
        }

        rgb = output;
        return true;
    }

    /// <summary>Reverses the per-scanline filter PNG applies before compression.</summary>
    private static void Unfilter(byte filter, byte[] current, byte[] previous, int channels)
    {
        for (var i = 0; i < current.Length; i++)
        {
            byte left = i >= channels ? current[i - channels] : (byte)0;
            var up = previous[i];
            byte upLeft = i >= channels ? previous[i - channels] : (byte)0;
            current[i] = filter switch
            {
                0 => current[i],
                1 => (byte)(current[i] + left),
                2 => (byte)(current[i] + up),
                3 => (byte)(current[i] + (left + up) / 2),
                4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                _ => current[i],
            };
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
