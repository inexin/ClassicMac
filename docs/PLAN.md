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
the libraries, the CLI and the viewer/editor app. Packages: `ClassicMac.Core`, `ClassicMac.Files`,
`ClassicMac.Resources`, `ClassicMac.Encodings`, `ClassicMac.Graphics`, `ClassicMac.Graphics.ImageSharp`, `ClassicMac.Graphics.SkiaSharp`,
`ClassicMac.Resources.Decoders`, `ClassicMac.Resources.Cli`; the app carries the same name. QuickDraw.Pict moved into
this repo with its history on 2026-09-29 and became `ClassicMac.Graphics` (QuickDraw, PICT, QuickTime images,
MacPaint, fonts) with the two adapters (the layering below).

## Inputs

Resource forks rarely survive on modern disks, so most of the value is in unwrapping the containers they travel in.
Proposed priority:

| Priority | Container | Typical source | Status (2026-10-01) |
| --- | --- | --- | --- |
| 1 | Raw resource fork (`.rsrc`, `file/..namedfork/rsrc` on macOS) | Extracted forks, macOS copies | Built |
| 1 | AppleDouble (`._file`, `__MACOSX/` in zips) and AppleSingle | Files copied to FAT/SMB, zip archives | Built (read and write) |
| 1 | Basilisk II / SheepShaver shared folders (`.rsrc/<name>` fork, `.finf/<name>` Finder info) | Files copied out of emulators | Built |
| 1 | PC Exchange / File Exchange folders (`RESOURCE.FRK/<8.3 name>` fork, `FINDER.DAT` records) | DOS and Windows disks written by Mac OS 7.1–9 | Built |
| 1 | MacBinary I/II/III (`.bin`) | Downloads, archive sites | Built (read and write) |
| 1 | BinHex 4.0 (`.hqx`) | Usenet, old download sites | Built (read and write) |
| 2 | HFS and MFS disk images: raw `.dsk`/`.img`/`.hda` bare or inside an Apple Partition Map, DiskCopy 4.2, NDIF (Disk Copy 6 `.img`, `.smi`; ADC and resource-fork-less reconstruction), DART, UDIF `.dmg` | Emulator disks, floppy images, Apple system software, images re-shared from Mac OS X | Built; plain HFS also written (phase 8) |
| 2 | CD images: ISO 9660 with Apple extensions and Rock Ridge names, hybrid ISO + partition map + HFS; `.iso`/`.toast`/`.cdr`, raw 2352-byte `.bin` + `.cue` | Magazine, game and system CDs | Built, as the Mac reads them: hybrid discs as HFS; no Rock Ridge (the Mac reads only the primary descriptor); multisession by the last session, as the Mac reads it |
| 2 | FAT disk images with PC Exchange / File Exchange data (e.g. `RealmzClassicHD.img`) | Emulator hard disks and floppies shared with PCs | Built (with DOS partition tables) |
| 2 | Zip / MacZip (`.zip`, stored and DEFLATE) with Mac extra fields (Info-ZIP 0x07c8, `M3`, ZipIt) and `__MACOSX/` pairing; tar/gzip (`.tar`, `.gz`, `.tgz`, MacGzip) with `._` pairing | Modern re-uploads, Unix-era transfers | Built (read); Mac extra fields fitted, no Mac-made zip checked yet |
| 2 | uuencode (`.uu`, `begin-base64`) | Usenet and mail transfers | Built |
| 3 | HFS+ images | Mac OS 8.1–9 disks | Built, read-only (phase 10) |
| 3 | StuffIt 1.x–5 (`.sit`, `.sea`), including the v1.5 and 1.6–4.5 `SIT!` generations and v5 store/LZ77+Huffman/Deflate/Arsenic-BWT methods; Compact Pro (`.cpt`) and StuffIt/Compact Pro self-extractors | Most classic Mac downloads | Built (phase 10; self-extractors through their data fork) |
| 3 | DiskDup+ (`DDim`/`DDp+`, `.dsk`); PCE developer toolkit MAR (`.mar`, TAR/MacBinary hybrid by Hampa Hug) | DiskDup+ disks and PCE toolkit archives | DiskDup+ built (raw sectors, verified); MAR not started (Todo) |
| 3 | Mac ROM images: the ROM's built-in resource map (its own entry format, selected per machine) | ROM dumps for emulators | Built: PowerPC ROMs and the New World `Mac OS ROM` file; 68k ROMs unverified |
| 4 | DiskDoubler (`.dd`), StuffIt SegmentIt archives, PackIt (`.pit`), standalone LHA/LZH (`.lzh`) | Early 1990s downloads, multi-floppy BBS files | Built (phase 10) |
| 5 | Encrypted Mac application formats, only on request: StuffIt X (`.sitx`); FolderBolt; MacSafe II; Crypt for Mac; MacPGP; Apple File Security | Password-protected archives, files and folders | On request only |
| 5 | Only on request: AppleLink PackageIt, Now Compress, MacLHA, MOOF flux images, MAME CHD, Apple II formats | Rare, or not classic Mac | On request only |

Containers can nest (a `.hqx` holding a `.sit` holding a disk image), so input detection recurses (built).

Out of scope: `.sparseimage`/`.sparsebundle` (Mac OS X). StuffIt X (`.sitx`) and the other priority-5 encryption formats
are only considered on request; their exact on-disk variants, password handling and cryptographic details need
verification before implementation.

Every container yields the same *Mac file* entry (`MacFile`, in `ClassicMac.Files`): name (MacRoman), type and
creator, Finder flags, creation and modification dates (seconds since 1904, local time), data fork and resource fork.
Single-file containers yield one entry; disk images and archives yield a tree of them.

**File layer and resource map are separate packages (decided).** Containers wrap whole Mac files (both forks and
Finder info), not resource forks, so they live apart from the resource map in one package, `ClassicMac.Files`: the
file model and single-file wrappers, `ClassicMac.Files.Hfs` for HFS and MFS volumes and disk images, `ClassicMac.Files.Fat` for FAT volumes, and later
`ClassicMac.Files.Archives`.
People who only unpack old downloads or disk images don't need the resource map, people who only edit resource forks
don't need the containers, and each package stays smaller to test and fuzz. The types both sides use — `FourCC`
(types, creators, resource types), `MacString` (file and resource names), `MacDate` (file and volume dates, and the
1904-based dates inside resource data such as QuickTime headers) and `Diagnostic` — live in a small shared base,
`ClassicMac.Core`, so neither side depends on the other.

## Core: the resource map

`ClassicMac.Resources` reads a resource fork into an in-memory model and writes one back.

- **Model:** file → types → resources; each resource has type, ID, name, attributes (system heap, purgeable, locked,
  protected, preload, compressed) and its data.
- **Finder info:** belongs to the file (`FinderInfo` in `ClassicMac.Files`), not the fork; the manifest records it
  beside the fork's resources.
- **Compressed resources:** the System's `dcmp` 0, 1, 2 and 3 decompressed natively (`ResourceDecompression`),
  all four and the Resource Manager's handling (header, in-place block, working buffer, failures) following the
  disassembly of the ROM $077D and Mac OS 9.0 code; `dcmp` 3 (bit-stream LZ77, used by every compressed Mac OS 9
  System resource) was first ported from resource_dasm and then checked against it. On Mac OS 9 all decompressors run
  as 68k code (the native `ncmp` ones are never used). Malformed-input behaviour follows Mac OS 9 by default, the 68k
  ROM when `ReadOptions.ResourceManager` says so. Where the Mac has no bounds the result is reproduced: memo tables
  overwrite each other, output may overtake input in place, and the memory after the block (2 KiB, zero) takes
  `dcmp` 3's overshoot and reads past the input; anything further stops with a diagnostic. Unknown `dcmp` IDs are kept
  compressed and flagged in the manifest; applications can add decompressors.
- **Map attributes:** the word at map offset 22 is two bytes — `mAttr` (read-only, compact, changed, force system
  heap) and `mInMemoryAttr` (flags such as the decompression password bit, which some files carry on disk); both are
  kept (`ResourceFork.Attributes`, `ResourceFork.MapFlags`).
- **API shape:** read from a `Stream` or memory. A fork's 24-bit data offsets cap it at 16 MiB of data, so a fork is
  loaded whole and each resource's data is a slice of that buffer; laziness lives one level up, in `ForkData`, so
  disk images stay cheap to open.
- **Canonical writing (decided):** the writer produces what the Resource Manager's compaction leaves (UpdateResFile /
  CloseResFile, from the ROM and Mac OS 9 disassembly, checked in SheepShaver): header; the reserved areas; data items
  packed with no padding in ascending order of where they were, added or grown data after the old data, shared data
  kept shared; the map with the header copy, its runtime fields as read (zero for a new fork), attributes without
  compact and changed ($60), type list at 28, types in the order first added, references in the order added, names in
  the order set (a new name goes to the end). A new fork matches CreateResFile (286 bytes). Round trips are judged on
  the model; a fork the Mac left compacted comes back byte for byte — 142 of 152 corpus forks, and 29 of 33 forks
  written by Mac OS 9 in the harness (the rest: stale bytes left by in-place shrinking, duplicate IDs, and a fork
  OS 9 itself corrupts).
- **Duplicates (decided):** a damaged fork with two resources of the same type and ID keeps the first, as
  `GetResource` would (confirmed in the harness), and reports the rest. A type listed twice is merged (Mac OS 9's
  GetResource searches all its lists; counting and indexing see only the first).
- **Tolerant reading:** truncated or overlapping entries go to a diagnostics list (severity, offset, message), not
  exceptions; only unusable input throws. The reader also reports whether the modelled Resource Manager would open the
  fork (`fork.mac-rejects`, from Mac OS 9's vNewMap/CheckMap or the ROM's much weaker checks, matching the analysis
  model on all harness forks it reads), would read past its map (`fork.mac-misreads`), would hang (a resource count of
  $FFFF), or would misread reference lists that are not contiguous in type order.
