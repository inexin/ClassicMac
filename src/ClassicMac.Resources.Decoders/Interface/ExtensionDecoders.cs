using System;
using System.Collections.Generic;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Interface
{
    /// <summary>
    /// The interface's colour and extension resources as JSON: window, dialog, alert and control colour tables
    /// (<c>wctb</c>, <c>dctb</c>, <c>actb</c>, <c>cctb</c>), menu colours (<c>mctb</c>), dialog item colours and fonts
    /// (<c>ictb</c>, read with the <c>DITL</c> of the same ID), and the Appearance extensions (<c>dlgx</c>, <c>alrx</c>,
    /// <c>xmnu</c>).
    /// </summary>
    internal sealed class ExtensionDecoder(DecodeOptions options, string name, string[] types, Func<ExtensionDecoder, DecodeInput, byte[]> write)
        : IResourceDecoder, IBuiltInDecoder
    {
        private readonly HashSet<FourCC> handled = [.. Array.ConvertAll(types, FourCC.FromString)];

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC type) => handled.Contains(type);

        public IReadOnlyCollection<FourCC> Types => handled;

        internal DecodeOptions Options => options;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input) => [new DecodedFile(".json", write(this, input))];

        public static IEnumerable<IResourceDecoder> All(DecodeOptions options) =>
        [
            new ExtensionDecoder(options, "ui.colors", ["wctb", "dctb", "actb", "cctb"], (_, i) => PartColors(i)),
            new ExtensionDecoder(options, "ui.menu-colors", ["mctb"], (_, i) => MenuColors(i)),
            new ExtensionDecoder(options, "ui.item-colors", ["ictb"], (d, i) => ItemColors(i, d.Options)),
            new ExtensionDecoder(options, "ui.dialog-extension", ["dlgx"], (_, i) => DialogExtension(i)),
            new ExtensionDecoder(options, "ui.alert-extension", ["alrx"], (d, i) => AlertExtension(i, d.Options)),
            new ExtensionDecoder(options, "ui.menu-extension", ["xmnu"], (_, i) => MenuExtension(i)),
        ];

        private static readonly string[] WindowParts =
        [
            "content", "frame", "text", "hilite", "titleBar", "hiliteLight", "hiliteDark", "titleBarLight", "titleBarDark",
            "dialogLight", "dialogDark", "tingeLight", "tingeDark",
        ];

        private static readonly string[] ControlParts =
        [
            "frame", "body", "text", "thumb", "fillPattern", "arrowsLight", "arrowsDark", "thumbLight", "thumbDark",
            "hiliteLight", "hiliteDark", "titleBarLight", "titleBarDark", "tingeLight", "tingeDark",
        ];

        // A colour table (seed, flags, count − 1, then value and RGB): the part codes of windows, dialogs and alerts, or of controls.
        private static byte[] PartColors(DecodeInput input)
        {
            var parts = input.Resource.Type.ToString() == "cctb" ? ControlParts : WindowParts;
            return Json(input, (w, data) =>
            {
                w.WriteStartObject();
                ColorTable(w, data, parts, input);
                w.WriteEndObject();
            });
        }

        internal static void ColorTable(Utf8JsonWriter w, ReadOnlySpan<byte> data, string[] parts, DecodeInput input)
        {
            if (data.Length < 8)
            {
                Short(input);
                w.WriteStartArray("entries");
                w.WriteEndArray();
                return;
            }
            var reader = new BigEndianReader(data);
            w.WriteNumber("seed", reader.ReadInt32());
            w.WriteNumber("flags", reader.ReadUInt16());
            var count = reader.ReadInt16() + 1;
            w.WriteStartArray("entries");
            for (var i = 0; i < count; i++)
            {
                var at = 8 + i * 8;
                if (at + 8 > data.Length)
                {
                    Short(input);
                    break;
                }
                var part = reader.ReadInt16At(at);
                w.WriteStartObject();
                w.WriteNumber("value", part);
                if (part >= 0 && part < parts.Length) w.WriteString("part", parts[part]);
                Rgb(w, data[(at + 2)..]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // mctb: a count, then 30-byte entries (menu ID, item, four colours, reserved); the last has ID −99. What the
        // colours mean depends on the entry: the menu bar (ID 0), a menu's title (item 0) or an item.
        private static byte[] MenuColors(DecodeInput input)
        {
            return Json(input, (w, data) =>
            {
                w.WriteStartObject();
                var reader = new BigEndianReader(data);
                var count = reader.TryReadInt16At(0, out var entryCount) ? entryCount : 0;
                if (data.Length < 2) Short(input);
                w.WriteStartArray("entries");
                for (var i = 0; i < count; i++)
                {
                    var at = 2 + i * 30;
                    if (at + 30 > data.Length)
                    {
                        Short(input);
                        break;
                    }
                    var menu = reader.ReadInt16At(at);
                    var item = reader.ReadInt16At(at + 2);
                    string[] names = menu == 0 ? ["titles", "menuBackground", "items", "menuBar"]
                        : item == 0 ? ["title", "menuBackground", "items", "menuBar"]
                        : ["mark", "text", "key", "background"];
                    w.WriteStartObject();
                    w.WriteNumber("menu", menu);
                    w.WriteNumber("item", item);
                    w.WriteString("kind", menu == -99 ? "end" : menu == 0 ? "menuBar" : item == 0 ? "title" : "item");
                    for (var c = 0; c < 4; c++)
                    {
                        w.WriteStartObject(names[c]);
                        Rgb(w, data[(at + 4 + c * 6)..]);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        // ictb: one 4-byte entry per item of the DITL of the same ID (data word, offset from the ictb's start). A control
        // item's data is the length of a control colour table at the offset; a text item's are flags for a 20-byte text
        // style at the offset.
        private static byte[] ItemColors(DecodeInput input, DecodeOptions options)
        {
            var ditl = input.Find(FourCC.FromString("DITL"), input.Resource.Id);
            var items = ditl is { } list ? InterfaceResources.ReadDialogItems(list.Span, options, new List<Diagnostic>(), "") : [];
            return Json(input, (w, data) =>
            {
                w.WriteStartObject();
                w.WriteBoolean("itemList", ditl is not null);
                w.WriteStartArray("items");
                var reader = new BigEndianReader(data);
                var entries = ditl is null ? data.Length / 4 : items.Count;
                for (var i = 0; i < entries; i++)
                {
                    if (i * 4 + 4 > data.Length)
                    {
                        Short(input);
                        break;
                    }
                    var value = reader.ReadInt16At(i * 4);
                    var offset = reader.ReadInt16At(i * 4 + 2);
                    w.WriteStartObject();
                    w.WriteNumber("number", i + 1);
                    w.WriteNumber("data", value);
                    w.WriteNumber("offset", offset);
                    var type = i < items.Count ? items[i].Type : -1;
                    if (type is 4 or 5 or 6 or 7 && value > 0 && offset >= 0 && offset < data.Length)
                    {
                        w.WriteStartObject("colors");
                        ColorTable(w, data[offset..Math.Min(data.Length, offset + value)], ControlParts, input);
                        w.WriteEndObject();
                    }
                    else if (type is 8 or 16 && value != 0 && offset >= 0 && offset + 20 <= data.Length)
                    {
                        TextStyle(w, data, value, offset, options);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        // A text style: font (or, with flag bit 15, an offset to its name), face (high byte), size, foreground, background, mode.
        private static void TextStyle(Utf8JsonWriter w, ReadOnlySpan<byte> data, short flags, int offset, DecodeOptions options)
        {
            var style = data[offset..];
            var reader = new BigEndianReader(style);
            w.WriteStartObject("textStyle");
            w.WriteNumber("flags", (ushort)flags);
            var font = reader.ReadInt16At(0);
            if ((flags & 0x8000) != 0 && font >= 0 && font < data.Length)
            {
                var at = (int)font;
                MacText.TryReadPascal(data, ref at, out var fontName);
                w.WriteString("fontName", MacText.Decode(fontName, options));
            }
            else if ((flags & 1) != 0)
            {
                w.WriteNumber("font", font);
            }
            if ((flags & 2) != 0)
            {
                w.WriteNumber("face", style[2]);
            }
            if ((flags & 4) != 0)
            {
                w.WriteNumber("size", reader.ReadInt16At(4));
                w.WriteBoolean("addSize", (flags & 0x10) != 0);
            }
            if ((flags & 8) != 0)
            {
                w.WriteStartObject("foreground");
                Rgb(w, style[6..]);
                w.WriteEndObject();
            }
            if ((flags & 0x2000) != 0)
            {
                w.WriteStartObject("background");
                Rgb(w, style[12..]);
                w.WriteEndObject();
            }
            if ((flags & 0x4000) != 0) w.WriteNumber("mode", reader.ReadInt16At(18));
            w.WriteEndObject();
        }

        private static readonly string[] AppearanceFlags = ["useThemeBackground", "useControlHierarchy", "handleMovableModal", "useThemeControls"];

        // dlgx: version, flags.
        private static byte[] DialogExtension(DecodeInput input)
        {
            return Json(input, (w, data) =>
            {
                if (data.Length < 6) Short(input);
                w.WriteStartObject();
                var reader = new BigEndianReader(data);
                w.WriteNumber("version", reader.TryReadInt16At(0, out var version) ? version : 0);
                Flags(w, reader.TryReadUInt32At(2, out var flags) ? flags : 0, AppearanceFlags);
                w.WriteEndObject();
            });
        }

        // alrx: version, flags (bit 2 movable), refCon, theme window byte, a filler; the title at +12 (version 1) or +28
        // (version 0; one too short for that is read as version 1).
        private static byte[] AlertExtension(DecodeInput input, DecodeOptions options)
        {
            var head = input.Data.Span;
            if (head.Length < 12) Short(input);
            var version = new BigEndianReader(head).TryReadInt16At(0, out var word) ? word : (short)0;
            var titleAt = version == 0 && head.Length >= 0x1D && 0x1C + 1 + head[0x1C] <= head.Length ? 0x1C : 12;
            var title = "";
            if (head.Length > titleAt)
            {
                var at = titleAt;
                MacText.TryReadPascal(head, ref at, out var text);
                title = MacText.Decode(text, options);
            }
            return Json(input, (w, data) =>
            {
                w.WriteStartObject();
                w.WriteNumber("version", version);
                var reader = new BigEndianReader(data);
                var flags = reader.TryReadUInt32At(2, out var flagBits) ? flagBits : 0;
                Flags(w, flags, ["useThemeBackground", "useControlHierarchy", "movable", "useThemeControls"]);
                w.WriteBoolean("movable", (flags & 4) != 0);
                w.WriteNumber("refCon", reader.TryReadInt32At(6, out var refCon) ? refCon : 0);
                w.WriteBoolean("useThemeWindow", data.Length >= 11 && data[10] != 0);
                w.WriteString("title", title);
                w.WriteEndObject();
            });
        }

        // xmnu: version (not checked), count, then per item a key word: 1 is followed by 28 bytes of data, anything else by none.
        private static byte[] MenuExtension(DecodeInput input)
        {
            return Json(input, (w, data) =>
            {
                w.WriteStartObject();
                var reader = new BigEndianReader(data);
                w.WriteNumber("version", reader.TryReadInt16At(0, out var version) ? version : 0);
                var count = reader.TryReadInt16At(2, out var itemCount) ? itemCount : 0;
                if (data.Length < 4) Short(input);
                w.WriteStartArray("items");
                var at = 4;
                for (var i = 0; i < count; i++)
                {
                    if (at + 2 > data.Length)
                    {
                        Short(input);
                        break;
                    }
                    var key = reader.ReadInt16At(at);
                    at += 2;
                    w.WriteStartObject();
                    w.WriteNumber("number", i + 1);
                    w.WriteNumber("key", key);
                    if (key == 1)
                    {
                        if (at + 28 > data.Length)
                        {
                            Short(input);
                            w.WriteEndObject();
                            break;
                        }
                        var e = new BigEndianReader(data[at..]);
                        var commandId = e.ReadUInt32At(0);
                        w.WriteNumber("commandId", commandId);
                        w.WriteString("command", new FourCC(commandId).ToString());
                        Flags(w, e.ReadByteAt(4), ["shift", "option", "control", "noCommand"], "modifiers");
                        w.WriteNumber("iconType", e.ReadByteAt(5));
                        w.WriteNumber("textEncoding", e.ReadInt32At(10));
                        w.WriteNumber("refCon", e.ReadInt32At(14));
                        w.WriteNumber("refCon2", e.ReadInt32At(18));
                        w.WriteNumber("submenu", e.ReadUInt16At(22));
                        w.WriteNumber("font", e.ReadUInt16At(24));
                        w.WriteNumber("glyph", e.ReadInt16At(26));
                        at += 28;
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        private delegate void SpanWriter(Utf8JsonWriter w, ReadOnlySpan<byte> data);

        private static byte[] Json(DecodeInput input, SpanWriter write) => MacText.Json(w => write(w, input.Data.Span));

        private static void Flags(Utf8JsonWriter w, uint flags, string[] names, string name = "flags")
        {
            w.WriteNumber(name, flags);
            w.WriteStartArray(name + "Names");
            for (var bit = 0; bit < names.Length; bit++)
            {
                if ((flags & (1u << bit)) != 0) w.WriteStringValue(names[bit]);
            }
            w.WriteEndArray();
        }

        // An RGBColor: three u16, and the colour as #rrggbb (high bytes).
        private static void Rgb(Utf8JsonWriter w, ReadOnlySpan<byte> rgb)
        {
            if (rgb.Length < 6) return;
            var reader = new BigEndianReader(rgb);
            ushort r = reader.ReadUInt16(), g = reader.ReadUInt16(), b = reader.ReadUInt16();
            w.WriteNumber("red", r);
            w.WriteNumber("green", g);
            w.WriteNumber("blue", b);
            w.WriteString("hex", $"#{r >> 8:x2}{g >> 8:x2}{b >> 8:x2}");
        }

        private static void Short(DecodeInput input)
        {
            var message = $"{input.Resource}: the data ends early; read as far as it goes.";
            foreach (var d in input.Diagnostics)
            {
                if (d.Code == "ui.short" && d.Message == message) return;
            }
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "ui.short", message));
        }
    }
}
