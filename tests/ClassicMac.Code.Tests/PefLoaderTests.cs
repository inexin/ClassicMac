using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// The loader section (Mac OS Runtime Architectures ch. 8): entry points, imports, relocation headers, exports and
// the export hash table.
public class PefLoaderTests
{
    private static PefBuilder Sample()
    {
        var b = new PefBuilder { HashPower = 2, Main = (1, 0x10), Init = (1, 0x18), Term = null };
        b.AddSection(PefSectionKind.Code, new byte[0x40]);
        b.AddSection(PefSectionKind.UnpackedData, new byte[0x40]);
        b.Libraries.Add(new PefBuilder.Library("InterfaceLib",
            [new("NewPtr"), new("DisposePtr", PefSymbolClass.TVector, 0x8), new("qd", PefSymbolClass.Data)],
            PefLibraryOptions.InitBefore, 0x01000000, 0x01108000));
        b.Libraries.Add(new PefBuilder.Library("WeakLib", [new("Maybe")], PefLibraryOptions.WeakImport));
        b.Exports.AddRange(
        [
            new("alpha", 0x00050401, PefSymbolClass.TVector, 0x10, 1),
            new("beta", 0x0004020D, PefSymbolClass.Code, 0x20, 0),
            new("gamma", 0x00050477, PefSymbolClass.Data, 0x30, 1),
            new("Magic", 0x000506F5, PefSymbolClass.Data, 0x12345678, PefExport.AbsoluteSection),
            new("NewPtr", 0x00060DF2, PefSymbolClass.TVector, 0, PefExport.ReexportedSection),
            new("DisposePtr", 0x000ACCA2, PefSymbolClass.TVector, 1, PefExport.ReexportedSection),
        ]);
        b.Relocations.Add((1, [0x4600]));
        return b;
    }

    private static (PefLoader Loader, List<Diagnostic> Diagnostics) Read(byte[] pef)
    {
        var diagnostics = new List<Diagnostic>();
        var container = PefContainer.Read(pef, diagnostics);
        return (container.Loader!, diagnostics);
    }

    // Reads a hand-patched loader section directly.
    private static (PefLoader? Loader, List<Diagnostic> Diagnostics) ReadLoader(byte[] loader)
    {
        var diagnostics = new List<Diagnostic>();
        return (PefLoader.Read(loader, 3, diagnostics), diagnostics);
    }

    [Fact]
    public void Reads_the_entry_points()
    {
        var (loader, diagnostics) = Read(Sample().Build());
        Assert.Empty(diagnostics);
        Assert.Equal(new PefEntryPoint(1, 0x10), loader.Main);
        Assert.Equal(new PefEntryPoint(1, 0x18), loader.Init);
        Assert.Null(loader.Term);
    }

    [Fact]
    public void Reads_the_imported_libraries()
    {
        var (loader, _) = Read(Sample().Build());
        Assert.Equal(2, loader.ImportedLibraries.Count);
        Assert.Equal(new PefImportedLibrary("InterfaceLib", 0x01000000, 0x01108000, 0, 3, PefLibraryOptions.InitBefore),
            loader.ImportedLibraries[0]);
        Assert.Equal(new PefImportedLibrary("WeakLib", 0, 0, 3, 1, PefLibraryOptions.WeakImport), loader.ImportedLibraries[1]);
    }

    [Fact]
    public void Reads_the_imported_symbols_with_class_weak_flag_and_library()
    {
        var (loader, _) = Read(Sample().Build());
        Assert.Equal(
        [
            new PefImportedSymbol("NewPtr", PefSymbolClass.TVector, 0, 0),
            new PefImportedSymbol("DisposePtr", PefSymbolClass.TVector, 0x8, 0),
            new PefImportedSymbol("qd", PefSymbolClass.Data, 0, 0),
            new PefImportedSymbol("Maybe", PefSymbolClass.TVector, 0, 1),
        ], loader.ImportedSymbols);
        Assert.False(loader.ImportedSymbols[0].IsWeak);
        Assert.True(loader.ImportedSymbols[1].IsWeak);
    }

