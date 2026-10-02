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

/// <summary>What an annotation in a listing says about an instruction.</summary>
public enum CodeReferenceKind
{
    /// <summary>A call or reference through the jump table (<c>jsr n(A5)</c>): the segment and offset it reaches.</summary>
    JumpTable,
    /// <summary>An A5-relative global or application parameter (<c>A5-$1F3A</c>).</summary>
    A5Global,
    /// <summary>A low-memory global by its absolute address.</summary>
    LowMemory,
    /// <summary>An A-line trap (the mnemonic already names it; the listing does not repeat it).</summary>
    Trap,
    /// <summary>The routine a dispatcher trap's selector names.</summary>
    Selector,
    /// <summary>An operand the loader relocates: what it is relative to.</summary>
    Relocation,
    /// <summary>A Pascal or C string a PC-relative operand points at.</summary>
    String,
    /// <summary>A call or branch to a labelled function.</summary>
    Call,
    /// <summary>A call through a cross-fragment glue stub: the import it reaches.</summary>
    Glue,
    /// <summary>A load from the TOC: what the slot holds.</summary>
    TocSlot,
}

/// <summary>An annotation: what an instruction refers to.</summary>
/// <param name="Section">The PEF section of a fragment's instruction; 0 for 68k code.</param>
/// <param name="Offset">The instruction's offset in the resource (68k) or the section (PowerPC).</param>
/// <param name="Kind">What it says.</param>
/// <param name="Text">The annotation as the listing writes it.</param>
public sealed record CodeReference(int Section, uint Offset, CodeReferenceKind Kind, string Text);
