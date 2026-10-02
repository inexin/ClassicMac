# Menus (MENU, MBAR)

Contents

1. [Menus (MENU)](#1-menus-menu)
2. [Menu bars (MBAR)](#2-menu-bars-mbar)
3. [Viewer preview](#3-viewer-preview)
4. [Writing templates](#4-writing-templates)

---

## 1. Menus (MENU)

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
| 1 | `u8` | Style: QuickDraw face bits 0–6 ([styled-text.md §4.2](styled-text.md#42-face-bits)); bit 7 reserved |

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
  (extended item data) of the same ID [Code]. ClassicMac reads them as their own resources ([windows-dialogs.md §6](windows-dialogs.md#6-colour-and-extension-resources)).

---

## 2. Menu bars (MBAR)

A count (`i16`), then that many menu IDs (`i16`), in the order the menus appear [Doc]. Nothing else [Code]. The 68k
`GetNewMBar` reads one ID even when the count is 0 (its loop tests at the end) [Code: 68k]; ClassicMac reads none
[ClassicMac].

---

## 3. Viewer preview

The title highlighted on a strip of menu bar, then the items with their marks, styles (bold), Command keys, submenu
arrows and dividers; disabled items grey. Item icons are not drawn. Text is drawn in Chicago 12 where installed, else a
font of similar width; the zoom applies [ClassicMac].

---

## 4. Writing templates

ClassicMac's editor writes `MENU`, `WIND`, `DLOG`, `ALRT`, `DITL` and `CNTL` back from their fields
(`InterfaceWriter`), in the layouts above [ClassicMac]:

- **`MENU`:** the enable flags are those read, with the bits of items 1–31 set from each item's enabled state, except
  for dividers (which are read as disabled whatever their bit) and for items the menu does not have, whose stored bits
  are kept. An item must have text: a zero length byte ends the item list.
