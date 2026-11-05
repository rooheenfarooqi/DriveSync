using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

/// <summary>
/// Live activity (design page 27): every change the real FileSystemWatchers pick up, newest first, with
/// filters, the Watch for changes switch, Refresh now, index health and the 24-hour chart.
/// </summary>
public sealed class ActivityPage : UserControl, IPage, IActivityListener
{
    private readonly StackPanel _view = new() { Margin = new Thickness(32, 24, 32, 32) };
    private readonly ToggleSwitch _watch = new();
    private readonly Grid _grid = new();
    private readonly StackPanel _events = new();
    private readonly TextBlock _count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _kpis = new();
    private readonly HourlyBars _chart = new();
    private readonly StackPanel _right = new();
    private readonly List<RadioButton> _filterButtons = new();
    private ChangeKind? _filter;
    private bool _settingToggle;
    private TextBlock? _nextRefresh;
    private LayoutBand _band = LayoutBand.Wide;

    private static IndexService S => App.Service;

    public ActivityPage()
    {
        Content = new ScrollViewer { Content = _view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Build.Text("Live activity", style: "H1"));
        titles.Children.Add(Build.Text("Every change DriveSync picks up in your watched folders, newest first.", style: "Muted", margin: new Thickness(0, 4, 0, 0)));
        head.Children.Add(titles);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Build.Text("Watch for changes", size: 12.5, brush: "Text2Brush", margin: new Thickness(0, 0, 10, 0)));
        System.Windows.Automation.AutomationProperties.SetName(_watch, "Watch for changes");
        _watch.Margin = new Thickness(0, 0, 16, 0);
        _watch.Checked += (_, _) => { if (!_settingToggle && !S.Watching) App.Host.ToggleWatching(); };
        _watch.Unchecked += (_, _) => { if (!_settingToggle && S.Watching) App.Host.ToggleWatching(); };
        right.Children.Add(_watch);
        right.Children.Add(Build.Button("Refresh now", "i-refresh", ButtonVariant.Primary, onClick: App.Host.RefreshNow));
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        _view.Children.Add(head);

