using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Finder;

/// <summary>
/// ClassicMac's own table of what files and resources are called, in its own words (no Apple data is copied):
/// documents by file type and creator, the Finder's kinds of applications and system files by type, applications by
/// signature, and resource types (docs/formats/resources/finder.md §5). It is the fallback for what the volume does not
/// say: <see cref="Resolve"/> puts it after the creator application's own kinds.
/// </summary>
public static class KnownKinds
{
    private static FourCC F(string s) => FourCC.FromString(s);

    // (type, creator) → kind; a creator of null stands for any creator. Written here in ClassicMac's words; the codes
    // were cross-checked against public type and creator lists (finder.md §9), none of whose text is copied.
    private static readonly Dictionary<(FourCC Type, FourCC? Creator), string> Documents = Table(
    [
        // Apple
        ("TEXT", "ttxt", "SimpleText text document"), ("ttro", "ttxt", "SimpleText read-only document"), ("PICT", "ttxt", "SimpleText picture"),
        ("sEXT", "ttxt", "SimpleText stationery"), ("TEXT", "hbwr", "Apple Help page"), ("MooV", "TVOD", "QuickTime movie"),
        ("PICT", "ogle", "PictureViewer picture"), ("JPEG", "ogle", "PictureViewer JPEG picture"), ("GIFf", "ogle", "PictureViewer GIF picture"),
        ("osas", "ToyS", "AppleScript script"), ("TEXT", "ToyS", "AppleScript text"), ("APPL", "aplt", "AppleScript applet"),
        ("sfil", "movr", "sound"), ("FFIL", "DMOV", "font suitcase"), ("clpt", "drag", "text clipping"), ("clpp", "drag", "picture clipping"),
        ("clps", "drag", "sound clipping"), ("STAK", "WILD", "HyperCard stack"), ("rsrc", "RSED", "ResEdit file"),
        ("TEXT", "MPS ", "MPW text file"), ("PDF ", "CARO", "Acrobat PDF document"), ("TEXT", "CARO", "Acrobat Reader text"),
        // Disk images
        ("dimg", "ddsk", "Disk Copy read-write disk image"), ("rohd", "ddsk", "Disk Copy read-only disk image"),
        ("hdro", "ddsk", "Disk Copy read-only disk image"), ("dseg", "ddsk", "part of a Disk Copy disk image"),
        ("devi", "ddsk", "Disk Copy device image"), ("devs", "ddsk", "part of a Disk Copy device image"),
        ("dImg", "dCpy", "Disk Copy 4.2 disk image"), ("dImg", "Wrap", "ShrinkWrap disk image"), ("DMd1", "DART", "DART disk image"),
        // Archives and encodings
        ("SIT!", "SIT!", "StuffIt archive"), ("SIT5", "SIT!", "StuffIt 5 archive"), ("SITD", "SIT!", "StuffIt Deluxe archive"),
        ("SegM", "SIT!", "StuffIt archive segment"), ("SITD", "SITx", "StuffIt Deluxe archive"), ("PACT", "CPCT", "Compact Pro archive"),
        ("DDF2", "DDAP", "DiskDoubler archive"), ("PIT ", "PIT ", "PackIt archive"), ("TEXT", "BnHq", "BinHex file"),
        ("ZIP ", "ZIP ", "zip archive"), ("ZIP ", "SITx", "zip archive"), ("Gzip", "Gzip", "gzip archive"), ("TARF", "TAR ", "tar archive"),
        // Word processing and office
        ("WDBN", "MSWD", "Microsoft Word 3–5 document"), ("W6BN", "MSWD", "Microsoft Word 6 document"), ("W8BN", "MSWD", "Microsoft Word 98 document"),
        ("TEXT", "MSWD", "Microsoft Word text document"), ("XLS3", "XCEL", "Microsoft Excel 3 worksheet"), ("XLS4", "XCEL", "Microsoft Excel 4 worksheet"),
        ("XLS5", "XCEL", "Microsoft Excel 5 workbook"), ("XLS8", "XCEL", "Microsoft Excel 98 workbook"), ("XLC3", "XCEL", "Microsoft Excel 3 chart"),
        ("XLW4", "XCEL", "Microsoft Excel 4 workbook"), ("SLD3", "PPT3", "PowerPoint 3 presentation"), ("SLD8", "PPT3", "PowerPoint 98 presentation"),
        ("CWWP", "BOBO", "ClarisWorks word processing document"), ("CWDB", "BOBO", "ClarisWorks database"), ("CWSS", "BOBO", "ClarisWorks spreadsheet"),
        ("CWGR", "BOBO", "ClarisWorks drawing"), ("CWPT", "BOBO", "ClarisWorks painting"), ("CWCM", "BOBO", "ClarisWorks communications document"),
        ("sWWP", "BOBO", "ClarisWorks word processing stationery"), ("WORD", "MACA", "MacWrite document"), ("MW2D", "MWII", "MacWrite II document"),
        ("MWPd", "MWPR", "MacWrite Pro document"), ("nX^d", "nX^n", "WriteNow document"), ("TEXT", "NISI", "Nisus Writer document"),
        ("FMPR", "FMPR", "FileMaker Pro database"), ("FMP3", "FMP3", "FileMaker Pro 3 database"), ("FMP5", "FMP5", "FileMaker Pro 5 database"),
        // Text and programming
        ("TEXT", "R*ch", "BBEdit text document"), ("TEXT", "ALFA", "Alpha text document"), ("TEXT", "EDIT", "Edit text document"),
        ("TEXT", "CWIE", "CodeWarrior source file"), ("MMPr", "CWIE", "CodeWarrior project"), ("TEXT", "KAHL", "THINK C source file"),
        ("PROJ", "KAHL", "THINK C project"), ("TEXT", "PJMM", "THINK Pascal source file"),
        // Graphics and publishing
        ("PNTG", "MPNT", "MacPaint document"), ("DRWG", "MDRW", "MacDraw drawing"), ("PICT", "MDRW", "MacDraw picture"), ("DRWG", "MDPL", "MacDraw II drawing"),
        ("8BPS", "8BIM", "Photoshop document"), ("JPEG", "8BIM", "Photoshop JPEG picture"), ("GIFf", "8BIM", "Photoshop GIF picture"),
        ("TIFF", "8BIM", "Photoshop TIFF picture"), ("PICT", "8BIM", "Photoshop PICT picture"), ("EPSF", "8BIM", "Photoshop EPS file"),
        ("GIFf", "GKON", "GraphicConverter GIF picture"), ("JPEG", "GKON", "GraphicConverter JPEG picture"), ("PICT", "GKON", "GraphicConverter picture"),
        ("TIFF", "GKON", "GraphicConverter TIFF picture"), ("PNGf", "GKON", "GraphicConverter PNG picture"),
        ("XDOC", "XPR3", "QuarkXPress document"), ("XTMP", "XPR3", "QuarkXPress template"), ("ALB3", "ALD3", "PageMaker 3 document"),
        ("ALB4", "ALD4", "PageMaker 4 document"), ("ALB5", "ALD5", "PageMaker 5 document"), ("ALB6", "ALD6", "PageMaker 6 document"),
        // Internet and sound
        ("TEXT", "MOSS", "Netscape HTML document"), ("HTML", "MOSS", "Netscape HTML document"), ("TEXT", "MSIE", "Internet Explorer HTML document"),
        ("TEXT", "CSOm", "Eudora mail file"), ("AIFF", "SCPL", "SoundApp sound"), ("WAVE", "TVOD", "WAVE sound"), ("ULAW", "TVOD", "μ-law sound"),
        ("Sd2f", "Sd2a", "Sound Designer II sound"),
        // Games
        ("scen", "RLMZ", "Realmz scenario"),
        // Any creator
        ("TEXT", null, "text document"), ("PICT", null, "PICT picture"), ("PNTG", null, "MacPaint document"), ("SIT!", null, "StuffIt archive"),
        ("SIT5", null, "StuffIt 5 archive"), ("SITD", null, "StuffIt Deluxe archive"), ("PACT", null, "Compact Pro archive"),
        ("PIT ", null, "PackIt archive"), ("dImg", null, "Disk Copy 4.2 disk image"), ("DMd1", null, "DART disk image"), ("DMdf", null, "DART disk image"),
        ("DDim", null, "DiskDup+ disk image"), ("hdrv", null, "raw disk image"), ("STAK", null, "HyperCard stack"), ("8BPS", null, "Photoshop document"),
        ("MooV", null, "QuickTime movie"), ("AIFF", null, "AIFF sound"), ("AIFC", null, "AIFF-C sound"), ("WAVE", null, "WAVE sound"),
        ("qtif", null, "QuickTime image"), ("JPEG", null, "JPEG picture"), ("GIFf", null, "GIF picture"), ("TIFF", null, "TIFF picture"),
        ("PNGf", null, "PNG picture"), ("EPSF", null, "EPS file"), ("PDF ", null, "PDF document"), ("HTML", null, "HTML document"),
        ("ZIP ", null, "zip archive"), ("Gzip", null, "gzip archive"), ("TARF", null, "tar archive"), ("pref", null, "preferences file"),
        ("rsrc", null, "resource file"), ("WDBN", null, "Microsoft Word document"), ("XDOC", null, "QuarkXPress document"),
    ]);

