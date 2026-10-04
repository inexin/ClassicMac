using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.Fonts;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A size chip of the font sample: a point size, or the family's TrueType font (size 0).</summary>
public sealed record FontSizeChoice(string Label, int Size)
{
    public bool IsTrueType => Size == 0;
}

/// <summary>A style of the font sample; one without its own strike at the chosen size is QuickDraw's, from the plain strike.</summary>
public sealed record FontStyleChoice(string Label, int Face, bool HasStrike);

/// <summary>A font resource of the family, to select in the tree.</summary>
public sealed record FontResourceLink(string Type, short Id);

/// <summary>
/// A cell of the association matrix: the resource (<c>NFNT 393</c>), its depth badge, whether it is missing from the
/// file; or empty.
/// </summary>
public sealed record FontAssociationCell(string Text, FontResourceLink? Link, bool IsMissing, string? Depth)
{
    public static FontAssociationCell Empty { get; } = new("—", null, false, null);

    public bool IsEmpty => ReferenceEquals(this, Empty);

    public bool HasDepth => Depth is not null;

    public bool IsLink => Link is not null;

    public string? ToolTip => IsMissing ? "Not found in the open files" : null;
}

/// <summary>A row of the association matrix: a point size (or TrueType, Type 1), then a cell per style column.</summary>
public sealed record FontAssociationRow(string Label, IReadOnlyList<FontAssociationCell> Cells);

/// <summary>A style extra width: the style, the value in ems ("+0.031"), and whether it is zero (muted).</summary>
public sealed record FontStyleExtra(string Label, string Value, bool IsZero);

/// <summary>A style's PostScript name from the style-mapping table.</summary>
public sealed record FontStyleName(string Style, string Name);

/// <summary>A kerning pair: the two characters, their codes, the kern in ems and in pixels at the chosen size.</summary>
public sealed record FontKernRow(string Pair, string Codes, string Em, string Pixels, byte First, byte Second);

/// <summary>The span of the sample a kerning pair covers, in the sample's pixels.</summary>
public readonly record struct FontSampleSpan(int X, int Width);

/// <summary>The sample line as drawn: black glyphs on a transparent canvas, ARGB words row by row, and as PNG.</summary>
public sealed class FontSample
{
    public FontSample(RgbaBitmap bitmap)
    {
        Width = bitmap.Width;
        Height = bitmap.Height;
        Pixels = new uint[Width * Height];
        var rgba = bitmap.Pixels;
        for (var i = 0; i < Pixels.Length; i++)
        {
            var a = rgba[4 * i + 3];
            Pixels[i] = a == 0 ? 0 : (uint)(a << 24 | rgba[4 * i] << 16 | rgba[4 * i + 1] << 8 | rgba[4 * i + 2]);
        }

        Png = Resources.Decoders.Images.PngEncoder.Instance.Encode(Width, Height, rgba);
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    public byte[] Png { get; }

    public uint Pixel(int x, int y) => Pixels[y * Width + x];
}

/// <summary>
/// The font family preview (design/boards/font-family.md, P6): the family's sizes and styles, a sample line drawn by
/// QuickDraw from the chosen strike (the Font Manager's choice, styles without a strike synthesised from the plain
/// one), the association matrix, the metrics, style and kerning cards. Strikes are looked for in the family's own file.
/// </summary>
public sealed partial class FontFamilyPreview : ObservableObject
{
    public const string DefaultSampleText = "The quick brown fox jumps over the lazy dog 0123456789";

    /// <summary>The kerning pairs shown before "Show all".</summary>
    public const int PairsShown = 8;

    private static readonly string[] StyleWords = ["Bold", "Italic", "Underline", "Outline", "Shadow", "Condensed", "Extended"];

    private readonly FontFamily family;
    private readonly int familyId;
    private readonly ResourceFork fork;
    private readonly ReadOptions readOptions;
    private readonly FontLibrary library;
    private bool showAllPairs;

