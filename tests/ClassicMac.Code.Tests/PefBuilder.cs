using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// Builds small PEF containers field by field, the layout of Mac OS Runtime Architectures ch. 8: the 40-byte header,
// 28-byte section headers, the section name table, the sections' contents, and a loader section made from the
// libraries, exports, relocations and entry points given.
internal sealed class PefBuilder
{
    public sealed record Section(PefSectionKind Kind, byte[] Contents, uint UnpackedLength, uint TotalLength, string? Name = null,
        PefShareKind Share = PefShareKind.Process, byte Alignment = 4);

    public sealed record Import(string Name, PefSymbolClass Class = PefSymbolClass.TVector, byte Flags = 0);

    public sealed record Library(string Name, IReadOnlyList<Import> Symbols, PefLibraryOptions Options = PefLibraryOptions.None,
        uint OldImpVersion = 0, uint CurrentVersion = 0);

    // Key is the export's hash word, given as a literal (worked out by hand from the "Hash Word" rule) so the loader's
    // own Hash is not used to build its test input.
    public sealed record Export(string Name, uint Key, PefSymbolClass Class, uint Value, short Section);

    public List<Section> Sections { get; } = [];
    public List<Library> Libraries { get; } = [];
    public List<Export> Exports { get; } = [];
    public List<(int Section, ushort[] Words)> Relocations { get; } = [];
    public (int Section, uint Offset)? Main { get; set; }
    public (int Section, uint Offset)? Init { get; set; }
    public (int Section, uint Offset)? Term { get; set; }
    public int HashPower { get; set; } = 1;
    public bool WithLoader { get; set; } = true;
    public FourCC Architecture { get; set; } = FourCC.FromString("pwpc");
    public uint FormatVersion { get; set; } = 1;

    // The loader section's layout, filled in by Build for tests that patch it.
    public int LoaderOffset { get; private set; }
    public int LoaderStringsOffset { get; private set; }
    public int LoaderHashOffset { get; private set; }
    public int LoaderRelocOffset { get; private set; }

    public PefBuilder AddSection(PefSectionKind kind, byte[] contents, uint? unpacked = null, uint? total = null, string? name = null)
    {
        Sections.Add(new Section(kind, contents, unpacked ?? (uint)contents.Length, total ?? unpacked ?? (uint)contents.Length, name));
        return this;
    }

