namespace ClassicMac.Resources.Decoders.Finder;

/// <summary>How a Finder window shows its items.</summary>
public enum FinderViewKind
{
    /// <summary>By large icon (32 × 32).</summary>
    LargeIcon,

    /// <summary>By small icon.</summary>
    SmallIcon,

    /// <summary>By large button.</summary>
    Button,

    /// <summary>By small button.</summary>
    SmallButton,

    /// <summary>As a list.</summary>
    List,
}

/// <summary>
/// A folder window's view as Finder 9 reads it from <c>frView</c>, <c>frScript</c> and <c>frOpenChain</c>
/// (docs/formats/file-systems/finder-windows.md §2.6).
/// </summary>
/// <param name="Kind">The view.</param>
/// <param name="ListStyle">A list view's style; 0 for the others.</param>
/// <param name="ArrangeKey">How icons are arranged: <c>frView</c>'s low 3 bits (0 name, 4 kind, 5 label, 6 position, 7 none).</param>
public sealed record FinderView(FinderViewKind Kind, int ListStyle, int ArrangeKey)
{
    /// <summary>Large icons, not arranged by a key.</summary>
    public static FinderView LargeIcons { get; } = new(FinderViewKind.LargeIcon, 0, 0);

    /// <summary>The view's name as the View menu words it: "icon view", "list view", ….</summary>
    public string Name => Kind switch
    {
        FinderViewKind.SmallIcon => "small icon view",
        FinderViewKind.Button => "button view",
        FinderViewKind.SmallButton => "small button view",
        FinderViewKind.List => "list view",
        _ => "icon view",
    };

    /// <summary>
    /// The view [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder]: <c>(frView &gt;&gt; 8) &amp; 15</c> of 2 is a list whose
    /// style is <c>frOpenChain</c> bits 18–21, 3–8 a list of that style, above 8 a list of style 2; 0 and 1 are icons,
    /// buttons when <c>frScript</c> has $20 and its bit 7 clear. With <c>frScript</c> $40 (the folder's own options)
    /// icons are small when <c>frView</c>'s low byte has $40, buttons when <c>frScript</c> has $08; otherwise the size
    /// is the Finder Preferences' standard view, large by default (not read here).
    /// </summary>
    public static FinderView Read(short view, sbyte script, int openChain)
    {
        int v = (view >> 8) & 0xF, arrange = view & 7;
        if (v == 2)
        {
            return new(FinderViewKind.List, (openChain >> 18) & 0xF, arrange);
        }

        if (v > 8)
        {
            return new(FinderViewKind.List, 2, arrange);
        }

        if (v >= 3)
        {
            return new(FinderViewKind.List, v, arrange);
        }

        bool buttons = script >= 0 && (script & 0x20) != 0, own = (script & 0x40) != 0;
        var kind = buttons
            ? own && (script & 0x08) != 0 ? FinderViewKind.SmallButton : FinderViewKind.Button
            : own && (view & 0x40) != 0 ? FinderViewKind.SmallIcon : FinderViewKind.LargeIcon;
        return new(kind, 0, arrange);
    }
}
