# ClassicMac UI: backlog plan

Exported 2026-10-02 from the plan doc. Design specs live next to this file: [TOKENS.md](TOKENS.md), [Tokens.axaml](Tokens.axaml) and [boards/](boards/). The "Spec" column names the file each item is checked against.

The redesign ships in six milestones, M0 to M5: a token foundation first, then quick wins in the tree and diagnostics, then the shell, the read-then-edit forms, the previews and the volume tools. That is 43 backlog items in six epics, each item one or a few commits on `main` in `ClassicMac.App` (plus the audio player for A1).

## Purpose and scope

This plan turns the design canvas into work that can be picked up one item at a time. It covers the neutral frame (light and dark), the token sheet, and every board on the canvas.

Out of scope: the Platinum homage (dropped: the folder preview already shows the real Platinum window), new decoders or format support, and the libraries under `src/` other than the app. Sizes are first guesses (S under a day, M two to three days, L about a week) to be corrected at refinement.

## Principles

- **Tokens before pixels.** No visual item starts before M0 lands; every colour, size and radius comes from `Styles/Tokens.axaml`, never a literal in a view.
- **Quick wins early.** M1 changes what users feel most (a shorter tree, readable diagnostics) and needs only M0.
- **One item, one or a few commits on `main`.** The project works on `main` with no feature branches (CLAUDE.md); each item's commits leave the app shippable.
- **View-models first.** Behaviour lives in the plain C# view-models and is unit tested; the views stay thin, as today.
- **Screenshots are the acceptance test.** Every visual item adds or updates a headless render in light, dark and 150% scaling, compared against the spec it names.
- **Same pattern everywhere.** Every editable thing opens read only and switches to editing in place, with Cancel and Apply ([boards/read-then-edit.md](boards/read-then-edit.md)).

**Definition of done:** builds on Windows, macOS and Linux CI; unit tests for new view-model behaviour; headless screenshots updated; keyboard reachable with accessible names; CHANGELOG line under Viewer.

## Milestones

```
M0 Foundation ──gate──┬──> M1 Quick wins (tree, diagnostics)
                      └──> M2 Shell ──┬──> M3 Read, then edit
                                      ├──> M4 Previews, dialogs
                                      └──> M5 Volume tools (after S4, P5)
```

Gate after M0: screenshot baselines pass in light, dark and 150%. M1 and M2 can then run side by side; M3 and M4 build on the new inspector header from M2 (S3).

## Epics and items

"Done when" is the acceptance criterion; "Spec" is the file it is checked against.

### M0 · Foundation (6 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| F1 | Theme dictionaries for every colour token, light and dark; `SystemAccentColor` set | Changing the OS theme flips every surface; no hex literal left in `MainWindow.axaml` | M | [TOKENS.md](TOKENS.md), [Tokens.axaml](Tokens.axaml) |
| F2 | Bundle IBM Plex Sans and Plex Mono; `CmFontSans`, `CmFontMono` | Same fonts on all three OSes; the Cascadia/Consolas chain is gone | S | [TOKENS.md](TOKENS.md) |
| F3 | `Styles/ClassicMac.axaml` with shared classes and Fluent selection overrides | Tree, lists and tabs use CmSelection tokens; views use classes, not local setters | M | [TOKENS.md](TOKENS.md) |
| F4 | Replace the hard-coded colours: hex cursor, form error, template note, checkerboard, waveform | All come from tokens and redraw when the theme changes | S | [TOKENS.md](TOKENS.md) |
| F5 | Pixel scaling helper: k = max(1, floor(zoom × scaling)), snapped origin, no interpolation | A 1 px checkerboard stays 1:1 at 100%, 125%, 150% and 200% | M | [TOKENS.md](TOKENS.md) |
| F6 | Headless screenshot baselines for light, dark and 150% | First confirm Avalonia 12's headless platform can render at 150% scaling. Then CI compares frames against stored baselines (today frames are captured but not compared) and fails on an unexpected change | L | all |

