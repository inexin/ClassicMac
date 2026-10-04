# Icon suites and families

How the Icon Utilities draw an icon suite (the 1-, 4- and 8-bit icons of one ID, [icons.md](icons.md)), and the icon
family Mac OS 9's Icon Services knows: 20 members up to 128 × 128, with 32-bit colour and 8-bit masks, held in an
`'icns'` resource or made from the classic icon resources. The Finder and every application draw icons this way.
ClassicMac draws suites into a `QuickDrawPort` by the rules of Mac OS 9.0 or the 68k ROM, reads `'icns'` families,
exports each of their images, and writes the classic members of a family from an image.

| | |
| --- | --- |
| Identified by | Resource type `'icns'` (`'icns'` and a length at +$00); a suite is the `ICN#`, `icl4`, `icl8`, `ics#`, `ics4`, `ics8`, `icm#`, `icm4`, `icm8` of one ID |
| ClassicMac | Reads (families), draws (suites) and writes (the classic members); `ClassicMac.Resources.Decoders.Images.IconSuite`, `IconFamily`, `ImageImport`, the `image.icon-family` decoder |
| Verified against | Mac OS 9.0's Icon Utilities in SheepShaver: 628 drawing cases, pixel for pixel (§7)<br>Real icon families' 8-bit masks |
| Sources | *Inside Macintosh: More Macintosh Toolbox*, Icon Utilities; Mac OS 9.0's IconUtils and IconServicesLib, the 68k ROM's Icon Utilities and Mac OS 9.2.2's IconUtils, traced in disassembly |

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

### 1.1 Members

Mac OS 9's Icon Services knows these 20 members and nothing else [Code] (raw sizes in bytes):

| Size | 1-bit (image + mask) | 4-bit | 8-bit | 32-bit | 8-bit mask |
| --- | --- | --- | --- | --- | --- |
| 16 × 12 | `icm#` (48) | `icm4` (96) | `icm8` (192) | — | — |
| 16 × 16 | `ics#` (64) | `ics4` (128) | `ics8` (256) | `is32` (1024) | `s8mk` (256) |
| 32 × 32 | `ICN#` (256) | `icl4` (512) | `icl8` (1024) | `il32` (4096) | `l8mk` (1024) |
| 48 × 48 | `ich#` (576) | `ich4` (1152) | `ich8` (2304) | `ih32` (9216) | `h8mk` (2304) |
| 128 × 128 | — | — | — | `it32` (65536) | `t8mk` (16384) |

