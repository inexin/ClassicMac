using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;

namespace ClassicMac.Files.Hfs;

// The HFS catalog's key order: by parent ID, then by name with _RelString's weights (hfs.md §1.11).
internal static class HfsCatalogKeys
{
    internal static readonly ushort[] CatalogNameWeights = BuildCatalogNameWeights();

    internal static int CompareCatalogKeys(byte[] left, byte[] right)
    {
        // The parent IDs, big-endian, compare as their bytes do (no reader per comparison: a sort makes thousands).
        int byParent = left.AsSpan(2, 4).SequenceCompareTo(right.AsSpan(2, 4));
        if (byParent != 0)
        {
            return byParent;
        }

        int leftLength = left[6], rightLength = right[6];
        if (left.Length < 7 + leftLength || right.Length < 7 + rightLength)
        {
            throw new InvalidDataException("An HFS catalog key has an invalid name length.");
        }

        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            int comparison = CatalogNameWeights[left[7 + index]].CompareTo(CatalogNameWeights[right[7 + index]]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return leftLength.CompareTo(rightLength);
    }

    /// <summary>Whether two Mac OS Roman names are the same name to an HFS catalog: equal by its ordering (case-insensitive, diacritics kept).</summary>
    internal static bool CatalogNamesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (CatalogNameWeights[left[index]] != CatalogNameWeights[right[index]])
            {
                return false;
            }
        }

        return true;
    }

    internal static ushort[] BuildCatalogNameWeights()
    {
        // Inside Macintosh: Text, RelString; the exceptions here follow the Mac OS 9 ROM rules
        // recorded in docs/formats/file-systems/hfs.md §1.11. Unlisted Mac Roman bytes keep their code order.
        var weights = new ushort[256];
        for (int value = 0; value < weights.Length; value++)
        {
            weights[value] = (ushort)(value << 8);
        }

        for (char value = 'a'; value <= 'z'; value++)
        {
            Set(value.ToString(), (ushort)((value - 32) << 8));
        }

        void Set(string chars, ushort weight)
        {
            foreach (char value in chars)
            {
                if (!MacRoman.TryGetByte(value, out byte encoded))
                {
                    throw new InvalidOperationException($"The HFS comparison table contains an unencodable character: {value}.");
                }

                weights[encoded] = weight;
            }
        }

        Set("`", 0x4180);
        Set("\u00A0", 0x2000);
        Set("äÄ", 0x4108);
        Set("åÅ", 0x410C);
        Set("àÀ", 0x4104);
        Set("ãÃ", 0x410A);
        Set("æÆ", 0x4114);
        Set("çÇ", 0x4310);
        Set("éÉ", 0x4502);
        Set("ñÑ", 0x4E0A);
        Set("öÖ", 0x4F08);
        Set("õÕ", 0x4F0A);
        Set("øØ", 0x4F0E);
        Set("œŒ", 0x4F14);
        Set("üÜ", 0x5508);
        Set("á", 0x4182);
        Set("â", 0x4186);
        Set("è", 0x4584);
        Set("ê", 0x4586);
        Set("ë", 0x4588);
        Set("í", 0x4982);
        Set("ì", 0x4984);
        Set("î", 0x4986);
        Set("ï", 0x4988);
        Set("ó", 0x4F82);
        Set("ò", 0x4F84);
        Set("ô", 0x4F86);
        Set("ú", 0x5582);
        Set("ù", 0x5584);
        Set("û", 0x5586);
        Set("ß", 0x5382);
        Set("ÿ", 0x5988);
        Set("ª", 0x4192);
        Set("º", 0x4F92);
        Set("“", 0x2202);
        Set("”", 0x2204);
        Set("«", 0x2206);
        Set("»", 0x2208);
        Set("‘", 0x2702);
        Set("’", 0x2704);
        return weights;
    }
}
