using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc;

/// <summary>
/// A traceback table: the AIX-format record the Mac OS PowerPC compilers (MPW, CodeWarrior) put after a function's
/// final <c>blr</c>, which carries the function's name for debuggers [Doc: Mac OS Runtime Architectures; the layout is
/// AIX's tbtable.h]. It is a zero word, 8 flag bytes, then the optional fields in this order: parminfo (when there are
/// fixed or floating-point parameters), tb_offset, hand_mask, ctl_info (a count and that many words), the name (a
/// 16-bit length and the characters) and alloca_reg. The AIX vector extension and extension table that may follow are
/// not read (reported as <c>traceback.extension-unread</c>).
/// </summary>
public sealed record TracebackTable
{
    private const uint Blr = 0x4E800020;

    /// <summary>The offset of the table's zero word in the code.</summary>
    public int Offset { get; init; }

    /// <summary>The table's length in bytes, from the zero word to the end of its last field read (alloca_reg at the
    /// latest).</summary>
    public int Length { get; init; }

    /// <summary>The 8 flag bytes as a big-endian number: version, language, then the bit fields.</summary>
    public ulong Flags { get; init; }

    /// <summary>The format version (byte 0), 0 for every table the scan accepts.</summary>
    public byte Version => (byte)(Flags >> 56);

    /// <summary>The source language (byte 1): 0 C, 1 FORTRAN, 2 Pascal, 9 C++ ...</summary>
    public byte Language => (byte)(Flags >> 48);

    /// <summary>The table has tb_offset (byte 2, bit 0x20).</summary>
    public bool HasTbOffset => (Flags & 0x0000_2000_0000_0000) != 0;

    /// <summary>The table has ctl_info, the controlled-storage displacements (byte 2, bit 0x08).</summary>
    public bool HasControlledStorage => (Flags & 0x0000_0800_0000_0000) != 0;

    /// <summary>The function is an interrupt handler and the table has hand_mask (byte 3, bit 0x80).</summary>
    public bool IsInterruptHandler => (Flags & 0x0000_0080_0000_0000) != 0;

    /// <summary>The table has the function's name (byte 3, bit 0x40).</summary>
    public bool HasName => (Flags & 0x0000_0040_0000_0000) != 0;

    /// <summary>The function uses alloca and the table has alloca_reg (byte 3, bit 0x20).</summary>
    public bool UsesAlloca => (Flags & 0x0000_0020_0000_0000) != 0;

    /// <summary>The table has the vector extension after alloca_reg (byte 5, bit 0x40); it is not read.</summary>
    public bool HasVectorInfo => (Flags & 0x0000_0000_0040_0000) != 0;

    /// <summary>The table has the extension table byte after the vector extension (byte 5, bit 0x80); it is not
    /// read.</summary>
    public bool HasExtensionTable => (Flags & 0x0000_0000_0080_0000) != 0;

    /// <summary>The number of fixed-point parameters (byte 6).</summary>
    public int FixedParameterCount => (int)(Flags >> 8) & 0xFF;

    /// <summary>The number of floating-point parameters (byte 7, bits 1–7).</summary>
    public int FloatParameterCount => (int)(Flags >> 1) & 0x7F;

    /// <summary>parminfo: two bits per parameter giving its kind, or null when there are no parameters.</summary>
    public uint? ParameterInfo { get; init; }

    /// <summary>tb_offset: the distance from the function's first instruction to the zero word.</summary>
    public uint? TbOffset { get; init; }

    /// <summary>hand_mask: the interrupts an interrupt handler handles.</summary>
    public uint? HandlerMask { get; init; }

    /// <summary>ctl_info: the controlled-storage displacements.</summary>
    public IReadOnlyList<uint> ControlledStorage { get; init; } = [];

    /// <summary>The function's name (Mac Roman), or null.</summary>
    public string? Name { get; init; }

    /// <summary>alloca_reg: the register holding the alloca'd frame.</summary>
    public byte? AllocaRegister { get; init; }

    /// <summary>The offset of the function's first instruction: <see cref="Offset"/> − <see cref="TbOffset"/>, or null
    /// without a tb_offset or when it points before the code.</summary>
    public int? FunctionStart { get; init; }

