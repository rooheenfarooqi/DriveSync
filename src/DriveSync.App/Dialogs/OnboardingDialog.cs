using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Dialogs;

/// <summary>
/// First-run setup (design pages 33–36): Welcome → Choose folders → Build index (the four pipeline stages
/// with live progress) → Ready. Shown on first launch and from Settings → About → Run setup.
/// </summary>
public sealed class OnboardingDialog
{
    private static readonly string[] Steps = { "Welcome", "Choose folders", "Build index", "Ready" };
    private const string AllDrives = "*all-drives*";

    private readonly bool _firstRun;
    private readonly Grid _root = new();
    private readonly StackPanel _side = new();
    private readonly Border _main = new() { Padding = new Thickness(32, 24, 32, 24) };
    private readonly List<(string Path, string Name, bool Indexed)> _options = new();
    private readonly HashSet<string> _picks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DialogHandle? _handle;
    private int _step;
    private Task<JobOutcome>? _job;
    private bool _horizontal;

    private static IndexService S => App.Service;

    private OnboardingDialog(bool firstRun)
    {
        _firstRun = firstRun;
        _root.MinHeight = 440;
        _horizontal = App.Host.Band is LayoutBand.Narrow or LayoutBand.Tiny;
        if (_horizontal)
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition());
        }
        else
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            _root.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var side = new Border
        {
            Padding = _horizontal ? new Thickness(12) : new Thickness(24),
            BorderThickness = _horizontal ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0),
            CornerRadius = _horizontal ? new CornerRadius(16, 16, 0, 0) : new CornerRadius(16, 0, 0, 16),
            Child = _side,
        };
        side.SetResourceReference(Border.BackgroundProperty, "SideBrush");
        side.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        _root.Children.Add(side);
        if (_horizontal) Grid.SetRow(_main, 1); else Grid.SetColumn(_main, 1);
        _root.Children.Add(_main);

        // Folder choices: All drives, what's indexed already, then common folders.
        foreach (IndexedFolder f in S.Folders.Where(f => f.Status != RootStatus.Unreachable))
            _options.Add((f.Path, f.Name, true));
        foreach (var (name, path) in AddFolderDialog.QuickPicks())
            if (!_options.Any(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _options.Add((path, name, false));

        bool allDrivesIndexed = FileScanner.GetAllDriveRoots().All(r => S.Folders.Any(f => f.Path.Equals(r, StringComparison.OrdinalIgnoreCase)));
        if (allDrivesIndexed && S.Folders.Count > 0) _picks.Add(AllDrives);
        else if (S.Folders.Count > 0) foreach (IndexedFolder f in S.Folders) _picks.Add(f.Path);
        else if (S.HadOldIndex) _picks.Add(AllDrives); // DriveSync 1 indexed every drive; keep that by default
        else foreach (var (_, path) in AddFolderDialog.QuickPicks().Where(q => q.Name is "Desktop" or "Documents" or "Downloads")) _picks.Add(path);

        _poll.Tick += (_, _) => UpdateBuild();
    }

    public static void Show(bool firstRun)
    {
        var dialog = new OnboardingDialog(firstRun);
        dialog.Draw();
        dialog._handle = Ui.Dialog(dialog._root, wide: true, "Set up DriveSync");
        dialog._handle.Closed += dialog.OnClosed;
    }

    private void OnClosed()
    {
        _poll.Stop();
        if (_firstRun && !S.Settings.Onboarded) S.UpdateSettings(s => s.Onboarded = true);
        App.Host.UpdateChrome();
    }

    // ==================================================================

    private void Draw()
    {
        DrawSide();
        _main.Child = _step switch
        {
            0 => Welcome(),
            1 => ChooseFolders(),
            2 => BuildIndex(),
            _ => Ready(),
        };
        Motion.Enter(_main, dy: 6, ms: 220);
        _main.Dispatcher.BeginInvoke(() =>
        {
            if (FindTagged(_main) is UIElement f) f.Focus();
        }, DispatcherPriority.Input);
    }

    private static UIElement? FindTagged(DependencyObject root)
    {
        if (root is FrameworkElement fe && Equals(fe.Tag, "autofocus") && fe.IsVisible) return fe;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindTagged(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void DrawSide()
    {
        _side.Children.Clear();
        _side.Orientation = _horizontal ? Orientation.Horizontal : Orientation.Vertical;
        if (!_horizontal)
        {
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            brand.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Logo.png")), Width = 28, Height = 28 });
            var name = Build.Text("DriveSync", size: 15, weight: FontWeights.SemiBold, margin: new Thickness(10, 0, 0, 0));
            name.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            brand.Children.Add(name);
            _side.Children.Add(brand);
        }
        for (int i = 0; i < Steps.Length; i++)
        {
            bool on = i == _step, done = i < _step;
            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            badge.SetResourceReference(Border.BorderBrushProperty, on || done ? "AccentBrush" : "Stroke2Brush");
            if (done)
            {
                badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                var check = new Icon { Kind = "i-check", Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                check.SetResourceReference(Icon.ForegroundProperty, "TextOnAccentBrush");
                badge.Child = check;
            }
            else
            {
                TextBlock n = Build.Mono((i + 1).ToString(), 11, on ? "AccentBrush" : "Text3Brush", trim: false);
                n.FontWeight = FontWeights.SemiBold;
                n.HorizontalAlignment = HorizontalAlignment.Center;
                badge.Child = n;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = _horizontal ? new Thickness(6, 0, 6, 0) : new Thickness(6, 8, 6, 8) };
            row.Children.Add(badge);
            if (!_horizontal)
                row.Children.Add(Build.Text(Steps[i], size: 13, brush: on ? "TextBrush" : done ? "Text2Brush" : "Text3Brush", margin: new Thickness(12, 0, 0, 0)));
            _side.Children.Add(row);
        }
    }

    private static TextBlock Eyebrow(string text) => Build.Text(text, style: "Eyebrow");

    private static TextBlock Title(string text, double size = 22)
    {
        var t = Build.Text(text, size: size, weight: FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 8));
        t.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static DockPanel Footer(params UIElement[] buttons)
    {
        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 20, 0, 0) };
        foreach (UIElement b in buttons) dock.Children.Add(b);
        return dock;
    }

    private static FrameworkElement Layout(UIElement content, UIElement footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(content);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        return grid;
    }

    // ---- Step 1: Welcome ----
    private FrameworkElement Welcome()
    {
        var content = new StackPanel();
        content.Children.Add(Eyebrow("WELCOME"));
        content.Children.Add(Title("Find any file in a blink", 26));
        var intro = Build.Text("DriveSync keeps a live index of the folders you choose. Type part of a name and the file is there, with a short summary so you know it's the right one before you open it.",
            style: "Muted", margin: new Thickness(0, 0, 0, 20));
        intro.MaxWidth = 460;
        intro.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(intro);
        foreach (var (icon, title, sub) in new[]
        {
            ("i-bolt", "Instant name search", "Lookups finish in well under a millisecond."),
            ("i-sparkle", "Summaries on demand", "Key sentences and an outline, made on this PC."),
            ("i-shield", "Stays offline", "Nothing about your files leaves your computer."),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(Build.AccentGlyph(icon));
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Build.Text(title, weight: FontWeights.SemiBold));
            text.Children.Add(Build.Text(sub, size: 12.5, brush: "Text3Brush"));
            row.Children.Add(text);
            content.Children.Add(row);
        }
        UiButton skip = Build.Button("Skip setup", onClick: () => _handle?.Close());
        UiButton next = Build.Button("Get started", variant: ButtonVariant.Primary, onClick: () => { _step = 1; Draw(); });
        next.Tag = "autofocus";
        next.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(skip, Dock.Right);
        return Layout(content, Footer(next, skip));
    }

    // ---- Step 2: Choose folders ----
    private FrameworkElement ChooseFolders()
    {
        var content = new StackPanel();
        content.Children.Add(Eyebrow("STEP 2 OF 4"));
        content.Children.Add(Title("Which folders should DriveSync watch?"));
        content.Children.Add(Build.Text("Pick the places you search most. You can change this any time in Indexed folders.", style: "Muted"));

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        UiButton next = Build.Button("", variant: ButtonVariant.Primary, onClick: StartIndexing);
        TextBlock hint = new() { Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Hint");

        void Refresh()
        {
            int drives = FileScanner.GetAllDriveRoots().Count;
            int count = _picks.Contains(AllDrives) ? drives + _picks.Count(p => p != AllDrives && !IsOnSomeDrive(p)) : _picks.Count;
            next.Content = _picks.Contains(AllDrives) ? $"Index all {drives} drive{(drives == 1 ? "" : "s")}" : $"Index {count} folder{(count == 1 ? "" : "s")}";
            next.IsEnabled = _picks.Count > 0;
            hint.Text = _picks.Count > 0 ? "" : "Pick at least one folder to continue.";
            hint.Tag = _picks.Count > 0 ? null : "err";
        }

        CheckBox Option(string key, string name, string detail, bool indexed)
        {
            var label = new Grid();
            label.ColumnDefinitions.Add(new ColumnDefinition());
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(Build.Text(name, weight: FontWeights.Medium));
            TextBlock path = Build.Mono(detail, 11, trim: false);
            path.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(path);
            label.Children.Add(text);
            if (indexed)
            {
                Pill pill = Build.Pill("ok", "Indexed");
                pill.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(pill, 1);
                label.Children.Add(pill);
            }
            var box = new CheckBox { Content = label, IsChecked = _picks.Contains(key) };
            box.SetResourceReference(FrameworkElement.StyleProperty, "CheckRow");
            box.Checked += (_, _) => { _picks.Add(key); Refresh(); };
            box.Unchecked += (_, _) => { _picks.Remove(key); Refresh(); };
            return box;
        }

        list.Children.Add(Option(AllDrives, "All drives", string.Join("  ", FileScanner.GetAllDriveRoots()), false));
        foreach (var (path, name, indexed) in _options)
            list.Children.Add(Option(path, name, path, indexed));
        content.Children.Add(list);

        var add = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = new TextBox();
        input.SetResourceReference(FrameworkElement.StyleProperty, "InputMono");
        Field.SetPlaceholder(input, @"Add another path, like D:\Work");
        System.Windows.Automation.AutomationProperties.SetName(input, "Add another folder path");
        add.Children.Add(input);
        UiButton addButton = Build.Button("Add");
        addButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(addButton, 1);
        add.Children.Add(addButton);
        UiButton browse = Build.Button("Browse…", "i-folder-open");
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 2);
        add.Children.Add(browse);
        content.Children.Add(add);
        content.Children.Add(hint);

        void AddPath(string text)
        {
            FolderRules.Result r = FolderRules.Validate(text, Array.Empty<IndexedFolder>());
            if (r.Error != null) { hint.Text = r.Error; hint.Tag = "err"; Field.SetIsInvalid(input, true); return; }
            Field.SetIsInvalid(input, false);
            string p = r.Path!;
            if (!_options.Any(o => o.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                _options.Add((p, FolderRules.DefaultName(p), false));
            _picks.Add(p);
            Draw();
        }
        addButton.Click += (_, _) => AddPath(input.Text);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddPath(input.Text); e.Handled = true; } };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for DriveSync to watch" };
            if (dialog.ShowDialog(App.Host) == true) AddPath(dialog.FolderName);
        };

        UiButton back = Build.Button("Back", onClick: () => { _step = 0; Draw(); });
        DockPanel.SetDock(back, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        next.Tag = "autofocus";
        Refresh();
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, MaxHeight = Math.Max(260, App.Host.ActualHeight - 220) };
        return Layout(scroll, Footer(back, next));
    }

    private static bool IsOnSomeDrive(string path) =>
        FileScanner.GetAllDriveRoots().Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    private void StartIndexing()
    {
        var picks = new List<FolderChoice>();
        if (_picks.Contains(AllDrives))
            picks.AddRange(FileScanner.GetAllDriveRoots().Select(r => new FolderChoice(r, FolderRules.DefaultName(r))));
        foreach (string p in _picks.Where(p => p != AllDrives))
        {
            if (picks.Any(x => ExclusionRules.IsSameOrInside(p, x.Path))) continue;
            string name = _options.FirstOrDefault(o => o.Path.Equals(p, StringComparison.OrdinalIgnoreCase)).Name ?? FolderRules.DefaultName(p);
            picks.Add(new FolderChoice(p, name));
        }
        _job = S.TryRunSetup(picks);
        if (_job == null)
        {
            Ui.Toast(NoticeKind.Warn, "DriveSync is busy", "Wait for the current job to finish, then try again.");
            return;
        }
        S.UpdateSettings(s => s.Onboarded = true);
        _step = 2;
        Draw();
        _poll.Start();
        _job.ContinueWith(t => _main.Dispatcher.BeginInvoke(async () =>
        {
            _poll.Stop();
            UpdateBuild(finished: true);
            await Task.Delay(450);
            if (_handle?.IsOpen == true && _step == 2) { _step = 3; Draw(); }
            App.Host.SearchPage.AfterIndexEdit();
        }), TaskScheduler.Default);
    }

    // ---- Step 3: Build index ----
    private readonly List<(Border Circle, TextBlock Right)> _stages = new();
    private ProgressLine? _bar;
    private TextBlock? _count;

    private FrameworkElement BuildIndex()
    {
        var content = new StackPanel();
        content.Children.Add(Eyebrow("STEP 3 OF 4"));
        content.Children.Add(Title("Building your index"));
        content.Children.Add(Build.Text("You can close this window. DriveSync finishes in the background.", style: "Muted"));
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _stages.Clear();
        foreach (var (title, sub) in new[]
        {
            ("Select", "Reading the folders you chose"), ("Index", "Turning names and paths into search tokens"),
            ("Parse", "Saving metadata to the local database"), ("Search", "Warming up the lookup engine"),
        })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14) };
            row.Children.Add(circle);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Build.Text(title, weight: FontWeights.SemiBold));
            text.Children.Add(Build.Text(sub, size: 12, brush: "Text3Brush"));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            TextBlock right = Build.Text("", size: 12, brush: "Text3Brush");
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            _stages.Add((circle, right));
            list.Children.Add(row);
        }
        content.Children.Add(list);
        _bar = new ProgressLine { Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(_bar);
        _count = Build.Text("Starting…", size: 12, brush: "Text3Brush", margin: new Thickness(0, 6, 0, 0));
        content.Children.Add(_count);

        UiButton bg = Build.Button("Continue in background", onClick: () =>
        {
            _handle?.Close();
            Ui.Toast(NoticeKind.Info, "Indexing continues in the background", "You can search while DriveSync finishes.");
        });
        DockPanel.SetDock(bg, Dock.Right);
        bg.Tag = "autofocus";
        UpdateBuild();
        return Layout(content, Footer(bg));
    }

    private void UpdateBuild(bool finished = false)
    {
        if (_bar == null || _count == null) return;
        JobInfo? job = S.Job;
        double progress = finished ? 1 : job?.Progress ?? 0;
        int stage = finished ? 4 : job != null ? (int)job.Stage : 0;
        _bar.Value = progress;
        (double from, double to)[] ranges = { (0, 0.6), (0.6, 0.8), (0.8, 0.95), (0.95, 1) };
        for (int i = 0; i < _stages.Count; i++)
        {
            var (circle, right) = _stages[i];
            string state = i < stage ? "ok" : i == stage ? "run" : "";
            if ((string?)circle.Tag != state)
            {
                circle.Tag = state;
                var icon = new Icon
                {
                    Kind = state == "ok" ? "i-check" : state == "run" ? "i-refresh" : "i-clock",
                    Size = 14, Spin = state == "run", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                icon.SetResourceReference(Icon.ForegroundProperty, state == "ok" ? "TextOnAccentBrush" : state == "run" ? "AccentBrush" : "Text3Brush");
                circle.SetResourceReference(Border.BackgroundProperty, state == "ok" ? "AccentBrush" : state == "run" ? "AccentSoftBrush" : "Layer2Brush");
                circle.Child = icon;
            }
            right.Text = state == "ok" ? "Done" : state == "run" ? $"{Math.Clamp((int)Math.Round((progress - ranges[i].from) / (ranges[i].to - ranges[i].from) * 100), 0, 99)}%" : "";
        }
        long seen = job?.FilesSeen ?? 0;
        _count.Text = finished ? $"{Fmt.Num(S.FileCount)} of {Fmt.Num(S.FileCount)} files"
            : stage == 0 ? (seen > 0 ? $"{Fmt.Num(seen)} files found so far" : "Starting…")
            : $"{Fmt.Num(Math.Min(seen, (long)(seen * progress / 0.95)))} of {Fmt.Num(seen)} files";
    }

    // ---- Step 4: Ready ----
    private FrameworkElement Ready()
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        FileGlyph check = Build.AccentGlyph("i-check", "OkBrush", large: true);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(check);
        content.Children.Add(Title("You're all set", 26));
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        text.Inlines.Add("DriveSync indexed ");
        var n = new System.Windows.Documents.Run(Fmt.Num(S.FileCount)) { FontWeight = FontWeights.SemiBold };
        n.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
        text.Inlines.Add(n);
        int folders = S.Folders.Count;
        text.Inlines.Add($" files across {folders} folder{(folders == 1 ? "" : "s")} and is now watching for changes.");
        content.Children.Add(text);
        string hotkey = S.Settings.Hotkey;
        if (hotkey != "off")
        {
            var keys = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            keys.Children.Add(Build.Text("Press ", style: "Muted"));
            keys.Children.Add(Build.Keys(hotkey.Replace("+", " ")));
            keys.Children.Add(Build.Text("from anywhere to search.", style: "Muted"));
            content.Children.Add(keys);
        }
        UiButton review = Build.Button("Review folders", onClick: () => { _handle?.Close(); App.Host.Navigate("folders"); });
        UiButton start = Build.Button("Start searching", variant: ButtonVariant.Primary, onClick: () => { _handle?.Close(); App.Host.Navigate("search", focusSearch: true); });
        start.Tag = "autofocus";
        start.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(start, Dock.Right);
        DockPanel.SetDock(review, Dock.Right);
        return Layout(content, Footer(start, review));
    }
}
