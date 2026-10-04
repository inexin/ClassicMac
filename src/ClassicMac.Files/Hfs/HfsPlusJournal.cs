using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusReader;
using static ClassicMac.Files.Hfs.HfsPlusAllocation;
using static ClassicMac.Files.Hfs.HfsPlusAttributes;
using static ClassicMac.Files.Hfs.HfsPlusLinks;
using static ClassicMac.Files.Hfs.HfsPlusBTree;

namespace ClassicMac.Files.Hfs;

// HFS Plus reading, for HfsPlusReader: the journal info block, the journal header and the catalog files the journal names (TN1150 "Journal").
internal static class HfsPlusJournal
{
    internal static JournalInfo? ReportJournalInfoBlockProblem(ForkData image, BigEndianReader volumeHeader,
        uint blockSize, uint totalBlocks, byte[] allocationBitmap, ContainerContext context)
    {
        const uint journalInVolumeFlag = 0x00000001;
        const uint journalOnOtherDeviceFlag = 0x00000002;
        const uint journalNeedsInitializationFlag = 0x00000004;
        const int journalInfoBlockLength = 180;
        uint allocationBlock = U32(volumeHeader, 12);
        if (allocationBlock >= totalBlocks ||
            (allocationBitmap[allocationBlock / 8] & (0x80 >> (int)(allocationBlock & 7))) == 0)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus journalInfoBlock points outside the allocation area or to an unallocated block.");
            return null;
        }

        long blockOffset = checked((long)allocationBlock * blockSize);
        var journalInfo = new BigEndianReader(image.Slice(blockOffset, journalInfoBlockLength).ToArray());
        uint flags = U32(journalInfo, 0);
        ulong volumeBytes = (ulong)blockSize * totalBlocks;
        ulong journalOffset = U64(journalInfo, 36);
        ulong journalSize = U64(journalInfo, 44);
        if ((flags & journalInVolumeFlag) == 0 || (flags & journalOnOtherDeviceFlag) != 0 ||
            journalSize == 0 || journalOffset > volumeBytes || journalSize > volumeBytes - journalOffset)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus JournalInfoBlock has unsupported storage flags or a journal range outside the volume.");
            return null;
        }

        ulong journalEnd = journalOffset + journalSize;
        uint firstJournalBlock = checked((uint)(journalOffset / blockSize));
        uint lastJournalBlock = checked((uint)((journalEnd - 1) / blockSize));
        for (uint block = firstJournalBlock; block <= lastJournalBlock; block++)
        {
            if ((allocationBitmap[block / 8] & (0x80 >> (int)(block & 7))) != 0)
            {
                continue;
            }

            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus journal range includes an allocation block marked free.");
            return null;
        }
        return new JournalInfo(journalOffset, journalSize, (flags & journalNeedsInitializationFlag) != 0);
    }

    internal static void ValidateJournalCatalogFiles(JournalInfo? journalInfo,
        IReadOnlyDictionary<string, JournalCatalogFile> journalFiles, uint journalInfoBlock, uint blockSize,
        ContainerContext context)
    {
        if (!journalFiles.TryGetValue(".journal_info_block", out JournalCatalogFile infoFile) ||
            !journalFiles.TryGetValue(".journal", out JournalCatalogFile journalFile))
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The journaled HFS Plus volume is missing its root .journal or .journal_info_block file.");
            return;
        }

        if (!infoFile.IsSingleExtent || infoFile.LogicalSize != 180 || infoFile.TotalBlocks != 1 ||
            infoFile.ExtentStart != journalInfoBlock)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus .journal_info_block file does not match the volume header's journalInfoBlock extent.");
        }

        if (journalInfo is not { } info)
        {
            return;
        }

        ulong expectedJournalBlocks = (info.Size + blockSize - 1) / blockSize;
        ulong expectedOffset = (ulong)journalFile.ExtentStart * blockSize;
        if (!journalFile.IsSingleExtent || journalFile.LogicalSize != info.Size ||
            journalFile.TotalBlocks != expectedJournalBlocks || info.Offset != expectedOffset)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus JournalInfoBlock range does not match the root .journal file's single extent.");
        }
    }

    internal static void ValidateJournalHeader(ForkData image, JournalInfo journalInfo, ContainerContext context)
    {
        const int journalHeaderLength = 44;
        const uint journalHeaderMagic = 0x4A4E4C78;
        const uint endianMagic = 0x12345678;
        if (journalInfo.NeedsInitialization)
        {
            return;
        }

        if (journalInfo.Size < journalHeaderLength || journalInfo.Offset > long.MaxValue ||
            journalInfo.Offset > (ulong)image.Length ||
            journalInfo.Size > (ulong)(image.Length - (long)journalInfo.Offset))
        {
            ReportInvalidJournalHeader(context);
            return;
        }

        var fields = new BigEndianReader(
            image.Slice((long)journalInfo.Offset, journalHeaderLength).ToArray(journalHeaderLength));
        uint magic = U32(fields, 0);
        uint endian = U32(fields, 4);
        ulong start = U64(fields, 8);
        ulong end = U64(fields, 16);
        ulong declaredSize = U64(fields, 24);
        uint blockListHeaderSize = U32(fields, 32);
        uint expectedChecksum = U32(fields, 36);
        uint headerSize = U32(fields, 40);

        if (magic != journalHeaderMagic || endian != endianMagic || declaredSize != journalInfo.Size ||
            headerSize < journalHeaderLength || headerSize > journalInfo.Size || blockListHeaderSize < 32 ||
            start < headerSize || start >= journalInfo.Size || end < headerSize || end > journalInfo.Size ||
            CalculateJournalHeaderChecksum(image, journalInfo.Offset, headerSize) != expectedChecksum)
        {
            ReportInvalidJournalHeader(context);
        }
    }

    internal static uint CalculateJournalHeaderChecksum(ForkData image, ulong journalOffset, uint headerSize)
    {
        uint checksum = 0;
        byte[] buffer = new byte[8192];
        using Stream stream = image.Slice(checked((long)journalOffset), headerSize).Open();
        long position = 0;
        while (position < headerSize)
        {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, headerSize - position));
            if (count == 0)
            {
                throw new EndOfStreamException("The HFS Plus journal header is truncated.");
            }

            for (int index = 0; index < count; index++, position++)
            {
                byte value = position is >= 36 and < 40 ? (byte)0 : buffer[index];
                checksum = unchecked((checksum << 8) ^ (checksum + value));
            }
        }
        return ~checksum;
    }

    internal static void ReportInvalidJournalHeader(ContainerContext context) =>
        context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
            "The HFS Plus journal header has invalid fields, offsets, size, or checksum.");

    internal static JournalCatalogFile ReadJournalCatalogFile(BigEndianReader fork)
    {
        ulong logicalSize = U64(fork, 0);
        uint totalBlocks = U32(fork, 12);
        uint extentStart = U32(fork, 16);
        uint extentBlocks = U32(fork, 20);
        bool singleExtent = extentBlocks != 0 && extentBlocks == totalBlocks;
        for (int index = 1; index < 8; index++)
        {
            if (U32(fork, 16 + index * 8) != 0 || U32(fork, 20 + index * 8) != 0)
            {
                singleExtent = false;
            }
        }

        return new JournalCatalogFile(logicalSize, totalBlocks, extentStart, singleExtent);
    }

    internal readonly record struct JournalInfo(ulong Offset, ulong Size, bool NeedsInitialization);

    internal readonly record struct JournalCatalogFile(ulong LogicalSize, uint TotalBlocks, uint ExtentStart,
        bool IsSingleExtent);
}
