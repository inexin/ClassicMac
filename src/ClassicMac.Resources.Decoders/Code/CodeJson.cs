using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Code;

// The structured model beside a listing (docs/formats/output/disassembly.md §1.4): names in camelCase, enum values
// in camelCase, offsets as numbers (resource offsets for 68k code, section offsets for PowerPC).
internal static class CodeJson
{
    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    // CODE 0: the A5 world's sizes, the build model, the entry points and the jump table.
    public static void Application(Utf8JsonWriter w, CodeApplication app)
    {
        w.WriteNumber("aboveA5", app.AboveA5);
        w.WriteNumber("belowA5", app.BelowA5);
        w.WriteNumber("jumpTableSize", app.JumpTableSize);
        w.WriteNumber("jumpTableOffset", app.JumpTableOffset);
        w.WriteString("model", Name(app.Model));
        w.WriteBoolean("farModel", app.IsFarModel);
        w.WriteBoolean("powerPC", app.HasPowerPCFragment);
        if (app.Entry is { } entry)
        {
            Entry(w, "entry", entry);
        }
        else
        {
            w.WriteNull("entry");
        }

        if (app.OriginalEntry is { } original)
        {
            Entry(w, "originalEntry", original);
        }

        if (app.A5Init is { } init)
        {
            w.WriteStartObject("a5Init");
            w.WriteNumber("segment", app.A5InitSegment ?? 0);
            w.WriteNumber("belowA5Size", init.BelowA5Size);
            w.WriteNumber("runs", init.Runs.Count);
            w.WriteNumber("relocations", init.Relocations.Count);
            w.WriteEndObject();
        }
        if (app.CodeWarriorData is { } cw)
        {
            w.WriteStartObject("codeWarriorData");
            w.WriteStartArray("relocations");
            foreach (var list in cw.Relocations)
            {
                w.WriteStartObject();
                w.WriteString("kind", Name(list.Kind));
                w.WriteNumber("count", list.Offsets.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        if (app.DataRelocations.Count > 0)
        {
            w.WriteNumber("dataRelocations", app.DataRelocations.Count);
        }

        w.WriteStartArray("entries");
        foreach (var e in app.JumpTable)
        {
            w.WriteStartObject();
            w.WriteNumber("index", e.Index);
            w.WriteNumber("a5Offset", e.A5Offset);
            w.WriteString("kind", Name(e.Kind));
            if (e.Kind is not (JumpTableEntryKind.FarMarker or JumpTableEntryKind.Unrecognized))
            {
                w.WriteNumber("segment", e.Segment);
            }

            if (e.Kind is JumpTableEntryKind.NearUnloaded or JumpTableEntryKind.FarUnloaded)
            {
                w.WriteNumber("offset", e.Offset);
            }

            if (e.ResourceOffset is { } at)
            {
                w.WriteNumber("resourceOffset", at);
            }

            if (e.Kind is JumpTableEntryKind.NearLoaded or JumpTableEntryKind.FarLoaded)
            {
                w.WriteNumber("address", e.Address);
            }

            if (e.Kind == JumpTableEntryKind.Unrecognized)
            {
                w.WriteString("raw", e.Raw.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
            }

            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Entry(Utf8JsonWriter w, string name, CodeEntryPoint entry)
    {
        w.WriteStartObject(name);
        w.WriteNumber("segment", entry.Segment);
        w.WriteNumber("offset", entry.Offset);
        w.WriteNumber("resourceOffset", entry.ResourceOffset);
        w.WriteEndObject();
    }

    public static void SegmentHeader(Utf8JsonWriter w, SegmentHeader? header)
    {
        if (header is null)
        {
            w.WriteNull("header");
            return;
        }
        w.WriteStartObject("header");
        w.WriteBoolean("far", header.IsFar);
        w.WriteNumber("firstNearOffset", header.FirstNearOffset);
        w.WriteNumber("nearCount", header.NearCount);
        if (header.IsFar)
        {
            w.WriteNumber("firstFarOffset", header.FirstFarOffset);
            w.WriteNumber("farCount", header.FarCount);
            w.WriteNumber("a5RelocationOffset", header.A5RelocationOffset);
            w.WriteNumber("pcRelocationOffset", header.PcRelocationOffset);
        }
        w.WriteEndObject();
    }

    public static void Relocations(Utf8JsonWriter w, IEnumerable<(long Offset, string Base)> relocations)
    {
        w.WriteStartArray("relocations");
        foreach (var (offset, @base) in relocations.OrderBy(r => r.Offset))
        {
            w.WriteStartObject();
            w.WriteNumber("offset", offset);
            w.WriteString("base", @base);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static void Functions(Utf8JsonWriter w, IEnumerable<CodeFunction> functions, bool withSection)
    {
        w.WriteStartArray("functions");
        foreach (var f in functions)
        {
            w.WriteStartObject();
            if (withSection)
            {
                w.WriteNumber("section", f.Section);
            }

            w.WriteNumber("offset", f.Offset);
            w.WriteString("name", f.Name);
            w.WriteString("source", Name(f.Source));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static void References(Utf8JsonWriter w, IEnumerable<CodeReference> references, bool withSection)
    {
        w.WriteStartArray("references");
        foreach (var r in references)
        {
            w.WriteStartObject();
            if (withSection)
            {
                w.WriteNumber("section", r.Section);
            }

            w.WriteNumber("offset", r.Offset);
            w.WriteString("kind", Name(r.Kind));
            w.WriteString("text", r.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // A PEF container: header fields, sections, entry points, imports and exports.
    public static void Fragment(Utf8JsonWriter w, PefContainer pef)
    {
        w.WriteString("architecture", pef.Architecture.ToString());
        w.WriteNumber("formatVersion", pef.FormatVersion);
        w.WriteNumber("currentVersion", pef.CurrentVersion);
        w.WriteNumber("oldDefVersion", pef.OldDefVersion);
        w.WriteNumber("oldImpVersion", pef.OldImpVersion);
        w.WriteStartArray("sections");
        foreach (var s in pef.Sections)
        {
            w.WriteStartObject();
            w.WriteNumber("index", s.Index);
            if (s.Name is { } name)
            {
                w.WriteString("name", name);
            }

            w.WriteString("kind", Name(s.Kind));
            w.WriteString("share", Name(s.ShareKind));
            w.WriteNumber("totalLength", s.TotalLength);
            w.WriteNumber("unpackedLength", s.UnpackedLength);
            w.WriteNumber("containerLength", s.ContainerLength);
            w.WriteNumber("containerOffset", s.ContainerOffset);
            w.WriteNumber("alignment", s.Alignment);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (pef.Loader is not { } loader)
        {
            return;
        }

        EntryPoint(w, "main", loader.Main);
        EntryPoint(w, "init", loader.Init);
        EntryPoint(w, "term", loader.Term);
        w.WriteStartArray("imports");
        foreach (var symbol in loader.ImportedSymbols)
        {
            w.WriteStartObject();
            w.WriteString("library", symbol.LibraryIndex >= 0 && symbol.LibraryIndex < loader.ImportedLibraries.Count
                ? loader.ImportedLibraries[symbol.LibraryIndex].Name : null);
            w.WriteString("name", symbol.Name);
            w.WriteString("class", Name(symbol.Class));
            w.WriteBoolean("weak", symbol.IsWeak);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("exports");
        foreach (var export in loader.Exports)
        {
            w.WriteStartObject();
            w.WriteString("name", export.Name);
            w.WriteString("class", Name(export.Class));
            w.WriteNumber("section", export.SectionIndex);
            w.WriteNumber("value", export.Value);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void EntryPoint(Utf8JsonWriter w, string name, PefEntryPoint? point)
    {
        if (point is not { } p)
        {
            return;
        }

        w.WriteStartObject(name);
        w.WriteNumber("section", p.Section);
        w.WriteNumber("offset", p.Offset);
        w.WriteEndObject();
    }

    public static void Member(Utf8JsonWriter w, CfrgMember m)
    {
        w.WriteString("name", m.Name);
        w.WriteString("architecture", m.Architecture.ToString());
        w.WriteNumber("updateLevel", m.UpdateLevel);
        w.WriteNumber("currentVersion", m.CurrentVersion);
        w.WriteNumber("oldDefVersion", m.OldDefVersion);
        w.WriteString("usage", Name(m.Usage));
        w.WriteNumber("usage1", m.Usage1);
        w.WriteNumber("usage2", m.Usage2);
        w.WriteString("where", Name(m.Where));
        w.WriteNumber("offset", m.Offset);
        w.WriteNumber("length", m.Length);
        w.WriteNumber("where1", m.Where1);
        w.WriteNumber("where2", m.Where2);
        if (m.Where == CfrgWhere.Resource)
        {
            w.WriteString("resourceType", new FourCC(m.Where1).ToString());
            w.WriteNumber("resourceId", (short)m.Where2);
        }
        if (m.Search is { } search)
        {
            w.WriteStartObject("search");
            w.WriteString("libraryKind", search.LibraryKind.ToString());
            w.WriteStartArray("qualifiers");
            foreach (var q in search.Qualifiers)
            {
                w.WriteStringValue(q);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteStartArray("extensions");
        foreach (var e in m.Extensions)
        {
            w.WriteStartObject();
            w.WriteNumber("kind", e.Kind);
            w.WriteNumber("length", e.Data.Length);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
