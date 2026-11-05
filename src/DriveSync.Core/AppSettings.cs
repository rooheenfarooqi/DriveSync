namespace DriveSync.Core;

/// <summary>
/// Everything on the Settings page. Saved as JSON in the settings table of the SQLite database.
/// Defaults are the ones in the design.
/// </summary>
public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; } = true;
    public bool KeepRunningWhenClosed { get; set; } = true;
    public string Hotkey { get; set; } = "Alt+Space";   // "Alt+Space", "Ctrl+Shift+F", "Win+Alt+S" or "off"
    public bool AutoUpdate { get; set; } = true;        // check GitHub Releases and download new versions in the background

    // Indexing
    public bool DetectChangesInstantly { get; set; } = true;
    public int RefreshMinutes { get; set; } = 15;       // 5, 15, 30, 60 or 240
    public int MaxParseMb { get; set; } = 50;           // 1..500
    public bool IncludeHidden { get; set; }
    public bool PauseOnBatterySaver { get; set; } = true;

    // Search & preview
    public bool SummarizeOnSelect { get; set; } = true;
    public int SummaryLength { get; set; } = 3;         // 2, 3 or 5 sentences
    public bool ShowFullPaths { get; set; } = true;
    public int ResultsPerPage { get; set; } = 100;      // 50, 100 or 200
    public bool RememberHistory { get; set; } = true;

    // Appearance
    public string Accent { get; set; } = "#3fd0c0";
    public double Glass { get; set; } = 0.74;           // window surface alpha, 0.55..0.97
    public bool ReduceMotion { get; set; }
    public bool CompactRows { get; set; }

    // Window and first run
    public bool NavCollapsed { get; set; }
    public bool Onboarded { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Restore defaults keeps the things that aren't preferences (first-run state, window size).</summary>
    public AppSettings Defaults() => new()
    {
        Onboarded = Onboarded,
        NavCollapsed = NavCollapsed,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowMaximized = WindowMaximized,
    };
}