    // The Finder's own kinds by type, whatever the creator: English copies of the Finder's strings, used when the volume
    // has no Finder to read them from (finder.md §2.4) [Code: Finder 9.2.2].
    private static readonly Dictionary<FourCC, string> SystemKinds = SystemTable();

    private static Dictionary<FourCC, string> SystemTable()
    {
        var table = new Dictionary<FourCC, string>();
        void Add(string kind, params string[] types)
        {
            foreach (var type in types)
            {
                table[F(type)] = kind;
            }
        }

        Add("application program", "APPL", "APPC", "APPD");
        Add("desk accessory", "dfil");
        Add("desk accessory suitcase", "DFIL");
        Add("font suitcase", "FFIL");
        Add("font", "ffil", "tfil", "sfnt", "ttcf");
        Add("keyboard layout", "kfil");
        Add("sound", "sfil");
        Add("script", "ifil");
        Add("picture clipping", "clpp");
        Add("text clipping", "clpt");
        Add("sound clipping", "clps");
        Add("Mac OS X alias", "slnk");
        Add("system file", "zsys");
        // 'fmap' 5111 into 'STR#' 5100.
        Add("Chooser extension", "RDEV", "PRER", "PRES", "pdvr");
        Add("system extension", "INIT", "thng", "sLnk", "adev", "mdev", "appe", "etpp", "ttpp", "atlk", "scri", "ndrv", "comd", "CLMP", "fext");
        Add("database extension", "ddev");
        Add("communications tool", "cbnd", "tbnd", "fbnd");
        Add("control panel", "cdev");
        Add("PostScript® font", "LWFN");
        Add("printing extension", "pext");
        Add("paper type", "drpt", "uspt");
        Add("control strip module", "sdev");
        Add("OpenDoc® editor", "oded");
        Add("library", "shlb", "libr");
        Add("Text Encoding Converter document", "utbl", "ecpg");
        Add("contextual menu plug-in", "cmpi");
        Add("modem script", "mlts");
        Add("scripting addition", "osax");
        // 'fmap' 11010 into 'STR#' 11000 [Fitted].
        Add("internet location", "url ", "ilge");
        Add("web page location", "ilht");
        Add("ftp location", "ilft");
        Add("network location", "ilaf");
        Add("file location", "ilfi");
        Add("email address", "ilma");
        Add("news location", "ilnw");
        Add("AppleTalk zone location", "ilat");
        Add("neighborhood location", "ilns");
        return table;
    }

