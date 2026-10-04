# The command line

`classicmac` reads, lists, converts and extracts classic Mac OS files (`classicmac --help` lists the commands). This
document describes the file commands that work on *Mac paths* the MCP server that offers them (§4) and the shell (§5) (planned in [PLAN.md](PLAN.md), "File commands, shell
and MCP"); the library's `MacPathTree` (`ClassicMac.Files`) implements the paths.

## 1. Mac paths

A Mac path names something inside a host file: the host file's own path, then Mac names, each a folder, a file, or a
container entered as a folder.

```
Mac OS 9.hfv:System Folder:Finder
disk.img:Archive.sit:Folder:File
disk.img/Applications/SimpleText
Mac OS 9.hfv:System Folder:Finder:#rsrc:'vers':1
```

### 1.1 The host file

The host file is the shortest part of the path, ending just before a `:`, `/` or `\`, that is an existing file; the
rest is the Mac part. A path that is only a host file names the host file itself. So `C:\Disks\disk.img:System Folder`
and `/home/me/disk.img/System Folder` both start with their disk image; a folder on the host is never a host file.

### 1.2 Names

- In the Mac part, `:` and `/` both separate names; empty names (`::`, a leading or trailing separator) are skipped.
- A backslash before `:`, `/` or `\` makes that character part of the name: a file named `A/B` is `A\/B`. A backslash
  before any other character is itself.
- Names compare as an HFS catalog compares them: ignoring case, not diacritics (`finder` is `Finder`, `e` is not
  `é`), with an exact match preferred. Names that are not Mac OS Roman (from zip and HFS Plus) compare ignoring case.
- The path ClassicMac prints for an entry joins the escaped names with `:` after the host file's path.

### 1.3 Containers

A file whose data fork holds files (a disk image, a partition map, an archive, a MacBinary, BinHex or AppleSingle
file…) is a container, and its contents are its children, as the app's tree shows them: its files, grouped into folders
by their folder paths. A container is read only when a path goes into it, one level at a time. A container that holds
only one container (a MacBinary file of a disk image, a disk image's disk) shows that container's contents in its
place; the name of the container it holds may be given or left out (`disk.bin:File` and `disk.bin:Disk Image:File`).

### 1.4 Resources

After a file, `#rsrc` is its resource fork (from the resource fork, an AppleDouble or Basilisk II companion, or a plain
host file that is a resource fork). Below it, a type is `'TYPE'` in single quotes (inside them `:` and `/` are part of
the type), or bare as its four characters (`TEXT`); below the type, a resource is its ID
(`-16455`). A real file named `#rsrc` wins over the fork.

| Path ends with | Names |
| --- | --- |
| `…:Finder` | the file |
| `…:Finder:#rsrc` | its resource fork |
| `…:Finder:#rsrc:'snd '` | its `'snd '` resources |
| `…:Finder:#rsrc:'snd ':128` | `'snd '` 128 |

The library's `MacPathTree` reads the forks of the entries listed in a container; empty folders come from an HFS
volume's catalog (its folder records), so they list too.

## 2. Read commands

Each takes a Mac path (§1). Results go to standard output, diagnostics to standard error; `--json` writes one JSON
object (§2.6). Text redirected to a file or a pipe is UTF-8 without a byte order mark, whatever the console's
code page. The limit options of every command (`--max-resource-size`, `--max-nesting-depth`,
`--max-expanded-bytes`, `--strict`, `-q`) apply.

