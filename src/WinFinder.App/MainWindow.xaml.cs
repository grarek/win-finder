using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinFinder.App.Interop;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;
using WinFinder.App.Views;
using WinFinder.Core.Input;
using WinFinder.Core.Navigation;
using WinFinder.Core.Settings;
using WinFinder.Core.Shell;

namespace WinFinder.App;

public sealed partial class MainWindow : Window, IBrowserHost, INotifyPropertyChanged
{
    private readonly Dictionary<BrowserTabViewModel, BrowserView> _views = new();
    private readonly List<FileItemViewModel> _cutItems = new();
    private DispatcherTimerLike? _searchDebounce;
    private DispatcherTimerLike? _tabHoverTimer;
    private BrowserTabViewModel? _hoverTab;
    private bool _syncingSearch;
    private BrowserTabViewModel? _observedTab;
    private DateTime _lastPathBarClick;

    public MainWindow(IEnumerable<BrowserTabViewModel>? tabs, WindowState? placement, int selectedIndex)
    {
        StartupTrace.Mark("MainWindow ctor");
        InitializeComponent();
        StartupTrace.Mark("MainWindow XAML loaded");
        Hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.InstallShellMenuHook(Hwnd);

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "WinFinder.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 420;
        }
        ApplyPlacement(placement);

