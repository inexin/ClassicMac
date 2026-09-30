using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads legacy StuffIt v1/v2 and StuffIt 5 archives and decompresses supported forks.</summary>
/// <remarks>
/// StuffIt does not have a published format specification. Legacy v1/v2 and v5 record layouts are fitted against
/// independent format references; legacy v1/v2 still need verification against archives written by the original
/// StuffIt application.
/// </remarks>
public sealed class StuffItReader : IContainerReader
{
    private const int ArchiveHeaderLength = 100;
    private const uint MemberSignature = 0xA5A5A5A5;
    private const byte FolderFlag = 0x40;
    private const byte EncryptedFlag = 0x20;
    private const ushort HasResourceForkFlag = 0x0001;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The built-in reader.</summary>
    public static StuffItReader Instance { get; } = new();

    private StuffItReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "StuffIt archive";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        if (input.Length < 22) return false;
        byte[] prefix = input.ReadPrefix(checked((int)Math.Min(input.Length, 83)));
        if (prefix.Length >= 83 && prefix.AsSpan(0, 8).SequenceEqual("StuffIt "u8) && prefix[82] == 5)
            return true;
        return IsLegacyV1OrV2(prefix);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanRead(input)) throw new InvalidDataException("Not a supported StuffIt archive.");

        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        if (IsLegacyV1(archive)) return ReadLegacyV1(archive, context);
        if (IsLegacyV2(archive)) return ReadLegacyV2(archive, context);
        if (archive.Length < ArchiveHeaderLength)
            throw new InvalidDataException("The StuffIt archive header is truncated.");

        uint reportedLength = U32(archive, 84);
        if (reportedLength != 0 && reportedLength > archive.Length)
            throw new InvalidDataException("The StuffIt archive's reported size extends past the input.");

        int rootCount = U16(archive, 92);
        int firstMember = ReadPosition(U32(archive, 94), "first member");
        if (rootCount > context.Options.MaxVolumeEntries)
            throw new InvalidDataException("The StuffIt archive exceeds the configured entry limit.");
        if (rootCount != 0 && (firstMember < ArchiveHeaderLength || firstMember >= archive.Length))
            throw new InvalidDataException("The first StuffIt archive member lies outside the archive.");

        var files = new List<MacFile>();
        var visited = new HashSet<int>();
        var pending = new Stack<MemberList>();
        pending.Push(new MemberList(firstMember, rootCount, [], []));
        int entriesRead = 0;
        long expandedBytes = 0;

        while (pending.Count > 0)
        {
            MemberList list = pending.Pop();
            if (list.Remaining == 0) continue;
            if (list.Position == 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.count-mismatch",
                    $"A StuffIt member list ends before its declared count of {list.Remaining} remaining entries.");
                continue;
            }
            if (!visited.Add(list.Position))
                throw new InvalidDataException("A StuffIt member link refers to an entry already visited.");
            if (++entriesRead > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The StuffIt archive exceeds the configured entry limit.");

            Member member = ParseMember(archive, list.Position, context);
            int remaining = list.Remaining - 1;
            if (remaining > 0)
            {
                if (member.Next == 0)
                    context.Report(DiagnosticSeverity.Warning, "archive.count-mismatch",
                        $"A StuffIt member list has {remaining} more declared entries but no next-member link.");
                else
                    pending.Push(new MemberList(member.Next, remaining, list.LegacyPath, list.UnicodePath));
            }

            if (member.IsFolder)
            {
                if (member.ChildCount > context.Options.MaxVolumeEntries)
                    throw new InvalidDataException("A StuffIt folder exceeds the configured entry limit.");
                if (member.ChildCount > 0)
                {
                    var legacyPath = new MacString[list.LegacyPath.Length + 1];
                    list.LegacyPath.CopyTo(legacyPath, 0);
                    legacyPath[^1] = LegacyName(member.Name);
                    var unicodePath = new string[list.UnicodePath.Length + 1];
                    list.UnicodePath.CopyTo(unicodePath, 0);
                    unicodePath[^1] = member.Name;
                    pending.Push(new MemberList(member.FirstChild, member.ChildCount, legacyPath, unicodePath));
                }
                continue;
            }

            if (member.Encrypted)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                    $"The encrypted StuffIt entry '{member.Name}' is listed but not opened.", list.Position);
                continue;
            }

            if (member.DataMethod is not (0 or 1 or 2 or 3 or 5 or 8 or 13 or 14 or 15) ||
                member.ResourceMethod is not (0 or 1 or 2 or 3 or 5 or 8 or 13 or 14 or 15 or null))
            {
                context.Report(DiagnosticSeverity.Warning, "archive.compression-unsupported",
                    $"The StuffIt entry '{member.Name}' uses an unsupported compression method.", list.Position);
                continue;
            }

            expandedBytes = checked(expandedBytes + member.DataLength + member.ResourceLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                throw new InvalidDataException("StuffIt extraction exceeds the configured expanded-size limit.");

            byte[] data = DecodeFork(archive, member.DataOffset, member.DataCompressedLength, member.DataLength,
                member.DataMethod);
            byte[] resource = member.ResourceMethod is { } resourceMethod
                ? DecodeFork(archive, member.ResourceOffset, member.ResourceCompressedLength, member.ResourceLength,
                    resourceMethod)
                : [];
            if (member.DataMethod != 15)
                CheckForkCrc(data, member.DataCrc, "data", member.Name, list.Position, context);
            if (member.ResourceMethod is not null and not 15)
                CheckForkCrc(resource, member.ResourceCrc, "resource", member.Name, list.Position, context);

            files.Add(new MacFile
            {
                Name = LegacyName(member.Name),
                UnicodeName = member.Name,
                FolderPath = list.LegacyPath,
                UnicodeFolderPath = list.UnicodePath,
                FinderInfo = member.FinderInfo,
                Created = member.Created,
                Modified = member.Modified,
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
            });
        }
        return files;
    }

    private static IReadOnlyList<MacFile> ReadLegacyV2(byte[] archive, ContainerContext context)
    {
        const int headerLength = 22;
        if (archive.Length < headerLength)
            throw new InvalidDataException("The legacy StuffIt archive header is truncated.");

        uint reportedLength = U32(archive, 6);
        if (reportedLength != 0 && reportedLength > archive.Length)
            throw new InvalidDataException("The legacy StuffIt archive's reported size extends past the input.");

        int rootCount = U16(archive, 4);
        int firstMember = ReadPosition(U32(archive, 16), "first legacy member");
        if (rootCount > context.Options.MaxVolumeEntries)
            throw new InvalidDataException("The legacy StuffIt archive exceeds the configured entry limit.");
        if (rootCount != 0 && (firstMember < headerLength || firstMember >= archive.Length))
            throw new InvalidDataException("The first legacy StuffIt member lies outside the archive.");

        var files = new List<MacFile>();
        var visited = new HashSet<int>();
        var pending = new Stack<LegacyMemberList>();
        pending.Push(new LegacyMemberList(firstMember, rootCount, []));
        int entriesRead = 0;
        long expandedBytes = 0;

        while (pending.Count > 0)
        {
            LegacyMemberList list = pending.Pop();
            if (list.Remaining == 0) continue;
            if (list.Position == 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.count-mismatch",
                    $"A legacy StuffIt member list ends before its declared count of {list.Remaining} remaining entries.");
                continue;
            }
            if (!visited.Add(list.Position))
                throw new InvalidDataException("A legacy StuffIt member link refers to an entry already visited.");
            if (++entriesRead > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The legacy StuffIt archive exceeds the configured entry limit.");

            LegacyMember member = ParseLegacyMember(archive, list.Position, context);
            int remaining = list.Remaining - 1;
            if (remaining > 0)
            {
                if (member.Next == 0)
                    context.Report(DiagnosticSeverity.Warning, "archive.count-mismatch",
                        $"A legacy StuffIt member list has {remaining} more declared entries but no next-member link.");
                else
                    pending.Push(new LegacyMemberList(member.Next, remaining, list.Path));
            }

            if (member.IsFolder)
            {
                if (member.ChildCount > context.Options.MaxVolumeEntries)
                    throw new InvalidDataException("A legacy StuffIt folder exceeds the configured entry limit.");
                if (member.ChildCount > 0)
                {
                    var path = new MacString[list.Path.Length + 1];
                    list.Path.CopyTo(path, 0);
                    path[^1] = member.Name;
                    pending.Push(new LegacyMemberList(member.FirstChild, member.ChildCount, path));
                }
                continue;
            }

            if (member.Encrypted)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                    $"The encrypted legacy StuffIt entry '{member.Name}' is listed but not opened.", list.Position);
                continue;
            }
            if (!IsSupportedMethod(member.DataMethod) || !IsSupportedMethod(member.ResourceMethod))
            {
                context.Report(DiagnosticSeverity.Warning, "archive.compression-unsupported",
                    $"The legacy StuffIt entry '{member.Name}' uses an unsupported compression method.", list.Position);
                continue;
            }

            expandedBytes = checked(expandedBytes + member.DataLength + member.ResourceLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                throw new InvalidDataException("Legacy StuffIt extraction exceeds the configured expanded-size limit.");

            byte[] resource = DecodeFork(archive, member.ResourceOffset, member.ResourceCompressedLength,
                member.ResourceLength, member.ResourceMethod);
            byte[] data = DecodeFork(archive, member.DataOffset, member.DataCompressedLength,
                member.DataLength, member.DataMethod);
            if (member.ResourceMethod != 15)
                CheckForkCrc(resource, member.ResourceCrc, "resource", member.Name.ToString(), list.Position, context);
            if (member.DataMethod != 15)
                CheckForkCrc(data, member.DataCrc, "data", member.Name.ToString(), list.Position, context);

            files.Add(new MacFile
            {
                Name = member.Name,
                FolderPath = list.Path,
                FinderInfo = member.FinderInfo,
                Created = Date(member.Created),
                Modified = Date(member.Modified),
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
            });
        }
        return files;
    }

    private static IReadOnlyList<MacFile> ReadLegacyV1(byte[] archive, ContainerContext context)
    {
        const int archiveHeaderLength = 22;
        const int memberHeaderLength = 112;
        uint reportedLength = U32(archive, 6);
        if (reportedLength != 0 && (reportedLength < archiveHeaderLength || reportedLength > archive.Length))
            throw new InvalidDataException("The legacy StuffIt archive's reported size is invalid.");
        int archiveEnd = reportedLength == 0 ? archive.Length : (int)reportedLength;

        var files = new List<MacFile>();
        var folderPath = new List<MacString>();
        int position = archiveHeaderLength;
        int entriesRead = 0;
        long expandedBytes = 0;
        while (position <= archiveEnd - memberHeaderLength)
        {
            if (++entriesRead > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The legacy StuffIt archive exceeds the configured entry limit.");

            ReadOnlySpan<byte> header = archive.AsSpan(position, memberHeaderLength);
            ushort expectedHeaderCrc = U16(header, 110);
            if (Crc16Arc(header[..110]) != expectedHeaderCrc)
                context.Report(DiagnosticSeverity.Warning, "archive.header-crc",
                    $"The legacy StuffIt member header checksum is incorrect at offset {position}.", position);

            byte resourceMethod = header[0];
            byte dataMethod = header[1];
            int nameLength = header[2];
            bool startsFolder = resourceMethod == 32 || dataMethod == 32;
            bool endsFolder = resourceMethod == 33 || dataMethod == 33;
            if (nameLength > 63 || (nameLength == 0 && !endsFolder))
                throw new InvalidDataException("A legacy StuffIt member name length is invalid.");
            MacString name = nameLength == 0 ? MacString.FromMacRoman("") : new MacString(header.Slice(3, nameLength));
            int resourceLength = ReadLength(U32(header, 84), "resource fork length");
            int dataLength = ReadLength(U32(header, 88), "data fork length");
            int resourceCompressedLength = ReadLength(U32(header, 92), "compressed resource fork length");
            int dataCompressedLength = ReadLength(U32(header, 96), "compressed data fork length");
            int payloadOffset = checked(position + memberHeaderLength);
            int payloadLength = startsFolder || endsFolder ? 0 :
                checked(resourceCompressedLength + dataCompressedLength);
            Require(archive, payloadOffset, payloadLength, "legacy StuffIt fork data");
            if (payloadOffset > archiveEnd - payloadLength)
                throw new InvalidDataException("Legacy StuffIt fork data extends past the declared archive size.");

            if (startsFolder)
            {
                if (nameLength == 0)
                    throw new InvalidDataException("A legacy StuffIt folder has an empty name.");
                folderPath.Add(name);
            }
            else if (endsFolder)
            {
                if (folderPath.Count == 0)
                    throw new InvalidDataException("A legacy StuffIt folder end marker has no matching folder.");
                folderPath.RemoveAt(folderPath.Count - 1);
            }
            else
            {
                bool encrypted = (resourceMethod & 0x10) != 0 || (dataMethod & 0x10) != 0;
                if (encrypted)
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                        $"The encrypted legacy StuffIt entry '{name}' is listed but not opened.", position);
                }
                else if (!IsSupportedMethod((byte)(resourceMethod & 0x0F)) ||
                         !IsSupportedMethod((byte)(dataMethod & 0x0F)))
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.compression-unsupported",
                        $"The legacy StuffIt entry '{name}' uses an unsupported compression method.", position);
                }
                else
                {
                    expandedBytes = checked(expandedBytes + resourceLength + dataLength);
                    if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                        throw new InvalidDataException("Legacy StuffIt extraction exceeds the configured expanded-size limit.");

                    byte resourceCompression = (byte)(resourceMethod & 0x0F);
                    byte dataCompression = (byte)(dataMethod & 0x0F);
                    byte[] resource = DecodeFork(archive, payloadOffset, resourceCompressedLength,
                        resourceLength, resourceCompression);
                    int dataOffset = checked(payloadOffset + resourceCompressedLength);
                    byte[] data = DecodeFork(archive, dataOffset, dataCompressedLength, dataLength, dataCompression);
                    if (resourceCompression != 15)
                        CheckForkCrc(resource, U16(header, 100), "resource", name.ToString(), position, context);
                    if (dataCompression != 15)
                        CheckForkCrc(data, U16(header, 102), "data", name.ToString(), position, context);

                    files.Add(new MacFile
                    {
                        Name = name,
                        FolderPath = [.. folderPath],
                        FinderInfo = new FinderInfo
                        {
                            Type = new FourCC(header.Slice(66, 4)),
                            Creator = new FourCC(header.Slice(70, 4)),
                            Flags = (FinderFlags)U16(header, 74),
                        },
                        Created = Date(U32(header, 76)),
                        Modified = Date(U32(header, 80)),
                        DataFork = ForkData.FromBytes(data),
                        ResourceFork = ForkData.FromBytes(resource),
                    });
                }
            }
            position = checked(payloadOffset + payloadLength);
        }

        if (position < archiveEnd && archive.AsSpan(position, archiveEnd - position).StartsWith("PEnd"u8))
            position += 4;
        if (folderPath.Count != 0)
            context.Report(DiagnosticSeverity.Warning, "archive.folder-unclosed",
                "The legacy StuffIt archive ends before all folders are closed.", position);
        if (position < archiveEnd && archiveEnd - position >= memberHeaderLength)
            throw new InvalidDataException("The legacy StuffIt archive contains an incomplete member record.");
        return files;
    }

    private static LegacyMember ParseLegacyMember(byte[] archive, int offset, ContainerContext context)
    {
        const int headerLength = 112;
        Require(archive, offset, headerLength, "legacy StuffIt member header");
        ReadOnlySpan<byte> header = archive.AsSpan(offset, headerLength);
        int nameLength = header[2];
        if (nameLength is < 1 or > 31)
            throw new InvalidDataException("A legacy StuffIt member name length is invalid.");

        ushort expectedHeaderCrc = U16(header, 110);
        if (Crc16Arc(header[..110]) != expectedHeaderCrc)
            context.Report(DiagnosticSeverity.Warning, "archive.header-crc",
                $"The legacy StuffIt member header checksum is incorrect at offset {offset}.", offset);

        int resourceLength = ReadLength(U32(header, 84), "resource fork length");
        int dataLength = ReadLength(U32(header, 88), "data fork length");
        int resourceCompressedLength = ReadLength(U32(header, 92), "compressed resource fork length");
        int dataCompressedLength = ReadLength(U32(header, 96), "compressed data fork length");
        int resourceOffset = checked(offset + headerLength);
        int dataOffset = checked(resourceOffset + resourceCompressedLength);
        Require(archive, resourceOffset, checked(resourceCompressedLength + dataCompressedLength),
            "legacy StuffIt fork data");

        uint firstChildRaw = U32(header, 62);
        bool isFolder = firstChildRaw != uint.MaxValue;
        int firstChild = isFolder && firstChildRaw != 0 ? ReadPosition(firstChildRaw, "first child") : 0;
        int next = ReadPosition(U32(header, 54), "next member");
        if (next != 0 && (next < headerLength || next >= archive.Length))
            throw new InvalidDataException("A legacy StuffIt next-member link lies outside the archive.");
        if (firstChild != 0 && (firstChild < headerLength || firstChild >= archive.Length))
            throw new InvalidDataException("A legacy StuffIt first-child link lies outside the archive.");

        var finderInfo = new FinderInfo
        {
            Type = new FourCC(header.Slice(66, 4)),
            Creator = new FourCC(header.Slice(70, 4)),
            Flags = (FinderFlags)U16(header, 74),
        };
        return new LegacyMember(
            new MacString(header.Slice(3, nameLength)),
            isFolder,
            firstChild,
            U16(header, 48),
            next,
            (header[0] & 0x10) != 0 || (header[1] & 0x10) != 0,
            checked((byte)(header[0] & 0x0F)),
            checked((byte)(header[1] & 0x0F)),
            resourceLength,
            dataLength,
            resourceCompressedLength,
            dataCompressedLength,
            resourceOffset,
            dataOffset,
            U16(header, 100),
            U16(header, 102),
            U32(header, 76),
            U32(header, 80),
            finderInfo);
    }

    private static bool IsLegacyV2(ReadOnlySpan<byte> input) =>
        input.Length >= 22 && input[..4].SequenceEqual("SIT!"u8) &&
        input.Slice(10, 4).SequenceEqual("rLau"u8) && input[14] == 2;

    private static bool IsLegacyV1(ReadOnlySpan<byte> input) =>
        input.Length >= 22 && input[..4].SequenceEqual("SIT!"u8) &&
        input.Slice(10, 4).SequenceEqual("rLau"u8) && input[14] == 1;

    private static bool IsLegacyV1OrV2(ReadOnlySpan<byte> input) => IsLegacyV1(input) || IsLegacyV2(input);

    private static bool IsSupportedMethod(byte method) =>
        method is 0 or 1 or 2 or 3 or 5 or 8 or 13 or 14 or 15;

    private static Member ParseMember(byte[] archive, int offset, ContainerContext context)
    {
        Require(archive, offset, 48, "StuffIt member header");
        if (U32(archive, offset) != MemberSignature)
            throw new InvalidDataException($"No StuffIt member header is present at offset {offset}.");
        int headerLength = U16(archive, offset + 6);
        if (headerLength is < 48 or > 2000)
            throw new InvalidDataException($"The StuffIt member header length {headerLength} is invalid.");
        Require(archive, offset, headerLength, "StuffIt member name and header");

        ushort expectedHeaderCrc = U16(archive, offset + 32);
        if (HeaderCrc(archive.AsSpan(offset, headerLength)) != expectedHeaderCrc)
            context.Report(DiagnosticSeverity.Warning, "archive.header-crc",
                $"The StuffIt member header checksum is incorrect at offset {offset}.", offset);

        byte flags = archive[offset + 9];
        bool isFolder = (flags & FolderFlag) != 0;
        int nameLength = U16(archive, offset + 30);
        int nameOffset;
        int firstChild = 0;
        int childCount = 0;
        uint dataLength = 0;
        uint dataCompressedLength = 0;
        ushort dataCrc = 0;
        byte dataMethod = 0;
        int dataPasswordLength = 0;
        if (isFolder)
        {
            firstChild = ReadPosition(U32(archive, offset + 34), "first child");
            childCount = U16(archive, offset + 46);
            nameOffset = offset + 48;
        }
        else
        {
            dataLength = U32(archive, offset + 34);
            dataCompressedLength = U32(archive, offset + 38);
            dataCrc = U16(archive, offset + 42);
            dataMethod = archive[offset + 46];
            dataPasswordLength = archive[offset + 47];
            nameOffset = checked(offset + 48 + dataPasswordLength);
        }
        if (nameOffset > offset + headerLength - nameLength)
            throw new InvalidDataException("A StuffIt member name extends past its header.");

        string name;
        try { name = StrictUtf8.GetString(archive.AsSpan(nameOffset, nameLength)); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("A StuffIt member name is not valid UTF-8.", e); }
        if (name.Length == 0 || name.Contains(':'))
            throw new InvalidDataException("A StuffIt member name is empty or contains a path separator.");

        uint nextRaw = U32(archive, offset + 22);
        if (nextRaw > int.MaxValue || (nextRaw != 0 && nextRaw >= archive.Length))
            throw new InvalidDataException("A StuffIt next-member link lies outside the archive.");

        var member = new Member
        {
            Name = name,
            IsFolder = isFolder,
            Next = (int)nextRaw,
            FirstChild = firstChild,
            ChildCount = childCount,
            Encrypted = (flags & EncryptedFlag) != 0 || dataPasswordLength != 0,
            DataLength = ReadLength(dataLength, "data-fork length"),
            DataCompressedLength = ReadLength(dataCompressedLength, "compressed data-fork length"),
            DataCrc = dataCrc,
            DataMethod = dataMethod,
            Created = Date(U32(archive, offset + 10)),
            Modified = Date(U32(archive, offset + 14)),
        };
        if (isFolder)
        {
            if (firstChild != 0 && (firstChild < ArchiveHeaderLength || firstChild >= archive.Length))
                throw new InvalidDataException("A StuffIt folder's first member lies outside the archive.");
            return member;
        }

        int headerEnd = checked(offset + headerLength);
        Require(archive, headerEnd, 36, "StuffIt Finder information");
        ushort fileFlags = U16(archive, headerEnd);
        var finder = new byte[FinderInfo.Length];
        archive.AsSpan(headerEnd + 4, 8).CopyTo(finder);
        U16(finder, 8, U16(archive, headerEnd + 12));
        int forkInfo = headerEnd + 36;
        int resourceLength = 0;
        int resourceCompressedLength = 0;
        ushort resourceCrc = 0;
        byte? resourceMethod = null;
        bool encrypted = member.Encrypted;
        if ((fileFlags & HasResourceForkFlag) != 0)
        {
            Require(archive, forkInfo, 14, "StuffIt resource-fork metadata");
            resourceLength = ReadLength(U32(archive, forkInfo), "resource-fork length");
            resourceCompressedLength = ReadLength(U32(archive, forkInfo + 4), "compressed resource-fork length");
            resourceCrc = U16(archive, forkInfo + 8);
            resourceMethod = archive[forkInfo + 12];
            int resourcePasswordLength = archive[forkInfo + 13];
            encrypted |= resourcePasswordLength != 0;
            forkInfo = checked(forkInfo + 14 + resourcePasswordLength);
        }

        long resourceOffsetLong = forkInfo;
        long dataOffsetLong = resourceOffsetLong + resourceCompressedLength;
        long forksEnd = dataOffsetLong + member.DataCompressedLength;
        int archiveEnd = member.Next == 0 ? archive.Length : member.Next;
        if (resourceOffsetLong > archiveEnd || forksEnd > archiveEnd)
            throw new InvalidDataException("A StuffIt member's fork data overlaps its next entry or exceeds the archive.");
        int resourceOffset = (int)resourceOffsetLong;
        int dataOffset = (int)dataOffsetLong;

        finder[0] = archive[headerEnd + 4];
        finder[1] = archive[headerEnd + 5];
        finder[2] = archive[headerEnd + 6];
        finder[3] = archive[headerEnd + 7];
        finder[4] = archive[headerEnd + 8];
        finder[5] = archive[headerEnd + 9];
        finder[6] = archive[headerEnd + 10];
        finder[7] = archive[headerEnd + 11];
        member.FinderInfo = FinderInfo.Read(finder);
        member.Encrypted = encrypted;
        member.ResourceLength = resourceLength;
        member.ResourceCompressedLength = resourceCompressedLength;
        member.ResourceCrc = resourceCrc;
        member.ResourceMethod = resourceMethod;
        member.ResourceOffset = resourceOffset;
        member.DataOffset = dataOffset;
        return member;
    }

    private static void CheckForkCrc(ReadOnlySpan<byte> bytes, ushort expected, string fork, string name, int offset,
        ContainerContext context)
    {
        if (Crc16Arc(bytes) != expected)
            context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                $"The StuffIt {fork} fork checksum is incorrect for '{name}'.", offset);
    }

    private static byte[] DecodeFork(byte[] archive, int offset, int compressedLength, int outputLength, byte method)
    {
        Require(archive, offset, compressedLength, "StuffIt compressed fork");
        if (method == 0)
        {
            if (compressedLength != outputLength)
                throw new InvalidDataException("A stored StuffIt fork has different stored and logical lengths.");
            return archive.AsSpan(offset, outputLength).ToArray();
        }
        ReadOnlySpan<byte> input = archive.AsSpan(offset, compressedLength);
        if (method == 2) return DecodeCompress(input, outputLength);
        if (method == 3) return DecodeHuffman(input, outputLength);
        if (method == 5) return DecodeLzah(input, outputLength);
        if (method == 8) return DecodeMw(input, outputLength);
        if (method == 13) return StuffItMethod13Decoder.Decode(input, outputLength);
        if (method == 14) return StuffItMethod14Decoder.Decode(input, outputLength);
        if (method == 15) return StuffItMethod15Decoder.Decode(input, outputLength);

        var output = new byte[outputLength];
        int written = 0;
        for (int index = 0; index < input.Length; index++)
        {
            byte value = input[index];
            if (value != 0x90)
            {
                if (written == output.Length) throw new InvalidDataException("StuffIt RLE90 output exceeds its declared size.");
                output[written++] = value;
                continue;
            }

            if (++index == input.Length)
                throw new InvalidDataException("A StuffIt RLE90 fork ends with an incomplete run marker.");
            byte count = input[index];
            if (count == 0)
            {
                if (written == output.Length) throw new InvalidDataException("StuffIt RLE90 output exceeds its declared size.");
                output[written++] = 0x90;
                continue;
            }
            if (written == 0) throw new InvalidDataException("A StuffIt RLE90 run has no preceding byte to repeat.");
            int additional = count - 1;
            if (additional > output.Length - written)
                throw new InvalidDataException("StuffIt RLE90 output exceeds its declared size.");
            output.AsSpan(written, additional).Fill(output[written - 1]);
            written += additional;
        }
        if (written != outputLength)
            throw new InvalidDataException($"StuffIt RLE90 produced {written} of {outputLength} declared bytes.");
        return output;
    }

    private static byte[] DecodeCompress(ReadOnlySpan<byte> input, int outputLength)
    {
        const int ClearCode = 256;
        const int FirstDictionaryCode = 257;
        const int MaximumCodeBits = 14;
        int maximumCodes = 1 << MaximumCodeBits;
        var prefix = new int[maximumCodes];
        Array.Fill(prefix, -1);
        var suffix = new byte[maximumCodes];
        for (int code = 0; code < 256; code++) suffix[code] = (byte)code;
        var phrase = new byte[maximumCodes];
        var output = new byte[outputLength];
        var reader = new LsbBitReader(input);
        int nextCode = FirstDictionaryCode;
        int codeBits = 9;
        int previousCode = -1;
        int codesInGroup = 0;
        int written = 0;

        while (reader.TryRead(codeBits, out int code))
        {
            codesInGroup = (codesInGroup + 1) & 7;
            if (code == ClearCode)
            {
                reader.Skip((8 - codesInGroup) % 8 * codeBits);
                nextCode = FirstDictionaryCode;
                codeBits = 9;
                previousCode = -1;
                codesInGroup = 0;
                continue;
            }

            if (previousCode < 0)
            {
                if (code >= FirstDictionaryCode)
                    throw new InvalidDataException("A StuffIt Compress fork starts with an invalid LZW code.");
                if (written == output.Length)
                    throw new InvalidDataException("StuffIt Compress output exceeds its declared fork length.");
                output[written++] = (byte)code;
                previousCode = code;
                continue;
            }

            if (code > nextCode)
                throw new InvalidDataException("A StuffIt Compress fork contains an invalid LZW code.");

            bool nextCodeCase = code == nextCode;
            int currentCode = nextCodeCase ? previousCode : code;
            int phraseLength = 0;
            if (nextCodeCase) phrase[phraseLength++] = FirstByte(previousCode, prefix);

            while (currentCode >= 256)
            {
                if (currentCode >= nextCode || prefix[currentCode] < 0 || phraseLength == phrase.Length)
                    throw new InvalidDataException("A StuffIt Compress fork contains an invalid LZW dictionary chain.");
                phrase[phraseLength++] = suffix[currentCode];
                currentCode = prefix[currentCode];
            }
            if (phraseLength == phrase.Length)
                throw new InvalidDataException("A StuffIt Compress LZW phrase is too long.");
            phrase[phraseLength++] = (byte)currentCode;
            byte firstByte = (byte)currentCode;
            if (phraseLength > output.Length - written)
                throw new InvalidDataException("StuffIt Compress output exceeds its declared fork length.");
            while (phraseLength > 0) output[written++] = phrase[--phraseLength];

            if (nextCode < maximumCodes)
            {
                prefix[nextCode] = previousCode;
                suffix[nextCode] = firstByte;
                nextCode++;
                if (codeBits < MaximumCodeBits && nextCode == 1 << codeBits) codeBits++;
            }
            previousCode = code;
        }

        if (written != output.Length)
            throw new InvalidDataException(
                $"StuffIt Compress produced {written} of {output.Length} declared bytes.");
        return output;
    }

    private static byte FirstByte(int code, int[] prefix)
    {
        while (code >= 256)
        {
            if (prefix[code] < 0)
                throw new InvalidDataException("A StuffIt Compress fork contains an invalid LZW dictionary chain.");
            code = prefix[code];
        }
        return (byte)code;
    }

    private static byte[] DecodeHuffman(ReadOnlySpan<byte> input, int outputLength)
    {
        var reader = new MsbBitReader(input);
        int nodeCount = 0;
        int leafCount = 0;
        HuffmanNode root = ReadHuffmanNode(ref reader, 0, ref nodeCount, ref leafCount);
        var output = new byte[outputLength];
        for (int index = 0; index < output.Length; index++)
        {
            HuffmanNode node = root;
            while (!node.IsLeaf)
                node = reader.ReadBit() ? node.One! : node.Zero!;
            output[index] = node.Symbol;
        }
        return output;
    }

    private static byte[] DecodeLzah(ReadOnlySpan<byte> input, int outputLength)
    {
        var output = new byte[outputLength];
        if (outputLength == 0) return output;

        var reader = new MsbBitReader(input);
        var tree = new LzahTree();
        byte[] window = CreateLzahWindow();
        int windowPosition = 0;
        int written = 0;
        while (written < output.Length)
        {
            int symbol = tree.ReadSymbol(ref reader);
            tree.Update(symbol);
            if (symbol < 256)
            {
                WriteLzahByte((byte)symbol, output, ref written, window, ref windowPosition);
                continue;
            }

            int length = symbol - 253;
            int offsetHigh = ReadLzahOffset(ref reader);
            int offsetLow = 0;
            for (int bit = 0; bit < 6; bit++) offsetLow = (offsetLow << 1) | (reader.ReadBit() ? 1 : 0);
            int distance = (offsetHigh << 6) | offsetLow;
            int source = (windowPosition - distance - 1) & 0xFFF;
            for (int index = 0; index < length && written < output.Length; index++)
            {
                byte value = window[source];
                source = (source + 1) & 0xFFF;
                WriteLzahByte(value, output, ref written, window, ref windowPosition);
            }
        }
        return output;
    }

    private static byte[] DecodeMw(ReadOnlySpan<byte> input, int outputLength)
    {
        const int FirstDictionaryCode = 256;
        const int MaximumDictionarySize = 16_385;
        var dictionary = new ushort[MaximumDictionarySize];
        var stack = new ushort[MaximumDictionarySize];
        var output = new byte[outputLength];
        var reader = new LsbBitReader(input);
        int written = 0;

        while (written < output.Length)
        {
            int nextCode = FirstDictionaryCode;
            int nextWidthBoundary = nextCode * 2;
            int codeWidth = 9;
            if (!reader.TryRead(codeWidth, out int code))
                throw new InvalidDataException("A StuffIt MW fork ends before its declared output length.");

            if (code < nextCode)
            {
                dictionary[FirstDictionaryCode - 1] = (ushort)code;
                WriteMwPhrase(code, nextCode, dictionary, stack, output, ref written);
            }
            else if (code != nextCode)
            {
                throw new InvalidDataException("A StuffIt MW fork starts a dictionary group with an invalid code.");
            }
            else
            {
                // A code equal to the first free slot starts a new code group.
                continue;
            }

            while (written < output.Length && reader.TryRead(codeWidth, out code) && code < nextCode)
            {
                if (nextCode < MaximumDictionarySize)
                    dictionary[nextCode++] = (ushort)code;
                WriteMwPhrase(code, nextCode, dictionary, stack, output, ref written);
                if (nextCode == nextWidthBoundary)
                {
                    nextWidthBoundary <<= 1;
                    codeWidth++;
                }
            }

            if (written == output.Length) break;
            if (code > nextCode)
                throw new InvalidDataException("A StuffIt MW fork contains an invalid dictionary code.");
            // A code equal to the next free dictionary slot ends this code group. The next
            // group starts with a fresh 9-bit dictionary, as in the original MW decoder.
            if (code == nextCode)
                continue;
            else
                throw new InvalidDataException("A StuffIt MW fork ends before its declared output length.");
        }

        return output;
    }

    private static void WriteMwPhrase(int code, int nextCode, ushort[] dictionary, ushort[] stack,
        byte[] output, ref int written)
    {
        int stackLength = 0;
        int pendingCode = code;
        while (true)
        {
            while (pendingCode >= 256)
            {
                if (pendingCode >= nextCode || pendingCode >= dictionary.Length || stackLength == stack.Length)
                    throw new InvalidDataException("A StuffIt MW fork contains an invalid dictionary chain.");
                stack[stackLength++] = dictionary[pendingCode];
                pendingCode = dictionary[pendingCode - 1];
            }

            if (written == output.Length)
                throw new InvalidDataException("StuffIt MW output exceeds its declared fork length.");
            output[written++] = (byte)pendingCode;
            if (stackLength == 0) return;
            pendingCode = stack[--stackLength];
        }
    }

    private static byte[] CreateLzahWindow()
    {
        var window = new byte[4096];
        int position = 18;
        for (int value = 0; value <= byte.MaxValue; value++)
            for (int repeat = 0; repeat < 13; repeat++) window[position++] = (byte)value;
        for (int value = 0; value <= byte.MaxValue; value++) window[position++] = (byte)value;
        for (int value = byte.MaxValue; value >= 0; value--) window[position++] = (byte)value;
        position += 128; // The volume-format seed leaves this region zero-filled.
        window.AsSpan(position, 110).Fill(0x20);
        return window;
    }

    private static void WriteLzahByte(byte value, byte[] output, ref int written, byte[] window,
        ref int windowPosition)
    {
        output[written++] = value;
        window[windowPosition] = value;
        windowPosition = (windowPosition + 1) & 0xFFF;
    }

    private static int ReadLzahOffset(ref MsbBitReader reader)
    {
        int code = 0;
        for (int length = 1; length <= 8; length++)
        {
            code = (code << 1) | (reader.ReadBit() ? 1 : 0);
            for (int value = 0; value < 64; value++)
                if (LzahOffsetCodeLengths[value] == length && LzahOffsetCodes[value] == code)
                    return value;
        }
        throw new InvalidDataException("A StuffIt LZAH offset code is invalid.");
    }

    private static readonly byte[] LzahOffsetCodeLengths = CreateLzahOffsetCodeLengths();
    private static readonly ushort[] LzahOffsetCodes = CreateLzahOffsetCodes();

    private static byte[] CreateLzahOffsetCodeLengths()
    {
        var lengths = new byte[64];
        for (int value = 0; value < lengths.Length; value++)
            lengths[value] = value switch
            {
                0 => 3,
                <= 3 => 4,
                <= 11 => 5,
                <= 23 => 6,
                <= 47 => 7,
                _ => 8,
            };
        return lengths;
    }

    private static ushort[] CreateLzahOffsetCodes()
    {
        var codes = new ushort[64];
        int code = 0;
        int previousLength = LzahOffsetCodeLengths[0];
        for (int value = 1; value < codes.Length; value++)
        {
            int length = LzahOffsetCodeLengths[value];
            code = (code + 1) << (length - previousLength);
            codes[value] = checked((ushort)code);
            previousLength = length;
        }
        return codes;
    }

    private sealed class LzahTree
    {
        private const int LeafCount = 314;
        private const int TreeSize = LeafCount * 2 - 1;
        private readonly int[] frequency = new int[TreeSize + 1];
        private readonly int[] forward = new int[TreeSize];
        private readonly int[] backward = new int[TreeSize + LeafCount];

        public LzahTree()
        {
            for (int symbol = 0; symbol < LeafCount; symbol++)
            {
                frequency[symbol] = 1;
                forward[symbol] = symbol + TreeSize;
                backward[symbol + TreeSize] = symbol;
            }
            for (int node = LeafCount, child = 0; node < TreeSize; node++, child += 2)
            {
                frequency[node] = frequency[child] + frequency[child + 1];
                forward[node] = child;
                backward[child] = backward[child + 1] = node;
            }
            frequency[TreeSize] = ushort.MaxValue;
        }

        public int ReadSymbol(ref MsbBitReader reader)
        {
            int node = forward[TreeSize - 1];
            while (node < TreeSize)
            {
                if (reader.ReadBit()) node++;
                node = forward[node];
            }
            return node - TreeSize;
        }

        public void Update(int symbol)
        {
            if (frequency[TreeSize - 1] >= 0x8000) Reorder();

            int node = backward[symbol + TreeSize];
            while (node != 0)
            {
                int weight = ++frequency[node];
                int swap = node + 1;
                if (frequency[swap] < weight)
                {
                    do { swap++; } while (frequency[swap] < weight);
                    swap--;
                    frequency[node] = frequency[swap];
                    frequency[swap] = weight;

                    int child = forward[node];
                    backward[child] = swap;
                    if (child < TreeSize) backward[child + 1] = swap;
                    forward[node] = forward[swap];
                    forward[swap] = child;
                    child = forward[node];
                    backward[child] = node;
                    if (child < TreeSize) backward[child + 1] = node;
                    node = swap;
                }
                node = backward[node];
            }
        }

        private void Reorder()
        {
            int leaf = 0;
            for (int node = 0; node < TreeSize; node++)
            {
                if (forward[node] < TreeSize) continue;
                frequency[leaf] = (frequency[node] + 1) >> 1;
                forward[leaf++] = forward[node];
            }

            int nextNode = LeafCount;
            for (int child = 0; child < TreeSize - 1; child += 2, nextNode++)
            {
                int combinedFrequency = frequency[child] + frequency[child + 1];
                int insertAt = nextNode - 1;
                while (insertAt >= 0 && combinedFrequency < frequency[insertAt]) insertAt--;
                insertAt++;
                Array.Copy(frequency, insertAt, frequency, insertAt + 1, nextNode - insertAt);
                Array.Copy(forward, insertAt, forward, insertAt + 1, nextNode - insertAt);
                frequency[insertAt] = combinedFrequency;
                forward[insertAt] = child;
            }

            for (int node = 0; node < TreeSize; node++)
            {
                int child = forward[node];
                backward[child] = node;
                if (child < TreeSize) backward[child + 1] = node;
            }
        }
    }

    private static HuffmanNode ReadHuffmanNode(ref MsbBitReader reader, int depth, ref int nodeCount,
        ref int leafCount)
    {
        if (depth > 255 || ++nodeCount > 511)
            throw new InvalidDataException("A StuffIt Huffman code tree is too large.");
        if (reader.ReadBit())
        {
            if (++leafCount > 256)
                throw new InvalidDataException("A StuffIt Huffman code tree has too many symbols.");
            return new HuffmanNode(reader.ReadByte());
        }
        HuffmanNode zero = ReadHuffmanNode(ref reader, depth + 1, ref nodeCount, ref leafCount);
        HuffmanNode one = ReadHuffmanNode(ref reader, depth + 1, ref nodeCount, ref leafCount);
        return new HuffmanNode(zero, one);
    }

    private sealed class HuffmanNode
    {
        public HuffmanNode(byte symbol)
        {
            Symbol = symbol;
            IsLeaf = true;
        }

        public HuffmanNode(HuffmanNode zero, HuffmanNode one)
        {
            Zero = zero;
            One = one;
        }

        public bool IsLeaf { get; }
        public byte Symbol { get; }
        public HuffmanNode? Zero { get; }
        public HuffmanNode? One { get; }
    }

    private ref struct MsbBitReader
    {
        private readonly ReadOnlySpan<byte> input;
        private long bitOffset;

        public MsbBitReader(ReadOnlySpan<byte> input) => this.input = input;

        public bool ReadBit()
        {
            if (bitOffset >= (long)input.Length * 8)
                throw new InvalidDataException("A StuffIt Huffman fork ends inside its code tree or data.");
            bool value = (input[checked((int)(bitOffset >> 3))] & (0x80 >> (int)(bitOffset & 7))) != 0;
            bitOffset++;
            return value;
        }

        public byte ReadByte()
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++) value = (byte)((value << 1) | (ReadBit() ? 1 : 0));
            return value;
        }
    }

    private ref struct LsbBitReader
    {
        private readonly ReadOnlySpan<byte> input;
        private long bitOffset;

        public LsbBitReader(ReadOnlySpan<byte> input) => this.input = input;

        public bool TryRead(int bitCount, out int value)
        {
            if ((long)input.Length * 8 - bitOffset < bitCount)
            {
                value = 0;
                return false;
            }

            value = 0;
            for (int bit = 0; bit < bitCount; bit++, bitOffset++)
                value |= ((input[(int)(bitOffset >> 3)] >> (int)(bitOffset & 7)) & 1) << bit;
            return true;
        }

        public void Skip(int bitCount) => bitOffset = Math.Min((long)input.Length * 8, bitOffset + bitCount);
    }

    // StuffIt's CRC-16/ARC variant is identified independently in Deark's v5 reader; this checksum only reports
    // damaged fork data and does not prevent extraction.
    private static ushort HeaderCrc(ReadOnlySpan<byte> header)
    {
        ushort crc = 0;
        for (int index = 0; index < header.Length; index++)
            crc = CrcByte(crc, index is 32 or 33 ? (byte)0 : header[index]);
        return crc;
    }

    private static ushort Crc16Arc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes) crc = CrcByte(crc, value);
        return crc;
    }

    private static ushort CrcByte(ushort crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        return crc;
    }

    private static MacString LegacyName(string name)
    {
        try { return MacString.FromMacRoman(name); }
        catch (ArgumentException) { return MacString.FromMacRoman("?"); }
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private static void Require(byte[] archive, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > archive.Length - length)
            throw new InvalidDataException($"The {what} lies outside the StuffIt archive.");
    }

    private static int ReadPosition(uint value, string what)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"The StuffIt {what} offset is too large.");
        return (int)value;
    }

    private static int ReadLength(uint value, string what)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"The StuffIt {what} exceeds the supported size.");
        return (int)value;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
    private static void U16(Span<byte> bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

    private readonly record struct MemberList(int Position, int Remaining, MacString[] LegacyPath, string[] UnicodePath);
    private readonly record struct LegacyMemberList(int Position, int Remaining, MacString[] Path);

    private readonly record struct LegacyMember(
        MacString Name,
        bool IsFolder,
        int FirstChild,
        int ChildCount,
        int Next,
        bool Encrypted,
        byte ResourceMethod,
        byte DataMethod,
        int ResourceLength,
        int DataLength,
        int ResourceCompressedLength,
        int DataCompressedLength,
        int ResourceOffset,
        int DataOffset,
        ushort ResourceCrc,
        ushort DataCrc,
        uint Created,
        uint Modified,
        FinderInfo FinderInfo);

    private sealed class Member
    {
        public required string Name { get; init; }
        public bool IsFolder { get; init; }
        public bool Encrypted { get; set; }
        public int Next { get; init; }
        public int FirstChild { get; init; }
        public int ChildCount { get; init; }
        public int DataLength { get; init; }
        public int DataCompressedLength { get; init; }
        public ushort DataCrc { get; init; }
        public byte DataMethod { get; init; }
        public int DataOffset { get; set; }
        public int ResourceLength { get; set; }
        public int ResourceCompressedLength { get; set; }
        public ushort ResourceCrc { get; set; }
        public byte? ResourceMethod { get; set; }
        public int ResourceOffset { get; set; }
        public FinderInfo FinderInfo { get; set; } = FinderInfo.Empty;
        public MacDate? Created { get; init; }
        public MacDate? Modified { get; init; }
    }
}
