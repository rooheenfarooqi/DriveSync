using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Shell;

/// <summary>Small helpers shared by every page: toasts, clipboard, opening files.</summary>
public static class Ui
{
    public static IndexService Service => App.Service;

    public static void Toast(NoticeKind kind, string title, string message = "", string? actionLabel = null, Action? action = null, int ms = 4600) =>
        App.Host.Toasts.Show(new Notice(kind, title, message, actionLabel, action, ms));

    public static DialogHandle Dialog(FrameworkElement content, bool wide = false, string label = "Dialog") =>
        App.Host.Dialogs.Show(content, wide, label);

    public static bool IsOffline(FileView f) => Service.FolderOf(f.RootId)?.Status == RootStatus.Unreachable;

    public static string DriveOf(FileView f) => (System.IO.Path.GetPathRoot(f.Folder) ?? "").TrimEnd('\\');

    /// <summary>Copies text and confirms with a toast ("Path copied"). Retries briefly if another app holds the clipboard.</summary>
    public static void Copy(string text, string what = "Path")
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                Toast(NoticeKind.Ok, $"{what} copied", text, ms: 3000);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(40);
            }
        }
        Toast(NoticeKind.Warn, "Copy was blocked", "Another app is using the clipboard. Try again: " + text, ms: 7000);
    }

    /// <summary>Opens a file in its default app (like double-clicking it in File Explorer).</summary>
    public static void Open(FileView f)
    {
        if (IsOffline(f))
        {
            Toast(NoticeKind.Err, $"Can't open {f.Name}", $"Drive {DriveOf(f)} isn't connected. Plug it in and try again.");
            return;
        }
        if (!System.IO.File.Exists(f.FullPath))
        {
            Toast(NoticeKind.Err, $"Can't open {f.Name}", "The file isn't there any more. It may have been moved or deleted.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(f.FullPath) { UseShellExecute = true });
            Toast(NoticeKind.Info, $"Opening {f.Name}", string.IsNullOrEmpty(f.Extension) ? "In its default app." : $"In your default app for .{f.Extension} files.", ms: 2600);
        }
        catch (Win32Exception ex)
        {
            Toast(NoticeKind.Err, $"Can't open {f.Name}", ex.NativeErrorCode == 1155 ? "No app is set up to open this type of file." : ex.Message);
        }
    }

    /// <summary>Opens File Explorer with the file selected.</summary>
    public static void Reveal(FileView f)
    {
        if (IsOffline(f))
        {
            Toast(NoticeKind.Err, "Folder not available", $"Drive {DriveOf(f)} isn't connected.");
            return;
        }
        try
        {
            if (System.IO.File.Exists(f.FullPath))
                Process.Start("explorer.exe", $"/select,\"{f.FullPath}\"");
            else
                Process.Start(new ProcessStartInfo(f.Folder) { UseShellExecute = true });
            Toast(NoticeKind.Info, "Showing in File Explorer", f.Folder, ms: 3000);
        }
        catch (Win32Exception ex)
        {
            Toast(NoticeKind.Err, "Couldn't open File Explorer", ex.Message);
        }
    }
}
