# Menus (MENU, MBAR)

A `'MENU'` resource holds one menu: its ID, title, enable flags and items, each with its text, icon, Command key, mark
and style. An `'MBAR'` lists the menus of a menu bar. The Menu Manager also applies a menu's colours (`'mctb'`) and,
from Mac OS 8.5, its extended item data (`'xmnu'`), both by the menu's ID. ClassicMac reads all four to JSON, writes
`'MENU'` back from its fields and previews a menu pulled down. The rules the interface documents share are in
[windows-dialogs.md](windows-dialogs.md).

| | |
| --- | --- |
| Identified by | Resource types `'MENU'`, `'MBAR'`; `'mctb'` and `'xmnu'` by the `'MENU'` ID |
| ClassicMac | Reads all four; writes `'MENU'`. `ClassicMac.Resources.Decoders.Interface` (`InterfaceResources.ReadMenu`, `ReadMenuBar`, `InterfaceWriter.WriteMenu`, the `ui.menu`, `ui.menu-bar`, `ui.menu-colors` and `ui.menu-extension` decoders) |
| Verified against | ClassicMac's corpus of application resources (the write round trip) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials*, Menu Manager; Apple's Rez `Types.r`; Mac OS 9.0's MenusLib and the 68k standard `'MDEF'` (disassembly) |

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

[Code] is Mac OS 9.0's MenusLib unless it names the 68k code.