    private FontFamilyPreview(FontFamily family, int familyId, ResourceFork fork, ReadOptions readOptions, bool isShort)
    {
        this.family = family;
        this.familyId = familyId;
        this.fork = fork;
        this.readOptions = readOptions;
        IsShort = isShort;
        library = new FontLibrary();
        foreach (var resource in fork.Resources)
        {
            var type = resource.Type.ToString();
            if (type is "FOND" or "NFNT" or "FONT")
            {
                var data = Data(resource);
                if (type == "FOND")
                {
                    library.AddFamily(resource.Id, resource.Name?.ToMacRoman(), data);
                }
                else if (type == "NFNT")
                {
                    library.AddNfnt(resource.Id, data);
                }
                else
                {
                    library.AddFont(resource.Id, data, resource.Name?.ToMacRoman());
                }
            }
        }

        Columns = [.. ColumnFaces().Select(FaceName)];
        Rows = MakeRows();
        Sizes = [.. family.BitmapSizes.Select(s => new FontSizeChoice(s.ToString(CultureInfo.InvariantCulture), s)),
            .. family.Fonts.Any(f => f.Size == 0) ? [new FontSizeChoice("TrueType", 0)] : Array.Empty<FontSizeChoice>()];
        chosenSize = Sizes.FirstOrDefault(s => s.Size == 12)
            ?? Sizes.Where(s => s.Size > 0).OrderBy(s => Math.Abs(s.Size - 12)).FirstOrDefault()
            ?? Sizes.FirstOrDefault();
        styles = MakeStyles();
        chosenStyle = styles[0];
        Flags = [.. FlagNames()];
        StyleExtras = [.. ExtraLabels.Select((label, i) => Extra(label, i < family.StyleExtras.Count ? family.StyleExtras[i] : 0))];
        WidthTables = family.WidthTables.Count == 0 ? "None"
            : $"{Count(family.WidthTables.Count, "table")}: {string.Join(", ", family.WidthTables.Select(t => FaceName(t.Style & 0xFF)))}";
        StyleNames = MakeStyleNames();
        KerningSummary = family.KerningTables.Count == 0 ? "No kerning"
            : string.Join(" · ", family.KerningTables.Select(t => $"{FaceName(t.Style & 0xFF)} {Count(t.Pairs.Count, "pair")}")
                .Prepend(Count(family.KerningTables.Count, "table")));
        Refresh();
    }

    /// <summary>
    /// The preview of a <c>'FOND'</c> in <paramref name="fork"/>; null when it cannot be read. Problems reading it are
    /// the decoder's to report; a table cut short is noted on the cards.
    /// </summary>
    internal static FontFamilyPreview? Create(Resource resource, ReadOnlyMemory<byte> data, ResourceFork fork, ReadOptions readOptions)
    {
        var found = new List<Diagnostic>();
        FontFamily family;
        try
        {
            family = FontFamily.Read(data, resource.Name?.ToMacRoman() ?? "", found);
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            return null;
        }

        return new FontFamilyPreview(family, resource.Id, fork, readOptions, found.Any(d => d.Code == "font.short"));
    }

    /// <summary>The family's name.</summary>
    public string Name => family.Name;

    /// <summary>Whether a table ran past the data (<c>font.short</c>): "Read as far as the data goes".</summary>
    public bool IsShort { get; }

    public IReadOnlyList<FontSizeChoice> Sizes { get; }

    /// <summary>The chosen size chip (12 by default, else the nearest).</summary>
    [ObservableProperty]
    private FontSizeChoice? chosenSize;

    /// <summary>The styles to choose from: Plain, Bold, Italic, Bold Italic and any other with a strike in the family.</summary>
    [ObservableProperty]
    private IReadOnlyList<FontStyleChoice> styles;

    [ObservableProperty]
    private FontStyleChoice? chosenStyle;

    /// <summary>The sample line's text (Mac OS Roman; other characters draw as the missing symbol).</summary>
    [ObservableProperty]
    private string sampleText = DefaultSampleText;

    /// <summary>The sample as drawn; null for TrueType, or when the strike is not in the file.</summary>
    [ObservableProperty]
    private FontSample? sample;

    /// <summary>Where the sample comes from: "From 'NFNT' 393 · 12 pt plain · 1-bit".</summary>
    [ObservableProperty]
    private string sampleSource = "";

    /// <summary>The resource the sample comes from.</summary>
    [ObservableProperty]
    private FontResourceLink? sampleLink;

