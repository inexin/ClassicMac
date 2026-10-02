using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>A transition vector in a data section: a code address and a TOC base, each relocated by a section.</summary>
/// <param name="Section">The data section it is in.</param>
/// <param name="Offset">Its offset there.</param>
/// <param name="CodeSection">The code section its first word points into.</param>
/// <param name="CodeOffset">The code offset (the word before relocation).</param>
/// <param name="TocSection">The section its second word points into.</param>
/// <param name="TocOffset">The TOC base's offset (the word before relocation).</param>
/// <param name="Label">The export, or main/init/term, that names it; null when none does.</param>
internal sealed record PpcTransitionVectorEntry(int Section, uint Offset, int CodeSection, uint CodeOffset, int TocSection,
    uint TocOffset, string? Label);

/// <summary>
/// What a fragment's code is: its functions (exports, traceback names, main/init/term, glue stubs, call targets), its
/// TOC base (the base most transition vectors give; CodeWarrior centres it, so it need not be the section start
/// [Verified: Disk Copy 6.5]), its glue stubs and the imports they reach, and its transition vectors
/// [Doc: Mac OS Runtime Architectures, ch. 1 and 8].
/// </summary>
internal sealed class PpcFragmentMap
{
    // lwz r12,N(r2) | stw r2,20(r1) | lwz r0,0(r12) | lwz r2,4(r12) | mtctr r0 | bctr: the cross-TOC glue the linkers put
    // in front of an imported routine [Doc: Mac OS Runtime Architectures, ch. 3, "Cross-TOC glue"].
    private const uint GlueFirstMask = 0xFFFF0000, GlueFirst = 0x81820000;
    private static readonly uint[] GlueRest = [0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420];

    private readonly Dictionary<(int Section, long Offset), PefFixup> fixupsAt = [];
    private readonly Dictionary<(int Section, uint Offset), List<(string? Name, CodeFunctionSource Source)>> candidates = [];
    private ICollection<Diagnostic> diagnostics = null!;

    private PpcFragmentMap() { }

    /// <summary>The fragment.</summary>
    public PefContainer Pef { get; private init; } = null!;

    /// <summary>The code sections' indices.</summary>
    public IReadOnlyList<int> CodeSections { get; private init; } = [];

    /// <summary>The section the TOC is in; null when no transition vector gives one.</summary>
    public int? TocSection { get; private set; }

    /// <summary>The TOC base's offset in <see cref="TocSection"/>.</summary>
    public uint TocBase { get; private set; }

    /// <summary>The functions, by section and offset.</summary>
    public SortedDictionary<(int Section, uint Offset), CodeFunction> Functions { get; } = [];

    /// <summary>The glue stubs, by section and offset: the import each calls (<c>lib::name</c>), or <c>?slot s:0xN</c>
    /// when no import relocation fills its TOC slot.</summary>
    public Dictionary<(int Section, uint Offset), string> Glue { get; } = [];

    /// <summary>The transition vectors in the data sections, in section and offset order.</summary>
    public List<PpcTransitionVectorEntry> TransitionVectors { get; } = [];

    /// <summary>Each code section's traceback tables.</summary>
    public Dictionary<int, IReadOnlyList<TracebackTable>> Tracebacks { get; } = [];

    /// <summary>A section's image (before relocation).</summary>
    public ReadOnlyMemory<byte> Image(int section) => Pef.GetImage(section, diagnostics);

    /// <summary>Maps a fragment.</summary>
    public static PpcFragmentMap Build(PefContainer pef, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(pef);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var map = new PpcFragmentMap
        {
            Pef = pef,
            CodeSections = pef.Sections.Where(s => s.Kind == PefSectionKind.Code).Select(s => s.Index).ToList(),
            diagnostics = diagnostics,
        };
        map.Run();
        return map;
    }

    /// <summary>An import as <c>lib::name</c>.</summary>
    public string ImportName(int index)
    {
        if (Pef.Loader is not { } loader || (uint)index >= (uint)loader.ImportedSymbols.Count)
        {
            return "?import " + index.ToString(CultureInfo.InvariantCulture);
        }

        var symbol = loader.ImportedSymbols[index];
        return (uint)symbol.LibraryIndex < (uint)loader.ImportedLibraries.Count
            ? loader.ImportedLibraries[symbol.LibraryIndex].Name + "::" + symbol.Name
            : symbol.Name;
    }

    /// <summary>The relocation of the word at a section offset, or null.</summary>
    public PefFixup? FixupAt(int section, long offset) => fixupsAt.TryGetValue((section, offset), out var f) ? f : null;

