using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Resources.Decoders.Images.WebP;

/// <summary>
/// A VP8L (lossless WebP) bit stream (RFC 9649 §3): the header; the subtract-green transform, then the predictor
/// transform with each 16 × 16 block's mode chosen by the smallest residuals; then the residuals as an entropy-coded
/// image of literals, colour cache hits and backward references (LZ77), under one group of prefix codes. The colour
/// cache size is the one of 0, 6 and 10 bits that gives the smallest stream.
/// </summary>
internal static class Vp8lEncoder
{
    private const int PredictorBits = 4, MaxCopy = 4096, MaxDistance = (1 << 20) - 120, MinCopy = 3, ChainDepth = 48, LengthCodes = 24;

    private readonly record struct Token(int Kind, uint Value, int Length, int Distance);

    private const int Literal = 0, Cached = 1, Copy = 2;

    /// <summary>The VP8L data for the pixels (ARGB, rows top down).</summary>
    public static byte[] Encode(int width, int height, uint[] argb)
    {
        // Subtract green: red and blue less green (§4.2), on which the predictor works.
        var pixels = new uint[argb.Length];
        var alpha = false;
        for (var i = 0; i < argb.Length; i++)
        {
            uint p = argb[i], g = (p >> 8) & 0xFF;
            uint r = ((p >> 16) - g) & 0xFF, b = (p - g) & 0xFF;
            pixels[i] = (p & 0xFF00FF00) | (r << 16) | b;
            alpha |= p >> 24 != 0xFF;
        }

        var (modes, blocksX, blocksY, residuals) = Predict(width, height, pixels);
        byte[]? best = null;
        foreach (var cacheBits in new[] { 0, 6, 10 })
        {
            var w = new Vp8lBitWriter();
            w.Write(0x2F, 8);                                                 // signature
            w.Write((uint)(width - 1), 14);
            w.Write((uint)(height - 1), 14);
            w.Write(alpha ? 1u : 0u, 1);
            w.Write(0, 3);                                                    // version
            w.Write(1, 1);
            w.Write(2, 2);                                                    // subtract green
            w.Write(1, 1);
            w.Write(0, 2);                                                    // predictor
            w.Write(PredictorBits - 2, 3);
            WriteImage(w, modes, blocksX, cacheBits: 0, main: false);
            w.Write(0, 1);                                                    // no more transforms
            WriteImage(w, residuals, width, cacheBits, main: true);
            var data = w.ToArray();
            if (best is null || data.Length < best.Length)
            {
                best = data;
            }
        }

        return best!;
    }

