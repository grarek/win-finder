using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Shell;

namespace WinFinder.App.Views;

/// <summary>
/// Quick Look: Space shows a preview of the selected item over the window. Images, text/code,
/// PDF/HTML/SVG (WebView2), audio/video (MediaPlayerElement), and a large-icon fallback.
/// </summary>
public sealed partial class QuickLookOverlay : UserControl
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".ico", ".avif" };

    private static readonly HashSet<string> WebTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".htm", ".html", ".svg", ".mhtml" };

    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma", ".ogg", ".opus", ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm" };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".config", ".env", ".gitignore", ".gitattributes", ".editorconfig", ".cs", ".csproj", ".sln", ".props",
        ".targets", ".vb", ".fs", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".go", ".rs", ".rb", ".py", ".ps1", ".psm1",
        ".sh", ".bash", ".bat", ".cmd", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".sql", ".php",
        ".swift", ".lua", ".r", ".dart", ".vue", ".svelte", ".tf", ".hcl", ".dockerfile", ".reg", ".srt", ".vtt",
    };

    private const int MaxTextBytes = 512 * 1024;

    private FileItemViewModel? _item;
    private WebView2? _web;
    private MediaPlayerElement? _media;
    private int _version;

    public QuickLookOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => SizeCard();
    }

    public bool IsOpen => Visibility == Visibility.Visible;
    public FileItemViewModel? Item => _item;
    public IntPtr OwnerHwnd { get; set; }

    public event Action? FullScreenToggled;
    public event Action? Closed;

    public bool IsFullScreen { get; set; }

    private void SizeCard()
    {
        double w = ActualWidth, h = ActualHeight;
        if ((w <= 0 || h <= 0) && XamlRoot is not null)
        {
            // Not laid out yet (we were collapsed): size against the window instead.
            w = XamlRoot.Size.Width;
            h = XamlRoot.Size.Height;
        }
        if (w <= 0 || h <= 0) return;
        double factor = IsFullScreen ? 1.0 : 0.82;
        Card.Width = Math.Max(320, w * factor);
        Card.Height = Math.Max(240, h * factor);
        Card.CornerRadius = new CornerRadius(IsFullScreen ? 0 : 12);
    }

    public async Task ShowAsync(FileItemViewModel item)
    {
        _item = item;
        int version = ++_version;
        Visibility = Visibility.Visible;
        SizeCard();
        TitleText.Text = item.Name;
        SubtitleText.Text = item.IsFolder ? item.KindText : $"{item.KindText} - {item.SizeText}";
        ResetContent();
        Spinner.IsActive = true;
        Focus(FocusState.Programmatic);

        bool mediaStillOpening = false;
        try
        {
            string ext = item.Entry.Extension;
            if (item.IsFolder) await ShowFallbackAsync(item, version);
            else if (ImageTypes.Contains(ext)) await ShowImageAsync(item, version);
            else if (WebTypes.Contains(ext)) await ShowWebAsync(item, version);
            else if (MediaTypes.Contains(ext)) { ShowMedia(item, version); mediaStillOpening = true; }
            else if (TextTypes.Contains(ext) || await LooksLikeTextAsync(item.Path)) await ShowTextAsync(item, version);
            else await ShowFallbackAsync(item, version);
        }
        catch
        {
            if (version == _version) await ShowFallbackAsync(item, version);
        }
        finally
        {
            // Media keeps its spinner until playback opens (MediaOpened/MediaFailed clear it).
            if (version == _version && !mediaStillOpening) Spinner.IsActive = false;
        }
    }

    /// <summary>Closes immediately — never waits on a video that is still buffering.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _version++;
        ResetContent();
        Spinner.IsActive = false;
        Visibility = Visibility.Collapsed;
        _item = null;
        Closed?.Invoke();
    }

    private void ResetContent()
    {
        if (ImageView.Source is BitmapImage { IsPlaying: true } playing) playing.Stop();
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        TextScroller.Visibility = Visibility.Collapsed;
        FallbackView.Visibility = Visibility.Collapsed;
        if (_web is not null)
        {
            _web.Visibility = Visibility.Collapsed;
            try { _web.Source = new Uri("about:blank"); } catch { }
        }
        ReleaseMedia();
    }

    private const long MaxImageBytes = 256L * 1024 * 1024;

    private async Task ShowImageAsync(FileItemViewModel item, int version)
    {
        // Read the file off the UI thread: a big image on a slow share must not freeze the window.
        string path = item.Path;
        byte[]? bytes = await Task.Run(() =>
        {
            var info = new FileInfo(path);
            return info.Length > MaxImageBytes ? null : File.ReadAllBytes(path);
        });
        if (version != _version) return;
        if (bytes is null) { await ShowFallbackAsync(item, version); return; }

        bool animated = string.Equals(item.Entry.Extension, ".gif", StringComparison.OrdinalIgnoreCase);
        var bitmap = new BitmapImage { AutoPlay = true };
        if (!animated)
        {
            // Decode near display size: sharp, but without holding a 50-megapixel bitmap in memory.
            // (Animated GIFs are decoded at full size so every frame plays.)
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            double side = double.IsNaN(Card.Width) ? 1600 : Math.Max(Card.Width, Card.Height);
            bitmap.DecodePixelType = DecodePixelType.Physical;
            bitmap.DecodePixelWidth = (int)Math.Clamp(side * scale, 512, 3840);
        }
        using (var stream = new MemoryStream(bytes))
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        if (version != _version) return;

        ImageView.Source = bitmap;
        ImageView.Visibility = Visibility.Visible;
        if (bitmap.IsAnimatedBitmap && !bitmap.IsPlaying) bitmap.Play();
    }

    private async Task ShowWebAsync(FileItemViewModel item, int version)
    {
        if (_web is null)
        {
            _web = new WebView2();
            ContentHost.Children.Insert(0, _web);
        }
        _web.Visibility = Visibility.Visible;
        await _web.EnsureCoreWebView2Async();
        if (version != _version) return;
        _web.Source = new Uri(item.Path);
    }

    /// <summary>
    /// Audio/video. Opening, stopping and closing a source can block for seconds while it buffers
    /// (large files, network shares), so the player is driven from a background thread and the UI
    /// thread never waits on it. MediaPlayer is agile, so this is safe.
    /// </summary>
    private void ShowMedia(FileItemViewModel item, int version)
    {
        if (_media is null)
        {
            _media = new MediaPlayerElement { AreTransportControlsEnabled = true, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            ContentHost.Children.Insert(0, _media);
        }
        _media.Visibility = Visibility.Visible;

        var player = new MediaPlayer { AutoPlay = true };
        _player = player;
        player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _version) Spinner.IsActive = false;
        });
        player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (version != _version) return;
            ReleaseMedia();
            Spinner.IsActive = false;
            await ShowFallbackAsync(item, version);
        });
        _media.SetMediaPlayer(player);

        string path = item.Path;
        _ = Task.Run(() =>
        {
            try { player.Source = MediaSource.CreateFromUri(new Uri(path)); }
            catch { }
        });
    }

    private MediaPlayer? _player;

    /// <summary>Detaches the player at once and tears it down in the background.</summary>
    private void ReleaseMedia()
    {
        var player = _player;
        _player = null;
        if (_media is not null)
        {
            _media.SetMediaPlayer(null);
            _media.Visibility = Visibility.Collapsed;
        }
        if (player is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                player.Pause();
                player.Source = null;
                player.Dispose();
            }
            catch { }
        });
    }

    private async Task ShowTextAsync(FileItemViewModel item, int version)
    {
        string text = await Task.Run(() =>
        {
            using var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxTextBytes];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            string s = new(buffer, 0, read);
            if (!reader.EndOfStream) s += "\n\n… (preview truncated)";
            return s;
        });
        if (version != _version) return;
        TextView.Text = text;
        TextScroller.Visibility = Visibility.Visible;
        TextScroller.ChangeView(0, 0, null, disableAnimation: true);
    }

    private async Task ShowFallbackAsync(FileItemViewModel item, int version)
    {
        FallbackName.Text = item.Name;
        string details = item.IsFolder ? item.KindText : $"{item.KindText}\n{item.SizeText}";
        details += $"\nModified {Format.Date(item.Entry.Modified, DateTime.Now)}";
        if (item.IsFolder)
        {
            int count = await Task.Run(() =>
            {
                try { return Directory.EnumerateFileSystemEntries(item.Path).Take(100_000).Count(); }
                catch { return -1; }
            });
            if (count >= 0) details = $"{item.KindText}\n{Format.ItemCount(count)}\nModified {Format.Date(item.Entry.Modified, DateTime.Now)}";
        }
        if (version != _version) return;
        FallbackDetails.Text = details;
        FallbackView.Visibility = Visibility.Visible;
        FallbackIcon.Source = await AppServices.Icons.GetAsync(item.Entry, 160, ThumbnailMode.Thumbnail);
    }

    /// <summary>Treat small files without NUL bytes in their first 4 KB as text.</summary>
    private static Task<bool> LooksLikeTextAsync(string path) => Task.Run(() =>
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > 8 * 1024 * 1024) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            int n = fs.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < n; i++) if (buffer[i] == 0) return false;
            return true;
        }
        catch { return false; }
    });

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private void Card_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // don't close when clicking the card

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        FullScreenToggled?.Invoke();
    }

    public void SetFullScreen(bool on)
    {
        IsFullScreen = on;
        FullScreenGlyph.Glyph = on ? "" : "";
        SizeCard();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        ShellInfo.Open(_item.Path, OwnerHwnd);
        Close();
    }
}
