using System.Buffers.Binary;
using ClassicMac.Core;

namespace ClassicMac.Resources.Tests;

public class ResourceForkReadWriteTests
{
    private static readonly FourCC Test = FourCC.FromString("TEST");
    private static readonly FourCC Snd = FourCC.FromString("snd ");

    // A small fork in the canonical layout, assembled by hand from Inside Macintosh's description:
    // 'TEST' 128 "Hi" (preload) = 01 02 03; 'TEST' -1 unnamed = empty; 'snd ' 1 "" (purgeable) = AA.
    private const int MapOffset = 272;
    private const int TypeList = MapOffset + 28;
    private const int TestReferences = TypeList + 18;
    private const int SndReferences = TypeList + 42;

    private static byte[] Canonical()
    {
        var bytes = new byte[358];
        Hex("00000100 00000110 00000010 00000056").CopyTo(bytes, 0);
        bytes[16] = 0x11; // first byte of the system area
        bytes[128] = 0x22; // first byte of the application area
        Hex(
            "00000003 010203" + // 'TEST' 128
            "00000000" + // 'TEST' -1
            "00000001 AA" + // 'snd ' 1
            "00000100 00000110 00000010 00000056" + // map: header copy
            "12345678 9ABC 80 01 001C 0052" + // handle, file ref, attributes (read-only), flags (password bit), lists
            "0001" + // two types
            "54455354 0001 0012" + // 'TEST', two resources, references at 18
            "736E6420 0000 002A" + // 'snd ', one resource, references at 42
            "0080 0000 04000000 00000000" + // 128, name at 0, preload, data at 0
            "FFFF FFFF 00000007 00000000" + // -1, no name, data at 7
            "0001 0003 2000000B 00000000" + // 1, name at 3, purgeable, data at 11
            "02 4869 00" // "Hi", ""
        ).CopyTo(bytes, 256);
        return bytes;
    }

    private static ResourceFork CanonicalModel()
    {
        var fork = new ResourceFork
        {
            Attributes = ResourceForkAttributes.ReadOnly | ResourceForkAttributes.Changed,
            SystemData = Area(ResourceFork.SystemDataLength, 0x11),
            ApplicationData = Area(ResourceFork.ApplicationDataLength, 0x22),
            MapReservedData = Hex("123456789ABC"),
            MapFlags = ResourceMapFlags.DecompressionPassword,
        };
        fork.Add(new Resource(Test, 128, new byte[] { 1, 2, 3 })
        {
            Name = new MacString("Hi"u8),
            Attributes = ResourceAttributes.Preload,
        });
        fork.Add(new Resource(Test, -1, ReadOnlyMemory<byte>.Empty));
        fork.Add(new Resource(Snd, 1, new byte[] { 0xAA })
        {
            Name = new MacString([]),
            Attributes = ResourceAttributes.Purgeable | ResourceAttributes.Changed,
        });
        return fork;
    }

    [Fact]
    public void Writes_the_canonical_layout()
    {
        Assert.Equal(Convert.ToHexString(Canonical()), Convert.ToHexString(CanonicalModel().ToArray()));
    }

    [Fact]
    public void Reads_the_canonical_layout()
    {
        var fork = ResourceFork.Read(Canonical());

        Assert.Empty(fork.Diagnostics);
        Assert.Equal(ResourceForkAttributes.ReadOnly, fork.Attributes);
        Assert.Equal(ResourceMapFlags.DecompressionPassword, fork.MapFlags);
        Assert.Equal(0x11, fork.SystemData.Span[0]);
        Assert.Equal(0x22, fork.ApplicationData.Span[0]);
        Assert.Equal(Hex("123456789ABC"), fork.MapReservedData.ToArray());
        Assert.Equal([Test, Snd], fork.Types);

        var hi = fork.Find(Test, 128)!;
        Assert.Equal(new MacString("Hi"u8), hi.Name);
        Assert.Equal(ResourceAttributes.Preload, hi.Attributes);
        Assert.Equal([1, 2, 3], hi.GetData().ToArray());

        var unnamed = fork.Find(Test, -1)!;
        Assert.Null(unnamed.Name);
        Assert.Equal(0, unnamed.Length);

        var snd = fork.Find(Snd, 1)!;
        Assert.Equal(new MacString([]), snd.Name);
        Assert.Equal(ResourceAttributes.Purgeable, snd.Attributes);
        Assert.Equal([0xAA], snd.GetData().ToArray());
    }

