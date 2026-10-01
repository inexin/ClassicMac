using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// An icon's suite drawn as the Finder draws it (the Icon Utilities, through the QuickDraw renderer): each size the
    /// suite has, plain, selected, disabled, offline and open, and at 32 × 32 in the seven label colours, on white at the
    /// preview's screen depth.
    /// </summary>
    internal static class FinderIcons
    {
        private static readonly HashSet<string> SuiteTypes = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8", "icns"];

        private static readonly (IconTransform Transform, string Name)[] States =
        [
            (IconTransform.None, "plain"), (IconTransform.Selected, "selected"), (IconTransform.Disabled, "disabled"),
            (IconTransform.Offline, "offline"), (IconTransform.Open, "open"),
        ];

        public static bool Applies(Resource resource) => SuiteTypes.Contains(resource.Type.ToString());

        /// <summary>The suite of <paramref name="resource"/>'s ID drawn in each size, or nothing when it has no 1-bit member.</summary>
        public static IReadOnlyList<PreviewImage> Draw(Resource resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
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
            else suite = IconSuite.FromResources(Lookup, resource.Id);

            var quickDraw = new QuickDrawOptions
            {
                ScreenDepth = options.ScreenDepth,
                Version = options.QuickDraw == ResourceManagerModel.Rom68k ? QuickDrawVersion.MacRom : QuickDrawVersion.MacOS9,
            };
            var sizes = new List<(int W, int H, string Member)> { (48, 48, "ich#"), (32, 32, "ICN#"), (16, 16, "ics#"), (16, 12, "icm#") }
                .Where(s => suite.Members.ContainsKey(s.Member)).ToList();
            var images = new List<PreviewImage>();
            foreach (var (w, h, _) in sizes)
            {
                if (Strip(suite, quickDraw, w, h, States.Select(s => s.Transform).ToList()) is { } strip)
                    images.Add(Image(strip, string.Create(CultureInfo.InvariantCulture, $"Finder {w} × {h}: {string.Join(", ", States.Select(s => s.Name))}")));
            }
            if (sizes.Any(s => s.W == 32) && Strip(suite, quickDraw, 32, 32, Enumerable.Range(1, 7).Select(l => (IconTransform)(l << 8)).ToList()) is { } labels)
                images.Add(Image(labels, "Finder labels 1–7"));
            return images;
        }

        // The suite drawn in a row of w x h rects, one per transform, 8 pixels apart on white.
        private static RgbaBitmap? Strip(IconSuite suite, QuickDrawOptions options, int w, int h, IReadOnlyList<IconTransform> transforms)
        {
            const int Gap = 8;
            var canvas = new RgbaBitmap(Gap + transforms.Count * (w + Gap), h + 2 * Gap);
            var port = new QuickDrawPort(canvas, options);
            port.EraseRect(port.PortRect);
            for (var i = 0; i < transforms.Count; i++)
            {
                var left = Gap + i * (w + Gap);
                if (!suite.Plot(port, new MacRect(Gap, (short)left, (short)(Gap + h), (short)(left + w)), IconAlignment.None, transforms[i])) return null;
            }
            return canvas;
        }

        private static PreviewImage Image(RgbaBitmap bitmap, string caption) =>
            new(PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels), bitmap.Width, bitmap.Height, caption);
    }
}
