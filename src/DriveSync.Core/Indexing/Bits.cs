using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DriveSync.Core.Indexing;

/// <summary>Small helpers for bitsets stored as ulong arrays (one bit per file).</summary>
internal static class Bits
{
    public static int WordsFor(int bits) => (bits + 63) >> 6;

    public static ulong[] Rent(int bits)
    {
        int words = WordsFor(bits);
        ulong[] array = ArrayPool<ulong>.Shared.Rent(Math.Max(words, 1));
        Array.Clear(array, 0, words);
        return array;
    }

    public static void Return(ulong[]? array)
    {
        if (array != null)
            ArrayPool<ulong>.Shared.Return(array);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(ulong[] bits, int i) => bits[i >> 6] |= 1UL << (i & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(ulong[] bits, int i) => bits[i >> 6] &= ~(1UL << (i & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Get(ulong[] bits, int i) => (bits[i >> 6] & (1UL << (i & 63))) != 0;

    /// <summary>Sets bits [start, end).</summary>
    public static void SetRange(ulong[] bits, int start, int end)
    {
        if (start >= end)
            return;
        int first = start >> 6, last = (end - 1) >> 6;
        ulong firstMask = ~0UL << (start & 63);
        ulong lastMask = ~0UL >> (63 - ((end - 1) & 63));
        if (first == last)
        {
            bits[first] |= firstMask & lastMask;
            return;
        }
        bits[first] |= firstMask;
        for (int w = first + 1; w < last; w++)
            bits[w] = ~0UL;
        bits[last] |= lastMask;
    }

    /// <summary>target &= source over the first <paramref name="words"/> words.</summary>
    public static void And(ulong[] target, ulong[] source, int words)
    {
        for (int w = 0; w < words; w++)
            target[w] &= source[w];
    }

    /// <summary>target &= ~source.</summary>
    public static void AndNot(ulong[] target, ulong[] source, int words)
    {
        for (int w = 0; w < words; w++)
            target[w] &= ~source[w];
    }

    public static int Count(ulong[] bits, int words)
    {
        int n = 0;
        for (int w = 0; w < words; w++)
            n += BitOperations.PopCount(bits[w]);
        return n;
    }

    /// <summary>Counts set bits in [start, end).</summary>
    public static int CountRange(ulong[] bits, int start, int end)
    {
        int n = 0;
        for (int i = start; i < end && (i & 63) != 0; i++)
            if (Get(bits, i)) n++;
        int s = (start + 63) & ~63;
        int wordEnd = end >> 6;
        for (int w = s >> 6; w < wordEnd; w++)
            n += BitOperations.PopCount(bits[w]);
        for (int i = Math.Max(s, wordEnd << 6); i < end; i++)
            if (Get(bits, i)) n++;
        return n;
    }
}
