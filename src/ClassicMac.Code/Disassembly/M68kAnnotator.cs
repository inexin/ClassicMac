using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>What a relocation adds to the long it patches.</summary>
internal enum M68kRelocationBase
{
    /// <summary>A5: the long is an offset from A5.</summary>
    A5,
    /// <summary>A segment's address: the long is an offset in that <c>'CODE'</c> resource.</summary>
    Segment,
}

/// <summary>A relocation of a long in the code.</summary>
/// <param name="Base">What is added.</param>
/// <param name="Segment">For <see cref="M68kRelocationBase.Segment"/>, the segment.</param>
internal readonly record struct M68kRelocation(M68kRelocationBase Base, short Segment);

/// <summary>What the annotator knows about the code beyond its bytes: the application, the segment, its relocations
/// and the names of functions in other segments.</summary>
internal sealed class M68kContext
{
    private static readonly ConditionalWeakTable<CodeApplication, Dictionary<short, Dictionary<long, string>>> NameCache = [];

    /// <summary>The application the code is a segment of, for the jump table.</summary>
    public CodeApplication? Application { get; init; }

    /// <summary>The segment's ID.</summary>
    public short Segment { get; init; }

    /// <summary>The relocations, by the offset of the long they patch.</summary>
    public IReadOnlyDictionary<long, M68kRelocation> Relocations { get; init; } = new Dictionary<long, M68kRelocation>();

    /// <summary>The MacsBug name of the function at a segment's resource offset, or null.</summary>
    public Func<short, long, string?> FunctionName { get; init; } = (_, _) => null;

    /// <summary>The context of a segment of <paramref name="app"/>: its relocations (far A5 and PC lists, Retro68's
    /// <c>'RELA'</c>, CodeWarrior's code lists for <c>'CODE'</c> 1) and the MacsBug names of every segment.</summary>
    public static M68kContext ForApplication(CodeApplication app, short segment)
    {
        ArgumentNullException.ThrowIfNull(app);
        var relocations = new Dictionary<long, M68kRelocation>();
        if (app.FindSegment(segment) is { } s)
        {
            foreach (long offset in s.A5Relocations)
            {
                relocations[offset] = new M68kRelocation(M68kRelocationBase.A5, 0);
            }

            foreach (long offset in s.PcRelocations)
            {
                relocations[offset] = new M68kRelocation(M68kRelocationBase.Segment, segment);
            }
            // Retro68: kind 0 adds the segment's address, kinds 1 to 3 the A5 displacement (docs/formats/code/code-data.md
            // §1.3). A relative relocation's long names the same target, as a distance once patched.
            foreach (var r in s.Retro68Relocations)
            {
                relocations[r.Offset] = r.Base == Retro68RelocationBase.Segment
                    ? new M68kRelocation(M68kRelocationBase.Segment, segment)
                    : new M68kRelocation(M68kRelocationBase.A5, 0);
            }

            if (segment == 1 && app.CodeWarriorData is { } cw)
            {
                foreach (var list in cw.Relocations)
                {
                    var relocation = list.Kind switch
                    {
                        CodeWarriorRelocationKind.CodePlusA5 => new M68kRelocation(M68kRelocationBase.A5, 0),
                        CodeWarriorRelocationKind.CodePlusCode or CodeWarriorRelocationKind.CodePlusCodeSecond =>
                            new M68kRelocation(M68kRelocationBase.Segment, 1),
                        _ => (M68kRelocation?)null,
                    };
                    if (relocation is { } r)
                    {
                        foreach (int offset in list.Offsets)
                        {
                            relocations[offset] = r;
                        }
                    }
                }
            }
        }
        var names = NameCache.GetValue(app, BuildNames);
        return new M68kContext
        {
            Application = app,
            Segment = segment,
            Relocations = relocations,
            FunctionName = (id, offset) => names.TryGetValue(id, out var n) && n.TryGetValue(offset, out var name) ? name : null,
        };
    }

