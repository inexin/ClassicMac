using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Compression;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Graphics;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Rez;

namespace Fuzz;

/// <summary>
/// The writers' targets: the input chooses what is written, and what is written must read back as what was meant. A
/// mismatch, or a check failing on the writer's own output, is a crash (<see cref="Bad"/>).
/// </summary>
internal static class WriterTargets
{
    private static InvalidOperationException Bad(string what) => new(what);

    /// <summary>Hears each hfs-edit step when set (replaying a crash).</summary>
    public static Action<string>? Trace { get; set; }

    // ---- hfs-edit ----

    // A sequence of edits on a new HFS volume (the input chooses each edit, its names and its data), each one either
    // refused or made; after each, the volume passes the writer's checks and First Aid, and holds exactly the files and
    // folders the edits made, every fork byte for byte.
    public static void HfsEdit(ReadOnlyMemory<byte> input)
    {
        var r = new FuzzReader(input);
        var date = new MacDate(3_000_000_000);
        var volume = HfsWriter.Format((1 + r.Int(4)) * 400 * 1024, "Fuzz", date);
        var files = new Dictionary<string, (byte[] Data, byte[] Resource)>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal) { "" };
        for (var step = 0; step < 24 && !r.Done; step++)
        {
            var before = volume;
            try
            {
                volume = Edit(r, volume, files, folders, date);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                volume = before;                                                       // refused: nothing changed
                Trace?.Invoke($"refused: {e.Message}");
            }

            Trace?.Invoke($"after step {step}: files [{string.Join(", ", files.Keys)}] folders [{string.Join(", ", folders)}]");

            Verify(volume, files, folders);
        }
    }

    private static byte[] Edit(FuzzReader r, byte[] volume, Dictionary<string, (byte[] Data, byte[] Resource)> files, HashSet<string> folders, MacDate date)
    {
        var image = ForkData.FromBytes(volume);
        string Pick(IEnumerable<string> from)
        {
            var list = from.Order(StringComparer.Ordinal).ToList();
            return list.Count == 0 ? "" : list[r.Int(list.Count)];
        }

        string Join(string folder, string name) => folder.Length == 0 ? name : folder + ":" + name;
        string Name()
        {
            const string Letters = "abcdefgh0123 .é";
            var length = 1 + r.Int(8);
            return new string([.. Enumerable.Range(0, length).Select(_ => Letters[r.Int(Letters.Length)])]);
        }

        void Moved(string from, string to)
        {
            if (from == to)
            {
                return;
            }

            foreach (var key in files.Keys.Where(k => k == from || k.StartsWith(from + ":", StringComparison.Ordinal)).ToList())
            {
                files[to + key[from.Length..]] = files[key];
                files.Remove(key);
            }

            foreach (var key in folders.Where(k => k == from || k.StartsWith(from + ":", StringComparison.Ordinal)).ToList())
            {
                folders.Remove(key);
                folders.Add(to + key[from.Length..]);
            }
        }

        switch (r.Int(7))
        {
            case 0:
                {
                    var path = Join(Pick(folders), Name());
                    Trace?.Invoke($"mkdir {path}");
                    var result = HfsWriter.CreateFolder(image, path, date, date);
                    folders.Add(path);
                    return result;
                }
            case 1:
                {
                    var path = Join(Pick(folders), Name());
                    var data = r.Content(r.Int(60_000));
                    var resource = r.Content(r.Int(3) == 0 ? r.Int(20_000) : 0);
                    Trace?.Invoke($"create {path} ({data.Length}, {resource.Length})");
                    var result = HfsWriter.CreateFile(image, path, data, resource, FinderInfo.Empty, date, date);
                    files[path] = (data, resource);
                    return result;
                }
            case 2:
                {
                    var path = Pick(files.Keys);
                    Trace?.Invoke($"delete {path}");
                    var result = HfsWriter.DeleteFile(image, path);
                    files.Remove(path);
                    return result;
                }
            case 3:
                {
                    var path = Pick(folders.Where(f => f.Length > 0));
                    Trace?.Invoke($"rmdir {path}");
                    var result = HfsWriter.DeleteFolder(image, path);
                    folders.Remove(path);
                    return result;
                }
            case 4:
                {
                    var path = Pick(files.Keys.Concat(folders.Where(f => f.Length > 0)));
                    var name = Name();
                    Trace?.Invoke($"rename {path} to {name}");
                    var result = HfsWriter.Rename(image, path, name);
                    var parent = path.Contains(':') ? path[..path.LastIndexOf(':')] : "";
                    Moved(path, Join(parent, name));
                    return result;
                }
            case 5:
                {
                    var path = Pick(files.Keys.Concat(folders.Where(f => f.Length > 0)));
                    var into = Pick(folders);
                    Trace?.Invoke($"move {path} into [{into}]");
                    var result = HfsWriter.Move(image, path, into);
                    Moved(path, Join(into, path[(path.LastIndexOf(':') + 1)..]));
                    return result;
                }
            default:
                {
                    var path = Pick(files.Keys);
                    var resourceFork = r.Bool();
                    var data = r.Content(r.Int(60_000));
                    Trace?.Invoke($"replace {path} {(resourceFork ? "resource" : "data")} ({data.Length})");
                    var result = HfsWriter.ReplaceFork(image, path, resourceFork ? HfsFork.Resource : HfsFork.Data, data);
                    files[path] = resourceFork ? (files[path].Data, data) : (data, files[path].Resource);
                    return result;
                }
        }
    }

    private static void Verify(byte[] volume, Dictionary<string, (byte[] Data, byte[] Resource)> files, HashSet<string> folders)
    {
        var image = ForkData.FromBytes(volume);
        if (HfsWriter.Check(image) is { } fault)
        {
            throw Bad($"The writer's own volume fails its checks: {fault}");
        }

        if (HfsFirstAid.Verify(image) is { Verdict: not FirstAidVerdict.AppearsOk } report)
        {
            throw Bad($"The writer's own volume fails First Aid: {string.Join("; ", report.Problems.Select(p => p.Message))}");
        }

        var read = HfsReader.Instance.Read(image, new ContainerContext());
        string PathOf(IEnumerable<MacString> names) => string.Join(":", names.Select(n => n.ToMacRoman()));
        if (read.Count != files.Count)
        {
            throw Bad($"The volume holds {read.Count} files; the edits made {files.Count}.");
        }

        foreach (var file in read)
        {
            var path = PathOf(file.FolderPath.Append(file.Name));
            if (!files.TryGetValue(path, out var forks) || !file.DataFork.ToArray().AsSpan().SequenceEqual(forks.Data)
                || !file.ResourceFork.ToArray().AsSpan().SequenceEqual(forks.Resource))
            {
                throw Bad($"{path} is not what the edits wrote.");
            }
        }

        var readFolders = HfsReader.Instance.ReadFolders(image, new ContainerContext()).Where(f => !f.IsRoot).Select(f => PathOf(f.Path)).ToHashSet(StringComparer.Ordinal);
        if (!readFolders.SetEquals(folders.Where(f => f.Length > 0)))
        {
            throw Bad("The volume's folders are not the ones the edits made.");
        }
    }

    // ---- wrappers ----

    // A file (its name, Finder info and forks chosen by the input) through MacBinary III, AppleSingle and BinHex and
    // back; a resource fork through the fork writer and through DeRez and Rez and back; data through ADC, KenCode and
    // PackBits and back (QuickDraw's PackBits is pict-write's, in the pixel maps it records).
    public static void Wrappers(ReadOnlyMemory<byte> input)
    {
        var r = new FuzzReader(input);
        // A name a Mac file can have: 1 to 31 bytes, no ':' or NUL.
        var name = r.Bytes(31).Where(b => b is not ((byte)':' or 0)).ToArray();
        if (name.Length == 0)
        {
            name = [(byte)'x'];
        }

        var file = new MacFile
        {
            Name = new MacString(name),
            FinderInfo = FinderInfo.Empty with { Type = new FourCC((uint)r.Int(int.MaxValue)), Creator = new FourCC((uint)r.Int(int.MaxValue)) },
            DataFork = ForkData.FromBytes(r.Bytes(4000)),
            ResourceFork = ForkData.FromBytes(r.Bytes(4000)),
        };
        Same(file, MacBinaryReader.III.Read(ForkData.FromBytes(MacBinaryWriter.ToArray(file)), new ContainerContext()), "MacBinary III");
        using (var single = new MemoryStream())
        {
            AppleDoubleWriter.WriteAppleSingle(file, single);
            Same(file, AppleSingleReader.AppleSingle.Read(ForkData.FromBytes(single.ToArray()), new ContainerContext()), "AppleSingle");
        }

        Same(file, BinHexReader.Instance.Read(ForkData.FromBytes(System.Text.Encoding.ASCII.GetBytes(BinHexWriter.ToText(file))), new ContainerContext()), "BinHex");

        var fork = new ResourceFork();
        for (var n = r.Int(6); n > 0; n--)
        {
            var type = new FourCC((uint)r.Int(int.MaxValue));
            var id = (short)r.Int(65536);
            if (fork.Find(type, id) is not null)
            {
                continue;
            }

            var resourceName = r.Bool() ? new MacString(r.Bytes(20)) : (MacString?)null;
            fork.Add(new Resource(type, id, r.Bytes(300)) { Name = resourceName, Attributes = (ResourceAttributes)(r.Byte() & 0x7C) });
        }

        Same(fork, ResourceFork.Read(fork.ToArray()), "the resource fork writer");
        var diagnostics = new List<Diagnostic>();
        var rez = RezWriter.Write(fork, null, diagnostics);
        var compiled = RezCompiler.Compile(rez, new RezCompileOptions(), diagnostics)
            ?? throw Bad($"Rez refused DeRez's output: {string.Join("; ", diagnostics.Select(d => d.Message))}");
        Same(fork, compiled, "DeRez and Rez");

        var data = r.Bytes(3000);
        Codec("ADC", Adc.Compress(data), data, (packed, output) => Adc.Decompress(packed, output, out var written) == Adc.Result.Done && written == output.Length);
        // KenCode output longer than its input is not decodable (the decoder reads at most 8 bits per byte): Disk Copy
        // stores such a chunk raw (kencode.md §3).
        if (KenCode.Compress(data) is var kenCode && kenCode.Length <= data.Length)
        {
            Codec("KenCode", kenCode, data, (packed, output) => KenCode.Decompress(packed, output, out var written) == KenCode.Result.Done && written == output.Length);
        }
        foreach (var unit in new[] { 1, 2 })
        {
            var whole = data.AsSpan(0, data.Length / unit * unit).ToArray();
            Codec($"PackBits ({unit})", PackBits.Pack(whole, unit), whole,
                (packed, output) => PackBits.Unpack(packed, output, new PackBitsOptions { UnitSize = unit }).Written == output.Length);
        }
    }

    private static void Same(MacFile file, IReadOnlyList<MacFile> read, string through)
    {
        if (read.Count != 1 || !read[0].Name.Bytes.SequenceEqual(file.Name.Bytes) || read[0].FinderInfo.Type != file.FinderInfo.Type
            || read[0].FinderInfo.Creator != file.FinderInfo.Creator || !read[0].DataFork.ToArray().AsSpan().SequenceEqual(file.DataFork.ToArray())
            || !read[0].ResourceFork.ToArray().AsSpan().SequenceEqual(file.ResourceFork.ToArray()))
        {
            throw Bad($"The file does not come back through {through}.");
        }
    }

    private static void Same(ResourceFork fork, ResourceFork read, string through)
    {
        var expected = fork.Resources.OrderBy(x => x.Type.Value).ThenBy(x => x.Id).ToList();
        var got = read.Resources.OrderBy(x => x.Type.Value).ThenBy(x => x.Id).ToList();
        if (expected.Count != got.Count || expected.Zip(got).Any(p => p.First.Type != p.Second.Type || p.First.Id != p.Second.Id
            || !p.First.GetData().Span.SequenceEqual(p.Second.GetData().Span) || p.First.Attributes != p.Second.Attributes
            || !NameBytes(p.First).AsSpan().SequenceEqual(NameBytes(p.Second))))
        {
            throw Bad($"The resource fork does not come back through {through}.");
        }
    }

    private static byte[] NameBytes(Resource resource) => resource.Name is { } name ? name.Bytes.ToArray() : [];

    private static void Codec(string name, byte[] packed, byte[] data, Func<byte[], byte[], bool> unpack)
    {
        var output = new byte[data.Length];
        if (!unpack(packed, output) || !output.AsSpan().SequenceEqual(data))
        {
            throw Bad($"{name} does not give back what it packed: {Convert.ToHexString(data)}");
        }
    }

    // ---- pict-write ----

    // A bitmap the input makes (in colours the chosen pixel format holds) through PictWriter and back, every pixel the
    // same; and drawing the input chooses, on both QuickDraws, recorded and played back: the picture draws what the
    // port drew.
    public static void PictWrite(ReadOnlyMemory<byte> input)
    {
        var r = new FuzzReader(input);
        int width = 1 + r.Int(40), height = 1 + r.Int(40);
        var formats = Enum.GetValues<PictPixelFormat>();
        var format = formats[r.Int(formats.Length)];
        var bits = format switch
        {
            PictPixelFormat.Indexed1 => 1,
            PictPixelFormat.Indexed2 => 2,
            PictPixelFormat.Indexed4 => 4,
            PictPixelFormat.Indexed8 => 8,
            _ => 0,
        };
        var palette = bits == 0 ? null
            : Enumerable.Range(0, 1 + r.Int(1 << bits)).Select(_ => new RgbaColor(r.Byte(), r.Byte(), r.Byte(), 255)).Distinct().ToList();
        var bitmap = new RgbaBitmap(width, height);
        for (var i = 0; i < width * height; i++)
        {
            var c = palette is not null ? palette[r.Int(palette.Count)]
                : format == PictPixelFormat.Rgb555 ? new RgbaColor(Five(r.Byte()), Five(r.Byte()), Five(r.Byte()), 255)
                : new RgbaColor(r.Byte(), r.Byte(), r.Byte(), format == PictPixelFormat.Argb8888 ? r.Byte() : (byte)255);
            (bitmap.Pixels[4 * i], bitmap.Pixels[4 * i + 1], bitmap.Pixels[4 * i + 2], bitmap.Pixels[4 * i + 3]) = (c.R, c.G, c.B, c.A);
        }

        using var stream = new MemoryStream();
        PictWriter.Write(stream, bitmap, new PictWriteOptions { Format = format, Palette = palette });
        var back = PictReader.Decode(stream.ToArray(), new PictDecodeOptions { PreserveAlpha = true });
        if (!back.Pixels.AsSpan().SequenceEqual(bitmap.Pixels))
        {
            throw Bad($"A {format} picture does not draw the bitmap it was written from.");
        }

        var script = r.Bytes(200);
        foreach (var version in new[] { QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom })
        {
            var direct = new QuickDrawPort(new RgbaBitmap(64, 64), new QuickDrawOptions { Version = version });
            Draw(direct, new FuzzReader(script));
            var recording = new QuickDrawPort(new RgbaBitmap(64, 64), new QuickDrawOptions { Version = version });
            var picture = PictureRecorder.OpenCPicture(recording, new MacRect(0, 0, 64, 64));
            Draw(recording, new FuzzReader(script));
            var played = PictReader.Decode(picture.ClosePicture(), new PictDecodeOptions { QuickDraw = version });
            if (!played.Pixels.AsSpan().SequenceEqual(direct.Canvas.Pixels))
            {
                throw Bad($"A recorded picture does not draw what the port drew ({version}).");
            }
        }
    }

    // A 5-bit channel as the reader widens it: the bits repeated into the low ones.
    private static byte Five(byte b) => (byte)((b & 0xF8) | (b >> 5));

    private static void Draw(QuickDrawPort p, FuzzReader r)
    {
        MacRect Rect()
        {
            int top = r.Int(70) - 3, left = r.Int(70) - 3;
            return new MacRect((short)top, (short)left, (short)(top + r.Int(40)), (short)(left + r.Int(40)));
        }

        QuickDrawPattern Pattern() => r.Int(5) switch
        {
            0 => QuickDrawPattern.Black,
            1 => QuickDrawPattern.Gray,
            2 => QuickDrawPattern.LightGray,
            3 => QuickDrawPattern.DarkGray,
            _ => QuickDrawPattern.White,
        };

        while (!r.Done)
        {
            switch (r.Int(14))
            {
                case 0:
                    p.ForeColor = new RgbColor((ushort)(r.Byte() * 257), (ushort)(r.Byte() * 257), (ushort)(r.Byte() * 257));
                    break;
                case 1:
                    p.BackColor = new RgbColor((ushort)(r.Byte() * 257), (ushort)(r.Byte() * 257), (ushort)(r.Byte() * 257));
                    break;
                case 2:
                    p.PenSize = new MacPoint((short)(1 + r.Int(4)), (short)(1 + r.Int(4)));
                    break;
                case 3:
                    p.PenPattern = Pattern();
                    break;
                case 4:
                    p.PaintRect(Rect());
                    break;
                case 5:
                    p.FrameRect(Rect());
                    break;
                case 6:
                    p.PaintOval(Rect());
                    break;
                case 7:
                    p.FrameOval(Rect());
                    break;
                case 8:
                    p.PaintRoundRect(Rect(), (short)r.Int(16), (short)r.Int(16));
                    break;
                case 9:
                    p.BackPattern = Pattern();
                    p.EraseArc(Rect(), (short)r.Int(360), (short)(r.Int(720) - 360));
                    break;
                case 10:
                    p.InvertPoly([new MacPoint((short)r.Int(64), (short)r.Int(64)), new MacPoint((short)r.Int(64), (short)r.Int(64)), new MacPoint((short)r.Int(64), (short)r.Int(64))]);
                    break;
                case 11:
                    p.FillRgn(Region.FromRect(new MacRect((short)r.Int(64), (short)r.Int(64), (short)r.Int(64), (short)r.Int(64))), Pattern());
                    break;
                case 12:
                    p.MoveTo((short)r.Int(64), (short)r.Int(64));
                    p.LineTo((short)r.Int(64), (short)r.Int(64));
                    break;
                default:
                    var image = Enumerable.Range(0, 4 * 16).Select(_ => r.Byte()).ToArray();
                    p.CopyBits(PixMap.FromBitMap(image, 4, new MacRect(0, 0, 16, 32)), new MacRect(0, 0, 16, 32), Rect(), (TransferMode)r.Int(8));
                    break;
            }
        }
    }
}
