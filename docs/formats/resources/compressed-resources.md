# Compressed resources

System 7 added compressed resources: a resource whose attribute bit 0 is set and whose data begins with an 18-byte
extended header. The Resource Manager decompresses it when it loads the resource (CheckLoad), with the `'dcmp'`
resource whose ID the header names; the System supplies `'dcmp'` 0 to 3, and applications may supply their own. Apple
never documented the format; everything here comes from the code of the 68k ROM and the Mac OS 9.0 System. ClassicMac
keeps the stored bytes and decompresses on request with its own implementations of `'dcmp'` 0–3. The resource fork
itself is in [resource-fork.md](resource-fork.md).

| | |
| --- | --- |
| Identified by | Resource attribute bit 0 (`resCompressed`) and the signature `$A89F6572` at +$00 of the data |
| ClassicMac | Reads (decompresses); `ClassicMac.Resources.Compression.ResourceDecompression` (`GetData`), `IResourceDecompressor` |
| Verified against | Mac OS 9.0 in SheepShaver: crafted compressed resources loaded through the Resource Manager<br>Apple's `'dcmp'` 2 and 3 run in an emulator: the 34 compressed resources of the Mac OS 9.0 System and crafted inputs |
| Sources | The Mac OS 9.0 System's native Resource Manager and its `'dcmp'` 0–3, the 68k Resource Manager in ROM `$077D` (disassembly). Other implementations (behaviour only): resource_dasm, macresources |

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

