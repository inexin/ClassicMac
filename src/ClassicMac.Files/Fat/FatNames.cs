using System;
using System.Collections.Generic;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Fat
{
    // Names of FAT files without a FINDER.DAT record as File Exchange 3.0.2 (Mac OS 9.0) shows them (disassembly of PCXFS
    // Win95NameToMac, MakeCRC, FindExtension and UDFChecksum; confirmed in SheepShaver).
    internal static class FatNames
    {
        private const int MaxName = 31;

        // A VFAT long name as a Mac name. In Mac Roman (precomposed) when every character has a byte: whole if it fits
        // 31 bytes, else shortened. When some character has none, each UTF-16 unit's low byte stands for it and ':'
        // becomes '_'. A shortened name is the start of the name, '#', three hex digits of a CRC of the UTF-16 name,
        // and the extension (a '.' within the last six characters), 31 bytes in all.
        public static MacString FromLongName(string longName)
        {
            var units = longName.Normalize(NormalizationForm.FormC);
            var roman = MacRoman.TryEncode(units, out var encoded);
            if (roman && encoded.Length <= MaxName) return new MacString(encoded);
            if (!roman && units.Length <= MaxName) return new MacString(LowBytes(units));

            var extension = Extension(units) is { } at ? units[at..] : "";
            var extensionBytes = roman ? MacRoman.Encode(extension) : LowBytes(extension);
            var head = units[..Math.Min(units.Length, MaxName - 4 - extensionBytes.Length)];
            var name = new List<byte>(MaxName);
            name.AddRange(roman ? MacRoman.Encode(head) : LowBytes(head));
            var crc = Crc(longName); // over the long name's own UTF-16, as stored
            name.Add((byte)'#');
            foreach (var shift in new[] { 8, 4, 0 }) name.Add((byte)"0123456789ABCDEF"[crc >> shift & 0xF]);
            name.AddRange(extensionBytes);
            return new MacString(name.ToArray());
        }

        private static byte[] LowBytes(string units)
        {
            var bytes = new byte[units.Length];
            for (var i = 0; i < units.Length; i++) bytes[i] = units[i] == ':' ? (byte)'_' : (byte)units[i];
            return bytes;
        }

        // The last '.' among the final six characters (fewer in names under eight), as FindExtension looks for it.
        private static int? Extension(string units)
        {
            var window = units.Length < 3 ? 0 : units.Length < 7 ? units.Length - 2 : 6;
            for (var i = units.Length - 1; i >= units.Length - window; i--)
            {
                if (units[i] == '.') return i;
            }
            return null;
        }

        // CRC-16 (polynomial $1021, initial 0, no reflection) over the name as big-endian UTF-16, as UDFChecksum
        // computes it (the OSTA UDF unique-name checksum).
        internal static int Crc(string units)
        {
            var crc = 0;
            foreach (var c in units)
            {
                foreach (var b in new[] { (byte)(c >> 8), (byte)c })
                {
                    crc ^= b << 8;
                    for (var bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                    crc &= 0xFFFF;
                }
            }
            return crc;
        }
    }
}
