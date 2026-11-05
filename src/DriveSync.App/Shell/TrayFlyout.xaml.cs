using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Shell;

/// <summary>The flyout that opens from the tray icon (design page 37, "Closed to tray").</summary>
public partial class TrayFlyout : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TrayFlyout()
    {
        InitializeComponent();
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            MicaHelper.Apply(this, acrylic: true);
            PlaceNearTray();
            Refresh();
            _timer.Start();
            var shift = new TranslateTransform(0, 8);
            Surface.RenderTransform = shift;
            Motion.To(Surface, OpacityProperty, 1, 200, 0);
            Motion.To(shift, TranslateTransform.YProperty, 0, 200, 8);
            OpenButton.Focus();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void PlaceNearTray()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth + 12;
        Top = area.Bottom - ActualHeight + 12;
    }

    private void Refresh()
    {
        IndexService s = App.Service;
        StatusLine.Text = s.Job != null ? s.Job.Label + $" {s.Job.Percent}%"
            : s.IsCleared ? "Index is empty"
            : s.BatteryPaused ? "Paused on battery saver"
            : s.Watching ? "Watching for changes" : "Watching paused";
        FilesValue.Text = Fmt.Num(s.FileCount);
        RefreshValue.Text = Fmt.Rel(s.LastRefresh);
        ChangesValue.Text = Fmt.Num(s.ChangesToday);
        PauseButton.Content = s.Watching ? "Pause" : "Resume";
        RefreshButton.IsEnabled = s.Job == null;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Close();
        App.Host.Restore();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        App.Host.ToggleWatching();
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        App.Host.RefreshNow();
        Refresh();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        App.Quit();
    }
}