    // Editions take their application's name: "^0 edition".
    private static readonly HashSet<FourCC> EditionTypes = [F("edtP"), F("edtp"), F("edtT"), F("edts"), F("edtt"), F("edtu"), F("publ")];

    private static readonly FourCC Macs = F("MACS"), Fndr = F("FNDR");

    // Applications by signature.
    private static readonly Dictionary<FourCC, string> Applications = new[]
    {
        ("ttxt", "SimpleText"), ("hbwr", "Apple Help Viewer"), ("TVOD", "QuickTime Player"), ("ogle", "PictureViewer"), ("ToyS", "Script Editor"),
        ("fndf", "Sherlock"), ("RonA", "Graphing Calculator"), ("ddsk", "Disk Copy"), ("dCpy", "Disk Copy"), ("DART", "DART"), ("Wrap", "ShrinkWrap"),
        ("sbkt", "Scrapbook"), ("notz", "Stickies"), ("calc", "Calculator"), ("keyc", "Key Caps"), ("DMOV", "Font/DA Mover"), ("MACS", "Mac OS"),
        ("WILD", "HyperCard"), ("RSED", "ResEdit"), ("MPS ", "MPW Shell"), ("CARO", "Acrobat Reader"),
        ("SIT!", "StuffIt"), ("SITx", "StuffIt Expander"), ("DStf", "DropStuff"), ("CPCT", "Compact Pro"), ("DDAP", "DiskDoubler"), ("BnHq", "BinHex"),
        ("BNHQ", "BinHex 4"), ("PIT ", "PackIt"),
        ("MSWD", "Microsoft Word"), ("XCEL", "Microsoft Excel"), ("PPT3", "Microsoft PowerPoint"), ("BOBO", "ClarisWorks"), ("CWKS", "ClarisWorks"),
        ("MACA", "MacWrite"), ("MWII", "MacWrite II"), ("MWPR", "MacWrite Pro"), ("nX^n", "WriteNow"), ("NISI", "Nisus Writer"),
        ("FMPR", "FileMaker Pro"), ("FMP3", "FileMaker Pro 3"), ("FMP5", "FileMaker Pro 5"),
        ("R*ch", "BBEdit"), ("ALFA", "Alpha"), ("EDIT", "Edit"), ("CWIE", "CodeWarrior"), ("KAHL", "THINK C"), ("PJMM", "THINK Pascal"),
        ("MPNT", "MacPaint"), ("MDRW", "MacDraw"), ("MDPL", "MacDraw II"), ("8BIM", "Adobe Photoshop"), ("ART3", "Adobe Illustrator"),
        ("GKON", "GraphicConverter"), ("XPR3", "QuarkXPress"), ("ALD3", "PageMaker 3"), ("ALD4", "PageMaker 4"), ("ALD5", "PageMaker 5"),
        ("ALD6", "PageMaker 6"),
        ("MOSS", "Netscape"), ("MSIE", "Internet Explorer"), ("CSOm", "Eudora"), ("FTCh", "Fetch"), ("SCPL", "SoundApp"), ("Sd2a", "Sound Designer II"),
        ("Dk@P", "DOCMaker"),
        ("RLMZ", "Realmz"), ("MYST", "Myst"), ("Civ2", "Civilization II"), ("MCRP", "SimCity"), ("DCAS", "Dark Castle"), ("GLID", "Glider"),
        ("PUCK", "Shufflepuck Café"), ("ORGN", "Oregon Trail"), ("Psyg", "Lemmings"),
    }.ToDictionary(a => F(a.Item1), a => a.Item2);

