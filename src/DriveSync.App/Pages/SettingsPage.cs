using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;
using DriveSync.Core.Parsing;

namespace DriveSync.App.Pages;

/// <summary>
/// Settings (design pages 29–32): General, Indexing, Search &amp; preview, Privacy &amp; data, Appearance and
/// About. Every change is saved to SQLite and applied at once.
/// </summary>
public sealed class SettingsPage : UserControl, IPage
{
    private static readonly (string Key, string Label, string Icon)[] Sections =
    {
        ("general", "General", "i-gear"), ("indexing", "Indexing", "i-db"), ("searchp", "Search & preview", "i-search"),
        ("privacy", "Privacy & data", "i-shield"), ("appearance", "Appearance", "i-palette"), ("about", "About", "i-info"),
    };

    private readonly Grid _layout = new();
    private readonly StackPanel _nav = new();
    private readonly ScrollViewer _navScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly StackPanel _body = new();
    private readonly Dictionary<string, FrameworkElement> _sectionHeads = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private TextBox? _maxParse;
    private string _current = "general";
    private bool _rendering;
    private LayoutBand _band = LayoutBand.Wide;

    private static IndexService S => App.Service;
    private static AppSettings Set => S.Settings;

    public SettingsPage()
    {
        var root = new Grid { Margin = new Thickness(32, 24, 32, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var head = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Build.Text("Settings", style: "H1"));
        titles.Children.Add(Build.Text("Changes apply right away.", style: "Muted", margin: new Thickness(0, 4, 0, 0)));
        head.Children.Add(titles);
        UiButton restore = Build.Button("Restore defaults", onClick: RestoreDefaults);
        restore.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(restore, 1);
        head.Children.Add(restore);
        root.Children.Add(head);

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition());
        foreach (var (key, label, icon) in Sections)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Icon { Kind = icon, Size = 16, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(Build.Text(label, margin: new Thickness(12, 0, 0, 0)));
            var b = new Button { Content = content };
            b.SetResourceReference(StyleProperty, "SectionNavButton");
            string k = key;
            b.Click += (_, _) => ScrollTo(k);
            _navButtons[key] = b;
            _nav.Children.Add(b);
        }
        _navScroll.Content = _nav;
        _layout.Children.Add(_navScroll);
        _scroll.Content = new Border { Child = _body, Padding = new Thickness(0, 0, 0, 32) };
        _scroll.ScrollChanged += (_, _) => TrackSection();
        Grid.SetColumn(_scroll, 2);
        Grid.SetRowSpan(_scroll, 2);
        _layout.Children.Add(_scroll);
        Grid.SetRow(_layout, 1);
        root.Children.Add(_layout);
        Content = root;
    }

    public void OnShown() => Render();
    public void OnDataChanged() { if (IsVisible && !_rendering) UpdateLive(); }

    private UiButton? _clearIndexButton;
    private UiButton? _clearHistoryButton;
    public void OnTick() { }

