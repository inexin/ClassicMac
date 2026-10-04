using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Checksums;

namespace ClassicMac.Files.Archives;

/// <summary>
/// Zip archives (PKWARE's APPNOTE.TXT): stored and DEFLATE entries, ZIP64 sizes and offsets, with the Mac data zips
/// carry: AppleDouble <c>._name</c> entries beside the file or under <c>__MACOSX/</c> (Mac OS X's Archive Utility), the
/// Info-ZIP Macintosh extra fields (old <c>0x07c8</c>, new <c>0x334d</c> "M3") and ZipIt's (<c>0x2605</c>,
/// <c>0x2705</c>), from Info-ZIP's <c>proginfo/extrafld.txt</c>. Zip's own fields are little-endian; the Mac fields of
/// <c>0x07c8</c> and ZipIt's are big-endian.
/// </summary>
public sealed class ZipReader : IContainerReader
{
    private const uint LocalSignature = 0x04034B50, CentralSignature = 0x02014B50, EndSignature = 0x06054B50,
        Zip64EndSignature = 0x06064B50, Zip64LocatorSignature = 0x07064B50;
    private const int LocalHeaderLength = 30, CentralHeaderLength = 46, EndLength = 22, Zip64EndLength = 56;
    private const ushort FlagEncrypted = 1, FlagUtf8 = 1 << 11;
    private const int HostMsDos = 0, HostUnix = 3, HostMacintosh = 7;

    // Extra-field tags (extrafld.txt).
    private const ushort Zip64Tag = 0x0001, OldMacTag = 0x07C8, Mac3Tag = 0x334D, ZipItTag = 0x2605,
        ZipItFileTag = 0x2705, ExtendedTimeTag = 0x5455;

    // Info-ZIP's Mac port stores a resource fork as its own entry, under this folder [Fitted: Info-ZIP Zip's
    // macos/source ResourceMark "XtraStuf.mac:"; the extra-field document does not say where the fork goes].
    private const string ResourceFolder = "XtraStuf.mac";

    /// <summary>The built-in zip reader.</summary>
    public static ZipReader Instance { get; } = new();

    private ZipReader() { }

    /// <inheritdoc/>
    public string FormatName => "Zip";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        var prefix = input.ReadPrefix(4);
        if (prefix.Length < 4)
        {
            return false;
        }

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        return signature == LocalSignature || signature == EndSignature && input.Length >= EndLength;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        if (!CanRead(input))
        {
            throw new InvalidDataException("Not a zip archive.");
        }

        var directory = ReadEndOfCentralDirectory(input);
        if (directory.Size > input.Length || directory.Size > context.Options.MaxExpandedBytesPerInput)
        {
            throw new InvalidDataException("The zip central directory is larger than the archive.");
        }

        var central = input.Slice(directory.Offset, directory.Size).ToArray();

