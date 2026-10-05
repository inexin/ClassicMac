using System.Collections.Generic;

namespace ClassicMac.Core;

/// <summary>
/// The Script Manager's scripts and regions as text encodings (docs/formats/codecs/text-encodings.md §2.1): which
/// encoding a script code and a system region mean, which script a font family's ID belongs to, and which encoding a
/// region's localized system uses.
/// </summary>
public static class MacScripts
{
    // Font family IDs from smFondStart ($4000) to smFondEnd ($C000) belong to the non-Roman scripts, 512 each, in script
    // code order; below and above them is Roman.
    private const int FondStart = 0x4000, FondEnd = 0xC000, FondsPerScript = 512;

    // Script codes whose encoding has the same number (kTextEncodingMac… = sm…), among those ClassicMac reads.
    private static readonly HashSet<int> SameNumber = [1, 2, 3, 4, 5, 6, 7, 21, 25, 29];

    // Regions' localized systems and their script, from the regions' languages (verJapan ja_JP …).
    private static readonly Dictionary<int, int> RegionScripts = new()
    {
        [14] = 1, [53] = 2, [52] = 25, [51] = 3,                                    // Japan, Taiwan, China, Korea
        [13] = 5, [16] = 4,                                                         // Israel, Arabic
        [49] = 7, [61] = 7, [62] = 7, [72] = 7, [65] = 7, [67] = 7,                 // Russia, Byelorussia, Ukraine, Bulgaria, Serbia, Macedonia
        [54] = 21,                                                                  // Thailand
        [56] = 29, [57] = 29, [42] = 29, [43] = 29, [41] = 29, [44] = 29, [45] = 29,// Czech, Slovak, Poland, Hungary, Lithuania, Estonia, Latvia
    };

    /// <summary>The script code of the font family <paramref name="familyId"/> (0, Roman, outside the Script Manager's range).</summary>
    public static int ScriptOfFontFamily(int familyId) =>
        familyId is >= FondStart and < FondEnd ? (familyId - FondStart) / FondsPerScript + 1 : 0;

    /// <summary>
    /// The encoding of text in script <paramref name="script"/> on a system of region <paramref name="region"/>, as
    /// Apple's mapping tables give it: Roman is Icelandic, Turkish, Croatian, Romanian or Greek on those regions'
    /// systems; Cyrillic is Ukrainian on Ukraine's. Null for a script ClassicMac has no encoding for.
    /// </summary>
    public static MacTextEncoding? Encoding(int script, int region) => script switch
    {
        0 => region switch
        {
            21 => MacTextEncoding.Icelandic,
            24 => MacTextEncoding.Turkish,
            68 or 25 => MacTextEncoding.Croatian,
            39 => MacTextEncoding.Romanian,
            20 => MacTextEncoding.Greek,
            _ => MacTextEncoding.Roman,
        },
        7 when region == 62 => MacTextEncoding.Ukrainian,
        _ when SameNumber.Contains(script) => (MacTextEncoding)script,
        _ => null,
    };

    /// <summary>The encoding a region's localized system uses for its own text (Mac OS Roman, or its variant, by default).</summary>
    public static MacTextEncoding EncodingOfRegion(int region) =>
        Encoding(RegionScripts.GetValueOrDefault(region), region) ?? MacTextEncoding.Roman;
}