    /// <summary>Why there is no sample, or null.</summary>
    [ObservableProperty]
    private string? sampleMessage;

    /// <summary>The selected kerning pair's place in the sample, or null.</summary>
    [ObservableProperty]
    private FontSampleSpan? highlight;

    /// <summary>The kerning pair selected (it is highlighted in the sample).</summary>
    [ObservableProperty]
    private FontKernRow? selectedPair;

    /// <summary>The metrics card's rows: ascent, descent, leading and max width in ems and pixels, the character range.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyRow> metrics = [];

    /// <summary>The kerning pairs of the chosen style, strongest first; the first <see cref="PairsShown"/> until "Show all".</summary>
    [ObservableProperty]
    private IReadOnlyList<FontKernRow> kernPairs = [];

    /// <summary>"Show all 120", or null when every pair shows.</summary>
    [ObservableProperty]
    private string? showAllPairsLabel;

    public bool HasSample => Sample is not null;

    public bool HasSampleMessage => SampleMessage is not null;

    /// <summary>The style columns of the matrix: Plain, Bold, Italic, Bold Italic, then other styles present.</summary>
    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<FontAssociationRow> Rows { get; }

    /// <summary>The flags set, by name.</summary>
    public IReadOnlyList<string> Flags { get; }

    public string FlagsRaw => string.Create(CultureInfo.InvariantCulture, $"0x{family.Flags:X4}");

    public IReadOnlyList<FontStyleExtra> StyleExtras { get; }

    /// <summary>"2 tables: Plain, Bold" or "None".</summary>
    public string WidthTables { get; }

    /// <summary>The style-mapping table's font class, or null without one.</summary>
    public string? FontClass => family.StyleMapping?.FontClass.ToString(CultureInfo.InvariantCulture);

    public bool HasStyleMapping => family.StyleMapping is not null;

    /// <summary>The PostScript names of Plain, Bold, Italic and Bold Italic.</summary>
    public IReadOnlyList<FontStyleName> StyleNames { get; }

    /// <summary>"2 tables · Plain 120 pairs · Bold 98 pairs", or "No kerning".</summary>
    public string KerningSummary { get; }

    public bool HasKernPairs => KernPairs.Count > 0;

    public bool CanShowAllPairs => ShowAllPairsLabel is not null;

    /// <summary>Shows every kerning pair of the chosen style.</summary>
    [RelayCommand]
    private void ShowAllPairs()
    {
        showAllPairs = true;
        RefreshPairs();
    }

    partial void OnChosenSizeChanged(FontSizeChoice? value)
    {
        var face = ChosenStyle?.Face ?? 0;
        Styles = MakeStyles();
        chosenStyle = Styles.FirstOrDefault(s => s.Face == face) ?? Styles[0];
        OnPropertyChanged(nameof(ChosenStyle));
        Refresh();
    }

    partial void OnChosenStyleChanged(FontStyleChoice? value) => Refresh();

    partial void OnSampleTextChanged(string value) => Refresh();

    partial void OnSelectedPairChanged(FontKernRow? value) => Highlight = Span();

    partial void OnSampleChanged(FontSample? value) => OnPropertyChanged(nameof(HasSample));

    partial void OnSampleMessageChanged(string? value) => OnPropertyChanged(nameof(HasSampleMessage));

    partial void OnKernPairsChanged(IReadOnlyList<FontKernRow> value) => OnPropertyChanged(nameof(HasKernPairs));

    partial void OnShowAllPairsLabelChanged(string? value) => OnPropertyChanged(nameof(CanShowAllPairs));

    // The size the metrics and kerns are given at: the chosen one, 12 for TrueType.
    private int PointSize => ChosenSize is { Size: > 0 } size ? size.Size : 12;

    private void Refresh()
    {
        Metrics = MakeMetrics();
        RefreshPairs();
        Draw();
        Highlight = Span();
    }