    public byte[] BuildLoader()
    {
        var strings = new BigEndianWriter();
        var nameOffsets = new Dictionary<string, int>();
        int CString(string s)
        {
            if (nameOffsets.TryGetValue(s, out var o)) return o;
            o = strings.Length;
            strings.WriteBytes(MacRoman.Encode(s));
            strings.WriteByte(0);
            nameOffsets[s] = o;
            return o;
        }
        foreach (var lib in Libraries)
        {
            CString(lib.Name);
            foreach (var sym in lib.Symbols) CString(sym.Name);
        }

        // Exports go in hash-chain order; their names are packed with no NUL between them. The slot is the "Hash Word"
        // index rule, (key XOR key >> power) AND (2^power - 1), written out here rather than taken from the loader.
        int power = HashPower;
        var ordered = Exports
            .Select((e, i) => (Export: e, e.Key, Slot: (int)((e.Key ^ (e.Key >> power)) & ((1u << power) - 1)), Order: i))
            .OrderBy(x => x.Slot).ThenBy(x => x.Order).ToList();
        var exportNameOffsets = new int[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            exportNameOffsets[i] = strings.Length;
            strings.WriteBytes(MacRoman.Encode(ordered[i].Export.Name));
        }

        int symbolCount = Libraries.Sum(l => l.Symbols.Count);
        var w = new BigEndianWriter();
        WriteEntry(w, Main);
        WriteEntry(w, Init);
        WriteEntry(w, Term);
        w.WriteInt32(Libraries.Count);
        w.WriteInt32(symbolCount);
        w.WriteInt32(Relocations.Count);
        int relocInstrAt = w.Length; w.WriteUInt32(0);
        int stringsAt = w.Length; w.WriteUInt32(0);
        int hashAt = w.Length; w.WriteUInt32(0);
        w.WriteInt32(power);
        w.WriteInt32(Exports.Count);

        int first = 0;
        foreach (var lib in Libraries)
        {
            w.WriteInt32(CString(lib.Name));
            w.WriteUInt32(lib.OldImpVersion);
            w.WriteUInt32(lib.CurrentVersion);
            w.WriteInt32(lib.Symbols.Count);
            w.WriteInt32(first);
            w.WriteByte((byte)lib.Options);
            w.WriteByte(0);
            w.WriteUInt16(0);
            first += lib.Symbols.Count;
        }
        foreach (var sym in Libraries.SelectMany(l => l.Symbols))
            w.WriteUInt32(((uint)sym.Flags << 28) | ((uint)sym.Class << 24) | (uint)CString(sym.Name));

        int relocFirst = 0;
        foreach (var (section, words) in Relocations)
        {
            w.WriteUInt16(section);
            w.WriteUInt16(0);
            w.WriteInt32(words.Length);
            w.WriteInt32(relocFirst);
            relocFirst += 2 * words.Length;
        }
        LoaderRelocOffset = w.Length;
        w.WriteInt32At(relocInstrAt, w.Length);
        foreach (var (_, words) in Relocations)
            foreach (var word in words) w.WriteUInt16(word);

        LoaderStringsOffset = w.Length;
        w.WriteInt32At(stringsAt, w.Length);
        w.WriteBytes(strings.WrittenSpan);
        while (w.Length % 4 != 0) w.WriteByte(0);

        LoaderHashOffset = w.Length;
        w.WriteInt32At(hashAt, w.Length);
        for (int slot = 0; slot < 1 << power; slot++)
        {
            int count = ordered.Count(x => x.Slot == slot);
            int firstIndex = count == 0 ? 0 : ordered.FindIndex(x => x.Slot == slot);
            w.WriteUInt32(((uint)count << 18) | (uint)firstIndex);
        }
        foreach (var x in ordered) w.WriteUInt32(x.Key);
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i].Export;
            w.WriteUInt32(((uint)e.Class << 24) | (uint)exportNameOffsets[i]);
            w.WriteUInt32(e.Value);
            w.WriteInt16(e.Section);
        }
        return w.ToArray();
    }

    private static void WriteEntry(BigEndianWriter w, (int Section, uint Offset)? entry)
    {
        w.WriteInt32(entry?.Section ?? -1);
        w.WriteUInt32(entry?.Offset ?? 0);
    }

    public byte[] Build()
    {
        var all = Sections.ToList();
        if (WithLoader)
        {
            var loader = BuildLoader();
            all.Add(new Section(PefSectionKind.Loader, loader, (uint)loader.Length, (uint)loader.Length, Share: 0, Alignment: 4));
        }
        var names = new BigEndianWriter();
        var nameOffsets = all.Select(s =>
        {
            if (s.Name is null) return -1;
            int o = names.Length;
            names.WriteBytes(MacRoman.Encode(s.Name));
            names.WriteByte(0);
            return o;
        }).ToList();

        var w = new BigEndianWriter();
        w.WriteBytes("Joy!peff"u8);
        w.WriteFourCC(Architecture);
        w.WriteUInt32(FormatVersion);
        w.WriteUInt32(0xB5000000);  // dateTimeStamp
        w.WriteUInt32(0x01000000);  // oldDefVersion
        w.WriteUInt32(0x01008000);  // oldImpVersion
        w.WriteUInt32(0x01108000);  // currentVersion
        w.WriteUInt16(all.Count);
        w.WriteUInt16(all.Count(s => s.Kind != PefSectionKind.Loader));
        w.WriteUInt32(0);
        int headers = w.Length;
        w.WriteZeros(28 * all.Count);
        w.WriteBytes(names.WrittenSpan);
        for (int i = 0; i < all.Count; i++)
        {
            while (w.Length % 16 != 0) w.WriteByte(0);
            int at = w.Length;
            if (all[i].Kind == PefSectionKind.Loader) LoaderOffset = at;
            w.WriteBytes(all[i].Contents);
            int h = headers + 28 * i;
            w.WriteInt32At(h, nameOffsets[i]);
            w.WriteUInt32At(h + 4, 0u);
            w.WriteUInt32At(h + 8, all[i].TotalLength);
            w.WriteUInt32At(h + 12, all[i].UnpackedLength);
            w.WriteInt32At(h + 16, all[i].Contents.Length);
            w.WriteInt32At(h + 20, at);
            w.WriteByteAt(h + 24, (byte)all[i].Kind);
            w.WriteByteAt(h + 25, (byte)all[i].Share);
            w.WriteByteAt(h + 26, all[i].Alignment);
        }
        return w.ToArray();
    }

    // A word list as big-endian bytes, for section contents.
    public static byte[] Words(params uint[] words)
    {
        var w = new BigEndianWriter();
        foreach (var word in words) w.WriteUInt32(word);
        return w.ToArray();
    }
}
