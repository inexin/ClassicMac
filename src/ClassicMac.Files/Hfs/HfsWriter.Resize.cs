using System;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

public static partial class HfsWriter
{
    /// <summary>
    /// A plain HFS volume grown to <paramref name="size"/> bytes within its allocation block size (hfs.md §3.2): the new
    /// allocation blocks are free, the bitmap covers them (the allocation area moved up whole sectors when the bitmap's
    /// sectors are full), and the alternate MDB is at the new end. Every file, folder and CNID stays. Returns a new image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not whole 512-byte blocks, or is past what an image in memory can hold.</exception>
    /// <exception cref="InvalidDataException">
    /// The volume is not one the writer edits, or the size is not larger, or the volume would need more than 65,535
    /// allocation blocks of its size.
    /// </exception>
    public static byte[] Resize(ForkData image, long size)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (size % BlockSize != 0 || size <= 0 || size > MaximumFormatSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An HFS volume is whole 512-byte blocks, up to just under 2 GB.");
        }

        var state = OpenCatalog(image);
        var source = state.Source;
        if (size < source.Length)
        {
            throw new InvalidDataException("Shrinking an HFS volume is not supported yet.");
        }

        if (size == source.Length)
        {
            throw new InvalidDataException($"The HFS volume is {size} bytes already.");
        }

        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        long factor = state.BlockSize / BlockSize;
        long bitmapStart = U16(mdb, 0x0E);
        long oldStart = U16(mdb, 0x1C);
        long sectors = size / BlockSize;

        // The new block count, with the bitmap grown into the allocation area when its sectors cannot cover it.
        long start = oldStart, count;
        while (true)
        {
            count = (sectors - start - 2) / factor;
            if ((start - bitmapStart) * 4096 >= count)
            {
                break;
            }

            start++;
        }

        if (count > ushort.MaxValue)
        {
            throw new InvalidDataException($"The HFS volume would need {count} allocation blocks of {state.BlockSize} bytes; HFS has at most 65,535 (a larger block size is not supported yet).");
        }

        long oldCount = state.BlockCount;
        var result = new byte[size];
        source.AsSpan(0, (int)(oldStart * BlockSize)).CopyTo(result);
        long areaBytes = oldCount * state.BlockSize;
        source.AsSpan((int)(oldStart * BlockSize), (int)areaBytes).CopyTo(result.AsSpan((int)(start * BlockSize)));
        var bitmapBytes = (int)((oldCount + 7) / 8);
        result.AsSpan((int)(bitmapStart * BlockSize + bitmapBytes), (int)((start - bitmapStart) * BlockSize - bitmapBytes)).Clear();
        source.AsSpan((int)(bitmapStart * BlockSize), bitmapBytes).CopyTo(result.AsSpan((int)(bitmapStart * BlockSize)));
        for (long block = oldCount; block < (bitmapBytes * 8L); block++)
        {
            result[bitmapStart * BlockSize + block / 8] &= (byte)~(0x80 >> (int)(block % 8));
        }

        var writer = new BigEndianWriter(result);
        writer.WriteUInt16At(MdbOffset + 0x12, count);                                // drNmAlBlks
        writer.WriteUInt16At(MdbOffset + 0x1C, start);                                // drAlBlSt
        writer.WriteUInt16At(MdbOffset + 0x22, U16(mdb, 0x22) + (count - oldCount));   // drFreeBks
        writer.WriteUInt32At(MdbOffset + 0x06, MacDate.FromDateTime(DateTime.Now).Seconds);   // drLsMod
        writer.WriteUInt32At(MdbOffset + 0x46, unchecked(U32(mdb, 0x46) + 1));        // drWrCnt
        result.AsSpan(MdbOffset, BlockSize).CopyTo(result.AsSpan((int)(size - 2 * BlockSize)));

        // Checked as a deletion is: the result opens as the writer opens a volume, its records are the source's, and every
        // allocated block is the source's, read where the allocation area now starts.
        var after = OpenCatalog(ForkData.FromBytes(result), writable: false);
        if (after.Records.Count != state.Records.Count ||
            after.Records.Zip(state.Records).Any(pair => !pair.First.Key.AsSpan().SequenceEqual(pair.Second.Key) || !pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data)))
        {
            throw new InvalidDataException("The grown HFS volume's catalog differs from the source's.");
        }

        for (uint block = 0; block < oldCount; block++)
        {
            if (IsAllocated(state.Bitmap, (ushort)block) &&
                !source.AsSpan((int)(oldStart * BlockSize + block * state.BlockSize), (int)state.BlockSize)
                    .SequenceEqual(result.AsSpan((int)(start * BlockSize + block * state.BlockSize), (int)state.BlockSize)))
            {
                throw new InvalidDataException("The grown HFS volume changed a block in use.");
            }
        }

        return result;
    }
}
