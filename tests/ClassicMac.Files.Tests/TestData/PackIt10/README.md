# PackIt 1.0 fixture

Made with PackIt 1.0 (from the PackIt 400K MFS floppy) on Mac OS 9.0 (SheepShaver), 2026-10-02: File > Pack files…,
appending `ReadMe`, `Big.txt` and `Empty` one at a time (PackIt has no folders), from the synthetic, redistributable
file set of `../StuffIt151` (the source forks are `../StuffIt151/Forks`). The archive is the data fork of a
`PIT `/`PIT ` file (Finder flags `$0100`) with an empty resource fork.

| Offset | Magic | Name | Type/creator | Data | Resource |
| --- | --- | --- | --- | --- | --- |
| 0 | `PMag` | `ReadMe` | `TEXT`/`ttxt` | 75 | 373 |
| 548 | `PMag` | `Big.txt` | `TEXT`/`ttxt` | 40800 | 0 |
| 41448 | `PMag` | `Empty` | `TEXT`/`ttxt` | 0 | 0 |
| 41548 | `PEnd` | | | | |

The bytes after each Pascal name in its 64-byte name field are not zeroed (whatever was in memory).