        var entries = new List<UnixArchiveEntry>();
        long expanded = 0;
        var at = 0;
        for (long index = 0; index < directory.Count; index++)
        {
            if (index >= context.Options.MaxVolumeEntries)
            {
                throw new InvalidDataException("The zip archive exceeds the configured entry limit.");
            }

            if (at > central.Length - CentralHeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(at)) != CentralSignature)
            {
                context.Report(DiagnosticSeverity.Error, "archive.count-mismatch",
                    $"The zip central directory lists {directory.Count} entries but ends after {index}.", directory.Offset + at);
                break;
            }
            var entry = ReadCentralEntry(central, at, directory.Bias);
            at = entry.Next;
            var read = ReadEntry(input, entry, context, ref expanded);
            if (read is not null)
            {
                entries.Add(read);
            }
        }
        return UnixArchive.ToMacFiles(entries, context, FormatName);
    }

    private readonly record struct Directory(long Count, long Size, long Offset, long Bias);

    // The end-of-central-directory record is the last thing in the archive, followed only by its comment (up to 65535
    // bytes); data before the archive (a self-extractor's code) shifts every offset by the same bias.
    private static Directory ReadEndOfCentralDirectory(ForkData input)
    {
        var tailLength = (int)Math.Min(input.Length, EndLength + ushort.MaxValue);
        var tailStart = input.Length - tailLength;
        var tail = input.Slice(tailStart, tailLength).ToArray();
        var end = -1;
        for (var i = tail.Length - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature &&
                BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) <= tail.Length - EndLength - i)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            throw new InvalidDataException("The zip archive has no end-of-central-directory record.");
        }

        var record = tail.AsSpan(end);
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0)
        {
            throw new InvalidDataException("Multi-disk (split or spanned) zip archives are not supported.");
        }

        long count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        long endPosition = tailStart + end;

        if ((count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue) && end >= 20 &&
            BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end - 20)) == Zip64LocatorSignature)
        {
            var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(end - 20 + 8));
            if (zip64Offset > (ulong)(input.Length - Zip64EndLength))
            {
                throw new InvalidDataException("The ZIP64 end record lies outside the archive.");
            }

            var zip64 = input.Slice((long)zip64Offset, Zip64EndLength).ToArray();
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndSignature)
            {
                throw new InvalidDataException("The ZIP64 end record is missing.");
            }

            var total = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
            var zip64Size = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            var zip64Start = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
            if (total > int.MaxValue || zip64Size > (ulong)input.Length || zip64Start > (ulong)input.Length)
            {
                throw new InvalidDataException("The ZIP64 end record has impossible sizes.");
            }

            count = (long)total;
            size = (long)zip64Size;
            offset = (long)zip64Start;
            endPosition = (long)zip64Offset;
        }

        var bias = endPosition - (offset + size);
        if (bias < 0)
        {
            throw new InvalidDataException("The zip central directory overlaps its end record.");
        }

        return new Directory(count, size, offset + bias, bias);
    }

    private sealed record CentralEntry(int Next, int Host, ushort Flags, ushort Method, ushort Time, ushort Date,
        uint Crc, long CompressedSize, long Size, long LocalOffset, uint ExternalAttributes, byte[] Name, byte[] Extra);

    private static CentralEntry ReadCentralEntry(byte[] central, int at, long bias)
    {
        var header = central.AsSpan(at);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        if (nameLength + extraLength + commentLength > central.Length - at - CentralHeaderLength)
        {
            throw new InvalidDataException("A zip central directory entry runs past the directory.");
        }

        var name = header.Slice(CentralHeaderLength, nameLength).ToArray();
        var extra = header.Slice(CentralHeaderLength + nameLength, extraLength).ToArray();
        long compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        long local = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);

        // ZIP64 (APPNOTE 4.5.3): the 64-bit values of the fields set to all ones, in this order.
        if (FindExtra(extra, Zip64Tag) is { } zip64)
        {
            var z = 0;
            if (size == uint.MaxValue && z <= zip64.Length - 8)
            {
                size = ReadLength(zip64, z);
                z += 8;
            }
            if (compressed == uint.MaxValue && z <= zip64.Length - 8)
            {
                compressed = ReadLength(zip64, z);
                z += 8;
            }
            if (local == uint.MaxValue && z <= zip64.Length - 8)
            {
                local = ReadLength(zip64, z);
            }
        }

        return new CentralEntry(
            at + CentralHeaderLength + nameLength + extraLength + commentLength,
            header[5],
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
            compressed, size, local + bias,
            BinaryPrimitives.ReadUInt32LittleEndian(header[38..]),
            name, extra);
    }

    private static long ReadLength(ReadOnlySpan<byte> bytes, int at)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
        if (value > long.MaxValue)
        {
            throw new InvalidDataException("A ZIP64 size is impossibly large.");
        }

        return (long)value;
    }

    private UnixArchiveEntry? ReadEntry(ForkData input, CentralEntry entry, ContainerContext context, ref long expanded)
    {
        var nameText = DecodeName(entry.Name, (entry.Flags & FlagUtf8) != 0, entry.Host);
        var unixMode = entry.Host == HostUnix ? entry.ExternalAttributes >> 16 : 0;
        var isDirectory = nameText.EndsWith('/') || (unixMode & 0xF000) == 0x4000 ||
            entry.Host == HostMsDos && (entry.ExternalAttributes & 0x10) != 0;
        if (entry.Host == HostMsDos)
        {
            nameText = nameText.Replace('\\', '/');
        }

        var path = UnixArchive.SplitPath(nameText, context, FormatName, entry.LocalOffset);

        if (isDirectory)
        {
            return new UnixArchiveEntry { Path = path, IsDirectory = true };
        }

        if ((entry.Flags & FlagEncrypted) != 0)
        {
            context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                $"The zip entry \"{nameText}\" is encrypted; it is skipped.", entry.LocalOffset);
            return null;
        }
        if (entry.Method is not 0 and not 8)
        {
            context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                $"The zip entry \"{nameText}\" uses compression method {entry.Method}; it is skipped.", entry.LocalOffset);
            return null;
        }

        if (entry.LocalOffset < 0 || entry.LocalOffset > input.Length - LocalHeaderLength)
        {
            throw new InvalidDataException($"The local header of \"{nameText}\" lies outside the archive.");
        }

        var local = input.Slice(entry.LocalOffset, LocalHeaderLength).ToArray();
        if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalSignature)
        {
            throw new InvalidDataException($"The local header of \"{nameText}\" is missing.");
        }

        long localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
        long localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
        var dataOffset = entry.LocalOffset + LocalHeaderLength + localNameLength + localExtraLength;
        if (dataOffset > input.Length || entry.CompressedSize > input.Length - dataOffset)
        {
            throw new InvalidDataException($"The data of \"{nameText}\" runs past the end of the archive.");
        }

        var localExtra = input.Slice(entry.LocalOffset + LocalHeaderLength + localNameLength, localExtraLength).ToArray();

        if (expanded > context.Options.MaxExpandedBytesPerInput - entry.Size)
        {
            throw new InvalidDataException("Zip extraction exceeds the configured expanded-size limit.");
        }

        expanded += entry.Size;
        var data = Extract(input.Slice(dataOffset, entry.CompressedSize), entry, nameText, context, dataOffset);
        if (Crc32.Compute(data) != entry.Crc)
        {
            context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                $"The zip entry \"{nameText}\" has a CRC-32 mismatch; its data is kept.", dataOffset);
        }

        var mac = ReadMacFields(localExtra, entry.Extra, context, nameText, entry.LocalOffset);
        if (mac.ZipItName is { } macName && path.Length > 0)
        {
            path[^1] = MacRoman.Decode(macName);
        }

        if (mac.IsResourceFork is { } resource)
        {
            // Info-ZIP's old field appends 'd' or 'r' to the name; its new one files the fork under XtraStuf.mac/.
            if (mac.OldStyle && path.Length > 0 && path[^1].Length > 1 && path[^1][^1] == (resource ? 'r' : 'd'))
            {
                path[^1] = path[^1][..^1];
            }

            if (resource && path.Length > 1 && path[0] == ResourceFolder)
            {
                path = path[1..];
            }
        }

        var isLink = (unixMode & 0xF000) == 0xA000;
        return new UnixArchiveEntry
        {
            Path = path,
            IsResourceFork = mac.IsResourceFork == true,
            Data = isLink ? ForkData.Empty : ForkData.FromBytes(data),
            SymbolicLinkTarget = isLink ? Encoding.UTF8.GetString(data) : null,
            FinderInfo = mac.FinderInfo,
            Created = mac.Created ?? mac.UnixCreated,
            Modified = mac.Modified ?? mac.UnixModified ?? UnixArchive.FromDos(entry.Date, entry.Time),
        };
    }

    private static byte[] Extract(ForkData packed, CentralEntry entry, string name, ContainerContext context, long offset)
    {
        if (entry.Size > int.MaxValue)
        {
            throw new InvalidDataException($"The zip entry \"{name}\" is too large to hold in memory.");
        }

        if (entry.Method == 0)
        {
            if (entry.CompressedSize != entry.Size)
            {
                context.Report(DiagnosticSeverity.Error, "archive.truncated",
                    $"The stored zip entry \"{name}\" has different packed ({entry.CompressedSize}) and expanded ({entry.Size}) sizes; the packed bytes are kept.", offset);
            }

            return packed.ToArray();
        }
        using var stream = new DeflateStream(packed.Open(), CompressionMode.Decompress);
        var data = new byte[entry.Size];
        var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        if (read < data.Length)
        {
            context.Report(DiagnosticSeverity.Error, "archive.truncated",
                $"The zip entry \"{name}\" expands to {read} bytes, not {entry.Size}; the bytes there are kept.", offset);
            return data[..read];
        }
        return data;
    }

    // Names: UTF-8 when general-purpose flag bit 11 says so (APPNOTE appendix D); otherwise the encoding of the system
    // that wrote the archive. APPNOTE says CP437, but Mac zippers (host 7) write Mac Roman and Mac OS X's Archive
    // Utility writes UTF-8 without the flag [Fitted], so a name that is valid UTF-8 is read as UTF-8 and only other
    // non-ASCII names as CP437.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string DecodeName(byte[] name, bool utf8, int host)
    {
        if (utf8)
        {
            return Encoding.UTF8.GetString(name);
        }

        if (Array.TrueForAll(name, b => b < 0x80))
        {
            return Encoding.ASCII.GetString(name);
        }

        if (host == HostMacintosh)
        {
            return MacRoman.Decode(name);
        }

        try
        {
            return StrictUtf8.GetString(name);
        }
        catch (DecoderFallbackException)
        {
            var chars = new char[name.Length];
            for (var i = 0; i < name.Length; i++)
            {
                chars[i] = name[i] < 0x80 ? (char)name[i] : Cp437High[name[i] - 0x80];
            }

            return new string(chars);
        }
    }

    private const string Cp437High =
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    private sealed record MacFields
    {
        public bool? IsResourceFork { get; init; }
        public bool OldStyle { get; init; }
        public FinderInfo? FinderInfo { get; init; }
        public MacDate? Created { get; init; }
        public MacDate? Modified { get; init; }
        public MacDate? UnixCreated { get; init; }
        public MacDate? UnixModified { get; init; }
        public byte[]? ZipItName { get; init; }
    }

    // The local header's extra fields carry the full Mac data (the central copies are short forms), so they are read
    // first and the central ones fill what they lack.
    private static MacFields ReadMacFields(byte[] local, byte[] central, ContainerContext context, string name, long offset)
    {
        var fields = new MacFields();
        foreach (var extra in new[] { local, central })
        {
            foreach (var (tag, data) in ExtraFields(extra))
            {
                try
                {
                    fields = tag switch
                    {
                        OldMacTag => ReadOldMac(fields, data),
                        Mac3Tag => ReadMac3(fields, data, context, name, offset),
                        ZipItTag or ZipItFileTag => ReadZipIt(fields, tag, data),
                        ExtendedTimeTag => ReadExtendedTime(fields, data, context),
                        _ => fields,
                    };
                }
                catch (Exception e) when (e is EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException)
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.extra-field-invalid",
                        $"The extra field 0x{tag:x4} of zip entry \"{name}\" is damaged ({e.Message}); ignored.", offset);
                }
            }
        }
        return fields;
    }

    private static IEnumerable<(ushort Tag, byte[] Data)> ExtraFields(byte[] extra)
    {
        var at = 0;
        while (at <= extra.Length - 4)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at));
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at + 2));
            if (size > extra.Length - at - 4)
            {
                yield break;
            }

            yield return (tag, extra.AsSpan(at + 4, size).ToArray());
            at += 4 + size;
        }
    }

    private static byte[]? FindExtra(byte[] extra, ushort tag)
    {
        foreach (var (t, data) in ExtraFields(extra))
        {
            if (t == tag)
            {
                return data;
            }
        }

        return null;
    }

    // 0x07c8, Info-ZIP's old Macintosh field (J. Lee) [Doc: extrafld.txt]: "JLEE", FInfo (16), creation and
    // modification dates (Mac, local), flags (bit 0: this entry is the data fork), the directory ID and an optional
    // volume name; all big-endian. The entry's name carries an extra 'd' or 'r'.
    private static MacFields ReadOldMac(MacFields fields, byte[] data)
    {
        var reader = new BigEndianReader(data);
        if (reader.ReadUInt32() != 0x4A4C4545)
        {
            throw new InvalidDataException("no JLEE signature");
        }

        var info = FinderInfo.Read(data.AsSpan(4, 16));
        var created = reader.ReadUInt32At(20);
        var modified = reader.ReadUInt32At(24);
        var flags = reader.ReadUInt32At(28);
        return fields with
        {
            OldStyle = true,
            IsResourceFork = fields.IsResourceFork ?? (flags & 1) == 0,
            FinderInfo = fields.FinderInfo ?? info,
            Created = fields.Created ?? NonZero(created),
            Modified = fields.Modified ?? NonZero(modified),
        };
    }

    // 0x334d "M3", Info-ZIP's new Macintosh field [Doc: extrafld.txt]: BSize (the attributes' expanded size), flags,
    // type and creator in both headers; in the local header, unless flag bit 2 says the attributes are stored, a
    // compression type (0 stored, 8 deflate) and the CRC-32 of the expanded attributes, then the attributes. Flag bit 0
    // marks the data fork. The document does not give the byte order; BSize, the flags, the compression type, the CRC
    // and the attributes' numbers are read little-endian like zip's own fields [Fitted: Info-ZIP's readers use
    // makeword/makelong on them].
    private static MacFields ReadMac3(MacFields fields, byte[] data, ContainerContext context, string name, long offset)
    {
        var span = data.AsSpan();
        if (span.Length < 14)
        {
            throw new InvalidDataException("shorter than its header");
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var type = new FourCC(span[6..10]);
        var creator = new FourCC(span[10..14]);
        fields = fields with { IsResourceFork = fields.IsResourceFork ?? (flags & 1) == 0 };
        if (span.Length == 14)
        {
            return fields with { FinderInfo = fields.FinderInfo ?? new FinderInfo { Type = type, Creator = creator } };
        }

        byte[] attributes;
        if ((flags & 4) != 0)
        {
            attributes = span[14..].ToArray();
        }
        else
        {
            if (span.Length < 20)
            {
                throw new InvalidDataException("shorter than its compressed header");
            }

            var method = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            var packed = span[20..].ToArray();
            if (size > ushort.MaxValue * 4)
            {
                throw new InvalidDataException("attributes impossibly large");
            }

            attributes = method switch
            {
                0 => packed,
                8 => Inflate(packed, (int)size),
                _ => throw new InvalidDataException($"attribute compression type {method} is not supported"),
            };
            if (Crc32.Compute(attributes) != crc)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.extra-field-crc",
                    $"The Macintosh attributes of zip entry \"{name}\" fail their CRC-32; they are used.", offset);
            }
        }
        if (attributes.Length < 40)
        {
            throw new InvalidDataException("attributes shorter than 40 bytes");
        }

        var a = attributes.AsSpan();
        var fx = new BigEndianWriter(16);
        fx.WriteInt16(BinaryPrimitives.ReadInt16LittleEndian(a[8..]));   // fdIconID
        fx.WriteBytes(a.Slice(10, 6));                                   // fdUnused
        fx.WriteByte(a[16]);                                             // fdScript
        fx.WriteByte(a[17]);                                             // fdXFlags
        fx.WriteInt16(BinaryPrimitives.ReadInt16LittleEndian(a[18..]));  // fdComment
        fx.WriteInt32(BinaryPrimitives.ReadInt32LittleEndian(a[20..]));  // fdPutAway
        var info = new FinderInfo
        {
            Type = type,
            Creator = creator,
            Flags = (FinderFlags)BinaryPrimitives.ReadUInt16LittleEndian(a),
            Location = new MacPoint(BinaryPrimitives.ReadInt16LittleEndian(a[2..]), BinaryPrimitives.ReadInt16LittleEndian(a[4..])),
            Folder = BinaryPrimitives.ReadInt16LittleEndian(a[6..]),
            Extended = fx.ToArray(),
        };
        // After FXInfo: version and access bytes, then creation, modification and backup dates, Mac local times,
        // 32-bit unless flag bit 3 asks for 64.
        MacDate? created, modified;
        if ((flags & 8) != 0)
        {
            if (a.Length < 42)
            {
                throw new InvalidDataException("attributes too short for 64-bit dates");
            }

            created = Mac64(BinaryPrimitives.ReadUInt64LittleEndian(a[26..]));
            modified = Mac64(BinaryPrimitives.ReadUInt64LittleEndian(a[34..]));
        }
        else
        {
            created = NonZero(BinaryPrimitives.ReadUInt32LittleEndian(a[26..]));
            modified = NonZero(BinaryPrimitives.ReadUInt32LittleEndian(a[30..]));
        }
        return fields with
        {
            FinderInfo = info,
            Created = fields.Created ?? created,
            Modified = fields.Modified ?? modified,
        };
    }

    private static byte[] Inflate(byte[] packed, int size)
    {
        using var stream = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        var result = new byte[size];
        var read = stream.ReadAtLeast(result, size, throwOnEndOfStream: false);
        return result[..read];
    }

    // 0x2605, ZipIt's field [Doc: extrafld.txt]: "ZPIT", the full Mac name (a length byte and Mac Roman), type and
    // creator; then the Finder flags and a reserved word, which may be left out [Fitted: the document lists them only
    // for 0x2705]. 0x2705 (ZipIt 1.3.5 and later, files without MacBinary data) has no name. Big-endian.
    private static MacFields ReadZipIt(MacFields fields, ushort tag, byte[] data)
    {
        var reader = new BigEndianReader(data);
        if (reader.ReadUInt32() != 0x5A504954)
        {
            throw new InvalidDataException("no ZPIT signature");
        }

        byte[]? name = null;
        if (tag == ZipItTag)
        {
            var length = reader.ReadByte();
            name = reader.ReadBytes(length).ToArray();
        }
        var type = reader.ReadFourCC();
        var creator = reader.ReadFourCC();
        var finderFlags = reader.TryReadUInt16(out var f) ? f : (ushort)0;
        return fields with
        {
            ZipItName = fields.ZipItName ?? name,
            FinderInfo = fields.FinderInfo ?? new FinderInfo { Type = type, Creator = creator, Flags = (FinderFlags)finderFlags },
        };
    }

    // 0x5455, the extended timestamp [Doc: extrafld.txt]: a flags byte, then the modification, access and creation
    // times that the flags name (the central copy has at most the modification time), Unix seconds UTC, little-endian.
    private static MacFields ReadExtendedTime(MacFields fields, byte[] data, ContainerContext context)
    {
        if (data.Length < 1)
        {
            return fields;
        }

        var flags = data[0];
        var at = 1;
        MacDate? modified = null, created = null;
        if ((flags & 1) != 0 && at <= data.Length - 4)
        {
            modified = UnixArchive.FromUnix(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), context);
            at += 4;
        }
        if ((flags & 2) != 0 && at <= data.Length - 4)
        {
            at += 4;
        }

        if ((flags & 4) != 0 && at <= data.Length - 4)
        {
            created = UnixArchive.FromUnix(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), context);
        }

        return fields with
        {
            UnixModified = fields.UnixModified ?? modified,
            UnixCreated = fields.UnixCreated ?? created,
        };
    }

    private static MacDate? NonZero(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private static MacDate? Mac64(ulong seconds) => seconds is 0 or > uint.MaxValue ? null : new MacDate((uint)seconds);
}
