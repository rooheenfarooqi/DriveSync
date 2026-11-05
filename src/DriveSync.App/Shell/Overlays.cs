using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DriveSync.App.Controls;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Shell;

/// <summary>
/// Toasts in the bottom-right corner (.toast): success, info, warning and error, with an optional action
/// (Undo, Search it…), a close button and auto-dismiss. At most four at a time, newest on top.
/// </summary>
public sealed class ToastHost
{
    private readonly StackPanel _panel;

    public ToastHost(StackPanel panel) => _panel = panel;

    public void Show(Notice notice)
    {
        (string icon, string bg, Color fg) = notice.Kind switch
        {
            NoticeKind.Ok => ("i-check", "OkBrush", Color.FromRgb(0x06, 0x21, 0x0f)),
            NoticeKind.Err => ("i-x", "ErrBrush", Color.FromRgb(0x2b, 0x06, 0x06)),
            NoticeKind.Warn => ("i-warn", "WarnBrush", Color.FromRgb(0x2b, 0x1d, 0x03)),
            _ => ("i-info", "AccentBrush", default),
        };

        var circle = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        circle.SetResourceReference(Border.BackgroundProperty, bg);
        var mark = new Icon { Kind = icon, Size = 12, Thickness = 2.4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (notice.Kind == NoticeKind.Info) mark.SetResourceReference(Icon.ForegroundProperty, "TextOnAccentBrush");
        else mark.Foreground = ThemeManager.Brush(fg);
        circle.Child = mark;

        var body = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        body.Children.Add(new TextBlock { Text = notice.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(notice.Message))
        {
            var msg = new TextBlock { Text = notice.Message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            body.Children.Add(msg);
        }

        var close = new UiButton { Variant = ButtonVariant.Ghost, Small = true, IconOnly = true, Icon = "i-x", Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top };
        close.SetResourceReference(Control.ForegroundProperty, "Text3Brush");
        System.Windows.Automation.AutomationProperties.SetName(close, "Dismiss");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(circle);
        grid.Children.Add(body);
        grid.Children.Add(close);

        var toast = new Border
        {
            Child = grid,
            Padding = new Thickness(16, 14, 14, 14),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0,
        };
        toast.SetResourceReference(Border.BackgroundProperty, "ToastBrush");
        toast.SetResourceReference(Border.BorderBrushProperty, "Stroke2Brush");
        toast.SetResourceReference(UIElement.EffectProperty, "ShadowMenu");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(toast, System.Windows.Automation.AutomationLiveSetting.Polite);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(notice.Milliseconds) };
        void Kill()
        {
            timer.Stop();
            if (!_panel.Children.Contains(toast)) return;
            var shift = toast.RenderTransform as TranslateTransform ?? new TranslateTransform();
            toast.RenderTransform = shift;
            Motion.To(shift, TranslateTransform.XProperty, 24, 250);
            Motion.To(toast, UIElement.OpacityProperty, 0, 250, done: () => _panel.Children.Remove(toast));
        }
        close.Click += (_, _) => Kill();
        if (notice.ActionLabel != null && notice.Action != null)
        {
            var action = new UiButton { Small = true, Content = notice.ActionLabel, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            action.Click += (_, _) => { notice.Action(); Kill(); };
            body.Children.Add(action);
        }
        // Hovering keeps it open.
        toast.MouseEnter += (_, _) => timer.Stop();
        toast.MouseLeave += (_, _) => timer.Start();
        timer.Tick += (_, _) => Kill();

        _panel.Children.Insert(0, toast);
        while (_panel.Children.Count > 4) _panel.Children.RemoveAt(_panel.Children.Count - 1);
        var enter = new TranslateTransform(24, 0);
        toast.RenderTransform = enter;
        Motion.To(enter, TranslateTransform.XProperty, 0, 300);
        Motion.To(toast, UIElement.OpacityProperty, 1, 300, 0);
        timer.Start();
    }
}

/// <summary>A dialog that is open; <see cref="Close"/> dismisses it.</summary>
public sealed class DialogHandle
{
    internal Action? CloseAction;
    public event Action? Closed;
    public bool IsOpen { get; internal set; } = true;

    /// <summary>When false, Esc and clicks on the scrim don't close the dialog.</summary>
    public bool Dismissable { get; set; } = true;

    public void Close() => CloseAction?.Invoke();
    internal void RaiseClosed() => Closed?.Invoke();
}

/// <summary>
/// Modal dialogs inside the window (.scrim + .dialog): the page behind dims and blurs, the dialog scales
/// in, Tab stays inside it, Esc or a click outside closes it, and focus goes back where it was.
/// </summary>
public sealed class DialogHost
{
    private readonly Grid _layer;
    private readonly Border _surface;
    private readonly UIElement _behind;
    private DialogHandle? _current;
    private IInputElement? _returnFocus;

    public DialogHost(Grid layer, Border surface, UIElement behind)
    {
        _layer = layer;
        _surface = surface;
        _behind = behind;
        _layer.MouseDown += (_, e) =>
        {
            if (e.OriginalSource == _layer && _current?.Dismissable == true) _current.Close();
        };
        _layer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _current != null && !FluentMenu.IsOpen)
            {
                if (_current.Dismissable) _current.Close();
                e.Handled = true;
            }
        };
    }

    public bool IsOpen => _current != null;

    public DialogHandle Show(FrameworkElement content, bool wide = false, string label = "Dialog")
    {
        _current?.Close();
        _returnFocus = Keyboard.FocusedElement;
        FluentMenu.Close();
        var handle = new DialogHandle();
        _current = handle;

        _surface.Child = content;
        _surface.MaxWidth = wide ? 760 : 520;
        System.Windows.Automation.AutomationProperties.SetName(_surface, label);
        _layer.Visibility = Visibility.Visible;
        _behind.IsHitTestVisible = false;
        _behind.Effect = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };

        Motion.To(_layer, UIElement.OpacityProperty, 1, 180, 0);
        var scale = new ScaleTransform(1.04, 1.04);
        _surface.RenderTransformOrigin = new Point(0.5, 0.5);
        _surface.RenderTransform = scale;
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, 220, 1.04);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, 220, 1.04);
        Motion.To(_surface, UIElement.OpacityProperty, 1, 220, 0);

        handle.CloseAction = () =>
        {
            if (!handle.IsOpen) return;
            handle.IsOpen = false;
            if (_current == handle)
            {
                _current = null;
                _layer.Visibility = Visibility.Collapsed;
                _surface.Child = null;
                _behind.IsHitTestVisible = true;
                _behind.Effect = null;
                if (_returnFocus is UIElement el && el.IsVisible) el.Focus();
            }
            handle.RaiseClosed();
        };

        content.Dispatcher.BeginInvoke(() =>
        {
            IInputElement? first = FindAutofocus(content) ?? FindFirstFocusable(content);
            if (first is UIElement u) u.Focus();
        }, DispatcherPriority.Input);
        return handle;
    }

    /// <summary>Elements marked with Tag="autofocus" get focus first, as in the prototype.</summary>
    private static IInputElement? FindAutofocus(DependencyObject root)
    {
        if (root is FrameworkElement fe && Equals(fe.Tag, "autofocus") && fe.Focusable && fe.IsVisible && fe.IsEnabled) return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindAutofocus(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static IInputElement? FindFirstFocusable(DependencyObject root)
    {
        if (root is Control c && c.Focusable && c.IsTabStop && c.IsVisible && c.IsEnabled) return c;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindFirstFocusable(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
