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
}
