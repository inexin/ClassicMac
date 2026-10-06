using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// The icon a file shows by what it is (design/boards/browse-tree.md, T4): its file type, else, for a file with no
/// type of its own (from a zip archive, an ISO 9660 disc or a FAT disk) or a text file, its name's extension.
/// </summary>
public static class FileTypeIcons
{
    private static readonly Dictionary<string, TreeIconKind> Types = new(StringComparer.Ordinal)
    {
        ["APPL"] = TreeIconKind.Application, ["APPC"] = TreeIconKind.Application, ["APPD"] = TreeIconKind.Application,
        ["appe"] = TreeIconKind.Application,

        ["TEXT"] = TreeIconKind.Document, ["ttro"] = TreeIconKind.Document,

        ["PICT"] = TreeIconKind.Picture, ["JPEG"] = TreeIconKind.Picture, ["GIFf"] = TreeIconKind.Picture,
        ["GIF "] = TreeIconKind.Picture, ["PNGf"] = TreeIconKind.Picture, ["PNG "] = TreeIconKind.Picture,
        ["TIFF"] = TreeIconKind.Picture, ["BMP "] = TreeIconKind.Picture, ["BMPp"] = TreeIconKind.Picture,
        ["PNTG"] = TreeIconKind.Picture, ["8BPS"] = TreeIconKind.Picture, ["qtif"] = TreeIconKind.Picture,
        ["SCRN"] = TreeIconKind.Picture, ["EPSF"] = TreeIconKind.Picture,

        ["snd "] = TreeIconKind.Sound, ["sfil"] = TreeIconKind.Sound, ["AIFF"] = TreeIconKind.Sound,
        ["AIFC"] = TreeIconKind.Sound, ["WAVE"] = TreeIconKind.Sound, ["Sd2f"] = TreeIconKind.Sound,
        ["ULAW"] = TreeIconKind.Sound, ["MPG3"] = TreeIconKind.Sound, ["Mp3 "] = TreeIconKind.Sound,
        ["Midi"] = TreeIconKind.Sound, ["MiDi"] = TreeIconKind.Sound,

        ["MooV"] = TreeIconKind.Movie, ["MPEG"] = TreeIconKind.Movie, ["mpg4"] = TreeIconKind.Movie,
        ["VfW "] = TreeIconKind.Movie,

        ["PDF "] = TreeIconKind.Pdf,
        ["HTML"] = TreeIconKind.WebPage,

        ["WDBN"] = TreeIconKind.WordProcessor, ["W6BN"] = TreeIconKind.WordProcessor, ["W8BN"] = TreeIconKind.WordProcessor,
        ["WORD"] = TreeIconKind.WordProcessor, ["RTF "] = TreeIconKind.WordProcessor,

        ["XLS "] = TreeIconKind.Spreadsheet, ["XLS4"] = TreeIconKind.Spreadsheet, ["XLS5"] = TreeIconKind.Spreadsheet,
        ["XLS8"] = TreeIconKind.Spreadsheet,

        ["FFIL"] = TreeIconKind.Font, ["ffil"] = TreeIconKind.Font, ["tfil"] = TreeIconKind.Font,
        ["LWFN"] = TreeIconKind.Font, ["sfnt"] = TreeIconKind.Font,

        ["INIT"] = TreeIconKind.Extension,
        ["cdev"] = TreeIconKind.ControlPanel,
        ["pref"] = TreeIconKind.Preferences,
        ["zsys"] = TreeIconKind.SystemFile, ["FNDR"] = TreeIconKind.SystemFile, ["gbly"] = TreeIconKind.SystemFile,

        ["SIT!"] = TreeIconKind.Parcel, ["SITD"] = TreeIconKind.Parcel, ["SIT5"] = TreeIconKind.Parcel,
        ["PACT"] = TreeIconKind.Parcel, ["ZIP "] = TreeIconKind.Parcel, ["Gzip"] = TreeIconKind.Parcel,
        ["TARF"] = TreeIconKind.Parcel, ["LHA "] = TreeIconKind.Parcel,

        ["dImg"] = TreeIconKind.Floppy, ["rohd"] = TreeIconKind.Floppy,
    };

