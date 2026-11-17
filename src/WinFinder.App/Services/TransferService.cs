using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinFinder.App.Interop;
using WinFinder.Core.Navigation;
using WinFinder.Core.Shell;

namespace WinFinder.App.Services;

/// <summary>Clipboard and drag-and-drop, interoperable with Explorer (StorageItems = CF_HDROP).</summary>
public static class TransferService
{
    /// <summary>Fast in-process format so drags inside the app don't need StorageFile lookups.</summary>
    public const string PathsFormat = "WinFinder.Paths";
    public const string TabFormat = "WinFinder.Tab";

    private static IReadOnlyList<string> _cutPaths = Array.Empty<string>();

    public static event Action<IReadOnlyList<string>>? CutChanged;

    public static IReadOnlyList<string> CutPaths => _cutPaths;

    public static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IEnumerable<string> paths)
    {
        var list = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) list.Add(await StorageFolder.GetFolderFromPathAsync(p));
                else if (File.Exists(p)) list.Add(await StorageFile.GetFileFromPathAsync(p));
            }
            catch { }
        }
        return list;
    }

    // ----- clipboard ----------------------------------------------------------------------

    public static async Task CopyToClipboardAsync(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        var package = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        package.SetStorageItems(await ToStorageItemsAsync(paths), readOnly: !cut);
        package.SetText(string.Join(Environment.NewLine, paths));
        package.SetData(PathsFormat, string.Join("\n", paths));
        Clipboard.SetContent(package);
        try { Clipboard.Flush(); } catch { }
        _cutPaths = cut ? paths.ToList() : Array.Empty<string>();
        CutChanged?.Invoke(_cutPaths);
    }

    public static bool CanPaste()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.StorageItems);
        }
        catch { return false; }
    }

    /// <summary>Pastes clipboard files into <paramref name="folder"/>. Returns the created paths.</summary>
    public static async Task<IReadOnlyList<string>> PasteAsync(string folder, IntPtr owner)
    {
        DataPackageView content;
        try { content = Clipboard.GetContent(); }
        catch { return Array.Empty<string>(); }
        if (!content.Contains(StandardDataFormats.StorageItems)) return Array.Empty<string>();

        var paths = await GetPathsAsync(content);
        if (paths.Count == 0) return Array.Empty<string>();

        bool move = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
        AppServices.FileOps.OwnerWindow = owner;
        var result = move
            ? await AppServices.FileOps.MoveAsync(paths, folder)
            : await AppServices.FileOps.CopyAsync(paths, folder);

        if (move && !result.Aborted)
        {
            Clipboard.Clear();
            _cutPaths = Array.Empty<string>();
            CutChanged?.Invoke(_cutPaths);
        }
        return result.ResultPaths;
    }

    // ----- drag and drop ------------------------------------------------------------------

    public static async Task<IReadOnlyList<string>> GetPathsAsync(DataPackageView view)
    {
        try
        {
            if (view.Contains(PathsFormat) && await view.GetDataAsync(PathsFormat) is string s)
                return s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                return items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    public static bool HasFiles(DataPackageView view) =>
        view.Contains(PathsFormat) || view.Contains(StandardDataFormats.StorageItems);

    /// <summary>Starts a drag of files out of the app (to Explorer, the desktop, other apps, or ourselves).</summary>
    public static void StartDrag(DataPackage data, IReadOnlyList<string> paths)
    {
        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
        data.SetData(PathsFormat, string.Join("\n", paths));
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await ToStorageItemsAsync(paths)); }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>Explorer's rule: same drive moves, different drive copies; Ctrl forces copy, Shift forces move.</summary>
    public static DataPackageOperation DecideOperation(IReadOnlyList<string> sources, string destination, DataPackageOperation allowed)
    {
        DataPackageOperation op;
        if (WindowHelper.IsCtrlDown()) op = DataPackageOperation.Copy;
        else if (WindowHelper.IsShiftDown()) op = DataPackageOperation.Move;
        else
        {
            string? destRoot = Path.GetPathRoot(destination);
            bool sameDrive = sources.Count > 0 && sources.All(s => string.Equals(Path.GetPathRoot(s), destRoot, StringComparison.OrdinalIgnoreCase));
            op = sameDrive ? DataPackageOperation.Move : DataPackageOperation.Copy;
        }
        if (!allowed.HasFlag(op)) op = allowed.HasFlag(DataPackageOperation.Copy) ? DataPackageOperation.Copy : allowed;
        return op;
    }

    /// <summary>True when the drop would be a no-op or invalid (into itself, or into its current folder for a move).</summary>
    public static bool IsInvalidTarget(IReadOnlyList<string> sources, string destination, DataPackageOperation op)
    {
        if (sources.Count == 0) return true;
        if (sources.Any(s => PathUtil.IsSameOrUnder(destination, s))) return true;
        if (op == DataPackageOperation.Move &&
            sources.All(s => string.Equals(PathUtil.Parent(s), PathUtil.Normalize(destination), StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    // DragOver fires continuously; cache the source paths for the current drag.
    private static DataPackageView? _lastView;
    private static IReadOnlyList<string> _lastPaths = Array.Empty<string>();

    public static async Task<IReadOnlyList<string>> GetDragPathsCachedAsync(DataPackageView view)
    {
        if (ReferenceEquals(view, _lastView)) return _lastPaths;
        var paths = await GetPathsAsync(view);
        _lastView = view;
        _lastPaths = paths;
        return paths;
    }

    /// <summary>Shared DragOver logic for any drop target folder.</summary>
    public static async Task HandleDragOverAsync(DragEventArgs e, string destination)
    {
        if (!HasFiles(e.DataView)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var deferral = e.GetDeferral();
        try
        {
            var paths = await GetDragPathsCachedAsync(e.DataView);
            var op = DecideOperation(paths, destination, e.DataView.RequestedOperation);
            if (IsInvalidTarget(paths, destination, op)) op = DataPackageOperation.None;
            e.AcceptedOperation = op;
            string name = Path.GetFileName(destination.TrimEnd('\\'));
            if (name.Length == 0) name = destination;
            e.DragUIOverride.Caption = op switch
            {
                DataPackageOperation.Copy => $"Copy to {name}",
                DataPackageOperation.Move => $"Move to {name}",
                _ => "",
            };
            e.DragUIOverride.IsCaptionVisible = op != DataPackageOperation.None;
        }
        finally { deferral.Complete(); }
    }

    /// <summary>Shared Drop logic. Returns the paths created in <paramref name="destination"/>.</summary>
    public static async Task<IReadOnlyList<string>> HandleDropAsync(DragEventArgs e, string destination, IntPtr owner)
    {
        IReadOnlyList<string> paths;
        DataPackageOperation op;
        var deferral = e.GetDeferral();
        try
        {
            paths = await GetPathsAsync(e.DataView);
            op = DecideOperation(paths, destination, e.DataView.RequestedOperation);
            if (IsInvalidTarget(paths, destination, op)) return Array.Empty<string>();
            e.AcceptedOperation = op;
        }
        finally
        {
            // Release the drag source (it may be Explorer) before the copy starts.
            deferral.Complete();
            _lastView = null;
        }

        AppServices.FileOps.OwnerWindow = owner;
        var result = op == DataPackageOperation.Move
            ? await AppServices.FileOps.MoveAsync(paths, destination)
            : await AppServices.FileOps.CopyAsync(paths, destination);
        return result.ResultPaths;
    }
}
