using System.Windows;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.Core.Indexing;
using DriveSync.Core.Parsing;

namespace DriveSync.App.Shell;

/// <summary>User actions that several screens share, with their confirmations and toasts.</summary>
public static class Actions
{
    private static IndexService S => App.Service;

    /// <summary>The file menu: right-click on a result, Shift+F10 and the preview's More button.</summary>
    public static void FileMenu(FileView f, FrameworkElement anchor, Point? screenPoint, Action summarize)
    {
        FluentMenu.Show(anchor, new List<MenuEntry>
        {
            new() { Label = "Open", Icon = "i-open", Kbd = "Enter", Action = () => Ui.Open(f) },
            new() { Label = "Show in folder", Icon = "i-folder-open", Action = () => Ui.Reveal(f) },
            MenuEntry.Separator,
            new() { Label = "Copy path", Icon = "i-copy", Kbd = "Ctrl+Shift+C", Action = () => Ui.Copy(f.FullPath) },
            new() { Label = "Copy name", Icon = "i-copy", Action = () => Ui.Copy(f.Name, "Name") },
            new() { Label = "Summarize", Icon = "i-sparkle", Action = summarize },
            MenuEntry.Separator,
            new() { Label = "Exclude this folder from the index", Icon = "i-eye-off", Danger = true, Action = () => ExcludeFolder(f) },
        }, screenPoint);
    }

    /// <summary>Adds the file's folder as an exclusion rule at once, with Undo.</summary>
    public static void ExcludeFolder(FileView f)
    {
        string dir = f.Folder.TrimEnd('\\');
        IndexedFolder? root = S.FolderOf(f.RootId);
        if (root != null && string.Equals(root.Path.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Toast(NoticeKind.Warn, $"{root.Name} is an indexed folder", "To stop indexing it, remove it in Indexed folders instead.",
                "Open Indexed folders", () => App.Host.Navigate("folders"));
            return;
        }
        if (S.Exclusions.Any(x => x.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
        UndoToken token = S.AddExclusion(dir);
        string name = Path.GetFileName(dir);
        App.Host.SearchPage.AfterIndexEdit();
        Ui.Toast(NoticeKind.Ok, "Folder excluded",
            $"{Fmt.Plural(token.Count, "file")} in {name} will no longer appear in results.",
            "Undo", () =>
            {
                S.UndoExclusion(token);
                App.Host.SearchPage.AfterIndexEdit();
                Ui.Toast(NoticeKind.Ok, "Exclusion removed", ms: 2400);
            }, ms: 7000);
    }

    public static void Rebuild(bool confirm = true)
    {
        if (S.IsCleared || !confirm)
        {
            RunRebuild();
            return;
        }
        int reachable = S.Folders.Count(f => f.Status != RootStatus.Unreachable);
        ConfirmDialog.Show(new ConfirmDialog.Options
        {
            Title = "Rebuild the index?",
            Description = $"DriveSync scans all {reachable} reachable folder{(reachable == 1 ? "" : "s")} from scratch. It usually takes under a minute, and search keeps working while it runs.",
            ConfirmLabel = "Rebuild",
            ConfirmIcon = "i-refresh",
            Variant = ButtonVariant.Primary,
            FocusConfirm = true,
            OnConfirm = RunRebuild,
        });
    }

    private static async void RunRebuild()
    {
        Task<JobOutcome>? job = S.TryRebuild();
        if (job == null) return;
        PreviewService.ClearCache();
        try
        {
            JobOutcome r = await job;
            Ui.Toast(NoticeKind.Ok, "Index rebuilt", $"{Fmt.Num(r.Files)} files from {S.Folders.Count} folders, built in {Fmt.Ms(r.BuildMs)}.");
        }
        catch (Exception) { /* the service already showed the error */ }
    }

    public static void ConfirmClear()
    {
        ConfirmDialog.Show(new ConfirmDialog.Options
        {
            Title = "Clear the index?",
            Description = $"DriveSync deletes its list of {Fmt.Num(S.FileCount)} files and all cached summaries. Searches return nothing until you rebuild. Your files and folders are not touched.",
            Acknowledgement = "I understand searching won't work until I rebuild",
            ConfirmLabel = "Clear index",
            ConfirmIcon = "i-trash",
            Variant = ButtonVariant.Danger,
            OnConfirm = async () =>
            {
                PreviewService.ClearCache();
                await S.ClearIndexAsync();
                App.Host.SearchPage.AfterIndexEdit();
                Ui.Toast(NoticeKind.Warn, "Index cleared", "Rebuild it to search again.", "Rebuild now", () => Rebuild(confirm: false), ms: 8000);
            },
        });
    }

    /// <summary>Adds and scans a folder, then offers to search it.</summary>
    public static async void AddFolder(string path, string? name, bool subfolders, IReadOnlySet<Core.TypeGroup>? types, IReadOnlyList<IndexedFolder> merges)
    {
        Task<JobOutcome>? job = S.TryAddFolder(path, name, subfolders, types, merges);
        if (job == null) return;
        if (App.Host.CurrentPage != "folders") App.Host.Navigate("folders");
        try
        {
            await job;
            IndexedFolder? added = S.Folders.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (added == null) return;
            Ui.Toast(NoticeKind.Ok, $"{added.Name} is indexed", $"{Fmt.Num(added.FileCount)} files added. DriveSync will keep it up to date.",
                "Search it", () =>
                {
                    App.Host.Navigate("search");
                    App.Host.SearchPage.SetFolderFilter(added.Id);
                });
        }
        catch (Exception) { /* reported by the service */ }
    }

    public static async void RescanFolder(IndexedFolder folder)
    {
        Task<JobOutcome>? job = S.TryRescanFolder(folder.Id);
        if (job == null) return;
        try
        {
            await job;
            int count = S.FolderOf(folder.Id)?.FileCount ?? 0;
            Ui.Toast(NoticeKind.Ok, $"{folder.Name} rescanned", $"{Fmt.Num(count)} files, no missed changes.");
        }
        catch (Exception) { /* reported by the service */ }
    }

    /// <summary>Check again for disconnected drives.</summary>
    public static void CheckDrives(UiButton? button)
    {
        if (button != null)
        {
            button.IsEnabled = false;
            button.SpinIcon = true;
            button.Content = "Checking…";
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            List<IndexedFolder> missing = S.CheckDrivesNow();
            if (button != null)
            {
                button.IsEnabled = true;
                button.SpinIcon = false;
                button.Content = "Check again";
            }
            if (missing.Count > 0)
            {
                string drives = string.Join(", ", missing.Select(f => f.Drive).Distinct());
                Ui.Toast(NoticeKind.Err, $"Drive {drives} still not found", "Connect the external drive and DriveSync will reconnect on its own.");
            }
            App.Host.SearchPage.AfterIndexEdit();
        };
        timer.Start();
    }
}
