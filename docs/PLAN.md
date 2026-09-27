# ClassicMac — Project Plan

As of 2026-09-27.

## Purpose and goals

A .NET library, command-line tool and desktop app that gets the resources out of classic Mac OS files — whatever
they are wrapped in — and turns them into modern files with a manifest, and later edits and writes them back.

- **Reach the fork.** Read resource forks from raw forks, AppleDouble/AppleSingle, MacBinary, BinHex and, later, HFS
  disk images and old archives.
- **Decode, faithfully.** Convert each resource type to a modern format the way the Mac would have shown it: images
  through QuickDraw.Pict, sound to WAV, text to UTF-8, fonts, and UI resources to JSON.
- **Stay dependency-free at the core**, like QuickDraw.Pict, so other tools can embed it.
- **Stand alone.** A general tool for anyone working with classic Mac files; applications with their own formats
  extend it through custom decoders.

**Name and repository (decided):** the project is **ClassicMac**, in its own GitHub repo `inexin/ClassicMac` holding
the libraries, the CLI and the viewer/editor app. Packages: `ClassicMac.Resources`, `ClassicMac.Resources.Decoders`,
`ClassicMac.Resources.Cli`; the app carries the same name. QuickDraw.Pict stays a separate repo and package for now;
merging it into ClassicMac (as `ClassicMac.QuickDraw`) is the long-term intent.

## Inputs

Resource forks rarely survive on modern disks, so most of the value is in unwrapping the containers they travel in.
Proposed priority:

| Priority | Container | Typical source |
| --- | --- | --- |
| 1 | Raw resource fork (`.rsrc`, `file/..namedfork/rsrc` on macOS) | Extracted forks, macOS copies |
| 1 | AppleDouble (`._file`, `__MACOSX/` in zips) and AppleSingle | Files copied to FAT/SMB, zip archives |
| 1 | MacBinary I/II/III (`.bin`) | Downloads, archive sites |
| 1 | BinHex 4.0 (`.hqx`) | Usenet, old download sites |
| 2 | HFS disk images: raw `.dsk`/`.img`, DiskCopy 4.2, NDIF | Emulator disks, floppy images, CD-ROMs |
| 3 | HFS+ images | Mac OS 8.1–9 disks |
| 3 | StuffIt (`.sit`) and Compact Pro (`.cpt`) archives | Most classic Mac downloads |

Containers can nest (a `.hqx` holding a `.sit` holding a disk image), so input detection should recurse.

## Core: the resource map

The core reads a resource fork into an in-memory model and writes one back.

- **Model:** file → types → resources; each resource has type, ID, name, attributes (system heap, purgeable, locked,
  protected, preload, compressed) and its data.
- **Compressed resources:** System 7's `dcmp` 0, 1 and 2 decompressed natively. Unknown `dcmp` IDs are kept
  compressed and flagged in the manifest.
- **Tolerant reading:** truncated or overlapping entries are reported, not fatal.
- **Writing:** rebuild a fork from the model, for modding and round-trip tests.
- **Text encodings:** MacRoman by default; the script of a file or of a font picks other Mac encodings (Japanese,
  Cyrillic, …).

## Decoders

Each decoder turns one resource type into a modern file; anything without a decoder is exported raw.

| Group | Resource types | Output |
| --- | --- | --- |
| Images | `PICT`, `ICON`, `ICN#`, `ics#`, `icl4/8`, `ics4/8`, `cicn`, `CURS`, `crsr`, `PAT `, `PAT#`, `ppat`, `SICN` | PNG, via QuickDraw.Pict (screen depth selectable) |
| Sound | `snd ` (sampled formats 1/2; MACE 3:1/6:1, IMA4, µ-law) | WAV |
| Text | `STR `, `STR#`, `TEXT` + `styl`, `vers` | UTF-8 text / JSON; styled text as RTF or Markdown |
| Fonts | `sfnt`; `NFNT`/`FONT` + `FOND` | TTF; BDF or a PNG strike + metrics JSON |
| UI | `MENU`, `MBAR`, `DLOG`, `DITL`, `ALRT`, `WIND`, `CNTL` | JSON, optionally a rendered preview of the dialog |
| Colour | `clut`, `pltt` | JSON and `.act` palettes |
| Unknown | anything else | raw `.bin` + hex preview in the manifest |

App-specific types (a game's data records, an application's private resources) plug in as custom decoders
registered by the application.

## Output

One folder per input file, one subfolder per resource type, and a manifest that describes everything.

```
MyApp/
  manifest.json
  PICT/128 Title Screen.png
  snd /200 Door.wav
  STR#/1000 Messages.json
  CODE/1.bin
```