- Rows run top down without padding: 1-bit members are the image then the mask, `w / 8` bytes a row; 4-bit pixels
  high nibble first; 4- and 8-bit colours from the system colour tables 4 and 8 ([icons.md §1.2](icons.md#12-4--and-8-bit-icons)) [Code].
- 8-bit masks: one byte a pixel, the alpha: 0 transparent, $FF opaque. Most real ones have partial alpha [Code]
  [Verified].
- 32-bit members: ARGB, A first; the alpha is not used [Code]. Compressed data is §1.4.
- The table's order (`icm#` … `t8mk`, row by row) is Icon Services' member order.

### 1.2 icns header

An IconFamilyResource [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | type | `'icns'`, or a variant: `'tile'`, `'over'`, `'drop'`, `'open'`, `'odrp'` |
| +$04 | 4 | length | `u32`, including the header |
| +$08 | | elements | §1.3, packed without alignment, in any order |

### 1.3 icns element

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | type | A member (§1.1), a variant type (a nested family, §1.2), or anything else (`'TOC '`, `'info'`, `'icnV'`, `'name'`, `'ic07'`, …) |
| +$04 | 4 | size | `u32`, including this 8-byte header |
| +$08 | size − 8 | data | |

[Code]

### 1.4 Compressed 32-bit data

A 32-bit member whose payload is not exactly its raw size is compressed [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | compression format | `it32` only: must be 0 |
| follows | | planes | Red, green and blue, each one byte per pixel, run-length coded |

Each plane is a sequence of a control byte and data: a control byte under $80 copies that many plus one literal
bytes; $80 and over repeats the next byte (control − 125) times (3 to 130) [Code].

## 2. Reading

### 2.1 Drawing an icon suite

PlotIconID and PlotIconSuite, as Mac OS 9.0's native Icon Utilities do them [Code]; the 68k ROM's differences are
§4.1. On Mac OS 9 a suite may also hold the 48 × 48 and 32-bit members. PlotIconID never reads `ICON` or `SICN`.

1. **The mask group** comes from the rect's size before alignment; the first 1-bit member in the list is used, and
   the group sets the source size:
   - width or height ≥ 48: `ich#` `ICN#` `ics#` `icm#`; both under 32 and height > 12: `ics#` `ICN#` `icm#` `ich#`;
     both under 32: `icm#` `ics#` `ICN#` `ich#`; otherwise `ICN#` `ich#` `ics#` `icm#`.
   - No 1-bit member: noMaskFoundErr (−1000), nothing drawn. The mask is never taken from colour data.
   - The mask is the member's second half; a member without it gets CalcMask of its image
     ([icons.md §2.2](icons.md#22-masks)); a short `ich#` fails the call. An `icm#` longer than one icon and its mask
     is treated as SICNs: the rect is re-centred to 16 rows (`c = (top + bottom) >> 1`, top `c − 8`), the source is
     16 tall, and the mask is its second half (or CalcMask for one SICN).
   - An all-zero mask draws nothing (noErr).
2. **Alignment** never resizes: the member always fills the rect, however unevenly. With an alignment, the mask's
   bounding box is mapped into the rect (MapPt: `(|v − from| × toSize + fromSize / 2) / fromSize`, sign restored; no
   scaling when the sizes are equal), and the rect is moved so the box is centred (`((a + b) − (box a + box b)) >> 1`,
   a floor) or on the chosen edge. An off-centre mask moves even at 1:1.
3. **Colour or 1-bit:** colour data when (depth > 4 and not open) or (depth 4, not selected, and no transform or
   offline). The colour member comes only from the mask's group, by the port's depth: 16 or 32 bits `il32` `icl8`
   `icl4`; 8 bits `icl8` `il32` `icl4`; 4 bits `icl4`; 1–2 bits none (the small, mini and huge groups likewise with
   their own members). Otherwise, or with no colour member, the 1-bit path (step 5).
4. **Colour data** (PlotDeep): fore- and background forced black and white; the data drawn through the mask with
   CopyMask, both stretched to the rect. Label, selection and dimming are baked into a copy of the system colour
   table, every entry, in this order:
   - label, 8-bit table: `L = Brighten(label colour)`; entry 0 becomes `(c + L) >> 1`, the others `(c × L) >> 16`;
     4-bit table: entry 15 (black) becomes the raw label colour;
   - selected: Darken (halve every component; unless all three are then equal within $200, halve the smallest, then
     the smaller of the other two unless they are equal);
   - disabled: `(c + $FFFF) >> 1`, and no pattern.
   - 32-bit data is transformed on a copy: label `c × Brighten(L) >> 16` per channel, selected halves each channel,
     disabled `(c + 255) >> 1`.
   - Brighten: white for black; else each component scaled up so the largest is $FFFF, then
     `c × lum′ / $FFFF + ($FFFF − lum′)`, with `lum = (5r + 9g + 2b) >> 4` and `lum′ = (lum >> 1) + (lum >> 3) + $6000`.
   - Offline then adds 25% black dots inside the mask (step 6).
   - **8-bit masks** (a suite from an icon family; there is no `t8mk` in a suite) [Code] [Verified]:
     - used only when all hold: colour data is drawn (so never open, and never at depths 1–4, which take the 1-bit
       mask), the screen has 8 bits or more, the port is not recording a picture or printing, and the mask's bounds
       equal the data's.
     - The mask: width or height ≥ 48 → `h8mk` `l8mk` `s8mk`; both under 32 → `s8mk` `l8mk` `h8mk`; otherwise `l8mk`
       `h8mk` `s8mk`, from the caller's rect before alignment; the first present is taken, and if its size is not the
       data's the 1-bit mask is used, without trying the next 8-bit mask.
     - It replaces the 1-bit mask (an all-$00 `l8mk` draws nothing inside the `ICN#` mask; an all-$FF one draws the
       whole square, outside it too). Alignment still uses the 1-bit mask's box, and an all-zero 1-bit mask draws
       nothing, whatever the 8-bit mask holds (MakeBoundary finds no boundary).
     - IconUtils calls CopyMask (not CopyDeepMask) with the data and an 8-bit mask PixMap using clut 40 (a grey ramp).
       Both are stretched alike, nearest pixel (no filtering). Per 8-bit component, `out = m = $FF ? s : d + (((s − d)
       × m) >> 8)` (s − d signed, an arithmetic shift: a floor, not ÷ 255). A 16-bit screen truncates the result to 5
       bits a component; an 8-bit one takes the nearest table entry, undithered.
     - The label, selected and disabled recolouring above applies to the source first; offline's dots follow as usual.
5. **1-bit data** (PlotShallow):
   - At depth 2 and more: the foreground is the raw label colour (depth > 2 and a label) or the port's; the
     background is `$CC2A $CC2A $FF2A` for open, else the port's; disabled, when depth > 4 or there is no label, takes
     GetGray's colour as the foreground and drops the transform if GetGray succeeds; selected Darkens the foreground
     (depth > 4) and always the background (so white becomes $7FFF grey).
   - At depth 1: selected swaps the port's colours; no label, grey or Darken.
   - With no transform: the image through the mask with CopyMask (image 1 = foreground, 0 = background, inside the
     mask).
6. **Transforms (offline, open, disabled on 1-bit):**
   - **Bitmap path** (only a rect of exactly 32 × 32), patterns aligned to the icon: T = the image (the outline for
     open: the mask less every pixel whose four neighbours are set); disabled: even rows AND $AAAAAAAA, odd rows AND
     $55555555; offline and open: even rows OR `mask & $88888888`, odd rows OR `mask & $22222222`. T goes through the
     mask with CopyMask, or, when the mask was stretched, T is copied srcCopy through the mask's region. For colour
     data, T (the dots alone) is ORed on in black.
   - **Region path** (otherwise), patterns aligned to the port: the mask's region (BitMapToRegion, MapRgn'd to the
     rect); disabled: the image drawn, then PaintRgn with gray in patBic; offline: the image (1-bit) drawn, then
     PaintRgn with ltGray in patOr; open: FillRgn black, InsetRgn(1, 1), FillRgn ltGray.
   - **The prescaled mask:** with any transform, a rect of exactly 32 × 32 and a smaller 1-bit member, the mask is
     first copied (doubled) into a 32 × 32 1-bit buffer with CopyBits in the caller's colours, realised on a 1-bit
     device: a set pixel becomes the foreground's index and a clear one the background's (the nearer of black, 1, and
     white, 0, at 4 bits a component); when the two collide and the colours differ, the foreground takes its inverse's
     index. Black on white copies the mask unchanged; red on blue, for example, inverts it. Every later step (the
     rendering, even when GetGray then drops a disabled transform, and the bitmap transform) goes through that
     buffer's region with CopyBits srcCopy, never CopyMask [Code] [Verified].
7. **Labels:** 0 none to 7, from the transform's bits 8–11, or PlotIconSuite's suite label when the transform has none;
   8–15 make GetLabel fail. The colours are the System's `'rgb '` −16392 + n (Mac OS 9's defaults: 1 $5600 $2C9D $0524
   Project 2, 2 $0000 $64AF $11B0 Project 1, 3 $0000 $0000 $D400 Personal, 4 $0241 $AB54 $EAFF Cool, 5 $F2D7 $0856
   $84EC In Progress, 6 $DD6B $08C2 $06A2 Hot, 7 $FFFF $648A $028C Essential); the Finder's Labels settings change them.
8. **Other routines:** PlotIconHandle draws an `ICON` or `ICN#` as the 32 × 32 member (a 128-byte `ICON` gets
   CalcMask); PlotSICNHandle a `SICN` list as the small or mini member (a list of two or more: the second is the mask);
   PlotIcon copies an `ICON` unmasked with CopyBits srcCopy, stretched, in the port's colours. IconIDToRgn gives the
   region of the mask steps 1 and 2 select and place.

### 2.2 Reading an icns

IconFamilyResource, as Mac OS 9's Icon Services reads it [Code]:

1. The length at +$04 must be at least 9 and equal the resource's size exactly; otherwise the family is empty
   (noIconDataAvailableErr, which callers tolerate).
2. Walk the elements from +$08. An element size under 1 fails the whole family (paramErr); an element running past
   the end is skipped.
3. A known member is stored, the last of a type winning; a variant type nests a family; anything else is ignored.
4. A 32-bit member of exactly its raw size is raw; any other size is compressed (§1.4):
   - `it32` alone starts with the 4-byte compression-format word; any value but 0 fails the whole family (paramErr);
   - the planes fill bytes 1, 2 and 3 of each pixel; alpha stays 0;
   - a count stops at the end of its plane (the excess literal bytes are skipped, a run is cut short); nothing
     carries into the next plane, which begins only when the current one is full; bytes after the blue plane are
     ignored, and short data leaves zeros.
5. Every other member (1-, 4-, 8-bit and the 8-bit masks) must be exactly its raw size; otherwise it is dropped
   without an error. So a mask-less `ICN#` counts here as absent, unlike the Icon Utilities' CalcMask.

### 2.3 Which resources make a family

An `'icns'` of the ID is used alone. Without one, the classic resources of the ID (`icm#`/`4`/`8`, `ics#`/`4`/`8`,
`ICN#`, `icl4`, `icl8`) are read, each only at its exact size. 32-bit members, 8-bit masks and the 48 × 48 members come
only from an `'icns'` [Code].

### 2.4 Drawing a family

PlotIconRefFast [Code]:

1. The size group comes from the destination rect's height: ≤ 12 mini, ≤ 20 small, ≤ 40 large, ≤ 56 huge, ≥ 57
   thumbnail. The first mask present in the group's list wins:
   - mini: `icm#` `ics#` `ICN#` `ich#` (never an 8-bit mask);
   - small: `s8mk` `l8mk` `h8mk` `ics#` `ICN#` `icm#` `ich#`;
   - large: `l8mk` `s8mk` `h8mk` `ICN#` `ics#` `icm#` `ich#`;
   - huge: `h8mk` `l8mk` `s8mk` `ich#` `ICN#` `ics#` `icm#`;
   - thumbnail: `t8mk` `h8mk` `l8mk` `s8mk` `ich#` `ICN#` `ics#` `icm#`.

   So an 8-bit mask wins over any 1-bit mask, even one of another size, and the two are never combined. The 1-bit
   and 8-bit masks of real families differ by a few pixels; neither is derived from the other [Verified].
2. Data by screen depth (large group shown; the other groups start with their own size): 32 bits `il32` `icl8` `icl4`
   `is32` `ics8` `ics4` `ih32` `ich8` `ich4` `icm8` `icm4` `ICN#` …; 8 or 16 bits `icl8` `icl4` `ics8` `ics4` `ich8`
   `ich4` `icm8` `icm4` `ICN#` … (no 32-bit data, so a 16-bit screen gets `icl8`); 4 bits `icl4` `ICN#` …; 1 bit
   `ICN#` `ics#` `icm#` `ich#`. The thumbnail list, at any depth: `it32` `ih32` `ich8` `il32` `icl8` `is32` `ics8`
   `icm8` `ich#` `ICN#` `ics#` `icm#`.
3. A mask the same size as the data: CopyDeepMask with srcCopy (an 8-bit mask blends as alpha). Otherwise the mask
   becomes a region (8-bit: a pixel is in when its byte is not 0), MapRgn'd to the rect, and CopyBits srcCopy draws
   through it (a hard edge).
4. A family without any mask falls back to the Icon Utilities (§2.1).

## 3. Writing

- **SetIconFamilyData** stores a 32-bit member of exactly its raw size as it is and decodes any other size as
  compressed (§1.4); the family keeps 32-bit members uncompressed [Code: Mac OS 9.0 Icon Services].
- **MakeIconFamilyHandle** writes the 8-byte header (`'icns'`, the total length), then each member present, in table
  order (`icm#` … `t8mk`), as its type, its length with the 8-byte element header, and its data; nothing is padded
  and variants are not written [Code: Mac OS 9.0 Icon Services].
- **AppendCompressedData** compresses every 32-bit member; none is stored raw [Code: Mac OS 9.0 Icon Services]:
  1. `it32` starts with the compression-format word 0.
  2. The red, green and blue planes follow in that order, each compressed on its own; alpha is dropped.
  3. At each byte, count it and the equal bytes after it, up to 130 (RepeatingPixel). Three or more: write any
     pending literals, then the control byte count + $7D ($80–$FF) and the byte. Fewer: the byte joins the pending
     literals, written as the control byte n − 1 and the n bytes when they reach 128 and at the plane's end.

  So an `il32` of 1,024 red pixels is `'icns'` $40, `'il32'` $38, and each plane `FF v` seven times then `EF v`.
- **What ClassicMac writes** [ClassicMac]: an icon family as the classic resources `ICN#`, `icl4`, `icl8`, `ics#`,
  `ics4` and `ics8` of one ID, the small ones from the image scaled to 16 × 16 (or from a second image given for them),
  each by [icons.md §3](icons.md#3-writing); `IconFamily.ToIcns` writes an `'icns'` as MakeIconFamilyHandle does.

## 4. Variants

### 4.1 The 68k ROM

The ROM's Icon Utilities differ from §2.1 [Code: 68k ROM]:

- Step 1, the mask group: width or height ≥ 32 → `ICN#` `ics#` `icm#`; height > 12 → `ics#` `icm#` `ICN#`; else `icm#`
  `ics#` `ICN#`. There is no `ich#`.
- Step 3, the colour member: `icl8` at 8 bits and more, else `icl4` at 4 bits and more; no 32-bit members.
- Step 4: the label and selection change only the colour-table entries its `indl` resources −16392 (8-bit) and −16391
  (4-bit) list; dimming changes every entry. There are no 8-bit masks.
- Step 6: the bitmap path is taken for any rect up to 32 × 32, the mask first stretched to the rect when the sizes
  differ; with a stretched mask the ROM paints the mask srcBic then T srcOr. Its pattern loop never ends for an odd
  mask height (a crash). There is no prescaled mask.

### 4.2 Mac OS 9.0 on a 1-bit screen

A selected icon, or any drawn with colours other than black on white, whose member is scaled comes out as a solid
black mask, because of Mac OS 9.0's scaled CopyMask (a bug;
[quickdraw.md §4.1](../graphics/quickdraw.md#41-mac-os-9-bitmaps)); offline then adds white dots, and disabled changes nothing
[Verified]. The ROM draws it white on black at every size [Code: 68k ROM].

### 4.3 Mac OS 9.2.2

From the code only [Code: 9.2.2] ([README.md](../README.md#reference-builds)):

- a 16-bit screen takes the 32-bit members (`il32`, `is32`, `ih32`) instead of `icl8`;
- an `'icns'` element over $18FFF bytes fails the whole family;
- the open transform on a one-row mask draws an empty row;
- the 8-bit mask blend (§2.1 step 4) is the same.

### 4.4 Family variants

An `'icns'` may nest variant families (`'tile'`, `'over'`, `'drop'`, `'open'`, `'odrp'`), with the same layout (§1.2)
[Code].

## 5. ClassicMac

- `IconSuite.Plot` draws a suite (and `PlotIconHandle`, `PlotSICNHandle`, `PlotIcon`; `ToRegion` gives IconIDToRgn's
  region) into a `QuickDrawPort` by either QuickDraw's rules. The port's depth is the screen depth; a port is never a
  picture or printer, so CopyMask is always used. `IconSuite.FromFamily` takes every member but `it32` and `t8mk`
  (IconFamilyToIconSuite). [ClassicMac]
- Labels 8–15, where GetLabel fails and the colour is undefined, draw as no label. [ClassicMac]
- An 8-bit mask without a colour member (undefined on the Mac) is ignored: the 1-bit path. [ClassicMac]
- CopyMask's single stretch of data and mask is taken to sample both alike. [ClassicMac]
- `IconFamily.SetMember` sets a member as SetIconFamilyData does (§3) and refuses another size for a 1-, 4- or 8-bit
  member or mask; `IconFamily.ToIcns` writes the family as §3. [ClassicMac]
- `IconFamily.ReadIcns` reads as §2.2 and `IconFamily.FromResources` as §2.3. Data under 8 bytes or of another type
  gives an empty family (`icon.family-header`); a wrong length, an empty one (`icon.family-length`); ignored element
  types and dropped members are reported (`icon.family-ignored`, `icon.member-size`); the failures of §2.2 are
  `image.undecodable`. Variants are read but not exported. [ClassicMac]
- The `icns` export writes each image member through the mask its own size selects (§2.4 step 1), the 8-bit mask of
  the same size as the image's alpha, any other as a hard edge mapped as MapRgn maps it; names and order are
  [export-manifest.md §3.10](../output/export-manifest.md#310-icons-and-their-masks). A family with no mask is exported
  opaque (`image.no-mask`). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `icon.family-header` | Warning | The data is under 8 bytes, or its type is not `'icns'` or a variant | Reads an empty family | Not traced |
| `icon.family-ignored` | Info | Elements of types Icon Services does not know | Skips them | Ignores them [Code] |
| `icon.family-length` | Warning | The header's length is under 9 or not the data's size | Reads an empty family | An empty family (noIconDataAvailableErr) [Code] |
| `icon.member-size` | Info | A 1-, 4- or 8-bit member or 8-bit mask is not its raw size | Drops it | Drops it [Code] |
| `image.no-mask` | Info | The family has no mask | Exports its images opaque | Falls back to the Icon Utilities, which draw nothing without a 1-bit member [Code] |
| `image.undecodable` | Warning | An element size under 1, or an `it32` compression format other than 0 | Exports the resource raw | The whole family fails (paramErr) [Code] |

## 7. Verification

- **Checked against Mac OS 9.0** in SheepShaver ([README.md](../README.md#reference-builds)) [Verified]: all 628
  cases match pixel for pixel: every PlotIconID/PlotIconSuite case (alignments, rect sizes 16–52, every transform,
  labels, member choice, missing masks, colour pairs, depths 1/4/8/32, a moved origin), IconIDToRgn, PlotIconHandle,
  PlotSICNHandle and PlotIcon. The ROM rules (§4.1) are from the code only.
- `tests/ClassicMac.Resources.Decoders.Tests/IconSuiteTests.cs`: the 1-bit path, selected and disabled colours,
  colour members with a label baked in, no 1-bit member, alignment by the mask's box, offline dots on both paths, the
  8-bit mask's blend, its conditions, its choice by rect size and fallback, 16- and 8-bit screens, transforms before
  the blend, nearest-pixel stretching, Darken and Brighten.
- `tests/ClassicMac.Resources.Decoders.Tests/IconFamilyTests.cs`: families read as Mac OS 9 reads them, runs and
  literals stopping at a plane's end, bad families empty or failing, a family made from the classic resources.
  Writing: the 1,024-pixel `il32` example of §3 byte for byte (the encoder re-implemented from the code, not a live
  dump), runs from three and literals up to 128, table order and `it32`'s format word, a family read back.
- `tests/ClassicMac.Resources.Decoders.Tests/ImportTests.cs`, `Standard_table_icons_use_the_nearest_colour`: the six
  family members written, the small ones at 16 × 16.
- Golden fixture `icns` 128 (`ICN#`, a raw `il32`, a compressed `is32`, an `s8mk`; `GoldenFixtures`, hashes in
  `tests/ClassicMac.Resources.Decoders.Tests/Golden/golden.json`).

## 8. Not covered

- Several screens (DeviceLoop), a grey-scale device's label rule, and the ROM's endless pattern loop are not
  reproduced by `IconSuite`.
- The export decoders take one resource at a time and do not draw suites; `IconSuite.Plot` does.
- `'icns'` variants are read but not exported or written; standalone 32-bit, 48 × 48 and 8-bit mask resources are not
  decoded (Icon Services never reads them outside an `'icns'`).
- Importing an image as an `'icns'`: the import makes the classic resources (§3).
- A Mac OS 9.2.2 mode (§4.3).

## 9. References

1. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), Icon Utilities: icon suites, alignment, transforms and
   labels.
