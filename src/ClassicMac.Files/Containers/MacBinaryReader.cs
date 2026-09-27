using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// MacBinary I, II and III: a 128-byte header, an optional secondary header, then the data and resource forks, each
    /// padded to a multiple of 128. There is no Apple specification and no Mac OS code for it; the layout follows the
    /// published MacBinary I, MacBinary II (1987) and MacBinary III (1996) specifications. Only III has a signature, so
    /// each version is one reader with its own strict test, tried III, II, I.
    /// </summary>
    public sealed class MacBinaryReader : IContainerReader
    {
        private const int HeaderLength = 128;
        private const uint MBin = 0x6D42494E; // 'mBIN'
        private const long MaxForkLength = 0x7FFFFF; // the specifications' limit for either fork

        private readonly int version;

        private MacBinaryReader(int version) => this.version = version;

        /// <summary>MacBinary III: the MacBinary II header with 'mBIN' at 102.</summary>
        public static MacBinaryReader III { get; } = new(3);

        /// <summary>MacBinary II: the header's CRC at 124 matches bytes 0–123.</summary>
        public static MacBinaryReader II { get; } = new(2);

        /// <summary>MacBinary I: no CRC; bytes 99–125 zero.</summary>
        public static MacBinaryReader I { get; } = new(1);

        /// <inheritdoc/>
        public string FormatName => version switch { 3 => "MacBinary III", 2 => "MacBinary II", _ => "MacBinary I" };

        /// <inheritdoc/>
        public bool CanRead(ForkData input) => Detect(input.ReadPrefix(HeaderLength)) == version;

        // The version the header shows, or 0. Checks fitted to the specifications' "must be zero" bytes and limits:
        // bytes 0, 74 and 82 zero, a name of 1–63 bytes, fork lengths within $7FFFFF; then the CRC decides II/III
        // against I, and 'mBIN' decides III against II.
        internal static int Detect(ReadOnlySpan<byte> header)
        {
            if (header.Length < HeaderLength) return 0;
            if (header[0] != 0 || header[74] != 0 || header[82] != 0) return 0;
            if (header[1] is < 1 or > 63) return 0;
            if (BinaryPrimitives.ReadUInt32BigEndian(header[83..]) > MaxForkLength) return 0;
            if (BinaryPrimitives.ReadUInt32BigEndian(header[87..]) > MaxForkLength) return 0;
            if (Crc16.Compute(header[..124]) == BinaryPrimitives.ReadUInt16BigEndian(header[124..]))
                return BinaryPrimitives.ReadUInt32BigEndian(header[102..]) == MBin ? 3 : 2;
            return header[99..126].IndexOfAnyExcept((byte)0) < 0 ? 1 : 0;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var header = input.ReadPrefix(HeaderLength);
            if (Detect(header) != version) throw new InvalidDataException($"Not a {FormatName} file.");

            var name = new MacString(header.AsSpan(2, header[1]));
            // Finder flags: the high byte at 73 in every version, the low byte at 101 from MacBinary II on.
            var flags = (FinderFlags)(header[73] << 8 | (version >= 2 ? header[101] : 0));
            var extended = new byte[16];
            if (version == 3)
            {
                extended[8] = header[106]; // fdScript
                extended[9] = header[107]; // fdXFlags
            }
            var finderInfo = new FinderInfo
            {
                Type = new FourCC(header.AsSpan(65, 4)),
                Creator = new FourCC(header.AsSpan(69, 4)),
                Flags = flags,
                Location = MacPoint.Read(header.AsSpan(75)),
                Folder = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(79)),
                Extended = extended,
            };

            long dataLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(83));
            long resourceLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(87));
            long secondaryLength = version >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(120)) : 0;
            var dataOffset = HeaderLength + Padded(secondaryLength);
            var resourceOffset = dataOffset + Padded(dataLength);

            return
            [
                new MacFile
                {
                    Name = name,
                    FinderInfo = finderInfo,
                    Created = Date(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(91))),
                    Modified = Date(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(95))),
                    DataFork = Fork(input, dataOffset, dataLength, "data", context),
                    ResourceFork = Fork(input, resourceOffset, resourceLength, "resource", context),
                },
            ];
        }

        private static long Padded(long length) => (length + HeaderLength - 1) / HeaderLength * HeaderLength;

        private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

        private static ForkData Fork(ForkData input, long offset, long length, string which, ContainerContext context)
        {
            if (length == 0) return ForkData.Empty;
            var available = Math.Max(0, input.Length - offset);
            if (length > available)
            {
                context.Report(DiagnosticSeverity.Error, "macbinary.fork-truncated",
                    $"The {which} fork claims {length} bytes but only {available} remain; the rest is missing.", offset);
                length = available;
            }
            return length == 0 ? ForkData.Empty : input.Slice(offset, length);
        }
    }
}
