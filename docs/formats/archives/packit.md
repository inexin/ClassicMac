# PackIt (stored, Huffman, and encrypted entries)

PackIt is a flat stream of entries with no archive header. `PMag` starts an uncompressed entry, `PEnd` ends the archive,
and the 94-byte entry metadata follows the four-byte signature. It holds a 63-byte Pascal name field, Finder type,
creator and flags, data- and resource-fork lengths, and creation/modification dates. The data fork, resource fork and a
CRC-16/XMODEM of their concatenation follow; the metadata also has a CRC-16/XMODEM **[Fitted]** against
[psx-spx's PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/) and
[XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/).
ClassicMac extracts stored `PMag`, XOR-encrypted uncompressed `PMa1`, DES-encrypted uncompressed `PMa2`,
Huffman-compressed `PMa4`, XOR-encrypted Huffman `PMa5`, and DES-encrypted Huffman `PMa6` records. Passwords are
supplied through `ContainerReadOptions.ArchivePassword` as MacRoman. The XOR key used by `PMa1` and `PMa5` is
expanded from the first eight password bytes using PackIt's PC-1 selection table and cycles over seven key bytes.
`PMa2` and `PMa6` use the first eight password bytes, zero-padded, as a DES key; the payload stream is transformed in
ECB mode. Both encryption schemes pad ciphertext to an 8-byte boundary before the next entry. The stream transformations are
**[Fitted]** against [XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/)
and the published [PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/).
`PMa1` and `PMa2` behavior applies those encryption layers to otherwise uncompressed entries; this pairing is fitted
from the published signature table and hand-built vectors because XADMaster only implements the encrypted Huffman
variants and no original-app fixture is available.
Header and fork CRC mismatches are reported; a failed encrypted fork checksum rejects the file as a bad password or
damaged ciphertext. `PMa3` and `PMa7` are reserved and other markers are reported as unsupported, stopping parsing at
that record. Tests cover stored and Huffman entries with both forks, Finder metadata and dates, CRC diagnostics, correct
and wrong passwords for raw and Huffman XOR/DES entries, weak DES keys, encrypted stream alignment, unsupported methods,
truncated headers, and enforcing `MaxVolumeEntries` before decoding an over-limit record.

Stored `PMag` entries and `PEnd` are **[Verified against PackIt 1.0]** (`TestData/PackIt10`, made on Mac OS 9): the
94-byte header (a 64-byte Str63 name field, type, creator, Finder flags, a locked word, data and resource lengths,
creation and modification dates, then the CRC-16/XMODEM of the 92 bytes after the magic), the data fork, the resource
fork and the CRC-16/XMODEM of data then resource (0 for an empty file). PackIt 1.0 leaves whatever was in memory after
the name in its 64-byte field, so the reader uses only the Pascal length's bytes. `PMa4` and the encrypted records
stay **[Fitted]**: PackIt III 1.3, which writes them, does not run on Mac OS 9.
