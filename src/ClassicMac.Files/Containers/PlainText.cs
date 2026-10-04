using System;
using System.Buffers;

namespace ClassicMac.Files.Containers;

// What may precede a BinHex marker or a uuencode begin line for the input to be recognised as that format: mail or
// news text. docs/formats/containers/binhex.md §5.
internal static class PlainText
{
    // The control characters text does not hold: all but tab, line feed, vertical tab, form feed and return. Bytes
    // from $20 up, $7F and 8-bit characters included, are text (8-bit mail, Mac OS Roman).
    private static readonly SearchValues<byte> Binary = SearchValues.Create(
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0E, 0x0F,
         0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F]);

    // Whether the bytes are text: they hold none of those control characters. A file system's or disk image's
    // structures (boot blocks, a master directory block, a partition map) hold zero bytes.
    public static bool IsText(ReadOnlySpan<byte> bytes) => bytes.IndexOfAny(Binary) < 0;
}
