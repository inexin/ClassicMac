using System;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes the adaptive-Huffman LZSS method used by LHarc's <c>-lh1-</c>.</summary>
internal static class LhaOldDecoder
{
    private const int WindowSize = 4096;
    private const int MatchThreshold = 3;

    private static readonly int[] OffsetCodeCounts = [1, 3, 8, 12, 24, 16];

    public static byte[] DecodeLh1(ReadOnlySpan<byte> packed, int expandedSize)
    {
        var decoder = new LhaAdaptiveHuffmanTree(314);
        var bits = new LhaBitReader(packed);
        var window = new byte[WindowSize];
        window.AsSpan().Fill((byte)' ');
        var output = new byte[expandedSize];
        int windowPosition = 0;
        int outputPosition = 0;

        while (outputPosition < output.Length)
        {
            int code = decoder.ReadCode(ref bits);
            if (code < 256)
            {
                Emit((byte)code, output, ref outputPosition, window, ref windowPosition);
                continue;
            }

            int count = code - 0x100 + MatchThreshold;
            if (count > output.Length - outputPosition)
            {
                throw new InvalidDataException("An LHA match exceeds the declared expanded size.");
            }

            int offset = ReadOffset(ref bits);
            int sourcePosition = (windowPosition - offset - 1 + WindowSize) & (WindowSize - 1);
            for (int index = 0; index < count; index++)
            {
                byte value = window[sourcePosition];
                sourcePosition = (sourcePosition + 1) & (WindowSize - 1);
                Emit(value, output, ref outputPosition, window, ref windowPosition);
            }
        }

        return output;
    }

    private static int ReadOffset(ref LhaBitReader bits)
    {
        // LZHUF's d_code/d_len table as a canonical code: the shortest codes are 3 bits, so the first
        // step reads 3 bits and each later one a single bit.
        int code = bits.ReadBits(2);
        int firstCode = 0;
        int symbol = 0;
        for (int length = 3; length <= 8; length++)
        {
            code = (code << 1) | bits.ReadBit();
            int count = OffsetCodeCounts[length - 3];
            if (code >= firstCode && code - firstCode < count)
            {
                return ((symbol + code - firstCode) << 6) | bits.ReadBits(6);
            }

            symbol += count;
            firstCode = (firstCode + count) << 1;
        }

        throw new InvalidDataException("An LHA offset uses an invalid Huffman code.");
    }

    private static void Emit(byte value, byte[] output, ref int outputPosition, byte[] window,
        ref int windowPosition)
    {
        output[outputPosition++] = value;
        window[windowPosition] = value;
        windowPosition = (windowPosition + 1) & (WindowSize - 1);
    }
}

internal sealed class LhaAdaptiveHuffmanTree
{
    private const int ReorderLimit = 32 * 1024;
    private readonly int _symbolCount;
    private readonly int _nodeCount;
    private readonly Node[] _nodes;
    private readonly int[] _leafNodes;
    private readonly int[] _groups;
    private readonly int[] _groupLeaders;
    private int _groupCount;

