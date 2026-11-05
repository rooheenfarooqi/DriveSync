using System.Windows;
using System.Windows.Controls;
using DriveSync.App.Controls;
using DriveSync.App.Shell;

namespace DriveSync.App.Dialogs;

/// <summary>The design's dialog layout (.dialog): a 24-px body with title and description, and a tinted footer of buttons.</summary>
public sealed class DialogFrame
{
    public Grid Root { get; } = new();
    public StackPanel Body { get; } = new();
    public StackPanel Footer { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    public DialogHandle? Handle { get; set; }

    public DialogFrame(string? title, string? description)
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = new Border { Padding = new Thickness(24), Child = Body } };
        Root.Children.Add(scroll);
        var footer = new Border { Padding = new Thickness(24, 16, 24, 16), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 16, 16), Child = Footer };
        footer.SetResourceReference(Border.BackgroundProperty, "FooterBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        Grid.SetRow(footer, 1);
        Root.Children.Add(footer);

        if (title != null) Body.Children.Add(Build.Text(title, style: "DialogTitle"));
        if (description != null)
        {
            var d = Build.Text(description, style: "Muted", margin: new Thickness(0, 0, 0, 16));
            d.TextWrapping = TextWrapping.Wrap;
            Body.Children.Add(d);
        }
    }

    public UiButton AddButton(string text, ButtonVariant variant = ButtonVariant.Default, string icon = "", Action? click = null, bool autofocus = false)
    {
        var b = new UiButton { Content = text, Variant = variant, Icon = icon, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (autofocus) b.Tag = "autofocus";
        if (click != null) b.Click += (_, _) => click();
        Footer.Children.Add(b);
        return b;
    }

    public DialogHandle Show(bool wide = false, string label = "Dialog")
    {
        Handle = Ui.Dialog(Root, wide, label);
        return Handle;
    }

    public void Close() => Handle?.Close();
}

/// <summary>Confirmations: Remove folder, Rebuild index, Clear index (with an acknowledgement checkbox).</summary>
public static class ConfirmDialog
{
    public sealed class Options
    {
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string? Acknowledgement { get; init; }
        public UIElement? Extra { get; init; }
        public string ConfirmLabel { get; init; } = "OK";
        public string ConfirmIcon { get; init; } = "";
        public ButtonVariant Variant { get; init; } = ButtonVariant.Primary;
        public bool FocusConfirm { get; init; }
        public Action? OnConfirm { get; init; }
    }

    public static void Show(Options o)
    {
        var frame = new DialogFrame(o.Title, o.Description);
        if (o.Extra != null) frame.Body.Children.Add(o.Extra);
        CheckBox? ack = null;
        if (o.Acknowledgement != null)
        {
            ack = new CheckBox { Content = o.Acknowledgement, Margin = new Thickness(0, 0, 0, 0) };
            ack.SetResourceReference(FrameworkElement.StyleProperty, "Check");
            frame.Body.Children.Add(ack);
        }
        frame.AddButton("Cancel", click: frame.Close, autofocus: !o.FocusConfirm);
        UiButton ok = frame.AddButton(o.ConfirmLabel, o.Variant, o.ConfirmIcon, () =>
        {
            frame.Close();
            o.OnConfirm?.Invoke();
        }, autofocus: o.FocusConfirm);
        if (ack != null)
        {
            ok.IsEnabled = false;
            ack.Checked += (_, _) => ok.IsEnabled = true;
            ack.Unchecked += (_, _) => ok.IsEnabled = false;
        }
        frame.Show(label: o.Title);
    }
}

/// <summary>Every shortcut DriveSync understands (opened with ? or from Settings → About).</summary>
public static class ShortcutsDialog
{
    public static void Show()
    {
        var frame = new DialogFrame("Keyboard shortcuts", "DriveSync works without a mouse.");
        (string Keys, string What)[] rows =
        {
            ("Ctrl K|/", "Jump to search"), ("↑ ↓", "Move through results"), ("Enter", "Open the selected file"),
            ("Ctrl Shift C", "Copy the selected file's path"), ("Shift F10", "More actions for the selected file"),
            ("Ctrl ,", "Open Settings"), ("Alt 1 – 6", "Switch between pages"), ("?", "Show this list"), ("Esc", "Close a dialog or menu"),
        };
        string hotkey = App.Service.Settings.Hotkey;
        var list = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            var keys = new StackPanel { Orientation = Orientation.Horizontal };
            string[] alternatives = rows[i].Keys.Split('|');
            for (int a = 0; a < alternatives.Length; a++)
            {
                if (a > 0) keys.Children.Add(Build.Text(" or ", size: 12, brush: "Text3Brush", margin: new Thickness(0, 0, 4, 0)));
                keys.Children.Add(Build.Keys(alternatives[a]));
            }
            list.Children.Add(Build.Kpi(rows[i].What, keys, last: i == rows.Length - 1 && hotkey == "off"));
        }
        if (hotkey != "off")
            list.Children.Add(Build.Kpi("Open search from anywhere", Build.Keys(hotkey.Replace("+", " ")), last: true));
        frame.Body.Children.Add(list);
        frame.AddButton("Done", ButtonVariant.Primary, click: frame.Close, autofocus: true);
        frame.Show(label: "Keyboard shortcuts");
    }
}
