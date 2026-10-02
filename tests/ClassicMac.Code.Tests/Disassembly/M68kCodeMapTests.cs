using ClassicMac.Code.Disassembly;
using static ClassicMac.Code.Tests.Disassembly.M68kDisassemblerTests;

namespace ClassicMac.Code.Tests.Disassembly;

// Recursive descent over hand-built 68k code: what is reached from the entries, what the gap sweep finds, and what is
// left as data.
public class M68kCodeMapTests
{
    private static M68kCodeMap Map(string hex, params int[] entries) =>
        M68kCodeMap.Build(Bytes(hex), 0, entries.Select(e => new M68kEntry(e, null, CodeFunctionSource.Entry)), []);

    private static byte[] With(string hex, string text, string tail) =>
        [.. Bytes(hex), .. System.Text.Encoding.ASCII.GetBytes(text), .. Bytes(tail)];

    [Fact]
    public void Follows_fall_through_and_stops_at_rts()
    {
        // link a6,#0; unlk a6; rts
        var map = Map("4E56 0000 4E5E 4E75", 0);
        Assert.Equal([0, 4, 6], map.Instructions.Keys);
        Assert.Empty(map.Swept);
        Assert.Empty(map.Data);
        var f = Assert.Single(map.Functions.Values);
        Assert.Equal(new CodeFunction(0, 0, "sub_0000", CodeFunctionSource.Entry), f);
    }

    [Fact]
    public void Follows_both_ways_of_a_conditional_branch()
    {
        // 0 tst.w d0; 2 beq.s 8; 4 moveq #1,d0; 6 rts; 8 moveq #2,d0; A rts
        var map = Map("4A40 6704 7001 4E75 7002 4E75", 0);
        Assert.Equal([0, 2, 4, 6, 8, 10], map.Instructions.Keys);
        Assert.Empty(map.Swept);
        Assert.Contains(8, map.BranchTargets);
    }

