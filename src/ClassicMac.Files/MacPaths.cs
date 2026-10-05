using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files;

/// <summary>What a Mac path names.</summary>
public enum MacPathKind
{
    /// <summary>A folder of a volume or archive.</summary>
    Folder,

    /// <summary>A file that holds no other files.</summary>
    File,

    /// <summary>A file whose data fork holds files (a disk image, an archive, a MacBinary file…), entered as a folder.</summary>
    Container,

    /// <summary>A file's resource fork (<c>#rsrc</c>).</summary>
    ResourceFork,

    /// <summary>The resources of one type in a fork (<c>'TYPE'</c>).</summary>
    ResourceType,

    /// <summary>One resource (its ID).</summary>
    Resource,
}

/// <summary>
/// The syntax of Mac paths (docs/cli.md §1): the host file, then Mac names joined by ':' or '/'; a backslash escapes
/// ':', '/' and itself in a name; <c>#rsrc</c> after a file is its resource fork, then <c>'TYPE'</c> (quoted, or four
/// characters bare) and the resource's ID. Names compare as an HFS catalog compares them.
/// </summary>
public static class MacPaths
{
    /// <summary>The name that stands for a file's resource fork in a path.</summary>
    public const string ResourceFork = "#rsrc";

    /// <summary>
    /// The host file a path starts with, and the rest: the shortest part of the path, ending before a ':', '/' or
    /// '\', that is an existing file (or the whole path); null when there is none.
    /// </summary>
    public static (string Host, string MacPath)? SplitHost(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        for (var i = 1; i < path.Length; i++)
        {
            if (path[i] is ':' or '/' or '\\' && File.Exists(path[..i]))
            {
                return (path[..i], path[(i + 1)..]);
            }
        }

        return File.Exists(path) ? (path, "") : null;
    }

