using System;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Editing
{
    /// <summary>A host file read as a Mac file to add to an input (the CLI's put, the app's Add File).</summary>
    public static class HostImport
    {
        /// <summary>
        /// <paramref name="path"/> as a Mac file: with its companions (AppleDouble, Basilisk II, PC Exchange), or the one file
        /// it holds when it is MacBinary, AppleSingle or BinHex; else its bytes as the data fork, named after it.
        /// </summary>
        /// <exception cref="IOException">The file cannot be read.</exception>
        public static MacFile Read(string path, ContainerReadOptions? options = null)
        {
            options ??= ContainerReadOptions.Default;
            var host = HostFiles.Read(path, options);
            if (host.Layout != HostLayout.Plain)
            {
                return host.File;
            }

            IContainerReader[] wrappers = [MacBinaryReader.III, MacBinaryReader.II, MacBinaryReader.I, AppleSingleReader.AppleSingle, BinHexReader.Instance];
            if (wrappers.FirstOrDefault(r => r.CanRead(host.File)) is { } reader && reader.Read(host.File, new ContainerContext(options)) is [var inner])
            {
                return inner;
            }

            return host.File;
        }

        /// <summary>
        /// A host text file as a SimpleText document (docs/cli.md §3.1): UTF-8 (a byte order mark dropped) made Mac OS
        /// Roman, CR LF and LF made CR, type <c>TEXT</c> and creator <c>ttxt</c>, named after the file.
        /// </summary>
        /// <exception cref="IOException">The file cannot be read.</exception>
        /// <exception cref="InvalidDataException">The text holds a character Mac OS Roman has not; the message names it and its line.</exception>
        public static MacFile ReadText(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            var text = File.ReadAllText(path, new UTF8Encoding(false)).TrimStart('﻿').Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
            if (!MacRoman.TryEncode(text, out var bytes))
            {
                var lines = text.Split('\r');
                for (var line = 0; line < lines.Length; line++)
                {
                    if (!MacRoman.TryEncode(lines[line], out _))
                    {
                        var bad = lines[line].EnumerateRunes().First(r => !MacRoman.TryEncode(r.ToString(), out _));
                        throw new InvalidDataException($"{Path.GetFileName(path)}: line {line + 1} has {bad} (U+{bad.Value:X4}), which Mac OS Roman cannot hold.");
                    }
                }
            }

            return new MacFile
            {
                Name = MacString.FromMacRoman(Path.GetFileName(path)),
                DataFork = ForkData.FromBytes(bytes!),
                FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") },
            };
        }
    }
}
