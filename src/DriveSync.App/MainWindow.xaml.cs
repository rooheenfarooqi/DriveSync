using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DriveSync.App.Controls;
using DriveSync.App.Dialogs;
using DriveSync.App.Pages;
using DriveSync.App.Shell;
using DriveSync.Core.Indexing;

namespace DriveSync.App;

/// <summary>Window width bands from the design's responsive rules.</summary>
public enum LayoutBand { Wide, Medium, Narrow, Tiny } // ≥1180, 920–1180, 720–920, <720

/// <summary>
/// The DriveSync window: Mica surface, custom title bar with breadcrumb and live status chip, collapsible
/// navigation with the indexing card, the six pages, dialogs and toasts. Keyboard shortcuts, tray and
/// the global hotkey are wired here.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] PageOrder = { "home", "search", "folders", "activity", "benchmarks", "preferences" };
    private static readonly Dictionary<string, string> PageTitles = new()
    {
        ["home"] = "Home", ["search"] = "Search", ["folders"] = "Indexed folders",
        ["activity"] = "Live activity", ["benchmarks"] = "Benchmarks", ["preferences"] = "Settings",
    };

    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HotkeyService _hotkey = new();
    private TrayService? _tray;
    private bool _quitting;
    private bool _trayHintShown;
    private int _unseen;
    private bool _navCollapsed;

    public ToastHost Toasts { get; }
    public DialogHost Dialogs { get; }
    public string CurrentPage { get; private set; } = "";
    public LayoutBand Band { get; private set; } = LayoutBand.Wide;

    /// <summary>Raised when the window crosses a responsive breakpoint.</summary>
    public event Action<LayoutBand>? BandChanged;

    public MainWindow()
    {
        InitializeComponent();
        Toasts = new ToastHost(ToastPanel);
        Dialogs = new DialogHost(DialogLayer, DialogSurface, ShellGrid);

        IndexService s = App.Service;
        // Like the prototype: min(1440, screen − 48) × min(880, screen − 48).
        Rect work = SystemParameters.WorkArea;
        Width = Math.Clamp(s.Settings.WindowWidth, MinWidth, Math.Max(MinWidth, Math.Min(1440, work.Width - 48)));
        Height = Math.Clamp(s.Settings.WindowHeight, MinHeight, Math.Max(MinHeight, Math.Min(880, work.Height - 48)));
        _navCollapsed = s.Settings.NavCollapsed;

        s.StatusChanged += () => Dispatcher.BeginInvoke(UpdateChrome);
        s.FoldersChanged += () => Dispatcher.BeginInvoke(() => { UpdateChrome(); CurrentPageObject?.OnDataChanged(); });
        s.IndexChanged += () => Dispatcher.BeginInvoke(() => { UpdateChrome(); CurrentPageObject?.OnDataChanged(); });
        s.ActivityAdded += ev => Dispatcher.BeginInvoke(() => OnActivity(ev));
        s.Notify += n => Dispatcher.BeginInvoke(() => Toasts.Show(n));

        _tick.Tick += (_, _) =>
        {
            UpdateChrome();
            CurrentPageObject?.OnTick();
        };
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => OnStateChanged();
        SizeChanged += (_, _) => OnSize();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(UpdateChrome);
    }

    public FrameworkElement? FindPage(string key) => _pages.TryGetValue(key, out FrameworkElement? p) ? p : null;

    private IPage? CurrentPageObject => _pages.TryGetValue(CurrentPage, out FrameworkElement? p) ? p as IPage : null;

    // ==================================================================
    // Start-up
    // ==================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ThemeManager.BackdropActive = MicaHelper.Apply(this);
        ThemeManager.SetGlass(App.Service.Settings.Glass);
        _hotkey.Attach(this);
        _hotkey.Pressed += OnHotkey;
        if (App.Service.Settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Called once the saved index has loaded.</summary>
    public void OnServiceReady()
    {
        IndexService s = App.Service;
        if (!ScreenshotTour.Active) _tray = new TrayService();
        if (!ScreenshotTour.Active) ApplyHotkey(s.Settings.Hotkey, announce: false);
        if (App.SandboxFolder == null) StartupRegistration.Apply(s.Settings.StartWithWindows);
        _tick.Start();
        UpdateChrome();
        CurrentPageObject?.OnDataChanged();
        Task.Run(Core.Parsing.PreviewService.WarmUp);
        Updater.Start();
        if (!s.Settings.Onboarded && !ScreenshotTour.Active)
            Dispatcher.BeginInvoke(() => OnboardingDialog.Show(firstRun: true), DispatcherPriority.Background);
    }

    public bool ApplyHotkey(string combo, bool announce)
    {
        bool ok = _hotkey.Register(combo);
        if (!ok && combo != "off")
            Toasts.Show(new Notice(NoticeKind.Warn, "Shortcut not available", $"{combo.Replace("+", " + ")} is already used by another app. Pick a different one in Settings."));
        else if (announce)
            Toasts.Show(new Notice(NoticeKind.Ok, combo == "off" ? "Shortcut turned off" : "Shortcut updated",
                combo == "off" ? "" : $"Press {combo.Replace("+", " + ")} to open search.", Milliseconds: 2600));
        return ok;
    }

    // ==================================================================
    // Navigation
    // ==================================================================

    public void Navigate(string page, bool focusSearch = false)
    {
        if (!PageTitles.ContainsKey(page)) page = "home";
        bool changed = CurrentPage != page;
        if (!_pages.TryGetValue(page, out FrameworkElement? view))
        {
            view = page switch
            {
                "search" => new SearchPage(),
                "folders" => new FoldersPage(),
                "activity" => new ActivityPage(),
                "benchmarks" => new BenchmarksPage(),
                "preferences" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[page] = view;
            PageHost.Children.Add(view);
            if (view is IPage p0) p0.OnBand(Band);
        }
        foreach (var (key, el) in _pages)
            el.Visibility = key == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (NavItem item in NavItems.Children.OfType<NavItem>().Append(NavSettings))
            item.IsCurrent = (string)item.Tag == page;
        CrumbText.Text = PageTitles[page];
        CurrentPage = page;
        if (page == "activity") _unseen = 0;
        if (changed)
        {
            Motion.Enter(view);
            ((IPage)view).OnShown();
        }
        UpdateChrome();
        if (focusSearch && view is SearchPage search)
            Dispatcher.BeginInvoke(search.FocusQuery, DispatcherPriority.Input);
    }

    public SearchPage SearchPage
    {
        get
        {
            if (!_pages.ContainsKey("search")) { string current = CurrentPage; Navigate("search"); if (current.Length > 0) Navigate(current); }
            return (SearchPage)_pages["search"];
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        string page = (string)((FrameworkElement)sender).Tag;
        Navigate(page, focusSearch: page == "search");
    }

    private void StatusChip_Click(object sender, RoutedEventArgs e) => Navigate("activity");

    private void Burger_Click(object sender, RoutedEventArgs e)
    {
        _navCollapsed = !_navCollapsed;
        App.Service.UpdateSettings(s => s.NavCollapsed = _navCollapsed);
        ApplyNavWidth(animate: true);
    }

    private void ApplyNavWidth(bool animate)
    {
        bool tiny = Band == LayoutBand.Tiny;
        bool collapsed = _navCollapsed || tiny;
        double width = tiny ? 56 : _navCollapsed ? 60 : 236;
        if (animate) Motion.To(NavHost, WidthProperty, width, 250);
        else { NavHost.BeginAnimation(WidthProperty, null); NavHost.Width = width; }
        foreach (NavItem item in NavItems.Children.OfType<NavItem>().Append(NavSettings))
        {
            item.Collapsed = collapsed;
            item.Margin = new Thickness(0, 0, 0, item == NavSettings ? 0 : 2);
        }
        Motion.To(Mini, OpacityProperty, collapsed ? 0 : 1, 200);
        Mini.IsHitTestVisible = !collapsed;
        Mini.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        string label = _navCollapsed ? "Expand navigation" : "Collapse navigation";
        Burger.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(Burger, label);
    }

    // ==================================================================
    // Live status: title-bar chip, navigation card, badge, tray
    // ==================================================================

    public void UpdateChrome()
    {
        IndexService s = App.Service;
        JobInfo? job = s.Job;
        string text = s.StatusText;
        StatusText.Text = text;
        StatusDot.State = s.StatusDot;
        _tray?.SetStatus(text);

        if (job != null)
        {
            MiniDot.State = "busy";
            MiniTitle.Text = job.Label;
            MiniPct.Text = job.Percent + "%";
            MiniSub.Visibility = Visibility.Collapsed;
            MiniBar.Visibility = Visibility.Visible;
            MiniBar.Value = job.Progress;
        }
        else
        {
            MiniPct.Text = "";
            MiniBar.Visibility = Visibility.Collapsed;
            MiniSub.Visibility = Visibility.Visible;
            if (!s.IsLoaded)
            {
                MiniDot.State = "busy";
                MiniTitle.Text = "Loading index";
                MiniSub.Text = "Reading the saved index…";
            }
            else if (s.IsCleared)
            {
                MiniDot.State = "warn";
                MiniTitle.Text = "Index is empty";
                MiniSub.Text = "Rebuild it from Settings";
            }
            else
            {
                bool live = s.Watching && !s.BatteryPaused;
                MiniDot.State = live ? "live" : "warn";
                MiniTitle.Text = live ? "Up to date" : s.BatteryPaused ? "Paused on battery saver" : "Watching paused";
                MiniSub.Text = s.LastRefresh == DateTime.MinValue ? "Not refreshed yet" : "Refreshed " + Fmt.Rel(s.LastRefresh);
            }
        }
        NavActivity.Badge = _unseen == 0 || CurrentPage == "activity" ? "" : _unseen > 99 ? "99+" : _unseen.ToString();
    }

    private void OnActivity(ActivityEvent ev)
    {
        if (CurrentPage != "activity") _unseen++;
        UpdateChrome();
        foreach (FrameworkElement page in _pages.Values)
            if (page is IActivityListener l) l.OnActivity(ev);
    }

    // ==================================================================
    // Keyboard
    // ==================================================================

    private bool Typing => Keyboard.FocusedElement is TextBox;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Dialogs.IsOpen || FluentMenu.IsOpen) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control && key == Key.K)
        {
            Navigate("search", focusSearch: true);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key == Key.OemComma)
        {
            Navigate("preferences");
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D6)
        {
            Navigate(PageOrder[key - Key.D1], focusSearch: key == Key.D2);
            e.Handled = true;
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C && CurrentPage == "search")
        {
            SearchPage.CopySelectedPath();
            e.Handled = true;
        }
        else if (key == Key.Escape && CurrentPage == "search" && SearchPage.ClosePreviewSheet())
        {
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Dialogs.IsOpen || FluentMenu.IsOpen || Typing) return;
        if (e.Text == "/")
        {
            Navigate("search", focusSearch: true);
            e.Handled = true;
        }
        else if (e.Text == "?")
        {
            ShortcutsDialog.Show();
            e.Handled = true;
        }
    }

    private void OnHotkey()
    {
        Restore();
        Navigate("search", focusSearch: true);
    }

    // ==================================================================
    // Window: caption buttons, maximize, responsive bands, close to tray
    // ==================================================================

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = max ? MicaHelper.MaximizedInset(this) : new Thickness(0);
        MaxIcon.Kind = max ? "i-win-restore" : "i-win-max";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, max ? "Restore" : "Maximize");
        if (WindowState != WindowState.Minimized)
            App.Service.UpdateSettings(s => s.WindowMaximized = max);
    }

    private void OnSize()
    {
        double w = ActualWidth;
        LayoutBand band = w >= 1180 ? LayoutBand.Wide : w >= 920 ? LayoutBand.Medium : w >= 720 ? LayoutBand.Narrow : LayoutBand.Tiny;
        DialogSurface.MaxHeight = Math.Max(200, ActualHeight - 32);
        if (band == Band && NavHost.Width > 0 && IsLoaded) return;
        Band = band;
        bool tiny = band == LayoutBand.Tiny;
        Crumb.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        foreach (Button b in CaptionButtons.Children.OfType<Button>()) b.Width = tiny ? 40 : 46;
        ApplyNavWidth(animate: false);
        foreach (FrameworkElement page in _pages.Values) ((IPage)page).OnBand(band);
        BandChanged?.Invoke(band);
    }

    /// <summary>Brings the window back from the tray or the taskbar.</summary>
    public void Restore()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = App.Service.Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        IndexService s = App.Service;
        if (WindowState == WindowState.Normal)
            s.UpdateSettings(x => { x.WindowWidth = ActualWidth; x.WindowHeight = ActualHeight; });
        if (_quitting) return;
        if (s.Settings.KeepRunningWhenClosed)
        {
            // Hide to the tray: watching and indexing carry on.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("DriveSync is still running", "It keeps your index up to date from the system tray.");
            }
            return;
        }
        e.Cancel = true;
        App.Quit();
    }

    /// <summary>Called by App.Quit: really close.</summary>
    public void Shutdown()
    {
        _quitting = true;
        _tick.Stop();
        _hotkey.Dispose();
        _tray?.Dispose();
        Close();
    }

    // ==================================================================
    // Actions shared by several places (tray, Home, Activity)
    // ==================================================================

    public void ToggleWatching()
    {
        IndexService s = App.Service;
        s.SetWatching(!s.Watching);
        Toasts.Show(s.Watching
            ? new Notice(NoticeKind.Ok, "Watching resumed", "DriveSync is tracking changes again.", Milliseconds: 3200)
            : new Notice(NoticeKind.Warn, "Watching paused", "Search still works, but new and changed files won't show up until you resume.", Milliseconds: 3200));
        CurrentPageObject?.OnDataChanged();
    }

    public async void RefreshNow()
    {
        IndexService s = App.Service;
        Task<JobOutcome>? job = s.TryRefresh();
        if (job == null) return;
        try
        {
            JobOutcome r = await job;
            int folders = s.Folders.Count(f => f.Status == RootStatus.Watching);
            Toasts.Show(new Notice(NoticeKind.Ok, "Index is up to date", $"{Fmt.Num(r.Files)} files checked in {folders} folder{(folders == 1 ? "" : "s")}."));
        }
        catch (Exception) { /* reported by the service */ }
    }
}

/// <summary>What the window tells each page.</summary>
public interface IPage
{
    void OnShown();
    void OnDataChanged();
    void OnTick();
    void OnBand(LayoutBand band);
}

/// <summary>Pages that show watcher events as they arrive.</summary>
public interface IActivityListener
{
    void OnActivity(ActivityEvent ev);
}
