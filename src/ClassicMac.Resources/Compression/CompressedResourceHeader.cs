using System;
using ClassicMac.Core;

namespace ClassicMac.Resources;

/// <summary>
/// The 18-byte header of a compressed resource. Layout confirmed in the Resource Manager (ROM $077D CheckLoad
/// $FFC7A1C6; Mac OS 9 'Resources' $1000144C): signature, header length (never read), version, attributes,
/// decompressed size, then a version-8 or version-9 tail. The compressed data always starts at 18.
/// </summary>
public readonly record struct CompressedResourceHeader
{
    /// <summary>The signature at offset 0.</summary>
    public const uint Signature = 0xA89F6572;

    /// <summary>Where the compressed data starts; the header-length field is ignored by the Mac.</summary>
    public const int Length = 18;

    /// <summary>8 selects the version-8 form (with a working buffer); 9 the version-9 form.</summary>
    public byte Version { get; init; }

    /// <summary>Header attributes; bit 0 (<c>resCompressed</c>) clear means the data is stored uncompressed.</summary>
    public byte Attributes { get; init; }

    /// <summary>The size of the decompressed resource.</summary>
    public uint DecompressedSize { get; init; }

    /// <summary>The ID of the <c>'dcmp'</c> that decompresses it.</summary>
    public short DecompressorId { get; init; }

    /// <summary>Extra bytes after the output that absorb in-place overlap (v8: u8 @13; v9: u16 @14).</summary>
    public ushort ExpansionBytes { get; init; }

    /// <summary>Version 8: working-buffer size as a fraction of the block, out of 256 (@12).</summary>
    public byte WorkingBufferFraction { get; init; }

    /// <summary>Version 8: the word at 16, which must be zero.</summary>
    public ushort Reserved { get; init; }

    /// <summary>Version 9: the decompressor's first parameter (@16).</summary>
    public byte Param1 { get; init; }

    /// <summary>Version 9: the decompressor's second parameter (@17).</summary>
    public byte Param2 { get; init; }

    /// <summary>Whether this is the version-8 form. Any other version is read as version 9.</summary>
    public bool IsVersion8 => Version == 8;

    /// <summary>Whether header attribute bit 0 is set.</summary>
    public bool IsCompressed => (Attributes & 1) != 0;

    /// <summary>Whether <paramref name="data"/> starts with the compressed-resource signature.</summary>
    public static bool HasSignature(ReadOnlyMemory<byte> data) =>
        new BigEndianReader(data).TryReadUInt32At(0, out var signature) && signature == Signature;

    /// <summary>Reads the header; false when the data is too short or lacks the signature.</summary>
    public static bool TryRead(ReadOnlyMemory<byte> data, out CompressedResourceHeader header)
    {
        header = default;
        if (data.Length < Length || !HasSignature(data))
        {
            return false;
        }

        var reader = new BigEndianReader(data);
        var version = data.Span[6];
        header = version == 8
            ? new CompressedResourceHeader
            {
                Version = version,
                Attributes = data.Span[7],
                DecompressedSize = reader.ReadUInt32At(8),
                WorkingBufferFraction = data.Span[12],
                ExpansionBytes = data.Span[13],
                DecompressorId = reader.ReadInt16At(14),
                Reserved = reader.ReadUInt16At(16),
            }
            : new CompressedResourceHeader
            {
                Version = version,
                Attributes = data.Span[7],
                DecompressedSize = reader.ReadUInt32At(8),
                DecompressorId = reader.ReadInt16At(12),
                ExpansionBytes = reader.ReadUInt16At(14),
                Param1 = data.Span[16],
                Param2 = data.Span[17],
            };
        return true;
    }
}
