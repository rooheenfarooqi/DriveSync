using System.Windows;
using System.Windows.Controls;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core.Benchmarking;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

/// <summary>
/// Benchmarks (design page 28): measured on this PC with the user's own files — indexed lookup against a
/// full directory scan at growing collection sizes, plus Windows Search when its SystemIndex answers —
/// and the planned evaluation.
/// </summary>
public sealed class BenchmarksPage : UserControl, IPage
{
    private readonly StackPanel _view = new() { Margin = new Thickness(32, 24, 32, 32) };
    private readonly UiButton _run;
    private readonly Grid _grid = new();
    private readonly ContentControl _chartHost = new() { Focusable = false };
    private readonly ContentControl _tableHost = new() { Focusable = false };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _caption = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ContentControl _info = new() { Focusable = false };
    private readonly StackPanel _planned = new();
    private readonly Border _resultsCard = new();
    private BenchmarkResult? _result;
    private bool _running;
    private bool _loaded;
    private ProgressLine? _progressBar;
    private TextBlock? _progressLabel;

    private static IndexService S => App.Service;

    public BenchmarksPage()
    {
        Content = new ScrollViewer { Content = _view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Build.Text("Benchmarks", style: "H1"));
        titles.Children.Add(Build.Text("How fast the index answers compared with scanning every path, as the collection grows.", style: "Muted", margin: new Thickness(0, 4, 0, 0)));
        head.Children.Add(titles);
        _run = Build.Button("Run benchmark", "i-gauge", ButtonVariant.Primary, onClick: Run);
        _run.VerticalAlignment = VerticalAlignment.Bottom;
        _run.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(_run, 1);
        head.Children.Add(_run);
        _view.Children.Add(head);
        _view.Children.Add(_info);

        // Chart card
        var chartCard = new Border { MinWidth = 0 };
        chartCard.SetResourceReference(StyleProperty, "Card");
        var cs = new StackPanel();
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_legend, Dock.Right);
        _legend.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(_legend);
        titleRow.Children.Add(Build.Text("Median lookup time", style: "SectionTitle"));
        cs.Children.Add(titleRow);
        cs.Children.Add(_chartHost);
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
        cs.Children.Add(_caption);
        chartCard.Child = cs;

        // Results card
        _resultsCard.SetResourceReference(StyleProperty, "Card");
        var rs = new StackPanel();
        rs.Children.Add(Build.Text("Results", style: "SectionTitle", margin: new Thickness(0, 0, 0, 12)));
        rs.Children.Add(_tableHost);
        _resultsCard.Child = rs;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.Children.Add(chartCard);
        Grid.SetColumn(_resultsCard, 2);
        _grid.Children.Add(_resultsCard);
        _view.Children.Add(_grid);

