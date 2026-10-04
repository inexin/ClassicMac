using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Code;

/// <summary>Which code <see cref="CodeExport"/> lists.</summary>
public enum CodeCpu
{
    /// <summary>Both.</summary>
    Both,

    /// <summary>68k code: the segments and the 68k code resources (fat ones with the fragment they carry).</summary>
    M68k,

    /// <summary>PowerPC code: native code resources and the fragments the <c>'cfrg'</c> resources name.</summary>
    PowerPC,
}

/// <summary>A file <see cref="CodeExport"/> writes: its name (a host name) and its bytes.</summary>
/// <param name="Name">The file's name.</param>
/// <param name="Content">Its bytes (UTF-8 text).</param>
public sealed record CodeFile(string Name, ReadOnlyMemory<byte> Content);

/// <summary>
/// A Mac file's code as listings (the <c>disasm</c> command): one <c>.s</c> per 68k segment (<c>CODE-1 Main.s</c>),
/// per code resource (<c>DRVR-12 .Sony.s</c>) and per fragment a <c>'cfrg'</c> names in the data fork
/// (<c>fragment-0 Name.s</c> for <c>'cfrg'</c> 0, <c>fragment-1.0 Name.s</c> for 1) or in a resource no decoder lists,
/// then <c>code.json</c>: the application (its jump
/// table and build model), and each segment, code resource and fragment with its file and functions.
/// </summary>
public static class CodeExport
{
    private static readonly FourCC CodeType = FourCC.FromString("CODE");
    private static readonly FourCC CfrgType = FourCC.FromString("cfrg");

    /// <summary>
    /// Lists <paramref name="fork"/>'s code (and the data fork's fragments, read by <paramref name="dataFork"/> only when
    /// a <c>'cfrg'</c> places one there). Empty when the file has no code. Problems go to <paramref name="diagnostics"/>.
    /// </summary>
    public static IReadOnlyList<CodeFile> Disassemble(ResourceFork fork, Func<ReadOnlyMemory<byte>> dataFork, CodeCpu cpu = CodeCpu.Both,
        ReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(dataFork);
        diagnostics ??= new List<Diagnostic>();
        bool m68k = cpu != CodeCpu.PowerPC, ppc = cpu != CodeCpu.M68k;
        var files = new List<CodeFile>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "code.json" };
        var listed = new Dictionary<(FourCC, short), string>();
        ReadOnlyMemory<byte> Data(Resource r) => ResourceDecompression.Default.GetData(r, fork, options, diagnostics);
        string Add(Resource r, string text)
        {
            var stem = new List<byte>();
            Span<byte> type = stackalloc byte[4];
            r.Type.CopyTo(type);
            stem.AddRange(type);
            stem.AddRange(Encoding.ASCII.GetBytes("-" + r.Id.ToString(CultureInfo.InvariantCulture)));
            if (r.Name is { Bytes.Length: > 0 } name)
            {
                stem.Add((byte)' ');
                stem.AddRange(name.Bytes);
            }
            var file = HostNames.MakeUnique(HostNames.ToHostName(new MacString(stem.ToArray()), 120) + ".s", taken);
            files.Add(new CodeFile(file, Encoding.UTF8.GetBytes(text)));
            listed[(r.Type, r.Id)] = file;
            return file;
        }

