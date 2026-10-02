using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// An icon's family for the image preview (boards/main-window.md, P1): the members of its ID, their 1-bit masks, and
    /// the suite drawn as the Finder draws it (the Icon Utilities, through the QuickDraw renderer) in the five states and
    /// the seven label colours, each on its own transparent canvas at the preview's screen depth.
    /// </summary>
    internal static class FinderIcons
    {
        /// <summary>The members of an icon family, largest first, in the order the preview shows them.</summary>
        public static IReadOnlyList<string> MemberTypes { get; } = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8"];

        private static readonly HashSet<string> SuiteTypes = [.. MemberTypes, "icns"];

        private static readonly (IconTransform Transform, string Name)[] States =
        [
            (IconTransform.None, "Normal"), (IconTransform.Selected, "Selected"), (IconTransform.Disabled, "Disabled"),
            (IconTransform.Offline, "Offline"), (IconTransform.Open, "Open"),
            ((IconTransform)(1 << 8), "Essential"), ((IconTransform)(2 << 8), "Hot"), ((IconTransform)(3 << 8), "In Progress"),
            ((IconTransform)(4 << 8), "Cool"), ((IconTransform)(5 << 8), "Personal"), ((IconTransform)(6 << 8), "Project 1"),
            ((IconTransform)(7 << 8), "Project 2"),
        ];

        // The 1-bit members with a mask after the image: their size.
        private static readonly Dictionary<string, (int Width, int Height)> Masked = new()
        {
            ["ICN#"] = (32, 32),
            ["ics#"] = (16, 16),
            ["icm#"] = (16, 12),
        };

        public static bool Applies(Resource resource) => SuiteTypes.Contains(resource.Type.ToString());

        /// <summary>
        /// The suite of <paramref name="resource"/>'s ID in the five Finder states and the seven labels, at 32 × 32 when it has
        /// a large member, else its largest; nothing when it has no 1-bit member.
        /// </summary>
        public static IReadOnlyList<PreviewImage> FinderStates(Resource resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics)
        {
            ReadOnlyMemory<byte>? Lookup(FourCC type, short id) =>
                fork.Find(type, id) is { } r ? ResourceDecompression.Default.GetData(r, fork, readOptions, diagnostics) : null;
            IconSuite suite;
            if (resource.Type.ToString() == "icns")
            {
                try
                {
                    suite = IconSuite.FromFamily(IconFamily.ReadIcns(Lookup(resource.Type, resource.Id)!.Value, diagnostics));
                }
                catch (System.IO.InvalidDataException)
                {
                    return [];
                }
            }
            else
            {
                suite = IconSuite.FromResources(Lookup, resource.Id);
            }

            var quickDraw = new QuickDrawOptions
            {
                ScreenDepth = options.ScreenDepth,
                Version = options.QuickDraw == ResourceManagerModel.Rom68k ? QuickDrawVersion.MacRom : QuickDrawVersion.MacOS9,
            };
            var sizes = new List<(int W, int H, string Member)> { (32, 32, "ICN#"), (48, 48, "ich#"), (16, 16, "ics#"), (16, 12, "icm#") }
                .Where(s => suite.Members.ContainsKey(s.Member)).ToList();
            if (sizes.Count == 0)
            {
                return [];
            }

            var (w, h, _) = sizes[0];
            var images = new List<PreviewImage>();
            foreach (var (transform, name) in States)
            {
                var canvas = new RgbaBitmap(w, h);
                var port = new QuickDrawPort(canvas, quickDraw);
                if (!suite.Plot(port, new MacRect(0, 0, (short)h, (short)w), IconAlignment.None, transform))
                {
                    return [];
                }

                images.Add(new PreviewImage(PngEncoder.Instance.Encode(w, h, canvas.Pixels), w, h, name));
            }

            return images;
        }

        /// <summary>The masks of the family's 1-bit members (the second half of an <c>ICN#</c>, <c>ics#</c> or <c>icm#</c>), black on white.</summary>
        public static IReadOnlyList<PreviewImage> Masks(Resource resource, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            var masks = new List<PreviewImage>();
            foreach (var (type, (w, h)) in Masked)
            {
                if (fork.Find(FourCC.FromString(type), resource.Id) is not { } member)
                {
                    continue;
                }

                var data = ResourceDecompression.Default.GetData(member, fork, readOptions, diagnostics).Span;
                var rowBytes = w / 8;
                var size = rowBytes * h;
                if (data.Length < 2 * size)
                {
                    continue;
                }

                var mask = data.Slice(size, size);
                var canvas = new RgbaBitmap(w, h);
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var on = (mask[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0;
                        canvas[x, y] = on ? new RgbaColor(0, 0, 0, 255) : new RgbaColor(255, 255, 255, 255);
                    }
                }

                masks.Add(new PreviewImage(PngEncoder.Instance.Encode(w, h, canvas.Pixels), w, h, null, $"'{type}' mask", $"{w}×{h} · 1-bit"));
            }

            return masks;
        }
    }
}
