using System;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes the dynamic-Huffman LZSS method used by LHA <c>-lh2-</c> files.</summary>
internal static class LhaLh2Decoder
{
    private const int WindowSize = 8192;
    private const int LiteralAndMatchSymbolCount = 286;
    private const int ExtendedLengthSymbol = LiteralAndMatchSymbolCount - 1;
    private const int MatchLengthBias = 256 - 3;

    public static byte[] Decode(ReadOnlySpan<byte> packed, int expandedSize)
    {
        if (expandedSize < 0) throw new ArgumentOutOfRangeException(nameof(expandedSize));

        var bits = new LhaBitReader(packed);
        var literalAndMatchTree = new LhaAdaptiveHuffmanTree(LiteralAndMatchSymbolCount);
        var positionTree = new GrowingHuffmanTree(WindowSize / 64);
        byte[] window = new byte[WindowSize];
        window.AsSpan().Fill((byte)' ');
        byte[] output = new byte[expandedSize];
        int outputOffset = 0;

        while (outputOffset < output.Length)
        {
            int symbol = literalAndMatchTree.ReadCode(ref bits);
            if (symbol == ExtendedLengthSymbol)
                symbol += bits.ReadBits(8);

            if (symbol < 256)
            {
                byte value = (byte)symbol;
                output[outputOffset] = value;
                window[outputOffset & (WindowSize - 1)] = value;
                outputOffset++;
                continue;
            }

            int matchLength = symbol - MatchLengthBias;
            if (matchLength is < 3 or > 256)
                throw new InvalidDataException("An LH2 match length is outside the method's range.");
            if (matchLength > output.Length - outputOffset)
                throw new InvalidDataException("An LH2 match exceeds the declared expanded size.");

            int encodedPosition = positionTree.ReadPosition(ref bits, outputOffset);
            int sourceOffset = (outputOffset - encodedPosition - 1) & (WindowSize - 1);
            for (int index = 0; index < matchLength; index++)
            {
                byte value = window[(sourceOffset + index) & (WindowSize - 1)];
                output[outputOffset] = value;
                window[outputOffset & (WindowSize - 1)] = value;
                outputOffset++;
            }
        }

        return output;
    }

    private sealed class GrowingHuffmanTree
    {
        private const int ReorderLimit = 0x8000;
        private readonly int _maximumSymbols;
        private readonly Node[] _nodes;
        private readonly int[] _symbolNodes;
        private readonly int[] _freeGroups;
        private readonly int[] _groupLeaders;
        private int _activeNodeCount = 1;
        private int _symbolCount = 1;
        private int _freeGroupCount;
        private int _mostRecentNode;
        private int _totalFrequency;
        private int _nextOutputCount = 64;

        public GrowingHuffmanTree(int maximumSymbols)
        {
            if (maximumSymbols < 1) throw new ArgumentOutOfRangeException(nameof(maximumSymbols));
            _maximumSymbols = maximumSymbols;
            int capacity = maximumSymbols * 2 - 1;
            _nodes = new Node[capacity];
            _symbolNodes = new int[maximumSymbols];
            _freeGroups = new int[capacity];
            _groupLeaders = new int[capacity];
            for (int index = 0; index < capacity; index++) _freeGroups[index] = index;
            int firstGroup = AllocateGroup();
            _nodes[0] = new Node(true, 0, 1, firstGroup, 0);
            _symbolNodes[0] = 0;
            _groupLeaders[firstGroup] = 0;
        }

        public int ReadPosition(ref LhaBitReader bits, int outputCount)
        {
            while (outputCount > _nextOutputCount && _symbolCount < _maximumSymbols)
            {
                AddSymbol(_symbolCount);
                _nextOutputCount += 64;
            }

            int highPositionBits = ReadSymbol(ref bits);
            return (highPositionBits << 6) | bits.ReadBits(6);
        }

        private int ReadSymbol(ref LhaBitReader bits)
        {
            int nodeIndex = 0;
            while (!_nodes[nodeIndex].IsLeaf)
                nodeIndex = _nodes[nodeIndex].ChildIndex - bits.ReadBit();

            int symbol = _nodes[nodeIndex].Symbol;
            UpdateSymbol(symbol);
            return symbol;
        }

        private void AddSymbol(int symbol)
        {
            if (symbol != _symbolCount || symbol >= _maximumSymbols || !_nodes[_mostRecentNode].IsLeaf)
                throw new InvalidDataException("An LH2 position tree cannot add another symbol.");

            int parentIndex = _mostRecentNode;
            Node previous = _nodes[parentIndex];
            int oldLeafIndex = _activeNodeCount++;
            int newLeafIndex = _activeNodeCount++;
            int newGroup = AllocateGroup();
            _nodes[oldLeafIndex] = new Node(true, previous.Symbol, previous.Frequency,
                previous.Group, parentIndex);
            _nodes[newLeafIndex] = new Node(true, symbol, 0, newGroup, parentIndex);
            _symbolNodes[previous.Symbol] = oldLeafIndex;
            _symbolNodes[symbol] = newLeafIndex;
            _nodes[parentIndex] = new Node(false, previous.Symbol, previous.Frequency,
                previous.Group, previous.Parent) { ChildIndex = newLeafIndex };
            if (parentIndex == 0)
            {
                _nodes[0].Frequency = ushort.MaxValue;
                _groupLeaders[previous.Group]++;
            }
            _groupLeaders[newGroup] = newLeafIndex;
            _mostRecentNode = newLeafIndex;
            _symbolCount++;
            UpdateSymbol(symbol);
        }