    /// <summary>
    /// Reads the table whose zero word is at <paramref name="offset"/>. Returns null when the word there is not zero
    /// (not a table) or when the table runs past the end of the code (reported as <c>traceback.truncated</c>). A
    /// tb_offset that points before the code is reported as <c>traceback.bad-offset</c> and leaves
    /// <see cref="FunctionStart"/> null; vector or extension fields are reported as <c>traceback.extension-unread</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside the code.</exception>
    public static TracebackTable? Read(ReadOnlyMemory<byte> code, int offset, ICollection<Diagnostic> diagnostics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, code.Length);
        return Read(new BigEndianReader(code), offset, diagnostics);
    }

    /// <summary>
    /// Finds the tables in a code section: every zero word that follows a <c>blr</c> (4E800020), is word-aligned and
    /// has a version byte of 0 and its 8 flag bytes inside the code [Verified: Disk Copy 6.5, MathLib]. A table that
    /// runs past the end is reported and left out.
    /// </summary>
    public static IReadOnlyList<TracebackTable> Find(ReadOnlyMemory<byte> code, ICollection<Diagnostic> diagnostics)
    {
        var reader = new BigEndianReader(code);
        var tables = new List<TracebackTable>();
        for (int offset = 4; offset <= code.Length - 12; offset += 4)
        {
            if (reader.ReadUInt32At(offset) != 0 || reader.ReadUInt32At(offset - 4) != Blr || reader.ReadByteAt(offset + 4) != 0)
            {
                continue;
            }

            if (Read(reader, offset, diagnostics) is { } table)
            {
                tables.Add(table);
            }
        }
        return tables;
    }

    private static TracebackTable? Read(BigEndianReader reader, int offset, ICollection<Diagnostic> diagnostics)
    {
        reader.Position = offset;
        if (!reader.TryReadUInt32(out uint zero))
        {
            return Truncated();
        }

        if (zero != 0)
        {
            return null;
        }

        if (!reader.TryReadUInt64(out ulong flags))
        {
            return Truncated();
        }

        var table = new TracebackTable { Offset = offset, Flags = flags };

        uint? parameterInfo = null, tbOffset = null, handlerMask = null;
        if (table.FixedParameterCount != 0 || table.FloatParameterCount != 0)
        {
            if (!reader.TryReadUInt32(out uint p))
            {
                return Truncated();
            }

            parameterInfo = p;
        }
        if (table.HasTbOffset)
        {
            if (!reader.TryReadUInt32(out uint t))
            {
                return Truncated();
            }

            tbOffset = t;
        }
        if (table.IsInterruptHandler)
        {
            if (!reader.TryReadUInt32(out uint h))
            {
                return Truncated();
            }

            handlerMask = h;
        }
        uint[] controlled = [];
        if (table.HasControlledStorage)
        {
            if (!reader.TryReadUInt32(out uint count))
            {
                return Truncated();
            }

            if (count > (uint)reader.Remaining / 4)
            {
                return Truncated();
            }

            controlled = new uint[count];
            for (int i = 0; i < controlled.Length; i++)
            {
                controlled[i] = reader.ReadUInt32();
            }
        }
        string? name = null;
        if (table.HasName)
        {
            if (!reader.TryReadUInt16(out ushort length) || length > reader.Remaining)
            {
                return Truncated();
            }

            name = MacRoman.Decode(reader.ReadBytes(length));
        }
        byte? allocaRegister = null;
        if (table.UsesAlloca)
        {
            if (!reader.TryReadByte(out byte r))
            {
                return Truncated();
            }

            allocaRegister = r;
        }

        // has_vec and has_ext_table add the vector extension and an extension byte after alloca_reg [Doc: AIX
        // sys/debug.h]. Whether a Mac OS compiler sets them is not known, so they are reported, not read.
        if (table.HasVectorInfo || table.HasExtensionTable)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "traceback.extension-unread",
                $"The traceback table at 0x{offset:X} has vector or extension fields after alloca_reg, not read.",
                offset));
        }

        int? functionStart = null;
        if (tbOffset is { } tb)
        {
            if (tb > (uint)offset)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "traceback.bad-offset",
                    $"The traceback table at 0x{offset:X} gives tb_offset 0x{tb:X}, before the start of the code.", offset));
            }
            else
            {
                functionStart = offset - (int)tb;
            }
        }
        return table with
        {
            Length = reader.Position - offset,
            ParameterInfo = parameterInfo,
            TbOffset = tbOffset,
            HandlerMask = handlerMask,
            ControlledStorage = controlled,
            Name = name,
            AllocaRegister = allocaRegister,
            FunctionStart = functionStart,
        };

        TracebackTable? Truncated()
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "traceback.truncated",
                $"The traceback table at 0x{offset:X} runs past the end of the code.", offset));
            return null;
        }
    }
}
