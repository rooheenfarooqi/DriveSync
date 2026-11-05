using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DriveSync.App.Shell;

/// <summary>
/// Windows 11 window materials: dark title bar, rounded corners and the Mica system backdrop.
/// On older Windows (or if DWM refuses) the window falls back to a solid dark surface.
/// </summary>
public static class MicaHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Applies the backdrop. Returns true when Mica (or acrylic) is active.</summary>
    public static bool Apply(Window window, bool acrylic = false)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        int on = 1, round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        if (Environment.OSVersion.Version.Build < 22523)
            return false;
        // With the frame extended under a system backdrop, DWM draws its own caption buttons.
        // DriveSync draws its own, so drop the system menu style that makes DWM add them.
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        int backdrop = acrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>The frame thickness a maximized WindowChrome window overhangs the screen by, in DIPs.</summary>
    public static Thickness MaximizedInset(Window window)
    {
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int frame = GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */);
        double v = frame / scale;
        return new Thickness(v, v, v, v);
    }
}

/// <summary>
/// The "Open search from anywhere" shortcut, registered with RegisterHotKey so it works while
/// DriveSync is in the background or the tray.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0xD5;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public void Attach(Window window)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(Hook);
    }

    /// <summary>Registers "Alt+Space", "Ctrl+Shift+F", "Win+Alt+S" or nothing ("off"). Returns false if another app owns it.</summary>
    public bool Register(string combo)
    {
        Unregister();
        if (_source == null || string.IsNullOrEmpty(combo) || combo == "off") return true;
        uint mods = MOD_NOREPEAT, vk = 0;
        foreach (string part in combo.Split('+'))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                case "space": vk = 0x20; break;
                default: if (part.Trim().Length == 1) vk = char.ToUpperInvariant(part.Trim()[0]); break;
            }
        }
        if (vk == 0) return false;
        _registered = RegisterHotKey(_source.Handle, Id, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source != null) UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
    }
}

/// <summary>Start with Windows: the per-user Run key, starting quietly in the background.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "DriveSync";

    public static void Apply(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            string? exe = Environment.ProcessPath;
            if (enabled && exe != null)
            {
                string value = $"\"{exe}\" --background";
                if (key.GetValue(Name) as string != value) key.SetValue(Name, value);
            }
            else if (key.GetValue(Name) != null)
            {
                key.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Group policy can block the Run key; the setting simply has no effect then.
        }
    }
}
