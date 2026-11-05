using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.App.Shell;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

/// <summary>
/// Indexed folders (design pages 22–26): the watched folders with status, counts and actions, the
/// disconnected-drive banner, Pause watching, Add folder, and the exclusion rules.
/// </summary>
public sealed class FoldersPage : UserControl, IPage
{
    private readonly StackPanel _view = new() { Margin = new Thickness(32, 24, 32, 32) };
    private readonly UiButton _pauseAll = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly ContentControl _banner = new() { Focusable = false };
    private readonly WrapPanel _summary = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly Border _tableCard = new();
    private readonly WrapPanel _exclusions = new();
    private readonly TextBox _exclusionInput = new();
    private readonly TextBlock _exclusionHint = new() { Margin = new Thickness(0, 8, 0, 0) };
    private LayoutBand _band = LayoutBand.Wide;
    private readonly Dictionary<int, ProgressLine> _bars = new();
    private readonly Dictionary<int, Pill> _pills = new();

    private static IndexService S => App.Service;

    public FoldersPage()
    {
        Content = new ScrollViewer { Content = _view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        // Page head
        var head = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Build.Text("Indexed folders", style: "H1"));
        titles.Children.Add(Build.Text("DriveSync watches these folders and keeps their file names and paths in the index. Files never leave this PC.",
            style: "Muted", margin: new Thickness(0, 4, 0, 0)));
        head.Children.Add(titles);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 0) };
        _pauseAll.Click += (_, _) => { App.Host.ToggleWatching(); Render(); };
        buttons.Children.Add(_pauseAll);
        buttons.Children.Add(Build.Button("Add folder", "i-folder-add", ButtonVariant.Primary, onClick: () => AddFolderDialog.Show()));
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        _view.Children.Add(head);

        _view.Children.Add(_banner);
        _view.Children.Add(_summary);
        _tableCard.SetResourceReference(StyleProperty, "CardFlush");
        _view.Children.Add(_tableCard);

        // Exclusions card
        var excl = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        title.Children.Add(new Icon { Kind = "i-eye-off", Size = 16, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Build.Text("Exclusions", style: "SectionTitle", margin: new Thickness(8, 0, 0, 0)));
        excl.Children.Add(title);
        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, -4, 0, 12) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        help.Inlines.Add("Files in these folders or with these extensions are skipped. Use a folder name like ");
        help.Inlines.Add(MonoRun("bin"));
        help.Inlines.Add(", an extension like ");
        help.Inlines.Add(MonoRun("*.tmp"));
        help.Inlines.Add(", or a full path.");
        excl.Children.Add(help);
        excl.Children.Add(_exclusions);
        _exclusionHint.SetResourceReference(StyleProperty, "Hint");
        excl.Children.Add(_exclusionHint);
        var card = new Border { Child = excl, Margin = new Thickness(0, 16, 0, 0) };
        card.SetResourceReference(StyleProperty, "Card");
        _view.Children.Add(card);

        _exclusionInput.SetResourceReference(StyleProperty, "InputMono");
        _exclusionInput.Height = 28;
        _exclusionInput.Width = 180;
        _exclusionInput.FontSize = 12;
        Field.SetPlaceholder(_exclusionInput, "node_modules or *.log");
        System.Windows.Automation.AutomationProperties.SetName(_exclusionInput, "New exclusion");
        _exclusionInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddExclusion(); e.Handled = true; } };
    }

    private static System.Windows.Documents.Run MonoRun(string text)
    {
        var r = new System.Windows.Documents.Run(text);
        r.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "FontMono");
        return r;
    }

    public void OnShown() { Render(); RenderExclusions(); }
    public void OnDataChanged()
    {
        if (!IsVisible) return;
        Render();
        // Rules added elsewhere (Exclude this folder from the index) show up here too.
        if (S.Exclusions.Count != _exclusionCount && !_exclusionInput.IsKeyboardFocusWithin) RenderExclusions();
    }

    public void OnTick()
    {
        if (!IsVisible) return;
        JobInfo? job = S.Job;
        foreach (var (id, bar) in _bars) bar.Value = job?.Progress ?? 0;
        foreach (var (id, pill) in _pills) pill.Content = $"Scanning {job?.Percent ?? 0}%";
    }

    public void OnBand(LayoutBand band)
    {
        _band = band;
        _view.Margin = band == LayoutBand.Tiny ? new Thickness(16, 16, 16, 24) : new Thickness(32, 24, 32, 32);
        if (IsVisible) Render();
    }

    private void Render()
    {
        var folders = S.Folders;
        JobInfo? job = S.Job;
        _pauseAll.Icon = S.Watching ? "i-pause" : "i-play";
        _pauseAll.Content = S.Watching ? "Pause watching" : "Resume watching";

        // Disconnected drive banner
        IndexedFolder? off = folders.FirstOrDefault(f => f.Status == RootStatus.Unreachable);
        if (off != null)
        {
            UiButton check = Build.Button("Check again", small: true);
            check.Click += (_, _) => Actions.CheckDrives(check);
            var bar = Build.Infobar("err", "i-drive",
                Build.Rich(($"{off.Drive}\\ isn't connected.", true),
                           ($" {Fmt.Num(off.FileCount)} files from {off.Name} stay searchable from the last scan but can't be opened. DriveSync resumes watching when the drive is back.", false)),
                check);
            bar.Margin = new Thickness(0, 0, 0, 16);
            _banner.Content = bar;
        }
        else _banner.Content = null;

        // Summary line
        _summary.Children.Clear();
        long totalSize = S.Engine.TotalSize();
        foreach (var (value, label) in new[]
        {
            (Fmt.Num(folders.Count), folders.Count == 1 ? " folder" : " folders"), (Fmt.Num(S.FileCount), " files indexed"),
            (Fmt.Size(totalSize), " on disk"), (Fmt.Num(S.Exclusions.Count), " exclusion rules"),
        })
        {
            var t = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 0, 24, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            var v = new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold };
            v.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
            t.Inlines.Add(v);
            t.Inlines.Add(label);
            _summary.Children.Add(t);
        }

        // Table
        _bars.Clear();
        _pills.Clear();
        if (folders.Count == 0)
        {
            _tableCard.Child = new EmptyState
            {
                Icon = "i-folder-add", Title = "No folders yet",
                Message = "Add the folders you search most. DriveSync indexes them once, then keeps up with changes on its own.",
                Content = Build.Button("Add folder", "i-folder-add", ButtonVariant.Primary, onClick: () => AddFolderDialog.Show()),
            };
        }
        else
        {
            bool tiny = _band == LayoutBand.Tiny;
            var table = new Grid();
            double[] widths = tiny ? new[] { -1.0, 0, 0, 0, 0 } : new[] { -1.0, 0, 0, 0, 0 };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = tiny ? new GridLength(0) : GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = tiny ? new GridLength(0) : GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string[] heads = { "FOLDER", "STATUS", "FILES", "LAST SCAN", "ACTIONS" };
            for (int c = 0; c < heads.Length; c++)
            {
                var h = Build.Text(heads[c], size: 11.5, brush: "Text3Brush", weight: FontWeights.SemiBold);
                if (c == 2 || c == 4) h.HorizontalAlignment = HorizontalAlignment.Right;
                Cell(table, h, row, c, header: true);
            }
            foreach (IndexedFolder f in folders)
            {
                row++;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool scanning = f.Status == RootStatus.Scanning;

                var name = new Grid();
                name.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                name.ColumnDefinitions.Add(new ColumnDefinition());
                name.Children.Add(Build.AccentGlyph(f.IsSystemDrive ? "i-folder" : "i-drive"));
                var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Build.Text(f.Name, weight: FontWeights.SemiBold, trim: true));
                TextBlock path = Build.Mono(f.Path, 11.5, trim: false);
                path.TextWrapping = TextWrapping.Wrap;
                text.Children.Add(path);
                if (scanning)
                {
                    var bar = new ProgressLine { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0), Value = job?.Progress ?? 0 };
                    _bars[f.Id] = bar;
                    text.Children.Add(bar);
                }
                Grid.SetColumn(text, 1);
                name.Children.Add(text);
                Cell(table, name, row, 0);

                Pill pill = Build.FolderPill(f, job);
                if (scanning) _pills[f.Id] = pill;
                Cell(table, pill, row, 1);

                var count = Build.Text(Fmt.Num(f.FileCount));
                count.HorizontalAlignment = HorizontalAlignment.Right;
                Cell(table, count, row, 2);

                string last = f.Status == RootStatus.Unreachable ? "Before drive was removed" : f.LastScan is DateTime t ? Fmt.Rel(t) : "Not scanned yet";
                Cell(table, Build.Text(last, brush: "Text2Brush"), row, 3);

                var acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                IndexedFolder folder = f;
                if (f.Status == RootStatus.Unreachable)
                {
                    UiButton check = Build.Button("Check again", "i-refresh", small: true);
                    check.Click += (_, _) => Actions.CheckDrives(check);
                    check.Margin = new Thickness(0, 0, 4, 0);
                    acts.Children.Add(check);
                }
                else
                {
                    UiButton pause = Build.IconButton(f.Paused ? "i-play" : "i-pause", f.Paused ? "Resume watching" : "Pause watching", () => TogglePause(folder));
                    System.Windows.Automation.AutomationProperties.SetName(pause, $"{(f.Paused ? "Resume" : "Pause")} {f.Name}");
                    pause.IsEnabled = !scanning;
                    pause.Margin = new Thickness(0, 0, 4, 0);
                    acts.Children.Add(pause);
                    UiButton rescan = Build.IconButton("i-refresh", "Rescan now", () => Actions.RescanFolder(folder));
                    System.Windows.Automation.AutomationProperties.SetName(rescan, $"Rescan {f.Name}");
                    rescan.IsEnabled = !scanning;
                    rescan.Margin = new Thickness(0, 0, 4, 0);
                    acts.Children.Add(rescan);
                }
                UiButton remove = Build.IconButton("i-trash", "Stop indexing", () => ConfirmRemove(folder));
                System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {f.Name}");
                remove.IsEnabled = !scanning;
                acts.Children.Add(remove);
                Cell(table, acts, row, 4, last: row == folders.Count);
            }
            _tableCard.Child = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }

    }

    private int _exclusionCount = -1;

    private void RenderExclusions()
    {
        _exclusionCount = S.Exclusions.Count;
        _exclusions.Children.Clear();
        foreach (string x in S.Exclusions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Build.Mono(x, 11.5, "Text2Brush", trim: false));
            string pattern = x;
            var close = new UiButton { Variant = ButtonVariant.Ghost, IconOnly = true, Small = true, Icon = "i-x", Width = 18, Height = 18, Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Remove {x}" };
            System.Windows.Automation.AutomationProperties.SetName(close, $"Remove {x}");
            close.Click += (_, _) => RemoveExclusion(pattern);
            row.Children.Add(close);
            var chip = new Border { Child = row, Height = 26, Padding = new Thickness(10, 0, 4, 0), CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8) };
            chip.SetResourceReference(Border.BackgroundProperty, "Layer2Brush");
            chip.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            _exclusions.Children.Add(chip);
        }
        var form = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (_exclusionInput.Parent is Panel p) p.Children.Remove(_exclusionInput);
        form.Children.Add(_exclusionInput);
        UiButton add = Build.Button("Add", "i-plus", small: true, onClick: AddExclusion);
        add.Margin = new Thickness(6, 0, 0, 0);
        form.Children.Add(add);
        _exclusions.Children.Add(form);
    }

    private static void Cell(Grid table, FrameworkElement content, int row, int column, bool header = false, bool last = false)
    {
        var cell = new Border
        {
            Child = content,
            Padding = header ? new Thickness(14, 10, 14, 10) : new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        content.VerticalAlignment = VerticalAlignment.Center;
        cell.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        if (!header)
        {
            cell.MouseEnter += (_, _) => HoverRow(table, row, true);
            cell.MouseLeave += (_, _) => HoverRow(table, row, false);
        }
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static void HoverRow(Grid table, int row, bool on)
    {
        foreach (Border b in table.Children.OfType<Border>().Where(b => Grid.GetRow(b) == row))
        {
            if (on) b.SetResourceReference(Border.BackgroundProperty, "LayerBrush");
            else b.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void TogglePause(IndexedFolder f)
    {
        bool pause = !f.Paused;
        S.SetFolderPaused(f.Id, pause);
        Ui.Toast(pause ? NoticeKind.Warn : NoticeKind.Ok, $"{f.Name} {(pause ? "paused" : "resumed")}",
            pause ? "Its files stay searchable, but changes aren't tracked." : "DriveSync is watching it again.", ms: 2800);
        Render();
    }

    private void ConfirmRemove(IndexedFolder f)
    {
        var path = Build.Infobar("info", "i-folder", Build.Mono(f.Path, 12, "TextBrush", trim: false));
        ((TextBlock)((Grid)path.Child).Children[1]).TextWrapping = TextWrapping.Wrap;
        ConfirmDialog.Show(new ConfirmDialog.Options
        {
            Title = $"Stop indexing {f.Name}?",
            Description = $"DriveSync removes {Fmt.Num(f.FileCount)} entries for this folder from its index and stops watching it. Your files stay exactly where they are.",
            Extra = path,
            ConfirmLabel = "Remove folder",
            ConfirmIcon = "i-trash",
            Variant = ButtonVariant.Danger,
            OnConfirm = () =>
            {
                UndoToken token = S.RemoveFolder(f.Id);
                App.Host.SearchPage.AfterIndexEdit();
                Render();
                Ui.Toast(NoticeKind.Ok, $"{f.Name} removed", $"{Fmt.Num(token.Count)} entries cleared from the index.", "Undo", () =>
                {
                    S.UndoRemoveFolder(token);
                    App.Host.SearchPage.AfterIndexEdit();
                    Render();
                }, ms: 7000);
            },
        });
    }

    private void AddExclusion()
    {
        string v = _exclusionInput.Text.Trim();
        string? error = ExclusionRules.Validate(v, S.Exclusions);
        if (error != null)
        {
            _exclusionHint.Text = error;
            _exclusionHint.Tag = "err";
            Field.SetIsInvalid(_exclusionInput, true);
            _exclusionInput.Focus();
            return;
        }
        Field.SetIsInvalid(_exclusionInput, false);
        UndoToken token = S.AddExclusion(v);
        _exclusionInput.Text = "";
        App.Host.SearchPage.AfterIndexEdit();
        Render();
        RenderExclusions();
        _exclusionHint.Text = $"Added “{v}”. {Fmt.Plural(token.Count, "file")} removed from results.";
        _exclusionHint.Tag = "ok";
        _exclusionInput.Focus();
    }

    private async void RemoveExclusion(string pattern)
    {
        Task<int>? rescan = S.RemoveExclusion(pattern);
        Render();
        RenderExclusions();
        if (rescan == null)
        {
            _exclusionHint.Text = $"Removed “{pattern}”. Its files come back with the next refresh.";
            _exclusionHint.Tag = "ok";
            return;
        }
        _exclusionHint.Text = $"Removed “{pattern}”. Scanning to add its files back…";
        _exclusionHint.Tag = null;
        int added = await rescan;
        App.Host.SearchPage.AfterIndexEdit();
        Render();
        _exclusionHint.Text = $"Removed “{pattern}”. {Fmt.Plural(added, "file")} added back to results.";
        _exclusionHint.Tag = "ok";
    }
}
