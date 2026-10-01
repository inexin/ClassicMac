using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>
/// gzip files (RFC 1952), expanded with <see cref="GZipStream"/> (which also reads members concatenated one after
/// another). The result is one file, named from the header's FNAME field or from the input's name without its
/// <c>.gz</c>; a tar archive or MacBinary file inside (<c>.tgz</c>, MacGzip) is unwrapped in turn.
/// </summary>
public sealed class GzipReader : IContainerReader
{
    private const byte FlagExtra = 4, FlagName = 8;

    /// <summary>The built-in gzip reader.</summary>
    public static GzipReader Instance { get; } = new();

    private GzipReader() { }

    /// <inheritdoc/>
    public string FormatName => "gzip";

    /// <summary>The magic bytes $1F $8B, method 8 (deflate) and no reserved flag bits (RFC 1952 §2.3.1).</summary>
    public bool CanRead(ForkData input)
    {
        var header = input.ReadPrefix(10);
        return header.Length == 10 && header[0] == 0x1F && header[1] == 0x8B && header[2] == 8 && (header[3] & 0xE0) == 0;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        if (!CanRead(input)) throw new InvalidDataException("Not a gzip file.");
        var header = input.ReadPrefix(10 + 2 + 1024);
        var flags = header[3];
        var mtime = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        string? storedName = null;
        var at = 10;
        if ((flags & FlagExtra) != 0 && at <= header.Length - 2)
            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(at));
        if ((flags & FlagName) != 0 && at < header.Length)
        {
            var end = header.AsSpan(at).IndexOf((byte)0);
            // FNAME is ISO 8859-1 (RFC 1952 §2.3.1); only its last path component is the name.
            if (end > 0) storedName = Encoding.Latin1.GetString(header, at, end).Split('/', '\\')[^1];
        }

        var limit = context.Options.MaxExpandedBytesPerInput;
        using var output = new MemoryStream();
        using (var stream = new GZipStream(input.Open(), CompressionMode.Decompress))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (output.Length > limit - read)
                    throw new InvalidDataException("gzip expansion exceeds the configured expanded-size limit.");
                output.Write(buffer, 0, read);
            }
        }

        var name = string.IsNullOrEmpty(storedName) ? NameFromHost(context.HostName) : storedName;
        return
        [
            new MacFile
            {
                Name = UnixArchive.ToMacString(name),
                UnicodeName = name,
                Modified = mtime == 0 ? null : UnixArchive.FromUnix(mtime, context),
                DataFork = ForkData.FromBytes(output.ToArray()),
            },
        ];
    }

    // The input's name without .gz (or .z, -gz, _gz); .tgz becomes .tar [ClassicMac, after gzip's own rule].
    private static string NameFromHost(MacString? host)
    {
        var name = host?.ToMacRoman() ?? "";
        if (name.Length == 0) return "gzip data";
        if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return name[..^4] + ".tar";
        foreach (var suffix in new[] { ".gz", "-gz", "_gz", ".z" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        return name;
    }
}
