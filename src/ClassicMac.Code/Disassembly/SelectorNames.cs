using System.Collections.Generic;
using static ClassicMac.Code.Disassembly.SelectorTable;

namespace ClassicMac.Code.Disassembly;

/// <summary>Where a dispatcher trap finds its routine selector.</summary>
public enum SelectorLocation
{
    /// <summary>Pushed just before the trap (<c>MOVE.W #sel,-(SP)</c> or <c>MOVE.L #sel,-(SP)</c>).</summary>
    Stack,
    /// <summary>In D0 (<c>MOVEQ #sel,D0</c>, <c>MOVE.W #sel,D0</c> or <c>MOVE.L #sel,D0</c>).</summary>
    D0,
}

/// <summary>The size of a dispatcher's selector.</summary>
public enum SelectorWidth
{
    /// <summary>A word.</summary>
    Word,
    /// <summary>A long; for _ScriptUtil and _PrGlue the high word encodes the parameter sizes.</summary>
    Long,
    /// <summary>A long that is a four-character code (_Gestalt's selector).</summary>
    OSType,
}

/// <summary>How a dispatcher trap is given its selector.</summary>
/// <param name="Trap">The dispatcher's trap word.</param>
/// <param name="Name">The dispatcher's name (<c>Pack7</c>, <c>HFSDispatch</c>).</param>
/// <param name="Location">Where the selector is.</param>
/// <param name="Width">Its size.</param>
/// <param name="Mask">The bits of the value that select the routine (the rest are parameter sizes or flags).</param>
public readonly record struct SelectorConvention(ushort Trap, string Name, SelectorLocation Location,
    SelectorWidth Width, uint Mask);

/// <summary>
/// Routine names behind the selector dispatchers (_Pack0–_Pack15, _OSDispatch, _ScriptUtil, _HFSDispatch,
/// _Gestalt ...), from Multiversal Interfaces (tools/TrapTables). Where the selector sits, and its width, are per
/// dispatcher [Doc: each manager's chapter of Inside Macintosh; checked against the inline code of 68k applications].
/// </summary>
public static class SelectorNames
{
    // In a nested class so that the generated tables are initialized first.
    private static class Index
    {
        public static readonly Dictionary<ushort, SelectorConvention> Conventions = BuildConventions();
        public static readonly Dictionary<ushort, Dictionary<uint, string>> Names = BuildNames();
    }

    /// <summary>The selector convention of a dispatcher trap.</summary>
    /// <param name="trap">The trap word; modifier bits not part of the dispatcher's own word are ignored.</param>
    /// <param name="convention">Where its selector is and how wide it is.</param>
    /// <returns>True when <paramref name="trap"/> is a known dispatcher.</returns>
    public static bool TryGetConvention(ushort trap, out SelectorConvention convention)
    {
        foreach (ushort t in Candidates(trap))
        {
            if (Index.Conventions.TryGetValue(t, out convention))
            {
                return true;
            }
        }

        convention = default;
        return false;
    }

    /// <summary>
    /// The routine a dispatcher trap calls with a selector: <c>TryGet(0xA9EE, 0, out n)</c> gives
    /// <c>NumToString</c>. The selector is masked by the dispatcher's <see cref="SelectorConvention.Mask"/>; for a
    /// long selector given without its high word, a routine whose low word matches uniquely is found.
    /// </summary>
    /// <param name="trap">The dispatcher's trap word.</param>
    /// <param name="selector">The selector value (sign-extended MOVEQ values are fine).</param>
    /// <param name="name">The routine's name.</param>
    /// <returns>True when the selector is known.</returns>
    public static bool TryGet(ushort trap, uint selector, out string name)
    {
        name = "";
        if (!TryGetConvention(trap, out var convention) || !Index.Names.TryGetValue(convention.Trap, out var table))
        {
            return false;
        }

        uint value = selector & convention.Mask;
        if (table.TryGetValue(value, out var found))
        {
            name = found;
            return true;
        }
        if (convention.Width == SelectorWidth.Long && value <= 0xFFFF)
        {
            string? unique = null;
            foreach (var (s, n) in table)
            {
                if ((s & 0xFFFF) != value)
                {
                    continue;
                }

                if (unique != null)
                {
                    return false;
                }

                unique = n;
            }
            if (unique != null)
            {
                name = unique;
                return true;
            }
        }
        return false;
    }

    // The exact word, then without bit 10 (auto-pop on a Toolbox trap, ASYNC on an OS trap), then the bare number.
    // An OS trap's bits are stripped only where they are modifiers of the same trap: _NewGestalt ($A3AD) and
    // _ReplaceGestalt ($A5AD) are calls of their own, not _Gestalt ($A1AD) with modifiers.
    private static IEnumerable<ushort> Candidates(ushort trap)
    {
        yield return trap;
        if ((trap & 0x0800) != 0)
        {
            yield return (ushort)(trap & 0xFBFF);
            yield break;
        }
        string? name = TrapNames.Lookup(trap).Name;
        foreach (ushort t in new[] { (ushort)(trap & 0xFBFF), (ushort)(trap & 0xF8FF) })
        {
            if (t != trap && TrapNames.Lookup(t).Name == name)
            {
                yield return t;
            }
        }
    }

    private static Dictionary<ushort, SelectorConvention> BuildConventions()
    {
        var d = new Dictionary<ushort, SelectorConvention>(Dispatchers.Length);
        foreach (var (trap, name, location, width, mask) in Dispatchers)
        {
            d[trap] = new SelectorConvention(trap, name, location, width, mask);
        }

        return d;
    }

    private static Dictionary<ushort, Dictionary<uint, string>> BuildNames()
    {
        var d = new Dictionary<ushort, Dictionary<uint, string>>();
        foreach (var (trap, selector, name) in Table)
        {
            if (!d.TryGetValue(trap, out var table))
            {
                d[trap] = table = [];
            }

            table[selector] = name;
        }
        return d;
    }
}
