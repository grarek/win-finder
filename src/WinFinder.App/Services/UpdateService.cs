using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using WinFinder.Core.Updates;

namespace WinFinder.App.Services;

/// <summary>
/// In-app updater. Checks the repository's latest GitHub Release, downloads the zip for this
/// machine's architecture, and hands off to a small PowerShell script that waits for WinFinder
/// to exit, swaps the new files into the install folder (rolling back on failure) and relaunches.
/// Settings, tags and the session live in the data folder, so they're untouched.
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Instance { get; } = new();

    private static readonly HttpClient Http = CreateClient();
    private bool _checking;

    private UpdateService() { }

    public Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    public string Repository { get; } =
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "grarek/win-finder";

    /// <summary>Folder the running app lives in (what an update replaces).</summary>
    public static string InstallFolder => AppContext.BaseDirectory.TrimEnd('\\', '/');

#if DEBUG
    public static bool IsDevelopmentBuild => true;
#else
    public static bool IsDevelopmentBuild => false;
#endif

    /// <summary>The newest release found that's newer than this build (and not skipped).</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised on the UI thread when a newer release is found.</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a User-Agent; keep it generic.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinFinder-Updater");
        return http;
    }

    /// <summary>Quiet daily check shortly after startup (Release builds only, respects the setting).</summary>
    public void StartBackgroundCheck()
    {
        var s = AppServices.Settings.Current;
        if (IsDevelopmentBuild || !s.AutoCheckForUpdates) return;
        if (s.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;

        var timer = AppServices.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            try { await CheckAsync(manual: false); } catch { /* offline etc.: try again next launch */ }
        };
        timer.Start();
    }

    /// <summary>
    /// Checks GitHub. Returns the latest release (even if not newer) so a manual check can report
    /// "up to date"; raises <see cref="UpdateAvailable"/> when it's newer and not skipped (manual
    /// checks ignore "skip").
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(bool manual)
    {
        if (_checking) return Available;
        _checking = true;
        try
        {
            var latest = await UpdateChecker.GetLatestAsync(Repository, Http);
            AppServices.Settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
            AppServices.Settings.SaveSoon();
            if (latest is null || !UpdateChecker.IsNewer(latest.Version, CurrentVersion)) return latest;

            bool skipped = string.Equals(AppServices.Settings.Current.SkippedVersion, latest.Tag, StringComparison.OrdinalIgnoreCase);
            if (skipped && !manual) return latest;

            Available = latest;
            UpdateAvailable?.Invoke(latest);
            return latest;
        }
        finally
        {
            _checking = false;
        }
    }

    public void Skip(ReleaseInfo release)
    {
        AppServices.Settings.Current.SkippedVersion = release.Tag;
        AppServices.Settings.SaveSoon();
    }

    /// <summary>Why an in-place update isn't possible here (null when it is).</summary>
    public string? InPlaceUpdateBlocker(ReleaseInfo release)
    {
        if (IsDevelopmentBuild) return "This is a development build, so it isn't replaced automatically.";
        if (release.AssetFor(RuntimeInformation.ProcessArchitecture) is null) return "This release has no download for this PC's processor.";
        if (!CanWrite(InstallFolder) || !CanWrite(Path.GetDirectoryName(InstallFolder)!))
            return $"WinFinder can't write to {InstallFolder}.";
        return null;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".wf-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Downloads and stages the update, starts the swap script, and returns. The caller must then
    /// exit the app promptly (the script waits for this process to end before replacing files).
    /// </summary>
    public async Task PrepareAndLaunchUpdaterAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        var asset = release.AssetFor(RuntimeInformation.ProcessArchitecture)
                    ?? throw new InvalidOperationException("No download for this PC's processor.");

        string work = Path.Combine(Path.GetTempPath(), "WinFinder-update", release.Tag);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, asset.Name);
        string extracted = Path.Combine(work, "app");

        // Download with progress.
        using (var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted), ct);
        if (!File.Exists(Path.Combine(extracted, "WinFinder.exe")))
            throw new InvalidDataException("The downloaded update doesn't contain WinFinder.exe.");

        string script = Path.Combine(work, "apply-update.ps1");
        await File.WriteAllTextAsync(script, ApplyScript, ct);
        string log = Path.Combine(Core.Settings.SettingsStore.DefaultDirectory, "update.log");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-ParentPid", Environment.ProcessId.ToString(),
            "-Source", extracted,
            "-Target", InstallFolder,
            "-Log", log,
        })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    /// <summary>
    /// Waits for WinFinder to exit, copies the new build next to the install folder, then swaps
    /// folders by rename (fast, and reversible if anything fails), and relaunches.
    /// </summary>
    private const string ApplyScript = """
        param([int]$ParentPid, [string]$Source, [string]$Target, [string]$Log)
        $ErrorActionPreference = 'Stop'
        function Write-Log([string]$m) {
            try { Add-Content -LiteralPath $Log -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) } catch {}
        }
        try {
            Write-Log "Updating $Target from $Source (waiting for pid $ParentPid)"
            try { Wait-Process -Id $ParentPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
            # Other WinFinder windows running from this folder must close too.
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Process WinFinder -ErrorAction SilentlyContinue |
                    Where-Object { $_.Path -and $_.Path.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) }) -and
                   (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }

            $staging = "$Target.new"
            $backup = "$Target.old"
            foreach ($p in $staging, $backup) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
            Copy-Item -LiteralPath $Source -Destination $staging -Recurse

            # [IO.Directory]::Move is a pure rename: it either happens completely or not at all
            # (Move-Item can fall back to a partial copy when a file is in use).
            [IO.Directory]::Move($Target, $backup)
            try {
                [IO.Directory]::Move($staging, $Target)
            } catch {
                [IO.Directory]::Move($backup, $Target)   # roll back
                throw
            }
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "Update installed."
        } catch {
            Write-Log "Update failed, keeping the current version: $($_.Exception.Message)"
            if ((Test-Path -LiteralPath $Target) -and (Test-Path -LiteralPath "$Target.new")) {
                Remove-Item -LiteralPath "$Target.new" -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        try {
            Start-Process -FilePath (Join-Path $Target 'WinFinder.exe') -WorkingDirectory $Target
            Write-Log "Relaunched WinFinder."
        } catch {
            Write-Log "Couldn't relaunch WinFinder: $($_.Exception.Message)"
        }
        try { Remove-Item -LiteralPath (Split-Path $Source -Parent) -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        """;
}
