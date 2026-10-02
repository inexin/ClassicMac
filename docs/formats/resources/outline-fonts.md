# Outline fonts (sfnt)

An `'sfnt'` resource is a TrueType font file's bytes [Doc]: an offset table (`u32` version, `u16` number of tables,
search fields), a table directory of 16-byte entries (tag, checksum, offset, length), then the tables. Mac OS 9 picks
the scaler by the first four bytes, $00010000 counting as `'true'`; its only scaler takes `'true'` and Apple's own
`'mor0'`, `$A5kbd` and `$A5lst` fonts (the System file's .Keyboard and .Last Resort), a `bhed` table in place of
`head`, and bitmap-only fonts; PostScript `'typ1'` fonts need ATM [Code]. A family refers
to it with an association of size 0. ClassicMac reads the directory and the `name` table's family (ID 1), subfamily
(ID 2) and full (ID 4) names, from the Macintosh Roman records first, else Unicode [ClassicMac].
