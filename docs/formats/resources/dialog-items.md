# Dialog item lists (DITL)

A `'DITL'` resource is the item list of a dialog or an alert: buttons, check boxes, radio buttons, controls, static and
editable text, icons, pictures, user items and help items, each with its rectangle. A `'DLOG'` or `'ALRT'` names its
list by ID ([windows-dialogs.md](windows-dialogs.md#1-layout)), and an `'ictb'` of the list's ID gives its items'
colours and fonts. ClassicMac reads both to JSON, writes `'DITL'` back from its items, and draws item lists in its
dialog previews. The rules the interface documents share are in [windows-dialogs.md](windows-dialogs.md).

| | |
| --- | --- |
| Identified by | Resource type `'DITL'`; `'ictb'` by the `'DITL'` ID |
| ClassicMac | Reads both; writes `'DITL'`. `ClassicMac.Resources.Decoders.Interface` (`InterfaceResources.ReadDialogItems`, `InterfaceWriter.WriteDialogItems`, the `ui.dialog-items` and `ui.item-colors` decoders) |
| Verified against | ClassicMac's corpus of application resources (the write round trip); Mac OS 9.0 drawing item lists ([windows-dialogs.md §7](windows-dialogs.md#7-verification)) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials*, Dialog Manager; Mac OS 9.0's MacDialogsLib and the 68k Dialog Manager (disassembly) |

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

[Code] is Mac OS 9.0's MacDialogsLib unless it names the 68k code.

### 1.1 The item list

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Count less one | `i16` |
| +$02 | … | Items | Below, one after another |

Each item [Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Placeholder | Should be 0; Mac OS 9 keeps a user item's non-zero value and calls it as a drawing procedure [Code] |
| +$04 | 8 | Rectangle | `Rect`, in the dialog's local coordinates |
| +$0C | 1 | Type | `u8`; bit 7 ($80, `itemDisable`) disables the item, the only flag |
| +$0D | 1 | Data length | `u8`: n |
| +$0E | n | Data | By type, below |
| +$0E + n | 0–1 | Pad | One byte when n is odd, for every type [Code] |

### 1.2 Item types

The type less bit 7 [Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| Type | Name | Data |
| --- | --- | --- |
| 0 | User item | None; the application draws it |
| 1 | Help item | `i16` kind, `i16` resource ID (kind 1: an `'hdlg'`; 2: an `'hrct'`; 8: an `'hdlg'` appended, with an `i16` offset that the Dialog Manager overwrites) [Code] |
| 4 | Button | Its title |
| 5 | Check box | Its title |
| 6 | Radio button | Its title |
| 7 | Control | A `'CNTL'` ID ([controls.md](controls.md)) |
| 8 | Static text | The text |
| 16 | Editable text | The initial text |
| 32 | Icon | An icon ID: `'cicn'` first in a colour dialog, else `'ICON'`; Mac OS 9 draws IDs 0, 1 and 2 as the system's stop, note and caution icons [Code] |
| 64 | Picture | A `'PICT'` ID |

A text item's data is the text itself, with no length byte of its own; a line break is a CR.

### 1.3 Item colours and fonts (ictb)

By the `'DITL'` ID [Code]: one 4-byte entry per item, in item order, then the tables the entries point to.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Data | `i16`; meaning by the item's type, below |
| +$02 | 2 | Offset | `i16`, from the start of the `'ictb'` |

Data and offset 0 keep the item's defaults.

- A button, check box, radio button or control: the data is the length of a control colour table at the offset, laid
  out as a `'cctb'` ([controls.md §1.2](controls.md#12-control-colour-tables-cctb)).
- Static or editable text: the data is a set of flags for a 20-byte text style at the offset.

The text style:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Font | `i16` family ID; with flag bit 15, an offset from the start of the `'ictb'` to the font's name (a Pascal string) |
| +$02 | 2 | Face | The face in the high byte |
| +$04 | 2 | Size | `i16` |
| +$06 | 6 | Foreground | `RGBColor` |
| +$0C | 6 | Background | `RGBColor` |
| +$12 | 2 | Mode | `i16`, the transfer mode |

The flags: bit 0 font, bit 1 face, bit 2 size, bit 3 foreground, bit 4 the size is added to the dialog's, bit 13
background, bit 14 mode, bit 15 the font word is an offset to the font's name [Code].

## 2. Reading

1. Read the count less one as a signed number: −1 ($FFFF), or any negative count, is an empty list [Code].
2. For each item, read the fixed part, the data and, after odd-length data, the pad byte.
3. A type Mac OS 9 does not list in §1.2 draws nothing but still takes space and clicks [Code].
4. A control's, icon's or picture's ID is the `i16` at the start of the data, whatever the length says [Code].
5. In static text, `^0`–`^3` are replaced by `ParamText`'s strings when drawn, on a copy; a missing string deletes the
   citation [Code].

The count is never checked against the resource's size: a count past the data makes the Dialog Manager read past the
end [Code].

How Mac OS 9.0 draws each item is in [windows-dialogs.md §2.4](windows-dialogs.md#24-how-mac-os-90-draws-dialogs-and-alerts).

## 3. Writing

A `'DITL'` is written by the shared rules of [windows-dialogs.md §3](windows-dialogs.md#3-writing), and:

1. The count less one, then each item.
2. Each item's placeholder long is 0.
3. A text item's data is its text; a control's, icon's or picture's data is its 2-byte ID.
4. The pad byte after odd-length data is 0.

## 4. Variants

- The 68k Dialog Manager tested the type's bits in the order control, editable text, static text, icon, picture, so a
  combined value took the first that matched; Mac OS 9 accepts exactly the values of §1.2 [Code: 68k] [Code].

## 5. ClassicMac

- The reading stops at the end of the data: a count past it gives the items read whole, and `ui.short`. An item whose
  data runs past the end is dropped. [ClassicMac]
- A control's, icon's or picture's ID is read only when the data length is at least 2; a help item's resource ID only
  when it is at least 4. [ClassicMac]
- An `'ictb'` is read with the `'DITL'` of the same ID from the fork, which gives each entry's item type. Without it,
  the entries are listed (as many as whole 4-byte entries fit) without their colours or styles, as the item types are
  unknown. A control item's colour table is read when its data is above 0 and its offset inside the resource; a text
  item's style when its flags are not 0 and its 20 bytes are inside the resource. [ClassicMac]
- JSON, by the shared rules of [windows-dialogs.md §5.2](windows-dialogs.md#52-json-output) [ClassicMac]:

| Type | Decoder | Fields |
| --- | --- | --- |
| `DITL` | `ui.dialog-items` | `items[]`: `number` (from 1), `type` (`user`, `help`, `button`, `checkBox`, `radioButton`, `control`, `staticText`, `editText`, `icon`, `picture`, `unknown`), `typeCode`, `enabled`, `bounds`, then `text`, `resourceId`, a help item's `helpKind`, `helpKindName` (`hdlg`, `hrct`, `appendHdlg`), `resourceId` and `offset`, or `data` (hex) |
| `ictb` | `ui.item-colors` | `itemList` (whether the `'DITL'` was found), `items[]`: `number`, `data`, `offset`, and `colors` (a control's table, as `ui.colors`) or `textStyle` (`flags`, `font` or `fontName`, `face`, `size`, `addSize`, `foreground`, `background`, `mode`, as the flags give) |

- `InterfaceWriter.WriteDialogItems` writes by §3: the data of buttons, check boxes, radio buttons, static and
  editable text from their text; of controls, icons and pictures from their resource ID; help and user items keep
  their data as stored. It refuses an empty list and item data over 255 bytes (`ArgumentException`). [ClassicMac]
- The previews draw item lists as [windows-dialogs.md §5.3](windows-dialogs.md#53-dialog-and-alert-previews) says; a
  lone `'DITL'` is drawn in a plain box. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ui.short` | Warning | A `'DITL'` holds fewer items than its count says, or ends inside an item; an `'ictb'` ends inside an entry or a control colour table | Keeps the items read whole; the JSON holds what was read | Reads past the end of the resource [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs` with `GoldenTests` (outputs in `Golden/`): `DITL` 128
  (one item of every type, a disabled static text with `^0` and a disabled icon, a help item of kind 8), `DITL` 129
  (says 2 items, holds 1: `ui.short`), and `ictb` 128 for `DITL` 128 (the button's colour table; the static text's
  font by name, size and colour).
- `tests/ClassicMac.Resources.Decoders.Tests/InterfaceWriterTests.cs` (`Templates_round_trip`): a button, a disabled
  icon and a user item with data written and read back.
- `tests/ClassicMac.Resources.Decoders.Tests/DialogRendererTests.cs`: items drawn; see
  [windows-dialogs.md §7](windows-dialogs.md#7-verification).
- Read back and written again, every `'DITL'` of ClassicMac's corpus differs only in its filler and pad bytes
  [Verified: ClassicMac's corpus, not in the repository].

## 8. Not covered

- `'hdlg'` and `'hrct'`, the Help Manager's resources a help item names.
- `'ictb'` colours and fonts in the previews.
- Writing `'ictb'`, and writing either type from JSON (with `pack`).

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Dialog Manager: the item list resource.
2. Mac OS 9.0's System file, disassembly: MacDialogsLib ([README.md](../README.md#reference-builds)).
3. The 68k ROM (`$077D`), disassembly: the Dialog Manager.
