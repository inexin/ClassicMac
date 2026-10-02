using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Code.Disassembly;

/// <summary>One decoded 68k instruction, or a <c>dc.w</c>/<c>dc.b</c> for a word that is not one.</summary>
/// <param name="Address">The address of the instruction's first word.</param>
/// <param name="Length">The instruction's length in bytes (2 for a <c>dc.w</c>, 1 for a <c>dc.b</c>).</param>
/// <param name="Words">The instruction's words as they are in the code (empty for a <c>dc.b</c>).</param>
/// <param name="Mnemonic">The mnemonic without its size suffix: <c>move</c>, <c>divsl</c>, <c>bne</c>,
/// <c>cpusha</c>, <c>aline</c> for an A-line trap word, <c>dc</c> for data.</param>
/// <param name="Size">The operation size (the suffix <c>.b</c>, <c>.w</c>, <c>.l</c>, <c>.s</c>, <c>.d</c>,
/// <c>.x</c>, <c>.p</c>; for a branch, <see cref="M68kSize.Byte"/> is the short form), or
/// <see cref="M68kSize.None"/>.</param>
/// <param name="Operands">The operands in Motorola order, source first.</param>
/// <param name="Flags">What the instruction does to the flow of control, and its kind.</param>
/// <param name="References">The addresses the instruction refers to: branch and call targets and PC-relative
/// data.</param>
/// <param name="Text">The instruction in Motorola syntax (<c>move.w #1,-(sp)</c>), followed by
/// <see cref="Comment"/> after <c>"  ; "</c> when there is one.</param>
/// <param name="Comment">A note on an operand: an immediate's characters (<c>'CODE'</c>) or a PC-relative
/// operand's address; null when there is none.</param>
public sealed record M68kInstruction(uint Address, int Length, IReadOnlyList<ushort> Words, string Mnemonic,
    M68kSize Size, IReadOnlyList<M68kOperand> Operands, M68kFlags Flags, IReadOnlyList<M68kReference> References,
    string Text, string? Comment)
{
    /// <summary>The A-line trap word (<c>$A000</c>–<c>$AFFF</c>) when the instruction is one, else null.</summary>
    public ushort? TrapWord => (Flags & M68kFlags.ALine) != 0 ? Words[0] : null;

    /// <summary>True when the bytes did not decode as an instruction (<c>dc.w</c>/<c>dc.b</c>).</summary>
    public bool IsInvalid => (Flags & M68kFlags.Invalid) != 0;

    /// <summary>The instruction's text.</summary>
    /// <returns><see cref="Text"/>.</returns>
    public override string ToString() => Text;
}

/// <summary>An operation size: the suffix of a 68k or FPU mnemonic.</summary>
public enum M68kSize
{
    /// <summary>No size (<c>rts</c>, <c>lea</c>).</summary>
    None,
    /// <summary>Byte (<c>.b</c>); for a branch, the 8-bit displacement form (<c>.s</c>).</summary>
    Byte,
    /// <summary>Word (<c>.w</c>).</summary>
    Word,
    /// <summary>Long (<c>.l</c>).</summary>
    Long,
    /// <summary>FPU single precision (<c>.s</c>).</summary>
    Single,
    /// <summary>FPU double precision (<c>.d</c>).</summary>
    Double,
    /// <summary>FPU extended precision (<c>.x</c>).</summary>
    Extended,
    /// <summary>FPU packed decimal (<c>.p</c>).</summary>
    Packed,
}

/// <summary>What an instruction does to the flow of control, and what kind of word it is.</summary>
[Flags]
public enum M68kFlags
{
    /// <summary>An ordinary instruction.</summary>
    None = 0,
    /// <summary>Transfers control: a branch, jump or decrement-and-branch.</summary>
    Branch = 1,
    /// <summary>The branch is conditional: execution may also fall through.</summary>
    Conditional = 2,
    /// <summary>Calls a subroutine (<c>bsr</c>, <c>jsr</c>).</summary>
    Call = 4,
    /// <summary>Returns (<c>rts</c>, <c>rtd</c>, <c>rte</c>, <c>rtr</c>, <c>rtm</c>).</summary>
    Return = 8,
    /// <summary>An A-line word ($Axxx): a Mac OS trap.</summary>
    ALine = 16,
    /// <summary>An F-line word ($Fxxx): a coprocessor (FPU, MMU) or 68040 cache instruction.</summary>
    FLine = 32,
    /// <summary>Not an instruction: shown as <c>dc.w</c> (or <c>dc.b</c> at an odd address or for a last odd
    /// byte).</summary>
    Invalid = 64,
}

