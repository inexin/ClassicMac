# IMA 4:1

`'ima4'` is IMA ADPCM [Author] in Apple's packet layout, decoded by the `ima4` `sdec` component (68k `sift` −16589,
PowerPC `nift` −16589); the two agree everywhere but on invalid step indexes (§3) [Code].

Contents

1. [Packets](#1-packets)
2. [When the preamble is read](#2-when-the-preamble-is-read)
3. [Nibbles](#3-nibbles)

---

## 1. Packets

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | preamble: the predictor in the upper 9 bits, the step index in the low 7 |
| +2 | 32 | u8[] | 64 samples, 4 bits each, **low nibble first** |

[Code][Verified].

- A packet is 34 bytes per channel; channels alternate by packet, L R L R …, each with its own state [Code][Verified].
  The component handles 1 or 2 channels only [Code].
- Preamble: predictor = sign-extend16(word & $FF80) (the low 7 bits zero); step index = word & $7F, not clamped
  [Code].
- `numFrames` counts packets; the sound has 64 × `numFrames` frames [Code][Verified].
- A trailing partial packet is never decoded [Code].

---

## 2. When the preamble is read

The component decodes in batches of min(remaining packets, maxFrames / 64), and `maxFrames` is at most 1024, so a
batch is **16 packets**. It looks at a preamble only for the first packet of each batch, and uses it only when it
differs from the running state [Code][Verified]:

```
per channel: pred = 0, index = 0
for packet p = 0, 1, … of the channel:
    if p mod 16 == 0:
        pp = sign-extend16(preamble & $FF80);  pi = preamble & $7F
        if not (pi == index and |pp - pred| <= 127):
            pred = pp
            index = pi
    # the preambles of the other 15 packets are skipped unread
    for each of the 32 data bytes b:
        nibble(b & $0F)
        nibble(b >> 4)
```

- The running state starts at 0 when the component opens and is reset by `StopSource` [Code].
- On streams from Apple's own compressor this gives the same result as reading every preamble; on spliced data, a
  decoder that reads every preamble differs [Code].

---

## 3. Nibbles

```
nibble(n):
    step = STEP[index]
    d = step >> 3
    if n & 4: d += step
    if n & 2: d += step >> 1
    if n & 1: d += step >> 2
    if n & 8: d = -d
    pred  = clamp(pred + d, -32768, 32767)
    index = clamp(index + INDEX[n], 0, 88)
    emit pred
```

- The difference is built by shifts that truncate, as the IMA recommendation gives it [Author][Code]. The 68k code
  uses a precomputed 89 × 16 table of these values; a multiplying form would differ in 404 of its 1424 entries
  [Code].
- The predictor is clamped before the index is updated [Code].
- A preamble index above 88 is used once, for the next nibble, before it is clamped; Apple's code then reads past its
  table, into memory that differs between 68k and PowerPC [Code]. ClassicMac uses `STEP[88]` for that nibble.
- Output: 16-bit signed samples [Code][Verified]. Asked for 8-bit output, the component gives
  `((pred >> 8) & $FF) XOR $80` [Code]. ClassicMac writes 16-bit.

Tables [Author][Code] (Apple's are the standard ones):

```
STEP = [     7,     8,     9,    10,    11,    12,    13,    14,    16,    17,
            19,    21,    23,    25,    28,    31,    34,    37,    41,    45,
            50,    55,    60,    66,    73,    80,    88,    97,   107,   118,
           130,   143,   157,   173,   190,   209,   230,   253,   279,   307,
           337,   371,   408,   449,   494,   544,   598,   658,   724,   796,
           876,   963,  1060,  1166,  1282,  1411,  1552,  1707,  1878,  2066,
          2272,  2499,  2749,  3024,  3327,  3660,  4026,  4428,  4871,  5358,
          5894,  6484,  7132,  7845,  8630,  9493, 10442, 11487, 12635, 13899,
         15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 ]        indexes 0–88

INDEX = [ -1, -1, -1, -1, 2, 4, 6, 8,  -1, -1, -1, -1, 2, 4, 6, 8 ]           nibbles 0–15
```