### M1 · Tree and diagnostics (9 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| T1 | Hide **files** with the invisible flag; footer "N invisible items hidden · Show" | `Icon\r` and the desktop database files are gone by default and come back from the footer; invisible folders (`Desktop Folder`, `Trash`) still show | S | [boards/tree-no-name.md](boards/tree-no-name.md) |
| T2 | "No name" group: 2 or more files with empty or whitespace names per folder fold into one collapsed node | The Realmz folder shows one row instead of 15; search and type-ahead skip grouped files | M | [boards/tree-no-name.md](boards/tree-no-name.md) |
| T3 | Tree display options: "Group files with no name", "Hide invisible files" | Both on by default; choice persists between sessions | S | [boards/tree-no-name.md](boards/tree-no-name.md) |
| T4 | 16 px pixel icons per node kind; icon resources show their own icon; files show their Finder icon via `FinderIconResolver` | Every node has an icon; drawn 1:1, never at 1.25 or 1.5 | L | [boards/browse-tree.md](boards/browse-tree.md) |
| T5 | Unsaved " •", loading spinner ("Loading…", no counts) and "not read" chip styled | States match the spec in light and dark | S |
| T6 | Unsaved " •" on each edited resource, not only its file | The mark clears on Save, or when undo returns the resource to its saved state | S | [boards/browse-tree.md](boards/browse-tree.md) | [boards/browse-tree.md](boards/browse-tree.md) |
| D1 | Diagnostics: severity icons and colours, column headers | Error, warning and info differ by icon and colour, not colour alone | S | [boards/diagnostics.md](boards/diagnostics.md) |
| D2 | Group diagnostics by file; info-only groups start collapsed | Groups show their counts; clicking a row still selects its node | M | [boards/diagnostics.md](boards/diagnostics.md) |
| D3 | Collapsible panel with count badges and Ctrl+Shift+D | Collapsed bar shows the counts and the latest problem | S | [boards/diagnostics.md](boards/diagnostics.md) |

### M2 · Shell (7 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| S1 | Custom title bar on Windows and macOS; system title bar on Linux | Caption buttons work; Linux falls back cleanly | M | [boards/main-window.md](boards/main-window.md) |
| S2 | Toolbar: Open, Save, Get Info, Edit Hex, Export, Extract All, Play, Zoom, Depth | Each button follows its command's enabled state | M | [boards/main-window.md](boards/main-window.md) |
| S3 | Inspector header (icon, name, kind, facts, actions); tabs Details and Preview, Hex only when shown today | Header updates with the selection; the Edit tab is gone; Hex appears only for resources without a preview or while editing bytes | M | [boards/main-window.md](boards/main-window.md) |
| S4 | Status bar with counts and a progress bar | Long exports show progress and can be read at a glance | S | [boards/main-window.md](boards/main-window.md) |
| S5 | Empty state with drop zone and recent files | Recent list persists, opens on click and can be cleared | M | [boards/empty-state.md](boards/empty-state.md) |
| S6 | Tree filter (Ctrl+F) and type-ahead pill | Typing jumps between matches among loaded nodes; Esc clears | M | [boards/browse-tree.md](boards/browse-tree.md) |
| S7 | View, Window and Help menus and an About box | Every new item works; About shows version, licence and notices | M | [boards/main-window.md](boards/main-window.md) |

### M3 · Read, then edit (8 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| E1 | Shared read-then-edit host: Edit button, Editing badge, Cancel and Apply footer, Esc and Ctrl+Enter, double-click a row to edit | One control used by every form; Apply makes one undoable edit as today | L | [boards/read-then-edit.md](boards/read-then-edit.md) |
| E2 | Menu: read-only table and live preview, duplicate ⌘ key error, all seven style bits | Selecting a row highlights the item in the preview and back | M | [boards/read-then-edit.md](boards/read-then-edit.md) |
| E3 | Dialog item list: rows and preview select each other; live text and bounds | Changing bounds moves the item in the preview on each keystroke | L | [boards/dialog-item-list.md](boards/dialog-item-list.md) |
| E4 | Window, alert and control forms as property cards that switch to inputs | Alert stages preview their default button (item 1 or 2 only); window kinds and positions are named, not numbers | M | [boards/window-alert.md](boards/window-alert.md) |
| E5 | Strings, string lists, text and version forms moved to the pattern | All open read only first | S | [boards/read-then-edit.md](boards/read-then-edit.md) |
| E6 | Template form: nested list cards, read-only counts, note banner, template panel | Add, insert and remove keep counts in step | M | [boards/template-form.md](boards/template-form.md) |
| E7 | Hex editing in place: Edit Hex (Ctrl+H) opens the Hex tab in editing; changed-byte tint, overwrite and insert switch, numeric byte inspector, Go to | The hex dialog is retired; Ctrl+H keeps working; Find is a follow-up | L | [boards/hex.md](boards/hex.md) |
| E8 | Byte meanings in the hex inspector ("Character 1 of string 1") from a per-type field map | Works for `STR `, `STR#` and template-backed types; other types show only the numeric view | L | [boards/hex.md](boards/hex.md) |

