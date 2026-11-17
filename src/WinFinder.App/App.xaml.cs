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
