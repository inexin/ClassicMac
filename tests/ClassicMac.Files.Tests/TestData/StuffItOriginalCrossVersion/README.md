# Original StuffIt Deluxe cross-version archives

These four archives are from Stephan Sokolow's [StuffIt Test Files repository](https://github.com/ssokolow/stuffit-test-files),
which releases the test corpus under CC0 (see `LICENSE.cc0`). The archives were created with licensed copies of StuffIt
Deluxe 6.5 and 7.0 for Macintosh, with Mac OS 9 and Mac OS X 10.1.3 archive variants. The source repository documents its
clean-install procedure and the origin of the minimal files inside the archives.

The feature test compares all six extracted members and their data forks against the corpus originals. It also checks
both forks of `testfile.PICT` and the resource fork of `Test Image` against the matching outputs already recorded in
`TestData/StuffItLegacy45`. It exercises both the classic HFS and Mac OS X archive variants.

Three more archives from the same corpus check member layouts (`StuffItOriginalLayoutTests`):
`testfile.stuffit7_dlx.mac9.rreceipt.sit` (StuffIt Deluxe 7.0 with a return receipt prepended as the first member),
`testfile.stuffit7.win.sit` (StuffIt 7.0 for Windows: version-3 members in a `sources` folder; its `testfile.txt`
ends in LF) and `testfile.stuffit45_dlx.mac9.password.sit` (StuffIt Deluxe 4.5 with a password: every entry
encrypted).
