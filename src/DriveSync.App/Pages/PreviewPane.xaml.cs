using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;
using DriveSync.Core.Parsing;

namespace DriveSync.App.Pages;

/// <summary>
/// Reads the selected file only when asked (on selection, or with Summarize when automatic summaries are
/// off), on a background thread, and shows the summary, outline and details. Results are cached by
/// PreviewService, so going back to a file is instant.
/// </summary>
public partial class PreviewPane : UserControl
{
    private int? _id;
    private FileView _file;
    private readonly HashSet<int> _forced = new();
    private readonly HashSet<int> _parsing = new();
    private CancellationTokenSource? _cts;

    public event Action? CloseRequested;
    public event Action<string>? KeywordClicked;

    public PreviewPane()
    {
        InitializeComponent();
        Tabs.SetItems("Summary", "Outline", "Details");
        Tabs.SelectionChanged += _ => RenderBody();
    }

    private static IndexService S => App.Service;

    public void SetSheetMode(bool sheet) => CloseButton.Visibility = sheet ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows a file (or the empty state for null). <paramref name="force"/> summarizes even if blocked by the size limit or settings.</summary>
    public void Show(int? id, bool force, int? tab = null)
    {
        if (tab is int t) Tabs.Select(t, raise: false);
        if (id is int fid && force) _forced.Add(fid);
        bool same = id == _id;
        _id = id;
        if (id == null)
        {
            _cts?.Cancel();
            Empty.Visibility = Visibility.Visible;
            Filled.Visibility = Visibility.Collapsed;
            return;
        }
        _file = S.Engine.GetFile(id.Value);
        Empty.Visibility = Visibility.Collapsed;
        Filled.Visibility = Visibility.Visible;
        if (!same) _cts?.Cancel();
        RenderHead();
        RenderBody();
        if (!same) BodyScroll.ScrollToTop();
    }

    private void RenderHead()
    {
        FileView f = _file;
        Glyph.Kind = f.Kind;
        Glyph.Extension = f.Extension;
        NameText.Text = f.Name;
        PathText.Text = f.FullPath;
        Pills.Children.Clear();
        Pills.Children.Add(Spaced(Build.Pill("mut", FileKinds.Short(f.Kind))));
        Pills.Children.Add(Spaced(Build.Pill("mut", Fmt.Size(f.Size))));
        if (f.IsNew) Pills.Children.Add(Spaced(Build.Pill("acc", "New")));
        if (Ui.IsOffline(f)) Pills.Children.Add(Spaced(Build.Pill("err", "Offline")));
    }

