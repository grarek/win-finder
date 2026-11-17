using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTrace.Mark("OnLaunched");
        AppServices.Initialize(DispatcherQueue.GetForCurrentThread());
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
        var flush = DispatcherQueue.GetForCurrentThread().CreateTimer();
        flush.Interval = TimeSpan.FromSeconds(3);
        flush.IsRepeating = false;
        flush.Tick += (_, _) => StartupTrace.Flush();
        flush.Start();
    }
}