    // The predictor transform (§4.1): the mode of each block that gives the smallest residuals (each channel's distance
    // from 0, wrapping), and the residuals of every pixel against its prediction from its decoded neighbours.
    private static (uint[] Modes, int BlocksX, int BlocksY, uint[] Residuals) Predict(int width, int height, uint[] pixels)
    {
        int size = 1 << PredictorBits, blocksX = (width + size - 1) / size, blocksY = (height + size - 1) / size;
        var modes = new uint[blocksX * blocksY];
        var residuals = new uint[pixels.Length];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                int bestMode = 0;
                long bestCost = long.MaxValue;
                for (var mode = 0; mode < 14; mode++)
                {
                    long cost = 0;
                    for (var y = by * size; y < Math.Min(height, (by + 1) * size) && cost < bestCost; y++)
                    {
                        for (var x = bx * size; x < Math.Min(width, (bx + 1) * size); x++)
                        {
                            cost += Cost(Subtract(pixels[y * width + x], Prediction(pixels, width, x, y, mode)));
                        }
                    }

                    if (cost < bestCost)
                    {
                        (bestCost, bestMode) = (cost, mode);
                    }
                }

                modes[by * blocksX + bx] = 0xFF000000u | ((uint)bestMode << 8);
                for (var y = by * size; y < Math.Min(height, (by + 1) * size); y++)
                {
                    for (var x = bx * size; x < Math.Min(width, (bx + 1) * size); x++)
                    {
                        residuals[y * width + x] = Subtract(pixels[y * width + x], Prediction(pixels, width, x, y, bestMode));
                    }
                }
            }
        }

        return (modes, blocksX, blocksY, residuals);
    }

    private static long Cost(uint residual)
    {
        long cost = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var c = (int)((residual >> shift) & 0xFF);
            cost += Math.Min(c, 256 - c);
        }

        return cost;
    }

    // The prediction for (x, y) (§4.1): the top-left pixel predicts opaque black, the rest of the top row its left
    // neighbour, the left column its top neighbour; elsewhere the mode's, the top-right of the last column being the
    // first pixel of the row.
    private static uint Prediction(uint[] p, int width, int x, int y, int mode)
    {
        if (x == 0 && y == 0)
        {
            return 0xFF000000;
        }

        if (y == 0)
        {
            return p[x - 1];
        }

        if (x == 0)
        {
            return p[(y - 1) * width];
        }

        uint l = p[y * width + x - 1], t = p[(y - 1) * width + x], tl = p[(y - 1) * width + x - 1];
        uint tr = x == width - 1 ? p[y * width] : p[(y - 1) * width + x + 1];
        return mode switch
        {
            0 => 0xFF000000,
            1 => l,
            2 => t,
            3 => tr,
            4 => tl,
            5 => Average(Average(l, tr), t),
            6 => Average(l, tl),
            7 => Average(l, t),
            8 => Average(tl, t),
            9 => Average(t, tr),
            10 => Average(Average(l, tl), Average(t, tr)),
            11 => Select(l, t, tl),
            12 => Channels(l, t, tl, (a, b, c) => a + b - c),
            _ => Channels(Average(l, t), tl, 0, (a, b, _) => a + (a - b) / 2),
        };
    }

    private static uint Average(uint a, uint b) =>
        (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

    private static uint Select(uint l, uint t, uint tl)
    {
        int pl = 0, pt = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            int cl = (int)((l >> shift) & 0xFF), ct = (int)((t >> shift) & 0xFF), ctl = (int)((tl >> shift) & 0xFF);
            var estimate = cl + ct - ctl;
            pl += Math.Abs(estimate - cl);
            pt += Math.Abs(estimate - ct);
        }

        return pl < pt ? l : t;
    }

    // Each channel through f, clamped to 0–255.
    private static uint Channels(uint a, uint b, uint c, Func<int, int, int, int> f)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var value = f((int)((a >> shift) & 0xFF), (int)((b >> shift) & 0xFF), (int)((c >> shift) & 0xFF));
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }

    private static uint Subtract(uint a, uint b)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            result |= ((((a >> shift) & 0xFF) - ((b >> shift) & 0xFF)) & 0xFF) << shift;
        }

        return result;
    }

    // An entropy-coded image (§5): its colour cache flag and size, for the main image the meta prefix code flag (one
    // group), the five prefix codes (green with the length codes and the cache's, red, blue, alpha, distance), then the
    // pixels as their tokens.
    private static void WriteImage(Vp8lBitWriter w, uint[] pixels, int width, int cacheBits, bool main)
    {
        var tokens = Tokens(pixels, width, cacheBits);
        var cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        int[] green = new int[256 + LengthCodes + cacheSize], red = new int[256], blue = new int[256], alpha = new int[256], distance = new int[40];
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case Literal:
                    green[(t.Value >> 8) & 0xFF]++;
                    red[(t.Value >> 16) & 0xFF]++;
                    blue[t.Value & 0xFF]++;
                    alpha[t.Value >> 24]++;
                    break;
                case Cached:
                    green[256 + LengthCodes + (int)t.Value]++;
                    break;
                default:
                    green[256 + PrefixOf(t.Length).Prefix]++;
                    distance[PrefixOf(t.Distance).Prefix]++;
                    break;
            }
        }

        w.Write(cacheBits > 0 ? 1u : 0u, 1);
        if (cacheBits > 0)
        {
            w.Write((uint)cacheBits, 4);
        }

        if (main)
        {
            w.Write(0, 1);                                                    // no meta prefix codes
        }

        var codes = new[] { PrefixCode.Build(green), PrefixCode.Build(red), PrefixCode.Build(blue), PrefixCode.Build(alpha), PrefixCode.Build(distance) };
        foreach (var code in codes)
        {
            code.WriteCode(w);
        }

        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case Literal:
                    codes[0].WriteSymbol(w, (int)((t.Value >> 8) & 0xFF));
                    codes[1].WriteSymbol(w, (int)((t.Value >> 16) & 0xFF));
                    codes[2].WriteSymbol(w, (int)(t.Value & 0xFF));
                    codes[3].WriteSymbol(w, (int)(t.Value >> 24));
                    break;
                case Cached:
                    codes[0].WriteSymbol(w, 256 + LengthCodes + (int)t.Value);
                    break;
                default:
                    var length = PrefixOf(t.Length);
                    codes[0].WriteSymbol(w, 256 + length.Prefix);
                    w.Write((uint)length.Extra, length.ExtraBits);
                    var distanceCode = PrefixOf(t.Distance);
                    codes[4].WriteSymbol(w, distanceCode.Prefix);
                    w.Write((uint)distanceCode.Extra, distanceCode.ExtraBits);
                    break;
            }
        }
    }

    // A length or distance code as a prefix and extra bits (§5.2.2): 1–4 the prefixes 0–3, then for v = value − 1 the
    // prefix from its two highest bits, the bits below as extra.
    private static (int Prefix, int Extra, int ExtraBits) PrefixOf(int value)
    {
        var v = value - 1;
        if (v < 2)
        {
            return (v, 0, 0);
        }

        var highest = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);
        var second = (v >> (highest - 1)) & 1;
        var extraBits = highest - 1;
        return (2 * highest + second, v & ((1 << extraBits) - 1), extraBits);
    }

    // The pixels as tokens: backward references to the longest earlier match (at least 3 pixels, found through chains
    // of the positions of each pixel pair), else a colour cache hit, else a literal; every pixel goes into the cache as
    // the decoder puts it there. A distance of one row, or one pixel, takes its neighbourhood code (§5.2.2).
    private static List<Token> Tokens(uint[] pixels, int width, int cacheBits)
    {
        var tokens = new List<Token>();
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        var cacheValid = cacheBits > 0 ? new bool[1 << cacheBits] : null;
        const int HashBits = 16;
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var chain = new int[pixels.Length];
        int Hash(int i) => (int)(((pixels[i] * 0x1E35A7BDu) ^ (pixels[i + 1] * 0x9E3779B1u)) >> (32 - HashBits));
        void Insert(int i)
        {
            if (i + 1 < pixels.Length)
            {
                var h = Hash(i);
                chain[i] = head[h];
                head[h] = i;
            }
        }

        void Cache(uint p)
        {
            if (cache is not null)
            {
                var key = (int)((p * 0x1E35A7BDu) >> (32 - cacheBits));
                cache[key] = p;
                cacheValid![key] = true;
            }
        }

        for (var i = 0; i < pixels.Length;)
        {
            int bestLength = 0, bestFrom = 0;
            if (i + MinCopy <= pixels.Length)
            {
                var depth = 0;
                for (var j = head[Hash(i)]; j >= 0 && depth < ChainDepth && i - j <= MaxDistance; j = chain[j], depth++)
                {
                    var length = 0;
                    var limit = Math.Min(MaxCopy, pixels.Length - i);
                    while (length < limit && pixels[j + length] == pixels[i + length])
                    {
                        length++;
                    }

                    if (length > bestLength)
                    {
                        (bestLength, bestFrom) = (length, j);
                        if (length == limit)
                        {
                            break;
                        }
                    }
                }
            }

            if (bestLength >= MinCopy)
            {
                var d = i - bestFrom;
                var code = d == width ? 1 : d == 1 ? 2 : d + 120;
                tokens.Add(new Token(Copy, 0, bestLength, code));
                for (var k = 0; k < bestLength; k++)
                {
                    Insert(i + k);
                    Cache(pixels[i + k]);
                }

                i += bestLength;
                continue;
            }

            var p = pixels[i];
            if (cache is not null && (int)((p * 0x1E35A7BDu) >> (32 - cacheBits)) is var index && cacheValid![index] && cache[index] == p)
            {
                tokens.Add(new Token(Cached, (uint)index, 0, 0));
            }
            else
            {
                tokens.Add(new Token(Literal, p, 0, 0));
            }

            Insert(i);
            Cache(p);
            i++;
        }

        return tokens;
    }
}
