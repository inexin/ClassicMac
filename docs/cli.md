# The command line

`classicmac` reads, lists, converts and extracts classic Mac OS files (`classicmac --help` lists the commands). This
document describes the file commands that work on *Mac paths* (planned in [PLAN.md](PLAN.md), "File commands, shell
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
object (§2.6). The limit options of every command (`--max-resource-size`, `--max-nesting-depth`,
`--max-expanded-bytes`, `--strict`, `-q`) apply.

| Exit code | When |
| --- | --- |
| 0 | Done (warnings printed) |
| 1 | Part of the input could not be read (or warnings with `--strict`) |
| 2 | The command line is wrong, or the entry has nothing for the command (`cat` of a folder) |
| 4 | Reading or writing a host file failed (`get` onto a file that exists, without `--overwrite`) |
| 5 | The path names nothing, or starts with no host file |

### 2.1 ls

`classicmac ls <path> [--json]` lists what the path holds: a container's or folder's files and folders, a fork's types,
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
through from the host file.

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

`classicmac cat <path> [--hex] [--raw] [--fork data|rsrc] [--max-bytes <size>] [--json]`:

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

`classicmac get <path> [-o <dir>] [--as appledouble|basilisk|macbinary|raw] [--enter] [--overwrite] [--json]` copies
to a host folder (default: the current one) and lists the files written:

- a file or container: as an AppleDouble pair (default), Basilisk II folders, a MacBinary III `.bin`, or its forks raw
  (the data fork, and the resource fork as `.rsrc`); `--enter` writes a container's contents as a folder instead;
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
`DataFork`…) and `resourceAttributes` (a resource).

**cat**: `{ "input", "path", "encoding", "truncated", … }` with `encoding` `text` and `text` (a string), `hex` and `hex` (the
bytes as hex digits) with `size` (the whole size), or `json` and `json` (the decoder's JSON, as it is).

**ls**: `{ "input", "path", "entries": [entry…] }`.

**find**: `{ "input", "path", "matches": [entry…], "truncated" }` (`truncated`: more matched than `--limit`).

**get**: `{ "input", "path", "written": [host path…] }` (`written` as in §3.3).

The library behind them is `MacCommands` (`ClassicMac.Files.Commands`): `List`, `Stat`, `Info`, `Chain`, `ReadBytes`,
`Text`, `Hex`, `Find` (with `MacFindQuery`), `Matches` and `Get` (with `MacGetFormat`), returning `MacEntryInfo`
records; the MCP server's read tools use the same.

## 3. Write commands

Each write command changes one thing on a Mac path, through the library's `InputEditSession`
(`ClassicMac.Files.Editing`), and never changes its input unless asked.

### 3.1 What can be written

- **A plain HFS volume image** (the host file is the volume, with no partition map or disk image wrapper): files and
  folders added, deleted (a folder only with `--recursive` when it holds anything), renamed in their folder, a file's
  type, creator and Finder flags and a folder's Finder flags set, and a file's resources added, replaced and deleted.
  The rules are [hfs.md §3](formats/file-systems/hfs.md#3-writing).
- **One Mac file** (a resource fork file, MacBinary, BinHex, AppleSingle, AppleDouble or Basilisk II file): its
  resources, and its type, creator and flags (a resource fork file has none). It is written back in its own form.
- Nothing else: a path that goes into an archive, a disk image of another kind, or a container inside the volume is
  refused.

### 3.2 Commands

| Command | Arguments and options | Does |
| --- | --- | --- |
| `put` | `<host file> <Mac path>` `[--name N] [--type T] [--creator C]` | Adds a host file: into the folder the path names (keeping its name), or as the file the path names. The host file is read with its AppleDouble or Basilisk II companions, MacBinary and AppleSingle unwrapped, else its bytes are the data fork |
| `mkdir` | `<Mac path>` | Makes an empty folder |
| `rm` | `<Mac path> [--recursive/-r]` | Deletes a file, or a folder (with everything in it only with `-r`) |
| `rename` | `<Mac path> <new name>` | Renames a file or folder in its folder (names are at most 31 bytes, unique as HFS compares them) |
| `set` | `<Mac path> [--type T] [--creator C] [--flags F]` | Sets a file's type and creator; `--flags` replaces the Finder flags: a number (`0x4000`, `$4000`, `16384`) or flag names joined with commas (`Invisible,HasBundle`; `IsInvisible` too) |
| `res-add` | `<file>:#rsrc:<type>:<ID> <data file> [--name N] [--replace]` | Adds a resource with the data file's bytes; one that exists is replaced only with `--replace` |
| `res-rm` | `<file>:#rsrc:<type>:<ID>` | Deletes a resource |

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

`action` is `add`, `mkdir`, `delete`, `rename`, `set`, `res-set` or `res-delete`. An AppleDouble pair or a Basilisk II
entry writes more than one file.

### 3.4 Exit codes

0 success; 2 a usage error or a refused change (a path that names nothing, a name in use, a folder that is not empty,
an input ClassicMac does not write), with the reason on standard error; 4 the file could not be written.
