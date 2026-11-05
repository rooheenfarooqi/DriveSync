using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

/// <summary>
/// The Search page. Typing searches after a 50 ms pause; the lookup runs on a background thread and
/// only the newest answer is shown. Results are a virtualized list, the preview pane reads the selected
/// file on demand.
/// </summary>
public partial class SearchPage : UserControl, IPage
{
    private enum ModWindow { Any, Today, Week, Month, Year }

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _historyTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private int _searchSeq;
    private int _shown;
    private SearchResult? _result;
    private LazyRows? _rows;
    private int? _selectedId;
    private bool _settingQuery;
    private bool _sheetOpen;
    private LayoutBand _band = LayoutBand.Wide;

    // Filters
    private TypeGroup _type = TypeGroup.All;
    private object _folder = "all";          // "all", an indexed folder id (int), or a browsed path (string with '\')
    private string? _browsedPath;
    private ModWindow _mod = ModWindow.Any;
    private SortMode _sort = SortMode.Best;

    public SearchPage()
    {
        InitializeComponent();
        _shown = S.Settings.ResultsPerPage;
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        _historyTimer.Tick += (_, _) => { _historyTimer.Stop(); SaveHistory(); };

        TypeFilter.SetOptions(FileKinds.AllGroups.Select(g => new DropOption(g, FileKinds.GroupLabel(g))), _type);
        TypeFilter.Changed += v => { _type = (TypeGroup)v; AfterFilter(); };
        ModFilter.SetOptions(new[]
        {
            new DropOption(ModWindow.Any, "Any time"), new DropOption(ModWindow.Today, "Last 24 hours"), new DropOption(ModWindow.Week, "Past 7 days"),
            new DropOption(ModWindow.Month, "Past 30 days"), new DropOption(ModWindow.Year, "This year"),
        }, _mod);
        ModFilter.Changed += v => { _mod = (ModWindow)v; AfterFilter(); };
        SortFilter.SetOptions(new[]
        {
            new DropOption(SortMode.Best, "Best match"), new DropOption(SortMode.Name, "Name"),
            new DropOption(SortMode.Modified, "Date modified"), new DropOption(SortMode.Size, "Size"),
        }, _sort);
        SortFilter.Changed += v => { _sort = (SortMode)v; AfterFilter(); };
        FolderFilter.Changed += v => { _folder = v; AfterFilter(); };
        FolderFilter.ExtraEntries.Add(new MenuEntry { Label = "Browse…", Icon = "i-folder-open", Action = BrowseFolder });

        Preview.CloseRequested += () => ClosePreviewSheet();
        Preview.KeywordClicked += kw => SetQuery(kw, commit: true);
        ThemeManager.Changed += () => { if (_rows != null) RenderResults(keepScroll: true); };
    }

    private static IndexService S => App.Service;

    // ==================================================================
    // IPage
    // ==================================================================

    public void OnShown()
    {
        RefreshFolderOptions();
        RunSearch();
    }

    public void OnDataChanged()
    {
        RefreshFolderOptions();
        // A change on disk: refresh the results quietly, keeping the scroll position and selection.
        if (IsVisible && _result is { Ran: true }) RunSearch(quietSelect: true, keepScroll: true);
        else if (IsVisible) RunSearch(quietSelect: true);
    }

    public void OnTick() { }

