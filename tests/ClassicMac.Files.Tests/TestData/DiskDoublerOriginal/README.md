# DiskDoubler Pro 4.1.1 AD1, AD2, and DD3 fixtures

`DiskDoublerPro411Ad1TestFile.dd`, `DiskDoublerPro411Ad2TestFile.dd`, and `DiskDoublerPro411Dd3TestFile.dd` are the
individually compressed `testfile.PICT` files from the CC0
[DiskDoubler Test Files corpus](https://github.com/ssokolow/diskdoubler-test-files). They were produced by DiskDoubler Pro
4.1.1 using AD1, AD2, and DD3 compression respectively. `ExpectedDataFork.pict` and `ExpectedResourceFork.bin` are the
uncompressed data and resource forks from the corpus's source PICT and AppleDouble files. `LICENSE.cc0` is the upstream
license.

`DiskDoublerPro411Dda2Dd3Archive.dd` is the original Pro 4.1.1 archive at
`build/sources.ddpro411.dd3.dd` in the same corpus. The test extracts its `testfile.PICT` entry and compares its data
and resource forks with `ExpectedDataFork.pict` and `ExpectedResourceFork.bin`. Other records with entry type `0x1000` are
skipped with a diagnostic because they do not use the standard DDA2 file-header layout implemented by this reader.
