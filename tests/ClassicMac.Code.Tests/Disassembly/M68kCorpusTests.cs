using ClassicMac.Code.Disassembly;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.Disassembly;

// A linear sweep over real 68k code (CLASSICMAC_CODE_CORPUS) decodes without an exception, every instruction inside
// the segment. The counts were measured by this disassembler [Verified: the samples named in each test]; the jsr
// n(A5) count of ResEdit's CODE 1 matches an independent listing, and decoding at every address of Ghidra listings of
// Realmz CODE 1 (121 337 instructions) and Disk Copy's CODE resources (69 737) gave the same lengths throughout.
public class M68kCorpusTests
{
    private static readonly FourCC Code = FourCC.FromString("CODE");

    internal readonly record struct Sweep(int Instructions, int Invalid, int Bytes, int JumpTableCalls);

    internal static Sweep Disassemble(ReadOnlyMemory<byte> segment, int headerLength)
    {
        int instructions = 0, invalid = 0, bytes = 0, jumpTableCalls = 0;
        uint end = (uint)segment.Length;
        foreach (var ins in M68kDisassembler.Disassemble(segment[headerLength..], (uint)headerLength, TrapNames.Describe))
        {
            Assert.InRange(ins.Address + (uint)ins.Length, (uint)headerLength + 1, end);
            Assert.False(string.IsNullOrEmpty(ins.Text));
            instructions++;
            bytes += ins.Length;
            if (ins.IsInvalid)
                invalid++;
            if (ins is { Mnemonic: "jsr", Operands: [M68kEffectiveAddress { Mode: M68kAddressingMode.Displacement, Register: 5 }] })
                jumpTableCalls++;
        }
        Assert.Equal(segment.Length - headerLength, bytes);
        return new Sweep(instructions, invalid, bytes, jumpTableCalls);
    }

    private static ReadOnlyMemory<byte> CodeResource(ResourceFork fork, short id)
    {
        var resource = fork.Resources.Single(r => r.Type == Code && r.Id == id);
        var diagnostics = new List<Diagnostic>();
        var data = ResourceDecompression.Default.GetData(resource, fork, null, diagnostics);
        Assert.Empty(diagnostics);
        return data;
    }

    [Fact]
    public void Realmz_CODE_1()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955));
        var sweep = Disassemble(CodeResource(fork, 1), 4);
        // 889 dc.w in 128 106 (0.69 %): data between functions (jump tables, strings, MacsBug names).
        Assert.Equal(new Sweep(128106, 889, 485272, 0), sweep);
    }

    [Fact]
    public void ResEdit_CODE_1()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("ResEdit.rsrc", 670639));
        var sweep = Disassemble(CodeResource(fork, 1), 4);
        // 46 dc.w in 9 800 (0.47 %); all 145 jsr n(A5) of the independent listing.
        Assert.Equal(new Sweep(9800, 46, 29224, 145), sweep);
    }

    // Disk Copy 6.1.2's far-model segments (header $FFFF, 40 bytes).
    [Fact]
    public void DiskCopy_far_segments()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("DC612", 434333));
        int segments = 0, instructions = 0, invalid = 0;
        foreach (var resource in fork.Resources.Where(r => r.Type == Code && r.Id != 0).OrderBy(r => r.Id))
        {
            var data = CodeResource(fork, resource.Id);
            if (data.Length < 0x28 || data.Span[0] != 0xFF || data.Span[1] != 0xFF)
                continue;
            var sweep = Disassemble(data, 0x28);
            segments++;
            instructions += sweep.Instructions;
            invalid += sweep.Invalid;
        }
        // 29 far segments (CODE 30 is the near-model entry); 794 dc.w in 72 675 (1.09 %).
        Assert.Equal((29, 72675, 794), (segments, instructions, invalid));
    }
}