    [Fact]
    public void Reads_from_a_stream()
    {
        var fork = ResourceFork.Read(new MemoryStream(Canonical()));
        Assert.Equal(3, fork.Resources.Count);
    }

    [Fact]
    public void An_empty_input_is_an_empty_fork()
    {
        var fork = ResourceFork.Read(ReadOnlyMemory<byte>.Empty);
        Assert.Empty(fork.Resources);
        Assert.Empty(fork.Diagnostics);
    }

    [Fact]
    public void An_empty_fork_round_trips()
    {
        var bytes = new ResourceFork().ToArray();
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(256 + 28))); // no types
        var fork = ResourceFork.Read(bytes);
        Assert.Empty(fork.Resources);
        Assert.Empty(fork.Diagnostics);
        Assert.Equal(bytes, fork.ToArray());
    }

    [Fact]
    public void Round_trips_model_and_bytes()
    {
        var original = new ResourceFork();
        var random = new Random(1984);
        foreach (var type in new[] { "PICT", "snd ", "STR#", "pict", "©abc" })
        {
            for (var i = 0; i < 20; i++)
            {
                var data = new byte[random.Next(0, 300)];
                random.NextBytes(data);
                original.Add(new Resource(FourCC.FromString(type), (short)random.Next(short.MinValue, short.MaxValue), data)
                {
                    Name = i % 3 == 0 ? null : new MacString(data.AsSpan(0, Math.Min(data.Length, i * 3))),
                    Attributes = (ResourceAttributes)(random.Next(256) & ~(int)ResourceAttributes.Changed),
                });
            }
        }

        var written = original.ToArray();
        var read = ResourceFork.Read(written);

        Assert.Empty(read.Diagnostics);
        AssertSameModel(original, read);
        Assert.Equal(written, read.ToArray());
    }

    [Fact]
    public void Writing_past_24_bit_data_offsets_fails()
    {
        var fork = new ResourceFork();
        for (short id = 0; id < 3; id++) fork.Add(new Resource(Test, id, new byte[8 * 1024 * 1024]));
        Assert.Throws<InvalidOperationException>(() => fork.ToArray());
    }

    [Fact]
    public void Unusable_input_throws()
    {
        Assert.Throws<InvalidDataException>(() => ResourceFork.Read(new byte[10]));

        var mapOutside = Canonical();
        BinaryPrimitives.WriteUInt32BigEndian(mapOutside.AsSpan(4), 5000);
        Assert.Throws<InvalidDataException>(() => ResourceFork.Read(mapOutside));

        var typeListOutside = Canonical();
        BinaryPrimitives.WriteUInt16BigEndian(typeListOutside.AsSpan(MapOffset + 24), 0xFFF0);
        Assert.Throws<InvalidDataException>(() => ResourceFork.Read(typeListOutside));
    }

    [Fact]
    public void Wrong_lengths_in_the_header_are_tolerated()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 5000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 5000);
        var fork = ResourceFork.Read(bytes);

        Assert.Equal(3, fork.Resources.Count);
        AssertCodes(fork, "fork.data-length", "fork.map-length", "fork.header-mismatch");
    }

    [Fact]
    public void A_zeroed_header_copy_is_not_reported()
    {
        var bytes = Canonical();
        bytes.AsSpan(MapOffset, 16).Clear();
        Assert.Empty(ResourceFork.Read(bytes).Diagnostics);
    }

    [Fact]
    public void A_type_list_longer_than_the_map_is_reported()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(TypeList), 0xFFFE);
        Assert.Contains(ResourceFork.Read(bytes).Diagnostics, d => d.Code == "fork.type-list-truncated");
    }

    [Fact]
    public void A_reference_list_longer_than_the_map_is_reported()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(TypeList + 2 + 4), 0x0FFF);
        Assert.Contains(ResourceFork.Read(bytes).Diagnostics, d => d.Code == "fork.ref-list-out-of-range");
    }

    [Fact]
    public void Data_outside_the_data_area_skips_the_resource()
    {
        var bytes = Canonical();
        Hex("FFFFFF").CopyTo(bytes, TestReferences + 5);
        var fork = ResourceFork.Read(bytes);

        Assert.Null(fork.Find(Test, 128));
        AssertCodes(fork, "resource.data-out-of-range");
    }

    [Fact]
    public void Truncated_data_keeps_what_is_there()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(256), 100);
        var fork = ResourceFork.Read(bytes);

        Assert.Equal(12, fork.Find(Test, 128)!.Length); // the rest of the 16-byte data area
        Assert.Contains(fork.Diagnostics, d => d.Code == "resource.data-truncated" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Resources_over_the_size_limit_are_skipped()
    {
        var fork = ResourceFork.Read(Canonical(), ReadOptions.Default with { MaxResourceSize = 2 });

        Assert.Null(fork.Find(Test, 128));
        Assert.NotNull(fork.Find(Snd, 1));
        AssertCodes(fork, "resource.too-large");
    }

    [Fact]
    public void A_name_outside_the_map_leaves_the_resource_unnamed()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(TestReferences + 2), 0x1000);
        var fork = ResourceFork.Read(bytes);

        Assert.Null(fork.Find(Test, 128)!.Name);
        AssertCodes(fork, "resource.name-out-of-range");
    }

    [Fact]
    public void A_duplicate_resource_keeps_the_first()
    {
        var bytes = Canonical();
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(TestReferences + 12), 128);
        var fork = ResourceFork.Read(bytes);

        Assert.Equal(2, fork.Resources.Count);
        Assert.Equal([1, 2, 3], fork.Find(Test, 128)!.GetData().ToArray());
        AssertCodes(fork, "resource.duplicate");
    }

    [Fact]
    public void A_type_listed_twice_is_merged()
    {
        var bytes = Canonical();
        "TEST"u8.CopyTo(bytes.AsSpan(TypeList + 2 + 8));
        var fork = ResourceFork.Read(bytes);

        Assert.Equal([Test], fork.Types);
        Assert.NotNull(fork.Find(Test, 1));
        AssertCodes(fork, "fork.duplicate-type");
    }

    [Fact]
    public void Shared_data_is_reported_but_read()
    {
        var bytes = Canonical();
        Hex("000000").CopyTo(bytes, SndReferences + 5);
        var fork = ResourceFork.Read(bytes);

        Assert.Equal([1, 2, 3], fork.Find(Snd, 1)!.GetData().ToArray());
        AssertCodes(fork, "resource.data-overlap");
    }

    [Fact]
    public void Corpus_forks_round_trip()
    {
        var files = Corpus.ForkFiles();
        var identical = 0;
        foreach (var file in files)
        {
            var original = File.ReadAllBytes(file);
            var read = ResourceFork.Read(original);
            var written = read.ToArray();
            var reread = ResourceFork.Read(written);
            Assert.Empty(reread.Diagnostics);
            AssertSameModel(read, reread);
            if (written.AsSpan().SequenceEqual(original)) identical++;
        }
        TestContext.Current.SendDiagnosticMessage(
            $"{files.Count} corpus forks round-trip; {identical} are byte-identical (already canonical).");
    }

    private static void AssertSameModel(ResourceFork expected, ResourceFork actual)
    {
        Assert.Equal(expected.Attributes & ~ResourceForkAttributes.Changed, actual.Attributes);
        Assert.Equal(expected.MapFlags, actual.MapFlags);
        Assert.Equal(expected.SystemData.ToArray(), actual.SystemData.ToArray());
        Assert.Equal(expected.ApplicationData.ToArray(), actual.ApplicationData.ToArray());
        Assert.Equal(expected.MapReservedData.ToArray(), actual.MapReservedData.ToArray());
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Resources.Count, actual.Resources.Count);
        foreach (var type in expected.Types)
        {
            var e = expected.OfType(type).ToList();
            var a = actual.OfType(type).ToList();
            Assert.Equal(e.Count, a.Count);
            for (var i = 0; i < e.Count; i++)
            {
                Assert.Equal(e[i].Id, a[i].Id);
                Assert.Equal(e[i].Name, a[i].Name);
                Assert.Equal(e[i].Attributes & ~ResourceAttributes.Changed, a[i].Attributes);
                Assert.Equal(e[i].GetData().ToArray(), a[i].GetData().ToArray());
            }
        }
    }

    private static void AssertCodes(ResourceFork fork, params string[] codes) =>
        Assert.Equal(codes.Order(), fork.Diagnostics.Select(d => d.Code).Order());

    private static byte[] Area(int length, byte first)
    {
        var area = new byte[length];
        area[0] = first;
        return area;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
}