    // Each segment's MacsBug-bounded functions: from the end of the previous name (or the code's start) to the next.
    private static Dictionary<short, Dictionary<long, string>> BuildNames(CodeApplication app)
    {
        var all = new Dictionary<short, Dictionary<long, string>>();
        foreach (var segment in app.Segments)
        {
            if (!segment.IsReadable)
            {
                continue;
            }

            var names = new Dictionary<long, string>();
            int functionStart = segment.Header?.Length ?? 0;
            foreach (var name in MacsBugNames.Find(segment.Data))
            {
                if (name.ReturnOffset < functionStart)
                {
                    continue;
                }

                names.TryAdd(functionStart, name.Name);
                functionStart = name.End;
            }
            all[segment.Id] = names;
        }
        return all;
    }
}

/// <summary>
/// The annotations of a 68k instruction: jump-table calls (<c>CODE s:+off name</c>), A5 globals, low-memory globals,
/// traps and their selectors, relocated operands, string previews and calls to labelled functions.
/// </summary>
internal static class M68kAnnotator
{
    private const int Lookback = 3;

    /// <summary>The annotations of <paramref name="ins"/>, in operand order then trap, selector and call.</summary>
    public static List<CodeReference> Annotate(M68kInstruction ins, M68kCodeMap map, M68kContext context)
    {
        var refs = new List<CodeReference>();
        void Add(CodeReferenceKind kind, string text) => refs.Add(new CodeReference(0, ins.Address, kind, text));
        var reader = new BigEndianReader(map.Code);
        var usedRelocations = new HashSet<long>();

        foreach (var operand in ins.Operands)
        {
            switch (operand)
            {
                case M68kEffectiveAddress { Mode: M68kAddressingMode.Displacement, Register: 5 } a5:
                    if (ins.Mnemonic is "jsr" or "jmp" or "pea" or "lea" && context.Application?.ResolveA5(a5.BaseDisplacement) is { } entry)
                    {
                        Add(CodeReferenceKind.JumpTable, JumpTable(entry, context));
                    }
                    else
                    {
                        Add(CodeReferenceKind.A5Global, Signed("A5", a5.BaseDisplacement));
                    }

                    break;
                case M68kEffectiveAddress { Mode: M68kAddressingMode.AbsoluteLong or M68kAddressingMode.AbsoluteShort, Address: uint address } abs:
                    if (abs.Mode == M68kAddressingMode.AbsoluteLong && FindRelocation(ins, address, reader, context, usedRelocations) is { } r)
                    {
                        Add(CodeReferenceKind.Relocation, Relocated(r, address, map, context));
                    }
                    else if (LowMemoryGlobals.TryFind(address, out var global, out int offset))
                    {
                        Add(CodeReferenceKind.LowMemory, offset == 0 ? global.Name : $"{global.Name}+{offset}");
                    }

                    break;
                case M68kImmediate { Size: M68kSize.Long } imm:
                    if (FindRelocation(ins, (uint)imm.Value, reader, context, usedRelocations) is { } ri)
                    {
                        Add(CodeReferenceKind.Relocation, Relocated(ri, (uint)imm.Value, map, context));
                    }

                    break;
                case M68kEffectiveAddress { Mode: M68kAddressingMode.PcDisplacement, Address: uint target }
                    when ins.Mnemonic is not ("jsr" or "jmp"):
                    if (target < (uint)map.Code.Length && M68kStrings.At(map.Code.Span, (int)target) is { } s)
                    {
                        Add(CodeReferenceKind.String, s.Preview);
                    }

                    break;
            }
        }

        if (ins.TrapWord is ushort trap)
        {
            Add(CodeReferenceKind.Trap, TrapNames.Describe(trap));
            if (SelectorNames.TryGetConvention(trap, out var convention) && FindSelector(ins, map, convention) is uint selector)
            {
                Add(CodeReferenceKind.Selector, SelectorText(trap, selector, convention));
            }
        }

        foreach (var reference in ins.References)
        {
            bool call = reference.Kind == M68kReferenceKind.Call;
            bool jump = reference.Kind == M68kReferenceKind.Branch && (ins.Flags & M68kFlags.Conditional) == 0;
            if ((call || jump) && reference.Address < (uint)map.Code.Length && map.NameAt((int)reference.Address) is { } name
                && !refs.Exists(r => r.Kind == CodeReferenceKind.Relocation))
            {
                Add(CodeReferenceKind.Call, name);
            }
        }
        return refs;
    }

