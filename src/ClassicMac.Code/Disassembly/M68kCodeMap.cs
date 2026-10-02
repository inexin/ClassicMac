using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>What a run of bytes that is not code holds.</summary>
internal enum M68kDataKind
{
    /// <summary>A header before the code (a segment or code-resource header).</summary>
    Header,
    /// <summary>A MacsBug procedure name.</summary>
    MacsBugName,
    /// <summary>The literals after a MacsBug name.</summary>
    Literals,
    /// <summary>A switch statement's table of offsets.</summary>
    SwitchTable,
    /// <summary>A routine descriptor (<c>$AAFE</c>) and what follows it.</summary>
    RoutineDescriptor,
    /// <summary>Relocation lists.</summary>
    Relocations,
    /// <summary>A global-data initializer's packed data.</summary>
    Initializer,
    /// <summary>Bytes that do not decode, or that no entry reaches and that do not decode.</summary>
    Unknown,
}

/// <summary>An entry point: where descent starts, and a function.</summary>
internal readonly record struct M68kEntry(int Offset, string? Name, CodeFunctionSource Source);

/// <summary>A run of data in the code.</summary>
internal readonly record struct M68kDataRegion(int Offset, int Length, M68kDataKind Kind, string? Note = null)
{
    public int End => Offset + Length;
}

/// <summary>
/// Which bytes of a piece of 68k code are instructions and which are data. Recursive descent from the entry points
/// and the functions MacsBug names bound: each path follows fall-through, branches and calls (PC-relative and relative
/// ones; absolute addresses are not this code's), switch tables (<c>move.w d(pc,Dn.w),Dn; jmp d(pc,Dn.w)</c>) and runs
/// of branches after an indexed <c>jmp</c>, and stops at a return, an unconditional jump, a word that does not decode,
/// <c>_ExitToShell</c>, an auto-pop trap, or <c>$AAFE</c> (a routine descriptor: data from there on). Then the gaps
/// no path reached are swept linearly; what does not decode there is data.
/// </summary>
internal sealed class M68kCodeMap
{
    private const byte Free = 0, DataByte = 1, InstructionStart = 2, InstructionByte = 3;
    private const ushort MixedModeMagic = 0xAAFE;
    private const ushort ExitToShell = 0xA9F4;
    private const int MaxTableEntries = 1024;
    private const int MaxBranchRun = 256;

    private readonly ReadOnlyMemory<byte> code;
    private readonly BigEndianReader reader;
    private readonly int start;
    private readonly byte[] kinds;
    private readonly List<M68kDataRegion> regions = [];
    private readonly Dictionary<int, (string? Name, CodeFunctionSource Source)> registered = [];
    private readonly Dictionary<int, string> macsBugAt = [];
    private readonly Stack<int> work = new();

    private M68kCodeMap(ReadOnlyMemory<byte> code, int start)
    {
        this.code = code;
        reader = new BigEndianReader(code);
        this.start = Math.Clamp(start, 0, code.Length);
        kinds = new byte[code.Length];
    }

    /// <summary>The code.</summary>
    public ReadOnlyMemory<byte> Code => code;

    /// <summary>Where the code starts; the bytes before it are a header.</summary>
    public int Start => start;

    /// <summary>The instructions, by offset.</summary>
    public SortedDictionary<int, M68kInstruction> Instructions { get; } = [];

    /// <summary>The offsets of the instructions the gap sweep found (no entry reaches them).</summary>
    public HashSet<int> Swept { get; } = [];

    /// <summary>The data runs, in offset order.</summary>
    public List<M68kDataRegion> Data { get; } = [];

    /// <summary>The functions, by offset.</summary>
    public SortedDictionary<int, CodeFunction> Functions { get; } = [];

    /// <summary>The targets of branches, calls and switch tables: where basic blocks start.</summary>
    public HashSet<int> BranchTargets { get; } = [];