        // Planned evaluation
        var planned = new Border { Margin = new Thickness(0, 16, 0, 0) };
        planned.SetResourceReference(StyleProperty, "CardFlush");
        var ps = new StackPanel();
        var phead = new StackPanel { Margin = new Thickness(16, 16, 16, 4) };
        phead.Children.Add(Build.Text("Planned evaluation", style: "SectionTitle"));
        phead.Children.Add(Build.Text("Run in the desktop build against native Windows Search, following the precision and recall method from the literature survey.",
            style: "Muted", size: 12.5, margin: new Thickness(0, 4, 0, 8)));
        ps.Children.Add(phead);
        ps.Children.Add(new ScrollViewer { Content = _planned, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        planned.Child = ps;
        _view.Children.Add(planned);
    }

    public void OnShown()
    {
        if (!_loaded)
        {
            _loaded = true;
            _result = S.LoadBenchmark();
        }
        Render();
        // Like the prototype: measure the first time the page is opened.
        if (_result == null && !_running && S.IsLoaded && S.FileCount > 0) Run();
    }

    public void OnDataChanged() { }
    public void OnTick() { if (IsVisible && _result != null && !_running) _caption.Text = Caption(); }

    public void OnBand(LayoutBand band)
    {
        bool stack = band != LayoutBand.Wide;
        _grid.ColumnDefinitions[0].Width = new GridLength(1.4, GridUnitType.Star);
        _grid.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : 16);
        _grid.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_resultsCard, stack ? 0 : 2);
        Grid.SetRow(_resultsCard, stack ? 1 : 0);
        _resultsCard.Margin = new Thickness(0, stack ? 16 : 0, 0, 0);
        _view.Margin = band == LayoutBand.Tiny ? new Thickness(16, 16, 16, 24) : new Thickness(32, 24, 32, 32);
    }

    private string Caption() => _result == null ? "" :
        $"Median of {_result.RunsPerSize} timed runs per size · log scale · measured {Fmt.Rel(_result.At)}";

    private void Render()
    {
        _run.Content = _result != null ? "Run again" : "Run benchmark";
        _run.IsEnabled = !_running;
        bool windows = _result?.WindowsSearchAvailable == true;

        string note = windows
            ? "Windows Search is timed through its SystemIndex provider, scoped to your indexed folders. It always searches its whole index, so its line stays flat."
            : _result?.WindowsSearchNote != null ? _result.WindowsSearchNote + " The comparison with native Windows Search stays scheduled."
            : "The comparison with native Windows Search runs when its SystemIndex provider answers on this PC.";
        var info = Build.Infobar("info", "i-info", Build.Text($"These numbers are measured live on this PC using your own files, at up to 100,000 files. {note}", size: 12.5));
        ((TextBlock)((Grid)info.Child).Children[1]).TextWrapping = TextWrapping.Wrap;
        info.Margin = new Thickness(0, 0, 0, 16);
        _info.Content = info;

        _legend.Children.Clear();
        _legend.Children.Add(LegendItem("AccentBrush", "Indexed lookup"));
        _legend.Children.Add(LegendItem("Text3Brush", "Full directory scan"));
        if (windows) _legend.Children.Add(LegendItem("InfoBrush", "Windows Search"));

        if (_result == null || _result.Points.Count == 0)
        {
            var progress = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 90, 0, 90) };
            _progressLabel = Build.Text(_running ? "Preparing…" : S.FileCount == 0 ? "Index some folders first, then run the benchmark." : "Press Run benchmark to measure.", size: 12, brush: "Text3Brush");
            _progressLabel.HorizontalAlignment = HorizontalAlignment.Center;
            progress.Children.Add(_progressLabel);
            _progressBar = new ProgressLine { Width = 240, Margin = new Thickness(0, 8, 0, 0), Visibility = _running ? Visibility.Visible : Visibility.Collapsed };
            progress.Children.Add(_progressBar);
            _chartHost.Content = progress;
            var skeleton = new StackPanel();
            foreach (double w in new[] { 1.0, 0.8, 0.9 }) skeleton.Children.Add(new Skeleton { Width = 240 * w, HorizontalAlignment = HorizontalAlignment.Left });
            _tableHost.Content = skeleton;
            _caption.Text = "";
        }
        else
        {
            var points = _result.Points;
            var chart = new LogLineChart();
            var series = new List<Series>
            {
                new("Full directory scan", "Text3Brush", points.Select(p => (double?)p.ScanMs).ToArray()),
                new("Indexed lookup", "AccentBrush", points.Select(p => (double?)p.IndexedMs).ToArray()),
            };
            if (windows) series.Insert(1, new Series("Windows Search", "InfoBrush", points.Select(p => p.WindowsSearchMs).ToArray(), Dashed: true));
            chart.SetData(points.Select(p => p.Files).ToArray(), series);
            var scroll = new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            if (_running)
            {
                var stack = new StackPanel();
                stack.Children.Add(scroll);
                _progressLabel = Build.Text("Measuring…", size: 12, brush: "Text3Brush");
                _progressBar = new ProgressLine { Margin = new Thickness(0, 6, 0, 0) };
                stack.Children.Add(_progressLabel);
                stack.Children.Add(_progressBar);
                _chartHost.Content = stack;
            }
            else _chartHost.Content = scroll;
            _caption.Text = _running ? "" : Caption();
            _tableHost.Content = ResultsTable(_result, windows);
        }
        RenderPlanned(windows);
    }

    private static StackPanel LegendItem(string brush, string text)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        swatch.SetResourceReference(Border.BackgroundProperty, brush);
        item.Children.Add(swatch);
        item.Children.Add(Build.Text(text, size: 12, brush: "Text2Brush"));
        return item;
    }

    private static FrameworkElement ResultsTable(BenchmarkResult r, bool windows)
    {
        var heads = new List<string> { "FILES", "INDEXED", "FULL SCAN" };
        if (windows) heads.Add("WINDOWS");
        heads.AddRange(new[] { "FASTER", "BUILD", "TOKENS" });
        var grid = new Grid();
        for (int c = 0; c < heads.Count; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        int row = 0;
        void Cell(string text, int col, string brush = "TextBrush", bool bold = false, double size = 12.5)
        {
            var t = Build.Text(text, size: size, brush: brush, weight: bold ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            var cell = new Border { Child = t, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
            cell.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < heads.Count; c++) Cell(heads[c], c, "Text3Brush", bold: true, size: 11);
        foreach (BenchmarkPoint p in r.Points)
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int c = 0;
            Cell(Fmt.Num(p.Files), c++);
            Cell(Fmt.Ms(p.IndexedMs), c++, "AccentBrush", bold: true);
            Cell(Fmt.Ms(p.ScanMs), c++);
            if (windows) Cell(p.WindowsSearchMs is double w ? Fmt.Ms(w) : "—", c++);
            Cell($"{Math.Round(p.ScanMs / p.IndexedMs):N0}×", c++);
            Cell(Fmt.Ms(p.BuildMs), c++);
            Cell(Fmt.Num(p.Tokens), c);
        }
        var stack = new StackPanel();
        stack.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        BenchmarkPoint last = r.Points[^1];
        var kpis = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        kpis.Children.Add(Build.Kpi($"Index build, {Fmt.Num(last.Files)} files", Build.KpiValue(Fmt.Ms(last.BuildMs))));
        kpis.Children.Add(Build.Kpi("Distinct tokens", Build.KpiValue(Fmt.Num(last.Tokens)), last: true));
        stack.Children.Add(kpis);
        var queries = Build.Text($"Queries, taken from your own file names: {string.Join(", ", r.Queries)}.", size: 12, brush: "Text3Brush", margin: new Thickness(0, 8, 0, 0));
        queries.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(queries);
        return stack;
    }

    private void RenderPlanned(bool windowsMeasured)
    {
        _planned.Children.Clear();
        var grid = new Grid { MinWidth = 560 };
        foreach (GridLength w in new[] { new GridLength(1.3, GridUnitType.Star), new GridLength(1.3, GridUnitType.Star), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        (string Measure, string Dataset, string Window)[] rows =
        {
            ("Lookup latency vs Windows Search", "2k, 20k, 40k files (12 levels deep)", "Apr 16 – May 15, 2027"),
            ("Precision and recall", "Labelled everyday queries", "Apr 16 – May 15, 2027"),
            ("Idle CPU and memory", "Service running for 24 h", "Mar 16 – Apr 15, 2027"),
            ("Burst of 10,000 changes", "Copy and delete test folder", "Mar 16 – Apr 15, 2027"),
        };
        int r = 0;
        void Add(UIElement content, int col, bool header = false, bool last = false)
        {
            var cell = new Border { Child = content, Padding = new Thickness(header ? 16 : 16, 8, 16, 8), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1) };
            cell.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            if (content is FrameworkElement fe && col == 3) fe.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = { "MEASURE", "DATASET", "WINDOW", "STATUS" };
        for (int c = 0; c < 4; c++) Add(Build.Text(heads[c], size: 11, brush: "Text3Brush", weight: FontWeights.SemiBold), c, header: true);
        for (int i = 0; i < rows.Length; i++)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool last = i == rows.Length - 1;
            Add(Build.Text(rows[i].Measure, size: 12.5), 0, last: last);
            Add(Build.Text(rows[i].Dataset, size: 12.5, brush: "Text2Brush"), 1, last: last);
            Add(Build.Text(rows[i].Window, size: 12.5, brush: "Text2Brush"), 2, last: last);
            bool measured = i == 0 && windowsMeasured;
            Add(measured ? Build.Pill("ok", "Measured · see chart") : Build.Pill("mut", "Scheduled"), 3, last: last);
        }
        _planned.Children.Add(grid);
    }

    private async void Run()
    {
        if (_running) return;
        var roots = S.Folders.Where(f => f.Status != RootStatus.Unreachable).Select(f => f.Path).ToList();
        if (roots.Count == 0)
        {
            Ui.Toast(NoticeKind.Info, "Nothing to measure yet", "Index a folder first, then run the benchmark.");
            return;
        }
        _running = true;
        Render();
        var progress = new Progress<(double Fraction, string Label)>(p =>
        {
            if (_progressBar != null) { _progressBar.Visibility = Visibility.Visible; _progressBar.Value = p.Fraction; }
            if (_progressLabel != null) _progressLabel.Text = p.Label;
        });
        try
        {
            BenchmarkResult result = await Task.Run(() => BenchmarkRunner.Run(roots, progress, CancellationToken.None));
            _result = result;
            S.SaveBenchmark(result);
            if (result.Points.Count > 0)
            {
                BenchmarkPoint last = result.Points[^1];
                Ui.Toast(NoticeKind.Ok, "Benchmark finished",
                    $"At {Fmt.Num(last.Files)} files the index answered in {Fmt.Ms(last.IndexedMs)} versus {Fmt.Ms(last.ScanMs)} for a full scan.");
            }
        }
        catch (Exception ex)
        {
            Ui.Toast(NoticeKind.Err, "Benchmark stopped", ex.Message);
        }
        finally
        {
            _running = false;
            Render();
        }
    }
}
