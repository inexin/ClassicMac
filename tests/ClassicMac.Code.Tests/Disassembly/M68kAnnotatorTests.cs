using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Tests.M68k;
using ClassicMac.Core;
using ClassicMac.Resources;
using static ClassicMac.Code.Tests.Disassembly.M68kDisassemblerTests;

namespace ClassicMac.Code.Tests.Disassembly;

// Each annotation rule on hand-built instructions.
public class M68kAnnotatorTests
{
    private static M68kCodeMap Map(byte[] code, int start = 0) =>
        M68kCodeMap.Build(code, start, [new M68kEntry(start, null, CodeFunctionSource.Entry)], []);

    private static List<(CodeReferenceKind, string)> At(M68kCodeMap map, int offset, M68kContext? context = null) =>
        M68kAnnotator.Annotate(map.Instructions[offset], map, context ?? new M68kContext())
            .Select(r => (r.Kind, r.Text)).ToList();

    private static List<(CodeReferenceKind, string)> Of(string hex, int offset = 0, M68kContext? context = null) =>
        At(Map(Bytes(hex)), offset, context);

    private static byte[] With(string hex, string text, string tail) =>
        [.. Bytes(hex), .. System.Text.Encoding.ASCII.GetBytes(text), .. Bytes(tail)];

    // CODE 0: entry 0 is CODE 2 at code offset 0 (resource offset 4), entry 1 is loaded, entry 2 is CODE 2 +$10.
    // CODE 2 starts with "rts" named Fone by MacsBug.
    private static CodeApplication App()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.NearEntry(0, 2), CodeBuilder.LoadedEntry(2, 0x1234), CodeBuilder.NearEntry(0x10, 2)]));
        fork.Add("CODE", 1, CodeBuilder.Near(0, 0, Bytes("4E75")));
        fork.Add("CODE", 2, CodeBuilder.Near(0, 3, With("4E75 84", "Fone", "00 0000")));
        return CodeApplication.Read(fork, new List<Diagnostic>());
    }

    [Fact]
    public void Jump_table_call_names_the_segment_offset_and_MacsBug_label()
    {
        var context = M68kContext.ForApplication(App(), 1);
        Assert.Equal([(CodeReferenceKind.JumpTable, "CODE 2:+$4 Fone")], Of("4EAD 0022", 0, context));   // jsr 34(a5)
        Assert.Equal([(CodeReferenceKind.JumpTable, "CODE 2:+$14")], Of("486D 0032", 0, context));       // pea 50(a5)
        Assert.Equal([(CodeReferenceKind.JumpTable, "JT 1")], Of("4EED 002A", 0, context));              // jmp 42(a5)
    }

    [Fact]
    public void A5_offsets_that_are_not_jump_table_targets_are_globals()
    {
        var context = M68kContext.ForApplication(App(), 1);
        Assert.Equal([(CodeReferenceKind.A5Global, "A5+$20")], Of("4EAD 0020", 0, context));     // not entry + 2
        Assert.Equal([(CodeReferenceKind.A5Global, "A5+$22")], Of("4EAD 0022"));                // no application
        Assert.Equal([(CodeReferenceKind.A5Global, "A5-$1F3A")], Of("302D E0C6"));              // move.w -7994(a5),d0
        Assert.Equal([(CodeReferenceKind.A5Global, "A5+$22")], Of("202D 0022", 0, context));    // a load, not a call
    }

    [Fact]
    public void Low_memory_globals_by_absolute_address()
    {
        Assert.Equal([(CodeReferenceKind.LowMemory, "Ticks")], Of("2038 016A"));          // move.l ($016A).w,d0
        Assert.Equal([(CodeReferenceKind.LowMemory, "Ticks")], Of("2039 0000 016A"));     // move.l ($0000016A).l,d0
        Assert.Equal([(CodeReferenceKind.LowMemory, "Ticks+2")], Of("3038 016C"));        // inside it
        Assert.Empty(Of("2039 0001 0000"));                                               // no global there
    }

    [Fact]
    public void Traps_are_references_with_their_modifiers()
    {
        Assert.Equal([(CodeReferenceKind.Trap, "_NewPtr ,SYS")], Of("A51E"));
        Assert.Equal([(CodeReferenceKind.Trap, "_A0C0")], Of("A0C0"));
    }

    [Fact]
    public void Stack_selector_pushed_before_the_trap()
    {
        // move.w #0,-(sp); _Pack7
        Assert.Equal((CodeReferenceKind.Selector, "NumToString"), Of("3F3C 0000 A9EE", 4)[1]);
        // clr.w -(sp); _Pack7
        Assert.Equal((CodeReferenceKind.Selector, "NumToString"), Of("4267 A9EE", 2)[1]);
    }

    [Fact]
    public void D0_selector_by_moveq_and_move()
    {
        Assert.Equal((CodeReferenceKind.Selector, "PBOpenWD"), Of("7001 A260", 2)[1]);            // moveq #1,d0
        Assert.Equal((CodeReferenceKind.Selector, "PBCloseWD"), Of("303C 0002 A260", 4)[1]);      // move.w #2,d0
        Assert.Equal((CodeReferenceKind.Selector, "PBCatMove"), Of("203C 0000 0005 A260", 6)[1]); // move.l #5,d0
        // An unrelated instruction between them is looked past.
        Assert.Equal((CodeReferenceKind.Selector, "PBOpenWD"), Of("7001 2208 A260", 4)[1]);
    }

    [Fact]
    public void Gestalt_selector_is_a_four_character_code()
    {
        // move.l #'sysv',d0; _Gestalt
        Assert.Equal((CodeReferenceKind.Selector, "'sysv' gestaltSystemVersion"), Of("203C 7379 7376 A1AD", 6)[1]);
        Assert.Equal((CodeReferenceKind.Selector, "'zzzz'"), Of("203C 7A7A 7A7A A1AD", 6)[1]);
    }

    [Fact]
    public void Unknown_selector_value()
    {
        Assert.Equal((CodeReferenceKind.Selector, "selector $7F"), Of("707F A260", 2)[1]);
    }

    [Theory]
    [InlineData("7001 4E71 4E71 4E71 A260", 8)]   // more than 3 instructions back
    [InlineData("7001 2001 A260", 4)]             // D0 written by another instruction first
    [InlineData("3F3C 0000 3F01 A9EE", 6)]        // another push between
    [InlineData("3F3C 0000 6100 0002 A9EE", 8)]   // bsr: the block ends at a call
    [InlineData("A260", 0)]                       // nothing before it
    public void Selector_not_found(string hex, int trap) =>
        Assert.DoesNotContain(Of(hex, trap), r => r.Item1 == CodeReferenceKind.Selector);

    [Fact]
    public void Selector_lookback_stays_in_the_block()
    {
        // 0 moveq #1,d0; 2 tst.w d1; 4 beq.s 6 (a branch target between); 6 _HFSDispatch
        var map = Map(Bytes("7001 4A41 6700 0002 A260"));
        Assert.DoesNotContain(At(map, 8), r => r.Item1 == CodeReferenceKind.Selector);
        // In the same block, with the branch target before the selector: found.
        var inBlock = Map(Bytes("4A41 6702 4E71 7001 A260"));
        Assert.Contains((CodeReferenceKind.Selector, "PBOpenWD"), At(inBlock, 8));
    }

    [Fact]
    public void Relocated_absolute_operands()
    {
        // 0 jsr ($00000018).l relocated by the segment; 6 lea ($FFFFFF00).l,a0 by A5; C move.l #$2A,-(sp) by A5;
        // 12 move.l ($0000016A).l,d0 by the segment (not a low-memory global); 18 rts
        var code = Bytes("4EB9 0000 0018 41F9 FFFF FF00 2F3C 0000 002A 2039 0000 016A 4E75");
        var context = new M68kContext
        {
            Application = App(),
            Segment = 3,
            Relocations = new Dictionary<long, M68kRelocation>
            {
                [2] = new(M68kRelocationBase.Segment, 3),
                [8] = new(M68kRelocationBase.A5, 0),
                [0xE] = new(M68kRelocationBase.A5, 0),
                [0x14] = new(M68kRelocationBase.Segment, 3),
            },
        };
        var map = M68kCodeMap.Build(code, 0, [new M68kEntry(0, null, CodeFunctionSource.Entry), new M68kEntry(0x18, "there", CodeFunctionSource.Entry)], []);
        Assert.Equal([(CodeReferenceKind.Relocation, "CODE 3+$18 there")], At(map, 0, context));
        Assert.Equal([(CodeReferenceKind.Relocation, "A5-$100")], At(map, 6, context));
        Assert.Equal([(CodeReferenceKind.Relocation, "A5+$2A JT 1")], At(map, 0xC, context));
        Assert.Equal([(CodeReferenceKind.Relocation, "CODE 3+$16A")], At(map, 0x12, context));
    }

    [Fact]
    public void Relocation_bases_for_data()
    {
        var code = Bytes("41F9 0000 0010 43F9 0000 0020 4E75");
        var context = new M68kContext
        {
            Relocations = new Dictionary<long, M68kRelocation>
            {
                [2] = new(M68kRelocationBase.InitializedData, 0),
                [8] = new(M68kRelocationBase.UninitializedData, 0),
            },
        };
        var map = Map(code);
        Assert.Equal([(CodeReferenceKind.Relocation, "DATA+$10")], At(map, 0, context));
        Assert.Equal([(CodeReferenceKind.Relocation, "BSS+$20")], At(map, 6, context));
    }

    [Fact]
    public void Relocation_of_a_jump_table_target()
    {
        var context = new M68kContext
        {
            Application = App(),
            Segment = 1,
            Relocations = new Dictionary<long, M68kRelocation> { [2] = new(M68kRelocationBase.A5, 0) },
            FunctionName = (s, o) => s == 2 && o == 4 ? "Fone" : null,
        };
        Assert.Equal([(CodeReferenceKind.Relocation, "A5+$22 CODE 2:+$4 Fone")], At(Map(Bytes("2F3C 0000 0022 4E75")), 0, context));
    }

    [Fact]
    public void Pascal_and_C_strings_at_PC_relative_operands()
    {
        // lea 4(pc),a0 (-> 6); rts; "\x05Hello"
        Assert.Contains((CodeReferenceKind.String, "P'Hello'"), Of("41FA 0004 4E75 0548 656C 6C6F"));
        // pea 4(pc) (-> 6); rts; "Hi there\0"
        Assert.Contains((CodeReferenceKind.String, "C'Hi there'"), Of("487A 0004 4E75 4869 2074 6865 7265 00"));
        // Not a string: binary bytes.
        Assert.DoesNotContain(Of("41FA 0004 4E75 0301 0203"), r => r.Item1 == CodeReferenceKind.String);
        // One character is not a C string.
        Assert.DoesNotContain(Of("41FA 0004 4E75 4100"), r => r.Item1 == CodeReferenceKind.String);
    }

    [Fact]
    public void Long_strings_are_cut()
    {
        string text = new('x', 60);
        var code = With("41FA 0004 4E75 3C", text, "");
        var s = Assert.Single(At(Map(code), 0), r => r.Item1 == CodeReferenceKind.String);
        Assert.Equal("P'" + new string('x', 40) + "...'", s.Item2);
    }

    [Fact]
    public void Calls_to_labelled_functions()
    {
        // 0 bsr.s 4; 2 rts; 4 rts, with 4 named
        var map = M68kCodeMap.Build(Bytes("6102 4E75 4E75"), 0,
            [new M68kEntry(0, null, CodeFunctionSource.Entry), new M68kEntry(4, "Helper", CodeFunctionSource.Entry)], []);
        Assert.Equal([(CodeReferenceKind.Call, "Helper")], At(map, 0));
    }

    [Fact]
    public void Context_from_a_far_application()
    {
        // Far model: entry 1 the marker; CODE 1 far, with one A5 and one PC relocation.
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.FarEntry(1, 0x28), CodeBuilder.FarMarker]));
        // Code at $28: lea ($00000100).l,a0 (long at $2A, A5); jsr ($00000030).l (long at $30, PC); rts.
        // Lists at $36: A5: delta $2A/2 = $15; PC: $30/2 = $18.
        var code = Bytes("41F9 0000 0100 4EB9 0000 0030 4E75 1500 1800");
        var segment = CodeBuilder.Far(0, 0, 0, 1, code, a5Relocations: 0x36, pcRelocations: 0x38);
        fork.Add("CODE", 1, segment);
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var context = M68kContext.ForApplication(app, 1);
        Assert.Equal(M68kRelocationBase.A5, context.Relocations[0x2A].Base);
        Assert.Equal(new M68kRelocation(M68kRelocationBase.Segment, 1), context.Relocations[0x30]);
    }

    [Fact]
    public void Context_from_Retro68_relocations()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, CodeBuilder.Code0([CodeBuilder.FarEntry(1, 0x28), CodeBuilder.FarMarker]));
        fork.Add("CODE", 1, CodeBuilder.Far(0, 0, 0, 1, Bytes("41F9 0000 0010 43F9 0000 0020 45F9 0000 0030 47F9 0000 0040 4E75")));
        // RELA 1: positions from the code start (first step from -1): 2 (code), 8 (data), 14 (bss), 20 (A5).
        fork.Add("RELA", 1, Bytes("0C 19 1A 1B 00"));
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var context = M68kContext.ForApplication(app, 1);
        Assert.Equal(
        [
            (0x2AL, M68kRelocationBase.Segment), (0x30L, M68kRelocationBase.InitializedData),
            (0x36L, M68kRelocationBase.UninitializedData), (0x3CL, M68kRelocationBase.A5),
        ], context.Relocations.OrderBy(r => r.Key).Select(r => (r.Key, r.Value.Base)));
    }

    [Fact]
    public void Context_names_functions_in_other_segments_by_MacsBug()
    {
        var context = M68kContext.ForApplication(App(), 1);
        Assert.Equal("Fone", context.FunctionName(2, 4));
        Assert.Null(context.FunctionName(2, 6));
        Assert.Null(context.FunctionName(9, 4));
    }
}
