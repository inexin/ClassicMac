using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Input;
using ClassicMac.Files;
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
        private bool half;

        public HexEditor(ReadOnlyMemory<byte> data)
        {
            original = data.ToArray();
            bytes = [.. original];
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
        [NotifyPropertyChangedFor(nameof(Status))]
        private bool insertMode;

        public int Length => bytes.Count;

        public bool IsModified => !bytes.SequenceEqual(original);

        public string Status => string.Create(CultureInfo.InvariantCulture,
            $"Offset ${Cursor:X} of {bytes.Count:N0} bytes · {(InsertMode ? "insert" : "overwrite")}{(IsModified ? " · changed" : "")}");

        public byte[] ToArray() => [.. bytes];

        /// <summary>Types a hex digit (0–15).</summary>
        public void TypeDigit(int digit)
        {
            if (digit is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(digit));
            if (!half)
            {
                if (InsertMode || Cursor >= bytes.Count) bytes.Insert(Cursor, (byte)(digit << 4));
                else bytes[Cursor] = (byte)((bytes[Cursor] & 0x0F) | (digit << 4));
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
            if (Cursor < bytes.Count) bytes.RemoveAt(Cursor);
            Reload();
            RaiseEdited();
        }

        /// <summary>Removes the byte before the cursor.</summary>
        public void Backspace()
        {
            half = false;
            if (Cursor > 0) bytes.RemoveAt(--Cursor);
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
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return false;
            var digit = key switch
            {
                >= Key.D0 and <= Key.D9 when modifiers == KeyModifiers.None => key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
                >= Key.A and <= Key.F => key - Key.A + 10,
                _ => -1,
            };
            if (digit >= 0) { TypeDigit(digit); return true; }
            switch (key)
            {
                case Key.Left: Move(-1); return true;
                case Key.Right: Move(1); return true;
                case Key.Up: Move(-16); return true;
                case Key.Down: Move(16); return true;
                case Key.PageUp: Move(-256); return true;
                case Key.PageDown: Move(256); return true;
                case Key.Home: MoveTo(Cursor / 16 * 16); return true;
                case Key.End: MoveTo(Math.Min(Cursor / 16 * 16 + 15, bytes.Count)); return true;
                case Key.Insert: ToggleInsert(); return true;
                case Key.Delete: Delete(); return true;
                case Key.Back: Backspace(); return true;
                default: return false;
            }
        }

        public void ToggleInsert() => InsertMode = !InsertMode;

        private void Reload()
        {
            Lines.Reload(ForkData.FromBytes(bytes.ToArray()), Cursor);
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(Length));
        }
    }
}
