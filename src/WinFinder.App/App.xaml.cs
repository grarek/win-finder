using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinFinder.App.Services;
using WinFinder.App.ViewModels;

namespace WinFinder.App;

public partial class App : Application
{
    public App()
    {
        StartupTrace.Mark("App ctor");
        InitializeComponent();
        StartupTrace.Mark("App resources loaded");
        UnhandledException += (_, e) =>
        {
            // Keep the file manager alive on unexpected UI exceptions; log for diagnosis.
            try
            {
                File.AppendAllText(Path.Combine(Core.Settings.SettingsStore.DefaultDirectory, "errors.log"),
                    $"[{DateTime.Now:O}] {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
        };
    }

    private static DispatcherQueue? _ui;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTrace.Mark("OnLaunched");
        _ui = DispatcherQueue.GetForCurrentThread();

        // Single instance, like Explorer: a second launch (taskbar jump list, Start Menu, command
        // line) hands its arguments to the running copy, which opens a new window, then exits.
        if (await RedirectToRunningInstanceAsync())
        {
            Exit();
            return;
        }

        AppServices.Initialize(_ui);
        StartupTrace.Mark("services initialized");
        ListLayout.Instance.Load();
        SidebarViewModel.Instance.Initialize();
        StartupTrace.Mark("sidebar initialized");

        var cmdArgs = Environment.GetCommandLineArgs();
        string? startPath = cmdArgs.Length > 1 ? cmdArgs[1] : null;
        // Resolve a relative path argument first, then move the working folder off the install
        // folder (shortcuts often start apps there), so nothing we launch keeps it busy and an
        // update can rename it.
        if (startPath is { Length: > 0 } && !startPath.Contains('%') && !startPath.StartsWith('~'))
        {
            try { startPath = Path.GetFullPath(startPath); } catch { }
        }
        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
        WindowManager.Start(startPath);
        StartupTrace.Mark("window created + activated");
        UpdateService.Instance.StartBackgroundCheck();
        JumpListService.Initialize();
        var flush = DispatcherQueue.GetForCurrentThread().CreateTimer();
        flush.Interval = TimeSpan.FromSeconds(3);
        flush.IsRepeating = false;
        flush.Tick += (_, _) => StartupTrace.Flush();
        flush.Start();
    }

    /// <summary>
    /// One running copy per data folder (so a test copy with its own WINFINDER_DATA_DIR stays separate).
    /// </summary>
    private static string InstanceKey
    {
        get
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Core.Settings.SettingsStore.DefaultDirectory.ToLowerInvariant()));
            return "WinFinder-" + Convert.ToHexString(bytes)[..16];
        }
    }

    /// <summary>True if another copy is already running and has been handed this launch.</summary>
    private static async Task<bool> RedirectToRunningInstanceAsync()
    {
        try
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (main.IsCurrent)
            {
                main.Activated += OnForwardedLaunch;
                return false;
            }
            // Let the running copy bring its new window to the front.
            Interop.WindowHelper.AllowSetForeground((int)main.ProcessId);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirecting must not run on the UI (STA) thread.
            await Task.Run(() => main.RedirectActivationToAsync(activation).AsTask());
            return true;
        }
        catch
        {
            return false; // if anything goes wrong, just run as a normal copy
        }
    }

    /// <summary>Another launch was forwarded to us: open its folder (or Home) in a new window.</summary>
    private static void OnForwardedLaunch(object? sender, AppActivationArguments e)
    {
        if (e.Kind != ExtendedActivationKind.Launch) return;
        string? commandLine = (e.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        string? path = Core.Shell.CommandLine.FirstPathArgument(commandLine);
        _ui?.TryEnqueue(() => WindowManager.OpenFromLaunch(path));
    }
}
