using System.Buffers.Binary;
using System.Text;
using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

public class EpsPreviewTests
{
    private const string PostScript = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 10 10\n%%EndComments\nshowpage\n%%EOF\n";

    [Theory]
    [InlineData(@"C:\a\logo.eps", true)]
    [InlineData(@"C:\a\logo.EPS", true)]
    [InlineData(@"C:\a\logo.epsf", true)]
    [InlineData(@"C:\a\logo.epsi", true)]
    [InlineData(@"C:\a\logo.ps", false)]
    [InlineData(@"C:\a\logo.jpg", false)]
    public void IsEps_MatchesEpsExtensions(string path, bool expected) => Assert.Equal(expected, EpsPreview.IsEps(path));

    [Fact]
    public void DosBinaryHeader_ReturnsTiffSection()
    {
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
        byte[] ps = Encoding.ASCII.GetBytes(PostScript);
        var file = new byte[30 + ps.Length + tiff.Length];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0xC6D3D0C5);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 30);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)ps.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(30 + ps.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)tiff.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 0xFFFF);
        ps.CopyTo(file, 30);
        tiff.CopyTo(file, 30 + ps.Length);

        Assert.Equal(tiff, EpsPreview.Read(new MemoryStream(file)));
    }

    [Fact]
    public void DosBinaryHeader_WithoutTiff_FallsBackToPostScriptSection()
    {
        string text = "%!PS-Adobe-3.0 EPSF-3.0\n%%BeginPreview: 8 1 1 1\n% 0F\n%%EndPreview\n";
        byte[] ps = Encoding.ASCII.GetBytes(text);
        var file = new byte[30 + ps.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0xC6D3D0C5);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)ps.Length);
        ps.CopyTo(file, 30);

        var bmp = EpsPreview.Read(new MemoryStream(file));
        Assert.NotNull(bmp);
        Assert.Equal((byte)'B', bmp![0]);
    }

    [Fact]
    public void PhotoshopResources_ReturnJpegThumbnail()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 0xFF, 0xD9];
        var resources = new List<byte>();
        // An unrelated resource first (id 1005, empty name, 3 bytes of data padded to 4).
        resources.AddRange("8BIM"u8.ToArray());
        resources.AddRange([0x03, 0xED, 0x00, 0x00, 0, 0, 0, 3, 1, 2, 3, 0]);
        // The thumbnail: 28-byte header with format 1 (JPEG), then the JPEG.
        resources.AddRange("8BIM"u8.ToArray());
        resources.AddRange([0x04, 0x0C, 0x00, 0x00]);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(28 + jpeg.Length));
        resources.AddRange(size);
        var thumbHeader = new byte[28];
        thumbHeader[3] = 1;
        resources.AddRange(thumbHeader);
        resources.AddRange(jpeg);

        string hex = Convert.ToHexString(resources.ToArray());
        var lines = Enumerable.Range(0, (hex.Length + 63) / 64).Select(i => "% " + hex.Substring(i * 64, Math.Min(64, hex.Length - i * 64)));
        string text = "%!PS-Adobe-3.0 EPSF-3.0\r\n%%EndComments\r\n%BeginPhotoshop: " + resources.Count + "\r\n"
            + string.Join("\r\n", lines) + "\r\n%EndPhotoshop\r\nshowpage\r\n";

        Assert.Equal(jpeg, EpsPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes(text))));
    }

    [Fact]
    public void EpsiPreview_BecomesGrayscaleBmpWithZeroAsWhite()
    {
        // 4x2, 1 bit: row 0 = 1010 (black, white, black, white), row 1 = 0000 (all white).
        string text = "%!PS-Adobe-3.0 EPSF-3.0\n%%BeginPreview: 4 2 1 2\n% A0\n% 00\n%%EndPreview\n";
        var bmp = EpsPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes(text)));

        Assert.NotNull(bmp);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
        int pixels = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, bmp.AsSpan(pixels, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bmp.AsSpan(pixels + 4, 4).ToArray());
    }

    [Fact]
    public void EpsiPreview_EightBitDepth()
    {
        string text = "%%BeginPreview: 2 1 8 1\n% 00FF\n%%EndPreview\n";
        var bmp = EpsPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes(text)));
        int pixels = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        Assert.Equal(new byte[] { 255, 0 }, bmp.AsSpan(pixels, 2).ToArray());
    }

    [Theory]
    [InlineData(PostScript)]
    [InlineData("%%BeginPreview: 4 2 1 2\n% A0\n%%EndPreview\n")] // fewer rows than declared
    [InlineData("%%BeginPreview: 4 2 1 2\n% A0\n% 00\n")] // no end marker
    [InlineData("%%BeginPreview: 0 2 1 2\n% A0\n% 00\n%%EndPreview\n")] // bad size
    [InlineData("%%BeginPreview: 4 2 3 2\n% A0\n% 00\n%%EndPreview\n")] // bad depth
    [InlineData("")]
    public void NoUsablePreview_ReturnsNull(string text) =>
        Assert.Null(EpsPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes(text))));

    [Fact]
    public void MissingFile_ReturnsNull() => Assert.Null(EpsPreview.Read(@"C:\does\not\exist.eps"));
}
