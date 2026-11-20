using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using WinFinder.App.Interop;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;
using WinFinder.Core.FileSystem;

namespace WinFinder.App.Views;

/// <summary>What a browser view needs from its window.</summary>
public interface IBrowserHost
{
    IntPtr Hwnd { get; }

    /// <summary>When the window last became active (a click that activates it shouldn't start a rename).</summary>
    DateTime LastActivatedUtc { get; }

    void OpenItems(BrowserTabViewModel tab, IReadOnlyList<FileItemViewModel> items, bool newTab);
    void OpenInNewTab(string folder);
    void ShowContextMenu(BrowserTabViewModel tab, IReadOnlyList<FileItemViewModel>? items, Point position);
}

public sealed class ColumnTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Folder { get; set; }
    public DataTemplate? Preview { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is PreviewColumnViewModel ? Preview : Folder;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>The content of one tab: list, icon and column views over the tab's view model.</summary>
public sealed partial class BrowserView : UserControl
{
    private readonly IBrowserHost _host;
    private string _typeAhead = "";
    private DateTime _lastTypeAhead;
    private bool _syncingScope;

    public BrowserView(BrowserTabViewModel tab, IBrowserHost host)
    {
        Tab = tab;
        _host = host;
        InitializeComponent();

        Tab.SelectRequested += OnSelectRequested;
        Tab.MoveSelectionRequested += OnMoveSelectionRequested;
        // Deferred so a focus move requested from a click/double-click lands after the pointer
        // release (which would otherwise put focus back on the list that was clicked).
        Tab.FocusRequested += () => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusActiveList);
        Loaded += (_, _) => FocusActiveList();
        Tab.Columns.ColumnAppended += ScrollColumnsToEnd;
        Tab.Columns.SelectInColumnRequested += (col, path) => SelectInColumn(col, path, attempts: 0);
        Tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserTabViewModel.Location)) SyncScopeSelector();
        };
        SyncScopeSelector();

        AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), handledEventsToo: true);
        // One handler for every list (including the column view's templated ones). handledEventsToo so
        // a list part that marks the right-tap handled can't swallow the menu.
        AddHandler(RightTappedEvent, new RightTappedEventHandler(OnAnyRightTapped), handledEventsToo: true);
    }

    public BrowserTabViewModel Tab { get; }
    public ListLayout Layout => ListLayout.Instance;

    // ----- x:Bind helpers --------------------------------------------------------------------

    public string SortGlyph(SortKey current, bool descending, int column) =>
        (int)current == column ? (descending ? "" : "") : "";

    public bool ShowListSpinner(bool loading, bool columnView) => loading && !columnView;

    // ----- selection plumbing ----------------------------------------------------------------

    private void FocusActiveList() => FocusActiveList(0);

    /// <summary>Focuses the active list; retries after layout because a just-shown view can't take focus yet.</summary>
    private void FocusActiveList(int attempt)
    {
        if (Visibility != Visibility.Visible || !IsLoaded) return;
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox) return; // don't steal from search/rename
        var list = ActiveList();
        bool focused = list is not null && list.Visibility == Visibility.Visible && list.Focus(FocusState.Programmatic);
        if (!focused && attempt < 8)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusActiveList(attempt + 1));
        // No auto-selection here: in column view selecting a folder opens it (and makes it current).
    }

    private ListViewBase? ActiveList() =>
        Tab.IsColumnView ? ColumnListFor(Tab.Columns.ActiveColumn)
        : Tab.IsIconView ? IconGrid
        : FileList;

    private ListView? ColumnListFor(ColumnViewModel? column)
    {
        if (column is null) return null;
        var container = ColumnsHost.ContainerFromItem(column) as DependencyObject;
        return container is null ? null : FindDescendant<ListView>(container);
    }

    private static IReadOnlyList<FileItemViewModel> Selected(ListViewBase list) =>
        list.SelectedItems.OfType<FileItemViewModel>().ToList();

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelPendingRename();
        if (sender is ListViewBase list) Tab.SelectedItems = Selected(list);
    }

    private void Column_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelPendingRename();
        if (sender is not ListView list || list.DataContext is not ColumnViewModel column) return;
        var selected = Selected(list);
        Tab.SelectedItems = selected;
        Tab.Columns.OnSelectionChanged(column, selected);
    }

    private void Column_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView list || list.DataContext is not ColumnViewModel column) return;
        if (!ReferenceEquals(Tab.Columns.ActiveColumn, column)) Tab.Columns.SetActive(column);
        Tab.SelectedItems = Selected(list); // status bar and commands follow the focused column
    }

    private async void OnSelectRequested(IReadOnlyList<string> paths, bool beginRename)
    {
        if (Tab.IsColumnView)
        {
            if (paths.Count > 0 && Tab.Columns.ActiveColumn is { } active)
                SelectInColumn(active, paths[0], attempts: 0);
            return;
        }
        // In a brand-new window the folder can finish listing before the list is bound and laid
        // out; wait for it briefly rather than dropping the selection.
        for (int attempt = 0; attempt < 40 && (!IsLoaded || ActiveList() is not { Items.Count: > 0 }); attempt++)
            await Task.Delay(50);
        var list = ActiveList();
        if (list is null) return;
        var lookup = list.Items.OfType<FileItemViewModel>().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        list.SelectedItems.Clear();
        FileItemViewModel? first = null;
        foreach (var p in paths)
        {
            if (!lookup.TryGetValue(p, out var item)) continue;
            list.SelectedItems.Add(item);
            first ??= item;
        }
        if (first is null) return;
        list.ScrollIntoView(first);
        if (!beginRename) list.Focus(FocusState.Programmatic);
    }

    private void SelectInColumn(ColumnViewModel column, string path, int attempts)
    {
        var list = ColumnListFor(column);
        var item = list?.Items.OfType<FileItemViewModel>().FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (list is null || item is null)
        {
            // The column's container may not exist yet; try again after layout.
            if (attempts < 10)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => SelectInColumn(column, path, attempts + 1));
            return;
        }
        list.SelectedItem = item;
        list.ScrollIntoView(item);
        list.Focus(FocusState.Programmatic);
    }

    private void OnMoveSelectionRequested(int delta)
    {
        var list = ActiveList();
        if (list is null || list.Items.Count == 0) return;
        int index = Math.Clamp(list.SelectedIndex < 0 ? 0 : list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
    }

    private void ScrollColumnsToEnd()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ColumnScroller.UpdateLayout();
            ColumnScroller.ChangeView(ColumnScroller.ScrollableWidth, null, null);
        });
    }

    // ----- lazy icons --------------------------------------------------------------------------

    private void ListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is FileItemViewModel vm) vm.EnsureIcon(16);
    }

    private void IconGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is FileItemViewModel vm) vm.EnsureThumbnail(Layout.IconSize);
    }

    public void SelectAll() => ActiveList()?.SelectAll();

    /// <summary>Menu key / Shift+F10: open the context menu for the selection (or the folder background).</summary>
    public void ShowContextMenuFromKeyboard()
    {
        var list = ActiveList();
        if (list is null) return;
        var selected = Selected(list);
        UIElement anchor = list;
        if (selected.Count > 0 && list.ContainerFromItem(selected[0]) is UIElement container) anchor = container;
        var point = anchor.TransformToVisual(null).TransformPoint(new Point(24, 16));
        _host.ShowContextMenu(Tab, selected.Count > 0 ? selected : null, point);
    }

    /// <summary>Called when the icon-size slider changes so visible cells re-render sharply.</summary>
    public void RefreshThumbnails()
    {
        foreach (var item in IconGrid.Items.OfType<FileItemViewModel>())
            if (item.Thumbnail is not null) item.EnsureThumbnail(Layout.IconSize);
    }

    // ----- opening, context menus, middle-click --------------------------------------------------

    private static FileItemViewModel? ItemFrom(object? source) =>
        (source as FrameworkElement)?.DataContext as FileItemViewModel;

    private void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        CancelPendingRename();
        if (ItemFrom(e.OriginalSource) is not { } item || item.IsRenaming) return;
        _host.OpenItems(Tab, [item], WindowHelper.IsCtrlDown() && item.IsFolder);
        e.Handled = true;
    }

    /// <summary>Right-click on an item (its menu) or on empty space in a list (the folder's menu), like Explorer.</summary>
    private void OnAnyRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Find the item list under the pointer; leave text boxes (rename, search) their own menu.
        ListViewBase? list = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, this); node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox) return;
            if (node is ListViewBase found) { list = found; break; }
        }
        if (list is null) return;

        var item = ItemFrom(e.OriginalSource);
        IReadOnlyList<FileItemViewModel>? targets = null;
        if (item is not null)
        {
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Clear();
                list.SelectedItems.Add(item);
            }
            targets = Selected(list);
        }
        else if (list.DataContext is ColumnViewModel column)
        {
            Tab.Columns.SetActive(column); // keep the selection: in column view it decides which columns are open
        }
        else
        {
            list.SelectedItems.Clear(); // Explorer deselects when you right-click empty space
        }
        _host.ShowContextMenu(Tab, targets, e.GetPosition(null));
        e.Handled = true;
    }

    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _renameTimer?.Stop(); // e.g. the second click of a double-click
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsMiddleButtonPressed) return;
        if (ItemFrom(e.OriginalSource) is { IsFolder: true } item)
        {
            _host.OpenInNewTab(item.Path);
            e.Handled = true;
        }
    }

    private async void Expander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileItemViewModel item)
            await Tab.Tree.ToggleAsync(item);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key && Enum.TryParse<SortKey>(key, out var sortKey))
            Tab.SetSort(sortKey);
    }

    // ----- keyboard ------------------------------------------------------------------------------

    private async void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FileList.SelectedItems.Count != 1 || FileList.SelectedItem is not FileItemViewModel item) return;
        if (e.Key == VirtualKey.Right && item.CanExpand)
        {
            if (!item.IsExpanded) await Tab.Tree.ExpandAsync(item);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            if (item.IsExpanded) Tab.Tree.Collapse(item);
            else if (Tab.Tree.ParentOf(item) is { } parent)
            {
                FileList.SelectedItem = parent;
                FileList.ScrollIntoView(parent);
            }
            e.Handled = true;
        }
    }

    private void Column_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list || list.DataContext is not ColumnViewModel column) return;
        int index = Tab.Columns.Columns.IndexOf(column);
        if (e.Key == VirtualKey.Right)
        {
            if (list.SelectedItem is FileItemViewModel { IsFolder: true } &&
                index + 1 < Tab.Columns.Columns.Count &&
                Tab.Columns.Columns[index + 1] is ColumnViewModel next)
            {
                FocusColumn(next, selectFirst: true);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            if (index > 0 && Tab.Columns.Columns[index - 1] is ColumnViewModel previous)
            {
                list.SelectedItems.Clear();
                FocusColumn(previous, selectFirst: false);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Entering a column with nothing selected: the first Up/Down selects the first/last item.
    /// Runs in the tunnelling phase because the focused item would otherwise move focus first,
    /// skipping item 0.
    /// </summary>
    private void Column_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list || list.SelectedIndex >= 0 || list.Items.Count == 0) return;
        if (e.Key is not (VirtualKey.Down or VirtualKey.Up) || WindowHelper.CurrentModifiers() != Core.Input.KeyModifiers.None) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        int target = e.Key == VirtualKey.Down ? 0 : list.Items.Count - 1;
        list.SelectedIndex = target;
        (list.ContainerFromIndex(target) as Control)?.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void FocusColumn(ColumnViewModel column, bool selectFirst)
    {
        var list = ColumnListFor(column);
        if (list is null) return;
        Tab.Columns.SetActive(column);
        if (selectFirst && list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
        list.Focus(FocusState.Keyboard);
    }

    /// <summary>Type the start of a name to jump to it (Finder and Explorer both do this).</summary>
    private void Items_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (sender is not ListViewBase list || e.Character < ' ') return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        var now = DateTime.Now;
        if ((now - _lastTypeAhead).TotalMilliseconds > 900) _typeAhead = "";
        if (e.Character == ' ' && _typeAhead.Length == 0) return;
        _typeAhead += e.Character;
        _lastTypeAhead = now;

        var match = list.Items.OfType<FileItemViewModel>()
            .FirstOrDefault(i => i.Name.StartsWith(_typeAhead, StringComparison.CurrentCultureIgnoreCase));
        if (match is null) return;
        list.SelectedItems.Clear();
        list.SelectedItem = match;
        list.ScrollIntoView(match);
        e.Handled = true;
    }

    // ----- click-to-rename ---------------------------------------------------------------------
    // Like Explorer and Finder: clicking the name of the item that is already selected (alone, in
    // the focused list) starts renaming it once the double-click time has passed without a
    // second click.

    private FileItemViewModel? _renamePressItem;
    private ListViewBase? _renamePressList;
    private (FileItemViewModel Item, ListViewBase List)? _renameCandidate;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _renameTimer;

    /// <summary>
    /// Runs before the list handles the press, so it still sees the selection and focus from
    /// before this click.
    /// </summary>
    private void ItemName_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _renamePressItem = null;
        _renamePressList = null;
        if (sender is not FrameworkElement { DataContext: FileItemViewModel item } element || item.IsDrive) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        if (WindowHelper.CurrentModifiers() != Core.Input.KeyModifiers.None) return;
        // A click that brings the window to the front only activates it.
        if ((DateTime.UtcNow - _host.LastActivatedUtc).TotalMilliseconds < 400) return;
        if (FindAncestor<ListViewBase>(element) is not { } list) return;
        if (list.SelectedItems.Count != 1 || !ReferenceEquals(list.SelectedItem, item)) return;
        // The list must already have focus: clicking back into a parent column (or into the
        // list from the sidebar or search box) just selects there.
        if (!HasFocusWithin(list)) return;
        _renamePressItem = item;
        _renamePressList = list;
    }

    private void ItemName_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var item = _renamePressItem;
        var list = _renamePressList;
        _renamePressItem = null;
        _renamePressList = null;
        if (item is null || list is null || !ReferenceEquals((sender as FrameworkElement)?.DataContext, item)) return;

        if (_renameTimer is null)
        {
            _renameTimer = DispatcherQueue.CreateTimer();
            _renameTimer.IsRepeating = false;
            _renameTimer.Tick += (_, _) => RenameCandidateNow();
        }
        _renameCandidate = (item, list);
        _renameTimer.Interval = TimeSpan.FromMilliseconds(WindowHelper.DoubleClickTimeMs + 50);
        _renameTimer.Start();
    }

    private void RenameCandidateNow()
    {
        if (_renameCandidate is not { } c) return;
        _renameCandidate = null;
        if (!IsLoaded || Visibility != Visibility.Visible || c.Item.IsRenaming) return;
        if (c.List.SelectedItems.Count != 1 || !ReferenceEquals(c.List.SelectedItem, c.Item)) return;
        Tab.BeginRename(c.Item);
    }

    private void CancelPendingRename()
    {
        _renameTimer?.Stop();
        _renameCandidate = null;
    }

    private bool HasFocusWithin(DependencyObject container)
    {
        if (XamlRoot is null) return false;
        for (var d = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = VisualTreeHelper.GetParent(start); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T match) return match;
        return null;
    }

    // ----- inline rename -----------------------------------------------------------------------

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItemViewModel item) return;
        box.Focus(FocusState.Programmatic);
        // Like Finder: select the name without its extension.
        int stem = item.IsFolder ? item.Name.Length : Path.GetFileNameWithoutExtension(item.Name).Length;
        box.Select(0, stem > 0 ? stem : item.Name.Length);
    }

    private async void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItemViewModel item) return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            item.RenameText = box.Text;
            await Tab.CommitRenameAsync(item);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Tab.CancelRename(item);
            Tab.RequestFocus();
        }
    }

    private async void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is FileItemViewModel { IsRenaming: true } item)
        {
            item.RenameText = box.Text;
            await Tab.CommitRenameAsync(item);
        }
    }

    // ----- drag and drop -----------------------------------------------------------------------

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileItemViewModel>().Where(i => !i.IsDrive).Select(i => i.Path).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        TransferService.StartDrag(e.Data, paths);
    }

    private FileItemViewModel? _dropHighlight;

    /// <summary>
    /// The row under the pointer. During a drag, <c>OriginalSource</c> is often the list itself
    /// rather than the row, so hit-test at the pointer position instead.
    /// </summary>
    private static FileItemViewModel? ItemUnderPointer(UIElement list, DragEventArgs e)
    {
        if (list.XamlRoot?.Content is not UIElement root) return null;
        var point = e.GetPosition(root);
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, list))
            if (element is FrameworkElement { DataContext: FileItemViewModel item }) return item;
        return null;
    }

    /// <summary>
    /// Where a drop at the pointer lands, and which row represents that folder (for highlighting):
    /// a folder row → into that folder; a file inside an expanded subfolder (list view) → that
    /// subfolder, i.e. the level being hovered; otherwise the column's or view's own folder.
    /// </summary>
    private (string? Folder, FileItemViewModel? Row) DropTarget(object sender, DragEventArgs e)
    {
        var item = sender is UIElement list ? ItemUnderPointer(list, e) : null;
        if (item is { IsFolder: true }) return (item.Path, item);
        if (item is { Depth: > 0 } && Tab.IsListView && Tab.Tree.ParentOf(item) is { } parentRow)
            return (parentRow.Path, parentRow);
        if (sender is FrameworkElement { DataContext: ColumnViewModel column }) return (column.Folder, null);
        return (Tab.Location.IsFolder ? Tab.Location.Path : null, null);
    }

    private void SetDropHighlight(FileItemViewModel? row)
    {
        if (ReferenceEquals(_dropHighlight, row)) return;
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = row;
        if (row is not null) row.IsDropTarget = true;
    }

    private async void List_DragOver(object sender, DragEventArgs e)
    {
        var (target, row) = DropTarget(sender, e);
        if (target is null)
        {
            SetDropHighlight(null);
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        await TransferService.HandleDragOverAsync(e, target);
        // Only highlight a folder that will actually accept the drop (not the dragged folder itself).
        SetDropHighlight(e.AcceptedOperation == DataPackageOperation.None ? null : row);
    }

    private void List_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    private async void List_Drop(object sender, DragEventArgs e)
    {
        var (target, row) = DropTarget(sender, e);
        SetDropHighlight(null);
        if (target is null) return;
        var created = await TransferService.HandleDropAsync(e, target, _host.Hwnd);
        if (created.Count == 0) return;
        Tab.RevealNewItems(created);
        // An expanded subfolder in list view is a snapshot; refresh it so the dropped items show.
        if (row is { IsExpanded: true })
        {
            Tab.Tree.Collapse(row);
            await Tab.Tree.ExpandAsync(row);
        }
    }

    // ----- search scope ------------------------------------------------------------------------

    private void SyncScopeSelector()
    {
        if (!Tab.IsSearch) return;
        _syncingScope = true;
        ScopeSelector.SelectedIndex = Tab.Location.SearchEverywhere || string.IsNullOrEmpty(Tab.Location.Path) ? 0 : 1;
        _syncingScope = false;
    }

    private void Scope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingScope || !Tab.IsSearch) return;
        bool everywhere = ScopeSelector.SelectedIndex == 0;
        if (everywhere != Tab.Location.SearchEverywhere) Tab.Search(Tab.Location.Query, everywhere);
    }

    // ----- helpers -------------------------------------------------------------------------------

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
