# MacLHA 2.24 archives

Four archives from [lhasa](https://github.com/fragglet/lhasa)'s test suite (`test/archives/maclha_224/`, ISC licence;
see `THIRD-PARTY-NOTICES.md`), made by MacLHA 2.24. Their payload is the GPL-2 licence text (verbatim copies are
allowed) or a TeachText `hello world` document.

| File | Header level | Method | Content |
| --- | --- | --- | --- |
| `l0_lh5.lzh` | 0 (no OS byte) | `-lh5-` | `gpl-2` as a MacBinary file |
| `l1_nm_lh5.lzh` | 1 | `-lh5-` | `gpl-2`, made with "non-Mac": the plain file |
| `l1_subdir.lzh` | 1 | `-lh0-` | `subdir/subdir2/hello.txt` as a MacBinary file (`TEXT`/`ttxt`) |
| `l2_full_subdir.lzh` | 2 | `-lh0-` | the same with the "full" path, starting with the volume name `Untitled` |

lhasa's expected listings are in its `test/output/maclha_224/`.
