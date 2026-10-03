using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// Apple Data Compression (ADC), as Disk Copy 6.3.3 decodes NDIF chunks (disassembly of its <c>hdi</c> codec,
    /// confirmed on its own images). Each opcode byte is a literal run (high bit set: 1–128 bytes follow), a short match
    /// (<c>00LLLLDD DDDDDDDD</c>: 3–18 bytes from 1–1024 back) or a long match (<c>01LLLLLL</c> + 16-bit distance: 4–67
    /// bytes from 1–65536 back); matches copy byte by byte, so they may overlap what they write. Decoding stops when the
    /// output is full; a token that would pass its end is an error (checked before anything of it is written). No state
    /// carries from one chunk to the next. Disk Copy checks nothing else — a match before the start reads the memory
    /// before its buffer, and input is read past its stored length — so those are reported here instead.
    /// </summary>
    internal static class Adc
    {
        /// <summary>How a decompression ended.</summary>
        public enum Result
        {
            /// <summary>The output is full.</summary>
            Done,

            /// <summary>A token would pass the end of the output (Disk Copy: "damaged").</summary>
            Overrun,

            /// <summary>The input ran out before the output was full.</summary>
            Truncated,

            /// <summary>A match reached before the start of the output.</summary>
            BadDistance,
        }

        private const int MaxMatch = 67, MaxShortMatch = 18, MaxShortDistance = 1024, MaxLiteralRun = 128, PoolSize = 65_537;

        /// <summary>Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3 does (adc.md §3).</summary>
        public static byte[] Compress(ReadOnlySpan<byte> input) => Compress(input, out _);

        /// <summary>
        /// Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3's encoder does (adc.md §3): every position
        /// goes into binary search trees of the earlier ones, by its first 2 bytes (1 byte under 4,000 bytes, none under
        /// 200), and the longest match met is taken greedily. <paramref name="margin"/> is how far decoding in place runs
        /// ahead of the input, the room NDIF's buffer size (<c>+$48</c>) keeps.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> input, out int margin)
        {
            var encoder = new Encoder(input.Length);
            return encoder.Run(input, out margin);
        }

        // Disk Copy 6.3.3's ADC encoder (ADCCOMPRESSDATA, TreeSearch, InitNodes) [Code: Disk Copy 6.3.3; Verified: its
        // images' chunks re-encode exactly].
        private sealed class Encoder
        {
            private readonly int prefix;
            private readonly int[] roots;
            private readonly int[] position = new int[PoolSize], left = new int[PoolSize], right = new int[PoolSize], parent = new int[PoolSize];
            private int cursor;

            public Encoder(int length)
            {
                // InitNodes: two prefix bytes, one under 4,000 bytes, none under 200.
                prefix = length < 200 ? 0 : length < 4000 ? 1 : 2;
                roots = new int[prefix == 2 ? 65_536 : prefix == 1 ? 256 : 1];
                Array.Fill(roots, -1);
                Array.Fill(position, -1);
            }

            public byte[] Run(ReadOnlySpan<byte> input, out int margin)
            {
                var output = new System.IO.MemoryStream(input.Length / 2 + 16);
                int literalStart = 0, at = 0, mostAhead = 0;

                // After each flush: how far the input covered so far is ahead of the bytes written.
                void Note(int covered)
                {
                    mostAhead = Math.Max(mostAhead, covered - (int)output.Length);
                }

                void FlushLiterals(ReadOnlySpan<byte> bytes, int end)
                {
                    while (literalStart < end)
                    {
                        int run = Math.Min(MaxLiteralRun, end - literalStart);
                        output.WriteByte((byte)(0x80 | (run - 1)));
                        output.Write(bytes.Slice(literalStart, run));
                        literalStart += run;
                    }
                }

                while (at < input.Length)
                {
                    var (length, from) = Search(input, at);
                    int distance = at - from;
                    bool isShort = length >= 3 && length <= MaxShortMatch && distance <= MaxShortDistance;
                    if (!isShort && length < 4)
                    {
                        at++;
                        if (at - literalStart == MaxLiteralRun)
                        {
                            FlushLiterals(input, at);
                            Note(at);
                        }

                        continue;
                    }

                    FlushLiterals(input, at);
                    if (isShort)
                    {
                        output.WriteByte((byte)(((length - 3) << 2) | ((distance - 1) >> 8)));
                        output.WriteByte((byte)(distance - 1));
                    }
                    else
                    {
                        output.WriteByte((byte)(0x40 | (length - 4)));
                        output.WriteByte((byte)((distance - 1) >> 8));
                        output.WriteByte((byte)(distance - 1));
                    }

                    // The positions the match covers go into the trees too.
                    for (int covered = at + 1; covered < at + length; covered++)
                    {
                        Search(input, covered);
                    }

                    at += length;
                    literalStart = at;
                    Note(at);
                }

                FlushLiterals(input, input.Length);
                Note(input.Length);
                margin = mostAhead - (input.Length - (int)output.Length) + 4;
                return output.ToArray();
            }

            private int Root(ReadOnlySpan<byte> input, int at) => prefix switch
            {
                2 => input[at] << 8 | input[at + 1],
                1 => input[at],
                _ => 0,
            };

            // TreeSearch: puts the position into its tree and returns the longest match met on the way down (the first
            // node met with a longer one). A node whose compared bytes all match is replaced by the new position.
            private (int Length, int From) Search(ReadOnlySpan<byte> input, int at)
            {
                if (prefix == 2 && at + 2 > input.Length - 1)
                {
                    return (0, 0);
                }

                int node = cursor;
                cursor = (cursor + 1) % PoolSize;
                if (position[node] >= 0)
                {
                    Delete(input, node, at & 1);
                }

                position[node] = at;
                left[node] = right[node] = parent[node] = -1;
                int root = Root(input, at);
                int current = roots[root];
                if (current < 0)
                {
                    roots[root] = node;
                    return (0, 0);
                }

                int compared = Math.Min(MaxMatch - prefix, input.Length - at - prefix);
                int bestLength = 0, bestFrom = 0;
                while (true)
                {
                    int other = position[current];
                    int same = 0;
                    while (same < compared && input[at + prefix + same] == input[other + prefix + same])
                    {
                        same++;
                    }

                    if (prefix + same > bestLength)
                    {
                        bestLength = prefix + same;
                        bestFrom = other;
                    }

                    if (same == compared)
                    {
                        Replace(input, current, node);
                        return (bestLength, bestFrom);
                    }

                    bool lower = input[at + prefix + same] < input[other + prefix + same];
                    int next = lower ? left[current] : right[current];
                    if (next < 0)
                    {
                        if (lower)
                        {
                            left[current] = node;
                        }
                        else
                        {
                            right[current] = node;
                        }

                        parent[node] = current;
                        return (bestLength, bestFrom);
                    }

                    current = next;
                }
            }

            // The new node takes the old one's place: its parent link (or root), both children; the old one is cleared.
            private void Replace(ReadOnlySpan<byte> input, int old, int node)
            {
                left[node] = left[old];
                right[node] = right[old];
                SetParent(left[node], node);
                SetParent(right[node], node);
                TakePlace(input, old, node);
                Clear(old);
            }

            // Deletes a node whose pool slot is reused: with two children, the replacement is the rightmost node of the
            // left subtree (parity 0, the inserted position even) or the leftmost of the right (parity 1).
            private void Delete(ReadOnlySpan<byte> input, int node, int parity)
            {
                int replacement;
                if (left[node] >= 0 && right[node] >= 0)
                {
                    int near = parity == 0 ? left[node] : right[node];
                    replacement = near;
                    while ((parity == 0 ? right[replacement] : left[replacement]) >= 0)
                    {
                        replacement = parity == 0 ? right[replacement] : left[replacement];
                    }

                    if (replacement == near)
                    {
                        // The direct child keeps its own children and takes the other side.
                        if (parity == 0)
                        {
                            right[replacement] = right[node];
                            SetParent(right[replacement], replacement);
                        }
                        else
                        {
                            left[replacement] = left[node];
                            SetParent(left[replacement], replacement);
                        }
                    }
                    else
                    {
                        // Its parent's far link takes its near child; it takes both of the deleted node's children.
                        int above = parent[replacement];
                        int inner = parity == 0 ? left[replacement] : right[replacement];
                        if (parity == 0)
                        {
                            right[above] = inner;
                        }
                        else
                        {
                            left[above] = inner;
                        }

                        SetParent(inner, above);
                        left[replacement] = left[node];
                        right[replacement] = right[node];
                        SetParent(left[replacement], replacement);
                        SetParent(right[replacement], replacement);
                    }
                }
                else
                {
                    replacement = left[node] >= 0 ? left[node] : right[node];
                }

                TakePlace(input, node, replacement);
                Clear(node);
            }

            // Points what pointed at old (its parent's link, or its root) at node, which may be none.
            private void TakePlace(ReadOnlySpan<byte> input, int old, int node)
            {
                int above = parent[old];
                if (above < 0)
                {
                    roots[Root(input, position[old])] = node;
                }
                else if (left[above] == old)
                {
                    left[above] = node;
                }
                else
                {
                    right[above] = node;
                }

                SetParent(node, above);
            }

            private void SetParent(int node, int above)
            {
                if (node >= 0)
                {
                    parent[node] = above;
                }
            }

            private void Clear(int node)
            {
                position[node] = -1;
                left[node] = right[node] = parent[node] = -1;
            }
        }

        /// <summary>Decompresses into <paramref name="output"/> until it is full; returns how it ended.</summary>
        public static Result Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var i = 0;
            while (written < output.Length)
            {
                if (i >= input.Length)
                {
                    return Result.Truncated;
                }

                var op = input[i++];
                int length, distance;
                if ((op & 0x80) != 0)
                {
                    length = (op & 0x7F) + 1;
                    if (written + length > output.Length)
                    {
                        return Result.Overrun;
                    }

                    if (i + length > input.Length)
                    {
                        return Result.Truncated;
                    }

                    input.Slice(i, length).CopyTo(output[written..]);
                    written += length;
                    i += length;
                    continue;
                }
                if ((op & 0x40) != 0)
                {
                    length = (op & 0x3F) + 4;
                    if (i + 2 > input.Length)
                    {
                        return Result.Truncated;
                    }

                    distance = (input[i] << 8 | input[i + 1]) + 1;
                    i += 2;
                }
                else
                {
                    length = (op >> 2) + 3;
                    if (i + 1 > input.Length)
                    {
                        return Result.Truncated;
                    }

                    distance = ((op & 0x03) << 8 | input[i]) + 1;
                    i += 1;
                }
                if (written + length > output.Length)
                {
                    return Result.Overrun;
                }

                if (distance > written)
                {
                    return Result.BadDistance;
                }

                for (var n = 0; n < length; n++, written++)
                {
                    output[written] = output[written - distance];
                }
            }
            return Result.Done;
        }
    }
}
