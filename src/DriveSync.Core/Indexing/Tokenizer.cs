using System.Runtime.CompilerServices;

namespace DriveSync.Core.Indexing;

/// <summary>
/// Splits names into lower-case search tokens:
/// "Final-Report_2026.docx" gives final, report, 2026, docx; "IndexEngine.cs" gives index, engine, cs;
/// "Lab03" gives lab, 03. Queries are split the same way, so "lab03" finds "Lab 03 - Docker.docx".
/// </summary>
public static class Tokenizer
{
    /// <summary>Calls <paramref name="onToken"/> for every token (already lower-case) in <paramref name="text"/>.</summary>
    public static void Tokenize(ReadOnlySpan<char> text, Span<char> buffer, TokenHandler onToken)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ' ';
            bool word = char.IsLetterOrDigit(c);
            if (start >= 0)
            {
                bool split = !word;
                if (!split)
                {
                    char prev = text[i - 1];
                    if (char.IsDigit(c) != char.IsDigit(prev))
                        split = true;                                   // letter/digit boundary
                    else if (char.IsUpper(c) && char.IsLower(prev))
                        split = true;                                   // camelCase
                    else if (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < text.Length && char.IsLower(text[i + 1]))
                        split = true;                                   // XMLParser -> XML, Parser
                }
                if (split)
                {
                    Emit(text.Slice(start, i - start), buffer, onToken);
                    start = word ? i : -1;
                }
            }
            else if (word)
            {
                start = i;
            }
        }
    }

    private static void Emit(ReadOnlySpan<char> token, Span<char> buffer, TokenHandler onToken)
    {
        if (token.Length > buffer.Length)
            token = token[..buffer.Length];
        Span<char> lower = buffer[..token.Length];
        token.ToLowerInvariant(lower);
        onToken(lower);
    }

    /// <summary>The tokens of a string as a list (allocates; used for queries and the Details tab).</summary>
    public static List<string> Tokens(string text)
    {
        var list = new List<string>();
        Span<char> buffer = stackalloc char[128];
        Tokenize(text, buffer, token => list.Add(token.ToString()));
        return list;
    }
}

public delegate void TokenHandler(ReadOnlySpan<char> token);

/// <summary>
/// A string table that can be searched with a span, so tokenizing a million names only allocates
/// a string for each distinct token. Not thread-safe.
/// </summary>
public sealed class SpanStringPool
{
    private int[] _buckets;
    private Entry[] _entries;
    private int _count;

    private struct Entry
    {
        public string Value;
        public int Hash;
        public int Next;
    }

    public SpanStringPool(int capacity = 1024)
    {
        int size = 1;
        while (size < capacity * 2) size <<= 1;
        _buckets = new int[size];
        Array.Fill(_buckets, -1);
        _entries = new Entry[capacity];
    }

    public int Count => _count;

    public string this[int id] => _entries[id].Value;

    /// <summary>Returns the id of <paramref name="value"/>, adding it if new.</summary>
    public int GetOrAdd(ReadOnlySpan<char> value)
    {
        int hash = Hash(value);
        int bucket = hash & (_buckets.Length - 1);
        for (int i = _buckets[bucket]; i >= 0; i = _entries[i].Next)
        {
            ref Entry e = ref _entries[i];
            if (e.Hash == hash && value.SequenceEqual(e.Value))
                return i;
        }

        if (_count == _entries.Length)
            Grow();
        bucket = hash & (_buckets.Length - 1);
        int id = _count++;
        _entries[id] = new Entry { Value = value.ToString(), Hash = hash, Next = _buckets[bucket] };
        _buckets[bucket] = id;
        return id;
    }

    /// <summary>The id of <paramref name="value"/>, or -1.</summary>
    public int Find(ReadOnlySpan<char> value)
    {
        int hash = Hash(value);
        for (int i = _buckets[hash & (_buckets.Length - 1)]; i >= 0; i = _entries[i].Next)
        {
            ref Entry e = ref _entries[i];
            if (e.Hash == hash && value.SequenceEqual(e.Value))
                return i;
        }
        return -1;
    }

    private void Grow()
    {
        Array.Resize(ref _entries, _entries.Length * 2);
        _buckets = new int[_entries.Length * 2];
        Array.Fill(_buckets, -1);
        for (int i = 0; i < _count; i++)
        {
            int bucket = _entries[i].Hash & (_buckets.Length - 1);
            _entries[i].Next = _buckets[bucket];
            _buckets[bucket] = i;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ReadOnlySpan<char> s)
    {
        // FNV-1a: fast and good enough for short tokens.
        uint h = 2166136261;
        foreach (char c in s)
            h = (h ^ c) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }
}