    /// <summary>
    /// The names of a path after its host file: split at ':' and '/' (empty names skipped), a backslash before ':', '/'
    /// or a backslash taking that character as part of the name (before anything else, it is itself), and a name that
    /// starts with a quote running to the next quote whatever it holds (a resource type).
    /// </summary>
    public static IReadOnlyList<string> Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var names = new List<string>();
        var name = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\'' && name.Length == 0 && path.IndexOf('\'', i + 1) is > 0 and var close)
            {
                name.Append(path, i, close - i + 1);
                i = close;
            }
            else if (c == '\\' && i + 1 < path.Length && path[i + 1] is ':' or '/' or '\\')
            {
                name.Append(path[++i]);
            }
            else if (c is ':' or '/')
            {
                Flush();
            }
            else
            {
                name.Append(c);
            }
        }

        Flush();
        return names;

        void Flush()
        {
            if (name.Length > 0)
            {
                names.Add(name.ToString());
                name.Clear();
            }
        }
    }

    /// <summary>A name as it is written in a path: '\', ':' and '/' escaped with a backslash.</summary>
    public static string Escape(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal).Replace("/", "\\/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether two names are the same name on a Mac volume: the HFS catalog's comparison when both are Mac OS Roman
    /// (ignoring case, not diacritics: <i>Inside Macintosh: Text</i>, RelString; docs/formats/file-systems/hfs.md §1.11),
    /// else ignoring case.
    /// </summary>
    public static bool NamesEqual(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return MacRoman.TryEncode(left, out var a) && MacRoman.TryEncode(right, out var b)
            ? HfsCatalogKeys.CatalogNamesEqual(a, b)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A file, folder, container, resource fork, resource type or resource reached by a Mac path.</summary>
public sealed class MacPathEntry
{
    internal List<MacPathEntry>? children;

    internal MacPathEntry(MacPathKind kind, string name, string path, MacPathEntry? parent)
    {
        Kind = kind;
        Name = name;
        Path = path;
        Parent = parent;
    }

    /// <summary>What it is.</summary>
    public MacPathKind Kind { get; }

    /// <summary>Its name: the file's or folder's, <c>#rsrc</c>, <c>'TYPE'</c>, or the resource's ID.</summary>
    public string Name { get; }

    /// <summary>Its full path: the host file, then the escaped names joined by ':'.</summary>
    public string Path { get; }

    /// <summary>The entry above it; null for the host file.</summary>
    public MacPathEntry? Parent { get; }

    /// <summary>The file, for a file or container (and a fork's file for a fork, type or resource).</summary>
    public MacFile? File { get; internal init; }

    /// <summary>For a container, the format of what it holds ("HFS volume", "MacBinary II"…); null otherwise.</summary>
    public string? Format { get; internal set; }

    /// <summary>The format the file was found in (the reader that read it, or the host layout); null for others.</summary>
    public string? FoundIn { get; internal init; }

    /// <summary>For a folder, its folder path in the container holding it.</summary>
    public IReadOnlyList<MacString>? FolderPath { get; internal init; }

    /// <summary>The resource fork, for a fork, a type or a resource.</summary>
    public ResourceFork? Resources { get; internal set; }

    /// <summary>Where the fork came from (the resource fork, an AppleDouble file, the data fork…), for a fork.</summary>
    public ResourceForkSource? ResourcesSource { get; internal set; }

    /// <summary>The type, for a type or a resource.</summary>
    public FourCC? ResourceType { get; internal init; }

    /// <summary>The resource, for a resource.</summary>
    public Resource? Resource { get; internal init; }

    // The container tree node of a file or container (replaced by the read one when a container is entered).
    internal ContainerNode? Node { get; set; }

    // The container that holds a folder, file or container (its contents' node).
    internal MacPathEntry? Holder { get; init; }

    // A container's read contents (past a wrapper of one container); set when it is first entered.
    internal ContainerNode? Contents { get; set; }

    // A container's volume folder records, once read.
    internal IReadOnlyList<MacFolder>? Folders { get; set; }

    // A container's volume for resolving aliases, once made.
    internal AliasVolume? AliasVolume { get; set; }

    /// <summary>For a folder, the volume's record of it (its dates and Finder info), when the volume keeps folder records.</summary>
    public MacFolder? Folder { get; internal init; }

    // The name of the container a wrapper holds, which a path may give or leave out.
    internal string? WrappedName { get; set; }

    /// <summary>Its path.</summary>
    public override string ToString() => Path;
}

/// <summary>
/// A host file opened for Mac paths (docs/cli.md §1): its contents as folders and files, containers entered as
/// folders and read only when entered (one level at a time, through <see cref="ContainerUnwrapper"/>), and files'
/// resource forks. Entries are made once: resolving the same path twice gives the same entry.
/// </summary>
public sealed class MacPathTree : IDisposable
{
    private readonly ContainerUnwrapper unwrapper;
    private readonly ContainerContext context;
    private readonly ReadOptions? readOptions;
    private readonly bool rawHost;

    private MacPathTree(string hostPath, ContainerUnwrapper unwrapper, ContainerContext context, ReadOptions? readOptions, HostFile? given = null)
    {
        this.unwrapper = unwrapper;
        this.context = context;
        this.readOptions = readOptions;
        var host = given ?? HostFiles.Read(hostPath, context.Options, context.Diagnostics);
        rawHost = host.Layout == HostLayout.Plain;
        HostLayout = host.Layout;
        var node = unwrapper.Unwrap(host.File, HostFiles.FormatName(host.Layout),
            context.For(null, HostFiles.Siblings(hostPath, context.Options, context.Diagnostics)), levels: 1);
        Root = new MacPathEntry(IsContainer(node) ? MacPathKind.Container : MacPathKind.File, System.IO.Path.GetFileName(hostPath), hostPath, null)
        {
            File = node.File,
            Node = node,
            FoundIn = node.Format,
        };
        Root.Format = ContentFormat(node);
    }

    /// <summary>The host file.</summary>
    public MacPathEntry Root { get; }

    private readonly Dictionary<ContainerNode, MacTextEncoding?> systemEncodings = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The encoding the volume says the file an entry is (or is in) has its text in (text-encodings.md §5): the file's
    /// own <see cref="MacFile.TextEncoding"/>, else its volume's System file's region (<see cref="FileEncodings.OfSystem"/>,
    /// read once per volume); null when neither says anything, or the entry is on no volume.
    /// </summary>
    public MacTextEncoding? EncodingOf(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var file = entry;
        while (file is not null && file.File is null)
        {
            file = file.Parent;
        }

        if (file?.File?.TextEncoding is { } own)
        {
            return own;
        }

        if (file?.Holder?.Contents is not { Volume: { } volume } contents)
        {
            return null;
        }

        if (!systemEncodings.TryGetValue(contents, out var system))
        {
            systemEncodings[contents] = system = FileEncodings.OfSystem(contents.Children.Select(c => c.File), volume.BlessedFolderId);
        }

        return system;
    }

    /// <summary>How the host file was stored (plain, with an AppleDouble file, MacBinary…).</summary>
    public HostLayout HostLayout { get; }

    /// <summary>The problems found reading the host file and what it holds.</summary>
    public ICollection<Diagnostic> Diagnostics => context.Diagnostics;

    /// <summary>Opens a host file.</summary>
    public static MacPathTree Open(string hostPath, ContainerReadOptions? options = null, ReadOptions? readOptions = null,
        ICollection<Diagnostic>? diagnostics = null, ContainerUnwrapper? unwrapper = null)
    {
        ArgumentNullException.ThrowIfNull(hostPath);
        return new MacPathTree(hostPath, unwrapper ?? ContainerUnwrapper.Default, new ContainerContext(options, diagnostics), readOptions);
    }

    /// <summary>
    /// Opens a host file already read, or made in memory (an edited volume), under its path: the tree reads
    /// <paramref name="host"/> and names entries by <paramref name="hostPath"/>.
    /// </summary>
    public static MacPathTree Open(string hostPath, HostFile host, ContainerReadOptions? options = null, ReadOptions? readOptions = null,
        ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(hostPath);
        ArgumentNullException.ThrowIfNull(host);
        return new MacPathTree(hostPath, ContainerUnwrapper.Default, new ContainerContext(options, diagnostics), readOptions, host);
    }

    /// <summary>
    /// Opens the host file a full path starts with and resolves the rest (<paramref name="entry"/> null when it names
    /// nothing); null when the path starts with no host file.
    /// </summary>
    public static MacPathTree? OpenPath(string path, out MacPathEntry? entry, ContainerReadOptions? options = null, ReadOptions? readOptions = null,
        ICollection<Diagnostic>? diagnostics = null)
    {
        entry = null;
        if (MacPaths.SplitHost(path) is not var (host, rest))
        {
            return null;
        }

        var tree = Open(host, options, readOptions, diagnostics);
        entry = tree.Resolve(rest);
        return tree;
    }

    /// <summary>The entry a path after the host file names (the root for an empty path); null when it names nothing.</summary>
    public MacPathEntry? Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var at = Root;
        foreach (var name in MacPaths.Split(path))
        {
            if (Child(at, name) is not { } next)
            {
                return null;
            }

            at = next;
        }

        return at;
    }

    /// <summary>The entry above (null for the root).</summary>
    public MacPathEntry? Parent(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Parent;
    }

    /// <summary>The volume a container entry is (its name, space, counts and locks), or null when it is no volume.</summary>
    public VolumeInfo? VolumeInfoOf(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == MacPathKind.Container ? ContentsOf(entry).Volume : null;
    }

    /// <summary>Where an HFS volume entry's free space and files lie (hfs.md §5.7); null for other entries and volumes.</summary>
    public VolumeLayout? LayoutOf(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return VolumeInfoOf(entry) is { Format: "HFS" } ? Hfs.HfsReader.Instance.ReadLayout(ContentsOf(entry).File.DataFork) : null;
    }

    /// <summary>The path inside a volume entry of its folder with this catalog ID (colon-separated), or null.</summary>
    public string? FolderPathOf(MacPathEntry volume, uint catalogId)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.Kind != MacPathKind.Container)
        {
            return null;
        }

        ContentsOf(volume);
        return FoldersOf(volume).FirstOrDefault(f => f.CatalogId == catalogId) is { } folder
            ? string.Join(":", folder.Path.Select(n => n.ToString()))
            : null;
    }

    /// <summary>
    /// What an entry holds: a container's or folder's folders and files, in the container's order; a file's resource
    /// fork (when it has one); a fork's types; a type's resources.
    /// </summary>
    public IReadOnlyList<MacPathEntry> Children(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.children ??= entry.Kind switch
        {
            MacPathKind.Container => Contents(entry, ContentsOf(entry), []),
            MacPathKind.Folder => Contents(entry, entry.Holder!.Contents!, entry.FolderPath!),
            MacPathKind.File => ForkOf(entry) is { } fork ? [fork] : [],
            MacPathKind.ResourceFork => [.. entry.Resources!.Types.Select(type => Type(entry, type))],
            MacPathKind.ResourceType => [.. entry.Resources!.OfType(entry.ResourceType!.Value).Select(r => ResourceEntry(entry, r))],
            _ => [],
        };
    }

    /// <summary>
    /// An alias file's record resolved on the volume holding it (docs/formats/resources/aliases.md §2); null when the
    /// entry is not an alias file (the isAlias flag) or has no readable <c>'alis'</c>.
    /// </summary>
    public AliasResolution? ResolveAlias(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.File is not { } file || entry.Kind is not (MacPathKind.File or MacPathKind.Container) || !AliasResolver.IsAlias(file)
            || AliasResolver.ReadAlias(file, readOptions) is not { } alias)
        {
            return null;
        }

        return AliasResolver.Resolve(alias, entry.Holder is { } holder && VolumeOf(holder) is { } volume ? [volume] : [], file);
    }

    /// <summary>
    /// The alias files on the volume holding <paramref name="entry"/> (a file or folder) whose original is that entry or
    /// inside it, and which are not inside it themselves: those that would no longer find their original if it were
    /// deleted (docs/formats/resources/aliases.md §5). Paths are inside the volume, colon-separated.
    /// </summary>
    public IReadOnlyList<(string Alias, string Original)> AliasesTo(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind is not (MacPathKind.Folder or MacPathKind.File or MacPathKind.Container) || entry.Holder is not { } holder ||
            VolumeOf(holder) is not { } volume)
        {
            return [];
        }

        IReadOnlyList<MacString> target = entry.Kind == MacPathKind.Folder ? entry.FolderPath! : [.. entry.File!.FolderPath, entry.File.Name];
        bool Within(IReadOnlyList<MacString> path) => path.Count >= target.Count &&
            target.Select((name, i) => MacPaths.NamesEqual(Text(name), Text(path[i]))).All(same => same);
        string Join(IEnumerable<MacString> path) => string.Join(":", path.Select(Text));

        var found = new List<(string, string)>();
        foreach (var file in ContentsOf(holder).Children.Select(node => node.File))
        {
            if (!AliasResolver.IsAlias(file) || Within([.. file.FolderPath, file.Name]) || AliasResolver.ReadAlias(file, readOptions) is not { } alias)
            {
                continue;
            }

            var resolution = AliasResolver.Resolve(alias, [volume], file);
            IReadOnlyList<MacString> original = resolution.File is { } to ? [.. to.FolderPath, to.Name] : resolution.Folder?.Path ?? [];
            if (resolution.Found && Within(original))
            {
                found.Add((Join([.. file.FolderPath, file.Name]), Join(original)));
            }
        }

        return found;
    }

    /// <summary>The entry of a resolved alias's target in this tree; null when it was not found here.</summary>
    public MacPathEntry? TargetOf(AliasResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Volume?.Tag is not MacPathEntry holder)
        {
            return null;
        }

        var names = resolution.File is { } file ? [.. file.FolderPath, file.Name] : resolution.Folder?.Path ?? [];
        var at = holder;
        foreach (var name in names)
        {
            if (Children(at).FirstOrDefault(c => c.Kind is MacPathKind.Folder or MacPathKind.File or MacPathKind.Container
                && MacPaths.NamesEqual(c.Name, Text(name))) is not { } next)
            {
                return null;
            }

            at = next;
        }

        return at;
    }

    /// <summary>
    /// The entry an alias file leads to, through aliases of aliases (at most ten, as ResolveAliasFile follows them); the
    /// entry itself when it is no alias; null when an original is not found.
    /// </summary>
    public MacPathEntry? FollowAlias(MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var at = entry;
        for (var hop = 0; hop < 10; hop++)
        {
            if (ResolveAlias(at) is not { } resolution)
            {
                return at;
            }

            if (!resolution.Found || TargetOf(resolution) is not { } next)
            {
                return null;
            }

            at = next;
        }

        return ResolveAlias(at) is null ? at : null;
    }

    // The volume a container holds, for aliases (read once): its files, folder records and creation date.
    private AliasVolume? VolumeOf(MacPathEntry holder)
    {
        if (holder.AliasVolume is { } read)
        {
            return read;
        }

        var folders = FoldersOf(holder);
        if (!folders.Any(f => f.IsRoot) || holder.Contents is not { } contents)
        {
            return null;
        }

        return holder.AliasVolume = new AliasVolume([.. contents.Children.Select(c => c.File)], folders, holder,
            HfsReader.Instance.ReadVolumeInfo(contents.File.DataFork)?.Created);
    }

    /// <summary>Releases the tree.</summary>
    public void Dispose()
    {
        // The tree holds no handles of its own: forks open their streams when read.
    }

    // The child of an entry with a name: an exact match first, else one HFS takes for the same name; a container's
    // resource fork (#rsrc) when it has no item of that name; the name of the container a wrapper holds (the same
    // contents); for a fork, a type; for a type, an ID.
    private MacPathEntry? Child(MacPathEntry entry, string name)
    {
        switch (entry.Kind)
        {
            case MacPathKind.ResourceFork:
                var type = name.Length >= 2 && name[0] == '\'' && name[^1] == '\'' ? name[1..^1] : name;
                return type.Length == 4 && MacRoman.TryEncode(type, out _)
                    ? Children(entry).FirstOrDefault(c => c.ResourceType == FourCC.FromString(type))
                    : null;
            case MacPathKind.ResourceType:
                return short.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
                    ? Children(entry).FirstOrDefault(c => c.Resource!.Id == id)
                    : null;
            case MacPathKind.Resource:
                return null;
        }

        var children = entry.Kind == MacPathKind.File ? [] : Children(entry);
        var found = children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? children.FirstOrDefault(c => MacPaths.NamesEqual(c.Name, name));
        if (found is not null)
        {
            return found;
        }

        if (entry.Kind == MacPathKind.Container && entry.WrappedName is { } wrapped && MacPaths.NamesEqual(wrapped, name))
        {
            return entry;
        }

        return name == MacPaths.ResourceFork && entry.Kind is MacPathKind.File or MacPathKind.Container ? ForkOf(entry) : null;
    }

    // A container's contents, read when first entered: a level of what its data fork holds; a wrapper of one
    // container (a MacBinary file of a disk image, a disk image's disk) passes on to that container's contents.
    private ContainerNode ContentsOf(MacPathEntry container)
    {
        if (container.Contents is { } read)
        {
            return read;
        }

        var node = Read(container.Node!, container.Holder);
        container.Node = node;
        while (node.Children is [var only] && only.File.FolderPath.Count == 0 && IsContainer(only))
        {
            container.WrappedName ??= NameOf(only.File);
            node = only.UnreadFormat is not null ? unwrapper.Expand(only, context.For(null, () => []), levels: 1) : only;
        }

        container.Format = ContentFormat(node) ?? container.Format;
        return container.Contents = node;
    }

    // A container node with its unread level read, its siblings (the files beside it in the same folder) at hand for
    // formats split across files.
    private ContainerNode Read(ContainerNode node, MacPathEntry? holder)
    {
        if (node.UnreadFormat is null)
        {
            return node;
        }

        var file = node.File;
        var beside = holder?.Contents?.Children.Select(c => c.File).Where(f => !ReferenceEquals(f, file) && f.FolderPath.SequenceEqual(file.FolderPath)).ToList() ?? [];
        return unwrapper.Expand(node, context.For(null, () => beside), levels: 1);
    }

    // The folders and files directly in a folder (by folder path) of a container's contents.
    private List<MacPathEntry> Contents(MacPathEntry parent, ContainerNode contents, IReadOnlyList<MacString> folder)
    {
        var holder = parent.Kind == MacPathKind.Container ? parent : parent.Holder!;
        var entries = new List<MacPathEntry>();
        var folders = new HashSet<MacString>();
        foreach (var child in contents.Children)
        {
            var path = child.File.FolderPath;
            if (path.Count < folder.Count || !path.Take(folder.Count).SequenceEqual(folder))
            {
                continue;
            }

            if (path.Count == folder.Count)
            {
                var name = NameOf(child.File);
                var kind = IsContainer(child) ? MacPathKind.Container : MacPathKind.File;
                var entry = new MacPathEntry(kind, name, parent.Path + ":" + MacPaths.Escape(name), parent)
                {
                    File = child.File,
                    Node = child,
                    Holder = holder,
                    FoundIn = child.Format,
                };
                entry.Format = kind == MacPathKind.Container ? ContentFormat(child) : null;
                entries.Add(entry);
            }
            else if (folders.Add(path[folder.Count]))
            {
                var name = child.File.UnicodeFolderPath is { } unicode && unicode.Count == path.Count ? unicode[folder.Count] : Text(path[folder.Count]);
                entries.Add(FolderEntry(parent, holder, name, [.. folder, path[folder.Count]]));
            }
        }

        // The folders the volume's catalog records that hold no files (an empty folder has no file to show it).
        foreach (var record in FoldersOf(holder).Where(f => !f.IsRoot && f.FolderPath.SequenceEqual(folder) && !folders.Contains(f.Name)))
        {
            folders.Add(record.Name);
            entries.Add(FolderEntry(parent, holder, Text(record.Name), record.Path));
        }

        return entries;
    }

    private MacPathEntry FolderEntry(MacPathEntry parent, MacPathEntry holder, string name, IReadOnlyList<MacString> path) =>
        new(MacPathKind.Folder, name, parent.Path + ":" + MacPaths.Escape(name), parent)
        {
            Holder = holder,
            FolderPath = path,
            Folder = FoldersOf(holder).FirstOrDefault(f => !f.IsRoot && f.Path.SequenceEqual(path)),
        };

    // An HFS volume's folder records (names, dates, Finder info), read once per volume; none for other containers.
    private IReadOnlyList<MacFolder> FoldersOf(MacPathEntry holder)
    {
        if (holder.Folders is { } read)
        {
            return read;
        }

        var contents = holder.Contents!;
        IReadOnlyList<MacFolder> folders = [];
        if (contents.Children.Count > 0 && contents.Children[0].Format == HfsReader.Instance.FormatName || contents.Children.Count == 0 && holder.Format == HfsReader.Instance.FormatName)
        {
            try
            {
                folders = HfsReader.Instance.ReadFolders(contents.File.DataFork, context.For(null, () => []));
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                folders = [];
            }
        }

        return holder.Folders = folders;
    }

    // A file's resource fork entry, made once; null when it has none.
    private MacPathEntry? ForkOf(MacPathEntry file)
    {
        if (file.File is not { } macFile)
        {
            return null;
        }

        var existing = file.children?.FirstOrDefault(c => c.Kind == MacPathKind.ResourceFork);
        if (existing is not null)
        {
            return existing;
        }

        var read = macFile.ResourceFork.Length > 0 ? MacFileResources.Read(macFile, readOptions, context.Diagnostics)
            : ReferenceEquals(file, Root) && rawHost && MacFileResources.LooksLikeFork(macFile.DataFork)
                ? MacFileResources.ReadRaw(macFile.DataFork, readOptions, context.Diagnostics)
                : null;
        if (read?.Fork is not { } fork)
        {
            return null;
        }

        var entry = new MacPathEntry(MacPathKind.ResourceFork, MacPaths.ResourceFork, file.Path + ":" + MacPaths.ResourceFork, file)
        {
            File = macFile,
            Resources = fork,
            ResourcesSource = read.Source,
        };
        if (file.Kind == MacPathKind.File)
        {
            file.children = [entry];
        }

        return entry;
    }

    private static MacPathEntry Type(MacPathEntry fork, FourCC type)
    {
        var name = $"'{type}'";
        return new MacPathEntry(MacPathKind.ResourceType, name, fork.Path + ":" + name, fork)
        {
            File = fork.File,
            Resources = fork.Resources,
            ResourceType = type,
        };
    }

    private static MacPathEntry ResourceEntry(MacPathEntry type, Resource resource)
    {
        var name = resource.Id.ToString(CultureInfo.InvariantCulture);
        return new MacPathEntry(MacPathKind.Resource, name, type.Path + ":" + name, type)
        {
            File = type.File,
            Resources = type.Resources,
            ResourceType = type.ResourceType,
            Resource = resource,
        };
    }

    // A node holding files, to be read, or a volume (an empty one holds none).
    private static bool IsContainer(ContainerNode node) => node.Children.Count > 0 || node.UnreadFormat is not null || node.Volume is not null;

    // The format of what a container node holds.
    private static string? ContentFormat(ContainerNode node) => node.UnreadFormat ?? (node.Children.Count > 0 ? node.Children[0].Format : null)
        ?? (node.Volume is { } volume ? volume.Format == "MFS" ? MfsReader.Instance.FormatName : HfsReader.Instance.FormatName : null);

    private string NameOf(MacFile file) => file.UnicodeName ?? Text(file.Name);

    // A name stored as bytes, as text in the options' name encoding.
    private string Text(MacString name) => MacEncodings.Decode(name.Bytes, context.Options.NameEncoding);
}
