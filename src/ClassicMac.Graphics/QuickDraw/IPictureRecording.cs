using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Graphics.QuickDraw;

/// <summary>The shapes a picture records (StdRect, StdRRect, StdOval, StdArc): their opcodes' noun.</summary>
internal enum PictureNoun
{
    Rect = 0x30,
    RoundRect = 0x40,
    Oval = 0x50,
    Arc = 0x60,
}

// A picture open on a port (OpenPicture, OpenCPicture; pict.md §3.3): the port calls it with each drawing call, in local
// coordinates, before drawing; it writes the opcodes. The Pict layer implements it.
internal interface IPictureRecording
{
    void Line(int h1, int v1, int h2, int v2);

    void Shape(PictureNoun noun, int verb, MacRect rect, int ovalWidth, int ovalHeight, int startAngle, int arcAngle);

    void Polygon(int verb, IReadOnlyList<MacPoint> points);

    void Region(int verb, Region region);

    void Text(ReadOnlySpan<byte> text);

    void Bits(PixMap source, MacRect sourceRect, MacRect destinationRect, int mode, Region? mask);

    void Comment(int kind, ReadOnlySpan<byte> data);
}
