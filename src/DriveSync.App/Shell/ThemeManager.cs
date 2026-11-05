using System.Windows;
using System.Windows.Media;
using DriveSync.Core;

namespace DriveSync.App.Shell;

/// <summary>
/// Applies Settings → Appearance live: accent colour (and every brush derived from it, as the design's
/// color-mix() does), window transparency, reduce motion and compact rows.
/// </summary>
public static class ThemeManager
{
    public static readonly (string Hex, string Name)[] Accents =
    {
        ("#3fd0c0", "Teal"), ("#7fd6ff", "Sky"), ("#8f9bff", "Iris"), ("#f0b453", "Amber"), ("#ff8fab", "Rose"), ("#b8e986", "Lime"),
    };

    private static readonly string[] LightAccents = { "#f0b453", "#3fd0c0", "#7fd6ff", "#b8e986" };

    public static bool ReduceMotion { get; private set; }
    public static bool Compact { get; private set; }
    public static bool BackdropActive { get; set; }
    public static double Glass { get; private set; } = 0.74;

    /// <summary>Raised after any appearance change (rows re-measure, dots restart their pulse…).</summary>
    public static event Action? Changed;

    public static void Apply(AppSettings s)
    {
        SetAccent(s.Accent);
        SetGlass(s.Glass);
        ReduceMotion = s.ReduceMotion;
        Compact = s.CompactRows;
        Changed?.Invoke();
    }

    public static void SetAccent(string hex)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { accent = Color.FromRgb(0x3f, 0xd0, 0xc0); }

        ResourceDictionary r = Application.Current.Resources;
        r["AccentColor"] = accent;
        r["AccentBrush"] = Brush(accent);
        r["AccentSoftBrush"] = Brush(WithAlpha(accent, 0.15));
        r["AccentLineBrush"] = Brush(WithAlpha(accent, 0.45));
        r["AccentFaintBrush"] = Brush(WithAlpha(accent, 0.38));
        r["AccentHoverBrush"] = Brush(Mix(accent, Colors.White, 0.88));
        r["AccentPressedBrush"] = Brush(Mix(accent, Colors.Black, 0.80));
        r["AccentBorderBrush"] = Brush(Mix(accent, Colors.White, 0.70));
        bool light = LightAccents.Contains(hex.ToLowerInvariant());
        r["TextOnAccentBrush"] = Brush(light ? Color.FromRgb(0x06, 0x24, 0x21) : Colors.White);
    }

    /// <summary>The Transparency slider: window surface alpha from .55 (clear) to .97 (solid). Solid when Mica isn't available.</summary>
    public static void SetGlass(double alpha)
    {
        Glass = Math.Clamp(alpha, 0.55, 0.97);
        double a = BackdropActive ? Glass : 1.0;
        Application.Current.Resources["WindowSurfaceBrush"] = Brush(Color.FromArgb((byte)Math.Round(a * 255), 0x10, 0x15, 0x1B));
    }

    public static void SetReduceMotion(bool on)
    {
        ReduceMotion = on;
        Changed?.Invoke();
    }

    public static void SetCompact(bool on)
    {
        Compact = on;
        Changed?.Invoke();
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    /// <summary>color-mix(in srgb, a p%, b).</summary>
    public static Color Mix(Color a, Color b, double p) => Color.FromRgb(
        (byte)Math.Round(a.R * p + b.R * (1 - p)),
        (byte)Math.Round(a.G * p + b.G * (1 - p)),
        (byte)Math.Round(a.B * p + b.B * (1 - p)));

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Color TypeColor(FileKind kind) => (Color)Application.Current.Resources[kind switch
    {
        FileKind.Doc => "TypeDocColor",
        FileKind.Pdf => "TypePdfColor",
        FileKind.Sheet => "TypeSheetColor",
        FileKind.Slides => "TypeSlidesColor",
        FileKind.Code => "TypeCodeColor",
        FileKind.Image => "TypeImageColor",
        FileKind.Db => "TypeDbColor",
        FileKind.Zip => "TypeZipColor",
        _ => "TypeTextColor",
    }];

    public static string TypeIcon(FileKind kind) => kind switch
    {
        FileKind.Sheet => "i-sheet",
        FileKind.Slides => "i-slides",
        FileKind.Code => "i-code",
        FileKind.Text => "i-text",
        FileKind.Image => "i-image",
        FileKind.Db => "i-db",
        FileKind.Zip => "i-zip",
        _ => "i-doc",
    };
}
