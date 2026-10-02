# DiskDoubler 3.7.7 and Pro 4.1.1 fixtures

`DiskDoublerPro411Ad1TestFile.dd`, `DiskDoublerPro411Ad2TestFile.dd`, and `DiskDoublerPro411Dd3TestFile.dd` are the
individually compressed `testfile.PICT` files from the CC0
[DiskDoubler Test Files corpus](https://github.com/ssokolow/diskdoubler-test-files). They were produced by DiskDoubler Pro
4.1.1 using AD1, AD2, and DD3 compression respectively. `ExpectedDataFork.pict` and `ExpectedResourceFork.bin` are the
uncompressed data and resource forks from the corpus's source PICT and AppleDouble files. `LICENSE.cc0` is the upstream
license.

`DiskDoublerPro411Dda2Dd3Archive.dd` is the original Pro 4.1.1 archive at
`build/sources.ddpro411.dd3.dd` in the same corpus. The test extracts its `testfile.PICT` entry and compares its data
and resource forks with `ExpectedDataFork.pict` and `ExpectedResourceFork.bin`. The same archive contains raw JPEG and
PNG records with entry type `0x1000`. Their 44-byte metadata blocks provide Finder information, timestamps, and fork
lengths before the uncompressed fork bytes; feature tests check those fields and both image signatures. This layout is
fitted to the two records in this archive, and their checksum fields are not yet verified.

`DiskDoublerPro411Dda2Dd1Archive.dd` and `DiskDoublerPro411Dda2Dd2Archive.dd` are the original Pro 4.1.1 archives at
`build/sources.ddpro411.dd1.dd` and `build/sources.ddpro411.dd2.dd` in the same corpus. Feature tests compare both
forks of each archive's `testfile.PICT` entry with the uncompressed source files. Those PICT records carry method ID 10
in their DDA2 entry headers; the archive filenames do not identify each record's method ID.

`DiskDoublerPro411Dd1TestFile.dd` and `DiskDoublerPro411Dd2TestFile.dd` are the original standalone files at
`build/sources.ddpro411.dd1/testfile.PICT` and `build/sources.ddpro411.dd2/testfile.PICT`. They are read directly and
their data and resource forks are compared with the uncompressed source files.

`DiskDoubler377DdaTestFile.dd` is the original standalone file at `build/sources.dd377.dda/testfile.PICT`; the fixture
has method 1 selected for both forks. Its extracted forks are compared with the same uncompressed source files.

`DiskDoubler377DdbTestFile.dd` is the original standalone file at `build/sources.dd377.ddb/testfile.PICT`; the fixture
has method 8 selected for both forks. Its extracted forks are compared with the same uncompressed source files.

`StuffIt45DiskDoubler377DdaFiles.sit` is the corpus's `build/sources.dd377.dda.sit`: StuffIt Deluxe 4.5's archive of
the `sources` folder after DiskDoubler 3.7.7 compressed each file with DiskDoubler A (method 1). It is a version-2
StuffIt archive with a folder, and its `Test Image`, `testfile.jpg` and `testfile.png` have an empty method-1 fork.
The test expands every file through both containers and compares the forks with the sources.

`StuffIt651DiskDoublerPro411Ad1Files.sit` and `StuffIt651DiskDoublerPro411Ad2Files.sit` are the corpus's
`build/sources.ddpro411.ad1.sit` and `build/sources.ddpro411.ad2.sit`: StuffIt 6.5.1 archives (method 15) of the
`sources` folder after DiskDoubler Pro 4.1.1 compressed each file with AD1 or AD2. Their Arsenic streams need a model's
last symbol to own the rest of the coder's range. The test expands every file through both containers and compares the
forks with the sources.
