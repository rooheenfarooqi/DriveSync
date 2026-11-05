using DriveSync.App.Shell;
using DriveSync.Core.Storage;
using Velopack;

namespace DriveSync.App;

/// <summary>
/// Entry point. The installer, updater and uninstaller start DriveSync.exe with special arguments;
/// Velopack handles those first and exits before any window appears. A normal start goes on to WPF.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => OnUninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Uninstalling removes everything DriveSync left on the PC: the Start with Windows entry and the
    /// index folder (file names, settings and search history). The user's own files are never touched.
    /// </summary>
    private static void OnUninstall()
    {
        StartupRegistration.Apply(false);
        try
        {
            string? folder = Path.GetDirectoryName(IndexStore.DefaultPath);
            if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use stays behind; uninstalling itself must not fail because of it.
        }
    }
}
