namespace ClassicMac.Resources.Cli
{
    /// <summary>The tool's exit codes; documented in the plan's CLI section.</summary>
    internal static class ExitCodes
    {
        /// <summary>Done; warnings, if any, were printed (they fail only with <c>--strict</c>).</summary>
        public const int Success = 0;

        /// <summary>Done, but part of the input could not be read (error diagnostics, or warnings with <c>--strict</c>).</summary>
        public const int Damaged = 1;

        /// <summary>The command line is wrong.</summary>
        public const int Usage = 2;

        /// <summary>The input is not a recognised format or is unusable as a whole.</summary>
        public const int Unreadable = 3;

        /// <summary>Reading the input or writing the output failed at the file-system level.</summary>
        public const int IoError = 4;

        /// <summary>A Mac path names nothing, or starts with no host file.</summary>
        public const int NotFound = 5;

        /// <summary>The command is specified but not built yet.</summary>
        public const int NotImplemented = 70;
    }
}
