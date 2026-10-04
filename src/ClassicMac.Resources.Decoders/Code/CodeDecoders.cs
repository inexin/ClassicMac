using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Code;

/// <summary>
/// <c>'CODE'</c>: each segment as its data (<c>.bin</c>, so <c>pack</c> reads it back), its listing (<c>.s</c>) and its
/// model (<c>.json</c>), read with the fork's application (<c>'CODE'</c> 0, <c>'DATA'</c>, <c>'RELA'</c>);
/// <c>'CODE'</c> 0 as its data and the jump table (<c>.json</c>), with the application's diagnostics.
/// </summary>
internal sealed class CodeSegmentDecoder : IResourceDecoder, IBuiltInDecoder
{
    private static readonly FourCC CodeType = FourCC.FromString("CODE");
    private readonly ApplicationCache applications = new();

    public string Name => "code.segment";

    public int Version => 1;

    public IReadOnlyCollection<FourCC> Types { get; } = [CodeType];

    public bool CanDecode(FourCC type) => type == CodeType;

    public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
    {
        var bin = new DecodedFile(".bin", input.Data);
        short id = input.Resource.Id;
        var (app, appDiagnostics, failure) = applications.Get(input);
        if (id == 0)
        {
            if (app is null)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.jump-table-unreadable",
                    $"{input.Resource}: the jump table could not be read ({failure}); written as its data only."));
                return [bin];
            }
            foreach (var d in appDiagnostics)
            {
                input.Diagnostics.Add(d);
            }

            return [bin, new DecodedFile(".json", MacText.Json(w =>
            {
                w.WriteStartObject();
                CodeJson.Application(w, app);
                w.WriteEndObject();
            }))];
        }

        if (app?.FindSegment(id) is { } segment)
        {
            var listing = CodeListing.ForSegment(app, id);
            return [bin, Listing(listing), new DecodedFile(".json", MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("segment", id);
                w.WriteString("name", segment.Name);
                w.WriteString("model", CodeJson.Name(app.Model));
                w.WriteBoolean("readable", segment.IsReadable);
                CodeJson.SegmentHeader(w, segment.Header);
                w.WriteStartArray("jumpTableEntries");
                foreach (var e in app.JumpTable.Where(e => e.Segment == id && e.ResourceOffset is not null))
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", e.Index);
                    w.WriteNumber("resourceOffset", e.ResourceOffset!.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                CodeJson.Relocations(w, Relocations(app, segment));
                CodeJson.Functions(w, listing.Functions, withSection: false);
                CodeJson.References(w, listing.References, withSection: false);
                w.WriteEndObject();
            }))];
        }

        // No application (or one that does not read): the segment on its own.
        if (failure is not null && input.Ids(CodeType).Any(r => r.Id == 0))
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.listing-application",
                $"The fork's 'CODE' 0 could not be read ({failure}); 'CODE' {id} is listed on its own."));
        }

        var lone = CodeListing.ForCodeResource(CodeType, id, input.Data, NamedFork(input));
        foreach (var d in lone.Diagnostics)
        {
            input.Diagnostics.Add(d);
        }

        var header = ClassicMac.Code.M68k.SegmentHeader.Read(input.Data, new List<Diagnostic>());
        return [bin, Listing(lone), new DecodedFile(".json", MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("segment", id);
            w.WriteString("name", input.Resource.Name?.ToMacRoman());
            w.WriteBoolean("readable", true);
            CodeJson.SegmentHeader(w, header);
            w.WriteStartArray("jumpTableEntries");
            w.WriteEndArray();
            var relocations = header is { IsFar: true, A5RelocationOffset: not 0 } far
                ? FarRelocations.Read(input.Data, far.A5RelocationOffset, new List<Diagnostic>()).Select(o => (o, "a5"))
                : [];
            CodeJson.Relocations(w, relocations);
            CodeJson.Functions(w, lone.Functions, withSection: false);
            CodeJson.References(w, lone.References, withSection: false);
            w.WriteEndObject();
        }))];
    }

    // What the loader patches in the segment: the far lists, Retro68's 'RELA', CodeWarrior's lists for CODE 1.
    private static IEnumerable<(long Offset, string Base)> Relocations(CodeApplication app, CodeSegment segment)
    {
        foreach (long at in segment.A5Relocations)
        {
            yield return (at, "a5");
        }

        foreach (long at in segment.PcRelocations)
        {
            yield return (at, "segment");
        }

        foreach (var r in segment.Retro68Relocations)
        {
            yield return (r.Offset, r.Base == Retro68RelocationBase.Segment ? "segment" : "a5");
        }

        if (segment.Id == 1 && app.CodeWarriorData is { } cw)
        {
            foreach (var list in cw.Relocations)
            {
                string? name = list.Kind switch
                {
                    CodeWarriorRelocationKind.CodePlusA5 => "a5",
                    CodeWarriorRelocationKind.CodePlusCode or CodeWarriorRelocationKind.CodePlusCodeSecond => "segment",
                    _ => null,
                };
                if (name is not null)
                {
                    foreach (int at in list.Offsets)
                    {
                        yield return (at, name);
                    }
                }
            }
        }
    }

    internal static DecodedFile Listing(CodeListing listing) => new(".s", Encoding.UTF8.GetBytes(listing.Text));

    // A fork holding only the resource, with its name, for the listing's title.
    internal static ResourceFork NamedFork(DecodeInput input)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(input.Resource.Type, input.Resource.Id, input.Data.ToArray()) { Name = input.Resource.Name });
        return fork;
    }
}