- **Writing:** rebuild a fork from the model, for modding and round-trip tests.
- **Text encodings:** MacRoman by default; the script of a file or of a font picks other Mac encodings (Japanese,
  Cyrillic, …). See [Text encodings](#text-encodings).
- **Untrusted input:** see [Hostile input](#hostile-input).

### Text encodings

.NET has no Mac encodings built in (`CodePagesEncodingProvider` must be registered and does not cover every Mac
script), so ClassicMac ships its own tables, generated from Unicode's published Apple mapping files (Unicode licence,
noted in `THIRD-PARTY-NOTICES.md`).

- **`ClassicMac.Core`:** MacRoman (done: `MacRoman`, `MacString.FromMacRoman`/`ToMacRoman`) and the single-byte Mac
  scripts (Central European, Cyrillic, Greek, Turkish, Icelandic, Croatian, Romanian, Symbol, Dingbats) beside
  `MacString`, which both file names and resource names use; small enough to keep the base dependency-free.
  `FourCC` and `MacString` display as Mac OS Roman with control characters as `\xHH`, and `FourCC` parses that form.
- **`ClassicMac.Encodings`:** the multi-byte scripts (Japanese, Traditional and Simplified Chinese, Korean), optional
  because their tables are large.
- **Choosing the script:** an explicit `--encoding` wins; otherwise the font's script (`FOND` family ID range), then
  the file's region (`vers`), then MacRoman. The choice is recorded in the manifest.
- MacRoman ↔ Unicode is lossless (every byte maps to one code point), so names and four-character codes stored as
  text round-trip exactly.

### Hostile input

The readers parse arbitrary downloaded files, so every size and offset is checked before use.

- **Bounds:** offsets and lengths are checked against the stream before reading; anything outside goes to diagnostics.
- **Allocation limits:** a per-resource ceiling on decompressed size; `dcmp` output must match the size its header
  declares; decoders check image dimensions against a pixel ceiling before allocating.
- **Nesting and cycles:** container recursion stops at a depth limit; HFS B-tree and extent walks detect cycles.
- **Fuzzing:** SharpFuzz with libFuzzer on each container reader, the resource map and `dcmp`, seeded from the test
  fixtures; a short run on every CI build, a longer one nightly; crashes become regression fixtures.

### Configuration

No tunable value is hard-coded: every limit and default lives as a property on an options object, with its default
documented there, and the CLI flags and the app's settings map onto the same objects.

| Options object | Package | Settings (default) |
| --- | --- | --- |
| `ContainerReadOptions` | Files | Max container nesting depth (8), max total bytes expanded per input (1 GiB), time zone for UTC container dates (local), max files and folders read from one volume (1,000,000), File Exchange extension map (none), verify whole-image checksums (off) |
| `ReadOptions` | Resources | Max decompressed resource size (64 MiB), Resource Manager model (Mac OS 9), text encoding override (none) |
| `DecodeOptions` | Decoders | Text encoding (Mac OS Roman; more scripts later), line endings in text output (LF), image encoder (PNG), screen depth (32-bit), max image pixels (64 megapixels), QuickDraw model (Mac OS 9); later make fonts loadable (off) |
| `ExportOptions` | Resources | Max output path length (200 characters), keep raw data (off), types to export (all), overwrite (off), `ReadOptions` for decompression |
| `HostWriteOptions` | Files | Layout for unpacked files (AppleDouble or Basilisk II; AppleDouble), max path length (200 characters), overwrite (off), time zone for dates (local) |
| `PackOptions` | Resources | Base fork (none), allow deletes (off) |

Options objects are immutable records with `with` for changes, so one instance can be shared across threads; the
defaults are a static `Default` on each. Each object arrives with the code that uses it; the encoding override joins
`ReadOptions` with the encodings.

### Core API

The model, by package (namespaces start with the package name). `ResourceFork.Read` and `ResourceFork.Write`/`ToArray`
read and write a fork; `ResourceDecompression` returns a resource's data as the Resource Manager would:

| Package | Type | What it is |
| --- | --- | --- |
| Core | `FourCC` | A four-byte code (type, creator, resource type); exact, case-sensitive comparison |
| Core | `MacString` | A Pascal string's raw bytes; decoded to Unicode only with the file's encoding, so round trips are exact |
| Core | `Diagnostic` | Severity, stable code, message, offset |
| Core | `MacDate` | Seconds since 1904 in the writer's local time; converts to a `DateTime` of unspecified kind |
| Core | `MacPoint`, `MacRect` | QuickDraw `Point` (v, h) and `Rect` (top, left, bottom, right), read and written big-endian; used by Finder info and by many resources (`DLOG`, `DITL`, `WIND`, `PICT` frames, `cicn` bounds) |
| Core | `Fixed`, `UnsignedFixed` | 16.16 fixed-point numbers (resolutions, font metrics, QuickTime values; sound sample rates are unsigned) |
| Core | `BigEndianReader`, `BigEndianWriter` | Cursor-based, allocation-free readers over `ReadOnlySpan<byte>` and writers over `Span<byte>`; big-endian scalar and Mac value reads/writes, with position, remaining length and checked bounds |
| Files | `FinderInfo`, `FinderFlags` | `FInfo` fields and the raw 16 bytes of `FXInfo` |
| Files | `MacFile` | Name, folder path inside its container (`MacPath` joins it with `:`), Finder info, dates, and both forks as `ForkData` (opened on demand) |
| Files | `ForkData` | A fork opened on demand: from bytes, a host file, or a `Slice` of another fork (no copy), so containers and disk images stay lazy; `ReadAt` reads in place (a host file through one handle kept open between reads, closed when idle or before a save replaces it) |
| Files | `IContainerReader` | One per container format: `CanRead(ForkData)`, `Read(ForkData, ContainerContext)` → Mac files |
| Files.Containers | `AppleSingleReader`, `MacBinaryReader`, `BinHexReader`, `PcExchange` | AppleSingle/AppleDouble v1–v2, MacBinary I/II/III (one reader per version), BinHex 4.0, PC Exchange records |
| Files.Hfs | `HfsReader`, `MfsReader`, `DiskCopy42Reader`, `NdifReader`, `DartReader`, `UdifReader`, `PartitionMapReader` | HFS and MFS volumes (forks read in place through their extents), Disk Copy 4.2, UDIF, NDIF (chunks decoded on demand, segments joined) and DART images, Apple partition maps; each yields files the next can open |
| Files.Fat | `FatReader`, `MbrReader` | FAT12/16/32 volumes with each file's Mac name, Finder info, dates and resource fork from `FINDER.DAT`/`RESOURCE.FRK`; DOS partition tables |
| Files.Iso | `IsoReader` | ISO 9660 and High Sierra volumes as Mac OS 9 shows them: `AA`/`BA` Finder info, associated files as resource forks |
| Files.Iso | `RawCdReader`, `CueSheetReader` | Raw CD images (2352/2336-byte sectors, per-sector mode) and cue sheets (first data track, from the `.bin` beside it) as 2048-byte blocks for the volume readers |
| Files.Containers | `ExtensionMap` | Internet Config's name-ending map, as File Exchange applies it to `TEXT`/`dosa` files; opt-in through `ContainerReadOptions.ExtensionMap` |
| Files | `HostFiles` | A host file with its companions: PC Exchange `RESOURCE.FRK`/`FINDER.DAT`, Basilisk II `.rsrc`/`.finf`, AppleDouble `._name` or `name.rsrc` files (The Unarchiver), macOS named forks; `Write` puts a Mac file back on disk as AppleDouble or Basilisk II |
| Files | `MacFileResources` | A file's resources: its resource fork, or a data fork that is a clean resource fork (Realmz `.rsf`), or a plain file read as a fork when its header describes one; shared by the CLI and the app |
| Core | `HostNames` | Mac names as safe, distinct host names (`%XX` escapes, reserved Windows names, collisions, length) and type folder names, shared by `unpack` and `extract`; SheepShaver's own naming for Basilisk II folders |
| Files.Containers | `AppleDoubleWriter` | AppleDouble v2 `._` files: real name, dates, Finder info, resource fork |
| Files | `ContainerUnwrapper` | Tries the readers on each data fork and recurses, giving a `ContainerNode` tree; or reads a number of levels (a container holding one file does not count) and leaves the containers below unread (`UnreadFormat`) for `Expand`. A container's files are probed in parallel, the probes sharing one read of each fork's head and tail. Diagnostics from inside a nested file carry its `Location` |
| Files | `ContainerReadOptions` | Unwrapping limits and the time zone (see Configuration) |
| Resources | `Resource` | Type, ID, optional name, attributes, and data as stored (a slice of the fork read) |
| Resources | `ResourceFork` | Resources in read order, map attributes, the reserved header areas and the map's runtime handle and file reference (kept so such forks round-trip exactly); add, remove, renumber, find |
| Resources | `ResourceDecompression`, `IResourceDecompressor` | `dcmp` 0–3 and app-supplied decompressors |
| Resources | `ReadOptions` | Resource-reading limits and the Resource Manager model (see Configuration) |
| Files.Export | `Unpacker`, `OutputLayout`, `ExportFolders` | Every Mac file under a tree to a folder (`unpack`), or every resource fork (`extract`), placed as the tree nests; new numbered folders that never overwrite. Shared by the CLI and the app |
| Resources.Export | `ResourceExporter`, `ExportOptions`, `ExportSource`, `ExportManifest` | A fork to type folders (decoded, or the data itself; stored bytes in `raw/` on request) with `manifest.json`, format 1.1 (`schemas/manifest-1.schema.json`) |

| Resources.Export | `IResourceDecoder`, `DecodeInput`, `DecodedFile` | A decoder for some resource types; the exporter uses the first that handles a type and falls back to raw |
| Resources.Decoders | `ResourceDecoders`, `DecodeOptions` | The built-in decoders, one namespace per area (`.Text`, `.Images` and `.Sound` built) |
| Resources.Decoders.Images | `IImageEncoder`, `PngEncoder` | How decoded images are written; PNG (8-bit RGBA) built in |

**Big-endian helpers:** `BigEndianReader` and `BigEndianWriter` are implemented in `ClassicMac.Core` as `ref struct`
cursors over caller-owned spans. They support signed and unsigned 16-, 32- and 64-bit values, borrowed byte slices,
skip/position/remaining, and `FourCC`, `MacPoint`, `MacRect`, `Fixed` and `UnsignedFixed`; throwing and `Try` operations
check bounds. `Read*At` / `Write*At` operations address offsets from the start of the span without changing cursor
position. The types do not allocate or take ownership of buffers. Existing callers are being migrated selectively;
retain offset-explicit `BinaryPrimitives` calls where clearer and do not replace `Stream` or `BinaryReader` generally.

A resource fork travels from the file layer to the resource map as bytes (`MacFile.ResourceFork` → `ResourceFork.Read`).
`ClassicMac.Files` references `ClassicMac.Resources` (never the other way), because a few disk images keep their
layout in resources (NDIF's `bcem`, early UDIF's `blkx`).

- **Mutable model, immutable records:** `ResourceFork` and `Resource` are mutable, because the editors change them;
  everything describing a file (`MacFile`, `FinderInfo`, options) is an immutable record. The model is not
  thread-safe.
- **Uniqueness enforced:** a fork holds one resource per type and ID, and a resource belongs to one fork at a time.
- **Order kept:** resources stay in the order read, and each resource remembers where its data and name sat and its
  handle field, so a fork the Mac left compacted is written back byte for byte.

### CLI

A `dotnet tool` (package `ClassicMac.Resources.Cli`, command `classicmac`) built on System.CommandLine; `classicmac mcp`
uses the official MCP C# SDK's `ModelContextProtocol.Core` (Apache-2.0; decided 2026-10-03: a permissive licence is
fine for a dependency, with its notice in `THIRD-PARTY-NOTICES.md`; only the core package, no hosting or DI).

