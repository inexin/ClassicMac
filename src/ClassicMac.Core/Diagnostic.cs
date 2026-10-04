namespace ClassicMac.Core;

/// <summary>How serious a <see cref="Diagnostic"/> is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Worth knowing; the result is complete.</summary>
    Info,
    /// <summary>Something unusual was tolerated; the result may differ from what the Mac would have done.</summary>
    Warning,
    /// <summary>Part of the input could not be read and is missing from the result.</summary>
    Error,
}

/// <summary>
/// A problem found while reading. Readers report damaged input here instead of throwing, and throw only when the
/// input is unusable as a whole.
/// </summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">A stable identifier, such as <c>resource.data-out-of-range</c>, for filtering and tests.</param>
/// <param name="Message">A description for people.</param>
/// <param name="Offset">Where in the input it was found, if known.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, long? Offset = null)
{
    /// <summary>
    /// Which file inside the input it is about, when not the input itself: the Mac path of the nested file whose
    /// contents were being read, after the files that hold it (<c>Disks:Tools.img &gt; Read Me</c>). The offset
    /// counts within that file.
    /// </summary>
    public string? Location { get; init; }
}
