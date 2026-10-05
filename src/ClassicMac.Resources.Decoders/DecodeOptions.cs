using System;
using System.Collections.Generic;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders;

/// <summary>How line breaks (CR on the Mac) are written in text output.</summary>
public enum LineEndings
{
    /// <summary>LF, as modern tools expect.</summary>
    Lf,

    /// <summary>As stored: CR.</summary>
    AsStored,
}

/// <summary>
/// Choices for the built-in decoders. Every tunable value lives here; the CLI and the app map their settings onto
/// this record.
/// </summary>
public sealed record DecodeOptions
{
    /// <summary>The defaults.</summary>
    public static DecodeOptions Default { get; } = new();

    /// <summary>The encoding text in resources and documents is read with (docs/formats/codecs/text-encodings.md). Default Mac OS Roman.</summary>
    public MacTextEncoding TextEncoding { get; init; } = MacTextEncoding.Roman;

    /// <summary>
    /// Whether text that says its own script is read in it, before <see cref="TextEncoding"/>: a styled run in a font of
    /// a non-Roman script, a <c>'vers'</c> resource's region (text-encodings.md §5). On by default.
    /// </summary>
    public bool AutomaticEncoding { get; init; } = true;

    /// <summary>How line breaks are written in <c>.txt</c> output. Default LF.</summary>
    public LineEndings LineEndings { get; init; } = LineEndings.Lf;

    /// <summary>How pictures and icons are written. Default PNG.</summary>
    public Images.IImageEncoder ImageEncoder { get; init; } = Images.PngEncoder.Instance;

    /// <summary>
    /// The screen depth pictures are drawn at: 32 (the default, full colour), or 1, 2, 4, 8 or 16 bits, where
    /// QuickDraw's colour matching and dithering for that depth apply (the output is still RGBA).
    /// </summary>
    public int ScreenDepth { get; init; } = 32;

    /// <summary>The largest picture decoded, in pixels (width × height). Default 64 megapixels.</summary>
    public long MaxImagePixels { get; init; } = 64L * 1024 * 1024;

    /// <summary>Whose QuickDraw pictures are drawn as: Mac OS 9's (the default) or the 68k ROM's.</summary>
    public ResourceManagerModel QuickDraw { get; init; } = ResourceManagerModel.MacOS9;

    /// <summary>
    /// Whether a TrueType <c>'sfnt'</c> is exported made loadable by Windows and other modern loaders
    /// (<see cref="ClassicMac.Graphics.Fonts.LoadableFont"/>: a Windows Unicode cmap, Windows names, OS/2 and post added
    /// where missing), instead of as the data it is. Default false.
    /// </summary>
    public bool LoadableFonts { get; init; }
}

// The types a built-in decoder handles (for the golden tests' coverage check).
internal interface IBuiltInDecoder
{
    IReadOnlyCollection<FourCC> Types { get; }
}

/// <summary>The built-in decoders.</summary>
public static class ResourceDecoders
{
    /// <summary>
    /// The built-in decoders for a file whose volume says its text is in an encoding (text-encodings.md §5), for
    /// <see cref="ExportOptions.DecodersFor"/>: with <see cref="DecodeOptions.AutomaticEncoding"/> on and the default
    /// Mac OS Roman as <see cref="DecodeOptions.TextEncoding"/>, decoders reading in the file's encoding (made once per
    /// encoding); otherwise the encoding chosen wins and every file gets <paramref name="options"/>' decoders.
    /// </summary>
    public static Func<MacTextEncoding, IReadOnlyList<IResourceDecoder>> CreateFor(DecodeOptions? options = null) =>
        For(options ?? DecodeOptions.Default, Create);

    /// <summary>The document converters for such a file, for <see cref="ExportOptions.DocumentsFor"/>, as <see cref="CreateFor"/>.</summary>
    public static Func<MacTextEncoding, IReadOnlyList<IDocumentConverter>> CreateDocumentConvertersFor(DecodeOptions? options = null) =>
        For(options ?? DecodeOptions.Default, CreateDocumentConverters);

    private static Func<MacTextEncoding, IReadOnlyList<T>> For<T>(DecodeOptions options, Func<DecodeOptions, IReadOnlyList<T>> create)
    {
        var own = create(options);
        if (!options.AutomaticEncoding || options.TextEncoding != MacTextEncoding.Roman)
        {
            return _ => own;
        }

        var made = new System.Collections.Concurrent.ConcurrentDictionary<MacTextEncoding, IReadOnlyList<T>>();
        return encoding => made.GetOrAdd(encoding, e => create(options with { TextEncoding = e }));
    }

    /// <summary>The built-in decoders with <paramref name="options"/>, for <see cref="ExportOptions.Decoders"/>.</summary>
    public static IReadOnlyList<IResourceDecoder> Create(DecodeOptions? options = null)
    {
        options ??= DecodeOptions.Default;
        return
        [
            new StringDecoder(options),
            new StringListDecoder(options),
            new TextDecoder(options),
            new StyleDecoder(),
            new VersionDecoder(options),
            new Images.PictureDecoder(options),
            new Images.IconDecoder(options),
            new Images.IconFamilyDecoder(options),
            new Images.CursorDecoder(options),
            new Images.PatternDecoder(options),
            new Sound.SoundDecoder(),
            .. Interface.InterfaceDecoder.All(options),
            .. Interface.ExtensionDecoder.All(options),
            .. Colors.PaletteDecoder.All(),
            .. Finder.FinderDecoder.All(options),
            new Finder.AliasDecoder(options),
            .. Fonts.FontDecoder.All(options),
            new Code.CodeSegmentDecoder(),
            new Code.CfrgDecoder(),
            new Code.CodeResourceDecoder(),
        ];
    }

    /// <summary>The built-in document converters with <paramref name="options"/>, for <see cref="ExportOptions.Documents"/>.</summary>
    public static IReadOnlyList<IDocumentConverter> CreateDocumentConverters(DecodeOptions? options = null) =>
        [new Documents.HtmlDocumentConverter(options ?? DecodeOptions.Default), new Sound.AiffConverter()];
}