    private void RefreshPairs()
    {
        var face = ChosenStyle?.Face ?? 0;
        var table = family.KerningTables.FirstOrDefault(t => (t.Style & 0xFF) == face) ?? family.KerningTables.FirstOrDefault(t => (t.Style & 0xFF) == 0);
        var all = table?.Pairs.OrderByDescending(p => Math.Abs(p.Kern)).ToList() ?? [];
        var size = PointSize;
        KernPairs = [.. all.Take(showAllPairs ? all.Count : PairsShown).Select(p => new FontKernRow(
            MacRoman.Decode([p.First]) + MacRoman.Decode([p.Second]),
            string.Create(CultureInfo.InvariantCulture, $"${p.First:X2} ${p.Second:X2}"),
            Em(p.Kern),
            string.Create(CultureInfo.InvariantCulture, $"{Pixels(p.Kern, size)} px"),
            p.First, p.Second))];
        ShowAllPairsLabel = !showAllPairs && all.Count > PairsShown ? $"Show all {all.Count}" : null;
    }

    // The association the Font Manager draws the chosen size and style from: the style's own, else the plain one.
    private (FontAssociation Association, bool Own)? Chosen()
    {
        if (ChosenSize is not { } size)
        {
            return null;
        }

        var face = ChosenStyle?.Face ?? 0;
        var atSize = family.Fonts.Where(f => f.Size == size.Size).ToList();
        var own = atSize.Where(f => f.Face == face).ToList();
        if (own.Count > 0)
        {
            return (own[0], true);
        }

        var plain = atSize.Where(f => f.Face == 0).ToList();
        if (plain.Count > 0)
        {
            return (plain[0], false);
        }

        return atSize.Count > 0 ? (atSize[0], false) : null;
    }

    private void Draw()
    {
        Sample = null;
        SampleLink = null;
        SampleMessage = null;
        SampleSource = "";
        if (!family.Fonts.Any(f => f.Size == 0 ? Find("sfnt", f.FontId) is not null : Strike(f) is not null))
        {
            SampleMessage = "This family has no strikes in the open files";
            return;
        }

        if (Chosen() is not var (association, own))
        {
            return;
        }

        if (association.Size == 0)
        {
            SampleSource = $"From 'sfnt' {association.FontId}";
            SampleLink = new FontResourceLink("sfnt", association.FontId);
            SampleMessage = "No preview for outline fonts yet";
            return;
        }

        var type = Find("NFNT", association.FontId) is not null ? "NFNT" : Find("FONT", association.FontId) is not null ? "FONT" : null;
        if (type is null || Strike(association) is not { } strike)
        {
            SampleMessage = $"'NFNT' {association.FontId} is not in the open files";
            return;
        }

        var face = ChosenStyle?.Face ?? 0;
        var style = own || face == 0 ? FaceWord(association.Face) : $"{FaceWord(association.Face)}, {FaceWord(face & ~association.Face)} by QuickDraw";
        SampleSource = string.Create(CultureInfo.InvariantCulture, $"From '{type}' {association.FontId} · {association.Size} pt {style} · {association.Depth}-bit");
        SampleLink = new FontResourceLink(type, association.FontId);
        Sample = Render(Encode(SampleText, strike), association.Size, face);
    }

    // The sample text in Mac OS Roman; a character outside it becomes one the strike has no glyph for (its missing symbol).
    private static byte[] Encode(string text, BitmapFont strike)
    {
        var missing = strike.LastChar < 255 ? (byte)255 : strike.FirstChar > 0 ? (byte)0 : (byte)255;
        return [.. text.Select(c => MacRoman.TryGetByte(c, out var b) ? b : missing)];
    }

    private QuickDrawPort Port(RgbaBitmap canvas, int size, int face) => new(canvas, new QuickDrawOptions { Fonts = library })
    {
        TextFont = familyId,
        TextSize = size,
        TextFace = (QuickDrawStyle)face,
    };

