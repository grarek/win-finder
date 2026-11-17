<p align="center">
  <img src="src/WinFinder.App/Assets/WinFinder-256.png" width="96" alt="WinFinder icon">
</p>

# WinFinder

A Windows file manager that works like macOS Finder. It's built with C#, .NET 8 and WinUI 3, and uses native Windows 11 controls (Mica, Fluent) laid out the way Finder is.

## Features

### Column view (Ctrl+3)
- Miller columns with a preview column for the selected file.
- **Click, double-click or Enter on a folder** makes it the current folder: the title, tab, path bar and history follow it. Its parent columns stay on the left.
- Back and Forward (toolbar, Alt+←/→, or mouse buttons) restore the same columns. Moving between neighbouring folders in one column doesn't flood the history.
- **Path bar**:
  - Clicking a folder inside the current columns keeps them.
  - Clicking a folder above the leftmost column rebuilds the columns from the drive down, so its parents still show.
- Columns can be resized; Shift+drag resizes all of them. Widths are remembered per folder.

### Quick Look (Space)
- Previews:
  - images, including **animated GIFs**;
  - text and code;
  - PDF, HTML and SVG (through WebView2);
  - audio and video.
- Other files get a large thumbnail plus details.
- Arrow keys move through the selection while it's open. Full-screen toggle.
- Video opens and closes on a background thread, so a file that's slow to buffer (large files, network shares) never freezes the window. Space or Esc always closes Quick Look immediately.