    public LhaAdaptiveHuffmanTree(int symbolCount)
    {
        if (symbolCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        _symbolCount = symbolCount;
        _nodeCount = symbolCount * 2 - 1;
        _nodes = new Node[_nodeCount];
        _leafNodes = new int[symbolCount];
        _groups = new int[_nodeCount];
        _groupLeaders = new int[_nodeCount];
        InitializeGroups();
        InitializeTree();
    }

    public int ReadCode(ref LhaBitReader bits)
    {
        int nodeIndex = 0;
        while (!_nodes[nodeIndex].IsLeaf)
        {
            nodeIndex = _nodes[nodeIndex].ChildIndex - bits.ReadBit();
        }

        int code = _nodes[nodeIndex].ChildIndex;
        IncrementForCode(code);
        return code;
    }

    private void InitializeTree()
    {
        int leafGroup = AllocateGroup();
        int nodeIndex = _nodeCount - 1;
        for (int code = 0; code < _symbolCount; code++)
        {
            _nodes[nodeIndex] = new Node(true, code, 1, leafGroup);
            _leafNodes[code] = nodeIndex;
            nodeIndex--;
        }
        // A group's leader is its left-most (lowest-index) node: the last leaf placed.
        _groupLeaders[leafGroup] = _nodeCount - _symbolCount;

        int child = _nodeCount - 1;
        for (nodeIndex = _symbolCount - 2; nodeIndex >= 0; nodeIndex--, child -= 2)
        {
            Node node = new(false, child, _nodes[child].Frequency + _nodes[child - 1].Frequency, 0);
            _nodes[nodeIndex] = node;
            _nodes[child].Parent = nodeIndex;
            _nodes[child - 1].Parent = nodeIndex;
            if (node.Frequency == _nodes[nodeIndex + 1].Frequency)
            {
                _nodes[nodeIndex].Group = _nodes[nodeIndex + 1].Group;
            }
            else
            {
                _nodes[nodeIndex].Group = AllocateGroup();
            }

            _groupLeaders[_nodes[nodeIndex].Group] = nodeIndex;
        }
    }

    private void InitializeGroups()
    {
        for (int index = 0; index < _nodeCount; index++)
        {
            _groups[index] = index;
        }

        _groupCount = 0;
    }

    private int AllocateGroup() => _groups[_groupCount++];

    private void FreeGroup(int group)
    {
        _groupCount--;
        _groups[_groupCount] = group;
    }

    private void IncrementForCode(int code)
    {
        if (_nodes[0].Frequency >= ReorderLimit)
        {
            ReconstructTree();
        }

        _nodes[0].Frequency++;
        int nodeIndex = _leafNodes[code];
        while (nodeIndex != 0)
        {
            nodeIndex = MakeGroupLeader(nodeIndex);
            IncrementNodeFrequency(nodeIndex);
            nodeIndex = _nodes[nodeIndex].Parent;
        }
    }

    private int MakeGroupLeader(int nodeIndex)
    {
        int leaderIndex = _groupLeaders[_nodes[nodeIndex].Group];
        if (leaderIndex == nodeIndex)
        {
            return nodeIndex;
        }

        (_nodes[nodeIndex].IsLeaf, _nodes[leaderIndex].IsLeaf) =
            (_nodes[leaderIndex].IsLeaf, _nodes[nodeIndex].IsLeaf);
        (_nodes[nodeIndex].ChildIndex, _nodes[leaderIndex].ChildIndex) =
            (_nodes[leaderIndex].ChildIndex, _nodes[nodeIndex].ChildIndex);
        UpdateMovedNode(nodeIndex);
        UpdateMovedNode(leaderIndex);
        return leaderIndex;
    }

    private void UpdateMovedNode(int nodeIndex)
    {
        if (_nodes[nodeIndex].IsLeaf)
        {
            _leafNodes[_nodes[nodeIndex].ChildIndex] = nodeIndex;
        }
        else
        {
            _nodes[_nodes[nodeIndex].ChildIndex].Parent = nodeIndex;
            _nodes[_nodes[nodeIndex].ChildIndex - 1].Parent = nodeIndex;
        }
    }

    private void IncrementNodeFrequency(int nodeIndex)
    {
        _nodes[nodeIndex].Frequency++;
        if (nodeIndex < _nodeCount - 1 && _nodes[nodeIndex].Group == _nodes[nodeIndex + 1].Group)
        {
            _groupLeaders[_nodes[nodeIndex].Group]++;
            if (_nodes[nodeIndex].Frequency == _nodes[nodeIndex - 1].Frequency)
            {
                _nodes[nodeIndex].Group = _nodes[nodeIndex - 1].Group;
            }
            else
            {
                _nodes[nodeIndex].Group = AllocateGroup();
                _groupLeaders[_nodes[nodeIndex].Group] = nodeIndex;
            }
        }
        else if (_nodes[nodeIndex].Frequency == _nodes[nodeIndex - 1].Frequency)
        {
            FreeGroup(_nodes[nodeIndex].Group);
            _nodes[nodeIndex].Group = _nodes[nodeIndex - 1].Group;
        }
    }

    private void ReconstructTree()
    {
        for (int index = 0, leafCount = 0; index < _nodeCount; index++)
        {
            if (!_nodes[index].IsLeaf)
            {
                continue;
            }

            Node leaf = _nodes[index];
            leaf.Frequency = (leaf.Frequency + 1) / 2;
            _nodes[leafCount++] = leaf;
        }

        int sourceLeaf = _symbolCount - 1;
        int child = _nodeCount - 1;
        int nodeIndex = _nodeCount - 1;
        while (nodeIndex >= 0)
        {
            while (child - nodeIndex < 2)
            {
                _nodes[nodeIndex] = _nodes[sourceLeaf--];
                _leafNodes[_nodes[nodeIndex].ChildIndex] = nodeIndex;
                nodeIndex--;
            }

            int frequency = _nodes[child].Frequency + _nodes[child - 1].Frequency;
            while (sourceLeaf >= 0 && frequency >= _nodes[sourceLeaf].Frequency)
            {
                _nodes[nodeIndex] = _nodes[sourceLeaf--];
                _leafNodes[_nodes[nodeIndex].ChildIndex] = nodeIndex;
                nodeIndex--;
            }

            _nodes[nodeIndex] = new Node(false, child, frequency, 0);
            _nodes[child].Parent = nodeIndex;
            _nodes[child - 1].Parent = nodeIndex;
            nodeIndex--;
            child -= 2;
        }

        InitializeGroups();
        int group = AllocateGroup();
        _nodes[0].Group = group;
        _groupLeaders[group] = 0;
        for (int index = 1; index < _nodeCount; index++)
        {
            if (_nodes[index].Frequency != _nodes[index - 1].Frequency)
            {
                group = AllocateGroup();
                _groupLeaders[group] = index;
            }
            _nodes[index].Group = group;
        }
    }

    private struct Node(bool isLeaf, int childIndex, int frequency, int group)
    {
        public bool IsLeaf = isLeaf;
        public int ChildIndex = childIndex;
        public int Frequency = frequency;
        public int Group = group;
        public int Parent;
    }
}

internal ref struct LhaBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public LhaBitReader(ReadOnlySpan<byte> data) => _data = data;

    public int ReadBit()
    {
        if (_position >= _data.Length * 8)
        {
            throw new InvalidDataException("An LHA compressed stream is truncated.");
        }

        int bit = (_data[_position >> 3] >> (7 - (_position & 7))) & 1;
        _position++;
        return bit;
    }

    public int ReadBits(int count)
    {
        int value = 0;
        for (int index = 0; index < count; index++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }
}