| Command | Does | Options | Phase |
| --- | --- | --- | --- |
| `info <input>` | Companions, container chain, Finder info, kinds with their source, dates, fork sizes (built) | `--type-creator-db <xlsx>` (the user's TCDB spreadsheet in place of the shipped TCDB, [finder.md §2.6](formats/resources/finder.md#26-the-type-and-creator-database-tcdb); a missing or unreadable file is a usage error, exit 2) | 1 |
| `list <input>` | Resources of every file inside the input, through containers; a data fork holding a resource fork (Realmz `.rsf`) or a raw fork file is read as a fork (built) | `--format text\|json` | 1 |
| `unpack <input>` | Every Mac file inside the input, through containers and disk images, to a folder with both forks and Finder info; folders kept, a container of one file replaced by it, a disk or archive of several becomes a folder (built) | `-o <dir>`, `--layout appledouble\|basilisk`, `--overwrite` | 2 |
| `extract <input>` | Resources into a folder with a manifest; a folder per file when the input holds several; decoded by default; a DOCMaker or SimpleText document also as HTML in `document/` (built) | `-o <dir>`, `--raw`, `--keep-raw`, `-t <type>` (repeatable), `--overwrite`, `--screen-depth`, `--no-documents`; later `--encoding` | 1 (raw), 3 (decoded) |
| `convert <input>` | Every DOCMaker and SimpleText document inside the input as an HTML folder; a folder per document when there are several (built) | `-o <dir>`, `--overwrite`, `--screen-depth` | 3 |
| `disasm <input>` | The code of every Mac file inside the input: a listing (`.s`) per 68k segment, code resource and fragment (the data fork's included) and `code.json`; a folder per file when several have code (built; [disassembly.md](formats/output/disassembly.md)) | `-o <dir>`, `--cpu 68k\|ppc\|both`, `--overwrite` | 11 |
| `pack <dir>` | Rebuild a fork or container from a folder and manifest (built; changed decoded files wait for encoders) | `-o <file>`, `--base <file>`, `--data <file>`, `--allow-deletes`, `--overwrite`, `--container raw\|appledouble\|applesingle\|macbinary\|binhex` | 5 |
| `ls <path>` | What a Mac path holds: files and folders (through containers), a fork's types, a type's resources (built; [cli.md](cli.md) §2.1) | `--follow`, `--json` | — |
| `stat <path>` | Kind, type/creator and Finder kind, an alias's original and whether it resolves, forks, dates, flags, how it was read (built; §2.2) | `--json` | — |
| `cat <path>` | A file's text (Mac OS Roman as UTF-8), a hex dump, its raw bytes, or a resource decoded (built; §2.3) | `--hex`, `--raw`, `--fork data\|rsrc`, `--max-bytes`, `--follow`, `--json` | — |
| `find <path>` | Folders and files below a path, through containers (built; §2.4) | `--name`, `--type`, `--creator`, `--kind`, `--resource-type`, `--contains`, `--contains-hex`, `--max-depth`, `--limit`, `--json` | — |
| `get <path>` | A file (both forks), folder or resource to the host (built; §2.5) | `-o <dir>`, `--as appledouble\|basilisk\|macbinary\|raw`, `--enter`, `--overwrite`, `--follow`, `--json` | — |
| `check <input>` | Read the input through and report its diagnostics; a plain HFS volume also gets the writer's checks (built; [cli.md](cli.md) §2.7) | `--json`, `--strict`, `-q` | — |
| `shell <input>` | A DOS-like shell on one input: cd, dir, type, info, res, find, copy in and out, del, md, ren, set, save, save as; history and tab completion; `--script` or piped input for automation (built; [cli.md](cli.md) §5) | `--script <file>`, `--json` | — |
| `mcp` | An MCP server over standard input and output: sessions on opened inputs, the read and write commands as tools, saves only through `save_as` (built; [cli.md](cli.md) §4) | the limit options | — |

- **Every command:** `--max-resource-size` maps onto `ReadOptions`, `--max-nesting-depth` and `--max-expanded-bytes`
  onto `ContainerReadOptions` (sizes in bytes or KiB/MiB/GiB), defaults taken from each record's `Default`;
  `--strict` makes warnings fail; `-q` prints errors only. The CLI references Files and Resources.
- **Exit codes:** 0 success (warnings printed); 1 part of the input could not be read (or warnings with `--strict`);
  2 usage error; 3 input not recognised or unusable; 4 file-system error; 5 a Mac path names nothing (or starts with no
  host file); 70 command not built yet.
- **Output:** results to stdout, diagnostics to stderr, so `list --format json` can be piped.

**File commands, shell and MCP (planned, decided 2026-10-03).** One command core in the library, three front ends:

1. **Core** (library, under Files): operations on *Mac paths* that go through containers
   (`Mac OS 9.hfv:System Folder:Finder`, an archive inside a disk inside a disk image): `ls`, `stat`, `cat` (text,
   hex, or a resource decoded as JSON), `find` (by name, type/creator, kind, resource type, content), `get` (extract),
   `put`/`add`, `rm`, `mkdir`, `save-as`. The paths are built: `MacPathTree` and `MacPaths` (syntax in
   [cli.md](cli.md) §1), and so are the read operations (`MacCommands`: ls, stat, cat's bytes, find, get) with their
   CLI subcommands and `--json` (§2). Writes reuse the editing code (`HfsWriter`, fork editing, Save As; what lives
   in the app moves into the library). The original is never changed unless `--in-place`; otherwise writes go to a
   new file. `--dry-run` on every write. The write side is built: `InputEditSession` (`ClassicMac.Files.Editing`) on
   an opened input (a plain HFS image: add file, add folder, delete, recursive delete, rename, move, lock, bless, type/creator/flags,
   resources; a single Mac file: resources, Finder info, name), its changes listed as `PlannedChange`s, saved as a new
   file or in place; `HostImport` reads a host file to add; `HfsWriter.Rename`, `SetFinderInfo`, `SetFolderFlags`,
   `Delete`. The app's Volume menu uses them.
2. **CLI subcommands** for each operation with `--json` (stable, documented schemas in `docs/cli.md`), so any AI with a
   shell can drive them. The write commands are built: `put`, `mkdir`, `rm`, `rename`, `mv`, `lock`, `unlock`, `bless`, `set`, `res-add`, `res-rm`
   ([cli.md §3](cli.md#3-write-commands)).
3. **MCP server** (`classicmac mcp`, stdio): the same operations as MCP tools (list, stat, read, search, extract, add,
   delete, save as), with the same write safety and output-size limits. Built ([cli.md §4](cli.md#4-mcp-server)): a
   session per opened input, changes made on working copies so reads see them, written only by `save_as` (in place
   only with `in_place`), lists and reads in pages with a `more` cursor.
4. **Interactive shell** (`classicmac shell <input>`), DOS-like: `cd` (into disk images and archives as folders),
   `dir`/`ls`, `type`, `info`, `res`, `copy` (in or out), `del`, `md`, `find`; prompt `Mac OS 9.hfv:System Folder>`,
   tab completion, history; leaving with unsaved changes asks Save / Save As / Discard. Built
   ([cli.md §5](cli.md#5-shell)): on the MCP server's session (working copies), `save` in place and `save as`, a
   script or piped input run without questions (stopping at the first failure; `--json`: one object per command),
   the console behind an interface for tests.

Order: core with the `--json` subcommands, then the MCP server, then the shell.

**More file commands (planned, decided 2026-10-03; suggested by the Mac RE session).** In this order:

1. `check <input>`: the writer's checks without an edit, plus every diagnostic (built; [cli.md](cli.md) §2.7).
2. `mv <path> <folder>`: move a file or folder to another folder on the same volume (built: `HfsWriter.Move`, the
   CLI, the MCP server's `mv` and the shell's `move`; [cli.md](cli.md) §3.2).
3. Volume information in `stat` of a volume's root: block size, free space, file and folder counts, dates, the lock
   (built: `VolumeInfo` from HFS, HFS Plus and MFS, [cli.md](cli.md) §2.2).
4. `lock` / `unlock` (a file's locked flag) and `bless` (the System Folder in the MDB's Finder info) (built: the CLI,
   MCP and shell; `stat` shows the blessed folder).
5. Transfers that convert: `put --text`/`get --as text` (Mac OS Roman with CR to and from UTF-8 with LF), `get --as
   binhex` and `.hqx` read by `put` (built: the CLI, MCP and shell; [cli.md](cli.md) §2.5, §3.2).
6. Writes to an HFS partition inside an Apple Partition Map image (built for a disk with one Mac volume partition:
   edited in place, [partition-map.md §5](formats/file-systems/partition-map.md#5-classicmac); still to do: disks with
   several, and the app's Volume menu on partitioned disks), and to Disk Copy images (written back in their own
   format: built for Disk Copy 4.2, diskcopy42.md §3, and NDIF, ndif.md §3: changed chunks stored raw; still to do:
   compressing changed chunks, segmented images, checking against Disk Copy in SheepShaver).
7. `format`/`mkvol`: a new, empty HFS volume image of a given size and name (built: `HfsWriter.Format`, the CLI's
   `format`; laid out as Mac OS 9.0's initializer does, traced, hfs.md §3.1).
8. `resize <volume> --size <n>` (decided 2026-10-03; growing built, hfs.md §3.2): grow a plain HFS volume within its allocation block size
   (`drNmAlBlks` at most 65,535): the image lengthened, the block count and free count raised, the bitmap extended (the
   allocation area moved up when the bitmap's sectors are full) and the alternate MDB moved to the new end, every
   CNID kept. Later, shrinking: files and B-tree extents in the blocks cut off moved down into free space first,
   refused when they do not fit; and growing past 65,535 blocks with a larger block size.

**Editing through a block overlay (decided 2026-10-03).** An edit touches only the MDB, the bitmap, the B-tree files and
the edited forks, so the writer stops holding whole images:

- `HfsVolume` (internal): a read-only base `ForkData` (the file, a partition's range, a Disk Copy disk, a decoded NDIF
  disk) and an overlay of changed 512-byte sectors; `Read`, `Write`, `ChangedSectors`, `Fork()` (a failed edit leaves
  the session as it was). `BigEndianReader`/`Writer` rules stay: the MDB, bitmap and trees are read into their own small
  buffers, one reader or writer each, and written back to the overlay; a test fails any path asking for a whole image.
- `HfsWriter` reads and writes through it; the public `X(ForkData) → byte[]` methods stay as wrappers. Verification:
  every changed sector lies in the MDB, bitmap, B-tree files, the target fork's blocks or the alternate MDB, plus the
  pre-edit checks and the record comparisons. Resize stays whole-image.
- `InputEditSession` opens the volume without reading it; `Current()` reads through the overlay. Save As streams a copy
  of the input and writes the changed sectors into it (Disk Copy 4.2's checksum made again by streaming). Save in place
  does the same into a temporary copy and swaps it over the input (crash-safe, as now; the first save keeps `.orig`),
  after checking the input can be opened exclusively (an image mounted elsewhere is refused). Patching in place with a
  journal is not done (it could corrupt an image another program has open). NDIF keeps its disk decoded; the overlay
  names the changed chunks.
- `format` and resize lift the 2 GB limit (streamed writes).
- Steps, each test-first with every suite and the hfsutils/`fsck_hfs` interop passing: a gated corpus write test first;
  `HfsVolume`; writer reads; writer writes with byte-identical results against today's writer; the session and Save As;
  Save in place; NDIF; past 2 GB; docs.

**Further improvements (suggested 2026-10-03, from the work above and the Mac RE session's tests on a real 500 MB
Mac OS 9 volume).** Not ordered yet:

- **Faster sessions** (built): a volume's changes are read from memory (`InputEditSession.Current`, `MacPathTree.Open`
  over a host file), with no working copy written per change.
- **Cheaper fork edits:** `ReplaceFork` (put, resource edits, Save As) checked as deletions are, by kept catalog
  records and allocated blocks, instead of reading every fork of every file (about 0.9 s per fork on 500 MB). Fewer
  whole-image copies in `HfsWriter` too, to lower peak memory.
- **Aliases on rm** (built): a warning for each alias on the volume whose original a deletion removes
  (`aliases.md` §5), also with `--dry-run`, in JSON as the change's `warnings`.
- **More partitioned disks:** writes to disks with several Mac volume partitions (the partition named in the Mac path).
  The app's Volume menu and Save As ▸ HFS Volume Image work on every input the edit session writes (partitioned disks
  with one HFS partition, Disk Copy 4.2 and NDIF images) through `InputEditSession` (built).
- **A gated corpus write test:** under `CLASSICMAC_CORPUS`, edit copies of real Mac OS-written images (delete, add,
  rename, move) and require `check` to pass, so writer rules are tested against Mac OS's layouts, not only against
  `HfsBuilder`'s.
- **`check` of the input's own structures** (built, the default; `--deep` reads into the archives and disk images
  stored in it).

## Decoders

Each decoder turns one resource type into a modern file; anything without a decoder is exported raw.

| Group | Resource types | Output |
| --- | --- | --- |
| Images | `PICT`, `ICON`, `ICN#`, `ics#`, `icm#`, `icl4/8`, `ics4/8`, `icm4/8`, `cicn`, `SICN`, `CURS`, `crsr`, `PAT `, `PAT#`, `ppat`, `ppt#` (built); `icns` later | PNG via QuickDraw.Pict (screen depth selectable); cursors add a JSON file (hotspot, inverted pixels); lists give one image each (`.1.png` …). The image format is a setting (`IImageEncoder`, PNG built in; lossless WebP later) |
| Sound | `snd ` formats 1 and 2, standard/extended/compressed headers: PCM (`raw `, `twos`, `sowt`, `in24`, `in32`, `fl32`, `fl64`), MACE 3:1/6:1, IMA4 and µ-law (built; the codecs from the Sound Manager 3.5.1 disassembly, byte-identical to its output on harness samples); AIFF/AIFC files later (the Sound Manager plays them through the same decompressors); `csnd` is not a Sound Manager type | WAV (rate rounded; loop and base note in a `smpl` chunk) plus JSON (exact rate, header, synthesizers, commands); commands-only sounds give the JSON alone |
| Text | `STR `, `STR#`, `TEXT` + `styl`, `vers` | UTF-8 text (`STR `, `TEXT`), JSON (`STR#`, `styl`, `vers`); styled text also as RTF (built) |
| Fonts | `sfnt`; `NFNT`/`FONT` + `FOND`; `fctb` (built, through `ClassicMac.Graphics.Fonts`) | TTF; BDF, a glyph sheet PNG and metrics JSON (built) |
| UI | `MENU`, `MBAR`, `DLOG`, `DITL`, `ALRT`, `WIND`, `CNTL`, and their colour and extension resources (`wctb`, `dctb`, `actb`, `cctb`, `mctb`, `ictb`, `dlgx`, `alrx`, `xmnu`) (built) | JSON (built), optionally a rendered preview of the dialog |
| Colour | `clut`, `pltt` (built) | JSON and `.act` palettes (built) |
| Finder | `BNDL`, `FREF`, `SIZE` (built) | JSON (built) |
| Code | `CODE`, `cfrg`, native code resources (`ncod`, `nlib`, `ndrv`, …) and 68k code resources (`CDEF`, `WDEF`, `DRVR`, `PACK`, `INIT`, `XCMD`, …) (built, through `ClassicMac.Code`) | the data as `.bin` (so `pack` takes it back), an annotated listing (`.s`) and a model (`.json`); `CODE` 0 and `cfrg` the data and JSON ([disassembly.md](formats/output/disassembly.md)) |
| Unknown | anything else | raw `.bin` + hex preview in the manifest |

**Document decoders** turn a whole file's resources into one document, beside the per-resource output:

| Document | Recognised by | Resources | Output |
| --- | --- | --- | --- |
| DOCMaker stand-alone documents (Green Mountain Software, 1986–1998; common for shareware manuals, e.g. Divinity's) | `APPL`/`Dk@P` | per chapter `TEXT` + `styl` and `Wndo`; `PICT` placed by `pInf`; `STR ` chapter titles, `foot`, `conp`, `xtr2`, `sTwD` | HTML, one page per chapter, pictures as PNG reflowed into the text |
| SimpleText / TeachText documents | `TEXT`/`ttxt`, `ttro` | data-fork text, `styl` 128, `PICT` 1000+ at the option-space markers | HTML |

DOCMaker's private resources (`pInf`, `Wndo`, `foot`, `conp`, …) have no published description; their layout comes
from the reader code each document carries (disassembly), checked against real documents. Both readers draw a
picture over the text at its anchor's line; the HTML puts it in the text flow instead (side by side for anchors on one
line, the blank lines left for it dropped), since a browser's line breaks differ from the Mac's
([html.md §3.2](formats/output/html.md#32-pictures-reflowed)).

App-specific types (a game's data records, an application's private resources) plug in as custom decoders
registered by the application.

## Output

One folder per input file, one subfolder per resource type, and a manifest that describes everything.

```
MyApp/
  manifest.json
  PICT/128 Title Screen.png
  snd%20/200 Door.wav
  STR#/1000 Messages.json
  CODE/1 Main.bin
  CODE/1 Main.s
  CODE/1 Main.json
```

- **Images:** 32-bit RGBA PNG by default; `--screen-depth` renders at a chosen screen depth (1, 2, 4, 8, 16 bit).
- **Round trip:** a `pack` command rebuilds a resource fork from the folder and manifest (below).

### Names on disk

Folder and file names must be valid and distinct on Windows, macOS and Linux. The manifest, not the name, is the
authority: `pack` reads type, ID and name from it, so escaping only has to be safe and readable, never reversible.

- **Resource files:** `<id> <name>.<ext>`, or `<id>.<ext>` when unnamed; the name is converted to Unicode through the
  file's encoding and cut so the whole path stays within the configured maximum (see Configuration).
- **Escaping:** `%XX` (the byte in the file's encoding) for control characters, `% / \ : * ? " < > |`, and a trailing
  space or dot — so `snd ` becomes `snd%20` and `PAT ` becomes `PAT%20`.
- **Reserved Windows names:** a name that matches `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9` or `LPT1`–`LPT9`
  (ignoring case and extension) gets its last character escaped: `COM1` → `COM%31`.
- **Case collisions:** types are case-sensitive but Windows and macOS disks are not. When two types in one fork fold
  to the same name (`PICT` and `pict`, `snd ` and `SND `), each of them gets `~` and the type's hex bytes appended:
  `PICT~50494354`, `pict~70696374`. Types without a collision keep their plain name, so output stays readable.

### Manifest

`manifest.json` is a versioned format, because `pack` and other tools depend on it.

- **Schema:** a JSON Schema per major version in the repo (`schemas/manifest-1.schema.json`), referenced by the
  manifest's `$schema` and `formatVersion` fields. New optional fields are a minor change; anything else is a new
  major version. Readers reject a newer major version and ignore unknown fields.
- **Contents:** the source (container chain, file name), Finder info, encoding used, and diagnostics; then per
  resource: type (as text), ID, name, attributes, original size, `dcmp` ID if compressed, decoder and its version,
  output path (relative, `/`-separated), SHA-256 of the raw data and of the output file, and warnings.
- **Pack rules:** a file whose hash is unchanged takes the original raw data, from `raw/` (written by
  `extract --keep-raw`; three characters, so it never clashes with a type folder) or from the original fork given with
  `--base`; a changed file is re-encoded by its decoder's encoder, or is an error if there is none.
  Resources in the manifest with no file are dropped only with `--allow-deletes`.

## Architecture and packaging

A shared base, a file layer and a resource layer, then decoders and the tools on top, so each part can be embedded on
its own and the heavy dependencies stay optional. Arrows point from a package to what uses it.

```mermaid
flowchart LR
    B["ClassicMac.Core<br/>FourCC, MacString, MacDate,<br/>Point, Rect, Fixed,<br/>diagnostics"]
    subgraph filelayer["File layer"]
        F["ClassicMac.Files<br/>Mac files, Finder info, host folders;<br/>.Containers: AppleSingle/Double,<br/>MacBinary, BinHex, PC Exchange;<br/>.Hfs: HFS/MFS, DiskCopy 4.2;<br/>.Fat: FAT, DOS partitions;<br/>later .Archives"]
    end
    subgraph resourcelayer["Resource layer"]
        M["ClassicMac.Resources<br/>resource map, dcmp,<br/>read and write"]
        D["ClassicMac.Resources.Decoders<br/>images to PNG, sound to WAV,<br/>text, fonts, UI to JSON,<br/>code to listings"]
    end
    E["CLI · viewer app<br/>unwrap, browse, export,<br/>pack back"]
    B --> F & M & Q
    M --> F
    M --> D
    F & D --> E
    Q["ClassicMac.Graphics<br/>QuickDraw, PICT, QuickTime,<br/>MacPaint, fonts"] --> D
    M --> C["ClassicMac.Code<br/>PEF, cfrg, 68k segments,<br/>code resources, disassembly"]
    C --> D
    A["App decoders<br/>an app's own formats"] --> D
```

- **ClassicMac.Core** — `FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `Diagnostic`, `MacRoman`,
  host-safe naming (`HostNames`) and (later) the single-byte text encodings; no dependencies, .NET 10.
- **ClassicMac.Files** — the whole file layer, one package with one namespace per area; depends on Core and
  Resources:
  - `ClassicMac.Files`: the Mac file model (`MacFile`, `FinderInfo`, `ForkData`), `IContainerReader`, `HostFiles`,
    `ContainerUnwrapper`, options;
  - `ClassicMac.Files.Containers`: AppleSingle/AppleDouble, MacBinary, BinHex, PC Exchange records;
  - `ClassicMac.Files.Hfs`: HFS and MFS volumes, Apple partition maps, Disk Copy 4.2, NDIF (Disk Copy 6, including
    `.smi` and segmented images), DART, UDIF `.dmg`; HFS+ later;
  - `ClassicMac.Files.Iso`: ISO 9660 and High Sierra volumes as Mac OS 9 reads them, raw-sector CD images
    (`.bin`, 2352/2336-byte sectors) and cue sheets, multisession discs by their last session;
  - `ClassicMac.Files.Compression`: decompressors shared by disk images and archives (ADC for NDIF and UDIF, KenCode,
    DART RLE and LZH, bzip2 for UDIF; the StuffIt and Compact Pro methods later);
  - `ClassicMac.Files.Fat`: FAT12/16/32 volumes with the PC Exchange / File Exchange data Mac OS kept on them, and DOS
    (MBR) partition tables;
  - `ClassicMac.Files.Archives` (later): zip and tar with Mac data, StuffIt, Compact Pro, DiskDoubler, PackIt.
- **ClassicMac.Resources** — the resource map and `dcmp`; depends on Core only.
- **ClassicMac.Encodings** — the multi-byte Mac text encodings; optional, depends on Core.
- **ClassicMac.Graphics** (decided) — QuickDraw, PICT, QuickTime still images, MacPaint and fonts, one package on
  Core only (see the layering below). Its fonts (`ClassicMac.Graphics.Fonts`) are the Font Manager's resources: bitmap
  strikes (`NFNT`, `FONT`), families (`FOND`), font colour tables (`fctb`) and TrueType `sfnt` data, parsed into
  glyphs as plain pixel arrays and metrics. They find a family's strikes through a small lookup (type and ID to bytes)
  rather than `ClassicMac.Resources`, so the renderer can use them too. The decoders use them for font export and the renderer
  for text (its own parser, inherited from QuickDraw.Pict, went in stage 3; the ROM's reading rules are an internal
  option). `ClassicMac.Graphics.ImageSharp` and `ClassicMac.Graphics.SkiaSharp` are the
  host-library adapters.
- **ClassicMac.Code** — classic Mac code: PEF containers and `cfrg` (`.Ppc`), 68k applications, data initialisers
  and code resources (`.M68k`), and the 68k and PowerPC disassemblers with their annotator and listings
  (`.Disassembly`); depends on Core and Resources.
- **ClassicMac.Resources.Decoders** — the built-in decoders, one package with a namespace per area (text, images,
  sound, code; decided, like the file layer); depends on Resources, `ClassicMac.Graphics` and `ClassicMac.Code`.
- **CLI** — a `dotnet tool` with `info`, `list`, `unpack`, `extract`, `convert`, `disasm` and `pack`; references the
  file and resource packages.
- **Viewer app** — a cross-platform desktop app on the same packages (below).
- **Extension points:** an `IContainerReader` per container format and an `IResourceDecoder` per resource type, so
  apps add their own.
- **Own core (decided):** the resource map and disk-image layers are written here, because writing and exact Apple
  behaviour are needed throughout; ResourceForkReader, HfsReader and MfsReader (read-only) serve as cross-checks.

### Repository layout and build

```
ClassicMac.slnx
global.json                 .NET 10 SDK, rolling forward to the latest feature band
Directory.Build.props       shared settings (below) and package metadata
Directory.Packages.props    every package version, in one place
src/ClassicMac.<Package>/   one folder per package; the app goes in src/ClassicMac.App/
                            today: Core, Files, Resources, Graphics (and its two adapters), Code,
                            Resources.Decoders, Resources.Cli, App
tests/ClassicMac.<Package>.Tests/   one test project per package
tests/fixtures/             synthetic fixtures (and their Rez sources)
schemas/                    manifest JSON Schemas
tools/                      fixture and table generators; never packed
```

- **Every project:** `net10.0`, nullable enabled, warnings as errors, implicit usings off, deterministic builds.
- **Projects under `src/`:** XML documentation, `IsAotCompatible` (trimming and AOT analysers on), so the CLI can
  publish as a native executable; tests and tools are never packable.
- **Packages** are referenced without versions; `Directory.Packages.props` holds them.
- **CI:** `dotnet test` on Windows, Linux and macOS for every push; fuzzing and the Rez hash check join as they arrive.

### Layering of the graphics package (the QuickDraw.Pict merge)

QuickDraw.Pict is split so every dependency points down: QuickTime and MacPaint stop living inside PICT, PICT calls
QuickTime for its embedded images (`$8200`/`$8201`), and the QuickDraw renderer is separated from the PICT file
format. **Done (stage 2, 2026-09-29):** the layers are folders and namespaces of one package, `ClassicMac.Graphics`
(decided 2026-09-29, revising a first split into four projects and a separate fonts package: users want PICT,
QuickDraw or fonts together, and one package is simpler to publish and version). `LayeringTests` fails on any
namespace used against the layering. `RgbaBitmap.Info`, the one upward reference, went: `PictReader.Read` returns the
bitmap with its `PictInfo`, whose frame and bounds are Core's `MacRect`. `QuickDrawResources` moved into
`ClassicMac.Resources.Decoders`. The adapters and the decoders share internals (`InternalsVisibleTo`) until the
renderer's drawing API is public. **Stage 3** (below) is what the table still describes: Core's geometry,
the public drawing API, icons as full resource decoders; the renderer draws text with `.Fonts` (done, 2026-09-29).

| Layer (namespace) | Contains | Depends on |
| --- | --- | --- |
| `ClassicMac.Graphics` (base) | The RGBA bitmap type, standard colour tables (`clut` 1–8, greys), colour-table and PixMap reading | Core (`MacRect`, `MacPoint`, `Fixed`, `PackBits`) |
| `.Fonts` | `NFNT`/`FONT`, `FOND`, `fctb`, `sfnt` | base |
| `.QuickTime` | ImageDescription, the codecs (`raw `, `rle `, `rpza`, `smc `, `cvid`, `8BPS`, `yuv2`, `YVU9`, `tga `), the codec plugin hook, QTIF files | Graphics, MacPaint (its `PNTG` codec) |
| MacPaint (in the base) | PNTG files and the `PNTG` codec's decoder | base |
| `.QuickDraw` | The renderer: GrafPort state, regions, shapes, patterns, transfer modes, CopyBits/StretchBits, text drawing, screen depths, with a public drawing API (`FrameRect`, `PaintRgn`, `CopyBits`, `DrawText`, …) on a canvas | Graphics, Fonts |
| `.Pict` | The PICT file format: the opcode reader that replays a picture into the renderer, and the writer | QuickDraw, QuickTime |
| `ClassicMac.Core`, `ClassicMac.Files` | The shared base and the file layer (unchanged by the merge) | nothing; Files on Core |
| `ClassicMac.Resources` (+ `.Decoders`) | Resource forks; icons, cursors and patterns become resource decoders here | Core; the decoders on Graphics, QuickDraw |
| `ClassicMac.Graphics.ImageSharp`, `ClassicMac.Graphics.SkiaSharp` | One integration package per host library, covering every image format (PICT, QTIF, MacPaint, icons) | `ClassicMac.Graphics` |

Colour tables and PixMaps sit in the base because QuickTime's codecs and QuickDraw both need them. QuickDraw.Pict's
own geometry (`PictRect`, points, fixed-point values) is replaced by Core's `MacRect`, `MacPoint` and `Fixed`, so the
graphics stack and the resource decoders share one set of types. At the merge,
`QuickDraw.Pict`, `QuickDraw.Pict.ImageSharp` and `QuickDraw.Pict.SkiaSharp` are deprecated on NuGet, pointing to
the new packages.

**Renderer and file format are separate (decided).** A picture is a recording of QuickDraw calls, which is why the two
grew up together, but other parts need the renderer without PICT: dialog previews drawn from `DLOG`/`DITL` the way the
Dialog Manager draws them, icons scaled with CopyBits, `cicn` masks, cursors and patterns at a screen depth, and any
later QuickDraw GX or 3DMF work reusing regions and colour. Exposing the renderer's drawing API (today internal and
shaped around PICT playback) is part of the merge work. The specification is split the same way (done, 2026-09-29): QuickDraw.Pict's
`PICT-FORMAT.md` became `docs/formats/PICT.md` (opcodes, operands, play state) and `docs/formats/QUICKDRAW.md` (the
drawing rules), with `QUICKTIME.md`, `MACPAINT.md` and `ICONS.md` beside them, one spec per format (since 2026-10-02 in
`docs/formats/graphics/` and `docs/formats/resources/`, with icons, icon families, cursors and patterns one file each).

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
  both forks; nested containers open in place. Opening reads one level: archives and disk images inside are read when
  their node is first expanded, and exports read them all (a 500 MB disk with 5,000 files opens in under a second).
- **Resources:** a file's resource fork as types → resources, with ID, name, size and attributes; search by type, ID
  or name.
- **Previews:** images (with a screen-depth switch), text, sound playback, font glyph sheet, dialogs drawn from
  `DLOG`/`DITL`, and a hex view for everything.
- **Export:** selected resources, a whole file or a whole volume, through the same code as the CLI; drag and drop out
  of the app.
- **Later:** editing, in stages (below), starting once `pack` round-trips cleanly.
- **App icon (built for Windows; packaging later):** the design session's icon (`design/icon/`, the diagonal-stripes
  monitor). The app carries `Assets/icon/classicmac.ico` (`ApplicationIcon`, `Window.Icon`) and the PNGs it draws: the
  title bar's hand-drawn 16, 20, 24 and 32 px versions picked by display scaling and drawn 1:1, and the About box's
  128. Packaging steps, not scripted yet: on macOS copy `classicmac.icns` into the bundle's `Contents/Resources` and set
  `CFBundleIconFile` to `classicmac`; on Linux install `png/classicmac-<n>.png` as
  `share/icons/hicolor/<n>x<n>/apps/classicmac.png` (16, 24, 32, 48, 64, 128, 256, 512) and `source/classicmac.svg`
  as `share/icons/hicolor/scalable/apps/classicmac.svg`, with `Icon=classicmac` in the `.desktop` file. The icon
  is kept as it is: the owner decided (2026-10-02) to use it although its six stripes echo Apple's old logo (it has no
  Apple shape).

**Structure:** the app adds nothing format-specific — it sits on the same `ClassicMac.Resources` and `Decoders`
packages as the CLI. MVVM with plain C# view-models (testable without a UI), one preview control per kind (image, hex,
text, sound, font, dialog) chosen by resource type, lazy opening and decoding so large disk images browse instantly;
sound playback is the only native dependency: SoundFlow 1.4.1 (MIT), which bundles miniaudio (MIT or public domain) for
Windows, Linux and macOS; only the app references it (behind `IAudioPlayer`), so the libraries stay pure.

**First version (built):** `src/ClassicMac.App` (Avalonia 12, Fluent theme, CommunityToolkit.Mvvm) — open files,
disk images and resource forks (menu, drag and drop, command line), browse the tree (input › containers › folders ›
files › resource types › resources, resources read on expand), details of the selection, and a diagnostics list
that leads to its node. View-models carry no Avalonia types and are tested; a headless test draws the window.
**Previews (built):** Details | Preview | Hex tabs. Previews come from the same decoders as `extract`: images
(zoom, screen depth, nearest-neighbour on a checkerboard), styled `TEXT` drawn with its fonts and colours (through the
public `StyledText` model), strings, decoders' JSON as labelled values in cards with a Properties | JSON switch (P2), PICT/TEXT files, dialogs, alerts and menus drawn in the System 7 style, and documents (DOCMaker, SimpleText with
pictures) a chapter at a time with their pictures reflowed as in the HTML output, at 72 dpi, picture links followed
(chapter, next, previous, back); hex for resources no preview shows (the Hex tab appears only for them, or while bytes are edited), read on demand. **Export (built):** Export menu and tree context menu, each command enabled for the nodes it applies to —
Save Resource As (decoded or `.bin`, current screen depth), Export Resources (a file, or one type, with a manifest),
Extract All Resources (input, container or folder), Convert Documents (the documents under an input, container,
folder or file, as HTML) and Unpack as AppleDouble or Basilisk II (input, container, folder or file). They run the CLI's code (`ClassicMac.Files.Export`) off the UI thread, one at a time, into a new subfolder
named after the item ("Disk unpacked", numbered when it exists), with progress in the status line and problems in the
diagnostics list. **Sound (built):** a `snd ` shows its waveform (one lane per channel) and details (exact rate, channels,
size, length, loop, base note) and plays through SoundFlow at its true pitch (converted to the device's 48 kHz
stereo); playback stops when the selection changes. The preview (P3, A1) shows the details as chips, one lane per channel with the loop band and a playhead that follows the player; a click on the waveform plays from there, Space plays and stops, and Repeat the loop loops the sound's loop through SoundFlow's loop points; a sound that cannot be decoded gets a card with Show in Hex and Save raw data. **Images (built, P1):** images show as cards with their type and size, cut into rows for the pane's width in a virtualising list (hundreds of images scroll smoothly). **Icons (built):** an icon resource's preview shows every member of its family (masks on request) and its suite as the Finder draws it (`IconSuite` through the QuickDraw renderer): plain, selected, disabled, offline and open, and the seven labels, captioned, at the preview's screen depth. **Folders (built):** a folder, a volume's root or a container read open previews as the Mac OS 9 Finder draws its window, by the rules traced in Finder 9.2.2 and matched pixel for pixel on Mac OS 9.0: HFS and HFS Plus folder records (`HfsReader.ReadFolders`) give the window (header pane and scroll bars included), scroll, view (large or small icons, large or small buttons; lists drawn as icons) and each icon's place, so folder art shows as it was arranged; items with no place are arranged in the view's grid as the Finder does; icons follow `GetIconRef` (custom icon, applications' bundles standing in for the desktop database, the System's icon mapping table and icons) with label tints and alias, lock and custom badges, from the same volume or the open files (none ship with ClassicMac); names in the views font (Geneva 10, or the volume's Finder Preferences) ([finder-windows.md](formats/file-systems/finder-windows.md)). **Next:** drag and drop out of the app, then the other previews
(fonts, dialogs) as their decoders arrive.

**Empty state and search (built 2026-10-02, design S5, S6):** with nothing open the inspector shows a drop zone and
the Recent list (10 files, newest first, kept in the settings; a missing file shows "Not found" and leaves the list on
click). The tree's filter (Ctrl+F) shows the rows whose shown name matches, with the rows above them opened and the
rows below them kept, and reads the containers not yet read so their contents can match; the type-ahead (typing in the
tree) steps through the matches among the loaded rows (F3, Shift+F3) without hiding any. Both skip the files in a
"No name" group.

**Tree display (built 2026-10-02, design T1–T3):** the tree leaves out files with the Finder's invisible flag
(`Icon
`, the desktop database; never folders: `Desktop Folder` and `Trash` show), with a footer "N invisible items
hidden · Show"; a folder's files whose names are empty or only whitespace (space, option-space, control characters)
fold into one collapsed "No name" node when there are two or more, their names shown with the whitespace made visible
as ASCII token chips (`sp`, `nbsp`, `tab`, `cr`, `lf`, `^X`; runs as `sp×3`; the bytes in hex on hover); one alone is titled "(no name)". A "Tree display" popover switches both (on by default). Only the tree
changes: each folder node keeps all its items for exports, previews, Details and the Volume commands. A third option, "Show details column"
(off by default), shows the rows' right-hand details (type · creator, sizes). Rows fit the pane (names trim, the chip
and details stay whole) and the tree's vertical scroll bar is laid out beside the rows (Fluent's overlay auto-hide is
off for the tree), so it never covers the details.

**Settings:** what the app remembers between sessions (the tree display options, the recent files) is kept as JSON in
`%AppData%/ClassicMac/settings.json` (`Environment.SpecialFolder.ApplicationData`, so `~/.config` on Linux and
`~/Library/Application Support` on macOS) through `ISettingsStore` (`JsonSettingsStore`; tests use
`MemorySettingsStore`). A missing or unreadable file gives the defaults; a failed write is ignored.

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

**Editor I design (confirmed 2026-09-29: `.orig` once per file, MacBinary saved as III, Ctrl+S Save and Ctrl+E Save Resource As):** **built** (2026-09-29): the library (`ClassicMac.Resources.Editing`: the edits, `EditSession`, the rules, fork comparison; `ClassicMac.Files.Editing.ForkSaver`: saving back and Save As, verified; [writing.md §3](formats/containers/writing.md#3-writing)) and the app (Edit and Resource menus, Save/Save As/Revert, prompts for unsaved edits). Hex editing is a dialog (the bytes as editable hex) for now; editing in the hex view itself is later.

- **Edits live in the library.** `ClassicMac.Resources.Editing`: each edit is a command on a `ResourceFork` that can be
  applied and undone (add, delete, duplicate, rename, renumber, set attributes, set data, set the fork's attributes), and
  an `EditSession` holds them with undo and redo stacks and a dirty flag. The app's view-models wrap it, so the rules are
  tested without a UI and the CLI can use them later.
- **Rules** [ClassicMac, checked against the Resource Manager where it has one]: a type and ID already in the fork is
  refused (the Resource Manager's `AddResource` does not check; ResEdit does); names are at most 255 bytes of Mac OS
  Roman; IDs below 128 get a warning (reserved for the system); the compressed attribute ($01) cannot be set by hand, and
  new data clears it; duplicating gives the next free ID from 128 up, with the same name.
- **Hex editing** (built 2026-09-29): Edit Bytes in the hex tab; hex digits overwrite the byte under the cursor two
  digits at a time (insert mode inserts; at the end they append), Delete and Backspace remove bytes, arrows, Home, End and
  Page keys move; Apply makes one undoable edit, Discard drops it. Moving the selection, undo, the Resource commands,
  "Edit with template", Save, closing and quitting with unapplied edits (here or in a form) first ask "Apply your
  changes to 'STR#' 128?" (Apply, Discard, Cancel). Editing the text column is
  not done. Replacing the data from a file is for anything larger.
- **What can be saved:** a raw fork file; a data file with its AppleDouble `._` file or its Basilisk II `.rsrc`/`.finf`
  companions; AppleSingle; MacBinary (written as MacBinary III); BinHex. Only the resource fork changes; the data fork,
  name and Finder info are written back as read. Files inside disk images and archives are read-only until Editor III:
  for them only *Save As*.
- **Saving:** the new file is written beside the original under a temporary name, read back with the same reader and
  compared with the session's fork (every resource's type, ID, name, attributes and data, the fork's attributes; the
  container's name, Finder info and data fork), then swapped in with `File.Replace`. The first save of a session keeps
  the original as `<name>.orig` (unless one exists); a save that fails verification leaves the original untouched and
  says why. A file changed on disk since it was opened (size or modification time) is not overwritten without asking.
- **Save As** writes to a new file in a chosen form: raw fork, AppleDouble pair, AppleSingle, MacBinary III, BinHex, or a
  Basilisk II folder entry.
- **Undo** keeps working across saves within the session; *Revert* reloads the file from disk.
- **UI:** a Resource menu and the tree's context menu — New Resource…, Duplicate, Delete, Get Info (type, ID, name,
  attributes), Replace Data from File…, and hex editing; File ▸ Save (Ctrl+S; *Save Resource As* moves to Ctrl+E), Save
  As…, Revert. A changed file is marked in the tree, and closing it or quitting with unsaved changes asks first.

## Ground truth and licensing

Apple's documentation decides first; where it is silent or ambiguous, the answer comes from disassembling the Mac OS
code that handles the format (the Resource Manager, Sound Manager, Icon Utilities, HFS) — never from another
implementation's guess. A rule fitted to real data instead is marked as such.

**Format documentation:** `docs/formats/` holds an implementer's specification per format, one file each in a category
folder (`containers/`, `archives/`, `disk-images/`, `file-systems/`, `resources/`, `graphics/`, `codecs/`, `output/`;
decided 2026-10-02, authoring rules in `docs/formats/CLAUDE.md`), each rule tagged with its source ([Doc], [Code], [Verified], [Author], [Fitted]). They are written in our own words, never cite the
private harness, and change in the same commit as the behaviour they describe.

The Resource Manager model is the native Mac OS 9 one (plus the 68k ROM where selected) without the trap patches that
extensions install — Multiple Users, Apple Menu Options, language packs, the Process Manager's font-release rule.
They change what running applications see, not what a file contains.

- **Specs:** *Inside Macintosh: More Macintosh Toolbox* (resource format), *Inside Macintosh: Files* (HFS),
  *Inside Macintosh II* (MFS), *Inside Macintosh: Devices* (Apple partition map), Apple's File Type Note $E0/$0005
  (Disk Copy 4.2),
  *Sound* (`snd `), Apple's *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note (versions 1 and 2;
  RFC 1740). No Apple code in Mac OS 7.1–9 reads or writes AppleSingle/AppleDouble (only mail clients and StuffIt
  do), so the Developer Note is the whole reference.
- **PC Exchange / File Exchange:** from the disassembly of PC Exchange 1.0.4 and File Exchange 3.0.2 (the same format
  in both), confirmed on a FAT12 disk in SheepShaver: browsing alone creates records (zero dates, `TEXT`/`dosa`);
  creation comes from the DOS entry, modification is the later of the DOS entry's and the record's; DOS keeps even
  seconds, and years from 2032 read as 128 years earlier (Mac 1904–1979). The only type mapping (File Exchange 3.0.2's
  own table is always empty) is Internet Config's map of name endings, applied to the `TEXT`/`dosa` placeholder and
  written back only when the data fork is closed, so readers show stored types and apply a map only when the app
  supplies one (`ExtensionMap`; no Apple-derived table ships). Names without a record come from the VFAT long name as
  File Exchange converts it: Mac Roman when every character maps (`:` kept), else each UTF-16 unit's low byte (`:` →
  `_`); over 31 bytes, the start, `#`, three hex digits of a CRC-16 and the extension. Garbage names in lazily made
  records are shown as the Mac shows them, with a warning. The record packing's cluster size is not stored and is
  found by trying the FAT sizes (fitted) — on a FAT volume the reader knows it. Floppies File Exchange wrote read as
  OS 9 listed them (local test against the harness logs).
- **FAT:** Microsoft's FAT specification (fatgen103): BPB, type by cluster count, 12/16/28-bit entries, VFAT long
  names; DOS partition tables by the standard MBR layout. Not Apple formats, so no Apple code decides them.
- **Raw CD sectors and cue sheets:** ECMA-130 for the sector layout (user data at +16 in mode 1, +24 in mode 2 form
  1, +8 in 2336-byte sectors; form 2 has none); the Apple CD driver never parses sectors (the drive does), so the
  2048-byte blocks are all the Mac sees (driver disassembly). Cue sheets follow the de facto CDRWIN format.
- **CD images:** ECMA-119 (ISO 9660) for the layout; the Mac view from the disassembly of Mac OS 9's ISO 9660 and
  High Sierra File Access 5.3, confirmed in SheepShaver on a test disc (every entry matched): only the primary
  descriptor (no Joliet, no Rock Ridge); root from the big-endian path table; type/creator from `BA`, or `AA`
  version 2 with flags masked to $B020 (never invisible), else `TEXT`/`hscd` (no extension map); an associated record
  is the resource fork and its Finder info wins; names cut to 31 bytes before `;1` is stripped, a final '.' dropped
  only up to 9 bytes; dates as local time (GMT offset ignored), clamped to 1904–2040; multi-extent files not joined;
  a name over 37 bytes or an empty file's record ends that sector's listing; icons in a six-column 64-pixel grid.
  Hybrid discs mount as HFS, as the Mac mounts them, because the HFS and partition-map readers go first.
- **Zip and tar:** Info-ZIP's `extrafld.txt` for the Mac extra fields; POSIX tar.
- **NDIF and ADC:** no Apple spec; from the disassembly of Disk Copy 6.3.3 (its `.HDI` driver and codecs; there is
  no separate extension on OS 9 and no UDIF support), confirmed on images it and Disk Copy 6.1.2 made in SheepShaver (each decodes to its
  source sectors, the CRC matches). The `bcem` header and its validator (what refuses a mount is refused, the rest
  reported), chunk types (zero, raw, KenCode, DART RLE, DART LZH, ADC, end, all decoded; KenCode
  confirmed on a "Smaller (KC)" image from Disk Copy's hidden Control-Save dialog, 512-sector chunks), ADC with its overrun check, the CRC-32 (reflected table from the normal polynomial, no final xor;
  hdiutil's "CRC28"; verified only when `VerifyChecksums` is set, as only Disk Copy's "Verify checksum" does), and segments found by their
  `bcm#` ID among `dseg` files in the folder, never by name. Disk Copy picks the format by file type (`dimg`,
  `rohd`, the older `hdro`); we find the `bcem` itself, so images that lost their type still open.
  - Map versions 10, 11 (ADC) and 12 (Disk Copy 6.5b13, confirmed on its images; the end entry's offset is 0) are
    read. `+$48` is the buffer size (chunk size plus the largest compression overrun); the chunk size is the user's
    choice (6.1.2: 32 sectors, 6.3.3: 512 by default) and any compressed image may mix raw chunks. KenCode shares the
    System's `dcmp` 3 codes but not its distance classes: every Disk Copy stays at class 10 from `$2A01`, where
    `dcmp` 3 moves to class 11 at `$5401`, so the two match only while matches start at or below `$5400`. Our KenCode
    decoder stays separate (byte-exact).
  - **Version 2 is read as Disk Copy 6.1.2's driver reads it** (decision, replacing the earlier refusal): that driver
    mounted hand-built version 2 images (raw and KenCode) with a valid checksum and refused ADC in them (-10). Layout:
    the version 10 header up to +$54, the count there (end entry included), 8-byte entries {start<<8|type, offset} from
    +$58, each chunk running to the next offset; types 0, 2 and $80–$82. (Disk Copy 6.3.3 reads version 2 as a blank
    disk, 6.5b13 rejects it, and ShrinkWrap 2.1 expects the 12-byte layout.) No real image has been seen, so an info
    diagnostic asks for one and records type/creator, `+$54` and the map size.
  - The CLI's `--verify` checks the CRC-32 (as Disk Copy's "Verify checksum" does).
  - Chunk type `$F0` (ShrinkWrap 3, per Aaru) and the `+$74`/`+$78` encryption fields are unverified: `$F0` is
    reported by name and reads as zeros; no image with either has been seen.
- **ShrinkWrap 2.1** writes nothing new: `dImg` is Disk Copy 4.2 byte for byte (junk after the name's Str63 is
  ignored), `hdrv` (volume image, Drive Container) and the self-mounting `APPL`/`sImg`/`iImg` are raw volumes in the
  data fork. All read today (harness run20). Disk Copy 6.0 (1994, `dCpy`)'s own RLE `dImg` variants are not decoded
  (optional). No UDIF samples yet: Disk Copy 6.5b13 offers UDIF only for devices (its hidden debug menu, Option at
  launch, has UDIF test items; its conversion fails on OS 9.0); they need OS 9.1–9.2.2, real 2000–2002 `.dmg` files
  or `hdiutil`. (Disk Copy cannot mount images on SheepShaver's shared volume, -8812.)
- **UDIF (built; `docs/formats/disk-images/udif.md`):** Disk Copy 6.5b13's read-only, compressed and "entire device"
  images decode to their source device exactly, every checksum matching; Mac OS X's XML-plist images are read as
  dmg2img describes them, tested on synthetic images (no OS X-made `.dmg` in the corpus yet). Runs: zeros, raw, ADC,
  zlib, bzip2 (our own decoder); LZFSE reads as zeros with an error; segmented and encrypted images are refused.
  - The `koly` trailer is the last 512 bytes.
  - Early images (Disk Copy 6.4/6.5) have XMLOffset/Length 0. Their RsrcForkOffset/Length point to a flattened
    resource fork inside the data fork. Its `blkx` resources hold the `mish` block tables that later images keep
    base64-encoded in the XML plist.
  - The reader reads that fork's map properly; dmg2img (GPL, reference only) just walks the blocks.
  - Checksums name their algorithm: type 2 CRC-32, type 4 MD5 ("entire device" images); verifying is optional.
    Read/write device images (`devr`) and CD-R masters (`GImg`/`CDr3`) are raw devices, read as raw.
  - `mish` runs are 0x28-byte entries from +0xCC. Types: 0 zero, 1 raw, 2 ignore, 0x80000004 ADC (the NDIF codec),
    0x80000005 zlib, 0x80000006 bzip2, 0x80000007 LZFSE, 0x7FFFFFFE comment, 0xFFFFFFFF end.
  - Encrypted images are recognised and refused, not parsed: `encrcdsa` at offset 0 (header v2), or `cdsaencr` at
    the end (v1) (VileFault).
  - HFVExplorer adds nothing (libhfs, GPL).
- **DART:** Disk Copy 6.3.3's DART reading (disassembly) confirmed on DART 1.5.3's own files (CiderPress2's test
  data): header and block lengths (RLE in words, LZH in bytes, −1 stored), 20,480 data + 480 tag bytes per block,
  "fast" RLE and "best" LZH (Okumura/Yoshizaki LZHUF with a zero-filled window whose tail carries between blocks; a
  block may end a tag byte short, zero-filled), `CKSM` 2 = Disk Copy 4.2 sum of the data and `CKSM` 1 of all the tags
  (a Disk Copy 4.2 header's tag sum skips the first 12 bytes, now confirmed).
- **Sound:** *Inside Macintosh: Sound* for the resource and headers; where it is silent, the Mac OS 9.0 Sound Manager
  (3.5.1) disassembly: numChannels is the word at +6; compressionID 0 is PCM whatever `format` says, 3/4 MACE, −1/−2
  use `format`, others refused; a codec's numFrames counts packets; SndPlay reads a format 2 sound's header right
  after the commands and never its offset (so Realmz's format 2 sounds, which point to 20, play). MACE 3:1/6:1 from
  SoundLib's Exp1to3/Exp1to6 (the PowerPC code OS 9 runs; its 6:1 rounds slightly differently from the 68k ROM's),
  8-bit output as the Sound Manager gives it; IMA4 from the `ima4` decompressor, including its preamble rule (read at
  the start of each 16-packet batch, only when it differs from the running state). All checked byte for byte against
  the Sound Manager's own decoding (harness run22). The loop and base note are used only for instrument playback;
  they are kept in WAV's `smpl` chunk as information.
- **Formats with no Apple spec or Mac OS code:** MacBinary I/II/III and BinHex 4.0 follow their authors' published
  specifications (BinHex also RFC 1741); Basilisk II's shared-folder layout follows the emulator's behaviour (its GPL
  source is reference only), checked in the SheepShaver harness (Windows build): name bytes pass 1:1 through
  Windows-1252 (no Unicode conversion), only `% ? * " < > |` are escaped as `%XX` and any `%XX` is decoded, so names
  it cannot store (`/ \ :`, a trailing dot or space, device names) are replaced when unpacking, with a warning; folder
  Finder info lives in the parent's `.finf`. Detection heuristics and name mappings fitted to real files are marked
  in the code.
- **Behavioural references:** resource_dasm (MIT) and other open tools for container and `dcmp` edge cases. Its
  `dcmp` 3 and the decoding halves of its 68k and PowerPC emulators were ported (phase 11), with notices in
  `THIRD-PARTY-NOTICES.md`.
- **Licence:** MIT, with third-party notices for anything ported; no Apple code or files in the repo.

### Testing

- **Fixtures in the repo are synthetic:** built by our own writer inside the tests, or from Rez source compiled with
  Apple's Rez through `mpw` (the output is ours, not an Apple file).
- **Rez fixtures are compiled locally, not in CI:** MPW is Apple software and never enters the repo or CI. The `.r`
  source and the compiled fork are both committed; a script regenerates them on a machine where `MPW_ROOT` points to
  an MPW install and records each source's hash beside its fork; CI checks the hashes so a stale fork fails the build.
- **Real-file corpus outside the repo:** system files, applications, games such as Realmz, shareware and the
  harness's samples. `CLASSICMAC_CORPUS` names one or more folders, separated by `;`; the tests that use it skip when
  it is absent. Only names, counts and hashes of corpus results are committed, never the files or their decoded output.
  A folder holding a `.classicmac-damage-test` file (or named `ndiftest`) holds deliberately damaged inputs: tests that
  expect clean reads skip it, the tests written for it read it.
- **Checks:**
  - Round trip: read → write → read gives the same model, and canonical forks are byte-identical.
  - **Golden outputs** (`tests/ClassicMac.Resources.Decoders.Tests/Golden/`): a fixture fork made in code, with one
    resource per decoder, type and variant. Text outputs are committed as files, images and sounds as SHA-256 and
    length, with each fixture's decoder and diagnostic codes. The export manifest is pinned too, and a coverage check
    fails when a type a decoder handles has no fixture. `CLASSICMAC_UPDATE_GOLDEN=1` regenerates them; review the
    change in git.
  - **Corpus export** (`CorpusExportTests`): every input exports with the built-in decoders.
    - Failures: decoder errors or exceptions, a resource of a decoded type left raw, a manifest hash mismatch.
    - Damaged inputs and warnings are reported, not failed.
    - Explained exceptions go in `Corpus/allowlist.json`.
    - `Corpus/baseline.json` holds per input (name and SHA-256) its counts and one hash of all its outputs, so any
      change in decoded output names the input. `CLASSICMAC_UPDATE_BASELINE=1` rewrites it.
  - Later, separately: corpus output compared with resource_dasm and DeRez.
- **Tooling:** xUnit v3 on Microsoft.Testing.Platform (`dotnet test --solution ClassicMac.slnx`); CI on GitHub
  Actions for Windows, Linux and macOS.

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
| Sound | `snd ` formats 1/2, uncompressed PCM only (MACE and IMA4 throw) | Taken: the header walk, re-checked against *Inside Macintosh: Sound* (it read the extended header's sample size from the compressed header's place and ignored `bufferCmd`'s offset). Its note that it follows resource_dasm, and resource_dasm's MACE (from FFmpeg, LGPL), are reference only |
| Text | `TEXT` and `STR#` (JSON, 0-based) in MacRoman | The `STR#` layout |
| Fonts | `sfnt` → TTF, adding a Windows Unicode `cmap` and a missing `OS/2` table so modern loaders accept it | A "make it loadable" option for exported fonts |
| UI | `DLOG`, `DITL`, `WIND`, `CNTL`, `MENU`, `MBAR` → JSON | The field layouts, re-checked against *Inside Macintosh* |
| Output | A folder per category (`pictures/`, `sounds/`, …), 5-digit IDs, no manifest | Replace with per-type folders and a manifest |

### Prior art: other projects

No existing project combines containers, faithful conversion, writing and a cross-platform GUI — but each covers a
slice worth learning from. Licences matter: MIT code may be reused with notice; GPL and LGPL code is reference only.

| Project | Language, licence | Covers | Use for us |
| --- | --- | --- | --- |
| [ResourceForkReader](https://github.com/hughbe/ResourceForkReader) | C#, MIT; NuGet, active since 2025 | Reads raw forks; typed parsers for ~130 resource types; no `dcmp`, containers, conversion or writing | Cross-check record layouts and parsing |
| [HfsReader](https://github.com/hughbe/HfsReader) / [MfsReader](https://github.com/hughbe/MfsReader) | C#, MIT; NuGet | Read HFS and MFS disk images | Cross-check for the disk-image layer |
| [macresources](https://github.com/elliotnunn/macresources) | Python, MIT; dormant since 2020 | Rez-style text dumps, round trip, BinHex, `dcmp` 2 (GreggyBits) | Round-trip design; `dcmp` 2 reference |
| [machfs](https://github.com/elliotnunn/machfs) | Python, MIT | Reads and writes HFS volumes | Reference for writing HFS images |
| [resource_dasm](https://github.com/fuzziqersoftware/resource_dasm) | C++, MIT; very active | Decodes a very wide range of resource types to modern formats; `dcmp` via 68k emulation; disassembly | Widest coverage to compare output against |
| Claunia.RsrcFork (Aaru) | C#; NuGet, ~16k downloads | Resource fork reading for the [Aaru](https://github.com/aaru-dps/Aaru) preservation suite | Cross-check; Aaru (GPL-3 overall, some files LGPL) for disk-image formats, NDIF/ADC, DART, FAT and its host-folder PC Exchange filter: reference only |
| [HFSExplorer](https://github.com/unsound/hfsexplorer) | Java, GPL-3 | GUI browser for HFS/HFS+ images, extracts both forks | UX reference for the viewer |
| [XADMaster](https://github.com/MacPaw/XADMaster) | C/Objective-C, LGPL-2.1 | The Unarchiver's engine: StuffIt, Compact Pro, BinHex, MacBinary | Reference for archive formats |
| [mpw](https://github.com/ksherlock/mpw) | C | Runs Apple's MPW tools (Rez, DeRez) on modern systems | Ground truth: compile/decompile with Apple's own Rez |
| [DiscUtils](https://github.com/LTRData/DiscUtils) (LTRData fork) | C#, MIT; NuGet `LTRData.DiscUtils.Fat`, active | Reads, formats and writes FAT12/16/32 with long names; nothing Mac | Cross-check for the FAT layer; candidate base for FAT writing |
| DiscUtils (other packages) | C#, MIT | ISO 9660/Joliet/Rock Ridge, HFS+, UDIF `.dmg` (zlib, ADC) | Cross-check or port (with notice) for CD images, HFS+ and UDIF |
| hughbe's [StuffItReader](https://github.com/hughbe/StuffItReader), DiskCopyReader and Apple II readers | C#, MIT | StuffIt headers (v1, v5) with store and LZW only; Disk Copy images; ProDOS, DOS 3.3, WOZ | Cross-check for archive headers and disk images |
| [CiderPress2](https://github.com/fadden/CiderPress2) | C#, Apache-2.0 | Apple II disk images and archives (ShrinkIt, Binary II), some Mac formats | Reusable with NOTICE; reference for Apple II formats if ever wanted |
| [deark](https://github.com/jsummers/deark) | C, MIT-style | Many old formats, partial StuffIt and Compact Pro | Reusable with notice; archive cross-check |
| [dmg2img](https://github.com/Lekensteyn/dmg2img), [libdmg-hfsplus](https://github.com/planetbeing/libdmg-hfsplus) | C, GPL | UDIF `.dmg` decoding | Reference only |
| macutils | C, licence unclear | `unsit`, `macunpack` (StuffIt, Compact Pro, PackIt) | Reference only |
| [ResourceForker](https://github.com/csammis/ResourceForker) | C; dormant since 2016 | Small utilities to split resource forks | Minor reference |

## Phases

Each phase ships something usable and ends when its exit check passes; no dates set yet.

**Status (2026-10-02):**

| # | Phase | Status |
|---|---|---|
| 1 | Core | Done |
| 2 | Disk images | Done |
| 3 | Decoders I | Done (exit passed; DOCMaker and SimpleText built after) |
| 4 | Viewer app | Done (drag-out built) |
| 5 | Decoders II | Done (exit passed) |
| 6 | Editor I | Done |
| 7 | Editor II | Done (forms, import, templates with toggle, in-view hex editing) |
| 8 | Editor III (HFS writing) | Done for plain HFS: the library replaces forks and creates and deletes files and folders (with constrained B-tree growth); the app creates, imports and deletes them too, and Save As saves all edits into a verified copy of the image |
| 9 | Merge (QuickDraw.Pict) | Done; NuGet publishing is the owner's step |
| 10 | HFS+ and archives | Done (exit passed 2026-10-01); fixtures still missing for the fitted methods (Todo) |
| 11 | Code (`ClassicMac.Code`) | Done (exit passed 2026-10-02): PEF, `cfrg`, 68k applications and code resources, both disassemblers, `extract`'s code decoders, `disasm` and the viewer's listing |
| 12 | Runtime (`ClassicMac.Runtime`) | Idea, not started (after phase 11) |

1. **Core** — `ClassicMac.Core`; `ClassicMac.Resources`: resource map read/write, `dcmp` 0/1/2/3;
   `ClassicMac.Files`: Finder info, AppleDouble/AppleSingle, MacBinary, BinHex, Basilisk II shared folders; raw forks;
   CLI `list` and raw `extract`. *Exit:* read → write → read gives the same model on every corpus fork, and canonical
   forks come back byte for byte.
2. **Disk images** — `ClassicMac.Files.Hfs`: HFS and MFS volumes, raw or in DiskCopy 4.2 or behind an Apple partition
   map; `ClassicMac.Files.Fat` (FAT volumes with PC Exchange / File Exchange data, DOS partition tables) (built); then
   NDIF (with ADC in `ClassicMac.Files.Compression`), DART and UDIF `.dmg` (zlib, bzip2, ADC; LZFSE if needed) (built); CD
   images (`ClassicMac.Files.Iso`: ISO 9660, High Sierra, raw sectors, cue sheets and multisession built);
   self-mounting `.smi` images read as NDIF (built); recursive unwrapping through all of them (built); zip, tar and gzip with
   Mac data, uuencode and `.sea` self-extractors (built). *Exit:* every file of the corpus images (`RealmzClassicHD.img` and the other HFS
   images) lists and unpacks with both forks and Finder info, and file and folder counts match each volume's
   (`classicmac unpack`, built: every corpus image unpacks and reads back identically). The classic HFS reader also
   reports damaged B-tree headers and node maps, out-of-order or duplicate keys and IDs, and malformed or misplaced
   extents-overflow records as diagnostics while reading on ([hfs.md §5.3](formats/file-systems/hfs.md#53-checks-and-recovery), [§6](formats/file-systems/hfs.md#6-diagnostics)).
3. **Decoders I** — images through QuickDraw.Pict; text (`STR `, `STR#`, `TEXT` + `styl`, `vers`); `snd ` to WAV
   including MACE and IMA4; the manifest; document decoders for SimpleText and DOCMaker. Text decoders built (with the
   decoder interface and `extract` decoding by default); image decoders built through QuickDraw.Pict (NuGet), with a
   pixel limit and `--screen-depth`; `snd ` to WAV built (PCM, MACE, IMA4, µ-law; every corpus sound decodes).
   *Exit:* golden outputs pass and the corpus exports without errors. **Done:** golden outputs for every decoder,
   and the corpus (Realmz, the Divinity manual, the harness runs: 321 inputs, 13,890 resources) exports with no decoder
   failure, pinned by a committed baseline. DOCMaker was deferred at the exit and has since been built: DOCMaker
   and SimpleText documents to HTML, in `extract`, the `convert` command and the viewer
   ([documents.md](formats/resources/documents.md), [html.md](formats/output/html.md)).
4. **Viewer app** — read-only: browse disk images, files and resources with previews and export; grows with later
   decoders. First version built (browse, details, diagnostics, previews, hex, export, drag-out).
5. **Decoders II** — UI resources to JSON and dialog previews, then fonts; palettes and Finder resources; `pack`.
   *Exit:* extract → `pack` is byte-identical for unchanged resources. **Built:** UI resources and their colour and
   extension resources to JSON with dialog, alert and menu previews ([formats/README.md](formats/README.md#resources): menus, windows and dialogs, dialog items, controls),
   palettes, Finder resources, and `pack` with MacBinary III, BinHex 4.0 and AppleSingle writers; the exit check passes
   on the whole corpus (the corpus test packs every export back); fonts through the new `ClassicMac.Graphics.Fonts`
   ([bitmap-fonts.md](formats/resources/bitmap-fonts.md), [font-families.md](formats/resources/font-families.md), [outline-fonts.md](formats/resources/outline-fonts.md)).
6. **Editor I** — resource-level edits and saving back into forks and single-file containers. **Built** (2026-09-29).
7. **Editor II** — typed editors and PNG/WAV import (image and sound encoders). **Built** (2026-09-29): the Edit tab's forms for `STR `, `STR#`, `TEXT` (its `styl` kept in step), `vers` and the UI templates (`DLOG`, `DITL`, `ALRT`, `MENU`, `WIND`, `CNTL`, with the dialog or menu preview redrawn as the form changes), applied as undoable edits; and import (Resource ▸ Import Image or Sound): an image (PNG, JPEG, BMP, GIF, through Avalonia) becomes a `PICT`, `cicn`, icon (`ICON`, `ICN#`, `icl4`/`icl8`, the small and mini icons, or a whole icon family) or cursor (`CURS`, `crsr`), a WAV file a `snd ` (`ImageImport`, `SoundImport`; [icons.md](formats/resources/icons.md), [pict.md §3](formats/graphics/pict.md#3-writing), [sound.md §3](formats/resources/sound.md#3-writing)), replacing the data of a resource of that type and ID after asking. **Templates** (2026-09-29): a resource with no form of its own is edited through a `TMPL` found by name in its own file or any other open file (so a user's copy of ResEdit supplies ResEdit's templates; ClassicMac ships none; a check box shows a resource that has a form of its own through its `TMPL` too), read and written as ResEdit 2.1.3's template editor does ([templates.md](formats/resources/templates.md); all 471 templated resources in ResEdit's own fork read and write back byte for byte).
8. **Editor III** — writing HFS disk images. Build a verified HFS volume writer and connect it to the editor, preserving
   the other fork and Finder metadata when a file is edited. Work in these increments:
   - First, replace the data or resource fork of an existing file in a plain HFS volume. Read the original
     volume, write to a separate output, then reopen it and verify the changed fork and all unaffected file metadata.
     Reject unsupported wrappers, malformed structures, software-locked volumes and changes that cannot be represented safely;
     never partially modify the source image.
   - The writer can grow a fork through free bitmap blocks, initialize an empty extents-overflow tree when it has a
     mapped free node, insert records in key order, split full leaves and propagate splits through index nodes using
     already mapped free nodes. Insertion before the first indexed key and deletion of the final overflow record are
     supported by rebuilding the extents tree and its node map. The tree file can take additional free allocation blocks
     with linked map nodes when the header map fills, while its three primary extent descriptors can represent them. On shrink
     it reclaims trailing blocks and fully emptied descriptors, including overflow records. It updates `drFreeBks`, file
     physical EOF and volume write metadata. Apple's `ExtendFileC` disallows extending the extents-overflow file beyond
     its three primary extents, so the writer rejects that case. Keep edits copy-on-write until the completed image passes
     verification.
   - File and folder creation and deletion now maintain catalog records, folder threads and valences, B-tree nodes,
     fork extents, allocation bitmap and MDB counts. Creation grows the catalog tree, adds linked B-tree map nodes when
     needed, and stores additional catalog extents in the extents-overflow tree after its three primary runs are full.
     Creation can preserve caller-supplied Mac creation and modification dates. Deletion refuses locked files and nonempty folders. Name lookup and ordering now use the complete `_RelString`
     weight rules, independently checked against Apple's 256-entry compare table. The writer validates catalog and
     extents B-tree structure, catalog ID uniqueness, bitmap ownership, free counts and folder valences before and after edits.
   - The editor's Save As writes a verified copy of a plain HFS image with the edited fork replaced. File and folder
     creation, import and deletion (`HfsWriter.CreateFile`, `DeleteFile`, `CreateFolder`, `DeleteFolder`) are in the
     app's Volume menu, pending until Save As like fork edits. Handle partitioned and Disk Copy images only after their outer-container write
     strategy is specified. MFS, HFS+, and archives remain read-only in this phase.
   - Feature tests cover both forks, metadata and dates, nested paths, creation/deletion, B-tree and bitmap growth,
     fragmentation, malformed structures, error atomicity and interleaved edits. An app-level test edits a nested
     resource and reopens the saved image. Edited real HFS images mount and export with both Distrotech/hfsutils and
     JotaRandom/hfsutils, with exported contents compared byte for byte.
   *Exit:* edit, save, reopen and compare the target volume; verify both forks, Finder info, dates, folder paths and
   counts, then confirm unrelated files are byte-identical at the logical-file level. Source images remain untouched
   on errors. Update [hfs.md](formats/file-systems/hfs.md) with each implemented write rule.
9. **Merge** — QuickDraw.Pict moves into the ClassicMac repo, split into the target layering (Graphics, QuickTime,
   MacPaint, the QuickDraw renderer with a public drawing API, the PICT format, one ImageSharp and one SkiaSharp
   package); the old packages are deprecated. Brought forward, in stages: **1. moved in with its history (done,
   2026-09-29)**, the decoders using it directly; **2. layered as one `ClassicMac.Graphics` package with a namespace per
   layer (done, 2026-09-29)**; 3. shared types (Core's geometry; the renderer on `.Fonts`, done 2026-09-29, pixel-identical
   on the corpus's fonts; icons as resource decoders) and a public drawing API (`QuickDrawPort`, built 2026-09-29; `DrawPicture` onto a port built; spec split done). The old repo is archived and the
   NuGet packages deprecated when the new ones are published (by the owner).
10. **HFS+ and archives** — read-only: HFS+ and HFSX volumes (also inside an HFS wrapper) in
    `ClassicMac.Files.Hfs`, and the classic archive formats in `ClassicMac.Files.Archives`. The rules each reader follows
    are in [hfs-plus.md](formats/file-systems/hfs-plus.md) and the [archives documents](formats/README.md#archives);
    this entry tracks only what is built and what is verified. "Fitted" means built from published descriptions or
    other implementations and hand-built vectors, without an archive made by the original application.

    | Format | Built | Verified against the original application | Remaining |
    |---|---|---|---|
    | HFS+ / HFSX | Catalog files with Unicode paths, Finder info, dates and both forks (with overflow extents); symbolic links; file and directory hard links; journal metadata checked, not replayed; a bounded read-path validation of TN1150 (§11) | A journaled Mac OS X image from Digital Corpora's `nps-2009-hfsjtest1` (optional test) | Broader cross-version and layout interoperability. `fsck_hfs` parity and repair are not targets |
    | StuffIt 1.x–4 | v1 sequential records with folder markers, v2 linked entries; stored, method 6 (fixed Huffman + PackBits), method 13; `SitC` comments | StuffIt Deluxe 4.5 (v2 traversal, method 13); a `.comment.sit` with its AppleDouble companion (`SitC`); StuffIt 1.5.1 (v1 with folders, methods 0, 1, 2, 3) | Method 6 (StuffIt 1.5.1 cannot write it) |
    | StuffIt 5 | Stored, RLE90 (1), LZW (2), Huffman (3), LZAH (5), method 6, LZMW (8), LZ + Huffman (13), Installer (14), Arsenic (15); encrypted archives reported, not opened | Deluxe 6.5 and 7.0, Mac OS 9 and Mac OS X variants (listings and exact forks); method 15 with Deluxe 6.5.1; DropStuff 7.0.3 (methods 13, 15; folder end before its members); StuffIt 7.0 return receipts and Windows archives (CC0 corpus) | Method 14 (hand-built vectors only) |
    | StuffIt split files | SegmentIt (`$B056`) and StuffIt 1.5.1 (`$41A7`) segments reassembled, both forks and Finder info, the `.sit` inside unwrapped | StuffIt 1.5.1's Segment command | An original SegmentIt volume set |
    | Compact Pro | Directory and folders; RLE and LZH + RLE forks; multi-volume sets through sibling files, missing volumes reported per entry; comments as diagnostics | Compact Pro 1.52: an archive, its `.sea` and a segmented set; munbox's 1.33/1.52 samples; RLE half-escape rule from real archives | Sets split across floppies while saving |
    | PackIt | Stored (`PMag`), XOR- and DES-encrypted stored (`PMa1`, `PMa2`), Huffman (`PMa4`), encrypted Huffman (`PMa5`, `PMa6`) | Stored (`PMag`) with PackIt 1.0 | `PMa4` and the encrypted records (PackIt III does not run on Mac OS 9); reserved `PMa3`/`PMa7` |
    | DiskDoubler | DDAR stored entries; DDA2 archives and standalone files with methods 0–10; delta types 1 and 2; header CRCs (fitted) | Standalone methods 1 and 8 (DiskDoubler 3.7.7) and 6, 9, 10 (Pro 4.1.1); 6, 9, 10 in DDA2 records; a Pro 4.1.1 DDA2 archive (its `0x1000` layout fitted); the whole CC0 DiskDoubler corpus (3.7.7 and Pro 4.1.1, `.dd`, `.sea`), DDA2 record CRCs fitted to it | Methods 2, 3, 4, 5, 7 and the delta types (no sample uses them) |
    | LHA | Header levels 0–3; `-lh0-`, `-lh1-`/`-lh2-`, `-lh3-`, `-lzs-`/`-lz5-`, `-lh4-` to `-lh7-` | MacLHA 2.24 (lhasa test suite): levels 0–2, `-lh0-`, `-lh1-`, `-lh5-`, MacBinary entries, paths | — |

    Other segmented archive formats remain, and anything from row 5 of Inputs is built only on request.
    *Exit:* every format in the table reads its original-application fixtures with both forks and Finder info byte for
    byte, a method still without one is marked fitted in its [archive document](formats/README.md#archives), the journaled
    HFS+ image lists and reads, and the corpus unpack baseline holds. **Passed 2026-10-01:** the Files tests with every
    original-app fixture, every remaining method marked **[Fitted]** or **[Reference]**, the `nps-2009-hfsjtest1` image
    (SHA-256 pinned), and the corpus baseline over the Realmz, harness and `.rsrc` corpora. The table's Remaining column
    is the Todo item below.
11. **Code** — `ClassicMac.Code`: readers for classic Mac code, the 68k and PowerPC disassemblers, and their use in
    `extract`, the new `disasm` command and the viewer. The rules each reader follows are in the
    [code documents](formats/README.md#code) and [disassembly.md](formats/output/disassembly.md); this entry tracks
    what is built and what is verified. The corpus facts are asserted by tests gated by `CLASSICMAC_CODE_CORPUS`; the
    samples (Apple's and other vendors' code) are never committed.

    | Format | Built | Verified against | Remaining |
    |---|---|---|---|
    | PEF | Header and sections; pattern-initialized data (ops 0–4); the loader: imported libraries and symbols (weak, class), all relocation opcodes, the export hash and lookup by name, re-exported and absolute exports, main/init/term; transition vectors; traceback tables | NQD (199 imports, 269 exports found by their hash), its alternative build, the Font Manager, Disk Copy 6.1.2 (main 1:$BB8) and 6.5, all 90 Mac OS 9.2.2 fragments, and the System file's 114 native PEF resources (kind 3 and SECN in `'ncod'` 3); 288 containers give the 117,236 fixups the Code Fragment Manager's own relocation interpreter gives | Section kinds 5, 6, 8 and the CDIS, LRPT, LSEC opcodes (no sample); CFM-68K containers |
    | `cfrg` | Version 1 members, name padding, extensions, the `0x30EE` search extension | The Mac OS 9 System file: 12 `cfrg` resources, 162 members, each landing on a fragment in the data fork; Disk Copy 6.1.2's member naming its data fork | Locators other than the data fork (no sample) |
    | 68k applications | `CODE` 0 and every jump-table entry form; near and far headers; the entry point, with the bootstrap shape's saved entry; the MPW near, MPW far, Retro68 and CodeWarrior models | ResEdit 2.1.3 (MPW near: 468 entries, entry CODE 66 +$C, saved entry CODE 1 +$3634); Disk Copy 6.1.2 (MPW far: 88 A5 and 33 PC relocations in CODE 1); Realmz 7.1.2 (CodeWarrior); QDHarness (Retro68) | The far lists' `$80 $00` long form and loaded far entries (no sample) |
    | Data initialisers | MPW `%A5Init`, CodeWarrior `DATA` 0 (3 blocks, 6 relocation lists), Retro68 `RELA` | ResEdit's `%A5Init` (55 runs, 58 relocations); Realmz's `DATA` 0 (25/10/0/26862/391/0); QDHarness, its old build (1291 `RELA` relocations in `Runtime`) | THINK C and Symantec; multi-segment CodeWarrior |
    | Code resources | The standard header, `DRVR`, the `$A9FF` package form (fitted), `thng` components, `$AAFE` routine descriptors with their PEF, native PEF resources | The System file: 41 fat descriptors each with a PEF, its `DRVR`s and the non-standard ATADisk, its packages and components; Disk Copy 6.1.2's `.HDI` driver | `gpch`, `ptch` bodies, `scod`, `boot` and other shapes |
    | Names | Trap, selector and low-memory tables generated from Multiversal Interfaces (`tools/TrapTables`), MacsBug names in their three encodings | ResEdit, Realmz (485 MacsBug names: 341 variable, 144 fixed-8 without bit 7, as resource_dasm finds), Disk Copy 6.1.2, QDHarness | — |
    | 68k disassembler | Ported from resource_dasm: 68000, the 020/030 integer extensions, the 040's cache and `move16`, `movec`/`moves`, the FPU; recursive descent, switch tables, MacsBug-bounded functions, the gap sweep; the annotator | The instruction vectors; ResEdit's CODE 1 (all 145 `jsr n(A5)` resolve); Realmz's CODE 1 | An optional comparison with resource_dasm's listings, syntax normalised (not run) |
    | PowerPC disassembler | Ported from resource_dasm: UISA, FP, the OEA operations drivers use, AltiVec, extended mnemonics; traceback names, glue to `lib::name`, TOC slots, transition vectors | The instruction vectors; Disk Copy 6.1.2 (440 glue stubs), Disk Copy 6.5 (686 glue stubs, 1619 traceback names), NQD | Code is listed linearly, not by descent; data sections are not disassembled |
    | Integration | Decoders `code.segment`, `code.cfrg`, `code.resource` (the data stays the `.bin` main file, with `.s` and `.json`); `pack` takes a `.bin` main file back; `disasm`; the viewer's listing preview | Every code resource of ResEdit, Realmz, QDHarness, Disk Copy 6.1.2 and the System file decodes with no error, and `disasm` lists them (the System's 162 data-fork fragments included); the corpus export with raw copies packs back byte for byte | — |

    *Exit:* every corpus sample's facts match:
    - **ResEdit:** 468 jump-table entries; entry CODE 66 +$C; saved entry CODE 1 +$3634; `%A5Init` with 55 runs and
      58 relocations; all 145 `jsr n(A5)` resolve.
    - **Realmz:** CodeWarrior `DATA` 0 with relocation counts 25/10/0/26862/391/0, and 485 MacsBug names
      (341 variable, 144 fixed-8 with bit 7 clear; resource_dasm finds the same 485).
    - **QDHarness** (its old build, the one the counts were taken on): 1291 `RELA` relocations.
    - **Disk Copy 6.1.2:** MPW far, with 88 A5 and 33 PC relocations; its `cfrg` member; PEF main 1:$BB8; 440 glue
      stubs.
    - **NQD:** 199 imports and 269 exports, found by their hash.
    - **The 90 Mac OS 9.2.2 fragments:** all verify.
    - **Disk Copy 6.5:** 1619 traceback names and 686 glue stubs.
    - **The System file:** 12 `cfrg` resources with 162 members, and 41 fat descriptors.

    Also: every corpus `CODE` and PEF disassembles with no decoder error, and the instruction vectors pass. **Passed
    2026-10-02:** `ClassicMac.Code.Tests` with `CLASSICMAC_CODE_CORPUS` set (1449 tests, none skipped), the decoders'
    `CodeCorpusTests` (every code resource of the five samples decoded and listed with no error), and the
    `CLASSICMAC_CORPUS` export, where no code resource fails or is left raw and every export packs back (its only
    failures are five `icns` resources left raw, which predate this phase). The optional comparison with
    resource_dasm's listings was not run.
12. **Runtime** — `ClassicMac.Runtime`: run classic Mac applications without Apple's ROM or System, by high-level
    emulation. **Idea, not started; depends on phase 11.**
    - Approach: the application's code runs on a CPU core; the Toolbox is ClassicMac's own .NET implementation, the
      way Executor and Advanced Mac Substitute work. A full-machine emulator (Mini vMac, Basilisk II, SheepShaver)
      needs Apple's ROM and System and uses none of this code, so it is not the goal.
    - Already built: QuickDraw (pixel-exact, both QuickDraws), the Resource Manager's reading, the Font Manager's
      choice and bitmap text, HFS/MFS reading and HFS writing (the File Manager's backing), the interface templates,
      the Platinum dialog drawing and icon drawing; phase 11's `CODE`, jump-table and `%A5Init` loading.
    - To build, in order:
      1. A 68k interpreter: a C# port of syn68k's interpreter (MIT, 68LC040; Executor's core), Moira (MIT), or the
         emulator half of resource_dasm's `M68KEmulator` (MIT), whose decoding half phase 11 ported (its
         `PPC32Emulator` is the matching candidate for PowerPC); the
         A-trap dispatcher with `SetTrapAddress`, and the low-memory globals at their fixed addresses. Trap numbers,
         signatures, structs and globals generated from Multiversal Interfaces (Executor's API definitions in YAML;
         check its licence before use) rather than typed in.
      2. The Memory Manager (zones, handles, master pointers, locking and purging), Segment Loader and Resource
         Manager calls.
      3. A headless harness: run an application's code, log every trap, draw into an `RgbaBitmap`; a trap census over
         the corpus picks which traps to write first.
      4. The Event, Window, Menu, Control, Dialog and TextEdit Managers, Scrap and Standard File; built-in definition
         procedures native, an application's own `WDEF`/`CDEF`/`MDEF`/`LDEF` run as 68k code.
      5. The File Manager over host folders and disk images; the Sound Manager; printing stubbed.
      6. An Avalonia window with input, the screen as a `QuickDrawPort` canvas.
    - Target: System 6/7-era 68k applications, one at a time (a small Realmz utility or game first), implementing only
      the traps each uses.
    - PowerPC (later): PEF code calls the Toolbox by name through the Code Fragment Manager, so a ClassicMac
      `InterfaceLib` binds its exports to the same .NET managers. It needs a PowerPC core (FPU; AltiVec for later
      applications) and mixed mode, since PowerPC applications still carry 68k code (definition procedures, plug-ins)
      through routine descriptors.
    - Ground truth: the Mac OS 9 harness answers each trap's behaviour, as it did for QuickDraw; Inside Macintosh is
      the specification.
    - Hard parts: undocumented behaviour and private globals, applications that write the screen directly or read
      the ROM, timing, copy protection, and the size of the API (most applications use 150–300 traps).
    - References: Executor 2000 (github.com/autc04/executor, MIT-style; its cxmon debugger is GPL and stays out) is
      the portable guide to the managers and how they fit together, with a notice in `THIRD-PARTY-NOTICES.md` for
      anything ported. Its behaviour is a 1990s clean-room guess, so Inside Macintosh and the Mac OS 9 harness still
      decide. Its PowerPC core (PowerCore) is incomplete and unlicensed. Advanced Mac Substitute (AGPL 3) and the GPL
      emulators are reference only: their behaviour may be studied, their code never copied.

### Todo (not in a phase)

- [x] **Verify imported icons and cursors on Mac OS 9** (`ImageImport` output loaded by a real Resource Manager): all
  14 types load; PlotIconID draws every family member with 0 mismatched pixels at depths 1–32 (2026-10-02).
- [x] **Real Dialog Manager previews** for `DLOG`/`ALRT`: drawn as Mac OS 9.0 with Appearance (Platinum) draws them,
  every non-text pixel matching the captures ([windows-dialogs.md §2.4](formats/resources/windows-dialogs.md#24-how-mac-os-90-draws-dialogs-and-alerts); 2026-10-02).
- [x] **8-bit icon masks in icon suites** (Mac OS 9's CopyMask through an 8-bit mask in `IconSuite.Plot`), from a Mac OS 9
  oracle run (2026-10-02).
- [ ] **Publish the NuGet packages** (owner's step; then deprecate the QuickDraw.Pict ones).
- [x] **`.sea` self-extracting archive detection** (phase 2): the archive is the data fork, read like any other.
- [ ] **Original-app fixtures still missing** (phase 10's Remaining column; StuffIt, DiskDoubler, Compact Pro, PackIt
  and MacLHA samples added 2026-10-02): StuffIt method 6 and 5's method 14, a SegmentIt set, PackIt `PMa4` and encrypted entries, DiskDoubler methods 2–5 and 7 and the delta types.
  None is on GitHub or in the CC0 corpora (searched 2026-10-02); method 14 probably needs a pre-7 StuffIt InstallerMaker
  installer, PackIt III a System 6/7 68k emulator (it does not run on Mac OS 9).
- [x] **Archive bugs from the samples** (2026-10-02): LHA `-lh1-` (position code and tree), StuffIt method 15's last
  symbol, DiskDoubler split files (`SPLT`), and `list` paths through single-file wrappers.
- [x] **DiskDup+** images: raw sectors, read by the raw-image path [Verified with DiskDup+ 2.9.2] ([raw-images.md](formats/disk-images/raw-images.md)).
- [x] **Apple Help pages** (`TEXT`/`hbwr`, Mac OS 8.5+ Help Viewer books; also `.html`/`.htm` files)
  ([help-pages.md](formats/resources/help-pages.md)): shown by the platform's own web engine through NativeWebView
  (MIT; WebView2, WKWebView, WebKitGTK), with a Rendered | Source switch. Decided instead of parsing the HTML into
  `StyledDocument`: help books are table layouts with fonts, frames and pictures that a hand-written renderer would
  only approximate, while the platform engines render HTML 3.2/4 well and are already on every desktop. ClassicMac
  makes each page self-contained (pictures, a PICT drawn to PNG, stylesheets and frames inline as `data:` URIs, no
  temporary files, JavaScript off) and turns links to the disk's files into tree selections; `help:`, AppleScript and
  outside links are not followed. Without an engine the page shows as its source. The CLI leaves help pages as the
  files they are.
- [x] **Documents previewed through their HTML export** (decided 2026-10-03; built 2026-10-03): DOCMaker, SimpleText and Word documents
  are previewed by showing the HTML that `convert` writes (`HtmlDocuments`, a page per chapter, pictures as PNG) in the
  NativeWebView the help pages use, served the same way (self-contained pages, no scripts; chapter links and
  "next/previous/back" pictures navigate within the document), so the preview and the export are one layout. The app's
  own document layout (`DocumentFlow` in the app, the document view's text and picture rows) is then removed. Single
  `TEXT` + `styl` resources keep the native styled-text view. Without a web engine the preview falls back to plain text
  with a message. Document screenshot baselines go (a web view cannot be captured headless); tests cover the HTML and
  the navigation instead.
- [ ] **Microsoft Word documents** (`MSWD`): a document decoder for the viewer, `convert` and `extract`, text first,
  then character and paragraph styles. `WDBN` Word 4 and 5 (built: [word-mac.md](formats/documents/word-mac.md); no
  specification was found, so the layout is fitted to real documents: 21 that Word 4.0 and 5.1a wrote with known
  content (test fixtures, `tests/ClassicMac.Resources.Decoders.Tests/Word/`) and a fast-saved Word 5 file, read through
  its piece table; libmwaw as a behavioural reference; Word 1 and 3 are reported, not read). `W8BN` Word 98 (built:
  [word-binary.md](formats/documents/word-binary.md), by [MS-DOC], in a compound file read by `CompoundFile`,
  [compound-file.md](formats/containers/compound-file.md), by [MS-CFB]; verified only against fixtures built from the
  specifications). `W6BN` Word 6 (built, in the same reader: no public specification, so its structures follow other
  readers' behaviour, LibreOffice, Apache POI and wv, and two are assumed from Word 97's; word-binary.md §4.1;
  checked against 10 documents Word 6.0 for the Macintosh wrote). Still needed: Word 98 documents, and fast-saved
  Word 6 ones.
- [ ] **PCE MAR** input (Inputs, priority 3). Blocked: no sample, and its layout is published only in GPL source.
- [x] **Mac ROM images:** the ROM's built-in resource map, raw and New World `Mac OS ROM` files ([rom.md](formats/disk-images/rom.md); 68k ROMs
  unverified).
- [x] **Fork repair beyond `fork.map-recovered`:** not needed: no corpus fork reports a `fork.*` diagnostic
  (checked 2026-10-02); reopen if one does.
- [x] **Big-endian reading and writing through Core:** every big-endian read and write goes through `BigEndianReader`
  and `BigEndianWriter` (done 2026-10-01; see CLAUDE.md).

## Decisions

- **Name:** ClassicMac (see Purpose and goals).
- **Repository:** a new repo, `inexin/ClassicMac`; QuickDraw.Pict moved in with its history (2026-09-29), ahead of
  the editors, instead of publishing a QuickDraw.Pict 0.1.1.
- **UI framework:** Avalonia (see Viewer app); view-models with CommunityToolkit.Mvvm (source-generated, AOT-safe).
- **Own core:** written here rather than built on ResourceForkReader/HfsReader, which are read-only; they serve as
  cross-checks.
- **Target framework:** .NET 10 (LTS; .NET 8 support ends November 2026).
- **Built-in templates (2026-10-02):** ClassicMac ships its own templates for common types with no form of their own
  (MBAR, BNDL, FREF, SIZE, TMPL, CURS, PAT , PAT#, clut, wctb, actb, dctb, cctb, mctb), as a C# table written from
  Inside Macintosh and the ResEdit Reference in its own words; ResEdit's `TMPL` resources are never copied (they are
  a cross-check only, and a layout known only from them is tagged `[Reference: ResEdit]`). A `TMPL` in an open file
  wins over a built-in one (templates.md §5).
- **Document kinds (2026-10-03):** a file's kind is named the Finder's way first, from what is on the volume, as traced
  in Finder 9.2.2 and the Translation library: the Finder's own kinds by type (applications, system files through its
  `'fmap'`, clippings, …), then `GetDocumentKindString`'s order (the `'kind'` for creator and type, the creator's
  `'apnm'` " document", the application's file name " document", the System's `'istd'` kind, "document";
  `FinderKindResolver`, one per volume standing in for the desktop database, reading a file's fork only when needed;
  [finder.md §2.3](formats/resources/finder.md#23-a-documents-kind)). Strings come from the volume's Finder and System,
  with English copies when they are absent. ClassicMac's own table (`KnownKinds`, its own words, codes cross-checked
  against public lists, none copied) is the fallback when the volume does not say, placed before the `'istd'` kinds
  for exact type and creator pairs.
- **Alias files (2026-10-03):** `'alis'` records are read in `ClassicMac.Resources` (`AliasRecord`) so the File layer can
  resolve them (`AliasResolver`, `AliasVolume`, `MacPathTree.ResolveAlias`; HFS and HFS Plus files and folders keep
  their catalog IDs for it), and the CLI, the MCP server and the app share that one resolver. Resolving follows the
  Alias Manager's fast search on the volumes that are open (volume by name and date, then by number, by parent and
  name, by full path), not the relative path, catalog search or mounting; paths are shown as the Finder's Get Info shows
  "Original:" ("Vol: folder: name"). The app shows an alias's original path in the header with Show Original (Ctrl+R),
  previews the original under a strip, and says when it is not found
  ([aliases.md](formats/resources/aliases.md)).
- **ClassicMac ships TCDB (2026-10-03, the owner's decision):** TCDB (Type/Creator Database by Ilan Szekely,
  1996–2003) is the largest list of type and creator codes. ClassicMac ships its own copy of TCDB 2003.10, with the
  file names, descriptions, comments, categories and extensions, credited in THIRD-PARTY-NOTICES.md and the About box.
  The owner made this call after weighing the licence caveats: TCDB was a free download with no licence to
  redistribute; the codes and what they belong to are facts, which are not protectable; the EU database right in a
  2003 database expired about 2018 (fifteen years); the wording of the descriptions may still be. Records TCDB took
  from filext.com (another site's data, and without codes) are left out. `tools/TcdbData` converts a tab-delimited
  export into `Finder/tcdb.tsv.gz`, embedded in Resources.Decoders and read on first use; the export is never
  committed. Kinds ask it after the Finder's kinds and ClassicMac's own table, as "TCDB"
  ([finder.md §2.6](formats/resources/finder.md#26-the-type-and-creator-database-tcdb)). A user's own TCDB
  spreadsheet (View ▸ Type/Creator Database…, the CLI's `--type-creator-db`) replaces it, as "TCDB (your copy)"; it
  is read with the .NET library's zip and XML readers, as the spreadsheet packages (ClosedXML, ExcelDataReader) are
  far larger than the parts needed. This replaces the decision of the same day to leave TCDB to the user.
- **Disk images early:** phase 2, right after the core, because much classic software survives only as disk images.
- **Decoder priority after images:** text, sound, UI, fonts.
- **Fonts package:** part of `ClassicMac.Graphics` (`ClassicMac.Graphics.Fonts`), on Core only (2026-09-29, revising
  a standalone `ClassicMac.Fonts` of 2026-09-28); see Architecture.
- **Image output:** 32-bit RGBA PNG by default; other screen depths on request.
- **Reference Mac OS 9 (2026-09-29):** "Mac OS 9" means Mac OS 9.0, the build analysed and tested (component versions
  in [formats/README.md](formats/README.md#reference-builds)). Its quirks and bugs are reproduced, since the goal is
  the pixels that Mac shows; the ROM mode draws the classic 68k behaviour. Should a later 9.x differ, the version
  setting names builds (for example 9.0 and 9.2.2) rather than one "Mac OS 9".
- **Editing order:** resource-level edits, typed editors, then writing disk images.
- **Names on disk:** `%XX` escaping, reserved-name escaping, hex suffix on case collisions; the manifest is the
  authority (see Names on disk).
- **Manifest:** versioned JSON Schema; hashes decide what `pack` re-encodes.
- **Encodings:** own tables from Unicode's Apple mappings; single-byte in `ClassicMac.Core`, multi-byte in
  `ClassicMac.Encodings`.
- **Hostile input:** bounds checks, allocation and nesting limits, fuzzing in CI.
- **Resource Manager model:** Mac OS 9's behaviour is the default where it and the 68k ROM differ (only on malformed
  compressed resources); the ROM's is selectable in `ReadOptions`.
- **Configuration:** every limit and default is a property on an immutable options object
  (`ContainerReadOptions`, `ReadOptions`, `DecodeOptions`, `ExportOptions`, `PackOptions`); nothing tunable is
  hard-coded.
- **Renderer and file format:** the QuickDraw renderer (`ClassicMac.Graphics.QuickDraw`) and the PICT format
  (`ClassicMac.Graphics.Pict`) are separate layers of one package (see the layering).
- **Drawing API (2026-09-29):** a public `QuickDrawPort` with QuickDraw's own names over the renderer, everything the
  engine already draws; `RgbaBitmap`/`RgbaColor` (formerly `PictBitmap`/`PictColor`) as the base image and pixel
  colour (`Bitmap`/`Color` would clash with host libraries), 16-bit `RgbColor` on the port, as QuickDraw's. Not in the
  first cut: ScrollRect, recording regions and polygons (OpenRgn/OpenPoly) and pictures (`OpenPicture`).
- **Integrations:** one package per host library (`ClassicMac.Graphics.ImageSharp`, `ClassicMac.Graphics.SkiaSharp`) covering every
  image format, instead of one per format.
- **Package naming:** `ClassicMac.<Area>`, named after the Apple technology (QuickDraw, QuickTime); namespaces start
  with the package name, and areas inside a package get sub-namespaces (`ClassicMac.Files.Hfs`); format specs live
  under `docs/formats/`.
- **One file-layer package (revised):** the file layer is a single package, `ClassicMac.Files`, with one namespace
  per area (containers, HFS/MFS, FAT, later archives), to avoid a project per format; it stays separate from
  `ClassicMac.Resources`, so resource-only users skip the containers; Files references Resources (one way) for disk
  images whose layout is in resources (NDIF); the shared
  types (`FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `Diagnostic`) sit in the dependency-free
  base `ClassicMac.Core` (see Inputs).
- **Archive decompressors without a spec:** StuffIt and Compact Pro methods have no Apple or vendor spec; XADMaster
  (LGPL) and macutils are reference only, deark and hughbe's readers may be ported with notice. The decompressors are
  written here and verified against archives made by the real StuffIt and Compact Pro in an emulator (the corpus),
  marked as fitted where behaviour comes from test data rather than a published description.
- **More input formats (decided):** CD images, DART, UDIF and zip/tar with Mac data join Phase 2; DiskDoubler, PackIt
  and segmented archives follow StuffIt and Compact Pro; StuffIt X and other encrypted Mac application formats are
  considered only on request (priority 5); Mac OS X sparse images remain out of scope.
- **`dcmp` 0–3 from disassembly:** all four decompressors follow the Mac OS 9.0 System's code (68k, emulated and
  compared on all 34 compressed System resources); the memory after the in-place block is modelled as 2 KiB of zeros.

## Open questions

- [x] **Fonts package:** decided: `ClassicMac.Graphics.Fonts`, inside the graphics package on Core only, used by the
  decoders and the renderer (see Architecture).
