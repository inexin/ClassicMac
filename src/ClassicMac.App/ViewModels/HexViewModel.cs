using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One line of the hex view: offset, 16 bytes in hex, and those bytes as Mac Roman characters.</summary>
    public sealed record HexLine(string Offset, string Hex, string Characters);

    /// <summary>
    /// The hex view of some data: a list of 16-byte lines made when shown, read 64 KB at a time, so a disk image's whole
    /// data fork scrolls without being loaded. (A non-generic <see cref="IList"/>, so list controls can virtualise it.)
    /// </summary>
    public sealed class HexLines : IReadOnlyList<HexLine>, IList
    {
        private const int BytesPerLine = 16, Block = 64 * 1024;
        private readonly ForkData data;
        private long cachedBlock = -1;
        private byte[] cache = [];

        public HexLines(ForkData data)
        {
            this.data = data;
            Count = (int)Math.Min(int.MaxValue, (data.Length + BytesPerLine - 1) / BytesPerLine);
        }

        public int Count { get; }

        /// <summary>How many bytes have been read so far (for tests).</summary>
        public long BytesRead { get; private set; }

        public HexLine this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                var offset = (long)index * BytesPerLine;
                var block = offset / Block;
                if (block != cachedBlock)
                {
                    var start = block * Block;
                    cache = data.Slice(start, Math.Min(Block, data.Length - start)).ToArray();
                    BytesRead += cache.Length;
                    cachedBlock = block;
                }
                var at = (int)(offset - block * Block);
                var line = cache.AsSpan(at, (int)Math.Min(BytesPerLine, cache.Length - at));
                var hex = new StringBuilder(BytesPerLine * 3);
                var text = new StringBuilder(BytesPerLine);
                for (var i = 0; i < BytesPerLine; i++)
                {
                    if (i == 8) hex.Append(' ');
                    if (i < line.Length)
                    {
                        hex.Append(line[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                        var c = MacRoman.ToChar(line[i]);
                        text.Append(c < ' ' || c == '\u007F' ? '·' : c);
                    }
                    else hex.Append("   ");
                }
                return new HexLine(offset.ToString("X8", CultureInfo.InvariantCulture), hex.ToString().TrimEnd(), text.ToString());
            }
        }

        public IEnumerator<HexLine> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        bool IList.IsFixedSize => true;

        bool IList.IsReadOnly => true;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        int IList.Add(object? value) => throw new NotSupportedException();

        void IList.Clear() => throw new NotSupportedException();

        bool IList.Contains(object? value) => value is HexLine line && ((IList)this).IndexOf(line) >= 0;

        int IList.IndexOf(object? value) =>
            value is HexLine line && int.TryParse(line.Offset, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset)
                && offset / BytesPerLine < Count && this[offset / BytesPerLine] == line ? offset / BytesPerLine : -1;

        void IList.Insert(int index, object? value) => throw new NotSupportedException();

        void IList.Remove(object? value) => throw new NotSupportedException();

        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index)
        {
            for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
        }
    }

    /// <summary>A fork or resource the hex tab can show.</summary>
    public sealed record HexSource(string Label, ForkData Data);

    /// <summary>What the hex tab shows for the selection: its sources (a file's two forks, or a resource) and the lines of the chosen one.</summary>
    public sealed class HexViewModel
    {
        private HexViewModel(IReadOnlyList<HexSource> sources) => Sources = sources;

        public static HexViewModel Empty { get; } = new([]);

        public IReadOnlyList<HexSource> Sources { get; }

        public static HexViewModel For(NodeViewModel? node) => node switch
        {
            ResourceNode r => new([new HexSource($"{r.Resource} ({r.Resource.Length:N0} bytes)", ForkData.FromBytes(r.Resource.GetData()))]),
            FileNode f => Forks(f.File),
            ContainerFileNode c => Forks(c.File),
            InputNode i => Forks(i.Root.File),
            _ => Empty,
        };

        private static HexViewModel Forks(MacFile file)
        {
            var sources = new List<HexSource>();
            if (file.DataFork.Length > 0) sources.Add(new HexSource($"Data fork ({file.DataFork.Length:N0} bytes)", file.DataFork));
            if (file.ResourceFork.Length > 0) sources.Add(new HexSource($"Resource fork ({file.ResourceFork.Length:N0} bytes)", file.ResourceFork));
            return new HexViewModel(sources);
        }
    }
}
