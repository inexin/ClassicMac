# Compact Pro 1.52 fixtures

Made with Compact Pro 1.52 (unregistered) on Mac OS 9.0 (SheepShaver), 2026-10-02, from the synthetic,
redistributable file set of `../StuffIt151` (its source forks are `../StuffIt151/Forks`) plus `Noise.bin`
(`Forks/Noise.bin.data`, 120000 bytes of LCG noise, type `BINA`/`????`). Finder info is in Basilisk II's `.finf/`
(32 bytes each).

| File | Data | Type/creator | How |
| --- | --- | --- | --- |
| `cp152` | 2305 | `PACT`/`CPCT` | Archive > Add… > Add All (Big.txt, Empty, Folder:Inner, ReadMe), File > Save As |
| `cp152.sea` | 2305 | `APPL`/`EXTR` | the same, Save As with Self-Extracting checked |
| `cpnoise.#1`…`#3` | 40960, 40960, 38167 | `PACT`/`CPCT` | Misc > Segment… (custom size 40 K) of an archive of `Noise.bin` alone |

- `cp152.sea`'s data fork is `cp152` with another id in header bytes 2–3. Its resource fork (13057 bytes) is Compact
  Pro's extractor and is not committed; tests stand a dummy fork in for it.
- Each segment starts with `01`, its number, the set's id (`$4287`) and a directory offset: 0, except in the last
  segment, where it counts from that segment's start (38105). Segment 1 plus segments 2 and 3 after their 8-byte
  headers is the unsegmented archive (120071 bytes, not committed) from offset 8; the directory is unchanged, so
  `Noise.bin`'s data fork (offset 8, 120001 bytes, RLE) runs across all three segments.
- `cp152` uses LZH + RLE (`Big.txt`'s data fork, `ReadMe`'s resource fork) and RLE alone (the other forks).
