# Alias records

An alias record (`'alis'`) is the Alias Manager's description of a file, folder or volume: enough to find it again after
it moves or is renamed, or on a volume that is not mounted. The Finder's alias files keep one as `'alis'` 0 in their
resource fork; applications keep them in their own resources and preferences. ClassicMac reads the record, writes it
as JSON, and resolves alias files on the volumes it has open, showing where each one points.

| | |
| --- | --- |
| Identified by | Resource type `'alis'`; an alias file has the Finder's isAlias flag (bit 15), an empty data fork and its original's type and creator (or a special type: `fdrp` folder, `adrp` application, `hdsk`, `flpy`, `cddr`, `srvr` disks) |
| ClassicMac | Reads; `ClassicMac.Resources.AliasRecord`; `ClassicMac.Files.AliasResolver`, `AliasVolume`, `MacPathTree.ResolveAlias`; the decoder `finder.alias` |
| Verified against | The 36 alias files on a Mac OS 9.0 startup disk: files, folders, aliases to other volumes, Recent Applications and Documents items |
| Sources | *Inside Macintosh: Files*, Alias Manager (the public header, the resolution rules); the Mac OS 9 Finder and Alias Manager as traced (MatchAlias, FollowFinderAlias, GetAliasInfo) |

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

Big-endian. *Inside Macintosh: Files* documents only the first two fields (`userType`, `aliasSize`) and calls the rest
private [Doc]; the rest is as the Alias Manager writes it, verified on real records [Verified: 36 aliases on a Mac OS
9.0 disk].

### 1.1 The fixed part

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `userType` | The application's own type; 0 in the Finder's aliases [Doc] |
| +$04 | 2 | `aliasSize` | The record's length, without data an application appends after it [Doc] |
| +$06 | 2 | version | 2 |
| +$08 | 2 | kind | 0 file, 1 folder (a volume alias is a folder) |
| +$0A | 28 | volume name | Str27 |
| +$26 | 4 | volume creation date | Seconds since 1904 |
| +$2A | 2 | volume signature | `'BD'` ($4244) HFS, $D2D7 MFS |
| +$2C | 2 | volume type | 0 hard disk, 1 foreign or AppleShare, 2 400K, 3 800K, 4 1.4 MB, 5 other ejectable; −1 in an alias made from a full path alone |
| +$2E | 4 | parent directory ID | The folder holding the target; 1 for a volume alias; −1 from a full path alone |
| +$32 | 64 | target name | Str63 |
| +$72 | 4 | target number | A file's file number (catalog ID) or a folder's directory ID; 2 for a volume; −1 from a full path alone |
| +$76 | 4 | target creation date | |
| +$7A | 4 | file type | 0 for a folder |
| +$7E | 4 | creator | 0 for a folder |
| +$82 | 2 | levels from | Folders from the alias up to the folder shared with the target; −1 when no relative path is recorded |
| +$84 | 2 | levels to | Folders from that folder down to the target; −1 likewise |
| +$86 | 4 | volume attributes | Bit 0 mount information recorded, 1 ejectable, 2 auxiliary remote information, 3 auxiliary folder IDs, 4 AFP media |
| +$8A | 2 | volume file-system ID | 0 for the File Manager's volumes; $4A48 an audio CD |
| +$8C | 10 | reserved | Zero |

### 1.2 Tagged data

From +$96, items of `tag` (2 bytes, signed), `length` (2 bytes) and `length` bytes of data, padded to an even length;
the list ends with tag −1 and length 0.

| Tag | Holds |
| --- | --- |
| 0 | The parent folder's name (the characters, no length byte) |
| 1 | The directory IDs of the folders from the parent up, 4 bytes each, up to and not including the root; absent when the parent is the root |
| 2 | The full path, "Volume:Folder:…:Name" |
| 3, 4, 5 | AppleShare zone, server and user names |
| 6 | Driver name |
| 7 | Auxiliary remote information (46 bytes: a Str27 volume name, a date, an auxiliary parent and number, an auxiliary root, a volume type) |
| 8 | Auxiliary real directory IDs |
| 9 | Volume mount information (an AppleShare volume's `'afpm'` record) |

Mac OS 9.0 writes no Unicode or POSIX tags.

### 1.3 Network volumes

Tag 9's mount information for an AppleShare volume is an `AFPVolMountInfo` [Doc: Inside Macintosh: Files,
AFPVolMountInfo]:

