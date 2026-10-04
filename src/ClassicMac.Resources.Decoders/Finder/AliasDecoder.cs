using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Finder;

/// <summary>Alias records (<c>'alis'</c>) as JSON (<c>finder.alias</c>, docs/formats/resources/aliases.md §5).</summary>
internal sealed class AliasDecoder(DecodeOptions options) : IResourceDecoder, IBuiltInDecoder
{
    private static readonly FourCC Alis = FourCC.FromString("alis");

    public string Name => "finder.alias";

    public int Version => 1;

    public bool CanDecode(FourCC type) => type == Alis;

    public IReadOnlyCollection<FourCC> Types => [Alis];

    public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
    {
        var alias = AliasRecord.Read(input.Data, out var complete);    // a record it cannot read is exported raw
        if (!complete)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "alias.short",
                $"{input.Resource}: the tagged data ends early or has no end tag; read as far as it goes."));
        }

        if (alias.Size != input.Data.Length)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "alias.size",
                $"{input.Resource}: the record says it is {alias.Size} bytes; the resource is {input.Data.Length}."));
        }

        string Text(MacString text) => MacText.Decode(text.Bytes, options);
        string? Code(FourCC code) => code.Value == 0 ? null : code.ToString();
        string? Date(MacDate? date) => date?.ToDateTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var json = MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("userType", Code(alias.UserType));
            w.WriteNumber("size", alias.Size);
            w.WriteNumber("version", alias.Version);
            w.WriteString("kind", alias.Kind switch
            {
                AliasKind.File => "file",
                AliasKind.Folder => "folder",
                _ => ((short)alias.Kind).ToString(CultureInfo.InvariantCulture),
            });
            w.WriteString("targetPath", MacText.Decode(MacRoman.Encode(alias.TargetPath), options));
            w.WriteStartObject("volume");
            w.WriteString("name", Text(alias.VolumeName));
            w.WriteString("created", Date(alias.VolumeCreated));
            w.WriteString("signature", alias.VolumeSignatureText);
            w.WriteNumber("volumeType", alias.VolumeType);
            w.WriteNumber("attributes", alias.VolumeAttributes);
            w.WriteNumber("fileSystemId", alias.VolumeFileSystemId);
            w.WriteEndObject();
            w.WriteNumber("parentId", alias.ParentId);
            w.WriteString("name", Text(alias.Name));
            w.WriteNumber("targetId", alias.TargetId);
            w.WriteString("created", Date(alias.TargetCreated));
            w.WriteString("type", Code(alias.Type));
            w.WriteString("creator", Code(alias.Creator));
            w.WriteNumber("levelsFrom", alias.LevelsFrom);
            w.WriteNumber("levelsTo", alias.LevelsTo);
            w.WriteStartArray("extras");
            foreach (var extra in alias.Extras)
            {
                w.WriteStartObject();
                w.WriteNumber("tag", extra.Tag);
                w.WriteString("name", extra.Name);
                switch (extra.Tag)
                {
                    case AliasRecord.FolderIdsTag:
                        w.WriteStartArray("ids");
                        foreach (var id in alias.FolderIds)
                        {
                            w.WriteNumberValue(id);
                        }

                        w.WriteEndArray();
                        break;
                    case AliasRecord.ParentNameTag or AliasRecord.FullPathTag or 3 or AliasRecord.ServerNameTag or 5 or 6:
                        w.WriteString("text", Text(new MacString(extra.Data.Span)));
                        break;
                    default:
                        w.WriteString("data", Convert.ToHexString(extra.Data.Span));
                        break;
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
        return [new DecodedFile(".json", json, MacText.EncodingName(options.TextEncoding))];
    }
}