    private static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 0, 6, 0);
        return e;
    }

    // ==================================================================
    // Body
    // ==================================================================

    private void RenderBody()
    {
        if (_id == null) return;
        Body.Children.Clear();
        FileView f = _file;
        int tab = Tabs.Selected;
        int sentences = S.Settings.SummaryLength;
        PreviewResult? cached = PreviewService.Cached(f, sentences);

        if (tab == 2)
        {
            RenderDetails(f, cached);
            return;
        }

        bool forced = _forced.Contains(f.Id);
        PreviewResult? blocker = PreviewService.Blocker(f, Ui.IsOffline(f), S.Settings.MaxParseMb, forced);
        if (blocker != null)
        {
            RenderBlocker(f, blocker);
            return;
        }
        if (cached == null && !_parsing.Contains(f.Id) && _lastResult.Remove(f.Id, out PreviewResult? last))
        {
            // A result that isn't cached (locked, missing, damaged) is shown once; the next look tries again.
            if (last.State == PreviewState.Ok) { if (tab == 1) RenderOutline(last); else RenderSummary(last); }
            else RenderBlocker(f, last);
            return;
        }
        if (cached == null)
        {
            if (_parsing.Contains(f.Id) || S.Settings.SummarizeOnSelect || forced)
            {
                StartParse(f, sentences);
                RenderParsing(f, tab);
            }
            else
            {
                Body.Children.Add(new EmptyState
                {
                    Icon = "i-sparkle", MinHeight = 200, Padding = new Thickness(8, 24, 8, 24),
                    Title = "Summary not generated yet",
                    Message = "DriveSync reads the file only when you ask. Automatic summaries are turned off in Settings.",
                    Content = Build.Button("Summarize", "i-sparkle", ButtonVariant.Primary, onClick: () => Show(f.Id, force: true)),
                });
            }
            return;
        }
        if (cached.State != PreviewState.Ok)
        {
            RenderBlocker(f, cached);
            return;
        }
        if (tab == 1) RenderOutline(cached);
        else RenderSummary(cached);
    }

    private void StartParse(FileView f, int sentences)
    {
        if (_parsing.Contains(f.Id)) return;
        _parsing.Add(f.Id);
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Run(() => PreviewService.Analyze(f, sentences, cts.Token), cts.Token).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _parsing.Remove(f.Id);
                if (t.IsCanceled || _id != f.Id) return;
                PreviewResult result = t.IsFaulted ? new PreviewResult { State = PreviewState.Error, Message = t.Exception?.InnerException?.Message } : t.Result;
                _lastResult[f.Id] = result;
                RenderBody();
            });
        }, TaskScheduler.Default);
    }

    // Results that aren't cached (locked, missing, errors) are kept here until the next attempt.
    private readonly Dictionary<int, PreviewResult> _lastResult = new();

    private void RenderParsing(FileView f, int tab)
    {
        var lead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        lead.Children.Add(new Dot { State = "busy", VerticalAlignment = VerticalAlignment.Center });
        lead.Children.Add(Build.Text($"Reading {Fmt.Size(f.Size)} and building {(tab == 1 ? "an outline" : "a summary")}…", size: 12, brush: "Text3Brush", margin: new Thickness(8, 0, 0, 0)));
        Body.Children.Add(lead);
        foreach (double w in new[] { 0.96, 0.88, 0.92, 0.6 })
        {
            var sk = new Skeleton { HorizontalAlignment = HorizontalAlignment.Left };
            sk.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = Body, Converter = new ScaleConverter(w) });
            Body.Children.Add(sk);
        }
    }

    private void RenderSummary(PreviewResult r)
    {
        Summary s = r.Summary!;
        var lead = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var ms = Build.Text($"{Math.Round(r.Milliseconds):0} ms", size: 12, brush: "Text3Brush");
        DockPanel.SetDock(ms, Dock.Right);
        lead.Children.Add(ms);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Icon { Kind = "i-sparkle", Size = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Icon.ForegroundProperty, "Text3Brush");
        left.Children.Add(icon);
        left.Children.Add(Build.Text(s.IsCode ? "Structural overview" : $"Extractive summary · {s.Sentences.Count} key sentence{(s.Sentences.Count == 1 ? "" : "s")}",
            size: 12, brush: "Text3Brush", margin: new Thickness(8, 0, 0, 0)));
        lead.Children.Add(left);
        Body.Children.Add(lead);

        if (s.Words == 0)
        {
            Body.Children.Clear();
            Body.Children.Add(new EmptyState
            {
                Icon = "i-text", MinHeight = 200, Padding = new Thickness(8, 24, 8, 24),
                Title = "This file is empty", Message = "There's no text in it to summarize. Check Details for its dates.",
            });
            return;
        }
        if (s.Sentences.Count == 0)
            Body.Children.Add(Build.Text("This file has no sentences DriveSync can pick out.", style: "Muted"));
        foreach (string sentence in s.Sentences)
            Body.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 0, 10) });

        if (s.Keywords.Count > 0)
        {
            Body.Children.Add(Build.Text("KEYWORDS", style: "Eyebrow", margin: new Thickness(0, 6, 0, 8)));
            var chips = new WrapPanel();
            foreach (string kw in s.Keywords)
            {
                string k = kw;
                Button chip = Build.Chip(kw, null, () => KeywordClicked?.Invoke(k));
                chip.Margin = new Thickness(0, 0, 6, 6);
                chips.Children.Add(chip);
            }
            Body.Children.Add(chips);
        }
        Body.Children.Add(Build.Text($"{Fmt.Num(s.Words)} words · about {Math.Max(1, (int)Math.Round(s.Words / 220.0))} min read · summarized on this PC",
            size: 12, brush: "Text3Brush", margin: new Thickness(0, 16, 0, 0)));
    }

    private void RenderOutline(PreviewResult r)
    {
        bool code = r.Summary?.IsCode == true;
        if (r.Outline.Count == 0)
        {
            Body.Children.Add(new EmptyState { Icon = "", MinHeight = 160, Message = code ? "No namespaces, types or members were found in this file." : "This file has no headings, so there's no outline to show." });
            return;
        }
        Body.Children.Add(Build.Text($"{r.Outline.Count} {(code ? "symbols" : r.Outline.All(o => o.Tag == "col") ? "columns" : "headings")}".ToUpperInvariant(), style: "Eyebrow", margin: new Thickness(0, 0, 0, 8)));
        foreach (OutlineItem item in r.Outline.Take(400))
        {
            var row = new Grid { Margin = new Thickness((item.Level - 1) * 16, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Build.Mono(item.Tag, 10.5));
            var text = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            if (code) { text.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono"); text.FontSize = 12; }
            if (item.Level >= 3) text.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var hover = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
            hover.MouseEnter += (_, _) => hover.SetResourceReference(Border.BackgroundProperty, "LayerBrush");
            hover.MouseLeave += (_, _) => hover.Background = Brushes.Transparent;
            Body.Children.Add(hover);
        }
    }

    private void RenderDetails(FileView f, PreviewResult? summary)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Add(string key, UIElement value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock k = Build.Text(key, size: 12.5, brush: "Text3Brush");
            k.VerticalAlignment = VerticalAlignment.Top;
            k.Margin = new Thickness(0, 0, 12, 8);
            Grid.SetRow(k, row);
            grid.Children.Add(k);
            if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            row++;
        }
        TextBlock Value(string text) => new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

        string description = string.IsNullOrEmpty(f.Extension) ? FileKinds.Label(f.Kind) : $"{FileKinds.Label(f.Kind)} (.{f.Extension})";
        Add("Type", Value(description));
        var size = Value(Fmt.Size(f.Size) + " ");
        var bytes = new System.Windows.Documents.Run($"({Fmt.Num(f.Size)} bytes)");
        bytes.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text3Brush");
        size.Inlines.Add(bytes);
        Add("Size", size);
        Add("Folder", Value(S.FolderOf(f.RootId)?.Name ?? ""));
        var location = Value(f.Folder);
        location.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
        location.FontSize = 11.5;
        Add("Location", location);
        Add("Modified", Value(Fmt.DateTimeLong(f.Modified)));
        Add("Created", Value(Fmt.DateTimeLong(f.Created)));
        Add("Indexed", Value(Fmt.DateTimeLong(f.Indexed)));
        if (summary?.Summary is { } s)
        {
            var words = Value(Fmt.Num(s.Words) + " ");
            var read = new System.Windows.Documents.Run($"· about {Math.Max(1, (int)Math.Round(s.Words / 220.0))} min read");
            read.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text3Brush");
            words.Inlines.Add(read);
            Add("Words", words);
        }
        var tokens = new WrapPanel();
        foreach (string t in Core.Indexing.Tokenizer.Tokens(f.Name).Distinct().Take(12))
        {
            var chip = new Border { Height = 22, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 6) };
            chip.SetResourceReference(Border.BackgroundProperty, "Layer2Brush");
            chip.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            chip.Child = Build.Mono(t, 11, "Text2Brush", trim: false);
            tokens.Children.Add(chip);
        }
        Add("Index tokens", tokens);
        Body.Children.Add(grid);
    }

    private void RenderBlocker(FileView f, PreviewResult r)
    {
        string drive = Ui.DriveOf(f);
        EmptyState state = r.State switch
        {
            PreviewState.Offline => new EmptyState
            {
                Tone = "err", Icon = "i-drive", Title = $"Drive {drive} isn't connected",
                Message = "DriveSync can only read this file while the drive is plugged in. Connect it and DriveSync will pick it up again.",
            },
            PreviewState.Locked => new EmptyState
            {
                Tone = "err", Icon = "i-lock", Title = "This file is open in another app",
                Message = r.LockedBy != null
                    ? $"{r.LockedBy} has it locked, so DriveSync can't read it right now. Close the file in {r.LockedBy}, then try again."
                    : "Another app has it locked, so DriveSync can't read it right now. Close it there, then try again.",
            },
            PreviewState.Image => new EmptyState
            {
                Icon = "i-image", Title = "No text to summarize",
                Message = "Images don't contain readable text. Use Open to view it, or check Details for its size and dates.",
                Extra = Thumbnail(f),
            },
            PreviewState.Unsupported => new EmptyState
            {
                Icon = "i-eye-off", Title = "Can't read inside this file type",
                Message = $"DriveSync summarizes documents, PDFs, slides, spreadsheets, text and code. {(string.IsNullOrEmpty(f.Extension) ? FileKinds.Short(f.Kind) : "." + f.Extension)} files are searchable by name only.",
            },
            PreviewState.TooLarge => new EmptyState
            {
                Tone = "warn", Icon = "i-warn", Title = $"Larger than your {S.Settings.MaxParseMb} MB parsing limit",
                Message = $"This file is {Fmt.Size(f.Size)}. Parsing large files takes longer and uses more memory.",
            },
            PreviewState.Missing => new EmptyState
            {
                Tone = "err", Icon = "i-error", Title = "This file isn't there any more",
                Message = "It was moved or deleted after DriveSync last saw it. The index catches up with the next change or refresh.",
            },
            _ => new EmptyState
            {
                Tone = "err", Icon = "i-error", Title = "Couldn't read this file",
                Message = r.Message ?? "The file looks damaged or uses a format DriveSync can't read.",
            },
        };
        state.MinHeight = 220;
        state.Padding = new Thickness(8, 28, 8, 28);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        switch (r.State)
        {
            case PreviewState.Offline:
                UiButton check = Build.Button("Check again", "i-refresh");
                check.Click += (_, _) => Actions.CheckDrives(check);
                actions.Children.Add(check);
                break;
            case PreviewState.Locked:
            case PreviewState.Error:
                actions.Children.Add(Build.Button("Try again", onClick: () => RetryParse(f, r.State == PreviewState.Locked ? r.LockedBy : null)));
                break;
            case PreviewState.TooLarge:
                UiButton anyway = Build.Button("Summarize anyway", onClick: () => Show(f.Id, force: true));
                anyway.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(anyway);
                actions.Children.Add(Build.Button("Change limit", variant: ButtonVariant.Ghost, onClick: () =>
                {
                    App.Host.Navigate("preferences");
                    (App.Host.FindPage("preferences") as SettingsPage)?.FocusMaxParse();
                }));
                break;
            case PreviewState.Missing:
                actions.Children.Add(Build.Button("Refresh index", "i-refresh", onClick: App.Host.RefreshNow));
                break;
        }
        if (actions.Children.Count > 0) state.Content = actions;
        Body.Children.Add(state);
    }

    private void RetryParse(FileView f, string? lockedBy)
    {
        Body.Children.Clear();
        var checking = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 80, 0, 80) };
        checking.Children.Add(new Dot { State = "busy", HorizontalAlignment = HorizontalAlignment.Center });
        checking.Children.Add(Build.Text("Checking whether the file is free…", style: "Muted", margin: new Thickness(0, 10, 0, 0)));
        Body.Children.Add(checking);
        int sentences = S.Settings.SummaryLength;
        Task.Run(async () =>
        {
            await Task.Delay(500);
            return PreviewService.Analyze(f, sentences, CancellationToken.None);
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (_id != f.Id) return;
            PreviewResult result = t.IsFaulted ? new PreviewResult { State = PreviewState.Error } : t.Result;
            _lastResult[f.Id] = result;
            if (result.State == PreviewState.Locked)
                Ui.Toast(NoticeKind.Err, $"Still open in {result.LockedBy ?? lockedBy ?? "another app"}", $"Save and close {f.Name}, then try again.");
            RenderBody();
        }), TaskScheduler.Default);
    }

    private static Image? Thumbnail(FileView f)
    {
        if (Ui.IsOffline(f) || f.Size > 60L * 1024 * 1024) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(f.FullPath);
            bitmap.DecodePixelWidth = 480;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;  // don't keep the file locked
            bitmap.EndInit();
            bitmap.Freeze();
            return new Image { Source = bitmap, MaxHeight = 220, MaxWidth = 340, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) };
        }
        catch (Exception)
        {
            return null; // formats WPF can't decode (HEIC, PSD…) just show the message
        }
    }

    // ==================================================================
    // Actions
    // ==================================================================

    private void Open_Click(object sender, RoutedEventArgs e) => Ui.Open(_file);
    private void Reveal_Click(object sender, RoutedEventArgs e) => Ui.Reveal(_file);
    private void Copy_Click(object sender, RoutedEventArgs e) => Ui.Copy(_file.FullPath);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void More_Click(object sender, RoutedEventArgs e)
    {
        FileView f = _file;
        Actions.FileMenu(f, MoreButton, null, () => Show(f.Id, force: true, tab: 0));
    }
}

/// <summary>Multiplies a width (skeleton bars at 96%, 88%…).</summary>
public sealed class ScaleConverter : System.Windows.Data.IValueConverter
{
    private readonly double _factor;
    public ScaleConverter(double factor) => _factor = factor;
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is double d ? Math.Max(0, d * _factor) : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
