using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc
{
    /// <summary>A PEF section's kind (the section header's sectionKind byte).</summary>
    public enum PefSectionKind : byte
    {
        /// <summary>Code, read-only and executable (kPEFCodeSection).</summary>
        Code = 0,
        /// <summary>Data stored as is (kPEFUnpackedDataSection).</summary>
        UnpackedData = 1,
        /// <summary>Data stored as pattern-initialization instructions (kPEFPatternDataSection), unpacked by <see cref="PatternData"/>.</summary>
        PatternInitData = 2,
        /// <summary>Read-only data (kPEFConstantSection).</summary>
        Constant = 3,
        /// <summary>The loader section: imports, exports, relocations, entry points (kPEFLoaderSection).</summary>
        Loader = 4,
        /// <summary>Reserved for debugging information (kPEFDebugSection).</summary>
        Debug = 5,
        /// <summary>Data that is also executable (kPEFExecDataSection).</summary>
        ExecutableData = 6,
        /// <summary>Exception-handling tables (kPEFExceptionSection).</summary>
        Exception = 7,
        /// <summary>Traceback tables (kPEFTracebackSection).</summary>
        Traceback = 8,
    }

    /// <summary>How a PEF section is shared between processes (the section header's shareKind byte).</summary>
    public enum PefShareKind : byte
    {
        /// <summary>One copy per process (kPEFProcessShare).</summary>
        Process = 1,
        /// <summary>One copy for the whole system (kPEFGlobalShare).</summary>
        Global = 4,
        /// <summary>One copy for the whole system, writable only by privileged code (kPEFProtectedShare).</summary>
        Protected = 5,
    }

    /// <summary>A PEF section header (28 bytes).</summary>
    /// <param name="Index">The section's index.</param>
    /// <param name="Name">The section's name from the section name table, or null when it has none (nameOffset −1).</param>
    /// <param name="DefaultAddress">The address the section is built for.</param>
    /// <param name="TotalLength">The section's length in memory, including zero-filled space after its initialized part.</param>
    /// <param name="UnpackedLength">The length of the initialized part.</param>
    /// <param name="ContainerLength">The length of the stored contents.</param>
    /// <param name="ContainerOffset">Where the stored contents start, from the start of the container.</param>
    /// <param name="Kind">The section kind.</param>
    /// <param name="ShareKind">How the section is shared.</param>
    /// <param name="Alignment">The section's alignment, as a power of 2.</param>
    public sealed record PefSection(int Index, string? Name, uint DefaultAddress, uint TotalLength, uint UnpackedLength,
        uint ContainerLength, uint ContainerOffset, PefSectionKind Kind, PefShareKind ShareKind, byte Alignment)
    {
        /// <summary>Whether the section is placed in memory (code and data kinds; not the loader, debug or traceback sections).</summary>
        public bool IsInstantiable => Kind is PefSectionKind.Code or PefSectionKind.UnpackedData or PefSectionKind.PatternInitData
            or PefSectionKind.Constant or PefSectionKind.ExecutableData;
    }

    /// <summary>
    /// A PEF container (<c>Joy!peff</c>): the preferred executable format of PowerPC (and CFM-68K) code fragments
    /// [Doc: Mac OS Runtime Architectures, ch. 8]. A 40-byte header, the section headers, the section name table, then
    /// the sections' contents.
    /// </summary>
    public sealed class PefContainer
    {
        private const int HeaderSize = 40;
        private const int SectionHeaderSize = 28;

        private readonly ReadOnlyMemory<byte> data;
        private readonly byte[]?[] images;
        private IReadOnlyList<PefFixup>? fixups;
        private Dictionary<(int, long), PefFixup>? fixupsAt;

        private PefContainer(ReadOnlyMemory<byte> data, PefSection[] sections)
        {
            this.data = data;
            Sections = sections;
            images = new byte[]?[sections.Length];
        }

        /// <summary>The instruction set: <c>'pwpc'</c> (PowerPC) or <c>'m68k'</c> (CFM-68K).</summary>
        public FourCC Architecture { get; private init; }

        /// <summary>The format version (1).</summary>
        public uint FormatVersion { get; private init; }

        /// <summary>When the container was made, in seconds since 1904 (0 when not set).</summary>
        public uint DateTimeStamp { get; private init; }

        /// <summary>The oldest definition version the fragment is compatible with.</summary>
        public uint OldDefVersion { get; private init; }

        /// <summary>The oldest implementation version the fragment is compatible with.</summary>
        public uint OldImpVersion { get; private init; }

        /// <summary>The fragment's current version.</summary>
        public uint CurrentVersion { get; private init; }

        /// <summary>The number of sections placed in memory: the first this many sections.</summary>
        public int InstantiatedSectionCount { get; private init; }

        /// <summary>The section headers.</summary>
        public IReadOnlyList<PefSection> Sections { get; }

        /// <summary>The loader section, read; null when the container has none.</summary>
        public PefLoader? Loader { get; private set; }

        /// <summary>The container's bytes.</summary>
        public ReadOnlyMemory<byte> Data => data;

        /// <summary>Whether <paramref name="data"/> starts with a PEF container's tag, <c>Joy!peff</c>.</summary>
        public static bool IsPef(ReadOnlySpan<byte> data) => data.StartsWith("Joy!peff"u8);

        /// <summary>
        /// Reads a PEF container and its loader section. Damage past the header (sections outside the container, a
        /// truncated loader section) is reported as <c>pef.*</c> diagnostics and read as far as it goes.
        /// </summary>
        /// <param name="data">The container, starting at its <c>Joy!peff</c> tag (slice a data fork at a <c>'cfrg'</c> member's offset).</param>
        /// <param name="diagnostics">Receives problems.</param>
        /// <exception cref="InvalidDataException">The data is shorter than the 40-byte header or does not start with <c>Joy!peff</c>.</exception>
        public static PefContainer Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < HeaderSize)
                throw new InvalidDataException($"A PEF container header is {HeaderSize} bytes; there are {reader.Length}.");
            if (!IsPef(data.Span))
                throw new InvalidDataException("Not a PEF container: it does not start with 'Joy!peff'.");

            reader.Position = 8;
            var architecture = reader.ReadFourCC();
            var formatVersion = reader.ReadUInt32();
            var dateTimeStamp = reader.ReadUInt32();
            var oldDefVersion = reader.ReadUInt32();
            var oldImpVersion = reader.ReadUInt32();
            var currentVersion = reader.ReadUInt32();
            int sectionCount = reader.ReadUInt16();
            int instantiatedCount = reader.ReadUInt16();
            if (formatVersion != 1)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.format-version",
                    $"PEF format version {formatVersion}; only version 1 is defined.", 12));

            int fitting = Math.Min(sectionCount, (reader.Length - HeaderSize) / SectionHeaderSize);
            if (fitting < sectionCount)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.sections-truncated",
                    $"The container has room for {fitting} of its {sectionCount} section headers.", HeaderSize));
            long nameTable = HeaderSize + (long)sectionCount * SectionHeaderSize;

            var sections = new PefSection[fitting];
            for (int i = 0; i < fitting; i++)
            {
                reader.Position = HeaderSize + i * SectionHeaderSize;
                int nameOffset = reader.ReadInt32();
                var defaultAddress = reader.ReadUInt32();
                var totalLength = reader.ReadUInt32();
                var unpackedLength = reader.ReadUInt32();
                var containerLength = reader.ReadUInt32();
                var containerOffset = reader.ReadUInt32();
                var kind = (PefSectionKind)reader.ReadByte();
                var shareKind = (PefShareKind)reader.ReadByte();
                var alignment = reader.ReadByte();
                string? name = nameOffset == -1 ? null : SectionName(reader, nameTable, nameOffset, i, diagnostics);
                if (containerOffset > reader.Length || containerLength > reader.Length - (long)containerOffset)
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.section-out-of-range",
                        $"Section {i}'s contents (0x{containerOffset:X} + 0x{containerLength:X}) run past the container's end (0x{reader.Length:X}).",
                        HeaderSize + i * SectionHeaderSize));
                sections[i] = new PefSection(i, name, defaultAddress, totalLength, unpackedLength, containerLength,
                    containerOffset, kind, shareKind, alignment);
            }

            var container = new PefContainer(data, sections)
            {
                Architecture = architecture,
                FormatVersion = formatVersion,
                DateTimeStamp = dateTimeStamp,
                OldDefVersion = oldDefVersion,
                OldImpVersion = oldImpVersion,
                CurrentVersion = currentVersion,
                InstantiatedSectionCount = instantiatedCount,
            };
            foreach (var section in sections)
            {
                if (section.Kind != PefSectionKind.Loader) continue;
                container.Loader = PefLoader.Read(container.GetContents(section.Index), section.Index, diagnostics);
                break;
            }
            return container;
        }

        private static string? SectionName(BigEndianReader reader, long nameTable, int nameOffset, int index,
            ICollection<Diagnostic> diagnostics)
        {
            long at = nameTable + (uint)nameOffset;
            if (nameOffset < 0 || at >= reader.Length)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.section-name-out-of-range",
                    $"Section {index}'s name offset {nameOffset} is outside the container.", HeaderSize + index * SectionHeaderSize));
                return null;
            }
            var rest = reader.Source.Span[(int)at..];
            int end = rest.IndexOf((byte)0);
            if (end < 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.string-unterminated",
                    $"Section {index}'s name runs to the end of the container without a NUL.", at));
                end = rest.Length;
            }
            return MacRoman.Decode(rest[..end]);
        }

        /// <summary>A section's stored contents (clipped to the container when the header claims more).</summary>
        public ReadOnlyMemory<byte> GetContents(int sectionIndex)
        {
            var section = Sections[sectionIndex];
            long start = Math.Min(section.ContainerOffset, (long)data.Length);
            long length = Math.Min(section.ContainerLength, data.Length - start);
            return data.Slice((int)start, (int)length);
        }

        /// <summary>
        /// A section's initial image, <see cref="PefSection.TotalLength"/> bytes before relocation: pattern data unpacked,
        /// other kinds copied, and zeros after the initialized part. Problems go to <paramref name="diagnostics"/> the
        /// first time a section is built; the image is cached, so treat it as read-only (copy it to relocate).
        /// </summary>
        public ReadOnlyMemory<byte> GetImage(int sectionIndex, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            return images[sectionIndex] ??= BuildImage(Sections[sectionIndex], diagnostics);
        }

        private byte[] BuildImage(PefSection section, ICollection<Diagnostic> diagnostics)
        {
            var contents = GetContents(section.Index).Span;
            // A section longer than the bytes it could be built from is damage; cap it rather than allocate it.
            long cap = Math.Max(section.UnpackedLength, (long)contents.Length) + (1 << 24);
            long total = Math.Min(section.TotalLength, cap);
            ReadOnlySpan<byte> initialized;
            if (section.Kind == PefSectionKind.PatternInitData)
            {
                var unpacked = PatternData.Unpack(contents, diagnostics, (int)Math.Min(cap, PatternData.DefaultMaxLength));
                if (unpacked.Length != section.UnpackedLength)
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.pidata-length",
                        $"Section {section.Index}'s pattern data unpacks to 0x{unpacked.Length:X} bytes; its header says 0x{section.UnpackedLength:X}.",
                        section.ContainerOffset));
                initialized = unpacked;
            }
            else
            {
                initialized = contents[..(int)Math.Min(contents.Length, section.UnpackedLength)];
            }
            var image = new byte[Math.Max(total, initialized.Length)];
            initialized.CopyTo(image);
            return image;
        }

        /// <summary>
        /// Every relocation the loader section makes, run with each section at address 0 and every import at 0: where
        /// each fixup is and what it adds. Empty without a loader section. Problems are reported once, to the first
        /// caller's <paramref name="diagnostics"/>.
        /// </summary>
        public IReadOnlyList<PefFixup> GetFixups(ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (fixups is not null) return fixups;
            var all = new List<PefFixup>();
            foreach (var header in HeadersRun(diagnostics))
                all.AddRange(PefRelocator.Run(this, header, null, null, null, diagnostics));
            return fixups = all;
        }

        // The relocation headers that run: the first for each section. The Code Fragment Manager looks a section's header
        // up by its index and takes the first match [Code: the Code Fragment Manager in the Mac OS ROM]; later ones are
        // reported.
        private List<PefRelocationHeader> HeadersRun(ICollection<Diagnostic> diagnostics)
        {
            var run = new List<PefRelocationHeader>();
            if (Loader is null) return run;
            var seen = new HashSet<int>();
            foreach (var header in Loader.RelocationHeaders)
            {
                if (seen.Add(header.SectionIndex))
                    run.Add(header);
                else
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.relocation-duplicate-header",
                        $"Section {header.SectionIndex} has more than one relocation header; only the first runs."));
            }
            return run;
        }

        /// <summary>
        /// Places every instantiated section at <paramref name="sectionAddresses"/> and applies the loader's relocations
        /// to copies of the images, resolving imports through <paramref name="importAddress"/> (given the import's
        /// index).
        /// </summary>
        /// <returns>The relocated images (by section index; empty for sections not placed in memory) and the fixups made.</returns>
        public PefInstance Instantiate(IReadOnlyList<uint> sectionAddresses, Func<int, uint> importAddress,
            ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(sectionAddresses);
            ArgumentNullException.ThrowIfNull(importAddress);
            ArgumentNullException.ThrowIfNull(diagnostics);
            var relocated = new byte[Sections.Count][];
            for (int i = 0; i < relocated.Length; i++)
                relocated[i] = Sections[i].IsInstantiable ? GetImage(i, diagnostics).ToArray() : [];
            var made = new List<PefFixup>();
            foreach (var header in HeadersRun(diagnostics))
            {
                if ((uint)header.SectionIndex >= (uint)relocated.Length) continue;
                made.AddRange(PefRelocator.Run(this, header, relocated[header.SectionIndex], sectionAddresses,
                    importAddress, diagnostics));
            }
            return new PefInstance(relocated, made);
        }

        /// <summary>
        /// Reads the transition vector at <paramref name="sectionIndex"/>:<paramref name="offset"/> (a main, init or
        /// term entry point, or a <see cref="PefSymbolClass.TVector"/> export): the code address and the TOC base, as
        /// offsets into the sections the relocations add to them (the code section and the TOC's data section)
        /// [Doc: Mac OS Runtime Architectures, ch. 1, "Transition Vectors"]. Null when it is not inside an instantiated
        /// section.
        /// </summary>
        public PefTransitionVector? GetTransitionVector(int sectionIndex, uint offset, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if ((uint)sectionIndex >= (uint)Sections.Count || !Sections[sectionIndex].IsInstantiable) return null;
            var image = GetImage(sectionIndex, diagnostics);
            if (offset > (uint)image.Length || image.Length - offset < 8) return null;
            var reader = new BigEndianReader(image);
            var code = reader.ReadUInt32At((int)offset);
            var toc = reader.ReadUInt32At((int)offset + 4);

            if (fixupsAt is null)
            {
                fixupsAt = [];
                foreach (var fixup in GetFixups(diagnostics))
                    fixupsAt[(fixup.Section, fixup.Offset)] = fixup;
            }
            int SectionAdded(long at) =>
                fixupsAt.TryGetValue((sectionIndex, at), out var f) && f.Target == PefFixupTarget.Section ? f.TargetIndex : -1;
            return new PefTransitionVector(SectionAdded(offset), code, SectionAdded(offset + 4L), toc);
        }

        /// <summary>The transition vector of an entry point; null when there is none.</summary>
        public PefTransitionVector? GetTransitionVector(PefEntryPoint? entry, ICollection<Diagnostic> diagnostics) =>
            entry is { } e ? GetTransitionVector(e.Section, e.Offset, diagnostics) : null;
    }

    /// <summary>A section and an offset in it: a loader entry point (main, init, term).</summary>
    public readonly record struct PefEntryPoint(int Section, uint Offset)
    {
        /// <inheritdoc/>
        public override string ToString() => $"{Section}:0x{Offset:X}";
    }

    /// <summary>
    /// A transition vector, before relocation: the code's offset and the TOC base's offset, each with the section whose
    /// address the relocations add (−1 when no relocation touches the word).
    /// </summary>
    public readonly record struct PefTransitionVector(int CodeSection, uint CodeOffset, int TocSection, uint TocOffset);

    /// <summary>A container placed in memory: the relocated section images and the fixups made.</summary>
    public sealed record PefInstance(IReadOnlyList<byte[]> Images, IReadOnlyList<PefFixup> Fixups);
}