- **File names:** `<id> <name>.<ext>`, with characters illegal on Windows or macOS escaped; the type folder keeps its
  four characters (escaped when needed).
- **Manifest:** per resource: type, ID, name, attributes, original size, decoder used, output path, and any warnings.
- **Round trip:** a `pack` command rebuilds a resource fork from the folder and manifest, using raw data where a file
  was not changed.

## Architecture and packaging

Three packages and an app, layered so the core can be embedded anywhere and the heavy dependencies stay optional.

```mermaid
flowchart LR
    subgraph core["ClassicMac.Resources · dependency-free"]
        C["Containers<br/>raw fork, AppleDouble,<br/>MacBinary, BinHex,<br/>HFS images, archives"] --> M["Resource map<br/>types, IDs, names,<br/>attributes, dcmp,<br/>read and write"]
    end
    subgraph dec["ClassicMac.Resources.Decoders"]
        D["Decoders<br/>images to PNG,<br/>sound to WAV, text,<br/>fonts, UI to JSON"]
    end
    subgraph cli["CLI · viewer app"]
        E["Export and browse<br/>folders + manifest,<br/>pack back to a fork"]
    end
    M --> D --> E
    Q["QuickDraw.Pict<br/>PICT and icon drawing"] --> D
    A["App decoders<br/>an app's own formats"] --> D
```

- **ClassicMac.Resources** — containers, the resource map and `dcmp`; no dependencies, .NET 8.
- **ClassicMac.Resources.Decoders** — the built-in decoders; depends on QuickDraw.Pict for images.
- **CLI** — a `dotnet tool` with `list`, `extract` and `pack`.
- **Viewer app** — a cross-platform desktop app on the same packages (below).
- **Extension points:** an `IContainerReader` per container format and an `IResourceDecoder` per resource type, so
  apps add their own.

### Target layering after the QuickDraw.Pict merge

When QuickDraw.Pict moves into ClassicMac, it is split so every dependency points down: QuickTime and MacPaint stop
living inside PICT, and PICT calls QuickTime for its embedded images (`$8200`/`$8201`). Until the merge,
QuickDraw.Pict stays as it is.

| Layer | Contains | Depends on |
| --- | --- | --- |
| `ClassicMac.Graphics` (base) | The RGBA bitmap type, standard colour tables (`clut` 1–8, greys), PackBits, colour-table and PixMap reading | nothing |
| `ClassicMac.QuickTime` | ImageDescription, the codecs (`raw `, `rle `, `rpza`, `smc `, `cvid`, `8BPS`, `yuv2`, `YVU9`, `tga `), the codec plugin hook, QTIF files | Graphics |
| `ClassicMac.MacPaint` (or inside Graphics) | PNTG files and the `PNTG` codec's decoder | Graphics |
| `ClassicMac.QuickDraw` | PICT reading and writing, the drawing engine, regions, text, screen depths | Graphics, QuickTime |
| `ClassicMac.Resources` (+ `.Decoders`) | Resource forks and containers; icons, cursors and patterns become resource decoders here | Graphics, QuickDraw |
| ImageSharp plugins | One thin adapter per format | the layers above |

Colour tables and PixMaps sit in the base because QuickTime's codecs and QuickDraw both need them. At the merge,
`QuickDraw.Pict` and `QuickDraw.Pict.ImageSharp` are deprecated on NuGet, pointing to the new packages.

**Where icons live (settled by this layering):** icons, cursors and patterns are resources, so their decoders go in
`ClassicMac.Resources.Decoders`. Plain decoding (`ICN#`, `icl8`, `cicn`, …) needs only Graphics (colour tables,
PixMaps, masks); QuickDraw is needed only to scale them or draw them at a screen depth. The Icon Utilities rules that
QuickDraw.Pict's spec describes but never implemented move here too — suite member choice by size and depth, the
selected/disabled/label transforms, CalcMask, the `cicn` bitmap-or-pixmap rule — as does `icns` (Mac OS X icons).

### Viewer app

A desktop app for Windows, Linux and macOS to browse disk images, archives and files, and open their resource forks —
like ResEdit, read-only at first.