### Sidebar and Favorites
- **Favorites**: pin any folder by
  - right-clicking it and choosing **Add to Favorites**,
  - right-clicking empty space in a folder (pins the folder you're in),
  - the **…** menu,
  - **Ctrl+D**,
  - or dragging it onto the list.

  Drag to reorder; right-click a favorite to remove it.
- **Locations**: This PC, and drives with usage bars.
- **Tags**: click a tag to list every file that carries it.

### Colour tags
- Finder's seven colours, set from the toolbar, the right-click menu or **Ctrl+Shift+1…7** (Ctrl+Shift+0 clears).
- Stored in SQLite and keyed by NTFS file ID, so tags survive renames and moves, even ones made outside the app.

### Tabs and windows
- Each tab keeps its own history, view and selection. New tabs and windows open your home folder.
- Ctrl+T, Ctrl+W, Ctrl+Tab and Ctrl+Alt+1…9 work as expected; Ctrl+Shift+T reopens a closed tab.
- Middle-click or Ctrl+click a folder to open it in a new tab.
- Drag files onto a tab header (hovering switches to that tab). Drag a tab out to make a new window, or into another window.
- Open tabs, including their column layout, are restored on the next launch.

### Views
- **List view**: inline folder expansion (disclosure triangles), sortable and resizable columns.
- **Icon view**: thumbnails with a size slider.

### Drag and drop
- Files land in the folder under the pointer, which is highlighted while you hover ("Move to …" / "Copy to …").
- In list view, dropping onto a file inside an expanded subfolder puts the files in that subfolder.
- Works between columns in column view, onto sidebar favorites and drives, and both ways with Explorer.
- Explorer's rules apply: the same drive moves, a different drive copies; hold Ctrl to copy or Shift to move.

### Real shell integration
- The native Explorer context menu, with every shell extension (7-Zip, VS Code, Send to, Open with).
- Copies, moves, renames and deletes go through IFileOperation: native progress, conflict and UAC dialogs, and the Recycle Bin.
- **Ctrl+Z undoes** them, including moves to the Recycle Bin.
- The clipboard works both ways with Explorer.

### Also
- Search the current folder or the whole PC (Windows Search index, with a folder scan as fallback).
- Go to Folder (Ctrl+Shift+G), Get Info (Ctrl+I), Show Hidden Files (Ctrl+Shift+.). Dot-files are hidden like on macOS.
- Mouse back/forward buttons and the keyboard's Browser Back/Forward keys navigate.

## Keyboard shortcuts

Explorer's keys keep working; Finder's are added where they don't clash.

| Keys | Action |
|---|---|
| Enter, Ctrl+↓ | Open (in column view: open the folder as the next column) |
| F2 | Rename |
| Space | Quick Look |
| Del, Ctrl+Backspace | Move to Recycle Bin |
| Shift+Del | Delete permanently (asks first) |
| Ctrl+1 / Ctrl+2 / Ctrl+3 | Icon / List / Column view |
| Alt+←, Alt+→, Ctrl+[, Ctrl+], Backspace | Back / Forward |
| Alt+↑, Ctrl+↑ | Enclosing folder |
| Ctrl+Shift+N | New folder |
| Ctrl+D | Add to Favorites |
| Ctrl+Shift+1…7, Ctrl+Shift+0 | Toggle a colour tag / clear tags |
| Ctrl+Shift+G, Ctrl+L | Go to Folder |
| Ctrl+I, Alt+Enter | Get Info |
| Ctrl+Shift+. | Show / hide hidden files |
| Ctrl+F, Ctrl+E | Search |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / Cut / Paste files |
| Ctrl+Z | Undo the last file operation |
| Ctrl+T, Ctrl+W, Ctrl+N | New tab, close tab, new window |
| Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+Alt+1…9 | Switch tabs |
| Ctrl+Shift+T | Reopen closed tab |
| Menu key, Shift+F10 | Context menu |
| F5, Ctrl+R | Refresh |

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio isn't needed.
- The WebView2 runtime (preinstalled on Windows 11), used for PDF and HTML previews.

## Install

### Download a release
1. Open the [latest release](https://github.com/grarek/win-finder/releases/latest).
2. Download the zip for your PC: `win-x64` for most PCs, or `win-arm64` for Snapdragon/ARM devices.
3. Extract it to a folder you can write to, such as `%LocalAppData%\Programs\WinFinder`.
4. Run `WinFinder.exe`.

The builds aren't code-signed, so Windows SmartScreen may warn on first launch; choose **More info → Run anyway**.

### Or build and install from source

```bash
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

This publishes an optimized, self-contained build to `%LocalAppData%\Programs\WinFinder` and adds a WinFinder shortcut to the Start Menu. Close WinFinder first if it's running.

## Updates

WinFinder updates itself from this repository's GitHub Releases.

- **Automatic check:** Release builds check about 15 seconds after startup, then every 4 hours while WinFinder stays open (including after the PC wakes from sleep). Turn this off under **… → Check for Updates Automatically**.
- **Manual check:** **… → Check for Updates…** checks now, in any build.
- **When a newer version exists**, a bar appears under the toolbar with three options:
  - **Update and restart**: downloads the zip for your processor and closes WinFinder. A small helper then swaps the new files into the app folder and reopens WinFinder.
  - **What's new**: opens the release notes.
  - **Skip this version**.
- **Safety:** the swap is a folder rename. If it can't happen (for example, a file in the app folder is in use), the current version is kept and the reason is written to `update.log` in the data folder.
- **What's kept:** settings, tags, favorites and open tabs live outside the app folder, so updates keep them.
- **When it can't install in place:** a development build (`dotnet run`), or an app folder WinFinder can't write to (such as Program Files), gets the release page instead.

## Releases (CI)

The **Build and release** workflow ([`.github/workflows/release.yml`](.github/workflows/release.yml)) runs on every push to `main`, which includes every merged pull request. It:
1. runs the tests;
2. publishes optimized self-contained `win-x64` and `win-arm64` builds;
3. creates a GitHub Release, marked Latest, with both zips and auto-generated notes.

Releases are numbered one after another: each is the highest existing `vX.Y.Z` tag plus one (`v1.0.8` → `v1.0.9` → `v1.0.10`), worked out by [`tools/next-version.ps1`](tools/next-version.ps1). Re-running the workflow for a commit that was already released reuses its version instead of creating a new release. To start a new minor or major series, push a tag such as `v1.1.0` to the commit you want; the next release continues from there.

Running apps then pick the release up through the updater. The workflow can also be run by hand from the Actions tab.

Pull requests into `main` run the same tests and builds, but don't create a release. The two zips are attached to the workflow run as artifacts for 7 days, so you can try a PR's build before merging.

To point the updater at a fork, publish with `-p:UpdateRepository=owner/repo`.

## Develop

```bash
dotnet build src/WinFinder.App
```

```bash
dotnet run --project src/WinFinder.App
```

```bash
dotnet test tests/WinFinder.Core.Tests
```

- The project defaults to x64. For ARM64, add `-p:Platform=ARM64`.
- `dotnet run` rechecks the build each time, which adds several seconds before the app starts. Use the installed build when you just want to use the app.
- Pass a folder path as the first argument to open there.
- Settings, tags and the saved session live in `%LocalAppData%\WinFinder`. To run a second copy (for example, a dev build) without touching them, set `WINFINDER_DATA_DIR` to another folder first.
- Set `WINFINDER_TRACE=1` to write startup timings to `startup-trace.log` in the data folder.

## Project layout

| Path | What's in it |
|---|---|
| `src/WinFinder.Core` | UI-free logic: directory listing and watching, sorting, navigation and the column model, the keymap, shell interop (thumbnails, IFileOperation and undo, context menus), tags, search, settings |
| `src/WinFinder.App` | The WinUI 3 app: view models (tabs, folder contents, list tree, column view, sidebar), views, window and session management |
| `tests/WinFinder.Core.Tests` | xUnit tests for Core |
| `src/WinFinder.App/Services/UpdateService.cs` | The in-app updater (release check, download, swap-and-restart helper) |
| `.github/workflows/release.yml` | Builds, tests and publishes a GitHub Release on every push to `main` |
| `tools/install.ps1` | Publishes the Release build and adds the Start Menu shortcut |
| `tools/make-icon.ps1` | Draws the app icon and writes `src/WinFinder.App/Assets/WinFinder.ico` (16–256 px; small sizes use a simplified drawing) |

## Known limitations

- Subfolders expanded inline in list view don't update live; they refresh when re-expanded or after a drop into them. The top-level listing is live.
- The context menu can only cover items that share a folder. In search results that span several folders, it covers the selected items in the first item's folder.
- The app runs unpackaged and isn't registered as the default folder handler, so Win+E still opens Explorer.

## License

[MIT](LICENSE)
