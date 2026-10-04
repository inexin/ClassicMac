using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace ClassicMac.Core;

/// <summary>Writes big-endian values into a buffer that grows as needed, like a <see cref="System.Text.StringBuilder"/> for bytes.</summary>
/// <remarks>
/// <c>Write…</c> appends. <c>Write…At</c> overwrites bytes already written, for a length or offset that is only known
/// later: write a placeholder, then patch it.
/// </remarks>
public sealed class BigEndianWriter
{
    private byte[] buffer;
    private int length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The bytes to reserve; the buffer grows past it as needed.</param>
    public BigEndianWriter(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        buffer = capacity == 0 ? [] : new byte[capacity];
    }

    /// <summary>
    /// Creates a writer over <paramref name="buffer"/>, all of it counting as written: <c>Write…At</c> patches the
    /// array in place. Appending grows into a new array and leaves <paramref name="buffer"/> as it was then.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    public BigEndianWriter(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        this.buffer = buffer;
        length = buffer.Length;
    }

    /// <summary>The number of bytes written.</summary>
    public int Length => length;

    /// <summary>The bytes written, without copying; valid until the next write.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => buffer.AsMemory(0, length);

    /// <summary>The bytes written, without copying; valid until the next write.</summary>
    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, length);

    /// <summary>A copy of the bytes written.</summary>
    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

    /// <summary>Writes the bytes written so far to <paramref name="stream"/>, which is left open.</summary>
    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(buffer, 0, length);
    }

    /// <summary>Discards the bytes written, keeping the buffer for reuse.</summary>
    public void Clear() => length = 0;

    /// <summary>Appends one byte.</summary>
    public void WriteByte(byte value) => Append(1)[0] = value;

    /// <summary>Appends a signed 16-bit integer.</summary>
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16BigEndian(Append(sizeof(short)), value);

    /// <summary>Appends an unsigned 16-bit integer.</summary>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Append(sizeof(ushort)), value);

    /// <summary>Appends a signed 32-bit integer.</summary>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32BigEndian(Append(sizeof(int)), value);

    /// <summary>Appends an unsigned 32-bit integer.</summary>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Append(sizeof(uint)), value);

    /// <summary>Appends a signed 64-bit integer.</summary>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64BigEndian(Append(sizeof(long)), value);

    /// <summary>Appends an unsigned 64-bit integer.</summary>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Append(sizeof(ulong)), value);

    /// <summary>Appends a four-character code.</summary>
    public void WriteFourCC(FourCC value) => WriteUInt32(value.Value);

    /// <summary>Appends a QuickDraw point (v, h).</summary>
    public void WriteMacPoint(MacPoint value)
    {
        WriteInt16(value.V);
        WriteInt16(value.H);
    }

    /// <summary>Appends a QuickDraw rectangle (top, left, bottom, right).</summary>
    public void WriteMacRect(MacRect value)
    {
        WriteInt16(value.Top);
        WriteInt16(value.Left);
        WriteInt16(value.Bottom);
        WriteInt16(value.Right);
    }

    /// <summary>Appends a signed 16.16 fixed-point value.</summary>
    public void WriteFixed(Fixed value) => WriteInt32(value.Raw);

    /// <summary>Appends an unsigned 16.16 fixed-point value.</summary>
    public void WriteUnsignedFixed(UnsignedFixed value) => WriteUInt32(value.Raw);

    /// <summary>Appends bytes verbatim.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Append(value.Length));

    /// <summary>Appends <paramref name="count"/> zero bytes (padding, reserved fields, placeholders).</summary>
    public void WriteZeros(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Append(count).Clear();
    }

    /// <summary>Overwrites one byte already written.</summary>
    public void WriteByteAt(int offset, byte value) => At(offset, 1)[0] = value;

    /// <summary>Overwrites a signed 16-bit integer already written.</summary>
    public void WriteInt16At(int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(At(offset, sizeof(short)), value);

    /// <summary>Overwrites an unsigned 16-bit integer already written.</summary>
    public void WriteUInt16At(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(At(offset, sizeof(ushort)), value);

    /// <summary>Overwrites a signed 32-bit integer already written.</summary>
    public void WriteInt32At(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(At(offset, sizeof(int)), value);

    /// <summary>Overwrites an unsigned 32-bit integer already written.</summary>
    public void WriteUInt32At(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(At(offset, sizeof(uint)), value);

    /// <summary>Overwrites a signed 64-bit integer already written.</summary>
    public void WriteInt64At(int offset, long value) => BinaryPrimitives.WriteInt64BigEndian(At(offset, sizeof(long)), value);

    /// <summary>Overwrites an unsigned 64-bit integer already written.</summary>
    public void WriteUInt64At(int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(At(offset, sizeof(ulong)), value);

    /// <summary>Overwrites a four-character code already written.</summary>
    public void WriteFourCCAt(int offset, FourCC value) => WriteUInt32At(offset, value.Value);

    /// <summary>Overwrites a QuickDraw point already written.</summary>
    public void WriteMacPointAt(int offset, MacPoint value)
    {
        At(offset, MacPoint.Length);
        WriteInt16At(offset, value.V);
        WriteInt16At(offset + 2, value.H);
    }

    /// <summary>Overwrites a QuickDraw rectangle already written.</summary>
    public void WriteMacRectAt(int offset, MacRect value)
    {
        At(offset, MacRect.Length);
        WriteInt16At(offset, value.Top);
        WriteInt16At(offset + 2, value.Left);
        WriteInt16At(offset + 4, value.Bottom);
        WriteInt16At(offset + 6, value.Right);
    }

    /// <summary>Overwrites bytes already written.</summary>
    public void WriteBytesAt(int offset, ReadOnlySpan<byte> value) => value.CopyTo(At(offset, value.Length));


    // Any number, written as the field's type when it fits: a generic overload loses to the exact one, so a value
    // of the field's type, or one cast to it on purpose (to wrap), is written as before.

    /// <summary>Appends any number as a byte.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a byte.</exception>
    public void WriteByte<T>(T value) where T : INumberBase<T> => WriteByte(Fit<byte, T>(value));

    /// <summary>Appends any number as a short.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a short.</exception>
    public void WriteInt16<T>(T value) where T : INumberBase<T> => WriteInt16(Fit<short, T>(value));

    /// <summary>Appends any number as a ushort.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a ushort.</exception>
    public void WriteUInt16<T>(T value) where T : INumberBase<T> => WriteUInt16(Fit<ushort, T>(value));

    /// <summary>Appends any number as a int.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a int.</exception>
    public void WriteInt32<T>(T value) where T : INumberBase<T> => WriteInt32(Fit<int, T>(value));

    /// <summary>Appends any number as a uint.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a uint.</exception>
    public void WriteUInt32<T>(T value) where T : INumberBase<T> => WriteUInt32(Fit<uint, T>(value));

    /// <summary>Appends any number as a long.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a long.</exception>
    public void WriteInt64<T>(T value) where T : INumberBase<T> => WriteInt64(Fit<long, T>(value));

    /// <summary>Appends any number as a ulong.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a ulong.</exception>
    public void WriteUInt64<T>(T value) where T : INumberBase<T> => WriteUInt64(Fit<ulong, T>(value));

    /// <summary>Overwrites a byte already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a byte, or the bytes were not written yet.</exception>
    public void WriteByteAt<T>(int offset, T value) where T : INumberBase<T> => WriteByteAt(offset, Fit<byte, T>(value));

    /// <summary>Overwrites a short already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a short, or the bytes were not written yet.</exception>
    public void WriteInt16At<T>(int offset, T value) where T : INumberBase<T> => WriteInt16At(offset, Fit<short, T>(value));

    /// <summary>Overwrites a ushort already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a ushort, or the bytes were not written yet.</exception>
    public void WriteUInt16At<T>(int offset, T value) where T : INumberBase<T> => WriteUInt16At(offset, Fit<ushort, T>(value));

    /// <summary>Overwrites a int already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a int, or the bytes were not written yet.</exception>
    public void WriteInt32At<T>(int offset, T value) where T : INumberBase<T> => WriteInt32At(offset, Fit<int, T>(value));

    /// <summary>Overwrites a uint already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a uint, or the bytes were not written yet.</exception>
    public void WriteUInt32At<T>(int offset, T value) where T : INumberBase<T> => WriteUInt32At(offset, Fit<uint, T>(value));

    /// <summary>Overwrites a long already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a long, or the bytes were not written yet.</exception>
    public void WriteInt64At<T>(int offset, T value) where T : INumberBase<T> => WriteInt64At(offset, Fit<long, T>(value));

    /// <summary>Overwrites a ulong already written with any number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit a ulong, or the bytes were not written yet.</exception>
    public void WriteUInt64At<T>(int offset, T value) where T : INumberBase<T> => WriteUInt64At(offset, Fit<ulong, T>(value));

    private static TField Fit<TField, T>(T value) where TField : INumberBase<TField> where T : INumberBase<T>
    {
        try
        {
            return TField.CreateChecked(value);
        }
        catch (OverflowException e)
        {
            throw new ArgumentOutOfRangeException($"{value} does not fit a {typeof(TField).Name}.", e);
        }
    }

    // The next count bytes, growing the buffer, and counts them as written.
    private Span<byte> Append(int count)
    {
        if (buffer.Length - length < count)
        {
            Array.Resize(ref buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)length + count, Math.Max(256, 2L * buffer.Length))));
        }

        var span = buffer.AsSpan(length, count);
        length += count;
        return span;
    }

    private Span<byte> At(int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Only bytes already written can be overwritten.");
        }

        return buffer.AsSpan(offset, count);
    }
}
