# ADC

Apple Data Compression, the "Faster (ADC)" compression of Disk Copy 6: a byte-oriented LZ77 code in which every token
starts with an opcode byte, either a literal run or a match into the bytes already decoded. NDIF images store it as
chunk type `$83` and UDIF images as run type `$80000004`. ClassicMac decodes it.

| | |
| --- | --- |
| Used by | [ndif.md](../disk-images/ndif.md) (chunk type `$83`), [udif.md](../disk-images/udif.md) (run type `$80000004`) |
| ClassicMac | Reads; `ClassicMac.Files.Compression.Adc` |
| Verified against | Disk Copy 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF |
| Sources | Disk Copy 6.3.3's `hdi2` codec, traced |

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

A chunk is a sequence of tokens. The top bits of the opcode byte choose the token
[Code: Disk Copy 6.3.3 `hdi2` codec] [Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF].

Literal run, opcode `1LLLLLLL`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bit 7 set; bits 0–6 `L`: the length is `L` + 1 (1–128) |
| +$01 | length | Bytes | Copied to the output |

Short match, opcode `00LLLLDD`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bits 6–7 zero; bits 2–5 `L`: the length is `L` + 3 (3–18); bits 0–1: the distance's upper 2 bits |
| +$01 | 1 | Distance | The distance's lower 8 bits; the distance is the 10-bit value + 1 (1–1024) |

Long match, opcode `01LLLLLL`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bits 6–7 `01`; bits 0–5 `L`: the length is `L` + 4 (4–67) |
| +$01 | 2 | Distance | The 16-bit value + 1 (1–65,536) |

No state carries from one chunk to the next [Code: Disk Copy 6.3.3 `hdi2` codec].

## 2. Reading

The output is the chunk's decoded size, known from the image's map
[Code: Disk Copy 6.3.3 `hdi2` codec] [Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF]:

1. While the output is not full, read an opcode and its operands (§1).
2. If the token would pass the end of the output, stop: the chunk is damaged. This is checked before any of the token
   is written. Disk Copy reports −8819 (damaged) at read time.
3. A literal run copies its bytes to the output.
4. A match copies *length* bytes from *distance* bytes back in the output, one byte at a time, so a match may overlap
   what it writes (distance 1 repeats the last byte).
5. Stop when the output is full.

Disk Copy checks nothing else: a match reaching before the output's start reads the memory before its buffer, and
input is read past the stored length [Code: Disk Copy 6.3.3 `hdi2` codec].

## 3. Writing

None.

## 4. Variants

None.

## 5. ClassicMac

- A match reaching before the output's start, and input running out before the output is full, are reported as a
  damaged chunk (§6) instead of reading stray memory. [ClassicMac]
- A damaged chunk keeps the bytes decoded before the fault; the rest of the chunk reads as zeros
  ([ndif.md §5](../disk-images/ndif.md#5-classicmac), [udif.md §5](../disk-images/udif.md#5-classicmac)). [ClassicMac]

## 6. Diagnostics

The decoder reports how it ended; the image readers raise the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ndif.bad-chunk` | Error | An ADC chunk in an NDIF image overruns its output, runs out of input or reaches before its start | Keeps what was decoded; the rest reads as zeros | −8819 for an overrun; reads stray memory for the others |
| `udif.bad-run` | Error | An ADC run in a UDIF image fails the same way | Keeps what was decoded; the rest reads as zeros | Not traced |

## 7. Verification

- `tests/ClassicMac.Files.Tests/NdifTests.cs`: `ADC_opcodes_decode` (a literal run, a short match, an overlapping
  long match; a match before the start, truncated input, a token passing the end detected before anything is
  written); `Damaged_data_is_reported` (a match before any output in an image is `ndif.bad-chunk`).
- Disk Copy 6.3.3's NDIF images `S800 ADC.img`, `S800 ADC.smi`, `S5M ADC.img` and `S5M ADC del.img` decode to the
  read/write images of the same volumes, and its `ndiftest` images `adc_flip.img` and `adc_garb.img` are reported as
  damaged ([ndif.md §7](../disk-images/ndif.md#7-verification); not committed).
- `tests/ClassicMac.Files.Tests/UdifTests.cs`: `Every_run_type_decodes_and_the_checksums_match` (a synthetic ADC run)
  and `Disk_Copy_6_5_images_decode_to_their_device` (Disk Copy 6.5b13's compressed image, with its checksums;
  [udif.md §7](../disk-images/udif.md#7-verification); not committed).

## 8. Not covered

- Writing ADC.

## 9. References

1. Disk Copy 6.3.3 (Apple), its `hdi2` codec resources, traced in disassembly.
