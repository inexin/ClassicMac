namespace ClassicMac.Code.Disassembly;

/// <summary>Why a listing starts a function at an address.</summary>
public enum CodeFunctionSource
{
    /// <summary>An entry point the caller gave, or the code resource's start.</summary>
    Entry,
    /// <summary>A jump-table entry into the segment.</summary>
    JumpTable,
    /// <summary>A driver routine (open, prime, control, status, close).</summary>
    DriverRoutine,
    /// <summary>A routine of a package's dispatch table.</summary>
    PackageRoutine,
    /// <summary>The code before a MacsBug procedure name.</summary>
    MacsBug,
    /// <summary>The target of a call.</summary>
    Call,
    /// <summary>The start of code no entry reaches, found by the sweep of the gaps.</summary>
    Gap,
    /// <summary>An exported symbol of a fragment.</summary>
    Export,
    /// <summary>A function a traceback table names.</summary>
    Traceback,
    /// <summary>The fragment's main entry point.</summary>
    Main,
    /// <summary>The fragment's initialization routine.</summary>
    Init,
    /// <summary>The fragment's termination routine.</summary>
    Term,
    /// <summary>A cross-fragment glue stub that calls an import.</summary>
    Glue,
}

/// <summary>A function a listing labels.</summary>
/// <param name="Section">The PEF section of a fragment's function; 0 for 68k code.</param>
/// <param name="Offset">Where it starts: the offset in the resource (68k) or the section (PowerPC).</param>
/// <param name="Name">Its label.</param>
/// <param name="Source">Why it is a function.</param>
public sealed record CodeFunction(int Section, uint Offset, string Name, CodeFunctionSource Source);
