# Interface resources — an implementer's specification

This document describes the classic Mac OS Toolbox's interface resources: menus (`'MENU'`, `'MBAR'`), window, dialog
and alert templates (`'WIND'`, `'DLOG'`, `'ALRT'`), dialog item lists (`'DITL'`) and control templates (`'CNTL'`). It
describes them completely enough to write a reader without reading ClassicMac's code, and it specifies the JSON
ClassicMac writes for them.

References:

- *Inside Macintosh: Macintosh Toolbox Essentials* (1992): the Menu, Window, Dialog and Control Managers and their
  resources, and the menu mark constants.
- Apple's Rez templates for these types (`Types.r`, MPW): field order, alignment and the named constants.
- Mac OS 9.0, disassembly: the native Dialog, Menu, Control and Window Managers in the System file's data fork
  (MacDialogsLib, MenusLib, ControlsLib and ControlDefinitions, WindowsLib and WindowDefinitions), and the stub
  `'CDEF'`/`'WDEF'` resources that map the classic definition IDs to the Appearance ones.

Contents

1. [Conventions](#1-conventions)
2. [Menus (MENU)](#2-menus-menu)
3. [Menu bars (MBAR)](#3-menu-bars-mbar)
4. [Window templates (WIND)](#4-window-templates-wind)
5. [Dialog templates (DLOG)](#5-dialog-templates-dlog)
6. [Alert templates (ALRT)](#6-alert-templates-alrt)
7. [Positioning](#7-positioning)
8. [Dialog item lists (DITL)](#8-dialog-item-lists-ditl)
9. [Control templates (CNTL)](#9-control-templates-cntl)
10. [JSON output](#10-json-output)
11. [Diagnostics](#11-diagnostics)
12. [Not covered yet](#12-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold: big-endian values, `Rect` as four `i16` (top, left, bottom,
right), Pascal strings, Mac OS Roman text. Tags are those of [README.md](README.md); **[Code]** here is Mac OS 9.0's
native managers unless it names the 68k code. **[ClassicMac]** marks ClassicMac's own choices, as in
[TEXT.md](TEXT.md).

The Toolbox checks almost nothing: it never compares a template's counts with its size and reads past the end of
a short one [Code]. ClassicMac reads short data as far as it goes, with zeros for missing fields, and reports it
(§11) [ClassicMac].

---

## 2. Menus (MENU)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `i16` | Menu ID |
| +$02 | 2 | `i16` | Width: ignored; `GetMenu` recalculates it [Code] |
| +$04 | 2 | `i16` | Height: likewise |
| +$06 | 2 | `i16` | Menu definition: the `'MDEF'` resource ID itself (0 the standard one; 63 Appearance's, which Mac OS 9 draws the same way) [Code] |
| +$08 | 2 | `i16` | Filler |
| +$0A | 4 | `u32` | Enable flags: bit 0 the menu, bit *n* item *n* (1–31) |
| +$0E | 1 + *n* | Pascal string | Title (the apple character $14 for the Apple menu) |
| … | | | Items, then a 0 byte (an item with an empty text ends the list) |

Each item [Doc] (*Toolbox Essentials*, Menu Manager):

| Size | Type | Meaning |
| --- | --- | --- |
| 1 + *n* | Pascal string | Text |
| 1 | `u8` | Icon number: the icon is resource 256 + number (0 none); a script code with key equivalent $1C |
| 1 | `u8` | Key equivalent: the Command-key character (0 none), or one of the codes below |
| 1 | `u8` | Mark character (0 none); a submenu's menu ID with key equivalent $1B |
| 1 | `u8` | Style: QuickDraw face bits 0–6 ([TEXT.md](TEXT.md) §6.2); bit 7 reserved |

Key equivalents $1A–$1E are codes, not keys, and are cleared once read [Code] (MenusLib, and the 68k standard MDEF):

| Value | Meaning |
| --- | --- |
| $1A | A small icon with no mark column: `'SICN'` −4000 + icon number (Mac OS 9 tries an icon suite of that ID first) |
| $1B | A submenu: the mark byte is its menu ID. `GetNewMBar` does not load submenus; the application does |
| $1C | The icon byte is the item's script code, not an icon |
| $1D | The large icon (`'cicn'` or `'ICON'` 256 + icon number) drawn at half size |
| $1E | A small icon: `'cicn'` 256 + icon number at half size, else `'SICN'` 256 + icon number |

$1F and every other value are ordinary keys. Icons are looked up as `'cicn'` first on a colour machine (never for
$1A), then `'ICON'` or `'SICN'`; with $1A, $1D or $1E an icon is looked up even when the icon number is 0 [Code].

- **Dividers.** An item is drawn as a divider line when its text starts with "-" (any length) and it has no icon and
  no Command key once the codes are read [Code]. ClassicMac counts an item as a divider when its text starts with "-",
  its icon number is 0 and its key equivalent is 0, $1B or $1C (the others look an icon up) [ClassicMac].
- **Enabling.** A divider is always disabled [Code]. Items past 31 have no enable bit and are enabled whenever the menu
  is [Code].
- **Marks.** Chicago draws $11 as the Command key ⌘, $12 as a check mark ✓, $13 as a diamond ◆ and $14 as the Apple
  logo [Doc] (*Toolbox Essentials*: `commandMark`, `checkMark`, `diamondMark`, `appleMark`); the JSON shows them as
  those Unicode characters.
- **Colours and extensions.** `GetMenu` also applies the `'mctb'` of the same ID, and Mac OS 9's reads an `'xmnu'`
  (extended item data) of the same ID [Code]. ClassicMac reads neither yet (§12).

---

## 3. Menu bars (MBAR)

A count (`i16`), then that many menu IDs (`i16`), in the order the menus appear [Doc]. Nothing else [Code]. The 68k
`GetNewMBar` reads one ID even when the count is 0 (its loop tests at the end) [Code: 68k]; ClassicMac reads none
[ClassicMac].

---

## 4. Window templates (WIND)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 8 | `Rect` | The content area, in global coordinates |
| +$08 | 2 | `i16` | Window definition ID: the `'WDEF'` resource × 16 + variation |
| +$0A | 1 | `u8` | Visible (non-zero): the window is made invisible, then shown [Code] |
| +$0B | 1 | | Filler |
| +$0C | 1 | `u8` | Close box (non-zero) |
| +$0D | 1 | | Filler |
| +$0E | 4 | `i32` | Reference value, for the application |
| +$12 | 1 + *n* | Pascal string | Title |
| (even) | 2 | `u16` | Positioning (§7), when the resource holds it |

A `'wctb'` of the same ID gives the window's colours [Code].

Classic definition IDs [Doc], and the Appearance ones Mac OS 9 draws them with (`'WDEF'` 0 is a stub that maps them)
[Code]: 0 `documentProc` (1025), 1 `dBoxProc` (1042, or 1044 for an alert), 2 `plainDBox` (1040), 3 `altDBoxProc`
(1041), 4 `noGrowDocProc` (1024), 5 `movableDBoxProc` (1043, or 1045 for an alert), 8 `zoomDocProc` (1031),
12 `zoomNoGrow` (1030), 16–23 `rDocProc` (corner radius 16, 4, 6, 8, 10, 12, 20 or 24 by the variation ÷ 2).

---

## 5. Dialog templates (DLOG)

As `'WIND'` (§4), with the item list's ID inserted before the title [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00–$11 | 18 | | As `'WIND'`: rectangle, definition ID, visible, filler, close box, filler, reference value |
| +$12 | 2 | `i16` | The item list: `'DITL'` ID |
| +$14 | 1 + *n* | Pascal string | Title |
| (even) | 2 | `u16` | Positioning (§7), when the resource holds it |

The Dialog Manager also reads, by the `'DLOG'`'s ID, a `'dctb'` (colours) and a `'dlgx'` (Appearance flags), and by
the `'DITL'`'s ID an `'ictb'` (item colours and fonts) [Code] (§12).

---

## 6. Alert templates (ALRT)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 8 | `Rect` | The alert's rectangle, in global coordinates |
| +$08 | 2 | `i16` | The item list: `'DITL'` ID |
| +$0A | 2 | `u16` | Stages |
| +$0C | 2 | `u16` | Positioning (§7), when the resource holds it |

The stages word holds four 4-bit entries, stage 1 in the lowest and stage 4 in the highest [Doc] (Rez `Types.r`)
[Code]. Each entry: bit 3, the default button (0 item 1, 1 item 2); bit 2, the alert is drawn at this stage; bits 0–1,
the number of beeps (`SysBeep` through `ErrorSound`; not a `'snd '` ID) [Code].

An `'actb'` and an `'alrx'` of the same ID give the colours and the Appearance flags [Code] (§12).

---

## 7. Positioning

System 7 added a positioning word after the templates [Doc] (*Toolbox Essentials*; Rez `Types.r`) [Code]:

- **Present** when the resource holds at least 2 more bytes than its fixed part: after the stages (`'ALRT'`, at
  +$0C), or after the title (`'WIND'`, `'DLOG'`). The word is at the next even offset: one pad byte follows the title
  when its length is even [Code]. A resource with one byte to spare has no word. Mac OS 9 compares the spare bytes
  unsigned, so an `'ALRT'` under 12 bytes reads a word past its end [Code]; ClassicMac records none, and also none
  when the pad byte leaves only one byte for the word [ClassicMac].
- **Used** only when its low 11 bits are $00A; any other value, `noAutoCenter` ($0000) included, is ignored and the
  rectangle is taken as it is [Code].
- **Fields** of a used word [Code]:

| Bits | Meaning |
| --- | --- |
| 15–14 | The reference: 0 the main screen (less the menu bar), 1 the screen holding most of the parent window, 2 the parent window, 3 a parent path |
| 13 | 1: centre horizontally |
| 12–11 | Vertically: 0 at the top, 1 centred, 2 alert position (a fifth of the space above), 3 staggered |

The parent window is the first visible window behind the new one; with none, the main screen [Code]. Rez names the
usual combinations [Doc]:

| Value | Name |
| --- | --- |
| $0000 | `noAutoCenter` |
| $280A | `centerMainScreen` |
| $300A | `alertPositionMainScreen` |
| $380A | `staggerMainScreen` |
| $A80A | `centerParentWindow` |
| $B00A | `alertPositionParentWindow` |
| $B80A | `staggerParentWindow` |
| $680A | `centerParentWindowScreen` |
| $700A | `alertPositionParentWindowScreen` |
| $780A | `staggerParentWindowScreen` |

Mac OS 9 leaves a window where it is when the word has neither horizontal nor vertical placement; the 68k code moves
it to the reference's top-left corner [Code].

---

## 8. Dialog item lists (DITL)

An `i16` count **less one**, then the items [Doc]:

| Size | Type | Meaning |
| --- | --- | --- |
| 4 | | Placeholder: should be 0 (Mac OS 9 keeps a user item's non-zero value and calls it as a drawing procedure) [Code] |
| 8 | `Rect` | The item's rectangle, in the dialog's local coordinates |
| 1 | `u8` | Type; bit 7 ($80, `itemDisable`) disables it, the only flag |
| 1 | `u8` | Data length |
| *n* | | Data, then a pad byte when *n* is odd, for every type [Code] |

- **Count.** A signed number: −1 ($FFFF) or any negative count is an empty list [Code]. It is never checked against
  the resource's size: a count past the data makes the Dialog Manager read past the end [Code]. ClassicMac stops at the
  end of the data (§11) [ClassicMac].
- **Types.** Mac OS 9 accepts exactly these values of the type less bit 7; any other item draws nothing but still
  takes space and clicks [Code]. The 68k Dialog Manager tested bits in the order control, editable text, static text,
  icon, picture, so combined values took the first that matched [Code: 68k].

| Type | Name | Data |
| --- | --- | --- |
| 0 | user item | none; the application draws it |
| 1 | help item | `i16` kind, `i16` resource ID (kind 1: an `'hdlg'`; 2: an `'hrct'`; 8: an `'hdlg'` appended, with an `i16` offset that the Dialog Manager overwrites) [Code] |
| 4 | button | its title |
| 5 | check box | its title |
| 6 | radio button | its title |
| 7 | control | a `'CNTL'` ID |
| 8 | static text | the text |
| 16 | editable text | the initial text |
| 32 | icon | an icon ID: `'cicn'` first in a colour dialog, else `'ICON'`; Mac OS 9 draws IDs 0, 1 and 2 as the system's stop, note and caution icons [Code] |
| 64 | picture | a `'PICT'` ID |

- **IDs.** A control's, icon's or picture's ID is the `i16` at the start of the data, whatever the length says [Code];
  ClassicMac reads it only when the length is at least 2 [ClassicMac].
- **Text.** The data is the text itself, with no length byte of its own. In static text, `^0`–`^3` are replaced by
  `ParamText`'s strings when drawn, on a copy (a missing string deletes the citation) [Code].

---

## 9. Control templates (CNTL)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 8 | `Rect` | The control's rectangle, in the window's local coordinates |
| +$08 | 2 | `i16` | Initial value |
| +$0A | 1 | `u8` | Visible (non-zero) |
| +$0B | 1 | | Filler |
| +$0C | 2 | `i16` | Maximum |
| +$0E | 2 | `i16` | Minimum |
| +$10 | 2 | `i16` | Control definition ID: the `'CDEF'` resource × 16 + a 4-bit variation (`'CDEF'` 0 when missing) |
| +$12 | 4 | `i32` | Reference value |
| +$16 | 1 + *n* | Pascal string | Title |

A `'cctb'` of the same ID gives the control's colours [Code].

Classic definition IDs [Doc]: 0 `pushButProc`, 1 `checkBoxProc`, 2 `radioButProc` (+8 `useWFont`: the window's
font), 16 `scrollBarProc`, 1008 `popupMenuProc` (+1 `popupFixedWidth`, +4 `popupUseAddResMenu`, +8 `popupUseWFont`).
Mac OS 9 draws the first three with Appearance's buttons (368–370) [Code].

A **pop-up menu** reads the fields otherwise [Code]:

| Field | Meaning |
| --- | --- |
| Minimum | The `'MENU'` ID |
| Maximum | The title's width in pixels |
| Value | Low byte: the title's justification (0 left, 1 centre, −1 right); bits 8–14: its style (face bits × 256); bit 15: no style |
| Reference value | With `popupUseAddResMenu`, the resource type whose names are added to the menu |

Once made, the control sets its minimum to 1, its maximum to the number of items and its value to 1 [Code].

---

## 10. JSON output

One `.json` file per resource, UTF-8, indented by two spaces, LF line ends [ClassicMac]. Every stored field is kept;
names for known codes are added beside the numbers. Rectangles are objects `{"top", "left", "bottom", "right"}`.

| Type | Decoder | Fields |
| --- | --- | --- |
| `MENU` | `ui.menu` | `id`, `title`, `enabled` (bit 0), `definition`, `width`, `height`, `enableFlags`, `items[]`: `text`, `enabled`, `divider` (only when true), `icon`, `keyEquivalent` (the byte), `keyKind` (for $1A–$1E) or `key` (the character, when printable), `mark` (the byte), `submenu` (for $1B) or `markCharacter`, `face`, `style` (names of the face bits) |
| `MBAR` | `ui.menu-bar` | `menus[]` |
| `WIND` | `ui.window` | `title`, `bounds`, `definition`, `definitionName` (when classic), `visible`, `goAway`, `refCon`, `position` |
| `DLOG` | `ui.dialog` | as `WIND`, and `items` (the `'DITL'` ID) |
| `ALRT` | `ui.alert` | `bounds`, `items`, `stagesWord`, `stages[]` (stage 1 first: `stage`, `defaultItem`, `drawn`, `sound`), `position` |
| `DITL` | `ui.dialog-items` | `items[]`: `number` (from 1), `type`, `typeCode`, `enabled`, `bounds`, then `text`, `resourceId`, a help item's `helpKind`, `helpKindName`, `resourceId` and `offset`, or `data` (hex) |
| `CNTL` | `ui.control` | `title`, `bounds`, `definition`, `definitionName`, `value`, `minimum`, `maximum`, `visible`, `refCon`; a pop-up menu adds `popup`: `menu`, `titleWidth`, `titleJustification`, `titleNoStyle`, `titleStyle`, `addResMenu` |

`position` is `null` when the resource has no positioning word, else `{"code", "name", "used"}`, `name` only for the
values of §7, and for a used word `screen` (`main`, `parentWindowScreen`, `parentWindow`, `parent`),
`centerHorizontally` and `vertical` (`none`, `center`, `alertPosition`, `stagger`). All decoders are version 1, and
record the encoding (`macintosh`).

---

## 11. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `ui.short` | Warning | The data ends before the resource's fields do (a `'DITL'` with fewer items than its count says, a `'MENU'` without its closing 0 byte, …); the JSON holds what was read |

---

## 12. Not covered yet

- Colour and extension resources: `'mctb'`, `'wctb'`, `'dctb'`, `'actb'`, `'cctb'`, `'ictb'`, `'dlgx'`, `'alrx'`,
  `'xmnu'`, `'dftb'`.
- Previews of dialogs, alerts and menus in the viewer.
- Writing these resources from JSON (with `pack`).
