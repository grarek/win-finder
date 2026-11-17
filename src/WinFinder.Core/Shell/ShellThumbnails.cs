using System.Runtime.InteropServices;

namespace WinFinder.Core.Shell;

/// <summary>Premultiplied BGRA pixels, top-down rows.</summary>
public sealed record ThumbnailData(int Width, int Height, byte[] Pixels);

public enum ThumbnailMode
{
    /// <summary>The file-type icon only (fast; used in list/column views).</summary>
    Icon,
    /// <summary>A content thumbnail when the type has one, otherwise the icon.</summary>
    Thumbnail,
}

/// <summary>Shell icons and thumbnails via IShellItemImageFactory on a pool of STA threads.</summary>
public sealed class ShellThumbnails : IDisposable
{
    private readonly StaWorker _worker = new("Thumbnails", threadCount: 3, lifo: true);
    public Task<ThumbnailData?> GetAsync(string path, int size, ThumbnailMode mode, CancellationToken ct = default) =>
        _worker.RunAsync(() => Get(path, size, mode), ct);

    /// <summary>Synchronous version; must be called on an STA thread.</summary>
    public static ThumbnailData? Get(string path, int size, ThumbnailMode mode)
    {
        IShellItem? item = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            item = Native.CreateItem(path);
            var factory = (IShellItemImageFactory)item;
            var flags = mode == ThumbnailMode.Icon ? SIIGBF.ICONONLY : SIIGBF.RESIZETOFIT;
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm);
            if (hr < 0 || hbm == IntPtr.Zero) return null;
            return ReadBitmap(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    private static ThumbnailData? ReadBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };
        var pixels = new byte[w * h * 4];
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }

        FixAlpha(pixels, bmp.bmBitsPixel);
        return new ThumbnailData(w, h, pixels);
    }

    /// <summary>
    /// Many thumbnail handlers return opaque bitmaps with an all-zero alpha channel, which would render
    /// fully transparent. If no pixel has alpha, make the image opaque.
    /// </summary>
    internal static void FixAlpha(byte[] pixels, int bitsPerPixel)
    {
        bool anyAlpha = false;
        if (bitsPerPixel == 32)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 0) { anyAlpha = true; break; }
        }
        if (anyAlpha) return;
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
    }

    public void Dispose() => _worker.Dispose();
}
