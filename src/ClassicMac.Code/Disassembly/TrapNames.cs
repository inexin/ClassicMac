using System.Collections.Generic;
using System.Text;

namespace ClassicMac.Code.Disassembly;

/// <summary>What an A-line trap word names: the trap and the flags its modifier bits carry.</summary>
/// <param name="Word">The trap word as it appears in the code.</param>
/// <param name="Name">The trap's name without the underscore (<c>NewPtr</c>), or null when the word is not a known
/// trap.</param>
/// <param name="IsToolbox">True for a Toolbox trap (bit 11 set, $A800–$AFFF), false for an OS trap.</param>
/// <param name="AutoPop">A Toolbox trap with bit 10 set: it returns to the caller's caller.</param>
/// <param name="Modifiers">The OS trap's modifier names for bits 9 and 10 not already part of the named trap
/// (<c>SYS</c>, <c>CLEAR</c>, <c>ASYNC</c>, <c>HFS</c> ...), or <c>$200</c>/<c>$400</c> where the trap gives them
/// none.</param>
/// <param name="DontPreserveA0">An OS trap with bit 8 set: the dispatcher does not restore A0, which holds the
/// result.</param>
public readonly record struct TrapInfo(ushort Word, string? Name, bool IsToolbox, bool AutoPop,
    IReadOnlyList<string> Modifiers, bool DontPreserveA0);

/// <summary>
/// Names of A-line traps ($Axxx) [Doc: Inside Macintosh: Operating System Utilities, "Trap Manager"]. A Toolbox trap
/// (bit 11 set) is numbered by bits 0–9, bit 10 being auto-pop; an OS trap (bit 11 clear) by bits 0–7, bit 8
/// meaning "don't preserve A0" and bits 9–10 being flags whose meaning depends on the manager (Memory Manager $400
/// SYS and $200 CLEAR, File Manager $400 ASYNC and $200 HFS). The names come from Multiversal Interfaces with
/// Apple's trap-macro names supplemented (tools/TrapTables).
/// </summary>
public static partial class TrapNames
{
    // In a nested class so that Table (in the generated part) is initialized first.
    private static class Index
    {
        public static readonly Dictionary<ushort, (string Name, TrapModifierKind Kind)> Exact = BuildExact();
        public static readonly Dictionary<ushort, List<(ushort Word, string Name, TrapModifierKind Kind)>> OsByKey =
            BuildOsByKey();
    }

    /// <summary>True when <paramref name="word"/> is an A-line trap word ($A000–$AFFF).</summary>
    public static bool IsTrap(ushort word) => (word & 0xF000) == 0xA000;

    /// <summary>
    /// Looks up a trap word: by the exact word first (a table entry may carry modifier bits, as $A11E _NewPtr or
    /// $A260 _HFSDispatch do), then by the trap number with the modifier bits removed.
    /// </summary>
    /// <param name="word">The trap word.</param>
    /// <returns>The trap's name (null if unknown) and flags.</returns>
    public static TrapInfo Lookup(ushort word)
    {
        if (!IsTrap(word))
            return new TrapInfo(word, null, false, false, [], false);
        if ((word & 0x0800) != 0)
        {
            bool autoPop = (word & 0x0400) != 0;
            string? name = Index.Exact.TryGetValue(word, out var e)
                || Index.Exact.TryGetValue((ushort)(word & 0xFBFF), out e) ? e.Name : null;
            return new TrapInfo(word, name, true, autoPop, [], false);
        }

        bool noA0 = (word & 0x0100) != 0;
        if (Index.Exact.TryGetValue(word, out var exact))
            return new TrapInfo(word, exact.Name, false, false, [], noA0);
        if (!Index.OsByKey.TryGetValue((ushort)(word & 0xF8FF), out var candidates))
            return new TrapInfo(word, null, false, false, [], noA0);

        // The most specific entry whose own modifier bits are all present in the word; else the plainest.
        int bits = word & 0x0700;
        (ushort Word, string Name, TrapModifierKind Kind)? best = null;
        foreach (var c in candidates)
        {
            int cb = c.Word & 0x0700;
            if ((cb & ~bits) == 0 && (best is null || PopCount(cb) > PopCount(best.Value.Word & 0x0700)))
                best = c;
        }
        if (best is null)
            foreach (var c in candidates)
                if (best is null || PopCount(c.Word & 0x0700) < PopCount(best.Value.Word & 0x0700))
                    best = c;

        var b = best!.Value;
        int extra = bits & ~(b.Word & 0x0700) & 0x0600;
        return new TrapInfo(word, b.Name, false, false, ModifierNames(b.Kind, extra), noA0);
    }