- **Framework:** [Avalonia](https://avaloniaui.net) — decided. The libraries are .NET, so the UI calls them directly
  with no bridge; one codebase ships as a self-contained download per OS (about 30–60 MB); it draws with Skia, so
  previews render pixel-identical everywhere. Rejected: Electron/Tauri (a web UI only pays off for a browser version,
  which the libraries could later reach through WebAssembly on their own), .NET MAUI (no Linux desktop), and a native
  UI per platform (three UIs).
- **Browse:** open a disk image, archive or file; a tree of volumes → folders → files, showing type/creator, dates and
  both forks; nested containers open in place.
- **Resources:** a file's resource fork as types → resources, with ID, name, size and attributes; search by type, ID
  or name.
- **Previews:** images (with a screen-depth switch), text, sound playback, font glyph sheet, dialogs drawn from
  `DLOG`/`DITL`, and a hex view for everything.
- **Export:** selected resources, a whole file or a whole volume, through the same code as the CLI; drag and drop out
  of the app.
- **Later:** editing, in stages (below), starting once `pack` round-trips cleanly.

**Structure:** the app adds nothing format-specific — it sits on the same `ClassicMac.Resources` and `Decoders`
packages as the CLI. MVVM with plain C# view-models (testable without a UI), one preview control per kind (image, hex,
text, sound, font, dialog) chosen by resource type, lazy opening and decoding so large disk images browse instantly;
sound playback is the only native dependency.

**First version:** read-only — open a file or disk image, browse the tree, preview images and hex, export. Other
previews arrive with their decoders.

**Editing, in three stages** — the viewer becomes an editor, ResEdit-style. Every save writes a verified round trip
(read back and compared) and keeps the original; undo within a session.

1. **Resource-level edits:** add, delete, duplicate, renumber and rename resources, change attributes, replace a
   resource's data from a file or the hex editor; save back into the same container (raw fork,
   AppleDouble/AppleSingle, MacBinary, BinHex).
2. **Typed editors:** forms for `STR `, `STR#`, `TEXT`/`styl`, `vers` and the UI templates (`DLOG`, `DITL`, `MENU`,
   …) with a live dialog preview; import PNG as PICT, icons and cursors, and WAV as `snd ` — which needs encoders in
   the libraries.
3. **Writing disk images:** add, replace and delete files in HFS images, with type/creator, dates and both forks.
   Archives (StuffIt, Compact Pro) stay read-only.

## Ground truth and licensing

Same approach as QuickDraw.Pict: Apple's documentation is the spec, other implementations are behavioural references,
and results are checked against real data.

- **Specs:** *Inside Macintosh: More Macintosh Toolbox* (resource format), *Inside Macintosh: Files* (HFS), *Sound*
  (`snd `), the Apple file-format notes for MacBinary, AppleSingle/AppleDouble and BinHex 4.0.
- **Behavioural references, not code to copy:** resource_dasm (MIT) and other open tools for container and `dcmp`
  edge cases.
- **Verification:** a corpus of real files (system files, applications, games such as Realmz, shareware) extracted and compared;
  round-trip tests (read → write → read gives the same map); golden outputs for each decoder.
- **Licence:** MIT, with third-party notices for anything ported; no Apple code or files in the repo.

**When unsure, go to the source.** Apple's documentation decides first; where it is silent or ambiguous, the answer
comes from disassembling the Mac OS code that handles the format (the Resource Manager, Sound Manager, Icon Utilities,
HFS) — never from another implementation's guess.

### Prior art: Realmz.ResourceExtractor

The resource extractor in the Realmz project (a separate game reimplementation) is inspiration, not an authority: its
rules were written for Realmz's files and must be re-checked against Apple's documentation or the disassembly before
they are carried over. ClassicMac does not depend on Realmz; Realmz's data files are one of the test corpora, and the
Realmz project can serve as a real-world consumer to try the libraries against.

| Area | What it does today | Take from it |
| --- | --- | --- |
| Input | Raw resource-fork files (`.rsf`) only | The resource map reader, as a first draft |
| Resource map | Types, IDs, data; names not read; no `dcmp` | Needs names, attributes and compression |
| Images | PICT, `cicn`, icon lists and `icl`/`ics` with their masks, cursors with hotspots, `ppat` via QuickDraw.Pict | The icon-list pairing and the hotspot JSON |
| Sound | `snd ` formats 1/2, uncompressed PCM only (MACE and IMA4 throw) | The WAV writer; codecs still to do |
| Text | `TEXT` and `STR#` (JSON, 0-based) in MacRoman | The `STR#` layout |
| Fonts | `sfnt` → TTF, adding a Windows Unicode `cmap` and a missing `OS/2` table so modern loaders accept it | A "make it loadable" option for exported fonts |
| UI | `DLOG`, `DITL`, `WIND`, `CNTL`, `MENU`, `MBAR` → JSON | The field layouts, re-checked against *Inside Macintosh* |
| Output | A folder per category (`pictures/`, `sounds/`, …), 5-digit IDs, no manifest | Replace with per-type folders and a manifest |

