using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>A bundle (<c>'BNDL'</c>): the application's signature, and per resource type its local-to-resource ID map.</summary>
    /// <param name="Signature">The creator code.</param>
    /// <param name="SignatureId">The ID of the signature resource (a resource of the creator's type; 0 by convention).</param>
    /// <param name="Maps">Per type (<c>'ICN#'</c>, <c>'FREF'</c>, …) the (local ID, resource ID) pairs.</param>
    public sealed record Bundle(FourCC Signature, short SignatureId, IReadOnlyList<(FourCC Type, IReadOnlyList<(short Local, short Resource)> Ids)> Maps);

    /// <summary>A file reference (<c>'FREF'</c>): a file type the application handles, and its icon's local ID.</summary>
    /// <param name="FileType">The file type.</param>
    /// <param name="LocalIconId">The icon's local ID, mapped to a resource ID by the bundle.</param>
    /// <param name="FileName">A file name (unused by the Finder; usually empty).</param>
    public sealed record FileReference(FourCC FileType, short LocalIconId, string FileName);

    /// <summary>An application's size resource (<c>'SIZE'</c>): its flags and memory partition sizes.</summary>
    /// <param name="Flags">The flags word.</param>
    /// <param name="Preferred">The preferred partition size, in bytes.</param>
    /// <param name="Minimum">The minimum partition size, in bytes.</param>
    public sealed record SizeResource(ushort Flags, uint Preferred, uint Minimum);

    /// <summary>Reads the Finder's resources (<i>Inside Macintosh: Macintosh Toolbox Essentials</i>, Finder Interface; <i>Processes</i>).</summary>
    public static class FinderResources
    {
        /// <summary>
        /// A <c>'BNDL'</c>: signature, signature resource ID, the number of types less one, then per type the type, its
        /// number of mappings less one, and the (local ID, resource ID) pairs.
        /// </summary>
        public static Bundle ReadBundle(ReadOnlySpan<byte> data, out bool complete)
        {
            complete = data.Length >= 8;
            var maps = new List<(FourCC, IReadOnlyList<(short, short)>)>();
            if (!complete) return new Bundle(data.Length >= 4 ? new FourCC(data[..4]) : default, 0, maps);
            var reader = new BigEndianReader(data);
            var signature = reader.ReadFourCC();
            var signatureId = reader.ReadInt16();
            var types = reader.ReadInt16() + 1;
            for (var t = 0; t < types; t++)
            {
                if (reader.Remaining < 6)
                {
                    complete = false;
                    break;
                }
                var type = reader.ReadFourCC();
                var count = reader.ReadInt16() + 1;
                var ids = new List<(short, short)>();
                for (var i = 0; i < count; i++)
                {
                    if (reader.Remaining < 4)
                    {
                        complete = false;
                        break;
                    }
                    ids.Add((reader.ReadInt16(), reader.ReadInt16()));
                }
                maps.Add((type, ids));
                if (!complete) break;
            }
            return new Bundle(signature, signatureId, maps);
        }

        /// <summary>A <c>'FREF'</c>: file type, local icon ID, file name.</summary>
        public static FileReference ReadFileReference(ReadOnlySpan<byte> data, DecodeOptions options, out bool complete)
        {
            complete = data.Length >= 6;
            if (!complete) return new FileReference(data.Length >= 4 ? new FourCC(data[..4]) : default, 0, "");
            var at = 6;
            var name = at < data.Length && MacText.TryReadPascal(data, ref at, out var text) ? MacText.Decode(text, options) : "";
            return new FileReference(new FourCC(data[..4]), new BigEndianReader(data).ReadInt16At(4), name);
        }

        /// <summary>A <c>'SIZE'</c>: flags, preferred size, minimum size.</summary>
        public static SizeResource ReadSize(ReadOnlySpan<byte> data, out bool complete)
        {
            complete = data.Length >= 10;
            var reader = new BigEndianReader(data);
            return complete
                ? new SizeResource(reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32())
                : new SizeResource(reader.TryReadUInt16(out var flags) ? flags : (ushort)0, 0, 0);
        }

        /// <summary>The <c>'SIZE'</c> flags that are set, by their Rez names, from bit 15 down.</summary>
        public static IEnumerable<string> SizeFlagNames(ushort flags)
        {
            for (var bit = 15; bit >= 0; bit--)
            {
                if ((flags & (1 << bit)) != 0 && SizeFlags[15 - bit] is { } name) yield return name;
            }
        }

        // Bits 15 to 0 (Rez Types.r); null for the reserved ones.
        private static readonly string?[] SizeFlags =
        [
            null, "acceptSuspendResumeEvents", null, "canBackground", "doesActivateOnFGSwitch", "onlyBackground", "getFrontClicks",
            "acceptAppDiedEvents", "is32BitCompatible", "isHighLevelEventAware", "localAndRemoteHLEvents", "isStationeryAware",
            "useTextEditServices", "isDisplayManagerAware", null, null,
        ];
    }

    /// <summary>The Finder's resources as JSON: bundles (with each file type's icon, through its <c>FREF</c>), file references, size resources.</summary>
    internal sealed class FinderDecoder(DecodeOptions options, string name, string type) : IResourceDecoder, IBuiltInDecoder
    {
        private readonly FourCC handled = FourCC.FromString(type);

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC t) => t == handled;

        public IReadOnlyCollection<FourCC> Types => [handled];

        public static IEnumerable<IResourceDecoder> All(DecodeOptions options) =>
        [
            new FinderDecoder(options, "finder.bundle", "BNDL"),
            new FinderDecoder(options, "finder.file-reference", "FREF"),
            new FinderDecoder(options, "finder.size", "SIZE"),
        ];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var data = input.Data.Span;
            bool complete;
            byte[] json;
            switch (handled.ToString())
            {
                case "BNDL":
                {
                    var bundle = FinderResources.ReadBundle(data, out complete);
                    json = Bundle(bundle, input);
                    break;
                }
                case "FREF":
                {
                    var reference = FinderResources.ReadFileReference(data, options, out complete);
                    json = MacText.Json(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("fileType", reference.FileType.ToString());
                        w.WriteNumber("localIconId", reference.LocalIconId);
                        w.WriteString("fileName", reference.FileName);
                        w.WriteEndObject();
                    });
                    break;
                }
                default:
                {
                    var size = FinderResources.ReadSize(data, out complete);
                    json = MacText.Json(w =>
                    {
                        w.WriteStartObject();
                        w.WriteNumber("flags", size.Flags);
                        w.WriteStartArray("flagNames");
                        foreach (var flag in FinderResources.SizeFlagNames(size.Flags)) w.WriteStringValue(flag);
                        w.WriteEndArray();
                        w.WriteNumber("preferredSize", size.Preferred);
                        w.WriteNumber("minimumSize", size.Minimum);
                        w.WriteEndObject();
                    });
                    break;
                }
            }
            if (!complete)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "finder.short",
                    $"{input.Resource}: the data ends early; read as far as it goes."));
            }
            return [new DecodedFile(".json", json, MacText.EncodingName(options.TextEncoding))];
        }

        // The maps, then each file type the application claims: its FREF (through the bundle's FREF map) and its icon
        // family's resource ID (the FREF's local icon ID through the ICN# map).
        private byte[] Bundle(Bundle bundle, DecodeInput input)
        {
            var icons = bundle.Maps.FirstOrDefault(m => m.Type == FourCC.FromString("ICN#")).Ids ?? [];
            var references = bundle.Maps.FirstOrDefault(m => m.Type == FourCC.FromString("FREF")).Ids ?? [];
            return MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("signature", bundle.Signature.ToString());
                w.WriteNumber("signatureId", bundle.SignatureId);
                w.WriteStartArray("maps");
                foreach (var (type, ids) in bundle.Maps)
                {
                    w.WriteStartObject();
                    w.WriteString("type", type.ToString());
                    w.WriteStartArray("ids");
                    foreach (var (local, resource) in ids)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("local", local);
                        w.WriteNumber("resource", resource);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("fileTypes");
                foreach (var (_, freId) in references)
                {
                    if (input.Find(FourCC.FromString("FREF"), freId) is not { } fref) continue;
                    var reference = FinderResources.ReadFileReference(fref.Span, options, out _);
                    w.WriteStartObject();
                    w.WriteString("fileType", reference.FileType.ToString());
                    w.WriteNumber("fref", freId);
                    var icon = icons.Where(i => i.Local == reference.LocalIconId).Select(i => (short?)i.Resource).FirstOrDefault();
                    if (icon is { } id) w.WriteNumber("icon", id);
                    else w.WriteNull("icon");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }
    }
}
