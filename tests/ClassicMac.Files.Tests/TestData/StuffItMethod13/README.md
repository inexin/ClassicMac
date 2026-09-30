These compressed forks were extracted from `testfile.stuffit45_dlx.mac9.sit` in
[ssokolow/stuffit-test-files](https://github.com/ssokolow/stuffit-test-files):

- `DataFork.bin`: `testfile.jpg` data fork, method 13, 220 output bytes, CRC-16 `05C2`.
- `Preset1ResourceFork.bin`: `Test Text` resource fork, method 13, 332 output bytes, CRC-16 `F0F8`.
- `DynamicResourceFork.bin`: `Test Image` resource fork, method 13, 9,134 output bytes, CRC-16 `B07B`.
- `ExpectedDataFork.jpg`: the corresponding 220-byte `testfile.jpg` from the corpus `sources` directory.

The archive was made by StuffIt Deluxe 4.5 for Macintosh. The corpus author released the test files and their
contents under CC0; see the upstream [license](https://github.com/ssokolow/stuffit-test-files/blob/master/LICENSE.cc0).
These two small files retain that license and are included only as a real-application interoperability test vector.

`DistinctDynamicTrees.bin` is a hand-built method-13 stream. Its control byte selects separately transmitted literal/
length trees, and the payload decodes to `AAAAB` using a length-three distance-one back-reference before a literal from
the second tree. It is a protocol feature vector, not an original-application sample.
