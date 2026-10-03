using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// Disk Copy 6.3.3's match finder, shared by its ADC and KenCode encoders (TreeSearch, InitNodes; adc.md §3 steps 1–4,
    /// kencode.md §3): every position of a chunk goes into binary search trees of the positions before it, chosen by its
    /// first two bytes (one byte under 4,000 bytes, none under 200), and the longest match met on the way down is
    /// returned. A pool of window + 1 nodes, used in turn, keeps matches within the window [Code: Disk Copy 6.3.3;
    /// Verified: its images' chunks re-encode exactly].
    /// </summary>
    internal sealed class TreeMatcher
    {
        private readonly int prefix;
        private readonly int maxMatch;
        private readonly int[] roots;
        private readonly int[] position, left, right, parent;
        private int cursor;

        /// <param name="length">The chunk's length, which sets the prefix.</param>
        /// <param name="window">How far back a match may reach (the pool holds one node more).</param>
        /// <param name="maxMatch">The longest match.</param>
        public TreeMatcher(int length, int window, int maxMatch)
        {
            prefix = length < 200 ? 0 : length < 4000 ? 1 : 2;
            this.maxMatch = maxMatch;
            roots = new int[prefix == 2 ? 65_536 : prefix == 1 ? 256 : 1];
            Array.Fill(roots, -1);
            int pool = window + 1;
            position = new int[pool];
            left = new int[pool];
            right = new int[pool];
            parent = new int[pool];
            Array.Fill(position, -1);
        }

        private int Root(ReadOnlySpan<byte> input, int at) => prefix switch
        {
            2 => input[at] << 8 | input[at + 1],
            1 => input[at],
            _ => 0,
        };

        /// <summary>
        /// Puts the position into its tree and returns the longest match met on the way down (the first node met with a
        /// longer one); a length 0 is none. A node whose compared bytes all match is replaced by the new position.
        /// </summary>
        public (int Length, int From) Search(ReadOnlySpan<byte> input, int at)
        {
            // With a two-byte prefix, the last two positions are neither searched nor inserted.
            if (prefix == 2 && at + 2 > input.Length - 1)
            {
                return (0, 0);
            }

            int node = cursor;
            cursor = (cursor + 1) % position.Length;
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

            int compared = Math.Min(maxMatch - prefix, input.Length - at - prefix);
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
}