[Code: 68k ROM] [Code: Mac OS 9.0] are the Resource Managers as in [resource-fork.md](resource-fork.md#11-conventions);
[Code: `'dcmp'` n] is the Mac OS 9.0 System's `'dcmp'` n. Error codes: `badExtResource` −185, `CantDecompress` −186,
`resNotFound` −192, `memFullErr` −108.

### 1.1 The extended header

18 bytes, then the compressed data: a common part, then one of two tails chosen by the version byte [Code: 68k ROM]
[Code: Mac OS 9.0].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `u32`: `$A89F6572`, the robustness signature |
| +$04 | 2 | Header length | `u16`: 18; never read |
| +$06 | 1 | Version | `u8`: 8 or 9 |
| +$07 | 1 | Header attributes | `u8`; bit 0 = `resCompressed` |
| +$08 | 4 | Decompressed size | `u32` |

Version 8, for decompressors with a working buffer:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$0C | 1 | Working-buffer fraction | `u8`, out of 256 (§2.3) |
| +$0D | 1 | Expansion bytes | `u8` |
| +$0E | 2 | `'dcmp'` ID | `i16` |
| +$10 | 2 | Reserved | `u16`, must be 0 |

Version 9:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$0C | 2 | `'dcmp'` ID | `i16` |
| +$0E | 2 | Expansion bytes | `u16` |
| +$10 | 1 | param1 | `u8`, for the decompressor |
| +$11 | 1 | param2 | `u8`, for the decompressor |

- The compressed data always starts at +$12, whatever the header-length field says [Code: 68k ROM] [Code: Mac OS
  9.0].
- The expansion bytes are the margin the block is enlarged by, so that decompressing in place does not overwrite
  unread input (§2.2).
- param1 and param2 are not read by the Resource Manager; it passes the header to the decompressor [Code: Mac OS 9.0].
- Version 8's reserved word must be zero; otherwise both Resource Managers fail with `CantDecompress` [Code: 68k ROM]
  [Code: Mac OS 9.0] [Verified].
- `badExtResource` (−185) is defined but never returned [Code: 68k ROM] [Code: Mac OS 9.0].

### 1.2 `'dcmp'` 0 and 1: opcodes

Two byte-oriented decompressors for 68k code and data, both in the version-8 form, sharing their memo-table and
extension code and differing in the opcode map and the constant table [Code: `'dcmp'` 0, 1]. Each opcode is one byte:

| `'dcmp'` 0 | `'dcmp'` 1 | Action |
| --- | --- | --- |
| `00` v | `D0` v | Literal: copy (2v) & `$FFFF` bytes (`'dcmp'` 0) or v & `$FFFF` bytes (`'dcmp'` 1) from the input |
| `01`–`0F` | `00`–`0F` | Literal: copy 2n bytes (`'dcmp'` 0) or n + 1 bytes (`'dcmp'` 1) |
| `10` v | `D1` v | As `00`/`D0`, and remember the string (§2.6) |
| `11`–`1F` | `10`–`1F` | A literal of 2(n − `$10`) bytes (`'dcmp'` 0) or n − `$0F` bytes (`'dcmp'` 1), remembered |
| `20` b | `D2` b | Recall slot b + `$28` (`'dcmp'` 0) or b + `$B0` (`'dcmp'` 1) |
| `21` b | `D3` b | Recall slot b + `$128` or b + `$1B0` |
| `22` w | `D4` w | Recall slot (w + `$28`) & `$FFFF` or (w + `$B0`) & `$FFFF` |
| `23`–`4A` | `20`–`CF` | Recall slot n − `$23` or n − `$20` |
| `4B`–`FD` | `D5`–`FD` | Write the constant word table[n − `$4B`] or table[n − `$D5`] (§1.4) |
| `FE` | `FE` | Extension (§1.3) |
| `FF` | `FF` | End |

v is a varint, b a byte, w a big-endian `u16`. A varint [Code: `'dcmp'` 0, 1]: read a byte x;

1. x < `$80`: x;
2. x = `$FF`: the next four bytes, as a 32-bit value;
3. otherwise: (x − `$C0`) × 256 + the next byte. So first bytes `$C0`–`$FE` give 0 to `$3EFF`, and `$80`–`$BF` give
   −`$4000` to −1.

### 1.3 `'dcmp'` 0 and 1: extensions (FE)

`FE` is followed by a sub-opcode byte. Counts are taken as their low 16 bits, unsigned (a `dbf` loop); values written
as words are their low 16 bits [Code: `'dcmp'` 0, 1].

| Sub-op | Operands (varints unless noted) | Output |
| --- | --- | --- |
| 0 | seg, cnt, then cnt deltas | An export table: index starts at 6; for each delta, index = (index + delta − 6) & `$FFFF`, then the words `3F3C` seg `A9F0` index. After exactly cnt entries, one more `3F3C` seg `A9F0` |
| 1 | target, a5Δ, cnt, a5 | A jump table of (cnt & `$FFFF`) + 1 entries `6100` target `4EED` a5; before each entry after the first, target −= 8 and a5 += a5Δ, or, when a5Δ & `$FFFF` = 0, a5 = the next varint |
| 2 | value, cnt | (cnt & `$FFFF`) + 1 bytes of value (its low byte) |
| 3 | value, cnt | (cnt & `$FFFF`) + 1 words of value |
| 4 | value, cnt, then signed bytes | (cnt & `$FFFF`) + 1 words: value, then each next one plus a signed byte from the input |
| 5 | value, cnt, then varints | As 4, with varint deltas |
| 6 | value, cnt, then varints | (cnt & `$FFFF`) + 1 longs, with varint deltas (32-bit) |
| 7–255 | — | Nothing: only the sub-opcode byte is consumed, and decoding continues |

### 1.4 `'dcmp'` 0 and 1: constant tables

The words are immediates in the decompressors' dispatch tables [Code: `'dcmp'` 0, 1]; each row starts at the opcode
given.

`'dcmp'` 0, opcodes `4B`–`FD` (179 words):

```
4B: 0000 4EBA 0008 4E75 000C 4EAD 2053 2F0B 6100 0010 7000 2F00 486E 2050 206E 2F2E
5B: FFFC 48E7 3F3C 0004 FFF8 2F0C 2006 4EED 4E56 2068 4E5E 0001 588F 4FEF 0002 0018
6B: 6000 FFFF 508F 4E90 0006 266E 0014 FFF4 4CEE 000A 000E 41EE 4CDF 48C0 FFF0 2D40
7B: 0012 302E 7001 2F28 2054 6700 0020 001C 205F 1800 266F 4878 0016 41FA 303C 2840
8B: 7200 286E 200C 6600 206B 2F07 558F 0028 FFFE FFEC 22D8 200B 000F 598F 2F3C FF00
9B: 0118 81E1 4A00 4EB0 FFE8 48C7 0003 0022 0007 001A 6706 6708 4EF9 0024 2078 0800
AB: 6604 002A 4ED0 3028 265F 6704 0030 43EE 3F00 201F 001E FFF6 202E 42A7 2007 FFFA
BB: 6002 3D40 0C40 6606 0026 2D48 2F01 70FF 6004 1880 4A40 0040 002C 2F08 0011 FFE4
CB: 2140 2640 FFF2 426E 4EB9 3D7C 0038 000D 6006 422E 203C 670C 2D68 6608 4A2E 4AAE
DB: 002E 4840 225F 2200 670A 3007 4267 0032 2028 0009 487A 0200 2F2B 0005 226E 6602
EB: E580 670E 660A 0050 3E00 660C 2E00 FFEE 206D 2040 FFE0 5340 6008 0480 0068 0B7C
FB: 4400 41E8 4841
```

`'dcmp'` 1, opcodes `D5`–`FD` (41 words):

```
D5: 0000 0001 0002 0003 2E01 3E01 0101 1E01 FFFF 0E01 3100 1112 0107 3332 1239 ED10
E5: 0127 2322 0137 0706 0117 0123 00FF 002F 070E FD3C 0135 0115 0102 0007 003E 05D5
F5: 0201 0607 0708 3001 0133 0010 1716 373E 3637
```

### 1.5 `'dcmp'` 2: the default table

The 256 words of "GreggyBits" [Code: `'dcmp'` 2]:

```
00: 0000 0008 4EBA 206E 4E75 000C 0004 7000 0010 0002 486E FFFC 6000 0001 48E7 2F2E
10: 4E56 0006 4E5E 2F00 6100 FFF8 2F0B FFFF 0014 000A 0018 205F 000E 2050 3F3C FFF4
20: 4CEE 302E 6700 4CDF 266E 0012 001C 4267 FFF0 303C 2F0C 0003 4ED0 0020 7001 0016
30: 2D40 48C0 2078 7200 588F 6600 4FEF 42A7 6706 FFFA 558F 286E 3F00 FFFE 2F3C 6704
40: 598F 206B 0024 201F 41FA 81E1 6604 6708 001A 4EB9 508F 202E 0007 4EB0 FFF2 3D40
50: 001E 2068 6606 FFF6 4EF9 0800 0C40 3D7C FFEC 0005 203C FFE8 DEFC 4A2E 0030 0028
60: 2F08 200B 6002 426E 2D48 2053 2040 1800 6004 41EE 2F28 2F01 670A 4840 2007 6608
70: 0118 2F07 3028 3F2E 302B 226E 2F2B 002C 670C 225F 6006 00FF 3007 FFEE 5340 0040
80: FFE4 4A40 660A 000F 4EAD 70FF 22D8 486B 0022 204B 670E 4AAE 4E90 FFE0 FFC0 002A
90: 2740 6702 51C8 02B6 487A 2278 B06E FFE6 0009 322E 3E00 4841 FFEA 43EE 4E71 7400
A0: 2F2C 206C 003C 0026 0050 1880 301F 2200 660C FFDA 0038 6602 302C 200C 2D6E 4240
B0: FFE2 A9F0 FF00 377C E580 FFDC 4868 594F 0034 3E1F 6008 2F06 FFDE 600A 7002 0032
C0: FFCC 0080 2251 101F 317C A029 FFD8 5240 0100 6710 A023 FFCE FFD4 2006 4878 002E
D0: 504F 43FA 6712 7600 41E8 4A6E 20D9 005A 7FFF 51CA 005C 2E00 0240 48C7 6714 0C80
E0: 2E9F FFD6 8000 1000 4842 4A6B FFD2 0048 4A47 4ED1 206F 0041 600C 2A78 422E 3200
F0: 6574 6716 0044 486D 2008 486C 0B7C 2640 0400 0068 206D 000D 2A40 000B 003E 0220
```

### 1.6 `'dcmp'` 3: length codes

Bits are read most significant first [Code: `'dcmp'` 3].

Copy length, 0–2042. Count up to ten leading 1 bits (k); the 0 that ends them is consumed only when k < 10:

| k | Then | Length |
| --- | --- | --- |
| 0 | 1 bit x | x (0 or 1) |
| 1 | 1 bit: 0 | 2 |
| 1 | 1 bit: 1, then 1 bit x | 3 + x |
| 2 | 1 bit: 0, then 1 bit x | 5 + x |
| 2 | 1 bit: 1, then 2 bits x | 7 + x |
| 3 | 3 bits x | 11 + x |
| 4 | 3 bits x | 19 + x |
| 5–10 | k bits x | 2^k − 5 + x (27, 59, 123, 251, 507, 1019) |

Literal length, 1–63:

| Code | Length |
| --- | --- |
| `0` | 1 |
| `100` | 2 |
| `101` | 3 |
| `110` + 2 bits x | 4 + x |
| `111` + 4 bits s, s < 8 | 8 + s |
| `111` + 4 bits s, 8 ≤ s < 12, + 2 bits x | 16 + 4(s − 8) + x |
| `111` + 4 bits s, s ≥ 12, + 3 bits x | 32 + 8(s − 12) + x |

### 1.7 `'dcmp'` 3: offset codes

The offset code depends on written, the bytes output before the command. Three forms [Code: `'dcmp'` 3]:

- `0` + a bits x → 1 + x
- `10` + (a + 2) bits x → 1 + 2^a + x
- `11` + w bits x → base + x, with base = 1 + 2^a + 2^(a+2) and the width w chosen by written

| written | a | `10`: base | `11`: base | `11`: width by written |
| --- | --- | --- | --- | --- |
| 0–`$A` | 0 | 2 | 6 | ≤ 7: 1, ≤ 9: 2, else 3 |
| `$B`–`$14` | 1 | 3 | `$B` | ≤ `$C`: 1, ≤ `$E`: 2, ≤ `$12`: 3, else 4 |
| `$15`–`$28` | 2 | 5 | `$15` | ≤ `$16`: 1, ≤ `$18`: 2, ≤ `$1C`: 3, ≤ `$24`: 4, else 5 |
| `$29`–`$50` | 3 | 9 | `$29` | ≤ `$2A`: 1, ≤ `$2C`: 2, ≤ `$30`: 3, ≤ `$38`: 4, ≤ `$48`: 5, else 6 |
| `$51`–`$A0` | 4 | `$11` | `$51` | ≤ `$52`: 1, ≤ `$54`: 2, ≤ `$58`: 3, ≤ `$60`: 4, ≤ `$70`: 5, ≤ `$90`: 6, else 7 |
| `$A1`–`$2A0` | 5 | `$21` | `$A1` | ≤ `$A2`: 1, ≤ `$A4`: 2, ≤ `$A8`: 3, ≤ `$B0`: 4, ≤ `$C0`: 5, ≤ `$E0`: 6, ≤ `$120`: 7, ≤ `$1A0`: 8, else 9 |
| `$2A1`–`$3E8` | 6 | `$41` | `$141` | ≤ `$340`: 9, else 10 |
| `$3E9`–`$A80` | 7 | `$81` | `$281` | ≤ `$480`: 9, ≤ `$66C`: 10, else 11 |
| `$A81`–`$1500` | 8 | `$101` | `$501` | ≤ `$D00`: 11, else 12 |
| `$1501`–`$2A00` | 9 | `$201` | `$A01` | ≤ `$1A00`: 12, else 13 |
| `$2A01`–`$5400` | 10 | `$401` | `$1401` | ≤ `$3400`: 13, else 14 |
| `$5401`–`$A800` | 11 | `$801` | `$2801` | ≤ `$6800`: 14, else 15 |
| `$A801`–`$11170` | 12 | `$1001` | `$5001` | ≤ `$D000`: 15, else 16 |
| `$11171`–`$2A000` | 13 | `$2001` | `$A001` | ≤ `$12000`: 15, ≤ `$1A000`: 16, else 17 |
| above `$2A000` | 14 | `$4001` | `$14001` | ≤ `$34000`: 17, else 18 |

- The regular rule is: width k while written ≤ base − 1 + 2^k, for k = 1 … a + 3, else a + 4; the table shows the
  widths reachable in each range. The range limits `$3E8` and `$11170` are irregular.
- `$66C` replaces the regular `$680` for a = 7 and is live: with written from `$66D` to `$680` the third form reads 11
  bits [Code: `'dcmp'` 3] [Verified: Apple's code in an emulator].
- Two other irregular thresholds exist in the code but can never apply: `$200C` (7 bits) for a = 14, which runs only
  above `$2A000`; and `$288` for a = 7, which runs only from `$3E9` (Apple's code reads 3 bits there, as the regular
  rule gives) [Code: `'dcmp'` 3].
- The last width of each form is an unconditional else: there is no range check [Code: `'dcmp'` 3].

## 2. Reading

### 2.1 When data is decompressed

| Resource attribute bit 0 | Signature | Header attribute bit 0 | Result |
| --- | --- | --- | --- |
| Clear | Any | Any | Loaded as stored, header included [Verified] |
| Set | Absent | — | Loaded as stored [Verified] |
| Set | Present | Clear | "Extended, uncompressed": the 68k ROM loads length − 12 bytes from +$0C (the first 12 header bytes stripped) [Code: 68k ROM]; Mac OS 9 loads length − 12 bytes from +$00 (the header kept, the last 12 bytes lost) [Verified] |
| Set | Present | Set | Decompressed (§2.2) |

- Decompression happens only when ResLoad is on and the resource is not already loaded; otherwise later, in
  LoadResource [Code: 68k ROM] [Code: Mac OS 9.0].
- The version: the 68k ROM takes 8 as version 8, 9 as version 9, and fails anything else with `CantDecompress`; Mac
  OS 9 reads any value other than 8 as version 9 [Code: 68k ROM] [Code: Mac OS 9.0] [Verified].
- Sizes of an unloaded compressed resource: GetResourceSizeOnDisk gives the decompressed size on the 68k ROM and the
  decompressed size + expansion bytes on Mac OS 9 [Code] [Verified]; GetMaxResourceSize the on-disk gap
  ([resource-fork.md §1.4](resource-fork.md#14-the-data-area)) [Verified]; ReadPartialResource reads the raw stored
  bytes, header included [Verified].

### 2.2 The block and in-place decompression

[Code: 68k ROM] [Code: Mac OS 9.0] [Verified]

1. Allocate a block of decompressed size + expansion bytes.
2. Read the compressed bytes (stored length − 18) into the tail of the block.
3. The decompressor writes its output from the start of the block, reading from the tail: output may overtake the
   input, which is then corrupted exactly as the decompressor wrote it. Nothing pads the input; nothing after the
   block is cleared.
4. Afterwards the handle is set to the declared decompressed size, whatever was written, and any error from that is
   ignored: extra output is cut off, and short output is extended with the block's leftover contents [Verified].

### 2.3 The working buffer (version 8)

Version-8 decompressors get a working buffer [Code: 68k ROM] [Code: Mac OS 9.0]:

1. n = 0 when the fraction is 0, else (block × (fraction + 1)) >> 8, with block including the expansion bytes.
2. The 68k ROM uses a 32-bit multiply and a logical shift, and passes the size n + 4 (a locked handle) [Code: 68k ROM].
   Mac OS 9 uses a 32-bit multiply and an arithmetic shift, and passes the size n + 2 (NewPtr) [Code: Mac OS 9.0]
   [Verified: sizes 29, 2 and 110 for a 108-byte block with fractions `$3F`, `$00`, `$FF`].
3. The buffer is not cleared.

Version 9 has no working buffer.

### 2.4 Finding the decompressor

| | 68k ROM [Code: 68k ROM] | Mac OS 9 [Code: Mac OS 9.0] [Verified] |
| --- | --- | --- |
| Where | From the resource's own file down the map chain, Get1Resource(`'dcmp'`, ID) in each map | A native `'ncmp'` table first, then GetResource(`'dcmp'`, ID) through the whole chain from the current map |
| Password bit | Only maps with `decompressionPasswordBit` ([resource-fork.md §1.7](resource-fork.md#17-in-memory-map-flags-minmemoryattr-17)) are searched | Ignored |
| Not found | Nil with ResError 0 or −192 goes on to the next map; any other error is `CantDecompress` | §2.5 |
| Caching | None | |

- On Mac OS 9.0, `'ncmp'` 0 and 2 (native versions of `'dcmp'` 0 and 2) exist but are never used: they lack an entry
  point, and a file's own `'dcmp'` 0 overrode the System one [Verified]. So every decompressor runs as 68k code, and a
  file or application can supply or override any ID [Verified: overrides of 0, 1 and new IDs].
- The entry form is chosen by the resource header's version, not by the decompressor [Code: 68k ROM] [Code: Mac OS
  9.0]:
  - Version 8: the entry is at byte 0 of the `'dcmp'`; Pascal convention, arguments pushed as source, destination,
    working buffer, working size; the callee pops 16. The 68k ROM passes the entry in A0 and the resource handle in
    A5, and needs D3–D7 and A2–A6 preserved; Mac OS 9 calls it through CallUniversalProc with procInfo `$3FC0`.
  - Version 9: the `'dcmp'` starts with three `u16` offsets to Prepare(header), Decompress(source, destination,
    header) (pops 12) and Done(header). header points to the Resource Manager's copy of the 18-byte header. Mac OS 9
    passes garbage to Prepare and Done; the System's decompressors ignore them.
- A header version that does not match the decompressor's form (version 9 for `'dcmp'` 0 or 1, version 8 for 2 or 3)
  makes the Mac jump into the wrong entry point.

### 2.5 Failures

| Case | 68k ROM [Code: 68k ROM] | Mac OS 9 [Code: Mac OS 9.0] [Verified] |
| --- | --- | --- |
| No `'dcmp'` of that ID | Nil, `CantDecompress` | A handle of the decompressed size holding the untouched compressed input, ResError 0 |
| Version not 8 or 9 | `CantDecompress` | Decoded as version 9 |
| Version-8 reserved word ≠ 0 | Nil, `CantDecompress` | Nil, `CantDecompress` |
| Working buffer cannot be allocated | Nil, `memFullErr` (the map entry is left dangling) | Nil, `memFullErr` |
| I/O error | A garbage handle and the error | Nil and the error |

### 2.6 `'dcmp'` 0 and 1

[Code: `'dcmp'` 0, 1]

1. Read opcodes (§1.2) until `$FF`, the only terminator. The declared size is never checked, and neither the input
   nor the output is bounds-checked.
2. The remembered strings live in the working buffer (§2.3). Word 0 is the offset of the next free slot, starting at
   4; word 1 is the working size & `$FFFF`; slot i's start is the word at 4 + 2i, and its end the word before it (for
   slot 0, word 1). All values are 16-bit.
3. Remember a string of length bytes: slot = word 0; start = (word at slot − 2) − length; store start at slot; word 0
   = slot + 2; copy the length input bytes into the buffer at start before they are copied to the output.
4. Recall slot i: start = word at 4 + 2i; length = (word at 4 + 2i − 2) − start, 16-bit; copy those buffer bytes to
   the output.

Strings grow down from the end of the buffer and slot words up from offset 4, with no capacity check: when the table
fills, they overwrite each other. A slot that was never filled reads whatever the buffer held.

### 2.7 `'dcmp'` 2

"GreggyBits": 16-bit words looked up in a 256-word table, optionally mixed with literal words; version-9 form [Code:
`'dcmp'` 2].

1. Custom table. If param2 bit 0 is set, (param1 + 1) big-endian words follow at the start of the input and are copied
   into a 256-word custom table kept inside the decompressor's code, which is used instead of the default table (§1.5).
   The table is never cleared: entries above param1 keep the words an earlier resource put there (zero after the
   decompressor is freshly loaded) [Code: `'dcmp'` 2] [Verified: Apple's code in an emulator]. Indexes are not checked
   against param1.
2. Words. words = decompressed size >> 1.
   - param2 bit 1 clear: each input byte is a table index; write that table word. The loop is a do-while, so a
     declared size of 0 or 1 still writes one word [Code: `'dcmp'` 2] [Verified: Apple's code in an emulator].
   - param2 bit 1 set: for each group of 8 words, one flag byte, most significant bit first: 1 = an index byte (write
     the table word), 0 = two literal bytes. The (words & 7) leftover words use the top bits of one more flag byte.
3. Odd size. If the decompressed size is odd, one final input byte is copied as it is, in both modes.

No other param2 bits are used.

### 2.8 `'dcmp'` 3

A bit-stream LZ77 codec in the version-9 form; every compressed resource in the Mac OS 9.0 System uses it (34
resources) [Code: `'dcmp'` 3]. Disk Copy's KenCode is the same codec ([kencode.md](../codecs/kencode.md)). Only the
header's decompressed size (+$08) is read; version, ID, expansion bytes, param1 and param2 are ignored [Code:
`'dcmp'` 3].

Bits are read most significant first. A literal byte is the next 8 bits of the stream, not byte-aligned. Apple's reader
for 9 or more bits prefetches up to three bytes; reading a byte at a time gives the same values. Past the end of the
input the Mac reads whatever memory follows: nothing checks it. [Code: `'dcmp'` 3]

A flag, literal allowed, starts true. While written < decompressed size (unsigned) [Code: `'dcmp'` 3]:

1. Read a copy length L (§1.6, 0–2042).
2. If L = 0 and literal allowed: a literal run. Read n (§1.6, 1–63) and copy n 8-bit values from the stream. Literal
   allowed = (n = 63): only a full 63-byte run may be followed by another.
3. Otherwise a back-reference of L + 2 bytes, plus 1 if literal allowed was false (so a back-reference straight after
   a short literal run is at least 3 bytes). Literal allowed = true. Read an offset (§1.7) chosen by written before
   this command, and copy forward, a byte at a time, from written − offset: an offset shorter than the length repeats
   the pattern.

- Offset 0 cannot be encoded. An offset greater than written is not checked: the Mac reads the bytes before the
  output. No real resource does this. [Code: `'dcmp'` 3]
- There is no end code: decoding stops when written reaches the declared size, checked only between commands. The
  last command is completed, so output may run up to 2044 bytes past the declared size (a back-reference of 2042 + 3
  bytes starting one byte short of it), or 62 for a literal run [Code: `'dcmp'` 3] [Verified: Apple's code in an
  emulator]. It lands in the expansion bytes and, beyond them, in whatever memory follows the block; the Resource
  Manager then cuts the handle to the declared size (§2.2).
- A declared size of 0 writes nothing.
- All 34 System resources consume exactly their input, to the last partial byte [Verified].

## 3. Writing

None. ClassicMac writes compressed resources only as they were read.

## 4. Variants

Where the two Resource Managers differ:

| Area | Mac OS 9 | 68k ROM | § |
| --- | --- | --- | --- |
| A version other than 8 or 9 | Read as version 9 | `CantDecompress` | 2.1 |
| Extended, uncompressed | Header kept, last 12 bytes lost | First 12 header bytes stripped | 2.1 |
| GetResourceSizeOnDisk of an unloaded compressed resource | Decompressed size + expansion bytes | Decompressed size | 2.1 |
| Working-buffer size | n + 2, arithmetic shift | n + 4, logical shift | 2.3 |
| Finding the `'dcmp'` | `'ncmp'`, then the whole chain from the current map; password bit ignored | Own file down the chain; only maps with the password bit | 2.4 |
| No `'dcmp'` of that ID | The compressed input in a handle, no error | `CantDecompress` | 2.5 |
| I/O error | Nil and the error | A garbage handle and the error | 2.5 |

The decompressors themselves are the same 68k code under both (§2.4). `'dcmp'` 0–3 of System versions other than 9.0
were not compared.

## 5. ClassicMac

- The model keeps the stored bytes; `ResourceDecompression.GetData` returns what the Resource Manager would hand an
  application, following the Resource Manager `ReadOptions.ResourceManager` selects (`MacOS9`, the default, or
  `Rom68k`; [resource-fork.md §5.1](resource-fork.md#51-the-resource-manager-model)). It returns the stored bytes
  whenever it cannot decompress, whatever the Mac would have returned. [ClassicMac]
- A resource marked compressed without the signature is used as stored and reported (`resource.not-compressed`). The
  extended-uncompressed case is reported in the Mac OS 9 model (`resource.extended-uncompressed`); the 68k ROM model
  strips 12 bytes silently. [ClassicMac]
- An extended header shorter than 18 bytes is an error (`resource.dcmp-header`). [ClassicMac]
- A decompressed size over `ReadOptions.MaxResourceSize` (default 64 MiB) is refused (`resource.too-large`).
  [ClassicMac]
- ClassicMac runs its own implementations of `'dcmp'` 0–3 and any registered with it, never a fork's `'dcmp'` and
  never another file's. When the fork holds a `'dcmp'` of the needed ID that the Mac would run instead (any, on Mac OS
  9; only in a map with the password bit, on the 68k ROM), it reports `resource.dcmp-overridden` and still uses its
  own. A header version that does not match the decompressor's form is `resource.dcmp-form`. [ClassicMac]
- The block (§2.2) is followed by 2048 zero bytes standing in for the memory after it: enough for `'dcmp'` 3's largest
  overshoot (§2.8); the emulator's memory there was zero. A decompressor may read or write that area; anything beyond
  it, or before the block, stops decompression with `resource.dcmp-overrun`. Compressed data longer than the block is
  refused the same way. The result is the first decompressed-size bytes of the block. [ClassicMac]
- `'dcmp'` 0 and 1: the working buffer starts zeroed and is capped at 64 KiB, all a 16-bit offset can reach. A slot
  recalled before it is filled is reported once (`resource.dcmp-undefined-slot`); a string beyond the buffer stops
  decompression (`resource.dcmp-overrun`). Running off the input is stopped the same way. [ClassicMac]
- `'dcmp'` 2: ClassicMac models a freshly loaded decompressor: custom-table entries above param1 are zero, and using
  one is reported once (`resource.dcmp2-stale-table`). [ClassicMac]
- `'dcmp'` 3: ClassicMac allows the overshoot into its 2 KiB after the block (`resource.dcmp-wrote-past-block` when it
  passes the expansion bytes), reads zeros past the input (`resource.dcmp-read-past-input`), and stops at an offset
  past the output (`resource.dcmp-overrun`). [ClassicMac]
- Fewer bytes written than declared are reported (`resource.dcmp-size`); the rest is the block's leftover contents,
  as on the Mac. [ClassicMac]
- `'dcmp'` 3 was ported from resource_dasm's `System3.cc` (MIT; `THIRD-PARTY-NOTICES.md`), then checked against the
  disassembly of the Mac OS 9.0 System's `'dcmp'` 3. [ClassicMac]
- Application-supplied decompressors [ClassicMac]. ClassicMac cannot run 68k code, so an application of ClassicMac
  supplies a decompressor for a private `'dcmp'`:
  1. Implement `IResourceDecompressor`: an `Id` (the `'dcmp'` ID) and `int Decompress(DecompressionContext context)`.
  2. Pass it to `new ResourceDecompression(extra)`. A decompressor with a built-in ID (0–3) replaces the built-in one,
     as a file's own `'dcmp'` would on the Mac; among several with one ID, the last wins.
  3. Decompress in place, writing from offset 0, and return the number of bytes written. The result is cut or padded
     to the declared size as in §2.2. Throw `InvalidDataException` for bad input; it, `IndexOutOfRangeException` and
     `ArgumentException` become `resource.dcmp-failed`, and the stored bytes are returned.

  The context models the Mac's call:

  | Member | Meaning |
  | --- | --- |
  | `Header` | The parsed 18-byte header (§1.1), including param1/param2 or the version-8 fields |
  | `Block` | The block (decompressed size + expansion bytes) followed by 2048 zero bytes |
  | `BlockLength` | The length of the block proper |
  | `SourceOffset` | Where the compressed bytes start; they end at `BlockLength` |
  | `Options` | The `ReadOptions`, including which Resource Manager is modelled |
  | `Report(severity, code, message)` | Adds a diagnostic without stopping |

- Decompression diagnostics are reported by `ResourceDecompression.GetData`, their message prefixed with the resource
  (`'TYPE' id`). [ClassicMac]

## 6. Diagnostics

Fork diagnostics are in [resource-fork.md §6](resource-fork.md#6-diagnostics).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `resource.dcmp-failed` | Error | An application-supplied decompressor threw | Returns the stored bytes | — |
| `resource.dcmp-form` | Error | The header version does not match the decompressor's entry form | Returns the stored bytes | Jumps into the wrong entry point (§2.4) |
| `resource.dcmp-header` | Error | The extended header is shorter than 18 bytes, or a version-8 reserved word is not zero | Returns the stored bytes | `CantDecompress` for the reserved word; a short header is not traced |
| `resource.dcmp-overridden` | Info | The fork carries its own `'dcmp'` of that ID, which the Mac would run | Uses the built-in one | Runs the file's `'dcmp'` (§2.4) |
| `resource.dcmp-overrun` | Error | The compressed data do not fit the block, or decoding read or wrote outside the block and the 2 KiB after it, or referred to memory before the output | Returns the stored bytes | Reads or overwrites neighbouring memory |
| `resource.dcmp-read-past-input` | Warning | The decompressor read past its input | Reads zeros | Reads whatever memory follows |
| `resource.dcmp-size` | Warning | Fewer bytes written than declared | Keeps the block's leftover contents for the rest | The same: SetHandleSize to the declared size (§2.2) |
| `resource.dcmp-undefined-slot` | Warning | (`'dcmp'` 0/1) A memo slot was recalled before it was filled (reported once) | Uses zeros | Copies whatever the working buffer held |
| `resource.dcmp-unknown` | Warning | No decompressor with the header's ID | Returns the stored bytes | Mac OS 9: the compressed bytes in a handle, no error; 68k ROM: `CantDecompress` (§2.5) |
| `resource.dcmp-version` | Info (Mac OS 9 model), Error (68k ROM model) | The header version is not 8 or 9 | Mac OS 9 model: reads it as version 9; 68k ROM model: returns the stored bytes | Mac OS 9 decodes as version 9; the 68k ROM fails with `CantDecompress` |
| `resource.dcmp-wrote-past-block` | Info | The last command overshot the block and its expansion bytes, as `'dcmp'` 3 may | Cuts the result to size | Writes into the memory after the block |
| `resource.dcmp2-stale-table` | Info | (`'dcmp'` 2) A custom-table entry above param1 was used (reported once) | Uses zero | Uses a word left by an earlier resource |
| `resource.extended-uncompressed` | Warning | (Mac OS 9 model) The signature is present and header bit 0 clear | Keeps the header and drops the last 12 bytes | As §2.1; the 68k ROM strips the first 12 bytes |
| `resource.not-compressed` | Info | Marked compressed but without the signature | Uses the data as stored | Loads it as stored (§2.1) |
| `resource.too-large` | Error | The decompressed size is over `ReadOptions.MaxResourceSize` | Returns the stored bytes | Decompresses it if memory allows, else `memFullErr` |

## 7. Verification

- Crafted compressed resources loaded through Mac OS 9.0's Resource Manager in SheepShaver established the
  [Verified] rules of §2.1–§2.5: the extended-uncompressed case, the version handling, the sizes of unloaded resources,
  the working-buffer sizes (29, 2 and 110 for a 108-byte block), short output, the `'ncmp'` resources never used and
  overrides of `'dcmp'` 0, 1 and new IDs.
- Apple's `'dcmp'` 2 and 3, run in an emulator, established the table persistence and the one-word minimum of
  `'dcmp'` 2, and the `$66C` threshold and overshoot of `'dcmp'` 3; ClassicMac's `'dcmp'` 3 gives the same output as
  Apple's on all 34 compressed resources of the Mac OS 9.0 System and on crafted inputs [Verified]. The System's
  resources are Apple's and not in the repository.
- `tests/ClassicMac.Resources.Tests/DecompressionTests.cs`:
  - Resource Manager behaviour: uncompressed resources as stored; the bit without a header; a truncated header; the
    extended-uncompressed case in both models; an unknown version (version 9 on Mac OS 9, an error on the ROM); a
    non-zero reserved word; sizes over the limit; an unknown decompressor; a fork with its own `'dcmp'` (and the ROM
    needing the password bit); the entry form by header version; short output padded with the block; running off the
    input; an application-supplied decompressor.
  - `'dcmp'` 0 and 1: varints; literals and constants; remember and recall; an undefined slot reading the working
    buffer; every extension sub-op, an unknown one included; `'dcmp'` 1's opcodes.
  - `'dcmp'` 2: the default table; an odd final byte; one word for a size of 0; the custom table; the bitmap mode.
  - `'dcmp'` 3: literals and overlapping back-references; the overshoot into the memory after the block; zeros past
    the input; the `$66C` threshold; a back-reference before the start.
  - `Corpus_compressed_resources_decompress_to_their_declared_size`: every compressed resource under
    `CLASSICMAC_CORPUS` (not in the repository; skipped without it) is reported, never thrown, and those decompressed
    cleanly have their declared size.

## 8. Not covered

- `'dcmp'` 0–3 of System versions other than 9.0, and the native `'ncmp'` decompressors, which Mac OS 9.0 never uses
  (they differ from `'dcmp'` 0 and 2 only on bad input).
- Running an application's own `'dcmp'` (68k code); an application of ClassicMac supplies a replacement (§5).
- Compressing resources.

## 9. References

1. The Mac OS 9.0 System: its native Resource Manager (the `'Resources'` code fragment, CheckLoad) and its `'dcmp'`
   0–3 and `'ncmp'` 0 and 2, traced in disassembly.
2. The 68k Resource Manager in Mac OS ROM `$077D` (CheckLoad), traced in disassembly.
3. resource_dasm, <https://github.com/fuzziqersoftware/resource_dasm>, `System3.cc` and its `'dcmp'` 0–2. MIT;
   ClassicMac's `'dcmp'` 3 was ported from it, with notice in `THIRD-PARTY-NOTICES.md`. Its constant tables and
   opcode maps for `'dcmp'` 0 and 1 match Apple's. Its `'dcmp'` 2 compares a byte count with a word count and decodes
   only half the output, then falls back to emulating Apple's code; it ignores the table's persistence and declared
   sizes 0 and 1. Its `'dcmp'` 3 reads 4 bits at the `$288` threshold, and throws on an overshoot, a read past the
   input and an offset past the output.
4. macresources, <https://github.com/elliotnunn/macresources>, its `'dcmp'` 2. MIT; reference only.
5. [kencode.md](../codecs/kencode.md): Disk Copy's KenCode, the same codec as `'dcmp'` 3.
