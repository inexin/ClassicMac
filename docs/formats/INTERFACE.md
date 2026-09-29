# Interface resources — an implementer's specification

This document describes the classic Mac OS Toolbox's interface resources: menus (`'MENU'`, `'MBAR'`), window, dialog
and alert templates (`'WIND'`, `'DLOG'`, `'ALRT'`), dialog item lists (`'DITL'`), control templates (`'CNTL'`), and
their colour and Appearance extension resources (`'wctb'`, `'dctb'`, `'actb'`, `'cctb'`, `'mctb'`, `'ictb'`,
`'dlgx'`, `'alrx'`, `'xmnu'`). It
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
10. [Colour and extension resources](#10-colour-and-extension-resources)
11. [JSON output](#11-json-output)
12. [Viewer previews](#12-viewer-previews)
13. [Writing templates](#13-writing-templates)
14. [Diagnostics](#14-diagnostics)
15. [Not covered yet](#15-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold: big-endian values, `Rect` as four `i16` (top, left, bottom,
right), Pascal strings, Mac OS Roman text. Tags are those of [README.md](README.md); **[Code]** here is Mac OS 9.0's
native managers unless it names the 68k code. **[ClassicMac]** marks ClassicMac's own choices, as in
[TEXT.md](TEXT.md).

The Toolbox checks almost nothing: it never compares a template's counts with its size and reads past the end of
a short one [Code]. ClassicMac reads short data as far as it goes, with zeros for missing fields, and reports it
(§14) [ClassicMac].

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
  (extended item data) of the same ID [Code]. ClassicMac reads them as their own resources (§10).

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
the `'DITL'`'s ID an `'ictb'` (item colours and fonts) [Code] (§10).

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

An `'actb'` and an `'alrx'` of the same ID give the colours and the Appearance flags [Code] (§10).

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
  end of the data (§14) [ClassicMac].
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

## 10. Colour and extension resources

Each is found by the ID of what it belongs to [Code].

**`'wctb'`, `'dctb'`, `'actb'`, `'cctb'`** (a window's, a dialog's, an alert's and a control's colours, by the
`'WIND'`, `'DLOG'`, `'ALRT'` and `'CNTL'` ID) are colour tables [Doc] [Code]: `u32` seed, `u16` flags, `i16` count
less one (−1: no entries), then per entry an `i16` part code and an `RGBColor`. An 8-byte `'dctb'` or `'actb'` with no
entries makes the dialog a colour one with the default colours [Code].

| Part | Windows, dialogs, alerts | Controls |
| --- | --- | --- |
| 0 | content | frame |
| 1 | frame | body |
| 2 | text | text |
| 3 | hilite | thumb |
| 4 | title bar | fill pattern |
| 5–6 | hilite light, dark | arrows light, dark |
| 7–8 | title bar light, dark | thumb light, dark |
| 9–10 | dialog light, dark | hilite light, dark |
| 11–12 | tinge light, dark | title bar light, dark |
| 13–14 | | tinge light, dark |

**`'mctb'`** (a menu's colours, by the `'MENU'` ID) [Doc] (*Toolbox Essentials*): an `i16` count, then 30-byte entries
of `i16` menu ID, `i16` item, four `RGBColor`s and a reserved `i16`; an entry with menu ID −99 ends the table. The
colours of the menu bar entry (ID 0): the titles, the menus' background, the items, the bar; of a title entry (item 0):
the title, the menu's background, its items, the bar; of an item: its mark, its text, its Command key, its background.

**`'ictb'`** (item colours and fonts, by the `'DITL'` ID) [Code]: one 4-byte entry per item (an `i16` value and an
`i16` offset from the start of the `'ictb'`), in item order; 0 and 0 keep the defaults.

- A button, check box, radio button or control: the value is the length of a control colour table (as `'cctb'`) at the
  offset.
- Static or editable text: the value is a set of flags for a 20-byte text style at the offset: `i16` font, face (the
  high byte of a word), `i16` size, foreground and background `RGBColor`s, `i16` transfer mode. Flags: bit 0 font,
  bit 1 face, bit 2 size, bit 3 foreground, bit 4 the size is added to the dialog's, bit 13 background, bit 14 mode,
  bit 15 the font word is an offset to the font's name (a Pascal string).

**`'dlgx'`** (by the `'DLOG'` ID) [Code]: `i16` version (0), `u32` flags: 1 theme background, 2 control hierarchy,
4 movable modal, 8 theme controls.

**`'alrx'`** (by the `'ALRT'` ID) [Code]: `i16` version (0 or 1), `u32` flags (as `'dlgx'`, with 4: the alert is
movable), `i32` reference value, a byte (non-zero: the Appearance alert window), a filler byte, then the window's
title: at +12 in version 1, at +28 after 16 reserved bytes in version 0. A version 0 resource too short for its title
there is read as version 1.

**`'xmnu'`** (extended menu item data, by the `'MENU'` ID; Mac OS 8.5 and later) [Code]: `i16` version (not checked),
`i16` count, then per item from item 1 an `i16` key: 1 is followed by 28 bytes (`u32` command ID, a modifiers byte (1
Shift, 2 Option, 4 Control, 8 no Command key), an icon type byte, a 4-byte placeholder, `i32` text encoding (−1 the
system's, −2 the item's own), two `i32` reference values, `u16` submenu ID (used when the item's mark gives none),
`u16` font, `i16` keyboard glyph); any other key has no data.

---

## 11. JSON output

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
`centerHorizontally` and `vertical` (`none`, `center`, `alertPosition`, `stagger`). All decoders are version 1; those
above record the encoding (`macintosh`).

| Type | Decoder | Fields |
| --- | --- | --- |
| `wctb`, `dctb`, `actb`, `cctb` | `ui.colors` | `seed`, `flags`, `entries[]`: `value`, `part` (when known), `red`, `green`, `blue` (0–65535), `hex` (`#rrggbb`) |
| `mctb` | `ui.menu-colors` | `entries[]`: `menu`, `item`, `kind` (`menuBar`, `title`, `item`, `end`), then the four colours by name |
| `ictb` | `ui.item-colors` | `itemList` (whether the `'DITL'` was found), `items[]`: `number`, `data`, `offset`, and `colors` (a control's table) or `textStyle` (`flags`, `font` or `fontName`, `face`, `size`, `addSize`, `foreground`, `background`, `mode`, as the flags give) |
| `dlgx` | `ui.dialog-extension` | `version`, `flags`, `flagsNames` |
| `alrx` | `ui.alert-extension` | `version`, `flags`, `flagsNames`, `movable`, `refCon`, `useThemeWindow`, `title` |
| `xmnu` | `ui.menu-extension` | `version`, `items[]`: `number`, `key`, and for key 1 `commandId`, `command`, `modifiers`, `modifiersNames`, `iconType`, `textEncoding`, `refCon`, `refCon2`, `submenu`, `font`, `glyph` |

Without its `'DITL'`, an `'ictb'`'s entries are listed without their colours or styles, as the item types are unknown.

---

## 12. Viewer previews

The viewer draws dialogs, alerts, lone item lists and menus in the System 7 style [ClassicMac]. It is an approximation
drawn by the viewer, not by the Toolbox (Mac OS 9 draws them with the Appearance Manager's theme), for seeing a
resource's layout; the other interface resources preview as their JSON.

- **Dialogs and alerts:** the window frame for the definition ID (a title bar for document and movable windows, the
  double border of a modal box, altDBox's shadow), filled with the content colour of the `'dctb'` or `'actb'` of the
  same ID, and the items of the `'DITL'` in their rectangles: buttons (an alert's stage 1 default with its ring),
  check boxes, radio buttons, controls from their `'CNTL'` (buttons, check boxes, radio buttons, scroll bars, pop-up
  menus; other definitions as a labelled box), static and editable text (unsubstituted), icons (`'cicn'`, else
  `'ICON'`), pictures scaled to their rectangle, and user items as a dotted box. A lone `'DITL'` is drawn in a plain box
  around its items.
- **Menus:** the title highlighted on a strip of menu bar, then the items with their marks, styles (bold), Command keys,
  submenu arrows and dividers; disabled items grey. Item icons are not drawn.
- Text is drawn in Chicago 12 where installed, else a font of similar width; the zoom applies.

---

## 13. Writing templates

ClassicMac's editor writes `MENU`, `WIND`, `DLOG`, `ALRT`, `DITL` and `CNTL` back from their fields
(`InterfaceWriter`), in the layouts above [ClassicMac]:

- Text is Mac OS Roman (a line break in item text is a carriage return); text it cannot hold, and a string over 255
  bytes, are refused.
- Filler fields (the `MENU` word after the MDEF ID, the bytes after `WIND`/`DLOG`'s visible and close-box flags,
  a `DITL` item's placeholder long and the pad byte after odd-length data, the pad before a `DLOG`'s positioning
  word) are written as 0; the Toolbox does not read them.
- **`MENU`:** the enable flags are those read, with the bits of items 1–31 set from each item's enabled state, except
  for dividers (which are read as disabled whatever their bit) and for items the menu does not have, whose stored bits
  are kept. An item must have text: a zero length byte ends the item list.
- **`DITL`:** an item's data is made from its kind: the text of buttons, check boxes, radio buttons, static and
  editable text; the resource ID of controls, icons and pictures; help and user items keep their data as stored.
- **`DLOG`/`WIND`/`ALRT`:** the positioning word is written when the template had one (or the editor adds it), at the
  next even offset for `WIND` and `DLOG`.
- Read back and written again, every `WIND`, `ALRT` and `CNTL` of the test corpus comes out byte for byte, and every
  `MENU` but one; `DLOG` and `DITL` differ only in their filler and pad bytes [Verified: ClassicMac's corpus].

## 14. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `ui.short` | Warning | The data ends before the resource's fields do (a `'DITL'` with fewer items than its count says, a `'MENU'` without its closing 0 byte, a colour table with fewer entries than its count, …); the JSON holds what was read |

---

## 15. Not covered yet

- `'dftb'` (Appearance's dialog font table) and `'hdlg'`/`'hrct'` (the Help Manager's).
- In the previews: `'ictb'` item colours and fonts, menu icons and `'mctb'` colours, and the Appearance look.
- Writing these resources from JSON (with `pack`).
