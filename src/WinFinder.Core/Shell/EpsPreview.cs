using System.Buffers.Binary;
using System.Text;

namespace WinFinder.Core.Shell;

/// <summary>
/// Pulls the preview image an EPS file carries for display, since Windows has no EPS thumbnail handler.
/// Returns an encoded image (TIFF, JPEG or BMP) that a standard image decoder can read. Sources, in order:
/// the TIFF section of a DOS EPS binary header (what Illustrator and Photoshop write on Windows), the JPEG
/// thumbnail in a Photoshop <c>%BeginPhotoshop</c> resource block, and an EPSI <c>%%BeginPreview</c> bitmap.
/// EPS files with none of these (pure PostScript, or only a WMF preview) return null.
/// </summary>
public static class EpsPreview
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".eps", ".epsf", ".epsi" };

    /// <summary>The previews live in the header, so a text EPS is only scanned this far.</summary>
    private const int MaxScanBytes = 4 * 1024 * 1024;
    private const int MaxPreviewBytes = 64 * 1024 * 1024;

    public static bool IsEps(string path) => Extensions.Contains(Path.GetExtension(path));

    public static byte[]? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(fs);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? Read(Stream stream)
    {
        long psOffset = 0, psLength = stream.Length;
        Span<byte> header = stackalloc byte[30];
        if (ReadFully(stream, header) == 30 && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xC6D3D0C5)
        {
            psOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            psLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            long tiffOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            long tiffLength = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            if (tiffOffset > 0 && tiffLength > 8 && tiffLength <= MaxPreviewBytes && tiffOffset + tiffLength <= stream.Length)
            {
                var tiff = new byte[tiffLength];
                stream.Position = tiffOffset;
                if (ReadFully(stream, tiff) == tiff.Length) return tiff;
            }
        }

        if (psOffset < 0 || psOffset >= stream.Length) return null;
        stream.Position = psOffset;
        var text = new byte[(int)Math.Min(Math.Min(psLength, stream.Length - psOffset), MaxScanBytes)];
        int read = ReadFully(stream, text);
        return FromPostScript(text.AsSpan(0, read));
    }

    /// <summary>Looks for a Photoshop thumbnail, then an EPSI bitmap, in the PostScript text.</summary>
    internal static byte[]? FromPostScript(ReadOnlySpan<byte> ps)
    {
        // Latin-1 maps bytes 1:1 to chars, so binary data in the stream can't throw or shift offsets.
        string text = Encoding.Latin1.GetString(ps);
        return PhotoshopThumbnail(text) ?? EpsiPreview(text);
    }

    private static byte[]? PhotoshopThumbnail(string text)
    {
        var data = HexSection(text, "%BeginPhotoshop:", "%EndPhotoshop", out _);
        if (data is null) return null;

        // Photoshop image resource blocks: "8BIM", id, padded Pascal name, size, padded data.
        int pos = 0;
        while (pos + 12 <= data.Length && data.AsSpan(pos, 4).SequenceEqual("8BIM"u8))
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 4));
            int nameLength = data[pos + 6] + 1;
            if (nameLength % 2 == 1) nameLength++;
            int sizeAt = pos + 6 + nameLength;
            if (sizeAt + 4 > data.Length) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sizeAt));
            int dataAt = sizeAt + 4;
            if (dataAt + size > data.Length) return null;

            // 1036 = thumbnail: a 28-byte header (format 1 = JPEG) followed by the JPEG itself.
            // (1033, its Photoshop 4 predecessor, stores BGR JPEG data and is skipped.)
            if (id == 1036 && size > 28 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataAt)) == 1)
                return data.AsSpan(dataAt + 28, (int)size - 28).ToArray();

            pos = dataAt + (int)size;
            if (pos % 2 == 1) pos++;
        }
        return null;
    }

    /// <summary>EPSI: <c>%%BeginPreview: width height depth lines</c>, then hex rows where 0 is white.</summary>
    private static byte[]? EpsiPreview(string text)
    {
        var data = HexSection(text, "%%BeginPreview:", "%%EndPreview", out string args);
        if (data is null) return null;
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height) || !int.TryParse(parts[2], out int depth)
            || width <= 0 || height <= 0 || width > 4096 || height > 4096 || depth is not (1 or 2 or 4 or 8))
            return null;

        int rowBytes = (width * depth + 7) / 8;
        if ((long)rowBytes * height > data.Length) return null;
        int max = (1 << depth) - 1;
        var gray = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = x * depth;
                int b = data[y * rowBytes + bit / 8];
                int value = (b >> (8 - depth - bit % 8)) & max;
                gray[y * width + x] = (byte)(255 - value * 255 / max);
            }
        }
        return GrayBmp(width, height, gray);
    }

    /// <summary>
    /// Finds <paramref name="begin"/> at the start of a line, returns the rest of that line in
    /// <paramref name="args"/>, and decodes the hex digits in the following "% ..." lines until <paramref name="end"/>.
    /// </summary>
    private static byte[]? HexSection(string text, string begin, string end, out string args)
    {
        args = "";
        int start = LineStartIndexOf(text, begin);
        if (start < 0) return null;
        int lineEnd = NextLineBreak(text, start);
        args = text[(start + begin.Length)..lineEnd].Trim();

        var bytes = new List<byte>();
        int pos = lineEnd;
        int high = -1;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] is '\r' or '\n') pos++;
            if (pos >= text.Length) break;
            int eol = NextLineBreak(text, pos);
            if (string.CompareOrdinal(text, pos, end, 0, end.Length) == 0) return bytes.ToArray();
            if (text[pos] != '%') return null; // a line outside the comment block: the section is malformed
            for (int i = pos + 1; i < eol; i++)
            {
                int nibble = HexValue(text[i]);
                if (nibble < 0) continue;
                if (high < 0) high = nibble;
                else { bytes.Add((byte)(high << 4 | nibble)); high = -1; }
            }
            pos = eol;
        }
        return null; // ran past the scanned text before the end marker
    }

    private static int LineStartIndexOf(string text, string value)
    {
        int i = 0;
        while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0)
        {
            if (i == 0 || text[i - 1] is '\r' or '\n') return i;
            i += value.Length;
        }
        return -1;
    }

    private static int NextLineBreak(string text, int from)
    {
        int i = text.IndexOfAny(['\r', '\n'], from);
        return i < 0 ? text.Length : i;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>An 8-bit grayscale BMP (top-down rows), so EPSI previews decode like the other formats.</summary>
    internal static byte[] GrayBmp(int width, int height, byte[] gray)
    {
        int stride = (width + 3) & ~3;
        int pixelOffset = 14 + 40 + 256 * 4;
        var bmp = new byte[pixelOffset + stride * height];
        var span = bmp.AsSpan();
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], -height);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 8);
        BinaryPrimitives.WriteInt32LittleEndian(span[46..], 256);
        for (int i = 0; i < 256; i++)
        {
            int p = 54 + i * 4;
            bmp[p] = bmp[p + 1] = bmp[p + 2] = (byte)i;
        }
        for (int y = 0; y < height; y++)
            Array.Copy(gray, y * width, bmp, pixelOffset + y * stride, width);
        return bmp;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