    private static string JumpTable(JumpTableEntry entry, M68kContext context)
    {
        if (entry.ResourceOffset is not { } offset)
        {
            return "JT " + entry.Index.ToString(CultureInfo.InvariantCulture);
        }

        string text = $"CODE {entry.Segment}:+${offset:X}";
        return context.FunctionName(entry.Segment, offset) is { } name ? text + " " + name : text;
    }

    private static string Signed(string register, long value) =>
        value < 0 ? $"{register}-${-value:X}" : $"{register}+${value:X}";

    // The relocation whose patched long is inside the instruction and holds value.
    private static M68kRelocation? FindRelocation(M68kInstruction ins, uint value, BigEndianReader reader, M68kContext context,
        HashSet<long> used)
    {
        if (context.Relocations.Count == 0)
        {
            return null;
        }

        for (long at = ins.Address + 2L; at <= ins.Address + (long)ins.Length - 4; at += 2)
        {
            if (!used.Contains(at) && context.Relocations.TryGetValue(at, out var r) && reader.ReadUInt32At((int)at) == value)
            {
                used.Add(at);
                return r;
            }
        }

        return null;
    }

    private static string Relocated(M68kRelocation relocation, uint value, M68kCodeMap map, M68kContext context)
    {
        switch (relocation.Base)
        {
            case M68kRelocationBase.A5:
                int a5 = unchecked((int)value);
                string text = Signed("A5", a5);
                return context.Application?.ResolveA5(a5) is { } entry ? text + " " + JumpTable(entry, context) : text;
            default:
                string code = $"CODE {relocation.Segment}+${value:X}";
                string? name = relocation.Segment == context.Segment && value < (uint)map.Code.Length
                    ? map.NameAt((int)value) : null;
                name ??= context.FunctionName(relocation.Segment, value);
                return name is null ? code : code + " " + name;
        }
    }

    // Looks back up to 3 instructions in the trap's basic block for the instruction that sets the selector.
    private static uint? FindSelector(M68kInstruction trap, M68kCodeMap map, SelectorConvention convention)
    {
        if (IsBlockStart(trap, map))
        {
            return null;
        }

        var current = trap;
        for (int i = 0; i < Lookback; i++)
        {
            if (map.Previous(current) is not { } previous
                || (previous.Flags & (M68kFlags.Branch | M68kFlags.Return | M68kFlags.Call | M68kFlags.ALine)) != 0)
            {
                return null;
            }

            var (decided, value) = convention.Location == SelectorLocation.Stack ? StackSelector(previous) : D0Selector(previous);
            if (decided)
            {
                return value;
            }

            if (IsBlockStart(previous, map))
            {
                return null;
            }

            current = previous;
        }
        return null;
    }

    private static bool IsBlockStart(M68kInstruction ins, M68kCodeMap map) =>
        map.BranchTargets.Contains((int)ins.Address) || map.Functions.ContainsKey((int)ins.Address);

