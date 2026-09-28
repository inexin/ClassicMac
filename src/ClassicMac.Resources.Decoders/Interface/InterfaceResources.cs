using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Interface
{
    /// <summary>A menu (<c>'MENU'</c>, <i>Inside Macintosh: Macintosh Toolbox Essentials</i>, Menu Manager).</summary>
    /// <param name="Id">The menu ID.</param>
    /// <param name="Width">The width stored (the Menu Manager recalculates it).</param>
    /// <param name="Height">The height stored (recalculated too).</param>
    /// <param name="Definition">The menu definition procedure's resource ID (<c>'MDEF'</c>; 0 is the standard one).</param>
    /// <param name="EnableFlags">Bit 0 enables the menu, bits 1–31 items 1–31.</param>
    /// <param name="Title">The title (an apple character, $14, for the Apple menu).</param>
    /// <param name="Items">The items.</param>
    public sealed record MenuResource(short Id, short Width, short Height, short Definition, uint EnableFlags, string Title, IReadOnlyList<MenuItem> Items)
    {
        /// <summary>Whether the menu itself is enabled.</summary>
        public bool Enabled => (EnableFlags & 1) != 0;
    }

    /// <summary>A menu item.</summary>
    /// <param name="Text">The text.</param>
    /// <param name="Icon">The icon number (the icon is resource 256 + number), or 0; with key equivalent $1C, a script code.</param>
    /// <param name="KeyEquivalent">The Command-key character, or 0; $1A–$1E are codes, not keys (<see cref="KeyKind"/>).</param>
    /// <param name="Mark">The mark character (a submenu's menu ID when the key equivalent is $1B), or 0.</param>
    /// <param name="Face">QuickDraw style bits.</param>
    /// <param name="Enabled">Its enable bit (items past 31 have none and are enabled).</param>
    public sealed record MenuItem(string Text, byte Icon, byte KeyEquivalent, byte Mark, byte Face, bool Enabled)
    {
        /// <summary>
        /// A divider line: text starting with '-', and no icon or Command key (the codes $1B and $1C leave none; $1A, $1D
        /// and $1E still look an icon up, so they are not counted as dividers).
        /// </summary>
        public bool IsDivider => Text.StartsWith('-') && Icon == 0 && KeyEquivalent is 0 or 0x1B or 0x1C;

        /// <summary>The submenu's ID, or null.</summary>
        public int? Submenu => KeyEquivalent == 0x1B ? Mark : null;

        /// <summary>What a key equivalent of $1A–$1E means, or null for a Command key or none.</summary>
        public string? KeyKind => KeyEquivalent switch
        {
            0x1A => "smallIconNoMark",
            0x1B => "submenu",
            0x1C => "script",
            0x1D => "shrunkenIcon",
            0x1E => "smallIcon",
            _ => null,
        };
    }

    /// <summary>A window template (<c>'WIND'</c>) or a dialog template (<c>'DLOG'</c>, which adds its item list).</summary>
    /// <param name="Bounds">The content area, in global coordinates.</param>
    /// <param name="Definition">The window definition ID (<c>'WDEF'</c> resource × 16 + variation).</param>
    /// <param name="Visible">Whether it is shown when made.</param>
    /// <param name="GoAway">Whether it has a close box.</param>
    /// <param name="RefCon">The application's value.</param>
    /// <param name="Title">The title.</param>
    /// <param name="ItemsId">A dialog's item list (<c>'DITL'</c>) ID; null for a window.</param>
    /// <param name="Position">The positioning word after the title, or null when the resource has none.</param>
    public sealed record WindowTemplate(MacRect Bounds, short Definition, bool Visible, bool GoAway, int RefCon, string Title, short? ItemsId,
        ushort? Position);

    /// <summary>An alert template (<c>'ALRT'</c>).</summary>
    /// <param name="Bounds">The alert's rectangle, in global coordinates.</param>
    /// <param name="ItemsId">Its item list (<c>'DITL'</c>) ID.</param>
    /// <param name="Stages">The stages word: four 4-bit entries, stage 4 in the highest.</param>
    /// <param name="Position">The positioning word, or null when the resource has none.</param>
    public sealed record AlertTemplate(MacRect Bounds, short ItemsId, ushort Stages, ushort? Position)
    {
        /// <summary>Stage <paramref name="n"/> (1–4): the default button (1 or 2), whether the alert is drawn, and the sound (0–3).</summary>
        public (int BoldItem, bool Drawn, int Sound) Stage(int n)
        {
            var entry = (Stages >> ((n - 1) * 4)) & 0xF;
            return ((entry & 8) != 0 ? 2 : 1, (entry & 4) != 0, entry & 3);
        }
    }

    /// <summary>A dialog item (<c>'DITL'</c>).</summary>
    /// <param name="Bounds">Its rectangle, in the dialog's local coordinates.</param>
    /// <param name="Type">The item type, without the disable bit: 0 user, 1 help, 4 button, 5 check box, 6 radio button,
    /// 7 control, 8 static text, 16 editable text, 32 icon, 64 picture.</param>
    /// <param name="Enabled">False when the disable bit ($80) is set.</param>
    /// <param name="Text">The text of a button, check box, radio button, static or editable text item; null otherwise.</param>
    /// <param name="ResourceId">The <c>'CNTL'</c>, <c>'ICON'</c>/<c>'cicn'</c> or <c>'PICT'</c> ID of a control, icon or picture item;
    /// a help item's <c>'hdlg'</c> or <c>'hrct'</c> ID.</param>
    /// <param name="Data">The item's data as stored.</param>
    public sealed record DialogItem(MacRect Bounds, int Type, bool Enabled, string? Text, short? ResourceId, ReadOnlyMemory<byte> Data);

    /// <summary>A control template (<c>'CNTL'</c>).</summary>
    /// <param name="Bounds">Its rectangle, in the window's local coordinates.</param>
    /// <param name="Value">The initial value.</param>
    /// <param name="Visible">Whether it is shown when made.</param>
    /// <param name="Maximum">The maximum.</param>
    /// <param name="Minimum">The minimum.</param>
    /// <param name="Definition">The control definition ID (<c>'CDEF'</c> resource × 16 + variation).</param>
    /// <param name="RefCon">The application's value.</param>
    /// <param name="Title">The title.</param>
    public sealed record ControlTemplate(MacRect Bounds, short Value, bool Visible, short Maximum, short Minimum, short Definition, int RefCon, string Title);

    /// <summary>Reads the Toolbox's interface resources. Short data gives what could be read and a <c>ui.short</c> warning.</summary>
    public static class InterfaceResources
    {
        /// <summary>A <c>'MENU'</c>: ID, width, height, MDEF ID, a filler word, the enable flags, the title, then items until a 0 length byte.</summary>
        public static MenuResource ReadMenu(ReadOnlySpan<byte> data, DecodeOptions options, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var id = r.I16();
            var width = r.I16();
            var height = r.I16();
            var definition = r.I16();
            r.Skip(2);
            var flags = (uint)r.I32();
            var title = r.Pascal(options);
            var items = new List<MenuItem>();
            while (!r.Short && r.Remaining > 0 && r.Peek() != 0)
            {
                var text = r.Pascal(options);
                byte icon = r.U8(), key = r.U8(), mark = r.U8(), face = r.U8();
                var n = items.Count + 1;
                var item = new MenuItem(text, icon, key, mark, face, n > 31 || (flags & (1u << n)) != 0);
                items.Add(item.IsDivider ? item with { Enabled = false } : item); // a divider is never enabled
            }
            if (!r.Short && r.Remaining == 0) r.Short = true; // no terminating 0
            Report(r, diagnostics, what);
            return new MenuResource(id, width, height, definition, flags, title, items);
        }

        /// <summary>An <c>'MBAR'</c>: a count, then that many menu IDs.</summary>
        public static IReadOnlyList<short> ReadMenuBar(ReadOnlySpan<byte> data, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var count = r.I16();
            var ids = new List<short>();
            for (var i = 0; i < count && !r.Short; i++)
            {
                var menu = r.I16();
                if (!r.Short) ids.Add(menu);
            }
            Report(r, diagnostics, what);
            return ids;
        }

        /// <summary>
        /// A <c>'WIND'</c> (<paramref name="dialog"/> false) or <c>'DLOG'</c>: bounds, definition ID, visible, a filler byte,
        /// close box, a filler byte, reference value, (a dialog's item list ID,) title, then the positioning word at the
        /// next even offset when there is room for it.
        /// </summary>
        public static WindowTemplate ReadWindow(ReadOnlySpan<byte> data, bool dialog, DecodeOptions options, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var bounds = r.Rect();
            var definition = r.I16();
            var visible = r.U8() != 0;
            r.Skip(1);
            var goAway = r.U8() != 0;
            r.Skip(1);
            var refCon = r.I32();
            short? items = dialog ? r.I16() : null;
            var title = r.Pascal(options);
            var position = r.OptionalWord(align: true);
            Report(r, diagnostics, what);
            return new WindowTemplate(bounds, definition, visible, goAway, refCon, title, items, position);
        }

        /// <summary>An <c>'ALRT'</c>: bounds, item list ID, stages word, then the positioning word when there is room for it.</summary>
        public static AlertTemplate ReadAlert(ReadOnlySpan<byte> data, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var bounds = r.Rect();
            var items = r.I16();
            var stages = (ushort)r.I16();
            var position = r.OptionalWord(align: false);
            Report(r, diagnostics, what);
            return new AlertTemplate(bounds, items, stages, position);
        }

        /// <summary>
        /// A <c>'DITL'</c>: the item count less 1, then per item a placeholder long, its rectangle, its type (bit 7 disables
        /// it), its data's length and the data, padded to an even length.
        /// </summary>
        public static IReadOnlyList<DialogItem> ReadDialogItems(ReadOnlySpan<byte> data, DecodeOptions options, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var count = r.I16() + 1;
            var items = new List<DialogItem>();
            for (var i = 0; i < count && !r.Short; i++)
            {
                r.Skip(4);
                var bounds = r.Rect();
                var type = r.U8();
                var length = r.U8();
                var itemData = r.Bytes(length);
                if (r.Short) break;
                if ((length & 1) != 0 && r.Remaining > 0) r.Skip(1);
                var kind = type & 0x7F;
                string? text = kind is 4 or 5 or 6 or 8 or 16 ? MacText.Decode(itemData, options) : null;
                short? resource = kind is 7 or 32 or 64 && itemData.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(itemData)
                    : kind == 1 && itemData.Length >= 4 ? BinaryPrimitives.ReadInt16BigEndian(itemData[2..]) : null;
                items.Add(new DialogItem(bounds, kind, (type & 0x80) == 0, text, resource, itemData.ToArray()));
            }
            Report(r, diagnostics, what);
            return items;
        }

        /// <summary>A <c>'CNTL'</c>: bounds, value, visible, a filler byte, maximum, minimum, definition ID, reference value, title.</summary>
        public static ControlTemplate ReadControl(ReadOnlySpan<byte> data, DecodeOptions options, ICollection<Diagnostic> diagnostics, string what)
        {
            var r = new Reader(data);
            var bounds = r.Rect();
            var value = r.I16();
            var visible = r.U8() != 0;
            r.Skip(1);
            var maximum = r.I16();
            var minimum = r.I16();
            var definition = r.I16();
            var refCon = r.I32();
            var title = r.Pascal(options);
            Report(r, diagnostics, what);
            return new ControlTemplate(bounds, value, visible, maximum, minimum, definition, refCon, title);
        }

        private static void Report(Reader r, ICollection<Diagnostic> diagnostics, string what)
        {
            if (r.Short) diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "ui.short", $"{what}: the data ends early; read as far as it goes."));
        }

        // Big-endian fields in order; reading past the end gives zeros and sets Short.
        private ref struct Reader(ReadOnlySpan<byte> data)
        {
            private readonly ReadOnlySpan<byte> data = data;
            private int at;

            public bool Short { get; set; }

            public readonly int Remaining => Math.Max(0, data.Length - at);

            public readonly byte Peek() => at < data.Length ? data[at] : (byte)0;

            private bool Take(int n)
            {
                if (at + n <= data.Length) return true;
                Short = true;
                at = data.Length;
                return false;
            }

            public void Skip(int n)
            {
                if (Take(n)) at += n;
            }

            public byte U8() => Take(1) ? data[at++] : (byte)0;

            public short I16()
            {
                if (!Take(2)) return 0;
                at += 2;
                return BinaryPrimitives.ReadInt16BigEndian(data[(at - 2)..]);
            }

            public int I32()
            {
                if (!Take(4)) return 0;
                at += 4;
                return BinaryPrimitives.ReadInt32BigEndian(data[(at - 4)..]);
            }

            public MacRect Rect() => new(I16(), I16(), I16(), I16());

            public ReadOnlySpan<byte> Bytes(int n)
            {
                var available = Math.Min(n, Remaining);
                var bytes = data.Slice(at, available);
                at += available;
                if (available < n) Short = true;
                return bytes;
            }

            public string Pascal(DecodeOptions options)
            {
                if (!Take(1)) return "";
                var length = data[at++];
                return MacText.Decode(Bytes(length), options);
            }

            // A trailing word, at an even offset when aligned; null when the data ends first.
            public ushort? OptionalWord(bool align)
            {
                if (Short) return null;
                var offset = align ? (at + 1) & ~1 : at;
                if (offset + 2 > data.Length) return null;
                at = offset + 2;
                return BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            }
        }
    }
}
