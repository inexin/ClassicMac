using System;
using System.Collections.Generic;
using static ClassicMac.Files.Hfs.HfsPlusUnicodeData;

namespace ClassicMac.Files.Hfs;

/// <summary>Validates HFS Plus names using the fixed Unicode 3.2 decomposition rules.</summary>
internal static class HfsPlusUnicodeNormalization
{
    public static bool IsCanonical(ReadOnlySpan<byte> bigEndianName, bool usePostJaguarFixups = false)
    {
        var decomposed = new List<int>(bigEndianName.Length / 2);
        for (int offset = 0; offset < bigEndianName.Length; offset += 2)
        {
            int codePoint = (bigEndianName[offset] << 8) | bigEndianName[offset + 1];
            if (char.IsHighSurrogate((char)codePoint))
            {
                if (offset + 3 >= bigEndianName.Length)
                {
                    return false;
                }

                int lowSurrogate = (bigEndianName[offset + 2] << 8) | bigEndianName[offset + 3];
                if (!char.IsLowSurrogate((char)lowSurrogate))
                {
                    return false;
                }

                codePoint = char.ConvertToUtf32((char)codePoint, (char)lowSurrogate);
                offset += 2;
            }
            else if (char.IsLowSurrogate((char)codePoint))
            {
                return false;
            }

            if (!usePostJaguarFixups && (codePoint is 0x0FB2 or 0x0FB3) && offset + 5 < bigEndianName.Length &&
                ((bigEndianName[offset + 2] << 8) | bigEndianName[offset + 3]) == 0x0F80 &&
                ((bigEndianName[offset + 4] << 8) | bigEndianName[offset + 5]) == 0x0F71)
            {
                // Preserve the Unicode 2.1 Tibetan form; HFSX requires Apple's corrected U+0F77/U+0F79 form.
                decomposed.Add(codePoint);
                decomposed.Add(0x0F80);
                decomposed.Add(0x0F71);
                offset += 4;
                continue;
            }
            // TN1150 has no normalization-version field, so HFS+ must retain these Unicode 2.1 spellings.
            if (!usePostJaguarFixups && HasLegacyUnicode21Decomposition(codePoint))
            {
                decomposed.Add(codePoint);
            }
            else
            {
                AppendDecomposition(codePoint, decomposed);
            }
        }

        for (int index = 1; index < decomposed.Count; index++)
        {
            byte currentClass = CombiningClass(decomposed[index]);
            if (currentClass == 0)
            {
                continue;
            }

            int position = index;
            while (position > 0)
            {
                // Preserve the historical order of the corrected Tibetan three-code-point forms.
                if (!usePostJaguarFixups && position >= 2 &&
                    (decomposed[position - 2] is 0x0FB2 or 0x0FB3) &&
                    decomposed[position - 1] == 0x0F80 && decomposed[position] == 0x0F71)
                {
                    break;
                }

                byte previousClass = CombiningClass(decomposed[position - 1]);
                if (previousClass == 0 || previousClass <= currentClass)
                {
                    break;
                }

                (decomposed[position - 1], decomposed[position]) =
                    (decomposed[position], decomposed[position - 1]);
                position--;
            }
        }

        if (usePostJaguarFixups && HasKnownPostJaguarCorrection(decomposed))
        {
            return false;
        }

        int inputOffset = 0;
        foreach (int codePoint in decomposed)
        {
            if (codePoint <= char.MaxValue)
            {
                if (inputOffset + 1 >= bigEndianName.Length ||
                    bigEndianName[inputOffset] != (byte)(codePoint >> 8) ||
                    bigEndianName[inputOffset + 1] != (byte)codePoint)
                {
                    return false;
                }

                inputOffset += 2;
                continue;
            }

            int supplementary = codePoint - 0x10000;
            ushort highSurrogate = (ushort)(0xD800 + (supplementary >> 10));
            ushort lowSurrogate = (ushort)(0xDC00 + (supplementary & 0x3FF));
            if (inputOffset + 3 >= bigEndianName.Length ||
                bigEndianName[inputOffset] != (byte)(highSurrogate >> 8) ||
                bigEndianName[inputOffset + 1] != (byte)highSurrogate ||
                bigEndianName[inputOffset + 2] != (byte)(lowSurrogate >> 8) ||
                bigEndianName[inputOffset + 3] != (byte)lowSurrogate)
            {
                return false;
            }

            inputOffset += 4;
        }

        return inputOffset == bigEndianName.Length;
    }

