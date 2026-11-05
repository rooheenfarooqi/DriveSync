using System.Globalization;

namespace DriveSync.App.Shell;

/// <summary>Number, size, date and time formats from the prototype (fmtNum, fmtSize, rel, fmtDT, fmtTime, fmtMs).</summary>
public static class Fmt
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Gb = CultureInfo.GetCultureInfo("en-GB");

    public static string Num(long n) => n.ToString("N0", En);

    public static string Size(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1048576) return (bytes / 1024.0).ToString(bytes < 10240 ? "0.0" : "0", En) + " KB";
        if (bytes < 1073741824) return (bytes / 1048576.0).ToString("0.0", En) + " MB";
        return (bytes / 1073741824.0).ToString("0.00", En) + " GB";
    }

    /// <summary>"just now", "4 min ago", "3 h ago", "Yesterday", "5 days ago", "3 Nov", "3 Nov 2025".</summary>
    public static string Rel(DateTime t)
    {
        if (t == DateTime.MinValue) return "never";
        TimeSpan d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalHours < 1) return Math.Max(1, (int)Math.Round(d.TotalMinutes)) + " min ago";
        if (d.TotalDays < 1) return (int)Math.Round(d.TotalHours) + " h ago";
        if (d.TotalDays < 2) return "Yesterday";
        if (d.TotalDays < 7) return (int)Math.Round(d.TotalDays) + " days ago";
        return t.Year == DateTime.Now.Year ? t.ToString("d MMM", Gb) : t.ToString("d MMM yyyy", Gb);
    }

    public static string DateTimeLong(DateTime t) => t.ToString("d MMM yyyy, HH:mm", Gb);

    public static string Time(DateTime t) => t.ToString("HH:mm:ss", Gb);

    public static string Ms(double ms) =>
        ms < 0.1 ? ms.ToString("0.000", En) + " ms" :
        ms < 1 ? ms.ToString("0.00", En) + " ms" :
        ms < 10 ? ms.ToString("0.0", En) + " ms" :
        Math.Round(ms).ToString("0", En) + " ms";

    /// <summary>"Tuesday 3 November" for the Home eyebrow.</summary>
    public static string LongDay(DateTime t) => t.ToString("dddd d MMMM", Gb);

    public static string Plural(int n, string word) => $"{Num(n)} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"11:20" countdown for the next scheduled refresh.</summary>
    public static string Countdown(TimeSpan left)
    {
        int s = Math.Max(0, (int)Math.Round(left.TotalSeconds));
        return $"{s / 60}:{s % 60:00}";
    }
}