    // Resource types (singular, plural).
    private static readonly Dictionary<FourCC, (string One, string Many)> ResourceTypes = new()
    {
        [F("STR ")] = ("String", "Strings"), [F("STR#")] = ("String list", "String lists"), [F("TEXT")] = ("Text", "Texts"),
        [F("styl")] = ("Text style", "Text styles"), [F("PICT")] = ("Picture", "Pictures"), [F("ICON")] = ("Icon", "Icons"),
        [F("ICN#")] = ("Icon list", "Icon lists"), [F("icl4")] = ("4-bit icon", "4-bit icons"), [F("icl8")] = ("8-bit icon", "8-bit icons"),
        [F("ics#")] = ("Small icon list", "Small icon lists"), [F("ics4")] = ("Small 4-bit icon", "Small 4-bit icons"),
        [F("ics8")] = ("Small 8-bit icon", "Small 8-bit icons"), [F("icns")] = ("Icon family", "Icon families"),
        [F("cicn")] = ("Colour icon", "Colour icons"), [F("SICN")] = ("Small icons", "Small icons"), [F("CURS")] = ("Cursor", "Cursors"),
        [F("crsr")] = ("Colour cursor", "Colour cursors"), [F("snd ")] = ("Sound", "Sounds"), [F("MENU")] = ("Menu", "Menus"),
        [F("MBAR")] = ("Menu bar", "Menu bars"), [F("DLOG")] = ("Dialog", "Dialogs"), [F("DITL")] = ("Dialog item list", "Dialog item lists"),
        [F("ALRT")] = ("Alert", "Alerts"), [F("WIND")] = ("Window", "Windows"), [F("CNTL")] = ("Control", "Controls"),
        [F("vers")] = ("Version", "Versions"), [F("PAT ")] = ("Pattern", "Patterns"), [F("PAT#")] = ("Pattern list", "Pattern lists"),
        [F("ppat")] = ("Pixel pattern", "Pixel patterns"), [F("FOND")] = ("Font family", "Font families"), [F("NFNT")] = ("Bitmap font", "Bitmap fonts"),
        [F("FONT")] = ("Font", "Fonts"), [F("sfnt")] = ("TrueType font", "TrueType fonts"), [F("CODE")] = ("Code segment", "Code segments"),
        [F("TMPL")] = ("Template", "Templates"), [F("BNDL")] = ("Bundle", "Bundles"), [F("FREF")] = ("File reference", "File references"),
        [F("clut")] = ("Colour table", "Colour tables"), [F("pltt")] = ("Palette", "Palettes"), [F("kind")] = ("Kind list", "Kind lists"),
        [F("SIZE")] = ("Size resource", "Size resources"), [F("cfrg")] = ("Code fragment list", "Code fragment lists"),
        [F("hfdr")] = ("Finder help", "Finder helps"), [F("dctb")] = ("Dialog colours", "Dialog colours"), [F("wctb")] = ("Window colours", "Window colours"),
        [F("actb")] = ("Alert colours", "Alert colours"), [F("cctb")] = ("Control colours", "Control colours"), [F("mctb")] = ("Menu colours", "Menu colours"),
        [F("dcmp")] = ("Decompressor", "Decompressors"), [F("DRVR")] = ("Driver", "Drivers"), [F("WDEF")] = ("Window definition", "Window definitions"),
        [F("MDEF")] = ("Menu definition", "Menu definitions"), [F("CDEF")] = ("Control definition", "Control definitions"),
        [F("LDEF")] = ("List definition", "List definitions"), [F("INIT")] = ("Startup code", "Startup code"), [F("PREC")] = ("Print record", "Print records"),
        [F("moov")] = ("QuickTime movie", "QuickTime movies"), [F("open")] = ("Open list", "Open lists"),
    };

