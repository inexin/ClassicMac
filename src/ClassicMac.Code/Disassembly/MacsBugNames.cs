using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>How a MacsBug procedure name is encoded.</summary>
public enum MacsBugNameForm
{
    /// <summary>A length byte <c>$80</c> + n (n 1–31), or <c>$80</c> then a length byte; the name; a pad byte to an
    /// even offset; then the size of the literals that follow.</summary>
    Variable,
    /// <summary>8 characters; bit 7 of the first may be set.</summary>
    Fixed8,
    /// <summary>16 characters, the method then the class, bit 7 of the first and second set.</summary>
    Fixed16,
}

/// <summary>A MacsBug procedure name: the symbol a compiler puts after a routine's return.</summary>
/// <param name="ReturnOffset">The offset of the return (<c>rts</c>, <c>jmp (a0)</c> or <c>rtd #n</c>) it follows.</param>
/// <param name="Offset">Where the name's encoding starts.</param>
/// <param name="Length">The encoding's length: the length bytes, the name, the pad byte and the literal-size word of
/// the variable form; 8 or 16 for the fixed forms.</param>
/// <param name="LiteralSize">The bytes of constants after the encoding (variable form only).</param>
/// <param name="Name">The name: trailing spaces of a fixed form removed, a 16-character name as
/// <c>Class.Method</c>.</param>
/// <param name="Form">The encoding.</param>
public sealed record MacsBugName(int ReturnOffset, int Offset, int Length, int LiteralSize, string Name, MacsBugNameForm Form)
{
    /// <summary>Where the next routine can start: after the encoding and the literals, at an even offset (code is
    /// word-aligned).</summary>
    public int End => (Offset + Length + LiteralSize + 1) & ~1;
}

