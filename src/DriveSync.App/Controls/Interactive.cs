using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DriveSync.App.Shell;

namespace DriveSync.App.Controls;

public enum ButtonVariant { Default, Primary, Danger, Ghost }

/// <summary>The design's .btn: Default, Primary, Danger and Ghost variants, small and icon-only sizes, an optional leading icon.</summary>
public class UiButton : Button
{
    static UiButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(UiButton), new FrameworkPropertyMetadata(typeof(UiButton)));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(nameof(Variant), typeof(ButtonVariant), typeof(UiButton), new PropertyMetadata(ButtonVariant.Default));
    public static readonly DependencyProperty SmallProperty = DependencyProperty.Register(nameof(Small), typeof(bool), typeof(UiButton), new PropertyMetadata(false));
    public static readonly DependencyProperty IconOnlyProperty = DependencyProperty.Register(nameof(IconOnly), typeof(bool), typeof(UiButton), new PropertyMetadata(false));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(UiButton), new PropertyMetadata(""));
    public static readonly DependencyProperty SpinIconProperty = DependencyProperty.Register(nameof(SpinIcon), typeof(bool), typeof(UiButton), new PropertyMetadata(false));

    public ButtonVariant Variant { get => (ButtonVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public bool Small { get => (bool)GetValue(SmallProperty); set => SetValue(SmallProperty, value); }
    public bool IconOnly { get => (bool)GetValue(IconOnlyProperty); set => SetValue(IconOnlyProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool SpinIcon { get => (bool)GetValue(SpinIconProperty); set => SetValue(SpinIconProperty, value); }
}

/// <summary>The design's toggle switch (40 × 20), with the knob sliding 20 px in 180 ms.</summary>
public class ToggleSwitch : ToggleButton
{
    static ToggleSwitch() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch), new FrameworkPropertyMetadata(typeof(ToggleSwitch)));

    private TranslateTransform? _knob;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Knob") is FrameworkElement knob)
        {
            _knob = new TranslateTransform(IsChecked == true ? 20 : 0, 0);
            knob.RenderTransform = _knob;
        }
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        if (_knob != null) Motion.To(_knob, TranslateTransform.XProperty, 20, 180);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        if (_knob != null) Motion.To(_knob, TranslateTransform.XProperty, 0, 180);
    }
}

/// <summary>Status and tag pills (.pill): ok, warn, err, acc and mut, with an optional icon or dot.</summary>
public class Pill : ContentControl
{
    static Pill() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Pill), new FrameworkPropertyMetadata(typeof(Pill)));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Pill), new PropertyMetadata("mut"));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(Pill), new PropertyMetadata(""));
    public static readonly DependencyProperty DotProperty = DependencyProperty.Register(nameof(Dot), typeof(string), typeof(Pill), new PropertyMetadata(""));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>"ok" or "busy" shows a 6-px dot before the text.</summary>
    public string Dot { get => (string)GetValue(DotProperty); set => SetValue(DotProperty, value); }
}

/// <summary>A keyboard key (kbd).</summary>
public class Kbd : ContentControl
{
    static Kbd() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Kbd), new FrameworkPropertyMetadata(typeof(Kbd)));
}