    [Fact]
    public void A_branch_into_data_stops_there()
    {
        // 0 tst.w d0; 2 beq.s 6; 4 rts; 6 dc.w $FFFF (not an instruction)
        var map = Map("4A40 6702 4E75 FFFF", 0);
        Assert.Equal([0, 2, 4], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(6, 2, M68kDataKind.Unknown)], map.Data);
    }

    [Fact]
    public void An_unreachable_gap_is_swept()
    {
        // 0 rts; 2 nop; 4 rts (never reached), 6 dc.w $FFFF
        var map = Map("4E75 4E71 4E75 FFFF", 0);
        Assert.Equal([0, 2, 4], map.Instructions.Keys);
        Assert.Equal([2, 4], map.Swept.Order());
        Assert.Equal([new M68kDataRegion(6, 2, M68kDataKind.Unknown)], map.Data);
        Assert.Equal(CodeFunctionSource.Gap, map.Functions[2].Source);
        Assert.Equal("sub_0002", map.Functions[2].Name);
    }

    [Fact]
    public void Swept_code_after_a_return_or_a_string_starts_a_function()
    {
        // 0 rts; 2 nop; 4 rts; 6 nop; 8 rts — then a string the swept code points at, then more code.
        var map = Map("4E75 4E71 4E75 4E71 4E75", 0);
        Assert.Equal([0, 2, 6], map.Functions.Keys);
        // 0 rts; 2 lea 6(pc),a0 (-> $A); 6 nop; 8 nop; A "\x02ab" (data); E rts
        var after = Map("4E75 41FA 0006 4E71 4E71 0261 6200 4E75", 0);
        Assert.Equal([0, 2, 0xE], after.Functions.Keys);
    }

    [Fact]
    public void A_gap_starting_with_data_is_not_a_function()
    {
        var map = Map("4E75 FFFF 4E71 4E75", 0);
        Assert.Equal([0], map.Functions.Keys);
        Assert.Equal([4, 6], map.Swept.Order());
    }

    [Fact]
    public void Calls_are_functions()
    {
        // 0 bsr.s 6; 2 rts; 4 dc.w; 6 moveq #0,d0; 8 rts
        var map = Map("6104 4E75 FFFF 7000 4E75", 0);
        Assert.Equal([0, 2, 6, 8], map.Instructions.Keys);
        Assert.Equal(new CodeFunction(0, 6, "sub_0006", CodeFunctionSource.Call), map.Functions[6]);
    }

    [Fact]
    public void A_switch_table_bounded_by_cmpi()
    {
        // 00 cmpi.w #2,d0; 04 bgt.w $1C; 08 add.w d0,d0; 0A move.w 6(pc,d0.w),d0; 0E jmp 2(pc,d0.w);
        // 12 table (from $12): 6, 8, 10; 18 rts; 1A nop; 1C rts
        var map = Map("0C40 0002 6E00 0016 D040 303B 0006 4EFB 0002 0006 0008 000A 4E75 4E71 4E75", 0);
        Assert.Equal([0, 4, 8, 10, 14, 0x18, 0x1A, 0x1C], map.Instructions.Keys);
        Assert.Empty(map.Swept);
        Assert.Equal([new M68kDataRegion(0x12, 6, M68kDataKind.SwitchTable)], map.Data);
        Assert.Contains(0x1A, map.BranchTargets);
    }

    [Fact]
    public void A_switch_table_bounded_by_its_first_target()
    {
        // As above without the bound: the table ends where the code it points at starts.
        var map = Map("D040 303B 0006 4EFB 0002 0006 0008 000A 4E75 4E71 4E75", 0);
        Assert.Equal([0, 2, 6, 0x10, 0x12, 0x14], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(0x0A, 6, M68kDataKind.SwitchTable)], map.Data);
    }

    [Fact]
    public void A_switch_table_with_a_separate_jump_base()
    {
        // MPW's other shape: the table at move.w's base, the offsets from jmp's (the extension word).
        // 0 add.w d0,d0; 2 move.w 6(pc,d0.w),d0 (table $A); 6 jmp 0(pc,d0.w) (base 8); A table 6, 8; E rts; 10 rts
        var map = Map("D040 303B 0006 4EFB 0000 0006 0008 4E75 4E75", 0);
        Assert.Equal([0, 2, 6, 0x0E, 0x10], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(0x0A, 4, M68kDataKind.SwitchTable)], map.Data);
    }

    [Fact]
    public void A_table_of_branches_after_an_indexed_jmp()
    {
        // 0 jmp 2(pc,d0.w); 4 bra.s $A; 6 bra.s $C; 8 nop; A rts; C rts
        var map = Map("4EFB 0002 6004 6004 4E71 4E75 4E75", 0);
        Assert.Equal([0, 4, 6, 8, 10, 12], map.Instructions.Keys);
        Assert.Empty(map.Swept);
    }

    [Fact]
    public void The_mixed_mode_magic_word_stops_decoding()
    {
        // nop; then a routine descriptor: _MixedModeMagic, version 7, ... (data to the end)
        var map = Map("4E71 4E71 AAFE 0700 0000 0000 4E75", 0);
        Assert.Equal([0, 2], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(4, 10, M68kDataKind.RoutineDescriptor)], map.Data);
    }

    [Fact]
    public void The_mixed_mode_magic_word_in_a_gap_makes_the_rest_of_the_gap_data()
    {
        var map = Map("4E75 4E71 AAFE 0700 0000", 0);
        Assert.Equal([0, 2], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(4, 6, M68kDataKind.RoutineDescriptor)], map.Data);
    }

    [Fact]
    public void MacsBug_names_bound_functions()
    {
        // f1: link, unlk, rts, "Fone"; f2 (reached only as the next function): nop, rts, "Ftwo" with 2 bytes of literals.
        byte[] code = [.. With("4E56 0000 4E5E 4E75 84", "Fone", "00 0000"), .. With("4E71 4E75 84", "Ftwo", "00 0002 1234")];
        var map = M68kCodeMap.Build(code, 0, [new M68kEntry(0, null, CodeFunctionSource.Entry)], []);
        Assert.Equal(["Fone", "Ftwo"], map.Functions.Values.Select(f => f.Name));
        Assert.Equal(CodeFunctionSource.MacsBug, map.Functions[0x10].Source);
        Assert.Equal(CodeFunctionSource.Entry, map.Functions[0].Source);
        Assert.Equal([0, 4, 6, 0x10, 0x12], map.Instructions.Keys);
        Assert.Empty(map.Swept);
        Assert.Equal(
        [
            new M68kDataRegion(8, 8, M68kDataKind.MacsBugName, "Fone"),
            new M68kDataRegion(0x14, 8, M68kDataKind.MacsBugName, "Ftwo"),
            new M68kDataRegion(0x1C, 2, M68kDataKind.Literals),
        ], map.Data);
        Assert.Equal(2, map.MacsBugNames.Count);
    }

    [Fact]
    public void Given_data_regions_are_not_decoded()
    {
        // A 4-byte header, then nop; rts. The header is data even though it decodes.
        var map = M68kCodeMap.Build(Bytes("4E71 4E71 4E71 4E75"), 4, [new M68kEntry(4, "main", CodeFunctionSource.Entry)],
            [new M68kDataRegion(0, 4, M68kDataKind.Header)]);
        Assert.Equal([4, 6], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(0, 4, M68kDataKind.Header)], map.Data);
        Assert.Equal("main", map.Functions[4].Name);
    }

    [Fact]
    public void Bytes_before_the_start_are_data()
    {
        var map = M68kCodeMap.Build(Bytes("1234 4E75"), 2, [new M68kEntry(2, null, CodeFunctionSource.Entry)], []);
        Assert.Equal([new M68kDataRegion(0, 2, M68kDataKind.Header)], map.Data);
    }

    [Fact]
    public void A_branch_into_an_instruction_is_not_decoded()
    {
        // 0 beq.s 4 (inside the move.l's immediate); 2 move.l #$4E714E75,d0; 8 rts
        var map = Map("6702 203C 4E71 4E75 4E75", 0);
        Assert.Equal([0, 2, 8], map.Instructions.Keys);
    }

    [Fact]
    public void Targets_outside_the_code_and_odd_entries_are_ignored()
    {
        // bra.w far away; an odd entry; an entry past the end.
        var map = Map("6000 1000 4E75", 0, 3, 100);
        Assert.Equal([0, 4], map.Instructions.Keys);
        Assert.Equal([4], map.Swept);
    }

    [Fact]
    public void Absolute_jumps_are_not_followed()
    {
        // jmp ($00000006).l; nop (never reached: the address is not relocated to this code)
        var map = Map("4EF9 0000 0008 4E71 4E75", 0);
        Assert.Equal([6, 8], map.Swept.Order());
    }

    [Fact]
    public void Absolute_calls_relocated_to_this_code_are_followed()
    {
        // 0 jsr ($00000008).l, its long relocated by this code's address; 6 rts; 8 nop; A rts.
        var map = M68kCodeMap.Build(Bytes("4EB9 0000 0008 4E75 4E71 4E75"), 0, [new M68kEntry(0, null, CodeFunctionSource.Entry)], [],
            new HashSet<int> { 2 });
        Assert.Empty(map.Swept);
        Assert.Equal(CodeFunctionSource.Call, map.Functions[8].Source);
        // jmp ($00000008).l likewise, without a fall-through.
        var jump = M68kCodeMap.Build(Bytes("4EF9 0000 0008 4E75 4E71 4E75"), 0, [new M68kEntry(0, null, CodeFunctionSource.Entry)], [],
            new HashSet<int> { 2 });
        Assert.Equal([6], jump.Swept);
        Assert.Contains(8, jump.BranchTargets);
    }

    [Fact]
    public void ExitToShell_and_auto_pop_traps_do_not_return()
    {
        Assert.Equal([2], Map("A9F4 4E75", 0).Swept);
        Assert.Equal([2], Map("ADA0 4E75", 0).Swept);   // _GetResource ,AUTOPOP
        Assert.Empty(Map("A9A0 4E75", 0).Swept);
    }

    [Fact]
    public void An_instruction_running_into_data_is_data()
    {
        // jsr ($xxxxxxxx).l cut off by a given data region.
        var map = M68kCodeMap.Build(Bytes("4EB9 0000 1234 4E75"), 0, [new M68kEntry(0, null, CodeFunctionSource.Entry)],
            [new M68kDataRegion(4, 4, M68kDataKind.Header)]);
        Assert.Empty(map.Instructions);
        Assert.Equal([new M68kDataRegion(0, 4, M68kDataKind.Unknown), new M68kDataRegion(4, 4, M68kDataKind.Header)], map.Data);
    }

    [Fact]
    public void Function_names_prefer_MacsBug_then_the_entry_name()
    {
        byte[] code = [.. With("4E75 84", "Fone", "00 0000"), .. Bytes("4E75")];
        var map = M68kCodeMap.Build(code, 0,
            [new M68kEntry(0, "JT1", CodeFunctionSource.JumpTable), new M68kEntry(10, "JT2", CodeFunctionSource.JumpTable),
             new M68kEntry(10, "other", CodeFunctionSource.Entry)], []);
        Assert.Equal(new CodeFunction(0, 0, "Fone", CodeFunctionSource.JumpTable), map.Functions[0]);
        Assert.Equal(new CodeFunction(0, 10, "JT2", CodeFunctionSource.JumpTable), map.Functions[10]);
        Assert.Equal("Fone", map.NameAt(0));
        Assert.Null(map.NameAt(2));
    }

    [Fact]
    public void A_MacsBug_name_labels_the_last_known_function_before_it()
    {
        // 0 bsr.s 4; 2 rts; 4 nop; 6 rts; "Fone": the name belongs to the routine at 4 (a call target), not to 0.
        byte[] code = With("6102 4E75 4E71 4E75 84", "Fone", "00 0000");
        var map = M68kCodeMap.Build(code, 0, [new M68kEntry(0, null, CodeFunctionSource.Entry)], []);
        Assert.Equal(new CodeFunction(0, 0, "sub_0000", CodeFunctionSource.Entry), map.Functions[0]);
        Assert.Equal(new CodeFunction(0, 4, "Fone", CodeFunctionSource.Call), map.Functions[4]);
    }

    [Fact]
    public void Strings_at_PC_relative_operands_are_data()
    {
        // lea 4(pc),a0; rts; "\x05Hello": data, not swept as code.
        var map = Map("41FA 0004 4E75 0548 656C 6C6F", 0);
        Assert.Equal([0, 4], map.Instructions.Keys);
        Assert.Equal([new M68kDataRegion(6, 6, M68kDataKind.String)], map.Data);
        // A C string with its NUL.
        var c = Map("487A 0004 4E75 4869 2121 00", 0);
        Assert.Equal([new M68kDataRegion(6, 5, M68kDataKind.String)], c.Data);
        // Three characters and NUL is not taken for a C string: link a6,#0 reads "NV ".
        Assert.Equal([0, 4, 6, 0xA], Map("487A 0004 4E75 4E56 0000 4E75", 0).Instructions.Keys);
    }

    [Fact]
    public void Strings_ahead_of_swept_code_are_data()
    {
        // 0 rts; 2 (unreached) lea 4(pc),a0; 6 rts; 8 "abc"
        var map = Map("4E75 41FA 0004 4E75 0361 6263", 0);
        Assert.Equal([2, 6], map.Swept.Order());
        Assert.Equal([new M68kDataRegion(8, 4, M68kDataKind.String)], map.Data);
    }

    [Fact]
    public void Odd_trailing_byte_is_data()
    {
        var map = Map("4E75 4E", 0);
        Assert.Equal([new M68kDataRegion(2, 1, M68kDataKind.Unknown)], map.Data);
    }
}