    // move.w/move.l #sel,-(sp) or clr.w/clr.l -(sp); any other push ends the search.
    private static (bool, uint?) StackSelector(M68kInstruction ins)
    {
        if (ins.Operands.Count == 0 || ins.Operands[^1] is not M68kEffectiveAddress { Mode: M68kAddressingMode.PreDecrement, Register: 7 })
        {
            return (false, null);
        }

        return ins switch
        {
            { Mnemonic: "move", Size: M68kSize.Word, Operands: [M68kImmediate imm, _] } => (true, (uint)(ushort)imm.Value),
            { Mnemonic: "move", Size: M68kSize.Long, Operands: [M68kImmediate imm, _] } => (true, (uint)imm.Value),
            { Mnemonic: "clr" } => (true, 0u),
            _ => (true, null),
        };
    }

    // moveq #sel,d0 or move.w/move.l #sel,d0; any other write to D0 ends the search.
    private static (bool, uint?) D0Selector(M68kInstruction ins)
    {
        if (ins.Operands.Count == 0 || ins.Operands[^1] is not M68kRegisterOperand { Kind: M68kRegisterKind.Data, Number: 0 })
        {
            return (false, null);
        }

        return ins switch
        {
            { Mnemonic: "moveq", Operands: [M68kImmediate imm, _] } => (true, unchecked((uint)(sbyte)(byte)imm.Value)),
            { Mnemonic: "move", Size: M68kSize.Word, Operands: [M68kImmediate imm, _] } => (true, (uint)(ushort)imm.Value),
            { Mnemonic: "move", Size: M68kSize.Long, Operands: [M68kImmediate imm, _] } => (true, (uint)imm.Value),
            { Mnemonic: "cmp" or "cmpi" or "tst" or "btst" } => (false, null),
            _ => (true, null),
        };
    }

    private static string SelectorText(ushort trap, uint selector, SelectorConvention convention)
    {
        bool named = SelectorNames.TryGet(trap, selector, out var name);
        if (convention.Width == SelectorWidth.OSType)
        {
            string code = FourCCText(selector);
            return named ? code + " " + name : code;
        }
        return named ? name : $"selector ${selector:X}";
    }

    private static string FourCCText(uint value)
    {
        Span<byte> b = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        return M68kStrings.IsPrintable(b) ? "'" + Encoding.ASCII.GetString(b) + "'" : $"${value:X8}";
    }
}

/// <summary>Strings at the targets of PC-relative operands.</summary>
internal static class M68kStrings
{
    private const int MaxPreview = 40;

    // Shorter runs ending in NUL are too often code: link a6,#0 reads "NV ".
    private const int MinCString = 4;

    /// <summary>
    /// A Pascal string (a length byte, then that many printable characters) or a C string (at least 4 printable
    /// characters, then NUL) at <paramref name="at"/>: its preview (<c>P'text'</c>, <c>C'text'</c>, cut after 40
    /// characters) and the bytes it takes (the length byte or the NUL included); null when there is neither.
    /// </summary>
    public static (string Preview, int Length)? At(ReadOnlySpan<byte> code, int at)
    {
        if (at < 0 || at >= code.Length)
        {
            return null;
        }

        int n = code[at];
        if (n >= 1 && n <= code.Length - at - 1 && IsPrintable(code.Slice(at + 1, n)))
        {
            return ("P'" + Preview(code.Slice(at + 1, n)) + "'", n + 1);
        }

        int end = at;
        while (end < code.Length && end - at <= 255 && code[end] is >= 0x20 and <= 0x7E)
        {
            end++;
        }

        if (end - at >= MinCString && end < code.Length && code[end] == 0)
        {
            return ("C'" + Preview(code[at..end]) + "'", end - at + 1);
        }

        return null;
    }

    /// <summary>Whether every byte is printable ASCII.</summary>
    public static bool IsPrintable(ReadOnlySpan<byte> chars)
    {
        foreach (byte c in chars)
        {
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static string Preview(ReadOnlySpan<byte> chars) =>
        chars.Length > MaxPreview ? Encoding.ASCII.GetString(chars[..MaxPreview]) + "..." : Encoding.ASCII.GetString(chars);
}