/// <summary>
/// Finds MacsBug procedure names in 68k code [Doc: MacsBug Reference and Debugging Guide, "Procedure names"]: right
/// after an <c>rts</c> (<c>$4E75</c>), <c>jmp (a0)</c> (<c>$4ED0</c>) or <c>rtd #n</c> (<c>$4E74 nnnn</c>), in one of
/// three encodings, with the characters <c>[A-Za-z0-9_%. ]</c>. docs/formats/output/disassembly.md §2.3.
/// </summary>
public static class MacsBugNames
{
    /// <summary>Every name in <paramref name="code"/> that follows a return word at an even offset.</summary>
    public static IReadOnlyList<MacsBugName> Find(ReadOnlyMemory<byte> code)
    {
        var names = new List<MacsBugName>();
        var reader = new BigEndianReader(code);
        for (int p = 0; p <= reader.Length - 2; p += 2)
        {
            ushort word = reader.ReadUInt16At(p);
            int at = word is Rts or JmpA0 ? p + 2 : word == Rtd ? p + 4 : -1;
            if (at >= 0 && Read(reader, at, p) is { } name)
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>The name encoded at <paramref name="offset"/>, or null when the bytes there are not one.</summary>
    /// <param name="code">The code.</param>
    /// <param name="offset">Where the encoding would start.</param>
    /// <param name="returnOffset">The return it follows, for <see cref="MacsBugName.ReturnOffset"/>.</param>
    public static MacsBugName? Read(ReadOnlyMemory<byte> code, int offset, int returnOffset) =>
        Read(new BigEndianReader(code), offset, returnOffset);

    private const ushort Rts = 0x4E75, JmpA0 = 0x4ED0, Rtd = 0x4E74;

    private static MacsBugName? Read(BigEndianReader reader, int offset, int returnOffset)
    {
        var data = reader.Source.Span;
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        byte first = data[offset];
        if (first is >= 0x80 and <= 0x9F)
        {
            int length = first & 0x1F;
            int q = offset + 1;
            if (length == 0)
            {
                if (q >= data.Length)
                {
                    return null;
                }

                length = data[q++];
            }
            if (length == 0 || length > data.Length - q || !IsName(data.Slice(q, length)))
            {
                return null;
            }

            string name = Latin(data.Slice(q, length));
            q += length;
            if ((q & 1) != 0)
            {
                q++;
            }
            // A word that would put the literals past the end of the code (or no word) is not a literal size: the
            // name ends at the pad [Fitted: Disk Copy 6.1.2's CODE 1, where code follows %__MAIN at $696].
            int literals = q <= data.Length - 2 ? reader.ReadUInt16At(q) : int.MaxValue;
            if (literals > data.Length - (q + 2))
            {
                return new MacsBugName(returnOffset, offset, Math.Min(q, data.Length) - offset, 0, name, MacsBugNameForm.Variable);
            }

            return new MacsBugName(returnOffset, offset, q + 2 - offset, literals, name, MacsBugNameForm.Variable);
        }
        if (first < 0x80)
        {
            return ReadUnmarked(reader, offset, returnOffset);
        }

        if (first < 0xA0 || !IsNameChar((byte)(first & 0x7F)))
        {
            return null;
        }

        bool wide = offset + 1 < data.Length && data[offset + 1] >= 0x80;
        int n = wide ? 16 : 8;
        if (n > data.Length - offset)
        {
            return null;
        }

        Span<byte> chars = stackalloc byte[16];
        data.Slice(offset, n).CopyTo(chars);
        chars[0] &= 0x7F;
        chars[1] &= 0x7F;
        // A name that starts with a space is none (nor one of spaces only) [ClassicMac].
        if (!IsName(chars[..n]) || chars[0] == (byte)' ')
        {
            return null;
        }

        if (!wide)
        {
            return new MacsBugName(returnOffset, offset, 8, 0, Latin(chars[..8]).TrimEnd(' '), MacsBugNameForm.Fixed8);
        }
        // The method's 8 characters, then the class's; MacsBug shows Class.Method.
        string method = Latin(chars[..8]).TrimEnd(' ');
        string cls = Latin(chars[8..16]).Trim(' ');
        return new MacsBugName(returnOffset, offset, 16, 0, cls.Length == 0 ? method : cls + "." + method,
            MacsBugNameForm.Fixed16);
    }

    // A fixed-8 name whose first character has bit 7 clear. [Fitted: Realmz 7.1.2, CODE 1; and against the text
    // after returns in ResEdit 2.1.3 and the Mac OS 9 System file] It is upper case: a letter or '_', then letters,
    // digits and '_', then spaces to 8 characters; and the word after it does not continue the text: the code ends,
    // a link (the next routine, $4E50–$4E57), or a first byte that is not one of those characters.
    private static MacsBugName? ReadUnmarked(BigEndianReader reader, int offset, int returnOffset)
    {
        var data = reader.Source.Span;
        if (8 > data.Length - offset)
        {
            return null;
        }

        var chars = data.Slice(offset, 8);
        if (!IsUpperStart(chars[0]))
        {
            return null;
        }

        int i = 1;
        while (i < 8 && IsUpperChar(chars[i]))
        {
            i++;
        }

        for (int j = i; j < 8; j++)
        {
            if (chars[j] != (byte)' ')
            {
                return null;
            }
        }

        int next = offset + 8;
        if (next <= data.Length - 2)
        {
            int word = reader.ReadUInt16At(next);
            bool link = (word & 0xFFF8) == 0x4E50;
            if (!link && (IsUpperChar(data[next]) || data[next] == (byte)' '))
            {
                return null;
            }
        }
        return new MacsBugName(returnOffset, offset, 8, 0, Latin(chars[..i]), MacsBugNameForm.Fixed8);
    }

    private static bool IsUpperStart(byte c) => c is (>= (byte)'A' and <= (byte)'Z') or (byte)'_';

    private static bool IsUpperChar(byte c) => IsUpperStart(c) || c is >= (byte)'0' and <= (byte)'9';

    private static bool IsName(ReadOnlySpan<byte> chars)
    {
        foreach (byte c in chars)
        {
            if (!IsNameChar(c))
            {
                return false;
            }
        }

        return true;
    }

    // [A-Za-z0-9_%. ] [Doc: MacsBug Reference and Debugging Guide, "Procedure names"]
    private static bool IsNameChar(byte c) =>
        c is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'_' or (byte)'%' or (byte)'.' or (byte)' ';

    private static string Latin(ReadOnlySpan<byte> chars) => System.Text.Encoding.ASCII.GetString(chars);
}
