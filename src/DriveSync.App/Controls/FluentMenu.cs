using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DriveSync.App.Shell;

namespace DriveSync.App.Controls;

/// <summary>One row of a Fluent menu.</summary>
public sealed class MenuEntry
{
    public string Label { get; init; } = "";
    public string? Icon { get; init; }
    public string? Kbd { get; init; }

    /// <summary>Non-null makes it a radio item with a tick when true.</summary>
    public bool? Checked { get; init; }
    public bool Danger { get; init; }
    public bool IsSeparator { get; init; }
    public Action? Action { get; init; }

    public static MenuEntry Separator => new() { IsSeparator = true };
}

/// <summary>
/// The design's acrylic menus (.menu): dropdown filters, the result right-click menu, the More menu and
/// Shift+F10. Arrow keys move, Enter picks, Esc and Tab close, and focus returns to what opened it.
/// </summary>
public static class FluentMenu
{
    private static Popup? _popup;
    private static FrameworkElement? _anchor;
    private static DateTime _closedAt;
    private static FrameworkElement? _closedAnchor;

    public static bool IsOpen => _popup?.IsOpen == true;

    public static bool IsOpenFor(FrameworkElement anchor) =>
        (IsOpen && _anchor == anchor) || (_closedAnchor == anchor && (DateTime.Now - _closedAt).TotalMilliseconds < 200);

    public static void Close()
    {
        if (_popup != null) _popup.IsOpen = false;
    }

    /// <summary>Opens a menu under <paramref name="anchor"/>, or at <paramref name="screenPoint"/> (a right-click).</summary>
    public static void Show(FrameworkElement anchor, IReadOnlyList<MenuEntry> entries, Point? screenPoint = null, double minWidth = 200)
    {
        Close();
        var list = new StackPanel();
        var buttons = new List<Button>();
        foreach (MenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                var hr = new Border { Height = 1, Margin = new Thickness(2, 4, 2, 4) };
                hr.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
                list.Children.Add(hr);
                continue;
            }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (entry.Checked != null)
            {
                var tick = new Icon { Kind = "i-check", Size = 14, Margin = new Thickness(0, 0, 10, 0), Opacity = entry.Checked == true ? 1 : 0 };
                tick.SetResourceReference(Icon.ForegroundProperty, "AccentBrush");
                row.Children.Add(tick);
            }
            if (entry.Icon != null)
            {
                var icon = new Icon { Kind = entry.Icon, Size = 16, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetColumn(icon, 1);
                row.Children.Add(icon);
            }
            var text = new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            if (entry.Kbd != null)
            {
                var kbd = new TextBlock { Text = entry.Kbd, Margin = new Thickness(16, 0, 0, 0), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                kbd.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
                Grid.SetColumn(kbd, 3);
                row.Children.Add(kbd);
            }

            var button = new Button { Content = row, Tag = entry.Danger ? "danger" : null };
            button.SetResourceReference(FrameworkElement.StyleProperty, "MenuItemButton");
            AutomationName(button, entry.Label);
            MenuEntry captured = entry;
            button.Click += (_, _) =>
            {
                Close();
                captured.Action?.Invoke();
            };
            buttons.Add(button);
            list.Children.Add(button);
        }

        var surface = new Border
        {
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            MinWidth = minWidth,
            Child = list,
            Margin = new Thickness(18, 4, 18, 30), // room for the shadow
        };
        surface.SetResourceReference(Border.BackgroundProperty, "MenuBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "Stroke2Brush");
        surface.SetResourceReference(UIElement.EffectProperty, "ShadowMenu");
        TextElementSetup(surface);

        var popup = new Popup
        {
            Child = surface,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.None,
            PlacementTarget = anchor,
            Focusable = false,
        };
        if (screenPoint is Point p)
        {
            popup.Placement = PlacementMode.AbsolutePoint;
            popup.HorizontalOffset = p.X - 18;
            popup.VerticalOffset = p.Y - 4;
        }
        else
        {
            popup.Placement = PlacementMode.Bottom;
            popup.HorizontalOffset = -18;
            popup.VerticalOffset = 0;
        }

        surface.PreviewKeyDown += (_, e) =>
        {
            int i = buttons.FindIndex(b => b.IsKeyboardFocused);
            switch (e.Key)
            {
                case Key.Down: buttons[(i + 1) % buttons.Count].Focus(); e.Handled = true; break;
                case Key.Up: buttons[(i - 1 + buttons.Count) % buttons.Count].Focus(); e.Handled = true; break;
                case Key.Home: buttons[0].Focus(); e.Handled = true; break;
                case Key.End: buttons[^1].Focus(); e.Handled = true; break;
                case Key.Escape: case Key.Tab: Close(); e.Handled = true; break;
            }
        };

        popup.Opened += (_, _) =>
        {
            int current = entries.Where(x => !x.IsSeparator).ToList().FindIndex(x => x.Checked == true);
            Button first = buttons.Count > 0 ? buttons[Math.Max(0, current)] : null!;
            surface.Dispatcher.BeginInvoke(() => first?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            var shift = new TranslateTransform(0, -6);
            surface.RenderTransform = shift;
            Motion.To(surface, UIElement.OpacityProperty, 1, 160, 0);
            Motion.To(shift, TranslateTransform.YProperty, 0, 160, -6);
        };
        popup.Closed += (_, _) =>
        {
            if (anchor is DropDown dd) dd.IsOpen = false;
            _closedAt = DateTime.Now;
            _closedAnchor = anchor;
            if (_popup == popup) { _popup = null; _anchor = null; }
            if (anchor.IsVisible && anchor.Focusable) anchor.Focus();
        };

        _popup = popup;
        _anchor = anchor;
        if (anchor is DropDown d) d.IsOpen = true;
        popup.IsOpen = true;
    }

    private static void TextElementSetup(Border surface)
    {
        surface.SetResourceReference(TextBlock.FontFamilyProperty, "FontBody");
        TextBlock.SetFontSize(surface, 13);
        surface.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }

    private static void AutomationName(UIElement e, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(e, name);
}
