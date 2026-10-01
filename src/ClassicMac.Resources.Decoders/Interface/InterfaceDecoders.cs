using System;
using System.Collections.Generic;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Interface
{
    /// <summary>
    /// The Toolbox's interface resources as JSON: menus (<c>MENU</c>, <c>MBAR</c>), window, dialog and alert templates
    /// (<c>WIND</c>, <c>DLOG</c>, <c>ALRT</c>), dialog item lists (<c>DITL</c>) and control templates (<c>CNTL</c>). Every
    /// stored field is kept, with names for the known codes beside them.
    /// </summary>
    internal sealed class InterfaceDecoder(DecodeOptions options, string name, string type, Func<InterfaceDecoder, DecodeInput, byte[]> write)
        : IResourceDecoder, IBuiltInDecoder
    {
        private readonly FourCC handled = FourCC.FromString(type);

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC t) => t == handled;

        public IReadOnlyCollection<FourCC> Types => [handled];

        internal DecodeOptions Options => options;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input) => [new DecodedFile(".json", write(this, input), MacText.EncodingName(options.TextEncoding))];

        public static IEnumerable<IResourceDecoder> All(DecodeOptions options) =>
        [
            new InterfaceDecoder(options, "ui.menu", "MENU", (d, i) => Menu(InterfaceResources.ReadMenu(i.Data, d.Options, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.menu-bar", "MBAR", (d, i) => MenuBar(InterfaceResources.ReadMenuBar(i.Data, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.window", "WIND", (d, i) => Window(InterfaceResources.ReadWindow(i.Data, false, d.Options, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.dialog", "DLOG", (d, i) => Window(InterfaceResources.ReadWindow(i.Data, true, d.Options, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.alert", "ALRT", (d, i) => Alert(InterfaceResources.ReadAlert(i.Data, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.dialog-items", "DITL", (d, i) => Items(InterfaceResources.ReadDialogItems(i.Data, d.Options, i.Diagnostics, i.Resource.ToString()))),
            new InterfaceDecoder(options, "ui.control", "CNTL", (d, i) => Control(InterfaceResources.ReadControl(i.Data, d.Options, i.Diagnostics, i.Resource.ToString()))),
        ];

        private static byte[] Menu(MenuResource menu) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", menu.Id);
            w.WriteString("title", InterfaceNames.Chicago(menu.Title));
            w.WriteBoolean("enabled", menu.Enabled);
            w.WriteNumber("definition", menu.Definition);
            w.WriteNumber("width", menu.Width);
            w.WriteNumber("height", menu.Height);
            w.WriteNumber("enableFlags", menu.EnableFlags);
            w.WriteStartArray("items");
            foreach (var item in menu.Items)
            {
                w.WriteStartObject();
                w.WriteString("text", item.Text);
                w.WriteBoolean("enabled", item.Enabled);
                if (item.IsDivider) w.WriteBoolean("divider", true);
                w.WriteNumber("icon", item.Icon);
                w.WriteNumber("keyEquivalent", item.KeyEquivalent);
                if (item.KeyKind is { } keyKind) w.WriteString("keyKind", keyKind);
                else if (item.KeyEquivalent is > 0x20 and < 0x7F) w.WriteString("key", ((char)item.KeyEquivalent).ToString());
                w.WriteNumber("mark", item.Mark);
                if (item.Submenu is { } submenu) w.WriteNumber("submenu", submenu);
                else if (item.Mark != 0) w.WriteString("markCharacter", InterfaceNames.Chicago(MacRoman.Decode([item.Mark])));
                w.WriteNumber("face", item.Face);
                Face(w, item.Face);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        private static byte[] MenuBar(IReadOnlyList<short> ids) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("menus");
            foreach (var id in ids) w.WriteNumberValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        });

        private static byte[] Window(WindowTemplate window) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("title", window.Title);
            Rect(w, "bounds", window.Bounds);
            w.WriteNumber("definition", window.Definition);
            if (InterfaceNames.Window(window.Definition) is { } kind) w.WriteString("definitionName", kind);
            w.WriteBoolean("visible", window.Visible);
            w.WriteBoolean("goAway", window.GoAway);
            w.WriteNumber("refCon", window.RefCon);
            if (window.ItemsId is { } items) w.WriteNumber("items", items);
            Position(w, window.Position);
            w.WriteEndObject();
        });

        private static byte[] Alert(AlertTemplate alert) => MacText.Json(w =>
        {
            w.WriteStartObject();
            Rect(w, "bounds", alert.Bounds);
            w.WriteNumber("items", alert.ItemsId);
            w.WriteNumber("stagesWord", alert.Stages);
            w.WriteStartArray("stages");
            for (var n = 1; n <= 4; n++)
            {
                var (bold, drawn, sound) = alert.Stage(n);
                w.WriteStartObject();
                w.WriteNumber("stage", n);
                w.WriteNumber("defaultItem", bold);
                w.WriteBoolean("drawn", drawn);
                w.WriteNumber("sound", sound);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Position(w, alert.Position);
            w.WriteEndObject();
        });

        private static byte[] Items(IReadOnlyList<DialogItem> items) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                w.WriteStartObject();
                w.WriteNumber("number", i + 1);
                w.WriteString("type", InterfaceNames.Item(item.Type));
                w.WriteNumber("typeCode", item.Type);
                w.WriteBoolean("enabled", item.Enabled);
                Rect(w, "bounds", item.Bounds);
                if (item.Text is { } text) w.WriteString("text", text);
                else if (item.Type == 1 && item.Data.Length >= 4)
                {
                    var help = item.Data.Span;
                    var helpKind = (help[0] << 8) | help[1];
                    w.WriteNumber("helpKind", helpKind);
                    if (helpKind switch { 1 => "hdlg", 2 => "hrct", 8 => "appendHdlg", _ => null } is { } helpName) w.WriteString("helpKindName", helpName);
                    w.WriteNumber("resourceId", item.ResourceId ?? 0);
                    if (help.Length >= 6) w.WriteNumber("offset", (short)((help[4] << 8) | help[5]));
                }
                else if (item.ResourceId is { } id) w.WriteNumber("resourceId", id);
                else if (item.Data.Length > 0) w.WriteString("data", Convert.ToHexString(item.Data.Span));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        private static byte[] Control(ControlTemplate control) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("title", control.Title);
            Rect(w, "bounds", control.Bounds);
            w.WriteNumber("definition", control.Definition);
            if (InterfaceNames.Control(control.Definition) is { } kind) w.WriteString("definitionName", kind);
            w.WriteNumber("value", control.Value);
            w.WriteNumber("minimum", control.Minimum);
            w.WriteNumber("maximum", control.Maximum);
            w.WriteBoolean("visible", control.Visible);
            w.WriteNumber("refCon", control.RefCon);
            if (control.Definition is >= 1008 and <= 1023)
            {
                // A pop-up menu gives the fields other meanings.
                w.WriteStartObject("popup");
                w.WriteNumber("menu", control.Minimum);
                w.WriteNumber("titleWidth", control.Maximum);
                w.WriteNumber("titleJustification", (sbyte)(control.Value & 0xFF));
                w.WriteBoolean("titleNoStyle", (control.Value & 0x8000) != 0);
                Face(w, (byte)((control.Value >> 8) & 0x7F), "titleStyle");
                if ((control.Definition & 4) != 0) w.WriteString("addResMenu", FourCCText(control.RefCon));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });

        private static void Rect(Utf8JsonWriter w, string name, MacRect rect)
        {
            w.WriteStartObject(name);
            w.WriteNumber("top", rect.Top);
            w.WriteNumber("left", rect.Left);
            w.WriteNumber("bottom", rect.Bottom);
            w.WriteNumber("right", rect.Right);
            w.WriteEndObject();
        }

        // The positioning word: used only when its low 11 bits are $00A, then read as fields.
        private static void Position(Utf8JsonWriter w, ushort? position)
        {
            if (position is not { } code)
            {
                w.WriteNull("position");
                return;
            }
            w.WriteStartObject("position");
            w.WriteNumber("code", code);
            if (InterfaceNames.Position(code) is { } name) w.WriteString("name", name);
            var used = (code & 0x7FF) == 0x00A;
            w.WriteBoolean("used", used);
            if (used)
            {
                w.WriteString("screen", (code >> 14) switch { 0 => "main", 1 => "parentWindowScreen", 2 => "parentWindow", _ => "parent" });
                w.WriteBoolean("centerHorizontally", ((code >> 13) & 1) != 0);
                w.WriteString("vertical", ((code >> 11) & 3) switch { 0 => "none", 1 => "center", 2 => "alertPosition", _ => "stagger" });
            }
            w.WriteEndObject();
        }

        private static string FourCCText(int value) => new FourCC((uint)value).ToString();

        private static void Face(Utf8JsonWriter w, byte face, string name = "style")
        {
            w.WriteStartArray(name);
            string[] names = ["bold", "italic", "underline", "outline", "shadow", "condense", "extend"];
            for (var bit = 0; bit < names.Length; bit++)
            {
                if ((face & (1 << bit)) != 0) w.WriteStringValue(names[bit]);
            }
            w.WriteEndArray();
        }
    }

    /// <summary>Names for the Toolbox's codes, as the Rez headers and <i>Inside Macintosh</i> give them.</summary>
    public static class InterfaceNames
    {
        /// <summary>A dialog item type's name.</summary>
        public static string Item(int type) => type switch
        {
            0 => "user",
            1 => "help",
            4 => "button",
            5 => "checkBox",
            6 => "radioButton",
            7 => "control",
            8 => "staticText",
            16 => "editText",
            32 => "icon",
            64 => "picture",
            _ => "unknown",
        };

        /// <summary>A positioning word's name (the Rez <c>Types.r</c> constants), or null.</summary>
        public static string? Position(ushort code) => code switch
        {
            0x0000 => "noAutoCenter",
            0x280A => "centerMainScreen",
            0x300A => "alertPositionMainScreen",
            0x380A => "staggerMainScreen",
            0xA80A => "centerParentWindow",
            0xB00A => "alertPositionParentWindow",
            0xB80A => "staggerParentWindow",
            0x680A => "centerParentWindowScreen",
            0x700A => "alertPositionParentWindowScreen",
            0x780A => "staggerParentWindowScreen",
            _ => null,
        };

        /// <summary>A standard window definition ID's name, or null.</summary>
        public static string? Window(short definition) => definition switch
        {
            0 => "documentProc",
            1 => "dBoxProc",
            2 => "plainDBox",
            3 => "altDBoxProc",
            4 => "noGrowDocProc",
            5 => "movableDBoxProc",
            8 => "zoomDocProc",
            12 => "zoomNoGrow",
            >= 16 and <= 23 => "rDocProc",
            _ => null,
        };

        /// <summary>
        /// A standard control definition ID's name, or null: the button, check box and radio button (+8 uses the window's
        /// font), the scroll bar, and the pop-up menu (variations +1 fixed width, +4 AddResMenu, +8 the window's font).
        /// </summary>
        public static string? Control(short definition)
        {
            if (definition == 16) return "scrollBarProc";
            if (definition is >= 1008 and <= 1023)
            {
                var variation = definition - 1008;
                return "popupMenuProc" + ((variation & 1) != 0 ? "+popupFixedWidth" : "") + ((variation & 4) != 0 ? "+popupUseAddResMenu" : "")
                    + ((variation & 8) != 0 ? "+popupUseWFont" : "");
            }
            var name = (definition & ~8) switch
            {
                0 => "pushButProc",
                1 => "checkBoxProc",
                2 => "radioButProc",
                _ => null,
            };
            return name is not null && (definition & 8) != 0 ? name + "+useWFont" : name;
        }

        /// <summary>
        /// Chicago's special characters as Unicode: $11 command key ⌘, $12 check mark ✓, $13 diamond ◆, $14 apple
        /// (the Menu Manager's mark constants, <i>Inside Macintosh: Macintosh Toolbox Essentials</i>); other text unchanged.
        /// </summary>
        public static string Chicago(string text) => text
            .Replace('\u0011', '⌘').Replace('\u0012', '✓').Replace('\u0013', '◆').Replace("\u0014", "", StringComparison.Ordinal);
    }
}
