using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Export;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// A dialog, alert or item list as the preview draws it: the window's kind and size, and its items in the window's
    /// coordinates, with each control's template and each icon's or picture's image.
    /// </summary>
    /// <param name="Title">The window's title (none for an alert or a lone item list).</param>
    /// <param name="Definition">The window definition ID (1, a modal dialog box, for an alert).</param>
    /// <param name="Width">The content's width.</param>
    /// <param name="Height">The content's height.</param>
    /// <param name="Items">The items.</param>
    /// <param name="DefaultItem">The item drawn with the default ring (an alert's stage 1 default), or 0.</param>
    /// <param name="Background">The content colour from the <c>'dctb'</c> or <c>'actb'</c> of the same ID, or null for white.</param>
    public sealed record DialogPreview(string Title, int Definition, int Width, int Height, IReadOnlyList<DialogPreviewItem> Items, int DefaultItem,
        Resources.Decoders.Documents.Rgb? Background = null);

    /// <summary>A dialog item to draw.</summary>
    /// <param name="Item">The item as read.</param>
    /// <param name="Control">A control item's template, when its <c>'CNTL'</c> exists.</param>
    /// <param name="Png">An icon's or picture's image, when it exists and can be drawn.</param>
    public sealed record DialogPreviewItem(DialogItem Item, ControlTemplate? Control, byte[]? Png);

    /// <summary>Builds the previews of interface resources from their fork.</summary>
    internal static class InterfacePreviews
    {
        private static readonly FourCC Ditl = FourCC.FromString("DITL"), Cntl = FourCC.FromString("CNTL"), Icon = FourCC.FromString("ICON"),
            Cicn = FourCC.FromString("cicn"), Pict = FourCC.FromString("PICT");

        public static DialogPreview? Dialog(Resource resource, ReadOnlyMemory<byte> data, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics)
        {
            var what = resource.ToString();
            switch (resource.Type.ToString())
            {
                case "DLOG":
                {
                    var dialog = InterfaceResources.ReadWindow(data, true, options, diagnostics, what);
                    var items = Items(dialog.ItemsId ?? 0, fork, options, readOptions, diagnostics);
                    return new DialogPreview(dialog.Title, dialog.Definition, Width(dialog.Bounds), Height(dialog.Bounds), items, 0,
                        Content(fork.Find(FourCC.FromString("dctb"), resource.Id), fork, readOptions, diagnostics));
                }
                case "ALRT":
                {
                    var alert = InterfaceResources.ReadAlert(data, diagnostics, what);
                    var items = Items(alert.ItemsId, fork, options, readOptions, diagnostics);
                    return new DialogPreview("", 1, Width(alert.Bounds), Height(alert.Bounds), items, alert.Stage(1).BoldItem,
                        Content(fork.Find(FourCC.FromString("actb"), resource.Id), fork, readOptions, diagnostics));
                }
                case "DITL":
                {
                    var items = Build(InterfaceResources.ReadDialogItems(data, options, diagnostics, what), fork, options, readOptions, diagnostics);
                    // A lone item list: a plain box around its items, with a margin.
                    var right = items.Select(i => (int)i.Item.Bounds.Right).DefaultIfEmpty(100).Max() + 10;
                    var bottom = items.Select(i => (int)i.Item.Bounds.Bottom).DefaultIfEmpty(40).Max() + 10;
                    return new DialogPreview("", 2, right, bottom, items, 0);
                }
                default:
                    return null;
            }
        }

        // A window colour table's content colour (part 0), if it has one.
        private static Resources.Decoders.Documents.Rgb? Content(Resource? table, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            if (table is null) return null;
            var data = Data(table, fork, readOptions, diagnostics);
            if (data.Length < 8) return null;
            var reader = new BigEndianReader(data);
            var count = reader.ReadInt16At(6) + 1;
            for (var i = 0; i < count && 16 + i * 8 <= data.Length; i++)
            {
                var e = 8 + i * 8;
                if (reader.ReadInt16At(e) == 0) return new(data.Span[e + 2], data.Span[e + 4], data.Span[e + 6]);
            }
            return null;
        }

        private static int Width(MacRect r) => Math.Clamp(r.Right - r.Left, 1, 4096);

        private static int Height(MacRect r) => Math.Clamp(r.Bottom - r.Top, 1, 4096);

        private static IReadOnlyList<DialogPreviewItem> Items(short id, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics) =>
            fork.Find(Ditl, id) is { } list
                ? Build(InterfaceResources.ReadDialogItems(Data(list, fork, readOptions, diagnostics), options, diagnostics, list.ToString()),
                    fork, options, readOptions, diagnostics)
                : [];

        private static List<DialogPreviewItem> Build(IReadOnlyList<DialogItem> items, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics) =>
            items.Select(item =>
            {
                ControlTemplate? control = null;
                byte[]? png = null;
                if (item.ResourceId is { } id)
                {
                    switch (item.Type)
                    {
                        case 7 when fork.Find(Cntl, id) is { } c:
                            control = InterfaceResources.ReadControl(Data(c, fork, readOptions, diagnostics), options, diagnostics, c.ToString());
                            break;
                        case 32:
                            // A colour icon of the same ID is used first.
                            png = Image(fork.Find(Cicn, id) ?? fork.Find(Icon, id), fork, options, readOptions, diagnostics);
                            break;
                        case 64:
                            png = Image(fork.Find(Pict, id), fork, options, readOptions, diagnostics);
                            break;
                    }
                }
                return new DialogPreviewItem(item, control, png);
            }).ToList();

        private static byte[]? Image(Resource? resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            if (resource is null) return null;
            var decoder = ResourceDecoders.Create(options).FirstOrDefault(d => d.CanDecode(resource.Type));
            var files = decoder?.Decode(new DecodeInput(resource, Data(resource, fork, readOptions, diagnostics), fork, readOptions, diagnostics)) ?? [];
            return files.FirstOrDefault(f => f.Extension.EndsWith(".png", StringComparison.Ordinal))?.Content.ToArray();
        }

        private static ReadOnlyMemory<byte> Data(Resource resource, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics) =>
            ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
    }
}
