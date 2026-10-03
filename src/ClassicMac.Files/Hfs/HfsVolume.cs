using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// A volume being edited: a read-only base (a file, a partition's range, a Disk Copy disk, a decoded NDIF disk) and the
    /// 512-byte sectors written over it, so an edit holds only what it changes (docs/PLAN.md, editing through a block
    /// overlay). Written sectors are never changed in place, so <see cref="Fork"/> is cheap and a failed edit made on a fork
    /// leaves the volume as it was.
    /// </summary>
    internal sealed class HfsVolume
    {
        private const int SectorSize = 512;
        private readonly ForkData data;
        private readonly Dictionary<long, byte[]> sectors;
        private readonly HashSet<long> changed = [];

        public HfsVolume(ForkData data)
            : this(data, [])
        {
        }

        private HfsVolume(ForkData data, Dictionary<long, byte[]> sectors)
        {
            ArgumentNullException.ThrowIfNull(data);
            this.data = data;
            this.sectors = sectors;
        }

        /// <summary>The volume's length in bytes (the base's).</summary>
        public long Length => data.Length;

        /// <summary>The numbers of the sectors this volume has written (a fork's: since it was forked).</summary>
        public IReadOnlyCollection<long> ChangedSectors => changed;

        /// <summary>The numbers of every sector over the base (a fork's include those it was forked with), for saving.</summary>
        public IReadOnlyCollection<long> Sectors => sectors.Keys;

        /// <summary>Reads <paramref name="buffer"/>'s length of bytes at <paramref name="offset"/>, the written sectors over the base.</summary>
        public void Read(long offset, Span<byte> buffer)
        {
            Check(offset, buffer.Length);
            data.ReadAt(offset, buffer);
            if (sectors.Count == 0)
            {
                return;
            }

            for (long sector = offset / SectorSize; sector * SectorSize < offset + buffer.Length; sector++)
            {
                if (sectors.TryGetValue(sector, out var written))
                {
                    long from = Math.Max(offset, sector * SectorSize), to = Math.Min(offset + buffer.Length, (sector + 1) * SectorSize);
                    written.AsSpan((int)(from - sector * SectorSize), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
                }
            }
        }

        /// <summary>Writes <paramref name="bytes"/> at <paramref name="offset"/>, into the sectors over the base.</summary>
        public void Write(long offset, ReadOnlySpan<byte> bytes)
        {
            Check(offset, bytes.Length);
            for (long sector = offset / SectorSize; sector * SectorSize < offset + bytes.Length; sector++)
            {
                var copy = new byte[SectorSize];
                Read(sector * SectorSize, copy.AsSpan(0, (int)Math.Min(SectorSize, Length - sector * SectorSize)));
                long from = Math.Max(offset, sector * SectorSize), to = Math.Min(offset + bytes.Length, (sector + 1) * SectorSize);
                bytes.Slice((int)(from - offset), (int)(to - from)).CopyTo(copy.AsSpan((int)(from - sector * SectorSize)));
                sectors[sector] = copy;
                changed.Add(sector);
            }
        }

        /// <summary>
        /// A copy that changes on its own (the written sectors shared until either writes them again); its
        /// <see cref="ChangedSectors"/> start empty.
        /// </summary>
        public HfsVolume Fork() => new(data, new Dictionary<long, byte[]>(sectors));

        /// <summary>The volume as a fork, reading through the written sectors, with nothing copied.</summary>
        public ForkData AsForkData() => new OverlayForkData(this);

        /// <summary>The whole volume in memory (for the operations that still need it whole).</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[Length];
            Read(0, bytes);
            return bytes;
        }

        private void Check(long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"{offset}+{length} lies outside the {Length}-byte volume.");
            }
        }

        private sealed class OverlayForkData(HfsVolume volume) : ForkData
        {
            public override long Length => volume.Length;

            public override Stream Open() => new OverlayStream(volume);

            protected override void ReadAtCore(long offset, Span<byte> buffer) => volume.Read(offset, buffer);
        }

        private sealed class OverlayStream(HfsVolume volume) : Stream
        {
            private long position;

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => volume.Length;

            public override long Position
            {
                get => position;
                set => position = value;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var count = (int)Math.Max(0, Math.Min(buffer.Length, Length - position));
                volume.Read(position, buffer[..count]);
                position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin) => position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                _ => Length + offset,
            };

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
