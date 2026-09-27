using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// Reads one container format (MacBinary, BinHex, AppleSingle, …) into Mac files. Applications register their own
    /// alongside the built-in ones.
    /// </summary>
    public interface IContainerReader
    {
        /// <summary>A short name for the format, such as <c>MacBinary</c>, used in manifests and messages.</summary>
        string FormatName { get; }

        /// <summary>Whether <paramref name="input"/> looks like this format; reads only what it needs to decide.</summary>
        bool CanRead(ForkData input);

        /// <summary>
        /// Reads the container's files. Damage goes to <see cref="ContainerContext.Diagnostics"/>; only unusable input
        /// throws (<see cref="System.IO.InvalidDataException"/>).
        /// </summary>
        IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context);
    }

    /// <summary>What a container reader works with besides its input.</summary>
    public sealed class ContainerContext
    {
        /// <summary>A context with the given options, diagnostics sink and host name.</summary>
        public ContainerContext(
            ContainerReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null, MacString? hostName = null)
        {
            Options = options ?? ContainerReadOptions.Default;
            Diagnostics = diagnostics ?? new List<Diagnostic>();
            HostName = hostName;
        }

        /// <summary>The limits and choices for reading.</summary>
        public ContainerReadOptions Options { get; }

        /// <summary>Where problems go.</summary>
        public ICollection<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// The name the input had on the host (or in its outer container), for containers that may not record one.
        /// </summary>
        public MacString? HostName { get; }

        /// <summary>The same context for an inner container with a different host name.</summary>
        public ContainerContext WithHostName(MacString? hostName) => new(Options, Diagnostics, hostName);

        /// <summary>Records a problem.</summary>
        public void Report(DiagnosticSeverity severity, string code, string message, long? offset = null) =>
            Diagnostics.Add(new Diagnostic(severity, code, message, offset));
    }
}
