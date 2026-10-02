using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Code.Tests.M68k;
using ClassicMac.Core;
using ClassicMac.Resources;
using static ClassicMac.Code.Tests.Disassembly.M68kDisassemblerTests;

namespace ClassicMac.Code.Tests.Disassembly;

// Golden listings of small hand-built segments, code resources and fragments.
public class CodeListingTests
{
    private static byte[] With(string hex, string text, string tail) =>
        [.. Bytes(hex), .. System.Text.Encoding.ASCII.GetBytes(text), .. Bytes(tail)];

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    // CODE 0: entry 0 = CODE 1 +$4, entry 1 = CODE 1 +$18.
    // CODE 1 "Main": 04 jsr 42(a5); 08 move.w #0,-(sp); 0C _Pack7; 0E rts; 10 "Main"; 18 move.l ($016A).w,d0; 1C rts;
    // 1E "Helper" with 4 bytes of literals "Hi!\0".
    internal static CodeApplication App()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1), CodeBuilder.NearEntry(0x14, 1)]));
        byte[] code =
        [
            .. With("4EAD 002A 3F3C 0000 A9EE 4E75 84", "Main", "00 0000"),
            .. With("2038 016A 4E75 86", "Helper", "00 0004 4869 2100"),
        ];
        fork.Add("CODE", 1, CodeBuilder.Near(0, 2, code), "Main");
        return CodeApplication.Read(fork, new List<Diagnostic>());
    }

    [Fact]
    public void Near_segment()
    {
        var listing = CodeListing.ForSegment(App(), 1);
        Assert.Equal(Lines(
            "; 'CODE' 1 \"Main\": 68k segment, near header",
            "; Model: unknown",
            "; Entry: CODE 1:+$4",
            "; Jump-table entries: 2",
            "",
            "00000000  0000 0002                 dc.w $0000,$0002  ; segment header; '....'",
            "",
            "Main:",
            "00000004  4EAD 002A                 jsr 42(a5)  ; CODE 1:+$18 Helper",
            "00000008  3F3C 0000                 move.w #0,-(sp)",
            "0000000C  A9EE                      _Pack7  ; NumToString",
            "0000000E  4E75                      rts",
            "00000010  844D 6169 6E00 0000       dc.w $844D,$6169,$6E00,$0000  ; MacsBug name Main; '.Main...'",
            "",
            "Helper:",
            "00000018  2038 016A                 move.l ($016A).w,d0  ; Ticks",
            "0000001C  4E75                      rts",
            "0000001E  8648 656C 7065 7200       dc.w $8648,$656C,$7065,$7200  ; MacsBug name Helper; '.Helper.'",
            "00000026  0004                      dc.w $0004  ; '..'",
            "00000028  4869 2100                 dc.w $4869,$2100  ; literals; 'Hi!.'"), listing.Text);
    }

    [Fact]
    public void Near_segment_model()
    {
        var listing = CodeListing.ForSegment(App(), 1);
        Assert.Equal(
        [
            new CodeFunction(0, 4, "Main", CodeFunctionSource.Entry),
            new CodeFunction(0, 0x18, "Helper", CodeFunctionSource.JumpTable),
        ], listing.Functions);
        Assert.Equal(
        [
            new CodeReference(0, 4, CodeReferenceKind.JumpTable, "CODE 1:+$18 Helper"),
            new CodeReference(0, 0xC, CodeReferenceKind.Trap, "_Pack7"),
            new CodeReference(0, 0xC, CodeReferenceKind.Selector, "NumToString"),
            new CodeReference(0, 0x18, CodeReferenceKind.LowMemory, "Ticks"),
        ], listing.References);
        Assert.Empty(listing.Diagnostics);
        Assert.Empty(listing.Fragments);
    }

    [Fact]
    public void Missing_segment_throws() =>
        Assert.Throws<ArgumentException>(() => CodeListing.ForSegment(App(), 7));

    [Fact]
    public void Unreadable_segment()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1)]));
        // Compressed, with a 'dcmp' that does not exist: not readable.
        var compressed = Bytes("A89F 6572 0012 0801 0000 0010 0000 4E75 0000 0000");
        fork.Add("CODE", 1, compressed, attributes: ResourceAttributes.Compressed);
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        Assert.False(app.FindSegment(1)!.IsReadable);
        var listing = CodeListing.ForSegment(app, 1);
        Assert.Equal(Lines(
            "; 'CODE' 1: 68k segment, compressed and not readable",
            "; Model: unknown",
            "; Entry: CODE 1:+$4",
            "; Jump-table entries: 1",
            ""), listing.Text);
        Assert.Empty(listing.Functions);
    }

    [Fact]
    public void Far_segment_with_relocations()
    {
        // Far model; CODE 1 code at $28: lea ($00000100).l,a0 (A5); jsr ($00000034).l (PC); 34 rts; lists at $36.
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.FarEntry(1, 0x28), CodeBuilder.FarMarker]));
        var code = Bytes("41F9 0000 0100 4EB9 0000 0034 4E75 1500 1800");
        fork.Add("CODE", 1, CodeBuilder.Far(0, 1, 0, 0, code, a5Relocations: 0x36, pcRelocations: 0x38,
            a5AtLast: 0, addressAtLast: 0, reserved: 0));
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var listing = CodeListing.ForSegment(app, 1);
        Assert.Equal(Lines(
            "; 'CODE' 1: 68k segment, far header",
            "; Model: MPW far",
            "; Entry: CODE 1:+$28",
            "; Jump-table entries: 1",
            "; Relocations: 2",
            "",
            "00000000  FFFF 0000 0000 0000       dc.w $FFFF,$0000,$0000,$0000  ; segment header; '........'",
            "00000008  0000 0001 0000 0000       dc.w $0000,$0001,$0000,$0000  ; '........'",
            "00000010  0000 0000 0000 0036       dc.w $0000,$0000,$0000,$0036  ; '.......6'",
            "00000018  0000 0000 0000 0038       dc.w $0000,$0000,$0000,$0038  ; '.......8'",
            "00000020  0000 0000 0000 0000       dc.w $0000,$0000,$0000,$0000  ; '........'",
            "",
            "entry:",
            "00000028  41F9 0000 0100            lea ($00000100).l,a0  ; A5+$100",
            "0000002E  4EB9 0000 0034            jsr ($00000034).l  ; CODE 1+$34 sub_0034",
            "",
            "sub_0034:",
            "00000034  4E75                      rts",
            "00000036  1500 1800                 dc.w $1500,$1800  ; relocations; '....'"), listing.Text);
    }

    [Fact]
    public void Standard_header_code_resource()
    {
        // bra.s $C; flags 0; 'WDEF' 0 version 1; then moveq #0,d0; rts.
        var data = Bytes("600A 0000 5744 4546 0000 0001 7000 4E75");
        var listing = CodeListing.ForCodeResource(FourCC.FromString("WDEF"), 0, data);
        Assert.Equal(Lines(
            "; 'WDEF' 0: 68k code resource",
            "; Standard header: 'WDEF' 0, version $0001",
            "",
            "entry:",
            "00000000  600A                      bra.s $000C  ; main",
            "00000002  0000 5744 4546 0000       dc.w $0000,$5744,$4546,$0000  ; code resource header; '..WDEF..'",
            "0000000A  0001                      dc.w $0001  ; '..'",
            "",
            "main:",
            "0000000C  7000                      moveq #0,d0",
            "0000000E  4E75                      rts"), listing.Text);
    }

    [Fact]
    public void Raw_code_resource()
    {
        var listing = CodeListing.ForCodeResource(FourCC.FromString("FKEY"), 3, Bytes("4E71 4E75 FFFF"));
        Assert.Equal(Lines(
            "; 'FKEY' 3: 68k code resource",
            "",
            "entry:",
            "00000000  4E71                      nop",
            "00000002  4E75                      rts",
            "00000004  FFFF                      dc.w $FFFF  ; '..'"), listing.Text);
    }

    // Traps and MacsBug labels in one code resource:
    // 00 move.l #'sysv',d0; _Gestalt; move.l #$40,d0; _NewPtr ,SYS,CLEAR; _A0FA (unknown); move.w #$7F,-(sp);
    //    _Pack7 (selector $7F has no name); bsr $26; bsr $32; bsr $4A; _ExitToShell (the path ends);
    // 26 nop; rts; 'MOT32   ' (fixed-8, bit 7 clear, then a link);
    // 32 link a6; unlk a6; rts; 'DRAW    TView   ' (fixed-16: TView.DRAW);
    // 4A _GetResource ,AUTOPOP (the path ends); 4C nop; rts (no path reaches them: the gap sweep finds them);
    // 50 fmove.d #imm,fp0 (6 words, wider than the hex column); rts.
    [Fact]
    public void Traps_and_MacsBug_labels()
    {
        var data = Bytes("203C 7379 7376 A1AD 203C 0000 0040 A71E A0FA 3F3C 007F A9EE 6100 000C 6100 0014 6100 0028 A9F4"
            + " 4E71 4E75 4D4F 5433 3220 2020"
            + " 4E56 0000 4E5E 4E75 C4D2 4157 2020 2020 5456 6965 7720 2020"
            + " ADA0 4E71 4E75"
            + " F23C 5400 3FF0 0000 0000 0000 4E75");
        var listing = CodeListing.ForCodeResource(FourCC.FromString("FKEY"), 4, data);
        Assert.Equal(Lines(
            "; 'FKEY' 4: 68k code resource",
            "",
            "entry:",
            "00000000  203C 7379 7376            move.l #$73797376,d0  ; 'sysv'",
            "00000006  A1AD                      _Gestalt  ; 'sysv' gestaltSystemVersion",
            "00000008  203C 0000 0040            move.l #64,d0",
            "0000000E  A71E                      _NewPtr ,SYS,CLEAR",
            "00000010  A0FA                      _A0FA",
            "00000012  3F3C 007F                 move.w #127,-(sp)",
            "00000016  A9EE                      _Pack7  ; selector $7F",
            "00000018  6100 000C                 bsr.w $0026  ; MOT32",
            "0000001C  6100 0014                 bsr.w $0032  ; TView.DRAW",
            "00000020  6100 0028                 bsr.w $004A  ; sub_004A",
            "00000024  A9F4                      _ExitToShell",
            "",
            "MOT32:",
            "00000026  4E71                      nop",
            "00000028  4E75                      rts",
            "0000002A  4D4F 5433 3220 2020       dc.w $4D4F,$5433,$3220,$2020  ; MacsBug name MOT32; 'MOT32   '",
            "",
            "TView.DRAW:",
            "00000032  4E56 0000                 link a6,#0",
            "00000036  4E5E                      unlk a6",
            "00000038  4E75                      rts",
            "0000003A  C4D2 4157 2020 2020       dc.w $C4D2,$4157,$2020,$2020  ; MacsBug name TView.DRAW; '..AW    '",
            "00000042  5456 6965 7720 2020       dc.w $5456,$6965,$7720,$2020  ; 'TView   '",
            "",
            "sub_004A:",
            "0000004A  ADA0                      _GetResource ,AUTOPOP",
            "",
            "sub_004C:",
            "0000004C  4E71                      nop",
            "0000004E  4E75                      rts",
            "",
            "sub_0050:",
            "00000050  F23C 5400 3FF0 0000 0000 0000  fmove.d #$3FF0000000000000,fp0",
            "0000005C  4E75                      rts"), listing.Text);
        Assert.Equal(
        [
            new CodeFunction(0, 0, "entry", CodeFunctionSource.Entry),
            new CodeFunction(0, 0x26, "MOT32", CodeFunctionSource.Call),
            new CodeFunction(0, 0x32, "TView.DRAW", CodeFunctionSource.Call),
            new CodeFunction(0, 0x4A, "sub_004A", CodeFunctionSource.Call),
            new CodeFunction(0, 0x4C, "sub_004C", CodeFunctionSource.Gap),   // after the auto-pop trap
            new CodeFunction(0, 0x50, "sub_0050", CodeFunctionSource.Gap),
        ], listing.Functions);
        Assert.Contains(new CodeReference(0, 0x16, CodeReferenceKind.Selector, "selector $7F"), listing.References);
        Assert.Contains(new CodeReference(0, 0x10, CodeReferenceKind.Trap, "_A0FA"), listing.References);
    }

    // pea 4(pc) points at a Pascal string after the rts: it is data.
    [Fact]
    public void Pc_relative_string()
    {
        var listing = CodeListing.ForCodeResource(FourCC.FromString("FKEY"), 5, With("487A 0004 4E75 05", "Hello", ""));
        Assert.Equal(Lines(
            "; 'FKEY' 5: 68k code resource",
            "",
            "entry:",
            "00000000  487A 0004                 pea 4(pc)  ; $0006; P'Hello'",
            "00000004  4E75                      rts",
            "00000006  0548 656C 6C6F            dc.w $0548,$656C,$6C6F  ; string; '.Hello'"), listing.Text);
    }

    [Fact]
    public void Driver()
    {
        // flags, delay, mask, menu; open $18, prime $1A, control $1A, status 0, close $1C; ".D"; then the routines.
        var data = Bytes("4F00 0000 0000 0000 0018 001A 001A 0000 001C 022E 4400 0000 7000 4E75 4E75");
        // $16 a pad word; $18 moveq #0,d0; $1A rts; $1C rts
        var listing = CodeListing.ForCodeResource(FourCC.FromString("DRVR"), 12, data);
        // Control shares Prime's routine at $1A: a function has one label, the first given (docs/formats/output/disassembly.md §1.5).
        Assert.Equal(
        [
            new CodeFunction(0, 0x18, "Open", CodeFunctionSource.DriverRoutine),
            new CodeFunction(0, 0x1A, "Prime", CodeFunctionSource.DriverRoutine),
            new CodeFunction(0, 0x1C, "Close", CodeFunctionSource.DriverRoutine),
        ], listing.Functions);
        Assert.DoesNotContain("Control:", listing.Text, StringComparison.Ordinal);
        Assert.StartsWith(Lines(
            "; 'DRVR' 12: 68k code resource",
            "; Driver \".D\": flags $4F00",
            "",
            "00000000  4F00 0000 0000 0000       dc.w $4F00,$0000,$0000,$0000  ; driver header; 'O.......'"), listing.Text);
    }

    [Fact]
    public void Package()
    {
        // _Debugger, 'PACK' 3, version 1, flags 0, selectors 0 to 1, offsets 8 (the rts at $0A + 8 = $12) and 0 (none).
        var data = Bytes("A9FF 5041 434B 0003 0001 0000 0001 0008 0000 4E75");
        var listing = CodeListing.ForCodeResource(FourCC.FromString("PACK"), 3, data);
        Assert.Equal([new CodeFunction(0, 0x12, "selector_0", CodeFunctionSource.PackageRoutine)], listing.Functions);
        Assert.Contains("; Package: 'PACK' 3, selectors 0 to 1", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CODE_resource_with_its_fork_is_a_segment()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1)]));
        fork.Add("CODE", 1, CodeBuilder.Near(0, 1, Bytes("4E75")));
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CODE"), 1, CodeBuilder.Near(0, 1, Bytes("4E75")), fork);
        Assert.StartsWith("; 'CODE' 1: 68k segment, near header\n; Model: unknown\n; Entry: CODE 1:+$4\n", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CODE_resource_with_a_damaged_CODE_0_is_listed_alone()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Bytes("0000 0010"));
        var data = CodeBuilder.Near(0, 1, Bytes("4E75"));
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CODE"), 1, data, fork);
        Assert.Equal("code.listing-application", Assert.Single(listing.Diagnostics).Code);
        Assert.StartsWith("; 'CODE' 1: 68k segment, near header\n\n", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CODE_resource_without_an_application()
    {
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CODE"), 1, CodeBuilder.Near(0, 1, Bytes("4E75")));
        Assert.Equal(Lines(
            "; 'CODE' 1: 68k segment, near header",
            "",
            "00000000  0000 0001                 dc.w $0000,$0001  ; segment header; '....'",
            "",
            "entry:",
            "00000004  4E75                      rts"), listing.Text);
    }

    [Fact]
    public void CODE_0_is_the_jump_table()
    {
        var code0 = CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1)]);
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CODE"), 0, code0);
        Assert.Equal(Lines(
            "; 'CODE' 0: the jump table",
            "",
            "00000000  0000 0028 0000 0100       dc.w $0000,$0028,$0000,$0100  ; jump table; '...(....'",
            "00000008  0000 0008 0000 0020       dc.w $0000,$0008,$0000,$0020  ; '....... '",
            "00000010  0000 3F3C 0001 A9F0       dc.w $0000,$3F3C,$0001,$A9F0  ; '..?<....'"), listing.Text);
    }

    [Fact]
    public void Odd_length_data_uses_dc_b()
    {
        var listing = CodeListing.ForCodeResource(FourCC.FromString("FKEY"), 3, Bytes("4E75 FFFF 41"));
        Assert.EndsWith(Lines(
            "00000002  FFFF                      dc.w $FFFF  ; '..'",
            "00000004  41                        dc.b $41  ; 'A'"), listing.Text);
    }

    [Fact]
    public void Fragment()
    {
        var pef = PpcFragmentMapTests.Read(PpcFragmentMapTests.Fragment());
        var listing = CodeListing.ForFragment(pef, "Test");
        Assert.Equal(Lines(
            "; \"Test\": PowerPC fragment ('pwpc'), 3 sections",
            "; Section 0: Code, 0x50 bytes",
            "; Section 1: UnpackedData, 0x1C bytes",
            "; Section 2: Loader, 0x" + pef.Sections[2].ContainerLength.ToString("X") + " bytes",
            "; Main: 1:0x0 -> 0:0x0",
            "; TOC base: 1:0x8",
            "; Imports: 1 from 1 library",
            "; Exports: 1",
            "",
            "main:",
            "0:00000000  7C0802A6  mflr r0",
            "0:00000004  48000015  bl 0x18  ; InterfaceLib::InitGraf",
            "0:00000008  80410014  lwz r2,20(r1)",
            "0:0000000C  48000025  bl 0x30  ; Helper",
            "0:00000010  80620004  lwz r3,4(r2)  ; 1:0x10",
            "0:00000014  4E800020  blr",
            "",
            ".InitGraf:",
            "0:00000018  81820000  lwz r12,0(r2)  ; InterfaceLib::InitGraf",
            "0:0000001C  90410014  stw r2,20(r1)",
            "0:00000020  800C0000  lwz r0,0(r12)",
            "0:00000024  804C0004  lwz r2,4(r12)",
            "0:00000028  7C0903A6  mtctr r0",
            "0:0000002C  4E800420  bctr",
            "",
            "Helper:",
            "0:00000030  4E800020  blr",
            "0:00000034  00000000 00002040 00000000 00000004  dc.l $00000000,$00002040,$00000000,$00000004  ; traceback table .Helper",
            "0:00000044  00072E48 656C7065 72000000  dc.l $00072E48,$656C7065,$72000000",
            "",
            "; Transition vectors",
            "1:00000000  00000000 00000008  dc.l $00000000,$00000008  ; main: code 0:0x0, TOC 1:0x8",
            "1:00000014  00000030 00000008  dc.l $00000030,$00000008  ; Helper: code 0:0x30, TOC 1:0x8"), listing.Text);
        Assert.Equal(3, listing.Functions.Count);
        Assert.Contains(new CodeReference(0, 4, CodeReferenceKind.Glue, "InterfaceLib::InitGraf"), listing.References);
        Assert.Contains(new CodeReference(0, 0x10, CodeReferenceKind.TocSlot, "1:0x10"), listing.References);
    }

    [Fact]
    public void Fragment_without_a_name_or_entry_points()
    {
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x4E800020, 0x00000000));
        b.WithLoader = false;
        var listing = CodeListing.ForFragment(PpcFragmentMapTests.Read(b.Build()));
        Assert.Equal(Lines(
            "; PowerPC fragment ('pwpc'), 1 section",
            "; Section 0: Code, 0x8 bytes",
            "",
            "sub_0000:",
            "0:00000000  4E800020  blr",
            "0:00000004  00000000  .long 0x0"), listing.Text);
    }

    [Fact]
    public void CFM_68K_fragment_is_not_disassembled()
    {
        var b = new PefBuilder { Architecture = FourCC.FromString("m68k"), WithLoader = false };
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x4E754E75));
        var listing = CodeListing.ForFragment(PpcFragmentMapTests.Read(b.Build()));
        Assert.Equal(Lines(
            "; CFM-68K fragment ('m68k'), 1 section",
            "; Section 0: Code, 0x4 bytes",
            "; Not PowerPC code: not disassembled",
            ""), listing.Text);
    }

    [Fact]
    public void Native_code_resource_is_its_fragment()
    {
        var bytes = PpcFragmentMapTests.Fragment();
        var listing = CodeListing.ForCodeResource(FourCC.FromString("ncod"), 5, bytes);
        Assert.StartsWith("; 'ncod' 5: PowerPC fragment ('pwpc'), 3 sections\n", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fat_code_resource_lists_the_68k_part_and_the_fragment()
    {
        // A routine descriptor at 0 with one PowerPC routine, relative, at offset $20: the fragment.
        var pef = PpcFragmentMapTests.Fragment();
        var w = new BigEndianWriter();
        w.WriteUInt16(0xAAFE);          // goMixedModeTrap
        w.WriteByte(7);                 // version
        w.WriteByte(0);                 // flags
        w.WriteUInt32(0);               // reserved1
        w.WriteByte(0);                 // reserved2
        w.WriteByte(0);                 // selectorInfo
        w.WriteUInt16(0);               // routineCount (one routine)
        w.WriteUInt32(0x3BB0);          // procInfo
        w.WriteByte(0);                 // reserved1
        w.WriteByte(1);                 // ISA: PowerPC
        w.WriteUInt16(RoutineRecord.RelativeFlag);
        w.WriteUInt32(0x20);            // procDescriptor: relative offset
        w.WriteUInt32(0);               // reserved2
        w.WriteUInt32(0);               // selector
        w.WriteBytes(pef);
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CDEF"), 0, w.ToArray());
        Assert.StartsWith(Lines(
            "; 'CDEF' 0: 68k code resource",
            "; Routine descriptor at $0: 1 routine",
            "",
            "00000000  AAFE 0700 0000 0000       dc.w $AAFE,$0700,$0000,$0000  ; routine descriptor; '........'"), listing.Text);
        var fragment = Assert.Single(listing.Fragments);
        Assert.StartsWith("; 'CDEF' 0 routine 0: PowerPC fragment ('pwpc')", fragment.Text, StringComparison.Ordinal);
        Assert.Contains(fragment.Text, listing.Text, StringComparison.Ordinal);
    }

    // An MPW near application: CODE 66 is the bootstrap (entry 0), CODE 1 has the saved entry, CODE 2 ends with the
    // %A5Init data; a 'cfrg' 0 makes it fat.
    [Fact]
    public void Bootstrap_entry_A5Init_and_fat_application()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0x0C, 66), CodeBuilder.NearEntry(0, 2)]));
        fork.Add("CODE", 1, CodeBuilder.Near(0, 0, Bytes("4E71 4E71 4E71 4E75")));
        var shim = new BigEndianWriter();
        shim.WriteBytes(CodeBuilder.Near(0, 1));
        shim.WriteUInt32(0x20);
        shim.WriteBytes(CodeBuilder.NearEntry(0x04, 1));
        shim.WriteBytes(CodeBuilder.Words(0x4E75));
        fork.Add("CODE", 66, shim.ToArray());
        // CODE 2: rts, then the %A5Init header (below $100, version 1, data at +16, relocs at +18), 0, 0, trailer.
        var a5 = new BigEndianWriter();
        a5.WriteBytes(CodeBuilder.Near(8, 1, Bytes("4E75")));
        a5.WriteUInt32(0x100);
        a5.WriteUInt16(1);
        a5.WriteUInt16(0);
        a5.WriteUInt32(16);
        a5.WriteUInt32(18);
        a5.WriteBytes(CodeBuilder.Words(0, 0));
        a5.WriteUInt32(6);
        a5.WriteFourCC(FourCC.FromString("mpwd"));
        fork.Add("CODE", 2, a5.ToArray(), "%A5Init");
        fork.Add("cfrg", 0, new byte[32]);
        var app = CodeApplication.Read(fork, new List<Diagnostic>());

        var code1 = CodeListing.ForSegment(app, 1);
        Assert.StartsWith(Lines(
            "; 'CODE' 1: 68k segment, near header",
            "; Model: MPW near",
            "; Entry: CODE 66:+$10",
            "; Original entry: CODE 1:+$8",
            "; Also PowerPC code: 'cfrg' 0"), code1.Text);
        Assert.Contains(new CodeFunction(0, 8, "original_entry", CodeFunctionSource.Entry), code1.Functions);

        var code2 = CodeListing.ForSegment(app, 2);
        Assert.Contains("00000006  0000 0100 0001 0000       dc.w $0000,$0100,$0001,$0000  ; %A5Init data; '........'",
            code2.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_too_short_for_its_header()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1)]));
        fork.Add("CODE", 1, Bytes("0000"));
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var listing = CodeListing.ForSegment(app, 1);
        Assert.StartsWith("; 'CODE' 1: 68k segment, too short for its header\n", listing.Text, StringComparison.Ordinal);
        Assert.EndsWith("00000000  0000                      dc.w $0000  ; segment header; '..'\n", listing.Text, StringComparison.Ordinal);
        Assert.StartsWith("; 'CODE' 1: 68k segment, too short for its header\n",
            CodeListing.ForCodeResource(FourCC.FromString("CODE"), 1, Bytes("0000")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_id_out_of_range_throws() =>
        Assert.Throws<ArgumentException>(() => CodeListing.ForSegment(App(), 70000));

    [Fact]
    public void Retro68_model()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.FarEntry(1, 0x28), CodeBuilder.FarMarker]));
        fork.Add("CODE", 1, CodeBuilder.Far(0, 0, 0, 1, Bytes("41F9 0000 0010 4E75")));
        // RELA 1: position 2 from the code start (step 3 from -1), base 3 (A5).
        fork.Add("RELA", 1, Bytes("0F 00"));
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var listing = CodeListing.ForSegment(app, 1);
        Assert.Contains("; Model: Retro68\n", listing.Text, StringComparison.Ordinal);
        Assert.Contains("lea ($00000010).l,a0  ; A5+$10", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeWarrior_model()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 1)]));
        // CodeWarrior's startup: suba.l a6,a6; subq.l #4,sp; move.l #'CODE',-(sp); then rts.
        fork.Add("CODE", 1, CodeBuilder.Near(0, 1, Bytes("9DCE 598F 2F3C 434F 4445 4E75")));
        fork.Add("DATA", 0, new byte[64]);
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        Assert.Equal(CodeModel.CodeWarrior, app.Model);
        Assert.Contains("; Model: CodeWarrior\n", CodeListing.ForSegment(app, 1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lone_far_segment_annotates_its_A5_relocations()
    {
        var code = Bytes("41F9 0000 0100 4E75 1500");
        var listing = CodeListing.ForCodeResource(FourCC.FromString("CODE"), 4, CodeBuilder.Far(0, 0, 0, 0, code, a5Relocations: 0x30));
        Assert.Contains("lea ($00000100).l,a0  ; A5+$100", listing.Text, StringComparison.Ordinal);
        Assert.Contains("00000030  1500                      dc.w $1500  ; relocations; '..'", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Switch_table_and_odd_data()
    {
        // 0 add.w d0,d0; 2 move.w 6(pc,d0.w),d0; 6 jmp 2(pc,d0.w); A table 4, 6; E rts; 10 rts; 12 odd tail: dc.b
        var listing = CodeListing.ForCodeResource(FourCC.FromString("FKEY"), 1, Bytes("D040 303B 0006 4EFB 0002 0004 0006 4E75 4E75 41"));
        Assert.Contains("0000000A  0004 0006                 dc.w $0004,$0006  ; switch table; '....'", listing.Text, StringComparison.Ordinal);
        Assert.EndsWith("00000012  41                        dc.b $41  ; 'A'\n", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fragment_with_init_term_section_names_and_a_tail()
    {
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, [.. PefBuilder.Words(0x4E800020, 0x4BFFFFFC), 0xAB, 0xCD], name: "text");
        b.AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(0, 8, 4, 8));
        b.Libraries.Add(new PefBuilder.Library("A", [new PefBuilder.Import("x")]));
        b.Libraries.Add(new PefBuilder.Library("B", [new PefBuilder.Import("y")]));
        b.Relocations.Add((1, [0x4601]));   // DSC2 2
        b.Init = (1, 0);
        b.Term = (1, 8);
        var listing = CodeListing.ForFragment(PpcFragmentMapTests.Read(b.Build()));
        Assert.StartsWith(Lines(
            "; PowerPC fragment ('pwpc'), 3 sections",
            "; Section 0: Code, 0xA bytes \"text\"",
            "; Section 1: UnpackedData, 0x10 bytes"), listing.Text);
        Assert.Contains("; Init: 1:0x0 -> 0:0x0\n; Term: 1:0x8 -> 0:0x4\n", listing.Text, StringComparison.Ordinal);
        Assert.Contains("; Imports: 2 from 2 libraries\n", listing.Text, StringComparison.Ordinal);
        Assert.Contains("0:00000004  4BFFFFFC  b 0x0  ; init\n", listing.Text, StringComparison.Ordinal);
        Assert.Contains("0:00000008  AB  dc.b $AB\n0:00000009  CD  dc.b $CD\n", listing.Text, StringComparison.Ordinal);
        Assert.Equal(
        [
            new CodeFunction(0, 0, "init", CodeFunctionSource.Init),
            new CodeFunction(0, 4, "term", CodeFunctionSource.Term),
        ], listing.Functions);
    }

    [Fact]
    public void Fragment_of_another_architecture()
    {
        var b = new PefBuilder { Architecture = FourCC.FromString("abcd"), WithLoader = false };
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0));
        Assert.StartsWith("; fragment ('abcd'), 1 section\n",
            CodeListing.ForFragment(PpcFragmentMapTests.Read(b.Build())).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tail_call_to_glue_names_the_import()
    {
        // 0 b $4 (glue); 4 glue for TOC[0].
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x48000004, 0x81820000, 0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420));
        b.AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(0, 8, 0));
        b.Libraries.Add(new PefBuilder.Library("L", [new PefBuilder.Import("F")]));
        b.Relocations.Add((1, [0x4600, 0x4A00]));
        b.Main = (1, 0);
        var listing = CodeListing.ForFragment(PpcFragmentMapTests.Read(b.Build()));
        Assert.Contains("0:00000000  48000004  b 0x4  ; L::F\n", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_returned_by_ToString() =>
        Assert.Equal(CodeListing.ForSegment(App(), 1).Text, CodeListing.ForSegment(App(), 1).ToString());
}