    private static readonly Dictionary<string, TreeIconKind> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = TreeIconKind.Picture, ["jpeg"] = TreeIconKind.Picture, ["gif"] = TreeIconKind.Picture,
        ["png"] = TreeIconKind.Picture, ["tif"] = TreeIconKind.Picture, ["tiff"] = TreeIconKind.Picture,
        ["bmp"] = TreeIconKind.Picture, ["pict"] = TreeIconKind.Picture, ["pct"] = TreeIconKind.Picture,
        ["psd"] = TreeIconKind.Picture, ["webp"] = TreeIconKind.Picture, ["eps"] = TreeIconKind.Picture,

        ["mp3"] = TreeIconKind.Sound, ["wav"] = TreeIconKind.Sound, ["aif"] = TreeIconKind.Sound,
        ["aiff"] = TreeIconKind.Sound, ["aifc"] = TreeIconKind.Sound, ["snd"] = TreeIconKind.Sound,
        ["mid"] = TreeIconKind.Sound, ["midi"] = TreeIconKind.Sound, ["m4a"] = TreeIconKind.Sound,

        ["mov"] = TreeIconKind.Movie, ["qt"] = TreeIconKind.Movie, ["mp4"] = TreeIconKind.Movie,
        ["m4v"] = TreeIconKind.Movie, ["avi"] = TreeIconKind.Movie, ["mpg"] = TreeIconKind.Movie,
        ["mpeg"] = TreeIconKind.Movie,

        ["pdf"] = TreeIconKind.Pdf,
        ["htm"] = TreeIconKind.WebPage, ["html"] = TreeIconKind.WebPage,

        ["doc"] = TreeIconKind.WordProcessor, ["docx"] = TreeIconKind.WordProcessor, ["rtf"] = TreeIconKind.WordProcessor,

        ["xls"] = TreeIconKind.Spreadsheet, ["xlsx"] = TreeIconKind.Spreadsheet, ["csv"] = TreeIconKind.Spreadsheet,

        ["ttf"] = TreeIconKind.Font, ["otf"] = TreeIconKind.Font, ["ttc"] = TreeIconKind.Font,

        ["sit"] = TreeIconKind.Parcel, ["sitx"] = TreeIconKind.Parcel, ["sea"] = TreeIconKind.Parcel,
        ["cpt"] = TreeIconKind.Parcel, ["zip"] = TreeIconKind.Parcel, ["gz"] = TreeIconKind.Parcel,
        ["tgz"] = TreeIconKind.Parcel, ["tar"] = TreeIconKind.Parcel, ["lha"] = TreeIconKind.Parcel,
        ["lzh"] = TreeIconKind.Parcel, ["hqx"] = TreeIconKind.Parcel,

        ["img"] = TreeIconKind.Floppy, ["dmg"] = TreeIconKind.Floppy, ["iso"] = TreeIconKind.Floppy,
        ["toast"] = TreeIconKind.Floppy, ["dsk"] = TreeIconKind.Floppy, ["hfv"] = TreeIconKind.Floppy,
        ["image"] = TreeIconKind.Floppy,
    };

    // Text whose extension says what text it is.
    private static readonly HashSet<TreeIconKind> TextKinds = [TreeIconKind.WebPage, TreeIconKind.Spreadsheet, TreeIconKind.WordProcessor];

    /// <summary>
    /// The icon for a file of <paramref name="type"/> named <paramref name="name"/>: a known type's; else (no type,
    /// <c>????</c>, or an unknown one) its extension's; a text file's extension when it names a kind of text; else the
    /// document page.
    /// </summary>
    public static TreeIconKind For(FourCC type, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var byName = Extension(name) is { } extension && Extensions.TryGetValue(extension, out var kind) ? kind : (TreeIconKind?)null;
        if (Types.TryGetValue(type.ToString(), out var byType))
        {
            return byType == TreeIconKind.Document && byName is { } text && TextKinds.Contains(text) ? text : byType;
        }

        return byName ?? TreeIconKind.Document;
    }

    // The part after the last '.', when there is one and something comes before it.
    private static string? Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : null;
    }
}
