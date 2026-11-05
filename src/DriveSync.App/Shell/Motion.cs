using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DriveSync.App.Shell;

/// <summary>
/// Every animation in the app goes through here, so Settings → Reduce motion (and the Windows
/// "Animation effects" setting) turns them all off at once. Durations follow the design:
/// 180 ms for state changes, 220–280 ms for views and dialogs, easing cubic-bezier(.2,.8,.2,1).
/// </summary>
public static class Motion
{
    public static bool Enabled => !ThemeManager.ReduceMotion && SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Ease = new CubicBezierEase(0.2, 0.8, 0.2, 1);

    public static Duration Dur(double ms) => Enabled ? new Duration(TimeSpan.FromMilliseconds(ms)) : new Duration(TimeSpan.Zero);

    /// <summary>Animates a double property; jumps straight to the end when motion is off.</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, double ms, double? from = null,
                          IEasingFunction? ease = null, Action? done = null)
    {
        if (!Enabled || ms <= 0)
        {
            target.BeginAnimation(property, null);
            if (target is DependencyObject d) d.SetValue(property, to);
            done?.Invoke();
            return;
        }
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = ease ?? Ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (from.HasValue) anim.From = from.Value;
        if (done != null) anim.Completed += (_, _) => done();
        target.BeginAnimation(property, anim);
    }

    /// <summary>The view entrance from the design: fade in while rising 8 px, 280 ms.</summary>
    public static void Enter(UIElement element, double dy = 8, double ms = 280)
    {
        var shift = new TranslateTransform(0, dy);
        element.RenderTransform = shift;
        To(element, UIElement.OpacityProperty, 1, ms, 0);
        To(shift, TranslateTransform.YProperty, 0, ms, dy);
    }

    /// <summary>Repeating opacity pulse (status dots), 1 → .45 → 1.</summary>
    public static void Pulse(UIElement element, double seconds)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(seconds) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromPercent(0.5)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}

/// <summary>CSS cubic-bezier() as a WPF easing function.</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        EasingMode = EasingMode.EaseIn; // EaseInCore is used as-is
    }

    protected override double EaseInCore(double t)
    {
        // Solve x(s) = t for s with Newton's method, then return y(s).
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bezier(s, _x1, _x2) - t;
            double dx = Derivative(s, _x1, _x2);
            if (Math.Abs(x) < 1e-5 || Math.Abs(dx) < 1e-6) break;
            s = Math.Clamp(s - x / dx, 0, 1);
        }
        return Bezier(s, _y1, _y2);
    }

    private static double Bezier(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;

    private static double Derivative(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * p1 + 6 * (1 - s) * s * (p2 - p1) + 3 * s * s * (1 - p2);

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);
}
