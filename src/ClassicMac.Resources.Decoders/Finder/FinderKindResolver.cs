using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources.Compression;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>Where a kind string came from.</summary>
    public enum FinderKindSource
    {
        /// <summary>A <c>'kind'</c> resource's entry for the creator and type (an application's, in the desktop database).</summary>
        ApplicationKind,

        /// <summary>The creator application's name and " document": its <c>'apnm'</c> kind, else its file name.</summary>
        ApplicationName,

        /// <summary>The System's kinds of standard types (the <c>'istd'</c> <c>'kind'</c> resource), any creator.</summary>
        SystemKind,

        /// <summary>The Finder's own kind of a type (applications, system files, clippings, …), read from the Finder or System.</summary>
        FinderKind,

        /// <summary>ClassicMac's own table, or the built-in English copies of the Finder's and System's strings.</summary>
        BuiltIn,

        /// <summary>A type and creator database the user supplied (<see cref="TypeCreatorDatabase"/>).</summary>
        Database,
    }

    /// <summary>A file's kind, as the Finder's Get Info and list views name it, and where it came from.</summary>
    /// <param name="Text">The kind string ("SimpleText text document").</param>
    /// <param name="Source">Where it came from.</param>
    /// <param name="Application">The creator application's name, when it was found (or is known).</param>
    /// <param name="ResourceId">The ID of the resource it came from: the <c>'kind'</c> for <see cref="FinderKindSource.ApplicationKind"/> and <see cref="FinderKindSource.SystemKind"/>, the Finder's string list for <see cref="FinderKindSource.FinderKind"/>.</param>
    public sealed record FinderKind(string Text, FinderKindSource Source, string? Application, short? ResourceId);

    /// <summary>
    /// A file the Finder registers in its desktop database (an application, or a file with the hasBundle flag): its creator,
    /// its file name, and a reader of its resource fork, called only when its kinds are needed.
    /// </summary>
    /// <param name="Signature">The file's creator code.</param>
    /// <param name="Name">Its file name.</param>
    /// <param name="Fork">Reads its resource fork; null when it has none.</param>
    public sealed record FinderApplicationSource(FourCC Signature, string Name, Func<ResourceFork?> Fork)
    {
        /// <summary>Whether it is an application (APPL, APPC, APPD, appe) rather than another file with a bundle: preferred when the Finder looks an application up by its creator.</summary>
        public bool IsApplication { get; init; } = true;
    }

    /// <summary>
    /// Names kinds as the Finder does (docs/formats/resources/finder.md §2.3, §2.4): the Finder's own kinds of applications,
    /// system files and other special types, from the Finder's and System's resources on the volume; and a document's kind as
    /// <c>GetDocumentKindString</c> finds it: the <c>'kind'</c> entry for its creator and type, its creator's <c>'apnm'</c>
    /// kind and " document", its application's file name and " document", the System's <c>'istd'</c> kind of its type.
    /// One resolver serves one volume: a file's fork is read once, when first needed, and its kinds kept, as the desktop
    /// database keeps them. Safe to use from several threads.
    /// </summary>
    public sealed class FinderKindResolver
    {
        /// <summary>The <c>'kind'</c> resources' type.</summary>
        public static FourCC KindType { get; } = FourCC.FromString("kind");

        /// <summary>The signature of the System's kinds of standard types ("industry standards").</summary>
        public static FourCC StandardSignature { get; } = FourCC.FromString("istd");

        /// <summary>The longest kind string the desktop database keeps [Code: Finder 9.2.2 InstallKindResource].</summary>
        public const int MaxKindLength = 63;

        /// <summary>The longest "^0 document" string <c>GetDocumentKindString</c> makes [Code: Translation library, GetDocumentKindString].</summary>
        public const int MaxDocumentKindLength = 64;

        private const ushort HasBundleFlag = 0x2000;

        private static readonly HashSet<FourCC> ApplicationTypes =
            [FourCC.FromString("APPL"), FourCC.FromString("APPC"), FourCC.FromString("APPD"), FourCC.FromString("appe")];

        private static readonly FourCC Apnm = FourCC.FromString("apnm"), Fmap = FourCC.FromString("fmap"), StrList = FourCC.FromString("STR#"),
            Str = FourCC.FromString("STR ");

        private readonly IReadOnlyList<FinderApplicationSource> sources;
        private readonly IEnumerable<ResourceFork> systemForks;
        private readonly ReadOptions readOptions;
        private readonly short region;
        private readonly object gate = new();
        private readonly Dictionary<(FourCC Signature, FourCC Type), (string Text, short Id)> desktop = [];
        private readonly HashSet<FinderApplicationSource> registered = [];
        private readonly Lazy<IReadOnlyList<ResourceFork>> system;
        private bool allRegistered;

        /// <summary>
        /// A resolver over the files the desktop database would register and the System's forks (System Resources for the
        /// <c>'istd'</c> kinds and the document strings, the Finder for its own kinds); <paramref name="region"/> is the system's
        /// region code, the only one whose kinds are used (0, the United States, by default).
        /// </summary>
        public FinderKindResolver(IEnumerable<FinderApplicationSource> applications, IEnumerable<ResourceFork> systemForks, ReadOptions? readOptions = null,
            short region = 0)
        {
            ArgumentNullException.ThrowIfNull(applications);
            ArgumentNullException.ThrowIfNull(systemForks);
            sources = applications.ToList();
            this.systemForks = systemForks;
            this.readOptions = readOptions ?? ReadOptions.Default;
            this.region = region;
            system = new(() => this.systemForks.ToList());
        }

        /// <summary>
        /// A resolver over one volume's files: those the desktop database registers (an application type or the hasBundle
        /// flag, $2000) and, for the System's and Finder's strings, the System Resources and Finder files (<c>'zsyr'</c> and
        /// <c>'FNDR'</c> by <c>'MACS'</c>) and the System file (<c>'zsys'</c>). Nothing is read until a kind is asked for;
        /// <paramref name="fork"/> may return null or throw <see cref="InvalidDataException"/> for a fork it cannot read.
        /// </summary>
        public static FinderKindResolver ForFiles<T>(IEnumerable<T> files, Func<T, FourCC> type, Func<T, FourCC> creator, Func<T, ushort> flags,
            Func<T, string> name, Func<T, ResourceFork?> fork, ReadOptions? readOptions = null, short region = 0)
        {
            ArgumentNullException.ThrowIfNull(files);
            var list = files.ToList();
            var applications = list.Where(f => IsApplicationType(type(f)) || (flags(f) & HasBundleFlag) != 0)
                .Select(f => new FinderApplicationSource(creator(f), name(f), () => fork(f)) { IsApplication = IsApplicationType(type(f)) });
            var macs = FourCC.FromString("MACS");
            var systemTypes = new[] { FourCC.FromString("zsyr"), FourCC.FromString("FNDR"), FourCC.FromString("zsys") };
            var system = list.Where(f => creator(f) == macs && systemTypes.Contains(type(f)))
                .OrderBy(f => Array.IndexOf(systemTypes, type(f))).Select(f => Safe(() => fork(f))).OfType<ResourceFork>();
            return new FinderKindResolver(applications, system, readOptions, region);
        }

        /// <summary>Whether a file of <paramref name="type"/> is an application: APPL, APPC, APPD, appe.</summary>
        public static bool IsApplicationType(FourCC type) => ApplicationTypes.Contains(type);

        /// <summary>
        /// The kind of a document of <paramref name="type"/> made by <paramref name="creator"/>, as <c>GetDocumentKindString</c>
        /// finds it (§2.3 steps 1–4), or null when it would say just "document".
        /// </summary>
        public FinderKind? Find(FourCC type, FourCC creator) => FindByApplication(type, creator) ?? FindStandard(type);

        /// <summary>
        /// §2.3 steps 1–3: the <c>'kind'</c> for (<paramref name="creator"/>, <paramref name="type"/>); else the
        /// <c>'apnm'</c> kind for the creator and " document"; else the creator's application's file name and " document";
        /// null when none applies.
        /// </summary>
        public FinderKind? FindByApplication(FourCC type, FourCC creator)
        {
            if (Lookup(creator, type) is { } kind)
            {
                return new FinderKind(kind.Text, FinderKindSource.ApplicationKind, KnownName(creator), kind.Id);
            }

            if (Lookup(creator, Apnm) is { } name)
            {
                return new FinderKind(DocumentOf(name.Text), FinderKindSource.ApplicationName, name.Text, name.Id);
            }

            return Application(creator) is { } application
                ? new FinderKind(DocumentOf(application.Name), FinderKindSource.ApplicationName, application.Name, null)
                : null;
        }

        /// <summary>§2.3 step 4: the System's kind of the standard <paramref name="type"/> (<c>'istd'</c>), any creator; null when it names none.</summary>
        public FinderKind? FindStandard(FourCC type)
        {
            lock (gate)
            {
                RegisterSystem();
                return desktop.TryGetValue((StandardSignature, type), out var kind) ? new FinderKind(kind.Text, FinderKindSource.SystemKind, null, kind.Id) : null;
            }
        }

        /// <summary>The creator's application name: its <c>'apnm'</c> kind, else its application's file name; null when neither is here.</summary>
        public string? ApplicationName(FourCC creator) => Lookup(creator, Apnm)?.Text ?? Application(creator)?.Name;

        /// <summary>"document": the System's word for a document of no known kind (System Resources <c>'STR#'</c> −16552 item 1), or null when the volume has none.</summary>
        public string? DocumentWord => SystemString(StrList, -16552, 1);

        /// <summary>
        /// "<paramref name="name"/> document", as the System's pattern (<c>'STR#'</c> −16552 item 2, "^0 document") makes it,
        /// cut to <see cref="MaxDocumentKindLength"/> characters.
        /// </summary>
        public string DocumentOf(string name)
        {
            var pattern = SystemString(StrList, -16552, 2) ?? "^0 document";
            var text = pattern.Replace("^0", name, StringComparison.Ordinal);
            return text.Length > MaxDocumentKindLength ? text[..MaxDocumentKindLength] : text;
        }

        /// <summary>
        /// The Finder's own kind of <paramref name="type"/> read from the Finder on the volume (§2.4): "application program"
        /// (<c>'STR '</c> 6902) for applications, and a system file's kind through <c>'fmap'</c> 5111 into <c>'STR#'</c> 5100;
        /// null when the type has none there (or the Finder is not on the volume).
        /// </summary>
        public FinderKind? FindFinderKind(FourCC type)
        {
            if (IsApplicationType(type) && type != FourCC.FromString("appe") && SystemString(Str, 6902, 0) is { } program)
            {
                return new FinderKind(program, FinderKindSource.FinderKind, null, 6902);
            }

            if (FinderMap(5111, type) is { } item && SystemString(StrList, 5100, item) is { Length: > 0 } systemKind)
            {
                return new FinderKind(systemKind, FinderKindSource.FinderKind, null, 5100);
            }

            if (FinderMap(11010, type) is { } location && SystemString(StrList, 11000, location) is { Length: > 0 } locationKind)
            {
                return new FinderKind(locationKind, FinderKindSource.FinderKind, null, 11000);
            }

            return null;
        }

        /// <summary>The item number <c>'fmap'</c> <paramref name="id"/> gives <paramref name="type"/> ({OSType, i16, i16 item}, ended by a zero type), or null.</summary>
        private int? FinderMap(short id, FourCC type)
        {
            foreach (var fork in system.Value)
            {
                if (fork.Find(Fmap, id) is not { } map)
                {
                    continue;
                }

                var data = Data(map, fork);
                var reader = new BigEndianReader(data);
                for (var at = 0; at <= data.Length - 8; at += 8)
                {
                    var entry = reader.ReadUInt32At(at);
                    if (entry == 0)
                    {
                        break;
                    }

                    if (entry == type.Value)
                    {
                        return reader.ReadInt16At(at + 6);
                    }
                }

                return null;
            }

            return null;
        }

        // A string from the System's forks: an 'STR ' (item 0) or an 'STR#' item (from 1).
        private string? SystemString(FourCC listType, short id, int item)
        {
            foreach (var fork in system.Value)
            {
                if (fork.Find(listType, id) is not { } resource)
                {
                    continue;
                }

                var data = Data(resource, fork).Span;
                var at = 0;
                if (listType == StrList)
                {
                    if (data.Length < 2)
                    {
                        return null;
                    }

                    var count = data[0] << 8 | data[1];
                    at = 2;
                    for (var i = 1; i < item && i <= count && at < data.Length; i++)
                    {
                        at += 1 + data[at];
                    }

                    if (item > count)
                    {
                        return null;
                    }
                }

                return at < data.Length && at + 1 + data[at] <= data.Length ? MacRoman.Decode(data.Slice(at + 1, data[at])) : null;
            }

            return null;
        }

        private ReadOnlyMemory<byte> Data(Resource resource, ResourceFork fork)
        {
            try
            {
                return ResourceDecompression.Default.GetData(resource, fork, readOptions, []);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }

        // The application the Finder finds for a creator (PBDTGetAPPL): an application with that creator, else another file
        // with a bundle and that creator.
        private FinderApplicationSource? Application(FourCC creator) =>
            sources.FirstOrDefault(s => s.Signature == creator && s.IsApplication) ?? sources.FirstOrDefault(s => s.Signature == creator);

        // The creator's name as far as is read already (its 'apnm' kind, else its application's file name), for display:
        // no other file is read for it.
        private string? KnownName(FourCC creator)
        {
            lock (gate)
            {
                return desktop.TryGetValue((creator, Apnm), out var name) ? name.Text : Application(creator)?.Name;
            }
        }

        // The desktop database's kind for (signature, type): the files with that creator are registered first; when they do
        // not have it, every file is (once), since a 'kind' may be signed for another creator (§2.3).
        private (string Text, short Id)? Lookup(FourCC signature, FourCC type)
        {
            lock (gate)
            {
                foreach (var source in sources.Where(s => s.Signature == signature))
                {
                    Register(source);
                }

                if (desktop.TryGetValue((signature, type), out var found))
                {
                    return found;
                }

                if (!allRegistered)
                {
                    foreach (var source in sources)
                    {
                        Register(source);
                    }

                    allRegistered = true;
                    if (desktop.TryGetValue((signature, type), out found))
                    {
                        return found;
                    }
                }

                return null;
            }
        }

        // Adds a file's kinds as the Finder's bundle registration does [Code: Finder 9.2.2 InstallKindResource]: each valid
        // 'kind' of the system's region, every entry by (its signature, type), an existing one replaced only by a 'kind'
        // signed with the installing file's own creator.
        private void Register(FinderApplicationSource source)
        {
            if (!registered.Add(source))
            {
                return;
            }

            if (Safe(source.Fork) is { } fork)
            {
                Install(fork, source.Signature);
            }
        }

        private bool systemRegistered;

        private void RegisterSystem()
        {
            if (systemRegistered)
            {
                return;
            }

            systemRegistered = true;
            foreach (var fork in system.Value)
            {
                Install(fork, StandardSignature);
            }
        }

        private void Install(ResourceFork fork, FourCC creator)
        {
            foreach (var resource in fork.Resources.Where(r => r.Type == KindType).OrderBy(r => r.Id))
            {
                var data = Data(resource, fork);
                var kind = FinderResources.ReadKind(data, out _);
                if (data.Length < 8 || kind.Reserved != 0 || kind.Region != region)
                {
                    continue;
                }

                foreach (var (type, text) in kind.Entries)
                {
                    var key = (kind.Signature, type);
                    if (!desktop.ContainsKey(key) || kind.Signature == creator)
                    {
                        desktop[key] = (text.Length > MaxKindLength ? text[..MaxKindLength] : text, resource.Id);
                    }
                }
            }
        }

        private static ResourceFork? Safe(Func<ResourceFork?> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                return null;
            }
        }
    }
}