    /// <summary>The MacsBug names in the code (those outside the given data).</summary>
    public IReadOnlyList<MacsBugName> MacsBugNames { get; private set; } = [];

    /// <summary>The label of the function that starts at <paramref name="offset"/>, or null.</summary>
    public string? NameAt(int offset) => Functions.TryGetValue(offset, out var f) ? f.Name : null;

    /// <summary>Maps <paramref name="code"/>.</summary>
    /// <param name="code">The code: a whole resource, offsets counting from its start.</param>
    /// <param name="start">Where the code starts; the bytes before it are a header.</param>
    /// <param name="entries">The entry points.</param>
    /// <param name="data">Runs known to be data (headers, relocation lists, initializer data).</param>
    public static M68kCodeMap Build(ReadOnlyMemory<byte> code, int start, IEnumerable<M68kEntry> entries,
        IEnumerable<M68kDataRegion> data)
    {
        var map = new M68kCodeMap(code, start);
        map.Run(entries, data);
        return map;
    }

    private void Run(IEnumerable<M68kEntry> entries, IEnumerable<M68kDataRegion> data)
    {
        foreach (var region in data)
        {
            int from = Math.Clamp(region.Offset, 0, kinds.Length);
            int to = (int)Math.Clamp((long)region.Offset + region.Length, from, kinds.Length);
            if (to > from && IsFree(from, to - from))
                MarkData(new M68kDataRegion(from, to - from, region.Kind, region.Note));
        }
        MarkFreeAsData(0, start, M68kDataKind.Header);
        foreach (var entry in entries)
            Register(entry.Offset, entry.Name, entry.Source);
        FindMacsBugNames();

        foreach (int offset in registered.Keys.Order().Reverse())
            work.Push(offset);
        Descend();
        SweepGaps();
        BuildData();
        foreach (var (offset, (name, source)) in registered)
        {
            if (!Instructions.ContainsKey(offset) && !macsBugAt.ContainsKey(offset))
                continue;
            string label = macsBugAt.TryGetValue(offset, out var m) ? m
                : name ?? "sub_" + offset.ToString("X4", CultureInfo.InvariantCulture);
            Functions[offset] = new CodeFunction(0, (uint)offset, label, source);
        }
    }

    // The function bounded by each name runs from the end of the previous name (or the start of the code).
    private void FindMacsBugNames()
    {
        var accepted = new List<MacsBugName>();
        int functionStart = start;
        foreach (var name in Disassembly.MacsBugNames.Find(code))
        {
            int end = Math.Min(name.End, kinds.Length);
            if (name.ReturnOffset < functionStart || !IsFree(name.ReturnOffset, end - name.ReturnOffset))
                continue;
            accepted.Add(name);
            macsBugAt[functionStart] = name.Name;
            Register(functionStart, null, CodeFunctionSource.MacsBug);
            MarkData(new M68kDataRegion(name.Offset, name.Length, M68kDataKind.MacsBugName, name.Name));
            if (end > name.Offset + name.Length)
                MarkData(new M68kDataRegion(name.Offset + name.Length, end - name.Offset - name.Length, M68kDataKind.Literals));
            functionStart = end;
        }
        MacsBugNames = accepted;
    }

    private void Register(int offset, string? name, CodeFunctionSource source)
    {
        if ((offset & 1) != 0 || offset < start || offset >= kinds.Length)
            return;
        if (registered.TryGetValue(offset, out var known))
        {
            if (known.Name is null && name is not null)
                registered[offset] = (name, known.Source);
            return;
        }
        registered[offset] = (name, source);
    }