    public void OnBand(LayoutBand band)
    {
        _band = band;
        bool sheet = band is LayoutBand.Narrow or LayoutBand.Tiny;
        PreviewColumn.Width = sheet ? new GridLength(0) : new GridLength(band == LayoutBand.Medium ? 340 : 400);
        if (sheet)
        {
            Grid.SetColumn(PreviewHost, 0);
            PreviewHost.HorizontalAlignment = HorizontalAlignment.Right;
            PreviewHost.Width = Math.Min(420, Math.Max(280, ActualWidth));
            PreviewHost.SetResourceReference(Border.BackgroundProperty, "SheetBrush");
            PreviewHost.SetResourceReference(EffectProperty, "ShadowSheet");
            PreviewHost.RenderTransform = new TranslateTransform(_sheetOpen ? 0 : 440, 0);
            PreviewHost.Visibility = _sheetOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            Grid.SetColumn(PreviewHost, 1);
            PreviewHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewHost.Width = double.NaN;
            PreviewHost.SetResourceReference(Border.BackgroundProperty, "PreviewBrush");
            PreviewHost.Effect = null;
            PreviewHost.RenderTransform = Transform.Identity;
            PreviewHost.Visibility = Visibility.Visible;
            _sheetOpen = false;
        }
        Preview.SetSheetMode(sheet);
        bool tiny = band == LayoutBand.Tiny;
        Top.Margin = tiny ? new Thickness(16, 16, 16, 12) : new Thickness(24, 20, 24, 12);
        Banner.Margin = tiny ? new Thickness(16, 0, 16, 12) : new Thickness(24, 0, 24, 12);
        Header.Visibility = tiny || _rows == null || _rows.RowCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_rows != null) RenderResults(keepScroll: true);
    }

    // ==================================================================
    // Public entry points (Home, keyboard, toasts, Activity)
    // ==================================================================

    public void FocusQuery()
    {
        Query.Focus();
        Query.SelectAll();
    }

    public void SetQuery(string query, bool commit)
    {
        _settingQuery = true;
        Query.Text = query;
        Query.CaretIndex = query.Length;
        _settingQuery = false;
        ClearButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _shown = S.Settings.ResultsPerPage;
        RunSearch(commit: commit);
    }

    /// <summary>For the screenshot tour: switch the preview tab.</summary>
    public void ShowTab(int tab)
    {
        if (_selectedId is int id) Preview.Show(id, force: false, tab: tab);
    }

    /// <summary>For the screenshot tour: open the narrow-window preview sheet on the first result.</summary>
    public void OpenSheetForTour()
    {
        if (_rows is { RowCount: > 0 }) Select(_rows.IdAt(0), quiet: false);
    }

    public void SetFolderFilter(int rootId)
    {
        _folder = rootId;
        RefreshFolderOptions();
        AfterFilter();
    }

    public void SelectFile(int id)
    {
        _selectedId = id;
        Dispatcher.BeginInvoke(() => SyncSelection(scroll: true, openSheet: true), DispatcherPriority.Background);
        Preview.Show(id, force: false);
    }

    public void CopySelectedPath()
    {
        if (_selectedId is int id) Ui.Copy(S.Engine.GetFile(id).FullPath);
    }

    /// <summary>Esc on a narrow window closes the sliding preview. Returns true if it was open.</summary>
    public bool ClosePreviewSheet()
    {
        if (!_sheetOpen) return false;
        _sheetOpen = false;
        var shift = PreviewHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PreviewHost.RenderTransform = shift;
        Motion.To(shift, TranslateTransform.XProperty, 440, 250, done: () => { if (!_sheetOpen) PreviewHost.Visibility = Visibility.Collapsed; });
        Results.Focus();
        return true;
    }

    private void OpenPreviewSheet()
    {
        if (_band is not (LayoutBand.Narrow or LayoutBand.Tiny) || _sheetOpen) return;
        _sheetOpen = true;
        PreviewHost.Visibility = Visibility.Visible;
        PreviewHost.Width = Math.Min(420, Math.Max(280, ActualWidth));
        var shift = PreviewHost.RenderTransform as TranslateTransform ?? new TranslateTransform(440, 0);
        PreviewHost.RenderTransform = shift;
        Motion.To(shift, TranslateTransform.XProperty, 0, 250, 440);
    }

    /// <summary>After an exclusion, removal or clear: search again and drop the selection if its file is gone.</summary>
    public void AfterIndexEdit()
    {
        if (_selectedId is int id && S.Engine.GetFile(id).IsDead)
        {
            _selectedId = null;
            Preview.Show(null, false);
        }
        RefreshFolderOptions();
        RunSearch(quietSelect: true);
    }

    // ==================================================================
    // Searching
    // ==================================================================

    private bool FiltersDefault => _type == TypeGroup.All && Equals(_folder, "all") && _mod == ModWindow.Any && _sort == SortMode.Best;

    private void RefreshFolderOptions()
    {
        var options = new List<DropOption> { new("all", "All drives") };
        options.AddRange(S.Folders.Select(f => new DropOption(f.Id, f.Name)));
        if (_browsedPath != null) options.Add(new DropOption(_browsedPath, Path.GetFileName(_browsedPath.TrimEnd('\\')) is { Length: > 0 } n ? n : _browsedPath));
        if (_folder is int id && S.FolderOf(id) == null) _folder = "all";
        FolderFilter.SetOptions(options, _folder);
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Search inside this folder" };
        if (_browsedPath != null) dialog.InitialDirectory = _browsedPath;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _browsedPath = dialog.FolderName;
        _folder = _browsedPath;
        RefreshFolderOptions();
        AfterFilter();
    }

    private void AfterFilter()
    {
        ResetButton.Visibility = FiltersDefault ? Visibility.Collapsed : Visibility.Visible;
        _shown = S.Settings.ResultsPerPage;
        RunSearch();
    }

    private SearchRequest BuildRequest() => new()
    {
        Query = Query.Text,
        Type = _type,
        RootId = _folder is int id ? id : -1,
        ScopePath = _folder is string p && p != "all" ? p : null,
        ModifiedSince = _mod switch
        {
            ModWindow.Today => DateTime.Now.AddDays(-1).Ticks,
            ModWindow.Week => DateTime.Now.AddDays(-7).Ticks,
            ModWindow.Month => DateTime.Now.AddDays(-30).Ticks,
            ModWindow.Year => new DateTime(DateTime.Now.Year, 1, 1).Ticks,
            _ => 0,
        },
        Sort = _sort,
        Take = _shown,
    };

    private async void RunSearch(bool commit = false, bool quietSelect = false, bool keepScroll = false)
    {
        int seq = ++_searchSeq;
        SearchRequest request = BuildRequest();
        bool record = !string.IsNullOrWhiteSpace(request.Query) && !keepScroll;
        SearchResult result = await Task.Run(() => S.Search(request, record));
        if (seq != _searchSeq) return; // the user typed something else meanwhile
        _result = result;
        RenderResults(keepScroll: keepScroll);

        bool hasResults = _rows != null && _rows.RowCount > 0;
        if (hasResults && (_selectedId == null || _rows!.IndexOfId(_selectedId.Value) < 0) && _band is LayoutBand.Wide or LayoutBand.Medium)
            Select(_rows!.IdAt(0), quiet: true);
        else
            SyncSelection(scroll: false, openSheet: false);

        _historyTimer.Stop();
        if (commit) SaveHistory();
        else if (record) _historyTimer.Start();
    }

    private void SaveHistory()
    {
        string q = Query.Text.Trim();
        if (q.Length > 0 && _result is { Total: > 0 }) S.AddHistory(q);
    }

    private void RenderResults(bool keepScroll)
    {
        SearchResult? r = _result;
        Banner.Content = null;
        Banner.Visibility = Visibility.Collapsed;
        Timing.Visibility = Visibility.Collapsed;
        bool active = r is { Ran: true };
        int folders = S.Folders.Count;

        if (!S.IsLoaded)
        {
            ShowState(new EmptyState { Icon = "i-db", Title = "Loading your index", Message = "Search is ready in a moment. DriveSync is reading the saved index." });
            MetaText.Text = "";
            return;
        }
        if (S.IsCleared)
        {
            MetaText.Text = "";
            var rebuild = Build.Button("Rebuild index", "i-refresh", ButtonVariant.Primary, onClick: () => Actions.Rebuild(confirm: false));
            ShowState(new EmptyState
            {
                Icon = "i-db", Tone = "warn", Title = "Your index is empty",
                Message = $"DriveSync has nothing to search because the index was cleared. Rebuild it to scan your {folders} folder{(folders == 1 ? "" : "s")} again.",
                Content = rebuild,
            });
            return;
        }
        if (folders == 0)
        {
            MetaText.Text = "";
            ShowState(new EmptyState
            {
                Icon = "i-folder-add", Title = "No folders yet",
                Message = "Add the folders you search most. DriveSync indexes them once, then keeps up with changes on its own.",
                Content = Build.Button("Add folder", "i-folder-add", ButtonVariant.Primary, onClick: () => AddFolderDialog.Show()),
            });
            return;
        }
        if (!active)
        {
            MetaText.Text = $"{Fmt.Num(S.FileCount)} files ready to search";
            MetaText.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
            ShowState(ReadyState());
            return;
        }

        MetaText.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        int n = r!.Total;
        MetaText.Text = $"{Fmt.Num(n)} result{(n == 1 ? "" : "s")}";
        TimingText.Text = Fmt.Ms(r.ElapsedMs);
        Timing.Visibility = Visibility.Visible;

        if (r.Fuzzy)
        {
            Banner.Visibility = Visibility.Visible;
            Banner.Content = Build.Infobar("info", "i-info", Build.Text($"No exact matches for “{Query.Text.Trim()}”. Showing names that contain those letters in order.", size: 12.5));
        }
        else if (n > 0)
        {
            var offlineDrives = r.Ids.Select(id => S.Engine.GetFile(id)).Where(Ui.IsOffline).Select(Ui.DriveOf).Distinct().ToList();
            if (offlineDrives.Count > 0)
            {
                string drives = string.Join(", ", offlineDrives.Select(d => d + "\\"));
                var check = Build.Button("Check folders", small: true, onClick: () => App.Host.Navigate("folders"));
                Banner.Visibility = Visibility.Visible;
                Banner.Content = Build.Infobar("warn", "i-warn",
                    Build.Rich(("Some results are on ", false), (drives, true), (", which isn't connected. You can see them, but they won't open until the drive is back.", false)), check);
            }
        }

        if (n == 0)
        {
            var paused = S.Folders.Where(f => f.Paused).Select(f => f.Name).ToList();
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            if (!FiltersDefault) actions.Children.Add(Spaced(Build.Button("Remove filters", "i-filter", onClick: ResetFilters)));
            if (r.Query.HasOperators) actions.Children.Add(Spaced(Build.Button("Search without operators", onClick: () => SetQuery(ParsedQuery.StripOperators(Query.Text), commit: true))));
            actions.Children.Add(Spaced(Build.Button("Index another folder", "i-folder-add", onClick: () => App.Host.Navigate("folders"))));
            string what = Query.Text.Trim().Length > 0 ? Query.Text.Trim() : "these filters";
            ShowState(new EmptyState
            {
                Icon = "i-search",
                Title = $"No files match “{what}”",
                Message = "Check the spelling, use fewer words, or search part of a name." +
                          (paused.Count > 0 ? $" Changes in {string.Join(", ", paused)} aren't being tracked while paused." : ""),
                Content = actions,
            });
            _rows = null;
            Results.ItemsSource = null;
            Header.Visibility = Visibility.Collapsed;
            return;
        }

        StateHost.Content = null;
        Results.Visibility = Visibility.Visible;
        Header.Visibility = _band == LayoutBand.Tiny ? Visibility.Collapsed : Visibility.Visible;
        int perPage = S.Settings.ResultsPerPage;
        MoreRow? more = n > r.Ids.Length ? new MoreRow { Label = $"Show {Fmt.Num(Math.Min(perPage, n - r.Ids.Length))} more of {Fmt.Num(n - r.Ids.Length)}" } : null;
        ParsedQuery query = r.Query;
        bool fuzzy = r.Fuzzy;
        bool showPaths = S.Settings.ShowFullPaths;
        bool compact = ThemeManager.Compact;
        bool narrowRow = _band == LayoutBand.Tiny;
        _rows = new LazyRows(r.Ids, id => MakeRow(id, query, fuzzy, showPaths, compact), more);
        double offset = keepScroll ? FindScroll()?.VerticalOffset ?? 0 : 0;
        Results.ItemsSource = _rows;
        Results.ItemContainerStyle = (Style)FindResource(compact ? "ResultItemCompact" : "ResultItem");
        SyncSelection(scroll: false, openSheet: false);
        if (keepScroll) Dispatcher.BeginInvoke(() => FindScroll()?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        else FindScroll()?.ScrollToTop();
        SetRowColumns(narrowRow);
    }

    private static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(4);
        return e;
    }

    private void SetRowColumns(bool narrow)
    {
        // On a tiny window the Modified and Size columns are hidden (the design's 720-px rule).
        Results.Tag = narrow ? "narrow" : null;
    }

    private ScrollViewer? FindScroll()
    {
        if (VisualTreeHelper.GetChildrenCount(Results) == 0) return null;
        DependencyObject border = VisualTreeHelper.GetChild(Results, 0);
        return VisualTreeHelper.GetChildrenCount(border) > 0 ? VisualTreeHelper.GetChild(border, 0) as ScrollViewer : null;
    }

    private static ResultRow MakeRow(int id, ParsedQuery query, bool fuzzy, bool showPaths, bool compact)
    {
        FileView f = S.Engine.GetFile(id);
        return new ResultRow
        {
            Id = id,
            File = f,
            NameSpec = Highlighter.For(f.Name, query, fuzzy),
            Path = f.FullPath,
            Modified = Fmt.Rel(f.Modified),
            Size = Fmt.Size(f.Size),
            Kind = f.Kind,
            IsNew = f.IsNew,
            Offline = Ui.IsOffline(f),
            Compact = compact,
            PathVisibility = showPaths ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    private void ShowState(EmptyState state)
    {
        _rows = null;
        Results.ItemsSource = null;
        Results.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Collapsed;
        StateHost.Content = new ScrollViewer { Content = state, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    private EmptyState ReadyState()
    {
        var body = new StackPanel();
        var tips = new System.Windows.Controls.Primitives.UniformGrid { Columns = _band is LayoutBand.Narrow or LayoutBand.Tiny ? 1 : 3, MaxWidth = 640, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (code, text) in new[] { ("ext:pdf", "Only one file extension"), ("in:semester", "Path contains a folder name"), ("type:code", "Documents, PDFs, code, images…") })
        {
            var t = new StackPanel();
            var c = Build.Mono(code, 12, "AccentBrush");
            t.Children.Add(c);
            t.Children.Add(Build.Text(text, size: 12, brush: "Text3Brush", margin: new Thickness(0, 4, 0, 0)));
            var card = new Border { Child = t, Margin = new Thickness(5), Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch };
            card.SetResourceReference(StyleProperty, "Card");
            card.Padding = new Thickness(12);
            tips.Children.Add(card);
        }
        body.Children.Add(tips);
        if (S.Settings.RememberHistory && S.History.Count > 0)
        {
            var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            foreach (string h in S.History.Take(6)) chips.Children.Add(Build.Chip(h, "i-history", () => SetQuery(h, commit: true)));
            body.Children.Add(chips);
        }
        var keys = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        keys.Children.Add(new Kbd { Content = "↑" });
        keys.Children.Add(new Kbd { Content = "↓", Margin = new Thickness(4, 0, 0, 0) });
        keys.Children.Add(Build.Text(" to move · ", size: 12, brush: "Text3Brush"));
        keys.Children.Add(new Kbd { Content = "Enter" });
        keys.Children.Add(Build.Text(" to open · right-click for more", size: 12, brush: "Text3Brush"));
        body.Children.Add(keys);
        return new EmptyState
        {
            Icon = "i-search",
            Title = $"Search {Fmt.Num(S.FileCount)} indexed files",
            Message = "Results appear as you type. Names and folder paths are both searchable, and partial words match.",
            Content = body,
        };
    }

    // ==================================================================
    // Selection and keyboard
    // ==================================================================

    private void Select(int id, bool quiet)
    {
        _selectedId = id;
        SyncSelection(scroll: !quiet, openSheet: !quiet);
        Preview.Show(id, force: false);
    }

    private bool _syncing;

    private void SyncSelection(bool scroll, bool openSheet)
    {
        if (_rows == null) return;
        int index = _selectedId is int id ? _rows.IndexOfId(id) : -1;
        _syncing = true;
        Results.SelectedIndex = index;
        _syncing = false;
        if (index >= 0 && scroll) Results.ScrollIntoView(Results.Items[index]);
        if (index >= 0 && openSheet) OpenPreviewSheet();
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (Results.SelectedItem is ResultRow row)
        {
            _selectedId = row.Id;
            Preview.Show(row.Id, force: false);
            OpenPreviewSheet();
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource as DependencyObject) is ResultRow row) Ui.Open(row.File);
    }

    private object? ItemUnder(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext;
    }

    private void Results_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource as DependencyObject) is not ResultRow row) return;
        Select(row.Id, quiet: true);
        Point screen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        Actions.FileMenu(row.File, Results, new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY), () => SummarizeFile(row.Id));
        e.Handled = true;
    }

    private void SummarizeFile(int id)
    {
        Select(id, quiet: false);
        Preview.Show(id, force: true, tab: 0);
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_rows == null || _rows.RowCount == 0) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int index = Results.SelectedIndex;
        if (key == Key.Up && index <= 0)
        {
            FocusQuery();
            e.Handled = true;
        }
        else if (key == Key.Enter && Results.SelectedItem is ResultRow row)
        {
            Ui.Open(row.File);
            e.Handled = true;
        }
        else if ((key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) || key == Key.Apps)
        {
            if (Results.SelectedItem is ResultRow r && Results.ItemContainerGenerator.ContainerFromItem(r) is ListBoxItem item)
            {
                Point p = item.PointToScreen(new Point(60, item.ActualHeight));
                var dpi = VisualTreeHelper.GetDpi(this);
                Actions.FileMenu(r.File, item, new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY), () => SummarizeFile(r.Id));
            }
            e.Handled = true;
        }
        else if (key == Key.Down && index == _rows.RowCount - 1)
        {
            e.Handled = true; // don't move onto the "Show more" row with the arrow
        }
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _rows is { RowCount: > 0 })
        {
            int id = _selectedId is int s && _rows.IndexOfId(s) >= 0 ? s : _rows.IdAt(0);
            Select(id, quiet: false);
            FocusSelectedRow();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _debounce.Stop();
            RunSearchThenFocus();
            e.Handled = true;
        }
    }

    private async void RunSearchThenFocus()
    {
        RunSearch(commit: true);
        await Task.Delay(80);
        if (_rows is { RowCount: > 0 })
        {
            Select(_rows.IdAt(0), quiet: false);
            FocusSelectedRow();
        }
    }

    private void FocusSelectedRow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Results.SelectedItem != null && Results.ItemContainerGenerator.ContainerFromItem(Results.SelectedItem) is ListBoxItem item)
                item.Focus();
            else
                Results.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ==================================================================
    // Controls
    // ==================================================================

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearButton.Visibility = Query.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingQuery) return;
        _shown = S.Settings.ResultsPerPage;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Query_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool on = Query.IsKeyboardFocusWithin;
        QLine.Height = on ? 2 : 1;
        QLine.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "QueryBottomBrush");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetQuery("", commit: false);
        Query.Focus();
    }

    private void ResetFilters()
    {
        _type = TypeGroup.All;
        _folder = "all";
        _mod = ModWindow.Any;
        _sort = SortMode.Best;
        TypeFilter.Value = _type;
        ModFilter.Value = _mod;
        SortFilter.Value = _sort;
        RefreshFolderOptions();
        AfterFilter();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetFilters();

    private void SortModified_Click(object sender, RoutedEventArgs e)
    {
        _sort = SortMode.Modified;
        SortFilter.Value = _sort;
        AfterFilter();
    }

    private void SortSize_Click(object sender, RoutedEventArgs e)
    {
        _sort = SortMode.Size;
        SortFilter.Value = _sort;
        AfterFilter();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _shown += S.Settings.ResultsPerPage;
        double offset = FindScroll()?.VerticalOffset ?? 0;
        RunSearchKeepingScroll(offset);
    }

    private async void RunSearchKeepingScroll(double offset)
    {
        int seq = ++_searchSeq;
        SearchRequest request = BuildRequest();
        SearchResult result = await Task.Run(() => S.Search(request, recordLookup: false));
        if (seq != _searchSeq) return;
        _result = result;
        RenderResults(keepScroll: false);
        await Dispatcher.InvokeAsync(() => FindScroll()?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }
}
