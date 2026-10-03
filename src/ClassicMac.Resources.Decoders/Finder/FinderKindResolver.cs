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
        /// <summary>The creator application's <c>'kind'</c> resource, its entry for the file type.</summary>
        ApplicationKind,

        /// <summary>The creator application was found but names no kind for the type: "&lt;its name&gt; document".</summary>
        ApplicationName,

        /// <summary>The System's kinds of standard types (the <c>'istd'</c> <c>'kind'</c> resource), any creator.</summary>
        SystemKind,

        /// <summary>ClassicMac's own table of known types and creators.</summary>
        BuiltIn,
    }

    /// <summary>A file's kind, as the Finder's Get Info and list views name it, and where it came from.</summary>
    /// <param name="Text">The kind string ("SimpleText text document").</param>
    /// <param name="Source">Where it came from.</param>
    /// <param name="Application">The creator application's name, when it was found (or is known).</param>
    /// <param name="ResourceId">The ID of the <c>'kind'</c> resource it came from, for <see cref="FinderKindSource.ApplicationKind"/> and <see cref="FinderKindSource.SystemKind"/>.</param>
    public sealed record FinderKind(string Text, FinderKindSource Source, string? Application, short? ResourceId);

    /// <summary>
    /// An application the Finder can take kinds from: its signature (its creator code), its file name, and a reader of its
    /// resource fork, called only when a kind of one of its documents is asked for.
    /// </summary>
    /// <param name="Signature">The application's creator code.</param>
    /// <param name="Name">Its file name.</param>
    /// <param name="Fork">Reads its resource fork; null when it has none.</param>
    public sealed record FinderApplicationSource(FourCC Signature, string Name, Func<ResourceFork?> Fork);

    /// <summary>
    /// Names a document's kind as the Finder does (docs/formats/resources/finder.md §2.3): the creator application's
    /// <c>'kind'</c> entry for the file type; else, when the application is there, "&lt;its name&gt; document"; else the
    /// System's kind for a standard type (<c>'istd'</c>); else nothing, for the caller's own table. One resolver serves one
    /// volume (or any set of files): each application's fork is read once, when first needed, and kept. Safe to use from
    /// several threads.
    /// </summary>
    public sealed class FinderKindResolver
    {
        /// <summary>The <c>'kind'</c> resources' type.</summary>
        public static FourCC KindType { get; } = FourCC.FromString("kind");

        /// <summary>The signature of the System's kinds of standard types ("industry standards").</summary>
        public static FourCC StandardSignature { get; } = FourCC.FromString("istd");

        private static readonly HashSet<FourCC> ApplicationTypes =
            [FourCC.FromString("APPL"), FourCC.FromString("APPC"), FourCC.FromString("APPD"), FourCC.FromString("appe")];

        private readonly ILookup<FourCC, FinderApplicationSource> applications;
        private readonly IReadOnlyList<ResourceFork> systemForks;
        private readonly ReadOptions readOptions;
        private readonly Dictionary<FourCC, App?> read = [];
        private readonly Lazy<IReadOnlyList<(short Id, KindResource Kind)>> standard;
        private readonly object gate = new();

        /// <summary>A resolver over the applications found (by their signatures) and the System's forks (System, System Resources).</summary>
        public FinderKindResolver(IEnumerable<FinderApplicationSource> applications, IEnumerable<ResourceFork> systemForks, ReadOptions? readOptions = null)
        {
            ArgumentNullException.ThrowIfNull(applications);
            ArgumentNullException.ThrowIfNull(systemForks);
            this.applications = applications.ToLookup(a => a.Signature);
            this.systemForks = systemForks.ToList();
            this.readOptions = readOptions ?? ReadOptions.Default;
            standard = new(() => this.systemForks.SelectMany(f => Kinds(f, StandardSignature)).ToList());
        }

        /// <summary>Whether a file of <paramref name="type"/> is an application the Finder looks kinds up in: APPL, APPC, APPD, appe.</summary>
        public static bool IsApplicationType(FourCC type) => ApplicationTypes.Contains(type);

        /// <summary>
        /// The kind of a document of <paramref name="type"/> made by <paramref name="creator"/>, or null when neither its
        /// application nor the System names one.
        /// </summary>
        public FinderKind? Find(FourCC type, FourCC creator)
        {
            if (FindApplication(creator) is { } application)
            {
                foreach (var (id, kind) in application.Kinds)
                {
                    if (kind.KindOf(type) is { } text)
                    {
                        return new FinderKind(text, FinderKindSource.ApplicationKind, application.Name, id);
                    }
                }

                return new FinderKind($"{application.Name} document", FinderKindSource.ApplicationName, application.Name, null);
            }

            foreach (var (id, kind) in standard.Value)
            {
                if (kind.KindOf(type) is { } text)
                {
                    return new FinderKind(text, FinderKindSource.SystemKind, null, id);
                }
            }

            return null;
        }

        /// <summary>The name of the application with <paramref name="signature"/> (its <c>'apnm'</c> kind, else its file name), or null when it is not here.</summary>
        public string? ApplicationName(FourCC signature) => FindApplication(signature)?.Name;

        private App? FindApplication(FourCC signature)
        {
            lock (gate)
            {
                if (read.TryGetValue(signature, out var known))
                {
                    return known;
                }
            }

            var found = Read(signature);
            lock (gate)
            {
                read[signature] = found;
            }

            return found;
        }

        // The first application with the signature: its kinds for that signature (in resource ID order) and its name.
        private App? Read(FourCC signature)
        {
            var source = applications[signature].FirstOrDefault();
            if (source is null)
            {
                return null;
            }

            ResourceFork? fork;
            try
            {
                fork = source.Fork();
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                fork = null;
            }

            var kinds = fork is null ? [] : Kinds(fork, signature);
            var name = kinds.Select(k => k.Kind.ApplicationName).FirstOrDefault(n => n is { Length: > 0 }) ?? source.Name;
            return new App(name, kinds);
        }

        // The fork's 'kind' resources for the signature, in ID order; damaged ones are passed over.
        private IReadOnlyList<(short Id, KindResource Kind)> Kinds(ResourceFork fork, FourCC signature)
        {
            var kinds = new List<(short, KindResource)>();
            foreach (var resource in fork.Resources.Where(r => r.Type == KindType).OrderBy(r => r.Id))
            {
                try
                {
                    var kind = FinderResources.ReadKind(ResourceDecompression.Default.GetData(resource, fork, readOptions, []), out _);
                    if (kind.Signature == signature)
                    {
                        kinds.Add((resource.Id, kind));
                    }
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                {
                    // A damaged 'kind' is passed over, as if absent.
                }
            }

            return kinds;
        }

        private sealed record App(string Name, IReadOnlyList<(short Id, KindResource Kind)> Kinds);
    }
}
