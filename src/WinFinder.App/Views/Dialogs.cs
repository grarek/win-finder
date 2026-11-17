using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Navigation;
using WinFinder.Core.Shell;

namespace WinFinder.App.Views;

public static class Dialogs
{
    /// <summary>Finder's "Go to Folder" (Ctrl+Shift+G) with folder autocomplete. Returns the typed path or null.</summary>
    public static async Task<string?> GoToFolderAsync(XamlRoot root, string? startFolder)
    {
        var box = new AutoSuggestBox
        {
            PlaceholderText = @"Type a path, e.g. C:\Users or %APPDATA% or ~\Documents",
            Text = startFolder is null ? "" : startFolder.TrimEnd('\\') + "\\",
            MinWidth = 460,
        };
        box.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            s.ItemsSource = Suggest(s.Text);
        };
        box.SuggestionChosen += (s, e) =>
        {
            if (e.SelectedItem is string chosen) s.Text = chosen.TrimEnd('\\') + "\\";
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Go to Folder",
            Content = box,
            PrimaryButtonText = "Go",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.QuerySubmitted += (_, _) => dialog.Hide();
        bool submitted = false;
        box.QuerySubmitted += (_, _) => submitted = true;
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !submitted) return null;
        string text = box.Text.Trim();
        return text.Length == 0 ? null : PathUtil.ExpandUserInput(text);
    }

    private static List<string> Suggest(string input)
    {
        try
        {
            string expanded = PathUtil.ExpandUserInput(input);
            string? dir = expanded.EndsWith('\\') ? expanded : Path.GetDirectoryName(expanded);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new();
            string prefix = expanded.EndsWith('\\') ? "" : Path.GetFileName(expanded);
            return Directory.EnumerateDirectories(dir, prefix + "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Take(12).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Finder's "Get Info" (Ctrl+I): details, folder size, tag editing, and a link to Windows Properties.</summary>
    public static async Task ShowGetInfoAsync(XamlRoot root, FileItemViewModel item, IntPtr hwnd)
    {
        var cts = new CancellationTokenSource();
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MinWidth = 380 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Image { Width = 48, Height = 48 };
        header.Children.Add(icon);
        var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerText.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        headerText.Children.Add(new TextBlock { Text = $"Modified {Format.Date(item.Entry.Modified, DateTime.Now)}", FontSize = 12, Foreground = Secondary() });
        header.Children.Add(headerText);
        _ = LoadIconAsync(icon, item);

        var sizeText = new TextBlock { Text = item.IsFolder ? "Calculating\u2026" : $"{Format.Size(item.Entry.Size)} ({item.Entry.Size:N0} bytes)", TextWrapping = TextWrapping.Wrap };
        int row = 0;
        void AddRow(string label, UIElement value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Secondary(), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(l, row);
            Grid.SetRow((FrameworkElement)value, row);
            Grid.SetColumn((FrameworkElement)value, 1);
            grid.Children.Add(l);
            grid.Children.Add(value);
            row++;
        }
        TextBlock Text(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        AddRow("Kind:", Text(item.KindText));
        AddRow("Size:", sizeText);
        AddRow("Where:", Text(Path.GetDirectoryName(item.Path) ?? item.Path));
        AddRow("Created:", Text(Format.Date(item.Entry.Created, DateTime.Now)));
        AddRow("Modified:", Text(Format.Date(item.Entry.Modified, DateTime.Now)));

        // Tags: one toggle per tag.
        var tagPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var current = AppServices.TagStore.GetTagIds(item.Path).ToHashSet();
        foreach (var tag in AppServices.TagStore.GetTags())
        {
            var toggle = new ToggleButton
            {
                IsChecked = current.Contains(tag.Id),
                Padding = new Thickness(6),
                MinWidth = 0,
                Content = new Ellipse { Width = 14, Height = 14, Fill = Palette.Brush(tag.Color) },
            };
            ToolTipService.SetToolTip(toggle, tag.Name);
            int id = tag.Id;
            toggle.Click += (_, _) => AppServices.Tags.Toggle([item.Path], id);
            tagPanel.Children.Add(toggle);
        }
        AddRow("Tags:", tagPanel);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(grid);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Info",
            Content = new ScrollViewer { Content = content },
            PrimaryButtonText = "Done",
            SecondaryButtonText = "Windows Properties\u2026",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (item.IsFolder)
        {
            _ = Task.Run(() => FolderSize(item.Path, cts.Token)).ContinueWith(t =>
            {
                if (t.IsCanceled || t.IsFaulted) return;
                var (bytes, files) = t.Result;
                AppServices.Dispatcher.TryEnqueue(() =>
                    sizeText.Text = $"{Format.Size(bytes)} on disk for {files:N0} items");
            });
        }

        var result = await dialog.ShowAsync();
        cts.Cancel();
        if (result == ContentDialogResult.Secondary) ShellInfo.ShowProperties(item.Path, hwnd);
    }

    private static async Task LoadIconAsync(Image target, FileItemViewModel item) =>
        target.Source = await AppServices.Icons.GetAsync(item.Entry, 48, ThumbnailMode.Thumbnail);

    private static (long Bytes, long Files) FolderSize(string path, CancellationToken ct)
    {
        long bytes = 0, files = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            try { bytes += f.Length; } catch { }
            files++;
        }
        return (bytes, files);
    }

    private static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = message, CloseButtonText = "OK" };
        await dialog.ShowAsync();
    }
}