        private int AllocateGroup() => _freeGroups[_freeGroupCount++];

        private void ReleaseGroup(int group)
        {
            _freeGroupCount--;
            _freeGroups[_freeGroupCount] = group;
        }

        private void UpdateSymbol(int symbol)
        {
            if (_totalFrequency == ReorderLimit) ReconstructTree();
            int nodeIndex = _symbolNodes[symbol];
            while (nodeIndex != 0)
            {
                nodeIndex = MoveToGroupLeader(nodeIndex);
                IncrementFrequency(nodeIndex);
                nodeIndex = _nodes[nodeIndex].Parent;
            }
            _totalFrequency++;
        }

        private int MoveToGroupLeader(int nodeIndex)
        {
            int leaderIndex = _groupLeaders[_nodes[nodeIndex].Group];
            if (leaderIndex == nodeIndex) return nodeIndex;

            Node current = _nodes[nodeIndex];
            Node leader = _nodes[leaderIndex];
            _nodes[nodeIndex].IsLeaf = leader.IsLeaf;
            _nodes[nodeIndex].Symbol = leader.Symbol;
            _nodes[nodeIndex].ChildIndex = leader.ChildIndex;
            _nodes[leaderIndex].IsLeaf = current.IsLeaf;
            _nodes[leaderIndex].Symbol = current.Symbol;
            _nodes[leaderIndex].ChildIndex = current.ChildIndex;
            RefreshMovedNode(nodeIndex);
            RefreshMovedNode(leaderIndex);
            return leaderIndex;
        }

        private void RefreshMovedNode(int nodeIndex)
        {
            Node node = _nodes[nodeIndex];
            if (node.IsLeaf)
                _symbolNodes[node.Symbol] = nodeIndex;
            else
            {
                _nodes[node.ChildIndex].Parent = nodeIndex;
                _nodes[node.ChildIndex - 1].Parent = nodeIndex;
            }
        }

        private void IncrementFrequency(int nodeIndex)
        {
            _nodes[nodeIndex].Frequency++;
            if (nodeIndex < _activeNodeCount - 1 &&
                _nodes[nodeIndex].Group == _nodes[nodeIndex + 1].Group)
            {
                _groupLeaders[_nodes[nodeIndex].Group]++;
                if (_nodes[nodeIndex].Frequency == _nodes[nodeIndex - 1].Frequency)
                    _nodes[nodeIndex].Group = _nodes[nodeIndex - 1].Group;
                else
                {
                    _nodes[nodeIndex].Group = AllocateGroup();
                    _groupLeaders[_nodes[nodeIndex].Group] = nodeIndex;
                }
            }
            else if (_nodes[nodeIndex].Frequency == _nodes[nodeIndex - 1].Frequency)
            {
                ReleaseGroup(_nodes[nodeIndex].Group);
                _nodes[nodeIndex].Group = _nodes[nodeIndex - 1].Group;
            }
        }

        private void ReconstructTree()
        {
            int leafCount = 0;
            for (int index = 0; index < _activeNodeCount; index++)
            {
                if (!_nodes[index].IsLeaf) continue;
                Node leaf = _nodes[index];
                leaf.Frequency = (leaf.Frequency + 1) / 2;
                _nodes[leafCount++] = leaf;
            }

            int sourceLeaf = _symbolCount - 1;
            int child = _activeNodeCount - 1;
            int nodeIndex = _activeNodeCount - 1;
            while (nodeIndex >= 0)
            {
                while (child - nodeIndex < 2)
                {
                    _nodes[nodeIndex] = _nodes[sourceLeaf--];
                    _symbolNodes[_nodes[nodeIndex].Symbol] = nodeIndex;
                    nodeIndex--;
                }

                int frequency = _nodes[child].Frequency + _nodes[child - 1].Frequency;
                while (sourceLeaf >= 0 && frequency >= _nodes[sourceLeaf].Frequency)
                {
                    _nodes[nodeIndex] = _nodes[sourceLeaf--];
                    _symbolNodes[_nodes[nodeIndex].Symbol] = nodeIndex;
                    nodeIndex--;
                }

                _nodes[nodeIndex] = new Node(false, 0, frequency, 0, 0);
                _nodes[nodeIndex].ChildIndex = child;
                _nodes[child].Parent = nodeIndex;
                _nodes[child - 1].Parent = nodeIndex;
                nodeIndex--;
                child -= 2;
            }

            _freeGroupCount = 0;
            for (int group = 0; group < _freeGroups.Length; group++) _freeGroups[group] = group;
            int currentGroup = AllocateGroup();
            _groupLeaders[currentGroup] = 0;
            _nodes[0].Group = currentGroup;
            for (int index = 1; index < _activeNodeCount; index++)
            {
                if (_nodes[index].Frequency != _nodes[index - 1].Frequency)
                {
                    currentGroup = AllocateGroup();
                    _groupLeaders[currentGroup] = index;
                }
                _nodes[index].Group = currentGroup;
            }
            _mostRecentNode = _symbolNodes[_symbolCount - 1];
            _totalFrequency = _nodes[0].Frequency;
            _nodes[0].Frequency = ushort.MaxValue;
        }
    }

    private struct Node(bool isLeaf, int symbol, int frequency, int group, int parent)
    {
        public bool IsLeaf = isLeaf;
        public int Symbol = symbol;
        public int Frequency = frequency;
        public int Group = group;
        public int Parent = parent;
        public int ChildIndex;
    }
}