/// <summary><c>'cfrg'</c>: its data and its members (<c>.json</c>).</summary>
internal sealed class CfrgDecoder : IResourceDecoder, IBuiltInDecoder
{
    private static readonly FourCC CfrgType = FourCC.FromString("cfrg");

    public string Name => "code.cfrg";

    public int Version => 1;

    public IReadOnlyCollection<FourCC> Types { get; } = [CfrgType];

    public bool CanDecode(FourCC type) => type == CfrgType;

    public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
    {
        var bin = new DecodedFile(".bin", input.Data);
        Cfrg cfrg;
        try
        {
            cfrg = Cfrg.Read(input.Data, input.Diagnostics);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.cfrg-unreadable",
                $"{input.Resource}: not a code fragment resource ({e.Message}); written as its data only."));
            return [bin];
        }
        return [bin, new DecodedFile(".json", MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("version", cfrg.Version);
            w.WriteStartArray("members");
            foreach (var member in cfrg.Members)
            {
                w.WriteStartObject();
                CodeJson.Member(w, member);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }))];
    }
}

/// <summary>
/// Code resources: native code (a PEF container from offset 0), fat resources (a routine descriptor in front of one),
/// and 68k code behind a standard header, a driver header, a package's <c>$A9FF</c> table, or raw. Each is its data,
/// its listing and its model.
/// </summary>
internal sealed class CodeResourceDecoder : IResourceDecoder, IBuiltInDecoder
{
    // Types holding a PEF container from offset 0 [Verified: the Mac OS 9 System file].
    internal static readonly string[] NativeTypes =
    [
        "ncod", "nlib", "ndrv", "nift", "fovr", "ncmp", "cdek", "dcod", "scal", "vdig", "ntrb", "nitt", "sfvr", "ppct",
        "pthg", "qtcm", "hqda", "ndmc", "nsnd",
    ];

    // 68k code resources, some of them fat [Doc: Inside Macintosh, the managers that load each type].
    internal static readonly string[] M68kTypes =
    [
        "CDEF", "WDEF", "MDEF", "MBDF", "LDEF", "expt", "nsrd", "DRVR", "PACK", "proc", "sift", "FKEY", "INIT", "cdev",
        "RDEV", "XCMD", "XFCN", "ADBS", "PTCH", "ptch", "dcmp", "snth",
    ];

    private static readonly FourCC DriverType = FourCC.FromString("DRVR");

