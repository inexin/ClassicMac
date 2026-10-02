using System.Globalization;
using ClassicMac.Code.Ppc;
using ClassicMac.Code.Tests;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Tests;

// Hand-built code for the code decoders, the disasm command and the viewer: a near 68k application (CODE 0 and 1), a
// small PowerPC fragment, code resources in each header form, and 'cfrg' 0. The layouts are Inside Macintosh II's
// Segment Loader, Mac OS Runtime Architectures (PEF, 'cfrg') and Inside Macintosh: Devices (the driver header).
internal static class CodeFixtures
{
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    public static byte[] Words(params ushort[] words)
    {
        var w = new BigEndianWriter();
        foreach (var word in words) w.WriteUInt16(word);
        return w.ToArray();
    }

    // A near jump-table entry: offset (from the code), MOVE.W #seg,-(SP), _LoadSeg.
    public static byte[] NearEntry(ushort offset, short segment) => Words(offset, 0x3F3C, (ushort)segment, 0xA9F0);

    // CODE 0: aboveA5, belowA5, the jump table's size and offset ($20), then the entries.
    public static byte[] Code0(params byte[][] entries)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(0x20 + 8 * entries.Length);
        w.WriteUInt32(0x100);
        w.WriteUInt32(8 * entries.Length);
        w.WriteUInt32(0x20);
        foreach (var e in entries) w.WriteBytes(e);
        return w.ToArray();
    }

    // CODE 1 "Main": jsr 42(a5) (entry 1); rts; then at $A: moveq #0,d0; rts. Entry 0 is +4, entry 1 is +$A.
    public static readonly byte[] Code1 = Bytes("0000 0002 4EAD 002A 4E75 7000 4E75");

    public static byte[] Application0 => Code0(NearEntry(0, 1), NearEntry(6, 1));

    public static Resource Res(string type, short id, byte[] data, string? name = null, ResourceAttributes attributes = ResourceAttributes.None)
    {
        var r = new Resource(FourCC.FromString(type), id, data) { Attributes = attributes };
        if (name is not null) r.Name = MacString.FromMacRoman(name);
        return r;
    }

    public static ResourceFork Fork(params Resource[] resources)
    {
        var fork = new ResourceFork();
        foreach (var r in resources) fork.Add(r);
        return fork;
    }

    // The application: CODE 0 and CODE 1 "Main".
    public static ResourceFork Application() => Fork(Res("CODE", 0, Application0), Res("CODE", 1, Code1, "Main"));

    // A PowerPC fragment: main at 0 calls InterfaceLib's InitGraf through its glue stub and returns; a TOC of one import.
    public static byte[] Fragment()
    {
        var code = PefBuilder.Words(
            0x7C0802A6, 0x48000015, 0x80410014, 0x48000025, 0x80620004, 0x4E800020,
            0x81820000, 0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420,
            0x4E800020, 0x00000000, 0x00002040, 0x00000000, 0x00000004, 0x00072E48, 0x656C7065, 0x72000000);
        var data = PefBuilder.Words(0x00000000, 0x00000008, 0x00000000, 0x00000010, 0x12345678, 0x00000030, 0x00000008);
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, code);
        b.AddSection(PefSectionKind.UnpackedData, data);
        b.Libraries.Add(new PefBuilder.Library("InterfaceLib", [new PefBuilder.Import("InitGraf")]));
        b.Exports.Add(new PefBuilder.Export("Helper", 0x00060D48, PefSymbolClass.TVector, 0x14, 1));
        b.Relocations.Add((1, [0x4600, 0x4A00, 0x4200, 0x8003, 0x4600]));
        b.Main = (1, 0);
        return b.Build();
    }

    // A standard header ('CDEF' 0, version 1) branching to its code: moveq #0,d0; rts.
    public static readonly byte[] StandardHeader = Bytes("600A 0000 4344 4546 0000 0001 7000 4E75");

    // A driver ".D": open $18, prime $1A, control $1A, status 0, close $1C.
    public static readonly byte[] Driver = Bytes("4F00 0000 0000 0000 0018 001A 001A 0000 001C 022E 4400 0000 7000 4E75 4E75");

    // A package: _Debugger, 'PACK' 3, version 1, selectors 0 to 1 at $12 and 0; rts.
    public static readonly byte[] Package = Bytes("A9FF 5041 434B 0003 0001 0000 0001 0012 0000 4E75");

    // A fat code resource: a routine descriptor with one relative PowerPC routine at $20, the fragment.
    public static byte[] Fat()
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(0xAAFE);
        w.WriteByte(7);
        w.WriteByte(0);
        w.WriteUInt32(0);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteUInt16(0);          // one routine
        w.WriteUInt32(0x3BB0);     // procInfo
        w.WriteByte(0);
        w.WriteByte(1);            // ISA: PowerPC
        w.WriteUInt16(1);          // relative
        w.WriteUInt32(0x20);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteBytes(Fragment());
        return w.ToArray();
    }

    // 'cfrg' 0: the 32-byte header, then one member per (name, where, offset, length, where1, where2).
    public static byte[] Cfrg(params (string Name, CfrgWhere Where, uint Offset, uint Length, uint Where1, ushort Where2)[] members)
    {
        var w = new BigEndianWriter();
        w.WriteZeros(10);
        w.WriteUInt16(1);
        w.WriteZeros(18);
        w.WriteUInt16(members.Length);
        foreach (var m in members)
        {
            int start = w.Length;
            w.WriteFourCC(FourCC.FromString("pwpc"));
            w.WriteUInt16(0);
            w.WriteByte(0);
            w.WriteByte(1);            // updateLevel
            w.WriteUInt32(0x01108000); // currentVersion
            w.WriteUInt32(0x01000000); // oldDefVersion
            w.WriteUInt32(0x10000);    // usage1: the stack size
            w.WriteUInt16(0);
            w.WriteByte((byte)CfrgUsage.Application);
            w.WriteByte((byte)m.Where);
            w.WriteUInt32(m.Offset);
            w.WriteUInt32(m.Length);
            w.WriteUInt32(m.Where1);
            w.WriteUInt16(m.Where2);
            w.WriteUInt16(0);          // no extensions
            int sizeAt = w.Length;
            w.WriteUInt16(0);
            w.WriteByte(m.Name.Length);
            w.WriteBytes(MacRoman.Encode(m.Name));
            while ((w.Length - start) % 4 != 0) w.WriteByte(0);
            w.WriteUInt16At(sizeAt, w.Length - start);
        }
        return w.ToArray();
    }

    // A fat application: the 68k application, a 'cfrg' 0 naming the data fork's fragment (at 0x10, its length), a
    // driver, a native 'ncod', and its data fork (16 bytes of padding, then the fragment).
    public static (ResourceFork Fork, byte[] DataFork) FatApplication(string name = "App")
    {
        var fragment = Fragment();
        var fork = Application();
        fork.Add(Res("cfrg", 0, Cfrg((name, CfrgWhere.DataFork, 0x10, (uint)fragment.Length, 0, 0))));
        fork.Add(Res("DRVR", 12, Driver, ".D"));
        fork.Add(Res("ncod", 5, Fragment()));
        return (fork, [.. new byte[0x10], .. fragment]);
    }

    public static string Hex(int value) => value.ToString("X", CultureInfo.InvariantCulture);
}