### Prior art: other projects

No existing project combines containers, faithful conversion, writing and a cross-platform GUI — but each covers a
slice worth learning from. Licences matter: MIT code may be reused with notice; GPL and LGPL code is reference only.

| Project | Language, licence | Covers | Use for us |
| --- | --- | --- | --- |
| [ResourceForkReader](https://github.com/hughbe/ResourceForkReader) | C#, MIT; NuGet, active since 2025 | Reads raw forks; typed parsers for ~130 resource types; no `dcmp`, containers, conversion or writing | Candidate to build on; cross-check record layouts |
| [HfsReader](https://github.com/hughbe/HfsReader) / [MfsReader](https://github.com/hughbe/MfsReader) | C#, MIT; NuGet | Read HFS and MFS disk images | Candidate for the disk-image layer |
| [macresources](https://github.com/elliotnunn/macresources) | Python, MIT; dormant since 2020 | Rez-style text dumps, round trip, BinHex, `dcmp` 2 (GreggyBits) | Round-trip design; `dcmp` 2 reference |
| [machfs](https://github.com/elliotnunn/machfs) | Python, MIT | Reads and writes HFS volumes | Reference for writing HFS images |
| [resource_dasm](https://github.com/fuzziqersoftware/resource_dasm) | C++, MIT; very active | Decodes a very wide range of resource types to modern formats; `dcmp` via 68k emulation; disassembly | Widest coverage to compare output against |
| Claunia.RsrcFork (Aaru) | C#; NuGet, ~16k downloads | Resource fork reading for the [Aaru](https://github.com/aaru-dps/Aaru) preservation suite | Cross-check; Aaru for disk-image formats |
| [HFSExplorer](https://github.com/unsound/hfsexplorer) | Java, GPL-3 | GUI browser for HFS/HFS+ images, extracts both forks | UX reference for the viewer |
| [XADMaster](https://github.com/MacPaw/XADMaster) | C/Objective-C, LGPL-2.1 | The Unarchiver's engine: StuffIt, Compact Pro, BinHex, MacBinary | Reference for archive formats |
| [mpw](https://github.com/ksherlock/mpw) | C | Runs Apple's MPW tools (Rez, DeRez) on modern systems | Ground truth: compile/decompile with Apple's own Rez |
| [ResourceForker](https://github.com/csammis/ResourceForker) | C; dormant since 2016 | Small utilities to split resource forks | Minor reference |

## Phases

Each phase ships something usable; no dates set yet.

1. **Core** — resource map read/write, `dcmp` 0/1/2, raw forks, AppleDouble/AppleSingle, MacBinary, BinHex; CLI
   `list` and raw `extract`.
2. **Decoders I** — images through QuickDraw.Pict, `STR `/`STR#`/`TEXT`/`vers`, `snd ` to WAV; the manifest.
3. **Disk images** — HFS (raw, DiskCopy 4.2, NDIF), recursive unwrapping.
4. **Decoders II** — fonts, UI resources to JSON and dialog previews, palettes; `pack` round trip.
5. **Viewer app** — browse disk images, files and resources with previews and export; can start once phases 1–2
   exist and grow with them.
6. **Editor I** — resource-level edits and saving back into forks and single-file containers.
7. **Editor II** — typed editors and PNG/WAV import (image and sound encoders).
8. **Editor III** — writing HFS disk images.
9. **Merge** — QuickDraw.Pict moves into the ClassicMac repo, split into the target layering (Graphics, QuickTime,
   MacPaint, QuickDraw); the old packages are deprecated.
10. **Later** — HFS+, StuffIt and Compact Pro.

## Open questions

- [x] **Name:** decided — ClassicMac (see Purpose and goals).
- [x] **Repository:** decided — a new repo, `inexin/ClassicMac`; QuickDraw.Pict stays separate for now, to be merged
  in later.
- [ ] **Disk images:** in phase 3 as planned, or earlier because much classic software survives only as disk images?
- [ ] **Decoder priority:** which of sound, text, fonts and UI matters most after images?
- [ ] **Output defaults:** PNG at 32-bit only, or also the screen depth the file was made for?
- [x] **UI framework:** decided — Avalonia (see Viewer app).
- [ ] **Editing order:** the order of the editing stages after the read-only viewer.
- [ ] **Build on or own the core:** build on ResourceForkReader and HfsReader (MIT, .NET, read-only) for the map and
  HFS layers, or write our own core for writing and exact Apple behaviour throughout?