    public void OnBand(LayoutBand band)
    {
        _band = band;
        bool narrow = band is LayoutBand.Narrow or LayoutBand.Tiny;
        _nav.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        _navScroll.HorizontalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _layout.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(200);
        _layout.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(24);
        Grid.SetColumn(_navScroll, narrow ? 2 : 0);
        Grid.SetRow(_navScroll, 0);
        Grid.SetRow(_scroll, narrow ? 1 : 0);
        Grid.SetRowSpan(_scroll, narrow ? 1 : 2);
        _navScroll.Margin = narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0);
        if (Content is Grid root) root.Margin = band == LayoutBand.Tiny ? new Thickness(16, 16, 16, 0) : new Thickness(32, 24, 32, 0);
    }

    /// <summary>"Change limit" from the preview: scroll to Largest file to summarize and focus it.</summary>
    public void FocusMaxParse()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_maxParse == null) return;
            _maxParse.BringIntoView();
            _maxParse.Focus();
            _maxParse.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ==================================================================

    private void ScrollTo(string key)
    {
        _current = key;
        UpdateNav();
        if (_sectionHeads.TryGetValue(key, out FrameworkElement? head))
        {
            double y = head.TranslatePoint(new Point(0, 0), _body).Y;
            if (Motion.Enabled) AnimateScroll(_scroll.VerticalOffset, y - 8);
            else _scroll.ScrollToVerticalOffset(y - 8);
        }
    }

    private void AnimateScroll(double from, double to)
    {
        var start = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 280);
            double eased = Motion.Ease.Ease(t);
            _scroll.ScrollToVerticalOffset(from + (to - from) * eased);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void TrackSection()
    {
        string current = Sections[0].Key;
        foreach (var (key, _, _) in Sections)
        {
            if (!_sectionHeads.TryGetValue(key, out FrameworkElement? head)) continue;
            double y = head.TranslatePoint(new Point(0, 0), _scroll).Y;
            if (y < 120) current = key;
        }
        if (_scroll.VerticalOffset >= _scroll.ScrollableHeight - 2) current = Sections[^1].Key;
        if (current != _current) { _current = current; UpdateNav(); }
    }

    private void UpdateNav()
    {
        foreach (var (key, b) in _navButtons) b.Tag = key == _current ? "on" : null;
    }

    private void Render()
    {
        _rendering = true;
        double offset = _scroll.VerticalOffset;
        _body.Children.Clear();
        _sectionHeads.Clear();

        // ---- General ----
        Section("general", "General",
            Row("i-rocket", "Start with Windows", "DriveSync starts quietly in the background when you sign in.",
                Toggle("Start with Windows", Set.StartWithWindows, on =>
                {
                    S.UpdateSettings(s => s.StartWithWindows = on);
                    if (App.SandboxFolder == null) StartupRegistration.Apply(on);
                })),
            Row("i-x", "Keep running when closed", "Closing the window leaves DriveSync in the system tray so the index stays current.",
                Toggle("Keep running when closed", Set.KeepRunningWhenClosed, on =>
                {
                    S.UpdateSettings(s => s.KeepRunningWhenClosed = on);
                    Ui.Toast(NoticeKind.Info, on ? "DriveSync stays in the tray" : "Closing quits DriveSync",
                        on ? "Closing the window keeps the index up to date." : "The index won't update while DriveSync is closed.", ms: 3000);
                })),
            Row("i-keyboard", "Open search from anywhere", "Shortcut that brings DriveSync to the front with the search box ready.",
                Dropdown("", new[] { ("Alt+Space", "Alt + Space"), ("Ctrl+Shift+F", "Ctrl + Shift + F"), ("Win+Alt+S", "Win + Alt + S"), ("off", "Off") }, Set.Hotkey, v =>
                {
                    S.UpdateSettings(s => s.Hotkey = (string)v);
                    App.Host.ApplyHotkey((string)v, announce: true);
                })));

        // ---- Indexing ----
        var maxParseRow = MaxParseRow();
        Section("indexing", "Indexing",
            Row("i-bolt", "Detect changes instantly", "Update the index as soon as files are added, edited, renamed or deleted.",
                Toggle("Detect changes instantly", Set.DetectChangesInstantly, on =>
                {
                    S.UpdateSettings(s => s.DetectChangesInstantly = on);
                    Ui.Toast(NoticeKind.Info, on ? "Instant updates on" : "Instant updates off",
                        on ? "Changes appear in results within a second." : $"The index refreshes every {RefreshLabel(Set.RefreshMinutes)} instead.", ms: 2800);
                })),
            Row("i-clock", "Scheduled refresh", "Reconciles the index with the disk in case a change was missed.",
                Dropdown("Every", new[] { (5, "5 minutes"), (15, "15 minutes"), (30, "30 minutes"), (60, "hour"), (240, "4 hours") }.Select(x => ((object)x.Item1, x.Item2)).ToArray(),
                    Set.RefreshMinutes, v => S.UpdateSettings(s => s.RefreshMinutes = (int)v))),
            maxParseRow,
            Row("i-eye-off", "Include hidden and system files", "Off by default. Turning it on adds temporary files such as ~$ Office lock files.",
                Toggle("Include hidden and system files", Set.IncludeHidden, async on =>
                {
                    S.UpdateSettings(s => s.IncludeHidden = on);
                    Task<int>? change = S.ApplyHiddenSetting(on);
                    if (change == null) return;
                    int d = await change;
                    App.Host.SearchPage.AfterIndexEdit();
                    Ui.Toast(NoticeKind.Ok, on ? "Hidden files included" : "Hidden files skipped",
                        $"{Fmt.Plural(Math.Abs(d), "file")} {(d >= 0 ? "added to" : "removed from")} the index.", ms: 2800);
                })),
            Row("i-cpu", "Pause indexing on battery saver", "Keeps DriveSync light when your laptop is saving power.",
                Toggle("Pause indexing on battery saver", Set.PauseOnBatterySaver, on => S.UpdateSettings(s => s.PauseOnBatterySaver = on))));

        // ---- Search & preview ----
        var clearHistory = Build.Button("Clear history", small: true);
        clearHistory.IsEnabled = S.History.Count > 0;
        _clearHistoryButton = clearHistory;
        clearHistory.Margin = new Thickness(10, 0, 0, 0);
        clearHistory.Click += (_, _) =>
        {
            S.ClearHistory();
            clearHistory.IsEnabled = false;
            Ui.Toast(NoticeKind.Ok, "Search history cleared", ms: 2200);
        };
        var historyCtl = new StackPanel { Orientation = Orientation.Horizontal };
        historyCtl.Children.Add(Toggle("Remember recent searches", Set.RememberHistory, on => S.UpdateSettings(s => s.RememberHistory = on)));
        historyCtl.Children.Add(clearHistory);
        Section("searchp", "Search & preview",
            Row("i-sparkle", "Summarize when a file is selected", "Turn off to summarize only when you press Summarize.",
                Toggle("Summarize when a file is selected", Set.SummarizeOnSelect, on => S.UpdateSettings(s => s.SummarizeOnSelect = on))),
            Row("i-text", "Summary length", "Number of key sentences in each summary.", SummaryLength()),
            Row("i-folder", "Show full paths in results", "Shows each file's folder path under its name.",
                Toggle("Show full paths in results", Set.ShowFullPaths, on => { S.UpdateSettings(s => s.ShowFullPaths = on); App.Host.SearchPage.AfterIndexEdit(); })),
            Row("i-outline", "Results per page", "More results load when you press Show more at the end of the list.",
                Dropdown("", new[] { (50, "50"), (100, "100"), (200, "200") }.Select(x => ((object)x.Item1, x.Item2)).ToArray(), Set.ResultsPerPage,
                    v => S.UpdateSettings(s => s.ResultsPerPage = (int)v))),
            Row("i-history", "Remember recent searches", "Shown on Home and in an empty search.", historyCtl));

        // ---- Privacy & data ----
        var location = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock path = Build.Mono(S.DatabasePath, 11.5, trim: false);
        path.TextWrapping = TextWrapping.Wrap;
        path.MaxWidth = 320;
        location.Children.Add(path);
        UiButton copyPath = Build.IconButton("i-copy", "Copy", () => Ui.Copy(S.DatabasePath, "Location"));
        System.Windows.Automation.AutomationProperties.SetName(copyPath, "Copy index location");
        copyPath.Margin = new Thickness(10, 0, 0, 0);
        location.Children.Add(copyPath);
        UiButton clear = Build.Button("Clear index", variant: ButtonVariant.Danger, onClick: Actions.ConfirmClear);
        clear.IsEnabled = !S.IsCleared;
        _clearIndexButton = clear;
        Section("privacy", "Privacy & data",
            Row("i-shield", "Works fully offline", "DriveSync never uploads file names, paths or contents. Summaries are made on this PC.",
                Build.Pill("ok", "Offline", "i-check")),
            Row("i-db", "Index location", "SQLite database with your folders, file names and settings.", location),
            Row("i-refresh", "Rebuild index", "Scans every folder again from scratch. Search keeps working while it runs.",
                Build.Button("Rebuild", onClick: () => Actions.Rebuild())),
            Row("i-trash", "Clear index", "Deletes the index and the summary cache. Your files are not touched.", clear));

        // ---- Appearance ----
        Section("appearance", "Appearance",
            Row("i-palette", "Accent color", "Used for selection, progress and highlights.", Swatches()),
            Row("i-image", "Transparency effects", ThemeManager.BackdropActive ? "How much of the desktop shows through the window." : "Mica isn't available on this version of Windows, so the window stays solid.", GlassSlider()),
            Row("i-bolt", "Reduce motion", "Turns off transitions and animated progress.",
                Toggle("Reduce motion", Set.ReduceMotion, on => { S.UpdateSettings(s => s.ReduceMotion = on); ThemeManager.SetReduceMotion(on); })),
            Row("i-outline", "Compact result rows", "Fits more results on screen.",
                Toggle("Compact result rows", Set.CompactRows, on => { S.UpdateSettings(s => s.CompactRows = on); ThemeManager.SetCompact(on); })));

        // ---- About ----
        var about = new StackPanel { Orientation = Orientation.Horizontal };
        about.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Logo.png")), Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) });
        var aboutText = new StackPanel();
        var name = Build.Text("DriveSync", size: 16, weight: FontWeights.SemiBold);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
        aboutText.Children.Add(name);
        string version = typeof(App).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}" : "0.9";
        aboutText.Children.Add(Build.Text($"Version {version} · Real-time desktop search engine for Windows", size: 12, brush: "Text3Brush"));
        var credits = Build.Text("Final Year Project, BS Computer Science, Institute of Space Technology, Islamabad.\nAyesha Riaz and Rooheen Ahmad Farooqi · Supervised by Mr. Faran Mehmood",
            size: 12, brush: "Text3Brush", margin: new Thickness(0, 6, 0, 0));
        credits.TextWrapping = TextWrapping.Wrap;
        aboutText.Children.Add(credits);
        about.Children.Add(aboutText);
        Section("about", "About",
            RowRaw(about),
            Row("i-rocket", "First-run setup", "Walk through choosing folders and building the index again.",
                Build.Button("Run setup", onClick: () => OnboardingDialog.Show(firstRun: false))),
            Row("i-keyboard", "Keyboard shortcuts", "Every shortcut DriveSync understands.",
                Build.Button("View shortcuts", onClick: ShortcutsDialog.Show)));

        UpdateNav();
        Dispatcher.BeginInvoke(() => { _scroll.ScrollToVerticalOffset(offset); _rendering = false; }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Keeps buttons that depend on live state (Clear index, Clear history) current without a full re-render.</summary>
    private void UpdateLive()
    {
        if (_clearIndexButton != null) _clearIndexButton.IsEnabled = !S.IsCleared;
        if (_clearHistoryButton != null) _clearHistoryButton.IsEnabled = S.History.Count > 0;
    }

    private static string RefreshLabel(int minutes) => minutes switch { 60 => "hour", 240 => "4 hours", _ => $"{minutes} minutes" };

    // ---- building blocks ----

    private void Section(string key, string title, params Border[] rows)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
        var heading = Build.Text(title, size: 14, weight: FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 8));
        group.Children.Add(heading);
        _sectionHeads[key] = heading;
        for (int i = 0; i < rows.Length; i++)
        {
            double top = i == 0 ? 12 : 8, bottom = i == rows.Length - 1 ? 12 : 8;
            rows[i].CornerRadius = new CornerRadius(top, top, bottom, bottom);
            rows[i].Margin = new Thickness(0, 0, 0, 3);
            group.Children.Add(rows[i]);
        }
        _body.Children.Add(group);
    }

    private static Border Row(string icon, string title, string description, UIElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = new Icon { Kind = icon, Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ic.SetResourceReference(Icon.ForegroundProperty, "Text2Brush");
        grid.Children.Add(ic);
        var text = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Build.Text(title, weight: FontWeights.Medium));
        var desc = Build.Text(description, size: 12, brush: "Text3Brush");
        desc.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(desc);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);
        return RowRaw(grid);
    }

    private static Border RowRaw(UIElement content)
    {
        var row = new Border { Child = content, Padding = new Thickness(16, 14, 16, 14), MinHeight = 66, BorderThickness = new Thickness(1) };
        row.SetResourceReference(Border.BackgroundProperty, "LayerBrush");
        row.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return row;
    }

    private static ToggleSwitch Toggle(string label, bool value, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsChecked = value };
        System.Windows.Automation.AutomationProperties.SetName(t, label);
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    private static DropDown Dropdown(string label, (object Value, string Label)[] options, object value, Action<object> changed)
    {
        var d = new DropDown { Label = label };
        d.SetOptions(options.Select(o => new DropOption(o.Value, o.Label)), value);
        d.Changed += changed;
        return d;
    }

    private static DropDown Dropdown(string label, (string Value, string Label)[] options, string value, Action<object> changed) =>
        Dropdown(label, options.Select(o => ((object)o.Value, o.Label)).ToArray(), value, changed);

    private Border MaxParseRow()
    {
        var input = new TextBox { Text = Set.MaxParseMb.ToString(), Width = 90, TextAlignment = TextAlignment.Right };
        input.SetResourceReference(StyleProperty, "Input");
        System.Windows.Automation.AutomationProperties.SetName(input, "Largest file to summarize, in megabytes");
        _maxParse = input;
        var hint = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(StyleProperty, "Hint");
        input.TextChanged += (_, _) =>
        {
            if (_rendering) return;
            if (!int.TryParse(input.Text.Trim(), out int v) || v < 1 || v > 500)
            {
                Field.SetIsInvalid(input, true);
                hint.Text = "Enter a whole number from 1 to 500.";
                hint.Tag = "err";
                return;
            }
            Field.SetIsInvalid(input, false);
            hint.Text = "Saved.";
            hint.Tag = "ok";
            S.UpdateSettings(s => s.MaxParseMb = v);
        };
        var ctl = new StackPanel { Orientation = Orientation.Horizontal };
        ctl.Children.Add(input);
        ctl.Children.Add(Build.Text("MB", brush: "Text2Brush", margin: new Thickness(10, 0, 0, 0)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = new Icon { Kind = "i-doc", Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ic.SetResourceReference(Icon.ForegroundProperty, "Text2Brush");
        grid.Children.Add(ic);
        var text = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Build.Text("Largest file to summarize", weight: FontWeights.Medium));
        var desc = Build.Text("Bigger files are still searchable by name. Between 1 and 500 MB.", size: 12, brush: "Text3Brush");
        desc.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(desc);
        text.Children.Add(hint);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        ctl.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(ctl, 2);
        grid.Children.Add(ctl);
        return RowRaw(grid);
    }

    private static Border SummaryLength()
    {
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (n, label) in new[] { (2, "Short"), (3, "Medium"), (5, "Long") })
        {
            var b = new RadioButton { Content = label, GroupName = "summary-length", IsChecked = Set.SummaryLength == n };
            b.SetResourceReference(StyleProperty, "SegButton");
            int value = n;
            b.Checked += (_, _) =>
            {
                if (Set.SummaryLength == value) return;
                S.UpdateSettings(s => s.SummaryLength = value);
                Ui.Toast(NoticeKind.Ok, $"Summaries now use {value} sentences", ms: 2200);
            };
            seg.Children.Add(b);
        }
        var box = new Border { Child = seg };
        box.SetResourceReference(StyleProperty, "SegGroup");
        System.Windows.Automation.AutomationProperties.SetName(box, "Summary length");
        return box;
    }

    private static StackPanel Swatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        System.Windows.Automation.AutomationProperties.SetName(row, "Accent color");
        foreach (var (hex, name) in ThemeManager.Accents)
        {
            var b = new RadioButton
            {
                GroupName = "accent",
                IsChecked = string.Equals(Set.Accent, hex, StringComparison.OrdinalIgnoreCase),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = name,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            b.SetResourceReference(StyleProperty, "Swatch");
            string value = hex;
            b.Checked += (_, _) =>
            {
                S.UpdateSettings(s => s.Accent = value);
                ThemeManager.SetAccent(value);
            };
            row.Children.Add(b);
        }
        return row;
    }

    private static StackPanel GlassSlider()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Build.Text("Clear", size: 12, brush: "Text3Brush", margin: new Thickness(0, 0, 10, 0)));
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round((Set.Glass - 0.55) / 0.42 * 100), IsEnabled = ThemeManager.BackdropActive };
        slider.SetResourceReference(StyleProperty, "UiSlider");
        System.Windows.Automation.AutomationProperties.SetName(slider, "Transparency effects");
        slider.ValueChanged += (_, e) => ThemeManager.SetGlass(0.55 + e.NewValue / 100 * 0.42);
        slider.PreviewMouseUp += (_, _) => S.UpdateSettings(s => s.Glass = Math.Round(0.55 + slider.Value / 100 * 0.42, 3));
        slider.KeyUp += (_, _) => S.UpdateSettings(s => s.Glass = Math.Round(0.55 + slider.Value / 100 * 0.42, 3));
        row.Children.Add(slider);
        row.Children.Add(Build.Text("Solid", size: 12, brush: "Text3Brush", margin: new Thickness(10, 0, 0, 0)));
        return row;
    }

    private void RestoreDefaults()
    {
        AppSettings defaults = Set.Defaults();
        S.ReplaceSettings(defaults);
        ThemeManager.Apply(Set);
        ThemeManager.SetGlass(Set.Glass);
        App.Host.ApplyHotkey(Set.Hotkey, announce: false);
        if (App.SandboxFolder == null) StartupRegistration.Apply(Set.StartWithWindows);
        PreviewService.ClearCache();
        Render();
        Ui.Toast(NoticeKind.Ok, "Settings restored to defaults", ms: 2400);
    }
}
