using System;
using System.Buffers.Binary;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// An HFS Plus volume's journal replayed, as TN1150's "Replaying the Journal" describes (hfs-plus.md §1.6, §5.4): the
// journal info block from the volume header, the journal header, then each block list from start to end in the
// circular buffer, every block copied to its sector (bnum × jhdr_size bytes into the volume); then the journal is
// emptied (start = end, the checksum made again). The journal's structures are in the byte order of the Mac that wrote
// them, which the endian field tells. TN1150 gives a block list's data blocks both as binfo[1] to binfo[num_blocks] and
// as num_blocks − 1 of them; the field's description (binfo[0] counted) is followed [Doc: TN1150; Code: XNU journal.c,
// reference only].
internal static class PlusJournal
{
    private const uint Journaled = 0x2000, InFileSystem = 1, OnOtherDevice = 2, NeedsInit = 4;
    private const uint Magic = 0x4A4E4C78, EndianMagic = 0x12345678;
    private const int HeaderLength = 44, InfoLength = 180;

    /// <summary>What a replay found: the transactions and blocks written, or why the journal could not be replayed.</summary>
    public readonly record struct Result(int Transactions, int Blocks, string? Damage);

    /// <summary>
    /// Replays the journal of the HFS Plus volume at <paramref name="offset"/> into <paramref name="volume"/>; null when
    /// the volume is not journaled or its journal is empty or never initialised.
    /// </summary>
    public static Result? Replay(HfsVolume volume, long offset)
    {
        var header = Read(volume, offset + 1024, 512);
        var vh = new BigEndianReader(header);
        if (vh.ReadUInt16At(0) != 0x482B || (vh.ReadUInt32At(4) & Journaled) == 0)
        {
            return null;
        }

        long blockSize = vh.ReadUInt32At(40), volumeBytes = blockSize * vh.ReadUInt32At(44);
        var info = new BigEndianReader(Read(volume, offset + vh.ReadUInt32At(12) * blockSize, InfoLength));
        uint flags = info.ReadUInt32At(0);
        if ((flags & NeedsInit) != 0)
        {
            return null;
        }

        long journal = (long)Math.Min(info.ReadUInt64At(36), long.MaxValue), size = (long)Math.Min(info.ReadUInt64At(44), long.MaxValue);
        if ((flags & InFileSystem) == 0 || (flags & OnOtherDevice) != 0 || size < 512 || journal > volumeBytes - size)
        {
            return new Result(0, 0, "the journal info block does not place the journal in the volume");
        }

        var fields = Read(volume, offset + journal, HeaderLength);
        bool little = BinaryPrimitives.ReadUInt32LittleEndian(fields) == Magic;
        if (!little && new BigEndianReader(fields).ReadUInt32At(0) != Magic || U32(fields, 4, little) != EndianMagic)
        {
            return new Result(0, 0, "the journal header's magic is wrong");
        }

        long start = (long)U64(fields, 8, little), end = (long)U64(fields, 16, little);
        int listSize = (int)U32(fields, 32, little), sector = (int)U32(fields, 40, little);
        if ((long)U64(fields, 24, little) != size || sector < HeaderLength || sector > size || listSize < 32 || listSize > size - sector
            || start < sector || start >= size || end < sector || end >= size
            || Checksum(Read(volume, offset + journal, sector), 36) != U32(fields, 36, little))
        {
            return new Result(0, 0, "the journal header is inconsistent");
        }

        if (start == end)
        {
            return null;
        }

        int transactions = 0, blocks = 0;
        long at = start;
        for (long lists = 0; at != end; lists++)
        {
            if (lists > size / listSize)
            {
                return new Result(transactions, blocks, "the journal's block lists do not end");
            }

            var list = Circular(volume, offset + journal, size, sector, at, listSize);
            int count = U16(list, 2, little);
            long used = U32(list, 4, little);
            if (Checksum(list.AsSpan(0, 32), 8) != U32(list, 8, little))
            {
                return new Result(transactions, blocks, "a block list's checksum is wrong");
            }

            if (used < listSize || used > size - sector || 16 + 16L * count > listSize)
            {
                return new Result(transactions, blocks, "a block list's sizes are inconsistent");
            }

            long data = Advance(at, listSize, size, sector);
            for (var i = 1; i < count; i++)
            {
                ulong number = U64(list, 16 + 16 * i, little);
                int length = (int)U32(list, 16 + 16 * i + 8, little);
                if (length < 0 || length % 512 != 0 || length > used)
                {
                    return new Result(transactions, blocks, "a journal block's size is not whole sectors");
                }

                var bytes = Circular(volume, offset + journal, size, sector, data, length);
                data = Advance(data, length, size, sector);
                if (number == ulong.MaxValue)
                {
                    continue;
                }

                long target = (long)Math.Min(number, (ulong)(long.MaxValue / sector)) * sector;
                if (target > volumeBytes - length)
                {
                    return new Result(transactions, blocks, "a journal block lies past the volume");
                }

                volume.Write(offset + target, bytes);
                blocks++;
            }

            if (U32(list, 16 + 12, little) == 0)
            {
                transactions++;
            }

            at = Advance(at, used, size, sector);
        }

        // Emptied: start = end, the checksum over the header sector made again.
        var sectorBytes = Read(volume, offset + journal, sector);
        Put64(sectorBytes, 8, (ulong)end, little);
        Put32(sectorBytes, 36, Checksum(sectorBytes, 36), little);
        volume.Write(offset + journal, sectorBytes);
        return new Result(transactions, blocks, null);
    }

    // TN1150's calc_checksum, the 4-byte checksum field taken as zero.
    private static uint Checksum(ReadOnlySpan<byte> bytes, int field)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            byte value = i >= field && i < field + 4 ? (byte)0 : bytes[i];
            sum = unchecked((sum << 8) ^ (sum + value));
        }

        return ~sum;
    }

    // The journal buffer runs from the header sector to the journal's end and wraps around to just after the header.
    private static long Advance(long at, long length, long size, long sector)
    {
        long next = at + length;
        return next >= size ? sector + (next - size) % (size - sector) : next;
    }

    private static byte[] Circular(HfsVolume volume, long journal, long size, long sector, long at, int length)
    {
        var bytes = new byte[length];
        int done = 0;
        while (done < length)
        {
            int part = (int)Math.Min(length - done, size - at);
            volume.Read(journal + at, bytes.AsSpan(done, part));
            done += part;
            at = Advance(at, part, size, sector);
        }

        return bytes;
    }

    private static byte[] Read(HfsVolume volume, long at, int length)
    {
        var bytes = new byte[length];
        if (at >= 0 && at + length <= volume.Length)
        {
            volume.Read(at, bytes);
        }

        return bytes;
    }

    private static ushort U16(byte[] bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)) : new BigEndianReader(bytes).ReadUInt16At(at);

    private static uint U32(byte[] bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) : new BigEndianReader(bytes).ReadUInt32At(at);

    private static ulong U64(byte[] bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) : new BigEndianReader(bytes).ReadUInt64At(at);

    private static void Put32(byte[] bytes, int at, uint value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        }
        else
        {
            new BigEndianWriter(bytes).WriteUInt32At(at, value);
        }
    }

    private static void Put64(byte[] bytes, int at, ulong value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        }
        else
        {
            new BigEndianWriter(bytes).WriteUInt64At(at, value);
        }
    }
}
