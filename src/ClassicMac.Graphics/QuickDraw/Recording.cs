using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Graphics.QuickDraw;

/// <summary>A polygon as ClosePoly gives it: its bounding box and its points, in local coordinates (<c>PolyHandle</c>).</summary>
public sealed record Polygon(MacRect BoundingBox, IReadOnlyList<MacPoint> Points);

// OpenRgn's rgnBuf (docs/formats/graphics/quickdraw.md §2.26, §4.13): the inversion points of the shapes framed and the lines drawn while it is open,
// in local coordinates, with each QuickDraw's buffer limit.
internal sealed class RegionRecording(bool macOS9)
{
    // Mac OS 9 caps the buffer at $7FF8 bytes (rgnTooBigErr, sticky); the ROM refuses an item past its own limits
    // (rgnOverflowErr) and CloseRgn then gives an empty region.
    private const int NativeLimit = 0x7FF8;

    private readonly Dictionary<int, List<int>> rows = [];
    private int bytes;
    private bool overflow;

    // PutLine: an edge's inversion points; a vertical line adds none.
    public void Line(int h1, int v1, int h2, int v2)
    {
        var points = new Dictionary<int, List<int>>();
        RegionShapes.EdgeInversions(points, h1, v1, h2, v2);
        int added = 4 * points.Values.Sum(p => p.Count);
        Add(points, added, macOS9 ? added : 8 * (Math.Min(Math.Abs(h2 - h1), Math.Abs(v2 - v1)) + 1), 0xFE00);
    }

    // PutRect: the rect's four corners, the whole rect (not an outline).
    public void Rect(MacRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var points = new Dictionary<int, List<int>>
        {
            [rect.Top] = [rect.Left, rect.Right],
            [rect.Bottom] = [rect.Left, rect.Right],
        };
        Add(points, 16, macOS9 ? 16 : bytes > 65535 - 1024 - 16 ? int.MaxValue : 0, int.MaxValue);
    }

    // PutRgn and PutOval: every inversion point of the shape (an oval or round rect filled).
    public void Shape(Region region)
    {
        var points = new Dictionary<int, List<int>>();
        foreach (var (x, y) in region.InversionPoints())
        {
            if (!points.TryGetValue(y, out var xs))
            {
                points[y] = xs = [];
            }

            xs.Add(x);
        }

        int added = 4 * points.Values.Sum(p => p.Count);
        Add(points, added, macOS9 ? added : 2 * (10 + 2 * added), 0xFF00);
    }

    // An item goes in unless it would pass the limit: Mac OS 9's cap on the real size (and nothing after that), the
    // ROM's own estimate against the routine's bound.
    private void Add(Dictionary<int, List<int>> points, int added, int estimate, int romBound)
    {
        if (overflow && macOS9)
        {
            return;
        }

        if (macOS9 ? bytes + added > NativeLimit : bytes + estimate >= romBound)
        {
            overflow = true;
            return;
        }

        foreach (var (v, xs) in points)
        {
            if (!rows.TryGetValue(v, out var row))
            {
                rows[v] = row = [];
            }

            row.AddRange(xs);
        }

        bytes += added;
    }

    // CloseRgn: SortPoints and CullPoints (adjacent equal points cancel in pairs), then PackRgn: under four points an
    // empty region, exactly four the rect from the first to the last, else the points' region. After an overflow the
    // ROM gives an empty region and Mac OS 9 none (the destination is left as it was).
    public Region? Close()
    {
        if (overflow)
        {
            return macOS9 ? null : Region.Empty;
        }

        var points = new List<(int v, int h)>();
        foreach (var (v, xs) in rows)
        {
            foreach (var group in xs.GroupBy(h => h).Where(g => g.Count() % 2 == 1))
            {
                points.Add((v, group.Key));
            }
        }

        points.Sort();
        return points.Count switch
        {
            < 4 => Region.Empty,
            4 => Region.FromRect(new MacRect((short)points[0].v, (short)points[0].h, (short)points[3].v, (short)points[3].h)),
            _ => Region.FromInversionRows(points.GroupBy(p => p.v).Select(g => (g.Key, g.Select(p => p.h).ToList()))),
        };
    }
}

// OpenPoly's polygon (quickdraw.md §2.27): the first line stores its start and end, every later line its end only; no
// duplicate is dropped and nothing closes it.
internal sealed class PolygonRecording
{
    // A polygon handle is at most 65,535 bytes: 10 and 4 a point. Mac OS 9 refuses a point past it (−108); the ROM's
    // size wraps and corrupts the polygon [Code], which ClassicMac does not reproduce: it stops adding too.
    private const int MaxPoints = (65535 - 10) / 4;

    private readonly List<MacPoint> points = [];

    public void Line(int h1, int v1, int h2, int v2)
    {
        if (points.Count == 0)
        {
            Add(h1, v1);
        }

        Add(h2, v2);
    }

    private void Add(int h, int v)
    {
        if (points.Count < MaxPoints)
        {
            points.Add(new MacPoint((short)v, (short)h));
        }
    }

    public Polygon Close() => points.Count == 0
        ? new Polygon(default, points)
        : new Polygon(new MacRect(points.Min(p => p.V), points.Min(p => p.H), points.Max(p => p.V), points.Max(p => p.H)), points);
}