/// <summary>The design's .state: an icon tile, a title, a sentence and actions (the content).</summary>
public class EmptyState : ContentControl
{
    static EmptyState() => DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyState), new PropertyMetadata("i-search"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty ExtraProperty = DependencyProperty.Register(nameof(Extra), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <summary>"", "err" or "warn".</summary>
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    /// <summary>Optional content shown above the title (an image preview, a busy dot…).</summary>
    public object? Extra { get => GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }
}

/// <summary>A navigation item with the accent pill that grows in when it becomes the current page.</summary>
public class NavItem : Button
{
    static NavItem() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NavItem), new FrameworkPropertyMetadata(typeof(NavItem)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(NavItem), new PropertyMetadata(""));
    public static readonly DependencyProperty KbdProperty = DependencyProperty.Register(nameof(Kbd), typeof(string), typeof(NavItem), new PropertyMetadata(""));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(nameof(Badge), typeof(string), typeof(NavItem), new PropertyMetadata(""));
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(NavItem),
        new PropertyMetadata(false, (d, e) => ((NavItem)d).OnCurrentChanged((bool)e.NewValue)));
    public static readonly DependencyProperty CollapsedProperty = DependencyProperty.Register(nameof(Collapsed), typeof(bool), typeof(NavItem), new PropertyMetadata(false));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Kbd { get => (string)GetValue(KbdProperty); set => SetValue(KbdProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public bool IsCurrent { get => (bool)GetValue(IsCurrentProperty); set => SetValue(IsCurrentProperty, value); }
    public bool Collapsed { get => (bool)GetValue(CollapsedProperty); set => SetValue(CollapsedProperty, value); }

    private void OnCurrentChanged(bool current)
    {
        if (current && GetTemplateChild("PART_Pill") is FrameworkElement pill)
            Motion.To(pill, HeightProperty, 16, 250, 6);
    }
}

/// <summary>The preview's Summary / Outline / Details tabs, with the 3-px underline sliding to the selected tab.</summary>
public class TabBar : Grid
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly Border _line = new() { Height = 3, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _lineShift = new();
    private readonly List<Button> _buttons = new();
    private int _selected;

    public event Action<int>? SelectionChanged;

    public TabBar()
    {
        _line.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _line.RenderTransform = _lineShift;
        _line.Margin = new Thickness(0, 0, 0, -1);
        Children.Add(_row);
        Children.Add(_line);
        SizeChanged += (_, _) => PlaceLine(false);
    }

    public void SetItems(params string[] labels)
    {
        _row.Children.Clear();
        _buttons.Clear();
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var b = new Button { Content = labels[i] };
            b.SetResourceReference(StyleProperty, "TabButton");
            b.Click += (_, _) => Select(index, true);
            _buttons.Add(b);
            _row.Children.Add(b);
        }
        Select(0, false);
    }

    public int Selected => _selected;

    public void Select(int index, bool raise)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _buttons.Count - 1));
        for (int i = 0; i < _buttons.Count; i++)
            _buttons[i].Tag = i == _selected ? "on" : null;
        PlaceLine(true);
        if (raise) SelectionChanged?.Invoke(_selected);
    }

    private void PlaceLine(bool animate)
    {
        if (_buttons.Count == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Button b = _buttons[_selected];
            if (b.ActualWidth <= 0) return;
            double x = b.TranslatePoint(new Point(0, 0), this).X + 10;
            double w = Math.Max(0, b.ActualWidth - 20);
            if (animate && _line.Width > 0)
            {
                // The design's tabin: grow out from the middle of the tab.
                Motion.To(_lineShift, TranslateTransform.XProperty, x, 200, x + w * 0.45);
                Motion.To(_line, WidthProperty, w, 200, w * 0.1);
            }
            else
            {
                _lineShift.BeginAnimation(TranslateTransform.XProperty, null);
                _line.BeginAnimation(WidthProperty, null);
                _lineShift.X = x;
                _line.Width = w;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}

/// <summary>A choice in a dropdown.</summary>
public sealed record DropOption(object Value, string Label);

/// <summary>The filter and settings dropdowns (.dd-btn): "Type  All types ⌄", opening a Fluent menu with a tick on the current choice.</summary>
public class DropDown : Button
{
    static DropDown() => DefaultStyleKeyProperty.OverrideMetadata(typeof(DropDown), new FrameworkPropertyMetadata(typeof(DropDown)));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(DropDown), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueLabelProperty = DependencyProperty.Register(nameof(ValueLabel), typeof(string), typeof(DropDown), new PropertyMetadata(""));
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DropDown),
        new PropertyMetadata(false, (d, e) => ((DropDown)d).RotateChevron((bool)e.NewValue)));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ValueLabel { get => (string)GetValue(ValueLabelProperty); set => SetValue(ValueLabelProperty, value); }
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    private List<DropOption> _options = new();
    private object? _value;

    /// <summary>Raised when the user picks a different option.</summary>
    public event Action<object>? Changed;

    /// <summary>Extra entries after the options (for example "Browse…" in the Folder filter).</summary>
    public List<MenuEntry> ExtraEntries { get; } = new();

    public object? Value
    {
        get => _value;
        set
        {
            _value = value;
            ValueLabel = _options.FirstOrDefault(o => Equals(o.Value, value))?.Label ?? _options.FirstOrDefault()?.Label ?? "";
        }
    }

    public void SetOptions(IEnumerable<DropOption> options, object? value)
    {
        _options = options.ToList();
        Value = value;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (FluentMenu.IsOpenFor(this)) { FluentMenu.Close(); return; }
        var entries = _options.Select(o => new MenuEntry
        {
            Label = o.Label,
            Checked = Equals(o.Value, _value),
            Action = () =>
            {
                bool changed = !Equals(o.Value, _value);
                Value = o.Value;
                if (changed) Changed?.Invoke(o.Value);
            },
        }).ToList();
        if (ExtraEntries.Count > 0)
        {
            entries.Add(MenuEntry.Separator);
            entries.AddRange(ExtraEntries);
        }
        FluentMenu.Show(this, entries, minWidth: Math.Max(200, ActualWidth));
    }

    private void RotateChevron(bool open)
    {
        if (GetTemplateChild("PART_Chevron") is FrameworkElement chevron && chevron.RenderTransform is RotateTransform rotate)
            Motion.To(rotate, RotateTransform.AngleProperty, open ? 180 : 0, 200);
    }
}

/// <summary>Attached helpers for text boxes: placeholder text and the error state.</summary>
public static class Field
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Field), new PropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.RegisterAttached("IsInvalid", typeof(bool), typeof(Field), new PropertyMetadata(false));
    public static bool GetIsInvalid(DependencyObject d) => (bool)d.GetValue(IsInvalidProperty);
    public static void SetIsInvalid(DependencyObject d, bool value) => d.SetValue(IsInvalidProperty, value);
}

/// <summary>Builds a TextBlock's inlines with the matched parts of a name in bold accent (the design's mark).</summary>
public static class Highlight
{
    public static readonly DependencyProperty SpecProperty = DependencyProperty.RegisterAttached("Spec", typeof(HighlightSpec), typeof(Highlight),
        new PropertyMetadata(null, (d, e) => Apply((TextBlock)d, e.NewValue as HighlightSpec)));
    public static HighlightSpec? GetSpec(DependencyObject d) => (HighlightSpec?)d.GetValue(SpecProperty);
    public static void SetSpec(DependencyObject d, HighlightSpec? value) => d.SetValue(SpecProperty, value);

    private static void Apply(TextBlock block, HighlightSpec? spec)
    {
        block.Inlines.Clear();
        if (spec == null) return;
        int pos = 0;
        foreach (var (start, length) in spec.Ranges)
        {
            if (start > pos) block.Inlines.Add(new Run(spec.Text[pos..start]));
            var run = new Run(spec.Text.Substring(start, length)) { FontWeight = FontWeights.Bold };
            run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            block.Inlines.Add(run);
            pos = start + length;
        }
        if (pos < spec.Text.Length) block.Inlines.Add(new Run(spec.Text[pos..]));
    }
}

public sealed record HighlightSpec(string Text, IReadOnlyList<(int Start, int Length)> Ranges);