    private static Dictionary<(FourCC, FourCC?), string> Table(IEnumerable<(string Type, string? Creator, string Kind)> entries) =>
        entries.ToDictionary(e => (F(e.Type), e.Creator is null ? (FourCC?)null : F(e.Creator)), e => e.Kind);

    /// <summary>The table's kind of a document of <paramref name="type"/> and <paramref name="creator"/>: the pair's, else the type's for any creator; null when unknown.</summary>
    public static string? Document(FourCC type, FourCC creator) => DocumentPair(type, creator) ?? DocumentType(type);

    private static string? DocumentPair(FourCC type, FourCC creator) => Documents.GetValueOrDefault((type, creator));

    private static string? DocumentType(FourCC type) => Documents.GetValueOrDefault((type, null));

    /// <summary>
    /// The Finder's own kind of <paramref name="type"/> in English ("application program", "control panel", "text
    /// clipping"; other <c>clp</c> types "clipping"); null for documents.
    /// </summary>
    public static string? System(FourCC type) =>
        SystemKinds.GetValueOrDefault(type) ?? (type.ToString().StartsWith("clp", StringComparison.Ordinal) ? "clipping" : null);

    /// <summary>The name of the application with <paramref name="signature"/>, or null when the table does not know it.</summary>
    public static string? ApplicationName(FourCC signature) => Applications.GetValueOrDefault(signature);

