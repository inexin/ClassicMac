using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc;

/// <summary>What an imported or exported symbol is (CFragSymbolClass).</summary>
public enum PefSymbolClass : byte
{
    /// <summary>A code address (kCodeCFragSymbol).</summary>
    Code = 0,
    /// <summary>A data item (kDataCFragSymbol).</summary>
    Data = 1,
    /// <summary>A transition vector (kTVectorCFragSymbol): how functions are called across fragments.</summary>
    TVector = 2,
    /// <summary>A TOC (kTOCCFragSymbol).</summary>
    Toc = 3,
    /// <summary>Linker-inserted glue (kGlueCFragSymbol).</summary>
    Glue = 4,
}

/// <summary>An imported library's options byte.</summary>
[Flags]
public enum PefLibraryOptions : byte
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Every import from the library is weak: the fragment loads without it (kPEFWeakImportLibMask).</summary>
    WeakImport = 0x40,
    /// <summary>The library's initialization runs before the importer's (kPEFInitLibBeforeMask).</summary>
    InitBefore = 0x80,
}

/// <summary>A library the fragment imports from (24 bytes).</summary>
/// <param name="Name">The library's fragment name.</param>
/// <param name="OldImpVersion">The oldest implementation version the fragment accepts.</param>
/// <param name="CurrentVersion">The version the fragment was linked against.</param>
/// <param name="FirstSymbol">The index of the library's first symbol in <see cref="PefLoader.ImportedSymbols"/>.</param>
/// <param name="SymbolCount">How many symbols come from the library.</param>
/// <param name="Options">The options byte.</param>
public sealed record PefImportedLibrary(string Name, uint OldImpVersion, uint CurrentVersion, int FirstSymbol,
    int SymbolCount, PefLibraryOptions Options);

/// <summary>An imported symbol (4 bytes: flags in bits 28–31, class in 24–27, name offset in 0–23).</summary>
/// <param name="Name">The symbol's name.</param>
/// <param name="Class">What it is.</param>
/// <param name="Flags">The flag bits (bits 28–31 of the entry); <c>0x8</c> is weak.</param>
/// <param name="LibraryIndex">The index of the library it comes from in <see cref="PefLoader.ImportedLibraries"/>, or −1.</param>
public sealed record PefImportedSymbol(string Name, PefSymbolClass Class, byte Flags, int LibraryIndex)
{
    /// <summary>Whether the symbol itself is weak (kPEFWeakImportSymMask): the fragment loads without it.</summary>
    public bool IsWeak => (Flags & 0x8) != 0;
}

/// <summary>A relocation header (12 bytes): one section's relocation instructions.</summary>
/// <param name="SectionIndex">The section the instructions relocate.</param>
/// <param name="Instructions">The instruction words.</param>
/// <param name="FirstOffset">Where the instructions start, from the start of the relocation area.</param>
public sealed record PefRelocationHeader(int SectionIndex, IReadOnlyList<ushort> Instructions, uint FirstOffset);

/// <summary>An exported symbol (a key table entry and a 10-byte symbol table entry).</summary>
/// <param name="Name">The symbol's name.</param>
/// <param name="HashKey">The stored hash word: the name's length in the high 16 bits, its hash in the low 16.</param>
/// <param name="Class">What it is (the low 4 bits of classAndName's top byte).</param>
/// <param name="Value">An offset in <see cref="SectionIndex"/>, an absolute value (section −2), or an import index (section −3).</param>
/// <param name="SectionIndex">The section the value is in; −2 absolute, −3 re-exported import.</param>
public sealed record PefExport(string Name, uint HashKey, PefSymbolClass Class, uint Value, short SectionIndex)
{
    /// <summary>The section index of an absolute export (kPEFAbsoluteExport).</summary>
    public const short AbsoluteSection = -2;

    /// <summary>The section index of a re-exported import (kPEFReexportedImport).</summary>
    public const short ReexportedSection = -3;

    /// <summary>Whether the value is an absolute address rather than a section offset.</summary>
    public bool IsAbsolute => SectionIndex == AbsoluteSection;

    /// <summary>Whether the export passes on an import (the value is the import's index).</summary>
    public bool IsReexport => SectionIndex == ReexportedSection;

    // The name as stored, for lookups; not part of equality.
    internal ReadOnlyMemory<byte> NameBytes { get; init; }

    /// <summary>Whether two exports have the same name, key, class, value and section.</summary>
    public bool Equals(PefExport? other) =>
        other is not null && Name == other.Name && HashKey == other.HashKey && Class == other.Class && Value == other.Value
        && SectionIndex == other.SectionIndex;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, HashKey, Class, Value, SectionIndex);
}

