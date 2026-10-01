using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Resources.Decoders.Images
{
    /// <summary>One of the 20 icon family members Mac OS 9's Icon Services knows.</summary>
    /// <param name="Type">The member's type, as in an <c>'icns'</c> element or a resource type.</param>
    /// <param name="Width">Its width in pixels.</param>
    /// <param name="Height">Its height in pixels.</param>
    /// <param name="Depth">Bits per pixel: 1 (the image and then the mask), 4 or 8 (the system colour tables), 32 (RGB), or 8 for an 8-bit mask.</param>
    /// <param name="IsMask">Whether it is an 8-bit mask (<c>s8mk</c>, <c>l8mk</c>, <c>h8mk</c>, <c>t8mk</c>): one alpha byte per pixel.</param>
    public sealed record IconMemberType(string Type, int Width, int Height, int Depth, bool IsMask)
    {
        /// <summary>The member's uncompressed size in bytes.</summary>
        public int RawSize => Depth == 1 ? 2 * Width * Height / 8 : Width * Height * Depth / 8;
    }

    /// <summary>
    /// An icon family as Mac OS 9's Icon Services holds it: up to 20 members (1-, 4-, 8- and 32-bit images at 16 × 12,
    /// 16 × 16, 32 × 32, 48 × 48 and 128 × 128, and 8-bit masks), read from an <c>'icns'</c> resource or from the classic
    /// icon resources of one ID. Members are kept uncompressed.
    /// </summary>
    public sealed class IconFamily
    {
        /// <summary>The members, in Icon Services' table order.</summary>
        public static IReadOnlyList<IconMemberType> MemberTypes { get; } =
        [
            new("icm#", 16, 12, 1, false), new("icm4", 16, 12, 4, false), new("icm8", 16, 12, 8, false),
            new("ics#", 16, 16, 1, false), new("ics4", 16, 16, 4, false), new("ics8", 16, 16, 8, false),
            new("is32", 16, 16, 32, false), new("s8mk", 16, 16, 8, true),
            new("ICN#", 32, 32, 1, false), new("icl4", 32, 32, 4, false), new("icl8", 32, 32, 8, false),
            new("il32", 32, 32, 32, false), new("l8mk", 32, 32, 8, true),
            new("ich#", 48, 48, 1, false), new("ich4", 48, 48, 4, false), new("ich8", 48, 48, 8, false),
            new("ih32", 48, 48, 32, false), new("h8mk", 48, 48, 8, true),
            new("it32", 128, 128, 32, false), new("t8mk", 128, 128, 8, true),
        ];

        // The family variants an 'icns' may nest ('tile', 'over', 'drop', 'open', 'odrp') and its own type.
        private static readonly string[] FamilyTypes = ["icns", "tile", "over", "drop", "open", "odrp"];

        // The classic resources Icon Services reads when there is no 'icns' of the ID.
        private static readonly string[] ClassicTypes = ["icm#", "icm4", "icm8", "ics#", "ics4", "ics8", "ICN#", "icl4", "icl8"];

        private readonly Dictionary<string, byte[]> members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IconFamily> variants = new(StringComparer.Ordinal);

        /// <summary>The members present, by type, uncompressed (32-bit members as ARGB with alpha 0).</summary>
        public IReadOnlyDictionary<string, byte[]> Members => members;

        /// <summary>Nested variant families of an <c>'icns'</c> (<c>tile</c>, <c>over</c>, <c>drop</c>, <c>open</c>, <c>odrp</c>).</summary>
        public IReadOnlyDictionary<string, IconFamily> Variants => variants;

        /// <summary>A member type by name, or null.</summary>
        public static IconMemberType? MemberType(string type) => MemberTypes.FirstOrDefault(t => t.Type == type);

        /// <summary>
        /// Reads an <c>'icns'</c> resource (or data-fork file) as Mac OS 9 does: the header's length must equal the data's
        /// (else the family is empty); elements may come in any order, the last of a type wins; unknown elements
        /// (<c>'TOC '</c>, <c>'info'</c>, …) are skipped; a 1-, 4- or 8-bit member or mask of the wrong size is dropped; a
        /// 32-bit member of its raw size is raw, else compressed.
        /// </summary>
        /// <exception cref="InvalidDataException">An element size under 1, or a compressed <c>it32</c> whose format word is not 0: the whole family fails.</exception>
        public static IconFamily ReadIcns(ReadOnlySpan<byte> data, ICollection<Diagnostic>? diagnostics = null)
        {
            var family = new IconFamily();
            if (data.Length < 8 || !FamilyTypes.Contains(new FourCC(data[..4]).ToString()))
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "icon.family-header", "The data is not an icon family ('icns')."));
                return family;
            }
            var reader = new BigEndianReader(data);
            var length = reader.ReadUInt32At(4);
            if (length < 9 || length != data.Length)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "icon.family-length",
                    $"The family says it is {length} bytes; there are {data.Length}. Mac OS 9 treats it as empty (noIconDataAvailableErr)."));
                return family;
            }
            long at = 8;
            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            while (at + 8 <= length)
            {
                var type = reader.ReadFourCCAt((int)at).ToString();
                var size = reader.ReadUInt32At((int)at + 4);
                if (size < 1) throw new InvalidDataException($"The icon family's '{type}' element has size {size}; the family fails (paramErr).");
                if (at + size > length) break;                   // an element past the end is skipped
                var payload = size >= 8 ? data.Slice((int)at + 8, (int)size - 8) : [];
                if (MemberType(type) is { } member) family.Set(member, payload, diagnostics);
                else if (FamilyTypes.Contains(type) && type != "icns") family.variants[type] = ReadIcns(data.Slice((int)at, (int)size), diagnostics);
                else ignored.Add(type);
                at += size;
            }
            if (ignored.Count > 0)
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Info, "icon.family-ignored",
                    $"Elements Mac OS 9 does not know were skipped: {string.Join(", ", ignored.Select(t => $"'{t}'"))}."));
            return family;
        }

        /// <summary>
        /// The family of a resource ID as Icon Services builds it: the <c>'icns'</c> of that ID alone when there is one,
        /// else the classic icon resources of that ID (<c>icm#</c>/<c>4</c>/<c>8</c>, <c>ics#</c>/<c>4</c>/<c>8</c>,
        /// <c>ICN#</c>, <c>icl4</c>, <c>icl8</c>), each only at its exact size.
        /// </summary>
        public static IconFamily FromResources(Func<FourCC, short, ReadOnlyMemory<byte>?> lookup, short id, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(lookup);
            if (lookup(FourCC.FromString("icns"), id) is { Length: > 0 } icns) return ReadIcns(icns.Span, diagnostics);
            var family = new IconFamily();
            foreach (var type in ClassicTypes)
                if (lookup(FourCC.FromString(type), id) is { Length: > 0 } data) family.Set(MemberType(type)!, data.Span, diagnostics);
            return family;
        }

        private void Set(IconMemberType member, ReadOnlySpan<byte> payload, ICollection<Diagnostic>? diagnostics)
        {
            if (member.Depth == 32)
            {
                if (payload.Length == member.RawSize)
                {
                    members[member.Type] = payload.ToArray();
                    return;
                }
                if (member.Type == "it32")
                {
                    // it32 alone carries a compression-format word, which must be 0.
                    if (!new BigEndianReader(payload).TryReadUInt32(out var format) || format != 0)
                        throw new InvalidDataException("The 'it32' member's compression format is not 0; the family fails (paramErr).");
                    payload = payload[4..];
                }
                members[member.Type] = Decompress(payload, member.Width * member.Height);
                return;
            }
            if (payload.Length != member.RawSize)
            {
                members.Remove(member.Type);
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Info, "icon.member-size",
                    $"The '{member.Type}' member is {payload.Length} bytes, not {member.RawSize}; Mac OS 9 drops it."));
                return;
            }
            members[member.Type] = payload.ToArray();
        }

        // SetCompressedData: three planes (red, green, blue), each pixels bytes, into bytes 1-3 of each ARGB long (alpha
        // stays 0). A control byte under $80 copies that many plus one literal bytes; $80 and over repeats the next byte
        // (control - 125) times. Counts stop at the plane's end (the excess is skipped); short data leaves zeros.
        internal static byte[] Decompress(ReadOnlySpan<byte> data, int pixels)
        {
            var argb = new byte[pixels * 4];
            int at = 0;
            for (int plane = 1; plane <= 3; plane++)
            {
                int filled = 0;
                while (filled < pixels && at < data.Length)
                {
                    int control = data[at++];
                    if (control < 0x80)
                    {
                        int count = control + 1;
                        for (int i = 0; i < count && at < data.Length; i++, at++)
                            if (filled < pixels) argb[4 * filled++ + plane] = data[at];
                    }
                    else
                    {
                        if (at >= data.Length) break;
                        byte value = data[at++];
                        for (int i = control - 125; i > 0 && filled < pixels; i--) argb[4 * filled++ + plane] = value;
                    }
                }
            }
            return argb;
        }

        // The mask list Icon Services searches for a rect height: an 8-bit mask always wins over a 1-bit one, and the
        // mini size never takes an 8-bit mask.
        private static string[] MaskList(int height) => height switch
        {
            <= 12 => ["icm#", "ics#", "ICN#", "ich#"],
            <= 20 => ["s8mk", "l8mk", "h8mk", "ics#", "ICN#", "icm#", "ich#"],
            <= 40 => ["l8mk", "s8mk", "h8mk", "ICN#", "ics#", "icm#", "ich#"],
            <= 56 => ["h8mk", "l8mk", "s8mk", "ich#", "ICN#", "ics#", "icm#"],
            _ => ["t8mk", "h8mk", "l8mk", "s8mk", "ich#", "ICN#", "ics#", "icm#"],
        };

        /// <summary>The mask Icon Services uses at a rect of the given height, or null when the family has none.</summary>
        public string? MaskFor(int height) => MaskList(height).FirstOrDefault(members.ContainsKey);

        /// <summary>A member's pixels, opaque: 1-bit black on white, 4- and 8-bit through the system colour tables, 32-bit RGB.</summary>
        /// <returns>Null when the family lacks the member or it is a mask.</returns>
        public RgbaBitmap? Image(string type)
        {
            if (MemberType(type) is not { IsMask: false } member || !members.TryGetValue(type, out var data)) return null;
            var bitmap = new RgbaBitmap(member.Width, member.Height);
            var palette = member.Depth is 4 or 8 ? StandardColorTables.ForId(member.Depth)! : null;
            for (int y = 0; y < member.Height; y++)
                for (int x = 0; x < member.Width; x++)
                {
                    int i = y * member.Width + x;
                    RgbaColor c = member.Depth switch
                    {
                        1 => ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0 ? new(0, 0, 0) : new(255, 255, 255),
                        4 => palette![(data[i >> 1] >> ((i & 1) == 0 ? 4 : 0)) & 15],
                        8 => palette![data[i]],
                        _ => new(data[4 * i + 1], data[4 * i + 2], data[4 * i + 3]),
                    };
                    bitmap[x, y] = c;
                }
            return bitmap;
        }

        /// <summary>
        /// A member drawn through the mask Icon Services picks for its size: an 8-bit mask of the same size as its alpha;
        /// a mask of another size as a hard edge (a pixel is in where the mask is not 0), scaled as MapRgn scales it.
        /// </summary>
        /// <returns>Null when the family lacks the member or it is a mask; the image opaque when the family has no mask.</returns>
        public RgbaBitmap? Masked(string type)
        {
            if (Image(type) is not { } image) return null;
            if (MaskFor(image.Height) is not { } maskType) return image;
            var mask = MemberType(maskType)!;
            var data = members[maskType];
            bool In(int x, int y) => mask.Depth == 1
                ? ((data[mask.RawSize / 2 + (y * mask.Width + x) / 8] >> (7 - ((y * mask.Width + x) & 7))) & 1) != 0
                : data[y * mask.Width + x] != 0;
            if (mask.Width == image.Width && mask.Height == image.Height)
            {
                for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                    {
                        var c = image[x, y];
                        image[x, y] = c with { A = mask.Depth == 1 ? (byte)(In(x, y) ? 255 : 0) : data[y * mask.Width + x] };
                    }
                return image;
            }
            var region = Region.Empty;
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                {
                    if (!In(x, y)) continue;
                    int end = x;
                    while (end < mask.Width && In(end, y)) end++;
                    region = region.Union(Region.FromRect(new PictRect(y, x, y + 1, end)));
                    x = end;
                }
            region = PictureMapping.MapRegion(region, new PictRect(0, 0, mask.Height, mask.Width), new PictRect(0, 0, image.Height, image.Width));
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (!region.Contains(x, y)) image[x, y] = image[x, y] with { A = 0 };
            return image;
        }
    }
}