    // The line drawn by QuickDraw: as wide as TextWidth (plus room for a synthesised italic's slant, bold's smear,
    // outline and shadow), as tall as the ascent and descent.
    private FontSample? Render(byte[] text, int size, int face)
    {
        var probe = Port(new RgbaBitmap(1, 1), size, face);
        var info = probe.GetFontInfo();
        var height = info.Ascent + info.Descent;
        if (height <= 0)
        {
            return null;
        }

        var own = Chosen() is { Own: true } chosen ? chosen.Association.Face : 0;
        var synthesised = face & ~own;
        var slack = ((synthesised & 2) != 0 ? (info.Ascent + 1) / 2 : 0) + ((synthesised & 1) != 0 ? 1 : 0) + ((synthesised & 0x18) != 0 ? 2 : 0);
        var width = Math.Max(1, probe.TextWidth(text) + slack);
        var canvas = new RgbaBitmap(width, height);
        var port = Port(canvas, size, face);
        port.MoveTo(0, info.Ascent);
        port.DrawText(text);
        return new FontSample(canvas);
    }

    // Where the selected pair first appears in the sample.
    private FontSampleSpan? Span()
    {
        if (SelectedPair is not { } pair || Sample is null || Chosen() is not var (association, _) || Strike(association) is not { } strike)
        {
            return null;
        }

        var text = Encode(SampleText, strike);
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == pair.First && text[i + 1] == pair.Second)
            {
                var port = Port(new RgbaBitmap(1, 1), association.Size, ChosenStyle?.Face ?? 0);
                return new FontSampleSpan(port.TextWidth(text.AsSpan(0, i)), port.TextWidth(text.AsSpan(i, 2)));
            }
        }

