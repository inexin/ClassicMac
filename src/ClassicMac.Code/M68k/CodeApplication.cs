using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.M68k
{
    /// <summary>How a 68k application's code was built, from the shapes its resources take.</summary>
    public enum CodeModel
    {
        /// <summary>None of the shapes below.</summary>
        Unknown,
        /// <summary>MPW, near model: 4-byte segment headers and a <c>%A5Init</c> segment with the <c>'mpwd'</c> trailer.</summary>
        MpwNear,
        /// <summary>MPW, far model: the far marker at jump-table entry 1 and <c>$FFFF</c> headers using their first pair.</summary>
        MpwFar,
        /// <summary>Retro68: <c>'RELA'</c> relocation resources (far headers using their second pair).</summary>
        Retro68,
        /// <summary>CodeWarrior: <c>'DATA'</c> 0 and <c>'CODE'</c> 1 starting with CodeWarrior's startup.</summary>
        CodeWarrior,
    }

    /// <summary>Where code starts running.</summary>
    /// <param name="Segment">The segment (<c>'CODE'</c> ID).</param>
    /// <param name="Offset">The offset the jump-table entry gives (from the code for near, the resource for far).</param>
    /// <param name="ResourceOffset">The offset in the <c>'CODE'</c> resource.</param>
    public sealed record CodeEntryPoint(short Segment, uint Offset, long ResourceOffset);

    /// <summary>One <c>'CODE'</c> segment (ID 1 and up).</summary>
    public sealed class CodeSegment
    {
        internal CodeSegment(Resource resource, ReadOnlyMemory<byte> data, bool readable)
        {
            Id = resource.Id;
            Name = resource.Name?.ToMacRoman();
            Attributes = resource.Attributes;
            Data = data;
            IsReadable = readable;
        }

        /// <summary>The resource ID.</summary>
        public short Id { get; }

        /// <summary>The resource name (such as <c>%A5Init</c> or <c>Main</c>), if any.</summary>
        public string? Name { get; }

        /// <summary>The resource attributes.</summary>
        public ResourceAttributes Attributes { get; }

        /// <summary>Whether the resource is stored compressed.</summary>
        public bool IsCompressed => (Attributes & ResourceAttributes.Compressed) != 0;

        /// <summary>Whether <see cref="Data"/> is the code: false when it is compressed data that could not be decompressed.</summary>
        public bool IsReadable { get; }

        /// <summary>The segment's bytes, decompressed when it is stored compressed.</summary>
        public ReadOnlyMemory<byte> Data { get; }

        /// <summary>The header, or null when the segment is unreadable or too short.</summary>
        public SegmentHeader? Header { get; internal set; }

        /// <summary>A far segment's A5 relocations: offsets in the resource of longs that get A5 added.</summary>
        public IReadOnlyList<long> A5Relocations { get; internal set; } = [];

        /// <summary>A far segment's PC relocations: offsets in the resource of longs that get the segment's address added.</summary>
        public IReadOnlyList<long> PcRelocations { get; internal set; } = [];

        /// <summary>A Retro68 segment's relocations (from <c>'RELA'</c> with the same ID).</summary>
        public IReadOnlyList<Retro68Relocation> Retro68Relocations { get; internal set; } = [];
    }

    /// <summary>
    /// A 68k application's code: <c>'CODE'</c> 0 (the A5 world's sizes and the jump table), the segments, the entry point
    /// and the global-data initializer its build model uses
    /// [Doc: Inside Macintosh II, the Segment Loader; Mac OS Runtime Architectures, the 68k run-time architecture].
    /// </summary>
    public sealed class CodeApplication
    {
        private const int Code0HeaderLength = 16;
        private const int EntryLength = 8;
        private const int BootstrapEntry = 0x10;
        private const ushort MoveWToStack = 0x3F3C;   // MOVE.W #imm,-(SP)
        private const ushort LoadSeg = 0xA9F0;        // _LoadSeg
        private const ushort JmpAbsoluteLong = 0x4EF9; // JMP abs.L
        private const ulong FarMarkerValue = 0x0000FFFF00000000;

        private static readonly FourCC CodeType = FourCC.FromString("CODE");
        private static readonly FourCC DataType = FourCC.FromString("DATA");
        private static readonly FourCC RelaType = FourCC.FromString("RELA");
        private static readonly FourCC CfrgType = FourCC.FromString("cfrg");

        // CodeWarrior's startup at the start of CODE 1's code: SUBA.L A6,A6; SUBQ.L #4,SP; MOVE.L #'CODE',-(SP)
        // [Verified: a CodeWarrior 68k application].
        private static ReadOnlySpan<byte> CodeWarriorStartup => [0x9D, 0xCE, 0x59, 0x8F, 0x2F, 0x3C, (byte)'C', (byte)'O', (byte)'D', (byte)'E'];

        private CodeApplication() { }

        /// <summary>The bytes above A5: 32 bytes of application parameters plus the jump table (and CodeWarrior's globals).</summary>
        public uint AboveA5 { get; private set; }

        /// <summary>The bytes below A5: the application's and QuickDraw's globals.</summary>
        public uint BelowA5 { get; private set; }

        /// <summary>The jump table's size in bytes.</summary>
        public uint JumpTableSize { get; private set; }

        /// <summary>The jump table's offset from A5 (<c>$20</c>).</summary>
        public uint JumpTableOffset { get; private set; }

        /// <summary>The jump table's entries.</summary>
        public IReadOnlyList<JumpTableEntry> JumpTable { get; private set; } = [];

        /// <summary>Whether entry 1 is the far marker: the table uses far entries after it.</summary>
        public bool IsFarModel { get; private set; }

        /// <summary>The segments, <c>'CODE'</c> 1 and up, in ID order.</summary>
        public IReadOnlyList<CodeSegment> Segments { get; private set; } = [];

        /// <summary>The entry point: jump-table entry 0; null when it does not name a segment.</summary>
        public CodeEntryPoint? Entry { get; private set; }

        /// <summary>
        /// The entry the application had before a bootstrap segment took entry 0 over: the segment keeps it 8 bytes in,
        /// after the jump table's A5 offset, and puts it back before jumping there. Null without such a segment.
        /// </summary>
        public CodeEntryPoint? OriginalEntry { get; private set; }

        /// <summary>How the code was built.</summary>
        public CodeModel Model { get; private set; }

        /// <summary>Whether the file also has PowerPC code (<c>'cfrg'</c> 0): a fat application.</summary>
        public bool HasPowerPCFragment { get; private set; }

        /// <summary>The MPW global-data initializer, when a segment carries one.</summary>
        public MpwA5Init? A5Init { get; private set; }

        /// <summary>The segment the MPW initializer is in.</summary>
        public short? A5InitSegment { get; private set; }

        /// <summary>CodeWarrior's packed globals and relocations, for <see cref="CodeModel.CodeWarrior"/>.</summary>
        public CodeWarriorData? CodeWarriorData { get; private set; }

        /// <summary>Retro68's relocations of <c>'DATA'</c> 0 (<c>'RELA'</c> 0), for <see cref="CodeModel.Retro68"/>.</summary>
        public IReadOnlyList<Retro68Relocation> DataRelocations { get; private set; } = [];

        /// <summary>The segment with ID <paramref name="id"/>, or null.</summary>
        public CodeSegment? FindSegment(short id)
        {
            foreach (var segment in Segments)
            {
                if (segment.Id == id)
                {
                    return segment;
                }
            }

            return null;
        }

        /// <summary>
        /// What <c>n(A5)</c> refers to when <paramref name="displacement"/> is a jump-table call target (the entry plus 2):
        /// that entry; otherwise null, and <c>n(A5)</c> is a global (below A5) or an application parameter. The far marker
        /// and unrecognized entries are not call targets.
        /// </summary>
        public JumpTableEntry? ResolveA5(int displacement)
        {
            long d = (long)displacement - 2 - JumpTableOffset;
            if (d < 0 || d % EntryLength != 0)
            {
                return null;
            }

            long index = d / EntryLength;
            return index < JumpTable.Count
                && JumpTable[(int)index] is { Kind: not (JumpTableEntryKind.FarMarker or JumpTableEntryKind.Unrecognized) } entry
                ? entry : null;
        }

        /// <summary>
        /// Reads an application's code from its resource fork. Compressed resources are decompressed with
        /// <paramref name="decompression"/> (the built-in <c>'dcmp'</c>s by default); a segment that cannot be is reported
        /// (<c>code.compressed</c>) and left unreadable. Damage is reported as <c>m68k.*</c> diagnostics.
        /// </summary>
        /// <exception cref="InvalidDataException">There is no readable <c>'CODE'</c> 0 of at least 16 bytes.</exception>
        public static CodeApplication Read(ResourceFork fork, ICollection<Diagnostic> diagnostics,
            ResourceDecompression? decompression = null)
        {
            ArgumentNullException.ThrowIfNull(fork);
            ArgumentNullException.ThrowIfNull(diagnostics);
            decompression ??= ResourceDecompression.Default;

            var code0Resource = fork.Find(CodeType, 0)
                ?? throw new InvalidDataException("Not a 68k application: there is no 'CODE' 0.");
            if (!TryGetData(code0Resource, out var code0))
            {
                throw new InvalidDataException("'CODE' 0 is compressed and could not be decompressed.");
            }

            var app = new CodeApplication();
            app.ReadJumpTable(code0, diagnostics);

            var segments = new List<CodeSegment>();
            foreach (var resource in fork.OfType(CodeType).Where(r => r.Id > 0).OrderBy(r => r.Id))
            {
                bool readable = TryGetData(resource, out var data);
                var segment = new CodeSegment(resource, data, readable);
                if (readable)
                {
                    segment.Header = SegmentHeader.Read(data, diagnostics);
                }

                if (segment.Header is { IsFar: true } far)
                {
                    if (far.A5RelocationOffset != 0)
                    {
                        segment.A5Relocations = FarRelocations.Read(data, far.A5RelocationOffset, diagnostics);
                    }

                    if (far.PcRelocationOffset != 0)
                    {
                        segment.PcRelocations = FarRelocations.Read(data, far.PcRelocationOffset, diagnostics);
                    }
                }
                segments.Add(segment);
            }
            app.Segments = segments;
            app.CheckEntries(diagnostics);
            app.FindEntryPoint(diagnostics);
            app.HasPowerPCFragment = fork.Find(CfrgType, 0) is not null;

            foreach (var segment in segments)
            {
                if (segment.IsReadable && MpwA5Init.HasTrailer(segment.Data.Span))
                {
                    app.A5Init = MpwA5Init.Read(segment.Data, diagnostics);
                    app.A5InitSegment = segment.Id;
                    app.CheckBelowA5(app.A5Init.BelowA5Size, "The %A5Init globals", diagnostics);
                    break;
                }
            }

            var relas = fork.OfType(RelaType).OrderBy(r => r.Id).ToList();
            var data0 = fork.Find(DataType, 0);
            ReadOnlyMemory<byte>? data0Image = null;
            if (relas.Count > 0)
            {
                app.Model = CodeModel.Retro68;
                if (data0 is not null && TryGetData(data0, out var image))
                {
                    app.CheckBelowA5((uint)image.Length, "'DATA' 0's initialized data", diagnostics);
                    data0Image = image;
                }
                foreach (var rela in relas)
                {
                    if (!TryGetData(rela, out var relaData))
                    {
                        continue;
                    }

                    if (rela.Id == 0)
                    {
                        if (data0 is null)
                        {
                            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.rela-target",
                                "'RELA' 0 has no 'DATA' 0 to relocate; not read."));
                        }
                        else if (data0Image is { } readable)
                        {
                            app.DataRelocations = Retro68Relocations.Read(relaData, 0, readable.Length, diagnostics);
                        }
                    }
                    else if (app.FindSegment(rela.Id) is not { } target)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.rela-target",
                            $"'RELA' {rela.Id} has no 'CODE' {rela.Id} to relocate; not read."));
                    }
                    else if (target.IsReadable)
                    {
                        target.Retro68Relocations = Retro68Relocations.Read(relaData,
                            Retro68Relocations.CodeStart(target.Data.Span), target.Data.Length, diagnostics);
                    }
                }
            }
            else if (data0 is not null && app.FindSegment(1) is { IsReadable: true } code1 && StartsWithCodeWarriorStartup(code1.Data.Span))
            {
                app.Model = CodeModel.CodeWarrior;
                if (TryGetData(data0, out var cw))
                {
                    try
                    {
                        app.CodeWarriorData = CodeWarriorData.Read(cw, diagnostics);
                    }
                    catch (InvalidDataException e)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.cw-data-truncated", e.Message));
                    }
                }
            }
            else if (app.IsFarModel && segments.Any(s => s.Header is { IsFar: true, NearCount: > 0 }))
            {
                app.Model = CodeModel.MpwFar;
            }
            else if (!segments.Any(s => s.Header is { IsFar: true }) && app.A5Init is not null)
            {
                app.Model = CodeModel.MpwNear;
            }

            return app;

            bool TryGetData(Resource resource, out ReadOnlyMemory<byte> data)
            {
                data = decompression.GetData(resource, fork, null, diagnostics);
                if ((resource.Attributes & ResourceAttributes.Compressed) == 0 || !CompressedResourceHeader.HasSignature(data))
                {
                    return true;
                }

                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "code.compressed",
                    $"{resource}: compressed and could not be decompressed; its code is not read."));
                return false;
            }
        }

        private static bool StartsWithCodeWarriorStartup(ReadOnlySpan<byte> code1) =>
            code1.Length >= SegmentHeader.NearLength + CodeWarriorStartup.Length
            && code1.Slice(SegmentHeader.NearLength, CodeWarriorStartup.Length).SequenceEqual(CodeWarriorStartup);

        // CODE 0: aboveA5, belowA5, the jump table's size and A5 offset, then the table.
        private void ReadJumpTable(ReadOnlyMemory<byte> code0, ICollection<Diagnostic> diagnostics)
        {
            var reader = new BigEndianReader(code0);
            if (reader.Length < Code0HeaderLength)
            {
                throw new InvalidDataException($"'CODE' 0 is {reader.Length} bytes, shorter than its {Code0HeaderLength}-byte header.");
            }

            AboveA5 = reader.ReadUInt32();
            BelowA5 = reader.ReadUInt32();
            JumpTableSize = reader.ReadUInt32();
            JumpTableOffset = reader.ReadUInt32();
            if (JumpTableSize % EntryLength != 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.jt-size",
                    $"The jump table's size {JumpTableSize} is not a multiple of 8; the last {JumpTableSize % EntryLength} bytes are ignored.", 8));
            }

            long count = JumpTableSize / EntryLength;
            if (count > reader.Remaining / EntryLength)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.jt-truncated",
                    $"The jump table's {count} entries run past the end of 'CODE' 0; {reader.Remaining / EntryLength} read.", 8));
                count = reader.Remaining / EntryLength;
            }
            if (AboveA5 < (long)JumpTableOffset + JumpTableSize)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.above-a5",
                    $"The {AboveA5} bytes above A5 do not hold the jump table (offset {JumpTableOffset}, {JumpTableSize} bytes).", 0));
            }

            var entries = new List<JumpTableEntry>((int)count);
            for (int i = 0; i < count; i++)
            {
                int at = Code0HeaderLength + EntryLength * i;
                ulong raw = reader.ReadUInt64At(at);
                ushort w0 = (ushort)(raw >> 48), w1 = (ushort)(raw >> 32), w2 = (ushort)(raw >> 16), w3 = (ushort)raw;
                int a5 = (int)(JumpTableOffset + (uint)(EntryLength * i));
                JumpTableEntry entry;
                if (i == 1 && raw == FarMarkerValue)
                {
                    IsFarModel = true;
                    entry = new JumpTableEntry(i, a5, JumpTableEntryKind.FarMarker, 0, 0, 0, raw);
                }
                else if (w1 == MoveWToStack && w3 == LoadSeg)
                {
                    entry = new JumpTableEntry(i, a5, JumpTableEntryKind.NearUnloaded, (short)w2, w0, 0, raw);
                }
                else if (w1 == LoadSeg)
                {
                    entry = new JumpTableEntry(i, a5, JumpTableEntryKind.FarUnloaded, (short)w0, (uint)raw, 0, raw);
                }
                else if (w1 == JmpAbsoluteLong)
                {
                    entry = new JumpTableEntry(i, a5, IsFarModel && i >= 2 ? JumpTableEntryKind.FarLoaded : JumpTableEntryKind.NearLoaded,
                        (short)w0, 0, (uint)raw, raw);
                }
                else
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.jt-entry",
                        $"Jump-table entry {i} ({raw:X16}) is none of the entry forms.", at));
                    entry = new JumpTableEntry(i, a5, JumpTableEntryKind.Unrecognized, 0, 0, 0, raw);
                }
                entries.Add(entry);
            }
            JumpTable = entries;
        }

        // Every unloaded entry must name a segment and land inside it (a segment that could not be decompressed is not checked).
        private void CheckEntries(ICollection<Diagnostic> diagnostics)
        {
            foreach (var entry in JumpTable)
            {
                if (entry.ResourceOffset is not { } offset)
                {
                    continue;
                }

                var segment = FindSegment(entry.Segment);
                if (segment is null)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.segment-missing",
                        $"Jump-table entry {entry.Index} names 'CODE' {entry.Segment}, which does not exist."));
                }
                else if (segment.IsReadable && offset >= segment.Data.Length)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.entry-range",
                        $"Jump-table entry {entry.Index} points at {offset:X} in 'CODE' {entry.Segment}, past its {segment.Data.Length} bytes."));
                }
                else if (segment.Header is { } header && offset < header.Length)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.entry-header",
                        $"Jump-table entry {entry.Index} points at {offset:X} in 'CODE' {entry.Segment}, inside its {header.Length}-byte header."));
                }
            }
        }

        // The globals an initializer writes must fit in CODE 0's belowA5.
        private void CheckBelowA5(uint size, string what, ICollection<Diagnostic> diagnostics)
        {
            if (size > BelowA5)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.below-a5",
                    $"{what} take {size} bytes; 'CODE' 0 has {BelowA5} below A5."));
            }
        }

        // Entry 0; and the bootstrap shape: a near segment entered at +$10 that holds the table's A5 offset at +4 and the
        // original entry 0 at +8, which it copies back to the table before jumping there [Code: ResEdit 2.1.3's 'CODE' 66].
        private void FindEntryPoint(ICollection<Diagnostic> diagnostics)
        {
            if (JumpTable.Count == 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.entry-missing", "The jump table is empty: there is no entry point."));
                return;
            }
            var first = JumpTable[0];
            if (first.ResourceOffset is not { } offset)
            {
                return;
            }

            Entry = new CodeEntryPoint(first.Segment, first.Offset, offset);
            if (first.Kind != JumpTableEntryKind.NearUnloaded || offset != BootstrapEntry)
            {
                return;
            }

            if (FindSegment(first.Segment) is not { IsReadable: true, Header.IsFar: false } segment
                || segment.Data.Length < BootstrapEntry)
            {
                return;
            }

            var reader = new BigEndianReader(segment.Data);
            if (reader.ReadUInt32At(4) != JumpTableOffset
                || reader.ReadUInt16At(10) != MoveWToStack || reader.ReadUInt16At(14) != LoadSeg)
            {
                return;
            }

            ushort savedOffset = reader.ReadUInt16At(8);
            OriginalEntry = new CodeEntryPoint(reader.ReadInt16At(12), savedOffset, savedOffset + 4L);
        }
    }
}
