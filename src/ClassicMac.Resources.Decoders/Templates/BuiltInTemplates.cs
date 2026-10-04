using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Templates;

/// <summary>A template ClassicMac ships: the template and where its layout comes from.</summary>
/// <param name="Template">The template.</param>
/// <param name="Source">The documentation its layout follows.</param>
public sealed record BuiltInTemplate(ResourceTemplate Template, string Source);

/// <summary>
/// ClassicMac's own templates for common types that have no form of their own (templates.md §5), written in its
/// own words from Apple's documentation of each layout; a <c>TMPL</c> in an open file wins over them.
/// </summary>
public static class BuiltInTemplates
{
    private static readonly (string Label, string Type)[] ColorTable =
    [
        ("Seed", "DLNG"), ("Flags", "HWRD"), ("Entries", "ZCNT"), ("*****", "LSTC"),
        ("Value", "DWRD"), ("Red", "HWRD"), ("Green", "HWRD"), ("Blue", "HWRD"), ("*****", "LSTE"),
    ];

    private static readonly Dictionary<FourCC, BuiltInTemplate> Templates = new()
    {
        [FourCC.FromString("MBAR")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Menu Bar Resource",
            ("Menus", "OCNT"), ("*****", "LSTC"), ("Menu ID", "DWRD"), ("*****", "LSTE")),
        [FourCC.FromString("BNDL")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Bundle Resource",
            ("Owner signature", "TNAM"), ("Owner ID", "DWRD"), ("Types", "ZCNT"), ("*****", "LSTC"),
            ("Type", "TNAM"), ("IDs", "ZCNT"), ("*****", "LSTC"), ("Local ID", "DWRD"), ("Resource ID", "DWRD"), ("*****", "LSTE"),
            ("*****", "LSTE")),
        [FourCC.FromString("FREF")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The File Reference Resource",
            ("File type", "TNAM"), ("Local icon ID", "DWRD"), ("File name (unused)", "PSTR")),
        [FourCC.FromString("SIZE")] = Make("Inside Macintosh: Processes, The Size Resource",
            ("Reserved (bit 15)", "BBIT"), ("Accepts suspend and resume events", "BBIT"), ("Reserved (bit 13)", "BBIT"),
            ("Can run in the background", "BBIT"), ("Activates on a switch to the front", "BBIT"), ("Runs only in the background", "BBIT"),
            ("Gets clicks that bring it to the front", "BBIT"), ("Accepts application-died events", "BBIT"), ("32-bit compatible", "BBIT"),
            ("High-level event aware", "BBIT"), ("Local and remote high-level events", "BBIT"), ("Stationery aware", "BBIT"),
            ("Uses inline input (TextEdit services)", "BBIT"), ("Reserved (bit 2)", "BBIT"), ("Reserved (bit 1)", "BBIT"),
            ("Reserved (bit 0)", "BBIT"), ("Preferred memory size", "DLNG"), ("Minimum memory size", "DLNG")),
        [FourCC.FromString("TMPL")] = Make("ResEdit Reference, Templates",
            ("Fields", "LSTB"), ("Label", "PSTR"), ("Type", "TNAM"), ("*****", "LSTE")),
        [FourCC.FromString("CURS")] = Make("Inside Macintosh: Imaging With QuickDraw, The Cursor Resource",
            ("Image", "H020"), ("Mask", "H020"), ("Hot spot vertical", "DWRD"), ("Hot spot horizontal", "DWRD")),
        [FourCC.FromString("PAT ")] = Make("Inside Macintosh: Imaging With QuickDraw, The Pattern Resource",
            ("Pattern", "H008")),
        [FourCC.FromString("PAT#")] = Make("Inside Macintosh: Imaging With QuickDraw, The Pattern List Resource",
            ("Patterns", "OCNT"), ("*****", "LSTC"), ("Pattern", "H008"), ("*****", "LSTE")),
        [FourCC.FromString("clut")] = Make("Inside Macintosh: Imaging With QuickDraw, The Color Table Resource", ColorTable),
        [FourCC.FromString("wctb")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Window Color Table Resource", ColorTable),
        [FourCC.FromString("actb")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Alert Color Table Resource", ColorTable),
        [FourCC.FromString("dctb")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Dialog Color Table Resource", ColorTable),
        [FourCC.FromString("cctb")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Control Color Table Resource", ColorTable),
        [FourCC.FromString("mctb")] = Make("Inside Macintosh: Macintosh Toolbox Essentials, The Menu Color Information Table Resource",
            ("Entries", "OCNT"), ("*****", "LSTC"), ("Menu ID", "DWRD"), ("Item", "DWRD"),
            ("Colour 1 red", "HWRD"), ("Colour 1 green", "HWRD"), ("Colour 1 blue", "HWRD"),
            ("Colour 2 red", "HWRD"), ("Colour 2 green", "HWRD"), ("Colour 2 blue", "HWRD"),
            ("Colour 3 red", "HWRD"), ("Colour 3 green", "HWRD"), ("Colour 3 blue", "HWRD"),
            ("Colour 4 red", "HWRD"), ("Colour 4 green", "HWRD"), ("Colour 4 blue", "HWRD"),
            ("Reserved", "DWRD"), ("*****", "LSTE")),
    };

    /// <summary>The types ClassicMac has a template for.</summary>
    public static IReadOnlyList<FourCC> Types { get; } = [.. Templates.Keys];

    /// <summary>The built-in template for <paramref name="type"/>, or null when there is none.</summary>
    public static BuiltInTemplate? For(FourCC type) => Templates.GetValueOrDefault(type);

    private static BuiltInTemplate Make(string source, params (string Label, string Type)[] fields) =>
        new(ResourceTemplate.FromFields([.. fields.Select(f => new TemplateField(f.Label, f.Type))]), source);
}
