using System.Windows;
using Forms = System.Windows.Forms;

namespace DriveSync.App.Shell;

/// <summary>
/// The real notification-area icon. Clicking it opens the status flyout; while the window is closed
/// to the tray, DriveSync keeps watching and indexing.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private TrayFlyout? _flyout;
    private DateTime _flyoutClosed;

    public TrayService()
    {
        using Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DriveSync.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16)),
            Text = "DriveSync",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                Application.Current.Dispatcher.Invoke(ToggleFlyout);
        };
        _icon.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.Invoke(() => App.Host.Restore());
    }

    /// <summary>Updates the hover text ("DriveSync · Watching · 551 files").</summary>
    public void SetStatus(string status)
    {
        string text = "DriveSync · " + status;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.None);

    private void ToggleFlyout()
    {
        if (_flyout != null) { _flyout.Close(); return; }
        // Clicking the icon while the flyout is open deactivates (closes) it first; don't reopen.
        if ((DateTime.Now - _flyoutClosed).TotalMilliseconds < 250) return;
        _flyout = new TrayFlyout();
        _flyout.Closed += (_, _) => { _flyout = null; _flyoutClosed = DateTime.Now; };
        _flyout.Show();
        _flyout.Activate();
    }

    public void Dispose()
    {
        _flyout?.Close();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
