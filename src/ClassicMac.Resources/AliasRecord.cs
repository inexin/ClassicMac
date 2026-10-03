using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources
{
    /// <summary>What an alias stands for (the record's <c>kind</c> word).</summary>
    public enum AliasKind : short
    {
        /// <summary>A file.</summary>
        File = 0,

        /// <summary>A folder (or a volume, as its root folder).</summary>
        Folder = 1,
    }

    /// <summary>
    /// Where a network alias's volume is (aliases.md §1.3): the AppleShare zone, server, volume and user, from the alias's
    /// volume mount information (an <c>AFPVolMountInfo</c>, tag 9) or its older tags (3 zone, 4 server, 5 user); each null
    /// when the alias does not say.
    /// </summary>
    public sealed record AliasNetwork(string? Zone, string? Server, string? Volume, string? User);

    /// <summary>One item of an alias record's tagged data: its tag and bytes.</summary>
    /// <param name="Tag">The tag (0 the parent folder's name, 1 the folder IDs, 2 the full path, …; −1 ends the list).</param>
    /// <param name="Data">The item's bytes, without the pad byte.</param>
    public sealed record AliasExtra(short Tag, ReadOnlyMemory<byte> Data)
    {
        /// <summary>What the tag holds, in words, for the tags known; null for others.</summary>
        public string? Name => Tag switch
        {
            AliasRecord.ParentNameTag => "parent folder name",
            AliasRecord.FolderIdsTag => "folder IDs",
            AliasRecord.FullPathTag => "full path",
            3 => "AppleShare zone",
            AliasRecord.ServerNameTag => "server name",
            5 => "user name",
            6 => "driver name",
            7 => "auxiliary remote information",
            8 => "auxiliary folder IDs",
            9 => "volume mount information",
            _ => null,
        };
    }

    /// <summary>
    /// An alias record (<c>'alis'</c>, docs/formats/resources/aliases.md): the public header (<c>userType</c>,
    /// <c>aliasSize</c>; <i>Inside Macintosh: Files</i>, Alias Manager) and the version 2 record the Alias Manager keeps
    /// after it: the target's volume, parent folder, name, ID, dates and Finder type, then tagged data (the parent folder's
    /// name, the folder IDs up to the root, the full path and others). The private part is verified on Mac OS 9's aliases.
    /// </summary>
    public sealed record AliasRecord
    {
        /// <summary>The tagged item holding the parent folder's name.</summary>
        public const short ParentNameTag = 0;

        /// <summary>The tagged item holding the IDs of the folders from the parent up (each a 4-byte ID).</summary>
        public const short FolderIdsTag = 1;

        /// <summary>The tagged item holding the full path, "Volume:Folder:…:Name".</summary>
        public const short FullPathTag = 2;

        /// <summary>The tagged item holding the AppleShare server's name.</summary>
        public const short ServerNameTag = 4;

        /// <summary>The length of the fixed part before the tagged data.</summary>
        public const int FixedLength = 150;

        /// <summary>The application's own type (<c>userType</c>, +$00); zero in the Finder's aliases.</summary>
        public FourCC UserType { get; init; }

        /// <summary>The record's length as stored (<c>aliasSize</c>, +$04).</summary>
        public ushort Size { get; init; }

        /// <summary>The record's version (+$06); 2 is the one read.</summary>
        public short Version { get; init; }

        /// <summary>File or folder (+$08).</summary>
        public AliasKind Kind { get; init; }

        /// <summary>The target's volume name (+$0A, a Str27).</summary>
        public MacString VolumeName { get; init; }

        /// <summary>The volume's creation date (+$26), which tells volumes of one name apart; null when zero.</summary>
        public MacDate? VolumeCreated { get; init; }

        /// <summary>The volume's signature (+$2A): <c>'BD'</c> HFS, <c>'H+'</c> HFS Plus, <c>$D2D7</c> MFS.</summary>
        public ushort VolumeSignature { get; init; }

        /// <summary>The kind of volume (+$2C): 0 hard disk, 1 foreign or AppleShare, 2 400K, 3 800K, 4 1.4M, 5 other ejectable; −1 in an alias made from a full path alone.</summary>
        public short VolumeType { get; init; }

        /// <summary>The ID of the folder holding the target (+$2E); 1 for a volume alias; −1 (as 0xFFFFFFFF) in an alias made from a full path alone.</summary>
        public uint ParentId { get; init; }

        /// <summary>The target's name (+$32, a Str63).</summary>
        public MacString Name { get; init; }

        /// <summary>The target's file number (catalog ID), or a folder's directory ID (+$72); 2 for a volume.</summary>
        public uint TargetId { get; init; }

        /// <summary>The target's creation date (+$76); null when zero.</summary>
        public MacDate? TargetCreated { get; init; }

        /// <summary>The target's file type (+$7A); zero for a folder.</summary>
        public FourCC Type { get; init; }

        /// <summary>The target's creator (+$7E).</summary>
        public FourCC Creator { get; init; }

        /// <summary>Folder levels from the alias up to the folder shared with the target (+$82); −1 when not recorded.</summary>
        public short LevelsFrom { get; init; }

        /// <summary>Folder levels from that folder down to the target (+$84); −1 when not recorded.</summary>
        public short LevelsTo { get; init; }

        /// <summary>The volume's attributes (+$86): bit 0 mount information recorded, 1 ejectable, 2 auxiliary remote information, 3 auxiliary folder IDs, 4 AFP media.</summary>
        public uint VolumeAttributes { get; init; }

        /// <summary>The volume's file-system ID (+$8A); 0 for the File Manager's own volumes.</summary>
        public short VolumeFileSystemId { get; init; }

        /// <summary>The tagged data, in order (the end tag left out).</summary>
        public IReadOnlyList<AliasExtra> Extras { get; init; } = [];

        /// <summary>The signature as two characters ("BD", "H+"), or as hex when not printable.</summary>
        public string VolumeSignatureText =>
            VolumeSignature >> 8 is >= 0x20 and < 0x7F && (VolumeSignature & 0xFF) is >= 0x20 and < 0x7F
                ? $"{(char)(VolumeSignature >> 8)}{(char)(VolumeSignature & 0xFF)}"
                : $"${VolumeSignature:X4}";

        /// <summary>The text of the first tagged item with <paramref name="tag"/>, or null.</summary>
        public MacString? Extra(short tag) => Extras.FirstOrDefault(e => e.Tag == tag) is { } extra ? new MacString(extra.Data.Span) : null;

        /// <summary>The parent folder's name (tag 0), or null.</summary>
        public MacString? ParentName => Extra(ParentNameTag);

        /// <summary>The full path (tag 2), "Volume:Folder:…:Name", or null.</summary>
        public MacString? FullPath => Extra(FullPathTag);

        /// <summary>The IDs of the folders from the target's parent up to the root's child (tag 1); empty when not recorded.</summary>
        public IReadOnlyList<uint> FolderIds
        {
            get
            {
                if (Extras.FirstOrDefault(e => e.Tag == FolderIdsTag) is not { } extra)
                {
                    return [];
                }

                var reader = new BigEndianReader(extra.Data);
                return [.. Enumerable.Range(0, extra.Data.Length / 4).Select(i => reader.ReadUInt32At(i * 4))];
            }
        }

        /// <summary>
        /// The kind of disk the target's volume was, from the volume type (aliases.md §1.1): "hard disk", "network volume",
        /// "400K floppy disk", "800K floppy disk", "1.4 MB floppy disk", "removable disk"; null for −1 (an alias made from a
        /// full path) and types not known.
        /// </summary>
        public string? VolumeKindName => VolumeType switch
        {
            0 => "hard disk",
            1 => "network volume",
            2 => "400K floppy disk",
            3 => "800K floppy disk",
            4 => "1.4 MB floppy disk",
            5 => "removable disk",
            _ => null,
        };

        /// <summary>
        /// Where the target's network volume is, when it is on one: the volume type is 1 (AppleShare), the volume attributes
        /// say AFP media (bit 4), or the alias holds AFP mount information (tag 9, media <c>'afpm'</c>); null otherwise.
        /// The names come from the mount information [Doc: Inside Macintosh: Files, AFPVolMountInfo], else the older tags
        /// 3–5, the volume's from the record.
        /// </summary>
        public AliasNetwork? Network
        {
            get
            {
                var mount = Extras.FirstOrDefault(e => e.Tag == 9) is { } extra ? AfpMount(extra.Data) : null;
                if (VolumeType != 1 && (VolumeAttributes & 0x10) == 0 && mount is null)
                {
                    return null;
                }

                string? Text(short tag) => Extra(tag)?.ToMacRoman() is { Length: > 0 } text ? text : null;
                return new AliasNetwork(mount?.Zone ?? Text(3), mount?.Server ?? Text(ServerNameTag),
                    mount?.Volume ?? (VolumeName.Length > 0 ? VolumeName.ToMacRoman() : null), mount?.User ?? Text(5));
            }
        }

        // An AFPVolMountInfo: length, media 'afpm', flags, NBP interval and count, UAM type, then (at 12) the offsets (from the
        // record's start) of the zone, server, volume and user names, Pascal strings; null when it is not one or is cut.
        private static AliasNetwork? AfpMount(ReadOnlyMemory<byte> data)
        {
            if (data.Length < 24)
            {
                return null;
            }

            var reader = new BigEndianReader(data);
            if (reader.ReadFourCCAt(2) != FourCC.FromString("afpm"))
            {
                return null;
            }

            string? Name(int at)
            {
                int offset = reader.ReadInt16At(at);
                if (offset <= 0 || offset >= data.Length)
                {
                    return null;
                }

                int length = data.Span[offset];
                return length == 0 || offset + 1 + length > data.Length ? null : MacRoman.Decode(data.Span.Slice(offset + 1, length));
            }

            return new AliasNetwork(Name(12), Name(14), Name(16), Name(18));
        }

        /// <summary>Whether the alias stands for a volume (a folder alias whose parent is 1, the root's parent).</summary>
        public bool IsVolume => Kind == AliasKind.Folder && ParentId == 1;

        /// <summary>
        /// Where the alias points, as the Finder's Get Info shows it ("Original:", aliases.md §2.2): the volume, the folders
        /// and the target's name joined by ": " ("Mac OS 9: System Folder: Control Panels"); a volume alias is the volume
        /// alone. The folders are the full path's (tag 2) nearest the target, as many as the folder IDs (tag 1) count, so
        /// an alias without them shows "Volume: name". [Code: Finder GetAliasInfo]
        /// </summary>
        public string TargetPath
        {
            get
            {
                var volume = VolumeName.ToMacRoman();
                if (IsVolume)
                {
                    return volume;
                }

                var folders = FullPath is { } path
                    ? path.ToMacRoman().Split(':').Skip(1).SkipLast(1).Where(n => n.Length > 0).ToList()
                    : [];
                var count = Math.Min(FolderIds.Count, folders.Count);
                return string.Join(": ", new[] { volume }.Concat(folders.Skip(folders.Count - count)).Append(Name.ToMacRoman()));
            }
        }

        /// <summary>
        /// Reads a record (aliases.md §1): the fixed part, then tagged items (tag, length, data padded to even) until the
        /// end tag −1. <paramref name="complete"/> is false when the end tag is missing or an item runs past the data; the
        /// items read whole are kept.
        /// </summary>
        /// <exception cref="InvalidDataException">The data is shorter than the fixed part, or the version is not 2.</exception>
        public static AliasRecord Read(ReadOnlyMemory<byte> data, out bool complete)
        {
            if (data.Length < FixedLength)
            {
                throw new InvalidDataException($"An alias record is at least {FixedLength} bytes; this one is {data.Length}.");
            }

            var reader = new BigEndianReader(data);
            var version = reader.ReadInt16At(0x06);
            if (version != 2)
            {
                throw new InvalidDataException($"Alias record version {version} is not read (only version 2).");
            }

            var extras = new List<AliasExtra>();
            complete = false;
            var at = FixedLength;
            while (at <= data.Length - 4)
            {
                var tag = reader.ReadInt16At(at);
                var length = reader.ReadUInt16At(at + 2);
                if (tag == -1)
                {
                    complete = true;
                    break;
                }

                if (length > data.Length - at - 4)
                {
                    break;
                }

                extras.Add(new AliasExtra(tag, data.Slice(at + 4, length)));
                at += 4 + length + (length & 1);
            }

            return new AliasRecord
            {
                UserType = reader.ReadFourCCAt(0x00),
                Size = reader.ReadUInt16At(0x04),
                Version = version,
                Kind = (AliasKind)reader.ReadInt16At(0x08),
                VolumeName = Str(reader, 0x0A, 27),
                VolumeCreated = Date(reader.ReadUInt32At(0x26)),
                VolumeSignature = reader.ReadUInt16At(0x2A),
                VolumeType = reader.ReadInt16At(0x2C),
                ParentId = reader.ReadUInt32At(0x2E),
                Name = Str(reader, 0x32, 63),
                TargetId = reader.ReadUInt32At(0x72),
                TargetCreated = Date(reader.ReadUInt32At(0x76)),
                Type = reader.ReadFourCCAt(0x7A),
                Creator = reader.ReadFourCCAt(0x7E),
                LevelsFrom = reader.ReadInt16At(0x82),
                LevelsTo = reader.ReadInt16At(0x84),
                VolumeAttributes = reader.ReadUInt32At(0x86),
                VolumeFileSystemId = reader.ReadInt16At(0x8A),
                Extras = extras,
            };
        }

        // A Pascal string in a field of max + 1 bytes; a length past the field is cut to it.
        private static MacString Str(BigEndianReader reader, int offset, int max) =>
            new(reader.ReadBytesAt(offset + 1, Math.Min(reader.ReadByteAt(offset), max)));

        private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
    }
}