/// <summary>
/// A PEF loader section [Doc: Mac OS Runtime Architectures, ch. 8, "Loader Section"]: the entry points, the
/// imported libraries and symbols, the relocations, the string table, and the exports with their hash table.
/// </summary>
public sealed class PefLoader
{
    private const int HeaderSize = 56;
    private uint[] hashTable = [];

    private PefLoader() { }

    /// <summary>The loader section's index in the container.</summary>
    public int SectionIndex { get; private init; }

    /// <summary>The main entry point (an application's, or a library's main symbol); null when there is none.</summary>
    public PefEntryPoint? Main { get; private init; }

    /// <summary>The initialization routine; null when there is none.</summary>
    public PefEntryPoint? Init { get; private init; }

    /// <summary>The termination routine; null when there is none.</summary>
    public PefEntryPoint? Term { get; private init; }

    /// <summary>The imported libraries.</summary>
    public IReadOnlyList<PefImportedLibrary> ImportedLibraries { get; private set; } = [];

    /// <summary>The imported symbols, in import-index order.</summary>
    public IReadOnlyList<PefImportedSymbol> ImportedSymbols { get; private set; } = [];

    /// <summary>The relocation headers.</summary>
    public IReadOnlyList<PefRelocationHeader> RelocationHeaders { get; private set; } = [];

    /// <summary>The export hash table's size as a power of 2.</summary>
    public int ExportHashTablePower { get; private init; }

    /// <summary>The exports, in symbol-table order (the hash chains tile it).</summary>
    public IReadOnlyList<PefExport> Exports { get; private set; } = [];

    /// <summary>The hash table's entries: each chain's length (bits 18–31) and first export index (bits 0–17).</summary>
    public IReadOnlyList<uint> ExportHashTable => hashTable;

    /// <summary>The offset of the loader string table from the start of the loader section.</summary>
    public uint StringsOffset { get; private init; }

    /// <summary>The offset of the relocation instructions from the start of the loader section.</summary>
    public uint RelocationsOffset { get; private init; }

