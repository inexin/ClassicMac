# Raw CD images, cue sheets and multisession discs

A raw CD image stores every sector as the disc carries it (2352 bytes, or 2336 without sync and header) rather than
the 2048-byte blocks a drive hands the Mac; a cue sheet is the text that lays a disc's tracks out over such images.
Both come from PC CD-burning and ripping tools, not from the Mac. ClassicMac reads either as one file whose data fork
is the disc's 2048-byte blocks, numbered by absolute sector and carrying the start of the last session, for the ISO
9660, partition-map and HFS readers ([iso9660.md §2](../file-systems/iso9660.md#2-from-image-to-volume)) to open next.

| | |
| --- | --- |
| Identified by | Raw: a length that is a multiple of 2352 or 2336, with the sync pattern or a volume inside (extension `.bin`). Cue sheet: text with `FILE` and `TRACK` lines (extension `.cue`) |
| ClassicMac | Reads; `ClassicMac.Files.Iso.RawCdReader`, `ClassicMac.Files.Iso.CueSheetReader` |
| Verified against | Nothing yet: synthetic images only |
| Sources | ECMA-130 (the sector formats); the CDRWIN cue-sheet format; Apple CD/DVD Driver (disassembly) and the Mac OS 9.0 ISO 9660 file-system plug-in for multisession discs |

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

---

## 1. Layout

### 1.1 Raw sectors

A raw image stores every sector as the disc carries it [Doc: ECMA-130].

2352-byte sector, mode 1 [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$000` | 12 | Sync | `00`, ten `FF`, `00` |
| `+$00C` | 3 | Address | Minute, second, frame, in BCD |
| `+$00F` | 1 | Mode | 1 |
| `+$010` | 2048 | User data | |
| `+$810` | 4 | EDC | |
| `+$814` | 8 | Reserved | Zero |
| `+$81C` | 276 | ECC | P and Q parity |

2352-byte sector, mode 2 (CD-ROM XA) [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$000` | 12 | Sync | As mode 1 |
| `+$00C` | 3 | Address | As mode 1 |
| `+$00F` | 1 | Mode | 2 |
| `+$010` | 4 | Subheader | File number, channel number, submode, coding information. Submode bit 5 (`$20`) marks form 2 |
| `+$014` | 4 | Subheader copy | |
| `+$018` | 2048 or 2324 | User data | Form 1: 2048 bytes, then EDC and ECC. Form 2: 2324 bytes, then a 4-byte EDC |

A 2336-byte sector is a mode 2 sector from byte 16: subheader at `+$0`, its copy at `+$4`, user data at `+$8` [Doc].

The address is the sector's absolute position: ((minute × 60 + second) × 75 + frame) − 150 is its logical block
address, the 150 sectors (two seconds) before block 0 being the lead-in pregap [Doc].

Where the 2048-byte block is in each kind of sector:

| Sector | User data at | 2048-byte block |
| --- | --- | --- |
| 2352, mode 1 | 16 | Yes |
| 2352, mode 2 form 1 (subheader byte 18 bit 5 clear) | 24 | Yes |
| 2352, mode 2 form 2 | — | None |
| 2352, mode 0 or any other mode byte | — | None |
| 2336, form 1 (byte 2 bit 5 clear) | 8 | Yes |
| 2336, form 2 | — | None |

### 1.2 Cue sheets

A cue sheet is a text file that lists a disc's tracks and the image files that hold them [Author: CDRWIN]. These
commands place data; the others (`REM`, `CATALOG`, `PERFORMER`, `TITLE`, `PREGAP`, `POSTGAP`, `FLAGS`, `ISRC`, …) do
not.

| Command | Form | Meaning |
| --- | --- | --- |
| `FILE` | `FILE "name" type` or `FILE name type` | The image file that holds the following tracks [Author] |
| `TRACK` | `TRACK nn mode` | A track and its data mode [Author] |
| `INDEX` | `INDEX nn mm:ss:ff` | An index point, as a time in the current file: minutes, seconds, frames (75 per second) [Author]. `INDEX 00` starts the pregap, `INDEX 01` the track |

Track modes and their sector sizes [Author]:

| Mode | Bytes per sector |
| --- | --- |
| `AUDIO` | 2352 |
| `MODE1/2048` | 2048 |
| `MODE1/2352` | 2352 |
| `MODE2/2336` | 2336 |
| `MODE2/2352` | 2352 |
| `CDG` | 2448 |
| `CDI/2336` | 2336 |
| `CDI/2352` | 2352 |

`MODE2/2048` is not a CDRWIN mode, but some programs write it for 2048-byte mode 2 sectors. `REM SESSION nn`, a comment
in CDRWIN, is written by other programs to put the tracks after it in session *nn*.

## 2. Reading

### 2.1 What the Mac reads of a sector

A drive returns only mode 1 and form 1 data to a 2048-byte read, so form 2 data never reaches a Mac file system; the
Mac's CD driver never parses sectors itself [Code: CD driver]. Cue sheets and raw images are not Mac formats: the Mac
reads the disc, never an image of it.

### 2.2 Multisession discs

A multisession disc (a CD-R written in several sessions, a CD Extra/Enhanced CD with audio first and data after) has a
volume descriptor set in each data session; the last one describes the whole disc, and its extents are absolute sector
numbers [Doc]. The Mac [Code: CD driver]:

1. The driver reports the sessions (its table-of-contents call, session form): first and last session, and the start
   of the last session's first track. If the drive refuses the call, the driver answers "one session, starting at
   00:02:00", which gives offset 0.
2. The file-system plug-in computes D = (start of the last session − start of track 1) and reads the descriptors at
   sector D + 16 first, then at sector 16 ([iso9660.md §3.1](../file-systems/iso9660.md#31-finding-the-descriptor)).
3. The driver checks each session for a descriptor at its start + 16 sectors. When the last session's root extent is at
   or after that session's start, it maps the whole disc from offset 0, so absolute sector numbers work.
4. The driver applies the session base to a partition map as well
   ([partition-map.md §4](../file-systems/partition-map.md#4-which-partitions-are-mac-volumes)).

## 3. Writing

None.

## 4. Variants

- 2352-byte images carry the sync and header of every sector; 2336-byte images (cue mode `MODE2/2336`) have neither,
  so they carry no address ([§1.1](#11-raw-sectors)).
- A disc may mix modes, sector by sector.
- Images often leave out the sectors between sessions (lead-out, lead-in); a 2352-byte data sector's address still
  gives its place on the disc.
- A cue sheet may hold every track in one file or one file per track.

## 5. ClassicMac

### 5.1 Raw images

- **Recognition** [ClassicMac] (the Mac has no raw images to recognise):
  - **2352-byte sectors**: the length is a multiple of 2352 and at least 17 sectors, and sector 16 or sector 0 starts
    with the sync pattern and has mode byte 1 or 2.
  - **2336-byte sectors** (no sync to test): the length is a multiple of 2336 and at least 17 sectors, the two subheader
    copies of sector 16 are equal, and the cooked data hold a volume: `CD001` at byte 1 or `CDROM` at byte 9 of block
    16, or `ER` at byte 0 (partition map) or `BD` at byte 1024 (HFS) of the disc.
- **Cooking**: each sector's own mode and submode decide where its 2048-byte block is ([§1.1](#11-raw-sectors)); a
  sector with none reads as 2048 zeros. EDC and ECC are not checked. The cooked disc is (number of sectors × 2048)
  bytes, and the image's first sector is block 0, except where the addresses show a gap between sessions (§5.3).
- **Output**: one file named after the host file without its extension (else "CD").

### 5.2 Cue sheets

- **Text**: at most 64 KiB, no zero bytes, read as Latin-1 with a UTF-8 byte order mark removed; lines end in CR, LF or
  both; commands are matched without regard to case and leading spaces. A file is a cue sheet when it has at least one
  `FILE` line (with a type) and one `TRACK` line.
- **Tracks**: a `TRACK` line becomes a track at its `INDEX 01` (`INDEX 1` is accepted), in the file named by the last
  `FILE` line. `INDEX 00` gives the track's pregap. A track with no `INDEX 01` is dropped. The `FILE` type (`BINARY`,
  `MOTOROLA`, `WAVE`, …) is not checked. Without `REM SESSION` lines every track is in session 1.
- **Modes read**: `MODE1/2048` and `MODE2/2048` as they are; `MODE1/2352`, `MODE2/2352` and `MODE2/2336` cooked
  (§5.1). `AUDIO` is skipped. `CDG`, `CDI/2336`, `CDI/2352` and others are not read; their sector sizes
  ([§1.2](#12-cue-sheets)), 2352 for an unknown mode, are still used to lay out the file.
- **Files**: found among the files beside the cue sheet by name, without regard to case. A path in the name is not
  followed.
- **Placing tracks**:
  1. The files are laid end to end in the order the sheet names them.
  2. In a file, a track's sectors start at its `INDEX 00` or, without one, its `INDEX 01`, and each sector has its own
     track's size. A track's bytes start at (the sectors of the tracks before it in the file × their sizes) plus (its
     `INDEX 01` − its region start) × its size.
  3. A track runs to the next track's region in the same file, or the file's end, rounded down to whole sectors.
  4. Its absolute sector is the sectors of the files before it plus its `INDEX 01`, counted from track 1's.
  5. **The gap between sessions**: a raw 2352-byte data track whose first sector's header ([§1.1](#11-raw-sectors))
     gives a later address than its place in the files is put at the header's address, and the tracks after it move
     with it. A 2048-byte track has no header: its place in the files is taken as its address.
- **The disc**: one file, named after the cue sheet without its extension, whose data fork is the disc's 2048-byte
  blocks numbered by absolute sector from the first track's: each data track's blocks (§5.1 for raw sectors) at its
  sector, and zeros for audio tracks, gaps and tracks not read. The last session's start goes with it (§5.3).
- **Errors**: with no data track the sheet gives nothing (`cue.audio-only`). The track the descriptors come from (the
  last session's first data track, or the first data track when that session has none) must be readable: an unreadable
  mode, a missing file or a track starting past its file's end makes the sheet unreadable (the unwrapper reports
  `container.unreadable`, [unwrapping.md §3.1](../containers/unwrapping.md#31-readers-and-order)). Any other data
  track that cannot be read reads as zeros (`cue.track-unread`).

### 5.3 Multisession discs

The disc an image gives has no table of contents, so ClassicMac finds the last session itself, then follows the Mac
([§2.2](#22-multisession-discs)):

- **Cue sheet**: the last session's first track is the first track after the last `REM SESSION` line or, in a sheet
  without those lines, the **last data track** (a CD Extra's data track, or each later session's track, comes after the
  first session's). D = its absolute sector − track 1's (§5.2), and the disc is mapped from track 1's start.
- **Raw image** (§5.1) with no cue sheet: a 2352-byte image's sector headers give each sector's address. When the
  address less the sector's index grows somewhere in the image (the image left out the gap between sessions), each run
  of sectors with one offset is placed at its address (at most 99 runs; the offset at each end is taken from the
  nearest sector with a valid address within 300 sectors), and the last run is the last session. Its first track is
  taken to start at the run's first sector or, when that has no volume descriptor (`CD001`, `CD-I ` or High Sierra's
  `CDROM`) 16 sectors on, 150 sectors later (a pregap). An image whose addresses do not jump (one session, or the gap
  kept), a 2336-byte image (no headers) and a cooked image are read from sector 16 only, by their first session's
  descriptors.
- **Descriptors**: at D + 16 first; when that sector is not an anchor
  ([iso9660.md §3.1](../file-systems/iso9660.md#31-finding-the-descriptor)), at 16. When the last session's primary
  descriptor's root directory record (byte 156, High Sierra 180) gives an extent at or after the session's start, block
  numbers are absolute; otherwise they count from the session's start.
- **HFS**: a partition map (`ER` or zeros at block D, `PM` at byte 512 or 2048 of it) or an HFS volume (`BD` at byte
  1024) at the start of a cue sheet's last session is read from that session's start, as the driver applies the
  session base to a partition map; for a bare HFS volume this is ClassicMac's choice, as the driver's handling of one
  was not traced. A raw image's later session is not checked for HFS.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `container.unreadable` | Error | The cue sheet's descriptor track (§5.2) has a mode that is not read, its file is not beside the sheet, or it starts past the file's end | Gives nothing; the cue sheet stays a plain file (reported by the unwrapper) | Cue sheets are not a Mac format |
| `cue.audio-only` | Info | The cue sheet lists only audio tracks | Gives nothing | Cue sheets are not a Mac format |
| `cue.track-unread` | Info | A data track other than the one the descriptors come from has a mode that is not read, its file is not beside the sheet, or it starts past the file's end | Its sectors read as zeros | Cue sheets are not a Mac format |

## 7. Verification

`tests/ClassicMac.Files.Tests/RawCdTests.cs`, on discs built by `IsoBuilder.cs`:

- `Raw_sector_images_read_as_the_disc`: 2352-byte mode 1 and mode 2 form 1, and 2336-byte images, cook to the disc
  and its files. `Cooked_images_and_other_data_are_not_raw`.
- `Cue_sheets_read_their_first_data_track_from_the_file_beside_them`: a `MODE1/2352` data track followed by audio
  (with `INDEX 00`), CR LF lines; without the `.bin`, `container.unreadable`.
- `Cue_sheets_read_the_last_session_of_2048_byte_tracks` (with and without `REM SESSION`),
  `Cue_sheets_read_the_last_session_of_raw_tracks_placed_by_their_headers` (one file per track, the gap left out),
  `Raw_images_of_a_whole_disc_read_the_session_after_the_jump_in_address`,
  `A_last_session_whose_root_lies_before_it_counts_from_its_start`,
  `A_session_without_descriptors_falls_back_to_sector_16`: the multisession rules of §2.2 and §5.3.

No image made by a real burning or ripping program is in the repository.

## 8. Not covered

- Other raw formats: 2340-byte sectors, 2448-byte sectors with subchannel data, and the CloneCD, Alcohol, Nero and
  DiscJuggler image formats.
- `CDG` and CD-i tracks.
- Writing ISO 9660, High Sierra or CD images.
- How the CD driver handles a bare HFS volume in a later session (not traced).

## 9. References

1. ECMA-130, Data interchange on read-only 120 mm optical data disks (CD-ROM).
2. CDRWIN (Golden Hawk Technology), the cue-sheet format as its user's guide documents it.
3. Apple CD/DVD Driver 1.3.1 and the Mac OS 9.0 ISO 9660 file-system plug-in, traced in disassembly.
