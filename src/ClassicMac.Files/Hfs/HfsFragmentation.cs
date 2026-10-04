using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// HfsReader.ReadFragmentation (hfs.md §5.7): each file record's forks counted in extents, its catalog record's three and
// its overflow records', and the bitmap's runs of free blocks.
internal static class HfsFragmentation
{
    internal static VolumeFragmentation? Measure(ForkData input)
    {
        CatalogEditState state;
        try
        {
            state = OpenCatalog(input, writable: false);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }

        if (new BigEndianReader(state.Mdb).ReadUInt16At(0x7C) == 0x482B)
        {
            return null;                                                                // an HFS wrapper
        }

        var overflow = new Dictionary<(byte Fork, uint File), int>();
        foreach (var record in LeafRecords(state.ExtentsTree))
        {
            var key = (record.Key[1], KeyId(record.Key));
            overflow[key] = overflow.GetValueOrDefault(key) + Extents(record.Data, 0);
        }

        int files = 0, fragmentedFiles = 0, fragmentedForks = 0, most = 0;
        foreach (var (_, data) in state.Records)
        {
            if (data.Length < 0x62 || data[0] != 2)
            {
                continue;
            }

            files++;
            uint id = FileId(data);
            bool fragmented = false;
            foreach (var (fork, offset) in new[] { ((byte)0x00, 0x4A), ((byte)0xFF, 0x56) })
            {
                int extents = Extents(data, offset) + overflow.GetValueOrDefault((fork, id));
                most = Math.Max(most, extents);
                if (extents > 1)
                {
                    fragmentedForks++;
                    fragmented = true;
                }
            }

            fragmentedFiles += fragmented ? 1 : 0;
        }

        int runs = 0;
        long run = 0, largest = 0;
        for (uint block = 0; block < state.BlockCount; block++)
        {
            bool used = ((state.Bitmap[block / 8] >> (7 - (int)(block % 8))) & 1) != 0;
            if (used)
            {
                run = 0;
                continue;
            }

            runs += run == 0 ? 1 : 0;
            largest = Math.Max(largest, ++run);
        }

        return new VolumeFragmentation(files, fragmentedFiles, fragmentedForks, most, runs, largest);
    }

    // The extents with blocks among the three descriptors at offset.
    private static int Extents(byte[] data, int offset)
    {
        var reader = new BigEndianReader(data);
        int count = 0;
        for (var slot = 0; slot < 3; slot++)
        {
            count += reader.ReadUInt16At(offset + slot * 4 + 2) > 0 ? 1 : 0;
        }

        return count;
    }
}