        return null;
    }

    /// <summary>"Aa" in the family's 24 pt strike, else its largest one in the file, as PNG; null without one.</summary>
    internal byte[]? Tile()
    {
        var sizes = family.Fonts.Where(f => f.Size > 0 && f.Face == 0 && Strike(f) is not null).Select(f => f.Size).ToList();
        if (sizes.Count == 0)
        {
            return null;
        }

        var size = sizes.Contains(24) ? 24 : sizes.Max();
        var strike = Strike(family.Fonts.First(f => f.Size == size && f.Face == 0))!;
        return Render(Encode("Aa", strike), size, 0) is { } sample ? NodeImages.Fit(Bitmap(sample), 32) : null;
    }

    private static RgbaBitmap Bitmap(FontSample sample)
    {
        var bitmap = new RgbaBitmap(sample.Width, sample.Height);
        for (var y = 0; y < sample.Height; y++)
        {
            for (var x = 0; x < sample.Width; x++)
            {
                var p = sample.Pixel(x, y);
                bitmap[x, y] = new RgbaColor((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24));
            }
        }

        return bitmap;
    }

    private IReadOnlyList<FontStyleChoice> MakeStyles()
    {
        var size = ChosenSize?.Size;
        var faces = new List<int> { 0, 1, 2, 3 };
        faces.AddRange(family.Fonts.Select(f => f.Face).Where(f => f > 3).Distinct().Order());
        return [.. faces.Select(face =>
        {
            var has = family.Fonts.Any(f => f.Size == size && f.Face == face);
            return new FontStyleChoice(face == 0 || has ? FaceName(face) : $"{FaceName(face)} (QuickDraw)", face, has);
        })];
    }

    // The style extras' labels, in the FOND's order.
    private static readonly string[] ExtraLabels = ["Plain", "Bold", "Italic", "Underline", "Outline", "Shadow", "Condense", "Extend"];

    // Plain, bold, italic and bold italic: the faces every family has a column or name for.
    private static readonly int[] BasicFaces = [0, 1, 2, 3];

    private IEnumerable<int> ColumnFaces() =>
        BasicFaces.Concat(family.Fonts.Where(f => f.Size >= 0).Select(f => f.Face).Where(f => f > 3).Distinct().Order());

    private List<FontAssociationRow> MakeRows()
    {
        var faces = ColumnFaces().ToList();
        var rows = new List<FontAssociationRow>();
        foreach (var size in family.Fonts.Select(f => f.Size).Distinct().OrderBy(s => s <= 0 ? int.MaxValue - s : s))
        {
            var label = size > 0 ? size.ToString(CultureInfo.InvariantCulture) : size == 0 ? "TrueType" : "Type 1 (ATM)";
            rows.Add(new FontAssociationRow(label, [.. faces.Select(face =>
                family.Fonts.Where(f => f.Size == size && f.Face == face).Select(Cell).FirstOrDefault() ?? FontAssociationCell.Empty)]));
        }

        return rows;
    }

    private FontAssociationCell Cell(FontAssociation font)
    {
        var depth = (font.Style >> 8 & 7) != 0 ? $"{font.Depth}-bit" : null;
        var type = font.Size switch
        {
            0 => "sfnt",
            < 0 => "afnt",
            _ => Find("NFNT", font.FontId) is null && Find("FONT", font.FontId) is not null ? "FONT" : "NFNT",
        };
        var found = Find(type, font.FontId) is not null;
        return new FontAssociationCell($"{type} {font.FontId}", found ? new FontResourceLink(type, font.FontId) : null, !found, depth);
    }

    private IReadOnlyList<PropertyRow> MakeMetrics()
    {
        var size = PointSize;
        PropertyRow Metric(string label, double em) =>
            new(label, Em(em), true, Note: string.Create(CultureInfo.InvariantCulture, $"{Pixels(em, size)} px at {size} pt"));
        return
        [
            Metric("Ascent", family.Ascent),
            Metric("Descent", family.Descent),
            Metric("Leading", family.Leading),
            Metric("Max width", family.MaxWidth),
            new("First and last character", string.Create(CultureInfo.InvariantCulture, $"${family.FirstChar:X2}–${family.LastChar:X2}"), true),
        ];
    }

    private IEnumerable<string> FlagNames()
    {
        if ((family.Flags & 0x0002) != 0)
        {
            yield return "Has width tables";
        }

        if ((family.Flags & 0x1000) != 0)
        {
            yield return "Use style extra widths";
        }

        if ((family.Flags & 0x2000) != 0)
        {
            yield return "Never use style extra widths";
        }

        if ((family.Flags & 0x4000) != 0)
        {
            yield return "Ignore width tables";
        }

        if ((family.Flags & 0x8000) != 0)
        {
            yield return "Fixed width";
        }
    }

    // Plain, Bold, Italic and Bold Italic's names: the base name, then for a style whose index is past the base, the
    // suffixes its string lists (each character an index of a suffix string) [Inside Macintosh: Text, the
    // style-mapping table].
    private IReadOnlyList<FontStyleName> MakeStyleNames()
    {
        if (family.StyleMapping is not { Names.Count: > 0 } mapping)
        {
            return [];
        }

        string Derived(int style)
        {
            var index = style < mapping.Indexes.Count ? mapping.Indexes[style] : 0;
            if (index <= 1 || index > mapping.Names.Count)
            {
                return mapping.Names[0];
            }

            return mapping.Names[0] + string.Concat(mapping.Names[index - 1]
                .Select(c => (int)c is var i && i >= 1 && i <= mapping.Names.Count ? mapping.Names[i - 1] : ""));
        }

        return [.. BasicFaces.Select(s => new FontStyleName(FaceName(s), Derived(s)))];
    }

    private static FontStyleExtra Extra(string label, double em) =>
        new(label, em == 0 ? "0" : (em > 0 ? "+" : "-") + Math.Abs(em).ToString("0.000", CultureInfo.InvariantCulture), em == 0);

    private static string Em(double em) => em.ToString("0.000", CultureInfo.InvariantCulture) + " em";

    private static int Pixels(double em, int size) => (int)Math.Round(em * size, MidpointRounding.AwayFromZero);

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    /// <summary>A QuickDraw style's name: "Plain", "Bold Italic", "Bold Condensed".</summary>
    internal static string FaceName(int face) =>
        face == 0 ? "Plain" : string.Join(' ', StyleWords.Where((_, bit) => (face & (1 << bit)) != 0));

    // A style in the sample's source line, lower case: "plain", "bold", "bold italic".
    private static string FaceWord(int face) => FaceName(face).ToLowerInvariant();

    private Resource? Find(string type, short id) => fork.Find(FourCC.FromString(type), id);

    private byte[] Data(Resource resource) => ResourceDecompression.Default.GetData(resource, fork, readOptions, []).ToArray();

    private BitmapFont? Strike(FontAssociation font)
    {
        try
        {
            return FontFamily.Strike(font, (type, id) => Find(type.ToString(), id) is { } r ? Data(r) : null);
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            return null;
        }
    }
}
