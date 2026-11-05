using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Dialogs;

/// <summary>
/// Add a folder (design pages 23–24): path with inline validation, quick picks, Browse… with the Windows
/// folder picker, display name, Include subfolders, and the file types to index.
/// </summary>
public static class AddFolderDialog
{
    private static IndexService S => App.Service;

    /// <summary>Common folders that exist on this PC: Desktop, Documents, Pictures, Music, OneDrive (and Downloads).</summary>
    public static List<(string Name, string Path)> QuickPicks()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var picks = new List<(string, string)>
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Path.Combine(home, "Downloads")),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("OneDrive", Environment.GetEnvironmentVariable("OneDrive") ?? Path.Combine(home, "OneDrive")),
        };
        return picks.Where(p => !string.IsNullOrEmpty(p.Item2) && Directory.Exists(p.Item2)).ToList();
    }

    public static void Show(string prefill = "")
    {
        var frame = new DialogFrame("Add a folder", "DriveSync scans it once, then watches it for new, edited and deleted files.");
        var stack = new StackPanel();
        frame.Body.Children.Add(stack);

        // Folder path + Browse…
        stack.Children.Add(Label("Folder path"));
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var path = new TextBox { Text = prefill, Tag = "autofocus" };
        path.SetResourceReference(FrameworkElement.StyleProperty, "InputMono");
        Field.SetPlaceholder(path, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        System.Windows.Automation.AutomationProperties.SetName(path, "Folder path");
        pathRow.Children.Add(path);
        UiButton browse = Build.Button("Browse…", "i-folder-open");
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        stack.Children.Add(pathRow);
        TextBlock hint = Hint("Paste a path from File Explorer's address bar, pick a common folder below, or use Browse…");
        stack.Children.Add(hint);

        // Quick picks
        var quick = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (name, p) in QuickPicks())
        {
            string target = p;
            Button chip = Build.Chip(name, "i-folder", () => { path.Text = target; Check(true); });
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.IsEnabled = !S.Folders.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
            quick.Children.Add(chip);
        }
        stack.Children.Add(quick);

        // Display name
        var nameLabel = Label("Display name ");
        var optional = new System.Windows.Documents.Run("(optional)");
        optional.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text3Brush");
        nameLabel.Inlines.Add(optional);
        nameLabel.Margin = new Thickness(0, 12, 0, 6);
        stack.Children.Add(nameLabel);
        var displayName = new TextBox();
        displayName.SetResourceReference(FrameworkElement.StyleProperty, "Input");
        Field.SetPlaceholder(displayName, "Uses the folder's name");
        System.Windows.Automation.AutomationProperties.SetName(displayName, "Display name");
        stack.Children.Add(displayName);

        // Include subfolders
        var subRow = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var subToggle = new ToggleSwitch { IsChecked = true };
        System.Windows.Automation.AutomationProperties.SetName(subToggle, "Include subfolders");
        DockPanel.SetDock(subToggle, Dock.Right);
        subRow.Children.Add(subToggle);
        var subText = new StackPanel();
        subText.Children.Add(Build.Text("Include subfolders", weight: FontWeights.Medium));
        subText.Children.Add(Hint("Recommended. Turn off to index only the files directly inside."));
        subRow.Children.Add(subText);
        stack.Children.Add(subRow);

        // File types
        stack.Children.Add(Label("File types to index", top: 16));
        var types = new UniformGrid { Columns = App.Host.Band == LayoutBand.Tiny ? 1 : App.Host.Band == LayoutBand.Narrow ? 2 : 3 };
        var boxes = new List<(CheckBox Box, TypeGroup Group)>();
        foreach (TypeGroup g in FileKinds.AllGroups.Where(g => g != TypeGroup.All))
        {
            var box = new CheckBox { Content = FileKinds.GroupLabel(g), IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
            box.SetResourceReference(FrameworkElement.StyleProperty, "Check");
            boxes.Add((box, g));
            types.Children.Add(box);
        }
        stack.Children.Add(types);
        TextBlock typesHint = Hint("");
        stack.Children.Add(typesHint);

        frame.AddButton("Cancel", click: frame.Close);
        UiButton go = frame.AddButton("Add and scan", ButtonVariant.Primary);

        FolderRules.Result Check(bool show)
        {
            FolderRules.Result r = FolderRules.Validate(path.Text, S.Folders);
            bool error = show && r.Error != null;
            Field.SetIsInvalid(path, error);
            if (error) { hint.Text = r.Error; hint.Tag = "err"; }
            else if (r.Warning != null) { hint.Text = r.Warning; hint.Tag = "warn"; }
            else if (r.Path != null) { hint.Text = "Ready to scan."; hint.Tag = "ok"; }
            else { hint.Text = "Paste a path from File Explorer's address bar, pick a common folder below, or use Browse…"; hint.Tag = null; }
            return r;
        }

        void Submit()
        {
            FolderRules.Result r = Check(true);
            if (r.Error != null || r.Path == null)
            {
                path.Focus();
                return;
            }
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Group).ToHashSet();
            if (chosen.Count == 0)
            {
                typesHint.Text = "Pick at least one file type.";
                typesHint.Tag = "err";
                return;
            }
            if (S.Job != null)
            {
                Ui.Toast(NoticeKind.Warn, "DriveSync is busy", $"Wait for “{S.Job.Label}” to finish.");
                return;
            }
            frame.Close();
            Actions.AddFolder(r.Path, displayName.Text, subToggle.IsChecked == true, chosen, r.Merges);
        }

        path.TextChanged += (_, _) => Check(path.Text.Length > 3);
        path.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        displayName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        foreach (var (box, _) in boxes) box.Checked += (_, _) => { typesHint.Text = ""; typesHint.Tag = null; };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for DriveSync to index" };
            if (Directory.Exists(path.Text)) dialog.InitialDirectory = path.Text;
            if (dialog.ShowDialog(App.Host) == true)
            {
                path.Text = dialog.FolderName;
                Check(true);
            }
        };
        go.Click += (_, _) => Submit();
        if (prefill.Length > 0) Check(true);
        frame.Show(label: "Add a folder");
    }

    private static TextBlock Label(string text, double top = 0)
    {
        var t = Build.Text(text, size: 12.5, brush: "Text2Brush", weight: FontWeights.Medium, margin: new Thickness(0, top, 0, 6));
        return t;
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0) };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Hint");
        return t;
    }
}