    /// <summary>
    /// The trap as an assembler writes it: <c>_NewPtr ,SYS,CLEAR</c>, <c>_GetResource ,AUTOPOP</c>, or the raw
    /// word (<c>_A123</c>) when the trap is unknown.
    /// </summary>
    /// <param name="word">The trap word.</param>
    /// <returns>The trap macro text.</returns>
    public static string Describe(ushort word)
    {
        var info = Lookup(word);
        if (info.Name is null)
            return "_" + word.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder("_").Append(info.Name);
        string sep = " ,";
        if (info.AutoPop)
            sb.Append(sep).Append("AUTOPOP");
        foreach (string m in info.Modifiers)
        {
            sb.Append(sep).Append(m);
            sep = ",";
        }
        return sb.ToString();
    }

    /// <summary>The trap's name without the underscore or modifiers.</summary>
    /// <param name="word">The trap word.</param>
    /// <param name="name">The name (<c>NewPtr</c>).</param>
    /// <returns>True when the trap is known.</returns>
    public static bool TryGetName(ushort word, out string name)
    {
        name = Lookup(word).Name ?? "";
        return name.Length > 0;
    }

    private static List<string> ModifierNames(TrapModifierKind kind, int bits)
    {
        var names = new List<string>(2);
        (string High, string Low) = kind switch
        {
            TrapModifierKind.Memory => ("SYS", "CLEAR"),
            TrapModifierKind.File => ("ASYNC", "HFS"),
            TrapModifierKind.Device => ("ASYNC", "IMMED"),
            TrapModifierKind.String => ("CASE", "MARKS"),
            _ => ("$400", "$200"),
        };
        // Inside Macintosh writes _CmpString ,MARKS,CASE and _NewPtr ,SYS,CLEAR.
        if (kind == TrapModifierKind.String)
        {
            if ((bits & 0x200) != 0) names.Add(Low);
            if ((bits & 0x400) != 0) names.Add(High);
        }
        else
        {
            if ((bits & 0x400) != 0) names.Add(High);
            if ((bits & 0x200) != 0) names.Add(Low);
        }
        return names;
    }

    private static int PopCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    private static Dictionary<ushort, (string, TrapModifierKind)> BuildExact()
    {
        var d = new Dictionary<ushort, (string, TrapModifierKind)>(Table.Length);
        foreach (var (word, name, kind) in Table)
            d[word] = (name, kind);
        return d;
    }

    private static Dictionary<ushort, List<(ushort, string, TrapModifierKind)>> BuildOsByKey()
    {
        var d = new Dictionary<ushort, List<(ushort, string, TrapModifierKind)>>();
        foreach (var entry in Table)
        {
            if ((entry.Word & 0x0800) != 0)
                continue;
            ushort key = (ushort)(entry.Word & 0xF8FF);
            if (!d.TryGetValue(key, out var list))
                d[key] = list = [];
            list.Add(entry);
        }
        return d;
    }
}

/// <summary>What bits 9 and 10 of an OS trap mean for the manager that owns it.</summary>
internal enum TrapModifierKind
{
    /// <summary>No named meaning; printed as <c>$200</c> and <c>$400</c>.</summary>
    None,
    /// <summary>Memory Manager: $400 SYS (system heap), $200 CLEAR (zero the block).</summary>
    Memory,
    /// <summary>File Manager: $400 ASYNC, $200 HFS.</summary>
    File,
    /// <summary>Device Manager: $400 ASYNC, $200 IMMED.</summary>
    Device,
    /// <summary>String comparison: $200 MARKS (diacriticals ignored), $400 CASE (case sensitive).</summary>
    String,
}
