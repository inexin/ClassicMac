using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// Makes an NDIF (Disk Copy 6) image again around a changed disk (docs/formats/disk-images/ndif.md §3): the chunk
    /// boundaries stay, a chunk whose sectors are unchanged keeps its stored bytes (compressed or not), and a changed one
    /// is stored raw, as Disk Copy stores a chunk that would not shrink. The map's offsets, types and lengths, its CRC
    /// (when the image has one) and the CRC in the <c>'vers'</c> text are made again.
    /// </summary>
    public static class NdifWriter
    {
        private const int SectorSize = 512, HeaderLength = 0x80, EntryLength = 12;
        private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkEnd = 0xFF;
        private static readonly FourCC Bcem = FourCC.FromString("bcem"), Vers = FourCC.FromString("vers");

        /// <summary>Whether <see cref="Rewrite"/> takes the image: an NDIF image with map version 10 to 12, not segmented.</summary>
        public static bool CanRewrite(MacFile image)
        {
            ArgumentNullException.ThrowIfNull(image);
            try
            {
                var map = ResourceFork.Read(image.ResourceFork.ToArray()).Find(Bcem, 128)?.GetData().ToArray();
                return map is { Length: >= HeaderLength } && new BigEndianReader(map) is var reader &&
                    reader.ReadUInt16At(0) is >= 10 and <= 12 && reader.ReadUInt32At(0x54) == 0;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>
        /// The image with its disk replaced by <paramref name="disk"/>; its name, Finder info and other resources stay. With
        /// <paramref name="changedSectors"/> (the 512-byte sectors that may differ from the image's disk), a chunk holding
        /// none of them keeps its stored bytes without the old disk being decoded to compare it.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The image is not one ClassicMac rewrites (map version 10–12, not segmented), or the disk is not the image's size.
        /// </exception>
        public static MacFile Rewrite(MacFile image, ReadOnlyMemory<byte> disk, IReadOnlySet<long>? changedSectors = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var fork = ResourceFork.Read(image.ResourceFork.ToArray());
            var bcem = fork.Find(Bcem, 128) ?? throw new InvalidDataException("Not an NDIF image: it has no 'bcem' 128.");
            var map = bcem.GetData().ToArray();
            var reader = new BigEndianReader(map);
            int version = reader.ReadUInt16At(0);
            if (version is < 10 or > 12)
            {
                throw new InvalidDataException($"NDIF map version {version} is not rewritten (versions 10 to 12 are).");
            }

            if (reader.ReadUInt32At(0x54) != 0)
            {
                throw new InvalidDataException("A segmented NDIF image is not rewritten.");
            }

            long blocks = reader.ReadUInt32At(0x44);
            if (blocks * SectorSize != disk.Length)
            {
                throw new InvalidDataException($"The disk is {disk.Length} bytes; the image holds {blocks * SectorSize}.");
            }

            long dataStart = reader.ReadUInt32At(0x4C);
            int count = checked((int)reader.ReadUInt32At(0x7C));
            if (map.Length < HeaderLength + count * EntryLength)
            {
                throw new InvalidDataException("The NDIF map is shorter than its entries.");
            }

            var old = changedSectors is null ? NdifReader.Instance.Read(image, new ContainerContext()).Single().DataFork.ToArray() : null;
            var data = image.DataFork.ToArray();
            var output = new MemoryStream();
            output.Write(data.AsSpan(0, (int)Math.Min(dataStart, data.Length)));
            var writer = new BigEndianWriter(map);
            for (var k = 0; k < count; k++)
            {
                int at = HeaderLength + k * EntryLength;
                uint word = reader.ReadUInt32At(at);
                long start = word >> 8;
                byte type = (byte)word;
                if (type == ChunkEnd)
                {
                    writer.WriteUInt32At(at + 4, output.Length - dataStart);
                    writer.WriteUInt32At(at + 8, 0u);
                    continue;
                }

                long next = k + 1 < count ? reader.ReadUInt32At(at + EntryLength) >> 8 : blocks;
                int from = checked((int)(start * SectorSize)), size = checked((int)((next - start) * SectorSize));
                var sectors = disk.Span.Slice(from, size);
                bool same = old is not null
                    ? sectors.SequenceEqual(old.AsSpan(from, size))
                    : !changedSectors!.Any(sector => sector >= start && sector < next);
                if (same)
                {
                    if (type == ChunkZero)
                    {
                        continue;
                    }

                    long offset = reader.ReadUInt32At(at + 4), stored = reader.ReadUInt32At(at + 8);
                    writer.WriteUInt32At(at + 4, output.Length - dataStart);
                    output.Write(data.AsSpan((int)(dataStart + offset), (int)stored));
                    continue;
                }

                writer.WriteUInt32At(at, (uint)(start << 8) | ChunkRaw);
                writer.WriteUInt32At(at + 4, output.Length - dataStart);
                writer.WriteUInt32At(at + 8, size);
                output.Write(sectors);
            }

            uint crc = 0;
            if (reader.ReadUInt32At(0x50) != 0)
            {
                crc = NdifReader.Crc(ForkData.FromBytes(disk.ToArray()));
                writer.WriteUInt32At(0x50, crc);
            }

            var resources = new ResourceFork();
            foreach (var resource in fork.Resources)
            {
                var bytes = resource.Type == Bcem && resource.Id == 128 ? map
                    : resource.Type == Vers && resource.Id == 1 && crc != 0 ? WithCrc(resource.GetData().ToArray(), crc)
                    : resource.GetData().ToArray();
                resources.Add(new Resource(resource.Type, resource.Id, bytes) { Name = resource.Name, Attributes = resource.Attributes });
            }

            var result = image with { DataFork = ForkData.FromBytes(output.ToArray()), ResourceFork = ForkData.FromBytes(resources.ToArray()) };
            var check = new List<Diagnostic>();
            var decoded = NdifReader.Instance.Read(result, new ContainerContext(diagnostics: check)).Single().DataFork.ToArray();
            if (!decoded.AsSpan().SequenceEqual(disk.Span) || check.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                throw new InvalidDataException("The rewritten NDIF image does not read back as the disk.");
            }

            return result;
        }

        // The 'vers' text's "CRC: $xxxxxxxx" (or "CRC28: $…") with the new value; the same length, so the strings' lengths stay.
        private static byte[] WithCrc(byte[] vers, uint crc)
        {
            var text = Encoding.Latin1.GetString(vers);
            var replaced = Regex.Replace(text, @"(CRC(?:28)?: ?\$)[0-9A-Fa-f]{8}", m => m.Groups[1].Value + crc.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return Encoding.Latin1.GetBytes(replaced);
        }
    }
}
