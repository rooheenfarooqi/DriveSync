using System.Collections;
using System.Windows;
using System.Windows.Controls;
using DriveSync.App.Controls;
using DriveSync.App.Shell;
using DriveSync.Core;
using DriveSync.Core.Indexing;

namespace DriveSync.App.Pages;

/// <summary>One row of the results list, formatted for display.</summary>
public sealed class ResultRow
{
    public int Id { get; init; }
    public FileView File { get; init; }
    public HighlightSpec NameSpec { get; init; } = new("", Array.Empty<(int, int)>());
    public string Path { get; init; } = "";
    public string Modified { get; init; } = "";
    public string Size { get; init; } = "";
    public FileKind Kind { get; init; }
    public bool IsNew { get; init; }
    public bool Offline { get; init; }
    public bool Compact { get; init; }
    public Visibility PathVisibility { get; init; }
    public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineVisibility => Offline ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModifiedVisibility => Offline ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>The "Show 100 more of 2,340" row at the end of the list.</summary>
public sealed class MoreRow
{
    public string Label { get; init; } = "";
}

/// <summary>
/// The results as a list that builds each row only when WPF asks for it, so a virtualized ListBox can
/// show 100,000+ results while only the visible rows exist.
/// </summary>
public sealed class LazyRows : IList
{
    private readonly int[] _ids;
    private readonly Func<int, ResultRow> _make;
    private readonly MoreRow? _more;
    private readonly Dictionary<int, ResultRow> _made = new();

    public LazyRows(int[] ids, Func<int, ResultRow> make, MoreRow? more)
    {
        _ids = ids;
        _make = make;
        _more = more;
    }

    public int Count => _ids.Length + (_more != null ? 1 : 0);

    public int IndexOfId(int id) => Array.IndexOf(_ids, id);

    public int IdAt(int index) => _ids[index];

    public int RowCount => _ids.Length;

    public object? this[int index]
    {
        get
        {
            if (index == _ids.Length && _more != null) return _more;
            if (!_made.TryGetValue(index, out ResultRow? row))
                _made[index] = row = _make(_ids[index]);
            return row;
        }
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value) => value switch
    {
        ResultRow r => _made.FirstOrDefault(kv => ReferenceEquals(kv.Value, r)).Value != null ? _made.First(kv => ReferenceEquals(kv.Value, r)).Key : -1,
        MoreRow => _more != null ? _ids.Length : -1,
        _ => -1,
    };
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
}

/// <summary>Picks the row template or the "Show more" template.</summary>
public sealed class ResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Row { get; set; }
    public DataTemplate? More { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is MoreRow ? More : Row;
}

/// <summary>Lays out a name and a tag after it; the name is trimmed first so the tag always fits.</summary>
public sealed class InlineTagPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count < 2) return base.MeasureOverride(available);
        UIElement name = Children[0], tag = Children[1];
        tag.Measure(available);
        double tagWidth = tag.Visibility == Visibility.Collapsed ? 0 : tag.DesiredSize.Width;
        name.Measure(new Size(Math.Max(0, available.Width - tagWidth), available.Height));
        return new Size(Math.Min(available.Width, name.DesiredSize.Width + tagWidth), Math.Max(name.DesiredSize.Height, tag.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count < 2) return base.ArrangeOverride(final);
        UIElement name = Children[0], tag = Children[1];
        double tagWidth = tag.Visibility == Visibility.Collapsed ? 0 : tag.DesiredSize.Width;
        double nameWidth = Math.Min(name.DesiredSize.Width, Math.Max(0, final.Width - tagWidth));
        name.Arrange(new Rect(0, 0, nameWidth, final.Height));
        tag.Arrange(new Rect(nameWidth, (final.Height - tag.DesiredSize.Height) / 2, tagWidth, tag.DesiredSize.Height));
        return final;
    }
}

/// <summary>Match highlighting (the prototype's highlight()).</summary>
public static class Highlighter
{
    /// <summary>
    /// For each query term, marks its first occurrence that starts a word (start of name, after a
    /// separator, or at a capital letter). For close matches, marks the typed letters as one piece if they
    /// appear that way.
    /// </summary>
    public static HighlightSpec For(string name, ParsedQuery query, bool fuzzy)
    {
        var marks = new bool[name.Length];
        string low = name.ToLowerInvariant();
        if (fuzzy)
        {
            string needle = string.Concat(query.Terms);
            int at = low.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0) for (int k = at; k < at + needle.Length; k++) marks[k] = true;
        }
        else
        {
            foreach (string t in query.Terms)
            {
                int i = -1;
                while ((i = low.IndexOf(t, i + 1, StringComparison.Ordinal)) != -1)
                {
                    bool boundary = i == 0 || !char.IsLetterOrDigit(low[i - 1]) || char.IsUpper(name[i]) ||
                                    char.IsDigit(name[i]) != char.IsDigit(name[i - 1]);
                    if (boundary)
                    {
                        for (int k = i; k < i + t.Length && k < name.Length; k++) marks[k] = true;
                        break;
                    }
                }
            }
        }
        var ranges = new List<(int, int)>();
        for (int i = 0; i < marks.Length;)
        {
            if (!marks[i]) { i++; continue; }
            int start = i;
            while (i < marks.Length && marks[i]) i++;
            ranges.Add((start, i - start));
        }
        return new HighlightSpec(name, ranges);
    }
}