    private void Descend()
    {
        while (work.Count > 0)
        {
            int p = work.Pop();
            while (CanStart(p))
            {
                if (IsMixedModeMagic(p))
                    break;
                var ins = Decode(p);
                if (ins.IsInvalid || !IsFree(p, ins.Length))
                    break;
                Record(ins, swept: false);
                if (FollowsReferences(ins))
                    foreach (var reference in ins.References)
                    {
                        if (reference.Kind == M68kReferenceKind.Data || reference.Address >= (uint)kinds.Length)
                            continue;
                        int target = (int)reference.Address;
                        BranchTargets.Add(target);
                        if (reference.Kind == M68kReferenceKind.Call)
                            Register(target, null, CodeFunctionSource.Call);
                        work.Push(target);
                    }
                if ((ins.Flags & M68kFlags.Return) != 0 || EndsFlow(ins))
                    break;
                if ((ins.Flags & (M68kFlags.Branch | M68kFlags.Conditional | M68kFlags.Call)) == M68kFlags.Branch)
                {
                    if (ins is { Mnemonic: "jmp", Operands: [M68kEffectiveAddress { Mode: M68kAddressingMode.PcIndexed } ea] })
                        IndexedJump(ins, ea);
                    break;
                }
                p += ins.Length;
            }
        }
    }

    // Branch targets and PC-relative jumps and calls are in this code; an absolute address is not.
    private static bool FollowsReferences(M68kInstruction ins)
    {
        foreach (var operand in ins.Operands)
            if (operand is M68kEffectiveAddress { Mode: M68kAddressingMode.AbsoluteShort or M68kAddressingMode.AbsoluteLong })
                return false;
        return true;
    }

    // _ExitToShell does not return, nor does an auto-pop Toolbox trap (it returns to the caller's caller).
    private static bool EndsFlow(M68kInstruction ins) =>
        ins.TrapWord is ushort trap && (trap == ExitToShell || (trap & 0x0C00) == 0x0C00);

    // jmp d(pc,Dn.w): after move.w d(pc,Dn.w),Dn it reads a table of word offsets from the jump's base [Verified: MPW
    // and CodeWarrior switch statements, the table at move.w's base and the offsets from jmp's]; otherwise the code
    // after it is a run of branches, each a case.
    private void IndexedJump(M68kInstruction jmp, M68kEffectiveAddress ea)
    {
        int at = (int)jmp.Address;
        int jumpBase = at + 2 + ea.BaseDisplacement;
        if (ea.Index is { } index && Instructions.TryGetValue(at - 4, out var move)
            && move is { Mnemonic: "move", Size: M68kSize.Word, Length: 4 }
            && move.Operands is [M68kEffectiveAddress { Mode: M68kAddressingMode.PcIndexed } source, M68kRegisterOperand destination]
            && source.Index is { } moveIndex && moveIndex.Register == index.Register && destination == index.Register)
        {
            SwitchTable(at - 4 + 2 + source.BaseDisplacement, jumpBase, Bound(move, index.Register));
            return;
        }
        int q = at + jmp.Length;
        for (int i = 0; i < MaxBranchRun && CanStart(q); i++)
        {
            work.Push(q);
            var next = Decode(q);
            if (next.Mnemonic != "bra")
                break;
            q += next.Length;
        }
    }

    // cmpi.w #n,Dn (or cmp) in the 4 instructions before the table's move: n + 1 cases.
    private int? Bound(M68kInstruction move, M68kRegisterOperand register)
    {
        var ins = move;
        for (int i = 0; i < 4 && Previous(ins) is { } previous; i++)
        {
            ins = previous;
            if (ins is { Mnemonic: "cmpi" or "cmp", Size: M68kSize.Word or M68kSize.Long, Operands: [M68kImmediate imm, M68kRegisterOperand r] }
                && r == register)
                return imm.Value is >= 0 and < MaxTableEntries ? (int)imm.Value + 1 : null;
        }
        return null;
    }

    // The instruction that ends where ins starts.
    private M68kInstruction? Previous(M68kInstruction ins)
    {
        for (int back = 2; back <= 22 && back <= ins.Address; back += 2)
            if (Instructions.TryGetValue((int)ins.Address - back, out var p))
                return p.Address + (uint)p.Length == ins.Address ? p : null;
        return null;
    }

