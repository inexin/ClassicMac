using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Alias records ('alis', docs/formats/resources/aliases.md §1) made in code: the version 2 record and its tagged data.
internal static class AliasBuilder
{
    public const uint VolumeCreated = 0xB0000000, TargetCreated = 0xB1000000;

    public static byte[] Alias(string volume, uint parentId, string name, uint targetId, string type = "TEXT", string creator = "ttxt",
        short kind = 0, string? path = null, string? parentName = null, uint[]? folderIds = null, short version = 2,
        bool terminate = true, (short Tag, byte[] Data)[]? extras = null, uint volumeCreated = VolumeCreated)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(0);                                   // userType
        w.WriteUInt16(0);                                   // aliasSize, patched below
        w.WriteInt16(version);
        w.WriteInt16(kind);
        Str(w, volume, 28);
        w.WriteUInt32(volumeCreated);
        w.WriteUInt16(0x4244);                              // 'BD': HFS
        w.WriteInt16(0);                                    // drive type: fixed disk
        w.WriteUInt32(parentId);
        Str(w, name, 64);
        w.WriteUInt32(targetId);
        w.WriteUInt32(TargetCreated);
        w.WriteFourCC(FourCC.FromString(type));
        w.WriteFourCC(FourCC.FromString(creator));
        w.WriteInt16(1);                                    // levels from
        w.WriteInt16(2);                                    // levels to
        w.WriteUInt32(0);                                   // volume attributes
        w.WriteInt16(0);                                    // volume file-system ID
        w.WriteZeros(10);
        if (parentName is not null)
        {
            Extra(w, 0, MacRoman.Encode(parentName));
        }

        if (folderIds is not null)
        {
            var ids = new BigEndianWriter();
            foreach (var id in folderIds)
            {
                ids.WriteUInt32(id);
            }
            Extra(w, 1, ids.ToArray());
        }

        if (path is not null)
        {
            Extra(w, 2, MacRoman.Encode(path));
        }

        foreach (var (tag, data) in extras ?? [])
        {
            Extra(w, tag, data);
        }

        if (terminate)
        {
            w.WriteInt16(-1);
            w.WriteUInt16(0);
        }

        w.WriteUInt16At(4, w.Length);
        return w.ToArray();
    }

    private static void Str(BigEndianWriter w, string text, int field)
    {
        var bytes = MacRoman.Encode(text);
        w.WriteByte((byte)bytes.Length);
        w.WriteBytes(bytes);
        w.WriteZeros(field - 1 - bytes.Length);
    }

    private static void Extra(BigEndianWriter w, short tag, byte[] data)
    {
        w.WriteInt16(tag);
        w.WriteUInt16((ushort)data.Length);
        w.WriteBytes(data);
        if (data.Length % 2 != 0)
        {
            w.WriteByte(0);
        }
    }

    // A resource fork holding 'alis' 0.
    public static byte[] Fork(byte[] alias)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("alis"), 0, alias));
        return fork.ToArray();
    }
}
