using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using WinFinder.App.Services;

namespace WinFinder.App.ViewModels;

/// <summary>
/// Shared, persisted layout values that item templates bind to (list column widths, icon size).
/// Exposed through <see cref="FileItemViewModel.Layout"/> so x:Bind templates can reach it.
/// </summary>
public sealed partial class ListLayout : ObservableObject
{
    public static ListLayout Instance { get; } = new();

    private ListLayout() { }

    public void Load()
    {
        var s = AppServices.Settings.Current;
        NameWidth = s.NameColumnWidth;
        DateWidth = s.DateColumnWidth;
        SizeWidth = s.SizeColumnWidth;
        KindWidth = s.KindColumnWidth;
        IconSize = s.IconSize;
        ColumnWidth = s.ColumnWidth;
    }

    /// <summary>
    /// Default width of folder columns in column view (Shift+drag sets it). Folders the user
    /// resized on their own keep their width; see <see cref="ColumnWidthFor"/>.
    /// </summary>
    [ObservableProperty]
    private double _columnWidth = 240;

    private const int MaxRememberedColumnWidths = 2000;

    /// <summary>The width a column showing <paramref name="folder"/> opens at.</summary>
    public double ColumnWidthFor(string folder) =>
        AppServices.Settings.Current.FolderColumnWidths.TryGetValue(folder, out var w) && w > 0 ? w : ColumnWidth;

    /// <summary>Remembers a width the user dragged for one folder's column.</summary>
    public void RememberColumnWidth(string folder, double width)
    {
        var widths = AppServices.Settings.Current.FolderColumnWidths;
        widths[folder] = width;
        if (widths.Count > MaxRememberedColumnWidths)
            foreach (var key in widths.Keys.Where(k => !string.Equals(k, folder, StringComparison.OrdinalIgnoreCase))
                                           .Take(widths.Count - MaxRememberedColumnWidths).ToList())
                widths.Remove(key);
        AppServices.Settings.SaveSoon();
    }

    /// <summary>Drops a folder's own width so its column follows <see cref="ColumnWidth"/> again.</summary>
    public void ForgetColumnWidth(string folder)
    {
        if (AppServices.Settings.Current.FolderColumnWidths.Remove(folder))
            AppServices.Settings.SaveSoon();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameGrid))]
    private double _nameWidth = 320;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateGrid))]
    private double _dateWidth = 200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeGrid))]
    private double _sizeWidth = 90;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindGrid))]
    private double _kindWidth = 150;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconCellWidth))]
    private double _iconSize = 96;

    public GridLength NameGrid => new(NameWidth);
    public GridLength DateGrid => new(DateWidth);
    public GridLength SizeGrid => new(SizeWidth);
    public GridLength KindGrid => new(KindWidth);
    public double IconCellWidth => Math.Max(IconSize + 28, 96);

    partial void OnNameWidthChanged(double value) { AppServices.Settings.Current.NameColumnWidth = value; AppServices.Settings.SaveSoon(); }
    partial void OnDateWidthChanged(double value) { AppServices.Settings.Current.DateColumnWidth = value; AppServices.Settings.SaveSoon(); }
    partial void OnSizeWidthChanged(double value) { AppServices.Settings.Current.SizeColumnWidth = value; AppServices.Settings.SaveSoon(); }
    partial void OnKindWidthChanged(double value) { AppServices.Settings.Current.KindColumnWidth = value; AppServices.Settings.SaveSoon(); }
    partial void OnIconSizeChanged(double value) { AppServices.Settings.Current.IconSize = value; AppServices.Settings.SaveSoon(); }
    partial void OnColumnWidthChanged(double value) { AppServices.Settings.Current.ColumnWidth = value; AppServices.Settings.SaveSoon(); }
}