    // A library's weak option ($40) covers its imports whose own flags are 0 (DC612's AOCELib, SpeechLib and DragLib)
    // [Verified: Disk Copy 6.1.2]; IsWeak is the symbol's own flag, so a consumer ORs in the library's option.
    [Fact]
    public void A_weak_library_leaves_its_symbols_own_flag_clear()
    {
        var (loader, _) = Read(Sample().Build());
        var maybe = loader.ImportedSymbols[3];
        Assert.False(maybe.IsWeak);
        Assert.True(loader.ImportedLibraries[maybe.LibraryIndex].Options.HasFlag(PefLibraryOptions.WeakImport));
    }

    [Fact]
    public void Import_classes_and_flags_are_kept_as_stored()
    {
        var b = Sample();
        b.Libraries.Add(new PefBuilder.Library("Odd",
            [new("c0", PefSymbolClass.Code), new("c3", PefSymbolClass.Toc), new("c4", PefSymbolClass.Glue),
             new("c7", (PefSymbolClass)7, 0x4), new("c15", (PefSymbolClass)15, 0xC)]));
        var (loader, diagnostics) = Read(b.Build());
        Assert.Empty(diagnostics);
        Assert.Equal([PefSymbolClass.Code, PefSymbolClass.Toc, PefSymbolClass.Glue, (PefSymbolClass)7, (PefSymbolClass)15],
            loader.ImportedSymbols.Skip(4).Select(s => s.Class));
        Assert.Equal([0, 0, 0, 0x4, 0xC], loader.ImportedSymbols.Skip(4).Select(s => (int)s.Flags));
        Assert.Equal([false, false, false, false, true], loader.ImportedSymbols.Skip(4).Select(s => s.IsWeak));
    }

    [Fact]
    public void Reads_the_relocation_headers()
    {
        var b = Sample();
        b.Relocations.Add((0, [0x4000, 0x8003]));
        var (loader, _) = Read(b.Build());
        Assert.Equal(2, loader.RelocationHeaders.Count);
        Assert.Equal(1, loader.RelocationHeaders[0].SectionIndex);
        Assert.Equal([0x4600], loader.RelocationHeaders[0].Instructions);
        Assert.Equal(0u, loader.RelocationHeaders[0].FirstOffset);
        Assert.Equal(0, loader.RelocationHeaders[1].SectionIndex);
        Assert.Equal([0x4000, 0x8003], loader.RelocationHeaders[1].Instructions);
        Assert.Equal(2u, loader.RelocationHeaders[1].FirstOffset);
        Assert.Equal((uint)b.LoaderRelocOffset, loader.RelocationsOffset);
        Assert.Equal((uint)b.LoaderStringsOffset, loader.StringsOffset);
    }

    [Fact]
    public void Reads_the_exports_with_their_keys()
    {
        var (loader, diagnostics) = Read(Sample().Build());
        Assert.Empty(diagnostics);
        Assert.Equal(6, loader.Exports.Count);
        Assert.Equal(2, loader.ExportHashTablePower);
        Assert.Equal(4, loader.ExportHashTable.Count);
        var alpha = loader.FindExport("alpha")!;
        Assert.Equal(new PefExport("alpha", 0x00050401, PefSymbolClass.TVector, 0x10, 1), alpha);
        Assert.False(alpha.IsAbsolute);
        Assert.False(alpha.IsReexport);
        Assert.Equal(new PefExport("beta", 0x0004020D, PefSymbolClass.Code, 0x20, 0), loader.FindExport("beta"));
        Assert.Equal(new PefExport("gamma", 0x00050477, PefSymbolClass.Data, 0x30, 1), loader.FindExport("gamma"));
        Assert.Equal(new PefExport("Magic", 0x000506F5, PefSymbolClass.Data, 0x12345678, PefExport.AbsoluteSection), loader.FindExport("Magic"));
    }