        Sidebar.OwnerHwnd = Hwnd;
        Sidebar.NavigateRequested += OnSidebarNavigate;
        Sidebar.FilesDropped += () => ViewModel.SelectedTab?.InvalidateFreeSpace();
        QuickLook.OwnerHwnd = Hwnd;
        QuickLook.FullScreenToggled += ToggleQuickLookFullScreen;
        QuickLook.Closed += () =>
        {
            if (QuickLook.IsFullScreen) ToggleQuickLookFullScreen();
            ActiveView()?.Tab.RequestFocus();
        };

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        TransferService.CutChanged += OnCutChanged;
        UpdateService.Instance.UpdateAvailable += ShowUpdate;
        if (UpdateService.Instance.Available is { } pending) ShowUpdate(pending);

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                AppServices.FileOps.OwnerWindow = Hwnd;
        };
        Root.Loaded += (_, _) =>
        {
            StartupTrace.Mark("window content loaded (first layout)");
            // Mouse back/forward (X1/X2) buttons navigate, anywhere in the window — even over
            // lists and other controls that handle pointer presses themselves.
            Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMouseNavigationButton), handledEventsToo: true);
            UpdateScale();
            Root.XamlRoot.Changed += (_, _) => UpdateScale();
            UpdateTitleBarInsets();
        };
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) UpdateTitleBarInsets(); };
        AppWindow.Closing += (_, _) => WindowManager.OnWindowClosing(this);
        Closed += (_, _) =>
        {
            WindowManager.OnWindowClosed(this);
            TransferService.CutChanged -= OnCutChanged;
            UpdateService.Instance.UpdateAvailable -= ShowUpdate;
            foreach (var tab in ViewModel.Tabs.ToList()) tab.Dispose();
        };

        var list = tabs?.ToList() ?? new List<BrowserTabViewModel>();
        if (list.Count == 0)
        {
            list.Add(new BrowserTabViewModel(Location.Folder(KnownFolders.Home)));
            list[0].Start();
        }
        foreach (var tab in list) AttachTab(tab, -1, select: false);
        ViewModel.SelectedTab = ViewModel.Tabs[Math.Clamp(selectedIndex, 0, ViewModel.Tabs.Count - 1)];
    }

    public MainViewModel ViewModel { get; } = new();
    public ListLayout Layout => ListLayout.Instance;
    public IntPtr Hwnd { get; }

    public double SidebarWidth
    {
        get => AppServices.Settings.Current.SidebarWidth;
        set
        {
            if (Math.Abs(AppServices.Settings.Current.SidebarWidth - value) < 0.5) return;
            AppServices.Settings.Current.SidebarWidth = value;
            AppServices.Settings.SaveSoon();
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private BrowserTabViewModel? Tab => ViewModel.SelectedTab;
    private BrowserView? ActiveView() => Tab is { } t && _views.TryGetValue(t, out var v) ? v : null;

    private void UpdateScale() => AppServices.DisplayScale = Root.XamlRoot?.RasterizationScale ?? 1.0;

    private void UpdateTitleBarInsets()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        DragRegion.MinWidth = AppWindow.TitleBar.RightInset / scale + 48;
        // The minimize/maximize/close buttons are as tall as the system title bar (48 px with the
        // "Tall" option). Make our tab row at least that tall so their hover/press backgrounds
        // stay inside the title area instead of overhanging the toolbar.
        TitleRow.MinHeight = AppWindow.TitleBar.Height / scale;
    }

    // ----- window placement & session ------------------------------------------------------------

    private void ApplyPlacement(WindowState? placement)
    {
        if (placement is { Width: > 200, Height: > 200 } &&
            DisplayArea.GetFromRect(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
            if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
            return;
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1280, (int)(area.Width * 0.85)), h = Math.Min(800, (int)(area.Height * 0.85));
        int offset = WindowManager.Windows.Count * 32;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2 + offset, area.Y + (area.Height - h) / 2 + offset, w, h));
    }

    public WindowState CaptureState()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        return new WindowState
        {
            X = AppWindow.Position.X,
            Y = AppWindow.Position.Y,
            Width = AppWindow.Size.Width,
            Height = AppWindow.Size.Height,
            Maximized = maximized,
            SelectedTab = Tab is null ? 0 : ViewModel.Tabs.IndexOf(Tab),
            Tabs = ViewModel.Tabs.Select(WindowManager.CaptureTab).ToList(),
        };
    }

    // ----- tabs ----------------------------------------------------------------------------------

    private void AttachTab(BrowserTabViewModel tab, int index, bool select)
    {
        ViewModel.Insert(tab, index, select);
        tab.PropertyChanged += OnAnyTabPropertyChanged;
        WindowManager.SaveSessionSoon();
    }

    /// <summary>Removes a tab from this window without disposing it (used when moving tabs between windows).</summary>
    public void DetachTab(BrowserTabViewModel tab)
    {
        tab.PropertyChanged -= OnAnyTabPropertyChanged;
        if (_views.Remove(tab, out var view)) BrowserHost.Children.Remove(view);
        ViewModel.Detach(tab);
        WindowManager.SaveSessionSoon();
        if (ViewModel.Tabs.Count == 0) Close();
    }

    private void OnAnyTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTabViewModel.Location) or nameof(BrowserTabViewModel.ViewMode))
            WindowManager.SaveSessionSoon();
    }

    public BrowserTabViewModel NewTab(Location? location = null, ViewMode? view = null, bool select = true)
    {
        var current = Tab;
        // Like Finder: a new tab with no explicit target opens the home folder.
        location ??= Location.Folder(KnownFolders.Home);
        if (location.Kind == LocationKind.Search) location = Location.Folder(KnownFolders.Home);
        int index = current is null ? -1 : ViewModel.Tabs.IndexOf(current) + 1;
        var tab = new BrowserTabViewModel(location, view ?? current?.ViewMode);
        tab.Start();
        AttachTab(tab, index, select);
        return tab;
    }

    public void CloseTab(BrowserTabViewModel tab)
    {
        WindowManager.RememberClosedTab(tab);
        DetachTab(tab);
        tab.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedTab)) return;
        var tab = Tab;
        if (!ReferenceEquals(TabStrip.SelectedItem, tab)) TabStrip.SelectedItem = tab;
        ShowTab(tab);
    }

    private void ShowTab(BrowserTabViewModel? tab)
    {
        if (_observedTab is not null) _observedTab.PropertyChanged -= OnSelectedTabPropertyChanged;
        _observedTab = tab;
        if (tab is null) return;
        tab.PropertyChanged += OnSelectedTabPropertyChanged;

        if (!_views.TryGetValue(tab, out var view))
        {
            view = new BrowserView(tab, this);
            _views[tab] = view;
            BrowserHost.Children.Add(view);
        }
        foreach (var (t, v) in _views)
            v.Visibility = ReferenceEquals(t, tab) ? Visibility.Visible : Visibility.Collapsed;

        QuickLook.Close();
        SyncChromeToTab();
        DispatcherQueue.TryEnqueue(() => tab.RequestFocus());
    }

    private void OnSelectedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserTabViewModel.Location):
            case nameof(BrowserTabViewModel.Title):
                SyncChromeToTab();
                break;
            case nameof(BrowserTabViewModel.SelectedItems):
                if (QuickLook.IsOpen && Tab?.SelectedItems.FirstOrDefault() is { } item && !ReferenceEquals(item, QuickLook.Item))
                    _ = QuickLook.ShowAsync(item);
                break;
        }
    }

    private void SyncChromeToTab()
    {
        if (Tab is not { } tab) return;
        Title = $"{tab.Title} - WinFinder";
        Sidebar.Highlight(tab.Location);
        _syncingSearch = true;
        string wanted = tab.IsSearch ? tab.Location.Query : "";
        if (SearchBox.Text != wanted && FocusManager.GetFocusedElement(Root.XamlRoot) is not TextBox) SearchBox.Text = wanted;
        _syncingSearch = false;
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is BrowserTabViewModel tab && !ReferenceEquals(tab, ViewModel.SelectedTab))
            ViewModel.SelectedTab = tab;
    }

    private void TabStrip_AddTabButtonClick(TabView sender, object args) => NewTab();

    private void TabStrip_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is BrowserTabViewModel tab) CloseTab(tab);
    }

    private void TabStrip_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Item is not BrowserTabViewModel tab) return;
        args.Data.SetData(TransferService.TabFormat, tab.Id.ToString());
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TabStrip_TabStripDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(TransferService.TabFormat)) e.AcceptedOperation = DataPackageOperation.Move;
    }

    private async void TabStrip_TabStripDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(TransferService.TabFormat)) return;
        if (await e.DataView.GetDataAsync(TransferService.TabFormat) is not string id || !Guid.TryParse(id, out var guid)) return;
        if (WindowManager.FindTab(guid) is not { } found || ReferenceEquals(found.Window, this)) return;

        // Insert where it was dropped.
        int index = ViewModel.Tabs.Count;
        var pos = e.GetPosition(TabStrip);
        for (int i = 0; i < ViewModel.Tabs.Count; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is FrameworkElement item)
            {
                var origin = item.TransformToVisual(TabStrip).TransformPoint(new Point(0, 0));
                if (pos.X < origin.X + item.ActualWidth / 2) { index = i; break; }
            }
        }
        found.Window.DetachTab(found.Tab);
        AttachTab(found.Tab, index, select: true);
        Activate();
    }

    private void TabStrip_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Item is not BrowserTabViewModel tab || ViewModel.Tabs.Count <= 1) return;
        DetachTab(tab);
        var (x, y) = WindowHelper.CursorPosition();
        var size = AppWindow.Size;
        WindowManager.CreateWindow([tab], new WindowState { X = x - 120, Y = y - 20, Width = size.Width, Height = size.Height });
    }

    // Dragging files over a tab header: hover to switch, drop to move/copy into that tab's folder.
    private async void TabItem_DragOver(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTabViewModel tab) return;
        if (!TransferService.HasFiles(e.DataView)) return;
        e.Handled = true;

        if (!ReferenceEquals(_hoverTab, tab))
        {
            _hoverTab = tab;
            _tabHoverTimer ??= new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromSeconds(1), () =>
            {
                if (_hoverTab is not null) ViewModel.SelectedTab = _hoverTab;
            });
            _tabHoverTimer.Restart();
        }

        if (tab.CurrentFolder is { } folder) await TransferService.HandleDragOverAsync(e, folder);
        else e.AcceptedOperation = DataPackageOperation.None;
    }

    private void TabItem_DragLeave(object sender, DragEventArgs e)
    {
        _hoverTab = null;
        _tabHoverTimer?.Stop();
    }

    private async void TabItem_Drop(object sender, DragEventArgs e)
    {
        _hoverTab = null;
        _tabHoverTimer?.Stop();
        if ((sender as FrameworkElement)?.DataContext is not BrowserTabViewModel tab || tab.CurrentFolder is not { } folder) return;
        if (!TransferService.HasFiles(e.DataView)) return;
        e.Handled = true;
        var created = await TransferService.HandleDropAsync(e, folder, Hwnd);
        if (created.Count > 0) tab.RevealNewItems(created);
    }

    // ----- sidebar, toolbar, path bar --------------------------------------------------------------

    private void OnSidebarNavigate(Location location, bool newTab)
    {
        if (newTab) NewTab(location, select: false);
        else Tab?.Navigate(location);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Tab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => Tab?.GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => Tab?.GoUp();

    private void PathBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is not ViewModels.PathSegment seg || Tab is not { } tab) return;
        // Navigating re-lays out the path bar, so the second click of a double-click would land on
        // a different segment. Treat clicks within the system double-click time as one.
        var now = DateTime.UtcNow;
        if ((now - _lastPathBarClick).TotalMilliseconds < WindowHelper.DoubleClickTimeMs) return;
        _lastPathBarClick = now;
        Location? target = seg.Kind switch
        {
            SegmentKind.Computer => Location.Computer,
            SegmentKind.Folder => tab.PathBarLocation(seg.Path),
            _ => null,
        };
        if (target is null) return;
        if (WindowHelper.IsCtrlDown()) NewTab(target, select: false);
        else tab.Navigate(target);
    }

    private void IconSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        ActiveView()?.RefreshThumbnails();

    private void TagsMenu_Opening(object sender, object e)
    {
        TagsMenu.Items.Clear();
        var paths = Tab?.SelectedItems.Where(i => !i.IsDrive).Select(i => i.Path).ToList() ?? new();
        if (paths.Count == 0)
        {
            TagsMenu.Items.Add(new MenuFlyoutItem { Text = "Select items to tag", IsEnabled = false });
            return;
        }
        var have = paths.Select(p => AppServices.TagStore.GetTagIds(p).ToHashSet()).ToList();
        int index = 1;
        foreach (var tag in AppServices.TagStore.GetTags())
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = tag.Name,
                IsChecked = have.All(h => h.Contains(tag.Id)),
                Icon = new FontIcon { FontFamily = new FontFamily("Segoe UI"), Glyph = "\u25CF", Foreground = Palette.Brush(tag.Color) },
            };
            if (index <= 7)
                item.KeyboardAcceleratorTextOverride = $"Ctrl+Shift+{index}";
            int id = tag.Id;
            item.Click += (_, _) => AppServices.Tags.Toggle(paths, id);
            TagsMenu.Items.Add(item);
            index++;
        }
        TagsMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Remove All Tags", KeyboardAcceleratorTextOverride = "Ctrl+Shift+0" };
        clear.Click += (_, _) => AppServices.Tags.Clear(paths);
        TagsMenu.Items.Add(clear);
    }

    private void ActionsMenu_Opening(object sender, object e)
    {
        ActionsMenu.Items.Clear();
        MenuFlyoutItem Item(string text, string glyph, string? accel, Action action, bool enabled = true)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (accel is not null) i.KeyboardAcceleratorTextOverride = accel;
            i.Click += (_, _) => action();
            return i;
        }

        bool hasFolder = Tab?.CurrentFolder is not null;
        ActionsMenu.Items.Add(Item("New Folder", "\uE8F4", "Ctrl+Shift+N", () => Execute(FinderCommand.NewFolder), hasFolder));
        ActionsMenu.Items.Add(Item("New Tab", "\uE8A7", "Ctrl+T", () => Execute(FinderCommand.NewTab)));
        ActionsMenu.Items.Add(Item("New Window", "\uE737", "Ctrl+N", () => Execute(FinderCommand.NewWindow)));
        ActionsMenu.Items.Add(new MenuFlyoutSeparator());
        ActionsMenu.Items.Add(Item("Go to Folder\u2026", "\uE8DA", "Ctrl+Shift+G", () => Execute(FinderCommand.GoToFolder)));
        ActionsMenu.Items.Add(Item("Get Info", "\uE946", "Ctrl+I", () => Execute(FinderCommand.GetInfo)));
        if (Tab?.CurrentFolder is { } current)
        {
            ActionsMenu.Items.Add(SidebarViewModel.Instance.IsFavorite(current)
                ? Item("Remove from Favorites", "\uE77A", null, () => SidebarViewModel.Instance.RemoveFavorite(current))
                : Item("Add to Favorites", "\uE718", "Ctrl+D", () => SidebarViewModel.Instance.AddFavorite(current)));
        }
        ActionsMenu.Items.Add(new MenuFlyoutSeparator());

        var hidden = new ToggleMenuFlyoutItem { Text = "Show Hidden Files", IsChecked = AppServices.ShowHidden, KeyboardAcceleratorTextOverride = "Ctrl+Shift+." };
        hidden.Click += (_, _) => AppServices.ShowHidden = hidden.IsChecked;
        ActionsMenu.Items.Add(hidden);
        var foldersFirst = new ToggleMenuFlyoutItem { Text = "Keep Folders on Top", IsChecked = AppServices.FoldersFirst };
        foldersFirst.Click += (_, _) => AppServices.FoldersFirst = foldersFirst.IsChecked;
        ActionsMenu.Items.Add(foldersFirst);

        ActionsMenu.Items.Add(new MenuFlyoutSeparator());
        var undo = AppServices.FileOps.Undo.Peek();
        ActionsMenu.Items.Add(Item(undo?.Description ?? "Undo", "\uE7A7", "Ctrl+Z", () => Execute(FinderCommand.Undo), undo is not null));
        ActionsMenu.Items.Add(new MenuFlyoutSeparator());
        ActionsMenu.Items.Add(Item("Check for Updates\u2026", "\uE895", null, () => _ = CheckForUpdatesAsync()));
        var autoUpdate = new ToggleMenuFlyoutItem { Text = "Check for Updates Automatically", IsChecked = AppServices.Settings.Current.AutoCheckForUpdates };
        autoUpdate.Click += (_, _) =>
        {
            AppServices.Settings.Current.AutoCheckForUpdates = autoUpdate.IsChecked;
            AppServices.Settings.SaveSoon();
        };
        ActionsMenu.Items.Add(autoUpdate);
        ActionsMenu.Items.Add(new MenuFlyoutItem { Text = $"WinFinder {UpdateService.Instance.CurrentVersionText}", IsEnabled = false });
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncingSearch || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDebounce ??= new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromMilliseconds(350), () => Tab?.Search(SearchBox.Text));
        _searchDebounce.Restart();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce?.Stop();
        Tab?.Search(sender.Text);
    }

    // ----- updates --------------------------------------------------------------------------------

    private Core.Updates.ReleaseInfo? _offeredUpdate;

    private void ShowUpdate(Core.Updates.ReleaseInfo release)
    {
        _offeredUpdate = release;
        UpdateBar.Title = "Update available";
        UpdateBar.Message = $"WinFinder {release.Version.ToString(3)} is available (you have {UpdateService.Instance.CurrentVersionText}).";
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateNowButton.IsEnabled = true;
        UpdateNowButton.Content = UpdateService.Instance.InPlaceUpdateBlocker(release) is null ? "Update and restart" : "Download";
        UpdateProgress.Visibility = Visibility.Collapsed;
        SkipVersionLink.Visibility = Visibility.Visible;
        UpdateBar.IsOpen = true;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is not { } release) return;
        if (UpdateService.Instance.InPlaceUpdateBlocker(release) is { } reason)
        {
            // Can't replace this copy in place: send the user to the release page instead.
            ShellInfo.Open(release.HtmlUrl, Hwnd);
            UpdateBar.Message = reason + " The release page has been opened so you can download it.";
            return;
        }

        UpdateNowButton.IsEnabled = false;
        SkipVersionLink.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateBar.IsClosable = false;
        UpdateBar.Message = $"Downloading WinFinder {release.Version.ToString(3)}…";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress.Value = p);
            await UpdateService.Instance.PrepareAndLaunchUpdaterAsync(release, progress);
            UpdateBar.Message = "Restarting to finish the update…";
            WindowManager.ExitForUpdate();
        }
        catch (Exception ex)
        {
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Title = "Update failed";
            UpdateBar.Message = ex.Message;
            UpdateBar.IsClosable = true;
            UpdateNowButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is { HtmlUrl.Length: > 0 } r) ShellInfo.Open(r.HtmlUrl, Hwnd);
    }

    private void SkipVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is { } r) UpdateService.Instance.Skip(r);
        UpdateBar.IsOpen = false;
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var latest = await UpdateService.Instance.CheckAsync(manual: true);
            if (latest is not null && Core.Updates.UpdateChecker.IsNewer(latest.Version, UpdateService.Instance.CurrentVersion))
                return; // the update bar is showing
            string message = latest is null
                ? $"No releases have been published yet. You have version {UpdateService.Instance.CurrentVersionText}."
                : $"You're up to date. WinFinder {UpdateService.Instance.CurrentVersionText} is the latest version.";
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Check for Updates", message);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Check for Updates", $"Couldn't reach GitHub: {ex.Message}");
        }
    }

    // ----- mouse back/forward buttons -------------------------------------------------------------

    private void OnMouseNavigationButton(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Root).Properties;
        if (!props.IsXButton1Pressed && !props.IsXButton2Pressed) return;
        if (Tab is not { } tab) return;
        if (QuickLook.IsOpen) QuickLook.Close();
        if (props.IsXButton1Pressed) tab.GoBack();
        else tab.GoForward();
        e.Handled = true;
    }

    // ----- keyboard ------------------------------------------------------------------------------

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var tab = Tab;
        if (tab is null) return;

        if (QuickLook.IsOpen)
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.Escape:
                    QuickLook.Close();
                    e.Handled = true;
                    return;
                case VirtualKey.Up:
                case VirtualKey.Left:
                    tab.RequestMoveSelection(-1);
                    e.Handled = true;
                    return;
                case VirtualKey.Down:
                case VirtualKey.Right:
                    tab.RequestMoveSelection(1);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == VirtualKey.Escape && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox && SearchBox.Text.Length > 0
            && IsInSearchBox())
        {
            SearchBox.Text = "";
            tab.Search("");
            tab.RequestFocus();
            e.Handled = true;
            return;
        }

        if ((e.Key == VirtualKey.Application || (e.Key == VirtualKey.F10 && WindowHelper.IsShiftDown()))
            && FocusManager.GetFocusedElement(Root.XamlRoot) is not TextBox)
        {
            ActiveView()?.ShowContextMenuFromKeyboard();
            e.Handled = true;
            return;
        }

        var command = KeyMap.Resolve((int)e.Key, WindowHelper.CurrentModifiers());
        if (command is null) return;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox && !KeyMap.AppliesWhileEditingText(command.Value)) return;
        e.Handled = Execute(command.Value);
    }

    private bool IsInSearchBox()
    {
        for (var d = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, SearchBox)) return true;
        return false;
    }

    /// <summary>Runs a keyboard/menu command. Returns true when handled.</summary>
    public bool Execute(FinderCommand command)
    {
        var tab = Tab;
        if (tab is null) return false;
        var selected = tab.SelectedItems;
        var paths = selected.Where(i => !i.IsDrive).Select(i => i.Path).ToList();
        AppServices.FileOps.OwnerWindow = Hwnd;

        switch (command)
        {
            case FinderCommand.Open:
                if (selected.Count == 0) return false;
                OpenItems(tab, selected, newTab: false);
                return true;
            case FinderCommand.Rename:
                if (selected.Count != 1) return false;
                tab.BeginRename(selected[0]);
                return true;
            case FinderCommand.Recycle:
            case FinderCommand.DeletePermanently:
                if (paths.Count == 0) return false;
                _ = tab.RecycleAsync(selected, command == FinderCommand.DeletePermanently);
                return true;
            case FinderCommand.QuickLook:
                if (selected.Count == 0) return false;
                _ = QuickLook.ShowAsync(selected[0]);
                return true;

            case FinderCommand.ViewIcons: tab.SetViewMode(ViewMode.Icons); return true;
            case FinderCommand.ViewList: tab.SetViewMode(ViewMode.List); return true;
            case FinderCommand.ViewColumns: tab.SetViewMode(ViewMode.Columns); return true;

            case FinderCommand.Back: tab.GoBack(); return true;
            case FinderCommand.Forward: tab.GoForward(); return true;
            case FinderCommand.Parent: tab.GoUp(); return true;

            case FinderCommand.GoToFolder:
                _ = GoToFolderAsync();
                return true;
            case FinderCommand.GetInfo:
                _ = GetInfoAsync();
                return true;
            case FinderCommand.ToggleHidden:
                AppServices.ShowHidden = !AppServices.ShowHidden;
                return true;
            case FinderCommand.NewFolder:
                _ = tab.NewFolderAsync();
                return true;
            case FinderCommand.AddToFavorites:
            {
                // Selected folders, or the current folder when no folder is selected.
                var targets = selected.Where(i => i.IsFolder).Select(i => i.Path).ToList();
                if (targets.Count == 0 && tab.CurrentFolder is { } here) targets.Add(here);
                if (targets.Count == 0) return false;
                SidebarViewModel.Instance.AddFavorites(targets);
                return true;
            }

            case FinderCommand.ClearTags:
                if (paths.Count == 0) return false;
                AppServices.Tags.Clear(paths);
                return true;
            case >= FinderCommand.Tag1 and <= FinderCommand.Tag7:
                if (paths.Count == 0) return false;
                AppServices.Tags.ToggleByIndex(paths, command - FinderCommand.Tag1 + 1);
                return true;

            case FinderCommand.NewTab: NewTab(); return true;
            case FinderCommand.CloseTab: CloseTab(tab); return true;
            case FinderCommand.NewWindow:
                WindowManager.CreateWindow(Location.Folder(KnownFolders.Home), tab.ViewMode); // new windows start at home
                return true;
            case FinderCommand.NextTab:
            case FinderCommand.PreviousTab:
            {
                int count = ViewModel.Tabs.Count;
                int i = ViewModel.Tabs.IndexOf(tab) + (command == FinderCommand.NextTab ? 1 : -1);
                ViewModel.SelectedTab = ViewModel.Tabs[(i + count) % count];
                return true;
            }
            case >= FinderCommand.JumpTab1 and <= FinderCommand.JumpTab9:
            {
                int n = command - FinderCommand.JumpTab1;
                int index = command == FinderCommand.JumpTab9 ? ViewModel.Tabs.Count - 1 : n; // 9 = last tab, like browsers
                if (index < ViewModel.Tabs.Count) ViewModel.SelectedTab = ViewModel.Tabs[index];
                return true;
            }
            case FinderCommand.ReopenClosedTab:
                if (WindowManager.PopClosedTab() is { } closed)
                    NewTab(WindowManager.RestoreLocation(closed), closed.View);
                return true;

            case FinderCommand.Undo:
                _ = UndoAsync();
                return true;
            case FinderCommand.Copy:
            case FinderCommand.Cut:
                if (paths.Count == 0) return false;
                _ = TransferService.CopyToClipboardAsync(paths, command == FinderCommand.Cut);
                return true;
            case FinderCommand.Paste:
                _ = PasteAsync(tab);
                return true;
            case FinderCommand.SelectAll:
                ActiveView()?.SelectAll();
                return true;
            case FinderCommand.Refresh:
                tab.Refresh();
                return true;
            case FinderCommand.Find:
                SearchBox.Focus(FocusState.Keyboard);
                return true;
        }
        return false;
    }

    private async Task UndoAsync()
    {
        var undone = await AppServices.FileOps.UndoLastAsync();
        if (undone is not null) Tab?.InvalidateFreeSpace();
    }

    private async Task PasteAsync(BrowserTabViewModel tab)
    {
        if (tab.CurrentFolder is not { } folder) return;
        var created = await TransferService.PasteAsync(folder, Hwnd);
        if (created.Count > 0) tab.RevealNewItems(created);
    }

    private async Task GoToFolderAsync()
    {
        var target = await Dialogs.GoToFolderAsync(Root.XamlRoot, Tab?.CurrentFolder);
        if (target is null || Tab is not { } tab) return;
        if (Directory.Exists(target)) tab.Navigate(Location.Folder(target));
        else if (File.Exists(target) && PathUtil.Parent(target) is { } parent) tab.Navigate(Location.Folder(parent), target);
        else await Dialogs.ShowMessageAsync(Root.XamlRoot, "Folder not found", $"\u201C{target}\u201D doesn't exist.");
    }

    private async Task GetInfoAsync()
    {
        if (Tab is not { } tab) return;
        var item = tab.SelectedItems.FirstOrDefault();
        if (item is null && tab.CurrentFolder is { } folder && Core.FileSystem.FileEntry.TryFromPath(folder) is { } entry)
            item = new FileItemViewModel(entry);
        if (item is null) return;
        await Dialogs.ShowGetInfoAsync(Root.XamlRoot, item, Hwnd);
    }

    private void OnCutChanged(IReadOnlyList<string> paths)
    {
        foreach (var i in _cutItems) i.IsCut = false;
        _cutItems.Clear();
        if (paths.Count == 0) return;
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in ViewModel.Tabs)
            foreach (var item in tab.ActiveContents?.Items ?? [])
                if (set.Contains(item.Path)) { item.IsCut = true; _cutItems.Add(item); }
    }

    // ----- IBrowserHost --------------------------------------------------------------------------

    public void OpenItems(BrowserTabViewModel tab, IReadOnlyList<FileItemViewModel> items, bool newTab)
    {
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            var item = items[0];
            if (item.IsFolder && newTab) OpenInNewTab(item.Path);
            else tab.OpenItem(item, Hwnd);
            return;
        }
        // Several items: folders open in new tabs (like Finder), files with their apps.
        foreach (var item in items.Take(20))
        {
            if (item.IsFolder) NewTab(Location.Folder(item.Path), select: false);
            else ShellInfo.Open(item.Path, Hwnd);
        }
    }

    public void OpenInNewTab(string folder) => NewTab(Location.Folder(folder), select: false);

    public void ShowContextMenu(BrowserTabViewModel tab, IReadOnlyList<FileItemViewModel>? items, Point position)
    {
        var (x, y) = WindowHelper.ToScreen(Hwnd, position, Root.XamlRoot.RasterizationScale);
        bool extended = WindowHelper.IsShiftDown();
        AppServices.FileOps.OwnerWindow = Hwnd;

        if (items is { Count: > 0 })
        {
            var real = items.Where(i => !i.IsDrive).ToList();
            var paths = items.Select(i => i.Path).ToList();
            var tags = AppServices.TagStore.GetTags();
            var have = real.Select(i => AppServices.TagStore.GetTagIds(i.Path).ToHashSet()).ToList();

            var extras = new List<MenuExtra>();
            if (items.All(i => i.IsFolder)) extras.Add(new MenuExtra(1, items.Count == 1 ? "Open in New Tab" : "Open in New Tabs"));
            extras.Add(new MenuExtra(2, "Quick Look"));
            extras.Add(new MenuExtra(3, "Get Info"));
            if (real.Count > 0)
            {
                var tagItems = tags.Select(t => new MenuExtra(1000 + t.Id, t.Name, Checked: have.All(h => h.Contains(t.Id)))).ToList();
                tagItems.Add(MenuExtra.Separator);
                tagItems.Add(new MenuExtra(999, "Remove All Tags"));
                extras.Add(new MenuExtra(0, "Tags", tagItems));
            }
            var folders = items.Where(i => i.IsFolder).Select(i => i.Path).ToList();
            if (folders.Count > 0)
            {
                bool allPinned = folders.All(SidebarViewModel.Instance.IsFavorite);
                extras.Add(allPinned ? new MenuExtra(6, "Remove from Favorites") : new MenuExtra(4, "Add to Favorites"));
            }
            if (tab.ShowWhereColumn) extras.Add(new MenuExtra(5, "Show in Enclosing Folder"));

            var result = ShellContextMenu.ShowForItems(Hwnd, paths, x, y, extras, verb => InterceptVerb(verb, tab, items), extended);
            switch (result.ExtraId)
            {
                case 1: OpenItems(tab, items, newTab: true); break;
                case 2: _ = QuickLook.ShowAsync(items[0]); break;
                case 3: _ = Dialogs.ShowGetInfoAsync(Root.XamlRoot, items[0], Hwnd); break;
                case 4: SidebarViewModel.Instance.AddFavorites(items.Where(i => i.IsFolder).Select(i => i.Path)); break;
                case 6:
                    foreach (var f in items.Where(i => i.IsFolder)) SidebarViewModel.Instance.RemoveFavorite(f.Path);
                    break;
                case 5:
                    if (PathUtil.Parent(items[0].Path) is { } parent) tab.Navigate(Location.Folder(parent), items[0].Path);
                    break;
                case 999: AppServices.Tags.Clear(real.Select(i => i.Path).ToList()); break;
                case int id when id > 1000: AppServices.Tags.Toggle(real.Select(i => i.Path).ToList(), id - 1000); break;
            }
            return;
        }

        if (tab.CurrentFolder is not { } folder) return;
        var bg = new List<MenuExtra>
        {
            new(10, "New Folder"),
            new(11, "Get Info"),
            new(0, "View", [
                new MenuExtra(20, "as Icons", Checked: tab.ViewMode == ViewMode.Icons),
                new MenuExtra(21, "as List", Checked: tab.ViewMode == ViewMode.List),
                new MenuExtra(22, "as Columns", Checked: tab.ViewMode == ViewMode.Columns),
            ]),
            new(0, "Sort By", [
                new MenuExtra(30, "Name", Checked: tab.SortKey == Core.FileSystem.SortKey.Name),
                new MenuExtra(31, "Date Modified", Checked: tab.SortKey == Core.FileSystem.SortKey.Modified),
                new MenuExtra(32, "Size", Checked: tab.SortKey == Core.FileSystem.SortKey.Size),
                new MenuExtra(33, "Kind", Checked: tab.SortKey == Core.FileSystem.SortKey.Kind),
            ]),
            new(12, "Show Hidden Files", Checked: AppServices.ShowHidden),
            SidebarViewModel.Instance.IsFavorite(folder)
                ? new MenuExtra(14, "Remove This Folder from Favorites")
                : new MenuExtra(13, "Add This Folder to Favorites"),
        };
        var bgResult = ShellContextMenu.ShowForFolderBackground(Hwnd, folder, x, y, bg, verb => InterceptVerb(verb, tab, null), extended);
        switch (bgResult.ExtraId)
        {
            case 10: _ = tab.NewFolderAsync(); break;
            case 11: _ = GetInfoAsync(); break;
            case 12: AppServices.ShowHidden = !AppServices.ShowHidden; break;
            case 13: SidebarViewModel.Instance.AddFavorite(folder); break;
            case 14: SidebarViewModel.Instance.RemoveFavorite(folder); break;
            case 20: tab.SetViewMode(ViewMode.Icons); break;
            case 21: tab.SetViewMode(ViewMode.List); break;
            case 22: tab.SetViewMode(ViewMode.Columns); break;
            case 30: tab.SetSort(Core.FileSystem.SortKey.Name); break;
            case 31: tab.SetSort(Core.FileSystem.SortKey.Modified); break;
            case 32: tab.SetSort(Core.FileSystem.SortKey.Size); break;
            case 33: tab.SetSort(Core.FileSystem.SortKey.Kind); break;
        }
    }

    /// <summary>Shell verbs we handle ourselves so they stay inside the app (and get undo records).</summary>
    private bool InterceptVerb(string verb, BrowserTabViewModel tab, IReadOnlyList<FileItemViewModel>? items)
    {
        verb = verb.ToLowerInvariant();
        bool allFolders = items is { Count: > 0 } && items.All(i => i.IsFolder);
        switch (verb)
        {
            case "rename" when items is { Count: 1 }:
                tab.BeginRename(items[0]);
                return true;
            case "delete" when items is { Count: > 0 }:
                _ = tab.RecycleAsync(items, WindowHelper.IsShiftDown());
                return true;
            case "open" or "explore" when allFolders:
                OpenItems(tab, items!, newTab: false);
                return true;
            case "opennewtab" when allFolders:
                foreach (var i in items!) OpenInNewTab(i.Path);
                return true;
            case "opennewwindow" or "opennewprocess" when allFolders:
                foreach (var i in items!) WindowManager.CreateWindow(Location.Folder(i.Path), tab.ViewMode);
                return true;
            case "copy" or "cut" when items is { Count: > 0 }:
                _ = TransferService.CopyToClipboardAsync(items.Select(i => i.Path).ToList(), verb == "cut");
                return true;
            case "paste":
                _ = PasteAsync(tab);
                return true;
            case "newfolder":
                _ = tab.NewFolderAsync();
                return true;
        }
        return false;
    }

    // ----- Quick Look full screen ------------------------------------------------------------------

    private void ToggleQuickLookFullScreen()
    {
        bool goFull = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(goFull ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        QuickLook.SetFullScreen(goFull);
    }
}

/// <summary>Small restartable one-shot timer on the UI dispatcher.</summary>
internal sealed class DispatcherTimerLike
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherTimerLike(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action action)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => action();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
