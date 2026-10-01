# StuffIt Deluxe 4.5 legacy archive fixture

`StuffItDeluxe45.sit` is `build/testfile.stuffit45_dlx.mac9.sit` from the CC0
[StuffIt test file corpus](https://github.com/ssokolow/stuffit-test-files). The archive was created with StuffIt
Deluxe 4.5 for Mac OS 9 and contains legacy version-2 member records. The feature test verifies its six entries,
version-2 traversal, and method-13 extraction of both `testfile.PICT` forks and the `Test Image` resource fork.

The expected forks are from the same corpus's original source files and AppleDouble sidecars. `LICENSE.cc0` is the
upstream license. `StuffItDeluxe45WithComment.sit` and `StuffItDeluxe45CommentAppleDouble.bin` are the corpus's
`.comment.sit` archive and its AppleDouble companion; the latter carries the resource fork used to verify the archive
comment.
