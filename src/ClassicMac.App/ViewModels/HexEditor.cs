using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Input;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources.Decoders.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// Edits a resource's bytes in the hex view, as ResEdit's hex editor does: hex digits overwrite the byte under the
    /// cursor two digits at a time (or insert, in insert mode; at the end they append), Delete and Backspace remove bytes.
    /// </summary>
    public sealed partial class HexEditor : ObservableObject
    {
        private readonly byte[] original;
        private readonly List<byte> bytes;
        private readonly Func<byte[], int, ByteMeaning?>? meaning;
        private bool half;

        /// <summary>
        /// An editor of <paramref name="data"/>; <paramref name="meaning"/>, when given, says what the byte at an offset
        /// of the bytes (as edited) means and which field it is in (design/boards/hex.md, E8): the <see cref="Inspector"/>
        /// shows it and the grid highlights the field.
        /// </summary>
        public HexEditor(ReadOnlyMemory<byte> data, Func<byte[], int, ByteMeaning?>? meaning = null)
        {
            original = data.ToArray();
            bytes = [.. original];
            this.meaning = meaning;
            Lines = new HexLines(ForkData.FromBytes(original));
            Reload();
        }

        /// <summary>The lines shown, redrawn after every edit.</summary>
        public HexLines Lines { get; }

        /// <summary>The offset of the byte the cursor is on; the length when at the end.</summary>
        public int Cursor { get; private set; }

        /// <summary>The line the cursor is on.</summary>
        public int CursorLine => Cursor / 16;

        /// <summary>Whether typing inserts bytes instead of overwriting them.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(ModeIndex))]
        private bool insertMode;

        /// <summary>The footer's switch, in <see cref="Modes"/> order: 0 overwrite, 1 insert.</summary>
        public int ModeIndex
        {
            get => InsertMode ? 1 : 0;
            set => InsertMode = value == 1;
        }

        /// <summary>The switch's segments.</summary>
        public static IReadOnlyList<string> Modes { get; } = ["Overwrite", "Insert"];

        public int Length => bytes.Count;

        public bool IsModified => !bytes.SequenceEqual(original);

        /// <summary>"0x000A = 32 · 1 byte changed · hex digits type, Insert toggles, Delete removes".</summary>
        public string Status
        {
            get
            {
                var value = Cursor < bytes.Count ? bytes[Cursor].ToString(CultureInfo.InvariantCulture) : "end";
                var changed = ChangedCount switch
                {
                    0 => "no changes",
                    1 => "1 byte changed",
                    var n => string.Create(CultureInfo.InvariantCulture, $"{n:N0} bytes changed"),
                };
                return string.Create(CultureInfo.InvariantCulture, $"0x{Cursor:X4} = {value} · {changed} · hex digits type, Insert toggles, Delete removes");
            }
        }

        /// <summary>Whether the byte at <paramref name="offset"/> differs from the original's at that offset (or is past its end).</summary>
        public bool IsChanged(int offset) => offset >= 0 && offset < bytes.Count && (offset >= original.Length || bytes[offset] != original[offset]);

        /// <summary>How many bytes differ from the original's at their offsets.</summary>
        public int ChangedCount { get; private set; }

        /// <summary>The byte inspector: the bytes at the cursor read as numbers, and their meaning when known.</summary>
        public HexInspection Inspector { get; private set; } = HexInspection.Empty;

        /// <summary>
        /// Moves the cursor to a hex offset ("0x1A", "$1A" or "1A"; past the end goes to the end); false, and no move,
        /// when the text is not one.
        /// </summary>
        public bool GoTo(string text)
        {
            var digits = text.Trim();
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[2..];
            }
            else if (digits.StartsWith('$'))
            {
                digits = digits[1..];
            }

            if (digits.Length == 0 || !digits.All(Uri.IsHexDigit)
                || !long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset) || offset > int.MaxValue)
            {
                return false;
            }

            MoveTo((int)Math.Min(offset, bytes.Count));
            return true;
        }

        public byte[] ToArray() => [.. bytes];

        /// <summary>Types a hex digit (0–15).</summary>
        public void TypeDigit(int digit)
        {
            if (digit is < 0 or > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(digit));
            }

            if (!half)
            {
                if (InsertMode || Cursor >= bytes.Count)
                {
                    bytes.Insert(Cursor, (byte)(digit << 4));
                }
                else
                {
                    bytes[Cursor] = (byte)((bytes[Cursor] & 0x0F) | (digit << 4));
                }

                half = true;
            }
            else
            {
                bytes[Cursor] = (byte)((bytes[Cursor] & 0xF0) | digit);
                Cursor++;
                half = false;
            }
            Reload();
            RaiseEdited();
        }

        /// <summary>Raised after each change to the bytes (typing, Delete, Backspace).</summary>
        public event EventHandler? Edited;

        private void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);

        /// <summary>Removes the byte under the cursor.</summary>
        public void Delete()
        {
            half = false;
            if (Cursor < bytes.Count)
            {
                bytes.RemoveAt(Cursor);
            }

            Reload();
            RaiseEdited();
        }

        /// <summary>Removes the byte before the cursor.</summary>
        public void Backspace()
        {
            half = false;
            if (Cursor > 0)
            {
                bytes.RemoveAt(--Cursor);
            }

            Reload();
            RaiseEdited();
        }

        /// <summary>Moves the cursor to <paramref name="offset"/>, kept between 0 and the length.</summary>
        public void MoveTo(int offset)
        {
            half = false;
            Cursor = Math.Clamp(offset, 0, bytes.Count);
            Reload();
        }

        public void Move(int delta) => MoveTo(Cursor + delta);

        /// <summary>Handles a key of the hex view; false when it is not one the editor uses.</summary>
        public bool OnKey(Key key, KeyModifiers modifiers)
        {
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            {
                return false;
            }

            var digit = key switch
            {
                >= Key.D0 and <= Key.D9 when modifiers == KeyModifiers.None => key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
                >= Key.A and <= Key.F => key - Key.A + 10,
                _ => -1,
            };
            if (digit >= 0)
            {
                TypeDigit(digit);
                return true;
            }
            switch (key)
            {
                case Key.Left:
                    Move(-1);
                    return true;
                case Key.Right:
                    Move(1);
                    return true;
                case Key.Up:
                    Move(-16);
                    return true;
                case Key.Down:
                    Move(16);
                    return true;
                case Key.PageUp:
                    Move(-256);
                    return true;
                case Key.PageDown:
                    Move(256);
                    return true;
                case Key.Home:
                    MoveTo(Cursor / 16 * 16);
                    return true;
                case Key.End:
                    MoveTo(Math.Min(Cursor / 16 * 16 + 15, bytes.Count));
                    return true;
                case Key.Insert:
                    ToggleInsert();
                    return true;
                case Key.Delete:
                    Delete();
                    return true;
                case Key.Back:
                    Backspace();
                    return true;
                default:
                    return false;
            }
        }

        public void ToggleInsert() => InsertMode = !InsertMode;

        private void Reload()
        {
            ChangedCount = Enumerable.Range(0, bytes.Count).Count(IsChanged);
            var data = bytes.ToArray();
            Inspector = HexInspection.At(data, Cursor, meaning);
            OnPropertyChanged(nameof(Inspector));
            OnPropertyChanged(nameof(ChangedCount));
            Lines.Reload(ForkData.FromBytes(data), Cursor, IsChanged, Inspector.Meaning is { } field ? (field.Start, field.Length) : null);
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(Length));
        }
    }

    /// <summary>One reading of the bytes at the cursor: a label and its value ("—" when there are too few bytes).</summary>
    public sealed record HexReading(string Label, string Value);

    /// <summary>
    /// The byte inspector (design/boards/hex.md): "At 0x0003", the bytes at the cursor as UInt8, Int8, UInt16 BE,
    /// Int16 BE, UInt32 BE, OSType and binary, and what the byte means in its resource when a provider knows (E8).
    /// </summary>
    public sealed record HexInspection(string Heading, IReadOnlyList<HexReading> Rows, ByteMeaning? Meaning)
    {
        public static HexInspection Empty { get; } = new("", [], null);

        public static HexInspection At(byte[] data, int offset, Func<byte[], int, ByteMeaning?>? meaning)
        {
            const string None = "—";
            var reader = new BigEndianReader(data);
            string Number<T>(bool read, T value) where T : IFormattable =>
                read ? value.ToString("N0", CultureInfo.InvariantCulture) : None;
            var hasByte = reader.TryReadByteAt(offset, out var b);
            var rows = new List<HexReading>
            {
                new("UInt8", hasByte ? b.ToString(CultureInfo.InvariantCulture) : None),
                new("Int8", hasByte ? ((sbyte)b).ToString(CultureInfo.InvariantCulture) : None),
                new("UInt16 BE", Number(reader.TryReadUInt16At(offset, out var u16), u16)),
                new("Int16 BE", Number(reader.TryReadInt16At(offset, out var i16), i16)),
                new("UInt32 BE", Number(reader.TryReadUInt32At(offset, out var u32), u32)),
                new("OSType", reader.TryReadUInt32At(offset, out var type) ? $"'{new FourCC(type)}'" : None),
                new("Binary", hasByte ? $"{Convert.ToString(b >> 4, 2).PadLeft(4, '0')} {Convert.ToString(b & 0xF, 2).PadLeft(4, '0')}" : None),
            };
            return new HexInspection(string.Create(CultureInfo.InvariantCulture, $"At 0x{offset:X4}"), rows, meaning?.Invoke(data, offset));
        }
    }
}
