# StuffIt 1.5.1 fixtures

Made with StuffIt 1.5.1 on Mac OS 9.0 (SheepShaver), 2026-10-02, from a synthetic, redistributable file set: `ReadMe`
(data and resource fork), `Empty`, `Folder:Inner` and `Big.txt` (40 800 bytes), all `TEXT`/`ttxt`, flags 0, dated
`$E6E4F395`. `Forks/` holds the source forks (`Folder__Inner` is `Folder:Inner`); `expected.json` their lengths and MD5s.

All archives are version 1 (`SIT!`, `rLau`, version byte 1) with folder start/end entries (methods 32/33), saved as
data forks of `SIT!`/`SIT!` files:

| File | StuffIt options | Methods |
| --- | --- | --- |
| `fx151_lzw_huff.sit` | Try LZW + Try Huffman (default) | 2 LZW, 3 Huffman |
| `fx151_lzw.sit` | Try LZW only | 2 |
| `fx151_huf.sit` | Try Huffman only | 3, 1 RLE90 |
| `fx151_non.sit` | Neither | 0 stored |

In all but `fx151_lzw_huff.sit` the folder-end entry's data lengths hold leftover values (32 and 256).

`fx151_non.seg1`–`seg5` (type `SegM`, creator `SIT!`) are `fx151_non.sit` cut by Other > Set Segment Size (10 K) and
Segment…: each is a 100-byte `$41A7` header plus the next part of the archive.