| Offset | Size | Field | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | `length` | The record's length |
| +2 | 4 | `media` | `'afpm'` |
| +6 | 2 | `flags` | |
| +8 | 1 | `nbpInterval` | NBP retry interval |
| +9 | 1 | `nbpCount` | NBP retry count |
| +10 | 2 | `uamType` | The user authentication method |
| +12 | 2 | `zoneNameOffset` | The zone's name, a Pascal string at this offset from the record's start |
| +14 | 2 | `serverNameOffset` | The server's name |
| +16 | 2 | `volNameOffset` | The volume's name |
| +18 | 2 | `userNameOffset` | The user's name |
| +20 | 2 | `userPasswordOffset` | The user's password (not read) |
| +22 | 2 | `volPasswordOffset` | The volume's password (not read) |
| +24 | | `AFPData` | The strings |

An alias is on a network volume when its volume type is 1, its volume attributes have bit 4 (AFP media) set, or it holds
`'afpm'` mount information; without the mount information the zone, server and user come from tags 3, 4 and 5.

A complete alias has the fixed part and tags 0, 1 and 2 (with 6–9 for remote volumes); NewAliasMinimal's has the fixed
part alone with both levels −1; NewAliasMinimalFromFullPath's has tag 2 alone, with −1 in the volume type, parent and
number [Fitted].

## 2. Reading

1. Read the fixed part (150 bytes). A version other than 2 is not read.
2. Read tagged items from +$96 until tag −1. An item that runs past the data ends the list.

### 2.1 Resolving

The Finder opens an alias file through FollowFinderAlias, with the alias's own folder as the starting point and
`'alis'` 0, and writes the record back when the Alias Manager says it changed [Code: Finder]. FollowFinderAlias is
MatchAlias with the rules `kARMSearchRelFirst` and `kARMSearch` (and mounting volumes), asking for one match; a match
that is the alias file itself is "not found" [Fitted]. ResolveAliasFile follows aliases of aliases, at most ten.

MatchAlias's order [Fitted]:

1. With `kARMSearchRelFirst`, the relative path: levels from and to, from the starting folder.
2. The volume: by name and creation date (and type); else another mounted volume with that date and name, then the date
   alone, then the name alone.
3. The fast search on that volume: by the target number; by the parent directory ID and name (when the item found has
   another number, also by number); by the full path (tag 2), else by walking tag 1's directory IDs.
4. With `kARMSearchMore`, a catalog search (CatSearch).
5. The relative path, when it was not first.
6. With `kARMMultVols`, other volumes.

On a volume whose creation date differs from the alias's, a candidate matches when [Fitted]: a file has the same number,
or the same creation date, type and creator; a folder the same creation date; and the kind (file or folder) is the
alias's. The first match wins; none is `fnfErr` (−43), which the Finder reports as "The original item could not be
found" (and −35 as the disk not found) [Code: Finder].

The resolution rules are `kARMMountVol` 1, `kARMNoUI` 2, `kARMMultVols` 8, `kARMSearch` $100, `kARMSearchMore` $200,
`kARMSearchRelFirst` $400 [Doc].

### 2.2 The path Get Info shows

Get Info's "Original:" reads `'alis'` 0 ("n/a" when there is none) and does not resolve it; it joins with ": " (colon
and space) the AppleShare zone, server and volume (each when not empty), then the folder names from the highest down,
then the target's name; a disk alias leaves out the trailing name [Code: Finder GetAliasInfo]. The folder names are cut
from the full path (tag 2), as many as tag 1 has IDs [Fitted]; so an alias without them shows "Volume: name", and one
that no longer resolves still shows where it pointed:

```
Mac OS 9: System Folder: Control Panels
```

## 3. Writing

None.

## 4. Variants

- Version 2 is the only one Mac OS 9.0 writes and the only one ClassicMac reads; later systems' tags and versions are not
  covered (§8).

## 5. ClassicMac