/// <summary>What an instruction does with an address it refers to.</summary>
public enum M68kReferenceKind
{
    /// <summary>Branches or jumps there.</summary>
    Branch,
    /// <summary>Calls it.</summary>
    Call,
    /// <summary>Reads or writes data there (a PC-relative operand), or reads a pointer there (PC memory
    /// indirect).</summary>
    Data,
}

/// <summary>An address an instruction refers to.</summary>
/// <param name="Address">The address.</param>
/// <param name="Kind">What the instruction does with it.</param>
public readonly record struct M68kReference(uint Address, M68kReferenceKind Kind);

/// <summary>An operand of a 68k instruction.</summary>
public abstract record M68kOperand;

/// <summary>The kind of a register operand.</summary>
public enum M68kRegisterKind
{
    /// <summary>A data register, D0–D7.</summary>
    Data,
    /// <summary>An address register, A0–A7 (A7 is SP).</summary>
    Address,
    /// <summary>An FPU data register, FP0–FP7.</summary>
    FloatingPoint,
    /// <summary>The status register SR.</summary>
    StatusRegister,
    /// <summary>The condition code register CCR.</summary>
    ConditionCodes,
    /// <summary>The user stack pointer USP.</summary>
    UserStackPointer,
    /// <summary>A control register of <c>movec</c>; the number is its 12-bit code ($002 CACR, $801 VBR ...).</summary>
    Control,
    /// <summary>An FPU control register; the number is its bit in the register-select field (4 FPCR, 2 FPSR,
    /// 1 FPIAR).</summary>
    FloatingPointControl,
    /// <summary>A 68040 cache of <c>cinv</c>/<c>cpush</c>: 1 DC, 2 IC, 3 BC (both).</summary>
    Cache,
}

/// <summary>A register.</summary>
/// <param name="Kind">The register's kind.</param>
/// <param name="Number">Its number within the kind.</param>
public sealed record M68kRegisterOperand(M68kRegisterKind Kind, int Number) : M68kOperand;

/// <summary>Two registers written <c>Dh:Dl</c> (a 64-bit product or dividend, <c>cas2</c>'s compare and update
/// registers), or two memory operands written <c>(Rn1):(Rn2)</c> (<c>cas2</c>'s addresses).</summary>
/// <param name="First">The first register.</param>
/// <param name="Second">The second register.</param>
/// <param name="Indirect">True when the registers hold the operands' addresses.</param>
public sealed record M68kRegisterPair(M68kRegisterOperand First, M68kRegisterOperand Second, bool Indirect) : M68kOperand;

/// <summary>The registers a register list can hold.</summary>
public enum M68kRegisterListKind
{
    /// <summary><c>movem</c>: bit n is Dn for n &lt; 8 and A(n−8) above.</summary>
    Integer,
    /// <summary><c>fmovem</c> data registers: bit n is FPn.</summary>
    FloatingPoint,
    /// <summary><c>fmovem</c> control registers: 4 FPCR, 2 FPSR, 1 FPIAR.</summary>
    FloatingPointControl,
}

/// <summary>A register list, normalized so that bit n is register n whatever order the instruction's mask uses.</summary>
/// <param name="Mask">The registers.</param>
/// <param name="Kind">Which registers the bits name.</param>
public sealed record M68kRegisterList(ushort Mask, M68kRegisterListKind Kind) : M68kOperand;

/// <summary>An immediate operand.</summary>
/// <param name="Value">The value as stored, zero-extended (a byte immediate is the low byte of its word); for an
/// 8-byte FPU immediate, its 64 bits; for a 12-byte one, 0 (see <paramref name="Bytes"/>).</param>
/// <param name="Size">The operand's size.</param>
/// <param name="Bytes">The immediate's bytes as stored in the instruction (a byte immediate's whole word).</param>
public sealed record M68kImmediate(long Value, M68kSize Size, IReadOnlyList<byte> Bytes) : M68kOperand
{
    /// <summary>Compares the value, size and bytes.</summary>
    /// <param name="other">The other immediate.</param>
    /// <returns>True when they are the same.</returns>
    public bool Equals(M68kImmediate? other) =>
        other is not null && Value == other.Value && Size == other.Size && Bytes.SequenceEqual(other.Bytes);

