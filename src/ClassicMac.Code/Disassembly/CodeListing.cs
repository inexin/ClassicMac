using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Disassembly;

/// <summary>
/// An annotated listing of classic Mac code (the <c>.s</c> text) and its structured model: the functions it labels and
/// the references it annotates. The text is deterministic: a header of <c>;</c> lines (what the code is, the build
/// model, the entry points), then each function's label line and its lines, <c>address  hex  instruction  ; notes</c>,
/// with data as <c>dc.b</c>/<c>dc.w</c>/<c>dc.l</c> and an ASCII preview. 68k addresses are offsets in the resource;
/// PowerPC addresses are <c>section:offset</c>.
/// </summary>
public sealed class CodeListing
{
    private static readonly FourCC CodeType = FourCC.FromString("CODE");
    private static readonly FourCC DriverType = FourCC.FromString("DRVR");
    private static readonly FourCC PowerPC = FourCC.FromString("pwpc");
    private static readonly FourCC Cfm68k = FourCC.FromString("m68k");

    private CodeListing(string text, IReadOnlyList<CodeFunction> functions, IReadOnlyList<CodeReference> references,
        IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<CodeListing> fragments)
    {
        Text = text;
        Functions = functions;
        References = references;
        Diagnostics = diagnostics;
        Fragments = fragments;
    }

    /// <summary>The listing text, lines ending in <c>\n</c>; embedded fragments' listings follow the code's.</summary>
    public string Text { get; }

    /// <summary>The functions the listing labels, in address order.</summary>
    public IReadOnlyList<CodeFunction> Functions { get; }

    /// <summary>Every annotation, in address order (traps included, which the text leaves to the mnemonic).</summary>
    public IReadOnlyList<CodeReference> References { get; }

    /// <summary>Problems found while reading what the listing needed.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>The listings of PEF containers embedded in a code resource (behind a routine descriptor).</summary>
    public IReadOnlyList<CodeListing> Fragments { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;

    /// <summary>
    /// Lists a segment (<c>'CODE'</c> 1 and up) of a 68k application: recursive descent from its jump-table entries,
    /// the entry point and its code start, with jump-table calls, globals, traps, selectors and relocations annotated.
    /// </summary>
    /// <exception cref="ArgumentException">The application has no segment <paramref name="segmentId"/>.</exception>
    public static CodeListing ForSegment(CodeApplication application, int segmentId)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (segmentId is < short.MinValue or > short.MaxValue || application.FindSegment((short)segmentId) is not { } segment)
            throw new ArgumentException($"The application has no 'CODE' {segmentId}.", nameof(segmentId));
        short id = segment.Id;
        var w = new ListingWriter(24);
        string title = $"'CODE' {id}" + (segment.Name is { } n ? $" \"{n}\"" : "") + ": 68k segment, "
            + (!segment.IsReadable ? "compressed and not readable"
                : segment.Header is null ? "too short for its header"
                : segment.Header.IsFar ? "far header" : "near header");
        w.Header(title);
        w.Header("Model: " + ModelText(application.Model));
        if (application.Entry is { } entry)
            w.Header($"Entry: CODE {entry.Segment}:+${entry.ResourceOffset:X}");
        if (application.OriginalEntry is { } original)
            w.Header($"Original entry: CODE {original.Segment}:+${original.ResourceOffset:X}");
        if (application.HasPowerPCFragment)
            w.Header("Also PowerPC code: 'cfrg' 0");
        var own = application.JumpTable.Where(e => e.ResourceOffset is not null && e.Segment == id).ToList();
        if (own.Count > 0)
            w.Header($"Jump-table entries: {own.Count}");
        var context = M68kContext.ForApplication(application, id);
        if (context.Relocations.Count > 0)
            w.Header($"Relocations: {context.Relocations.Count}");
        w.EndHeader();
        if (!segment.IsReadable)
            return w.Finish([], [], [], []);

        var data = segment.Data;
        int start = segment.Header?.Length ?? data.Length;
        var entries = new List<M68kEntry>();
        if (application.Entry is { } e0 && e0.Segment == id)
            entries.Add(new M68kEntry(Offset(e0.ResourceOffset), "entry", CodeFunctionSource.Entry));
        if (application.OriginalEntry is { } e1 && e1.Segment == id)
            entries.Add(new M68kEntry(Offset(e1.ResourceOffset), "original_entry", CodeFunctionSource.Entry));
        foreach (var jt in own)
            entries.Add(new M68kEntry(Offset(jt.ResourceOffset!.Value), "JT" + jt.Index.ToString(CultureInfo.InvariantCulture),
                CodeFunctionSource.JumpTable));
        entries.Add(new M68kEntry(start, null, CodeFunctionSource.Entry));

        var regions = new List<M68kDataRegion> { new(0, start, M68kDataKind.Header, "segment header") };
        if (segment.Header is { IsFar: true } far)
            regions.AddRange(FarLists(data, far));
        if (application.A5InitSegment == id && application.A5Init is { } init && init.HeaderOffset < data.Length)
            regions.Add(new M68kDataRegion(init.HeaderOffset, data.Length - init.HeaderOffset, M68kDataKind.Initializer));
        var selfRelocated = context.Relocations.Where(r => r.Value.Base == M68kRelocationBase.Segment && r.Value.Segment == id)
            .Select(r => (int)r.Key).ToHashSet();
        var map = M68kCodeMap.Build(data, start, entries, regions, selfRelocated);
        var references = WriteBody(w, map, context);
        return w.Finish(map.Functions.Values.ToList(), references, [], []);
    }

