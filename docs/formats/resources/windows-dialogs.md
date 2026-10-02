# Window, dialog and alert templates (WIND, DLOG, ALRT)

The Toolbox's window, dialog and alert templates (`'WIND'`, `'DLOG'`, `'ALRT'`), the positioning word System 7 added
to them, and the resources the Window and Dialog Managers find by a template's ID: the colour tables `'wctb'`,
`'dctb'` and `'actb'` and Appearance's extensions `'dlgx'` and `'alrx'`. Every Mac application with a window or a
dialog has them. This document also holds how Mac OS 9.0 draws a dialog or an alert with the Platinum theme, which
ClassicMac's previews reproduce, and the rules the interface documents share ([menus.md](menus.md),
[dialog-items.md](dialog-items.md), [controls.md](controls.md)). ClassicMac reads these resources to JSON, writes the
templates back from their fields, and draws dialogs, alerts and item lists as Mac OS 9.0 does.

| | |
| --- | --- |
| Identified by | Resource types `'WIND'`, `'DLOG'`, `'ALRT'`; `'wctb'`, `'dctb'`, `'actb'`, `'dlgx'`, `'alrx'`, each by the ID of the template it belongs to |
| ClassicMac | Reads and writes the templates; reads the colour and extension resources; draws dialogs and alerts. `ClassicMac.Resources.Decoders.Interface` (`InterfaceResources`, `InterfaceWriter`, the `ui.*` decoders, `DialogDrawings`, `DialogRenderer`) |
| Verified against | Screen captures of Mac OS 9.0 (Appearance 1.1.1, Platinum) drawing ClassicMac's test dialogs and alerts, at 32 and 8 bits<br>ClassicMac's corpus of application resources (the write round trip) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials*; Apple's Rez `Types.r`; Mac OS 9.0's MacDialogsLib, WindowsLib and WindowDefinitions, ControlsLib and ControlDefinitions, and the stub `'WDEF'`/`'CDEF'` resources (disassembly); the 68k ROM's Window and Dialog Managers |

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

