using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Controls;

/// <summary>Small builders for pieces that pages assemble in code (pills, glyphs, chips, text).</summary>
public static class Build
{
    public static TextBlock Text(string text, string? style = null, double? size = null, string? brush = null, FontWeight? weight = null,
                                 bool trim = false, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text };
        if (style != null) t.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (size != null) t.FontSize = size.Value;
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (weight != null) t.FontWeight = weight.Value;
        if (trim) { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }
        if (margin != null) t.Margin = margin.Value;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    public static TextBlock Mono(string text, double size = 11.5, string brush = "Text3Brush", bool trim = true)
    {
        var t = Text(text, size: size, brush: brush, trim: trim);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
        return t;
    }

    public static Pill Pill(string kind, string text, string icon = "", string dot = "") =>
        new() { Kind = kind, Content = text, Icon = icon, Dot = dot };

    public static Pill EventPill(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => Pill("ok", "Added"),
        ChangeKind.Modified => Pill("acc", "Modified"),
        ChangeKind.Renamed => Pill("mut", "Renamed"),
        _ => Pill("err", "Removed"),
    };

    public static Pill FolderPill(IndexedFolder f, JobInfo? job) => f.Status switch
    {
        RootStatus.Scanning => Pill("acc", $"Scanning {job?.Percent ?? 0}%", dot: "busy"),
        RootStatus.Paused => Pill("mut", "Paused", "i-pause"),
        RootStatus.Unreachable => Pill("err", "Drive not found", "i-warn"),
        _ => Pill("ok", "Watching", dot: "ok"),
    };

    public static FileGlyph Glyph(FileKind kind, string ext = "", bool large = false) =>
        new() { Kind = kind, Extension = ext, Large = large, Compact = !large && ThemeManager.Compact, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>An accent-tinted glyph (folders, drives, onboarding features).</summary>
    public static FileGlyph AccentGlyph(string icon, string tint = "AccentBrush", bool large = false)
    {
        var g = new FileGlyph { IconKind = icon, Large = large, VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(FileGlyph.TintProperty, tint);
        return g;
    }

    public static Button Chip(string text, string? icon, Action onClick, bool mono = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) row.Children.Add(new Icon { Kind = icon, Size = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (mono) { label.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono"); label.FontSize = 11.5; }
        row.Children.Add(label);
        var b = new Button { Content = row, Margin = new Thickness(0, 0, 8, 8) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static UiButton Button(string text, string icon = "", ButtonVariant variant = ButtonVariant.Default, bool small = false, Action? onClick = null)
    {
        var b = new UiButton { Content = text, Icon = icon, Variant = variant, Small = small };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static UiButton IconButton(string icon, string tip, Action onClick, bool small = true, ButtonVariant variant = ButtonVariant.Default)
    {
        var b = new UiButton { Icon = icon, IconOnly = true, Small = small, Variant = variant, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Border Separator(Thickness margin)
    {
        var b = new Border { Height = 1, Margin = margin };
        b.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
        return b;
    }

    /// <summary>A key/value row with a bottom rule (.kpi).</summary>
    public static Border Kpi(string label, UIElement value, bool last = false)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(value);
        dock.Children.Add(Text(label, brush: "Text2Brush"));
        var b = new Border { Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = dock };
        b.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return b;
    }

    public static TextBlock KpiValue(string text) => Text(text, weight: FontWeights.SemiBold);

    /// <summary>The infobar (.infobar): warn, err or info, an icon, text and optional action buttons.</summary>
    public static Border Infobar(string kind, string icon, UIElement text, params UIElement[] actions)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = new Icon { Kind = icon, Size = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(ic);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        foreach (UIElement a in actions) acts.Children.Add(a);
        Grid.SetColumn(acts, 2);
        grid.Children.Add(acts);
        var bar = new Border { Child = grid, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        TextBlock.SetFontSize(bar, 12.5);
        (string bg, string border, string fg) = kind switch
        {
            "warn" => ("WarnSoftBrush", "WarnLineBrush", "WarnTextBrush"),
            "err" => ("ErrSoftBrush", "ErrLineBrush", "ErrTextBrush"),
            _ => ("AccentSoftBrush", "AccentLineBrush", "TextBrush"),
        };
        bar.SetResourceReference(Border.BackgroundProperty, bg);
        bar.SetResourceReference(Border.BorderBrushProperty, border);
        bar.SetResourceReference(TextBlock.ForegroundProperty, fg);
        ic.SetResourceReference(Icon.ForegroundProperty, fg);
        return bar;
    }

    /// <summary>A TextBlock made of runs, some bold: Rich(("E:\\ isn't connected.", true), (" 120 files…", false)).</summary>
    public static TextBlock Rich(params (string Text, bool Bold)[] parts)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, bold) in parts)
            t.Inlines.Add(new System.Windows.Documents.Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        return t;
    }

    /// <summary>Row of keyboard keys: "Ctrl K" gives [Ctrl] [K].</summary>
    public static StackPanel Keys(string keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (string k in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            row.Children.Add(new Kbd { Content = k, Margin = new Thickness(0, 0, 4, 0) });
        return row;
    }

    public static string Greeting()
    {
        int h = DateTime.Now.Hour;
        return h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
    }

    public static Brush Res(string key) => ThemeManager.Res(key);
}
