using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class ZipTarFeatureTests
{
    private static readonly byte[] Resource = "resource fork bytes"u8.ToArray();

    // An AppleDouble version 2 header: Finder info (type, creator, flags) and a resource fork.
    private static byte[] AppleDouble(string type, string creator, byte[] resource, ushort flags = 0)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(0x00051607);
        w.WriteUInt32(0x00020000);
        w.WriteZeros(16);
        w.WriteUInt16((ushort)2);
        const int dataStart = 26 + 2 * 12;
        w.WriteUInt32(9u);
        w.WriteUInt32((uint)dataStart);
        w.WriteUInt32(32u);
        w.WriteUInt32(2u);
        w.WriteUInt32((uint)(dataStart + 32));
        w.WriteUInt32((uint)resource.Length);
        w.WriteBytes(Encoding.Latin1.GetBytes(type));
        w.WriteBytes(Encoding.Latin1.GetBytes(creator));
        w.WriteUInt16(flags);
        w.WriteZeros(22);
        w.WriteBytes(resource);
        return w.ToArray();
    }

    private static byte[] SystemZip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data);
            }
        }
        return stream.ToArray();
    }

    private static IReadOnlyList<MacFile> ReadZip(byte[] zip, List<Diagnostic>? diagnostics = null) =>
        ZipReader.Instance.Read(ForkData.FromBytes(zip), new ContainerContext(diagnostics: diagnostics));

    [Fact]
    public void ZipPairsMacOsxAppleDoubleEntriesWithTheirFiles()
    {
        byte[] data = Encoding.ASCII.GetBytes(new string('x', 2000));
        var zip = SystemZip(
            ("Folder/", []),
            ("Folder/Read Me", data),
            ("__MACOSX/", []),
            ("__MACOSX/Folder/", []),
            ("__MACOSX/Folder/._Read Me", AppleDouble("TEXT", "ttxt", Resource)),
            ("__MACOSX/._Folder", AppleDouble("\0\0\0\0", "\0\0\0\0", [])),
            ("__MACOSX/Folder/._App", AppleDouble("APPL", "Abcd", Resource, 0x2000)));

        var diagnostics = new List<Diagnostic>();
        var files = ReadZip(zip, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(["Folder:Read Me", "Folder:App"], files.Select(f => f.MacPath));
        var readMe = files[0];
        Assert.Equal(data, readMe.DataFork.ToArray());
        Assert.Equal(Resource, readMe.ResourceFork.ToArray());
        Assert.Equal("TEXT", readMe.FinderInfo.Type.ToString());
        Assert.Equal("ttxt", readMe.FinderInfo.Creator.ToString());
        Assert.NotNull(readMe.Modified);
        var app = files[1];
        Assert.Equal(0, app.DataFork.Length);
        Assert.Equal(Resource, app.ResourceFork.ToArray());
        Assert.Equal((FinderFlags)0x2000, app.FinderInfo.Flags);
    }

    [Fact]
    public void ZipPairsAppleDoubleBesideTheFile()
    {
        var zip = SystemZip(("._Doc", AppleDouble("TEXT", "R*ch", Resource)), ("Doc", "text"u8.ToArray()));

        var file = Assert.Single(ReadZip(zip));

        Assert.Equal("Doc", file.MacPath);
        Assert.Equal(Resource, file.ResourceFork.ToArray());
        Assert.Equal("R*ch", file.FinderInfo.Creator.ToString());
    }

    [Fact]
    public void ZipKeepsAFalseAppleDoubleAsAFile()
    {
        var diagnostics = new List<Diagnostic>();
        var files = ReadZip(SystemZip(("._notes", "plain"u8.ToArray())), diagnostics);

        Assert.Equal("._notes", Assert.Single(files).MacPath);
        Assert.Contains(diagnostics, d => d.Code == "archive.appledouble-invalid");
    }

    [Fact]
    public void ZipReadsTheInfoZipMac3FieldWithCompressedAttributes()
    {
        var attributes = new byte[64];
        var a = attributes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(a, 0x0100);       // fdFlags: hasBeenInited
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(a[2..], 40);         // fdLocation.v
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(a[4..], 70);         // fdLocation.h
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(a[8..], 7);          // fdIconID
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(a[26..], 3_000_000_000); // FlCrDat
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(a[30..], 3_000_000_100); // FlMdDat
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                d.Write(attributes);
            }

            packed = ms.ToArray();
        }
        byte[] Mac3(bool dataFork, bool local)
        {
            var f = new MemoryStream();
            var w = new BinaryWriter(f);
            w.Write((uint)attributes.Length);
            w.Write((ushort)(dataFork ? 1 : 0));
            w.Write("SIMPttxt"u8);
            if (local)
            {
                w.Write((ushort)8);
                w.Write(Crc32(attributes));
                w.Write(packed);
            }
            return Field(0x334D, f.ToArray());
        }

        var zip = ZipBuilder.Build(
            new ZipItem("Sound", "data fork"u8.ToArray()) { LocalExtra = Mac3(true, true), CentralExtra = Mac3(true, false), Host = 7 },
            new ZipItem("XtraStuf.mac/Sound", Resource) { LocalExtra = Mac3(false, true), CentralExtra = Mac3(false, false), Host = 7 });

        var file = Assert.Single(ReadZip(zip));

        Assert.Equal("Sound", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal(Resource, file.ResourceFork.ToArray());
        Assert.Equal("SIMP", file.FinderInfo.Type.ToString());
        Assert.Equal((FinderFlags)0x0100, file.FinderInfo.Flags);
        Assert.Equal(new MacPoint(40, 70), file.FinderInfo.Location);
        Assert.Equal(7, new BigEndianReader(file.FinderInfo.Extended).ReadInt16At(0));
        Assert.Equal(new MacDate(3_000_000_000), file.Created);
        Assert.Equal(new MacDate(3_000_000_100), file.Modified);
    }

    [Fact]
    public void ZipReadsTheOldInfoZipMacintoshField()
    {
        byte[] Jlee(bool dataFork)
        {
            var w = new BigEndianWriter();
            w.WriteBytes("JLEE"u8);
            w.WriteBytes("TEXTttxt"u8);
            w.WriteUInt16((ushort)0x0400);
            w.WriteZeros(6);
            w.WriteUInt32(2_900_000_000u);
            w.WriteUInt32(2_900_000_500u);
            w.WriteUInt32(dataFork ? 1u : 0u);
            w.WriteUInt32(2u);
            return Field(0x07C8, w.ToArray());
        }

        var zip = ZipBuilder.Build(
            new ZipItem("Notesd", "words"u8.ToArray()) { LocalExtra = Jlee(true), CentralExtra = Jlee(true) },
            new ZipItem("Notesr", Resource) { LocalExtra = Jlee(false), CentralExtra = Jlee(false) });

        var file = Assert.Single(ReadZip(zip));

        Assert.Equal("Notes", file.MacPath);
        Assert.Equal("words"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal(Resource, file.ResourceFork.ToArray());
        Assert.Equal("ttxt", file.FinderInfo.Creator.ToString());
        Assert.Equal((FinderFlags)0x0400, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_900_000_000), file.Created);
        Assert.Equal(new MacDate(2_900_000_500), file.Modified);
    }

    [Fact]
    public void ZipReadsZipItFieldsAndMacRomanNames()
    {
        var w = new BigEndianWriter();
        w.WriteBytes("ZPIT"u8);
        w.WriteByte((byte)6);
        w.WriteBytes(MacRoman.Encode("Résumé"));
        w.WriteBytes("TEXTMSWD"u8);
        var zipIt = Field(0x2605, w.ToArray());
        var zip = ZipBuilder.Build(
            new ZipItem("RESUME.TXT", "cv"u8.ToArray()) { LocalExtra = zipIt },
            new ZipItem(MacRoman.Encode("Café"), "x"u8.ToArray()) { Host = 7 });

        var files = ReadZip(zip);

        Assert.Equal("Résumé", files[0].UnicodeName);
        Assert.Equal("Résumé", files[0].Name.ToMacRoman());
        Assert.Equal("MSWD", files[0].FinderInfo.Creator.ToString());
        Assert.Equal("Café", files[1].UnicodeName);
    }

    [Fact]
    public void ZipDecodesCp437AndUtf8Names()
    {
        var zip = ZipBuilder.Build(
            new ZipItem([0x80, 0x41], "a"u8.ToArray()) { Host = 0 },
            new ZipItem(Encoding.UTF8.GetBytes("Über"), "b"u8.ToArray()) { Flags = 1 << 11 });

        var files = ReadZip(zip);

        Assert.Equal("ÇA", files[0].UnicodeName);
        Assert.Equal("Über", files[1].UnicodeName);
    }

    [Fact]
    public void ZipSkipsEncryptedEntriesAndReportsBadCrcs()
    {
        var diagnostics = new List<Diagnostic>();
        var zip = ZipBuilder.Build(
            new ZipItem("secret", "xxxx"u8.ToArray()) { Flags = 1 },
            new ZipItem("damaged", "abcd"u8.ToArray()) { Crc = 1234 });

        var file = Assert.Single(ReadZip(zip, diagnostics));

        Assert.Equal("damaged", file.MacPath);
        Assert.Contains(diagnostics, d => d.Code == "archive.encrypted");
        Assert.Contains(diagnostics, d => d.Code == "archive.fork-checksum");
    }

    [Fact]
    public void ZipReadsZip64Sizes()
    {
        var zip = ZipBuilder.Build(new ZipItem("big", "zip64 data"u8.ToArray()) { Zip64 = true });

        var file = Assert.Single(ReadZip(zip));

        Assert.Equal("zip64 data"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void TarPairsAppleDoubleEntriesAndKeepsLongNamesAndLinks()
    {
        var longName = "Folder/" + new string('n', 120);
        foreach (var format in new[] { TarEntryFormat.Pax, TarEntryFormat.Gnu, TarEntryFormat.Ustar })
        {
            var tar = Tar(format, w =>
            {
                w.WriteEntry(Dir(format, "Folder/"));
                w.WriteEntry(File(format, "Folder/._Read Me", AppleDouble("TEXT", "ttxt", Resource)));
                w.WriteEntry(File(format, "Folder/Read Me", "hello"u8.ToArray()));
                if (format != TarEntryFormat.Ustar)
                {
                    w.WriteEntry(File(format, longName, "long"u8.ToArray()));
                }

                var link = Entry(format, TarEntryType.SymbolicLink, "Folder/Link");
                link.LinkName = "Read Me";
                w.WriteEntry(link);
            });

            Assert.True(TarArchiveReader.Instance.CanRead(ForkData.FromBytes(tar)));
            var context = new ContainerContext(ContainerReadOptions.Default with { TimeZone = TimeZoneInfo.Utc });
            var files = TarArchiveReader.Instance.Read(ForkData.FromBytes(tar), context);

            var readMe = files.Single(f => f.UnicodeName == "Read Me");
            Assert.Equal(["Folder"], readMe.UnicodeFolderPath);
            Assert.Equal("hello"u8.ToArray(), readMe.DataFork.ToArray());
            Assert.Equal(Resource, readMe.ResourceFork.ToArray());
            Assert.Equal("TEXT", readMe.FinderInfo.Type.ToString());
            Assert.Equal(MacDate.FromDateTime(new DateTime(2020, 1, 1)), readMe.Modified);
            Assert.Equal("Read Me", files.Single(f => f.UnicodeName == "Link").SymbolicLinkTarget);
            if (format != TarEntryFormat.Ustar)
            {
                Assert.Contains(files, f => f.UnicodeName == new string('n', 120));
            }

            Assert.DoesNotContain(files, f => f.UnicodeName!.StartsWith("._", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TgzUnwrapsThroughGzipAndTar()
    {
        var tar = Tar(TarEntryFormat.Pax, w =>
        {
            w.WriteEntry(File(TarEntryFormat.Pax, "._App", AppleDouble("APPL", "Abcd", Resource)));
            w.WriteEntry(File(TarEntryFormat.Pax, "App", []));
        });
        var tgz = Gzip(tar, null);
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("app.tgz"), DataFork = ForkData.FromBytes(tgz) }, "test",
            new ContainerContext(diagnostics: diagnostics, hostName: MacString.FromMacRoman("app.tgz")));

        var gz = Assert.Single(root.Children);
        Assert.Equal("gzip", gz.Format);
        Assert.Equal("app.tar", gz.File.UnicodeName);
        var app = Assert.Single(root.Leaves());
        Assert.Equal("tar", app.Format);
        Assert.Equal("App", app.File.MacPath);
        Assert.Equal(Resource, app.File.ResourceFork.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GzipTakesItsNameFromTheHeader()
    {
        var gz = Gzip("payload"u8.ToArray(), "dir/Original Name");

        var file = Assert.Single(GzipReader.Instance.Read(ForkData.FromBytes(gz), new ContainerContext()));

        Assert.Equal("Original Name", file.UnicodeName);
        Assert.Equal("payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void ZipIsFoundByTheUnwrapper()
    {
        var zip = SystemZip(("Doc", "text"u8.ToArray()), ("__MACOSX/._Doc", AppleDouble("TEXT", "ttxt", Resource)));

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("doc.zip"), DataFork = ForkData.FromBytes(zip) }, "test", new ContainerContext());

        var leaf = Assert.Single(root.Leaves());
        Assert.Equal("Zip", leaf.Format);
        Assert.Equal(Resource, leaf.File.ResourceFork.ToArray());
    }

    // --- builders ---

    private static byte[] Field(ushort tag, byte[] data)
    {
        var f = new byte[4 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f, tag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)data.Length);
        data.CopyTo(f, 4);
        return f;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }

    private static readonly DateTimeOffset TarTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TarEntry Entry(TarEntryFormat format, TarEntryType type, string name)
    {
        TarEntry entry = format switch
        {
            TarEntryFormat.Pax => new PaxTarEntry(type, name),
            TarEntryFormat.Gnu => new GnuTarEntry(type, name),
            _ => new UstarTarEntry(type, name),
        };
        entry.ModificationTime = TarTime;
        return entry;
    }

    private static TarEntry Dir(TarEntryFormat format, string name) => Entry(format, TarEntryType.Directory, name);

    private static TarEntry File(TarEntryFormat format, string name, byte[] data)
    {
        var entry = Entry(format, TarEntryType.RegularFile, name);
        entry.DataStream = new MemoryStream(data);
        return entry;
    }

    private static byte[] Tar(TarEntryFormat format, Action<TarWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, format, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    private static byte[] Gzip(byte[] data, string? name)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data);
        }

        var bytes = ms.ToArray();
        if (name is null)
        {
            return bytes;
        }
        // Insert an FNAME field after the 10-byte header.
        var nameBytes = Encoding.Latin1.GetBytes(name + "\0");
        var result = new byte[bytes.Length + nameBytes.Length];
        bytes.AsSpan(0, 10).CopyTo(result);
        result[3] |= 8;
        nameBytes.CopyTo(result, 10);
        bytes.AsSpan(10).CopyTo(result.AsSpan(10 + nameBytes.Length));
        return result;
    }

    internal sealed record ZipItem(byte[] Name, byte[] Data)
    {
        public ZipItem(string name, byte[] data) : this(Encoding.ASCII.GetBytes(name), data) { }
        public byte[] LocalExtra { get; init; } = [];
        public byte[] CentralExtra { get; init; } = [];
        public ushort Flags { get; init; }
        public byte Host { get; init; } = 3;
        public bool Zip64 { get; init; }
        public uint? Crc { get; init; }
    }

    // Stored entries, written by hand so the extra fields, flags and host are exactly as given.
    private static class ZipBuilder
    {
        public static byte[] Build(params ZipItem[] items)
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            var offsets = new List<long>();
            foreach (var item in items)
            {
                offsets.Add(stream.Position);
                w.Write(0x04034B50u);
                w.Write((ushort)20);
                w.Write(item.Flags);
                w.Write((ushort)0);
                w.Write((ushort)0x6000); // 12:00
                w.Write((ushort)((40 << 9) | (6 << 5) | 15)); // 2020-06-15
                w.Write(item.Crc ?? Crc32(item.Data));
                w.Write((uint)item.Data.Length);
                w.Write((uint)item.Data.Length);
                w.Write((ushort)item.Name.Length);
                w.Write((ushort)item.LocalExtra.Length);
                w.Write(item.Name);
                w.Write(item.LocalExtra);
                w.Write(item.Data);
            }
            var centralStart = stream.Position;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var extra = item.CentralExtra;
                if (item.Zip64)
                {
                    var z = new byte[24];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(z, (ulong)item.Data.Length);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(8), (ulong)item.Data.Length);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(16), (ulong)offsets[i]);
                    extra = [.. Field(1, z), .. extra];
                }
                w.Write(0x02014B50u);
                w.Write((ushort)((item.Host << 8) | 20));
                w.Write((ushort)20);
                w.Write(item.Flags);
                w.Write((ushort)0);
                w.Write((ushort)0x6000);
                w.Write((ushort)((40 << 9) | (6 << 5) | 15));
                w.Write(item.Crc ?? Crc32(item.Data));
                w.Write(item.Zip64 ? uint.MaxValue : (uint)item.Data.Length);
                w.Write(item.Zip64 ? uint.MaxValue : (uint)item.Data.Length);
                w.Write((ushort)item.Name.Length);
                w.Write((ushort)extra.Length);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write(item.Host == 3 ? 0x81A40000u : 0u);
                w.Write(item.Zip64 ? uint.MaxValue : (uint)offsets[i]);
                w.Write(item.Name);
                w.Write(extra);
            }
            var centralSize = stream.Position - centralStart;
            w.Write(0x06054B50u);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)items.Length);
            w.Write((ushort)items.Length);
            w.Write((uint)centralSize);
            w.Write((uint)centralStart);
            w.Write((ushort)0);
            return stream.ToArray();
        }
    }
}