`ls`, `cat` and `get` take `--follow`: an alias file (the Finder's isAlias flag) stands for its original, resolved on
the volume holding it ([aliases.md §2](formats/resources/aliases.md#2-reading)), through aliases of aliases (at most
ten). An original that is not found is an error naming the path the alias recorded (exit 5).

| Exit code | When |
| --- | --- |
| 0 | Done (warnings printed) |
| 1 | Part of the input could not be read (or warnings with `--strict`) |
| 2 | The command line is wrong, or the entry has nothing for the command (`cat` of a folder) |
| 4 | Reading or writing a host file failed (`get` onto a file that exists, without `--overwrite`) |
| 5 | The path names nothing, or starts with no host file |

### 2.1 ls

`classicmac ls <path> [--follow] [--json]` lists what the path holds: a container's or folder's files and folders, a fork's types,
a type's resources; a file or resource lists itself. Text: one line each, kind, type and creator, data fork size (or a
count), resource fork size, modified, name.

```
$ classicmac ls "Mac OS 9.hfv:System Folder"
folder                         -          -  2001-02-28 10:04:12  Appearance
file          FNDR MACS     614400     501242  2000-11-03 09:00:00  Finder
container     rohd ddsk    1474560          0  1999-07-15 12:00:00  Disk Tools.img
```

### 2.2 stat

`classicmac stat <path> [--json]`: everything about the entry: its kind, type and creator, the Finder's kind with
where the name came from ([finder.md](formats/resources/finder.md)), the forks' sizes, dates, Finder flags, locked, how
many entries it holds, a resource's type, ID, name and attributes, a fork's source, and the chain of formats it was read
through from the host file. For a volume (`disk.img:`, or a disk image inside one), its format and name, size and
free space in bytes and allocation blocks, file and folder counts, its own dates, its blessed System Folder and whether it is locked
([hfs.md §5.2](formats/file-systems/hfs.md#52-what-comes-out)). For an alias file, where it points as the Finder's Get Info shows it ("Original:"), and
whether that resolves on the volume, how, and to which entry:

```
$ classicmac stat "Mac OS 9.hfv:Late Breaking News"
…
Original: Mac OS 9: System Folder: Help: Mac Help: ln: pgs: lnFmSet.htm
Resolves: yes, by its file ID: /data/Mac OS 9.hfv:System Folder:Help:Mac Help:ln:pgs:lnFmSet.htm
```

```
$ classicmac stat "disk.img:Inner.img:Deep:Note"
Path: /data/disk.img:Inner.img:Deep:Note
Kind: file
Type / creator: 'TEXT' / 'ttxt'
Finder kind: SimpleText text document (ClassicMac's list)
Data fork: 9 bytes
Resource fork: 0 bytes
Finder flags: $0100 (inited)
Read as: disk.img (host file) > disk.img (HFS volume) > Inner.img (HFS volume)
```

### 2.3 cat

`classicmac cat <path> [--hex] [--raw] [--fork data|rsrc] [--max-bytes <size>] [--follow] [--json]`:

- A file: its data fork as text, Mac OS Roman decoded to UTF-8 and CR made LF; `--fork rsrc` the resource fork (as a hex
  dump).
- A resource: decoded by the built-in decoders, as JSON (`'vers'`, `'STR#'`, `'MENU'`…) or text (`'STR '`, `'TEXT'`);
  anything else (a picture, a sound) as a hex dump (use `get` or `extract` for the decoded files).
- `--hex`: a hex dump (offset, 16 bytes, the bytes as Mac OS Roman). `--raw`: the bytes themselves to standard output (not
  with `--json` or `--hex`).
- `--max-bytes` (default 16 MiB): only the first bytes are shown, and standard error says so.
- A folder or a type has nothing to show (exit 2).

### 2.4 find

`classicmac find <path> [--name <pattern>] [--type T] [--creator C] [--kind folder|file|container] [--resource-type T]
[--contains <text>] [--contains-hex <hex>] [--max-depth <n>] [--limit <n>] [--json]` lists the folders, files and
containers below the path that match every criterion, depth first. `--name` is a pattern (`*` any characters, `?` one;
case ignored as HFS ignores it); `--contains` looks for Mac OS Roman text in either fork (forks up to 64 MiB);
`--max-depth` (default 8) is how many levels of containers are entered; `--limit` (default 1000) the most matches
listed. Text: one path per line.

### 2.5 get

`classicmac get <path> [-o <dir>] [--as appledouble|basilisk|macbinary|raw|binhex|text] [--enter] [--overwrite] [--follow] [--json]` copies
to a host folder (default: the current one) and lists the files written:

- a file or container: as an AppleDouble pair (default), Basilisk II folders, a MacBinary III `.bin`, its forks raw
  (the data fork, and the resource fork as `.rsrc`), a BinHex 4.0 `.hqx`, or `text`: the data fork alone, read as Mac
  OS Roman with CR line ends and written as UTF-8 with LF line ends and no byte order mark (`.txt` added to a name
  without an extension); `--enter` writes a container's contents as a folder instead;
- a folder: a host folder of its files and folders;
- a resource: its data (decompressed) as `TYPE_ID.bin`.

Names the host does not allow have those characters replaced by `_`.

### 2.6 JSON

Names are camelCase, as the write commands' (§3.3); a fact that does not apply is left out (never null). Every object
starts with `input` (the host file) and `path` (the entry's Mac path inside it, empty for the host file itself). Dates are the Mac's local time, as stored,
without a zone: `"1999-01-24T05:20:00"`. Sizes are bytes.

**An entry** (the elements of `ls` and `find`, and `stat`'s object):

| Field | Type | Notes |
| --- | --- | --- |
| `name` | string | |
| `path` | string | The Mac path inside the input, without the host file (`System Folder:Finder`), as §3.3 names paths |
| `kind` | string | `folder`, `file`, `container`, `resource-fork`, `resource-type` or `resource` |
| `type`, `creator` | string | Four characters (a resource: its type) |
| `dataSize`, `resourceSize` | number | A file's forks; a resource's data (decompressed); a fork's size |
| `created`, `modified` | string | |
| `flags` | number | The Finder flags word |
| `flagNames` | string[] | `onDesk`, `shared`, `noInits`, `inited`, `customIcon`, `stationery`, `nameLocked`, `hasBundle`, `invisible`, `alias` |
| `locked` | true | Only when locked |
| `format` | string | A container: what it holds (`HFS volume`, `MacBinary II`…) |
| `count` | number | A fork's types, a type's resources (`stat`: a folder's or container's entries) |
| `resourceType`, `resourceId`, `resourceName` | string, number, string | A resource; `resourceType` also for a type |

```json
$ classicmac ls "disk.img:System Folder" --json
{
  "input": "/data/disk.img",
  "path": "System Folder",
  "entries": [
    {
      "name": "Finder",
      "path": "System Folder:Finder",
      "kind": "file",
      "type": "FNDR",
      "creator": "MACS",
      "dataSize": 6,
      "resourceSize": 350,
      "flags": 24576,
      "flagNames": [ "hasBundle", "invisible" ]
    }
  ]
}
```

**stat** is an entry with `input`, and adds `kindName` and `kindSource` (a file's Finder kind, [finder.md](formats/resources/finder.md)), `chain`
(`[{ "name", "format" }]`, from the host file down), `resourceForkSource` (a fork: `ResourceFork`, `AppleDouble`,
`DataFork`…) and `resourceAttributes` (a resource). An alias file adds `alias`: `{ "storedPath", "found", "how",
"resolvedPath", "state", "explanation", "target" }`: the recorded path and the original's now (Get Info's "Vol: folder:
name"), how it was found (`by its file ID`, `by its folder ID`, `by name in its folder`, `by its path`, `not found`),
`state` (`found`, `missing` from its open volume, `volumeNotOpen`, or `network`, [aliases.md §5](formats/resources/aliases.md)),
`explanation` (the state in a sentence), and `target`, the original's path inside the input when found. A volume adds
`volume`: `{ "format", "name", "blockSize", "totalBlocks", "totalBytes", "freeBlocks", "freeBytes", "files",
"folders"?, "created"?, "modified"?, "backedUp"?, "utcAfterCreation", "blessedFolderId"?, "blessedFolder"?,
"softwareLocked", "hardwareLocked" }` (`name`
null for HFS Plus, `folders` absent for MFS).

**cat**: `{ "input", "path", "encoding", "truncated", … }` with `encoding` `text` and `text` (a string), `hex` and `hex` (the
bytes as hex digits) with `size` (the whole size), or `json` and `json` (the decoder's JSON, as it is).

**ls**: `{ "input", "path", "entries": [entry…] }`.

**find**: `{ "input", "path", "matches": [entry…], "truncated" }` (`truncated`: more matched than `--limit`).

**get**: `{ "input", "path", "written": [host path…] }` (`written` as in §3.3).

The library behind them is `MacCommands` (`ClassicMac.Files.Commands`): `List`, `Stat`, `Info`, `Chain`, `ReadBytes`,
`Text`, `Hex`, `Find` (with `MacFindQuery`), `Matches`, `Get` (with `MacGetFormat`) and `AliasOf` (`MacAliasInfo`),
returning `MacEntryInfo` records, and `MacPathTree.ResolveAlias`, `TargetOf` and `FollowAlias`; the MCP server's read
tools use the same.

### 2.7 check

`classicmac check <input> [--deep]` reads the input's own structures: its wrappers (MacBinary, AppleDouble, Disk Copy,
NDIF, a partition map), the volume or archive they hold, every file in it and each file's resource fork. Archives and
disk images stored in it are not opened; the last lines count them (`41 containers in it not opened (--deep checks
inside them)`; JSON `notOpened`). `--deep` reads through them too, as `info` and `list` do. Its diagnostics are the result, printed to standard output (`-q`: errors only), each
named by the containers down to its file (`disk.img > Images:Inner.img > Broken`). A plain
HFS volume image also gets the checks the writer makes before an edit (`HfsWriter.Check`,
[hfs.md §5.5](formats/file-systems/hfs.md#55-the-writer)), run even on a software-locked volume, and the first fault
found is shown as `volume: <fault>`, or `volume: passes the writer's checks`. Each HFS volume, and each HFS partition
of a partitioned disk, is also checked as Disk First Aid checks it ([hfs.md §5.6](formats/file-systems/hfs.md#56-first-aid)):
its problem lines and verdict come before the `volume:` line, as `first aid: Problem:  Invalid PEOF, 18, 2` and
`first aid: The volume “Macintosh HD” needs to be repaired.` (a partition's as `partition 3 "Macintosh HD" first aid:
…`). The last line counts the errors and warnings; a volume or partition the writer refuses, or that First Aid does
not find OK, counts as one error (in JSON's `errors` too).

Exit 0 when nothing is wrong (warnings allowed, unless `--strict`), 1 when there is an error or a volume fault, 4 when
the input cannot be read. `--json` writes `{ "input", "diagnostics": [{ "source", "severity", "code", "message",
"location"?, "offset"? }], "volume": { "passes", "fault" } or null, "firstAid": { "verdict", "summary", "problems":
[{ "number", "message", "arg2", "arg3", "stage", "repairable", "code" }] } or null, "partitions"?:
[{ "number", "name", "passes", "fault", "firstAid" }], "notOpened", "errors", "warnings" }`; `volume` and `firstAid`
are null for input that is not a plain HFS volume, and `partitions` lists a partitioned disk's HFS partitions (text:
`partition 3 "Macintosh HD": passes the writer's checks`). `verdict` is `appearsOk`, `needsRepair`, `cannotRepair`,
`notHfs` or `notChecked` (an HFS Plus volume). A problem with number 0 is MountCheck's or one of the checks Disk
First Aid lacks, printed `first aid: Problem:  <text>.`.

## 3. Write commands

Each write command changes one thing on a Mac path, through the library's `InputEditSession`
(`ClassicMac.Files.Editing`), and never changes its input unless asked.

### 3.1 What can be written

- **A plain HFS volume image** (the host file is the volume, with no disk image wrapper), or **a partitioned disk**
  whose map holds one Mac volume partition, a plain HFS one (edited in place within the partition,
  [partition-map.md §5](formats/file-systems/partition-map.md#5-classicmac)), or **a Disk Copy 4.2 image** of an HFS
  disk (edited in place, its checksum made again, [diskcopy42.md §3](formats/disk-images/diskcopy42.md#3-writing)), or
  **an NDIF (Disk Copy 6) image** of an HFS disk as an AppleDouble pair, Basilisk II entry, MacBinary, AppleSingle or
  BinHex file (made again around the changed disk and saved in the same layout,
  [ndif.md §3](formats/disk-images/ndif.md#3-writing)): files and
  folders added, deleted (a folder only with `--recursive` when it holds anything), renamed in their folder, moved to another folder, a file locked or unlocked, a System Folder blessed, a file's
  type, creator and Finder flags and a folder's Finder flags set, and a file's resources added, replaced and deleted.
  The rules are [hfs.md §3](formats/file-systems/hfs.md#3-writing).
- **One Mac file** (a resource fork file, MacBinary, BinHex, AppleSingle, AppleDouble or Basilisk II file): its
  resources, and its type, creator and flags (a resource fork file has none). It is written back in its own form.
- Nothing else: a path that goes into an archive, a disk image of another kind, or a container inside the volume is
  refused.

### 3.2 Commands

| Command | Arguments and options | Does |
| --- | --- | --- |
| `put` | `<host file> <Mac path>` `[--name N] [--type T] [--creator C] [--text]` | Adds a host file: into the folder the path names (keeping its name), or as the file the path names. The host file is read with its AppleDouble or Basilisk II companions, MacBinary, AppleSingle and BinHex unwrapped, else its bytes are the data fork. `--text`: a UTF-8 text file (a byte order mark dropped) made Mac OS Roman with CR line ends, type `TEXT`, creator `ttxt`; a character Mac OS Roman has not is refused, naming its line |
| `mkdir` | `<Mac path>` | Makes an empty folder |
| `rm` | `<Mac path> [--recursive/-r]` | Deletes a file, or a folder (with everything in it only with `-r`); each alias on the volume whose original it deletes is named in a warning (`  warning: <alias> will no longer find its original, <path>`; in JSON the change's `warnings`), also with `--dry-run` |
| `rename` | `<Mac path> <new name>` | Renames a file or folder in its folder (names are at most 31 bytes, unique as HFS compares them) |
| `lock`, `unlock` | `<Mac path>` | Locks or unlocks a file (an HFS folder has no lock); a locked file cannot be deleted |
| `bless` | `<Mac path>` | Makes a folder the volume's System Folder (`drFndrInfo[0]`); it must hold a System file (type `zsys`) |
| `mv` | `<Mac path> <folder>` | Moves a file or folder into another folder of its volume, keeping its name; the folder is a path inside the same input, or a Mac path starting with the input (`disk.img:` for the top level). A folder cannot move into itself, nor anything onto a name the folder holds |
| `set` | `<Mac path> [--type T] [--creator C] [--flags F]` | Sets a file's type and creator; `--flags` replaces the Finder flags: a number (`0x4000`, `$4000`, `16384`) or flag names joined with commas (`Invisible,HasBundle`; `IsInvisible` too) |
| `res-add` | `<file>:#rsrc:<type>:<ID> <data file> [--name N] [--replace]` | Adds a resource with the data file's bytes; one that exists is replaced only with `--replace` |
| `res-rm` | `<file>:#rsrc:<type>:<ID>` | Deletes a resource |
| `repair` | `<volume>` | Repairs an HFS volume as Disk First Aid would ([hfs.md §5.6](formats/file-systems/hfs.md#56-first-aid)), then verifies it again: one `repair` change per fix, then `first aid: ` and the problems left and the last line (`The volume “X” was repaired successfully.`, or the verify's verdict). A volume that appears to be OK, or that First Aid cannot repair, is not written (`Nothing to repair: nothing written.` / `Nothing written.`). Exit 0 when the volume ends OK, 1 when problems remain. JSON adds `firstAidBefore` and `firstAid` (§2.7's shape, `summary` the last line) |

Every command takes:

| Option | Meaning |
| --- | --- |
| `-o`, `--output <file>` | Write the input with the change to this new file (verified by reading it back). It cannot be the input |
| `--in-place` | Write over the input itself (verified first), keeping the original as `<input>.orig` the first time |
| `--dry-run` | Print the change and write nothing (no `-o` needed) |
| `--json` | Print the result as JSON (§3.3) |

One of `-o` and `--in-place` is needed unless `--dry-run` is given.

### 3.3 Output

The text output is one line per change, `<action> <path> (<detail>)`, then `Wrote <file>` for each file written, or
`Dry run: nothing written.`. Paths are inside the volume, without its name (`Docs:Letter`), and empty for a single-file
input. With `--json`:

```json
{
  "input": "/disks/disk.img",
  "dryRun": false,
  "written": [ "/disks/out.img" ],
  "changes": [ { "action": "mkdir", "path": "Docs:Old", "detail": "a new folder" } ]
}
```

`action` is `add`, `mkdir`, `delete`, `rename`, `move`, `lock`, `unlock`, `bless`, `set`, `res-set`, `res-delete` or
`repair`. An AppleDouble pair or a Basilisk II
entry writes more than one file.

### 3.4 format

`classicmac format <file> --size <n> [--name <name>] [--overwrite] [--json]` writes a new, empty HFS volume image:
`--size` in bytes or with K/KiB, M/MiB, G/GiB (400 KB to 2 TB, whole 512-byte blocks; only the volume's MDB, bitmap and B-trees are written, so a
large volume is as quick to make as a small one), `--name` 1 to 27
characters with no colon (default `Untitled`), laid out as [hfs.md §3.1](formats/file-systems/hfs.md#31-a-new-volume)
and checked before it is written. An existing file is replaced only with `--overwrite` (exit 4 otherwise); a size or
name HFS cannot hold is a usage error (exit 2). It prints `Wrote <file> (HFS "<name>", <size> bytes, <n>-byte blocks)`;
`--json`: `{ "written": [file], "name", "size", "blockSize" }`.

`classicmac resize <file> --size <n>` grows a plain HFS volume image, with the write options (`-o`, `--in-place`,
`--dry-run`, `--json`): the new space is free at the end ([hfs.md §3.2](formats/file-systems/hfs.md#32-growing-a-volume)).
Shrinking, a size needing more than 65,535 allocation blocks of the volume's size, and a partitioned disk's partition
are refused (exit 2). The MCP server's `resize` tool takes `session` and `size`.

### 3.5 Exit codes

0 success; 1 `repair` left problems (or could not repair the volume); 2 a usage error or a refused change (a name in use, a folder that is not empty, an input ClassicMac does not
write), with the reason on standard error; 4 the file could not be written; 5 a path that names nothing (no host file,
no such item, no folder for a new item, no file before `#rsrc`), as for the read commands (§2).

## 4. MCP server

`classicmac mcp` serves the file commands to an AI assistant as [Model Context Protocol](https://modelcontextprotocol.io)
tools, over standard input and output (the official C# SDK, `ModelContextProtocol.Core`). It runs until the client
closes its input. The limit options of every command (`--max-resource-size`, `--max-nesting-depth`,
`--max-expanded-bytes`) apply to everything it reads.

### 4.1 Setup

Claude Code, for the current project (or with `--scope user` for every project):

```sh
claude mcp add classicmac -- classicmac mcp
```

Claude Desktop, in `claude_desktop_config.json` (Settings > Developer > Edit Config):

```json
{
  "mcpServers": {
    "classicmac": { "command": "classicmac", "args": [ "mcp" ] }
  }
}
```

`classicmac` must be on the path (`dotnet tool install -g ClassicMac.Cli`); otherwise give its full path as
the command.

### 4.2 Sessions

`open` takes a host file and returns a session; every other tool takes that `session` and a `path` inside the input: the
Mac path after the host file (§1), names joined by `:` or `/`, `""` for the input itself. A session lasts until
`close`, which is refused while changes are unsaved unless `discard` is true.

The write tools change the session only. The changes are made once, on one edit session of the input kept for the
session's life, so `list`, `stat`, `read` and `search` see them: a volume's are read from memory (the volume as it stands,
put back in its partition or Disk Copy image), other inputs' from a working copy in the temporary folder. A dry run is
tried on the input as it stands. The input is not touched. `save_as` writes every change from that edit session: to `destination`, a new file
(never the input), or with `in_place: true` over the input, the original kept as `<input>.orig` the first time. The
result is read back to verify it, as the write commands' `-o` and `--in-place` do (§3). A write with `dry_run: true` checks the change and reports it without making
it. Inputs ClassicMac does not write (§3.1) can be read but not changed.

### 4.3 Tools

| Tool | Arguments | Result |
| --- | --- | --- |
| `open` | `path` (the host file) | `session`, `input`, `path` (`""`), `kind`, `format`, `editable` (`volume`, `file` or `no`) |
| `close` | `session`, `discard` | `session`, `input`, `closed` |
| `list` | `session`, `path`, `limit` (200, at most 1000), `cursor` | ls's object (§2.6) with `count`, `truncated`, `more` |
| `stat` | `session`, `path` | stat's object (§2.6) |
| `read` | `session`, `path`, `fork` (`data` or `rsrc`), `hex`, `max_bytes` (65536, at most 1 MiB), `cursor` | cat's object (§2.6) with `offset`, `size`, `truncated`, `more`; text also `length` |
| `search` | `session`, `path`, `name`, `type`, `creator`, `kind` (`folder`, `file`, `container`), `resource_type`, `contains`, `contains_hex`, `max_depth`, `limit` (100, at most 1000), `cursor` | find's object (§2.6) with `more` |
| `extract` | `session`, `path`, `directory`, `format` (`appledouble`, `basilisk`, `macbinary`, `raw`, `binhex`, `text`), `overwrite`, `enter` | get's object (§2.6) |
| `put` | `session`, `source` (a host file), `path`, `name`, `type`, `creator`, `text`, `dry_run` | §3.3's object with `unsaved` |
| `mkdir` | `session`, `path`, `dry_run` | the same |
| `rm` | `session`, `path`, `recursive`, `dry_run` | the same |
| `rename` | `session`, `path`, `name`, `dry_run` | the same |
| `lock`, `unlock`, `bless` | `session`, `path`, `dry_run` | the same |
| `mv` | `session`, `path`, `to`, `dry_run` | the same (`to` a folder's path; empty for the top level) |
| `set` | `session`, `path`, `type`, `creator`, `flags`, `dry_run` | the same |
| `res_add` | `session`, `path` (`<file>:#rsrc:<type>:<ID>`), `data_file` or `data_hex`, `name`, `replace`, `dry_run` | the same |
| `res_rm` | `session`, `path`, `dry_run` | the same |
| `save_as` | `session`, `destination` or `in_place: true` | §3.3's object: `written`, every change saved, `unsaved` (0) |

The arguments mean what the commands' options mean (§2, §3). A write's result lists the changes it made (`written`
empty); `unsaved` counts the session's changes no save has written. The tools' annotations mark the read tools
read-only and `save_as` destructive.

### 4.4 Pages

Results are compact JSON, both as the tool's text and as its structured content. `list`, `search` and `read` give a
page at a time: a result that stops short has `truncated: true` and `more`, a cursor to pass back as `cursor` for the
next page. A read pages text by characters and hex by bytes (`max_bytes` sets either). A resource decoded as JSON comes
as the `json` object when it fits in one page, otherwise as its JSON text in pages (`encoding` stays `json`).

### 4.5 Errors

A call that fails is a tool error (`isError`) whose JSON is `{ "error": "<why>", "code": "<code>" }`:

| Code | When |
| --- | --- |
| `notFound` | The path names nothing, or the host file or a source file does not exist (exit 5 on the command line) |
| `badArguments` | An argument is missing or of the wrong kind, the session is not open, or the cursor is not one the server gave |
| `refused` | The change or read is refused: a name in use, a folder that is not empty, an input ClassicMac does not write, a folder read as a file, `save_as` onto the input (exit 2) |
| `ioError` | A host file could not be read or written (exit 4) |

## 5. Shell

`classicmac shell <input>` is a DOS-like shell on one input: a host file, or a Mac path inside it to start at
(`classicmac shell "Mac OS 9.hfv:System Folder"`). It reads the input as the read commands do (§2) and changes it as
the write commands do (§3), in a session like the MCP server's (§4.2): changes are made on working copies, so later
commands see them, and the input changes only with `save`.

### 5.1 Commands

| Command | Does |
| --- | --- |
| `cd [path]` | Goes into a folder, disk image, archive, file (its `#rsrc`), fork or type; alone, prints where the shell is |
| `dir`, `ls [path]` | What a path holds (§2.1) |
| `type`, `cat <path> [--hex] [--rsrc] [--max-bytes N]` | A file's text, a resource decoded, or hex (§2.3) |
| `info`, `stat <path>` | Everything known about an entry (§2.2) |
| `res [file]` | A file's resources: type, ID, size and name |
| `find [path] [--name P] [--type T] [--creator C] [--kind folder\|file\|container] [--resource-type T] [--contains TEXT] [--limit N]` | Folders and files below a path (§2.4) |
| `copy <path> <host folder> [--as appledouble\|basilisk\|macbinary\|raw\|binhex\|text] [--overwrite]` | Copies out (§2.5); `get` always copies out |
| `copy <host file> <path> [--name N] [--type T] [--creator C] [--text]` | Copies in (`put`, §3.2); `put` always copies in |
| `del`, `rm <path> [-r]` | Deletes a file, or a folder (with contents only with `-r`) |
| `md`, `mkdir <path>` | Makes a folder |
| `ren`, `rename <path> <name>` | Renames |
| `lock`, `unlock <path>`, `bless <folder>` | Locks, unlocks, blesses |
| `move`, `mv <path> <folder>` | Moves into a folder, named from where the shell is |
| `set <path> [--type T] [--creator C] [--flags F]` | Sets Finder info (§3.2) |
| `save` | Writes the changes over the input, verified, keeping `<input>.orig` the first time |
| `save as <file>` | Writes them to a new file, verified (never the input) |
| `exit`, `quit [--discard]` | Leaves |
| `help` | Lists the commands |

`copy` copies out when its first path names something in the input, otherwise in from the host. A line starting with
`#` is a comment. The prompt is the input's name and the path inside it: `Mac OS 9.hfv:System Folder>`.

### 5.2 Words and paths

Spaces separate words. Double quotes group a word with spaces and are taken off (`cd "System Folder"`); a backslash
before a space or a double quote makes it part of the word. A name that starts with a single quote runs to the next
one, quotes kept, as a resource type does in a Mac path (`type Finder:#rsrc:'STR ':128`); an apostrophe inside a name
(`Bob's`) is not a quote. Other backslashes stay, for the Mac path's escapes (§1.2) and host paths.

A path is a Mac path (§1) from where the shell is. `:` or `/` alone is the input itself, and a path that starts with
one, or with the input's name (`Mac OS 9.hfv:System Folder`), starts from the input. `..` goes up a level and `.`
stays. When a change takes away the folder the shell is in, it moves up to what is left.

### 5.3 Typing

At a terminal, a line is edited anywhere (Left, Right, Home, End, Backspace, Delete; Escape clears it). Up and Down
recall the lines typed before in the session. Tab completes the word before the cursor: a command name first, then
the names in the folder the word points into (ignoring case), quoted when they hold a space; a folder, container,
fork or type gets a `:` to go on, a file a space. When several names fit, Tab completes what they share, and when that
adds nothing it lists them. Ctrl+D (or Ctrl+Z) on an empty line ends the input, as `exit` does.

Leaving with unsaved changes asks: Save (in place), Save As (asks for the file), Discard or Cancel.

### 5.4 Scripts

With `--script <file>`, or when standard input is not a terminal, the shell runs the lines it is given, without
prompts or questions. It stops at the first command that fails, with that command's exit code. Leaving with unsaved
changes is refused (exit 2, nothing written) unless the script ends with `save`, `save as` or `exit --discard`.

With `--json`, each command prints its result as one JSON object on one line: the read commands' objects (§2.6), the
write commands' (§3.3) with `unsaved`, `cd`'s `input` and `path`, `res`'s `input`, `path` and `resources` (entries
as §2.6). A command that fails prints `{ "command": "<command>", "error": "<why>", "code": <exit code> }`.

### 5.5 Exit codes

As the commands' (§2, §3.5): 0 success, 2 a usage error or refused change, 4 a host file not read or written, 5 a path
that names nothing. A script exits with the failing command's code; leaving with changes not saved (a script, or
the input ending while the shell asks) exits 2; otherwise the shell exits 0.
