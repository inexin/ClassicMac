# Control templates (CNTL)

A `'CNTL'` resource is the template of a control: a button, check box, radio button, scroll bar, pop-up menu or any
other control definition, with its rectangle, value range and title. Dialog items of type 7 name one by ID
([dialog-items.md](dialog-items.md)), and a `'cctb'` of the same ID gives the control's colours. ClassicMac reads both
to JSON, writes `'CNTL'` back from its fields, and draws controls in its dialog previews. The rules the interface
documents share are in [windows-dialogs.md](windows-dialogs.md).

| | |
| --- | --- |
| Identified by | Resource type `'CNTL'`; `'cctb'` by the `'CNTL'` ID |
| ClassicMac | Reads both; writes `'CNTL'`. `ClassicMac.Resources.Decoders.Interface` (`InterfaceResources.ReadControl`, `InterfaceWriter.WriteControl`, the `ui.control` and `ui.colors` decoders) |
| Verified against | ClassicMac's corpus of application resources (the write round trip); Mac OS 9.0 drawing controls in dialogs ([windows-dialogs.md §7](windows-dialogs.md#7-verification)) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials*, Control Manager; Mac OS 9.0's ControlsLib and ControlDefinitions and the stub `'CDEF'` resources (disassembly) |

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

[Code] is Mac OS 9.0's ControlsLib and ControlDefinitions.

### 1.1 Control templates (CNTL)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | Rectangle | `Rect`, in the window's local coordinates |
| +$08 | 2 | Value | `i16`, the initial value |
| +$0A | 1 | Visible | Non-zero: shown when made |
| +$0B | 1 | Filler | |
| +$0C | 2 | Maximum | `i16` |
| +$0E | 2 | Minimum | `i16` |
| +$10 | 2 | Definition ID | `i16`: the `'CDEF'` resource × 16 + a 4-bit variation (`'CDEF'` 0 when missing) |
| +$12 | 4 | Reference value | `i32` |
| +$16 | 1 + n | Title | Pascal string |

[Doc: Inside Macintosh: Macintosh Toolbox Essentials]

The classic definition IDs [Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| ID | Name | Variations |
| --- | --- | --- |
| 0 | `pushButProc` | +8 `useWFont`: the window's font |
| 1 | `checkBoxProc` | +8 `useWFont` |
| 2 | `radioButProc` | +8 `useWFont` |
| 16 | `scrollBarProc` | |
| 1008 | `popupMenuProc` | +1 `popupFixedWidth`, +4 `popupUseAddResMenu`, +8 `popupUseWFont` |

### 1.2 Control colour tables (cctb)

By the `'CNTL'` ID [Code]. The layout is that of the window colour tables
([windows-dialogs.md §1.6](windows-dialogs.md#16-colour-tables-wctb-dctb-actb)); the part codes are a control's
[Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| Part | Colour |
| --- | --- |
| 0 | Frame |
| 1 | Body |
| 2 | Text |
| 3 | Thumb |
| 4 | Fill pattern |
| 5–6 | Arrows light, dark |
| 7–8 | Thumb light, dark |
| 9–10 | Hilite light, dark |
| 11–12 | Title bar light, dark |
| 13–14 | Tinge light, dark |

### 1.3 Pop-up menus

A pop-up menu control reads the template's fields otherwise [Code]:

| Field | Meaning |
| --- | --- |
| Minimum | The `'MENU'` ID |
| Maximum | The title's width in pixels |
| Value | Low byte: the title's justification (0 left, 1 centre, −1 right); bits 8–14: its style (face bits × 256); bit 15: no style |
| Reference value | With `popupUseAddResMenu`, the resource type whose names are added to the menu |

## 2. Reading

1. Read the fields in order (§1.1).
2. Find the `'CDEF'` by the definition ID ÷ 16, the variation being the low 4 bits; Mac OS 9 maps the classic IDs
   (§4) [Doc: Inside Macintosh: Macintosh Toolbox Essentials].
3. Find the `'cctb'` of the same ID [Code].
4. A pop-up menu reads its fields as §1.3 says; once made, the control sets its minimum to 1, its maximum to the
   number of items and its value to 1 [Code].

How Mac OS 9.0 draws controls in a dialog is in
[windows-dialogs.md §2.4](windows-dialogs.md#24-how-mac-os-90-draws-dialogs-and-alerts).

## 3. Writing

A `'CNTL'` is written by the shared rules of [windows-dialogs.md §3](windows-dialogs.md#3-writing): the fields of
§1.1 in order, the filler byte 0.

## 4. Variants

- Mac OS 9 draws `pushButProc`, `checkBoxProc` and `radioButProc` with Appearance's IDs 368–370 (§2) [Code].

## 5. ClassicMac

- Short data is read as far as it goes, with zeros for the missing fields, and reported (`ui.short`). A `'cctb'` is
  read as the window colour tables are ([windows-dialogs.md §5.1](windows-dialogs.md#51-reading)). [ClassicMac]
- JSON, by the shared rules of [windows-dialogs.md §5.2](windows-dialogs.md#52-json-output), where the `'cctb'` row
  (`ui.colors`, with the part names of §1.2) also is [ClassicMac]:

| Type | Decoder | Fields |
| --- | --- | --- |
| `CNTL` | `ui.control` | `title`, `bounds`, `definition`, `definitionName` (for the IDs of §1.1, with their variations joined by `+`), `value`, `minimum`, `maximum`, `visible`, `refCon`; a pop-up menu (1008–1023) adds `popup`: `menu`, `titleWidth`, `titleJustification`, `titleNoStyle`, `titleStyle` (names of the face bits), `addResMenu` (the type, with variation 4) |

- `InterfaceWriter.WriteControl` writes by §3; it refuses a title Mac OS Roman cannot hold or over 255 bytes
  (`ArgumentException`). [ClassicMac]
- Which controls the previews draw, and how, is in
  [windows-dialogs.md §5.3](windows-dialogs.md#53-dialog-and-alert-previews). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ui.short` | Warning | A `'CNTL'` ends before its fields or title; a `'cctb'` under 8 bytes or with fewer entries than its count | Reads as far as the data goes, zeros for the missing fields | Reads past the end of the resource [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs` with `GoldenTests` (outputs in `Golden/`): `CNTL` 128
  (a scroll bar), `CNTL` 129 (a pop-up menu: menu 128, title width 60, a centred bold title, AddResMenu of `'FONT'`)
  and `cctb` 128.
- `tests/ClassicMac.Resources.Decoders.Tests/InterfaceWriterTests.cs` (`Templates_round_trip`): a scroll bar
  `'CNTL'` written and read back.
- `tests/ClassicMac.Resources.Decoders.Tests/DialogRendererTests.cs`: a scroll bar and a check box control drawn; see
  [windows-dialogs.md §7](windows-dialogs.md#7-verification).
- Read back and written again, every `'CNTL'` of ClassicMac's corpus comes out byte for byte [Verified: ClassicMac's
  corpus, not in the repository].

## 8. Not covered

- Control definitions other than those of §1.1 and their Appearance equivalents.
- Writing `'cctb'`, and writing either type from JSON (with `pack`).

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Control Manager: the control template, the
   definition IDs and the control colour table.
2. Mac OS 9.0's System file, disassembly: ControlsLib, ControlDefinitions and the stub `'CDEF'` resources
   ([README.md](../README.md#reference-builds)).