    /// <summary>A hash of the value and size.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode() => HashCode.Combine(Value, Size);
}

/// <summary>A branch target: the address a relative branch goes to.</summary>
/// <param name="Address">The target address.</param>
public sealed record M68kBranchTarget(uint Address) : M68kOperand;

/// <summary>A bit field <c>{offset:width}</c>, written after the effective address it is in.</summary>
/// <param name="Offset">The offset, or the number of the data register holding it.</param>
/// <param name="OffsetIsRegister">True when the offset is in a data register.</param>
/// <param name="Width">The width (1–32), or the number of the data register holding it.</param>
/// <param name="WidthIsRegister">True when the width is in a data register.</param>
public sealed record M68kBitField(int Offset, bool OffsetIsRegister, int Width, bool WidthIsRegister) : M68kOperand;

/// <summary>A memory addressing mode.</summary>
public enum M68kAddressingMode
{
    /// <summary><c>(An)</c></summary>
    Indirect,
    /// <summary><c>(An)+</c></summary>
    PostIncrement,
    /// <summary><c>-(An)</c></summary>
    PreDecrement,
    /// <summary><c>d16(An)</c></summary>
    Displacement,
    /// <summary><c>d8(An,Xn)</c> or, with a full extension word, <c>(bd,An,Xn)</c>.</summary>
    Indexed,
    /// <summary><c>([bd,An],Xn,od)</c>: the index is added after the pointer is read.</summary>
    MemoryIndirectPostIndexed,
    /// <summary><c>([bd,An,Xn],od)</c>: the index is added before the pointer is read.</summary>
    MemoryIndirectPreIndexed,
    /// <summary><c>(xxx).w</c>, sign-extended.</summary>
    AbsoluteShort,
    /// <summary><c>(xxx).l</c></summary>
    AbsoluteLong,
    /// <summary><c>d16(PC)</c></summary>
    PcDisplacement,
    /// <summary><c>d8(PC,Xn)</c> or <c>(bd,PC,Xn)</c>.</summary>
    PcIndexed,
    /// <summary><c>([bd,PC],Xn,od)</c></summary>
    PcMemoryIndirectPostIndexed,
    /// <summary><c>([bd,PC,Xn],od)</c></summary>
    PcMemoryIndirectPreIndexed,
}

/// <summary>An index register with its size and scale.</summary>
/// <param name="Register">The register (a data or address register).</param>
/// <param name="IsLong">True for <c>.l</c>, false for the sign-extended low word <c>.w</c>.</param>
/// <param name="Scale">1, 2, 4 or 8.</param>
public readonly record struct M68kIndex(M68kRegisterOperand Register, bool IsLong, int Scale);

/// <summary>A memory operand.</summary>
/// <param name="Mode">The addressing mode.</param>
/// <param name="Register">The base address register's number (0–7); unused for the absolute and PC modes.</param>
/// <param name="BaseDisplacement">The displacement (base displacement for the 68020 forms); for the absolute modes,
/// the address.</param>
/// <param name="Index">The index register, or null when there is none or it is suppressed.</param>
/// <param name="OuterDisplacement">The memory-indirect modes' outer displacement.</param>
/// <param name="BaseSuppressed">A full extension word suppresses the base register (or PC).</param>
/// <param name="FullExtension">The operand uses a 68020 full extension word.</param>
/// <param name="Address">The address the operand reads when it is fixed by the instruction alone (the absolute and
/// PC-relative modes without an index); for the memory-indirect modes, the address of the pointer. Null when it
/// depends on a register.</param>
public sealed record M68kEffectiveAddress(M68kAddressingMode Mode, int Register, int BaseDisplacement,
    M68kIndex? Index, int OuterDisplacement, bool BaseSuppressed, bool FullExtension, uint? Address) : M68kOperand
{
    /// <summary>True for the PC-relative modes.</summary>
    public bool IsPcRelative => Mode >= M68kAddressingMode.PcDisplacement;
}
