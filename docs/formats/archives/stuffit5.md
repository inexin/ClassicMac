# StuffIt 5 (initial subset)

StuffIt 5 is recognized by the eight-byte `StuffIt ` signature and version byte 5 at offset 82. The 100-byte archive
header stores its total length at 84, the first root member's absolute offset at 88, and the root member count at 92.
The offset at 94 is the same until StuffIt Deluxe 7.0 prepends a member (its return receipt,
`StuffItReturnReceipt.txt`): then 88 points to the receipt, whose next link is the old first member, and 94 still to
the old first member, so 94 would lose the receipt **[Verified]** (Deluxe 7.0, `testfile.stuffit7_dlx.mac9.rreceipt.sit`).
An archive member begins with `A5 A5 A5 A5`; the `u16` at +6 gives its header length. The member header has flags at +9,
Mac creation and modification seconds at +10 and +14, the next sibling offset at +22, the name byte count at +30, and
the header CRC at +32. These field positions and encodings are **[Fitted]** by comparison with
[Deark's StuffIt reader](https://github.com/jsummers/deark/blob/master/modules/stuffit.c).

The member flag `$40` denotes a folder and `$20` denotes an encrypted member **[Fitted]**. Folder headers store the
first child offset at +34 and child count at +46. A file header stores logical and compressed data-fork lengths at
+34/+38, data-fork CRC at +42, and compression method at +46. The UTF-8 name follows the fixed fields (and any
password bytes). File-specific Finder information starts after the variable header: resource-fork presence is flag bit
0 of its `flags2`; type, creator and Finder flags follow. That block is 36 bytes in a member whose version byte (+4)
is 1, as every Mac StuffIt writes (Deluxe 6.5 and 7.0, DropStuff 7.0.3), and 32 bytes in the version-3 members of
StuffIt 7.0 for Windows **[Verified]**; a Windows member's type and creator fields hold Windows data (`$00000020`). When present, resource-fork lengths, CRC and method precede
resource bytes, which precede data-fork bytes. These member details are **[Fitted]** against Deark's independent
parser and the hand-built vectors in `StuffItFeatureTests`; they have not yet been checked against a corpus created by
the original StuffIt application.

ClassicMac currently extracts methods 0 (stored), 1 (RLE90), 2 (Compress/LZW), 3 (Huffman), 5 (LZAH), 6 (fixed Huffman + PackBits), 8 (MW),
13 (LZ + Huffman), 14 (Installer), and 15 (Arsenic) for either fork. RLE90 emits ordinary bytes as
literals; `$90 00` emits a literal `$90`; `$90 n` for nonzero `n` repeats the previously decoded byte until the run has
`n` copies **[Fitted]**. Truncated runs, runs without a prior byte, output-length mismatches and extents outside the
archive are rejected. Per-fork CRC mismatches are reported as errors while retaining the decoded file. Encrypted
entries and unsupported methods are reported and omitted; no password is requested. Files and folders are returned
through the normal `ContainerUnwrapper` pipeline, preserving UTF-8 paths, Finder type/creator/flags, dates and both
forks. Original v5 archives made with StuffIt Deluxe 6.5 and 7.0 for Macintosh verify the member listing and exact
data/resource fork bytes across the Mac OS 9 and Mac OS X archive variants; the specific compression method of each
member is not part of that acceptance test.

Folders are **[Verified]** against DropStuff 7.0.3 (StuffIt Standard 7.0.3) archives of a synthetic file set
(`TestData/StuffIt703`; Better Compression writes methods 15 and 0, Faster Compression 13 and 0). A folder header is
followed by the same 36-byte Finder block as a file. Right after it StuffIt writes the folder's end marker: a
48-byte folder header with no name, first child `$FFFFFFFF` and no Finder block; the folder's first member follows
the marker, and the folder's last member's next link points back to the marker. The reader walks each list by its
declared count, so it never reaches the marker, and it bounds a member's forks by its next link only when that link
points forward. StuffIt X (`.sitx`, signature `StuffIt!`) is not this format and is not recognised (on request
only).
