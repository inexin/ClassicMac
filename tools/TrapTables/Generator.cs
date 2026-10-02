using System.Globalization;
using System.Text;

namespace TrapTables;

/// <summary>Reads Multiversal's defs/*.yaml and writes the three generated tables.</summary>
public static class TrapTablesGenerator
{
    /// <summary>Generates TrapNames.g.cs, SelectorNames.g.cs and LowMemoryGlobals.g.cs into <paramref name="outDir"/>.</summary>
    /// <returns>A one-line summary of the counts.</returns>
    public static string Generate(string multiversal, string outDir)
    {
        string defs = Path.Combine(multiversal, "defs");
        Directory.CreateDirectory(outDir);

        var items = new List<Item>();
        foreach (string file in Directory.GetFiles(defs, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            items.AddRange(Yaml.Read(file));

        // ---- Traps ----
        // word -> (name, modifier kind). The first definition of a word wins (files in name order, items in file order).
        var traps = new SortedDictionary<ushort, (string Name, string Kind)>();
        var dispatchers = new Dictionary<string, (ushort Trap, string Location)>(StringComparer.Ordinal);

        foreach (var d in items.Where(i => i.Kind == "dispatcher"))
        {
            ushort trap = ParseWord(d["trap"]);
            dispatchers[d["name"]] = (trap, d["selector-location"]);
            AddTrap(trap, d["name"], d.File);
        }
        foreach (var f in items.Where(i => i.Kind == "function" && i.Has("trap") && !i.Has("dispatcher")))
            AddTrap(ParseWord(f["trap"]), f["name"], f.File, f);
        // Calls selected by the trap word's own bits (Gestalt's $200 and $400): the word is the dispatcher's plus the selector.
        foreach (var f in items.Where(i => i.Kind == "function" && !i.Has("trap") && i.Has("dispatcher") && i.Has("selector")))
        {
            var (trap, location) = dispatchers[f["dispatcher"]];
            if (location == "TrapBits")
                AddTrap(checked((ushort)(trap | ParseLong(f["selector"]))), f["name"], f.File, f);
        }
        foreach (var (word, name, kind) in Supplements.Traps)
            traps[word] = (name, kind ?? (traps.TryGetValue(word, out var old) ? old.Kind : "None"));

        void AddTrap(ushort word, string name, string file, Item? f = null)
        {
            if (IsExecutorOwn(name) || traps.ContainsKey(word))
                return;
            traps[word] = (name, ModifierKind(word, file, f));
        }

        // Executor's own entry points and placeholders (_AE_hdlr_table_alloc, ROMlib_Fsetenv, FinaleUnknown1), not
        // Apple's routines.
        static bool IsExecutorOwn(string name) =>
            name.StartsWith('_') || name.StartsWith("ROMlib_", StringComparison.Ordinal)
            || name.Contains("Unknown", StringComparison.Ordinal);

        // Bits 9 and 10 of an OS trap mean different things to different managers.
        static string ModifierKind(ushort word, string file, Item? f)
        {
            if ((word & 0x0800) != 0)
                return "None";
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem == "MemoryMgr")
                return "Memory";
            if (f != null && (f.Has("file_trap") || f.Args.Any(a => a.GetValueOrDefault("register") == "TrapBit<0x400>")))
                return "File";
            return "None";
        }

        // ---- Selectors ----
        // trap -> selector -> name; the table belongs to the function's own trap word if it gives one, else its dispatcher's.
        var selectors = new SortedDictionary<ushort, SortedDictionary<uint, string>>();
        var conventions = new SortedDictionary<ushort, (string Name, string Location, string Width, uint Mask)>();
        foreach (var (name, (trap, location)) in dispatchers)
        {
            if (location == "TrapBits")
                continue;
            var c = Convention(location);
            conventions[trap] = (name, c.Location, c.Width, c.Mask);
        }
        foreach (var f in items.Where(i => i.Kind == "function" && i.Has("dispatcher") && i.Has("selector")))
        {
            var (dtrap, location) = dispatchers[f["dispatcher"]];
            if (location == "TrapBits" || IsExecutorOwn(f["name"]))
                continue;
            ushort trap = f.Has("trap") ? ParseWord(f["trap"]) : dtrap;
            if (!conventions.ContainsKey(trap))
            {
                var c = Convention(location);
                conventions[trap] = (traps.TryGetValue(trap, out var t) ? t.Name : f["dispatcher"], c.Location, c.Width, c.Mask);
            }
            var table = selectors.TryGetValue(trap, out var s) ? s : selectors[trap] = new();
            table.TryAdd(ParseLong(f["selector"]), f["name"]);
        }
        foreach (var (trap, name, location, width, mask) in Supplements.Conventions)
            conventions.TryAdd(trap, (name, location, width, mask));

        // Gestalt: D0 holds an OSType selector; its names are the gestalt* constants.
        const ushort GestaltTrap = 0xA1AD;
        var gestalt = new SortedDictionary<uint, string>();
        foreach (var e in items.Where(i => i.Kind == "enum" && Path.GetFileNameWithoutExtension(i.File) == "Gestalt"))
            foreach (var v in e.Values)
                if (v.Name.StartsWith("gestalt", StringComparison.Ordinal) && TryParseOSType(v.Value, out uint code))
                    gestalt.TryAdd(code, v.Name);
        conventions[GestaltTrap] = ("Gestalt", "D0", "OSType", 0xFFFFFFFF);
        selectors[GestaltTrap] = gestalt;

        // Multiversal's selector-location: StackW, StackL, D0W, D0L, D0<mask>, StackWLookahead<mask>, StackWMasked<mask>.
        static (string Location, string Width, uint Mask) Convention(string location)
        {
            if (location == "StackW") return ("Stack", "Word", 0xFFFF);
            if (location == "StackL") return ("Stack", "Long", 0xFFFFFFFF);
            if (location == "D0W") return ("D0", "Word", 0xFFFF);
            if (location == "D0L") return ("D0", "Long", 0xFFFFFFFF);
            int lt = location.IndexOf('<');
            if (lt > 0 && location.EndsWith('>'))
            {
                uint mask = ParseLong(location[(lt + 1)..^1]);
                string head = location[..lt];
                if (head == "D0") return ("D0", mask > 0xFFFF ? "Long" : "Word", mask);
                if (head is "StackWLookahead" or "StackWMasked") return ("Stack", "Word", mask);
            }
            throw new InvalidDataException("Unknown selector-location " + location);
        }

        // ---- Low-memory globals ----
        var lowmem = new SortedDictionary<uint, (string Name, int Size, string Type)>();
        // Names that start in lower case (nilhandle, lastlowglobal, ...) are Executor's own labels, not Apple's globals.
        foreach (var g in items.Where(i => i.Kind == "lowmem" && char.IsUpper(i["name"][0])))
        {
            uint address = ParseLong(g["address"]);
            lowmem.TryAdd(address, (g["name"], LowMemSize(g["type"]), g["type"]));
        }

        static int LowMemSize(string type)
        {
            int bracket = type.IndexOf('[');
            if (bracket > 0)
                return LowMemSize(type[..bracket]) * int.Parse(type[(bracket + 1)..^1], CultureInfo.InvariantCulture);
            if (type.EndsWith('*') || type.EndsWith("Ptr", StringComparison.Ordinal) || type.EndsWith("Handle", StringComparison.Ordinal)
                || type.EndsWith("UPP", StringComparison.Ordinal) || type.EndsWith("_ptr", StringComparison.Ordinal)
                || type is "WindowPeek" or "THz")
                return 4;
            return type switch
            {
                "Byte" or "Boolean" or "uint8_t" or "SignedByte" or "Char" => 1,
                "INTEGER" or "int16_t" or "uint16_t" => 2,
                "LONGINT" or "ULONGINT" or "int32_t" or "uint32_t" or "Point" or "OSType" or "Fixed" => 4,
                "RGBColor" => 6,
                "Rect" or "Pattern" => 8,
                "QHdr" => 10,             // qFlags.w, qHead.l, qTail.l [Doc: Inside Macintosh: Operating System Utilities]
                "FMInput" => 16,          // family, size, face, needBits, device, numer, denom [Doc: Inside Macintosh: Text, "Font Manager"]
                "FMOutput" => 26,         // errNum, fontHandle, 12 bytes of metrics, numer, denom [Doc: Inside Macintosh: Text, "Font Manager"]
                _ => throw new InvalidDataException("Unknown low-memory type " + type),
            };
        }

        // ---- Output ----
        const string Header = """
            // <auto-generated>
            // Generated by tools/TrapTables from Multiversal Interfaces (https://github.com/autc04/multiversal, MIT,
            // Copyright 2019 Wolfgang Thaller; see THIRD-PARTY-NOTICES.md), with the supplements in tools/TrapTables.
            // Do not edit; rerun the tool.
            // </auto-generated>

            namespace ClassicMac.Code.Disassembly;

            """;

        var sb = new StringBuilder(Header).AppendLine();
        sb.AppendLine("public static partial class TrapNames");
        sb.AppendLine("{");
        sb.AppendLine("    // Trap word as Multiversal defines it (its canonical modifier bits included) -> name and modifier kind.");
        sb.AppendLine("    private static readonly (ushort Word, string Name, TrapModifierKind Kind)[] Table =");
        sb.AppendLine("    [");
        foreach (var (word, (name, kind)) in traps)
            sb.AppendLine(CultureInfo.InvariantCulture, $"        (0x{word:X4}, \"{name}\", TrapModifierKind.{kind}),");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        Write("TrapNames.g.cs", sb);

        sb = new StringBuilder(Header).AppendLine();
        sb.AppendLine("public static partial class SelectorNames");
        sb.AppendLine("{");
        sb.AppendLine("    // Dispatcher trap -> name, where the selector is, its width and the mask applied before lookup.");
        sb.AppendLine("    private static readonly (ushort Trap, string Name, SelectorLocation Location, SelectorWidth Width, uint Mask)[] Dispatchers =");
        sb.AppendLine("    [");
        foreach (var (trap, (name, location, width, mask)) in conventions)
            sb.AppendLine(CultureInfo.InvariantCulture, $"        (0x{trap:X4}, \"{name}\", SelectorLocation.{location}, SelectorWidth.{width}, 0x{mask:X}),");
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    // Dispatcher trap -> selector -> routine name.");
        sb.AppendLine("    private static readonly (ushort Trap, uint Selector, string Name)[] Table =");
        sb.AppendLine("    [");
        foreach (var (trap, table) in selectors)
            foreach (var (selector, name) in table)
                sb.AppendLine(CultureInfo.InvariantCulture, $"        (0x{trap:X4}, 0x{selector:X}, \"{name}\"),");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        Write("SelectorNames.g.cs", sb);

        sb = new StringBuilder(Header).AppendLine();
        sb.AppendLine("public static partial class LowMemoryGlobals");
        sb.AppendLine("{");
        sb.AppendLine("    // Address -> name, size in bytes and Multiversal's type.");
        sb.AppendLine("    private static readonly (uint Address, string Name, int Size, string Type)[] Table =");
        sb.AppendLine("    [");
        foreach (var (address, (name, size, type)) in lowmem)
            sb.AppendLine(CultureInfo.InvariantCulture, $"        (0x{address:X4}, \"{name}\", {size}, \"{type}\"),");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        Write("LowMemoryGlobals.g.cs", sb);

        return ($"{traps.Count} traps, {conventions.Count} dispatchers, {selectors.Values.Sum(s => s.Count)} selectors, {lowmem.Count} low-memory globals");

        void Write(string name, StringBuilder text) =>
            File.WriteAllText(Path.Combine(outDir, name), text.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

        static ushort ParseWord(string s) => checked((ushort)ParseLong(s));

        static uint ParseLong(string s)
        {
            s = s.Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : uint.Parse(s, CultureInfo.InvariantCulture);
        }

        static bool TryParseOSType(string s, out uint code)
        {
            code = 0;
            s = s.Trim().Trim('"');
            if (s.Length != 6 || s[0] != '\'' || s[5] != '\'')
                return false;
            for (int i = 1; i <= 4; i++)
            {
                if (s[i] > 0xFF)
                    return false;
                code = (code << 8) | s[i];
            }
            return true;
        }
    }
}
