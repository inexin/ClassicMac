using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Resources.Decoders.Images.WebP;

/// <summary>
/// A VP8L prefix code (RFC 9649 §3.7.2): canonical Huffman code lengths from symbol counts, at most 15 bits, and how it
/// is written: a simple code for one or two symbols under 256, else the code lengths themselves coded by a code-length
/// code. A code with one symbol takes no bits per symbol.
/// </summary>
internal sealed class PrefixCode
{
    private const int MaxLength = 15, MaxCodeLengthLength = 7;
    private static readonly int[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly byte[] lengths;
    private readonly uint[] codes;
    private readonly int single = -1;

    private PrefixCode(byte[] lengths)
    {
        this.lengths = lengths;
        codes = new uint[lengths.Length];
        var used = Enumerable.Range(0, lengths.Length).Where(s => lengths[s] > 0).ToList();
        if (used.Count == 1)
        {
            single = used[0];
            return;
        }

        // Canonical codes, by length then symbol, each reversed: the bit stream is read from its least significant bit.
        uint code = 0;
        var previous = 0;
        foreach (var symbol in used.OrderBy(s => lengths[s]).ThenBy(s => s))
        {
            code <<= lengths[symbol] - previous;
            previous = lengths[symbol];
            codes[symbol] = Reverse(code, previous);
            code++;
        }
    }

    /// <summary>The code for symbols counted <paramref name="counts"/> times; at most <paramref name="maxLength"/> bits.</summary>
    public static PrefixCode Build(int[] counts, int maxLength = MaxLength) => new(Lengths(counts, maxLength));

    /// <summary>Writes <paramref name="symbol"/>'s code (nothing for a code of one symbol).</summary>
    public void WriteSymbol(Vp8lBitWriter writer, int symbol)
    {
        if (single >= 0)
        {
            return;
        }

        writer.Write(codes[symbol], lengths[symbol]);
    }

    /// <summary>Writes the code itself, as a decoder reads it before the symbols.</summary>
    public void WriteCode(Vp8lBitWriter writer)
    {
        var used = Enumerable.Range(0, lengths.Length).Where(s => lengths[s] > 0).ToList();
        if (used.Count == 0)
        {
            used = [0];                                                   // an unused alphabet: one symbol, 0
        }

        if (used.Count <= 2 && used.All(s => s < 256))
        {
            writer.Write(1, 1);                                           // simple
            writer.Write((uint)(used.Count - 1), 1);
            if (used[0] < 2)
            {
                writer.Write(0, 1);
                writer.Write((uint)used[0], 1);
            }
            else
            {
                writer.Write(1, 1);
                writer.Write((uint)used[0], 8);
            }

            if (used.Count == 2)
            {
                writer.Write((uint)used[1], 8);
            }

            return;
        }

        writer.Write(0, 1);                                               // normal
        var tokens = Tokens(lengths);
        var counts = new int[19];
        foreach (var (code, _, _) in tokens)
        {
            counts[code]++;
        }

        var lengthCode = new PrefixCode(Lengths(counts, MaxCodeLengthLength));
        var written = CodeLengthOrder.Length;
        while (written > 4 && lengthCode.lengths[CodeLengthOrder[written - 1]] == 0)
        {
            written--;
        }

        writer.Write((uint)(written - 4), 4);
        for (var i = 0; i < written; i++)
        {
            writer.Write(lengthCode.lengths[CodeLengthOrder[i]], 3);
        }

        writer.Write(0, 1);                                               // every symbol's length follows
        foreach (var (code, extra, extraBits) in tokens)
        {
            lengthCode.WriteSymbol(writer, code);
            writer.Write((uint)extra, extraBits);
        }
    }

    // The code lengths as code-length symbols (§3.7.2.1.2): 0–15 a length; 16 the previous non-zero length 3–6 times
    // (2 extra bits; before any, 8); 17 3–10 zeros (3 bits); 18 11–138 zeros (7 bits).
    private static List<(int Code, int Extra, int ExtraBits)> Tokens(byte[] lengths)
    {
        var tokens = new List<(int, int, int)>();
        var previous = 8;
        for (var i = 0; i < lengths.Length;)
        {
            int value = lengths[i], run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    var n = Math.Min(run, 138);
                    tokens.Add((18, n - 11, 7));
                    run -= n;
                }

                if (run >= 3)
                {
                    tokens.Add((17, run - 3, 3));
                    run = 0;
                }

                for (; run > 0; run--)
                {
                    tokens.Add((0, 0, 0));
                }

                continue;
            }

            if (value != previous)
            {
                tokens.Add((value, 0, 0));
                previous = value;
                run--;
            }

            while (run >= 3)
            {
                var n = Math.Min(run, 6);
                tokens.Add((16, n - 3, 2));
                run -= n;
            }

            for (; run > 0; run--)
            {
                tokens.Add((value, 0, 0));
            }
        }

        return tokens;
    }

    // Huffman code lengths for the counts, none over maxLength: built, and while too long, built again with every count
    // raised to at least a minimum that doubles each time (as libwebp limits its trees).
    private static byte[] Lengths(int[] counts, int maxLength)
    {
        var lengths = new byte[counts.Length];
        var used = Enumerable.Range(0, counts.Length).Where(s => counts[s] > 0).ToList();
        if (used.Count == 0)
        {
            return lengths;
        }

        if (used.Count == 1)
        {
            lengths[used[0]] = 1;
            return lengths;
        }

        for (var minimum = 1; ; minimum *= 2)
        {
            var nodes = new List<(long Count, int Left, int Right)>();
            var queue = new PriorityQueue<int, (long, int)>();
            foreach (var s in used)
            {
                nodes.Add((Math.Max(counts[s], minimum), -1, s));
                queue.Enqueue(nodes.Count - 1, (nodes[^1].Count, nodes.Count - 1));
            }

            while (queue.Count > 1)
            {
                var a = queue.Dequeue();
                var b = queue.Dequeue();
                nodes.Add((nodes[a].Count + nodes[b].Count, a, b));
                queue.Enqueue(nodes.Count - 1, (nodes[^1].Count, nodes.Count - 1));
            }

            var depth = new int[nodes.Count];
            var longest = 0;
            for (var n = nodes.Count - 1; n >= 0; n--)
            {
                if (nodes[n].Left >= 0)
                {
                    depth[nodes[n].Left] = depth[n] + 1;
                    depth[nodes[n].Right] = depth[n] + 1;
                }
                else
                {
                    lengths[nodes[n].Right] = (byte)depth[n];
                    longest = Math.Max(longest, depth[n]);
                }
            }

            if (longest <= maxLength)
            {
                return lengths;
            }
        }
    }

    private static uint Reverse(uint code, int length)
    {
        uint reversed = 0;
        for (var i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return reversed;
    }
}
