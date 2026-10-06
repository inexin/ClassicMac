using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace Fuzz;

/// <summary>
/// The edit session's target (volume-session): HFS volumes as the app and the CLI edit them, plain, in a partition map
/// (one partition, or two each named in its paths) or in a Disk Copy 4.2 image, through every edit the session makes.
/// After each edit, made or refused, the input as it stands (<see cref="InputEditSession.Current"/>) reads cleanly, each
/// volume passes the writer's checks and First Aid and holds exactly what a model of the edits holds, and the bytes
/// around the volumes are the input's (a Disk Copy image's data checksum made again).
/// </summary>
internal static class SessionTargets
{
    private static InvalidOperationException Bad(string what) => new(what);

    // A file in the model: its data, its resources by type and ID, its type and creator, whether it is locked.
    private sealed class Item
    {
        public byte[] Data = [];
        public SortedDictionary<(uint Type, short Id), byte[]> Resources = [];
        public FourCC Type;
        public FourCC Creator;
        public bool Locked;
    }

    // One volume of the input: the name its session paths start with ("" when it is the only one), its files and folders.
    private sealed class Model(string prefix)
    {
        public string Prefix { get; } = prefix;
        public Dictionary<string, Item> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal) { "" };
    }

    private enum Layout
    {
        Plain,
        OnePartition,
        TwoPartitions,
        DiskCopy42,
    }

    private static readonly byte[] Driver = [.. Enumerable.Range(0, 1024).Select(i => (byte)(i * 7))];

    public static void VolumeSession(ReadOnlyMemory<byte> input)
    {
        var r = new FuzzReader(input);
        var date = new MacDate(3_000_000_000);
        var layout = (Layout)r.Int(4);
        byte[] Volume(string name) => HfsWriter.Format((1 + r.Int(3)) * 400 * 1024, name, date);
        var (bytes, models) = layout switch
        {
            Layout.Plain => (Volume("Fuzz"), new[] { new Model("") }),
            Layout.OnePartition => (Fixtures.PartitionMap(("Driver", "Apple_Driver43", Driver), ("Macintosh HD", "Apple_HFS", Volume("HD"))), new[] { new Model("") }),
            Layout.TwoPartitions => (Fixtures.PartitionMap(("One", "Apple_HFS", Volume("One")), ("Two", "Apple_HFS", Volume("Two"))), new[] { new Model("One"), new Model("Two") }),
            _ => (Fixtures.DiskCopy42("Fuzz", Volume("Fuzz")), new[] { new Model("") }),
        };

        var host = new HostFile(new MacFile { Name = MacString.FromMacRoman("fuzz.img"), DataFork = ForkData.FromBytes(bytes) }, HostLayout.Plain, []);
        var root = ContainerUnwrapper.Default.Unwrap(host.File, "host file", new ContainerContext());
        var session = InputEditSession.Open("fuzz.img", host, root);
        if (session.Kind != InputEditKind.HfsVolume)
        {
            throw Bad($"A {layout} input opens as {session.Kind}, not an HFS volume.");
        }

        for (var step = 0; step < 16 && !r.Done; step++)
        {
            var model = models[r.Int(models.Length)];
            try
            {
                Edit(r, session, model, date);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                WriterTargets.Trace?.Invoke($"refused: {e.Message}");      // refused: neither the session nor the model changed
            }

            Verify(session, bytes, layout, models);
        }
    }

    private static void Edit(FuzzReader r, InputEditSession session, Model model, MacDate date)
    {
        string Pick(IEnumerable<string> from)
        {
            var list = from.Order(StringComparer.Ordinal).ToList();
            return list.Count == 0 ? "" : list[r.Int(list.Count)];
        }

        string Join(string folder, string name) => folder.Length == 0 ? name : folder + ":" + name;
        string Name()
        {
            const string Letters = "abcdefgh0123 .é";
            if (r.Int(8) == 0)
            {
                return "System";                                                // what a blessed folder must hold
            }

            return new string([.. Enumerable.Range(0, 1 + r.Int(8)).Select(_ => Letters[r.Int(Letters.Length)])]);
        }

        // The session's path for a path in the volume (on a disk of two partitions, after the partition's name).
        string At(string path) => model.Prefix.Length == 0 ? path : path.Length == 0 ? model.Prefix : model.Prefix + ":" + path;
        string Item() => Pick(model.Files.Keys.Concat(model.Folders.Where(f => f.Length > 0)));
        // A few codes, so they repeat, and the System file's type a blessed folder needs.
        FourCC Code() => r.Int(8) == 0 ? FourCC.FromString("zsys") : new((uint)(0x20202020 + r.Int(4) * 0x01000000 + r.Int(4)));

        void Moved(string from, string to)
        {
            if (from == to)
            {
                return;
            }

            foreach (var key in model.Files.Keys.Where(k => k == from || k.StartsWith(from + ":", StringComparison.Ordinal)).ToList())
            {
                model.Files[to + key[from.Length..]] = model.Files[key];
                model.Files.Remove(key);
            }

            foreach (var key in model.Folders.Where(k => k == from || k.StartsWith(from + ":", StringComparison.Ordinal)).ToList())
            {
                model.Folders.Remove(key);
                model.Folders.Add(to + key[from.Length..]);
            }
        }

        void Trace(string what) => WriterTargets.Trace?.Invoke(model.Prefix.Length == 0 ? what : $"[{model.Prefix}] {what}");

        switch (r.Int(11))
        {
            case 0:
                {
                    var path = Join(Pick(model.Folders), Name());
                    Trace($"mkdir {path}");
                    session.AddFolder(At(path));
                    model.Folders.Add(path);
                    return;
                }
            case 1:
                {
                    var path = Join(Pick(model.Folders), Name());
                    var item = new Item { Data = r.Content(r.Int(30_000)), Type = Code(), Creator = Code() };
                    var fork = new ResourceFork();
                    for (var n = r.Int(4); n > 0; n--)
                    {
                        var (type, id) = (Code(), (short)(128 + r.Int(4)));
                        if (fork.Find(type, id) is null)
                        {
                            var data = r.Content(r.Int(2000));
                            fork.Add(new Resource(type, id, data));
                            item.Resources[(type.Value, id)] = data;
                        }
                    }

                    Trace($"create {path} ({item.Data.Length}, {item.Resources.Count} resources)");
                    session.AddFile(At(path), new MacFile
                    {
                        Name = MacString.FromMacRoman(path[(path.LastIndexOf(':') + 1)..]),
                        DataFork = ForkData.FromBytes(item.Data),
                        ResourceFork = ForkData.FromBytes(item.Resources.Count == 0 ? [] : fork.ToArray()),
                        FinderInfo = FinderInfo.Empty with { Type = item.Type, Creator = item.Creator },
                        Created = date,
                        Modified = date,
                    });
                    model.Files[path] = item;
                    return;
                }
            case 2:
                {
                    var path = Item();
                    var recursive = r.Bool();
                    Trace($"delete {path}{(recursive ? " with everything in it" : "")}");
                    session.Delete(At(path), recursive);
                    Moved(path, "\0gone");
                    model.Files.Remove("\0gone");
                    foreach (var gone in model.Files.Keys.Where(k => k.StartsWith("\0gone:", StringComparison.Ordinal)).ToList())
                    {
                        model.Files.Remove(gone);
                    }

                    model.Folders.RemoveWhere(f => f == "\0gone" || f.StartsWith("\0gone:", StringComparison.Ordinal));
                    return;
                }
            case 3:
                {
                    var path = Item();
                    var name = Name();
                    Trace($"rename {path} to {name}");
                    session.Rename(At(path), name);
                    Moved(path, Join(path.Contains(':') ? path[..path.LastIndexOf(':')] : "", name));
                    return;
                }
            case 4:
                {
                    var path = Item();
                    var into = Pick(model.Folders);
                    Trace($"move {path} into [{into}]");
                    session.Move(At(path), At(into));
                    Moved(path, Join(into, path[(path.LastIndexOf(':') + 1)..]));
                    return;
                }
            case 5:
                {
                    var path = Pick(model.Files.Keys);
                    var locked = r.Bool();
                    Trace($"{(locked ? "lock" : "unlock")} {path}");
                    session.SetLocked(At(path), locked);
                    model.Files[path].Locked = locked;
                    return;
                }
            case 6:
                {
                    var path = Pick(model.Files.Keys);
                    var (type, creator) = (Code(), Code());
                    Trace($"set {path} to {type}/{creator}");
                    session.SetInfo(At(path), type, creator);
                    (model.Files[path].Type, model.Files[path].Creator) = (type, creator);
                    return;
                }
            case 7:
                {
                    var path = Pick(model.Folders.Where(f => f.Length > 0));
                    var flags = (FinderFlags)(r.Int(2) == 0 ? 0 : 0x4000);
                    Trace($"set folder {path} flags {flags}");
                    session.SetInfo(At(path), flags: flags);
                    return;
                }
            case 8:
                {
                    var path = Pick(model.Folders.Where(f => f.Length > 0));
                    Trace($"bless [{path}]");
                    session.Bless(At(path));
                    return;
                }
            case 9:
                {
                    var path = Pick(model.Files.Keys);
                    var (type, id) = (Code(), (short)(128 + r.Int(4)));
                    var data = r.Content(r.Int(2000));
                    Trace($"set resource {path} '{type}' {id} ({data.Length})");
                    session.SetResource(At(path), type, id, data);
                    model.Files[path].Resources[(type.Value, id)] = data;
                    return;
                }
            default:
                {
                    var path = Pick(model.Files.Keys);
                    var (type, id) = model.Files.TryGetValue(path, out var item) && item.Resources.Count > 0
                        ? item.Resources.Keys.ElementAt(r.Int(item.Resources.Count)) : (Code().Value, (short)128);
                    Trace($"delete resource {path} '{new FourCC(type)}' {id}");
                    session.DeleteResource(At(path), new FourCC(type), id);
                    model.Files[path].Resources.Remove((type, id));
                    return;
                }
        }
    }

    private static void Verify(InputEditSession session, byte[] original, Layout layout, Model[] models)
    {
        var current = session.Current() ?? throw Bad("An edited volume's session has no input as it stands.");
        var whole = current.File.DataFork.ToArray();
        if (whole.Length != original.Length)
        {
            throw Bad($"The input changed length, {original.Length} to {whole.Length}.");
        }

        var diagnostics = new List<Diagnostic>();
        _ = ContainerUnwrapper.Default.Unwrap(current.File, "host file", new ContainerContext(null, diagnostics));
        FuzzTargets.ThrowOnFault(diagnostics);

        // Each volume where it lies, and the bytes around them unchanged.
        List<(long Offset, long Length)> places = layout switch
        {
            Layout.Plain => [(0, whole.Length)],
            Layout.DiskCopy42 => [(84, new BigEndianReader(whole).ReadUInt32At(0x40))],
            _ => [.. PartitionMapReader.Partitions(ForkData.FromBytes(original)).Select(p => (p.Offset, p.Length))],
        };
        if (places.Count != models.Length)
        {
            throw Bad($"The input has {places.Count} volumes; it was made with {models.Length}.");
        }

        var outside = whole.ToArray();
        var before = original.ToArray();
        foreach (var (offset, length) in places)
        {
            outside.AsSpan((int)offset, (int)length).Clear();
            before.AsSpan((int)offset, (int)length).Clear();
        }

        if (layout == Layout.DiskCopy42)
        {
            var disk = ForkData.FromBytes(whole.AsMemory(84, (int)places[0].Length).ToArray());
            if (new BigEndianReader(whole).ReadUInt32At(0x48) != DiskCopy42Reader.Sum(disk))
            {
                throw Bad("The Disk Copy image's data checksum is not its disk's.");
            }

            outside.AsSpan(0x48, 4).Clear();
            before.AsSpan(0x48, 4).Clear();
        }

        if (!outside.AsSpan().SequenceEqual(before))
        {
            throw Bad("Bytes outside the volumes changed.");
        }

        for (var i = 0; i < models.Length; i++)
        {
            VerifyVolume(ForkData.FromBytes(whole.AsMemory((int)places[i].Offset, (int)places[i].Length).ToArray()), models[i]);
        }
    }

    private static void VerifyVolume(ForkData image, Model model)
    {
        if (HfsWriter.Check(image) is { } fault)
        {
            throw Bad($"The session's volume fails the writer's checks: {fault}");
        }

        if (HfsFirstAid.Verify(image) is { Verdict: not FirstAidVerdict.AppearsOk } report)
        {
            throw Bad($"The session's volume fails First Aid: {string.Join("; ", report.Problems.Select(p => p.Message))}");
        }

        var read = HfsReader.Instance.Read(image, new ContainerContext());
        string PathOf(IEnumerable<MacString> names) => string.Join(":", names.Select(n => n.ToMacRoman()));
        if (read.Count != model.Files.Count)
        {
            throw Bad($"The volume holds {read.Count} files; the edits made {model.Files.Count}.");
        }

        foreach (var file in read)
        {
            var path = PathOf(file.FolderPath.Append(file.Name));
            if (!model.Files.TryGetValue(path, out var item) || !file.DataFork.ToArray().AsSpan().SequenceEqual(item.Data))
            {
                throw Bad($"{path} is not what the edits wrote.");
            }

            if ((file.FinderInfo.Type, file.FinderInfo.Creator, file.IsLocked) != (item.Type, item.Creator, item.Locked))
            {
                throw Bad($"{path} is {file.FinderInfo.Type}/{file.FinderInfo.Creator}{(file.IsLocked ? ", locked" : "")}; the edits made it {item.Type}/{item.Creator}{(item.Locked ? ", locked" : "")}.");
            }

            var bytes = file.ResourceFork.ToArray();
            var resources = bytes.Length == 0 ? [] : ResourceFork.Read(bytes).Resources.ToDictionary(x => (x.Type.Value, x.Id), x => x.GetData().ToArray());
            if (resources.Count != item.Resources.Count
                || item.Resources.Any(e => !resources.TryGetValue(e.Key, out var data) || !data.AsSpan().SequenceEqual(e.Value)))
            {
                throw Bad($"{path}'s resources are not the ones the edits made.");
            }
        }

        var folders = HfsReader.Instance.ReadFolders(image, new ContainerContext()).Where(f => !f.IsRoot).Select(f => PathOf(f.Path)).ToHashSet(StringComparer.Ordinal);
        if (!folders.SetEquals(model.Folders.Where(f => f.Length > 0)))
        {
            throw Bad("The volume's folders are not the ones the edits made.");
        }
    }
}