    [Fact]
    public void Absolute_exports_have_section_minus_2()
    {
        var (loader, _) = Read(Sample().Build());
        var magic = loader.FindExport("Magic")!;
        Assert.True(magic.IsAbsolute);
        Assert.Equal(0x12345678u, magic.Value);
        Assert.Null(loader.ReexportedImport(magic));
    }

    [Fact]
    public void Reexports_have_section_minus_3_and_name_an_import()
    {
        var (loader, _) = Read(Sample().Build());
        var reexport = loader.FindExport("DisposePtr")!;
        Assert.True(reexport.IsReexport);
        Assert.Equal(loader.ImportedSymbols[1], loader.ReexportedImport(reexport));
        Assert.Equal(reexport.Name, loader.ReexportedImport(reexport)!.Name);
        Assert.Throws<ArgumentNullException>(() => loader.ReexportedImport(null!));
    }

    [Fact]
    public void A_reexport_of_a_missing_import_is_reported()
    {
        var b = Sample();
        b.Exports.Add(new("Ghost", 0x0005061E, PefSymbolClass.TVector, 99, PefExport.ReexportedSection));
        var (loader, diagnostics) = Read(b.Build());
        Assert.Equal("pef.export-reexport-out-of-range", Assert.Single(diagnostics).Code);
        Assert.Null(loader.ReexportedImport(loader.FindExport("Ghost")!));
    }

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0x00010061u)]
    [InlineData("NewPtr", 0x00060DF2u)]
    [InlineData("GetResource", 0x000B9812u)]
    [InlineData("GetControlPopupMenuHandle", 0x00190C25u)]
    // Long enough for the 32-bit hash to go negative: the shift right is arithmetic (a logical one gives 0x0024C06D).
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", 0x0024CAFAu)]
    public void The_hash_word_is_the_length_and_the_folded_hash(string name, uint expected) =>
        Assert.Equal(expected, PefLoader.Hash(MacRoman.Encode(name)));

    [Theory]
    [InlineData(0x12345678u, 0, 0)]
    [InlineData(0x12345678u, 5, 11)]
    [InlineData(0x00060DF2u, 5, 29)]   // NewPtr
    [InlineData(0x00060DF2u, 2, 2)]
    [InlineData(0x00060DF2u, 1, 1)]
    [InlineData(0x00020086u, 5, 2)]    // NQD's "qd" [Verified: the Mac OS 9 System file]
    [InlineData(0x000CCD40u, 5, 10)]   // NQD's "NQDSetCursor" [Verified: the Mac OS 9 System file]
    public void The_hash_index_folds_the_word_by_the_power(uint word, int power, int index) =>
        Assert.Equal(index, PefLoader.HashIndex(word, power));

    // PEFComputeHashWord stops at a NUL; the length is the bytes before it [Doc: Mac OS Runtime Architectures, ch. 8,
    // "Hash Word"].
    [Fact]
    public void The_hash_word_stops_at_a_NUL()
    {
        Assert.Equal(0x00010061u, PefLoader.Hash("a\0bc"u8));
        Assert.Equal(0x00000000u, PefLoader.Hash("\0a"u8));
    }

    [Fact]
    public void A_name_with_a_NUL_is_not_found()
    {
        var b = new PefBuilder { HashPower = 0 };
        b.AddSection(PefSectionKind.Code, new byte[4]);
        b.Exports.Add(new("a", 0x00010061, PefSymbolClass.Code, 0, 0));
        var (loader, _) = Read(b.Build());
        Assert.NotNull(loader.FindExport("a"u8));
        Assert.Null(loader.FindExport("a\0b"u8));
    }

    // Real libraries hold names with the same hash word in one chain: MathLib's exp and nan, StdCLib's feof and open
    // [Verified: Mac OS 9.2.2's MathLib and StdCLib]. The lookup must compare the names, not only the keys.
    [Theory]
    [InlineData("exp", "nan", 0x00030114u)]
    [InlineData("feof", "open", 0x0004021Cu)]
    public void Exports_with_the_same_hash_word_are_told_apart_by_name(string first, string second, uint key)
    {
        foreach (int power in new[] { 0, 3 })
        {
            var b = new PefBuilder { HashPower = power };
            b.AddSection(PefSectionKind.Code, new byte[8]);
            b.Exports.Add(new(first, key, PefSymbolClass.Code, 0, 0));
            b.Exports.Add(new(second, key, PefSymbolClass.Code, 4, 0));
            var (loader, diagnostics) = Read(b.Build());
            Assert.Empty(diagnostics);
            Assert.Equal(key, PefLoader.Hash(MacRoman.Encode(first)));
            Assert.Equal(key, PefLoader.Hash(MacRoman.Encode(second)));
            Assert.Equal((first, 0u), (loader.FindExport(first)!.Name, loader.FindExport(first)!.Value));
            Assert.Equal((second, 4u), (loader.FindExport(second)!.Name, loader.FindExport(second)!.Value));
        }
    }

    [Fact]
    public void Every_export_is_found_through_the_hash_and_missing_names_are_not()
    {
        var (loader, _) = Read(Sample().Build());
        foreach (var export in loader.Exports)
        {
            Assert.Same(export, loader.FindExport(export.Name));
        }

        Assert.Null(loader.FindExport("delta"));
        Assert.Null(loader.FindExport("alph"));
        Assert.Null(loader.FindExport("一"));   // not Mac Roman
        Assert.Same(loader.Exports[0], loader.FindExport(MacRoman.Encode(loader.Exports[0].Name)));
    }

    [Fact]
    public void Exports_are_found_when_names_collide_in_a_chain()
    {
        var b = new PefBuilder { HashPower = 0 };
        b.AddSection(PefSectionKind.Code, new byte[4]);
        foreach (var (n, key) in new[] { ("one", 0x00030105u), ("two", 0x00030151u), ("three", 0x00050567u), ("four", 0x00040214u) })
        {
            b.Exports.Add(new(n, key, PefSymbolClass.Code, 0, 0));
        }

        var (loader, diagnostics) = Read(b.Build());
        Assert.Empty(diagnostics);
        Assert.Single(loader.ExportHashTable);
        Assert.Equal((4u << 18) | 0, loader.ExportHashTable[0]);
        foreach (var n in new[] { "one", "two", "three", "four" })
        {
            Assert.Equal(n, loader.FindExport(n)!.Name);
        }
    }

    [Fact]
    public void Export_names_are_not_NUL_terminated_and_are_read_by_the_key_length()
    {
        // The builder packs export names with no NUL: "ab" then "abc" share the bytes "ababc".
        var b = new PefBuilder { HashPower = 0 };
        b.AddSection(PefSectionKind.Code, new byte[4]);
        b.Exports.Add(new("ab", 0x000200A0, PefSymbolClass.Code, 0, 0));
        b.Exports.Add(new("abc", 0x00030123, PefSymbolClass.Code, 4, 0));
        var (loader, diagnostics) = Read(b.Build());
        Assert.Empty(diagnostics);
        Assert.Equal(["ab", "abc"], loader.Exports.Select(e => e.Name));
        Assert.Equal(4u, loader.FindExport("abc")!.Value);
    }

    [Fact]
    public void A_loader_without_exports_finds_nothing()
    {
        var b = new PefBuilder { HashPower = 0 };
        b.AddSection(PefSectionKind.Code, new byte[4]);
        var (loader, _) = Read(b.Build());
        Assert.Empty(loader.Exports);
        Assert.Null(loader.FindExport("x"));
    }

    [Fact]
    public void A_loader_shorter_than_its_header_is_reported()
    {
        var (loader, diagnostics) = ReadLoader(new byte[55]);
        Assert.Null(loader);
        Assert.Equal("pef.loader-truncated", Assert.Single(diagnostics).Code);
    }

    // The loader's header fields, by offset.
    private const int LibraryCountAt = 24, SymbolCountAt = 28, RelocCountAt = 32, RelocOffsetAt = 36, StringsAt = 40,
        HashOffsetAt = 44, HashPowerAt = 48, ExportCountAt = 52;

    private static byte[] Loader(Action<BigEndianWriter, PefBuilder> patch)
    {
        var b = Sample();
        var loader = b.BuildLoader();
        patch(new BigEndianWriter(loader), b);
        return loader;
    }

    [Theory]
    [InlineData(LibraryCountAt)]
    [InlineData(SymbolCountAt)]
    [InlineData(RelocCountAt)]
    [InlineData(ExportCountAt)]
    public void Tables_past_the_end_are_reported(int countAt)
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(countAt, 0x00FFFFFFu)));
        Assert.NotNull(loader);
        Assert.Contains(diagnostics, d => d.Code == "pef.loader-truncated");
    }

    [Fact]
    public void Exports_past_the_end_keep_those_that_fit()
    {
        // Seven exports move the symbol table 4 bytes on (it follows seven keys), leaving room for five of its 10-byte
        // entries before the section ends.
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(ExportCountAt, 7u)));
        Assert.Equal(5, loader!.Exports.Count);
        Assert.Contains(diagnostics, d => d.Code == "pef.loader-truncated");
    }

    // The Mac OS 9 System file's 'ndrv' resources -20192 to -20195 have $CBCC there [Verified: the Mac OS 9 System file].
    [Fact]
    public void A_relocation_header_reserved_field_is_ignored()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt16At(56 + 2 * 24 + 4 * 4 + 2, (ushort)0xCBCC)));
        Assert.Empty(diagnostics);
        Assert.Equal(1, loader!.RelocationHeaders[0].SectionIndex);
        Assert.Equal([0x4600], loader.RelocationHeaders[0].Instructions);
    }

    // An empty hash slot's first index is 0 in some libraries and the next chain's first in others [Verified: the
    // Mac OS 9 System file]; only nonempty chains are checked.
    [Fact]
    public void An_empty_hash_slot_may_hold_any_first_index()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, b) =>
        {
            var r = new BigEndianReader(w.WrittenMemory);
            for (int i = 0; i < 4; i++)
            {
                if (r.ReadUInt32At(b.LoaderHashOffset + 4 * i) >> 18 == 0)
                {
                    w.WriteUInt32At(b.LoaderHashOffset + 4 * i, 0x3u + (uint)i);
                }
            }
        }));
        Assert.Empty(diagnostics);
        Assert.Equal(6, loader!.Exports.Count(e => loader.FindExport(e.Name) == e));
    }

    [Fact]
    public void A_hash_power_of_18_is_allowed()
    {
        var (_, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(HashPowerAt, 18u)));
        Assert.DoesNotContain(diagnostics, d => d.Code == "pef.loader-hash-power");
    }

    [Fact]
    public void A_hash_table_past_the_end_is_reported()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(HashOffsetAt, 0x7FFFFFF0u)));
        Assert.Empty(loader!.ExportHashTable);
        Assert.Contains(diagnostics, d => d.Code == "pef.loader-truncated");
    }

    [Fact]
    public void Relocation_words_past_the_end_are_reported()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(RelocOffsetAt, 0x7FFFFFF0u)));
        Assert.Empty(loader!.RelocationHeaders[0].Instructions);
        Assert.Equal("pef.loader-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_hash_power_beyond_18_bits_is_reported_and_exports_are_skipped()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(HashPowerAt, 19u)));
        Assert.Empty(loader!.Exports);
        Assert.Equal("pef.loader-hash-power", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_string_outside_the_loader_is_reported()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(56, 0x00FFFFFFu))); // library 0's name
        Assert.Equal("", loader!.ImportedLibraries[0].Name);
        Assert.Equal("pef.loader-string-out-of-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_string_without_a_NUL_is_reported()
    {
        // Point the string table at the section's last byte and make it nonzero: library 0's name (offset 0) has no NUL.
        var (loader, diagnostics) = ReadLoader(Loader((w, _) =>
        {
            w.WriteByteAt(w.Length - 1, (byte)'A');
            w.WriteUInt32At(StringsAt, w.Length - 1);
        }));
        Assert.Equal("A", loader!.ImportedLibraries[0].Name);
        Assert.Contains(diagnostics, d => d.Code == "pef.string-unterminated" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_library_whose_symbols_run_past_the_imports_is_reported()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, _) => w.WriteUInt32At(56 + 12, 9u))); // library 0's count
        Assert.Equal(9, loader!.ImportedLibraries[0].SymbolCount);
        Assert.Equal("pef.loader-library-symbols", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_symbol_in_no_library_has_library_index_minus_1()
    {
        var (loader, _) = ReadLoader(Loader((w, _) => w.WriteUInt32At(56 + 24 + 12, 0u))); // library 1's count
        Assert.Equal(-1, loader!.ImportedSymbols[3].LibraryIndex);
    }

    [Fact]
    public void An_export_name_outside_the_loader_is_reported()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, b) =>
        {
            int symbols = b.LoaderHashOffset + 4 * 4 + 4 * 6;
            w.WriteUInt32At(symbols, 0x02FFFFFFu);
        }));
        Assert.Equal("", loader!.Exports[0].Name);
        Assert.Contains(diagnostics, d => d.Code == "pef.export-name-out-of-range");
    }

    [Fact]
    public void A_key_that_is_not_the_name_hash_is_a_warning()
    {
        var (loader, diagnostics) = ReadLoader(Loader((w, b) =>
        {
            int keys = b.LoaderHashOffset + 4 * 4;
            w.WriteUInt16At(keys + 2, (ushort)0xFFFF); // keep the length, break the hash
        }));
        var d = Assert.Single(diagnostics);
        Assert.Equal(("pef.export-hash-mismatch", DiagnosticSeverity.Warning), (d.Code, d.Severity));
        // The lookup compares the hash words before the names, so the damaged entry is not found.
        Assert.Null(loader!.FindExport(loader.Exports[0].Name));
        Assert.NotNull(loader.FindExport(loader.Exports[1].Name));
    }

    [Fact]
    public void Hash_chains_out_of_order_are_a_warning()
    {
        var (_, diagnostics) = ReadLoader(Loader((w, b) =>
        {
            // Swap the first two nonempty chains' first indexes.
            var r = new BigEndianReader(w.WrittenMemory);
            var chains = Enumerable.Range(0, 4).Select(i => (At: b.LoaderHashOffset + 4 * i, Entry: r.ReadUInt32At(b.LoaderHashOffset + 4 * i)))
                .Where(x => x.Entry >> 18 != 0).Take(2).ToList();
            Assert.Equal(2, chains.Count);
            w.WriteUInt32At(chains[0].At, (chains[0].Entry & ~0x3FFFFu) | (chains[1].Entry & 0x3FFFF));
            w.WriteUInt32At(chains[1].At, (chains[1].Entry & ~0x3FFFFu) | (chains[0].Entry & 0x3FFFF));
        }));
        Assert.Contains(diagnostics, d => d.Code == "pef.export-hash-chains" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Hash_chains_that_do_not_cover_every_export_are_a_warning()
    {
        var (_, diagnostics) = ReadLoader(Loader((w, b) =>
        {
            // One more export in the last nonempty chain than there are exports.
            var r = new BigEndianReader(w.WrittenMemory);
            int at = Enumerable.Range(0, 4).Select(i => b.LoaderHashOffset + 4 * i).Last(a => r.ReadUInt32At(a) >> 18 != 0);
            w.WriteUInt32At(at, r.ReadUInt32At(at) + (1u << 18));
        }));
        Assert.Equal("pef.export-hash-chains", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Diagnostics_are_required() => Assert.Throws<ArgumentNullException>(() => PefLoader.Read(new byte[56], 0, null!));
}