    /// <summary>What a resource of <paramref name="type"/> is called, singular and plural ("Text style", "Text styles"), or null.</summary>
    public static (string One, string Many)? ResourceType(FourCC type) => ResourceTypes.TryGetValue(type, out var names) ? names : null;

    /// <summary>Where a kind came from, for people: "from SimpleText’s 'kind' 128", "from Teach, by its name", "from the System’s 'kind' -16550", "built-in".</summary>
    public static string Describe(FinderKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Source switch
        {
            FinderKindSource.ApplicationKind => string.Create(CultureInfo.InvariantCulture, $"from {kind.Application ?? "its application"}’s 'kind' {kind.ResourceId}"),
            FinderKindSource.ApplicationName when kind.ResourceId is { } id => string.Create(CultureInfo.InvariantCulture, $"from {kind.Application}’s name in its 'kind' {id}"),
            FinderKindSource.ApplicationName => $"from {kind.Application}, by its file name",
            FinderKindSource.SystemKind => string.Create(CultureInfo.InvariantCulture, $"from the System’s 'kind' {kind.ResourceId}"),
            FinderKindSource.FinderKind => string.Create(CultureInfo.InvariantCulture, $"from the Finder’s {(kind.ResourceId == 6902 ? "'STR '" : "'STR#'")} {kind.ResourceId}"),
            FinderKindSource.Database => "TCDB",
            FinderKindSource.UserDatabase => "TCDB (your copy)",
            _ => "built-in",
        };
    }

    /// <summary>
    /// A file's kind, always one (finder.md §2.3–§2.5): the Finder's own kind of its type (read from the volume's Finder,
    /// else <see cref="System"/>; an edition "&lt;application&gt; edition"; another file with creator <c>'MACS'</c> "system
    /// file"); else <c>GetDocumentKindString</c>'s steps 1–3 on <paramref name="volume"/>; then ClassicMac's table for the
    /// type and creator; the System's <c>'istd'</c> kind; the table for the type; "&lt;the table's name for the
    /// creator&gt; document"; and last "document". The type and creator database (<paramref name="database"/>, a user's
    /// copy, else <see cref="TypeCreatorDatabase.Shipped"/>) comes after the Finder's kinds and ClassicMac's table: its
    /// kind of the type and creator before "&lt;application&gt; document", and its name for a creator the table does not
    /// know.
    /// </summary>
    public static FinderKind Resolve(FinderKindResolver? volume, FourCC type, FourCC creator, TypeCreatorDatabase? database = null)
    {
        var known = ApplicationName(creator);
        if (volume?.FindFinderKind(type) is { } finder)
        {
            return finder;
        }

        if (System(type) is { } system)
        {
            return new FinderKind(system, FinderKindSource.BuiltIn, null, null);
        }

        if (EditionTypes.Contains(type))
        {
            var name = volume?.ApplicationName(creator) ?? known;
            return new FinderKind(name is null ? "edition" : $"{name} edition", FinderKindSource.BuiltIn, name, null);
        }

        if (creator == Macs || type == Fndr)
        {
            return new FinderKind("system file", FinderKindSource.BuiltIn, null, null);   // [Fitted]
        }

        if (volume?.FindByApplication(type, creator) is { } application)
        {
            return application;
        }

        if (DocumentPair(type, creator) is { } pair)
        {
            return new FinderKind(pair, FinderKindSource.BuiltIn, known, null);
        }

        if (volume?.FindStandard(type) is { } standard)
        {
            return standard;
        }

        if (DocumentType(type) is { } generic)
        {
            return new FinderKind(generic, FinderKindSource.BuiltIn, known, null);
        }

        database ??= TypeCreatorDatabase.Shipped;
        if (database.Find(type, creator) is { } listed)
        {
            return listed;
        }

        if (known is not null)
        {
            return new FinderKind(volume?.DocumentOf(known) ?? $"{known} document", FinderKindSource.BuiltIn, known, null);
        }

        return database.ApplicationName(creator) is { } named
            ? new FinderKind(volume?.DocumentOf(named) ?? $"{named} document", database.Source, named, null)
            : new FinderKind(volume?.DocumentWord ?? "document", FinderKindSource.BuiltIn, null, null);
    }
}
