using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using WinFinder.App.Interop;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;
using WinFinder.Core.Navigation;

namespace WinFinder.App.Views;

public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), handledEventsToo: true);
    }

    public SidebarViewModel Sidebar => SidebarViewModel.Instance;

    /// <summary>Raised when the user picks a place. The bool is true to open it in a new tab.</summary>
    public event Action<Location, bool>? NavigateRequested;

    /// <summary>Raised after files are dropped onto a sidebar folder/drive (for status refresh).</summary>
    public event Action? FilesDropped;

    public IntPtr OwnerHwnd { get; set; }

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SidebarItem item)
        {
            ClearOtherSelections(sender as ListView);
            NavigateRequested?.Invoke(item.Location, WindowHelper.IsCtrlDown());
        }
    }

    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarItem item)
        {
            NavigateRequested?.Invoke(item.Location, true);
            e.Handled = true;
        }
    }

    private void ClearOtherSelections(ListView? keep)
    {
        foreach (var list in new[] { FavoritesList, LocationsList, TagsList })
            if (!ReferenceEquals(list, keep)) list.SelectedItem = null;
    }

    /// <summary>Highlights the sidebar entry matching the tab's location (if any).</summary>
    public void Highlight(Location location)
    {
        SidebarItem? Match(IEnumerable<SidebarItem> items) => items.FirstOrDefault(i =>
            location.Kind == LocationKind.Tag ? i.Kind == SidebarKind.Tag && i.TagId == location.TagId
            : location.Kind == LocationKind.Computer ? i.Kind == SidebarKind.Computer
            : location.IsFolder && i.Kind != SidebarKind.Tag && i.Kind != SidebarKind.Computer &&
              string.Equals(i.Path, location.Path, StringComparison.OrdinalIgnoreCase));

        var fav = Match(Sidebar.Favorites);
        var loc = fav is null ? Match(Sidebar.Locations) : null;
        var tag = fav is null && loc is null ? Match(Sidebar.Tags) : null;
        FavoritesList.SelectedItem = fav;
        LocationsList.SelectedItem = loc;
        TagsList.SelectedItem = tag;
    }

    // ----- context menu ------------------------------------------------------------------------

    private void ItemMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not SidebarItem item) return;
        menu.Items.Clear();

        var open = new MenuFlyoutItem { Text = "Open in New Tab", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => NavigateRequested?.Invoke(item.Location, true);
        menu.Items.Add(open);

        if (item.Kind is SidebarKind.Favorite or SidebarKind.Drive)
        {
            var explorer = new MenuFlyoutItem { Text = "Open in File Explorer" };
            explorer.Click += (_, _) => Core.Shell.ShellInfo.Open(item.Path);
            menu.Items.Add(explorer);
        }

        if (item.IsRemovable)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "Remove from Favorites", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => Sidebar.RemoveFavorite(item);
            menu.Items.Add(remove);
        }
    }

    // ----- drag and drop -----------------------------------------------------------------------

    /// <summary>Dropping folders on the Favorites list (not on an item) pins them.</summary>
    private async void Favorites_DragOver(object sender, DragEventArgs e)
    {
        if (!TransferService.HasFiles(e.DataView)) return; // let the ListView handle reordering
        var deferral = e.GetDeferral();
        try
        {
            var paths = await TransferService.GetDragPathsCachedAsync(e.DataView);
            bool anyFolder = paths.Any(Directory.Exists);
            e.AcceptedOperation = anyFolder ? DataPackageOperation.Link : DataPackageOperation.None;
            e.DragUIOverride.Caption = anyFolder ? "Add to Favorites" : "";
            e.DragUIOverride.IsCaptionVisible = anyFolder;
        }
        finally { deferral.Complete(); }
    }

    private async void Favorites_Drop(object sender, DragEventArgs e)
    {
        if (!TransferService.HasFiles(e.DataView)) return;
        var paths = await TransferService.GetPathsAsync(e.DataView);
        foreach (var p in paths.Where(Directory.Exists))
            Sidebar.AddFavorite(p);
    }

    /// <summary>Dropping onto a specific favorite or drive copies/moves into it.</summary>
    private async void Item_DragOver(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarItem { CanAcceptDrop: true } item) return;
        if (!TransferService.HasFiles(e.DataView)) return;
        e.Handled = true;
        await TransferService.HandleDragOverAsync(e, item.Path);
    }

    private async void Item_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarItem { CanAcceptDrop: true } item) return;
        if (!TransferService.HasFiles(e.DataView)) return;
        e.Handled = true;
        await TransferService.HandleDropAsync(e, item.Path, OwnerHwnd);
        FilesDropped?.Invoke();
    }
}
