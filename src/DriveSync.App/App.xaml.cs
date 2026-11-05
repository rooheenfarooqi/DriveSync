using System.Windows;
using System.Windows.Threading;
using DriveSync.App.Shell;
using DriveSync.Core.Indexing;

namespace DriveSync.App;

/// <summary>
/// Start-up: one instance only, splash screen, settings and theme, the window, then the saved index.
/// Started with --background (Start with Windows) it goes straight to the tray.
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    public static IndexService Service { get; private set; } = null!;

    /// <summary>
    /// Set DRIVESYNC_DATA to a folder to run DriveSync on a separate database (for testing). Sandboxed runs
    /// don't touch the Start with Windows registry entry.
    /// </summary>
    public static string? SandboxFolder { get; } = Environment.GetEnvironmentVariable("DRIVESYNC_DATA");
    public static MainWindow Host { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch just brings the running DriveSync to the front.
        // Each test database gets its own instance, so a sandbox never clashes with the real DriveSync.
        string instance = SandboxFolder is { Length: > 0 } dir0 ? "DriveSync.Sandbox." + (uint)dir0.ToLowerInvariant().GetHashCode() : "DriveSync";
        _single = new Mutex(true, $@"Local\{instance}.App", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instance}.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => Host?.Restore());
        }) { IsBackground = true, Name = "DriveSync show signal" };
        listener.Start();

        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!background && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVESYNC_TOUR")))
            new SplashScreen("Assets/Splash.png").Show(autoClose: true, topMost: true);

        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(350));
        DispatcherUnhandledException += OnUnhandled;

        Service = new IndexService(SandboxFolder is { Length: > 0 } dir ? Path.Combine(dir, "index.db") : null);
        await Service.LoadConfigAsync();
        ThemeManager.Apply(Service.Settings);

        Host = new MainWindow();
        MainWindow = Host;
        Host.Navigate("home");
        if (ScreenshotTour.Active)
        {
            // Off-screen and not activated: the tour never takes focus from the user.
            Host.WindowStartupLocation = WindowStartupLocation.Manual;
            Host.Left = -20000;
            Host.Top = 0;
            Host.Width = 1280;
            Host.Height = 800;
            Host.ShowActivated = false;
            Host.Show();
        }
        else if (!background || !Service.Settings.KeepRunningWhenClosed)
            Host.Show();

        await Service.LoadIndexAsync();
        Host.OnServiceReady();
        if (ScreenshotTour.Active) await ScreenshotTour.RunAsync(Host);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive and say what went wrong instead of crashing the background indexer.
        e.Handled = true;
        Host?.Toasts.Show(new Notice(NoticeKind.Err, "Something went wrong", e.Exception.Message));
    }

    /// <summary>Saves everything and exits (tray "Quit", or closing with "Keep running" off).</summary>
    public static void Quit()
    {
        Host?.Shutdown();
        Service?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
