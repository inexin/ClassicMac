using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers;

/// <summary>
/// The type and creator File Exchange 3.0.2 (Mac OS 9.0) shows for a file on a DOS disk whose Finder info is the <c>TEXT</c>/<c>dosa</c>
/// placeholder: Internet Config's map of name endings (<c>.doc</c> → <c>WDBN</c>/<c>MSWD</c>), matched against the
/// long name without regard to case, longest ending first, the earlier entry on a tie. Other types are shown as
/// stored. File Exchange writes the mapped type into <c>FINDER.DAT</c> only when the file's data fork is closed or
/// flushed, so files that were only browsed keep the placeholder on disk (disassembly, confirmed in SheepShaver).
/// Readers apply a map only when the app supplies one (<see cref="ContainerReadOptions.ExtensionMap"/>); none ships
/// with ClassicMac.
/// </summary>
public sealed class ExtensionMap
{
    private readonly List<(string Suffix, FourCC Type, FourCC Creator)> suffixes;

    /// <summary>A map of name endings (<c>.doc</c>) to type and creator, in Internet Config's order.</summary>
    public ExtensionMap(IEnumerable<KeyValuePair<string, (FourCC Type, FourCC Creator)>> suffixes)
    {
        ArgumentNullException.ThrowIfNull(suffixes);
        // A stable sort keeps the earlier entry first among endings of the same length.
        this.suffixes = suffixes.Select(p => (p.Key, p.Value.Type, p.Value.Creator))
            .OrderByDescending(p => p.Key.Length).ToList();
    }

    /// <summary>The Finder info shown for a file whose long name (or 8.3 name, when it has none) is <paramref name="name"/>.</summary>
    public FinderInfo Apply(FinderInfo stored, string name)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(name);
        if (stored.Type != PcExchange.Placeholder.Type || stored.Creator != PcExchange.Placeholder.Creator)
        {
            return stored;
        }

        foreach (var (suffix, type, creator) in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stored with { Type = type, Creator = creator };
            }
        }
        return stored;
    }
}
