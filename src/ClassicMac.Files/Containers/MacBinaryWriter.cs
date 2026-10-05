using System.IO;
using System.Text;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Checksums;

namespace ClassicMac.Files.Containers;

/// <summary>
/// Writes MacBinary III files: the 128-byte header (signature <c>'mBIN'</c>, writer version 130, reader version 129,
/// CRC-16), then the data fork and the resource fork, each padded with zeros to a multiple of 128. No secondary header
/// or comment.
/// </summary>
public static class MacBinaryWriter
{
    private const int Block = 128;

    /// <summary>
    /// Writes <paramref name="file"/>. Its name is cut to 63 bytes; a name with ':' or NUL (which no Mac file has, and the
    /// reader takes for another kind of file) and forks over $7FFFFF bytes (the MacBinary limit) are refused (an argument
    /// error, nothing written).
    /// </summary>
    public static void Write(MacFile file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        if (file.DataFork.Length > 0x7FFFFF || file.ResourceFork.Length > 0x7FFFFF)
        {
            throw new ArgumentException("A fork is over MacBinary's 8 MiB limit.", nameof(file));
        }

        if (file.Name.Bytes.IndexOfAny((byte)':', (byte)0) >= 0)
        {
            throw new ArgumentException("A Mac name holds no ':' or NUL; MacBinary readers refuse one.", nameof(file));
        }

        var header = new byte[Block];
        var name = file.Name.Bytes;
        var length = Math.Clamp(name.Length, 1, 63);
        header[1] = (byte)length;
        if (name.Length > 0)
        {
            name[..Math.Min(63, name.Length)].CopyTo(header.AsSpan(2));
        }
        else
        {
            header[2] = (byte)'?';
        }

        var info = file.FinderInfo;
        var writer = new BigEndianWriter(header);
        writer.WriteFourCCAt(65, info.Type);
        writer.WriteFourCCAt(69, info.Creator);
        header[73] = (byte)((ushort)info.Flags >> 8);
        writer.WriteMacPointAt(75, info.Location);
        writer.WriteInt16At(79, info.Folder);
        writer.WriteUInt32At(83, file.DataFork.Length);
        writer.WriteUInt32At(87, file.ResourceFork.Length);
        writer.WriteUInt32At(91, file.Created?.Seconds ?? 0);
        writer.WriteUInt32At(95, file.Modified?.Seconds ?? 0);
        header[101] = (byte)info.Flags;
        "mBIN"u8.CopyTo(header.AsSpan(102));
        header[106] = info.Extended.Length > 9 ? info.Extended.Span[8] : (byte)0;
        header[107] = info.Extended.Length > 9 ? info.Extended.Span[9] : (byte)0;
        header[122] = 130;
        header[123] = 129;
        writer.WriteUInt16At(124, Crc16Xmodem.Compute(header.AsSpan(0, 124)));
        output.Write(header);
        Fork(file.DataFork, output);
        Fork(file.ResourceFork, output);
    }

    /// <summary>The file's bytes.</summary>
    public static byte[] ToArray(MacFile file)
    {
        using var output = new MemoryStream();
        Write(file, output);
        return output.ToArray();
    }

    private static void Fork(ForkData fork, Stream output)
    {
        using (var stream = fork.Open())
        {
            stream.CopyTo(output);
        }

        var pad = (int)((Block - fork.Length % Block) % Block);
        output.Write(new byte[pad]);
    }
}

/// <summary>
/// Writes BinHex 4.0 files: the marker line, then the binary stream (header, data fork, resource fork, each with its
/// CRC-16) run-length encoded and written six bits per character between colons, in lines of 64 characters, CR line
/// ends as on the Mac.
/// </summary>
public static class BinHexWriter
{
    private const string Alphabet = "!\"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr";
    private const int LineLength = 64;

    /// <summary>Writes <paramref name="file"/>. Its name is cut to 63 bytes.</summary>
    public static void Write(MacFile file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        var text = Encoding.ASCII.GetBytes(ToText(file));
        output.Write(text);
    }

    /// <summary>The file as BinHex text.</summary>
    public static string ToText(MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var stream = new MemoryStream();
        var name = file.Name.Bytes;
        var length = Math.Clamp(name.Length, 1, 63);
        var header = new BigEndianWriter(22 + length);
        header.WriteByte(length);
        if (name.Length > 0)
        {
            header.WriteBytes(name[..length]);
        }
        else
        {
            header.WriteByte((byte)'?');
        }

        header.WriteByte(0);
        header.WriteFourCC(file.FinderInfo.Type);
        header.WriteFourCC(file.FinderInfo.Creator);
        header.WriteUInt16((ushort)file.FinderInfo.Flags);
        header.WriteUInt32(file.DataFork.Length);
        header.WriteUInt32(file.ResourceFork.Length);
        header.WriteUInt16(Crc16Xmodem.Compute(header.WrittenSpan));
        header.WriteTo(stream);
        Part(file.DataFork, stream);
        Part(file.ResourceFork, stream);

        var encoded = SixBits(RunLength(stream.ToArray()));
        var text = new StringBuilder("(This file must be converted with BinHex 4.0)\r:");
        var column = 1;
        foreach (var c in encoded)
        {
            if (column == LineLength)
            {
                text.Append('\r');
                column = 0;
            }
            text.Append(c);
            column++;
        }
        return text.Append(":\r").ToString();
    }

    private static void Part(ForkData fork, Stream stream)
    {
        var bytes = fork.ToArray();
        stream.Write(bytes);
        var crc = new BigEndianWriter(2);
        crc.WriteUInt16(Crc16Xmodem.Compute(bytes));
        crc.WriteTo(stream);
    }

    // $90 is written as $90 $00; a byte repeated 3 to 255 times in all as the byte, $90, the count [ClassicMac: runs
    // of 3 or more; any choice decodes the same].
    internal static byte[] RunLength(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream(data.Length + data.Length / 16);
        for (var i = 0; i < data.Length;)
        {
            var b = data[i];
            var run = 1;
            while (i + run < data.Length && data[i + run] == b && run < 255)
            {
                run++;
            }

            if (b == 0x90)
            {
                output.WriteByte(0x90);
                output.WriteByte(0x00);
            }
            else
            {
                output.WriteByte(b);
            }
            if (run >= 3)
            {
                output.WriteByte(0x90);
                output.WriteByte((byte)run);
                i += run;
            }
            else
            {
                i++;
            }
        }
        return output.ToArray();
    }

    // Six bits per character, most significant first; the last character holds the remaining bits, zero-filled.
    private static string SixBits(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 4 + 2) / 3);
        int bits = 0, count = 0;
        foreach (var b in data)
        {
            bits = (bits << 8) | b;
            count += 8;
            while (count >= 6)
            {
                count -= 6;
                text.Append(Alphabet[(bits >> count) & 0x3F]);
            }
        }
        if (count > 0)
        {
            text.Append(Alphabet[(bits << (6 - count)) & 0x3F]);
        }

        return text.ToString();
    }
}
