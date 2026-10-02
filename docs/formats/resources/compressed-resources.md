# Compressed resources

Contents

1. [Compressed resources](#1-compressed-resources)
2. [`'dcmp'` 0 and 1](#2-dcmp-0-and-1)
3. [`'dcmp'` 2](#3-dcmp-2)
4. [`'dcmp'` 3](#4-dcmp-3)
5. [Application-supplied decompressors](#5-application-supplied-decompressors)
6. [Diagnostics](#6-diagnostics)
7. [Not covered](#7-not-covered)

---

## 1. Compressed resources

System 7 added compressed resources: a resource whose attribute bit 0 is set and whose data begins with an extended
header. The Resource Manager decompresses it when it loads the resource (CheckLoad), with a `'dcmp'` resource whose ID
the header names [Code: 68k ROM; Mac OS 9.0]. Apple never documented the format; everything here comes from the code.

**ClassicMac** keeps the stored bytes in the model and decompresses on request: `ResourceDecompression.GetData`
returns what the Resource Manager would hand an application.

### 1.1 The extended header

18 bytes, then the compressed data. A common part, then one of two tails chosen by the version byte [Code: 68k ROM;
Mac OS 9.0]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | robustness signature `$A89F6572` |
| +$04 | 2 | u16 | header length (18); **never read** |
| +$06 | 1 | u8 | version: 8 or 9 |
| +$07 | 1 | u8 | header attributes; bit 0 = `resCompressed` |
| +$08 | 4 | u32 | decompressed size |

Version 8 (for decompressors with a working buffer):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$0C | 1 | u8 | working-buffer fraction, out of 256 (§1.4) |
| +$0D | 1 | u8 | expansion bytes |
| +$0E | 2 | i16 | `'dcmp'` ID |
| +$10 | 2 | u16 | reserved, must be 0 |

Version 9:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$0C | 2 | i16 | `'dcmp'` ID |
| +$0E | 2 | u16 | expansion bytes |
| +$10 | 1 | u8 | param1, for the decompressor |
| +$11 | 1 | u8 | param2, for the decompressor |

- The compressed data **always starts at +18**, whatever the header-length field says [Code: 68k ROM; Mac OS 9.0].
- **Expansion bytes** are the margin the block is enlarged by so that decompressing in place does not overwrite unread
  input (§1.3).
- param1 and param2 are not read by the Resource Manager; it passes the header to the decompressor [Code].
- **Version.** The 68k ROM takes 8 as version 8, 9 as version 9, and fails anything else with `CantDecompress`; Mac OS 9
  reads **any value other than 8 as version 9** [Code: 68k ROM; Mac OS 9.0; Verified].
- **Version 8's reserved word** must be zero; otherwise both fail with `CantDecompress` [Code; Verified].
- `badExtResource` (−185) is defined but never returned [Code: 68k ROM; Mac OS 9.0].

### 1.2 When data is decompressed

| Resource attribute bit 0 | Signature | Header attribute bit 0 | Result |
| --- | --- | --- | --- |
| clear | any | any | loaded as stored, header included [Verified] |
| set | absent | — | loaded as stored [Verified] |
| set | present | clear | **"extended, uncompressed"**: 68k ROM: length − 12 bytes from +12 (the first 12 header bytes stripped) [Code]; Mac OS 9: length − 12 bytes from +0 (the header kept, the last 12 bytes lost) [Verified] |
| set | present | set | decompressed (§1.3) |

- Decompression happens only when ResLoad is on and the resource is not already loaded; otherwise later, in
  LoadResource [Code].
- **Sizes of an unloaded compressed resource:** GetResourceSizeOnDisk gives the decompressed size on the 68k ROM and
  the **decompressed size + expansion bytes** on Mac OS 9 [Code; Verified]; GetMaxResourceSize the on-disk gap ([resource-fork.md §4](resource-fork.md#4-the-data-area))
  [Verified]; ReadPartialResource reads the raw stored bytes, header included [Verified].

### 1.3 The block and in-place decompression

[Code: 68k ROM; Mac OS 9.0; Verified]

1. A block of **decompressed size + expansion bytes** is allocated.
2. The compressed bytes (stored length − 18) are read into the **tail** of the block.
3. The decompressor writes its output from the start of the block, reading from the tail: output may overtake the
   input, which is then corrupted exactly as the decompressor wrote it. Nothing pads the input; nothing after the block
   is cleared.
4. Afterwards the handle is set to the **declared decompressed size**, whatever was written, and any error from that is
   ignored: extra output is cut off, and short output is extended with the block's leftover contents [Verified].

**ClassicMac** reproduces this: the block is followed by **2048 zero bytes** standing in for the memory after it
(enough for `'dcmp'` 3's largest overshoot, §4.5; the emulator's memory there was zero). A decompressor may read or
write that area; anything beyond it, or before the block, stops decompression with `resource.dcmp-overrun`. Compressed
data longer than the block is refused the same way. The result is the first *decompressed size* bytes of the block.

### 1.4 The working buffer (version 8)

Version-8 decompressors get a working buffer [Code: 68k ROM; Mac OS 9.0]:

- n = 0 when the fraction is 0, else (block × (fraction + 1)) >> 8, with *block* including the expansion bytes.
- **68k ROM:** a 32-bit multiply and a logical shift; the size passed is **n + 4** (a locked handle) [Code: 68k ROM].
- **Mac OS 9:** a 32-bit multiply and an arithmetic shift; the size passed is **n + 2** (NewPtr) [Code: Mac OS 9.0;
  Verified: sizes 29, 2 and 110 for a 108-byte block with fractions `$3F`, `$00`, `$FF`].
- The buffer is not cleared.

Version 9 has no working buffer.

### 1.5 Finding the decompressor

| | 68k ROM [Code] | Mac OS 9 [Code; Verified] |
| --- | --- | --- |
| Where | from the resource's **own file** down the map chain, Get1Resource(`'dcmp'`, ID) in each map | a native `'ncmp'` table first, then GetResource(`'dcmp'`, ID) through the **whole chain from the current map** |
| Password bit | only maps with `decompressionPasswordBit` ([resource-fork.md §5.3](resource-fork.md#53-in-memory-map-flags-minmemoryattr-17)) are searched | ignored |
| Not found | nil with ResError 0 or −192 goes on to the next map; any other error is `CantDecompress` | see §1.6 |
| Caching | none | |

- On Mac OS 9.0, `'ncmp'` 0 and 2 (native versions of `'dcmp'` 0 and 2) exist but are never used: they lack an entry
  point, and a file's own `'dcmp'` 0 overrode the System one [Verified]. So **every decompressor runs as 68k code**,
  and a file or application can supply or override any ID [Verified: overrides of 0, 1 and new IDs].
- **The entry form is chosen by the resource header's version**, not by the decompressor [Code]:
  - **Version 8:** the entry is at byte 0 of the `'dcmp'`; Pascal convention, arguments pushed as source, destination,
    working buffer, working size; the callee pops 16. The 68k ROM passes the entry in A0 and the resource handle in A5,
    and needs D3–D7 and A2–A6 preserved; Mac OS 9 calls it through CallUniversalProc with procInfo `$3FC0`.
  - **Version 9:** the `'dcmp'` starts with three u16 offsets to Prepare(header), Decompress(source, destination,
    header) (pops 12) and Done(header). *header* points to the Resource Manager's copy of the 18-byte header. Mac OS 9
    passes garbage to Prepare and Done; the System's decompressors ignore them.
- **ClassicMac** runs its own implementations of `'dcmp'` 0–3 (§§2–4) and any registered with it (§5). When the fork
  holds a `'dcmp'` of the needed ID that the Mac would run instead (any, on Mac OS 9; only in a map with the password
  bit, on the 68k ROM), it notes `resource.dcmp-overridden` and still uses its own. It does not search other files.
  A header version that does not match the decompressor's form (version 9 for `'dcmp'` 0 or 1, version 8 for 2 or 3)
  is `resource.dcmp-form`: the Mac would jump into the wrong entry point.

### 1.6 Failures

| Case | 68k ROM [Code] | Mac OS 9 [Code; Verified] | ClassicMac |
| --- | --- | --- | --- |
| No `'dcmp'` of that ID | nil, `CantDecompress` | a handle of the decompressed size holding the **untouched compressed input**, ResError 0 | stored bytes, `resource.dcmp-unknown` |
| Version not 8 or 9 | `CantDecompress` | decoded as version 9 | as the model says; `resource.dcmp-version` |
| Version-8 reserved word ≠ 0 | nil, `CantDecompress` | nil, `CantDecompress` | stored bytes, `resource.dcmp-header` |
| Working buffer cannot be allocated | nil, `memFullErr` (the map entry is left dangling) | nil, `memFullErr` | — |
| I/O error | a garbage handle and the error | nil and the error | — |

ClassicMac returns the **stored bytes** whenever it cannot decompress, whatever the Mac would have returned.

---

## 2. `'dcmp'` 0 and 1

Two byte-oriented decompressors for 68k code and data, both in the version-8 form, sharing their memo-table and
extension code and differing in the opcode map and the constant table [Code: `'dcmp'` 0, 1]. resource_dasm (MIT)
implements the same two; its constant tables and opcode maps match Apple's.

### 2.1 Opcodes

Each opcode is one byte; decoding runs until opcode `$FF`, **the only terminator**. The declared size is never
checked, and neither the input nor the output is bounds-checked [Code].

| `'dcmp'` 0 | `'dcmp'` 1 | Action |
| --- | --- | --- |
| `00` v | `D0` v | literal: copy (2v) & `$FFFF` bytes (`'dcmp'` 0) or v & `$FFFF` bytes (`'dcmp'` 1) from the input |
| `01`–`0F` | `00`–`0F` | literal: copy 2n bytes (`'dcmp'` 0) or n + 1 bytes (`'dcmp'` 1) |
| `10` v | `D1` v | as `00`/`D0`, and **remember** the string (§2.3) |
| `11`–`1F` | `10`–`1F` | literal of 2(n − `$10`) bytes (`'dcmp'` 0) or n − `$0F` bytes (`'dcmp'` 1), remembered |
| `20` b | `D2` b | recall slot b + `$28` (`'dcmp'` 0) or b + `$B0` (`'dcmp'` 1) |
| `21` b | `D3` b | recall slot b + `$128` or b + `$1B0` |
| `22` w | `D4` w | recall slot (w + `$28`) & `$FFFF` or (w + `$B0`) & `$FFFF` |
| `23`–`4A` | `20`–`CF` | recall slot n − `$23` or n − `$20` |
| `4B`–`FD` | `D5`–`FD` | write the constant word *table*[n − `$4B`] or *table*[n − `$D5`] (§2.5) |
| `FE` | `FE` | extension (§2.4) |
| `FF` | `FF` | end |

*v* is a varint, *b* a byte, *w* a big-endian u16.

**Varint** [Code: `'dcmp'` 0, 1]: read a byte *x*:

- *x* < `$80`: *x*;
- *x* = `$FF`: the next four bytes, as a 32-bit value;
- otherwise: (*x* − `$C0`) × 256 + the next byte. So first bytes `$C0`–`$FE` give 0 to `$3EFF`, and **`$80`–`$BF`
  give −`$4000` to −1**.

### 2.2 The working buffer and memo table

The remembered strings live in the working buffer of §1.4 [Code: `'dcmp'` 0, 1]:

- word 0 = offset of the next free slot, starting at 4;
- word 1 = the working size & `$FFFF`;
- slot *i*'s start is the word at 4 + 2*i*, and its end the word before it (for slot 0, word 1).

All values are 16-bit. Strings grow down from the end of the buffer, slot words up from offset 4, with **no capacity
check**: when the table fills, they overwrite each other, as on the Mac.

### 2.3 Remember and recall

- **Remember** a string of *length* bytes: *slot* = word 0; *start* = (word at *slot* − 2) − *length*; store *start*
  at *slot*; word 0 = *slot* + 2; copy the *length* input bytes into the buffer at *start* **before** they are copied
  to the output.
- **Recall** slot *i*: *start* = word at 4 + 2*i*; *length* = (word at 4 + 2*i* − 2) − *start*, 16-bit; copy those
  buffer bytes to the output.
- A slot that was never filled reads whatever the buffer held [Code]. **ClassicMac** starts the buffer zeroed, reports
  `resource.dcmp-undefined-slot` once, and stops with `resource.dcmp-overrun` if a string lies beyond the buffer
  (which it caps at 64 KiB, all a 16-bit offset can reach).

### 2.4 Extensions (`FE`)

`FE` is followed by a sub-opcode byte. Counts are taken as their **low 16 bits, unsigned** (a `dbf` loop) [Code:
`'dcmp'` 0, 1]. Values written as words are their low 16 bits.

| Sub-op | Operands (varints unless noted) | Output |
| --- | --- | --- |
| 0 | *seg*, *cnt*, then *cnt* deltas | an export table: *index* starts at 6; for each delta, *index* = (*index* + delta − 6) & `$FFFF`, then the words `3F3C` *seg* `A9F0` *index*. After exactly *cnt* entries, one more `3F3C` *seg* `A9F0` |
| 1 | *target*, *a5Δ*, *cnt*, *a5* | a jump table of (*cnt* & `$FFFF`) + 1 entries `6100` *target* `4EED` *a5*; before each entry after the first, *target* −= 8 and *a5* += *a5Δ*, or, when *a5Δ* & `$FFFF` = 0, *a5* = the next varint |
| 2 | *value*, *cnt* | (*cnt* & `$FFFF`) + 1 bytes of *value* (its low byte) |
| 3 | *value*, *cnt* | (*cnt* & `$FFFF`) + 1 words of *value* |
| 4 | *value*, *cnt*, then signed bytes | (*cnt* & `$FFFF`) + 1 words: *value*, then each next one plus a signed byte from the input |
| 5 | *value*, *cnt*, then varints | as 4, with varint deltas |
| 6 | *value*, *cnt*, then varints | (*cnt* & `$FFFF`) + 1 **longs**, with varint deltas (32-bit) |
| 7–255 | — | nothing: only the sub-opcode byte is consumed, and decoding continues |

### 2.5 Constant tables

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

---

## 3. `'dcmp'` 2

"GreggyBits": 16-bit words looked up in a 256-word table, optionally mixed with literal words; version-9 form
[Code: `'dcmp'` 2]. macresources (MIT) and resource_dasm implement it too.

1. **Custom table.** If param2 bit 0 is set, (param1 + 1) big-endian words follow at the start of the input and are
   copied into a 256-word **custom table kept inside the decompressor's code**, which is used instead of the default
   table. The table is never cleared: entries above param1 keep the words an earlier resource put there (zero after the
   decompressor is freshly loaded) [Code; emulated]. Indexes are **not checked** against param1.
2. **Words.** *words* = decompressed size >> 1.
   - **param2 bit 1 clear:** each input byte is a table index; write that table word. The loop is a do-while, so a
     declared size of 0 or 1 still writes **one word** [Code; emulated].
   - **param2 bit 1 set:** for each group of 8 words, one flag byte, most significant bit first: 1 = an index byte
     (write the table word), 0 = two literal bytes. The (*words* & 7) leftover words use the top bits of one more flag
     byte.
3. **Odd size.** If the decompressed size is odd, one final input byte is copied as it is, in both modes.

No other param2 bits are used.

**ClassicMac** models a freshly loaded `'dcmp'` 2: custom-table entries above param1 are zero, and using one gives
`resource.dcmp2-stale-table` (Info). resource_dasm's `'dcmp'` 2 has a bug (it compares a byte count with a word count
and decodes only half the output, then falls back to emulating Apple's code); it also ignores table persistence and
declared sizes 0 and 1.

The default table [Code: `'dcmp'` 2]:

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

---

## 4. `'dcmp'` 3

A bit-stream LZ77 codec in the version-9 form. **Every compressed resource in the Mac OS 9.0 System uses it** (34
resources) [Code: `'dcmp'` 3]. ClassicMac's implementation was first ported from resource_dasm's `System3.cc` (MIT; see
`THIRD-PARTY-NOTICES.md`) and then checked against the disassembly of the Mac OS 9.0 System's `'dcmp'` 3; Apple's code,
run in an emulator, gives the same output on all 34 System resources and on crafted inputs. Disk Copy's KenCode is the
same codec (see [kencode.md](../codecs/kencode.md)).

Only the header's decompressed size (+8) is read; version, ID, expansion bytes, param1 and param2 are ignored [Code].

### 4.1 Bits

Bits are read **most significant first**. A literal byte is the next 8 bits of the stream, **not byte-aligned**
[Code]. (Apple's reader for 9 or more bits prefetches up to three bytes; reading a byte at a time gives the same
values.) Past the end of the input the Mac reads whatever memory follows: nothing checks it [Code].

### 4.2 Commands

A flag, *literal allowed*, starts true. While *written* < decompressed size (unsigned) [Code: `'dcmp'` 3]:

1. Read a copy length *L* (§4.3, 0–2042).
2. If *L* = 0 and *literal allowed*: a **literal run**. Read *n* (§4.3, 1–63) and copy *n* 8-bit values from the
   stream. *literal allowed* = (*n* = 63): only a full 63-byte run may be followed by another.
3. Otherwise a **back-reference** of *L* + 2 bytes, **plus 1 if *literal allowed* was false** (so a back-reference
   straight after a short literal run is at least 3 bytes). *literal allowed* = true. Read an offset (§4.4) chosen by
   *written* before this command, and copy forward, a byte at a time, from *written* − offset: an offset shorter than
   the length repeats the pattern.

Offset 0 cannot be encoded. An offset greater than *written* is not checked: the Mac reads the bytes before the output
[Code]. No real resource does this; **ClassicMac** stops with `resource.dcmp-overrun`.

### 4.3 Length codes

**Copy length, 0–2042.** Count up to ten leading 1 bits (*k*); the 0 that ends them is consumed only when *k* < 10
[Code: `'dcmp'` 3]:

| *k* | Then | Length |
| --- | --- | --- |
| 0 | 1 bit *x* | *x* (0 or 1) |
| 1 | 1 bit: 0 | 2 |
| 1 | 1 bit: 1, then 1 bit *x* | 3 + *x* |
| 2 | 1 bit: 0, then 1 bit *x* | 5 + *x* |
| 2 | 1 bit: 1, then 2 bits *x* | 7 + *x* |
| 3 | 3 bits *x* | 11 + *x* |
| 4 | 3 bits *x* | 19 + *x* |
| 5–10 | *k* bits *x* | 2^*k* − 5 + *x* (27, 59, 123, 251, 507, 1019) |

**Literal length, 1–63** [Code: `'dcmp'` 3]:

| Code | Length |
| --- | --- |
| `0` | 1 |
| `100` | 2 |
| `101` | 3 |
| `110` + 2 bits *x* | 4 + *x* |
| `111` + 4 bits *s*, *s* < 8 | 8 + *s* |
| `111` + 4 bits *s*, 8 ≤ *s* < 12, + 2 bits *x* | 16 + 4(*s* − 8) + *x* |
| `111` + 4 bits *s*, *s* ≥ 12, + 3 bits *x* | 32 + 8(*s* − 12) + *x* |

### 4.4 Offset codes

The offset code depends on *written*, the bytes output before the command. Three forms [Code: `'dcmp'` 3]:

- `0` + *a* bits *x* → 1 + *x*
- `10` + (*a* + 2) bits *x* → 1 + 2^*a* + *x*
- `11` + *w* bits *x* → *base* + *x*, with *base* = 1 + 2^*a* + 2^(*a*+2) and the width *w* chosen by *written*

| *written* | *a* | `10`: base | `11`: base | `11`: width by *written* |
| --- | --- | --- | --- | --- |
| 0–`$A` | 0 | 2 | 6 | ≤ 7: 1, ≤ 9: 2, else 3 |
| `$B`–`$14` | 1 | 3 | `$B` | ≤ `$C`: 1, ≤ `$E`: 2, ≤ `$12`: 3, else 4 |
| `$15`–`$28` | 2 | 5 | `$15` | ≤ `$16`: 1, ≤ `$18`: 2, ≤ `$1C`: 3, ≤ `$24`: 4, else 5 |
| `$29`–`$50` | 3 | 9 | `$29` | ≤ `$2A`: 1, ≤ `$2C`: 2, ≤ `$30`: 3, ≤ `$38`: 4, ≤ `$48`: 5, else 6 |
| `$51`–`$A0` | 4 | `$11` | `$51` | ≤ `$52`: 1, ≤ `$54`: 2, ≤ `$58`: 3, ≤ `$60`: 4, ≤ `$70`: 5, ≤ `$90`: 6, else 7 |
| `$A1`–`$2A0` | 5 | `$21` | `$A1` | ≤ `$A2`: 1, ≤ `$A4`: 2, ≤ `$A8`: 3, ≤ `$B0`: 4, ≤ `$C0`: 5, ≤ `$E0`: 6, ≤ `$120`: 7, ≤ `$1A0`: 8, else 9 |
| `$2A1`–`$3E8` | 6 | `$41` | `$141` | ≤ `$340`: 9, else 10 |
| `$3E9`–`$A80` | 7 | `$81` | `$281` | ≤ `$480`: 9, **≤ `$66C`: 10**, else 11 |
| `$A81`–`$1500` | 8 | `$101` | `$501` | ≤ `$D00`: 11, else 12 |
| `$1501`–`$2A00` | 9 | `$201` | `$A01` | ≤ `$1A00`: 12, else 13 |
| `$2A01`–`$5400` | 10 | `$401` | `$1401` | ≤ `$3400`: 13, else 14 |
| `$5401`–`$A800` | 11 | `$801` | `$2801` | ≤ `$6800`: 14, else 15 |
| `$A801`–`$11170` | 12 | `$1001` | `$5001` | ≤ `$D000`: 15, else 16 |
| `$11171`–`$2A000` | 13 | `$2001` | `$A001` | ≤ `$12000`: 15, ≤ `$1A000`: 16, else 17 |
| above `$2A000` | 14 | `$4001` | `$14001` | ≤ `$34000`: 17, else 18 |

- The regular rule is: width *k* while *written* ≤ *base* − 1 + 2^*k*, for *k* = 1 … *a* + 3, else *a* + 4; the table
  shows the widths reachable in each range. Note the odd range limits `$3E8` and `$11170`.
- **`$66C`** replaces the regular `$680` for *a* = 7 and is live: with *written* from `$66D` to `$680` the third form
  reads **11 bits** [Code: `'dcmp'` 3; emulated].
- Two other irregular thresholds exist in the code but can never apply: `$200C` (7 bits) for *a* = 14, which runs only
  above `$2A000`; and `$288` for *a* = 7, which runs only from `$3E9` (Apple's code reads 3 bits there, as the regular
  rule gives; resource_dasm reads 4) [Code].
- The last width of each form is an unconditional else: there is no range check [Code].

### 4.5 The end and the overshoot

- There is **no end code**: decoding stops when *written* reaches the declared size, checked only between commands. The
  last command is completed, so output may run **up to 2044 bytes** past the declared size (a back-reference of
  2042 + 3 bytes starting one byte short of it), or 62 for a literal run [Code: `'dcmp'` 3; emulated]. It lands in the
  expansion bytes and, beyond them, in whatever memory follows the block; the Resource Manager then cuts the handle to
  the declared size (§1.3).
- A declared size of 0 writes nothing.
- All 34 System resources consume exactly their input, to the last partial byte.

**ClassicMac** allows the overshoot into its 2 KiB after the block (`resource.dcmp-wrote-past-block`, Info, when it
passes the expansion bytes) and reads zeros past the input (`resource.dcmp-read-past-input`, Warning). resource_dasm
throws in both cases, and on an offset past the output.

---

## 5. Application-supplied decompressors

Applications compressed their own resources with private `'dcmp'` resources (§1.5 says how the Mac finds them).
ClassicMac cannot run 68k code; an application of ClassicMac supplies a decompressor instead:

- implement `IResourceDecompressor`: an `Id` (the `'dcmp'` ID) and `int Decompress(DecompressionContext context)`;
- pass it to `new ResourceDecompression(extra)`. A decompressor with a built-in ID (0–3) **replaces** the built-in one,
  as a file's own `'dcmp'` would on the Mac; among several with one ID, the last wins.

The context models the Mac's call:

| Member | Meaning |
| --- | --- |
| `Header` | the parsed 18-byte header (§1.1), including param1/param2 or the version-8 fields |
| `Block` | the block (decompressed size + expansion bytes) followed by 2048 zero bytes |
| `BlockLength` | the length of the block proper |
| `SourceOffset` | where the compressed bytes start; they end at `BlockLength` |
| `Options` | the `ReadOptions`, including which Resource Manager is modelled |
| `Report(severity, code, message)` | adds a diagnostic without stopping |

Decompress in place, writing from offset 0, and return the number of bytes written. The result is cut or padded to the
declared size as in §1.3. Throw `InvalidDataException` for bad input; it, `IndexOutOfRangeException` and
`ArgumentException` become `resource.dcmp-failed` and the stored bytes are returned.

---

## 6. Diagnostics

Fork diagnostics are reported by `ResourceFork.Read` (with the fork offset of the problem, where there is one);
decompression diagnostics by `ResourceDecompression.GetData`, their message prefixed with the resource (`'TYPE' id`).
`fork.unreadable` is reported by `ClassicMac.Files` (`MacFileResources`) and the app when `ResourceFork.Read` throws.

| Code | Severity | Meaning | What the Mac does |
| --- | --- | --- | --- |
| `resource.not-compressed` | Info | marked compressed but without the signature; used as stored | loads as stored (§1.2) |
| `resource.extended-uncompressed` | Warning | (Mac OS 9 model) signature present, header bit 0 clear: the header kept, the last 12 bytes dropped | as described (§1.2); the 68k ROM model strips 12 bytes silently |
| `resource.dcmp-header` | Error | the extended header is shorter than 18 bytes, or a version-8 reserved word is not zero; stored bytes returned | `CantDecompress` for the reserved word; a short header is not traced |
| `resource.dcmp-version` | Info (Mac OS 9) / Error (68k ROM) | the header version is not 8 or 9 | Mac OS 9 decodes as version 9; the ROM fails with `CantDecompress` |
| `resource.dcmp-unknown` | Warning | no decompressor with the header's ID; kept compressed | Mac OS 9: the compressed bytes in a handle, no error; ROM: `CantDecompress` (§1.6) |
| `resource.dcmp-overridden` | Info | the fork carries its own `'dcmp'` of that ID, which the Mac would run; the built-in one was used | runs the file's `'dcmp'` (§1.5) |
| `resource.dcmp-form` | Error | the header version does not match the decompressor's entry form; kept compressed | jumps into the wrong entry point |
| `resource.dcmp-overrun` | Error | the compressed data do not fit the block, or decoding read or wrote outside the block and the 2 KiB after it, or referred to memory before the output; kept compressed | reads or overwrites neighbouring memory |
| `resource.dcmp-failed` | Error | an application-supplied decompressor threw; kept compressed | — |
| `resource.dcmp-read-past-input` | Warning | the decompressor read past its input; zeros were read | reads whatever memory follows |
| `resource.dcmp-wrote-past-block` | Info | the last command overshot the block, as `'dcmp'` 3 may; the result is cut to size | writes into the memory after the block |
| `resource.dcmp-size` | Warning | fewer bytes written than declared; the rest is the block's leftover contents | the same: SetHandleSize to the declared size (§1.3) |
| `resource.dcmp-undefined-slot` | Warning | (`'dcmp'` 0/1) a memo slot was recalled before it was filled; zeros used (reported once) | copies whatever the working buffer held |
| `resource.dcmp2-stale-table` | Info | (`'dcmp'` 2) a custom-table entry above param1 was used; zero used (reported once) | uses a word left by an earlier resource |

---

## 7. Not covered

- `'dcmp'` 0–3 of System versions other than 9.0, and the native `'ncmp'` decompressors, which Mac OS 9.0 never uses
  (they differ from `'dcmp'` 0 and 2 only on bad input).
- Compressing resources: ClassicMac writes compressed resources only as they were read.
