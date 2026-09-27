using System;
using System.Buffers.Binary;

namespace ClassicMac.Core
{
    /// <summary>
    /// A QuickDraw <c>Point</c>: <c>{ short v; short h; }</c>, vertical first as on the Mac (<i>Inside Macintosh:
    /// Imaging With QuickDraw</i>). Four bytes, big-endian, in files and resources.
    /// </summary>
    /// <param name="V">The vertical coordinate (y).</param>
    /// <param name="H">The horizontal coordinate (x).</param>
    public readonly record struct MacPoint(short V, short H)
    {
        /// <summary>The size in bytes as stored.</summary>
        public const int Length = 4;

        /// <summary>Reads a point from the first four bytes of <paramref name="source"/>.</summary>
        public static MacPoint Read(ReadOnlySpan<byte> source) =>
            new(BinaryPrimitives.ReadInt16BigEndian(source), BinaryPrimitives.ReadInt16BigEndian(source[2..]));

        /// <summary>Writes the point as four big-endian bytes.</summary>
        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteInt16BigEndian(destination, V);
            BinaryPrimitives.WriteInt16BigEndian(destination[2..], H);
        }

        /// <inheritdoc/>
        public override string ToString() => $"(v {V}, h {H})";
    }

    /// <summary>
    /// A QuickDraw <c>Rect</c>: <c>{ short top, left, bottom, right; }</c>, which the Mac also reads as two points,
    /// <c>topLeft</c> and <c>botRight</c>. Eight bytes, big-endian, in files and resources.
    /// </summary>
    /// <param name="Top">The top edge.</param>
    /// <param name="Left">The left edge.</param>
    /// <param name="Bottom">The bottom edge.</param>
    /// <param name="Right">The right edge.</param>
    public readonly record struct MacRect(short Top, short Left, short Bottom, short Right)
    {
        /// <summary>The size in bytes as stored.</summary>
        public const int Length = 8;

        /// <summary>A rectangle from its two corner points.</summary>
        public MacRect(MacPoint topLeft, MacPoint bottomRight)
            : this(topLeft.V, topLeft.H, bottomRight.V, bottomRight.H)
        {
        }

        /// <summary>The top-left corner (<c>topLeft</c>).</summary>
        public MacPoint TopLeft => new(Top, Left);

        /// <summary>The bottom-right corner (<c>botRight</c>).</summary>
        public MacPoint BottomRight => new(Bottom, Right);

        /// <summary>Right minus left; negative for an inverted rectangle.</summary>
        public int Width => Right - Left;

        /// <summary>Bottom minus top; negative for an inverted rectangle.</summary>
        public int Height => Bottom - Top;

        /// <summary>Whether the rectangle encloses no pixels (bottom ≤ top or right ≤ left), as <c>EmptyRect</c> tests.</summary>
        public bool IsEmpty => Bottom <= Top || Right <= Left;

        /// <summary>Reads a rectangle from the first eight bytes of <paramref name="source"/>.</summary>
        public static MacRect Read(ReadOnlySpan<byte> source) => new(
            BinaryPrimitives.ReadInt16BigEndian(source),
            BinaryPrimitives.ReadInt16BigEndian(source[2..]),
            BinaryPrimitives.ReadInt16BigEndian(source[4..]),
            BinaryPrimitives.ReadInt16BigEndian(source[6..]));

        /// <summary>Writes the rectangle as eight big-endian bytes.</summary>
        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteInt16BigEndian(destination, Top);
            BinaryPrimitives.WriteInt16BigEndian(destination[2..], Left);
            BinaryPrimitives.WriteInt16BigEndian(destination[4..], Bottom);
            BinaryPrimitives.WriteInt16BigEndian(destination[6..], Right);
        }

        /// <inheritdoc/>
        public override string ToString() => $"(t {Top}, l {Left}, b {Bottom}, r {Right})";
    }
}
