using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One line of the hex view: offset, 16 bytes in hex, and those bytes as Mac Roman characters.</summary>
    /// <param name="CursorColumn">The character column of the byte the edit cursor is on (in <see cref="Hex"/>), or -1.</param>
    public sealed record HexLine(string Offset, string Hex, string Characters, int CursorColumn = -1)
    {
        private string Padded => Hex.PadRight(CursorColumn + 2);

        /// <summary>The hex text before the cursor byte (all of it when the cursor is elsewhere).</summary>
        public string Before => CursorColumn < 0 ? Hex : Padded[..CursorColumn];

        /// <summary>The cursor byte's two digits (blank at the end of the data), or nothing.</summary>
        public string At => CursorColumn < 0 ? "" : Padded.Substring(CursorColumn, 2);

        /// <summary>The hex text after the cursor byte.</summary>
        public string After => CursorColumn < 0 ? "" : Padded[(CursorColumn + 2)..];
    }

    /// <summary>
    /// The hex view of some data: a list of 16-byte lines made when shown, read 64 KB at a time, so a disk image's whole
    /// data fork scrolls without being loaded. (A non-generic <see cref="IList"/>, so list controls can virtualise it.)
    /// </summary>
    public sealed class HexLines : IReadOnlyList<HexLine>, IList, INotifyCollectionChanged
    {
        private const int BytesPerLine = 16, Block = 64 * 1024;
        private ForkData data;
        private bool editing;
        private int cursor = -1;
        private long cachedBlock = -1;
        private byte[] cache = [];

        /// <summary>
        /// No lines: what the hex list shows when there is nothing to show. An items control whose source becomes null
        /// walks the old one item by item (Avalonia's ItemCollection raises a Remove of every item); one whose source
        /// becomes another list does not, so the list never goes null.
        /// </summary>
        public static HexLines None { get; } = new(ForkData.Empty);

        public HexLines(ForkData data)
        {
            this.data = data;
            Count = (int)Math.Min(int.MaxValue, (data.Length + BytesPerLine - 1) / BytesPerLine);
        }

        public int Count { get; private set; }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        /// <summary>Shows edited bytes, with the edit cursor on byte <paramref name="cursorOffset"/> (which may be the
        /// length, to append). There is always a line for the cursor at the end.</summary>
        public void Reload(ForkData newData, int cursorOffset)
        {
            data = newData;
            editing = true;
            cursor = cursorOffset;
            cachedBlock = -1;
            Count = (int)Math.Min(int.MaxValue, data.Length / BytesPerLine + 1);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

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
                var column = cursor >= offset && cursor < offset + BytesPerLine ? (int)(cursor - offset) * 3 + (cursor - offset >= 8 ? 1 : 0) : -1;
                return new HexLine(offset.ToString("X8", CultureInfo.InvariantCulture), hex.ToString().TrimEnd(), text.ToString(), editing ? column : -1);
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
            // Containers (a disk image or archive, opened or inside one) show their contents, not their bytes.
            InputNode i when i.Root.Children.Count == 0 => Forks(i.Root.File),
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