        // Event stream card
        var card = new Border();
        card.SetResourceReference(StyleProperty, "CardFlush");
        var cardStack = new StackPanel();
        var toolbar = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
        DockPanel.SetDock(_count, Dock.Right);
        toolbar.Children.Add(_count);
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label) in new (ChangeKind?, string)[] { (null, "All"), (ChangeKind.Added, "Added"), (ChangeKind.Modified, "Modified"), (ChangeKind.Renamed, "Renamed"), (ChangeKind.Removed, "Removed") })
        {
            var b = new RadioButton { Content = label, GroupName = "activity-filter", IsChecked = kind == null };
            b.SetResourceReference(StyleProperty, "SegButton");
            ChangeKind? k = kind;
            b.Checked += (_, _) => { _filter = k; RenderEvents(); };
            _filterButtons.Add(b);
            seg.Children.Add(b);
        }
        var segBox = new Border { Child = seg };
        segBox.SetResourceReference(StyleProperty, "SegGroup");
        System.Windows.Automation.AutomationProperties.SetName(segBox, "Filter events");
        toolbar.Children.Add(segBox);
        var toolbarBorder = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
        toolbarBorder.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        cardStack.Children.Add(toolbarBorder);
        cardStack.Children.Add(_events);
        card.Child = cardStack;

        // Right column: index health + chart
        var health = new Border();
        health.SetResourceReference(StyleProperty, "Card");
        var hs = new StackPanel();
        hs.Children.Add(SectionTitle("i-db", "Index health"));
        hs.Children.Add(_kpis);
        health.Child = hs;
        _right.Children.Add(health);
        var chartCard = new Border { Margin = new Thickness(0, 16, 0, 0) };
        chartCard.SetResourceReference(StyleProperty, "Card");
        var cs = new StackPanel();
        cs.Children.Add(SectionTitle("i-pulse", "Changes, last 24 hours"));
        cs.Children.Add(_chart);
        var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        axis.Children.Add(Build.Text("24 h ago", size: 11, brush: "Text3Brush"));
        var mid = Build.Text("12 h", size: 11, brush: "Text3Brush"); mid.HorizontalAlignment = HorizontalAlignment.Center; axis.Children.Add(mid);
        var now = Build.Text("Now", size: 11, brush: "Text3Brush"); now.HorizontalAlignment = HorizontalAlignment.Right; axis.Children.Add(now);
        cs.Children.Add(axis);
        chartCard.Child = cs;
        _right.Children.Add(chartCard);

        _grid.ColumnDefinitions.Add(new ColumnDefinition());
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.Children.Add(card);
        Grid.SetColumn(_right, 2);
        _grid.Children.Add(_right);
        _view.Children.Add(_grid);
    }

    private static StackPanel SectionTitle(string icon, string text)
    {
        var t = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        t.Children.Add(new Icon { Kind = icon, Size = 16, VerticalAlignment = VerticalAlignment.Center });
        t.Children.Add(Build.Text(text, style: "SectionTitle", margin: new Thickness(8, 0, 0, 0)));
        return t;
    }

    public void OnShown() => Render();
    public void OnDataChanged() { if (IsVisible) RenderHealth(); }

    public void OnTick()
    {
        if (!IsVisible) return;
        if (_nextRefresh != null) _nextRefresh.Text = NextShort();
        _settingToggle = true;
        _watch.IsChecked = S.Watching;
        _settingToggle = false;
    }

    public void OnBand(LayoutBand band)
    {
        _band = band;
        bool stack = band != LayoutBand.Wide;
        _grid.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : 16);
        _grid.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(320);
        Grid.SetColumn(_right, stack ? 0 : 2);
        Grid.SetRow(_right, stack ? 1 : 0);
        _right.Margin = new Thickness(0, stack ? 16 : 0, 0, 0);
        _view.Margin = band == LayoutBand.Tiny ? new Thickness(16, 16, 16, 24) : new Thickness(32, 24, 32, 32);
        if (IsVisible) RenderEvents();
    }

    public void OnActivity(ActivityEvent ev)
    {
        if (!IsVisible) return;
        if (_filter == null || ev.Kind == _filter)
        {
            if (_events.Children.Count > 0 && _events.Children[0] is EmptyState) _events.Children.Clear();
            FrameworkElement row = EventRow(ev, fresh: true);
            _events.Children.Insert(0, row);
            while (_events.Children.Count > 80) _events.Children.RemoveAt(_events.Children.Count - 1);
            UpdateCount();
        }
        RenderHealth();
    }

    private static string NextShort() =>
        S.Job != null ? "Running" : S.NextRefresh == DateTime.MaxValue ? "—" : Fmt.Countdown(S.NextRefresh - DateTime.Now);

    private void Render()
    {
        _settingToggle = true;
        _watch.IsChecked = S.Watching;
        _settingToggle = false;
        RenderEvents();
        RenderHealth();
    }

    private List<ActivityEvent> Filtered() => S.Events.Where(e => _filter == null || e.Kind == _filter).ToList();

    private void UpdateCount()
    {
        int n = Filtered().Count;
        _count.Text = $"{Fmt.Num(n)} event{(n == 1 ? "" : "s")}";
    }

    private void RenderEvents()
    {
        _events.Children.Clear();
        var list = Filtered();
        UpdateCount();
        if (list.Count == 0)
        {
            string kind = _filter == null ? "" : _filter.ToString()!.ToLowerInvariant() + " ";
            _events.Children.Add(new EmptyState
            {
                Icon = "i-pulse", MinHeight = 220,
                Title = $"No {kind}events yet",
                Message = S.Watching ? "DriveSync is listening. Changes appear here within a second." : "Watching is paused. Turn it back on to track changes.",
            });
            return;
        }
        foreach (ActivityEvent ev in list.Take(80))
            _events.Children.Add(EventRow(ev, fresh: false));
        if (_events.Children[^1] is Border lastRow) lastRow.BorderThickness = new Thickness(0);
    }

    private FrameworkElement EventRow(ActivityEvent ev, bool fresh)
    {
        bool tiny = _band == LayoutBand.Tiny;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tiny ? 64 : 76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = tiny ? new GridLength(0) : new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Build.Mono(Fmt.Time(ev.At), 11.5));
        if (!tiny)
        {
            Pill pill = Build.EventPill(ev.Kind);
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);
        }
        var path = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), ToolTip = ev.Path };
        path.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
        path.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        if (ev.Kind == ChangeKind.Renamed && ev.OldPath != null)
        {
            var from = new System.Windows.Documents.Run(Path.GetFileName(ev.OldPath) + " → ");
            from.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text3Brush");
            path.Inlines.Add(from);
        }
        var name = new System.Windows.Documents.Run(ev.Name) { FontWeight = FontWeights.Medium };
        name.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
        path.Inlines.Add(name);
        var where = new System.Windows.Documents.Run($" in {S.FolderOf(ev.RootId)?.Name ?? ev.RootName}");
        where.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text3Brush");
        path.Inlines.Add(where);
        Grid.SetColumn(path, 2);
        grid.Children.Add(path);
        if (!tiny)
        {
            TextBlock size = Build.Text(ev.IsFolder ? "Folder" : Fmt.Size(ev.Size), size: 11.5, brush: "Text3Brush");
            Grid.SetColumn(size, 3);
            grid.Children.Add(size);
        }
        var row = new Border { Child = grid, Padding = new Thickness(14, 9, 14, 9), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        row.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        if (fresh && Motion.Enabled)
        {
            // The design's evin: start tinted and shifted, settle in 600 ms.
            var tint = new SolidColorBrush(((SolidColorBrush)ThemeManager.Res("AccentSoftBrush")).Color);
            row.Background = tint;
            tint.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(600)));
            Motion.Enter(row, dy: 0, ms: 600);
            var shift = new TranslateTransform(-6, 0);
            row.RenderTransform = shift;
            Motion.To(shift, TranslateTransform.XProperty, 0, 600, -6);
        }
        return row;
    }

    private void RenderHealth()
    {
        _kpis.Children.Clear();
        JobInfo? job = S.Job;
        UIElement status = job != null ? Build.Pill("acc", job.Label)
            : S.IsCleared ? Build.Pill("warn", "Index is empty")
            : S.Watching && !S.BatteryPaused ? Build.Pill("ok", "Watching")
            : Build.Pill("warn", S.BatteryPaused ? "Paused · battery saver" : "Paused");
        _kpis.Children.Add(Build.Kpi("Status", status));
        _kpis.Children.Add(Build.Kpi("Files in index", Build.KpiValue(Fmt.Num(S.FileCount))));
        _kpis.Children.Add(Build.Kpi("Index size on disk", Build.KpiValue(Fmt.Size(S.DatabaseSize))));
        _kpis.Children.Add(Build.Kpi("Last index build", Build.KpiValue(S.LastBuildMs > 0 ? Fmt.Ms(S.LastBuildMs) : "—")));
        _kpis.Children.Add(Build.Kpi("Changes today", Build.KpiValue(Fmt.Num(S.ChangesToday))));
        _kpis.Children.Add(Build.Kpi("Last refresh", Build.KpiValue(S.LastRefresh == DateTime.MinValue ? "Not yet" : Fmt.Rel(S.LastRefresh))));
        _nextRefresh = Build.KpiValue(NextShort());
        _kpis.Children.Add(Build.Kpi("Next scheduled refresh", _nextRefresh, last: true));
        _chart.SetValues(S.HourlyChanges());
    }
}
