using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Editing;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Cli;

/// <summary>
/// The steps the CLI's path commands and the MCP server share beyond <see cref="MacCommands"/>: a file's Finder kind,
/// a resource decoded by the built-in decoders, and the JSON objects of docs/cli.md §2.6 and §3.3 (every object starts
/// with <c>input</c> and <c>path</c>; camelCase; facts that do not apply are left out).
/// </summary>
internal static class MacPathJson
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The Finder kind of a file (an alias by its flag, else by type and creator); null for anything else.</summary>
    public static FinderKind? KindOf(MacPathEntry entry) =>
        entry.File is { } file && entry.Kind is MacPathKind.File or MacPathKind.Container
            ? (file.FinderInfo.Flags & FinderFlags.IsAlias) != 0
                ? new FinderKind("alias", FinderKindSource.BuiltIn, null, null)
                : KnownKinds.Resolve(null, file.FinderInfo.Type, file.FinderInfo.Creator)
            : null;

    /// <summary>A resource decoded by the built-in decoders: its main file, or null when none decodes it.</summary>
    public static DecodedFile? Decode(MacPathEntry entry, byte[] data, ReadOptions readOptions)
    {
        var resource = entry.Resource!;
        var decoder = ResourceDecoders.Create(DecodeOptions.Default).FirstOrDefault(d => d.CanDecode(resource.Type));
        return decoder?.Decode(new DecodeInput(resource, data, entry.Resources!, readOptions, new List<Diagnostic>())).FirstOrDefault();
    }

    /// <summary>One JSON object written by <paramref name="body"/>: indented for people, or compact (the MCP server).</summary>
    public static string Document(Action<Utf8JsonWriter> body, bool indented = true)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, indented ? Indented : Compact))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The input (the host file) and an entry's path inside it (docs/cli.md §3.3).</summary>
    public static void Where(Utf8JsonWriter w, string input, MacPathTree tree, string path)
    {
        w.WriteString("input", input);
        w.WriteString("path", Inside(tree, path));
    }

    /// <summary>A full Mac path without its host file: the names inside the input ("" for the input itself).</summary>
    public static string Inside(MacPathTree tree, string path) => path.Length > tree.Root.Path.Length ? path[(tree.Root.Path.Length + 1)..] : "";

    /// <summary>One entry's facts (ls, find); those that do not apply are left out.</summary>
    public static void Entry(Utf8JsonWriter w, MacPathTree tree, MacEntryInfo e)
    {
        w.WriteString("name", e.Name);
        w.WriteString("path", Inside(tree, e.Path));
        w.WriteString("kind", e.Kind);
        Optional(w, "type", e.Type);
        Optional(w, "creator", e.Creator);
        Optional(w, "dataSize", e.DataSize);
        Optional(w, "resourceSize", e.ResourceSize);
        Optional(w, "created", e.Created?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        Optional(w, "modified", e.Modified?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        Optional(w, "flags", e.Flags);
        if (e.FlagNames.Count > 0)
        {
            w.WriteStartArray("flagNames");
            foreach (var name in e.FlagNames)
            {
                w.WriteStringValue(name);
            }

            w.WriteEndArray();
        }

        if (e.Locked == true)
        {
            w.WriteBoolean("locked", true);
        }

        Optional(w, "format", e.Format);
        Optional(w, "count", e.Count);
        Optional(w, "resourceType", e.ResourceType);
        Optional(w, "resourceId", e.ResourceId);
        Optional(w, "resourceName", e.ResourceName);
    }

    /// <summary>A list of entries (ls's <c>entries</c>, find's <c>matches</c>).</summary>
    public static void Entries(Utf8JsonWriter w, string name, MacPathTree tree, IEnumerable<MacEntryInfo> entries)
    {
        w.WriteStartArray(name);
        foreach (var e in entries)
        {
            w.WriteStartObject();
            Entry(w, tree, e);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>stat's object (docs/cli.md §2.2): the input, the entry's facts, its Finder kind and how it was read.</summary>
    public static void Stat(Utf8JsonWriter w, string input, MacPathTree tree, MacPathEntry entry, MacEntryInfo info)
    {
        w.WriteString("input", input);
        Entry(w, tree, info);
        if (KindOf(entry) is { } kind)
        {
            w.WriteString("kindName", kind.Text);
            w.WriteString("kindSource", KnownKinds.Describe(kind));
        }

        w.WriteStartArray("chain");
        foreach (var step in info.Chain)
        {
            w.WriteStartObject();
            w.WriteString("name", step.Name);
            w.WriteString("format", step.Format);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        Optional(w, "resourceForkSource", info.ResourceForkSource);
        Optional(w, "resourceAttributes", info.ResourceAttributes);
        if (info.Volume is { } volume)
        {
            w.WriteStartObject("volume");
            w.WriteString("format", volume.Format);
            w.WriteString("name", volume.Name);
            w.WriteNumber("blockSize", volume.BlockSize);
            w.WriteNumber("totalBlocks", volume.TotalBlocks);
            w.WriteNumber("totalBytes", volume.TotalBytes);
            w.WriteNumber("freeBlocks", volume.FreeBlocks);
            w.WriteNumber("freeBytes", volume.FreeBytes);
            if (volume.Files is { } files)
            {
                w.WriteNumber("files", files);
            }

            if (volume.Folders is { } folders)
            {
                w.WriteNumber("folders", folders);
            }

            Optional(w, "created", volume.Created?.ToDateTime().ToString("s", CultureInfo.InvariantCulture));
            Optional(w, "modified", volume.Modified?.ToDateTime().ToString("s", CultureInfo.InvariantCulture));
            Optional(w, "backedUp", volume.BackedUp?.ToDateTime().ToString("s", CultureInfo.InvariantCulture));
            w.WriteBoolean("utcAfterCreation", volume.UtcAfterCreation);
            if (volume.BlessedFolderId is { } blessed)
            {
                w.WriteNumber("blessedFolderId", blessed);
                Optional(w, "blessedFolder", info.BlessedFolder);
            }

            w.WriteBoolean("softwareLocked", volume.SoftwareLocked);
            w.WriteBoolean("hardwareLocked", volume.HardwareLocked);
            w.WriteEndObject();
        }

        if (info.Alias is { } alias)
        {
            w.WriteStartObject("alias");
            w.WriteString("storedPath", alias.StoredPath);
            w.WriteBoolean("found", alias.Found);
            w.WriteString("how", alias.How);
            w.WriteString("resolvedPath", alias.ResolvedPath);
            w.WriteString("state", alias.State);
            w.WriteString("explanation", alias.Explanation);
            Optional(w, "target", alias.Target is { } target ? Inside(tree, target) : null);
            w.WriteEndObject();
        }
    }

    /// <summary>A list of host files (get's and the writes' <c>written</c>).</summary>
    public static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
        {
            w.WriteStringValue(value);
        }

        w.WriteEndArray();
    }

    /// <summary>The write commands' result (docs/cli.md §3.3).</summary>
    public static void Changes(Utf8JsonWriter w, string input, bool dryRun, IEnumerable<string> written, IEnumerable<PlannedChange> changes)
    {
        w.WriteString("input", input);
        w.WriteBoolean("dryRun", dryRun);
        Strings(w, "written", written);
        w.WriteStartArray("changes");
        foreach (var planned in changes)
        {
            w.WriteStartObject();
            w.WriteString("action", planned.Action);
            w.WriteString("path", planned.Path);
            w.WriteString("detail", planned.Detail);
            if (planned.Warnings.Count > 0)
            {
                Strings(w, "warnings", planned.Warnings);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    public static void Optional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    public static void Optional(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } number)
        {
            w.WriteNumber(name, number);
        }
    }
}
