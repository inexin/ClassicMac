using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One line of the hex view: offset, 16 bytes in hex, and those bytes as Mac Roman characters.</summary>
    /// <param name="CursorColumn">The character column of the byte the edit cursor is on (in <see cref="Hex"/>), or -1.</param>
    public sealed record HexLine(string Offset, string Hex, string Characters, int CursorColumn = -1)
    {
        /// <summary>The offset as the grid shows it: "0x" and six hex digits, more when needed.</summary>
        public string DisplayOffset => "0x" + Offset.TrimStart('0').PadLeft(6, '0');

        /// <summary>The line's bytes one by one, for the grid; while editing, the append position too.</summary>
        public IReadOnlyList<HexCell> Cells { get; init; } = [];

        private string Padded => Hex.PadRight(CursorColumn + 2);

        /// <summary>The hex text before the cursor byte (all of it when the cursor is elsewhere).</summary>
        public string Before => CursorColumn < 0 ? Hex : Padded[..CursorColumn];

        /// <summary>The cursor byte's two digits (blank at the end of the data), or nothing.</summary>
        public string At => CursorColumn < 0 ? "" : Padded.Substring(CursorColumn, 2);

        /// <summary>The hex text after the cursor byte.</summary>
        public string After => CursorColumn < 0 ? "" : Padded[(CursorColumn + 2)..];
    }

    /// <summary>
    /// One byte of the hex grid (design/boards/hex.md): its two digits and Mac OS Roman character ("·" for non-printables),
    /// whether it is zero (drawn lighter), changed (tinted), under the cursor, and the last of a group of eight (a gap
    /// follows). The append position at the end is a cell with no digits.
    /// </summary>
    /// <param name="IsSelected">Selected in the read-only view (a click): the pair highlights in both columns.</param>
    /// <param name="IsInField">Part of the field the inspected byte belongs to (E8).</param>
    /// <param name="IsMatch">Part of the match Find found.</param>
    public sealed record HexCell(long Offset, string Hex, string Character, bool IsZero, bool IsChanged, bool IsCursor, bool IsGroupEnd,
        bool IsSelected = false, bool IsInField = false, bool IsMatch = false)
    {
        /// <summary>A non-printable byte, shown as a muted "·".</summary>
        public bool IsPlaceholder => Character == "·";
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
        private Func<int, bool>? changed;
        private long selected = -1;
        private (long Start, long Length)? field;
        private (long Start, long Length)? match;
        private long cachedBlock = -1;
        private byte[] cache = [];

        /// <summary>
        /// No lines: what the hex list shows when there is nothing to show. An items control whose source becomes null
        /// walks the old one item by item (Avalonia's ItemCollection raises a Remove of every item); one whose source
        /// becomes another list does not, so the list never goes null.
        /// </summary>
        public static HexLines None { get; } = new(ForkData.Empty);

        /// <summary>The grid's column header: 00 to 0F, a gap after the eighth.</summary>
        public static IReadOnlyList<HexCell> HeaderCells { get; } =
            [.. Enumerable.Range(0, BytesPerLine).Select(i => new HexCell(i, i.ToString("X2", CultureInfo.InvariantCulture), "", false, false, false, i == 7))];

        public HexLines(ForkData data)
        {
            this.data = data;
            Count = (int)Math.Min(int.MaxValue, (data.Length + BytesPerLine - 1) / BytesPerLine);
        }

        public int Count { get; private set; }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        /// <summary>Shows edited bytes, with the edit cursor on byte <paramref name="cursorOffset"/> (which may be the
        /// length, to append). There is always a line for the cursor at the end.</summary>
        public void Reload(ForkData newData, int cursorOffset, Func<int, bool>? isChanged = null, (int Start, int Length)? meaningField = null)
        {
            data = newData;
            editing = true;
            cursor = cursorOffset;
            changed = isChanged;
            field = meaningField;
            cachedBlock = -1;
            Count = (int)Math.Min(int.MaxValue, data.Length / BytesPerLine + 1);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Marks a byte selected in the read-only view (-1 for none) and the field of its meaning (null for none).
        /// </summary>
        public void Select(long offset, (int Start, int Length)? meaningField)
        {
            selected = offset;
            field = meaningField;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>Highlights the bytes Find found (until the next find; these lines are made anew for another selection).</summary>
        public void ShowMatch(long start, int length)
        {
            match = (start, length);
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
                var cells = new List<HexCell>(BytesPerLine);
                for (var i = 0; i < BytesPerLine; i++)
                {
                    var byteOffset = offset + i;
                    if (i < line.Length)
                    {
                        var ch = MacRoman.ToChar(line[i]);
                        cells.Add(new HexCell(byteOffset, line[i].ToString("X2", CultureInfo.InvariantCulture), ch < ' ' || ch == '\u007F' ? "·" : ch.ToString(),
                            line[i] == 0, editing && byteOffset <= int.MaxValue && changed?.Invoke((int)byteOffset) == true, editing && byteOffset == cursor, i == 7,
                            !editing && byteOffset == selected, field is var (start, length) && byteOffset >= start && byteOffset < start + length,
                            match is var (from, count) && byteOffset >= from && byteOffset < from + count));
                    }
                    else if (editing && byteOffset == cursor)
                    {
                        cells.Add(new HexCell(byteOffset, "", "", false, false, true, i == 7));
                    }

                    if (i == 8)
                    {
                        hex.Append(' ');
                    }

                    if (i < line.Length)
                    {
                        hex.Append(line[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                        var c = MacRoman.ToChar(line[i]);
                        text.Append(c < ' ' || c == '\u007F' ? '·' : c);
                    }
                    else
                    {
                        hex.Append("   ");
                    }
                }
                var column = cursor >= offset && cursor < offset + BytesPerLine ? (int)(cursor - offset) * 3 + (cursor - offset >= 8 ? 1 : 0) : -1;
                return new HexLine(offset.ToString("X8", CultureInfo.InvariantCulture), hex.ToString().TrimEnd(), text.ToString(), editing ? column : -1)
                {
                    Cells = cells,
                };
            }
        }

        public IEnumerator<HexLine> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
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
            for (var i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
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

        /// <summary>A resource's bytes; the view shows them for resources with no preview.</summary>
        public static HexViewModel For(ResourceNode node) =>
            new([new HexSource($"{node.Resource} ({node.Resource.Length:N0} bytes)", ForkData.FromBytes(node.Resource.GetData()))]);
    }
}
