using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DriveSync.App.Shell;
using DriveSync.Core;

namespace DriveSync.App.Controls;

/// <summary>
/// One icon from Themes/Icons.xaml, stroked like the design's SVG symbols (1.5 units on a 20-unit grid,
/// round caps and joins). Colour comes from the inherited Foreground.
/// </summary>
public sealed class Icon : FrameworkElement
{
    private static readonly Dictionary<string, Geometry?> Cache = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoxProperty = DependencyProperty.Register(nameof(Box), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(nameof(Spin), typeof(bool), typeof(Icon),
        new PropertyMetadata(false, (d, _) => ((Icon)d).UpdateSpin()));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double Box { get => (double)GetValue(BoxProperty); set => SetValue(BoxProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Spin { get => (bool)GetValue(SpinProperty); set => SetValue(SpinProperty, value); }

    public Icon()
    {
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateSpin();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public static Geometry? Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out Geometry? g))
            {
                g = Application.Current?.TryFindResource(key) as Geometry;
                g?.Freeze();
                Cache[key] = g;
            }
            return g;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        Geometry? g = Find(Kind);
        if (g == null) return;
        double scale = Size / Box;
        var pen = new Pen(Foreground, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, g);
        dc.Pop();
    }

    private void UpdateSpin()
    {
        if (!Spin)
        {
            RenderTransform = Transform.Identity;
            return;
        }
        var rotate = new RotateTransform(0);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = rotate;
        if (Motion.Enabled)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}

/// <summary>
/// The coloured file-type tile (.fg): a tinted rounded square with the type icon, and on the large
/// version an extension badge in the corner.
/// </summary>
public sealed class FileGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(FileKind), typeof(FileGlyph),
        new FrameworkPropertyMetadata(FileKind.Other, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExtensionProperty = DependencyProperty.Register(nameof(Extension), typeof(string), typeof(FileGlyph),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LargeProperty = DependencyProperty.Register(nameof(Large), typeof(bool), typeof(FileGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(FileGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(FileGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(nameof(IconKind), typeof(string), typeof(FileGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FileKind Kind { get => (FileKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Extension { get => (string)GetValue(ExtensionProperty); set => SetValue(ExtensionProperty, value); }
    public bool Large { get => (bool)GetValue(LargeProperty); set => SetValue(LargeProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    /// <summary>Overrides the type colour (folders use the accent).</summary>
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>Overrides the type icon ("i-folder", "i-drive"…).</summary>
    public string? IconKind { get => (string?)GetValue(IconKindProperty); set => SetValue(IconKindProperty, value); }

    private double Side => Large ? 52 : Compact ? 26 : 32;

    public FileGlyph()
    {
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    protected override void OnRender(DrawingContext dc)
    {
        double side = Side;
        Color c = Tint is SolidColorBrush sb ? sb.Color : ThemeManager.TypeColor(Kind);
        double radius = Large ? 12 : 8;
        var rect = new Rect(0.5, 0.5, side - 1, side - 1);
        dc.DrawRoundedRectangle(ThemeManager.Brush(ThemeManager.WithAlpha(c, 0.14)), new Pen(ThemeManager.Brush(ThemeManager.WithAlpha(c, 0.22)), 1), rect, radius, radius);

        Geometry? g = Icon.Find(IconKind ?? ThemeManager.TypeIcon(Kind));
        if (g != null)
        {
            double iconSize = Large ? 26 : Compact ? 14 : 17;
            double scale = iconSize / 20;
            double offset = (side - iconSize) / 2;
            var pen = new Pen(ThemeManager.Brush(c), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, pen, g);
            dc.Pop();
            dc.Pop();
        }

        if (Large && !string.IsNullOrEmpty(Extension))
        {
            string ext = Extension.ToUpperInvariant();
            if (ext.Length > 4) ext = ext[..4];
            var text = new FormattedText(ext, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface((FontFamily)Application.Current.Resources["FontMono"], FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                8.5, ThemeManager.Brush(Color.FromRgb(0x0b, 0x0f, 0x12)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double w = text.Width + 6, h = text.Height + 2;
            double x = side + 6 - w, y = side + 5 - h;
            dc.DrawRoundedRectangle(ThemeManager.Brush(c), null, new Rect(x, y, w, h), 3, 3);
            dc.DrawText(text, new Point(x + 3, y + 1));
        }
    }
}

/// <summary>Status dot (.dot): live, ok, warn, busy or err, with the soft 3-px ring and the pulse.</summary>
public sealed class Dot : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(Dot),
        new FrameworkPropertyMetadata("live", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Dot)d).UpdatePulse()));
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(nameof(Ring), typeof(bool), typeof(Dot),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(Dot),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public bool Ring { get => (bool)GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    public Dot()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { UpdatePulse(); ThemeManager.Changed += OnTheme; };
        Unloaded += (_, _) => ThemeManager.Changed -= OnTheme;
    }

    private void OnTheme() { InvalidateVisual(); UpdatePulse(); }

    private double Outer => Ring ? Diameter + 6 : Diameter;

    protected override Size MeasureOverride(Size availableSize) => new(Outer, Outer);

    protected override void OnRender(DrawingContext dc)
    {
        (string main, string soft) = State switch
        {
            "warn" => ("WarnBrush", "WarnSoftBrush"),
            "busy" => ("AccentBrush", "AccentSoftBrush"),
            "err" => ("ErrBrush", "ErrSoftBrush"),
            _ => ("OkBrush", "OkSoftBrush"),
        };
        var center = new Point(Outer / 2, Outer / 2);
        if (Ring) dc.DrawEllipse(ThemeManager.Res(soft), null, center, Outer / 2, Outer / 2);
        dc.DrawEllipse(ThemeManager.Res(main), null, center, Diameter / 2, Diameter / 2);
    }

    private void UpdatePulse()
    {
        if (!IsLoaded) return;
        if (State == "live") Motion.Pulse(this, 2.4);
        else if (State == "busy") Motion.Pulse(this, 1.0);
        else { BeginAnimation(OpacityProperty, null); Opacity = 1; }
    }
}

/// <summary>The 4-px progress bar (.bar), determinate or indeterminate.</summary>
public sealed class ProgressLine : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, (d, e) => ((ProgressLine)d).AnimateTo((double)e.NewValue)));
    public static readonly DependencyProperty IndeterminateProperty = DependencyProperty.Register(nameof(Indeterminate), typeof(bool), typeof(ProgressLine),
        new FrameworkPropertyMetadata(false, (d, _) => ((ProgressLine)d).UpdateIndeterminate()));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty SlideProperty = DependencyProperty.Register("Slide", typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Indeterminate { get => (bool)GetValue(IndeterminateProperty); set => SetValue(IndeterminateProperty, value); }

    public ProgressLine()
    {
        Height = 4;
        Loaded += (_, _) => UpdateIndeterminate();
    }

    private void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double current = (double)GetValue(ShownProperty);
        if (v < current || !Motion.Enabled) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, v); return; }
        BeginAnimation(ShownProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(300)));
    }

    private void UpdateIndeterminate()
    {
        if (Indeterminate && Motion.Enabled)
            BeginAnimation(SlideProperty, new DoubleAnimation(-0.35, 1.05, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Ease });
        else
            BeginAnimation(SlideProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        var track = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(track, h / 2, h / 2));
        dc.DrawRectangle(ThemeManager.Res("Layer3Brush"), null, track);
        Brush accent = ThemeManager.Res("AccentBrush");
        if (Indeterminate)
        {
            double slide = (double)GetValue(SlideProperty);
            dc.DrawRoundedRectangle(accent, null, new Rect(slide * w, 0, w * 0.35, h), h / 2, h / 2);
        }
        else
        {
            double shown = (double)GetValue(ShownProperty);
            if (shown > 0) dc.DrawRoundedRectangle(accent, null, new Rect(0, 0, w * shown, h), h / 2, h / 2);
        }
        dc.Pop();
    }
}

/// <summary>The loading shimmer (.skel): a 12-px bar with a moving highlight.</summary>
public sealed class Skeleton : FrameworkElement
{
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double), typeof(Skeleton),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Skeleton()
    {
        Height = 12;
        Margin = new Thickness(0, 0, 0, 10);
        Loaded += (_, _) =>
        {
            if (Motion.Enabled)
                BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0) return;
        Color l2 = ((SolidColorBrush)ThemeManager.Res("Layer2Brush")).Color, l3 = ((SolidColorBrush)ThemeManager.Res("Layer3Brush")).Color;
        double phase = (double)GetValue(PhaseProperty);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(-w * 2 * phase, 0),
            EndPoint = new Point(w * 2 - w * 2 * phase, 0),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        brush.GradientStops.Add(new GradientStop(l2, 0));
        brush.GradientStops.Add(new GradientStop(l3, 0.5));
        brush.GradientStops.Add(new GradientStop(l2, 1));
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, w, ActualHeight), 6, 6);
    }
}
