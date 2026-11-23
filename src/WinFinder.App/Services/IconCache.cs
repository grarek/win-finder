using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Shell;

namespace WinFinder.App.Services;

/// <summary>
/// LRU cache of shell icons/thumbnails as XAML image sources. Generic file-type icons are
/// shared per extension; folder icons are per folder; anything else that can differ per file (exe, lnk, thumbnails) is keyed by
/// path + size + modified time. Must be used from the UI thread.
/// </summary>
public sealed class IconCache
{
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".msc", ".appref-ms", ".cpl",
    };

    private readonly ShellThumbnails _thumbnails;
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _map = new();
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new();
    private const int Capacity = 2500;

    public IconCache(ShellThumbnails thumbnails) => _thumbnails = thumbnails;

    public static string KeyFor(FileEntry entry, int pixelSize, ThumbnailMode mode)
    {
        if (mode == ThumbnailMode.Icon && !entry.IsDrive)
        {
            // Folders can't share one icon: known folders (Documents, Downloads, 3D Objects...) and
            // folders customized with desktop.ini each have their own, and a shared entry would show
            // whichever loaded first on every folder. Keyed by path only, so a folder's icon isn't
            // reloaded each time its contents (and so its modified time) change.
            if (entry.IsDirectory) return $"icon:<dir>:{entry.FullPath.ToLowerInvariant()}:{pixelSize}";
            if (!PerFileIconExtensions.Contains(entry.Extension)) return $"icon:{entry.Extension.ToLowerInvariant()}:{pixelSize}";
        }
        return $"{mode}:{entry.FullPath}:{pixelSize}:{entry.Modified.Ticks}";
    }

    public ImageSource? TryGet(string key)
    {
        if (!_map.TryGetValue(key, out var node)) return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Image;
    }

    public Task<ImageSource?> GetAsync(FileEntry entry, double logicalSize, ThumbnailMode mode)
    {
        int px = (int)Math.Ceiling(logicalSize * AppServices.DisplayScale);
        string key = KeyFor(entry, px, mode);
        if (TryGet(key) is { } cached) return Task.FromResult<ImageSource?>(cached);
        if (_inflight.TryGetValue(key, out var pending)) return pending;

        var task = LoadAsync(entry.FullPath, px, mode, key);
        // Only track loads that are still running; a load that already finished must not
        // leave a stale entry behind (its finally block ran before we could add it).
        if (!task.IsCompleted) _inflight[key] = task;
        return task;
    }

    private async Task<ImageSource?> LoadAsync(string path, int px, ThumbnailMode mode, string key)
    {
        try
        {
            // Windows has no EPS thumbnail handler, so show the preview image embedded in the file.
            ThumbnailData? data = mode == ThumbnailMode.Thumbnail && EpsPreview.IsEps(path)
                ? await LoadEpsPreviewAsync(path, px)
                : null;

            // The shell occasionally fails a first request while its icon cache warms up; retry briefly.
            for (int attempt = 0; attempt < 3 && data is null; attempt++)
            {
                if (attempt > 0) await Task.Delay(150 * attempt);
                data = await _thumbnails.GetAsync(path, px, mode);
            }
            if (data is null) return null;

            var bitmap = new WriteableBitmap(data.Width, data.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(data.Pixels, 0, data.Pixels.Length);
            bitmap.Invalidate();
            Add(key, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inflight.Remove(key);
        }
    }

    /// <summary>Decodes an EPS file's embedded preview, scaled to fit within <paramref name="px"/> square.</summary>
    private static async Task<ThumbnailData?> LoadEpsPreviewAsync(string path, int px)
    {
        byte[]? encoded = await Task.Run(() => EpsPreview.Read(path));
        if (encoded is null) return null;
        try
        {
            using var stream = new MemoryStream(encoded).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            if (w == 0 || h == 0) return null;
            double scale = Math.Min(1.0, (double)px / Math.Max(w, h));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)Math.Round(w * scale)),
                ScaledHeight = Math.Max(1, (uint)Math.Round(h * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return new ThumbnailData((int)transform.ScaledWidth, (int)transform.ScaledHeight, pixels.DetachPixelData());
        }
        catch
        {
            return null;
        }
    }

    private void Add(string key, ImageSource image)
    {
        if (_map.TryGetValue(key, out var existing)) _lru.Remove(existing);
        var node = _lru.AddFirst((key, image));
        _map[key] = node;
        while (_lru.Count > Capacity)
        {
            var last = _lru.Last!;
            _map.Remove(last.Value.Key);
            _lru.RemoveLast();
        }
    }
}
