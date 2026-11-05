using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

public partial class HomePage : UserControl, IPage, IActivityListener
{
    private TextBlock? _nextRefresh;
    private TextBlock? _lastRefresh;

    public HomePage()
    {
        InitializeComponent();
    }

    private static IndexService S => App.Service;

    public void OnShown() => Render();
    public void OnDataChanged() { if (IsVisible) Render(); }
    public void OnActivity(ActivityEvent ev) { if (IsVisible) RenderRecent(); }

    public void OnTick()
    {
        if (!IsVisible) return;
        if (_nextRefresh != null) _nextRefresh.Text = NextRefreshText();
        if (_lastRefresh != null) _lastRefresh.Text = S.Job != null ? "Running…" : RelOrNever(S.LastRefresh);
        RenderPipeline();
    }

    public void OnBand(LayoutBand band)
    {
        bool stack = band != LayoutBand.Wide;
        HomeGrid.ColumnDefinitions[0].Width = new GridLength(stack ? 1 : 1.5, GridUnitType.Star);
        HomeGrid.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : 16);
        HomeGrid.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SideColumn, stack ? 0 : 2);
        Grid.SetRow(SideColumn, stack ? 1 : 0);
        SideColumn.Margin = new Thickness(0, stack ? 16 : 0, 0, 0);
        Stats.Columns = band is LayoutBand.Narrow or LayoutBand.Tiny ? 2 : 4;
        Pipeline.Columns = band == LayoutBand.Tiny ? 2 : 4;
        View.Margin = band == LayoutBand.Tiny ? new Thickness(16, 16, 16, 24) : new Thickness(32, 24, 32, 32);
    }

    private static string RelOrNever(DateTime t) => t == DateTime.MinValue ? "Not yet" : Fmt.Rel(t);

    private static string NextRefreshText()
    {
        if (S.Job != null) return "Index is updating";
        if (S.NextRefresh == DateTime.MaxValue) return "Scheduled refresh starts once the index has loaded";
        return "Next scheduled refresh in " + Fmt.Countdown(S.NextRefresh - DateTime.Now);
    }

    private void Render()
    {
        DateText.Text = Fmt.LongDay(DateTime.Now).ToUpperInvariant();
        GreetingText.Text = Build.Greeting();
        var folders = S.Folders;
        SubText.Text = !S.IsLoaded ? "Loading your index…"
            : S.IsCleared ? "Your index is empty. Rebuild it to start searching again."
            : folders.Count == 0 ? "Add a folder to start. Everything stays on this PC."
            : $"Search {Fmt.Num(S.FileCount)} files across {folders.Count} folder{(folders.Count == 1 ? "" : "s")}. Everything stays on this PC.";
        RefreshButton.IsEnabled = S.Job == null;
        RenderHistory();
        RenderStats();
        RenderRecent();
        RenderPipeline();
        RenderFolders();
    }

    private void RenderHistory()
    {
        HistoryRow.Children.Clear();
        var history = S.History;
        if (S.Settings.RememberHistory && history.Count > 0)
        {
            HistoryRow.Children.Add(Build.Text("Recent", size: 12, brush: "Text3Brush", margin: new Thickness(0, 0, 10, 8)));
            foreach (string h in history.Take(6))
                HistoryRow.Children.Add(Build.Chip(h, "i-history", () => GoSearch(h)));
        }
        else
        {
            var tip = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            tip.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
            tip.Inlines.Add("Tip: type ");
            tip.Inlines.Add(MonoAccent("ext:pdf"));
            tip.Inlines.Add(" or ");
            tip.Inlines.Add(MonoAccent("in:cloud"));
            tip.Inlines.Add(" to narrow results.");
            HistoryRow.Children.Add(tip);
        }
    }

    private static System.Windows.Documents.Run MonoAccent(string text)
    {
        var run = new System.Windows.Documents.Run(text);
        run.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "FontMono");
        run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentBrush");
        return run;
    }

    private void RenderStats()
    {
        Stats.Children.Clear();
        var folders = S.Folders;
        int active = folders.Count(f => f.Status != RootStatus.Unreachable);
        int bad = folders.Count - active;
        double median = S.MedianLookup;
        int lookups = S.Lookups.Count;

        Stats.Children.Add(Stat("i-doc", "Files indexed", Fmt.Num(S.FileCount), null,
            S.AddedThisSession > 0 ? $"+{Fmt.Num(S.AddedThisSession)} added this session" : "No new files this session"));
        TextBlock folderDetail;
        Stats.Children.Add(Stat("i-folder", "Watched folders", active.ToString(), $"of {folders.Count}",
            folders.Count == 0 ? "Add a folder to start" : bad > 0 ? $"{bad} drive{(bad == 1 ? "" : "s")} not connected" : "All folders reachable", out folderDetail));
        if (bad > 0) folderDetail.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        Border last = Stat("i-refresh", "Last refresh", S.Job != null ? "Running…" : RelOrNever(S.LastRefresh), null, NextRefreshText(), out _nextRefresh, smallValue: true);
        _lastRefresh = (TextBlock)((StackPanel)last.Child).Children[1];
        Stats.Children.Add(last);
        Stats.Children.Add(Stat("i-bolt", "Median lookup", median > 0 ? median.ToString("0.00") : "—", "ms",
            lookups > 0 ? $"Over your last {lookups} search{(lookups == 1 ? "" : "es")}" : "Run a search to measure"));
    }

    private static Border Stat(string icon, string key, string value, string? small, string detail) => Stat(icon, key, value, small, detail, out _);

    private static Border Stat(string icon, string key, string value, string? small, string detail, out TextBlock detailText, bool smallValue = false)
    {
        var k = new StackPanel { Orientation = Orientation.Horizontal };
        k.Children.Add(new Icon { Kind = icon, Size = 16, VerticalAlignment = VerticalAlignment.Center });
        k.Children.Add(Build.Text(key, size: 12.5, margin: new Thickness(8, 0, 0, 0)));
        k.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        k.SetResourceReference(Icon.ForegroundProperty, "Text2Brush");

        var v = new TextBlock { Margin = new Thickness(0, smallValue ? 13 : 10, 0, smallValue ? 3 : 0), TextTrimming = TextTrimming.CharacterEllipsis };
        v.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
        v.FontSize = smallValue ? 22 : 28;
        v.FontWeight = FontWeights.SemiBold;
        v.Inlines.Add(value);
        if (small != null)
        {
            var run = new System.Windows.Documents.Run(" " + small) { FontSize = 13, FontWeight = FontWeights.Medium };
            run.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "FontBody");
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text2Brush");
            v.Inlines.Add(run);
        }
        detailText = Build.Text(detail, size: 12, brush: "Text3Brush", trim: true, margin: new Thickness(0, 4, 0, 0));
        var card = new Border { Child = new StackPanel { Children = { k, v, detailText } }, Margin = new Thickness(6, 0, 6, 12) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    private void RenderRecent()
    {
        RecentList.Children.Clear();
        var events = S.Events.Take(7).ToList();
        if (events.Count == 0)
        {
            RecentList.Children.Add(new EmptyState
            {
                Icon = "",
                MinHeight = 160,
                Padding = new Thickness(24),
                Message = "No changes yet. New, edited and deleted files show up here as soon as DriveSync sees them.",
            });
            return;
        }
        foreach (ActivityEvent ev in events)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement glyph = ev.IsFolder ? Build.AccentGlyph("i-folder") : Build.Glyph(FileKinds.FromName(ev.Name));
            grid.Children.Add(glyph);
            var main = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            main.Children.Add(Build.Text(ev.Name, weight: FontWeights.Medium, trim: true));
            main.Children.Add(Build.Mono(Breadcrumb(ev), 12));
            Grid.SetColumn(main, 1);
            grid.Children.Add(main);
            Pill pill = Build.EventPill(ev.Kind);
            pill.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
            TextBlock when = Build.Text(Fmt.Rel(ev.At), size: 12, brush: "Text3Brush");
            Grid.SetColumn(when, 3);
            grid.Children.Add(when);

            var row = new Button { Content = grid, IsEnabled = ev.Kind != ChangeKind.Removed };
            row.SetResourceReference(StyleProperty, "ListRow");
            ActivityEvent captured = ev;
            row.Click += (_, _) => OpenEvent(captured);
            RecentList.Children.Add(row);
        }
    }

    /// <summary>"DriveSync (FYP) › Docs" — the indexed folder's name, then the folders inside it.</summary>
    private static string Breadcrumb(ActivityEvent ev)
    {
        IndexedFolder? root = S.FolderOf(ev.RootId);
        string dir = Path.GetDirectoryName(ev.Path) ?? "";
        string rel = root != null && dir.Length >= root.Path.TrimEnd('\\').Length ? dir[root.Path.TrimEnd('\\').Length..].Trim('\\') : dir;
        string name = root?.Name ?? ev.RootName;
        return rel.Length == 0 ? $"{name} › root" : $"{name} › {rel.Replace("\\", " › ")}";
    }

    private static void OpenEvent(ActivityEvent ev)
    {
        int id = S.Engine.FindFile(ev.Path);
        App.Host.Navigate("search");
        SearchPage search = App.Host.SearchPage;
        search.SetQuery(Path.GetFileNameWithoutExtension(ev.Name), commit: true);
        if (id >= 0) search.SelectFile(id);
    }

    private void RenderPipeline()
    {
        JobInfo? job = S.Job;
        PipeSub.Text = job != null ? $"{job.Label} in progress" : "Select → Index → Parse → Search. All stages are idle and the index is current.";
        string[][] stages =
        {
            new[] { "Select", "Folders you chose" }, new[] { "Index", "Names into tokens" },
            new[] { "Parse", "Paths and metadata" }, new[] { "Search", "Rank and preview" },
        };
        int current = job == null ? -1 : (int)job.Stage;
        if (Pipeline.Children.Count != 4)
        {
            Pipeline.Children.Clear();
            for (int i = 0; i < 4; i++)
            {
                var stack = new StackPanel();
                var n = Build.Text($"0{i + 1}", size: 11, brush: "AccentBrush", weight: FontWeights.SemiBold);
                n.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
                stack.Children.Add(n);
                stack.Children.Add(Build.Text(stages[i][0], weight: FontWeights.SemiBold, margin: new Thickness(0, 2, 0, 0)));
                stack.Children.Add(Build.Text("", size: 12, brush: "Text3Brush", margin: new Thickness(0, 2, 0, 0)));
                var card = new Border { Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(4, 0, 4, 8) };
                ((TextBlock)stack.Children[2]).TextWrapping = TextWrapping.Wrap;
                Pipeline.Children.Add(card);
            }
        }
        for (int i = 0; i < 4; i++)
        {
            var card = (Border)Pipeline.Children[i];
            bool on = i == current;
            card.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoftBrush" : "LayerBrush");
            card.SetResourceReference(Border.BorderBrushProperty, on ? "AccentLineBrush" : "StrokeBrush");
            ((TextBlock)((StackPanel)card.Child).Children[2]).Text = current < 0 ? stages[i][1] : i < current ? "Done" : i == current ? "Running…" : "Waiting";
        }
    }

    private void RenderFolders()
    {
        FolderList.Children.Clear();
        JobInfo? job = S.Job;
        var folders = S.Folders;
        if (folders.Count == 0)
        {
            var add = Build.Button("Add folder", "i-folder-add", onClick: () => AddFolderDialog.Show());
            add.Margin = new Thickness(12, 4, 12, 8);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            FolderList.Children.Add(add);
            return;
        }
        foreach (IndexedFolder f in folders)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Build.AccentGlyph("i-folder"));
            var main = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            main.Children.Add(Build.Text(f.Name, weight: FontWeights.Medium, trim: true));
            main.Children.Add(Build.Mono(f.Path, 12));
            Grid.SetColumn(main, 1);
            grid.Children.Add(main);
            Pill pill = Build.FolderPill(f, job);
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
            var row = new Button { Content = grid };
            row.SetResourceReference(StyleProperty, "ListRow");
            row.Click += (_, _) => App.Host.Navigate("folders");
            FolderList.Children.Add(row);
        }
    }

    private static void GoSearch(string query)
    {
        App.Host.Navigate("search");
        App.Host.SearchPage.SetQuery(query, commit: true);
        App.Host.SearchPage.FocusQuery();
    }

    private void HeroBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string q = HeroBox.Text.Trim();
        HeroBox.Text = "";
        GoSearch(q);
        e.Handled = true;
    }

    private void Hero_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool on = HeroBox.IsKeyboardFocusWithin;
        Hero.SetResourceReference(Border.BorderBrushProperty, on ? "AccentLineBrush" : "Stroke2Brush");
        Glow.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoftBrush" : "TransparentBrush");
        if (!on) Glow.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolderDialog.Show();
    private void Refresh_Click(object sender, RoutedEventArgs e) => App.Host.RefreshNow();
    private void ViewActivity_Click(object sender, RoutedEventArgs e) => App.Host.Navigate("activity");
    private void Manage_Click(object sender, RoutedEventArgs e) => App.Host.Navigate("folders");
}
