using System;
using System.Collections.Generic;

namespace ClassicMac.Code.Disassembly;

/// <summary>What kind of value a PowerPC operand holds.</summary>
public enum PpcOperandKind
{
    /// <summary>A general-purpose register, r0–r31.</summary>
    Gpr,
    /// <summary>A floating-point register, f0–f31.</summary>
    Fpr,
    /// <summary>An AltiVec vector register, v0–v31.</summary>
    Vr,
    /// <summary>A condition register field, cr0–cr7.</summary>
    CrField,
    /// <summary>A condition register bit, 0–31 (printed <c>eq</c>, <c>4*cr1+gt</c> ...).</summary>
    CrBit,
    /// <summary>A special-purpose register by number (printed by name where it has one: <c>lr</c>, <c>ctr</c>,
    /// <c>xer</c>, <c>srr0</c> ...).</summary>
    Spr,
    /// <summary>An immediate value: a signed or unsigned number, a mask, a shift, a TO field, a segment register
    /// number, or the literal 0 that an rA field of 0 means in an address.</summary>
    Immediate,
    /// <summary>A memory operand <c>d(rA)</c>: <see cref="PpcOperand.Value"/> is the displacement and
    /// <see cref="PpcOperand.Base"/> the base register (0 meaning the literal 0, not r0).</summary>
    Displacement,
    /// <summary>A branch target address.</summary>
    BranchTarget,
}

/// <summary>One operand of a <see cref="PpcInstruction"/>.</summary>
/// <param name="Kind">What the operand is.</param>
/// <param name="Value">The register, field or bit number, the immediate, the displacement or the target address.</param>
/// <param name="Base">For <see cref="PpcOperandKind.Displacement"/>, the base register (0 for the literal 0).</param>
/// <param name="Hex">For <see cref="PpcOperandKind.Immediate"/>, true when the value prints in hexadecimal (logical
/// immediates and masks) rather than decimal.</param>
public readonly record struct PpcOperand(PpcOperandKind Kind, long Value, int Base = 0, bool Hex = false)
{
    /// <summary>The operand in assembler syntax: <c>r3</c>, <c>f1</c>, <c>v2</c>, <c>cr7</c>, <c>4*cr1+eq</c>,
    /// <c>lr</c>, <c>-1</c>, <c>0xFF</c>, <c>8(r1)</c>, <c>0x1008</c>.</summary>
    public override string ToString() => Kind switch
    {
        PpcOperandKind.Gpr => "r" + Value,
        PpcOperandKind.Fpr => "f" + Value,
        PpcOperandKind.Vr => "v" + Value,
        PpcOperandKind.CrField => "cr" + Value,
        PpcOperandKind.CrBit => CrBitName((int)Value),
        PpcOperandKind.Spr => PpcDisassembler.SprName((int)Value) ?? Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PpcOperandKind.Immediate => Hex ? "0x" + Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
            : Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PpcOperandKind.Displacement => Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (Base == 0 ? "(0)" : $"(r{Base})"),
        PpcOperandKind.BranchTarget => "0x" + Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(),
    };

    private static readonly string[] BitNames = ["lt", "gt", "eq", "so"];

    // A CR bit as the extended mnemonics write it: lt/gt/eq/so in cr0, 4*crN+xx elsewhere.
    private static string CrBitName(int bit) =>
        bit < 4 ? BitNames[bit] : $"4*cr{bit >> 2}+{BitNames[bit & 3]}";
}

/// <summary>How an instruction changes the flow of control.</summary>
[Flags]
public enum PpcFlow
{
    /// <summary>Falls through to the next instruction.</summary>
    None = 0,
    /// <summary>A branch: b, bc, bclr or bcctr.</summary>
    Branch = 1,
    /// <summary>The branch sets LR (LK = 1): a call.</summary>
    Call = 2,
    /// <summary>A branch to LR that does not set it: blr and its conditional forms.</summary>
    Return = 4,
    /// <summary>The branch depends on a CR bit or on CTR.</summary>
    Conditional = 8,
}

/// <summary>One decoded PowerPC instruction.</summary>
/// <param name="Address">The address the word was decoded at.</param>
/// <param name="Word">The instruction word.</param>
/// <param name="Mnemonic">The mnemonic, an extended one where it applies (<c>mflr</c>, <c>li</c>, <c>beq+</c>), or
/// <c>.long</c> for a word that is not a 32-bit PowerPC or AltiVec instruction.</param>
/// <param name="Operands">The operands in assembler order.</param>
/// <param name="Flow">Whether it branches, calls, returns, conditionally.</param>
/// <param name="Target">The branch target of b and bc; null for everything else, bclr and bcctr included.</param>
public sealed record PpcInstruction(uint Address, uint Word, string Mnemonic, IReadOnlyList<PpcOperand> Operands,
    PpcFlow Flow, uint? Target)
{
    /// <summary>The instruction in assembler syntax: the mnemonic, then the operands separated by commas
    /// (<c>stw r0,8(r1)</c>).</summary>
    public string Text => Operands.Count == 0 ? Mnemonic : Mnemonic + " " + string.Join(",", Operands);

    /// <summary>False for a <c>.long</c>: a word that does not decode.</summary>
    public bool IsValid => Mnemonic != ".long";

    /// <summary>The instruction is a branch.</summary>
    public bool IsBranch => (Flow & PpcFlow.Branch) != 0;

    /// <summary>The branch sets LR.</summary>
    public bool IsCall => (Flow & PpcFlow.Call) != 0;

    /// <summary>The branch returns through LR.</summary>
    public bool IsReturn => (Flow & PpcFlow.Return) != 0;

    /// <summary>The branch is conditional.</summary>
    public bool IsConditional => (Flow & PpcFlow.Conditional) != 0;

    /// <inheritdoc/>
    public override string ToString() => $"{Address:X8}  {Word:X8}  {Text}";
}
