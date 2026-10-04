using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// The fields of HFS keys and catalog records the writer reads one at a time (hfs.md §1.8–§1.10), named.
internal static class HfsRecords
{
    // A catalog key's parent ID, or an extents key's file ID: both at 2 (ckrParID, xkrFNum).
    internal static uint KeyId(byte[] key) => new BigEndianReader(key).ReadUInt32At(2);

    // An extents key's first allocation block in the fork (xkrFABN).
    internal static ushort ExtentsStart(byte[] key) => new BigEndianReader(key).ReadUInt16At(6);

    // A folder record's ID (dirDirID).
    internal static uint FolderId(byte[] data) => new BigEndianReader(data).ReadUInt32At(6);

    // A file record's ID (filFlNum).
    internal static uint FileId(byte[] data) => new BigEndianReader(data).ReadUInt32At(0x14);
}
