# ClassicMac command line (`classicmac`)

The ClassicMac command-line tool: list, extract, convert and write classic Mac OS files, whatever they are wrapped
in. It also serves its file commands over MCP and as a DOS-like shell.

```
dotnet tool install --global ClassicMac.Cli      # once published
dotnet run --project src/ClassicMac.Cli -- --help # from the repository
```

`classicmac help <command>` shows a command's options and examples. The full reference, with every option, the JSON
output and the exit codes, is [docs/cli.md](https://github.com/inexin/ClassicMac/blob/main/docs/cli.md).

## Commands

**Whole inputs**

| Command | Does |
| --- | --- |
| `info <input>` | The container chain, Finder info, kinds and fork sizes |
| `list <input>` | The files and resources inside |
| `unpack <input> -o <folder>` | Every Mac file inside, with both forks and Finder info |
| `extract <input> -o <folder>` | Resources as modern files, with a manifest (`--image-format webp`, `--loadable-fonts`) |
| `convert <input> -o <folder>` | DOCMaker, SimpleText and Word documents to HTML, AIFF sounds to WAV, PDFs out as `.pdf` |
| `disasm <input> -o <folder>` | 68k and PowerPC code as listings, with `code.json` |
| `pack <folder> -o <file>` | A resource fork (or a container holding it) rebuilt from an export folder |
| `check <input>` | Damage in the input's own structures; First Aid on HFS and HFS Plus volumes |

**Mac paths** (`disk.img:Folder:File`, through any nesting of containers)

| Command | Does |
| --- | --- |
| `ls`, `stat`, `find` | List, describe and search |
| `cat`, `get` | Show a file, fork or resource, or copy it to the host |
| `derez`, `rez` | A resource fork as Rez source, and Rez source compiled back, as MPW does |

**Writing** (to `-o <file>`, `--in-place` or `--dry-run`; every write is verified before it is kept)

| Command | Does |
| --- | --- |
| `put`, `mkdir`, `rm`, `rename`, `mv` | Files and folders on HFS volume images |
| `lock`, `unlock`, `bless`, `set` | Locks, the System Folder, type, creator and Finder flags |
| `res-add`, `res-rm` | Add, replace or delete a resource |
| `format`, `resize`, `defrag`, `repair` | New HFS volumes, growing and shrinking, defragmenting, First Aid repairs |
| `ndif <disk> <image>` | A Disk Copy 6 (NDIF) image of a disk, segmented on request |

**Front ends**

| Command | Does |
| --- | --- |
| `mcp` | The file commands as MCP tools over standard input and output |
| `shell <input>` | A DOS-like shell: `cd` into disk images and archives, `dir`, `type`, `copy`, `del`, `md`, `save` |
| `help [<command>]` | The commands, or one command's help; every help ends with examples |

## Examples

```
classicmac ls "System 7.5.img:System Folder"
classicmac get "System 7.5.img:System Folder:Finder" -o .
classicmac extract "Game.sit" -o out --image-format webp
classicmac convert "ReadMe" -o readme
classicmac pack out -o "App.rsrc"
classicmac check "disk.img" --json
classicmac repair "disk.img" -o fixed.img
```

Global options set the text encoding of names and text (`--encoding japanese`), limits on decompression and nesting,
`--strict` and `--quiet`.
