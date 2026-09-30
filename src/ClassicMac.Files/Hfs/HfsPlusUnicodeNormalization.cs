using System;
using System.Collections.Generic;

namespace ClassicMac.Files.Hfs;

/// <summary>Validates HFS Plus names using the fixed Unicode 3.2 decomposition rules.</summary>
internal static partial class HfsPlusUnicodeNormalization
{
    public static bool IsCanonical(ReadOnlySpan<byte> bigEndianName)
    {
        var decomposed = new List<int>(bigEndianName.Length / 2);
        for (int offset = 0; offset < bigEndianName.Length; offset += 2)
        {
            int codePoint = (bigEndianName[offset] << 8) | bigEndianName[offset + 1];
            if (char.IsHighSurrogate((char)codePoint) && offset + 3 < bigEndianName.Length)
            {
                int lowSurrogate = (bigEndianName[offset + 2] << 8) | bigEndianName[offset + 3];
                if (char.IsLowSurrogate((char)lowSurrogate))
                {
                    codePoint = char.ConvertToUtf32((char)codePoint, (char)lowSurrogate);
                    offset += 2;
                }
            }
            AppendDecomposition(codePoint, decomposed);
        }

        for (int index = 1; index < decomposed.Count; index++)
        {
            byte currentClass = CombiningClass(decomposed[index]);
            if (currentClass == 0) continue;
            int position = index;
            while (position > 0)
            {
                byte previousClass = CombiningClass(decomposed[position - 1]);
                if (previousClass == 0 || previousClass <= currentClass) break;
                (decomposed[position - 1], decomposed[position]) =
                    (decomposed[position], decomposed[position - 1]);
                position--;
            }
        }

        int inputOffset = 0;
        foreach (int codePoint in decomposed)
        {
            if (codePoint <= char.MaxValue)
            {
                if (inputOffset + 1 >= bigEndianName.Length ||
                    bigEndianName[inputOffset] != (byte)(codePoint >> 8) ||
                    bigEndianName[inputOffset + 1] != (byte)codePoint)
                    return false;
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
                return false;
            inputOffset += 4;
        }

        return inputOffset == bigEndianName.Length;
    }

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
            if (trailingIndex != 0) output.Add(hangulTBase + trailingIndex);
            return;
        }

        int low = 0;
        int high = DecompositionSources.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (DecompositionSources[middle] < codePoint) low = middle + 1;
            else if (DecompositionSources[middle] > codePoint) high = middle;
            else
            {
                for (int index = DecompositionOffsets[middle]; index < DecompositionOffsets[middle + 1]; index++)
                    output.Add(DecomposedCodePoints[index]);
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
            if (source < codePoint) low = middle + 1;
            else if (source > codePoint) high = middle;
            else return (byte)entry;
        }

        return 0;
    }
}