The shared conventions of [README.md](../README.md#conventions) hold. In this document and the other interface
documents, [Code] is Mac OS 9.0's native managers unless it names the 68k code.

### 1.1 Window templates (WIND)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | Bounds | `Rect`: the content area, in global coordinates |
| +$08 | 2 | Definition ID | `i16`: the `'WDEF'` resource × 16 + variation (§1.5) |
| +$0A | 1 | Visible | Non-zero: shown when made |
| +$0B | 1 | Filler | |
| +$0C | 1 | Close box | Non-zero: the window has one (goAway) |
| +$0D | 1 | Filler | |
| +$0E | 4 | Reference value | `i32`, for the application |
| +$12 | 1 + n | Title | Pascal string |
| (even) | 2 | Positioning | `u16` (§1.4), when the resource holds it |

[Doc: Inside Macintosh: Macintosh Toolbox Essentials]

### 1.2 Dialog templates (DLOG)

As `'WIND'`, with the item list's ID inserted before the title [Doc: Inside Macintosh: Macintosh Toolbox Essentials]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 18 | As `'WIND'` +$00–+$11 | Bounds, definition ID, visible, filler, close box, filler, reference value |
| +$12 | 2 | Item list | `i16`: the `'DITL'` ID ([dialog-items.md](dialog-items.md)) |
| +$14 | 1 + n | Title | Pascal string |
| (even) | 2 | Positioning | `u16` (§1.4), when the resource holds it |

### 1.3 Alert templates (ALRT)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | Bounds | `Rect`: the alert's rectangle, in global coordinates |
| +$08 | 2 | Item list | `i16`: the `'DITL'` ID |
| +$0A | 2 | Stages | `u16`: four 4-bit entries, below |
| +$0C | 2 | Positioning | `u16` (§1.4), when the resource holds it |

[Doc: Inside Macintosh: Macintosh Toolbox Essentials]

The stages word holds stage 1 in its lowest 4 bits and stage 4 in its highest [Doc: Rez Types.r] [Code]. Each entry
[Code]:

| Bits | Field | Notes |
| --- | --- | --- |
| 3 | Default button | 0: item 1; 1: item 2 |
| 2 | Drawn | The alert is drawn at this stage |
| 0–1 | Sound | The number of beeps (`SysBeep` through `ErrorSound`); not a `'snd '` ID |

### 1.4 The positioning word

A `u16` after the template's fixed part: after the stages in an `'ALRT'` (+$0C), after the title in a `'WIND'` or
`'DLOG'`, at the next even offset (one pad byte follows a title of even length) [Doc: Inside Macintosh: Macintosh
Toolbox Essentials; Rez Types.r] [Code]. A used word (§2.2) has these fields [Code]:

| Bits | Field | Notes |
| --- | --- | --- |
| 15–14 | Reference | 0: the main screen (less the menu bar); 1: the screen holding most of the parent window; 2: the parent window; 3: a parent path |
| 13 | Horizontal | 1: centre horizontally |
| 12–11 | Vertical | 0: at the top; 1: centred; 2: alert position (a fifth of the space above); 3: staggered |
| 10–0 | Marker | $00A in a used word |

Rez names the usual values [Doc: Rez Types.r]:

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

### 1.5 Window definition IDs

The classic IDs [Doc: Inside Macintosh: Macintosh Toolbox Essentials], and the Appearance IDs Mac OS 9 draws them with
(`'WDEF'` 0 is a stub that maps them) [Code]:

| ID | Name | Appearance ID |
| --- | --- | --- |
| 0 | `documentProc` | 1025 |
| 1 | `dBoxProc` | 1042; 1044 for an alert |
| 2 | `plainDBox` | 1040 |
| 3 | `altDBoxProc` | 1041 |
| 4 | `noGrowDocProc` | 1024 |
| 5 | `movableDBoxProc` | 1043; 1045 for an alert |
| 8 | `zoomDocProc` | 1031 |
| 12 | `zoomNoGrow` | 1030 |
| 16–23 | `rDocProc` | Corner radius 16, 4, 6, 8, 10, 12, 20 or 24 by the variation ÷ 2 |

### 1.6 Colour tables (wctb, dctb, actb)

A window's, a dialog's and an alert's colours, by the `'WIND'`, `'DLOG'` and `'ALRT'` ID; a control's `'cctb'` has the
same layout ([controls.md §1.2](controls.md#12-control-colour-tables-cctb)) [Doc: Inside Macintosh: Macintosh Toolbox Essentials] [Code]. It is
the `ColorTable` of a `'clut'` ([palettes.md §1.1](palettes.md#11-colour-tables-clut)), with part codes for values:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Seed | `u32` |
| +$04 | 2 | Flags | `u16` |
| +$06 | 2 | Count less one | `i16`; −1: no entries |
| +$08 | 8 × n | Entries | Each an `i16` part code and an `RGBColor` (three `u16`) |

Part codes of windows, dialogs and alerts:

| Part | Colour |
| --- | --- |
| 0 | Content |
| 1 | Frame |
| 2 | Text |
| 3 | Hilite |
| 4 | Title bar |
| 5–6 | Hilite light, dark |
| 7–8 | Title bar light, dark |
| 9–10 | Dialog light, dark |
| 11–12 | Tinge light, dark |

### 1.7 Dialog extensions (dlgx)

By the `'DLOG'` ID [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `i16`: 0 |
| +$02 | 4 | Flags | `u32`: 1 theme background; 2 control hierarchy (embedding); 4 movable modal; 8 theme controls |

### 1.8 Alert extensions (alrx)

By the `'ALRT'` ID [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `i16`: 0 or 1 |
| +$02 | 4 | Flags | `u32`: as `'dlgx'` (§1.7), with 4 meaning the alert is movable |
| +$06 | 4 | Reference value | `i32` |
| +$0A | 1 | Theme window | Non-zero: the Appearance alert window |
| +$0B | 1 | Filler | |
| +$0C | 1 + n | Title | Version 1: the window's title, a Pascal string |
| +$0C | 16 | Reserved | Version 0 |
| +$1C | 1 + n | Title | Version 0: the window's title |

## 2. Reading

### 2.1 Templates

1. Read the fields in order (§1.1–§1.3). A visible window is made invisible, then shown [Code].
2. Read the positioning word when the resource holds at least 2 bytes past the fixed part (§2.2).
3. Find the resources of the same ID (§2.3).

The Toolbox checks almost nothing: it never compares a template's counts with its size, and reads past the end of a
short one [Code].

### 2.2 Positioning a window

1. **Present.** The word is present when the resource holds at least 2 more bytes than its fixed part (the stages for
   an `'ALRT'`, the title for a `'WIND'` or `'DLOG'`); one byte to spare gives no word. Mac OS 9 compares the spare
   bytes unsigned, so an `'ALRT'` under 12 bytes reads a word past its end [Code].
2. **Used.** The word is used only when its low 11 bits are $00A. Any other value, `noAutoCenter` ($0000) included, is
   ignored and the rectangle is taken as it is [Code].
3. **Reference.** The parent window is the first visible window behind the new one; with none, the main screen
   [Code].
4. **Placement.** Centre horizontally when bit 13 is set; place vertically by bits 12–11 (§1.4) [Code]. With neither
   horizontal nor vertical placement, Mac OS 9 leaves the window where it is (§4 for the 68k code) [Code].

### 2.3 The resources found by ID

| Template | By its own ID | By its item list's ID |
| --- | --- | --- |
| `'WIND'` | `'wctb'` | — |
| `'DLOG'` | `'dctb'`, `'dlgx'` | `'DITL'`, `'ictb'` ([dialog-items.md §1.3](dialog-items.md#13-item-colours-and-fonts-ictb)) |
| `'ALRT'` | `'actb'`, `'alrx'` | `'DITL'`, `'ictb'` |
| `'CNTL'` | `'cctb'` ([controls.md](controls.md)) | — |

[Code]

- An 8-byte `'dctb'` or `'actb'` with no entries makes the dialog a colour one with the default colours [Code].
- An `'alrx'`'s title is at +$0C in version 1 and at +$1C in version 0; a version 0 resource too short for its title
  there is read as version 1 [Code].

### 2.4 How Mac OS 9.0 draws dialogs and alerts

Mac OS 9.0 with Appearance 1.1.1 and the Platinum theme draws a dialog (`GetNewDialog` with `DrawDialog` or
`ModalDialog`) and an alert (`Alert`, `StopAlert`, `NoteAlert`, `CautionAlert`) as below. Rules tagged [Verified] were
read from screen captures (§7); "the structure" is the window's frame, shadow and content.

**Frames.** By definition ID, as margins around the content (left, top, right, bottom), the 1-pixel shadow included,
which starts 2 pixels in [Verified]:

| Definition | Frame | Margins |
| --- | --- | --- |
| 1 `dBoxProc` | A black line; a bevel ($BBBBBB top-left, $555555 bottom-right; inside it white and $999999); 3 pixels of the content colour | 6, 6, 7, 7 |
| Alerts | As `dBoxProc`, the outer bevel tinted red ($FF9999, $FF6666) | 6, 6, 7, 7 |
| 5 `movableDBoxProc` | A 22-row title bar, then the `dBoxProc` frame with a $DDDDDD outer bevel | 6, 27, 7, 7 |
| 0 `documentProc` | A 22-row title bar with the collapse box at the right; a black line around the content; a 4-pixel bevelled border | 6, 22, 7, 7 |
| 2 `plainDBox` | A 1-pixel black frame | 1, 1, 1, 1 |
| 3 `altDBoxProc` | A 1-pixel black frame and a 2-pixel black shadow, 2 pixels down and in | 1, 1, 3, 3 |

Every frame piece is a few pixels at each corner and edge with a middle that repeats exactly to any size [Verified].

**Title bar.** Six pairs of stripes (white, $777777) on $CCCCCC. The title is black, its pen at (structure width −
title width) ÷ 2, its baseline on row 15; the stripes are cleared from 5 pixels before the pen to 3 pixels after the
title [Verified: three titles].

**Content colour.** The `'dctb'`'s part 0; the theme background $DDDDDD when the `'dlgx'` sets flag 1; white otherwise.
Every alert has the theme background, whatever its `'actb'` says. The `'dctb'`'s or `'actb'`'s text colour (part 2) is
not used: item text and the title are black. [Verified]

**Alert icon.** None for `Alert`; for `StopAlert`, `NoteAlert` and `CautionAlert` the system's stop, note and caution
icons (`'cicn'`, else `'ICON'`, IDs 0, 1 and 2, from the System file) at (10, 20, 42, 52) in the content [Verified: the
place]. An icon item with ID 0–2 takes the same icons [Code].

**Default ring.** An alert's default item (stage 1's, §1.3) is drawn as Appearance's default button, a ring 3 pixels
outside the button. Dialogs drawn with `DrawDialog` or `ModalDialog` have no ring. [Verified]

**Items** [Verified unless marked]:

- Buttons, and `'CNTL'` `pushButProc`: the Platinum button, rounded, filled $DDDDDD; the title centred, its baseline at
  top + (height − 15) ÷ 2 + 12.
- Check boxes and radio buttons, and `'CNTL'` 1 and 2: a 12 × 12 box 2 pixels in, centred vertically; the title's pen
  18 pixels in. A check box control with a non-zero value is drawn checked; the mark overhangs the box by 2 pixels.
- Static text: TextEdit's layout in the item, left-aligned, the first baseline 12 below the item's top, lines 16
  apart, clipped to the item. A disabled item draws the same.
- Editable text: as static text, with a 1-pixel black frame 3 pixels outside the item.
- Icons: `'cicn'` (masked), else `'ICON'` (`PlotIcon`, srcCopy) at the item's top-left. Pictures: `DrawPicture` into
  the item's rectangle.
- User items and help items draw nothing [Verified: a user item].
- Scroll bars (`'CNTL'` 16): the arrows together at the bottom; the track shaded below each black line; the scroll box
  (16 × 17 with its black lines, in the default accent colour) at (value − min) ÷ (max − min) of the track less the
  box, rounded [Verified: a vertical bar, one value].
- A control is drawn in its item's rectangle; an invisible one is not drawn.

**Text.** The system font, Charcoal 12, black, srcOr. Charcoal's metrics place every line: ascent 12, line height 16.
Mac OS 9 smooths Charcoal (anti-aliased greys). [Verified]

**Screen depth.** At 8 bits the layout and every frame and control pixel are the same as at 32: the Platinum greys and
colours are in the 8-bit system palette [Verified].

## 3. Writing

The rules for every interface template ClassicMac writes (`'MENU'`, `'WIND'`, `'DLOG'`, `'ALRT'`, `'DITL'`, `'CNTL'`):

1. Write the fields in the layouts of §1 and of [menus.md](menus.md), [dialog-items.md](dialog-items.md) and
   [controls.md](controls.md).
2. Text is Mac OS Roman, a line break in item text a carriage return; a Pascal string holds at most 255 bytes.
3. Write filler fields as 0: the bytes after a `'WIND'` or `'DLOG'`'s visible and close-box flags, the pad before a
   `'DLOG'`'s or `'WIND'`'s positioning word, and the fillers of [menus.md §3](menus.md#3-writing) and
   [dialog-items.md §3](dialog-items.md#3-writing). The Toolbox does not read them.
4. `'WIND'`, `'DLOG'`, `'ALRT'`: write the positioning word only when the template has one, at the next even offset for
   `'WIND'` and `'DLOG'`.

## 4. Variants

- The positioning word is System 7's; earlier templates end at the title or the stages [Doc: Inside Macintosh:
  Macintosh Toolbox Essentials].
- With neither horizontal nor vertical placement in a used word, the 68k code moves the window to the reference's
  top-left corner; Mac OS 9 leaves it where it is [Code].
- Mac OS 9 draws the classic definition IDs with Appearance's (§1.5). `'dlgx'` and `'alrx'` are Appearance's (Mac OS
  8 and later) [Code].
- §2.4 is Mac OS 9.0 with Appearance's Platinum theme; the 68k ROM's classic frames are not covered (§8).

## 5. ClassicMac

### 5.1 Reading

- Short data is read as far as it goes, with zeros for the missing fields, and reported (`ui.short`). [ClassicMac]
- The positioning word is recorded only when its two bytes are in the resource: none for an `'ALRT'` under 14 bytes,
  and none when the pad byte leaves only one byte for the word. [ClassicMac]

### 5.2 JSON output

One `.json` file per resource, UTF-8, indented by two spaces, LF line ends. Every stored field is kept; names for known
codes are added beside the numbers. Rectangles are objects `{"top", "left", "bottom", "right"}`. Every `ui.*` decoder
of the interface documents is version 1; the template decoders record the text encoding's name (`macintosh` by
default). [ClassicMac]

| Type | Decoder | Fields |
| --- | --- | --- |
| `WIND` | `ui.window` | `title`, `bounds`, `definition`, `definitionName` (for the IDs of §1.5), `visible`, `goAway`, `refCon`, `position` |
| `DLOG` | `ui.dialog` | As `WIND`, and `items` (the `'DITL'` ID) |
| `ALRT` | `ui.alert` | `bounds`, `items`, `stagesWord`, `stages[]` (stage 1 first: `stage`, `defaultItem`, `drawn`, `sound`), `position` |
| `wctb`, `dctb`, `actb`, `cctb` | `ui.colors` | `seed`, `flags`, `entries[]`: `value`, `part` (when known: §1.6, or [controls.md §1.2](controls.md#12-control-colour-tables-cctb) for `cctb`), `red`, `green`, `blue` (0–65535), `hex` (`#rrggbb`, the high bytes) |
| `dlgx` | `ui.dialog-extension` | `version`, `flags`, `flagsNames` (`useThemeBackground`, `useControlHierarchy`, `handleMovableModal`, `useThemeControls`) |
| `alrx` | `ui.alert-extension` | `version`, `flags`, `flagsNames` (as `dlgx`, with `movable` for 4), `movable`, `refCon`, `useThemeWindow`, `title` |

`position` is `null` when the resource has no positioning word, else `{"code", "name", "used"}`: `name` only for the
values of §1.4, and for a used word `screen` (`main`, `parentWindowScreen`, `parentWindow`, `parent` for references
0–3), `centerHorizontally` and `vertical` (`none`, `center`, `alertPosition`, `stagger`).

The other interface types' JSON is in [menus.md §5](menus.md#5-classicmac), [dialog-items.md
§5](dialog-items.md#5-classicmac) and [controls.md §5](controls.md#5-classicmac).

### 5.3 Dialog and alert previews

`DialogDrawings.Read` builds a `DialogDrawing` from a `'DLOG'`, `'ALRT'` or `'DITL'` and the resources of its fork
(item list, `'CNTL'`s, icons, pictures, `'dctb'`/`'actb'`, `'dlgx'`); `DialogRenderer.Render` draws it through a
`QuickDrawPort` in `MacOS9` mode, reproducing §2.4. The viewer previews `'DLOG'`, `'ALRT'` and `'DITL'` this way, at
zoom 2 with a 12-pixel gutter, and redraws the preview as an edit form changes; the other interface resources preview
as their JSON, and menus as [menus.md §5](menus.md#5-classicmac) says. [ClassicMac]

- **Output.** The bitmap is the structure region's bounding box; pixels outside the region are transparent. The
  content's size is clamped to 1–4096 pixels. `DialogRenderOptions.ScreenDepth` is 32 (the default) or 1, 2, 4, 8,
  16, through QuickDraw's colour matching; the viewer takes `DecodeOptions.ScreenDepth`. [ClassicMac]
- **Frames.** Each frame of §2.4 is stored as measured (`PlatinumArt`): a few pixels at each corner and edge, with one
  middle column and row that repeat to any size. 4, 8, 12 and 16–23 are drawn as `documentProc`, without a zoom box or
  rounded corners; other definitions as `plainDBox`. An alert is drawn with the alert frame whatever its kind. The title
  bar is always drawn active. A window with goAway gets a close box: the collapse box's frame without its bars, at the
  left [ClassicMac: not captured]. [ClassicMac]
- **Alerts.** An `'ALRT'` is previewed as `Alert` shows it (no icon). The stop, note and caution icons are the System
  file's: ClassicMac draws them only when an open file supplies them (any loaded fork with a `'cicn'` or `'ICON'` 0–2),
  and a grey 1-pixel frame in their place otherwise. [ClassicMac]
- **Items.** Static and editable text break lines at spaces and at returns [ClassicMac: TextEdit's own breaking is not
  reproduced]; `^0`–`^3` are not substituted. An `'ICON'` needs 128 bytes and is drawn 32 × 32; a `'cicn'` at its own
  size, masked; a picture item takes only a `'PICT'`. A damaged image draws nothing. An on radio button control's dot
  is drawn [ClassicMac: not captured]. [ClassicMac]
- **Controls.** The classic IDs and their Appearance equivalents are drawn: buttons 0, 8, 368, 376; check boxes 1, 9,
  369, 377; radio buttons 2, 10, 370, 378; scroll bars 16–31 and 384–387; pop-up menus 1008–1023 and 400–415. A scroll
  bar is vertical when its item is at least as tall as wide; a horizontal one is the vertical one turned; it has no
  scroll box when max ≤ min, and only its track when the item is too short for the arrows. A pop-up menu draws its title
  in the title width (the maximum, clamped to the item), then, when at least 24 pixels remain, a button with a black
  triangle [ClassicMac: not captured]. Other controls, and control items whose `'CNTL'` is missing, are a grey frame;
  an invisible control draws nothing. [ClassicMac]
- **Text.** Charcoal is an Apple font: ClassicMac draws text from the `FOND`/`NFNT`/`FONT` resources of the user's open
  files, Charcoal by name when one has it, else family 0 (Chicago), and through the viewer's `ITextFallback` (an
  installed font close to it) when no bitmap font draws the text. Layout uses Charcoal's metrics and the widths of
  whatever draws the text (7 pixels a character with neither), so wrapping and the title's gap follow the font used.
  Bitmap text and the fallback are 1-bit, not smoothed. [ClassicMac]
- **A lone `'DITL'`** is drawn as a `plainDBox` from (0, 0) to 10 pixels past its items' right and bottom edges
  (100 × 40 with no items). [ClassicMac]

### 5.4 Writing

`InterfaceWriter` writes the templates by §3. It refuses (`ArgumentException`) text Mac OS Roman cannot hold and a
string over 255 bytes. A template read and written again keeps its positioning word, or lack of one. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ui.short` | Warning | The data ends before the fields do: a template's fixed part or title; a colour table under 8 bytes or with fewer entries than its count; a `'dlgx'` under 6 bytes; an `'alrx'` under 12 bytes | Reads as far as the data goes, zeros for the missing fields; the JSON holds what was read (reported once per resource) | Reads past the end of the resource [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs` with `GoldenTests` (outputs in `Golden/`): `WIND` 128
  (with the word $280A), `DLOG` 128 (without one), `ALRT` 128 (stages $7654, word $300A), `wctb` 128, the 8-byte
  `dctb` 128, `actb` 128, `cctb` 128, `dlgx` 128, `alrx` 128 (version 1) and 129 (version 0).
- `tests/ClassicMac.Resources.Decoders.Tests/DialogRendererTests.cs`: a hand-built fork (`DialogRendererTests.Fork`)
  with `DLOG` 128 (`dBoxProc`, every item kind), 129 (`documentProc`, a picture, a scroll bar and a check box
  `'CNTL'`), 130 (`movableDBoxProc` with a `'dctb'`), 131 (with a `'dlgx'`), 132 (`plainDBox`), 133
  (`altDBoxProc`), and `ALRT` 200 (stages $4444) and 201 (stages $5555, with an `'actb'`). Tests check the frames,
  margins and shadow, the alert tint, theme background and default ring, the alert icon and its placeholder, the
  title bars, the items' pixels, the scroll box's place, the 8-bit screen and the text's baselines through a fallback.
- `DialogRendererTests.Matches_the_captures_outside_text` compares the drawings with screen captures of that fork on
  Mac OS 9.0 (Appearance 1.1.1, Platinum, system font Charcoal 12) in SheepShaver: `DLOG` 128–133 and `ALRT` 200 as
  `Alert`, `StopAlert`, `NoteAlert` and `CautionAlert`, `ALRT` 201 as `Alert` and `NoteAlert`, at 32 and 8 bits. The
  captures are not in the repository; the test runs when `CLASSICMAC_DIALOG_CAPTURES` names their folder. It requires
  the same size and at least 99 % of the pixels outside text and outside the System's alert icons to match; outside
  text the drawings match pixel for pixel at both depths. These captures are the source of every [Verified] rule of
  §2.4.
- `tests/ClassicMac.Resources.Decoders.Tests/InterfaceWriterTests.cs`: a `'DLOG'` and an `'ALRT'` round trip; the
  `'DLOG'`'s positioning word is word-aligned.
- `tests/ClassicMac.App.Tests/InterfacePreviewTests.cs` and `WindowTests.Dialogs_and_menus_draw`: a disk image with a
  `'DLOG'` and grey `'dctb'`, an `'ALRT'` on the same list (stages $555D, default item 2) and a lone `'DITL'`, previewed
  in the app: the drawing's model, the movable-modal margins, the lone list's size and the view's size with gutters.
- Read back and written again, every `'WIND'`, `'ALRT'` and `'CNTL'` of ClassicMac's corpus comes out byte for byte,
  and every `'MENU'` but one; `'DLOG'` and `'DITL'` differ only in their filler and pad bytes [Verified: ClassicMac's
  corpus, not in the repository].

## 8. Not covered

- `'dftb'` (Appearance's dialog font table) and `'hdlg'`/`'hrct'` (the Help Manager's).
- In the previews: `'ictb'` item colours and fonts; `'dftb'`; Appearance themes other than Platinum, accent colours
  other than the default, inactive windows and highlighted controls; the zoom box, rounded document windows and
  `'WDEF'`s of the application's own; disabled (dimmed) controls; the 68k ROM's classic frames.
- Writing these resources from JSON (with `pack`).
- Which Appearance version Mac OS 9.0's captures ran: 1.1.1 as recorded here, against the 1.1.4 the
  [README](../README.md#reference-builds) lists for the reference build.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992): the Window, Dialog and Control Managers and their
   resources.
2. Apple, MPW Rez `Types.r`: field order, alignment and the named constants.
3. Mac OS 9.0's System file, disassembly: MacDialogsLib, WindowsLib and WindowDefinitions, ControlsLib and
   ControlDefinitions, AppearanceLib, and the stub `'WDEF'`/`'CDEF'` resources ([README.md](../README.md#reference-builds)).
4. The 68k ROM (`$077D`), disassembly: the Window and Dialog Managers.
