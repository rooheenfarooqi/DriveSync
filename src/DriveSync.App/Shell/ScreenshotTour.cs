using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DriveSync.App.Dialogs;
using DriveSync.App.Pages;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Shell;

/// <summary>
/// Developer tool: with DRIVESYNC_TOUR set to a folder (and DRIVESYNC_DATA set to a test database), DriveSync
/// opens off-screen, renders every page, state and dialog to PNG files there, and exits. Used to check the
/// screens against the design without touching the user's own window or index.
/// </summary>
public static class ScreenshotTour
{
    public static string? Folder { get; } = Environment.GetEnvironmentVariable("DRIVESYNC_TOUR");

    public static void Log(string line)
    {
        if (!Active) return;
        try { File.AppendAllText(Path.Combine(Folder!, "tour.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    public static bool Active => !string.IsNullOrEmpty(Folder) && !string.IsNullOrEmpty(App.SandboxFolder);

    public static async Task RunAsync(MainWindow window)
    {
        Directory.CreateDirectory(Folder!);
        IndexService s = App.Service;
        string? folder = Environment.GetEnvironmentVariable("DRIVESYNC_TOUR_FOLDER");
        if (s.Folders.Count == 0 && folder != null)
        {
            s.UpdateSettings(x => x.Onboarded = true);
            Task<JobOutcome>? job = s.TryRunSetup(new[] { new FolderChoice(folder, FolderRules.DefaultName(folder)) });
            if (job != null) await job;
        }
        string query = Environment.GetEnvironmentVariable("DRIVESYNC_TOUR_QUERY") ?? "report";

        async Task Shot(string name, int wait = 700)
        {
            await Task.Delay(wait);
            window.UpdateLayout();
            FrameworkElement root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var ground = new DrawingVisual();
            using (DrawingContext dc = ground.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0c, 0x14, 0x1a)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bitmap.Render(ground);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(Folder!, name + ".png"));
            encoder.Save(file);
        }

        foreach (string page in new[] { "home", "folders", "activity", "preferences" })
        {
            window.Navigate(page);
            await Shot("page-" + page);
        }

        window.Navigate("search");
        await Shot("search-ready");
        SearchPage search = window.SearchPage;
        search.SetQuery(query, commit: true);
        await Shot("search-results", 1500);
        search.ShowTab(1);
        await Shot("search-outline", 1200);
        search.ShowTab(2);
        await Shot("search-details", 600);
        search.ShowTab(0);
        search.SetQuery("qzxvkq", commit: false);
        await Shot("search-noresults", 900);
        search.SetQuery("ext:py", commit: false);
        await Shot("search-code", 1500);

        AddFolderDialog.Show(@"Q:\Nowhere");
        await Shot("dialog-addfolder-invalid");
        window.Dialogs.Show(new System.Windows.Controls.Border()).Close();
        ShortcutsDialog.Show();
        await Shot("dialog-shortcuts");
        window.Dialogs.Show(new System.Windows.Controls.Border()).Close();
        Actions.ConfirmClear();
        await Shot("dialog-clear");
        window.Dialogs.Show(new System.Windows.Controls.Border()).Close();
        OnboardingDialog.Show(firstRun: false);
        await Shot("dialog-onboarding");
        window.Dialogs.Show(new System.Windows.Controls.Border()).Close();

        window.Navigate("benchmarks");
        await Shot("page-benchmarks-running", 1500);
        for (int i = 0; i < 120 && App.Service.LoadBenchmark() == null; i++) await Task.Delay(1000);
        await Shot("page-benchmarks", 800);

        // Narrow window: icon rail and sliding preview sheet.
        window.Width = 700;
        window.Navigate("search");
        search.SetQuery(query, commit: true);
        await Shot("narrow-search", 1500);
        search.OpenSheetForTour();
        await Shot("narrow-preview", 900);

        App.Quit();
    }
}