    /// <summary>
    /// Reads a loader section. A truncated or inconsistent section is reported (<c>pef.loader-*</c>) and read as far
    /// as it goes.
    /// </summary>
    /// <param name="data">The loader section's contents.</param>
    /// <param name="sectionIndex">Its index in the container.</param>
    /// <param name="diagnostics">Receives problems.</param>
    public static PefLoader? Read(ReadOnlyMemory<byte> data, int sectionIndex, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var reader = new BigEndianReader(data);
        if (reader.Length < HeaderSize)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-truncated",
                $"The loader section is {reader.Length} bytes, shorter than its {HeaderSize}-byte header."));
            return null;
        }
        PefEntryPoint? Entry()
        {
            int section = reader.ReadInt32();
            uint offset = reader.ReadUInt32();
            return section == -1 ? null : new PefEntryPoint(section, offset);
        }
        var main = Entry();
        var init = Entry();
        var term = Entry();
        uint libraryCount = reader.ReadUInt32();
        uint symbolCount = reader.ReadUInt32();
        uint relocSectionCount = reader.ReadUInt32();
        uint relocInstrOffset = reader.ReadUInt32();
        uint stringsOffset = reader.ReadUInt32();
        uint hashOffset = reader.ReadUInt32();
        uint hashPower = reader.ReadUInt32();
        uint exportCount = reader.ReadUInt32();

        var loader = new PefLoader
        {
            SectionIndex = sectionIndex,
            Main = main,
            Init = init,
            Term = term,
            ExportHashTablePower = (int)Math.Min(hashPower, 31),
            StringsOffset = stringsOffset,
            RelocationsOffset = relocInstrOffset,
        };
        if (hashPower > 18)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-hash-power",
                $"The export hash table power {hashPower} is larger than an export index (18 bits) allows."));
        }

        long at = HeaderSize;
        loader.ReadImports(reader, ref at, libraryCount, symbolCount, diagnostics);
        loader.ReadRelocationHeaders(reader, at, relocSectionCount, diagnostics);
        if (hashPower <= 18)
        {
            loader.ReadExports(reader, hashOffset, exportCount, diagnostics);
        }

        return loader;
    }

    private static long Fit(BigEndianReader reader, long at, uint count, int size, string what, ICollection<Diagnostic> diagnostics)
    {
        long room = Math.Max(0, (reader.Length - at) / size);
        if (count <= room)
        {
            return count;
        }

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-truncated",
            $"The loader section has room for {room} of its {count} {what}.", at));
        return room;
    }

    // A C string in the loader string table.
    private string CString(BigEndianReader reader, uint nameOffset, ICollection<Diagnostic> diagnostics)
    {
        long at = (long)StringsOffset + nameOffset;
        if (at >= reader.Length)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-string-out-of-range",
                $"A loader string at 0x{nameOffset:X} lies outside the loader section.", at));
            return "";
        }
        var rest = reader.Source.Span[(int)at..];
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.string-unterminated",
                $"A loader string at 0x{nameOffset:X} runs to the end of the loader section without a NUL.", at));
            end = rest.Length;
        }
        return MacRoman.Decode(rest[..end]);
    }

    private void ReadImports(BigEndianReader reader, ref long at, uint libraryCount, uint symbolCount,
        ICollection<Diagnostic> diagnostics)
    {
        long libraries = Fit(reader, at, libraryCount, 24, "imported libraries", diagnostics);
        var libs = new PefImportedLibrary[libraries];
        for (int i = 0; i < libs.Length; i++)
        {
            reader.Position = (int)(at + 24L * i);
            var nameOffset = reader.ReadUInt32();
            var oldImpVersion = reader.ReadUInt32();
            var currentVersion = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var first = reader.ReadUInt32();
            var options = (PefLibraryOptions)reader.ReadByte();
            if (first > symbolCount || count > symbolCount - first)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-library-symbols",
                    $"Imported library {i}'s symbols ({first} + {count}) run past the {symbolCount} imported symbols.", reader.Position - 9));
            }

            libs[i] = new PefImportedLibrary(CString(reader, nameOffset, diagnostics), oldImpVersion, currentVersion,
                (int)Math.Min(first, int.MaxValue), (int)Math.Min(count, int.MaxValue), options);
        }
        at += 24L * libraryCount;

        long symbols = Fit(reader, at, symbolCount, 4, "imported symbols", diagnostics);
        var owner = new int[symbols];
        Array.Fill(owner, -1);
        for (int l = 0; l < libs.Length; l++)
        {
            for (long s = libs[l].FirstSymbol; s < (long)libs[l].FirstSymbol + libs[l].SymbolCount && s < symbols; s++)
            {
                owner[s] = l;
            }
        }

        var imports = new PefImportedSymbol[symbols];
        for (int i = 0; i < imports.Length; i++)
        {
            var entry = reader.ReadUInt32At((int)(at + 4L * i));
            imports[i] = new PefImportedSymbol(CString(reader, entry & 0xFFFFFF, diagnostics),
                (PefSymbolClass)((entry >> 24) & 0xF), (byte)(entry >> 28), owner[i]);
        }
        at += 4L * symbolCount;
        ImportedLibraries = libs;
        ImportedSymbols = imports;
    }

    private void ReadRelocationHeaders(BigEndianReader reader, long at, uint count, ICollection<Diagnostic> diagnostics)
    {
        long fitting = Fit(reader, at, count, 12, "relocation headers", diagnostics);
        var headers = new PefRelocationHeader[fitting];
        for (int i = 0; i < headers.Length; i++)
        {
            reader.Position = (int)(at + 12L * i);
            int section = reader.ReadUInt16();
            reader.Skip(2);
            uint words = reader.ReadUInt32();
            uint first = reader.ReadUInt32();
            long start = (long)RelocationsOffset + first;
            long room = start > reader.Length ? 0 : (reader.Length - start) / 2;
            if (words > room)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.loader-truncated",
                    $"Section {section}'s {words} relocation words run past the loader section; {room} read.", start));
            }

            var instructions = new ushort[Math.Min(words, room)];
            for (int w = 0; w < instructions.Length; w++)
            {
                instructions[w] = reader.ReadUInt16At((int)(start + 2L * w));
            }

            headers[i] = new PefRelocationHeader(section, instructions, first);
        }
        RelocationHeaders = headers;
    }

    private void ReadExports(BigEndianReader reader, uint hashOffset, uint exportCount, ICollection<Diagnostic> diagnostics)
    {
        long hashCount = 1L << ExportHashTablePower;
        long fitting = Fit(reader, hashOffset, (uint)hashCount, 4, "export hash entries", diagnostics);
        hashTable = new uint[fitting];
        for (int i = 0; i < hashTable.Length; i++)
        {
            hashTable[i] = reader.ReadUInt32At((int)(hashOffset + 4L * i));
        }

        long keys = hashOffset + 4 * hashCount;
        long count = Fit(reader, keys, exportCount, 4, "export keys", diagnostics);
        long symbols = keys + 4L * exportCount;
        count = Math.Min(count, Fit(reader, symbols, exportCount, 10, "exported symbols", diagnostics));

        var exports = new PefExport[count];
        for (int i = 0; i < exports.Length; i++)
        {
            var key = reader.ReadUInt32At((int)(keys + 4L * i));
            reader.Position = (int)(symbols + 10L * i);
            var classAndName = reader.ReadUInt32();
            var value = reader.ReadUInt32();
            var section = reader.ReadInt16();
            // Export names are not NUL-terminated: the key's high 16 bits give the length.
            long nameAt = (long)StringsOffset + (classAndName & 0xFFFFFF);
            int length = (int)(key >> 16);
            ReadOnlyMemory<byte> name;
            if (nameAt > reader.Length || length > reader.Length - nameAt)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.export-name-out-of-range",
                    $"Export {i}'s name (0x{classAndName & 0xFFFFFF:X}, {length} bytes) lies outside the loader section.", symbols + 10L * i));
                name = ReadOnlyMemory<byte>.Empty;
            }
            else
            {
                name = reader.Source.Slice((int)nameAt, length);
            }
            var export = new PefExport(MacRoman.Decode(name.Span), key, (PefSymbolClass)((classAndName >> 24) & 0xF), value, section)
            {
                NameBytes = name,
            };
            if (name.Length == length && Hash(name.Span) != key)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.export-hash-mismatch",
                    $"Export '{export.Name}' has key 0x{key:X8}; its name hashes to 0x{Hash(name.Span):X8}.", keys + 4L * i));
            }

            if (export.IsReexport && value >= (uint)ImportedSymbols.Count)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.export-reexport-out-of-range",
                    $"Export '{export.Name}' re-exports import {value}; there are {ImportedSymbols.Count}.", symbols + 10L * i));
            }

            exports[i] = export;
        }
        Exports = exports;

        // The chains tile the export table in order [Verified: Mac OS 9 fragments].
        long next = 0;
        foreach (var entry in hashTable)
        {
            long chain = entry >> 18, first = entry & 0x3FFFF;
            if (chain == 0)
            {
                continue;
            }

            if (first != next)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.export-hash-chains",
                    "The export hash chains do not cover the exports in order.", hashOffset));
                return;
            }
            next += chain;
        }
        if (next != exportCount)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pef.export-hash-chains",
                $"The export hash chains cover {next} exports; there are {exportCount}.", hashOffset));
        }
    }

    /// <summary>
    /// The export hash word of a name: the name's length in the high 16 bits; in the low 16, the hash folded from
    /// 32 bits [Doc: Mac OS Runtime Architectures, ch. 8, "Hash Word"]:
    /// <c>hash = (hash &lt;&lt; 1) − (hash &gt;&gt; 16)</c> (arithmetic shift) then <c>hash ^= byte</c> for each byte, and
    /// <c>(hash ^ (hash &gt;&gt; 16)) &amp; 0xFFFF</c>. Like PEFComputeHashWord it stops at a NUL: the length and the
    /// hash cover the bytes before it.
    /// </summary>
    public static uint Hash(ReadOnlySpan<byte> name)
    {
        int nul = name.IndexOf((byte)0);
        if (nul >= 0)
        {
            name = name[..nul];
        }

        int hash = 0;
        foreach (var c in name)
        {
            hash = unchecked((hash << 1) - (hash >> 16)) ^ c;
        }

        return unchecked((uint)(name.Length << 16)) | (uint)((hash ^ (hash >> 16)) & 0xFFFF);
    }

    /// <summary>The hash table index of a hash word for a table of 2^<paramref name="power"/> entries.</summary>
    public static int HashIndex(uint hashWord, int power) =>
        (int)((hashWord ^ (hashWord >> power)) & ((1u << power) - 1));

    /// <summary>Finds an export by name through the hash table, as the Code Fragment Manager does; null when there is none.</summary>
    public PefExport? FindExport(ReadOnlySpan<byte> name)
    {
        if (hashTable.Length == 0)
        {
            return null;
        }

        var key = Hash(name);
        int index = HashIndex(key, ExportHashTablePower);
        if (index >= hashTable.Length)
        {
            return null;
        }

        var entry = hashTable[index];
        long first = entry & 0x3FFFF, end = first + (entry >> 18);
        for (long i = first; i < end && i < Exports.Count; i++)
        {
            var export = Exports[(int)i];
            if (export.HashKey == key && export.NameBytes.Span.SequenceEqual(name))
            {
                return export;
            }
        }
        return null;
    }

    /// <summary>Finds an export by name (Mac Roman) through the hash table; null when there is none.</summary>
    public PefExport? FindExport(string name) =>
        MacRoman.TryEncode(name, out var bytes) ? FindExport(bytes) : null;

    /// <summary>The import a re-export passes on; null for other exports.</summary>
    public PefImportedSymbol? ReexportedImport(PefExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        return export.IsReexport && export.Value < (uint)ImportedSymbols.Count ? ImportedSymbols[(int)export.Value] : null;
    }
}
