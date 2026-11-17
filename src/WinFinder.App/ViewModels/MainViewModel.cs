using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinder.Core.Navigation;
using WinFinder.Core.Settings;

namespace WinFinder.App.ViewModels;

/// <summary>Per-window state: the tab strip and which tab is selected.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = new();
    public SidebarViewModel Sidebar => SidebarViewModel.Instance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewModeIndex), nameof(HasTab))]
    private BrowserTabViewModel? _selectedTab;

    public bool HasTab => SelectedTab is not null;

    /// <summary>Segmented control index: 0 icons, 1 list, 2 columns.</summary>
    public int ViewModeIndex
    {
        get => (int)(SelectedTab?.ViewMode ?? ViewMode.List);
        set
        {
            if (SelectedTab is null || value < 0 || value == (int)SelectedTab.ViewMode) return;
            SelectedTab.SetViewMode((ViewMode)value);
        }
    }

    partial void OnSelectedTabChanged(BrowserTabViewModel? oldValue, BrowserTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnTabPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.ViewMode)) OnPropertyChanged(nameof(ViewModeIndex));
    }

    public BrowserTabViewModel CreateTab(Location location, ViewMode? view = null, int index = -1, bool select = true)
    {
        var tab = new BrowserTabViewModel(location, view);
        Insert(tab, index, select);
        tab.Start();
        return tab;
    }

    public void Insert(BrowserTabViewModel tab, int index = -1, bool select = true)
    {
        if (index < 0 || index > Tabs.Count) Tabs.Add(tab);
        else Tabs.Insert(index, tab);
        if (select) SelectedTab = tab;
    }

    /// <summary>Removes the tab from this window (without disposing it, so it can move to another window).</summary>
    public void Detach(BrowserTabViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0) return;
        bool wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.RemoveAt(index);
        if (wasSelected)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }
}