    /// <summary>What the TOC slot at <paramref name="slot"/> holds: an import (<c>lib::name</c>) or a section address
    /// (<c>s:0xN</c> and its label); null when no relocation fills it.</summary>
    public string? SlotText(int section, long slot)
    {
        if (FixupAt(section, slot) is not { } fixup)
        {
            return null;
        }

        if (fixup.Target == PefFixupTarget.Import)
        {
            return ImportName(fixup.TargetIndex);
        }

        var image = Image(section);
        if (slot < 0 || slot > image.Length - 4L)
        {
            return null;
        }

        uint value = new BigEndianReader(image).ReadUInt32At((int)slot);
        string text = $"{fixup.TargetIndex}:0x{value:X}";
        string? label = Functions.TryGetValue((fixup.TargetIndex, value), out var f) ? f.Name
            : TransitionVectors.Find(t => t.Section == fixup.TargetIndex && t.Offset == value)?.Label;
        return label is null ? text : text + " " + label;
    }

    private void Run()
    {
        foreach (var fixup in Pef.GetFixups(diagnostics))
        {
            fixupsAt[(fixup.Section, fixup.Offset)] = fixup;
        }

        FindTransitionVectors();
        var entryLabels = EntryLabels();
        foreach (int section in CodeSections)
        {
            FindFunctions(section);
        }

        foreach (var (section, offset, label, source, _, _) in entryLabels)
        {
            Candidate(section, offset, label, source);
        }

        foreach (var t in TransitionVectors.ToList())
        {
            string? label = entryLabels.Find(e => e.VectorSection == t.Section && e.VectorOffset == t.Offset).Label;
            if (label is not null)
            {
                TransitionVectors[TransitionVectors.IndexOf(t)] = t with { Label = label };
            }
        }
        foreach (int section in CodeSections)
        {
            if (!candidates.ContainsKey((section, 0)) && Image(section).Length > 0)
            {
                Candidate(section, 0, null, CodeFunctionSource.Entry);
            }
        }

        foreach (var (key, list) in candidates)
        {
            var best = list.MinBy(c => Priority(c.Source));
            string name = list.OrderBy(c => Priority(c.Source)).Select(c => c.Name).FirstOrDefault(n => n is not null)
                ?? "sub_" + key.Offset.ToString("X4", CultureInfo.InvariantCulture);
            Functions[key] = new CodeFunction(key.Section, key.Offset, name, best.Source);
        }
    }

    private static int Priority(CodeFunctionSource source) => source switch
    {
        CodeFunctionSource.Export => 0,
        CodeFunctionSource.Traceback => 1,
        CodeFunctionSource.Main or CodeFunctionSource.Init or CodeFunctionSource.Term => 2,
        CodeFunctionSource.Glue => 3,
        CodeFunctionSource.Call => 4,
        _ => 5,
    };

    private void Candidate(int section, uint offset, string? name, CodeFunctionSource source)
    {
        if (!CodeSections.Contains(section) || offset >= (uint)Image(section).Length)
        {
            return;
        }

        if (!candidates.TryGetValue((section, offset), out var list))
        {
            candidates[(section, offset)] = list = [];
        }

        list.Add((name, source));
    }

    // A code relocation followed by a data relocation of the next word: {code, TOC}. The TOC base is the one most give.
    private void FindTransitionVectors()
    {
        foreach (var fixup in fixupsAt.Values.OrderBy(f => f.Section).ThenBy(f => f.Offset))
        {
            if (fixup.Target != PefFixupTarget.Section || !CodeSections.Contains(fixup.TargetIndex)
                || FixupAt(fixup.Section, fixup.Offset + 4) is not { Target: PefFixupTarget.Section } toc
                || CodeSections.Contains(toc.TargetIndex))
            {
                continue;
            }

            var image = Image(fixup.Section);
            if (fixup.Offset < 0 || fixup.Offset > image.Length - 8L)
            {
                continue;
            }

            var reader = new BigEndianReader(image);
            TransitionVectors.Add(new PpcTransitionVectorEntry(fixup.Section, (uint)fixup.Offset, fixup.TargetIndex,
                reader.ReadUInt32At((int)fixup.Offset), toc.TargetIndex, reader.ReadUInt32At((int)fixup.Offset + 4), null));
        }
        var common = TransitionVectors.GroupBy(t => (t.TocSection, t.TocOffset))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        if (common is not null)
        {
            TocSection = common.Key.TocSection;
            TocBase = common.Key.TocOffset;
        }
    }

