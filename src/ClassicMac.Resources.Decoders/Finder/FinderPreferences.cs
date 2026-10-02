using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>
    /// The Finder's global view settings from the <c>Finder Preferences</c> file's <c>'fvl8'</c> 128
    /// (docs/formats/file-systems/finder-windows.md §1.5): the views font and size, and the grid spacing.
    /// </summary>
    /// <param name="ViewsFontId">The views font's family ID.</param>
    /// <param name="ViewsFontSize">The views font's size.</param>
    public sealed record FinderPreferences(int ViewsFontId, int ViewsFontSize)
    {
        /// <summary>The resource that holds them: <c>'fvl8'</c> 128.</summary>
        public static readonly FourCC ResourceType = FourCC.FromString("fvl8");

        /// <summary>The resource's ID.</summary>
        public const short ResourceId = 128;

        /// <summary>Mac OS 9's defaults: Geneva (3) 10, grid percentages 200, 120, 200, 120.</summary>
        public static FinderPreferences Default { get; } = new(3, 10);

        /// <summary>The four grid percentages at +$24 (uninterpreted).</summary>
        public IReadOnlyList<int> GridSpacing { get; init; } = [200, 120, 200, 120];

        /// <summary>
        /// Reads <c>'fvl8'</c>: the font at +$18 and the size at +$1C (32-bit each), the grid at +$24 (four 16-bit
        /// percentages) when present. Null when shorter than +$20, or when the font is not a family ID (0–32767) or the
        /// size is not 1–127.
        /// </summary>
        public static FinderPreferences? Read(ReadOnlyMemory<byte> data)
        {
            if (data.Length < 0x20)
            {
                return null;
            }

            var reader = new BigEndianReader(data);
            int font = reader.ReadInt32At(0x18), size = reader.ReadInt32At(0x1C);
            if (font is < 0 or > short.MaxValue || size is < 1 or > 127)
            {
                return null;
            }

            var preferences = new FinderPreferences(font, size);
            if (data.Length < 0x2C)
            {
                return preferences;
            }

            return preferences with
            {
                GridSpacing = [reader.ReadInt16At(0x24), reader.ReadInt16At(0x26), reader.ReadInt16At(0x28), reader.ReadInt16At(0x2A)],
            };
        }

        /// <summary>The preferences in <paramref name="fork"/>'s <c>'fvl8'</c> 128; null when it has none or it is not usable.</summary>
        public static FinderPreferences? FromFork(ResourceFork fork, ReadOptions? readOptions = null)
        {
            ArgumentNullException.ThrowIfNull(fork);
            return fork.Find(ResourceType, ResourceId) is { } resource
                ? Read(ResourceDecompression.Default.GetData(resource, fork, readOptions))
                : null;
        }
    }
}
