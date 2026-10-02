# µ-law

`'ulaw'` is ITU-T G.711 µ-law: one byte per sample, expanded to a 16-bit sample. Mac OS 9 decodes it with an `sdec`
component; sampled sounds in `'snd '` resources name it as their format. ClassicMac decodes it to 16-bit WAV samples.

| | |
| --- | --- |
| Used by | [sound.md](../resources/sound.md) (format `'ulaw'` in a compressed sound header) |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Sound` (`SoundCodecs`) |
| Verified against | Sound Manager 3.5.1 on Mac OS 9.0, its own decoding of µ-law samples it compressed (mono) |
| Sources | ITU-T G.711; the Mac OS 9.0 System's `sdec` component −16593 (68k `sift` and PowerPC `nift`), traced |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Sample code | One per sample per channel |

- The header's `numFrames` counts samples [Code] [Verified].
- Channels interleave by byte: L, R, L, R … (68k `sift` and PowerPC `nift` alike) [Code]. Only mono was verified.

## 2. Reading

Each code expands to a 16-bit signed sample [Author: ITU-T G.711] [Code] [Verified]:

```
u = (NOT code) & $FF
t = (((u & $0F) << 3) + $84) << ((u >> 4) & 7)
sample = (u & $80) ? ($84 - t) : (t - $84)
```

The component's table is the standard G.711 one, all 256 entries [Code]. The samples range over ±32 124; `$FF` and
`$7F` give 0.

## 3. Writing

None.

## 4. Variants

- Asked for 8-bit output, the component gives the sample's high byte XOR `$80` [Code].

## 5. ClassicMac

- ClassicMac writes µ-law as 16-bit samples ([sound.md](../resources/sound.md)). [ClassicMac]
- A trailing partial frame (fewer bytes than channels) is dropped. [ClassicMac]

## 6. Diagnostics

None. Sample counts beyond the resource are reported by the sound reader (`sound.short`,
[sound.md](../resources/sound.md)).

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/SoundDecoderTests.cs`, `Sound_Manager_samples_decode_as_the_Sound_Manager_does`:
  with `CLASSICMAC_CORPUS` set, a mono µ-law sound (`ulawm.snd`) decodes byte for byte to Sound Manager 3.5.1's own
  16-bit output on Mac OS 9.0 in SheepShaver. Not committed.

## 8. Not covered

- Stereo µ-law against the Sound Manager's output.

## 9. References

1. ITU-T Recommendation G.711, *Pulse code modulation (PCM) of voice frequencies*.
2. Mac OS 9.0 System file, `sdec` component −16593 (`sift`, `nift`), traced in disassembly.
