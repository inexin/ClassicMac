using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    /// <summary>
    /// The tree's 16 × 16 pixel icons, one per kind of node (design/boards/browse-tree.md, T4): Finder-like, each with a
    /// black outline, drawn for ClassicMac (not Apple's art).
    /// </summary>
    internal static class TreeIcons
    {
        public const int Size = 16;

        private static readonly Dictionary<char, uint> Palette = new()
        {
            ['.'] = 0x00000000, ['K'] = 0xFF000000, ['W'] = 0xFFFFFFFF, ['G'] = 0xFFBBBBBB, ['D'] = 0xFF555555,
            ['g'] = 0xFF22B14C, ['T'] = 0xFFD9B98C, ['S'] = 0xFF7A4A1E, ['L'] = 0xFFC9C0F2, ['P'] = 0xFF8A62C8,
            ['B'] = 0xFF2E5CD6, ['b'] = 0xFF9DB6F2,
        };

        /// <summary>The art of each kind, row by row; '.' is transparent (see <see cref="Palette"/>).</summary>
        public static IReadOnlyDictionary<TreeIconKind, string[]> Art { get; } = new Dictionary<TreeIconKind, string[]>
        {
            // A hard disk: grey body, a green light.
            [TreeIconKind.HardDisk] =
            [
                "................",
                "................",
                "................",
                "................",
                ".KKKKKKKKKKKKKK.",
                "KWWWWWWWWWWWWWWK",
                "KWGGGGGGGGGGGGDK",
                "KWGGGGGGGGGGGGDK",
                "KWGGGGGGGGGggGDK",
                "KWGGGGGGGGGGGGDK",
                "KWGGGGGGGGGGGGDK",
                "KDDDDDDDDDDDDDDK",
                ".KKKKKKKKKKKKKK.",
                "..KK........KK..",
                "................",
                "................",
            ],
            // A floppy disk: dark body, the metal shutter, a white label.
            [TreeIconKind.Floppy] =
            [
                "................",
                "KKKKKKKKKKKKKKK.",
                "KDDDGGGGGGGDDDDK",
                "KDDDGGGDDGGDDDDK",
                "KDDDGGGDDGGDDDDK",
                "KDDDGGGGGGGDDDDK",
                "KDDDDDDDDDDDDDDK",
                "KDDWWWWWWWWWWDDK",
                "KDDWDDDDDDDDWDDK",
                "KDDWWWWWWWWWWDDK",
                "KDDWDDDDDDWWWDDK",
                "KDDWWWWWWWWWWDDK",
                "KDDWWWWWWWWWWDDK",
                "KDDWWWWWWWWWWDDK",
                "KKKKKKKKKKKKKKKK",
                "................",
            ],
            // A parcel: a tan box tied with string.
            [TreeIconKind.Parcel] =
            [
                "................",
                ".....SS..SS.....",
                "......SSSS......",
                ".KKKKKKSSKKKKKK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KSSSSSSSSSSSSK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KTTTTTSSTTTTTK.",
                ".KKKKKKKKKKKKKK.",
                "................",
            ],
            // A folder with its tab, lavender.
            [TreeIconKind.Folder] =
            [
                "................",
                "................",
                "..KKKKK.........",
                ".KLLLLLK........",
                "KKKKKKKKKKKKKKK.",
                "KWWWWWWWWWWWWLK.",
                "KWLLLLLLLLLLLLK.",
                "KWLLLLLLLLLLLLK.",
                "KWLLLLLLLLLLLLK.",
                "KWLLLLLLLLLLLLK.",
                "KWLLLLLLLLLLLLK.",
                "KWLLLLLLLLLLLLK.",
                "KLLLLLLLLLLLLLK.",
                "KKKKKKKKKKKKKKK.",
                "................",
                "................",
            ],
            // An application: a purple diamond.
            [TreeIconKind.Application] =
            [
                ".......KK.......",
                "......KWPK......",
                ".....KWPPPK.....",
                "....KWPPPPPK....",
                "...KWPPPPPPPK...",
                "..KWPPPPPPPPPK..",
                ".KWPPPPPPPPPPPK.",
                "KWPPPPPPPPPPPPPK",
                "KPPPPPPPPPPPPPPK",
                ".KPPPPPPPPPPPPK.",
                "..KPPPPPPPPPPK..",
                "...KPPPPPPPPK...",
                "....KPPPPPPK....",
                ".....KPPPPK.....",
                "......KPPK......",
                ".......KK.......",
            ],
            // A document: a page with a folded corner and lines of text.
            [TreeIconKind.Document] =
            [
                "..KKKKKKKK......",
                "..KWWWWWWKK.....",
                "..KWWWWWWKWK....",
                "..KWWWWWWKWWK...",
                "..KWWWWWWKKKKK..",
                "..KWWWWWWWWWWK..",
                "..KWDDDDDDDWWK..",
                "..KWWWWWWWWWWK..",
                "..KWDDDDDDDDWK..",
                "..KWWWWWWWWWWK..",
                "..KWDDDDDDDDWK..",
                "..KWWWWWWWWWWK..",
                "..KWDDDDDWWWWK..",
                "..KWWWWWWWWWWK..",
                "..KWWWWWWWWWWK..",
                "..KKKKKKKKKKKK..",
            ],
            // A "No name" group: two pages with folded corners, the back one up and to the right.
            [TreeIconKind.NoNameGroup] =
            [
                ".....KKKKKK.....",
                ".....KWWWWKK....",
                ".....KWWWWKWK...",
                ".....KWWWWKKKK..",
                "..KKKKKKWWWWWK..",
                "..KWWWWKKWWWWK..",
                "..KWWWWKWKWWWK..",
                "..KWWWWKKKKWWK..",
                "..KWWWWWWWKWWK..",
                "..KWDDDDDWKWWK..",
                "..KWWWWWWWKWWK..",
                "..KWDDDDDWKKKK..",
                "..KWWWWWWWK.....",
                "..KWDDDWWWK.....",
                "..KWWWWWWWK.....",
                "..KKKKKKKKK.....",
            ],
            // A resource type: two stacked cards with blue lines.
            [TreeIconKind.ResourceType] =
            [
                "................",
                "....KKKKKKKKKKK.",
                "....KWWWWWWWWWK.",
                "....KWbbbbbbbWK.",
                "..KKKKKKKKKKKWK.",
                "..KWWWWWWWWWKWK.",
                "..KWBBBBBBBWKWK.",
                "..KWWWWWWWWWKKK.",
                "..KWBBBBBWWWK...",
                "..KWWWWWWWWWK...",
                "..KWBBBBBBBWK...",
                "..KWWWWWWWWWK...",
                "..KWBBBBWWWWK...",
                "..KWWWWWWWWWK...",
                "..KKKKKKKKKKK...",
                "................",
            ],
            // A resource: a card with a blue edge and a 2 × 3 grid of black bytes.
            [TreeIconKind.Resource] =
            [
                "................",
                "................",
                "...KKKKKKKKKK...",
                "...KBWWWWWWWK...",
                "...KBWKKWKKWK...",
                "...KBWKKWKKWK...",
                "...KBWWWWWWWK...",
                "...KBWKKWKKWK...",
                "...KBWKKWKKWK...",
                "...KBWWWWWWWK...",
                "...KBWKKWKKWK...",
                "...KBWKKWKKWK...",
                "...KBWWWWWWWK...",
                "...KKKKKKKKKK...",
                "................",
                "................",
            ],
        };

        private static readonly Dictionary<TreeIconKind, Bitmap> Bitmaps = [];

        /// <summary>The art's pixels as ARGB words, row by row.</summary>
        public static uint[] Pixels(TreeIconKind kind)
        {
            var art = Art[kind];
            if (art.Length != Size) throw new InvalidOperationException($"{kind}: {art.Length} rows");
            var pixels = new uint[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                if (art[y].Length != Size) throw new InvalidOperationException($"{kind}: row {y} has {art[y].Length} pixels");
                for (int x = 0; x < Size; x++) pixels[y * Size + x] = Palette[art[y][x]];
            }
            return pixels;
        }

        /// <summary>A node kind's icon (a diagnostics group's header): the kind icon its rows would show.</summary>
        public static IValueConverter KindConverter { get; } = new FuncValueConverter<NodeKind, Bitmap?>(kind => For(kind switch
        {
            NodeKind.Input => TreeIconKind.HardDisk,
            NodeKind.Container => TreeIconKind.Floppy,
            NodeKind.File => TreeIconKind.Document,
            NodeKind.ResourceType => TreeIconKind.ResourceType,
            NodeKind.Resource => TreeIconKind.Resource,
            NodeKind.NoNameGroup => TreeIconKind.NoNameGroup,
            _ => TreeIconKind.Folder,
        }));

        /// <summary>The kind's icon as a bitmap (made once, on the UI thread); none for the loading placeholder.</summary>
        public static Bitmap? For(TreeIconKind kind)
        {
            if (!Art.ContainsKey(kind)) return null;
            if (Bitmaps.TryGetValue(kind, out var bitmap)) return bitmap;
            var writeable = new WriteableBitmap(new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            var pixels = Pixels(kind);
            using (var locked = writeable.Lock())
                for (int y = 0; y < Size; y++)
                    Marshal.Copy((int[])(object)pixels, y * Size, locked.Address + y * locked.RowBytes, Size);
            return Bitmaps[kind] = writeable;
        }
    }

    /// <summary>
    /// A tree row's 16 DIP icon slot: the node's own icon once resolved, else its kind's, drawn 1:1 in device pixels
    /// (1 device pixel per Mac pixel at 100%, 125% and 150%, centred; 2 at 200%; never 1.25 or 1.5: design/TOKENS.md).
    /// When the row first shows on screen it asks the node to resolve its own icon; rows never scrolled into view never do.
    /// </summary>
    internal sealed class TreeIcon : PixelControl
    {
        public static readonly StyledProperty<NodeViewModel?> NodeProperty = AvaloniaProperty.Register<TreeIcon, NodeViewModel?>(nameof(Node));

        private NodeViewModel? watched;
        private Bitmap? own;
        private bool watchingViewport;

        static TreeIcon() => AffectsRender<TreeIcon>(NodeProperty);

        public NodeViewModel? Node
        {
            get => GetValue(NodeProperty);
            set => SetValue(NodeProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != NodeProperty) return;
            if (watched is not null) watched.PropertyChanged -= OnNodeChanged;
            watched = Node;
            if (watched is not null) watched.PropertyChanged += OnNodeChanged;
            LoadOwn();
            WatchViewport(watched is not null && watched.IconPng is null);
        }

        private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NodeViewModel.IconPng)) return;
            LoadOwn();
            InvalidateVisual();
        }

        private void LoadOwn()
        {
            own?.Dispose();
            own = watched?.IconPng is { } png ? new Bitmap(new MemoryStream(png)) : null;
        }

        private void WatchViewport(bool watch)
        {
            if (watch == watchingViewport) return;
            watchingViewport = watch;
            if (watch) EffectiveViewportChanged += OnViewportChanged;
            else EffectiveViewportChanged -= OnViewportChanged;
        }

        // On screen: resolve the node's own icon (once; the node keeps it).
        private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            if (Node is not { } node || !IsEffectivelyVisible || Bounds.Width <= 0) return;
            if (!e.EffectiveViewport.Intersects(new Rect(Bounds.Size))) return;
            WatchViewport(false);
            _ = node.RequestIconAsync();
        }

        protected override Size MeasureOverride(Size availableSize) => new(TreeIcons.Size, TreeIcons.Size);

        public override void Render(DrawingContext context)
        {
            var bitmap = own ?? (Node is { } node ? TreeIcons.For(node.IconKind) : null);
            if (bitmap is null) return;
            var scaling = RenderScaling;
            var k = PixelScaling.DevicePixels(1, scaling);
            var slot = (int)Math.Round(TreeIcons.Size * scaling);
            int w = bitmap.PixelSize.Width * k, h = bitmap.PixelSize.Height * k;
            using var snap = PushSnap(context);
            context.DrawImage(bitmap, new Rect((slot - w) / 2 / scaling, (slot - h) / 2 / scaling, w / scaling, h / scaling));
        }
    }
}