    private void SwitchTable(int table, int jumpBase, int? count)
    {
        int lowest = int.MaxValue;
        for (int i = 0; i < (count ?? MaxTableEntries); i++)
        {
            int pos = table + 2 * i;
            if (pos < start || pos > kinds.Length - 2 || !IsFree(pos, 2) || (count is null && pos >= lowest))
                break;
            int target = jumpBase + reader.ReadInt16At(pos);
            if ((target & 1) != 0 || target < start || target >= kinds.Length)
                break;
            if (target > table)
                lowest = Math.Min(lowest, target);
            MarkData(new M68kDataRegion(pos, 2, M68kDataKind.SwitchTable));
            BranchTargets.Add(target);
            work.Push(target);
        }
    }

    private void SweepGaps()
    {
        for (int g = start; g < kinds.Length;)
        {
            if (kinds[g] != Free)
            {
                g++;
                continue;
            }
            int e = g;
            while (e < kinds.Length && kinds[e] == Free)
                e++;
            SweepGap(g, e);
            g = e;
        }
    }

    private void SweepGap(int g, int e)
    {
        bool first = true;
        for (int p = g; p < e; first = false)
        {
            if ((p & 1) != 0)
            {
                MarkData(new M68kDataRegion(p, 1, M68kDataKind.Unknown));
                p++;
                continue;
            }
            if (IsMixedModeMagic(p))
            {
                MarkData(new M68kDataRegion(p, e - p, M68kDataKind.RoutineDescriptor));
                return;
            }
            var ins = Decode(p);
            if (!ins.IsInvalid && ins.Length <= e - p)
            {
                Record(ins, swept: true);
                if (first)
                    Register(p, null, CodeFunctionSource.Gap);
                p += ins.Length;
                continue;
            }
            int n = Math.Min(2, e - p);
            MarkData(new M68kDataRegion(p, n, M68kDataKind.Unknown));
            p += n;
        }
    }

    // Sorted, with neighbouring runs of the same kind (and no note) merged.
    private void BuildData()
    {
        foreach (var region in regions.OrderBy(r => r.Offset))
        {
            if (Data.Count > 0 && Data[^1] is var last && last.End == region.Offset && last.Kind == region.Kind
                && last.Note is null && region.Note is null && region.Kind != M68kDataKind.Header)
                Data[^1] = last with { Length = last.Length + region.Length };
            else
                Data.Add(region);
        }
    }

    private bool IsMixedModeMagic(int p) => p <= kinds.Length - 2 && reader.ReadUInt16At(p) == MixedModeMagic;

    private M68kInstruction Decode(int p) => M68kDisassembler.Decode(code, p, 0, TrapNames.Describe);

    private bool CanStart(int p) => (p & 1) == 0 && p >= start && p < kinds.Length && kinds[p] == Free;

    private bool IsFree(int p, int length)
    {
        if (p < 0 || length > kinds.Length - p)
            return false;
        for (int i = p; i < p + length; i++)
            if (kinds[i] != Free)
                return false;
        return true;
    }

    private void Record(M68kInstruction ins, bool swept)
    {
        int p = (int)ins.Address;
        Instructions[p] = ins;
        if (swept)
            Swept.Add(p);
        kinds[p] = InstructionStart;
        for (int i = p + 1; i < p + ins.Length; i++)
            kinds[i] = InstructionByte;
    }

    private void MarkData(M68kDataRegion region)
    {
        for (int i = region.Offset; i < region.End; i++)
            kinds[i] = DataByte;
        regions.Add(region);
    }

    private void MarkFreeAsData(int from, int to, M68kDataKind kind)
    {
        for (int p = from; p < to;)
        {
            if (kinds[p] != Free)
            {
                p++;
                continue;
            }
            int e = p;
            while (e < to && kinds[e] == Free)
                e++;
            MarkData(new M68kDataRegion(p, e - p, kind));
            p = e;
        }
    }
}
