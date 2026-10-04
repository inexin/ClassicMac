# Volume tools

Items: V1–V6. Answers [../volume-tools/BRIEF.md](../volume-tools/BRIEF.md). Canvas frames: "Volume tools · light", "· dark" and "· dialogs at 150%" (row 10). Built on the dialog frame of [dialogs.md](dialogs.md) and the Details cards of [details.md](details.md). Every command stays a session edit: the title gets "•", and nothing is written to the image until Save As ▸ HFS Volume Image.

## 1 · Volume menu and context menu (V1)

**Volume menu:** two groups split by a divider, no submenu (three maintenance items are few enough to show):

```
New File…
New Folder…              Ctrl+Shift+N
Import File…
────────────
Delete File or Folder…   Del
────────────
First Aid…
Defragment…
Resize…
```

- Defragment gets "…": it now opens a dialog (section 4).
- Items that do not apply stay visible but disabled, with the reason as a tooltip: HFS Plus → "Only First Aid works on HFS Plus volumes"; partition → "A partition can't be resized: its map would change"; read-only input → "This volume can't be changed".
- **Context menu:** First Aid…, Defragment…, Resize… appear only on the volume's own node (the input, or a disk image's volume node), above the file items. On files and folders the context menu keeps only the file items (New File, New Folder, Import, Delete). This changes today's behaviour, where every item of the volume offers the maintenance commands.

## 2 · Long operations: progress and cancel (V2)

- First Aid, Defragment and Resize run in their own dialog, which switches to a **progress state**: a bold step line ("Checking…", "Defragmenting…", "Resizing…"), a 6 px determinate bar (CmSegmentTrack, fill CmAccent, `role=progressbar` with value), a muted detail ("Step 3 of 5 · Checking the catalog B-tree" or "File 12 of 21"), and a single **Cancel** button in the footer.
- Cancel is always offered: the work runs on a copy of the volume in memory, so cancelling drops the copy and leaves the session's volume as it was. The muted line says so ("Cancel stops the check; nothing has changed yet").
- The status bar progress bar (S4) mirrors it, with the operation's name, so the user can see it after moving the dialog.
- The dialog stays modal to the window; the app stays responsive (work off the UI thread).
- No sheet: the dialog frame is the same one used for the result, so the window does not jump.

## 3 · Defragment (V5)

Explains itself first, then shows before and after, in one 420 px dialog.

- **Before:** one sentence ("Moves every file into one piece and gathers the free space into one run at the end. Files and their contents don't change; only where they lie."), the allocation map (see 6) titled "Now", and a three-column figure table, label column 120, columns **Now** and **After**:
  - Split files: "1 of 21" → "None"
  - Free space: "4 runs · largest 2 blocks" → "1 run · 8 blocks"
  - Can shrink to (mono): current smallest → smallest after
  
  After values are predicted (they follow from the layout) and bold. Muted note: "Changes stay in this session until you Save As." Buttons Cancel, **Defragment** (primary).
- **Running:** the progress state of section 2.
- **After:** the same dialog, lead "Done. Every file is in one piece and the free space is one run.", map titled "After", the table now measured, a muted line with the time taken and "Save As ▸ HFS Volume Image writes the result.", one button **Done**.
- When there is nothing to gain (no split file, one free run) the menu item still opens the dialog, with the lead "This volume is already in order." and only Done.
- Opened from Resize ("Defragment first…"), Done returns to the Resize dialog with the new smallest size and the typed value kept.

## 4 · Resize (V4)

420 px dialog, title "Resize “<volume>”".

- **New size:** a text box (mono 14) plus a unit select (KB, MB, GB, bytes). Typing a suffix in the box ("800K", "20M", "1.4 MB") moves it into the unit select. Decimal input is allowed for MB and GB.
- **Slider** under the field; the field and the slider follow each other both ways.
  - Above it, one muted line: "Smallest 403K", "Now 800K" (centred), "Largest 2G" (sizes in mono).
  - Track 6 px (CmSegmentTrack), logarithmic from the smallest to the largest size. An 18 px round thumb (CmPaneBackground, 2 px ring in CmAccent; CmWarning or CmError when the size has that note). A 2 px CmText tick marks the current size; the stretch between the current size and the thumb is tinted in the thumb's colour at 45%.
  - Snap points: the current size and the classic disk sizes, each a 5 px tick with a mono label under it: 1.4M, 20M, 100M (tooltips: 1.4 MB floppy disk, 20 MB hard disk, 100 MB Zip disk). Sizes outside the range are left out; labels that would overlap the next are dropped. The 400K and 800K floppies are snap points without a label when the range is small.
  - Between the smallest size and what the free space allows now, the track is striped in CmWarning: the sizes that need Defragment first.
  - Dragging snaps to whole 1K below 1 MB and 0.1 MB above; Shift drags freely. Arrow keys step 64K below 1 MB and 1 MB above, Page Up/Down step to the next snap point, Home/End go to smallest/largest. `role=slider` with `aria-valuetext` ("500 KB, needs Defragment").
  - These replace the earlier preset chips.