    /// <summary>
    /// Lists a code resource: a <c>'CODE'</c> segment (with <paramref name="fork"/>'s <c>'CODE'</c> 0 for the jump
    /// table, when given), <c>'CODE'</c> 0 (the jump table, as data), a PEF container (a native code resource), or 68k
    /// code entered at its start: behind a standard header, a driver header (its routines), a package's <c>$A9FF</c>
    /// table (its routines), or raw. A routine descriptor's PowerPC fragments are listed after the 68k part.
    /// </summary>
    public static CodeListing ForCodeResource(FourCC type, short id, ReadOnlyMemory<byte> data, ResourceFork? fork = null)
    {
        var diagnostics = new List<Diagnostic>();
        string title = $"'{type}' {id}";
        if (fork?.Find(type, id)?.Name is { } resourceName)
            title += $" \"{resourceName.ToMacRoman()}\"";

        if (type == CodeType && id == 0)
        {
            var w0 = new ListingWriter(24);
            w0.Header(title + ": the jump table");
            w0.EndHeader();
            var map0 = M68kCodeMap.Build(data, data.Length, [], [new M68kDataRegion(0, data.Length, M68kDataKind.Header, "jump table")]);
            WriteBody(w0, map0, new M68kContext());
            return w0.Finish([], [], diagnostics, []);
        }
        if (type == CodeType && fork?.Find(CodeType, 0) is not null)
        {
            try
            {
                var app = CodeApplication.Read(fork, diagnostics);
                if (app.FindSegment(id) is not null)
                {
                    var listing = ForSegment(app, id);
                    return new CodeListing(listing.Text, listing.Functions, listing.References, diagnostics, []);
                }
            }
            catch (InvalidDataException e)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.listing-application",
                    $"The fork's 'CODE' 0 could not be read ({e.Message}); 'CODE' {id} is listed on its own."));
            }
        }
        if (PefContainer.IsPef(data.Span))
        {
            var fragment = ForFragment(PefContainer.Read(data, diagnostics), title, diagnostics);
            return fragment;
        }
        return type == CodeType ? LoneSegment(title, data, diagnostics) : CodeResource(type, title, data, diagnostics);
    }

    /// <summary>
    /// Lists a PowerPC fragment: each code section from its start, functions labelled by export, traceback table,
    /// main/init/term, glue stub and call; calls through glue named <c>lib::name</c>; TOC loads named by their slot;
    /// traceback tables and transition vectors as data. A CFM-68K container gets the header only.
    /// </summary>
    /// <param name="fragment">The container.</param>
    /// <param name="name">A name for the header (the <c>'cfrg'</c> member's), or null.</param>
    public static CodeListing ForFragment(PefContainer fragment, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return ForFragment(fragment, name is null ? null : $"\"{name}\"", []);
    }

    private static CodeListing ForFragment(PefContainer pef, string? title, List<Diagnostic> diagnostics)
    {
        var w = new ListingWriter(0);
        var arch = pef.Architecture;
        string kind = arch == PowerPC ? "PowerPC fragment" : arch == Cfm68k ? "CFM-68K fragment" : "fragment";
        int count = pef.Sections.Count;
        w.Header((title is null ? "" : title + ": ") + $"{kind} ('{arch}'), {count} section{(count == 1 ? "" : "s")}");
        foreach (var section in pef.Sections)
            w.Header($"Section {section.Index}: {section.Kind}, 0x{(section.IsInstantiable ? section.TotalLength : section.ContainerLength):X} bytes"
                + (section.Name is { } sn ? $" \"{sn}\"" : ""));
        if (arch != PowerPC)
        {
            w.Header("Not PowerPC code: not disassembled");
            w.EndHeader();
            return w.Finish([], [], diagnostics, []);
        }

        var map = PpcFragmentMap.Build(pef, diagnostics);
        if (pef.Loader is { } loader)
        {
            foreach (var (label, point) in new[] { ("Main", loader.Main), ("Init", loader.Init), ("Term", loader.Term) })
            {
                if (point is not { } p)
                    continue;
                string text = $"{label}: {p}";
                if (pef.GetTransitionVector(p, diagnostics) is { CodeSection: >= 0 } tv)
                    text += $" -> {tv.CodeSection}:0x{tv.CodeOffset:X}";
                w.Header(text);
            }
        }
        if (map.TocSection is int toc)
            w.Header($"TOC base: {toc}:0x{map.TocBase:X}");
        if (pef.Loader is { } l && (l.ImportedSymbols.Count > 0 || l.ImportedLibraries.Count > 0))
            w.Header($"Imports: {l.ImportedSymbols.Count} from {l.ImportedLibraries.Count} librar{(l.ImportedLibraries.Count == 1 ? "y" : "ies")}");
        if (pef.Loader is { Exports.Count: > 0 } x)
            w.Header($"Exports: {x.Exports.Count}");
        w.EndHeader();

        var references = new List<CodeReference>();
        foreach (int section in map.CodeSections)
        {
            var image = map.Image(section);
            var reader = new BigEndianReader(image);
            var tables = map.Tracebacks[section].ToDictionary(t => t.Offset);
            for (int at = 0; at < image.Length;)
            {
                if (map.Functions.TryGetValue((section, (uint)at), out var function))
                    w.Label(function.Name);
                if (tables.TryGetValue(at, out var table))
                {
                    int end = (int)Math.Min(image.Length, (table.Offset + (long)table.Length + 3) & ~3L);
                    w.PpcData(section, image.Span[at..end], at, "traceback table" + (table.Name is { } tn ? " " + tn : ""));
                    at = end;
                    continue;
                }
                if (at > image.Length - 4)
                {
                    w.PpcData(section, image.Span[at..], at, null);
                    break;
                }
                var ins = PpcDisassembler.Decode(reader.ReadUInt32At(at), (uint)at);
                var refs = PpcAnnotator.Annotate(ins, section, map);
                references.AddRange(refs);
                w.Line($"{section}:{at:X8}", ins.Word.ToString("X8", CultureInfo.InvariantCulture), ins.Text,
                    refs.Count == 0 ? null : string.Join("; ", refs.Select(r => r.Text)));
                at += 4;
            }
        }
        if (map.TransitionVectors.Count > 0)
        {
            w.Blank();
            w.Comment("Transition vectors");
            foreach (var t in map.TransitionVectors)
                w.Line($"{t.Section}:{t.Offset:X8}", $"{t.CodeOffset:X8} {t.TocOffset:X8}", $"dc.l ${t.CodeOffset:X8},${t.TocOffset:X8}",
                    (t.Label is null ? "" : t.Label + ": ") + $"code {t.CodeSection}:0x{t.CodeOffset:X}, TOC {t.TocSection}:0x{t.TocOffset:X}");
        }
        return w.Finish(map.Functions.Values.ToList(), references, diagnostics, []);
    }

    // A 'CODE' resource with no application: its header, any far relocation lists, entered at its code start.
    private static CodeListing LoneSegment(string title, ReadOnlyMemory<byte> data, List<Diagnostic> diagnostics)
    {
        var header = SegmentHeader.Read(data, diagnostics);
        var w = new ListingWriter(24);
        w.Header(title + ": 68k segment, " + (header is null ? "too short for its header" : header.IsFar ? "far header" : "near header"));
        w.EndHeader();
        int start = header?.Length ?? data.Length;
        var regions = new List<M68kDataRegion> { new(0, start, M68kDataKind.Header, "segment header") };
        var relocations = new Dictionary<long, M68kRelocation>();
        if (header is { IsFar: true } far)
        {
            regions.AddRange(FarLists(data, far));
            if (far.A5RelocationOffset != 0)
                foreach (long at in FarRelocations.Read(data, far.A5RelocationOffset, diagnostics))
                    relocations[at] = new M68kRelocation(M68kRelocationBase.A5, 0);
        }
        var map = M68kCodeMap.Build(data, start, [new M68kEntry(start, "entry", CodeFunctionSource.Entry)], regions);
        var references = WriteBody(w, map, new M68kContext { Relocations = relocations });
        return w.Finish(map.Functions.Values.ToList(), references, diagnostics, []);
    }

    private static CodeListing CodeResource(FourCC type, string title, ReadOnlyMemory<byte> data, List<Diagnostic> diagnostics)
    {
        var w = new ListingWriter(24);
        w.Header(title + ": 68k code resource");
        var entries = new List<M68kEntry>();
        var regions = new List<M68kDataRegion>();
        var fragments = new List<CodeListing>();
        bool entersAtStart = true;

        if (type == DriverType && DriverHeader.Read(data, diagnostics) is { IsStandard: true } driver)
        {
            w.Header($"Driver \"{driver.Name}\": flags ${(ushort)driver.Flags:X4}");
            int end = DriverHeader.NameOffset + 1 + MacRoman.Encode(driver.Name).Length;
            end = Math.Min(data.Length, (end + 1) & ~1);
            regions.Add(new M68kDataRegion(0, end, M68kDataKind.Header, "driver header"));
            foreach (var (offset, name) in new[]
            {
                (driver.Open, "Open"), (driver.Prime, "Prime"), (driver.Control, "Control"), (driver.Status, "Status"),
                (driver.Close, "Close"),
            })
                if (offset >= end)
                    entries.Add(new M68kEntry(offset, name, CodeFunctionSource.DriverRoutine));
            entersAtStart = false;
        }
        else if (PackageHeader.Read(data, diagnostics) is { } package)
        {
            w.Header($"Package: '{package.Type}' {package.Id}, selectors {package.FirstSelector} to {package.LastSelector}");
            int end = Math.Min(data.Length, PackageHeader.TableOffset + 2 * package.Entries.Count);
            regions.Add(new M68kDataRegion(0, end, M68kDataKind.Header, "package header"));
            foreach (var entry in package.Entries)
                if (entry.TargetOffset is { } target && target >= end && target < data.Length)
                    entries.Add(new M68kEntry((int)target,
                        "selector_" + (entry.Selector < 0 ? "m" : "") + Math.Abs(entry.Selector).ToString(CultureInfo.InvariantCulture),
                        CodeFunctionSource.PackageRoutine));
            entersAtStart = false;
        }
        else if (CodeResourceHeader.Read(data, type, diagnostics) is { } header)
        {
            w.Header($"Standard header: '{header.Type}' {header.Id}, version ${header.Version:X4}");
            int branch = header.Branch == CodeResourceBranch.BraShort ? 2 : 4;
            regions.Add(new M68kDataRegion(branch, CodeResourceHeader.Length - branch, M68kDataKind.Header, "code resource header"));
            entries.Add(new M68kEntry(0, "entry", CodeFunctionSource.Entry));
            entries.Add(new M68kEntry(header.BranchTarget, "main", CodeFunctionSource.Entry));
            entersAtStart = false;
        }
        if (entersAtStart)
            entries.Add(new M68kEntry(0, "entry", CodeFunctionSource.Entry));

        if (RoutineDescriptor.Find(data) is int at && RoutineDescriptor.Read(data, diagnostics) is { } descriptor)
        {
            int routines = descriptor.Routines.Count;
            w.Header($"Routine descriptor at ${at:X}: {routines} routine{(routines == 1 ? "" : "s")}");
            regions.Add(new M68kDataRegion(at, Math.Min(data.Length - at, 12 + 20 * routines), M68kDataKind.RoutineDescriptor));
            for (int i = 0; i < routines; i++)
            {
                var routine = descriptor.Routines[i];
                if (routine is not { TargetOffset: long target, Pef: { } pef } || target < 0 || target >= data.Length)
                    continue;
                int length = (int)Math.Min(data.Length - target, pef.Data.Length);
                regions.Add(new M68kDataRegion((int)target, length, M68kDataKind.Fragment, $"routine {i}"));
                fragments.Add(ForFragment(pef, $"{title} routine {i}", diagnostics));
            }
        }
        w.EndHeader();
        var map = M68kCodeMap.Build(data, 0, entries, regions);
        var references = WriteBody(w, map, new M68kContext());
        foreach (var fragment in fragments)
            w.Append("\n" + fragment.Text);
        return w.Finish(map.Functions.Values.ToList(), references, diagnostics, fragments);
    }

    private static IEnumerable<M68kDataRegion> FarLists(ReadOnlyMemory<byte> data, SegmentHeader far)
    {
        foreach (uint list in new[] { far.A5RelocationOffset, far.PcRelocationOffset })
            if (list != 0 && FarRelocations.ListLength(data, list) is int length and > 0)
                yield return new M68kDataRegion((int)list, length, M68kDataKind.Relocations);
    }

    private static int Offset(long resourceOffset) => (int)Math.Clamp(resourceOffset, -1, int.MaxValue);

    private static string ModelText(CodeModel model) => model switch
    {
        CodeModel.MpwNear => "MPW near",
        CodeModel.MpwFar => "MPW far",
        CodeModel.Retro68 => "Retro68",
        CodeModel.CodeWarrior => "CodeWarrior",
        _ => "unknown",
    };

    // The instructions and data in address order, with labels; returns the annotations.
    private static List<CodeReference> WriteBody(ListingWriter w, M68kCodeMap map, M68kContext context)
    {
        var references = new List<CodeReference>();
        var span = map.Code.Span;
        int d = 0;
        foreach (var (offset, ins) in map.Instructions)
        {
            for (; d < map.Data.Count && map.Data[d].Offset < offset; d++)
                w.M68kData(span, map.Data[d]);
            if (map.Functions.TryGetValue(offset, out var function))
                w.Label(function.Name);
            var refs = M68kAnnotator.Annotate(ins, map, context);
            references.AddRange(refs);
            var notes = new List<string>();
            if (ins.Comment is { } comment)
                notes.Add(comment);
            notes.AddRange(refs.Where(r => r.Kind != CodeReferenceKind.Trap).Select(r => r.Text));
            string text = ins.Comment is null ? ins.Text : ins.Text[..^(ins.Comment.Length + 4)];
            w.Line(offset.ToString("X8", CultureInfo.InvariantCulture),
                string.Join(" ", ins.Words.Select(x => x.ToString("X4", CultureInfo.InvariantCulture))), text,
                notes.Count == 0 ? null : string.Join("; ", notes));
        }
        for (; d < map.Data.Count; d++)
            w.M68kData(span, map.Data[d]);
        return references;
    }

    private sealed class ListingWriter(int hexWidth)
    {
        private const int BytesPerLine = 8;
        private const int WordsPerLine = 4;
        private readonly StringBuilder sb = new();
        private bool body;

        public void Header(string line) => sb.Append("; ").Append(line).Append('\n');

        public void EndHeader() => sb.Append('\n');

        public void Comment(string line)
        {
            sb.Append("; ").Append(line).Append('\n');
            body = true;
        }

        public void Blank() => sb.Append('\n');

        public void Append(string text) => sb.Append(text);

        public void Label(string name)
        {
            if (body)
                sb.Append('\n');
            sb.Append(name).Append(":\n");
            body = true;
        }

        public void Line(string address, string hex, string text, string? note)
        {
            sb.Append(address).Append("  ").Append(hex.PadRight(hexWidth)).Append("  ").Append(text);
            if (note is not null)
                sb.Append("  ; ").Append(note);
            sb.Append('\n');
            body = true;
        }

        public void M68kData(ReadOnlySpan<byte> code, M68kDataRegion region)
        {
            string? note = region.Kind switch
            {
                M68kDataKind.MacsBugName => "MacsBug name " + region.Note,
                M68kDataKind.Literals => "literals",
                M68kDataKind.SwitchTable => "switch table",
                M68kDataKind.RoutineDescriptor => "routine descriptor",
                M68kDataKind.Relocations => "relocations",
                M68kDataKind.Initializer => "%A5Init data",
                M68kDataKind.String => "string",
                M68kDataKind.Header => region.Note ?? "header",
                M68kDataKind.Fragment => "PowerPC fragment" + (region.Note is { } r ? " (" + r + ")" : "") + ", listed below",
                _ => null,
            };
            if (region.Kind == M68kDataKind.Fragment)
            {
                Line(region.Offset.ToString("X8", CultureInfo.InvariantCulture), "", $"ds.b {region.Length}", note);
                return;
            }
            for (int at = region.Offset; at < region.End;)
            {
                int n = (at & 1) != 0 ? 1 : Math.Min(BytesPerLine, region.End - at);
                if (n > 1)
                    n &= ~1;
                var bytes = code.Slice(at, n);
                string hex, text;
                if (n == 1)
                {
                    hex = bytes[0].ToString("X2", CultureInfo.InvariantCulture);
                    text = "dc.b $" + hex;
                }
                else
                {
                    var words = new List<string>();
                    for (int i = 0; i < n; i += 2)
                        words.Add(((bytes[i] << 8) | bytes[i + 1]).ToString("X4", CultureInfo.InvariantCulture));
                    hex = string.Join(" ", words);
                    text = "dc.w $" + string.Join(",$", words);
                }
                string preview = "'" + Preview(bytes) + "'";
                Line(at.ToString("X8", CultureInfo.InvariantCulture), hex, text, note is null ? preview : note + "; " + preview);
                note = null;
                at += n;
            }
        }

        public void PpcData(int section, ReadOnlySpan<byte> bytes, int offset, string? note)
        {
            for (int i = 0; i < bytes.Length;)
            {
                string address = $"{section}:{offset + i:X8}";
                if (bytes.Length - i >= 4)
                {
                    var words = new List<string>();
                    for (; words.Count < WordsPerLine && bytes.Length - i >= 4; i += 4)
                        words.Add(((uint)(bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3])).ToString("X8", CultureInfo.InvariantCulture));
                    Line(address, string.Join(" ", words), "dc.l $" + string.Join(",$", words), note);
                }
                else
                {
                    string hex = bytes[i].ToString("X2", CultureInfo.InvariantCulture);
                    Line(address, hex, "dc.b $" + hex, note);
                    i++;
                }
                note = null;
            }
        }

        public CodeListing Finish(IReadOnlyList<CodeFunction> functions, IReadOnlyList<CodeReference> references,
            IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<CodeListing> fragments) =>
            new(sb.ToString(), functions, references, diagnostics, fragments);

        private static string Preview(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
            return new string(chars);
        }
    }
}
