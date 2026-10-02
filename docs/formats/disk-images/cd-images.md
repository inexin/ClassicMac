# Raw CD images, cue sheets and multisession discs

Contents

1. [Raw CD images](#1-raw-cd-images)
2. [Cue sheets](#2-cue-sheets)
3. [Multisession discs](#3-multisession-discs)
4. [Not covered](#4-not-covered)
5. [Diagnostics](#5-diagnostics)

---

## 1. Raw CD images

### 1.1 Sector formats

A raw image stores every sector as the disc carries it, 2352 bytes, or 2336 bytes without the sync and header
[Doc]:

2352-byte sector, mode 1 [Doc]:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 12 | Sync: `00`, ten `FF`, `00` |
| 12 | 3 | Address: minute, second, frame (BCD) |
| 15 | 1 | Mode: 1 |
| 16 | 2048 | **User data** |
| 2064 | 4 | EDC |
| 2068 | 8 | Reserved (zero) |
| 2076 | 276 | ECC (P and Q parity) |

2352-byte sector, mode 2 (CD-ROM XA) [Doc]:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 12 | Sync |
| 12 | 3 | Address |
| 15 | 1 | Mode: 2 |
| 16 | 4 | Subheader: file number, channel number, submode, coding information |
| 20 | 4 | Copy of the subheader |
| 24 | 2048 | **User data** (form 1), followed by EDC and ECC |
| 24 | 2324 | User data (form 2), followed by a 4-byte EDC |

Submode bit 5 (`$20`) marks form 2 [Doc]. A 2336-byte sector is a mode 2 sector from byte 16: subheader at 0, copy at
4, user data at **8** [Doc].

| Sector | User data at | 2048-byte block |
| --- | --- | --- |
| 2352, mode 1 | 16 | yes |
| 2352, mode 2 form 1 (subheader byte 18 bit 5 clear) | 24 | yes |
| 2352, mode 2 form 2 | — | none: reads as 2048 zeros |
| 2352, mode 0 or any other mode byte | — | none: reads as zeros |
| 2336, form 1 (byte 2 bit 5 clear) | 8 | yes |
| 2336, form 2 | — | none: reads as zeros |

A drive returns only form 1 and mode 1 data to a 2048-byte read, so form 2 data never reaches a Mac file system
[Code: CD driver]. ClassicMac: each sector's own mode and submode decide, so a disc may mix modes; EDC and ECC
are not checked, and the image's first sector is logical block 0, except where the addresses show a gap between
sessions (§3).

### 1.2 Recognising a raw image

ClassicMac recognises a raw image by content (the Mac has no raw images to recognise):

- **2352-byte sectors**: the length is a multiple of 2352 and at least 17 sectors, and sector 16 or sector 0 starts
  with the sync pattern and has mode byte 1 or 2.
- **2336-byte sectors** (no sync to test): the length is a multiple of 2336 and at least 17 sectors, the two
  subheader copies of sector 16 are equal, and the cooked data holds a volume: `CD001` at byte 1 or `CDROM` at byte 9
  of block 16, or `ER` at byte 0 (partition map) or `BD` at byte 1024 (HFS) of the disc.

The cooked disc is (number of sectors × 2048) bytes, handed to the volume readers ([iso9660.md §2](../file-systems/iso9660.md#2-from-image-to-volume)).

---

## 2. Cue sheets

A cue sheet is a text file that lists a disc's tracks and the image files that hold them [Author]. Only the commands
below matter here; every other line (`REM`, `CATALOG`, `PERFORMER`, `TITLE`, `PREGAP`, `POSTGAP`, `FLAGS`, `ISRC`, …)
is ignored.

| Command | Form | Meaning |
| --- | --- | --- |
| `FILE` | `FILE "name" type` or `FILE name type` | The image file that holds the following tracks [Author] |
| `TRACK` | `TRACK nn mode` | A track and its data mode [Author] |
| `INDEX` | `INDEX nn mm:ss:ff` | An index point, as a time in the current file: minutes, seconds, frames (75 per second) [Author] |

Track modes and their sector sizes [Author]:

| Mode | Bytes per sector | Read by ClassicMac |
| --- | --- | --- |
| `AUDIO` | 2352 | skipped |
| `MODE1/2048` | 2048 | as is |
| `MODE1/2352` | 2352 | cooked (§1) |
| `MODE2/2336` | 2336 | cooked (§1) |
| `MODE2/2352` | 2352 | cooked (§1) |
| `MODE2/2048` | 2048 | as is (not a CDRWIN mode, but written by some programs) |
| `CDG`, `CDI/2336`, `CDI/2352`, others | — | not read (`container.unreadable`) |

ClassicMac's reading:

- **Text**: at most 64 KiB, no zero bytes, read as Latin-1 with a UTF-8 byte order mark removed; lines end in CR, LF
  or both; commands are matched without regard to case and leading spaces. A file is a cue sheet when it has at least
  one `FILE` line and one `TRACK` line.
- **Tracks**: a `TRACK` line becomes a track at its `INDEX 01` (`INDEX 1` is accepted), in the file named by the last
  `FILE` line. `INDEX 00` (the pregap) is ignored. A track with no `INDEX 01` is dropped. The `FILE` type (`BINARY`,
  `MOTOROLA`, `WAVE`, …) is not checked.
- **Sessions**: `REM SESSION nn` (a comment in CDRWIN, written by other programs for multisession discs) puts the
  tracks after it in session nn; without it every track is in session 1.
- **Files**: found among the files beside the cue sheet by name, without regard to case. A path in the name is not
  followed.
- **Placing tracks**: the files are laid end to end in the order the sheet names them. In a file, a track's sectors
  start at its `INDEX 00` (the pregap) or, without one, its `INDEX 01`, and each sector has its own track's size, so
  a track's bytes start at (the sectors of the tracks before it in the file × their sizes) plus (its INDEX 01 − its
  region start) × its size. A track runs to the next track's region in the same file, or the file's end, rounded
  down to whole sectors. Its absolute sector is the sectors of the files before it plus its INDEX 01, counted from
  track 1's.
- **The gap between sessions**: images often leave out the sectors between sessions (lead-out, lead-in). A raw 2352-byte data track whose first sector's header (§1.1) gives a later address than
  its place in the files is put at the header's address, and the tracks after it move with it. A 2048-byte track has
  no header: its place in the files is taken as its address.
- **The disc**: one file, named after the cue sheet without its extension, whose data fork is the disc's 2048-byte
  blocks numbered by absolute sector, from the first track's: each data track's blocks (§1 for raw sectors) at its
  sector, and zeros for audio tracks, gaps and tracks not read ([iso9660.md §2](../file-systems/iso9660.md#2-from-image-to-volume)). The last session's start goes with it (§3).
- **Errors**: with no data track the sheet gives nothing (`cue.audio-only`). The track the descriptors come from (the
  last session's first data track, or the first data track when that session has none) must be readable: an
  unreadable mode, a missing file or a track starting past its file's end makes the sheet unreadable. Any other data
  track that cannot be read reads as zeros (`cue.track-unread`).

---

## 3. Multisession discs

A multisession disc (a CD-R written in several sessions, a CD Extra/Enhanced CD with audio first and data after) has
a volume descriptor set in each data session; the last one describes the whole disc, and its extents are absolute
sector numbers [Doc].

The Mac [Code: CD driver][Code]:

- The driver reports the sessions (its table of contents call, session form): first and last session, and the start
  of the last session's first track. If the drive refuses the call, the driver answers "one session, starting at
  00:02:00", which gives offset 0.
- The plug-in computes D = (start of the last session − start of track 1) and reads the descriptors at sector D + 16
  first, then at sector 16.
- The driver checks each session for a descriptor at its start + 16 sectors. When the last session's root extent is
  at or after that session's start, it maps the whole disc from offset 0, so absolute sector numbers work.

The disc an image gives has no table of contents, so ClassicMac finds the last session itself, then follows the Mac:

- **Cue sheet**: the last session's first track is the first track after the last `REM SESSION` line or, in a sheet
  without those lines, the **last data track** (ClassicMac's reading: a CD Extra's data track, or each later
  session's track, comes after the first session's). D = its absolute sector − track 1's (§2), and the disc is
  mapped from track 1's start.
- **Raw image** (§1) with no cue sheet: a 2352-byte image's sector headers give each sector's address. When the
  address less the sector's index grows somewhere in the image (the image left out the gap between sessions), each
  run of sectors with one offset is placed at its address, and the last run is the last session. Its first track is
  taken to start at the run's first sector or, when that has no descriptor 16 sectors on, 150 sectors later (a
  pregap); this is ClassicMac's rule, not the Mac's. An image whose addresses do not jump (one session, or the gap
  kept), a 2336-byte image (no headers) and a cooked image are read from sector 16 only, by their first session's
  descriptors.
- **Descriptors**: at D + 16 first; when that sector is not an anchor ([iso9660.md §3.1](../file-systems/iso9660.md#31-finding-the-descriptor)), at 16. When the last session's
  primary descriptor's root directory record (byte 156, High Sierra 180) gives an extent at or after the session's
  start, block numbers are absolute; otherwise they count from the session's start.
- **HFS**: a partition map (`ER` or zeros at block D, `PM` at byte 512 or 2048 of it) or an HFS volume (`BD` at
  byte 1024) at the start of a cue sheet's last session is read from that session's start: the CD driver applies the
  session base to the partition map ([partition-map.md §4](../file-systems/partition-map.md#4-which-partitions-are-mac-volumes)). For a bare HFS volume this is ClassicMac's
  choice; the driver's handling of one was not traced. A raw image's later session is not checked for HFS.

---

## 4. Not covered

- **Other raw formats**: 2340-byte sectors, 2448-byte sectors with subchannel data, and the CloneCD, Alcohol, Nero
  and DiscJuggler image formats.
- **Writing**: ClassicMac does not write ISO 9660, High Sierra or CD images.

---

## 5. Diagnostics

| Code | Severity | Meaning | What the Mac does |
| --- | --- | --- | --- |
| `cue.audio-only` | Info | The cue sheet lists only audio tracks; nothing is read. | Cue sheets are not a Mac format. |
| `cue.track-unread` | Info | A data track other than the one the descriptors come from has a mode that is not read, its file is not beside the sheet, or it starts past the file's end; its sectors read as zeros (§2). | Cue sheets are not a Mac format. |
| `container.unreadable` | Error | The cue sheet's descriptor track (§2) has a mode that is not read, its file is not beside the sheet, or it starts past the file's end. | — |