    public string Name => "code.resource";

    public int Version => 1;

    public IReadOnlyCollection<FourCC> Types { get; } = [.. NativeTypes.Concat(M68kTypes).Select(FourCC.FromString)];

    public bool CanDecode(FourCC type) => Types.Contains(type);

    public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
    {
        var bin = new DecodedFile(".bin", input.Data);
        var type = input.Resource.Type;
        CodeListing listing;
        try
        {
            listing = CodeListing.ForCodeResource(type, input.Resource.Id, input.Data, CodeSegmentDecoder.NamedFork(input));
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.pef-unreadable",
                $"{input.Resource}: its PEF container could not be read ({e.Message}); written as its data only."));
            return [bin];
        }
        foreach (var d in listing.Diagnostics)
        {
            input.Diagnostics.Add(d);
        }

        return [bin, CodeSegmentDecoder.Listing(listing), new DecodedFile(".json", MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", type.ToString());
            w.WriteNumber("id", input.Resource.Id);
            Model(w, type, input.Data, listing);
            w.WriteEndObject();
        }))];
    }

    // The model of a code resource whose listing is given; also used by CodeExport.
    internal static void Model(System.Text.Json.Utf8JsonWriter w, FourCC type, ReadOnlyMemory<byte> data, CodeListing listing)
    {
        var scratch = new List<Diagnostic>();
        if (PefContainer.IsPef(data.Span))
        {
            w.WriteString("format", "pef");
            w.WriteStartObject("fragment");
            CodeJson.Fragment(w, PefContainer.Read(data, scratch));
            w.WriteEndObject();
            CodeJson.Functions(w, listing.Functions, withSection: true);
            CodeJson.References(w, listing.References, withSection: true);
            return;
        }
        w.WriteString("format", "68k");
        if (type == DriverType && DriverHeader.Read(data, scratch) is { IsStandard: true } driver)
        {
            w.WriteStartObject("driver");
            w.WriteString("name", driver.Name);
            w.WriteNumber("flags", (ushort)driver.Flags);
            w.WriteNumber("delay", driver.Delay);
            w.WriteNumber("eventMask", driver.EventMask);
            w.WriteNumber("menu", driver.Menu);
            w.WriteNumber("open", driver.Open);
            w.WriteNumber("prime", driver.Prime);
            w.WriteNumber("control", driver.Control);
            w.WriteNumber("status", driver.Status);
            w.WriteNumber("close", driver.Close);
            w.WriteEndObject();
        }
        else if (PackageHeader.Read(data, scratch) is { } package)
        {
            w.WriteStartObject("package");
            w.WriteString("type", package.Type.ToString());
            w.WriteNumber("id", package.Id);
            w.WriteNumber("version", package.Version);
            w.WriteNumber("flags", package.Flags);
            w.WriteNumber("firstSelector", package.FirstSelector);
            w.WriteNumber("lastSelector", package.LastSelector);
            w.WriteStartArray("entries");
            foreach (var e in package.Entries)
            {
                w.WriteStartObject();
                w.WriteNumber("selector", e.Selector);
                w.WriteNumber("offset", e.Offset);
                if (e.TargetOffset is { } target)
                {
                    w.WriteNumber("target", target);
                }
                else
                {
                    w.WriteNull("target");
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else if (CodeResourceHeader.Read(data, type, scratch) is { } header)
        {
            w.WriteStartObject("standardHeader");
            w.WriteString("type", header.Type.ToString());
            w.WriteNumber("id", header.Id);
            w.WriteNumber("version", header.Version);
            w.WriteNumber("flags", header.Flags);
            w.WriteNumber("entry", header.BranchTarget);
            w.WriteEndObject();
        }
        if (RoutineDescriptor.Find(data) is int at && RoutineDescriptor.Read(data, scratch) is { } descriptor)
        {
            w.WriteStartObject("routineDescriptor");
            w.WriteNumber("offset", at);
            w.WriteNumber("version", descriptor.Version);
            w.WriteStartArray("routines");
            foreach (var r in descriptor.Routines)
            {
                w.WriteStartObject();
                w.WriteNumber("procInfo", r.ProcInfo);
                w.WriteBoolean("powerPC", r.IsPowerPC);
                w.WriteNumber("flags", r.Flags);
                if (r.TargetOffset is { } target)
                {
                    w.WriteNumber("targetOffset", target);
                }
                else
                {
                    w.WriteNumber("procDescriptor", r.ProcDescriptor);
                }

                w.WriteBoolean("pef", r.Pef is not null);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        CodeJson.Functions(w, listing.Functions, withSection: false);
        CodeJson.References(w, listing.References, withSection: false);
        w.WriteStartArray("fragments");
        var pefs = RoutineDescriptor.Find(data) is not null && RoutineDescriptor.Read(data, scratch) is { } d
            ? d.Routines.Where(r => r.Pef is not null && r.TargetOffset is >= 0 && r.TargetOffset < data.Length).Select(r => r.Pef!).ToList()
            : [];
        for (int i = 0; i < listing.Fragments.Count; i++)
        {
            w.WriteStartObject();
            if (i < pefs.Count)
            {
                CodeJson.Fragment(w, pefs[i]);
            }

            CodeJson.Functions(w, listing.Fragments[i].Functions, withSection: true);
            CodeJson.References(w, listing.Fragments[i].References, withSection: true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

// The application a fork's CODE resources make, read once per fork: decoders see one resource at a time, so the
// application (CODE 0 and the segments, DATA 0, RELA, 'cfrg' 0) is rebuilt from DecodeInput.Find and kept while
// the same resources come back (compared by content).
internal sealed class ApplicationCache
{
    private static readonly FourCC[] ApplicationTypes = [.. new[] { "CODE", "DATA", "RELA", "cfrg" }.Select(FourCC.FromString)];

    private readonly object gate = new();
    private byte[]? key;
    private (CodeApplication? App, IReadOnlyList<Diagnostic> Diagnostics, string? Failure) cached;

    public (CodeApplication? App, IReadOnlyList<Diagnostic> Diagnostics, string? Failure) Get(DecodeInput input)
    {
        // The other resources are read quietly: their own decoding reports their problems.
        var before = input.Diagnostics.ToList();
        var fork = new ResourceFork();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var type in ApplicationTypes)
        {
            foreach (var (id, name) in input.Ids(type))
            {
                var data = type == input.Resource.Type && id == input.Resource.Id ? input.Data : input.Find(type, id) ?? ReadOnlyMemory<byte>.Empty;
                var resource = new Resource(type, id, data.ToArray());
                if (name is not null)
                {
                    resource.Name = MacString.FromMacRoman(name);
                }
                // Data the decompressor could not expand keeps its signature: the application reports it (code.compressed).
                if (CompressedResourceHeader.HasSignature(data))
                {
                    resource.Attributes = ResourceAttributes.Compressed;
                }

                fork.Add(resource);
                var entry = new BigEndianWriter();
                entry.WriteFourCC(type);
                entry.WriteInt16(id);
                entry.WriteInt32(data.Length);
                entry.WriteBytes(Encoding.UTF8.GetBytes(name ?? "\0"));
                hash.AppendData(entry.WrittenSpan);
                hash.AppendData(data.Span);
            }
        }
        foreach (var d in input.Diagnostics.ToList())
        {
            if (!before.Contains(d))
            {
                input.Diagnostics.Remove(d);
            }
        }

        var digest = hash.GetHashAndReset();

        lock (gate)
        {
            if (key is not null && key.AsSpan().SequenceEqual(digest))
            {
                return cached;
            }

            var diagnostics = new List<Diagnostic>();
            try
            {
                // Already decompressed: nothing is left for the decompressor but the data it could not expand.
                cached = (CodeApplication.Read(fork, diagnostics), diagnostics, null);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                cached = (null, [], e.Message);
            }
            key = digest;
            return cached;
        }
    }
}