    // Exports (a transition vector's code, or a code symbol) and main/init/term, with the vector each names.
    private List<(int Section, uint Offset, string Label, CodeFunctionSource Source, int VectorSection, uint VectorOffset)> EntryLabels()
    {
        var labels = new List<(int, uint, string, CodeFunctionSource, int, uint)>();
        if (Pef.Loader is not { } loader)
        {
            return labels;
        }

        foreach (var export in loader.Exports)
        {
            if (export.SectionIndex < 0)
            {
                continue;
            }

            if (export.Class == PefSymbolClass.TVector
                && Pef.GetTransitionVector(export.SectionIndex, export.Value, diagnostics) is { CodeSection: >= 0 } tv)
            {
                labels.Add((tv.CodeSection, tv.CodeOffset, export.Name, CodeFunctionSource.Export, export.SectionIndex, export.Value));
            }
            else if (export.Class == PefSymbolClass.Code)
            {
                labels.Add((export.SectionIndex, export.Value, export.Name, CodeFunctionSource.Export, -1, 0));
            }
        }
        foreach (var (entry, name, source) in new[]
        {
            (loader.Main, "main", CodeFunctionSource.Main), (loader.Init, "init", CodeFunctionSource.Init),
            (loader.Term, "term", CodeFunctionSource.Term),
        })
        {
            if (entry is { } e && Pef.GetTransitionVector(e, diagnostics) is { CodeSection: >= 0 } tv)
            {
                labels.Add((tv.CodeSection, tv.CodeOffset, name, source, e.Section, e.Offset));
            }
        }

        return labels;
    }

    // Traceback names, glue stubs and bl targets.
    private void FindFunctions(int section)
    {
        var image = Image(section);
        var tables = TracebackTable.Find(image, diagnostics);
        Tracebacks[section] = tables;
        var inTable = new HashSet<int>();
        foreach (var table in tables)
        {
            for (int i = table.Offset; i < table.Offset + table.Length; i += 4)
            {
                inTable.Add(i & ~3);
            }

            if (table.FunctionStart is int start)
            {
                Candidate(section, (uint)start, table.Name, CodeFunctionSource.Traceback);
            }
        }
        var reader = new BigEndianReader(image);
        for (int at = 0; at <= image.Length - 4; at += 4)
        {
            if (inTable.Contains(at))
            {
                continue;
            }

            uint word = reader.ReadUInt32At(at);
            if ((word & GlueFirstMask) == GlueFirst && IsGlue(reader, at))
            {
                string import = GlueImport((short)word);
                Glue[(section, (uint)at)] = import;
                Candidate(section, (uint)at, import.StartsWith('?') ? null : "." + import[(import.IndexOf("::", StringComparison.Ordinal) + 2)..],
                    CodeFunctionSource.Glue);
            }
            if ((word & 0xFC000003) == 0x48000001)
            {
                var ins = PpcDisassembler.Decode(word, (uint)at);
                if (ins.Target is uint target)
                {
                    Candidate(section, target, null, CodeFunctionSource.Call);
                }
            }
        }
    }

    private static bool IsGlue(BigEndianReader reader, int at)
    {
        if (at > reader.Length - 24)
        {
            return false;
        }

        for (int i = 0; i < GlueRest.Length; i++)
        {
            if (reader.ReadUInt32At(at + 4 + 4 * i) != GlueRest[i])
            {
                return false;
            }
        }

        return true;
    }

    private string GlueImport(short displacement)
    {
        if (TocSection is not int toc)
        {
            return $"?slot r2+0x{displacement:X}";
        }

        long slot = TocBase + (long)displacement;
        return FixupAt(toc, slot) is { Target: PefFixupTarget.Import } fixup ? ImportName(fixup.TargetIndex) : $"?slot {toc}:0x{slot:X}";
    }
}

/// <summary>The annotations of a PowerPC instruction: calls through glue (<c>lib::name</c>), calls and jumps to
/// labelled functions, and loads from the TOC.</summary>
internal static class PpcAnnotator
{
    public static List<CodeReference> Annotate(PpcInstruction ins, int section, PpcFragmentMap map)
    {
        var refs = new List<CodeReference>();
        if (ins.Target is uint target && (ins.IsCall || !ins.IsConditional))
        {
            if (map.Glue.TryGetValue((section, target), out var import))
            {
                refs.Add(new CodeReference(section, ins.Address, CodeReferenceKind.Glue, import));
            }
            else if (map.Functions.TryGetValue((section, target), out var f))
            {
                refs.Add(new CodeReference(section, ins.Address, CodeReferenceKind.Call, f.Name));
            }
        }
        if (ins is { Mnemonic: "lwz", Operands: [_, { Kind: PpcOperandKind.Displacement, Base: 2 } d] } && map.TocSection is int toc
            && map.SlotText(toc, map.TocBase + d.Value) is { } slot)
        {
            refs.Add(new CodeReference(section, ins.Address, CodeReferenceKind.TocSlot, slot));
        }

        return refs;
    }
}
