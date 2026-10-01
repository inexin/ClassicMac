# Original journaled HFS+ image

The test `HfsPlusOriginalImageTests.JournaledMacOsHfsPlusImageListsFilesWithThePublishedContents` accepts the
Digital Corpora `nps-2009-hfsjtest1/image.gen1.dmg` image through the environment variable
`CLASSICMAC_HFSPLUS_REFERENCE_IMAGE`. The 10 MiB image is not included in this repository. The test pins its SHA-256
and verifies `file1.txt` and `file2.txt` against the SHA-1 values in the corpus's DFXML sidecar.

Download URL: <http://digitalcorpora.s3.amazonaws.com/corpora/drives/nps-2009-hfsjtest1/image.gen1.dmg>

DFXML metadata: <http://digitalcorpora.s3.amazonaws.com/corpora/drives/nps-2009-hfsjtest1/image.gen1.dmg.xml>