        // The 68k application, or its segments alone when CODE 0 does not read.
        CodeApplication? app = null;
        var segments = new List<(CodeSegment? Segment, Resource Resource, string File, CodeListing Listing)>();
        if (m68k && fork.OfType(CodeType).Any())
        {
            if (fork.Find(CodeType, 0) is not null)
            {
                try
                {
                    app = CodeApplication.Read(fork, diagnostics);
                }
                catch (InvalidDataException e)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.jump-table-unreadable",
                        $"'CODE' 0: the jump table could not be read ({e.Message}); the segments are listed on their own."));
                }
            }
            foreach (var resource in fork.OfType(CodeType).Where(r => r.Id != 0).OrderBy(r => r.Id))
            {
                CodeListing listing;
                var segment = app?.FindSegment(resource.Id);
                if (app is not null && segment is not null)
                {
                    listing = CodeListing.ForSegment(app, resource.Id);
                }
                else
                {
                    listing = CodeListing.ForCodeResource(CodeType, resource.Id, Data(resource), Named(resource, Data(resource)));
                    foreach (var d in listing.Diagnostics)
                    {
                        diagnostics.Add(d);
                    }
                }
                segments.Add((segment, resource, Add(resource, listing.Text), listing));
            }
        }

        // Code resources, in the fork's order.
        var codeTypes = CodeResourceDecoder.NativeTypes.Concat(CodeResourceDecoder.M68kTypes).Select(FourCC.FromString).ToHashSet();
        var resources = new List<(Resource Resource, string File, string Format, CodeListing Listing)>();
        foreach (var resource in fork.Resources.Where(r => codeTypes.Contains(r.Type)))
        {
            var data = Data(resource);
            bool pef = PefContainer.IsPef(data.Span);
            if (pef ? !ppc : !m68k)
            {
                continue;
            }

            if (List(resource, data, diagnostics) is { } listing)
            {
                resources.Add((resource, Add(resource, listing.Text), pef ? "pef" : "68k", listing));
            }
        }

        // The fragments the 'cfrg' resources name ('cfrg' 0 an application's or library's; the System file has more).
        var fragments = new List<(short Cfrg, int Index, CfrgMember Member, string? File, PefContainer? Pef, CodeListing? Listing)>();
        ReadOnlyMemory<byte>? dataForkBytes = null;
        foreach (var cfrgResource in fork.OfType(CfrgType).Where(_ => ppc).OrderBy(r => r.Id))
        {
            short cfrgId = cfrgResource.Id;
            Cfrg? cfrg = null;
            try
            {
                cfrg = Cfrg.Read(Data(cfrgResource), diagnostics);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.cfrg-unreadable",
                    $"{cfrgResource} is not a code fragment resource ({e.Message}); its fragments are not listed."));
            }
            for (int i = 0; cfrg is not null && i < cfrg.Members.Count; i++)
            {
                var member = cfrg.Members[i];
                switch (member.Where)
                {
                    case CfrgWhere.DataFork:
                        {
                            var bytes = dataForkBytes ??= dataFork();
                            long length = member.Length == 0 ? bytes.Length - (long)member.Offset : member.Length;
                            if (member.Offset > bytes.Length || length < 0 || length > bytes.Length - (long)member.Offset)
                            {
                                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.fragment-range",
                                    $"Fragment \"{member.Name}\" is at {member.Offset} for {length} bytes, past the data fork's {bytes.Length} bytes; not listed."));
                                fragments.Add((cfrgId, i, member, null, null, null));
                                break;
                            }
                            var slice = bytes.Slice((int)member.Offset, (int)length);
                            PefContainer pef;
                            try
                            {
                                pef = PefContainer.Read(slice, diagnostics);
                            }
                            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                            {
                                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.fragment-unreadable",
                                    $"Fragment \"{member.Name}\" in the data fork is not a PEF container ({e.Message}); not listed."));
                                fragments.Add((cfrgId, i, member, null, null, null));
                                break;
                            }
                            var listing = CodeListing.ForFragment(pef, member.Name);
                            foreach (var d in listing.Diagnostics)
                            {
                                diagnostics.Add(d);
                            }

                            var stem = "fragment-" + (cfrgId == 0 ? "" : cfrgId.ToString(CultureInfo.InvariantCulture) + ".")
                            + i.ToString(CultureInfo.InvariantCulture) + (member.Name.Length > 0 ? " " + member.Name : "");
                            var file = HostNames.MakeUnique(HostNames.ToHostName(MacString.FromMacRoman(stem), 120) + ".s", taken);
                            files.Add(new CodeFile(file, Encoding.UTF8.GetBytes(listing.Text)));
                            fragments.Add((cfrgId, i, member, file, pef, listing));
                            break;
                        }
                    case CfrgWhere.Resource:
                        {
                            var key = (new FourCC(member.Where1), (short)member.Where2);
                            if (fork.Find(key.Item1, key.Item2) is not { } resource)
                            {
                                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.fragment-missing",
                                    $"Fragment \"{member.Name}\" is in '{key.Item1}' {key.Item2}, which the file does not have."));
                                fragments.Add((cfrgId, i, member, null, null, null));
                                break;
                            }
                            var data = Data(resource);
                            if (!listed.TryGetValue(key, out var file) && List(resource, data, diagnostics) is { } listing)
                            {
                                file = Add(resource, listing.Text);
                                resources.Add((resource, file, "pef", listing));
                            }
                            PefContainer? pef = null;
                            if (PefContainer.IsPef(data.Span))
                            {
                                try
                                { pef = PefContainer.Read(data, new List<Diagnostic>()); }
                                catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { }
                            }
                            var known = resources.FirstOrDefault(r => r.Resource == resource);
                            fragments.Add((cfrgId, i, member, file, pef, known.Listing));
                            break;
                        }
                    default:
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "code.fragment-elsewhere",
                            $"Fragment \"{member.Name}\" is not in this file ({CodeJson.Name(member.Where)}); not listed."));
                        fragments.Add((cfrgId, i, member, null, null, null));
                        break;
                }
            }
        }

        if (files.Count == 0 && fragments.Count == 0 && segments.Count == 0)
        {
            return [];
        }

        files.Add(new CodeFile("code.json", MacText.Json(w =>
        {
            w.WriteStartObject();
            if (app is null)
            {
                w.WriteNull("application");
            }
            else
            {
                w.WriteStartObject("application");
                CodeJson.Application(w, app);
                w.WriteEndObject();
            }
            w.WriteStartArray("segments");
            foreach (var (segment, resource, file, listing) in segments)
            {
                w.WriteStartObject();
                w.WriteNumber("id", resource.Id);
                w.WriteString("name", resource.Name?.ToMacRoman());
                w.WriteString("file", file);
                w.WriteBoolean("readable", segment?.IsReadable ?? true);
                CodeJson.Functions(w, listing.Functions, withSection: false);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("codeResources");
            foreach (var (resource, file, format, listing) in resources)
            {
                w.WriteStartObject();
                w.WriteString("type", resource.Type.ToString());
                w.WriteNumber("id", resource.Id);
                w.WriteString("name", resource.Name?.ToMacRoman());
                w.WriteString("format", format);
                w.WriteString("file", file);
                CodeJson.Functions(w, listing.Functions, withSection: format == "pef");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("fragments");
            foreach (var (cfrgId, index, member, file, pef, listing) in fragments)
            {
                w.WriteStartObject();
                w.WriteNumber("cfrg", cfrgId);
                w.WriteNumber("index", index);
                w.WriteString("name", member.Name);
                w.WriteString("where", CodeJson.Name(member.Where));
                w.WriteNumber("offset", member.Offset);
                w.WriteNumber("length", member.Length);
                if (member.Where == CfrgWhere.Resource)
                {
                    w.WriteString("resourceType", new FourCC(member.Where1).ToString());
                    w.WriteNumber("resourceId", (short)member.Where2);
                }
                w.WriteString("file", file);
                if (pef is not null)
                {
                    CodeJson.Fragment(w, pef);
                }

                if (listing is not null)
                {
                    CodeJson.Functions(w, listing.Functions, withSection: true);
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        })));
        return files;
    }

    // A code resource's listing; a PEF container that does not read is reported and gives none.
    private static CodeListing? List(Resource resource, ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
    {
        try
        {
            var listing = CodeListing.ForCodeResource(resource.Type, resource.Id, data, Named(resource, data));
            foreach (var d in listing.Diagnostics)
            {
                diagnostics.Add(d);
            }

            return listing;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "code.pef-unreadable",
                $"{resource}: its PEF container could not be read ({e.Message}); not listed."));
            return null;
        }
    }

    // A fork holding only the resource, with its name, for the listing's title.
    private static ResourceFork Named(Resource resource, ReadOnlyMemory<byte> data)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(resource.Type, resource.Id, data.ToArray()) { Name = resource.Name });
        return fork;
    }
}
