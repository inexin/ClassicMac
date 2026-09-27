using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// Reads one container format (MacBinary, BinHex, an HFS image, …) into Mac files. Applications register their own
    /// alongside the built-in ones.
    /// </summary>
    public interface IContainerReader
    {
        /// <summary>A short name for the format, such as <c>MacBinary</c>, used in manifests and messages.</summary>
        string FormatName { get; }

        /// <summary>
        /// Whether <paramref name="input"/> looks like this format. Reads from the current position and restores it.
        /// </summary>
        bool CanRead(Stream input);

        /// <summary>
        /// Reads the container's files. Damage goes to <paramref name="diagnostics"/>; only unusable input throws.
        /// </summary>
        IReadOnlyList<MacFile> Read(Stream input, ContainerReadOptions options, ICollection<Diagnostic> diagnostics);
    }
}