### 1.1 Menus (MENU)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Menu ID | `i16` |
| +$02 | 2 | Width | `i16`; ignored: `GetMenu` recalculates it [Code] |
| +$04 | 2 | Height | `i16`; likewise [Code] |
| +$06 | 2 | Menu definition | `i16`: the `'MDEF'` resource ID itself (0 the standard one; 63 Appearance's, which Mac OS 9 draws the same way) [Code] |
| +$08 | 2 | Filler | |
| +$0A | 4 | Enable flags | `u32`: bit 0 the menu, bit n item n (1–31) |
| +$0E | 1 + n | Title | Pascal string (the apple character $14 for the Apple menu) |
| … | | Items | Below, one after another |
| … | 1 | End | 0: an item with an empty text ends the list |

[Doc: Inside Macintosh: Macintosh Toolbox Essentials, Menu Manager]

Each item [Doc: Inside Macintosh: Macintosh Toolbox Essentials, Menu Manager]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 + n | Text | Pascal string |
| +$01 + n | 1 | Icon number | `u8`: the icon is resource 256 + number (0 none); a script code with key equivalent $1C |
| +$02 + n | 1 | Key equivalent | `u8`: the Command-key character (0 none), or one of the codes of §1.2 |
| +$03 + n | 1 | Mark | `u8`: the mark character (0 none); a submenu's menu ID with key equivalent $1B |
| +$04 + n | 1 | Style | `u8`: QuickDraw face bits 0–6 ([styled-text.md §1.4](styled-text.md#14-face-bits)); bit 7 reserved |

### 1.2 Key-equivalent codes

Key equivalents $1A–$1E are codes, not keys, and are cleared once read [Code: MenusLib, and the 68k standard `'MDEF'`]:

| Value | Meaning |
| --- | --- |
| $1A | A small icon with no mark column: `'SICN'` −4000 + icon number (Mac OS 9 tries an icon suite of that ID first) |
| $1B | A submenu: the mark byte is its menu ID. `GetNewMBar` does not load submenus; the application does |
| $1C | The icon byte is the item's script code, not an icon |
| $1D | The large icon (`'cicn'` or `'ICON'` 256 + icon number) drawn at half size |
| $1E | A small icon: `'cicn'` 256 + icon number at half size, else `'SICN'` 256 + icon number |

$1F and every other value are ordinary keys. Icons are looked up as `'cicn'` first on a colour machine (never for
$1A), then `'ICON'` or `'SICN'`; with $1A, $1D or $1E an icon is looked up even when the icon number is 0 [Code].

### 1.3 Mark characters

Chicago draws $11 as the Command key ⌘, $12 as a check mark ✓, $13 as a diamond ◆ and $14 as the Apple logo
[Doc: Inside Macintosh: Macintosh Toolbox Essentials: `commandMark`, `checkMark`, `diamondMark`, `appleMark`].

### 1.4 Menu bars (MBAR)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Count | `i16` |
| +$02 | 2 × n | Menu IDs | `i16` each, in the order the menus appear |

[Doc: Inside Macintosh: Macintosh Toolbox Essentials] Nothing else follows [Code].

### 1.5 Menu colours (mctb)

By the `'MENU'` ID [Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Count | `i16` |
| +$02 | 30 × n | Entries | Below; an entry with menu ID −99 ends the table |

Each entry:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Menu ID | `i16`; 0 the menu bar, −99 the end |
| +$02 | 2 | Item | `i16`; 0 the menu's title |
| +$04 | 6 | Colour 1 | `RGBColor` |
| +$0A | 6 | Colour 2 | `RGBColor` |
| +$10 | 6 | Colour 3 | `RGBColor` |
| +$16 | 6 | Colour 4 | `RGBColor` |
| +$1C | 2 | Reserved | |

What the four colours are depends on the entry [Doc]:

| Entry | Colour 1 | Colour 2 | Colour 3 | Colour 4 |
| --- | --- | --- | --- | --- |
| The menu bar (ID 0) | The titles | The menus' background | The items | The bar |
| A title (item 0) | The title | The menu's background | Its items | The bar |
| An item | Its mark | Its text | Its Command key | Its background |

### 1.6 Extended menu item data (xmnu)

By the `'MENU'` ID; Mac OS 8.5 and later [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `i16`; not checked |
| +$02 | 2 | Count | `i16` |
| +$04 | … | Items | From item 1, each an `i16` key, then the key's data |

Key 1 is followed by 28 bytes; any other key has no data [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Command ID | `u32` |
| +$04 | 1 | Modifiers | 1 Shift, 2 Option, 4 Control, 8 no Command key |
| +$05 | 1 | Icon type | |
| +$06 | 4 | Placeholder | |
| +$0A | 4 | Text encoding | `i32`: −1 the system's, −2 the item's own |
| +$0E | 4 | Reference value | `i32` |
| +$12 | 4 | Second reference value | `i32` |
| +$16 | 2 | Submenu ID | `u16`; used when the item's mark gives none |
| +$18 | 2 | Font | `u16` |
| +$1A | 2 | Keyboard glyph | `i16` |

## 2. Reading

### 2.1 A menu

1. Read the fixed part and the title (§1.1).
2. Read items until a 0 length byte.
3. Read the key-equivalent codes (§1.2) and clear them.
4. An item is drawn as a divider line when its text starts with "-" (any length) and it has no icon and no Command key
   once the codes are read [Code].
5. Enabling: a divider is always disabled. Items past 31 have no enable bit and are enabled whenever the menu is
   [Code].
6. `GetMenu` applies the `'mctb'` of the same ID, and Mac OS 9's reads the `'xmnu'` of the same ID [Code].

### 2.2 A menu bar

Read the count, then that many IDs. The 68k `GetNewMBar` reads one ID even when the count is 0, as its loop tests at
the end [Code: 68k].

## 3. Writing

A `'MENU'` is written by the shared rules of [windows-dialogs.md §3](windows-dialogs.md#3-writing), and:

1. The filler word at +$08 is 0.
2. An item must have text: a zero length byte ends the item list.
3. A 0 byte follows the last item.

## 4. Variants

- `'xmnu'` is Mac OS 8.5's and later (§1.6).
- The 68k `GetNewMBar` reads one menu ID from an `'MBAR'` whose count is 0 (§2.2).
- The 68k standard `'MDEF'` and MenusLib agree on the key-equivalent codes (§1.2).

## 5. ClassicMac

- Short data is read as far as it goes, with zeros for the missing fields, and reported (`ui.short`). A `'MENU'`
  without its closing 0 byte is reported the same way. An `'mctb'` under 2 bytes, or with fewer whole entries than its
  count, and an `'xmnu'` under 4 bytes, or ending inside an item, are reported too. [ClassicMac]
- An `'MBAR'` with a count of 0 or less gives no menus. [ClassicMac]
- ClassicMac counts an item as a divider when its text starts with "-", its icon number is 0 and its key equivalent
  is 0, $1B or $1C (the others look an icon up). [ClassicMac]
- An `'mctb'` is read for as many entries as its count says; the −99 entry is listed (`kind` `end`) and does not stop
  the reading. [ClassicMac]
- JSON, by the shared rules of [windows-dialogs.md §5.2](windows-dialogs.md#52-json-output) [ClassicMac]:

| Type | Decoder | Fields |
| --- | --- | --- |
| `MENU` | `ui.menu` | `id`, `title`, `enabled` (bit 0), `definition`, `width`, `height`, `enableFlags`, `items[]`: `text`, `enabled`, `divider` (only when true), `icon`, `keyEquivalent` (the byte), `keyKind` (`smallIconNoMark`, `submenu`, `script`, `shrunkenIcon`, `smallIcon` for $1A–$1E) or `key` (the character, when printable), `mark` (the byte), `submenu` (for $1B) or `markCharacter`, `face`, `style` (names of the face bits) |
| `MBAR` | `ui.menu-bar` | `menus[]` |
| `mctb` | `ui.menu-colors` | `entries[]`: `menu`, `item`, `kind` (`menuBar`, `title`, `item`, `end`), then the four colours by name (`titles`/`title`, `menuBackground`, `items`, `menuBar`; or `mark`, `text`, `key`, `background`), each `red`, `green`, `blue`, `hex` |
| `xmnu` | `ui.menu-extension` | `version`, `items[]`: `number`, `key`, and for key 1 `commandId`, `command` (as four characters), `modifiers`, `modifiersNames` (`shift`, `option`, `control`, `noCommand`), `iconType`, `textEncoding`, `refCon`, `refCon2`, `submenu`, `font`, `glyph` |

- In the title and mark characters, $11–$14 are shown as ⌘, ✓, ◆ and U+F8FF (the Apple logo). [ClassicMac]
- `InterfaceWriter.WriteMenu` writes the enable flags as read, with the bits of items 1–31 set from each item's enabled
  state, except for dividers (read as disabled whatever their bit) and for items the menu does not have, whose stored
  bits are kept. It refuses an item with no text (`ArgumentException`). [ClassicMac]
- The viewer previews a `'MENU'` pulled down in the System 7 style, an approximation: the title highlighted on a strip
  of menu bar, then the items with their marks, styles (bold), Command keys, submenu arrows and dividers; disabled
  items grey. Item icons are not drawn. Text is drawn in Chicago 12 where installed, else a font of similar width; the
  zoom applies. The preview redraws as the edit form changes. The other menu resources preview as their JSON.
  [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ui.short` | Warning | A `'MENU'` ends before its fields or without its closing 0 byte; an `'MBAR'` with fewer IDs than its count; an `'mctb'` or `'xmnu'` that ends early | Reads as far as the data goes; the JSON holds what was read | Reads past the end of the resource [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs` with `GoldenTests` (outputs in `Golden/`): `MENU` 128
  (a divider, a Command key, a check mark, a submenu, an italic item and a disabled item), `MBAR` 128, `mctb` 128 (the
  bar, a title, an item, the end) and `xmnu` 128 (a skipped entry).
- `tests/ClassicMac.Resources.Decoders.Tests/InterfaceWriterTests.cs`
  (`Menus_keep_the_enable_bits_of_dividers_and_of_absent_items`): the divider's bit and items 4–31's bits kept; an item
  with no text refused.
- `tests/ClassicMac.App.Tests/InterfacePreviewTests.cs` (`Menus_preview_pulled_down`): the title, the divider and a
  disabled item.
- Read back and written again, every `'MENU'` but one of ClassicMac's corpus comes out byte for byte [Verified:
  ClassicMac's corpus, not in the repository].

## 8. Not covered

- In the preview: menus as Mac OS 9 draws them, item icons and `'mctb'` colours.
- Writing `'MBAR'`, `'mctb'` and `'xmnu'`, and writing any of them from JSON (with `pack`).

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Menu Manager: the menu, menu bar and menu colour
   resources, and the mark constants.
2. Apple, MPW Rez `Types.r`: field order and the named constants.
3. Mac OS 9.0's System file, disassembly: MenusLib ([README.md](../README.md#reference-builds)).
4. The 68k ROM (`$077D`), disassembly: `GetNewMBar` and the standard `'MDEF'`.
