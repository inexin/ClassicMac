using System;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// The fields of HFS and HFS Plus keys and records read one at a time (hfs.md §1.8–§1.10, hfs-plus.md §2), named.
internal static class HfsRecords
{
    // A catalog key's parent ID (HFS and HFS Plus), or an HFS extents key's file ID: all at 2 (ckrParID, parentID,
    // xkrFNum).
    internal static uint KeyId(ReadOnlyMemory<byte> key) => new BigEndianReader(key).ReadUInt32At(2);

    // An HFS extents key's first allocation block in the fork (xkrFABN).
    internal static ushort ExtentsStart(ReadOnlyMemory<byte> key) => new BigEndianReader(key).ReadUInt16At(6);

    // An HFS folder record's ID (dirDirID).
    internal static uint FolderId(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt32At(6);

    // An HFS file record's ID (filFlNum).
    internal static uint FileId(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt32At(0x14);

    // An HFS thread record's parent ID (thdParID).
    internal static uint ThreadParentId(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt32At(0x0A);

    // A B-tree index record's child node.
    internal static uint IndexChild(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt32At(0);

    // An HFS Plus extents or attributes key's file ID (at 4, after the key length and the fork type or pad).
    internal static uint PlusKeyFileId(ReadOnlyMemory<byte> key) => new BigEndianReader(key).ReadUInt32At(4);

    // An HFS Plus extents key's first allocation block in the fork.
    internal static uint PlusExtentsStart(ReadOnlyMemory<byte> key) => new BigEndianReader(key).ReadUInt32At(8);

    // A catalog record's type word: HFS's cdrType and its reserved byte, HFS Plus's recordType.
    internal static ushort RecordType(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt16At(0);

    // An HFS Plus folder or file record's CNID.
    internal static uint PlusRecordId(ReadOnlyMemory<byte> data) => new BigEndianReader(data).ReadUInt32At(8);
}
