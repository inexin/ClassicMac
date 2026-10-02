using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// A dialog, alert or item list drawn as Mac OS 9's Appearance (Platinum) draws it: the drawing's model and the
    /// structure region's pixels.
    /// </summary>
    /// <param name="Drawing">What was drawn: the window's kind, size and items.</param>
    /// <param name="Png">The drawn window (frame, shadow, content), PNG.</param>
    /// <param name="PixelWidth">The image's width.</param>
    /// <param name="PixelHeight">The image's height.</param>
    public sealed record DialogPreview(DialogDrawing Drawing, byte[] Png, int PixelWidth, int PixelHeight);

    /// <summary>
    /// What the user's open files supply to draw dialogs with: the System's stand-ins (forks with the stop, note and caution
    /// icons, ID 0–2) and the fonts of every loaded fork.
    /// </summary>
    /// <param name="SystemForks">Forks with icons 0–2.</param>
    /// <param name="Fonts">The fonts, or null when none is loaded.</param>
    public sealed record DialogSources(IReadOnlyList<ResourceFork> SystemForks, FontLibrary? Fonts)
    {
        public static DialogSources None { get; } = new([], null);

        /// <summary>
        /// Forks with the System's Finder icons (<c>ICN#</c> or <c>'icns'</c> −4000, or the <c>'isrv'</c> 128 icon mapping
        /// table): the System and System Resources files, for folder previews.
        /// </summary>
        public IReadOnlyList<ResourceFork> GenericIconForks { get; init; } = [];

        private static readonly FourCC Fond = FourCC.FromString("FOND"), Nfnt = FourCC.FromString("NFNT"), Font = FourCC.FromString("FONT"),
            Icon = FourCC.FromString("ICON"), Cicn = FourCC.FromString("cicn"), IconList = FourCC.FromString("ICN#"),
            Icns = FourCC.FromString("icns"), Isrv = FourCC.FromString("isrv");

        /// <summary>The sources among the forks whose resources are loaded in the tree.</summary>
        public static DialogSources From(IEnumerable<NodeViewModel> roots)
        {
            var forks = new List<ResourceFork>();
            void Walk(NodeViewModel node)
            {
                if (node is ResourceTypeNode type)
                {
                    if (!forks.Any(f => ReferenceEquals(f, type.Fork))) forks.Add(type.Fork);
                    return;
                }
                foreach (var child in node.Children) Walk(child);
            }
            foreach (var root in roots) Walk(root);
            var system = forks.Where(f => Enumerable.Range(0, 3).Any(id => f.Find(Cicn, (short)id) is not null || f.Find(Icon, (short)id) is not null)).ToList();
            var generic = forks.Where(f => f.Find(IconList, FinderIconResolver.GenericDocumentId) is not null
                || f.Find(Icns, FinderIconResolver.GenericDocumentId) is not null || f.Find(Isrv, 128) is not null).ToList();
            var withFonts = forks.Where(f => f.OfType(Fond).Any() || f.OfType(Font).Any()).ToList();
            if (withFonts.Count == 0) return new(system, null) { GenericIconForks = generic };
            // Mac OS 9's system font is Charcoal; without it, Chicago (family 0) as earlier systems.
            var charcoal = withFonts.SelectMany(f => f.OfType(Fond)).FirstOrDefault(r => r.Name?.ToMacRoman() == "Charcoal");
            var library = new FontLibrary { SystemFontId = charcoal?.Id ?? 0 };
            foreach (var fork in withFonts)
            {
                foreach (var r in fork.OfType(Fond)) library.AddFamily(r.Id, r.Name?.ToMacRoman(), Data(r, fork));
                foreach (var r in fork.OfType(Nfnt)) library.AddNfnt(r.Id, Data(r, fork));
                foreach (var r in fork.OfType(Font)) library.AddFont(r.Id, Data(r, fork), r.Name?.ToMacRoman());
            }
            return new(system, library) { GenericIconForks = generic };
        }

        private static byte[] Data(Resource resource, ResourceFork fork) => ResourceDecompression.Default.GetData(resource, fork).ToArray();
    }

    /// <summary>Builds the previews of interface resources from their fork.</summary>
    internal static class InterfacePreviews
    {
        /// <summary>
        /// The preview of a <c>'DLOG'</c>, <c>'ALRT'</c> (as <c>Alert</c> shows it) or <c>'DITL'</c>, drawn at the options'
        /// screen depth with the fonts and system icons <paramref name="sources"/> supply; null for other types.
        /// </summary>
        public static DialogPreview? Dialog(Resource resource, ReadOnlyMemory<byte> data, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics, DialogSources? sources = null)
        {
            sources ??= DialogSources.None;
            if (DialogDrawings.Read(resource, data, fork, options, readOptions, diagnostics, DialogKind.Alert, sources.SystemForks) is not { } drawing)
                return null;
            var rendering = DialogRenderer.Render(drawing, new DialogRenderOptions
            {
                ScreenDepth = options.ScreenDepth, Fonts = sources.Fonts, TextFallback = SystemTextFallback.Instance,
            });
            var bitmap = rendering.Bitmap;
            return new DialogPreview(drawing, PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels), bitmap.Width, bitmap.Height);
        }
    }
}
