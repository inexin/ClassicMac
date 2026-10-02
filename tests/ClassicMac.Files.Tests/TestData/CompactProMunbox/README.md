# Compact Pro sample from munbox

`testfile.compact_pro_152.cpt` and `md5sums.txt` are `test/testfiles/compact_pro_152.cpt/` from
[munbox](https://github.com/dafo123/munbox) (MIT; see `THIRD-PARTY-NOTICES.md`). munbox says the archive was made by
Compact Pro 1.52 (its 1.33 copy differs only in two header bytes); that is not confirmed here. The content is
synthetic, from munbox's `test/data/additional_test_generator.c`: nine test files at the root, `Folder1`
with the same nine and `Folder1:Folder2` with the same nine again. `md5sums.txt` lists the data forks' MD5s. The
forks lie before the directory, which ends the archive; LZH + RLE forks use the RLE half-escape chain
(`81 81 81`, `81 81 82`), RLE-only forks do not, and no fork has `81 82 01`.