### M4 · Previews and dialogs (7 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| P1 | Image grid cards with captions and the Finder states strip | Hundreds of items stay smooth to scroll | M | [boards/main-window.md](boards/main-window.md) |
| P2 | Property view instead of JSON for structured resources, with a JSON toggle | `vers`, `WIND` and friends read as labelled values | M | [boards/property-view.md](boards/property-view.md) |
| P3 | Sound preview: play button, detail chips, channel lanes, loop region, error state; playhead and seek after A1 | Undecodable sounds offer Hex and Save raw data | M | [boards/sound.md](boards/sound.md) |
| P4 | Details tab in groups, Finder flag chips, "how it was read" chain, Copy all | Copy all puts plain text on the clipboard | M | [boards/details.md](boards/details.md) |
| P5 | Dialogs restyled: Get Info, Import Image or Sound, New File, New Folder, Unsaved changes | Same header, footer and button order in all of them | M | [boards/dialogs.md](boards/dialogs.md) |
| A1 | `IAudioPlayer`: playback position, seek and loop repeat (SoundFlow) | The sound preview's playhead follows playback, a click seeks, Repeat the loop loops | M | [boards/sound.md](boards/sound.md) |
| P6 | Font family (`FOND`) preview: sample from the strikes, association matrix, metrics, style and kerning cards | Every FOND in the System file shows a sample when a strike exists; association cells select their NFNT/sfnt | M | [boards/font-family.md](boards/font-family.md) |

### M5 · Volume tools (6 items)

| ID | Item | Done when | Size | Spec |
| --- | --- | --- | --- | --- |
| V1 | Volume menu in two groups; maintenance on the volume node's context menu only; disabled items say why | HFS Plus and partitions show the disabled items with their reason; files and folders no longer offer First Aid, Defragment or Resize | S | [boards/volume-tools.md](boards/volume-tools.md) |
| V2 | Progress state and Cancel for First Aid, Defragment and Resize, mirrored in the status bar | A 2 GB volume shows steps while it works; Cancel leaves the session's volume unchanged; the UI stays responsive | M | [boards/volume-tools.md](boards/volume-tools.md) |
| V3 | First Aid window: verdict banner (icon, colour, text), one log with sections, Copy report, Extract All… when it can't repair; CmSuccess tokens | The four verdicts match the board in light and dark; Copy report puts plain text on the clipboard | M | [boards/volume-tools.md](boards/volume-tools.md) |
| V4 | Resize dialog: number and unit, draggable log slider with snap points, block size (Automatic or larger), inline errors and notes, Defragment first… | No size error reaches the status line; the 65,535-block and shrink notes appear as you type | M | [boards/volume-tools.md](boards/volume-tools.md) |
| V5 | Defragment dialog: explanation, allocation map, Now and After figures, result state; returns to Resize | Opened from Resize, Done comes back with the new smallest size | M | [boards/volume-tools.md](boards/volume-tools.md) |
| V6 | Volume card: used bar, allocation map, 112 px labels with shorter names, Defragment… and First Aid… in the header | "Split files" and every label fit on one line at 100% and 150% | M | [boards/volume-tools.md](boards/volume-tools.md) |

## Risks

| Risk | Effect | Mitigation |
| --- | --- | --- |
| Fluent resource key names differ in Avalonia 12 | Selection overrides (F3) silently do nothing | Check the keys against Avalonia 12's FluentTheme source first; cover with a screenshot case |
| Fractional scaling at 125% and 150% | Pixel art and 1 px lines blur | F5 helper plus a 150% screenshot case before any preview work |
| Custom title bar on Linux | Broken window chrome on some desktops | System title bar fallback is part of S1, not a follow-up |
| macOS global menu bar | Menu styling does not apply there | Accept it; style only the in-window menu |
| Grouping hides files from search (T2) | Users can't find a no-name file | The group itself matches "no name"; the display option turns grouping off |
| E1 touches every form | One large, risky change | Land the host with the menu form (E2), then move the others one per item |
| Headless rendering at 150% may not be possible (F6) | No automated check of fractional scaling | Spike it first; fall back to a manual check list at 125% and 150% |

## Decisions

- Platinum theme: dropped. The folder preview already shows the real Platinum window.
- Resources get their own unsaved " •" (T6).
- Find in hex: a follow-up after E7.
- "No name" group: no art thumbnail; selecting the group shows the parent folder's preview.
- Volume tools: maintenance is a menu group, not a submenu; every long operation can be cancelled; Defragment explains itself first; Resize reports errors in the dialog ([boards/volume-tools.md](boards/volume-tools.md)).

## Open questions

- [ ] When the selection changes with unapplied edits: ask or apply? Hex applies today. Decide once for every form.
- [ ] Should folders also show their own custom icons in the tree once T4 lands, like files?
