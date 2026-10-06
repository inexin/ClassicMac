using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Iso;

/// <summary>
/// Raw CD images (<c>.bin</c>): sectors of 2352 bytes (sync, header, data, error correction) or 2336 (mode 2
/// without sync and header). The disc comes out as one file whose data fork holds the 2048-byte user-data blocks —
/// what a CD drive hands the Mac, since Apple's CD driver never parses sectors itself — for the HFS, partition-map
/// or ISO 9660 readers to open next. User data starts at byte 16 of a mode 1 sector, 24 of a mode 2 form 1 sector
/// and 8 of a 2336-byte one (ECMA-130; Apple CD/DVD Driver 1.3.1 disassembly for what the Mac reads). Mode 2 form 2
/// sectors carry no 2048-byte blocks and read as zeros.
/// </summary>
public sealed class RawCdReader : IContainerReader
{
    private const int Block = 2048;
    private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>The reader.</summary>
    public static RawCdReader Instance { get; } = new();

    private RawCdReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "raw CD image";

    /// <inheritdoc/>
    public bool CanRead(ForkData input) => Layout(input) is not null;

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        var sectorSize = Layout(input) ?? throw new InvalidDataException("Not a raw CD image.");
        var disc = sectorSize == 2352 ? Sessions(input) : null;
        return [new MacFile { Name = DiscName(context), DataFork = disc ?? Cooked(input, sectorSize) }];
    }

    /// <summary>
    /// The absolute sector (logical block address) in a 2352-byte data sector's header: its minute, second and frame
    /// in BCD, less the 150 sectors (two seconds) before logical block 0. Null for a sector without sync, a data mode
    /// or a valid address (audio, damaged sectors).
    /// </summary>
    internal static long? HeaderLba(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 16 || !IsRawSector(sector))
        {
            return null;
        }

        static int? Bcd(byte b) => (b >> 4) <= 9 && (b & 15) <= 9 ? (b >> 4) * 10 + (b & 15) : null;
        if (Bcd(sector[12]) is not { } m || Bcd(sector[13]) is not { } s || Bcd(sector[14]) is not { } f || s >= 60 || f >= 75)
        {
            return null;
        }

        var lba = ((long)m * 60 + s) * 75 + f - 150;
        return lba >= 0 ? lba : null;
    }

    // ClassicMac's rule, not the Mac's (which asks the drive for the table of contents): a raw image of a whole
    // multisession disc usually leaves out the sectors between sessions (lead-out, lead-in), which the sector headers
    // show as a jump in address. Each run of sectors whose address less its index is constant is placed at its
    // address; the last run is the last session. Its first track starts at the run's first sector, or 150 sectors
    // later when the run begins with a pregap: whichever has a volume descriptor 16 sectors on. An image without a
    // jump (one session, or the gap kept) is left as it is (null).
    private static CdDisc? Sessions(ForkData input)
    {
        var count = input.Length / 2352;
        using var stream = input.Open();
        var sector = new byte[2352];
        long? Offset(long index)
        {
            stream.Position = index * 2352;
            stream.ReadExactly(sector);
            return HeaderLba(sector) - index;
        }

        // The offset of the nearest sector with an address, looking up to 300 sectors from index on (a disc may
        // start or end in audio or damaged sectors).
        long? Nearest(long index, int step)
        {
            for (var i = 0; i < 300 && index >= 0 && index < count; i++, index += step)
            {
                if (Offset(index) is { } o)
                {
                    return o;
                }
            }
            return null;
        }

        if (Nearest(count - 1, -1) is not { } offset || offset <= 0 || Nearest(0, 1) == offset)
        {
            return null;
        }

        var runs = new List<CdDisc.Segment>();
        var end = count;
        while (offset > 0 && end > 0 && runs.Count < 99)
        {
            // The run's first sector: the first index from which the offset is this one (offsets only grow).
            long lo = 0, hi = end - 1;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (Offset(mid) is { } o && o >= offset)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            runs.Add(new CdDisc.Segment(lo + offset, end - lo, Cooked(input.Slice(lo * 2352, (end - lo) * 2352), 2352)));
            end = lo;
            offset = end > 0 && Nearest(end - 1, -1) is { } before && before < offset ? before : 0;
        }
        if (end > 0)
        {
            runs.Add(new CdDisc.Segment(0, end, Cooked(input.Slice(0, end * 2352), 2352)));
        }

        var session = runs[0].Lba;
        var disc = new CdDisc(runs, session);
        foreach (var start in new[] { session, session + 150 })
        {
            if ((start + 17) * 2048 > disc.Length)
            {
                continue;
            }

            var v = disc.Slice((start + 16) * 2048, 16).ToArray();
            if (v.AsSpan(1, 5).SequenceEqual("CD001"u8) || v.AsSpan(1, 5).SequenceEqual("CD-I "u8) || v.AsSpan(9, 5).SequenceEqual("CDROM"u8))
            {
                return new CdDisc(runs, start);
            }
        }
        return disc;
    }

    /// <summary>The 2048-byte blocks of raw sectors of <paramref name="sectorSize"/> bytes (2352 or 2336).</summary>
    internal static ForkData Cooked(ForkData raw, int sectorSize) => new CookedForkData(raw, sectorSize);

    internal static MacString DiscName(ContainerContext context)
    {
        var name = context.HostName?.ToMacRoman() ?? "CD";
        var dot = name.LastIndexOf('.');
        return MacString.FromMacRoman(dot > 0 ? name[..dot] : name);
    }

    // 2352-byte sectors start with the sync pattern (checked at sectors 0 and 16) and a mode byte of 1 or 2; 2336-byte
    // sectors have none, so a volume must be found in their user data (an ISO descriptor at sector 16, or an Apple
    // partition map or HFS volume at the start), with the subheader's two copies equal.
    private static int? Layout(ForkData input)
    {
        if (input.Length >= 2352 * 17 && input.Length % 2352 == 0)
        {
            var sector16 = input.Slice(16 * 2352L, 2352).ToArray();
            var sector0 = input.ReadPrefix(2352);
            if (IsRawSector(sector16) || IsRawSector(sector0))
            {
                return 2352;
            }
        }
        if (input.Length >= 2336 * 17 && input.Length % 2336 == 0)
        {
            var cooked = Cooked(input, 2336);
            var descriptor = cooked.Slice(16 * Block, 16).ToArray();
            var start = cooked.ReadPrefix(1026);
            var subheader = input.Slice(16 * 2336L, 8).ToArray();
            if (subheader.AsSpan(0, 4).SequenceEqual(subheader.AsSpan(4, 4))
                && (descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8) || descriptor.AsSpan(9, 5).SequenceEqual("CDROM"u8)
                    || start.AsSpan(0, 2).SequenceEqual("ER"u8) || start.AsSpan(1024, 2).SequenceEqual("BD"u8)))
            {
                return 2336;
            }
        }
        return null;
    }

    private static bool IsRawSector(ReadOnlySpan<byte> sector) => sector[..12].SequenceEqual(Sync) && sector[15] is 1 or 2;

    // 2048-byte blocks read out of raw sectors, each sector's own mode deciding where its data starts.
    private sealed class CookedForkData(ForkData raw, int sectorSize) : ForkData
    {
        public override long Length { get; } = raw.Length / sectorSize * Block;

        // Each block is read out of its raw sector, none made.
        internal override ForkData? Underlying => raw;

        public override Stream Open() => new CookedStream(raw.Open(), sectorSize, Length);

        private sealed class CookedStream(Stream inner, int sectorSize, long length) : Stream
        {
            private readonly byte[] sector = new byte[2352];
            private long position;
            private long cachedSector = -1;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;

            public override long Position
            {
                get => position;
                set => position = value >= 0 ? value : throw new IOException("Cannot seek before the start of the stream.");
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var total = 0;
                while (buffer.Length > 0 && position < length)
                {
                    var index = position / Block;
                    var within = (int)(position % Block);
                    var data = Data(index);
                    var take = Math.Min(buffer.Length, Block - within);
                    data.Slice(within, take).CopyTo(buffer);
                    buffer = buffer[take..];
                    position += take;
                    total += take;
                }
                return total;
            }

            private ReadOnlySpan<byte> Data(long index)
            {
                if (index != cachedSector)
                {
                    inner.Seek(index * sectorSize, SeekOrigin.Begin);
                    inner.ReadExactly(sector.AsSpan(0, sectorSize));
                    cachedSector = index;
                }
                if (sectorSize == 2336)
                {
                    return (sector[2] & 0x20) != 0 ? Zeros : sector.AsSpan(8, Block); // form 2: no 2048-byte block
                }

                return sector[15] switch
                {
                    1 => sector.AsSpan(16, Block),
                    2 when (sector[18] & 0x20) == 0 => sector.AsSpan(24, Block),
                    _ => Zeros,
                };
            }

            private static readonly byte[] Zeros = new byte[Block];

            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
                return position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
