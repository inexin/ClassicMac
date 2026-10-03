using System.IO;
using System.Linq;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Editing
{
    /// <summary>A host file read as a Mac file to add to a volume.</summary>
    public static class HostImport
    {
        /// <summary>
        /// <paramref name="path"/> as a Mac file: with its companions (AppleDouble, Basilisk II, PC Exchange), or the one file
        /// it holds when it is MacBinary or AppleSingle; else its bytes as the data fork, named after it.
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

            IContainerReader[] wrappers = [MacBinaryReader.III, MacBinaryReader.II, MacBinaryReader.I, AppleSingleReader.AppleSingle];
            if (wrappers.FirstOrDefault(r => r.CanRead(host.File)) is { } reader && reader.Read(host.File, new ContainerContext(options)) is [var inner])
            {
                return inner;
            }

            return host.File;
        }
    }
}