    private static bool HasKnownPostJaguarCorrection(List<int> codePoints)
    {
        for (int index = 0; index < codePoints.Count; index++)
        {
            // Apple fsck_hfs's FixDecomps corrects these legacy sequences before comparing names.
            if (codePoints[index] == 0x0306 && index + 1 < codePoints.Count && codePoints[index + 1] == 0x0307)
            {
                return true;
            }

            if ((codePoints[index] is 0x00A8 or 0x0308 || IsGreekTonosBase(codePoints[index])) &&
                index + 1 < codePoints.Count && codePoints[index + 1] == 0x030D)
            {
                return true;
            }

            if (codePoints[index] == 0x09AC && index + 1 < codePoints.Count && codePoints[index + 1] == 0x09BC)
            {
                return true;
            }

            // Apple fsck_hfs replaces the obsolete Odia YA + NUKTA sequence with U+0B5F.
            if (codePoints[index] == 0x0B2F && index + 1 < codePoints.Count && codePoints[index + 1] == 0x0B3C)
            {
                return true;
            }

            if (index + 1 < codePoints.Count &&
                (codePoints[index] == 0x0A21 && codePoints[index + 1] == 0x0A3C ||
                 codePoints[index] == 0x0E4D && codePoints[index + 1] == 0x0E32 ||
                 codePoints[index] == 0x0ECD && codePoints[index + 1] == 0x0EB2))
            {
                return true;
            }

            if (index + 2 < codePoints.Count && (codePoints[index] is 0x0FB2 or 0x0FB3) &&
                codePoints[index + 1] == 0x0F80 && codePoints[index + 2] == 0x0F71)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGreekTonosBase(int codePoint) => codePoint is
        0x0391 or 0x0395 or 0x0397 or 0x0399 or 0x039F or 0x03A5 or 0x03A9 or
        0x03B1 or 0x03B5 or 0x03B7 or 0x03B9 or 0x03BF or 0x03C5 or 0x03C9 or 0x03D2;

    private static bool HasLegacyUnicode21Decomposition(int codePoint) => codePoint is
        0x01F8 or 0x01F9 or
        0x0218 or 0x0219 or 0x021A or 0x021B or 0x021E or 0x021F or
        0x0226 or 0x0227 or 0x0228 or 0x0229 or 0x022A or 0x022B or 0x022C or
        0x022D or 0x022E or 0x022F or 0x0230 or 0x0231 or 0x0232 or 0x0233 or
        0x0400 or 0x040D or 0x0450 or 0x045D or 0x04EC or 0x04ED or
        0x0622 or 0x0623 or 0x0624 or 0x0625 or 0x0626 or
        0x06C0 or 0x06C2 or 0x06D3 or
        0x0A33 or 0x0A36 or
        0x0DDA or 0x0DDC or 0x0DDD or 0x0DDE or
        0x1026 or 0xFB1D;

    private static void AppendDecomposition(int codePoint, List<int> output)
    {
        const int hangulSBase = 0xAC00;
        const int hangulLBase = 0x1100;
        const int hangulVBase = 0x1161;
        const int hangulTBase = 0x11A7;
        const int hangulVCount = 21;
        const int hangulTCount = 28;
        const int hangulNCount = hangulVCount * hangulTCount;
        const int hangulSCount = 19 * hangulNCount;

        int hangulIndex = codePoint - hangulSBase;
        if (hangulIndex is >= 0 and < hangulSCount)
        {
            output.Add(hangulLBase + hangulIndex / hangulNCount);
            output.Add(hangulVBase + hangulIndex % hangulNCount / hangulTCount);
            int trailingIndex = hangulIndex % hangulTCount;
            if (trailingIndex != 0)
            {
                output.Add(hangulTBase + trailingIndex);
            }

            return;
        }

        int low = 0;
        int high = DecompositionSources.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (DecompositionSources[middle] < codePoint)
            {
                low = middle + 1;
            }
            else if (DecompositionSources[middle] > codePoint)
            {
                high = middle;
            }
            else
            {
                for (int index = DecompositionOffsets[middle]; index < DecompositionOffsets[middle + 1]; index++)
                {
                    output.Add(DecomposedCodePoints[index]);
                }

                return;
            }
        }

        output.Add(codePoint);
    }

    private static byte CombiningClass(int codePoint)
    {
        int low = 0;
        int high = CombiningClasses.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            int entry = (int)CombiningClasses[middle];
            int source = entry >> 8;
            if (source < codePoint)
            {
                low = middle + 1;
            }
            else if (source > codePoint)
            {
                high = middle;
            }
            else
            {
                return (byte)entry;
            }
        }

        return 0;
    }
}