- **Block size:** a select under the slider, "Automatic · 512 bytes" by default: the smallest multiple of 512 that keeps the volume within 65,535 blocks (512 up to 32 MB, 1,024 up to 64 MB, and so on). The list offers only valid larger sizes (multiples of 512, up to 4,096 shown, more on scroll), for matching an original disk. Choosing a size other than the current one gets the info note "Changing the block size lays every file out again, as Defragment does." Hidden if the writer cannot take a block size (then Automatic is the only choice and is shown as text).
- **Inline note** under the field, updated as you type (no wait for Resize), a tinted box with the severity colour on its inner 1 px ring:
  - Not a size → error: "Type a size, such as 800K or 20M."
  - Too large → error: "Too large: the largest HFS volume here is 2 GB."
  - Below the smallest → error: "Too small: the files need at least 403K."
  - Between the smallest and what the free space allows now → warning: "The free space lies in 3 runs, so this volume can shrink only to 640K now. Defragment first to reach 403K." with a **Defragment first…** button in the note.
  - Growing past 65,535 blocks at the current block size → info: "Above 32 MB the blocks must grow to 1,024 bytes, so every file is laid out again, as Defragment does." (Name the threshold and block size actually chosen.)
- The text box gets `aria-invalid` and a CmError border plus CmErrorTint ring while invalid. **Resize** is disabled while the size is an error or warning; Esc and Cancel close.
- Running: the progress state of section 2.

## 5 · First Aid (V3)

360 px dialog, title "First Aid · “<volume>”". No separate volume line: the title names it.

- **Verdict banner** first, icon + colour + text (never colour alone; icons as in TOKENS.md severity):

  | Verdict | Banner | Icon | Headline | Sentence |
  | --- | --- | --- | --- | --- |
  | Appears to be OK | CmSuccessTint | filled circle, check | "Appears to be OK" | "First Aid found no problems." |
  | Needs repair | CmWarningTint | filled triangle, ! | "Needs repair" | "First Aid found N problems it can repair." |
  | Can't repair | CmErrorTint | filled circle, × | "Can't repair" | "Copy the files you need with Extract All, then make a new volume." |
  | Repaired | CmSuccessTint | filled circle, check | "Repaired · the volume appears to be OK" | "Changes stay in this session until you Save As." |

  The OK and Repaired states use **CmSuccess** and **CmSuccessTint** (added to TOKENS.md and Tokens.axaml). Banner padding 10 × 12, CmRadius, `role=status`.
- **Log:** one mono list (CmFontSizeMonoSmall, line height 1.7, CmSidebarBackground, CmCardBorder, max 160 px high and scrolling), with muted section headings in the order things happened: "Problems found", then after Repair "Repaired" and "Checked again". Disk First Aid's lines stay verbatim.
- **Footer:** **Copy report** on the left (copies the volume name, the verdict and every log line as plain text); on the right Done, plus Repair (primary) when it can repair, or Extract All… when it can't.
- Running: the progress state of section 2.

## 6 · Volume card (V6)

On the Details tab of the input or a disk image's volume node.

- **Header** (caption on CmSidebarBackground): "VOLUME", then link buttons on the right: **Defragment…** (only when there is something to gain: a split file or more than one free run) and **First Aid…**.
- **Used bar first:** a line "**2.0 MB** HFS volume · 512-byte blocks" with "4 KB free" right-aligned in mono muted, then an 8 px bar (CmSegmentTrack track, CmAccent used), `aria-label` "99.8% used". Same look as the fork bars on the Forks card.
- **Allocation map:** muted line "Where the free space lies" with "4 runs · largest 2 blocks" right-aligned, then one strip 14 px high (CmRadiusSmall, 1 px CmBorder inset, CmSegmentTrack behind):
  - **Sized to the width, not a fixed count:** one segment per 2 DIP of the strip's width (about 190 on the card, about 190 in the Defragment dialog), each covering an equal share of the volume's blocks. A volume with fewer blocks than segments gets one block per segment. Redraw on resize.
  - **Small things win** (so scattered free space looks scattered): a segment with any block of a file in more than one piece is **Split file** (CmWarning); otherwise one with any free block is **Partly free** (CmMapPartial) or **Free** (CmSegmentTrack) when all of it is free; otherwise **Used** (CmAccent).
  - **Hover** a segment for its block range and contents: "Blocks 1,024–1,045: 2 free, the rest used". The strip's accessible name gives the summary ("4,090 blocks; 1 file in pieces; free space in 4 runs of 2 blocks").
  - Legend below: Used, Split file, Partly free, Free. The same map is used in the Defragment dialog.
  - Draw it as one custom control (rectangles per segment, snapped to device pixels), not one element per segment.
- **Rows:** label column widened to **112 px** and labels shortened, so nothing wraps at 13 px:

  | Today | New label | Value |
  | --- | --- | --- |
  | Size | Size | mono "2,094,080 bytes · 4,090 blocks" |
  | Free | Free | mono "4,096 bytes · 8 blocks" |
  | Files / folders | Files, folders | "21 files · 0 folders" |
  | Fragmented files | Split files | "1 of 21 · up to 17 pieces" (the tail muted) |
  | Free space | (moved to the map line) | |
  | Format, Block size | (moved to the bar line) | |
  | Created, Modified, Backed up | same | mono dates; "Never" muted |

## Accessibility and keyboard

- Every dialog: Enter = primary, Esc = Cancel/Done, focus starts in the size box (Resize) or on the primary button.
- Progress bar and verdict banner are announced (`progressbar`, `status`).
- Map and bar carry text equivalents; colour never carries meaning alone (the figures and legend repeat it).

## View-model needs

- A layout summary from the HFS writer: the free runs and the extents of files in more than one piece (block ranges, not cells: the view buckets them for its width), the largest free run, split file count and worst extent count, smallest size now and after defragmenting. One call feeds the card, Defragment and Resize.
- Progress reporting (`IProgress<VolumeProgress>` with step, count, text) and `CancellationToken` through First Aid, Defragment and Resize.
