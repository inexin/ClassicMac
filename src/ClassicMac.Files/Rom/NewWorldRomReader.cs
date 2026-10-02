using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Compression;

namespace ClassicMac.Files.Rom
{
    /// <summary>
    /// The NewWorld "Mac OS ROM" file (type <c>tbxi</c>, in the System Folder of NewWorld machines): an Open Firmware
    /// <c>&lt;CHRP-BOOT&gt;</c> script whose <c>lzss-offset</c> and <c>lzss-size</c> constants locate the ROM image,
    /// compressed with Okumura's LZSS (<see cref="Lzss"/>). The result is one file whose data fork is the expanded image,
    /// for <see cref="MacRomReader"/> to open next. Later files that carry "parcels" instead of an LZSS image are not
    /// recognised. Layout and rules: docs/formats/disk-images/rom.md.
    /// </summary>
    public sealed partial class NewWorldRomReader : IContainerReader
    {
        private const int ScriptSearchLength = 64 * 1024;
        private static readonly byte[] Signature = "<CHRP-BOOT>"u8.ToArray();

        /// <summary>The reader.</summary>
        public static NewWorldRomReader Instance { get; } = new();

        private NewWorldRomReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "NewWorld ROM";

        /// <summary>The file starts with <c>&lt;CHRP-BOOT&gt;</c> and its script locates an LZSS image inside the file.</summary>
        public bool CanRead(ForkData input) => TryLocate(input, out _, out _);

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(context);
            if (!TryLocate(input, out var offset, out var size))
            {
                throw new InvalidDataException("Not a NewWorld Mac OS ROM file with an LZSS image.");
            }

            var image = Lzss.Decompress(input.Slice(offset, size).ToArray(), context.Options.MaxExpandedBytesPerInput);
            var name = image.Length >= 10
                ? $"ROM ${new BigEndianReader(image).ReadUInt16At(8):X4}"
                : "ROM image";
            return [new MacFile { Name = MacString.FromMacRoman(name), DataFork = ForkData.FromBytes(image) }];
        }

        // The constants in the boot script: "h# 010DB0 constant lzss-offset" and "h# 1CA2E2 constant lzss-size".
        private static bool TryLocate(ForkData input, out long offset, out long size)
        {
            offset = size = 0;
            if (!input.ReadPrefix(Signature.Length).AsSpan().SequenceEqual(Signature))
            {
                return false;
            }

            var prefix = input.ReadPrefix((int)Math.Min(input.Length, ScriptSearchLength));
            var script = Encoding.Latin1.GetString(prefix);
            var end = script.IndexOf("</CHRP-BOOT>", StringComparison.Ordinal);
            if (end >= 0)
            {
                script = script[..end];
            }

            var offsetMatch = OffsetConstant().Match(script);
            var sizeMatch = SizeConstant().Match(script);
            if (!offsetMatch.Success || !sizeMatch.Success)
            {
                return false;
            }

            if (!long.TryParse(offsetMatch.Groups[1].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset) ||
                !long.TryParse(sizeMatch.Groups[1].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size))
            {
                return false;
            }

            return size > 0 && offset >= 0 && offset <= input.Length - size;
        }

        [GeneratedRegex(@"h#\s+([0-9A-Fa-f]{1,8})\s+constant\s+lzss-offset\b")]
        private static partial Regex OffsetConstant();

        [GeneratedRegex(@"h#\s+([0-9A-Fa-f]{1,8})\s+constant\s+lzss-size\b")]
        private static partial Regex SizeConstant();
    }
}