- `MacPathTree.AliasesTo` lists the alias files on an entry's volume whose original, as §2 resolves it, is that entry or
  inside it, leaving out aliases inside it themselves: what a deletion would leave without an original. The CLI's `rm`
  (and the shell's and MCP server's) records each as a warning on its change ([cli.md §3.2](../../cli.md#32-commands)).
  Aliases to an alias that is deleted are listed; aliases further down such a chain are not [ClassicMac].
- `AliasRecord.Read` reads §1; `TargetPath` is §2.2's path without the AppleShare names; `IsVolume` is a folder alias
  whose parent is 1. A record shorter than the fixed part, or of another version, is `InvalidDataException`; tagged data
  that ends early is kept as far as it goes. [ClassicMac]
- `AliasResolver.ReadAlias` reads an alias file's `'alis'` 0, else its first `'alis'`. `AliasResolver.Resolve` follows §2.1
  steps 2 and 3 on the volumes it is given: the volume order, then by number, by parent and name, by full path; the
  date test on a volume found by name alone; never the alias itself. The relative path, the walk of tag 1's IDs, the
  catalog search and mounting are not done. `AliasResolver.Follow` follows aliases of aliases, at most ten. A volume is
  an `AliasVolume`: an HFS or HFS Plus volume's files and folders (with their catalog IDs, `MacFile.CatalogId`,
  `MacFolder.CatalogId`) and creation date. [ClassicMac]
- An original not found is told apart as `AliasState`: `Missing` when a volume of the alias's (by name and date, date or
  name) is open but the original is not on it; `Network` when the alias's volume is a network volume (§1.3,
  `AliasRecord.Network`: zone, server, volume and user) and not open; `VolumeNotOpen` otherwise.
  `AliasRecord.VolumeKindName` names the volume type ("800K floppy disk", "network volume"…), and
  `AliasResolution.Explanation` says it in a sentence ("The original is on the 800K floppy disk “Bag of Holding”, which
  is not open."). The Finder only says "original could not be found" (§2.1). [ClassicMac]
- `MacPathTree.ResolveAlias`, `TargetOf` and `FollowAlias` resolve on the volume holding the alias file; the CLI's `stat`
  shows the recorded path and whether it resolves, and `ls`, `cat` and `get --follow` follow aliases (docs/cli.md §2).
  The app resolves on the alias's volume and the other open inputs, and shows the recorded path, the original and how
  it was found; an original not found is said apart by state in the header ("original missing", "on another disk", "on a
  network volume"), the preview's card (with the explanation), Details (State, Disk kind or Server, Zone, User) and the
  tree, where the row is dimmed and its badge carries a mark (a cross, a disk, a globe). [ClassicMac]
- `finder.alias` writes one `.json`, version 1, recording its text encoding: `userType`, `size`, `version`, `kind` (`file`,
  `folder` or the number), `targetPath` (§2.2), `volume` (`name`, `created`, `signature`, `volumeType`, `attributes`,
  `fileSystemId`), `parentId`, `name`, `targetId`, `created`, `type`, `creator`, `levelsFrom`, `levelsTo`, and `extras`
  (`tag`, `name` when known, then `text` for tags 0 and 2–6, `ids` for tag 1, `data` as hex for others). Dates are local
  times as stored, codes of zero are null. A record it cannot read is exported raw. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `alias.short` | Warning | The tagged data has no end tag, or an item runs past the data | Keeps the items read whole | Not traced |
| `alias.size` | Info | `aliasSize` differs from the resource's length | Reads the whole resource | Ignores data past `aliasSize` |

## 7. Verification

- `tests/ClassicMac.Files.Tests/AliasTests.cs`: §1 on records made by `AliasBuilder` (every field, the tags, short,
  unknown-version and unterminated records), §2.2's path, catalog IDs from `HfsReader`, and §2.1 on `HfsBuilder`
  volumes: by number when the target moved, by parent and name, by full path, the volume order (a renamed volume by
  date, another volume by name), the date test, folder and volume aliases, aliases of aliases, an alias pointing at
  itself.
- `tests/ClassicMac.Resources.Decoders.Tests/AliasDecoderTests.cs` and the golden `Golden/alis-0.json`: the JSON and the
  diagnostics.
- `tests/ClassicMac.Cli.Tests/PathCommandTests.cs` and `tests/ClassicMac.App.Tests/AliasViewTests.cs` on
  `AliasFixtures`: an alias whose original is in place, one whose original moved (found by its file ID), one whose
  original is gone, a folder alias, an alias of an alias, one on an 800K floppy disk that is not open, and one on an
  AppleShare volume with `'afpm'` mount information (§1.3).
- The 36 alias files on a Mac OS 9.0 startup disk, read locally (not kept): every record reads whole with §1; the 19
  whose originals are on the disk resolve by number, and the 17 pointing at the emulator host's shared volume or at
  another volume do not.

## 8. Not covered

- The relative path, the walk of tag 1's IDs, the catalog search and mounting volumes (§2.1 steps 1, 3's last part, 4–6).
- AppleShare names in §2.2's path (they are shown apart, §5).
- Writing or updating alias records.
- Later systems' records and tags (§4).

## 9. References

1. Apple, *Inside Macintosh: Files* (1992), Alias Manager: `AliasRecord`'s public fields, the resolution rules,
   ResolveAlias, MatchAlias, FollowFinderAlias, ResolveAliasFile.
2. Apple, *Inside Macintosh: Files*, File Manager: catalog file and directory IDs.
