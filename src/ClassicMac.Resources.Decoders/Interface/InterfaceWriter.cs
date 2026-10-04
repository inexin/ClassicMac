using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Interface;

/// <summary>
/// Writes the Toolbox's interface resources back from the records <see cref="InterfaceResources"/> reads, in the
/// layouts docs/formats/resources/ gives (menus.md, windows-dialogs.md, dialog-items.md, controls.md). Text is Mac
/// OS Roman; text it cannot hold, or a string over 255 bytes, is refused (<see cref="ArgumentException"/>). Filler
/// fields are written as 0 [ClassicMac: the Toolbox ignores them].
/// </summary>
public static class InterfaceWriter
{
    /// <summary>
    /// A <c>'MENU'</c>. The enable flags are <paramref name="menu"/>'s, with the bits of items 1–31 set from their
    /// Enabled, except dividers (read as disabled whatever their bit), whose stored bit is kept, as are the bits past
    /// the last item.
    /// </summary>
    public static byte[] WriteMenu(MenuResource menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var flags = menu.EnableFlags;
        for (var i = 0; i < menu.Items.Count && i < 31; i++)
        {
            if (menu.Items[i].IsDivider)
            {
                continue;
            }

            var bit = 1u << (i + 1);
            flags = menu.Items[i].Enabled ? flags | bit : flags & ~bit;
        }
        var w = new Writer();
        w.I16(menu.Id).I16(menu.Width).I16(menu.Height).I16(menu.Definition).I16(0).I32((int)flags).Pascal(menu.Title);
        foreach (var item in menu.Items)
        {
            if (item.Text.Length == 0)
            {
                throw new ArgumentException("A menu item needs text (an empty one would end the menu).", nameof(menu));
            }

            w.Pascal(item.Text).U8(item.Icon).U8(item.KeyEquivalent).U8(item.Mark).U8(item.Face);
        }
        return w.U8(0).ToArray();
    }

    /// <summary>A <c>'WIND'</c> or, with <paramref name="dialog"/>, a <c>'DLOG'</c>; the positioning word, when there is one, at the next even offset.</summary>
    public static byte[] WriteWindow(WindowTemplate window, bool dialog)
    {
        ArgumentNullException.ThrowIfNull(window);
        var w = new Writer();
        w.Rect(window.Bounds).I16(window.Definition).U8(window.Visible ? (byte)1 : (byte)0).U8(0)
            .U8(window.GoAway ? (byte)1 : (byte)0).U8(0).I32(window.RefCon);
        if (dialog)
        {
            w.I16(window.ItemsId ?? 0);
        }

        w.Pascal(window.Title);
        if (window.Position is { } position)
        {
            w.Align().I16((short)position);
        }

        return w.ToArray();
    }

    /// <summary>An <c>'ALRT'</c>, with its positioning word when there is one.</summary>
    public static byte[] WriteAlert(AlertTemplate alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var w = new Writer();
        w.Rect(alert.Bounds).I16(alert.ItemsId).I16((short)alert.Stages);
        if (alert.Position is { } position)
        {
            w.I16((short)position);
        }

        return w.ToArray();
    }

    /// <summary>
    /// A <c>'DITL'</c>: each item's data made from its record (the text of buttons, check boxes, radio buttons, static
    /// and editable text; the resource ID of controls, icons and pictures; help and user items as stored).
    /// </summary>
    public static byte[] WriteDialogItems(IReadOnlyList<DialogItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("An item list needs at least one item.", nameof(items));
        }

        var w = new Writer();
        w.I16((short)(items.Count - 1));
        foreach (var item in items)
        {
            byte[] data = item.Type switch
            {
                4 or 5 or 6 or 8 or 16 => Mac(item.Text ?? ""),
                7 or 32 or 64 => [(byte)((item.ResourceId ?? 0) >> 8), (byte)(item.ResourceId ?? 0)],
                _ => item.Data.ToArray(),
            };
            if (data.Length > 255)
            {
                throw new ArgumentException("An item's data is at most 255 bytes.", nameof(items));
            }

            w.I32(0).Rect(item.Bounds).U8((byte)((item.Type & 0x7F) | (item.Enabled ? 0 : 0x80))).U8((byte)data.Length).Bytes(data).Align();
        }
        return w.ToArray();
    }

    /// <summary>A <c>'CNTL'</c>.</summary>
    public static byte[] WriteControl(ControlTemplate control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var w = new Writer();
        w.Rect(control.Bounds).I16(control.Value).U8(control.Visible ? (byte)1 : (byte)0).U8(0).I16(control.Maximum).I16(control.Minimum)
            .I16(control.Definition).I32(control.RefCon).Pascal(control.Title);
        return w.ToArray();
    }

    private static byte[] Mac(string text) =>
        MacRoman.TryEncode(text.Replace("\r\n", "\r").Replace('\n', '\r'), out var bytes)
            ? bytes
            : throw new ArgumentException($"“{text}” has characters Mac OS Roman cannot hold.");

    private sealed class Writer
    {
        private readonly MemoryStream output = new();

        public Writer U8(byte v)
        {
            output.WriteByte(v);
            return this;
        }

        public Writer I16(short v) => U8((byte)(v >> 8)).U8((byte)v);

        public Writer I32(int v) => I16((short)(v >> 16)).I16((short)v);

        public Writer Rect(MacRect r) => I16(r.Top).I16(r.Left).I16(r.Bottom).I16(r.Right);

        public Writer Bytes(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            return this;
        }

        public Writer Pascal(string text)
        {
            var bytes = Mac(text);
            if (bytes.Length > 255)
            {
                throw new ArgumentException($"“{text}” is over 255 bytes.");
            }

            return U8((byte)bytes.Length).Bytes(bytes);
        }

        public Writer Align() => (output.Length & 1) != 0 ? U8(0) : this;

        public byte[] ToArray() => output.ToArray();
    }
}
